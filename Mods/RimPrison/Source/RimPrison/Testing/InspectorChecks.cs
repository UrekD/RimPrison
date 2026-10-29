using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The inspector who itemises (Story/Inspector.cs): the visit is a walk (a visiting pawn on the overseer's mechanism: a wing, the canteen, the kitchen, the yard), the report has up to five findings, each only what the warden
    /// has to put right (read here from a state the check makes, and what the prison's routine clears is shown not to be written), with a deadline each; they are lines of the to-do band until put right; the re-inspection grants a sum for
    /// each one put right and kept so through the last day and fines for each one not; a bribed inspector writes none, and the exposure letter names what was left out. Self-contained on the live prison with the inspector's book
    /// swapped for an empty one (<see cref="InspectorBegin"/>) and put back, throwaway inmates and bodies, the rooms a check empties painted again. The round trip of the book is in the saveload plan
    /// (<see cref="SeedInspectorForReload"/>). Premises the live prison may not give are told, not failed.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The longest to-do line that shows whole in the overview's list at 1280 x 720 (about 850 px of 5.7 px characters, with room to spare).</summary>
        private const int InspectorLineMax = 125;

        private static void InspectorFail(string check, string what) => problems0.Add($"inspector ({check}): {what}");

        private static void CheckInspector(Map map)
        {
            CheckInspectorWalk(map);
            CheckInspectorBodies(map);
            CheckInspectorUnlit(map);
            CheckInspectorNight(map);
            CheckInspectorRoutineIsNotWritten(map);
            CheckInspectorRooms(map);
            CheckInspectorBareCells(map);
            CheckInspectorOrderAndCap(map);
            CheckInspectorNotice(map);
            CheckInspectorHold(map);
            CheckInspectorTodo(map);
            CheckInspectorReinspection(map);
            CheckInspectorBribe(map);
        }

        /// <summary>The inspector's book swapped for an empty one (and no inspection announced), all of it and the visitors the check brought put back and let go when the check is done.</summary>
        private static void InspectorBegin(PrisonGame g, Map map, Rollback r)
        {
            StorySystem s = g.story;
            InspectionState book0 = s.inspection;
            (int tick0, int prep0, int expose0) = (s.inspectionTick, s.inspectionPrep, s.bribeExposeTick);
            s.inspection = new InspectionState();
            (s.inspectionTick, s.inspectionPrep, s.bribeExposeTick) = (-1, 0, -1);
            r.Add(() =>
            {
                InspectorLetGo(map);
                s.inspection = book0;
                (s.inspectionTick, s.inspectionPrep, s.bribeExposeTick) = (tick0, prep0, expose0);
                InspectionReport.TestStrip = null;
            });
        }

        /// <summary>The inspectors on the map are gone (they would leave on their own after their hours).</summary>
        private static void InspectorLetGo(Map map)
        {
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.GetComp<CompStaff>()?.role == RPInspectorDefOf.RP_Inspector).ToList())
            {
                p.GetComp<CompStaff>().role = null;
                if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
            }
            StaffCommute.StaffVersion++;
            StaffCareers.InvalidateLeads();
        }

        private static int InspectorNow() => Find.TickManager.TicksGame;

        /// <summary>
        /// The clock moved on to day 10 (and put back when the check is rolled back; no tick runs in between). The checks live in the first days of a game, and a finding put right "two days ago" would have a negative "gone since",
        /// which is not put right at all.
        /// </summary>
        private static void InspectorClockOn(Rollback r)
        {
            int clock0 = Find.TickManager.TicksGame;
            r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
            Find.TickManager.DebugSetTicksGame(System.Math.Max(clock0, 10 * GenDate.TicksPerDay));
        }

        /// <summary>The letter says the words (a letter capitalises the first letter of a line: "- Bodies lie" for "bodies lie").</summary>
        private static bool InspectorSays(string text, string words) => text != null && words != null && text.IndexOf(words, System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static InspectionFinding InspectorCandidate(PrisonGame g, Map map, string key) => InspectionReport.Candidates(g, map).FirstOrDefault(f => f.key == key);

        private static string InspectorLetterText(Rollback r, string labelPart) =>
            r.NewLetters.OfType<ChoiceLetter>().LastOrDefault(l => l.Label.ToString().Contains(labelPart))?.Text.ToString();

        // ---- seeds: a state that gives each kind of finding -----------------------------------------------------------------------------------------------

        /// <summary>A body of the prison's own that has lain outside the morgue for two hours.</summary>
        private static Corpse InspectorSeedBody(Map map, Rollback r)
        {
            Corpse c = FixCorpse(map, r, Throwaway(map, r), IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)));
            c.timeOfDeath = InspectorNow() - 2 * GenDate.TicksPerHour;
            Morgue.Reset();
            return c;
        }

        /// <summary>An inmate who is starving.</summary>
        private static Pawn InspectorSeedHungry(Map map, Rollback r)
        {
            Pawn p = Throwaway(map, r);
            p.needs.food.CurLevel = 0f;
            return p;
        }

        /// <summary>A complaint whose letter went out <paramref name="days"/> days ago and has not been answered.</summary>
        private static Complaint InspectorSeedComplaint(PrisonGame g, Rollback r, float days)
        {
            int now = InspectorNow();
            var cm = new Complaint
            {
                id = 990001 + g.story.complaints.Count,
                officerName = "Seeded Officer",
                inmateName = "Seeded Inmate",
                kind = ComplaintKind.Force,
                stage = ComplaintStage.Waiting,
                filedTick = now - Mathf.RoundToInt(days * GenDate.TicksPerDay) - GenDate.TicksPerHour,
                dueTick = now - Mathf.RoundToInt(days * GenDate.TicksPerDay),
                spot = new IntVec3(10, 0, 11),
            };
            g.story.complaints.Add(cm);
            r.Add(() => g.story.complaints.Remove(cm));
            return cm;
        }

        /// <summary>The shower room unpainted (painted again by the rollback); its cells, or null when there is none to take away.</summary>
        private static List<IntVec3> InspectorSeedNoShowers(Map map, Rollback r)
        {
            PrisonMap pm = PrisonMap.Of(map);
            List<IntVec3> cells = pm.zones.CellsOf(RPDefOf.RP_Shower).ToList();
            if (cells.Count == 0) return null;
            r.Add(() =>
            {
                foreach (IntVec3 c in cells) pm.zones.Set(c, RPDefOf.RP_Shower);
            });
            foreach (IntVec3 c in cells) pm.zones.Set(c, null);
            return cells;
        }

        /// <summary>One cell of the prison given grade 0 with an inmate in it who is meant for better (entitlement 1 at least); both put back by the rollback. Null when no cell has a grade to take down and an inmate the ladder covers.</summary>
        private static CompInmate InspectorSeedBareCell(Map map, Rollback r)
        {
            PrisonMap pm = PrisonMap.Of(map);
            foreach (Building_Bed bed in pm.PrisonerBeds)
            {
                if (!bed.Spawned || CellQuality.GradeAt(bed) <= 0 || PaddedCells.IsPadded(bed) || PunishmentUtility.IsSolitaryBed(bed) || bed.GetRoom() is not { } room) continue;
                CompInmate owner = bed.OwnersForReading.Select(o => o.Inmate()).FirstOrDefault(c => c != null && CellQuality.UnderLadder(c));
                if (owner == null) continue;
                (int grade, int entitlement) = (pm.CellGrades[room], owner.entitlement);
                pm.CellGrades[room] = 0;
                owner.entitlement = Mathf.Max(1, entitlement);
                r.Add(() =>
                {
                    pm.CellGrades[room] = grade;
                    owner.entitlement = entitlement;
                });
                return owner;
            }
            return null;
        }

        private const int FilthPieces = 20;

        /// <summary>A good deal of filth in the home area, enough that the first version of the inspector wrote it down (the pieces are destroyed by the rollback); false when the home area has no room for it.</summary>
        private static bool InspectorSeedFilth(Map map, Rollback r)
        {
            var made = new List<Thing>();
            r.Add(() =>
            {
                foreach (Thing t in made)
                    if (!t.Destroyed) t.Destroy();
            });
            int missing = FilthPieces + 2 - map.listerFilthInHomeArea.FilthInHomeArea.Count;
            foreach (IntVec3 c in map.areaManager.Home.ActiveCells.Where(x => x.Standable(map) && x.GetFirstBuilding(map) == null && !x.GetThingList(map).Any(t => t is Filth)))
            {
                if (missing <= 0) break;
                if (FilthMaker.TryMakeFilth(c, map, ThingDefOf.Filth_Dirt) && c.GetThingList(map).OfType<Filth>().FirstOrDefault() is { } f)
                {
                    made.Add(f);
                    missing--;
                }
            }
            return map.listerFilthInHomeArea.FilthInHomeArea.Count >= FilthPieces;
        }

        // ---- the walk -------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A visit: an inspector of their own role is on the map, not on the payroll, leaves within the visit's hours, and has a walk queued whose stops are a bed in an ordinary wing, the canteen, the kitchen and the yard; a second
        /// visit while they are still here keeps the same one (and settles the first one's findings, which a visit with a re-inspection to come must not drop). The report has at most five findings, one cause to each, none put right
        /// yet, each due at the re-inspection's day, and the re-inspection is five days on (none with nothing written down).
        /// </summary>
        private static void CheckInspectorWalk(Map map)
        {
            Guarded("inspector: the walk and the report's shape", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                PrisonMap pm = PrisonMap.Of(map);
                // (A body outside the morgue: so that the report has something in it whatever the live prison gives.)
                InspectorSeedBody(map, r);
                int now = InspectorNow();
                Inspections.Inspect(g, map);
                InspectionState book = g.story.inspection;
                Pawn p = book.inspector;
                CompStaff sc = p?.GetComp<CompStaff>();
                if (p is not { Spawned: true } || sc?.role != RPInspectorDefOf.RP_Inspector)
                {
                    InspectorFail("the walk", $"no inspector on the map after the visit (pawn {p != null}, role {sc?.role?.defName})");
                    return;
                }
                if (sc.leaveAtTick <= now || sc.leaveAtTick > now + InspectionReport.VisitHours * GenDate.TicksPerHour + 5) InspectorFail("the walk", $"the inspector leaves at {sc.leaveAtTick - now} ticks, not within the visit's {InspectionReport.VisitHours} hours");
                if (StaffCareers.Payroll(new List<Pawn> { p }) != 0) InspectorFail("the walk", "the visiting inspector is paid");
                // The walk: the stops in turn (the job under way and the ones queued).
                var dests = new List<IntVec3>();
                if (p.CurJob is { def: var cur } curJob && cur == JobDefOf.Goto) dests.Add(curJob.targetA.Cell);
                for (int i = 0; i < p.jobs.jobQueue.Count; i++) dests.Add(p.jobs.jobQueue[i].job.targetA.Cell);
                int wanted = InspectionReport.Stops(map).Count;
                if (wanted < 3) RPLog.Message($"[RimPrison:test] inspector: the live prison gives the walk only {wanted} stops (a wing, the canteen, the kitchen, the yard are not all there)");
                else if (dests.Count < 2) InspectorFail("the walk", $"the inspector walks {dests.Count} stops of the {wanted} the prison gives");
                foreach (IntVec3 d in dests)
                {
                    PrisonZoneDef zone = pm.zones.ZoneAt(d);
                    Building_Bed bed = pm.PrisonerBeds.FirstOrDefault(b => b.Spawned && b.InteractionCell == d);
                    bool ok = zone == RPDefOf.RP_Canteen || zone == RPLaborDefOf.RP_Kitchen || zone == RPDefOf.RP_Yard || bed != null;
                    if (!ok) InspectorFail("the walk", $"a stop at {d} is none of a wing's bed, the canteen, the kitchen or the yard (zone {zone?.defName})");
                    else if (bed != null && (IntakeSystem.WingOf(bed) is { wingOnly: true } || PunishmentUtility.IsSolitaryBed(bed) || PaddedCells.IsPadded(bed))) InspectorFail("the walk", $"the wing stop is a bed of {IntakeSystem.WingOf(bed)?.defName ?? "a solitary or padded cell"}: supermax, protective custody, death row, the psychiatric wing and solitary are not walked");
                }
                // A second visit keeps the inspector who is here.
                Inspections.Inspect(g, map);
                if (g.story.inspection.inspector != p) InspectorFail("the walk", "a second visit brought another inspector while the first was still on the map");
                // The report.
                List<InspectionFinding> list = g.story.inspection.findings;
                if (list.Count == 0 || list.Count > InspectionReport.MaxFindings) InspectorFail("the report", $"{list.Count} findings written down (at most {InspectionReport.MaxFindings}, and the seeded body is one)");
                if (list.Select(f => f.key).Distinct().Count() != list.Count) InspectorFail("the report", "a kind of finding is written down twice: " + string.Join(", ", list.Select(f => f.key)));
                if (list.Any(f => f.key == InspectionReport.DiagKey(InspectionReport.NoShowersKey)) && list.Any(f => f.key == InspectionReport.DiagKey(InspectionReport.NoZoneKey + RPDefOf.RP_Shower.defName))) InspectorFail("the report", "one missing shower room is written down twice (the room and the showers)");
                foreach (InspectionFinding f in list)
                {
                    int left = f.dueTick - InspectorNow(), full = InspectionReport.ReinspectDays * GenDate.TicksPerDay;
                    if (left > full || left < full - 5) InspectorFail("the report", $"{f.key} is due in {left} ticks, not at the re-inspection's {InspectionReport.ReinspectDays} days");
                    if (f.Gone) InspectorFail("the report", $"{f.key} is written down as put right");
                }
                if (book.reinspectTick < InspectorNow() + InspectionReport.ReinspectDays * GenDate.TicksPerDay - 5 || book.reinspectTick > InspectorNow() + InspectionReport.ReinspectDays * GenDate.TicksPerDay) InspectorFail("the report", $"the re-inspection is at {book.reinspectTick - InspectorNow()} ticks, not {InspectionReport.ReinspectDays} days on");
                RPLog.Message($"[RimPrison:test] inspector: a visit walks {dests.Count} stops, writes {list.Count} findings ({string.Join(", ", list.Select(f => f.key))}), re-inspection in {(book.reinspectTick - InspectorNow()) / (float)GenDate.TicksPerDay:F1} days");
            });
        }

        // ---- each kind of finding -------------------------------------------------------------------------------------------------------------------------

        /// <summary>Bodies outside the morgue (an hour or more): read when there, with where the first lies and how many; not one a minute old (the guards collect that); not present once there is none.</summary>
        private static void CheckInspectorBodies(Map map)
        {
            Guarded("inspector: bodies outside the morgue", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                InspectorBegin(g, map, r);
                int bodies0 = Morgue.Uncollected(map, pm, Morgue.StuckAfterTicks);
                Corpse body = InspectorSeedBody(map, r);
                InspectionFinding b = InspectorCandidate(g, map, InspectionReport.BodyKey);
                if (b == null || b.id != bodies0 + 1 || !b.cell.IsValid) InspectorFail("bodies", $"a body outside the morgue for two hours: finding {(b == null ? "none" : $"{b.id} bodies at {b.cell}")}, expected {bodies0 + 1}");
                else if (!InspectionReport.Describe(b, map).Contains((bodies0 + 1).ToString())) InspectorFail("bodies", "the finding's words do not give the number of bodies: " + InspectionReport.Describe(b, map));
                if (b != null && !InspectionReport.Present(b, g, map, null)) InspectorFail("bodies", "a body that lies outside the morgue is not present");
                if (b != null && InspectionReport.TabOf(b.key) != MainTabWindow_Prison.Tab.Rooms) InspectorFail("bodies", "a body's line does not open the Rooms tab");
                // Put right: a fresh body is not one.
                body.timeOfDeath = InspectorNow();
                Morgue.Reset();
                int now = Morgue.Uncollected(map, pm, Morgue.StuckAfterTicks);
                if (now != bodies0) InspectorFail("bodies", $"a body that is a minute old counts as lying outside the morgue ({now} counted, {bodies0} before)");
                if (b != null && now == 0 && InspectionReport.Present(b, g, map, null)) InspectorFail("bodies", "the finding is still present with no body outside the morgue");
            });
        }

        /// <summary>
        /// A patch of the danger map that no lamp lights is written down, with the patch's place; lit again, it is not present. It is kept by the cell it was seen at, not the zone grid's anchor: painting the zone away at the
        /// anchor leaves it present. And what is dark by day is dark at night (the sky leaves the verdict out).
        /// </summary>
        private static void CheckInspectorUnlit(Map map)
        {
            Guarded("inspector: an unlit patch", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                DarkScene s = DarkScene.TryMake(map, r);
                if (s == null) return;
                PrisonMap pm = s.pm;
                List<IncidentMark> marks0 = pm.incidents;
                r.Add(() =>
                {
                    pm.incidents = marks0;
                    DangerMap.ForgetUnlit();
                });
                int patchIndex = pm.zones.PatchAt(s.cell);
                if (patchIndex < 0)
                {
                    InspectorFail("unlit", $"the check's own premise: the spot {s.cell} is in no patch of a zone");
                    return;
                }
                pm.incidents = new List<IncidentMark> { new IncidentMark { cell = s.cell, kind = DangerKind.Stabbing, tick = InspectorNow() } };
                PrisonZoneDef zone = pm.zones.Patches[patchIndex].zone;
                List<IntVec3> cells = pm.zones.CellsOf(zone).Where(c => pm.zones.PatchAt(c) == patchIndex && !c.Impassable(map)).ToList();
                void Force(bool dark)
                {
                    foreach (IntVec3 c in cells) TensionUtility.TestGlow[c] = dark ? 0f : 1f;
                    DangerMap.ForgetUnlit();
                }
                Force(true);
                InspectionFinding f = InspectorCandidate(g, map, InspectionReport.UnlitKey);
                if (f == null || !f.cell.IsValid || pm.zones.PatchAt(f.cell) != patchIndex) InspectorFail("unlit", $"a dark patch with a stabbing on it: finding {(f == null ? "none" : $"at {f.cell}")}, the patch is the one at {s.cell}");
                else
                {
                    if (!InspectionReport.Present(f, g, map, null)) InspectorFail("unlit", "a patch that is dark is not present");
                    string words = InspectionReport.Describe(f, map);
                    if (!words.Contains($"{f.cell.x}") || !words.Contains($"{f.cell.z}") || !words.Contains("unlit") || !words.Contains(zone.label)) InspectorFail("unlit", "the finding's words do not say where (the zone and the cell) and that it is unlit: " + words);
                    if (InspectionReport.TabOf(f.key) != MainTabWindow_Prison.Tab.Deployment) InspectorFail("unlit", "an unlit patch's line does not open the Deployment tab");
                    // The zone painted away at the patch's anchor: the patch is another patch with another anchor, and still no lamp lights it.
                    IntVec3 anchor = map.cellIndices.IndexToCell(pm.zones.Patches[patchIndex].anchor);
                    if (anchor == f.cell || !cells.Contains(anchor)) RPLog.Message($"[RimPrison:test] inspector unlit: the patch's anchor {anchor} is the finding's cell or not in the patch, the anchor check is skipped");
                    else
                    {
                        int anchor0 = pm.zones.Patches[patchIndex].anchor;
                        pm.zones.Set(anchor, null);
                        try
                        {
                            int moved = pm.zones.PatchAt(f.cell);
                            if (moved < 0 || pm.zones.Patches[moved].anchor == anchor0) RPLog.Message("[RimPrison:test] inspector unlit: painting a cell away did not move the patch's anchor, the anchor check is skipped");
                            else if (!InspectionReport.Present(f, g, map, null)) InspectorFail("unlit", "a patch that is still dark is not present once the zone is painted away at its anchor (the finding is kept by the zone grid's anchor, not by the place)");
                        }
                        finally
                        {
                            pm.zones.Set(anchor, zone);
                        }
                    }
                }
                // Dark by day is dark by night: with no forced glow, what is dark now is unlit at night too.
                TensionUtility.TestGlow.Clear();
                DangerMap.ForgetUnlit();
                HotPatch patch = DangerMap.PatchAt(pm, s.cell);
                if (DangerMap.Unlit(pm, patch) && !DangerMap.Unlit(pm, patch, atNight: true)) InspectorFail("unlit", "a patch that is dark now is not unlit at night (the sky's light is left out of the night verdict)");
                Force(false);
                if (InspectorCandidate(g, map, InspectionReport.UnlitKey) != null) InspectorFail("unlit", "a lit patch is written down as unlit");
                if (f != null && InspectionReport.Present(f, g, map, null)) InspectorFail("unlit", "a patch that has been lit is still present");
            });
        }

        /// <summary>The nights: the fewest guards on duty in any night hour (22 to 6) against one for every twenty inmates, at least one; a thin day hour is not a finding.</summary>
        private static void CheckInspectorNight(Map map)
        {
            Guarded("inspector: the night cover", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                int inmates = map.mapPawns.PrisonersOfColonySpawned.Count(InmateUtility.IsActiveInmate);
                if (inmates == 0)
                {
                    RPLog.Message("[RimPrison:test] inspector night: no active inmate in the live prison, nothing to guard");
                    return;
                }
                int needed = Mathf.Max(1, Mathf.CeilToInt(inmates / (float)InspectionReport.NightInmatesPerGuard));
                int[] Strip(int fill, int hour = -1, int at = 0)
                {
                    var strip = new int[24];
                    for (int h = 0; h < 24; h++) strip[h] = fill;
                    if (hour >= 0) strip[hour] = at;
                    return strip;
                }
                InspectionReport.TestStrip = Strip(0);
                InspectionFinding f = InspectorCandidate(g, map, InspectionReport.NightKey);
                if (f == null || f.id != needed || InspectionReport.NightShort(map) != needed) InspectorFail("night", $"nobody on duty at night with {inmates} inmates: finding {(f == null ? "none" : f.id.ToString())}, expected {needed} needed");
                if (f != null && !InspectionReport.Describe(f, map).Contains(needed.ToString())) InspectorFail("night", "the finding's words do not give the guards needed: " + InspectionReport.Describe(f, map));
                InspectionReport.TestStrip = Strip(needed);
                if (InspectionReport.NightShort(map) != 0 || InspectorCandidate(g, map, InspectionReport.NightKey) != null) InspectorFail("night", $"{needed} on duty in every hour is not cover enough for {inmates} inmates");
                InspectionReport.TestStrip = Strip(needed, 3, needed - 1);
                if (InspectionReport.NightShort(map) != needed) InspectorFail("night", $"one thin hour at 3 in the morning ({needed - 1} on duty, {needed} needed) is not a finding");
                InspectionReport.TestStrip = Strip(needed, 12, 0);
                if (InspectionReport.NightShort(map) != 0) InspectorFail("night", "nobody on duty at noon is a finding about the night");
                InspectionReport.TestStrip = Strip(needed, 22, needed - 1);
                if (InspectionReport.NightShort(map) != needed) InspectorFail("night", "the first night hour (22) is not counted");
                InspectionReport.TestStrip = Strip(needed, 6, needed - 1);
                if (InspectionReport.NightShort(map) != 0) InspectorFail("night", "6 in the morning is counted as night");
                InspectionReport.TestStrip = null;
            });
        }

        /// <summary>
        /// What the prison's routine clears, or the warden has not been asked yet, is not written: inmates going hungry (a starving one is made), filth in the home area (made), and a complaint waiting past its day (seeded, as the
        /// letter would have dismissed it within the day). Every candidate is one of the structural kinds.
        /// </summary>
        private static void CheckInspectorRoutineIsNotWritten(Map map)
        {
            Guarded("inspector: what the prison's routine clears is not written", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                InspectorSeedHungry(map, r);
                InspectorSeedComplaint(g, r, 3f);
                bool filth = InspectorSeedFilth(map, r);
                var kinds = new[] { InspectionReport.BodyKey, InspectionReport.UnlitKey, InspectionReport.NightKey, InspectionReport.Grade0Key };
                List<string> keys = InspectionReport.Candidates(g, map).Select(f => f.key).ToList();
                foreach (string k in keys)
                    if (!kinds.Contains(k) && !k.StartsWith(InspectionReport.DiagPrefix) && !k.StartsWith(InspectionReport.LockedPrefix)) InspectorFail("routine", $"{k} is written down: a starving inmate, a complaint three days old and filth (made: {filth}) are not things only the warden can put right ({string.Join(", ", keys)})");
            });
        }

        /// <summary>
        /// The structural gaps of the to-do list (the shower room unpainted): read, worded, opening the Rooms tab, and one cause to a finding - no showers is not written beside the shower room that is not painted (the room is the
        /// cause), but is when the room is there.
        /// </summary>
        private static void CheckInspectorRooms(Map map)
        {
            Guarded("inspector: a gap in the rooms", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                string zoneKey = InspectionReport.NoZoneKey + RPDefOf.RP_Shower.defName, showers = InspectionReport.NoShowersKey, toilets = InspectionReport.NoToiletsKey;
                var one = InspectionReport.OneCause(new List<string> { zoneKey, showers, toilets });
                if (one.Count != 2 || !one.Contains(zoneKey) || !one.Contains(toilets)) InspectorFail("rooms", "no shower room and no showers are not one finding: " + string.Join(", ", one));
                var kept = InspectionReport.OneCause(new List<string> { showers, toilets });
                if (kept.Count != 2) InspectorFail("rooms", "no showers is dropped though the shower room is there: " + string.Join(", ", kept));
                if (InspectorSeedNoShowers(map, r) is null) RPLog.Message("[RimPrison:test] inspector rooms: the live prison has no shower room to take away");
                else
                {
                    string showerKey = InspectionReport.DiagKey(zoneKey);
                    InspectionFinding f = InspectorCandidate(g, map, showerKey);
                    if (f == null) InspectorFail("rooms", "no shower room painted is not written down: " + string.Join(", ", InspectionReport.Candidates(g, map).Select(x => x.key)));
                    else
                    {
                        if (!InspectionReport.Describe(f, map).Contains(RPDefOf.RP_Shower.label)) InspectorFail("rooms", "the finding's words do not name the room: " + InspectionReport.Describe(f, map));
                        if (InspectionReport.TabOf(f.key) != MainTabWindow_Prison.Tab.Rooms) InspectorFail("rooms", "a missing room's line does not open the Rooms tab");
                        if (f.dueTick - InspectorNow() != InspectionReport.ReinspectDays * GenDate.TicksPerDay) InspectorFail("rooms", "a missing room's deadline is not the re-inspection's day");
                    }
                    if (InspectionReport.Candidates(g, map).Any(x => x.key == InspectionReport.DiagKey(showers))) InspectorFail("rooms", "the showers are written down beside the shower room that is not painted (one cause, two findings)");
                }
            });
        }

        /// <summary>
        /// Cells graded 0: counted as cells, for an inmate who is meant for better (entitlement above 0 with cells by conduct on, where the ladder houses entitlement 0 in grade 0 on purpose); a cell with nobody who is
        /// meant for better in it is not one.
        /// </summary>
        private static void CheckInspectorBareCells(Map map)
        {
            Guarded("inspector: cells graded 0", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                InspectorBegin(g, map, r);
                int bare0 = InspectionReport.BareCells(pm).count;
                if (InspectorSeedBareCell(map, r) is not { } owner)
                {
                    RPLog.Message("[RimPrison:test] inspector bare cells: no graded cell with an inmate under the ladder in the live prison to take down to grade 0");
                    return;
                }
                (int bare, IntVec3 first) = InspectionReport.BareCells(pm);
                if (bare != bare0 + 1 || !first.IsValid) InspectorFail("grade 0", $"a cell graded 0 with an inmate of entitlement {owner.entitlement} in it is not counted as one cell more ({bare0} before, {bare} after)");
                InspectionFinding f = InspectorCandidate(g, map, InspectionReport.Grade0Key);
                if (f == null || f.id != bare) InspectorFail("grade 0", $"the finding is {(f == null ? "none" : f.id.ToString())} cells, the count is {bare}");
                else if (!InspectionReport.Describe(f, map).Contains(bare.ToString())) InspectorFail("grade 0", "the finding's words do not give the number of cells: " + InspectionReport.Describe(f, map));
                // The ladder's own: with cells by conduct on, an inmate of entitlement 0 lives in a bare cell on purpose.
                if (g.cellsByConduct)
                {
                    int entitlement = owner.entitlement;
                    owner.entitlement = 0;
                    int after = InspectionReport.BareCells(pm).count;
                    owner.entitlement = entitlement;
                    if (after != bare0) InspectorFail("grade 0", $"a grade-0 cell for an inmate of entitlement 0 is counted ({after} cells, {bare0} without it): the ladder puts them there");
                }
                else RPLog.Message("[RimPrison:test] inspector bare cells: cells by conduct is off in this game, the entitlement-0 case is not run");
            });
        }

        /// <summary>Several kinds at once: the most pressing first (a body, a dark patch, the night, a gap in the rooms, a bare cell), the report keeps the first five and no more.</summary>
        private static void CheckInspectorOrderAndCap(Map map)
        {
            Guarded("inspector: the order and the cap of five", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                InspectorSeedBody(map, r);
                bool rooms = InspectorSeedNoShowers(map, r) != null;
                bool bare = InspectorSeedBareCell(map, r) != null;
                if (map.mapPawns.PrisonersOfColonySpawned.Any(InmateUtility.IsActiveInmate)) InspectionReport.TestStrip = new int[24];
                List<string> keys;
                try
                {
                    keys = InspectionReport.Candidates(g, map).Select(f => f.key).ToList();
                    int At(string key) => keys.IndexOf(key);
                    string Order(params string[] ks)
                    {
                        int last = -1;
                        foreach (string k in ks)
                        {
                            int i = At(k);
                            if (i < 0) continue;
                            if (i < last) return $"{k} comes before what should come first";
                            last = i;
                        }
                        return null;
                    }
                    string order = Order(InspectionReport.BodyKey, InspectionReport.UnlitKey, InspectionReport.NightKey, InspectionReport.DiagKey(InspectionReport.NoZoneKey + RPDefOf.RP_Shower.defName), InspectionReport.Grade0Key);
                    if (order != null) InspectorFail("order", $"{order} in {string.Join(", ", keys)}");
                    if (At(InspectionReport.BodyKey) < 0) InspectorFail("order", "a seeded body is not among the candidates: " + string.Join(", ", keys));
                    List<InspectionFinding> read = InspectionReport.Read(g, map);
                    if (read.Count != System.Math.Min(keys.Count, InspectionReport.MaxFindings) || !read.Select(f => f.key).SequenceEqual(keys.Take(InspectionReport.MaxFindings))) InspectorFail("the cap", $"the report has {string.Join(", ", read.Select(f => f.key))} of the candidates {string.Join(", ", keys)}");
                    if (keys.Count > InspectionReport.MaxFindings && read.Any(f => f.key == keys.Last())) InspectorFail("the cap", "the least pressing finding is in a report that is over its cap");
                    RPLog.Message($"[RimPrison:test] inspector: {keys.Count} candidates (rooms {rooms}, bare cell {bare}): {string.Join(", ", keys)}; the report keeps {read.Count}");
                }
                finally
                {
                    InspectionReport.TestStrip = null;
                }
            });
        }

        // ---- the notice, the hold ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A visit reads only what the prison already gave when it was announced: a room unpainted after the notice (and put back after the visit) is not written, a body that lay there at the notice is; with no notice taken
        /// (a dev incident) everything is read; the visit uses the notice up.
        /// </summary>
        private static void CheckInspectorNotice(Map map)
        {
            Guarded("inspector: only what was there at the notice", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                InspectorSeedBody(map, r);
                InspectionReport.Notice(g, map);
                List<string> noticed = g.story.inspection.noticeKeys;
                if (noticed == null || !noticed.Contains(InspectionReport.BodyKey)) InspectorFail("notice", "the notice did not take the body that lay there: " + (noticed == null ? "none" : string.Join(", ", noticed)));
                string showerKey = InspectionReport.DiagKey(InspectionReport.NoZoneKey + RPDefOf.RP_Shower.defName);
                if (InspectorSeedNoShowers(map, r) is null) RPLog.Message("[RimPrison:test] inspector notice: the live prison has no shower room to take away after the notice");
                else
                {
                    if (!InspectionReport.Candidates(g, map).Any(f => f.key == showerKey)) InspectorFail("notice", "the shower room unpainted after the notice is not a candidate at all (the premise)");
                    List<string> read = InspectionReport.Read(g, map).Select(f => f.key).ToList();
                    if (read.Contains(showerKey)) InspectorFail("notice", "a shower room unpainted after the notice is written down (it is made for the visit)");
                    if (!read.Contains(InspectionReport.BodyKey)) InspectorFail("notice", "the body that lay there at the notice is not written down: " + string.Join(", ", read));
                    g.story.inspection.noticeKeys = null;
                    if (!InspectionReport.Read(g, map).Any(f => f.key == showerKey)) InspectorFail("notice", "with no notice taken the shower room unpainted is not read");
                }
                // The visit uses the notice up.
                InspectionReport.Notice(g, map);
                Inspections.Inspect(g, map);
                if (g.story.inspection.noticeKeys != null) InspectorFail("notice", "the visit kept the notice's snapshot");
            });
        }

        /// <summary>
        /// The hourly look and the day's hold: a finding is gone from the hour it is no longer so, open again the hour it returns, and held (so that it pays) only once it has been gone a whole day - not before. A room painted
        /// again is gone, a body that is a minute old is gone.
        /// </summary>
        private static void CheckInspectorHold(Map map)
        {
            Guarded("inspector: the hourly look and the day's hold", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                InspectorBegin(g, map, r);
                InspectionState book = g.story.inspection;
                int day = InspectionReport.HoldDays * GenDate.TicksPerDay;
                int now = InspectorNow();
                Corpse body = InspectorSeedBody(map, r);
                var f = new InspectionFinding { key = InspectionReport.BodyKey, id = 1, dueTick = now + 5 * GenDate.TicksPerDay };
                book.findings.Add(f);
                book.reinspectTick = now + 5 * GenDate.TicksPerDay;
                InspectionReport.Hourly(g, map);
                if (f.Gone || InspectionReport.Held(f, now + 9 * day)) InspectorFail("hold", "the hourly look says a body that still lies there is gone");
                body.timeOfDeath = InspectorNow();
                Morgue.Reset();
                bool others = Morgue.Uncollected(map, pm, Morgue.StuckAfterTicks) > 0;
                InspectionReport.Hourly(g, map);
                if (others) RPLog.Message("[RimPrison:test] inspector hold: the live prison has another body outside the morgue, the gone part is not run");
                else
                {
                    if (!f.Gone || f.goneSince != InspectorNow()) InspectorFail("hold", $"the hourly look does not mark a finding gone from the hour it is no longer so (goneSince {f.goneSince}, now {InspectorNow()})");
                    if (InspectionReport.Held(f, InspectorNow())) InspectorFail("hold", "a finding that has just been put right is held");
                    if (InspectionReport.Held(f, InspectorNow() + day - 1)) InspectorFail("hold", "a finding gone for less than a day is held");
                    if (!InspectionReport.Held(f, InspectorNow() + day)) InspectorFail("hold", "a finding gone for a whole day is not held");
                    // It comes back: open again, and the day starts over.
                    body.timeOfDeath = InspectorNow() - 2 * GenDate.TicksPerHour;
                    Morgue.Reset();
                    InspectionReport.Hourly(g, map);
                    if (f.Gone) InspectorFail("hold", "the hourly look does not open a finding again when it returns");
                    body.timeOfDeath = InspectorNow();
                    Morgue.Reset();
                    InspectionReport.Hourly(g, map);
                    if (f.goneSince != InspectorNow()) InspectorFail("hold", "the day's hold does not start over when a finding is gone again");
                }
                // A room: open while it is not painted, gone when it is, as the to-do list says.
                string showerKey = InspectionReport.DiagKey(InspectionReport.NoZoneKey + RPDefOf.RP_Shower.defName);
                if (InspectorSeedNoShowers(map, r) is not { } cells) RPLog.Message("[RimPrison:test] inspector hold: the live prison has no shower room to take away");
                else
                {
                    var room = new InspectionFinding { key = showerKey, dueTick = now + 5 * GenDate.TicksPerDay };
                    book.findings.Add(room);
                    InspectionReport.Hourly(g, map);
                    if (room.Gone) InspectorFail("hold", "the hourly look says a missing shower room is put right");
                    foreach (IntVec3 c in cells) pm.zones.Set(c, RPDefOf.RP_Shower);
                    InspectionReport.Hourly(g, map);
                    if (!room.Gone) InspectorFail("hold", "the hourly look says a shower room that is painted again is not put right");
                    foreach (IntVec3 c in cells) pm.zones.Set(c, null);
                    InspectionReport.Hourly(g, map);
                    if (room.Gone) InspectorFail("hold", "the hourly look does not open a room again when it is unpainted again");
                    foreach (IntVec3 c in cells) pm.zones.Set(c, RPDefOf.RP_Shower);
                }
                book.findings.Clear();
                book.reinspectTick = -1;
            });
        }

        // ---- the to-do band -------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The findings that are still open are lines of the to-do list (with the time left, or that they are due now), the ones put right are not, a pending re-inspection is what makes them lines, and the line opens the screen the finding is about.</summary>
        private static void CheckInspectorTodo(Map map)
        {
            Guarded("inspector: the to-do lines", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                int now = InspectorNow();
                InspectionState book = g.story.inspection;
                var open = new InspectionFinding { key = InspectionReport.NightKey, id = 4, dueTick = now + GenDate.TicksPerDay + GenDate.TicksPerHour };
                var late = new InspectionFinding { key = InspectionReport.UnlitKey, cell = new IntVec3(30, 0, 31), dueTick = now - GenDate.TicksPerHour };
                var done = new InspectionFinding { key = InspectionReport.BodyKey, id = 33, dueTick = now + GenDate.TicksPerDay, goneSince = now - GenDate.TicksPerHour };
                book.findings.AddRange(new[] { open, late, done });
                List<PrisonDiagnostics.Item> items = PrisonDiagnostics.Items(map).ToList();
                // No re-inspection pending: nothing is a line.
                if (items.Any(i => i.text.Contains("Inspector - "))) InspectorFail("to-do", "the inspector's findings are lines of the to-do list with no re-inspection pending");
                book.reinspectTick = now + 4 * GenDate.TicksPerDay;
                items = PrisonDiagnostics.Items(map).ToList();
                string openWords = InspectionReport.Describe(open, map), lateWords = InspectionReport.Describe(late, map), doneWords = InspectionReport.Describe(done, map);
                PrisonDiagnostics.Item openLine = items.FirstOrDefault(i => i.text.Contains(openWords)), lateLine = items.FirstOrDefault(i => i.text.Contains(lateWords));
                if (openLine.text == null) InspectorFail("to-do", "an open finding is not a line of the to-do list: " + string.Join(" | ", items.Select(i => i.text)));
                else
                {
                    if (!openLine.text.Contains((string)"RP_InspectorTimeLeft".Translate((open.dueTick - now).ToStringTicksToPeriod()))) InspectorFail("to-do", "an open finding's line does not say the time left: " + openLine.text);
                    if (openLine.tab != InspectionReport.TabOf(open.key)) InspectorFail("to-do", $"the night line opens {openLine.tab}, not {InspectionReport.TabOf(open.key)}");
                }
                if (lateLine.text == null) InspectorFail("to-do", "a finding past its deadline is not a line of the to-do list");
                else
                {
                    if (!lateLine.text.Contains((string)"RP_InspectorOverdue".Translate())) InspectorFail("to-do", "a finding past its deadline is not said to be due now: " + lateLine.text);
                    if (lateLine.tab != MainTabWindow_Prison.Tab.Deployment) InspectorFail("to-do", "the unlit line does not open the Deployment tab");
                }
                if (items.Any(i => i.text.Contains(doneWords))) InspectorFail("to-do", "a finding that is put right is a line of the to-do list");
                // Short enough for the overview's list (a line is cut at the band's width: about 850 px at 1280 x 720, 5.7 px to a character of the small font): the longest line a finding can make, most time left or none.
                var kinds = new List<InspectionFinding>
                {
                    new InspectionFinding { key = InspectionReport.BodyKey, id = 99 }, new InspectionFinding { key = InspectionReport.NightKey, id = 99 }, new InspectionFinding { key = InspectionReport.Grade0Key, id = 99 },
                    new InspectionFinding { key = InspectionReport.UnlitKey, cell = new IntVec3(299, 0, 299) }, new InspectionFinding { key = InspectionReport.DiagKey(InspectionReport.NoStoveKey) },
                    new InspectionFinding { key = InspectionReport.DiagKey(InspectionReport.NoShowersKey) }, new InspectionFinding { key = InspectionReport.DiagKey(InspectionReport.NoToiletsKey) },
                };
                foreach (PrisonZoneDef z in new[] { RPDefOf.RP_Canteen, RPLaborDefOf.RP_Kitchen, RPDefOf.RP_Shower, RPDefOf.RP_Yard }) kinds.Add(new InspectionFinding { key = InspectionReport.DiagKey(InspectionReport.NoZoneKey + z.defName) });
                foreach (InspectionFinding k in kinds)
                    foreach (int due in new[] { now - GenDate.TicksPerHour, now + InspectionReport.ReinspectDays * GenDate.TicksPerDay - 1 })
                    {
                        k.dueTick = due;
                        string line = "RP_InspectorTodo".Translate(InspectionReport.Describe(k, map), InspectionReport.TimeLeft(k, now));
                        if (line.Length > InspectorLineMax) InspectorFail("to-do", $"a to-do line is {line.Length} characters, over the {InspectorLineMax} the overview's list shows: {line}");
                    }
                if (openLine.text != null && !PrisonDiagnostics.Problems(map).Contains(openLine.text)) InspectorFail("to-do", "the autotest's list does not have the inspector's line the screen's does");
                // The structural lines keep their order (the first-hour review): the inspector's come after them.
                int firstTodo = items.FindIndex(i => i.text.Contains(openWords));
                int lastStructural = items.FindLastIndex(i => i.key != null);
                if (firstTodo >= 0 && lastStructural > firstTodo) InspectorFail("to-do", "the inspector's lines come before the rooms a prison cannot run without");
                else if (lastStructural < 0) RPLog.Message("[RimPrison:test] inspector to-do: the live prison has no structural line, so the order against them is not seen");
            });
        }

        // ---- the re-inspection ----------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Five days on: a grant for each finding that is held (gone, and gone for the last day), a fine for each that is not - one gone for half a day, or open - as two ledger entries and a letter that says each; the book is closed (no
        /// findings, no re-inspection) and the inspector is on the map. Before the day nothing happens. All held is a grant only (a good letter), none held a fine only.
        /// </summary>
        private static void CheckInspectorReinspection(Map map)
        {
            Guarded("inspector: the re-inspection's grant and fine", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                InspectorClockOn(r);
                int now = InspectorNow(), day = GenDate.TicksPerDay;
                InspectionState book = g.story.inspection;
                // (How long it has been gone, in ticks: -1 is open.)
                InspectionFinding F(string key, int goneFor, int id = 1) => new InspectionFinding { key = key, id = id, dueTick = now - GenDate.TicksPerHour, goneSince = goneFor < 0 ? -1 : now - goneFor };
                book.findings.AddRange(new[] { F(InspectionReport.BodyKey, 2 * day, 2), F(InspectionReport.Grade0Key, 3 * day, 3), F(InspectionReport.NightKey, -1, 5) });
                book.reinspectTick = now + day;
                int funds0 = g.ledger.funds;
                Inspections.Hourly(g, map);
                if (g.ledger.funds != funds0 || book.findings.Count != 3 || book.reinspectTick < 0) InspectorFail("re-inspection", "the re-inspection happened a day early");
                // (The hourly look above judged the hand-made findings against the live prison: they are made again.)
                book.findings.Clear();
                book.findings.AddRange(new[] { F(InspectionReport.BodyKey, 2 * day, 2), F(InspectionReport.Grade0Key, 3 * day, 3), F(InspectionReport.NightKey, -1, 5) });
                int right = 2, open = 1;
                InspectionReport.Reinspect(g, map);
                int expected = right * InspectionReport.GrantPerFixed - open * InspectionReport.FinePerOpen;
                if (g.ledger.funds - funds0 != expected) InspectorFail("re-inspection", $"two put right and one not moved the funds by {g.ledger.funds - funds0}, expected {expected} ({right} x {InspectionReport.GrantPerFixed} - {open} x {InspectionReport.FinePerOpen})");
                if (book.findings.Count != 0 || book.reinspectTick != -1) InspectorFail("re-inspection", "the book is not closed after the re-inspection");
                List<LedgerEntry> entries = r.NewLedgerEntries.ToList();
                if (!entries.Any(e => e.amount == right * InspectionReport.GrantPerFixed && e.category == RPDefOf.RP_Grants) || !entries.Any(e => e.amount == -open * InspectionReport.FinePerOpen && e.category == RPDefOf.RP_Fines))
                    InspectorFail("re-inspection", "the grant and the fine are not entries of their own: " + string.Join(", ", entries.Select(e => $"{e.category?.defName}:{e.amount}")));
                string text = InspectorLetterText(r, "Re-inspection");
                if (text == null) InspectorFail("re-inspection", "no letter for the re-inspection");
                else if (!text.Contains(Money.Format(right * InspectionReport.GrantPerFixed)) || !text.Contains(Money.Format(open * InspectionReport.FinePerOpen))
                         || !InspectorSays(text, InspectionReport.Describe(F(InspectionReport.BodyKey, 2 * day, 2), map)) || !InspectorSays(text, InspectionReport.Describe(F(InspectionReport.NightKey, -1, 5), map)))
                    InspectorFail("re-inspection", "the letter does not give the sums and say each item: " + text);
                if (book.inspector is not { Spawned: true }) InspectorFail("re-inspection", "the inspector does not come for the re-inspection");
                // Put right at the last minute, or cleared by the routine on the day: gone for less than the last day is not paid.
                int fundsLate = g.ledger.funds;
                book.findings.AddRange(new[] { F(InspectionReport.BodyKey, day / 2), F(InspectionReport.NightKey, day - 5) });
                InspectionReport.Reinspect(g, map);
                if (g.ledger.funds - fundsLate != -2 * InspectionReport.FinePerOpen) InspectorFail("re-inspection", $"two findings gone for less than the last day moved the funds by {g.ledger.funds - fundsLate}, expected {-2 * InspectionReport.FinePerOpen}");
                // All held: a grant only. None: a fine only.
                int fundsAll = g.ledger.funds;
                book.findings.AddRange(new[] { F(InspectionReport.BodyKey, day), F(InspectionReport.NightKey, 4 * day) });
                InspectionReport.Reinspect(g, map);
                if (g.ledger.funds - fundsAll != 2 * InspectionReport.GrantPerFixed) InspectorFail("re-inspection", $"two put right moved the funds by {g.ledger.funds - fundsAll}, expected {2 * InspectionReport.GrantPerFixed}");
                int fundsNone = g.ledger.funds;
                book.findings.AddRange(new[] { F(InspectionReport.BodyKey, -1), F(InspectionReport.NightKey, -1), F(InspectionReport.Grade0Key, -1) });
                InspectionReport.Reinspect(g, map);
                if (g.ledger.funds - fundsNone != -3 * InspectionReport.FinePerOpen) InspectorFail("re-inspection", $"three not put right moved the funds by {g.ledger.funds - fundsNone}, expected {-3 * InspectionReport.FinePerOpen}");
                // A new inspection is not announced while one is to come back.
                book.reinspectTick = InspectorNow() + GenDate.TicksPerDay;
                if (DefDatabase<IncidentDef>.GetNamed("RP_Inspection").Worker.CanFireNow(new IncidentParms { target = map })) InspectorFail("re-inspection", "a new inspection can be announced while the re-inspection is still to come");
                // A visit that comes with one still to come (a dev incident) settles it, and does not drop it.
                book.findings.Add(F(InspectionReport.NightKey, -1));
                book.reinspectTick = InspectorNow() + GenDate.TicksPerDay;
                int fines0 = r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Fines && e.amount == -InspectionReport.FinePerOpen);
                Inspections.Inspect(g, map);
                if (r.NewLedgerEntries.Count(e => e.category == RPDefOf.RP_Fines && e.amount == -InspectionReport.FinePerOpen) != fines0 + 1) InspectorFail("re-inspection", "a visit with a re-inspection still to come dropped its findings with no grant or fine");
                book.reinspectTick = -1;
            });
        }

        // ---- the bribe ------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A bribed inspector walks and writes nothing down (the letter says so, no findings, no re-inspection), and what they left out is kept; the exposure letter names it (and says so when there was nothing); an inspection
        /// that is not bribed with the same state writes it down. An earlier bribe's leftovers are dropped at the next visit when no exposure is pending.
        /// </summary>
        private static void CheckInspectorBribe(Map map)
        {
            Guarded("inspector: a bribed inspector's empty report and the exposure", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                InspectorBegin(g, map, r);
                StorySystem s = g.story;
                InspectorSeedBody(map, r);
                // The same state, not bribed: the body is written down (the live prison's own findings may be there too).
                Inspections.Inspect(g, map);
                InspectionFinding written = s.inspection.findings.FirstOrDefault(f => f.key == InspectionReport.BodyKey);
                if (written == null) InspectorFail("bribe", "the control visit (not bribed) did not write down the body outside the morgue");
                string plain = InspectorLetterText(r, "State inspection");
                string bodyWords = written == null ? null : InspectionReport.Describe(written, map);
                if (!InspectorSays(plain, bodyWords)) InspectorFail("bribe", "the control visit's letter does not list the finding: " + plain);
                // Bribed: nothing written down.
                InspectorLetGo(map);
                s.inspection = new InspectionState();
                foreach (Letter l in r.NewLetters.ToList()) Find.LetterStack.RemoveLetter(l);
                s.inspectionPrep = 2;
                s.bribeExposeTick = -1;
                Inspections.Inspect(g, map);
                InspectionState book = s.inspection;
                if (book.findings.Count != 0 || book.reinspectTick != -1) InspectorFail("bribe", $"a bribed inspector wrote down {book.findings.Count} findings (re-inspection {book.reinspectTick})");
                if (!book.hidden.Any(f => f.key == InspectionReport.BodyKey)) InspectorFail("bribe", "what the bribed inspector left out is not kept: " + string.Join(", ", book.hidden.Select(f => f.key)));
                string bribed = InspectorLetterText(r, "State inspection");
                if (bribed == null || !bribed.Contains("RP_InspectorReportBribed".Translate().Resolve()) || InspectorSays(bribed, bodyWords)) InspectorFail("bribe", "the bribed inspection's letter does not say that nothing was written down (or lists what was): " + bribed);
                if (book.inspector is not { Spawned: true }) InspectorFail("bribe", "a bribed inspector does not walk the prison");
                if (!PrisonDiagnostics.Items(map).All(i => !i.text.Contains("Inspector - "))) InspectorFail("bribe", "a bribed inspector's report has lines in the to-do list");
                // The exposure, whether or not the roll came up: it names what was left out.
                s.bribeExposeTick = InspectorNow();
                foreach (Letter l in r.NewLetters.ToList()) Find.LetterStack.RemoveLetter(l);
                Inspections.Hourly(g, map);
                string exposed = InspectorLetterText(r, "bribery exposed");
                if (exposed == null) InspectorFail("bribe", "no exposure letter");
                else
                {
                    if (!exposed.Contains("RP_InspectorBribeHidden".Translate("X").Resolve().Split('\n')[0])) InspectorFail("bribe", "the exposure letter does not say what was left out: " + exposed);
                    if (bodyWords != null && !InspectorSays(exposed, bodyWords)) InspectorFail("bribe", $"the exposure letter does not name the body the inspector left out ({bodyWords}): {exposed}");
                }
                if (book.hidden.Count != 0) InspectorFail("bribe", "what was left out is kept after the exposure named it");
                // Nothing left out: the exposure says so.
                s.bribeExposeTick = InspectorNow();
                foreach (Letter l in r.NewLetters.ToList()) Find.LetterStack.RemoveLetter(l);
                Inspections.Hourly(g, map);
                string none = InspectorLetterText(r, "bribery exposed");
                if (none == null || !none.Contains("RP_InspectorBribeHiddenNone".Translate().Resolve())) InspectorFail("bribe", "an exposure with nothing left out does not say so: " + none);
                // An earlier bribe's leftovers are dropped at the next visit when no exposure is pending.
                book.hidden.Add(new InspectionFinding { key = InspectionReport.NightKey, id = 99, dueTick = InspectorNow() });
                s.bribeExposeTick = -1;
                s.inspectionPrep = 0;
                Inspections.Inspect(g, map);
                if (s.inspection.hidden.Count != 0) InspectorFail("bribe", "the leftovers of an old bribe are kept past the next visit with no exposure to come");
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------------------------

        /// <summary>The inspector's whole state as one comparable string: the announcement, the prep, the exposure tick, the re-inspection, the inspector on the map, the notice's kinds, the findings and what a bribe left out.</summary>
        private static string InspectorDescribe(PrisonGame g)
        {
            StorySystem s = g.story;
            InspectionState b = s.inspection;
            CompStaff sc = b.inspector?.GetComp<CompStaff>();
            return $"{s.inspectionTick}/{s.inspectionPrep}/{s.bribeExposeTick}/{b.reinspectTick}/{b.inspector?.thingIDNumber}:{sc?.role?.defName}:{sc?.leaveAtTick}/n={(b.noticeKeys == null ? "-" : string.Join("+", b.noticeKeys))}/f=[{string.Join(",", b.findings.Select(f => f.Describe()))}]/h=[{string.Join(",", b.hidden.Select(f => f.Describe()))}]";
        }

        private static (int tick, int prep, int expose, int inspectorId, int leave) inspectorSeed = (-1, 0, -1, -1, -1);
        private static (int tick, int prep, int expose) inspectorBefore;
        private static (List<InspectionFinding> findings, List<InspectionFinding> hidden, int reinspect, List<string> notice) inspectorBook;

        /// <summary>Saveload plan, before the snapshot: an inspection announced, a bribe in the book with an exposure to come, a re-inspection due, the notice's kinds, three findings (one gone since a tick, one at a cell, one a structural line) off every default, one left out by the bribe, and an inspector on the map with a time to leave.</summary>
        private static void SeedInspectorForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            StorySystem s = g.story;
            InspectionState b = s.inspection;
            int now = Find.TickManager.TicksGame;
            inspectorBefore = (s.inspectionTick, s.inspectionPrep, s.bribeExposeTick);
            inspectorBook = (b.findings, b.hidden, b.reinspectTick, b.noticeKeys);
            (s.inspectionTick, s.inspectionPrep, s.bribeExposeTick) = (now + 12345, 2, now + 54321);
            b.reinspectTick = now + 77777;
            b.noticeKeys = new List<string> { InspectionReport.BodyKey, InspectionReport.DiagKey(InspectionReport.NoZoneKey + RPDefOf.RP_Yard.defName) };
            b.findings = new List<InspectionFinding>
            {
                new InspectionFinding { key = InspectionReport.UnlitKey, cell = new IntVec3(21, 0, 34), dueTick = now + 99000 },
                new InspectionFinding { key = InspectionReport.Grade0Key, id = 7, dueTick = now + 88000, goneSince = now - 4321 },
                new InspectionFinding { key = InspectionReport.DiagKey(InspectionReport.NoZoneKey + RPDefOf.RP_Yard.defName), dueTick = now + 66000 },
            };
            b.hidden = new List<InspectionFinding> { new InspectionFinding { key = InspectionReport.BodyKey, cell = new IntVec3(3, 0, 4), id = 42, dueTick = now + 5000 } };
            Pawn p = SpecialMeasures.Visit(map, null, RPInspectorDefOf.RP_Inspector, InspectionReport.VisitHours, new List<IntVec3>());
            if (p == null)
            {
                reloadProblems.Add("saveload: no inspector could be seeded");
                return;
            }
            b.inspector = p;
            inspectorSeed = (now + 12345, 2, now + 54321, p.thingIDNumber, p.GetComp<CompStaff>().leaveAtTick);
            RPLog.Message($"[RimPrison:test] saveload inspector: {InspectorDescribe(g)}");
        }

        /// <summary>After the reload: the inspector came back the same person with the same time to leave (the snapshot compared the rest); then the book the plan found is put back (its inspector, if one, was let go) and the plan's visitor let go.</summary>
        private static void CheckInspectorAfterReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            StorySystem s = g.story;
            if (inspectorSeed.inspectorId < 0) return;
            InspectionState b = s.inspection;
            CompStaff sc = b.inspector?.GetComp<CompStaff>();
            if (b.inspector?.thingIDNumber != inspectorSeed.inspectorId || sc?.role != RPInspectorDefOf.RP_Inspector || sc.leaveAtTick != inspectorSeed.leave || !b.inspector.Spawned)
                reloadProblems.Add($"the inspector on the map did not come back from the save (pawn {b.inspector?.thingIDNumber} for {inspectorSeed.inspectorId}, role {sc?.role?.defName}, leaves at {sc?.leaveAtTick} for {inspectorSeed.leave})");
            else if (b.findings.Count != 3 || b.hidden.Count != 1 || b.noticeKeys?.Count != 2 || s.inspectionTick != inspectorSeed.tick || s.inspectionPrep != inspectorSeed.prep || s.bribeExposeTick != inspectorSeed.expose)
                reloadProblems.Add("the inspector's findings or the announcement did not come back from the save: " + InspectorDescribe(g));
            else RPLog.Message("[RimPrison:test] saveload inspector: the findings, the notice's kinds, what a bribe left out, the announcement, the re-inspection and the inspector on the map are intact");
            InspectorLetGo(map);
            (b.findings, b.hidden, b.reinspectTick, b.noticeKeys) = inspectorBook;
            b.inspector = null;
            (s.inspectionTick, s.inspectionPrep, s.bribeExposeTick) = inspectorBefore;
            inspectorSeed = (-1, 0, -1, -1, -1);
        }
    }
}
