using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>A prison job inmates can hold (workshop, cleaning...). Behavior lives in the worker.</summary>
    public class InmateJobDef : Def
    {
        public Type workerClass = typeof(InmateJobWorker);
        /// <summary>Security levels allowed to hold the job (empty = all).</summary>
        public List<SecurityLevelDef> allowedSecurity = new List<SecurityLevelDef>();
        /// <summary>Inmates above this tension are not given the job.</summary>
        public float maxTension = 60f;
        /// <summary>Tension change per hour while working (negative calms).</summary>
        public float tensionPerHour = -3f;
        /// <summary>Inmate dollars earned per hour worked (paid by the prison at midnight).</summary>
        public int wagePerHour = 2;
        public SkillDef skill;
        /// <summary>Program the inmate must have passed to hold the job (e.g. workshop induction).</summary>
        public ProgramDef requiredProgram;
        /// <summary>Where job holders wait when there is nothing to do right now.</summary>
        public PrisonZoneDef zone;
        /// <summary>Working this job can put contraband in an unwatched inmate's pocket (checked hourly).</summary>
        public bool toolTheft;
        /// <summary>What goes missing: tools (workshop, garden) or knives (kitchen).</summary>
        public ContrabandKind theftKind = ContrabandKind.Tool;
        /// <summary>Working this job can put kitchen fruit in an unwatched inmate's cell stash as hooch mash (checked hourly, see <see cref="HoochUtility"/>).</summary>
        public bool stealsFood;
        /// <summary>An inmate in this job who is inside for smokeleaf growing may sow a patch of it in the garden (checked hourly, see <see cref="GardenPatches"/>).</summary>
        public bool growsContraband;
        public int uiOrder;

        [Unsaved] private InmateJobWorker workerInt;

        public InmateJobWorker Worker
        {
            get
            {
                if (workerInt == null)
                {
                    workerInt = (InmateJobWorker)Activator.CreateInstance(workerClass);
                    workerInt.def = this;
                }
                return workerInt;
            }
        }
    }

    public class InmateJobWorker
    {
        public InmateJobDef def;

        /// <summary>How many inmates the prison can employ in this job right now.</summary>
        public virtual int Capacity(Map map) => 0;

        public virtual Job TryGiveJob(Pawn inmate, PrisonMap pm) => null;

        /// <summary>This inmate has something to do in the job right now, so a class does not take their work hour (most jobs are always there to be done: false).</summary>
        public virtual bool HasWork(Pawn inmate) => false;
    }

    /// <summary>A production bench run by inmates: input material in, product out.</summary>
    public class CompProperties_Workstation : CompProperties
    {
        public ThingDef input;
        public int inputCount = 10;
        public ThingDef product;
        public int productCount = 5;
        public int workTicks = 2000;
        public SkillDef skill;
        /// <summary>Laundry machines: clean uniforms per cycle (no input, no product).</summary>
        public int laundry;
        /// <summary>Tailor benches: new uniforms per cycle (no input item, no product: the stock gets them, see <see cref="Tailoring"/>).</summary>
        public int uniforms;

        public CompProperties_Workstation() => compClass = typeof(CompWorkstation);
    }

    public class CompWorkstation : ThingComp
    {
        public CompProperties_Workstation Props => (CompProperties_Workstation)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            PrisonMap.Of(parent.Map)?.RegisterWorkstation(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            PrisonMap.Of(map)?.DeregisterWorkstation(this);
        }

        public bool Usable => parent.Spawned && !parent.IsBrokenDown() && (parent.TryGetComp<CompPowerTrader>() is not { } power || power.PowerOn);
    }

    /// <summary>Workshop: fetch material from the workshop, work it at a bench, leave the product by the bench.</summary>
    public class InmateJobWorker_Workshop : InmateJobWorker
    {
        public override int Capacity(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return 0;
            int n = 0;
            foreach (CompWorkstation w in pm.Workstations)
                if (w.Usable && w.Props.input != null && pm.zones.ZoneAt(w.parent.Position) == RPLaborDefOf.RP_Workshop) n++;
            return n;
        }

        public override Job TryGiveJob(Pawn inmate, PrisonMap pm)
        {
            foreach (CompWorkstation w in pm.Workstations)
            {
                if (!w.Usable || w.Props.input == null || pm.zones.ZoneAt(w.parent.Position) != RPLaborDefOf.RP_Workshop) continue;
                if (!inmate.CanReserveAndReach(w.parent, PathEndMode.InteractionCell, Danger.Some)) continue;
                Thing input = GenClosest.ClosestThingReachable(w.parent.Position, inmate.Map, ThingRequest.ForDef(w.Props.input), PathEndMode.ClosestTouch,
                    TraverseParms.For(inmate), 30f, t => pm.zones.ZoneAt(t.Position) == RPLaborDefOf.RP_Workshop && t.stackCount >= w.Props.inputCount && inmate.CanReserve(t));
                if (input == null) continue;
                Job job = JobMaker.MakeJob(RPLaborDefOf.RP_InmateProduce, w.parent, input);
                job.count = w.Props.inputCount;
                return job;
            }
            return null;
        }
    }

    /// <summary>Laundry: run the washing machines in the laundry; every cycle adds clean uniforms to the stock.</summary>
    public class InmateJobWorker_Laundry : InmateJobWorker
    {
        private static bool IsLaundry(CompWorkstation w, PrisonMap pm) =>
            w.Usable && w.Props.laundry > 0 && pm.zones.ZoneAt(w.parent.Position) == RPLaborDefOf.RP_Laundry;

        public override int Capacity(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return 0;
            int n = 0;
            foreach (CompWorkstation w in pm.Workstations)
                if (IsLaundry(w, pm)) n++;
            return n;
        }

        /// <summary>Clean uniforms short of two days' worth: there is washing to do, so a class does not take the laundry worker's work hours (a worker always in class left
        /// the laundry undone, every uniform washed outside at a price).</summary>
        public override bool HasWork(Pawn inmate) => PrisonGame.Instance.laundry.stock < LaundrySystem.Goal(inmate.Map);

        public override Job TryGiveJob(Pawn inmate, PrisonMap pm)
        {
            // Enough clean uniforms for two days: the shift folds and waits.
            if (!HasWork(inmate)) return null;
            foreach (CompWorkstation w in pm.Workstations)
                if (IsLaundry(w, pm) && inmate.CanReserveAndReach(w.parent, PathEndMode.InteractionCell, Danger.Some))
                    return JobMaker.MakeJob(RPLaborDefOf.RP_DoLaundry, w.parent);
            return null;
        }
    }

    public class JobDriver_DoLaundry : JobDriver
    {
        private CompWorkstation Station => job.targetA.Thing?.TryGetComp<CompWorkstation>();

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => Station is not { Usable: true });
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            Toil work = Toils_General.Wait(Station?.Props.workTicks ?? 1500);
            work.handlingFacing = true;
            work.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(job.targetA);
            work.WithProgressBarToilDelay(TargetIndex.A);
            yield return work;
            Toil finish = ToilMaker.MakeToil("LaundryDone");
            finish.initAction = () =>
            {
                if (Station is { } st) PrisonGame.Instance.laundry.Add(st.Props.laundry);
            };
            yield return finish;
        }
    }

    /// <summary>
    /// Gardening: sow and harvest the growing zones inside the garden (vanilla grower work givers, run for the inmate).
    /// The crop goes to the kitchen; hoes and trowels have a way of disappearing.
    /// </summary>
    public class InmateJobWorker_Garden : InmateJobWorker
    {
        private static WorkGiver_Scanner sow, harvest;

        private static WorkGiver_Scanner Sow => sow ??= (WorkGiver_Scanner)RPVanillaDefOf.GrowerSow.Worker;
        private static WorkGiver_Scanner Harvest => harvest ??= (WorkGiver_Scanner)RPVanillaDefOf.GrowerHarvest.Worker;

        /// <summary>One gardener per 25 cells of growing zone inside the garden.</summary>
        public override int Capacity(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return 0;
            int n = 0;
            foreach (IntVec3 c in pm.zones.CellsOf(RPLaborDefOf.RP_Garden))
                if (c.GetZone(map) is Zone_Growing) n++;
            return n == 0 ? 0 : Mathf.Max(1, n / 25);
        }

        /// <summary>Vanilla keeps the crop being worked on in a shared static, normally set per zone by its scan; we
        /// call the work givers directly, so set it for the cell's zone and clear it afterwards.</summary>
        private static readonly AccessTools.FieldRef<ThingDef> WantedPlant = AccessTools.StaticFieldRefAccess<ThingDef>(AccessTools.Field(typeof(WorkGiver_Grower), "wantedPlantDef"));

        public override Job TryGiveJob(Pawn inmate, PrisonMap pm)
        {
            // Harvest first, then sow.
            return GrowerJob(inmate, pm, RPLaborDefOf.RP_Garden, sowing: false) ?? GrowerJob(inmate, pm, RPLaborDefOf.RP_Garden, sowing: true);
        }

        /// <summary>
        /// The vanilla grower work for a growing zone inside a prison zone (the garden's, or the outside work zone's): a harvest (<paramref name="sowing"/> false) or what a sow asks for (the sowing itself, the cut of a plant
        /// in its way, the haul of an item in its way). Samples the zone's cells rather than scanning them all, unless <paramref name="every"/> (is there one at all: every cell, in order). <paramref name="accept"/>: the job is not given unless it says so (the outside work wants every target in the open).
        /// </summary>
        internal static Job GrowerJob(Pawn inmate, PrisonMap pm, PrisonZoneDef prisonZone, bool sowing, System.Func<Job, bool> accept = null, bool every = false)
        {
            List<IntVec3> cells = pm.zones.CellsOf(prisonZone);
            if (cells.Count == 0) return null;
            Map map = inmate.Map;
            for (int i = 0, tries = every ? cells.Count : 40; i < tries; i++)
            {
                IntVec3 c = every ? cells[i] : cells.RandomElement();
                if (c.GetZone(map) is not Zone_Growing zone || zone.ContainsStaticFire) continue;
                if (sowing && !zone.allowSow) continue;
                Job job;
                WantedPlant() = zone.GetPlantDefToGrow();
                try
                {
                    job = !sowing ? (Harvest.HasJobOnCell(inmate, c) ? Harvest.JobOnCell(inmate, c) : null) : Sow.JobOnCell(inmate, c);
                }
                finally
                {
                    WantedPlant() = null;
                }
                if (job == null) continue;
                // Harvest jobs queue nearby plants of the same crop: keep only the zone's.
                if (job.targetQueueA != null)
                {
                    job.targetQueueA.RemoveAll(t => pm.zones.ZoneAt(t.Cell) != prisonZone);
                    if (job.def == JobDefOf.Harvest && job.targetQueueA.Count == 0 && !job.targetA.IsValid) continue;
                }
                if (accept != null && !accept(job)) continue;
                return job;
            }
            return null;
        }

        /// <summary>Hourly: whatever inmates harvest is dropped forbidden (vanilla, non-colonist harvester), often just
        /// outside the garden: release items in and around it.</summary>
        public static void ReleaseProduce(Map map, PrisonMap pm) => ReleaseAround(map, pm, RPLaborDefOf.RP_Garden, null);

        /// <summary>Releases the forbidden items on the zone's cells and the ones next to them (every item, or the ones <paramref name="which"/> says).</summary>
        internal static void ReleaseAround(Map map, PrisonMap pm, PrisonZoneDef prisonZone, System.Func<Thing, bool> which)
        {
            List<IntVec3> garden = pm.zones.CellsOf(prisonZone);
            if (garden.Count == 0) return;
            var seen = new HashSet<IntVec3>();
            foreach (IntVec3 g in garden)
                foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(new TargetInfo(g, map)).Concat(g))
                {
                    if (!c.InBounds(map) || !seen.Add(c)) continue;
                    List<Thing> things = c.GetThingList(map);
                    for (int i = 0; i < things.Count; i++)
                        if (things[i].def.category == ThingCategory.Item && things[i].IsForbidden(Faction.OfPlayer) && (which == null || which(things[i]))) things[i].SetForbidden(false, warnOnFail: false);
                }
        }
    }

    /// <summary>
    /// The trusties' outside work (see <see cref="Trusties"/>): cut the wild trees and brush in the outside work zone, and harvest and sow the vanilla growing zones inside it. Given only to a trusty while the party is out, and
    /// only for targets that lie in the zone and in the open. What is cut and harvested is the prison's (<see cref="ReleaseProduce"/>).
    /// </summary>
    public class InmateJobWorker_Outside : InmateJobWorker
    {
        /// <summary>One place per <see cref="Trusties.CellsPerPlace"/> cells of the zone that have work to do: a growing zone, or a wild plant to cut.</summary>
        public override int Capacity(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return 0;
            int n = 0;
            foreach (IntVec3 c in pm.zones.CellsOf(RPTrustyDefOf.RP_OutsideWork))
                if (Trusties.InZone(pm, c) && (c.GetZone(map) is Zone_Growing || WildTarget(c, map) != null)) n++;
            return n == 0 ? 0 : Mathf.Max(1, n / Trusties.CellsPerPlace);
        }

        /// <summary>
        /// The party is out and the trusty has the work: they are at it, or the zone has some to give them now (<see cref="FindJob"/> over every cell: a ripe harvest, a cell to sow, a wild plant to cut; a strip with everything sown and
        /// growing, or nothing left to cut, gives none, and they go to the class). A class that would take the work slot leaves them be (see <see cref="ProgramSystem.KeepsToTheirJob"/>): the hourly class pass queues every attendee for a
        /// regime interrupt, which ends a cut, and a cut that is ended starts again from nothing, so a tree that takes more than an hour was never felled. A course the warden referred them to is the warden's word: the class pass leaves
        /// that course to take them (and no other).
        /// </summary>
        public override bool HasWork(Pawn inmate) =>
            inmate.Inmate() is { IsTrusty: true } && inmate.Spawned && PrisonMap.Of(inmate.Map) is { } pm && pm.party.isOut && (Trusties.IsWorkingOutside(inmate) || FindJob(inmate, pm, every: true) != null);

        public override Job TryGiveJob(Pawn inmate, PrisonMap pm) => inmate.Inmate() is { IsTrusty: true } && pm.party.isOut ? FindJob(inmate, pm, every: false) : null;

        /// <summary>
        /// The work the zone has for this trusty: the job <see cref="TryGiveJob"/> gives (sampling the zone's cells), or, <paramref name="every"/>, whether there is any (every cell looked at, so a miss is no chance of the sample). The one
        /// test of what is work, for the two, so that a trusty is never told there is work and then given none.
        /// </summary>
        private static Job FindJob(Pawn inmate, PrisonMap pm, bool every)
        {
            List<IntVec3> cells = pm.zones.CellsOf(RPTrustyDefOf.RP_OutsideWork);
            if (cells.Count == 0) return null;
            bool Accept(Job job) => Trusties.JobInZone(job, pm) && FirstTarget(job) is { IsValid: true } first && inmate.CanReach(first, PathEndMode.Touch, Danger.Some);
            // Harvest first, then the wild plants, then sow.
            return InmateJobWorker_Garden.GrowerJob(inmate, pm, RPTrustyDefOf.RP_OutsideWork, sowing: false, Accept, every) ?? CutJob(inmate, pm, cells, Accept, every) ?? InmateJobWorker_Garden.GrowerJob(inmate, pm, RPTrustyDefOf.RP_OutsideWork, sowing: true, Accept, every);
        }

        /// <summary>Where the job goes first: its target, or the first of the queue (a harvest has no target until it starts).</summary>
        private static LocalTargetInfo FirstTarget(Job job) => job.targetA.IsValid ? job.targetA : job.targetQueueA is { Count: > 0 } queue ? queue[0] : LocalTargetInfo.Invalid;

        /// <summary>The cost of walking over a plant that makes it brush (bushes, brambles, tall grass): short grass and the flowers are no work.</summary>
        private const int BrushPathCost = 14;

        /// <summary>A wild tree or brush on the cell (a plant that is no crop and not in a growing zone), or null.</summary>
        internal static Plant WildTarget(IntVec3 c, Map map)
        {
            if (c.GetPlant(map) is not { Spawned: true, Destroyed: false } plant || c.GetZone(map) is Zone_Growing) return null;
            return plant.def.plant.IsTree || !plant.def.plant.Sowable && plant.def.pathCost >= BrushPathCost ? plant : null;
        }

        /// <summary>Cuts a wild plant in the zone: a stretch of the zone's cells from a random start (the zone is no larger than a few hundred cells; <paramref name="every"/>: all of them), the first plant that is theirs to cut.</summary>
        private static Job CutJob(Pawn inmate, PrisonMap pm, List<IntVec3> cells, System.Func<Job, bool> accept, bool every)
        {
            Map map = inmate.Map;
            int start = every ? 0 : Rand.Range(0, cells.Count);
            for (int i = 0; i < cells.Count && (every || i < 150); i++)
            {
                IntVec3 c = cells[(start + i) % cells.Count];
                if (WildTarget(c, map) is not { } plant || plant.IsBurning() || !inmate.CanReserve(plant) || !PlantUtility.PawnWillingToCutPlant_Job(plant, inmate) || PlantUtility.TreeMarkedForExtraction(plant)) continue;
                if (plant.TryGetComp<CompPlantPreventCutting>() is { PreventCutting: true }) continue;
                Job job = JobMaker.MakeJob(JobDefOf.CutPlant, plant);
                if (accept(job)) return job;
            }
            return null;
        }

        private static HashSet<ThingDef> produce;

        /// <summary>What a plant yields (wood, the crops): the only things the zone gives the prison. Not any other item that happens to lie forbidden near it.</summary>
        private static HashSet<ThingDef> Produce
        {
            get
            {
                if (produce != null) return produce;
                produce = new HashSet<ThingDef>();
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                    if (d.plant?.harvestedThingDef is { } yield) produce.Add(yield);
                return produce;
            }
        }

        /// <summary>Hourly: the wood cut and the crops harvested in the zone are dropped forbidden, as the garden's are: released, those only (never another forbidden item the warden left there).</summary>
        public static void ReleaseProduce(Map map, PrisonMap pm) => InmateJobWorker_Garden.ReleaseAround(map, pm, RPTrustyDefOf.RP_OutsideWork, t => Produce.Contains(t.def));
    }

    /// <summary>Cleaning duty: mop the filth in inmate areas.</summary>
    public class InmateJobWorker_Cleaning : InmateJobWorker
    {
        public override int Capacity(Map map) => Mathf.Max(1, map.mapPawns.PrisonersOfColonySpawnedCount / 8);

        public override Job TryGiveJob(Pawn inmate, PrisonMap pm)
        {
            // (Assigned work is exempt from the rooms kept for a level, see ZoneGrid.SetKept: the cleaners mop wherever the filth is, and compliance reads them the same way.)
            Thing filth = GenClosest.ClosestThingReachable(inmate.Position, inmate.Map, ThingRequest.ForGroup(ThingRequestGroup.Filth), PathEndMode.Touch,
                TraverseParms.For(inmate), 40f, t => t is Filth && pm.zones.ZoneAt(t.Position) is not { staffOnly: true } && inmate.Map.areaManager.Home[t.Position] && inmate.CanReserve(t));
            if (filth == null) return null;
            Job job = JobMaker.MakeJob(JobDefOf.Clean);
            job.AddQueuedTarget(TargetIndex.A, filth);
            return job;
        }
    }

    [DefOf]
    public static class RPLaborDefOf
    {
        public static PrisonZoneDef RP_Workshop;
        public static PrisonZoneDef RP_Laundry;
        public static PrisonZoneDef RP_Garden;
        public static PrisonZoneDef RP_Kitchen;
        public static StaffRoleDef RP_Gardener;
        public static ThingDef RP_LicensePlates;
        public static ThingDef RP_ServingCounter;
        public static JobDef RP_InmateCook;
        public static JobDef RP_InmateProduce;
        public static JobDef RP_DoLaundry;
        public static JobDef RP_Sew;
        public static RegimeActivityDef RP_Work;

        static RPLaborDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPLaborDefOf));
    }

    /// <summary>Work slot: inmates with a prison job do it; the rest stay in their cells.</summary>
    public class RegimeWorker_Work : RegimeActivityWorker
    {
        public override Job TryGiveJob(Pawn pawn, CompInmate comp, PrisonMap pm)
        {
            if (comp.job == null) return null;
            if (comp.job.requiredProgram != null && !comp.passedPrograms.Contains(comp.job.requiredProgram)) return null;
            // The outside job is a trusty's, and only while the party is out: no work and no wandering in the outside zone for anyone else, and none in the hours the party is called in (the slot falls back to lockup).
            if (comp.job == RPTrustyDefOf.RP_JobOutside)
            {
                if (!comp.IsTrusty || !pm.party.isOut) return null;
                // (The pass is theirs from the moment the work starts, not at the next look of the 250 ticks.)
                comp.UpdateWorkPass();
            }
            // The room's work is held for a re-count or a search of its crew (see ToolCount): they wait in it.
            if (ToolCount.Held(comp.job.zone)) return WanderInZones(pawn, pm, JobZones(comp.job));
            return comp.job.Worker.TryGiveJob(pawn, pm) ?? WanderInZones(pawn, pm, JobZones(comp.job));
        }

        private static readonly Dictionary<InmateJobDef, List<PrisonZoneDef>> zonesByJob = new Dictionary<InmateJobDef, List<PrisonZoneDef>>();

        private static List<PrisonZoneDef> JobZones(InmateJobDef job)
        {
            if (!zonesByJob.TryGetValue(job, out List<PrisonZoneDef> list))
                zonesByJob[job] = list = job.zone != null ? new List<PrisonZoneDef> { job.zone } : new List<PrisonZoneDef>();
            return list;
        }
    }

    public class JobDriver_InmateProduce : JobDriver
    {
        private Thing Bench => job.targetA.Thing;
        private CompWorkstation Station => Bench?.TryGetComp<CompWorkstation>();

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed) && pawn.Reserve(job.targetB, job, 1, job.count, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => Station is not { Usable: true });
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            // (A workshop short of tools by the staff's count works more slowly, see ToolCount.)
            Toil work = Toils_General.Wait(ToolCount.WorkTicks(RPLaborDefOf.RP_Workshop, Station?.Props.workTicks ?? 2000));
            work.handlingFacing = true;
            work.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(Bench);
                SkillDef skill = Station?.Props.skill;
                if (skill != null) pawn.skills?.Learn(skill, 0.08f * delta);
            };
            work.WithProgressBarToilDelay(TargetIndex.A);
            work.FailOn(() => pawn.carryTracker.CarriedThing == null);
            yield return work;
            Toil finish = ToilMaker.MakeToil("FinishProduct");
            finish.initAction = () =>
            {
                CompWorkstation st = Station;
                Thing carried = pawn.carryTracker.CarriedThing;
                if (st == null || carried == null) return;
                int used = Mathf.Min(carried.stackCount, st.Props.inputCount);
                carried.SplitOff(used).Destroy();
                if (pawn.carryTracker.CarriedThing != null) pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                Thing product = ThingMaker.MakeThing(st.Props.product);
                product.stackCount = st.Props.productCount;
                GenPlace.TryPlaceThing(product, Bench.InteractionCell, Bench.Map, ThingPlaceMode.Near);
            };
            yield return finish;
        }
    }

    public static class LaborUtility
    {
        private static readonly HashSet<JobDef> workJobs = new HashSet<JobDef>();

        /// <summary>Doing their prison job right now (paid, counts as hours worked).</summary>
        public static bool IsWorking(Pawn p, CompInmate c)
        {
            // The outside work counts only on a target in the zone: a cut, a sow, a harvest or a haul that clears a sow's way.
            if (c.job == RPTrustyDefOf.RP_JobOutside) return Trusties.IsWorkingOutside(p);
            // Gardening also means cutting weeds and moving things out of the way.
            if (c.job?.zone == RPLaborDefOf.RP_Garden && (p.CurJobDef == JobDefOf.CutPlant || p.CurJobDef == JobDefOf.HaulToCell)) return true;
            if (workJobs.Count == 0)
            {
                workJobs.Add(RPLaborDefOf.RP_InmateProduce);
                workJobs.Add(RPLaborDefOf.RP_DoLaundry);
                workJobs.Add(RPLaborDefOf.RP_Sew);
                workJobs.Add(JobDefOf.Clean);
                workJobs.Add(JobDefOf.Sow);
                workJobs.Add(JobDefOf.Harvest);
                workJobs.Add(RPLaborDefOf.RP_InmateCook);
                workJobs.Add(RPMailDefOf.RP_SortMail);
                workJobs.Add(RPLibraryDefOf.RP_DeliverBooks);
            }
            return p.CurJobDef != null && workJobs.Contains(p.CurJobDef);
        }

        /// <summary>Hands out free job slots to eligible inmates; removes jobs from those no longer eligible.</summary>
        public static void AssignJobs(Map map)
        {
            // (The outside job first: only a trusty may hold it, and one with no job would else be taken by the first job with a place free.)
            var jobs = new List<InmateJobDef>(DefDatabase<InmateJobDef>.AllDefsListForReading);
            if (jobs.Remove(RPTrustyDefOf.RP_JobOutside)) jobs.Insert(0, RPTrustyDefOf.RP_JobOutside);
            var used = new Dictionary<InmateJobDef, int>();
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            // The jobs the inmates hold, by thing id, when the warden keeps some of them apart: two of a pair do not work in one room (the workshop, the kitchen, the laundry).
            Dictionary<int, InmateJobDef> held = KeepApart.Any ? new Dictionary<int, InmateJobDef>() : null;
            foreach (Pawn p in inmates)
            {
                CompInmate c = p.Inmate();
                if (c?.job == null) continue;
                // A job that asks for a program they have not passed does nothing (the regime gives no work) and would hold a slot from a trained inmate, locked by the warden or not (an old save's menu choice).
                bool untrained = c.job.requiredProgram != null && !c.passedPrograms.Contains(c.job.requiredProgram);
                // The outside job is a trusty's, locked by the warden or not: one who holds it without the status loses it.
                bool notTrusty = !Trusties.MayHold(c, c.job);
                if (untrained || notTrusty || !Eligible(c, c.job) && !c.jobLocked)
                {
                    if (untrained || notTrusty) c.jobLocked = false;
                    c.job = null;
                    continue;
                }
                used.TryGetValue(c.job, out int n);
                used[c.job] = n + 1;
                if (held != null) held[p.thingIDNumber] = c.job;
            }
            if (held != null)
            {
                SeparatePairs(inmates, held, used);
                RotatePairs(held, used);
            }
            foreach (InmateJobDef job in jobs)
            {
                used.TryGetValue(job, out int n);
                int cap = job.Worker.Capacity(map);
                if (n >= cap) continue;
                // The outside job goes first to trusties with no job, then moves ones whose job the warden did not lock out of it: a trusty nearly always has a job already, and the status would do nothing else.
                bool outside = job == RPTrustyDefOf.RP_JobOutside;
                for (int round = 0; round < (outside ? 2 : 1) && n < cap; round++)
                    foreach (Pawn p in inmates)
                    {
                        CompInmate c = p.Inmate();
                        if (c == null || c.jobLocked || c.job == job || (round == 0 ? c.job != null : c.job == null) || !Eligible(c, job) || held != null && KeepApart.PartnerHolds(p, job.zone, held)) continue;
                        if (c.job != null)
                        {
                            used[c.job]--;
                            held?.Remove(p.thingIDNumber);
                        }
                        c.job = job;
                        if (held != null) held[p.thingIDNumber] = job;
                        if (++n >= cap) break;
                    }
            }
        }

        /// <summary>
        /// Of two inmates the warden keeps apart who hold jobs in one room (the same zone), the one whose job the warden did not lock gives theirs up (when both are free to, the one who does not <see cref="KeepApart.Precedes"/> the other today): they are
        /// given another job below, or none. Two locked jobs stay as the warden set them, and the order's line says so.
        /// </summary>
        private static void SeparatePairs(IReadOnlyList<Pawn> inmates, Dictionary<int, InmateJobDef> held, Dictionary<InmateJobDef, int> used)
        {
            foreach (Pawn p in inmates)
            {
                if (p.Inmate() is not { job: { zone: { } zone } job } c || c.jobLocked || !KeepApart.PartnerHolds(p, zone, held)) continue;
                // (A partner whose job is free too is asked in turn: the one who goes first today keeps theirs.)
                if (KeepApart.OrdersOf(p) is not { } orders || !orders.Exists(o => held.TryGetValue(o.OtherId(p.thingIDNumber), out InmateJobDef other) && other.zone == zone && (o.Other(p)?.Inmate() is not { jobLocked: false } || KeepApart.Precedes(o.Other(p), p)))) continue;
                c.job = null;
                held.Remove(p.thingIDNumber);
                used[job]--;
            }
        }

        /// <summary>
        /// The rotation of a room between two the warden keeps apart: the one who holds a job in a zone keeps it only while they <see cref="KeepApart.Precedes"/> the other - or while the other has no job at all. On a day the other goes first,
        /// holds no job at all (is out of work, so the room is the only thing they lack), can do the holder's job and neither job is locked by the warden, the holder's job is handed to the other at once (the holder is given another below,
        /// if one is free): so over the days a pair left with one room between them have it in turn, and no one keeps it just for having been there first. Two who both have work are left as they are (they are in different rooms already, and
        /// nobody loses a job to the rotation), and an inmate with several orders takes one handover a pass: they hold a job after the first. At most one handover per order per day (<see cref="KeepApartOrder.handoverDay"/>). A job the
        /// warden locked is never taken, and an inmate who could not do the job (<see cref="Eligible"/>) or is kept out of the zone by another order is not offered it.
        /// </summary>
        private static void RotatePairs(Dictionary<int, InmateJobDef> held, Dictionary<InmateJobDef, int> used)
        {
            int today = KeepApart.Today;
            KeepApart.Book.EnsureResolved();
            foreach (KeepApartOrder o in KeepApart.Book.orders)
            {
                if (o.handoverDay == today || o.a == null || o.b == null) continue;
                (Pawn first, Pawn holder) = KeepApart.Precedes(o.a, o.b) ? (o.a, o.b) : (o.b, o.a);
                if (!first.Spawned || !first.IsPrisonerOfColony || first.Dead || first.Inmate() is not { jobLocked: false } fc || holder.Inmate() is not { jobLocked: false } hc || hc.job is not { zone: { } zone } job) continue;
                // (Only to one with no job at all - one who works elsewhere is no one to hand a job to - and not the holder's to give: the separation above has dealt with that.)
                if (fc.job != null || !held.TryGetValue(holder.thingIDNumber, out InmateJobDef current) || current != job || !Eligible(fc, job)) continue;
                held.Remove(holder.thingIDNumber);
                if (KeepApart.PartnerHolds(first, zone, held))
                {
                    held[holder.thingIDNumber] = job;
                    continue;
                }
                hc.job = null;
                fc.job = job;
                held[first.thingIDNumber] = job;
                o.handoverDay = today;
            }
        }

        /// <summary>
        /// Whether the inmate may hold the job. <paramref name="forProgram"/>: asked by a program that trains for it, which does not look at what the job itself asks for (the programs passed, and the kitchen's stand-down
        /// after a scare: the day to retrain).
        /// </summary>
        public static bool Eligible(CompInmate c, InmateJobDef job, bool forProgram = false)
        {
            if (c.security == null || c.tension > job.maxTension || c.forcedActivity != null || !forProgram && FoodScare.StoodDown(job)) return false;
            // (Only a trusty holds the outside job, whoever asks: the daily pass, the warden's menu, a talent, a gang deal.)
            if (!Trusties.MayHold(c, job)) return false;
            if (!forProgram && job.requiredProgram != null && !c.passedPrograms.Contains(job.requiredProgram)) return false;
            return job.allowedSecurity.Count == 0 || job.allowedSecurity.Contains(c.security);
        }

        /// <summary>Morning export run: everything in the Exports zone is sold at market value.</summary>
        public static void SellExports(Map map, PrisonGame game)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            List<IntVec3> cells = pm.zones.CellsOf(RPDefOf.RP_Exports);
            float total = 0f;
            int count = 0, plates = 0;
            var sold = new HashSet<Thing>();
            foreach (IntVec3 c in cells)
                foreach (Thing t in c.GetThingList(map))
                    if (t.def.category == ThingCategory.Item && t.def.tradeability.TraderCanSell()) sold.Add(t);
            foreach (Thing t in sold)
            {
                total += t.MarketValue * t.stackCount;
                count += t.stackCount;
                if (t.def == ExportContracts.Plates) plates += t.stackCount;
                t.Destroy();
            }
            // The tailors' surplus uniforms go with the rest (there has to be an Exports zone for them to be loaded from).
            if (cells.Count > 0 && game.laundry.TakeSurplus() is var uniforms and > 0)
            {
                total += uniforms * Tailoring.UniformValue;
                count += uniforms;
            }
            int money = Mathf.RoundToInt(total);
            if (cells.Count > 0) ExportContracts.Notify_ExportRun(game, plates);
            if (money <= 0) return;
            game.ledger.Add(RPDefOf.RP_ExportSales, money, "RP_ExportNote".Translate(count));
            game.stats.itemsExported += count;
            Messages.Message("RP_ExportsSold".Translate(count, Money.Format(money)), MessageTypeDefOf.PositiveEvent);
        }
    }
}
