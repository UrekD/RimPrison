using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Trusties and the outside work party (see <see cref="Trusties"/>). A small wing on open ground outside the compound (a corridor, two cells, a gate door to a strip painted for the outside work, and two other doors
    /// to the outside: a staff room's and a cell's, the controls), with throwaway inmates and guards; the gate, the way back, the exemption from the breach rule, the walk-off, the lifecycle of the status and the job, the
    /// placement, the work and the pay, the party's conditions and the walk of a stranded trusty. Each check says what it would see without the change it guards, or is a control.
    /// </summary>
    public static partial class AutoTest
    {
        private static void TrustyFail(string check, string what) => problems0.Add($"trusties ({check}): {what}");

        /// <summary>
        /// The crop a check's growing cells are sown with, the premise of the grower checks: potato first, then any other sowable crop, that the soil allows and the season allows now (the events plan's season and map vary, and a
        /// cold map has no such crop). The cells get soil that grows (restored by the rollback). When the weather leaves no crop, the growing season is held on over those cells for the check (<see cref="TrustySeason"/>, let go by the
        /// rollback, the way the shuttle checks pin the weather) and the premise is told, not failed; null only when the soil or the cells themselves take no crop.
        /// </summary>
        private static ThingDef TrustyGrowerCrop(Map map, Rollback r, CellRect cells, string check)
        {
            foreach (IntVec3 cell in cells)
            {
                TerrainDef terrain0 = cell.GetTerrain(map);
                if (terrain0.fertility > 0f) continue;
                r.Add(() => map.terrainGrid.SetTerrain(cell, terrain0));
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
            }
            List<ThingDef> crops = new[] { ThingDefOf.Plant_Potato }.Concat(DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.plant is { Sowable: true, harvestedThingDef: not null } && d != ThingDefOf.Plant_Potato)).ToList();
            ThingDef crop = crops.FirstOrDefault(d => cells.Cells.All(cell => PlantUtility.GrowthSeasonNow(cell, map, d) && d.CanEverPlantAt(cell, map)));
            if (crop != null) return crop;
            // (The sowing's own test is whether the crop can be planted now, which leaves the map's yearly extremes out.)
            crop = crops.FirstOrDefault(d => cells.Cells.All(cell => d.CanEverPlantAt(cell, map, false, false)));
            if (crop == null) return null;
            TrustyHoldSeason(map, r, cells);
            RPLog.Message($"[RimPrison:test] trusties ({check}): no crop grows at {map.mapTemperature.OutdoorTemp:F0} C outdoors (seasonal {map.mapTemperature.SeasonalTemp:F0} C), so the growing season is held on over the check's cells for {crop.defName}");
            return crop;
        }

        /// <summary>
        /// The growing season is held on over these cells, and the cold held off the plants on them, until the rollback (see <see cref="TrustySeason"/>): a plant on a cold map dies of the cold the moment it is spawned (the game's own check of a
        /// plant that is spawned or grown), so a check's seedlings and crop plants would be dead, and the cells they stood on empty and sowable, before the check looked.
        /// </summary>
        private static void TrustyHoldSeason(Map map, Rollback r, CellRect cells)
        {
            (TrustySeason.Map, TrustySeason.Cells) = (map, cells);
            r.Add(() => (TrustySeason.Map, TrustySeason.Cells) = (null, default));
        }

        /// <summary>The text of the messages on screen now.</summary>
        private static List<string> TrustyLiveMessages()
        {
            var live = (List<Message>)typeof(Messages).GetField("liveMessages", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            return live?.Select(m => m.text).ToList() ?? new List<string>();
        }

        /// <summary>
        /// How many chronicle lines and messages on screen are the revoke's own wording about this inmate (the lines of "lost trusty status" and "is a trusty no more"; the reason is cut off the end). The count, not the presence: a check that
        /// had a loud revoke earlier still sees only what a later call adds.
        /// </summary>
        private static int TrustyRevokeTold(Rollback r, Pawn p)
        {
            string chronicle = "RP_Chr_TrustyLost".Translate(p.LabelShort, "\u0001").Resolve(), message = "RP_TrustyLost".Translate(p.LabelShort, "\u0001").Resolve();
            chronicle = chronicle.Substring(0, chronicle.IndexOf('\u0001'));
            message = message.Substring(0, message.IndexOf('\u0001'));
            return r.NewEntries.Count(e => e.text.StartsWith(chronicle)) + TrustyLiveMessages().Count(m => m.StartsWith(message));
        }

        /// <summary>A line on a pawn for the text of a check that failed.</summary>
        private static string TrustyLine(TrustyScene s, Pawn p)
        {
            CompInmate c = p.Inmate();
            TrustyParty party = s.pm.party;
            return $"{p.LabelShort} at {p.Position - s.origin} (outside space {s.pm.IsOutsideSpace(p.Position)}, zone {s.pm.zones.ZoneAt(p.Position)?.defName ?? "none"}) doing {p.CurJobDef?.defName ?? "nothing"}, mental {p.MentalStateDef?.defName ?? "none"}, "
                   + $"trusty {c?.IsTrusty}, job {c?.job?.defName ?? "none"}, pass {c?.workPass?.defName ?? "none"}/{c?.outsidePass}, activity {(c != null ? PrisonGame.Instance.CurrentActivity(p).defName : "?")}; "
                   + $"party {party.state} out {party.isOut} (holders {party.holders}, guards near {party.near}, hold until {party.holdUntilTick}, now {Find.TickManager.TicksGame})";
        }

        private sealed class TrustyScene
        {
            public const int Width = 21, Height = 13, SouthPad = 8;
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly Rollback r;
            public readonly string check;
            public IntVec3 origin;
            public Building_Door gate, staffDoor, cellDoorOut, bedroomDoor, bedroomDoor2;
            public Building_Bed bed, bed2, bedOut;
            public PrisonZoneDef wingZone;

            private TrustyScene(Map map, Rollback r, string check)
            {
                this.map = map;
                this.r = r;
                this.check = check;
                pm = PrisonMap.Of(map);
            }

            public int Now => Find.TickManager.TicksGame;

            /// <summary>Local coordinates (x east, z north from the strip's south-west corner: rows below 0 are open ground too) in the map's.</summary>
            public IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);

            /// <summary>The corridor behind the gate, a cell of the strip in front of it, a cell of the strip's far corner, one beside it that is off the strip (still within a few cells of it), one well away, and the first cell's bed.</summary>
            public IntVec3 Corridor => At(10, 6);
            public IntVec3 InZone => At(10, 3);
            public IntVec3 OffZoneNear => At(10, 0);
            public IntVec3 FarOut => At(10, -6);
            public IntVec3 InCell => At(8, 9);
            public IntVec3 InCell2 => At(12, 9);

            /// <summary>What the ground of the scene must be like for a check that needs it: none of the prison's own guards, guard posts or manned towers can be near (its guards and posts are within the supervision's reach of the strip's cells, or the
            /// scene itself), and/or nothing that watches (a guard, a tower, a camera) sees a cell of the strip. Asked of the open ground the scene is built on: a check that cannot have it is failed, not skipped.</summary>
            [Flags]
            public enum Needs
            {
                None = 0,
                /// <summary>No guard of the prison, and no guard post, within the supervision's reach of the scene (the party's guards near the zone are the check's own).</summary>
                NoGuardsNear = 1,
                /// <summary>Nothing that watches sees the strip (a hidden escape is the check).</summary>
                Unseen = 2,
            }

            /// <summary>How far from the scene a guard or a post of the prison must be for <see cref="Needs.NoGuardsNear"/>: the supervision's reach, and a guard's walk of a few seconds besides.</summary>
            private const int GuardsAwayCells = (int)Trusties.SupervisionRadius + 8;

            public static TrustyScene Build(Map map, Rollback r, string check, Needs needs = Needs.None, int southPad = SouthPad)
            {
                var s = new TrustyScene(map, r, check);
                // (The storyteller's life beats keep coming through the thousands of real ticks of a walk: the day's beats are used up for the check, as the supermax scenes do.)
                StorySystem story = PrisonGame.Instance.story;
                int beats0 = story.beatsToday, day0 = GenLocalDate.DayOfYear(map);
                story.beatsToday = Math.Max(beats0, StoryPacing.MaxBeatsPerDay(map.mapPawns.PrisonersOfColonySpawnedCount));
                suppressedBeatsScenes++;
                r.Add(() =>
                {
                    suppressedBeatsScenes--;
                    if (GenLocalDate.DayOfYear(map) == day0) story.beatsToday = beats0;
                });
                if (needs != Needs.None) Traverse.Create(s.pm).Method("RefreshGuards").GetValue();
                IntVec3 corner = OpenCorner(map, Width, Height + southPad, needs, southPad);
                if (!corner.IsValid)
                {
                    TrustyFail(check, $"no open ground outside the compound for the scene{(needs == Needs.None ? "" : " " + needs + " (the prison's own guards, posts, towers and cameras are everywhere the ground is open)")} (footprint {PrisonBuilder.Footprint(map)}, map {map.Size})");
                    return null;
                }
                s.origin = new IntVec3(corner.x, 0, corner.z + southPad);
                foreach (IntVec3 c in new CellRect(corner.x, corner.z, Width, Height + southPad).ExpandedBy(1))
                    foreach (Thing plant in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Plant).ToList())
                        plant.Destroy(DestroyMode.Vanish);
                s.Isolate();
                s.Construct();
                return s;
            }

            /// <summary>The party and the grounds the check's own (put back), the walk-off pinned to nothing until a check says otherwise.</summary>
            private void Isolate()
            {
                TrustyParty party0 = pm.party;
                pm.party = new TrustyParty();
                PrisonGrounds grounds = pm.grounds;
                bool defined0 = grounds.defined;
                grounds.defined = false;
                int exempt0 = Trusties.exemptHits, rolls0 = Trusties.walkOffRolls;
                Trusties.WalkOffOverride = 0f;
                // (The party's own checks say when a raid is on: a live one of the events plan must not call it in under them.)
                Trusties.ThreatForTests = false;
                // (Nor an escape of the events plan's own that somebody has seen: the party's check of an escape adds its own runner to the scope.)
                HashSet<Pawn> scope0 = Trusties.EscapeScope;
                Trusties.EscapeScope = new HashSet<Pawn>();
                r.Add(() =>
                {
                    pm.party = party0;
                    grounds.defined = defined0;
                    Trusties.WalkOffOverride = null;
                    Trusties.ThreatForTests = null;
                    Trusties.EscapeScope = scope0;
                    (Trusties.exemptHits, Trusties.walkOffRolls) = (exempt0, rolls0);
                });
            }

            private static IntVec3 OpenCorner(Map map, int w, int h, Needs needs, int southPad)
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
                    if (open && Suits(map, pm, rect, needs, southPad)) return c;
                }
                return IntVec3.Invalid;
            }

            /// <summary>The ground has what <paramref name="needs"/> asks: no guard or post of the prison within <see cref="GuardsAwayCells"/> of the scene's rectangle, and the strip's cells (every few) unseen by anything that watches.</summary>
            private static bool Suits(Map map, PrisonMap pm, CellRect rect, Needs needs, int southPad)
            {
                if (needs == Needs.None) return true;
                if (needs.HasFlag(Needs.NoGuardsNear))
                {
                    CellRect reach = rect.ExpandedBy(GuardsAwayCells);
                    List<Pawn> guards = pm.Guards;
                    for (int i = 0; i < guards.Count; i++)
                        if (guards[i].Spawned && reach.Contains(guards[i].Position)) return false;
                    List<Thing> posts = map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost);
                    for (int i = 0; i < posts.Count; i++)
                        if (reach.Contains(posts[i].Position)) return false;
                }
                if (needs.HasFlag(Needs.Unseen))
                    for (int x = rect.minX; x <= rect.maxX; x += 3)
                        for (int z = rect.minZ + southPad; z <= rect.minZ + southPad + 4; z++)
                            if (TensionUtility.SeenAt(new IntVec3(x, 0, z), map, pm)) return false;
                return true;
            }

            public T Spawn<T>(ThingDef def, ThingDef stuff, IntVec3 cell, Rot4 rot = default) where T : Thing
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

            public void Paint(CellRect rect, PrisonZoneDef zone)
            {
                foreach (IntVec3 c in rect)
                {
                    PrisonZoneDef was = pm.zones.ZoneAt(c);
                    r.Add(() => pm.zones.Set(c, was));
                    pm.zones.Set(c, zone);
                }
            }

            /// <summary>One cell painted (put back by the rollback).</summary>
            public void PaintCell(IntVec3 c, PrisonZoneDef zone) => Paint(new CellRect(c.x, c.z, 1, 1), zone);

            private void Walls(int x0, int z0, int x1, int z1, params (int x, int z)[] gaps)
            {
                for (int x = x0; x <= x1; x++)
                    foreach (int z in new[] { z0, z1 })
                        if (!gaps.Contains((x, z))) Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(x, z));
                for (int z = z0 + 1; z < z1; z++)
                    foreach (int x in new[] { x0, x1 })
                        if (!gaps.Contains((x, z))) Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(x, z));
            }

            private Building_Door Door(int x, int z)
            {
                var door = Spawn<Building_Door>(ThingDefOf.Door, ThingDefOf.Steel, At(x, z));
                door.GetComp<CompPrisonDoor>().mode = PrisonDoorMode.Auto;
                return door;
            }

            private Building_Bed Bed(int x, int z)
            {
                var b = Spawn<Building_Bed>(DefDatabase<ThingDef>.GetNamed("Bed"), ThingDefOf.WoodLog, At(x, z), Rot4.North);
                b.ForPrisoners = true;
                b.GetRoom()?.Notify_RoomShapeChanged();
                // (Not a free bed of the live prison: a shuttle that lands during the check's real ticks sends its arrivals to the minimum wing's free beds, and one of them was walked out to the strip and left there when the scene was
                // taken down. The scene's pawns are given its beds by the checks.)
                (IntakeSystem.HeldForTests ??= new HashSet<Building_Bed>()).Add(b);
                r.Add(() =>
                {
                    IntakeSystem.HeldForTests?.Remove(b);
                    foreach (Pawn owner in b.OwnersForReading.ToList()) owner.ownership?.UnclaimBed();
                });
                return b;
            }

            private void Construct()
            {
                wingZone = DefDatabase<PrisonZoneDef>.AllDefsListForReading.First(z => z.security == RPTrustyDefOf.RP_Minimum);
                // The wing: x 6..14, z 5..12. The gate in the south wall at (10, 5), a wall across at z 8 (the cells' doors at (8, 8) and (12, 8)), the cells' partition at x 10 above it.
                Walls(6, 5, 14, 12, (10, 5));
                for (int x = 7; x <= 13; x++)
                    if (x != 8 && x != 12) Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(x, 8));
                for (int z = 9; z <= 11; z++) Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(10, z));
                // A staff room (x 0..4, z 5..9) with its door out to the strip at (2, 5), and a cell (x 16..20, z 5..9) with its own door out at (18, 5): neither is a gate.
                Walls(0, 5, 4, 9, (2, 5));
                Walls(16, 5, 20, 9, (18, 5));
                gate = Door(10, 5);
                bedroomDoor = Door(8, 8);
                bedroomDoor2 = Door(12, 8);
                staffDoor = Door(2, 5);
                cellDoorOut = Door(18, 5);
                bed = Bed(8, 10);
                bed2 = Bed(12, 10);
                bedOut = Bed(18, 7);
                // The zones: the minimum wing over the wing and the outside cell, the staff-only room, and the outside work on the strip (z 1..4, the whole width: z 0 is bare ground off it).
                Paint(new CellRect(origin.x + 7, origin.z + 6, 7, 6), wingZone);
                Paint(new CellRect(origin.x + 17, origin.z + 6, 3, 3), wingZone);
                Paint(new CellRect(origin.x + 1, origin.z + 6, 3, 3), RPDefOf.RP_StaffOnly);
                Paint(new CellRect(origin.x, origin.z + 1, Width, 4), RPTrustyDefOf.RP_OutsideWork);
                // The roof last (so it is the first to go when the check is rolled back).
                foreach (CellRect roof in new[] { new CellRect(origin.x + 6, origin.z + 5, 9, 8), new CellRect(origin.x, origin.z + 5, 5, 5), new CellRect(origin.x + 16, origin.z + 5, 5, 5) })
                    foreach (IntVec3 c in roof)
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

            /// <summary>
            /// Puts a pawn of the scene at a cell. A pawn that is not on the map to be put (a throwaway that failed to spawn, one the live prison's own business took away, a dead one) fails the check with a line saying what became of it
            /// (the scene is not built on it: the caller stops) instead of throwing; false then.
            /// </summary>
            public bool Place(Pawn p, IntVec3 cell)
            {
                string gone = TrustyGone(p, map);
                if (gone != null)
                {
                    Fail($"the check's own fixture: a pawn to be put at {cell - origin} {gone}");
                    return false;
                }
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
                // (A guard put somewhere else is posted there now.)
                if (trustyPosts.ContainsKey(p)) trustyPosts[p] = cell;
                return true;
            }

            /// <summary>An inmate of a level, calm and clean, at a place, with a bed if one is given. Held against the wardens' take-to-bed work and routine frisk for as long as the scene stands (stability 28's <see cref="HoldInmate"/>: a real guard
            /// carried the garden check's gardener to a bed through the real ticks of the work check, trusty-t9b, and the haul's trusty was taken from the haul in its first tick, t9a).</summary>
            public Pawn Inmate(string name, SecurityLevelDef sec, IntVec3 at, Building_Bed claim = null)
            {
                Pawn p = Throwaway(map, r, sec);
                HoldInmate(p, r);
                p.Name = new NameSingle(name);
                CompInmate c = p.Inmate();
                c.security = sec;
                (c.processed, c.awaitingSearch, c.forcedActivity, c.pendingPunishmentHours, c.want, c.gang, c.gangKnown, c.gangLeader) = (true, false, null, 0, null, null, false, false);
                (c.tension, c.fear, c.cash, c.familyCount, c.visitor, c.snitchExposed, c.informantQuit, c.informantPaid) = (15f, 0f, 0, 0, null, false, false, false);
                (c.leg, c.escortRetryTick, c.yardSlot, c.lastReclassTick, c.entitlement) = (null, 0, -1, Now - 3 * GenDate.TicksPerDay, 5);
                c.records.Clear();
                c.reputations.Clear();
                c.revealed.Clear();
                c.relatives.Clear();
                c.staffMemories.Clear();
                c.job = null;
                c.jobLocked = false;
                ClearAddictions(p);
                p.health.Reset();
                c.RefreshActiveFlag();
                Place(p, at);
                if (claim != null) p.ownership.ClaimBedIfNonMedical(claim);
                if (p.needs.TryGetNeed(RPDefOf.RP_Bladder) is { } bladder) bladder.CurLevel = 1f;
                if (p.needs.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs.food != null) p.needs.food.CurLevel = 1f;
                if (p.needs.TryGetNeed(RPDefOf.RP_Hygiene) is { } hygiene) hygiene.CurLevel = 0.9f;
                r.Add(() =>
                {
                    c.trustySinceTick = -1;
                    c.outsidePass = false;
                    c.workPass = null;
                    c.leg = null;
                    c.job = null;
                });
                pm.ForceDoorUpdate();
                return p;
            }

            /// <summary>A trusty at minimum security: grade 5, the status made a day ago, the outside job if asked.</summary>
            public Pawn Trusty(string name, IntVec3 at, bool withJob = true, Building_Bed claim = null)
            {
                Pawn p = Inmate(name, RPTrustyDefOf.RP_Minimum, at, claim);
                CompInmate c = p.Inmate();
                c.trustySinceTick = Math.Max(0, Now - GenDate.TicksPerDay);
                if (withJob)
                {
                    c.job = RPTrustyDefOf.RP_JobOutside;
                    c.jobLocked = true;
                }
                return p;
            }

            /// <summary>A guard on site, awake and on shift at every hour.</summary>
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
                // (No work of their own: a guard with nothing to do would walk off to a patrol, and the checks that want them to walk start it by hand.)
                if (s.workSettings != null)
                    foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefsListForReading) s.workSettings.SetPriority(work, 0);
                Place(s, at);
                // (Standing where they are put: an idle guard would wander home to the compound, and the party's guards near the zone would be none.)
                s.jobs.StartJob(new Job(JobDefOf.Wait) { expiryInterval = 60000 }, JobCondition.InterruptForced);
                trustyPosts[s] = at;
                r.Add(() => trustyPosts.Remove(s));
                return s;
            }

            /// <summary>The map's guards refreshed and the party's beat run now (the beat the map component runs every 250 ticks).</summary>
            public void Beat()
            {
                Traverse.Create(pm).Method("RefreshGuards").GetValue();
                Trusties.Beat(map, pm);
            }

            /// <summary>The regime of the minimum level one activity all day (put back).</summary>
            public void RegimeAllDay(RegimeActivityDef activity) => SupermaxRegimeAllDay(g, r, RPTrustyDefOf.RP_Minimum, activity);

            /// <summary>A tree on the strip (a wild one, grown), put away by the rollback.</summary>
            public Plant Tree(IntVec3 cell)
            {
                var tree = (Plant)ThingMaker.MakeThing(ThingDefOf.Plant_TreeOak);
                tree.Growth = 1f;
                GenSpawn.Spawn(tree, cell, map);
                r.Add(() =>
                {
                    if (tree.Spawned) tree.Destroy(DestroyMode.Vanish);
                });
                return tree;
            }

            /// <summary>A second gate in the wing's south wall at local x: the wall piece there taken out and an Auto door put in its place (the door passes redone). Put away with the scene.</summary>
            public Building_Door SecondGate(int x)
            {
                foreach (Thing wall in At(x, 5).GetThingList(map).Where(t => t.def == ThingDefOf.Wall).ToList()) wall.Destroy(DestroyMode.Vanish);
                Building_Door door = Door(x, 5);
                map.reachability.ClearCache();
                pm.ForceDoorUpdate();
                return door;
            }

            /// <summary>The south pad a scene needs for <see cref="Courtyard"/>: the courtyard's rows (z -18..-1) are open ground the scene is built on (build the scene with it).</summary>
            public const int CourtyardPad = 20;

            /// <summary>
            /// A courtyard of its own district for <see cref="SecondGate"/> at 13: walls along z 0 (the strip's south edge) and round a yard of x 1..19, z -17..-1, with the way into it the three cells of the strip in front of the door (x 12..14,
            /// z 0..4, walled at x 11 and x 15). The strip itself stays open ground that touches the map's edge, and the courtyard does not: the game takes ground that touches no edge as outdoors only when its room has 300 open-roof cells
            /// (<c>Room.PsychologicallyOutdoors</c>, which is how the zone's side of a gate is told from a staff room), so the yard is big enough for that (19 x 17 and the way in, 338 cells). Needs a scene built with <see cref="CourtyardPad"/>.
            /// </summary>
            public void Courtyard()
            {
                Walls(0, -18, Width - 1, 0, (12, 0), (13, 0), (14, 0));
                for (int z = 1; z <= 4; z++)
                    foreach (int x in new[] { 11, 15 })
                        Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(x, z));
                map.reachability.ClearCache();
                pm.ForceDoorUpdate();
            }

            /// <summary>What the game made of the courtyard's door, for a check's premise line: the room beyond it (its open-roof cells, whether it counts as outdoors, whether it touches the map's edge) and the door's sides.</summary>
            public string CourtyardLine(Building_Door door)
            {
                IntVec3 beyond = door.Position + IntVec3.South;
                Room room = beyond.GetRoom(map);
                CompPrisonDoor comp = door.GetComp<CompPrisonDoor>();
                return $"beyond the door {beyond - origin}: room cells {room?.CellCount.ToString() ?? "none"}, open roof {room?.OpenRoofCount.ToString() ?? "none"}, outdoors {room?.PsychologicallyOutdoors}, touches the edge {room?.TouchesMapEdge}, zone {pm.zones.ZoneAt(beyond)?.defName ?? "none"}; "
                       + $"the door's sides {comp.sideA}/{comp.sideB}, gate {comp.trustyGate}, work zone {comp.workZone?.defName ?? "none"}";
            }

            /// <summary>A standable cell of the compound as far from the strip as the compound allows (invalid when even that is within the supervision's reach of it): where a guard is put to be nowhere near the zone.</summary>
            public IntVec3 FarCell()
            {
                IntVec3 best = IntVec3.Invalid;
                float bestDist = Trusties.SupervisionRadius + 4f;
                foreach (IntVec3 c in PrisonBuilder.Footprint(map))
                {
                    float d = c.DistanceTo(InZone);
                    if (d > bestDist && c.Standable(map) && c.GetEdifice(map) == null)
                    {
                        best = c;
                        bestDist = d;
                    }
                }
                return best;
            }

            public void Fail(string what) => TrustyFail(check, what);
        }

        /// <summary>The scenes' guards and where each stands (see <see cref="TrustyHoldGuards"/>).</summary>
        private static readonly Dictionary<Pawn, IntVec3> trustyPosts = new Dictionary<Pawn, IntVec3>();

        /// <summary>
        /// A guard of a scene is a prop of the check: the real ticks of a walk run the prison round it, and a free guard of the prison is taken for its duties (the shuttle's escort of an arrival, a post, a patrol), so the one the
        /// check stood beside the strip walks off and the party that was out is short of guards and called in an hour later. Each is put back where the check stands them, with a wait, unless the check has them on a job of its
        /// own (an escort walk, a post).
        /// </summary>
        private static void TrustyHoldGuards()
        {
            foreach (KeyValuePair<Pawn, IntVec3> post in trustyPosts)
            {
                Pawn g = post.Key;
                if (g.Destroyed || !g.Spawned || g.Downed || g.CurJobDef == RPSupermaxDefOf.RP_EscortInmate || g.CurJobDef == RPPostDefOf.RP_ManPost) continue;
                if (g.CurJobDef == JobDefOf.Wait && g.Position == post.Value) continue;
                g.pather.StopDead();
                g.Position = post.Value;
                g.Notify_Teleported();
                g.jobs.StartJob(new Job(JobDefOf.Wait) { expiryInterval = 60000 }, JobCondition.InterruptForced);
            }
        }

        /// <summary>Real ticks, the scenes' guards kept on shift and where the check put them.</summary>
        private static int TrustyTicks(Func<bool> done, int most)
        {
            int ticked = 0;
            while (ticked < most && !done())
            {
                Find.TickManager.DoSingleTick();
                KeepSupermaxGuardsOnShift();
                KeepDrugTestDoctorsOnShift();
                ticked++;
                if (ticked % 30 == 0) TrustyHoldGuards();
            }
            return ticked;
        }

        /// <summary>What became of a scene's pawn that is no longer there to be put or asked anything (null when it is on the map and whole): the words for a check's fixture line.</summary>
        private static string TrustyGone(Pawn p, Map map)
        {
            if (p == null) return "is null (the throwaway was not made)";
            string who = p.LabelShort;
            if (p.Destroyed) return $"({who}) was destroyed (dead {p.Dead}, discarded {p.Discarded})";
            if (!p.Spawned) return $"({who}) is not on the map (held by {p.ParentHolder?.ToString() ?? "nothing"}, map {p.Map?.ToString() ?? "none"}, dead {p.Dead}, job {p.CurJobDef?.defName ?? "none"})";
            if (p.Map != map) return $"({who}) is on another map ({p.Map})";
            if (p.pather == null || p.jobs == null) return $"({who}) has no path follower or no jobs (dead {p.Dead})";
            return null;
        }

        /// <summary>Everything that runs about the wing stopped (the next check starts from still pawns).</summary>
        private static void TrustyStill(params Pawn[] pawns)
        {
            foreach (Pawn p in pawns)
            {
                if (p == null || p.Destroyed || !p.Spawned) continue;
                p.MentalState?.RecoverFromState();
                p.jobs?.StopAll();
                p.pather?.StopDead();
            }
        }

        // ---- the entry ---------------------------------------------------------------------------------------------------------------------------------------------------

        private static bool trustiesRan;

        /// <summary>
        /// The events plan's trusty checks, once, from the plan's own loop after the burst and after the stove fire left to the staff is over (out, or not lit, or its hours are up): the checks' real ticks take some hours of the game's clock
        /// (the walks, the party's holds), and the fire is lit at the first hourly sample after the burst in the day shift's hours - run inside the burst they took the clock past the fire's hours (the events run of trusty-t9b: "the first hourly
        /// sample after the kitchen's tests was at hour 19"). Run here, the burst's clock is the one main's is, every other check keeps its time of day, and the fire's window (hours 7-17) stays as it is. The checks are self-contained (their
        /// own scenes, put back), so the later start changes nothing they need; the end of the run fails when they never ran.
        /// </summary>
        private static void CheckTrustiesOnce(Map map)
        {
            if (trustiesRan || staffFirePending) return;
            bool fireDone = staffFireTick < 0 || staffFireOutTick >= 0 || Find.TickManager.TicksGame - staffFireTick > (StaffFireHours + 1) * GenDate.TicksPerHour;
            if (!fireDone) return;
            trustiesRan = true;
            CheckTrusties(map);
        }

        private static void CheckTrusties(Map map)
        {
            // (The game time the checks' real ticks take is told: the events run's own timed tests - the stove fire left to the staff - wait on the clock.)
            int ticks0 = Find.TickManager.TicksGame;
            CheckTrustyGrant(map);
            CheckTrustyGate(map);
            CheckTrustyPass(map);
            CheckTrustyGoesOut(map);
            CheckTrustyWayBack(map);
            CheckTrustyExemption(map);
            CheckTrustyWalkOff(map);
            CheckTrustyRevoke(map);
            CheckTrustyJobRules(map);
            CheckTrustyPlacement(map);
            CheckTrustyWork(map);
            CheckTrustyPay(map);
            CheckTrustyStay(map);
            CheckTrustyParty(map);
            CheckTrustyStranded(map);
            CheckTrustyZoneRules(map);
            CheckTrustiesRound4(map);
            CheckTrustiesRound5(map);
            CheckTrustiesRound6(map);
            CheckTrustiesRound7(map);
            CheckTrustiesRound8(map);
            CheckTrustiesRound11(map);
            RPLog.Message($"[trusty] the checks took {Find.TickManager.TicksGame - ticks0} ticks ({(Find.TickManager.TicksGame - ticks0) / (float)GenDate.TicksPerHour:F1} hours of the game's clock)");
        }

        // ---- grant -------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Medium, a grade of 3, a tension of 50, a serious misconduct seen lately, the watch list and a confinement each refuse with a reason of their own; a calm minimum inmate at grade 4 is made one. (Without CanGrant none of it exists.)</summary>
        private static void CheckTrustyGrant(Map map)
        {
            Guarded("trusties: who can be made one", map, r =>
            {
                const string check = "grant";
                Pawn p = Throwaway(map, r, RPTrustyDefOf.RP_Minimum);
                CompInmate c = p.Inmate();
                (c.processed, c.awaitingSearch, c.forcedActivity) = (true, false, null);
                c.records.Clear();
                c.entitlement = Trusties.MinGrade;
                c.tension = 10f;
                c.riskTick = -1;
                r.Add(() => (c.trustySinceTick, c.job, c.jobLocked, c.riskTick, c.forcedActivity) = (-1, null, false, -1, null));
                int now = Find.TickManager.TicksGame;
                var reasons = new List<string>();
                void Refused(string what)
                {
                    if (Trusties.CanGrant(p, out string why) || string.IsNullOrEmpty(why)) TrustyFail(check, $"{what}: the warden can make them a trusty (reason '{why}')");
                    else reasons.Add(why);
                }
                if (!Trusties.CanGrant(p, out string whyNot)) { TrustyFail(check, $"a calm minimum inmate at grade 4 cannot be made a trusty: {whyNot}"); return; }
                SecurityLevelDef level = c.security;
                c.security = RPSecurityDefOf.RP_Medium;
                Refused("a medium-security inmate");
                c.security = level;
                c.entitlement = Trusties.MinGrade - 1;
                Refused("grade 3");
                c.entitlement = Trusties.MinGrade;
                c.tension = 50f;
                Refused("a tension of 50");
                c.tension = 10f;
                c.records.Add(new MisconductRecord { def = RPMisconductDefOf.RP_Fight, tick = now - 5 * GenDate.TicksPerDay, seen = true });
                Refused("a seen fight five days ago");
                c.records.Clear();
                // (One nobody saw is not held against them, and one from before the ten days is not either: the control.)
                c.records.Add(new MisconductRecord { def = RPMisconductDefOf.RP_Fight, tick = now - 5 * GenDate.TicksPerDay, seen = false });
                c.records.Add(new MisconductRecord { def = RPMisconductDefOf.RP_Fight, tick = now - (Trusties.SevereDays + 2) * GenDate.TicksPerDay, seen = true });
                if (!Trusties.CanGrant(p, out string whyOld)) TrustyFail(check, $"an unseen fight, and a seen one twelve days ago, are held against them: {whyOld}");
                c.records.Clear();
                c.riskTick = Math.Max(1, now - 1000);
                Refused("the watch list");
                c.riskTick = -1;
                c.forcedActivity = RPDefOf.RP_Lockup;
                Refused("a confinement");
                c.forcedActivity = null;
                if (reasons.Distinct().Count() != reasons.Count) TrustyFail(check, "two of the refusals gave the same reason: " + string.Join(" | ", reasons));
                int entries0 = r.NewEntries.Count();
                if (!Trusties.Grant(p) || !c.IsTrusty) { TrustyFail(check, "the warden's grant did not make them a trusty"); return; }
                if (c.job != null) TrustyFail(check, "making a trusty gave the job at once: it is the daily pass's, or the warden's menu's");
                if (r.NewEntries.Count() <= entries0) TrustyFail(check, "the chronicle has no line for a trusty made");
                if (Trusties.CanGrant(p, out _)) TrustyFail(check, "a trusty can be made a trusty again");
                // The warden's own button: not silent.
                int entries1 = r.NewEntries.Count();
                Trusties.Revoke(p, "check", silent: false);
                if (c.IsTrusty || r.NewEntries.Count() <= entries1) TrustyFail(check, $"the warden's end of the status left {(c.IsTrusty ? "the status" : "no chronicle line")}");
            });
        }

        // ---- the gate ----------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The gate opens to a trusty on the job in the work hours while the party is out, and to nobody else. Controls (true before and after): a non-trusty at minimum, a trusty in a mental state, a staff room's door and a cell's door to
        /// the outside (the plan's first gate was any Auto door with an outside side) are refused; so is a gate whose inmate side is holding, a medium wing or the visiting room, and one that is not Auto. A zone repainted gives the gate
        /// back its answer at once (the warmed reachability cache is cleared). (Without the gate branch of the door pass no door opens to anyone.)
        /// </summary>
        private static void CheckTrustyGate(Map map)
        {
            Guarded("trusties: the gate", map, r =>
            {
                const string check = "gate";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn t = s.Trusty("Ztg1", s.Corridor, claim: s.bed);
                Pawn plain = s.Inmate("Ztg2", RPTrustyDefOf.RP_Minimum, s.At(11, 6));
                Pawn mental = s.Trusty("Ztg3", s.At(9, 6), claim: s.bed2);
                CompPrisonDoor gateComp = s.gate.GetComp<CompPrisonDoor>();
                if (gateComp.workZone != RPTrustyDefOf.RP_OutsideWork)
                {
                    s.Fail($"the gate's work zone is {gateComp.workZone?.defName ?? "none"} (sides {gateComp.sideA}/{gateComp.sideB}, mode {gateComp.mode}): an Auto door between minimum space and the outside work strip names the strip");
                    return;
                }
                if (s.staffDoor.GetComp<CompPrisonDoor>().workZone != null || s.cellDoorOut.GetComp<CompPrisonDoor>().workZone != null)
                    s.Fail($"a staff room's door ({s.staffDoor.GetComp<CompPrisonDoor>().workZone?.defName ?? "none"}) or a cell's door ({s.cellDoorOut.GetComp<CompPrisonDoor>().workZone?.defName ?? "none"}) to the strip is a gate");
                s.pm.party.isOut = true;
                s.pm.party.state = PartyState.Out;
                foreach (Pawn x in new[] { t, plain, mental }) x.Inmate().UpdateWorkPass();
                if (!s.gate.PawnCanOpen(t)) s.Fail("the gate is shut to a trusty on the job, at work, with the party out: " + TrustyLine(s, t));
                if (s.gate.PawnCanOpen(plain)) s.Fail("the gate is open to a minimum inmate who is no trusty");
                plain.Inmate().job = RPTrustyDefOf.RP_JobOutside;
                plain.Inmate().UpdateWorkPass();
                if (s.gate.PawnCanOpen(plain) || plain.Inmate().workPass != null) s.Fail($"a minimum inmate with the outside job and no status was given the pass ({plain.Inmate().workPass?.defName ?? "none"})");
                plain.Inmate().job = null;
                if (s.staffDoor.PawnCanOpen(t) || s.cellDoorOut.PawnCanOpen(t)) s.Fail($"a staff room's door (open {s.staffDoor.PawnCanOpen(t)}) or a cell's door (open {s.cellDoorOut.PawnCanOpen(t)}) to the outside is open to a trusty at work");
                // A trusty in a mental state: refused.
                if (!mental.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Vandalizing, null, forced: true, otherPawn: null)) s.Fail("the check's own premise: a mental state could not be started");
                else
                {
                    mental.Inmate().UpdateWorkPass();
                    if (s.gate.PawnCanOpen(mental) || mental.Inmate().workPass != null) s.Fail("the gate is open to a trusty in a mental state");
                    mental.MentalState?.RecoverFromState();
                }
                // Not in the work hours, or not on the job, or with the party in, or confined: no pass.
                s.RegimeAllDay(RPDefOf.RP_FreeTime);
                t.Inmate().UpdateWorkPass();
                if (t.Inmate().workPass != null || s.gate.PawnCanOpen(t)) s.Fail("a trusty has the pass outside the work hours");
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                s.pm.party.isOut = false;
                t.Inmate().UpdateWorkPass();
                if (t.Inmate().workPass != null || s.gate.PawnCanOpen(t)) s.Fail("a trusty has the pass while the party is in");
                s.pm.party.isOut = true;
                t.Inmate().job = null;
                t.Inmate().UpdateWorkPass();
                if (t.Inmate().workPass != null) s.Fail("a trusty with no outside job has the pass");
                t.Inmate().job = RPTrustyDefOf.RP_JobOutside;
                t.Inmate().UpdateWorkPass();
                if (!s.gate.PawnCanOpen(t)) s.Fail("the gate did not open again once the job came back");
                // The gate's own side: holding, a medium wing and the visiting room make it no gate; nor does a mode but Auto.
                foreach (PrisonZoneDef bad in new[] { RPDefOf.RP_Holding, RPSecurityDefOf.RP_Medium != null ? DefDatabase<PrisonZoneDef>.AllDefsListForReading.First(z => z.security == RPSecurityDefOf.RP_Medium) : null, RPVisitDefOf.RP_Visitation })
                {
                    if (bad == null) continue;
                    PrisonZoneDef was = s.pm.zones.ZoneAt(s.Corridor);
                    s.pm.zones.Set(s.Corridor, bad);
                    s.pm.ForceDoorUpdate();
                    if (gateComp.workZone != null || s.gate.PawnCanOpen(t)) s.Fail($"a gate out of {bad.defName} is a gate for the work party (work zone {gateComp.workZone?.defName ?? "none"})");
                    s.pm.zones.Set(s.Corridor, was);
                    s.pm.ForceDoorUpdate();
                }
                foreach (PrisonDoorMode mode in new[] { PrisonDoorMode.Locked, PrisonDoorMode.StaffOnly })
                {
                    gateComp.mode = mode;
                    s.pm.ForceDoorUpdate();
                    t.Inmate().UpdateWorkPass();
                    if (s.gate.PawnCanOpen(t)) s.Fail($"the gate is open to a trusty with the door set to {mode}");
                }
                gateComp.mode = PrisonDoorMode.Auto;
                s.pm.ForceDoorUpdate();
                if (gateComp.workZone != RPTrustyDefOf.RP_OutsideWork || !s.gate.PawnCanOpen(t)) s.Fail("the gate did not come back with the mode set to Auto again");
                // The zone erased: the answer is at once, the warmed cache and all.
                map.reachability.ClearCache();
                bool warm = t.CanReach(s.InZone, PathEndMode.OnCell, Danger.Deadly);
                var strip = new CellRect(s.origin.x, s.origin.z + 1, TrustyScene.Width, 4);
                foreach (IntVec3 c in strip) s.PaintCell(c, null);
                s.pm.Notify_DoorsChanged();
                s.pm.ForceDoorUpdate();
                if (!warm) s.Fail("the check's own premise: a trusty with the pass could not reach the strip");
                else
                {
                    // (Each answer alone: the gate's verdict is false the moment the zone is erased, so only the reachability asked apart can see a stale cache.)
                    if (s.gate.PawnCanOpen(t)) s.Fail("with the zone erased the gate still opens to the trusty");
                    if (t.CanReach(s.InZone, PathEndMode.OnCell, Danger.Deadly)) s.Fail("with the zone erased the strip is still reachable for the trusty: a warmed reachability cache answered");
                }
                TrustyStill(t, plain, mental);
            });
        }

        /// <summary>
        /// The pass: given by the work rule only; kept by one who holds it while they stand out in the open on owned ground (a revoked trusty, a party called in, the cell beside the strip), and lost at the first look back inside; never
        /// given to a non-trusty by where they stand; ended at once by an escape's start. (With the old rule that kept the pass in the zone only, a trusty one cell off it has none.)
        /// </summary>
        private static void CheckTrustyPass(Map map)
        {
            Guarded("trusties: the way back is held, the pass is lost inside and at an escape", map, r =>
            {
                const string check = "pass";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                s.pm.party.isOut = true;
                s.pm.party.state = PartyState.Out;
                Pawn never = s.Inmate("Ztp1", RPTrustyDefOf.RP_Minimum, s.InZone);
                never.Inmate().UpdateWorkPass();
                if (never.Inmate().workPass != null || never.Inmate().outsidePass) s.Fail("a minimum inmate who is no trusty, standing in the zone, was given the pass");
                Pawn t = s.Trusty("Ztp2", s.Corridor, claim: s.bed);
                CompInmate c = t.Inmate();
                c.UpdateWorkPass();
                if (!c.outsidePass || c.workPass != RPTrustyDefOf.RP_OutsideWork) { s.Fail("the work rule did not give the pass: " + TrustyLine(s, t)); return; }
                // On the strip, off the job (a revoked returner): the pass is kept; one cell off the strip beside it too.
                s.Place(t, s.InZone);
                Trusties.Revoke(t, null, silent: true);
                s.pm.party.isOut = false;
                c.UpdateWorkPass();
                if (!c.outsidePass || s.pm.IsOutsideSpace(t.Position) == false) s.Fail($"a revoked trusty in the zone lost the pass before they were back inside (pass {c.outsidePass}): {TrustyLine(s, t)}");
                s.Place(t, s.OffZoneNear);
                c.UpdateWorkPass();
                if (!c.outsidePass) s.Fail("a trusty one cell off the strip lost the pass (the pass was kept in the zone only)");
                if (!t.CanReach(s.Corridor, PathEndMode.OnCell, Danger.Deadly) || !s.gate.PawnCanOpen(t)) s.Fail("the way back from a cell off the zone is shut");
                // Owned ground: off it they keep nothing.
                PrisonGrounds grounds = s.pm.grounds;
                (bool defined, int minX, int maxX, int minZ, int maxZ) = (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ);
                r.Add(() => (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (defined, minX, maxX, minZ, maxZ));
                (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (true, 0, 1, 0, 1);
                c.UpdateWorkPass();
                if (c.outsidePass) s.Fail("a trusty off the prison's land kept the pass");
                (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (defined, minX, maxX, minZ, maxZ);
                c.outsidePass = true;
                c.workPass = RPTrustyDefOf.RP_OutsideWork;
                // The first look back inside: the pass goes.
                s.Place(t, s.Corridor);
                c.UpdateWorkPass();
                if (c.outsidePass || c.workPass != null || s.gate.PawnCanOpen(t)) s.Fail("the pass was kept inside the prison by a revoked trusty: " + TrustyLine(s, t));
                // A trusty with the pass in their own cell loses it too (a cell is no open air).
                c.outsidePass = true;
                s.Place(t, s.InCell);
                c.UpdateWorkPass();
                if (c.outsidePass) s.Fail("the pass was kept in a cell");
                // An escape's start: gone on the spot, before any look, and the status with it, quietly.
                Pawn runner = s.Trusty("Ztp3", s.InZone, claim: s.bed2);
                CompInmate rc = runner.Inmate();
                s.pm.party.isOut = true;
                rc.UpdateWorkPass();
                if (!s.gate.PawnCanOpen(runner)) s.Fail("the check's own premise: the runner has no pass before the escape");
                int entries = r.NewEntries.Count(), told = TrustyRevokeTold(r, runner);
                CountSystem.StartEscaping(runner, "check", EscapeWay.Walked);
                if (rc.outsidePass || rc.workPass != null || s.gate.PawnCanOpen(runner)) s.Fail($"the pass was still there at the escape's start: {TrustyLine(s, runner)}");
                if (rc.IsTrusty || rc.job != null) s.Fail("the escape's start left the status or the job");
                if (TrustyRevokeTold(r, runner) != told) s.Fail("an escape's revoke wrote a chronicle line or posted a message of its own (it is silent: the escape's own message tells it)");
                if (r.NewEntries.Count() < entries) s.Fail("the entries went backwards");
                TrustyStill(t, never, runner);
            });
        }

        // ---- real ticks: out ------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A trusty with the job in the work hours, with a guard near the zone, goes out by the gate to the strip and cuts a tree (real ticks); with no guard the party stays in and they stay in their cell. The cut is judged by its
        /// work (the driver's own count over the time a tree takes): a cut that was ended and begun again from nothing is the failure, and the message says every job the trusty ended, when, how and by what (<see cref="JobTrace"/>).
        /// </summary>
        private static void CheckTrustyGoesOut(Map map)
        {
            Guarded("trusties: the party goes out by the gate and cuts a tree", map, r =>
            {
                const string check = "goes out";
                // (Where none of the prison's own guards or posts are near: the control of no guard is then built, not hoped for.)
                TrustyScene s = TrustyScene.Build(map, r, check, TrustyScene.Needs.NoGuardsNear);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Plant tree = s.Tree(s.At(14, 3));
                Pawn t = s.Trusty("Ztgo1", s.Corridor, claim: s.bed);
                CompInmate c = t.Inmate();
                // No guard near the zone and no post: the party is short and stays in (a control).
                s.Beat();
                if (s.pm.party.near != 0) { s.Fail($"the check's own premise: {s.pm.party.near} of the prison's own guards or posts are near the scene's strip: {TrustyLine(s, t)}"); return; }
                if (s.pm.party.isOut) { s.Fail($"the party is out with no guard near the zone and no post: {TrustyLine(s, t)}"); return; }
                if (s.pm.party.state != PartyState.Short) s.Fail($"with no guard the party's state is {s.pm.party.state}, Short expected");
                Pawn guard = s.Guard("Ztgo2", s.At(10, 2));
                s.Beat();
                if (!s.pm.party.isOut || s.pm.party.state != PartyState.Out) { s.Fail($"the party did not go out with a guard standing in the zone: {TrustyLine(s, t)} (guards on duty {s.pm.Guards.Count}, on duty {GuardPosts.OnDuty(guard)})"); return; }
                // (Every job of the trusty's from here: when it began, when and how it ended, and what ended it.)
                JobTrace.Begin(t);
                try
                {
                    int ticked = TrustyTicks(() => s.pm.zones.ZoneAt(t.Position) == RPTrustyDefOf.RP_OutsideWork, 3500);
                    if (s.pm.zones.ZoneAt(t.Position) != RPTrustyDefOf.RP_OutsideWork)
                    {
                        s.Fail($"a trusty on the job did not get to the strip in {ticked} ticks: {TrustyLine(s, t)}; reach {t.CanReach(s.InZone, PathEndMode.OnCell, Danger.Deadly)}, gate pass {s.gate.PawnCanOpen(t)}, gate work zone {s.gate.GetComp<CompPrisonDoor>().workZone?.defName ?? "none"}; jobs: {JobTrace.Text()}");
                        TrustyStill(t);
                        return;
                    }
                    // They work: the job is given (it fails if no cutting job ever comes), then the cut gets the time a tree takes (the plant's work over the pawn's cut speed, as the driver counts it), and the tree comes down.
                    bool Cut() => t.CurJobDef == JobDefOf.CutPlant && t.CurJob.targetA.Thing == tree;
                    int waited = TrustyTicks(() => Cut() || tree.Destroyed || !tree.Spawned, 1500);
                    bool cutOnce = Cut();
                    if (!cutOnce && tree.Spawned && !tree.Destroyed) { s.Fail($"the trusty on the strip was never given the cut of the tree in the zone in {waited} ticks: {TrustyLine(s, t)}; jobs: {JobTrace.Text()}"); TrustyStill(t, guard); return; }
                    float perTick = Mathf.Max(0.01f, t.GetStatValue(StatDefOf.PlantWorkSpeed) * Mathf.Lerp(3.3f, 1f, tree.Growth));
                    int needed = Mathf.CeilToInt(tree.def.plant.harvestWork / perTick);
                    int window = needed + needed / 4 + 600;
                    RPLog.Message($"[RimPrison:test] trusties goes out: the cut of the tree takes {needed} ticks at {perTick:0.###} work a tick ({tree.def.plant.harvestWork} work): the window is {window}");
                    float Done() => Traverse.Create(t.jobs.curDriver).Field("workDone").GetValue<float>();
                    float progress = 0f;
                    int worked = TrustyTicks(() =>
                    {
                        cutOnce |= Cut();
                        if (Cut() && t.jobs.curDriver != null) progress = Mathf.Max(progress, Done());
                        return tree.Destroyed || !tree.Spawned;
                    }, window);
                    if (tree.Spawned && !tree.Destroyed)
                        s.Fail($"the trusty did not cut the tree in the zone in {waited + worked} ticks (the cut is {needed} ticks of work: the job ran {cutOnce}, work done {progress:0}/{tree.def.plant.harvestWork}): {TrustyLine(s, t)}; jobs: {JobTrace.Text()}");
                }
                finally
                {
                    JobTrace.End();
                }
                TrustyStill(t, guard);
            });
        }

        /// <summary>
        /// A party called in while the trusty is on the strip (a real rioter, a pawn in the riot's mental state): they walk back in through the gate and no escape begins (the plan's first rule ran them off at once: the end of the party
        /// stranded them and the breach rule fired). Then again at the end of the work slot with the party still out.
        /// </summary>
        private static void CheckTrustyWayBack(Map map)
        {
            Guarded("trusties: the way back when the party is called in and when the work ends", map, r =>
            {
                const string check = "way back";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztwb1", s.At(10, 2));
                Pawn t = s.Trusty("Ztwb2", s.InZone, claim: s.bed);
                CompInmate c = t.Inmate();
                s.Beat();
                if (!s.pm.party.isOut) { s.Fail("the check's own premise: the party is not out: " + TrustyLine(s, t)); return; }
                c.UpdateWorkPass();
                // A rioter (in the wing, far from the strip): the riot calls the party in at the next beat.
                Pawn rioter = s.Inmate("Ztwb3", RPSecurityDefOf.RP_Medium, s.At(12, 6));
                if (!rioter.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true)) { s.Fail("the check's own premise: a rioter could not be made"); return; }
                bool escaped = false;
                int ticked = TrustyTicks(() =>
                {
                    if (t.MentalStateDef == RPMentalStateDefOf.RP_Escaping) escaped = true;
                    return escaped || !s.pm.IsOutsideSpace(t.Position) && s.pm.party.state == PartyState.Riot && t.GetRoom() == InmateUtility.CellOf(t);
                }, 4000);
                rioter.MentalState?.RecoverFromState();
                if (escaped) s.Fail("the party was called in and the trusty on the strip began an escape: " + TrustyLine(s, t));
                else if (t.GetRoom() != InmateUtility.CellOf(t)) s.Fail($"a trusty on the strip was not back in their cell {ticked} ticks after a riot called the party in: {TrustyLine(s, t)}");
                else if (s.pm.party.state != PartyState.Riot) s.Fail($"the riot did not call the party in (state {s.pm.party.state})");
                // The work slot ends with the party still out (the beat is run, and the party is out at the end of it too): they walk back by the pass they hold, and no escape begins.
                s.pm.party.holdUntilTick = -1;
                s.Place(t, s.InZone);
                s.Beat();
                if (!s.pm.party.isOut) { s.Fail($"the check's own premise: the party is not out again after the riot: {TrustyLine(s, t)}"); TrustyStill(t, guard, rioter); return; }
                c.UpdateWorkPass();
                s.RegimeAllDay(RPDefOf.RP_Lockup);
                s.pm.QueueRegimeInterrupt(t);
                t.jobs.EndCurrentJob(JobCondition.InterruptForced);
                escaped = false;
                bool partyCalledIn = false;
                int back = TrustyTicks(() =>
                {
                    if (t.MentalStateDef == RPMentalStateDefOf.RP_Escaping) escaped = true;
                    if (!s.pm.party.isOut) partyCalledIn = true;
                    return escaped || t.GetRoom() == InmateUtility.CellOf(t);
                }, 4000);
                if (partyCalledIn) s.Fail($"the party was called in while the work slot's end was walked ({s.pm.party.state}): the leg is not the one it says");
                if (escaped) s.Fail("the work slot ended and the trusty on the strip began an escape");
                else if (t.GetRoom() != InmateUtility.CellOf(t)) s.Fail($"the work slot ended and the trusty was not back in their cell in {back} ticks: {TrustyLine(s, t)}");
                // One cell off the strip (revoked, so only the pass they hold is theirs): the walk back is by real ticks too.
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                s.Place(t, s.InZone);
                c.UpdateWorkPass();
                s.Place(t, s.OffZoneNear);
                Trusties.Revoke(t, null, silent: true);
                if (!c.outsidePass) { s.Fail($"the check's own premise: no pass to walk back by from one cell off the strip: {TrustyLine(s, t)}"); TrustyStill(t, guard, rioter); return; }
                s.RegimeAllDay(RPDefOf.RP_Lockup);
                s.pm.QueueRegimeInterrupt(t);
                t.jobs.EndCurrentJob(JobCondition.InterruptForced);
                escaped = false;
                int fromOff = TrustyTicks(() =>
                {
                    if (t.MentalStateDef == RPMentalStateDefOf.RP_Escaping) escaped = true;
                    return escaped || t.GetRoom() == InmateUtility.CellOf(t);
                }, 4000);
                if (escaped) s.Fail("a returner one cell off the strip began an escape on the way back");
                else if (t.GetRoom() != InmateUtility.CellOf(t)) s.Fail($"a returner one cell off the strip did not walk back in {fromOff} ticks: {TrustyLine(s, t)}");
                TrustyStill(t, guard, rioter);
            });
        }

        // ---- the escape rule --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A trusty in the zone, edge-connected, the walk-off pinned to 0, a guard in sight, across a day of hourly checks (and one real hour with the check seen to run): no escape; the breach roll never runs for them (the counter
        /// of the new branch counts every call). Controls: a minimum inmate who is no trusty in the zone, and a trusty farther out with no pass, are under the old rule and break for the edge at once.
        /// </summary>
        private static void CheckTrustyExemption(Map map)
        {
            Guarded("trusties: the breach rule does not apply to a trusty at work", map, r =>
            {
                const string check = "exemption";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztex1", s.At(11, 3));
                Pawn t = s.Trusty("Ztex2", s.InZone, claim: s.bed);
                CompInmate c = t.Inmate();
                s.pm.party.isOut = true;
                s.pm.party.state = PartyState.Out;
                c.UpdateWorkPass();
                bool edge = t.GetDistrict()?.TouchesMapEdge == true;
                RPLog.Message($"[RimPrison:test] trusties exemption: the strip's district touches the map edge: {edge}; a guard sees the trusty: {TensionUtility.GuardsNear(t, s.pm)}");
                if (!edge) { s.Fail("the check's own premise: the strip is not edge-connected"); return; }
                int hits0 = Trusties.exemptHits;
                for (int hour = 0; hour < 24; hour++)
                {
                    EscapeUtility.HourlyCheck(t, c);
                    if (t.MentalStateDef == RPMentalStateDefOf.RP_Escaping)
                    {
                        s.Fail($"a trusty at work in the zone began an escape in hour {hour} (the old breach rule): {TrustyLine(s, t)}");
                        break;
                    }
                }
                if (Trusties.exemptHits - hits0 != 24 && t.MentalStateDef != RPMentalStateDefOf.RP_Escaping) s.Fail($"the exemption was reached {Trusties.exemptHits - hits0} times in 24 hourly checks (24 expected): the old breach roll may have run");
                // The pass holder anywhere on owned ground out here is covered too (the way back), the farther trusty without it is not.
                s.Place(t, s.FarOut);
                c.UpdateWorkPass();
                int hitsFar = Trusties.exemptHits;
                EscapeUtility.HourlyCheck(t, c);
                if (!c.outsidePass) RPLog.Message("[RimPrison:test] trusties exemption: the trusty far out lost the pass on the next look (not a holder), as designed");
                // At work off the prison's land (the pass is given and kept on owned ground only): the old rule, and the exemption does not cover it.
                PrisonGrounds grounds = s.pm.grounds;
                (bool defined, int minX, int maxX, int minZ, int maxZ) = (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ);
                r.Add(() => (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (defined, minX, maxX, minZ, maxZ));
                Pawn off = s.Trusty("Ztex5", s.FarOut);
                (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (true, 0, 1, 0, 1);
                off.Inmate().UpdateWorkPass();
                if (off.Inmate().outsidePass || Trusties.Exempt(off, off.Inmate())) s.Fail($"a trusty at work off the prison's land has the pass ({off.Inmate().outsidePass}) or is exempt from the breach rule ({Trusties.Exempt(off, off.Inmate())}): {TrustyLine(s, off)}");
                bool brokeOff = false;
                for (int i = 0; i < 12 && !brokeOff; i++)
                {
                    EscapeUtility.HourlyCheck(off, off.Inmate());
                    brokeOff = off.MentalStateDef == RPMentalStateDefOf.RP_Escaping;
                }
                if (!brokeOff) s.Fail("a trusty at work well off the prison's land did not break for the edge under the old rule in twelve hourly checks");
                off.MentalState?.RecoverFromState();
                // A trusty a cell off the strip with no pass (the radius covers them) on ground that is not the prison's: not covered either (the radius, like the pass, is for owned ground only).
                Pawn rim = s.Trusty("Ztex8", s.OffZoneNear);
                if (rim.Inmate().outsidePass || !s.pm.IsOutsideSpace(rim.Position) || s.pm.zones.ZoneAt(rim.Position) != null) s.Fail($"the check's own premise: a trusty a cell off the strip, in the open, holding no pass: {TrustyLine(s, rim)}");
                else
                {
                    if (Trusties.Exempt(rim, rim.Inmate())) s.Fail("a trusty within three cells of the strip but off the prison's land, with no pass, is exempt from the breach rule by the radius");
                    (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (defined, minX, maxX, minZ, maxZ);
                    if (!Trusties.Exempt(rim, rim.Inmate())) s.Fail("the control: the same trusty on the prison's land, a cell off the strip, with no pass, is not exempt by the radius");
                    (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (true, 0, 1, 0, 1);
                }
                TrustyStill(rim);
                (grounds.defined, grounds.minX, grounds.maxX, grounds.minZ, grounds.maxZ) = (defined, minX, maxX, minZ, maxZ);
                // Beside the strip but inside the wing (two cells from it through the wall): no outside space, so no exemption by the radius; one standing in the strip is exempt by it (the control).
                Pawn beside = s.Trusty("Ztex6", s.At(10, 6));
                if (Trusties.Exempt(beside, beside.Inmate())) s.Fail("a trusty in the wing beside the strip (two cells from it, through the wall) is exempt from the breach rule by the radius");
                Pawn onStrip = s.Trusty("Ztex7", s.At(14, 2));
                if (!Trusties.Exempt(onStrip, onStrip.Inmate())) s.Fail("the control: a trusty standing in the strip, holding no pass, is not exempt");
                TrustyStill(off, beside, onStrip);
                // Controls.
                Pawn plain = s.Inmate("Ztex3", RPTrustyDefOf.RP_Minimum, s.InZone);
                bool broke = false;
                for (int i = 0; i < 12 && !broke; i++)
                {
                    EscapeUtility.HourlyCheck(plain, plain.Inmate());
                    broke = plain.MentalStateDef == RPMentalStateDefOf.RP_Escaping;
                }
                if (!broke) s.Fail("the control: a minimum inmate who is no trusty, standing in the edge-connected zone, did not break for the edge under the old rule in twelve hourly checks");
                plain.MentalState?.RecoverFromState();
                Pawn far = s.Trusty("Ztex4", s.FarOut, claim: s.bed2);
                far.Inmate().outsidePass = false;
                broke = false;
                for (int i = 0; i < 12 && !broke; i++)
                {
                    EscapeUtility.HourlyCheck(far, far.Inmate());
                    broke = far.MentalStateDef == RPMentalStateDefOf.RP_Escaping;
                }
                if (!broke) s.Fail("the control: a trusty well beyond the zone with no pass did not break for the edge under the old rule");
                far.MentalState?.RecoverFromState();
                // One real hour with the check running by itself (the counter moves).
                TrustyStill(t);
                s.Place(t, s.InZone);
                Trusties.exemptHits = 0;
                s.pm.party.isOut = true;
                c.UpdateWorkPass();
                t.Inmate().trustySinceTick = Math.Max(0, s.Now - GenDate.TicksPerDay);
                int ticked = TrustyTicks(() => Trusties.exemptHits > 0 || t.MentalStateDef == RPMentalStateDefOf.RP_Escaping || !s.pm.IsOutsideSpace(t.Position), GenDate.TicksPerHour + 300);
                if (t.MentalStateDef == RPMentalStateDefOf.RP_Escaping) s.Fail($"in a real hour on the strip the trusty began an escape: {TrustyLine(s, t)}");
                else if (Trusties.exemptHits == 0 && s.pm.IsOutsideSpace(t.Position)) RPLog.Message($"[RimPrison:test] trusties exemption: the hourly check did not come to the trusty in {ticked} ticks of real time (the 24 direct calls above are the check)");
                TrustyStill(t, guard, plain, far);
            });
        }

        /// <summary>
        /// The walk-off: the chance pinned to 1, the count on, no guard in sight: a hidden escape begins - no message, chronicle line or fine before the count - the status is gone, and the count finds it. (The plan's first walk-off
        /// went through the hidden-record's order: the status was read before the record existed.)
        /// </summary>
        private static void CheckTrustyWalkOff(Map map)
        {
            Guarded("trusties: a walk-off is a real escape, hidden when nobody sees it", map, r =>
            {
                const string check = "walk-off";
                // (On ground that nothing watches: no guard, tower or camera of the prison sees the strip, so the escape is a hidden one, built and not hoped for.)
                TrustyScene s = TrustyScene.Build(map, r, check, TrustyScene.Needs.Unseen);
                if (s == null) return;
                CountState count0 = s.pm.count;
                int perDay0 = s.g.countsPerDay;
                s.pm.count = new CountState();
                s.g.countsPerDay = CountSystem.DefaultPerDay;
                CountSystem.TestOn = true;
                HashSet<Pawn> only0 = CountSystem.OnlyThese;
                CountSystem.OnlyThese = new HashSet<Pawn>();
                r.Add(() =>
                {
                    s.pm.count = count0;
                    s.g.countsPerDay = perDay0;
                    CountSystem.TestOn = false;
                    CountSystem.OnlyThese = only0;
                });
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn t = s.Trusty("Ztwo1", s.InZone, claim: s.bed);
                Pawn mate = s.Inmate("Ztwo2", RPTrustyDefOf.RP_Minimum, s.At(11, 7), s.bed2);
                CompInmate c = t.Inmate();
                CountSystem.OnlyThese.Add(t);
                CountSystem.OnlyThese.Add(mate);
                s.pm.party.isOut = true;
                s.pm.party.state = PartyState.Out;
                c.UpdateWorkPass();
                if (TensionUtility.SeenAt(t.Position, map, s.pm)) { s.Fail("the check's own premise: something watches the scene's strip (a guard, a tower or a camera), the walk-off cannot be hidden"); return; }
                // (A guard on the roster, at the intake and far from the strip: the escape is held for the count, and the count has someone to tell.)
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (!CountSystem.AnyGuard(map)) { s.Fail("the check's own premise: no guard on the roster after one was made"); return; }
                Traverse.Create(s.pm).Method("RefreshGuards").GetValue();
                if (TensionUtility.SeenAt(t.Position, map, s.pm)) { s.Fail($"the check's own premise: the guard put on the roster (at {guard.Position - s.origin}) sees the strip, the walk-off cannot be hidden"); return; }
                Trusties.WalkOffOverride = 1f;
                float chance = Trusties.WalkOffChance(t, c, s.pm);
                if (chance < 1f) s.Fail($"the pinned walk-off chance is {chance}");
                Trusties.WalkOffOverride = 1f;
                int entries0 = r.NewEntries.Count(), ledger0 = r.NewLedgerEntries.Count(), letters0 = r.NewLetters.Count();
                int rolls0 = Trusties.walkOffRolls;
                EscapeUtility.HourlyCheck(t, c);
                if (t.MentalStateDef != RPMentalStateDefOf.RP_Escaping) { s.Fail($"a trusty with the walk-off pinned to 1 did not break away: {TrustyLine(s, t)}; rolls {Trusties.walkOffRolls - rolls0}"); return; }
                if (!CountSystem.IsHidden(t)) s.Fail("a walk-off nobody saw is not held for the count");
                if (c.IsTrusty || c.job != null || c.outsidePass) s.Fail($"the status, the job or the pass survived the walk-off: {TrustyLine(s, t)}");
                if (r.NewLetters.Count() != letters0 || r.NewEntries.Count() != entries0 || r.NewLedgerEntries.Count() != ledger0 || TrustyLiveMessages().Any(m => m.Contains(t.LabelShort)))
                    s.Fail($"a hidden walk-off told something before the count: {r.NewLetters.Count() - letters0} letters, {r.NewEntries.Count() - entries0} chronicle lines, {r.NewLedgerEntries.Count() - ledger0} ledger lines");
                // The count walks the wing and finds the sighting (the runner is still on the map: a letter of the sighting).
                CountSystem.Begin(map, s.pm, s.g, s.Now);
                if (!s.pm.count.Running) { s.Fail($"the count did not run for the scene's wing (mate at {mate.Position - s.origin}, in the wing zone {s.pm.zones.ZoneAt(mate.Position)?.defName ?? "none"}): the walk-off is not judged revealed"); TrustyStill(t, mate); return; }
                foreach (int idx in s.pm.count.wingsLeft.ToList()) CountSystem.WingCounted(guard, PrisonZoneDef.FromGridIndex((byte)idx));
                if (CountSystem.IsHidden(t)) s.Fail("the count did not find the walk-off");
                else if (r.NewLetters.Count() == letters0) s.Fail("the count's finding of the walk-off told nothing");
                TrustyStill(t, mate);
            });
        }

        // ---- the status and the job --------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A seen serious misconduct removes the status and the job (locked too) and queues the interrupt; the count's out-of-place mark and a light misconduct do not; an escape's start revokes quietly; and then the daily pass, the
        /// job menu and the talent letter do not give the job back; a move off minimum revokes; a gang deal's saved outside job restores to no job. (Without the hooks the status outlives all of it.)
        /// </summary>
        private static void CheckTrustyRevoke(Map map)
        {
            Guarded("trusties: what ends the status", map, r =>
            {
                const string check = "revoke";
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                Pawn p = Throwaway(map, r, RPTrustyDefOf.RP_Minimum);
                CompInmate c = p.Inmate();
                (c.processed, c.awaitingSearch, c.forcedActivity) = (true, false, null);
                c.records.Clear();
                c.entitlement = 5;
                c.tension = 10f;
                r.Add(() => (c.trustySinceTick, c.job, c.jobLocked) = (-1, null, false));
                void Make()
                {
                    c.trustySinceTick = Math.Max(0, Find.TickManager.TicksGame - GenDate.TicksPerDay);
                    c.job = RPTrustyDefOf.RP_JobOutside;
                    c.jobLocked = true;
                    p.jobs.StartJob(new Job(JobDefOf.Wait_Wander) { expiryInterval = 5000 }, JobCondition.InterruptForced);
                }
                // A seen fight: the status, the job and the lock go, the interrupt is queued (not run on the spot), the warden is told.
                Make();
                int pending0 = pm.PendingInterrupts, entries0 = r.NewEntries.Count();
                MisconductUtility.Record(p, c, pm, RPMisconductDefOf.RP_Fight);
                if (c.IsTrusty || c.job != null || c.jobLocked) TrustyFail(check, $"a seen fight left the status ({c.IsTrusty}), the job ({c.job?.defName ?? "none"}) or the lock ({c.jobLocked})");
                if (pm.PendingInterrupts <= pending0 || p.CurJobDef != JobDefOf.Wait_Wander) TrustyFail(check, $"the revoke did not queue the interrupt, or ended the job at once (queue {pm.PendingInterrupts - pending0}, doing {p.CurJobDef?.defName})");
                if (r.NewEntries.Count() <= entries0) TrustyFail(check, "a seen fight that ended the status wrote no chronicle line");
                // The count's mark, and a light misconduct: nothing.
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                Make();
                if (!RPCountDefOf.RP_OutOfPlace.mark) TrustyFail(check, "the check's own premise: the count's out-of-place misconduct is not a mark");
                MisconductUtility.Record(p, c, pm, RPCountDefOf.RP_OutOfPlace, evidence: true, chronicle: false);
                if (!c.IsTrusty || c.job == null) TrustyFail(check, "the count's out-of-place mark ended the status");
                MisconductUtility.Record(p, c, pm, DefDatabase<MisconductDef>.GetNamed("RP_Insult"), evidence: true, chronicle: false);
                if (!c.IsTrusty) TrustyFail(check, "a seen insult (severity 1) ended the status");
                // An unseen fight: not seen, so nothing (the escape's own start takes it).
                MisconductUtility.Record(p, c, pm, RPMisconductDefOf.RP_Fight, unseen: true);
                // (A loud misconduct is seen wherever they stand, unless the caller says nobody saw it.)
                if (!c.IsTrusty) TrustyFail(check, "an unseen fight ended the status");
                // An escape (seen: no count): quietly.
                // (The seen fight above wrote the revoke's own line and message already: only what the escape's start adds is judged.)
                int entries1 = r.NewEntries.Count(), told1 = TrustyRevokeTold(r, p);
                CountSystem.StartEscaping(p, "check", EscapeWay.Walked);
                if (c.IsTrusty || c.job != null || c.jobLocked) TrustyFail(check, "an escape's start left the status, the job or the lock");
                if (TrustyRevokeTold(r, p) != told1) TrustyFail(check, "an escape's revoke said something of its own");
                p.MentalState?.RecoverFromState();
                // Revoke on one who is no trusty only drops an outside job, silently: a second call after the escape posts nothing.
                int messages = TrustyLiveMessages().Count;
                Trusties.Revoke(p, "again", silent: false);
                if (TrustyLiveMessages().Count != messages || r.NewEntries.Any(e => e.text.Contains("again"))) TrustyFail(check, "a second revoke on one who is no trusty posted something");
                // Not given back: the daily pass, the warden's menu and the talent.
                c.job = null;
                c.jobLocked = false;
                LaborUtility.AssignJobs(map);
                if (c.job == RPTrustyDefOf.RP_JobOutside) TrustyFail(check, "the daily pass gave the outside job to one who is no trusty");
                c.job = RPTrustyDefOf.RP_JobOutside;
                c.jobLocked = true;
                LaborUtility.AssignJobs(map);
                if (c.job == RPTrustyDefOf.RP_JobOutside || c.jobLocked) TrustyFail(check, "the daily pass left a locked outside job on one who is no trusty");
                if (LaborUtility.Eligible(c, RPTrustyDefOf.RP_JobOutside) || LaborUtility.Eligible(c, RPTrustyDefOf.RP_JobOutside, forProgram: true)) TrustyFail(check, "a non-trusty is eligible for the outside job");
                var menu = (List<FloatMenuOption>)AccessTools.Method(typeof(InmatesTab), "JobOptions").Invoke(null, new object[] { c });
                FloatMenuOption outside = menu.FirstOrDefault(o => o.Label.StartsWith(RPTrustyDefOf.RP_JobOutside.LabelCap));
                if (outside == null) TrustyFail(check, "the job menu has no line for the outside job");
                else if (!outside.Disabled) TrustyFail(check, "the job menu offers the outside job to one who is no trusty");
                p.skills.GetSkill(SkillDefOf.Plants).Level = 14;
                if (IncidentWorker_Talent.TalentJob(p) == RPTrustyDefOf.RP_JobOutside) TrustyFail(check, "the talent letter offers the outside job to one who is no trusty");
                // A trusty: the menu offers it, the talent may name it.
                c.trustySinceTick = Math.Max(0, Find.TickManager.TicksGame);
                menu = (List<FloatMenuOption>)AccessTools.Method(typeof(InmatesTab), "JobOptions").Invoke(null, new object[] { c });
                outside = menu.FirstOrDefault(o => o.Label.StartsWith(RPTrustyDefOf.RP_JobOutside.LabelCap));
                if (outside == null || outside.Disabled || !LaborUtility.Eligible(c, RPTrustyDefOf.RP_JobOutside)) TrustyFail(check, "the control: a trusty is not offered the outside job");
                // A move off minimum security: the status and the job go.
                c.job = RPTrustyDefOf.RP_JobOutside;
                c.jobLocked = true;
                SecurityLevelDef medium = RPSecurityDefOf.RP_Medium;
                SecurityLevelDef was = c.security;
                InmateUtility.ChangeSecurity(p, c, medium, onPaper: true);
                if (c.IsTrusty || c.job != null) TrustyFail(check, "a move to medium security left the status or the job");
                c.security = was;
                // A gang deal's saved outside job: restored to no job for one who is no trusty, to the job for a trusty (the control).
                MethodInfo withdraw = AccessTools.Method(typeof(GangDeals), "Withdraw");
                InmateJobDef garden = DefDatabase<InmateJobDef>.GetNamed("RP_JobGarden");
                foreach (bool trusty in new[] { false, true })
                {
                    c.trustySinceTick = trusty ? Math.Max(0, Find.TickManager.TicksGame) : -1;
                    c.job = garden;
                    c.jobLocked = true;
                    var arc = new StoryArc { deal = new GangDeal { term = GangDealTerm.Job, jobMemberId = p.thingIDNumber, jobDef = garden, prevJob = RPTrustyDefOf.RP_JobOutside, prevJobLocked = true } };
                    withdraw.Invoke(null, new object[] { arc, map });
                    if (trusty && (c.job != RPTrustyDefOf.RP_JobOutside || !c.jobLocked)) TrustyFail(check, $"the control: a deal's end did not give a trusty their outside job back ({c.job?.defName ?? "none"})");
                    if (!trusty && (c.job != null || c.jobLocked)) TrustyFail(check, $"a deal's end gave the outside job back to one who is no trusty ({c.job?.defName ?? "none"}, locked {c.jobLocked})");
                }
            });
        }

        /// <summary>The stay ends, and the save loads: release, transfer, death and escape (<see cref="StorySystem.Notify_Leaving"/>) take the status and the job quietly, a new stay starts with neither, and a load clears an outside job that has no status.</summary>
        private static void CheckTrustyStay(Map map)
        {
            Guarded("trusties: the stay ending, a new stay and a load", map, r =>
            {
                const string check = "stay";
                Pawn p = Throwaway(map, r, RPTrustyDefOf.RP_Minimum);
                CompInmate c = p.Inmate();
                r.Add(() => (c.trustySinceTick, c.job, c.jobLocked, c.outsidePass, c.workPass) = (-1, null, false, false, null));
                void Make()
                {
                    c.trustySinceTick = Math.Max(0, Find.TickManager.TicksGame - GenDate.TicksPerDay);
                    c.job = RPTrustyDefOf.RP_JobOutside;
                    c.jobLocked = true;
                    c.outsidePass = true;
                    c.workPass = RPTrustyDefOf.RP_OutsideWork;
                }
                Make();
                Trusties.Notify_Leaving(p);
                if (c.IsTrusty || c.job != null || c.jobLocked || c.outsidePass || c.workPass != null) TrustyFail(check, "the stay's end left the status, the job, the lock or the pass");
                if (r.NewEntries.Any(e => e.text.Contains("trusty"))) TrustyFail(check, "the stay's end wrote a chronicle line for the status");
                Make();
                c.ResetForNewStay();
                if (c.IsTrusty || c.job != null || c.jobLocked || c.outsidePass || c.workPass != null) TrustyFail(check, "a new stay began with the status, the job, the lock or the pass of the old one");
                // A job with no status after a load (the save fix is the one the load runs).
                c.job = RPTrustyDefOf.RP_JobOutside;
                c.jobLocked = true;
                c.trustySinceTick = -1;
                c.FixOutsideJobAfterLoad();
                if (c.job != null || c.jobLocked) TrustyFail(check, "a load left an outside job on an inmate with no status");
                c.trustySinceTick = Math.Max(0, Find.TickManager.TicksGame);
                c.job = RPTrustyDefOf.RP_JobOutside;
                c.FixOutsideJobAfterLoad();
                if (c.job != RPTrustyDefOf.RP_JobOutside) TrustyFail(check, "the control: a load took the outside job from a trusty");
                // A death takes the status too (the notification the kill sends).
                Make();
                PrisonGame.Instance.story.Notify_Leaving(p);
                if (c.IsTrusty || c.outsidePass) TrustyFail(check, "the story's leaving notice left the status or the pass");
            });
        }

        // ---- job rules in the regime -----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The work slot gives the outside job only to a trusty while the party is out: not to one who is no trusty (the job set by hand), not while the party is in (no wandering into the zone either) - and then the work is the tree on the strip.</summary>
        private static void CheckTrustyJobRules(Map map)
        {
            Guarded("trusties: the work slot's dispatch", map, r =>
            {
                const string check = "dispatch";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Plant tree = s.Tree(s.At(12, 3));
                Plant outsideTree = s.Tree(s.At(12, -5));
                Pawn t = s.Trusty("Ztdp1", s.Corridor, claim: s.bed);
                Pawn plain = s.Inmate("Ztdp2", RPTrustyDefOf.RP_Minimum, s.At(11, 6));
                plain.Inmate().job = RPTrustyDefOf.RP_JobOutside;
                RegimeActivityWorker work = RPLaborDefOf.RP_Work.Worker;
                s.pm.party.isOut = false;
                if (work.TryGiveJob(t, t.Inmate(), s.pm) != null) s.Fail("a trusty is given work (or a wander into the zone) while the party is in");
                s.pm.party.isOut = true;
                s.pm.party.state = PartyState.Out;
                if (work.TryGiveJob(plain, plain.Inmate(), s.pm) != null) s.Fail("an inmate who is no trusty, with the outside job set by hand, is given work or a wander into the zone");
                Job job = work.TryGiveJob(t, t.Inmate(), s.pm);
                if (job == null) s.Fail($"a trusty on the job with the party out and a tree in the zone is given nothing: {TrustyLine(s, t)}");
                else
                {
                    // (Over many asks: the tree on the strip, never the one outside it.)
                    var targets = new HashSet<Thing>();
                    for (int i = 0; i < 40; i++)
                    {
                        Job j = work.TryGiveJob(t, t.Inmate(), s.pm);
                        if (j?.def == JobDefOf.CutPlant && j.targetA.Thing != null) targets.Add(j.targetA.Thing);
                    }
                    if (targets.Contains(outsideTree)) s.Fail("a tree outside the zone was given as the trusty's work");
                    if (job.def == JobDefOf.CutPlant && job.targetA.Thing != tree) s.Fail($"the trusty's work is a cut of {job.targetA.Thing}, the tree on the strip expected");
                }
                // A target off the zone is no job (the guard on every job: queued targets, a cut, a haul).
                var cutOff = new Job(JobDefOf.CutPlant, outsideTree);
                if (Trusties.JobInZone(cutOff, s.pm)) s.Fail("the cut of a tree outside the zone passes the zone check");
                if (!Trusties.JobInZone(new Job(JobDefOf.CutPlant, tree), s.pm)) s.Fail("the control: the cut of the tree on the strip fails the zone check");
                var harvest = new Job(JobDefOf.Harvest);
                harvest.AddQueuedTarget(TargetIndex.A, tree);
                harvest.AddQueuedTarget(TargetIndex.A, outsideTree);
                if (Trusties.JobInZone(harvest, s.pm)) s.Fail("a harvest queue with a plant outside the zone passes the zone check");
                TrustyStill(t, plain);
            });
        }

        // ---- placement ---------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// With the party in during a work slot (and the slot before it also work) an outside-job holder is expected in their cell: in it the count and the grades find them in place, in the corridor they are out of place; with the party
        /// out the strip is their place. A count that walks the wing writes up the one in the corridor and not the one in the cell. (Without the rule the work slot expected them on a strip nobody sent them to.)
        /// </summary>
        private static void CheckTrustyPlacement(Map map)
        {
            Guarded("trusties: the place the regime expects", map, r =>
            {
                const string check = "placement";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn inCell = s.Trusty("Ztpl1", s.InCell, claim: s.bed);
                Pawn loiterer = s.Trusty("Ztpl2", s.Corridor, claim: s.bed2);
                s.pm.party.isOut = false;
                if (!RegimePlacement.InExpectedPlace(inCell, RPLaborDefOf.RP_Work, s.pm)) s.Fail("with the party in, a trusty in their cell is out of place in the work slot");
                if (RegimePlacement.InExpectedPlace(loiterer, RPLaborDefOf.RP_Work, s.pm)) s.Fail("with the party in, a trusty loitering in the corridor is in place in the work slot");
                s.pm.party.isOut = true;
                s.pm.party.state = PartyState.Out;
                if (RegimePlacement.InExpectedPlace(inCell, RPLaborDefOf.RP_Work, s.pm)) s.Fail("the control: with the party out, a trusty in their cell is in place in the work slot");
                s.Place(inCell, s.InZone);
                if (!RegimePlacement.InExpectedPlace(inCell, RPLaborDefOf.RP_Work, s.pm)) s.Fail("with the party out, a trusty in the zone is out of place in the work slot");
                s.Place(inCell, s.InCell);
                s.pm.party.isOut = false;
                // The count: two work slots in a row, the wing walked.
                CountState count0 = s.pm.count;
                int perDay0 = s.g.countsPerDay;
                s.pm.count = new CountState();
                s.g.countsPerDay = CountSystem.DefaultPerDay;
                CountSystem.TestOn = true;
                HashSet<Pawn> only0 = CountSystem.OnlyThese;
                CountSystem.OnlyThese = new HashSet<Pawn> { inCell, loiterer };
                r.Add(() =>
                {
                    s.pm.count = count0;
                    s.g.countsPerDay = perDay0;
                    CountSystem.TestOn = false;
                    CountSystem.OnlyThese = only0;
                });
                if (!CountSystem.Accounted(inCell, inCell.Inmate(), s.pm, s.g)) s.Fail("the count does not account for a trusty in their cell with the party in");
                loiterer.pather.StopDead();
                if (CountSystem.Accounted(loiterer, loiterer.Inmate(), s.pm, s.g)) s.Fail("the count accounts for a trusty loitering in the corridor with the party in (the control)");
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CountSystem.Begin(map, s.pm, s.g, s.Now);
                if (!s.pm.count.Running) s.Fail("the count did not run for the scene's wing: the placement is not judged by a count that walks it");
                else
                {
                    foreach (int idx in s.pm.count.wingsLeft.ToList()) CountSystem.WingCounted(guard, PrisonZoneDef.FromGridIndex((byte)idx));
                    if (inCell.Inmate().records.Any(x => x.def == RPCountDefOf.RP_OutOfPlace)) s.Fail("the count wrote up a trusty in their cell as out of place");
                    if (!loiterer.Inmate().records.Any(x => x.def == RPCountDefOf.RP_OutOfPlace)) s.Fail("the count that walked the wing did not write up the trusty loitering in the corridor (the control: the count sees out of place at all)");
                }
                TrustyStill(inCell, loiterer);
            });
        }

        // ---- the work ----------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A trusty's cut of a tree in the zone counts as work; the same cut by an inmate on another job (the garden's, say) does not (the plan's first rule counted every cut); an obstruction hauled in the zone counts; the wood is
        /// released, and only wood and crops (never another forbidden item the warden left there); the garden still releases everything in its own zone; a harvest batch across the zone's edge is pruned to the zone.
        /// </summary>
        private static void CheckTrustyWork(Map map)
        {
            Guarded("trusties: the work and what it yields", map, r =>
            {
                const string check = "work";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Plant tree = s.Tree(s.At(12, 3));
                Plant offTree = s.Tree(s.At(12, -5));
                Pawn t = s.Trusty("Ztwk1", s.At(12, 2));
                Pawn gardener = s.Inmate("Ztwk2", RPTrustyDefOf.RP_Minimum, s.At(14, 2));
                // (Cleaning: the garden's own job counts a cut as work, anywhere: that is the garden's rule and stays.)
                gardener.Inmate().job = DefDatabase<InmateJobDef>.GetNamed("RP_JobCleaning");
                var cut = new Job(JobDefOf.CutPlant, tree);
                t.jobs.StartJob(cut, JobCondition.InterruptForced);
                if (t.CurJobDef != JobDefOf.CutPlant) { s.Fail($"the check's own fixture: the cut did not start for the trusty (doing {t.CurJobDef?.defName ?? "nothing"}), so no work was judged"); TrustyStill(t, gardener); return; }
                if (!LaborUtility.IsWorking(t, t.Inmate())) s.Fail("a trusty's cut of a tree in the zone does not count as work");
                t.jobs.EndCurrentJob(JobCondition.InterruptForced);
                t.jobs.StartJob(new Job(JobDefOf.CutPlant, offTree), JobCondition.InterruptForced);
                if (t.CurJobDef != JobDefOf.CutPlant) s.Fail("the check's own fixture: the cut of the tree off the zone did not start");
                else if (LaborUtility.IsWorking(t, t.Inmate())) s.Fail("a trusty's cut of a tree outside the zone counts as work");
                t.jobs.EndCurrentJob(JobCondition.InterruptForced);
                gardener.jobs.StartJob(new Job(JobDefOf.CutPlant, tree), JobCondition.InterruptForced);
                if (gardener.CurJobDef != JobDefOf.CutPlant) s.Fail("the check's own fixture: the cut did not start for the inmate on another job");
                else if (LaborUtility.IsWorking(gardener, gardener.Inmate())) s.Fail("an inmate on another job (cleaning), cutting a tree on the outside strip, counts as working");
                gardener.jobs.EndCurrentJob(JobCondition.InterruptForced);
                // A haul that clears a way in the zone (a sow's obstruction): from the zone to the zone, by real ticks through the pickup, it counts as work at every tick; its destination must lie in the zone (any other cell in the open is no work),
                // and a thing already in the hands is not looked for where the carrier walks.
                // (Real ticks of the live prison run in the scene: what it does with a pawn of the scene - a transfer, a death - must come as a line of the check, not a throw at the next placing.)
                bool Lost(string when)
                {
                    foreach ((string who, Pawn pawn) in new[] { ("the trusty", t), ("the gardener", gardener) })
                        if (TrustyGone(pawn, map) is { } gone)
                        {
                            s.Fail($"the check's own fixture: {who} {gone} {when}, so the rest of the work was not judged");
                            TrustyStill(t, gardener);
                            return true;
                        }
                    return false;
                }
                var crate = (Thing)ThingMaker.MakeThing(ThingDefOf.Steel);
                crate.stackCount = 5;
                GenSpawn.Spawn(crate, s.At(11, 3), map);
                s.r.Add(() => { if (crate.Spawned) crate.Destroy(DestroyMode.Vanish); });
                // (As vanilla sets a haul aside, to a cell that is no storage: a haul to storage fails at once on a cell no stockpile holds, and the trusty goes on to wander.)
                var haul = new Job(JobDefOf.HaulToCell, crate, s.At(16, 3)) { count = 5, haulMode = HaulMode.ToCellNonStorage, ignoreDesignations = true };
                if (!Trusties.JobInZone(haul, s.pm)) s.Fail("the haul of a thing in the zone to a cell in the zone fails the zone check");
                if (Trusties.JobInZone(new Job(JobDefOf.HaulToCell, crate, s.At(10, -3)) { count = 5 }, s.pm)) s.Fail("a haul to a cell in the open off the zone passes the zone check (the destination must lie in the zone)");
                t.jobs.StartJob(haul, JobCondition.InterruptForced);
                if (t.CurJobDef != JobDefOf.HaulToCell) s.Fail($"the check's own fixture: the haul did not start for the trusty (doing {t.CurJobDef?.defName ?? "nothing"}), so no haul was judged");
                else
                {
                    // The wait for the pickup is a window of real ticks, a walk of a few cells and the stoop (not the first tick: a job that is gone at the first tick is the prison taking the trusty from it, and is told with what took it). A
                    // haul that ended before the pickup is given again, twice at most, so that a walk the prison cut short is not the verdict; one that never picks the crate up in the window fails.
                    const int PickupWindow = 900;
                    bool carried = false, notWorking = false, endedByTheProduct = false;
                    int restarts = 0, firstEnd = -1, ticksRun = 0;
                    string endedAs = null;
                    int hauled = TrustyTicks(() =>
                    {
                        ticksRun++;
                        if (!carried && ticksRun > PickupWindow) return true;
                        if (t.CurJobDef != JobDefOf.HaulToCell)
                        {
                            if (carried || !crate.Spawned) return true;
                            if (firstEnd < 0)
                            {
                                firstEnd = ticksRun;
                                endedAs = $"doing {t.CurJobDef?.defName ?? "nothing"} at {t.Position - s.origin}, held {t.ParentHolder?.ToString() ?? "by nothing"}";
                            }
                            // (Only the world's hand on the trusty - carried off, put in a bed - is a cause that is not the product's: a haul that ended with nothing holding them was ended by the trusties' own logic, and is the verdict.)
                            if (t.ParentHolder == null && t.Spawned)
                            {
                                endedByTheProduct = true;
                                return true;
                            }
                            if (restarts >= 2) return true;
                            restarts++;
                            t.jobs.StartJob(new Job(JobDefOf.HaulToCell, crate, s.At(16, 3)) { count = 5, haulMode = HaulMode.ToCellNonStorage, ignoreDesignations = true }, JobCondition.InterruptForced);
                            return t.CurJobDef != JobDefOf.HaulToCell;
                        }
                        carried |= t.carryTracker.CarriedThing != null;
                        if (!LaborUtility.IsWorking(t, t.Inmate())) notWorking = true;
                        return notWorking;
                    }, PickupWindow + 600);
                    if (restarts > 0) RPLog.Message($"[trusty] work: the haul ended before the pickup {restarts} time(s), first at tick {firstEnd} ({endedAs}); carried {carried}");
                    if (endedByTheProduct) s.Fail($"the haul ended before the pickup with nothing holding the trusty ({endedAs}; tick {firstEnd}, carried {carried}): the trusties' own logic ended a valid haul");
                    else if (!carried) s.Fail($"the haul never came to the pickup in {hauled} ticks (the job ended {restarts} time(s), first {endedAs ?? "never"}; doing {t.CurJobDef?.defName ?? "nothing"} at {t.Position - s.origin}, the crate at {crate.Position - s.origin}, spawned {crate.Spawned}): the carrying was not judged");
                    if (notWorking) s.Fail($"a haul in the zone stopped counting as work on the way (carrying {carried}, at {t.Position - s.origin})");
                }
                t.jobs.EndCurrentJob(JobCondition.InterruptForced);
                TrustyStill(t);
                crate.Destroy(DestroyMode.Vanish);
                if (Lost("after the haul")) return;
                // In the hands, off the zone (the carrier's own position is where the thing is held): still the job's, to the zone.
                var load = (Thing)ThingMaker.MakeThing(ThingDefOf.Steel);
                load.stackCount = 4;
                GenSpawn.Spawn(load, s.At(13, 2), map);
                s.r.Add(() => { if (!load.Destroyed) load.Destroy(DestroyMode.Vanish); });
                if (t.carryTracker.CarriedThing != null) t.carryTracker.DestroyCarriedThing();
                s.Place(t, s.OffZoneNear);
                if (t.carryTracker.TryStartCarry(load, 4) != 4) s.Fail("the check's own fixture: the thing was not taken into the hands");
                else
                {
                    var carriedHaul = new Job(JobDefOf.HaulToCell, load, s.At(14, 2)) { count = 4 };
                    if (!Trusties.JobInZone(carriedHaul, s.pm)) s.Fail($"a haul in the hands, carried a cell off the zone, fails the zone check (the source was read from the carrier's position {t.Position - s.origin})");
                    if (Trusties.JobInZone(new Job(JobDefOf.HaulToCell, load, s.At(10, -3)) { count = 4 }, s.pm)) s.Fail("a carried haul to a cell off the zone passes the zone check");
                }
                t.carryTracker.DestroyCarriedThing();
                s.Place(t, s.At(12, 2));
                // Release: the wood on the strip and the crops, not another forbidden item; the garden releases all in its own zone.
                var wood = (Thing)ThingMaker.MakeThing(ThingDefOf.WoodLog);
                wood.stackCount = 10;
                GenSpawn.Spawn(wood, s.At(13, 3), map);
                var steel = (Thing)ThingMaker.MakeThing(ThingDefOf.Steel);
                steel.stackCount = 10;
                GenSpawn.Spawn(steel, s.At(14, 3), map);
                s.r.Add(() => { if (wood.Spawned) wood.Destroy(DestroyMode.Vanish); if (steel.Spawned) steel.Destroy(DestroyMode.Vanish); });
                wood.SetForbidden(true, warnOnFail: false);
                steel.SetForbidden(true, warnOnFail: false);
                InmateJobWorker_Outside.ReleaseProduce(map, s.pm);
                if (wood.IsForbidden(Faction.OfPlayer)) s.Fail("the wood cut in the zone is not released to the prison");
                if (!steel.IsForbidden(Faction.OfPlayer)) s.Fail("another forbidden item in the zone (steel) was released by the outside work: only wood and crops are the prison's");
                // The garden regression: its release takes every forbidden item in and around the garden.
                PrisonZoneDef garden = RPLaborDefOf.RP_Garden;
                IntVec3 gc = s.At(3, 1);
                PrisonZoneDef gardenWas = s.pm.zones.ZoneAt(gc);
                s.PaintCell(gc, garden);
                var gsteel = (Thing)ThingMaker.MakeThing(ThingDefOf.Steel);
                gsteel.stackCount = 10;
                GenSpawn.Spawn(gsteel, gc, map);
                s.r.Add(() => { if (gsteel.Spawned) gsteel.Destroy(DestroyMode.Vanish); });
                gsteel.SetForbidden(true, warnOnFail: false);
                InmateJobWorker_Garden.ReleaseProduce(map, s.pm);
                if (gsteel.IsForbidden(Faction.OfPlayer)) s.Fail("the garden no longer releases the forbidden items in its zone");
                s.PaintCell(gc, gardenWas);
                // The garden regression, the work itself, for real: a growing zone of ripe crop on cells painted as the garden gives a gardener a harvest (its queue kept to the garden) that is done by real ticks and yields the crop, and an empty
                // one a sowing that is done by real ticks and leaves the crop standing. The refactored grower code is the outside work's too. (The soil is made fertile under the cells, and the crop is one the season allows: if none can be
                // had the check fails, it does not skip.)
                // (On the bare ground beside the strip, within the trusty's reach of it: the one who does the work is the trusty, who the breach rule leaves be there through the real ticks; the gardener the jobs are asked for stays inside.)
                // (The one who does the garden's work holds the garden's job while they do it, and no class is on: a trusty who holds the outside job and is given a job on targets off the zone has it ended by the hourly look - rightly, it is
                // no outside work, that is what this check had taken for a fault of the garden's harvest - and a class takes a worker from the Work slot every hour.)
                CompInmate standIn = t.Inmate();
                (InmateJobDef outsideJob, bool outsideLocked) = (standIn.job, standIn.jobLocked);
                standIn.job = DefDatabase<InmateJobDef>.GetNamed("RP_JobGarden");
                List<ProgramDef> programsOff = s.g.programs.disabled.ToList();
                s.g.programs.disabled = DefDatabase<ProgramDef>.AllDefsListForReading.ToList();
                void BackToTheOutsideJob()
                {
                    s.g.programs.disabled = programsOff;
                    (standIn.job, standIn.jobLocked) = (outsideJob, outsideLocked);
                }
                s.r.Add(BackToTheOutsideJob);
                var gardenCells = new CellRect(s.origin.x + 2, s.origin.z, 3, 1);
                ThingDef crop = TrustyGrowerCrop(map, s.r, gardenCells, check);
                if (crop == null) { s.Fail("the check's own fixture: no crop the soil allows on the scene's garden cells, so the garden's sowing and harvest cannot be judged"); TrustyStill(t, gardener); return; }
                var gardenGrowing = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(gardenGrowing);
                s.r.Add(() => { if (map.zoneManager.AllZones.Contains(gardenGrowing)) gardenGrowing.Delete(); });
                foreach (IntVec3 cell in gardenCells)
                {
                    s.PaintCell(cell, garden);
                    gardenGrowing.AddCell(cell);
                    if (cell.GetPlant(map) is { } old) old.Destroy(DestroyMode.Vanish);
                    var ripe = (Plant)ThingMaker.MakeThing(crop);
                    ripe.Growth = 1f;
                    GenSpawn.Spawn(ripe, cell, map);
                    s.r.Add(() => { if (ripe.Spawned) ripe.Destroy(DestroyMode.Vanish); });
                }
                gardenGrowing.SetPlantDefToGrow(crop);
                InmateJobWorker gardenWorker = DefDatabase<InmateJobDef>.GetNamed("RP_JobGarden").Worker;
                // (The jobs are asked for the gardener and done by the trusty, who the breach rule leaves be beside the strip through the real ticks; the gardener waits inside the wing: out here it would run them off.)
                TrustyStill(gardener);
                s.Place(gardener, s.Corridor);
                // (The live prison's own garden is in the zone's cells too: the job asked for is one on the scene's own.)
                Job harvestJob = null;
                int gardenOff = 0;
                for (int i = 0; i < 400 && harvestJob == null; i++)
                {
                    Job gj = gardenWorker.TryGiveJob(gardener, s.pm);
                    if (gj?.def != JobDefOf.Harvest || gj.targetQueueA == null || !gj.targetQueueA.Any(q => gardenCells.Contains(q.Cell))) continue;
                    if (gj.targetQueueA.Any(q => s.pm.zones.ZoneAt(q.Cell) != garden)) gardenOff++;
                    harvestJob = gj;
                }
                if (harvestJob == null) s.Fail("the garden gave no harvest of the ripe crop on the scene's cells in 400 asks (the refactored grower work)");
                if (gardenOff > 0) s.Fail("the garden's harvest queue held plants off the garden");
                if (harvestJob != null)
                {
                    int ripe0 = gardenCells.Cells.Count(cell => cell.GetPlant(map) != null);
                    int yield0 = map.listerThings.ThingsOfDef(crop.plant.harvestedThingDef).Where(x => gardenCells.ExpandedBy(6).Contains(x.Position)).Sum(x => x.stackCount);
                    if (!s.Place(t, gardenCells.CenterCell + new IntVec3(0, 0, -1))) { TrustyStill(gardener); return; }
                    t.jobs.StartJob(harvestJob, JobCondition.InterruptForced);
                    if (t.CurJobDef != JobDefOf.Harvest) s.Fail($"the check's own fixture: the garden's harvest did not start (doing {t.CurJobDef?.defName ?? "nothing"})");
                    else
                    {
                        int harvested = TrustyTicks(() => gardenCells.Cells.All(cell => cell.GetPlant(map) == null) || t.CurJobDef != JobDefOf.Harvest, 4500);
                        int left = gardenCells.Cells.Count(cell => cell.GetPlant(map) != null), yield1 = map.listerThings.ThingsOfDef(crop.plant.harvestedThingDef).Where(x => gardenCells.ExpandedBy(6).Contains(x.Position)).Sum(x => x.stackCount);
                        // (The plants are gone: the yield is a roll of the picker's skill - a failed harvest takes the plant too - so it is told, not judged.)
                        RPLog.Message($"[RimPrison:test] trusties work: the garden's harvest took {ripe0 - left} of {ripe0} plants in {harvested} ticks ({crop.plant.harvestedThingDef.defName} near the cells {yield0} -> {yield1})");
                        if (left >= ripe0) s.Fail($"the garden's harvest by a gardener, in real ticks, took no plant ({ripe0} ripe plants, {left} after {harvested} ticks; doing {t.CurJobDef?.defName ?? "nothing"})");
                    }
                    TrustyStill(t);
                }
                if (Lost("after the garden's harvest")) return;
                foreach (IntVec3 cell in gardenCells)
                    if (cell.GetPlant(map) is { } ripe2) ripe2.Destroy(DestroyMode.Vanish);
                // The empty zone, sown: the job is one on the scene's cells, and the crop stands there when it is done.
                Job sowJob = null;
                for (int i = 0; i < 400 && sowJob == null; i++)
                    if (gardenWorker.TryGiveJob(gardener, s.pm) is { def: var sd } sj && sd == JobDefOf.Sow && gardenCells.Contains(sj.targetA.Cell)) sowJob = sj;
                if (sowJob == null) s.Fail($"the garden gave no sowing of {crop.defName} on the empty growing zone in 400 asks (season {PlantUtility.GrowthSeasonNow(gardenCells.CenterCell, map, crop)}, soil {gardenCells.CenterCell.GetTerrain(map).defName})");
                else
                {
                    IntVec3 sowAt = sowJob.targetA.Cell;
                    if (!s.Place(t, sowAt + new IntVec3(0, 0, -1))) { TrustyStill(gardener); return; }
                    t.jobs.StartJob(sowJob, JobCondition.InterruptForced);
                    if (t.CurJobDef != JobDefOf.Sow) s.Fail($"the check's own fixture: the garden's sowing did not start (doing {t.CurJobDef?.defName ?? "nothing"})");
                    else
                    {
                        int sown = TrustyTicks(() => sowAt.GetPlant(map) is { } stand && stand.def == crop || t.CurJobDef != JobDefOf.Sow, 3500);
                        if (sowAt.GetPlant(map) is { } planted) s.r.Add(() => { if (planted.Spawned) planted.Destroy(DestroyMode.Vanish); });
                        if (sowAt.GetPlant(map)?.def != crop) s.Fail($"the garden's sowing by a gardener, in real ticks, left no {crop.defName} at {sowAt - s.origin} after {sown} ticks (doing {t.CurJobDef?.defName ?? "nothing"})");
                    }
                    TrustyStill(t);
                }
                if (Lost("after the garden's sowing")) return;
                BackToTheOutsideJob();
                s.Place(t, s.At(12, 2));
                // A harvest batch across the zone's edge: a growing zone of potatoes half on the strip, half beside it; the job the worker gives has its queue pruned to the strip, and it is given.
                var growing = new Zone_Growing(map.zoneManager);
                map.zoneManager.RegisterZone(growing);
                s.r.Add(() => { if (map.zoneManager.AllZones.Contains(growing)) growing.Delete(); });
                var plants = new List<Plant>();
                foreach (IntVec3 cell in new CellRect(s.origin.x + 5, s.origin.z - 1, 1, 6))
                {
                    growing.AddCell(cell);
                    if (cell.GetPlant(map) is { } old) old.Destroy(DestroyMode.Vanish);
                    var potato = (Plant)ThingMaker.MakeThing(ThingDefOf.Plant_Potato);
                    potato.Growth = 1f;
                    GenSpawn.Spawn(potato, cell, map);
                    plants.Add(potato);
                    s.r.Add(() => { if (potato.Spawned) potato.Destroy(DestroyMode.Vanish); });
                }
                growing.SetPlantDefToGrow(ThingDefOf.Plant_Potato);
                s.pm.party.isOut = true;
                s.pm.party.state = PartyState.Out;
                tree.Destroy(DestroyMode.Vanish);
                offTree.Destroy(DestroyMode.Vanish);
                int harvests = 0, offZone = 0, queued = 0;
                for (int i = 0; i < 80; i++)
                {
                    Job j = RPTrustyDefOf.RP_JobOutside.Worker.TryGiveJob(t, s.pm);
                    if (j == null) continue;
                    if (!Trusties.JobInZone(j, s.pm)) offZone++;
                    if (j.def != JobDefOf.Harvest) continue;
                    harvests++;
                    queued += j.targetQueueA?.Count ?? 0;
                }
                if (offZone > 0) s.Fail($"the outside worker gave {offZone} jobs with a target off the zone");
                if (harvests == 0) s.Fail("the outside worker gave no harvest of the ripe potatoes half on the strip in 80 asks: a batch across the zone's edge is pruned to the zone, not refused whole");
                else if (queued == 0) s.Fail("the outside worker's harvests had no plant queued: the batch was pruned to nothing");
                TrustyStill(t, gardener);
            });
        }

        // ---- pay ---------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>N trusty-hours in the zone book N x 6 dollars in one grounds-contract line at midnight; an hour off the zone books nothing (it is no work).</summary>
        private static void CheckTrustyPay(Map map)
        {
            Guarded("trusties: the grounds contract", map, r =>
            {
                const string check = "pay";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                Plant tree = s.Tree(s.At(12, 3));
                Plant offTree = s.Tree(s.At(12, -5));
                Pawn t = s.Trusty("Ztpy1", s.At(12, 2));
                CompInmate c = t.Inmate();
                int hours0 = s.pm.party.hours;
                t.jobs.StartJob(new Job(JobDefOf.CutPlant, tree), JobCondition.InterruptForced);
                if (t.CurJobDef != JobDefOf.CutPlant) { s.Fail($"the check's own fixture: the cut did not start for the trusty (doing {t.CurJobDef?.defName ?? "nothing"}), no hour of work was judged"); TrustyStill(t); return; }
                const int N = 4;
                for (int i = 0; i < N; i++)
                    if (LaborUtility.IsWorking(t, c)) c.WorkedAnHour(t, s.pm);
                if (s.pm.party.hours - hours0 != N) s.Fail($"{N} hours at work in the zone were credited as {s.pm.party.hours - hours0}");
                t.jobs.EndCurrentJob(JobCondition.InterruptForced);
                t.jobs.StartJob(new Job(JobDefOf.CutPlant, offTree), JobCondition.InterruptForced);
                if (t.CurJobDef != JobDefOf.CutPlant) s.Fail($"the check's own fixture: the cut of the tree off the zone did not start (doing {t.CurJobDef?.defName ?? "nothing"})");
                for (int i = 0; i < 3; i++)
                    if (LaborUtility.IsWorking(t, c)) c.WorkedAnHour(t, s.pm);
                if (s.pm.party.hours - hours0 != N) s.Fail("hours spent cutting off the zone were credited");
                t.jobs.EndCurrentJob(JobCondition.InterruptForced);
                int credited = s.pm.party.hours;
                int before = r.NewLedgerEntries.Count(e => e.category == RPTrustyDefOf.RP_GroundsContract);
                Trusties.Daily(s.g, map);
                var lines = r.NewLedgerEntries.Where(e => e.category == RPTrustyDefOf.RP_GroundsContract).ToList();
                if (lines.Count - before != 1 || lines[lines.Count - 1].amount != credited * Trusties.PayPerHour) s.Fail($"the day's {credited} trusty-hours booked {lines.Count - before} lines, the last for {lines.LastOrDefault()?.amount} ({credited * Trusties.PayPerHour} expected)");
                if (s.pm.party.hours != 0) s.Fail("the day's hours were not cleared once booked");
                Trusties.Daily(s.g, map);
                if (r.NewLedgerEntries.Count(e => e.category == RPTrustyDefOf.RP_GroundsContract) != lines.Count) s.Fail("a day with no hours booked a line");
                TrustyStill(t);
            });
        }

        // ---- the party ---------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The party's conditions: out with a guard near (and the ratio: one guard for 3, two trusties for one guard at 2), in with none; the posts count for going out; a riot, a raid, a lockdown of the minimum wing, an evacuation, a seen
        /// escape each call it in at once, held in an hour; a shortfall while out calls it in only after an hour; a zone gone (the strip erased) keeps it in.
        /// </summary>
        private static void CheckTrustyParty(Map map)
        {
            Guarded("trusties: the party's conditions", map, r =>
            {
                const string check = "party";
                // (Where none of the prison's own guards or posts are near, so what is counted near the zone is the check's own.)
                TrustyScene s = TrustyScene.Build(map, r, check, TrustyScene.Needs.NoGuardsNear);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                TrustyParty party = s.pm.party;
                Pawn a = s.Trusty("Ztpt1", s.Corridor, claim: s.bed);
                Pawn b = s.Trusty("Ztpt2", s.At(11, 6), claim: s.bed2);
                Pawn guard = s.Guard("Ztpt3", s.At(10, 2));
                // The ratio: one guard for three, two trusties: out; one guard at a ratio of 2 and three trusties: short.
                s.Beat();
                if (!party.isOut) s.Fail($"one guard near the zone and two trusties at a ratio of {party.ratio}: the party is {party.state}");
                Pawn c3 = s.Trusty("Ztpt4", s.At(9, 6));
                Pawn c4 = s.Trusty("Ztpt5", s.At(12, 6));
                party.ratio = 2;
                party.holdUntilTick = -1;
                s.Beat();
                if (party.near != 1) s.Fail($"the check's own premise: {party.near} guards or posts are near the scene's strip, the one guard of the check expected");
                else
                {
                    if (party.state != PartyState.Out) s.Fail($"four holders, a ratio of 2 and one guard: the party was called in at the first beat of the shortfall ({party.state}), an hour of it is waited out while it is out");
                    party.shortSinceTick = s.Now - Trusties.ShortTicks - 1;
                    s.Beat();
                    if (party.isOut || party.state != PartyState.Short) s.Fail($"four trusties, one guard and a ratio of 2: the party was not called in after an hour of the shortfall ({party.state})");
                }
                party.ratio = 4;
                party.holdUntilTick = -1;
                s.Beat();
                if (!party.isOut) s.Fail($"four trusties, one guard and a ratio of 4: the party is {party.state}");
                c3.Inmate().trustySinceTick = -1;
                c3.Inmate().job = null;
                c4.Inmate().trustySinceTick = -1;
                c4.Inmate().job = null;
                party.ratio = Trusties.DefaultRatio;
                // The hold: called in by a riot, it is not sent out again within the hour; the hold over, it goes.
                Pawn rioter = s.Inmate("Ztpt6", RPSecurityDefOf.RP_Medium, s.At(12, 7));
                rioter.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
                s.Beat();
                if (party.isOut || party.state != PartyState.Riot) s.Fail($"a rioter on the map did not call the party in at once (state {party.state})");
                rioter.MentalState?.RecoverFromState();
                s.Beat();
                if (party.isOut || party.state != PartyState.Hold) s.Fail($"the party was sent out again at once after the riot (state {party.state}), the hold of an hour expected");
                party.holdUntilTick = s.Now - 1;
                s.Beat();
                if (!party.isOut) s.Fail($"the hold was over and the party stayed in ({party.state})");
                // A raid (a hostile threat on the map): at once, held in an hour like the rest; the same hold with the threat gone.
                Trusties.ThreatForTests = true;
                party.holdUntilTick = -1;
                s.Beat();
                if (party.isOut || party.state != PartyState.Raid) s.Fail($"a hostile raid on the map did not call the party in at once (state {party.state})");
                Trusties.ThreatForTests = false;
                s.Beat();
                if (party.isOut || party.state != PartyState.Hold) s.Fail($"the party was sent out again at once after the raid (state {party.state}), the hold of an hour expected");
                party.holdUntilTick = s.Now - 1;
                s.Beat();
                if (!party.isOut) s.Fail($"the raid was over, the hold too, and the party stayed in ({party.state})");
                // The last holder's job taken (nothing to run: Idle) is no recall for a reason: the next holder is not held in for an hour.
                a.Inmate().job = null;
                b.Inmate().job = null;
                party.holdUntilTick = -1;
                s.Beat();
                if (party.state != PartyState.Idle || party.isOut || party.holdUntilTick >= 0) s.Fail($"nobody on the job: the party is {party.state} (out {party.isOut}), hold until {party.holdUntilTick} (Idle, in, no hold expected)");
                a.Inmate().job = RPTrustyDefOf.RP_JobOutside;
                b.Inmate().job = RPTrustyDefOf.RP_JobOutside;
                s.Beat();
                if (!party.isOut) s.Fail($"a trusty given the job after nobody held it was held in ({party.state}, hold until {party.holdUntilTick}): the hold is for a recall, not for a job changed");
                // A lockdown of the minimum wing, an evacuation (the minimum wing's is a state of the map), a seen escape, a hostage crisis.
                int lock0 = s.g.lockdownUntilTick;
                r.Add(() => s.g.lockdownUntilTick = lock0);
                s.g.lockdownUntilTick = s.Now + GenDate.TicksPerHour;
                s.Beat();
                if (party.isOut || party.state != PartyState.Lockdown) s.Fail($"a lockdown did not call the party in at once (state {party.state})");
                s.g.lockdownUntilTick = lock0;
                party.holdUntilTick = -1;
                s.Beat();
                // An evacuation of the minimum wing: at once.
                EvacuationState evac = s.pm.evac;
                (PrisonZoneDef evacWing0, EvacPhase evacPhase0) = (evac.wing, evac.phase);
                r.Add(() => (evac.wing, evac.phase) = (evacWing0, evacPhase0));
                (evac.wing, evac.phase) = (s.wingZone, EvacPhase.Out);
                s.Beat();
                if (party.isOut || party.state != PartyState.Evacuation) s.Fail($"an evacuation did not call the party in at once (state {party.state})");
                (evac.wing, evac.phase) = (evacWing0, evacPhase0);
                party.holdUntilTick = -1;
                s.Beat();
                // A hostage crisis (the arc running on this map): at once.
                var crisis = new StoryArc { def = RPHostageDefOf.RP_Arc_Hostage, pawns = new List<Pawn> { a } };
                s.g.story.arcs.Add(crisis);
                r.Add(() => s.g.story.arcs.Remove(crisis));
                s.Beat();
                if (party.isOut || party.state != PartyState.Hostage) s.Fail($"a hostage crisis did not call the party in at once (state {party.state})");
                s.g.story.arcs.Remove(crisis);
                party.holdUntilTick = -1;
                s.Beat();
                if (!party.isOut) s.Fail($"the evacuation and the hostage crisis were over, the hold set aside, and the party stayed in ({party.state})");
                Pawn runner = s.Inmate("Ztpt7", RPSecurityDefOf.RP_Medium, s.At(12, 7));
                Trusties.EscapeScope.Add(runner);
                runner.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Escaping, null, forced: true);
                s.Beat();
                if (party.isOut || party.state != PartyState.Escape) s.Fail($"an escape somebody has seen did not call the party in at once (state {party.state})");
                runner.MentalState?.RecoverFromState();
                party.holdUntilTick = -1;
                // The posts: with no guard near, an open post near the zone that nobody is on or on the way to is no supervision (the party stays in; the supervision check has the manned post).
                IntVec3 away = s.FarCell();
                if (!away.IsValid) { s.Fail("the check's own premise: no cell of the compound is out of the supervision's reach of the strip, so the guard cannot be put far from the zone"); TrustyStill(a, b, c3, c4, guard, rioter, runner); return; }
                s.Place(guard, away);
                party.holdUntilTick = -1;
                s.Beat();
                if (party.near != 0 || party.isOut) s.Fail($"with the guard far from the zone and no post the party is {party.state} (out {party.isOut}, near {party.near}): a clean baseline of none near and the party in was expected");
                Thing post = GuardPosts.Place(map, s.At(10, 2));
                if (post == null) s.Fail("the check's own fixture: no guard post could be placed beside the strip, so the posts are not judged");
                else
                {
                    s.r.Add(() => { if (post.Spawned) post.Destroy(DestroyMode.Vanish); });
                    party.holdUntilTick = -1;
                    s.Beat();
                    // (An open post with nobody on it or on the way is no supervision: nothing near, the party stays in, and the tab says it is the post.)
                    if (party.near != 0 || party.isOut) s.Fail($"an open guard post near the zone with nobody on it or on the way counts for going out ({party.state}, near {party.near}, out {party.isOut})");
                    if (Trusties.PostsNearZone(s.pm) < 1) s.Fail("the post near the zone is not counted");
                    if (party.state != PartyState.Short || party.postsNear != Trusties.PostsNearZone(s.pm)) s.Fail($"the tab's count of the posts near the zone ({party.postsNear}) is not the beat's ({Trusties.PostsNearZone(s.pm)}), or the state is {party.state} (Short expected)");
                    else if (Trusties.StateText(s.pm) != "RP_TrustyParty_Unmanned".Translate(party.postsNear, party.holders).Resolve()) s.Fail($"the line for a party kept in by a post nobody mans says '{Trusties.StateText(s.pm)}'");
                }
                // The zone erased: no strip in the open, the party is in.
                foreach (IntVec3 cell in new CellRect(s.origin.x, s.origin.z + 1, TrustyScene.Width, 4)) s.PaintCell(cell, null);
                s.Beat();
                if (party.isOut || party.state != PartyState.NoZone) s.Fail($"with the zone erased the party is {party.state}");
                TrustyStill(a, b, c3, c4, guard, rioter, runner);
            });
        }

        // ---- a stranded trusty ---------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The zone erased while a trusty is out on the strip: nothing opens the gate to them, so the party's beat finds them stranded and a free guard walks them in through the escort's own door pass (real ticks); no escape begins
        /// meanwhile (they stay covered by the exemption). Without it the trusty stood outside until the breach rule ran them off.
        /// </summary>
        private static void CheckTrustyStranded(Map map)
        {
            Guarded("trusties: a stranded trusty is walked in", map, r =>
            {
                const string check = "stranded";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Pawn guard = s.Guard("Ztst1", s.At(10, 2));
                Pawn t = s.Trusty("Ztst2", s.InZone, claim: s.bed);
                CompInmate c = t.Inmate();
                s.Beat();
                c.UpdateWorkPass();
                if (!c.outsidePass) { s.Fail("the check's own premise: the trusty has no pass: " + TrustyLine(s, t)); return; }
                // The strip erased and the gate's pass with it.
                foreach (IntVec3 cell in new CellRect(s.origin.x, s.origin.z + 1, TrustyScene.Width, 4)) s.PaintCell(cell, null);
                s.pm.Notify_DoorsChanged();
                s.pm.ForceDoorUpdate();
                map.reachability.ClearCache();
                s.Beat();
                bool reach = t.CanReach(s.InCell, PathEndMode.Touch, Danger.Deadly);
                if (reach) { s.Fail("the check's own premise: with the zone erased the trusty can still reach their bed"); TrustyStill(t, guard); return; }
                if (!Trusties.Stranded(t)) { s.Fail($"the beat did not find a trusty who cannot reach their bed: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                // The count does not write them up for being out of place: they cannot get back by themselves (the work slot expects the cell while the party is in).
                bool partyOut = s.pm.party.isOut;
                s.pm.party.isOut = false;
                if (!RegimePlacement.InExpectedPlace(t, RPLaborDefOf.RP_Work, s.pm)) s.Fail("a stranded trusty out on the strip is out of place for the count (they wait for a guard)");
                s.pm.party.isOut = partyOut;
                TrustyEscortHome(s, guard, t, () => t.GetRoom() == InmateUtility.CellOf(t), "a stranded trusty");
                TrustyStill(t, guard);
                // One with no bed to go to: a guard walks them to their wing (the strip is still erased, the gate shut to them).
                Pawn u = s.Trusty("Ztst3", s.At(12, 3));
                CompInmate uc = u.Inmate();
                uc.outsidePass = true;
                uc.workPass = RPTrustyDefOf.RP_OutsideWork;
                s.Beat();
                if (!Trusties.Stranded(u)) s.Fail($"the beat did not find a stranded holder who has no bed: {TrustyLine(s, u)}");
                else TrustyEscortHome(s, guard, u, () => !s.pm.IsOutsideSpace(u.Position), "a stranded holder with no bed");
                TrustyStill(u, guard);
            });
        }

        /// <summary>
        /// A free guard is offered the walk home for one who is stranded, and walks them in by real ticks, no escape beginning meanwhile; <paramref name="home"/> says when they are in.
        /// </summary>
        private static void TrustyEscortHome(TrustyScene s, Pawn guard, Pawn t, Func<bool> home, string what)
        {
            InmateCandidates.Shared.Invalidate();
            var giver = new WorkGiver_EscortInmate();
            if (!giver.HasJobOnThing(guard, t)) { s.Fail($"{what}: a free guard is not offered the walk (candidate {SupermaxSystem.Candidate(t)}): {TrustyLine(s, t)}"); return; }
            Job job = giver.JobOnThing(guard, t);
            if (job == null || (EscortKind)job.count != EscortKind.Home) { s.Fail($"{what}: the walk offered is {job?.def.defName} kind {(job != null ? ((EscortKind)job.count).ToString() : "none")}, the walk home expected"); return; }
            guard.jobs.StartJob(job, JobCondition.InterruptForced);
            bool escaped = false;
            int ticked = TrustyTicks(() =>
            {
                if (t.MentalStateDef == RPMentalStateDefOf.RP_Escaping) escaped = true;
                return escaped || home();
            }, 5000);
            if (escaped) s.Fail($"{what} began an escape while they waited for the guard");
            else if (!home()) s.Fail($"{what}: the guard did not walk them home in {ticked} ticks: {TrustyLine(s, t)}, guard on {guard.CurJobDef?.defName}, leg {t.Inmate().leg?.phase.ToString() ?? "none"}");
        }

        // ---- the zone's rules ----------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The outside work zone cannot be painted in a cell inside the walls (its district does not touch the map's edge); a new arrival on the strip is not inside the prison.</summary>
        private static void CheckTrustyZoneRules(Map map)
        {
            Guarded("trusties: where the zone can be painted, and what is inside", map, r =>
            {
                const string check = "zone";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                var designator = new Designator_PrisonZone(RPTrustyDefOf.RP_OutsideWork);
                if (Find.CurrentMap == map)
                {
                    // (The strip is painted already: a cell of it is refused as "already this zone", so the painting is tried on the bare ground beside it, through the designator's own path.)
                    IntVec3 bare = s.OffZoneNear;
                    if (s.pm.zones.ZoneAt(bare) != null) s.Fail($"the check's own premise: the bare ground beside the strip is painted ({s.pm.zones.ZoneAt(bare).defName})");
                    else
                    {
                        AcceptanceReport open = designator.CanDesignateCell(bare);
                        if (!open.Accepted) s.Fail($"the designator refuses open ground beyond the wall for the outside zone: {open.Reason}");
                        else
                        {
                            r.Add(() => s.pm.zones.Set(bare, null));
                            designator.DesignateMultiCell(new[] { bare });
                            if (s.pm.zones.ZoneAt(bare) != RPTrustyDefOf.RP_OutsideWork) s.Fail($"the designator accepted open ground but painted {s.pm.zones.ZoneAt(bare)?.defName ?? "nothing"} there");
                        }
                    }
                    if (s.pm.zones.ZoneAt(s.Corridor) == RPTrustyDefOf.RP_OutsideWork) s.Fail("the check's own premise: the corridor is painted as the outside zone");
                    AcceptanceReport inside = designator.CanDesignateCell(s.Corridor);
                    if (inside.Accepted) s.Fail("the outside work zone can be painted in the corridor inside the walls");
                    else if (inside.Reason != "RP_OutsideWorkNotOutside".Translate().ToString()) s.Fail($"the corridor was refused for another reason than being inside the walls: {inside.Reason}");
                    // The control: another zone is accepted in the same corridor, so the refusal above is the outside zone's rule.
                    PrisonZoneDef other = DefDatabase<PrisonZoneDef>.AllDefsListForReading.FirstOrDefault(z => !z.outsideWork && z != s.pm.zones.ZoneAt(s.Corridor));
                    if (other != null && !new Designator_PrisonZone(other).CanDesignateCell(s.Corridor).Accepted) s.Fail($"the control: the {other.defName} zone is refused in the corridor too");
                }
                else RPLog.Message("[RimPrison:test] trusties zone: the checked map is not the one on screen, the designator is not asked");
                Pawn arrival = s.Inmate("Ztzn1", RPTrustyDefOf.RP_Minimum, s.InZone);
                arrival.Inmate().processed = false;
                if (ShuttleSystem.IsInside(arrival)) s.Fail("a new arrival standing on the outside work strip counts as inside the prison");
                s.Place(arrival, s.Corridor);
                if (!ShuttleSystem.IsInside(arrival)) s.Fail("the control: a new arrival in the wing is not inside the prison");
                arrival.Inmate().processed = true;
                TrustyStill(arrival);
            });
        }

        // ---- the saveload plan ------------------------------------------------------------------------------------------------------------------------------------------------

        private static int trustySeedId = -1, trustyMentalSeedId = -1;
        private static string trustySeedState;
        private static TrustyParty trustyParty0;

        /// <summary>Every inmate with the status or the pass, and the party's saved fields, on one line (the saveload plan compares it across a round trip).</summary>
        private static string TrustyDescribe(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            string inmates = string.Join(",", map.mapPawns.PrisonersOfColony.Where(p => p.Inmate() is { } c && (c.IsTrusty || c.outsidePass)).OrderBy(p => p.thingIDNumber).Select(p => $"{p.thingIDNumber}:{p.Inmate().trustySinceTick}:{p.Inmate().outsidePass}:{p.Inmate().job?.defName}"));
            return $"{inmates}/{pm?.party.ratio}:{pm?.party.hours}:{pm?.party.holdUntilTick}:{pm?.party.isOut}:{pm?.party.shortSinceTick}";
        }

        /// <summary>An inmate made a trusty with the pass, and a party off its defaults (the ratio, a day's hours, a hold), for the round trip. <see cref="CheckTrustiesAfterReload"/> puts them back.</summary>
        private static void SeedTrustiesForReload(Map map)
        {
            trustySeedId = trustyMentalSeedId = -1;
            PrisonMap pm = PrisonMap.Of(map);
            Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true, trustySinceTick: < 0, outsidePass: false });
            if (p == null || pm == null)
            {
                reloadProblems.Add("saveload: no inmate to seed the trusty status on");
                return;
            }
            trustyParty0 = new TrustyParty { ratio = pm.party.ratio, hours = pm.party.hours, holdUntilTick = pm.party.holdUntilTick, isOut = pm.party.isOut, shortSinceTick = pm.party.shortSinceTick };
            CompInmate c = p.Inmate();
            int now = Find.TickManager.TicksGame;
            c.trustySinceTick = Math.Max(1, now - 4321);
            c.outsidePass = true;
            (pm.party.ratio, pm.party.hours, pm.party.holdUntilTick) = (Trusties.MinRatio, 7, now + 3333);
            // (Out, and a shortfall's hour of grace begun: both are read back, so a load in the middle of it goes on with it.)
            (pm.party.isOut, pm.party.shortSinceTick) = (!trustyParty0.isOut, now - 777);
            trustySeedId = p.thingIDNumber;
            RPLog.Message($"[RimPrison:test] saveload trusties: {p.LabelShort} is a trusty since {c.trustySinceTick} with the pass, the party at {pm.party.ratio}/{pm.party.hours}/{pm.party.holdUntilTick}, out {pm.party.isOut}, short since {pm.party.shortSinceTick}");
            // A second holder of the token in a mental break (not an escape): the token is saved, the pass is shut during the break, and a load puts the break back without starting it, so no start clears the pass: it must not come
            // back with the load. (Built or failed: the plan has no seeded pass that a break shuts if no such inmate can be made.)
            Pawn q = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x != p && x.Inmate() is { activeFlag: true, trustySinceTick: < 0, outsidePass: false } && !x.InMentalState && !x.Downed);
            if (q == null)
            {
                reloadProblems.Add("saveload: no second inmate to seed a trusty in a mental break on");
                return;
            }
            CompInmate qc = q.Inmate();
            qc.trustySinceTick = Math.Max(1, now - 1234);
            qc.outsidePass = true;
            qc.workPass = RPTrustyDefOf.RP_OutsideWork;
            if (!q.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true, transitionSilently: true))
            {
                reloadProblems.Add($"saveload: a mental break could not be started on {q.LabelShort} for the trusty with the token");
                (qc.trustySinceTick, qc.outsidePass, qc.workPass) = (-1, false, null);
                return;
            }
            if (qc.workPass != null) reloadProblems.Add("saveload: the start of a mental break did not take the pass of the trusty outside (the seed's premise)");
            trustyMentalSeedId = q.thingIDNumber;
            RPLog.Message($"[RimPrison:test] saveload trusties: {q.LabelShort} holds the token in a mental break ({q.MentalStateDef?.defName}), the pass shut");
        }

        /// <summary>The seeded state as it is at the snapshot.</summary>
        private static void RecordTrustiesForReload(Map map) => trustySeedState = TrustyDescribe(map);

        private static void CheckTrustiesAfterReload(Map map)
        {
            if (trustySeedId < 0) return;
            int id = trustySeedId;
            trustySeedId = -1;
            PrisonMap pm = PrisonMap.Of(map);
            Pawn p = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == id);
            if (p?.Inmate() is not { } c || pm == null)
            {
                reloadProblems.Add("saveload: the inmate with the trusty status did not survive the load");
                return;
            }
            string after = TrustyDescribe(map);
            if (after != trustySeedState) reloadProblems.Add($"saveload: the trusty status and the party changed across the load ({trustySeedState} -> {after})");
            else if (c.workPass != RPTrustyDefOf.RP_OutsideWork) reloadProblems.Add("saveload: a trusty who held the pass at the save did not hold it at the load");
            else RPLog.Message("[RimPrison:test] saveload trusties: intact");
            c.trustySinceTick = -1;
            c.outsidePass = false;
            c.workPass = null;
            // The holder in a break: still in it, still holding the token, the pass still shut (no look has had the chance to give it back); then put as they were.
            if (trustyMentalSeedId >= 0)
            {
                int qid = trustyMentalSeedId;
                trustyMentalSeedId = -1;
                Pawn q = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == qid);
                if (q?.Inmate() is not { } qc) reloadProblems.Add("saveload: the inmate with the token in a mental break did not survive the load");
                else
                {
                    if (!q.InMentalState) reloadProblems.Add("saveload: the seeded mental break did not come back with the load (the case of a pass given back during a break is not judged)");
                    else if (!qc.outsidePass || qc.trustySinceTick < 0) reloadProblems.Add($"saveload: the trusty in a mental break lost the token ({qc.outsidePass}) or the status ({qc.trustySinceTick}) across the load");
                    else if (qc.workPass != null || Trusties.HoldsPass(q, qc)) reloadProblems.Add($"saveload: the load gave the pass to a trusty who is still in a mental break (pass {qc.workPass?.defName ?? "none"}, holds {Trusties.HoldsPass(q, qc)})");
                    else RPLog.Message("[RimPrison:test] saveload trusties: the token came back, the pass stayed shut through the mental break");
                    q.MentalState?.RecoverFromState();
                    (qc.trustySinceTick, qc.outsidePass, qc.workPass) = (-1, false, null);
                }
            }
            if (trustyParty0 != null) (pm.party.ratio, pm.party.hours, pm.party.holdUntilTick, pm.party.isOut, pm.party.shortSinceTick) = (trustyParty0.ratio, trustyParty0.hours, trustyParty0.holdUntilTick, trustyParty0.isOut, trustyParty0.shortSinceTick);
        }
    }

    /// <summary>The cells over which a check holds the growing season on and the cold off the plants (see <c>AutoTest.TrustyHoldSeason</c>); no map, nothing held.</summary>
    internal static class TrustySeason
    {
        internal static Map Map;
        internal static CellRect Cells;

        internal static bool Holds(IntVec3 c, Map map) => Map != null && map == Map && Cells.Contains(c);
    }

    /// <summary>The growing season is on over the cells a grower check holds (see <see cref="TrustySeason"/>), for the sowing's own test, the plant's growth rate and the zone's; everywhere else the game's own answer stands.</summary>
    [HarmonyPatch(typeof(PlantUtility), nameof(PlantUtility.GrowthSeasonNow), new[] { typeof(IntVec3), typeof(Map), typeof(ThingDef) })]
    [TestOnly]
    public static class Patch_PlantUtility_GrowthSeasonNow_TrustySeason
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            if (!TrustySeason.Holds(c, map)) return true;
            __result = true;
            return false;
        }
    }

    /// <summary>The plants on the cells a grower check holds (see <see cref="TrustySeason"/>) do not die of the cold, on the spawn or on the long tick; everywhere else the game's own check stands.</summary>
    [HarmonyPatch(typeof(Plant), "CheckMakeLeafless")]
    [TestOnly]
    public static class Patch_Plant_CheckMakeLeafless_TrustySeason
    {
        public static bool Prefix(Plant __instance) => !(__instance.Spawned && TrustySeason.Holds(__instance.Position, __instance.Map));
    }
}
