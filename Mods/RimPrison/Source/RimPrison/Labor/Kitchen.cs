using System.Linq;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Kitchen duty: trusted inmates cook simple meals on the kitchen stoves from raw food kept in the kitchen (they
    /// pass the kitchen's Auto doors during work hours). Saves cook wages; knives have a way of going missing.
    /// </summary>
    public class InmateJobWorker_Kitchen : InmateJobWorker
    {
        public const float MealNutrition = 0.5f;

        private static RecipeDef CookSimple => RPVanillaDefOf.CookMealSimple;

        private static readonly Dictionary<ThingDef, bool> stoveDefs = new Dictionary<ThingDef, bool>();
        private static List<ThingDef> stoveDefList;

        /// <summary>Whether things of the def cook simple meals (asked of every bill check and every building the kitchen passes look at, so the answer is kept per def).</summary>
        private static bool IsStoveDef(ThingDef def)
        {
            if (stoveDefs.TryGetValue(def, out bool stove)) return stove;
            return stoveDefs[def] = CookSimple != null && def.AllRecipes.Contains(CookSimple);
        }

        public static bool IsStove(Building b) => IsStoveDef(b.def);

        /// <summary>Every building def that cooks simple meals (the stoves of the map are then looked up by def, not found by walking every building).</summary>
        public static List<ThingDef> StoveDefsList => StoveDefs;

        private static List<ThingDef> StoveDefs
        {
            get
            {
                if (stoveDefList != null) return stoveDefList;
                stoveDefList = new List<ThingDef>();
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                    if (def.category == ThingCategory.Building && IsStoveDef(def)) stoveDefList.Add(def);
                return stoveDefList;
            }
        }

        /// <summary>On fire, or fire where the cook would stand.</summary>
        public static bool Burning(Building b) => b.IsBurning() || (b.def.hasInteractionCell && b.InteractionCell.ContainsStaticFire(b.Map));

        private static bool Usable(Building b) =>
            b.Spawned && !b.IsBrokenDown() && !Burning(b) && (b.TryGetComp<CompRefuelable>() is not { } fuel || fuel.HasFuel) && (b.TryGetComp<CompPowerTrader>() is not { } power || power.PowerOn);

        /// <summary>The player's stoves standing in the kitchen zone.</summary>
        public static IEnumerable<Building> KitchenStoves(Map map, PrisonMap pm) => Stoves(map, pm);

        private static IEnumerable<Building> Stoves(Map map, PrisonMap pm)
        {
            // (By def from the map's thing lister, which is an index; the buildings lister's by-def list is a walk of every building.)
            List<ThingDef> defs = StoveDefs;
            for (int d = 0; d < defs.Count; d++)
            {
                List<Thing> of = map.listerThings.ThingsOfDef(defs[d]);
                for (int i = 0; i < of.Count; i++)
                    if (of[i] is Building b && b.Faction == Faction.OfPlayer && pm.zones.ZoneAt(b.Position) == RPLaborDefOf.RP_Kitchen) yield return b;
            }
        }

        public override int Capacity(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return 0;
            if (AutoTest.Active) Shadow.CheckStoves(map, pm, Stoves(map, pm).ToList());
            int n = 0;
            foreach (Building stove in Stoves(map, pm))
                if (Usable(stove)) n++;
            return n;
        }

        public static bool RawFood(Thing t) =>
            t.def.IsNutritionGivingIngestible && t.def.ingestible.preferability is FoodPreferability.RawBad or FoodPreferability.RawTasty
            && (t.def.ingestible.foodType & (FoodTypeFlags.VegetableOrFruit | FoodTypeFlags.Meat)) != 0;

        public override Job TryGiveJob(Pawn inmate, PrisonMap pm)
        {
            Map map = inmate.Map;
            foreach (Building stove in Stoves(map, pm))
            {
                if (!Usable(stove) || !inmate.CanReserveAndReach(stove, PathEndMode.InteractionCell, Danger.Some)) continue;
                Thing food = GenClosest.ClosestThingReachable(stove.Position, map, ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree), PathEndMode.ClosestTouch,
                    TraverseParms.For(inmate), 30f, t => RawFood(t) && pm.zones.ZoneAt(t.Position) == RPLaborDefOf.RP_Kitchen && !t.IsForbidden(Faction.OfPlayer)
                                                    && t.stackCount * t.GetStatValue(StatDefOf.Nutrition) >= MealNutrition && inmate.CanReserve(t, 1, -1));
                if (food == null) continue;
                Job job = JobMaker.MakeJob(RPLaborDefOf.RP_InmateCook, stove, food);
                job.count = Mathf.CeilToInt(MealNutrition / food.GetStatValue(StatDefOf.Nutrition));
                return job;
            }
            return null;
        }
    }

    public class JobDriver_InmateCook : JobDriver
    {
        private const int CookTicks = 600;

        private Building Stove => (Building)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed) && pawn.Reserve(job.targetB, job, 1, job.count, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.FailOn(() => Stove.TryGetComp<CompRefuelable>() is { HasFuel: false } || Stove.TryGetComp<CompPowerTrader>() is { PowerOn: false }
                              || InmateJobWorker_Kitchen.Burning(Stove));
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            Toil cook = Toils_General.Wait(ToolCount.WorkTicks(RPLaborDefOf.RP_Kitchen, CookTicks));
            cook.handlingFacing = true;
            cook.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(Stove);
                pawn.skills?.Learn(SkillDefOf.Cooking, 0.08f * delta);
            };
            cook.WithProgressBarToilDelay(TargetIndex.A);
            cook.FailOn(() => pawn.carryTracker.CarriedThing == null);
            yield return cook;
            Toil done = ToilMaker.MakeToil("MealCooked");
            done.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried == null) return;
                carried.SplitOff(Mathf.Min(carried.stackCount, job.count)).Destroy();
                if (pawn.carryTracker.CarriedThing != null) pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                Stove.TryGetComp<CompRefuelable>()?.ConsumeFuel(0.5f);
                Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                GenPlace.TryPlaceThing(meal, Stove.InteractionCell, Stove.Map, ThingPlaceMode.Near);
                PrisonGame.Instance.stats.inmateMeals++;
                PrisonGame.Instance.today.mealsCooked++;
            };
            yield return done;
        }
    }
}

namespace RimPrison
{
    /// <summary>
    /// While inmates are on kitchen duty in the kitchen, the kitchen is theirs: staff meal bills at the kitchen
    /// stoves pause, so a staff cook does not use up the raw food the inmate cooks need (they used to stand idle).
    /// Outside the inmates' work hours the staff cook works as usual.
    /// </summary>
    public static class KitchenShift
    {
        private static int cacheTick = -1, cacheMapId = -1;
        private static bool cacheResult;

        /// <summary>How long the answer of <see cref="InmatesCooking"/> is kept: the tick (measured, a verdict costs a pass over the prisoners that stops at the first test: not worth a stale answer).</summary>
        public const int VerdictTicks = 1;

        private static bool KitchenHasRawFood(Map map, PrisonMap pm)
        {
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
                if (InmateJobWorker_Kitchen.RawFood(t) && pm.zones.ZoneAt(t.Position) == RPLaborDefOf.RP_Kitchen && !t.IsForbidden(Faction.OfPlayer)
                    && t.stackCount * t.GetStatValue(StatDefOf.Nutrition) >= InmateJobWorker_Kitchen.MealNutrition)
                    return true;
            return false;
        }

        /// <summary>Forget the answer (tests change the kitchen within a tick).</summary>
        public static void Reset() => cacheTick = -1;

        public static bool InmatesCooking(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (cacheTick >= 0 && now >= cacheTick && now - cacheTick < VerdictTicks && map.uniqueID == cacheMapId) return cacheResult;
            cacheTick = now;
            cacheMapId = map.uniqueID;
            cacheResult = false;
            PrisonGame game = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            if (game == null || pm == null) return false;
            bool onDuty = false;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { activeFlag: true } c || c.job?.workerClass != typeof(InmateJobWorker_Kitchen)) continue;
                if (pm.zones.ZoneAt(p.Position) != RPLaborDefOf.RP_Kitchen || game.CurrentActivity(p) != RPLaborDefOf.RP_Work) continue;
                // Already at a stove: the kitchen is theirs.
                if (p.CurJobDef == RPLaborDefOf.RP_InmateCook) return cacheResult = true;
                onDuty = true;
            }
            // On duty but idle: only theirs if they can cook (a working stove, raw food kept in the kitchen); else the
            // staff cook carries on (raw food in a freezer outside the kitchen zone, say).
            cacheResult = onDuty && new InmateJobWorker_Kitchen().Capacity(map) > 0 && KitchenHasRawFood(map, pm);
            return cacheResult;
        }
    }

    /// <summary>
    /// Vanilla's reservations are respected between pawns of one faction and, for a colonist, not at all for a prisoner of a hostile faction (<c>ReservationManager.RespectsReservationsOf</c>),
    /// so a staff cook's scan for a bill's ingredients offers the stack an inmate cook has reserved: both go for the same raw food and the staff cook's trip is wasted. In the kitchen
    /// the raw food is shared by the two, so a staff member respects an inmate's claim on a stack the way they respect a colleague's: the stack is taken when the inmate is done with it.
    /// (A physical-interaction reservation, which an inmate chewing raw food holds, is vanilla's own check and already applies to everybody.)
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(ReservationManager), nameof(ReservationManager.CanReserve))]
    public static class Patch_ReservationManager_CanReserve_InmateClaimsInKitchen
    {
        public static void Postfix(ReservationManager __instance, Pawn claimant, LocalTargetInfo target, int stackCount, ReservationLayerDef layer, bool ignoreOtherReservations, ref bool __result, Map ___map)
        {
            if (!__result || ignoreOtherReservations || claimant == null || !target.HasThing || PrisonGame.Instance == null) return;
            Thing t = target.Thing;
            // (Asked for everything that is reserved: the cheap tests come first.)
            if (t.def.ingestible == null || !InmateJobWorker_Kitchen.RawFood(t) || !claimant.IsColonist || !t.Spawned || PrisonMap.Of(___map)?.zones.ZoneAt(t.Position) != RPLaborDefOf.RP_Kitchen) return;
            if (InmateClaim(__instance, t, layer) is var claimed and > 0 && claimed + (stackCount == -1 ? t.stackCount : stackCount) > t.stackCount) __result = false;
        }

        /// <summary>How much of the stack the inmates have reserved (all of it for a reservation of the whole stack).</summary>
        public static int InmateClaim(ReservationManager manager, Thing t, ReservationLayerDef layer)
        {
            int claimed = 0;
            List<ReservationManager.Reservation> all = manager.ReservationsReadOnly;
            for (int i = 0; i < all.Count; i++)
                if (all[i].Target.Thing == t && all[i].Layer == layer && all[i].Claimant.IsPrisonerOfColony) claimed += all[i].StackCount == -1 ? t.stackCount : all[i].StackCount;
            return claimed;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.ShouldDoNow))]
    public static class Patch_BillProduction_KitchenShift
    {
        public static void Postfix(Bill_Production __instance, ref bool __result)
        {
            if (!__result || PrisonGame.Instance == null) return;
            if (__instance.billStack?.billGiver is not Building stove || !stove.Spawned || !InmateJobWorker_Kitchen.IsStove(stove)) return;
            if (__instance.recipe?.ProducedThingDef is not { IsIngestible: true }) return;
            if (PrisonMap.Of(stove.Map)?.zones.ZoneAt(stove.Position) != RPLaborDefOf.RP_Kitchen) return;
            if (KitchenShift.InmatesCooking(stove.Map)) __result = false;
        }
    }
}

namespace RimPrison
{
    /// <summary>
    /// A stove in a kitchen zone cooks without the warden having to know about bills: when a stove of the kitchen has none, it gets a standing
    /// target-count meal bill, scaled to the cell beds (whoever built the stove, when it is built, when the zone is painted under it, and at every
    /// hour). A stove is billed once: a bill the warden then deletes stays deleted, one they edit stays theirs; the bills left as made grow
    /// with the beds (a kitchen built before the cells does not keep a target of ten meals for a prison of sixty).
    /// </summary>
    public static class KitchenBills
    {
        /// <summary>Meals kept in stock for every cell bed, and a flat amount on top (the stockpile and the counters hold the meals of a day or so).</summary>
        public const int MealsPerBed = 4, BaseMeals = 10;
        /// <summary>From this many beds the stoves cook in bulk: four meals per trip to the ingredients.</summary>
        public const int BulkFromBeds = 24;

        public static int TargetFor(int beds) => BaseMeals + MealsPerBed * beds;

        public static RecipeDef RecipeFor(KitchenMenu menu, int beds) => KitchenMenuPolicy.Recipe(menu, bulk: beds >= BulkFromBeds);

        private static WorkTypeDef CookingWork => RPVanillaDefOf.Cooking;

        /// <summary>The staff whose role cooks, on site or at home (a guard who happens to have the skill is not who cooks, and a cook off shift is still the prison's cook).</summary>
        public static IEnumerable<Pawn> Cooks(Map map)
        {
            foreach (Pawn p in StaffCommute.AllStaff(map))
                if (p.GetComp<CompStaff>()?.role?.workTypes?.Contains(CookingWork) == true) yield return p;
        }

        /// <summary>Whether somebody on the staff can cook the recipe (the fine meal wants Cooking 6).</summary>
        public static bool SomeoneCanCook(IEnumerable<Pawn> staff, RecipeDef recipe) => FirstWhoCanCook(staff, recipe) != null;

        /// <summary>The first of the staff who can cook the recipe (null: nobody).</summary>
        public static Pawn FirstWhoCanCook(IEnumerable<Pawn> staff, RecipeDef recipe)
        {
            if (recipe == null) return null;
            foreach (Pawn p in staff)
                if (!p.WorkTypeIsDisabled(CookingWork) && recipe.PawnSatisfiesSkillRequirements(p)) return p;
            return null;
        }

        /// <summary>
        /// The meal a new stove's bill is for: the menu's, but never one that no cook on the staff can make (a fine menu with nobody at Cooking 6 would leave the stove with a bill nobody
        /// does): then the simple meal, and a message says so (<paramref name="said"/>).
        /// </summary>
        public static RecipeDef RecipeForStove(Map map, KitchenMenu menu, int beds, out bool said, IEnumerable<Pawn> cooks = null)
        {
            said = false;
            RecipeDef recipe = RecipeFor(menu, beds);
            if (menu != KitchenMenu.Fine || map == null || SomeoneCanCook(cooks ?? Cooks(map), recipe)) return recipe;
            said = true;
            return RecipeFor(KitchenMenu.Simple, beds);
        }

        /// <summary>Whether a stove has a bill of its own for meals (a paused one counts: it is somebody's decision).</summary>
        public static bool HasMealBill(IBillGiver giver)
        {
            List<Bill> bills = giver.BillStack.Bills;
            for (int i = 0; i < bills.Count; i++)
                if (bills[i] is Bill_Production { recipe: { } r } && r.ProducedThingDef is { IsIngestible: true }) return true;
            return false;
        }

        /// <summary>Puts the standing meal bill on a stove (the menu's meal, bulk for a big prison) and records it as made by the prison.</summary>
        public static Bill_Production AddBill(Thing stove, int beds, PrisonMap pm = null)
        {
            if (stove is not IBillGiver giver) return null;
            RecipeDef recipe = RecipeForStove(stove.Map, PrisonGame.Instance?.kitchenMenu ?? KitchenMenu.Simple, beds, out bool fellBack);
            if (recipe == null || !stove.def.AllRecipes.Contains(recipe)) return null;
            if (fellBack) Messages.Message("RP_KitchenFineNoCook".Translate(stove.LabelShort, RecipeFor(KitchenMenu.Fine, beds).skillRequirements?.FirstOrDefault()?.minLevel ?? 6), stove, MessageTypeDefOf.CautionInput, historical: false);
            var bill = new Bill_Production(recipe)
            {
                repeatMode = BillRepeatModeDefOf.TargetCount,
                targetCount = TargetFor(beds),
            };
            giver.BillStack.AddBill(bill);
            pm ??= PrisonMap.Of(stove.Map);
            if (pm != null)
            {
                if (!pm.billedStoves.Contains(stove.thingIDNumber)) pm.billedStoves.Add(stove.thingIDNumber);
                pm.cookBills[bill.GetUniqueLoadID()] = bill.targetCount;
            }
            return bill;
        }

        /// <summary>
        /// Bills every unbilled stove of the kitchen zone and lets the prison's own bills follow the beds (and, from <see cref="BulkFromBeds"/>, switch to the bulk recipe). A stove that has
        /// a bill of the warden's is remembered too, so a bill they delete stays deleted. Stoves a save from before stoves were billed already had are remembered as they stand (some were
        /// left unbilled on purpose, so that only inmates cook). Returns how many bills it made.
        /// </summary>
        public static int Ensure(Map map) => Ensure(map, map == null ? 0 : IntakeSystem.Capacity(map));

        /// <summary>The same for a prison of <paramref name="beds"/> cell beds (tests ask about a bigger one than they have).</summary>
        public static int Ensure(Map map, int beds)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || map == null || PrisonGame.Instance == null || !pm.zones.Any(RPLaborDefOf.RP_Kitchen)) return 0;
            int made = 0;
            bool remembering = !pm.stovesRemembered;
            pm.stovesRemembered = true;
            foreach (Building stove in InmateJobWorker_Kitchen.KitchenStoves(map, pm))
            {
                if (stove is not IBillGiver giver) continue;
                MendBillIds(giver, pm);
                bool billed = pm.billedStoves.Contains(stove.thingIDNumber);
                if (!billed && (remembering || HasMealBill(giver)))
                {
                    pm.billedStoves.Add(stove.thingIDNumber);
                    billed = true;
                }
                if (!billed)
                {
                    // (A bill the prison puts on a stove of the warden's: the note says what happened. The starter's own are made by the builder, not by this.)
                    if (AddBill(stove, beds, pm) != null)
                    {
                        made++;
                        Concepts.Teach(Concepts.Kitchen);
                    }
                    continue;
                }
                // Left as made: it grows with the beds (never shrinks, so a warden who turned down an intake does not starve the kitchen), and goes bulk when the prison is big.
                foreach (Bill b in giver.BillStack.Bills.ToList())
                    if (b is Bill_Production { repeatMode: var mode } bill && mode == BillRepeatModeDefOf.TargetCount && pm.cookBills.TryGetValue(bill.GetUniqueLoadID(), out int set))
                    {
                        if (bill.targetCount != set)
                        {
                            pm.cookBills.Remove(bill.GetUniqueLoadID());
                            continue;
                        }
                        if (TargetFor(beds) > set) pm.cookBills[bill.GetUniqueLoadID()] = bill.targetCount = TargetFor(beds);
                        RecipeDef bulk = KitchenMenuPolicy.BulkOf(bill.recipe);
                        if (beds >= BulkFromBeds && bulk != null && stove.def.AllRecipes.Contains(bulk)) KitchenMenuPolicy.Replace(giver, bill, bulk, pm);
                    }
            }
            KitchenMenuPolicy.Follow(map, pm);
            Prune(map, pm);
            return made;
        }

        /// <summary>A bill that was cloned without a number of its own (a menu change used to, and every such bill is "Bill_x_-1") is numbered again, and keeps its memory in <see cref="PrisonMap.cookBills"/>.</summary>
        private static void MendBillIds(IBillGiver giver, PrisonMap pm)
        {
            foreach (Bill b in giver.BillStack.Bills)
            {
                string id = b.GetUniqueLoadID();
                if (!id.EndsWith("_-1")) continue;
                b.InitializeAfterClone();
                if (pm.cookBills.Remove(id, out int target)) pm.cookBills[b.GetUniqueLoadID()] = target;
            }
        }

        /// <summary>Forgets stoves that are gone and bills that are gone (the lists would otherwise grow with every stove and menu change).</summary>
        private static void Prune(Map map, PrisonMap pm)
        {
            var stoves = new HashSet<int>();
            var bills = new HashSet<string>();
            List<ThingDef> defs = StoveDefsOf();
            for (int d = 0; d < defs.Count; d++)
                foreach (Thing t in map.listerThings.ThingsOfDef(defs[d]))
                {
                    stoves.Add(t.thingIDNumber);
                    if (t is IBillGiver giver)
                        foreach (Bill b in giver.BillStack.Bills) bills.Add(b.GetUniqueLoadID());
                }
            pm.billedStoves.RemoveAll(id => !stoves.Contains(id));
            if (pm.cookBills.Keys.Any(k => !bills.Contains(k)))
                foreach (string k in pm.cookBills.Keys.Where(k => !bills.Contains(k)).ToList()) pm.cookBills.Remove(k);
        }

        private static List<ThingDef> StoveDefsOf() => InmateJobWorker_Kitchen.StoveDefsList;
    }

    /// <summary>A stove built, or moved into place, in a painted kitchen is billed at once.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Building), nameof(Building.SpawnSetup))]
    public static class Patch_Building_SpawnSetup_KitchenBill
    {
        public static void Postfix(Building __instance, Map map, bool respawningAfterLoad)
        {
            if (respawningAfterLoad || PrisonGame.Instance == null || __instance.Faction != Faction.OfPlayer || !InmateJobWorker_Kitchen.IsStove(__instance)) return;
            if (PrisonMap.Of(map)?.zones.ZoneAt(__instance.Position) == RPLaborDefOf.RP_Kitchen) KitchenBills.Ensure(map);
        }
    }
}

namespace RimPrison
{
    public enum KitchenMenu : byte
    {
        Simple,
        Fine,
    }

    /// <summary>
    /// The kitchen menu (Policy): simple meals, or fine ones (vegetable fine meals: half as much produce again, and
    /// the vanilla "ate a fine meal" lift for whoever eats them). Applied when the warden changes it, to the meal bills
    /// on the kitchen's stoves only (bills elsewhere are the player's own business); each bill keeps all its settings
    /// and its place in the list. Inmate cooks keep to simple meals.
    /// </summary>
    public static class KitchenMenuPolicy
    {
        /// <summary>(simple, fine) recipe pairs: single and bulk.</summary>
        private static (RecipeDef simple, RecipeDef fine)[] Pairs => new[] { (RPVanillaDefOf.CookMealSimple, RPVanillaDefOf.CookMealFine_Veg), (RPVanillaDefOf.CookMealSimpleBulk, RPVanillaDefOf.CookMealFineBulk_Veg) };

        public static RecipeDef Recipe(KitchenMenu menu, bool bulk = false) =>
            menu == KitchenMenu.Fine ? Pairs[bulk ? 1 : 0].fine : Pairs[bulk ? 1 : 0].simple;

        /// <summary>
        /// Swap the other menu's meal bills on kitchen stoves for this one's. Returns how many. The fine meal is never put on a stove while nobody on the staff can cook it (a stove whose bill
        /// nobody does cooks nothing: three days with no meals cooked, the cook under Cooking 6): the bills stay simple, the warden is told, and <see cref="Follow"/> switches them once somebody can.
        /// </summary>
        public static int Apply(Map map, KitchenMenu menu)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (map == null || pm == null) return 0;
            int swapped = 0;
            bool declined = false;
            foreach ((RecipeDef simple, RecipeDef fine) in Pairs)
            {
                RecipeDef want = menu == KitchenMenu.Fine ? fine : simple;
                RecipeDef other = menu == KitchenMenu.Fine ? simple : fine;
                if (menu == KitchenMenu.Fine && !KitchenBills.SomeoneCanCook(KitchenBills.Cooks(map), want))
                {
                    declined = true;
                    continue;
                }
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                {
                    if (b is not IBillGiver giver || !InmateJobWorker_Kitchen.IsStove(b) || pm.zones.ZoneAt(b.Position) != RPLaborDefOf.RP_Kitchen
                        || !b.def.AllRecipes.Contains(want)) continue;
                    foreach (Bill_Production bill in giver.BillStack.Bills.OfType<Bill_Production>().Where(x => x.recipe == other).ToList())
                    {
                        Replace(giver, bill, want, pm);
                        swapped++;
                    }
                }
            }
            if (declined) Messages.Message("RP_KitchenFineNoCookNow".Translate(FineSkill()), MessageTypeDefOf.CautionInput, historical: false);
            return swapped;
        }

        private static int FineSkill() => Recipe(KitchenMenu.Fine)?.skillRequirements?.FirstOrDefault()?.minLevel ?? 6;

        /// <summary>
        /// Hourly: the meal bills the prison made follow what the staff can cook. With the fine menu and nobody who can cook it (nobody ever could, or the last cook who could has left, died or
        /// been fired) they are simple meals, and fine ones again when somebody can. Only the prison's own bills (<see cref="PrisonMap.cookBills"/>): one the warden made or edited is theirs.
        /// Returns how many were swapped.
        /// </summary>
        public static int Follow(Map map, PrisonMap pm)
        {
            if (PrisonGame.Instance is not { kitchenMenu: KitchenMenu.Fine } || pm.cookBills.Count == 0) return 0;
            int swapped = 0, toSimple = 0;
            Pawn cook = null;
            foreach ((RecipeDef simpleRecipe, RecipeDef fineRecipe) in Pairs)
            {
                cook = KitchenBills.FirstWhoCanCook(KitchenBills.Cooks(map), fineRecipe);
                RecipeDef want = cook != null ? fineRecipe : simpleRecipe, other = cook != null ? simpleRecipe : fineRecipe;
                foreach (Building b in InmateJobWorker_Kitchen.KitchenStoves(map, pm))
                {
                    if (b is not IBillGiver giver || !b.def.AllRecipes.Contains(want)) continue;
                    foreach (Bill_Production bill in giver.BillStack.Bills.OfType<Bill_Production>().Where(x => x.recipe == other && pm.cookBills.ContainsKey(x.GetUniqueLoadID())).ToList())
                    {
                        Replace(giver, bill, want, pm);
                        swapped++;
                        if (cook == null) toSimple++;
                    }
                }
            }
            if (swapped > 0)
                Messages.Message(toSimple > 0 ? "RP_KitchenFineNoCookNow".Translate(FineSkill()) : "RP_KitchenFineCookBack".Translate(cook?.LabelShort ?? "-"), MessageTypeDefOf.NeutralEvent, historical: false);
            return swapped;
        }

        /// <summary>The bulk recipe of a single-meal one (null when it is not one, or already bulk).</summary>
        public static RecipeDef BulkOf(RecipeDef recipe)
        {
            foreach ((RecipeDef simple, RecipeDef fine) in Pairs)
                if (recipe == simple || recipe == fine) return (recipe == simple ? Pairs[1].simple : Pairs[1].fine) is { } bulk && bulk != recipe ? bulk : null;
            return null;
        }

        /// <summary>
        /// Puts a bill for another recipe in the place of this one: everything the player set (store mode, skill range, worker, pause, radius...) comes along and only the ingredient filter
        /// starts from the new recipe's own. The copy is a bill of its own (a clone has no number until it is given one, and two with the same number are one bill to a save), and the
        /// prison's memory of the target of the bill it replaces goes to it.
        /// </summary>
        public static Bill_Production Replace(IBillGiver giver, Bill_Production bill, RecipeDef want, PrisonMap pm)
        {
            List<Bill> bills = giver.BillStack.Bills;
            int at = bills.IndexOf(bill);
            var swap = (Bill_Production)bill.Clone();
            swap.recipe = want;
            swap.InitializeAfterClone();
            swap.ingredientFilter = new ThingFilter();
            swap.ingredientFilter.CopyAllowancesFrom(want.defaultIngredientFilter ?? want.fixedIngredientFilter);
            if (pm != null && pm.cookBills.Remove(bill.GetUniqueLoadID(), out int target)) pm.cookBills[swap.GetUniqueLoadID()] = target;
            giver.BillStack.Delete(bill);
            giver.BillStack.AddBill(swap);
            bills.Remove(swap);
            bills.Insert(Mathf.Clamp(at, 0, bills.Count), swap);
            return swap;
        }
    }
}
