using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the smuggling-ring items of gap analysis 4 (H4, M6 and the ring lows), on the quiet corner of <see cref="RingScene"/>. Each fails without its fix.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckGa4Rings(Map map)
        {
            Rand.PushState(20261025);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            int checksBegan = Find.TickManager.TicksGame; // (a real shuttle that lands while the checks tick brings inmates of its own: not theirs to put away)
            try
            {
                CheckGa4SupplierNamesNobody(map);
                CheckGa4MoreLinksMarkerIsNotLive(map);
                CheckGa4HeatFollowsWhatIsFound(map);
                CheckGa4TurnedCouriersAndTheOneToTurn(map);
                CheckGa4SupplyCutOutlastsTheRing(map);
                CheckGa4OfficerOnTwoRingsIsTracedIntoBoth(map);
            }
            finally
            {
                Rand.PopState();
            }
            List<Pawn> leftBehind = LeftOnTheMap(map, pawnsBefore, checksBegan);
            if (leftBehind.Count > 0) problems0.Add($"gap analysis 4 ring checks left {string.Join(", ", leftBehind.Select(x => x.LabelShort))} on the map");
        }

        // ---- H4: a ring's supplier link names the inmate it serves ----------------------------------------------------------------------------------

        /// <summary>
        /// The outside supplier's link stores the inmate they come to see (it is how the ring cuts that inmate off), who may be a courier or the dealer nobody has found. Nothing that points at
        /// pawns may use it: not the case letter, not the bust's letter, line and message, and the bust does not ban the owner's visits (the file shows the ban); the supplier is barred by name.
        /// </summary>
        private static void CheckGa4SupplierNamesNobody(Map map)
        {
            Guarded("gap analysis 4 H4: the supplier's link names nobody", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer830", dealer: true), courier = s.Inmate("Zcourier830");
                CompInmate cc = courier.Inmate();
                cc.lastDealerId = dealer.thingIDNumber;
                var kin = new Relative { name = "Zsup830", kind = "RP_Kin_Cousin" };
                cc.relatives.Clear();
                cc.relatives.Add(kin);
                cc.familyCount = 1;
                StoryArc ring = s.Form(dealer);
                s.Recruit(ring);
                RingSystem.Notify_BroughtIn(courier, kin.Label);
                // The tap finds the supplier first (the officer, then the supplier): nothing else of the ring is known.
                RingSystem.Notify_Tapped(courier, force: true);
                RingLink supplier = s.Link(ring, RingRole.Relative);
                if (supplier is not { known: true } || s.Link(ring, RingRole.Courier).known || s.Link(ring, RingRole.Dealer).known)
                {
                    RingScene.Fail($"the check's set-up failed: the supplier known {supplier?.known}, the courier {s.Link(ring, RingRole.Courier).known}, the dealer {s.Link(ring, RingRole.Dealer).known}");
                    return;
                }
                int ban0 = cc.visitBanUntilTick;
                RingSystem.MaybeAsk(ring, map, force: true);
                ChoiceLetter_PrisonDecision letter = s.AskedAbout(ring);
                if (letter == null) RingScene.Fail("the case was not put to the warden");
                else if (letter.pawns.Contains(courier) || letter.pawns.Contains(dealer) || letter.lookTargets != null && (letter.lookTargets.targets?.Any(t => t.Thing == courier || t.Thing == dealer) ?? false))
                    RingScene.Fail("the letter about a ring whose only known link is the outside supplier points at the inmate the supplier comes to see (a courier the warden has not found)");
                int entries0 = r.NewEntries.Count();
                RingSystem.Bust(ring, map);
                if (r.NewEntries.Skip(entries0).Any(e => e.ids.Contains(courier.thingIDNumber) || e.ids.Contains(dealer.thingIDNumber))) RingScene.Fail("the chronicle line of the bust names an inmate the warden has not found through the supplier's link");
                if (cc.visitBanUntilTick != ban0) RingScene.Fail($"taking out the supplier banned the visits of the inmate they came to see, which their file shows (ban until {cc.visitBanUntilTick}, was {ban0})");
                if (!RingSystem.SupplierBarred(kin.Label) || Relatives.RandomVisitor(cc) != null || !Relatives.AllBarred(cc))
                    RingScene.Fail($"the supplier was not barred by name (barred {RingSystem.SupplierBarred(kin.Label)}, can still be the visitor {Relatives.RandomVisitor(cc) != null}, all barred {Relatives.AllBarred(cc)})");
                CompInmate alone = s.Inmate("Zother830").Inmate();
                (alone.familyCount, alone.relatives) = (0, new List<Relative>());
                if (Relatives.RandomVisitor(alone) != null || Relatives.AllBarred(alone)) RingScene.Fail("an inmate with no family has a visitor, or has all of it barred");
            });
        }

        // ---- M6: the "more links suspected" marker is a live one-bit counter ---------------------------------------------------------------------------

        private static void CheckGa4MoreLinksMarkerIsNotLive(Map map)
        {
            Guarded("gap analysis 4 M6: the marker follows the daily pass, not the links", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer831", dealer: true);
                StoryArc ring = s.Form(dealer);
                s.Assess(dealer);
                string more = "RP_IntelRingMore".Translate().ToString();
                bool Shown() => RingSystem.Chain(ring).Any(p => p.text == more);
                if (Shown()) RingScene.Fail("a ring that is only its dealer, found, still shows a marker");
                // A delivery gets past the search: a link nobody knows of appears. The marker does not come on at that moment.
                RingSystem.Notify_BroughtIn(dealer, "cousin Zsup831");
                if (!RingSystem.HasUnknown(ring) || Shown()) RingScene.Fail($"the marker came on the moment an unfound delivery made a supplier link (links unknown {RingSystem.HasUnknown(ring)}, marker shown {Shown()}): it would tell the warden something got in");
                // At the daily pass it does, and it clears when the last link is found.
                RingSystem.Daily(map, only: dealer);
                if (!Shown()) RingScene.Fail("the marker was not on after the daily pass, with a link still to find");
                RingSystem.Notify_Tapped(dealer, force: true);
                if (Shown()) RingScene.Fail("the marker stayed on after the last link was found");
            });
        }

        // ---- lows: heat is a side channel -----------------------------------------------------------------------------------------------------------------

        private static void CheckGa4HeatFollowsWhatIsFound(Map map)
        {
            Guarded("gap analysis 4 low: heat rises with what a find gives away", map, r =>
            {
                var s = new RingScene(map, r);
                // A tap that overhears nothing new does not heat the ring (it would tell the warden the caller is in one).
                Pawn dealer = s.Inmate("Zdealer832", dealer: true);
                StoryArc ring = s.Form(dealer);
                s.Assess(dealer);
                ring.ring.heat = 0f;
                bool found = RingSystem.Notify_Tapped(dealer, force: true);
                if (found || ring.ring.heat != 0f) RingScene.Fail($"a tap on a ring with every link known revealed {found} and heated it to {ring.ring.heat}");
                // A find on the dealer's customer that does not trace the dealer does not heat the ring whose dealer is unknown.
                Pawn dealer2 = s.Inmate("Zdealer833", dealer: true), customer = s.Inmate("Zcustomer833");
                customer.Inmate().lastDealerId = dealer2.thingIDNumber;
                StoryArc ring2 = s.Form(dealer2);
                int unlucky = 0;
                for (int i = 0; i < 60 && !s.Link(ring2, RingRole.Dealer).known; i++)
                {
                    ring2.ring.heat = 0f;
                    RingSystem.Notify_Found(customer, customer.Inmate(), null, traced: false);
                    if (!s.Link(ring2, RingRole.Dealer).known && ring2.ring.heat != 0f) unlucky++;
                }
                if (unlucky > 0) RingScene.Fail($"a find on a customer that did not trace the dealer heated the ring {unlucky} times (the dealer was not found)");
            });
        }

        // ---- lows: turned couriers ---------------------------------------------------------------------------------------------------------------------------

        private static void CheckGa4TurnedCouriersAndTheOneToTurn(Map map)
        {
            Guarded("gap analysis 4 low: turned couriers stop working and free their place", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer834", s.ravens, dealer: true), c1 = s.Inmate("Zcourier834a", s.ravens), c2 = s.Inmate("Zcourier834b", s.ravens), c3 = s.Inmate("Zcourier834c", s.ravens);
                StoryArc ring = s.Form(dealer);
                RingLink l1 = s.Recruit(ring), l2 = s.Recruit(ring);
                if (l1 == null || l2 == null || s.Recruit(ring) != null)
                {
                    RingScene.Fail("the check's set-up failed: two couriers were not recruited, or a third was");
                    return;
                }
                // Both couriers are turned: they work for the warden, so a recruit may take a place.
                l1.turned = l2.turned = true;
                if (s.Recruit(ring) == null) RingScene.Fail("two turned couriers still fill the places for couriers (a new recruit is refused)");
                // A turned courier hands nothing to the dealer.
                Pawn courier = PawnOfLink(map, l1);
                s.Place(dealer, 1);
                s.Place(courier, 2);
                ContrabandUtility.Give(courier, s.drug);
                l1.carried = 1;
                if (RingSystem.TryHandover(ring, map, force: true)) RingScene.Fail("a turned courier handed the dealer what the ring brought for them");
                // The letter's "turn" takes the courier the ring counts on most.
                var links = new List<RingLink> { new RingLink { role = RingRole.Courier, carried = 0 }, new RingLink { role = RingRole.Courier, carried = 5 } };
                if (RingSystem.BestToTurn(links) != links[1]) RingScene.Fail("the letter's \"turn\" took the first courier, not the one carrying the ring's stock");
            });
        }

        private static Pawn PawnOfLink(Map map, RingLink l) => map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.thingIDNumber == l.pawnId);

        // ---- lows: the supply cut ends when the ring ends -------------------------------------------------------------------------------------------

        private static void CheckGa4SupplyCutOutlastsTheRing(Map map)
        {
            Guarded("gap analysis 4 low: the supply cut is the inmate's, not the ring's", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn dealer = s.Inmate("Zdealer835", dealer: true), courier = s.Inmate("Zcourier835");
                courier.Inmate().lastDealerId = dealer.thingIDNumber;
                StoryArc ring = s.Form(dealer);
                s.Recruit(ring);
                s.Assess(dealer);
                s.Assess(courier);
                if (RingSystem.Bust(ring, map) != 2 || !RingSystem.SupplyCut(courier)) RingScene.Fail("the check's set-up failed: the bust did not cut the courier off");
                ring.done = true;
                if (!RingSystem.SupplyCut(courier) || !RingSystem.SupplyCut(dealer)) RingScene.Fail("the ring ending gave the inmates taken out of it their visits, post and stock back before the cut was over");
                courier.Inmate().supplyCutUntilTick = -1;
                ring.done = false;
            });
        }

        // ---- lows: an officer on two rings ----------------------------------------------------------------------------------------------------------------

        private static void CheckGa4OfficerOnTwoRingsIsTracedIntoBoth(Map map)
        {
            Guarded("gap analysis 4 low: an officer on two rings is traced into both", map, r =>
            {
                var s = new RingScene(map, r);
                Pawn d1 = s.Inmate("Zdealer836", s.ravens, dealer: true), d2 = s.Inmate("Zdealer837", s.ravens, dealer: true), customer = s.Inmate("Zcustomer836");
                Pawn officer = s.Officer("Zofficer836", d1);
                CompStaff sc = officer.GetComp<CompStaff>();
                (sc.patron, sc.patronGang) = (null, s.ravens);
                StoryArc ring1 = s.Form(d1), ring2 = s.Form(d2);
                RingSystem.Sync(ring1, map);
                RingSystem.Sync(ring2, map);
                RingLink o1 = s.Link(ring1, RingRole.Officer), o2 = s.Link(ring2, RingRole.Officer);
                if (o1 == null || o2 == null)
                {
                    RingScene.Fail($"the check's set-up failed: the officer is on ring one {o1 != null} and ring two {o2 != null}");
                    return;
                }
                float heat1 = ring1.ring.heat, heat2 = ring2.ring.heat;
                RingSystem.Notify_Found(customer, customer.Inmate(), officer, traced: true);
                if (!o1.known || !o2.known || ring1.ring.heat <= heat1 || ring2.ring.heat <= heat2)
                    RingScene.Fail($"an officer on the payroll of two dealers, traced, was found in ring one {o1.known} and ring two {o2.known} (both expected; heat {ring1.ring.heat - heat1}/{ring2.ring.heat - heat2})");
            });
        }
    }
}
