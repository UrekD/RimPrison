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
    /// Regression checks for the review of the first hour (H1-H4, M1-M7, L1 and the cook's DoBill loop). Each works on throwaway things and puts back what it touches (<see cref="Rollback"/>),
    /// and each fails on the behaviour the fix replaced (a copy of the mod with the fixes taken out, <c>.work/mutate.py</c>, was run to show it). RIMPRISON_SKIP names (see
    /// <see cref="SkipFirstHour"/>) leave single checks out.
    /// </summary>
    public static partial class AutoTest
    {
        private static DifficultyDef Level(string name) => DefDatabase<DifficultyDef>.GetNamed(name);

        private static void CheckFirstHourFixes(Map map)
        {
            var checks = new (string name, Action<Map> run)[]
            {
                ("fix-letters", CheckFixLetters), ("fix-kitchen", CheckFixKitchen), ("fix-wants", CheckFixWants), ("fix-morgue", CheckFixMorgue), ("fix-list", CheckFixList),
                ("fix-informant", CheckFixInformant), ("fix-grades", CheckFixGrades), ("fix-small", CheckFixSmall), ("fix-dobill", CheckFixDoBill),
            };
            foreach ((string name, Action<Map> run) in checks)
                if (!SkipFirstHour(name)) run(map);
        }

        // ---- H1: the budget of decisions ------------------------------------------------------------------------------------------------------

        private static void CheckFixLetters(Map map)
        {
            Guarded("first hour fix H1: a letter that warns of something with a deadline is never held", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                List<ChoiceLetter_PrisonDecision> deferred0 = g.story.deferredLetters;
                int decisions0 = g.story.decisionsToday, nextArc0 = g.story.nextArcId, arcs0 = g.story.arcs.Count;
                DifficultyDef difficulty0 = Find.Storyteller.difficultyDef;
                r.Add(() =>
                {
                    g.story.deferredLetters = deferred0;
                    g.story.decisionsToday = decisions0;
                    g.story.nextArcId = nextArc0;
                    if (g.story.arcs.Count > arcs0) g.story.arcs.RemoveRange(arcs0, g.story.arcs.Count - arcs0);
                    Find.Storyteller.difficultyDef = difficulty0;
                    StoryLetters.TestBudget = false;
                });
                g.story.deferredLetters = new List<ChoiceLetter_PrisonDecision>();
                g.story.decisionsToday = 0;
                StoryLetters.TestBudget = true;
                int budget = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                ChoiceLetter_PrisonDecision Letter(string decision, int hours)
                {
                    ChoiceLetter_PrisonDecision l = ChoiceLetter_PrisonDecision.Make(decision, "fix test", "fix test", map);
                    l.StartTimeout(GenDate.TicksPerHour * hours);
                    return l;
                }
                bool OnDesk(Letter l) => Find.LetterStack.LettersListForReading.Contains(l);
                var all = new List<ChoiceLetter_PrisonDecision>();
                void Send(ChoiceLetter_PrisonDecision l)
                {
                    all.Add(l);
                    Find.LetterStack.ReceiveLetter(l);
                }
                // The day's budget is used up by ordinary letters; one more ordinary letter waits.
                for (int i = 0; i < budget; i++) Send(Letter("RP_Decision_Holiday", 24));
                ChoiceLetter_PrisonDecision plain = Letter("RP_Decision_Holiday", 24);
                Send(plain);
                if (OnDesk(plain) || !g.story.deferredLetters.Contains(plain)) FirstHourFail("an ordinary decision past the day's budget was not held (the check's own premise)");
                // What warns of something with a deadline gets through whatever its answer time is (long ones: the flag, not the deadline).
                foreach (string name in new[] { "RP_Decision_Inspection", "RP_Decision_StaffDeath", "RP_Decision_TrialCase", "RP_Decision_DebtCrisis", "RP_Decision_MeasuresExtended", "RP_Decision_Overdose",
                             "RP_Decision_ProtectiveCustody", "RP_Decision_InmateDemand", "RP_Decision_HazardPay", "RP_Decision_RiotNegotiation", "RP_Decision_KitchenFire", "RP_Decision_StaffStrike" })
                {
                    ChoiceLetter_PrisonDecision l = Letter(name, 24);
                    Send(l);
                    if (!OnDesk(l) || g.story.deferredLetters.Contains(l)) FirstHourFail($"{name} was held back by the day's budget (a 24 hour answer time)");
                }
                ChoiceLetter_PrisonDecision inspection = Letter("RP_Decision_Inspection", IncidentWorker_Inspection.NoticeHours - 2);
                Send(inspection);
                if (!OnDesk(inspection)) FirstHourFail("the inspection's notice (an 18 hour answer time) was held back by the day's budget");
                // The arcs that run their own clock: a feud, a gang war, a leadership challenge. A smuggling ring's decision is not one of them.
                StoryArc Arc(string def)
                {
                    var a = new StoryArc { def = DefDatabase<StoryArcDef>.GetNamed(def), id = g.story.nextArcId++ };
                    g.story.arcs.Add(a);
                    return a;
                }
                ChoiceLetter_PrisonDecision ArcLetter(StoryArc a)
                {
                    ChoiceLetter_PrisonDecision l = Letter("RP_Decision_Arc", 24);
                    l.arcId = a.id;
                    return l;
                }
                foreach (string arc in new[] { "RP_Arc_Feud", "RP_Arc_GangWar", "RP_Arc_GangChallenge" })
                {
                    ChoiceLetter_PrisonDecision l = ArcLetter(Arc(arc));
                    Send(l);
                    if (!OnDesk(l)) FirstHourFail($"a letter of {arc} (an event on its own clock) was held back");
                }
                StoryArc ringArc = Arc("RP_Arc_SmugglingRing");
                ChoiceLetter_PrisonDecision ringLetter = ArcLetter(ringArc);
                Send(ringLetter);
                if (OnDesk(ringLetter)) FirstHourFail("a smuggling ring's letter was not held back: the arc flag is on every arc");
                // None of those used up the day's budget.
                if (g.story.decisionsToday != budget) FirstHourFail($"the urgent letters used up the budget: {g.story.decisionsToday} of {budget}");
                // Midnight: the count starts again before the day's own systems write theirs, and the backlog takes what is left after them; an ended arc's letter is dropped, a stale one lapses.
                ringArc.done = true;
                // (A letter that is no more than a closed notice: its default answer does nothing, so the check leaves nothing behind.)
                ChoiceLetter_PrisonDecision stale = Letter("RP_Decision_Arc", 24);
                Send(stale);
                int later = Find.TickManager.TicksGame + (StoryLetters.MaxHeldDays + 1) * GenDate.TicksPerDay;
                foreach (ChoiceLetter_PrisonDecision waiting in g.story.deferredLetters.Where(x => x != stale)) waiting.heldTick = later - 1;
                StoryLetters.NewDay(g);
                if (g.story.decisionsToday != 0) FirstHourFail("the new day's count did not start at zero");
                ChoiceLetter_PrisonDecision own = Letter("RP_Decision_Holiday", 24);
                Send(own);
                StoryLetters.Daily(g, map, later);
                if (!OnDesk(own)) FirstHourFail("a letter the day's own system wrote before the backlog was delivered was held");
                if (!OnDesk(plain) || g.story.deferredLetters.Contains(plain)) FirstHourFail("the held letter was not delivered at midnight");
                if (g.story.deferredLetters.Contains(ringLetter) || OnDesk(ringLetter) || g.story.deferredLetters.Count != 0) FirstHourFail($"the letter of an ended arc was delivered or kept ({g.story.deferredLetters.Count} still waiting)");
                if (OnDesk(stale) || g.story.deferredLetters.Contains(stale) || !stale.answered) FirstHourFail("a letter held for more than three days neither lapsed nor was dropped");
                if (g.story.decisionsToday != 2) FirstHourFail($"the midnight delivery counted {g.story.decisionsToday} decisions against the new day (the day's own and the held one: two)");
                // More decisions where more happens; never fewer where less does.
                int inmates = map.mapPawns.PrisonersOfColonySpawnedCount;
                Find.Storyteller.difficultyDef = Level("Extreme");
                int extreme = StoryLetters.Budget(inmates);
                Find.Storyteller.difficultyDef = Level("Peaceful");
                int peaceful = StoryLetters.Budget(inmates);
                Find.Storyteller.difficultyDef = Level("Rough");
                int neutral = StoryLetters.Budget(inmates);
                if (extreme <= neutral || peaceful != neutral) FirstHourFail($"the day's budget is {peaceful} at Peaceful, {neutral} at Rough and {extreme} at Extreme (more at Extreme, the same below)");
                foreach (ChoiceLetter_PrisonDecision l in all) Find.LetterStack.RemoveLetter(l);
            });
        }

        // ---- H3, M7: the kitchen's bills ---------------------------------------------------------------------------------------------------------

        private static readonly FieldInfo BillLoadId = AccessTools.Field(typeof(Bill), "loadID");

        /// <summary>A stove put up in the yard (no bill: it is nobody's kitchen), removed by the rollback; <see cref="Kitchenize"/> paints the kitchen under it.</summary>
        private static Building FixStove(Map map, PrisonMap pm, Rollback r, List<IntVec3> taken)
        {
            ThingDef stoveDef = DefDatabase<ThingDef>.GetNamed("FueledStove");
            // (Where the whole stove and the cell a cook stands on are free yard.)
            foreach (IntVec3 spot in pm.zones.CellsOf(RPDefOf.RP_Yard).ToList())
            {
                List<IntVec3> cells = GenAdj.OccupiedRect(spot, Rot4.North, stoveDef.size).Cells.ToList();
                IntVec3 stand = spot + new IntVec3(0, 0, -1);
                if (!spot.Standable(map) || cells.Any(x => !x.InBounds(map) || !x.Standable(map) || x.GetThingList(map).Count > 0 || pm.zones.ZoneAt(x) != RPDefOf.RP_Yard || taken.Contains(x))
                    || !stand.InBounds(map) || !stand.Standable(map) || stand.GetThingList(map).Count > 0 || taken.Contains(stand)) continue;
                var stove = (Building)ThingMaker.MakeThing(stoveDef, GenStuff.DefaultStuffFor(stoveDef));
                stove.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(stove, spot, map, Rot4.North);
                if (!stove.InteractionCell.Standable(map) || stove.InteractionCell.GetRegion(map) == null)
                {
                    stove.Destroy(DestroyMode.Vanish);
                    continue;
                }
                taken.AddRange(cells);
                taken.Add(stove.InteractionCell);
                r.Add(() =>
                {
                    foreach (IntVec3 c in cells) pm.zones.Set(c, RPDefOf.RP_Yard);
                    if (!stove.Destroyed) stove.Destroy(DestroyMode.Vanish);
                    pm.billedStoves.Remove(stove.thingIDNumber);
                });
                return stove;
            }
            return null;
        }

        /// <summary>What the prison remembers of the kitchen's stoves and bills, and the bills' targets, is put back by the rollback.</summary>
        private static void KeepKitchenMemory(Map map, PrisonMap pm, Rollback r)
        {
            List<int> billed0 = pm.billedStoves.ToList();
            Dictionary<string, int> made0 = new Dictionary<string, int>(pm.cookBills);
            bool remembered0 = pm.stovesRemembered;
            List<(Bill_Production bill, int target)> targets0 = InmateJobWorker_Kitchen.KitchenStoves(map, pm).SelectMany(b => ((IBillGiver)b).BillStack.Bills.OfType<Bill_Production>()).Select(b => (b, b.targetCount)).ToList();
            r.Add(() =>
            {
                pm.billedStoves.Clear();
                pm.billedStoves.AddRange(billed0);
                pm.cookBills.Clear();
                foreach (KeyValuePair<string, int> kv in made0) pm.cookBills[kv.Key] = kv.Value;
                foreach ((Bill_Production bill, int target) in targets0) bill.targetCount = target;
                pm.stovesRemembered = remembered0;
            });
        }

        private static void Kitchenize(Building stove, PrisonMap pm)
        {
            foreach (IntVec3 c in stove.OccupiedRect().Cells) pm.zones.Set(c, RPLaborDefOf.RP_Kitchen);
        }

        private static void CheckFixKitchen(Map map)
        {
            Guarded("first hour fixes H3/M7: the kitchen's bills", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                PrisonGame g = PrisonGame.Instance;
                KeepKitchenMemory(map, pm, r);
                KitchenMenu menu0 = g.kitchenMenu;
                r.Add(() => g.kitchenMenu = menu0);
                g.kitchenMenu = KitchenMenu.Simple;
                pm.stovesRemembered = true;
                var taken = new List<IntVec3>();
                Building a = FixStove(map, pm, r, taken), b = FixStove(map, pm, r, taken);
                if (a == null || b == null)
                {
                    FirstHourFail("no free yard cells for two stoves");
                    return;
                }
                Kitchenize(a, pm);
                Kitchenize(b, pm);
                int beds = IntakeSystem.Capacity(map);
                KitchenBills.Ensure(map);
                var giverA = (IBillGiver)a;
                var giverB = (IBillGiver)b;
                if (giverA.BillStack.Bills.FirstOrDefault() is not Bill_Production billA || giverB.BillStack.Bills.FirstOrDefault() is not Bill_Production billB)
                {
                    FirstHourFail("the two new stoves of the kitchen got no bills");
                    return;
                }
                // H3: a menu change gives each copy a number of its own, and the prison's memory of the bill's target follows it (a bill that is left as made keeps following the beds).
                int targetA = billA.targetCount;
                RecipeDef fine = KitchenMenuPolicy.Recipe(KitchenMenu.Fine);
                Bill_Production newA = KitchenMenuPolicy.Replace(giverA, billA, fine, pm), newB = KitchenMenuPolicy.Replace(giverB, billB, fine, pm);
                string idA = newA.GetUniqueLoadID(), idB = newB.GetUniqueLoadID();
                if (idA == idB || idA.EndsWith("_-1") || idB.EndsWith("_-1")) FirstHourFail($"two bills swapped by a menu change are \"{idA}\" and \"{idB}\" (one number for two bills in a save)");
                if (!pm.cookBills.TryGetValue(idA, out int remembered) || remembered != targetA || pm.cookBills.ContainsKey(billA.GetUniqueLoadID())) FirstHourFail("the prison's memory of a swapped bill's target did not move to the copy");
                // The same in a save that has the numbers lost already: renumbered by the next pass, the memory with them.
                BillLoadId.SetValue(newA, -1);
                BillLoadId.SetValue(newB, -1);
                pm.cookBills.Remove(idA);
                pm.cookBills.Remove(idB);
                pm.cookBills[newA.GetUniqueLoadID()] = targetA;
                KitchenBills.Ensure(map);
                if (newA.GetUniqueLoadID() == newB.GetUniqueLoadID() || newA.GetUniqueLoadID().EndsWith("_-1") || newB.GetUniqueLoadID().EndsWith("_-1")) FirstHourFail("bills with no number of their own were not numbered again");
                if (!pm.cookBills.ContainsKey(newA.GetUniqueLoadID()) && !pm.cookBills.ContainsKey(newB.GetUniqueLoadID())) FirstHourFail("the memory of a bill's target was lost when its number was mended");
                // M7: a bill of the warden's own is remembered too: deleted, it stays deleted.
                pm.billedStoves.Remove(a.thingIDNumber);
                KitchenBills.Ensure(map);
                if (!pm.billedStoves.Contains(a.thingIDNumber)) FirstHourFail("a stove with a bill of the warden's was not remembered");
                giverA.BillStack.Delete(newA);
                if (KitchenBills.Ensure(map) != 0 || giverA.BillStack.Count != 0) FirstHourFail("a bill the warden made and deleted was made again by the prison");
                // M7: a save from before stoves were billed keeps its stoves as they are (unbilled on purpose: only inmates cook there); a stove put up later is billed.
                giverB.BillStack.Delete(newB);
                pm.billedStoves.Remove(b.thingIDNumber);
                pm.stovesRemembered = false;
                KitchenBills.Ensure(map);
                if (giverB.BillStack.Count != 0 || !pm.billedStoves.Contains(b.thingIDNumber) || !pm.stovesRemembered) FirstHourFail("an old save's unbilled stove was billed at the first hour (or not remembered)");
                Building c = FixStove(map, pm, r, taken);
                if (c == null) FirstHourFail("no free yard cell for a third stove");
                else
                {
                    Kitchenize(c, pm);
                    int made = KitchenBills.Ensure(map);
                    if (made != 1 || ((IBillGiver)c).BillStack.Bills.FirstOrDefault() is not Bill_Production billC) FirstHourFail($"a stove put up after the old save's was loaded got {made} bills, not one");
                    else
                    {
                        // M7: from 24 beds the prison's own bill goes bulk, the target with it.
                        RecipeDef single = billC.recipe;
                        KitchenBills.Ensure(map, KitchenBills.BulkFromBeds + 6);
                        Bill_Production now = ((IBillGiver)c).BillStack.Bills.OfType<Bill_Production>().FirstOrDefault();
                        if (now == null || now.recipe != KitchenMenuPolicy.BulkOf(single) || now.targetCount != KitchenBills.TargetFor(KitchenBills.BulkFromBeds + 6)) FirstHourFail($"a bill left as made did not go bulk at {KitchenBills.BulkFromBeds + 6} beds ({now?.recipe?.defName} {now?.targetCount})");
                        // ...and one the warden edited does not.
                        if (now != null)
                        {
                            now.targetCount += 3;
                            RecipeDef before = now.recipe;
                            KitchenBills.Ensure(map, 60);
                            if (((IBillGiver)c).BillStack.Bills.OfType<Bill_Production>().FirstOrDefault() is not { } still || still.recipe != before || still.targetCount != now.targetCount) FirstHourFail("a bill the warden edited was changed by the prison");
                        }
                    }
                }
                // M7: with the fine menu and nobody who can cook it, a new stove gets the simple meal and the warden is told; with a cook who can, the fine one.
                StaffRoleDef cookRole = DefDatabase<StaffRoleDef>.GetNamed("RP_Cook");
                Pawn low = ThrowawayStaff(map, r, cookRole), high = ThrowawayStaff(map, r, cookRole);
                low.skills.GetSkill(SkillDefOf.Cooking).Level = 3;
                high.skills.GetSkill(SkillDefOf.Cooking).Level = 10;
                RecipeDef without = KitchenBills.RecipeForStove(map, KitchenMenu.Fine, beds, out bool said, new[] { low });
                RecipeDef with = KitchenBills.RecipeForStove(map, KitchenMenu.Fine, beds, out bool saidWith, new[] { low, high });
                if (without != KitchenBills.RecipeFor(KitchenMenu.Simple, beds) || !said) FirstHourFail($"with the fine menu and no cook at Cooking 6 a new stove's bill is for {without?.defName} (told: {said})");
                if (with != KitchenBills.RecipeFor(KitchenMenu.Fine, beds) || saidWith) FirstHourFail($"with the fine menu and a cook at Cooking 10 a new stove's bill is for {with?.defName}");
                // M7: what the prison remembers of stoves and bills that are gone is forgotten.
                pm.billedStoves.Add(987654321);
                pm.cookBills["Bill_Gone_987654321"] = 5;
                KitchenBills.Ensure(map);
                if (pm.billedStoves.Contains(987654321) || pm.cookBills.ContainsKey("Bill_Gone_987654321")) FirstHourFail("a stove and a bill that are gone were not forgotten");
            });
        }

        // ---- H4: a goal that would name a gang nobody has found ---------------------------------------------------------------------------------

        private static void CheckFixWants(Map map)
        {
            Guarded("first hour fix H4: no screen names an undiscovered gang through a shared grudge", map, r =>
            {
                r.Add(RememberGoals(map.mapPawns.PrisonersOfColonySpawned));
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.First();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn victim = Throwaway(map, r), mate = Throwaway(map, r), friend = Throwaway(map, r);
                CompInmate vc = victim.Inmate(), mc = mate.Inmate(), fc = friend.Inmate();
                (vc.gang, vc.gangKnown) = (gang, false);
                (mc.gang, mc.gangKnown) = (gang, false);
                (fc.gang, fc.gangKnown) = (null, false);
                foreach (CompInmate x in new[] { mc, fc })
                {
                    x.want = null;
                    x.staffMemories.Clear();
                }
                friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, victim);
                r.Add(() => friend.relations.TryRemoveDirectRelation(RPRelationDefOf.RP_Friend, victim));
                StaffMemories.Notify_ShotDead(victim, guard);
                if (mc.want?.def != RPWantDefOf.RP_Want_Score || fc.want?.def != RPWantDefOf.RP_Want_Score)
                {
                    FirstHourFail($"the shooting did not give the gang-mate and the friend a grudge ({mc.want?.def?.defName}, {fc.want?.def?.defName})");
                    return;
                }
                // The Inmates tab (the column, its sort and its filter), the inmate's file and the week's report show the friend's goal and not the gang-mate's.
                if (InmatesTab.ShownWant(mc) != null || Wants.Shown(mc) != null || InmatesTab.WantOrder(mc) != 0f) FirstHourFail("the Inmates tab shows the grudge of a member of a gang nobody has found");
                if (InmatesTab.ShownWant(fc) == null || Wants.Shown(fc) == null) FirstHourFail("the Inmates tab hides the grudge of a friend");
                List<string> goals = WeeklyReport.NearestGoals(map, 99);
                // (A goal line opens with its inmate's name; a name elsewhere in it is somebody else's, a want's partner or target ("Iosif - stay in touch with family (partner Colin)") that a throwaway may share by chance.)
                bool Names(string line, Pawn who) => line.StartsWith(who.LabelShort + " - ", System.StringComparison.OrdinalIgnoreCase);
                if (goals.Any(line => Names(line, mate))) FirstHourFail($"the week's report names the gang-mate with a grudge in an undiscovered gang ({mate.LabelShort}: {string.Join(" | ", goals.Where(line => Names(line, mate)))})");
                if (!goals.Any(line => Names(line, friend))) FirstHourFail("the week's report leaves out the friend's grudge");
                // Found, the same goal is shown like any other.
                mc.gangKnown = true;
                if (InmatesTab.ShownWant(mc) == null || !WeeklyReport.NearestGoals(map, 99).Any(line => Names(line, mate))) FirstHourFail("a known gang member's grudge is not shown");
            });
        }

        // ---- M1: the morgue -----------------------------------------------------------------------------------------------------------------------

        private static Corpse FixCorpse(Map map, Rollback r, Pawn body, IntVec3 at)
        {
            if (body.Spawned) body.DeSpawn();
            Corpse corpse = (Corpse)ThingMaker.MakeThing(body.RaceProps.corpseDef);
            corpse.InnerPawn = body;
            GenSpawn.Spawn(corpse, at, map);
            r.Add(() =>
            {
                if (!corpse.Destroyed) corpse.Destroy();
                if (!body.Destroyed) body.Destroy(DestroyMode.Vanish);
            });
            return corpse;
        }

        private static void CheckFixMorgue(Map map)
        {
            Guarded("first hour fix M1: the morgue and storage that takes bodies", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                var giver = new WorkGiver_HaulToMorgue { def = DefDatabase<WorkGiverDef>.GetNamed("RP_HaulToMorgue") };
                List<IntVec3> morgue = pm.zones.CellsOf(RPMorgueDefOf.RP_Morgue).ToList();
                if (morgue.Count < 3)
                {
                    FirstHourFail("the check needs a morgue of three cells");
                    return;
                }
                // A body in a stockpile that takes corpses is left there (vanilla carries it in and the morgue's guards would carry it out, for as long as both stood).
                IntVec3 spot = pm.zones.CellsOf(RPDefOf.RP_Yard).First(c => c.Standable(map) && c.GetThingList(map).Count == 0);
                Corpse stored = FixCorpse(map, r, Throwaway(map, r), spot);
                if (!giver.HasJobOnThing(guard, stored)) FirstHourFail("the check's own premise: a body lying in the yard has no job to carry it to the morgue");
                var stockpile = new Zone_Stockpile(StorageSettingsPreset.CorpseStockpile, map.zoneManager);
                map.zoneManager.RegisterZone(stockpile);
                stockpile.AddCell(spot);
                r.Add(() =>
                {
                    if (map.zoneManager.AllZones.Contains(stockpile)) stockpile.Delete();
                });
                if (!StoreUtility.IsInValidStorage(stored)) FirstHourFail("the check's own premise: the corpse stockpile takes the body");
                else
                {
                    if (giver.HasJobOnThing(guard, stored)) FirstHourFail("the morgue's guards would carry a body out of a stockpile that takes it");
                    if (!Morgue.Settled(stored, pm) || Morgue.Uncollected(map, pm) != Morgue.Uncollected(map, pm, 0) || LooseBodies(map, pm).Contains(stored)) FirstHourFail("a body in a stockpile that takes it counts as uncollected");
                }
                stockpile.Delete();
                if (!giver.HasJobOnThing(guard, stored)) FirstHourFail("the body has no job again once the stockpile is gone");
                // A dog of the player's faction is not the prison's dead.
                Pawn dog = PawnGenerator.GeneratePawn(new PawnGenerationRequest(DefDatabase<PawnKindDef>.GetNamed("Husky"), Faction.OfPlayer));
                r.Add(() =>
                {
                    if (!dog.Destroyed) dog.Destroy(DestroyMode.Vanish);
                });
                Corpse dogBody = FixCorpse(map, r, dog, spot);
                if (Morgue.Ours(dogBody)) FirstHourFail("the body of a dog of the player's faction is the prison's own (it would fill the morgue)");
                Morgue.Reset();
                // A full morgue: nothing to carry to, and the list says bodies lie outside it once they have for an hour.
                var filler = new List<Corpse>();
                foreach (IntVec3 c in morgue.Where(x => x.Standable(map) && x.GetFirstThing<Corpse>(map) == null))
                    filler.Add(FixCorpse(map, r, Throwaway(map, r), c));
                Morgue.Reset();
                if (!Morgue.Full(map, pm) || giver.HasJobOnThing(guard, stored)) FirstHourFail("a morgue with a body on every cell still has a job to carry another one in");
                if (PrisonDiagnostics.Problems(map).Contains("RP_Problem_BodiesStuck".Translate(Morgue.Uncollected(map, pm, Morgue.StuckAfterTicks)).Resolve()) && Morgue.Uncollected(map, pm, Morgue.StuckAfterTicks) > 0)
                    FirstHourFail("a body that has lain outside the morgue for no time at all is on the list as stuck");
                stored.timeOfDeath = Find.TickManager.TicksGame - 2 * GenDate.TicksPerHour;
                int stuck = Morgue.Uncollected(map, pm, Morgue.StuckAfterTicks);
                if (stuck < 1 || !PrisonDiagnostics.Problems(map).Contains("RP_Problem_BodiesStuck".Translate(stuck).Resolve())) FirstHourFail("a body outside a full morgue for two hours is not on the list of what to fix");
                filler[0].Destroy();
                Morgue.Reset();
                if (Morgue.Full(map, pm)) FirstHourFail("the morgue is full with a cell free");
            });
        }

        private static IEnumerable<Corpse> LooseBodies(Map map, PrisonMap pm) =>
            map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Where(c => Morgue.Ours(c) && !Morgue.Settled(c, pm));

        // ---- M5: the to-do list ---------------------------------------------------------------------------------------------------------------

        private static void CheckFixList(Map map)
        {
            Guarded("first hour fix M5: the to-do list follows the welcome letter and opens the right screen", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                // Without a pad the list begins with it; without holding and a yard it asks for holding first and the yard last (the letter's order).
                List<Building> pads = ShuttleSystem.Pads(map).ToList();
                var padPlaces = pads.Select(p => (p, p.Position, p.Rotation)).ToList();
                foreach (Building p in pads) p.DeSpawn();
                r.Add(() =>
                {
                    foreach ((Building p, IntVec3 at, Rot4 rot) in padPlaces)
                        if (!p.Spawned) GenSpawn.Spawn(p, at, map, rot);
                });
                List<IntVec3> holding = pm.zones.CellsOf(RPDefOf.RP_Holding).ToList(), yard = pm.zones.CellsOf(RPDefOf.RP_Yard).ToList();
                r.Add(() =>
                {
                    foreach (IntVec3 c in holding) pm.zones.Set(c, RPDefOf.RP_Holding);
                    foreach (IntVec3 c in yard) pm.zones.Set(c, RPDefOf.RP_Yard);
                });
                foreach (IntVec3 c in holding) pm.zones.Set(c, null);
                foreach (IntVec3 c in yard) pm.zones.Set(c, null);
                List<string> lines = PrisonDiagnostics.Problems(map).ToList();
                string pad = "RP_Problem_NoPad".Translate().Resolve(), holdingLine = "RP_Problem_NoZone".Translate(RPDefOf.RP_Holding.label).Resolve(), yardLine = "RP_Problem_NoZone".Translate(RPDefOf.RP_Yard.label).Resolve();
                if (padPlaces.Count > 0 && lines.IndexOf(pad) != 0) FirstHourFail($"a prison with no shuttle pad does not begin its list with the pad (at {lines.IndexOf(pad)})");
                if (lines.IndexOf(holdingLine) is var h && (h < 0 || h > 1)) FirstHourFail($"the list asks for holding at {h}: after the pad and before everything else");
                if (lines.IndexOf(yardLine) < lines.IndexOf(holdingLine)) FirstHourFail("the list asks for the yard before holding");
                foreach (Building p in pads) GenSpawn.Spawn(p, p.Position, map, p.Rotation);
                if (PrisonDiagnostics.Problems(map).Contains(pad)) FirstHourFail("the list still asks for a pad with one standing");
                // The lines that say what to hire open Staff, the others Rooms; the hunger line follows what it blames.
                PrisonServices.Report few = new PrisonServices.Report { inmates = 100, cooks = 1, counters = 99, seats = 999, showers = 99 };
                var items = PrisonServices.ItemsOf(few).ToList();
                (string cooksText, MainTabWindow_Prison.Tab? cooksTab) = items.FirstOrDefault(i => i.text == "RP_Problem_FewCooks".Translate(1, 100, PrisonServices.InmatesPerCook).Resolve());
                if (cooksText == null || cooksTab != MainTabWindow_Prison.Tab.Staff) FirstHourFail($"the line about too few cooks opens {cooksTab?.ToString() ?? "nothing"}, not Staff");
                if (items.Any(i => i.text != cooksText && i.tab != MainTabWindow_Prison.Tab.Rooms)) FirstHourFail("a line about the services that is not about cooks does not open Rooms");
                Pawn hungry = Throwaway(map, r);
                hungry.needs.food.CurLevelPercentage = 0.02f;
                ThingDef mealDef = ThingDefOf.MealSimple;
                IntVec3 canteen = pm.zones.CellsOf(RPDefOf.RP_Canteen).First(c => c.Standable(map) && c.GetThingList(map).Count == 0);
                PrisonDiagnostics.Item bare = PrisonDiagnostics.Items(map).FirstOrDefault(i => i.text.Contains("going hungry"));
                if (bare.text == null) FirstHourFail("an inmate at 2% food is not on the list as hungry");
                else if (PrisonServices.MealsInCanteen(map, pm) == 0 && bare.tab != MainTabWindow_Prison.Tab.Supply) FirstHourFail($"hunger with bare counters opens {bare.tab}, not Supply");
                Thing meal = ThingMaker.MakeThing(mealDef);
                GenSpawn.Spawn(meal, canteen, map);
                r.Add(() =>
                {
                    if (!meal.Destroyed) meal.Destroy();
                });
                PrisonDiagnostics.Item fed = PrisonDiagnostics.Items(map).FirstOrDefault(i => i.text.Contains("going hungry"));
                if (fed.text != null && fed.tab != MainTabWindow_Prison.Tab.Rooms) FirstHourFail($"hunger with meals on the counters (the way to them is the problem) opens {fed.tab}, not Rooms");
                // The overview's card and list are kept per map: another map's are never shown for two seconds after the switch.
                var cache = new OverviewCache();
                cache.Stamp(map, 100);
                if (cache.Stale(map, 110) || !cache.Stale(map, 100 + OverviewCache.Frames + 1) || !cache.Stale(null, 110)) FirstHourFail("the overview's cache is stale for the same map and fresh after a long time");
                var other = new Map();
                if (!cache.Stale(other, 110)) FirstHourFail("the overview's cache is kept across a change of map");
            });
        }

        // ---- M6: the case's informant ---------------------------------------------------------------------------------------------------------

        private static void CheckFixInformant(Map map)
        {
            Guarded("first hour fix M6: the case's letter asks the informant it names", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkiller92"), victim = s.Inmate("Zvictim92"), witness = s.Inmate("Zwitness92"), informant = s.Inmate("Zsnitch92");
                CompInmate ic = informant.Inmate();
                ic.reputations.Add(ReputationDefOf.RP_Snitch);
                ic.revealed.Add(ReputationDefOf.RP_Snitch);
                ic.informantQuit = false;
                MurderTrial t = s.Case(killer, victim);
                s.Witness(t, witness);
                int before = r.NewLetters.Count();
                Trials.Assemble(s.g, map, t);
                if (r.NewLetters.Skip(before).FirstOrDefault() is not ChoiceLetter_PrisonDecision d || !d.Is("RP_Decision_TrialCase"))
                {
                    FirstHourFail("assembling a case sent no case letter");
                    return;
                }
                if (d.subjectId != informant.thingIDNumber) FirstHourFail($"the case's letter names {d.subjectId} as its informant, not {informant.thingIDNumber}");
                int ask = LetterOption(d, "RP_TrialCase_Both");
                // The letter's people in another order (a save drops the destroyed from the list): the informant is still the one it names.
                d.pawns.Reverse();
                d.decision.Worker.Choose(d, map, ask);
                if (t.informantId != informant.thingIDNumber) FirstHourFail($"asking the informant asked {t.informantId} (a witness, not {informant.thingIDNumber}) when the list of the letter's people was in another order");
                if (!Trials.KnownSpeakers(t, map).Contains(informant.LabelShort)) FirstHourFail("the court-sits letter does not list an informant who agreed to testify among the speakers");
                // The informant is gone (transferred out): the letter's choice to ask says so, and nobody else is exposed in their place.
                t.informantId = -1;
                t.informantName = null;
                d.pawns.Remove(informant);
                if (d.decision.Worker.Disabled(d, ask) == null) FirstHourFail("a letter whose informant has gone still offers to ask them");
                d.decision.Worker.Choose(d, map, ask);
                if (t.informantId >= 0) FirstHourFail($"with the informant gone, the letter's \"ask\" made {t.informantId} the informant");
            });
        }

        // ---- M4, L1: the grade line, empty prisons, funds ---------------------------------------------------------------------------------------

        private static void CheckFixGrades(Map map)
        {
            Guarded("first hour fixes M4/L1: the bad-grade line, an empty prison, the penitentiary's funds", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                DifficultyDef difficulty0 = Find.Storyteller.difficultyDef;
                List<float> history0 = g.gradeHistory.ToList(), rules0 = g.gradeRules.ToList();
                List<ReportCard> cards0 = g.gradeCards.ToList();
                float lastGrade0 = g.lastGrade, reputation0 = g.reputation;
                r.Add(() =>
                {
                    Find.Storyteller.difficultyDef = difficulty0;
                    g.gradeHistory.Clear();
                    g.gradeHistory.AddRange(history0);
                    g.gradeRules.Clear();
                    g.gradeRules.AddRange(rules0);
                    g.gradeCards.Clear();
                    g.gradeCards.AddRange(cards0);
                    g.lastGrade = lastGrade0;
                    g.reputation = reputation0;
                });
                // The words follow the difficulty: at Extreme "below 65 points", not "D or F"; at the neutral level the letters are named.
                Find.Storyteller.difficultyDef = Level("Extreme");
                string extreme = SpecialMeasures.BadGradeText, extremeIntro = PrisonGame.IntroText("x", 9, null), extremeCause = "RP_MeasuresCause_Grades".Translate(SpecialMeasures.BadDays, SpecialMeasures.BadGradeText).Resolve();
                if (!extreme.Contains("65") || !extremeIntro.Contains(extreme) || !extremeCause.Contains("65") || extremeIntro.Contains("D or F") || extremeCause.Contains("D or F") || !SpecialMeasures.GoodGradeText.Contains("65"))
                    FirstHourFail($"at Extreme the texts say \"{extreme}\" / \"{SpecialMeasures.GoodGradeText}\" and the welcome letter {(extremeIntro.Contains("D or F") ? "still says D or F" : "")}");
                Find.Storyteller.difficultyDef = Level("Rough");
                if (!SpecialMeasures.BadGradeText.Contains("55") || !SpecialMeasures.BadGradeText.Contains("D or an F") || !SpecialMeasures.GoodGradeText.Contains("C or better")) FirstHourFail($"at Rough the texts say \"{SpecialMeasures.BadGradeText}\" / \"{SpecialMeasures.GoodGradeText}\"");
                // The pane says what the level does to mood (the game's own setting for it), and the penitentiary's funds.
                if (!PrisonDifficulty.Describe(Level("Extreme")).Contains("-10")) FirstHourFail("the difficulty pane does not say that Extreme takes ten points off everybody's mood");
                int penitentiary = PrisonDifficulty.StartingFunds(Level("Extreme"), penitentiary: true);
                if (penitentiary != Mathf.RoundToInt(PrisonBuilder.PenStartFunds * 0.7f) || !PrisonDifficulty.Describe(Level("Extreme"), true).Contains(Money.Format(penitentiary)) || penitentiary == PrisonDifficulty.StartingFunds(Level("Extreme")))
                    FirstHourFail($"the penitentiary's starting funds at Extreme are {penitentiary} and the pane says {PrisonDifficulty.Describe(Level("Extreme"), true)}");
                // A day keeps the line it was graded under: three 60s are bad days graded under Extreme's line, and not under Rough's.
                g.gradeHistory.Clear();
                g.gradeRules.Clear();
                Find.Storyteller.difficultyDef = Level("Extreme");
                for (int i = 0; i < 3; i++) g.RecordGrade(60f);
                Find.Storyteller.difficultyDef = Level("Rough");
                if (!g.BadDay(2) || SpecialMeasures.GradeRun(g) != 0) FirstHourFail("days graded 60 under Extreme's line of 65 are not bad days after the difficulty is changed to Rough");
                g.gradeHistory.Clear();
                g.gradeRules.Clear();
                for (int i = 0; i < 3; i++) g.RecordGrade(60f);
                Find.Storyteller.difficultyDef = Level("Extreme");
                if (g.BadDay(2) || SpecialMeasures.GradeRun(g) != 2) FirstHourFail("days graded 60 under Rough's line of 55 became bad days when the difficulty was changed to Extreme");
                // A save from before the line moved has the neutral one for all its days.
                g.gradeHistory.Clear();
                g.gradeRules.Clear();
                g.gradeHistory.AddRange(new[] { 50f, 58f, 62f });
                g.AlignGradeRules();
                if (g.gradeRules.Count != 3 || g.gradeRules.Any(x => x != 55f) || !g.BadDay(0) || g.BadDay(1)) FirstHourFail($"an old save's days were not given the neutral line ({string.Join(",", g.gradeRules)})");
                // An empty prison has no grade: nothing goes into the history, the reputation or the last grade.
                Find.Storyteller.difficultyDef = Level("Rough");
                g.gradeHistory.Clear();
                g.gradeRules.Clear();
                g.lastGrade = -1f;
                g.reputation = 50f;
                var empty = new ReportCard { inmates = 0, safety = 100, security = 100, health = 100, conditions = 100, order = 100, reform = 100 };
                bool emptyGraded = DailySettlement.NoteGrade(g, empty);
                DailySettlement.NoteReputation(g, empty);
                if (emptyGraded || g.gradeHistory.Count != 0 || g.lastGrade != -1f || g.reputation != 50f) FirstHourFail("an empty prison's day was graded, entered in the history or moved the reputation");
                var some = new ReportCard { inmates = 5, safety = 100, security = 100, health = 100, conditions = 100, order = 100, reform = 100 };
                bool someGraded = DailySettlement.NoteGrade(g, some);
                DailySettlement.NoteReputation(g, some);
                if (!someGraded || g.gradeHistory.Count != 1 || g.reputation <= 50f) FirstHourFail("a prison with inmates was not graded");
            });
        }

        // ---- L1 -----------------------------------------------------------------------------------------------------------------------------------

        private static void CheckFixSmall(Map map)
        {
            Guarded("first hour fix L1: the small things", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                // A decision of a version of the game that had two more is not lost: it becomes the plain notice of an arc that is over, or none.
                foreach (string removed in new[] { "RP_Decision_TrialWitnesses", "RP_Decision_TrialInformant" })
                    if (ChoiceLetter_PrisonDecision.ResolveDecision(removed)?.defName != "RP_Decision_Arc") FirstHourFail($"a letter for the removed decision {removed} does not load as a plain notice");
                if (ChoiceLetter_PrisonDecision.ResolveDecision("RP_Decision_Inspection")?.defName != "RP_Decision_Inspection" || ChoiceLetter_PrisonDecision.ResolveDecision("null") != null) FirstHourFail("a decision that exists is not found by name, or \"null\" is");
                // Vanilla's notes held back in a prison are taken off the helper's list in a save that has them.
                ConceptDef vanilla = DefDatabase<ConceptDef>.AllDefsListForReading.FirstOrDefault(c => !Concepts.Ours(c) && !PlayerKnowledgeDatabase.IsComplete(c));
                ConceptDef ours = ConceptDef.Named(Concepts.Pad);
                LearningReadout readout = Find.Tutor.learningReadout;
                if (vanilla == null) FirstHourFail("no vanilla note left to check with");
                else
                {
                    readout.TryActivateConcept(vanilla);
                    readout.TryActivateConcept(ours);
                    if (!readout.IsActive(vanilla) || Concepts.DropVanillaNotes(readout) < 1 || readout.IsActive(vanilla) || !readout.IsActive(ours)) FirstHourFail("a vanilla note on the helper's list was not dropped, or the prison's was");
                }
                // An inspection or a case's letter held back shows as held on the Intel screen, not as "no letter waiting".
                List<ChoiceLetter_PrisonDecision> deferred0 = g.story.deferredLetters;
                int decisions0 = g.story.decisionsToday;
                r.Add(() =>
                {
                    g.story.deferredLetters = deferred0;
                    g.story.decisionsToday = decisions0;
                    StoryLetters.TestBudget = false;
                });
                g.story.deferredLetters = new List<ChoiceLetter_PrisonDecision>();
                StoryLetters.TestBudget = true;
                g.story.decisionsToday = 99;
                ChoiceLetter_PrisonDecision held = ChoiceLetter_PrisonDecision.Make("RP_Decision_Complaint", "fix test", "fix test", map);
                held.amount = 424242;
                held.StartTimeout(GenDate.TicksPerDay);
                Find.LetterStack.ReceiveLetter(held);
                if (IntelTab.OpenDecision("RP_Decision_Complaint", d => d.amount == 424242) != null || !IntelTab.HeldDecision("RP_Decision_Complaint", d => d.amount == 424242)) FirstHourFail("a held letter is not shown as held (or is shown as on the desk)");
                StoryLetters.NewDay(g);
                StoryLetters.Daily(g, map);
                if (IntelTab.OpenDecision("RP_Decision_Complaint", d => d.amount == 424242) == null || IntelTab.HeldDecision("RP_Decision_Complaint", d => d.amount == 424242)) FirstHourFail("a delivered letter is still shown as held");
                Find.LetterStack.RemoveLetter(held);
                // The bureaucracy is stalled for an administrator who is suspended and for a desk nobody may use.
                Pawn admin = StaffCommute.AllStaff(map).FirstOrDefault(p => p.GetComp<CompStaff>()?.role?.workTypes.Contains(RPAdminDefOf.RP_Administration) == true);
                Building desk = map.listerBuildings.AllBuildingsColonistOfDef(RPAdminDefOf.RP_OfficeDesk).FirstOrDefault();
                if (admin == null || desk == null) FirstHourFail("no administrator or no desk to check the stall with");
                else
                {
                    int admins = BureaucracySystem.Administrators(map), desks = BureaucracySystem.Desks(map);
                    CompStaff staff = admin.GetComp<CompStaff>();
                    int suspended0 = staff.suspendedUntilTick;
                    bool forbidden0 = desk.IsForbidden(Faction.OfPlayer);
                    r.Add(() =>
                    {
                        staff.suspendedUntilTick = suspended0;
                        desk.SetForbidden(forbidden0, warnOnFail: false);
                    });
                    staff.suspendedUntilTick = Find.TickManager.TicksGame + 5 * GenDate.TicksPerDay;
                    if (BureaucracySystem.Administrators(map) != admins - 1) FirstHourFail("a suspended administrator is counted as one who could do the paperwork");
                    staff.suspendedUntilTick = suspended0;
                    if (BureaucracySystem.Desks(map) != desks) FirstHourFail("a desk is lost to the count with nothing wrong with it");
                }
                // A stowaway's first escape notes the first without a second line for the same event.
                bool first0 = g.story.firsts.Contains("escape");
                r.Add(() =>
                {
                    if (first0) g.story.firsts.Add("escape");
                    else g.story.firsts.Remove("escape");
                });
                g.story.firsts.Remove("escape");
                int lines = g.story.entries.Count;
                Story.MarkFirst("escape");
                Story.RecordFirst("escape", RPChronicleDefOf.RP_Chr_Security, "RP_Chr_FirstEscape".Translate("x"), null);
                if (g.story.entries.Count != lines || !g.story.firsts.Contains("escape")) FirstHourFail("noting a first without a line wrote one, or did not note it");
                // The lockdown button asks "nothing is happening" only when nothing is: an assault or a gang war under way is something.
                var war = new StoryArc { def = RPGangWarDefOf.RP_Arc_GangWar, id = g.story.nextArcId++, stage = GangWarArc.Declared };
                int nextArc0 = g.story.nextArcId - 1;
                g.story.arcs.Add(war);
                r.Add(() =>
                {
                    g.story.arcs.Remove(war);
                    g.story.nextArcId = nextArc0;
                });
                bool quiet = PrisonHUD.FightingOn(g, map);
                war.stage = GangWarArc.TitForTat;
                bool fighting = PrisonHUD.FightingOn(g, map);
                war.done = true;
                bool over = PrisonHUD.FightingOn(g, map);
                if (quiet || !fighting || over) FirstHourFail($"a gang war is {(quiet ? "fighting when only declared" : "")}{(!fighting ? "not fighting once it has begun" : "")}{(over ? " fighting when over" : "")} to the lockdown dialog");
                Pawn brawler = Throwaway(map, r);
                if (brawler.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_AttackingInmate, null, forced: true))
                {
                    if (!PrisonHUD.FightingOn(g, map)) FirstHourFail("an inmate attacking another is not an emergency to the lockdown dialog");
                    brawler.mindState.mentalStateHandler.Reset();
                }
                // The tabs say where the programs are.
                if (DefDatabase<MainButtonDef>.GetNamed("RP_Reports").description.Contains("programs") || !DefDatabase<MainButtonDef>.GetNamed("RP_Policy").description.Contains("programs")) FirstHourFail("the Reports and Policy buttons' descriptions put the programs on the wrong screen");
            });
        }

        // ---- the round trip through a save ---------------------------------------------------------------------------------------------------

        private static bool fixSeeded;

        /// <summary>
        /// The saveload plan: a letter held back for the next day with the tick it was put off and the informant it names, a day of the history with a line of its own, and the kitchen's memory
        /// of its stoves unset (a save from before has it so): the snapshot compares them, and after the reload they go.
        /// </summary>
        private static void SeedFixesForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            ChoiceLetter_PrisonDecision held = ChoiceLetter_PrisonDecision.Make("RP_Decision_Arc", "seeded", "seeded", map);
            held.deferredTimeout = 12345;
            held.heldTick = Find.TickManager.TicksGame - 777;
            held.subjectId = 4242;
            g.story.deferredLetters.Add(held);
            g.gradeHistory.Add(61f);
            g.gradeRules.Add(65f);
            g.gradeCards.Add(new ReportCard());
            pm.stovesRemembered = false;
            fixSeeded = true;
            RPLog.Message("[RimPrison:test] saveload fixes: a held letter (held at -777, informant 4242), a day graded 61 under a line of 65, the stoves not remembered");
        }

        private static void CheckFixesAfterReload(Map map)
        {
            if (!fixSeeded) return;
            fixSeeded = false;
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            ChoiceLetter_PrisonDecision held = g.story.deferredLetters.LastOrDefault(d => d.subjectId == 4242);
            bool letter = held != null && held.deferredTimeout == 12345 && held.heldTick >= 0 && held.Is("RP_Decision_Arc");
            bool day = g.gradeHistory.Count > 0 && g.gradeHistory[g.gradeHistory.Count - 1] == 61f && g.gradeRules.Count == g.gradeHistory.Count && g.gradeRules[g.gradeRules.Count - 1] == 65f;
            RPLog.Message($"[RimPrison:test] saveload fixes: the held letter {(letter ? "intact" : "changed")}, the graded day {(day ? "intact" : "changed")}, the stoves' memory {(pm.stovesRemembered ? "set" : "unset")}");
            if (!letter) reloadProblems.Add("the letter held for the next day did not come back with its tick and its informant");
            if (!day) reloadProblems.Add("a day of the grade history lost its line in the round trip");
            if (pm.stovesRemembered) reloadProblems.Add("the kitchen's memory of its stoves was set by the round trip (a save that has none must read as unset)");
            if (held != null) g.story.deferredLetters.Remove(held);
            if (day)
            {
                g.gradeHistory.RemoveAt(g.gradeHistory.Count - 1);
                g.gradeRules.RemoveAt(g.gradeRules.Count - 1);
                g.gradeCards.RemoveAt(g.gradeCards.Count - 1);
            }
            pm.stovesRemembered = true;
        }

        // ---- the cook's DoBill loop -----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A staff cook is offered a stack of the kitchen's raw food that an inmate has reserved: vanilla does not make a colonist respect a prisoner's reservation, so both went for the same
        /// stack. A physical-interaction reservation (an inmate chewing) is vanilla's own test in <c>CanReserve</c> and always held. The check puts each claim on a stack beside a stove with a
        /// bill, asks the cook's work scan, starts the job it offers and ticks its driver once: a job that ends in the tick it started is the loop ("started 10 jobs in one tick").
        /// </summary>
        private static void CheckFixDoBill(Map map)
        {
            Guarded("first hour (DoBill): a cook is not sent for a stack an inmate has claimed", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                KeepKitchenMemory(map, pm, r);
                pm.stovesRemembered = true;
                // A stove of its own with its own bill (the starter's have things lying beside them that a cook is sent to haul off first), in the kitchen.
                Building stove = FixStove(map, pm, r, new List<IntVec3>());
                if (stove == null)
                {
                    FirstHourFail("no free yard cells for a stove");
                    return;
                }
                Kitchenize(stove, pm);
                stove.TryGetComp<CompRefuelable>()?.Refuel(stove.TryGetComp<CompRefuelable>().Props.fuelCapacity);
                KitchenBills.Ensure(map);
                var giver = (IBillGiver)stove;
                Bill_Production bill = giver.BillStack.Bills.OfType<Bill_Production>().FirstOrDefault(x => !x.suspended);
                if (bill == null)
                {
                    FirstHourFail("the check's stove in the kitchen got no bill");
                    return;
                }
                r.Add(() => KitchenShift.Reset());
                bill.repeatMode = BillRepeatModeDefOf.Forever;
                bill.ingredientSearchRadius = 20f;
                KitchenShift.Reset();
                // One stack of raw food beside the stove, and no other: everything else the bill could use is forbidden for the length of the check.
                // (Not on the cells beside the stove, where the bill's ingredients are put down: anything lying there is hauled off first.)
                IntVec3 spot = GenRadial.RadialCellsAround(stove.InteractionCell, 12f, true).Where(c => c.DistanceTo(stove.Position) >= 4f && c.Standable(map) && c.GetThingList(map).Count == 0
                    && pm.zones.ZoneAt(c) == RPDefOf.RP_Yard && map.reachability.CanReach(stove.InteractionCell, c, PathEndMode.Touch, TraverseParms.For(TraverseMode.PassDoors))).FirstOrDefault();
                if (!spot.IsValid)
                {
                    FirstHourFail("no free yard cell near the stove for the check's stack of raw food");
                    return;
                }
                pm.zones.Set(spot, RPLaborDefOf.RP_Kitchen);
                r.Add(() => pm.zones.Set(spot, RPDefOf.RP_Yard));
                Thing stack = ThingMaker.MakeThing(ThingDefOf.RawPotatoes);
                stack.stackCount = 30;
                GenSpawn.Spawn(stack, spot, map);
                r.Add(() =>
                {
                    if (!stack.Destroyed) stack.Destroy();
                });
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).ToList())
                    if (t != stack && t.Spawned && InmateJobWorker_Kitchen.RawFood(t) && !t.IsForbidden(Faction.OfPlayer))
                    {
                        t.SetForbidden(true, warnOnFail: false);
                        r.Add(() =>
                        {
                            if (t.Spawned) t.SetForbidden(false, warnOnFail: false);
                        });
                    }
                Pawn cook = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed("RP_Cook"));
                cook.DeSpawn();
                GenSpawn.Spawn(cook, stove.InteractionCell, map);
                Pawn inmate = Throwaway(map, r);
                r.Add(() =>
                {
                    map.reservationManager.ReleaseAllClaimedBy(inmate);
                    map.physicalInteractionReservationManager.ReleaseAllClaimedBy(inmate);
                    map.reservationManager.ReleaseAllClaimedBy(cook);
                });
                WorkGiver_Scanner scan = (WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("DoBillsCook").Worker;
                // (A failed search for ingredients is not repeated for 500-600 ticks: the check asks afresh each time.)
                Job Offered()
                {
                    bill.nextTickToSearchForIngredients = 0;
                    return scan.JobOnThing(cook, stove);
                }
                bool UsesStack(Job j) => j != null && j.def == JobDefOf.DoBill && (j.targetB.Thing == stack || (j.targetQueueB?.Any(q => q.Thing == stack) ?? false));
                Job baseline = Offered();
                if (!UsesStack(baseline))
                {
                    var chosenProbe = new List<ThingCount>();
                    bool foundProbe = (bool)AccessTools.Method(typeof(WorkGiver_DoBill), "TryFindBestBillIngredients").Invoke(null, new object[] { bill, cook, stove, chosenProbe, null });
                    bool usableProbe = (bool)AccessTools.Method(typeof(WorkGiver_DoBill), "IsUsableIngredient").Invoke(null, new object[] { stack, bill });
                    FirstHourFail($"the check's own premise: a cook next to a stove with a bill and one stack of raw food is offered {baseline?.def.defName} on {baseline?.targetB.ToStringSafe()} [ingredients found {foundProbe}: {string.Join(",", chosenProbe.Select(c => c.Thing.ToStringSafe() + "x" + c.Count))}, stack usable {usableProbe}, filter allows {bill.ingredientFilter.Allows(stack)}, in the bill's recipe {bill.recipe.ingredients.Any(i => i.filter.Allows(stack))}, region {stack.Position.GetRegion(map)?.id} root {stove.InteractionCell.GetRegion(map)?.id}] "
                                  + $"(bill {bill.LabelCap} should do now {bill.ShouldDoNow()}, allowed {bill.PawnAllowedToStartAnew(cook)}, skill {bill.recipe.FirstSkillRequirementPawnDoesntSatisfy(cook)?.minLevel}, usable {giver.UsableForBillsAfterFueling()}, "
                                  + $"fuel {stove.TryGetComp<CompRefuelable>()?.Fuel}, burning {stove.IsBurning()}, can reserve stove {cook.CanReserve(stove)}, stack {stack.stackCount} at {stack.Position} forbidden {stack.IsForbidden(cook)} can reserve {cook.CanReserve(stack)}, "
                                  + $"cook at {cook.Position} reach {cook.CanReach(stack, PathEndMode.Touch, Danger.Some)}, cooking {cook.skills.GetSkill(SkillDefOf.Cooking).Level}, work type disabled {cook.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamed("Cooking"))}, search radius {bill.ingredientSearchRadius}, next search {bill.nextTickToSearchForIngredients})");
                    return;
                }
                cook.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                // An inmate chewing from it (a physical-interaction reservation): vanilla's CanReserve refuses it to everybody.
                Job claim = JobMaker.MakeJob(RPLaborDefOf.RP_InmateCook, stove, stack);
                map.physicalInteractionReservationManager.Reserve(inmate, claim, stack);
                if (UsesStack(Offered())) FirstHourFail("a cook is offered a stack an inmate holds a physical-interaction reservation on");
                map.physicalInteractionReservationManager.ReleaseAllClaimedBy(inmate);
                // An inmate cook's claim on a part of it (their job reserves the stack for what it takes): the staff cook leaves it, as they leave a colleague's. Vanilla makes a colonist
                // ignore the reservation of a prisoner whose faction is hostile to the colony or who has none (and respects one of a neutral faction's).
                Faction hostile = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && f.HostileTo(Faction.OfPlayer) && f.def.humanlikeFaction);
                Faction faction0 = inmate.Faction;
                r.Add(() => inmate.SetFactionDirect(faction0));
                foreach ((string whose, Faction faction) in new[] { ("a hostile faction's", hostile), ("no faction's", null) })
                {
                    inmate.SetFactionDirect(faction);
                    map.reservationManager.ReleaseAllClaimedBy(inmate);
                    map.reservationManager.Reserve(inmate, claim, stack, 1, 3, null, errorOnFailed: false);
                    Job offered = Offered();
                    RPLog.Message($"[RimPrison:test] DoBill check, {whose} inmate's claim on {stack}: offered {offered?.def.defName} on {offered?.targetB.ToStringSafe()} queue {string.Join(",", offered?.targetQueueB?.Select(q => q.Thing.ToStringSafe()) ?? new string[0])}; claims on it {Patch_ReservationManager_CanReserve_InmateClaimsInKitchen.InmateClaim(map.reservationManager, stack, null)}; stack {stack.stackCount}, bill should do now {bill.ShouldDoNow()}");
                    if (UsesStack(offered))
                    {
                        FirstHourFail($"a staff cook is offered a stack of the kitchen's raw food that an inmate of {whose} cook has reserved (the work scan does not respect an inmate's claim)");
                        // What the job does when it is taken up anyway: the loop, if it is one.
                        int loops0 = JobLoopErrors;
                        cook.jobs.StartJob(offered, JobCondition.InterruptForced, fromQueue: false);
                        cook.jobs.curDriver?.DriverTick();
                        RPLog.Message($"[RimPrison:test] DoBill with {whose} inmate's claim on the stack, taken up anyway: after one driver tick the cook does {cook.CurJobDef?.defName} (toil {cook.jobs.curDriver?.CurToilIndex}), job loop errors {JobLoopErrors - loops0}");
                        cook.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                    }
                }
                // Done with it, the stack is offered again.
                map.reservationManager.ReleaseAllClaimedBy(inmate);
                Job again = Offered();
                RPLog.Message($"[RimPrison:test] DoBill check, claim released: offered {again?.def.defName} on {again?.targetB.ToStringSafe()}; claims {Patch_ReservationManager_CanReserve_InmateClaimsInKitchen.InmateClaim(map.reservationManager, stack, null)}, can reserve {cook.CanReserve(stack)}, physical {map.physicalInteractionReservationManager.FirstReserverOf(stack)?.LabelShort}, reservers [{string.Join("; ", map.reservationManager.ReservationsReadOnly.Where(x => x.Target == stack).Select(x => x.Claimant.LabelShort))}]");
                if (!UsesStack(again)) FirstHourFail("the stack is not offered to the cook once the inmate's claim is released");
                cook.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                // The diagnostic says what it should of a DoBill that ends in the tick it began.
                Job probe = JobMaker.MakeJob(JobDefOf.DoBill, stove, stack);
                probe.bill = bill;
                probe.count = 5;
                string line = Patch_JobTracker_EndCurrentJob_DoBillSameTick.Describe(cook, probe, JobCondition.Incompletable, null);
                if (!line.Contains("DOBILL SAME TICK") || !line.Contains("stack 30 against the job's 5") || !line.Contains("physical interaction by nobody")) FirstHourFail($"the same-tick diagnostic leaves out what it should: {line}");
            });
        }
    }
}
