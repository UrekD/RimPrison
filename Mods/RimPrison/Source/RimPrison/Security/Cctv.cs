using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPCctvDefOf
    {
        public static ThingDef RP_CctvMonitor;

        static RPCctvDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCctvDefOf));
    }

    public class CompProperties_CctvMonitor : CompProperties
    {
        /// <summary>How many cameras one manned monitor carries.</summary>
        public int feeds = 8;

        public CompProperties_CctvMonitor() => compClass = typeof(CompCctvMonitor);
    }

    /// <summary>A CCTV monitor's side of the control room: what it carries now, and the cache rebuilds when one is built or taken down (its post is <see cref="CompGuardPost"/>).</summary>
    public class CompCctvMonitor : ThingComp
    {
        public CompProperties_CctvMonitor Props => (CompProperties_CctvMonitor)props;

        /// <summary>Derived, never saved: the cameras it carries now (see <see cref="Cctv.Rebuild"/>).</summary>
        internal int carrying;

        /// <summary>Derived, never saved: who sits at it now (null: nobody).</summary>
        internal Pawn seated;

        /// <summary>Powered and unbroken: a monitor that is not shows nothing.</summary>
        public bool Working => parent.Spawned && !parent.IsBrokenDown() && (parent.TryGetComp<CompPowerTrader>() is not { } p || p.PowerOn);

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            // (A load is rebuilt once when the game is ready, see PrisonGame.FinalizeInit.)
            if (!respawningAfterLoad) Cctv.MonitorBuilt(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            carrying = 0;
            seated = null;
            Cctv.Rebuild(map);
        }

        public override string CompInspectStringExtra()
        {
            if (!Cctv.Needed) return "RP_CctvInspectPolicyOff".Translate();
            var sb = new StringBuilder();
            if (!Working) sb.Append("RP_CctvInspectDark".Translate());
            else if (seated != null) sb.Append("RP_CctvInspectCarrying".Translate(seated.LabelShort, carrying, Props.feeds));
            // (Nobody at it: told apart from a monitor that is off for the hours, which nobody is sent to.)
            else sb.Append((parent.TryGetComp<CompGuardPost>() is { OpenNow: true } ? "RP_CctvScreensEmpty" : "RP_CctvInspectOffHours").Translate());
            return sb.ToString();
        }
    }

    /// <summary>
    /// The control room. With <see cref="PrisonGame.camerasNeedOperator"/> on, a camera is watched live only while a guard sits at a powered, unbroken CCTV monitor that carries it (see <see cref="Rebuild"/>); with it off
    /// every working camera watches by itself, as before. The answer is a cache (<see cref="CompSecurityCamera.watched"/>) rebuilt on the map's beat and at once when the policy, a camera, a monitor or a post's hours change;
    /// the seen rule reads it through <see cref="CompSecurityCamera.WatchedLive"/>, and court evidence does not (<see cref="CompSecurityCamera.Covers"/> alone: a working, lit camera is a recording).
    /// </summary>
    public static class Cctv
    {
        /// <summary>How many cameras one manned monitor carries: the def's number (<see cref="CompProperties_CctvMonitor.feeds"/>), which the cache, the policy's text and the gizmo's text all read.</summary>
        public static int FeedsPerMonitor => RPCctvDefOf.RP_CctvMonitor.GetCompProperties<CompProperties_CctvMonitor>().feeds;

        /// <summary>The old-save letter's decision (see <see cref="PrisonDecisionWorker_CctvSwitch"/>).</summary>
        public const string DecisionDef = "RP_Decision_CctvSwitch";

        /// <summary>Cameras ask this before anything else: the policy is on, so cameras are watched by a monitor and not by themselves.</summary>
        public static bool Needed => PrisonGame.Instance is { camerasNeedOperator: true };

        private static readonly List<CompSecurityCamera> pool = new List<CompSecurityCamera>();
        private static readonly List<CompCctvMonitor> monitors = new List<CompCctvMonitor>();

        /// <summary>The high-priority cameras first, then by thing id (the order the monitors take them in).</summary>
        private static int ByPriority(CompSecurityCamera a, CompSecurityCamera b)
        {
            int byPriority = b.priority.CompareTo(a.priority);
            return byPriority != 0 ? byPriority : a.parent.thingIDNumber.CompareTo(b.parent.thingIDNumber);
        }

        /// <summary>The monitor shows something: it is powered and unbroken (a dark one is no post, see <see cref="CompGuardPost.OpenNow"/>, and carries nothing).</summary>
        public static bool Shows(Thing monitor) => monitor.TryGetComp<CompCctvMonitor>() is { Working: true };

        /// <summary>A monitor's post as a post (null for a thing that is not one).</summary>
        private static CompGuardPost PostOf(CompCctvMonitor monitor) => monitor.parent.TryGetComp<CompGuardPost>();

        /// <summary>The guard who sits at the monitor now: on duty and standing on its <see cref="GuardPosts.StandCell"/> (the job that sent them there is not what counts, the chair is). Null for nobody.</summary>
        public static Pawn OperatorOf(Thing monitor)
        {
            Map map = monitor.Map;
            if (map == null) return null;
            IntVec3 cell = GuardPosts.StandCell(monitor);
            if (!cell.InBounds(map)) return null;
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
                if (things[i] is Pawn p && GuardUtility.IsGuard(p) && GuardPosts.OnDuty(p)) return p;
            return null;
        }

        /// <summary>The monitor shows its screens now: it works, its post is open (the policy is on and its hours cover now) and somebody sits at it. Sets <paramref name="who"/>.</summary>
        private static bool Manned(CompCctvMonitor monitor, out Pawn who)
        {
            who = null;
            if (!monitor.Working || PostOf(monitor) is not { OpenNow: true }) return false;
            who = OperatorOf(monitor.parent);
            return who != null;
        }

        /// <summary>
        /// Works out which cameras are watched: every manned monitor, in thing-id order, takes the next <see cref="CompProperties_CctvMonitor.feeds"/> of the cameras set to show on the screens (the high-priority ones first, then
        /// by thing id). A camera on no screen is not watched. A camera that does not work now (no power, broken down) gives its place to the next; that is only as fresh as the last rebuild (the beat), and
        /// <see cref="CompSecurityCamera.Covers"/> reads whether it works live when it looks, so a camera that breaks between beats sees nothing at once and holds its place until the beat.
        /// </summary>
        public static void Rebuild(Map map)
        {
            PrisonMap pm = map != null ? PrisonMap.Of(map) : null;
            if (pm == null) return;
            List<CompSecurityCamera> cams = pm.Cameras;
            pool.Clear();
            for (int i = 0; i < cams.Count; i++)
            {
                cams[i].watched = false;
                cams[i].screenId = 0;
                cams[i].poolWorked = cams[i].Working;
                cams[i].builtNeeded = Needed;
                if (cams[i].onScreens && cams[i].poolWorked) pool.Add(cams[i]);
            }
            pool.Sort(ByPriority);
            monitors.Clear();
            List<Thing> things = map.listerThings.ThingsOfDef(RPCctvDefOf.RP_CctvMonitor);
            for (int i = 0; i < things.Count; i++)
                if (things[i].TryGetComp<CompCctvMonitor>() is { } m)
                {
                    m.carrying = 0;
                    m.seated = null;
                    monitors.Add(m);
                }
            monitors.Sort((a, b) => a.parent.thingIDNumber.CompareTo(b.parent.thingIDNumber));
            int next = 0;
            for (int i = 0; i < monitors.Count; i++)
            {
                CompCctvMonitor m = monitors[i];
                if (!Needed || !Manned(m, out Pawn who)) continue;
                m.seated = who;
                for (; m.carrying < m.Props.feeds && next < pool.Count; m.carrying++, next++)
                {
                    pool[next].watched = true;
                    pool[next].screenId = m.parent.thingIDNumber;
                }
            }
            pool.Clear();
            monitors.Clear();
        }

        /// <summary>The cache of every home map (a load, the policy).</summary>
        public static void RebuildAll()
        {
            foreach (Map map in Find.Maps)
                if (map.IsPlayerHome) Rebuild(map);
        }

        /// <summary>
        /// The map's beat (after the utilities pass, so a monitor's power is this beat's): the cache is rebuilt. In a test run it is held against a plain walk twice, once before the rebuild (a stale cache: something that changed
        /// the cameras' answers without rebuilding at once) and once after it (the rebuild's own rule).
        /// </summary>
        public static void Beat(Map map, PrisonMap pm)
        {
            if (AutoTest.Active) Shadow.CheckCctvStale(map, pm);
            Rebuild(map);
            if (AutoTest.Active) Shadow.CheckCctv(map, pm);
        }

        /// <summary>The saved flags (PrisonGame.ExposeData): each loads false when its key is missing, which is what an old save has.</summary>
        public static void ExposeFlags(ref bool camerasNeedOperator, ref bool cctvMigrated, ref bool cctvLetterSent)
        {
            Scribe_Values.Look(ref camerasNeedOperator, "camerasNeedOperator");
            Scribe_Values.Look(ref cctvMigrated, "cctvMigrated");
            Scribe_Values.Look(ref cctvLetterSent, "cctvLetterSent");
        }

        /// <summary>The policy is set (the Policy tab, the old-save letter): the warden has chosen, so no letter is owed, and the cache is rebuilt at once.</summary>
        public static void SetPolicy(bool on)
        {
            if (PrisonGame.Instance is not { } game) return;
            game.camerasNeedOperator = on;
            game.cctvMigrated = true;
            RebuildAll();
        }

        /// <summary>A monitor was built (not loaded): the cache is rebuilt, and an old save is offered the switch once.</summary>
        public static void MonitorBuilt(CompCctvMonitor monitor)
        {
            Rebuild(monitor.parent.Map);
            // (A new game has its flags set before the builder puts the penitentiary's monitors up, so none of them finds an unmigrated game; a load rebuilds in FinalizeInit and never gets here.)
            if (PrisonGame.Instance is not { } game || game.cctvMigrated || game.cctvLetterSent) return;
            game.cctvLetterSent = true;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(DecisionDef, "RP_CctvLetterLabel".Translate(), "RP_CctvLetterText".Translate(), monitor.parent.Map, monitor.parent);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>For the tests: what the cache says that a plain walk would not (or the other way round), counted over every camera of the map (watched or not, and which monitor carries it) and every monitor (who sits, how many it carries), see <see cref="Shadow.CheckCctv"/>: who sits now, the cameras that work now.</summary>
        internal static int Mismatches(Map map, PrisonMap pm, out int asked)
        {
            SeatedMonitors(map, live: true);
            asked = pm.Cameras.Count;
            return Differ(map, pm, live: true);
        }

        /// <summary>
        /// For the tests, before a rebuild: how many of the cache's answers are out of date for a reason a rebuild is owed at once (see <see cref="Shadow.CheckCctvStale"/>). Who sits at a monitor and what has power are the beat's to
        /// change (they may lag up to 250 ticks), so the walk takes the monitors that were manned at the last rebuild and the cameras that worked then, and asks what only the warden or the builders change: the policy, which cameras
        /// are on the screens and their priority, which cameras and monitors are spawned (a camera that a taken-down monitor carries, or a monitor that carries what it should not, counts). A difference is a trigger that never rebuilds.
        /// </summary>
        internal static int Stale(Map map, PrisonMap pm, out int asked)
        {
            SeatedMonitors(map, live: false);
            asked = pm.Cameras.Count;
            return Differ(map, pm, live: false);
        }

        private static readonly List<CompCctvMonitor> oracleSeated = new List<CompCctvMonitor>();

        /// <summary>The monitors that would carry cameras, in thing-id order (the order they take them in): the ones manned now (<paramref name="live"/>), else the ones that were seated at the last rebuild and still stand.</summary>
        private static void SeatedMonitors(Map map, bool live)
        {
            oracleSeated.Clear();
            List<Thing> things = map.listerThings.ThingsOfDef(RPCctvDefOf.RP_CctvMonitor);
            for (int i = 0; i < things.Count; i++)
                if (things[i].TryGetComp<CompCctvMonitor>() is { } m && (live ? Needed && Manned(m, out _) : m.seated != null && m.parent.Spawned)) oracleSeated.Add(m);
            oracleSeated.Sort((a, b) => a.parent.thingIDNumber.CompareTo(b.parent.thingIDNumber));
        }

        /// <summary>
        /// The plain way over <see cref="oracleSeated"/>: a camera's rank among those that may go on the screens (the high-priority first, then by id) says whether it is watched and which monitor carries it (each takes its
        /// <see cref="CompProperties_CctvMonitor.feeds"/> in turn), and so how many each monitor carries. <paramref name="live"/>: the cameras that work now, who sits now; else the ones that worked at the last rebuild, and a camera
        /// built under another policy is out of date.
        /// </summary>
        private static int Differ(Map map, PrisonMap pm, bool live)
        {
            bool Eligible(CompSecurityCamera c) => c.onScreens && (live ? c.Working : c.parent.Spawned && c.poolWorked);
            int differ = 0, eligible = 0;
            for (int i = 0; i < pm.Cameras.Count; i++)
                if (Needed && Eligible(pm.Cameras[i])) eligible++;
            for (int i = 0; i < pm.Cameras.Count; i++)
            {
                CompSecurityCamera cam = pm.Cameras[i];
                if (!live && cam.builtNeeded != Needed)
                {
                    differ++;
                    continue;
                }
                int rank = 0;
                if (Eligible(cam))
                    for (int j = 0; j < pm.Cameras.Count; j++)
                        if (j != i && Eligible(pm.Cameras[j]) && ByPriority(pm.Cameras[j], cam) < 0) rank++;
                int owner = 0;
                if (Needed && Eligible(cam))
                {
                    int before = 0;
                    for (int k = 0; k < oracleSeated.Count; k++)
                    {
                        before += oracleSeated[k].Props.feeds;
                        if (rank >= before) continue;
                        owner = oracleSeated[k].parent.thingIDNumber;
                        break;
                    }
                }
                if ((owner != 0) != cam.watched || owner != cam.screenId) differ++;
            }
            List<Thing> things = map.listerThings.ThingsOfDef(RPCctvDefOf.RP_CctvMonitor);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].TryGetComp<CompCctvMonitor>() is not { } m) continue;
                int at = oracleSeated.IndexOf(m), expected = 0;
                if (at >= 0)
                {
                    int before = 0;
                    for (int k = 0; k < at; k++) before += oracleSeated[k].Props.feeds;
                    expected = Mathf.Clamp(eligible - before, 0, m.Props.feeds);
                }
                if (m.carrying != expected || live && m.seated != (at >= 0 ? OperatorOf(m.parent) : null)) differ++;
            }
            return differ;
        }
    }

    /// <summary>The old-save letter: a monitor was built in a game that keeps today's rule; accepting turns on "cameras need an operator".</summary>
    public class PrisonDecisionWorker_CctvSwitch : PrisonDecisionWorker
    {
        public override void Accept(ChoiceLetter_PrisonDecision l, Map map) => Cctv.SetPolicy(true);

        public override void Decline(ChoiceLetter_PrisonDecision l, Map map)
        {
            if (PrisonGame.Instance is { } game) game.cctvMigrated = true;
        }
    }
}
