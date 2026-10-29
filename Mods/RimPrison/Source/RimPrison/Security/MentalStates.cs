using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPMentalStateDefOf
    {
        public static MentalStateDef RP_Rioting;
        public static MentalStateDef RP_Assaulting;
        /// <summary>The same assault, on another inmate (no "attacked a member of staff" alarm).</summary>
        public static MentalStateDef RP_AttackingInmate;
        public static MentalStateDef RP_Vandalizing;
        public static MentalStateDef RP_Escaping;

        static RPMentalStateDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPMentalStateDefOf));
    }

    /// <summary>Rioting: hostile to staff and prison property.</summary>
    public class MentalState_Rioting : MentalState
    {
        /// <summary>Riots spread: every few seconds a rioter pulls in tense, unwatched inmates around them.</summary>
        public override void MentalStateTick(int delta)
        {
            // Hostage-takers hold out until the crisis ends (see HostageCrisis): the clock does not end it for them.
            if (!HostageCrisis.Holding(pawn)) base.MentalStateTick(delta);
            if (pawn.MapHeld != null) RiotPolice.Notify_Rioting(pawn.MapHeld);
            if (pawn.MentalState != this || !pawn.IsHashIntervalTick(250, delta) || pawn.Map == null) return;
            // (The riot's record: still on, and this rioter in it, seen or not. A few lookups, every 250 ticks.)
            RiotReview.Notify_Rioting(pawn);
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            if (pm == null) return;
            Room room = pawn.GetRoom();
            IReadOnlyList<Pawn> inmates = pawn.Map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn o = inmates[i];
                if (o == pawn || o.InMentalState || o.Downed || !o.Awake() || !o.Position.InHorDistOf(pawn.Position, 8f) || o.GetRoom() != room) continue;
                if (o.Inmate() is not { activeFlag: true } oc || oc.tension < MisconductWorker_Riot.JoinTension || Suppression.Held(oc)) continue;
                if (o.WorkTagIsDisabled(WorkTags.Violent) || MisconductWorker_Riot.RiotedRecently(oc)) continue;
                // The angriest join readily; the merely tense mostly hold back.
                if (!Rand.Chance((oc.tension >= 70f ? 0.3f : 0.12f) * Suppression.RiotFactor(oc)) || TensionUtility.Watched(o, pm)) continue;
                if (MisconductWorker_Riot.Join(o, oc, pm, "RP_RiotSpread".Translate()))
                {
                    PrisonGame.Instance.stats.riotJoined++;
                    RiotReview.Notify_Rioting(o, pawn);
                }
            }
        }

        // Never to the held hostage: vanilla's pather starts a melee attack on whoever hostile blocks a pawn's path, and a hostage stands in
        // the middle of the room the rioters wander in (it was how they beat one to death within seconds, outside the crisis' own harm).
        public override bool ForceHostileTo(Thing t) => t.Faction == Faction.OfPlayer && !(t is Pawn p && HostageCrisis.IsHeld(p));

        public override bool ForceHostileTo(Faction f) => f == Faction.OfPlayer;

        public override RandomSocialMode SocialModeMax() => RandomSocialMode.Off;
    }

    /// <summary>Attacking one staff member (<see cref="MentalState.causedByPawn"/>).</summary>
    public class MentalState_Assaulting : MentalState
    {
        public Pawn Target => causedByPawn;

        public override bool ForceHostileTo(Thing t) => t == Target && !HostageCrisis.IsHeld(Target);

        public override bool ForceHostileTo(Faction f) => false;

        public override void MentalStateTick(int delta)
        {
            base.MentalStateTick(delta);
            // (Whoever else was after a member of staff who has since been taken hostage leaves them to the crisis.)
            if (pawn.MentalState == this && pawn.IsHashIntervalTick(60, delta) && (Target == null || Target.Dead || Target.Downed || !Target.Spawned || Target.Map != pawn.Map || HostageCrisis.IsHeld(Target)))
                RecoverFromState();
        }

        public override RandomSocialMode SocialModeMax() => RandomSocialMode.Off;
    }

    /// <summary>Smashing one object.</summary>
    public class MentalState_Vandalizing : MentalState
    {
        public Thing target;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref target, "target");
        }

        public override void MentalStateTick(int delta)
        {
            base.MentalStateTick(delta);
            // Smashed up, not destroyed: once it is badly damaged they have made their point (workmen repair it).
            if (pawn.MentalState == this && pawn.IsHashIntervalTick(60, delta)
                && (target == null || target.Destroyed || !target.Spawned || VandalismUtility.Wrecked(target))) RecoverFromState();
        }

        public override RandomSocialMode SocialModeMax() => RandomSocialMode.Off;
    }

    public static class VandalismUtility
    {
        /// <summary>Inmates stop smashing a thing below this share of its hit points (so it can be repaired).</summary>
        public const float WreckedBelow = 0.3f;

        public static bool Wrecked(Thing t) => t.def.useHitPoints && t.HitPoints < t.MaxHitPoints * WreckedBelow;

        /// <summary>Worth smashing: not already wrecked.</summary>
        public static bool Smashable(Thing t) => t.def.useHitPoints && t.HitPoints >= t.MaxHitPoints * 0.5f;
    }

    /// <summary>Making a break for the map edge.</summary>
    public class MentalState_Escaping : MentalState
    {
        public override RandomSocialMode SocialModeMax() => RandomSocialMode.Off;
    }

    public class JobGiver_Riot : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!pawn.Awake() || pawn.WorkTagIsDisabled(WorkTags.Violent)) return null;
            Pawn staff = null;
            float best = 14f * 14f;
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            // Hostage-takers keep the room they hold: back to it if they strayed, and only whoever else is inside is theirs to fight (never the hostage).
            bool holding = HostageCrisis.Holding(pawn, out StoryArc crisis);
            if (holding && !HostageHold.InHeld(pawn.Map, pawn.Position) && HostageNote.Of(crisis).anchor is { IsValid: true } anchor && anchor.InBounds(pawn.Map)
                && HostageHold.BackSpot(pawn, anchor) is { IsValid: true } spot)
            {
                Job back = JobMaker.MakeJob(JobDefOf.Goto, spot);
                back.canBashDoors = true;
                back.expiryInterval = 300;
                return back;
            }
            foreach (Pawn s in pawn.Map.mapPawns.FreeColonistsSpawned)
            {
                float d = (s.Position - pawn.Position).LengthHorizontalSquared;
                if (s.Downed || d >= best || HostageCrisis.IsHeld(s) || holding && !HostageHold.InHeld(pawn.Map, s.Position)) continue;
                // Rioters fight staff inside the prison; they do not chase them out of the compound.
                if (pm != null && pm.zones.ZoneAt(s.Position) is not { inmateAccess: true, staffOnly: false }) continue;
                if (!pawn.CanReach(s, PathEndMode.Touch, Danger.Deadly)) continue;
                best = d;
                staff = s;
            }
            // The ringleader who gets at a guard may take them instead of fighting them (the room is held from then on).
            if (staff != null && !holding && HostageCrisis.TryStartFromFight(pawn, staff) != null) return null;
            if (staff != null)
            {
                Job attack = JobMaker.MakeJob(JobDefOf.AttackMelee, staff);
                attack.maxNumMeleeAttacks = 3;
                attack.expiryInterval = 400;
                attack.canBashDoors = true;
                return attack;
            }
            foreach (IntVec3 c in GenRadial.RadialCellsAround(pawn.Position, 6f, true))
            {
                if (!c.InBounds(pawn.Map)) continue;
                Building b = c.GetFirstBuilding(pawn.Map);
                if (b == null || b.Faction != Faction.OfPlayer || !b.def.useHitPoints || b.def.IsWall || !VandalismUtility.Smashable(b) || !pawn.CanReach(b, PathEndMode.Touch, Danger.Deadly)) continue;
                Job smash = JobMaker.MakeJob(JobDefOf.AttackMelee, b);
                smash.expiryInterval = 300;
                return smash;
            }
            return null;
        }
    }

    public class JobGiver_AssaultTarget : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.MentalState is not MentalState_Assaulting { Target: { Spawned: true, Downed: false } target } || HostageCrisis.IsHeld(target)) return null;
            if (!pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly)) return null;
            Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            job.killIncappedTarget = false;
            job.expiryInterval = 600;
            return job;
        }
    }

    public class JobGiver_Vandalize : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.MentalState is not MentalState_Vandalizing { target: { Spawned: true } target }) return null;
            Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            job.expiryInterval = 500;
            return job;
        }
    }

    public class JobGiver_EscapeAttempt : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            // A stowaway runs for the shuttle's ramp while it is down; otherwise (and after it has gone) the usual way out.
            // (A walk to the ramp with exitMapOnArrival never left the map, the ramp being no map edge: the same job again every tick.)
            if (ShuttleSystem.StowawayShuttle(pawn) is { } shuttle)
            {
                Job stow = JobMaker.MakeJob(RPShuttleDefOf.RP_BoardShuttle, shuttle);
                stow.canBashDoors = true;
                stow.locomotionUrgency = LocomotionUrgency.Sprint;
                stow.expiryInterval = 1000;
                stow.checkOverrideOnExpire = true;
                return stow;
            }
            if (!EscapeUtility.TryFindEscapeRoute(pawn, out IntVec3 exit))
            {
                pawn.MentalState?.RecoverFromState();
                return null;
            }
            Job job = JobMaker.MakeJob(JobDefOf.Goto, exit);
            job.exitMapOnArrival = true;
            job.canBashDoors = true;
            job.locomotionUrgency = LocomotionUrgency.Sprint;
            job.expiryInterval = 1000;
            job.checkOverrideOnExpire = true;
            return job;
        }
    }
}
