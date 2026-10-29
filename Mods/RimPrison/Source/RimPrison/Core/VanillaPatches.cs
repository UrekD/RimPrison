using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Vanilla only lets prisoners use social-propriety things (tables, chairs, joy objects, food sources) in their
    /// current room. Inmates may also use them in inmate zones (canteen, yard, ...), never in staff zones.
    /// </summary>
    [HarmonyPatch(typeof(SocialProperness), nameof(SocialProperness.IsSociallyProper), typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool))]
    public static class Patch_SocialProperness_IsSociallyProper
    {
        public static bool Prefix(Thing t, Pawn p, bool forPrisoner, ref bool __result)
        {
            if (!forPrisoner || p == null || !t.Spawned || !t.def.socialPropernessMatters) return true;
            if (p.GetComp<CompInmate>() is not { activeFlag: true }) return true;
            IntVec3 c = t.def.hasInteractionCell ? t.InteractionCell : t.Position;
            Room room = c.GetRoom(t.Map);
            if (room != null && room == p.GetRoom())
            {
                __result = true;
                return false;
            }
            PrisonZoneDef zone = PrisonMap.Of(t.Map)?.zones.ZoneAt(c);
            __result = zone != null && zone.inmateAccess && !zone.staffOnly;
            return false;
        }
    }

    /// <summary>Managed inmates never start vanilla prison breaks; riots and escapes come from misconduct.</summary>
    [HarmonyPatch(typeof(PrisonBreakUtility), nameof(PrisonBreakUtility.InitiatePrisonBreakMtbDays))]
    public static class Patch_PrisonBreakUtility_InitiatePrisonBreakMtbDays
    {
        public static void Postfix(Pawn pawn, ref float __result)
        {
            if (pawn.GetComp<CompInmate>() is { activeFlag: true }) __result = -1f;
        }
    }

    /// <summary>Covers vanilla's jailbreaker mental state too: managed inmates never join vanilla prison breaks.</summary>
    [HarmonyPatch(typeof(PrisonBreakUtility), nameof(PrisonBreakUtility.CanParticipateInPrisonBreak))]
    public static class Patch_PrisonBreakUtility_CanParticipateInPrisonBreak
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            if (__result && pawn.GetComp<CompInmate>() is { activeFlag: true }) __result = false;
        }
    }

    /// <summary>
    /// Staff at home are held by <see cref="PrisonGame.home"/>, off the map, where vanilla does not count them: when the last
    /// member of staff on site dies, resigns or is fired (the commute keeps just one on site at night) vanilla would start the game-over
    /// countdown until the next shift walks in. Staff at home still are the prison's colonists.
    /// </summary>
    [HarmonyPatch(typeof(GameEnder), nameof(GameEnder.CheckOrUpdateGameOver))]
    public static class Patch_GameEnder_CheckOrUpdateGameOver
    {
        public static bool Prefix(GameEnder __instance)
        {
            if (!StaffCommute.AnyAtHome()) return true;
            __instance.gameEnding = false;
            return false;
        }
    }

    /// <summary>
    /// Vanilla's cleanup of a "take to bed" job that failed on arrival (the bed no longer usable) kicks the sleeper out of that bed if they lie in one, without asking whether it is the same bed:
    /// a downed inmate already lying in their solitary bed, whom a guard was taking to another, was "kicked out" of the other ("Tried to kick pawn Sye out of a bed they're not currently in",
    /// an error, and their lying-in-bed posture cleared while they still lay there). A kick out of a bed the pawn is not in is skipped.
    /// </summary>
    [HarmonyPatch(typeof(RestUtility), nameof(RestUtility.KickOutOfBed))]
    public static class Patch_RestUtility_KickOutOfBed_OtherBed
    {
        public static bool Prefix(Pawn pawn, Building_Bed bed) => bed == null || pawn?.CurrentBed() is not { } current || current == bed;
    }

    /// <summary>A member of staff whose shift is over is given the walk home, not a sleep on the floor (<see cref="StaffCommute.InsteadOfTheFloor"/>).</summary>
    [HarmonyPatch(typeof(JobGiver_GetRest), "TryGiveJob")]
    public static class Patch_JobGiver_GetRest_StaffGoHome
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result?.def != JobDefOf.LayDown || __result.targetA.Thing is Building_Bed || StaffCommute.InsteadOfTheFloor(pawn) is not { } home) return;
            JobMaker.ReturnToPool(__result);
            __result = home;
        }
    }

    /// <summary>
    /// Vanilla's "Need colonist beds" counts the staff as colonists who need a bed: they go home at the end of their shift (<see cref="StaffCommute"/>), and the warden's cot is made for them
    /// (<see cref="WardenSystem.EnsureCot"/>), so in a prison the alert only told the warden to build beds nobody would sleep in.
    /// </summary>
    [HarmonyPatch(typeof(Alert_NeedColonistBeds), nameof(Alert_NeedColonistBeds.GetReport))]
    public static class Patch_Alert_NeedColonistBeds_GetReport
    {
        public static void Postfix(ref AlertReport __result)
        {
            if (PrisonGame.Instance != null) __result = AlertReport.Inactive;
        }
    }

    /// <summary>
    /// The state's security chief is not the warden's to command, and the warden on the floor is not the player's to send to a fight: no draft button, and no drafting by any other route (a
    /// drafted chief takes no work, so the warden could park them in a corner or walk them into a riot; a drafted warden kills an inmate in a brawl and the inquiry has nobody to suspend).
    /// </summary>
    [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.ShowDraftGizmo), MethodType.Getter)]
    public static class Patch_Pawn_DraftController_ShowDraftGizmo
    {
        public static void Postfix(Pawn_DraftController __instance, ref bool __result)
        {
            if (__result && StaffUtility.IsProtected(__instance.pawn)) __result = false;
        }
    }

    [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
    public static class Patch_Pawn_DraftController_Drafted
    {
        public static bool Prefix(Pawn_DraftController __instance, bool value) => !(value && StaffUtility.IsProtected(__instance.pawn));
    }

    /// <summary>No wildlife wandering into the prison grounds.</summary>
    [HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.SpawnRandomWildAnimalAt))]
    public static class Patch_WildAnimalSpawner_SpawnRandomWildAnimalAt
    {
        public static bool Prefix(ref bool __result)
        {
            if (PrisonGame.Instance == null) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Only staff carry inmates. Visitors and released inmates belong to the outside faction and would otherwise
    /// "rescue" a downed inmate of their faction and carry them off the map.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry), typeof(Thing), typeof(int), typeof(bool))]
    public static class Patch_Pawn_CarryTracker_TryStartCarry
    {
        public static bool Prefix(Pawn_CarryTracker __instance, Thing item, ref int __result)
        {
            if (item is not Pawn carried || !carried.IsPrisonerOfColony || PrisonGame.Instance == null) return true;
            Pawn carrier = __instance.pawn;
            if (carrier.Faction == Faction.OfPlayer && !carrier.IsPrisoner) return true;
            if (AutoTest.Active) RPLog.Message($"[RimPrison:test] blocked {carrier.LabelShort} ({carrier.Faction?.Name}, job {carrier.CurJobDef?.defName}) carrying inmate {carried.LabelShort}");
            __result = 0;
            return false;
        }
    }

    /// <summary>Staff do not throw parties or hold ceremonies on the job (they would pull guards off their posts).</summary>
    [HarmonyPatch(typeof(VoluntarilyJoinableLordsStarter), nameof(VoluntarilyJoinableLordsStarter.VoluntarilyJoinableLordsStarterTick))]
    public static class Patch_VoluntarilyJoinableLordsStarter_Tick
    {
        public static bool Prefix() => PrisonGame.Instance == null;
    }

    /// <summary>No persona core offers: the research everything-unlocked start counts as ship research.</summary>
    [HarmonyPatch(typeof(GameComponent_OnetimeNotification), nameof(GameComponent_OnetimeNotification.GameComponentTick))]
    public static class Patch_GameComponent_OnetimeNotification_Tick
    {
        public static void Prefix(GameComponent_OnetimeNotification __instance)
        {
            if (PrisonGame.Instance != null) __instance.sendAICoreRequestReminder = false;
        }
    }

    /// <summary>
    /// Wardens bring food to hungry prisoners in cells. With a canteen, meals happen there; wardens only feed inmates
    /// who are urgently hungry (locked down, in solitary, or the canteen ran dry).
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_Warden_DeliverFood), nameof(WorkGiver_Warden_DeliverFood.JobOnThing))]
    public static class Patch_WorkGiver_Warden_DeliverFood
    {
        public static bool Prefix(Thing t, ref Job __result)
        {
            if (t is not Pawn p || p.GetComp<CompInmate>() is not { activeFlag: true } c) return true;
            if (HungerStrike.Fasting(c))
            {
                __result = null;
                return false;
            }
            if (p.needs?.food == null || p.needs.food.CurCategory >= HungerCategory.UrgentlyHungry) return true;
            // Confined (punishment, lockdown): meals come to the cell, as soon as they are hungry.
            if (c.forcedActivity != null || PrisonGame.Instance.LockedDown(c) || c.security is { cellMeals: true }) return true;
            PrisonMap pm = PrisonMap.Of(p.Map);
            if (pm == null || !pm.zones.Any(RPDefOf.RP_Canteen)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>
    /// Vanilla in-bed joy (praying, relaxing) reserves the bed without checking: right after a guard puts a restrained
    /// inmate in bed and the restraint comes off, the guard may still hold the bed and the job errors out.
    /// </summary>
    [HarmonyPatch(typeof(JoyGiver_InPrivateRoom), nameof(JoyGiver_InPrivateRoom.TryGiveJobWhileInBed))]
    public static class Patch_JoyGiver_InPrivateRoom_InBed
    {
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            Building_Bed bed = pawn.CurrentBed();
            if (bed == null || pawn.CanReserve(bed, bed.SleepingSlotsCount, 0)) return true;
            __result = null;
            return false;
        }
    }

    /// <summary>
    /// A berserk inmate rages where they are: only someone in the same room is attacked (berserk jobs bash doors, so a
    /// target elsewhere, worst of all beyond the gate, took them through the perimeter); otherwise they lash out in place.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_Berserk), "TryGiveJob")]
    public static class Patch_JobGiver_Berserk_StayInside
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result?.def != JobDefOf.AttackMelee || !pawn.IsPrisonerOfColony || __result.targetA.Thing is not { Spawned: true } target) return;
            // Within their own room only: berserk jobs bash doors, and a target down the corridor or beyond the gate
            // led them through the perimeter.
            if (pawn.GetDistrict() is not { TouchesMapEdge: false } || target.GetRoom() == pawn.GetRoom()) return;
            Job wait = JobMaker.MakeJob(JobDefOf.Wait_Combat);
            wait.expiryInterval = 90;
            wait.canUseRangedWeapon = false;
            __result = wait;
        }
    }

    /// <summary>
    /// The insulting spree's job giver only asks whether the target can be reached, and reachability goes by where a pawn is held: a target being carried (a guard taking them to bed) passes, and the
    /// insult job fails at its first toil (the target is not spawned), is handed out again, ten times in the tick ("started 10 jobs in one tick"), until the mental state picks someone else on its own,
    /// slower clock. So no insult is given to a target who is not spawned, or whom the interaction would refuse (asleep); the spree's next node runs meanwhile.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_InsultingSpree), "TryGiveJob")]
    public static class Patch_JobGiver_InsultingSpree_CanInsult
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result?.targetA.Thing is not Pawn target) return;
            if (target.Spawned && SocialInteractionUtility.CanReceiveInteraction(target, InteractionDefOf.Insult) && SocialInteractionUtility.CanInitiateInteraction(pawn, InteractionDefOf.Insult)) return;
            JobMaker.ReturnToPool(__result);
            __result = null;
        }
    }

    /// <summary>
    /// Vanilla walks every prisoner "in restraints" at 35% speed (Pawn.TicksPerMove). Inmates walk to the canteen, the
    /// showers and work on the regime's clock, so at that speed a 100-cell walk takes over two hours and a big prison
    /// starves with food on the counters. So every active inmate (a prisoner of the colony on the home map who is not
    /// breaking out the vanilla way) walks at their normal speed, whatever they are doing, except:
    /// one in a mental state (an escape, a riot, a fight) keeps the slow pace, and so does one a guard has to restrain
    /// (<see cref="GuardUtility.NeedsSubduing"/>: owes punishment for a misconduct that was seen). The restraint takes 90 still
    /// ticks next to the inmate and starts over whenever they step away, so a guard needs a target slower than themself.
    /// (Vanilla prison breaks were never slow: <c>LordJob_PrisonBreak.NeverInRestraints</c>.)
    /// </summary>
    [HarmonyPatch(typeof(RestraintsUtility), nameof(RestraintsUtility.InRestraints))]
    public static class Patch_RestraintsUtility_InRestraints
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            if (__result && pawn.IsPrisonerOfColony && !pawn.InMentalState && InmateUtility.IsActiveInmate(pawn) && !GuardUtility.NeedsSubduing(pawn)) __result = false;
        }
    }

    /// <summary>
    /// Vanilla's walk or carry to a bed (<c>JobDriver_TakeToBed</c>: a guard's escort of an inmate with no bed, a carry of a wounded one) clears every claim the inmate holds as it starts
    /// (<c>Takee.ClearAllReservations</c>) and leaves them on the job those claims were for. An inmate on the way to a meal walked on to it holding nothing: a doctor feeding a patient took the meal as a
    /// free one, and the inmate's pick-up could not reserve it ("Could not reserve Thing_MealSimple ... for Smalls for job Ingest", census c84). An inmate on their feet whose job held a claim has the job
    /// ended with the claims (the guard is about to lead them off anyway); whatever they do next claims again.
    /// </summary>
    [HarmonyPatch(typeof(JobDriver_TakeToBed), nameof(JobDriver_TakeToBed.TryMakePreToilReservations))]
    public static class Patch_JobDriver_TakeToBed_TakeeLosesTheJobWithItsClaims
    {
        public static void Prefix(JobDriver_TakeToBed __instance, out Job __state)
        {
            __state = null;
            if (PrisonGame.Instance == null || __instance.job?.targetA.Thing is not Pawn takee || !takee.Spawned || takee.Downed || takee.CurJob is not { } job) return;
            if (HoldsClaimFor(takee, job)) __state = job;
        }

        public static void Postfix(JobDriver_TakeToBed __instance, Job __state)
        {
            if (__state != null && __instance.job?.targetA.Thing is Pawn takee && takee.Spawned && takee.CurJob == __state) takee.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        /// <summary>The pawn holds a reservation for this job of theirs.</summary>
        public static bool HoldsClaimFor(Pawn p, Job job)
        {
            var all = p.Map.reservationManager.ReservationsReadOnly;
            for (int i = 0; i < all.Count; i++)
                if (all[i].Claimant == p && all[i].Job == job) return true;
            return false;
        }
    }
}
