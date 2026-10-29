using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPBurialDefOf
    {
        public static PrisonZoneDef RP_BurialPlot;
        public static JobDef RP_BuryBody;

        static RPBurialDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPBurialDefOf));
    }

    /// <summary>
    /// The prison plot: a staff-only patch of bare ground where the workmen bury the dead nobody will send for (an inmate with no family in touch, a member of staff with none). Each body is carried
    /// there from the morgue, a vanilla grave is dug (put up, with the body in it: it carries the name on its marker like any other) and the chronicle says so. The families' dead are not buried: they go home
    /// on the state shuttle (<see cref="CondolenceSystem"/>). Without a plot nothing is buried and a body with no family goes with the rest.
    /// </summary>
    public static class Burial
    {
        /// <summary>The time a workman spends at the grave before the body is laid in it.</summary>
        public const int DigTicks = 360;

        public static bool HasPlot(PrisonMap pm) => pm.zones.Any(RPBurialDefOf.RP_BurialPlot);

        public static bool InPlot(Corpse c, PrisonMap pm) => c.Spawned && pm.zones.ZoneAt(c.Position) == RPBurialDefOf.RP_BurialPlot;

        /// <summary>A body (of the prison's own) the plot is for by nature: it has nobody to send for it, and there is a plot painted. Whether the plot can take it now is <see cref="Wanted"/>.</summary>
        public static bool Wants(Corpse c, PrisonMap pm) => Morgue.Ours(c) && HasPlot(pm) && !CondolenceSystem.HasFamily(c.InnerPawn);

        /// <summary>A body that goes in the plot rather than home: <see cref="Wants"/> it, and a grave can be dug now (see <see cref="Capacity"/>). A body the plot cannot take is not held for it: it goes with the shuttle.</summary>
        public static bool Wanted(Corpse c, PrisonMap pm) => Wants(c, pm) && Capacity(c.MapHeld, pm, 1) > 0;

        /// <summary>Only a workman buries (the guards carry the dead to the morgue).</summary>
        public static bool IsWorkman(Pawn p) => p.GetComp<CompStaff>() is { role: { defName: "RP_Workman" } } sc && StaffCareers.Available(sc);

        /// <summary>The cell a grave (1 x 2) stands on when its south end is on <paramref name="c"/>: both cells in the plot and inside the grounds (a plot that stands outside them takes no new grave: what stands outside works, nothing new is built there), bare ground the grave can be dug in with nothing built on it.</summary>
        public static bool Fits(Map map, PrisonMap pm, IntVec3 c)
        {
            foreach (IntVec3 x in GenAdj.OccupiedRect(c, Rot4.North, ThingDefOf.Grave.Size))
                if (!x.InBounds(map) || pm.zones.ZoneAt(x) != RPBurialDefOf.RP_BurialPlot || !PrisonGrounds.Allows(map, x) || !x.Standable(map) || x.GetFirstBuilding(map) != null || x.GetFirstThing<Corpse>(map) != null
                    || ThingDefOf.Grave.terrainAffordanceNeeded is { } needed && !x.GetAffordances(map).Contains(needed))
                    return false;
            return true;
        }

        /// <summary>A free grave site the hauler can reserve (any, with no hauler asked).</summary>
        public static IntVec3 FindSite(Map map, PrisonMap pm, Pawn hauler)
        {
            foreach (IntVec3 c in pm.zones.CellsOf(RPBurialDefOf.RP_BurialPlot))
            {
                if (!Fits(map, pm, c)) continue;
                if (hauler == null || hauler.CanReserveAndReach(c, PathEndMode.OnCell, Danger.Some)) return c;
            }
            return IntVec3.Invalid;
        }


        /// <summary>The workmen who are on site and on duty.</summary>
        private static List<Pawn> Workmen(Map map)
        {
            var list = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                if (IsWorkman(p)) list.Add(p);
            return list;
        }

        /// <summary>
        /// How many bodies could be buried now: graves that fit side by side (never one over another) on ground some workman on duty can walk to, at most <paramref name="most"/>. Nothing when the plot is full,
        /// has no soil, stands outside the grounds, is walled off, or there is no workman.
        /// </summary>
        public static int Capacity(Map map, PrisonMap pm, int most = int.MaxValue)
        {
            if (map == null || !HasPlot(pm)) return 0;
            List<Pawn> workmen = Workmen(map);
            if (workmen.Count == 0) return 0;
            var taken = new HashSet<IntVec3>();
            int n = 0;
            foreach (IntVec3 c in pm.zones.CellsOf(RPBurialDefOf.RP_BurialPlot))
            {
                if (taken.Contains(c) || !Fits(map, pm, c) || !workmen.Any(w => w.CanReach(c, PathEndMode.OnCell, Danger.Some))) continue;
                bool overlaps = false;
                foreach (IntVec3 x in GenAdj.OccupiedRect(c, Rot4.North, ThingDefOf.Grave.Size))
                    overlaps |= taken.Contains(x);
                if (overlaps) continue;
                foreach (IntVec3 x in GenAdj.OccupiedRect(c, Rot4.North, ThingDefOf.Grave.Size)) taken.Add(x);
                if (++n >= most) break;
            }
            return n;
        }

        /// <summary>
        /// Lays the body in a new grave at <paramref name="at"/> (or, if that is no longer free, at the next site): the grave, the line in the chronicle and the news that a former inmate's friend is dead.
        /// False when there is no room left, with nothing changed.
        /// </summary>
        public static bool TryBury(Pawn hauler, Corpse corpse, IntVec3 at)
        {
            Map map = hauler.Map;
            if (PrisonMap.Of(map) is not { } pm) return false;
            if (!at.IsValid || !Fits(map, pm, at)) at = FindSite(map, pm, hauler);
            if (!at.IsValid) return false;
            Pawn dead = corpse.InnerPawn;
            var grave = (Building_Grave)ThingMaker.MakeThing(ThingDefOf.Grave);
            grave.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(grave, at, map, Rot4.North);
            if (!grave.TryAcceptThing(corpse))
            {
                grave.Destroy();
                return false;
            }
            grave.Notify_HauledTo(hauler, corpse, 1);
            Note(PrisonGame.Instance, dead);
            Funerals.Notify_Buried(dead);
            return true;
        }

        private static void Note(PrisonGame game, Pawn dead)
        {
            string name = dead.LabelShort;
            game.stats.buried++;
            game.story.bitterDeaths.Remove(dead.thingIDNumber);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 1, "RP_Chr_Buried".Translate(name), dead);
            // Somebody who did their time with them and has news still to come hears of it in it.
            foreach (FormerInmate e in game.story.formerInmates)
                if (e.newsTick >= 0 && e.friendIds.Contains(dead.thingIDNumber)) e.graveName = name;
        }
    }

    /// <summary>Carries a body from the morgue to the plot and buries it (a workman; only when there is a plot with room).</summary>
    public class WorkGiver_BuryBody : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.Corpse);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !Burial.IsWorkman(pawn) || PrisonMap.Of(pawn.Map) is not { } pm || !Burial.HasPlot(pm);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is not Corpse c || !c.Spawned || PrisonMap.Of(pawn.Map) is not { } pm || !Burial.Wants(c, pm) || !(Morgue.InMorgue(c, pm) || Burial.InPlot(c, pm)) || c.IsForbidden(pawn) || !Funerals.MayBury(c)) return false;
            return pawn.CanReserveAndReach(c, PathEndMode.ClosestTouch, Danger.Some, 1, -1, null, forced) && Burial.FindSite(pawn.Map, pm, pawn).IsValid;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            // (A funeral that is on is dug at the site it stands round.)
            IntVec3 site = t is Corpse body && Funerals.SiteFor(body, pawn.Map, PrisonMap.Of(pawn.Map)) is { IsValid: true } held && pawn.CanReserveAndReach(held, PathEndMode.OnCell, Danger.Some) ? held : Burial.FindSite(pawn.Map, PrisonMap.Of(pawn.Map), pawn);
            if (!site.IsValid) return null;
            Job job = JobMaker.MakeJob(RPBurialDefOf.RP_BuryBody, t, site);
            job.count = 1;
            return job;
        }
    }

    public class JobDriver_BuryBody : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed) && pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnForbidden(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnSomeonePhysicallyInteracting(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return Toils_Haul.CarryHauledThingToCell(TargetIndex.B);
            yield return Toils_General.Wait(Burial.DigTicks).WithProgressBarToilDelay(TargetIndex.B);
            Toil bury = ToilMaker.MakeToil("BuryBody");
            bury.initAction = () =>
            {
                if (pawn.carryTracker.CarriedThing is not Corpse corpse || !Burial.TryBury(pawn, corpse, job.targetB.Cell)) EndJobWith(JobCondition.Incompletable);
            };
            bury.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return bury;
        }
    }
}

namespace RimPrison
{
    /// <summary>
    /// Nothing wild grows in the burial plot: it is bare soil kept for graves, and vanilla's wild plant spawner seeds any fertile cell, so a tree came up in it during play and took a grave's room
    /// (a census run: a willow at 17% two days in, on a plot the builder had cleared).
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.CheckSpawnWildPlantAt))]
    public static class Patch_WildPlantSpawner_NotInThePlot
    {
        public static bool Prefix(IntVec3 c, Map ___map, ref bool __result)
        {
            if (PrisonGame.Instance == null || PrisonMap.Of(___map) is not { } pm || pm.zones.ZoneAt(c) != RPBurialDefOf.RP_BurialPlot) return true;
            __result = false;
            return false;
        }
    }
}
