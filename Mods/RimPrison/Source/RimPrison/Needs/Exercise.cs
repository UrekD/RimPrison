using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPExerciseDefOf
    {
        public static NeedDef RP_Exercise;
        public static ThingDef RP_WeightPlate;
        public static PrisonZoneDef RP_CommonRoom;

        static RPExerciseDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPExerciseDefOf));
    }

    /// <summary>
    /// Exercise: a need that falls through the day and is met on a weights bench (a <see cref="CompFixture"/>) in the yard or the common room
    /// during free time. Below <see cref="RestlessBelow"/> they are restless (tension, a small mood drain); the violent get tougher as they
    /// lift, and a plate off the rack is a weapon waiting for a fight.
    /// </summary>
    public static class ExerciseUtility
    {
        /// <summary>Below this the inmate is restless: a tension cause and a mood thought.</summary>
        public const float RestlessBelow = 0.2f;
        /// <summary>Tension per hour while restless.</summary>
        public const float RestlessTension = 1.2f;
        /// <summary>Free time goes to the weights once the need is below this, and then not always.</summary>
        public const float WorkoutBelow = 0.6f, WorkoutChance = 0.6f;
        /// <summary>A violent inmate who has lifted this many times may earn a name for it (<see cref="ToughChance"/> per workout after, up to <see cref="ToughShareCap"/> of the prison).</summary>
        public const int ToughAfterWorkouts = 6;
        public const float ToughChance = 0.2f;
        /// <summary>Lifting makes at most this share of the prison's inmates Tough (the name does not wear off, and it lowers the chance of subduing them): past it the bench toughens no more.</summary>
        public const float ToughShareCap = 0.25f;
        /// <summary>A tense inmate, out of sight, may walk off with a plate.</summary>
        public const float PlateChance = 0.06f, PlateTension = 25f;
        /// <summary>A workout shorter than this (ticks) was cut off and does not count.</summary>
        public const int MinWorkoutTicks = 300;

        public static Need NeedOf(Pawn p) => p.needs?.TryGetNeed(RPExerciseDefOf.RP_Exercise);

        public static bool Restless(Pawn p) => NeedOf(p) is { CurLevel: < RestlessBelow };

        /// <summary>Where free-time benches are used: the yard and the common room, never a staff area, a wing or a classroom.</summary>
        public static bool InBenchZone(PrisonZoneDef zone) => zone == RPDefOf.RP_Yard || zone == RPExerciseDefOf.RP_CommonRoom;

        /// <summary>Most Tough inmates lifting may make in a prison this size: a share of them, and one at least.</summary>
        public static int ToughCap(int inmates) => Mathf.Max(1, Mathf.FloorToInt(inmates * ToughShareCap));

        /// <summary>The prison already holds as many Tough inmates as lifting may make.</summary>
        public static bool ToughCapReached(Map map)
        {
            if (map == null) return true;
            int tough = 0, inmates = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { } c) continue;
                inmates++;
                if (c.HasReputation(ReputationDefOf.RP_Tough)) tough++;
            }
            return tough >= ToughCap(inmates);
        }

        /// <summary>A usable weights bench in the yard (or else the common room): the free-time zones, never a staff area.</summary>
        public static CompFixture FindBench(Pawn p) =>
            FixtureFinder.FindFor(p, RPExerciseDefOf.RP_Exercise, RPDefOf.RP_Yard) ?? FixtureFinder.FindFor(p, RPExerciseDefOf.RP_Exercise, RPExerciseDefOf.RP_CommonRoom);

        /// <summary>Free time: go and lift, when the need is low enough and the mood takes them.</summary>
        public static Job TryWorkout(Pawn p)
        {
            if (NeedOf(p) is not { CurLevel: < WorkoutBelow } || !Rand.Chance(WorkoutChance)) return null;
            CompFixture bench = FindBench(p);
            return bench == null ? null : FixtureFinder.UseJob(bench);
        }

        /// <summary>A workout was done: it is counted, the violent may get tougher for it, and a tense inmate with nobody watching may pocket a plate.</summary>
        public static void Notify_Workout(Pawn p, CompInmate c, bool? plate = null, bool? tough = null)
        {
            PrisonGame game = PrisonGame.Instance;
            game.stats.workouts++;
            c.workouts++;
            if (c.crime is { violent: true } && !c.HasReputation(ReputationDefOf.RP_Tough) && c.workouts >= ToughAfterWorkouts && (tough ?? Rand.Chance(ToughChance)) && !ToughCapReached(p.MapHeld))
            {
                c.reputations.Add(ReputationDefOf.RP_Tough);
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_BulkedUp".Translate(p.LabelShort), p);
            }
            if (c.tension >= PlateTension && p.Spawned && TensionUtility.GuardsNear(p, PrisonMap.Of(p.Map)) == 0 && (plate ?? Rand.Chance(PlateChance)))
                ContrabandUtility.Give(p, RPExerciseDefOf.RP_WeightPlate);
        }
    }
}
