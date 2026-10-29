using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Plan "showcase": the state penitentiary, built by the scenario, run for a few days with the usual checks (this adds to them): every zone
    /// type painted, no placement problems, every inmate in a bed of their own wing, every room reachable, the cold store cold, the staff of every
    /// role at work and posted, nobody starving and the regime kept.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The table of rooms of the built facility: zone, interior size, floor, furniture (what stands inside), one log line each.</summary>
        private static void LogRoomTable(Map map)
        {
            void Log(string name, PrisonZoneDef zone, IEnumerable<IntVec3> cells, string size, string floor)
            {
                var things = new Dictionary<string, int>();
                foreach (IntVec3 c in cells)
                    foreach (Thing t in c.GetThingList(map))
                        if (t.def.category == ThingCategory.Building && t.Position == c && t is not Building_Door && t.def != ThingDefOf.Wall)
                            things[t.def.defName.Replace("RP_", "")] = things.TryGetValue(t.def.defName.Replace("RP_", ""), out int n) ? n + 1 : 1;
                RPLog.Message($"[RimPrison:rooms] {name} | {zone?.defName.Replace("RP_", "") ?? "-"} | {size} | {floor} | {string.Join(", ", things.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"))}");
            }
            foreach (PrisonBuilder.BuiltRoom room in PrisonBuilder.Rooms) Log(room.Name, room.Zone, room.Interior, $"{room.Interior.Width}x{room.Interior.Height}", room.Floor);
            if (PrisonBuilder.Rooms.Count > 0) return;
            // The inherited facility is laid out by hand, not from a table: the rooms the game found (named by where they are, local coordinates of the south-west corner).
            PrisonMap pm = PrisonMap.Of(map);
            IntVec3 origin = PrisonBuilder.Origin(map);
            var seen = new HashSet<Room>();
            foreach (IntVec3 c in PrisonBuilder.Footprint(map).ClipInsideMap(map))
                if (c.GetRoom(map) is { PsychologicallyOutdoors: false } room && seen.Add(room))
                {
                    CellRect box = CellRect.FromLimits(room.Cells.Min(x => x.x), room.Cells.Min(x => x.z), room.Cells.Max(x => x.x), room.Cells.Max(x => x.z));
                    PrisonZoneDef zone = room.Cells.Select(x => pm.zones.ZoneAt(x)).FirstOrDefault(z => z != null);
                    Log($"room at ({box.minX - origin.x},{box.minZ - origin.z})", zone, room.Cells, $"{box.Width}x{box.Height}", room.Cells.First().GetTerrain(map).defName);
                }
        }

        private static void ShowcaseEvaluate(Map map, PrisonGame game, int days, List<string> problems, float avgCompliance)
        {
            PrisonMap pm = PrisonMap.Of(map);
            void Fail(string what) => problems.Add("showcase: " + what);
            if (PrisonBuilder.Kind != Facility.Penitentiary) Fail("the plan did not start in the state penitentiary");

            // Every zone type the mod defines is painted somewhere (the penitentiary has them all, the burial plot behind the morgue too).
            var zoneLine = new List<string>();
            foreach (PrisonZoneDef z in DefDatabase<PrisonZoneDef>.AllDefsListForReading)
            {
                int cells = pm.zones.CellsOf(z).Count;
                zoneLine.Add($"{z.defName.Replace("RP_", "")}={cells}");
                if (cells == 0) Fail($"zone {z.defName} is painted nowhere");
            }
            RPLog.Message("[RimPrison:showcase] zones: " + string.Join(" ", zoneLine));

            LogRoomTable(map);
            CheckPsychPenitentiary(map, problems);

            // The supermax inmate the start sent to the unit: walked out and back by the real guards in the days of the run (the first day's shower at least: a violent one is hurt or put in the hole on the
            // days after, and misses its walks, which is the level working, not a fault).
            if (showcaseSupermax.Count > 0)
            {
                int Count(Dictionary<EscortKind, int> by, EscortKind k) => by.TryGetValue(k, out int n) ? n : 0;
                string walks = $"showers {Count(escortWalksDone, EscortKind.Shower)} done/{Count(escortWalksFailed, EscortKind.Shower)} cut short, yards {Count(escortWalksDone, EscortKind.Yard)} done/{Count(escortWalksFailed, EscortKind.Yard)} cut short";
                foreach (int id in showcaseSupermax)
                {
                    Pawn caged = map.mapPawns.AllPawns.FirstOrDefault(x => x.thingIDNumber == id);
                    RPLog.Message($"[RimPrison:showcase] supermax: {caged?.LabelShort ?? "(gone)"} is {caged?.Inmate()?.security?.defName}, in their cell {caged != null && EscortWalk.InOwnCell(caged)}, yard slot {caged?.Inmate()?.yardSlot}, hygiene {caged?.needs?.TryGetNeed(RPDefOf.RP_Hygiene)?.CurLevel:F2}");
                }
                RPLog.Message($"[RimPrison:showcase] supermax walks: {walks}, fetched home {Count(escortWalksDone, SupermaxHomeWalk)} done/{Count(escortWalksFailed, SupermaxHomeWalk)} cut short, the longest the inmate was outside the cell with no guard: {showcaseSupermaxStrandedMax} hourly looks ({showcaseSupermaxLooksSkipped} more with every guard busy)");
                if (Count(escortWalksDone, EscortKind.Shower) + Count(escortWalksDone, EscortKind.Yard) == 0)
                    Fail($"the supermax inmate was not walked to a shower or to the yard in {days} days ({walks})");
                // The inmate was sent to the unit from the maximum wing (so they start the run outside their new cell, and the unit's door across the corridor is staff-only): a guard fetched them.
                // (Vanilla's own escort to the bed, by a guard, is a walk home too: it took the inmate to the new cell before our fetch came, once in a census run.)
                if (Count(escortWalksDone, SupermaxHomeWalk) == 0 && !showcaseSupermaxInByEscort) Fail($"the supermax inmate who was moved in from another wing was never walked to their cell by a guard ({walks}; at the start: {showcaseSupermaxAtStart}; {(showcaseSupermaxFirstIn == "" ? "never seen in the cell at an hourly look" : showcaseSupermaxFirstIn)})");
                // And never left outside the cell with nobody for long (a guard is free within the hour or two, whoever else needs one).
                if (showcaseSupermaxStrandedMax >= 4) Fail($"a supermax inmate was outside their cell with no guard for {showcaseSupermaxStrandedMax} hourly looks running ({showcaseSupermaxStrandedWhy})");
            }

            // The care reserve (one guard kept for care for the whole run, see ShowcaseStart): the longest a care request waited for a member of staff to take it, and the longest while an inmate was acting out or owed a restraint (the
            // run's riots). Told, not judged: a night of riot with every guard on the one who is down or berserk is the documented limit, and this is the number to read it by. The queue is worked out at the map's beat, so a wait is
            // seen to within 250 ticks.
            if (CareDetail.Of(map) is { } care)
            {
                IReadOnlyList<CareRequest> waiting = care.Queue;
                RPLog.Message($"[RimPrison:showcase] care detail: reserve {care.size} ({string.Join(", ", care.Members.Select(m => m.LabelShort))}), the longest a care request waited for a member of staff {care.longestWait / (float)GenDate.TicksPerHour:F1} h, "
                              + $"{care.longestWaitInTrouble / (float)GenDate.TicksPerHour:F1} h of it while an inmate was acting out or owed a restraint; riots {game.stats.riots}; waiting now {care.WaitingCount}"
                              + (care.WaitingCount > 0 ? $" ({string.Join(", ", waiting.Where(q => !q.served).Take(6).Select(q => q.kind + " " + q.inmate.LabelShort))})" : ""));
            }

            // Where the food is (a diagnostic: the cooks walk to their ingredients).
            foreach (string foodDef in new[] { "RawPotatoes", "RawRice", "MealSimple" })
            {
                ThingDef fd = DefDatabase<ThingDef>.GetNamedSilentFail(foodDef);
                if (fd == null) continue;
                var where = new Dictionary<string, int>();
                foreach (Thing t in map.listerThings.ThingsOfDef(fd))
                {
                    string place = t.Spawned ? PrisonBuilder.Rooms.Where(r => r.Interior.Contains(t.Position)).Select(r => r.Name).FirstOrDefault() ?? (PrisonBuilder.Footprint(map).Contains(t.Position) ? "in the compound" : "outside") : "carried";
                    where[place] = where.TryGetValue(place, out int n) ? n + t.stackCount : t.stackCount;
                }
                RPLog.Message($"[RimPrison:showcase] {foodDef}: {string.Join(", ", where.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
            }

            // Every inmate lives in a bed of their own wing (at the start strictly, see ShowcaseStart; later an inmate promoted or demoted waits for the next transfer).
            int homeless = 0, wrongWing = 0, stalledNoBed = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = p.Inmate();
                if (c == null || p.guest.Released) continue;
                Building_Bed bed = p.ownership?.OwnedBed;
                if (c.forcedActivity != null) continue; // (serving a punishment: in the hole, or locked up in whatever cell was free)
                if (p.InBed() && p.CurrentBed() is { Medical: true }) continue; // (a patient in the infirmary)
                if (bed == null)
                {
                    // (Counted only when a bed of their level and sex was free at the hourly looks, and for a few of them in a row, not just now: a riot breaks beds, an inmate whose bed went in the last hour or two has not had the
                    // hourly pass that gives them another yet, and with every spare bed of the wing destroyed there is nothing for that pass to give until the builders are done. That is the product working; the check is for
                    // the inmate nobody gives a bed to when there is one. See ShowcaseBedlessWatch.)
                    int looks = showcaseBedlessLooks.TryGetValue(p.thingIDNumber, out int n) ? n : 0;
                    int stalled = showcaseBedlessStalled.TryGetValue(p.thingIDNumber, out int stalledSoFar) ? stalledSoFar : 0;
                    bool couldHave = IntakeSystem.FreeBedCount(map, c.security, gender: p.gender) > 0;
                    if (looks >= BedlessLooksToCount) homeless++;
                    if (stalled >= StalledLooksToFail) stalledNoBed++;
                    string why = looks >= BedlessLooksToCount ? "" : couldHave ? $" (counted from {BedlessLooksToCount})" : $" (excused: no bed free for {c.security?.defName}/{p.gender}; {Patch_Building_Bed_DeSpawn_Log.Destroyed} prisoner beds destroyed this run, {stalled} hourly looks without one and none free)";
                    RPLog.Message($"[RimPrison:showcase] no bed: {p.LabelShort} ({c.security?.defName}) at {p.Position - PrisonBuilder.Origin(map)} job {p.CurJobDef?.defName} processed {c.processed}, without one while one was free for {looks} hourly looks{why}");
                    continue;
                }
                if (PunishmentUtility.IsSolitaryBed(bed)) continue;
                SecurityLevelDef wing = IntakeSystem.WingOf(bed);
                if (wing != null && wing != c.security || wing == null && c.security.wingOnly)
                {
                    wrongWing++;
                    RPLog.Message($"[RimPrison:showcase] wrong wing: {p.LabelShort} is {c.security?.defName}, bed at {bed.Position - PrisonBuilder.Origin(map)} in the {wing?.defName ?? "no"} wing (reclassified since the start?)");
                }
            }
            if (homeless > 2) Fail($"{homeless} inmates without a bed (a bed of their level and sex free for {BedlessLooksToCount}+ hourly looks)");
            if (stalledNoBed > 0) Fail($"{stalledNoBed} inmates without a bed for {StalledLooksToFail}+ hourly looks with none free for them: the beds a riot destroyed ({Patch_Building_Bed_DeSpawn_Log.Destroyed} this run) were not rebuilt");
            if (wrongWing > 3) Fail($"{wrongWing} inmates in a bed of another wing");
            var perLevel = DefDatabase<SecurityLevelDef>.AllDefsListForReading
                .Select(l => $"{l.defName.Replace("RP_", "")}={map.mapPawns.PrisonersOfColonySpawned.Count(x => x.Inmate()?.security == l)} (free beds {IntakeSystem.FreeBedCount(map, l)})");
            RPLog.Message($"[RimPrison:showcase] inmates by level: {string.Join(" ", perLevel)} (beds free: {IntakeSystem.FreePrisonerBeds(map).Count}; prisoner beds destroyed this run {Patch_Building_Bed_DeSpawn_Log.Destroyed})");

            // From their beds, can inmates walk to a counter (every door that is not staff-only or locked counts as open, cell doors too: they follow the regime)?
            {
                List<IntVec3> counters = pm.zones.CellsOf(RPDefOf.RP_Canteen).Where(c => c.GetEdifice(map)?.def.defName == "RP_ServingCounter").ToList();
                var cut = new List<string>();
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                {
                    Building_Bed bed = p.ownership?.OwnedBed;
                    if (bed == null || p.Inmate() is not { } c || c.forcedActivity != null || c.security.cellMeals || PunishmentUtility.IsSolitaryBed(bed)) continue; // (just out of the hole: they own its bed for a while)
                    if (!counters.Any(cc => OpenWalk(map, bed.Position, cc))) cut.Add($"{p.LabelShort} ({c.security?.defName.Replace("RP_", "")})");
                }
                RPLog.Message($"[RimPrison:showcase] inmates who cannot walk from their bed to a counter: {(cut.Count == 0 ? "none" : string.Join(", ", cut))}");
                if (cut.Count > 0) Fail($"{cut.Count} inmates cannot walk from their bed to a counter: {string.Join(", ", cut)}");
            }

            // Every room can be reached: staff from the gatehouse, inmates from holding wherever the room is open to them.
            IntVec3 gate = PrisonBuilder.Origin(map) + new IntVec3(59, 0, 6);
            List<IntVec3> holding = pm.zones.CellsOf(RPDefOf.RP_Holding);
            var unreachable = new List<string>();
            foreach (PrisonBuilder.BuiltRoom room in PrisonBuilder.Rooms)
            {
                IntVec3 cell = room.Interior.Cells.Where(c => c.Standable(map)).OrderBy(c => c.DistanceToSquared(room.Interior.CenterCell)).FirstOrDefault();
                if (!cell.IsValid) { Fail($"room {room.Name} has no cell to stand in"); continue; }
                if (!map.reachability.CanReach(gate, cell, Verse.AI.PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors))) unreachable.Add(room.Name + " (staff)");
                if (room.Zone is { inmateAccess: true, staffOnly: false } && holding.Count > 0 && !InmateReachability.CanInmateWalk(map, holding[holding.Count / 2], cell)) unreachable.Add(room.Name + " (inmates)");
            }
            if (unreachable.Count > 0) Fail("rooms not reachable: " + string.Join(", ", unreachable));

            // The prison's own list of what it lacks (counters, seats, showers, cooks, a bed within a walk of both, a gym, a library).
            foreach (string line in PrisonServices.Lines(map)) Fail("services: " + line);

            // The cold store is cold, everything that wants power has it, the cameras watch.
            PrisonBuilder.BuiltRoom? freezer = PrisonBuilder.Rooms.Where(r => r.Name == "Freezer").Select(r => (PrisonBuilder.BuiltRoom?)r).FirstOrDefault();
            if (freezer is { } f)
            {
                float temp = f.Interior.CenterCell.GetTemperature(map);
                RPLog.Message($"[RimPrison:showcase] freezer {temp:F1}C, outside {map.mapTemperature.OutdoorTemp:F1}C");
                if (temp > 5f) Fail($"the freezer is at {temp:F1}C");
            }
            int unpowered = map.listerBuildings.allBuildingsColonist.Count(b => b.GetComp<CompPowerTrader>() is { PowerOn: false } && FlickUtility.WantsToBeOn(b) && !b.IsBrokenDown());
            if (unpowered > 0 && !game.BlackoutActive) Fail($"{unpowered} buildings without power");
            RPLog.Message($"[RimPrison:showcase] cameras {pm.Cameras.Count(c => c.Working)}/{pm.Cameras.Count}, towers {map.listerBuildings.AllBuildingsColonistOfDef(RPArmedDefOf.RP_GuardTower).Count()}, lamps {map.listerBuildings.AllBuildingsColonistOfDef(DefDatabase<ThingDef>.GetNamed("StandingLamp")).Count()}");

            // Staff: every role the prison uses is at work, the guards are posted.
            var roles = StaffCommute.AllStaff(map).GroupBy(s => s.GetComp<CompStaff>().role.defName).ToDictionary(g => g.Key, g => g.Count());
            RPLog.Message("[RimPrison:showcase] staff: " + string.Join(" ", roles.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key.Replace("RP_", "")}={kv.Value}")));
            int posted = StaffCommute.AllStaff(map).Count(s => s.GetComp<CompStaff>() is { role.defName: "RP_Guard", patrolZone: not null });
            RPLog.Message($"[RimPrison:showcase] guards with a post: {posted}");
            if (posted < 6) Fail($"only {posted} guards have a post in Deployment");

            // Beauty: what the buildings and floors add up to (a diagnostic).
            {
                var beauty = new Dictionary<string, (int n, float each)>();
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    float v = b.GetStatValue(StatDefOf.Beauty);
                    if (Math.Abs(v) < 0.5f) continue;
                    beauty[b.def.defName] = beauty.TryGetValue(b.def.defName, out (int n, float each) e) ? (e.n + 1, v) : (1, v);
                }
                RPLog.Message("[RimPrison:showcase] beauty of buildings (count x each): " + string.Join(", ", beauty.OrderBy(kv => kv.Value.n * kv.Value.each).Take(8).Select(kv => $"{kv.Key} {kv.Value.n}x{kv.Value.each:F1}")) + " | best: " + string.Join(", ", beauty.OrderByDescending(kv => kv.Value.n * kv.Value.each).Take(4).Select(kv => $"{kv.Key} {kv.Value.n}x{kv.Value.each:F1}")));
                RPLog.Message("[RimPrison:showcase] floors: " + string.Join(", ", new[] { "Concrete", "PavedTile", "TileGranite", "SterileTile", "WoodPlankFloor", "Gravel", "Soil" }.Select(n => DefDatabase<TerrainDef>.GetNamedSilentFail(n)).Where(t => t != null).Select(t => $"{t.defName} {t.GetStatValueAbstract(StatDefOf.Beauty):F1}/{t.GetStatValueAbstract(StatDefOf.BeautyOutdoors):F1}")));
            }

            // What weighs on the mood: the thoughts that count most, over the staff and over the inmates (a diagnostic).
            foreach ((string who, IEnumerable<Pawn> pawns) in new[] { ("staff", (IEnumerable<Pawn>)StaffCommute.AllStaff(map)), ("inmates", map.mapPawns.PrisonersOfColonySpawned) })
            {
                var sum = new Dictionary<string, float>();
                int count = 0;
                var thoughts = new List<Thought>();
                foreach (Pawn p in pawns.ToList())
                {
                    if (p.needs?.mood == null) continue;
                    count++;
                    thoughts.Clear();
                    p.needs.mood.thoughts.GetAllMoodThoughts(thoughts);
                    foreach (Thought t in thoughts)
                    {
                        float v = t.MoodOffset();
                        if (Math.Abs(v) < 0.5f) continue;
                        string label = t.LabelCap + (t.CurStage != null && t.def.stages?.Count > 1 ? " (" + t.CurStage.label + ")" : "");
                        sum[label] = sum.TryGetValue(label, out float old) ? old + v : v;
                    }
                }
                RPLog.Message($"[RimPrison:showcase] mood of {who} ({count}): " + string.Join(", ", sum.OrderBy(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {kv.Value / Math.Max(1, count):F1}")) + " | best: " + string.Join(", ", sum.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} {kv.Value / Math.Max(1, count):F1}")));
            }

            // Regime kept and nobody lost in the first days.
            foreach (KeyValuePair<string, string> kv in starvingInmates) RPLog.Message($"[RimPrison:showcase] starved: {kv.Key}: {kv.Value}");
            (int starvedInmates, int starvedHours) = starvingAtDay3.inmates >= 0 ? starvingAtDay3 : (starvingInmates.Count, starvingHours);
            RPLog.Message($"[RimPrison:showcase] starving: {starvingInmates.Count} inmates, {starvingHours} inmate-hours ({starvedInmates} inmates, {starvedHours} inmate-hours in the first three days)");
            // (An inmate in the hole or on death row eats what a cook or an officer brings, and one carried to the infirmary at night waits for the night shift: a few hours of an empty
            // stomach in the first days is the mod's own, a prison where it is the rule is a broken one. Past the third day unrest takes over: riots, lockdowns, a quarter of the inmates
            // in the hole, as in the 48-cell benchmark's rate of misconduct; reported, not judged.)
            if (starvedHours > 12 || starvedInmates > 3) Fail($"starving in the first three days: {starvedInmates} inmates, {starvedHours} inmate-hours");
            if (avgCompliance < 0.6f) Fail($"compliance with the regime {avgCompliance:P0}");
            if (game.stats.escaped > 0) Fail($"escapes {game.stats.escaped} in the first days");
            RPLog.Message($"[RimPrison:showcase] first days: escapes {game.stats.escaped}, deaths {game.stats.deaths}, riots {game.stats.riots}, misconduct {game.stats.misconducts}, punished {game.stats.punishments}");

            // What the Reports and Intel tabs show: the systems that ran.
            PrisonStats st = game.stats;
            RPLog.Message($"[RimPrison:showcase] systems: laundry={game.laundry.washed} letters={st.lettersDelivered} visits={st.visits} prayers={st.prayers} therapy={st.therapySessions} " +
                          $"programsPassed={st.programsPassed} misconduct={st.misconducts} contraband={st.contrabandFound} chronicle={game.story.entries.Count} grade={game.gradeHistory.LastOrDefault():F0}");
            if (game.laundry.washed == 0) Fail("nobody did laundry");
            if (days >= 3 && st.lettersDelivered == 0) Fail("no letter was delivered");
        }

        /// <summary>A flood fill like <see cref="InmateReachability"/>, but with every door open to inmates unless it is staff-only or locked (the regime's own closing of cell doors is not a layout fault).</summary>
        private static bool OpenWalk(Map map, IntVec3 from, IntVec3 to)
        {
            var seen = new HashSet<IntVec3> { from };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                IntVec3 c = queue.Dequeue();
                for (int k = 0; k < 4; k++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[k];
                    if (!n.InBounds(map) || seen.Contains(n)) continue;
                    if (n == to) return true;
                    if (!n.Walkable(map)) continue;
                    if (n.GetEdifice(map) is Building_Door door && door.GetComp<CompPrisonDoor>() is { } d
                        && (d.mode is PrisonDoorMode.StaffOnly or PrisonDoorMode.Locked || d.sideA is DoorSide.Staff or DoorSide.Outside || d.sideB is DoorSide.Staff or DoorSide.Outside)) continue;
                    seen.Add(n);
                    queue.Enqueue(n);
                }
            }
            return false;
        }

        /// <summary>At the start: every inmate has a bed in the right wing, the staff are in, every room is built (checked before the clock runs).</summary>
        /// <summary>The inmates the showcase plan sent to supermax at the start, and the escort walks the guards finished in the run, by kind (see <see cref="Notify_EscortWalk"/>).</summary>
        private static readonly List<int> showcaseSupermax = new List<int>();
        private const EscortKind SupermaxHomeWalk = (EscortKind)3;
        private static int showcaseSupermaxStranded, showcaseSupermaxStrandedMax;
        private static string showcaseSupermaxStrandedWhy = "";
        /// <summary>The supermax inmate of the start, as the start left them and at the hourly look before they were first seen in their cell (for the line of a run with no walk home).</summary>
        private static string showcaseSupermaxAtStart = "", showcaseSupermaxLastOut = "", showcaseSupermaxFirstIn = "";
        /// <summary>At the look before they were first seen in the cell, a member of staff had them reserved on a walk or a carry to a bed (vanilla's own escort to the bed, or ours).</summary>
        private static bool showcaseSupermaxLastOutEscorted, showcaseSupermaxInByEscort;
        private static readonly HashSet<string> EscortJobs = new HashSet<string> { "EscortPrisonerToBed", "TakeWoundedPrisonerToBed", "RP_EscortInmate" };

        /// <summary>
        /// Every second of the showcase run until the supermax inmate of the start is first seen in their cell: where they are, on what, and who has them reserved on what (the hourly look missed the
        /// first hour, in which a run had them in the cell with no walk of ours and nothing seen before it).
        /// </summary>
        private static void ShowcaseSupermaxTrackIn(Map map)
        {
            if (showcaseSupermaxFirstIn != "" || Find.TickManager.TicksGame % 60 != 0) return;
            foreach (int id in showcaseSupermax)
                if (showcaseSupermaxFirstIn == "" && map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == id) is { } sent)
                {
                    Pawn reserver = map.reservationManager.FirstRespectedReserver(sent, sent);
                    string state = $"{sent.Position} on {sent.CurJobDef?.defName ?? "no job"} (leg {sent.Inmate()?.leg?.kind.ToString() ?? "none"}, carried by {sent.CarriedBy?.LabelShort ?? "nobody"}, reserved by {reserver?.LabelShort ?? "nobody"} on {reserver?.CurJobDef?.defName ?? "-"}, tick {Find.TickManager.TicksGame}, hour {GenLocalDate.HourOfDay(map)})";
                    if (EscortWalk.InOwnCell(sent))
                    {
                        showcaseSupermaxFirstIn = $"first seen in the cell {state}, walks home done by then {(escortWalksDone.TryGetValue(SupermaxHomeWalk, out int n) ? n : 0)}, the look before: {showcaseSupermaxLastOut}";
                        showcaseSupermaxInByEscort = showcaseSupermaxLastOutEscorted;
                    }
                    else
                    {
                        showcaseSupermaxLastOut = state;
                        showcaseSupermaxLastOutEscorted = reserver?.GetComp<CompStaff>()?.role != null && reserver.CurJob is { } rj && EscortJobs.Contains(rj.def.defName) && rj.targetA.Thing == sent;
                    }
                }
        }

        /// <summary>Every hour of the showcase run: how many looks in a row a supermax inmate was outside their cell with no guard (and no reason: not a patient, not in the hole).</summary>
        private static void ShowcaseSupermaxWatch(Map map)
        {
            bool stranded = false;
            string why = "";
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { security: { escortOnly: true }, leg: null, forcedActivity: null } sc && p.ownership?.OwnedBed != null && !EscortWalk.InOwnCell(p) && !p.Downed && !p.InBed() && !p.InMentalState && !HealthAIUtility.ShouldSeekMedicalRest(p))
                {
                    stranded = true;
                    // (What the product says about them, for the look that fails: why no guard comes for one who is out of their cell.)
                    why = $"{p.LabelShort} at {p.Position} on {p.CurJobDef?.defName}: stranded {EscortWalk.Stranded(p, sc)}, due {SupermaxSystem.DueFor(p, sc, PrisonGame.Instance)?.ToString() ?? "nothing"}, retry in {sc.escortRetryTick - Find.TickManager.TicksGame} ticks, awake {p.Awake()}, awaiting a search {sc.awaitingSearch}, "
                          + $"processed {sc.processed}, punishment due {sc.pendingPunishmentHours} h, candidate {SupermaxSystem.Candidate(p)}, in the escort list {InmateCandidates.Shared.Escort(map).Contains(p)}, reserved by {map.reservationManager.FirstRespectedReserver(p, p)?.LabelShort ?? "nobody"}, "
                          + $"guards on an escort {map.mapPawns.FreeColonistsSpawned.Count(g => g.CurJobDef == RPSupermaxDefOf.RP_EscortInmate)}, {SupermaxStrandedGuards(map, p, sc)}";
                }
            // (A look counts only when a guard could have come: one awake and on site who is not in the middle of something that cannot wait (restraining someone, carrying the wounded, an escort of
            // their own). A night of riot, with every guard on the one who is down or berserk, is not the fetch failing: the inmate waits their turn, and the look is neither counted nor forgiven.)
            if (stranded && !PrisonMap.Of(map).Guards.Any(g => g.Spawned && !g.Downed && g.Awake() && ProgramSystem.CanInterrupt(g)))
            {
                showcaseSupermaxLooksSkipped++;
                return;
            }
            showcaseSupermaxStranded = stranded ? showcaseSupermaxStranded + 1 : 0;
            if (stranded) RPLog.Message($"[RimPrison:showcase] supermax inmate outside the cell, look {showcaseSupermaxStranded}: {why}");
            if (showcaseSupermaxStranded > showcaseSupermaxStrandedMax) showcaseSupermaxStrandedWhy = why;
            showcaseSupermaxStrandedMax = Math.Max(showcaseSupermaxStrandedMax, showcaseSupermaxStranded);
        }

        /// <summary>What the free guards make of a stranded inmate (the line of the look that fails): can they reach them, are they offered the walk, the inmate's room and its doors.</summary>
        private static string SupermaxStrandedGuards(Map map, Pawn p, CompInmate sc)
        {
            InmateCandidates.Shared.Invalidate();
            var giver = new WorkGiver_EscortInmate();
            IEnumerable<Pawn> guards = PrisonMap.Of(map).Guards.Where(g => g.Spawned && !g.Downed && g.Awake()).Take(40);
            int free = 0, reach = 0, reserve = 0, offered = 0, subdue = 0;
            foreach (Pawn g in guards)
            {
                if (!ProgramSystem.CanInterrupt(g)) continue;
                free++;
                if (g.CanReach(p, PathEndMode.Touch, Danger.Deadly)) reach++;
                if (g.CanReserveAndReach(p, PathEndMode.Touch, Danger.Deadly)) reserve++;
                if (giver.HasJobOnThing(g, p)) offered++;
                if (GuardUtility.CanRespondTo(g, p)) subdue++;
            }
            Room room = p.GetRoom();
            string doors = room == null ? "no room" : string.Join(";", room.ContainedAndAdjacentThings.OfType<Building_Door>().Distinct().Select(d => $"{d.Position}:{d.GetComp<CompPrisonDoor>()?.mode}"));
            // (The guards on an escort: where they are, whether they are walking and where to, which toil, how far from whom they are after.)
            string escorts = string.Join("; ", map.mapPawns.FreeColonistsSpawned.Where(g => g.CurJobDef == RPSupermaxDefOf.RP_EscortInmate).Select(g =>
                $"{g.LabelShort} at {g.Position} for {g.CurJob.targetA.Thing?.LabelShort} at {g.CurJob.targetA.Thing?.Position}, {g.Position.DistanceTo(g.CurJob.targetA.Cell):F0} apart, kind {(EscortKind)g.CurJob.count}, toil {(g.jobs.curDriver as JobDriver_EscortInmate)?.CurToilIndex}, moving {g.pather.Moving} to {g.pather.Destination.Cell}, reach {g.CanReach(g.CurJob.targetA, PathEndMode.Touch, Danger.Deadly)}"));
            int striking = map.mapPawns.FreeColonistsSpawned.Count(IncidentWorker_StaffStrike.Striking);
            return $"escorts [{escorts}], striking staff {striking}, riot {RiotPolice.RiotOngoing(map)}, lockdown {PrisonGame.Instance.LockdownActive}, free guards {free}, reach {reach}, reserve+reach {reserve}, offered the escort {offered}, may subdue {subdue}, home cell {EscortWalk.HomeCell(p)} reachable {p.Map != null && EscortWalk.HomeCell(p).IsValid && PrisonMap.Of(map).Guards.FirstOrDefault(g => g.Spawned)?.CanReach(EscortWalk.HomeCell(p), PathEndMode.OnCell, Danger.Deadly) == true}, "
                   + $"leg {sc.leg != null}, forced {sc.forcedActivity?.defName ?? "none"}, room {room?.CellCount} cells zone {PrisonMap.Of(map).zones.ZoneAt(p.Position)?.defName ?? "none"} doors [{doors}], hediffs {string.Join(",", p.health.hediffSet.hediffs.Select(h => h.def.defName))}, pawn jobs {p.jobs?.curDriver?.GetType().Name}";
        }

        /// <summary>The hourly looks at a stranded supermax inmate that did not count, with every guard busy (reported).</summary>
        private static int showcaseSupermaxLooksSkipped;
        private static readonly Dictionary<EscortKind, int> escortWalksDone = new Dictionary<EscortKind, int>(), escortWalksFailed = new Dictionary<EscortKind, int>();

        /// <summary>A guard's escort walk ended (the job's finish action): counted by kind, done or not.</summary>
        public static void Notify_EscortWalk(EscortKind kind, JobCondition condition)
        {
            Dictionary<EscortKind, int> by = condition == JobCondition.Succeeded ? escortWalksDone : escortWalksFailed;
            by[kind] = by.TryGetValue(kind, out int n) ? n + 1 : 1;
        }

        private static void ShowcaseStart(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = p.Inmate();
                Building_Bed bed = p.ownership?.OwnedBed;
                if (bed == null) { problems0.Add($"showcase: {p.LabelShort} has no bed at the start"); continue; }
                SecurityLevelDef wing = IntakeSystem.WingOf(bed);
                if (wing != null && wing != c.security || wing == null && c.security.wingOnly) problems0.Add($"showcase: {p.LabelShort} ({c.security?.defName}) starts in a bed of the {wing?.defName ?? "no"} wing");
            }
            if (map.mapPawns.PrisonersOfColonySpawnedCount < 38) problems0.Add($"showcase: only {map.mapPawns.PrisonersOfColonySpawnedCount} inmates at the start");
            var startRoles = StaffCommute.AllStaff(map).Select(sp => sp.GetComp<CompStaff>().role.defName).ToHashSet();
            foreach (string role in new[] { "RP_Guard", "RP_ArmedGuard", "RP_DogHandler", "RP_Cook", "RP_Workman", "RP_Janitor", "RP_Doctor", "RP_Gardener", "RP_Administrator", "RP_Counsellor", "RP_Psychologist", "RP_Chaplain" })
                if (!startRoles.Contains(role)) problems0.Add($"showcase: no {role} on the staff at the start");
            // The plan opts in to supermax: the calmest maximum security inmate is sent to the unit (the warden's order), and the run judges their escorted walks on the real floor. (One: the
            // worst of them fight, are put in the hole and carried to the ward, and each of those is work for the guards, the cooks and the doctors that the plan's starvation check feels.)
            CheckDownedStaffHaveABed(map);
            // One guard kept for care for the run (the showcase's riots are the test of it; see ShowcaseEvaluate for the number).
            CareDetail.Of(map)?.SetSize(1);
            showcaseSupermax.Clear();
            (showcaseSupermaxStranded, showcaseSupermaxStrandedMax, showcaseSupermaxLooksSkipped, showcaseSupermaxStrandedWhy) = (0, 0, 0, "");
            (showcaseSupermaxAtStart, showcaseSupermaxLastOut, showcaseSupermaxFirstIn) = ("", "", "");
            (showcaseSupermaxLastOutEscorted, showcaseSupermaxInByEscort) = (false, false);
            foreach (Pawn caged in map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { security.defName: "RP_Maximum", forcedActivity: null }).OrderBy(x => x.Inmate().tension).Take(1).ToList())
            {
                if (SupermaxSystem.TrySend(caged, caged.Inmate(), SupermaxReason.Warden))
                {
                    showcaseSupermax.Add(caged.thingIDNumber);
                    CompInmate cc = caged.Inmate();
                    showcaseSupermaxAtStart = $"{caged.LabelShort} at {caged.Position} on {caged.CurJobDef?.defName ?? "no job"}, in their new cell {EscortWalk.InOwnCell(caged)}, the bed at {caged.ownership?.OwnedBed?.Position}, stranded {EscortWalk.Stranded(caged, cc)}, due {SupermaxSystem.DueFor(caged, cc, PrisonGame.Instance)?.ToString() ?? "nothing"}";
                    RPLog.Message($"[RimPrison:showcase] supermax at the start: {showcaseSupermaxAtStart}");
                }
                else problems0.Add($"showcase: {caged.LabelShort} could not be sent to the supermax unit ({SupermaxSystem.CannotSend(caged, caged.Inmate())})");
            }
            if (showcaseSupermax.Count == 0) problems0.Add("showcase: no maximum security inmate was sent to the supermax unit");
            RPLog.Message($"[RimPrison:showcase] start: {map.mapPawns.PrisonersOfColonySpawnedCount} inmates, {map.mapPawns.FreeColonistsSpawnedCount} staff, {PrisonBuilder.Rooms.Count} rooms, funds {PrisonGame.Instance.ledger.funds}");
        }
    }
}
