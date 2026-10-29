using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The Rim Prison start: an empty, flat, fog-free plot without wildlife or ruins; starting colonists get staff
    /// roles; optionally a ready-made starter facility.
    /// </summary>
    public class ScenPart_PrisonStart : ScenPart
    {
        public bool starterFacility;
        /// <summary>Which ready-made prison <see cref="starterFacility"/> puts up (the inherited facility unless a scenario says otherwise).</summary>
        public Facility facility;
        /// <summary>Bureaucracy projects already done at the start (an inherited facility comes with its offices).</summary>
        public List<BureaucracyDef> unlocked = new List<BureaucracyDef>();
        /// <summary>The sixth starting role (after the cook, the doctor, two guards and the workman): the janitor unless the scenario says otherwise (nobody else starts with Cleaning).</summary>
        public StaffRoleDef sixthRole;
        /// <summary>A seventh starting role on top of the six, or null: the inherited facility has an office with a desk, so it comes with an administrator (and keeps its janitor).</summary>
        public StaffRoleDef seventhRole;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref starterFacility, "starterFacility");
            Scribe_Values.Look(ref facility, "facility");
            Scribe_Collections.Look(ref unlocked, "unlocked", LookMode.Def);
            Scribe_Defs.Look(ref sixthRole, "sixthRole");
            Scribe_Defs.Look(ref seventhRole, "seventhRole");
        }

        public override void PostMapGenerate(Map map)
        {
            if (Find.GameInitData == null) return;
            PlotClearer.Clear(map);
        }

        public override void PostGameStart()
        {
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return;
            // The funds the difficulty gives (the neutral level's are the constant; set before anything is bought).
            if (PrisonGame.Instance != null) PrisonGame.Instance.ledger.funds = PrisonDifficulty.StartingFunds(Find.Storyteller?.difficultyDef, starterFacility && facility == Facility.Penitentiary);
            if (unlocked != null && PrisonGame.Instance != null)
                foreach (BureaucracyDef b in unlocked)
                    if (b != null && !PrisonGame.Instance.bureaucracy.IsDone(b)) PrisonGame.Instance.bureaucracy.done.Add(b);
            // Scarce skills first: each role takes the best capable colonist still free.
            // (An administrator needs the Intellectual work type and is picked before the guards and the workman, who take whoever is left.)
            StaffRoleDef sixth = sixthRole ?? RPStaffRoleDefOf.RP_Janitor;
            var roles = new List<StaffRoleDef>();
            roles.Add(RPStaffRoleDefOf.RP_Cook);
            roles.Add(RPStaffRoleDefOf.RP_Doctor);
            foreach (StaffRoleDef scarce in new[] { sixth, seventhRole })
                if (scarce != null && scarce.workTypes.Contains(RPAdminDefOf.RP_Administration)) roles.Add(scarce);
            roles.Add(RPSecurityDefOf.RP_Guard);
            roles.Add(RPSecurityDefOf.RP_Guard);
            roles.Add(RPStaffRoleDefOf.RP_Workman);
            foreach (StaffRoleDef rest in new[] { sixth, seventhRole })
                if (rest != null && !roles.Contains(rest)) roles.Add(rest);
            var free = new List<Pawn>(map.mapPawns.FreeColonists);
            foreach (StaffRoleDef role in roles)
            {
                if (free.Count == 0) break;
                Pawn best = StaffUtility.BestCandidate(free, role);
                free.Remove(best);
                StaffUtility.ApplyRole(best, role);
                StaffUtility.DressInUniform(best, role);
            }
            foreach (Pawn p in free)
            {
                StaffRoleDef role = RPStaffRoleDefOf.RP_Workman;
                StaffUtility.ApplyRole(p, role);
                StaffUtility.DressInUniform(p, role);
            }
            // A modern state prison: everything vanilla research unlocks is available from day one.
            foreach (ResearchProjectDef r in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
                if (r.baseCost > 0f && !r.IsFinished) Find.ResearchManager.FinishProject(r, doCompletionDialog: false, researcher: null, doCompletionLetter: false);
            // Starting supplies arrive forbidden with the standing arrival method; the prison owns them.
            foreach (Thing t in map.listerThings.AllThings)
                if (t.def.category == ThingCategory.Item && t.IsForbidden(Faction.OfPlayer)) t.SetForbidden(false, warnOnFail: false);
            // (Taken whatever the start is: a start with no facility to hand over has no past to sow, and the choice must not wait for the next game.)
            bool inherit = Handover.Take();
            if (starterFacility)
            {
                PrisonBuilder.Kind = facility;
                PrisonBuilder.InheritProblems = inherit;
                PrisonBuilder.BuildStarter(map);
            }
            // The warden on the floor: the player's presence, on top of the staff (not one of the colonists the scenario gave them).
            WardenSystem.Appoint(map, map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? IntVec3.Invalid, announce: false);
            PrisonGame.Instance?.supply.Restock(map);
            // The land the prison owns: a built facility's footprint (the road, pad and deliveries are in it) and a margin, else a centred square.
            PrisonMap.Of(map)?.grounds.SetStarting(starterFacility);
        }
    }

    public static class PlotClearer
    {
        /// <summary>Removes rock, ruins, hives, chunks, DLC map features, roofs, fog and wildlife; rough stone floor
        /// becomes soil.</summary>
        public static void Clear(Map map)
        {
            var doomed = new List<Thing>();
            foreach (Thing t in map.listerThings.AllThings)
            {
                if (t is Pawn p)
                {
                    if (p.Faction == null || !p.Faction.IsPlayer) doomed.Add(t);
                    continue;
                }
                // Ruins and anything owned by another faction (insect hives keep spawning insects that maul inmates).
                if (t.def.category == ThingCategory.Building && t.Faction?.IsPlayer != true) doomed.Add(t);
                else if (t.def.IsWithinCategory(ThingCategoryDefOf.Chunks)) doomed.Add(t);
                else if (t.def.category == ThingCategory.Filth) doomed.Add(t);
            }
            foreach (Thing t in doomed)
            {
                if (t.Destroyed || !t.Spawned) continue;
                // Indestructible map features (Anomaly's void monolith, ancient structures) are taken off the map.
                if (t.def.destroyable) t.Destroy(DestroyMode.Vanish);
                else t.DeSpawn(DestroyMode.Vanish);
            }
            TerrainDef soil = TerrainDefOf.Soil;
            foreach (IntVec3 c in map.AllCells)
            {
                map.roofGrid.SetRoof(c, null);
                TerrainDef terr = c.GetTerrain(map);
                if (terr.smoothedTerrain != null || terr.defName.EndsWith("_Rough") || terr.defName.EndsWith("_RoughHewn"))
                    map.terrainGrid.SetTerrain(c, soil);
            }
            map.fogGrid.ClearAllFog();
            RPLog.Message($"plot cleared: {doomed.Count} things removed");
        }
    }
}
