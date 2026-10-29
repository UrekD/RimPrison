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
    /// Checks for the psych wing (Programs/PsychWing.cs; gap analysis 4 section 1.6), called from one line of the events plan after its events: the level and its project and regime, the count of mental breaks,
    /// the psychologist's referral (two breaks in ten days, or a trait at the first assessment) into a padded cell, a break in a padded cell, the doctor's medication round, the cover that fades when a round is
    /// missed and the break that follows, and the stock of the drug. Each is self-contained: it builds a small unit of two cells on open ground outside the compound (<see cref="PsychScene"/>), uses throwaway
    /// inmates and staff, and puts back what it touched (<see cref="Rollback"/>). Each fails without the feature it is named for: they look at what only the feature does.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckPsychWing(Map map)
        {
            Rand.PushState(20261021);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns);
            string before = PsychDescribeWorld(map);
            try
            {
                CheckPsychDefs(map);
                CheckPsychBreaksAreCounted(map);
                CheckPsychReferral(map);
                CheckPsychNoCaseNoCell(map);
                CheckPsychTrait(map);
                CheckPsychHidesWhatIsHidden(map);
                CheckPsychPaddedCellHarmsNothing(map);
                CheckPsychMedicationRound(map);
                CheckPsychMissedRoundBreaks(map);
                CheckPsychStock(map);
                CheckPsychBreaksOnlyInTheCell(map);
                CheckPsychPaddedNeedsTheWing(map);
                CheckPsychGangExitSkipsPsych(map);
                CheckPsychCountsWhatThePrisonSaw(map);
                CheckPsychStepsOutWhereTheyCameFrom(map);
                CheckPsychTraitCaseKept(map);
                CheckPsychMedicsAndMessages(map);
                CheckPsychDoseReservation(map);
                CheckPsychNoIntakeForPsych(map);
            }
            finally
            {
                Rand.PopState();
            }
            Pawn[] left = LeftBehind(map, pawnsBefore).Where(x => x.Name is NameSingle && x.LabelShort.StartsWith("Zp")).ToArray();
            if (left.Length > 0) problems0.Add($"psych checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
            if (PsychDescribeWorld(map) != before) problems0.Add($"psych checks left the map, the regime or the project changed: {before} -> {PsychDescribeWorld(map)}");
        }

        /// <summary>What the checks change in the shared prison (walls, doors, beds on the map, the regime of the level, the projects done, the wing's cells), to see that they put it back.</summary>
        private static string PsychDescribeWorld(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            SecurityLevelDef level = RPPsychWingDefOf.RP_Psych;
            return $"{map.listerThings.ThingsOfDef(ThingDefOf.Wall).Count}/{map.listerThings.ThingsOfDef(ThingDefOf.Door).Count}/{map.listerThings.ThingsOfDef(RPPsychWingDefOf.RP_PaddedBed).Count}/"
                + $"{map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamed("Bed")).Count}/{map.listerThings.ThingsOfDef(RPPsychWingDefOf.RP_Antipsychotic).Count}/{g.bureaucracy.done.Count}/"
                + $"{pm.zones.CellsOf(RPPsychWingDefOf.RP_WingPsych).Count}/{string.Join(",", g.RegimeFor(level).slots.Select(s => s.defName.Replace("RP_", "")))}";
        }

        /// <summary>
        /// The prison's own psychologists (or doctors) do not take the check's inmates while the check runs: they would reserve them (and take the dose), and the check's own would find them taken. The work type is put to
        /// nothing for each of them and back by the rollback; what they were doing for it is ended.
        /// </summary>
        private static void QuietTheRealStaff(Map map, Rollback r, string workType)
        {
            WorkTypeDef type = DefDatabase<WorkTypeDef>.GetNamed(workType);
            foreach (Pawn real in map.mapPawns.FreeColonistsSpawned.Where(x => x.GetComp<CompStaff>()?.role?.workTypes.Contains(type) == true && !x.LabelShort.StartsWith("Zp") && x.workSettings != null).ToList())
            {
                int was = real.workSettings.GetPriority(type);
                r.Add(() =>
                {
                    if (!real.Destroyed && real.workSettings != null) real.workSettings.SetPriority(type, was);
                });
                real.workSettings.SetPriority(type, 0);
                if (real.CurJob?.def == RPPsychDefOf.RP_Therapy || real.CurJob?.def == RPPsychWingDefOf.RP_MedicationRound || real.CurJob?.def == RPDrugTestDefOf.RP_DrugTest) real.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
        }

        /// <summary>A line without the colour tags the game puts round numbers.</summary>
        private static string PsychPlain(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");

        private static void PsychFail(string check, string what) => problems0.Add($"psych ({check}): {what}");

        /// <summary>A small psych unit on open ground outside the compound: a padded cell (P), a plain cell (Q), a corridor, walled and roofed (see <see cref="CheckPsychWing"/>).</summary>
        private sealed class PsychScene
        {
            public const int Width = 9, Height = 7;
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly Rollback r;
            public readonly string check;
            public readonly SecurityLevelDef level = RPPsychWingDefOf.RP_Psych;
            public IntVec3 origin;
            public Building_Bed padded, plain;
            public Thing chairP, chairQ;
            public Building_Door doorP, doorQ;

            private PsychScene(Map map, Rollback r, string check)
            {
                this.map = map;
                this.r = r;
                this.check = check;
                pm = PrisonMap.Of(map);
            }

            public int Now => Find.TickManager.TicksGame;

            public IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);

            public IntVec3 CellP => At(2, 4);
            public IntVec3 CellQ => At(6, 4);
            public IntVec3 Corridor => At(2, 1);

            /// <summary>The unit, or null (a problem noted) when there is no open ground for it.</summary>
            public static PsychScene Build(Map map, Rollback r, string check)
            {
                var s = new PsychScene(map, r, check);
                // (The storyteller's life beats keep coming through the real ticks of a check, and one that picks the scene's inmate puts them in a bed: the day's beats are used up for the check, as the supermax scene's are.)
                StorySystem story = PrisonGame.Instance.story;
                int beats0 = story.beatsToday, day0 = GenLocalDate.DayOfYear(map);
                story.beatsToday = Math.Max(beats0, StoryPacing.MaxBeatsPerDay(map.mapPawns.PrisonersOfColonySpawnedCount));
                suppressedBeatsScenes++;
                r.Add(() =>
                {
                    suppressedBeatsScenes--;
                    if (GenLocalDate.DayOfYear(map) == day0) story.beatsToday = beats0;
                });
                s.origin = OpenGround(map);
                if (!s.origin.IsValid)
                {
                    PsychFail(check, $"no open ground outside the compound for the unit (footprint {PrisonBuilder.Footprint(map)}, map {map.Size})");
                    return null;
                }
                foreach (IntVec3 c in new CellRect(s.origin.x, s.origin.z, Width, Height).ExpandedBy(1))
                    foreach (Thing plant in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Plant).ToList())
                        plant.Destroy(DestroyMode.Vanish);
                s.Construct();
                return s;
            }

            private static IntVec3 OpenGround(Map map)
            {
                CellRect compound = PrisonBuilder.Footprint(map).ExpandedBy(3);
                PrisonMap pm = PrisonMap.Of(map);
                foreach (IntVec3 c in GenRadial.RadialCellsAround(PrisonBuilder.Footprint(map).CenterCell, 79f, true))
                {
                    CellRect around = new CellRect(c.x, c.z, Width, Height).ExpandedBy(1);
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

            private void Construct()
            {
                // The south and north walls, the wall between the corridor and the cells (doors at x 2 and 6), the two ends, and the wall between the cells.
                var doors = new HashSet<(int x, int z)> { (2, 2), (6, 2) };
                for (int x = 0; x < Width; x++)
                    foreach (int z in new[] { 0, 2, Height - 1 })
                        if (!doors.Contains((x, z))) Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(x, z));
                foreach (int x in new[] { 0, Width - 1 })
                    for (int z = 1; z < Height - 1; z++)
                        if (z != 2) Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(x, z));
                for (int z = 3; z < Height - 1; z++) Spawn<Thing>(ThingDefOf.Wall, ThingDefOf.BlocksGranite, At(4, z));
                doorP = Spawn<Building_Door>(ThingDefOf.Door, ThingDefOf.Steel, At(2, 2));
                doorQ = Spawn<Building_Door>(ThingDefOf.Door, ThingDefOf.Steel, At(6, 2));
                Building_Bed Bed(ThingDef def, int x)
                {
                    var bed = Spawn<Building_Bed>(def, ThingDefOf.WoodLog, At(x, 3), Rot4.North);
                    bed.ForPrisoners = true;
                    bed.GetRoom()?.Notify_RoomShapeChanged();
                    r.Add(() =>
                    {
                        foreach (Pawn owner in bed.OwnersForReading.ToList()) owner.ownership?.UnclaimBed();
                    });
                    return bed;
                }
                padded = Bed(RPPsychWingDefOf.RP_PaddedBed, 1);
                plain = Bed(DefDatabase<ThingDef>.GetNamed("Bed"), 5);
                ThingDef chair = DefDatabase<ThingDef>.GetNamed("DiningChair");
                chairP = Spawn<Thing>(chair, ThingDefOf.WoodLog, At(3, 4));
                chairQ = Spawn<Thing>(chair, ThingDefOf.WoodLog, At(7, 4));
                Paint(new CellRect(origin.x + 1, origin.z + 3, 3, 3), RPPsychWingDefOf.RP_WingPsych);
                // The roof last, taken off first (a roof left up when its walls came down collapses on whoever is there next).
                foreach (IntVec3 c in new CellRect(origin.x, origin.z, Width, Height))
                {
                    RoofDef was = map.roofGrid.RoofAt(c);
                    r.Add(() => map.roofGrid.SetRoof(c, was));
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                }
                map.reachability.ClearCache();
                pm.Notify_BedsChanged();
                pm.ForceDoorUpdate();
                r.Add(() =>
                {
                    map.reachability.ClearCache();
                    pm.Notify_BedsChanged();
                    pm.ForceDoorUpdate();
                });
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

            /// <summary>The plain cell painted as the psych wing too (put back by the rollback; <paramref name="on"/> false: unpainted now).</summary>
            public void PaintPlainCell(bool on)
            {
                Paint(new CellRect(origin.x + 5, origin.z + 3, 3, 3), on ? RPPsychWingDefOf.RP_WingPsych : null);
                pm.Notify_BedsChanged();
            }

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            /// <summary>An inmate of a level in a cell of the unit, calm, with a clean record, full needs, no wounds (a patient goes to bed for medical reasons before anything the regime says).</summary>
            public Pawn Inmate(string name, SecurityLevelDef sec, Building_Bed bed, IntVec3 at)
            {
                Pawn p = Throwaway(map, r, sec);
                p.Name = new NameSingle(name);
                CompInmate c = p.Inmate();
                c.security = sec;
                (c.processed, c.awaitingSearch, c.forcedActivity, c.pendingPunishmentHours, c.want, c.gang, c.gangKnown, c.gangLeader) = (true, false, null, 0, null, null, false, false);
                (c.tension, c.fear, c.cash, c.familyCount, c.visitor, c.snitchExposed, c.informantQuit, c.informantPaid) = (10f, 0f, 0, 0, null, false, false, false);
                (c.leg, c.escortRetryTick, c.lastReclassTick, c.assessed, c.lastTherapyTick) = (null, 0, Now - 20 * GenDate.TicksPerDay, true, Now);
                (c.psychCheckTick, c.unmedicatedHours, c.lastDoseTick) = (-999999, 0, -999999);
                c.breakTicks.Clear();
                c.records.Clear();
                c.reputations.Clear();
                c.revealed.Clear();
                c.relatives.Clear();
                c.staffMemories.Clear();
                ClearAddictions(p);
                p.health.Reset();
                if (p.story?.traits != null)
                    foreach (Trait t in p.story.traits.allTraits.Where(t => PsychWing.Ext?.traits.Contains(t.def) == true).ToList()) p.story.traits.RemoveTrait(t);
                c.RefreshActiveFlag();
                Place(p, at);
                if (bed != null) p.ownership.ClaimBedIfNonMedical(bed);
                if (p.needs.TryGetNeed(RPDefOf.RP_Bladder) is { } bladder) bladder.CurLevel = 1f;
                if (p.needs.rest != null) p.needs.rest.CurLevel = 1f;
                if (p.needs.food != null) p.needs.food.CurLevel = 1f;
                if (p.needs.TryGetNeed(RPDefOf.RP_Hygiene) is { } hygiene) hygiene.CurLevel = 1f;
                pm.ForceDoorUpdate();
                return p;
            }

            /// <summary>A member of staff on site, at work every hour, with no work of their own (the checks start what they want by hand).</summary>
            public Pawn Staff(string name, string role, IntVec3 at)
            {
                Pawn s = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed(role));
                s.Name = new NameSingle(name);
                if (s.timetable != null)
                    for (int hour = 0; hour < 24; hour++) s.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                if (s.needs?.rest != null) s.needs.rest.CurLevel = 1f;
                CompStaff sc = s.GetComp<CompStaff>();
                (sc.corrupt, sc.patron, sc.patronGang, sc.doubleAgent, sc.exposedCorrupt, sc.suspicion, sc.iaOpen, sc.suspendedUntilTick) = (false, null, null, false, false, 0f, false, -1);
                sc.evidence.Clear();
                s.health.Reset();
                if (s.workSettings != null)
                    foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefsListForReading) s.workSettings.SetPriority(work, 0);
                Place(s, at);
                return s;
            }

            public Thing Drug(IntVec3 at, int count)
            {
                Thing t = Spawn<Thing>(RPPsychWingDefOf.RP_Antipsychotic, null, at);
                t.stackCount = count;
                return t;
            }

            /// <summary>The project that opens psychiatric care, taken off or put on (put back by the rollback).</summary>
            public void Project(bool done)
            {
                BureaucracyDef project = Unlocks.GateOf(level);
                BureaucracySystem bureaucracy = g.bureaucracy;
                List<BureaucracyDef> done0 = bureaucracy.done.ToList();
                r.Add(() =>
                {
                    bureaucracy.done.Clear();
                    bureaucracy.done.AddRange(done0);
                });
                bureaucracy.done.Remove(project);
                if (done) bureaucracy.done.Add(project);
            }

            /// <summary>The level's schedule set to one activity all day (put back by the rollback).</summary>
            public void RegimeAllDay(SecurityLevelDef sec, RegimeActivityDef activity)
            {
                RegimeSchedule schedule = g.RegimeFor(sec);
                List<RegimeActivityDef> was = schedule.slots.ToList();
                r.Add(() =>
                {
                    for (int h = 0; h < 24; h++) schedule.Set(h, was[h]);
                });
                for (int h = 0; h < 24; h++) schedule.Set(h, activity);
            }

            /// <summary>Why somebody cannot get to somebody (for the text of a failure): the way, the doors, and what is on the pawn that could stop them.</summary>
            public string ReachDiagnostics(Pawn who, Pawn target)
            {
                string Door(Building_Door d)
                {
                    CompPrisonDoor dc = d.GetComp<CompPrisonDoor>();
                    return $"{d.Position - origin}: mode {dc?.mode}, held {dc?.heldCells != null}, open {d.Open}, forbidden {d.IsForbidden(who)}, can open {d.PawnCanOpen(who)}";
                }
                CompStaff sc = who.GetComp<CompStaff>();
                return $"{who.LabelShort} at {who.Position - origin} to {target.LabelShort} at {target.Position - origin}: reach {who.CanReach(target, PathEndMode.Touch, Danger.Deadly)}, spawned {who.Spawned}, downed {who.Downed}, mental {who.MentalStateDef?.defName}, "
                    + $"area {who.playerSettings?.AreaRestrictionInPawnCurrentMap?.Label}, passLocked {sc?.passLocked}, doors P ({Door(doorP)}) Q ({Door(doorQ)}), roof/walls ok {map.roofGrid.Roofed(CellP)}, room of the inmate {target.GetRoom()?.ID}, of the doctor {who.GetRoom()?.ID}";
            }

            /// <summary>A bed on the map (put back by the rollback), for the checks of where a padded bed pads.</summary>
            public Building_Bed ExtraBed(ThingDef def, IntVec3 cell)
            {
                var bed = Spawn<Building_Bed>(def, ThingDefOf.WoodLog, cell, Rot4.North);
                bed.ForPrisoners = true;
                r.Add(() =>
                {
                    foreach (Pawn owner in bed.OwnersForReading.ToList()) owner.ownership?.UnclaimBed();
                });
                pm.Notify_BedsChanged();
                pm.ForceDoorUpdate();
                return bed;
            }

            public T Thing<T>(ThingDef def, ThingDef stuff, IntVec3 cell) where T : Thing => Spawn<T>(def, stuff, cell);

            /// <summary>A bed taken off the map for the check and put back where it was by the rollback (a room with only one bed in it, for a while).</summary>
            public void TakeOut(Building_Bed bed)
            {
                (IntVec3 cell, Rot4 rot) = (bed.Position, bed.Rotation);
                foreach (Pawn owner in bed.OwnersForReading.ToList()) owner.ownership?.UnclaimBed();
                bed.DeSpawn();
                r.Add(() =>
                {
                    if (!bed.Spawned && !bed.Destroyed) GenSpawn.Spawn(bed, cell, map, rot);
                });
                pm.Notify_BedsChanged();
                pm.ForceDoorUpdate();
            }

            public bool InCell(Pawn p, Building_Bed bed) => p.GetRoom() is { } room && room == bed.GetRoom();
        }

        // ---- the level, its project and its regime ---------------------------------------------------------------------------------------------

        private static void CheckPsychDefs(Map map)
        {
            Guarded("psych: the level, its project and its regime", map, r =>
            {
                const string check = "defs";
                PrisonGame g = PrisonGame.Instance;
                SecurityLevelDef level = RPPsychWingDefOf.RP_Psych;
                if (level == null || !level.wingOnly || !level.paddedOnly || !level.referralOnly || level.cellMeals || level.escortOnly)
                    PsychFail(check, $"flags: wing only {level?.wingOnly}, padded only {level?.paddedOnly}, referral only {level?.referralOnly} (all set), cell meals {level?.cellMeals}, escort only {level?.escortOnly} (not set)");
                if (level == null) return;
                if (RPPsychWingDefOf.RP_WingPsych.security != level) PsychFail(check, "the wing zone names another level");
                // No workshop and no kitchen: not a work hour in the regime, and neither job takes one of them.
                List<RegimeActivityDef> slots = level.defaultRegime;
                if (slots.Count != 24 || slots.Any(a => a == RPLaborDefOf.RP_Work)) PsychFail(check, $"default regime: {slots.Count} hours, {slots.Count(a => a == RPLaborDefOf.RP_Work)} of them work (none expected)");
                PrisonZoneDef workshop = RPLaborDefOf.RP_Workshop, kitchen = RPLaborDefOf.RP_Kitchen;
                foreach (InmateJobDef job in DefDatabase<InmateJobDef>.AllDefsListForReading.Where(j => j.zone == workshop || j.zone == kitchen))
                {
                    Pawn probe = Throwaway(map, r, level);
                    CompInmate pc = probe.Inmate();
                    (pc.security, pc.tension, pc.forcedActivity) = (level, 0f, null);
                    if (LaborUtility.Eligible(pc, job, forProgram: true)) PsychFail(check, $"{job.defName} is open to inmates of psychiatric care");
                }
                // The project: after the psychology project, opening the level, the wing and the padded bed.
                BureaucracyDef project = Unlocks.GateOf(level), psychology = DefDatabase<BureaucracyDef>.GetNamed("RP_Bur_Psychology");
                if (project == null || !project.prerequisites.Contains(psychology) || Unlocks.GateOf(RPPsychWingDefOf.RP_WingPsych) != project || Unlocks.GateOf(RPPsychWingDefOf.RP_PaddedBed) != project)
                    PsychFail(check, $"the project {project?.defName ?? "(none)"} unlocks the level, the wing and the bed, after the psychology project");
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
                    bool closed = !Unlocks.IsUnlocked(level) && !Unlocks.IsUnlocked(RPPsychWingDefOf.RP_WingPsych) && !Unlocks.IsUnlocked(RPPsychWingDefOf.RP_PaddedBed);
                    bureaucracy.done.Add(project);
                    bool open = Unlocks.IsUnlocked(level) && Unlocks.IsUnlocked(RPPsychWingDefOf.RP_WingPsych) && Unlocks.IsUnlocked(RPPsychWingDefOf.RP_PaddedBed);
                    if (!closed || !open) PsychFail(check, $"the level, the wing and the bed are locked without the project ({closed}) and open with it ({open})");
                }
                // The state does not send them: the intake row is gone and nothing arrives on the roster.
                if (!level.referralOnly) PsychFail(check, "the state would send inmates to psychiatric care");
                // The doctor's work giver is the Doctor work type's.
                WorkGiverDef giver = DefDatabase<WorkGiverDef>.GetNamed("RP_MedicationRound");
                if (giver.workType != WorkTypeDefOf.Doctor || giver.Worker is not WorkGiver_MedicationRound) PsychFail(check, "the medication round is not a doctor's work giver");
            });
        }

        // ---- the count of breaks ---------------------------------------------------------------------------------------------------------------

        private static void CheckPsychBreaksAreCounted(Map map)
        {
            Guarded("psych: mental breaks are counted", map, r =>
            {
                const string check = "breaks";
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                c.breakTicks.Clear();
                c.lastReclassTick = Find.TickManager.TicksGame - 20 * GenDate.TicksPerDay;
                MentalStateDef sad = MentalStateDefOf.Wander_Sad;
                void Break(MentalStateDef def, bool mood, bool forced = true)
                {
                    p.mindState.mentalStateHandler.Reset();
                    p.mindState.mentalStateHandler.TryStartMentalState(def, forced: forced, causedByMood: mood);
                    p.mindState.mentalStateHandler.Reset();
                }
                r.Add(() => p.mindState.mentalStateHandler.Reset());
                Break(sad, mood: true);
                if (c.breakTicks.Count != 1 || PsychWing.BreaksInWindow(c) != 1) PsychFail(check, $"a mood break was not counted ({c.breakTicks.Count} on the record)");
                // Not breaks of a sick mind: a hostage held, a fight with a score behind it, a vanilla state with no mood behind it, and the mod's states however they are started (a riot is a protest, an assault
                // can be a restraint resisted or an old score; the spontaneous tempers the prison saw are counted where they are recorded: see CheckPsychCountsWhatThePrisonSaw).
                Break(RPHostageDefOf.RP_BeingHeld, mood: false);
                Break(RPMentalStateDefOf.RP_AttackingInmate, mood: false);
                Break(RPMentalStateDefOf.RP_Escaping, mood: false);
                Break(RPMentalStateDefOf.RP_Rioting, mood: false);
                Break(RPMentalStateDefOf.RP_Assaulting, mood: false);
                Break(RPMentalStateDefOf.RP_Vandalizing, mood: false);
                Break(sad, mood: false);
                if (c.breakTicks.Count != 1) PsychFail(check, $"states that are no break were counted ({c.breakTicks.Count} on the record, one expected)");
                // Breaks fall out of the window, and the ones before the last move were acted on.
                int now = Find.TickManager.TicksGame;
                c.breakTicks.Clear();
                c.breakTicks.AddRange(new[] { now - (PsychWing.WindowDays + 1) * GenDate.TicksPerDay, now - 2 * GenDate.TicksPerDay, now - GenDate.TicksPerDay });
                if (PsychWing.BreaksInWindow(c) != 2) PsychFail(check, $"a break eleven days old counted ({PsychWing.BreaksInWindow(c)} of 3 in the window, 2 expected)");
                c.lastReclassTick = now - GenDate.TicksPerDay - 100;
                c.breakTicks[1] = now - GenDate.TicksPerDay - 500;
                if (PsychWing.BreaksInWindow(c) != 1) PsychFail(check, $"a break from before the last move counted ({PsychWing.BreaksInWindow(c)}, 1 expected)");
                c.lastReclassTick = now - 20 * GenDate.TicksPerDay;
                Break(sad, mood: true);
                if (c.breakTicks.Any(t => now - t > PsychWing.WindowDays * GenDate.TicksPerDay + 10)) PsychFail(check, "an old break was kept on the record");
            });
        }

        // ---- the psychologist's referral -------------------------------------------------------------------------------------------------------

        /// <summary>Two breaks in ten days: the psychologist is sent (the real work giver and job, real ticks), sits with them, and they are in the padded cell, with the reason in the chronicle.</summary>
        private static void CheckPsychReferral(Map map)
        {
            Guarded("psych: two breaks in ten days and the psychologist moves them to a padded cell", map, r =>
            {
                const string check = "referral";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level, medium = DefDatabase<SecurityLevelDef>.GetNamed("RP_Medium");
                s.Project(true);
                s.RegimeAllDay(medium, RPDefOf.RP_Lockup);
                s.RegimeAllDay(level, RPDefOf.RP_Lockup);
                Pawn psychologist = s.Staff("Zpsy1", "RP_Psychologist", s.Corridor);
                QuietTheRealStaff(map, r, "RP_Psychology");
                int now = s.Now;
                Pawn a = s.Inmate("Zpa", medium, s.plain, s.CellQ);
                CompInmate ac = a.Inmate();
                // One break is nothing.
                ac.breakTicks.Add(now - 2 * GenDate.TicksPerDay);
                InmateCandidates.Shared.Invalidate();
                if (PsychologyUtility.WantsSession(ac)) PsychFail(check, "one break in ten days sent the psychologist");
                PsychologyUtility.Complete(psychologist, a);
                if (ac.security != medium) PsychFail(check, $"one break moved them to {ac.security?.defName}");
                // Two are a case.
                ac.breakTicks.Add(now - GenDate.TicksPerDay);
                InmateCandidates.Shared.Invalidate();
                if (!PsychologyUtility.WantsSession(ac)) PsychFail(check, "two breaks in ten days did not send the psychologist");
                if (!InmateCandidates.Shared.Therapy(map).Contains(a)) PsychFail(check, "the psychologist's list of inmates to see does not have them");
                WorkGiver_Scanner giver = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("RP_Therapy").Worker;
                if (!giver.HasJobOnThing(psychologist, a)) PsychFail(check, "the psychologist is offered no job with them");
                else
                {
                    int entries0 = r.NewEntries.Count();
                    if (!psychologist.CanReach(a, PathEndMode.Touch, Danger.Deadly)) PsychFail(check, "the psychologist cannot reach them: " + s.ReachDiagnostics(psychologist, a));
                    psychologist.jobs.StartJob(giver.JobOnThing(psychologist, a), JobCondition.InterruptForced);
                    // (The psychologist is a made-up member of staff on the live clock: an hour that turns while they walk to the inmate can send them off on a staff break, or the inmate's own regime can take them
                    // away from where they were met. A job that ended without the move is given again, up to three times; what ended it is in the line if it still does not come.)
                    string psyJobs = "";
                    string lastJob = psychologist.CurJobDef?.defName;
                    int ticked = 0, given = 1;
                    while (ac.security != level && ticked < 3000)
                    {
                        ticked += ShuttleTicks(() =>
                        {
                            if (psychologist.CurJobDef?.defName != lastJob)
                            {
                                lastJob = psychologist.CurJobDef?.defName;
                                psyJobs += $" [{ShNow}: {lastJob ?? "none"} at {psychologist.Position}, the inmate at {a.Position} doing {a.CurJobDef?.defName}]";
                            }
                            return ac.security == level || psychologist.CurJobDef != RPPsychDefOf.RP_Therapy;
                        }, 3000 - ticked);
                        if (ac.security != level && ticked < 3000 && given < 3 && giver.HasJobOnThing(psychologist, a))
                        {
                            psychologist.jobs.StartJob(giver.JobOnThing(psychologist, a), JobCondition.InterruptForced);
                            given++;
                        }
                        else if (ac.security != level) break;
                    }
                    if (ac.security != level) psyJobs = $" (the job given {given} times; the psychologist's jobs{psyJobs})";
                    else psyJobs = "";
                    if (ac.security != level || a.ownership?.OwnedBed != s.padded)
                        PsychFail(check, $"after {ticked} ticks{psyJobs} they are in {ac.security?.defName}, reserved by [{string.Join(", ", map.reservationManager.ReservationsReadOnly.Where(res => res.Target.Thing == a).Select(res => res.Claimant.LabelShort + " " + res.Job?.def.defName))}], bed {a.ownership?.OwnedBed?.def.defName} (psych and the padded bed expected); the psychologist is {psychologist.CurJob?.def.defName} at {psychologist.Position - s.origin}; the inmate is at {a.Position - s.origin} doing {a.CurJob?.def.defName}, awake {a.Awake()}, downed {a.Downed}, mental {a.MentalStateDef?.defName}, can talk {PsychologyUtility.CanTalk(a)}, wants a session {PsychologyUtility.WantsSession(ac)}, breaks {PsychWing.BreaksInWindow(ac)}, case \"{PsychWing.CaseFor(a, ac)}\", cannot move \"{PsychWing.CannotMove(a, ac)}\", last check {ac.psychCheckTick} (now {s.Now}), hour {GenLocalDate.HourOfDay(map)}");
                    if (s.plain.OwnersForReading.Contains(a)) PsychFail(check, "the plain cell is still theirs");
                    string line = r.NewEntries.Skip(entries0).Select(e => e.text).FirstOrDefault(t => t.Contains("psychiatric care")) ?? "";
                    if (!PsychPlain(line).Contains("2 mental breaks in 10 days")) PsychFail(check, $"the chronicle line does not give the reason (\"{line}\")");
                    InmateCandidates.Shared.Invalidate();
                    if (PsychologyUtility.WantsSession(ac) && ac.breakTicks.Count < 2) PsychFail(check, "they are still wanted for a referral after the move");
                }
            });
        }

        /// <summary>What is no case, and what cannot be acted on: old breaks, breaks from before the last move, no padded cell free (a plain bed in the wing is not one), the project not done.</summary>
        private static void CheckPsychNoCaseNoCell(Map map)
        {
            Guarded("psych: old breaks, no padded cell and no project make no move", map, r =>
            {
                const string check = "no case";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level, medium = DefDatabase<SecurityLevelDef>.GetNamed("RP_Medium");
                s.Project(true);
                Pawn psychologist = s.Staff("Zpsy2", "RP_Psychologist", s.Corridor);
                int now = s.Now;
                Pawn b = s.Inmate("Zpb", medium, s.plain, s.CellQ);
                CompInmate bc = b.Inmate();
                // Breaks eleven and twelve days old, and two from before the last move, are no case.
                bc.breakTicks.AddRange(new[] { now - 11 * GenDate.TicksPerDay, now - 12 * GenDate.TicksPerDay });
                InmateCandidates.Shared.Invalidate();
                if (PsychWing.CaseFor(b, bc) != null || PsychologyUtility.WantsSession(bc)) PsychFail(check, "two breaks older than ten days make a case");
                bc.breakTicks.Clear();
                bc.breakTicks.AddRange(new[] { now - 3 * GenDate.TicksPerDay, now - 2 * GenDate.TicksPerDay });
                bc.lastReclassTick = now - GenDate.TicksPerDay;
                if (PsychWing.CaseFor(b, bc) != null) PsychFail(check, "breaks from before the last move make a case");
                bc.lastReclassTick = now - 20 * GenDate.TicksPerDay;
                // A plain bed in a cell painted as the wing is no padded cell: of the two beds painted, one is free; and an inmate who lives in the plain cell is not already housed there.
                b.ownership.UnclaimBed(); // (their bed is free for the count)
                s.PaintPlainCell(true);
                int freeBeds = IntakeSystem.FreeBedCount(map, level);
                if (freeBeds != 1) PsychFail(check, $"{freeBeds} beds are free for psychiatric care (the padded one only: 1 expected)");
                if (InmateUtility.BedFitsLevel(s.plain, level)) PsychFail(check, "a plain bed in the psych wing is a proper home for psychiatric care");
                s.PaintPlainCell(false);
                b.ownership.ClaimBedIfNonMedical(s.plain);
                // The padded cell is taken: they stay where they are, nobody is sent for them, and the session that finds the case marks it as looked at.
                Pawn other = s.Inmate("Zpo", level, s.padded, s.CellP);
                InmateCandidates.Shared.Invalidate();
                if (PsychWing.CaseFor(b, bc) == null) PsychFail(check, "two breaks in ten days make no case");
                if (PsychologyUtility.WantsSession(bc)) PsychFail(check, "no padded cell is free and the psychologist is sent all the same");
                PsychologyUtility.Complete(psychologist, b);
                if (bc.security != medium || b.ownership?.OwnedBed != s.plain) PsychFail(check, $"moved with no padded cell free ({bc.security?.defName}, bed {b.ownership?.OwnedBed?.def.defName})");
                if (bc.psychCheckTick != s.Now) PsychFail(check, "the case that could not be moved was not marked as looked at");
                // The cell is free again, but it is looked at again only after two days; then they are sent for.
                other.ownership.UnclaimBed();
                other.Inmate().security = medium;
                s.pm.Notify_BedsChanged();
                InmateCandidates.Shared.Invalidate();
                if (PsychologyUtility.WantsSession(bc)) PsychFail(check, "looked at again the same hour");
                bc.psychCheckTick = s.Now - (PsychWing.CheckDays * GenDate.TicksPerDay + 10);
                InmateCandidates.Shared.Invalidate();
                if (!PsychologyUtility.WantsSession(bc)) PsychFail(check, "not looked at again two days after the last look, with a cell free");
                // The project is not done: nothing happens, whatever the breaks.
                s.Project(false);
                InmateCandidates.Shared.Invalidate();
                if (PsychologyUtility.WantsSession(bc)) PsychFail(check, "the psychologist is sent for a referral the project does not allow");
                PsychologyUtility.Complete(psychologist, b);
                if (bc.security != medium) PsychFail(check, "moved to psychiatric care without the project");
                if (PsychWing.CannotMove(b, bc) == null) PsychFail(check, "the wing is open without its project");
            });
        }

        /// <summary>A fitting trait at the first assessment is a case; at any later session it is not (a stepped-down inmate is not sent back for what they are).</summary>
        private static void CheckPsychTrait(Map map)
        {
            Guarded("psych: a fitting trait at the first assessment is a case", map, r =>
            {
                const string check = "trait";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level, medium = DefDatabase<SecurityLevelDef>.GetNamed("RP_Medium");
                s.Project(true);
                Pawn psychologist = s.Staff("Zpsy3", "RP_Psychologist", s.Corridor);
                TraitDef pyro = PsychWing.Ext?.traits.FirstOrDefault();
                if (pyro == null)
                {
                    PsychFail(check, "the level names no trait");
                    return;
                }
                // A trait the level does not name is no case.
                Pawn plainOne = s.Inmate("Zpt0", medium, s.plain, s.CellQ);
                plainOne.Inmate().assessed = false;
                if (PsychWing.FittingTrait(plainOne) != null) PsychFail(check, "an inmate with none of the traits has a fitting one");
                PsychologyUtility.Complete(psychologist, plainOne);
                if (plainOne.Inmate().security != medium) PsychFail(check, "an inmate with no fitting trait was moved");
                plainOne.ownership.UnclaimBed();
                // The trait at a later session: nothing. At the first assessment: the move.
                Pawn t = s.Inmate("Zpt1", medium, s.plain, s.CellQ);
                CompInmate tc = t.Inmate();
                t.story.traits.GainTrait(new Trait(pyro, 0, true));
                r.Add(() => t.story.traits.allTraits.RemoveAll(x => x.def == pyro));
                tc.assessed = true;
                PsychologyUtility.Complete(psychologist, t);
                if (tc.security != medium) PsychFail(check, "a fitting trait moved them at a later session");
                tc.assessed = false;
                int entries0 = r.NewEntries.Count();
                PsychologyUtility.Complete(psychologist, t);
                if (tc.security != level || t.ownership?.OwnedBed != s.padded) PsychFail(check, $"a fitting trait at the first assessment did not move them ({tc.security?.defName})");
                string line = r.NewEntries.Skip(entries0).Select(e => e.text).FirstOrDefault(x => x.Contains("psychiatric care")) ?? "";
                string traitLabel = t.story.traits.GetTrait(pyro)?.Label ?? "";
                if (traitLabel.NullOrEmpty() || !PsychPlain(line).Contains(traitLabel)) PsychFail(check, $"the chronicle line does not name the trait \"{traitLabel}\" (\"{line}\")");
            });
        }

        /// <summary>The referral names the breaks or the trait and nothing the staff have not learned: no hidden reputation, no unknown gang.</summary>
        private static void CheckPsychHidesWhatIsHidden(Map map)
        {
            Guarded("psych: the referral names nothing the staff have not learned", map, r =>
            {
                const string check = "hidden";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef medium = DefDatabase<SecurityLevelDef>.GetNamed("RP_Medium");
                s.Project(true);
                Pawn psychologist = s.Staff("Zpsy4", "RP_Psychologist", s.Corridor);
                ReputationDef rep = DefDatabase<ReputationDef>.AllDefsListForReading.FirstOrDefault(x => x.hidden);
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                Pawn a = s.Inmate("Zph", medium, s.plain, s.CellQ);
                CompInmate ac = a.Inmate();
                if (rep != null) ac.reputations.Add(rep);
                if (gang != null) (ac.gang, ac.gangKnown) = (gang, false);
                // (An assessment reveals hidden facts with a chance, and a fact it has revealed may be named: this one is an old inmate's session, so the chance is the low one, and the check looks at the line, not at the reveals.)
                ac.assessed = true;
                ac.breakTicks.AddRange(new[] { s.Now - 2 * GenDate.TicksPerDay, s.Now - GenDate.TicksPerDay });
                int entries0 = r.NewEntries.Count();
                var letters0 = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
                PsychologyUtility.Complete(psychologist, a);
                if (ac.security != s.level) PsychFail(check, "no referral to look at");
                IEnumerable<string> said = r.NewEntries.Skip(entries0).Select(e => e.text).Concat(Find.LetterStack.LettersListForReading.Where(l => !letters0.Contains(l)).Select(l => l.Label.ToString() + " " + ((l as ChoiceLetter)?.Text.ToString() ?? "")));
                foreach (string text in said.Where(t => t.Contains("psychiatric care")))
                {
                    if (rep != null && !ac.revealed.Contains(rep) && text.Contains(rep.label)) PsychFail(check, $"the line names the hidden reputation \"{rep.label}\": {text}");
                    if (gang != null && !ac.gangKnown && text.Contains(gang.label)) PsychFail(check, $"the line names the unknown gang \"{gang.label}\": {text}");
                }
            });
        }

        // ---- a break in a padded cell -------------------------------------------------------------------------------------------------------------

        /// <summary>A pawn in a mental break in a padded cell hurts nothing and no one but a person in the cell with them: the chair, themself, a fire. In a plain cell they do. The vandalism (the mod's own break, the one that smashes prison property) is the real thing, in real ticks.</summary>
        private static void CheckPsychPaddedCellHarmsNothing(Map map)
        {
            Guarded("psych: a break in a padded cell harms neither the inmate nor the things in it", map, r =>
            {
                const string check = "padded";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level, medium = DefDatabase<SecurityLevelDef>.GetNamed("RP_Medium");
                s.RegimeAllDay(level, RPDefOf.RP_Lockup);
                s.RegimeAllDay(medium, RPDefOf.RP_Lockup);
                Pawn a = s.Inmate("Zpp1", level, s.padded, s.CellP);
                Pawn b = s.Inmate("Zpp2", medium, s.plain, s.CellQ);
                Pawn guard = s.Staff("Zpg1", "RP_Guard", s.At(3, 3));
                MentalStateDef sick = MentalStateDefOf.Wander_Psychotic;
                foreach (Pawn x in new[] { a, b })
                {
                    x.mindState.mentalStateHandler.TryStartMentalState(sick, forced: true, causedByMood: true);
                    r.Add(() => x.mindState.mentalStateHandler.Reset());
                }
                if (!a.InMentalState || !b.InMentalState) PsychFail(check, "the throwaways did not break");
                if (a.ownership?.OwnedBed != s.padded || b.ownership?.OwnedBed != s.plain) PsychFail(check, $"set-up: the beds are not theirs (padded: {a.ownership?.OwnedBed?.def.defName}, plain: {b.ownership?.OwnedBed?.def.defName})");
                int Hp(Thing t) => t.HitPoints;
                // Blows: on the chair, on themself, on a person in the cell.
                int chairP0 = Hp(s.chairP), chairQ0 = Hp(s.chairQ);
                s.chairP.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 20f, instigator: a));
                s.chairQ.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 20f, instigator: b));
                if (Hp(s.chairP) != chairP0) PsychFail(check, $"a chair in a padded cell took {chairP0 - Hp(s.chairP)} damage from a pawn in a break");
                if (Hp(s.chairQ) >= chairQ0) PsychFail(check, "the control chair in the plain cell took none (the check measures nothing)");
                // (Hurt: a wound, or a part the blow took off - a blow of 12 on a toe or a finger destroys it, which is no Hediff_Injury.)
                bool Hurt(Pawn p) => p.health.hediffSet.hediffs.Any(h => h is Hediff_Injury or Hediff_MissingPart);
                string Hediffs(Pawn p) => string.Join(", ", p.health.hediffSet.hediffs.Select(h => h.def.defName + (h.Part != null ? " on " + h.Part.def.defName : "")));
                DamageWorker.DamageResult onA = a.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 12f, 1f, instigator: a));
                DamageWorker.DamageResult onB = b.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 12f, 1f, instigator: b));
                if (Hurt(a)) PsychFail(check, $"a pawn in a break in a padded cell hurt themself (dealt {onA?.totalDamageDealt}; {Hediffs(a)})");
                if (!Hurt(b)) PsychFail(check, $"the control pawn in the plain cell was not hurt by their own blow (the check measures nothing; dealt {onB?.totalDamageDealt}, deflected {onB?.deflected}, hediffs: {Hediffs(b)})");
                // (Bare: a guard's vest or helmet could take the blow, whatever its penetration.)
                guard.apparel?.DestroyAll();
                DamageWorker.DamageResult onGuard = guard.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 12f, 1f, instigator: a));
                if (!Hurt(guard)) PsychFail(check, $"a person in the padded cell was shielded from the pawn in the break (dealt {onGuard?.totalDamageDealt}, wounded {onGuard?.wounded}, guard at {guard.Position - s.origin} in the cell {guard.GetRoom() == a.GetRoom()}, dead {guard.Dead}, apparel {guard.apparel?.WornApparelCount})");
                // A blow of someone who is not in a break is nobody's business here.
                int chairP1 = Hp(s.chairP);
                s.chairP.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 20f, instigator: guard));
                if (Hp(s.chairP) >= chairP1) PsychFail(check, "a chair in a padded cell was shielded from someone who is not in a break");
                // A fire: nothing is lit in the padded cell, and a pawn in a break cannot set themself alight there.
                ThingDef wood = ThingDefOf.WoodLog;
                foreach (IntVec3 cell in new[] { s.At(3, 5), s.At(7, 5) })
                {
                    Thing logs = ThingMaker.MakeThing(wood);
                    logs.stackCount = 20;
                    GenSpawn.Spawn(logs, cell, map);
                    r.Add(() =>
                    {
                        if (logs.Spawned) logs.Destroy(DestroyMode.Vanish);
                    });
                }
                r.Add(() =>
                {
                    foreach (Fire f in map.listerThings.ThingsOfDef(ThingDefOf.Fire).OfType<Fire>().Where(f => s.map != null && new CellRect(s.origin.x, s.origin.z, PsychScene.Width, PsychScene.Height).Contains(f.Position)).ToList()) f.Destroy(DestroyMode.Vanish);
                });
                if (FireUtility.TryStartFireIn(s.At(3, 5), map, 1f, a)) PsychFail(check, "a pawn in a break lit a fire in the padded cell");
                a.TryAttachFire(0.5f, a);
                if (a.HasAttachment(ThingDefOf.Fire)) PsychFail(check, "a pawn in a break set themself alight in the padded cell");
                if (!FireUtility.TryStartFireIn(s.At(7, 5), map, 1f, b)) PsychFail(check, "the control fire in the plain cell did not start (the check measures nothing)");
                // The fires lit above are put out (the control's would burn the control's chair, and the check is about blows).
                foreach (IntVec3 cell in new[] { s.At(3, 5), s.At(7, 5) })
                    foreach (Fire f in map.thingGrid.ThingsListAt(cell).OfType<Fire>().ToList()) f.Destroy(DestroyMode.Vanish);
                // The vandalism, for real (the mod's own break: they go for a chair and smash it, real ticks): the one in the plain cell wrecks theirs; the one in the padded cell goes on swinging at theirs and it stays whole.
                foreach (Pawn x in new[] { a, b })
                {
                    x.mindState.mentalStateHandler.Reset();
                    x.health.Reset(); // (the blows above left their marks: the break is judged on its own)
                }
                foreach (Thing chair in new[] { s.chairP, s.chairQ }) chair.HitPoints = chair.MaxHitPoints;
                foreach ((Pawn x, Thing chair) in new[] { (a, s.chairP), (b, s.chairQ) })
                {
                    s.Place(x, chair.Position + IntVec3.West);
                    if (!x.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Vandalizing, null, forced: true, otherPawn: null)) PsychFail(check, $"{x.LabelShort} could not start vandalizing");
                    else if (x.MentalState is MentalState_Vandalizing v) v.target = chair;
                }
                int swingsA = 0, swingsB = 0;
                ShuttleTicks(() =>
                {
                    if (a.CurJob?.def == JobDefOf.AttackMelee) swingsA++;
                    if (b.CurJob?.def == JobDefOf.AttackMelee) swingsB++;
                    return false;
                }, 1500);
                // (Both must really be at it, or nothing was kept from the one in the padded cell.)
                if (swingsA == 0) PsychFail(check, $"the inmate in the padded cell never swung at the chair in 1500 ticks (a {a.CurJob?.def.defName}, mental {a.MentalStateDef?.defName}; the check measures nothing)");
                if (s.chairQ.Destroyed || s.chairQ.HitPoints >= s.chairQ.MaxHitPoints) PsychFail(check, $"the control in the plain cell never hurt their chair in 1500 ticks (swung {swingsB} ticks; a {b.CurJob?.def.defName}, mental {b.MentalStateDef?.defName}; the check measures nothing)");
                if (s.chairP.Destroyed || s.chairP.HitPoints < s.chairP.MaxHitPoints) PsychFail(check, $"the break in the padded cell hurt the chair ({s.chairP.HitPoints} of {s.chairP.MaxHitPoints}, swung {swingsA} ticks)");
                if (a.health.hediffSet.hediffs.OfType<Hediff_Injury>().Any(h => h.Severity > 0)) PsychFail(check, "the break in the padded cell hurt the inmate");
            });
        }
    }
}
