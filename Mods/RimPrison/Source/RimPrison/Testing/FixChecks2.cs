using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the rest of the review's bugs (B8, B9, B13, B17-B32; the first ones are in <see cref="FixChecks"/>'s file).
    /// Each is its own method called once from FireAllEvents, works on throwaway pawns, and puts back what it touches (the
    /// <see cref="Rollback"/>, plus its own undo actions, which run in reverse order and even when a check throws).
    /// </summary>
    public static partial class AutoTest
    {
        private static ChoiceLetter_PrisonDecision DecisionLetter(Map map, Pawn about)
        {
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Retire", "test", "test", map);
            letter.pawns.Add(about);
            return letter;
        }

        // ---- B8, B25: corruption reaches the staff at home ---------------------------------------------------------------

        /// <summary>
        /// The daily pass runs over everyone employed (day staff are at home at midnight): integrity drifts for the one at home, a case
        /// that crosses its threshold with the suspect off site opens (and the warden's suspension takes hold without them on site), a
        /// case nobody opened is opened by the next pass, and a double agent whose patron has gone and who has no gang is let go of.
        /// </summary>
        private static void CheckCorruptionReachesStaffAtHome(Map map)
        {
            Guarded("corruption off site", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                // (The pass drifts everybody's integrity, and may open a case or drop a gone patron: put each record back.)
                foreach (Pawn s in StaffCommute.AllStaff(map))
                {
                    CompStaff c = s.GetComp<CompStaff>();
                    (float integrity, float suspicion, bool open, bool corrupt, bool agent, bool exposed, Pawn patron, GangDef gang) = (c.integrity, c.suspicion, c.iaOpen, c.corrupt, c.doubleAgent, c.exposedCorrupt, c.patron, c.patronGang);
                    List<string> evidence = c.evidence.ToList();
                    r.Add(() =>
                    {
                        (c.integrity, c.suspicion, c.iaOpen, c.corrupt, c.doubleAgent, c.exposedCorrupt, c.patron, c.patronGang) = (integrity, suspicion, open, corrupt, agent, exposed, patron, gang);
                        c.evidence = evidence;
                    });
                }
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                Pawn patronGone = Throwaway(map, r, spawn: false);
                CompStaff sc = guard.GetComp<CompStaff>();
                if (!g.home.Contains(guard) || patronGone.IsPrisonerOfColony)
                {
                    problems0.Add("corruption off site test: set-up is wrong (the guard is at home " + g.home.Contains(guard) + ")");
                    return;
                }
                sc.integrity = 60f;
                guard.needs.mood.CurLevel = 0.05f;
                int daily0 = CorruptionSystem.DailyPasses;
                CorruptionSystem.Daily(map);
                bool drifted = Mathf.Approximately(sc.integrity, 58f) && CorruptionSystem.DailyPasses == daily0 + 1;
                int IaLetters() => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.Is("RP_Decision_InternalAffairs") && l.pawns.Contains(guard));
                CorruptionSystem.AddSuspicion(guard, sc, 60f, "RP_Sus_Traced");
                bool opened = sc.iaOpen && IaLetters() == 1;
                if (r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_InternalAffairs") && l.pawns.Contains(guard)) is { } ia)
                    ia.decision.Worker.Choose(ia, map, 1);
                bool suspended = CorruptionSystem.Suspended(sc) && g.home.Contains(guard) && StaffCommute.ShouldBeHome(guard, sc, 12) && !sc.iaOpen;
                // (The pass does not open a second case while the investigation is under way.)
                CorruptionSystem.Daily(map);
                suspended &= !sc.iaOpen && IaLetters() == 1;
                // Over the threshold with no case open (it crossed before, or while nobody could open one): the next pass opens it.
                sc.suspendedUntilTick = -1;
                sc.iaOpen = false;
                CorruptionSystem.Daily(map);
                bool rechecked = sc.iaOpen && IaLetters() == 2;
                // A double agent whose patron left, with no gang behind them: nobody left to report on.
                sc.iaOpen = false;
                sc.suspicion = 0f;
                (sc.corrupt, sc.doubleAgent, sc.exposedCorrupt, sc.patron, sc.patronGang) = (true, true, true, patronGone, null);
                CorruptionSystem.Daily(map);
                bool stoodDown = !sc.doubleAgent && !sc.corrupt && !sc.exposedCorrupt && sc.patron == null;
                GangDef anyGang = DefDatabase<GangDef>.AllDefsListForReading.First();
                (sc.corrupt, sc.doubleAgent, sc.exposedCorrupt, sc.patron, sc.patronGang) = (true, true, true, patronGone, anyGang);
                CorruptionSystem.Daily(map);
                bool keptWithGang = sc.doubleAgent && sc.corrupt && sc.patronGang == anyGang;
                if (!drifted || !opened || !suspended || !rechecked || !stoodDown || !keptWithGang)
                    problems0.Add($"corruption off site test: integrity drifted for the guard at home {drifted} ({sc.integrity}); a case opened with the suspect at home {opened}; suspension held without them on site {suspended}; "
                                  + $"a case nobody opened was opened by the next pass {rechecked}; a double agent with no patron and no gang stood down {stoodDown}, one with a gang kept {keptWithGang}");
            });
        }

        // ---- B9: shifts count the guards who can work ----------------------------------------------------------------------

        /// <summary>
        /// A suspended guard or one on leave is not counted when shifts are balanced (the night shift would have nobody working it), the
        /// balance runs once a day (and again at once when the policy changes), and when someone has to stay on site it is not the
        /// suspended one.
        /// </summary>
        private static void CheckShiftBalanceCountsAvailableGuards(Map map)
        {
            Guarded("shift balance", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                int now = Find.TickManager.TicksGame;
                Pawn night = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), day1 = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), day2 = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), leave = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                night.GetComp<CompStaff>().nightShift = true;
                leave.GetComp<CompStaff>().nightShift = true;
                CorruptionSystem.Suspend(night, night.GetComp<CompStaff>());
                (leave.GetComp<CompStaff>().leaveUntilTick, leave.GetComp<CompStaff>().restPending) = (now + 3 * GenDate.TicksPerDay, true);
                var guards = new[] { night, day1, day2, leave }.Where(ShiftSystem.AvailableGuard).ToList();
                bool counted = guards.Count == 2 && guards.Contains(day1) && guards.Contains(day2);
                ShiftSystem.Rebalance(guards, nightShiftPolicy: true);
                int working = guards.Count(ShiftSystem.NightShift);
                // The order the off-shift go home in: the one who stays (the last) is available, not the suspended one.
                List<Pawn> order = StaffCommute.SendHomeOrder(new List<Pawn> { day1, night, day2 });
                bool keepsAvailable = order[0] == night && order[order.Count - 1] != night;
                // (And of two available, the one who needs medical rest goes and the fit one stays: kept, the sick one lies down on the floor, there being no bed for staff. Listed second, so that an order that only
                // sorts the unavailable first keeps them.)
                // (The generated guards come with a history of their own - an old injury still healing, a condition that can be immune to - and one of them needing rest makes the pair no pair of one ill and one fit: the made-up
                // pair starts with a clean bill of health, the fit one stays that way, and the line tells the hediffs if the premise fails anyway.)
                foreach (Pawn guard in new[] { day1, day2 })
                    foreach (Hediff old in guard.health.hediffSet.hediffs.Where(x => !(x is Hediff_MissingPart)).ToList()) guard.health.RemoveHediff(old);
                Hediff flu = HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed("Flu"), day1);
                flu.Severity = 0.2f;
                day1.health.AddHediff(flu);
                bool day1Rests = HealthAIUtility.ShouldSeekMedicalRest(day1), day2Rests = HealthAIUtility.ShouldSeekMedicalRest(day2);
                bool premise = day1Rests && !day2Rests;
                List<Pawn> sickOrder = StaffCommute.SendHomeOrder(new List<Pawn> { day2, day1 });
                string Hediffs(Pawn who) => string.Join(", ", who.health.hediffSet.hediffs.Select(x => x is Hediff_Injury inj ? $"{x.def.defName} (injury, heals from tending {inj.CanHealFromTending()})" : x.def.defName));
                if (!premise) problems0.Add($"shift balance test's own premise: the ill guard needs medical rest {day1Rests} (wanted true) and the other {day2Rests} (wanted false); downed {day1.Downed}/{day2.Downed}; "
                                            + $"hediffs of the ill [{Hediffs(day1)}], of the other [{Hediffs(day2)}]");
                else if (sickOrder[0] != day1 || sickOrder[1] != day2)
                    problems0.Add($"shift balance test: of two available guards the one who needs medical rest is kept on site ({sickOrder.Last().LabelShort}), the fit one is sent home");
                int day0 = g.lastShiftBalanceDay;
                r.Add(() => g.lastShiftBalanceDay = day0);
                g.lastShiftBalanceDay = -1;
                bool first = ShiftSystem.BalanceDue(g), again = ShiftSystem.BalanceDue(g);
                g.lastShiftBalanceDay = Ledger.CurrentDay - 1;
                bool nextDay = ShiftSystem.BalanceDue(g);
                if (!counted || working != 1 || !keepsAvailable || !first || again || !nextDay)
                    problems0.Add($"shift balance test: only available guards counted {counted}; {working} of the two available guards work nights (1 expected: the suspended night guard and the one on leave do not count); "
                                  + $"the stay-on-site guard is an available one {keepsAvailable}; balance due after a new day {first}, again the same day {again} (false), the day after {nextDay}");
            });
        }

        // ---- B13: a referral kept waiting gains priority ---------------------------------------------------------------------

        /// <summary>
        /// A smaller program with someone referred to it overtakes a fuller one after it has been kept waiting a few hours, loses its age
        /// once it starts (or has nobody referred), and the referral alert counts a program that waited a day.
        /// </summary>
        private static void CheckStarvedProgramGainsPriority(Map map)
        {
            Guarded("program aging", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                List<ProgramDef> programs = DefDatabase<ProgramDef>.AllDefsListForReading.OrderBy(p => p.uiOrder).Take(2).ToList();
                if (programs.Count < 2)
                {
                    problems0.Add("program aging test: fewer than two programs");
                    return;
                }
                Dictionary<ProgramDef, int> waiting0 = new Dictionary<ProgramDef, int>(g.programs.waitingHours);
                r.Add(() => g.programs.waitingHours = waiting0);
                g.programs.waitingHours = new Dictionary<ProgramDef, int>();
                ProgramDef big = programs[0], small = programs[1];
                Pawn who = map.mapPawns.AllPawnsSpawned.First();
                ProgramSystem.Candidate Cand(ProgramDef p, int n)
                {
                    var c = new ProgramSystem.Candidate { program = p, referred = true };
                    for (int i = 0; i < n; i++) c.pawns.Add(who);
                    return c;
                }
                // (A negative result: the first goes first.)
                bool bigFirst = ProgramSystem.ComparePriority(g, Cand(big, 5), Cand(small, 1)) < 0;
                for (int hour = 0; hour < 3; hour++) g.programs.NoteWaiting(new List<ProgramDef> { small });
                bool smallFirst = ProgramSystem.ComparePriority(g, Cand(big, 5), Cand(small, 1)) > 0;
                bool counts = g.programs.WaitedHours(small) == 3 && g.programs.WaitedHours(big) == 0;
                g.programs.NoteWaiting(new List<ProgramDef>());
                bool reset = g.programs.WaitedHours(small) == 0;
                g.programs.waitingHours[small] = ProgramPolicy.AlertAfterHours - 1;
                bool quiet = !ProgramSystem.WaitedTooLong(g, small);
                g.programs.NoteWaiting(new List<ProgramDef> { small });
                bool alerts = ProgramSystem.WaitedTooLong(g, small);
                if (!bigFirst || !smallFirst || !counts || !reset || !quiet || !alerts)
                    problems0.Add($"program aging test: the fuller program goes first with nobody waiting {bigFirst}; after 3 hours waiting the smaller one does {smallFirst}; hours counted {counts}, reset when not waiting {reset}; "
                                  + $"alert after 23 hours {!quiet} (false), after 24 {alerts}");
            });
        }

        // ---- B17: the screens show what the warden knows ---------------------------------------------------------------------

        /// <summary>The file lists only seen misconduct; the tension shown for a cell ignores a hidden gang rivalry and a hidden extortionist; the game's own tension does not.</summary>
        private static void CheckHiddenInfoStaysOffScreens(Map map)
        {
            Guarded("hidden information", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn a = Throwaway(map, r), b = Throwaway(map, r);
                CompInmate ac = a.Inmate(), bc = b.Inmate();
                List<MisconductDef> defs = DefDatabase<MisconductDef>.AllDefsListForReading;
                ac.records.Add(new MisconductRecord { def = defs[0], tick = Find.TickManager.TicksGame, seen = true });
                ac.records.Add(new MisconductRecord { def = defs[1], tick = Find.TickManager.TicksGame, seen = false });
                List<MisconductRecord> shown = ITab_InmateFile.ShownRecords(ac);
                bool recordOk = shown.Count == 1 && shown[0].def == defs[0] && shown[0].seen;
                List<GangTruce> truces0 = g.story.truces.ToList();
                r.Add(() => g.story.truces = truces0);
                g.story.truces = new List<GangTruce>();
                GangDef saints = DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"), ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
                (ac.gang, bc.gang, ac.gangKnown, bc.gangKnown) = (saints, ravens, false, false);
                ReputationDef extortionist = RPReputationDefOf.RP_Extortionist;
                // (Neither starts as an extortionist, nor with the warden knowing: the intake rolled them reputations at random.)
                ac.reputations.Remove(extortionist);
                bc.reputations.Remove(extortionist);
                ac.revealed.Remove(extortionist);
                bc.revealed.Remove(extortionist);
                var mates = new List<Pawn> { b };
                float full = Cellmates.TensionAmong(a, ac, mates), known = Cellmates.TensionAmong(a, ac, mates, knownOnly: true);
                bool hiddenGang = full - known >= 2f - 0.001f;
                (ac.gangKnown, bc.gangKnown) = (true, true);
                bool shownOnceKnown = Mathf.Approximately(Cellmates.TensionAmong(a, ac, mates), Cellmates.TensionAmong(a, ac, mates, knownOnly: true));
                (ac.gang, bc.gang) = (null, null);
                float noGangs = Cellmates.TensionAmong(a, ac, mates);
                bool hiddenExtortionist = true;
                if (extortionist.hidden)
                {
                    bc.reputations.Add(extortionist);
                    hiddenExtortionist = Mathf.Approximately(Cellmates.TensionAmong(a, ac, mates) - noGangs, 0.5f) && Mathf.Approximately(Cellmates.TensionAmong(a, ac, mates, knownOnly: true), noGangs);
                }
                // The chronicle's line for the daily review's move names nobody and no reason (it used to say "away from X", which named a hidden rivalry).
                string moved = "RP_Chr_CellMove".Translate(a.LabelShort).Resolve();
                bool noReason = moved.Contains(a.LabelShort) && !moved.Contains("away from") && !moved.Contains("{1}");
                if (!noReason) problems0.Add($"hidden information test: the cell-move chronicle line gives a reason or a name: \"{moved}\"");
                if (!recordOk || !hiddenGang || !shownOnceKnown || !hiddenExtortionist)
                    problems0.Add($"hidden information test: the file lists only the seen record {recordOk} ({shown.Count} shown); a hidden gang rivalry is left out of the shown tension {hiddenGang} ({full} against {known}), a known one is not {shownOnceKnown}; "
                                  + $"a hidden extortionist is left out {hiddenExtortionist}");
            });
        }

        // ---- B18: a restrained inmate with nowhere to serve it ---------------------------------------------------------------

        /// <summary>
        /// With no solitary bed free and no bed of their own, the restraint's punishment is served in the cell at once, so that when the
        /// restraint runs out nobody is sent to subdue them again (every round another pat-down and another grudge). With a place to
        /// take them to, it is left to the guards as before.
        /// </summary>
        private static void CheckRestraintWithNoPunishmentBed(Map map)
        {
            Guarded("restraint loop", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                List<Building_Bed> beds0 = pm.PrisonerBeds.ToList();
                r.Add(() =>
                {
                    pm.PrisonerBeds.Clear();
                    pm.PrisonerBeds.AddRange(beds0);
                });
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                p.health.AddHediff(RPSecurityDefOf.RP_Restrained);
                c.pendingPunishmentHours = 12;
                void Rare() => Traverse.Create(c).Method("Rare").GetValue();
                // A place to go (when the prison has a free solitary bed): nothing changes.
                bool control = true;
                if (PunishmentUtility.FindPunishmentBed(p, null) != null)
                {
                    Rare();
                    control = c.pendingPunishmentHours == 12 && c.forcedActivity == null;
                }
                pm.PrisonerBeds.Clear();
                bool noPlace = PunishmentUtility.FindPunishmentBed(p, null) == null;
                Rare();
                // (The restraint wears off after a while: here it is gone either way.)
                if (p.health.hediffSet.GetFirstHediffOfDef(RPSecurityDefOf.RP_Restrained) is { } cuffs) p.health.RemoveHediff(cuffs);
                bool served = c.forcedActivity == RPDefOf.RP_Lockup && c.pendingPunishmentHours == 0;
                bool nobodyComes = !GuardUtility.NeedsSubduing(p);
                if (!control || !noPlace || !served || !nobodyComes)
                    problems0.Add($"restraint loop test: with a place to go nothing changed {control}; no place found {noPlace}; the punishment was served in the cell {served} ({c.forcedActivity?.defName}, {c.pendingPunishmentHours} h owed); nobody is sent to subdue them again {nobodyComes}");
            });
        }

        // ---- B19: the home bed follows ownership -------------------------------------------------------------------------------

        private static Building_Bed SpawnTestBed(Map map, Rollback r, IntVec3 near, Func<IntVec3, bool> accept = null)
        {
            ThingDef bedDef = DefDatabase<ThingDef>.GetNamed("Bed");
            IntVec3 spot = IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, 25f, true))
                if (c.InBounds(map) && GenAdj.OccupiedRect(c, Rot4.North, bedDef.size).Cells.All(y => y.InBounds(map) && y.Standable(map) && y.GetEdifice(map) == null && y.GetFirstItem(map) == null && y.GetFirstPawn(map) == null
                                                                                                           && y.GetZone(map) == null && (accept == null || accept(y))))
                {
                    spot = c;
                    break;
                }
            if (!spot.IsValid) return null;
            var bed = (Building_Bed)ThingMaker.MakeThing(bedDef, ThingDefOf.WoodLog);
            bed.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(bed, spot, map, Rot4.North);
            bed.ForPrisoners = true;
            r.Add(() =>
            {
                foreach (Pawn owner in bed.OwnersForReading.ToList()) owner.ownership?.UnclaimBed();
                if (!bed.Destroyed) bed.Destroy();
            });
            return bed;
        }

        /// <summary>
        /// Leaving for the hole notes the cell bed to go back to when the solitary bed is claimed (and not when a job to take them there is
        /// merely asked for); a home bed that is no longer theirs (they own it, or another cell's bed) is dropped and no longer held.
        /// </summary>
        private static void CheckHomeBedFollowsOwnership(Map map)
        {
            Guarded("home bed", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                IntVec3 near = IntakeSystem.IntakeSpot(map);
                Building_Bed cellBed = SpawnTestBed(map, r, near), otherBed = SpawnTestBed(map, r, near), holeBed = SpawnTestBed(map, r, near);
                if (cellBed == null || otherBed == null || holeBed == null)
                {
                    problems0.Add("home bed test: no room for three beds near the intake");
                    return;
                }
                var holeCells = holeBed.OccupiedRect().Cells.ToList();
                List<PrisonZoneDef> zones0 = holeCells.Select(cell => pm.zones.ZoneAt(cell)).ToList();
                foreach (IntVec3 cell in holeCells) pm.zones.Set(cell, RPSecurityDefOf.RP_SolitaryZone);
                r.Add(() =>
                {
                    for (int i = 0; i < holeCells.Count; i++) pm.zones.Set(holeCells[i], zones0[i]);
                    pm.ForceDoorUpdate();
                });
                pm.ForceDoorUpdate(); // (the new beds join the prisoner bed list, refreshed every 250 ticks)
                Pawn p = Throwaway(map, r);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompInmate c = p.Inmate();
                p.ownership.ClaimBedIfNonMedical(cellBed);
                if (p.ownership.OwnedBed != cellBed || !PunishmentUtility.IsSolitaryBed(holeBed) || PunishmentUtility.IsSolitaryBed(cellBed))
                {
                    problems0.Add("home bed test: set-up is wrong (the bed was not claimed, or the zones are not as set)");
                    return;
                }
                // Asking for the job (a right-click menu does) notes nothing...
                c.pendingPunishmentHours = 12;
                p.health.AddHediff(RPSecurityDefOf.RP_Restrained);
                bool asked = new WorkGiver_TakeToPunishment().JobOnThing(guard, p) != null;
                bool noSideEffect = c.homeBed == null;
                // ...claiming the solitary bed does.
                p.ownership.ClaimBedIfNonMedical(holeBed);
                bool noted = c.homeBed == cellBed && p.ownership.OwnedBed == holeBed;
                PunishmentUtility.End(p, c);
                bool back = p.ownership.OwnedBed == cellBed && c.homeBed == null;
                // A stale home bed (they live in another bed now) does not hold the bed back from the intake: counting the free beds only reads it (a screen asks every frame), and the sync lets it go.
                c.homeBed = otherBed;
                int freeBefore = IntakeSystem.FreePrisonerBeds(map).Count(x => x == otherBed);
                bool notHeld = IntakeSystem.FreePrisonerBeds(map).Count(x => x == otherBed) >= 1 && c.homeBed == otherBed;
                PunishmentUtility.SyncHomeBed(p, c);
                bool dropped = notHeld && c.homeBed == null;
                c.homeBed = cellBed; // the bed they own
                Cellmates.HomeBed(p, c);
                bool droppedOwn = c.homeBed == null;
                if ((!asked && PunishmentUtility.FindPunishmentBed(p, guard) != null) || !noSideEffect || !noted || !back || !dropped || !droppedOwn)
                    problems0.Add($"home bed test: asking for the job to carry them off noted a home bed {!noSideEffect} (the job was made: {asked}); claiming the solitary bed noted the cell bed {noted}; the end of the punishment sent them back {back}; "
                                  + $"a stale home bed was dropped and the bed free for the intake {dropped} (free slots counted before the check: {freeBefore}); owning the home bed drops it {droppedOwn}");
            });
        }

        // ---- B20: the gang war's act is not loud ------------------------------------------------------------------------------

        /// <summary>
        /// The war's own act (an ambush) is not loud: out of every guard's, camera's and tower's sight it is not seen, so it neither reveals the
        /// attacker's gang nor puts a record on their file, while a brawl (loud) always does. It is sanctioned by the warden's policy for fights.
        /// </summary>
        private static void CheckGangAmbushGoesUnseenOutOfSight(Map map)
        {
            Guarded("ambush", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                MisconductDef ambush = DefDatabase<MisconductDef>.GetNamed("RP_Ambush"), fight = DefDatabase<MisconductDef>.GetNamed("RP_Fight");
                var kinds0 = new Dictionary<MisconductDef, Sanction>(g.punishments.kinds);
                r.Add(() => g.punishments.kinds = kinds0);
                g.punishments.SetKind(fight, Sanction.LoseVisits);
                bool policy = !ambush.loud && ambush.sanctionAs == fight && g.punishments.KindFor(ambush) == Sanction.LoseVisits && g.punishments.HoursFor(ambush) == g.punishments.HoursFor(fight);
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                // Somewhere nobody can see: a cell with no guard, camera or tower covering it.
                IntVec3 hidden = IntVec3.Invalid;
                int tried = 0;
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(p.Position, 40f, true))
                {
                    if (!cell.InBounds(map) || !cell.Standable(map) || ++tried > 600) continue;
                    p.Position = cell;
                    p.Notify_Teleported();
                    if (TensionUtility.Watchers(p, pm) <= 0f)
                    {
                        hidden = cell;
                        break;
                    }
                }
                if (!hidden.IsValid)
                {
                    problems0.Add("ambush test: no cell out of every guard's, camera's and tower's sight");
                    return;
                }
                GangDef gang = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
                (c.gang, c.gangKnown) = (gang, false);
                MisconductUtility.Record(p, c, pm, ambush, chronicle: false);
                bool unseen = c.records.Count > 0 && !c.records[c.records.Count - 1].seen && !c.gangKnown && !ITab_InmateFile.ShownRecords(c).Any(rec => rec.def == ambush);
                MisconductUtility.Record(p, c, pm, fight, chronicle: false);
                bool brawlSeen = c.records[c.records.Count - 1].seen && c.gangKnown;
                if (!policy || !unseen || !brawlSeen)
                    problems0.Add($"ambush test: not loud and sanctioned like a fight {policy}; out of sight it left no seen record and revealed nobody {unseen}; a brawl does both {brawlSeen}");
            });
        }

        // ---- B21: the storyteller sees every rival pair ----------------------------------------------------------------------

        /// <summary>With Saints and Ravens in a truce and Ravens and the Brotherhood at war, the pulse still says rival gangs are inside (it compared everyone only with the first gang it met).</summary>
        private static void CheckStorytellerSeesAllRivalGangs(Map map)
        {
            Guarded("rival gangs", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                r.Add(() => g.story.pulse.Update(map)); // (added first, so it runs last, on the restored inmates)
                var gangs0 = map.mapPawns.PrisonersOfColony.Where(x => x.Inmate() != null).Select(x => (c: x.Inmate(), gang: x.Inmate().gang)).ToList();
                r.Add(() =>
                {
                    foreach ((CompInmate c, GangDef gang) in gangs0) c.gang = gang;
                });
                foreach ((CompInmate c, GangDef _) in gangs0) c.gang = null;
                List<GangTruce> truces0 = g.story.truces.ToList();
                r.Add(() => g.story.truces = truces0);
                GangDef saints = DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"), ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"), brotherhood = DefDatabase<GangDef>.GetNamed("RP_Gang_Brotherhood");
                foreach (GangDef gang in new[] { saints, ravens, brotherhood })
                {
                    CompInmate c = Throwaway(map, r).Inmate();
                    (c.gang, c.activeFlag) = (gang, true);
                }
                g.story.truces = new List<GangTruce> { new GangTruce { a = saints, b = ravens, untilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay } };
                g.story.pulse.Update(map);
                bool seen = g.story.pulse.rivalGangs;
                bool onlyTruce = !PrisonPulse.AnyRivals(new List<GangDef> { saints, ravens });
                if (!seen || !onlyTruce)
                    problems0.Add($"rival gangs test: with a truce between two of three gangs and a war between two others the pulse says rival gangs are inside {seen}; two gangs in a truce are not rivals {onlyTruce}");
            });
        }

        // ---- B22: resignations that are not burnouts ----------------------------------------------------------------------------

        /// <summary>A resignation over pay is a resignation, not a burnout; a burnout counts as both; the weekly snapshot counts resignations.</summary>
        private static void CheckResignationsAreNotAllBurnouts(Map map)
        {
            Guarded("resignation counts", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                int now = Find.TickManager.TicksGame;
                Pawn stuck = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), tired = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                stuck.GetComp<CompStaff>().hiredTick = stuck.GetComp<CompStaff>().roleSinceTick = now - (StaffCareers.StagnantDays + 1) * GenDate.TicksPerDay;
                (int burned0, int resigned0) = (g.stats.staffBurnedOut, g.stats.resignations);
                ResignReason pay = StaffCareers.Resign(stuck, stuck.GetComp<CompStaff>());
                (int burned1, int resigned1) = (g.stats.staffBurnedOut, g.stats.resignations);
                ResignReason burnout = StaffCareers.Resign(tired, tired.GetComp<CompStaff>());
                bool week = WeekSnapshot.Take(g, map).resigned == g.stats.resignations;
                if (pay != ResignReason.Pay || burned1 != burned0 || resigned1 != resigned0 + 1 || burnout != ResignReason.Burnout || g.stats.staffBurnedOut != burned1 + 1 || g.stats.resignations != resigned1 + 1 || !week)
                    problems0.Add($"resignation test: a long-serving officer quit for {pay} and the burnout count moved {burned1 - burned0} (0 expected), resignations {resigned1 - resigned0} (1); a tired one quit for {burnout}, burnouts {g.stats.staffBurnedOut - burned1} (1), resignations {g.stats.resignations - resigned1} (1); the weekly snapshot counts resignations {week}");
            });
        }

        // ---- B23: an unread promotion letter ------------------------------------------------------------------------------------

        /// <summary>A promotion letter that times out unanswered leaves the officer alone (no block, no thought, no count; the daily pass offers it again); a "not now" the warden chose still passes them over.</summary>
        private static void CheckUnansweredPromotionIsNeutral(Map map)
        {
            Guarded("promotion timeout", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn p = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff sc = p.GetComp<CompStaff>();
                sc.rank = 0;
                var worker = new PromotionDecision();
                ChoiceLetter_PrisonDecision Letter(bool timedOut)
                {
                    ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Promotion", "test", "test", map);
                    letter.pawns.Add(p);
                    letter.amount = 1;
                    letter.StartTimeout(timedOut ? 0 : GenDate.TicksPerDay);
                    return letter;
                }
                bool Thought() => p.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPCareerDefOf.RP_PassedOver) != null;
                int declined0 = g.stats.promotionsDeclined;
                worker.Choose(Letter(timedOut: true), map, 1);
                bool neutral = g.stats.promotionsDeclined == declined0 && sc.passedOverTick == CompStaff.NotPassedOver && !Thought();
                worker.Choose(Letter(timedOut: false), map, 1);
                bool chosen = g.stats.promotionsDeclined == declined0 + 1 && sc.passedOverTick != CompStaff.NotPassedOver && Thought();
                if (!neutral || !chosen)
                    problems0.Add($"promotion timeout test: an unanswered letter left the officer alone {neutral}; a \"not now\" the warden chose passed them over {chosen}");
            });
        }

        // ---- B24: letters about staff who have died ----------------------------------------------------------------------------------

        /// <summary>A letter answered after its member of staff was killed pays nothing and changes nothing.</summary>
        private static void CheckLettersIgnoreDeadStaff(Map map)
        {
            Guarded("dead staff letters", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn p = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                r.Add(() =>
                {
                    if (p.Corpse is { Destroyed: false } corpse) corpse.Destroy();
                });
                CompStaff sc = p.GetComp<CompStaff>();
                p.Kill(null);
                if (!p.Dead || sc.role == null)
                {
                    problems0.Add("dead staff letters test: set-up is wrong (the guard is not dead, or lost their role)");
                    return;
                }
                ChoiceLetter_PrisonDecision letter = DecisionLetter(map, p);
                (int funds0, int retired0, int leave0, float integrity0) = (g.ledger.funds, g.stats.staffRetired, sc.leaveUntilTick, sc.integrity);
                new StaffDebtDecision().Choose(letter, map, 0);
                new RetireDecision().Choose(letter, map, 0);
                new RetireDecision().Choose(letter, map, 1);
                new RookieDecision().Choose(letter, map, 1);
                new BurnoutDecision().Choose(letter, map, 0);
                new PrisonDecisionWorker_InternalAffairs().Choose(letter, map, 1);
                bool untouched = g.ledger.funds == funds0 && g.stats.staffRetired == retired0 && sc.leaveUntilTick == leave0 && !CorruptionSystem.Suspended(sc) && Mathf.Approximately(sc.integrity, integrity0);
                if (!untouched)
                    problems0.Add($"dead staff letters test: answering letters about a dead guard moved funds by {g.ledger.funds - funds0}, retired {g.stats.staffRetired - retired0}, leave {sc.leaveUntilTick != leave0}, suspended {CorruptionSystem.Suspended(sc)}");
            });
        }

        // ---- B26: the suspended-staff alert ------------------------------------------------------------------------------------------

        /// <summary>A suspended member of staff is at home (off the map): the alert finds them all the same.</summary>
        private static void CheckSuspensionAlertFindsStaffAtHome(Map map)
        {
            Guarded("suspension alert", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (PrisonAlerts.Map == null)
                {
                    problems0.Add("suspension alert test: the alerts cannot be refreshed (the current map is not the prison)");
                    return;
                }
                r.Add(PrisonAlerts.Invalidate);
                Pawn p = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                CorruptionSystem.Suspend(p, p.GetComp<CompStaff>());
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                bool listed = PrisonAlerts.Suspended.Contains(p) && g.home.Contains(p) && new Alert_RP_Suspended().GetReport().active;
                if (!listed)
                    problems0.Add($"suspension alert test: a suspended guard at home is in the alert {PrisonAlerts.Suspended.Contains(p)}");
            });
        }

        // ---- B27: letting someone go who is neither on a map nor at home ------------------------------------------------------------------

        /// <summary>Let go while carried (neither on a map nor held at home): they go once they are back on a map instead of the notice being lost.</summary>
        private static void CheckFireWhileCarried(Map map)
        {
            Guarded("fire while carried", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn p = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff sc = p.GetComp<CompStaff>();
                p.DeSpawn();
                bool away = !p.Spawned && !g.home.Contains(p) && p.Map == null;
                StaffUtility.Fire(p, record: false);
                bool waiting = sc.role != null && sc.pendingFire != 0;
                GenSpawn.Spawn(p, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
                sc.CompTickInterval(250);
                bool gone = sc.role == null && sc.pendingFire == 0 && p.Faction != Faction.OfPlayer;
                if (!away || !waiting || !gone)
                    problems0.Add($"fire while carried test: set-up {away}; the notice was kept for when they are back on a map {waiting}; once back they were let go {gone} (role {sc.role?.defName}, faction {p.Faction?.Name})");
            });
        }

        // ---- B28: retraining starts the promotion clock again --------------------------------------------------------------------------------

        /// <summary>A 30-day lead retrained into another role starts at the bottom and is not eligible again before the days a new officer needs.</summary>
        private static void CheckRetrainingRestartsPromotionClock(Map map)
        {
            Guarded("retraining", map, r =>
            {
                Pawn p = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff sc = p.GetComp<CompStaff>();
                sc.hiredTick = Find.TickManager.TicksGame - 30 * GenDate.TicksPerDay;
                sc.rank = 2;
                p.needs.mood.CurLevel = 1f;
                StaffRoleDef other = StaffUtility.Roles.First(x => x != RPSecurityDefOf.RP_Guard && !x.guardDuties && x.weapon == null && x.companion == null);
                sc.rank = 0;
                bool control = StaffCareers.Eligible(p, sc);
                sc.rank = 2;
                StaffUtility.ApplyRole(p, other, record: false);
                bool restarted = sc.rank == 0 && !StaffCareers.Eligible(p, sc);
                sc.roleSinceTick -= StaffCareers.SeniorDays * GenDate.TicksPerDay + 1;
                bool laterEligible = StaffCareers.Eligible(p, sc);
                if (!control || !restarted || !laterEligible)
                    problems0.Add($"retraining test: a 30-day officer is eligible for senior {control} (set-up); retrained into {other.defName} they start at rank 0 and are not eligible at once {restarted}; after {StaffCareers.SeniorDays} days in the new role they are {laterEligible}");
            });
        }

        // ---- B29: mail ------------------------------------------------------------------------------------------------------------------------

        /// <summary>A lawyer's letters reach an inmate with no family; a guard at home or with Warden work off is not a sorter.</summary>
        private static void CheckMailForTheLonelyAndRealSorters(Map map)
        {
            Guarded("mail lonely and sorters", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                MailSystem mail = g.mail;
                List<MailLetter> waiting0 = mail.waiting.ToList();
                r.Add(() =>
                {
                    mail.waiting.Clear();
                    mail.waiting.AddRange(waiting0);
                });
                Pawn lonely = Throwaway(map, r);
                CompInmate c = lonely.Inmate();
                (c.familyCount, c.relatives) = (0, new List<Relative>());
                c.reputations.Add(DefDatabase<ReputationDef>.GetNamed("RP_JailhouseLawyer"));
                for (int day = 0; day < 60; day++) mail.DailyFor(lonely, c);
                List<MailLetter> theirs = mail.waiting.Where(l => l.inmate == lonely).ToList();
                bool lawyerWrites = theirs.Count > 0 && theirs.All(l => l.kind.minFamily == 0);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                bool onSite = MailSystem.IsSorter(guard);
                guard.workSettings.SetPriority(WorkTypeDefOf.Warden, 0);
                bool workOff = !MailSystem.IsSorter(guard);
                guard.workSettings.SetPriority(WorkTypeDefOf.Warden, 1);
                bool workBack = MailSystem.IsSorter(guard);
                CorruptionSystem.Suspend(guard, guard.GetComp<CompStaff>());
                bool suspended = !MailSystem.IsSorter(guard);
                guard.GetComp<CompStaff>().suspendedUntilTick = -1;
                StaffCommute.Leave(guard);
                bool home = !MailSystem.IsSorter(guard);
                if (!lawyerWrites || !onSite || !workOff || !workBack || !suspended || !home)
                    problems0.Add($"mail test: letters reached an inmate with no family {lawyerWrites} ({theirs.Count} waiting); a guard on site is a sorter {onSite}, not with Warden work off {workOff}, again with it back on {workBack}, not when suspended {suspended}, not at home {home}");
            });
        }

        // ---- B30: grading ------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Finding contraband does not lift security above a prison that never had any; reform follows who has passed a program now and the
        /// last two weeks' paroles, not lifetime ratios; a job nobody works at earns no employment points.
        /// </summary>
        private static void CheckGradesFollowTheDay(Map map)
        {
            Guarded("grading", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DailyIncidents today = g.today;
                var states = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null).Select(x => (c: x.Inmate(), job: x.Inmate().job, work: x.Inmate().lastWorkDay, passed: x.Inmate().passedPrograms.ToList())).ToList();
                r.Add(() =>
                {
                    foreach ((CompInmate c, InmateJobDef job, int work, List<ProgramDef> passed) in states)
                    {
                        c.job = job;
                        c.lastWorkDay = work;
                        c.passedPrograms.Clear();
                        c.passedPrograms.AddRange(passed);
                    }
                });
                // Security: one escape, then a flood of finds.
                today.escapes = 1;
                today.contrabandSmuggled = 0;
                today.contrabandFound = 0;
                float clean = PrisonGrading.Compute(map, g).security;
                today.contrabandFound = 40;
                float findsOnly = PrisonGrading.Compute(map, g).security;
                today.contrabandSmuggled = 2;
                float smuggledAndFound = PrisonGrading.Compute(map, g).security;
                bool security = clean < 100f && Mathf.Approximately(findsOnly, clean) && smuggledAndFound <= clean;
                // Reform: no job work, no program passed (and nobody who stayed out, so that nothing sits at the top of the scale: it is 25 + 40 work + 15 programs + 10 paroles + 20 staying out).
                (int released0, int reoffended0) = (g.stats.released, g.stats.reoffended);
                (g.stats.released, g.stats.reoffended) = (10, 10);
                g.story.formerInmates.Clear();
                InmateJobDef someJob = DefDatabase<InmateJobDef>.AllDefsListForReading.First();
                ProgramDef someProgram = DefDatabase<ProgramDef>.AllDefsListForReading.First();
                foreach ((CompInmate c, InmateJobDef _, int _, List<ProgramDef> _) in states)
                {
                    c.job = someJob;
                    c.lastWorkDay = -1;
                    c.passedPrograms.Clear();
                }
                float idle = PrisonGrading.Compute(map, g).reform;
                foreach ((CompInmate c, InmateJobDef _, int _, List<ProgramDef> _) in states) c.lastWorkDay = Ledger.CurrentDay;
                float working = PrisonGrading.Compute(map, g).reform;
                foreach ((CompInmate c, InmateJobDef _, int _, List<ProgramDef> _) in states) c.passedPrograms.Add(someProgram);
                float schooled = PrisonGrading.Compute(map, g).reform;
                // The lifetime counters no longer move it...
                g.stats.programsPassed += 1000;
                g.stats.admitted += 10;
                g.stats.paroled += 400;
                float lifetime = PrisonGrading.Compute(map, g).reform;
                // ...recent paroles do.
                int now = Find.TickManager.TicksGame;
                for (int i = 0; i < 5; i++) g.story.formerInmates.Add(new FormerInmate { name = "paroled " + i, number = 85000 + i, releasedTick = now, paroled = true, newsTick = -1 });
                float paroledRecently = PrisonGrading.Compute(map, g).reform;
                bool reform = working > idle && schooled > working && Mathf.Approximately(lifetime, schooled) && paroledRecently > lifetime;
                if (!security || !reform)
                    problems0.Add($"grading test: security with finds only {findsOnly} against {clean} with none, and with 2 smuggled {smuggledAndFound} {security}; reform with the job but no work {idle}, working {working}, schooled {schooled}, after lifetime counters {lifetime}, with recent paroles {paroledRecently} {reform}");
            });
        }

        // ---- B31: the utility bill and the bus ------------------------------------------------------------------------------------------------

        /// <summary>The utility bill is the day's average draw, sampled hourly (not the moment of midnight); the bus comes once a day, whatever the hour is moved to.</summary>
        private static void CheckUtilityBillAndBus(Map map)
        {
            Guarded("utility bill and bus", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DailyIncidents today = g.today;
                (int samples0, float sum0) = (today.powerSamples, today.powerWattSum);
                today.powerSamples = 24;
                today.powerWattSum = 24 * 12345f;
                bool average = UtilitySystem.DailyBill(map) == Mathf.RoundToInt(12345f / 100f * UtilitySystem.RatePer100W);
                (today.powerSamples, today.powerWattSum) = (samples0, sum0);
                UtilitySystem.SampleHourly(map, g);
                bool sampled = today.powerSamples == samples0 + 1 && Mathf.Approximately(today.powerWattSum, sum0 + UtilitySystem.Draw(map));
                IntakePolicy intake = g.intake;
                (int hour0, int run0) = (intake.hour, intake.lastRunDay);
                r.Add(() => (intake.hour, intake.lastRunDay) = (hour0, run0));
                intake.hour = 9;
                intake.lastRunDay = -1;
                bool first = IntakeSystem.ShuttleDue(g, 9);
                intake.hour = 15; // the slider moved past the present hour after the bus came
                bool second = IntakeSystem.ShuttleDue(g, 15);
                intake.lastRunDay = Ledger.CurrentDay - 1;
                bool tomorrow = IntakeSystem.ShuttleDue(g, 15);
                bool wrongHour = !IntakeSystem.ShuttleDue(g, 16);
                if (!average || !sampled || !first || second || !tomorrow || !wrongHour)
                    problems0.Add($"utility and bus test: the bill is the average of the day's samples {average}; an hourly sample adds the draw {sampled}; the bus came {first}, a second time the same day {second} (false), the next day {tomorrow}, at another hour {!wrongHour} (false)");
            });
        }

        // ---- B32: smaller items ------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A downed or bedless inmate in a cell does not hold its door open at lockup; a rival is not placed into the cell of someone away in the hole;
        /// an addiction vanilla removed counts as coming clean; a punishment is not written against an unseen record; bureaucracy is booked under its own heading.
        /// </summary>
        private static void CheckSmallerItems(Map map)
        {
            Guarded("smaller items", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                // The door.
                List<Room> rooms = map.regionGrid.AllRooms.Where(x => !x.PsychologicallyOutdoors).Take(2).ToList();
                bool door = rooms.Count == 2 && PrisonMap.HoldsDoorOpen(false, rooms[1], rooms[0]) && !PrisonMap.HoldsDoorOpen(true, rooms[1], rooms[0])
                            && !PrisonMap.HoldsDoorOpen(false, null, rooms[0]) && !PrisonMap.HoldsDoorOpen(false, rooms[0], rooms[0]);
                // A rival is not put into the cell of someone who is away in the hole.
                List<GangTruce> truces0 = g.story.truces.ToList();
                r.Add(() => g.story.truces = truces0);
                g.story.truces = new List<GangTruce>();
                Building_Bed bed = pm.PrisonerBeds.FirstOrDefault(x => x.Spawned && x.GetRoom() is { IsPrisonCell: true });
                Pawn away = Throwaway(map, r), newcomer = Throwaway(map, r);
                (away.Inmate().gang, newcomer.Inmate().gang) = (DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"), DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"));
                bool rival = bed == null;
                float score0 = 0f, score1 = 0f;
                if (bed != null)
                {
                    score0 = Cellmates.MateScore(bed, newcomer.Inmate());
                    away.Inmate().homeBed = bed;
                    score1 = Cellmates.MateScore(bed, newcomer.Inmate());
                    away.Inmate().homeBed = null;
                    rival = score1 <= score0 - 10f + 0.001f;
                }
                // Coming clean.
                Pawn addict = Throwaway(map, r);
                foreach (Hediff existing in addict.health.hediffSet.hediffs.Where(h => h is Hediff_Addiction).ToList()) addict.health.RemoveHediff(existing); // (arrivals can come addicted)
                Hediff supervision = addict.health.AddHediff(RPDetoxDefOf.RP_SupervisedDetox);
                int clean0 = g.stats.gotClean;
                Detox.Fade(g, addict, supervision);
                bool clean = g.stats.gotClean == clean0 + 1 && !addict.health.hediffSet.HasHediff(RPDetoxDefOf.RP_SupervisedDetox);
                // A punishment is written against a seen record.
                Pawn offender = Throwaway(map, r);
                List<MisconductDef> defs = DefDatabase<MisconductDef>.AllDefsListForReading;
                CompInmate oc = offender.Inmate();
                oc.records.Add(new MisconductRecord { def = defs[0], tick = Find.TickManager.TicksGame, seen = true });
                oc.records.Add(new MisconductRecord { def = defs[1], tick = Find.TickManager.TicksGame, seen = false });
                oc.MarkLastRecordPunished();
                bool marked = oc.records[0].punished && !oc.records[1].punished;
                // Bureaucracy has its own ledger heading.
                bool ledger = true;
                BureaucracySystem bureaucracy = g.bureaucracy;
                (BureaucracyDef active0, float progress0) = (bureaucracy.active, bureaucracy.progress);
                r.Add(() => (bureaucracy.active, bureaucracy.progress) = (active0, progress0));
                bureaucracy.active = null;
                BureaucracyDef project = bureaucracy.All.FirstOrDefault(bureaucracy.Available);
                if (project != null)
                {
                    g.ledger.funds = Mathf.Max(g.ledger.funds, project.cost + 1);
                    int entries = g.ledger.recent.Count;
                    bureaucracy.TryStart(project);
                    ledger = g.ledger.recent.Skip(entries).Any(e => e.category == RPDefOf.RP_Bureaucracy && e.amount == -project.cost) && !g.ledger.recent.Skip(entries).Any(e => e.category == RPDefOf.RP_Hiring);
                }
                if (!door || !rival || !clean || !marked || !ledger)
                    problems0.Add($"smaller items test: a downed or bedless inmate does not hold a cell door open {door}; a rival is scored against someone away in the hole {rival} ({score0} then {score1}); a supervised inmate with no addiction left came clean {clean}; "
                                  + $"the punishment went on the seen record {marked}; bureaucracy is booked under its own heading {ledger}");
            });
        }
    }
}
