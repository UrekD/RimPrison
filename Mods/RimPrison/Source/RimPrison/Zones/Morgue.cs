using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPMorgueDefOf
    {
        public static PrisonZoneDef RP_Morgue;
        public static JobDef RP_HaulToMorgue;

        static RPMorgueDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPMorgueDefOf));
    }

    /// <summary>
    /// The morgue: a staff-only zone where the prison's dead are put. Without one a body lies where it fell (the vanilla storage presets do not take corpses, so nobody hauls it) and
    /// rots in front of inmates and staff, with vanilla's corpse moods for everyone who passes. With one painted, the staff who haul (guards, cooks, workmen, janitors) carry the
    /// body of an inmate or a staff member there. A body already in storage that takes it (a corpse stockpile) is left where it is. The state shuttle takes the bodies in the morgue home at lift-off and the family writes (<see cref="CondolenceSystem"/>); the bodies nobody will send for are buried in the prison plot (<see cref="Burial"/>).
    /// </summary>
    public static class Morgue
    {
        /// <summary>
        /// A body of the prison's own: a person who is an inmate (has a security level) or on the staff, as against a raider, which vanilla's own hauling deals with, or an animal or a mech
        /// (the penitentiary's dogs belong to the player's faction and would fill the morgue's cells).
        /// </summary>
        public static bool Ours(Corpse c) =>
            c.InnerPawn is { } p && p.RaceProps.Humanlike && (p.Faction == Faction.OfPlayer || p.guest?.HostFaction == Faction.OfPlayer || p.Inmate() is { security: not null });

        public static bool InMorgue(Corpse c, PrisonMap pm) => c.Spawned && pm.zones.ZoneAt(c.Position) == RPMorgueDefOf.RP_Morgue;

        /// <summary>
        /// A body that needs no carrying: in the morgue (or in the burial plot, when the plot is for it and can take it: see <see cref="Burial.Wanted"/>, else the guards carry it to the morgue), or in storage that takes it (vanilla's dumping stockpile, 1.7's corpse stockpile). The morgue is a zone of this mod, not storage, so to
        /// vanilla's hauling a body in it lies where it should not: carrying a body out of a stockpile into the morgue would then be carried back, for as long as both stood.
        /// </summary>
        public static bool Settled(Corpse c, PrisonMap pm) => InMorgue(c, pm) || Burial.InPlot(c, pm) && Burial.Wanted(c, pm) || c.Spawned && StoreUtility.IsInValidStorage(c);

        /// <summary>The prison's bodies that lie outside the morgue (and outside any storage that takes them) and have lain there at least <paramref name="minAgeTicks"/> ticks.</summary>
        public static int Uncollected(Map map, PrisonMap pm, int minAgeTicks = 0) => Uncollected(map, pm, minAgeTicks, out _);

        /// <summary>The same, and where the first of them lies (invalid when none, or it is not on the map).</summary>
        public static int Uncollected(Map map, PrisonMap pm, int minAgeTicks, out IntVec3 first)
        {
            int n = 0;
            first = IntVec3.Invalid;
            List<Thing> corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
            for (int i = 0; i < corpses.Count; i++)
            {
                if (corpses[i] is not Corpse c || c.Age < minAgeTicks || !Ours(c) || Settled(c, pm)) continue;
                if (n++ == 0 && c.Spawned) first = c.Position;
            }
            return n;
        }

        /// <summary>A body that has lain this long outside the morgue is reported on the to-do list (the guards collect one within the hour when they can).</summary>
        public const int StuckAfterTicks = GenDate.TicksPerHour;

        private static int fullMap = -1, fullTick = -1;
        private static bool fullVerdict;

        /// <summary>Forget the verdict of <see cref="Full"/> (tests change the morgue within a tick).</summary>
        public static void Reset() => fullTick = -1;

        /// <summary>Whether every cell of the morgue a body can lie on has one on it (kept for the tick per map: every hauler's scan asks, for every body, and a full morgue has nothing to reach).</summary>
        public static bool Full(Map map, PrisonMap pm)
        {
            int now = Find.TickManager.TicksGame;
            if (fullMap == map.uniqueID && fullTick == now) return fullVerdict;
            fullMap = map.uniqueID;
            fullTick = now;
            fullVerdict = true;
            foreach (IntVec3 c in pm.zones.CellsOf(RPMorgueDefOf.RP_Morgue))
                if (c.Standable(map) && c.GetFirstThing<Corpse>(map) == null)
                {
                    fullVerdict = false;
                    break;
                }
            return fullVerdict;
        }

        /// <summary>A cell of the morgue the hauler can reach with nothing laid on it yet.</summary>
        public static IntVec3 FreeCell(Pawn hauler, PrisonMap pm)
        {
            Map map = hauler.Map;
            if (Full(map, pm)) return IntVec3.Invalid;
            foreach (IntVec3 c in pm.zones.CellsOf(RPMorgueDefOf.RP_Morgue))
            {
                if (!c.Standable(map) || c.GetFirstThing<Corpse>(map) != null || !hauler.CanReserveAndReach(c, PathEndMode.OnCell, Danger.Some)) continue;
                return c;
            }
            return IntVec3.Invalid;
        }
    }

    /// <summary>Carries the body of an inmate or a staff member to the morgue (staff who haul; only when a morgue is painted).</summary>
    public class WorkGiver_HaulToMorgue : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.Corpse);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => PrisonMap.Of(pawn.Map) is not { } pm || !pm.zones.Any(RPMorgueDefOf.RP_Morgue);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is not Corpse c || !c.Spawned || PrisonMap.Of(pawn.Map) is not { } pm || !Morgue.Ours(c) || Morgue.Settled(c, pm) || c.IsForbidden(pawn)) return false;
            return pawn.CanReserveAndReach(c, PathEndMode.ClosestTouch, Danger.Some, 1, -1, null, forced) && Morgue.FreeCell(pawn, pm).IsValid;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            IntVec3 cell = Morgue.FreeCell(pawn, PrisonMap.Of(pawn.Map));
            if (!cell.IsValid) return null;
            Job job = JobMaker.MakeJob(RPMorgueDefOf.RP_HaulToMorgue, t, cell);
            job.count = 1;
            Concepts.Demonstrated(Concepts.Morgue);
            return job;
        }
    }

    public class JobDriver_HaulToMorgue : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed) && pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnForbidden(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnSomeonePhysicallyInteracting(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            Toil carry = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);
            yield return carry;
            yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carry, storageMode: false);
            // The body is in the morgue: the warden is asked what it gets (see Funerals).
            Toil laid = ToilMaker.MakeToil("Laid");
            laid.initAction = () =>
            {
                if (job.targetA.Thing is Corpse c) Funerals.Notify_Laid(c);
            };
            laid.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return laid;
        }
    }
}
