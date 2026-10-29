using System.Collections.Generic;
using System.Text;
using Verse;

namespace RimPrison
{
    /// <summary>Why a booked class did not run in its hour (None: it did). Saved by name in the booking's history.</summary>
    public enum ClassMiss
    {
        None, Stopped, NoTeacher, TeacherBusy, NoRoom, VenueGone, VenueClosed, VenueBusy, ZoneInUse, NoSlot, NobodyEligible, TooFew, NoFunds,
    }

    /// <summary>One hour a booking did not run in, and why.</summary>
    public class ClassMissRecord : IExposable
    {
        public int tick;
        public int hour;
        public ClassMiss reason;

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Values.Look(ref hour, "hour");
            Scribe_Values.Look(ref reason, "reason");
        }
    }

    /// <summary>
    /// A class reserved for some hours of the day (see <see cref="ClassBookings"/>): a program, the hours, optionally a room, and how it has fared. It never charges or credits anything: only the session it starts does.
    /// </summary>
    public class ClassBooking : IExposable
    {
        public int id;
        public ProgramDef program;
        /// <summary>Bit h set: the class is booked for the hour h:00 (every day).</summary>
        public int hours;
        /// <summary>A cell of the room it is booked in (a seat's, for a class that sits), invalid for any room of the program's zone. Resolved to a room each hour: a room that is not one any more is a miss, not an error.</summary>
        public IntVec3 anchor = IntVec3.Invalid;
        /// <summary>Of two booked classes that want the same room or teacher in an hour, the higher goes first (then the older booking).</summary>
        public int priority;
        /// <summary>Hours in a row it did not run (the Programs screen warns at <see cref="ClassBookings.WarnAfter"/>; nothing cancels it).</summary>
        public int missStreak;
        /// <summary>The game hour (ticks / 2500) the booking was last taken up in: an hour is handled once, however the pass is repeated or the game reloaded.</summary>
        public int handledStamp = -1;
        /// <summary>The last <see cref="ClassBookings.MaxMisses"/> hours it did not run in, oldest first.</summary>
        public List<ClassMissRecord> misses = new List<ClassMissRecord>();

        public bool Covers(int hour) => hour >= 0 && hour < 24 && (hours >> hour & 1) != 0;

        public ClassMissRecord LastMiss => misses.Count > 0 ? misses[misses.Count - 1] : null;

        public bool Warns => missStreak >= ClassBookings.WarnAfter;

        public int HourCount
        {
            get
            {
                int n = 0;
                for (int h = 0; h < 24; h++)
                    if (Covers(h)) n++;
                return n;
            }
        }

        internal void NoteRan() => missStreak = 0;

        internal void NoteMissed(int hour, ClassMiss reason)
        {
            missStreak++;
            misses.Add(new ClassMissRecord { tick = Find.TickManager.TicksGame, hour = hour, reason = reason });
            if (misses.Count > ClassBookings.MaxMisses) misses.RemoveRange(0, misses.Count - ClassBookings.MaxMisses);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Defs.Look(ref program, "program");
            Scribe_Values.Look(ref hours, "hours");
            Scribe_Values.Look(ref anchor, "anchor", IntVec3.Invalid);
            Scribe_Values.Look(ref priority, "priority");
            Scribe_Values.Look(ref missStreak, "missStreak");
            Scribe_Values.Look(ref handledStamp, "handledStamp", -1);
            Scribe_Collections.Look(ref misses, "misses", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                misses ??= new List<ClassMissRecord>();
                misses.RemoveAll(m => m == null);
                hours &= ClassBookings.AllHours;
            }
        }
    }

    /// <summary>What the Programs screen shows for one hour of a program: who could attend it and what the others do, and the other booked classes that want the same room or teacher.</summary>
    public struct ClassHourInfo
    {
        /// <summary>Inmates the program would take (eligible, active), and those of them whose regime has its slot in this hour.</summary>
        public int eligible, attendable;
        /// <summary>What most of those who are not in its slot do in this hour (a meal, lockup, sleep...).</summary>
        public RegimeActivityDef instead;
        /// <summary>Another program booked in this hour that uses the same teacher role or zone (the higher priority is served first).</summary>
        public ProgramDef clash;
    }

    /// <summary>
    /// The class bookings of a map (Programs tab): classes the warden reserves hours for, so that a scarce room and teacher go to the class chosen and not the fullest. At an hour's turn the booked classes are tried
    /// before any other (<see cref="ProgramSystem.HourlyTick"/>), each in its booked room; one that cannot run is a miss with a reason, never a refund or a charge, and nothing is cancelled for it. Whoever
    /// attends, what it costs and what it credits are the session's, as for any class. A program has at most one booking for an hour.
    /// </summary>
    public class ClassBookings : IExposable
    {
        public const int MaxMisses = 5, WarnAfter = 3, AllHours = (1 << 24) - 1;

        public List<ClassBooking> list = new List<ClassBooking>();
        public int nextId = 1;
        /// <summary>Moves with every edit (not saved): what a screen has worked out from the bookings is worked out again when it changes.</summary>
        public int version;

        public static ClassBookings Of(Map map) => PrisonMap.Of(map)?.bookings;

        /// <summary>The hours of the program that a booking other than <paramref name="except"/> holds.</summary>
        public int TakenHours(ProgramDef prog, ClassBooking except = null)
        {
            int taken = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i].program == prog && list[i] != except) taken |= list[i].hours;
            return taken;
        }

        public bool Any(ProgramDef prog)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].program == prog) return true;
            return false;
        }

        /// <summary>The program's bookings, in the order they were made.</summary>
        public void For(ProgramDef prog, List<ClassBooking> into)
        {
            into.Clear();
            for (int i = 0; i < list.Count; i++)
                if (list[i].program == prog) into.Add(list[i]);
        }

        /// <summary>The booking of the program that has this hour and has not been taken up in it yet (<paramref name="stamp"/> is the game hour), else null.</summary>
        public ClassBooking Due(ProgramDef prog, int hour, int stamp)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].program == prog && list[i].Covers(hour) && list[i].handledStamp != stamp) return list[i];
            return null;
        }

        /// <summary>A new booking for the hours that are free of the program's others (none of them free: no booking).</summary>
        public ClassBooking Add(ProgramDef prog, int hours, IntVec3? anchor = null)
        {
            hours &= AllHours & ~TakenHours(prog);
            if (prog == null || hours == 0) return null;
            var b = new ClassBooking { id = nextId++, program = prog, hours = hours, anchor = anchor ?? IntVec3.Invalid };
            list.Add(b);
            version++;
            return b;
        }

        public void Remove(ClassBooking b)
        {
            if (list.Remove(b)) version++;
        }

        /// <summary>Books or frees the hour. An hour another booking of the program holds is refused; freeing the last hour takes the booking away.</summary>
        public bool Toggle(ClassBooking b, int hour)
        {
            if (hour < 0 || hour >= 24) return false;
            if (b.Covers(hour)) b.hours &= ~(1 << hour);
            else if ((TakenHours(b.program, b) >> hour & 1) != 0) return false;
            else b.hours |= 1 << hour;
            if (b.hours == 0) list.Remove(b);
            version++;
            return true;
        }

        public void SetAnchor(ClassBooking b, IntVec3 anchor)
        {
            b.anchor = anchor;
            version++;
        }

        public void SetPriority(ClassBooking b, int priority)
        {
            b.priority = priority;
            version++;
        }

        /// <summary>Why nobody could be taught: no inmate would take the program at all, none has its slot in this hour (a meal, lockup or another activity for them), or those who do are in another class.</summary>
        internal static ClassMiss WhyNobody(Map map, PrisonGame game, ProgramDef prog)
        {
            int eligible = 0, inSlot = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = p.Inmate();
                if (c == null || !c.activeFlag || !prog.Eligible(p, c)) continue;
                eligible++;
                if (c.forcedActivity == null && game.CurrentActivity(p) == prog.Slot) inSlot++;
            }
            return eligible == 0 ? ClassMiss.NobodyEligible : inSlot == 0 ? ClassMiss.NoSlot : ClassMiss.NobodyEligible;
        }

        /// <summary>
        /// The program's 24 hours as the Programs screen shows them: how many of the inmates it would take have its slot in each, what the rest are doing, and the other booked classes that want its room or teacher. Worked out
        /// when a booking is edited and as the hour turns, not every frame.
        /// </summary>
        public static ClassHourInfo[] Analyse(Map map, PrisonMap pm, PrisonGame game, ProgramDef prog)
        {
            var info = new ClassHourInfo[24];
            var tally = new Dictionary<RegimeActivityDef, int>[24];
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = p.Inmate();
                if (c == null || !c.activeFlag || !prog.Eligible(p, c)) continue;
                for (int h = 0; h < 24; h++)
                {
                    info[h].eligible++;
                    RegimeActivityDef act = game.ActivityAt(c, h);
                    if (act == prog.Slot)
                    {
                        info[h].attendable++;
                        continue;
                    }
                    tally[h] ??= new Dictionary<RegimeActivityDef, int>();
                    tally[h][act] = tally[h].TryGetValue(act, out int n) ? n + 1 : 1;
                }
            }
            for (int h = 0; h < 24; h++)
            {
                if (tally[h] != null)
                {
                    int most = 0;
                    foreach (KeyValuePair<RegimeActivityDef, int> kv in tally[h])
                        if (kv.Value > most || kv.Value == most && kv.Key.defName.CompareTo(info[h].instead?.defName) < 0)
                        {
                            most = kv.Value;
                            info[h].instead = kv.Key;
                        }
                }
                ClassBooking first = null;
                foreach (ClassBooking other in pm.bookings.list)
                {
                    if (other.program == prog || !other.Covers(h) || !Shares(prog, other.program)) continue;
                    if (first == null || other.priority > first.priority) first = other;
                }
                info[h].clash = first?.program;
            }
            return info;
        }

        /// <summary>Two programs that want the same room or the same teacher: booked for one hour, only one of them has it.</summary>
        public static bool Shares(ProgramDef a, ProgramDef b)
        {
            if (a.Zone == b.Zone) return true;
            foreach (StaffRoleDef role in a.TeacherRoles)
                if (b.TeacherRoles.Contains(role)) return true;
            return false;
        }

        // ---- text -----------------------------------------------------------------------------------------------------------------------------------------------------------

        public static string HourLabel(int hour) => hour.ToString("00") + ":00";

        /// <summary>"09:00-11:00, 14:00-15:00": the booked hours in runs.</summary>
        public static string HoursLabel(int hours)
        {
            var sb = new StringBuilder();
            for (int h = 0; h < 24; h++)
            {
                if ((hours >> h & 1) == 0) continue;
                int end = h;
                while (end + 1 < 24 && (hours >> (end + 1) & 1) != 0) end++;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(HourLabel(h)).Append('-').Append(HourLabel((end + 1) % 24));
                h = end;
            }
            return sb.ToString();
        }

        /// <summary>The reason as the Programs screen says it ("no counsellor on duty").</summary>
        public static string Reason(ProgramDef prog, ClassMiss reason)
        {
            switch (reason)
            {
                case ClassMiss.NoTeacher:
                case ClassMiss.TeacherBusy:
                    return ("RP_BookMiss_" + reason).Translate(prog.TeacherLabel);
                case ClassMiss.NoRoom:
                case ClassMiss.ZoneInUse:
                    return ("RP_BookMiss_" + reason).Translate(prog.Zone.label);
                case ClassMiss.NoSlot:
                    return ("RP_BookMiss_" + reason).Translate(prog.Slot.label);
                default:
                    return ("RP_BookMiss_" + reason).Translate();
            }
        }

        /// <summary>"missed at 14:00 - no counsellor on duty".</summary>
        public static string MissText(ProgramDef prog, ClassMissRecord m) => "RP_BookMissed".Translate(HourLabel(m.hour), Reason(prog, m.reason));

        // ---- saving ---------------------------------------------------------------------------------------------------------------------------------------------------------

        public void ExposeData()
        {
            Scribe_Collections.Look(ref list, "bookings", LookMode.Deep);
            Scribe_Values.Look(ref nextId, "nextId", 1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                list ??= new List<ClassBooking>();
                // (A program the mod no longer has: its bookings go, said once. A program with two bookings for an hour keeps the older one's.)
                int dropped = list.RemoveAll(b => b?.program == null);
                if (dropped > 0) RPLog.Message($"class bookings: dropped {dropped} booking(s) of a program that no longer exists");
                var held = new Dictionary<ProgramDef, int>();
                foreach (ClassBooking b in list)
                {
                    int had = held.TryGetValue(b.program, out int h) ? h : 0;
                    b.hours &= ~had;
                    held[b.program] = had | b.hours;
                    nextId = System.Math.Max(nextId, b.id + 1);
                }
                list.RemoveAll(b => b.hours == 0);
            }
        }
    }
}
