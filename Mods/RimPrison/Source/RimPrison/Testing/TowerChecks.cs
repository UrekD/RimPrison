using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>A guard on a tower gives way to an emergency that is no threat to them, a fight between inmates (the tower job was a plain wait of 2500 ticks; vanilla's flee or attack answers only a threat to the guard, from any job).</summary>
    public static partial class AutoTest
    {
        private static void CheckTowerGivesWayToAnEmergency(Map map)
        {
            Guarded("a guard on a tower gives way to an emergency", map, r =>
            {
                void Fail(string what) => problems0.Add("tower: " + what);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (!guard.Spawned || GuardUtility.AlarmFor(guard) || map.mapPawns.PrisonersOfColonySpawned.Any(x => x.InMentalState))
                {
                    RPLog.Message("[RimPrison:test] tower emergency: the guard is not on site or an alarm is already on, skipped");
                    return;
                }
                // A tower built where the guard stands (open ground a few cells round), manned by the guard through the real work giver.
                ThingDef def = RPArmedDefOf.RP_GuardTower;
                IntVec3 at = GenRadial.RadialCellsAround(guard.Position, 12f, true).FirstOrDefault(c => c.InBounds(map) && GenAdj.OccupiedRect(c, Rot4.North, def.size).Cells.All(x => x.InBounds(map) && x.Standable(map)
                    && x.GetEdifice(map) == null && x.GetFirstItem(map) == null && x.GetFirstPawn(map) == null && guard.CanReach(x, PathEndMode.OnCell, Danger.Some)));
                if (!at.IsValid || at == default)
                {
                    Fail("the check's own premise: no open ground near the guard for a tower");
                    return;
                }
                Thing tower = GenSpawn.Spawn(ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null), at, map);
                tower.SetFaction(Faction.OfPlayer);
                r.Add(() =>
                {
                    if (!tower.Destroyed) tower.Destroy();
                });
                var giver = new WorkGiver_ManTower { def = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(w => w.giverClass == typeof(WorkGiver_ManTower)) };
                Job man = giver.JobOnThing(guard, tower);
                // (Started as the work scan would start it, not as an order: an ordered job is the player's and nothing overrides it.)
                if (man == null)
                {
                    Fail("the check's own premise: the guard could not be given the tower");
                    return;
                }
                guard.jobs.StartJob(man, JobCondition.InterruptForced);
                int ticks = 0;
                while (ticks < 1500 && !(guard.CurJobDef == RPArmedDefOf.RP_ManTower && guard.Position.IsInside(tower)))
                {
                    Find.TickManager.DoSingleTick();
                    ticks++;
                }
                if (guard.CurJobDef != RPArmedDefOf.RP_ManTower)
                {
                    RPLog.Message($"[RimPrison:test] tower emergency: the guard did not get on the tower ({guard.CurJobDef?.defName}), skipped");
                    return;
                }
                // Two inmates fighting each other some cells off: no threat to the guard (vanilla's own flee or attack answers only a threat to them, from any job), an inmate acting out the guards answer.
                IntVec3 near = GenRadial.RadialCellsAround(tower.Position, 12f, false).FirstOrDefault(c => c.InBounds(map) && c.Standable(map) && c.GetFirstPawn(map) == null && c.DistanceTo(tower.Position) >= 8f
                    && GenAdjFast.AdjacentCells8Way(c).Any(x => x.InBounds(map) && x.Standable(map) && x.GetFirstPawn(map) == null) && guard.CanReach(c, PathEndMode.Touch, Danger.Deadly));
                if (!near.IsValid || near == default)
                {
                    RPLog.Message("[RimPrison:test] tower emergency: no free cells some way off the tower, skipped");
                    return;
                }
                IntVec3 near2 = GenAdjFast.AdjacentCells8Way(near).First(x => x.InBounds(map) && x.Standable(map) && x.GetFirstPawn(map) == null);
                Pawn rowdy = Throwaway(map, r), other = Throwaway(map, r);
                foreach ((Pawn p, IntVec3 c) in new[] { (rowdy, near), (other, near2) })
                {
                    r.Add(() => p.mindState.mentalStateHandler.Reset());
                    p.pather?.StopDead();
                    p.Position = c;
                    p.Notify_Teleported();
                }
                rowdy.interactions.StartSocialFight(other);
                if (!rowdy.InMentalState)
                {
                    RPLog.Message("[RimPrison:test] tower emergency: the fight did not start, skipped");
                    return;
                }
                bool alarm = GuardUtility.AlarmFor(guard);
                int waited = 0;
                while (waited < 600 && guard.CurJobDef == RPArmedDefOf.RP_ManTower)
                {
                    Find.TickManager.DoSingleTick();
                    waited++;
                }
                RPLog.Message($"[RimPrison:test] tower emergency: alarm {alarm}, the guard's job after {waited} ticks: {guard.CurJobDef?.defName ?? "none"}");
                if (alarm && guard.CurJobDef == RPArmedDefOf.RP_ManTower) Fail($"a guard on a tower did not leave it for two inmates fighting {near.DistanceTo(tower.Position):0} cells off in {waited} ticks (the alarm is on for them)");
                else if (!alarm) RPLog.Message("[RimPrison:test] tower emergency: no alarm for the guard (the check's premise), not judged");
            });
        }
    }
}
