using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Supermax in the saveload plan: the game is saved with two guards in the middle of a walk (one has led a supermax inmate out of the cell towards the yard, one is walking another, who was left outside
    /// the cell, home), and after the load the walks go on: the walk is not saved, the guards' jobs are, and each toil makes it again (<see cref="JobDriver_EscortInmate"/>). The inmates are in the snapshot
    /// of the round trip like everybody; the unit they walk in is taken down after the check.
    /// </summary>
    public static partial class AutoTest
    {
        private static IntVec3 supermaxSeedOrigin = IntVec3.Invalid;
        private static int supermaxSeedGuardA = -1, supermaxSeedGuardB = -1, supermaxSeedInmateA = -1, supermaxSeedInmateB = -1, supermaxSeedSpare = -1;
        private static List<RegimeActivityDef> supermaxSeedRegime;
        /// <summary>What the first look after the load found (before anything ticks): the walks are not in the inmates' records and the guards' jobs are on.</summary>
        private static string supermaxAtLoad;

        /// <summary>Before the save: the unit, and two walks half done (the game is ticked until both are, which is a few hundred ticks).</summary>
        private static void SeedSupermaxForReload(Map map)
        {
            supermaxSeedOrigin = IntVec3.Invalid;
            if (RPSupermaxDefOf.RP_Supermax == null) return;
            var r = new Rollback(map);
            SupermaxScene s = SupermaxScene.Build(map, r, "saveload");
            if (s == null)
            {
                reloadProblems.Add("saveload: no open ground for a supermax unit to seed a walk in");
                return;
            }
            supermaxSeedOrigin = s.origin;
            PrisonGame g = s.g;
            RegimeSchedule schedule = g.RegimeFor(s.level);
            supermaxSeedRegime = schedule.slots.ToList();
            for (int h = 0; h < 24; h++) schedule.Set(h, RPSupermaxDefOf.RP_YardAlone);
            Pawn guardA = s.Guard("Zsavega", s.CorridorWest), guardB = s.Guard("Zsavegb", s.At(14, 1));
            Pawn a = s.Inmate("Zsavea", s.bedA, s.CellA), b = s.Inmate("Zsaveb", s.bedB, s.CellB);
            s.pm.ForceDoorUpdate();
            map.reachability.ClearCache();
            Job walk = SupermaxOffer(s, guardA, a, "saveload: the yard walk", EscortKind.Yard);
            if (walk == null) return;
            s.Start(guardA, walk);
            // The first walker is out of the cell and on the way (lead toil); then the second is left outside, a way off, and a guard sent to take them home.
            int ticked = ShuttleTicks(() => a.Inmate().leg is { phase: EscortPhase.Out } && !s.InCell(a, s.bedA) && (a.Position - s.CellA).LengthHorizontal > 4f, 1200);
            if (a.Inmate().leg == null)
            {
                reloadProblems.Add($"saveload: the first supermax walk did not set out ({ticked} ticks)");
                return;
            }
            s.Place(b, s.At(11, 1));
            // (Not offered, a problem is noted by the offer: the first walk is checked alone.)
            Job home = SupermaxOffer(s, guardB, b, "saveload: the walk home", HomeWalk);
            if (home != null) s.Start(guardB, home);
            // (Both on the way: the walk of the first is the guard's lead toil, the second's the walk home, toil 2 of its job.)
            bool BothOn() => guardA.jobs.curDriver?.CurToilIndex == 2 && a.Inmate().leg != null && (home == null || guardB.jobs.curDriver?.CurToilIndex == 2 && b.Inmate().leg != null);
            ShuttleTicks(BothOn, 600);
            if (!BothOn())
            {
                reloadProblems.Add($"saveload: the two supermax walks were not both on the way at the save (toils {guardA.jobs.curDriver?.CurToilIndex} and {guardB.jobs.curDriver?.CurToilIndex}, legs {a.Inmate().leg != null} and {b.Inmate().leg != null})");
                return;
            }
            (supermaxSeedGuardA, supermaxSeedGuardB, supermaxSeedInmateA, supermaxSeedInmateB, supermaxSeedSpare) = (guardA.thingIDNumber, home == null ? -1 : guardB.thingIDNumber, a.thingIDNumber, b.thingIDNumber, guardB.thingIDNumber);
            RPLog.Message($"[RimPrison:test] saveload supermax: walks half done, {guardA.LabelShort} has {a.LabelShort} (lead, at {a.Position - s.origin}), {(home == null ? "nobody is" : guardB.LabelShort + " is")} taking {b.LabelShort} home (at {b.Position - s.origin})");
        }

        private static Pawn SupermaxSeedPawn(Map map, int id) => id < 0 ? null : map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == id);

        /// <summary>First thing after the load, before any tick: no walk in either inmate's record (it is not saved), the guards still on their jobs (they are).</summary>
        private static void RecordSupermaxAtLoad(Map map)
        {
            supermaxAtLoad = null;
            if (!supermaxSeedOrigin.IsValid) return;
            Pawn guardA = SupermaxSeedPawn(map, supermaxSeedGuardA), guardB = SupermaxSeedPawn(map, supermaxSeedGuardB), a = SupermaxSeedPawn(map, supermaxSeedInmateA), b = SupermaxSeedPawn(map, supermaxSeedInmateB);
            if (guardA == null || a == null || b == null) return;
            bool noWalk = a.Inmate().leg == null && (guardB == null || b.Inmate().leg == null);
            bool jobsOn = guardA.CurJob?.def == RPSupermaxDefOf.RP_EscortInmate && (guardB == null || guardB.CurJob?.def == RPSupermaxDefOf.RP_EscortInmate);
            supermaxAtLoad = $"{noWalk}/{jobsOn}";
        }

        /// <summary>
        /// After the load (and the comparison of the state): the guards are still on their jobs, the first beat finds no walk and makes it again, the first walker gets to the yard and back, the second is
        /// home, and nobody walked alone. Then the unit, the pawns and the regime are put away.
        /// </summary>
        private static void CheckSupermaxAfterReload(Map map)
        {
            if (!supermaxSeedOrigin.IsValid) return;
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            IntVec3 origin = supermaxSeedOrigin;
            supermaxSeedOrigin = IntVec3.Invalid;
            Pawn guardA = SupermaxSeedPawn(map, supermaxSeedGuardA), guardB = SupermaxSeedPawn(map, supermaxSeedGuardB), a = SupermaxSeedPawn(map, supermaxSeedInmateA), b = SupermaxSeedPawn(map, supermaxSeedInmateB);
            try
            {
                if (guardA == null || a == null || b == null)
                {
                    reloadProblems.Add("saveload: the supermax walkers did not survive the load");
                    return;
                }
                CompInmate ac = a.Inmate(), bc = b.Inmate();
                bool hasB = guardB != null;
                bool noWalkYet = supermaxAtLoad == "True/True", onJobs = supermaxAtLoad is "True/True" or "False/True";
                for (int i = 0; i < 4; i++) Find.TickManager.DoSingleTick();
                bool madeAgain = ac.leg is { phase: EscortPhase.Out } && (!hasB || bc.leg is { phase: EscortPhase.Back });
                bool stillOn = guardA.CurJob?.def == RPSupermaxDefOf.RP_EscortInmate && (!hasB || guardB.CurJob?.def == RPSupermaxDefOf.RP_EscortInmate);
                RPLog.Message($"[RimPrison:test] saveload supermax: right after the load (no walk, jobs kept: {supermaxAtLoad}); four ticks later walks made again {madeAgain}, jobs on {stillOn}");
                if (!noWalkYet || !onJobs || !madeAgain || !stillOn)
                    reloadProblems.Add($"supermax walks across a load: no walk in the record just after the load {noWalkYet}, the guards' jobs kept {onJobs}, the walks made again by the first beat {madeAgain} (outbound {ac.leg?.phase}, home {bc.leg?.phase}), the jobs still on {stillOn} (guards doing {guardA.CurJobDef?.defName} and {guardB?.CurJobDef?.defName})");
                // The first walker gets to the yard (and the stay is not waited out), the second is seen in; then the first is walked back.
                bool inYard = false;
                int ticked = ShuttleTicks(() =>
                {
                    inYard |= ac.leg is { arrivedTick: >= 0 } && pm.zones.ZoneAt(a.Position) == RPSupermaxDefOf.RP_SupermaxYard;
                    return inYard;
                }, 2500);
                if (!inYard) reloadProblems.Add($"supermax walks across a load: the first walker never got to the yard ({ticked} ticks, at {a.Position - origin}, leg {ac.leg?.phase}, guard doing {guardA.CurJobDef?.defName})");
                if (ac.leg is { } leg) leg.done = true;
                ticked = ShuttleTicks(() => guardA.CurJob?.def != RPSupermaxDefOf.RP_EscortInmate && (!hasB || guardB.CurJob?.def != RPSupermaxDefOf.RP_EscortInmate) && ac.leg == null && bc.leg == null, 3000);
                bool aHome = InmateUtility.CellOf(a) is { } cellA && a.GetRoom() == cellA, bHome = !hasB || InmateUtility.CellOf(b) is { } cellB && b.GetRoom() == cellB;
                if (!aHome || !bHome || ac.leg != null || bc.leg != null)
                    reloadProblems.Add($"supermax walks across a load: after {ticked} more ticks the first walker is home {aHome} and the second {bHome}, walks still on {ac.leg != null}/{bc.leg != null}, guards doing {guardA.CurJobDef?.defName}/{guardB?.CurJobDef?.defName}");
                else RPLog.Message("[RimPrison:test] saveload supermax: the walks finished after the load, the inmates in their cells");
            }
            finally
            {
                TakeDownSupermaxSeed(map, pm, g, origin, new[] { guardA, SupermaxSeedPawn(map, supermaxSeedSpare) }, new[] { a, b });
            }
        }

        /// <summary>The unit of <see cref="SeedSupermaxForReload"/> and everything in it, after the load: walls, doors, beds, the shower, the roof and the zones painted, the throwaway pawns, the regime.</summary>
        private static void TakeDownSupermaxSeed(Map map, PrisonMap pm, PrisonGame g, IntVec3 origin, Pawn[] guards, Pawn[] inmates, bool restoreRegime = true)
        {
            foreach (Pawn p in inmates.Concat(guards))
            {
                if (p == null || p.Destroyed) continue;
                if (p.Inmate() is { } c) EscortWalk.End(p, c);
                if (p.GetComp<CompStaff>() != null) g.home.Remove(p);
                p.Destroy(DestroyMode.Vanish);
            }
            StaffCareers.InvalidateLeads();
            var rect = new CellRect(origin.x, origin.z, SupermaxScene.Width, SupermaxScene.Height);
            foreach (IntVec3 c in rect)
            {
                foreach (Thing t in c.GetThingList(map).ToList())
                    if (t.def.category is ThingCategory.Building or ThingCategory.Item && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
                if (pm.zones.ZoneAt(c) != null) pm.zones.Set(c, null);
            }
            foreach (IntVec3 c in rect) map.roofGrid.SetRoof(c, null);
            // (Not for another seed's unit: the regime is the supermax seed's, in force until its own check has run.)
            if (restoreRegime && supermaxSeedRegime != null)
            {
                RegimeSchedule schedule = g.RegimeFor(RPSupermaxDefOf.RP_Supermax);
                for (int h = 0; h < 24; h++) schedule.Set(h, supermaxSeedRegime[h]);
                supermaxSeedRegime = null;
            }
            map.reachability.ClearCache();
            pm.ForceDoorUpdate();
            InmateCandidates.Shared.Invalidate();
        }
    }
}
