using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks from stability round 4 (see WORKLOG): bugs found while looking for the cause of a flake or a failing run, each with the check that fails without its fix.
    /// Each runs in its own <see cref="Guarded"/> scope from <c>FireAllEvents</c>.
    /// </summary>
    public static partial class AutoTest
    {
        // ---- the fine menu and the cooks ------------------------------------------------------------------------------------------------------------

        /// <summary>The prison's own meal bills on the kitchen's stoves: (stove, bill) for each, with the recipe each is for.</summary>
        private static List<(Building stove, Bill_Production bill)> OwnMealBills(Map map, PrisonMap pm) =>
            InmateJobWorker_Kitchen.KitchenStoves(map, pm)
                .SelectMany(b => b is IBillGiver giver ? giver.BillStack.Bills.OfType<Bill_Production>().Where(x => pm.cookBills.ContainsKey(x.GetUniqueLoadID())).Select(x => (b, x)) : Enumerable.Empty<(Building, Bill_Production)>())
                .ToList();

        /// <summary>
        /// The fine menu with nobody on the staff who can cook it (Cooking 6): the stoves' bills stay simple meals (the old swap put the fine recipe on every stove, nobody did it, no meal was cooked for
        /// three days in the finemeals plan), and the warden is told. A cook who can (here one off shift, at home: the staff is the prison's, not whoever stands on the map) puts them on fine meals at
        /// the next hourly pass, and the last such cook leaving puts them back on simple ones.
        /// </summary>
        private static void CheckFineMenuFollowsTheCooks(Map map)
        {
            Guarded("kitchen: the fine menu follows the cooks", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                KitchenBills.Ensure(map);
                if (OwnMealBills(map, pm).Count == 0)
                {
                    problems0.Add("kitchen fine menu: no meal bill of the prison's own on a kitchen stove to check");
                    return;
                }
                RecipeDef fine = KitchenMenuPolicy.Recipe(KitchenMenu.Fine), fineBulk = KitchenMenuPolicy.Recipe(KitchenMenu.Fine, bulk: true);
                bool IsFine(RecipeDef x) => x == fine || x == fineBulk;
                KitchenMenu menu0 = g.kitchenMenu;
                r.Add(() =>
                {
                    g.kitchenMenu = menu0;
                    KitchenMenuPolicy.Apply(map, menu0);
                });
                // Every cook of the staff too weak for the fine meal (put back afterwards).
                foreach (Pawn cook in KitchenBills.Cooks(map).ToList())
                {
                    SkillRecord cooking = cook.skills.GetSkill(SkillDefOf.Cooking);
                    int level0 = cooking.Level;
                    r.Add(() => cooking.Level = level0);
                    cooking.Level = 3;
                }
                g.kitchenMenu = KitchenMenu.Simple;
                KitchenMenuPolicy.Apply(map, KitchenMenu.Simple);
                string told = "RP_KitchenFineNoCookNow".Translate(fine.skillRequirements?.FirstOrDefault()?.minLevel ?? 6).ToString().CapitalizeFirst();
                LiveMessages().RemoveAll(m => m.text == told);
                g.kitchenMenu = KitchenMenu.Fine;
                int swapped = KitchenMenuPolicy.Apply(map, KitchenMenu.Fine);
                int fineBills = OwnMealBills(map, pm).Count(x => IsFine(x.bill.recipe));
                if (swapped != 0 || fineBills != 0) FineMenuFail($"the fine menu with nobody who can cook it: {swapped} bills swapped, {fineBills} of the kitchen's bills are for fine meals (0 expected: nobody would do them)");
                if (!LiveMessages().Any(m => m.text == told)) FineMenuFail("the fine menu with nobody who can cook it: the warden was told nothing");
                // A cook who can, off shift: the hourly pass puts the bills on fine meals.
                StaffRoleDef cookRole = DefDatabase<StaffRoleDef>.GetNamed("RP_Cook");
                Pawn capable = ThrowawayStaff(map, r, cookRole, atHome: true);
                capable.skills.GetSkill(SkillDefOf.Cooking).Level = 10;
                KitchenBills.Ensure(map);
                List<(Building stove, Bill_Production bill)> own = OwnMealBills(map, pm);
                if (own.Count == 0 || own.Any(x => !IsFine(x.bill.recipe))) FineMenuFail($"a cook at Cooking 10 (off shift) joined the staff and the prison's bills are {string.Join(", ", own.Select(x => x.bill.recipe.defName))} (all fine meals expected)");
                // The last one who can leaves: back to simple meals.
                g.home.Remove(capable);
                if (!capable.Destroyed) capable.Destroy(DestroyMode.Vanish);
                KitchenBills.Ensure(map);
                own = OwnMealBills(map, pm);
                if (own.Count == 0 || own.Any(x => IsFine(x.bill.recipe))) FineMenuFail($"the only cook who could cook fine meals left and the prison's bills are {string.Join(", ", own.Select(x => x.bill.recipe.defName))} (simple meals expected)");
                RPLog.Message($"[RimPrison:test] kitchen fine menu: no cook -> {swapped} swapped, cook at home -> fine, cook gone -> simple");
            });
        }

        private static void FineMenuFail(string what) => problems0.Add("kitchen fine menu: " + what);

        /// <summary>
        /// For a check of the swap itself (the settings of a bill that goes to the fine meal and back): one of the staff's cooks is raised to Cooking 8 when nobody can cook the fine meal, as the swap
        /// is refused otherwise (see <see cref="CheckFineMenuFollowsTheCooks"/>). Returns what puts the skill back.
        /// </summary>
        private static System.Action TrainACookForTheFineMeal(Map map)
        {
            RecipeDef fine = KitchenMenuPolicy.Recipe(KitchenMenu.Fine);
            if (KitchenBills.SomeoneCanCook(KitchenBills.Cooks(map), fine) || KitchenBills.Cooks(map).FirstOrDefault() is not { } cook) return () => { };
            SkillRecord cooking = cook.skills.GetSkill(SkillDefOf.Cooking);
            int level0 = cooking.Level;
            cooking.Level = 8;
            return () => cooking.Level = level0;
        }

        // ---- the jobs that feed and carry --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A cook on the way with a meal for an inmate, a guard feeding a downed one by hand, a guard with a wounded one on their shoulder: a class that calls its teacher, the end of the shift and a staff break leave them alone (the jobs are
        /// not casually interruptible; they were, and the meal was dropped where the cook stood and the inmate lay where they fell: the starving of the evening and the night).
        /// </summary>
        private static void CheckCarryingJobsAreNotCutShort(Map map)
        {
            Guarded("jobs: carrying a meal or a wounded inmate is not cut short", map, r =>
            {
                Pawn cook = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed("RP_Cook"));
                Job saved = cook.jobs.curJob;
                r.Add(() => cook.jobs.curJob = saved);
                foreach (JobDef def in new[] { JobDefOf.DeliverFood, JobDefOf.FeedPatient, JobDefOf.TakeWoundedPrisonerToBed, JobDefOf.EscortPrisonerToBed })
                {
                    cook.jobs.curJob = JobMaker.MakeJob(def);
                    bool teacherCalled = ProgramSystem.CanInterrupt(cook), staffRoom = StaffBreaks.Busy(cook, map);
                    if (def.casualInterruptible || teacherCalled || !staffRoom) problems0.Add($"jobs: {def.defName} can be cut short (casually interruptible {def.casualInterruptible}, a class may call the teacher off it {teacherCalled}, a break leaves it alone {staffRoom})");
                }
                cook.jobs.curJob = null;
                if (!ProgramSystem.CanInterrupt(cook)) problems0.Add("jobs: a cook with no job cannot be called to teach (the control of the check)");
            });
        }

        // ---- the beds a vanilla search offers an inmate --------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Vanilla carries a downed inmate to the closest bed a guard can reach and the inmate takes it as theirs: it knows no wings, and the guard goes through the supermax unit's staff-only door. A man
        /// of one level (a protective custody inmate, one on death row) woke up in a supermax cell and starved behind its door. The search (<c>RestUtility.IsValidBedFor</c>) offers an inmate their own bed, a medical
        /// bed, a bed of their level's wing (or of no wing, but not for a level that keeps to its wing), and a solitary bed only when a punishment is due. The same bed in the same room, painted as each wing in turn.
        /// </summary>
        private static void CheckVanillaBedSearchKeepsInmatesToTheirWing(Map map)
        {
            Guarded("beds: a vanilla bed search keeps an inmate to their wing", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Building_Bed spare = null;
                Room room = null;
                foreach (Pawn q in map.mapPawns.PrisonersOfColonySpawned.ToList())
                    if (q.ownership?.OwnedBed is { } own && !own.Medical && !PunishmentUtility.IsSolitaryBed(own) && own.GetRoom() is { IsPrisonCell: true } rm && (spare = InsideSpareBed(map, rm, r)) != null)
                    {
                        room = rm;
                        break;
                    }
                if (spare == null)
                {
                    RPLog.Message("[RimPrison:test] beds: a vanilla bed search keeps an inmate to their wing: no cell with room for a spare bed, not checked");
                    return;
                }
                PrisonZoneDef Wing(string name) => DefDatabase<PrisonZoneDef>.GetNamed(name);
                SecurityLevelDef medium = DefDatabase<SecurityLevelDef>.GetNamed("RP_Medium"), protective = DefDatabase<SecurityLevelDef>.GetNamed("RP_Protective");
                Pawn man = Throwaway(map, r, medium), safe = Throwaway(map, r, protective);
                bool Valid(Pawn who) => RestUtility.IsValidBedFor(spare, who, guard, checkSocialProperness: false, allowMedBedEvenIfSetToNoCare: false, ignoreOtherReservations: false, GuestStatus.Prisoner);
                void Paint(PrisonZoneDef zone) => InsideZone(pm, r, zone, room.Cells);
                var answers = new List<string>();
                void Expect(string what, bool got, bool want)
                {
                    answers.Add($"{what}={got}");
                    if (got != want) problems0.Add($"beds: a vanilla bed search for {what}: offered {got}, {want} expected (a bed in a wing that is not theirs, or one of theirs refused)");
                }
                Paint(Wing("RP_WingMedium"));
                if (!Valid(man))
                {
                    problems0.Add("beds: the control of the vanilla bed search check failed: a spare bed in a medium security wing is not valid for a medium security inmate (set-up)");
                    return;
                }
                Paint(Wing("RP_WingMaximum"));
                Expect("a medium security inmate, bed in the maximum wing", Valid(man), false);
                Paint(Wing("RP_WingSupermax"));
                Expect("a medium security inmate, bed in a supermax cell", Valid(man), false);
                Paint(RPSecurityDefOf.RP_SolitaryZone);
                Expect("a medium security inmate, solitary bed, no punishment due", Valid(man), false);
                man.Inmate().pendingPunishmentHours = 4;
                Expect("a medium security inmate, solitary bed, a punishment due", Valid(man), true);
                man.Inmate().pendingPunishmentHours = 0;
                Paint(null);
                Expect("a medium security inmate, bed in no wing", Valid(man), true);
                Expect("a protective custody inmate, bed in no wing", Valid(safe), false);
                Paint(Wing("RP_WingMedium"));
                Expect("a protective custody inmate, bed in the medium wing", Valid(safe), false);
                Paint(Wing("RP_WingProtective"));
                Expect("a protective custody inmate, bed in their own wing", Valid(safe), true);
                // Their own bed, wherever it is (a level changed under them, a wing repainted): theirs is always theirs.
                Paint(Wing("RP_WingMaximum"));
                man.ownership.ClaimBedIfNonMedical(spare);
                Expect("a medium security inmate, their own bed in the maximum wing", Valid(man), true);
                RPLog.Message($"[RimPrison:test] beds: a vanilla bed search: {string.Join(", ", answers)}");
            });
        }

        // ---- who the work scan stops at -----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// For a pawn the work scan gives nothing, in the order the scan takes the givers: each scanning giver with a thing it says it has a job on (<c>HasJobOnThing</c>) and no job to give for it
        /// (<c>JobOnThing</c> is null). The scan keeps such a thing as its target, takes the giver's next priority for the end of the search and gives the pawn nothing: vanilla's "CanGiveJob and JobOnX
        /// may not be synchronized". (A diagnostic: the hungry line of the logs.)
        /// </summary>
        private static string WorkScanDesync(Pawn pawn)
        {
            var found = new List<string>();
            int considered = 0;
            foreach (WorkGiver giver in pawn.workSettings.WorkGiversInOrderNormal)
            {
                if (giver is not WorkGiver_Scanner scanner || !scanner.def.scanThings || giver.ShouldSkip(pawn) || pawn.WorkTypeIsDisabled(giver.def.workType) || giver.MissingRequiredCapacity(pawn) != null) continue;
                considered++;
                IEnumerable<Thing> things = scanner.PotentialWorkThingsGlobal(pawn) ?? pawn.Map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
                foreach (Thing t in things.ToList())
                {
                    if (t.IsForbidden(pawn) || !scanner.HasJobOnThing(pawn, t)) continue;
                    Job job = scanner.JobOnThing(pawn, t);
                    found.Add($"{giver.def.defName}({giver.def.priorityInType}) on {t.LabelShort}: {job?.def.defName ?? "null"}{(job != null && !pawn.CanReach(t, scanner.PathEndMode, Danger.Deadly) ? " unreachable" : "")}");
                    break;
                }
                if (found.Count > 0 && !found[found.Count - 1].Contains(": null")) break;
            }
            return $"scanners={considered} " + (found.Count == 0 ? "targets=none" : "targets=" + string.Join(", ", found));
        }

        // ---- the showcase's bedless inmates -----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// How many hourly looks in a row an inmate has had no bed while a bed of their level and sex was free (the showcase plan), and how many make it a fault: two hourly passes of the product have had their chance to give
        /// them one. An inmate with no such bed free is not counted here (<see cref="showcaseBedlessStalled"/>): <see cref="CompInmate.TryReassignBed"/> has nothing to give them.
        /// </summary>
        private static readonly Dictionary<int, int> showcaseBedlessLooks = new Dictionary<int, int>();
        private const int BedlessLooksToCount = 3;

        /// <summary>How many hourly looks in a row an inmate has had no bed with none free for them (beds destroyed in a riot, not yet rebuilt), and how many make it a rebuild that never came.</summary>
        private static readonly Dictionary<int, int> showcaseBedlessStalled = new Dictionary<int, int>();
        private const int StalledLooksToFail = 24;

        /// <summary>Every hour of the showcase run: who has no bed (not one in the hole, in the infirmary or being punished: those are not meant to have their own), and for how long running.</summary>
        private static void ShowcaseBedlessWatch(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = p.Inmate();
                if (c == null || p.guest.Released) continue;
                bool bedless = p.ownership?.OwnedBed == null && c.forcedActivity == null && !(p.InBed() && p.CurrentBed() is { Medical: true });
                // (TryReassignBed's own condition: a free bed of the inmate's level and sex.)
                bool couldHave = bedless && IntakeSystem.FreeBedCount(map, c.security, gender: p.gender) > 0;
                showcaseBedlessLooks[p.thingIDNumber] = couldHave ? (showcaseBedlessLooks.TryGetValue(p.thingIDNumber, out int n) ? n + 1 : 1) : 0;
                showcaseBedlessStalled[p.thingIDNumber] = bedless && !couldHave ? (showcaseBedlessStalled.TryGetValue(p.thingIDNumber, out int s) ? s + 1 : 1) : 0;
            }
        }
    }
}

namespace RimPrison
{
    /// <summary>Autotest: an inmate takes a bed in the wing of another level (and by what job), the way a protective inmate came to sleep in a supermax cell in the showcase runs.</summary>
    [HarmonyPatch(typeof(Pawn_Ownership), nameof(Pawn_Ownership.ClaimBedIfNonMedical))]
    [TestOnly]
    public static class Patch_Pawn_Ownership_ClaimBed_LogOtherWing
    {
        public static void Prefix(Building_Bed newBed, Pawn ___pawn)
        {
            if (!AutoTest.Active || newBed == null || ___pawn?.Inmate() is not { activeFlag: true, security: { } level } || !newBed.Spawned) return;
            SecurityLevelDef wing = IntakeSystem.WingOf(newBed);
            if (wing == null || wing == level || PunishmentUtility.IsSolitaryBed(newBed)) return;
            Pawn p = ___pawn;
            if (p.ownership?.OwnedBed == newBed) return; // (the bed they already have, claimed again by the job that lays them in it)
            // (Who is carrying them, and from which giver: the first toil of a carry claims the bed, so the carrier's job is the carry then.)
            Pawn carrier = p.MapHeld?.mapPawns.FreeColonistsSpawned.FirstOrDefault(g => g.CurJob is { } cj && cj.targetA.Thing == p && cj.targetB.Thing == newBed);
            string carriedBy = carrier == null ? "" : $", carried by {carrier.LabelShort} ({carrier.CurJobDef?.defName} from {carrier.CurJob?.workGiverDef?.defName})";
            string trace = string.Join(" < ", new System.Diagnostics.StackTrace().GetFrames().Skip(2).Take(7).Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name));
            RPLog.Message($"[RimPrison:wingbed] {p.LabelShort} ({level.defName}) takes a bed of the {wing.defName} wing at {newBed.Position - PrisonBuilder.Origin(newBed.Map)}: job {p.CurJobDef?.defName} from {p.CurJob?.jobGiver?.GetType().Name} ({p.CurJob?.jobGiver?.ToString()}), downed {p.Downed}, medical rest {HealthAIUtility.ShouldSeekMedicalRest(p)}, forced {p.Inmate().forcedActivity?.defName}{carriedBy}, day {GenDate.DaysPassed} h{GenLocalDate.HourOfDay(p.MapHeld ?? Find.CurrentMap)}: {trace}");
        }
    }
}
