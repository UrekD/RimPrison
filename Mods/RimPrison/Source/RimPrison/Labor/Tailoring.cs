using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Tailoring: inmates who passed the tailoring course sew prison uniforms at a vanilla tailor bench in the workshop, for export: the uniforms wait in <see cref="LaundrySystem.surplus"/> (up to <see cref="MaxSurplus"/>, and only
    /// with an Exports zone to load them from) for the next export run (see <see cref="LaborUtility.SellExports"/>). The laundry keeps the clean stock, which it does for nothing; the surplus only refills the stock when the stock
    /// runs out (an inmate's swap, the midnight top-up), before the outside service is paid. The cloth is bought, so each uniform costs <see cref="FabricPerUniform"/>, and only the uniforms that have somewhere to go are made and paid for.
    /// </summary>
    public static class Tailoring
    {
        /// <summary>Cloth for one uniform, what a surplus uniform sells for, and how many surplus uniforms wait for the export run at most.</summary>
        public const int FabricPerUniform = 4, UniformValue = 14, MaxSurplus = 24;

        /// <summary>How many more uniforms there is room for: the surplus up to its cap, with an Exports zone to load them from; none without.</summary>
        public static int Room(PrisonMap pm) => pm.zones.Any(RPDefOf.RP_Exports) ? Mathf.Max(0, MaxSurplus - PrisonGame.Instance.laundry.surplus) : 0;

        /// <summary>There is something to sew.</summary>
        public static bool Wanted(PrisonMap pm) => Room(pm) > 0;

        public static bool IsBench(CompWorkstation w, PrisonMap pm) =>
            w.Usable && w.Props.uniforms > 0 && pm.zones.ZoneAt(w.parent.Position) == RPLaborDefOf.RP_Workshop;

        public static int Cost(CompWorkstation w) => w.Props.uniforms * FabricPerUniform;

        /// <summary>
        /// A cycle done at the bench: the uniforms that there is room for (at this moment, not when the cycle began) are paid for and go to the surplus; none are sewn, or paid for, when there is no room or the prison cannot pay.
        /// </summary>
        public static bool Sew(CompWorkstation w, Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            int useful = PrisonMap.Of(map) is { } pm ? Mathf.Min(w.Props.uniforms, Room(pm)) : 0;
            if (useful <= 0 || !game.ledger.TrySpend(RPDefOf.RP_Supplies, useful * FabricPerUniform, "RP_SewNote".Translate(useful))) return false;
            game.laundry.AddSewn(useful);
            return true;
        }
    }

    /// <summary>Tailoring: sew uniforms at a tailor bench in the workshop.</summary>
    public class InmateJobWorker_Tailoring : InmateJobWorker
    {
        public override int Capacity(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return 0;
            int n = 0;
            foreach (CompWorkstation w in pm.Workstations)
                if (Tailoring.IsBench(w, pm)) n++;
            return n;
        }

        public override Job TryGiveJob(Pawn inmate, PrisonMap pm)
        {
            if (!Tailoring.Wanted(pm)) return null;
            foreach (CompWorkstation w in pm.Workstations)
                // (Not a bench the tailoring class has the room of: its attendees are on their way to it.)
                if (Tailoring.IsBench(w, pm) && !ProgramSystem.HeldByClass(inmate.Map, pm, w.parent) && PrisonGame.Instance.ledger.CanAfford(Tailoring.Cost(w))
                    && inmate.CanReserveAndReach(w.parent, PathEndMode.InteractionCell, Danger.Some))
                    return JobMaker.MakeJob(RPLaborDefOf.RP_Sew, w.parent);
            return null;
        }
    }

    public class JobDriver_Sew : JobDriver
    {
        private CompWorkstation Station => job.targetA.Thing?.TryGetComp<CompWorkstation>();

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => Station is not { Usable: true });
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            Toil work = Toils_General.Wait(Station?.Props.workTicks ?? 1800);
            work.handlingFacing = true;
            work.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(job.targetA);
                if (Station?.Props.skill is { } skill) pawn.skills?.Learn(skill, 0.08f * delta);
            };
            work.WithProgressBarToilDelay(TargetIndex.A);
            yield return work;
            Toil finish = ToilMaker.MakeToil("Sewn");
            finish.initAction = () =>
            {
                if (Station is { } st) Tailoring.Sew(st, pawn.Map);
            };
            yield return finish;
        }
    }
}
