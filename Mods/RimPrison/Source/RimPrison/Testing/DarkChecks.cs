using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The one seen rule and the dark (<see cref="TensionUtility.Watched"/>, <see cref="TensionUtility.Dark"/>): every caller reads the same answer; in the dark (ground glow under 0.3 in a free-time zone or a corridor) a guard sees
    /// only 3 cells and a camera nothing; the coverage overlay and the danger list say so; the builder's lamps keep its free-time rooms lit. Self-contained, on throwaway inmates and guards, glow forced where a check needs a cell dark
    /// or lit (<see cref="TensionUtility.TestGlow"/>), rolled back.
    /// </summary>
    public static partial class AutoTest
    {
        private static void DarkFail(string what) => problems0.Add("dark: " + what);

        /// <summary>A powered camera on the spot, taken down again by the rollback (it registers itself with the prison's cameras when it spawns).</summary>
        private static CompSecurityCamera SpawnDarkCamera(Map map, Rollback r, IntVec3 spot)
        {
            var building = (Building)ThingMaker.MakeThing(RPDefOf.RP_SecurityCamera);
            building.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(building, spot, map);
            r.Add(() =>
            {
                if (!building.Destroyed) building.Destroy(DestroyMode.Vanish);
            });
            if (building.GetComp<CompPowerTrader>() is { } power) power.PowerOn = true;
            return building.GetComp<CompSecurityCamera>();
        }

        private static void CheckDark(Map map)
        {
            CheckDarkOneSeenRule(map);
            CheckDarkRule(map);
            CheckDarkDoorway(map);
            CheckDarkTower(map);
            CheckDarkOverlay(map);
            CheckDarkDangerList(map);
            CheckDarkBuilderLamps(map);
            CheckDarkRealGlow(map);
        }

        /// <summary>An open spot in a free-time zone with an inmate on it, a guard two cells off, a second guard six off, a camera spot five off (all in sight of it), and the prison's own watchers put aside until the check is done.</summary>
        private sealed class DarkScene
        {
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly Pawn inmate, nearGuard, farGuard;
            public readonly IntVec3 cell, nearCell, farCell;
            public readonly CompSecurityCamera camera;
            public readonly MisconductDef quiet;

            private static readonly (int x, int z)[] Near = { (2, 0), (-2, 0), (0, 2), (0, -2), (2, 1), (-2, -1), (1, 2), (-1, -2) };
            private static readonly (int x, int z)[] Far = { (6, 0), (-6, 0), (0, 6), (0, -6), (6, 2), (-6, -2), (2, 6), (-2, -6) };
            private static readonly (int x, int z)[] Cam = { (0, 5), (0, -5), (5, 0), (-5, 0), (4, 3), (-4, -3), (3, 4), (-3, -4) };

            public static bool Free(Map map, IntVec3 c) => c.InBounds(map) && c.Standable(map) && !c.GetThingList(map).Any(t => t.def.category == ThingCategory.Building);

            /// <summary>Null (and a problem told) when the map has no such open spot.</summary>
            public static DarkScene TryMake(Map map, Rollback r)
            {
                PrisonMap pm = PrisonMap.Of(map);
                foreach (PrisonZoneDef zone in new[] { RPDefOf.RP_Yard, RPDefOf.RP_CommonRoom, RPDefOf.RP_Canteen })
                {
                    int tried = 0;
                    foreach (IntVec3 c in pm.zones.CellsOf(zone))
                    {
                        if (++tried > 800) break;
                        if (!Free(map, c) || !TensionUtility.DarkMatters(c, map, pm)) continue;
                        IntVec3 Pick((int x, int z)[] offsets, params IntVec3[] not)
                        {
                            foreach ((int x, int z) in offsets)
                            {
                                IntVec3 o = c + new IntVec3(x, 0, z);
                                if (Free(map, o) && !not.Contains(o) && GenSight.LineOfSight(o, c, map, skipFirstCell: true) && pm.zones.ZoneAt(o) == zone) return o;
                            }
                            return IntVec3.Invalid;
                        }
                        IntVec3 near = Pick(Near), far = Pick(Far, near), cam = Pick(Cam, near, far);
                        if (near.IsValid && far.IsValid && cam.IsValid) return new DarkScene(map, pm, r, c, near, far, cam);
                    }
                }
                DarkFail("the check's own premise: no open spot in a yard, common room or canteen with room for a guard close, a guard further off and a camera");
                return null;
            }

            private DarkScene(Map map, PrisonMap pm, Rollback r, IntVec3 cell, IntVec3 near, IntVec3 far, IntVec3 camSpot)
            {
                this.map = map;
                this.pm = pm;
                this.cell = cell;
                nearCell = near;
                farCell = far;
                // (What watches the prison is put aside, and put back last: the scene's own guards and camera are the only watchers.)
                List<Pawn> guards0 = pm.Guards.ToList();
                List<CompSecurityCamera> cameras0 = pm.Cameras.ToList();
                List<CompGuardTower> towers0 = pm.Towers.ToList();
                r.Add(() =>
                {
                    pm.Guards.Clear();
                    pm.Guards.AddRange(guards0);
                    pm.Cameras.Clear();
                    pm.Cameras.AddRange(cameras0);
                    pm.Towers.Clear();
                    pm.Towers.AddRange(towers0);
                });
                pm.Guards.Clear();
                pm.Cameras.Clear();
                pm.Towers.Clear();
                TensionUtility.TestGlow = new Dictionary<IntVec3, float>();
                r.Add(() => TensionUtility.TestGlow = null);
                inmate = Throwaway(map, r);
                (nearGuard, farGuard) = (ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard));
                Place(inmate, cell);
                Place(nearGuard, near);
                Place(farGuard, far);
                camera = SpawnDarkCamera(map, r, camSpot);
                pm.Cameras.Clear();
                quiet = DefDatabase<MisconductDef>.AllDefsListForReading.First(d => !d.loud && d.danger == DangerKind.None);
            }

            private static void Place(Pawn p, IntVec3 c)
            {
                p.pather?.StopDead();
                p.Position = c;
                p.Notify_Teleported();
            }

            /// <summary>Who watches now: any of the two guards, the camera.</summary>
            public void Watch(bool near = false, bool far = false, bool cam = false)
            {
                pm.Guards.Clear();
                if (near) pm.Guards.Add(nearGuard);
                if (far) pm.Guards.Add(farGuard);
                pm.Cameras.Clear();
                if (cam) pm.Cameras.Add(camera);
            }

            public void Glow(float glow, params IntVec3[] cells)
            {
                foreach (IntVec3 c in cells.Length == 0 ? new[] { cell } : cells) TensionUtility.TestGlow[c] = glow;
            }

            /// <summary>Every reading of "seen" there is for the inmate on the spot: the pawn's watched, the count over nothing, the cell's, and what a misconduct there is recorded as.</summary>
            public (bool watched, bool count, bool atCell, bool recorded) Read()
            {
                CompInmate c = inmate.Inmate();
                MisconductUtility.Record(inmate, c, pm, quiet, chronicle: false);
                return (TensionUtility.Watched(inmate, pm), TensionUtility.Watchers(inmate, pm) > 0f, TensionUtility.SeenAt(cell, map, pm), c.records[c.records.Count - 1].seen);
            }
        }

        /// <summary>The seen rule has one answer: for a few cells (nobody, a guard near, a camera, both) the pawn's watched, the watchers count, the cell's and the record all agree, and the answer is what a guard or a working camera sees.</summary>
        private static void CheckDarkOneSeenRule(Map map)
        {
            Guarded("dark: one seen rule", map, r =>
            {
                DarkScene s = DarkScene.TryMake(map, r);
                if (s == null) return;
                s.Glow(1f);
                void Expect(string what, bool seen)
                {
                    (bool watched, bool count, bool atCell, bool recorded) = s.Read();
                    if (watched != seen || count != seen || atCell != seen || recorded != seen)
                        DarkFail($"{what}: seen should be {seen}, the pawn's watched says {watched}, the count {count}, the cell {atCell}, the record {recorded} (they must be one rule)");
                }
                s.Watch();
                Expect("nobody watching", false);
                s.Watch(near: true);
                Expect("a guard two cells off", true);
                // A single working camera is a watcher however little (it counts half; the count over nothing was the rule the record used, and watched wanted two of them).
                s.Watch(cam: true);
                Expect("a camera only", true);
                s.Watch(near: true, cam: true);
                Expect("a guard and a camera", true);
                s.Watch(far: true);
                Expect("a guard six cells off in a lit place", true);
                // A camera that is off (no power) sees nothing: the same for every reading.
                s.Watch(cam: true);
                if (s.camera.parent.GetComp<CompPowerTrader>() is { } power)
                {
                    power.PowerOn = false;
                    Expect("a camera with no power", false);
                    power.PowerOn = true;
                }
                // Several cells of the prison: the pawn's and the cell's readings never differ, whatever watches.
                PrisonMap pm = s.pm;
                int n = 0;
                foreach (IntVec3 c in pm.zones.CellsOf(RPDefOf.RP_Yard).Take(60))
                {
                    if (++n % 7 != 0 || !c.Standable(map)) continue;
                    s.inmate.Position = c;
                    s.inmate.Notify_Teleported();
                    if (TensionUtility.Watched(s.inmate, pm) != TensionUtility.SeenAt(c, map, pm) || TensionUtility.Watched(s.inmate, pm) != TensionUtility.Watchers(s.inmate, pm) > 0f)
                        DarkFail($"at {c} the pawn's watched ({TensionUtility.Watched(s.inmate, pm)}), the cell's ({TensionUtility.SeenAt(c, map, pm)}) and the count ({TensionUtility.Watchers(s.inmate, pm)}) differ");
                }
            });
        }

        /// <summary>The dark: a free-time cell under 0.3 glow is seen by a guard within 3 cells and not by a camera or a guard further off; lit again, as before; a prison cell is not one of these; and the misconduct chance is the same after the real hourly update, lit or dark.</summary>
        private static void CheckDarkRule(Map map)
        {
            Guarded("dark: the dark rule", map, r =>
            {
                DarkScene s = DarkScene.TryMake(map, r);
                if (s == null) return;
                CompInmate inmate = s.inmate.Inmate();
                s.Glow(0f);
                if (!TensionUtility.Dark(s.cell, map, s.pm)) DarkFail($"the free-time cell {s.cell} with no glow is not dark");
                void Expect(string what, bool seen, float count)
                {
                    (bool watched, bool c, bool atCell, bool recorded) = s.Read();
                    float watchers = TensionUtility.Watchers(s.inmate, s.pm);
                    if (watched != seen || c != seen || atCell != seen || recorded != seen || !Mathf.Approximately(watchers, count))
                        DarkFail($"{what}: seen should be {seen} with {count} watchers, it is watched {watched}, count {c}, cell {atCell}, record {recorded}, {watchers} watchers");
                }
                s.Watch(cam: true);
                Expect("in the dark with a camera over the spot and no guard within 3 cells", false, 0f);
                // (The court's and the complaints' own reading of a camera over a spot is the same: in the dark it is no evidence.)
                if (CourtUtil.CameraOver(map, s.cell)) DarkFail("a camera over a dark spot is evidence for a trial or a complaint");
                s.Watch(far: true);
                Expect("in the dark with a guard six cells off", false, 0f);
                s.Watch(near: true);
                Expect("in the dark with a guard two cells off", true, 1f);
                s.Watch(near: true, far: true, cam: true);
                Expect("in the dark with a guard close, one further off and the camera: only the close one", true, 1f);
                if (TensionUtility.GuardsNear(s.inmate, s.pm) != 1) DarkFail($"in the dark, {TensionUtility.GuardsNear(s.inmate, s.pm)} guards are in view, only the one two cells off should be");
                // A lamp lit there: lit again, as it was.
                s.Glow(1f);
                s.Watch(cam: true);
                Expect("lit, with the camera over the spot", true, 0.5f);
                if (!CourtUtil.CameraOver(map, s.cell)) DarkFail("a camera over a lit spot is not evidence for a trial or a complaint");
                s.Watch(far: true);
                Expect("lit, with a guard six cells off", true, 1f);
                if (TensionUtility.Dark(s.cell, map, s.pm)) DarkFail("a lit cell is dark");
                // Just over the line is lit, just under is dark.
                s.Glow(TensionUtility.DarkGlow);
                if (TensionUtility.Dark(s.cell, map, s.pm)) DarkFail($"a cell at glow {TensionUtility.DarkGlow} is dark (the line is under it)");
                s.Glow(TensionUtility.DarkGlow - 0.01f);
                if (!TensionUtility.Dark(s.cell, map, s.pm)) DarkFail($"a cell at glow {TensionUtility.DarkGlow - 0.01f} is not dark");
                // The dark rule is about being seen, not about misconduct odds: the real hourly update (suppression is taken from it) on the same inmate, the same tension, with the cell lit and then dark, leaves the same
                // suppression and the same chance. A guard six cells off and the camera are watchers only while it is lit (1.5 of them), so a dark cell that cost suppression would show here.
                (float suppression, float chance) AfterAnHour(float glow)
                {
                    s.Glow(glow);
                    s.Watch(far: true, cam: true);
                    inmate.tension = 50f;
                    inmate.suppressed = 0f;
                    TensionUtility.HourlyUpdate(s.inmate, inmate, s.pm);
                    return (inmate.suppression, TensionUtility.MisconductChancePerHour(inmate));
                }
                (float litSuppression, float litChance) = AfterAnHour(1f);
                (float darkSuppression, float darkChance) = AfterAnHour(0f);
                if (litSuppression <= 0f) DarkFail($"the check's own premise: the lit hour left suppression {litSuppression} with a guard in sight and a camera");
                if (!Mathf.Approximately(litSuppression, darkSuppression) || !Mathf.Approximately(litChance, darkChance))
                    DarkFail($"the dark changed the odds: after an hour lit, suppression {litSuppression} and chance {litChance}, after an hour dark, {darkSuppression} and {darkChance} (suppression counts the watchers as if the cell were lit)");
                // Not these cells: a prison cell (its room), a staff-only room, ground outside the zones.
                s.Glow(0f);
                s.Watch(cam: true);
                Building_Bed bed = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().FirstOrDefault(b => b.ForPrisoners && !b.Medical && b.GetRoom() is { IsPrisonCell: true });
                if (bed == null) DarkFail("the check's own premise: no prisoner bed in a cell");
                else
                {
                    s.Glow(0f, bed.Position);
                    if (TensionUtility.Dark(bed.Position, map, s.pm)) DarkFail($"a prison cell at {bed.Position} is dark by this rule: a cell keeps the old one");
                    // (A camera in the cell sees the bed in the dark as it always did.)
                    IntVec3 spot = GenRadial.RadialCellsAround(bed.Position, 4f, false).FirstOrDefault(x => x.InBounds(map) && x.Standable(map) && x.GetRoom(map) == bed.GetRoom() && !x.GetThingList(map).Any(t => t.def.category == ThingCategory.Building)
                        && GenSight.LineOfSight(x, bed.Position, map, skipFirstCell: true));
                    if (spot == default) RPLog.Message("[RimPrison:test] dark: no free spot in the cell for a camera, the camera over a dark cell is not judged");
                    else
                    {
                        CompSecurityCamera cellCamera = SpawnDarkCamera(map, r, spot);
                        s.pm.Cameras.Clear();
                        s.pm.Cameras.Add(cellCamera);
                        if (!TensionUtility.SeenAt(bed.Position, map, s.pm)) DarkFail("a camera in a dark prison cell does not see the bed: the old rule (a camera sees it) holds there");
                        s.pm.Cameras.Clear();
                    }
                }
                IntVec3 staff = s.pm.zones.CellsOf(DefDatabase<PrisonZoneDef>.GetNamed("RP_StaffOnly")).FirstOrDefault(x => x.Standable(map));
                if (staff != default)
                {
                    s.Glow(0f, staff);
                    if (TensionUtility.Dark(staff, map, s.pm)) DarkFail("a staff-only room is dark by this rule");
                }
                IntVec3 outside = new IntVec3(5, 0, 5);
                s.Glow(0f, outside);
                if (s.pm.zones.ZoneAt(outside) == null && TensionUtility.Dark(outside, map, s.pm)) DarkFail("open ground outside every zone is dark by this rule");
            });
        }

        /// <summary>
        /// A doorway is never dark: a door blocks light, so vanilla gives its cell no glow at all, by day too (forced here to nought, what the glow grid says of it). An inmate standing in the doorway of a corridor or a free-time room under
        /// a working camera is seen, as one in the room beside it. Told, not failed, when the prison has no such door with a spot for a camera that sees it.
        /// </summary>
        private static void CheckDarkDoorway(Map map)
        {
            Guarded("dark: a doorway", map, r =>
            {
                DarkScene s = DarkScene.TryMake(map, r);
                if (s == null) return;
                int doors = 0;
                foreach (Building_Door door in map.listerBuildings.allBuildingsColonist.OfType<Building_Door>())
                {
                    IntVec3 d = door.Position;
                    if (!TensionUtility.DarkMatters(d, map, s.pm)) continue;
                    doors++;
                    IntVec3 spot = GenRadial.RadialCellsAround(d, 5f, false).FirstOrDefault(x => DarkScene.Free(map, x) && GenSight.LineOfSight(x, d, map, skipFirstCell: true));
                    if (!spot.IsValid || spot == default) continue;
                    CompSecurityCamera camera = SpawnDarkCamera(map, r, spot);
                    s.pm.Guards.Clear();
                    s.pm.Cameras.Clear();
                    s.pm.Cameras.Add(camera);
                    s.Glow(1f, d);
                    if (!camera.Covers(d))
                    {
                        // (A closed door can be what stops the sight line to its own cell: this one is not judged.)
                        camera.parent.Destroy(DestroyMode.Vanish);
                        continue;
                    }
                    s.inmate.Position = d;
                    s.inmate.Notify_Teleported();
                    s.Glow(0f, d);
                    if (TensionUtility.Dark(d, map, s.pm)) DarkFail($"the doorway {d} with no glow is dark: a door blocks light, so its cell has none and an inmate in it would be unseen");
                    else if (!TensionUtility.Watched(s.inmate, s.pm) || !TensionUtility.SeenAt(d, map, s.pm)) DarkFail($"an inmate in the doorway {d} under a working camera by day is not seen");
                    return;
                }
                RPLog.Message($"[RimPrison:test] dark: no door of a corridor or free-time room with a spot for a camera that sees it ({doors} doors where the rule applies), the doorway is not judged");
            });
        }

        /// <summary>A manned tower has a searchlight: it sees its whole radius in the dark as by day (a guard on foot and a camera are the ones the dark rule changes).</summary>
        private static void CheckDarkTower(Map map)
        {
            Guarded("dark: a manned tower", map, r =>
            {
                DarkScene s = DarkScene.TryMake(map, r);
                if (s == null) return;
                ThingDef def = RPArmedDefOf.RP_GuardTower;
                IntVec3 at = IntVec3.Invalid;
                foreach (IntVec3 c in GenRadial.RadialCellsAround(s.cell, 9f, true))
                {
                    if (c.DistanceTo(s.cell) < 6f || !GenAdj.OccupiedRect(c, Rot4.North, def.size).Cells.All(x => x.InBounds(map) && x.Standable(map) && x.GetEdifice(map) == null && x.GetFirstItem(map) == null && x.GetFirstPawn(map) == null)) continue;
                    at = c;
                    break;
                }
                if (!at.IsValid)
                {
                    RPLog.Message("[RimPrison:test] dark: no open ground for a tower six cells from the spot, the tower is not judged");
                    return;
                }
                Thing tower = GenSpawn.Spawn(ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null), at, map);
                tower.SetFaction(Faction.OfPlayer);
                r.Add(() =>
                {
                    if (!tower.Destroyed) tower.Destroy();
                });
                var comp = ((ThingWithComps)tower).GetComp<CompGuardTower>();
                comp.mannedUntil = Find.TickManager.TicksGame + 5000;
                s.pm.Towers.Clear();
                s.pm.Towers.Add(comp);
                s.Watch();
                IntVec3 close = GenRadial.RadialCellsAround(tower.Position, 3f, false).FirstOrDefault(x => x.InBounds(map) && x.Standable(map) && !tower.OccupiedRect().Contains(x) && TensionUtility.DarkMatters(x, map, s.pm));
                // (A tower has a searchlight: it sees its radius in the dark as by day.)
                s.Glow(0f);
                if (!TensionUtility.SeenAt(s.cell, map, s.pm)) DarkFail($"a manned tower {tower.Position.DistanceTo(s.cell):0} cells off does not see a dark cell (its searchlight)");
                s.Glow(1f);
                if (!TensionUtility.SeenAt(s.cell, map, s.pm)) DarkFail($"a manned tower {tower.Position.DistanceTo(s.cell):0} cells off does not see a lit cell (lit is as before)");
                if (close.IsValid)
                {
                    s.Glow(0f, close);
                    if (!TensionUtility.SeenAt(close, map, s.pm)) DarkFail("a manned tower does not see a dark cell beside it");
                }
                else RPLog.Message("[RimPrison:test] dark: no free-time cell beside the tower, the near side is not judged");
            });
        }

        /// <summary>The coverage overlay shades a dark cell of a free-time zone (and counts it as the tension model does), and not a lit one.</summary>
        private static void CheckDarkOverlay(Map map)
        {
            Guarded("dark: the coverage overlay", map, r =>
            {
                DarkScene s = DarkScene.TryMake(map, r);
                if (s == null) return;
                CoverageOverlay overlay = s.pm.Coverage;
                // (The overlay is built again from the prison as it was once the check is done.)
                r.Add(() => overlay.Rebuild());
                s.Glow(0f, s.cell, s.nearCell, s.farCell);
                s.Watch(near: true, cam: true);
                overlay.Rebuild();
                if (!overlay.DarkAt(s.cell)) DarkFail($"the coverage overlay does not shade the dark cell {s.cell}");
                if (!Mathf.Approximately(overlay.At(s.cell), 1f)) DarkFail($"the overlay counts {overlay.At(s.cell)} watchers on a dark cell with a guard two cells off and a camera, it should be 1");
                s.Watch(cam: true);
                overlay.Rebuild();
                if (overlay.At(s.cell) > 0f) DarkFail($"the overlay counts {overlay.At(s.cell)} watchers on a dark cell with only a camera over it");
                s.Watch(far: true);
                overlay.Rebuild();
                // (The far guard's own cell counts them, as it should: only the dark spot six cells off is asked.)
                if (overlay.At(s.cell) > 0f) DarkFail("the overlay counts a guard six cells off on a dark cell");
                s.Glow(1f, s.cell, s.nearCell, s.farCell);
                s.Watch(cam: true);
                overlay.Rebuild();
                if (overlay.DarkAt(s.cell)) DarkFail("the coverage overlay shades a lit cell");
                if (!Mathf.Approximately(overlay.At(s.cell), 0.5f)) DarkFail($"the overlay counts {overlay.At(s.cell)} watchers on a lit cell with a camera over it, it should be 0.5");
                // The overlay and the count agree for the inmate on the spot, dark or lit.
                s.Glow(0f);
                s.Watch(near: true, far: true, cam: true);
                overlay.Rebuild();
                if (!Mathf.Approximately(overlay.At(s.cell), TensionUtility.Watchers(s.inmate, s.pm))) DarkFail($"the overlay ({overlay.At(s.cell)}) and the count ({TensionUtility.Watchers(s.inmate, s.pm)}) differ on a dark cell");
            });
        }

        /// <summary>The danger list says "unlit" of a patch that is mostly dark now, and not of one that is mostly lit.</summary>
        private static void CheckDarkDangerList(Map map)
        {
            Guarded("dark: the danger list", map, r =>
            {
                DarkScene s = DarkScene.TryMake(map, r);
                if (s == null) return;
                PrisonMap pm = s.pm;
                List<IncidentMark> marks0 = pm.incidents;
                r.Add(() => pm.incidents = marks0);
                int patchIndex = pm.zones.PatchAt(s.cell);
                if (patchIndex < 0)
                {
                    DarkFail($"the check's own premise: the spot {s.cell} is in no patch of a zone");
                    return;
                }
                pm.incidents = new List<IncidentMark> { new IncidentMark { cell = s.cell, kind = DangerKind.Stabbing, tick = Find.TickManager.TicksGame } };
                List<IntVec3> cells = pm.zones.CellsOf(pm.zones.Patches[patchIndex].zone).Where(c => pm.zones.PatchAt(c) == patchIndex && !c.Impassable(map)).ToList();
                void Force(System.Func<int, bool> dark)
                {
                    for (int i = 0; i < cells.Count; i++) TensionUtility.TestGlow[cells[i]] = dark(i) ? 0f : 1f;
                }
                HotPatch Hottest()
                {
                    // (The list keeps its unlit answers for a few hundred ticks: the glow moved by hand is asked about at once.)
                    DangerMap.ForgetUnlit();
                    return DangerMap.Hottest(pm).FirstOrDefault();
                }
                Force(i => true);
                if (Hottest() is not { unlit: true }) DarkFail($"a patch of {cells.Count} cells that is all dark is not unlit on the danger list");
                Force(i => i % 10 < 7);
                if (Hottest() is not { unlit: true }) DarkFail("a patch that is seven tenths dark is not unlit on the danger list");
                Force(i => i % 10 < 3);
                if (Hottest() is not { unlit: false }) DarkFail("a patch that is three tenths dark is unlit on the danger list");
                Force(i => false);
                if (Hottest() is not { unlit: false }) DarkFail("a lit patch is unlit on the danger list");
                // The answer is kept for UnlitKeepTicks: lit again, the very next ask still says unlit (the expiry is a comparison with the tick and is not waited for), and forgetting it asks the glow again.
                Force(i => true);
                Hottest();
                Force(i => false);
                if (DangerMap.Hottest(pm).FirstOrDefault() is not { unlit: true }) DarkFail("the danger list asked again at once did not keep its unlit answer (it walks the patch's cells on every draw)");
                if (Hottest() is not { unlit: false }) DarkFail("the danger list kept its unlit answer after it was forgotten");
                Force(i => true);
                if (!"RP_DangerPlaceUnlit".CanTranslate() || !"RP_DangerRowUnlit".CanTranslate() || !"RP_ToggleCoverageDark".CanTranslate()) DarkFail("a keyed string of the dark (the unlit tag, its tip, the overlay legend) has no text");
            });
        }

        /// <summary>The builder's lamps, by the builder's own predicate (a lamp within its reach and in sight; the glow itself is read by <see cref="CheckDarkRealGlow"/>): nearly every cell of the prison it built where the dark rule applies has a lamp in reach and in sight, so a fight there is seen at night as it is by day.</summary>
        private static void CheckDarkBuilderLamps(Map map)
        {
            Guarded("dark: the builder's lamps", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                ThingDef lampDef = DefDatabase<ThingDef>.GetNamed("StandingLamp");
                List<IntVec3> lamps = map.listerBuildings.allBuildingsColonist.Where(b => b.def == lampDef).Select(b => b.Position).ToList();
                int cells = 0, unlit = 0;
                IntVec3 first = IntVec3.Invalid;
                foreach (IntVec3 c in PrisonBuilder.Footprint(map).ClipInsideMap(map))
                {
                    if (c.Impassable(map) || !TensionUtility.DarkMatters(c, map, pm)) continue;
                    cells++;
                    if (lamps.Any(l => l.InHorDistOf(c, PrisonBuilder.LampReach) && GenSight.LineOfSight(l, c, map, skipFirstCell: false))) continue;
                    unlit++;
                    if (!first.IsValid) first = c;
                }
                RPLog.Message($"[RimPrison:test] dark: {cells} free-time and corridor cells in the built prison, {unlit} out of every lamp's reach ({lamps.Count} lamps)");
                if (cells == 0) DarkFail("the check's own premise: no free-time or corridor cell in the built prison");
                else if (unlit * 20 > cells) DarkFail($"{unlit} of {cells} free-time and corridor cells have no lamp in reach, the first at {first} (a fight there at night would go unseen)");
            });
        }

        /// <summary>
        /// The real glow path: for the cells next to a lit, powered builder's lamp under a roof the glow grid itself (<c>map.glowGrid.GroundGlowAt</c>, with the grid brought up to date as a frame would) says 0.3 or more, so they
        /// are not dark by the rule. The lamps are powered by the utilities pass within 250 ticks of the build: told, not failed, when none glows yet.
        /// </summary>
        private static void CheckDarkRealGlow(Map map)
        {
            Guarded("dark: the real glow", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                ThingDef lampDef = DefDatabase<ThingDef>.GetNamed("StandingLamp");
                // (The grid is recomputed in a frame's map update, which has not run since a lamp lit in this tick.)
                map.glowGrid.GlowGridUpdate_First();
                int lamps = 0, lit = 0, judged = 0;
                foreach (Building lamp in map.listerBuildings.allBuildingsColonist)
                {
                    if (lamp.def != lampDef) continue;
                    lamps++;
                    if (lamp.GetComp<CompGlower>() is not { Glows: true } || lamp.GetComp<CompPowerTrader>() is not { PowerOn: true } || !lamp.Position.Roofed(map)) continue;
                    lit++;
                    foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(lamp))
                    {
                        if (!c.InBounds(map) || !c.Walkable(map) || !c.Roofed(map) || c.GetDoor(map) != null || !GenSight.LineOfSight(lamp.Position, c, map, skipFirstCell: true)) continue;
                        judged++;
                        float glow = map.glowGrid.GroundGlowAt(c);
                        if (glow < TensionUtility.DarkGlow) DarkFail($"the cell {c} next to the lit, powered lamp at {lamp.Position} has ground glow {glow:0.00}, under {TensionUtility.DarkGlow}");
                        else if (TensionUtility.Dark(c, map, pm)) DarkFail($"the cell {c} next to the lit lamp at {lamp.Position} has ground glow {glow:0.00} and is dark by the rule");
                    }
                    if (judged >= 12) break;
                }
                if (judged == 0) RPLog.Message($"[RimPrison:test] dark: none of the {lamps} lamps is lit and powered under a roof yet ({lit} lit), the real glow is not judged");
            });
        }
    }
}
