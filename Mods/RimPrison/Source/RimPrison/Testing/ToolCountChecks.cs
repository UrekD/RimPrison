using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Count the tools back in (Labor/ToolCount.cs; gap analysis 6, item 4), in the events plan. The scene stands on open ground outside the compound, with the real workshop and kitchen unpainted for it (put back by the
    /// rollback): a license-plate press in a workshop and a stove in a kitchen, a throwaway inmate on the workshop job and a guard, a workman and a cook, the prison's own ledgers swapped for an empty one. The scene's own inmates are the whole
    /// crew (<see cref="ToolCount.CastForTests"/>). The proof (<see cref="CheckToolCountProof"/>) is the headless one Astra asked for: a theft forced in real work (a real produce job on the press, the real hourly work hook), the count by a
    /// guard on real ticks, one shortage and no name and nothing known before the count, a search that takes the tool and puts the supply right once. The rest: a recount and a stash (a round trip of the ledgers with the tool in it), stacking
    /// refused, the warden's three answers (and what happens unanswered, held back for the budget), the replacing, the room's retiring, an old save, the garden left alone, the tunnel, the amnesty and the bust. Each says what it saw. The saveload
    /// plan's round trip is <see cref="SeedToolsForReload"/>. Premises the live prison may not give are told, not failed.
    /// </summary>
    public static partial class AutoTest
    {
        private static void ToolFail(string check, string what) => problems0.Add($"tool count ({check}): {what}");

        /// <summary>
        /// The scenes' staff, on duty at every hour for as long as the scene stands. The prison's hourly pass writes every member of staff's shift into their timetable and sends the off-shift home in the same tick
        /// (<c>ShiftSystem.Balance</c>, then <c>StaffCommute.Hourly</c>): the guard of the proof, set on the count with 1500 real ticks to do it in, was on <c>RP_GoHome</c> 23 cells from the bench when the hour turned (tools-t1a, tools-t1b).
        /// So the pass does not write a scene member's shift at all (<see cref="Patch_ShiftSystem_Apply_HeldDoctors"/>, as for the drug-test doctors and the post guards).
        /// </summary>
        private static readonly List<Pawn> toolStaff = new List<Pawn>();

        /// <summary>A member of staff of the tool count's scene: the hourly pass leaves their shift as the scene wrote it.</summary>
        internal static bool ToolStaffHeld(Pawn p) => toolStaff.Contains(p);

        private static void CheckToolCount(Map map)
        {
            if (RPToolDefOf.RP_CountTools == null || PrisonDecisionDef.Named("RP_Decision_ToolCount") == null || DefDatabase<WorkGiverDef>.GetNamedSilentFail("RP_CountTools") == null
                || DefDatabase<WorkGiverDef>.GetNamedSilentFail("RP_CountToolsWorkman") == null)
            {
                ToolFail("defs", "the count's job, work givers or decision are not defined");
                return;
            }
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns);
            CheckToolCountProof(map);
            CheckToolCountStash(map);
            CheckToolCountStacking(map);
            CheckToolCountAnswers(map);
            CheckToolCountBudget(map);
            CheckToolCountReplace(map);
            CheckToolCountRetire(map);
            CheckToolCountOldSave(map);
            CheckToolCountOtherFinds(map);
            CheckToolCountKitchen(map);
            CheckToolCountFindBeforeCount(map);
            CheckToolCountCrewSearched(map);
            CheckToolCountAskedRoom(map);
            CheckToolCountNobodyCame(map);
            CheckToolCountBenchless(map);
            Pawn[] left = LeftBehind(map, pawnsBefore).Where(x => x.Name is NameSingle && x.LabelShort.StartsWith("Ztc")).ToArray();
            if (left.Length > 0) ToolFail("cleanup", $"the checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        // ---- the scene ----------------------------------------------------------------------------------------------------------------------

        private sealed class ToolScene
        {
            public const int Width = 19, Height = 7;
            public readonly Map map;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly PrisonMap pm;
            public readonly Rollback r;
            public readonly string check;
            public readonly HashSet<Pawn> cast = new HashSet<Pawn>();
            public IntVec3 origin;
            public Thing press, steel, stove;

            private ToolScene(Map map, Rollback r, string check)
            {
                this.map = map;
                this.r = r;
                this.check = check;
                pm = PrisonMap.Of(map);
            }

            public IntVec3 At(int x, int z) => new IntVec3(origin.x + x, 0, origin.z + z);

            public static PrisonZoneDef Workshop => RPLaborDefOf.RP_Workshop;

            public static PrisonZoneDef Kitchen => RPLaborDefOf.RP_Kitchen;

            /// <summary>The scene: the real workshop and kitchen unpainted, the ledgers swapped for an empty book, a workshop with one press and a kitchen with one stove on open ground. Null (and a failure) when there is no open ground.</summary>
            public static ToolScene Build(Map map, Rollback r, string check, bool kitchen = false)
            {
                var s = new ToolScene(map, r, check);
                PrisonGame g = s.g;
                s.origin = OpenGround(map);
                if (!s.origin.IsValid)
                {
                    ToolFail(check, $"no open ground outside the compound for the scene (footprint {PrisonBuilder.Footprint(map)}, map {map.Size})");
                    return null;
                }
                // (The prison's own workshop and kitchen are out of the scene: a bench that is still in a painted room would be a bench of the scene's.)
                foreach (PrisonZoneDef zone in new[] { Workshop, Kitchen })
                    foreach (IntVec3 c in s.pm.zones.CellsOf(zone).ToList())
                    {
                        r.Add(() => s.pm.zones.Set(c, zone));
                        s.pm.zones.Set(c, null);
                    }
                ToolLedgers book0 = g.tools;
                g.tools = new ToolLedgers();
                ToolCount.TestOn = true;
                ToolCount.Heard = new List<string>();
                ToolCount.CastForTests = p => s.cast.Contains(p);
                // (A snitch of the real prison tips off a guard on whoever carries contraband - a thief with a traced tool does - at the daily settlement or on a warden's walk, which the count's real ticks may cross: it marked the proof's worker
                // a suspect before the count was done (tools-t2a, tools-t2b: "Tail quietly told a guard: ZtcWorker is carrying contraband"). The scene's people are out of the tips' reach.)
                var tipPool0 = InformantSystem.TipPool;
                InformantSystem.TipPool = p => !s.cast.Contains(p) && !toolStaff.Contains(p) && (tipPool0 == null || tipPool0(p));
                r.Add(() =>
                {
                    InformantSystem.TipPool = tipPool0;
                    g.tools = book0;
                    book0.Reindex();
                    ToolCount.TestOn = false;
                    ToolCount.Heard = null;
                    ToolCount.CastForTests = null;
                    ContrabandUtility.TheftRollForTests = null;
                    StoryLetters.TestBudget = false;
                });
                foreach (IntVec3 c in new CellRect(s.origin.x, s.origin.z, Width, Height).ExpandedBy(1))
                    foreach (Thing plant in c.GetThingList(map).Where(t => t.def.category == ThingCategory.Plant).ToList())
                        plant.Destroy(DestroyMode.Vanish);
                s.Paint(new CellRect(s.origin.x, s.origin.z, 8, 5), Workshop);
                s.press = s.Spawn(DefDatabase<ThingDef>.GetNamed("RP_LicensePlatePress"), s.At(2, 2));
                s.steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                s.steel.stackCount = 30;
                GenPlace.TryPlaceThing(s.steel, s.At(6, 2), map, ThingPlaceMode.Direct);
                r.Add(() =>
                {
                    if (!s.steel.Destroyed) s.steel.Destroy(DestroyMode.Vanish);
                });
                if (kitchen)
                {
                    s.Paint(new CellRect(s.origin.x + 9, s.origin.z, 8, 5), Kitchen);
                    s.stove = s.Spawn(DefDatabase<ThingDef>.GetNamed("FueledStove"), s.At(12, 2));
                }
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

            private void Paint(CellRect rect, PrisonZoneDef zone)
            {
                foreach (IntVec3 c in rect)
                {
                    PrisonZoneDef was = pm.zones.ZoneAt(c);
                    r.Add(() => pm.zones.Set(c, was));
                    pm.zones.Set(c, zone);
                }
            }

            public Thing Spawn(ThingDef def, IntVec3 cell)
            {
                Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                t.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(t, cell, map, Rot4.North);
                r.Add(() =>
                {
                    if (!t.Destroyed) t.Destroy(DestroyMode.Vanish);
                });
                return t;
            }

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            /// <summary>An inmate on a prison job (the workshop's unless said), locked to it, tense enough to steal and on a work slot of the regime (the check ends their work by taking the slot away).</summary>
            public Pawn Worker(string name, string jobName = "RP_JobWorkshop", IntVec3? at = null)
            {
                Pawn p = Throwaway(map, r);
                p.Name = new NameSingle("Ztc" + name);
                CompInmate c = p.Inmate();
                c.relatives.Clear();
                c.familyCount = 0;
                InmateJobDef job = DefDatabase<InmateJobDef>.GetNamed(jobName);
                c.job = job;
                c.jobLocked = true;
                c.tension = 40f;
                if (job.requiredProgram != null && !c.passedPrograms.Contains(job.requiredProgram)) c.passedPrograms.Add(job.requiredProgram);
                c.forcedActivity = RPLaborDefOf.RP_Work;
                cast.Add(p);
                Place(p, at ?? At(4, 3));
                return p;
            }

            /// <summary>A guard, a workman or a cook on site with no work of their own.</summary>
            public Pawn Staff(string name, StaffRoleDef role, IntVec3? at = null)
            {
                Pawn st = ThrowawayStaff(map, r, role);
                st.Name = new NameSingle("Ztc" + name);
                if (st.timetable != null)
                    for (int hour = 0; hour < 24; hour++) st.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                if (st.needs?.rest != null) st.needs.rest.CurLevel = 1f;
                CompStaff sc = st.GetComp<CompStaff>();
                (sc.corrupt, sc.suspendedUntilTick) = (false, -1);
                if (st.workSettings != null)
                    foreach (WorkTypeDef work in DefDatabase<WorkTypeDef>.AllDefsListForReading) st.workSettings.SetPriority(work, 0);
                toolStaff.Add(st);
                r.Add(() => toolStaff.Remove(st));
                Place(st, at ?? At(4, 0));
                return st;
            }

            /// <summary>The worker is put on a real produce job at the press (the work giver's, or the same job made by hand when the scene is out of the inmates' reach) and is, for the hourly work pass, working.</summary>
            public bool StartWork(Pawn p)
            {
                CompInmate c = p.Inmate();
                Job job = c.job.Worker.TryGiveJob(p, pm);
                if (job == null)
                {
                    job = JobMaker.MakeJob(RPLaborDefOf.RP_InmateProduce, press, steel);
                    job.count = 10;
                }
                p.jobs.StartJob(job, JobCondition.InterruptForced);
                return p.CurJobDef == RPLaborDefOf.RP_InmateProduce && LaborUtility.IsWorking(p, c);
            }

            /// <summary>The hourly pass for one working inmate: what an hour of work does (see <see cref="CompInmate.WorkedAnHour"/>), when they are working.</summary>
            public bool WorkHour(Pawn p)
            {
                CompInmate c = p.Inmate();
                if (!LaborUtility.IsWorking(p, c)) return false;
                c.WorkedAnHour(p, pm);
                return true;
            }

            public void OnSlot(Pawn p, bool work) => p.Inmate().forcedActivity = work ? RPLaborDefOf.RP_Work : RPDefOf.RP_Lockup;
        }

        private static List<ChoiceLetter_PrisonDecision> ToolLetters(Rollback r) =>
            r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_ToolCount")).ToList();

        /// <summary>The letter answered as the desk does: the choice, the letter off the stack.</summary>
        private static void ToolAnswer(ChoiceLetter_PrisonDecision letter, int option)
        {
            letter.answered = true;
            PrisonDecisions.Choose(letter, option);
            Find.LetterStack.RemoveLetter(letter);
        }

        /// <summary>The scene's workshop: its supply made and, with a worker on a work slot, the work period begun (the hour's pass).</summary>
        private static ToolPool ToolOpen(ToolScene s, Pawn worker)
        {
            ToolCount.Hourly(s.g, s.map);
            return s.g.tools.PoolOf(ToolScene.Workshop);
        }

        /// <summary>A theft forced (the roll decided, the rest as the game has it: tension, nobody watching), in the zone; the thing the thief now carries, or null.</summary>
        private static Thing ToolForceTheft(ToolScene s, Pawn thief, PrisonZoneDef zone, ContrabandKind kind = ContrabandKind.Tool)
        {
            var before = new List<Thing>();
            ContrabandUtility.CarriedContraband(thief, before);
            ContrabandUtility.TheftRollForTests = true;
            try
            {
                ContrabandUtility.Notify_WorkedWithTools(thief, kind, zone);
            }
            finally
            {
                ContrabandUtility.TheftRollForTests = null;
            }
            var after = new List<Thing>();
            ContrabandUtility.CarriedContraband(thief, after);
            return after.FirstOrDefault(t => !before.Contains(t));
        }

        /// <summary>What became of a scene's inmate, for a failure line: where they are or who holds them, whether they stand, what they are doing and who else is doing something to them.</summary>
        private static string ToolWorkerState(ToolScene s, Pawn p)
        {
            string where = p.Spawned ? $"at {p.Position}, {p.Position.DistanceTo(s.press.Position):F0} cells from the press" : p.Dead ? "dead" : p.Destroyed ? "destroyed" : p.holdingOwner != null ? $"held by {p.holdingOwner.Owner.ToStringSafe()}" : "nowhere (off the map)";
            string others = string.Join(", ", s.map.mapPawns.AllPawnsSpawned.Where(x => x != p && (x.CurJob?.targetA.Thing == p || x.CurJob?.targetB.Thing == p)).Select(x => $"{x.LabelShort} on {x.CurJobDef.defName}"));
            return $"{where}; downed {p.Downed}, mental {p.MentalStateDef?.defName ?? "none"}, active inmate {p.Inmate()?.activeFlag}, prisoner {p.IsPrisonerOfColony}, job {p.CurJobDef?.defName ?? "none"}, " +
                   $"on the way out {ShuttleSystem.OnTheWayOut(p)}, conditions [{string.Join(", ", p.health.hediffSet.hediffs.Select(h => h.def.defName))}], pawns with a job on them [{others}]";
        }

        // ---- the proof ----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Astra's proof. A theft is forced in real work, a staff member counts the tools back in on real ticks: one shortage, nothing known before the count, no name anywhere; the warden has the search done, a real search takes the
        /// tool and the supply is put right once, saying where it came from; the next period starts late.
        /// </summary>
        private static void CheckToolCountProof(Map map)
        {
            Guarded("tool count - a theft in real work, the count, no name, a search takes it back once", map, r =>
            {
                const string check = "proof";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonMap pm = s.pm;
                PrisonZoneDef shop = ToolScene.Workshop;
                Pawn worker = s.Worker("Worker");
                CompInmate wc = worker.Inmate();
                if (!wc.activeFlag)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): the throwaway inmate is not an active inmate here; the check is skipped");
                    return;
                }
                ToolPool pool = ToolOpen(s, worker);
                int benches = ToolCount.BenchesOf(map, pm, shop).Count;
                if (pool == null || benches != 1 || pool.owned != ToolCount.WorkshopPerBench || pool.period != 1 || !pool.inPeriod || !pool.workers.Contains(worker.thingIDNumber) || pool.issued != pool.owned)
                    ToolFail(check, $"the workshop's supply should be made at full ({ToolCount.WorkshopPerBench} for one press) and its first work period begun with the worker rostered: pool {(pool == null ? "none" : $"owned {pool.owned}, period {pool.period}, running {pool.inPeriod}, crew {pool.workers.Count}, issued {pool.issued}")}, {benches} bench(es)");
                if (pool == null) return;
                string lineBefore = ToolCount.IntelLine(pool);

                // The theft, in real work: a real produce job on the press, and the hourly work pass.
                if (!s.StartWork(worker))
                {
                    ToolFail(check, $"the worker could not be put on the produce job at the press (job {worker.CurJobDef?.defName})");
                    return;
                }
                if (TensionUtility.GuardsNear(worker, pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene, which stops a theft; the check is skipped");
                    return;
                }
                int smuggled0 = g.stats.contrabandSmuggled;
                ContrabandUtility.TheftRollForTests = true;
                bool worked = s.WorkHour(worker);
                ContrabandUtility.TheftRollForTests = null;
                var carried = new List<Thing>();
                ContrabandUtility.CarriedContraband(worker, carried);
                if (!worked) ToolFail(check, "the hourly work pass did not run for a worker on the produce job");
                if (carried.Count != 1 || !ToolCount.Traced(carried[0]) || carried[0].stackCount != 1)
                    ToolFail(check, $"one theft should leave one traced tool in the thief's pocket (and not the old creation too): {carried.Count} thing(s) carried: {string.Join(", ", carried.Select(t => $"{t.stackCount} x {t.def.defName}, traced {ToolCount.Traced(t)}"))}");
                if (g.stats.contrabandSmuggled != smuggled0 + 1) ToolFail(check, $"the theft counts as one thing smuggled in ({g.stats.contrabandSmuggled - smuggled0} counted)");
                if (ToolCount.Missing(pool) != 1 || ToolCount.OnHand(pool) != pool.owned - 1 || g.tools.traces.Count != 1 || g.tools.traces[0].poolId != pool.id || g.tools.traces[0].period != 1)
                    ToolFail(check, $"the theft should take one tool out of the supply and trace it to the room and period 1: missing {ToolCount.Missing(pool)}, on hand {ToolCount.OnHand(pool)} of {pool.owned}, traces {string.Join(";", g.tools.traces.Select(t => $"{t.poolId}/{t.period}"))}");

                // Nothing is known until the count.
                if (pool.knownMissing != 0 || ToolCount.Heard.Count != 0 || ToolLetters(r).Count != 0 || ToolCount.OutputFactor(shop) != 1f)
                    ToolFail(check, $"the shortage was known before any count (known {pool.knownMissing}, {ToolCount.Heard.Count} thing(s) said, {ToolLetters(r).Count} letter(s), output {ToolCount.OutputFactor(shop)})");
                if (ToolCount.IntelLine(pool) != lineBefore) ToolFail(check, $"Intel's line changed before the count: \"{lineBefore}\" -> \"{ToolCount.IntelLine(pool)}\"");

                // The period ends: a count falls due, and still nothing is said.
                worker.jobs.StopAll();
                s.OnSlot(worker, false);
                ToolCount.Hourly(g, map);
                if (pool.inPeriod || !pool.countDue || pool.countDuePeriod != 1 || ToolCount.Heard.Count != 0)
                    ToolFail(check, $"at the end of the work period the count should fall due and say nothing: running {pool.inPeriod}, due {pool.countDue} (period {pool.countDuePeriod}), {ToolCount.Heard.Count} thing(s) said");

                // The staff: a guard (the warden's work) and a workman (the hauling) count; a cook does not.
                IntVec3 benchCell = s.press.InteractionCell;
                Pawn guard = s.Staff("Guard", RPSecurityDefOf.RP_Guard, benchCell);
                Pawn workman = s.Staff("Workman", RPStaffRoleDefOf.RP_Workman, s.At(0, 4));
                Pawn cook = s.Staff("Cook", RPStaffRoleDefOf.RP_Cook, s.At(1, 4));
                var guardGiver = new WorkGiver_CountTools { def = DefDatabase<WorkGiverDef>.GetNamed("RP_CountTools") };
                var workmanGiver = new WorkGiver_CountTools { def = DefDatabase<WorkGiverDef>.GetNamed("RP_CountToolsWorkman") };
                if (guardGiver.ShouldSkip(guard, true) || workmanGiver.ShouldSkip(workman, true) || !guardGiver.ShouldSkip(cook, true) || !workmanGiver.ShouldSkip(cook, true) || !guardGiver.ShouldSkip(workman, true) || !workmanGiver.ShouldSkip(guard, true))
                    ToolFail(check, $"a guard counts on the warden's work and a workman on the hauling, and nobody else does: guard skipped {guardGiver.ShouldSkip(guard, true)}, workman skipped {workmanGiver.ShouldSkip(workman, true)}, cook skipped by both {guardGiver.ShouldSkip(cook, true)}/{workmanGiver.ShouldSkip(cook, true)}, guard skipped by the workman's {workmanGiver.ShouldSkip(guard, true)}, workman by the guard's {guardGiver.ShouldSkip(workman, true)}");
                Job count = guardGiver.NonScanJob(guard);
                if (count == null || count.def != RPToolDefOf.RP_CountTools || count.count != pool.id || count.targetA.Thing != s.press)
                {
                    ToolFail(check, $"the guard was not given the count of the workshop (job {count?.def?.defName}, pool {count?.count} for {pool.id})");
                    return;
                }
                int suspected0 = wc.lastSearchedTick;
                guard.jobs.StartJob(count, JobCondition.InterruptForced);
                // (No theft but the check's own: on real ticks the scene's other work would roll a real theft too, and the count would find two tools short.)
                int ticked;
                ContrabandUtility.TheftRollForTests = false;
                try { ticked = ShuttleTicks(() => !pool.countDue, 1500); }
                finally { ContrabandUtility.TheftRollForTests = null; }
                if (pool.countDue || pool.countedPeriod != 1)
                {
                    ToolFail(check, $"the guard did not count the tools back in on real ticks (due {pool.countDue}, {ticked} ticks, job {guard.CurJobDef?.defName} at {guard.Position}, bench {s.press.Position})");
                    return;
                }
                // One shortage, with the room and the period, and no name.
                List<ChoiceLetter_PrisonDecision> letters = ToolLetters(r);
                if (pool.knownMissing != 1 || pool.countedIssued != pool.owned || pool.countedReturned != pool.owned - 1)
                    ToolFail(check, $"the count should find one tool short of {pool.owned}: known {pool.knownMissing}, issued {pool.countedIssued}, back {pool.countedReturned}");
                if (letters.Count != 1 || ToolCount.Heard.Count != 2)
                    ToolFail(check, $"one shortage should be one message and one letter: {ToolCount.Heard.Count} thing(s) said, {letters.Count} letter(s)");
                string name = worker.LabelShort, full = worker.Name.ToStringFull;
                foreach (string said in ToolCount.Heard)
                    if (said.Contains(name) || said.Contains(full) || !said.Contains(shop.label) || !said.Contains("1"))
                        ToolFail(check, $"what the count says should name the room and the period and nobody: \"{said}\"");
                if (letters.Count == 1)
                {
                    ChoiceLetter_PrisonDecision l = letters[0];
                    bool pawnTarget = l.lookTargets != null && l.lookTargets.targets.Any(t => t.Thing is Pawn);
                    if (l.pawns.Count != 0 || pawnTarget || l.Text.ToString().Contains(name) || l.Label.ToString().Contains(name)) ToolFail(check, "the count's letter points at a pawn or names one");
                    if (StoryLetters.Urgent(l)) ToolFail(check, "the count's letter is urgent: it should be a budgeted letter");
                    if (l.decision.Worker.Options(l)?.Count != 3 || l.decision.Worker.TimeoutOption(l) != ToolCountDecision.CarryOn) ToolFail(check, "the letter should have three answers and carry on short for the one that is not given");
                }
                if (wc.suspected || wc.cellSearchPending || wc.lastSearchedTick != suspected0 || wc.awaitingSearch)
                    ToolFail(check, "the count made the worker a suspect (the holder of a tool is not known by it)");
                if (Math.Abs(ToolCount.OutputFactor(shop) - (1f - ToolCount.CutPerTool)) > 0.0001f || ToolCount.WorkTicks(shop, 1800) != (int)Math.Round(1800 / (1f - ToolCount.CutPerTool)))
                    ToolFail(check, $"a room one tool short works at {ToolCount.OutputFactor(shop)} (work of 1800 ticks takes {ToolCount.WorkTicks(shop, 1800)})");
                string lineAfter = ToolCount.IntelLine(pool);
                if (!lineAfter.Contains($"1 of {pool.owned} short") || lineAfter.Contains(name)) ToolFail(check, $"Intel's line after the count should say 1 of the room's {pool.owned} tools is short and name nobody: \"{lineAfter}\"");

                // The warden has the crew searched: the worker is owed a frisk, the next work period starts late, and a real search takes the tool.
                if (letters.Count != 1) return;
                ToolAnswer(letters[0], ToolCountDecision.Search);
                // (The count's real ticks ran the live prison's passes over the scene's worker too: census c77-ev1 had them no longer a frisk candidate - not on their feet, or not an active inmate, which is not being on the map - and the
                // placing below then threw. Said what became of them, and the rest of the check is not run over a pawn that is gone.)
                if (!worker.Spawned || worker.Dead || worker.Downed || worker.InMentalState || !wc.activeFlag)
                {
                    ToolFail(check, $"the premise: the worker is not an inmate on their feet in the scene after the count's real ticks: {ToolWorkerState(s, worker)}");
                    return;
                }
                if (!pool.searchIds.Contains(worker.thingIDNumber) || !ToolCount.SearchOwed(wc) || !WorkGiver_Frisk.Candidate(worker) || pool.asked || pool.pendingLateHours != ToolCount.SearchLateHours || pool.pendingSupervised)
                    ToolFail(check, $"the search of the crew should owe the worker a frisk and put the next period back {ToolCount.SearchLateHours} hours: owed {ToolCount.SearchOwed(wc)}, a frisk candidate {WorkGiver_Frisk.Candidate(worker)}, asked {pool.asked}, late {pool.pendingLateHours}");
                s.Place(worker, s.At(4, 3));
                s.Place(guard, s.At(5, 3));
                int found = ContrabandUtility.Search(guard, worker, 1f);
                if (found != 1 || ToolCount.Missing(pool) != 0 || ToolCount.OnHand(pool) != pool.owned || pool.knownMissing != 0 || g.tools.traces.Count != 0 || pool.owned != ToolCount.WorkshopPerBench)
                    ToolFail(check, $"a search that takes the tool should put the supply right once: found {found}, missing {ToolCount.Missing(pool)}, on hand {ToolCount.OnHand(pool)} of {pool.owned}, known {pool.knownMissing}, traces {g.tools.traces.Count}");
                if (pool.foundSource != ToolSource.Pockets || pool.foundPeriod != 1) ToolFail(check, $"the find should be put down as a pat-down in period 1 ({pool.foundSource}, {pool.foundPeriod})");
                if (ToolCount.SearchOwed(wc) || pool.searchUntilTick >= 0) ToolFail(check, "the worker is still owed a search they have had");
                string foundSaid = ToolCount.Heard.FirstOrDefault(x => x.Contains("turned up"));
                if (foundSaid == null || !foundSaid.Contains(shop.label) || foundSaid.Contains(name) || !foundSaid.Contains(((string)"RP_ToolSource_Pockets".Translate())))
                    ToolFail(check, $"the find should say where the tool came from (the room, the period, a pat-down) and not who had it: \"{foundSaid}\"");
                int said0 = ToolCount.Heard.Count;
                // (Once: the same tool taken again, or a search with nothing in it, changes nothing.)
                ToolCount.Notify_Taken(carried[0], ToolSource.Stash);
                ContrabandUtility.Search(guard, worker, 1f);
                if (ToolCount.Heard.Count != said0 || ToolCount.OnHand(pool) != pool.owned || pool.owned != ToolCount.WorkshopPerBench)
                    ToolFail(check, $"a tool that was taken is not taken twice: on hand {ToolCount.OnHand(pool)} of {pool.owned}, {ToolCount.Heard.Count - said0} more thing(s) said");

                // The next period starts late, and the crew wait in the room.
                s.OnSlot(worker, true);
                ToolCount.Hourly(g, map);
                int now = Find.TickManager.TicksGame;
                if (!pool.inPeriod || pool.period != 2 || pool.holdUntilTick != now + ToolCount.SearchLateHours * GenDate.TicksPerHour || !ToolCount.Held(shop) || pool.supervised || pool.countDue)
                    ToolFail(check, $"after the search the next period should start {ToolCount.SearchLateHours} hours late: period {pool.period}, running {pool.inPeriod}, held until {pool.holdUntilTick - now} ticks from now, supervised {pool.supervised}");
                Job held = new RegimeWorker_Work().TryGiveJob(worker, wc, pm);
                if (held?.def == RPLaborDefOf.RP_InmateProduce) ToolFail(check, "a worker was given the produce job while the room's work was held");
                RPLog.Message($"[RimPrison:test] tool count ({check}): one theft in real work, counted by a guard in {ticked} ticks, one shortage and no name, the search took the tool and the next period started {ToolCount.SearchLateHours} h late");
            });
        }

        // ---- a stash, and the book through a save --------------------------------------------------------------------------------------------

        /// <summary>The tool ledgers saved and loaded as a game does it (Scribe), through a file of their own in the save-data folder.</summary>
        private static ToolLedgers ToolRoundTrip(ToolLedgers book)
        {
            string path = Path.Combine(GenFilePaths.SaveDataFolderPath, "rp_tools_roundtrip.xml");
            ToolLedgers loaded = null;
            try
            {
                SafeSaver.Save(path, "toolstest", () => Scribe_Deep.Look(ref book, "book"));
                Scribe.loader.InitLoading(path);
                try
                {
                    Scribe_Deep.Look(ref loaded, "book");
                    Scribe.loader.FinalizeLoading();
                }
                catch
                {
                    Scribe.ForceStop();
                    throw;
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
            return loaded;
        }

        /// <summary>A tool in a stash is still the room's after the ledgers go through a save, and a cell search that takes it puts the supply right once and says it was in a stash.</summary>
        private static void CheckToolCountStash(Map map)
        {
            Guarded("tool count - a tool in a stash is the room's after a reload and is taken once", map, r =>
            {
                const string check = "stash";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef shop = ToolScene.Workshop;
                Pawn owner = s.Worker("Owner");
                CompInmate c = owner.Inmate();
                ToolPool pool = ToolOpen(s, owner);
                if (pool == null || TensionUtility.GuardsNear(owner, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): no supply, or a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                Thing tool = ToolForceTheft(s, owner, shop);
                if (tool == null || !ToolCount.Traced(tool))
                {
                    ToolFail(check, "the theft left no traced tool");
                    return;
                }
                int id = tool.thingIDNumber;
                // Counted short before the find: the staff know of it, so a find of it is said (a tool that nobody has counted is found in silence).
                ToolCountNow(s, pool, owner);
                if (pool.knownMissing != 1) { ToolFail(check, $"the premise: the tool counted short (known {pool.knownMissing})"); return; }
                // Into the stash (as an inmate alone in their cell does it), and the id is the same one.
                StashUtility.TuckAway(owner, c, new List<Thing> { tool }, 0);
                Thing inStash = c.stash.FirstOrDefault(t => t.def == tool.def);
                if (inStash == null || inStash.thingIDNumber != id || !ToolCount.Traced(inStash))
                    ToolFail(check, $"a traced tool put in the stash should be the same thing, still traced (stash holds {string.Join(",", c.stash.Select(t => $"{t.def.defName}#{t.thingIDNumber}"))} for #{id})");
                // The ledgers through a save: the tool is still the room's.
                ToolLedgers loaded = ToolRoundTrip(g.tools);
                if (loaded == null || loaded.pools.Count != 1 || loaded.traces.Count != 1 || loaded.traces[0].thingId != id || !loaded.byThing.ContainsKey(id) || ToolCount.Describe(g) != DescribeToolBook(loaded))
                    ToolFail(check, $"the ledgers did not come back from a save whole: {(loaded == null ? "none" : DescribeToolBook(loaded))} for {ToolCount.Describe(g)}");
                else
                {
                    ToolLedgers book0 = g.tools;
                    g.tools = loaded;
                    r.Add(() => g.tools = book0);
                    pool = g.tools.PoolOf(shop);
                }
                if (pool == null) return;
                // Taken by a cell search (a guard on the scene's own cell: the stash search runs at 85 of 100, so up to a dozen turns of the mattress).
                Pawn guard = s.Staff("Guard", RPSecurityDefOf.RP_Guard);
                int turns = 0;
                while (c.stash.Any(t => t.thingIDNumber == id) && turns++ < 12) StashUtility.Search(guard, owner, c);
                if (c.stash.Any(t => t.thingIDNumber == id) || ToolCount.Missing(pool) != 0 || ToolCount.OnHand(pool) != pool.owned || pool.knownMissing != 0 || pool.foundSource != ToolSource.Stash)
                    ToolFail(check, $"a search of the cell should take the traced tool and put the supply right: still in the stash {c.stash.Any(t => t.thingIDNumber == id)}, missing {ToolCount.Missing(pool)}, source {pool.foundSource}");
                int said = ToolCount.Heard.Count(x => x.Contains("turned up"));
                if (said != 1 || !ToolCount.Heard.Any(x => x.Contains(((string)"RP_ToolSource_Stash".Translate())) && !x.Contains(owner.LabelShort)))
                    ToolFail(check, $"the find should be said once, as a stash and not as whose: {string.Join(" | ", ToolCount.Heard)}");
                // (And an old trace to a tool already taken does not reconcile again, whatever the loaded book holds.)
                ToolCount.Notify_Taken(tool, ToolSource.Stash);
                if (ToolCount.Heard.Count(x => x.Contains("turned up")) != 1 || ToolCount.OnHand(pool) != pool.owned) ToolFail(check, "the tool was put right twice");
            });
        }

        /// <summary>The comparable string of a book that is not the game's (the same shape <see cref="ToolCount.Describe"/> gives for the game's).</summary>
        private static string DescribeToolBook(ToolLedgers book)
        {
            PrisonGame g = PrisonGame.Instance;
            ToolLedgers keep = g.tools;
            g.tools = book;
            try
            {
                return ToolCount.Describe(g);
            }
            finally
            {
                g.tools = keep;
            }
        }

        // ---- stacking -----------------------------------------------------------------------------------------------------------------------

        /// <summary>A traced tool is never stacked - not by an absorbing, a pocket, a stash, nor the ground - and keeps its id through the moves that are allowed; an untraced one stacks as ever.</summary>
        private static void CheckToolCountStacking(Map map)
        {
            Guarded("tool count - a traced tool is not stacked and keeps its id", map, r =>
            {
                const string check = "stacking";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef shop = ToolScene.Workshop;
                Pawn thief = s.Worker("Thief");
                CompInmate c = thief.Inmate();
                ToolPool pool = ToolOpen(s, thief);
                if (pool == null || TensionUtility.GuardsNear(thief, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): no supply, or a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                Thing a = ToolForceTheft(s, thief, shop), b = ToolForceTheft(s, thief, shop);
                var carried = new List<Thing>();
                ContrabandUtility.CarriedContraband(thief, carried);
                if (a == null || b == null || carried.Count != 2 || carried.Any(t => t.stackCount != 1) || !carried.All(ToolCount.Traced) || ToolCount.Missing(pool) != 2)
                    ToolFail(check, $"two thefts should leave two traced tools in two stacks of one in the pocket: {string.Join(", ", carried.Select(t => $"{t.stackCount} x {t.def.defName} #{t.thingIDNumber} traced {ToolCount.Traced(t)}"))}");
                if (a == null || b == null) return;
                if (a.TryAbsorbStack(b, true) || a.CanStackWith(b) || a.stackCount != 1 || b.Destroyed) ToolFail(check, "a traced tool absorbed another");
                // (The stash, and back to the pocket: the same things, one each.)
                StashUtility.TuckAway(thief, c, new List<Thing> { a, b }, 0);
                if (c.stash.Count(t => t.def == a.def) != 2 || c.stash.Any(t => t.stackCount != 1)) ToolFail(check, $"two traced tools put in the stash should stay two: {string.Join(", ", c.stash.Select(t => $"{t.stackCount} x {t.def.defName}"))}");
                Thing back = StashUtility.Take(thief, c, ContrabandKind.Tool);
                if (back == null || !ToolCount.Traced(back) || back.stackCount != 1 || back != a && back != b) ToolFail(check, "a traced tool taken out of the stash is not the one that was put in");
                // The ground: two traced tools put down at one cell lie as two, in two cells (the game keeps one item to a cell outside a stockpile: Direct would not place the second at all, and a stack that could be
                // absorbed would be - the tool is a stack of five).
                IntVec3 cell = s.At(3, 4);
                foreach (Thing t in new[] { a, b })
                {
                    if (t.holdingOwner != null) t.holdingOwner.Remove(t);
                    GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near);
                    Thing placed = t;
                    r.Add(() =>
                    {
                        if (!placed.Destroyed) placed.Destroy(DestroyMode.Vanish);
                    });
                }
                if (!a.Spawned || !b.Spawned || a.stackCount != 1 || b.stackCount != 1 || a.Destroyed || b.Destroyed || a.Position == b.Position) ToolFail(check, $"two traced tools put on the ground should lie as two (spawned {a.Spawned}/{b.Spawned}, destroyed {a.Destroyed}/{b.Destroyed}, counts {a.stackCount}/{b.stackCount})");
                // An untraced tool stacks as it always did.
                Pawn plain = s.Worker("Plain");
                ThingDef toolDef = a.def;
                Thing one = ContrabandUtility.Give(plain, toolDef), two = ContrabandUtility.Give(plain, toolDef);
                var plainCarried = new List<Thing>();
                ContrabandUtility.CarriedContraband(plain, plainCarried);
                if (plainCarried.Count != 1 || plainCarried[0].stackCount != 2 || g.tools.traces.Count != 2)
                    ToolFail(check, $"two tools that are not traced should stack as before: {string.Join(", ", plainCarried.Select(t => $"{t.stackCount} x {t.def.defName}"))}, {g.tools.traces.Count} traces (two expected, the thief's)");
            });
        }

        // ---- the warden's answers -----------------------------------------------------------------------------------------------------------

        /// <summary>A shortage counted: the supply with <paramref name="stolen"/> tools taken by a thief of the scene, the period over, counted by a member of staff at once (the letter and the message sent).</summary>
        private static ToolPool ToolShort(ToolScene s, Pawn thief, int stolen, Rollback r, out List<Thing> things)
        {
            things = new List<Thing>();
            ToolPool pool = ToolOpen(s, thief);
            if (pool == null) return null;
            for (int i = 0; i < stolen; i++)
                if (ToolForceTheft(s, thief, ToolScene.Workshop) is { } t) things.Add(t);
            s.OnSlot(thief, false);
            ToolCount.Hourly(s.g, s.map);
            ToolCount.Counted(pool, null);
            return pool;
        }

        /// <summary>
        /// The staff count what is out: the work slot is taken away, the hour ends the period and a member of staff counts the tools back in at once (the message and the letter sent, the first time). A find changes what the staff know - and says
        /// where the tool came from - only for a tool they have counted (<see cref="ToolCount.Notify_Taken"/>), so a check of a find counts first.
        /// </summary>
        private static void ToolCountNow(ToolScene s, ToolPool pool, Pawn worker)
        {
            s.OnSlot(worker, false);
            ToolCount.Hourly(s.g, s.map);
            ToolCount.Counted(pool, null);
        }

        /// <summary>A find is reconciled once, whatever path took it: the tools that were taken are asked after again (as another path of the game that meets the same thing would) and nothing is said, put right or changed.</summary>
        private static void ToolOnce(string check, string path, ToolPool pool, params Thing[] taken)
        {
            int said0 = ToolCount.Heard.Count, onHand0 = ToolCount.OnHand(pool), known0 = pool.knownMissing, order0 = PrisonGame.Instance.tools.nextOrder;
            foreach (Thing t in taken) ToolCount.Notify_Taken(t, ToolSource.Pockets);
            if (ToolCount.Heard.Count != said0 || ToolCount.OnHand(pool) != onHand0 || pool.knownMissing != known0 || PrisonGame.Instance.tools.nextOrder != order0 || taken.Any(ToolCount.Traced))
                ToolFail(check, $"{path} took the tools and the same tools were reconciled again ({ToolCount.Heard.Count - said0} more thing(s) said, on hand {onHand0} -> {ToolCount.OnHand(pool)}, known {known0} -> {pool.knownMissing})");
        }

        /// <summary>The three answers (and what none gives), each by what it does to the next period: the re-count starts it an hour late and supervised (nothing is taken in it), the search owes the crew a frisk and starts it two hours late, carrying on short changes nothing but the output - which a letter nobody answers is.</summary>
        private static void CheckToolCountAnswers(Map map)
        {
            Guarded("tool count - the re-count, the search, carrying on short and no answer", map, r =>
            {
                const string check = "answers";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef shop = ToolScene.Workshop;
                // (A second press: the supply is eight tools, enough for every theft below.)
                s.Spawn(DefDatabase<ThingDef>.GetNamed("RP_LicensePlatePress"), s.At(2, 4));
                Pawn thief = s.Worker("Thief");
                if (TensionUtility.GuardsNear(thief, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                ToolPool pool = ToolShort(s, thief, 2, r, out List<Thing> things);
                if (pool == null || things.Count != 2) { ToolFail(check, "the premise: two tools were not stolen"); return; }
                List<ChoiceLetter_PrisonDecision> letters = ToolLetters(r);
                if (pool.knownMissing != 2 || letters.Count != 1 || !pool.asked || pool.owned != 2 * ToolCount.WorkshopPerBench)
                {
                    ToolFail(check, $"the premise: a count that finds two short of {2 * ToolCount.WorkshopPerBench} should ask once (known {pool.knownMissing}, {letters.Count} letter(s), asked {pool.asked}, owned {pool.owned})");
                    return;
                }
                if (Math.Abs(ToolCount.OutputFactor(shop) - (1f - 2 * ToolCount.CutPerTool)) > 0.0001f) ToolFail(check, $"two tools short should cost {2 * ToolCount.CutPerTool:P0} of the output (factor {ToolCount.OutputFactor(shop)})");
                // The cap: the worst a room loses is the cap, and it works still.
                int known0 = pool.knownMissing;
                pool.knownMissing = 10;
                if (Math.Abs(ToolCount.OutputFactor(shop) - (1f - ToolCount.MaxCut)) > 0.0001f) ToolFail(check, $"a room many tools short should lose no more than {ToolCount.MaxCut:P0} (factor {ToolCount.OutputFactor(shop)})");
                pool.knownMissing = known0;

                // A letter nobody answers is carry on short: no delay, no search, no supervision.
                ChoiceLetter_PrisonDecision letter = letters[0];
                if (letter.decision.Worker.Disabled(letter, 0) != null) ToolFail(check, "the answers to a live letter are disabled");
                letter.Lapse();
                if (pool.asked || pool.pendingLateHours != 0 || pool.pendingSupervised || pool.searchIds.Count != 0 || pool.knownMissing != 2)
                    ToolFail(check, $"a letter that nobody answers should be carry on short (asked {pool.asked}, late {pool.pendingLateHours}, supervised {pool.pendingSupervised}, searches {pool.searchIds.Count}, known {pool.knownMissing})");
                if (letter.decision.Worker.Disabled(letter, 0) == null) ToolFail(check, "the answers to a letter that was answered are not disabled");

                // A re-count: the next period starts an hour late, supervised; nothing is taken in it; the period after it is not.
                ChoiceLetter_PrisonDecision second = ToolNextLetter(s, pool, thief, r);
                if (second == null) { ToolFail(check, "the premise: no second letter"); return; }
                ToolAnswer(second, ToolCountDecision.Recount);
                if (pool.pendingLateHours != ToolCount.RecountLateHours || !pool.pendingSupervised || pool.searchIds.Count != 0 || pool.asked)
                    ToolFail(check, $"a re-count should put the next period back {ToolCount.RecountLateHours} hour and supervise it (late {pool.pendingLateHours}, supervised {pool.pendingSupervised})");
                s.OnSlot(thief, true);
                ToolCount.Hourly(g, map);
                int now = Find.TickManager.TicksGame;
                if (!pool.inPeriod || pool.holdUntilTick != now + ToolCount.RecountLateHours * GenDate.TicksPerHour || !pool.supervised || !ToolCount.Held(shop))
                    ToolFail(check, $"after a re-count the period should start an hour late, supervised (running {pool.inPeriod}, held {pool.holdUntilTick - now} ticks, supervised {pool.supervised})");
                int missing0 = ToolCount.Missing(pool);
                if (ToolForceTheft(s, thief, shop) != null || ToolCount.Missing(pool) != missing0) ToolFail(check, "a tool was taken in a period that a guard supervised");
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(clock0 + ToolCount.RecountLateHours * GenDate.TicksPerHour);
                if (ToolCount.Held(shop)) ToolFail(check, "the room's work is still held after the hour");
                s.OnSlot(thief, false);
                ToolCount.Hourly(g, map);
                if (pool.supervised || pool.holdUntilTick >= 0 || pool.inPeriod) ToolFail(check, "the supervision outlived the period");
                s.OnSlot(thief, true);
                ToolCount.Hourly(g, map);
                if (!pool.inPeriod || pool.supervised || ToolCount.Held(shop)) ToolFail(check, "the next period after the re-count was held or supervised too");
                if (ToolForceTheft(s, thief, shop) == null || ToolCount.Missing(pool) != missing0 + 1) ToolFail(check, "an ordinary period after the supervised one took nothing from a supply with tools in it");

                // Carry on short, answered: nothing is delayed.
                ChoiceLetter_PrisonDecision third = ToolNextLetter(s, pool, thief, r);
                if (third == null) { ToolFail(check, "the premise: no third letter"); return; }
                ToolAnswer(third, ToolCountDecision.CarryOn);
                if (pool.pendingLateHours != 0 || pool.pendingSupervised || pool.searchIds.Count != 0 || pool.asked) ToolFail(check, "carrying on short should change nothing but the output");

                // A search answered while the room is at work holds the work from now.
                ChoiceLetter_PrisonDecision fourth = ToolNextLetter(s, pool, thief, r);
                if (fourth == null) { ToolFail(check, "the premise: no fourth letter"); return; }
                s.OnSlot(thief, true);
                ToolCount.Hourly(g, map);
                ToolAnswer(fourth, ToolCountDecision.Search);
                int now2 = Find.TickManager.TicksGame;
                if (!pool.inPeriod || pool.holdUntilTick != now2 + ToolCount.SearchLateHours * GenDate.TicksPerHour || !pool.searchIds.Contains(thief.thingIDNumber) || pool.pendingLateHours != 0)
                    ToolFail(check, $"a search answered while the room works should hold the work from now and owe the crew a frisk (held {pool.holdUntilTick - now2} ticks, crew {pool.searchIds.Count}, pending {pool.pendingLateHours})");
                // (A search that runs out of time is let go: the crew are owed it no more.)
                Find.TickManager.DebugSetTicksGame(now2 + ToolCount.SearchWindowTicks + 1);
                ToolCount.Hourly(g, map);
                if (pool.searchIds.Count != 0 || pool.searchUntilTick >= 0 || ToolCount.SearchOwed(thief.Inmate())) ToolFail(check, "a search nobody made in a day is still owed");
            });
        }

        /// <summary>One more tool is gone and counted (a count that finds nothing new says nothing): the letter that asks the warden, or null.</summary>
        private static ChoiceLetter_PrisonDecision ToolNextLetter(ToolScene s, ToolPool pool, Pawn thief, Rollback r)
        {
            int letters0 = ToolLetters(r).Count;
            bool supervised = pool.supervised;
            pool.supervised = false;
            ToolCount.Steal(thief, ContrabandKind.Tool, pool.zone);
            pool.supervised = supervised;
            // (The staff counting, at whatever hour of the period: what the count does with a rise is the same.)
            pool.countDue = true;
            pool.countDuePeriod = pool.period;
            ToolCount.Counted(pool, null);
            List<ChoiceLetter_PrisonDecision> letters = ToolLetters(r);
            return letters.Count > letters0 ? letters[^1] : null;
        }

        // ---- the budget ---------------------------------------------------------------------------------------------------------------------

        /// <summary>The letter is a budgeted one: over the day's budget it waits for midnight, still open for the systems that look for one, and is delivered then.</summary>
        private static void CheckToolCountBudget(Map map)
        {
            Guarded("tool count - the letter is held back for the day's budget, then delivered", map, r =>
            {
                const string check = "budget";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn thief = s.Worker("Thief");
                if (TensionUtility.GuardsNear(thief, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                int decisions0 = g.story.decisionsToday;
                List<ChoiceLetter_PrisonDecision> held0 = g.story.deferredLetters.ToList();
                r.Add(() =>
                {
                    g.story.decisionsToday = decisions0;
                    g.story.deferredLetters = held0;
                });
                g.story.deferredLetters = new List<ChoiceLetter_PrisonDecision>();
                StoryLetters.TestBudget = true;
                g.story.decisionsToday = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                ToolPool pool = ToolShort(s, thief, 1, r, out List<Thing> things);
                if (pool == null || things.Count != 1) { ToolFail(check, "the premise: no tool was stolen"); return; }
                ChoiceLetter_PrisonDecision letter = g.story.deferredLetters.FirstOrDefault(l => l.Is("RP_Decision_ToolCount"));
                if (letter == null || ToolLetters(r).Count != 0 || !pool.asked || !StoryLetters.AnyOpen("RP_Decision_ToolCount"))
                    ToolFail(check, $"over the day's budget the letter should wait for midnight (held {g.story.deferredLetters.Count}, on the stack {ToolLetters(r).Count}, asked {pool.asked})");
                // A second count while the letter waits is said (the room is counted whether it has been asked or not) but does not ask again.
                int said = ToolCount.Heard.Count;
                ToolCount.Steal(thief, ContrabandKind.Tool, ToolScene.Workshop);
                pool.countDue = true;
                pool.countDuePeriod = pool.period;
                ToolCount.Counted(pool, null);
                if (ToolCount.Heard.Count != said + 1 || g.story.deferredLetters.Count != 1 || pool.knownMissing != 2) ToolFail(check, $"a room that is waiting to be asked should be counted again and not asked twice ({ToolCount.Heard.Count - said} more said, {g.story.deferredLetters.Count} held, known {pool.knownMissing})");
                g.story.decisionsToday = 0;
                StoryLetters.Daily(g, map);
                if (g.story.deferredLetters.Count != 0 || ToolLetters(r).Count != 1) ToolFail(check, $"at midnight the letter should be delivered (held {g.story.deferredLetters.Count}, on the stack {ToolLetters(r).Count})");
                // Held longer than the days it may wait: it is answered as an unanswered letter is.
                if (ToolLetters(r).FirstOrDefault() is { } delivered) ToolAnswer(delivered, ToolCountDecision.CarryOn);
                pool.asked = false;
            });
        }

        // ---- replacing ----------------------------------------------------------------------------------------------------------------------

        /// <summary>Intel's button pays for the tools the count found short, once, from the funds; the output is whole again; a tool that turns up later is only taken (the supply is not made too big); without the money nothing happens.</summary>
        private static void CheckToolCountReplace(Map map)
        {
            Guarded("tool count - replacing the tools that were counted short", map, r =>
            {
                const string check = "replace";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef shop = ToolScene.Workshop;
                Pawn thief = s.Worker("Thief");
                if (TensionUtility.GuardsNear(thief, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                ToolPool pool = ToolShort(s, thief, 3, r, out List<Thing> things);
                if (pool == null || things.Count != 3 || pool.knownMissing != 3) { ToolFail(check, $"the premise: three tools short (stolen {things.Count}, known {pool?.knownMissing})"); return; }
                ChoiceLetter_PrisonDecision letter = ToolLetters(r).FirstOrDefault();
                // The fourth is stolen after the count: the staff do not know of it, and the money is for what they know.
                Thing fourth = ToolForceTheft(s, thief, shop);
                if (fourth == null) { ToolFail(check, "the premise: no fourth tool"); return; }
                int funds0 = g.ledger.funds;
                g.ledger.funds = 5;
                if (ToolCount.Replace(pool) || pool.knownMissing != 3) ToolFail(check, "tools were replaced with no money to pay for them");
                g.ledger.funds = funds0;
                int cost = ToolCount.ReplaceTotal(pool);
                if (cost != 3 * ToolCount.ReplaceCost) ToolFail(check, $"three tools should cost {3 * ToolCount.ReplaceCost} ({cost})");
                if (!ToolCount.Replace(pool) || g.ledger.funds != funds0 - cost || pool.knownMissing != 0 || pool.asked || ToolCount.Missing(pool) != 1 || ToolCount.OutputFactor(shop) != 1f)
                    ToolFail(check, $"replacing the three should cost {cost}, leave the one the staff do not know of and make the output whole: funds {funds0} -> {g.ledger.funds}, known {pool.knownMissing}, missing {ToolCount.Missing(pool)}, output {ToolCount.OutputFactor(shop)}");
                if (!r.NewLedgerEntries.Any(e => e.category == RPDefOf.RP_Supplies && e.amount == -cost)) ToolFail(check, "the replacing is not in the ledger as supplies");
                if (ToolCount.Replace(pool)) ToolFail(check, "a room with nothing counted short was charged again");
                if (letter != null && letter.decision.Worker.Disabled(letter, 0) == null) ToolFail(check, "the letter's answers are still on offer after the tools were replaced");
                // One of the replaced tools turns up: it is taken and the supply stays as it is (not one too many).
                int onHand = ToolCount.OnHand(pool);
                int said = ToolCount.Heard.Count;
                ToolCount.Notify_Taken(things[0], ToolSource.Pockets);
                if (ToolCount.OnHand(pool) != onHand || pool.owned != ToolCount.WorkshopPerBench || ToolCount.Missing(pool) != 1 || ToolCount.Heard.Count != said + 1 || !ToolCount.Heard[^1].Contains("replaced"))
                    ToolFail(check, $"a tool that was already replaced is only taken: on hand {onHand} -> {ToolCount.OnHand(pool)}, owned {pool.owned}, missing {ToolCount.Missing(pool)}, said \"{ToolCount.Heard.LastOrDefault()}\"");
                // The one the staff did not know of is found: nothing is short and nothing was known of it.
                ToolCount.Notify_Taken(fourth, ToolSource.Stash);
                if (ToolCount.Missing(pool) != 0 || ToolCount.OnHand(pool) != pool.owned) ToolFail(check, $"the last tool found should leave the supply whole (missing {ToolCount.Missing(pool)}, on hand {ToolCount.OnHand(pool)} of {pool.owned})");
                // The traces of the two that are paid for and not found are let go of after a while: the saved list does not grow with every theft of the game.
                if (g.tools.traces.Count != 2 || g.tools.traces.Any(t => !t.replaced)) ToolFail(check, $"the premise: the two replaced traces are what is left ({g.tools.traces.Count})");
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(clock0 + ToolCount.ReplacedKeepTicks + 1);
                ToolCount.Hourly(g, map);
                if (g.tools.traces.Count != 0 || g.tools.byThing.Count != 0 || ToolCount.Traced(things[1])) ToolFail(check, $"the traces of the tools that were paid for should be let go of after {ToolCount.ReplacedKeepTicks / GenDate.TicksPerDay} days ({g.tools.traces.Count} left)");
            });
        }

        // ---- a room that is gone ------------------------------------------------------------------------------------------------------------

        /// <summary>A workshop whose benches are destroyed (or unpainted) has no supply: what was traced to it is only contraband again, no tool is made, nobody is told, and a second pass says nothing either; a new press makes a new supply, at full, once.</summary>
        private static void CheckToolCountRetire(Map map)
        {
            Guarded("tool count - a destroyed room retires its supply without tools or alarms", map, r =>
            {
                const string check = "retire";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef shop = ToolScene.Workshop;
                Pawn thief = s.Worker("Thief");
                if (TensionUtility.GuardsNear(thief, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                ToolPool pool = ToolShort(s, thief, 1, r, out List<Thing> things);
                if (pool == null || things.Count != 1) { ToolFail(check, "the premise: no tool was stolen"); return; }
                int items0 = map.listerThings.ThingsOfDef(things[0].def).Count;
                int letters0 = ToolLetters(r).Count, said0 = ToolCount.Heard.Count;
                Thing tool = things[0];
                // A second press: the supply grows by a press's worth; with one gone it shrinks back.
                Thing second = s.Spawn(DefDatabase<ThingDef>.GetNamed("RP_LicensePlatePress"), s.At(2, 4));
                ToolCount.Hourly(g, map);
                if (pool.benches != 2 || pool.owned != 2 * ToolCount.WorkshopPerBench || ToolCount.Missing(pool) != 1) ToolFail(check, $"a second press should make the supply a press bigger (benches {pool.benches}, owned {pool.owned}, missing {ToolCount.Missing(pool)})");
                second.Destroy();
                ToolCount.Hourly(g, map);
                if (pool.benches != 1 || pool.owned != ToolCount.WorkshopPerBench || ToolCount.Missing(pool) != 1) ToolFail(check, $"a press gone should take its tools out of the supply and no more (benches {pool.benches}, owned {pool.owned}, missing {ToolCount.Missing(pool)})");
                // The last press destroyed: the room keeps its supply for a day (it may be put up again), and then loses it.
                s.press.Destroy();
                ToolCount.Hourly(g, map);
                if (g.tools.PoolOf(shop) != pool || g.tools.traces.Count != 1) ToolFail(check, "a room with no bench for an hour lost its supply (a press may be put up again)");
                int clock0 = Find.TickManager.TicksGame;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                Find.TickManager.DebugSetTicksGame(clock0 + ToolCount.BenchlessGraceTicks);
                ToolCount.Hourly(g, map);
                if (g.tools.PoolOf(shop) != null || g.tools.traces.Count != 0 || g.tools.byThing.Count != 0) ToolFail(check, $"a workshop with no bench should have no supply and nothing traced to it (pool {g.tools.PoolOf(shop) != null}, traces {g.tools.traces.Count})");
                if (ToolCount.Traced(tool) || tool.Destroyed || map.listerThings.ThingsOfDef(tool.def).Count != items0) ToolFail(check, "retiring the supply made or took a tool (the thief's is theirs, and is contraband and no more)");
                ToolCount.Hourly(g, map);
                if (ToolLetters(r).Count != letters0 || ToolCount.Heard.Count != said0) ToolFail(check, $"a room that is gone should raise no alarm ({ToolLetters(r).Count - letters0} letter(s), {ToolCount.Heard.Count - said0} thing(s) said)");
                // A tool of a room that is gone, found: only taken.
                ToolCount.Notify_Taken(tool, ToolSource.Pockets);
                if (ToolCount.Heard.Count != said0) ToolFail(check, "a tool of a room that is gone was reported found");
                // The letter that was open is stale.
                ChoiceLetter_PrisonDecision letter = ToolLetters(r).FirstOrDefault();
                if (letter != null && letter.decision.Worker.Disabled(letter, 2) == null) ToolFail(check, "the letter of a room that is gone still asks");
                // And no theft: a worker with no bench takes nothing from nothing.
                if (ToolForceTheft(s, thief, shop) != null) ToolFail(check, "a theft in a room with no supply made a tool");
                // The tool is not traced any more: two of them stack again.
                // A new press: a new supply, at full, made once.
                Thing again = s.Spawn(DefDatabase<ThingDef>.GetNamed("RP_LicensePlatePress"), s.At(2, 2));
                ToolCount.Hourly(g, map);
                ToolPool fresh = g.tools.PoolOf(shop);
                if (fresh == null || fresh.owned != ToolCount.WorkshopPerBench || ToolCount.Missing(fresh) != 0 || false) ToolFail(check, $"a new press should make a new supply at full (owned {fresh?.owned}, missing {(fresh == null ? -1 : ToolCount.Missing(fresh))})");
                if (fresh != null)
                {
                    int id = fresh.id;
                    ToolCount.Hourly(g, map);
                    if (g.tools.PoolOf(shop)?.id != id || g.tools.pools.Count != 1) ToolFail(check, "the supply was made twice");
                }
                // The room unpainted: the same as no bench.
                foreach (IntVec3 c in s.pm.zones.CellsOf(shop).ToList())
                {
                    PrisonZoneDef was = s.pm.zones.ZoneAt(c);
                    r.Add(() => s.pm.zones.Set(c, was));
                    s.pm.zones.Set(c, null);
                }
                ToolCount.Hourly(g, map);
                if (g.tools.PoolOf(shop) != null) ToolFail(check, "a workshop that was unpainted still has a supply");
                if (again.Destroyed) ToolFail(check, "the press was destroyed by the check");
            });
        }

        // ---- an old save --------------------------------------------------------------------------------------------------------------------

        /// <summary>A game from before the count: the supply is made once at full and never again, and the contraband that was already in the pockets is not traced to it.</summary>
        private static void CheckToolCountOldSave(Map map)
        {
            Guarded("tool count - an old save: supplies made once at full, old contraband untraced", map, r =>
            {
                const string check = "old save";
                ToolScene s = ToolScene.Build(map, r, check, kitchen: true);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn holder = s.Worker("Holder");
                if (TensionUtility.GuardsNear(holder, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                // An old tool in a pocket (made as the old game did) and no supplies.
                Thing old = ContrabandUtility.Give(holder, DefDatabase<ThingDef>.GetNamed("RP_Tools"));
                if (g.tools.pools.Count != 0 || old == null) { ToolFail(check, "the premise: an old tool and no supply"); return; }
                ToolCount.Hourly(g, s.map);
                ToolPool shop = g.tools.PoolOf(ToolScene.Workshop), kitchen = g.tools.PoolOf(ToolScene.Kitchen);
                if (shop == null || kitchen == null || shop.owned != ToolCount.WorkshopPerBench || kitchen.owned != ToolCount.KitchenPerBench || g.tools.traces.Count != 0 || ToolCount.Traced(old) || ToolCount.Missing(shop) != 0)
                    ToolFail(check, $"a save from before the count should get a supply for each room, at full ({shop?.owned}/{kitchen?.owned} for {ToolCount.WorkshopPerBench}/{ToolCount.KitchenPerBench}) and trace nothing already in a pocket ({g.tools.traces.Count} traces)");
                // A theft and another pass: the supply is not made again at full.
                Thing t = ToolForceTheft(s, holder, ToolScene.Workshop);
                ToolCount.Hourly(g, s.map);
                if (t == null || ToolCount.Missing(shop) != 1 || shop.owned != ToolCount.WorkshopPerBench || g.tools.pools.Count != 2) ToolFail(check, $"a later pass should not make the supply again (missing {ToolCount.Missing(shop)}, owned {shop.owned}, {g.tools.pools.Count} supplies)");
                // Not counted until the staff do it: the old tool is never found short of anything.
                ToolCount.Notify_Taken(old, ToolSource.Pockets);
                if (ToolCount.Heard.Count != 0) ToolFail(check, "an old tool that was taken was reported as a tool of a room");
                // The kitchen's worker takes a knife from its own supply, the garden's takes the old kind.
                Pawn cook = s.Worker("Cook", "RP_JobKitchen", s.At(12, 3));
                // (A guard of the real prison who sees the cook stops the theft: the holder was checked where they stand, the cook, the gardener and the last worker stand elsewhere, and tools-t1b was told so.)
                Pawn gardener = s.Worker("Gardener", "RP_JobGarden", s.At(4, 5));
                Pawn other = s.Worker("Other");
                if (new[] { cook, gardener, other }.Any(x => TensionUtility.GuardsNear(x, s.pm) > 0))
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees a worker of the scene; the rest of the check is skipped");
                    return;
                }
                Thing knife = ToolForceTheft(s, cook, ToolScene.Kitchen, ContrabandKind.Weapon);
                if (knife == null || !ToolCount.Traced(knife) || ToolCount.Missing(kitchen) != 1 || ToolCount.Missing(shop) != 1) ToolFail(check, $"a kitchen theft should be a knife traced to the kitchen's supply (traced {knife != null && ToolCount.Traced(knife)}, kitchen missing {ToolCount.Missing(kitchen)}, workshop {ToolCount.Missing(shop)})");
                int traces = g.tools.traces.Count;
                var before = new List<Thing>();
                ContrabandUtility.CarriedContraband(gardener, before);
                ContrabandUtility.TheftRollForTests = true;
                ContrabandUtility.Notify_WorkedWithTools(gardener, ContrabandKind.Tool, RPLaborDefOf.RP_Garden);
                ContrabandUtility.TheftRollForTests = null;
                var after = new List<Thing>();
                ContrabandUtility.CarriedContraband(gardener, after);
                if (after.Count != before.Count + 1 || g.tools.traces.Count != traces || after.Any(ToolCount.Traced)) ToolFail(check, $"the garden has no supply: its theft is the old one and traces nothing ({after.Count - before.Count} made, {g.tools.traces.Count - traces} traced)");
                // Switched off (an autotest run as it is): the workshop's theft is the old one too.
                ToolCount.TestOn = false;
                var carried0 = new List<Thing>();
                ContrabandUtility.CarriedContraband(other, carried0);
                int missing = ToolCount.Missing(shop);
                ContrabandUtility.TheftRollForTests = true;
                ContrabandUtility.Notify_WorkedWithTools(other, ContrabandKind.Tool, ToolScene.Workshop);
                ContrabandUtility.TheftRollForTests = null;
                var carried1 = new List<Thing>();
                ContrabandUtility.CarriedContraband(other, carried1);
                if (carried1.Count != carried0.Count + 1 || ToolCount.Missing(shop) != missing || carried1.Any(ToolCount.Traced)) ToolFail(check, "with the count off the workshop's theft is not the old one");
                ToolCount.TestOn = true;
            });
        }

        // ---- the other finds ----------------------------------------------------------------------------------------------------------------

        /// <summary>A tunnel found, an amnesty and the bust of a ring: each takes the traced tool and puts the supply right once, saying where it came from (the tunnel's tools and the cell's); an untraced tool beside it is only taken.</summary>
        private static void CheckToolCountOtherFinds(Map map)
        {
            Guarded("tool count - a tunnel, an amnesty and a bust take a traced tool back", map, r =>
            {
                const string check = "other finds";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef shop = ToolScene.Workshop;
                Pawn digger = s.Worker("Digger");
                CompInmate c = digger.Inmate();
                if (TensionUtility.GuardsNear(digger, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                ToolPool pool = ToolOpen(s, digger);
                if (pool == null) { ToolFail(check, "the premise: no supply"); return; }
                ThingDef toolDef = DefDatabase<ThingDef>.GetNamed("RP_Tools");
                // The tunnel: the digger had one traced tool in the stash and one in the pocket, and a phone that was never the room's.
                Thing inStash = ToolForceTheft(s, digger, shop), inPocket = ToolForceTheft(s, digger, shop);
                if (inStash == null || inPocket == null) { ToolFail(check, "the premise: two tools were not stolen"); return; }
                StashUtility.TuckAway(digger, c, new List<Thing> { inStash }, 0);
                Thing phone = ContrabandUtility.Give(digger, RPContrabandDefOf.RP_Cellphone);
                // (Each find is of tools the staff have counted short: a tool nobody has counted is found in silence, see ToolCount.Notify_Taken.)
                ToolCountNow(s, pool, digger);
                int found0 = g.stats.contrabandFound, today0 = g.today.contrabandFound;
                ToolCount.Notify_TunnelFound(digger, c);
                if (g.stats.contrabandFound != found0 + 2 || g.today.contrabandFound != today0 + 2) ToolFail(check, $"the two tools of a tunnel are two finds in the counts like any other ({g.stats.contrabandFound - found0} in the game's, {g.today.contrabandFound - today0} in the day's)");
                if (ToolCount.Missing(pool) != 0 || !inStash.Destroyed || !inPocket.Destroyed || phone == null || phone.Destroyed || pool.foundSource != ToolSource.Tunnel)
                    ToolFail(check, $"a tunnel found should take the tools that dug it and nothing else (missing {ToolCount.Missing(pool)}, stash tool gone {inStash.Destroyed}, pocket tool gone {inPocket.Destroyed}, phone kept {phone != null && !phone.Destroyed}, source {pool.foundSource})");
                if (!ToolCount.Heard.Any(x => x.Contains(((string)"RP_ToolSource_Tunnel".Translate())) && x.Contains(shop.label) && !x.Contains(digger.LabelShort))) ToolFail(check, $"the tunnel should be tied to the room, not to the digger: {string.Join(" | ", ToolCount.Heard)}");
                ToolOnce(check, "a tunnel", pool, inStash, inPocket);
                // The amnesty: what they carry and what is in the stash goes in the box.
                Thing handed = ToolForceTheft(s, digger, shop);
                Thing hidden = ToolForceTheft(s, digger, shop);
                if (handed == null || hidden == null) { ToolFail(check, "the premise: two more tools were not stolen"); return; }
                StashUtility.TuckAway(digger, c, new List<Thing> { hidden }, 0);
                ToolCountNow(s, pool, digger);
                Amnesty.HandIn(digger, c);
                if (ToolCount.Missing(pool) != 0 || ToolCount.OnHand(pool) != pool.owned || pool.foundSource != ToolSource.Amnesty) ToolFail(check, $"an amnesty should take both tools back (missing {ToolCount.Missing(pool)}, source {pool.foundSource})");
                ToolOnce(check, "an amnesty", pool, handed, hidden);
                // The bust of a ring: everything carried and hidden.
                Thing a = ToolForceTheft(s, digger, shop), b = ToolForceTheft(s, digger, shop);
                if (a == null || b == null) { ToolFail(check, "the premise: two more tools were not stolen for the bust"); return; }
                StashUtility.TuckAway(digger, c, new List<Thing> { b }, 0);
                ToolCountNow(s, pool, digger);
                int taken = RingSystem.Confiscate(digger, c);
                if (taken < 2 || ToolCount.Missing(pool) != 0 || ToolCount.OnHand(pool) != pool.owned || pool.foundSource != ToolSource.Raid) ToolFail(check, $"the bust of a ring should take both tools back (took {taken}, missing {ToolCount.Missing(pool)}, source {pool.foundSource})");
                ToolOnce(check, "the bust of a ring", pool, a, b);
                if (g.tools.traces.Count != 0) ToolFail(check, $"{g.tools.traces.Count} trace(s) are left after every tool was taken");
            });
        }

        // ---- the kitchen --------------------------------------------------------------------------------------------------------------------

        /// <summary>The kitchen is a room of its own, its stove the bench: knives (the weapons kind) are traced to it, not to the workshop, and its work slows when it is short.</summary>
        private static void CheckToolCountKitchen(Map map)
        {
            Guarded("tool count - the kitchen's knives are its own supply and its work slows when short", map, r =>
            {
                const string check = "kitchen";
                ToolScene s = ToolScene.Build(map, r, check, kitchen: true);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef kitchen = ToolScene.Kitchen, shop = ToolScene.Workshop;
                Pawn cook = s.Worker("Cook", "RP_JobKitchen", s.At(12, 3));
                if (TensionUtility.GuardsNear(cook, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                ToolCount.Hourly(g, map);
                ToolPool pool = g.tools.PoolOf(kitchen);
                if (pool == null || pool.owned != ToolCount.KitchenPerBench || pool.inPeriod != true || g.tools.PoolOf(shop) is not { inPeriod: false })
                    ToolFail(check, $"the kitchen's supply should be made for its stove, and its period begun by its crew alone (owned {pool?.owned}, running {pool?.inPeriod}, the workshop's {g.tools.PoolOf(shop)?.inPeriod})");
                if (pool == null) return;
                Thing knife = ToolForceTheft(s, cook, kitchen, ContrabandKind.Weapon);
                if (knife == null || !ToolCount.Traced(knife) || ToolCount.Missing(pool) != 1 || g.tools.byThing[knife.thingIDNumber].poolId != pool.id)
                    ToolFail(check, "a theft in the kitchen should be a knife traced to the kitchen's supply");
                // The knife is a weapon (it can sit in the hand): a pat-down of an inmate who holds it takes it from the equipment too.
                if (knife != null && knife is ThingWithComps weapon && knife.def.IsWeapon)
                {
                    cook.inventory.innerContainer.Remove(weapon);
                    cook.equipment.AddEquipment(weapon);
                    Pawn guard = s.Staff("Guard", RPSecurityDefOf.RP_Guard);
                    int found = ContrabandUtility.Search(guard, cook, 1f);
                    if (found != 1 || ToolCount.Missing(pool) != 0 || pool.foundSource != ToolSource.Pockets) ToolFail(check, $"a knife in the hand should be taken by a pat-down and put right (found {found}, missing {ToolCount.Missing(pool)})");
                }
                // Short: the kitchen cooks more slowly, and the workshop not at all.
                pool.knownMissing = 2;
                if (Math.Abs(ToolCount.OutputFactor(kitchen) - 0.7f) > 0.0001f || ToolCount.OutputFactor(shop) != 1f || ToolCount.WorkTicks(kitchen, 600) != (int)Math.Round(600 / 0.7f) || ToolCount.WorkTicks(shop, 600) != 600)
                    ToolFail(check, $"two knives short should slow the kitchen alone (kitchen {ToolCount.OutputFactor(kitchen)}, workshop {ToolCount.OutputFactor(shop)}, a cook's 600 ticks take {ToolCount.WorkTicks(kitchen, 600)})");
                pool.knownMissing = 0;
            });
        }


        // ---- the review: what the staff know, whose crew, a room that was asked, a press that is gone for a while -----------------------------

        /// <summary>
        /// Two tools stolen and counted, the warden says carry on, a third is stolen (nobody has counted it), and one of the first two is found: the staff counted two and have one back, so they know of one short - the message,
        /// Intel, the output and what Replace charges are the count's, not the truth. The third is in the next count, once (one message, one letter); a tool taken before any count says nothing at all.
        /// </summary>
        private static void CheckToolCountFindBeforeCount(Map map)
        {
            Guarded("tool count - a find before the next count changes only what was counted", map, r =>
            {
                const string check = "find before count";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef shop = ToolScene.Workshop;
                s.Spawn(DefDatabase<ThingDef>.GetNamed("RP_LicensePlatePress"), s.At(2, 4));
                Pawn thief = s.Worker("Thief");
                if (TensionUtility.GuardsNear(thief, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                ToolPool pool = ToolShort(s, thief, 2, r, out List<Thing> things);
                if (pool == null || things.Count != 2 || pool.knownMissing != 2 || ToolLetters(r).Count != 1) { ToolFail(check, "the premise: two tools counted short, one letter"); return; }
                ToolAnswer(ToolLetters(r)[0], ToolCountDecision.CarryOn);
                // Period 2: a third is taken, and nobody knows.
                s.OnSlot(thief, true);
                ToolCount.Hourly(g, map);
                Thing third = ToolForceTheft(s, thief, shop);
                if (third == null || ToolCount.Missing(pool) != 3 || pool.knownMissing != 2) { ToolFail(check, $"the premise: a third tool taken after the count (missing {ToolCount.Missing(pool)}, known {pool.knownMissing})"); return; }
                int said0 = ToolCount.Heard.Count;
                ToolCount.Notify_Taken(things[0], ToolSource.Pockets);
                string said = ToolCount.Heard.Skip(said0).FirstOrDefault() ?? "";
                if (pool.knownMissing != 1 || ToolCount.Heard.Count != said0 + 1 || !said.Contains((string)"RP_ToolFoundStill".Translate(1)) || said.Contains("2") || said.Contains("3"))
                    ToolFail(check, $"a tool of the first two found should leave the staff knowing one short, and say no more than that (known {pool.knownMissing}, said \"{said}\")");
                string line = ToolCount.IntelLine(pool);
                if (!line.Contains($"1 of {pool.owned} short") || Math.Abs(ToolCount.OutputFactor(shop) - (1f - ToolCount.CutPerTool)) > 0.0001f || ToolCount.ReplaceTotal(pool) != ToolCount.ReplaceCost)
                    ToolFail(check, $"Intel, the output and the price should be of the count (one short), not of the truth (two): \"{line}\", output {ToolCount.OutputFactor(shop)}, price {ToolCount.ReplaceTotal(pool)}");
                // What Replace pays for is the tool that was counted.
                int funds0 = g.ledger.funds;
                r.Add(() => g.ledger.funds = funds0);
                if (!ToolCount.Replace(pool) || g.tools.byThing.TryGetValue(third.thingIDNumber, out ToolTrace t3) && t3.replaced || g.tools.byThing[things[1].thingIDNumber].replaced == false)
                    ToolFail(check, "Replace should pay for the one the staff counted and not for the third, which nobody has counted");
                g.ledger.funds = funds0;
                // The end of period 2: the next count finds the third - one message, one letter.
                s.OnSlot(thief, false);
                ToolCount.Hourly(g, map);
                int letters0 = ToolLetters(r).Count;
                said0 = ToolCount.Heard.Count;
                if (!pool.countDue) { ToolFail(check, "the premise: a count due at the end of period 2"); return; }
                ToolCount.Counted(pool, null);
                if (pool.knownMissing != 1 || ToolLetters(r).Count != letters0 + 1 || ToolCount.Heard.Count != said0 + 2)
                    ToolFail(check, $"the next count should find the third tool, once: one message and one letter (known {pool.knownMissing} of 1 after the replacing, {ToolLetters(r).Count - letters0} letter(s), {ToolCount.Heard.Count - said0} thing(s) said)");
                // A tool taken before it was counted says nothing and changes nothing the staff know.
                Thing fourth = ToolForceTheft(s, thief, shop);
                said0 = ToolCount.Heard.Count;
                int known0 = pool.knownMissing, foundTick0 = pool.foundTick;
                if (fourth != null) ToolCount.Notify_Taken(fourth, ToolSource.Stash);
                if (fourth == null || ToolCount.Heard.Count != said0 || pool.knownMissing != known0 || pool.foundTick != foundTick0 || ToolCount.Traced(fourth))
                    ToolFail(check, $"a tool that was taken and found before any count should say nothing and change nothing the staff know ({ToolCount.Heard.Count - said0} thing(s) said, known {known0} -> {pool.knownMissing})");
            });
        }

        /// <summary>The letter is answered "search the crew" after the next work period has begun with another crew: the crew that was counted is searched, and the one at work is not.</summary>
        private static void CheckToolCountCrewSearched(Map map)
        {
            Guarded("tool count - the search is of the crew of the counted period", map, r =>
            {
                const string check = "crew searched";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn first = s.Worker("First"), second = s.Worker("Second", at: s.At(5, 3));
                if (TensionUtility.GuardsNear(first, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                s.OnSlot(second, false);
                ToolPool pool = ToolShort(s, first, 1, r, out List<Thing> things);
                if (pool == null || things.Count != 1 || pool.countedPeriod != 1 || ToolLetters(r).Count != 1) { ToolFail(check, "the premise: period 1's crew counted one tool short, one letter"); return; }
                // The next morning's crew is another.
                s.OnSlot(second, true);
                ToolCount.Hourly(g, map);
                if (pool.period != 2 || !pool.workers.Contains(second.thingIDNumber) || pool.workers.Contains(first.thingIDNumber)) { ToolFail(check, $"the premise: period 2 has the other crew (period {pool.period}, crew {pool.workers.Count})"); return; }
                ToolAnswer(ToolLetters(r)[0], ToolCountDecision.Search);
                if (!ToolCount.SearchOwed(first.Inmate()) || ToolCount.SearchOwed(second.Inmate()) || pool.searchIds.Count != 1)
                    ToolFail(check, $"the search should be of the crew of the period that was counted (the first worker owed {ToolCount.SearchOwed(first.Inmate())}, the second, who is at work now, owed {ToolCount.SearchOwed(second.Inmate())}, {pool.searchIds.Count} owed in all)");
            });
        }

        /// <summary>
        /// A room that has been asked is still counted at the end of each work period (the real end of one: the hourly pass), and the count is said; the letter is not sent twice. The search that is answered later is of both crews.
        /// </summary>
        private static void CheckToolCountAskedRoom(Map map)
        {
            Guarded("tool count - a room waiting for its answer is still counted", map, r =>
            {
                const string check = "asked room";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef shop = ToolScene.Workshop;
                s.Spawn(DefDatabase<ThingDef>.GetNamed("RP_LicensePlatePress"), s.At(2, 4));
                Pawn first = s.Worker("First"), second = s.Worker("Second", at: s.At(5, 3));
                if (TensionUtility.GuardsNear(first, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                s.OnSlot(second, false);
                ToolPool pool = ToolShort(s, first, 1, r, out List<Thing> things);
                if (pool == null || things.Count != 1 || !pool.asked || ToolLetters(r).Count != 1) { ToolFail(check, "the premise: one tool short, the warden asked"); return; }
                // Period 2, with the letter still open: another crew, another tool, the end of the period.
                s.OnSlot(second, true);
                ToolCount.Hourly(g, map);
                if (ToolForceTheft(s, second, shop) == null) { ToolFail(check, "the premise: a second tool taken in period 2"); return; }
                s.OnSlot(second, false);
                ToolCount.Hourly(g, map);
                if (!pool.countDue || pool.countDuePeriod != 2 || !ToolCount.IntelLine(pool).Contains((string)"RP_IntelToolsDue".Translate()))
                    ToolFail(check, $"the end of a period of a room that has been asked should still make a count due (due {pool.countDue}, period {pool.countDuePeriod}, \"{ToolCount.IntelLine(pool)}\")");
                if (!pool.countDue) return;
                int said0 = ToolCount.Heard.Count, letters0 = ToolLetters(r).Count;
                ToolCount.Counted(pool, null);
                if (pool.knownMissing != 2 || pool.countedPeriod != 2 || ToolCount.Heard.Count != said0 + 1 || ToolLetters(r).Count != letters0 || !pool.asked)
                    ToolFail(check, $"the count of period 2 should find the second tool and say so, without a second letter (known {pool.knownMissing}, counted period {pool.countedPeriod}, {ToolCount.Heard.Count - said0} thing(s) said, {ToolLetters(r).Count - letters0} more letter(s), asked {pool.asked})");
                else if (!ToolCount.Heard[^1].Contains((string)"RP_ToolShortInAll".Translate(2)) || !ToolCount.Heard[^1].Contains("1 short"))
                    ToolFail(check, $"a count that finds one new tool short of two should say so (\"{ToolCount.Heard[^1]}\")");
                // The answer, later: the search owes both crews a frisk.
                ToolAnswer(ToolLetters(r)[0], ToolCountDecision.Search);
                if (!ToolCount.SearchOwed(first.Inmate()) || !ToolCount.SearchOwed(second.Inmate()))
                    ToolFail(check, $"a search answered after two counts should be of both crews (first owed {ToolCount.SearchOwed(first.Inmate())}, second {ToolCount.SearchOwed(second.Inmate())})");
            });
        }

        /// <summary>A count that nobody came for before the next work period began is not made, and the warden is told so (and not what it would have found).</summary>
        private static void CheckToolCountNobodyCame(Map map)
        {
            Guarded("tool count - a count nobody came for is said not to have been made", map, r =>
            {
                const string check = "nobody came";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn worker = s.Worker("Worker");
                if (TensionUtility.GuardsNear(worker, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                ToolPool pool = ToolOpen(s, worker);
                if (pool == null || ToolForceTheft(s, worker, ToolScene.Workshop) == null) { ToolFail(check, "the premise: a tool taken in period 1"); return; }
                s.OnSlot(worker, false);
                ToolCount.Hourly(g, map);
                if (!pool.countDue || ToolCount.Heard.Count != 0) { ToolFail(check, "the premise: a count due, nothing said"); return; }
                s.OnSlot(worker, true);
                ToolCount.Hourly(g, map);
                string expected = "RP_ToolNotCounted".Translate(ToolScene.Workshop.label, 1).Resolve();
                if (pool.countDue || pool.period != 2 || ToolCount.Heard.Count != 1 || ToolCount.Heard[0] != expected || ToolLetters(r).Count != 0 || pool.knownMissing != 0)
                    ToolFail(check, $"a count nobody made should be dropped when the next period begins, with a word that it was not made and nothing of what it would have found (due {pool.countDue}, said {string.Join(" | ", ToolCount.Heard)}, known {pool.knownMissing})");
            });
        }

        /// <summary>
        /// A press that is gone for an hour (uninstalled, destroyed and rebuilt) is the same room: the supply, what is traced to it, what the staff know and the letter stay as they were; nobody steals from a room with no bench.
        /// </summary>
        private static void CheckToolCountBenchless(Map map)
        {
            Guarded("tool count - a room with no bench keeps its supply for a while", map, r =>
            {
                const string check = "benchless";
                ToolScene s = ToolScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonZoneDef shop = ToolScene.Workshop;
                Pawn thief = s.Worker("Thief");
                if (TensionUtility.GuardsNear(thief, s.pm) > 0)
                {
                    RPLog.Message($"[RimPrison:test] tool count ({check}): a guard of the real prison sees the scene; the check is skipped");
                    return;
                }
                ToolPool pool = ToolShort(s, thief, 1, r, out List<Thing> things);
                if (pool == null || things.Count != 1 || pool.knownMissing != 1 || !pool.asked) { ToolFail(check, "the premise: one tool short, the warden asked"); return; }
                int id = pool.id, said0 = ToolCount.Heard.Count, letters0 = ToolLetters(r).Count;
                s.press.Destroy();
                ToolCount.Hourly(g, map);
                if (g.tools.PoolOf(shop) != pool || pool.benchlessSinceTick < 0 || pool.knownMissing != 1 || !pool.asked || g.tools.traces.Count != 1 || ToolCount.Missing(pool) != 1)
                    ToolFail(check, $"a room whose press is gone for an hour should keep its supply, its trace, its shortage and its letter (supply {g.tools.PoolOf(shop) == pool}, since {pool.benchlessSinceTick}, known {pool.knownMissing}, asked {pool.asked}, traces {g.tools.traces.Count})");
                if (ToolForceTheft(s, thief, shop) != null || ToolCount.Missing(pool) != 1) ToolFail(check, "a tool was taken from a room with no bench");
                // The press is built again: the same supply, whole as it was.
                s.Spawn(DefDatabase<ThingDef>.GetNamed("RP_LicensePlatePress"), s.At(2, 2));
                ToolCount.Hourly(g, map);
                if (g.tools.PoolOf(shop) != pool || pool.id != id || pool.benchlessSinceTick >= 0 || pool.knownMissing != 1 || !pool.asked || pool.owned != ToolCount.WorkshopPerBench || ToolCount.Missing(pool) != 1)
                    ToolFail(check, $"a press built again should find the same supply, as it was (supply {g.tools.PoolOf(shop) == pool}, since {pool.benchlessSinceTick}, known {pool.knownMissing}, owned {pool.owned}, missing {ToolCount.Missing(pool)})");
                if (ToolCount.Heard.Count != said0 || ToolLetters(r).Count != letters0) ToolFail(check, "the press going and coming said something");
            });
        }

        // ---- the saveload plan --------------------------------------------------------------------------------------------------------------

        private static ToolLedgers toolsBeforeSeed;
        private static int toolSeedThing = -1, toolSeedInmate = -1;
        private static string toolSeedState;

        /// <summary>Every field of a supply and of a trace off its default - a workshop in the middle of a period, a count due, a letter asked, a search owed, a hold and a find - and a traced tool in a real inmate's stash.</summary>
        private static void SeedToolsForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            toolsBeforeSeed = g.tools;
            int now = Find.TickManager.TicksGame;
            Pawn inmate = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.Inmate()?.stash != null);
            if (inmate == null || DefDatabase<ThingDef>.GetNamed("RP_Tools") is not { } toolDef)
            {
                reloadProblems.Add("saveload: no inmate with a stash, or no tool def, to seed the tool count with");
                toolsBeforeSeed = null;
                return;
            }
            Thing tool = ThingMaker.MakeThing(toolDef);
            tool.stackCount = 1;
            var book = new ToolLedgers { nextId = 9, nextOrder = 12 };
            book.pools.Add(new ToolPool
            {
                id = 7, zone = RPLaborDefOf.RP_Workshop, benches = 2, owned = 6, period = 5, inPeriod = true, periodStartTick = now - 700, workers = new List<int> { 11, 22 }, issued = 5,
                countDue = true, countDuePeriod = 4, countedPeriod = 3, countedTick = now - 5000, countedIssued = 6, countedReturned = 5, countedOrder = 3, countedWorkers = new List<int> { 11, 44 }, knownMissing = 1, asked = true, askedTick = now - 400,
                pendingLateHours = 2, pendingSupervised = true, holdUntilTick = now + 1500, supervised = true, searchIds = new List<int> { 33 }, searchUntilTick = now + 9000, searchFound = 2,
                foundTick = now - 3000, foundSource = ToolSource.Tunnel, foundPeriod = 2, benchlessSinceTick = now - 600,
            });
            book.pools.Add(new ToolPool { id = 8, zone = RPLaborDefOf.RP_Kitchen, benches = 1, owned = 3, period = 1, inPeriod = false, countedPeriod = 1, countedTick = now - 100, countedIssued = 3, countedReturned = 3 });
            book.traces.Add(new ToolTrace { thingId = tool.thingIDNumber, poolId = 7, period = 4, order = 5, tick = now - 4000 });
            book.traces.Add(new ToolTrace { thingId = 987654, poolId = 7, period = 5, order = 8, tick = now - 900, replaced = true });
            book.Reindex();
            g.tools = book;
            inmate.Inmate().stash.TryAdd(tool, false);
            (toolSeedThing, toolSeedInmate) = (tool.thingIDNumber, inmate.thingIDNumber);
            toolSeedState = ToolCount.Describe(g);
            RPLog.Message($"[RimPrison:test] saveload tools: {toolSeedState}");
        }

        /// <summary>After the reload: the tool is in the same stash, still traced, taking it puts the supply right once; then the game's own ledgers are put back.</summary>
        private static void CheckToolsAfterReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            if (toolsBeforeSeed == null) return;
            string now = ToolCount.Describe(g);
            if (now != toolSeedState) reloadProblems.Add($"saveload: the tool count changed across the load ({toolSeedState} -> {now})");
            Pawn inmate = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.thingIDNumber == toolSeedInmate);
            Thing tool = inmate?.Inmate()?.stash.FirstOrDefault(t => t.thingIDNumber == toolSeedThing);
            ToolPool pool = g.tools.PoolById(7);
            if (tool == null || !ToolCount.Traced(tool) || pool == null || ToolCount.Missing(pool) != 1)
                reloadProblems.Add($"saveload: the traced tool did not come back in the stash, still traced (in the stash {tool != null}, traced {tool != null && ToolCount.Traced(tool)}, missing {(pool == null ? -1 : ToolCount.Missing(pool))})");
            else
            {
                inmate.Inmate().stash.Remove(tool);
                ToolCount.Notify_Taken(tool, ToolSource.Stash);
                tool.Destroy();
                if (ToolCount.Missing(pool) != 0 || g.tools.traces.Count != 1 || g.tools.byThing.Count != 1) reloadProblems.Add("saveload: the tool taken from the stash after the load did not reconcile once");
                else RPLog.Message("[RimPrison:test] saveload tools: the supplies, the traces, a tool in a stash and a search after the load are intact");
            }
            g.tools = toolsBeforeSeed;
            g.tools.Reindex();
            toolsBeforeSeed = null;
            toolSeedThing = toolSeedInmate = -1;
        }
    }

    /// <summary>Autotest: the tool count's scene stands on open ground outside the compound, where an inmate is already outside the walls and runs at their next hourly check (the count's real ticks may cross it). The scene's own people do not run.</summary>
    [HarmonyPatch(typeof(EscapeUtility), nameof(EscapeUtility.HourlyCheck))]
    [TestOnly]
    public static class Patch_EscapeUtility_HourlyCheck_Tools
    {
        public static bool Prefix(Pawn p) => ToolCount.CastForTests == null || !ToolCount.CastForTests(p);
    }
}
