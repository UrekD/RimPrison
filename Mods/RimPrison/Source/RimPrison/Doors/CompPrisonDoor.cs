using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    public enum PrisonDoorMode : byte
    {
        /// <summary>Inmates pass when both sides are inmate space; cell doors follow the regime.</summary>
        Auto,
        /// <summary>Inmates always pass (internal doors of a wing).</summary>
        Inmates,
        /// <summary>Only staff and visitors pass.</summary>
        StaffOnly,
        /// <summary>Nobody opens it.</summary>
        Locked,
    }

    /// <summary>What lies on one side of a door, from the inmates' point of view.</summary>
    public enum DoorSide : byte
    {
        None,
        InmateSpace,
        Cell,
        Staff,
        Outside,
    }

    /// <summary>
    /// Door policy for inmates and staff. The inmate verdict is computed by <see cref="PrisonMap"/> every 250 ticks
    /// (never per path query), so <see cref="Building_Door.PawnCanOpen"/> stays a field read and the reachability
    /// cache stays consistent: it is cleared once whenever any verdict flips.
    /// </summary>
    public class CompPrisonDoor : ThingComp
    {
        private PrisonDoorMode modeInt;

        /// <summary>Who may pass. Changing it clears the reachability cache (once, with the next door pass): a mode change decides who may pass even when no inmate verdict flips.</summary>
        public PrisonDoorMode mode
        {
            get => modeInt;
            set
            {
                if (modeInt == value) return;
                modeInt = value;
                parent?.Map?.GetComponent<PrisonMap>()?.Notify_DoorModeChanged();
            }
        }

        /// <summary>Computed: may an inmate open this door right now.</summary>
        public volatile bool inmatePass;
        /// <summary>Computed: the cell room behind this door, if it is a cell door.</summary>
        public Room cellRoom;
        public DoorSide sideA, sideB;
        /// <summary>Computed: the rooms on either side (main thread only; see <see cref="PrisonMap"/>'s door pass).</summary>
        public Room roomA, roomB;
        /// <summary>Computed: may a new arrival from the shuttle (escorted or walking in) pass: a gate to the outside, a door from the sally port behind it
        /// (the staff room a gate opens into, the gatehouse) into the prison, or any door that is neither staff-only nor into a staff room. Read on path threads.</summary>
        public volatile bool arrivalPass;
        /// <summary>The rule behind <see cref="arrivalPass"/>: <paramref name="portA"/>/<paramref name="portB"/> say whether the room on that side is a sally port.</summary>
        public static bool ArrivalPass(PrisonDoorMode mode, DoorSide a, DoorSide b, bool portA, bool portB)
        {
            if (mode == PrisonDoorMode.Locked) return false;
            bool gate = a == DoorSide.Outside && (b != DoorSide.Staff || portB) || b == DoorSide.Outside && (a != DoorSide.Staff || portA);
            bool fromSallyPort = portA && b != DoorSide.Staff || portB && a != DoorSide.Staff;
            bool plain = a != DoorSide.Staff && b != DoorSide.Staff && mode != PrisonDoorMode.StaffOnly;
            return gate || fromSallyPort || plain;
        }
        /// <summary>Computed: the staff work zone behind an Auto door that faces inmate space (kitchen); inmates
        /// with a work pass for that zone may use the door during work hours. Read on pathfinding threads.</summary>
        public volatile PrisonZoneDef workZone;
        /// <summary>Computed: this door is on the border of a room or wing held by rioters (a hostage crisis): the cells held, null otherwise. Staff are refused whichever side they stand on. Read on path threads.</summary>
        public volatile HashSet<int> heldCells;
        /// <summary>Tick the cell's occupants entered a lockup activity; -1 when not in lockup.</summary>
        public int lockupSince = -1;

        public Building_Door Door => (Building_Door)parent;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            parent.Map.GetComponent<PrisonMap>()?.RegisterDoor(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            map.GetComponent<PrisonMap>()?.DeregisterDoor(this);
        }

        public override void PostExposeData()
        {
            Scribe_Values.Look(ref modeInt, "rpDoorMode");
            Scribe_Values.Look(ref lockupSince, "rpLockupSince", -1);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (parent.Faction != Faction.OfPlayer) yield break;
            yield return new Command_Action
            {
                defaultLabel = ("RP_DoorMode_" + mode).Translate(),
                defaultDesc = "RP_DoorModeDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Icons/Door", reportFailure: false) ?? ModeIcon(mode),
                defaultIconColor = DeploymentTab.ModeColor(mode),
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    foreach (PrisonDoorMode m in new[] { PrisonDoorMode.Auto, PrisonDoorMode.Inmates, PrisonDoorMode.StaffOnly, PrisonDoorMode.Locked })
                    {
                        PrisonDoorMode chosen = m;
                        options.Add(new FloatMenuOption(("RP_DoorMode_" + m).Translate(), () => SetModeOnSelected(chosen)));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                },
            };
        }

        private static void SetModeOnSelected(PrisonDoorMode m)
        {
            foreach (object o in Find.Selector.SelectedObjects)
            {
                if (o is ThingWithComps t && t.GetComp<CompPrisonDoor>() is { } comp)
                {
                    comp.mode = m;
                    t.Map.GetComponent<PrisonMap>()?.Notify_DoorsChanged();
                }
            }
        }

        private static Texture2D ModeIcon(PrisonDoorMode m) => m switch
        {
            PrisonDoorMode.Locked => TexCommand.ForbidOn,
            PrisonDoorMode.StaffOnly => TexCommand.ForbidOff,
            _ => TexCommand.HoldOpen,
        };

        public override string CompInspectStringExtra()
        {
            string verdict = mode == PrisonDoorMode.Locked ? "RP_DoorLocked".Translate() :
                inmatePass ? "RP_DoorInmatesPass".Translate() : "RP_DoorInmatesBlocked".Translate();
            return "RP_DoorModeLabel".Translate(("RP_DoorMode_" + mode).Translate()) + " (" + verdict + ")" + (heldCells != null ? "\n" + "RP_DoorHeld".Translate().ToString() : "");
        }
    }

    /// <summary>
    /// Runs on pathfinding worker threads (PathGridDoorsBlockedJob) as well as the main thread: only reads fields
    /// that the main thread precomputed (door verdict, the inmate's active flag). No Find.*, rooms or lords here.
    /// </summary>
    [HarmonyPatch(typeof(Building_Door), nameof(Building_Door.PawnCanOpen))]
    public static class Patch_Building_Door_PawnCanOpen
    {
        public static void Postfix(Building_Door __instance, Pawn p, ref bool __result)
        {
            // p is null when the pathfinder precomputes door blocking for "nobody" (worker thread).
            if (p == null) return;
            CompPrisonDoor comp = __instance.GetComp<CompPrisonDoor>();
            if (comp == null) return;
            if (comp.mode == PrisonDoorMode.Locked)
            {
                // Released inmates walk out through locked gates (staff let them out); so do arrests.
                if ((p.guest == null || !p.guest.Released) && p.GetComp<CompStaff>() is not { passLocked: true }) __result = false;
                return;
            }
            // Held by rioters: no member of staff opens it, whichever side they stand on (staff caught inside are let out by a job, see
            // HostageHold.LetStaffOut). The verdict must not depend on the pawn's position: vanilla's reachability cache keeps one answer per pawn and
            // pair of districts and is not cleared when a pawn crosses a door. Riot police storming it are let in when the crisis ends.
            if (comp.heldCells != null && p.GetComp<CompStaff>() is { role: not null, passLocked: false })
            {
                __result = false;
                return;
            }
            // Active inmates (also rioting/escaping ones) get the door's inmate verdict; their aggro and escape jobs
            // bash doors they cannot open; new arrivals from the shuttle (inTransit) pass a door that is not locked, but not one held by rioters, not one into a staff
            // room (a starving arrival would walk into the kitchen for food) and not a staff-only one inside the prison, except the way in: the gate and the
            // sally port behind it (a gatehouse is a staff room; they are walked through it into the prison). See CompPrisonDoor.arrivalPass.
            // Non-inmates (staff, visitors, released, and transfers, who are released) keep vanilla rules.
            if (p.GetComp<CompInmate>() is { activeFlag: true } c)
            {
                PrisonZoneDef pass = c.workPass; // read once: written by the main thread
                bool arrival = c.inTransit && comp.heldCells == null && comp.arrivalPass;
                // A guard has them by the arm (supermax to the shower, anyone to the hearing room): the doors on the path of that walk, and no other (not another inmate's cell, not a staff door that
                // is not on it), and not a door held by rioters, either. The leg is cleared the moment the guard stops holding them (see EscortWalk.Holds).
                bool escorted = c.leg is { } leg && comp.heldCells == null && leg.PassesDoor(comp.Door);
                // An evacuation's walker: the doors on the route planned for them (a set made on the main thread and never changed), and no other; never a locked door (that is refused above) or one held by rioters.
                bool evacuating = c.evacDoors is { } route && comp.heldCells == null && route.Contains(comp.Door);
                __result = comp.inmatePass || (pass != null && pass == comp.workZone) || arrival || escorted || evacuating;
            }
        }
    }
}
