using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>A smokeleaf patch a gardener inmate has sown among the garden's crops: where, whose, since when. Hidden until it is found (never drawn, never listed).</summary>
    public class GardenPatch : IExposable
    {
        public IntVec3 cell;
        /// <summary>The gardener (thing id) who tends it.</summary>
        public int ownerId = -1;
        /// <summary>When it was sown, or last harvested (it ripens <see cref="GardenPatches.RipeTicks"/> after).</summary>
        public int sownTick;
        public int harvests;
        /// <summary>When the gardener last worked the garden (the patch is overgrown <see cref="GardenPatches.OvergrownTicks"/> after: a gardener moved to another job does not keep one).</summary>
        public int tendedTick = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Values.Look(ref ownerId, "owner", -1);
            Scribe_Values.Look(ref sownTick, "sown");
            Scribe_Values.Look(ref harvests, "harvests");
            Scribe_Values.Look(ref tendedTick, "tended", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && tendedTick < 0) tendedTick = sownTick;
        }

        public string Describe() => $"{cell.x},{cell.z}:{ownerId}:{sownTick}:{harvests}:{tendedTick}";
    }

    /// <summary>
    /// A smokeleaf patch in the prison garden. A gardener inmate who is inside for smokeleaf growing sows one among the crops when nobody is watching; ripe, it is harvested into joints
    /// in their pockets, and, if they carry for a dealer's ring, they are handed on as that ring's own supply (<see cref="RingSystem.Notify_BroughtIn"/>, with the garden as the source).
    /// The staff gardener and the guards in the garden may come across it, and a cell search of the gardener's cell finds where it is: it is pulled up and marked on the map of finds.
    /// </summary>
    public static class GardenPatches
    {
        public const int RipeTicks = 5 * GenDate.TicksPerDay, OvergrownTicks = 8 * GenDate.TicksPerDay;
        public const float SowChance = 0.15f, StaffFindChance = 0.05f, GardenerFindChance = 0.12f, SearchFindChance = 0.6f, FindRadius = 9f;

        public static bool GrowsContraband(CompInmate c) => c?.job is { growsContraband: true } && c.crime == RPContrabandDefOf.RP_Crime_SmokeleafGrowing;

        public static GardenPatch PatchOf(PrisonMap pm, Pawn owner)
        {
            if (pm == null) return null;
            for (int i = 0; i < pm.patches.Count; i++)
                if (pm.patches[i].ownerId == owner.thingIDNumber) return pm.patches[i];
            return null;
        }

        public static bool Owns(PrisonMap pm, Pawn p) => PatchOf(pm, p) != null;

        /// <summary>Hourly while a gardener works the garden: one with the smokeleaf crime sows a patch of their own, once, when nobody is watching. Returns whether they did.</summary>
        public static bool Notify_Gardening(Pawn p, CompInmate c, PrisonMap pm, bool force = false)
        {
            if (pm == null || !GrowsContraband(c)) return false;
            if (PatchOf(pm, p) is { } mine)
            {
                mine.tendedTick = Find.TickManager.TicksGame;
                return false;
            }
            if (!force && (TensionUtility.Watched(p, pm) || !Rand.Chance(SowChance))) return false;
            List<IntVec3> garden = pm.zones.CellsOf(RPLaborDefOf.RP_Garden);
            if (garden.Count == 0) return false;
            IntVec3 cell = IntVec3.Invalid;
            // Near where they work, among the crops.
            for (int i = 0; i < 20 && !cell.IsValid; i++)
            {
                IntVec3 c0 = garden.RandomElement();
                if (c0.InHorDistOf(p.Position, 12f)) cell = c0;
            }
            if (!cell.IsValid) cell = pm.zones.ZoneAt(p.Position) == RPLaborDefOf.RP_Garden ? p.Position : garden.RandomElement();
            pm.patches.Add(new GardenPatch { cell = cell, ownerId = p.thingIDNumber, sownTick = Find.TickManager.TicksGame, tendedTick = Find.TickManager.TicksGame });
            return true;
        }

        /// <summary>The gardener of a patch, if they are still here (an inmate of the map).</summary>
        private static Pawn OwnerOf(Map map, GardenPatch patch)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                if (p.thingIDNumber == patch.ownerId && !p.Dead) return p;
            return null;
        }

        /// <summary>Hourly: a ripe patch is harvested by its gardener (at their garden job, by the patch, unwatched), staff in the garden may find one, and a patch whose gardener is gone, or has not worked the garden for days, is overgrown.</summary>
        public static void Hourly(Map map, PrisonMap pm)
        {
            for (int i = pm.patches.Count - 1; i >= 0; i--)
            {
                GardenPatch patch = pm.patches[i];
                Pawn owner = OwnerOf(map, patch);
                if (owner?.Inmate() is not { } c || LeavingPrison(owner) || Find.TickManager.TicksGame - patch.tendedTick >= OvergrownTicks)
                {
                    pm.patches.RemoveAt(i);
                    continue;
                }
                if (StaffFind(map, pm, patch, owner, c)) continue;
                // (At the patch, not anywhere: a job that is work in the laundry is no harvest in the garden, and the watch that counts is the one over the patch.)
                if (Find.TickManager.TicksGame - patch.sownTick >= RipeTicks && owner.Spawned && !owner.Downed && !owner.InMentalState && c.job is { growsContraband: true } && LaborUtility.IsWorking(owner, c)
                    && owner.Position.InHorDistOf(patch.cell, FindRadius) && !TensionUtility.Watched(owner, pm))
                    Harvest(owner, c, patch);
            }
        }

        private static bool LeavingPrison(Pawn owner) => ShuttleSystem.OnTheWayOut(owner);

        /// <summary>The ripe patch is harvested into joints in the gardener's pockets; for a member of a ring that is the ring's supply, from the garden.</summary>
        public static bool Harvest(Pawn owner, CompInmate c, GardenPatch patch)
        {
            // (Made inside, not smuggled in: what the Statistics tab counts as smuggled must not rise by one every five days with nobody to bring it.)
            Thing joints = ContrabandUtility.Give(owner, RPContrabandDefOf.SmokeleafJoint, smuggled: false);
            if (joints == null) return false;
            patch.sownTick = Find.TickManager.TicksGame;
            patch.tendedTick = patch.sownTick;
            patch.harvests++;
            RingSystem.Notify_BroughtIn(owner, RingSystem.GardenSource, RPContrabandDefOf.SmokeleafJoint);
            return true;
        }

        /// <summary>Staff who work in the garden come across a patch: the staff gardener most of all, then a guard about the place. A guard on the gardener's payroll looks away.</summary>
        private static bool StaffFind(Map map, PrisonMap pm, GardenPatch patch, Pawn owner, CompInmate c)
        {
            foreach (Pawn s in map.mapPawns.FreeColonistsSpawned)
            {
                if (s.Downed || !s.Awake() || !s.Position.InHorDistOf(patch.cell, FindRadius) || s.GetComp<CompStaff>() is not { role: { } role }) continue;
                bool gardener = role == RPLaborDefOf.RP_Gardener;
                if (!gardener && !role.guardDuties) continue;
                float chance = gardener ? GardenerFindChance : StaffFindChance;
                if (!gardener && CorruptionSystem.LooksAway(s, owner)) chance *= 0.15f;
                if (!Rand.Chance(chance)) continue;
                Uproot(map, pm, patch, s, owner, c);
                return true;
            }
            return false;
        }

        /// <summary>A cell search of the gardener's cell finds the patch (where it is, from what their things say): it is pulled up. Returns 1 if it was.</summary>
        public static int Search(Pawn guard, Pawn owner, CompInmate c, float chance)
        {
            PrisonMap pm = PrisonMap.Of(owner.MapHeld);
            if (PatchOf(pm, owner) is not { } patch || !Rand.Chance(chance)) return 0;
            Uproot(owner.MapHeld, pm, patch, guard, owner, c);
            return 1;
        }

        /// <summary>The patch is found: pulled up, marked on the map of finds, written up for the gardener, and, if they are in a ring, a link of the ring found.</summary>
        public static void Uproot(Map map, PrisonMap pm, GardenPatch patch, Pawn finder, Pawn owner, CompInmate c)
        {
            pm.patches.Remove(patch);
            pm.AddFind(patch.cell, ContrabandKind.Drug, FindSource.Garden);
            PrisonGame.Instance.stats.contrabandFound++;
            PrisonGame.Instance.today.contrabandFound++;
            MisconductUtility.Record(owner, c, pm, RPContrabandDefOf.RP_ContrabandPossession, evidence: true);
            if (c.informedBy != null) InformantSystem.Notify_TipActedOn(owner, c);
            RingSystem.Notify_GardenFound(owner);
            Messages.Message("RP_GardenFound".Translate(finder.LabelShort, owner.LabelShort), new LookTargets(patch.cell, map), MessageTypeDefOf.PositiveEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_GardenFound".Translate(owner.LabelShort), owner);
        }

        /// <summary>A bust of the ring takes the garden out too: every patch of this gardener is pulled up, quietly (the ring's own line says what was taken).</summary>
        public static void Clear(Map map, int ownerId)
        {
            PrisonMap.Of(map)?.patches.RemoveAll(x => x.ownerId == ownerId);
        }

        public static string Describe(PrisonMap pm) => string.Join(",", pm.patches.Select(x => x.Describe()));
    }
}
