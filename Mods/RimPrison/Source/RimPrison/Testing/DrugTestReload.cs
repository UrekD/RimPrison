using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Drug testing in the saveload plan: the policy, the number of random tests, the day's counts, the hour's cap and a round under way (a doctor, an inmate, the reasons, begun) and the counters are put off their defaults, and
    /// an inmate gets a last drug use, a last test with its result, a random mark, a last failed test and a last-tested tick for each reason. The fields are in the snapshot of the round trip (<see cref="DrugTesting.Describe(PrisonGame)"/>, <see cref="DrugTesting.Describe(CompInmate)"/>), and after the load they are what they were
    /// (and are put back to nothing).
    /// </summary>
    public static partial class AutoTest
    {
        private static int drugSeedId = -1;
        private static string drugSeedState, drugSeedGameState;

        private static void SeedDrugTestForReload(Map map)
        {
            drugSeedId = -1;
            PrisonGame g = PrisonGame.Instance;
            int now = Find.TickManager.TicksGame;
            (g.drugTests, g.drugTestsPerDay) = (DrugTestPolicy.TargetedAndRandom, 4);
            (g.drugTestLog.day, g.drugTestLog.tests, g.drugTestLog.positives, g.drugTestLog.randoms, g.drugTestLog.yesterdayTests, g.drugTestLog.yesterdayPositives) = (Ledger.CurrentDay, 3, 2, 1, 5, 4);
            (g.stats.drugTests, g.stats.drugPositives) = (g.stats.drugTests + 7, g.stats.drugPositives + 3);
            Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true } ic && ic.lastTestResult == DrugTestResult.None);
            if (p == null)
            {
                drugSeedGameState = DrugTesting.Describe(g);
                reloadProblems.Add("saveload: no inmate to seed drug testing's fields on");
                return;
            }
            CompInmate c = p.Inmate();
            (c.lastDrugUseTick, c.lastTestedTick, c.lastFailedTestTick, c.lastTestResult, c.lastTestRandom) = (now - 1111, now - 2222, now - 3333, DrugTestResult.Dependence, true);
            // A round under way: the hour's count of tests begun and a doctor who has taken this inmate on (a random test, begun). (A doctor only: the claim is read back by id; no job is needed for it to be saved.)
            Pawn doctor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            (g.drugTestLog.hourKey, g.drugTestLog.started) = (now / GenDate.TicksPerHour, 2);
            if (doctor != null) g.drugTestLog.claims.Add(new DrugTestClaim { doctor = doctor, inmate = p, reasons = DrugTesting.ReasonRandom | DrugTesting.ReasonOverdose, begun = true });
            else reloadProblems.Add("saveload: no colonist to seed drug testing's round on");
            drugSeedGameState = DrugTesting.Describe(g);
            for (int i = 0; i < c.testedFor.Length; i++) c.testedFor[i] = now - 3000 - 100 * i;
            (drugSeedId, drugSeedState) = (p.thingIDNumber, DrugTesting.Describe(c));
            RPLog.Message($"[RimPrison:test] saveload drug testing: policy {drugSeedGameState}, {p.LabelShort} has {drugSeedState}");
        }

        private static void CheckDrugTestAfterReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            if (DrugTesting.Describe(g) != drugSeedGameState) reloadProblems.Add($"saveload: the drug testing policy and counts changed across the load ({drugSeedGameState} -> {DrugTesting.Describe(g)})");
            (g.drugTests, g.drugTestsPerDay) = (DrugTestPolicy.Off, DrugTesting.DefaultPerDay);
            g.drugTestLog = new DrugTestLog();
            (g.stats.drugTests, g.stats.drugPositives) = (0, 0);
            DrugTesting.ClearForTests();
            if (drugSeedId < 0) return;
            int id = drugSeedId;
            drugSeedId = -1;
            Pawn p = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == id);
            if (p?.Inmate() is not { } c)
            {
                reloadProblems.Add("saveload: the inmate with drug testing's fields did not survive the load");
                return;
            }
            if (DrugTesting.Describe(c) != drugSeedState) reloadProblems.Add($"saveload: drug testing's fields changed across the load ({drugSeedState} -> {DrugTesting.Describe(c)})");
            (c.lastDrugUseTick, c.lastTestedTick, c.lastFailedTestTick, c.lastTestResult, c.lastTestRandom, c.testedFor) = (DrugTesting.Never, DrugTesting.Never, DrugTesting.Never, DrugTestResult.None, false, DrugTesting.FreshTestedFor());
        }
    }
}
