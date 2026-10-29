using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the performance and structure items of gap analysis 4 (4b 1-6, 4c 1, 6): the candidate lists of vanilla's warden and doctor givers are exact, a count of free beds
    /// does not change the game, the staff list is read once a tick, readers of the fallback site never sweep, the interrupt queue compares jobs by number, a bed built is listed at the
    /// next tick, the test-only patches are not in a player's game, and the shuttle's weather comes from the weather defs.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckGa4Perf(Map map)
        {
            CheckGa4VanillaGiverListsAreExact(map);
            CheckGa4FreeBedCountChangesNothing(map);
            CheckGa4StaffListIsReadOncePerTick(map);
            CheckGa4ReadersOfTheFallbackSiteNeverSweep(map);
            CheckGa4InterruptQueueComparesJobNumbers(map);
            CheckGa4BedBuiltIsListedAtTheNextTick(map);
            CheckGa4TestOnlyPatchesAndWeatherDefs(map);
        }

        private static Job WaitJob()
        {
            Job wait = JobMaker.MakeJob(JobDefOf.Wait);
            wait.expiryInterval = 600;
            return wait;
        }

        private static WorkGiver_Scanner VanillaGiver(Type giverClass) => DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(d => d.giverClass == giverClass)?.Worker as WorkGiver_Scanner;

        // ---- 4b 1: the vanilla staff work scan ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Vanilla's warden and doctor givers scan the candidate lists of <see cref="InmateCandidates"/> (see <see cref="VanillaGiverLists"/>): an inmate a giver has a job on for a guard
        /// is always in its list (so the lists are exact: nothing is dropped that the giver would have acted on), the inmates in a state each giver wants are in it, the doctors' lists
        /// keep every colonist and animal the vanilla list had, and the lists are shorter than the prisoners.
        /// </summary>
        private static void CheckGa4VanillaGiverListsAreExact(Map map)
        {
            Guarded("gap analysis 4 4b1: the vanilla givers' candidate lists are exact", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                InmateCandidates lists = InmateCandidates.Shared;
                Pawn guard = pm.Guards.FirstOrDefault(x => x.Spawned && !x.Downed);
                if (guard == null)
                {
                    Ga4Fail("4b1: no guard on the map to ask the vanilla givers as");
                    return;
                }
                // Inmates in the states the givers care about.
                Pawn releasing = Throwaway(map, r), chatting = Throwaway(map, r), bedless = Throwaway(map, r), hurt = Throwaway(map, r);
                PrisonerInteractionModeDef mode0 = releasing.guest.ExclusiveInteractionMode;
                r.Add(() =>
                {
                    releasing.guest.SetExclusiveInteraction(mode0);
                    chatting.guest.SetExclusiveInteraction(mode0);
                    lists.Invalidate();
                });
                releasing.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.Release);
                chatting.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.AttemptRecruit);
                bedless.ownership?.UnclaimAll();
                HealthUtility.DamageUntilDowned(hurt, allowBleedingWounds: false);
                lists.Invalidate();
                var warden = new (Type giver, Func<List<Thing>> list, string name)[]
                {
                    (typeof(WorkGiver_Warden_Chat), () => lists.Chat(map), "chat"), (typeof(WorkGiver_Warden_Convert), () => lists.Convert(map), "convert"),
                    (typeof(WorkGiver_Warden_InterrogateIdentity), () => lists.Interrogate(map), "interrogate"), (typeof(WorkGiver_Warden_ReleasePrisoner), () => lists.Release(map), "release"),
                    (typeof(WorkGiver_Warden_DoExecution), () => lists.DoExecution(map), "execution"), (typeof(WorkGiver_Warden_Feed), () => lists.FeedPrisoner(map), "feed"),
                    (typeof(WorkGiver_Warden_TakeToBed), () => lists.TakeToBed(map), "take to bed"),
                };
                List<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned.ToList();
                int jobs = 0;
                foreach ((Type type, Func<List<Thing>> list, string name) in warden)
                {
                    WorkGiver_Scanner giver = VanillaGiver(type);
                    if (giver == null) continue;
                    List<Thing> candidates = list();
                    // The giver is handed exactly this list (the patch on its scan).
                    if (!ReferenceEquals(giver.PotentialWorkThingsGlobal(guard), candidates)) Ga4Fail($"4b1: the {name} giver scans {giver.PotentialWorkThingsGlobal(guard)?.GetType().Name}, not its candidate list");
                    foreach (Pawn inmate in prisoners)
                        if (giver.HasJobOnThing(guard, inmate))
                        {
                            jobs++;
                            if (!candidates.Contains(inmate)) Ga4Fail($"4b1: the {name} giver has a job on {inmate.LabelShort} for a guard, but its candidate list does not hold them");
                        }
                }
                if (!lists.Release(map).Contains(releasing) || !lists.Chat(map).Contains(chatting) || !lists.TakeToBed(map).Contains(bedless) || !lists.TakeToBed(map).Contains(hurt))
                    Ga4Fail($"4b1: an inmate in the state a giver wants is missing from its list (release {lists.Release(map).Contains(releasing)}, chat {lists.Chat(map).Contains(chatting)}, bedless {lists.TakeToBed(map).Contains(bedless)}, downed outside a bed {lists.TakeToBed(map).Contains(hurt)})");
                if (lists.Release(map).Count >= prisoners.Count || lists.Chat(map).Count >= prisoners.Count) Ga4Fail($"4b1: the release list holds {lists.Release(map).Count} and the chat list {lists.Chat(map).Count} of {prisoners.Count} prisoners (a list that is the whole prison saves nothing)");
                // The doctors: the vanilla list is cut down only of inmates, never of colonists, animals or anyone else.
                var doctors = new (Type giver, Func<IEnumerable<Thing>> vanilla, string name)[]
                {
                    (typeof(WorkGiver_TendOtherUrgent), () => map.mapPawns.SpawnedPawnsWithAnyHediff, "tend urgent"), (typeof(WorkGiver_TendOther_Humanlike), () => map.mapPawns.SpawnedHumanlikesWithAnyHediff, "tend humanlikes"),
                    (typeof(WorkGiver_FeedPatient), () => map.mapPawns.SpawnedHungryPawns, "feed patients"), (typeof(WorkGiver_RescueDowned), () => map.mapPawns.SpawnedDownedPawns, "rescue"),
                };
                lists.Invalidate();
                foreach ((Type type, Func<IEnumerable<Thing>> vanilla, string name) in doctors)
                {
                    // (Every def of the class: the feeding giver has one for the humanlikes and one for the animals.)
                    foreach (WorkGiver_Scanner giver in DefDatabase<WorkGiverDef>.AllDefsListForReading.Where(d => d.giverClass == type).Select(d => d.Worker).OfType<WorkGiver_Scanner>())
                    {
                        List<Thing> original = vanilla().ToList();
                        List<Thing> offered = giver.PotentialWorkThingsGlobal(guard).ToList();
                        List<Thing> others = original.Where(t => t is not Pawn { IsPrisonerOfColony: true }).ToList();
                        if (others.Any(t => !offered.Contains(t))) Ga4Fail($"4b1: the {name} giver lost a colonist or an animal from its list");
                        foreach (Pawn inmate in original.OfType<Pawn>().Where(p => p.IsPrisonerOfColony))
                            if (giver.HasJobOnThing(guard, inmate) && !offered.Contains(inmate)) Ga4Fail($"4b1: the {name} giver has a job on {inmate.LabelShort} for a guard, but its list does not hold them");
                        if (offered.Count > original.Count) Ga4Fail($"4b1: the {name} giver offers {offered.Count} where vanilla's list had {original.Count}");
                    }
                }
                RPLog.Message($"[RimPrison:test] gap analysis 4 4b1: candidate lists of {prisoners.Count} prisoners: chat {lists.Chat(map).Count}, release {lists.Release(map).Count}, feed {lists.FeedPrisoner(map).Count}, take to bed {lists.TakeToBed(map).Count}; {jobs} jobs found, all in their lists");
            });
        }

        // ---- 4b 4: FreeBedCount writes game state ---------------------------------------------------------------------------------------------------

        private static void CheckGa4FreeBedCountChangesNothing(Map map)
        {
            Guarded("gap analysis 4 4b4: counting the free beds changes nothing", map, r =>
            {
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                bed.SetFactionDirect(Faction.OfPlayer);
                GenSpawn.Spawn(bed, CellFinder.StandableCellNear(p.Position, map, 5f), map);
                r.Add(() =>
                {
                    c.homeBed = null;
                    if (!bed.Destroyed) bed.Destroy(DestroyMode.Vanish);
                });
                bed.ForPrisoners = true;
                p.ownership.ClaimBedIfNonMedical(bed);
                // A stale home bed: they own it, so nothing is held for them (the hourly update lets it go).
                c.homeBed = bed;
                int count = IntakeSystem.FreeBedCount(map);
                IntakeSystem.FreeBedCount(map, c.security, inWingOnly: true);
                if (c.homeBed != bed) Ga4Fail("4b4: counting the free beds (which a screen asks every frame) cleared an inmate's home bed");
                PunishmentUtility.SyncHomeBed(p, c);
                if (c.homeBed != null) Ga4Fail("4b4: the hourly sync did not let go of a home bed the inmate owns");
                if (IntakeSystem.FreeBedCount(map) != count) Ga4Fail("4b4: the count of free beds changed with the sync of a bed nobody was holding for them");
            });
        }

        // ---- 4b 3: the staff list ------------------------------------------------------------------------------------------------------------------------

        private static void CheckGa4StaffListIsReadOncePerTick(Map map)
        {
            Guarded("gap analysis 4 4b3: the staff list is read once a tick", map, r =>
            {
                List<Pawn> first = StaffCommute.AllStaff(map);
                if (!ReferenceEquals(first, StaffCommute.AllStaff(map))) Ga4Fail("4b3: two asks for the staff in one tick built the list twice");
                Pawn hired = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                List<Pawn> second = StaffCommute.AllStaff(map);
                if (!second.Contains(hired) || first.Contains(hired)) Ga4Fail($"4b3: somebody hired in this tick was missing from the staff (in the new list {second.Contains(hired)}), or the list given out before changed under its reader ({first.Contains(hired)})");
            });
        }

        // ---- 4b 2: the fallback-site sweep ---------------------------------------------------------------------------------------------------------------

        private static void CheckGa4ReadersOfTheFallbackSiteNeverSweep(Map map)
        {
            Guarded("gap analysis 4 4b2: readers never sweep the map for a site", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "fallback site readers")) return;
                List<Building> pads = ShuttleSystem.Pads(map).ToList();
                foreach (Building pad in pads) pad.DeSpawn();
                r.Add(() =>
                {
                    foreach (Building pad in pads)
                        if (!pad.Spawned) GenSpawn.Spawn(pad, pad.Position, map, pad.Rotation, WipeMode.VanishOrMoveAside);
                    g.shuttle.ForgetSite();
                });
                g.shuttle.ForgetSite();
                int sweeps0 = ShuttleSystem.SiteSweeps;
                for (int i = 0; i < 20; i++) g.shuttle.ReceptionAnchor(map);
                if (ShuttleSystem.SiteSweeps != sweeps0) Ga4Fail($"4b2: asking where the released wait swept the map for ground {ShuttleSystem.SiteSweeps - sweeps0} times (the think tree and a transfer's click ask)");
                // The hourly pass makes the sweep (once), and then the readers have the site.
                ShuttleSystem.Hourly(g, map, GenLocalDate.HourOfDay(map));
                int sweeps1 = ShuttleSystem.SiteSweeps;
                ShuttleSystem.Hourly(g, map, GenLocalDate.HourOfDay(map));
                IntVec3 anchor = g.shuttle.ReceptionAnchor(map);
                bool found = g.shuttle.KnownFallbackSite(map).IsValid;
                if (sweeps1 != sweeps0 + 1 || ShuttleSystem.SiteSweeps != sweeps1 || found && !anchor.IsValid)
                    Ga4Fail($"4b2: the hourly pass swept {sweeps1 - sweeps0} times for the first look (1 expected) and {ShuttleSystem.SiteSweeps - sweeps1} for the second (none), and reception is {(anchor.IsValid ? "known" : "not known")} after it (a site found {found})");
            });
        }

        // ---- 4b 5: the interrupt queue ---------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A queued inmate is sent to a new job only if the job they had is still the one they have, and jobs are pooled (a job ended and one made since are the same object): the queue
        /// compares the job's number. The first pass of a session sends every idle inmate to look for the regime's activity (the queue is not saved).
        /// </summary>
        private static void CheckGa4InterruptQueueComparesJobNumbers(Map map)
        {
            Guarded("gap analysis 4 4b5: the interrupt queue compares job numbers", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Pawn p = Throwaway(map, r);
                p.jobs.StartJob(WaitJob(), JobCondition.InterruptForced);
                Job first = p.CurJob;
                int firstId = first.loadID;
                pm.QueueRegimeInterrupt(p);
                // The job ends, and one made since is the same object under a new number.
                p.jobs.EndCurrentJob(JobCondition.Succeeded, startNewJob: false);
                Job second = WaitJob();
                p.jobs.StartJob(second, JobCondition.InterruptForced);
                bool reused = ReferenceEquals(first, second);
                int secondId = second.loadID;
                pm.DrainInterrupts();
                bool kept = p.CurJob == second && second.loadID == secondId;
                RPLog.Message($"[RimPrison:test] gap analysis 4 4b5: the job object was reused {reused} (numbers {firstId} and {secondId}); the new job was kept {kept}");
                if (!kept) Ga4Fail($"4b5: an inmate queued for an interrupt was interrupted from a job they started afterwards (the pooled object was reused {reused})");
                // The first pass of a session.
                System.Reflection.FieldInfo lastHour = AccessTools.Field(typeof(PrisonMap), "lastHour"), firstPass = AccessTools.Field(typeof(PrisonMap), "firstPassDue");
                int hour0 = (int)lastHour.GetValue(pm);
                r.Add(() =>
                {
                    lastHour.SetValue(pm, GenLocalDate.HourOfDay(map));
                    while (pm.PendingInterrupts > 0) pm.DrainInterrupts();
                });
                while (pm.PendingInterrupts > 0) pm.DrainInterrupts();
                p.jobs.EndCurrentJob(JobCondition.Succeeded, startNewJob: false);
                p.jobs.StartJob(WaitJob(), JobCondition.InterruptForced);
                lastHour.SetValue(pm, -1);
                firstPass.SetValue(pm, true);
                pm.MapComponentTick();
                if (pm.PendingInterrupts == 0) Ga4Fail("4b5: the first pass of a session did not send the idle inmates to look for what the regime has for them (a queue lost in a save is never made up)");
                if (hour0 < 0) Ga4Fail("4b5: the check found the prison pass never started");
            });
        }

        // ---- 4b 6: the cell-door bed cache lags ------------------------------------------------------------------------------------------------------------

        private static void CheckGa4BedBuiltIsListedAtTheNextTick(Map map)
        {
            Guarded("gap analysis 4 4b6: a bed built is listed at the next tick", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                if (Find.TickManager.TicksGame % 250 == 0) Find.TickManager.DoSingleTick();
                Pawn p = Throwaway(map, r);
                var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                bed.SetFactionDirect(Faction.OfPlayer);
                GenSpawn.Spawn(bed, CellFinder.StandableCellNear(p.Position, map, 5f), map);
                bed.ForPrisoners = true;
                r.Add(() =>
                {
                    if (!bed.Destroyed) bed.Destroy(DestroyMode.Vanish);
                });
                if (Find.TickManager.TicksGame % 250 == 0)
                {
                    RPLog.Message("[RimPrison:test] gap analysis 4 4b6: the tick is a 250-tick beat, not checked");
                    return;
                }
                pm.MapComponentTick();
                if (!pm.PrisonerBeds.Contains(bed)) Ga4Fail("4b6: a prisoner bed built in the prison was not in the list of beds at the next tick (it waits for the 250-tick beat, and the door verdicts read the old list)");
                bed.Destroy(DestroyMode.Vanish);
                pm.MapComponentTick();
                if (pm.PrisonerBeds.Contains(bed)) Ga4Fail("4b6: a bed taken away is still in the list of beds at the next tick");
            });
        }

        // ---- 4c 1 and 6 ---------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckGa4TestOnlyPatchesAndWeatherDefs(Map map)
        {
            Guarded("gap analysis 4 4c1 and 4c6: test patches are not in the game's own, the weather is on the defs", map, r =>
            {
                var owners = new Dictionary<Type, HashSet<string>>();
                foreach (Type t in typeof(TestOnlyAttribute).Assembly.GetTypes().Where(x => x.IsDefined(typeof(TestOnlyAttribute), false) && x.IsDefined(typeof(HarmonyPatch), false)))
                    owners[t] = new HashSet<string>();
                foreach (System.Reflection.MethodBase method in Harmony.GetAllPatchedMethods().ToList())
                {
                    Patches info = Harmony.GetPatchInfo(method);
                    foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers).Concat(info.Transpilers))
                        if (patch.PatchMethod.DeclaringType is { } declaring && owners.ContainsKey(declaring)) owners[declaring].Add(patch.owner);
                }
                foreach (KeyValuePair<Type, HashSet<string>> kv in owners)
                    if (kv.Value.Count == 0 || kv.Value.Contains("UrekD.RimPrison") || !kv.Value.Contains("UrekD.RimPrison.Test"))
                        Ga4Fail($"4c1: the test-only patch {kv.Key.Name} is applied by {(kv.Value.Count == 0 ? "nobody" : string.Join("/", kv.Value))} (only the test's own Harmony instance, \"UrekD.RimPrison.Test\", is expected)");
                if (owners.Count < 10) Ga4Fail($"4c1: only {owners.Count} test-only patch classes are marked (the 10 of the test harness are expected)");
                foreach (string stormy in new[] { "RainyThunderstorm", "DryThunderstorm", "SnowHard" })
                    if (DefDatabase<WeatherDef>.GetNamedSilentFail(stormy) is { } weather && !weather.HasModExtension<ShuttleHoldExtension>()) Ga4Fail($"4c6: the weather {stormy} does not hold the shuttle (the patch to the weather defs)");
                if (DefDatabase<WeatherDef>.GetNamed("Clear").HasModExtension<ShuttleHoldExtension>()) Ga4Fail("4c6: clear weather holds the shuttle");
            });
        }
    }
}
