using System.Linq;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>A reform program: sessions in a room of its own with its own teacher, an exam, and what passing changes.</summary>
    public class ProgramDef : Def
    {
        /// <summary>Where it runs: the zone whose rooms hold the class (default: the classroom).</summary>
        public PrisonZoneDef zone;
        /// <summary>The staff roles that teach it, the first preferred (default: the counsellor).</summary>
        public List<StaffRoleDef> teacherRoles = new List<StaffRoleDef>();
        /// <summary>What each attendee sits at, any of these (default: the school desk; dining chairs, stools and armchairs in the chapel).</summary>
        public List<ThingDef> seats = new List<ThingDef>();
        /// <summary>No furniture: attendees stand in the room (workshop induction), a place for every second cell.</summary>
        public bool standing;
        /// <summary>The regime slot the class takes its attendees from (default: Work; FreeTime lets maximum security attend).</summary>
        public RegimeActivityDef slot;
        public int sessions = 4;
        public int sessionHours = 1;
        public int costPerSession = 150;
        public float passChance = 0.5f;
        /// <summary>Pass chance + this × skill level.</summary>
        public SkillDef examSkill;
        public float passChancePerSkillLevel = 0.03f;
        // Eligibility
        public bool everyone;
        public bool drugCrimesOrAddicts;
        public List<ReputationDef> forReputations = new List<ReputationDef>();
        public List<InmateJobDef> forJobs = new List<InmateJobDef>();
        public float maxSkillLevel = 99f;
        // Rewards
        public int grantOnPass = 150;
        public List<ReputationDef> removesReputations = new List<ReputationDef>();
        public bool curesAddictions;
        public float skillXpOnPass;
        /// <summary>Added to the exam's pass chance in full for an inmate who reads as well as can be, in proportion to how well they read (see <see cref="LibrarySystem"/>).</summary>
        public float literacyBonus;
        /// <summary>Must have passed this program first (general education after foundation).</summary>
        public ProgramDef requiresProgram;
        /// <summary>Only for religious inmates.</summary>
        public bool religiousOnly;
        /// <summary>On passing: the faith need is topped up and decays slower from then on (stored on the inmate).</summary>
        public bool deepensFaith;
        /// <summary>The first part of a course: passing it opens the part that needs it (<see cref="requiresProgram"/>) but is no program of its own, so it adds nothing to the count of programs passed that parole and the reoffend roll read.</summary>
        public bool stage;
        /// <summary>Passing it certifies a trade (see <see cref="Vocation"/>): a better chance of a job in place on parole, and a lower reoffend band.</summary>
        public bool trade;
        public int uiOrder;

        public PrisonZoneDef Zone => zone ?? RPProgramDefOf.RP_Classroom;
        private List<StaffRoleDef> defaultTeachers;
        public List<StaffRoleDef> TeacherRoles => teacherRoles.Count > 0 ? teacherRoles : defaultTeachers ??= new List<StaffRoleDef> { RPProgramDefOf.RP_Counsellor };
        /// <summary>"psychologist or counsellor": for the Programs screen.</summary>
        public string TeacherLabel
        {
            get
            {
                string label = null;
                foreach (StaffRoleDef r in TeacherRoles) label = label == null ? r.label : "RP_TeacherRoleOr".Translate(label, r.label).ToString();
                return label;
            }
        }
        private static readonly List<ThingDef> noSeats = new List<ThingDef>();
        private List<ThingDef> defaultSeats;
        /// <summary>The furniture it seats attendees at (none for a class that stands).</summary>
        public List<ThingDef> Seats => standing ? noSeats : seats.Count > 0 ? seats : defaultSeats ??= new List<ThingDef> { RPProgramDefOf.RP_SchoolDesk };
        /// <summary>"dining chair, stool, armchair": for the Programs screen.</summary>
        public string SeatLabel => GenText.ToCommaList(Seats.Select(d => d.label));
        public RegimeActivityDef Slot => slot ?? RPLaborDefOf.RP_Work;

        /// <summary>Could take it at all: the prerequisite passed, religious if it is for the religious, not past the exam.</summary>
        public bool Suitable(Pawn p, CompInmate c) =>
            (requiresProgram == null || c.passedPrograms.Contains(requiresProgram)) && (!religiousOnly || c.religious)
            && (examSkill == null || (p.skills?.GetSkill(examSkill)?.Level ?? 0) <= maxSkillLevel);

        public bool Eligible(Pawn p, CompInmate c) =>
            // Too beaten down to learn.
            !Suppression.Held(c) && Qualifies(p, c);

        /// <summary>Would be eligible but for suppression: the inmates a class is missing because they are held down (the Programs screen says so).</summary>
        public bool ShutOut(Pawn p, CompInmate c) => Suppression.Held(c) && Qualifies(p, c);

        /// <summary>Eligible, whatever their suppression.</summary>
        private bool Qualifies(Pawn p, CompInmate c)
        {
            if (c.passedPrograms.Contains(this)) return false;
            if (!Suitable(p, c)) return false;
            if (c.referral == this) return true; // referred by the warden: suitable, whatever the usual target group
            if (everyone) return true;
            if (drugCrimesOrAddicts && (c.crime is { drugRelated: true } || AnyAddiction(p))) return true;
            foreach (ReputationDef r in forReputations) if (c.HasReputation(r)) return true;
            foreach (InmateJobDef j in forJobs) if (LaborUtility.Eligible(c, j, forProgram: true)) return true;
            return false;
        }

        private static bool AnyAddiction(Pawn p)
        {
            foreach (Hediff h in p.health.hediffSet.hediffs) if (h is Hediff_Addiction) return true;
            return false;
        }
    }

    public class ProgramSession : IExposable
    {
        public ProgramDef program;
        public int startTick;
        public int endTick;
        public Pawn teacher;
        public List<Pawn> attendees = new List<Pawn>();
        /// <summary>A cell of the room it holds (one session per room); invalid for a session from an old save, which holds none.</summary>
        public IntVec3 anchor = IntVec3.Invalid;
        /// <summary>Ticks the teacher actually spent teaching in the program's zone.</summary>
        public int taughtTicks;
        /// <summary>Ticks each attendee actually spent listening (credit does not hang on where they stand when the class ends: pawns tick before the map does).</summary>
        public Dictionary<Pawn, int> listenedTicks = new Dictionary<Pawn, int>();
        public int cost;

        private List<Pawn> listenedPawns;
        private List<int> listenedCounts;

        /// <summary>Whether this attendee listened for at least half the session.</summary>
        public bool Attended(Pawn p) => listenedTicks.TryGetValue(p, out int ticks) && ticks >= (endTick - startTick) / 2;

        public bool Active => Find.TickManager.TicksGame < endTick;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref program, "program");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref endTick, "endTick");
            Scribe_References.Look(ref teacher, "teacher");
            Scribe_Collections.Look(ref attendees, "attendees", LookMode.Reference);
            Scribe_Values.Look(ref anchor, "anchor", IntVec3.Invalid);
            Scribe_Values.Look(ref taughtTicks, "taughtTicks");
            Scribe_Collections.Look(ref listenedTicks, "listenedTicks", LookMode.Reference, LookMode.Value, ref listenedPawns, ref listenedCounts);
            Scribe_Values.Look(ref cost, "cost");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                attendees ??= new List<Pawn>();
                attendees.RemoveAll(p => p == null);
                listenedTicks ??= new Dictionary<Pawn, int>();
                listenedTicks.RemoveAll(kv => kv.Key == null);
            }
        }
    }

    [DefOf]
    public static class RPProgramDefOf
    {
        public static PrisonZoneDef RP_Classroom;
        public static JobDef RP_AttendClass;
        public static JobDef RP_TeachClass;
        public static StaffRoleDef RP_Counsellor;
        public static ThingDef RP_SchoolDesk;
        public static ProgramDef RP_SubstanceAbuse;

        static RPProgramDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPProgramDefOf));
    }

    /// <summary>Why a program that has inmates to teach is not running (None: nothing stops it).</summary>
    public enum ProgramBlock { None, NoRoom, NoTeacher, RoomBusy, TeacherBusy }

    /// <summary>How the Programs screen colours a program's status line.</summary>
    public enum ProgramStatusKind { Running, Blocked, Waiting }

    /// <summary>
    /// Programs run in parallel, one session per program at most, each in a room of its own (a room of the program's
    /// zone with seats) and with a teacher of its role (who teaches one session at a time).
    /// </summary>
    public static class ProgramSystem
    {
        /// <summary>Standing programs make a place for every second standable cell of the room, up to this many.</summary>
        public const int StandingCellsPerAttendee = 2, MaxStanding = 8;

        /// <summary>A teacher of a less preferred role leaves a new session to the preferred teacher assigned to it for this long.</summary>
        public const int PreferredGraceTicks = GenDate.TicksPerHour / 4;

        // Scratch lists for the venue search (hourly and the Programs screen only).
        private static readonly List<Room> venueRooms = new List<Room>();
        private static readonly List<int> venueCounts = new List<int>();
        /// <summary>The patches kept for some levels that hold a counted seat or cell, as (room index into <see cref="venueRooms"/>, patch).</summary>
        private static readonly HashSet<(int room, int patch)> venueKept = new HashSet<(int, int)>();
        private static readonly List<IntVec3> venueAnchors = new List<IntVec3>();
        private static readonly HashSet<Pawn> taken = new HashSet<Pawn>();
        private static readonly List<IntVec3> venueCells = new List<IntVec3>();
        private static readonly Dictionary<ProgramDef, bool> canRunNow = new Dictionary<ProgramDef, bool>();

        /// <summary>A program kept waiting with someone referred to it gains this much priority (in class places) per hour it waits.</summary>
        public const int AgingPerHour = 3;

        internal class Candidate
        {
            public ProgramDef program;
            public readonly List<Pawn> pawns = new List<Pawn>();
            public bool referred;
            /// <summary>A job with a free slot waits for this course (see <see cref="JobWaits"/>).</summary>
            public bool jobWaits;
            /// <summary>The booking for this hour (see <see cref="ClassBooking"/>): the class is tried before every other, in the booked room, and a failure is kept with its reason.</summary>
            public ClassBooking booking;
        }

        /// <summary>
        /// A job with a free slot asks for this program, or for the one it is the first part of (the kitchen has stoves and no hands, the workshop has tailor benches and no tailors): inmates cannot hold the job without it,
        /// so it goes before the others for the room, the teacher and the inmates (who are eligible for several programs at once, and would otherwise all be taken by the fuller classes, hour after hour).
        /// </summary>
        internal static bool JobWaits(Map map, ProgramDef prog)
        {
            foreach (InmateJobDef j in DefDatabase<InmateJobDef>.AllDefsListForReading)
            {
                ProgramDef need = j.requiredProgram;
                if (need == null || need != prog && need.requiresProgram != prog || FoodScare.StoodDown(j)) continue;
                int holders = 0;
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate()?.job == j) holders++;
                if (j.Worker.Capacity(map) > holders) return true;
            }
            return false;
        }

        /// <summary>
        /// A booked class goes before every other (the higher priority, then the older booking), then a program with someone referred to it, then one a job with a free slot waits for (<see cref="JobWaits"/>); otherwise the fullest class. A referred program that has been kept
        /// waiting climbs by <see cref="AgingPerHour"/> places an hour, so a smaller class does not lose the shared room or teacher to a
        /// fuller one every hour for ever.
        /// </summary>
        internal static int ComparePriority(PrisonGame game, Candidate a, Candidate b)
        {
            if ((a.booking != null) != (b.booking != null)) return a.booking != null ? -1 : 1;
            if (a.booking != null && b.booking != null)
            {
                if (a.booking.priority != b.booking.priority) return b.booking.priority.CompareTo(a.booking.priority);
                if (a.booking.id != b.booking.id) return a.booking.id.CompareTo(b.booking.id);
            }
            if (a.referred != b.referred) return a.referred ? -1 : 1;
            if (a.jobWaits != b.jobWaits) return a.jobWaits ? -1 : 1;
            int pa = a.pawns.Count + AgingPerHour * game.programs.WaitedHours(a.program), pb = b.pawns.Count + AgingPerHour * game.programs.WaitedHours(b.program);
            if (pa != pb) return pb.CompareTo(pa);
            return a.program.uiOrder.CompareTo(b.program.uiOrder);
        }

        /// <summary>Whether the screen and the referral alert should treat a program that is only waiting (not blocked) as a problem now.</summary>
        public static bool WaitedTooLong(PrisonGame game, ProgramDef prog) => game.programs.WaitedHours(prog) >= ProgramPolicy.AlertAfterHours;

        /// <summary>
        /// Hourly: finish the classes that are over, then start a session for each program that has a free room and a free teacher. A program booked for this hour (see <see cref="ClassBookings"/>)
        /// is tried first, in its booked room; when it cannot run, the hour is kept as a miss with its reason, and the rest are tried as they would have been.
        /// </summary>
        public static void HourlyTick(Map map, PrisonMap pm, PrisonGame game)
        {
            CompleteFinished(pm);
            taken.Clear();
            canRunNow.Clear();
            foreach (ProgramSession s in pm.sessions)
                foreach (Pawn p in s.attendees) taken.Add(p);
            int hour = GenLocalDate.HourOfDay(map), stamp = Find.TickManager.TicksGame / GenDate.TicksPerHour;
            var candidates = new List<Candidate>();
            foreach (ProgramDef prog in DefDatabase<ProgramDef>.AllDefsListForReading)
            {
                // (A program the warden has stopped skips its booking: no miss, no streak.)
                if (SessionFor(pm, prog) != null || !game.programs.IsRunning(prog)) continue;
                ClassBooking booking = pm.bookings.Due(prog, hour, stamp);
                if (booking != null) booking.handledStamp = stamp;
                var cand = new Candidate { program = prog, jobWaits = JobWaits(map, prog), booking = booking };
                // Referred inmates take the first seats.
                CollectAttendees(map, pm, game, prog, cand, referredPass: true);
                CollectAttendees(map, pm, game, prog, cand, referredPass: false);
                if (cand.pawns.Count > 0 || booking != null) candidates.Add(cand);
            }
            candidates.Sort((a, b) => ComparePriority(game, a, b));
            var kept = new List<ProgramDef>();
            foreach (Candidate cand in candidates)
            {
                bool started = TryStart(map, pm, game, cand, out ClassMiss why);
                if (cand.booking != null)
                {
                    if (started) cand.booking.NoteRan();
                    else cand.booking.NoteMissed(hour, why);
                }
                if (!started && cand.referred) kept.Add(cand.program);
            }
            game.programs.NoteWaiting(kept);
        }

        /// <summary>A class that would take the work slot does not take a worker who has work waiting in their job's hours (the post waits for the clerk's shift: a class started in it kept the clerk from the table).</summary>
        internal static bool KeepsToTheirJob(ProgramDef prog, Pawn p, CompInmate c) => prog.Slot == RPLaborDefOf.RP_Work && c.job != null && c.job.Worker.HasWork(p);

        private static void CollectAttendees(Map map, PrisonMap pm, PrisonGame game, ProgramDef prog, Candidate cand, bool referredPass)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = p.Inmate();
                if (c == null || taken.Contains(p) || (c.referral == prog) != referredPass) continue;
                if (!c.activeFlag || c.forcedActivity != null || !prog.Eligible(p, c)) continue;
                // Classes replace the slot the regime gives them (work or free time), never meals, sleep or the rest.
                if (game.CurrentActivity(p) != prog.Slot) continue;
                if (KeepsToTheirJob(prog, p, c)) continue;
                // A referral holds the inmate for that program, when it could take them right now (a room, a free teacher, no class running yet).
                if (!referredPass && c.referral is { } other && other.Slot == prog.Slot && game.programs.IsRunning(other) && other.Eligible(p, c) && CanRunNow(map, pm, other)) continue;
                cand.pawns.Add(p);
                if (referredPass) cand.referred = true;
            }
        }

        /// <summary>The program has no session yet, a free room and a free teacher: a referred inmate would be taught this hour.</summary>
        private static bool CanRunNow(Map map, PrisonMap pm, ProgramDef prog)
        {
            if (canRunNow.TryGetValue(prog, out bool ok)) return ok;
            ok = SessionFor(pm, prog) == null && TryFindVenueNow(map, pm, PrisonGame.Instance, prog, out _, out _) && FreeTeacher(map, pm, prog, out _) != null;
            return canRunNow[prog] = ok;
        }

        internal static bool TryStart(Map map, PrisonMap pm, PrisonGame game, Candidate cand) => TryStart(map, pm, game, cand, out _);

        /// <summary>Starts the class when it can; when it cannot, <paramref name="why"/> is what stopped it (the first of: nobody to teach, a room, a teacher, enough students, the funds).</summary>
        internal static bool TryStart(Map map, PrisonMap pm, PrisonGame game, Candidate cand, out ClassMiss why)
        {
            why = ClassMiss.None;
            ProgramDef prog = cand.program;
            // (Who is left to teach, in the order of the candidates: the referred first.)
            var cohort = new List<Pawn>();
            foreach (Pawn p in cand.pawns)
                if (!taken.Contains(p)) cohort.Add(p);
            if (cohort.Count == 0)
            {
                why = cand.booking != null ? ClassBookings.WhyNobody(map, game, prog) : ClassMiss.NobodyEligible;
                return false;
            }
            var attendees = new List<Pawn>();
            if (!FindVenueNow(map, pm, game, prog, out IntVec3 anchor, out int seats, out why, cohort, attendees, cand.booking?.anchor)) return false;
            Pawn teacher = FreeTeacher(map, pm, prog, out bool onSite);
            if (teacher == null)
            {
                why = onSite ? ClassMiss.TeacherBusy : ClassMiss.NoTeacher;
                return false;
            }
            bool referred = false;
            foreach (Pawn p in attendees) referred |= p.Inmate().referral == prog;
            // Two students make a class, unless the warden referred someone (then it runs for them alone).
            if (attendees.Count == 0 || attendees.Count < Mathf.Min(2, seats) && !referred)
            {
                why = ClassMiss.TooFew;
                return false;
            }
            if (!game.ledger.TrySpend(RPDefOf.RP_Programs, prog.costPerSession, prog.LabelCap))
            {
                why = ClassMiss.NoFunds;
                return false;
            }
            var session = new ProgramSession
            {
                program = prog,
                cost = prog.costPerSession,
                startTick = Find.TickManager.TicksGame,
                endTick = Find.TickManager.TicksGame + prog.sessionHours * GenDate.TicksPerHour,
                teacher = teacher,
                attendees = attendees,
                anchor = anchor,
            };
            pm.sessions.Add(session);
            TakeSeatsFromWork(map, pm, session);
            foreach (Pawn p in attendees)
            {
                taken.Add(p);
                pm.QueueRegimeInterrupt(p);
            }
            CallTeacher(teacher);
            Messages.Message("RP_SessionStarted".Translate(prog.label, attendees.Count), MessageTypeDefOf.SilentInput, historical: false);
            return true;
        }

        /// <summary>Completes every session whose time is up.</summary>
        public static void CompleteFinished(PrisonMap pm)
        {
            for (int i = pm.sessions.Count - 1; i >= 0; i--)
                if (!pm.sessions[i].Active) Complete(pm, pm.sessions[i]);
        }

        public static void Complete(PrisonMap pm, ProgramSession s)
        {
            pm.sessions.Remove(s);
            if (s?.program == null) return;
            // Credit only if the teacher actually taught for at least half the session.
            if (s.taughtTicks < (s.endTick - s.startTick) / 2)
            {
                if (s.cost > 0) PrisonGame.Instance.ledger.Add(RPDefOf.RP_Programs, s.cost, "RP_Refund".Translate());
                Messages.Message("RP_SessionNoTeacher".Translate(s.program.label), MessageTypeDefOf.NegativeEvent, historical: false);
                return;
            }
            int passed = 0;
            foreach (Pawn p in s.attendees)
            {
                // (Credit is for having sat through it: by the tick the class is closed, pawns, who tick first, have already left it.)
                if (!p.Spawned || p.Downed || p.InMentalState || !s.Attended(p) || p.Inmate() is not { } c) continue;
                c.programProgress.TryGetValue(s.program, out int done);
                c.programProgress[s.program] = ++done;
                CustodyContracts.Notify_Attended(p);
                c.tension = Mathf.Max(0f, c.tension - 5f);
                if (done < s.program.sessions) continue;
                c.programProgress.Remove(s.program);
                if (Rand.Chance(PassChance(p, s.program)))
                {
                    Pass(p, c, s.program);
                    if (!s.program.stage) passed++;
                }
                else
                {
                    Messages.Message("RP_ProgramFailed".Translate(p.LabelShort, s.program.label), p, MessageTypeDefOf.NeutralEvent, historical: false);
                    Wants.Notify_ProgramFailed(p, c, s.program);
                }
            }
            if (passed > 0) PrisonGame.Instance.stats.programsPassed += passed;
        }

        /// <summary>The chance this inmate passes the program's exam: its base, their skill, and what reading in their cell has done for the education programs.</summary>
        public static float PassChance(Pawn p, ProgramDef prog)
        {
            float chance = prog.passChance + (prog.examSkill != null ? prog.passChancePerSkillLevel * (p.skills?.GetSkill(prog.examSkill)?.Level ?? 0) : 0f);
            if (prog.literacyBonus > 0f) chance += prog.literacyBonus * LibrarySystem.LiteracyLevel(p);
            return Mathf.Clamp01(chance);
        }

        internal static void Pass(Pawn p, CompInmate c, ProgramDef prog)
        {
            c.passedPrograms.Add(prog);
            if (c.referral == prog) c.referral = null;
            if (!prog.stage)
            {
                c.programsPassed++;
                CellQuality.Notify_ProgramPassed(c);
            }
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_ProgramPassed".Translate(p.LabelShort, prog.label), p);
            foreach (ReputationDef r in prog.removesReputations) c.reputations.Remove(r);
            if (prog.curesAddictions)
            {
                var cured = new List<Hediff>();
                foreach (Hediff h in p.health.hediffSet.hediffs) if (h is Hediff_Addiction) cured.Add(h);
                foreach (Hediff h in cured) p.health.RemoveHediff(h);
            }
            if (prog.deepensFaith && FaithUtility.Need(p) is { } faith) faith.CurLevel = 1f;
            if (prog.skillXpOnPass > 0f && prog.examSkill != null) p.skills?.Learn(prog.examSkill, prog.skillXpOnPass, direct: true);
            if (prog.grantOnPass > 0) PrisonGame.Instance.ledger.Add(RPDefOf.RP_Grants, prog.grantOnPass, "RP_ProgramGrantNote".Translate(prog.label));
            Messages.Message(prog.grantOnPass > 0 ? "RP_ProgramPassed".Translate(p.LabelShort, prog.label, Money.Format(prog.grantOnPass)) : "RP_ProgramPassedNoGrant".Translate(p.LabelShort, prog.label),
                p, MessageTypeDefOf.PositiveEvent, historical: false);
            Wants.Notify_ProgramPassed(p, c, prog);
        }

        // ---- rooms and teachers ---------------------------------------------------------------------------------

        /// <summary>
        /// <see cref="TryFindVenue"/> (for these inmates) for a class about to start: not when the regime has other inmates in the program's zone right now (the canteen at a meal). With a <paramref name="booked"/> room only that one is tried.
        /// </summary>
        internal static bool TryFindVenueNow(Map map, PrisonMap pm, PrisonGame game, ProgramDef prog, out IntVec3 anchor, out int capacity, IReadOnlyList<Pawn> forWhom = null, List<Pawn> admitted = null, IntVec3? booked = null) =>
            FindVenueNow(map, pm, game, prog, out anchor, out capacity, out _, forWhom, admitted, booked);

        /// <summary><see cref="TryFindVenueNow"/>, and when there is no room, why (see <see cref="FindVenue"/>; the regime's own use of the zone is <see cref="ClassMiss.ZoneInUse"/>).</summary>
        internal static bool FindVenueNow(Map map, PrisonMap pm, PrisonGame game, ProgramDef prog, out IntVec3 anchor, out int capacity, out ClassMiss why, IReadOnlyList<Pawn> forWhom = null, List<Pawn> admitted = null, IntVec3? booked = null)
        {
            if (RegimeHoldsZone(map, game, prog.Zone))
            {
                anchor = IntVec3.Invalid;
                capacity = 0;
                why = ClassMiss.ZoneInUse;
                return false;
            }
            return FindVenue(map, pm, prog, out anchor, out capacity, out why, forWhom, admitted, booked);
        }

        /// <summary>Some inmate's regime activity right now takes place in the zone (eating, in the canteen): a class there would take the seats from them.</summary>
        internal static bool RegimeHoldsZone(Map map, PrisonGame game, PrisonZoneDef zone)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } && game.CurrentActivity(p).zones.Contains(zone)) return true;
            return false;
        }

        private static readonly HashSet<Pawn> reservers = new HashSet<Pawn>();

        /// <summary>
        /// The seat is taken: someone has it reserved who is not a worker whose seat a class may take (see <see cref="TakeSeatsFromWork"/>) and not an attendee of a class that is over (they leave it on their next tick).
        /// </summary>
        private static bool SeatTaken(Map map, Thing seat)
        {
            reservers.Clear();
            map.reservationManager.ReserversOf(seat, reservers);
            foreach (Pawn p in reservers)
                if (p.CurJobDef != RPLaborDefOf.RP_Sew && p.CurJobDef != RPProgramDefOf.RP_AttendClass) return true;
            return false;
        }

        /// <summary>A class that starts takes the seats of its room from the workers at them (the tailors at the benches the tailoring course sits at): their cycle is dropped unpaid.</summary>
        private static void TakeSeatsFromWork(Map map, PrisonMap pm, ProgramSession s)
        {
            foreach (ThingDef seat in s.program.Seats)
                foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(seat))
                {
                    if (!InVenue(map, pm, s, b.Position)) continue;
                    reservers.Clear();
                    map.reservationManager.ReserversOf(b, reservers);
                    foreach (Pawn p in reservers)
                        if (p.CurJobDef == RPLaborDefOf.RP_Sew) p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
        }

        /// <summary>The seat is in the room a running class holds, and the class sits at it: nobody starts work at it now.</summary>
        public static bool HeldByClass(Map map, PrisonMap pm, Thing seat)
        {
            foreach (ProgramSession s in pm.sessions)
                if (s.Active && s.program.Seats.Contains(seat.def) && InVenue(map, pm, s, seat.Position)) return true;
            return false;
        }

        /// <summary>
        /// The free room of the program's zone that seats the most: a cell of it and the number of places (the seats nobody has reserved). Busy: there are
        /// such rooms but every one holds another session. Rooms are told apart by the map's rooms, so two classrooms
        /// behind two doors seat two classes. A room with seats (or cells) in patches kept for some levels (see <see cref="ZoneGrid.SetKept"/>) admits only an inmate whose level may use
        /// every one of those patches, so no seat needs assigning: any seat of the room is theirs. With inmates given (<paramref name="forWhom"/>) the room that admits the most of them
        /// is the one (then the most places), and <paramref name="admitted"/> receives who it admits, as many as it has places; a room admitting none of them is passed over. Without them a
        /// room counts when some inmate living in the prison is admitted. Nothing kept: the room with the most places.
        /// </summary>
        public static bool TryFindVenue(Map map, PrisonMap pm, ProgramDef prog, out IntVec3 anchor, out int capacity, out bool busy, IReadOnlyList<Pawn> forWhom = null, List<Pawn> admitted = null)
        {
            bool found = FindVenue(map, pm, prog, out anchor, out capacity, out ClassMiss why, forWhom, admitted);
            busy = why == ClassMiss.VenueBusy;
            return found;
        }

        /// <summary>
        /// <see cref="TryFindVenue"/>, and when there is none, why: no room of the zone with seats at all (<see cref="ClassMiss.NoRoom"/>), the booked room is not one any more (<see cref="ClassMiss.VenueGone"/>: rebuilt,
        /// repainted, its seats gone), rooms that admit none of the inmates (<see cref="ClassMiss.VenueClosed"/>), or every one holds another class (<see cref="ClassMiss.VenueBusy"/>). With a <paramref name="booked"/> cell, the
        /// room it is in is the only one tried.
        /// </summary>
        internal static bool FindVenue(Map map, PrisonMap pm, ProgramDef prog, out IntVec3 anchor, out int capacity, out ClassMiss fail, IReadOnlyList<Pawn> forWhom = null, List<Pawn> admitted = null, IntVec3? booked = null)
        {
            anchor = IntVec3.Invalid;
            capacity = 0;
            fail = ClassMiss.None;
            // (Of two inmates the warden keeps apart, the one who goes first today has the seat: the other waits for another class.)
            forWhom = KeepApart.Cohort(forWhom);
            List<ThingDef> seats = prog.Seats;
            admitted?.Clear();
            Room only = null;
            if (booked is { IsValid: true } at)
            {
                only = at.InBounds(map) ? at.GetRoom(map) : null;
                if (only == null)
                {
                    fail = ClassMiss.VenueGone;
                    return false;
                }
            }
            FillVenues(map, pm, prog, free: true, only, out int seatsTaken);
            PrisonZoneDef zone = prog.Zone;
            HashSet<SecurityLevelDef> living = forWhom == null && venueKept.Count > 0 ? livingLevels.Get(map.uniqueID, Find.TickManager.TicksGame, () => KeptRooms.LevelsInPrison(map)) : null;
            bool claimed = false;
            int bestTook = 0, bestRoom = -1, considered = 0;
            for (int i = 0; i < venueRooms.Count; i++)
            {
                if (only != null && venueRooms[i] != only) continue;
                considered++;
                int places = seats.Count > 0 ? venueCounts[i] : Mathf.Clamp(venueCounts[i] / StandingCellsPerAttendee, 1, MaxStanding);
                int took = Admits(pm, i, places, forWhom, living, null);
                if (took == 0) continue;
                if (Claimed(map, pm, zone, venueRooms[i]))
                {
                    claimed = true;
                    continue;
                }
                if (took > bestTook || took == bestTook && places > capacity)
                {
                    bestTook = took;
                    capacity = places;
                    anchor = venueAnchors[i];
                    bestRoom = i;
                }
            }
            if (bestRoom >= 0) Admits(pm, bestRoom, capacity, forWhom, living, admitted);
            if (capacity == 0)
            {
                // (A booked room whose every seat has someone at it is busy, not gone.)
                fail = claimed || only != null && considered == 0 && seatsTaken > 0 ? ClassMiss.VenueBusy : considered == 0 ? only != null ? ClassMiss.VenueGone : ClassMiss.NoRoom : ClassMiss.VenueClosed;
            }
            return capacity > 0;
        }

        /// <summary>
        /// Counts the program zone's seats (or, for a class that stands, its cells) by room into the venue scratch lists. With <paramref name="free"/> a seat somebody has reserved is not counted (and
        /// <paramref name="takenIn"/> counts those in the <paramref name="watch"/> room); without it every seat is, the room as built (the Programs screen's view of it).
        /// </summary>
        private static void FillVenues(Map map, PrisonMap pm, ProgramDef prog, bool free, Room watch, out int takenIn)
        {
            takenIn = 0;
            PrisonZoneDef zone = prog.Zone;
            List<ThingDef> seats = prog.Seats;
            venueRooms.Clear();
            venueCounts.Clear();
            venueAnchors.Clear();
            venueKept.Clear();
            if (seats.Count > 0)
            {
                foreach (ThingDef seat in seats)
                    foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(seat))
                    {
                        if (pm.zones.ZoneAt(b.Position) != zone) continue;
                        if (free && SeatTaken(map, b))
                        {
                            if (watch != null && b.Position.GetRoom(map) == watch) takenIn++;
                            continue;
                        }
                        CountInRoom(pm, b.Position.GetRoom(map), b.Position, SeatCell(b));
                    }
            }
            else
            {
                foreach (IntVec3 c in pm.zones.CellsOf(zone))
                    if (c.Standable(map)) CountInRoom(pm, c.GetRoom(map), c, c);
            }
        }

        /// <summary>The rooms of the program's zone that hold its seats, as built: a cell of each and its places (what a booking is made against, see <see cref="ClassBooking.anchor"/>).</summary>
        public static void ListVenues(Map map, PrisonMap pm, ProgramDef prog, List<(IntVec3 anchor, int places)> into)
        {
            into.Clear();
            FillVenues(map, pm, prog, free: false, null, out _);
            for (int i = 0; i < venueRooms.Count; i++)
                into.Add((venueAnchors[i], prog.Seats.Count > 0 ? venueCounts[i] : Mathf.Clamp(venueCounts[i] / StandingCellsPerAttendee, 1, MaxStanding)));
        }

        /// <summary>
        /// For the Programs screen: how many of these inmates the program's room admits, as built (the room the cell is in, or the best of the zone's rooms; not whether a class is in it now), and its places. Exists is false when
        /// the cell is in no room of the zone that holds the program's seats (the booked room is gone).
        /// </summary>
        public static int VenueAdmits(Map map, PrisonMap pm, ProgramDef prog, IntVec3? booked, IReadOnlyList<Pawn> forWhom, out int places, out bool exists)
        {
            places = 0;
            exists = false;
            forWhom ??= System.Array.Empty<Pawn>();
            Room only = booked is { IsValid: true } at && at.InBounds(map) ? at.GetRoom(map) : null;
            FillVenues(map, pm, prog, free: false, null, out _);
            int best = 0;
            for (int i = 0; i < venueRooms.Count; i++)
            {
                if (booked is { IsValid: true } && venueRooms[i] != only) continue;
                exists = true;
                int here = prog.Seats.Count > 0 ? venueCounts[i] : Mathf.Clamp(venueCounts[i] / StandingCellsPerAttendee, 1, MaxStanding);
                int took = Admits(pm, i, here, forWhom, null, null);
                if (took > best || took == best && here > places)
                {
                    best = took;
                    places = here;
                }
            }
            return best;
        }

        private static readonly KeptRooms.PerMapTickCache<HashSet<SecurityLevelDef>> livingLevels = new KeptRooms.PerMapTickCache<HashSet<SecurityLevelDef>>();

        /// <summary>
        /// How many of the inmates given the room admits, at most <paramref name="places"/> (they are added to <paramref name="admitted"/> when given): all of them with no kept patch in the room, else those allowed
        /// in every one. With none given, 1 when the room admits some inmate living in the prison (or none live there), else 0.
        /// </summary>
        private static int Admits(PrisonMap pm, int room, int places, IReadOnlyList<Pawn> forWhom, HashSet<SecurityLevelDef> living, List<Pawn> admitted)
        {
            bool anyKept = false;
            foreach ((int room, int patch) kp in venueKept)
                if (kp.room == room) { anyKept = true; break; }
            if (forWhom == null)
            {
                if (!anyKept || living.Count == 0) return 1;
                foreach (SecurityLevelDef l in living)
                    if (AllowedInEvery(pm, room, l)) return 1;
                return 0;
            }
            int took = 0;
            for (int i = 0; i < forWhom.Count && took < places; i++)
            {
                if (anyKept && !AllowedInEvery(pm, room, KeptRooms.LevelOf(forWhom[i]))) continue;
                took++;
                admitted?.Add(forWhom[i]);
            }
            return took;
        }

        private static bool AllowedInEvery(PrisonMap pm, int room, SecurityLevelDef level)
        {
            foreach ((int room, int patch) kp in venueKept)
                if (kp.room == room && !pm.zones.Allows(kp.patch, level)) return false;
            return true;
        }

        /// <summary>Counts a seat or a cell in its room; <paramref name="useCell"/> is the cell it is used from, whose patch (when kept for some levels) the room's admission follows.</summary>
        private static void CountInRoom(PrisonMap pm, Room room, IntVec3 cell, IntVec3 useCell)
        {
            if (room == null) return;
            int i = venueRooms.IndexOf(room);
            if (i < 0)
            {
                venueRooms.Add(room);
                venueCounts.Add(0);
                venueAnchors.Add(cell);
                i = venueRooms.Count - 1;
            }
            venueCounts[i]++;
            if (!pm.zones.AnyKept) return;
            int patch = pm.zones.PatchAt(useCell);
            if (patch >= 0 && pm.zones.KeptFor(patch) != null) venueKept.Add((i, patch));
        }

        private static bool Claimed(Map map, PrisonMap pm, PrisonZoneDef zone, Room room)
        {
            foreach (ProgramSession s in pm.sessions)
                if (s.Active && s.program.Zone == zone && s.anchor.IsValid && s.anchor.GetRoom(map) == room) return true;
            return false;
        }

        /// <summary>The cell is in the room the session holds, in the program's zone (a session from an old save holds the whole zone).</summary>
        public static bool InVenue(Map map, PrisonMap pm, ProgramSession s, IntVec3 cell)
        {
            if (pm.zones.ZoneAt(cell) != s.program.Zone) return false;
            if (!s.anchor.IsValid) return true;
            Room room = s.anchor.GetRoom(map);
            return room != null && cell.GetRoom(map) == room;
        }

        /// <summary>The work givers that run a class (one per teaching role, in that role's own work type).</summary>
        private static List<WorkGiverDef> teachGivers;
        private static List<WorkGiverDef> TeachGivers => teachGivers ??= DefDatabase<WorkGiverDef>.AllDefsListForReading.Where(d => d.giverClass == typeof(WorkGiver_TeachClass)).ToList();

        /// <summary>Whether this work type is one a class is taught through (staff keep it at the top priority, so a class starts).</summary>
        public static bool IsTeachWorkType(WorkTypeDef w)
        {
            foreach (WorkGiverDef d in TeachGivers) if (d.workType == w) return true;
            return false;
        }

        /// <summary>The role's teaching work type is on in the pawn's Work tab (and not ruled out by their backstory): without it their work scan never reaches the class.</summary>
        public static bool TeachWorkActive(Pawn p, StaffRoleDef role)
        {
            if (p.workSettings is not { EverWork: true } settings) return false;
            foreach (WorkGiverDef d in TeachGivers)
                if (role.workTypes.Contains(d.workType) && !p.WorkTypeIsDisabled(d.workType) && settings.WorkIsActive(d.workType)) return true;
            return false;
        }

        public static bool CanTeach(Pawn p, StaffRoleDef role) =>
            p.GetComp<CompStaff>() is { } sc && sc.role == role && !p.Downed && !StaffLives.OnLeave(sc) && !CorruptionSystem.Suspended(sc) && TeachWorkActive(p, role);

        private static JobDef[] constructionJobs;

        /// <summary>
        /// Whether the pawn's current job can be ended to start a class: nothing, anything casually interruptible, the services,
        /// therapy and construction they would otherwise finish first, and a class of their own. Not what the player ordered, nor
        /// meals, sleep, medicine or a fight.
        /// </summary>
        public static bool CanInterrupt(Pawn p)
        {
            if (p.Downed || p.Drafted || p.InMentalState) return false;
            Job job = p.CurJob;
            if (job == null) return true;
            if (job.playerForced) return false;
            JobDef d = job.def;
            constructionJobs ??= new[] { JobDefOf.FinishFrame, JobDefOf.PlaceNoCostFrame, JobDefOf.Deconstruct, JobDefOf.DeconstructForBlueprint, JobDefOf.Uninstall, JobDefOf.Repair, JobDefOf.RemoveFloor, JobDefOf.BuildRoof };
            // (A counsellor sitting in on a restorative meeting may go to a class while the inmate has not sat down with the family yet.)
            return d.casualInterruptible || d == RPProgramDefOf.RP_TeachClass || d == RPFaithDefOf.RP_LeadService || d == RPPsychDefOf.RP_Therapy || System.Array.IndexOf(constructionJobs, d) >= 0
                   || d == RPAmendsDefOf.RP_AttendAmends && Amends.MayLeaveTable(job);
        }

        /// <summary>For a check that starts many classes of one teacher in a frame: the teacher is not called (each call starts a job, and vanilla logs an error at the tenth in a frame).</summary>
        internal static bool CallTeacherOffForTests;

        /// <summary>The teacher drops what they are doing, when that can wait: their work scan then reaches the class first.</summary>
        private static void CallTeacher(Pawn teacher)
        {
            if (CallTeacherOffForTests) return;
            if (teacher.Spawned && teacher.jobs != null && teacher.CurJob != null && CanInterrupt(teacher)) teacher.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        /// <summary>
        /// A teacher of one of the program's roles who is on site, up, has the teaching work on, and is not teaching another session;
        /// of those the most preferred role wins, but one who cannot leave what they are doing (a meal, an order) gives way to a free
        /// one of a lesser role. OnSite: there is one at all.
        /// </summary>
        public static Pawn FreeTeacher(Map map, PrisonMap pm, ProgramDef prog, out bool onSite)
        {
            onSite = false;
            Pawn free = null;
            int freeRank = int.MaxValue;
            bool freeBusy = true;
            List<StaffRoleDef> roles = prog.TeacherRoles;
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                StaffRoleDef role = colonists[i].GetComp<CompStaff>()?.role;
                int rank = role == null ? -1 : roles.IndexOf(role);
                if (rank < 0 || !CanTeach(colonists[i], role)) continue;
                onSite = true;
                if (TaughtBy(colonists[i], pm) != null) continue;
                bool busy = !CanInterrupt(colonists[i]);
                if (free == null || (freeBusy && !busy) || (freeBusy == busy && rank < freeRank))
                {
                    free = colonists[i];
                    freeRank = rank;
                    freeBusy = busy;
                }
            }
            return free;
        }

        public static ProgramSession SessionFor(PrisonMap pm, ProgramDef prog)
        {
            foreach (ProgramSession s in pm.sessions)
                if (s.program == prog && s.Active) return s;
            return null;
        }

        /// <summary>The active session the inmate is enrolled in.</summary>
        public static ProgramSession SessionOf(Pawn p, PrisonMap pm)
        {
            List<ProgramSession> list = pm.sessions;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Active && list[i].attendees.Contains(p)) return list[i];
            return null;
        }

        /// <summary>The active session this person is teaching.</summary>
        public static ProgramSession TaughtBy(Pawn teacher, PrisonMap pm)
        {
            List<ProgramSession> list = pm.sessions;
            for (int i = 0; i < list.Count; i++)
                if (list[i].teacher == teacher && list[i].Active) return list[i];
            return null;
        }

        /// <summary>The session this member of staff should teach: the one they have, else one their role teaches whose teacher is not at it.</summary>
        public static ProgramSession SessionToTeach(Pawn pawn, PrisonMap pm)
        {
            StaffRoleDef role = pawn.GetComp<CompStaff>()?.role;
            // (A teacher who can no longer teach, suspended or with the work switched off, is not offered their own class either: it is not
            // theirs to nudge every 250 ticks, and a stand-in is tried.)
            if (role == null || !CanTeach(pawn, role)) return null;
            if (TaughtBy(pawn, pm) is { } own) return own;
            List<ProgramSession> list = pm.sessions;
            for (int i = 0; i < list.Count; i++)
            {
                ProgramSession s = list[i];
                if (!s.Active || !s.program.TeacherRoles.Contains(role)) continue;
                // A teacher who is at it keeps it; one who has not started, or wandered off, can be replaced (a preferred one is given a moment to arrive).
                Pawn t = s.teacher;
                if (t == null || !t.Spawned || t.Downed || t.CurJobDef != RPProgramDefOf.RP_TeachClass)
                {
                    if (!HeldByPreferred(s, role)) return s;
                }
            }
            return null;
        }

        /// <summary>
        /// Every 250 ticks: a class whose teacher is not on it yet is offered to whoever may take it (the assigned teacher, then a
        /// stand-in once the grace is over or the assigned one is held up), who drops what they are doing. The work scan does the rest.
        /// </summary>
        public static void NudgeTeachers(Map map, PrisonMap pm)
        {
            for (int i = 0; i < pm.sessions.Count; i++)
            {
                ProgramSession s = pm.sessions[i];
                if (!s.Active || s.program == null || s.teacher is { } t && t.CurJobDef == RPProgramDefOf.RP_TeachClass) continue;
                // The assigned teacher first; a stand-in only when they cannot leave what they are doing.
                if (s.teacher != null && s.teacher.Spawned && SessionToTeach(s.teacher, pm) == s && CanInterrupt(s.teacher))
                {
                    if (TeacherSpot(s.teacher, pm, s).IsValid) CallTeacher(s.teacher);
                    continue;
                }
                IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
                for (int k = 0; k < colonists.Count; k++)
                {
                    Pawn standIn = colonists[k];
                    if (standIn == s.teacher || SessionToTeach(standIn, pm) != s || !CanInterrupt(standIn) || !TeacherSpot(standIn, pm, s).IsValid) continue;
                    CallTeacher(standIn);
                    break;
                }
            }
        }

        /// <summary>The session's teacher is of a more preferred role than <paramref name="role"/>, can teach, is free to come, and the session is new.</summary>
        private static bool HeldByPreferred(ProgramSession s, StaffRoleDef role)
        {
            StaffRoleDef assigned = s.teacher?.GetComp<CompStaff>()?.role;
            if (assigned == null || !CanTeach(s.teacher, assigned) || !s.teacher.Spawned) return false;
            List<StaffRoleDef> roles = s.program.TeacherRoles;
            int rank = roles.IndexOf(assigned);
            // (A preferred teacher who cannot leave what they are doing is not on their way: the stand-in takes over at once.)
            return rank >= 0 && rank < roles.IndexOf(role) && Find.TickManager.TicksGame - s.startTick < PreferredGraceTicks && CanInterrupt(s.teacher);
        }

        /// <summary>
        /// The standable cells of the room the session holds that are in the program's zone (the whole zone for a session from an old
        /// save). Sampling these, not the zone, is what lands every try in the right room when several rooms share a zone.
        /// </summary>
        private static List<IntVec3> VenueCells(Map map, PrisonMap pm, ProgramSession s)
        {
            venueCells.Clear();
            PrisonZoneDef zone = s.program.Zone;
            List<IntVec3> zoneCells = pm.zones.CellsOf(zone);
            Room room = s.anchor.IsValid ? s.anchor.GetRoom(map) : null;
            // Whichever is smaller is walked (an open-air "room" holds the whole map).
            if (room != null && room.CellCount < zoneCells.Count)
            {
                foreach (IntVec3 c in room.Cells)
                    if (pm.zones.ZoneAt(c) == zone && c.Standable(map)) venueCells.Add(c);
            }
            else
            {
                for (int i = 0; i < zoneCells.Count; i++)
                    if (zoneCells[i].Standable(map) && InVenue(map, pm, s, zoneCells[i])) venueCells.Add(zoneCells[i]);
            }
            return venueCells;
        }

        public static IntVec3 TeacherSpot(Pawn pawn, PrisonMap pm, ProgramSession s)
        {
            List<IntVec3> cells = VenueCells(pawn.Map, pm, s);
            for (int i = 0; i < 16 && cells.Count > 0; i++)
            {
                IntVec3 c = cells.RandomElement();
                if (pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) return c;
            }
            return IntVec3.Invalid;
        }

        // ---- attendance -----------------------------------------------------------------------------------------

        /// <summary>The cell the attendee uses a seat from (a desk's interaction cell, a chair's own cell): that is the cell the kept-rooms rule asks about, not the building's.</summary>
        public static IntVec3 SeatCell(Thing seat) => seat.def.hasInteractionCell ? seat.InteractionCell : seat.Position;

        public static PathEndMode SeatMode(Thing seat) => seat.def.hasInteractionCell ? PathEndMode.InteractionCell : PathEndMode.OnCell;

        /// <summary>Go to a seat (or a place to stand) in the room of the inmate's own session; wander the zone if all are taken.</summary>
        public static Job AttendJob(Pawn p, ProgramSession s, PrisonMap pm)
        {
            Map map = p.Map;
            Job job = null;
            List<ThingDef> seats = s.program.Seats;
            if (seats.Count > 0)
            {
                foreach (ThingDef seat in seats)
                {
                    foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(seat))
                    {
                        if (!InVenue(map, pm, s, b.Position) || !KeptRooms.Allows(pm, SeatCell(b), p) || !p.CanReserveAndReach(b, SeatMode(b), Danger.Some)) continue;
                        job = JobMaker.MakeJob(RPProgramDefOf.RP_AttendClass, b);
                        break;
                    }
                    if (job != null) break;
                }
            }
            else
            {
                List<IntVec3> cells = VenueCells(map, pm, s);
                for (int i = 0; i < 16 && cells.Count > 0 && job == null; i++)
                {
                    IntVec3 c = cells.RandomElement();
                    if (KeptRooms.Allows(pm, c, p) && p.CanReserveAndReach(c, PathEndMode.OnCell, Danger.Some)) job = JobMaker.MakeJob(RPProgramDefOf.RP_AttendClass, c);
                }
            }
            if (job == null) return RegimeActivityWorker.WanderInZones(p, pm, new List<PrisonZoneDef> { s.program.Zone });
            job.expiryInterval = s.endTick - Find.TickManager.TicksGame;
            return job;
        }

        // ---- the Programs screen --------------------------------------------------------------------------------

        /// <summary>Inmates eligible for the program, who passed it, and who the warden referred (and is still to take it).</summary>
        public static void Count(Map map, ProgramDef prog, out int eligible, out int graduated, out int referred)
        {
            eligible = graduated = referred = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = p.Inmate();
                if (c == null) continue;
                if (c.passedPrograms.Contains(prog)) graduated++;
                else if (prog.Eligible(p, c)) eligible++;
                if (c.activeFlag && c.referral == prog) referred++;
            }
        }

        /// <summary>Inmates the program would take but for their suppression (see <see cref="ProgramDef.ShutOut"/>).</summary>
        public static int ShutOutCount(Map map, ProgramDef prog)
        {
            int n = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } c && prog.ShutOut(p, c)) n++;
            return n;
        }

        /// <summary>The line for a class with one student and no referral: that a class needs two, and when more would attend but for their suppression, that they are held down.</summary>
        public static string OneStudentStatus(Map map, ProgramDef prog)
        {
            int shut = ShutOutCount(map, prog);
            return shut > 0 ? "RP_ProgramStatusSuppressedOne".Translate(shut, Suppression.Line.ToString("F0")) : "RP_ProgramStatusOne".Translate();
        }

        /// <summary>Why a program with eligible inmates cannot start now: no room with seats, no teacher of its roles on site, or both taken by other classes.</summary>
        public static ProgramBlock Blocker(Map map, PrisonMap pm, ProgramDef prog)
        {
            bool found = TryFindVenue(map, pm, prog, out _, out _, out bool busy);
            if (!found && !busy) return ProgramBlock.NoRoom;
            if (found && RegimeHoldsZone(map, PrisonGame.Instance, prog.Zone)) found = false;
            Pawn teacher = FreeTeacher(map, pm, prog, out bool onSite);
            if (!onSite) return ProgramBlock.NoTeacher;
            if (!found) return ProgramBlock.RoomBusy;
            return teacher == null ? ProgramBlock.TeacherBusy : ProgramBlock.None;
        }

        /// <summary>One line for the Programs screen: where it runs and with whom, or why it does not.</summary>
        public static string Status(Map map, PrisonMap pm, PrisonGame game, ProgramDef prog, int eligible, int referred, out ProgramStatusKind kind)
        {
            kind = ProgramStatusKind.Blocked;
            if (SessionFor(pm, prog) is { } s)
            {
                string left = ((s.endTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour).ToString("0.0");
                // The teacher is named in either line; "waiting" while they are not on the teach job (busy, on their way, or nobody assigned).
                if (s.teacher == null || s.teacher.CurJobDef != RPProgramDefOf.RP_TeachClass)
                {
                    kind = ProgramStatusKind.Waiting;
                    return "RP_ProgramStatusWaitingTeacher".Translate(s.teacher?.LabelShort ?? prog.TeacherLabel, prog.Zone.label, s.attendees.Count, left);
                }
                kind = ProgramStatusKind.Running;
                return "RP_ProgramStatusInSession".Translate(prog.Zone.label, s.teacher.LabelShort, s.attendees.Count, left);
            }
            if (!game.programs.IsRunning(prog)) return "RP_ProgramStatusOff".Translate();
            if (eligible == 0)
            {
                // (Nobody eligible because those who would be are held down: the blocker is the suppression, not the class.)
                int shut = ShutOutCount(map, prog);
                return shut > 0 ? "RP_ProgramStatusSuppressed".Translate(shut, Suppression.Line.ToString("F0")) : "RP_ProgramStatusNobody".Translate();
            }
            switch (Blocker(map, pm, prog))
            {
                case ProgramBlock.NoRoom:
                    return prog.Seats.Count > 0 ? "RP_ProgramStatusNoRoomSeats".Translate(prog.Zone.label, prog.SeatLabel) : "RP_ProgramStatusNoRoomStanding".Translate(prog.Zone.label);
                case ProgramBlock.NoTeacher:
                    return "RP_ProgramStatusNoTeacher".Translate(prog.TeacherLabel);
                case ProgramBlock.RoomBusy:
                    kind = ProgramStatusKind.Waiting;
                    return "RP_ProgramStatusRoomBusy".Translate(prog.Zone.label);
                case ProgramBlock.TeacherBusy:
                    kind = ProgramStatusKind.Waiting;
                    return "RP_ProgramStatusTeacherBusy".Translate(prog.TeacherLabel);
            }
            kind = ProgramStatusKind.Waiting;
            if (eligible == 1 && referred == 0) return OneStudentStatus(map, prog);
            return "RP_ProgramStatusWaiting".Translate(prog.Slot.label);
        }
    }

    /// <summary>Which programs the warden runs.</summary>
    public class ProgramPolicy : IExposable
    {
        public List<ProgramDef> disabled = new List<ProgramDef>();
        /// <summary>Hours a program with someone referred to it has been kept from starting (its priority ages with them).</summary>
        public Dictionary<ProgramDef, int> waitingHours = new Dictionary<ProgramDef, int>();

        /// <summary>Hours of waiting after which a referral that only waits for a free room or teacher raises the referral alert.</summary>
        public const int AlertAfterHours = 24;

        public bool IsRunning(ProgramDef p) => !disabled.Contains(p);

        public int WaitedHours(ProgramDef p) => p != null && waitingHours.TryGetValue(p, out int h) ? h : 0;

        /// <summary>One more hour for each of these (referred, not started); everyone else starts again from nothing.</summary>
        public void NoteWaiting(List<ProgramDef> kept)
        {
            waitingHours.RemoveAll(kv => !kept.Contains(kv.Key));
            foreach (ProgramDef p in kept) waitingHours[p] = WaitedHours(p) + 1;
        }

        private List<ProgramDef> waitKeys;
        private List<int> waitValues;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref disabled, "disabled", LookMode.Def);
            Scribe_Collections.Look(ref waitingHours, "waitingHours", LookMode.Def, LookMode.Value, ref waitKeys, ref waitValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                disabled ??= new List<ProgramDef>();
                disabled.RemoveAll(d => d == null);
                waitingHours ??= new Dictionary<ProgramDef, int>();
                waitingHours.RemoveAll(kv => kv.Key == null);
            }
        }
    }

    public class JobDriver_AttendClass : JobDriver
    {
        /// <summary>The session found for them (a session's attendees never change, so it stays theirs for as long as it runs); looked up again after a load.</summary>
        private ProgramSession session;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // The seat is a thing (a desk, a chair) or, for a class that stands, a cell.
            if (job.targetA.HasThing)
            {
                this.FailOnDespawnedOrNull(TargetIndex.A);
                yield return Toils_Goto.GotoThing(TargetIndex.A, ProgramSystem.SeatMode(job.targetA.Thing));
            }
            else yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil listen = ToilMaker.MakeToil("Listen");
            listen.defaultCompleteMode = ToilCompleteMode.Never;
            listen.handlingFacing = true;
            listen.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(job.targetA);
                PrisonMap pm = PrisonMap.Of(pawn.Map);
                ProgramSession s = session;
                if (s == null || !s.Active || pm == null || !pm.sessions.Contains(s)) session = s = pm == null ? null : ProgramSystem.SessionOf(pawn, pm);
                if (s == null)
                {
                    ReadyForNextToil();
                    return;
                }
                s.listenedTicks.TryGetValue(pawn, out int listened);
                s.listenedTicks[pawn] = listened + delta;
                if (s.program.examSkill != null) pawn.skills?.Learn(s.program.examSkill, 0.03f * delta);
                if (s.program.deepensFaith) FaithUtility.Attend(pawn, s, delta);
            };
            yield return listen;
        }
    }

    /// <summary>Any teacher runs their role's active session from the front of its room.</summary>
    public class WorkGiver_TeachClass : WorkGiver
    {
        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            return pm == null || ProgramSystem.SessionToTeach(pawn, pm) == null;
        }

        public override Job NonScanJob(Pawn pawn)
        {
            PrisonMap pm = PrisonMap.Of(pawn.Map);
            ProgramSession s = pm == null ? null : ProgramSystem.SessionToTeach(pawn, pm);
            if (s == null) return null;
            IntVec3 spot = ProgramSystem.TeacherSpot(pawn, pm, s);
            if (!spot.IsValid) return null;
            s.teacher = pawn;
            Job job = JobMaker.MakeJob(RPProgramDefOf.RP_TeachClass, spot);
            job.expiryInterval = Mathf.Max(200, s.endTick - Find.TickManager.TicksGame);
            return job;
        }
    }

    public class JobDriver_TeachClass : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil teach = ToilMaker.MakeToil("Teach");
            teach.defaultCompleteMode = ToilCompleteMode.Never;
            teach.tickIntervalAction = delta =>
            {
                PrisonMap pm = PrisonMap.Of(pawn.Map);
                // Over, or someone else has taken the class over: the teacher is free.
                ProgramSession s = pm == null ? null : ProgramSystem.TaughtBy(pawn, pm);
                if (s == null)
                {
                    ReadyForNextToil();
                    return;
                }
                // The class they were sent to is not this session's room (a new session of another program): back to the work scan for the right spot.
                if (job.targetA.Cell is { IsValid: true } spot && !ProgramSystem.InVenue(pawn.Map, pm, s, spot))
                {
                    ReadyForNextToil();
                    return;
                }
                pawn.skills?.Learn(SkillDefOf.Social, 0.05f * delta);
                if (pm.zones.ZoneAt(pawn.Position) == s.program.Zone) s.taughtTicks += delta;
                if (pawn.IsHashIntervalTick(300, delta) && s.attendees.Count > 0)
                    MoteMaker.MakeInteractionBubble(pawn, s.attendees.RandomElement(), ThingDefOf.Mote_Speech, InteractionDefOf.Chitchat.GetSymbol());
            };
            yield return teach;
        }
    }
}
