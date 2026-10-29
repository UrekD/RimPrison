using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    // Stories: the stowaway, what the cargo crew slips in, and the guards on pad duty.
    public partial class ShuttleSystem
    {
        /// <summary>The tests' switch: the stowaway roll always comes up, and this inmate is the one who runs.</summary>
        public static Pawn ForceStowawayOnlyFor;

        // ---- the stories ----------------------------------------------------------------------------------------

        /// <summary>A guard on pad duty is standing by the shuttle.</summary>
        public static bool GuardOnPad(Map map, Building_StateShuttle shuttle)
        {
            List<Pawn> guards = PrisonMap.Of(map)?.Guards;
            if (guards == null) return false;
            for (int i = 0; i < guards.Count; i++)
                if (guards[i].CurJobDef == RPShuttleDefOf.RP_PadDuty && guards[i].Position.InHorDistOf(shuttle.Position, 9f)) return true;
            return false;
        }

        public static bool Guarded(ShuttleVisit v) => v != null && Now - v.lastGuardTick <= 300;

        /// <summary>An inmate in reach of the pad runs for the shuttle once the guards have had time to take their posts (see <see cref="StowawayDelayTicks"/>); whether a guard stops them is decided when they reach the ramp.</summary>
        private void RollStowaway(PrisonGame game, Map map, ShuttleVisit v)
        {
            v.stowawayRolled = true;
            if (ForceStowawayOnlyFor == null && !Rand.Chance(StowawayChance)) return;
            TryStowaway(game, map, v, ForceStowawayOnlyFor);
        }

        /// <summary>Starts a stowaway attempt now (also the tests' hook): returns the inmate who tried, null when nobody could. They run for the ramp as an escape attempt: a guard on pad duty there stops them (<see cref="Board"/>).</summary>
        public Pawn TryStowaway(PrisonGame game, Map map, ShuttleVisit v, Pawn only = null)
        {
            IntVec3 target = v.shuttle.InteractionCell;
            var candidates = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (only != null && p != only) continue;
                if (p.Inmate() is not { activeFlag: true, processed: true } c || p.Downed || !p.Awake() || p.InMentalState || p.Position.DistanceTo(target) > StowawayRadius) continue;
                // (Not the condemned, who are watched, nor anyone a guard has by the arm.)
                if (IsArrival(p) || c.pendingPunishmentHours > 0 || c.forcedActivity != null || DeathRowSystem.Ext(c) != null || ParoleScene.IsEscorted(p)) continue;
                // (As the escape route counts them: doors are passable, they get bashed; walls stop it.)
                if (!p.CanReach(target, PathEndMode.OnCell, Danger.Deadly, canBashDoors: true, canBashFences: false, TraverseMode.PassDoors)) continue;
                candidates.Add(p);
            }
            if (candidates.Count == 0) return null;
            Pawn who = candidates.RandomElement();
            stowaway = who;
            game.stats.stowawayAttempts++;
            // (Seen running, they are spotted at once; with nothing that watches in sight of them the warden is not told, and the count may be the first to know.)
            CountSystem.StartEscaping(who, "RP_StowawayReason".Translate(), EscapeWay.Stowaway);
            if (!CountSystem.IsHidden(who)) Messages.Message("RP_StowawaySpotted".Translate(who.LabelShort), who, MessageTypeDefOf.ThreatSmall);
            return who;
        }

        /// <summary>Where the stowaway runs: the shuttle's ramp while it is down and open (null: the usual escape route).</summary>
        public static IntVec3? StowawayGoal(Pawn p)
        {
            ShuttleSystem s = Sys;
            if (s?.stowaway != p) return null;
            ShuttleVisit v = s.visit;
            // (While boarding is open and the ramp can be reached: a board job that cannot start ended at once and was given again, every tick.)
            if (v is not { phase: ShuttlePhase.Landed } || v.shuttle is not { Spawned: true } sh || sh.Map != p.Map || !BoardingOpen(sh)
                || !p.CanReach(sh.InteractionCell, PathEndMode.OnCell, Danger.Deadly, canBashDoors: true, canBashFences: false, TraverseMode.PassDoors))
            {
                s.stowaway = null;
                return null;
            }
            return sh.InteractionCell;
        }

        /// <summary>The shuttle a stowaway runs for, while it is down (see <see cref="StowawayGoal"/>): they board it as the released do.</summary>
        public static Building_StateShuttle StowawayShuttle(Pawn p) => StowawayGoal(p) != null ? Sys?.visit?.shuttle : null;

        /// <summary>The stowaway has reached the ramp: a guard on pad duty stops them there (an escape attempt, seen), else they go aboard.</summary>
        private bool StowawayAtRamp(Pawn who, Building_StateShuttle shuttle)
        {
            PrisonGame game = PrisonGame.Instance;
            Map map = shuttle.Map;
            CompInmate wc = who.Inmate();
            PrisonMap pm = PrisonMap.Of(map);
            bool stopped = Guarded(visit) || GuardOnPad(map, shuttle);
            if (wc != null) MisconductUtility.Record(who, wc, pm, RPShuttleDefOf.RP_EscapeAttempt, evidence: stopped, chronicle: false);
            if (!stopped) return false;
            stowaway = null;
            // (A guard has them: whatever nobody saw before, this is seen.)
            CountSystem.Forget(who);
            game.stats.stowawaysCaught++;
            who.MentalState?.RecoverFromState();
            Messages.Message("RP_StowawayCaught".Translate(who.LabelShort), who, MessageTypeDefOf.NeutralEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_StowawayCaught".Translate(who.LabelShort), who);
            return true;
        }

        /// <summary>The stowaway made it aboard (they have left the map: an escape, with a line to say how).</summary>
        /// <returns>Whether this was the stowaway (their escape has its own line, see <see cref="AnnounceStowawayAway"/>: the escape's generic line is not written as well).</returns>
        public static bool Notify_StowawayAboard(Pawn p)
        {
            ShuttleSystem s = Sys;
            if (s?.stowaway != p) return false;
            s.stowaway = null;
            PrisonGame.Instance.stats.stowawaysAway++;
            return true;
        }

        /// <summary>The stowaway's escape is told (the chronicle line, with how they did it, and the message): when they went aboard if somebody saw it, else when the count found them gone.</summary>
        public static void AnnounceStowawayAway(Pawn p)
        {
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 3, "RP_Chr_StowawayAway".Translate(p.LabelShort), p);
            Messages.Message("RP_StowawayAway".Translate(p.LabelShort), MessageTypeDefOf.NegativeEvent);
        }

        /// <summary>
        /// Something in the crates: the cargo crew is the outside supplier of a smuggling ring, or of a dealer's. A guard on the pad searches the crates
        /// (and finds it, mostly); with nobody there it reaches whoever it was for, unfound, and the ring has the supplier it did not before.
        /// </summary>
        private void CargoContraband(PrisonGame game, Map map, ShuttleVisit v)
        {
            if (!Rand.Chance(CargoContrabandChance)) return;
            TryCargoContraband(game, map, v, force: false);
        }

        /// <summary>Slips something into the cargo for a ring member or dealer now (also the tests' hook): true if a package was put in.</summary>
        public bool TryCargoContraband(PrisonGame game, Map map, ShuttleVisit v, bool force, float? roll = null, Pawn only = null)
        {
            var pool = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } && RingSystem.LinkOf(p, out _) != null && !RingSystem.SupplyCut(p)) pool.Add(p);
            // (Whoever was taken out of a ring lately is cut off from every way in, the cargo included: see RingSystem.SupplyCut.)
            if (pool.Count == 0)
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate() is { activeFlag: true } c && RingSystem.IsDealer(c) && !RingSystem.SupplyCut(p)) pool.Add(p);
            if (pool.Count == 0) return false;
            Pawn inmate = only != null && pool.Contains(only) ? only : pool.RandomElement();
            ThingDef item = ContrabandUtility.PickSmuggled(inmate, ContrabandKind.Drug) ?? ContrabandUtility.PickSmuggled(inmate);
            if (item == null) return false;
            string crew = "RP_CargoCrewName".Translate();
            float dice = roll ?? Rand.Value;
            if (Guarded(v) && dice < CargoSearchChance)
            {
                game.stats.cargoContrabandFound++;
                game.today.contrabandFound++;
                Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(s => s.CurJobDef == RPShuttleDefOf.RP_PadDuty);
                PrisonMap.Of(map)?.AddFind(v.shuttle.InteractionCell, ContrabandUtility.Ext(item).kind, FindSource.Cargo);
                Messages.Message("RP_CargoContrabandFound".Translate(guard?.LabelShort ?? "RP_ShuttlePadGuard".Translate().ToString(), item.label), v.shuttle, MessageTypeDefOf.PositiveEvent);
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_CargoContrabandFound".Translate(guard?.LabelShort ?? "RP_ShuttlePadGuard".Translate().ToString(), item.label), guard);
                RingSystem.Notify_FoundOnWay(inmate, crew, "cargo");
                return true;
            }
            if (ContrabandUtility.Give(inmate, item, forDebt: true) != null)
            {
                // (Kept off the statistics screen: a count of what got past the guards would tell the warden that something did.)
                cargoContrabandMissed++;
                RingSystem.Notify_BroughtIn(inmate, crew, item);
            }
            return true;
        }

        // ---- pad duty -----------------------------------------------------------------------------------------

        /// <summary>Fewer guards are on pad duty than Deployment asks for (not counting this one).</summary>
        public static bool PadPostFree(Pawn guard, Building_StateShuttle shuttle)
        {
            int n = 0;
            List<Pawn> guards = PrisonMap.Of(shuttle.Map)?.Guards;
            if (guards != null)
                for (int i = 0; i < guards.Count; i++)
                    if (guards[i] != guard && guards[i].CurJobDef == RPShuttleDefOf.RP_PadDuty) n++;
            return n < (Sys?.padGuards ?? 0);
        }

        /// <summary>Where a guard on pad duty stands: beside the ramp, one side or the other by their number, clear of the hull and of the way down.</summary>
        public static IntVec3 PostCell(Pawn guard, Building_StateShuttle shuttle)
        {
            Map map = shuttle.Map;
            IntVec3 ramp = shuttle.InteractionCell;
            int side = guard.thingIDNumber % 2 == 0 ? 1 : -1;
            for (int d = 3; d >= 1; d--)
            {
                IntVec3 c = ramp + new IntVec3(side * d, 0, 0);
                if (c.InBounds(map) && c.Standable(map) && !shuttle.OccupiedRect().Contains(c) && guard.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) return c;
            }
            return CellFinder.TryFindRandomCellNear(ramp, map, 4, c => c.Standable(map) && !shuttle.OccupiedRect().Contains(c) && c != ramp && guard.CanReach(c, PathEndMode.OnCell, Danger.Deadly), out IntVec3 near) ? near : IntVec3.Invalid;
        }
    }
}
