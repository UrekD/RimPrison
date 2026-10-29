using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Unit officers (<see cref="UnitOfficers"/>, gap analysis 7, item 2). A made-up scene (<see cref="UnitScene"/>): a throwaway guard and three throwaway inmates on each of two wings (medium and maximum security), all
    /// standing round the guard, the tips and the hourly presence confined to them (<see cref="InformantSystem.TipPool"/>, <see cref="UnitOfficers.PresencePool"/>), the state's chief held off (<see cref="UnitOfficers.ChiefOverride"/>) and the daily tip off unless a check wants it
    /// (<see cref="UnitOfficers.TipRoll"/>). Naming sets the patrol, rotation starts the days again and keeps the old patrol to put back, a second guard replaces the first, a patrol moved takes them off; five forced days
    /// of presence give the officer's wing <see cref="UnitOfficers.TrustPerDay"/> a day each and the other wing nothing, never past the ceiling, nothing to a grudge, and nothing while the chief deploys the guards; the
    /// corruption lean at day 5 is <see cref="UnitOfficers.LeanPerDay"/> times five over day 0 and rotation sets it back; the complaint factor, and none for an inmate with a grudge; the forced staff tip names the seeded
    /// hidden debt on the officer's wing and never the other wing's, comes as a letter from an officer at home at midnight too, keeps an informant's earlier tip on file, and says nothing of a corrupt officer's patron or
    /// their gang; a guard kept off the wing by kin is refused with the reason (the menu greys them); leave and suspension keep the days with the slot vacant and stop them growing, retraining and firing clear them, and of
    /// two holders of a wing the earlier named goes. Checks that need days in post move the clock a week on first (<see cref="TaskClockForward"/>, put back by the rollback). The saveload plan's seed is at the end. Written
    /// without a game run.
    /// </summary>
    public static partial class AutoTest
    {
        private static void UnitFail(string check, string what) => problems0.Add($"unit officer ({check}): {what}");

        private static void CheckUnitOfficers(Map map)
        {
            CheckUnitNaming(map);
            CheckUnitKin(map);
            CheckUnitTrust(map);
            CheckUnitLean(map);
            CheckUnitComplaints(map);
            CheckUnitTip(map);
            CheckUnitVacate(map);
            CheckUnitScreens(map);
        }

        /// <summary>The made-up guard and the inmates of the two wings.</summary>
        private sealed class UnitScene
        {
            public Pawn guard;
            public CompStaff sc;
            public SecurityLevelDef a, b;
            public readonly List<Pawn> onA = new List<Pawn>(), onB = new List<Pawn>(), cast = new List<Pawn>();
        }

        /// <summary>
        /// The scene, put back by the rollback (and every live guard's unit post and patrol as it was, should a real one hold a slot the check takes); null (a problem noted) when a premise did not take.
        /// </summary>
        private static UnitScene MakeUnitScene(Map map, Rollback r, string check, int perWing = 3)
        {
            var s = new UnitScene { a = RPSecurityDefOf.RP_Medium, b = RPSecurityDefOf.RP_Maximum };
            if (UnitOfficers.WingZone(s.a) == null || UnitOfficers.WingZone(s.b) == null)
            {
                UnitFail(check, $"premise: no wing zone for {s.a?.defName} ({UnitOfficers.WingZone(s.a)?.defName}) or {s.b?.defName} ({UnitOfficers.WingZone(s.b)?.defName})");
                return null;
            }
            var live = StaffCommute.AllStaff(map).Select(p => (p, p.GetComp<CompStaff>())).Where(x => x.Item2 != null).Select(x => (x.p, x.Item2, x.Item2.unitWing, x.Item2.unitSinceTick, x.Item2.unitPrevZone, x.Item2.patrolZone)).ToList();
            System.Func<Pawn, bool> pool0 = InformantSystem.TipPool;
            r.Add(() =>
            {
                InformantSystem.TipPool = pool0;
                UnitOfficers.ChiefOverride = null;
                UnitOfficers.TipRoll = null;
                UnitOfficers.PresencePool = null;
                foreach ((Pawn _, CompStaff c, SecurityLevelDef wing, int since, PrisonZoneDef prev, PrisonZoneDef patrol) in live) (c.unitWing, c.unitSinceTick, c.unitPrevZone, c.patrolZone) = (wing, since, prev, patrol);
            });
            UnitOfficers.ChiefOverride = false;
            UnitOfficers.TipRoll = false;
            InformantSystem.TipPool = p => s.cast.Contains(p);
            UnitOfficers.PresencePool = p => s.cast.Contains(p);
            s.guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
            s.sc = s.guard.GetComp<CompStaff>();
            r.Add(() => UnitOfficers.Unassign(s.guard, s.sc));
            Room room = s.guard.GetRoom();
            List<IntVec3> spots = GenRadial.RadialCellsAround(s.guard.Position, 3f, false).Where(c => c.InBounds(map) && c.Standable(map) && c.GetRoom(map) == room).ToList();
            for (int i = 0; i < perWing * 2; i++)
            {
                SecurityLevelDef level = i < perWing ? s.a : s.b;
                Pawn p = TaskClean(Throwaway(map, r, level));
                p.Inmate().security = level;
                if (i < spots.Count) TaskTeleport(p, spots[i]);
                (i < perWing ? s.onA : s.onB).Add(p);
                s.cast.Add(p);
            }
            bool allNear = s.cast.All(p => StaffMemories.Near(s.guard, p, room));
            if (room == null || !allNear || !s.guard.Awake() || !StaffCareers.Available(s.sc) || s.cast.Any(p => !InmateUtility.IsActiveInmate(p)) || s.sc.unitWing != null)
            {
                UnitFail(check, $"premise: the scene did not take (room {room != null}, {spots.Count} spots, all near the guard {allNear}, guard awake {s.guard.Awake()}, available {StaffCareers.Available(s.sc)}, inmates active {s.cast.Count(InmateUtility.IsActiveInmate)}/{s.cast.Count}, already named {s.sc.unitWing?.defName})");
                return null;
            }
            return s;
        }

        private static float UnitMean(List<Pawn> inmates, Pawn guard) => inmates.Average(p => StaffMemories.ScoreFor(p.Inmate(), guard));

        /// <summary>A zone inmates go to that is no wing (the patrol to put back).</summary>
        private static PrisonZoneDef UnitOtherZone() => DefDatabase<PrisonZoneDef>.AllDefsListForReading.FirstOrDefault(z => z.inmateAccess && z.security == null);

        // ---- naming, rotation, replacement, the patrol ---------------------------------------------------------------------------------------------------

        private static void CheckUnitNaming(Map map)
        {
            const string check = "naming";
            Guarded("unit officer: naming, rotation and the patrol put back", map, r =>
            {
                UnitScene s = MakeUnitScene(map, r, check, perWing: 1);
                if (s == null) return;
                int now = TaskClockForward(r);
                PrisonZoneDef before = UnitOtherZone();
                s.sc.patrolZone = before;
                PrisonZoneDef zoneA = UnitOfficers.WingZone(s.a), zoneB = UnitOfficers.WingZone(s.b);
                string why = UnitOfficers.Assign(s.guard, s.a, message: false);
                if (why != null || s.sc.unitWing != s.a || s.sc.unitSinceTick != now || s.sc.patrolZone != zoneA || s.sc.unitPrevZone != before || UnitOfficers.HolderOf(map, s.a) != s.guard)
                    UnitFail(check, $"named to {s.a.defName}: refused \"{why}\", {UnitOfficers.Describe(s.sc)}, patrol {s.sc.patrolZone?.defName} (want {zoneA.defName}, since {now}, old patrol {before?.defName} kept), holder {UnitOfficers.HolderOf(map, s.a)?.LabelShort}");
                string again = UnitOfficers.Assign(s.guard, s.a, message: false);
                if (again != "RP_Unit_WhyAlready".Translate()) UnitFail(check, $"named twice to the same wing: \"{again}\"");
                // Rotation: two days in, moved to the other wing - the days start again, the old patrol is still the one kept.
                s.sc.unitSinceTick = now - 2 * GenDate.TicksPerDay;
                UnitOfficers.Assign(s.guard, s.b, message: false);
                if (s.sc.unitWing != s.b || s.sc.unitSinceTick != now || s.sc.patrolZone != zoneB || s.sc.unitPrevZone != before || UnitOfficers.HolderOf(map, s.a) != null || UnitOfficers.HolderOf(map, s.b) != s.guard)
                    UnitFail(check, $"rotated to {s.b.defName}: {UnitOfficers.Describe(s.sc)}, patrol {s.sc.patrolZone?.defName}, days {UnitOfficers.DaysInPost(s.sc):0.0}, holder of {s.a.defName} {UnitOfficers.HolderOf(map, s.a)?.LabelShort}");
                // A second guard named to the same wing replaces the first, whose patrol goes back.
                Pawn second = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff sc2 = second.GetComp<CompStaff>();
                r.Add(() => UnitOfficers.Unassign(second, sc2));
                UnitOfficers.Assign(second, s.b, message: false);
                if (s.sc.unitWing != null || s.sc.unitSinceTick != -1 || s.sc.patrolZone != before || UnitOfficers.HolderOf(map, s.b) != second)
                    UnitFail(check, $"a second guard named to {s.b.defName}: the first {UnitOfficers.Describe(s.sc)} patrol {s.sc.patrolZone?.defName} (want off, patrol {before?.defName}), holder {UnitOfficers.HolderOf(map, s.b)?.LabelShort}");
                // A patrol on the officer's own wing changes nothing; another zone takes them off the post.
                UnitOfficers.SetPatrol(second, sc2, zoneB);
                bool keptOwn = sc2.unitWing == s.b;
                UnitOfficers.SetPatrol(second, sc2, before);
                if (!keptOwn || sc2.unitWing != null || sc2.patrolZone != before) UnitFail(check, $"the patrol menus: own wing kept the post {keptOwn}; another zone left {UnitOfficers.Describe(sc2)} patrol {sc2.patrolZone?.defName}");
                // Taken off: the old patrol back.
                UnitOfficers.Assign(s.guard, s.a, message: false);
                UnitOfficers.Unassign(s.guard, s.sc);
                if (s.sc.unitWing != null || s.sc.patrolZone != before || s.sc.unitPrevZone != null) UnitFail(check, $"taken off: {UnitOfficers.Describe(s.sc)} patrol {s.sc.patrolZone?.defName} (want {before?.defName})");
                RPLog.Message($"[RimPrison:test] unit officer: naming, rotation, replacement and the patrol ran ({zoneA.defName}, {zoneB.defName}, kept {before?.defName})");
            });
        }

        // ---- kin --------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckUnitKin(Map map)
        {
            const string check = "kin";
            Guarded("unit officer: a guard kept off the wing by kin is refused", map, r =>
            {
                UnitScene s = MakeUnitScene(map, r, check, perWing: 1);
                if (s == null) return;
                Pawn kin = s.onA[0];
                Kin.Link(kin, s.guard, KinKind.Parent);
                r.Add(() => kin.relations?.TryRemoveDirectRelation(PawnRelationDefOf.Parent, s.guard));
                if (Kin.Between(s.guard, kin) == null)
                {
                    UnitFail(check, "premise: the guard is not kin of the inmate");
                    return;
                }
                // Left on the wing by their own answer: they can be named, and the menu says there is kin.
                s.sc.kinKeepOff = false;
                string letBe = UnitOfficers.Refusal(s.guard, s.a);
                string guardLabel = "RP_Unit_MenuGuard".Translate(s.guard.LabelShortCap, UnitOfficers.ShiftNote(s.sc)).ToString();
                FloatMenuOption shown = UnitOfficers.SlotOptions(map, s.a).FirstOrDefault(o => o.Label.StartsWith(guardLabel));
                if (letBe != null || shown == null || shown.Disabled || shown.Label != "RP_Unit_MenuKin".Translate(guardLabel).ToString())
                    UnitFail(check, $"kin on the wing, not kept off: refused \"{letBe}\", menu \"{shown?.Label}\" disabled {shown?.Disabled}");
                // Kept off: refused with the reason, and greyed in the menu with it.
                s.sc.kinKeepOff = true;
                string want = "RP_Unit_WhyKin".Translate();
                string why = UnitOfficers.Assign(s.guard, s.a, message: false);
                FloatMenuOption greyed = UnitOfficers.SlotOptions(map, s.a).FirstOrDefault(o => o.Label.StartsWith(guardLabel));
                string greyedWant = "RP_Unit_MenuWhy".Translate(guardLabel, want).ToString();
                if (why != want || s.sc.unitWing != null || greyed == null || !greyed.Disabled || greyed.Label != greyedWant)
                    UnitFail(check, $"kept off the wing: Assign said \"{why}\" (want \"{want}\"), named {s.sc.unitWing?.defName}, menu \"{greyed?.Label}\" disabled {greyed?.Disabled} (want \"{greyedWant}\")");
                // The other wing is theirs; their kin moved onto it, the daily pass takes them off.
                string other = UnitOfficers.Assign(s.guard, s.b, message: false);
                kin.Inmate().security = s.b;
                UnitOfficers.Daily(map, s.guard);
                if (other != null || s.sc.unitWing != null) UnitFail(check, $"the other wing: refused \"{other}\"; with the kin moved onto it, after the daily pass {UnitOfficers.Describe(s.sc)} (want off)");
                RPLog.Message($"[RimPrison:test] unit officer: kin refused \"{why}\", menu \"{greyed?.Label}\"");
            });
        }

        // ---- trust -----------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckUnitTrust(Map map)
        {
            const string check = "trust";
            Guarded("unit officer: five days of presence give the wing's trust", map, r =>
            {
                UnitScene s = MakeUnitScene(map, r, check);
                if (s == null) return;
                UnitOfficers.Assign(s.guard, s.a, message: false);
                var seenDays = new List<int>();
                for (int day = 0; day < 5; day++)
                {
                    UnitOfficers.Hourly(map, s.guard);
                    seenDays.Add(UnitOfficers.SeenToday(s.guard));
                    UnitOfficers.Daily(map, s.guard);
                }
                float meanA = UnitMean(s.onA, s.guard), want = 5 * UnitOfficers.TrustPerDay;
                bool bUnchanged = s.onB.All(p => StaffMemories.ScoreFor(p.Inmate(), s.guard) == 0f);
                if (meanA < want - 0.01f || meanA > want + 0.01f || !bUnchanged || seenDays.Any(n => n != s.onA.Count))
                    UnitFail(check, $"after five days the officer's wing trusts them {meanA:0.00} on average (want {want:0.00}), the other wing {UnitMean(s.onB, s.guard):0.00} (want 0), seen a day [{string.Join(",", seenDays)}] (want {s.onA.Count} each)");
                // The ceiling: one at 49 goes to 50 and no further, one at 60 stays; and a grudge (-20) gets nothing from presence (acquaintances, not forgiveness).
                CompInmate c0 = s.onA[0].Inmate(), c1 = s.onA[1].Inmate(), c2 = s.onA[2].Inmate();
                StaffMemories.Note(c0, s.guard, 49f - StaffMemories.ScoreFor(c0, s.guard));
                StaffMemories.Note(c1, s.guard, 60f - StaffMemories.ScoreFor(c1, s.guard));
                StaffMemories.Note(c2, s.guard, -20f - StaffMemories.ScoreFor(c2, s.guard));
                UnitOfficers.Hourly(map, s.guard);
                int seenGrudge = UnitOfficers.SeenToday(s.guard);
                UnitOfficers.Daily(map, s.guard);
                float at49 = StaffMemories.ScoreFor(c0, s.guard), at60 = StaffMemories.ScoreFor(c1, s.guard), atGrudge = StaffMemories.ScoreFor(c2, s.guard);
                if (Mathf.Abs(at49 - UnitOfficers.DriftCeiling) > 0.01f || Mathf.Abs(at60 - 60f) > 0.01f || Mathf.Abs(atGrudge + 20f) > 0.01f || seenGrudge != s.onA.Count)
                    UnitFail(check, $"the ceiling: 49 became {at49:0.00} (want {UnitOfficers.DriftCeiling}), 60 became {at60:0.00} (want 60); a grudge of -20 became {atGrudge:0.00} (want -20, seen {seenGrudge} of {s.onA.Count})");
                // The state's chief deploys the guards: nothing seen, nothing paid, no lean and no fewer complaints.
                float before = UnitMean(s.onA, s.guard);
                UnitOfficers.ChiefOverride = true;
                UnitOfficers.Hourly(map, s.guard);
                int seenChief = UnitOfficers.SeenToday(s.guard);
                UnitOfficers.Daily(map, s.guard);
                float after = UnitMean(s.onA, s.guard);
                bool serving = UnitOfficers.Serving(s.sc);
                float lean = UnitOfficers.Lean(s.guard, s.onA[0]), factor = UnitOfficers.ComplaintFactor(s.guard, s.onA[0].Inmate());
                UnitOfficers.ChiefOverride = false;
                if (seenChief != 0 || Mathf.Abs(after - before) > 0.001f || serving || lean != 0f || factor != 1f || s.sc.unitWing != s.a)
                    UnitFail(check, $"under the state's chief: seen {seenChief}, trust {before:0.00} -> {after:0.00}, serving {serving}, lean {lean:0.000}, complaint factor {factor:0.00}, post {UnitOfficers.Describe(s.sc)} (want nothing paid and the post kept)");
                RPLog.Message($"[RimPrison:test] unit officer: five days give the wing {meanA:0.00} on average, the other wing {UnitMean(s.onB, s.guard):0.00}; ceiling {at49:0.00}/{at60:0.00}, grudge {atGrudge:0.00}");
            });
        }

        // ---- the corruption lean ---------------------------------------------------------------------------------------------------------------------------------

        private static void CheckUnitLean(Map map)
        {
            const string check = "lean";
            Guarded("unit officer: the corruption lean grows with the days in post", map, r =>
            {
                UnitScene s = MakeUnitScene(map, r, check, perWing: 1);
                if (s == null) return;
                int now = TaskClockForward(r);
                Pawn fixerA = s.onA[0], fixerB = s.onB[0];
                float unnamed = CorruptionSystem.OddsFor(s.guard, s.sc, fixerA, 0f);
                UnitOfficers.Assign(s.guard, s.a, message: false);
                float day0 = CorruptionSystem.OddsFor(s.guard, s.sc, fixerA, 0f);
                s.sc.unitSinceTick = now - 5 * GenDate.TicksPerDay;
                float day5 = CorruptionSystem.OddsFor(s.guard, s.sc, fixerA, 0f), offWing = CorruptionSystem.OddsFor(s.guard, s.sc, fixerB, 0f);
                float want = Mathf.Min(UnitOfficers.LeanCap, 5 * UnitOfficers.LeanPerDay);
                if (Mathf.Abs(day5 - day0 - want) > 0.0001f || Mathf.Abs(day0 - unnamed) > 0.0001f || Mathf.Abs(offWing - unnamed) > 0.0001f)
                    UnitFail(check, $"the odds for a fixer on the wing: unnamed {unnamed:0.0000}, day 0 {day0:0.0000}, day 5 {day5:0.0000} (want +{want:0.0000}), for a fixer on the other wing at day 5 {offWing:0.0000} (want unnamed)");
                // Rotated to the other wing: the days start again, the lean is gone for both.
                UnitOfficers.Assign(s.guard, s.b, message: false);
                float rotatedB = CorruptionSystem.OddsFor(s.guard, s.sc, fixerB, 0f), rotatedA = CorruptionSystem.OddsFor(s.guard, s.sc, fixerA, 0f);
                if (Mathf.Abs(rotatedB - unnamed) > 0.0001f || Mathf.Abs(rotatedA - unnamed) > 0.0001f || s.sc.unitSinceTick != now)
                    UnitFail(check, $"rotated: the odds for the new wing's fixer {rotatedB:0.0000}, the old wing's {rotatedA:0.0000} (want {unnamed:0.0000} both), since {s.sc.unitSinceTick} (want {now})");
                // Twenty days on the new wing (the clock moved on again, put back by the rollback): capped.
                Find.TickManager.DebugSetTicksGame(now + 20 * GenDate.TicksPerDay);
                float capped = UnitOfficers.Lean(s.guard, fixerB);
                if (Mathf.Abs(capped - UnitOfficers.LeanCap) > 0.0001f) UnitFail(check, $"the lean after twenty days is {capped:0.000}, not the cap {UnitOfficers.LeanCap}");
                RPLog.Message($"[RimPrison:test] unit officer: odds day 0 {day0:0.000}, day 5 {day5:0.000}, rotated {rotatedB:0.000}, twenty days lean {capped:0.000}");
            });
        }

        // ---- complaints ----------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckUnitComplaints(Map map)
        {
            const string check = "complaints";
            Guarded("unit officer: fewer complaints from the wing", map, r =>
            {
                UnitScene s = MakeUnitScene(map, r, check, perWing: 1);
                if (s == null) return;
                int now = TaskClockForward(r);
                UnitOfficers.Assign(s.guard, s.a, message: false);
                s.sc.unitSinceTick = now - 5 * GenDate.TicksPerDay;
                Pawn pa = s.onA[0], pb = s.onB[0];
                float factorA = UnitOfficers.ComplaintFactor(s.guard, pa.Inmate()), factorB = UnitOfficers.ComplaintFactor(s.guard, pb.Inmate());
                float fileA = ComplaintSystem.FileChance(ComplaintKind.Harassment, pa, pa.Inmate(), s.guard), plainA = ComplaintSystem.FileChance(ComplaintKind.Harassment, pa, pa.Inmate(), null);
                float fileB = ComplaintSystem.FileChance(ComplaintKind.Harassment, pb, pb.Inmate(), s.guard), plainB = ComplaintSystem.FileChance(ComplaintKind.Harassment, pb, pb.Inmate(), null);
                float want = 1f - UnitOfficers.ComplaintCut * 0.5f;
                if (Mathf.Abs(factorA - want) > 0.0001f || factorB != 1f || Mathf.Abs(fileA - plainA * want) > 0.0001f || Mathf.Abs(fileB - plainB) > 0.0001f)
                    UnitFail(check, $"at day 5 the factor on the wing is {factorA:0.000} (want {want:0.000}), off it {factorB:0.000}; filing odds {fileA:0.000} against {plainA:0.000}, off the wing {fileB:0.000} against {plainB:0.000}");
                // An inmate of the wing who resents the officer (-10) files as often as anywhere.
                StaffMemories.Note(pa.Inmate(), s.guard, -10f - StaffMemories.ScoreFor(pa.Inmate(), s.guard));
                float factorGrudge = UnitOfficers.ComplaintFactor(s.guard, pa.Inmate());
                if (factorGrudge != 1f) UnitFail(check, $"an inmate of the wing who resents the officer ({StaffMemories.ScoreFor(pa.Inmate(), s.guard):0.0}): the factor is {factorGrudge:0.000} (want 1)");
            });
        }

        // ---- the staff tip ---------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckUnitTip(Map map)
        {
            const string check = "the tip";
            Guarded("unit officer: the staff tip names a hidden fact of the officer's wing", map, r =>
            {
                UnitScene s = MakeUnitScene(map, r, check, perWing: 2);
                if (s == null) return;
                PrisonGame g = PrisonGame.Instance;
                InmateDebt debtA = DebtSystem.Open(s.onA[1], s.onA[0], DebtReason.Cards, 40, 5), debtB = DebtSystem.Open(s.onB[1], s.onB[0], DebtReason.Cards, 45, 5);
                r.Add(() =>
                {
                    g.story.debts.Remove(debtA);
                    g.story.debts.Remove(debtB);
                });
                string tipA = "RP_TipDebt".Translate(debtA.debtorName, debtA.creditorName, Money.Format(debtA.amount), DebtSystem.ReasonText(debtA));
                string tipB = "RP_TipDebt".Translate(debtB.debtorName, debtB.creditorName, Money.Format(debtB.amount), DebtSystem.ReasonText(debtB));
                UnitOfficers.Assign(s.guard, s.a, message: false);
                var seen = new List<string>();
                bool bKnown = false;
                for (int i = 0; i < 10; i++)
                {
                    (debtA.known, debtB.known) = (false, false);
                    seen.Add(UnitOfficers.StaffTip(s.guard, s.sc, map) ?? "(nothing)");
                    bKnown |= debtB.known;
                }
                if (seen.Any(t => t != tipA) || bKnown) UnitFail(check, $"of 10 forced tips {seen.Count(t => t == tipA)} named the wing's debt and {seen.Count(t => t == tipB)} the other wing's (other wing's revealed {bKnown}; saw: {string.Join(" | ", seen.Distinct())})");
                // Through the daily pass with the dice saying yes: a letter (counted), the debt known.
                int tips0 = g.stats.unitTips;
                r.Add(() => g.stats.unitTips = tips0);
                (debtA.known, debtB.known) = (false, false);
                UnitOfficers.TipRoll = true;
                int letters = Find.LetterStack.LettersListForReading.Count;
                UnitOfficers.Daily(map, s.guard);
                UnitOfficers.TipRoll = false;
                Letter letter = Find.LetterStack.LettersListForReading.Skip(letters).FirstOrDefault(l => l.Label.ToString() == "RP_Unit_TipLabel".Translate().ToString());
                if (letter == null || !debtA.known || debtB.known || g.stats.unitTips != tips0 + 1)
                    UnitFail(check, $"the daily pass with the dice saying yes: letter {letter != null}, the wing's debt known {debtA.known}, the other's {debtB.known}, tips counted {g.stats.unitTips - tips0} (want 1)");
                // The control: rotated to the other wing, its debt is the one told.
                (debtA.known, debtB.known) = (false, false);
                UnitOfficers.Assign(s.guard, s.b, message: false);
                string rotated = UnitOfficers.StaffTip(s.guard, s.sc, map);
                if (rotated != tipB || debtA.known) UnitFail(check, $"rotated to the other wing the tip said \"{rotated}\" (want \"{tipB}\"), the first wing's debt revealed {debtA.known}");
                // An officer at home at midnight (every day-shift guard: they leave at the end of the day), on no map: the daily pass's map finds the wing, the letter comes pointing at nobody, the debt is known.
                Pawn home = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                CompStaff hc = home.GetComp<CompStaff>();
                r.Add(() => UnitOfficers.Unassign(home, hc));
                string homeWhy = UnitOfficers.Assign(home, s.a, message: false);
                bool homePremise = homeWhy == null && home.MapHeld == null && !home.Spawned && StaffCommute.AllStaff(map).Contains(home) && UnitOfficers.Serving(hc);
                (debtA.known, debtB.known) = (false, false);
                UnitOfficers.TipRoll = true;
                letters = Find.LetterStack.LettersListForReading.Count;
                UnitOfficers.Daily(map, home);
                UnitOfficers.TipRoll = false;
                Letter homeLetter = Find.LetterStack.LettersListForReading.Skip(letters).FirstOrDefault(l => l.Label.ToString() == "RP_Unit_TipLabel".Translate().ToString());
                if (!homePremise) UnitFail(check, $"premise: an officer at home (named: \"{homeWhy}\", on a map {home.MapHeld != null}, spawned {home.Spawned}, among the staff {StaffCommute.AllStaff(map).Contains(home)}, serving {UnitOfficers.Serving(hc)})");
                else if (homeLetter == null || !debtA.known || debtB.known || homeLetter.lookTargets is { Any: true })
                    UnitFail(check, $"an officer at home at midnight: letter {homeLetter != null} (pointing at {homeLetter?.lookTargets?.PrimaryTarget.Label ?? "nobody"}), the wing's debt known {debtA.known}, the other's {debtB.known}");
                // A tip from staff leaves an informant's earlier tip on the inmate as it was (a search may still give the informant away).
                CompInmate dug = s.onB[1].Inmate();
                Pawn informer = s.onA[0];
                r.Add(() => (dug.tunnelProgress, dug.tunnelKnown, dug.cellSearchPending, dug.informedBy) = (0f, false, false, null));
                (dug.tunnelProgress, dug.tunnelKnown, dug.informedBy) = (0.5f, false, informer);
                string tunnelTip = UnitOfficers.StaffTip(s.guard, s.sc, map), wantTunnel = "RP_TipTunnel".Translate(s.onB[1].LabelShort);
                if (tunnelTip != wantTunnel || dug.informedBy != informer) UnitFail(check, $"a tunnel an informant had tipped: the staff tip said \"{tunnelTip}\" (want \"{wantTunnel}\"), the informant on file {dug.informedBy?.LabelShort ?? "none"} (want {informer.LabelShort})");
                // An officer on a fixer's payroll says nothing of the patron (their tunnel) nor of the patron's gang (a gang-mate's hidden tie); turned (a double agent), they do.
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                CompInmate mate = s.onB[0].Inmate();
                (bool corrupt0, bool double0, Pawn patron0, GangDef patronGang0) = (s.sc.corrupt, s.sc.doubleAgent, s.sc.patron, s.sc.patronGang);
                r.Add(() =>
                {
                    (s.sc.corrupt, s.sc.doubleAgent, s.sc.patron, s.sc.patronGang) = (corrupt0, double0, patron0, patronGang0);
                    (dug.gang, dug.gangKnown, mate.gang, mate.gangKnown) = (null, false, null, false);
                });
                if (gang == null) RPLog.Message("[RimPrison:test] unit officer: no gang def, the patron's gang not checked");
                (s.sc.corrupt, s.sc.doubleAgent, s.sc.patron, s.sc.patronGang) = (true, false, s.onB[1], gang);
                (dug.gang, dug.gangKnown, mate.gang, mate.gangKnown) = (gang, false, gang, false);
                var patronTips = new List<string>();
                bool patronTold = false, gangTold = false;
                for (int i = 0; i < 10; i++)
                {
                    dug.tunnelKnown = false;
                    patronTips.Add(UnitOfficers.StaffTip(s.guard, s.sc, map) ?? "(nothing)");
                    patronTold |= dug.tunnelKnown;
                    gangTold |= mate.gangKnown || dug.gangKnown;
                }
                // (The control: the gang-mate leaves the gang, so that its tie is not given away by the turned officer.)
                (mate.gang, s.sc.doubleAgent) = (null, true);
                bool turnedTold = false;
                for (int i = 0; i < 10 && !turnedTold; i++)
                {
                    dug.tunnelKnown = false;
                    UnitOfficers.StaffTip(s.guard, s.sc, map);
                    turnedTold = dug.tunnelKnown;
                }
                if (patronTold || gangTold || !turnedTold)
                    UnitFail(check, $"an officer on {s.onB[1].LabelShort}'s payroll: the patron's tunnel told {patronTold}, the gang ({gang?.defName}) given away {gangTold} (saw: {string.Join(" | ", patronTips.Distinct())}); turned, the tunnel told {turnedTold} (want true)");
                RPLog.Message($"[RimPrison:test] unit officer: tips {seen.Count(t => t == tipA)}/10 the wing's debt; daily letter {letter != null}; rotated \"{rotated}\"; at home letter {homeLetter != null}; the informant kept {dug.informedBy == informer}; patron told {patronTold}");
            });
        }

        // ---- vacating -------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckUnitVacate(Map map)
        {
            const string check = "vacating";
            Guarded("unit officer: leave keeps the days, firing and retraining clear them", map, r =>
            {
                UnitScene s = MakeUnitScene(map, r, check, perWing: 1);
                if (s == null) return;
                int now = TaskClockForward(r);
                UnitOfficers.Assign(s.guard, s.a, message: false);
                int since = now - 3 * GenDate.TicksPerDay;
                s.sc.unitSinceTick = since;
                // On leave, then suspended, a day each (the clock moved on, put back by the rollback): the slot vacant, nothing paid, the days kept and standing still - each midnight away moves the start a day on.
                int clock = now, wantSince = since;
                foreach (string how in new[] { "leave", "suspension" })
                {
                    clock += GenDate.TicksPerDay;
                    Find.TickManager.DebugSetTicksGame(clock);
                    if (how == "leave") s.sc.leaveUntilTick = clock + GenDate.TicksPerDay;
                    else s.sc.disciplineUntilTick = clock + GenDate.TicksPerDay;
                    UnitOfficers.Daily(map, s.guard);
                    wantSince += GenDate.TicksPerDay;
                    string line = UnitOfficers.SlotLine(s.guard), wantLine = "RP_Unit_SlotAway".Translate(s.guard.LabelShort, "3.0");
                    if (!UnitOfficers.Away(s.sc) || UnitOfficers.Serving(s.sc) || s.sc.unitWing != s.a || s.sc.unitSinceTick != wantSince || UnitOfficers.Lean(s.guard, s.onA[0]) != 0f || line != wantLine)
                        UnitFail(check, $"a day on {how}: away {UnitOfficers.Away(s.sc)}, serving {UnitOfficers.Serving(s.sc)}, post {UnitOfficers.Describe(s.sc)} (want {s.a.defName}:{wantSince}), lean {UnitOfficers.Lean(s.guard, s.onA[0]):0.000}, row \"{line}\" (want \"{wantLine}\")");
                    (s.sc.leaveUntilTick, s.sc.disciplineUntilTick) = (-1, -1);
                }
                if (!UnitOfficers.Serving(s.sc)) UnitFail(check, "back from leave and suspension, the officer is not serving");
                // A day serving counts (the control): the start stays, the days grow.
                clock += GenDate.TicksPerDay;
                Find.TickManager.DebugSetTicksGame(clock);
                UnitOfficers.Daily(map, s.guard);
                if (s.sc.unitSinceTick != wantSince || Mathf.Abs(UnitOfficers.DaysInPost(s.sc) - 4f) > 0.001f)
                    UnitFail(check, $"a day serving after the time away: {UnitOfficers.Describe(s.sc)}, {UnitOfficers.DaysInPost(s.sc):0.00} days in post (want {s.a.defName}:{wantSince}, 4.00)");
                // Retrained out of guard work: cleared.
                StaffRoleDef other = DefDatabase<StaffRoleDef>.AllDefsListForReading.FirstOrDefault(x => x.hireable && !x.guardDuties && !x.warden && x.weapon == null && x.companion == null);
                if (other != null)
                {
                    StaffUtility.ApplyRole(s.guard, other, record: false);
                    if (s.sc.unitWing != null || s.sc.unitSinceTick != -1) UnitFail(check, $"retrained as {other.defName}: {UnitOfficers.Describe(s.sc)} (want cleared)");
                }
                else RPLog.Message("[RimPrison:test] unit officer: no plain role to retrain into, retraining not checked");
                // Fired: cleared, the slot free.
                Pawn fired = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff fc = fired.GetComp<CompStaff>();
                UnitOfficers.Assign(fired, s.a, message: false);
                bool named = fc.unitWing == s.a;
                StaffUtility.Fire(fired, record: false);
                if (!named || fc.unitWing != null || fc.unitSinceTick != -1 || UnitOfficers.HolderOf(map, s.a) != null) UnitFail(check, $"fired: named first {named}, then {UnitOfficers.Describe(fc)}, holder {UnitOfficers.HolderOf(map, s.a)?.LabelShort} (want cleared and none)");
                // Two holders of one wing (the first came back from off the map after someone else was named): the daily sweep takes the earlier named off.
                Pawn first = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), second = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff c1 = first.GetComp<CompStaff>(), c2 = second.GetComp<CompStaff>();
                r.Add(() =>
                {
                    UnitOfficers.Unassign(first, c1);
                    UnitOfficers.Unassign(second, c2);
                });
                (c1.unitWing, c1.unitSinceTick, c1.patrolZone) = (s.a, clock - 2 * GenDate.TicksPerDay, UnitOfficers.WingZone(s.a));
                (c2.unitWing, c2.unitSinceTick, c2.patrolZone) = (s.a, clock - GenDate.TicksPerDay, UnitOfficers.WingZone(s.a));
                int doubled = UnitOfficers.ClearDoubledPosts(new List<Pawn> { first, second });
                if (doubled != 1 || c1.unitWing != null || c2.unitWing != s.a) UnitFail(check, $"two holders of the {s.a.defName}: {doubled} taken off (want 1), the earlier {UnitOfficers.Describe(c1)} (want off), the later {UnitOfficers.Describe(c2)} (want kept)");
            });
        }

        // ---- the screens ----------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckUnitScreens(Map map)
        {
            const string check = "screens";
            Guarded("unit officer: the Deployment row and the staff record", map, r =>
            {
                UnitScene s = MakeUnitScene(map, r, check, perWing: 1);
                if (s == null) return;
                string vacant = UnitOfficers.SlotLine(UnitOfficers.HolderOf(map, s.a));
                UnitOfficers.Assign(s.guard, s.a, message: false);
                bool night0 = s.sc.nightShift;
                r.Add(() => s.sc.nightShift = night0);
                s.sc.nightShift = false;
                string days = UnitOfficers.SlotLine(s.guard), record = UnitOfficers.RecordLine(s.sc), status = s.sc.StatusLines();
                s.sc.nightShift = true;
                string nights = UnitOfficers.SlotLine(s.guard);
                string wantDays = "RP_Unit_SlotLine".Translate("0.0", "RP_Unit_WorksDays".Translate()), wantNights = "RP_Unit_SlotLine".Translate("0.0", "RP_Unit_WorksNights".Translate());
                if (vacant != "RP_Unit_SlotVacant".Translate() || days != wantDays || nights != wantNights || record == null || !record.Contains(UnitOfficers.WingLabel(s.a)) || !status.Contains(record))
                    UnitFail(check, $"vacant \"{vacant}\", days \"{days}\" (want \"{wantDays}\"), nights \"{nights}\" (want \"{wantNights}\"), record \"{record}\", in the status lines {status.Contains(record ?? "?")}");
                List<FloatMenuOption> options = UnitOfficers.SlotOptions(map, s.a);
                string first = options.FirstOrDefault()?.Label;
                if (first != "RP_Unit_MenuNoneTake".Translate(s.guard.LabelShort).ToString()) UnitFail(check, $"the slot's menu begins with \"{first}\", not taking the officer off");
            });
        }

        // ---- the saveload plan --------------------------------------------------------------------------------------------------------------------------------

        private static int unitSeedId = -1;
        private static string unitSeed;
        private static (SecurityLevelDef wing, int since, PrisonZoneDef prev, PrisonZoneDef patrol) unitSeedWas;
        private static int unitSeedTipsWas = -1;

        /// <summary>A real guard named to a wing two days and a bit ago, with an old patrol to put back, and three unit officers' tips counted: the fields off their defaults (put back after the reload).</summary>
        private static void SeedUnitOfficerForReload(Map map)
        {
            PrisonZoneDef prev = UnitOtherZone();
            Pawn guard = null;
            SecurityLevelDef wing = null;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Where(x => x.GetComp<CompStaff>() is { role.guardDuties: true, Protected: false, leaveAtTick: < 0, unitWing: null }).OrderBy(x => x.thingIDNumber))
            {
                wing = new[] { RPSecurityDefOf.RP_Medium, RPSecurityDefOf.RP_Maximum, RPSecurityDefOf.RP_Protective }.FirstOrDefault(l => UnitOfficers.WingZone(l) is { } z && Kin.PatrolAllowed(p, z) && UnitOfficers.HolderOf(map, l) == null);
                if (wing == null) continue;
                guard = p;
                break;
            }
            if (guard == null || prev == null)
            {
                problems0.Add($"saveload: no guard to name a unit officer (guard {guard?.LabelShort}, a zone to put back {prev?.defName})");
                return;
            }
            CompStaff sc = guard.GetComp<CompStaff>();
            unitSeedWas = (sc.unitWing, sc.unitSinceTick, sc.unitPrevZone, sc.patrolZone);
            (sc.unitWing, sc.unitSinceTick, sc.unitPrevZone, sc.patrolZone) = (wing, System.Math.Max(0, Find.TickManager.TicksGame - 2 * GenDate.TicksPerDay - 55), prev, UnitOfficers.WingZone(wing));
            (unitSeedId, unitSeed) = (guard.thingIDNumber, UnitOfficers.Describe(sc));
            unitSeedTipsWas = PrisonGame.Instance.stats.unitTips;
            PrisonGame.Instance.stats.unitTips += 3;
            RPLog.Message($"[RimPrison:test] saveload unit officer: {guard.LabelShort} ({unitSeed})");
        }

        /// <summary>The seeded post came back whole; then it goes.</summary>
        private static void CheckUnitOfficerAfterReload(Map map)
        {
            if (unitSeedId < 0) return;
            CompStaff sc = StaffCommute.AllStaff(map).FirstOrDefault(p => p.thingIDNumber == unitSeedId)?.GetComp<CompStaff>();
            string after = sc == null ? "(guard gone)" : UnitOfficers.Describe(sc);
            RPLog.Message($"[RimPrison:test] saveload unit officer: {(after == unitSeed ? "intact" : "changed")} ({after})");
            if (after != unitSeed) reloadProblems.Add($"a unit officer's post changed in the round trip: {unitSeed} -> {after}");
            if (sc != null) (sc.unitWing, sc.unitSinceTick, sc.unitPrevZone, sc.patrolZone) = unitSeedWas;
            // (The count is in the snapshot, which compared it.)
            if (unitSeedTipsWas >= 0) PrisonGame.Instance.stats.unitTips = unitSeedTipsWas;
            unitSeedTipsWas = -1;
            unitSeedId = -1;
            unitSeed = null;
        }
    }
}
