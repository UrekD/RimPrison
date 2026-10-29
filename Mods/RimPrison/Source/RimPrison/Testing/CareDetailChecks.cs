using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The care reserve (<see cref="CareDetail"/>), in the events plan (<see cref="CheckCareDetail"/>) and the saveload plan (<see cref="SeedCareDetailForReload"/>, <see cref="CheckCareDetailAfterReload"/>). The proof is run on a small walled
    /// supermax unit (a hungry inmate shut in a cell, an injured one down in the corridor, two guards, and two inmates owing a restraint at the far end, more than <see cref="CareDetail.NearTroubleRadius"/> from everybody), with the pick limited
    /// to the check's own guards (<see cref="CareDetail.OnlyThese"/>: the real staff are not picked and not struck, a strike reaches the live prison) and real ticks. Each check says what would make it fail.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CareFail(string what) => problems0.Add("care detail: " + what);

        private static void CheckCareDetail(Map map)
        {
            CheckCareReserveOneKeepsCareGoing(map);
            CheckCareReserveZeroIsTheControl(map);
            CheckCareReserveZeroChangesNothing(map);
            CheckCareReserveHoldsFromFarTroubleOnly(map);
            CheckCareReserveFloor(map);
            CheckCareReserveNamedGuardsAreFiltered(map);
            CheckCareReserveUnavailable(map);
            CheckCareReservePickAndRelease(map);
            CheckCareFirstOutranksTheScan(map);
            CheckCareReservePatrolBreaksOff(map);
            CheckCareReserveTakesNoCount(map);
        }

        // ---- the fixture ----------------------------------------------------------------------------------------------------------------------------------------------

        private sealed class CareFixture
        {
            public SupermaxScene s;
            public Pawn reserve, other, hungry, injured, d1, d2;
            public CareDetail care;
        }

        /// <summary>
        /// The automatic pick (and the count of guards on duty) limited to these guards for the check, put back by the rollback: a real guard is never picked. (Not a strike of the real guards: it reached the live prison for the length of the check's ticks,
        /// its misconduct and escapes went unanswered and a running staff agreement counted the hours as uncovered.) The unit is walled, so no real guard, cook or doctor reaches it.
        /// </summary>
        private static void CareOnly(Rollback r, params Pawn[] guards)
        {
            r.Add(() => CareDetail.OnlyThese = null);
            CareDetail.OnlyThese = new HashSet<Pawn>(guards);
        }

        /// <summary>The guard suspended for a day for the check, put back by the rollback.</summary>
        private static void CareSuspend(Rollback r, Pawn p)
        {
            CompStaff sc = p.GetComp<CompStaff>();
            int was = sc.suspendedUntilTick;
            r.Add(() => sc.suspendedUntilTick = was);
            sc.suspendedUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
        }

        /// <summary>The pawn in a mental state (the one they are in first put by), or <paramref name="def"/> null for none; false when the state did not start (the check's premise).</summary>
        private static bool CareMentalState(Pawn p, MentalStateDef def, Pawn target = null)
        {
            p.MentalState?.RecoverFromState();
            if (def == null) return true;
            p.mindState.mentalStateHandler.TryStartMentalState(def, null, forced: true, otherPawn: target);
            return p.MentalStateDef == def;
        }

        /// <summary>A guard of the scene who can fight (the restraint work is not offered to one who cannot), with the warden's work on (the scene's guards have none of their own).</summary>
        private static Pawn CareGuard(SupermaxScene s, string name, IntVec3 at)
        {
            Pawn g = null;
            for (int i = 0; i < 8 && (g == null || g.WorkTagIsDisabled(WorkTags.Violent)); i++)
            {
                if (g != null)
                {
                    PrisonGame.Instance.home.Remove(g);
                    supermaxGuards.Remove(g);
                    g.Destroy(DestroyMode.Vanish);
                }
                g = s.Guard(name, at);
            }
            g.workSettings?.SetPriority(WorkTypeDefOf.Warden, 1);
            return g;
        }

        /// <summary>A throwaway guard on shift at every hour (the roster's balance writes a day guard's timetable with a night of sleep), put back by the rollback, with nothing on.</summary>
        private static void CareOnShift(Rollback r, Pawn p)
        {
            if (p.timetable != null)
            {
                List<TimeAssignmentDef> was = Enumerable.Range(0, 24).Select(h => p.timetable.GetAssignment(h)).ToList();
                r.Add(() => { for (int h = 0; h < 24; h++) p.timetable.SetAssignment(h, was[h]); });
                for (int h = 0; h < 24; h++) p.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
            }
            p.jobs?.StopAll();
        }

        /// <summary>The reserve's policy set for the check (put back by the rollback), the guards named in order, and the members worked out now.</summary>
        private static CareDetail CareSet(Map map, Rollback r, int size, params Pawn[] named)
        {
            CareDetail care = CareDetail.Of(map);
            int size0 = care.size;
            List<int> pins0 = care.pins.ToList();
            r.Add(() =>
            {
                care.pins.Clear();
                care.SetSize(size0);
                care.pins.AddRange(pins0);
                care.ForceRebuild();
            });
            care.pins.Clear();
            care.SetSize(size);
            foreach (Pawn p in named) care.TogglePin(p);
            care.ForceRebuild();
            care.Tick(map);
            return care;
        }

        /// <summary>
        /// The unit: <c>reserve</c> (the one the reserve is, or would be) and <c>other</c> stand at the west end, with a meal in the reserve's pockets (the cell's food is nowhere else, as in the other checks); <c>hungry</c> is shut in cell A
        /// at 18% (a supermax inmate eats in the cell), <c>injured</c> lies downed in the corridor (their bed is in cell B), and <c>d1</c> and <c>d2</c> owe a restraint, at the far end of the corridor: sixteen cells from the guards.
        /// The supermax regime is the lockup all day (or <paramref name="regime"/>).
        /// </summary>
        private static CareFixture BuildCareFixture(Map map, Rollback r, string check, RegimeActivityDef regime = null)
        {
            SupermaxScene s = SupermaxScene.Build(map, r, check);
            if (s == null) return null;
            var f = new CareFixture { s = s };
            // (The regime is one thing all day, the lockup unless the check says: the supermax level's own has a yard walk and a shower at some hours, which would take the shut-in inmate out of the cell at the hour the run happens to be at.)
            SupermaxRegimeAllDay(s.g, r, s.level, regime ?? RPDefOf.RP_Lockup);
            f.reserve = CareGuard(s, "Zcaregr", s.At(1, 1));
            f.other = CareGuard(s, "Zcaregs", s.At(2, 1));
            CareOnly(r, f.reserve, f.other);
            Thing meal = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MealSimple"));
            f.reserve.inventory.innerContainer.TryAdd(meal);
            r.Add(() => { if (!meal.Destroyed) meal.Destroy(); });
            f.hungry = s.Inmate("Zcareh", s.bedA, s.CellA);
            f.hungry.needs.food.CurLevel = 0.18f;
            f.injured = s.Inmate("Zcarei", s.bedB, s.At(3, 1), InmateUtility.DefaultSecurity);
            HealthUtility.DamageUntilDowned(f.injured, allowBleedingWounds: false);
            f.d1 = s.Inmate("Zcared1", null, s.At(17, 1), InmateUtility.DefaultSecurity);
            f.d2 = s.Inmate("Zcared2", null, s.At(16, 1), InmateUtility.DefaultSecurity);
            foreach (Pawn d in new[] { f.d1, f.d2 })
            {
                CompInmate dc = d.Inmate();
                r.Add(() => dc.pendingPunishmentHours = 0);
                dc.pendingPunishmentHours = 4;
            }
            s.pm.ForceDoorUpdate();
            map.reachability.ClearCache();
            InmateCandidates.Shared.Invalidate();
            WorkGiver_DeliverCellMeal.ForgetCanteenReach();
            f.care = CareDetail.Of(map);
            // (The premises: each of the three kinds of trouble and care is there, or the run proves nothing.)
            bool ok = GuardUtility.NeedsSubduing(f.d1) && GuardUtility.NeedsSubduing(f.d2) && WorkGiver_DeliverCellMeal.MealCandidate(f.hungry) && WorkGiver_RescueInmate.CareCandidate(f.injured);
            if (!ok)
            {
                s.Fail($"the check's own premise: restraints owed {GuardUtility.NeedsSubduing(f.d1)}/{GuardUtility.NeedsSubduing(f.d2)}, a meal waiting for the shut-in inmate {WorkGiver_DeliverCellMeal.MealCandidate(f.hungry)}, the downed one waiting to be carried {WorkGiver_RescueInmate.CareCandidate(f.injured)} (downed {f.injured.Downed}, in bed {f.injured.InBed()})");
                return null;
            }
            return f;
        }

        private sealed class CareObservation
        {
            public bool reserveRestrained, otherRestrained, reserveTended, together, delivered, rescued;
            public int ticks, firstCare = -1, firstReserveRestraint = -1, firstOtherRestraint = -1;
            /// <summary>The reserve guard's first restraint of an inmate who had gone for staff or for the fence by then (the reserve answers that one wherever it is: <see cref="CareDetail.Holds"/>), and the inmates that did.</summary>
            public int firstReserveAnswer = -1;
            public readonly HashSet<Pawn> staffTrouble = new HashSet<Pawn>();
        }

        /// <summary>
        /// Real ticks until the shut-in inmate has been brought a meal and the downed one is in a bed (at most <paramref name="most"/>): who was on what, tick by tick. A restraint the other guard fails (the inmate "resists") puts
        /// the inmate in the assaulting state against that guard, which is trouble aimed at staff: the reserve guard going to restrain that inmate is the reserve working as it should, so it is told from a restraint of a calm one
        /// (<see cref="CareObservation.reserveRestrained"/>) and not counted with it.
        /// </summary>
        private static CareObservation ObserveCare(CareFixture f, int most)
        {
            var o = new CareObservation();
            int start = ShNow;
            bool OnCare(Pawn g) => g.CurJob is { } j && (j.def == JobDefOf.DeliverFood && j.targetB.Thing == f.hungry || j.def == JobDefOf.TakeWoundedPrisonerToBed && j.targetA.Thing == f.injured);
            o.ticks = ShuttleTicks(() =>
            {
                int t = ShNow - start;
                bool rs = f.reserve.CurJobDef == RPSecurityDefOf.RP_Subdue, os = f.other.CurJobDef == RPSecurityDefOf.RP_Subdue;
                bool rc = OnCare(f.reserve), oc = OnCare(f.other);
                foreach (Pawn d in new[] { f.d1, f.d2 })
                    if (!d.Destroyed && d.Spawned && (d.MentalState is MentalState_Escaping || CareDetail.AimedAtStaff(d))) o.staffTrouble.Add(d);
                if (rs && CareDetail.IsReserve(f.reserve) && f.reserve.CurJob.targetA.Thing is Pawn restrained && o.staffTrouble.Contains(restrained))
                {
                    if (o.firstReserveAnswer < 0) o.firstReserveAnswer = t;
                    rs = false;
                }
                if (rs && o.firstReserveRestraint < 0) o.firstReserveRestraint = t;
                if (os && o.firstOtherRestraint < 0) o.firstOtherRestraint = t;
                if ((rc || oc) && o.firstCare < 0) o.firstCare = t;
                o.reserveRestrained |= rs;
                o.otherRestrained |= os;
                o.reserveTended |= rc;
                o.together |= rc && os;
                o.delivered |= f.hungry.needs.food.CurLevel > 0.5f || f.hungry.CurJobDef == JobDefOf.Ingest;
                o.rescued |= f.injured.InBed();
                return o.delivered && o.rescued;
            }, most);
            return o;
        }

        private static void CareStopAll(CareFixture f)
        {
            foreach (Pawn p in new[] { f.reserve, f.other, f.hungry, f.injured, f.d1, f.d2 })
                if (p != null && !p.Destroyed) p.jobs?.StopAll();
        }

        // ---- reserve 1: care completes while the other guard answers the trouble ----------------------------------------------------------------------------------

        /// <summary>
        /// A confined hungry inmate, an injured one, two guards and a remote disturbance, with one guard named for the reserve: over real ticks the reserve guard never goes to restrain anyone (the disturbance is sixteen cells off), the other does,
        /// and the reserve guard, while the other restrains, is on the meal or the carry; both are done. Fails if the reserve guard restrains (the reserve takes no remote response), if nobody on the care job is on it while the other
        /// restrains (care did not go on beside the response), or if the meal or the bed is not reached.
        /// </summary>
        private static void CheckCareReserveOneKeepsCareGoing(Map map)
        {
            Guarded("care detail: reserve 1, care completes while the other guard responds", map, r =>
            {
                const string check = "reserve 1";
                CareFixture f = BuildCareFixture(map, r, check);
                if (f == null) return;
                CareDetail care = CareSet(map, r, 1, f.reserve);
                if (!care.Members.Contains(f.reserve) || !CareDetail.IsReserve(f.reserve) || CareDetail.IsReserve(f.other))
                {
                    CareFail($"{check}: the named guard is not the reserve (members {string.Join(",", care.Members.Select(m => m.LabelShort))}, unavailable {string.Join(",", care.Unavailable.Select(u => u.pawn.LabelShort + " " + u.why))}, why {CareDetail.Why(f.reserve) ?? "none"})");
                    return;
                }
                CareObservation o = ObserveCare(f, 3000);
                RPLog.Message($"[RimPrison:test] care detail ({check}): {o.ticks} ticks, the reserve guard restrained {o.reserveRestrained}, the other {o.otherRestrained}, care began at {o.firstCare}, together {o.together}, meal {o.delivered}, in bed {o.rescued}, the reserve guard answered an inmate gone for staff at {o.firstReserveAnswer}");
                if (o.reserveRestrained) CareFail($"{check}: the reserve guard went to restrain an inmate sixteen cells away who had not gone for staff or the fence (tick {o.firstReserveRestraint})");
                if (!o.otherRestrained) CareFail($"{check}: the guard outside the reserve never restrained anyone in {o.ticks} ticks (the fixture's disturbance did not draw them)");
                if (!o.reserveTended) CareFail($"{check}: the reserve guard was never on the meal or the carry (care began at {o.firstCare}, reserve on {f.reserve.CurJobDef?.defName ?? "nothing"})");
                if (o.otherRestrained && o.reserveTended && !o.together) CareFail($"{check}: the reserve guard was never on care while the other guard was restraining");
                if (!o.delivered || !o.rescued) CareFail($"{check}: after {o.ticks} ticks the meal was brought {o.delivered} and the downed inmate was carried to a bed {o.rescued}");
                CareStopAll(f);
            });
        }

        // ---- reserve 0: the competition --------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The same unit with no reserve: both guards are drawn to the two restraints, and no care starts before they are both on them (the competition for staff the reserve exists for). Fails if care began before both guards had gone to restrain
        /// someone (then the fixture shows no competition, and the reserve-1 run proves nothing), or if a guard never did.
        /// </summary>
        private static void CheckCareReserveZeroIsTheControl(Map map)
        {
            Guarded("care detail: reserve 0, the control shows the competition", map, r =>
            {
                const string check = "reserve 0";
                CareFixture f = BuildCareFixture(map, r, check);
                if (f == null) return;
                CareDetail care = CareSet(map, r, 0);
                if (CareDetail.IsReserve(f.reserve) || care.Members.Count > 0) { CareFail($"{check}: a guard is in the reserve with a reserve of 0"); return; }
                CareObservation o = ObserveCare(f, 1500);
                int both = Math.Max(o.firstReserveRestraint, o.firstOtherRestraint);
                RPLog.Message($"[RimPrison:test] care detail ({check}): {o.ticks} ticks, restraints began at {o.firstReserveRestraint} and {o.firstOtherRestraint}, care at {o.firstCare}, meal {o.delivered}, in bed {o.rescued}");
                if (!o.reserveRestrained || !o.otherRestrained) CareFail($"{check}: with no reserve, not both guards went to restrain (reserve guard {o.reserveRestrained}, other {o.otherRestrained}): the fixture shows no competition for staff");
                else if (o.firstCare >= 0 && o.firstCare < both) CareFail($"{check}: with no reserve care began at tick {o.firstCare}, before both guards were on the restraints (at {o.firstReserveRestraint} and {o.firstOtherRestraint}): the fixture shows no competition for staff");
                CareStopAll(f);
            });
        }

        // ---- reserve 0 is today ----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>What the real work node gives a guard (the emergency one or the normal one), in a few words: the job, and what it is for.</summary>
        private static string CareNode(Pawn guard, bool emergency)
        {
            Job job = new JobGiver_Work { emergency = emergency }.TryIssueJobPackage(guard, default).Job;
            return job == null ? "none" : $"{job.def.defName}>{job.targetA.Thing?.LabelShort ?? "-"}>{job.targetB.Thing?.LabelShort ?? "-"}";
        }

        /// <summary>What a guard is offered in the fixture, in one line: the restraints, the carry, the meal, the searches, whether the care first would give them a job, and what the real work nodes give them (the emergency one and the normal one).</summary>
        private static string CareOffers(CareFixture f, Pawn guard)
        {
            InmateCandidates.Shared.Invalidate();
            var rescue = new WorkGiver_RescueInmate { def = RPCareDefOf.RP_RescueInmate };
            var meal = new WorkGiver_DeliverCellMeal { def = RPCareDefOf.RP_DeliverCellMeal };
            var result = default(ThinkResult);
            bool through = Patch_JobGiver_Work_CareFirst.Prefix(new JobGiver_Work(), guard, ref result);
            return $"restrain {GuardUtility.CanRespondTo(guard, f.d1)}/{GuardUtility.CanRespondTo(guard, f.d2)} carry {rescue.HasJobOnThing(guard, f.injured)} meal {meal.JobOnThing(guard, f.hungry)?.def.defName ?? "none"} "
                   + $"skips {new WorkGiver_SearchVisitor().ShouldSkip(guard)}/{new WorkGiver_SearchCell().ShouldSkip(guard)} care first {!through} reserve {CareDetail.IsReserve(guard)} "
                   + $"nodes emergency {CareNode(guard, true)} normal {CareNode(guard, false)}";
        }

        /// <summary>
        /// A reserve of 0 is the game as it was: every guard is offered everything, and the offers are the same with the reserve's hooks switched off altogether (<see cref="CareDetail.Disabled"/>), the work nodes' real answers (the emergency one and
        /// the normal one) included; the refactored givers still give what they gave (the meal for the shut-in inmate, the carry of the downed one, to those inmates); and the table is not a vacuous one: with the same guard named for the reserve, their
        /// offers are not. Fails if a reserve of 0 changes an offer or a node's answer, if a giver's job or target is not the one it was, or if naming the guard changes none.
        /// </summary>
        private static void CheckCareReserveZeroChangesNothing(Map map)
        {
            Guarded("care detail: a reserve of 0 changes nothing", map, r =>
            {
                const string check = "reserve 0 offers";
                CareFixture f = BuildCareFixture(map, r, check);
                if (f == null) return;
                CareSet(map, r, 0);
                string before = CareOffers(f, f.reserve);
                string withHooks = CareOffers(f, f.reserve) + " | " + CareOffers(f, f.other);
                r.Add(() => CareDetail.Disabled = false);
                CareDetail.Disabled = true;
                string without = CareOffers(f, f.reserve) + " | " + CareOffers(f, f.other);
                CareDetail.Disabled = false;
                if (withHooks != without) CareFail($"{check}: with a reserve of 0 the offers are {withHooks}; with the reserve switched off altogether they are {without}");
                if (!withHooks.StartsWith("restrain True/True carry True meal DeliverFood ")) CareFail($"{check}: the guard was not offered everything with no reserve: {withHooks}");
                // (The refactored givers, pinned to what they give in the fixture: a meal to the shut-in inmate, the carry of the downed inmate to a bed.)
                var rescue = new WorkGiver_RescueInmate { def = RPCareDefOf.RP_RescueInmate };
                var meal = new WorkGiver_DeliverCellMeal { def = RPCareDefOf.RP_DeliverCellMeal };
                Job mealJob = meal.JobOnThing(f.reserve, f.hungry), carryJob = rescue.HasJobOnThing(f.reserve, f.injured) ? rescue.JobOnThing(f.reserve, f.injured) : null;
                if (mealJob == null || mealJob.def != JobDefOf.DeliverFood || mealJob.targetB.Thing != f.hungry)
                    CareFail($"{check}: the cell meal giver gave {mealJob?.def.defName ?? "no job"} for {mealJob?.targetB.Thing?.LabelShort ?? "nobody"} (a delivery to {f.hungry.LabelShort} expected)");
                if (carryJob == null || carryJob.def != JobDefOf.TakeWoundedPrisonerToBed || carryJob.targetA.Thing != f.injured)
                    CareFail($"{check}: the rescue giver gave {carryJob?.def.defName ?? "no job"} for {carryJob?.targetA.Thing?.LabelShort ?? "nobody"} (a carry of {f.injured.LabelShort} to a bed expected)");
                CareSet(map, r, 1, f.reserve);
                string named = CareOffers(f, f.reserve);
                if (named.Replace("reserve True", "reserve False") == before) CareFail($"{check}: naming the guard for the reserve changed nothing in what they are offered ({named})");
                if (!named.StartsWith("restrain False/False ")) CareFail($"{check}: the reserve guard is still offered the far restraints: {named}");
                RPLog.Message($"[RimPrison:test] care detail ({check}): {withHooks}; as the reserve: {named}");
            });
        }

        // ---- the radius ------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The reserve guard is kept from trouble that is far from them and from the inmate they are tending, and from a calm inmate who owes a restraint however near; not from an inmate acting out that is near either, nor from an escape or trouble aimed
        /// at staff wherever it is, nor from the warden's own order; the guard outside the reserve is kept from nothing; the searches skip the reserve guard. Fails if the far restraint, or one for a calm inmate, is offered to the reserve guard, if a near
        /// inmate acting out (or one near their patient, or an escape, or an assault on staff, or a forced one) is not, or if the other guard is held.
        /// </summary>
        private static void CheckCareReserveHoldsFromFarTroubleOnly(Map map)
        {
            Guarded("care detail: the reserve is held from far trouble only", map, r =>
            {
                const string check = "radius";
                CareFixture f = BuildCareFixture(map, r, check);
                if (f == null) return;
                CareSet(map, r, 1, f.reserve);
                // (The far one is d1, at the east end; a near one, d2, at the reserve guard's own end. Both calm, owing a restraint, to begin with.)
                Pawn far = f.d1, near = f.d2;
                f.s.Place(near, f.s.At(6, 1));
                map.reachability.ClearCache();
                InmateCandidates.Shared.Invalidate();
                bool Offered(Pawn who, Pawn trouble, bool forced = false) => GuardUtility.CanRespondTo(who, trouble, forced);
                string cells = $"{f.reserve.Position.DistanceTo(far.Position):F0} cells to the far one, {f.reserve.Position.DistanceTo(near.Position):F0} to the near one";
                bool farCalm = Offered(f.reserve, far), nearCalm = Offered(f.reserve, near), forced = Offered(f.reserve, far, forced: true), otherFar = Offered(f.other, far);
                if (farCalm) CareFail($"{check}: the reserve guard is offered a restraint for a calm inmate far away ({cells})");
                if (nearCalm) CareFail($"{check}: the reserve guard is offered a restraint for a calm inmate owing one, near them ({cells}): only an inmate acting out is in their face");
                if (!forced) CareFail($"{check}: the warden's own order to the reserve guard to restrain was refused");
                if (!otherFar) CareFail($"{check}: the guard outside the reserve is not offered the far restraint");
                if (!new WorkGiver_SearchVisitor().ShouldSkip(f.reserve) || !new WorkGiver_SearchCell().ShouldSkip(f.reserve) || new WorkGiver_SearchVisitor().ShouldSkip(f.other) || new WorkGiver_SearchCell().ShouldSkip(f.other))
                    CareFail($"{check}: the searches are not skipped by the reserve guard alone");
                // Acting out, but neither an escape nor aimed at staff (vandalising): near, offered; far, held.
                if (!CareMentalState(near, RPMentalStateDefOf.RP_Vandalizing)) { CareFail($"{check}: the premise - the inmate did not start vandalising"); return; }
                bool nearActing = Offered(f.reserve, near);
                CareMentalState(near, null);
                CareMentalState(far, RPMentalStateDefOf.RP_Vandalizing);
                bool farActing = Offered(f.reserve, far), farActingOther = Offered(f.other, far);
                CareMentalState(far, null);
                if (!nearActing) CareFail($"{check}: the reserve guard is not offered an inmate acting out near them ({cells}; within {CareDetail.NearTroubleRadius} is immediate danger)");
                if (farActing) CareFail($"{check}: the reserve guard is offered an inmate acting out far away ({cells})");
                if (!farActingOther) CareFail($"{check}: the guard outside the reserve is not offered the far inmate acting out");
                // An escape is never held, wherever it is; nor is trouble aimed at staff (an assault on a member of staff).
                if (!CareMentalState(far, RPMentalStateDefOf.RP_Escaping)) { CareFail($"{check}: the premise - the inmate did not start an escape"); return; }
                bool farEscape = Offered(f.reserve, far);
                if (!CareMentalState(far, RPMentalStateDefOf.RP_Assaulting, f.other)) { CareFail($"{check}: the premise - the inmate did not start an assault on a member of staff"); return; }
                bool farAssault = Offered(f.reserve, far);
                CareMentalState(far, null);
                if (!farEscape) CareFail($"{check}: the reserve guard is not offered an escape {f.reserve.Position.DistanceTo(far.Position):F0} cells away (an escape is never held)");
                if (!farAssault) CareFail($"{check}: the reserve guard is not offered an assault on a member of staff {f.reserve.Position.DistanceTo(far.Position):F0} cells away (trouble aimed at staff is immediate danger)");
                // The guard on the carry: acting out beside the downed inmate they carry is near their patient, though far from them.
                f.s.Place(f.d1, f.s.At(12, 1));
                f.s.Place(f.reserve, f.s.At(1, 1));
                f.s.Place(f.injured, f.s.At(11, 1));
                map.reachability.ClearCache();
                InmateCandidates.Shared.Invalidate();
                Job carry = CareDetail.TryGive(f.reserve, CareKind.Rescue, f.injured);
                if (carry == null) { CareFail($"{check}: the reserve guard was not offered the carry of the downed inmate ({CareDetail.Why(f.reserve) ?? "serving"})"); return; }
                if (!CareMentalState(f.d1, RPMentalStateDefOf.RP_Vandalizing)) { CareFail($"{check}: the premise - the inmate did not start vandalising"); return; }
                bool farFromThem = !Offered(f.reserve, f.d1);
                f.s.Start(f.reserve, carry);
                bool nearTheirPatient = Offered(f.reserve, f.d1);
                f.reserve.jobs.StopAll();
                bool afterwards = Offered(f.reserve, f.d1);
                CareMentalState(f.d1, null);
                if (!farFromThem || !nearTheirPatient || afterwards)
                    CareFail($"{check}: an inmate acting out far from the guard and beside the inmate they are carrying: held before the carry {farFromThem} (yes expected), held on it {!nearTheirPatient} (no expected), held after {!afterwards} (yes expected)");
                CareStopAll(f);
            });
        }

        // ---- the floor and the named guards ------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The automatic pick never takes the last guard on duty (one stays outside the reserve whatever its size), and the warden's naming may. Three guards on duty and a reserve of three: two are in it and the pick says it left a place empty; two on duty:
        /// one; one on duty: none; the one guard on duty named: in it, and the tab's count of guards on duty is no more than the reserve. Fails if the reserve takes the last guard on duty by itself, or does not take a named one.
        /// </summary>
        private static void CheckCareReserveFloor(Map map)
        {
            Guarded("care detail: the pick leaves one guard outside the reserve, a named guard may be the last", map, r =>
            {
                const string check = "floor";
                Pawn a = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), b = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), c = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                foreach (Pawn p in new[] { a, b, c }) CareOnShift(r, p);
                CareOnly(r, a, b, c);
                CareDetail care = CareSet(map, r, 3);
                if (care.OnDuty != 3) { CareFail($"{check}: the premise - {care.OnDuty} guards on duty, three expected"); return; }
                string Members() => string.Join(",", care.Members.Select(m => m.LabelShort));
                if (care.Members.Count != 2 || !care.Floored) CareFail($"{check}: three on duty and a reserve of three: the reserve is [{Members()}] and left a place empty {care.Floored} (two members, one place left empty expected)");
                CareSuspend(r, c);
                care.ForceRebuild();
                care.Tick(map);
                if (care.OnDuty != 2 || care.Members.Count != 1) CareFail($"{check}: two on duty and a reserve of three: {care.OnDuty} on duty, the reserve is [{Members()}] (one member expected)");
                CareSuspend(r, b);
                care.ForceRebuild();
                care.Tick(map);
                if (care.OnDuty != 1 || care.Members.Count != 0) CareFail($"{check}: one guard on duty and a reserve of three: {care.OnDuty} on duty, the reserve is [{Members()}] (nobody expected: the last guard answers the trouble)");
                // The warden names the last one: theirs to decide, and the tab warns (the count of guards on duty is no more than the reserve's).
                care.pins.Clear();
                care.SetSize(1);
                care.TogglePin(a);
                care.ForceRebuild();
                care.Tick(map);
                if (!care.Members.Contains(a) || care.OnDuty > care.Members.Count) CareFail($"{check}: the last guard on duty, named: the reserve is [{Members()}], {care.OnDuty} on duty (the named guard in it, and the warning's condition, expected)");
                // Nobody on duty at all: nobody in it, and not a place filled from nothing.
                CareSuspend(r, a);
                care.ForceRebuild();
                care.Tick(map);
                if (care.Members.Count != 0) CareFail($"{check}: with nobody on duty the reserve is [{Members()}]");
                RPLog.Message($"[RimPrison:test] care detail ({check}): three on duty fill two places of three, two fill one, one fills none; a named last guard is in");
            });
        }

        /// <summary>
        /// A guard the warden named is held to the filters the pick applies to the others: one drafted, in a mental state, standing on a post or tower, or with no care work to do is listed as unavailable, with the reason, and the place is filled
        /// from the others; once they can serve again they are back in the reserve at the next beat. Fails if a named guard in any of these states is in the reserve, is not listed with the reason, or is not back once fit.
        /// </summary>
        private static void CheckCareReserveNamedGuardsAreFiltered(Map map)
        {
            Guarded("care detail: a named guard is filtered as the pick filters", map, r =>
            {
                const string check = "named filters";
                Pawn a = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), b = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), c = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                foreach (Pawn p in new[] { a, b, c }) CareOnShift(r, p);
                CareOnly(r, a, b, c);
                CareDetail care = CareSet(map, r, 1, a);
                if (!care.Members.Contains(a)) { CareFail($"{check}: the premise - the named guard is not in the reserve (unfit: {CareDetail.Unfit(a) ?? "none"})"); return; }
                void Expect(string state, string why)
                {
                    care.Tick(map);
                    string listed = string.Join(",", care.Unavailable.Select(u => u.pawn.LabelShort + " " + u.why));
                    if (care.Members.Contains(a) || care.Unavailable.All(u => u.pawn != a || u.why != why) || !care.IsPinned(a) || care.Members.Count != 1)
                        CareFail($"{check}: the named guard {state}: the reserve is [{string.Join(",", care.Members.Select(m => m.LabelShort))}], unavailable [{listed}], named still {care.IsPinned(a)} (out of the reserve, listed as {why}, named still, the place filled from the others expected)");
                }
                void Back(string state)
                {
                    care.Tick(map);
                    if (!care.Members.Contains(a)) CareFail($"{check}: the named guard is fit again ({state} over) and is not back in the reserve at the next beat (the reserve is [{string.Join(",", care.Members.Select(m => m.LabelShort))}], unfit: {CareDetail.Unfit(a) ?? "none"})");
                }
                // Drafted.
                if (a.drafter != null)
                {
                    a.drafter.Drafted = true;
                    Expect("is drafted", "RP_CareWhy_Drafted");
                    a.drafter.Drafted = false;
                    Back("the draft");
                }
                // In a mental state.
                if (!CareMentalState(a, MentalStateDefOf.Wander_Sad)) { CareFail($"{check}: the premise - the guard did not start a mental state"); return; }
                Expect("is in a mental state", "RP_CareWhy_Mental");
                CareMentalState(a, null);
                Back("the mental state");
                // No care work to do (the warden's work and the doctor's off).
                int warden0 = a.workSettings.GetPriority(WorkTypeDefOf.Warden), doctor0 = a.workSettings.GetPriority(WorkTypeDefOf.Doctor);
                r.Add(() => { a.workSettings.SetPriority(WorkTypeDefOf.Warden, warden0); a.workSettings.SetPriority(WorkTypeDefOf.Doctor, doctor0); });
                a.workSettings.SetPriority(WorkTypeDefOf.Warden, 0);
                a.workSettings.SetPriority(WorkTypeDefOf.Doctor, 0);
                Expect("has no care work on", "RP_CareWhy_NoCare");
                a.workSettings.SetPriority(WorkTypeDefOf.Warden, warden0);
                a.workSettings.SetPriority(WorkTypeDefOf.Doctor, doctor0);
                Back("the work being off");
                // On a post: the job is held by hand (no driver runs: nothing is ticked), as the post's own hold of up to an hour.
                Job post = JobMaker.MakeJob(RPPostDefOf.RP_ManPost);
                a.jobs.curJob = post;
                try
                {
                    Expect("stands on a post", "RP_CareWhy_Post");
                }
                finally
                {
                    a.jobs.curJob = null;
                }
                Back("the post");
                CareStopAll(new CareFixture { reserve = a });
            });
        }

        // ---- unavailable, never assigned --------------------------------------------------------------------------------------------------------------------------

        private static void CheckCareReserveUnavailable(Map map)
        {
            Guarded("care detail: a reserve guard who cannot serve is reported unavailable, never assigned", map, r =>
            {
                const string check = "unavailable";
                PrisonGame g = PrisonGame.Instance;
                // (Three guards: the pick keeps one outside the reserve, so a reserve of one needs two on duty to fill its place.)
                Pawn a = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), b = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), c = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CareOnShift(r, a);
                CareOnShift(r, b);
                CareOnShift(r, c);
                CareOnly(r, a, b, c);
                CareDetail care = CareSet(map, r, 1, a);
                CompStaff sc = a.GetComp<CompStaff>(), scb = b.GetComp<CompStaff>(), scc = c.GetComp<CompStaff>();
                int now = Find.TickManager.TicksGame;
                if (!care.Members.Contains(a) || !CareDetail.IsReserve(a)) { CareFail($"{check}: the premise - the named guard is not the reserve (why not: {CareDetail.Why(a) ?? "serving"}, members {care.Members.Count})"); return; }

                // Suspended: not the reserve at once (no rebuild), then reported, and the automatic pick fills the place from another guard.
                int suspended0 = sc.suspendedUntilTick;
                r.Add(() => sc.suspendedUntilTick = suspended0);
                sc.suspendedUntilTick = now + GenDate.TicksPerDay;
                if (CareDetail.IsReserve(a)) CareFail($"{check}: a suspended guard is still the reserve before the reserve is worked out again (a phantom reserve)");
                care.ForceRebuild();
                care.Tick(map);
                if (care.Members.Contains(a) || care.Members.Count != 1 || care.Unavailable.All(u => u.pawn != a || u.why != "RP_CareWhy_Suspended") || !care.IsPinned(a))
                    CareFail($"{check}: with the named guard suspended the reserve is [{string.Join(",", care.Members.Select(m => m.LabelShort))}], the unavailable [{string.Join(",", care.Unavailable.Select(u => u.pawn.LabelShort + " " + u.why))}], named still {care.IsPinned(a)} (another guard fills the place, the named guard is listed as suspended and stays named)");
                // Nobody else to fill it: capacity reported as missing, not made up.
                int strike0 = scb.strikeUntilTick, strikeC0 = scc.strikeUntilTick;
                r.Add(() => { scb.strikeUntilTick = strike0; scc.strikeUntilTick = strikeC0; });
                scb.strikeUntilTick = scc.strikeUntilTick = now + GenDate.TicksPerDay;
                care.ForceRebuild();
                care.Tick(map);
                if (care.Members.Count != 0 || CareDetail.IsReserve(a) || CareDetail.IsReserve(b) || CareDetail.IsReserve(c)) CareFail($"{check}: with one guard suspended and the others on strike the reserve is [{string.Join(",", care.Members.Select(m => m.LabelShort))}] (empty expected: a guard on strike is no reserve)");
                scb.strikeUntilTick = strike0;
                scc.strikeUntilTick = strikeC0;
                sc.suspendedUntilTick = suspended0;

                // Off shift (the timetable says sleep now): not the reserve at once.
                int hour = GenLocalDate.HourOfDay(map);
                TimeAssignmentDef was0 = a.timetable.GetAssignment(hour);
                r.Add(() => a.timetable.SetAssignment(hour, was0));
                a.timetable.SetAssignment(hour, TimeAssignmentDefOf.Sleep);
                string why = CareDetail.Why(a);
                if (why != "RP_CareWhy_OffShift" || CareDetail.IsReserve(a)) CareFail($"{check}: a guard whose timetable says sleep is {(why ?? "serving")} and the reserve {CareDetail.IsReserve(a)} (off shift, not the reserve, expected)");
                care.ForceRebuild();
                care.Tick(map);
                if (care.Members.Contains(a) || care.Unavailable.All(u => u.pawn != a || u.why != "RP_CareWhy_OffShift")) CareFail($"{check}: an off-shift named guard is not reported unavailable");
                a.timetable.SetAssignment(hour, was0);

                // On strike: no silent exemption.
                int strikeA = sc.strikeUntilTick;
                r.Add(() => sc.strikeUntilTick = strikeA);
                sc.strikeUntilTick = now + GenDate.TicksPerDay;
                if (CareDetail.IsReserve(a) || CareDetail.Why(a) != "RP_CareWhy_Strike") CareFail($"{check}: a striking guard is {(CareDetail.Why(a) ?? "serving")} (on strike expected)");
                sc.strikeUntilTick = strikeA;

                // At home (off the map): named, unavailable, never a member.
                Pawn home = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                care.pins.Clear();
                care.SetSize(1);
                care.TogglePin(home);
                CareOnly(r, a, b, c, home);
                care.ForceRebuild();
                care.Tick(map);
                if (care.Members.Contains(home) || care.Unavailable.All(u => u.pawn != home) || !care.IsPinned(home)) CareFail($"{check}: a named guard who is at home is a member {care.Members.Contains(home)} or not reported unavailable (unavailable [{string.Join(",", care.Unavailable.Select(u => u.pawn.LabelShort + " " + u.why))}])");

                // A named guard who is gone (let go, dead) loses the name; the places are filled again.
                Pawn gone = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CareOnShift(r, gone);
                care.pins.Clear();
                care.SetSize(2);
                care.TogglePin(gone);
                care.TogglePin(a);
                CareOnly(r, a, b, c, gone);
                care.ForceRebuild();
                care.Tick(map);
                if (!care.IsPinned(gone) || !care.Members.Contains(gone)) CareFail($"{check}: the premise - a named guard on duty is not in the reserve");
                int goneId = gone.thingIDNumber;
                gone.Destroy(DestroyMode.Vanish);
                g.home.Remove(gone);
                StaffCommute.StaffVersion++;
                care.Tick(map);
                if (care.pins.Contains(goneId) || !care.IsPinned(a)) CareFail($"{check}: a named guard who is gone keeps the name (names [{string.Join(",", care.pins)}], the gone guard's id {goneId})");
                RPLog.Message($"[RimPrison:test] care detail ({check}): suspended, struck, off shift, at home and gone guards are not assigned and are reported");
            });
        }

        // ---- the pick, the release, the queue ---------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The automatic pick prefers a guard already on care work; the release empties the reserve for the hour and says which care waits (and the guard answers trouble like the rest); calling it back fills it; the queue is built once
        /// for any number of questions in a tick. Fails if the guard on care work is passed over for an idle one, if the release leaves a member or says nothing, or if the queue is worked out again for each question.
        /// </summary>
        private static void CheckCareReservePickAndRelease(Map map)
        {
            Guarded("care detail: the pick prefers care work, the release empties the reserve, one queue", map, r =>
            {
                const string check = "pick and release";
                CareFixture f = BuildCareFixture(map, r, check);
                if (f == null) return;
                // The idle guard has the lower id, so that passing over care work is what an id order would do.
                (Pawn idle, Pawn busy) = f.reserve.thingIDNumber < f.other.thingIDNumber ? (f.reserve, f.other) : (f.other, f.reserve);
                CareDetail care = CareSet(map, r, 0);
                care.SetSize(1);
                InmateCandidates.Shared.Invalidate();
                Job meal = CareDetail.TryGive(busy, CareKind.Meal, f.hungry) ?? CareDetail.TryGive(busy, CareKind.Rescue, f.injured);
                if (meal == null) { CareFail($"{check}: the premise - the guard was offered no care job to be on"); return; }
                f.s.Start(busy, meal);
                care.ForceRebuild();
                care.Tick(map);
                if (care.Members.Count != 1 || care.Members[0] != busy) CareFail($"{check}: the reserve is [{string.Join(",", care.Members.Select(m => m.LabelShort + " on " + (m.CurJobDef?.defName ?? "nothing")))}] when {busy.LabelShort} is on {busy.CurJobDef?.defName} and {idle.LabelShort} (the lower id) is idle: the guard on care work is expected");
                // One queue: asked for again and again in a tick, it is worked out once.
                int builds0 = care.builds;
                IReadOnlyList<CareRequest> q = care.QueueFor(map);
                for (int i = 0; i < 40; i++)
                {
                    care.QueueFor(map);
                    CareDetail.CareJobFor(idle);
                }
                if (care.builds != builds0 || !ReferenceEquals(q, care.QueueFor(map))) CareFail($"{check}: the care queue was worked out {care.builds - builds0} more times for forty questions in one tick");
                if (!care.Queue.Any(x => x.kind == CareKind.Meal && x.inmate == f.hungry) || !care.Queue.Any(x => x.kind == CareKind.Rescue && x.inmate == f.injured))
                    CareFail($"{check}: the queue is [{string.Join(", ", care.Queue.Select(x => x.kind + " " + x.inmate.LabelShort))}] (the meal for the shut-in inmate and the carry of the downed one expected)");
                // The release.
                care.ForceRebuild();
                care.Tick(map);
                if (care.Members.Count != 1) { CareFail($"{check}: no reserve to release"); return; }
                care.Release(map);
                string summary = care.ReleaseSummary();
                if (care.Members.Count != 0 || CareDetail.IsReserve(busy) || !care.Released(map)) CareFail($"{check}: after the release the reserve is [{string.Join(",", care.Members.Select(m => m.LabelShort))}] (empty expected)");
                if (summary == "RP_CareReleasedNone".Translate().ToString() || care.WaitingCount == 0) CareFail($"{check}: the release says nothing is waiting while the meal and the carry are ({summary})");
                care.Tick(map);
                if (care.Members.Count != 0) CareFail($"{check}: the reserve was filled again at the map's next beat within the hour of its release");
                care.Resume(map);
                if (care.Members.Count != 1) CareFail($"{check}: calling the reserve back left it empty");
                CareStopAll(f);
                RPLog.Message($"[RimPrison:test] care detail ({check}): {summary}");
            });
        }

        // ---- the care first: the work node gives the care job before work vanilla would rank higher -----------------------------------------------------------------------

        /// <summary>The job the work node (the normal one) gives the guard now, null for none.</summary>
        private static Job CareNormalJob(Pawn guard) => new JobGiver_Work { emergency = false }.TryIssueJobPackage(guard, default).Job;

        private static string CareJobText(Job job) => job == null ? "no job" : $"{job.def.defName} for {job.targetA.Thing?.LabelShort ?? "-"}/{job.targetB.Thing?.LabelShort ?? "-"}";

        /// <summary>
        /// The prefix on the work node (<see cref="Patch_JobGiver_Work_CareFirst"/>), through the real <c>TryIssueJobPackage</c>: the shut-in inmate is due for a yard walk (the regime is the yard alone all day), which the work scan ranks above a cell meal
        /// (the escort's priority is 145, the meal's 70), and the doctor's work is off so that nothing else outranks it. With the care first off the reserve guard is given what the scan ranks first (a yard walk, not care: the premise); with it on they
        /// are given care (a carry, the meal); the guard outside the reserve is given the same with it on as off; the emergency node is left alone; and the care job the guard is given is the one <see cref="CareDetail.CareJobFor"/> says.
        /// Fails if the reserve guard is given anything but care with the care first on, if the scan gave care without it (the check then proves nothing), if the guard outside the reserve is given something else with it on, or if the emergency node is taken.
        /// </summary>
        private static void CheckCareFirstOutranksTheScan(Map map)
        {
            Guarded("care detail: the care first outranks the work the scan would rank higher", map, r =>
            {
                const string check = "care first";
                CareFixture f = BuildCareFixture(map, r, check, RPSupermaxDefOf.RP_YardAlone);
                if (f == null) return;
                foreach (Pawn g in new[] { f.reserve, f.other })
                {
                    Pawn guard = g;
                    int doctor0 = guard.workSettings.GetPriority(WorkTypeDefOf.Doctor);
                    r.Add(() => guard.workSettings.SetPriority(WorkTypeDefOf.Doctor, doctor0));
                    guard.workSettings.SetPriority(WorkTypeDefOf.Doctor, 0);
                }
                CareSet(map, r, 1, f.reserve);
                if (!CareDetail.IsReserve(f.reserve)) { CareFail($"{check}: the premise - the named guard is not the reserve (unfit: {CareDetail.Unfit(f.reserve) ?? "none"})"); return; }
                InmateCandidates.Shared.Invalidate();
                r.Add(() => Patch_JobGiver_Work_CareFirst.Off = false);
                Patch_JobGiver_Work_CareFirst.Off = true;
                Job scanned = CareNormalJob(f.reserve), scannedOther = CareNormalJob(f.other);
                Patch_JobGiver_Work_CareFirst.Off = false;
                Job given = CareNormalJob(f.reserve), givenOther = CareNormalJob(f.other);
                Job careJob = CareDetail.CareJobFor(f.reserve);
                var forEmergency = default(ThinkResult);
                bool emergencyThrough = Patch_JobGiver_Work_CareFirst.Prefix(new JobGiver_Work { emergency = true }, f.reserve, ref forEmergency);
                RPLog.Message($"[RimPrison:test] care detail ({check}): the scan alone gives the reserve guard {CareJobText(scanned)}, with the care first {CareJobText(given)} (care job {CareJobText(careJob)}); the other guard {CareJobText(scannedOther)} / {CareJobText(givenOther)}");
                if (CareDetail.IsCareJob(scanned)) { CareFail($"{check}: the premise - without the care first the work scan gives the reserve guard care already ({CareJobText(scanned)}): nothing here outranks it"); return; }
                if (careJob == null || !CareDetail.IsCareJob(careJob)) CareFail($"{check}: the care job for the reserve guard is {CareJobText(careJob)} (a care job expected: the meal for the shut-in inmate or the carry of the downed one)");
                if (!CareDetail.IsCareJob(given)) CareFail($"{check}: with the care first on the work node gives the reserve guard {CareJobText(given)}, not care (the scan alone gives {CareJobText(scanned)}, the care job is {CareJobText(careJob)})");
                if (CareJobText(givenOther) != CareJobText(scannedOther)) CareFail($"{check}: the guard outside the reserve is given {CareJobText(givenOther)} with the care first on and {CareJobText(scannedOther)} with it off");
                if (!emergencyThrough) CareFail($"{check}: the care first takes the emergency work node (a rescue and the fire stay the vanilla node's)");
                CareStopAll(f);
            });
        }

        /// <summary>The scene guards of the care checks that are left alone by the hourly pass (see <see cref="PostGuardHeld"/>).</summary>
        private static readonly List<Pawn> careHeld = new List<Pawn>();

        /// <summary>The guard's standing in the reserve in one line, for the failure line of a check that waits for them to be called: the job, the reserve's members and the reasons, the care job the look would give, the requests and who is on them.</summary>
        private static string CareStanding(CareFixture f, Pawn guard)
        {
            CareDetail care = f.care;
            string requests = string.Join(", ", care.Queue.Select(q => $"{q.kind} {q.inmate.LabelShort}{(q.served ? " served" : "")}"));
            return $"{guard.LabelShort} job {CareJobText(guard.CurJob)}, in the reserve {CareDetail.IsReserve(guard)} (why not {CareDetail.Why(guard) ?? "-"}, unfit {CareDetail.Unfit(guard) ?? "-"}), members [{string.Join(",", care.Members.Select(m => m.LabelShort))}], "
                   + $"the care job now {CareJobText(CareDetail.CareJobFor(guard))}, requests [{requests}], the other guard on {CareJobText(f.other.CurJob)}, the hour {GenLocalDate.HourOfDay(guard.Map)}:00";
        }

        /// <summary>
        /// A reserve guard on patrol is called off it for the care that waits (the patrol's own look every 120 ticks, <see cref="JobDriver_Patrol"/>): put on a patrol with a meal and a carry waiting, the guard is on a care job within 240 ticks, well
        /// before the patrol's own wait (300 ticks or more) is out. Fails if the guard is still on patrol after that. The look is every 120 ticks, and <see cref="CareDetail.CareJobFor"/> is asked once for each look (at most <see cref="CareDetail.MaxTries"/> requests,
        /// a failed errand waits <see cref="CareDetail.RetryTicks"/>, which is the interval of the look), so the two looks of the window are enough. What the check has to keep is the care waiting: the guard outside the reserve has the warden's work off for it
        /// (stability12 - one of them with both the carry and the meal reserved leaves the reserve guard nothing to be called to, and the patrol goes on rightly), and the scene guards are held from the hourly pass (the run that failed had the hour turn in the window).
        /// </summary>
        private static void CheckCareReservePatrolBreaksOff(Map map)
        {
            Guarded("care detail: a reserve guard on patrol is called off it for the care that waits", map, r =>
            {
                const string check = "patrol";
                CareFixture f = BuildCareFixture(map, r, check);
                if (f == null) return;
                careHeld.Add(f.reserve);
                careHeld.Add(f.other);
                r.Add(() => { careHeld.Remove(f.reserve); careHeld.Remove(f.other); });
                int warden0 = f.other.workSettings.GetPriority(WorkTypeDefOf.Warden);
                r.Add(() => f.other.workSettings?.SetPriority(WorkTypeDefOf.Warden, warden0));
                f.other.workSettings.SetPriority(WorkTypeDefOf.Warden, 0);
                f.other.jobs.StopAll();
                CareSet(map, r, 1, f.reserve);
                if (CareDetail.CareJobFor(f.reserve) == null) { CareFail($"{check}: the premise - the reserve guard has no care job to be called to ({CareStanding(f, f.reserve)})"); return; }
                Job patrol = JobMaker.MakeJob(RPSecurityDefOf.RP_Patrol, f.reserve.Position);
                f.s.Start(f.reserve, patrol);
                int hour = GenLocalDate.HourOfDay(map);
                int ticks = ShuttleTicks(() => f.reserve.CurJobDef != RPSecurityDefOf.RP_Patrol, 240);
                RPLog.Message($"[RimPrison:test] care detail ({check}): the reserve guard left the patrol after {ticks} ticks for {CareJobText(f.reserve.CurJob)}");
                if (f.reserve.CurJobDef == RPSecurityDefOf.RP_Patrol) CareFail($"{check}: after {ticks} ticks the reserve guard is still on patrol with care waiting (hour {hour}:00 at the start): {CareStanding(f, f.reserve)}");
                else if (!CareDetail.IsCareJob(f.reserve.CurJob)) CareFail($"{check}: the reserve guard left the patrol after {ticks} ticks for {CareJobText(f.reserve.CurJob)}, not for care");
                CareStopAll(f);
            });
        }

        /// <summary>
        /// The count's guards are not the reserve's (<see cref="CountSystem.Free"/>): with a count called, the free guard is sent to walk a wing and the reserve guard is not (nor does the count want them, which would end a patrol); with the reserve released either
        /// may; and a count that has only the reserve guard to send is missed, as one with nobody free is. Told, not failed, when the free guard is given no wing at all (the live prison's wings cannot be walked from the intake, or a post is due that guard).
        /// </summary>
        private static void CheckCareReserveTakesNoCount(Map map)
        {
            Guarded("care detail: a reserve guard is not sent to walk a count", map, r =>
            {
                const string check = "count";
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                PrisonMap pm = t.pm;
                Pawn reserve = t.Guard(), free = t.Guard();
                foreach (Pawn guard in new[] { reserve, free }) guard.workSettings?.SetPriority(WorkTypeDefOf.Warden, 1);
                CareOnly(r, reserve, free);
                CareDetail care = CareSet(map, r, 1, reserve);
                if (!CareDetail.IsReserve(reserve) || CareDetail.IsReserve(free)) { CareFail($"{check}: the premise - the reserve is [{string.Join(",", care.Members.Select(m => m.LabelShort))}], not the named guard alone"); return; }
                t.GoneUnseen(EscapeWay.Walked);
                CountSystem.Begin(map, pm, t.g, Find.TickManager.TicksGame);
                if (!pm.count.Running) { CareFail($"{check}: the premise - a count called with a runner of the wing hidden has no wing to walk"); return; }
                pm.count.dueTick = Find.TickManager.TicksGame;
                CountSystem.lastTry.Clear();
                Job reserveJob = CountSystem.JobFor(reserve);
                bool reserveWanted = CountSystem.WantsGuard(reserve);
                CountSystem.lastTry.Clear();
                Job freeJob = CountSystem.JobFor(free);
                if (freeJob == null)
                {
                    RPLog.Message($"[RimPrison:test] care detail ({check}): the free guard is given no wing from {free.Position} (a post due, or the wing cannot be walked), the reserve is not judged");
                    return;
                }
                if (reserveJob != null) CareFail($"{check}: the reserve guard is given {reserveJob.def.defName} for wing {reserveJob.count} with a free guard to send");
                if (reserveWanted) CareFail($"{check}: the count wants the reserve guard (a patrol would be ended for it)");
                // The reserve let go for the hour: its guard answers the count like the others (and the check is no proof if they would not walk it anyway).
                care.Release(map);
                CountSystem.lastTry.Clear();
                Job released = CountSystem.JobFor(reserve);
                if (CareDetail.IsReserve(reserve)) CareFail($"{check}: the premise - the guard is in the reserve after its release");
                else if (released == null) RPLog.Message($"[RimPrison:test] care detail ({check}): the released reserve guard is given no wing either, the release is not judged");
                else if (released.def != RPCountDefOf.RP_Count) CareFail($"{check}: the released reserve guard is given {released.def.defName}, not a count");
                care.Resume(map);
                if (!CareDetail.IsReserve(reserve)) { CareFail($"{check}: the premise - the reserve is not called back"); return; }
                // Nobody else free: the count has nobody to send, and an hour on is missed.
                free.timetable?.SetAssignment(GenLocalDate.HourOfDay(map), TimeAssignmentDefOf.Sleep);
                CountSystem.lastTry.Clear();
                if (CountSystem.JobFor(reserve) != null || CountSystem.JobFor(free) != null) CareFail($"{check}: the count has a guard to send with the reserve guard held and the other off shift");
                (pm.count.startedTick, pm.count.dueTick) = (pm.count.startedTick - CountSystem.WindowTicks - 1, pm.count.dueTick - CountSystem.WindowTicks - 1);
                CountSystem.Tick(map, pm);
                if (pm.count.Running || pm.count.lastMissedTick < 0 || pm.count.lastClearedTick >= 0)
                    CareFail($"{check}: a count with only the reserve guard to send is not a missed one: running {pm.count.Running}, missed at {pm.count.lastMissedTick}, cleared at {pm.count.lastClearedTick}");
                RPLog.Message($"[RimPrison:test] care detail ({check}): the free guard was given a wing, the reserve guard none (and a count with only the reserve to send was missed), the released reserve guard {(released == null ? "none" : "was")}");
            });
        }

        // ---- saveload: a save during a delivery ---------------------------------------------------------------------------------------------------------------------

        private static IntVec3 careSeedOrigin = IntVec3.Invalid;
        private static int careSeedReserve = -1, careSeedOther = -1, careSeedHungry = -1, careSeedSize;
        private static string careSeedDescribe;
        private static List<int> careSeedPins0;
        private static int careSeedSize0;
        private static float careSeedFood;
        private static string careAtLoad;

        /// <summary>The seed's premise does not hold in the live prison: told (the log), not failed; the unit is put away and the policy put back.</summary>
        private static void CareSeedTells(Map map, Rollback r, SupermaxScene s, string why, Pawn[] guards, Pawn[] inmates)
        {
            RPLog.Message($"[RimPrison:test] saveload care: not seeded - {why} (told, not failed: the live prison did not give the seed's premise)");
            careSeedOrigin = IntVec3.Invalid;
            TakeDownSupermaxSeed(map, s.pm, PrisonGame.Instance, s.origin, guards, inmates, restoreRegime: false);
            RestoreCareSeedPolicy(map);
            r.Dispose();
        }

        /// <summary>
        /// Before the save: a unit with a shut-in hungry inmate and two guards named for a reserve of two (the policy is in the snapshot of the round trip); the reserve guard is given the meal by the care first (<see cref="CareDetail.CareJobFor"/>) and
        /// started on it by hand, which carries the meal at once, and that is where the save is made. No game tick is run for it (at most a few, to see the carry begin): the seeds before this one have recorded clocks and jobs that a real tick moves (a parole
        /// hearing's escort and stage, a supermax walk half done, a shuttle on the pad), and the first version, which ticked up to 1500, moved all of them. The supermax seed's regime (the yard alone, all day) is still in force, so the inmate is confined
        /// to the cell by the warden's lockup: no guard takes them for a walk, and the delivery finishes. When the live prison does not give the premise the seed says so (the log) and is put away; it does not fail the plan.
        /// </summary>
        private static void SeedCareDetailForReload(Map map)
        {
            careSeedOrigin = IntVec3.Invalid;
            var r = new Rollback(map);
            SupermaxScene s = SupermaxScene.Build(map, r, "saveload care");
            if (s == null)
            {
                RPLog.Message("[RimPrison:test] saveload care: not seeded - no open ground for a unit to seed a delivery in (told, not failed)");
                r.Dispose();
                return;
            }
            CareDetail care = CareDetail.Of(map);
            (careSeedSize0, careSeedPins0) = (care.size, care.pins.ToList());
            // (No strike of the real staff and no limit on the pick: both guards are named, so that nobody else is picked, and the unit is walled.)
            Pawn reserve = CareGuard(s, "Zsavecr", s.At(1, 1)), other = CareGuard(s, "Zsavecs", s.At(2, 1));
            Thing meal = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MealSimple"));
            reserve.inventory.innerContainer.TryAdd(meal);
            Pawn hungry = s.Inmate("Zsavech", s.bedA, s.CellA);
            hungry.needs.food.CurLevel = 0.18f;
            // (Confined: the due yard walk of a supermax inmate would take them out of the cell, see above.)
            (hungry.Inmate().forcedActivity, hungry.Inmate().forcedUntilTick) = (RPDefOf.RP_Lockup, Find.TickManager.TicksGame + 3 * GenDate.TicksPerDay);
            s.pm.ForceDoorUpdate();
            map.reachability.ClearCache();
            InmateCandidates.Shared.Invalidate();
            care.pins.Clear();
            care.SetSize(2);
            care.TogglePin(reserve);
            care.TogglePin(other);
            care.ForceRebuild();
            care.Tick(map);
            Pawn[] guards = { reserve, other }, inmates = { hungry };
            if (!CareDetail.IsReserve(reserve) || !WorkGiver_DeliverCellMeal.MealCandidate(hungry))
            {
                CareSeedTells(map, r, s, $"the reserve guard is {(CareDetail.IsReserve(reserve) ? "" : "not ")}the reserve (unfit: {CareDetail.Unfit(reserve) ?? "none"}) and the shut-in inmate {(WorkGiver_DeliverCellMeal.MealCandidate(hungry) ? "waits" : "does not wait")} for a meal", guards, inmates);
                return;
            }
            Job job = CareDetail.CareJobFor(reserve);
            if (job == null || job.def != JobDefOf.DeliverFood)
            {
                CareSeedTells(map, r, s, $"the care first gave the reserve guard {CareJobText(job)}, not the meal (queue [{string.Join(", ", care.Queue.Select(q => q.kind + " " + q.inmate.LabelShort))}])", guards, inmates);
                return;
            }
            s.Start(reserve, job);
            careSeedOrigin = s.origin;
            (careSeedReserve, careSeedOther, careSeedHungry, careSeedSize, careSeedFood) = (reserve.thingIDNumber, other.thingIDNumber, hungry.thingIDNumber, care.size, hungry.needs.food.CurLevel);
            // (Carrying it, out in the corridor, is the moment. Starting the job carries the meal; a few ticks only if it somehow does not.)
            bool Carrying() => reserve.CurJobDef == JobDefOf.DeliverFood && reserve.carryTracker?.CarriedThing != null && reserve.GetRoom() != hungry.GetRoom();
            ShuttleTicks(Carrying, 60);
            careSeedDescribe = care.Describe();
            if (!Carrying())
            {
                CareSeedTells(map, r, s, $"the reserve guard was not carrying the meal at the save (job {reserve.CurJobDef?.defName ?? "none"}, carrying {reserve.carryTracker?.CarriedThing?.LabelNoCount ?? "nothing"}, in the room of the inmate {reserve.GetRoom() == hungry.GetRoom()})", guards, inmates);
                return;
            }
            RPLog.Message($"[RimPrison:test] saveload care: {reserve.LabelShort} is carrying the meal to {hungry.LabelShort} at {reserve.Position - s.origin}, the reserve is {careSeedDescribe}");
        }

        private static void RestoreCareSeedPolicy(Map map)
        {
            CareDetail care = CareDetail.Of(map);
            if (care == null || careSeedPins0 == null) return;
            care.pins.Clear();
            care.SetSize(careSeedSize0);
            care.pins.AddRange(careSeedPins0);
            care.ForceRebuild();
            careSeedPins0 = null;
        }

        /// <summary>The care reserve of the snapshot of the round trip (size and the names).</summary>
        private static string CareDescribe(Map map) => CareDetail.Of(map)?.Describe() ?? "";

        /// <summary>First thing after the load, before any tick: the guard is still on the delivery, still carrying.</summary>
        private static void RecordCareAtLoad(Map map)
        {
            careAtLoad = null;
            if (!careSeedOrigin.IsValid) return;
            Pawn reserve = SupermaxSeedPawn(map, careSeedReserve);
            if (reserve == null) return;
            careAtLoad = $"{reserve.CurJobDef == JobDefOf.DeliverFood}/{reserve.carryTracker?.CarriedThing != null}";
        }

        /// <summary>
        /// After the load, with no tick: the policy came back, the guard is on the delivery with the meal in hand (nothing abandoned). The delivery itself is judged later (<see cref="FinishCareDetailAfterReload"/>), after the checks that
        /// tick the game for thousands of ticks and expect the other seeds' walks as they were at the load; the unit stays up until then.
        /// </summary>
        private static void CheckCareDetailAfterReload(Map map)
        {
            if (!careSeedOrigin.IsValid) return;
            Pawn reserve = SupermaxSeedPawn(map, careSeedReserve), other = SupermaxSeedPawn(map, careSeedOther), hungry = SupermaxSeedPawn(map, careSeedHungry);
            if (reserve == null || other == null || hungry == null)
            {
                reloadProblems.Add("saveload care: the guards and the inmate of the delivery did not survive the load");
                return;
            }
            CareDetail care = CareDetail.Of(map);
            if (care.Describe() != careSeedDescribe) reloadProblems.Add($"saveload care: the reserve's size and names changed in the round trip: {care.Describe()} (saved {careSeedDescribe})");
            if (careAtLoad != "True/True") reloadProblems.Add($"saveload care: just after the load the guard's delivery job and the meal in hand were {careAtLoad} (True/True expected: nothing abandoned)");
        }

        /// <summary>
        /// After the checks that tick the game: the inmate is brought exactly one meal and eats it (ticked a while longer here if the delivery is not done), nothing is left in anybody's hands or on the floor of the unit, and the unit is taken down.
        /// (The delivery has been running through the other checks' ticks: a second delivery would need a second meal, and the one meal is the guard's.) Fails if no meal was delivered, if a meal is left over, or if the inmate is over a full stomach.
        /// </summary>
        private static void FinishCareDetailAfterReload(Map map)
        {
            if (!careSeedOrigin.IsValid) return;
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            IntVec3 origin = careSeedOrigin;
            careSeedOrigin = IntVec3.Invalid;
            Pawn reserve = SupermaxSeedPawn(map, careSeedReserve), other = SupermaxSeedPawn(map, careSeedOther), hungry = SupermaxSeedPawn(map, careSeedHungry);
            try
            {
                if (reserve == null || other == null || hungry == null) return;
                var rect = new CellRect(origin.x, origin.z, SupermaxScene.Width, SupermaxScene.Height);
                int Meals() => map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("MealSimple")).Count(t => t.Spawned && rect.Contains(t.Position));
                bool fed = hungry.needs.food.CurLevel > careSeedFood + 0.4f;
                int ticked = ShuttleTicks(() => fed |= hungry.needs.food.CurLevel > careSeedFood + 0.4f, 3000);
                // (A while longer: a meal carried about for nobody would show.)
                ShuttleTicks(() => false, 300);
                bool held = reserve.carryTracker?.CarriedThing != null || reserve.inventory.innerContainer.Any(t => t.def.defName == "MealSimple");
                RPLog.Message($"[RimPrison:test] saveload care: after the load the meal was eaten {fed} ({ticked} more ticks here), meals left in the unit {Meals()}, still held {held}");
                if (!fed) reloadProblems.Add($"saveload care: no meal was delivered after the load ({ticked} ticks: the guard on {reserve.CurJobDef?.defName ?? "nothing"}, carrying {reserve.carryTracker?.CarriedThing?.LabelNoCount ?? "nothing"}, the inmate at {hungry.needs.food.CurLevel:F2})");
                if (held || Meals() > 0) reloadProblems.Add($"saveload care: a meal was left over after the delivery (held {held}, lying in the unit {Meals()}): exactly one meal was expected");
                if (hungry.needs.food.CurLevel > 1.0001f) reloadProblems.Add($"saveload care: the inmate is above a full stomach ({hungry.needs.food.CurLevel:F2})");
            }
            finally
            {
                TakeDownSupermaxSeed(map, pm, g, origin, new[] { reserve, other }, new[] { hungry }, restoreRegime: false);
                RestoreCareSeedPolicy(map);
            }
        }
    }
}
