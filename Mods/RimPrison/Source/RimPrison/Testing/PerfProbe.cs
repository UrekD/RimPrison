using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Autotest only: inclusive wall time and call counts of our biggest passes (every hourly, daily and interval tick method, every work
    /// giver and think node of ours) and of the vanilla calls they lean on, patched in at startup when a test is armed and logged at the
    /// end of a run (<c>[RimPrison:perf]</c>), so a performance change is measured, not guessed. Times are inclusive: a method's callees are
    /// inside it, so the lines do not sum.
    /// </summary>
    public static class PerfProbe
    {
        private sealed class Entry
        {
            public string name;
            public long calls, ticks;
            /// <summary>Every this-many-th call the caller (the first frames up the stack) is noted with the call's time: who the time goes to (0: not sampled).</summary>
            public int sampleEvery;
            public readonly Dictionary<string, (long calls, long ticks)> callers = new Dictionary<string, (long, long)>();
        }

        /// <summary>What a probed call carries from its start to its end: when it began, and who called it if it is one of the sampled ones.</summary>
        public struct Started
        {
            public long ticks;
            public string caller;
        }

        private static readonly Dictionary<MethodBase, Entry> entries = new Dictionary<MethodBase, Entry>();
        private static readonly Entry tickEntry = new Entry { name = "(whole game tick)" };

        /// <summary>The vanilla calls worth watching (the type, the method).</summary>
        private static readonly (Type, string)[] Vanilla =
        {
            (typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage)),
            (typeof(Reachability), nameof(Reachability.CanReach)),
            (typeof(Reachability), nameof(Reachability.ClearCache)),
            (typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)),
            (typeof(Pawn_JobTracker), "TryFindAndStartJob"),
            (typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTick)),
            (typeof(GenClosest), nameof(GenClosest.ClosestThingReachable)),
            (typeof(GenClosest), nameof(GenClosest.ClosestThing_Global_Reachable)),
            (typeof(GenClosest), nameof(GenClosest.ClosestThing_Global)),
            (typeof(Pawn), "Tick"),
            (typeof(Pawn), "TickInterval"),
            (typeof(Map), "MapPreTick"),
            (typeof(Map), "MapPostTick"),
            (typeof(TickList), "Tick"),
            (typeof(RimWorld.Planet.World), "WorldTick"),
            (typeof(RimWorld.Planet.World), "WorldPostTick"),
            (typeof(GameComponentUtility), "GameComponentTick"),
            (typeof(MapComponentUtility), "MapComponentTick"),
            (typeof(Storyteller), "StorytellerTick"),
            (typeof(Pawn_PathFollower), "PatherTick"),
            (typeof(Pawn_PathFollower), "PatherTickInterval"),
            (typeof(Pawn_NeedsTracker), "NeedsTrackerTick"),
            (typeof(Pawn_NeedsTracker), "NeedsTrackerTickInterval"),
            (typeof(Pawn_HealthTracker), "HealthTick"),
            (typeof(Pawn_HealthTracker), "HealthTickInterval"),
            (typeof(Pawn_MindState), "MindStateTick"),
            (typeof(Pawn_MindState), "MindStateTickInterval"),
            (typeof(Need_Mood), "NeedInterval"),
            (typeof(Need_Food), "NeedInterval"),
            (typeof(Need_Rest), "NeedInterval"),
            (typeof(Need_Joy), "NeedInterval"),
            (typeof(ThoughtHandler), "ThoughtInterval"),
            (typeof(MemoryThoughtHandler), "MemoryThoughtHandlerTick"),
            (typeof(SituationalThoughtHandler), "SituationalThoughtInterval"),
            (typeof(Map), "MapPreTick"),
            (typeof(ResourceCounter), "ResourceCounterTick"),
            (typeof(ListerHaulables), "ListerHaulablesTick"),
        };

        /// <summary>The inner passes and searches of ours worth a line of their own ("Type:Method", private ones too).</summary>
        private static readonly string[] Named =
        {
            "PrisonMap:UpdateDoors", "PrisonMap:RefreshBeds", "PrisonMap:RefreshGuards", "PrisonMap:RefreshHold", "PrisonMap:OnHourChanged", "PrisonMap:ComputeInmatePass", "PrisonMap:CellDoorOpen",
            "PrisonMap:CollectDownedReleased", "GangTurf:Sample", "ProgramSystem:CompleteFinished", "ProgramSystem:NudgeTeachers", "ProgramSystem:TryStart", "ProgramSystem:SessionOf",
            "InmateUtility:InterruptForRegimeChange", "CommissarySystem:TryShop", "RegimeWorker_Eat:FindCanteenMeal", "ContrabandUtility:FindLooseContraband", "KitchenShift:InmatesCooking",
            "InmateJobWorker_Kitchen:Capacity", "InmateJobWorker_Kitchen:Stoves", "GangUtility:TensionFromGangs", "GangUtility:TryRecruit", "TensionUtility:HourlyUpdate", "TensionUtility:Watchers",
            "TensionUtility:GuardsNear", "MisconductUtility:HourlyRoll", "MisconductWorker:NearbyInmate", "MisconductWorker_Riot:Participants", "FixtureFinder:FindFor", "RegimeWorker_Eat:MealsIn", "StashUtility:Rare", "IntakeSystem:FreePrisonerBeds",
            "Cellmates:PickBed", "Cellmates:DailyReview", "Cellmates:Tension", "Mentors:AnyPair", "UtilitySystem:DailyBill", "CompSecurityCamera:Covers", "WorkGiver_DeliverCellMeal:FoodInRoom",
            "PrisonRelations:FriendsNear", "GangTurf:FearTerm", "GangTurf:TensionTerm", "PrisonHUD:Build", "PrisonGrading:Compute", "InmatesTab:Filtered", "WorkGiver_RescueInmate:BedFor",
            "PrisonMap:Of", "InmateWalking:TryFindNearest", "ExerciseUtility:TryWorkout", "FaithUtility:TryPray", "LibrarySystem:TryRead", "HostageHold:LetStaffOut",
        };

        private static readonly (Type, string, int)[] Sampled =
        {
            (typeof(GenClosest), nameof(GenClosest.ClosestThing_Global), 20), (typeof(GenClosest), nameof(GenClosest.ClosestThingReachable), 100), (typeof(Reachability), nameof(Reachability.CanReach), 400),
            (typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage), 10),
        };

        private static readonly string[] IntervalNames = { "Hourly", "HourlyTick", "Daily", "DailyTick", "DailyPass", "Tick", "CompTickInterval", "CompTickRare", "CompTick", "Notify_HourChanged", "Evening", "Morning", "MapComponentTick", "GameComponentTick", "WorldComponentTick" };

        public static void Install(Harmony harmony)
        {
            var targets = new List<MethodBase>();
            foreach ((Type type, string name) in Vanilla)
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == name && !m.IsAbstract && m.GetMethodBody() != null) targets.Add(m);
            foreach (Type type in typeof(PerfProbe).Assembly.GetTypes())
            {
                if (type.IsGenericTypeDefinition || type.Namespace != "RimPrison" || type.Name.StartsWith("<")) continue;
                bool patch = type.Name.StartsWith("Patch_");
                // (Not the test's own code and patches: the namespace is the mod's, so the type says so itself.)
                if (type.IsDefined(typeof(TestOnlyAttribute), false) || type == typeof(AutoTest)) continue;
                bool scanner = typeof(WorkGiver).IsAssignableFrom(type), node = typeof(ThinkNode).IsAssignableFrom(type);
                bool need = typeof(Need).IsAssignableFrom(type), thought = typeof(ThoughtWorker).IsAssignableFrom(type);
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.GetMethodBody() == null || m.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() != null) continue;
                    bool pick = (patch ? m.Name is "Prefix" or "Postfix" or "Finalizer" : Array.IndexOf(IntervalNames, m.Name) >= 0)
                        || (need && m.Name is "NeedInterval") || (thought && m.Name is "CurrentStateInternal" or "CurrentSocialStateInternal")
                        || (scanner && m.Name is "HasJobOnThing" or "PotentialWorkThingsGlobal" or "JobOnThing" or "HasJobOnCell" or "PotentialWorkCellsGlobal" or "NonScanJob" or "ShouldSkip")
                        || (node && m.Name is "TryGiveJob" or "TryIssueJobPackage" or "Satisfied" or "GetPriority");
                    if (pick && !m.Name.StartsWith("<")) targets.Add(m);
                }
            }
            // Vanilla's work givers and job givers (they decide what the staff scan, and our givers sit among them).
            foreach (Type type in typeof(JobGiver_Work).Assembly.GetTypes())
            {
                if (type.IsGenericTypeDefinition || !(typeof(WorkGiver_Scanner).IsAssignableFrom(type) || typeof(ThinkNode_JobGiver).IsAssignableFrom(type))) continue;
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!m.IsAbstract && !m.IsGenericMethodDefinition && m.GetMethodBody() != null
                        && m.Name is "HasJobOnThing" or "PotentialWorkThingsGlobal" or "HasJobOnCell" or "PotentialWorkCellsGlobal" or "ShouldSkip" or "TryGiveJob")
                        targets.Add(m);
            }
            foreach (string spec in Named)
            {
                string[] parts = spec.Split(':');
                Type type = typeof(PerfProbe).Assembly.GetType("RimPrison." + parts[0]);
                if (type == null)
                {
                    RPLog.Message($"[RimPrison:perf] no type {parts[0]} to probe");
                    continue;
                }
                bool any = false;
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == parts[1] && !m.IsAbstract && !m.IsGenericMethodDefinition && m.GetMethodBody() != null)
                    {
                        targets.Add(m);
                        any = true;
                    }
                if (!any) RPLog.Message($"[RimPrison:perf] no method {spec} to probe");
            }
            foreach ((Type type, string name) in Extra)
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (m.Name == name && !m.IsAbstract && m.GetMethodBody() != null) targets.Add(m);
            var pre = new HarmonyMethod(typeof(PerfProbe), nameof(Pre));
            var fin = new HarmonyMethod(typeof(PerfProbe), nameof(Fin));
            int ok = 0;
            foreach (MethodBase m in targets.Distinct())
            {
                try
                {
                    entries[m] = new Entry { name = m.DeclaringType.Name + "." + m.Name };
                    harmony.Patch(m, prefix: pre, finalizer: fin);
                    ok++;
                }
                catch (Exception e)
                {
                    entries.Remove(m);
                    RPLog.Message($"[RimPrison:perf] could not probe {m.DeclaringType.Name}.{m.Name}: {e.GetBaseException().Message}");
                }
            }
            // Who calls the vanilla searches that cost the most (a sample of the calls).
            // (Only when asked for with RIMPRISON_PERFCALLERS=1: a stack trace per sampled call costs a good deal, and the totals are for comparing runs.)
            if (Environment.GetEnvironmentVariable("RIMPRISON_PERFCALLERS") != null)
                foreach ((Type type, string name, int every) in Sampled)
                    foreach (KeyValuePair<MethodBase, Entry> kv in entries)
                        if (kv.Key.DeclaringType == type && kv.Key.Name == name)
                        {
                            kv.Value.sampleEvery = every;
                            sampled.Add(kv.Key);
                        }
            RPLog.Message($"[RimPrison:perf] probing {ok} methods");
        }

        /// <summary>More methods to time, by type and name (the passes the work under test touches).</summary>
        private static readonly List<(Type, string)> Extra = new List<(Type, string)>();

        public static void Watch(Type type, string name) => Extra.Add((type, name));

        public static long Start() => Stopwatch.GetTimestamp();

        /// <summary>How long each tick took, for the percentiles at the end (a regime change's spike is in the slowest ticks, not in the mean).</summary>
        private static readonly List<float> tickMs = new List<float>(200000);

        /// <summary>The game tick of the last hour change (set by the prison's hour pass), to say where the slow ticks fall.</summary>
        public static int HourChangedAt = -1;

        private static readonly int[] slowSince = new int[7];
        private static readonly int[] SlowBounds = { 0, 1, 3, 10, 50, 250 };

        public static void AddTick(long start)
        {
            long t = Stopwatch.GetTimestamp() - start;
            tickEntry.calls++;
            tickEntry.ticks += t;
            float ms = (float)(t * 1000.0 / Stopwatch.Frequency);
            tickMs.Add(ms);
            if (ms > 20f)
            {
                int since = Find.TickManager.TicksGame - HourChangedAt, bucket = 0;
                while (bucket < SlowBounds.Length && since > SlowBounds[bucket]) bucket++;
                slowSince[bucket]++;
            }
        }

        public static void Pre(MethodBase __originalMethod, out Started __state)
        {
            __state.caller = null;
            if (sampled.Count > 0 && entries.TryGetValue(__originalMethod, out Entry e) && e.sampleEvery > 0 && e.calls % e.sampleEvery == 0) __state.caller = Caller(__originalMethod);
            __state.ticks = Stopwatch.GetTimestamp();
        }

        public static void Fin(MethodBase __originalMethod, Started __state)
        {
            long t = Stopwatch.GetTimestamp() - __state.ticks;
            if (!entries.TryGetValue(__originalMethod, out Entry e)) return;
            e.calls++;
            e.ticks += t;
            if (__state.caller != null)
            {
                e.callers.TryGetValue(__state.caller, out (long calls, long ticks) c);
                e.callers[__state.caller] = (c.calls + 1, c.ticks + t);
            }
        }

        private static readonly HashSet<MethodBase> sampled = new HashSet<MethodBase>();

        /// <summary>The three frames above the probed method that are not the probe's or the patch's own.</summary>
        private static string Caller(MethodBase probed)
        {
            var frames = new StackTrace(2, false).GetFrames();
            var names = new List<string>();
            foreach (StackFrame f in frames)
            {
                MethodBase m = f.GetMethod();
                if (m == null || m.DeclaringType == typeof(PerfProbe) || m.DeclaringType == null || m == probed || m.Name.StartsWith("DMD")) continue;
                names.Add(m.DeclaringType.Name + "." + m.Name);
                if (names.Count == 3) break;
            }
            return string.Join(" < ", names);
        }

        /// <summary>The probes by inclusive time, one log line each.</summary>
        public static void Report()
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            var sb = new StringBuilder();
            sb.Append($"[RimPrison:perf] {tickEntry.name}: ticks={tickEntry.calls} ms={tickEntry.ticks * toMs:F0}");
            Log.Message(sb.ToString());
            if (tickMs.Count > 0)
            {
                float[] sorted = tickMs.ToArray();
                Array.Sort(sorted);
                float At(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
                Log.Message($"[RimPrison:perf] tick time ms: mean {sorted.Average():F2} p50 {At(0.5):F2} p99 {At(0.99):F2} p99.9 {At(0.999):F2} max {sorted[sorted.Length - 1]:F1}; ticks over 5 ms {sorted.Count(x => x > 5f)}, over 10 ms {sorted.Count(x => x > 10f)}, over 20 ms {sorted.Count(x => x > 20f)}, over 40 ms {sorted.Count(x => x > 40f)}");
            }
            Log.Message("[RimPrison:perf] ticks over 20 ms by ticks since the hour changed (0 / 1 / 2-3 / 4-10 / 11-50 / 51-250 / later): " + string.Join(" / ", slowSince));
            var byMod = new Dictionary<string, int>();
            foreach (ThoughtDef d in ThoughtUtility.situationalNonSocialThoughtDefs)
            {
                string mod = d.modContentPack?.Name ?? "?";
                byMod[mod] = byMod.TryGetValue(mod, out int n) ? n + 1 : 1;
            }
            Log.Message("[RimPrison:perf] situational non-social thought defs by mod: " + string.Join(", ", byMod.Select(kv => kv.Key + "=" + kv.Value)));
            foreach (Entry e in entries.Values.Where(x => x.calls > 0).OrderByDescending(x => x.ticks).Take(150))
                Log.Message($"[RimPrison:perf] {e.name}: calls={e.calls} ms={e.ticks * toMs:F0} us/call={e.ticks * toMs * 1000 / e.calls:F1}");
            foreach (Entry e in entries.Values.Where(x => x.sampleEvery > 0 && x.callers.Count > 0))
                foreach (KeyValuePair<string, (long calls, long ticks)> kv in e.callers.OrderByDescending(x => x.Value.ticks).Take(12))
                    Log.Message($"[RimPrison:perf] callers of {e.name} (every {e.sampleEvery}th call, ms scaled): {kv.Key}: calls~{kv.Value.calls * e.sampleEvery} ms~{kv.Value.ticks * toMs * e.sampleEvery:F0}");
        }
    }
}
