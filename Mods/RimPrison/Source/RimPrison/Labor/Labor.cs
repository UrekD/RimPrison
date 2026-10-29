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
            List<IntVec3> garden = pm.zones.CellsOf(RPLaborDefOf.RP_Garden);
            if (garden.Count == 0) return null;
            Map map = inmate.Map;
            // Sample rather than scan everything: harvest first, then sow.
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < 40; i++)
                {
                    IntVec3 c = garden.RandomElement();
                    if (c.GetZone(map) is not Zone_Growing zone || zone.ContainsStaticFire) continue;
                    if (pass == 1 && !zone.allowSow) continue;
                    Job job;
                    WantedPlant() = zone.GetPlantDefToGrow();
                    try
                    {
                        job = pass == 0 ? (Harvest.HasJobOnCell(inmate, c) ? Harvest.JobOnCell(inmate, c) : null) : Sow.JobOnCell(inmate, c);
                    }
                    finally
                    {
                        WantedPlant() = null;
                    }
                    if (job == null) continue;
                    // Harvest jobs queue nearby plants of the same crop: keep only the garden's.
                    if (job.targetQueueA != null)
                    {
                        job.targetQueueA.RemoveAll(t => pm.zones.ZoneAt(t.Cell) != RPLaborDefOf.RP_Garden);
                        if (job.def == JobDefOf.Harvest && job.targetQueueA.Count == 0 && !job.targetA.IsValid) continue;
                    }
                    return job;
                }
            return null;
        }

        /// <summary>Hourly: whatever inmates harvest is dropped forbidden (vanilla, non-colonist harvester), often just
        /// outside the garden: release items in and around it.</summary>
        public static void ReleaseProduce(Map map, PrisonMap pm)
        {
            List<IntVec3> garden = pm.zones.CellsOf(RPLaborDefOf.RP_Garden);
            if (garden.Count == 0) return;
            var seen = new HashSet<IntVec3>();
            foreach (IntVec3 g in garden)
                foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(new TargetInfo(g, map)).Concat(g))
                {
                    if (!c.InBounds(map) || !seen.Add(c)) continue;
                    List<Thing> things = c.GetThingList(map);
                    for (int i = 0; i < things.Count; i++)
                        if (things[i].def.category == ThingCategory.Item && things[i].IsForbidden(Faction.OfPlayer)) things[i].SetForbidden(false, warnOnFail: false);
                }
        }
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
            List<InmateJobDef> jobs = DefDatabase<InmateJobDef>.AllDefsListForReading;
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
                if (untrained || !Eligible(c, c.job) && !c.jobLocked)
                {
                    if (untrained) c.jobLocked = false;
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
                foreach (Pawn p in inmates)
                {
                    CompInmate c = p.Inmate();
                    if (c == null || c.job != null || c.jobLocked || !Eligible(c, job) || held != null && KeepApart.PartnerHolds(p, job.zone, held)) continue;
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
