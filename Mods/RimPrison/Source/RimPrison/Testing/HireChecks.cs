using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    public static partial class AutoTest
    {
        /// <summary>
        /// A hire is somebody who can do the work: when twenty candidates in a row cannot, there is no hire and no fee (it used to hire and charge for the twentieth).
        /// The appointees, the overseer and the riot police still get somebody (<see cref="StaffUtility.GenerateCandidate"/> keeps its fallback).
        /// </summary>
        private static void CheckHireFit(Map map)
        {
            Guarded("a hire is somebody who can do the work", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                StaffRoleDef role = RPSecurityDefOf.RP_Guard;
                if (!Unlocks.IsUnlocked(role)) { RPLog.Message("[RimPrison:test] hire fit: not checked, the guard role is locked"); return; }
                r.Add(() => StaffUtility.CandidateUnfit = null);
                if (g.ledger.funds < role.hireCost + 1000) g.ledger.funds = role.hireCost + 1000;
                int Guards() => map.mapPawns.FreeColonistsSpawned.Count(p => p.GetComp<CompStaff>()?.role == role);

                // Nobody fit: refused, not charged, nobody spawned.
                StaffUtility.CandidateUnfit = _ => true;
                int funds0 = g.ledger.funds, guards0 = Guards();
                var staff0 = map.mapPawns.FreeColonistsSpawned.ToList();
                bool hired = StaffUtility.Hire(map, role);
                // (One hired by mistake goes again with the check.)
                foreach (Pawn wrong in map.mapPawns.FreeColonistsSpawned.Where(p => !staff0.Contains(p)).ToList()) r.Add(() => { if (!wrong.Destroyed) wrong.Destroy(DestroyMode.Vanish); });
                if (hired || g.ledger.funds != funds0 || Guards() != guards0)
                    problems0.Add($"hire fit: with no candidate able to do the work the hire went through {hired}, funds {funds0} -> {g.ledger.funds}, guards {guards0} -> {Guards()}");
                if (StaffUtility.TryGenerateCandidate(role, out Pawn last) || last == null)
                    problems0.Add($"hire fit: the generator said a candidate was fit with every one marked unfit, or gave nobody back ({last?.LabelShort ?? "null"})");
                if (StaffUtility.GenerateCandidate(role) == null) problems0.Add("hire fit: the appointees' generator gave nobody when no candidate was fit (it must fall back to the last one)");

                // Somebody fit: hired once, charged once.
                StaffUtility.CandidateUnfit = null;
                var before = map.mapPawns.FreeColonistsSpawned.ToList();
                funds0 = g.ledger.funds;
                hired = StaffUtility.Hire(map, role);
                Pawn made = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !before.Contains(p));
                if (made != null) r.Add(() => { if (!made.Destroyed) made.Destroy(DestroyMode.Vanish); });
                if (!hired || made == null || g.ledger.funds != funds0 - role.hireCost || made.GetComp<CompStaff>()?.role != role)
                    problems0.Add($"hire fit: an ordinary hire went through {hired}, made {made?.LabelShort ?? "nobody"} as {made?.GetComp<CompStaff>()?.role?.defName ?? "no role"}, funds {funds0} -> {g.ledger.funds} (fee {role.hireCost})");
                RPLog.Message("[RimPrison:test] hire fit: no fit candidate is no hire and no fee; an ordinary hire is one guard and one fee");
            });
        }
    }
}
