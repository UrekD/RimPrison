using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks for contraband made inside (hooch from stolen kitchen fruit, the smokeleaf patch in the garden) and for men and women kept apart (the cellmate term and the wing policy). Each is
    /// self-contained, on the quiet corner of <see cref="RingScene"/> (throwaway inmates and staff, no real ring, guard or find in play), runs no ticks (the passes are called by hand) and puts back
    /// what it touches. Each fails without its feature: the way it was shown is in the WORKLOG entry.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckInside(Map map)
        {
            Rand.PushState(20261027);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            int checksBegan = Find.TickManager.TicksGame; // (a real shuttle that lands while the checks tick brings inmates of its own: not theirs to put away)
            try
            {
                CheckInsideHoochFromKitchenFruit(map);
                CheckInsideHoochIsContrabandAndNeverSmuggledIn(map);
                CheckInsideGardenerSowsAndFeedsTheRing(map);
                CheckInsideGardenPatchIsFound(map);
                CheckInsideMenAndWomenAreKeptApartInACell(map);
                CheckInsideWingPolicyKeepsTheOtherSexOut(map);
                RunInsideRegressions(map);
            }
            finally
            {
                Rand.PopState();
            }
            RPLog.Message($"[RimPrison:test] inside: {problems0.Count} problems recorded after the checks (the last: {problems0.LastOrDefault()})");
            List<Pawn> leftBehind = LeftOnTheMap(map, pawnsBefore, checksBegan);
            if (leftBehind.Count > 0) problems0.Add($"contraband made inside and mixed cells checks left {string.Join(", ", leftBehind.Select(x => x.LabelShort))} on the map");
        }

        /// <summary>
        /// The state's marshals' prisoners step out at once, by the intake's own way in (no shuttle, no new inmates): what a landing does to them, for the checks that look at what the return did
        /// (the shuttle's own checks run a real landing).
        /// </summary>
        private static void LandMarshalsNow(PrisonGame g, Map map)
        {
            var perDay0 = new Dictionary<SecurityLevelDef, int>(g.intake.perDay);
            int number0 = g.intake.nextInmateNumber;
            foreach (SecurityLevelDef sec in perDay0.Keys.ToList()) g.intake.perDay[sec] = 0;
            try
            {
                IntakeSystem.RunIntake(g, map, silent: true);
            }
            finally
            {
                foreach (KeyValuePair<SecurityLevelDef, int> kv in perDay0) g.intake.perDay[kv.Key] = kv.Value;
                g.intake.nextInmateNumber = number0;
            }
        }

        private static void InsideFail(string text) => problems0.Add("inside: " + text);

        /// <summary>Paints these cells as a zone for the check (the old zone back at the end).</summary>
        private static void InsideZone(PrisonMap pm, Rollback r, PrisonZoneDef zone, IEnumerable<IntVec3> cells)
        {
            foreach (IntVec3 c in cells.ToList())
            {
                PrisonZoneDef old = pm.zones.ZoneAt(c);
                pm.zones.Set(c, zone);
                r.Add(() => pm.zones.Set(c, old));
            }
        }

        /// <summary>The check's own garden patches only (the real ones, if any, are put back).</summary>
        private static void InsideOwnPatches(PrisonMap pm, Rollback r)
        {
            List<GardenPatch> patches0 = pm.patches;
            r.Add(() => pm.patches = patches0);
            pm.patches = new List<GardenPatch>();
        }

        private static int StashCount(CompInmate c, ThingDef def) => c.stash.Where(t => t.def == def).Sum(t => t.stackCount);

        // ---- hooch ------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A kitchen worker nobody is watching pockets a piece of the kitchen's fruit and starts a mash in the cell stash (it needs fruit in the kitchen, and nobody watching); two days on, in the stash,
        /// the mash is hooch (two to a lot of mash), a drink and a drug like the rest; a mash that has not worked long enough is still mash.
        /// </summary>
        private static void CheckInsideHoochFromKitchenFruit(Map map)
        {
            Guarded("inside: hooch from stolen kitchen fruit", map, r =>
            {
                var s = new RingScene(map, r);
                PrisonMap pm = s.pm;
                Pawn cook = s.Inmate("Zcook840");
                CompInmate c = cook.Inmate();
                InmateJobDef kitchenJob = DefDatabase<InmateJobDef>.GetNamed("RP_JobKitchen");
                if (!kitchenJob.stealsFood) InsideFail("the kitchen job is not marked as one where food goes missing");
                // The kitchen: a few cells with fruit in it.
                InsideZone(pm, r, RPLaborDefOf.RP_Kitchen, GenRadial.RadialCellsAround(cook.Position, 2.5f, true).Where(x => x.InBounds(map)));
                // (Whatever fruit already lies in those cells is set aside for the check: it is the check's own fruit that is counted.)
                foreach (IntVec3 cell in pm.zones.CellsOf(RPLaborDefOf.RP_Kitchen).ToList())
                    foreach (Thing other in cell.GetThingList(map).Where(HoochUtility.IsFruit).ToList())
                    {
                        other.DeSpawn();
                        r.Add(() => GenSpawn.Spawn(other, cell, map));
                    }
                var fruit = (Thing)ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RawBerries"));
                fruit.stackCount = 10;
                IntVec3 shelf = pm.zones.CellsOf(RPLaborDefOf.RP_Kitchen).First(x => x.Standable(map) && x != cook.Position);
                GenSpawn.Spawn(fruit, shelf, map);
                r.Add(() =>
                {
                    if (!fruit.Destroyed) fruit.Destroy(DestroyMode.Vanish);
                });
                c.stash.ClearAndDestroyContents();
                r.Add(() => c.stash.ClearAndDestroyContents());
                // (Nobody watching: a kitchen worker in the corner of the yard.)
                pm.Guards.Clear();
                // With no fruit in the kitchen there is nothing to take.
                fruit.DeSpawn();
                bool empty = HoochUtility.Notify_WorkedInKitchen(cook, c, pm, force: true);
                GenSpawn.Spawn(fruit, shelf, map);
                if (empty || c.stash.Count != 0) InsideFail($"a kitchen with no fruit gave a mash ({empty}, stash {c.stash.Count}, the kitchen holds {HoochUtility.KitchenFruit(map, pm)?.LabelShort})");
                // With fruit: one piece goes into one lot of mash, in the stash, started now.
                int before = fruit.stackCount;
                if (!HoochUtility.Notify_WorkedInKitchen(cook, c, pm, force: true) || StashCount(c, RPContrabandDefOf.RP_HoochMash) != 1 || fruit.stackCount != before - 1)
                    InsideFail($"a worker in the kitchen did not turn a piece of fruit into a lot of mash in the stash (mash {StashCount(c, RPContrabandDefOf.RP_HoochMash)}, fruit {before} -> {fruit.stackCount})");
                Thing mash = c.stash.First(t => t.def == RPContrabandDefOf.RP_HoochMash);
                CompHoochMash comp = mash.TryGetComp<CompHoochMash>();
                if (comp == null || comp.sinceTick != Find.TickManager.TicksGame) InsideFail($"the mash does not know since when it has worked ({comp?.sinceTick} at {Find.TickManager.TicksGame})");
                // Not watched, not stolen: a guard in sight stops the dice from being rolled at all (300 hours of work with a guard beside them take nothing), and nobody in sight gives some in 400.
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                s.Place(guard, 1);
                IntVec3 beside = cook.Position + IntVec3.East;
                guard.Position = beside.InBounds(map) && beside.Standable(map) ? beside : cook.Position + IntVec3.West;
                guard.Notify_Teleported(true, true);
                pm.Guards.Add(guard);
                c.tension = 40f;
                int taken = StashCount(c, RPContrabandDefOf.RP_HoochMash);
                for (int i = 0; i < 300; i++) HoochUtility.Notify_WorkedInKitchen(cook, c, pm);
                if (StashCount(c, RPContrabandDefOf.RP_HoochMash) != taken) InsideFail("a kitchen worker with a guard beside them still made mash");
                pm.Guards.Clear();
                for (int i = 0; i < 400 && StashCount(c, RPContrabandDefOf.RP_HoochMash) == taken && fruit.stackCount > 0; i++) HoochUtility.Notify_WorkedInKitchen(cook, c, pm);
                if (StashCount(c, RPContrabandDefOf.RP_HoochMash) == taken) InsideFail("400 hours of unwatched work in a kitchen with fruit made no mash");
                // It works for two days: before, still mash; after, hooch (two for one) and no mash.
                int lots = StashCount(c, RPContrabandDefOf.RP_HoochMash);
                foreach (Thing t in c.stash) if (t.TryGetComp<CompHoochMash>() is { } m) m.sinceTick = Find.TickManager.TicksGame - HoochUtility.FermentTicks + 1;
                HoochUtility.Ferment(c);
                if (StashCount(c, RPContrabandDefOf.RP_HoochMash) != lots || StashCount(c, RPContrabandDefOf.RP_Hooch) != 0) InsideFail("mash that has worked for less than two days turned into hooch");
                foreach (Thing t in c.stash) if (t.TryGetComp<CompHoochMash>() is { } m) m.sinceTick = Find.TickManager.TicksGame - HoochUtility.FermentTicks;
                HoochUtility.Ferment(c);
                if (StashCount(c, RPContrabandDefOf.RP_HoochMash) != 0 || StashCount(c, RPContrabandDefOf.RP_Hooch) != lots * HoochUtility.HoochPerMash)
                    InsideFail($"two days on, {lots} lots of mash gave {StashCount(c, RPContrabandDefOf.RP_Hooch)} hooch and left {StashCount(c, RPContrabandDefOf.RP_HoochMash)} mash ({lots * HoochUtility.HoochPerMash} hooch expected)");
                if (!StashUtility.Has(c, ContrabandKind.Drug)) InsideFail("hooch in the stash is not a drug to the stash (a dealer could not fetch it, the inmate could not take it)");
                // The stash keeps its own clock: the 250-tick pass (a worker who is out of their cell) does the same.
                Thing again = ThingMaker.MakeThing(RPContrabandDefOf.RP_HoochMash);
                again.stackCount = 1;
                c.stash.TryAdd(again);
                again.TryGetComp<CompHoochMash>().sinceTick = Find.TickManager.TicksGame - HoochUtility.FermentTicks - 10;
                StashUtility.Rare(cook, c, pm);
                if (StashCount(c, RPContrabandDefOf.RP_HoochMash) != 0) InsideFail("the stash's own pass (every 250 ticks) leaves mash that has worked long enough");
            });
        }

        /// <summary>
        /// Hooch and mash are contraband like the rest of the stash: a cell search finds them (the mash is the still) and marks the find, a bust takes them, they are never smuggled in from outside
        /// (their smuggle weight is nothing), hooch is a drink an inmate drinks through the stash's own use of drugs, and a ring's dealer can have it as stock.
        /// </summary>
        private static void CheckInsideHoochIsContrabandAndNeverSmuggledIn(Map map)
        {
            Guarded("inside: hooch is contraband and never smuggled in", map, r =>
            {
                var s = new RingScene(map, r);
                PrisonMap pm = s.pm;
                Pawn drinker = s.Inmate("Zdrinker841");
                CompInmate c = drinker.Inmate();
                c.stash.ClearAndDestroyContents();
                r.Add(() => c.stash.ClearAndDestroyContents());
                // Never from outside: 600 deliveries and 600 drug deliveries (every way in picks from the same list) bring neither.
                int hooch = 0;
                for (int i = 0; i < 600; i++)
                {
                    ThingDef any = ContrabandUtility.PickSmuggled(drinker), drug = ContrabandUtility.PickSmuggled(drinker, ContrabandKind.Drug);
                    if (any == RPContrabandDefOf.RP_Hooch || any == RPContrabandDefOf.RP_HoochMash || drug == RPContrabandDefOf.RP_Hooch || drug == RPContrabandDefOf.RP_HoochMash) hooch++;
                }
                if (hooch > 0) InsideFail($"hooch or mash was smuggled in from outside {hooch} times in 1,200 deliveries");
                // A cell search finds the still and the hooch, marks each where the cell is, and writes the inmate up.
                foreach (ThingDef def in new[] { RPContrabandDefOf.RP_HoochMash, RPContrabandDefOf.RP_Hooch })
                {
                    Thing t = ThingMaker.MakeThing(def);
                    t.stackCount = 2;
                    c.stash.TryAdd(t);
                }
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                int finds0 = pm.finds.Count, found = 0;
                for (int i = 0; i < 20 && c.stash.Count > 0; i++) found += StashUtility.Search(guard, drinker, c);
                if (c.stash.Count != 0 || found < 2) InsideFail($"a cell search did not find the still and the hooch ({c.stash.Count} things left in the stash, {found} found in 20 searches)");
                if (pm.finds.Count < finds0 + 2 || pm.finds.Skip(finds0).Any(f => f.source != FindSource.Stash)) InsideFail("what a cell search found of the hooch was not marked on the map of finds as a find in a stash");
                if (!pm.finds.Skip(finds0).Any(f => f.kind == ContrabandKind.Brew) || !pm.finds.Skip(finds0).Any(f => f.kind == ContrabandKind.Drug)) InsideFail("the map of finds lacks the mash (a brew) or the hooch (a drug)");
                // A drink: with hooch in the pockets and a bad day, the inmate drinks it (the same use of drugs as beer).
                Thing pocket = ContrabandUtility.Give(drinker, RPContrabandDefOf.RP_Hooch);
                c.tension = 60f;
                Job drink = null;
                for (int i = 0; i < 80 && drink == null; i++) drink = ContrabandUtility.TryUseDrugs(drinker, c);
                if (pocket == null || drink?.def != JobDefOf.Ingest || drink.targetA.Thing?.def != RPContrabandDefOf.RP_Hooch) InsideFail($"an inmate with hooch in the pockets and a bad day did not drink it ({drink?.def?.defName} {drink?.targetA.Thing?.def?.defName})");
                drinker.inventory.innerContainer.ClearAndDestroyContents();
                // A bust of a ring takes the hooch with the rest of what the dealer has hidden.
                Pawn dealer = s.Inmate("Zdealer841", dealer: true);
                CompInmate dc = dealer.Inmate();
                dc.stash.ClearAndDestroyContents();
                r.Add(() => dc.stash.ClearAndDestroyContents());
                Thing stock = ThingMaker.MakeThing(RPContrabandDefOf.RP_Hooch);
                stock.stackCount = 4;
                dc.stash.TryAdd(stock);
                if (RingSystem.Confiscate(dealer, dc) != 1 || dc.stash.Count != 0) InsideFail("taking out a dealer left the hooch in their stash");
            });
        }

        // ---- the garden's smokeleaf ----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A gardener who is inside for smokeleaf growing sows one patch among the garden's crops when nobody is watching (anyone else, and anyone watched, does not); a ring takes on a gardener with a patch;
        /// ripe, the patch is harvested into joints in their pockets only while they work, and that is the ring's supply from the garden: a supplier link of its own (next to an outside supplier), unknown
        /// to the warden, with the joints the courier carries for the dealer, and the dealer gets them when the courier hands them on. The patch grows back.
        /// </summary>
        private static void CheckInsideGardenerSowsAndFeedsTheRing(Map map)
        {
            Guarded("inside: the garden's smokeleaf feeds a ring", map, r =>
            {
                var s = new RingScene(map, r);
                PrisonMap pm = s.pm;
                InsideOwnPatches(pm, r);
                Pawn dealer = s.Inmate("Zdealer842", dealer: true), gardener = s.Inmate("Zgardener842"), thief = s.Inmate("Zthief842");
                CompInmate gc = gardener.Inmate(), tc = thief.Inmate();
                InmateJobDef gardenJob = DefDatabase<InmateJobDef>.GetNamed("RP_JobGarden");
                CrimeDef growing = RPContrabandDefOf.RP_Crime_SmokeleafGrowing, theft = DefDatabase<CrimeDef>.GetNamed("RP_Crime_Theft");
                (InmateJobDef job0, CrimeDef crime0) = (gc.job, gc.crime);
                (InmateJobDef tjob0, CrimeDef tcrime0) = (tc.job, tc.crime);
                r.Add(() =>
                {
                    (gc.job, gc.crime) = (job0, crime0);
                    (tc.job, tc.crime) = (tjob0, tcrime0);
                });
                (gc.job, gc.crime, tc.job, tc.crime) = (gardenJob, growing, gardenJob, theft);
                if (!gardenJob.growsContraband) InsideFail("the garden job is not marked as one where a patch may be sown");
                InsideZone(pm, r, RPLaborDefOf.RP_Garden, GenRadial.RadialCellsAround(gardener.Position, 3.5f, true).Where(x => x.InBounds(map)));
                pm.Guards.Clear();
                // Only the one inside for it sows, and not twice; one who is watched does not sow (a guard beside them, 300 hours of it).
                for (int i = 0; i < 300; i++) GardenPatches.Notify_Gardening(thief, tc, pm);
                if (pm.patches.Count != 0) InsideFail("a gardener who is not inside for smokeleaf growing sowed a patch of it");
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                s.Place(guard, 2);
                guard.Position = gardener.Position + IntVec3.East;
                guard.Notify_Teleported(true, true);
                pm.Guards.Add(guard);
                for (int i = 0; i < 300; i++) GardenPatches.Notify_Gardening(gardener, gc, pm);
                if (pm.patches.Count != 0) InsideFail("a gardener sowed a patch with a guard beside them");
                pm.Guards.Clear();
                for (int i = 0; i < 300 && pm.patches.Count == 0; i++) GardenPatches.Notify_Gardening(gardener, gc, pm);
                GardenPatch patch = GardenPatches.PatchOf(pm, gardener);
                if (patch == null || pm.zones.ZoneAt(patch.cell) != RPLaborDefOf.RP_Garden || patch.ownerId != gardener.thingIDNumber) InsideFail($"nobody-watching sowed no patch of their own in the garden in 300 hours ({patch?.Describe()})");
                if (patch == null) return;
                GardenPatches.Notify_Gardening(gardener, gc, pm, force: true);
                if (pm.patches.Count != 1) InsideFail($"a gardener who has a patch sowed another ({pm.patches.Count})");
                // The patch is nowhere the warden can see it: not in the finds, nothing in the ring.
                if (pm.finds.Count != 0) InsideFail("sowing the patch put a mark on the map of finds");
                // The ring takes on the gardener with a patch: a candidate with nothing else about them (not the dealer's gang, not a customer) joins only for the patch.
                StoryArc ring = s.Form(dealer);
                RingLink courierLink = s.Recruit(ring);
                if (courierLink == null || courierLink.pawnId != gardener.thingIDNumber) InsideFail($"the dealer's ring did not take on the gardener with a patch ({courierLink?.name})");
                if (courierLink == null) return;
                // Not ripe: nothing is harvested (and not by a gardener who is not at work, however ripe).
                GardenPatches.Hourly(map, pm);
                if (RingSystem.DrugsCarried(gardener) != 0) InsideFail("a patch that is not ripe was harvested");
                patch.sownTick = Find.TickManager.TicksGame - GardenPatches.RipeTicks;
                gardener.jobs.StopAll();
                GardenPatches.Hourly(map, pm);
                if (RingSystem.DrugsCarried(gardener) != 0 || patch.harvests != 0) InsideFail("a ripe patch was harvested by a gardener who was not at work");
                // Harvested: the joints are in the pockets, the supplier is the garden (unknown), the courier carries them for the dealer, and the patch grows back.
                RingSystem.Notify_BroughtIn(gardener, "Zkin842");
                if (!GardenPatches.Harvest(gardener, gc, patch)) InsideFail("a ripe patch gave nothing to harvest");
                int joints = RingSystem.DrugsCarried(gardener);
                RingLink garden = ring.ring.links.Find(RingSystem.IsGarden);
                RingLink outside = ring.ring.links.Find(l => l.role == RingRole.Relative && !RingSystem.IsGarden(l));
                if (joints != ContrabandUtility.DeliveryCount(RPContrabandDefOf.SmokeleafJoint) || garden is not { known: false, handled: false } || outside is not { known: false } || garden.pawnId != gardener.thingIDNumber)
                    InsideFail($"a harvest: joints {joints}, the garden's link {(garden == null ? "missing" : "there")} (known {garden?.known}), an outside supplier beside it {outside != null}");
                if (courierLink.carried != joints) InsideFail($"the courier carries {courierLink.carried} for the dealer, {joints} came from the garden");
                if (patch.harvests != 1 || Find.TickManager.TicksGame - patch.sownTick != 0) InsideFail("the patch did not start growing again after the harvest");
                if (RingSystem.Chain(ring).Any(p => p.link != null && RingSystem.IsGarden(p.link))) InsideFail("the chain of the ring shows the garden before anyone found it");
                // The courier hands them on, as with every drug the ring brings in.
                s.Place(dealer, 3);
                s.Place(gardener, 4);
                if (!RingSystem.TryHandover(ring, map, force: true) || RingSystem.DrugsCarried(dealer) != joints) InsideFail($"the dealer was not handed what the garden grew ({RingSystem.DrugsCarried(dealer)} of {joints})");
                // A second harvest does not make a second garden link.
                patch.sownTick = Find.TickManager.TicksGame - GardenPatches.RipeTicks;
                GardenPatches.Harvest(gardener, gc, patch);
                if (ring.ring.links.Count(RingSystem.IsGarden) != 1) InsideFail("a second harvest made a second garden link");
            });
        }

        /// <summary>
        /// The patch is found: by the staff gardener who works near it (and not by nobody at all), by a guard about the place, by a cell search of the gardener's cell. Pulled up, it is a mark on the map of
        /// finds from the garden, the gardener is written up, and a gardener in a ring has their link found, with the garden, and the ring's heat rises; a bust takes the patch out with the garden's link; a patch
        /// whose gardener has gone is overgrown.
        /// </summary>
        private static void CheckInsideGardenPatchIsFound(Map map)
        {
            Guarded("inside: a smokeleaf patch is found and traced", map, r =>
            {
                var s = new RingScene(map, r);
                PrisonMap pm = s.pm;
                InsideOwnPatches(pm, r);
                Pawn dealer = s.Inmate("Zdealer843", dealer: true), gardener = s.Inmate("Zgardener843");
                CompInmate gc = gardener.Inmate();
                (InmateJobDef job0, CrimeDef crime0) = (gc.job, gc.crime);
                r.Add(() => (gc.job, gc.crime) = (job0, crime0));
                (gc.job, gc.crime) = (DefDatabase<InmateJobDef>.GetNamed("RP_JobGarden"), RPContrabandDefOf.RP_Crime_SmokeleafGrowing);
                InsideZone(pm, r, RPLaborDefOf.RP_Garden, GenRadial.RadialCellsAround(gardener.Position, 3.5f, true).Where(x => x.InBounds(map)));
                pm.Guards.Clear();
                StoryArc ring = s.Form(dealer);
                s.Assess(dealer);
                ring.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = gardener.thingIDNumber, name = gardener.LabelShort });
                GardenPatches.Notify_Gardening(gardener, gc, pm, force: true);
                GardenPatch patch = GardenPatches.PatchOf(pm, gardener);
                RingSystem.Notify_BroughtIn(gardener, RingSystem.GardenSource, RPContrabandDefOf.SmokeleafJoint);
                RingLink courier = ring.ring.links.Find(l => l.role == RingRole.Courier), garden = ring.ring.links.Find(RingSystem.IsGarden);
                if (patch == null || courier == null || garden == null)
                {
                    InsideFail($"the check's set-up failed (patch {patch != null}, courier {courier != null}, garden link {garden != null})");
                    return;
                }
                // Nobody about: nobody finds it, in 200 hours (unless one of the real staff happens to be standing near it, which is then no test of nobody).
                bool alone = !map.mapPawns.FreeColonistsSpawned.Any(x => x.Position.InHorDistOf(patch.cell, GardenPatches.FindRadius));
                if (alone)
                {
                    for (int i = 0; i < 200; i++) GardenPatches.Hourly(map, pm);
                    if (pm.patches.Count != 1) InsideFail("a patch nobody was near was found");
                }
                else RPLog.Message("[RimPrison:test] inside: the patch nobody is near was not checked (one of the real staff stands near the quiet corner)");
                // The staff gardener, working near it, does (12% an hour: found within 120).
                Pawn staff = ThrowawayStaff(map, r, RPLaborDefOf.RP_Gardener);
                staff.Position = patch.cell;
                staff.Notify_Teleported(true, true);
                if (staff.needs?.rest != null) staff.needs.rest.CurLevel = 1f;
                int hours = 0;
                for (; hours < 120 && pm.patches.Count > 0; hours++) GardenPatches.Hourly(map, pm);
                if (pm.patches.Count != 0) InsideFail($"the staff gardener beside the patch did not find it in {hours} hours");
                ContrabandFind find = pm.finds.LastOrDefault();
                if (find == null || find.source != FindSource.Garden || find.cell != patch.cell || find.kind != ContrabandKind.Drug) InsideFail($"the patch found was not marked on the map of finds as a garden find ({find?.Describe()} for {patch.cell})");
                if (!courier.known || !garden.known || ring.ring.heat < RingSystem.HeatPerFind) InsideFail($"a patch found on a gardener who carries for a ring did not show their link ({courier.known}), the garden's ({garden.known}) and heat the ring ({ring.ring.heat})");
                if (gardener.Inmate().records.LastOrDefault()?.def != RPContrabandDefOf.RP_ContrabandPossession) InsideFail("the gardener was not written up for the patch");
                if (!RingSystem.Chain(ring).Any(p => p.link != null && RingSystem.IsGarden(p.link) && p.text == "RP_RingGardenName".Translate().ToString())) InsideFail("the chain does not show the garden by what it is once found");
                if (!r.NewEntries.Any(e => e.text == "RP_Chr_GardenFound".Translate(gardener.LabelShort).ToString())) InsideFail("the chronicle has no line for the patch found");
                // A cell search finds it as well (60% a search: certain when asked with the odds at one), and a search of a cell with no patch finds none.
                GardenPatches.Notify_Gardening(gardener, gc, pm, force: true);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                int finds0 = pm.finds.Count;
                if (GardenPatches.Search(guard, dealer, dealer.Inmate(), 1f) != 0) InsideFail("a search of the cell of somebody with no patch found one");
                if (GardenPatches.Search(guard, gardener, gc, 1f) != 1 || pm.patches.Count != 0 || pm.finds.Count != finds0 + 1 || pm.finds.Last().source != FindSource.Garden) InsideFail("a cell search of the gardener's cell did not find the patch and mark it");
                // ... and through the stash search itself, which a shakedown runs (the patch is pulled up in time, 60% a search).
                GardenPatches.Notify_Gardening(gardener, gc, pm, force: true);
                for (int i = 0; i < 40 && pm.patches.Count > 0; i++) StashUtility.Search(guard, gardener, gc);
                if (pm.patches.Count != 0) InsideFail("40 cell searches of the gardener's cell did not find the patch");
                // A bust takes the garden out with the rest: the patch is pulled up with the link.
                GardenPatches.Notify_Gardening(gardener, gc, pm, force: true);
                RingSystem.Notify_Assessed(gardener, 1f);
                garden.known = true;
                garden.handled = false;
                RingSystem.Bust(ring, map);
                if (pm.patches.Count != 0 || !garden.handled) InsideFail($"a bust left the patch in the garden (patches {pm.patches.Count}, link handled {garden.handled})");
                // The gardener gone: the patch is overgrown, quietly.
                GardenPatches.Notify_Gardening(gardener, gc, pm, force: true);
                gc.activeFlag = false;
                ShuttleSystem sys = PrisonGame.Instance.shuttle;
                sys.outbound.Add(new OutboundEntry { pawn = gardener, kind = OutboundKind.Released, sinceTick = Find.TickManager.TicksGame });
                r.Add(() => sys.Forget(gardener));
                int entries0 = r.NewEntries.Count();
                GardenPatches.Hourly(map, pm);
                if (pm.patches.Count != 0 || r.NewEntries.Count() != entries0) InsideFail($"a patch whose gardener was on their way out stayed in the garden ({pm.patches.Count}) or told the chronicle ({r.NewEntries.Count() - entries0} lines)");
            });
        }

        // ---- men and women apart ------------------------------------------------------------------------------------------------------------------

        /// <summary>A bed spawned free in this cell (as the cells rules test does), taken down again by the rollback; null if there is no room.</summary>
        private static Building_Bed InsideSpareBed(Map map, Room cell, Rollback r)
        {
            ThingDef bedDef = DefDatabase<ThingDef>.GetNamed("Bed");
            IntVec3 spot = cell.Cells.FirstOrDefault(x => GenAdj.OccupiedRect(x, Rot4.North, bedDef.size).Cells.All(y => y.InBounds(map) && y.Standable(map) && y.GetEdifice(map) == null
                && y.GetRoom(map) == cell && y.GetFirstItem(map) == null && y.GetFirstPawn(map) == null));
            if (!spot.IsValid || spot == default) return null;
            var bed = (Building_Bed)ThingMaker.MakeThing(bedDef, ThingDefOf.WoodLog);
            bed.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(bed, spot, map, Rot4.North);
            bed.ForPrisoners = true;
            r.Add(() =>
            {
                if (bed.Spawned) bed.Destroy();
                Cellmates.InvalidateCells();
                PrisonMap.Of(map)?.ForceDoorUpdate();
            });
            PrisonMap.Of(map)?.ForceDoorUpdate();
            return bed;
        }

        /// <summary>
        /// Men and women are kept apart in the cells: a man and a woman under one roof is a clash as bad as rival gangs (the term is on the pair, whichever way round); the cell block calls such a cell tense, the
        /// daily review would move one of them, and a newcomer is put in the cell of their own sex if there is one. Two of one sex are as they were.
        /// </summary>
        private static void CheckInsideMenAndWomenAreKeptApartInACell(Map map)
        {
            Guarded("inside: men and women are kept apart in the cells", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                (Pawn a, Pawn b) = CellsPair(map);
                if (a == null)
                {
                    RPLog.Message("[RimPrison:test] inside: mixed cells check skipped (no two inmates in different cells)");
                    return;
                }
                CompInmate ac = a.Inmate(), bc = b.Inmate();
                Gender genderA0 = a.gender, genderB0 = b.gender;
                r.Add(() => (a.gender, b.gender) = (genderA0, genderB0));
                // (Nothing else about the two weighs in: no gang, no reputation, no friendship, no feud.)
                (GangDef gangA0, GangDef gangB0) = (ac.gang, bc.gang);
                List<ReputationDef> repsA0 = ac.reputations.ToList(), repsB0 = bc.reputations.ToList();
                (int holdA0, int holdB0) = (ac.wardenPlacedTick, bc.wardenPlacedTick);
                r.Add(() =>
                {
                    (ac.gang, bc.gang) = (gangA0, gangB0);
                    (ac.reputations, bc.reputations) = (repsA0, repsB0);
                    (ac.wardenPlacedTick, bc.wardenPlacedTick) = (holdA0, holdB0);
                });
                (ac.gang, bc.gang) = (null, null);
                ac.reputations.Clear();
                bc.reputations.Clear();
                (ac.wardenPlacedTick, bc.wardenPlacedTick) = (-999999, -999999);
                Building_Bed bedA = a.ownership.OwnedBed, bedB = b.ownership.OwnedBed;
                Building_Bed spare = InsideSpareBed(map, bedB.GetRoom(), r);
                if (spare == null)
                {
                    RPLog.Message("[RimPrison:test] inside: mixed cells check skipped (no room for a second bed in a cell)");
                    return;
                }
                // The term: the same bed, the same newcomer, a man and then a woman.
                a.gender = Gender.Male;
                b.gender = Gender.Male;
                float same = Cellmates.MateScore(spare, ac);
                b.gender = Gender.Female;
                float mixed = Cellmates.MateScore(spare, ac);
                (a.gender, b.gender) = (Gender.Female, Gender.Male);
                float mixedTheOtherWay = Cellmates.MateScore(spare, ac);
                b.gender = Gender.Female;
                float sameWomen = Cellmates.MateScore(spare, ac);
                if (Math.Abs(mixed - (same + Cellmates.MixedCell)) > 0.001f || Math.Abs(mixedTheOtherWay - mixed) > 0.001f || Math.Abs(sameWomen - same) > 0.001f)
                    InsideFail($"the cellmate term: two men {same}, a man with a woman {mixed}, a woman with a man {mixedTheOtherWay}, two women {sameWomen} (the term is {Cellmates.MixedCell})");
                // In one cell: tense to the warden, the review would split them, and not when they are two women.
                a.gender = Gender.Male;
                b.gender = Gender.Female;
                a.ownership.UnclaimBed();
                a.ownership.ClaimBedIfNonMedical(spare);
                r.Add(() =>
                {
                    a.ownership.UnclaimBed();
                    a.ownership.ClaimBedIfNonMedical(bedA);
                });
                Cellmates.InvalidateCells();
                if (a.ownership.OwnedBed != spare || !Cellmates.Share(a, b)) InsideFail("the check's set-up failed: the two do not share a cell");
                else
                {
                    if (!Cellmates.ReviewWould(a, ac) || Cellmates.Verdict(spare, ac) == Cellmates.CellFit.Good) InsideFail($"a man and a woman in one cell: the review would move them {Cellmates.ReviewWould(a, ac)}, the cell block calls it {Cellmates.Verdict(spare, ac)}");
                    a.gender = Gender.Female;
                    if (Cellmates.ReviewWould(a, ac) || Cellmates.Verdict(spare, ac) != Cellmates.CellFit.Good) InsideFail($"two women in one cell: the review would move them {Cellmates.ReviewWould(a, ac)}, the cell block calls it {Cellmates.Verdict(spare, ac)}");
                    // A newcomer's fit with the cell where a woman lives (b, now in the spare's cell with nobody else): a woman fits it better than a man, by the term; and with nothing else free they still go there (kept apart where there is a choice).
                    a.ownership.UnclaimBed();
                    a.ownership.ClaimBedIfNonMedical(bedA);
                    Cellmates.InvalidateCells();
                    Pawn newcomer = Throwaway(map, r);
                    CompInmate nc = newcomer.Inmate();
                    (GangDef gangN0, List<ReputationDef> repsN0) = (nc.gang, nc.reputations.ToList());
                    r.Add(() => (nc.gang, nc.reputations) = (gangN0, repsN0));
                    nc.gang = null;
                    nc.reputations.Clear();
                    newcomer.gender = Gender.Female;
                    float forWoman = Cellmates.Fit(spare, nc);
                    newcomer.gender = Gender.Male;
                    float forMan = Cellmates.Fit(spare, nc);
                    if (Math.Abs((forWoman - forMan) + Cellmates.MixedCell) > 0.001f) InsideFail($"a woman's fit with the cell of a woman is {forWoman}, a man's is {forMan}: the difference is not the term ({Cellmates.MixedCell})");
                    if (Cellmates.PickBed(new List<Building_Bed> { spare }, nc) != spare) InsideFail("a man was refused the only free bed, in a cell with a woman (kept apart where there is a choice, not where there is none)");
                }
            });
        }

        /// <summary>
        /// A wing kept for men or for women takes no inmate of the other sex: its beds are not offered to them (the free beds, the move, the pick), the shuttle's roster brings the sex there are beds for, and
        /// an inmate who is already in such a wing is the daily review's to move. A wing for all inmates is as it was. The policy is saved (the saveload snapshot).
        /// </summary>
        private static void CheckInsideWingPolicyKeepsTheOtherSexOut(Map map)
        {
            Guarded("inside: a wing for one sex", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                (Pawn a, Pawn b) = CellsPair(map);
                if (a == null)
                {
                    RPLog.Message("[RimPrison:test] inside: wing policy check skipped (no two inmates in different cells)");
                    return;
                }
                CompInmate ac = a.Inmate();
                Building_Bed bedA = a.ownership.OwnedBed, bedB = b.ownership.OwnedBed;
                Building_Bed spare = InsideSpareBed(map, bedB.GetRoom(), r);
                if (spare == null || IntakeSystem.WingOf(spare) is not { } wing)
                {
                    RPLog.Message("[RimPrison:test] inside: wing policy check skipped (no room for a second bed in a cell of a wing)");
                    return;
                }
                Gender genderA0 = a.gender, genderB0 = b.gender;
                (GangDef gangA0, List<ReputationDef> repsA0, int holdA0) = (ac.gang, ac.reputations.ToList(), ac.wardenPlacedTick);
                var wings0 = new Dictionary<SecurityLevelDef, WingGender>(g.wingGenders);
                r.Add(() =>
                {
                    (a.gender, b.gender) = (genderA0, genderB0);
                    (ac.gang, ac.reputations, ac.wardenPlacedTick) = (gangA0, repsA0, holdA0);
                    g.wingGenders.Clear();
                    foreach (KeyValuePair<SecurityLevelDef, WingGender> kv in wings0) g.wingGenders[kv.Key] = kv.Value;
                    a.ownership.UnclaimBed();
                    a.ownership.ClaimBedIfNonMedical(bedA);
                });
                ac.gang = null;
                ac.reputations.Clear();
                ac.wardenPlacedTick = -999999;
                // (The man whose cell the spare bed is in is a man too: the review's only reason to move the other is the wing.)
                b.gender = Gender.Male;
                SecurityLevelDef level = ac.security;
                // For all inmates (the default): both sexes are offered the spare bed.
                g.wingGenders.Remove(wing);
                bool bothOpen = IntakeSystem.FreePrisonerBeds(map, null, Gender.Male).Contains(spare) && IntakeSystem.FreePrisonerBeds(map, null, Gender.Female).Contains(spare) && IntakeSystem.WingAllows(wing, Gender.Male) && IntakeSystem.WingAllows(wing, Gender.Female);
                if (!bothOpen) InsideFail("a wing the warden has set nothing for does not offer its free beds to both sexes");
                // Women only: not offered to a man, offered to a woman; counted the same; not picked, not moved into; and a woman can.
                g.wingGenders[wing] = WingGender.Women;
                bool menOut = !IntakeSystem.FreePrisonerBeds(map, null, Gender.Male).Contains(spare) && IntakeSystem.FreePrisonerBeds(map, null, Gender.Female).Contains(spare)
                              && IntakeSystem.FreeBedCount(map, null, gender: Gender.Female) == IntakeSystem.FreeBedCount(map, null, gender: Gender.Male) + IntakeSystem.FreePrisonerBeds(map, null, Gender.Female).Count(x => IntakeSystem.WingOf(x) == wing);
                if (!menOut) InsideFail("a women-only wing offers its free beds to a man, or not to a woman, or the counts disagree");
                a.gender = Gender.Male;
                if (Cellmates.PickBed(new List<Building_Bed> { spare }, ac) != null || Cellmates.Move(a, ac, spare) || a.ownership.OwnedBed != bedA) InsideFail("a man was put in a bed of a women-only wing (picked or moved)");
                a.gender = Gender.Female;
                if (Cellmates.PickBed(new List<Building_Bed> { spare }, ac) != spare) InsideFail("a woman was not offered a bed of a women-only wing");
                // The review moves out someone who is in a wing now kept for the other sex (a man in the spare bed, the wing now for women): a bed of theirs is free (their own old one).
                a.gender = Gender.Male;
                g.wingGenders.Remove(wing);
                a.ownership.UnclaimBed();
                a.ownership.ClaimBedIfNonMedical(spare);
                Cellmates.InvalidateCells();
                bool settled = a.ownership.OwnedBed == spare && !Cellmates.ReviewWould(a, ac);
                g.wingGenders[wing] = WingGender.Women;
                bool movedOut = Cellmates.ReviewWould(a, ac);
                if (!settled || !movedOut) InsideFail($"a man living in a wing that is then kept for women: before the review would leave him {settled}, after it would move him {movedOut}");
                // The roster: with every free bed of this level in women-only wings, whoever the state sends is a woman (20 draws are not all women by luck).
                a.ownership.UnclaimBed();
                a.ownership.ClaimBedIfNonMedical(bedA);
                Cellmates.InvalidateCells();
                foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading) g.wingGenders[sec] = WingGender.Women;
                var perDay0 = new Dictionary<SecurityLevelDef, int>(g.intake.perDay);
                int number0 = g.intake.nextInmateNumber;
                List<Pawn> carried0 = g.shuttle.carriedOver.ToList();
                r.Add(() =>
                {
                    foreach (KeyValuePair<SecurityLevelDef, int> kv in perDay0) g.intake.perDay[kv.Key] = kv.Value;
                    g.intake.nextInmateNumber = number0;
                    g.shuttle.carriedOver.Clear();
                    g.shuttle.carriedOver.AddRange(carried0);
                });
                foreach (SecurityLevelDef sec in perDay0.Keys.ToList()) g.intake.perDay[sec] = 0;
                if (level != null && Unlocks.IsUnlocked(level)) g.intake.perDay[level] = 8;
                InsideFreeBeds(map, r, level, 6);
                List<Building_Bed> offered = IntakeSystem.FreePrisonerBeds(map, level);
                bool allInWings = offered.All(x => IntakeSystem.WingOf(x) != null);
                int room = allInWings ? Math.Min(8, offered.Count) : 0;
                List<Pawn> roster = IntakeSystem.GenerateRoster(g, map, out _, out _, 8);
                r.Add(() =>
                {
                    foreach (Pawn p in roster)
                    {
                        if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                        if (!p.Destroyed) p.Destroy();
                    }
                });
                // (Only a prison whose free beds are all in wings tests this: a bed in no wing takes anybody, so men may come for it. With fewer than four beds the draws prove little.)
                if (room < 4) RPLog.Message($"[RimPrison:test] inside: the roster under a women-only policy was not checked (free beds at this level {offered.Count}, all in wings {allInWings})");
                else
                {
                    if (roster.Count < room) InsideFail($"{roster.Count} inmates came on the roster though there were {room} free beds, all in wings kept for women");
                    if (roster.Any(p => p.gender == Gender.Male)) InsideFail($"the state sent {roster.Count(p => p.gender == Gender.Male)} man(men) of {roster.Count} when every wing was kept for women");
                }
            });
        }

        // ---- save and load -------------------------------------------------------------------------------------------------------------------------

        private static int insideSeedPatchOwner = -1, insideSeedStashOwner = -1;
        private static SecurityLevelDef insideSeedWing;
        private static WingGender? insideSeedWingWas;
        private static string insideSeedStash;

        /// <summary>
        /// For the round trip: a wing kept for women, a smokeleaf patch in the garden part-way, a lot of mash and a stack of hooch in an inmate's stash with the mash's start. (The marshals' prisoners and an
        /// escort in progress are seeded with the shuttle's, in <see cref="RecordShuttleForReload"/>.) What the check changes is put back after the reload (<see cref="CheckInsideAfterReload"/>).
        /// </summary>
        private static void SeedInsideForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            insideSeedWing = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(x => !x.wingOnly);
            insideSeedWingWas = insideSeedWing != null && g.wingGenders.TryGetValue(insideSeedWing, out WingGender was) ? was : null;
            if (insideSeedWing != null) g.wingGenders[insideSeedWing] = WingGender.Women;
            Pawn[] inmates = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null).OrderBy(x => x.thingIDNumber).ToArray();
            if (inmates.Length < 2)
            {
                problems0.Add("saveload: no two inmates to seed a garden patch and a stash with");
                return;
            }
            int now = Find.TickManager.TicksGame;
            pm.patches.Add(new GardenPatch { cell = inmates[0].Position, ownerId = inmates[0].thingIDNumber, sownTick = now - 777, harvests = 3, tendedTick = now - 555 });
            insideSeedPatchOwner = inmates[0].thingIDNumber;
            CompInmate sc = inmates[1].Inmate();
            Thing mash = ThingMaker.MakeThing(RPContrabandDefOf.RP_HoochMash);
            mash.stackCount = 2;
            mash.TryGetComp<CompHoochMash>().sinceTick = now - 4242;
            Thing hooch = ThingMaker.MakeThing(RPContrabandDefOf.RP_Hooch);
            hooch.stackCount = 3;
            if (!sc.stash.TryAdd(mash) || !sc.stash.TryAdd(hooch)) problems0.Add("saveload: the stash would not take the seeded mash and hooch");
            insideSeedStashOwner = inmates[1].thingIDNumber;
            insideSeedStash = HoochUtility.Describe(sc);
            RPLog.Message($"[RimPrison:test] saveload inside: a patch of {inmates[0].LabelShort}'s in the garden, {insideSeedStash} in the stash of {inmates[1].LabelShort}, the wing {insideSeedWing?.defName} for women");
        }

        /// <summary>The seeded patch, the stash with its mash's clock, the wing policy and the marshals' prisoners came back as they were; then they go (they were never real).</summary>
        private static void CheckInsideAfterReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            if (insideSeedPatchOwner >= 0)
            {
                GardenPatch back = pm.patches.FirstOrDefault(x => x.ownerId == insideSeedPatchOwner);
                bool whole = back != null && back.harvests == 3 && Find.TickManager.TicksGame - back.sownTick >= 777 && Find.TickManager.TicksGame - back.tendedTick >= 555 && back.tendedTick > back.sownTick;
                RPLog.Message($"[RimPrison:test] saveload inside: garden patch {(whole ? "intact" : "changed")} ({back?.Describe()})");
                if (!whole) reloadProblems.Add($"the garden patch changed in the round trip ({back?.Describe()})");
                pm.patches.RemoveAll(x => x.ownerId == insideSeedPatchOwner);
                insideSeedPatchOwner = -1;
            }
            if (insideSeedStashOwner >= 0)
            {
                CompInmate sc = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.thingIDNumber == insideSeedStashOwner)?.Inmate();
                string after = sc == null ? "(inmate gone)" : HoochUtility.Describe(sc);
                RPLog.Message($"[RimPrison:test] saveload inside: stash {(after == insideSeedStash ? "intact" : "changed")} ({after})");
                if (after != insideSeedStash) reloadProblems.Add($"the stash with its mash changed in the round trip: {insideSeedStash} -> {after}");
                if (sc != null)
                    foreach (Thing t in sc.stash.Where(x => x.def == RPContrabandDefOf.RP_Hooch || x.def == RPContrabandDefOf.RP_HoochMash).ToList())
                    {
                        sc.stash.Remove(t);
                        t.Destroy();
                    }
                insideSeedStashOwner = -1;
            }
            if (insideSeedWing != null)
            {
                bool kept = g.WingGenderFor(insideSeedWing) == WingGender.Women;
                RPLog.Message($"[RimPrison:test] saveload inside: the wing {insideSeedWing.defName} {(kept ? "still" : "no longer")} for women");
                if (!kept) reloadProblems.Add("a wing kept for women was not kept in the round trip");
                if (insideSeedWingWas is { } was) g.wingGenders[insideSeedWing] = was;
                else g.wingGenders.Remove(insideSeedWing);
                insideSeedWing = null;
            }
        }
    }
}
