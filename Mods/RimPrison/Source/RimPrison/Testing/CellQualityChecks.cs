using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Cells by conduct (<see cref="CellQuality"/>): the grade bands and their cache, the entitlement's clock, the daily review's moves up and down, who it must not move, and what the social fit (a clash) keeps ahead of a
    /// grade. The review checks run in a <see cref="CellRig"/> of their own (cells walled on open ground, a bed each, the prison's own beds left out of the free list and its inmates held as placed by the warden while they run),
    /// with the cells' grades set to the numbers the bed walk would have cached. A check whose rig cannot be built fails as a premise: it never skips. The entitlement and the policy are in the saveload plan's snapshot.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CellFail(string text) => problems0.Add("cell quality: " + text);

        /// <summary>What the grade term sees for this bed and inmate (a failure's message).</summary>
        private static string CellWhy(Building_Bed bed, CompInmate c)
        {
            PrisonMap pm = bed?.Map == null ? null : PrisonMap.Of(bed.Map);
            Room room = bed?.GetRoom();
            return $"[applies {CellQuality.Applies(c)}: policy {PrisonGame.Instance?.cellsByConduct}, level {c?.security?.defName}, protected {(c == null ? "?" : Trials.IsProtected(c).ToString())}, psych {(c == null ? "?" : PsychWing.IsIn(c).ToString())}, " +
                   $"supermax {(c == null ? "?" : SupermaxSystem.IsIn(c).ToString())}, death row {(c == null ? "?" : (DeathRowSystem.Ext(c) != null).ToString())}; bed at {bed?.Position} padded {(bed == null ? "?" : PaddedCells.IsPadded(bed).ToString())}, solitary {PunishmentUtility.IsSolitaryBed(bed)}, " +
                   $"zone {pm?.zones.ZoneAt(bed?.Position ?? IntVec3.Invalid)?.defName}, room {room?.ID} cells {room?.CellCount} outdoors {room?.PsychologicallyOutdoors}, grade {CellQuality.GradeAt(bed)}, entitlement {c?.entitlement}, term {CellQuality.Term(bed, c)}]";
        }

        private static int CellNow() => Find.TickManager.TicksGame;

        /// <summary>
        /// A row of walled cells on open ground outside the compound, each with the beds asked for (three wide inside, the beds a row apart), painted as the medium wing, in the review's own throwaway world: every real prisoner is
        /// held as placed by the warden, the prison's own beds are out of the list of beds (<see cref="PrisonMap.PrisonerBeds"/>, until the rollback lists them again), the policy is on and a cell's grade is set by <see cref="Grades"/>.
        /// </summary>
        private sealed class CellRig
        {
            public const int Pitch = 4, Depth = 5;
            public Map map;
            public PrisonMap pm;
            public PrisonGame g;
            public Rollback r;
            public SecurityLevelDef level;
            public Gender gender = Gender.Male;
            public readonly List<List<Building_Bed>> beds = new List<List<Building_Bed>>();
            private string what;
            private IntVec3 origin;

            public Room RoomOf(int cell) => beds[cell][0].GetRoom();

            public Building_Bed Bed(int cell, int slot) => beds[cell][slot];

            /// <summary>Which cell of the rig this inmate lives in (-1: none of them).</summary>
            public int CellOf(Pawn p) => p.ownership?.OwnedBed is { } bed ? beds.FindIndex(cell => cell.Contains(bed)) : -1;

            /// <summary>The grade each cell has, in order, as the bed walk would have cached it.</summary>
            public void Grades(params int[] grades)
            {
                for (int k = 0; k < grades.Length; k++) pm.CellGrades[RoomOf(k)] = grades[k];
                Cellmates.InvalidateCells();
            }

            /// <summary>An inmate of the medium wing in this bed: calm, no gang, no reputation, no record, one sex, nothing held against the review, and this entitlement.</summary>
            public Pawn Inmate(int cell, int slot, int entitlement)
            {
                Pawn p = Throwaway(map, r, level);
                CompInmate c = p.Inmate();
                p.gender = gender;
                p.health.Reset();
                (c.security, c.processed, c.awaitingSearch, c.forcedActivity, c.gang, c.entitlement) = (level, true, false, null, null, entitlement);
                (c.wardenPlacedTick, c.gradeMovedTick, c.gradeMismatchTick) = (-999999, CellQuality.NoTick, CellQuality.NoTick);
                c.reputations = new List<ReputationDef>();
                c.records.Clear();
                c.RefreshActiveFlag();
                Building_Bed bed = Bed(cell, slot);
                p.ownership.ClaimBedIfNonMedical(bed);
                if (p.ownership.OwnedBed != bed || !InmateUtility.IsActiveInmate(p)) CellFail($"{what}: premise: a throwaway inmate did not take bed {slot} of cell {cell} or is not an active inmate");
                Cellmates.InvalidateCells();
                return p;
            }

            /// <summary>This inmate's grade mismatch was seen at yesterday's review as well (the review a day on counts it).</summary>
            public static void SeenYesterday(CompInmate c) => c.gradeMismatchTick = CellNow() - GenDate.TicksPerDay;

            /// <summary>The daily review, after the mismatch of these inmates was seen at yesterday's.</summary>
            public int Review(params CompInmate[] seenYesterday)
            {
                foreach (CompInmate c in seenYesterday) SeenYesterday(c);
                return Cellmates.DailyReview(map);
            }

            public void PutBack(Pawn p, Building_Bed bed)
            {
                p.ownership.UnclaimBed();
                p.ownership.ClaimBedIfNonMedical(bed);
                Cellmates.InvalidateCells();
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

            private IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);

            private static IntVec3 OpenGround(Map map, int width, int depth)
            {
                CellRect compound = PrisonBuilder.Footprint(map).ExpandedBy(3);
                PrisonMap pm = PrisonMap.Of(map);
                foreach (IntVec3 c in GenRadial.RadialCellsAround(PrisonBuilder.Footprint(map).CenterCell, 79f, true))
                {
                    CellRect around = new CellRect(c.x, c.z, width, depth).ExpandedBy(1);
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
                return IntVec3.Invalid;
            }

            /// <summary>The rig, or null with a problem noted (a premise that failed) when there is no open ground for it.</summary>
            public static CellRig Build(Map map, Rollback r, string what, params int[] bedsPerCell)
            {
                var s = new CellRig { map = map, pm = PrisonMap.Of(map), g = PrisonGame.Instance, r = r, what = what, level = InmateUtility.DefaultSecurity };
                PrisonZoneDef zone = DefDatabase<PrisonZoneDef>.AllDefsListForReading.FirstOrDefault(z => z.security == s.level);
                if (zone == null)
                {
                    CellFail($"{what}: premise: no wing zone for {s.level.defName}");
                    return null;
                }
                if (!IntakeSystem.WingAllows(s.level, s.gender)) s.gender = Gender.Female;
                if (!IntakeSystem.WingAllows(s.level, s.gender))
                {
                    CellFail($"{what}: premise: the {s.level.defName} wing takes neither sex");
                    return null;
                }
                // Put back last: the prison's own beds, listed again once the rig is gone.
                r.Add(() =>
                {
                    map.reachability.ClearCache();
                    Cellmates.InvalidateCells();
                    s.pm.ForceDoorUpdate();
                });
                int width = 1 + Pitch * bedsPerCell.Length;
                s.origin = OpenGround(map, width, Depth);
                if (!s.origin.IsValid)
                {
                    CellFail($"{what}: premise: no open ground outside the compound for {bedsPerCell.Length} cells (footprint {PrisonBuilder.Footprint(map)}, map {map.Size})");
                    return null;
                }
                bool policy0 = s.g.cellsByConduct;
                r.Add(() => s.g.cellsByConduct = policy0);
                s.g.cellsByConduct = true;
                int now = CellNow();
                foreach (Pawn o in map.mapPawns.PrisonersOfColony)
                {
                    if (o.Inmate() is not { } oc) continue;
                    int hold0 = oc.wardenPlacedTick;
                    r.Add(() => oc.wardenPlacedTick = hold0);
                    oc.wardenPlacedTick = now;
                }
                foreach (IntVec3 c in new CellRect(s.origin.x, s.origin.z, width, Depth).ExpandedBy(1))
                    foreach (Thing plant in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Plant).ToList())
                        plant.Destroy(DestroyMode.Vanish);
                for (int x = 0; x < width; x++)
                    foreach (int z in new[] { 0, Depth - 1 })
                        s.Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, s.At(x, z));
                for (int k = 0; k <= bedsPerCell.Length; k++)
                    for (int z = 1; z < Depth - 1; z++)
                        s.Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, s.At(k * Pitch, z));
                ThingDef bedDef = DefDatabase<ThingDef>.GetNamed("Bed");
                for (int k = 0; k < bedsPerCell.Length; k++)
                {
                    var cell = new List<Building_Bed>();
                    for (int j = 0; j < bedsPerCell[k]; j++)
                    {
                        var bed = s.Spawn<Building_Bed>(bedDef, ThingDefOf.WoodLog, s.At(k * Pitch + 1 + j, 1), Rot4.North);
                        bed.ForPrisoners = true;
                        r.Add(() =>
                        {
                            foreach (Pawn owner in bed.OwnersForReading.ToList()) owner.ownership?.UnclaimBed();
                        });
                        cell.Add(bed);
                    }
                    s.beds.Add(cell);
                    foreach (IntVec3 c in new CellRect(s.origin.x + k * Pitch + 1, s.origin.z + 1, 3, 3))
                    {
                        PrisonZoneDef was = s.pm.zones.ZoneAt(c);
                        r.Add(() => s.pm.zones.Set(c, was));
                        s.pm.zones.Set(c, zone);
                    }
                }
                // The roof last, taken off first (a roof left up when its walls came down collapses on whoever is there next).
                foreach (IntVec3 c in new CellRect(s.origin.x, s.origin.z, width, Depth))
                {
                    RoofDef was = map.roofGrid.RoofAt(c);
                    r.Add(() => map.roofGrid.SetRoof(c, was));
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
                map.reachability.ClearCache();
                s.pm.Notify_BedsChanged();
                s.pm.ForceDoorUpdate();
                var all = s.beds.SelectMany(x => x).ToHashSet();
                s.pm.PrisonerBeds.RemoveAll(b => !all.Contains(b));
                var rooms = new HashSet<Room>();
                for (int k = 0; k < s.beds.Count; k++)
                {
                    Room room = s.RoomOf(k);
                    if (room == null || !rooms.Add(room) || s.beds[k].Any(b => b.GetRoom() != room))
                    {
                        CellFail($"{what}: premise: cell {k} of the rig is not a room of its own with all its beds");
                        return null;
                    }
                }
                if (!IntakeSystem.FreePrisonerBeds(map, s.level, s.gender).All(all.Contains) || IntakeSystem.FreePrisonerBeds(map, s.level, s.gender).Count != all.Count)
                {
                    CellFail($"{what}: premise: the free beds are not the rig's alone");
                    return null;
                }
                return s;
            }
        }

        /// <summary>Two rival gangs (not in a truce) for the checks that need a clash; null when there are none (a premise failure, noted).</summary>
        private static (GangDef, GangDef) CellRivalGangs()
        {
            foreach (GangDef x in DefDatabase<GangDef>.AllDefsListForReading)
                foreach (GangDef y in x.rivals)
                    if (GangUtility.AreRivals(x, y)) return (x, y);
            CellFail("premise: no two rival gangs that are not in a truce");
            return (null, null);
        }

        /// <summary>An arrival's bed (`PickBed`): the grade breaks a tie only among beds with no clash - a friend and a gang mate in a cell with an enemy bring it within the band of an empty one (the re-review's case).</summary>
        private static void CheckCellPickBedNoClashForAGrade(Map map)
        {
            Guarded("cell quality: an arrival is not put with an enemy for a grade", map, r =>
            {
                // An empty grade 5 cell; a grade 1 cell with a gang mate who is their enemy, a gang mate who is their friend, and a free bed; their own grade 1 cell (a bed to stand in).
                CellRig s = CellRig.Build(map, r, "the arrival's bed", 1, 3, 1);
                if (s == null) return;
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                if (gang == null) { CellFail("the arrival's bed: premise: no gang def"); return; }
                Pawn a = s.Inmate(2, 0, 1), enemy = s.Inmate(1, 0, 1), friend = s.Inmate(1, 1, 1);
                CompInmate ac = a.Inmate();
                (ac.gang, enemy.Inmate().gang, friend.Inmate().gang) = (gang, gang, gang);
                PrisonRelations.MakeEnemies(a, enemy);
                a.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, friend);
                if (!PrisonRelations.Enemies(a, enemy) || !PrisonRelations.Friends(a, friend)) { CellFail("the arrival's bed: premise: the feud or the friendship did not take"); return; }
                s.Grades(5, 1, 1);
                Building_Bed empty = s.Bed(0, 0), withEnemy = s.Bed(1, 2);
                float socialEmpty = Cellmates.Fit(empty, ac), socialEnemy = Cellmates.Fit(withEnemy, ac);
                if (socialEmpty - socialEnemy <= 0f || socialEmpty - socialEnemy > CellQuality.SocialBand || CellQuality.Term(withEnemy, ac) <= CellQuality.Term(empty, ac))
                {
                    CellFail($"the arrival's bed: premise: the case is not the one asked for (social {socialEmpty} and {socialEnemy}, band {CellQuality.SocialBand}, terms {CellQuality.Term(empty, ac)} and {CellQuality.Term(withEnemy, ac)})");
                    return;
                }
                Building_Bed picked = Cellmates.PickBed(new List<Building_Bed> { empty, withEnemy }, ac);
                if (picked != empty) CellFail($"an arrival was put in the cell with their enemy for its grade (social {socialEnemy} against the empty cell's {socialEmpty})");
                // The control: with cells by conduct off the empty cell wins on the social fit alone, as before the grades.
                PrisonGame g = PrisonGame.Instance;
                bool policy0 = g.cellsByConduct;
                r.Add(() => g.cellsByConduct = policy0);
                g.cellsByConduct = false;
                if (Cellmates.PickBed(new List<Building_Bed> { empty, withEnemy }, ac) != empty) CellFail("with cells by conduct off an arrival was not given the empty cell");
                g.cellsByConduct = true;
            });
        }

        private static void CheckCellGradeBands(Map map)
        {
            Guarded("cell quality: the grade bands and their cache", map, r =>
            {
                // The bands are vanilla's own stages of impressiveness, capped at 5.
                (float score, int grade)[] samples = { (-50f, 0), (0f, 0), (19.9f, 0), (20f, 1), (29.9f, 1), (30f, 2), (40f, 3), (50f, 4), (64.9f, 4), (65f, 5), (500f, 5) };
                foreach ((float score, int grade) in samples)
                    if (CellQuality.GradeOf(score) != grade) CellFail($"impressiveness {score} is grade {CellQuality.GradeOf(score)} ({grade} expected)");
                // The term: asymmetric (living above what one has earned costs more than below) and nothing at the entitlement; it is apart from the social fit, which a grade never enters.
                for (int grade = 0; grade <= CellQuality.MaxGrade; grade++)
                    for (int ent = 0; ent <= CellQuality.MaxGrade; ent++)
                    {
                        float term = CellQuality.TermOf(grade, ent);
                        if (term > 0f || grade == ent && term != 0f) CellFail($"the term of grade {grade} for entitlement {ent} is {term}");
                    }
                // A shared cell is not a better one for its size: a dormitory is a 2 at the most, a double a 4 (the penitentiary's dormitory was a 5 for its space).
                if (CellQuality.CapForSleepers(5, 15) != CellQuality.DormCap || CellQuality.CapForSleepers(5, 3) != CellQuality.DormCap || CellQuality.CapForSleepers(5, 2) != CellQuality.DoubleCap
                    || CellQuality.CapForSleepers(5, 1) != 5 || CellQuality.CapForSleepers(1, 15) != 1)
                    CellFail($"the cap for shared cells: 15 sleep {CellQuality.CapForSleepers(5, 15)}, 3 {CellQuality.CapForSleepers(5, 3)}, 2 {CellQuality.CapForSleepers(5, 2)}, 1 {CellQuality.CapForSleepers(5, 1)}");
                {
                    PrisonMap dpm = PrisonMap.Of(map);
                    dpm.ForceDoorUpdate();
                    foreach (Room dorm in dpm.PrisonerBeds.Where(b => b.Spawned && !b.Medical).Select(b => b.GetRoom()).Where(x => x != null).Distinct())
                    {
                        int sleepers = dorm.ContainedBeds.Where(b => b.ForPrisoners && !b.Medical).Sum(b => b.SleepingSlotsCount);
                        if (sleepers >= 3 && dpm.GradeOf(dorm) > CellQuality.DormCap) CellFail($"a room that sleeps {sleepers} has the cached grade {dpm.GradeOf(dorm)} (a dormitory is {CellQuality.DormCap} at the most)");
                    }
                }
                if (CellQuality.TermOf(3, 1) >= CellQuality.TermOf(1, 3) || Math.Abs(CellQuality.TermOf(3, 1) + 2 * CellQuality.PerGradeAbove) > 0.001f)
                    CellFail($"the term's weights: two grades above {CellQuality.TermOf(3, 1)}, two below {CellQuality.TermOf(1, 3)}");

                PrisonMap pm = PrisonMap.Of(map);
                r.Add(() => pm.ForceDoorUpdate());
                pm.ForceDoorUpdate();
                // The cell with the lowest grade there is: the one furnishing can raise (an ordinary cell: a solitary or padded bed has no term at all, which a census run's pick of a solitary cell showed).
                Building_Bed bed = pm.PrisonerBeds.Where(x => x.Spawned && x.GetRoom() is { ProperRoom: true } && !PunishmentUtility.IsSolitaryBed(x) && !PaddedCells.IsPadded(x) && !x.Medical)
                    .OrderBy(x => pm.GradeOf(x.GetRoom())).FirstOrDefault();
                if (bed == null)
                {
                    CellFail("the cache check's premise: no prisoner bed in a room");
                    return;
                }
                Room room = bed.GetRoom();
                IntVec3 first = bed.Position;
                float impressiveness0 = room.GetStat(RoomStatDefOf.Impressiveness);
                int grade0 = pm.GradeOf(room);
                if (grade0 != CellQuality.GradeFor(room)) CellFail($"the cached grade of a cell is {grade0}, its impressiveness {impressiveness0:0} (and its beds) say {CellQuality.GradeFor(room)}");
                Pawn probe = Throwaway(map, r);
                CompInmate probeC = probe.Inmate();
                PrisonGame g = PrisonGame.Instance;
                (bool policy0, int ent0) = (g.cellsByConduct, probeC.entitlement);
                r.Add(() => (g.cellsByConduct, probeC.entitlement) = (policy0, ent0));
                g.cellsByConduct = true;
                // What the fit and the grade term say now, before the furnishing.
                float fitBefore = Cellmates.Fit(bed, probeC), termBefore = CellQuality.Term(bed, probeC);
                // Furnish it: the best floor there is and a lamp. The cache does not move (nor does anything that reads it) until the walk runs again.
                var cells = room.Cells.Where(x => x.Standable(map)).ToList();
                TerrainDef floor = DefDatabase<TerrainDef>.AllDefsListForReading.Where(t => t.BuildableByPlayer && !t.IsWater && t.GetStatValueAbstract(StatDefOf.Beauty) > 0f).OrderByDescending(t => t.GetStatValueAbstract(StatDefOf.Beauty)).FirstOrDefault();
                ThingDef lampDef = DefDatabase<ThingDef>.GetNamedSilentFail("StandingLamp");
                if (floor == null || lampDef == null)
                {
                    CellFail("the cache check's premise: no floor or lamp def to furnish a cell with");
                    return;
                }
                foreach (IntVec3 cell in cells)
                {
                    TerrainDef old = map.terrainGrid.TerrainAt(cell);
                    r.Add(() => map.terrainGrid.SetTerrain(cell, old));
                    map.terrainGrid.SetTerrain(cell, floor);
                }
                IntVec3 lampAt = cells.FirstOrDefault(x => x.GetEdifice(map) == null && x.GetFirstItem(map) == null && x.GetFirstPawn(map) == null && x != first && !GenAdj.OccupiedRect(first, bed.Rotation, bed.def.size).Contains(x));
                if (lampAt.IsValid && lampAt != default)
                {
                    var lamp = ThingMaker.MakeThing(lampDef, lampDef.MadeFromStuff ? GenStuff.DefaultStuffFor(lampDef) : null);
                    GenSpawn.Spawn(lamp, lampAt, map);
                    r.Add(() =>
                    {
                        if (!lamp.Destroyed) lamp.Destroy();
                    });
                }
                float impressiveness1 = room.GetStat(RoomStatDefOf.Impressiveness);
                // (A prison whose cells are all furnished already, or at the top grade, has no cell the furnishing can raise: told, not failed - the grade moves are checked on the rig's cells with set grades.)
                if (impressiveness1 <= impressiveness0)
                {
                    RPLog.Message($"[RimPrison:test] cell quality: the cache check not run, furnishing did not raise the cell's impressiveness ({impressiveness0:0} to {impressiveness1:0})");
                    return;
                }
                // Before the walk: nothing that reads the grade has moved.
                if (pm.GradeOf(room) != grade0 || Cellmates.Fit(bed, probeC) != fitBefore || CellQuality.Term(bed, probeC) != termBefore)
                    CellFail($"a furnished cell (impressiveness {impressiveness0:0} to {impressiveness1:0}) changed before the bed walk ran: the grade {grade0} to {pm.GradeOf(room)}, the fit {fitBefore} to {Cellmates.Fit(bed, probeC)}, the term {termBefore} to {CellQuality.Term(bed, probeC)} - it is read from the room, not from the cache");
                pm.ForceDoorUpdate();
                int grade1 = pm.GradeOf(room);
                if (grade1 != CellQuality.GradeFor(room)) CellFail($"after the walk the cached grade is {grade1}, the room's impressiveness {impressiveness1:0} (and its beds) say {CellQuality.GradeFor(room)}");
                if (grade1 <= grade0) RPLog.Message($"[RimPrison:test] cell quality: the cache check's last part not run, the furnished cell (impressiveness {impressiveness0:0} to {impressiveness1:0}) did not rise in grade ({grade0} to {grade1})");
                // After it: the grade term has moved with the grade; the social fit has not (a grade is no part of it).
                else if (CellQuality.Term(bed, probeC) == termBefore || Cellmates.Fit(bed, probeC) != fitBefore)
                    CellFail($"after the walk (grade {grade0} to {grade1}) the term is {termBefore} to {CellQuality.Term(bed, probeC)} (a change expected) and the fit {fitBefore} to {Cellmates.Fit(bed, probeC)} (none expected) {CellWhy(bed, probeC)}");
                RPLog.Message($"[RimPrison:test] cell quality: a cell furnished: impressiveness {impressiveness0:0} to {impressiveness1:0}, grade {grade0} to {grade1}");
            });
        }

        private static void CheckCellEntitlementClock(Map map)
        {
            Guarded("cell quality: the entitlement's clock", map, r =>
            {
                var others = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(x => x != null).Select(x => (c: x, ent: x.entitlement, tick: x.entitlementTick)).ToList();
                r.Add(() =>
                {
                    foreach ((CompInmate c, int ent, int tick) in others) (c.entitlement, c.entitlementTick) = (ent, tick);
                });
                Pawn p = Throwaway(map, r);
                CompInmate pc = p.Inmate();
                pc.activeFlag = true;
                if (!map.mapPawns.PrisonersOfColonySpawned.Contains(p))
                {
                    CellFail("the check's own premise: the throwaway is not among the prisoners");
                    return;
                }
                int day = GenDate.TicksPerDay;
                if (pc.entitlement != CellQuality.PlainCell) CellFail($"a new arrival is entitled to grade {pc.entitlement}, not {CellQuality.PlainCell}");
                // The clock of a new arrival starts at the first day; nothing is earned then.
                pc.entitlementTick = CellQuality.Unset;
                CellQuality.Daily(map);
                if (pc.entitlement != CellQuality.PlainCell || pc.entitlementTick != CellNow()) CellFail($"the first daily pass gave entitlement {pc.entitlement} and started the clock at {pc.entitlementTick} (now {CellNow()}): it should start the clock and give nothing");
                // One step for the clean days, not before.
                pc.entitlementTick = CellNow() - (CellQuality.StepDays - 1) * day;
                CellQuality.Daily(map);
                if (pc.entitlement != 1) CellFail($"{CellQuality.StepDays - 1} clean days raised the entitlement to {pc.entitlement}");
                pc.entitlementTick = CellNow() - CellQuality.StepDays * day;
                CellQuality.Daily(map);
                if (pc.entitlement != 2 || CellNow() - pc.entitlementTick > 1) CellFail($"{CellQuality.StepDays} clean days gave entitlement {pc.entitlement} (2 expected), the clock at {pc.entitlementTick} (now {CellNow()})");
                if (CellQuality.DaysToNext(pc, CellNow()) != CellQuality.StepDays) CellFail($"just after a step the file says {CellQuality.DaysToNext(pc, CellNow())} days to the next ({CellQuality.StepDays} expected)");
                pc.entitlementTick = CellNow() - 3 * day;
                if (CellQuality.DaysToNext(pc, CellNow()) != CellQuality.StepDays - 3) CellFail($"three days in, the file says {CellQuality.DaysToNext(pc, CellNow())} days to the next ({CellQuality.StepDays - 3} expected)");
                // A passed program is a step; the top is 5.
                CellQuality.Notify_ProgramPassed(pc);
                if (pc.entitlement != 3) CellFail($"a passed program gave entitlement {pc.entitlement} (3 expected)");
                pc.entitlement = CellQuality.MaxGrade;
                CellQuality.Notify_ProgramPassed(pc);
                pc.entitlementTick = CellNow() - CellQuality.StepDays * day;
                CellQuality.Daily(map);
                if (pc.entitlement != CellQuality.MaxGrade) CellFail($"the entitlement went past the top: {pc.entitlement}");
                // A seen misconduct of severity 2 or more: down two at once, the clock starts again; the floor is 0.
                MisconductDef fight = DefDatabase<MisconductDef>.GetNamed("RP_Fight"), insult = DefDatabase<MisconductDef>.GetNamed("RP_Insult");
                pc.entitlementTick = CellNow() - 3 * day;
                CellQuality.Notify_Misconduct(pc, insult, true);
                CellQuality.Notify_Misconduct(pc, fight, false);
                if (pc.entitlement != CellQuality.MaxGrade || pc.entitlementTick != CellNow() - 3 * day) CellFail($"a severity 1 misconduct, and a fight nobody saw, cost entitlement ({pc.entitlement}) or restarted the clock");
                CellQuality.Notify_Misconduct(pc, fight, true);
                if (pc.entitlement != CellQuality.MaxGrade - CellQuality.FallBy || pc.entitlementTick != CellNow()) CellFail($"a seen fight left entitlement {pc.entitlement} ({CellQuality.MaxGrade - CellQuality.FallBy} expected) and the clock at {pc.entitlementTick}");
                pc.entitlement = 1;
                CellQuality.Notify_Misconduct(pc, fight, true);
                if (pc.entitlement != 0) CellFail($"the entitlement went below the floor: {pc.entitlement}");
                // The real path: MisconductUtility.Record cuts it the same way (and not for one nobody saw).
                pc.entitlement = 4;
                PrisonMap pm = PrisonMap.Of(map);
                MisconductUtility.Record(p, pc, pm, fight, chronicle: false, unseen: true);
                if (pc.entitlement != 4) CellFail($"a fight nobody saw cost entitlement through Record ({pc.entitlement})");
                MisconductUtility.Record(p, pc, pm, fight, evidence: true, chronicle: false);
                if (pc.entitlement != 4 - CellQuality.FallBy) CellFail($"a seen fight through Record left entitlement {pc.entitlement} ({4 - CellQuality.FallBy} expected)");
            });
        }

        private static void CheckCellsMoveUpByConduct(Map map)
        {
            Guarded("cell quality: a clean inmate is moved up to the cell they have earned", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "the move up", 1, 1);
                if (s == null) return;
                // Earned: three, and a passed program is a step to grade 4 (the cell they live in is bare, a furnished one is free beside it).
                Pawn a = s.Inmate(0, 0, 3);
                CompInmate ac = a.Inmate();
                ac.entitlementTick = CellNow() - (CellQuality.StepDays - 1) * GenDate.TicksPerDay;
                CellQuality.Notify_ProgramPassed(ac);
                s.Grades(0, 4);
                if (!Cellmates.ReviewWouldForGrade(a, ac) || Cellmates.ReviewWould(a, ac))
                {
                    CellFail($"the move up's premise: the review would look at {a.LabelShort} for their cell's grade {Cellmates.ReviewWouldForGrade(a, ac)}, for a bad fit {Cellmates.ReviewWould(a, ac)}");
                    return;
                }
                // The grade is in the term, and nothing of it is in the social fit.
                float termHome = CellQuality.Term(s.Bed(0, 0), ac), termFree = CellQuality.Term(s.Bed(1, 0), ac);
                if (termHome != -4f || termFree != 0f) CellFail($"the grade term of the bare cell is {termHome} and of the furnished one {termFree} (-4 and 0 expected for entitlement 4)");
                if (Cellmates.Fit(s.Bed(1, 0), ac) != Cellmates.Fit(s.Bed(0, 0), ac)) CellFail($"the social fit differs between two empty cells of one wing ({Cellmates.Fit(s.Bed(0, 0), ac)} and {Cellmates.Fit(s.Bed(1, 0), ac)}): the grade is in it");
                // The first review that sees the mismatch moves no one; the next day's does.
                int moved = Cellmates.DailyReview(map);
                if (moved != 0 || s.CellOf(a) != 0 || ac.gradeMismatchTick != CellNow()) CellFail($"the first review that saw the mismatch moved {moved} (none expected) and left the mismatch noted at {ac.gradeMismatchTick} (now {CellNow()})");
                ac.gradeMismatchTick -= GenDate.TicksPerDay;
                moved = Cellmates.DailyReview(map);
                string expected = "RP_Chr_CellMoveUp".Translate(a.LabelShort, CellQuality.CleanDays(ac, CellNow())).Resolve();
                if (moved < 1 || s.CellOf(a) != 1)
                {
                    // (What the grade move's search sees of the furnished cell, step by step.)
                    Building_Bed target = s.Bed(1, 0), from = a.ownership.OwnedBed;
                    List<Building_Bed> free = IntakeSystem.FreePrisonerBeds(map, ac.security, a.gender);
                    string search = $"target {target.Position}: in the free list {free.Contains(target)} ({free.Count} free), own room {Cellmates.Home(a, ac) == target.GetRoom()}, accepts {Cellmates.Accepts(target, ac)}, " +
                                    $"padded {PaddedCells.IsPadded(target)}, term {CellQuality.Term(target, ac)} needed {CellQuality.Term(from, ac) + CellQuality.MoveBetterBy}, social {Cellmates.Fit(target, ac)} now {Cellmates.Fit(from, ac)}, " +
                                    $"best for grade {Cellmates.BestFreeForGrade(a, ac)?.Position.ToString() ?? "none"}, review would {Cellmates.ReviewWouldForGrade(a, ac)}, mismatch {ac.gradeMismatchTick} moved {ac.gradeMovedTick} now {CellNow()}, " +
                                    $"owners of the target {string.Join(",", target.OwnersForReading.Select(o => o.LabelShort))}, home bed {ac.homeBed?.Position}";
                    CellFail($"{a.LabelShort} earned grade 4 in a grade 0 cell with a grade 4 cell free: the second review moved {moved} and they live in cell {s.CellOf(a)} {CellWhy(a.ownership.OwnedBed, a.Inmate())} {search}");
                }
                else if (!r.NewEntries.Any(e => e.text == expected)) CellFail($"the move up wrote no chronicle line \"{expected}\" (lines: {string.Join(" | ", r.NewEntries.Select(e => e.text))})");
                else if (ac.gradeMovedTick != CellNow() || ac.gradeMismatchTick != CellQuality.NoTick) CellFail($"after the move up the grade move is noted at {ac.gradeMovedTick} (now {CellNow()}) and the mismatch at {ac.gradeMismatchTick} (-1 expected)");
            });
        }

        private static void CheckCellsMoveDownByConduct(Map map)
        {
            Guarded("cell quality: a misbehaving inmate is moved down", map, r =>
            {
                // A furnished grade 4 cell to live in and two free ones: a grade 1 cell and a grade 3 one.
                CellRig s = CellRig.Build(map, r, "the move down", 1, 1, 1);
                if (s == null) return;
                Pawn a = s.Inmate(0, 0, 3);
                CompInmate ac = a.Inmate();
                s.Grades(4, 1, 3);
                // Entitled to 3 while clean: one grade above it, nothing to move. A seen fight takes two off at once: three grades above what they have earned, with plainer cells free.
                if (Cellmates.ReviewWouldForGrade(a, ac)) CellFail($"{a.LabelShort} one grade above their entitlement is looked at for a move");
                MisconductDef fight = DefDatabase<MisconductDef>.GetNamed("RP_Fight");
                ac.records.Add(new MisconductRecord { def = fight, tick = CellNow(), seen = true });
                CellQuality.Notify_Misconduct(ac, fight, true);
                if (ac.entitlement != 1 || !Cellmates.ReviewWouldForGrade(a, ac) || Cellmates.ReviewWould(a, ac))
                {
                    CellFail($"the move down's premise: entitlement {ac.entitlement} after a seen fight (1 expected), looked at for the grade {Cellmates.ReviewWouldForGrade(a, ac)}");
                    return;
                }
                int moved = s.Review(ac);
                string expected = "RP_Chr_CellMoveDown".Translate(a.LabelShort, fight.label).Resolve();
                // (Any worse cell they are entitled to will do: the best of them is the grade 1 one, but the check does not name a bed.)
                int grade = CellQuality.GradeAt(a.ownership.OwnedBed);
                if (moved < 1 || s.CellOf(a) == 0 || grade < 0 || grade > ac.entitlement || IntakeSystem.WingOf(a.ownership.OwnedBed) != IntakeSystem.WingOf(s.Bed(0, 0)))
                    CellFail($"{a.LabelShort} fought, living in grade 4 with cells of grade 1 and 3 free: the review moved {moved} and they live in cell {s.CellOf(a)}, grade {grade} (entitled to {ac.entitlement}) {CellWhy(a.ownership.OwnedBed, ac)}");
                else if (!r.NewEntries.Any(e => e.text == expected)) CellFail($"the move down wrote no chronicle line \"{expected}\" (lines: {string.Join(" | ", r.NewEntries.Select(e => e.text))})");
                // Without a fight to name, the line says they live above what they have earned (and the days since the last grade move have passed).
                s.PutBack(a, s.Bed(0, 0));
                ac.records.Clear();
                ac.gradeMovedTick = CellQuality.NoTick;
                int entries0 = r.NewEntries.Count();
                if (!Cellmates.ReviewWouldForGrade(a, ac) || s.Review(ac) < 1 || !r.NewEntries.Skip(entries0).Any(e => e.text == "RP_Chr_CellMoveDownPlain".Translate(a.LabelShort).Resolve()))
                    CellFail($"with no misconduct on record the move down did not write the plain line (they live in cell {s.CellOf(a)})");
            });
        }

        /// <summary>One case of the grade review's: an inmate entitled to <see cref="entitlement"/> in a cell of <see cref="homeGrade"/> with <see cref="homeMates"/> neutral mates, and a free bed in a cell of <see cref="targetGrade"/> (with a neutral occupant when asked).</summary>
        private sealed class CellGradeCase
        {
            public string name;
            public int homeMates, homeGrade = 0, targetGrade = 4, entitlement = 4;
            public bool targetOccupied;
            /// <summary>Whether the review looks at them for their grade at all (not so under a hold or with the policy off), and whether the case ends with them moved.</summary>
            public bool looked = true, moves = true;
            /// <summary>What differs from the plain case: given the rig, the inmate, and the occupant of the better cell (null when none).</summary>
            public Action<CellRig, Pawn, Pawn> change;
        }

        private static void CheckCellsGradeCase(Map map, CellGradeCase k)
        {
            Guarded($"cell quality: the grade review, {k.name}", map, r =>
            {
                CellRig s = CellRig.Build(map, r, k.name, k.homeMates + 1, k.targetOccupied ? 2 : 1);
                if (s == null) return;
                Pawn a = s.Inmate(0, 0, k.entitlement);
                CompInmate ac = a.Inmate();
                for (int i = 1; i <= k.homeMates; i++) s.Inmate(0, i, k.homeGrade);
                Pawn occupant = k.targetOccupied ? s.Inmate(1, 0, k.targetGrade) : null;
                s.Grades(k.homeGrade, k.targetGrade);
                k.change?.Invoke(s, a, occupant);
                if (Cellmates.ReviewWould(a, ac))
                {
                    CellFail($"{k.name}: premise: the review looks at {a.LabelShort} for a bad fit");
                    return;
                }
                if (Cellmates.ReviewWouldForGrade(a, ac) != k.looked)
                {
                    CellFail($"{k.name}: the review {(k.looked ? "does not look" : "looks")} at them for their cell's grade");
                    return;
                }
                s.Review(ac);
                bool moved = s.CellOf(a) == 1;
                if (moved != k.moves) CellFail($"{k.name}: {(k.moves ? "they were not moved to the cell they have earned" : "the review moved them")} (they live in cell {s.CellOf(a)})");
            });
        }

        private static void CheckCellsByConductNeverMoves(Map map)
        {
            (GangDef gangX, GangDef gangY) = CellRivalGangs();
            if (gangX == null) return;
            var cases = new List<CellGradeCase>
            {
                // The controls: each case below differs from one of these in one thing.
                new CellGradeCase { name = "the control: a clean inmate, an empty better cell" },
                new CellGradeCase { name = "the control: the better cell has a mate, and so has theirs", homeMates = 1, targetOccupied = true },
                new CellGradeCase { name = "the control: a mate who shares their gang in the better cell, and two mates at home", homeMates = 2, targetOccupied = true, change = (s, a, o) => (a.Inmate().gang, o.Inmate().gang) = (gangX, gangX) },
                // Social first: a better cell that costs socially is no move (the alone-in-a-cell bonus), one that costs the same or less is.
                new CellGradeCase { name = "a better cell with a mate, from a cell of their own", targetOccupied = true, moves = false },
                new CellGradeCase { name = "an empty better cell, from a cell with a mate", homeMates = 1 },
                // Holds.
                new CellGradeCase { name = "with cells by conduct off", looked = false, moves = false, change = (s, a, o) => s.g.cellsByConduct = false },
                new CellGradeCase { name = "placed by the warden today", looked = false, moves = false, change = (s, a, o) => Cellmates.PlacedByWarden(a.Inmate()) },
                new CellGradeCase { name = "placed by the warden after the hold lapsed", change = (s, a, o) => a.Inmate().wardenPlacedTick = CellNow() - (Cellmates.WardenHoldDays + 1) * GenDate.TicksPerDay },
                // Clashes: never into one, whatever the grade is worth.
                new CellGradeCase { name = "a rival gang lives in the better cell", homeMates = 1, targetOccupied = true, moves = false, change = (s, a, o) => (a.Inmate().gang, o.Inmate().gang) = (gangX, gangY) },
                new CellGradeCase
                {
                    name = "an extortionist who shares their gang lives in the better cell (no worse socially, so only the clash keeps them out)", homeMates = 2, targetOccupied = true, moves = false,
                    change = (s, a, o) =>
                    {
                        (a.Inmate().gang, o.Inmate().gang) = (gangX, gangX);
                        o.Inmate().reputations.Add(RPReputationDefOf.RP_Extortionist);
                    },
                },
                // The case of a grade review that traded a clash for a grade: an inmate entitled to 0 in a grade 5 cell with a neutral mate, a grade 0 bed beside an enemy of their own gang.
                new CellGradeCase { name = "the control: entitled to 0 in a grade 5 cell, the grade 0 cell has a neutral occupant", homeMates = 1, homeGrade = 5, targetGrade = 0, entitlement = 0, targetOccupied = true },
                new CellGradeCase
                {
                    name = "entitled to 0 in a grade 5 cell, the grade 0 cell has an enemy of their gang", homeMates = 1, homeGrade = 5, targetGrade = 0, entitlement = 0, targetOccupied = true, moves = false,
                    change = (s, a, o) =>
                    {
                        (a.Inmate().gang, o.Inmate().gang) = (gangX, gangX);
                        PrisonRelations.MakeEnemies(a, o);
                    },
                },
            };
            foreach (CellGradeCase k in cases) CheckCellsGradeCase(map, k);
            CheckCellsTheLadderLeavesAlone(map);
        }

        /// <summary>Protective custody, psychiatric care, supermax and death row keep their own rules, and a padded bed and a solitary bed have no grade to live in: no term, no move.</summary>
        private static void CheckCellsTheLadderLeavesAlone(Map map)
        {
            Guarded("cell quality: who the ladder does not cover", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "the ladder's limits", 1, 1);
                if (s == null) return;
                Pawn a = s.Inmate(0, 0, 4);
                CompInmate ac = a.Inmate();
                s.Grades(0, 4);
                if (!Cellmates.ReviewWouldForGrade(a, ac)) CellFail("the ladder's premise: the review does not look at the control inmate for their grade");
                SecurityLevelDef security0 = ac.security;
                var special = new List<(string, SecurityLevelDef)>
                {
                    ("in protective custody", Trials.ProtectiveLevel), ("in psychiatric care", PsychWing.Level), ("in supermax", SupermaxSystem.Level),
                    ("on death row", DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(x => x.GetModExtension<DeathRowExtension>() != null)),
                };
                foreach ((string why, SecurityLevelDef level) in special)
                {
                    if (level == null)
                    {
                        CellFail($"the ladder's premise: no level for an inmate {why}");
                        continue;
                    }
                    ac.security = level;
                    if (CellQuality.Applies(ac) || CellQuality.Term(s.Bed(0, 0), ac) != 0f || Cellmates.ReviewWouldForGrade(a, ac)) CellFail($"{why}: the ladder still covers them");
                }
                ac.security = security0;
                // A padded bed has no grade; a solitary bed neither, and an inmate whose own bed is solitary is not looked at.
                ThingDef padded = RPPsychWingDefOf.RP_PaddedBed;
                if (padded == null || ThingMaker.MakeThing(padded, padded.MadeFromStuff ? GenStuff.DefaultStuffFor(padded) : null) is not Building_Bed paddedBed) CellFail("the ladder's premise: no padded bed to ask");
                else if (CellQuality.Term(paddedBed, ac) != 0f) CellFail("a padded bed has a grade term");
                InsideZone(s.pm, r, RPSecurityDefOf.RP_SolitaryZone, s.Bed(1, 0).OccupiedRect().Cells);
                if (!PunishmentUtility.IsSolitaryBed(s.Bed(1, 0)) || CellQuality.Term(s.Bed(1, 0), ac) != 0f) CellFail("a solitary bed has a grade term");
                InsideZone(s.pm, r, RPSecurityDefOf.RP_SolitaryZone, s.Bed(0, 0).OccupiedRect().Cells);
                if (Cellmates.ReviewWouldForGrade(a, ac)) CellFail("an inmate whose own bed is solitary is looked at for a grade move");
            });
        }

        /// <summary>A clash is decided on the social fit alone, whatever the grades are worth.</summary>
        private static void CheckCellsClashBeatsGrade(Map map)
        {
            Guarded("cell quality: a rival pair is split though the free cell is a worse grade for them", map, r =>
            {
                (GangDef gangX, GangDef gangY) = CellRivalGangs();
                if (gangX == null) return;
                // Two inmates entitled to 0 who share a grade 0 cell as rivals, and one empty grade 5 cell: before the grade was kept apart from the fit it scored 20 - 7.5, under what the review asks (9 + 5).
                CellRig s = CellRig.Build(map, r, "the rival pair", 2, 1);
                if (s == null) return;
                Pawn a = s.Inmate(0, 0, 0), b = s.Inmate(0, 1, 0);
                CompInmate ac = a.Inmate(), bc = b.Inmate();
                (ac.gang, bc.gang) = (gangX, gangY);
                s.Grades(0, 5);
                float fitHere = Cellmates.Fit(s.Bed(0, 0), ac), fitFree = Cellmates.Fit(s.Bed(1, 0), ac), term = CellQuality.Term(s.Bed(1, 0), ac);
                if (!Cellmates.ReviewWould(a, ac) || fitFree + term >= fitHere + Cellmates.BetterBy)
                {
                    CellFail($"the rival pair's premise: the review would split them {Cellmates.ReviewWould(a, ac)}, the free cell is worth {fitFree} + {term} against {fitHere} + {Cellmates.BetterBy} (the grade must be what would have kept them together)");
                    return;
                }
                int moved = Cellmates.DailyReview(map);
                if (moved < 1 || s.CellOf(a) == s.CellOf(b)) CellFail($"two rivals entitled to grade 0 with an empty grade 5 cell free were left together (the review moved {moved})");
            });
        }

        /// <summary>The review's quota goes to the bad fits first.</summary>
        private static void CheckCellsQuotaOrder(Map map)
        {
            Guarded("cell quality: the quota goes to a rival pair before a grade", map, r =>
            {
                (GangDef gangX, GangDef gangY) = CellRivalGangs();
                if (gangX == null) return;
                // Two inmates who would each move up (cells 0 and 1, grade 0, entitled to 4), a rival pair (cell 2) and two free single cells (3 and 4, grade 4): two moves a day.
                CellRig s = CellRig.Build(map, r, "the quota order", 1, 1, 2, 1, 1);
                if (s == null) return;
                Pawn q1 = s.Inmate(0, 0, 4), q2 = s.Inmate(1, 0, 4), x = s.Inmate(2, 0, 2), y = s.Inmate(2, 1, 2);
                (x.Inmate().gang, y.Inmate().gang) = (gangX, gangY);
                s.Grades(0, 0, 2, 4, 4);
                if (!Cellmates.ReviewWould(x, x.Inmate()) || !Cellmates.ReviewWouldForGrade(q1, q1.Inmate()) || !Cellmates.ReviewWouldForGrade(q2, q2.Inmate()))
                {
                    CellFail("the quota order's premise: the review does not look at the rivals for a bad fit and both of the others for their grade");
                    return;
                }
                int moved = s.Review(q1.Inmate(), q2.Inmate());
                int ups = (s.CellOf(q1) >= 3 ? 1 : 0) + (s.CellOf(q2) >= 3 ? 1 : 0);
                if (s.CellOf(x) == s.CellOf(y) || moved != Cellmates.MovesPerDay || ups != 1)
                    CellFail($"the review moved {moved} ({Cellmates.MovesPerDay} expected): the rivals live in cells {s.CellOf(x)} and {s.CellOf(y)} (apart expected), {ups} of the two for their grade (1 expected)");
            });
        }

        /// <summary>Two candidates from one room: the second is judged where it stands after the first has moved.</summary>
        private static void CheckCellsSecondCandidateKeepsItsFit(Map map)
        {
            Guarded("cell quality: the second inmate of a room does not take a socially worse bed", map, r =>
            {
                // Two who would each move up share cell 0 (a mate each: 19); cell 1 is a free bed of their own (20), cell 2 a free bed beside an occupant (19). The first takes cell 1; the second, alone now (20), must not take cell 2.
                CellRig s = CellRig.Build(map, r, "the second candidate", 2, 1, 2);
                if (s == null) return;
                Pawn a = s.Inmate(0, 0, 4), b = s.Inmate(0, 1, 4);
                s.Inmate(2, 0, 4);
                s.Grades(0, 4, 4);
                if (!Cellmates.ReviewWouldForGrade(a, a.Inmate()) || !Cellmates.ReviewWouldForGrade(b, b.Inmate()))
                {
                    CellFail("the second candidate's premise: the review does not look at both of them for their grade");
                    return;
                }
                int moved = s.Review(a.Inmate(), b.Inmate());
                int in1 = (s.CellOf(a) == 1 ? 1 : 0) + (s.CellOf(b) == 1 ? 1 : 0), in2 = (s.CellOf(a) == 2 ? 1 : 0) + (s.CellOf(b) == 2 ? 1 : 0);
                if (moved != 1 || in1 != 1 || in2 != 0) CellFail($"the review moved {moved} (1 expected): {in1} of them live in the free cell of their own (1 expected), {in2} beside an occupant (none expected: a worse fit than staying)");
            });
        }

        /// <summary>No one moves for a mismatch seen once, and no one moves back at once.</summary>
        private static void CheckCellsGradeMovesDoNotChurn(Map map)
        {
            Guarded("cell quality: no churn in the grade moves", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "the churn", 1, 1);
                if (s == null) return;
                Pawn a = s.Inmate(0, 0, 4);
                CompInmate ac = a.Inmate();
                s.Grades(0, 4);
                int day = GenDate.TicksPerDay;
                // A mismatch seen once moves no one: the first review notes it (and a second ask of the same pass changes nothing), one two days on starts over, one a day on moves them.
                Cellmates.DailyReview(map);
                int first = ac.gradeMismatchTick;
                Cellmates.DailyReview(map);
                if (s.CellOf(a) != 0 || first != CellNow() || ac.gradeMismatchTick != first) CellFail($"the first review moved them (cell {s.CellOf(a)}) or did not note the mismatch at once ({first}, then {ac.gradeMismatchTick}; now {CellNow()})");
                ac.gradeMismatchTick = CellNow() - 2 * day;
                Cellmates.DailyReview(map);
                if (s.CellOf(a) != 0 || ac.gradeMismatchTick != CellNow()) CellFail($"a mismatch seen two days ago moved them (cell {s.CellOf(a)}) or was not started over ({ac.gradeMismatchTick}; now {CellNow()})");
                // (The room is clean again: no mismatch for a day, so that a dirty day is not counted.)
                ac.entitlement = 0;
                ac.gradeMismatchTick = CellNow() - day;
                Cellmates.DailyReview(map);
                if (s.CellOf(a) != 0 || ac.gradeMismatchTick != CellNow() - day) CellFail($"with no mismatch the review moved them (cell {s.CellOf(a)}) or touched the note ({ac.gradeMismatchTick})");
                ac.entitlement = 4;
                if (s.Review(ac) < 1 || s.CellOf(a) != 1 || ac.gradeMovedTick != CellNow()) CellFail($"the control: a mismatch seen at two reviews in a row did not move them (cell {s.CellOf(a)})");
                // They are not moved back at once: their entitlement has dropped to 0 and they live in grade 4, with their old grade 0 cell free, but the grade move was today.
                ac.entitlement = 0;
                s.Review(ac);
                if (s.CellOf(a) != 1) CellFail($"moved for their grade, they were moved back for it the next day (cell {s.CellOf(a)})");
                // (Three days on, the same case moves them: the stay was the cooldown's.)
                ac.gradeMovedTick = CellNow() - CellQuality.MoveCooldownDays * day + 1;
                s.Review(ac);
                if (s.CellOf(a) != 1) CellFail($"just inside the {CellQuality.MoveCooldownDays} days they were moved for their grade again (cell {s.CellOf(a)})");
                ac.gradeMovedTick = CellNow() - CellQuality.MoveCooldownDays * day;
                s.Review(ac);
                if (s.CellOf(a) != 0) CellFail($"after the {CellQuality.MoveCooldownDays} days the control did not move them (cell {s.CellOf(a)})");
            });
        }
    }
}
