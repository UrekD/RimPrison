using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    public enum ShuttleKind : byte
    {
        /// <summary>The state's daily shuttle at the intake hour: new inmates, the supplies that are due, the post of the prison, and the way out.</summary>
        Daily,
        /// <summary>A cargo run between the daily shuttles, called when orders are due and the next daily is hours away.</summary>
        Cargo,
    }

    public enum ShuttlePhase : byte
    {
        /// <summary>Called, not yet in the air: waiting for the weather or a threat to pass, or for the pad to be cleared.</summary>
        Holding,
        /// <summary>The skyfaller is coming down.</summary>
        Inbound,
        /// <summary>On the ground: unloading, boarding, waiting for its time.</summary>
        Landed,
        /// <summary>Lifting off.</summary>
        Leaving,
    }

    /// <summary>Marks a weather the state shuttle does not fly in (a thunderstorm, hard snow: put on the weather defs by Patches/Shuttle.xml).</summary>
    public class ShuttleHoldExtension : DefModExtension
    {
    }

    public enum ShuttleHold : byte
    {
        None,
        Weather,
        Threat,
        PadBlocked,
        /// <summary>No pad, and no clear ground near holding or the gate for the hull: tried again every hour.</summary>
        NoSite,
    }

    /// <summary>One shuttle's stay, from the call to the lift-off. Saved with the game (the shuttle thing itself is saved with its map or its skyfaller).</summary>
    public class ShuttleVisit : IExposable
    {
        public ShuttleKind kind;
        public ShuttlePhase phase;
        public int mapId = -1;
        public Building_StateShuttle shuttle;
        public IntVec3 site = IntVec3.Invalid;
        public bool onPad;
        public int calledTick, blockedSince = -1, landedTick = -1, departTick = -1, nextStepTick;
        public ShuttleHold hold;
        public bool announcedHold, announcedBlocked, arrivalsDone, cargoDone, stowawayRolled;
        /// <summary>The stay was stretched to finish the unloading (told once).</summary>
        public bool stretched;
        /// <summary>Passengers the shuttle brought, how many have stepped off, and the grant the state paid for them (booked once the unloading is over, or the visit ends first).</summary>
        public int rosterSize, steppedOff, grant;
        /// <summary>Everybody who boarded it.</summary>
        public int boarded;
        /// <summary>Supplies aboard, unloaded after the passengers.</summary>
        public List<SupplyOrder> cargo = new List<SupplyOrder>();
        public int cargoStacks, cargoUnits;
        /// <summary>A crate could not be set down anywhere (no clear ground): the rest stays aboard, goes back with the lift-off, and does not hold the shuttle up.</summary>
        public bool cargoBlocked;
        /// <summary>The last tick a guard on pad duty stood on the pad.</summary>
        public int lastGuardTick = -999999;

        public Map Map
        {
            get
            {
                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                    if (maps[i].uniqueID == mapId) return maps[i];
                return null;
            }
        }

        public bool Daily => kind == ShuttleKind.Daily;

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref phase, "phase");
            Scribe_Values.Look(ref mapId, "mapId", -1);
            Scribe_References.Look(ref shuttle, "shuttle");
            Scribe_Values.Look(ref site, "site", IntVec3.Invalid);
            Scribe_Values.Look(ref onPad, "onPad");
            Scribe_Values.Look(ref calledTick, "calledTick");
            Scribe_Values.Look(ref blockedSince, "blockedSince", -1);
            Scribe_Values.Look(ref landedTick, "landedTick", -1);
            Scribe_Values.Look(ref departTick, "departTick", -1);
            Scribe_Values.Look(ref nextStepTick, "nextStepTick");
            Scribe_Values.Look(ref hold, "hold");
            Scribe_Values.Look(ref announcedHold, "announcedHold");
            Scribe_Values.Look(ref announcedBlocked, "announcedBlocked");
            Scribe_Values.Look(ref arrivalsDone, "arrivalsDone");
            Scribe_Values.Look(ref cargoDone, "cargoDone");
            Scribe_Values.Look(ref stowawayRolled, "stowawayRolled");
            Scribe_Values.Look(ref stretched, "stretched");
            Scribe_Values.Look(ref rosterSize, "rosterSize");
            Scribe_Values.Look(ref steppedOff, "steppedOff");
            Scribe_Values.Look(ref grant, "grant");
            Scribe_Values.Look(ref boarded, "boarded");
            Scribe_Collections.Look(ref cargo, "cargo", LookMode.Deep);
            Scribe_Values.Look(ref cargoStacks, "cargoStacks");
            Scribe_Values.Look(ref cargoUnits, "cargoUnits");
            Scribe_Values.Look(ref cargoBlocked, "cargoBlocked");
            Scribe_Values.Look(ref lastGuardTick, "lastGuardTick", -999999);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                cargo ??= new List<SupplyOrder>();
                cargo.RemoveAll(o => o?.thing == null);
            }
        }

        public string Describe() =>
            $"{kind}:{phase}:{mapId}:{site}:{onPad}:{landedTick}:{departTick}:{hold}:{rosterSize}/{steppedOff}:{grant}:{boarded}:[{string.Join("+", cargo.Select(o => o.thing.defName + "x" + o.count + "@" + o.arriveTick + "$" + o.cost))}]:{cargoStacks}/{cargoUnits}:{arrivalsDone}{cargoDone}{stretched}{cargoBlocked}{announcedHold}{announcedBlocked}{stowawayRolled}"
            + $":aboard[{(shuttle == null ? "" : string.Join("+", shuttle.passengers.InnerListForReading.Select(p => p.thingIDNumber)))}]:{shuttle?.thingIDNumber}:{nextStepTick}:{calledTick}:{blockedSince}:{lastGuardTick}";
    }
}
