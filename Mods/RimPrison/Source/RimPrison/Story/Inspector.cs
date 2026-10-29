using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPInspectorDefOf
    {
        public static StaffRoleDef RP_Inspector;

        static RPInspectorDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPInspectorDefOf));
    }

    /// <summary>One thing the inspector wrote down: what it is (<see cref="key"/>), where (<see cref="cell"/>) or how many (<see cref="id"/>), by when, and since when it has not been so (<see cref="goneSince"/>, -1 while it is).</summary>
    public class InspectionFinding : IExposable
    {
        public string key;
        public IntVec3 cell = IntVec3.Invalid;
        public int id = -1;
        public int dueTick;
        public int goneSince = -1;

        /// <summary>It is not so now (put right, for the moment: see <see cref="InspectionReport.Held"/>).</summary>
        public bool Gone => goneSince >= 0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref cell, "cell", IntVec3.Invalid);
            Scribe_Values.Look(ref id, "id", -1);
            Scribe_Values.Look(ref dueTick, "due");
            Scribe_Values.Look(ref goneSince, "goneSince", -1);
        }

        public string Describe() => $"{key}:{cell}:{id}:{dueTick}:{goneSince}";
    }

    /// <summary>
    /// The inspector's book, owned by <see cref="StorySystem"/>: what the last visit wrote down (while the re-inspection is pending), what a bribed visit left out (until the exposure letter names it or the next visit), the
    /// re-inspection's tick (-1 none), the kinds of finding the prison gave when the visit was announced (null: no notice taken) and the inspector on the map for a visit.
    /// </summary>
    public class InspectionState : IExposable
    {
        public List<InspectionFinding> findings = new List<InspectionFinding>();
        public List<InspectionFinding> hidden = new List<InspectionFinding>();
        public int reinspectTick = -1;
        public List<string> noticeKeys;
        public Pawn inspector;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref findings, "findings", LookMode.Deep);
            Scribe_Collections.Look(ref hidden, "hidden", LookMode.Deep);
            Scribe_Values.Look(ref reinspectTick, "reinspectTick", -1);
            Scribe_Collections.Look(ref noticeKeys, "noticeKeys", LookMode.Value);
            // (An inspector who has left and been let go of is not a reference to keep.)
            if (Scribe.mode == LoadSaveMode.Saving && inspector != null && (inspector.Destroyed || inspector.Discarded)) inspector = null;
            Scribe_References.Look(ref inspector, "inspector");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                findings ??= new List<InspectionFinding>();
                hidden ??= new List<InspectionFinding>();
                findings.RemoveAll(f => f?.key == null);
                hidden.RemoveAll(f => f?.key == null);
                noticeKeys?.RemoveAll(k => k == null);
            }
        }
    }

    /// <summary>
    /// What the inspector writes down. A visit (<see cref="Inspections.Inspect"/>) reads the prison as it stands for what only the warden can put right, through the systems that already know: bodies lying outside the morgue
    /// (it is full, out of reach or not painted), a patch of the danger map no lamp lights, the nights thinly guarded, the structural gaps of the to-do list (<see cref="PrisonDiagnostics"/>: a room not painted, no stove,
    /// showers or toilets) and cells graded 0 for inmates who have earned better. What the prison's own routine clears (hunger, filth, a letter the warden has not seen) is not written. At most <see cref="MaxFindings"/>, the most
    /// pressing first, one cause to a finding, and only what the prison already had when the visit was announced (<see cref="Notice"/>: what is made for the notice is not read). They are lines of the to-do band until put right;
    /// the re-inspection <see cref="ReinspectDays"/> days after the visit pays <see cref="GrantPerFixed"/> for each one that is gone then and has stayed gone for the last <see cref="HoldDays"/> day(s), and fines
    /// <see cref="FinePerOpen"/> for each one that is not. A bribed inspector writes nothing down (the findings are kept apart, for the exposure letter to name).
    /// </summary>
    public static class InspectionReport
    {
        public const int MaxFindings = 5, ReinspectDays = 5, HoldDays = 1, GrantPerFixed = 400, FinePerOpen = 300;
        /// <summary>The night hours (from, to - not in the last) and the inmates one guard on the night is thought to cover.</summary>
        public const int NightFrom = 22, NightTo = 6, NightInmatesPerGuard = 20;
        public const int VisitHours = 6;

        public const string UnlitKey = "unlit", Grade0Key = "grade0", BodyKey = "body", NightKey = "night";
        /// <summary>
        /// A level whose timetable keeps its inmates in their cells (lockup and sleep) more than <see cref="MaxLockedHours"/> hours of the day is a finding, one per level, kept under "locked:" and the level's name: only the
        /// warden's regime can put it right. Not a level that is kept in its cells by design (supermax, death row: <see cref="LockedByDesign"/>), and not one nobody is in. The timetable is read, not today's orders.
        /// </summary>
        public const string LockedPrefix = "locked:";
        /// <summary>
        /// A level the warden kept in with day orders (the yard called off, an early lockup) on more than <see cref="RegimeOrders.HabitOver"/> of the last <see cref="RegimeOrders.HabitDays"/> days is a finding too, kept under "orders:" and
        /// the level's name: the orders are a way round the timetable's hours, and only the warden's regime can stop it. It is gone when the days of the last week fall to the limit again.
        /// </summary>
        public const string OrdersPrefix = "orders:";
        public const int MaxLockedHours = 20;
        /// <summary>
        /// Days added to sentences by the warden's adjudication sheet (see <see cref="Adjudication"/>) in the last week, over <see cref="Adjudication.WeekLimit"/>: a finding, kept under this key. Days added are days of pay
        /// from the state, and the state notices a warden who piles them on; only the warden's sheet can stop it, and it is gone when the week's days fall to the limit again, as the orders' habit is.
        /// </summary>
        public const string AddedKey = "added";
        /// <summary>The structural lines of the to-do list the inspector writes down are kept under "diag:" and the line's own key (<see cref="DiagKey"/>).</summary>
        public const string DiagPrefix = "diag:", NoZoneKey = "noZone:", NoStoveKey = "noStove", NoShowersKey = "noShowers", NoToiletsKey = "noToilets";

        public static string DiagKey(string itemKey) => DiagPrefix + itemKey;

        /// <summary>The guards on duty in each hour of the day a check sets in place of the roster's (null in a game).</summary>
        internal static int[] TestStrip;

        // ---- reading ----------------------------------------------------------------------------------------------------------------

        /// <summary>What the inspector writes down: the most pressing <see cref="MaxFindings"/> of <see cref="Candidates"/> that the prison also gave at the notice (all of them, when no notice was taken).</summary>
        public static List<InspectionFinding> Read(PrisonGame game, Map map)
        {
            List<string> noticed = game.story.inspection.noticeKeys;
            return Candidates(game, map).Where(f => noticed == null || noticed.Contains(f.key)).Take(MaxFindings).ToList();
        }

        /// <summary>The announcement: the kinds of finding the prison gives now are what a visit may write down (a room unpainted, a lamp or a rostered guard taken away for the visit is not one).</summary>
        public static void Notice(PrisonGame game, Map map) => game.story.inspection.noticeKeys = Candidates(game, map).Select(f => f.key).ToList();

        /// <summary>Every finding the prison gives now, the most pressing first (a body, a dark patch, the night, a gap in the rooms, a bare cell), each with the re-inspection's day as its deadline.</summary>
        public static List<InspectionFinding> Candidates(PrisonGame game, Map map)
        {
            var found = new List<InspectionFinding>();
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return found;
            int due = Find.TickManager.TicksGame + ReinspectDays * GenDate.TicksPerDay;
            void Add(string key, IntVec3 cell, int id) => found.Add(new InspectionFinding { key = key, cell = cell, id = id, dueTick = due });

            if (Morgue.Uncollected(map, pm, Morgue.StuckAfterTicks, out IntVec3 bodyAt) is var bodies and > 0) Add(BodyKey, bodyAt, bodies);
            if (DangerMap.Hottest(pm, int.MaxValue).FirstOrDefault(p => DangerMap.Unlit(pm, p, atNight: true)) is { } dark) Add(UnlitKey, dark.cell, 0);
            if (NightShort(map) is var needed and > 0) Add(NightKey, IntVec3.Invalid, needed);
            foreach ((SecurityLevelDef level, int hours) in LockedLevels(game, map)) Add(LockedPrefix + level.defName, IntVec3.Invalid, hours);
            foreach ((SecurityLevelDef level, int days) in RegimeOrders.HabitLevels(game, map)) Add(OrdersPrefix + level.defName, IntVec3.Invalid, days);
            if (Adjudication.DaysAddedInWeek(game) is var added and > Adjudication.WeekLimit) Add(AddedKey, IntVec3.Invalid, added);
            foreach (string key in OneCause(DiagKeys(map))) Add(DiagKey(key), IntVec3.Invalid, 0);
            (int bare, IntVec3 bareAt) = BareCells(pm);
            if (bare > 0) Add(Grade0Key, bareAt, bare);
            return found;
        }

        /// <summary>A level the regime keeps in the cells on purpose.</summary>
        public static bool LockedByDesign(SecurityLevelDef level) => level.escortOnly || level.cellMeals;

        /// <summary>The hours of the day the level's timetable locks it up (lockup and sleep).</summary>
        public static int LockedHours(PrisonGame game, SecurityLevelDef level) => game.RegimeFor(level).slots.Count(a => a.lockup);

        /// <summary>The levels with inmates in them whose timetable locks them up more than <see cref="MaxLockedHours"/> hours a day, the worst first, with the hours.</summary>
        public static List<(SecurityLevelDef level, int hours)> LockedLevels(PrisonGame game, Map map)
        {
            var found = new List<(SecurityLevelDef, int)>();
            foreach (SecurityLevelDef level in DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(l => l.uiOrder))
            {
                int hours = LockedHours(game, level);
                if (LockedByDesign(level) || hours <= MaxLockedHours) continue;
                if (map.mapPawns.PrisonersOfColonySpawned.Any(p => InmateUtility.IsActiveInmate(p) && p.Inmate().security == level)) found.Add((level, hours));
            }
            return found.OrderByDescending(f => f.Item2).ToList();
        }

        /// <summary>The keys of the structural lines of the to-do list now.</summary>
        private static List<string> DiagKeys(Map map) => PrisonDiagnostics.Items(map).Where(i => i.key != null).Select(i => i.key).ToList();

        /// <summary>One cause is one finding: no showers is not written beside a shower room not painted (the room is the cause; the fixtures are only looked for once it is there).</summary>
        public static List<string> OneCause(List<string> keys)
        {
            bool noRoom = keys.Contains(NoZoneKey + RPDefOf.RP_Shower.defName);
            return noRoom ? keys.Where(k => k != NoShowersKey).ToList() : keys;
        }

        /// <summary>How many guards the nights lack: the guards a night needs (one for every <see cref="NightInmatesPerGuard"/> inmates, at least one) when the fewest on duty in any night hour (<see cref="NightFrom"/> to <see cref="NightTo"/>, by the roster) are fewer; 0 when covered or there is nobody to guard.</summary>
        public static int NightShort(Map map)
        {
            int inmates = map.mapPawns.PrisonersOfColonySpawned.Count(InmateUtility.IsActiveInmate);
            if (inmates == 0) return 0;
            int needed = Mathf.Max(1, Mathf.CeilToInt(inmates / (float)NightInmatesPerGuard));
            return Roster.Fewest(TestStrip ?? Roster.Strip(map), NightFrom, NightTo) < needed ? needed : 0;
        }

        /// <summary>
        /// The cells graded 0 that someone lives in who is meant for better: with cells by conduct on, an inmate whose entitlement is above 0 (the ladder houses entitlement 0 in grade 0 on purpose); with it off, any inmate the ladder
        /// would cover. Not a solitary or a padded cell, which are bare on purpose, and an empty one is nobody's. The count is cells, not beds; and where the first stands.
        /// </summary>
        internal static (int count, IntVec3 first) BareCells(PrisonMap pm)
        {
            var rooms = new HashSet<Room>();
            IntVec3 first = IntVec3.Invalid;
            List<Building_Bed> beds = pm.PrisonerBeds;
            bool conduct = PrisonGame.Instance is { cellsByConduct: true };
            for (int i = 0; i < beds.Count; i++)
            {
                Building_Bed bed = beds[i];
                if (!bed.Spawned || CellQuality.GradeAt(bed) != 0 || PaddedCells.IsPadded(bed) || PunishmentUtility.IsSolitaryBed(bed) || bed.GetRoom() is not { } room || rooms.Contains(room)) continue;
                if (!bed.OwnersForReading.Any(o => o.Inmate() is { } c && CellQuality.UnderLadder(c) && (!conduct || c.entitlement > 0))) continue;
                if (rooms.Add(room) && rooms.Count == 1) first = bed.Position;
            }
            return (rooms.Count, first);
        }

        /// <summary>Whether the finding is still so now. <paramref name="diag"/>: the keys of the structural lines of the to-do list now, whether or not one is written for each (worked out once for a pass).</summary>
        public static bool Present(InspectionFinding f, PrisonGame game, Map map, HashSet<string> diag)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return false;
            switch (f.key)
            {
                case BodyKey: return Morgue.Uncollected(map, pm, Morgue.StuckAfterTicks) > 0;
                // (The patch the cell is in now, and whether no lamp lights it: not the patch's anchor, which moves when the zone is painted over, and not the daylight.)
                case UnlitKey: return f.cell.IsValid && DangerMap.Unlit(pm, DangerMap.PatchAt(pm, f.cell), atNight: true);
                case NightKey: return NightShort(map) > 0;
                case Grade0Key: return BareCells(pm).count > 0;
                case AddedKey: return Adjudication.DaysAddedInWeek(game) > Adjudication.WeekLimit;
            }
            if (f.key.StartsWith(LockedPrefix)) return LockedLevels(game, map).Any(l => l.level.defName == f.key.Substring(LockedPrefix.Length));
            if (f.key.StartsWith(OrdersPrefix)) return RegimeOrders.HabitLevels(game, map).Any(l => l.level.defName == f.key.Substring(OrdersPrefix.Length));
            return f.key.StartsWith(DiagPrefix) && diag.Contains(f.key.Substring(DiagPrefix.Length));
        }

        // ---- the book, hour by hour -------------------------------------------------------------------------------------------------

        /// <summary>The hourly look: each finding is marked gone from the hour it is no longer so (and open again the hour it comes back); the re-inspection when its day comes.</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            InspectionState st = game.story.inspection;
            Look(game, map);
            if (st.reinspectTick >= 0 && Find.TickManager.TicksGame >= st.reinspectTick) Reinspect(game, map);
        }

        /// <summary>One look at every finding in the book.</summary>
        public static void Look(PrisonGame game, Map map)
        {
            int now = Find.TickManager.TicksGame;
            HashSet<string> diag = null;
            foreach (InspectionFinding f in game.story.inspection.findings)
            {
                if (f.key.StartsWith(DiagPrefix)) diag ??= DiagKeys(map).ToHashSet();
                if (Present(f, game, map, diag)) f.goneSince = -1;
                else if (f.goneSince < 0) f.goneSince = now;
            }
        }

        /// <summary>Put right for the re-inspection: gone now (it was looked at this hour) and gone for the last <see cref="HoldDays"/> day(s), so that a thing put right at the last minute, or cleared by the prison's routine on the day, does not pay.</summary>
        public static bool Held(InspectionFinding f, int now) => f.Gone && now - f.goneSince >= HoldDays * GenDate.TicksPerDay;

        /// <summary>The re-inspection comes before its day (a dev incident starts another inspection with one pending): what the book says is settled first.</summary>
        public static void Settle(PrisonGame game, Map map)
        {
            Look(game, map);
            Reinspect(game, map);
        }

        /// <summary>The inspector comes back: a grant for each finding that is <see cref="Held"/>, a fine for each one not, and the book is closed.</summary>
        public static void Reinspect(PrisonGame game, Map map)
        {
            InspectionState st = game.story.inspection;
            List<InspectionFinding> list = st.findings;
            st.findings = new List<InspectionFinding>();
            st.reinspectTick = -1;
            int now = Find.TickManager.TicksGame;
            int right = list.Count(f => Held(f, now)), open = list.Count - right;
            int grant = right * GrantPerFixed, fine = open * FinePerOpen;
            if (grant > 0) game.ledger.Add(RPDefOf.RP_Grants, grant, "RP_InspectorGrantNote".Translate());
            if (fine > 0) game.ledger.Add(RPDefOf.RP_Fines, -fine, "RP_InspectorFineNote".Translate());
            st.inspector = SpecialMeasures.Visit(map, st.inspector, RPInspectorDefOf.RP_Inspector, VisitHours, Stops(map)) ?? st.inspector;
            string lines = string.Join("\n", list.Select(f => (Held(f, now) ? "RP_InspectorLineFixed" : "RP_InspectorLineOpen").Translate(Describe(f, map)).Resolve()));
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, "RP_Chr_Reinspection".Translate(right, open), st.inspector);
            Find.LetterStack.ReceiveLetter("RP_ReinspectionLabel".Translate(), "RP_ReinspectionText".Translate(lines, right, Money.Format(grant), open, Money.Format(fine)),
                open == 0 ? LetterDefOf.PositiveEvent : right == 0 ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent, st.inspector);
        }

        /// <summary>Trouble the inspector is not walked into: a riot, a hostage crisis or a lockdown.</summary>
        public static bool Trouble(Map map) => RiotPolice.RiotActive(map) || HostageCrisis.Running(map) != null || PrisonGame.Instance is { LockdownActive: true };

        /// <summary>The inspector's walk: a bed in a wing (an ordinary one: not supermax, protective custody, death row, the psychiatric wing or solitary, and none in trouble), the canteen, the kitchen and the yard.</summary>
        public static List<IntVec3> Stops(Map map)
        {
            var stops = new List<IntVec3>();
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return stops;
            Building_Bed wing = Trouble(map) ? null : pm.PrisonerBeds.Where(b => b.Spawned && IntakeSystem.WingOf(b) is not { wingOnly: true } && !PunishmentUtility.IsSolitaryBed(b) && !PaddedCells.IsPadded(b)).RandomElementWithFallback();
            if (wing != null) stops.Add(wing.InteractionCell);
            stops.AddRange(SpecialMeasures.ZoneStops(pm, RPDefOf.RP_Canteen, RPLaborDefOf.RP_Kitchen, RPDefOf.RP_Yard));
            return stops;
        }

        // ---- the words --------------------------------------------------------------------------------------------------------------

        /// <summary>The finding in a few words, as the report and the to-do line say it.</summary>
        public static string Describe(InspectionFinding f, Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            switch (f.key)
            {
                case BodyKey: return "RP_Finding_body".Translate(f.id).Resolve();
                case NightKey: return "RP_Finding_night".Translate(f.id).Resolve();
                case Grade0Key: return "RP_Finding_grade0".Translate(f.id).Resolve();
                case AddedKey: return "RP_Finding_added".Translate(f.id, Adjudication.WeekDays, Adjudication.WeekLimit).Resolve();
                case UnlitKey:
                    PrisonZoneDef zone = f.cell.IsValid && pm != null ? pm.zones.ZoneAt(f.cell) : null;
                    string place = zone == null ? "RP_DangerPlaceNone".Translate(f.cell.x, f.cell.z).Resolve() : "RP_DangerPlace".Translate(zone.label, f.cell.x, f.cell.z).Resolve();
                    return "RP_Finding_unlit".Translate(place).Resolve();
            }
            if (f.key.StartsWith(LockedPrefix))
                return "RP_Finding_locked".Translate(DefDatabase<SecurityLevelDef>.GetNamedSilentFail(f.key.Substring(LockedPrefix.Length))?.label ?? "?", f.id, 24 - f.id, 24 - MaxLockedHours).Resolve();
            if (f.key.StartsWith(OrdersPrefix))
                return "RP_Finding_orders".Translate(DefDatabase<SecurityLevelDef>.GetNamedSilentFail(f.key.Substring(OrdersPrefix.Length))?.label ?? "?", f.id, RegimeOrders.HabitDays, RegimeOrders.HabitOver).Resolve();
            string diag = f.key.StartsWith(DiagPrefix) ? f.key.Substring(DiagPrefix.Length) : f.key;
            if (diag.StartsWith(NoZoneKey)) return "RP_Finding_noZone".Translate(DefDatabase<PrisonZoneDef>.GetNamedSilentFail(diag.Substring(NoZoneKey.Length))?.label ?? "?").Resolve();
            return ("RP_Finding_" + diag).Translate().Resolve();
        }

        /// <summary>Where the to-do line opens.</summary>
        public static MainTabWindow_Prison.Tab TabOf(string key) => key == UnlitKey ? MainTabWindow_Prison.Tab.Deployment : key == NightKey ? MainTabWindow_Prison.Tab.Staff : key == AddedKey ? MainTabWindow_Prison.Tab.Policy : key.StartsWith(LockedPrefix) || key.StartsWith(OrdersPrefix) ? MainTabWindow_Prison.Tab.Regime : MainTabWindow_Prison.Tab.Rooms;

        /// <summary>The report's findings, one to a line with the days each has.</summary>
        public static string ReportLines(List<InspectionFinding> list, Map map)
        {
            int now = Find.TickManager.TicksGame;
            return string.Join("\n", list.Select(f => "RP_InspectorLine".Translate(Describe(f, map), DeadlineText(f, now)).Resolve()));
        }

        private static string DeadlineText(InspectionFinding f, int now) => Mathf.Max(1, Mathf.RoundToInt((f.dueTick - now) / (float)GenDate.TicksPerDay)) is var days && days == 1 ? "RP_InspectorOneDay".Translate().Resolve() : "RP_InspectorDays".Translate(days).Resolve();

        /// <summary>The letter's part about the findings: what was written down, with its deadlines; or that nothing was; or that nothing was, for a bribe.</summary>
        public static string ReportText(List<InspectionFinding> list, bool bribed, Map map) =>
            bribed ? "RP_InspectorReportBribed".Translate().Resolve()
            : list.Count == 0 ? "RP_InspectorReportNone".Translate().Resolve()
            : "RP_InspectorReport".Translate(ReportLines(list, map), ReinspectDays, Money.Format(GrantPerFixed), Money.Format(FinePerOpen)).Resolve();

        /// <summary>The findings that are still open, as lines of the to-do band, each with the time left to the re-inspection. Nothing while no re-inspection is pending.</summary>
        public static IEnumerable<PrisonDiagnostics.Item> Todo(Map map)
        {
            if (PrisonGame.Instance?.story.inspection is not { reinspectTick: >= 0 } st) yield break;
            int now = Find.TickManager.TicksGame;
            foreach (InspectionFinding f in st.findings)
            {
                if (f.Gone) continue;
                yield return new PrisonDiagnostics.Item("RP_InspectorTodo".Translate(Describe(f, map), TimeLeft(f, now)), TabOf(f.key));
            }
        }

        /// <summary>How long the finding has to the re-inspection as the to-do line says it: "overdue" past its deadline, else "2 days left". Plain text (the line is a plain string, the colour of its numbers stripped).</summary>
        public static string TimeLeft(InspectionFinding f, int now) => now > f.dueTick ? "RP_InspectorOverdue".Translate() : "RP_InspectorTimeLeft".Translate((f.dueTick - now).ToStringTicksToPeriod());
    }
}
