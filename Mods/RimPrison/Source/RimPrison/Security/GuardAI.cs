using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPSecurityDefOf
    {
        public static JobDef RP_Subdue;
        public static JobDef RP_Patrol;
        public static HediffDef RP_Restrained;
        public static RegimeActivityDef RP_Solitary;
        public static PrisonZoneDef RP_SolitaryZone;
        public static ThoughtDef RP_Punished;
        public static ThoughtDef RP_Sanctioned;
        public static StaffRoleDef RP_Guard;
        public static WorkGiverDef RP_RespondToTrouble;
        public static SecurityLevelDef RP_Protective;
        public static SecurityLevelDef RP_Medium;
        public static SecurityLevelDef RP_Maximum;

        static RPSecurityDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPSecurityDefOf));
    }

    public static class GuardUtility
    {
        /// <summary>An inmate acting out: aggressive, fighting, escaping or vandalising (a riot, an escape, a fight: the alarms).</summary>
        public static bool ActingOut(Pawn t) =>
            // (A runaway nobody has seen is not trouble the guards know of, and not the warden: see CountSystem. They go after one that is seen.)
            t.MentalState is { } ms && (ms.def.IsAggro || ms.def == MentalStateDefOf.SocialFighting || ms is MentalState_Escaping && !CountSystem.IsHidden(t) || ms is MentalState_Vandalizing);

        /// <summary>Inmate that staff should restrain now: acting out, or caught and due for punishment.</summary>
        public static bool NeedsSubduing(Pawn t)
        {
            if (!t.Spawned || t.Downed || !t.IsPrisonerOfColony || t.guest.Released) return false;
            if (ActingOut(t)) return true;
            return t.Inmate() is { pendingPunishmentHours: > 0 };
        }

        /// <summary>Would this guard be sent to restrain this inmate now (the emergency work giver's own test)?</summary>
        public static bool CanRespondTo(Pawn guard, Pawn inmate, bool forced = false) =>
            // (A reserve guard kept for care is not sent to trouble that is not near them or their patient: see CareDetail.Holds, asked before the way there is. The warden's order is never put off.)
            NeedsSubduing(inmate) && (forced || !CareDetail.Holds(guard, inmate)) && guard.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced)
            // (A guard asked to restrain their own kin hesitates when another guard can go: see Kin.Hesitates, which does not count the reserve.)
            && (forced || !Kin.Hesitates(guard, inmate));

        /// <summary>
        /// Is an alarm sounding that this guard would be sent to: an inmate acting out, and nobody else on them yet? (Not the inmates who only owe
        /// punishment, who walk calmly about at the slow pace until a guard comes by: a guard on a short errand finishes it first.)
        /// </summary>
        public static bool AlarmFor(Pawn guard) => AlarmInmate(guard) != null;

        /// <summary>The inmate acting out whom this guard would be sent to (see <see cref="AlarmFor"/>), if any.</summary>
        public static Pawn AlarmInmate(Pawn guard)
        {
            if (!IsGuard(guard) || guard.Map == null || guard.WorkTagIsDisabled(WorkTags.Violent)) return null;
            foreach (Pawn inmate in guard.Map.mapPawns.PrisonersOfColonySpawned)
                if (ActingOut(inmate) && CanRespondTo(guard, inmate)) return inmate;
            return null;
        }

        public static bool IsGuard(Pawn p) => p.GetComp<CompStaff>() is { role.guardDuties: true } sc && !CorruptionSystem.Suspended(sc) && !HostageCrisis.IsHeld(p);
    }

    /// <summary>Emergency warden work: go and restrain inmates who fight, riot, escape or are due for punishment.</summary>
    public class WorkGiver_RespondToTrouble : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Subdue(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            pawn.Map.mapPawns.PrisonersOfColonySpawnedCount == 0 || pawn.WorkTagIsDisabled(WorkTags.Violent) || (!forced && InmateCandidates.Shared.Subdue(pawn.Map).Count == 0);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && GuardUtility.CanRespondTo(pawn, inmate, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPSecurityDefOf.RP_Subdue, t);
    }

    /// <summary>Carries restrained inmates to solitary (or their own cell) to serve their punishment.</summary>
    public class WorkGiver_TakeToPunishment : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.PunishmentCarry(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !forced && InmateCandidates.Shared.PunishmentCarry(pawn.Map).Count == 0;

        /// <summary>
        /// The tests of <see cref="HasJobOnThing"/> that do not depend on the guard (down, with punishment owed). Not an evacuee while their wing is emptied (<see cref="EvacuationSystem.BeingEmptied"/>): the hole beside it is part of it,
        /// and their own cell (the other place the sentence is served) is in it; the carry comes after the all-clear.
        /// </summary>
        public static bool Candidate(Pawn inmate) => inmate.Downed && inmate.Inmate() is { pendingPunishmentHours: > 0 } && !EvacuationSystem.BeingEmptied(inmate);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is not Pawn inmate || !Candidate(inmate) || !forced && CareDetail.Holds(pawn, inmate)) return false;
            // Already lying in the right place (solitary, or their cell when there is no solitary bed): sentence starts there.
            if (inmate.InBed() && (PunishmentUtility.IsSolitaryBed(inmate.CurrentBed()) || !PunishmentUtility.AnySolitaryBed(inmate.Map))) return false;
            if (!inmate.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) return false;
            return PunishmentUtility.FindPunishmentBed(inmate, pawn) != null && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            var inmate = (Pawn)t;
            Building_Bed bed = PunishmentUtility.FindPunishmentBed(inmate, pawn);
            if (bed == null) return null;
            // (The cell they go back to is noted when the bed is claimed, not here: this is also asked for by right-click menus and for jobs that never start.)
            Job job = JobMaker.MakeJob(JobDefOf.TakeWoundedPrisonerToBed, inmate, bed);
            job.count = 1;
            return job;
        }
    }

    /// <summary>
    /// Emergency: a downed, bleeding or injured inmate lying outside a bed is carried to a bed (a medical one if they
    /// need tending), so doctors can treat them. Punishment and execution transfers have their own work givers.
    /// </summary>
    public class WorkGiver_RescueInmate : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Rescue(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !(GuardUtility.IsGuard(pawn) || IsDoctor(pawn)) || (!forced && InmateCandidates.Shared.Rescue(pawn.Map).Count == 0);

        private static bool IsDoctor(Pawn p) => p.GetComp<CompStaff>()?.role is { } role && role.workTypes.Contains(WorkTypeDefOf.Doctor) && !HostageCrisis.IsHeld(p);

        /// <summary>The tests of <see cref="HasJobOnThing"/> that do not depend on the guard (down, out of bed, calm, in the prison's custody).</summary>
        public static bool Candidate(Pawn inmate) => inmate.Downed && !inmate.InBed() && !inmate.InAggroMentalState && inmate.Inmate() is { activeFlag: true } && !EvacuationSystem.Keeps(inmate);

        /// <summary>
        /// <see cref="Candidate"/> and the rest of <see cref="HasJobOnThing"/>'s tests that do not depend on the guard (in custody, not owing a restraint's punishment or an execution, in need of a bed or bleeding): the inmates who wait to
        /// be carried to a bed. The care reserve's queue reads it (see <see cref="CareDetail"/>).
        /// </summary>
        public static bool CareCandidate(Pawn inmate)
        {
            // Only inmates in custody (a released inmate carried to a bed would be imprisoned again).
            if (!Candidate(inmate)) return false;
            CompInmate c = inmate.Inmate();
            if (inmate.guest.Released || !inmate.guest.PrisonerIsSecure) return false;
            // Restrained offenders and the condemned have their own transfers; anyone else down (hunger, pain, a fight)
            // goes to bed even with punishment pending (they serve it once they are up).
            bool restrained = inmate.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained);
            if ((restrained && c.pendingPunishmentHours > 0) || DeathRowSystem.ExecutionDue(c)) return false;
            return HealthAIUtility.ShouldSeekMedicalRest(inmate) || inmate.health.hediffSet.BleedRateTotal > 0f;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is not Pawn inmate || !CareCandidate(inmate)) return false;
            // (The reservation and the way there first: they are the cheap tests, and a bed search walks the free beds and the prisoners.)
            if (!pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced)) return false;
            // A doctor who is not a guard does not walk into a riot or a brawl to fetch one (a downed member of staff is one more to carry, and the staff's deaths cost the prison's mood).
            if (!forced && !GuardUtility.IsGuard(pawn) && (RiotPolice.RiotOngoing(pawn.Map) || FightNear(inmate))) return false;
            return BedFor(inmate, pawn) != null;
        }

        /// <summary>Someone in an aggressive mental state within a few cells of the downed inmate.</summary>
        public static bool FightNear(Pawn inmate)
        {
            foreach (Thing t in GenRadial.RadialDistinctThingsAround(inmate.Position, inmate.Map, FightRadius, true))
                if (t is Pawn p && p != inmate && p.InAggroMentalState) return true;
            return false;
        }

        public const float FightRadius = 6f;

        /// <summary>A medical prisoner bed if they need tending and one is free, else their own bed, else a free cell
        /// bed of their wing (never solitary: the carried inmate claims the bed). Never a bed of a wing being evacuated for one of its evacuees (<see cref="EvacuationSystem.BedAllowed"/>).</summary>
        private static Building_Bed BedFor(Pawn inmate, Pawn carrier)
        {
            Building_Bed bed = RestUtility.FindBedFor(inmate, carrier, checkSocialProperness: false, ignoreOtherReservations: false, GuestStatus.Prisoner);
            if (bed != null && (bed.Medical || bed.OwnersForReading.Contains(inmate)) && EvacuationSystem.BedAllowed(inmate, bed)) return bed;
            Building_Bed own = inmate.ownership?.OwnedBed;
            if (own != null && own.Spawned && EvacuationSystem.BedAllowed(inmate, own) && carrier.CanReserveAndReach(own, PathEndMode.OnCell, Danger.Deadly)) return own;
            List<Building_Bed> free = IntakeSystem.FreePrisonerBeds(inmate.Map, inmate.Inmate()?.security, inmate.gender);
            for (int i = free.Count - 1; i >= 0; i--)
                if (EvacuationSystem.BedAllowed(inmate, free[i]) && carrier.CanReserveAndReach(free[i], PathEndMode.OnCell, Danger.Deadly)) return free[i];
            return null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            var inmate = (Pawn)t;
            Building_Bed bed = BedFor(inmate, pawn);
            if (bed == null) return null;
            Job job = JobMaker.MakeJob(JobDefOf.TakeWoundedPrisonerToBed, inmate, bed);
            job.count = 1;
            return job;
        }
    }

    /// <summary>Guards with nothing else to do walk among the inmates. Their presence is suppression.</summary>
    public class WorkGiver_Patrol : WorkGiver
    {
        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || pawn.Map.mapPawns.PrisonersOfColonySpawnedCount == 0;

        public override Job NonScanJob(Pawn pawn)
        {
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            if (pm == null) return null;
            Pawn focus = Focus(pawn, pm, out PrisonZoneDef deployed);
            IntVec3 dest = Destination(pawn, pm, focus, deployed);
            if (!dest.IsValid || !pawn.CanReach(dest, PathEndMode.OnCell, Danger.Some)) return null;
            Job job = JobMaker.MakeJob(RPSecurityDefOf.RP_Patrol, dest);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            return job;
        }

        /// <summary>Where the patrol walks: near the inmate it picked, else a cell of the deployed zone (the yard or the canteen when none); never a cell in a wing the guard kept off (see <see cref="Kin"/>). Invalid when there is none.</summary>
        internal static IntVec3 Destination(Pawn pawn, PrisonMap pm, Pawn focus, PrisonZoneDef deployed)
        {
            IntVec3 dest = IntVec3.Invalid;
            if (focus != null)
                CellFinder.TryFindRandomReachableNearbyCell(focus.Position, pawn.Map, 4f, TraverseParms.For(pawn), c => c.Standable(pawn.Map) && !Kin.KeepsOffCell(pawn, pawn.Map, c), null, out dest);
            if (!dest.IsValid)
            {
                PrisonZoneDef zone = deployed ?? (Rand.Bool ? RPDefOf.RP_Yard : RPDefOf.RP_Canteen);
                pm.zones.CellsOf(zone).Where(c => !Kin.KeepsOffCell(pawn, pawn.Map, c)).TryRandomElement(out dest);
            }
            return dest;
        }

        /// <summary>The inmate this guard's patrol goes to (the tense ones first, with a roll), and the zone they were deployed to (after the state's chief and a kin wing are allowed for); null when nobody is worth a walk.</summary>
        internal static Pawn Focus(Pawn pawn, PrisonMap pm, out PrisonZoneDef deployed)
        {
            // Go where the inmates are (and preferably the tense ones).
            Pawn focus = null;
            float bestScore = 0f;
            // (With the state's chief in post the patrols are theirs: wherever the inmates are, not the zones the warden set.)
            deployed = SpecialMeasures.DeployedZone(pawn);
            // (A guard who asked to be kept off the wing their kin is in does not walk it, and a patrol zone that has become that wing is no zone: see Kin.)
            List<SecurityLevelDef> keptOff = Kin.KeptOffWings(pawn);
            if (keptOff != null && deployed?.security != null && keptOff.Contains(deployed.security)) deployed = null;
            foreach (Pawn inmate in pawn.Map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!inmate.Awake() || inmate.Inmate() is not { } c || inmate.GetRoom() == InmateUtility.CellOf(inmate)) continue;
                if (deployed != null && pm.zones.ZoneAt(inmate.Position) != deployed) continue;
                if (keptOff != null && pm.zones.ZoneAt(inmate.Position)?.security is { } wing && keptOff.Contains(wing)) continue;
                float score = 10f + c.tension + Rand.Value * 30f;
                if (score > bestScore)
                {
                    bestScore = score;
                    focus = inmate;
                }
            }
            return focus;
        }
    }

    public class JobDriver_Patrol : JobDriver
    {
        private int nextVisitorCheckTick;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Patrol is the filler job: break off for a visitor waiting to be searched (only one this guard can take,
            // or the work scan hands out the next patrol straight away) or for a guard post to man. First look after two seconds: never a job storm.
            nextVisitorCheckTick = Find.TickManager.TicksGame + 120;
            AddEndCondition(() =>
            {
                int now = Find.TickManager.TicksGame;
                if (now < nextVisitorCheckTick) return JobCondition.Ongoing;
                nextVisitorCheckTick = now + 120;
                // (A guard post that wants this guard too: the post work giver would hand it to them, see GuardPosts.PostFor.)
                if (GuardPosts.PostFor(pawn) != null) return JobCondition.InterruptOptional;
                // (A count is called and a wing wants a guard: the patrol gives way, as it does for a post.)
                if (CountSystem.WantsGuard(pawn)) return JobCondition.InterruptOptional;
                // (A guard in the care reserve breaks off for the care that waits for them, see CareDetail.CareJobFor.)
                if (CareDetail.IsReserve(pawn) && CareDetail.CareJobFor(pawn) != null) return JobCondition.InterruptOptional;
                return VisitorSearch.UnclaimedVisitorWaiting(pawn.Map) && VisitorSearch.CanSearchAnyVisitor(pawn) ? JobCondition.InterruptOptional : JobCondition.Ongoing;
            });
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil watch = Toils_General.Wait(Rand.Range(300, 700));
            watch.handlingFacing = true;
            watch.tickIntervalAction = delta =>
            {
                if (pawn.IsHashIntervalTick(120, delta)) pawn.Rotation = Rot4.Random;
            };
            yield return watch;
        }
    }

    /// <summary>Walk up to an inmate and restrain them. Success depends on melee skill, backup and the inmate's state.</summary>
    public class JobDriver_Subdue : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !GuardUtility.NeedsSubduing(Inmate));
            Toil approach = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return approach;
            yield return Toils_Jump.JumpIf(approach, () => !pawn.Position.AdjacentTo8WayOrInside(Inmate.Position));
            Toil restrain = Toils_General.Wait(90);
            restrain.handlingFacing = true;
            restrain.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Inmate);
            restrain.WithProgressBarToilDelay(TargetIndex.A);
            yield return restrain;
            yield return Toils_Jump.JumpIf(approach, () => !pawn.Position.AdjacentTo8WayOrInside(Inmate.Position));
            Toil resolve = ToilMaker.MakeToil("Resolve");
            resolve.initAction = () => PunishmentUtility.ResolveSubdue(pawn, Inmate);
            yield return resolve;
        }
    }

    public static class PunishmentUtility
    {
        public static float SubdueChance(Pawn guard, Pawn inmate)
        {
            float chance = 0.55f;
            chance += 0.04f * ((guard.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 5) - (inmate.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 5));
            chance += inmate.InAggroMentalState ? -0.15f : 0.2f;
            if (inmate.Inmate() is { } c && c.HasReputation(ReputationDefOf.RP_Tough)) chance -= 0.2f;
            if (Unlocks.Feature("Tasers")) chance += 0.2f;
            chance += StaffMorale.SubdueOffset(guard);
            foreach (Pawn other in guard.Map.mapPawns.FreeColonistsSpawned)
                if (other != guard && !other.Downed && other.Position.AdjacentTo8Way(inmate.Position)) chance += 0.15f;
            chance = StaffMemories.AdjustSubdue(guard, inmate, Mathf.Clamp(chance, 0.1f, 0.95f));
            // A lead guard on site backs the others up (after the cap, kept under it).
            if (StaffCareers.SubdueBonus(guard) is > 0f and var backing) chance = Mathf.Min(0.95f, chance + backing);
            // Wary of this one: hesitates, whatever the odds (after the cap, so it shows even with tasers).
            return StaffWariness.WaryOf(guard.GetComp<CompStaff>(), inmate) ? chance * StaffWariness.SubdueFactor : chance;
        }

        public static void ResolveSubdue(Pawn guard, Pawn inmate, bool alwaysSucceeds = false)
        {
            if (alwaysSucceeds || Rand.Chance(SubdueChance(guard, inmate)))
            {
                CompInmate c = inmate.Inmate();
                bool brawlVictim = inmate.MentalStateDef == MentalStateDefOf.SocialFighting && c != null && !c.MisbehavedWithin(GenDate.TicksPerHour);
                bool wasAggro = inmate.InAggroMentalState;
                bool armed = inmate.equipment?.Primary != null && inmate.InMentalState && !brawlVictim;
                if (armed) StaffStories.Notify_SubduedArmed(guard, inmate);
                StaffWariness.Notify_Subdued(guard, inmate);
                StaffMemories.Note(c, guard, StaffMemories.Restrained);
                // Caught acting out: the policy for their latest offence (or a few hours for resisting staff), read before
                // the pat-down (contraband found on them is its own offence, sanctioned by its own policy) and before the
                // mental state ends. Someone who was jumped in a brawl is only separated.
                int hours = c != null && !brawlVictim ? c.PolicyHoursForLatest(c.ActingOut(inmate)) : 0;
                inmate.MentalState?.RecoverFromState();
                // Restraining includes a pat-down: whatever they carry is confiscated before they go down.
                ContrabandUtility.Search(guard, inmate, 1f);
                inmate.health.AddHediff(RPSecurityDefOf.RP_Restrained);
                guard.skills?.Learn(SkillDefOf.Melee, 200f);
                if (c != null && hours > c.pendingPunishmentHours) c.pendingPunishmentHours = hours;
                MoteMaker.ThrowText(inmate.DrawPos, inmate.Map, "RP_Restrained".Translate(), 3f);
                // More force than it took, or a liar's grievance about a restraint that was fine: a complaint may follow.
                ComplaintSystem.Notify_Restrained(guard, inmate, c, wasAggro);
            }
            else
            {
                MoteMaker.ThrowText(inmate.DrawPos, inmate.Map, "RP_Resists".Translate(), 3f);
                StaffMemories.Note(inmate.Inmate(), guard, StaffMemories.Resisted);
                if (!inmate.InMentalState && !inmate.WorkTagIsDisabled(WorkTags.Violent))
                    inmate.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Assaulting, null, forced: true, otherPawn: guard);
            }
        }

        /// <summary>A free solitary bed, else the inmate's own (or home) cell bed. Without a <paramref name="carrier"/> nobody is asked
        /// whether they can get there: is there a place at all?</summary>
        public static Building_Bed FindPunishmentBed(Pawn inmate, Pawn carrier)
        {
            PrisonMap pm = PrisonMap.Of(inmate.Map);
            if (pm != null)
            {
                foreach (Building_Bed bed in pm.PrisonerBeds)
                {
                    if (!bed.Spawned || pm.zones.ZoneAt(bed.Position) != RPSecurityDefOf.RP_SolitaryZone) continue;
                    if (bed.AnyUnoccupiedSleepingSlot && (bed.OwnersForReading.Count == 0 || bed.OwnersForReading.Contains(inmate)) && (carrier == null || carrier.CanReserveAndReach(bed, PathEndMode.OnCell, Danger.Deadly)))
                        return bed;
                }
            }
            Building_Bed home = inmate.Inmate()?.homeBed ?? inmate.ownership?.OwnedBed;
            if (home != null && home.Spawned && (carrier == null || carrier.CanReserveAndReach(home, PathEndMode.OnCell, Danger.Deadly))) return home;
            return null;
        }

        /// <summary>
        /// Restrained with a punishment due and no place to serve it (solitary full, no bed of their own to go back to): nobody can
        /// carry them anywhere, and the restraint would run out and have a guard subdue them again, every time with another
        /// pat-down and another grudge. They serve it where they are, in their cell, now.
        /// </summary>
        public static void ServeWhereNoBed(Pawn inmate, CompInmate c)
        {
            if (c.pendingPunishmentHours <= 0 || inmate.InBed() || !inmate.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained) || FindPunishmentBed(inmate, null) != null) return;
            Begin(inmate, c);
        }

        public static bool IsSolitaryBed(Building_Bed bed) => bed != null && PrisonMap.Of(bed.Map)?.zones.ZoneAt(bed.Position) == RPSecurityDefOf.RP_SolitaryZone;

        public static bool AnySolitaryBed(Map map)
        {
            foreach (Building_Bed bed in PrisonMap.Of(map).PrisonerBeds)
                if (IsSolitaryBed(bed)) return true;
            return false;
        }

        /// <summary>
        /// The home cell bed is only held for an inmate who is away from it (in the hole, or without a bed): one who owns their home bed,
        /// or has moved on to another cell bed since, has nothing to go back to and it must not stay marked as theirs (it would keep a
        /// bed "held" for no one, and send them back to an old wing).
        /// </summary>
        public static void SyncHomeBed(Pawn p, CompInmate c)
        {
            if (c?.homeBed != null && HeldHomeBed(p, c) == null) c.homeBed = null;
        }

        /// <summary>The cell bed that is held for them (<see cref="SyncHomeBed"/> says which that is), without changing anything: what a count of the free beds reads, which a screen may ask every frame.</summary>
        public static Building_Bed HeldHomeBed(Pawn p, CompInmate c)
        {
            if (c?.homeBed is not { } home) return null;
            Building_Bed own = p.ownership?.OwnedBed;
            return !home.Spawned || own == home || own != null && !IsSolitaryBed(own) ? null : home;
        }

        /// <summary>Taking a solitary bed leaves the cell bed behind: the one to go back to (what the job to take them there used to note in advance).</summary>
        public static void Notify_ClaimingBed(Pawn p, Building_Bed newBed)
        {
            if (newBed == null || p?.Inmate() is not { homeBed: null } c || p.ownership?.OwnedBed is not { } old || old == newBed) return;
            if (newBed.Spawned && IsSolitaryBed(newBed) && !IsSolitaryBed(old)) c.homeBed = old;
        }

        /// <summary>The hours the sentence now due runs: what was set, within the state's cap while special measures hold (so a sentence due before they began is capped too).</summary>
        public static int HoursToServe(CompInmate c) => Mathf.Max(1, SpecialMeasures.LimitSolitary(c.pendingPunishmentHours));

        /// <summary>Called when the restrained inmate lies in the punishment bed: the sentence starts.</summary>
        public static void Begin(Pawn inmate, CompInmate c)
        {
            int hours = HoursToServe(c);
            bool solitary = IsSolitaryBed(inmate.CurrentBed());
            // A confinement already running is never cut short by a new sentence: the longer of the two ends it.
            int until = Find.TickManager.TicksGame + hours * GenDate.TicksPerHour;
            if (c.forcedActivity != null && c.forcedUntilTick > until) until = c.forcedUntilTick;
            c.forcedActivity = solitary ? RPSecurityDefOf.RP_Solitary : RPDefOf.RP_Lockup;
            c.forcedUntilTick = until;
            c.pendingPunishmentHours = 0;
            c.tension = Mathf.Max(0f, c.tension - 30f);
            // Punishment suppresses: the hole far more than the cell.
            c.suppressed = Mathf.Min(100f, c.suppressed + hours * (solitary ? Suppression.SolitaryPerHour : Suppression.LockupPerHour));
            c.MarkLastRecordPunished();
            inmate.needs.mood?.thoughts.memories.TryGainMemory(RPSecurityDefOf.RP_Punished);
            Hediff h = inmate.health.hediffSet.GetFirstHediffOfDef(RPSecurityDefOf.RP_Restrained);
            if (h != null) inmate.health.RemoveHediff(h);
            PrisonGame.Instance.stats.punishments++;
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_Punished".Translate(inmate.LabelShort, c.forcedActivity.label, hours), inmate);
            Messages.Message("RP_PunishmentBegins".Translate(inmate.LabelShort, c.forcedActivity.label, hours), inmate, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>Punishment over: back to the home cell bed.</summary>
        public static void End(Pawn inmate, CompInmate c)
        {
            c.forcedActivity = null;
            c.forcedUntilTick = -1;
            Building_Bed home = c.homeBed;
            c.homeBed = null;
            if (home != null && home.Spawned && inmate.ownership.OwnedBed != home && (home.OwnersForReading.Count < home.SleepingSlotsCount))
                inmate.ownership.ClaimBedIfNonMedical(home);
            else if (IsSolitaryBed(inmate.ownership.OwnedBed))
                inmate.ownership.UnclaimBed();
            inmate.Map?.GetComponent<PrisonMap>()?.Notify_DoorsChanged();
        }
    }

    /// <summary>The cell an inmate leaves for the hole is noted the moment the solitary bed is claimed.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Pawn_Ownership), nameof(Pawn_Ownership.ClaimBedIfNonMedical))]
    public static class Patch_ClaimBed_NotesHome
    {
        public static void Prefix(Building_Bed newBed, Pawn ___pawn) => PunishmentUtility.Notify_ClaimingBed(___pawn, newBed);
    }
}
