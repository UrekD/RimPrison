using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// A bureaucracy project (Prison Architect's office tree): costs money up front and administrator hours at an
    /// office desk; unlocks buildings, zones, staff roles and security levels. Anything no project lists is
    /// available from the start.
    /// </summary>
    public class BureaucracyDef : Def
    {
        public int cost;
        /// <summary>Administrator-hours of paperwork (an average administrator does about 1 per hour).</summary>
        public float work = 8f;
        public int uiOrder;
        public List<BureaucracyDef> prerequisites = new List<BureaucracyDef>();
        public List<ThingDef> buildings = new List<ThingDef>();
        public List<PrisonZoneDef> zones = new List<PrisonZoneDef>();
        public List<StaffRoleDef> roles = new List<StaffRoleDef>();
        public List<SecurityLevelDef> securities = new List<SecurityLevelDef>();
        /// <summary>Named capabilities checked in code (Unlocks.Feature): "Tasers", "RiotGear".</summary>
        public List<string> features = new List<string>();

        public IEnumerable<Def> Unlocks => buildings.Cast<Def>().Concat(zones).Concat(roles).Concat(securities);
    }

    public static class Unlocks
    {
        private static Dictionary<Def, BureaucracyDef> gates;

        /// <summary>The project that unlocks a def, or null when it is available from the start.</summary>
        public static BureaucracyDef GateOf(Def def)
        {
            if (gates == null)
            {
                gates = new Dictionary<Def, BureaucracyDef>();
                foreach (BureaucracyDef b in DefDatabase<BureaucracyDef>.AllDefsListForReading)
                    foreach (Def d in b.Unlocks)
                        gates[d] = b;
            }
            return def != null && gates.TryGetValue(def, out BureaucracyDef g) ? g : null;
        }

        /// <summary>A named capability granted by a finished project (always on without a bureaucracy system).</summary>
        public static bool Feature(string name)
        {
            BureaucracySystem bur = PrisonGame.Instance?.bureaucracy;
            if (bur == null) return false;
            foreach (BureaucracyDef d in bur.done)
                if (d.features.Contains(name)) return true;
            return false;
        }

        public static bool IsUnlocked(Def def)
        {
            BureaucracyDef gate = GateOf(def);
            return gate == null || PrisonGame.Instance?.bureaucracy == null || PrisonGame.Instance.bureaucracy.IsDone(gate);
        }
    }

    public class BureaucracySystem : IExposable
    {
        public List<BureaucracyDef> done = new List<BureaucracyDef>();
        public BureaucracyDef active;
        public float progress;

        public bool IsDone(BureaucracyDef def) => done.Contains(def);

        /// <summary>Whether this administrator could do paperwork: not dead, not suspended, and not incapable of the work.</summary>
        private static bool CanDoPaperwork(Pawn p) =>
            p is { Dead: false } && !p.WorkTypeIsDisabled(RPAdminDefOf.RP_Administration) && !(p.GetComp<CompStaff>() is { } c && c.suspendedUntilTick > Find.TickManager.TicksGame);

        private static IEnumerable<Pawn> AdministratorsOf(Map map) =>
            StaffCommute.AllStaff(map).Where(p => p.GetComp<CompStaff>()?.role?.workTypes.Contains(RPAdminDefOf.RP_Administration) == true && CanDoPaperwork(p));

        /// <summary>Administrators on the staff who could work (at home between shifts too: they come back for the next; a suspended one or one who cannot do the work does not count) and the office desks they work at. Paperwork needs both.</summary>
        public static int Administrators(Map map) => AdministratorsOf(map).Count();

        /// <summary>
        /// The office desks somebody could sit at: not forbidden or burning, and reachable by an administrator who is on the map (when none is, between shifts, a desk is counted as long as it
        /// is open).
        /// </summary>
        public static int Desks(Map map)
        {
            List<Pawn> onMap = AdministratorsOf(map).Where(p => p.Spawned && p.Map == map).ToList();
            int n = 0;
            foreach (Building desk in map.listerBuildings.AllBuildingsColonistOfDef(RPAdminDefOf.RP_OfficeDesk))
            {
                if (desk.IsForbidden(Faction.OfPlayer) || desk.IsBurning()) continue;
                if (onMap.Count > 0 && !onMap.Any(p => p.CanReach(desk, PathEndMode.InteractionCell, Danger.Some))) continue;
                n++;
            }
            return n;
        }

        /// <summary>Nobody could do the paperwork: no administrator on the staff, or no office desk to work at.</summary>
        public static bool Stalled(Map map) => Administrators(map) == 0 || Desks(map) == 0;

        /// <summary>Why a project started now would make no progress (null: somebody could work on it).</summary>
        public static string StalledReason(Map map) => StalledReason(Administrators(map), Desks(map));

        public static string StalledReason(int administrators, int desks)
        {
            bool noAdmin = administrators == 0, noDesk = desks == 0;
            return noAdmin && noDesk ? "RP_BureaucracyStalledBoth".Translate().Resolve() : noAdmin ? "RP_BureaucracyStalledNoAdmin".Translate().Resolve() : noDesk ? "RP_BureaucracyStalledNoDesk".Translate().Resolve() : null;
        }

        public bool Available(BureaucracyDef def) => !IsDone(def) && def != active && def.prerequisites.All(IsDone);

        public IEnumerable<BureaucracyDef> All => DefDatabase<BureaucracyDef>.AllDefsListForReading.OrderBy(d => d.uiOrder);

        /// <summary>Pays and makes the project active. The money of a project switched away from is not refunded.</summary>
        public bool TryStart(BureaucracyDef def)
        {
            if (active != null || !Available(def)) return false;
            if (!PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Bureaucracy, def.cost, "RP_BureaucracyNote".Translate(def.label)))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(def.cost)), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            active = def;
            progress = 0f;
            Concepts.Demonstrated(Concepts.Bureaucracy);
            return true;
        }

        public void AddWork(float hours)
        {
            if (active == null) return;
            progress += hours;
            if (progress < active.work) return;
            BureaucracyDef finished = active;
            done.Add(finished);
            active = null;
            progress = 0f;
            Find.LetterStack.ReceiveLetter("RP_BureaucracyDoneLabel".Translate(), "RP_BureaucracyDoneText".Translate(finished.label, string.Join(", ", finished.Unlocks.Select(d => d.label).Concat(finished.features.Select(f => ("RP_Feature_" + f).Translate().ToString())))), LetterDefOf.PositiveEvent);
            if (finished.features.Contains("RiotGear"))
                foreach (Map map in Find.Maps)
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                        StaffUtility.IssueGear(p);
        }

        /// <summary>The finished project the state would take back: the latest, and not one that another finished project, or the one in progress, builds on (null: none to take).</summary>
        public BureaucracyDef NextToLose()
        {
            for (int i = done.Count - 1; i >= 0; i--)
                if (!done.Any(d => d != done[i] && d.prerequisites.Contains(done[i])) && !(active != null && active.prerequisites.Contains(done[i]))) return done[i];
            return null;
        }

        /// <summary>The bureaucracy loses a project (see <see cref="NextToLose"/>): its unlocks go until it is researched again.</summary>
        public BureaucracyDef LoseLatest()
        {
            BureaucracyDef lost = NextToLose();
            if (lost != null) done.Remove(lost);
            return lost;
        }

        public void UnlockAll()
        {
            foreach (BureaucracyDef def in DefDatabase<BureaucracyDef>.AllDefsListForReading)
                if (!done.Contains(def)) done.Add(def);
            active = null;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref done, "done", LookMode.Def);
            Scribe_Defs.Look(ref active, "active");
            Scribe_Values.Look(ref progress, "progress");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                done ??= new List<BureaucracyDef>();
                done.RemoveAll(d => d == null);
            }
        }
    }

    /// <summary>Administrators do paperwork at office desks while a project is active.</summary>
    public class WorkGiver_Paperwork : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(RPAdminDefOf.RP_OfficeDesk);

        public override PathEndMode PathEndMode => PathEndMode.InteractionCell;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => PrisonGame.Instance?.bureaucracy?.active == null;

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t.Faction == pawn.Faction && !t.IsBurning() && !BoardsDesk(pawn, t) && pawn.CanReserveAndReach(t, PathEndMode.InteractionCell, Danger.Some, 1, -1, null, forced);

        /// <summary>The hearing room's desk is the board's while an inmate is on their way to it or before it (paperwork there would hold it for two hours).</summary>
        private static bool BoardsDesk(Pawn pawn, Thing desk) =>
            PrisonGame.Instance is { } game && ParoleScene.BoardNeeded(game) && PrisonMap.Of(pawn.Map)?.zones.ZoneAt(desk.Position) == RPParoleDefOf.RP_HearingRoom;

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPAdminDefOf.RP_Paperwork, t);
    }

    public class JobDriver_Paperwork : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => PrisonGame.Instance.bureaucracy.active == null);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            Toil work = Toils_General.Wait(GenDate.TicksPerHour * 2);
            work.handlingFacing = true;
            work.WithEffect(EffecterDefOf.Research, TargetIndex.A);
            work.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(job.targetA);
                // An average administrator (Intellectual 8) does one admin-hour per hour.
                float skill = pawn.skills?.GetSkill(SkillDefOf.Intellectual)?.Level ?? 5;
                PrisonGame.Instance.bureaucracy.AddWork(delta / (float)GenDate.TicksPerHour * Mathf.Lerp(0.5f, 1.75f, skill / 20f));
                pawn.skills?.Learn(SkillDefOf.Intellectual, 0.05f * delta);
            };
            yield return work;
        }
    }

    [DefOf]
    public static class RPAdminDefOf
    {
        public static ThingDef RP_OfficeDesk;
        public static JobDef RP_Paperwork;
        public static WorkTypeDef RP_Administration;

        static RPAdminDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPAdminDefOf));
    }

    /// <summary>Locked buildings are not offered in the architect menu.</summary>
    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.Visible), MethodType.Getter)]
    public static class Patch_Designator_Build_Visible
    {
        public static void Postfix(Designator_Build __instance, ref bool __result)
        {
            if (__result && !DebugSettings.godMode && __instance.PlacingDef is ThingDef td && !Unlocks.IsUnlocked(td)) __result = false;
        }
    }
}
