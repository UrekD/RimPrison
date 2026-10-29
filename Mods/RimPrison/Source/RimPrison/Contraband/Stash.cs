using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The cell stash: inmates hide contraband in their cell (under the mattress, behind the toilet) when nobody is
    /// watching, so a pat-down finds only what they carry. Cell searches and shakedowns find stashes; tools in the
    /// stash still dig tunnels, drugs in it are taken in the cell, dealers fetch stock from it. Weapons stay on them.
    /// </summary>
    public static class StashUtility
    {
        public const float FindChance = 0.85f;

        private static readonly List<Thing> tmp = new List<Thing>();

        /// <summary>Every 250 ticks while in their own cell: hide what can be hidden, fetch dealer stock.</summary>
        public static void Rare(Pawn p, CompInmate c, PrisonMap pm)
        {
            // (Mash works wherever they are.)
            HoochUtility.Ferment(c);
            if (p.inventory == null || p.Downed || p.InMentalState || p.GetRoom() != InmateUtility.CellOf(p)) return;
            tmp.Clear();
            foreach (Thing t in p.inventory.innerContainer)
                if (ContrabandUtility.Ext(t.def) is { kind: not ContrabandKind.Weapon }) tmp.Add(t);
            // A dealer keeps a little stock on them for the yard.
            bool dealer = c.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer));
            // (Nothing to hide, no stock to fetch and nobody to ring: whether anyone is watching changes nothing, so it is not asked.)
            if (tmp.Count == 0 && !dealer && c.familyCount <= 0) return;
            if (TensionUtility.Watched(p, pm)) return;
            int keepDrugs = TuckAway(p, c, tmp, dealer ? 2 : 0);
            if (dealer && keepDrugs > 0) Take(p, c, ContrabandKind.Drug, keepDrugs);
            PhoneCalls.TryCellphoneCall(p, c);
        }

        /// <summary>Puts the contraband in <paramref name="items"/> (from the pockets) into the stash, keeping <paramref name="keepDrugs"/> drugs on them; returns how many drugs they still want to carry.</summary>
        internal static int TuckAway(Pawn p, CompInmate c, List<Thing> items, int keepDrugs)
        {
            foreach (Thing t in items)
            {
                // (Not what they are about to use: an Ingest of a beer from the pockets found it in the stash and ended with vanilla's "Can't transfer item ... not here".)
                if (InUse(p, t)) continue;
                if (ContrabandUtility.Ext(t.def).kind == ContrabandKind.Drug && keepDrugs > 0)
                {
                    keepDrugs -= t.stackCount;
                    continue;
                }
                p.inventory.innerContainer.TryTransferToContainer(t, c.stash, t.stackCount);
            }
            return keepDrugs;
        }

        /// <summary>Whether the pawn's current or a queued job is on this thing (an item they are about to eat, drink or use from their pockets).</summary>
        public static bool InUse(Pawn p, Thing t)
        {
            if (p.CurJob is { } cur && Targets(cur, t)) return true;
            if (p.jobs?.jobQueue is { } queue)
                foreach (QueuedJob q in queue)
                    if (Targets(q.job, t)) return true;
            return false;
        }

        private static bool Targets(Job job, Thing t)
        {
            if (job == null) return false;
            if (job.targetA.Thing == t || job.targetB.Thing == t || job.targetC.Thing == t) return true;
            if (job.targetQueueA != null)
                foreach (LocalTargetInfo x in job.targetQueueA)
                    if (x.Thing == t) return true;
            return false;
        }

        /// <summary>Moves up to <paramref name="count"/> items of a kind from the stash to the pockets.</summary>
        public static Thing Take(Pawn p, CompInmate c, ContrabandKind kind, int count = 1)
        {
            foreach (Thing t in c.stash)
                if (ContrabandUtility.Ext(t.def)?.kind == kind)
                {
                    Thing taken = t.SplitOff(Mathf.Min(count, t.stackCount));
                    if (p.inventory.innerContainer.TryAdd(taken)) return taken;
                    c.stash.TryAdd(taken);
                    return null;
                }
            return null;
        }

        public static bool Has(CompInmate c, ContrabandKind kind)
        {
            foreach (Thing t in c.stash)
                if (ContrabandUtility.Ext(t.def)?.kind == kind) return true;
            return false;
        }

        /// <summary>Cell search: each hidden item is found with <see cref="FindChance"/> and confiscated.</summary>
        public static int Search(Pawn guard, Pawn owner, CompInmate c)
        {
            tmp.Clear();
            tmp.AddRange(c.stash);
            int found = 0;
            PrisonMap pm = PrisonMap.Of(owner.MapHeld);
            // The find is marked where it was hidden: by the bed the guard turned over, else where the guard stands.
            IntVec3 cell = owner.ownership?.OwnedBed is { Spawned: true } bed ? bed.Position : guard.PositionHeld;
            // A guard on the inmate's payroll turns the mattress over without looking under it.
            float chance = (CorruptionSystem.LooksAway(guard, owner) ? FindChance * 0.15f : FindChance) * StaffMorale.SearchFactor(guard);
            foreach (Thing t in tmp)
            {
                if (!Rand.Chance(chance)) continue;
                ContrabandKind kind = ContrabandUtility.Ext(t.def).kind;
                c.stash.Remove(t);
                ToolCount.Notify_Taken(t, ToolSource.Stash);
                t.Destroy();
                found++;
                pm?.AddFind(cell, kind, FindSource.Stash);
            }
            // The cell turned over, a gardener's seed and the way their hands are stained tell where their patch is (it is pulled up, and written up, on its own).
            int garden = GardenPatches.Search(guard, owner, c, chance * GardenPatches.SearchFindChance / FindChance);
            if (found == 0) return garden;
            StaffMemories.Note(c, guard, StaffMemories.CellSearched);
            PrisonGame.Instance.stats.contrabandFound += found;
            PrisonGame.Instance.stats.stashesFound++;
            PrisonGame.Instance.today.contrabandFound += found;
            MisconductUtility.Record(owner, c, PrisonMap.Of(owner.Map), RPContrabandDefOf.RP_ContrabandPossession, evidence: true);
            CorruptionSystem.Notify_ContrabandFound(owner, c, "stash");
            if (c.informedBy != null) InformantSystem.Notify_TipActedOn(owner, c);
            Messages.Message("RP_StashFound".Translate(guard.LabelShort, owner.LabelShort, found), owner, MessageTypeDefOf.PositiveEvent, historical: false);
            return found + garden;
        }
    }
}
