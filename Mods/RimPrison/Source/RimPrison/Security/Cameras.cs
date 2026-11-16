using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    public class CompProperties_SecurityCamera : CompProperties
    {
        public float radius = 9f;

        public CompProperties_SecurityCamera() => compClass = typeof(CompSecurityCamera);
    }

    /// <summary>Which cameras the monitors carry first (see <see cref="Cctv"/>).</summary>
    public enum CctvPriority
    {
        Normal,
        High,
    }

    /// <summary>CCTV: inmates in view are watched (suppression, misconduct gets seen). Needs power, and with the policy on (see <see cref="Cctv"/>) a guard at a monitor that carries the camera.</summary>
    public class CompSecurityCamera : ThingComp
    {
        public CompProperties_SecurityCamera Props => (CompProperties_SecurityCamera)props;

        /// <summary>The warden's choice: the camera is one the monitors may carry (saved; on to begin with).</summary>
        public bool onScreens = true;

        /// <summary>The warden's choice: the monitors carry the high-priority cameras first (saved).</summary>
        public CctvPriority priority;

        /// <summary>Derived, never saved: a manned, powered, unbroken monitor carries this camera now (see <see cref="Cctv.Rebuild"/>).</summary>
        internal bool watched;

        /// <summary>Derived, never saved: the camera worked, and the policy was on, when the cache was last rebuilt (what the test's stale-cache walk holds the cache against).</summary>
        internal bool poolWorked, builtNeeded;

        /// <summary>Derived, never saved: the thing id of the monitor that carries it (0: none).</summary>
        internal int screenId;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            PrisonMap.Of(parent.Map)?.RegisterCamera(this);
            // (A load is rebuilt once when the game is ready, see PrisonGame.FinalizeInit: not once per camera.)
            if (!respawningAfterLoad) Cctv.Rebuild(parent.Map);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            PrisonMap.Of(map)?.DeregisterCamera(this);
            watched = false;
            screenId = 0;
            Cctv.Rebuild(map);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref onScreens, "onScreens", true);
            Scribe_Values.Look(ref priority, "priority", CctvPriority.Normal);
        }

        public bool Working => parent.Spawned && !parent.IsBrokenDown() && (parent.TryGetComp<CompPowerTrader>() is not { } p || p.PowerOn);

        /// <summary>
        /// Whether the camera is watched live: always when the policy is off (today's rule), else only when a manned, powered monitor carries it. A watched camera may still not see a cell (<see cref="Covers"/>);
        /// court evidence reads <see cref="Covers"/> alone (a working, lit camera is a recording).
        /// </summary>
        public bool WatchedLive => !Cctv.Needed || watched;

        // (The distance first: it is the cheap test that says no to nearly every inmate, and the answer to most cameras.)
        public bool Covers(IntVec3 c) => c.InHorDistOf(parent.Position, Props.radius) && Working && GenSight.LineOfSight(parent.Position, c, parent.Map, skipFirstCell: true);

        public override void PostDrawExtraSelectionOverlays() => GenDraw.DrawRadiusRing(parent.Position, Props.radius);

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer) yield break;
            yield return new Command_Toggle
            {
                defaultLabel = "RP_CctvOnScreens".Translate(),
                defaultDesc = "RP_CctvOnScreensDesc".Translate(Cctv.FeedsPerMonitor),
                icon = parent.def.uiIcon ?? BaseContent.BadTex,
                isActive = () => onScreens,
                toggleAction = () =>
                {
                    onScreens = !onScreens;
                    Cctv.Rebuild(parent.Map);
                },
            };
            yield return new Command_Action
            {
                defaultLabel = ("RP_CctvPriority_" + priority).Translate(),
                defaultDesc = "RP_CctvPriorityDesc".Translate(),
                icon = TexButton.ReorderUp,
                action = () =>
                {
                    priority = priority == CctvPriority.High ? CctvPriority.Normal : CctvPriority.High;
                    Cctv.Rebuild(parent.Map);
                },
            };
        }

        public override string CompInspectStringExtra()
        {
            if (!Cctv.Needed) return null;
            var sb = new StringBuilder();
            sb.Append((onScreens ? "RP_CctvInspectOnScreens" : "RP_CctvInspectOffScreens").Translate(("RP_CctvPriorityWord_" + priority).Translate()));
            // (A camera with no power or broken down shows no picture: it holds no place on a screen and sees nothing.)
            sb.Append(" - ").Append((!Working ? "RP_CctvInspectNoPicture" : watched ? "RP_CctvInspectWatched" : "RP_CctvInspectUnwatched").Translate());
            return sb.ToString();
        }
    }

    public class PlaceWorker_CameraRadius : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            CompProperties_SecurityCamera props = def.GetCompProperties<CompProperties_SecurityCamera>();
            if (props != null) GenDraw.DrawRadiusRing(center, props.radius);
        }
    }
}
