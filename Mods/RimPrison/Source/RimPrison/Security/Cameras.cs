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

    /// <summary>CCTV: inmates in view are watched (suppression, misconduct gets seen). Needs power.</summary>
    public class CompSecurityCamera : ThingComp
    {
        public CompProperties_SecurityCamera Props => (CompProperties_SecurityCamera)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            PrisonMap.Of(parent.Map)?.RegisterCamera(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            PrisonMap.Of(map)?.DeregisterCamera(this);
        }

        public bool Working => parent.Spawned && !parent.IsBrokenDown() && (parent.TryGetComp<CompPowerTrader>() is not { } p || p.PowerOn);

        // (The distance first: it is the cheap test that says no to nearly every inmate, and the answer to most cameras.)
        public bool Covers(IntVec3 c) => c.InHorDistOf(parent.Position, Props.radius) && Working && GenSight.LineOfSight(parent.Position, c, parent.Map, skipFirstCell: true);

        public override void PostDrawExtraSelectionOverlays() => GenDraw.DrawRadiusRing(parent.Position, Props.radius);
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
