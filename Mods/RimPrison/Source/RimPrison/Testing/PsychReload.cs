using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The psych wing in the saveload plan: an inmate with a record of breaks, a last look by the psychologist, hours without cover and a last dose is saved; the fields are in the snapshot of the round trip
    /// (<see cref="PsychDescribe"/>), and after the load they are what they were (and are put back to nothing).
    /// </summary>
    public static partial class AutoTest
    {
        private static int psychSeedId = -1;
        private static string psychSeedState;

        /// <summary>The scribed fields of psychiatric care, for the saveload snapshot.</summary>
        private static string PsychDescribe(CompInmate c) => $"psych={string.Join("+", c.breakTicks)}:{c.psychCheckTick}:{c.unmedicatedHours}:{c.lastDoseTick}:{c.lastBreakTick}:{c.psychTraitCase}:{c.psychFrom?.defName} ";

        private static void SeedPsychForReload(Map map)
        {
            psychSeedId = -1;
            Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true } ic && ic.security != null && !ic.security.escortOnly && ic.breakTicks.Count == 0);
            if (p == null)
            {
                reloadProblems.Add("saveload: no inmate to seed psychiatric care's fields on");
                return;
            }
            int now = Find.TickManager.TicksGame;
            CompInmate c = p.Inmate();
            c.breakTicks.Add(now - 3 * GenDate.TicksPerDay);
            c.breakTicks.Add(now - GenDate.TicksPerDay);
            (c.psychCheckTick, c.unmedicatedHours, c.lastDoseTick) = (now - 777, 5, now - 1234);
            (c.lastBreakTick, c.psychTraitCase, c.psychFrom) = (now - 4321, true, DefDatabase<SecurityLevelDef>.GetNamed("RP_Maximum"));
            (psychSeedId, psychSeedState) = (p.thingIDNumber, PsychDescribe(c));
            RPLog.Message($"[RimPrison:test] saveload psych: {p.LabelShort} has {psychSeedState}");
        }

        private static void CheckPsychAfterReload(Map map)
        {
            if (psychSeedId < 0) return;
            int id = psychSeedId;
            psychSeedId = -1;
            Pawn p = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == id);
            if (p?.Inmate() is not { } c)
            {
                reloadProblems.Add("saveload: the inmate with psychiatric care's fields did not survive the load");
                return;
            }
            if (PsychDescribe(c) != psychSeedState) reloadProblems.Add($"saveload: psychiatric care's fields changed across the load ({psychSeedState} -> {PsychDescribe(c)})");
            c.breakTicks.Clear();
            (c.psychCheckTick, c.unmedicatedHours, c.lastDoseTick) = (-999999, 0, -999999);
            (c.lastBreakTick, c.psychTraitCase, c.psychFrom) = (-999999, false, null);
        }
    }
}
