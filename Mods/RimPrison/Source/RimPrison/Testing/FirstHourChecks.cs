using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks for the first-hour work (gap analysis 4, sections 3 and 2): what a new player meets before the prison runs, and the deep systems made to land. Each is called from FireAllEvents,
    /// works on throwaway things and puts back what it touches (<see cref="Rollback"/>); the layout of the starter facility is checked once at the start of every plan.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The index of a choice of a decision letter, by its key (the choices of a letter differ with what it offers, and a new one must not move the others a test answers).</summary>
        private static int LetterOption(ChoiceLetter_PrisonDecision letter, string key)
        {
            int at = letter.decision.Worker.Options(letter).ToList().IndexOf(key);
            if (at < 0) throw new InvalidOperationException($"the letter offers no {key}");
            return at;
        }

        private static void FirstHourFail(string text) => problems0.Add("first hour: " + text);

        /// <summary>RIMPRISON_SKIP=kitchen,problems,... leaves named checks of this file out of a run (to find which one a failure comes from).</summary>
        private static bool SkipFirstHour(string name) => Environment.GetEnvironmentVariable("RIMPRISON_SKIP")?.Split(',').Contains(name) == true;

        private static void CheckFirstHour(Map map)
        {
            var checks = new (string name, Action<Map> run)[]
            {
                ("kitchen", CheckFirstHourKitchenBills), ("problems", CheckFirstHourProblemList), ("small", CheckFirstHourSmallThings), ("difficulty", CheckFirstHourDifficulty),
                ("concepts", CheckFirstHourConcepts), ("morgue", CheckFirstHourMorgue), ("letters", CheckFirstHourLetters), ("court", CheckFirstHourCourtPace), ("intel", CheckFirstHourIntel),
                ("carried", CheckFirstHourCarriedInmateRoomTension), ("fixes", CheckFirstHourFixes),
            };
            foreach ((string name, Action<Map> run) in checks)
                if (!SkipFirstHour(name)) run(map);
        }

        // ---- the starter facility ---------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The inherited facility reads as a prison that works (3.4, 3.6, 3.7): an office with a desk, a morgue, two solitary cells off holding, a chapel with seats, no beds in the staff room;
        /// the scenario names an administrator as the sixth of its staff and unlocks the library and the chaplaincy it paints; the new facility keeps the janitor. Run at the start of every plan.
        /// </summary>
        private static void CheckStarterLayout(Map map)
        {
            if (PrisonBuilder.Kind != Facility.Inherited) return;
            PrisonMap pm = PrisonMap.Of(map);
            int start = problems0.Count;
            ScenPart_PrisonStart inherited = DefDatabase<ScenarioDef>.GetNamed("RP_InheritedPrison").scenario.AllParts.OfType<ScenPart_PrisonStart>().First();
            ScenPart_PrisonStart fresh = DefDatabase<ScenarioDef>.GetNamed("RP_NewPrison").scenario.AllParts.OfType<ScenPart_PrisonStart>().First();
            // (Seven staff: the administrator is added to the six and nobody is taken away, so the janitor stays: nobody else cleans, and the kitchen is a staff-only room.)
            if (inherited.seventhRole != DefDatabase<StaffRoleDef>.GetNamed("RP_Administrator") || inherited.sixthRole != null) FirstHourFail($"the inherited facility's seventh starting role is {inherited.seventhRole?.defName} (sixth {inherited.sixthRole?.defName}), not an administrator on top of the janitor");
            if (fresh.seventhRole != null || fresh.sixthRole != null) FirstHourFail($"the new facility's extra starting roles are {fresh.sixthRole?.defName}/{fresh.seventhRole?.defName}: it has no office to put an administrator in");
            int inheritedPawns = DefDatabase<ScenarioDef>.GetNamed("RP_InheritedPrison").scenario.AllParts.OfType<ScenPart_ConfigPage_ConfigureStartingPawns>().First().pawnCount;
            int freshPawns = DefDatabase<ScenarioDef>.GetNamed("RP_NewPrison").scenario.AllParts.OfType<ScenPart_ConfigPage_ConfigureStartingPawns>().First().pawnCount;
            if (inheritedPawns != 7 || freshPawns != 6) FirstHourFail($"the inherited facility starts with {inheritedPawns} staff and the new one with {freshPawns}, not seven and six");
            if (StaffCommute.AllStaff(map).All(p => p.GetComp<CompStaff>().role?.defName != "RP_Janitor")) FirstHourFail("no janitor among the staff at the start (nobody cleans the kitchen)");
            foreach (string project in new[] { "RP_Bur_Library", "RP_Bur_Chaplaincy" })
                if (!inherited.unlocked.Contains(DefDatabase<BureaucracyDef>.GetNamed(project))) FirstHourFail($"the inherited facility paints a room that {project} unlocks and does not unlock it");
            if (StaffCommute.AllStaff(map).All(p => p.GetComp<CompStaff>().role?.defName != "RP_Administrator")) FirstHourFail("no administrator among the staff at the start");
            if (BureaucracySystem.Desks(map) < 1) FirstHourFail("no office desk at the start of the inherited facility");
            IntVec3 desk = map.listerBuildings.AllBuildingsColonistOfDef(RPAdminDefOf.RP_OfficeDesk).FirstOrDefault()?.Position ?? IntVec3.Invalid;
            if (desk.IsValid && pm.zones.ZoneAt(desk) != DefDatabase<PrisonZoneDef>.GetNamed("RP_StaffOnly")) FirstHourFail($"the office desk is not in a staff-only room ({pm.zones.ZoneAt(desk)?.defName})");
            if (pm.zones.CellsOf(RPMorgueDefOf.RP_Morgue).Count < 6) FirstHourFail($"the morgue has {pm.zones.CellsOf(RPMorgueDefOf.RP_Morgue).Count} cells");
            int solitary = pm.PrisonerBeds.Count(b => b.Spawned && PunishmentUtility.IsSolitaryBed(b));
            if (solitary != 2) FirstHourFail($"{solitary} solitary beds in the inherited facility, expected two");
            foreach (Building_Bed bed in pm.PrisonerBeds.Where(b => b.Spawned && PunishmentUtility.IsSolitaryBed(b)))
                if (pm.zones.ZoneAt(bed.Position) != RPSecurityDefOf.RP_SolitaryZone || !holdingSideReachable(bed)) FirstHourFail($"a solitary cell at {bed.Position} is not painted solitary or is not reached from holding");
            if (IntakeSystem.Capacity(map) != PrisonBuilder.Cells) FirstHourFail($"the inherited facility has {IntakeSystem.Capacity(map)} cell beds for {PrisonBuilder.Cells} cells (the solitary cells are not among them)");
            List<IntVec3> chapel = pm.zones.CellsOf(RPFaithDefOf.RP_Chapel);
            int seats = chapel.Count(c => c.GetEdifice(map) is { def.building.isSittable: true });
            if (chapel.Count < 20 || seats < 4) FirstHourFail($"the chapel has {chapel.Count} cells and {seats} seats");
            if (map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().Any(b => pm.zones.ZoneAt(b.Position) == RPStaffRoomDefOf.RP_StaffRoom)) FirstHourFail("the staff room has a bed in it (staff go home to sleep)");
            if (!pm.zones.Any(RPDefOf.RP_Holding) || !pm.zones.Any(RPLaborDefOf.RP_Kitchen) || !KitchenBillsOnStarterStoves(map, pm)) FirstHourFail("the starter's holding or kitchen is missing, or a starter stove has no meal bill, or two");
            if (problems0.Count == start) RPLog.Message($"[RimPrison:test] first hour: starter layout - an office desk, {pm.zones.CellsOf(RPMorgueDefOf.RP_Morgue).Count} morgue cells, {solitary} solitary cells, a chapel of {chapel.Count} cells with {seats} seats, administrator and unlocks as scripted");

            bool holdingSideReachable(Building_Bed bed)
            {
                List<IntVec3> holding = pm.zones.CellsOf(RPDefOf.RP_Holding);
                return holding.Count > 0 && holding.Any(h => map.reachability.CanReach(h, bed.Position, PathEndMode.Touch, TraverseParms.For(TraverseMode.PassDoors)));
            }
        }

        private static bool KitchenBillsOnStarterStoves(Map map, PrisonMap pm)
        {
            foreach (Building stove in InmateJobWorker_Kitchen.KitchenStoves(map, pm))
                if (stove is IBillGiver g && g.BillStack.Bills.OfType<Bill_Production>().Count(b => b.repeatMode == BillRepeatModeDefOf.TargetCount && b.targetCount >= KitchenBills.BaseMeals) != 1) return false;
            return true;
        }

        // ---- 3.1: a stove in a kitchen zone cooks, whoever built it ------------------------------------------------------------------------

        /// <summary>
        /// A stove put up in the kitchen zone gets a standing target-count meal bill scaled to the cell beds; one standing outside it gets it once the zone is painted under it; a bill the
        /// warden deleted is not made again and one they edited keeps its target; the starter's own (<see cref="PrisonBuilder.AddCookBill"/>) is the game's.
        /// </summary>
        private static void CheckFirstHourKitchenBills(Map map)
        {
            Guarded("first hour 3.1: stoves of the kitchen get a meal bill", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                ThingDef stoveDef = DefDatabase<ThingDef>.GetNamed("FueledStove");
                int beds = IntakeSystem.Capacity(map);
                // (What the prison remembers of the kitchen's own stoves and bills, and the targets of those bills, is put back: a check must leave the cooks' work as it found it.)
                List<int> billed0 = pm.billedStoves.ToList();
                Dictionary<string, int> made0 = new Dictionary<string, int>(pm.cookBills);
                List<(Bill_Production bill, int target)> targets0 = InmateJobWorker_Kitchen.KitchenStoves(map, pm).SelectMany(b => ((IBillGiver)b).BillStack.Bills.OfType<Bill_Production>()).Select(b => (b, b.targetCount)).ToList();
                r.Add(() =>
                {
                    pm.billedStoves.Clear();
                    pm.billedStoves.AddRange(billed0);
                    pm.cookBills.Clear();
                    foreach (KeyValuePair<string, int> kv in made0) pm.cookBills[kv.Key] = kv.Value;
                    foreach ((Bill_Production bill, int target) in targets0) bill.targetCount = target;
                });
                // The yard: a stove there is nobody's kitchen, so it gets no bill.
                IntVec3 spot = pm.zones.CellsOf(RPDefOf.RP_Yard).FirstOrDefault(c => c.Standable(map) && c.GetThingList(map).Count == 0
                    && GenAdj.OccupiedRect(c, Rot4.North, stoveDef.size).All(x => x.Standable(map) && x.GetThingList(map).Count == 0 && pm.zones.ZoneAt(x) == RPDefOf.RP_Yard));
                if (spot == default)
                {
                    FirstHourFail("no free yard cell for a stove");
                    return;
                }
                var stove = (Building)ThingMaker.MakeThing(stoveDef, GenStuff.DefaultStuffFor(stoveDef));
                stove.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(stove, spot, map, Rot4.North);
                var painted = GenAdj.OccupiedRect(spot, Rot4.North, stoveDef.size).Cells.ToList();
                r.Add(() =>
                {
                    foreach (IntVec3 c in painted) pm.zones.Set(c, RPDefOf.RP_Yard);
                    if (!stove.Destroyed) stove.Destroy(DestroyMode.Vanish);
                    pm.billedStoves.Remove(stove.thingIDNumber);
                });
                var giver = (IBillGiver)stove;
                if (giver.BillStack.Count != 0) FirstHourFail("a stove in the yard got a bill");
                // The kitchen is painted under it: the next pass bills it, for the menu's meal, scaled to the beds.
                foreach (IntVec3 c in painted) pm.zones.Set(c, RPLaborDefOf.RP_Kitchen);
                int made = KitchenBills.Ensure(map);
                if (made < 1 || giver.BillStack.Bills.FirstOrDefault() is not Bill_Production bill)
                {
                    FirstHourFail($"a stove in the kitchen zone got no bill (made {made})");
                    return;
                }
                if (bill.repeatMode != BillRepeatModeDefOf.TargetCount || bill.targetCount != KitchenBills.TargetFor(beds))
                    FirstHourFail($"the stove's bill is {bill.repeatMode} {bill.targetCount}, not a target count of {KitchenBills.TargetFor(beds)} for {beds} beds");
                if (bill.recipe != KitchenBills.RecipeFor(PrisonGame.Instance.kitchenMenu, beds)) FirstHourFail($"the stove's bill is for {bill.recipe?.defName}, not the menu's meal");
                // Billed once: the warden deletes it and it stays deleted; the second pass makes nothing.
                giver.BillStack.Delete(bill);
                if (KitchenBills.Ensure(map) != 0 || giver.BillStack.Count != 0) FirstHourFail("a bill the warden deleted was made again");
                // A stove with no memory of a bill is billed; then the warden edits it and it keeps their target.
                pm.billedStoves.Remove(stove.thingIDNumber);
                KitchenBills.Ensure(map);
                if (giver.BillStack.Bills.FirstOrDefault() is not Bill_Production again) FirstHourFail("a stove with no memory of a bill got none in the kitchen");
                else
                {
                    again.targetCount += 7;
                    int edited = again.targetCount;
                    KitchenBills.Ensure(map);
                    if (again.targetCount != edited) FirstHourFail($"a bill the warden edited was reset to {again.targetCount}");
                    // Left as made, it follows the beds.
                    again.targetCount = KitchenBills.TargetFor(beds) - 4;
                    pm.cookBills[again.GetUniqueLoadID()] = again.targetCount;
                    KitchenBills.Ensure(map);
                    if (again.targetCount != KitchenBills.TargetFor(beds)) FirstHourFail($"a bill left as made did not follow the beds ({again.targetCount}, wanted {KitchenBills.TargetFor(beds)})");
                }
                giver.BillStack.Clear();
                // The hint for hungry inmates names the bill.
                if (!"RP_Problem_Hungry".Translate(2).Resolve().Contains("bill")) FirstHourFail("the hungry inmates' hint does not mention the meal bill");
            });
        }

        // ---- 3.2, 3.11, 3.12: the to-do list ------------------------------------------------------------------------------------------------

        private static void CheckFirstHourProblemList(Map map)
        {
            Guarded("first hour 3.2/3.11/3.12: the list of what to fix", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                // Essentials: holding and the kitchen are in the list of rooms the prison cannot run without; the deliveries zone is not.
                List<PrisonZoneDef> essential = PrisonDiagnostics.EssentialZones().ToList();
                if (!essential.Contains(RPDefOf.RP_Holding) || !essential.Contains(RPLaborDefOf.RP_Kitchen) || essential.Contains(RPDefOf.RP_Deliveries) || !PrisonDiagnostics.IsEssential(RPDefOf.RP_Yard) || PrisonDiagnostics.IsEssential(RPFaithDefOf.RP_Chapel))
                    FirstHourFail($"the essential rooms are {string.Join(", ", essential.Select(z => z.defName))}");
                string Text(PrisonZoneDef z) => "RP_Problem_NoZone".Translate(z.label).Resolve();
                // Without a holding zone the list says so and opens the Rooms screen; without a deliveries zone it says nothing.
                List<IntVec3> holding = pm.zones.CellsOf(RPDefOf.RP_Holding).ToList(), deliveries = pm.zones.CellsOf(RPDefOf.RP_Deliveries).ToList();
                r.Add(() =>
                {
                    foreach (IntVec3 c in holding) pm.zones.Set(c, RPDefOf.RP_Holding);
                    foreach (IntVec3 c in deliveries) pm.zones.Set(c, RPDefOf.RP_Deliveries);
                });
                foreach (IntVec3 c in holding) pm.zones.Set(c, null);
                foreach (IntVec3 c in deliveries) pm.zones.Set(c, null);
                List<PrisonDiagnostics.Item> items = PrisonDiagnostics.Items(map).ToList();
                PrisonDiagnostics.Item holdingItem = items.FirstOrDefault(i => i.text == Text(RPDefOf.RP_Holding));
                if (holdingItem.text == null || holdingItem.tab != MainTabWindow_Prison.Tab.Rooms) FirstHourFail($"no holding zone painted: the list says {(holdingItem.text == null ? "nothing" : "to open " + holdingItem.tab)}");
                if (items.Any(i => i.text == Text(RPDefOf.RP_Deliveries))) FirstHourFail("the list asks for a deliveries zone (the crates land on the pad)");
                if (!Text(RPDefOf.RP_Holding).Contains("Rooms")) FirstHourFail("the missing-zone line does not say where to paint (Rooms)");
                if (PrisonDiagnostics.Problems(map).Count() != items.Count) FirstHourFail("the autotest's list and the screen's list differ");
                // The same list feeds the "prison incomplete" alert.
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
                if (PrisonAlerts.Map != null && !PrisonAlerts.MissingZones.Contains(RPDefOf.RP_Holding.label)) FirstHourFail("the 'prison incomplete' alert does not list holding");
                if (PrisonAlerts.MissingZones.Contains(RPDefOf.RP_Deliveries.label)) FirstHourFail("the 'prison incomplete' alert lists the deliveries zone");
                // A kitchen with no stove in it (the cells under the stoves are taken out of the zone for a moment; nothing is moved).
                List<IntVec3> under = InmateJobWorker_Kitchen.KitchenStoves(map, pm).SelectMany(b => b.OccupiedRect().Cells).Where(c => pm.zones.ZoneAt(c) == RPLaborDefOf.RP_Kitchen).ToList();
                r.Add(() =>
                {
                    foreach (IntVec3 c in under) pm.zones.Set(c, RPLaborDefOf.RP_Kitchen);
                });
                foreach (IntVec3 c in under) pm.zones.Set(c, null);
                if (!PrisonDiagnostics.KitchenWithoutStove(map, pm) || !PrisonDiagnostics.Problems(map).Contains("RP_Problem_NoStove".Translate().Resolve()))
                    FirstHourFail("a kitchen with no stove is not on the list");
            });
        }

        // ---- 3.4, 3.5, 3.8, 3.9, 3.13, 3.15, 3.16, 3.17 --------------------------------------------------------------------------------------

        private static void CheckFirstHourSmallThings(Map map)
        {
            Guarded("first hour: the small things", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                // 3.5: the shuttle's "no beds" alarm needs cells to run out of.
                if (PrisonAlerts.ShuttleWithoutBeds(8, 2, capacity: 0, freeBeds: 0, allowOvercrowding: false)) FirstHourFail("a prison with no cell at all got the 'no beds' alarm before the shuttle");
                if (!PrisonAlerts.ShuttleWithoutBeds(8, 2, capacity: 8, freeBeds: 0, allowOvercrowding: false) || PrisonAlerts.ShuttleWithoutBeds(8, 2, 8, 1, false) || PrisonAlerts.ShuttleWithoutBeds(8, 5, 8, 0, false))
                    FirstHourFail("the 'no beds' alarm is wrong for a full prison, a prison with a bed free, or a shuttle still hours away");
                // 3.4: Start is off while nobody could work on a project (and why).
                if (BureaucracySystem.StalledReason(map) != null) FirstHourFail($"the bureaucracy is said to be stalled with an administrator and a desk on the map: {BureaucracySystem.StalledReason(map)}");
                if (BureaucracySystem.StalledReason(1, 0) == null || !BureaucracySystem.StalledReason(1, 0).Contains("desk") || BureaucracySystem.StalledReason(0, 1) == null || !BureaucracySystem.StalledReason(0, 1).Contains("administrator")
                    || BureaucracySystem.StalledReason(0, 0) == null || BureaucracySystem.StalledReason(2, 3) != null)
                    FirstHourFail("the bureaucracy's reason for a stall is wrong for no desk, no administrator, neither, or both there");
                // 3.8: an empty prison is not graded (the card knows how many inmates it was worked out for).
                ReportCard card = PrisonGrading.Compute(map, g);
                int graded = map.mapPawns.PrisonersOfColonySpawned.Count(p => !ShuttleSystem.OnTheWayOut(p));
                if (card.inmates != graded || card.Graded != (graded > 0) || new ReportCard { inmates = 0 }.Graded) FirstHourFail($"the report card was worked out for {card.inmates} inmates, {graded} are here");
                foreach (string area in new[] { "Safety", "Security", "Health", "Conditions", "Order", "Reform" })
                    if (!("RP_GradeTip_" + area).CanTranslate()) FirstHourFail($"the {area} tile has no tooltip");
                // 3.9: a lockdown with nothing happening, and a shakedown, ask first (the dialogs are only looked at and closed: starting a lockdown interrupts every inmate and moves the doors).
                int lockdown0 = g.lockdownUntilTick, shakedown0 = g.lastShakedownTick;
                if (!PrisonHUD.LockdownNeedsConfirmation(emergencyRunning: false) || PrisonHUD.LockdownNeedsConfirmation(emergencyRunning: true))
                    FirstHourFail("a lockdown with nothing happening does not ask first, or one with a riot or an escape running does");
                PrisonHUD.RequestLockdown(g, emergencyRunning: false);
                Dialog_MessageBox ask = Find.WindowStack.WindowOfType<Dialog_MessageBox>();
                if (ask == null || g.lockdownUntilTick != lockdown0) FirstHourFail($"a lockdown with nothing happening did not ask first (dialog {ask != null}, lockdown started {g.lockdownUntilTick != lockdown0})");
                if (ask != null) Find.WindowStack.TryRemove(ask, doCloseSound: false);
                PrisonHUD.RequestShakedown(g);
                ask = Find.WindowStack.WindowOfType<Dialog_MessageBox>();
                if (ask == null || g.lastShakedownTick != shakedown0 || g.lockdownUntilTick != lockdown0) FirstHourFail("a shakedown did not ask first");
                if (ask != null) Find.WindowStack.TryRemove(ask, doCloseSound: false);
                // 3.13: Programs are on the Policy screen, which is what the alert says.
                if (MainTabWindow_Prison.ButtonFor(MainTabWindow_Prison.Tab.Programs)?.defName != "RP_Policy") FirstHourFail($"the Programs tab is on {MainTabWindow_Prison.ButtonFor(MainTabWindow_Prison.Tab.Programs)?.defName}, not on Policy");
                if (!"RP_Alert_ReferralBlockedDesc".Translate("x").Resolve().Contains("Policy")) FirstHourFail("the referral alert does not say where the Programs screen is");
                // 3.15: armed response is for after the project that brings armed guards.
                BureaucracyDef armed = DefDatabase<BureaucracyDef>.GetNamed("RP_Bur_Armed");
                bool was = g.bureaucracy.done.Contains(armed);
                r.Add(() =>
                {
                    if (was && !g.bureaucracy.done.Contains(armed)) g.bureaucracy.done.Add(armed);
                    if (!was) g.bureaucracy.done.Remove(armed);
                });
                g.bureaucracy.done.Remove(armed);
                if (ArmedResponse.LockedBy != armed) FirstHourFail("armed response is open before armed security is done");
                g.bureaucracy.done.Add(armed);
                if (ArmedResponse.LockedBy != null) FirstHourFail("armed response is locked with armed security done");
                // 3.16: the prison scenarios and the Board are not in vanilla's lists, and a new colony does not start on the Board.
                foreach (string defName in new[] { "RP_NewPrison", "RP_InheritedPrison", "RP_StatePenitentiary" })
                    if (DefDatabase<ScenarioDef>.GetNamed(defName).scenario.showInUI) FirstHourFail($"{defName} is in vanilla's New colony list");
                // (The Board is listed, so the in-game storyteller page draws its difficulty buttons and a warden who picked another storyteller can come back; a new colony's list hides it.)
                StorytellerDef board = RPDefOf.RP_TheBoard;
                if (!board.listVisible) FirstHourFail("the Board is not listed: the in-game storyteller page draws no difficulty buttons for it");
                if (StorytellerList.Hide() || !board.listVisible) FirstHourFail("the Board is hidden from the storyteller list of a running game");
                FieldInfo stateField = AccessTools.Field(typeof(Current), "programStateInt");
                object state0 = stateField.GetValue(null);
                try
                {
                    stateField.SetValue(null, ProgramState.Entry);
                    bool hidden = StorytellerList.Hide();
                    if (!hidden || board.listVisible) FirstHourFail("the Board is in a new colony's storyteller list");
                    StorytellerList.Show(hidden);
                    var page = new Page_SelectStoryteller();
                    page.PreOpen();
                    if (AccessTools.Field(typeof(Page_SelectStoryteller), "storyteller").GetValue(page) == board) FirstHourFail("a new colony preselects the Board");
                    if (!board.listVisible) FirstHourFail("the new colony's page left the Board hidden for the game that follows");
                }
                finally
                {
                    stateField.SetValue(null, state0);
                    board.listVisible = true;
                }
                // 3.17: the missing-zone text names the Rooms button.
                if (!"RP_Problem_NoZone".Translate("yard").Resolve().Contains("Rooms")) FirstHourFail("the missing-zone text does not name Rooms");
                // 3.3: the welcome letter of a handed-over prison does not tell the warden to build a pad, and an empty plot is told what to build.
                string built = PrisonGame.IntroText("Test", 9, Facility.Inherited), empty = PrisonGame.IntroText("Test", 9, null), pen = PrisonGame.IntroText("Test", 9, Facility.Penitentiary);
                string padStep = "RP_IntroSteps_New".Translate(9).Resolve();
                if (built.Contains(padStep.Substring(0, 120)) || !empty.Contains(padStep) || built.Contains("First build a shuttle pad") || empty.Contains("RP_IntroSteps"))
                    FirstHourFail("the welcome letters of the two kinds of start are not told apart");
                if (!empty.Contains("pad") || !empty.Contains("Holding") || !empty.Contains("meal bill") || !empty.Contains("administrator")) FirstHourFail("the new facility's welcome letter lacks its list of what to build");
                if (!built.Contains("administrator") || !built.Contains("Overview")) FirstHourFail("the handed-over prison's welcome letter lacks its next steps");
                // The penitentiary has its own letter: not the small facility's eight cells, six on the payroll and the desk in reception.
                // (Without case: a phrase can open a sentence.)
                string penLower = pen.ToLowerInvariant(), builtLower = built.ToLowerInvariant();
                if (pen == built || penLower.Contains("eight cells") || penLower.Contains("six people") || penLower.Contains("seven people") || penLower.Contains("desk in reception") || !pen.Contains("penitentiary") || !pen.Contains("Overview"))
                    FirstHourFail("the state penitentiary's welcome letter is the inherited facility's or lacks its own steps");
                if (!builtLower.Contains("seven people")) FirstHourFail("the inherited facility's letter does not say seven people are on the payroll");
            });
        }

        // ---- 3.10: what the difficulty changes ------------------------------------------------------------------------------------------------

        private static void CheckFirstHourDifficulty(Map map)
        {
            Guarded("first hour 3.10: the difficulty maps onto prison levers", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Storyteller teller = Find.Storyteller;
                DifficultyDef was = teller.difficultyDef;
                r.Add(() => teller.difficultyDef = was);
                DifficultyDef Def(string name) => DefDatabase<DifficultyDef>.GetNamed(name);
                // The neutral level changes nothing: strive to survive (the dialog's default, every test's) is one of everything.
                PrisonDifficulty.Levers neutral = PrisonDifficulty.LeversOf(Def("Rough"));
                if (neutral.events != 1f || neutral.funds != 1f || neutral.weeklyLimit != 1f || neutral.badGrade != 55f || PrisonDifficulty.StartingFunds(Def("Rough")) != PrisonConstants.StartingFunds)
                    FirstHourFail("the neutral difficulty is not the numbers the prison was tuned with");
                // Easier is kinder on every lever and harder is stricter, in order.
                float lastEvents = 0f, lastFunds = 99f, lastGrade = 0f, lastLimit = 99f;
                foreach (string name in new[] { "Peaceful", "Easy", "Medium", "Rough", "Hard", "Extreme" })
                {
                    PrisonDifficulty.Levers l = PrisonDifficulty.LeversOf(Def(name));
                    if (l.events <= lastEvents || l.funds >= lastFunds || l.badGrade <= lastGrade || l.weeklyLimit >= lastLimit) FirstHourFail($"{name} is not one step stricter than the level before it");
                    (lastEvents, lastFunds, lastGrade, lastLimit) = (l.events, l.funds, l.badGrade, l.weeklyLimit);
                }
                if (PrisonDifficulty.LeversOf(Def("Custom")).badGrade != 55f) FirstHourFail("a custom difficulty is not the neutral level");
                // The levers are read where they act.
                teller.difficultyDef = Def("Peaceful");
                int peacefulLimit = SpecialMeasures.WeeklyLimit(map);
                if (SpecialMeasures.BadGrade != 40f) FirstHourFail($"on peaceful a bad day is below {SpecialMeasures.BadGrade}");
                var oversight = new StateOversight();
                List<float> history0 = g.gradeHistory.ToList();
                r.Add(() =>
                {
                    g.gradeHistory.Clear();
                    g.gradeHistory.AddRange(history0);
                });
                g.gradeHistory.Clear();
                g.gradeHistory.AddRange(new[] { 50f, 50f, 50f });
                bool peacefulTriggers = SpecialMeasures.Trigger(g, map, oversight) == MeasuresCause.Grades;
                float peacefulMean = StorytellerComp_PrisonEvents.MeanDays(BoardEventProps());
                teller.difficultyDef = Def("Extreme");
                int extremeLimit = SpecialMeasures.WeeklyLimit(map);
                bool extremeTriggers = SpecialMeasures.Trigger(g, map, oversight) == MeasuresCause.Grades;
                float extremeMean = StorytellerComp_PrisonEvents.MeanDays(BoardEventProps());
                teller.difficultyDef = Def("Rough");
                bool roughTriggers = SpecialMeasures.Trigger(g, map, oversight) == MeasuresCause.Grades;
                float roughMean = StorytellerComp_PrisonEvents.MeanDays(BoardEventProps());
                if (peacefulTriggers || !roughTriggers || !extremeTriggers) FirstHourFail($"three days graded 50 bring special measures on peaceful {peacefulTriggers}, strive to survive {roughTriggers}, losing is fun {extremeTriggers}");
                if (!(peacefulLimit > SpecialMeasures.WeeklyLimit(map) && SpecialMeasures.WeeklyLimit(map) > extremeLimit)) FirstHourFail($"the week's limit of deaths and escapes is {peacefulLimit} / {SpecialMeasures.WeeklyLimit(map)} / {extremeLimit} (peaceful / strive / extreme)");
                if (!(peacefulMean > roughMean && roughMean > extremeMean) || Mathf.Abs(peacefulMean / roughMean - 1f / 0.6f) > 0.01f) FirstHourFail($"the days between incidents are {peacefulMean:F2} / {roughMean:F2} / {extremeMean:F2} (peaceful / strive / extreme)");
                // The Board uses the scaled comp for its incidents inside the walls.
                if (!DefDatabase<StorytellerDef>.GetNamed("RP_TheBoard").comps.Any(c => c.compClass == typeof(StorytellerComp_PrisonEvents))) FirstHourFail("the Board's incidents do not use the difficulty-scaled comp");
                // The pane says what each level changes, in the prison's own words (not vanilla's raid text).
                foreach (DifficultyDef d in DefDatabase<DifficultyDef>.AllDefsListForReading.Where(d => !d.isCustom))
                {
                    string pane = PrisonDifficulty.PaneText(d);
                    PrisonDifficulty.Levers l = PrisonDifficulty.LeversOf(d);
                    if (pane.Contains(new TaggedString(d.description).Resolve().Substring(0, 30)) || !pane.Contains(Mathf.RoundToInt(l.badGrade).ToString()) || !pane.Contains(Mathf.RoundToInt(l.events * 100f) + "%") || !("RP_Difficulty_" + d.defName).CanTranslate())
                        FirstHourFail($"the difficulty pane for {d.defName} is vanilla's text, or does not say what it changes ({pane.Substring(0, Math.Min(60, pane.Length))}...)");
                }
            });
        }

        private static StorytellerCompProperties_CategoryMTB BoardEventProps() =>
            DefDatabase<StorytellerDef>.GetNamed("RP_TheBoard").comps.OfType<StorytellerCompProperties_CategoryMTB>().First();

        // ---- 2.1: the learning helper's notes ---------------------------------------------------------------------------------------------------

        private static void CheckFirstHourConcepts(Map map)
        {
            Guarded("first hour 2.1: the learning helper teaches the deep systems", map, r =>
            {
                List<ConceptDef> ours = DefDatabase<ConceptDef>.AllDefsListForReading.Where(Concepts.Ours).ToList();
                if (ours.Count < 12 || ours.Count > 16) FirstHourFail($"{ours.Count} notes for the deep systems (a handful: about fourteen)");
                bool adaptive = Prefs.AdaptiveTrainingEnabled;
                r.Add(() => Prefs.AdaptiveTrainingEnabled = adaptive);
                Prefs.AdaptiveTrainingEnabled = true;
                LearningReadout readout = Find.Tutor.learningReadout;
                var knowledge = ours.ToDictionary(d => d, d => PlayerKnowledgeDatabase.GetKnowledge(d));
                r.Add(() =>
                {
                    foreach (ConceptDef d in ours)
                    {
                        PlayerKnowledgeDatabase.SetKnowledge(d, knowledge[d]);
                        readout.Notify_ConceptNewlyLearned(d);
                    }
                    Concepts.Reset();
                });
                foreach (ConceptDef d in ours)
                {
                    PlayerKnowledgeDatabase.SetKnowledge(d, 0f);
                    readout.Notify_ConceptNewlyLearned(d);
                }
                Concepts.Reset();
                // The readout is not hidden any more, and nothing of ours hides it.
                Patches patches = Harmony.GetPatchInfo(AccessTools.Method(typeof(LearningReadout), nameof(LearningReadout.LearningReadoutOnGUI)));
                if (patches != null && patches.Prefixes.Any(p => p.owner == "UrekD.RimPrison")) FirstHourFail("the learning readout is still hidden by a patch of the mod");
                // Vanilla's own notes (caravans, drafting) are not taught in a prison; ours are, once.
                ConceptDef vanilla = DefDatabase<ConceptDef>.GetNamed("Drafting");
                LessonAutoActivator.TeachOpportunity(vanilla, OpportunityType.Important);
                if (readout.IsActive(vanilla)) FirstHourFail("a vanilla note (drafting) was taught in a prison");
                // The places that matter teach their note.
                void Expect(string what, string concept, Action act)
                {
                    ConceptDef d = ConceptDef.Named(concept);
                    act();
                    if (!readout.IsActive(d)) FirstHourFail($"{what} did not teach {concept}");
                }
                PrisonMap pm = PrisonMap.Of(map);
                Expect("painting a holding room", Concepts.Holding, () => Concepts.Painted(RPDefOf.RP_Holding));
                Expect("painting a kitchen", Concepts.Kitchen, () => Concepts.Painted(RPLaborDefOf.RP_Kitchen));
                Expect("painting a hearing room", Concepts.HearingRoom, () => Concepts.Painted(RPParoleDefOf.RP_HearingRoom));
                Expect("painting a morgue", Concepts.Morgue, () => Concepts.Painted(RPMorgueDefOf.RP_Morgue));
                ConceptDef morgue = ConceptDef.Named(Concepts.Morgue), kitchen = ConceptDef.Named(Concepts.Kitchen);
                readout.Notify_ConceptNewlyLearned(morgue);
                PlayerKnowledgeDatabase.SetKnowledge(morgue, 0f);
                Concepts.Reset();
                Expect("the first death", Concepts.Morgue, () => Concepts.Notify_First("death"));
                readout.Notify_ConceptNewlyLearned(kitchen);
                PlayerKnowledgeDatabase.SetKnowledge(kitchen, 0f);
                Concepts.Reset();
                Expect("a meal bill the prison put on a stove", Concepts.Kitchen, () =>
                {
                    Building stove = InmateJobWorker_Kitchen.KitchenStoves(map, pm).First();
                    var giver = (IBillGiver)stove;
                    List<Bill> had = giver.BillStack.Bills.ToList();
                    // (The stove's own bills are taken off with Clear and put back as they were: Delete marks a bill deleted for good, and a deleted bill in the stack is offered to every cook, whose
                    // job then refuses it in the tick it starts: "started 10 jobs in one tick" on DoBill, for the rest of the run. This check used to do that.)
                    giver.BillStack.Clear();
                    pm.billedStoves.Remove(stove.thingIDNumber);
                    KitchenBills.Ensure(map);
                    giver.BillStack.Clear();
                    foreach (Bill b in had) giver.BillStack.AddBill(b);
                    if (!pm.billedStoves.Contains(stove.thingIDNumber)) pm.billedStoves.Add(stove.thingIDNumber);
                    if (giver.BillStack.Bills.Any(b => b.DeletedOrDereferenced)) FirstHourFail("a deleted bill was put back on the stove (every cook is handed it ten times in one tick)");
                });
                Concepts.Reset();
                var scene = new CourtScene(map, r);
                Pawn killer = scene.Inmate("Zkiller90"), victim = scene.Inmate("Zvictim90");
                Expect("a murder charge", Concepts.Trial, () => scene.Case(killer, victim));
                // A note learned stays learned: the same thing again teaches nothing.
                ConceptDef trial = ConceptDef.Named(Concepts.Trial);
                Concepts.Demonstrated(Concepts.Trial);
                Concepts.Reset();
                Concepts.Teach(Concepts.Trial);
                if (readout.IsActive(trial) || !PlayerKnowledgeDatabase.IsComplete(trial)) FirstHourFail("a note the player has learned was taught again");
                // The learning helper follows the player's own setting.
                Prefs.AdaptiveTrainingEnabled = false;
                ConceptDef complaint = ConceptDef.Named(Concepts.Complaint);
                Concepts.Reset();
                Concepts.Teach(Concepts.Complaint);
                if (readout.IsActive(complaint)) FirstHourFail("a note was taught with the learning helper switched off");
            });
        }

        // ---- 3.7: the dead --------------------------------------------------------------------------------------------------------------------------

        private static void CheckFirstHourMorgue(Map map)
        {
            Guarded("first hour 3.7: the guards carry the dead to the morgue", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Pawn body = Throwaway(map, r);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                IntVec3 where = body.Position;
                body.DeSpawn();
                Corpse corpse = (Corpse)ThingMaker.MakeThing(body.RaceProps.corpseDef);
                corpse.InnerPawn = body;
                GenSpawn.Spawn(corpse, where, map);
                r.Add(() =>
                {
                    if (!corpse.Destroyed) corpse.Destroy();
                });
                var giver = new WorkGiver_HaulToMorgue { def = DefDatabase<WorkGiverDef>.GetNamed("RP_HaulToMorgue") };
                if (!Morgue.Ours(corpse) || Morgue.InMorgue(corpse, pm)) FirstHourFail("the body of an inmate is not the prison's own, or is in the morgue where it fell");
                if (giver.ShouldSkip(guard) || !giver.HasJobOnThing(guard, corpse)) FirstHourFail("a guard has no job to carry the body of an inmate to the morgue");
                else if (giver.JobOnThing(guard, corpse) is not { } job || job.def != RPMorgueDefOf.RP_HaulToMorgue || job.targetA.Thing != corpse || pm.zones.ZoneAt(job.targetB.Cell) != RPMorgueDefOf.RP_Morgue)
                    FirstHourFail("the guard's job does not carry the body into the morgue");
                // A body already in the morgue is left; a raider's is vanilla's business.
                IntVec3 inside = pm.zones.CellsOf(RPMorgueDefOf.RP_Morgue).First(c => c.Standable(map));
                corpse.Position = inside;
                if (!Morgue.InMorgue(corpse, pm) || giver.HasJobOnThing(guard, corpse)) FirstHourFail("a body in the morgue is carried again");
                corpse.Position = where;
                // Without a morgue the list says bodies lie where they fell, and the giver has nothing to do.
                List<IntVec3> cells = pm.zones.CellsOf(RPMorgueDefOf.RP_Morgue).ToList();
                r.Add(() =>
                {
                    foreach (IntVec3 c in cells) pm.zones.Set(c, RPMorgueDefOf.RP_Morgue);
                });
                foreach (IntVec3 c in cells) pm.zones.Set(c, null);
                if (!giver.ShouldSkip(guard)) FirstHourFail("the guards look for bodies to carry with no morgue painted");
                string bodies = "RP_Problem_Bodies".Translate(Morgue.Uncollected(map, pm)).Resolve();
                if (Morgue.Uncollected(map, pm) < 1 || !PrisonDiagnostics.Problems(map).Contains(bodies)) FirstHourFail("a body with no morgue to take it to is not on the list of what to fix");
            });
        }

        // ---- 2.2, 2.3: one letter a decision moment; a budget of decisions ----------------------------------------------------------------------------

        private static void CheckFirstHourLetters(Map map)
        {
            // The cargo run says it once; the day's shuttle keeps its messages.
            var cargo = new ShuttleVisit { kind = ShuttleKind.Cargo, onPad = true };
            var daily = new ShuttleVisit { kind = ShuttleKind.Daily, onPad = true };
            if (ShuttleSystem.AnnouncesInbound(cargo) || !ShuttleSystem.AnnouncesInbound(daily) || ShuttleSystem.LandedKey(cargo) != "RP_ShuttleCargoLanded" || ShuttleSystem.LandedKey(daily) != "RP_ShuttleLanded"
                || ShuttleSystem.LiftOffNeedsMessage(cargo, 0, 0, 0) || !ShuttleSystem.LiftOffNeedsMessage(cargo, 1, 0, 0) || !ShuttleSystem.LiftOffNeedsMessage(cargo, 0, 0, 3) || !ShuttleSystem.LiftOffNeedsMessage(daily, 0, 0, 0))
                FirstHourFail("a cargo run does not announce itself once (coming down, landed, lifting off with nobody aboard)");
            Guarded("first hour 2.2: the case is one letter", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkiller91"), victim = s.Inmate("Zvictim91"), witness = s.Inmate("Zwitness91"), informant = s.Inmate("Zsnitch91");
                CompInmate ic = informant.Inmate();
                ic.reputations.Add(ReputationDefOf.RP_Snitch);
                ic.revealed.Add(ReputationDefOf.RP_Snitch);
                ic.informantQuit = false;
                MurderTrial t = s.Case(killer, victim);
                s.Witness(t, witness);
                int before = r.NewLetters.Count();
                Trials.Assemble(s.g, map, t);
                List<Letter> sent = r.NewLetters.Skip(before).ToList();
                if (sent.Count != 1 || sent[0] is not ChoiceLetter_PrisonDecision d || !d.Is("RP_Decision_TrialCase")) FirstHourFail($"assembling a case with a witness and an informant sent {sent.Count} letters ({string.Join(", ", sent.Select(l => l.Label.Resolve()))}), not one decision");
                else
                {
                    string text = d.Text.Resolve();
                    if (!text.Contains(witness.LabelShort) || !text.Contains(informant.LabelShort)) FirstHourFail("the case's letter does not say both choices (the witnesses and the informant)");
                    IReadOnlyList<string> options = d.decision.Worker.Options(d);
                    if (options.Count != 5 || options[d.decision.Worker.TimeoutOption(d)] != "RP_TrialCase_Neither") FirstHourFail("the case's letter does not leave things as they are when it times out");
                }
            });
            Guarded("first hour 2.3: a budget of decision letters", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                List<ChoiceLetter_PrisonDecision> deferred0 = g.story.deferredLetters;
                int decisions0 = g.story.decisionsToday;
                r.Add(() =>
                {
                    g.story.deferredLetters = deferred0;
                    g.story.decisionsToday = decisions0;
                    StoryLetters.TestBudget = false;
                });
                g.story.deferredLetters = new List<ChoiceLetter_PrisonDecision>();
                g.story.decisionsToday = 0;
                StoryLetters.TestBudget = true;
                int budget = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                ChoiceLetter_PrisonDecision Decision(int hours)
                {
                    ChoiceLetter_PrisonDecision l = ChoiceLetter_PrisonDecision.Make("RP_Decision_Holiday", "budget test", "budget test", map);
                    l.StartTimeout(GenDate.TicksPerHour * hours);
                    return l;
                }
                var all = new List<ChoiceLetter_PrisonDecision>();
                for (int i = 0; i < budget + 2; i++)
                {
                    ChoiceLetter_PrisonDecision l = Decision(24);
                    all.Add(l);
                    Find.LetterStack.ReceiveLetter(l);
                }
                int onStack = all.Count(l => Find.LetterStack.LettersListForReading.Contains(l)), waiting = g.story.deferredLetters.Count;
                if (onStack != budget || waiting != 2 || !all.Skip(budget).All(l => g.story.deferredLetters.Contains(l))) FirstHourFail($"{budget + 2} decisions in a day with a budget of {budget}: {onStack} reached the desk and {waiting} waited");
                if (!StoryLetters.AnyOpen("RP_Decision_Holiday") || StoryLetters.Open().Count(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_Holiday")) < budget + 2) FirstHourFail("a letter that waits is not counted as open");
                // An urgent one is never held back (a deadline within twelve hours).
                ChoiceLetter_PrisonDecision urgent = Decision(3);
                Find.LetterStack.ReceiveLetter(urgent);
                if (!Find.LetterStack.LettersListForReading.Contains(urgent) || g.story.deferredLetters.Contains(urgent)) FirstHourFail("an urgent decision (a deadline in three hours) was held back");
                // Midnight: a new day's budget, and the ones that waited are delivered with their answer time starting then.
                StoryLetters.NewDay(g);
                StoryLetters.Daily(g, map);
                if (g.story.deferredLetters.Count != 0 || all.Any(l => !Find.LetterStack.LettersListForReading.Contains(l)) || all.Skip(budget).Any(l => !l.TimeoutActive || l.disappearAtTick - Find.TickManager.TicksGame < 20 * GenDate.TicksPerHour))
                    FirstHourFail($"at midnight {g.story.deferredLetters.Count} decisions still waited, or one was not delivered with its full time to answer");
                if (g.story.decisionsToday != 2) FirstHourFail($"the delivered letters count {g.story.decisionsToday} against the new day, not two");
                foreach (ChoiceLetter_PrisonDecision l in all.Append(urgent)) Find.LetterStack.RemoveLetter(l);
                // With the budget off (an autotest run's own, ordinary state) nothing waits.
                StoryLetters.TestBudget = false;
                g.story.decisionsToday = 99;
                ChoiceLetter_PrisonDecision plain = Decision(24);
                Find.LetterStack.ReceiveLetter(plain);
                if (!Find.LetterStack.LettersListForReading.Contains(plain)) FirstHourFail("a decision was held back in a test run with the budget off");
                Find.LetterStack.RemoveLetter(plain);
            });
        }

        // ---- 2.7: the trial's pace -----------------------------------------------------------------------------------------------------------------

        private static void CheckFirstHourCourtPace(Map map)
        {
            Guarded("first hour 2.7: the evening before and the day it sits", map, r =>
            {
                var s = new CourtScene(map, r);
                if (HearingRooms.Available(map, s.pm))
                {
                    FirstHourFail("a hearing room is on the map before the check built one");
                    return;
                }
                Pawn killer = s.Inmate("Zkiller92"), victim = s.Inmate("Zvictim92"), witness = s.Inmate("Zwitness92");
                Pawn guard = s.Guard("Zguard92", victim.Position);
                MurderTrial t = s.Case(killer, victim);
                s.Witness(t, witness, known: true);
                s.Witness(t, guard, known: true);
                // The evening before (at six, with the court sitting within the day): a message, once a case; not on other evenings or other hours.
                int sent0 = Trials.RemindersSent;
                t.trialTick = s.Now + 16 * GenDate.TicksPerHour;
                s.Hour(17);
                s.Hour(19);
                if (Trials.RemindersSent != sent0) FirstHourFail("the court was announced at an hour that is not the evening before");
                t.trialTick = s.Now + 3 * GenDate.TicksPerDay;
                s.Hour(Trials.EveHour);
                if (Trials.RemindersSent != sent0) FirstHourFail("the court was announced three days ahead");
                t.trialTick = s.Now + 16 * GenDate.TicksPerHour;
                s.Hour(Trials.EveHour);
                if (Trials.RemindersSent != sent0 + 1) FirstHourFail($"the evening before the court sat, {Trials.RemindersSent - sent0} messages told the warden");
                // The day it sits (away at court): a letter names who will speak, as far as the warden knows.
                t.trialTick = s.Now;
                s.Hour(11);
                string sits = s.Told().FirstOrDefault(x => x.StartsWith("RP_TrialSitsLabel".Translate(killer.LabelShort).Resolve())) ?? "";
                if (sits.Length == 0 || !sits.Contains(witness.LabelShort) || !sits.Contains(guard.LabelShort)) FirstHourFail($"no letter at the start of the sitting named who will speak ({sits})");
                // What the warden has not been told stays untold.
                MurderTrial hidden = s.Case(s.Inmate("Zkiller93"), s.Inmate("Zvictim93"));
                Pawn secret = s.Inmate("Zsecret93");
                s.Witness(hidden, secret, known: false);
                if (Trials.KnownSpeakers(hidden, map).Contains(secret.LabelShort)) FirstHourFail("the sitting's letter would name a witness the prosecution has not told the warden of");
            });
        }

        // ---- a carried inmate's hourly update --------------------------------------------------------------------------------------------------

        /// <summary>
        /// An inmate being carried (to a bed, to a solitary cell: with the solitary cells of the starter facility, often) is on no map, and their hourly update asked the staff on "their" map for
        /// who is near: a null reference, seen in the events plan as "the room tension of Lady threw NullReferenceException (held in Verse.Pawn_CarryTracker)". The room and the map are the carrier's.
        /// </summary>
        private static void CheckFirstHourCarriedInmateRoomTension(Map map)
        {
            Guarded("first hour: the room tension of a carried inmate", map, r =>
            {
                Pawn inmate = Throwaway(map, r);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompInmate ic = inmate.Inmate();
                ic.staffMemories.Clear();
                for (int i = 0; i < 3; i++) StaffMemories.Note(ic, guard, StaffMemories.Restrained);
                guard.pather.StopDead();
                guard.Position = inmate.Position;
                guard.Notify_Teleported();
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                if (guard.carryTracker.TryStartCarry(inmate, 1) <= 0)
                {
                    FirstHourFail("a guard could not pick an inmate up to carry them");
                    return;
                }
                r.Add(() =>
                {
                    if (guard.carryTracker.CarriedThing != null) guard.carryTracker.TryDropCarriedThing(guard.Position, ThingPlaceMode.Near, out _);
                });
                try
                {
                    float t = StaffMemories.RoomTension(inmate, ic);
                    if (inmate.Spawned || t <= 0f) FirstHourFail($"a hated guard carrying an inmate did not wind them up (tension {t}, spawned {inmate.Spawned})");
                }
                catch (Exception e)
                {
                    FirstHourFail($"the room tension of a carried inmate threw {e.GetType().Name}");
                }
            });
        }

        // ---- 2.4, 2.5, 2.6: the screens ------------------------------------------------------------------------------------------------------------

        private static void CheckFirstHourIntel(Map map)
        {
            // The middle of the case's band is split in two halves (the chance of a conviction runs from nothing to certain across it).
            if (Trials.Band(0.3f) != "RP_TrialBand_Weak" || Trials.Band(0.45f) != "RP_TrialBand_LeanAcquit" || Trials.Band(0.55f) != "RP_TrialBand_LeanConvict" || Trials.Band(0.6f) != "RP_TrialBand_Strong"
                || Trials.Band(Trials.LeanMid - 0.001f) != "RP_TrialBand_LeanAcquit" || Trials.Band(Trials.LeanMid) != "RP_TrialBand_LeanConvict")
                FirstHourFail("the trial's uncertain band is not split into leaning to an acquittal and to a conviction at the middle");
            Guarded("first hour 2.4/2.5/2.6: the open cases desk", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller94"), victim = s.Inmate("Zvictim94"), w1 = s.Inmate("Zw1x94"), w2 = s.Inmate("Zw2x94");
                MurderTrial t = s.Case(killer, victim);
                t.assembled = true;
                s.Witness(t, w1, known: true);
                // The band is kept for a second per case: the screens ask on every event of a frame.
                Trials.ForgetScreenCache();
                string one = Trials.KnownBandCached(t, map);
                s.Witness(t, w2, known: true);
                t.priorRecord = 3;
                string again = Trials.KnownBandCached(t, map), fresh = Trials.KnownBand(t, map).Translate().Resolve();
                if (again != one || fresh == one) FirstHourFail($"the case's band is not kept for the screens (first {one}, kept {again}, fresh {fresh})");
                Trials.ForgetScreenCache();
                if (Trials.KnownBandCached(t, map) != fresh) FirstHourFail("the screens' cache of a case was not forgotten");
                Trials.ForgetScreenCache();
                // A complaint waiting for the warden can be reopened from the Intel screen while its letter stays in the stack.
                ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Complaint", "reopen test", "reopen test", map);
                letter.amount = 987654;
                Find.LetterStack.ReceiveLetter(letter);
                if (IntelTab.OpenDecision("RP_Decision_Complaint", d => d.amount == 987654) != letter) FirstHourFail("the Intel screen cannot find a complaint's letter that waits");
                letter.answered = true;
                if (IntelTab.OpenDecision("RP_Decision_Complaint", d => d.amount == 987654) != null) FirstHourFail("the Intel screen offers to reopen a letter that was answered");
                Find.LetterStack.RemoveLetter(letter);
                // Wants where the warden looks: a goal that is secret does not show, sort or filter; the report names the two nearest to their deadline.
                Pawn a = s.Inmate("Zwanta94"), b = s.Inmate("Zwantb94"), hush = s.Inmate("Zwantc94");
                InmateWant Want(Pawn p, int deadline, bool secret, WantStatus status)
                {
                    InmateWant w = new InmateWant { def = RPWantDefOf.RP_Want_Parole, status = status, deadlineTick = deadline, secret = secret, formedTick = 0 };
                    p.Inmate().want = w;
                    return w;
                }
                Want(a, -1_900_000_000, false, WantStatus.Slipping);
                Want(b, -1_800_000_000, false, WantStatus.OnTrack);
                Want(hush, -2_000_000_000, true, WantStatus.Slipping);
                if (InmatesTab.ShownWant(hush.Inmate()) != null || InmatesTab.WantOrder(hush.Inmate()) != 0f || InmatesTab.ShownWant(a.Inmate()) == null) FirstHourFail("a secret goal is shown in the inmate list");
                if (!(InmatesTab.WantOrder(a.Inmate()) > InmatesTab.WantOrder(b.Inmate()) && InmatesTab.WantOrder(b.Inmate()) > 0f)) FirstHourFail("slipping goals do not sort before those on track");
                List<string> nearest = WeeklyReport.NearestGoals(map, 2);
                if (nearest.Count != 2 || !nearest[0].Contains(a.LabelShort) || !nearest[1].Contains(b.LabelShort) || nearest.Any(l => l.Contains(hush.LabelShort))) FirstHourFail($"the weekly report's nearest goals are {string.Join(" / ", nearest)}");
            });
        }
    }
}
