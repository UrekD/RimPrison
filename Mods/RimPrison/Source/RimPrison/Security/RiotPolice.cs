using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>Emergency callout: a riot squad arrives, restrains rioters, and leaves after a few hours.</summary>
    public static class RiotPolice
    {
        public const int Cost = 2500;
        public const int SquadSize = 5;
        public const int StayHours = 8;

        /// <summary>A rioter is a rioter for this many ticks after their last tick (see <see cref="RiotActive"/>).</summary>
        public const int RiotMarkTicks = 60;

        private static readonly Dictionary<int, int> riotMarks = new Dictionary<int, int>();

        /// <summary>A rioter on this map is in their mental state at this tick (see <see cref="MentalState_Rioting"/>).</summary>
        public static void Notify_Rioting(Map map)
        {
            if (map != null) riotMarks[map.uniqueID] = Find.TickManager.TicksGame;
        }

        /// <summary>A riot is on, as the rioters' own ticks say (a flag read, no scan of the prisoners: <see cref="RiotOngoing"/> is the exact one).</summary>
        public static bool RiotActive(Map map) => map != null && riotMarks.TryGetValue(map.uniqueID, out int tick) && Find.TickManager.TicksGame - tick is >= 0 and <= RiotMarkTicks;

        /// <summary>A new game or a load: the marks are not saved, and one from another game's clock (or from later than now) is not this game's riot.</summary>
        internal static void Reset() => riotMarks.Clear();

        /// <summary>For a check: the riot's flag is put down at once (it would otherwise hold for <see cref="RiotMarkTicks"/> ticks).</summary>
        internal static void ForgetRiotForTests(Map map) => riotMarks.Remove(map.uniqueID);

        /// <summary>For a check: the last rioter's tick on the map is this one (a mark from a game with another clock, say).</summary>
        internal static void MarkForTests(Map map, int tick) => riotMarks[map.uniqueID] = tick;

        public static bool RiotOngoing(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.MentalStateDef == RPMentalStateDefOf.RP_Rioting) return true;
            return false;
        }

        /// <summary>Calls the riot police. With a hostage crisis running, the squad is sent to storm the held room (the stand-off's own storm option).</summary>
        public static void Call(Map map)
        {
            if (HostageCrisis.Running(map) is { } crisis) crisis.def.Worker.Choose(crisis, map, 3);
            else CallSquad(map);
        }

        /// <summary>Calls the squad out (null when the prison cannot afford it); the officers who arrive.</summary>
        public static List<Pawn> CallSquad(Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            if (!game.ledger.TrySpend(RPDefOf.RP_Hiring, Cost, "RP_RiotPoliceNote".Translate()))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(Cost)), MessageTypeDefOf.RejectInput, historical: false);
                return null;
            }
            StaffRoleDef role = RPStaffRoleDefOf.RP_RiotPolice;
            IntVec3 spot = IntakeSystem.IntakeSpot(map);
            var squad = new List<Pawn>();
            for (int i = 0; i < SquadSize; i++)
            {
                Pawn p = StaffUtility.GenerateCandidate(role);
                if (p == null) continue;
                Dress(p, armoured: true);
                if (!CellFinder.TryFindRandomCellNear(spot, map, 5, c => c.Standable(map), out IntVec3 cell)) cell = spot;
                GenSpawn.Spawn(p, cell, map);
                StaffUtility.ApplyRole(p, role, record: false);
                p.GetComp<CompStaff>().leaveAtTick = Find.TickManager.TicksGame + StayHours * GenDate.TicksPerHour;
                // On duty for their whole stay (no naps on the floor).
                if (p.timetable != null)
                    for (int h = 0; h < 24; h++) p.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                squad.Add(p);
            }
            game.stats.policeCallouts++;
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_RiotPolice".Translate(squad.Count));
            Find.LetterStack.ReceiveLetter("RP_RiotPoliceLabel".Translate(), "RP_RiotPoliceText".Translate(squad.Count, StayHours), LetterDefOf.PositiveEvent, new LookTargets(squad));
            return squad;
        }

        private static ThingDef[] Uniform => new[] { RPVanillaDefOf.Apparel_BasicShirt, RPVanillaDefOf.Apparel_Pants };
        private static ThingDef[] RiotUniform => new[] { RPVanillaDefOf.Apparel_BasicShirt, RPVanillaDefOf.Apparel_Pants, RPVanillaDefOf.Apparel_FlakVest, RPVanillaDefOf.Apparel_SimpleHelmet };

        /// <summary>Police dark blue; riot squads also get armour and a club.</summary>
        public static void Dress(Pawn p, bool armoured)
        {
            p.apparel?.DestroyAll();
            foreach (ThingDef d in armoured ? RiotUniform : Uniform)
            {
                if (!d.apparel.PawnCanWear(p)) continue;
                var a = (Apparel)ThingMaker.MakeThing(d, d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null);
                a.SetColor(new UnityEngine.Color(0.15f, 0.15f, 0.2f), reportFailure: false);
                p.apparel.Wear(a, dropReplacedApparel: false);
            }
            ThingDef club = RPVanillaDefOf.MeleeWeapon_Club;
            if (armoured && p.equipment != null)
            {
                p.equipment.DestroyAllEquipment();
                p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(club, ThingDefOf.WoodLog));
            }
        }
    }
}
