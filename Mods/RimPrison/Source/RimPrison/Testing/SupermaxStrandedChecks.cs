using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The supermax inmate left outside the cell with no guard (a showcase run in three to six: "a supermax inmate was outside their cell with no guard for 12 hourly looks"). The run's log: a staff strike
    /// (the demand letter unanswered, "the staff walked out for 12 hours") began an hour before a walk back from the shower ended in a break, and another inmate was moved into the unit after a killing;
    /// every guard took no work for the twelve hours (a striker takes none but the fire and the injured), so nobody fetched either, and both stood where they were with the meal in the cell.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckSupermaxStranded(Map map)
        {
            CheckSupermaxStrikersFetchThemHome(map);
            CheckSupermaxStrikeAfterABreak(map);
            CheckSupermaxGuardHurriesToTheInmate(map);
        }

        /// <summary>
        /// Every member of staff on the map (the real ones too: they would come for the inmate, unstruck) and at home (one who comes in at the hour's change during the check would come unstruck, and a
        /// fetch by them would pass the check with the fix taken out) on strike for the check, put back by the rollback.
        /// </summary>
        private static void SupermaxStrikeAll(Map map, Rollback r, params Pawn[] throwaways)
        {
            IEnumerable<Pawn> home = PrisonGame.Instance.home?.InnerListForReading ?? Enumerable.Empty<Pawn>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Concat(home).Concat(throwaways).Distinct().ToList())
            {
                if (p.GetComp<CompStaff>() is not { } sc) continue;
                int was = sc.strikeUntilTick;
                r.Add(() => sc.strikeUntilTick = was);
                sc.strikeUntilTick = Find.TickManager.TicksGame + IncidentWorker_StaffStrike.StrikeHours * GenDate.TicksPerHour;
            }
        }

        /// <summary>The look at an inmate for the line of a check that failed.</summary>
        private static string SupermaxStrandedLine(Pawn p, CompInmate c, SupermaxScene s, params Pawn[] guards)
        {
            InmateCandidates.Shared.Invalidate();
            var giver = new WorkGiver_EscortInmate();
            return $"at {p.Position - s.origin} from the unit's corner (hour {(p.Map != null ? GenLocalDate.HourOfDay(p.Map) : -1)}) on {p.CurJobDef?.defName}, mental {p.MentalStateDef?.defName ?? "none"}, stranded {EscortWalk.Stranded(p, c)}, due {SupermaxSystem.DueFor(p, c, PrisonGame.Instance)?.ToString() ?? "nothing"}, "
                   + $"leg {c.leg != null}, forced {c.forcedActivity?.defName ?? "none"}, punishment due {c.pendingPunishmentHours} h, downed {p.Downed}, candidate {SupermaxSystem.Candidate(p)}, "
                   + string.Join(", ", guards.Select(g => $"{g.LabelShort} {g.CurJobDef?.defName ?? "idle"} on {g.timetable?.CurrentAssignment?.defName ?? "no timetable"} striking {IncidentWorker_StaffStrike.Striking(g)} offered {giver.HasJobOnThing(g, p)} fetch {SupermaxSystem.FetchJobFor(g) != null}"));
        }

        /// <summary>
        /// On strike, the guards take no new work, but the walk of a supermax inmate who is out of the cell with nobody back to it (not the shower or the yard of another, who stay in their cells), though a
        /// restraint is waiting for them too (the scan's first find, which a strike throws away). (A striker was offered nothing at all, and the inmate stood where they were until the strike was over.)
        /// </summary>
        private static void CheckSupermaxStrikersFetchThemHome(Map map)
        {
            Guarded("supermax: on strike the guards still see a supermax inmate back into the cell", map, r =>
            {
                const string check = "strike fetch";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn striker = s.Guard("Zstrg1", s.CorridorEast);
                striker.workSettings?.SetPriority(WorkTypeDefOf.Warden, 1);
                // The one outside the cell, the one in it who is due for a shower, and one who owes the hole (the restraint a striker's scan finds first).
                Pawn a = s.Inmate("Zstr1", s.bedA, s.CorridorWest), b = s.Inmate("Zstr2", s.bedB, s.CellB);
                Pawn owes = s.Inmate("Zstr3", s.bedC, s.CellC, InmateUtility.DefaultSecurity);
                CompInmate ac = a.Inmate(), bc = b.Inmate(), oc = owes.Inmate();
                r.Add(() => oc.pendingPunishmentHours = 0);
                oc.pendingPunishmentHours = 8;
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_ShowerTime);
                SupermaxStrikeAll(map, r, striker);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                InmateCandidates.Shared.Invalidate();
                if (EscortWalk.InOwnCell(a) || !SupermaxSystem.Candidate(a)) { s.Fail($"the check's own premise: the inmate outside the cell is not a candidate for a walk home ({SupermaxStrandedLine(a, ac, s, striker)})"); return; }
                int ticked = ShuttleTicks(() => EscortWalk.InOwnCell(a) && ac.leg == null && SupermaxWalkOver(striker, ac, 40), 5000);
                if (!(EscortWalk.InOwnCell(a) && ac.leg == null))
                {
                    s.Fail($"on strike nobody saw the supermax inmate back into the cell in {ticked} ticks: {SupermaxStrandedLine(a, ac, s, striker)}");
                    return;
                }
                // Back in the cell, they are due a shower, as is the other: the strike takes no new work, and a walk to the shower is new work. (The strike's job filter throws a shower walk away on
                // its own; this also holds with the line in SupermaxSystem.TryPlan taken out, which only spares the offer.)
                Pawn[] walked = { a, b };
                int stay = ShuttleTicks(() => walked.Any(p => !EscortWalk.InOwnCell(p) || p.Inmate().leg != null) || striker.CurJobDef == RPSupermaxDefOf.RP_EscortInmate, 2500);
                if (walked.Any(p => !EscortWalk.InOwnCell(p) || p.Inmate().leg != null) || striker.CurJobDef == RPSupermaxDefOf.RP_EscortInmate)
                    s.Fail($"on strike a guard walked an inmate to the shower ({stay} ticks in: guard on {striker.CurJobDef?.defName ?? "nothing"}, {a.LabelShort} in the cell {EscortWalk.InOwnCell(a)}, {b.LabelShort} in the cell {EscortWalk.InOwnCell(b)}, {bc.leg != null})");
                striker.jobs.StopAll();
                foreach (Pawn p in new[] { a, b }) EscortWalk.End(p, p.Inmate());
            });
        }

        /// <summary>
        /// A guard sent for a supermax inmate who is out of the cell goes to them at a jog and walks them home at a walk (the pace the inmate keeps). The showcase's fetch of the inmate moved into the unit at the
        /// start took two to three hourly looks, and one that came from the gate at the shift's change, six: all of it was walked, across the prison, and a guard answering a call is not strolling.
        /// </summary>
        private static void CheckSupermaxGuardHurriesToTheInmate(Map map)
        {
            Guarded("supermax: the guard jogs to the inmate left outside and walks them home", map, r =>
            {
                const string check = "pace";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zpaceg1", s.CorridorEast);
                Pawn a = s.Inmate("Zpace1", s.bedA, s.CorridorWest);
                CompInmate ac = a.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPSupermaxDefOf.RP_YardAlone);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                Job job = SupermaxOffer(s, guard, a, "the walk home", HomeWalk);
                if (job == null) return;
                s.Start(guard, job);
                var approach = new List<LocomotionUrgency>();
                var led = new List<LocomotionUrgency>();
                int ticked = 0;
                while (ticked < 4000 && !SupermaxWalkOver(guard, ac, ticked))
                {
                    SupermaxTick();
                    ticked++;
                    if (guard.CurJobDef != RPSupermaxDefOf.RP_EscortInmate || !guard.pather.Moving) continue;
                    (ac.leg == null ? approach : led).Add(guard.CurJob.locomotionUrgency);
                }
                if (approach.Count < 5 || led.Count < 5 || approach.Any(u => u != LocomotionUrgency.Jog) || led.Any(u => u != LocomotionUrgency.Walk))
                    s.Fail($"the guard's pace to the inmate left outside and home with them: going {approach.Count} steps ({string.Join(",", approach.Distinct())}; jog expected), leading {led.Count} steps ({string.Join(",", led.Distinct())}; walk expected), over after {ticked} ticks");
                guard.jobs.StopAll();
                EscortWalk.End(a, ac);
            });
        }

        /// <summary>
        /// The log's case: a strike on, and a supermax inmate on the walk back from the shower has a break (a smashed shower: the walk ends, the misconduct is seen and the hole is owed); when the break is over a
        /// striking guard fetches them home (the restraint they owe is not what a striker takes up).
        /// </summary>
        private static void CheckSupermaxStrikeAfterABreak(Map map)
        {
            Guarded("supermax: on strike a walk ended by a break is followed by the walk home", map, r =>
            {
                const string check = "strike break";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn lead = s.Guard("Zbrkg1", s.CorridorWest), striker = s.Guard("Zbrkg2", s.CorridorEast);
                striker.workSettings?.SetPriority(WorkTypeDefOf.Warden, 1);
                Pawn a = s.Inmate("Zbrk1", s.bedA, s.CellA);
                CompInmate ac = a.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_ShowerTime);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                Job job = SupermaxOffer(s, lead, a, "the walk to the shower", EscortKind.Shower);
                if (job == null) return;
                s.Start(lead, job);
                // On the way back (the shower used, turned for the cell), the strike begins and the inmate smashes the shower.
                int ticked = ShuttleTicks(() => ac.leg is { phase: EscortPhase.Back } && !s.InCell(a, s.bedA), 6000);
                if (ac.leg is not { phase: EscortPhase.Back } || s.InCell(a, s.bedA)) { s.Fail($"the walk did not turn for the cell after {ticked} ticks (leg {ac.leg?.phase.ToString() ?? "none"}, at {a.Position - s.origin}, doing {a.CurJobDef?.defName})"); return; }
                a.needs.TryGetNeed(RPDefOf.RP_Hygiene).CurLevel = 1f;
                SupermaxStrikeAll(map, r, lead, striker);
                // (On hand, and on nothing, when the strike begins: through the walk the striker was free to take real work in the prison, a patrol or a carry far off, eating into the two hours.)
                striker.jobs.StopAll();
                striker.pather.StopDead();
                striker.Position = s.CorridorEast;
                striker.Notify_Teleported();
                // (The misconduct itself, not the hourly roll's pick: the inmate's traits decide what the roll could pick.)
                if (!a.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Vandalizing, null, forced: true, otherPawn: null) || a.MentalState is not MentalState_Vandalizing smash) { s.Fail("the check's own premise: the inmate could not be set to smash the shower"); return; }
                smash.target = s.shower.parent;
                MisconductUtility.Record(a, ac, s.pm, RPMisconductDefOf.RP_Vandalism);
                ShuttleTicks(() => !a.InMentalState, GenDate.TicksPerHour);
                if (a.InMentalState) a.MentalState.RecoverFromState();
                // The walk is over, the inmate is out of the cell, nobody has them: two hours for a guard.
                if (ac.leg != null || s.InCell(a, s.bedA)) EscortWalk.End(a, ac);
                InmateCandidates.Shared.Invalidate();
                if (!EscortWalk.Stranded(a, ac)) { s.Fail($"the check's own premise: after the break the inmate is not out of the cell with nobody: {SupermaxStrandedLine(a, ac, s, lead, striker)}"); return; }
                int waited = ShuttleTicks(() => EscortWalk.InOwnCell(a) && ac.leg == null || ac.forcedActivity != null || a.Downed, 2 * GenDate.TicksPerHour);
                if (ac.forcedActivity != null || a.Downed)
                    s.Fail($"the check's own premise: the inmate was taken off another way ({waited} ticks in: forced {ac.forcedActivity?.defName ?? "none"}, downed {a.Downed}): {SupermaxStrandedLine(a, ac, s, lead, striker)}");
                else if (!(EscortWalk.InOwnCell(a) && ac.leg == null))
                    s.Fail($"on strike the inmate whose walk a break ended was still outside their cell {waited} ticks after the break, with no guard: {SupermaxStrandedLine(a, ac, s, lead, striker)}");
                foreach (Pawn guard in new[] { lead, striker }) guard.jobs.StopAll();
                a.jobs.StopAll();
                EscortWalk.End(a, ac);
                a.MentalState?.RecoverFromState();
            });
        }
    }
}
