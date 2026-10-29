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
    /// The checks that came with the review of the death and danger commit (findings 1-10): a plot that cannot take a body does not hold it, a body lying in the plot the plot does not want is carried to the morgue,
    /// a fallback grave is one the workman can reach, nothing is buried outside the grounds, the danger map marks violence and not illness, never a weapon nobody saw, and where it happened, a guard who bleeds out of an
    /// inmate's blow is not the prison's neglect, and a pawn who comes back to life is not a death the prison answers for. Each is self-contained on throwaway pawns and rolled back, like <c>DeathDangerChecks.cs</c>.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>
        /// The starting prison's own burial plot (the layout's): painted, bare soil, every cell of it inside the grounds, and room for a dozen graves a workman on duty can walk to (checked here with the real
        /// workmen of the start; a start that has none is told so).
        /// </summary>
        private static void CheckDeathStartingPlotCanTakeBodies(Map map)
        {
            const string check = "the starting prison's plot";
            PrisonMap pm = PrisonMap.Of(map);
            List<IntVec3> cells = pm.zones.CellsOf(RPBurialDefOf.RP_BurialPlot).ToList();
            if (cells.Count == 0)
            {
                DeathFail(check, "the starting prison has no burial plot painted");
                return;
            }
            int soil = cells.Count(c => map.terrainGrid.TerrainAt(c) == TerrainDefOf.Soil), inside = cells.Count(c => PrisonGrounds.Allows(map, c));
            if (soil != cells.Count || inside != cells.Count) DeathFail(check, $"of {cells.Count} plot cells {soil} are bare soil and {inside} are inside the grounds");
            if (!map.mapPawns.FreeColonistsSpawned.Any(Burial.IsWorkman)) DeathFail(check, "the starting prison has no workman on duty to dig");
            else
            {
                // (What lies in the plot already (a grave dug by a real burial, a body that waits for one: the events plan has deaths before this check, its own made-up ones among them) is room that was used, not
                // room that is missing: the plot holds a dozen in all, and the message names what stands in it.)
                var used = new List<string>(); var other = new List<string>();
                foreach (IntVec3 c in cells)
                    foreach (Thing t in c.GetThingList(map))
                        if (t is Building_Grave or Corpse) used.Add($"{t.def.defName} at {c - PrisonBuilder.Origin(map)}");
                        else if (t is Building || t.def.category == ThingCategory.Pawn) other.Add($"{t.def.defName} at {c - PrisonBuilder.Origin(map)}");
                int room = Burial.Capacity(map, pm);
                // (Less room than the plot's size gives: what takes the cells, any thing that is not a building or a pawn too (an item, a chunk, a tree is not standable), and the cells a grave cannot have.)
                if (room + used.Count < 12)
                {
                    foreach (IntVec3 c in cells)
                        foreach (Thing t in c.GetThingList(map))
                            if (t is not (Building or Corpse or Pawn) && t.def.category != ThingCategory.Filth && (t.def.category == ThingCategory.Item || t.def.passability != Traversability.Standable))
                                other.Add($"{t.def.defName}{(t is Plant plant ? $" (grown {plant.Growth:P0})" : "")} at {c - PrisonBuilder.Origin(map)}");
                    var unfit = cells.Where(c => !Burial.Fits(map, pm, c)).Select(c => $"{c - PrisonBuilder.Origin(map)}{(c.Standable(map) ? "" : " (not standable)")}").ToList();
                    DeathFail(check, $"the starting prison's plot of {cells.Count} cells has room for {room} more graves a workman can reach, and holds {used.Count} ({string.Join(", ", used)}); other things on it: {string.Join(", ", other)}; {unfit.Count} cells no grave's corner fits on: {string.Join(", ", unfit.Take(16))}): fewer than a dozen");
                }
            }
        }

        /// <summary>Nothing wild grows in the burial plot (vanilla's spawner seeds every fertile cell; a willow came up in a cleared plot in two days); outside it the spawner still works.</summary>
        private static void CheckDeathPlotGrowsNothingWild(Map map)
        {
            Guarded("death inside: nothing wild grows in the burial plot", map, r =>
            {
                const string check = "the plot grows nothing wild";
                PrisonMap pm = PrisonMap.Of(map);
                // A free soil cell of the plot, and a control: a free fertile cell in the grounds outside it.
                bool Free(IntVec3 c) => c.GetPlant(map) == null && c.GetEdifice(map) == null && c.GetCover(map) == null && c.GetFirstItem(map) == null && !c.Roofed(map) && map.fertilityGrid.FertilityAt(c) > 0.5f
                                        && PlantUtility.SnowAllowsPlanting(c, map);
                IntVec3 plot = pm.zones.CellsOf(RPBurialDefOf.RP_BurialPlot).Where(Free).FirstOrFallback(IntVec3.Invalid);
                IntVec3 control = plot.IsValid ? GenRadial.RadialCellsAround(plot, 30f, false).Where(c => c.InBounds(map) && pm.zones.ZoneAt(c) == null && PrisonGrounds.Allows(map, c) && Free(c)).FirstOrFallback(IntVec3.Invalid) : IntVec3.Invalid;
                if (!plot.IsValid || !control.IsValid) { DeathFail(check, $"the check's own premise: no free fertile cell in the plot ({plot}) or outside it ({control})"); return; }
                void Clear(IntVec3 c)
                {
                    if (c.GetPlant(map) is { } plant) plant.Destroy(DestroyMode.Vanish);
                }
                r.Add(() => { Clear(plot); Clear(control); });
                bool grew = false;
                for (int i = 0; i < 400 && !grew; i++) grew = map.wildPlantSpawner.CheckSpawnWildPlantAt(control, 1f, 100000f) && control.GetPlant(map) != null;
                Clear(control);
                // (Out of the growing season, or in a biome that grows little, nothing wild grows anywhere: there is nothing to measure, and the plot cannot show the difference. Told, not failed: a census run
                // in a cold season had no growth on the control.)
                if (!grew)
                {
                    RPLog.Message($"[RimPrison:test] the plot grows nothing wild: not run, nothing wild grew in 400 tries on the fertile cell {control} outside the plot either (the season or the biome)");
                    return;
                }
                int grewInPlot = 0;
                for (int i = 0; i < 400; i++)
                    if (map.wildPlantSpawner.CheckSpawnWildPlantAt(plot, 1f, 100000f) || plot.GetPlant(map) != null)
                    {
                        grewInPlot++;
                        Clear(plot);
                    }
                if (grewInPlot > 0) DeathFail(check, $"a wild plant grew in the burial plot at {plot - PrisonBuilder.Origin(map)} ({grewInPlot} of 400 tries; the control outside it grew one)");
                else RPLog.Message($"[RimPrison:test] the plot grows nothing wild: 400 tries in it, none grew; outside it one did");
            });
        }

        /// <summary>Every workman on the map (the prison's own as well as the scene's) goes on leave; the returned action puts them back.</summary>
        private static Action AllWorkmenOnLeave(Map map)
        {
            var was = new List<(CompStaff sc, int leave)>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                if (p.GetComp<CompStaff>() is { role: { defName: "RP_Workman" } } sc)
                {
                    was.Add((sc, sc.leaveAtTick));
                    sc.leaveAtTick = Find.TickManager.TicksGame + 100000;
                }
            return () =>
            {
                foreach ((CompStaff sc, int leave) in was) sc.leaveAtTick = leave;
            };
        }

        /// <summary>
        /// Findings 1, 8 and 9. With a plot that cannot take a body (full, on a floor, no workman on duty, walled off from the workmen, outside the grounds) the plot does not want the body, the workman is offered
        /// nothing, and at lift-off the body goes on the shuttle as one with a family does (counted, no letter), with the warden told in one message; a plot that can take it keeps it. A burial that cannot reach
        /// the planned site does not fall back to a site no workman can walk to, and a plot outside the grounds takes no new grave.
        /// </summary>
        private static void CheckDeathPlotThatCannotTakeABody(Map map)
        {
            const string check = "a plot that cannot take a body";
            Guarded("death inside: a body is not held for a plot that cannot take it", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DeathScene s = DeathScene.Build(map, r, check);
                if (s == null) return;
                PrisonMap pm = s.pm;
                var condolences0 = g.story.condolences.ToList();
                var faults0 = g.story.bitterDeaths.ToList();
                r.Add(() =>
                {
                    g.story.condolences = condolences0;
                    g.story.bitterDeaths = faults0;
                });
                Pawn workman = s.Staff("Workman", Role("RP_Workman"), s.At(8, 5));
                var giver = new WorkGiver_BuryBody { def = DefDatabase<WorkGiverDef>.GetNamed("RP_BuryBody") };
                int n = 0;
                Corpse NewBody()
                {
                    Pawn nobody = s.Inmate("Nobody" + n++, false);
                    nobody.Kill(null);
                    Corpse c = s.Body(nobody);
                    if (c != null) s.PutInMorgue(c, 0);
                    return c;
                }
                PrisonZoneDef plot = RPBurialDefOf.RP_BurialPlot;
                var graves = new List<Building_Grave>();
                r.Add(() =>
                {
                    foreach (Building_Grave grave in graves)
                        if (!grave.Destroyed) grave.Destroy(DestroyMode.Vanish);
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    map.reachability.ClearCache();
                });

                // A usable plot wants the body, and keeps it through lift-off.
                Corpse usable = NewBody();
                if (usable == null) return;
                if (!Burial.Wanted(usable, pm) || Burial.Capacity(map, pm) < 2) DeathFail(check, $"the check's own premise: a plot of soil with a workman wants the body {Burial.Wanted(usable, pm)}, room for {Burial.Capacity(map, pm)} graves");
                Messages.Clear();
                if (CondolenceSystem.Ship(map, g) != 0 || usable.Destroyed || LiveMessages().Count != 0) DeathFail(check, "a plot that can take the body did not keep it for the workmen (or told the warden something)");
                usable.Destroy();

                // Asks the plot as the shuttle's lift-off does, with the body in the morgue, and says what happened.
                void Case(string what, bool workmanOffered)
                {
                    Corpse body = NewBody();
                    if (body == null) return;
                    Morgue.Reset();
                    if (Burial.Wanted(body, pm)) DeathFail(check, $"{what}: the plot still wants the body");
                    if (giver.HasJobOnThing(workman, body) && !workmanOffered) DeathFail(check, $"{what}: the workman is offered a burial");
                    int sent0 = g.stats.bodiesSent, letters0 = g.story.condolences.Count;
                    Messages.Clear();
                    int sent = CondolenceSystem.Ship(map, g);
                    List<Message> told = LiveMessages();
                    if (sent != 1 || !body.Destroyed || g.stats.bodiesSent - sent0 != 1) DeathFail(check, $"{what}: at lift-off {sent} bodies went, the body stayed in the morgue {!body.Destroyed}");
                    if (g.story.condolences.Count != letters0) DeathFail(check, $"{what}: a letter was queued for a body with nobody");
                    if (told.Count != 1 || told[0].text != ("RP_BodiesSent".Translate(1) + " " + "RP_BodiesNoPlot".Translate(1)).ToString().CapitalizeFirst())
                        DeathFail(check, $"{what}: the warden was told {told.Count} things ({string.Join(" | ", told.Select(x => x.text))}), one message was expected that says the plot could not take the body");
                    if (!body.Destroyed) body.Destroy();
                }

                // Full: a grave on every place there is.
                foreach (IntVec3 c in pm.zones.CellsOf(plot).ToList())
                    if (Burial.Fits(map, pm, c))
                    {
                        var grave = (Building_Grave)ThingMaker.MakeThing(ThingDefOf.Grave);
                        grave.SetFactionDirect(Faction.OfPlayer);
                        GenSpawn.Spawn(grave, c, map, Rot4.North);
                        graves.Add(grave);
                    }
                if (graves.Count == 0) DeathFail(check, "the check's own premise: no grave was put on the plot");
                Case("a full plot", workmanOffered: false);
                foreach (Building_Grave grave in graves) grave.Destroy(DestroyMode.Vanish);
                graves.Clear();

                // On a floor: no soil to dig.
                var terrain0 = s.PlotRect.Select(c => (c, map.terrainGrid.TerrainAt(c))).ToList();
                r.Add(() =>
                {
                    foreach ((IntVec3 c, TerrainDef t) in terrain0) map.terrainGrid.SetTerrain(c, t);
                });
                foreach (IntVec3 c in s.PlotRect) map.terrainGrid.SetTerrain(c, TerrainDefOf.Concrete);
                Case("a plot on a concrete floor", workmanOffered: false);
                foreach ((IntVec3 c, TerrainDef t) in terrain0) map.terrainGrid.SetTerrain(c, t);

                // No workman on duty (on leave).
                Action back = AllWorkmenOnLeave(map);
                Case("no workman on duty", workmanOffered: true);
                back();

                // Outside the grounds: what stands there works, nothing new is built.
                s.GroundsAreTheCompound(true);
                Case("a plot outside the grounds", workmanOffered: false);
                s.GroundsAreTheCompound(false);

                // Walled off: the only soil is a 2 x 2 patch inside a ring of walls, where no workman can walk.
                var walls = new List<Thing>();
                r.Add(() =>
                {
                    foreach (Thing w in walls)
                        if (!w.Destroyed) w.Destroy(DestroyMode.Vanish);
                });
                s.PaintPlot(false);
                foreach (IntVec3 c in s.PlotRect)
                {
                    bool edge = c.x == s.PlotRect.minX || c.x == s.PlotRect.maxX || c.z == s.PlotRect.minZ || c.z == s.PlotRect.maxZ;
                    if (edge)
                    {
                        Thing wall = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.BlocksGranite);
                        GenSpawn.Spawn(wall, c, map);
                        walls.Add(wall);
                    }
                    else pm.zones.Set(c, plot);
                }
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                map.reachability.ClearCache();
                if (workman.CanReach(s.PlotRect.CenterCell, PathEndMode.OnCell, Danger.Some)) DeathFail(check, "the check's own premise: the walled-off patch can be walked to");
                Case("a plot walled off from the workmen", workmanOffered: false);
                // The burial that cannot reach the planned site must not fall back to a grave nobody can walk to (finding 8).
                Corpse stuck = NewBody();
                if (stuck != null)
                {
                    bool buried = Burial.TryBury(workman, stuck, IntVec3.Invalid);
                    Building_Grave inside = s.PlotRect.ContractedBy(1).Cells.SelectMany(c => c.GetThingList(map)).OfType<Building_Grave>().FirstOrDefault();
                    if (inside != null) graves.Add(inside);
                    if (buried || inside != null || !stuck.Spawned) DeathFail(check, $"a burial with no reachable site was done anyway (buried {buried}, a grave in the walled-off patch {inside != null})");
                    if (!stuck.Destroyed) stuck.Destroy();
                }
            });
        }

        /// <summary>
        /// Finding 2. A body of the prison's own that lies in the plot is settled only if the plot is for it and can take it: the body of one with a family (or of anyone, when the plot can take no more) is carried
        /// to the morgue by the guards like any other lying about, so it is neither stuck nor unreported; the one the workmen will bury stays for them.
        /// </summary>
        private static void CheckDeathBodyInThePlotThatThePlotDoesNotWant(Map map)
        {
            const string check = "a body in the plot the plot does not want";
            Guarded("death inside: a body in the plot that the plot does not want is carried to the morgue", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DeathScene s = DeathScene.Build(map, r, check);
                if (s == null) return;
                PrisonMap pm = s.pm;
                var condolences0 = g.story.condolences.ToList();
                var faults0 = g.story.bitterDeaths.ToList();
                r.Add(() =>
                {
                    g.story.condolences = condolences0;
                    g.story.bitterDeaths = faults0;
                });
                Pawn guard = s.Staff("Guard", RPSecurityDefOf.RP_Guard);
                Pawn workman = s.Staff("Workman", Role("RP_Workman"), s.At(8, 5));
                var haul = new WorkGiver_HaulToMorgue { def = DefDatabase<WorkGiverDef>.GetNamed("RP_HaulToMorgue") };
                Corpse InPlot(string name, bool family, IntVec3 at)
                {
                    Pawn p = s.Inmate(name, family);
                    p.Kill(null);
                    Corpse c = s.Body(p);
                    if (c != null) s.PutAt(c, at);
                    return c;
                }
                Corpse withFamily = InPlot("Family", true, s.At(3, 7)), wanted = InPlot("Nobody", false, s.At(0, 4));
                if (withFamily == null || wanted == null) return;
                if (!Burial.InPlot(withFamily, pm) || !Burial.InPlot(wanted, pm)) DeathFail(check, "the check's own premise: a body is not in the plot");
                Morgue.Reset();
                if (Morgue.Settled(withFamily, pm) || !haul.HasJobOnThing(guard, withFamily))
                    DeathFail(check, $"the body of one with a family lying in the plot is settled {Morgue.Settled(withFamily, pm)}, or the guard has no job to carry it to the morgue ({haul.HasJobOnThing(guard, withFamily)}): it would lie there for good");
                if (Morgue.Uncollected(map, pm) < 1) DeathFail(check, "the body of one with a family lying in the plot is not on the list of bodies lying about");
                // The one the plot is for stays for the workmen, and is not carried away.
                if (!Morgue.Settled(wanted, pm) || haul.HasJobOnThing(guard, wanted)) DeathFail(check, "the body of one with nobody, lying in a plot that can take it, is not left for the workmen");
                // When the plot can take no more (no workman on duty), it is not left either.
                Action back = AllWorkmenOnLeave(map);
                if (Morgue.Settled(wanted, pm) || !haul.HasJobOnThing(guard, wanted)) DeathFail(check, "the body of one with nobody, lying in a plot that can take no burial, is settled there for good");
                back();
            });
        }

        /// <summary>
        /// Findings 5 and 10. A guard who bleeds out of an inmate's blow within the day is a killing, not neglect (no bitter letter), but one who bleeds out with no blow, or a long time after one, is the prison's;
        /// a pawn who died of neglect and then comes back to life does not leave a note that makes a later, unrelated death bitter.
        /// </summary>
        private static void CheckDeathFaultNotes(Map map)
        {
            const string check = "the notes of fault";
            Guarded("death inside: the notes of fault", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                var faults0 = g.story.bitterDeaths.ToList();
                List<IncidentMark> marks0 = pm.incidents;
                r.Add(() =>
                {
                    g.story.bitterDeaths = faults0;
                    pm.incidents = marks0;
                });
                pm.incidents = new List<IncidentMark>();
                Pawn attacker = Throwaway(map, r);
                Pawn Guard() => ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                void Bleed(Pawn dead)
                {
                    Hediff loss = HediffMaker.MakeHediff(HediffDefOf.BloodLoss, dead);
                    dead.health.AddHediff(loss);
                    dead.Kill(null, loss);
                    if (dead.Corpse is { } body) r.Add(() => { if (!body.Destroyed) body.Destroy(); });
                }
                // Struck by an inmate, then bled out: the inmate's doing.
                Pawn struck = Guard();
                CompStaff ssc = struck.GetComp<CompStaff>();
                struck.TakeDamage(new DamageInfo(DamageDefOf.Cut, 6f, 1f, -1f, attacker, struck.RaceProps.body.corePart)); // (armour-piercing, on the torso: a vest cannot turn the blow aside)
                if (ssc.assaultedTick != Find.TickManager.TicksGame) DeathFail(check, $"an inmate's blow on a guard was not noted (tick {ssc.assaultedTick} of {Find.TickManager.TicksGame})");
                Bleed(struck);
                if (CondolenceSystem.AtFault(struck)) DeathFail(check, "a guard who bled out of an inmate's blow was blamed on the prison's neglect (the bitter letter)");
                // Bled out with nobody's blow: neglect.
                Pawn alone = Guard();
                Bleed(alone);
                if (!CondolenceSystem.AtFault(alone)) DeathFail(check, "a guard who bled out with no blow was not the prison's fault");
                // A blow two days ago is not what they died of.
                Pawn old = Guard();
                old.GetComp<CompStaff>().assaultedTick = Find.TickManager.TicksGame - 2 * GenDate.TicksPerDay;
                Bleed(old);
                if (!CondolenceSystem.AtFault(old)) DeathFail(check, "a guard who bled out two days after an inmate's blow was not the prison's fault");

                // Died of neglect and came back: no note left for the next death.
                Pawn starved = Throwaway(map, r);
                Hediff starving = HediffMaker.MakeHediff(HediffDefOf.Malnutrition, starved);
                starved.health.AddHediff(starving);
                starved.Kill(null, starving);
                if (!CondolenceSystem.AtFault(starved)) DeathFail(check, "the check's own premise: a death of neglect left no note of the prison's fault");
                if (!ResurrectionUtility.TryResurrect(starved)) DeathFail(check, "the check's own premise: the pawn could not be brought back");
                else
                {
                    if (CondolenceSystem.AtFault(starved)) DeathFail(check, "a pawn who came back to life kept the note of the prison's fault");
                    starved.Kill(null);
                    if (starved.Corpse is { } body) r.Add(() => { if (!body.Destroyed) body.Destroy(); });
                    if (CondolenceSystem.AtFault(starved)) DeathFail(check, "an old age or a murder after the resurrection got the bitter letter of the first death");
                }
            });
        }

        // ---- the danger map ----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Finding 3. A death on the danger map is violence: a killing, a blow, a wound (an injury, bleeding out, infection); illness, starvation, heat, cold and old age are not marked, whoever dies (an inmate or a
        /// member of staff), and a natural death does not make a patch hot.
        /// </summary>
        private static void CheckDangerMapMarksViolenceNotNaturalDeaths(Map map)
        {
            Guarded("danger map: violent deaths are marked, natural ones are not", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                List<IncidentMark> marks0 = pm.incidents;
                var faults0 = PrisonGame.Instance.story.bitterDeaths.ToList();
                r.Add(() =>
                {
                    pm.incidents = marks0;
                    PrisonGame.Instance.story.bitterDeaths = faults0;
                });
                pm.incidents = new List<IncidentMark>();
                int Marks(Func<Pawn> make, Action<Pawn> kill)
                {
                    Pawn p = make();
                    int before = pm.incidents.Count(m => m.kind == DangerKind.Death);
                    kill(p);
                    if (p.Corpse is { } body) r.Add(() => { if (!body.Destroyed) body.Destroy(); });
                    return pm.incidents.Count(m => m.kind == DangerKind.Death) - before;
                }
                Pawn Inmate() => Throwaway(map, r);
                Pawn Guard() => ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Hediff Culprit(Pawn p, HediffDef def)
                {
                    Hediff h = HediffMaker.MakeHediff(def, p);
                    p.health.AddHediff(h);
                    return h;
                }
                void Die(Pawn p, HediffDef def) => p.Kill(null, Culprit(p, def));
                string[] natural =
                {
                    $"an inmate dead of no cause {Marks(Inmate, p => p.Kill(null))}",
                    $"an inmate who starved {Marks(Inmate, p => Die(p, HediffDefOf.Malnutrition))}",
                    $"an inmate dead of the flu {Marks(Inmate, p => Die(p, DefDatabase<HediffDef>.GetNamed("Flu")))}",
                    $"an inmate dead of heatstroke {Marks(Inmate, p => Die(p, HediffDefOf.Heatstroke))}",
                    $"a guard dead of no cause {Marks(Guard, p => p.Kill(null))}",
                    $"a guard who starved {Marks(Guard, p => Die(p, HediffDefOf.Malnutrition))}",
                };
                foreach (string line in natural)
                    if (!line.EndsWith(" 0")) DangerFail($"a natural death was marked as violence: {line} mark(s)");
                var violent = new (string what, int marks)[]
                {
                    ("an inmate cut down", Marks(Inmate, p => p.Kill(new DamageInfo(DamageDefOf.Cut, 200f, 0f, -1f, null)))),
                    ("an inmate who bled out", Marks(Inmate, p => Die(p, HediffDefOf.BloodLoss))),
                    ("an inmate dead of a wound's infection", Marks(Inmate, p => Die(p, HediffDefOf.WoundInfection))),
                    ("a guard cut down", Marks(Guard, p => p.Kill(new DamageInfo(DamageDefOf.Cut, 200f, 0f, -1f, null)))),
                    ("a guard who bled out", Marks(Guard, p => Die(p, HediffDefOf.BloodLoss))),
                };
                foreach ((string what, int marks) in violent)
                    if (marks != 1) DangerFail($"{what} left {marks} mark(s), one expected");
                // A natural death is not a hot patch: only the violent ones are on the list.
                if (DangerMap.Hottest(pm, 20).Sum(x => x.marks) != violent.Length) DangerFail("the hottest patches count a different number of marks than the violent deaths");
            });
        }

        /// <summary>
        /// Finding 7. An assault on staff is marked where the blows fall (the guard who was set on), not where the inmate stood when they snapped, up to twelve cells away; a fight likewise where the one they
        /// went for was.
        /// </summary>
        private static void CheckDangerMapMarksWhereItHappened(Map map)
        {
            Guarded("danger map: the mark is where the violence happened", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                List<IncidentMark> marks0 = pm.incidents;
                r.Add(() => pm.incidents = marks0);
                pm.incidents = new List<IncidentMark>();
                MisconductDef Def(string name) => DefDatabase<MisconductDef>.GetNamed(name);
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                (c.gang, c.gangKnown) = (null, false);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                IntVec3 away = GenRadial.RadialCellsAround(p.Position, 12f, true).FirstOrDefault(x => x.InBounds(map) && x.Standable(map) && x.DistanceTo(p.Position) >= 6f);
                if (!away.IsValid)
                {
                    DangerFail("no standable cell six steps from the inmate for the check");
                    return;
                }
                guard.pather.StopDead();
                guard.Position = away;
                guard.Notify_Teleported();
                if (!p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Assaulting, null, forced: true, otherPawn: guard) || p.MentalState is not MentalState_Assaulting)
                {
                    DangerFail("the check's own premise: the inmate could not be set on the guard");
                    return;
                }
                int n = pm.incidents.Count;
                MisconductUtility.Record(p, c, pm, Def("RP_AssaultStaff"), chronicle: false);
                if (pm.incidents.Count != n + 1 || pm.incidents[pm.incidents.Count - 1] is not { kind: DangerKind.Assault } mark || mark.cell != guard.Position)
                    DangerFail($"an assault on a guard {guard.Position.DistanceTo(p.Position):F0} steps away was marked at {(pm.incidents.Count == n + 1 ? pm.incidents[pm.incidents.Count - 1].Describe() : "no mark")}, not where the guard stood ({guard.Position}; the inmate stood at {p.Position})");
                p.MentalState?.RecoverFromState();
                // A riot, which has nobody to be set on, is where the one who started it stands.
                n = pm.incidents.Count;
                MisconductUtility.Record(p, c, pm, Def("RP_Riot"), chronicle: false);
                if (pm.incidents.Count != n + 1 || pm.incidents[pm.incidents.Count - 1].cell != p.Position) DangerFail("a riot was not marked where the one who started it stood");
                // A fight: where the one they went for was (when the game lets the two fight).
                Pawn other = Throwaway(map, r);
                other.pather.StopDead();
                other.Position = away;
                other.Notify_Teleported();
                p.interactions.StartSocialFight(other);
                if (p.MentalState is MentalState_SocialFighting)
                {
                    n = pm.incidents.Count;
                    MisconductUtility.Record(p, c, pm, Def("RP_Fight"), chronicle: false);
                    if (pm.incidents.Count != n + 1 || pm.incidents[pm.incidents.Count - 1].cell != other.Position) DangerFail($"a fight was not marked where the one they went for stood ({other.Position})");
                    p.MentalState?.RecoverFromState();
                    other.MentalState?.RecoverFromState();
                }
                else RPLog.Message("[RimPrison:test] danger map: the game would not start a social fight between the two throwaways, the fight's scene is not checked");
            });
        }

        /// <summary>Finding 6. The Deployment tab's patrol list is a scroll view with a row for every guard, whatever the doors and the danger map above it took (it used to stop at the bottom of the column).</summary>
        private static void CheckDeploymentTabKeepsEveryPatrolReachable()
        {
            var column = new Rect(0f, 0f, 600f, 700f);
            foreach (int guards in new[] { 0, 3, 10, 40 })
            {
                (Rect outer, Rect view) = DeploymentTab.PatrolView(column, 520f, guards);
                if (view.height < guards * DeploymentTab.PatrolRowHeight - 0.01f) DangerFail($"the patrol list has room for {view.height / DeploymentTab.PatrolRowHeight:F1} of {guards} guards: the rest cannot be given a patrol");
                if (outer.y != 520f || outer.yMax > column.yMax + 0.01f || view.width > outer.width) DangerFail($"the patrol list's scroll view {outer} leaves the column {column}");
            }
            (Rect none, _) = DeploymentTab.PatrolView(column, 740f, 5);
            if (none.height < 0f) DangerFail("the patrol list has a negative height when the column is used up");
        }
    }
}
