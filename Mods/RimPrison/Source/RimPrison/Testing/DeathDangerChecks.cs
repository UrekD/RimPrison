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
    /// Checks for a death inside (the morgue's bodies leave on the shuttle, the family writes, the plot buries the ones nobody sends for, a former friend's news mentions it) and for the danger map
    /// (gap analysis 4, sections 1.3 and 1.4). Each is self-contained, on throwaway pawns, rolled back (<see cref="Rollback"/>). The dead check runs real ticks on a small scene on open ground outside the
    /// compound (<see cref="DeathScene"/>: a morgue and a burial plot painted on it, a real guard and workman on real jobs) and a real shuttle visit; the danger checks call the game's own passes.
    /// Each fails without the feature it is named for.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckDeathDanger(Map map)
        {
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns);
            CheckDeathStartingPlotCanTakeBodies(map);
            CheckDeathBodiesGoHomeAndTheFamilyWrites(map);
            CheckDeathBurialPlotAndTheFormerFriendsNews(map);
            CheckDeathPlotThatCannotTakeABody(map);
            CheckDeathBodyInThePlotThatThePlotDoesNotWant(map);
            CheckDeathFaultNotes(map);
            CheckDangerMapMarksOnlySeenViolence(map);
            CheckDangerMapNamesTheHottestPatches(map);
            CheckDangerMapMarksViolenceNotNaturalDeaths(map);
            CheckDangerMapMarksWhereItHappened(map);
            CheckDeploymentTabKeepsEveryPatrolReachable();
            Pawn[] left = LeftBehind(map, pawnsBefore).Where(x => x.Name is NameSingle && x.LabelShort.StartsWith("Zdd")).ToArray();
            if (left.Length > 0) problems0.Add($"death and danger checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        private static void DeathFail(string check, string what) => problems0.Add($"death inside ({check}): {what}");

        private static void DangerFail(string what) => problems0.Add($"danger map: {what}");

        // ---- the scene ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>A morgue (eight cells) and a burial plot (four by four) painted on open ground outside the compound, with a real guard and workman to put on the jobs.</summary>
        private sealed class DeathScene
        {
            public const int Width = 12, Height = 8;
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly Rollback r;
            public readonly string check;
            public IntVec3 origin;

            private DeathScene(Map map, Rollback r, string check)
            {
                this.map = map;
                this.r = r;
                this.check = check;
                pm = PrisonMap.Of(map);
            }

            public IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);

            /// <summary>Where the dead fall in the scene.</summary>
            public IntVec3 Yard => At(9, 3);

            public static DeathScene Build(Map map, Rollback r, string check)
            {
                var s = new DeathScene(map, r, check);
                // The prison's own morgue and plot are out of the scene (a body goes to the nearest free cell of the first morgue in the grid, and the lift-off takes whatever lies in any): put back by the rollback.
                foreach (PrisonZoneDef zone in new[] { RPMorgueDefOf.RP_Morgue, RPBurialDefOf.RP_BurialPlot })
                    foreach (IntVec3 c in s.pm.zones.CellsOf(zone).ToList())
                    {
                        r.Add(() => s.pm.zones.Set(c, zone));
                        s.pm.zones.Set(c, null);
                    }
                Morgue.Reset();
                r.Add(() => Morgue.Reset());
                s.origin = OpenGround(map);
                if (!s.origin.IsValid)
                {
                    DeathFail(check, $"no open soil outside the compound for the scene (footprint {PrisonBuilder.Footprint(map)}, map {map.Size})");
                    return null;
                }
                foreach (IntVec3 c in new CellRect(s.origin.x, s.origin.z, Width, Height).ExpandedBy(1))
                    foreach (Thing plant in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Plant).ToList())
                        plant.Destroy(DestroyMode.Vanish);
                s.LiftGrounds();
                s.PaintMorgue(true);
                s.PaintPlot(true);
                return s;
            }

            /// <summary>The scene stands on open ground that may be outside the grounds (a plot there takes no grave): the grounds are lifted for the scene, put back by the rollback.</summary>
            private void LiftGrounds()
            {
                if (pm.grounds is not { } grounds) return;
                (bool defined, int x0, int z0, int x1, int z1) = (grounds.defined, grounds.minX, grounds.minZ, grounds.maxX, grounds.maxZ);
                r.Add(() =>
                {
                    (grounds.defined, grounds.minX, grounds.minZ, grounds.maxX, grounds.maxZ) = (defined, x0, z0, x1, z1);
                    grounds.version++;
                });
                grounds.defined = false;
                grounds.version++;
            }

            /// <summary>The grounds are the compound (the scene is outside it) or, with false, lifted again.</summary>
            public void GroundsAreTheCompound(bool on)
            {
                if (pm.grounds is not { } grounds) return;
                CellRect compound = PrisonBuilder.Footprint(map);
                grounds.defined = on;
                if (on) (grounds.minX, grounds.minZ, grounds.maxX, grounds.maxZ) = (compound.minX, compound.minZ, compound.maxX, compound.maxZ);
                grounds.version++;
            }

            private static IntVec3 OpenGround(Map map)
            {
                CellRect compound = PrisonBuilder.Footprint(map).ExpandedBy(3);
                PrisonMap pm = PrisonMap.Of(map);
                TerrainAffordanceDef needed = ThingDefOf.Grave.terrainAffordanceNeeded;
                foreach (IntVec3 c in GenRadial.RadialCellsAround(PrisonBuilder.Footprint(map).CenterCell, 79f, true))
                {
                    CellRect around = new CellRect(c.x, c.z, Width, Height).ExpandedBy(1);
                    if (!around.InBounds(map) || around.Overlaps(compound)) continue;
                    bool open = true;
                    foreach (IntVec3 x in around)
                        if (!x.Walkable(map) || x.Roofed(map) || x.GetEdifice(map) != null || x.GetTerrain(map).IsWater || pm.zones.ZoneAt(x) != null || needed != null && !x.GetAffordances(map).Contains(needed)
                            || x.GetThingList(map).Any(t => t is Pawn || t.def.category is ThingCategory.Item or ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame))
                        {
                            open = false;
                            break;
                        }
                    if (open) return c;
                }
                return IntVec3.Invalid;
            }

            private void Paint(CellRect rect, PrisonZoneDef zone)
            {
                foreach (IntVec3 c in rect)
                {
                    PrisonZoneDef was = pm.zones.ZoneAt(c);
                    r.Add(() => pm.zones.Set(c, was));
                    pm.zones.Set(c, zone);
                }
            }

            public CellRect MorgueRect => new CellRect(origin.x, origin.z, 4, 2);
            public CellRect PlotRect => new CellRect(origin.x, origin.z + 4, 4, 4);

            public void PaintMorgue(bool on)
            {
                if (on) Paint(MorgueRect, RPMorgueDefOf.RP_Morgue);
                else foreach (IntVec3 c in MorgueRect) pm.zones.Set(c, null);
                Morgue.Reset();
            }

            public void PaintPlot(bool on)
            {
                if (on) Paint(PlotRect, RPBurialDefOf.RP_BurialPlot);
                else foreach (IntVec3 c in PlotRect) pm.zones.Set(c, null);
            }

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            /// <summary>An inmate with a family in touch, or none, standing in the scene.</summary>
            public Pawn Inmate(string name, bool family, IntVec3? at = null)
            {
                Pawn p = Throwaway(map, r);
                p.Name = new NameSingle("Zdd" + name);
                CompInmate c = p.Inmate();
                c.relatives.Clear();
                c.familyCount = 0;
                if (family)
                {
                    c.relatives.Add(new Relative { name = "Ines", kind = "RP_Kin_Mother" });
                    c.familyCount = 1;
                }
                Place(p, at ?? Yard);
                return p;
            }

            /// <summary>A guard or a workman on site with no work of their own (the checks put them on the job by hand).</summary>
            public Pawn Staff(string name, StaffRoleDef role, IntVec3? at = null)
            {
                Pawn s = ThrowawayStaff(map, r, role);
                s.Name = new NameSingle("Zdd" + name);
                if (s.timetable != null)
                    for (int hour = 0; hour < 24; hour++) s.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                if (s.needs?.rest != null) s.needs.rest.CurLevel = 1f;
                CompStaff sc = s.GetComp<CompStaff>();
                (sc.corrupt, sc.suspendedUntilTick) = (false, -1);
                if (s.workSettings != null)
                    foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefsListForReading) s.workSettings.SetPriority(work, 0);
                Place(s, at ?? At(8, 1));
                return s;
            }

            /// <summary>The body of one who has just died, with what is left of them put away by the rollback.</summary>
            public Corpse Body(Pawn dead)
            {
                Corpse c = dead.Corpse;
                if (c == null)
                {
                    DeathFail(check, $"{dead.LabelShort} died and left no body");
                    return null;
                }
                r.Add(() =>
                {
                    if (!c.Destroyed) c.Destroy();
                    if (!dead.Destroyed) dead.Destroy(DestroyMode.Vanish);
                });
                Allow(c);
                return c;
            }

            public void PutInMorgue(Corpse c, int slot) => PutAt(c, At(slot % 4, slot / 4));

            /// <summary>Lays the body on a cell of the scene (free to take).</summary>
            public void PutAt(Corpse c, IntVec3 cell)
            {
                if (c.Spawned) c.DeSpawn();
                GenSpawn.Spawn(c, cell, map);
                Allow(c);
                Morgue.Reset();
            }

            /// <summary>(Vanilla forbids a body that lies outside the home area, as the scene does: the staff would not touch it. The check is about what they do with one they may.)</summary>
            private static void Allow(Corpse c)
            {
                if (c.TryGetComp<CompForbiddable>() is { } forbiddable) forbiddable.Forbidden = false;
            }
        }

        private static StaffRoleDef Role(string name) => DefDatabase<StaffRoleDef>.GetNamed(name);

        /// <summary>Runs the job on the pawn (as the work giver offered it) through real ticks until <paramref name="done"/>, at most <paramref name="most"/>; false when the giver offered none.</summary>
        private static bool DeathRunJob(Pawn worker, WorkGiver_Scanner giver, Thing target, Func<bool> done, int most)
        {
            if (!giver.HasJobOnThing(worker, target) || giver.JobOnThing(worker, target) is not { } job) return false;
            worker.jobs.StartJob(job, JobCondition.InterruptForced);
            ShuttleTicks(done, most);
            return true;
        }

        // ---- the dead: the shuttle, the family ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard carries an inmate who died to the morgue; the bodies in the morgue (an inmate whose family is in touch, one who starved, one a guard killed, a member of staff, and one with no family and
        /// no plot) are gone when the state shuttle lifts off (a real visit), the families' letters are queued (bitter for neglect and the killing by staff, plain for the rest, none for the one with nobody)
        /// and come a few days later; the plain letter costs nothing, the bitter one costs the prison a little reputation.
        /// </summary>
        private static void CheckDeathBodiesGoHomeAndTheFamilyWrites(Map map)
        {
            const string check = "the shuttle takes them home";
            Guarded("death inside: the morgue's bodies leave on the shuttle and the family writes", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "death inside")) return;
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
                s.PaintPlot(false);
                Pawn guard = s.Staff("Guard", RPSecurityDefOf.RP_Guard);
                Pawn killer = s.Staff("Killer", RPSecurityDefOf.RP_Guard, s.At(10, 1));
                var giver = new WorkGiver_HaulToMorgue { def = DefDatabase<WorkGiverDef>.GetNamed("RP_HaulToMorgue") };

                // An inmate with family dies (nothing to blame); a guard carries the body into the morgue on real ticks.
                Pawn plain = s.Inmate("Plain", true);
                plain.Kill(null);
                Corpse plainBody = s.Body(plain);
                if (plainBody == null) return;
                if (CondolenceSystem.AtFault(plain)) DeathFail(check, "a death with nobody to blame was marked as the prison's fault");
                if (!DeathRunJob(guard, giver, plainBody, () => Morgue.InMorgue(plainBody, pm), 1800) || !Morgue.InMorgue(plainBody, pm))
                    DeathFail(check, $"the guard did not carry the body of an inmate who died into the morgue (corpse at {plainBody.Position}, guard {guard.CurJob?.def?.defName} at {guard.Position})");

                // One who starved (neglect), one a guard killed, a member of staff, one with nobody.
                Pawn starved = s.Inmate("Starved", true);
                Hediff starving = HediffMaker.MakeHediff(HediffDefOf.Malnutrition, starved);
                starved.health.AddHediff(starving);
                starved.Kill(null, starving);
                Pawn shot = s.Inmate("Shot", true);
                shot.Kill(new DamageInfo(DamageDefOf.Cut, 200f, 0f, -1f, killer));
                Pawn colleague = s.Staff("Colleague", RPSecurityDefOf.RP_Guard, s.At(10, 5));
                colleague.Kill(null);
                Pawn nobody = s.Inmate("Nobody", false);
                nobody.Kill(null);
                Pawn[] dead = { starved, shot, colleague, nobody };
                if (!CondolenceSystem.AtFault(starved) || !CondolenceSystem.AtFault(shot)) DeathFail(check, $"neglect {CondolenceSystem.AtFault(starved)} and a killing by staff {CondolenceSystem.AtFault(shot)} were not marked as the prison's fault");
                if (CondolenceSystem.AtFault(colleague) || CondolenceSystem.AtFault(nobody)) DeathFail(check, "a death with nobody to blame was marked as the prison's fault");
                var bodies = new List<Corpse> { plainBody };
                int slot = 1;
                foreach (Pawn d in dead)
                {
                    Corpse c = s.Body(d);
                    if (c == null) return;
                    s.PutInMorgue(c, slot++);
                    bodies.Add(c);
                }
                foreach (Corpse c in bodies)
                    if (!Morgue.InMorgue(c, pm)) DeathFail(check, "the check's own premise: a body is not in the morgue");

                // A real shuttle visit (a cargo run) lifts off with the bodies in the morgue.
                g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = 10, cost = 0, arriveTick = ShNow - 1 });
                var steelBefore = new HashSet<Thing>(map.listerThings.ThingsOfDef(ThingDefOf.Steel));
                r.Add(() =>
                {
                    foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Steel).ToList())
                        if (!steelBefore.Contains(t) && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
                });
                if (!ShuttleSystem.CallCargo(g, map))
                {
                    DeathFail(check, "the cargo run could not be called");
                    return;
                }
                ShuttleVisit v = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 3000);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed)
                {
                    DeathFail(check, $"the shuttle did not land ({v.Describe()})");
                    return;
                }
                if (bodies.Any(b => b.Destroyed)) DeathFail(check, "a body was gone before the shuttle lifted off");
                int bodiesSent0 = g.stats.bodiesSent;
                ShuttleDepartIn(v, 0);
                ShuttleTicks(() => g.shuttle.visit != v, 1500);
                if (g.shuttle.visit == v)
                {
                    DeathFail(check, $"the shuttle did not lift off ({v.Describe()})");
                    return;
                }
                int gone = bodies.Count(b => b.Destroyed);
                if (gone != bodies.Count || g.stats.bodiesSent - bodiesSent0 != bodies.Count)
                    DeathFail(check, $"{gone} of the {bodies.Count} bodies in the morgue were gone after lift-off (counted as sent: {g.stats.bodiesSent - bodiesSent0})");
                Condolence Of(Pawn p) => g.story.condolences.FirstOrDefault(c => c.name == p.LabelShort);
                Condolence cPlain = Of(plain), cStarved = Of(starved), cShot = Of(shot), cColleague = Of(colleague);
                if (cPlain?.kind != RPCondolenceDefOf.RP_Mail_Condolence || cPlain.staff || cPlain.from == null || !cPlain.from.Contains("Ines"))
                    DeathFail(check, $"the plain family letter is {cPlain?.Describe() ?? "not queued"}");
                if (cStarved?.kind != RPCondolenceDefOf.RP_Mail_CondolenceBitter || cShot?.kind != RPCondolenceDefOf.RP_Mail_CondolenceBitter)
                    DeathFail(check, $"the letters for the one who starved ({cStarved?.Describe() ?? "none"}) and the one a guard killed ({cShot?.Describe() ?? "none"}) are not bitter");
                if (cColleague?.kind != RPCondolenceDefOf.RP_Mail_Condolence || !cColleague.staff) DeathFail(check, $"the staff member's family letter is {cColleague?.Describe() ?? "not queued"}");
                if (Of(nobody) != null) DeathFail(check, "a letter was queued for a body with no family");
                int days = Find.TickManager.TicksGame;
                foreach (Condolence c in new[] { cPlain, cStarved, cShot, cColleague })
                    if (c != null && (c.dueTick <= days || c.dueTick - days > CondolenceSystem.MaxDays * GenDate.TicksPerDay)) DeathFail(check, $"a letter's due time is {(c.dueTick - days) / (float)GenDate.TicksPerDay:F2} days away (it is one to three days after lift-off)");

                // Nothing has come yet; when it is due the letters come, and only the bitter ones cost reputation.
                int lettersBefore = r.NewLetters.Count();
                CondolenceSystem.Hourly(g);
                if (r.NewLetters.Count() != lettersBefore) DeathFail(check, "a family's letter came before it was due");
                foreach (Condolence c in g.story.condolences) c.dueTick = Find.TickManager.TicksGame - 1;
                float reputation0 = g.reputation;
                int delivered0 = g.stats.condolences;
                CondolenceSystem.Hourly(g);
                List<Letter> letters = r.NewLetters.ToList();
                bool Has(Pawn p, string kind, LetterDef def) => letters.Any(l => l.def == def && l.Label.ToString() == $"{kind}_Label".Translate(p.LabelShort).ToString());
                if (!Has(plain, "RP_Mail_Condolence", LetterDefOf.NeutralEvent) || !Has(colleague, "RP_Mail_Condolence", LetterDefOf.NeutralEvent))
                    DeathFail(check, "the plain letters did not come as neutral letters with the dead one's name");
                if (!Has(starved, "RP_Mail_CondolenceBitter", LetterDefOf.NegativeEvent) || !Has(shot, "RP_Mail_CondolenceBitter", LetterDefOf.NegativeEvent))
                    DeathFail(check, "the bitter letters did not come as negative letters with the dead one's name");
                if (Mathf.Abs((reputation0 - g.reputation) - 2f) > 0.01f) DeathFail(check, $"two bitter letters cost {reputation0 - g.reputation} reputation (1 each)");
                if (g.stats.condolences - delivered0 != 4 || g.story.condolences.Any(c => c.name == plain.LabelShort)) DeathFail(check, "the letters were not counted and taken off the queue");
                if (g.story.bitterDeaths.Contains(starved.thingIDNumber) || g.story.bitterDeaths.Contains(shot.thingIDNumber)) DeathFail(check, "the note of the prison's fault stayed after the body went home");
                if (!r.NewEntries.Any(e => e.text.Contains(plain.LabelShort) && e.text == "RP_Chr_BodySent".Translate(plain.LabelShort).ToString())) DeathFail(check, "no chronicle line for the body going home");
            });
        }

        // ---- the plot, and a former friend's news ------------------------------------------------------------------------------------------------

        /// <summary>
        /// With a plot painted, a workman (not a guard) carries the body of one with no family from the morgue to the plot and a grave is dug with the body in it, a chronicle line says so; the body of one
        /// with a family is not buried and goes home; a former friend whose news was still to come mentions the grave (one whose news had come does not); with no plot the body with nobody goes home with the others.
        /// </summary>
        private static void CheckDeathBurialPlotAndTheFormerFriendsNews(Map map)
        {
            const string check = "the plot";
            Guarded("death inside: the workmen bury the dead nobody sends for", map, r =>
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
                var giver = new WorkGiver_BuryBody { def = DefDatabase<WorkGiverDef>.GetNamed("RP_BuryBody") };
                Pawn nobody = s.Inmate("Nobody", false), nobody2 = s.Inmate("Nobody2", false), family = s.Inmate("Family", true);
                nobody.Kill(null);
                nobody2.Kill(null);
                family.Kill(null);
                Corpse body = s.Body(nobody), body2 = s.Body(nobody2), familyBody = s.Body(family);
                if (body == null || body2 == null || familyBody == null) return;
                s.PutInMorgue(body, 0);
                s.PutInMorgue(body2, 1);
                s.PutInMorgue(familyBody, 2);
                // A friend who left (news still to come) and one whose news has come.
                var waiting = new FormerInmate { name = "Zddformer", newsTick = Find.TickManager.TicksGame + 5 * GenDate.TicksPerDay, friendIds = new List<int> { nobody.thingIDNumber } };
                var heard = new FormerInmate { name = "Zddheard", newsTick = -1, news = "RP_FormerNews_Quiet", friendIds = new List<int> { nobody.thingIDNumber } };
                var stranger = new FormerInmate { name = "Zddstranger", newsTick = Find.TickManager.TicksGame + 5 * GenDate.TicksPerDay, friendIds = new List<int>() };
                g.story.formerInmates.AddRange(new[] { waiting, heard, stranger });

                if (!giver.ShouldSkip(guard) || giver.ShouldSkip(workman)) DeathFail(check, $"a guard skips the burying {giver.ShouldSkip(guard)} and a workman does {giver.ShouldSkip(workman)}: only workmen bury");
                if (giver.HasJobOnThing(workman, familyBody)) DeathFail(check, "a body with a family in touch has a job to bury it");
                if (!Burial.Wanted(body, pm) || Burial.Wanted(familyBody, pm)) DeathFail(check, "the plot wants the body of one with a family, or not the one with nobody");
                int buried0 = g.stats.buried;
                if (!giver.HasJobOnThing(workman, body) || giver.JobOnThing(workman, body) is not { } job || job.def != RPBurialDefOf.RP_BuryBody || pm.zones.ZoneAt(job.targetB.Cell) != RPBurialDefOf.RP_BurialPlot)
                {
                    DeathFail(check, "the workman was offered no job to bury the body in the plot");
                    return;
                }
                workman.jobs.StartJob(job, JobCondition.InterruptForced);
                ShuttleTicks(() => !body.Spawned && workman.CurJob?.def != RPBurialDefOf.RP_BuryBody, 3000);
                Building_Grave grave = pm.zones.CellsOf(RPBurialDefOf.RP_BurialPlot).SelectMany(c => c.GetThingList(map)).OfType<Building_Grave>().FirstOrDefault(x => x.Corpse == body);
                r.Add(() =>
                {
                    if (grave is { Destroyed: false }) grave.Destroy(DestroyMode.Vanish);
                });
                if (grave == null || body.Spawned) DeathFail(check, $"after {3000} ticks no grave holds the body (corpse spawned {body.Spawned}, workman {workman.CurJob?.def?.defName} at {workman.Position})");
                else if (!pm.zones.CellsOf(RPBurialDefOf.RP_BurialPlot).Contains(grave.Position) || grave.Faction != Faction.OfPlayer) DeathFail(check, "the grave is outside the plot or not the prison's");
                if (g.stats.buried - buried0 != 1) DeathFail(check, "the burial was not counted");
                string line = "RP_Chr_Buried".Translate(nobody.LabelShort).ToString();
                if (!r.NewEntries.Any(e => e.text == line)) DeathFail(check, "no chronicle line for the burial");
                if (waiting.graveName != nobody.LabelShort || heard.graveName != null || stranger.graveName != null)
                    DeathFail(check, $"the friend with news to come has the grave {waiting.graveName ?? "-"}, the one whose news had come {heard.graveName ?? "-"}, a stranger {stranger.graveName ?? "-"}");
                FormerInmates.Deliver(g, map, waiting);
                if (waiting.news != "RP_FormerNews_Grave" || waiting.graveName != null) DeathFail(check, $"the friend's news is {waiting.news} (it should mention the grave), the grave kept after the news {waiting.graveName}");
                FormerInmates.Deliver(g, map, stranger);
                if (stranger.news == "RP_FormerNews_Grave") DeathFail(check, "a stranger's news mentions the grave");

                // The other bodies: the one with a family goes home, the one with nobody stays for the workmen while there is a plot.
                int sent0 = g.stats.bodiesSent;
                int sent = CondolenceSystem.Ship(map, g);
                if (sent != 1 || !familyBody.Destroyed || body2.Destroyed || g.stats.bodiesSent - sent0 != 1) DeathFail(check, $"at lift-off with a plot, {sent} bodies went (the family's {familyBody.Destroyed}, the one with nobody {body2.Destroyed})");
                if (g.story.condolences.All(c => c.name != family.LabelShort)) DeathFail(check, "no letter was queued for the body that went home");
                // Without a plot a body with nobody goes home with the rest, and nobody writes.
                s.PaintPlot(false);
                if (Burial.Wanted(body2, pm) || !giver.ShouldSkip(workman)) DeathFail(check, "with no plot the workman is still looking for graves");
                int letters = g.story.condolences.Count;
                if (CondolenceSystem.Ship(map, g) != 1 || !body2.Destroyed || g.story.condolences.Count != letters) DeathFail(check, "with no plot the body with nobody did not go home with no letter");
            });
        }

        // ---- the danger map ----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A seen fight (loud: always seen) puts a mark where it was, a fight with a weapon in hand a stabbing (when the weapon was seen), an assault on staff and a riot starting each their own, a killing its own; what is not violence
        /// (an insult, a smashed chair), a riot joined, a murder record (the death is marked) and a gang ambush no guard or camera saw leave none; the same ambush found by staff is a mark.
        /// </summary>
        private static void CheckDangerMapMarksOnlySeenViolence(Map map)
        {
            Guarded("danger map: only seen violence is marked", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                List<IncidentMark> marks0 = pm.incidents;
                r.Add(() => pm.incidents = marks0);
                pm.incidents = new List<IncidentMark>();
                MisconductDef Def(string name) => DefDatabase<MisconductDef>.GetNamed(name);
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                // Somewhere nobody can see: a cell with no guard, camera or tower covering it.
                IntVec3 hidden = IntVec3.Invalid;
                int tried = 0;
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(p.Position, 40f, true))
                {
                    if (!cell.InBounds(map) || !cell.Standable(map) || ++tried > 600) continue;
                    p.Position = cell;
                    p.Notify_Teleported();
                    if (TensionUtility.Watchers(p, pm) <= 0f)
                    {
                        hidden = cell;
                        break;
                    }
                }
                if (!hidden.IsValid)
                {
                    DangerFail("no cell out of every guard's, camera's and tower's sight for the check");
                    return;
                }
                (c.gang, c.gangKnown) = (null, false);
                IncidentMark Record(MisconductDef def, bool evidence = false, bool unseen = false)
                {
                    int n = pm.incidents.Count;
                    MisconductUtility.Record(p, c, pm, def, evidence: evidence, chronicle: false, unseen: unseen);
                    return pm.incidents.Count == n + 1 ? pm.incidents[pm.incidents.Count - 1] : pm.incidents.Count == n ? null : throw new InvalidOperationException("two marks for one incident");
                }
                IncidentMark fight = Record(Def("RP_Fight"));
                if (fight == null || fight.kind != DangerKind.Fight || fight.cell != hidden || fight.tick != Find.TickManager.TicksGame) DangerFail($"a fight left {(fight == null ? "no mark" : fight.Describe())} (at {hidden}, a fight is loud: always seen)");
                // A weapon in the hand makes it a stabbing only where it was seen: here, in a blind spot, the fight is marked (it is loud) and the knife is not (a shiv drawn where nobody can see it is known to nobody).
                var knife = (ThingWithComps)ThingMaker.MakeThing(ThingDefOf.MeleeWeapon_Knife, GenStuff.DefaultStuffFor(ThingDefOf.MeleeWeapon_Knife));
                p.equipment.AddEquipment(knife);
                r.Add(() => p.equipment.DestroyAllEquipment());
                IncidentMark blind = Record(Def("RP_Fight"));
                if (blind?.kind != DangerKind.Fight) DangerFail($"a fight with a knife in hand in a blind spot left {(blind == null ? "no mark" : blind.Describe())}: a weapon nobody saw must not show (it should be a plain fight)");
                // Found by staff (evidence), the knife is known wherever it was.
                IncidentMark found = Record(Def("RP_Fight"), evidence: true);
                if (found?.kind != DangerKind.Stabbing) DangerFail($"a fight with a knife in hand found by staff left {(found == null ? "no mark" : found.Describe())}, not a stabbing");
                // Where a guard, camera or tower watches, the knife is seen.
                IntVec3 watched = IntVec3.Invalid;
                tried = 0;
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(hidden, 60f, true))
                {
                    if (!cell.InBounds(map) || !cell.Standable(map) || ++tried > 1500) continue;
                    p.Position = cell;
                    p.Notify_Teleported();
                    if (TensionUtility.Watchers(p, pm) > 0f)
                    {
                        watched = cell;
                        break;
                    }
                }
                // (In some runs no guard, camera or tower covers any of the nearest cells when the check runs, where the guards are depends on the hour and the shift: "no watched cell for the check", seen in
                // several events runs. Then a guard stands beside the hidden cell, on the prison's books for the moment of the check only: the knife is still seen by a
                // watcher, and the hidden cell is hidden again after it.)
                Pawn lookout = null;
                if (!watched.IsValid)
                {
                    lookout = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                    lookout.Position = hidden;
                    lookout.Notify_Teleported();
                    pm.Guards.Add(lookout);
                    foreach (IntVec3 cell in GenRadial.RadialCellsAround(hidden, 5f, false))
                    {
                        if (!cell.InBounds(map) || !cell.Standable(map) || !GenSight.LineOfSight(hidden, cell, map, skipFirstCell: true)) continue;
                        p.Position = cell;
                        p.Notify_Teleported();
                        if (TensionUtility.Watchers(p, pm) > 0f)
                        {
                            watched = cell;
                            break;
                        }
                    }
                }
                if (!watched.IsValid) DangerFail("no watched cell for the check, with a guard put beside the hidden one");
                else if (Record(Def("RP_Fight")) is not { kind: DangerKind.Stabbing }) DangerFail("a fight with a knife in hand where the staff watch is not a stabbing");
                if (lookout != null) pm.Guards.Remove(lookout);
                p.Position = hidden;
                p.Notify_Teleported();
                p.equipment.DestroyAllEquipment();
                IncidentMark assault = Record(Def("RP_AssaultStaff")), hostage = Record(Def("RP_HostageTaking")), riot = Record(Def("RP_Riot"));
                if (assault?.kind != DangerKind.Assault || hostage?.kind != DangerKind.Assault) DangerFail($"an assault on staff ({assault?.Describe() ?? "none"}) or a hostage-taking ({hostage?.Describe() ?? "none"}) did not leave an assault mark");
                if (riot?.kind != DangerKind.Riot) DangerFail($"a riot starting left {(riot == null ? "no mark" : riot.Describe())}");
                // Not violence to a person, or marked elsewhere.
                foreach (string name in new[] { "RP_Insult", "RP_Vandalism", "RP_EscapeAttempt", "RP_RiotJoined", "RP_Murder" })
                    if (Record(Def(name)) is { } extra) DangerFail($"{name} left a mark ({extra.Describe()})");
                // The war's ambush, out of sight, is not seen: nothing on the map. Found by staff it is.
                MisconductDef ambush = Def("RP_Ambush");
                if (TensionUtility.Watchers(p, pm) > 0f) DangerFail("the check's hidden cell is watched");
                else if (Record(ambush) is { } unseenMark) DangerFail($"a gang ambush nobody saw is on the map ({unseenMark.Describe()}): the hidden-information rule");
                if (Record(ambush, unseen: true) != null) DangerFail("an ambush marked unseen is on the map");
                if (Record(ambush, evidence: true) is not { kind: DangerKind.Fight }) DangerFail("an ambush found by staff is not on the map");
                // A killing is marked where they died (an inmate, and a member of staff).
                int before = pm.incidents.Count;
                Pawn dying = Throwaway(map, r);
                IntVec3 diedAt = dying.Position;
                dying.Kill(new DamageInfo(DamageDefOf.Cut, 200f, 0f, -1f, null));
                if (dying.Corpse is { } body0) r.Add(() => { if (!body0.Destroyed) body0.Destroy(); });
                r.Add(() => { if (!dying.Destroyed) dying.Destroy(DestroyMode.Vanish); });
                if (pm.incidents.Count != before + 1 || pm.incidents[pm.incidents.Count - 1] is not { kind: DangerKind.Death } death || death.cell != diedAt) DangerFail($"an inmate's killing left {pm.incidents.Count - before} marks (a death at {diedAt})");
                before = pm.incidents.Count;
                Pawn officer = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                IntVec3 officerAt = officer.Position;
                officer.Kill(new DamageInfo(DamageDefOf.Cut, 200f, 0f, -1f, null));
                if (officer.Corpse is { } body1) r.Add(() => { if (!body1.Destroyed) body1.Destroy(); });
                if (pm.incidents.Count != before + 1 || pm.incidents[pm.incidents.Count - 1] is not { kind: DangerKind.Death } d2 || d2.cell != officerAt) DangerFail($"a member of staff's killing left {pm.incidents.Count - before} marks");
                // The glow is where the marks are (and not far from them); it forgets them after a month.
                DangerOverlay overlay = pm.DangerShown;
                overlay.Rebuild(pm);
                IntVec3 far = new IntVec3(hidden.x < map.Size.x / 2 ? map.Size.x - 3 : 2, 0, hidden.z < map.Size.z / 2 ? map.Size.z - 3 : 2);
                if (overlay.At(hidden) <= 0f || overlay.At(far) > 0f) DangerFail($"the glow is {overlay.At(hidden):F2} on a mark and {overlay.At(far):F2} far from every one");
                foreach (IncidentMark m in pm.incidents) m.tick -= (PrisonMap.DangerDays + 1) * GenDate.TicksPerDay;
                overlay.Rebuild(pm);
                if (overlay.At(hidden) > 0f || DangerMap.Hottest(pm).Count != 0) DangerFail("marks older than a month are still on the map");
                // The list is trimmed when the next is made.
                pm.AddIncident(hidden, DangerKind.Fight);
                if (pm.incidents.Count != 1) DangerFail($"the old marks were not dropped when a new one was made ({pm.incidents.Count} left)");
            });
        }

        /// <summary>The three hottest patches, named, hottest first: a stabbing counts more than a fight, a death more than a stabbing, a month old less than new, one past the month not at all.</summary>
        private static void CheckDangerMapNamesTheHottestPatches(Map map)
        {
            Guarded("danger map: the hottest patches", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                List<IncidentMark> marks0 = pm.incidents;
                r.Add(() => pm.incidents = marks0);
                pm.incidents = new List<IncidentMark>();
                List<ZonePatch> patches = pm.zones.Patches.GroupBy(x => x.zone).Select(grp => grp.First()).ToList();
                if (patches.Count < 4)
                {
                    DangerFail($"the prison has {patches.Count} zones painted: four are needed for the check");
                    return;
                }
                int now = Find.TickManager.TicksGame;
                IntVec3 Cell(ZonePatch patch) => map.cellIndices.IndexToCell(patch.anchor);
                void Mark(ZonePatch patch, DangerKind kind, int days = 0) => pm.incidents.Add(new IncidentMark { cell = Cell(patch), kind = kind, tick = now - days * GenDate.TicksPerDay });
                (ZonePatch a, ZonePatch b, ZonePatch c, ZonePatch d) = (patches[0], patches[1], patches[2], patches[3]);
                // a: three stabbings (9), b: a death (4), c: two fights (2), d: a death a month old... and a fight past it.
                for (int i = 0; i < 3; i++) Mark(a, DangerKind.Stabbing);
                Mark(b, DangerKind.Death);
                Mark(c, DangerKind.Fight);
                Mark(c, DangerKind.Fight);
                Mark(d, DangerKind.Fight, 29);
                Mark(d, DangerKind.Death, PrisonMap.DangerDays + 2);
                List<HotPatch> hot = DangerMap.Hottest(pm);
                if (hot.Count != 3) DangerFail($"{hot.Count} patches named, three were asked for ({string.Join("; ", hot.Select(x => x.place))})");
                else
                {
                    string[] zones = { a.zone.label, b.zone.label, c.zone.label };
                    for (int i = 0; i < 3; i++)
                        if (!hot[i].place.StartsWith(zones[i])) DangerFail($"patch {i + 1} is {hot[i].place}, it should be the {zones[i]}");
                    if (hot[0].marks != 3 || hot[0].counts[(int)DangerKind.Stabbing] != 3 || !hot[0].KindsText().Contains("3")) DangerFail($"the hottest patch counts {hot[0].marks} marks ({hot[0].KindsText()})");
                    if (hot[0].center != Cell(a)) DangerFail($"the hottest patch points at {hot[0].center}, the marks are at {Cell(a)}");
                }
                // The month-old fight counts for less than a new one (the fourth is only the old fight: the death past a month is not counted).
                List<HotPatch> four = DangerMap.Hottest(pm, 4);
                if (four.Count != 4 || four[3].marks != 1 || four[3].heat >= 1f || four[3].heat <= 0f) DangerFail($"the fourth patch has {(four.Count == 4 ? $"{four[3].marks} marks and heat {four[3].heat:F2}" : "no entry")}: one fight a month old, less than a new one and more than nothing");
                if (DangerMap.Heat(new IncidentMark { kind = DangerKind.Fight, tick = now - 29 * GenDate.TicksPerDay }, now) >= DangerMap.Heat(new IncidentMark { kind = DangerKind.Fight, tick = now }, now)) DangerFail("an old fight is as hot as a new one");
            });
        }

        // ---- the round trip through a save -------------------------------------------------------------------------------------------------------

        private static bool deathDangerSeeded;
        private static int deathDangerStruckStaff = -1;
        private static (int bodies, int buried, int condolences) deathDangerStats0;

        /// <summary>The saveload plan: marks on the danger map, a family's letter on its way, a note of the prison's fault, a former friend's news that names a grave, and the three counters. The snapshot compares them; after the reload they go.</summary>
        private static void SeedDeathDangerForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            int now = Find.TickManager.TicksGame;
            IntVec3 spot = pm.zones.Patches.Count > 0 ? map.cellIndices.IndexToCell(pm.zones.Patches[0].anchor) : new IntVec3(20, 0, 20);
            pm.incidents.Add(new IncidentMark { cell = spot, kind = DangerKind.Stabbing, tick = now - 777 });
            pm.incidents.Add(new IncidentMark { cell = spot + new IntVec3(2, 0, 1), kind = DangerKind.Death, tick = now - 4321 });
            g.story.condolences.Add(new Condolence { kind = RPCondolenceDefOf.RP_Mail_CondolenceBitter, name = "Zddseed", from = "daughter Ines", staff = false, dueTick = now + 99999 });
            g.story.bitterDeaths.Add(424242);
            g.story.formerInmates.Add(new FormerInmate { name = "Zddseedformer", newsTick = now + 99999, graveName = "Zddseedgrave", friendIds = new List<int> { 424242 } });
            // A member of staff an inmate struck lately (a death of the wound is the inmate's, not the prison's neglect).
            if (map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.GetComp<CompStaff>()?.role != null) is { } struck)
            {
                struck.GetComp<CompStaff>().assaultedTick = now - 2468;
                deathDangerStruckStaff = struck.thingIDNumber;
            }
            deathDangerStats0 = (g.stats.bodiesSent, g.stats.buried, g.stats.condolences);
            (g.stats.bodiesSent, g.stats.buried, g.stats.condolences) = (g.stats.bodiesSent + 5, g.stats.buried + 7, g.stats.condolences + 3);
            deathDangerSeeded = true;
            RPLog.Message("[RimPrison:test] saveload death and danger: two marks on the danger map, a bitter letter on its way, a fault noted, a friend's news that names a grave, counters 5/7/3 higher");
        }

        private static void CheckDeathDangerAfterReload(Map map)
        {
            if (!deathDangerSeeded) return;
            deathDangerSeeded = false;
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            int now = Find.TickManager.TicksGame;
            bool marks = pm.incidents.Count(m => m.kind == DangerKind.Stabbing && now - m.tick is >= 777 and < 2000) >= 1 && pm.incidents.Count(m => m.kind == DangerKind.Death && now - m.tick >= 4321) >= 1;
            Condolence letter = g.story.condolences.FirstOrDefault(c => c.name == "Zddseed");
            bool letterOk = letter != null && letter.kind == RPCondolenceDefOf.RP_Mail_CondolenceBitter && letter.from == "daughter Ines" && !letter.staff && letter.dueTick > now;
            FormerInmate former = g.story.formerInmates.FirstOrDefault(e => e.name == "Zddseedformer");
            bool formerOk = former is { graveName: "Zddseedgrave" } && former.friendIds.Contains(424242);
            Pawn struck = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == deathDangerStruckStaff);
            bool staffOk = struck?.GetComp<CompStaff>() is { } ssc && ssc.assaultedTick >= 0 && now - ssc.assaultedTick is >= 2468 and < 4000;
            bool counters = g.stats.bodiesSent == deathDangerStats0.bodies + 5 && g.stats.buried == deathDangerStats0.buried + 7 && g.stats.condolences == deathDangerStats0.condolences + 3;
            RPLog.Message($"[RimPrison:test] saveload death and danger: marks {(marks ? "intact" : "changed")}, the letter {(letterOk ? "intact" : "changed")}, the fault {(g.story.bitterDeaths.Contains(424242) ? "kept" : "lost")}, the friend's grave {(formerOk ? "intact" : "changed")}, counters {(counters ? "intact" : "changed")}, the staff's note of a blow {(staffOk ? "intact" : "changed")}");
            if (!staffOk) reloadProblems.Add("the tick of an inmate's blow on a member of staff did not come back");
            if (!marks) reloadProblems.Add("the danger map's marks did not come back with their kind and tick");
            if (!letterOk) reloadProblems.Add("a family's letter on its way did not come back whole");
            if (!g.story.bitterDeaths.Contains(424242)) reloadProblems.Add("the note of the prison's fault was lost in the round trip");
            if (!formerOk) reloadProblems.Add("a former inmate's news about a grave did not come back");
            if (!counters) reloadProblems.Add("the counters of bodies sent, buried and letters were changed by the round trip");
            pm.incidents.RemoveAll(m => now - m.tick is >= 777 and < 2000 && m.kind == DangerKind.Stabbing || now - m.tick is >= 4321 and < 6000 && m.kind == DangerKind.Death);
            if (letter != null) g.story.condolences.Remove(letter);
            g.story.bitterDeaths.Remove(424242);
            if (former != null) g.story.formerInmates.Remove(former);
            (g.stats.bodiesSent, g.stats.buried, g.stats.condolences) = deathDangerStats0;
            if (struck?.GetComp<CompStaff>() is { } sc) sc.assaultedTick = -1;
        }

        /// <summary>The snapshot's part for the dead and the danger map.</summary>
        private static string DeathDangerDescribe(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            return $"{g.stats.bodiesSent}/{g.stats.buried}/{g.stats.condolences}:{string.Join(",", g.story.condolences.Select(c => c.Describe()))}:{string.Join(",", g.story.bitterDeaths)}:{string.Join(",", g.story.formerInmates.Select(e => e.name + "=" + e.graveName))}:{DangerMap.Describe(PrisonMap.Of(map))}";
        }
    }
}
