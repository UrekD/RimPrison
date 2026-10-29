using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPCountDefOf
    {
        public static JobDef RP_Count;
        public static MisconductDef RP_OutOfPlace;

        static RPCountDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCountDefOf));
    }

    /// <summary>How an escape went (what the count's letter says, and which letters a sighting brings back).</summary>
    public enum EscapeWay { Walked, Tunnel, Stowaway }

    /// <summary>
    /// An escape nobody saw: no guard, camera or tower had the inmate in sight when they got out, so the warden has not been told. The letter, the fine, the chronicle line and the police wait until
    /// a count finds the cell empty, or until something sees them (see <see cref="CountSystem"/>).
    /// </summary>
    public class HiddenEscape : IExposable
    {
        public Pawn pawn;
        public EscapeWay way;
        /// <summary>When they began the break, and when they left the map (-1: still on it).</summary>
        public int startTick, exitTick = -1;
        /// <summary>Where they were when they began (their cell, for a tunnel), and the name for it that the count's line gives.</summary>
        public IntVec3 cell = IntVec3.Invalid;
        public string place;
        /// <summary>The wing their cell is in: the count of that wing is the one that finds them gone.</summary>
        public SecurityLevelDef security;
        public string name;
        /// <summary>How many came out of the tunnel together (the letter a sighting brings says so).</summary>
        public int group = 1;

        public bool Exited => exitTick >= 0;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn", saveDestroyedThings: false);
            Scribe_Values.Look(ref way, "way");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref exitTick, "exitTick", -1);
            Scribe_Values.Look(ref cell, "cell", IntVec3.Invalid);
            Scribe_Values.Look(ref place, "place");
            Scribe_Defs.Look(ref security, "security");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref group, "group", 1);
        }
    }

    /// <summary>An inmate the last count could not find: the HUD and the alert name them and say where they were last seen.</summary>
    public class CountEntry : IExposable
    {
        public int pawnId;
        public string name, place;
        /// <summary>When they got away (the head start is what the count left the police).</summary>
        public int escapedTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref pawnId, "pawnId");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref place, "place");
            Scribe_Values.Look(ref escapedTick, "escapedTick");
        }
    }

    /// <summary>The count's state on a map: the one running (if any), how the last ended, who is missing, and the escapes nobody has seen. Every field has its default, so an old save loads with no count behind it.</summary>
    public class CountState : IExposable
    {
        /// <summary>When the last count that every wing was counted in ended with nobody missing (-1: never), and when the last one that no guard was free for ended.</summary>
        public int lastClearedTick = -1, lastMissedTick = -1;
        /// <summary>The running count: when it was called (-1: none) and when the guards may begin (the inmates are given a little time to settle first).</summary>
        public int startedTick = -1, dueTick = -1;
        /// <summary>The wings (zone grid indices) no guard has counted yet in the running count, how many have been, and how many inmates were written up for being out of place.</summary>
        public List<int> wingsLeft = new List<int>();
        public int walked, writeUps;
        /// <summary>Who the last count found missing.</summary>
        public List<CountEntry> unaccounted = new List<CountEntry>();
        /// <summary>The escapes the warden has not been told of.</summary>
        public List<HiddenEscape> hidden = new List<HiddenEscape>();

        public bool Running => startedTick >= 0;

        public HiddenEscape HiddenOf(Pawn p)
        {
            for (int i = 0; i < hidden.Count; i++)
                if (hidden[i].pawn == p) return hidden[i];
            return null;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref lastClearedTick, "lastClearedTick", -1);
            Scribe_Values.Look(ref lastMissedTick, "lastMissedTick", -1);
            Scribe_Values.Look(ref startedTick, "startedTick", -1);
            Scribe_Values.Look(ref dueTick, "dueTick", -1);
            Scribe_Collections.Look(ref wingsLeft, "wingsLeft", LookMode.Value);
            Scribe_Values.Look(ref walked, "walked");
            Scribe_Values.Look(ref writeUps, "writeUps");
            Scribe_Collections.Look(ref unaccounted, "unaccounted", LookMode.Deep);
            Scribe_Collections.Look(ref hidden, "hidden", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                wingsLeft ??= new List<int>();
                unaccounted ??= new List<CountEntry>();
                unaccounted.RemoveAll(e => e == null);
                hidden ??= new List<HiddenEscape>();
                // (A pawn that did not survive the save is not one the count can still find: the escape was never told and is not now.)
                hidden.RemoveAll(h => h?.pawn == null);
            }
        }
    }

    /// <summary>
    /// The count (see DESIGN.md): at lockup and at six (Policy sets how many a day) the guards walk the wings and count heads. An escape nobody saw is not known until a count finds the cell empty,
    /// or until a guard, a camera or a tower sees the runaway; an inmate out of place at the count is written up.
    /// </summary>
    public static class CountSystem
    {
        public const int DefaultPerDay = 2, MaxPerDay = 4;
        /// <summary>The inmates are given this long after the hour to be where the regime sends them before the guards begin; a wing's walk takes half an hour at most; a guard is waited for an hour.</summary>
        public const int GraceTicks = 600, WingTicks = GenDate.TicksPerHour / 2, WindowTicks = GenDate.TicksPerHour, StopPauseTicks = 60;
        /// <summary>Each hour of head start takes this much off the odds of getting a runaway back (never below <see cref="MinHeadStartFactor"/> of them).</summary>
        public const float HeadStartPenaltyPerHour = 0.02f, MinHeadStartFactor = 0.25f;
        /// <summary>The checks' switch: under <see cref="AutoTest.Active"/> there is no count (an escape is told at once, as the old checks expect) unless a check turns it on.</summary>
        public static bool TestOn;
        /// <summary>The checks' own inmates: a wing's count looks at these only (the live prison's inmates are not written up by a check).</summary>
        public static HashSet<Pawn> OnlyThese;
        /// <summary>The hours a day's counts are taken at after the lockup hour: the morning one, then mid-afternoon, then the middle of the night.</summary>
        private static readonly int[] MoreHours = { 6, 14, 2, 10 };
        private const int DefaultLockupHour = 22;

        /// <summary>The prison employs a guard (on shift, off it or at home): somebody there is to count heads. With none the count is not called and nothing is held back (the alert for no guards is the warden's news).</summary>
        public static bool AnyGuard(Map map) => StaffCommute.AllStaff(map).Any(GuardUtility.IsGuard);

        public static bool Enabled(PrisonGame game) => game != null && game.countsPerDay > 0 && (!AutoTest.Active || TestOn);

        /// <summary>
        /// The hour the regime locks the cells up for the night: the lockup that leads into the night's sleep (the first sleep slot from midday round, and back over the lockup slots before it: with a midday lockup and an
        /// evening one it is the evening's); the sleep hour itself when nothing locks up before it; 22:00 when the regime has no sleep.
        /// </summary>
        public static int LockupHour(PrisonGame game)
        {
            RegimeSchedule sched = game.RegimeFor(InmateUtility.DefaultSecurity);
            int hour = -1;
            for (int i = 0; i < 24 && hour < 0; i++)
                if (sched.At((12 + i) % 24) == RPDefOf.RP_Sleep) hour = (12 + i) % 24;
            if (hour < 0) return DefaultLockupHour;
            for (int back = 0; back < 24; back++)
            {
                RegimeActivityDef before = sched.At((hour + 23) % 24);
                if (!before.lockup || before == RPDefOf.RP_Sleep) break;
                hour = (hour + 23) % 24;
            }
            return hour;
        }

        /// <summary>The hours of day counts are taken at, in the order of the day: the lockup hour first, then six, then the afternoon and the small hours as more are asked for.</summary>
        public static List<int> Hours(PrisonGame game)
        {
            int perDay = Mathf.Clamp(game.countsPerDay, 0, MaxPerDay);
            var hours = new List<int>();
            if (perDay > 0) hours.Add(LockupHour(game));
            foreach (int h in MoreHours)
                if (hours.Count < perDay && !hours.Contains(h)) hours.Add(h);
            hours.Sort();
            return hours;
        }

        /// <summary>The next hour a count is called at after <paramref name="hour"/> (-1: none, counts are off).</summary>
        public static int NextHour(PrisonGame game, int hour)
        {
            List<int> hours = Hours(game);
            if (hours.Count == 0) return -1;
            foreach (int h in hours)
                if (h > hour) return h;
            return hours[0];
        }

        public static string HoursText(PrisonGame game) => string.Join(", ", Hours(game).Select(h => h.ToString("00") + ":00"));

        /// <summary>The clock time of a tick in the past (the day the prison keeps), as "22:10".</summary>
        public static string ClockAt(Map map, int tick)
        {
            float ago = Find.TickManager.TicksGame - tick;
            float hour = GenLocalDate.HourFloat(map) - ago / GenDate.TicksPerHour;
            hour = ((hour % 24f) + 24f) % 24f;
            int h = Mathf.FloorToInt(hour), m = Mathf.FloorToInt((hour - h) * 60f);
            return h.ToString("00") + ":" + m.ToString("00");
        }

        /// <summary>What the odds of getting a runaway back are after this many ticks' head start: the usual odds with no head start, fewer with every hour.</summary>
        public static float RecaptureChanceAfter(int headStartTicks) =>
            FugitiveSystem.RecaptureChance * Mathf.Max(MinHeadStartFactor, 1f - HeadStartPenaltyPerHour * Mathf.Max(0, headStartTicks) / GenDate.TicksPerHour);

        // ---- Starting an escape: seen, or not ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// An inmate makes a break for it. If nothing that watches has them in sight (the one seen rule: <see cref="TensionUtility.SeenAt"/>) and the prison counts, nobody is told: no begin letter, and the
        /// escape is held as one the count will find. Seen, or with no count, it is told at once, as ever. Returns whether the escape began.
        /// </summary>
        public static bool StartEscaping(Pawn p, string reason, EscapeWay way, int group = 1, IntVec3? from = null)
        {
            PrisonGame game = PrisonGame.Instance;
            PrisonMap pm = p.Spawned ? PrisonMap.Of(p.Map) : null;
            // (Not from another mental state: starting one silently does not end it, and the begin letter is the only one that tells of an escape.)
            bool hide = pm != null && Enabled(game) && !p.InMentalState && AnyGuard(p.Map) && !TensionUtility.SeenAt(p.Position, p.Map, pm);
            if (!p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Escaping, reason, forced: true, transitionSilently: hide)) return false;
            if (hide) Hide(p, pm, way, group, from ?? p.Position);
            return true;
        }

        private static void Hide(Pawn p, PrisonMap pm, EscapeWay way, int group, IntVec3 last)
        {
            CountState c = pm.count;
            if (c.HiddenOf(p) != null) return;
            PrisonZoneDef zone = pm.zones.ZoneAt(last);
            c.hidden.Add(new HiddenEscape
            {
                pawn = p, way = way, startTick = Find.TickManager.TicksGame, cell = last, security = p.Inmate()?.security, name = p.LabelShort, group = group,
                place = (zone != null ? "RP_Count_WhereZone".Translate(zone.label) : "RP_Count_WhereGrounds".Translate()).Resolve(),
            });
            if (AutoTest.Active) RPLog.Message($"[count] {p.LabelShort} began an escape ({way}) and nobody saw it");
        }

        /// <summary>Whether this runaway is one the warden has not been told of (the escape alert leaves them out).</summary>
        public static bool IsHidden(Pawn p) => p.Spawned && PrisonMap.Of(p.Map)?.count.HiddenOf(p) != null;

        /// <summary>A guard stopped them (the stowaway at the ramp): it is known, and there is nothing left to find.</summary>
        public static void Forget(Pawn p)
        {
            if (p.Spawned && PrisonMap.Of(p.Map) is { } pm && pm.count.HiddenOf(p) is { } h) pm.count.hidden.Remove(h);
        }

        /// <summary>
        /// The runaway reaches the edge of the map. True when nobody has seen the escape from the start to here (the count will find it: the record is kept, the world holds the pawn, and the letter,
        /// the fine and the chronicle wait); false when it is known (seen on the way or here: the sighting is told now) or never was a secret.
        /// </summary>
        public static bool Notify_Exiting(Pawn p, PrisonGame game, bool stowaway)
        {
            PrisonMap pm = p.Spawned ? PrisonMap.Of(p.Map) : null;
            HiddenEscape h = pm?.count.HiddenOf(p);
            if (h == null) return false;
            if (!Enabled(game) || TensionUtility.SeenAt(p.Position, p.Map, pm))
            {
                pm.count.hidden.Remove(h);
                Announce(p.Map, h);
                return false;
            }
            h.exitTick = Find.TickManager.TicksGame;
            h.cell = p.Position;
            // (One who missed the shuttle, the ramp closed or out of reach, goes the ordinary way: they are no stowaway to the count.)
            h.way = stowaway ? EscapeWay.Stowaway : h.way == EscapeWay.Stowaway ? EscapeWay.Walked : h.way;
            // (Gone from the map, a world pawn the world must not discard before the count has found it.)
            Find.WorldPawns.ForcefullyKeptPawns.Add(p);
            if (AutoTest.Active) RPLog.Message($"[count] {p.LabelShort} left the map at {p.Position} and nobody saw it");
            return true;
        }

        // ---- What the warden is told, and when --------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A hidden escape is seen while the runaway is still on the map: the begin letter that was held back (a stowaway's message, a tunnel's breakout).</summary>
        private static void Announce(Map map, HiddenEscape h)
        {
            Pawn p = h.pawn;
            switch (h.way)
            {
                case EscapeWay.Tunnel:
                    Find.LetterStack.ReceiveLetter("RP_TunnelBreakoutLabel".Translate(), "RP_TunnelBreakoutText".Translate(h.name, h.group), LetterDefOf.ThreatBig, new LookTargets(p));
                    break;
                case EscapeWay.Stowaway:
                    Messages.Message("RP_StowawaySpotted".Translate(h.name), p, MessageTypeDefOf.ThreatSmall);
                    break;
                default:
                    Find.LetterStack.ReceiveLetter("RP_Count_SightedLabel".Translate(), "RP_Count_SightedText".Translate(h.name), LetterDefOf.ThreatSmall, new LookTargets(p));
                    break;
            }
        }

        private enum Found { Seen, Count, Policy }

        /// <summary>The escape is known now: the runaway is on the map still (the held letter is sent), or gone (the books are opened: the fine, the police, the chronicle and the letter, as an escape known at once has them).</summary>
        private static void Reveal(Map map, PrisonMap pm, PrisonGame game, HiddenEscape h, Found how, int now)
        {
            pm.count.hidden.Remove(h);
            Pawn p = h.pawn;
            int gone = h.Exited ? h.exitTick : h.startTick;
            // (One still on the map is not unaccounted for: the cell was empty and they are out on the grounds, a sighting.)
            if (how == Found.Count && h.Exited)
            {
                pm.count.unaccounted.Add(new CountEntry { pawnId = p.thingIDNumber, name = h.name, place = h.place, escapedTick = gone });
                string way = ("RP_Count_Way" + h.way).Translate().Resolve();
                string hours = ((now - gone) / (float)GenDate.TicksPerHour).ToString("0");
                // (The count's own letter and chronicle line are the telling: not the escape's, nor a stowaway's.)
                EscapeUtility.Register(p, game, h.way == EscapeWay.Stowaway, gone, map, h.cell, letter: false, line: false);
                KeepOnlyFugitives(game, p);
                Find.LetterStack.ReceiveLetter("RP_Count_ShortLabel".Translate(), "RP_Count_ShortText".Translate(h.name, ClockAt(map, now), way, h.place, hours, Money.Format(EscapeUtility.EscapeFine)), LetterDefOf.ThreatBig, new LookTargets(h.cell, map));
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_CountShort".Translate(h.name), p);
                return;
            }
            if (!h.Exited)
            {
                Announce(map, h);
                return;
            }
            // Seen at last, or the count is off: the escape is told as it would have been, with the time it has been going on.
            EscapeUtility.Register(p, game, h.way == EscapeWay.Stowaway, gone, map, h.cell, letter: true);
            if (h.way == EscapeWay.Stowaway) ShuttleSystem.AnnounceStowawayAway(p);
            KeepOnlyFugitives(game, p);
        }

        /// <summary>The world held the runaway for the count to find; once the escape is told only the police's own record (a fugitive they mean to bring back) keeps them.</summary>
        private static void KeepOnlyFugitives(PrisonGame game, Pawn p)
        {
            if (!FugitiveSystem.Tracks(game, p.thingIDNumber)) Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
        }

        // ---- The hour, and the beat -------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Every hour: a count is called when the hour is one of the day's (and the one before it, if any is still running, is over).</summary>
        public static void Hourly(Map map, PrisonMap pm, PrisonGame game, int hour)
        {
            CountState c = pm.count;
            bool guards = AnyGuard(map);
            if (!Enabled(game) || !guards)
            {
                // Counts off, or nobody to count: nothing is hidden any more, and what was is told. (The checks' own state is theirs to put away.)
                if (!AutoTest.Active)
                {
                    c.startedTick = c.dueTick = -1;
                    c.wingsLeft.Clear();
                    foreach (HiddenEscape h in c.hidden.ToList()) Reveal(map, pm, game, h, Found.Policy, Find.TickManager.TicksGame);
                }
                return;
            }
            // (A count the beat has not ended by now is over: the guard on a wing had half an hour past the hour at most.)
            if (c.Running && Find.TickManager.TicksGame >= c.startedTick + WindowTicks + WingTicks) Finish(map, pm, game, Find.TickManager.TicksGame);
            if (Hours(game).Contains(hour)) Begin(map, pm, game, Find.TickManager.TicksGame);
        }

        /// <summary>The 250-tick beat: runaways that are seen, or have given up, and the end of a count the guards had no time for.</summary>
        public static void Tick(Map map, PrisonMap pm)
        {
            CountState c = pm.count;
            PrisonGame game = PrisonGame.Instance;
            if (game == null || !Enabled(game) || (!c.Running && c.hidden.Count == 0)) return;
            int now = Find.TickManager.TicksGame;
            for (int i = c.hidden.Count - 1; i >= 0; i--)
            {
                HiddenEscape h = c.hidden[i];
                if (h.Exited) continue;
                Pawn p = h.pawn;
                // On the map: seen now, or not an escape any more (caught, given it up, hurt, dead: nobody ever knew of it). Off it some other way: nothing for the count to find.
                if (!p.Spawned || p.Map != map || p.Dead || p.MentalStateDef != RPMentalStateDefOf.RP_Escaping) c.hidden.RemoveAt(i);
                else if (TensionUtility.SeenAt(p.Position, map, pm)) Reveal(map, pm, game, h, Found.Seen, now);
            }
            if (c.Running && now >= c.startedTick + WindowTicks && (!AnyoneCounting(map) || now >= c.startedTick + WindowTicks + WingTicks)) Finish(map, pm, game, now);
        }

        /// <summary>The count is called: every wing with somebody in it is to be walked.</summary>
        internal static void Begin(Map map, PrisonMap pm, PrisonGame game, int now)
        {
            CountState c = pm.count;
            // (A count still running when the next is called, a regime with two of its hours next to each other, is over: finished as it stands, the wings not walked missed, and the new one begins. It is never overwritten.)
            if (c.Running) Finish(map, pm, game, now);
            var wings = new List<int>();
            foreach (PrisonZoneDef wing in WardenSystem.Wings(map))
                if (HoldsAnyone(map, pm, wing)) wings.Add(wing.gridIndex);
            if (wings.Count == 0) return;
            (c.startedTick, c.dueTick, c.wingsLeft, c.walked, c.writeUps) = (now, now + GraceTicks, wings, 0, 0);
            c.unaccounted.Clear();
            lastTry.Clear();
            if (AutoTest.Active) RPLog.Message($"[count] called at {ClockAt(map, now)}: {wings.Count} wing(s)");
        }

        private static bool HoldsAnyone(Map map, PrisonMap pm, PrisonZoneDef wing)
        {
            foreach (HiddenEscape h in pm.count.hidden)
                if (h.security == wing.security) return true;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (InmateUtility.IsActiveInmate(p) && p.Inmate() != null && WardenSystem.InWing(p, wing, pm)) return true;
            return false;
        }

        /// <summary>The count is over: cleared, short, or missed (no guard was free for it, or for some of the wings).</summary>
        internal static void Finish(Map map, PrisonMap pm, PrisonGame game, int now)
        {
            CountState c = pm.count;
            if (!c.Running) return;
            int left = c.wingsLeft.Count, walked = c.walked, writeUps = c.writeUps;
            // (A runaway from a wing that has no cell to walk any more: the roll call of the rest finds the gap.)
            if (walked > 0)
            {
                List<PrisonZoneDef> wings = WardenSystem.Wings(map);
                foreach (HiddenEscape h in c.hidden.ToList())
                    if (!wings.Any(w => w.security == h.security)) Reveal(map, pm, game, h, Found.Count, now);
            }
            (c.startedTick, c.dueTick) = (-1, -1);
            c.wingsLeft.Clear();
            string at = ClockAt(map, now);
            if (walked == 0)
            {
                c.lastMissedTick = now;
                Messages.Message("RP_Count_Missed".Translate(at), MessageTypeDefOf.NegativeEvent, historical: false);
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_CountMissed".Translate(at));
                if (AutoTest.Active) RPLog.Message($"[count] missed at {at}: no guard was free for any wing");
                return;
            }
            if (left > 0)
            {
                c.lastMissedTick = now;
                Messages.Message("RP_Count_WingsMissed".Translate(walked, walked + left), MessageTypeDefOf.NegativeEvent, historical: false);
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, (left == 1 ? "RP_Chr_CountPartialOne".Translate(at) : "RP_Chr_CountPartial".Translate(at, left)));
            }
            else if (c.unaccounted.Count == 0) c.lastClearedTick = now;
            if (writeUps > 0) Messages.Message("RP_Count_WrittenUp".Translate(writeUps), MessageTypeDefOf.NeutralEvent, historical: false);
            if (AutoTest.Active) RPLog.Message($"[count] over at {at}: {walked} wing(s) walked, {left} missed, {writeUps} written up, {c.unaccounted.Count} unaccounted");
        }

        // ---- The guards -------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A guard that took a wing and had it end at once (the plan could not be walked) is not given it again for a while: no job storm.</summary>
        internal static readonly Dictionary<int, int> lastTry = new Dictionary<int, int>();

        /// <summary>Wings are offered to guards from when the inmates have settled until an hour after the call.</summary>
        public static bool Open(PrisonMap pm)
        {
            CountState c = pm?.count;
            // (Counts off, or off under a test: nothing is offered, whatever state a save or a check left behind.)
            if (c == null || !c.Running || c.wingsLeft.Count == 0 || !Enabled(PrisonGame.Instance)) return false;
            int now = Find.TickManager.TicksGame;
            return now >= c.dueTick && now < c.startedTick + WindowTicks;
        }

        /// <summary>Whether the wing is one the running count still wants walked.</summary>
        public static bool StillOpen(Map map, PrisonZoneDef wing) => wing != null && PrisonMap.Of(map)?.count is { Running: true } c && c.wingsLeft.Contains(wing.gridIndex);

        private static bool AnyoneCounting(Map map)
        {
            IReadOnlyList<Pawn> staff = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < staff.Count; i++)
                if (staff[i].CurJobDef == RPCountDefOf.RP_Count) return true;
            return false;
        }

        private static Pawn Counting(Map map, int wingIndex)
        {
            IReadOnlyList<Pawn> staff = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < staff.Count; i++)
                if (staff[i].CurJobDef == RPCountDefOf.RP_Count && staff[i].CurJob.count == wingIndex) return staff[i];
            return null;
        }

        /// <summary>
        /// Whether this guard may be sent to count a wing: on duty (not off shift, on a break, or at home), not on a post, a tower or the pad (the posts stay manned) nor due on one (see <see cref="DueOnAPost"/>), not on a job that cannot be cut (an escort, a subdue: they are not looking
        /// for work), nobody's alarm to answer, not in the care reserve (it keeps to its care; a released reserve is no reserve, see <see cref="CareDetail.IsReserve"/>), and not kept off the wing or deployed to another.
        /// </summary>
        private static bool Free(Pawn guard, PrisonZoneDef wing)
        {
            if (!GuardUtility.IsGuard(guard) || !GuardPosts.OnDuty(guard) || HoldsAPost(guard) || DueOnAPost(guard) || GuardUtility.AlarmFor(guard) || CareDetail.IsReserve(guard)) return false;
            if (SpecialMeasures.DeployedZone(guard) is { security: not null } deployed && deployed != wing) return false;
            return !Kin.KeepsOffZone(guard, wing);
        }

        /// <summary>On a guard post, on a tower (walking to one as well) or on pad duty: a place somebody must hold, which a count never takes a guard from.</summary>
        public static bool HoldsAPost(Pawn guard)
        {
            JobDef job = guard.CurJobDef;
            return job == RPPostDefOf.RP_ManPost || job == RPArmedDefOf.RP_ManTower || job == RPShuttleDefOf.RP_PadDuty;
        }

        /// <summary>
        /// A post that is open and this guard is the one it would send, or an empty tower and this guard the nearest who could go: not on it yet (just in for the shift, back from a meal or an escort), and the count does not
        /// take them from it (the morning count falls on the day shift's start, when the posts are being taken over).
        /// </summary>
        public static bool DueOnAPost(Pawn guard) => GuardPosts.PostFor(guard) != null || DueOnATower(guard);

        private static bool DueOnATower(Pawn guard)
        {
            List<Thing> towers = guard.Map.listerThings.ThingsOfDef(RPArmedDefOf.RP_GuardTower);
            for (int i = 0; i < towers.Count; i++)
            {
                if (towers[i].TryGetComp<CompGuardTower>() is not { Manned: false } || towers[i].IsForbidden(guard) || !guard.CanReach(towers[i].Position, PathEndMode.OnCell, Danger.Some)) continue;
                int mine = guard.Position.DistanceToSquared(towers[i].Position);
                bool nearer = false;
                IReadOnlyList<Pawn> staff = guard.Map.mapPawns.FreeColonistsSpawned;
                for (int j = 0; j < staff.Count && !nearer; j++)
                {
                    Pawn other = staff[j];
                    if (other == guard || !GuardUtility.IsGuard(other) || !GuardPosts.OnDuty(other) || HoldsAPost(other) || other.CurJobDef == RPCountDefOf.RP_Count) continue;
                    int theirs = other.Position.DistanceToSquared(towers[i].Position);
                    nearer = theirs < mine || theirs == mine && other.thingIDNumber < guard.thingIDNumber;
                }
                if (!nearer) return true;
            }
            return false;
        }

        /// <summary>A wing no guard is on that this guard could take (what ends a patrol: the count wants them).</summary>
        public static bool WantsGuard(Pawn guard)
        {
            PrisonMap pm = PrisonMap.Of(guard.Map);
            if (!Open(pm)) return false;
            foreach (int idx in pm.count.wingsLeft)
                if (PrisonZoneDef.FromGridIndex((byte)idx) is { } wing && Counting(guard.Map, idx) == null && Free(guard, wing)) return true;
            return false;
        }

        /// <summary>The job that has this guard walk the first wing nobody is on, by the warden's walk plan; null when there is none.</summary>
        public static Job JobFor(Pawn guard)
        {
            Map map = guard.Map;
            PrisonMap pm = PrisonMap.Of(map);
            if (map == null || !Open(pm)) return null;
            int now = Find.TickManager.TicksGame;
            if (lastTry.TryGetValue(guard.thingIDNumber, out int tried) && now - tried < 150) return null;
            foreach (int idx in pm.count.wingsLeft)
            {
                PrisonZoneDef wing = PrisonZoneDef.FromGridIndex((byte)idx);
                if (wing == null || Counting(map, idx) != null || !Free(guard, wing)) continue;
                List<IntVec3> stops = WardenSystem.WalkPlan(guard, wing);
                if (stops.Count == 0) continue;
                lastTry[guard.thingIDNumber] = now;
                Job job = JobMaker.MakeJob(RPCountDefOf.RP_Count);
                job.targetQueueA = stops.Select(c => new LocalTargetInfo(c)).ToList();
                job.count = idx;
                return job;
            }
            return null;
        }

        // ---- A wing counted ---------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard has walked the wing: everyone on it is counted (<see cref="CompInmate.lastCountedTick"/>), anyone out of place is written up, and a runaway of the wing is found missing. The last wing ends the count.
        /// </summary>
        public static void WingCounted(Pawn guard, PrisonZoneDef wing)
        {
            Map map = guard.MapHeld;
            PrisonMap pm = PrisonMap.Of(map);
            PrisonGame game = PrisonGame.Instance;
            if (pm == null || game == null || wing == null || !pm.count.Running || !pm.count.wingsLeft.Remove(wing.gridIndex)) return;
            CountState c = pm.count;
            int now = Find.TickManager.TicksGame;
            c.walked++;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
            {
                if (!InmateUtility.IsActiveInmate(p) || p.Inmate() is not { } ic || !WardenSystem.InWing(p, wing, pm) || OnlyThese != null && !OnlyThese.Contains(p)) continue;
                ic.lastCountedTick = now;
                if (Accounted(p, ic, pm, game)) continue;
                c.writeUps++;
                MisconductUtility.Record(p, ic, pm, RPCountDefOf.RP_OutOfPlace, evidence: true, chronicle: false);
                if (AutoTest.Active) RPLog.Message($"[count] {p.LabelShort} was out of place at {p.Position} and written up");
            }
            foreach (HiddenEscape h in c.hidden.ToList())
                if (h.security == wing.security) Reveal(map, pm, game, h, Found.Count, now);
            if (c.wingsLeft.Count == 0) Finish(map, pm, game, now);
        }

        /// <summary>
        /// Whether the inmate is somewhere the prison has put them: in their cell, the hole, a medical bed, holding, a hearing, a class, a job they are allowed, a visit, on an escort or carried by staff, or where the
        /// regime wants them (now, or in the slot before: they are given the hour to settle).
        /// </summary>
        public static bool Accounted(Pawn p, CompInmate c, PrisonMap pm, PrisonGame game)
        {
            // (Lying hurt, in a mental state, new, held for a search, on an escort, on the way out: the prison has them in hand.)
            if (p.Downed || p.InMentalState || !c.processed || c.awaitingSearch || c.leg != null || p.CarriedBy != null) return true;
            if (ShuttleSystem.OnTheWayOut(p) || ShuttleSystem.IsArrival(p) || ParoleScene.IsEscorted(p)) return true;
            if (RegimePlacement.Sanctioned(p) || LaborUtility.IsWorking(p, c) || InTransitOrInHand(p)) return true;
            PrisonZoneDef here = pm.zones.ZoneAt(p.Position);
            if (here == RPSecurityDefOf.RP_SolitaryZone || here == RPDefOf.RP_Holding) return true;
            if (RegimePlacement.InExpectedPlace(p, game.CurrentActivity(p), pm)) return true;
            int before = (GenLocalDate.HourOfDay(p.Map) + 23) % 24;
            return RegimePlacement.InExpectedPlace(p, game.ActivityAt(c, before), pm);
        }

        /// <summary>
        /// Between places, or with somebody: walking (to where the regime sends them: a walk that is no wander), held by staff for a session, a search or a frisk (the wait they are kept in), at prayer or at the commissary.
        /// Judged where they stand, none of these is a place the regime has; they are not out of it.
        /// </summary>
        private static bool InTransitOrInHand(Pawn p)
        {
            JobDef job = p.CurJobDef;
            if (job == JobDefOf.Wait_MaintainPosture || job == RPFaithDefOf.RP_Pray || job == RPCommissaryDefOf.RP_BuyCommissary) return true;
            return p.pather is { Moving: true } && job != JobDefOf.GotoWander;
        }

        // ---- The screens ------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The HUD's line, and whether it is a warning. In order of what the warden needs first: who is unaccounted, then a missed count, then (alone) a count running, a cleared one, or when the next is. A count running
        /// while one of the first two stands is added after it ("count: 2 unaccounted, counting - 1 to go"), never in its place: the trouble is not hidden by the next count's start.
        /// </summary>
        public static string HudLine(Map map, PrisonMap pm, PrisonGame game, out bool warn)
        {
            warn = false;
            CountState c = pm.count;
            if (game.countsPerDay <= 0) return "RP_HudCountOff".Translate();
            string trouble = null;
            int missing = StillMissing(map, pm).Count;
            if (missing > 0) trouble = "RP_HudCountShort".Translate(missing);
            else if (c.lastMissedTick >= 0 && c.lastMissedTick > c.lastClearedTick) trouble = "RP_HudCountMissed".Translate(ClockAt(map, c.lastMissedTick));
            if (trouble != null)
            {
                warn = true;
                return c.Running ? "RP_HudCountAlso".Translate(trouble, c.wingsLeft.Count) : trouble;
            }
            if (c.Running) return c.wingsLeft.Count == 1 ? "RP_HudCountingOne".Translate() : "RP_HudCounting".Translate(c.wingsLeft.Count);
            if (c.lastClearedTick >= 0) return "RP_HudCountCleared".Translate(ClockAt(map, c.lastClearedTick));
            return "RP_HudCountNext".Translate(NextHour(game, GenLocalDate.HourOfDay(map)).ToString("00"));
        }

        /// <summary>The inmates the last count found missing who are still gone (one back on the map, recaptured, is not): what the HUD's line counts and the alert lists.</summary>
        public static List<CountEntry> StillMissing(Map map, PrisonMap pm)
        {
            var missing = new List<CountEntry>();
            foreach (CountEntry e in pm.count.unaccounted)
                if (!map.mapPawns.PrisonersOfColonySpawned.Any(p => p.thingIDNumber == e.pawnId)) missing.Add(e);
            return missing;
        }

        /// <summary>The names the HUD and the alert give, with where they were last seen and how long ago they went.</summary>
        public static List<string> UnaccountedLines(Map map, PrisonMap pm)
        {
            int now = Find.TickManager.TicksGame;
            return StillMissing(map, pm).Select(e => "RP_Count_Line".Translate(e.name, e.place, ((now - e.escapedTick) / (float)GenDate.TicksPerHour).ToString("0")).Resolve()).ToList();
        }
    }

    /// <summary>A guard of the wing with nothing else on walks it and counts the heads.</summary>
    public class WorkGiver_Count : WorkGiver
    {
        // (The care reserve takes no count: see CareDetail.)
        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || pawn.Map == null || !forced && CareDetail.IsReserve(pawn) || !CountSystem.Open(PrisonMap.Of(pawn.Map));

        public override Job NonScanJob(Pawn pawn) => CountSystem.JobFor(pawn);
    }

    /// <summary>Walk the wing's stops (the warden's walk plan), a pause at each; the wing is counted when the last is done, or when half an hour is up, whichever is first.</summary>
    public class JobDriver_Count : JobDriver
    {
        public const int AlarmCheckTicks = 30;

        private int nextAlarmCheckTick;

        private PrisonZoneDef Wing => PrisonZoneDef.FromGridIndex((byte)job.count);

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // (The count over, or the wing done by another: nothing to walk for.)
            this.FailOn(() => !CountSystem.StillOpen(pawn.MapHeld, Wing));
            AddEndCondition(() => Find.TickManager.TicksGame - startTick >= CountSystem.WingTicks ? JobCondition.Succeeded : JobCondition.Ongoing);
            // (An alarm that this guard would be sent to ends the walk, as the patrol gives way: the wing is left for the next guard, or for this one when the trouble is over. Nothing else makes a walking job ask the think tree, and
            // the wing is not counted for an interrupted walk.)
            nextAlarmCheckTick = Find.TickManager.TicksGame + AlarmCheckTicks;
            AddEndCondition(() =>
            {
                int now = Find.TickManager.TicksGame;
                if (now < nextAlarmCheckTick) return JobCondition.Ongoing;
                nextAlarmCheckTick = now + AlarmCheckTicks;
                return GuardUtility.AlarmFor(pawn) ? JobCondition.InterruptOptional : JobCondition.Ongoing;
            });
            AddFinishAction(condition =>
            {
                if (condition == JobCondition.Succeeded) CountSystem.WingCounted(pawn, Wing);
            });
            List<LocalTargetInfo> stops = job.targetQueueA;
            for (int i = 0; i < stops.Count; i++)
            {
                yield return Toils_Goto.GotoCell(stops[i].Cell, PathEndMode.OnCell);
                yield return Toils_General.Wait(CountSystem.StopPauseTicks);
            }
        }
    }
}
