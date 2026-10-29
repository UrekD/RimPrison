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
    /// Checks for supermax (Security/Supermax.cs, Security/SupermaxEscort.cs) in the events plan: the level and its regime, a cell that is locked in every hour, never the canteen, the meal brought to the cell, the
    /// walk to the shower and the yard alone (real ticks, a real guard on a real job), who is sent there, the long stay and the step-down letter. Each is self-contained: it builds a small unit on open ground
    /// outside the compound (<see cref="SupermaxScene"/>: two supermax cells, a plain cell, a shower room, a yard of its own, a corridor, all walled and roofed), uses throwaway inmates and guards, and puts back
    /// everything it touched (<see cref="Rollback"/>). Each fails without the feature it is named for: they look at what only the feature does.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckSupermax(Map map)
        {
            Rand.PushState(20261031);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns);
            string buildingsBefore = SupermaxBuildingsDescribe(map);
            string settingsBefore = SupermaxSettingsDescribe();
            try
            {
                CheckSupermaxDefs(map);
                CheckSupermaxCellsAreLockedInEveryHour(map);
                CheckSupermaxNeverInTheCanteenAndTheMealComesToTheCell(map);
                CheckSupermaxEscortedToTheShower(map);
                CheckSupermaxYardAlone(map);
                CheckSupermaxLockdownBringsThemBack(map);
                CheckSupermaxWhoGoesThere(map);
                CheckSupermaxLongStayAndStepDown(map);
                CheckSupermaxFixes(map);
            }
            finally
            {
                Rand.PopState();
            }
            // (Their pawns are the ones with a single name that starts with a Z; the real ticks let the prison's own shuttle land and admit inmates, and a hire may be called Zach.)
            Pawn[] left = LeftBehind(map, pawnsBefore).Where(x => x.Name is NameSingle && x.LabelShort.StartsWith("Z")).ToArray();
            if (left.Length > 0) problems0.Add($"supermax checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
            string buildings = SupermaxBuildingsDescribe(map);
            if (buildings != buildingsBefore) problems0.Add($"supermax checks left buildings on the map (walls, doors, beds, showers): {buildingsBefore} -> {buildings}");
            if (SupermaxSettingsDescribe() != settingsBefore) problems0.Add($"supermax checks left the regime, the project or the zones changed: {settingsBefore} -> {SupermaxSettingsDescribe()}");
        }

        private static string SupermaxBuildingsDescribe(Map map) =>
            $"{map.listerThings.ThingsOfDef(ThingDefOf.Wall).Count}/{map.listerThings.ThingsOfDef(ThingDefOf.Door).Count}/{map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("Bed")).Count}/{map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("RP_Shower")).Count}";

        /// <summary>What the checks change in the settings (the supermax regime, the project, the zones painted), to see that they put it back.</summary>
        private static string SupermaxSettingsDescribe()
        {
            PrisonGame g = PrisonGame.Instance;
            SecurityLevelDef level = RPSupermaxDefOf.RP_Supermax;
            PrisonMap pm = PrisonMap.Of(Find.CurrentMap);
            return $"{string.Join(",", g.RegimeFor(level).slots.Select(s => s.defName.Replace("RP_", "")))}/{g.bureaucracy.done.Count}/{pm.zones.CellsOf(RPSupermaxDefOf.RP_SupermaxYard).Count}/{pm.zones.CellsOf(RPSupermaxDefOf.RP_WingSupermax).Count}";
        }

        /// <summary>How many supermax scenes are up: while one is, the storyteller's beats of the day are the check's, not the game's (<see cref="SupermaxScene.Build"/>).</summary>
        private static int suppressedBeatsScenes;

        private static void SupermaxFail(string check, string what) => problems0.Add($"supermax ({check}): {what}");

        /// <summary>
        /// The scenes' guards, on shift at every hour for as long as the scene stands. The prison's hourly pass writes every member of staff's shift into their timetable (a day guard sleeps from 22:00, and a
        /// sleeping or breaking one takes leisure before work: <c>JobGiver_Work.GetPriority</c> is 3 on Sleep, 2 on Joy and 9 on Work), so a guard the scene set to Work all day was off duty for the rest of any check
        /// that ran through the evening, and a striker did not fetch the inmate left outside for the two hours the check gave them.
        /// </summary>
        private static readonly List<Pawn> supermaxGuards = new List<Pawn>();

        /// <summary>The scenes' guards' timetables written as the scene wrote them, once the hourly pass has written another (only then: a write tells the roster).</summary>
        private static void KeepSupermaxGuardsOnShift()
        {
            foreach (Pawn p in supermaxGuards)
            {
                if (p.timetable == null || p.Destroyed) continue;
                for (int h = 0; h < 24; h++)
                    if (p.timetable.GetAssignment(h) != TimeAssignmentDefOf.Work) p.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
            }
        }

        /// <summary>One tick, with the scenes' guards kept on shift (see <see cref="supermaxGuards"/>).</summary>
        private static void SupermaxTick()
        {
            Find.TickManager.DoSingleTick();
            KeepSupermaxGuardsOnShift();
        }

        /// <summary>The level's schedule set to one activity all day, put back by the rollback.</summary>
        private static void SupermaxRegimeAllDay(PrisonGame g, Rollback r, SecurityLevelDef level, RegimeActivityDef activity)
        {
            RegimeSchedule schedule = g.RegimeFor(level);
            List<RegimeActivityDef> was = schedule.slots.ToList();
            r.Add(() =>
            {
                for (int h = 0; h < 24; h++) schedule.Set(h, was[h]);
            });
            for (int h = 0; h < 24; h++) schedule.Set(h, activity);
        }

        /// <summary>A small supermax unit on open ground outside the compound, walled and roofed, with throwaway inmates and guards in it (see <see cref="CheckSupermax"/>).</summary>
        private sealed class SupermaxScene
        {
            public const int Width = 19, Height = 7;
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly Rollback r;
            public readonly string check;
            public readonly SecurityLevelDef level = RPSupermaxDefOf.RP_Supermax;
            public IntVec3 origin;
            public Building_Bed bedA, bedB, bedC;
            public Building_Door doorA, doorB, doorC, showerDoor, yardDoor, unitDoor;
            public CompFixture shower;
            public CompFixture toiletB, toiletC;

            private SupermaxScene(Map map, Rollback r, string check)
            {
                this.map = map;
                this.r = r;
                this.check = check;
                pm = PrisonMap.Of(map);
            }

            public int Now => Find.TickManager.TicksGame;

            /// <summary>Local coordinates (x east, z north from the unit's south-west corner) in the map's.</summary>
            public IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);

            public IntVec3 CellA => At(2, 4);
            public IntVec3 CellB => At(6, 4);
            public IntVec3 CellC => At(10, 4);
            public IntVec3 CorridorWest => At(1, 1);
            public IntVec3 CorridorEast => At(17, 1);
            /// <summary>Where the unit's door across the corridor is, when it is built (<see cref="Build"/>'s <c>unitDoor</c>): between the first two cells and the rest.</summary>
            public IntVec3 UnitDoorCell => At(9, 1);
            public IntVec3 YardCell => At(16, 4);
            public IntVec3 ShowerCell => At(13, 4);

            /// <summary>
            /// The unit, or null (a problem noted) when there is no open ground for it. <paramref name="bait"/>: a patch of the ordinary yard (in the third cell) and of the canteen (in the corridor), with a meal on it.
            /// <paramref name="toilets"/>: a toilet in the second and third cells (the nearest ones to the yard, in someone else's cell). <paramref name="unitDoor"/>: a door across the corridor (a door of the mode
            /// the check sets) with the first two cells on the west side and the third cell, the shower room and the yard on the east. <paramref name="sharedShower"/>: the shower room is the prison's
            /// ordinary shower zone, not the wing's own.
            /// </summary>
            public static SupermaxScene Build(Map map, Rollback r, string check, bool bait = false, bool toilets = false, bool unitDoor = false, bool sharedShower = false)
            {
                var s = new SupermaxScene(map, r, check);
                // (The storyteller's life beats keep coming through the thousands of real ticks of a walk, and one that picks the scene's inmate - a heart attack, at the age of a random throwaway - puts
                // them in a bed: "doing LayDown" in the cell 4000 ticks after the walk was to begin. The day's beats are used up for the check.)
                StorySystem story = PrisonGame.Instance.story;
                int beats0 = story.beatsToday, day0 = GenLocalDate.DayOfYear(map);
                // (Up to the day's cap, not past it: a day that ends during the check is counted with what it had, and the plan's beats-a-day check reads that.)
                story.beatsToday = System.Math.Max(beats0, StoryPacing.MaxBeatsPerDay(map.mapPawns.PrisonersOfColonySpawnedCount));
                // (The hourly sample of the day's beats leaves the scene's days out: they are pinned at the cap, and the figure is the real storyteller's.)
                suppressedBeatsScenes++;
                r.Add(() =>
                {
                    suppressedBeatsScenes--;
                    // (Not on a new day: midnight reset the count to 0, and yesterday's would start today with part of its budget used.)
                    if (GenLocalDate.DayOfYear(map) == day0) story.beatsToday = beats0;
                });
                s.origin = OpenCorner(map, Width, Height);
                if (!s.origin.IsValid)
                {
                    SupermaxFail(check, $"no open ground outside the compound for the unit (footprint {PrisonBuilder.Footprint(map)}, map {map.Size})");
                    return null;
                }
                // (Trees, bushes and the like on the ground are cleared: a map with woods has no bare ground the size of the unit.)
                foreach (IntVec3 c in new CellRect(s.origin.x, s.origin.z, Width, Height).ExpandedBy(1))
                    foreach (Thing plant in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Plant).ToList())
                        plant.Destroy(DestroyMode.Vanish);
                s.Construct(bait, toilets, unitDoor, sharedShower);
                return s;
            }

            private static IntVec3 OpenCorner(Map map, int w, int h)
            {
                CellRect compound = PrisonBuilder.Footprint(map).ExpandedBy(3);
                PrisonMap pm = PrisonMap.Of(map);
                foreach (IntVec3 c in GenRadial.RadialCellsAround(PrisonBuilder.Footprint(map).CenterCell, 79f, true))
                {
                    var rect = new CellRect(c.x, c.z, w, h);
                    CellRect around = rect.ExpandedBy(1);
                    if (!around.InBounds(map) || around.Overlaps(compound)) continue;
                    bool open = true;
                    foreach (IntVec3 x in around)
                        if (!x.Walkable(map) || x.Roofed(map) || x.GetEdifice(map) != null || x.GetTerrain(map).IsWater || pm.zones.ZoneAt(x) != null
                            || x.GetThingList(map).Any(t => t is Pawn || t.def.category is ThingCategory.Item or ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame))
                        {
                            open = false;
                            break;
                        }
                    if (open) return c;
                }
                // (Nothing open within 70 cells: say what stands in the way, by kind, so that a run that has none can be read.)
                var why = new Dictionary<string, int>();
                foreach (IntVec3 x in GenRadial.RadialCellsAround(PrisonBuilder.Footprint(map).CenterCell, 70f, true))
                {
                    if (!x.InBounds(map) || compound.Contains(x)) continue;
                    void Count(string k) => why[k] = why.TryGetValue(k, out int n) ? n + 1 : 1;
                    if (!x.Standable(map)) Count("not standable");
                    if (x.Roofed(map)) Count("roofed");
                    if (x.GetEdifice(map) != null) Count("edifice");
                    if (pm.zones.ZoneAt(x) != null) Count("zone " + pm.zones.ZoneAt(x).defName);
                    foreach (Thing t in x.GetThingList(map))
                        if (t is Pawn || t.def.category is ThingCategory.Item or ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame) Count((t is Pawn ? "pawn " : t.def.IsBlueprint ? "blueprint " : t.def.IsFrame ? "frame " : "thing ") + t.def.defName);
                }
                RPLog.Message("[RimPrison:test] supermax: no open ground for " + w + " x " + h + ", cells within 70 of the compound blocked by: " + string.Join(", ", why.OrderByDescending(kv => kv.Value).Take(12).Select(kv => kv.Key + " " + kv.Value)));
                return IntVec3.Invalid;
            }

            private T Spawn<T>(ThingDef def, ThingDef stuff, IntVec3 cell, Rot4 rot = default) where T : Thing
            {
                var t = (T)ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff : null);
                if (def.CanHaveFaction) t.SetFactionDirect(Faction.OfPlayer);
                GenSpawn.Spawn(t, cell, map, rot);
                r.Add(() =>
                {
                    if (t.Spawned) t.Destroy(DestroyMode.Vanish);
                });
                return t;
            }

            private void Construct(bool bait, bool toilets, bool withUnitDoor, bool sharedShower)
            {
                // The walls: the south and north walls, the two ends, the walls between the rooms and the one between the rooms and the corridor.
                var doors = new HashSet<(int x, int z)> { (2, 2), (6, 2), (10, 2), (13, 2), (16, 2) };
                for (int x = 0; x < Width; x++)
                    foreach (int z in new[] { 0, 2, Height - 1 })
                        if (!doors.Contains((x, z))) Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(x, z));
                foreach (int x in new[] { 0, 4, 8, 12, 15, Width - 1 })
                    for (int z = 1; z < Height - 1; z++)
                        if (z != 2 && (z != 1 || x == 0 || x == Width - 1)) Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(x, z));
                Building_Door Door(int x) => Spawn<Building_Door>(ThingDefOf.Door, ThingDefOf.Steel, At(x, 2));
                (doorA, doorB, doorC, showerDoor, yardDoor) = (Door(2), Door(6), Door(10), Door(13), Door(16));
                if (withUnitDoor) unitDoor = Spawn<Building_Door>(ThingDefOf.Door, ThingDefOf.Steel, At(9, 1));
                ThingDef bedDef = DefDatabase<ThingDef>.GetNamed("Bed");
                Building_Bed Bed(int x)
                {
                    var bed = Spawn<Building_Bed>(bedDef, ThingDefOf.WoodLog, At(x, 3), Rot4.North);
                    bed.ForPrisoners = true;
                    // (The room decides whether it is a prison cell when its shape changes, which was before the bed was one.)
                    bed.GetRoom()?.Notify_RoomShapeChanged();
                    r.Add(() =>
                    {
                        foreach (Pawn owner in bed.OwnersForReading.ToList()) owner.ownership?.UnclaimBed();
                    });
                    return bed;
                }
                (bedA, bedB, bedC) = (Bed(1), Bed(5), Bed(9));
                shower = Spawn<Building>(DefDatabase<ThingDef>.GetNamed("RP_Shower"), null, ShowerCell).GetComp<CompFixture>();
                if (toilets)
                {
                    ThingDef toilet = DefDatabase<ThingDef>.GetNamed("RP_Toilet");
                    toiletB = Spawn<Building>(toilet, null, At(7, 5)).GetComp<CompFixture>();
                    toiletC = Spawn<Building>(toilet, null, At(11, 5)).GetComp<CompFixture>();
                }
                void Paint(CellRect rect, PrisonZoneDef zone)
                {
                    foreach (IntVec3 c in rect)
                    {
                        PrisonZoneDef was = pm.zones.ZoneAt(c);
                        r.Add(() => pm.zones.Set(c, was));
                        pm.zones.Set(c, zone);
                    }
                }
                Paint(new CellRect(origin.x + 1, origin.z + 3, 3, 3), RPSupermaxDefOf.RP_WingSupermax);
                Paint(new CellRect(origin.x + 5, origin.z + 3, 3, 3), RPSupermaxDefOf.RP_WingSupermax);
                Paint(new CellRect(origin.x + 13, origin.z + 3, 2, 3), sharedShower ? RPDefOf.RP_Shower : RPSupermaxDefOf.RP_WingSupermax);
                Paint(new CellRect(origin.x + 16, origin.z + 3, 2, 3), RPSupermaxDefOf.RP_SupermaxYard);
                if (bait)
                {
                    Paint(new CellRect(origin.x + 9, origin.z + 3, 3, 3), RPDefOf.RP_Yard);
                    Paint(new CellRect(origin.x + 13, origin.z + 1, 4, 1), RPDefOf.RP_Canteen);
                    Thing meal = Spawn<Thing>(DefDatabase<ThingDef>.GetNamed("MealSimple"), null, At(14, 1));
                    meal.stackCount = 5;
                }
                // The roof last (so it is the first to go when the check is rolled back: a roof left up while its walls come down is marked to collapse, and the collapse came down on the next scene's guard at the same spot).
                foreach (IntVec3 c in new CellRect(origin.x, origin.z, Width, Height))
                {
                    RoofDef was = map.roofGrid.RoofAt(c);
                    r.Add(() => map.roofGrid.SetRoof(c, was));
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
                map.reachability.ClearCache();
                pm.ForceDoorUpdate();
                r.Add(() =>
                {
                    map.reachability.ClearCache();
                    pm.ForceDoorUpdate();
                });
            }

            public void Fail(string what) => SupermaxFail(check, what);

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            /// <summary>A second bed in the third cell (it is then a cell for two), put away by the rollback.</summary>
            public Building_Bed ExtraBedInCellC()
            {
                var bed = Spawn<Building_Bed>(DefDatabase<ThingDef>.GetNamed("Bed"), ThingDefOf.WoodLog, At(11, 3), Rot4.North);
                bed.ForPrisoners = true;
                bed.GetRoom()?.Notify_RoomShapeChanged();
                pm.Notify_BedsChanged();
                return bed;
            }

            /// <summary>The third cell painted with the supermax wing (put back by the rollback).</summary>
            public void PaintCellCAsWing()
            {
                foreach (IntVec3 c in new CellRect(origin.x + 9, origin.z + 3, 3, 3))
                {
                    PrisonZoneDef was = pm.zones.ZoneAt(c);
                    r.Add(() => pm.zones.Set(c, was));
                    pm.zones.Set(c, RPSupermaxDefOf.RP_WingSupermax);
                }
            }

            /// <summary>A supermax inmate (or of another level) in a cell of the unit, with its bed: calm, clean record, three days in, full needs but for the shower.</summary>
            public Pawn Inmate(string name, Building_Bed bed, IntVec3 at, SecurityLevelDef sec = null)
            {
                sec ??= level;
                Pawn p = Throwaway(map, r, sec);
                p.Name = new NameSingle(name);
                CompInmate c = p.Inmate();
                c.security = sec;
                (c.processed, c.awaitingSearch, c.forcedActivity, c.pendingPunishmentHours, c.want, c.gang, c.gangKnown, c.gangLeader) = (true, false, null, 0, null, null, false, false);
                (c.tension, c.fear, c.cash, c.familyCount, c.visitor, c.snitchExposed, c.informantQuit, c.informantPaid) = (20f, 0f, 0, 0, null, false, false, false);
                (c.leg, c.escortRetryTick, c.yardSlot, c.supermaxAskTick, c.lastReclassTick) = (null, 0, -1, -999999, Now - 3 * GenDate.TicksPerDay);
                c.records.Clear();
                c.reputations.Clear();
                c.revealed.Clear();
                c.relatives.Clear();
                c.staffMemories.Clear();
                ClearAddictions(p);
                // (No wounds or illness, and no parts missing: a patient goes to bed for medical reasons before anything the regime says. Removing the hediffs one by one made a pawn with a prosthesis lose the part again, bleeding.)
                p.health.Reset();
                c.RefreshActiveFlag();
                Place(p, at);
                if (bed != null) p.ownership.ClaimBedIfNonMedical(bed);
                if (p.needs.TryGetNeed(RPDefOf.RP_Bladder) is { } bladder) bladder.CurLevel = 1f;
                if (p.needs.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs.food != null) p.needs.food.CurLevel = 1f;
                if (p.needs.TryGetNeed(RPDefOf.RP_Hygiene) is { } hygiene) hygiene.CurLevel = 0.2f;
                pm.ForceDoorUpdate();
                return p;
            }

            /// <summary>A guard on site, awake and on shift at every hour, in the unit.</summary>
            public Pawn Guard(string name, IntVec3 at)
            {
                Pawn s = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                s.Name = new NameSingle(name);
                if (s.timetable != null)
                    for (int hour = 0; hour < 24; hour++) s.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                supermaxGuards.Add(s);
                r.Add(() => supermaxGuards.Remove(s));
                if (s.needs?.rest != null) s.needs.rest.CurLevel = 1f;
                CompStaff sc = s.GetComp<CompStaff>();
                (sc.corrupt, sc.patron, sc.patronGang, sc.doubleAgent, sc.exposedCorrupt, sc.suspicion, sc.iaOpen, sc.suspendedUntilTick) = (false, null, null, false, false, 0f, false, -1);
                sc.evidence.Clear();
                s.health.Reset();
                // (No work of their own: the checks start the walk by hand, and a guard with nothing to do would take it, or anything else, by themselves.)
                if (s.workSettings != null)
                    foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefsListForReading) s.workSettings.SetPriority(work, 0);
                Place(s, at);
                return s;
            }

            public bool InCell(Pawn p, Building_Bed bed) => p.GetRoom() is { } room && room == bed.GetRoom();

            public Pawn Cook(IntVec3 at)
            {
                Pawn s = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed("RP_Cook"));
                s.Name = new NameSingle("Zcook");
                Place(s, at);
                return s;
            }

            /// <summary>Starts a guard's job by hand (the work giver's offer is a separate check: the walk should not depend on which guard is free).</summary>
            public Job Start(Pawn guard, Job job)
            {
                guard.jobs.StartJob(job, JobCondition.InterruptForced);
                return job;
            }
        }

        // ---- the level, its regime and its project --------------------------------------------------------------------------------------------

        private static void CheckSupermaxDefs(Map map)
        {
            Guarded("supermax: the level, its regime and its project", map, r =>
            {
                const string check = "defs";
                PrisonGame g = PrisonGame.Instance;
                SecurityLevelDef level = RPSupermaxDefOf.RP_Supermax, maximum = DefDatabase<SecurityLevelDef>.GetNamed("RP_Maximum"), protective = RPSecurityDefOf.RP_Protective;
                if (!level.wingOnly || !level.cellMeals || !level.escortOnly || !level.yardAlone || Math.Abs(level.dangerFactor - 3f) > 0.001f)
                    SupermaxFail(check, $"flags: wing only {level.wingOnly}, cell meals {level.cellMeals}, escort only {level.escortOnly}, yard alone {level.yardAlone}, danger factor {level.dangerFactor} (all set, 3 expected)");
                if (DefDatabase<SecurityLevelDef>.AllDefsListForReading.Any(s => s != level && s.dailyPayment >= level.dailyPayment))
                    SupermaxFail(check, $"the daily payment {level.dailyPayment} is not the highest ({string.Join(", ", DefDatabase<SecurityLevelDef>.AllDefsListForReading.Select(s => s.defName + " " + s.dailyPayment))})");
                if (!(maximum.uiOrder < level.uiOrder && level.uiOrder < protective.uiOrder)) SupermaxFail(check, $"order {level.uiOrder}: between maximum ({maximum.uiOrder}) and protective custody ({protective.uiOrder}) expected");
                List<RegimeActivityDef> slots = level.defaultRegime;
                int confined = slots.Count(a => a.lockup), showers = slots.Count(a => a == RPDefOf.RP_ShowerTime), yards = slots.Count(a => a == RPSupermaxDefOf.RP_YardAlone);
                // (The yard is in several hours: one patch takes one inmate at a time, and a unit of four cells needs four; the showers likewise.)
                if (slots.Count != 24 || confined != 17 || showers != 3 || yards != 4 || slots.Any(a => a.defName is "RP_Eat" or "RP_Work" or "RP_FreeTime"))
                    SupermaxFail(check, $"default regime: {slots.Count} hours, {confined} locked up or asleep (17 expected), {showers} shower (3), {yards} yard alone (4), a canteen, work or free-time hour {slots.Any(a => a.defName is "RP_Eat" or "RP_Work" or "RP_FreeTime")}");
                if (RPSupermaxDefOf.RP_WingSupermax.security != level || RPSupermaxDefOf.RP_SupermaxYard.yardFor != level)
                    SupermaxFail(check, "the wing zone names another level, or the yard zone is nobody's");
                BureaucracyDef project = Unlocks.GateOf(level);
                BureaucracyDef maxSecurity = DefDatabase<BureaucracyDef>.GetNamed("RP_Bur_MaxSecurity");
                if (project == null || !project.prerequisites.Contains(maxSecurity) || Unlocks.GateOf(RPSupermaxDefOf.RP_WingSupermax) != project || Unlocks.GateOf(RPSupermaxDefOf.RP_SupermaxYard) != project)
                    SupermaxFail(check, $"the project {project?.defName ?? "(none)"} unlocks the level, the wing and the yard, after maximum security");
                // The project is what opens it: without it nothing sends anybody there.
                BureaucracySystem bureaucracy = g.bureaucracy;
                List<BureaucracyDef> done0 = bureaucracy.done.ToList();
                r.Add(() =>
                {
                    bureaucracy.done.Clear();
                    bureaucracy.done.AddRange(done0);
                });
                if (project != null)
                {
                    bureaucracy.done.Remove(project);
                    bool closed = !Unlocks.IsUnlocked(level) && !Unlocks.IsUnlocked(RPSupermaxDefOf.RP_WingSupermax);
                    bureaucracy.done.Add(project);
                    bool open = Unlocks.IsUnlocked(level) && Unlocks.IsUnlocked(RPSupermaxDefOf.RP_SupermaxYard);
                    if (!closed || !open) SupermaxFail(check, $"the level and its zones are locked without the project ({closed}) and open with it ({open})");
                }
                // (The state does not send them: with free cells in a supermax wing and the intake set to three a day, nobody comes.)
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                var perDay0 = new Dictionary<SecurityLevelDef, int>(g.intake.perDay);
                r.Add(() =>
                {
                    g.intake.perDay.Clear();
                    foreach (KeyValuePair<SecurityLevelDef, int> kv in perDay0) g.intake.perDay[kv.Key] = kv.Value;
                });
                foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading) g.intake.perDay[sec] = sec == level ? 3 : 0;
                int free = IntakeSystem.FreeBedCount(map, level);
                List<Pawn> roster = IntakeSystem.GenerateRoster(g, map, out _, out _);
                foreach (Pawn made in roster)
                    if (!made.Spawned && !made.Destroyed) Find.WorldPawns.PassToWorld(made, RimWorld.Planet.PawnDiscardDecideMode.Discard);
                if (free != 2 || roster.Count > 0) SupermaxFail(check, $"the state's shuttle brought {roster.Count} inmate(s) with {free} free supermax cells (2) and the intake set to 3 a day for supermax (none expected)");
            });
        }

        // ---- a cell that is locked in every hour of the regime -------------------------------------------------------------------------------------

        private static void CheckSupermaxCellsAreLockedInEveryHour(Map map)
        {
            Guarded("supermax: the cell is locked in every hour", map, r =>
            {
                const string check = "cell doors";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn p = s.Inmate("Zlocked1", s.bedA, s.CellA);
                CompInmate c = p.Inmate();
                CompPrisonDoor door = s.doorA.GetComp<CompPrisonDoor>();
                if (door == null || !s.pm.PrisonerBeds.Contains(s.bedA)) { s.Fail("the scene's door has no prison door or its bed is not a prisoner bed"); return; }
                int now = s.Now;
                var open = new List<int>();
                for (int h = 0; h < 24; h++)
                    if (s.pm.CellDoorOpen(door, g, now, h)) open.Add(h);
                if (open.Count > 0) s.Fail($"a supermax cell's door is open at {string.Join(", ", open)} o'clock (locked in all 24 hours expected, the shower and yard slots included; owners {s.bedA.OwnersForReading.Count}, cell room {door.cellRoom != null}, sides {door.sideA}/{door.sideB}, mode {door.mode}, fires on the map {map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count}, locked down {g.LockedDown(c)}, active {InmateUtility.IsActiveInmate(p)}, unit at {s.origin - PrisonBuilder.Footprint(map).CenterCell})");
                // The control: the same cell with a medium security inmate in it is open when the regime lets them out and locked at night.
                c.security = InmateUtility.DefaultSecurity;
                int freeHour = Enumerable.Range(0, 24).First(h => !g.RegimeFor(InmateUtility.DefaultSecurity).At(h).lockup), lockedHour = Enumerable.Range(0, 24).First(h => g.RegimeFor(InmateUtility.DefaultSecurity).At(h).lockup);
                if (!s.pm.CellDoorOpen(door, g, now, freeHour) || s.pm.CellDoorOpen(door, g, now, lockedHour))
                    s.Fail($"the control (a medium security inmate in the same cell) is not open at {freeHour} and locked at {lockedHour}");
                c.security = s.level;
                // And in the world: out of the cell they cannot walk, however the regime reads (free time all day), and the AI keeps them inside.
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_FreeTime);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                bool canReach = p.CanReach(s.CorridorWest, PathEndMode.OnCell, Danger.Deadly), canOpen = s.doorA.PawnCanOpen(p);
                if (canReach || canOpen) s.Fail($"a supermax inmate can walk out of the cell on a free-time regime (reach {canReach}, can open the door {canOpen})");
                bool stayed = true;
                int ticked = ShuttleTicks(() =>
                {
                    stayed &= s.InCell(p, s.bedA);
                    return !stayed;
                }, 400);
                if (!stayed) s.Fail($"on a free-time regime they left the cell within {ticked} ticks (at {p.Position - s.origin}, doing {p.CurJobDef?.defName})");
                c.security = s.level;
            });
        }

        // ---- never in the canteen; the meal is brought --------------------------------------------------------------------------------------------

        private static void CheckSupermaxNeverInTheCanteenAndTheMealComesToTheCell(Map map)
        {
            Guarded("supermax: never in the canteen, the meal comes to the cell", map, r =>
            {
                const string check = "canteen and meals";
                SupermaxScene s = SupermaxScene.Build(map, r, check, bait: true);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn p = s.Inmate("Zfed1", s.bedA, s.CellA);
                CompInmate c = p.Inmate();
                Thing meal = s.CorridorEast.GetThingList(map).FirstOrDefault(t => t.def.defName == "MealSimple") ?? s.At(14, 1).GetThingList(map).FirstOrDefault(t => t.def.defName == "MealSimple");
                if (meal == null) { s.Fail("the scene's meal is not on the canteen"); return; }
                int stack0 = meal.stackCount;
                if (PrisonServices.UsesCanteen(p)) s.Fail("the canteen's ratios count a supermax inmate (they do not walk there)");
                // The regime says "eat" in every hour (a warden's mistake), they are starving, the canteen is in the corridor: they do not go.
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_FreeTime);
                g.RegimeFor(s.level).slots.ForEach(_ => { });
                RegimeSchedule schedule = g.RegimeFor(s.level);
                RegimeActivityDef eat = DefDatabase<RegimeActivityDef>.GetNamed("RP_Eat");
                for (int h = 0; h < 24; h++) schedule.Set(h, eat);
                p.needs.food.CurLevel = 0.12f;
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                bool stayed = true, ate = false;
                int ticked = ShuttleTicks(() =>
                {
                    stayed &= s.InCell(p, s.bedA);
                    ate |= p.CurJobDef == JobDefOf.Ingest && p.CurJob.targetA.Thing == meal;
                    return !stayed || ate;
                }, 600);
                if (!stayed || ate || meal.stackCount != stack0)
                    s.Fail($"a starving supermax inmate on an eating regime with the canteen next door: left the cell {!stayed}, ate the canteen's meal {ate}, the stack {stack0} -> {meal.stackCount} (after {ticked} ticks, doing {p.CurJobDef?.defName})");
                // The cook is offered the meal to take to them.
                Pawn cook = s.Cook(s.At(14, 1));
                p.needs.food.CurLevel = 0.2f;
                map.reachability.ClearCache();
                Job offered = WithoutTheOtherCooks(map, cook, () => new WorkGiver_DeliverCellMeal().JobOnThing(cook, p));
                if (offered == null || offered.def != JobDefOf.DeliverFood || offered.targetB.Thing != p)
                    s.Fail($"a hungry supermax inmate in their cell is not offered a meal to the cell (job {offered?.def.defName ?? "none"}; the cook may deliver {WorkGiver_DeliverCellMeal.CookMayDeliver(cook)}, can reach them {cook.CanReach(p, PathEndMode.Touch, Danger.Deadly)} and reserve them {cook.CanReserve(p)}, "
                           + $"the inmate is {(p.Inmate()?.activeFlag == true ? "active" : "not active")}, fasting {HungerStrike.Fasting(p.Inmate())}, food at {p.needs.food.CurLevelPercentage:P0}, in {(p.GetRoom()?.IsPrisonCell == true ? "a cell" : "no cell")}, wardens' view of them: secure {p.guest.PrisonerIsSecure}, brought food {p.guest.CanBeBroughtFood})");
                // (Not on the cell-meal giver: someone on the same level without cell meals gets none while they are free to walk to the canteen.)
                c.security = InmateUtility.DefaultSecurity;
                // (Shut in this cell with no meal they can walk to, a medium inmate below 25% is brought one too, the balance pass's stranded rule; above that they are not, whatever the level.)
                if (WithoutTheOtherCooks(map, cook, () => new WorkGiver_DeliverCellMeal().JobOnThing(cook, p))?.def != JobDefOf.DeliverFood) s.Fail("a medium inmate shut in a cell with no meal to walk to, below 25%, is not offered a meal in the cell");
                p.needs.food.CurLevel = 0.3f;
                if (WithoutTheOtherCooks(map, cook, () => new WorkGiver_DeliverCellMeal().JobOnThing(cook, p)) != null) s.Fail("the control: a medium inmate who is not hungry enough to be stranded is offered a meal in the cell");
                c.security = s.level;
            });
        }

        /// <summary>
        /// Asks <paramref name="ask"/> as if the scene's cook were the only cook: the giver lets at most half of the cooks walk meals out at once, and a real cook of the prison who is delivering at that moment (whoever it is
        /// depends on the hour) took the scene's cook's turn. The real cooks' roles are put back at once (no tick runs in the call).
        /// </summary>
        private static T WithoutTheOtherCooks<T>(Map map, Pawn cook, Func<T> ask)
        {
            var others = map.mapPawns.FreeColonistsSpawned.Where(x => x != cook && x.GetComp<CompStaff>() is { role: { } role } && role.workTypes.Any(w => w.defName == "Cooking")).Select(x => (comp: x.GetComp<CompStaff>(), role: x.GetComp<CompStaff>().role)).ToList();
            foreach ((CompStaff comp, StaffRoleDef _) in others) comp.role = null;
            try
            {
                return ask();
            }
            finally
            {
                foreach ((CompStaff comp, StaffRoleDef role) in others) comp.role = role;
            }
        }

        // ---- escorted to the shower ---------------------------------------------------------------------------------------------------------------

        private static void CheckSupermaxEscortedToTheShower(Map map)
        {
            Guarded("supermax: escorted to the shower", map, r =>
            {
                const string check = "shower";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zescort1", s.CorridorWest);
                Pawn p = s.Inmate("Zshower1", s.bedA, s.CellA);
                CompInmate c = p.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPDefOf.RP_ShowerTime);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                Need hygiene = p.needs.TryGetNeed(RPDefOf.RP_Hygiene);
                // Alone, the door is shut to them and the shower is out of reach, whatever the hour.
                if (p.CanReach(s.ShowerCell, PathEndMode.OnCell, Danger.Deadly) || s.doorA.PawnCanOpen(p)) s.Fail("a supermax inmate in the shower slot can walk to the shower by themselves");
                // The guard is offered the walk: a walk to the shower, at the pace of a walk, the inmate's turn.
                InmateCandidates.Shared.Invalidate();
                var giver = new WorkGiver_EscortInmate();
                bool listed = InmateCandidates.Shared.Escort(map).Contains(p), offers = giver.HasJobOnThing(guard, p);
                Job job = offers ? giver.JobOnThing(guard, p) : null;
                if (!listed || job == null || job.def != RPSupermaxDefOf.RP_EscortInmate || job.targetA.Thing != p || (EscortKind)job.count != EscortKind.Shower || job.targetC.Cell != s.ShowerCell || job.locomotionUrgency != LocomotionUrgency.Walk)
                {
                    s.Fail($"the guard was not offered a walk to the shower (listed {listed}, offered {offers}, job {job?.def.defName ?? "none"}, kind {(job == null ? "-" : ((EscortKind)job.count).ToString())}, target {(job == null ? "-" : (job.targetC.Cell - s.origin).ToString())}, walk {job?.locomotionUrgency})");
                    return;
                }
                // A patient (bleeding from a cut) stays in bed whatever the slot says: no walk is offered for them (it would hold a guard at the far end).
                var cut = (Hediff_Injury)HediffMaker.MakeHediff(HediffDefOf.Cut, p, p.RaceProps.body.AllParts.First(part => part.def == BodyPartDefOf.Arm));
                cut.Severity = 4f;
                p.health.AddHediff(cut);
                InmateCandidates.Shared.Invalidate();
                bool patientOffered = giver.HasJobOnThing(guard, p) || SupermaxSystem.Candidate(p);
                p.health.RemoveHediff(cut);
                InmateCandidates.Shared.Invalidate();
                if (patientOffered) s.Fail("a walk was offered for an inmate who is bleeding and needs a bed");
                // A walk whose inmate never came is given up after half an hour (the guard would wait at the far end for as long as the regime lets it).
                var late = new EscortLeg { startTick = s.Now - SupermaxSystem.NoShowTicks - 1 };
                var onTime = new EscortLeg { startTick = s.Now - SupermaxSystem.NoShowTicks + 100 };
                var arrived = new EscortLeg { startTick = s.Now - SupermaxSystem.NoShowTicks - 1, arrivedTick = s.Now - 10 };
                if (!EscortWalk.NoShow(late, s.Now) || EscortWalk.NoShow(onTime, s.Now) || EscortWalk.NoShow(arrived, s.Now)) s.Fail("the no-show rule: a walk the inmate never set out on is not given up after the limit, or one that is on time or arrived is");
                // Clean enough: no walk.
                float dirty = hygiene.CurLevel;
                hygiene.CurLevel = 0.95f;
                InmateCandidates.Shared.Invalidate();
                bool cleanOffered = giver.HasJobOnThing(guard, p);
                hygiene.CurLevel = dirty;
                if (cleanOffered) s.Fail("a walk to the shower was offered for an inmate who is already clean");
                // Real ticks: the guard walks them there, they wash, they are walked back.
                s.Start(guard, job);
                bool sawLead = false, sawPassOpen = false, sawBack = false, sawWalking = true, usedShower = false;
                float maxApart = 0f;
                int ticked = 0, lastToil = -1;
                var trace = new System.Text.StringBuilder();
                bool finished = false;
                trace.Append($" medical rest {HealthAIUtility.ShouldSeekMedicalRest(p)}, awake {p.Awake()}, rest {p.needs.rest?.CurLevel:F2}; after StartJob: guard job {guard.CurJobDef?.defName}, guard downed {guard.Downed} spawned {guard.Spawned} mental {guard.MentalStateDef?.defName}, reserved by {map.reservationManager.FirstRespectedReserver(p, guard)?.LabelShort ?? "nobody"}, inmate active {c.activeFlag} downed {p.Downed} mental {p.MentalStateDef?.defName} security {c.security?.defName} leg {c.leg != null};");
                while (ticked < 7000 && !finished)
                {
                    SupermaxTick();
                    ticked++;
                    if ((guard.jobs.curDriver?.CurToilIndex ?? -1) != lastToil || ticked < 40 && guard.CurJobDef != RPSupermaxDefOf.RP_EscortInmate)
                    {
                        lastToil = guard.jobs.curDriver?.CurToilIndex ?? -1;
                        trace.Append($" <{ticked}: toil {lastToil} {guard.CurJobDef?.defName} (tag {guard.mindState.lastJobTag}, giver {guard.CurJob?.jobGiver}), guard {guard.Position - s.origin}, inmate {p.Position - s.origin} {p.CurJobDef?.defName}, leg {(c.leg == null ? "-" : c.leg.phase + (c.leg.failed ? " failed" : "") + (c.leg.done ? " done" : ""))}>");
                    }
                    if (ticked == 160) trace.Append($" (regime job now: {new JobGiver_InmateRegime().TryIssueJobPackage(p, default).Job?.ToString() ?? "none"}, lastJobTag {p.mindState.lastJobTag}, curJob {p.CurJob}, hediffs [{string.Join(",", p.health.hediffSet.hediffs.Select(h => h.def.defName + (h.def.tendable ? "/tendable" : "")))}] seek rest {HealthAIUtility.ShouldSeekMedicalRest(p)}/{HealthAIUtility.ShouldSeekMedicalRestUrgent(p)} needing tend {p.health.HasHediffsNeedingTend()}, can reach {p.CanReach(s.ShowerCell, PathEndMode.OnCell, Danger.Deadly)})");
                    if (ticked % 150 == 1 && ticked < 2500) trace.Append($" [{ticked}: guard {guard.Position - s.origin} {guard.CurJobDef?.defName}/{guard.jobs.curDriver?.CurToilIndex}, inmate {p.Position - s.origin} {p.CurJobDef?.defName} leg {(c.leg == null ? "-" : c.leg.phase.ToString())} hyg {hygiene.CurLevel:F2}]");
                    if (c.leg is { } leg)
                    {
                        sawLead |= leg.phase == EscortPhase.Out;
                        sawBack |= leg.phase == EscortPhase.Back;
                        if (leg.phase == EscortPhase.Out && !s.InCell(p, s.bedA) && p.GetRoom() != s.shower.parent.GetRoom() && ticked % 5 == 0) maxApart = Mathf.Max(maxApart, guard.Position.DistanceTo(p.Position));
                        if (p.CurJobDef == JobDefOf.Goto && p.CurJob.locomotionUrgency != LocomotionUrgency.Walk) sawWalking = false;
                    }
                    if (!s.InCell(p, s.bedA) && p.CanReach(s.CorridorWest, PathEndMode.OnCell, Danger.Deadly)) sawPassOpen = true;
                    usedShower |= p.CurJobDef == RPDefOf.RP_UseFixture;
                    finished = guard.CurJob?.def != RPSupermaxDefOf.RP_EscortInmate && ticked > 30 && c.leg == null;
                }
                bool back = s.InCell(p, s.bedA);
                if (!finished || !sawLead || !sawBack || !usedShower || hygiene.CurLevel < SupermaxSystem.CleanEnough - 0.001f || !back || c.leg != null)
                    s.Fail($"the walk to the shower in real ticks: after {ticked} ticks finished {finished}, out {sawLead}, back {sawBack}, used the shower {usedShower}, hygiene {hygiene.CurLevel:F2} (at least {SupermaxSystem.CleanEnough} expected), back in the cell {back}, leg {c.leg != null}; trace{trace}");
                if (!sawWalking || maxApart > 8f) s.Fail($"the inmate and the guard did not walk together: the inmate's walk at a pace other than a walk {!sawWalking}, never more than {maxApart:F1} cells apart (8 allowed)");
                if (!sawPassOpen) s.Fail("the doors never opened to them while the guard had them (nothing reachable outside the cell)");
                // After: shut in again, and the shower is out of reach.
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                if (p.CanReach(s.ShowerCell, PathEndMode.OnCell, Danger.Deadly)) s.Fail("after the walk the inmate can still reach the shower by themselves");
            });
        }

        // ---- the yard, alone -----------------------------------------------------------------------------------------------------------------------

        private static void CheckSupermaxYardAlone(Map map)
        {
            Guarded("supermax: the yard alone", map, r =>
            {
                const string check = "yard";
                SupermaxScene s = SupermaxScene.Build(map, r, check, bait: true);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zyardg1", s.CorridorWest);
                Pawn a = s.Inmate("Zyard1", s.bedA, s.CellA), b = s.Inmate("Zyard2", s.bedB, s.CellB);
                CompInmate ac = a.Inmate(), bc = b.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPSupermaxDefOf.RP_YardAlone);
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                int slot = SupermaxSystem.SlotOf(map);
                // The yard is the unit's own, never the ordinary yard painted in the corridor.
                if (!SupermaxSystem.TryPlan(guard, a, out EscortKind kind, out IntVec3 target, out IntVec3 post) || kind != EscortKind.Yard || s.pm.zones.ZoneAt(target) != RPSupermaxDefOf.RP_SupermaxYard || s.pm.zones.ZoneAt(post) != RPSupermaxDefOf.RP_SupermaxYard)
                {
                    s.Fail($"the yard walk of the first inmate goes to {s.pm.zones.ZoneAt(target)?.defName ?? "nowhere"} (kind {kind}, post in {s.pm.zones.ZoneAt(post)?.defName ?? "nowhere"}): the supermax yard expected, never the ordinary yard");
                    return;
                }
                // The guard waits next to the inmate's cell of the yard, in the same patch: not on the cell the inmate is walked to.
                if (post == target || s.pm.zones.PatchAt(post) != s.pm.zones.PatchAt(target)) s.Fail($"the guard's post is {(post == target ? "the inmate's own target cell" : "in another patch than the inmate's target")} ({post - s.origin}, the target {target - s.origin})");
                // Two are due and there is one patch: the one longest without goes first, the other waits.
                ac.yardSlot = slot - 5;
                bc.yardSlot = slot - 2;
                bool firstA = SupermaxSystem.TryPlan(guard, a, out _, out _, out _), thenB = SupermaxSystem.TryPlan(guard, b, out _, out _, out _);
                ac.yardSlot = slot - 2;
                bc.yardSlot = slot - 5;
                bool nowB = SupermaxSystem.TryPlan(guard, b, out _, out _, out _), thenA = SupermaxSystem.TryPlan(guard, a, out _, out _, out _);
                if (!firstA || thenB || !nowB || thenA) s.Fail($"with two inmates due and one yard, the one longest without it goes first: a before b {firstA}/{thenB}, b before a {nowB}/{thenA} (true/false and true/false expected)");
                (ac.yardSlot, bc.yardSlot) = (-1, -1);
                // Skipped while another inmate is in the patch (standing in it, or on the way with a guard), and not before. (This one is the longest without the yard.)
                ac.yardSlot = slot - 9;
                bc.yardSlot = slot - 1;
                IntVec3 inYard = s.YardCell;
                Pawn other = s.Inmate("Zyard3", null, inYard, s.level);
                bool whileIn = SupermaxSystem.TryPlan(guard, a, out _, out _, out _);
                s.Place(other, s.CellC);
                bool afterOut = SupermaxSystem.TryPlan(guard, a, out _, out _, out _);
                other.Destroy(DestroyMode.Vanish);
                bc.leg = new EscortLeg { guard = guard, kind = EscortKind.Yard, target = inYard };
                bool whileOnTheWay = SupermaxSystem.TryPlan(guard, a, out _, out _, out _);
                bc.leg = null;
                if (whileIn || !afterOut || whileOnTheWay) s.Fail($"the yard hour with another inmate in the patch: offered while one stands in it {whileIn}, offered once it is empty {afterOut}, offered while one is on the way {whileOnTheWay} (false, true, false expected)");
                // The slot is theirs once taken: not due again in the same hour.
                ac.yardSlot = slot;
                InmateCandidates.Shared.Invalidate();
                if (SupermaxSystem.Candidate(a)) s.Fail("an inmate who has had the yard this hour is due for it again");
                (ac.yardSlot, bc.yardSlot) = (-1, -1);
                // Real ticks: the guard takes the first one out; while they are in the yard nobody else is offered it; they come back.
                InmateCandidates.Shared.Invalidate();
                var giver = new WorkGiver_EscortInmate();
                Job job = giver.HasJobOnThing(guard, a) ? giver.JobOnThing(guard, a) : null;
                if (job == null || (EscortKind)job.count != EscortKind.Yard) { s.Fail($"the guard was not offered the yard walk (job {job?.def.defName ?? "none"})"); return; }
                s.Start(guard, job);
                bool inYardAlone = false, otherOffered = false, ordinaryYard = false, finished = false;
                int ticked = 0, lastToil = -1;
                var trace = new System.Text.StringBuilder();
                while (ticked < 9000 && !finished)
                {
                    SupermaxTick();
                    ticked++;
                    if ((guard.jobs.curDriver?.CurToilIndex ?? -1) != lastToil)
                    {
                        lastToil = guard.jobs.curDriver?.CurToilIndex ?? -1;
                        trace.Append($" <{ticked}: toil {lastToil} {guard.CurJobDef?.defName}, guard {guard.Position - s.origin}, inmate {a.Position - s.origin}, leg {(ac.leg == null ? "-" : ac.leg.phase + (ac.leg.failed ? " failed" : ""))}>");
                    }
                    if (ticked % 300 == 1 && ticked < 3600) trace.Append($" [{ticked}: guard {guard.Position - s.origin} {guard.CurJobDef?.defName}, inmate {a.Position - s.origin} {a.CurJobDef?.defName} leg {(ac.leg == null ? "-" : ac.leg.phase.ToString())}]");
                    if (s.pm.zones.ZoneAt(a.Position) == RPDefOf.RP_Yard) ordinaryYard = true;
                    if (s.pm.zones.ZoneAt(a.Position) == RPSupermaxDefOf.RP_SupermaxYard)
                    {
                        inYardAlone = true;
                        if (ticked % 20 == 0)
                        {
                            InmateCandidates.Shared.Invalidate();
                            otherOffered |= SupermaxSystem.TryPlan(guard, b, out _, out _, out _);
                        }
                    }
                    finished = guard.CurJob?.def != RPSupermaxDefOf.RP_EscortInmate && ticked > 30 && ac.leg == null;
                }
                bool back = s.InCell(a, s.bedA);
                if (!finished || !inYardAlone || !back || ordinaryYard || otherOffered || ac.yardSlot < slot || ac.yardSlot > SupermaxSystem.SlotOf(map))
                    s.Fail($"the yard walk in real ticks: after {ticked} ticks finished {finished}, in the yard {inYardAlone}, back in the cell {back}, in the ordinary yard {ordinaryYard}, a second inmate was offered the yard while the first was in it {otherOffered}, slot {ac.yardSlot} (the hours of the check {slot} to {SupermaxSystem.SlotOf(map)}); trace{trace}");
            });
        }

        // ---- a lockdown ends the walk ------------------------------------------------------------------------------------------------------------------

        /// <summary>A lockdown of the wing while they are in the yard: the guard walks them back at once (the yard stay is 2000 ticks), and they are shut in.</summary>
        private static void CheckSupermaxLockdownBringsThemBack(Map map)
        {
            Guarded("supermax: a lockdown ends the walk", map, r =>
            {
                const string check = "lockdown";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zlockg1", s.CorridorWest);
                Pawn p = s.Inmate("Zlock1", s.bedA, s.CellA);
                CompInmate c = p.Inmate();
                SupermaxRegimeAllDay(g, r, s.level, RPSupermaxDefOf.RP_YardAlone);
                r.Add(() => g.wingLockdowns.Remove(s.level));
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                var giver = new WorkGiver_EscortInmate();
                InmateCandidates.Shared.Invalidate();
                Job job = giver.HasJobOnThing(guard, p) ? giver.JobOnThing(guard, p) : null;
                if (job == null) { s.Fail("the guard was not offered the yard walk"); return; }
                s.Start(guard, job);
                int ticked = ShuttleTicks(() => s.pm.zones.ZoneAt(p.Position) == RPSupermaxDefOf.RP_SupermaxYard && c.leg?.arrivedTick >= 0, 4000);
                if (s.pm.zones.ZoneAt(p.Position) != RPSupermaxDefOf.RP_SupermaxYard) { s.Fail($"the inmate was not in the yard after {ticked} ticks (at {p.Position - s.origin}, doing {p.CurJobDef?.defName})"); return; }
                g.StartWingLockdown(s.level, 1);
                int back = ShuttleTicks(() => s.InCell(p, s.bedA) && c.leg == null && guard.CurJob?.def != RPSupermaxDefOf.RP_EscortInmate, 2200);
                if (!s.InCell(p, s.bedA) || c.leg != null || back >= SupermaxSystem.YardStayTicks - 500)
                    s.Fail($"a lockdown of the wing while they were in the yard: back in the cell {s.InCell(p, s.bedA)} after {back} ticks (the stay is {SupermaxSystem.YardStayTicks}, so well under that expected), walk still on {c.leg != null}, guard {guard.CurJobDef?.defName}");
            });
        }

        // ---- who goes there ------------------------------------------------------------------------------------------------------------------------

        private static void CheckSupermaxWhoGoesThere(Map map)
        {
            Guarded("supermax: who is sent there", map, r =>
            {
                const string check = "who goes";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                var court = new CourtScene(map, r);
                SecurityLevelDef level = s.level, medium = InmateUtility.DefaultSecurity;
                BureaucracyDef project = Unlocks.GateOf(level);
                string Why(SupermaxReason why) => ("RP_Supermax_Why_" + why).Translate().Resolve();
                bool Told(SupermaxReason why) => r.NewEntries.Any(e => e.text.Contains(Why(why)));
                bool InWing(Pawn p) => p.Inmate().security == level && IntakeSystem.WingOf(p.ownership?.OwnedBed) == level;
                // (A cell is let go: the inmate goes back to medium security and gives up the bed.)
                void Release(Pawn p)
                {
                    p.Inmate().security = medium;
                    p.ownership?.UnclaimBed();
                    s.pm.ForceDoorUpdate();
                }
                if (IntakeSystem.FreeBedCount(map, level) != 2) { s.Fail($"the check's set-up is wrong: {IntakeSystem.FreeBedCount(map, level)} supermax cells free in a two-cell unit"); return; }
                // ---- a staff killer: at the charge, into a free cell
                Pawn killer1 = court.Inmate("Zkiller61"), guardVictim1 = court.Guard("Zvictimg61");
                StaffDeaths.Notify_Died(guardVictim1, guardVictim1.GetComp<CompStaff>(), new DamageInfo(DamageDefOf.Stab, 20f, instigator: killer1), null);
                if (!InWing(killer1) || !Told(SupermaxReason.StaffKiller))
                    s.Fail($"the killer of an officer was not moved to a free supermax cell at the charge: level {killer1.Inmate().security?.defName}, wing {IntakeSystem.WingOf(killer1.ownership?.OwnedBed)?.defName ?? "none"}, the chronicle line {Told(SupermaxReason.StaffKiller)}");
                // With the project not done they would not have been (the old rule stays: a level up if there is a bed, else where they are).
                List<BureaucracyDef> done0 = g.bureaucracy.done.ToList();
                r.Add(() =>
                {
                    g.bureaucracy.done.Clear();
                    g.bureaucracy.done.AddRange(done0);
                });
                if (project != null)
                {
                    g.bureaucracy.done.Remove(project);
                    Pawn killerLocked = court.Inmate("Zkiller66"), guardLocked = court.Guard("Zvictimg66");
                    StaffDeaths.Notify_Died(guardLocked, guardLocked.GetComp<CompStaff>(), new DamageInfo(DamageDefOf.Stab, 20f, instigator: killerLocked), null);
                    bool sentLocked = killerLocked.Inmate().security == level;
                    g.bureaucracy.done.Clear();
                    g.bureaucracy.done.AddRange(done0);
                    if (sentLocked) s.Fail("a killer was moved to supermax with the project not done");
                }
                // ---- no free cell: not supermax
                Building_Bed freeBed = IntakeSystem.FreePrisonerBeds(map, level).LastOrDefault();
                if (freeBed == null) { s.Fail("the check's set-up is wrong: no free supermax cell for the filler"); return; }
                Pawn filler = s.Inmate("Zfill61", freeBed, freeBed.Position);
                if (!InWing(filler) || IntakeSystem.FreeBedCount(map, level) != 0) { s.Fail($"the check's set-up is wrong: the filler is in the wing {InWing(filler)} with {IntakeSystem.FreeBedCount(map, level)} cells free"); return; }
                Pawn killer2 = court.Inmate("Zkiller62"), guardVictim2 = court.Guard("Zvictimg62");
                StaffDeaths.Notify_Died(guardVictim2, guardVictim2.GetComp<CompStaff>(), new DamageInfo(DamageDefOf.Stab, 20f, instigator: killer2), null);
                if (killer2.Inmate().security == level) s.Fail("a killer was moved to supermax with no free cell");
                // ---- the warden's order: refused with no cell (the button says so), carried out once one is free
                Pawn target = court.Inmate("Zorder61");
                CompInmate tc = target.Inmate();
                bool refusedNoRoom = SupermaxSystem.CannotSend(target, tc) != null && !SupermaxSystem.TrySend(target, tc, SupermaxReason.Warden) && tc.security != level;
                string button = "RP_ToSupermax".Translate().ToString();
                Command_Action ButtonOf(CompInmate c) => c.CompGetGizmosExtra().OfType<Command_Action>().FirstOrDefault(x => x.defaultLabel == button);
                bool buttonDisabled = ButtonOf(tc) is { Disabled: true };
                Release(filler);
                bool freeNow = SupermaxSystem.CannotSend(target, tc) == null, buttonEnabled = ButtonOf(tc) is { Disabled: false };
                if (project != null)
                {
                    g.bureaucracy.done.Remove(project);
                    bool lockedOut = SupermaxSystem.CannotSend(target, tc) != null && !SupermaxSystem.TrySend(target, tc, SupermaxReason.Warden), noButton = ButtonOf(tc) == null;
                    g.bureaucracy.done.Clear();
                    g.bureaucracy.done.AddRange(done0);
                    if (!lockedOut || !noButton) s.Fail($"without the project the order was carried out ({!lockedOut}) or the button shown ({!noButton})");
                }
                bool sent = SupermaxSystem.TrySend(target, tc, SupermaxReason.Warden);
                if (!refusedNoRoom || !buttonDisabled || !freeNow || !buttonEnabled || !sent || !InWing(target) || !Told(SupermaxReason.Warden))
                    s.Fail($"the warden's order: refused with no free cell {refusedNoRoom} (button disabled {buttonDisabled}), a cell free after one is let go {freeNow} (button enabled {buttonEnabled}), carried out {sent}, level {tc.security?.defName}, the chronicle line {Told(SupermaxReason.Warden)}");
                // ---- a convicted murderer (of an inmate): not at the charge, at the verdict, into the cell that is free
                Release(target);
                Pawn killer3 = court.Inmate("Zkiller63"), victim3 = court.Inmate("Zvictim63");
                court.Guard("Zwitness63", victim3.Position);
                DeathAftermath.Notify_Died(victim3, killer3, null, null);
                MurderTrial t3 = Trials.TrialOf(g, killer3);
                if (t3 == null) { s.Fail("the killer of an inmate was not charged"); return; }
                bool atCharge = InWing(killer3);
                Trials.Convict(g, map, t3, killer3, killer3.Inmate(), "the court", "the evidence");
                if (atCharge || !InWing(killer3) || !Told(SupermaxReason.Convicted))
                    s.Fail($"a convicted murderer: in supermax at the charge {atCharge} (not yet expected), after the verdict level {killer3.Inmate().security?.defName} and wing {IntakeSystem.WingOf(killer3.ownership?.OwnedBed)?.defName ?? "none"} (supermax expected), the chronicle line {Told(SupermaxReason.Convicted)}");
                // ---- a hostage ringleader: into the cell that is free, the next one finds none
                Release(killer3);
                Pawn leader = court.Inmate("Zleader64"), leaderTwin = court.Inmate("Zleader65");
                bool leaderLifer = leader.Inmate().IsLifer;
                HostageCrisis.ChargeOutcome outcome = HostageCrisis.Charge(leader, map, murder: false);
                if (!outcome.charged || !InWing(leader) || !Told(SupermaxReason.Ringleader)) s.Fail($"the hostage ringleader was not moved to a free supermax cell: charged {outcome.charged}, level {leader.Inmate().security?.defName}, wing {IntakeSystem.WingOf(leader.ownership?.OwnedBed)?.defName ?? "none"}, the chronicle line {Told(SupermaxReason.Ringleader)} ({outcome.line})");
                // (A life term handed down mid-stay is a trigger of the watch list: see WatchList.)
                if (outcome.charged && !leaderLifer && leader.Inmate().IsLifer && (leader.Inmate().riskEvent != RiskTrigger.LifeTerm || leader.Inmate().riskEventTick != Find.TickManager.TicksGame))
                    s.Fail($"the life term handed down to the ringleader is no trigger of the watch list: {WatchList.Describe(leader.Inmate())}");
                HostageCrisis.Charge(leaderTwin, map, murder: false);
                if (leaderTwin.Inmate().security == level) s.Fail("a second ringleader was put in supermax with no free cell");
            });
        }

        // ---- the long stay and the step-down ---------------------------------------------------------------------------------------------------------

        private static void CheckSupermaxLongStayAndStepDown(Map map)
        {
            Guarded("supermax: the long stay and the step-down letter", map, r =>
            {
                const string check = "long stay";
                SupermaxScene s = SupermaxScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn p = s.Inmate("Zstay1", s.bedA, s.CellA);
                CompInmate c = p.Inmate();
                HediffDef stayDef = RPSupermaxDefOf.RP_SupermaxStay;
                ThoughtDef thought = DefDatabase<ThoughtDef>.GetNamed("RP_SupermaxStayThought");
                StatDef threshold = StatDefOf.MentalBreakThreshold;
                int day = GenDate.TicksPerDay, now = s.Now;
                // A new arrival has nothing; each week adds to it: a lower mood and a higher threshold.
                c.lastReclassTick = now - 3 * day;
                SupermaxSystem.Sync(p, c);
                bool none = p.health.hediffSet.GetFirstHediffOfDef(stayDef) == null;
                var moods = new List<float>();
                var thresholds = new List<float>();
                foreach (float weeks in new[] { 1.5f, 2.5f, 3.5f, 5f })
                {
                    c.lastReclassTick = now - Mathf.RoundToInt(weeks * 7 * day);
                    SupermaxSystem.Sync(p, c);
                    Hediff h = p.health.hediffSet.GetFirstHediffOfDef(stayDef);
                    if (h == null) { s.Fail($"no long-stay hediff after {weeks} weeks"); return; }
                    ThoughtState state = thought.Worker.CurrentState(p);
                    moods.Add(state.Active ? thought.stages[state.StageIndex].baseMoodEffect : 0f);
                    thresholds.Add(h.CurStage.statOffsets.GetStatOffsetFromList(threshold));
                }
                bool mood = moods.Zip(moods.Skip(1), (x, y) => y < x).All(x => x) && moods[0] < 0f, raised = thresholds.Zip(thresholds.Skip(1), (x, y) => y > x).All(x => x) && thresholds[0] > 0f;
                if (!none || !mood || !raised)
                    s.Fail($"the long stay: nothing in the first week {none}; mood by week 1.5/2.5/3.5/5 {string.Join("/", moods)} (falling, below zero expected), break threshold offsets {string.Join("/", thresholds.Select(x => x.ToString("F2")))} (rising, above zero expected)");
                // The long stay is not an illness: it does not send them to a medical bed or wait for a doctor.
                if (HealthAIUtility.ShouldSeekMedicalRest(p) || p.health.HasHediffsNeedingTend()) s.Fail("the long-stay hediff makes the inmate seek medical rest or wait for tending");
                // Where the stat the game reads actually moves (its worker, not the game's cache of the answer).
                float withStay = threshold.Worker.GetValueUnfinalized(StatRequest.For(p), applyPostProcess: false);
                Hediff stay = p.health.hediffSet.GetFirstHediffOfDef(stayDef);
                p.health.RemoveHediff(stay);
                float without = threshold.Worker.GetValueUnfinalized(StatRequest.For(p), applyPostProcess: false);
                if (withStay - without < thresholds.Last() - 0.005f) s.Fail($"the pawn's mental break threshold is {withStay:F3} with a month in supermax and {without:F3} without: at least {thresholds.Last():F2} more expected");
                // Leaving takes it away.
                c.lastReclassTick = now - 28 * day;
                SupermaxSystem.Sync(p, c);
                c.security = InmateUtility.DefaultSecurity;
                SupermaxSystem.Sync(p, c);
                if (p.health.hediffSet.GetFirstHediffOfDef(stayDef) != null) s.Fail("the long-stay hediff stayed on after the inmate left supermax");
                c.security = s.level;
                // ---- the step-down: ten clean days, a letter once, then an answer
                var letters = new List<ChoiceLetter_PrisonDecision>();
                List<ChoiceLetter_PrisonDecision> Asked() => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_SupermaxStepDown")).ToList();
                c.lastReclassTick = now - 9 * day;
                SupermaxSystem.Daily(map, g);
                bool nineDays = Asked().Count == 0;
                // A mark on the record the staff saw, a day ago: the days run from it.
                c.lastReclassTick = now - 14 * day;
                c.records.Add(new MisconductRecord { def = DefDatabase<MisconductDef>.AllDefsListForReading.First(), tick = now - day, seen = true });
                SupermaxSystem.Daily(map, g);
                bool marked = Asked().Count == 0;
                c.records.Clear();
                // (An unseen one does not count: nobody saw it.)
                c.records.Add(new MisconductRecord { def = DefDatabase<MisconductDef>.AllDefsListForReading.First(), tick = now - day, seen = false });
                SupermaxSystem.Daily(map, g);
                List<ChoiceLetter_PrisonDecision> first = Asked();
                SupermaxSystem.Daily(map, g);
                bool once = Asked().Count == 1;
                if (!nineDays || !marked || first.Count != 1 || !once)
                    s.Fail($"the step-down letter: asked after nine days {!nineDays}, asked with a seen mark a day ago {!marked}, letters after ten clean days {first.Count} (1), after a second pass {Asked().Count} (still 1); inmate active {c.activeFlag}, in supermax {SupermaxSystem.IsIn(c)}, forced {c.forcedActivity?.defName}, on remand {Trials.OnRemand(g, p)}, clean days {SupermaxSystem.CleanDays(c)} (now {s.Now}, came {c.lastReclassTick}, last mark seen {SupermaxSystem.LastSeenMisconduct(c)}, {c.records.Count} records), asked at {c.supermaxAskTick}, spawned {p.Spawned}, prisoner {p.IsPrisonerOfColony}, among the colony's {map.mapPawns.PrisonersOfColonySpawned.Contains(p)}");
                if (first.Count != 1) return;
                // Declining keeps them where they are; the answer comes again after five days; accepting moves them down when there is a cell.
                PrisonDecisions.Decline(first[0]);
                bool stayed = c.security == s.level;
                c.supermaxAskTick = now - 4 * day;
                SupermaxSystem.Daily(map, g);
                bool notYet = Asked().Count == 1;
                c.supermaxAskTick = now - 6 * day;
                SupermaxSystem.Daily(map, g);
                List<ChoiceLetter_PrisonDecision> second = Asked();
                if (!stayed || !notYet || second.Count != 2) s.Fail($"declining: still in supermax {stayed}; asked again after four days {!notYet}; letters after six days {second.Count} (2 expected)");
                if (second.Count != 2) return;
                SecurityLevelDef maximum = DefDatabase<SecurityLevelDef>.GetNamed("RP_Maximum");
                bool room = InmateUtility.HasRoomIn(p, maximum);
                PrisonDecisions.Accept(second[1]);
                if (room && c.security != maximum || !room && c.security != s.level) s.Fail($"accepting the step-down: room in maximum {room}, level now {c.security?.defName}");
                if (room && (p.health.hediffSet.GetFirstHediffOfDef(stayDef) != null || !r.NewEntries.Any(e => e.text.Contains(("RP_Supermax_Why_" + SupermaxReason.StepDown).Translate().Resolve()))))
                    s.Fail("the step-down left the long-stay hediff on or wrote no chronicle line");
            });
        }
    }
}
