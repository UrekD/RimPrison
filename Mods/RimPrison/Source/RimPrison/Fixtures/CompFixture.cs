using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>A building inmates (and staff) use to satisfy a need: toilet, shower, sink, weights, phone.</summary>
    public class CompProperties_Fixture : CompProperties
    {
        public NeedDef need;
        /// <summary>Need level gained per use tick (1.0 = full).</summary>
        public float gainPerTick = 0.001f;
        public int maxUseTicks = 1500;
        /// <summary>Uses end early once the need reaches this level.</summary>
        public float satisfiedLevel = 0.99f;
        public float joyGainPerTick;
        public JoyKindDef joyKind;
        public string useReport = "using {0}.";
        /// <summary>Pawn stands on the interaction cell (true) or on the fixture itself (false).</summary>
        public bool useInteractionCell = true;
        public EffecterDef useEffecter;
        public SoundDef useSustainer;

        public CompProperties_Fixture() => compClass = typeof(CompFixture);
    }

    public class CompFixture : ThingComp
    {
        public CompProperties_Fixture Props => (CompProperties_Fixture)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            PrisonMap.Of(parent.Map)?.RegisterFixture(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            PrisonMap.Of(map)?.DeregisterFixture(this);
        }

        public bool Usable => parent.Spawned && !parent.IsBrokenDown() && (parent.TryGetComp<CompPowerTrader>() is not { } power || power.PowerOn)
                              && (!Props.useInteractionCell || !parent.def.hasInteractionCell || parent.InteractionCell.Standable(parent.Map));

        public IntVec3 UseCell => Props.useInteractionCell && parent.def.hasInteractionCell ? parent.InteractionCell : parent.Position;
    }

    public static class FixtureFinder
    {
        /// <summary>
        /// Closest usable fixture for <paramref name="need"/> that the pawn can reserve and reach, preferring fixtures
        /// in the pawn's own room, then (optionally) only within one zone type. An inmate's own errand (whatever the zone asked for) is only to fixtures whose use cell is in a patch their
        /// security level may use (see <see cref="ZoneGrid.SetKept"/>); with <paramref name="survival"/> (the urgent toilet) the nearest of the others is theirs when no such fixture can be had,
        /// so nobody wets themselves for a sign on a door. Assigned inmate work does not come through here and is not held to the rule (the clerk, the cleaners, the kitchen and the laundry go where the job is).
        /// </summary>
        public static CompFixture FindFor(Pawn pawn, NeedDef need, PrisonZoneDef zone = null, bool ownRoomOnly = false, bool survival = false)
        {
            CompFixture answer = FindForFast(pawn, need, zone, ownRoomOnly, true);
            if (answer == null && survival && KeptRooms.LevelOf(pawn) != null && PrisonMap.Of(pawn.Map) is { zones: { AnyKept: true } }) answer = FindForFast(pawn, need, zone, ownRoomOnly, false);
            if (AutoTest.Active && PrisonMap.Of(pawn.Map) != null) Shadow.CheckFixture(pawn, need, zone, ownRoomOnly, survival, answer);
            return answer;
        }

        private static CompFixture FindForFast(Pawn pawn, NeedDef need, PrisonZoneDef zone, bool ownRoomOnly, bool keptRule)
        {
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            if (pm == null) return null;
            List<CompFixture> all = pm.Fixtures;
            Room room = pawn.GetRoom();
            SecurityLevelDef level = KeptRooms.LevelOf(pawn);
            // The fixtures that fit, with how far each is (the ones in their own room count a tenth of the distance), then tried nearest first: the first that
            // can be reserved and reached is the answer, instead of reaching for every fixture that was nearer than the best so far in list order.
            fits.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                CompFixture f = all[i];
                if (f.Props.need != need || !f.Usable) continue;
                bool sameRoom = room != null && f.parent.GetRoom() == room;
                if (ownRoomOnly && !sameRoom) continue;
                if (zone != null && !sameRoom && pm.zones.ZoneAt(f.parent.Position) != zone) continue;
                // (Where they would stand to use it, not where the building is: the phone or the shower of a patch kept for other levels is not theirs, nor is one whose cell is in it.)
                if (keptRule && !pm.zones.Allows(f.UseCell, level)) continue;
                float d = (f.parent.Position - pawn.Position).LengthHorizontalSquared;
                if (sameRoom) d *= 0.1f;
                fits.Add((f, d));
            }
            while (fits.Count > 0)
            {
                // (The earliest in the list wins a tie, as it did when the list was walked in order: only a strictly nearer one replaces the pick.)
                int pick = 0;
                for (int i = 1; i < fits.Count; i++)
                    if (fits[i].dist < fits[pick].dist) pick = i;
                CompFixture f = fits[pick].fixture;
                if (pawn.CanReserve(f.parent) && pawn.CanReach(f.UseCell, PathEndMode.OnCell, Danger.Some)) return f;
                fits.RemoveAt(pick);
            }
            return null;
        }

        private static readonly List<(CompFixture fixture, float dist)> fits = new List<(CompFixture, float)>();

        public static Job UseJob(CompFixture f) => JobMaker.MakeJob(RPDefOf.RP_UseFixture, f.parent);
    }

    /// <summary>Walk to a fixture, use it until the need is satisfied or the use time runs out.</summary>
    public class JobDriver_UseFixture : JobDriver
    {
        /// <summary>A wash that ran this long was not cut off at once (the autotest counts it).</summary>
        private const int WashNotCutOffTicks = 300;

        private CompFixture Fixture => job.targetA.Thing?.TryGetComp<CompFixture>();

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        public override string GetReport()
        {
            CompFixture f = Fixture;
            return f == null ? base.GetReport() : string.Format(f.Props.useReport, f.parent.LabelShort);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => Fixture is not { Usable: true });
            CompFixture fixture = Fixture;
            yield return Toils_Goto.GotoCell(fixture.UseCell, PathEndMode.OnCell);
            var use = ToilMaker.MakeToil("UseFixture");
            use.defaultCompleteMode = ToilCompleteMode.Delay;
            use.defaultDuration = fixture.Props.maxUseTicks;
            use.handlingFacing = true;
            use.tickIntervalAction = delta =>
            {
                CompFixture f = Fixture;
                if (f == null) return;
                pawn.rotationTracker.FaceTarget(f.parent);
                Need need = f.Props.need == null ? null : pawn.needs.TryGetNeed(f.Props.need);
                if (need != null)
                {
                    need.CurLevel += f.Props.gainPerTick * delta;
                    if (need.CurLevel >= f.Props.satisfiedLevel) ReadyForNextToil();
                }
                if (f.Props.joyGainPerTick > 0f && pawn.needs.joy != null)
                    pawn.needs.joy.GainJoy(f.Props.joyGainPerTick * delta, f.Props.joyKind ?? JoyKindDefOf.Meditative);
            };
            if (fixture.Props.useEffecter != null) use.WithEffect(() => fixture.Props.useEffecter, TargetIndex.A);
            if (fixture.Props.useSustainer != null) use.PlaySustainerOrSound(fixture.Props.useSustainer);
            use.WithProgressBarToilDelay(TargetIndex.A);
            if (fixture.Props.need == RPVisitDefOf.RP_Family)
                use.AddFinishAction(() =>
                {
                    // A call that got through (not cut off in the first seconds).
                    if (pawn.Inmate() is { } c && ticksLeftThisToil < fixture.Props.maxUseTicks - 250)
                        PhoneCalls.Notify_Call(pawn, c, tappable: true);
                });
            if (fixture.Props.need == RPDefOf.RP_Hygiene)
                use.AddFinishAction(() =>
                {
                    if (ticksLeftThisToil < fixture.Props.maxUseTicks - WashNotCutOffTicks) AutoTest.Notify_Wash(pawn);
                });
            if (fixture.Props.need == RPExerciseDefOf.RP_Exercise)
                use.AddFinishAction(() =>
                {
                    // A workout that was not cut off in the first seconds.
                    if (pawn.Inmate() is { } c && ticksLeftThisToil < fixture.Props.maxUseTicks - ExerciseUtility.MinWorkoutTicks) ExerciseUtility.Notify_Workout(pawn, c);
                });
            yield return use;
        }
    }
}
