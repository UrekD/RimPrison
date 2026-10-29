using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Class bookings (Programs/ClassBookings.cs, the booked pass of <see cref="ProgramSystem.HourlyTick"/>, the Programs tab): two courses that want the one classroom and the one counsellor, the smaller one booked, gets the hour and
    /// the fuller one the next free one; a session through to its credit, charged and credited once and not again when the pass is repeated; every reason a booked class does not run (the teacher on leave, the room kept for other
    /// levels, the room gone or held, no one with the slot, no one eligible, too few, no funds) is kept as that reason, charges and credits nothing, and leaves the automatic scheduler to fill the room as it would
    /// have, and a stopped program's booking is skipped without a miss; three misses in a row warn and five are kept, and nothing is cancelled; a program has one booking for an hour. Self-contained, on throwaway inmates and the live classroom, with the other rooms and teachers of the
    /// program held by classes of the check's own (the prison's sessions, bookings, programs switched on, timetable and kept rooms set aside and put back by the rollback). The round trip of the saved booking, with a class under way, is in
    /// the saveload plan (<see cref="SeedClassBookingForReload"/>). The real teacher walking to the room and teaching is <see cref="CheckParallelPrograms"/>'s: here the session is taught by crediting its ticks, as in
    /// the vocation checks.
    /// </summary>
    public static partial class AutoTest
    {
        private static void BookFail(string check, string what) => problems0.Add($"class bookings ({check}): {what}");

        private static void CheckClassBooking(Map map)
        {
            CheckClassBookingTranslations(map);
            CheckClassBookingOnePerHour(map);
            CheckClassBookingContention(map);
            CheckClassBookingOneTeacher(map);
            CheckClassBookingReasons(map);
            CheckClassBookingStreak(map);
        }

        /// <summary>The prison as a booking check needs it, and what to ask of it.</summary>
        private sealed class BookingScene
        {
            public Map map;
            public PrisonMap pm;
            public PrisonGame g;
            public ProgramDef big, small, filler;
            public SecurityLevelDef level;
            public Pawn[] schoolers, addicts;
            public Pawn teacher;
            public List<Pawn> otherTeachers = new List<Pawn>();
            public List<IntVec3> otherRooms = new List<IntVec3>();
            public IntVec3 room;
            public int hour;
            public List<ProgramDef> disabled;
            public readonly List<ProgramDef> keep = new List<ProgramDef>();
            public Rollback r;
            public RegimeSchedule schedule;

            public IEnumerable<Pawn> Inmates => schoolers.Concat(addicts);

            /// <summary>Everything the passes changed made as it was at the start: no sessions but the check's holding ones, no bookings, no waits, the funds, who has passed or sat what.</summary>
            public void Reset()
            {
                pm.sessions.Clear();
                pm.bookings = new ClassBookings();
                g.programs.waitingHours.Clear();
                g.programs.disabled = new List<ProgramDef>(disabled);
                g.ledger.funds = 10000;
                foreach (Pawn p in Inmates)
                {
                    CompInmate c = p.Inmate();
                    c.passedPrograms.Clear();
                    c.programProgress.Clear();
                    c.tension = 0f;
                }
                foreach (Pawn t in otherTeachers.Concat(new[] { teacher }).Where(t => t != null)) t.GetComp<CompStaff>().leaveUntilTick = -1;
                for (int h = 0; h < 24; h++) schedule.Set(h, RPLaborDefOf.RP_Work);
                // The program's other rooms and teachers are held by classes of the check's own: one room and one teacher are left, and a class that wants them waits.
                foreach (IntVec3 anchor in otherRooms) Hold(anchor, otherTeachers.Count > 0 ? otherTeachers[0] : null);
                for (int i = Math.Min(1, otherRooms.Count); i < otherTeachers.Count; i++) Hold(IntVec3.Invalid, otherTeachers[i]);
            }

            /// <summary>A class of the check's own in a room (or nowhere) with a teacher (or none): it is nobody's to credit, and it holds the room and the teacher for as long as the check runs.</summary>
            public void Hold(IntVec3 anchor, Pawn teacherOf)
            {
                int now = Find.TickManager.TicksGame;
                pm.sessions.Add(new ProgramSession { program = filler, startTick = now, endTick = now + 100000, teacher = teacherOf, anchor = anchor });
            }

            public int Spends() => r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Programs && e.amount < 0);

            public string Seen() =>
                $"sessions [{string.Join(", ", pm.sessions.Where(x => x.cost > 0 || x.program != filler).Select(x => $"{x.program.defName} with {x.attendees.Count}"))}], bookings [{string.Join(", ", pm.bookings.list.Select(b => $"{b.program.defName} {ClassBookings.HoursLabel(b.hours)} streak {b.missStreak} last {b.LastMiss?.reason}"))}], hour {GenLocalDate.HourOfDay(map)}, funds {g.ledger.funds}, teacher {teacher.LabelShort} (can teach {ProgramSystem.CanTeach(teacher, RPProgramDefOf.RP_Counsellor)})";
        }

        /// <summary>A made-up inmate who would take a class: calm, in the work slot of the level the check has timetabled, with no job, not addicted unless the course is for the addicted, and reading below the education course's bar.</summary>
        private static Pawn BookingInmate(Map map, Rollback r, SecurityLevelDef level, string name, bool drug)
        {
            Pawn p = Throwaway(map, r, level);
            p.Name = new NameSingle("Zbook" + name);
            CompInmate c = p.Inmate();
            bool active0 = c.activeFlag;
            r.Add(() => c.activeFlag = active0);
            c.activeFlag = true;
            (c.tension, c.suppressed, c.referral, c.job, c.jobWaiting, c.fear, c.forcedActivity) = (0f, 0f, null, null, false, 0f, null);
            CrimeDef crime = DefDatabase<CrimeDef>.AllDefsListForReading.FirstOrDefault(d => d.drugRelated == drug);
            if (crime != null) c.crime = crime;
            foreach (Hediff h in p.health.hediffSet.hediffs.Where(h => h is Hediff_Addiction).ToList()) p.health.RemoveHediff(h);
            if (p.skills?.GetSkill(SkillDefOf.Intellectual) is { } skill) skill.Level = 2;
            if (p.needs?.rest != null) p.needs.rest.CurLevel = 1f;
            if (p.needs?.food != null) p.needs.food.CurLevel = 1f;
            return p;
        }

        /// <summary>
        /// The scene: a level nobody lives in with work in every hour, the program's other rooms and teachers held, three inmates who would take foundation education and two who would take it and substance abuse treatment, the real
        /// inmates set out of the way, no room kept for any level. Null (told) when the prison cannot give it.
        /// </summary>
        private static BookingScene BookingSetUp(Map map, Rollback r)
        {
            var s = new BookingScene { map = map, pm = PrisonMap.Of(map), g = PrisonGame.Instance, r = r, hour = GenLocalDate.HourOfDay(map) };
            s.big = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation");
            s.small = DefDatabase<ProgramDef>.GetNamed("RP_SubstanceAbuse");
            s.filler = DefDatabase<ProgramDef>.GetNamed("RP_GeneralEducation");
            s.level = RegimeEmptyLevel(map);
            if (s.level == null)
            {
                RPLog.Message("[RimPrison:test] class bookings: every ordinary level has an inmate in it, the bookings are not checked");
                return null;
            }
            if (!DefDatabase<CrimeDef>.AllDefsListForReading.Any(d => d.drugRelated) || !DefDatabase<CrimeDef>.AllDefsListForReading.Any(d => !d.drugRelated))
            {
                RPLog.Message("[RimPrison:test] class bookings: no drug crime or no other crime to give the inmates, the bookings are not checked");
                return null;
            }
            PrisonMap pm = s.pm;
            PrisonGame g = s.g;
            List<ProgramSession> sessions0 = pm.sessions;
            ClassBookings bookings0 = pm.bookings;
            List<ProgramDef> disabled0 = g.programs.disabled;
            Dictionary<ProgramDef, int> waits0 = g.programs.waitingHours;
            pm.sessions = new List<ProgramSession>();
            r.Add(() => (pm.sessions, pm.bookings, g.programs.disabled, g.programs.waitingHours) = (sessions0, bookings0, disabled0, waits0));
            // (The checks start a dozen classes of the one teacher in this frame, the sessions cleared between: each start would send them to a new job, and vanilla logs "started 10 jobs in one tick" for it. The class is taught by crediting its ticks here.)
            ProgramSystem.CallTeacherOffForTests = true;
            r.Add(() => ProgramSystem.CallTeacherOffForTests = false);
            g.programs.waitingHours = new Dictionary<ProgramDef, int>();
            s.disabled = DefDatabase<ProgramDef>.AllDefsListForReading.Where(x => x != s.big && x != s.small).ToList();
            s.schedule = RegimeSetAside(g, s.level, r);
            RegimeOrdersSetAside(g, r);
            // No room is kept for any level (put back); the real inmates are out of the way (put back).
            var kept0 = new List<(int patchAt, SecurityLevelDef[] levels)>();
            List<ZonePatch> patches = pm.zones.Patches;
            for (int i = 0; i < patches.Count; i++)
                if (pm.zones.KeptFor(i) is { Count: > 0 } keptLevels) kept0.Add((patches[i].anchor, keptLevels.ToArray()));
            r.Add(() =>
            {
                for (int i = 0; i < pm.zones.Patches.Count; i++) pm.zones.SetKept(i, null);
                foreach ((int patchAt, SecurityLevelDef[] keptFor) in kept0)
                    if (pm.zones.PatchAt(patchAt) is >= 0 and var i) pm.zones.SetKept(i, keptFor);
            });
            for (int i = 0; i < patches.Count; i++) pm.zones.SetKept(i, null);
            foreach (Pawn real in map.mapPawns.PrisonersOfColonySpawned.ToList())
                if (real.Inmate() is { } rc)
                {
                    bool active0 = rc.activeFlag;
                    r.Add(() => rc.activeFlag = active0);
                    rc.activeFlag = false;
                }
            s.schoolers = new[] { BookingInmate(map, r, s.level, "A", false), BookingInmate(map, r, s.level, "B", false), BookingInmate(map, r, s.level, "C", false) };
            s.addicts = new[] { BookingInmate(map, r, s.level, "D", true), BookingInmate(map, r, s.level, "E", true) };
            s.Reset();
            // (Calm and at work: the premises of a class at all.)
            foreach (Pawn p in s.Inmates)
                if (!InmateUtility.IsActiveInmate(p) || !s.big.Eligible(p, p.Inmate()) || g.CurrentActivity(p) != s.big.Slot)
                {
                    RPLog.Message($"[RimPrison:test] class bookings: {p.LabelShort} would not take foundation education now (active {InmateUtility.IsActiveInmate(p)}, eligible {s.big.Eligible(p, p.Inmate())}, activity {g.CurrentActivity(p)?.defName}), the bookings are not checked");
                    return null;
                }
            foreach (Pawn p in s.addicts)
                if (!s.small.Eligible(p, p.Inmate()))
                {
                    RPLog.Message($"[RimPrison:test] class bookings: {p.LabelShort} would not take substance abuse treatment, the bookings are not checked");
                    return null;
                }
            // The teacher: a counsellor on duty, else one of the check's own; every other is held by a class of the check's own.
            StaffRoleDef counsellor = RPProgramDefOf.RP_Counsellor;
            List<Pawn> teachers = map.mapPawns.FreeColonistsSpawned.Where(p => ProgramSystem.CanTeach(p, counsellor)).ToList();
            if (teachers.Count == 0) teachers.Add(ThrowawayStaff(map, r, counsellor));
            s.teacher = teachers[0];
            if (!ProgramSystem.CanTeach(s.teacher, counsellor))
            {
                RPLog.Message("[RimPrison:test] class bookings: no counsellor who can teach (the teaching work is off for them), the bookings are not checked");
                return null;
            }
            s.otherTeachers = teachers.Skip(1).ToList();
            foreach (Pawn t in s.otherTeachers.Concat(new[] { s.teacher }))
            {
                CompStaff sc = t.GetComp<CompStaff>();
                int leave0 = sc.leaveUntilTick;
                r.Add(() => sc.leaveUntilTick = leave0);
            }
            // The room: the one a class for these inmates would take; the others are held.
            if (!ProgramSystem.TryFindVenue(map, pm, s.big, out IntVec3 roomAt, out int places, out _, s.Inmates.ToList()) || places < 2)
            {
                RPLog.Message($"[RimPrison:test] class bookings: no room in the classroom zone with two seats the inmates may use, the bookings are not checked (found {places})");
                return null;
            }
            s.room = roomAt;
            var rooms = new List<(IntVec3 anchor, int places)>();
            ProgramSystem.ListVenues(map, pm, s.big, rooms);
            Room chosen = roomAt.GetRoom(map);
            s.otherRooms = rooms.Where(x => x.anchor.GetRoom(map) != chosen).Select(x => x.anchor).ToList();
            s.Reset();
            return s;
        }

        // ---- the texts ---------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckClassBookingTranslations(Map map)
        {
            Guarded("class bookings: every reason has its text", map, r =>
            {
                foreach (ClassMiss miss in Enum.GetValues(typeof(ClassMiss)))
                    if (miss != ClassMiss.None && !("RP_BookMiss_" + miss).CanTranslate()) BookFail("texts", $"the reason {miss} has no keyed string RP_BookMiss_{miss}");
                ProgramDef prog = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation");
                string line = ClassBookings.MissText(prog, new ClassMissRecord { hour = 14, reason = ClassMiss.NoTeacher });
                if (!line.StartsWith("missed at 14:00 - ") || !line.Contains(prog.TeacherLabel)) BookFail("texts", $"a miss at 14:00 for no teacher reads \"{line}\", expected \"missed at 14:00 - no {prog.TeacherLabel} on duty\"");
                string hours = ClassBookings.HoursLabel((1 << 9) | (1 << 10) | (1 << 14) | (1 << 23));
                if (hours != "09:00-11:00, 14:00-15:00, 23:00-00:00") BookFail("texts", $"the hours 9, 10, 14 and 23 read \"{hours}\"");
            });
        }

        // ---- one booking for a program in an hour -----------------------------------------------------------------------------------------------------------------------------

        private static void CheckClassBookingOnePerHour(Map map)
        {
            Guarded("class bookings: a program has one booking for an hour", map, r =>
            {
                var bk = new ClassBookings();
                ProgramDef prog = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation"), other = DefDatabase<ProgramDef>.GetNamed("RP_SubstanceAbuse");
                ClassBooking a = bk.Add(prog, (1 << 9) | (1 << 10));
                ClassBooking b = bk.Add(prog, (1 << 10) | (1 << 11));
                if (a == null || b == null || b.hours != 1 << 11 || a.hours != ((1 << 9) | (1 << 10)))
                    BookFail("one per hour", $"two bookings of one program for 9-10 and 10-11 hold {a?.hours} and {b?.hours} (the second is to keep only the 11th hour)");
                if (bk.Add(prog, 1 << 9) != null) BookFail("one per hour", "a booking for an hour the program already has was made");
                if (bk.Toggle(b, 9) || b.Covers(9)) BookFail("one per hour", "an hour another booking of the program holds was booked");
                if (bk.Add(other, 1 << 9) == null) BookFail("one per hour", "another program was refused an hour a different program has");
                bk.Toggle(b, 11);
                if (bk.list.Contains(b)) BookFail("one per hour", "a booking whose last hour was freed was kept");
                if (bk.list.Select(x => x.id).Distinct().Count() != bk.list.Count) BookFail("one per hour", "two bookings have the same id");
                // A booking is taken up in an hour once.
                ClassBooking c = bk.list.First(x => x.program == prog);
                if (bk.Due(prog, 9, 100) != c) BookFail("one per hour", "a booking is not due in its hour");
                c.handledStamp = 100;
                if (bk.Due(prog, 9, 100) != null) BookFail("one per hour", "a booking is due again in an hour it has been taken up in");
                if (bk.Due(prog, 9, 101) != c) BookFail("one per hour", "a booking is not due in its hour the next day");
                if (bk.Due(prog, 12, 101) != null) BookFail("one per hour", "a booking is due in an hour it does not have");
            });
        }

        // ---- two courses for one room and one teacher ---------------------------------------------------------------------------------------------------------------------------

        private static void CheckClassBookingContention(Map map)
        {
            Guarded("class bookings: the booked small class has the room, the fuller one the next free hour", map, r =>
            {
                BookingScene s = BookingSetUp(map, r);
                if (s == null) return;
                PrisonMap pm = s.pm;
                PrisonGame g = s.g;
                // The control: with no booking the fuller class has the room and the teacher.
                ProgramSystem.HourlyTick(map, pm, g);
                bool bigFirst = ProgramSystem.SessionFor(pm, s.big) != null, smallFirst = ProgramSystem.SessionFor(pm, s.small) != null;
                if (smallFirst) BookFail("contention", $"with no booking the smaller class was taken before the fuller one ({s.Seen()})");
                if (!bigFirst)
                {
                    RPLog.Message($"[RimPrison:test] class bookings: with no booking the fuller class did not start either, the contention is not checked ({s.Seen()})");
                    return;
                }
                // Booked: the small class at this hour, the other at the next.
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                s.Reset();
                int nextHour = (s.hour + 1) % 24;
                ClassBooking booked = pm.bookings.Add(s.small, 1 << s.hour);
                ClassBooking later = pm.bookings.Add(s.big, 1 << nextHour);
                int funds0 = g.ledger.funds, spends0 = s.Spends();
                ProgramSystem.HourlyTick(map, pm, g);
                ProgramSession ses = ProgramSystem.SessionFor(pm, s.small);
                if (ses == null || ProgramSystem.SessionFor(pm, s.big) != null)
                {
                    BookFail("contention", $"booked for this hour, the smaller class did not get the room and the teacher ({s.Seen()})");
                    return;
                }
                if (ses.attendees.Count != 2 || s.addicts.Any(p => !ses.attendees.Contains(p))) BookFail("contention", $"the booked class has {ses.attendees.Count} attendees, expected the two addicts ({s.Seen()})");
                if (ses.anchor.GetRoom(map) != s.room.GetRoom(map)) BookFail("contention", $"the booked class is in the room at {ses.anchor}, expected the one at {s.room}");
                if (booked.missStreak != 0 || booked.misses.Count != 0 || booked.handledStamp != clock0 / GenDate.TicksPerHour)
                    BookFail("contention", $"a booking that ran has streak {booked.missStreak}, {booked.misses.Count} misses and was taken up in hour {booked.handledStamp} (expected {clock0 / GenDate.TicksPerHour})");
                if (g.ledger.funds != funds0 - s.small.costPerSession || s.Spends() != spends0 + 1) BookFail("contention", $"the session cost {funds0 - g.ledger.funds} in {s.Spends() - spends0} charge(s), expected {s.small.costPerSession} in one");
                // Taught through: the teacher on it and the attendees listening for the whole session, then it is over.
                int length = ses.endTick - ses.startTick;
                ses.taughtTicks = length;
                foreach (Pawn p in ses.attendees) ses.listenedTicks[p] = length;
                ses.endTick = clock0;
                ProgramSystem.CompleteFinished(pm);
                foreach (Pawn p in s.addicts)
                    if (p.Inmate().programProgress.GetValueOrDefault(s.small) != 1) BookFail("contention", $"{p.LabelShort} has {p.Inmate().programProgress.GetValueOrDefault(s.small)} sessions of the course after the booked class, expected 1");
                foreach (Pawn p in s.schoolers)
                    if (p.Inmate().programProgress.Count > 0) BookFail("contention", $"{p.LabelShort} was credited a session of a class they were not in");
                if (g.ledger.funds != funds0 - s.small.costPerSession || s.Spends() != spends0 + 1) BookFail("contention", $"after the class was over the funds are {g.ledger.funds} in {s.Spends() - spends0} charge(s), expected {funds0 - s.small.costPerSession} in one (no refund, no second charge)");
                // The pass repeated in the same hour: the booking is not taken up again, so the fuller class has the room (no second booked class for the addicts).
                ProgramSystem.HourlyTick(map, pm, g);
                if (ProgramSystem.SessionFor(pm, s.small) != null) BookFail("contention", $"the same hour's pass again started the booked class a second time ({s.Seen()})");
                if (ProgramSystem.SessionFor(pm, s.big) == null) BookFail("contention", $"the fuller class did not start in the free room after the booked one ({s.Seen()})");
                pm.sessions.RemoveAll(x => x.program == s.big);
                // The next hour: the booking of the fuller class is its slot.
                Find.TickManager.DebugSetTicksGame(clock0 + GenDate.TicksPerHour);
                if (GenLocalDate.HourOfDay(map) != nextHour) BookFail("contention", $"the clock moved on an hour to {GenLocalDate.HourOfDay(map)}, expected {nextHour}");
                int spends1 = s.Spends();
                ProgramSystem.HourlyTick(map, pm, g);
                ProgramSession laterSession = ProgramSystem.SessionFor(pm, s.big);
                if (laterSession == null || later.missStreak != 0 || later.handledStamp != (clock0 + GenDate.TicksPerHour) / GenDate.TicksPerHour)
                    BookFail("contention", $"the fuller class's booking for the next hour did not run ({s.Seen()})");
                if (s.Spends() != spends1 + 1) BookFail("contention", $"the next hour's class was charged {s.Spends() - spends1} time(s), expected once");
                Find.TickManager.DebugSetTicksGame(clock0);
            });
        }

        // ---- two courses for one teacher, two rooms free ----------------------------------------------------------------------------------------------------------------------

        /// <summary>A teacher teaches one session at a time: with two rooms free and the one teacher, two classes that want the hour (two bookings, or a booking and the automatic pass) give one the teacher and the other a miss for the teacher.</summary>
        private static void CheckClassBookingOneTeacher(Map map)
        {
            Guarded("class bookings: two classes for one teacher in one hour, one runs and the other misses for the teacher", map, r =>
            {
                BookingScene s = BookingSetUp(map, r);
                if (s == null) return;
                PrisonMap pm = s.pm;
                PrisonGame g = s.g;
                if (s.otherRooms.Count == 0)
                {
                    RPLog.Message("[RimPrison:test] class bookings: the classroom zone has one room, a second room for the second class is not there, one teacher for two classes is not checked");
                    return;
                }
                int now = Find.TickManager.TicksGame;
                // The scene as the booking checks have it, but a second room is free (the class that holds it, and its teacher, are gone: the other counsellors are on leave, so the check's teacher is the one).
                void TwoRoomsOneTeacher()
                {
                    s.Reset();
                    pm.sessions.RemoveAll(x => x.anchor == s.otherRooms[0]);
                    foreach (Pawn t in s.otherTeachers) t.GetComp<CompStaff>().leaveUntilTick = now + 5000;
                }
                string Teaching() => string.Join(", ", pm.sessions.Where(x => x.cost > 0).Select(x => $"{x.program.defName} taught by {x.teacher?.LabelShort}"));
                int OfTheTeacher() => pm.sessions.Count(x => x.Active && x.teacher == s.teacher);

                // Two bookings for the hour: the first (the smaller class) runs; the other cannot, with the room free, for the teacher.
                TwoRoomsOneTeacher();
                var rooms = new List<(IntVec3 anchor, int places)>();
                ProgramSystem.ListVenues(map, pm, s.big, rooms);
                if (rooms.Count(x => x.places >= 2) < 2)
                {
                    RPLog.Message($"[RimPrison:test] class bookings: {rooms.Count(x => x.places >= 2)} room(s) with two seats are free, one teacher for two classes is not checked");
                    return;
                }
                ClassBooking first = pm.bookings.Add(s.small, 1 << s.hour);
                ClassBooking second = pm.bookings.Add(s.big, 1 << s.hour);
                ProgramSystem.HourlyTick(map, pm, g);
                if (ProgramSystem.SessionFor(pm, s.small) == null || ProgramSystem.SessionFor(pm, s.small).teacher != s.teacher)
                    BookFail("one teacher", $"of two bookings for the hour the first did not run with the teacher ({s.Seen()}; {Teaching()})");
                if (ProgramSystem.SessionFor(pm, s.big) != null || OfTheTeacher() != 1)
                    BookFail("one teacher", $"the one teacher has {OfTheTeacher()} session(s) in the hour, expected 1 ({Teaching()})");
                if (second.LastMiss == null || second.LastMiss.reason != ClassMiss.TeacherBusy || second.missStreak != 1)
                    BookFail("one teacher", $"the second booking's miss is {second.LastMiss?.reason.ToString() ?? "none"} (streak {second.missStreak}), expected TeacherBusy");
                if (first.missStreak != 0) BookFail("one teacher", "the booking that ran has a miss");
                // The pass repeated: still one session of the teacher.
                first.handledStamp = second.handledStamp = -1;
                ProgramSystem.HourlyTick(map, pm, g);
                if (OfTheTeacher() != 1) BookFail("one teacher", $"after the pass again the teacher has {OfTheTeacher()} session(s), expected 1 ({Teaching()})");

                // A booking and the automatic pass: the booked class has the teacher, and the fuller one that would be started on its own waits for them ("teacher busy" on the Programs tab).
                TwoRoomsOneTeacher();
                pm.bookings.Add(s.small, 1 << s.hour);
                ProgramSystem.HourlyTick(map, pm, g);
                if (ProgramSystem.SessionFor(pm, s.small) == null || ProgramSystem.SessionFor(pm, s.big) != null || OfTheTeacher() != 1)
                    BookFail("one teacher", $"a booking and the automatic pass gave the teacher {OfTheTeacher()} session(s), expected the booked one alone ({Teaching()})");
                Pawn free = ProgramSystem.FreeTeacher(map, pm, s.big, out bool onSite);
                if (free != null || !onSite || ProgramSystem.Blocker(map, pm, s.big) != ProgramBlock.TeacherBusy)
                    BookFail("one teacher", $"the fuller class should be held up by the teacher (free teacher {free?.LabelShort}, on site {onSite}, blocker {ProgramSystem.Blocker(map, pm, s.big)})");
                var cand = new ProgramSystem.Candidate { program = s.big };
                cand.pawns.AddRange(s.schoolers);
                if (ProgramSystem.TryStart(map, pm, g, cand, out ClassMiss why) || why != ClassMiss.TeacherBusy || OfTheTeacher() != 1)
                    BookFail("one teacher", $"the automatic class was tried with the teacher teaching: miss {why}, the teacher has {OfTheTeacher()} session(s), expected TeacherBusy and 1");
                s.Reset();
            });
        }

        // ---- the reasons ---------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>One booked hour of the small class, with something in the way: the miss it was, whether the fuller class took the free room and teacher, the funds spent, the sessions credited.</summary>
        private static void BookingMissed(BookingScene s, string check, ClassMiss expected, Action stop, bool fullerStarts)
        {
            PrisonMap pm = s.pm;
            PrisonGame g = s.g;
            s.Reset();
            ClassBooking b = pm.bookings.Add(s.small, 1 << s.hour);
            stop();
            int funds0 = g.ledger.funds, spends0 = s.Spends();
            ProgramSystem.HourlyTick(s.map, pm, g);
            if (ProgramSystem.SessionFor(pm, s.small) != null) BookFail(check, $"the booked class ran though it should have missed for {expected} ({s.Seen()})");
            if (b.LastMiss == null || b.LastMiss.reason != expected || b.missStreak != 1 || b.misses.Count != 1 || b.LastMiss.hour != s.hour)
                BookFail(check, $"the miss kept is {(b.LastMiss == null ? "none" : b.LastMiss.reason + " at " + b.LastMiss.hour)} (streak {b.missStreak}), expected {expected} at {s.hour} ({s.Seen()})");
            bool fuller = ProgramSystem.SessionFor(pm, s.big) != null;
            if (fuller != fullerStarts) BookFail(check, $"the fuller class {(fuller ? "started" : "did not start")}, expected it to {(fullerStarts ? "fill the room the failed booking left" : "wait")} ({s.Seen()})");
            int expectedSpend = fullerStarts ? s.big.costPerSession : 0;
            if (funds0 - g.ledger.funds != expectedSpend || s.Spends() != spends0 + (fullerStarts ? 1 : 0))
                BookFail(check, $"the funds went down by {funds0 - g.ledger.funds} in {s.Spends() - spends0} charge(s), expected {expectedSpend}: a booking that cannot run charges nothing");
            foreach (Pawn p in s.addicts)
                if (p.Inmate().programProgress.GetValueOrDefault(s.small) != 0) BookFail(check, $"{p.LabelShort} was credited a session of the class that missed");
            string text = b.LastMiss == null ? "" : ClassBookings.MissText(s.small, b.LastMiss);
            if (text.Length == 0 || text.Contains("RP_Book")) BookFail(check, $"the miss reads \"{text}\"");
            else RPLog.Message($"[RimPrison:test] class bookings: {check}: {text}");
        }

        private static void CheckClassBookingReasons(Map map)
        {
            Guarded("class bookings: a booked class that cannot run says why and charges nothing", map, r =>
            {
                BookingScene s = BookingSetUp(map, r);
                if (s == null) return;
                PrisonMap pm = s.pm;
                PrisonGame g = s.g;
                int now = Find.TickManager.TicksGame;
                IEnumerable<Pawn> Counsellors() => s.otherTeachers.Concat(new[] { s.teacher });
                // The teacher on leave (every counsellor: the class is not held up by a stand-in).
                BookingMissed(s, "teacher on leave", ClassMiss.NoTeacher, () =>
                {
                    pm.sessions.RemoveAll(x => x.teacher != null);
                    foreach (Pawn t in Counsellors()) t.GetComp<CompStaff>().leaveUntilTick = now + 5000;
                }, fullerStarts: false);
                // Every teacher is teaching something else.
                BookingMissed(s, "teacher busy", ClassMiss.TeacherBusy, () => s.Hold(IntVec3.Invalid, s.teacher), fullerStarts: false);
                // The room kept for another level (booked in that room).
                int patch = pm.zones.PatchAt(s.room);
                if (patch < 0 || pm.zones.Patches[patch].zone != s.small.Zone) RPLog.Message($"[RimPrison:test] class bookings: the booked room at {s.room} is in no patch of the classroom, the room kept for another level is not checked");
                else
                {
                    SecurityLevelDef another = DefDatabase<SecurityLevelDef>.AllDefsListForReading.First(l => l != s.level);
                    r.Add(() => pm.zones.SetKept(pm.zones.PatchAt(s.room), null));
                    BookingMissed(s, "room kept for another level", ClassMiss.VenueClosed, () =>
                    {
                        if (!pm.zones.SetKept(patch, new[] { another })) BookFail("room kept for another level", "the classroom's patch cannot be kept for a level");
                        pm.bookings.SetAnchor(pm.bookings.list[0], s.room);
                    }, fullerStarts: false);
                    pm.zones.SetKept(patch, null);
                    // ...and the booked class runs again once the room is open to them (the streak is over, the miss is kept).
                    ClassBooking again = pm.bookings.list[0];
                    again.handledStamp = -1;
                    ProgramSystem.HourlyTick(map, pm, g);
                    if (ProgramSystem.SessionFor(pm, s.small) == null || again.missStreak != 0 || again.misses.Count != 1)
                        BookFail("room kept for another level", $"opened to them again, the booking did not run, or lost its history ({s.Seen()})");
                }
                // The room gone: booked in a room that is not one of the zone's (a cell of the yard), and the free room goes to the fuller class.
                IntVec3 elsewhere = pm.zones.CellsOf(RPDefOf.RP_Yard).FirstOrDefault(c => c.Standable(map) && c.GetRoom(map) is { } room && room != s.room.GetRoom(map));
                if (!elsewhere.IsValid || elsewhere == default) RPLog.Message("[RimPrison:test] class bookings: no yard cell to book a room that is not the zone's, the booking of a room that is gone is not checked");
                else
                    BookingMissed(s, "room gone", ClassMiss.VenueGone, () => pm.bookings.SetAnchor(pm.bookings.list[0], elsewhere), fullerStarts: true);
                // The room held by another class.
                BookingMissed(s, "room held", ClassMiss.VenueBusy, () =>
                {
                    s.Hold(s.room, null);
                    // (And the teacher: the room is the reason, not the teacher.)
                }, fullerStarts: false);
                // A meal for the cohort in the booked hour.
                BookingMissed(s, "meal slot", ClassMiss.NoSlot, () => s.schedule.Set(s.hour, RPRegimeDefOf.RP_Eat), fullerStarts: false);
                // Lockup, the same way.
                BookingMissed(s, "lockup slot", ClassMiss.NoSlot, () => s.schedule.Set(s.hour, RPDefOf.RP_Lockup), fullerStarts: false);
                // No one eligible.
                BookingMissed(s, "no eligible inmate", ClassMiss.NobodyEligible, () => { foreach (Pawn p in s.addicts) p.Inmate().passedPrograms.Add(s.small); }, fullerStarts: true);
                // One student.
                BookingMissed(s, "one student", ClassMiss.TooFew, () => s.addicts[1].Inmate().passedPrograms.Add(s.small), fullerStarts: true);
                // The program stopped (the warden's own decision): its booking is skipped while it is stopped - no miss, no streak, the room is the fuller class's - and still there to run when it is switched on again in the hour.
                s.Reset();
                ClassBooking stopped = pm.bookings.Add(s.small, 1 << s.hour);
                g.programs.disabled.Add(s.small);
                ProgramSystem.HourlyTick(map, pm, g);
                if (ProgramSystem.SessionFor(pm, s.small) != null) BookFail("program stopped", $"the class ran though its program is stopped ({s.Seen()})");
                if (stopped.LastMiss != null || stopped.missStreak != 0 || stopped.misses.Count != 0) BookFail("program stopped", $"a stopped program's booking was kept as a miss ({s.Seen()})");
                if (ProgramSystem.SessionFor(pm, s.big) == null) BookFail("program stopped", $"the fuller class did not start in the room the stopped program left ({s.Seen()})");
                g.programs.disabled.Remove(s.small);
                pm.sessions.RemoveAll(x => x.program == s.big);
                ProgramSystem.HourlyTick(map, pm, g);
                if (ProgramSystem.SessionFor(pm, s.small) == null || stopped.missStreak != 0) BookFail("program stopped", $"switched on again, the booking did not run ({s.Seen()})");
                // No funds for the session.
                BookingMissed(s, "no funds", ClassMiss.NoFunds, () => g.ledger.funds = 0, fullerStarts: false);
                s.Reset();
            });
        }

        // ---- three in a row, five kept, none cancelled -------------------------------------------------------------------------------------------------------------------------

        private static void CheckClassBookingStreak(Map map)
        {
            Guarded("class bookings: three misses in a row warn, five are kept, nothing is cancelled", map, r =>
            {
                BookingScene s = BookingSetUp(map, r);
                if (s == null) return;
                PrisonMap pm = s.pm;
                int now = Find.TickManager.TicksGame;
                pm.sessions.RemoveAll(x => x.teacher != null);
                foreach (Pawn t in s.otherTeachers.Concat(new[] { s.teacher })) t.GetComp<CompStaff>().leaveUntilTick = now + 5000;
                ClassBooking b = pm.bookings.Add(s.small, 1 << s.hour);
                for (int i = 1; i <= 6; i++)
                {
                    b.handledStamp = -1;
                    ProgramSystem.HourlyTick(map, pm, s.g);
                    if (b.missStreak != i) BookFail("streak", $"after {i} hours without a teacher the streak is {b.missStreak}");
                    if (b.Warns != (i >= ClassBookings.WarnAfter)) BookFail("streak", $"after {i} misses in a row the warning is {(b.Warns ? "on" : "off")}");
                }
                if (b.misses.Count != ClassBookings.MaxMisses) BookFail("streak", $"six misses keep {b.misses.Count} in the history, expected the last {ClassBookings.MaxMisses}");
                if (!pm.bookings.list.Contains(b)) BookFail("streak", "a booking that missed six hours in a row was cancelled");
                // The teacher back: it runs, the streak is over, the history stays.
                foreach (Pawn t in s.otherTeachers.Concat(new[] { s.teacher })) t.GetComp<CompStaff>().leaveUntilTick = -1;
                b.handledStamp = -1;
                ProgramSystem.HourlyTick(map, pm, s.g);
                if (ProgramSystem.SessionFor(pm, s.small) == null || b.missStreak != 0 || b.Warns || b.misses.Count != ClassBookings.MaxMisses)
                    BookFail("streak", $"with the teacher back the booking did not run, or kept its streak, or lost its history ({s.Seen()}, streak {b.missStreak}, {b.misses.Count} misses)");
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------------------------------------------------

        private static string ClassBookingDescribe(Map map)
        {
            ClassBookings bk = PrisonMap.Of(map).bookings;
            return $"{bk.nextId}:" + string.Join(",", bk.list.OrderBy(b => b.id).Select(b =>
                $"{b.id}/{b.program?.defName}/{b.hours}/{b.anchor}/{b.priority}/{b.missStreak}/{b.handledStamp}/{string.Join("+", b.misses.Select(m => $"{m.tick}@{m.hour}={m.reason}"))}"));
        }

        private static ClassBookings classBookings0;
        private static int classBookingSeedHour, classBookingSeedStamp;

        /// <summary>
        /// Saveload plan, before the snapshot: two bookings with every field off its default. The first is of the program the seeded class under way is of, covers this hour and has been taken up in it (the booking that started that class),
        /// with two misses; the second is of another program with a priority below zero and its own room.
        /// </summary>
        private static void SeedClassBookingForReload(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            classBookings0 = pm.bookings;
            int now = Find.TickManager.TicksGame;
            classBookingSeedHour = GenLocalDate.HourOfDay(map);
            classBookingSeedStamp = now / GenDate.TicksPerHour;
            var bk = new ClassBookings { nextId = 41 };
            bk.list.Add(new ClassBooking
            {
                id = 7, program = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation"), hours = (1 << classBookingSeedHour) | (1 << ((classBookingSeedHour + 5) % 24)), anchor = new IntVec3(12, 0, 9),
                priority = 3, missStreak = 2, handledStamp = classBookingSeedStamp,
                misses = { new ClassMissRecord { tick = now - 5000, hour = (classBookingSeedHour + 23) % 24, reason = ClassMiss.NoTeacher }, new ClassMissRecord { tick = now - 2500, hour = classBookingSeedHour, reason = ClassMiss.VenueClosed } },
            });
            bk.list.Add(new ClassBooking
            {
                id = 40, program = DefDatabase<ProgramDef>.GetNamed("RP_SubstanceAbuse"), hours = 1 << ((classBookingSeedHour + 9) % 24), priority = -2, missStreak = 1, handledStamp = classBookingSeedStamp - 24,
                misses = { new ClassMissRecord { tick = now - 60000, hour = (classBookingSeedHour + 9) % 24, reason = ClassMiss.NoSlot } },
            });
            pm.bookings = bk;
            RPLog.Message($"[RimPrison:test] saveload class bookings: {ClassBookingDescribe(map)}");
        }

        /// <summary>After the reload, before the seeded sessions are taken out: the bookings came back whole (the snapshot compared them), the booking that started the class under way is not due again in the hour it was taken up in (that is
        /// what keeps the pass from starting a second class: the saved stamp), and the class under way is still the one seeded session of its program (a second one is ruled out by <see cref="ProgramSystem.SessionFor"/>, which the pass asks
        /// before it looks at a booking, so this is a check of the reload and not of the booking); then the game's own bookings are put back.</summary>
        private static void CheckClassBookingAfterReload(Map map)
        {
            if (classBookings0 == null) return;
            PrisonMap pm = PrisonMap.Of(map);
            ClassBookings bk = pm.bookings;
            ProgramDef edu = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation");
            ClassBooking started = bk.list.FirstOrDefault(x => x.id == 7);
            if (bk.list.Count != 2 || started == null || started.misses.Count != 2 || started.misses[1].reason != ClassMiss.VenueClosed || started.priority != 3 || started.missStreak != 2)
                reloadProblems.Add($"saveload: the class bookings did not come back from the save ({ClassBookingDescribe(map)})");
            else if (bk.Due(edu, classBookingSeedHour, classBookingSeedStamp) != null)
                reloadProblems.Add("saveload: the booking that started the class under way is due again in the hour it was taken up in, after the reload");
            else if (bk.Due(edu, classBookingSeedHour, classBookingSeedStamp + 1) != started)
                reloadProblems.Add("saveload: the booking is not due in the next hour after the reload");
            else if (sessionsSeeded && pm.sessions.Count(x => x.program == edu && x.cost == 0 && x.taughtTicks == 777) != 1)
                reloadProblems.Add($"saveload: the class of {edu.defName} under way did not come back from the save as the one session ({pm.sessions.Count(x => x.program == edu)} of its program)");
            else RPLog.Message("[RimPrison:test] saveload class bookings: the bookings are intact and the booking that started the class under way is not due again in its hour");
            pm.bookings = classBookings0;
            classBookings0 = null;
        }
    }
}
