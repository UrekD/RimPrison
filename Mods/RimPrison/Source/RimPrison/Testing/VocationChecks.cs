using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks for vocational training and prison industries (gap analysis 4, section 1.7): the tailoring course (the classroom, then a tailor bench in the workshop) and the tailoring job it opens, uniforms
    /// sewn into the clean stock with the surplus sold at the export run, the kitchen safety course that gates the kitchen job and lowers the food scare's chance, and a certified trade's effect on parole (the
    /// job in place, the reoffend band). Each is self-contained, on throwaway pawns, rolled back (<see cref="Rollback"/>, and the hourly job assignment's changes to the real inmates' jobs); the tailors run real ticks at the prison's own bench. Called once from the events plan.
    /// Each fails without the feature it is named for.
    /// </summary>
    public static partial class AutoTest
    {
        private static bool vocationRan;

        /// <summary>The events plan, once the events have fired.</summary>
        private static void CheckVocationOnce(Map map)
        {
            if (vocationRan) return;
            vocationRan = true;
            CheckVocation(map);
        }

        private static void CheckVocation(Map map)
        {
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns);
            CheckVocationTailoringCourse(map);
            CheckVocationBenchContention(map);
            CheckVocationTailorsSewUniforms(map);
            // (These two run no ticks, so the real inmates' jobs are as they were when they are over.)
            var jobs0 = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null).Select(x => (c: x.Inmate(), job: x.Inmate().job, locked: x.Inmate().jobLocked)).ToList();
            CheckVocationKitchenSafety(map);
            CheckVocationFoodScareOdds(map);
            foreach ((CompInmate c, InmateJobDef job, bool locked) in jobs0)
                if (c.job != job || c.jobLocked != locked) problems0.Add($"vocation checks changed the job of {c.Pawn.LabelShort} from {job?.defName ?? "none"} (locked {locked}) to {c.job?.defName ?? "none"} (locked {c.jobLocked})");
            // (The parole check delivers news from the register, which counts it and sends letters: both are put back, and it runs no ticks.)
            (int news0, int letters0) = (PrisonGame.Instance.stats.formerNews, Find.LetterStack.LettersListForReading.Count);
            CheckVocationTradeOnParole(map);
            if (PrisonGame.Instance.stats.formerNews != news0 || Find.LetterStack.LettersListForReading.Count != letters0)
                problems0.Add($"the parole check left the former inmates' news count at {PrisonGame.Instance.stats.formerNews} (was {news0}) and {Find.LetterStack.LettersListForReading.Count - letters0} letters behind");
            Pawn[] left = LeftBehind(map, pawnsBefore).Where(x => x.Name is NameSingle && x.LabelShort.StartsWith("Zvoc")).ToArray();
            if (left.Length > 0) problems0.Add($"vocation checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        private static void VocFail(string check, string what) => problems0.Add($"vocation ({check}): {what}");

        /// <summary>A made-up minimum-security inmate with nothing passed, calm, skilled enough that an exam is passed for certain.</summary>
        private static Pawn VocationInmate(Map map, Rollback r, string name)
        {
            // (One whose backstory leaves them able at both skills: a skill that is switched off reads as level 0, and the exam is not certain then.)
            Pawn p = null;
            for (int tries = 0; tries < 30; tries++)
            {
                p = Throwaway(map, r, DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum"));
                if (p.skills != null && !p.skills.GetSkill(SkillDefOf.Crafting).TotallyDisabled && !p.skills.GetSkill(SkillDefOf.Cooking).TotallyDisabled) break;
                if (tries < 29) p.Destroy(DestroyMode.Vanish);
            }
            p.Name = new NameSingle("Zvoc" + name);
            CompInmate c = p.Inmate();
            (c.tension, c.suppressed, c.referral, c.job, c.jobWaiting, c.hoursWorked, c.programsPassed) = (0f, 0f, null, null, false, 0, 0);
            // (Rested and fed: a tired inmate goes to bed in the middle of a check that runs real ticks.)
            if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
            if (p.needs?.food != null) p.needs.food.CurLevel = 1f;
            c.passedPrograms.Clear();
            c.programProgress.Clear();
            foreach (SkillDef skill in new[] { SkillDefOf.Crafting, SkillDefOf.Cooking })
                if (p.skills?.GetSkill(skill) is { } record) record.Level = 20;
            return p;
        }

        /// <summary>The program's sessions, one after another, as the hourly pass would run them (a room, a class, the teacher on it for the whole hour, the attendee in it): the check's own, not the game's.</summary>
        private static bool VocationRunSessions(Map map, PrisonMap pm, ProgramDef prog, Pawn p, int count, string check)
        {
            for (int i = 0; i < count; i++)
            {
                if (!VocationAnchor(map, pm, prog, out IntVec3 anchor))
                {
                    VocFail(check, $"no room for {prog.defName} (session {i + 1})");
                    return false;
                }
                int now = Find.TickManager.TicksGame;
                var s = new ProgramSession { program = prog, startTick = now - 200, endTick = now, anchor = anchor, taughtTicks = 200, attendees = new List<Pawn> { p } };
                s.listenedTicks[p] = 200;
                ProgramSystem.Complete(pm, s);
            }
            return true;
        }

        /// <summary>A seat of the program in its zone: the room the check's own session sits in (what the venue search finds, but not turned away by a seat an inmate happens to be sitting at, or a meal: those are checked on their own).</summary>
        private static bool VocationAnchor(Map map, PrisonMap pm, ProgramDef prog, out IntVec3 anchor)
        {
            anchor = IntVec3.Invalid;
            if (prog.Seats.Count == 0) return ProgramSystem.TryFindVenue(map, pm, prog, out anchor, out _, out _);
            foreach (ThingDef seat in prog.Seats)
                foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(seat))
                    if (pm.zones.ZoneAt(b.Position) == prog.Zone)
                    {
                        anchor = b.Position;
                        return true;
                    }
            return false;
        }

        /// <summary>The inmates' jobs as they are, put back by the rollback (the hourly job assignment hands out and takes away jobs for the whole prison).</summary>
        private static void VocationKeepJobs(Map map, Rollback r)
        {
            foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned)
                if (x.Inmate() is { } c)
                {
                    (InmateJobDef job, bool locked) = (c.job, c.jobLocked);
                    r.Add(() => (c.job, c.jobLocked) = (job, locked));
                }
        }

        /// <summary>The prison's Exports zone is the one cell <paramref name="mark"/> (its own cells, and what lies in them, are set aside, and put back, with the export contract's figures the run changes).</summary>
        private static void VocationExportsAt(Map map, PrisonMap pm, Rollback r, IntVec3 mark)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonZoneDef exports = RPDefOf.RP_Exports;
            List<IntVec3> realCells = pm.zones.CellsOf(exports).ToList();
            var set = new List<(Thing thing, IntVec3 cell)>();
            foreach (IntVec3 cell in realCells)
                foreach (Thing t in cell.GetThingList(map).Where(t => t.def.category == ThingCategory.Item).ToList())
                {
                    set.Add((t, cell));
                    t.DeSpawn();
                }
            foreach (IntVec3 cell in realCells) pm.zones.Set(cell, null);
            PrisonZoneDef markWas = pm.zones.ZoneAt(mark);
            pm.zones.Set(mark, exports);
            StorySystem story = g.story;
            (float rate0, int day0, int runs0, int delivered0) = (story.platesPerDay, story.lastExportRunDay, story.contractRunsLeft, story.contractDelivered);
            r.Add(() =>
            {
                pm.zones.Set(mark, markWas);
                foreach (IntVec3 cell in realCells) pm.zones.Set(cell, exports);
                foreach ((Thing thing, IntVec3 cell) in set) GenSpawn.Spawn(thing, cell, map);
                (story.platesPerDay, story.lastExportRunDay, story.contractRunsLeft, story.contractDelivered) = (rate0, day0, runs0, delivered0);
            });
        }

        // ---- the tailoring course -----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The tailoring course: the classroom part first (an inmate cannot take the bench part without it), three sessions, and it is no program of its own (parole and the reoffend roll count it not); then the bench
        /// part, taught at a tailor bench of the workshop (the attendee is sent to the bench, and listening there is credited), three sessions; passing it certifies the trade and opens the tailoring job, which no one
        /// who has not passed holds.
        /// </summary>
        private static void CheckVocationTailoringCourse(Map map)
        {
            Guarded("vocation: the tailoring course", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                ProgramDef theory = DefDatabase<ProgramDef>.GetNamed("RP_TailoringTheory"), bench = RPVocationDefOf.RP_Tailoring;
                InmateJobDef job = RPVocationDefOf.RP_JobTailoring;
                List<ProgramSession> sessions0 = pm.sessions;
                pm.sessions = new List<ProgramSession>();
                r.Add(() => pm.sessions = sessions0);
                Pawn p = VocationInmate(map, r, "student");
                CompInmate c = p.Inmate();
                c.job = job;
                if (!theory.Eligible(p, c)) VocFail("course", "an inmate who may hold the tailoring job is not offered the classroom part");
                if (bench.Eligible(p, c)) VocFail("course", "the bench part is open to an inmate who has not passed the classroom part");
                if (LaborUtility.Eligible(c, job)) VocFail("course", "an inmate who has passed nothing may hold the tailoring job");
                if (new RegimeWorker_Work().TryGiveJob(p, c, pm) != null) VocFail("course", "an inmate who has passed nothing is given tailoring work");
                if (Vocation.Trade(c) != null) VocFail("course", "an inmate who has passed nothing has a trade");
                // The classroom part: three sessions, then it is passed; it counts for nothing in the tally parole reads.
                if (!VocationRunSessions(map, pm, theory, p, 2, "course")) return;
                if (c.passedPrograms.Contains(theory) || c.programProgress.GetValueOrDefault(theory) != 2) VocFail("course", $"two classroom sessions gave progress {c.programProgress.GetValueOrDefault(theory)} (passed {c.passedPrograms.Contains(theory)})");
                if (!VocationRunSessions(map, pm, theory, p, 1, "course")) return;
                if (!c.passedPrograms.Contains(theory)) VocFail("course", "three classroom sessions (with every exam certain) did not pass the classroom part");
                if (c.programsPassed != 0) VocFail("course", $"the classroom part counted as a program of its own ({c.programsPassed} passed)");
                if (!bench.Eligible(p, c)) VocFail("course", "the bench part is closed to an inmate who has passed the classroom part");
                if (LaborUtility.Eligible(c, job)) VocFail("course", "the tailoring job is open after the classroom part alone");
                if (Vocation.Trade(c) != null) VocFail("course", "the classroom part alone certified a trade");
                // The bench part: the seat is the bench; the attendee, set down in the workshop, is sent to it and listens there.
                pm.sessions.Clear();
                if (!ProgramSystem.TryFindVenue(map, pm, bench, out IntVec3 anchor, out int seats, out _) || seats < 1)
                {
                    VocFail("course", "the tailoring course has no room: no tailor bench in a workshop room");
                    return;
                }
                CompWorkstation station = pm.Workstations.FirstOrDefault(w => Tailoring.IsBench(w, pm));
                if (station == null)
                {
                    VocFail("course", "no tailor bench in the workshop");
                    return;
                }
                // (The wait first, then the attendee set down at the bench: set down before it, they spent up to an hour of real ticks on the regime's errands - their cell, the canteen - and the walk back
                // to the bench outlasted the 600-tick session, "listened 0 ticks at the bench in 900" with them standing at it at the end.)
                ClearOfTheHourChange(map, 900);
                // (After the wait, which can tick across the hour a real class starts in: the real inmates are sent off the bench at the last moment.)
                ClearTheBenchOfRealInmates(map, station.parent);
                IntVec3 waitedAt = p.Position;
                p.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                MoveTo(p, station.parent.InteractionCell);
                // (The wait ticks through an hour change, whose pass can start a real class: one that has the attendee would take the listening first, as the attend job asks for the first live session they
                // are in. Only the check's own session is theirs.)
                List<string> theirsBefore = pm.sessions.Where(x => x.attendees.Contains(p)).Select(x => $"{x.program?.defName} ({x.endTick - Find.TickManager.TicksGame} ticks left)").ToList();
                pm.sessions.RemoveAll(x => x.attendees.Contains(p));
                if (theirsBefore.Count > 0) RPLog.Message($"[RimPrison:test] vocation (course): the attendee was in a real session started in the wait: {string.Join(", ", theirsBefore)}; taken off it");
                int now = Find.TickManager.TicksGame;
                // (The session lives for the whole measured window and a beat after it: the beat completes a session that is over, and the attend job leaves when none is found. It was 600 ticks of a
                // 900-tick window: "the session active False (-300 ticks left)" at the end, as it had to be.)
                var live = new ProgramSession { program = bench, startTick = now, endTick = now + VocCourseWindow + 250, anchor = anchor, attendees = new List<Pawn> { p } };
                pm.sessions.Add(live);
                Job attend = ProgramSystem.AttendJob(p, live, pm);
                if (attend?.def != RPProgramDefOf.RP_AttendClass || attend.targetA.Thing is not { } seat || !bench.Seats.Contains(seat.def))
                    VocFail("course", $"the attendee of the bench part is sent to {attend?.def?.defName} at {attend?.targetA} (not a tailor bench)");
                else
                {
                    IntVec3 startedAt = p.Position;
                    // (Who ended the attend job, and how: an end through the job tracker is caught with its condition, the session as the listening saw it and the calls; a job started over it is not, and the line
                    // says what came instead.)
                    vocWatchedJob = attend;
                    vocWatchedSession = live;
                    vocWatchedEnd = null;
                    r.Add(() =>
                    {
                        vocWatchedJob = null;
                        vocWatchedSession = null;
                        vocWatchedEnd = null;
                    });
                    p.jobs.StartJob(attend, JobCondition.InterruptForced);
                    // (When the listening began, and the jobs they had on the way: the line says whether the walk, a job taken from them, or the session itself kept the credit off.)
                    int listenFrom = -1, attendEnded = -1;
                    string afterAttend = null;
                    var jobsSeen = new List<string>();
                    ShuttleTicks(() =>
                    {
                        string jobNow = p.CurJobDef?.defName ?? "none";
                        if (jobsSeen.Count == 0 || jobsSeen[jobsSeen.Count - 1] != jobNow) jobsSeen.Add(jobNow);
                        if (listenFrom < 0 && p.CurJob == attend && p.jobs.curDriver?.CurToilString == "Listen") listenFrom = Find.TickManager.TicksGame - now;
                        if (attendEnded < 0 && p.CurJob != attend)
                        {
                            attendEnded = Find.TickManager.TicksGame - now;
                            afterAttend = $"{jobNow}, {VocSessionsSeenBy(p, pm, live)}";
                        }
                        return live.listenedTicks.GetValueOrDefault(p) >= 300;
                    }, VocCourseWindow);
                    vocWatchedJob = null;
                    string howEnded = attendEnded < 0 ? "the attend job ran to the end of the window"
                        : $"the attend job was over {attendEnded} ticks in ({vocWatchedEnd ?? "not ended through the job tracker: a job was started over it"}; then {afterAttend})";
                    if (live.listenedTicks.GetValueOrDefault(p) < 300) VocFail("course", $"the attendee listened {live.listenedTicks.GetValueOrDefault(p)} ticks at the bench in {VocCourseWindow} (the wait left them at {waitedAt}, set down at {startedAt}, the listening began {(listenFrom < 0 ? "never" : listenFrom + " ticks in")}, jobs on the way [{string.Join(" > ", jobsSeen)}]; {howEnded}; job {p.CurJobDef?.defName} at {p.Position} in toil {p.jobs.curDriver?.CurToilIndex} {p.jobs.curDriver?.CurToilString}, moving {p.pather.Moving}, busy stance {p.stances?.FullBodyBusy}, mental {p.MentalStateDef?.defName}, bench {seat.Position}, its interaction cell {seat.InteractionCell}; the session active {live.Active} ({live.endTick - Find.TickManager.TicksGame} ticks left), listed {pm.sessions.Contains(live)}, found for them {ProgramSystem.SessionOf(p, pm) == live}; the bench is reserved by [{string.Join(", ", map.reservationManager.ReservationsReadOnly.Where(res => res.Target.Thing == seat).Select(res => res.Claimant.LabelShort + " " + res.Job?.def.defName))}])");
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                }
                pm.sessions.Clear();
                if (!VocationRunSessions(map, pm, bench, p, 3, "course")) return;
                if (!c.passedPrograms.Contains(bench)) VocFail("course", "three bench sessions (with every exam certain) did not pass the tailoring course");
                if (c.programsPassed != 1) VocFail("course", $"the tailoring course counted as {c.programsPassed} programs passed (one)");
                if (Vocation.Trade(c) != bench) VocFail("course", $"the tailoring course certified {Vocation.Trade(c)?.defName ?? "no trade"}");
                if (!LaborUtility.Eligible(c, job)) VocFail("course", "the tailoring job is closed to an inmate who passed the whole course");
            });
        }

        // ---- the tailors ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A class that sits at the benches and the tailors who work at them: a bench the squatter (anyone not sewing) has reserved is no seat for the course, a bench a tailor is sewing at still is one; the class that
        /// starts takes its room and the tailor's cycle ends (unpaid), and the tailor is given no work at a bench the class holds.
        /// </summary>
        private static void CheckVocationBenchContention(Map map)
        {
            Guarded("vocation: tailors and the bench class", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                ProgramDef theory = DefDatabase<ProgramDef>.GetNamed("RP_TailoringTheory"), bench = RPVocationDefOf.RP_Tailoring;
                CompWorkstation station = pm.Workstations.FirstOrDefault(w => Tailoring.IsBench(w, pm));
                if (station == null)
                {
                    VocFail("contention", "no tailor bench in the workshop");
                    return;
                }
                ClearTheBenchOfRealInmates(map, station.parent);
                List<ProgramSession> sessions0 = pm.sessions;
                pm.sessions = new List<ProgramSession>();
                r.Add(() => pm.sessions = sessions0);
                g.ledger.funds += 1000;
                VocationExportsAt(map, pm, r, station.parent.InteractionCell);
                (int stock0, int sewn0, int surplus0) = (g.laundry.stock, g.laundry.sewn, g.laundry.surplus);
                r.Add(() => (g.laundry.stock, g.laundry.sewn, g.laundry.surplus) = (stock0, sewn0, surplus0));
                g.laundry.surplus = 0;
                if (!ProgramSystem.TryFindVenue(map, pm, bench, out _, out int seats0, out _) || seats0 < 1)
                {
                    VocFail("contention", "the tailoring course has no seat at a free bench");
                    return;
                }
                // Someone else (a squatter, not sewing, not at a class) has the bench reserved: one seat less.
                Pawn squatter = VocationInmate(map, r, "squatter");
                MoveTo(squatter, station.parent.InteractionCell);
                Job wait = JobMaker.MakeJob(JobDefOf.Wait);
                wait.expiryInterval = 6000;
                squatter.jobs.StartJob(wait, JobCondition.InterruptForced);
                if (!map.reservationManager.Reserve(squatter, wait, station.parent)) VocFail("contention", "the check's squatter could not reserve the bench");
                ProgramSystem.TryFindVenue(map, pm, bench, out _, out int seatsTaken, out _);
                if (seatsTaken != seats0 - 1) VocFail("contention", $"a bench someone else has reserved counts as a seat for the course ({seatsTaken} seats of {seats0}, one expected fewer)");
                map.reservationManager.ReleaseAllClaimedBy(squatter);
                squatter.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                // A tailor at work at the bench: its bench is a seat still, and the class that starts takes the bench from it.
                Pawn tailor = VocationInmate(map, r, "sewer");
                CompInmate tc = tailor.Inmate();
                tc.passedPrograms.Add(theory);
                tc.passedPrograms.Add(bench);
                tc.job = RPVocationDefOf.RP_JobTailoring;
                MoveTo(tailor, station.parent.InteractionCell);
                ClearOfTheHourChange(map, 600);
                ClearTheBenchOfRealInmates(map, station.parent);
                Job sew = new RegimeWorker_Work().TryGiveJob(tailor, tc, pm);
                if (sew?.def != RPLaborDefOf.RP_Sew)
                {
                    VocFail("contention", $"the tailor is given {sew?.def?.defName ?? "no work"} (surplus {g.laundry.surplus}, funds {g.ledger.funds})");
                    return;
                }
                tailor.jobs.StartJob(sew, JobCondition.InterruptForced);
                if (tailor.CurJobDef != RPLaborDefOf.RP_Sew) VocFail("contention", $"the tailor's job is {tailor.CurJobDef?.defName}");
                ProgramSystem.TryFindVenue(map, pm, bench, out _, out int seatsSewn, out _);
                if (seatsSewn != seats0) VocFail("contention", $"a bench a tailor sews at is {seatsSewn - seats0} seats off for the course (a class takes the bench from the tailor)");
                Pawn student = VocationInmate(map, r, "attendee");
                student.Inmate().passedPrograms.Add(theory);
                student.Inmate().referral = bench;
                Pawn workman = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed("RP_Workman"));
                workman.Name = new NameSingle("Zvocworkman");
                if (workman.workSettings != null)
                    foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefsListForReading) workman.workSettings.SetPriority(work, work == WorkTypeDefOf.Crafting ? 1 : 0);
                var cand = new ProgramSystem.Candidate { program = bench, referred = true };
                cand.pawns.Add(student);
                if (!ProgramSystem.TryStart(map, pm, g, cand))
                {
                    VocFail("contention", "the bench class did not start with a student, a workman and a bench a tailor was sewing at");
                    return;
                }
                if (tailor.CurJobDef == RPLaborDefOf.RP_Sew) VocFail("contention", "the bench class started and the tailor went on sewing at the bench: the attendee has nowhere to sit");
                if (!ProgramSystem.HeldByClass(map, pm, station.parent)) VocFail("contention", "the bench the class sits at is not held by it");
                if (new RegimeWorker_Work().TryGiveJob(tailor, tc, pm)?.def == RPLaborDefOf.RP_Sew) VocFail("contention", "the tailor is given a cycle at a bench the running class holds");
                if (g.laundry.sewn != sewn0) VocFail("contention", "a cycle the class cut short was counted as sewn");
                // The layout check (the showcase plan's) reads a room held by a running class as a room.
                var layout = new List<string>();
                CheckVocationLayout(map, layout);
                if (layout.Count > 0) VocFail("contention", $"the layout check while the bench class runs: {string.Join("; ", layout)}");
                // The attendee, sent to a seat, finds one: the bench (not the wandering round the workshop that a taken seat gives).
                Job attend = ProgramSystem.AttendJob(student, pm.sessions[0], pm);
                if (attend?.def != RPProgramDefOf.RP_AttendClass || attend.targetA.Thing == null) VocFail("contention", $"the attendee is sent to {attend?.def?.defName} (not a bench)");
            });
        }

        /// <summary>
        /// Tailoring at the prison's own bench through real ticks, for export: an inmate who has not passed is given no work at it; one who has sews three uniforms into the surplus for the cloth's price (not into the clean
        /// stock, which is the laundry's); only as many as there is room for are made and paid for, none without an Exports zone to load them from or at the surplus cap, none without money for the cloth; the surplus
        /// refills an empty stock (an inmate's swap, the midnight top-up) before the outside service is paid; the export run sells what is left at the stated price and empties it. (The prison's Exports zone is the check's own cell, the prison's set aside.)
        /// </summary>
        private static void CheckVocationTailorsSewUniforms(Map map)
        {
            Guarded("vocation: tailors sew uniforms", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                CompWorkstation station = pm.Workstations.FirstOrDefault(w => Tailoring.IsBench(w, pm));
                if (station == null)
                {
                    VocFail("sew", "no tailor bench in the workshop");
                    return;
                }
                // (A real tailoring class running at the bench holds it, and its attendee has it reserved: the tailor of this check was then given a walk round the workshop, not a cycle. The real classes are set aside, as the
                // course and bench checks do, and the real inmates sent off the bench.)
                List<ProgramSession> sessions0 = pm.sessions;
                pm.sessions = new List<ProgramSession>();
                r.Add(() => pm.sessions = sessions0);
                (int stock0, int washed0, int sewn0, int surplus0) = (g.laundry.stock, g.laundry.washed, g.laundry.sewn, g.laundry.surplus);
                r.Add(() => (g.laundry.stock, g.laundry.washed, g.laundry.sewn, g.laundry.surplus) = (stock0, washed0, sewn0, surplus0));
                int cycle0 = station.Props.workTicks;
                station.Props.workTicks = 400;
                r.Add(() => station.Props.workTicks = cycle0);
                g.ledger.funds += 1000;
                // (Before the tailor has a job: the hour pass that ends this wait takes the job of an inmate who is forced to an activity, so a wait after it, which ticks when the day's phase is wrong, left a tailor with no job.)
                ClearOfTheHourChange(map, 1500);
                // (After the wait, which can tick across the hour a real class starts in, and puts its attendees to the bench.)
                pm.sessions.Clear();
                ClearTheBenchOfRealInmates(map, station.parent);
                InmateJobDef job = RPVocationDefOf.RP_JobTailoring;
                Pawn untrained = VocationInmate(map, r, "novice");
                untrained.Inmate().job = job;
                if (new RegimeWorker_Work().TryGiveJob(untrained, untrained.Inmate(), pm) != null) VocFail("sew", "an inmate who has not passed the tailoring course is given work at the bench");
                Pawn p = VocationInmate(map, r, "tailor");
                CompInmate c = p.Inmate();
                c.passedPrograms.Add(DefDatabase<ProgramDef>.GetNamed("RP_TailoringTheory"));
                c.passedPrograms.Add(RPVocationDefOf.RP_Tailoring);
                c.job = job;
                // (At work whatever the hour of the day: the regime would send them to bed in a sleeping hour, in the middle of a cycle.)
                c.forcedActivity = RPLaborDefOf.RP_Work;
                MoveTo(p, station.parent.InteractionCell);
                // No Exports zone (the prison's own cells are taken out of it for the check): nothing to sew for. No work, and a cycle that ends (the zone gone while it ran) makes nothing and costs nothing.
                IntVec3 mark = station.parent.InteractionCell;
                VocationExportsAt(map, pm, r, mark);
                pm.zones.Set(mark, null);
                g.laundry.stock = 0;
                g.laundry.surplus = 0;
                if (Sewing(p, c, pm)) VocFail("sew", "the tailors are given work with no Exports zone to sew for");
                int sewnNone = g.laundry.sewn;
                int entriesNone = r.NewLedgerEntries.Count();
                if (Tailoring.Sew(station, map) || g.laundry.sewn != sewnNone || g.laundry.surplus != 0 || g.laundry.stock != 0 || r.NewLedgerEntries.Count() != entriesNone)
                    VocFail("sew", $"a cycle that ended with no Exports zone made {g.laundry.sewn - sewnNone} uniforms (stock {g.laundry.stock}, surplus {g.laundry.surplus}) and {r.NewLedgerEntries.Count() - entriesNone} ledger entries: the cloth is paid for uniforms that go nowhere");
                pm.zones.Set(mark, RPDefOf.RP_Exports);
                // Cycle 1: the stock is empty and the uniforms go to the surplus anyway (the laundry's job is the stock); the cloth is bought.
                int sewnBefore = g.laundry.sewn;
                if (!SewOneCycle(map, pm, p, c, "sew")) return;
                if (g.laundry.sewn - sewnBefore != 3 || g.laundry.surplus != 3) VocFail("sew", $"a cycle at the bench sewed {g.laundry.sewn - sewnBefore} uniforms into a surplus of {g.laundry.surplus} (three, into an empty one)");
                if (g.laundry.stock != 0) VocFail("sew", $"the tailors put uniforms into the clean stock ({g.laundry.stock}), which is the laundry's");
                if (!r.NewLedgerEntries.Any(e => e.category == RPDefOf.RP_Supplies && e.amount == -3 * Tailoring.FabricPerUniform)) VocFail("sew", $"the cloth was not paid for ({3 * Tailoring.FabricPerUniform}): {string.Join(", ", r.NewLedgerEntries.Select(e => e.category.defName + " " + e.amount))}");
                // The cap: two uniforms of room make two, and pay for two; none at the cap.
                p.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                g.laundry.surplus = Tailoring.MaxSurplus - 2;
                int paid = r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Supplies);
                if (!Tailoring.Sew(station, map) || g.laundry.surplus != Tailoring.MaxSurplus) VocFail("sew", $"a cycle with room for two made the surplus {g.laundry.surplus} (the cap, {Tailoring.MaxSurplus}, expected)");
                if (r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Supplies) != paid + 1 || !r.NewLedgerEntries.Any(e => e.category == RPDefOf.RP_Supplies && e.amount == -2 * Tailoring.FabricPerUniform))
                    VocFail("sew", $"a cycle with room for two was not paid for two uniforms' cloth: {string.Join(", ", r.NewLedgerEntries.Select(e => e.category.defName + " " + e.amount))}");
                int atCap = g.laundry.sewn;
                int entriesCap = r.NewLedgerEntries.Count();
                if (Tailoring.Sew(station, map) || g.laundry.surplus != Tailoring.MaxSurplus || g.laundry.sewn != atCap || r.NewLedgerEntries.Count() != entriesCap) VocFail("sew", "a cycle that ended at the surplus cap made uniforms or was paid for");
                if (Sewing(p, c, pm)) VocFail("sew", "the tailors keep sewing at the surplus cap");
                // No work without money for the cloth.
                g.laundry.surplus = 0;
                int funds = g.ledger.funds;
                g.ledger.funds = 2;
                if (Sewing(p, c, pm)) VocFail("sew", "the tailors start a cycle the prison cannot buy the cloth for");
                g.ledger.funds = funds;
                if (!Sewing(p, c, pm)) VocFail("sew", "the tailors are given no work with room for uniforms and money in hand");
                p.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                // The surplus refills an empty stock before anyone pays the outside service: an inmate's swap, and the midnight top-up.
                CompInmate swapper = VocationInmate(map, r, "swapper").Inmate();
                Need clean = swapper.Pawn.needs?.TryGetNeed(RPDefOf.RP_CleanClothes);
                if (clean == null) VocFail("sew", "the made-up inmate has no clean-clothes need");
                else
                {
                    clean.CurLevel = 0.1f;
                    g.laundry.stock = 0;
                    g.laundry.surplus = 2;
                    LaundrySystem.Hourly(swapper.Pawn, swapper);
                    if (clean.CurLevel < 0.99f || g.laundry.surplus != 1 || g.laundry.stock != 0) VocFail("sew", $"an inmate with a filthy uniform and an empty stock: clean {clean.CurLevel:F2}, surplus {g.laundry.surplus} (one used), stock {g.laundry.stock}");
                    clean.CurLevel = 0.1f;
                    g.laundry.surplus = 0;
                    LaundrySystem.Hourly(swapper.Pawn, swapper);
                    if (clean.CurLevel > 0.11f) VocFail("sew", "an inmate got a clean uniform from an empty stock and no surplus");
                }
                g.laundry.stock = 0;
                g.laundry.surplus = 5;
                int laundryEntries = r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Laundry);
                g.laundry.DailyTopUp(g, 3);
                if (g.laundry.stock != 3 || g.laundry.surplus != 2 || r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Laundry) != laundryEntries)
                    VocFail("sew", $"a midnight top-up of three from a surplus of five left stock {g.laundry.stock} (3), surplus {g.laundry.surplus} (2), and {r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Laundry) - laundryEntries} payments to the laundry service (none)");
                g.laundry.stock = 0;
                g.laundry.surplus = 5;
                g.laundry.DailyTopUp(g, 8);
                if (g.laundry.stock != 8 || g.laundry.surplus != 0 || !r.NewLedgerEntries.Any(e => e.category == RPDefOf.RP_Laundry && e.amount == -3 * LaundrySystem.OutsourcedPrice))
                    VocFail("sew", $"a midnight top-up of eight from a surplus of five left stock {g.laundry.stock} (8), surplus {g.laundry.surplus} (0), and paid for {r.NewLedgerEntries.Where(e => e.category == RPDefOf.RP_Laundry).Sum(e => -e.amount)} at the service (the other three: {3 * LaundrySystem.OutsourcedPrice})");
                // The export run: the surplus sold at the uniforms' price and gone.
                g.laundry.surplus = 3;
                int itemsBefore = g.stats.itemsExported;
                LaborUtility.SellExports(map, g);
                if (g.laundry.surplus != 0) VocFail("sew", $"the export run left {g.laundry.surplus} surplus uniforms");
                int expected = 3 * Tailoring.UniformValue;
                if (!r.NewLedgerEntries.Any(e => e.category == RPDefOf.RP_ExportSales && e.amount == expected)) VocFail("sew", $"the export run did not sell three uniforms for {expected}: {string.Join(", ", r.NewLedgerEntries.Select(e => e.category.defName + " " + e.amount))}");
                if (g.stats.itemsExported - itemsBefore != 3) VocFail("sew", $"the export run counted {g.stats.itemsExported - itemsBefore} items (three uniforms)");
            });
        }

        /// <summary>The regime gives this inmate a cycle at the bench (not nothing, and not a stroll round the workshop).</summary>
        private static bool Sewing(Pawn p, CompInmate c, PrisonMap pm) => new RegimeWorker_Work().TryGiveJob(p, c, pm)?.def == RPLaborDefOf.RP_Sew;

        /// <summary>The job the worker gives the inmate (a cycle at the bench), run through real ticks to its end.</summary>
        private static bool SewOneCycle(Map map, PrisonMap pm, Pawn p, CompInmate c, string check)
        {
            PrisonGame g = PrisonGame.Instance;
            p.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
            Job job = new RegimeWorker_Work().TryGiveJob(p, c, pm);
            if (job?.def != RPLaborDefOf.RP_Sew)
            {
                CompWorkstation bench = pm.Workstations.FirstOrDefault(w => w.Props.uniforms > 0);
                string why = bench == null ? "no bench" : $"wanted {Tailoring.Wanted(pm)} (room {Tailoring.Room(pm)}), bench {Tailoring.IsBench(bench, pm)} (usable {bench.Usable}, zone {pm.zones.ZoneAt(bench.parent.Position)?.defName}), held by a class {ProgramSystem.HeldByClass(map, pm, bench.parent)}, "
                    + $"can afford {g.ledger.CanAfford(Tailoring.Cost(bench))}, can reserve and reach {p.CanReserveAndReach(bench.parent, PathEndMode.InteractionCell, Danger.Some)} (reserved by {map.reservationManager.FirstRespectedReserver(bench.parent, p)?.LabelShort ?? "nobody"}), tailor at {p.Position} job {c.job?.defName} forced {c.forcedActivity?.defName}";
                VocFail(check, $"the tailor is given {job?.def?.defName ?? "no work"} (stock {g.laundry.stock}, surplus {g.laundry.surplus}, funds {g.ledger.funds}; {why})");
                return false;
            }
            int sewn = g.laundry.sewn;
            p.jobs.StartJob(job, JobCondition.InterruptForced);
            ShuttleTicks(() => g.laundry.sewn > sewn, 1800);
            if (g.laundry.sewn == sewn)
            {
                VocFail(check, $"a cycle at the bench sewed nothing in 1800 ticks (job {p.CurJobDef?.defName} at {p.Position}, bench {job.targetA.Thing?.Position})");
                return false;
            }
            return true;
        }

        // ---- kitchen safety -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Kitchen safety: held in the canteen (at its dining chairs: every prison has one from the start, the classroom is behind the Reform bureaucracy), not while an inmate's regime has them eating there, by the counsellor
        /// when there is one (else the cook, offered the class through the cook's own work type); pays no grant; the kitchen job is shut to an inmate who has not passed it (the hourly assignment takes it off one who has it,
        /// locked by the warden or not) and open after the two sessions; the kitchen's stand-down after a scare does not close the course.
        /// </summary>
        private static void CheckVocationKitchenSafety(Map map)
        {
            Guarded("vocation: kitchen safety", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                ProgramDef safety = RPVocationDefOf.RP_KitchenSafety;
                InmateJobDef kitchen = DefDatabase<InmateJobDef>.GetNamed("RP_JobKitchen");
                List<ProgramSession> sessions0 = pm.sessions;
                pm.sessions = new List<ProgramSession>();
                r.Add(() => pm.sessions = sessions0);
                VocationKeepJobs(map, r);
                if (kitchen.requiredProgram != safety) VocFail("kitchen", $"the kitchen job asks for {kitchen.requiredProgram?.defName ?? "no program"}");
                // The room: the canteen (not the classroom a new prison does not have), at seats the canteen has.
                if (safety.Zone != RPDefOf.RP_Canteen) VocFail("kitchen", $"kitchen safety is held in the {safety.Zone.defName}, not the canteen: a new prison has no classroom until it pays for the Reform bureaucracy");
                if (!safety.Seats.Any(seat => map.listerBuildings.AllBuildingsColonistOfDef(seat).Any(b => pm.zones.ZoneAt(b.Position) == RPDefOf.RP_Canteen)))
                    VocFail("kitchen", $"the prison's canteen has none of the seats kitchen safety sits at ({safety.SeatLabel})");
                // Nothing it needs is behind a bureaucracy project: the room, the seats, and a teacher (the cook, or the counsellor) are there from the first day.
                foreach (Def needed in new Def[] { safety.Zone }.Concat(safety.Seats).Cast<Def>())
                    if (Unlocks.GateOf(needed) is { } gate) VocFail("kitchen", $"kitchen safety needs {needed.defName}, which is behind the {gate.defName} project: a new prison cannot hold the course the kitchen job asks for");
                if (safety.TeacherRoles.All(role => Unlocks.GateOf(role) != null)) VocFail("kitchen", "every teacher of kitchen safety is behind a bureaucracy project");
                // Not at a meal: an inmate whose regime has them eating gives no room.
                Pawn eater = VocationInmate(map, r, "eater");
                eater.Inmate().activeFlag = true;
                eater.Inmate().forcedActivity = DefDatabase<RegimeActivityDef>.GetNamed("RP_Eat");
                if (ProgramSystem.TryFindVenueNow(map, pm, g, safety, out _, out _)) VocFail("kitchen", "kitchen safety found a room in the canteen while an inmate was eating there");
                if (ProgramSystem.Blocker(map, pm, safety) == ProgramBlock.None) VocFail("kitchen", "the Programs screen sees nothing in the way of kitchen safety while an inmate is eating in the canteen");
                eater.Inmate().forcedActivity = null;
                // Pays no grant, and is taught by the counsellor first.
                if (safety.grantOnPass != 0) VocFail("kitchen", $"kitchen safety pays a grant of {safety.grantOnPass} for every graduate (a cheap way to make money from the whole prison)");
                if (safety.TeacherRoles.Count == 0 || safety.TeacherRoles[0] != RPProgramDefOf.RP_Counsellor) VocFail("kitchen", $"the teacher kitchen safety prefers is {safety.TeacherRoles.FirstOrDefault()?.defName} (the counsellor, so the only cook stays at the stoves)");
                Pawn p = VocationInmate(map, r, "scullion");
                CompInmate c = p.Inmate();
                if (!safety.Eligible(p, c)) VocFail("kitchen", "an inmate who could work in the kitchen is not offered the kitchen safety course");
                if (LaborUtility.Eligible(c, kitchen)) VocFail("kitchen", "an inmate who has not passed kitchen safety may hold the kitchen job");
                // The day of the stand-down after a scare is no day to close the course.
                int standDown0 = g.story.kitchenStandDownUntilTick;
                r.Add(() => g.story.kitchenStandDownUntilTick = standDown0);
                g.story.kitchenStandDownUntilTick = Find.TickManager.TicksGame + 1000;
                if (!safety.Eligible(p, c)) VocFail("kitchen", "kitchen safety is closed to everyone during the kitchen's stand-down after a food scare");
                g.story.kitchenStandDownUntilTick = standDown0;
                c.job = kitchen;
                if (new RegimeWorker_Work().TryGiveJob(p, c, pm) != null) VocFail("kitchen", "an inmate who has not passed kitchen safety is given kitchen work");
                LaborUtility.AssignJobs(map);
                if (c.job == kitchen) VocFail("kitchen", "the hourly job assignment left the kitchen job with an inmate who has not passed kitchen safety");
                // A job the warden gave by hand in an old save, to one who has not passed: dropped, not kept as a dead job that holds a slot; one who has passed keeps theirs.
                Pawn locked = VocationInmate(map, r, "locked");
                (locked.Inmate().job, locked.Inmate().jobLocked) = (kitchen, true);
                Pawn trainedLocked = VocationInmate(map, r, "trained");
                trainedLocked.Inmate().passedPrograms.Add(safety);
                (trainedLocked.Inmate().job, trainedLocked.Inmate().jobLocked) = (kitchen, true);
                LaborUtility.AssignJobs(map);
                if (locked.Inmate().job == kitchen || locked.Inmate().jobLocked) VocFail("kitchen", $"a kitchen job locked on an inmate who has not passed kitchen safety stays (job {locked.Inmate().job?.defName}, locked {locked.Inmate().jobLocked}): a dead job that holds a kitchen slot");
                if (trainedLocked.Inmate().job != kitchen) VocFail("kitchen", "a locked kitchen job was taken from an inmate who has passed kitchen safety");
                int grants = r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Grants);
                if (!VocationRunSessions(map, pm, safety, p, 1, "kitchen")) return;
                if (c.passedPrograms.Contains(safety)) VocFail("kitchen", "one session passed a two-session course");
                if (!VocationRunSessions(map, pm, safety, p, 1, "kitchen")) return;
                if (!c.passedPrograms.Contains(safety)) VocFail("kitchen", "two sessions (with the exam certain) did not pass kitchen safety");
                if (r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Grants) != grants) VocFail("kitchen", "passing kitchen safety brought a grant");
                if (!LaborUtility.Eligible(c, kitchen)) VocFail("kitchen", "the kitchen job is closed to an inmate who passed kitchen safety");
                // The course a job with a free slot waits for goes before the fuller classes (who are the same inmates, taken by the biggest class first, hour after hour): with no one on the kitchen job, and
                // a stove to work, kitchen safety goes before a five-strong class of another program; with every slot held it does not.
                foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned)
                    if (x.Inmate() is { job: { } held } crew && held == kitchen) crew.job = null;
                int slots = kitchen.Worker.Capacity(map);
                if (slots < 1) VocFail("kitchen", "the prison's kitchen has no stove for an inmate to work at");
                else
                {
                    var safetyClass = new ProgramSystem.Candidate { program = safety, jobWaits = ProgramSystem.JobWaits(map, safety) };
                    safetyClass.pawns.Add(p);
                    var education = new ProgramSystem.Candidate { program = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation"), jobWaits = ProgramSystem.JobWaits(map, DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation")) };
                    for (int i = 0; i < 5; i++) education.pawns.Add(p);
                    if (!safetyClass.jobWaits) VocFail("kitchen", $"the kitchen has {slots} free slots and no one on them, and kitchen safety is not the course a job waits for");
                    if (ProgramSystem.ComparePriority(g, safetyClass, education) >= 0) VocFail("kitchen", "a five-strong class of another program goes before the kitchen safety class the kitchen job waits for (it would never start)");
                    var crew = new List<Pawn>();
                    for (int i = 0; i < slots; i++)
                    {
                        Pawn hand = VocationInmate(map, r, "crew" + i);
                        hand.Inmate().passedPrograms.Add(safety);
                        hand.Inmate().job = kitchen;
                        crew.Add(hand);
                    }
                    if (ProgramSystem.JobWaits(map, safety)) VocFail("kitchen", "kitchen safety still goes first with every kitchen slot held");
                }
                // The teachers: with a counsellor and a cook both free, the counsellor is the one chosen; a class with nobody on it is offered to a cook whose cooking work is on, and not to one who has switched it off.
                WorkTypeDef cooking = DefDatabase<WorkTypeDef>.GetNamed("Cooking");
                Pawn cook = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed("RP_Cook"));
                cook.Name = new NameSingle("Zvoccook");
                if (cook.workSettings != null)
                    foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefsListForReading) cook.workSettings.SetPriority(work, work == cooking ? 1 : 0);
                Pawn counsellor = ThrowawayStaff(map, r, RPProgramDefOf.RP_Counsellor);
                counsellor.Name = new NameSingle("Zvoccounsellor");
                if (counsellor.workSettings != null)
                    foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                        counsellor.workSettings.SetPriority(work, ProgramSystem.IsTeachWorkType(work) && RPProgramDefOf.RP_Counsellor.workTypes.Contains(work) ? 1 : 0);
                Pawn teacher = ProgramSystem.FreeTeacher(map, pm, safety, out _);
                if (teacher?.GetComp<CompStaff>()?.role != RPProgramDefOf.RP_Counsellor) VocFail("kitchen", $"with a counsellor and a cook both free, kitchen safety is taught by {teacher?.GetComp<CompStaff>()?.role?.defName ?? "nobody"}");
                if (!VocationAnchor(map, pm, safety, out IntVec3 anchor))
                {
                    VocFail("kitchen", "no seat for kitchen safety in the canteen");
                    return;
                }
                int now = Find.TickManager.TicksGame;
                var live = new ProgramSession { program = safety, startTick = now, endTick = now + 2000, anchor = anchor, attendees = new List<Pawn> { p } };
                pm.sessions.Add(live);
                if (ProgramSystem.SessionToTeach(cook, pm) != live) VocFail("kitchen", "a cook with the cooking work on is not offered the kitchen safety class to teach");
                if (cook.workSettings != null)
                {
                    cook.workSettings.SetPriority(cooking, 0);
                    if (ProgramSystem.SessionToTeach(cook, pm) != null) VocFail("kitchen", "a cook with the cooking work off is offered the kitchen safety class");
                }
                if (!safety.TeacherRoles.Contains(DefDatabase<StaffRoleDef>.GetNamed("RP_Cook"))) VocFail("kitchen", "the cook is not a teacher of kitchen safety");
            });
        }

        /// <summary>The food scare's chance: each trained kitchen hand takes 15% off, up to four; a hand who has the job without the course counts for nothing. (The real crew's kitchen jobs are taken aside first, so the count starts at nothing.)</summary>
        private static void CheckVocationFoodScareOdds(Map map)
        {
            Guarded("vocation: the food scare's odds", map, r =>
            {
                IncidentDef scare = DefDatabase<IncidentDef>.GetNamed("RP_FoodScare");
                InmateJobDef kitchen = DefDatabase<InmateJobDef>.GetNamed("RP_JobKitchen");
                ProgramDef safety = RPVocationDefOf.RP_KitchenSafety;
                VocationKeepJobs(map, r);
                // A prison that already has a trained crew of four (the real crew's kitchen jobs are then taken off, so the count starts from nothing whoever they are).
                for (int i = 0; i < 4; i++)
                {
                    Pawn veteran = VocationInmate(map, r, "veteran" + i);
                    veteran.Inmate().passedPrograms.Add(safety);
                    veteran.Inmate().job = kitchen;
                }
                if (FoodScare.TrainedCrew(map) < 4) VocFail("scare", "four trained hands with the kitchen job do not count as a crew");
                foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned)
                    if (x.Inmate() is { job: { } held } crew && held.zone == RPLaborDefOf.RP_Kitchen) crew.job = null;
                int trained0 = FoodScare.TrainedCrew(map);
                if (trained0 != 0) VocFail("scare", $"{trained0} trained hands left in the kitchen after the real crew was taken off it");
                float odds0 = scare.Worker.ChanceFactorNow(map);
                // Hands with the job and no course: no difference.
                var hands = new List<Pawn>();
                for (int i = 0; i < 6; i++)
                {
                    Pawn hand = VocationInmate(map, r, "hand" + i);
                    hand.Inmate().job = kitchen;
                    hands.Add(hand);
                }
                if (FoodScare.TrainedCrew(map) != trained0 || !Mathf.Approximately(scare.Worker.ChanceFactorNow(map), odds0))
                    VocFail("scare", $"six kitchen hands without the course changed the odds ({odds0:F3} to {scare.Worker.ChanceFactorNow(map):F3}, trained crew {trained0} to {FoodScare.TrainedCrew(map)})");
                float Factor(int trained) => 1f - 0.15f * Mathf.Min(trained, 4);
                for (int n = 1; n <= 6; n++)
                {
                    hands[n - 1].Inmate().passedPrograms.Add(safety);
                    int crew = FoodScare.TrainedCrew(map);
                    if (crew != trained0 + n) VocFail("scare", $"{n} trained hands counted as {crew - trained0}");
                    float expected = odds0 * Factor(crew) / Factor(trained0);
                    float odds = scare.Worker.ChanceFactorNow(map);
                    if (!Mathf.Approximately(odds, expected)) VocFail("scare", $"with {crew} trained hands the scare's odds are {odds:F3} (expected {expected:F3}, from {odds0:F3} with {trained0})");
                }
                if (scare.Worker.ChanceFactorNow(map) >= odds0) VocFail("scare", "six trained hands left the food scare's odds as they were");
                if (!Mathf.Approximately(FoodScare.SafetyFactor(map), Factor(FoodScare.TrainedCrew(map)))) VocFail("scare", "the safety factor is not 15% a trained hand, up to four");
            });
        }

        // ---- a trade, after release --------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A certified trade on parole: the reoffend chance is a tenth lower than the same inmate's without it (the classroom part alone is no trade); the "job in place" odds are one half for a certified parolee with no
        /// job waiting (none without the trade, certain with a job waiting, which the trade then does not claim), the register keeps the trade that found the job and its news says so, and a trade
        /// lowers nothing for one who walks out unsupervised (no terms).
        /// </summary>
        private static void CheckVocationTradeOnParole(Map map)
        {
            Guarded("vocation: a trade on parole", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                ProgramDef theory = DefDatabase<ProgramDef>.GetNamed("RP_TailoringTheory"), bench = RPVocationDefOf.RP_Tailoring;
                Pawn p = VocationInmate(map, r, "parolee");
                CompInmate c = p.Inmate();
                c.reputations.Clear();
                float plain = ParoleSystem.ReoffendChance(c);
                c.passedPrograms.Add(theory);
                float partway = ParoleSystem.ReoffendChance(c);
                c.passedPrograms.Add(bench);
                float certified = ParoleSystem.ReoffendChance(c);
                if (plain < 0.3f) VocFail("parole", $"the plain reoffend chance is {plain:F2}: too low to see a cut");
                if (!Mathf.Approximately(partway, plain)) VocFail("parole", $"the classroom part alone moved the reoffend chance from {plain:F2} to {partway:F2}");
                if (!Mathf.Approximately(plain - certified, 0.1f)) VocFail("parole", $"a certified trade took {plain - certified:F3} off the reoffend chance ({plain:F3} to {certified:F3}), a tenth was stated");
                // The job in place, over many paroles: the odds, and what the register keeps.
                float OddsOver(CompInmate who, out int tradeJobs, out FormerInmate firstTradeJob)
                {
                    int secured = 0;
                    tradeJobs = 0;
                    firstTradeJob = null;
                    Rand.PushState(20261021);
                    try
                    {
                        for (int i = 0; i < 400; i++)
                        {
                            var parolee = new FormerInmate { name = "Zvocformer" };
                            ParoleLife.Impose(parolee, who);
                            if (parolee.jobSecured) secured++;
                            if (parolee.tradeJob == null) continue;
                            tradeJobs++;
                            firstTradeJob ??= parolee;
                        }
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    return secured / 400f;
                }
                float with = OddsOver(c, out int tradeJobs, out FormerInmate found);
                if (with < 0.42f || with > 0.58f) VocFail("parole", $"a certified parolee with no job waiting had a job in place {with:P0} of 400 times (one half stated)");
                if (tradeJobs == 0 || tradeJobs != Mathf.RoundToInt(with * 400f)) VocFail("parole", $"the register kept the trade for {tradeJobs} of {Mathf.RoundToInt(with * 400f)} jobs it found");
                CompInmate plainC = VocationInmate(map, r, "plainparolee").Inmate();
                if (OddsOver(plainC, out int plainTradeJobs, out _) != 0f || plainTradeJobs != 0) VocFail("parole", "an inmate with no trade and no job waiting had a job in place on parole");
                c.jobWaiting = true;
                if (OddsOver(c, out int waitingTradeJobs, out _) != 1f || waitingTradeJobs != 0) VocFail("parole", $"with a job waiting the job was not always in place, or the trade claimed it ({waitingTradeJobs})");
                if (!Mathf.Approximately(ParoleLife.JobInPlaceChance(c), 1f) || !Mathf.Approximately(ParoleLife.JobInPlaceChance(p.Inmate()), 1f)) VocFail("parole", "the odds of a job in place with one waiting are not certain");
                c.jobWaiting = false;
                if (!Mathf.Approximately(ParoleLife.JobInPlaceChance(c), 0.5f) || !Mathf.Approximately(ParoleLife.JobInPlaceChance(plainC), 0f)) VocFail("parole", "the odds of a job in place are not 50% with a trade and none without");
                // The term, the letter and the news name the trade; the news comes (as the register would send it) as the trade's.
                if (found?.tradeJob == null)
                {
                    VocFail("parole", "no parolee in 400 had a job from the trade");
                    return;
                }
                FormerInmate news = new FormerInmate { name = "Zvocformer", tradeJob = found.tradeJob, jobSecured = true, paroleEndTick = Find.TickManager.TicksGame + 5 * GenDate.TicksPerDay, paroled = true };
                if (!ParoleLife.JobTerm(news).Contains(found.tradeJob.label)) VocFail("parole", $"the parole term does not name the trade: {ParoleLife.JobTerm(news)}");
                if (!ParoleLife.LetterLine(news).Contains(found.tradeJob.label)) VocFail("parole", $"the parolee's letter does not name the trade: {ParoleLife.LetterLine(news)}");
                // The register keeps the program, not its label: the label is read in the language of the day.
                string label0 = found.tradeJob.label;
                r.Add(() => found.tradeJob.label = label0);
                found.tradeJob.label = "zvoclabel";
                if (!ParoleLife.JobTerm(news).Contains("zvoclabel") || !ParoleLife.LetterLine(news).Contains("zvoclabel")) VocFail("parole", "the parole term and the letter keep the trade's label as it was when the job was found, not as it reads now");
                found.tradeJob.label = label0;
                FormerInmates.Deliver(g, map, news);
                if (news.news != "RP_FormerNews_Trade") VocFail("parole", $"the news of a parolee whose trade found the job is {news.news}");
                FormerInmate waiting = new FormerInmate { name = "Zvocformer", jobWaiting = true, jobSecured = true };
                FormerInmates.Deliver(g, map, waiting);
                if (waiting.news != "RP_FormerNews_Job") VocFail("parole", $"the news of a parolee with a job that was waiting is {waiting.news}");
            });
        }

        // ---- the layouts -----------------------------------------------------------------------------------------------------------------------

        /// <summary>Both prisons have what the job uses: a tailor bench in the workshop, and so a room for the course (the showcase plan, for the penitentiary; a room held by a session running now is still a room).</summary>
        private static void CheckVocationLayout(Map map, List<string> problems)
        {
            PrisonMap pm = PrisonMap.Of(map);
            int benches = pm.Workstations.Count(w => Tailoring.IsBench(w, pm));
            if (benches == 0) problems.Add("vocation: the workshop has no tailor bench");
            bool found = ProgramSystem.TryFindVenue(map, pm, RPVocationDefOf.RP_Tailoring, out _, out int seats, out bool busy);
            if (!found && !busy) problems.Add("vocation: the tailoring course has no room");
            RPLog.Message($"[RimPrison:test] vocation: {benches} tailor benches in the workshop, the course seats {seats}");
        }

        // ---- the round trip through a save --------------------------------------------------------------------------------------------------------

        private static bool vocationSeeded;
        private static (int sewn, int surplus) vocationLaundry0;

        /// <summary>The saveload plan: uniforms sewn and waiting for export, and a register entry that keeps the trade that found a job. The snapshot compares them; after the reload they go.</summary>
        private static void SeedVocationForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            vocationLaundry0 = (g.laundry.sewn, g.laundry.surplus);
            (g.laundry.sewn, g.laundry.surplus) = (g.laundry.sewn + 7, g.laundry.surplus + 5);
            g.story.formerInmates.Add(new FormerInmate { name = "Zvocseed", paroled = true, jobSecured = true, tradeJob = RPVocationDefOf.RP_Tailoring, newsTick = Find.TickManager.TicksGame + 99999, paroleEndTick = Find.TickManager.TicksGame + 99999 });
            vocationSeeded = true;
            RPLog.Message("[RimPrison:test] saveload vocation: seven more uniforms sewn, five waiting for export, a parolee whose trade found the job");
        }

        private static void CheckVocationAfterReload(Map map)
        {
            if (!vocationSeeded) return;
            vocationSeeded = false;
            PrisonGame g = PrisonGame.Instance;
            bool laundry = g.laundry.sewn == vocationLaundry0.sewn + 7 && g.laundry.surplus == vocationLaundry0.surplus + 5;
            FormerInmate former = g.story.formerInmates.FirstOrDefault(e => e.name == "Zvocseed");
            bool formerOk = former is { jobSecured: true } && former.tradeJob == RPVocationDefOf.RP_Tailoring;
            RPLog.Message($"[RimPrison:test] saveload vocation: uniforms {(laundry ? "intact" : "changed")} ({g.laundry.sewn}/{g.laundry.surplus}), the trade {(formerOk ? "kept" : "lost")}");
            if (!laundry) reloadProblems.Add("the uniforms sewn and the surplus waiting for export did not come back");
            if (!formerOk) reloadProblems.Add("the trade that found a parolee's job did not come back");
            (g.laundry.sewn, g.laundry.surplus) = vocationLaundry0;
            if (former != null) g.story.formerInmates.Remove(former);
        }

        /// <summary>The snapshot's part for vocational training.</summary>
        private static string VocationDescribe()
        {
            PrisonGame g = PrisonGame.Instance;
            return $"{g.laundry.sewn}/{g.laundry.surplus}:{string.Join(",", g.story.formerInmates.Select(e => e.name + "=" + e.tradeJob?.defName))}";
        }
    }
}
