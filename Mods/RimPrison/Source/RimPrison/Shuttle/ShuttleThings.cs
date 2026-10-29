using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPShuttleDefOf
    {
        public static ThingDef RP_StateShuttle;
        public static ThingDef RP_ShuttleIncoming;
        public static ThingDef RP_ShuttleLeaving;
        public static ThingDef RP_ShuttlePad;
        public static JobDef RP_BoardShuttle;
        public static JobDef RP_EscortArrival;
        public static JobDef RP_EscortTransfer;
        public static JobDef RP_PadDuty;
        public static MisconductDef RP_EscapeAttempt;

        static RPShuttleDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPShuttleDefOf));
    }

    /// <summary>
    /// The state shuttle standing on its pad. It holds the arrivals who have not yet stepped off (unspawned, like pawns in a drop pod) and
    /// is the thing the outbound inmates walk to; the schedule lives in <see cref="ShuttleVisit"/>.
    /// </summary>
    public class Building_StateShuttle : Building, IThingHolder
    {
        /// <summary>Arrivals still aboard.</summary>
        public ThingOwner<Pawn> passengers;

        public Building_StateShuttle() => passengers = new ThingOwner<Pawn>(this, oneStackOnly: false);

        public ThingOwner GetDirectlyHeldThings() => passengers;

        public void GetChildHolders(List<IThingHolder> outChildren) => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref passengers, "passengers", this);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) passengers ??= new ThingOwner<Pawn>(this, oneStackOnly: false);
        }

        public override string GetInspectString()
        {
            string basic = base.GetInspectString();
            ShuttleVisit v = ShuttleSystem.VisitOf(this);
            if (v == null) return basic;
            string line = ShuttleSystem.StatusLine(v);
            return basic.NullOrEmpty() ? line : basic + "\n" + line;
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            if (Spawned) GenDraw.DrawFieldEdges(new List<IntVec3> { InteractionCell }, Color.white);
        }
    }

    /// <summary>
    /// The shuttle coming down. Core's skyfaller, which holds the shuttle building: on impact the building is set on the ground (nothing under it is
    /// crushed: pawns are moved aside, items are moved aside), and until the last moment it hovers if somebody walks onto the landing spot.
    /// </summary>
    public class StateShuttleIncoming : Skyfaller
    {
        public const int HoverAt = 40, MaxHoverTicks = 600;
        private int hoverTicks;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref hoverTicks, "hoverTicks");
        }

        protected override void Tick()
        {
            if (!hasImpacted && ticksToImpact <= HoverAt + 1 && hoverTicks < MaxHoverTicks && Map != null && ShuttleSystem.AnyPawnOn(Map, Position))
            {
                hoverTicks++;
                ticksToImpact = HoverAt + 1;
            }
            base.Tick();
        }

        protected override void SpawnThings()
        {
            Map map = Map;
            for (int i = innerContainer.Count - 1; i >= 0; i--)
            {
                Thing t = innerContainer[i];
                ShuttleSystem.ClearSite(map, Position, t.def);
                GenSpawn.Spawn(t, Position, map, Rot4.North, WipeMode.VanishOrMoveAside);
                innerContainer.Remove(t);
                if (t is Building_StateShuttle shuttle) ShuttleSystem.Notify_Landed(shuttle);
            }
        }
    }

    /// <summary>The shuttle lifting off: when it is out of sight the visit is over.</summary>
    public class StateShuttleLeaving : Skyfaller
    {
        protected override void LeaveMap()
        {
            foreach (Thing t in innerContainer.ToList())
                if (t is Building_StateShuttle shuttle) ShuttleSystem.Notify_Gone(shuttle);
            base.LeaveMap();
        }
    }

    /// <summary>The pad goes on open ground, not on or through walls and furniture.</summary>
    public class PlaceWorker_ShuttlePad : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, checkingDef.Size))
            {
                if (!c.InBounds(map)) continue;
                Building edifice = c.GetEdifice(map);
                if (edifice != null && edifice != thingToIgnore) return new AcceptanceReport("RP_PadBlockedBy".Translate(edifice.LabelNoCount));
            }
            return true;
        }
    }
}
