using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the balance pass (WORKLOG "Balance"): each fails on the numbers and rules it replaced. Self-contained, on throwaway inmates, rolled back.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckBalanceRegressions(Map map)
        {
            CheckTunnelsTakeDays(map);
            CheckRegimesFeedEveryoneOften();
            CheckCompliancePlacesWorkAndPrograms(map);
            CheckMealsReachTheStranded();
            CheckWatchedNeedsNoMap(map);
        }

        /// <summary>An inmate who is being carried (not on the map) is asked whether they are watched: the answer is no (a carried inmate is on no tower's or camera's map), not an exception (7-day runs failed with a NullReferenceException in the line of sight, once a doctor carried one).</summary>
        private static void CheckWatchedNeedsNoMap(Map map)
        {
            Guarded("watched while carried", map, r =>
            {
                Pawn guard = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed("RP_Guard"));
                Pawn p = Throwaway(map, r);
                if (!guard.Spawned || !p.Spawned) return;
                IntVec3 near = guard.Position;
                p.DeSpawn();
                p.Position = near;
                PrisonMap pm = PrisonMap.Of(map);
                if (!pm.Guards.Contains(guard))
                {
                    pm.Guards.Add(guard);
                    r.Add(() => pm.Guards.Remove(guard));
                }
                // (The tower or camera that covered the cell they were lifted from does not see them now: no answer from a stale position either.)
                try
                {
                    if (TensionUtility.Watched(p, pm)) problems0.Add("watched: an inmate who is not on the map is watched");
                    if (TensionUtility.Watchers(p, pm) > 0f) problems0.Add($"watched: an inmate who is not on the map has {TensionUtility.Watchers(p, pm)} watchers");
                }
                catch (Exception e) { problems0.Add($"watched: asking whether a carried inmate is watched threw {e.GetType().Name}"); }
            });
        }

        /// <summary>The meal is carried to someone with no meal to walk to wherever they stand (an inmate with no bed stood in the supermax wing's corridor for days and starved), not to a free one in a corridor.</summary>
        private static void CheckMealsReachTheStranded()
        {
            if (!WorkGiver_DeliverCellMeal.ToBeBroughtOne(false, false, () => true)) problems0.Add("meals: an inmate with no meal to walk to and no cell to be in is not brought one");
            if (!WorkGiver_DeliverCellMeal.ToBeBroughtOne(true, true, () => false)) problems0.Add("meals: the condemned in their cell are not brought a meal");
            if (WorkGiver_DeliverCellMeal.ToBeBroughtOne(false, true, () => false)) problems0.Add("meals: a free inmate with a canteen to walk to is brought a meal in their cell");
            if (WorkGiver_DeliverCellMeal.ToBeBroughtOne(true, false, () => false)) problems0.Add("meals: a punished inmate who is not in a cell, with a canteen to walk to, is brought a meal where they stand");
        }

        /// <summary>
        /// A tunnel is a project of days: three days of digging every lockup hour (24 a day, which is how long death row's cells are locked) do not finish it, even for an escape artist, and one who digs ten hours a night is
        /// not half way. (It used to take two to four nights, and finished inside the showcase plan's three days in two runs of six.) The guards can notice a deep one without a tip, never a shallow one.
        /// </summary>
        private static void CheckTunnelsTakeDays(Map map)
        {
            Guarded("tunnels take days", map, r =>
            {
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                // (A made-up inmate can come with the escape artist's reputation, which doubles the digging: the plain digger is one with none.)
                c.reputations.Clear();
                float plain = 0f, artist = 0f, tenANight = 0f;
                Rand.PushState(20261020);
                try
                {
                    for (int h = 0; h < 72; h++) plain += TunnelSystem.DigThisHour(c);
                    for (int h = 0; h < 72; h++) tenANight += h % 24 < 10 ? TunnelSystem.DigThisHour(c) : 0f;
                    c.reputations.Add(ReputationDefOf.RP_EscapeArtist);
                    for (int h = 0; h < 72; h++) artist += TunnelSystem.DigThisHour(c);
                }
                finally
                {
                    Rand.PopState();
                }
                RPLog.Message($"[RimPrison:test] tunnels: after 3 days of lockup digging a plain digger is at {plain:F2}, an escape artist at {artist:F2}; at 10 hours a night (plain) {tenANight:F2}");
                if (artist >= 1f) problems0.Add($"tunnels: an escape artist finished a tunnel in three days of digging ({artist:F2})");
                if (tenANight >= 0.4f) problems0.Add($"tunnels: ten hours a night for three days dug {tenANight:F2} of a tunnel (a project of days, < 0.4 expected)");
                if (TunnelSystem.NoticeChance(TunnelSystem.NoticedAbove - 0.01f) > 0f) problems0.Add("tunnels: a shallow tunnel can be noticed by the guards");
                if (TunnelSystem.NoticeChance(0.9f) <= TunnelSystem.NoticeChance(0.3f)) problems0.Add("tunnels: a deeper tunnel is no easier to notice than a shallow one");
            });
        }

        /// <summary>
        /// A downed member of staff has a medical bed to be carried to (the penitentiary's hospital beds were all set for prisoners, which a colonist cannot use: a downed workman lay where he fell for a day
        /// and bled out, and downed cooks left the kitchen without hands). Only the penitentiary has medical beds.
        /// </summary>
        private static void CheckDownedStaffHaveABed(Map map)
        {
            if (PrisonBuilder.Kind != Facility.Penitentiary) return;
            Guarded("a downed member of staff has a bed", map, r =>
            {
                Pawn cook = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed("RP_Cook"));
                Pawn guard = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed("RP_Guard"));
                HealthUtility.DamageUntilDowned(cook);
                Building_Bed bed = RestUtility.FindBedFor(cook, guard, false, true, cook.GuestStatus); // (ignoring reservations: a real patient or a guard heading to a bed at the start of a run must not fail the check)
                foreach (Building_Bed b in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().Where(x => x.Medical))
                    RPLog.Message($"[RimPrison:test] medical bed at {b.Position - PrisonBuilder.Origin(map)}: owner type {b.ForOwnerType}, room cell={b.GetRoom()?.IsPrisonCell}, valid for the cook {RestUtility.IsValidBedFor(b, cook, guard, false, false, true, null)}, can reserve {ReservationUtility.CanReserve(guard, b, b.SleepingSlotsCount, 0, null, false)}, cook downed {cook.Downed}, needs tend {cook.health.HasHediffsNeedingTend()}, reachable {guard.CanReach(b, Verse.AI.PathEndMode.OnCell, Danger.Deadly)}");
                if (bed == null || !bed.Medical || bed.ForPrisoners) problems0.Add($"staff beds: a downed cook has no medical bed of the staff's to be rescued to (found {bed?.def.defName} at {bed?.Position})");
            });
        }

        /// <summary>
        /// Everyone who walks to the canteen has a meal slot at least every 11 hours: food falls 0.067 an hour from a full stomach, so a pawn is urgently hungry (< 15%) after 12.75 hours and starving
        /// after 15 (protective custody had Eat at 08 and 17, and its six inmates were urgently hungry every morning; the other three eat at 07, 12 and 19 and went 12 hours between supper and breakfast,
        /// which left every well inmate urgently hungry in the small hours).
        /// </summary>
        private static void CheckRegimesFeedEveryoneOften()
        {
            foreach (SecurityLevelDef level in DefDatabase<SecurityLevelDef>.AllDefsListForReading)
            {
                if (level.cellMeals) continue;
                List<int> eats = Enumerable.Range(0, 24).Where(h => level.defaultRegime.Count > h && level.defaultRegime[h]?.defName == "RP_Eat").ToList();
                if (eats.Count == 0) continue; // (a level that is never out: its own rules feed it)
                int gap = 0;
                for (int i = 0; i < eats.Count; i++)
                {
                    int between = (eats[(i + 1) % eats.Count] - eats[i] + 24) % 24;
                    gap = Math.Max(gap, between == 0 ? 24 : between);
                }
                if (gap > 11) problems0.Add($"regime: {level.defName} eats {eats.Count} times a day and goes {gap} hours between two meals (> 11)");
            }
        }

        /// <summary>
        /// The regime's own places count as in place: a job holder at their job's zone in the work hours (the Work slot lists no zones, and used to expect the cell), a class or a service, a hearing, a
        /// patient in a medical bed. Someone with a job who is elsewhere, in the work hours, is still out of place.
        /// </summary>
        private static void CheckCompliancePlacesWorkAndPrograms(Map map)
        {
            Guarded("compliance counts the regime's own places", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                InmateJobDef job = DefDatabase<InmateJobDef>.AllDefsListForReading.FirstOrDefault(j => j.zone != null && j.requiredProgram == null && pm.zones.Any(j.zone));
                if (job == null) { RPLog.Message("[RimPrison:test] compliance check: no job with a zone in this prison, skipped"); return; }
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                c.processed = true;
                c.forcedActivity = null;
                c.job = job;
                void Put(IntVec3 cell)
                {
                    p.jobs?.StopAll();
                    if (p.Spawned) p.DeSpawn();
                    GenSpawn.Spawn(p, cell, map);
                }
                IntVec3 atWork = pm.zones.CellsOf(job.zone).FirstOrDefault(x => x.Standable(map));
                IntVec3 yard = pm.zones.CellsOf(RPDefOf.RP_Yard).FirstOrDefault(x => x.Standable(map) && pm.zones.ZoneAt(x) != job.zone);
                if (!atWork.IsValid || !yard.IsValid) { RPLog.Message("[RimPrison:test] compliance check: no cell to stand in, skipped"); return; }
                Put(atWork);
                if (!RegimePlacement.InExpectedPlace(p, RPLaborDefOf.RP_Work, pm)) problems0.Add($"compliance: a {job.defName} holder at their job's zone in the work hours is out of place");
                Put(yard);
                if (RegimePlacement.InExpectedPlace(p, RPLaborDefOf.RP_Work, pm)) problems0.Add($"compliance: a {job.defName} holder in the yard in the work hours counts as in place");
                // Down (hurt): where they lie is not a failing of theirs, nor a point for the prison: they are left out of the reading (counted as in place, the fights that down them paid the order grade).
                if (!RegimePlacement.InTheSample(p)) problems0.Add("compliance: an inmate on their feet is left out of the compliance reading");
                HealthUtility.DamageUntilDowned(p);
                if (RegimePlacement.InTheSample(p)) problems0.Add("compliance: a downed inmate is counted in the compliance reading (as in place or out of it)");
            });
        }
    }
}
