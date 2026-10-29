using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Keep-apart orders (<see cref="KeepApart"/>, gap analysis 6, 1): a pair in one cell is split at the review and the chronicle says why; with no second wing the pair stays and the file and Intel lines say so, with one
    /// it is moved to it; an arrival is never put in the other's cell (and, with the term off, is: the negative run); the yard pick keeps out of the other's patch (draws, and real ticks); a class seats one of the pair;
    /// two of a pair do not hold jobs in one room; the trial, green-light and debt letters offer the order and write it, and so does a cell request answered yes; the daily pass drops an order whose party left and
    /// the list stops at its cap; the compliance reading does not read it. Each is rolled back (<see cref="Rollback"/>: the book too). The review checks run in the <see cref="CellRig"/> of the cell checks, the yard ones
    /// in a <see cref="KeptScene"/>. The saveload plan's seed and snapshot are at the end. Written without a game run: none of it has been run yet.
    /// </summary>
    public static partial class AutoTest
    {
        private static void ApartFail(string what) => problems0.Add("keep apart: " + what);

        private static void CheckKeepApart(Map map)
        {
            CheckApartCellSplit(map);
            CheckApartWings(map);
            CheckApartArrival(map);
            CheckApartYardPick(map);
            CheckApartClass(map);
            CheckApartJobs(map);
            CheckApartJobRotation(map);
            CheckApartJobsKept(map);
            CheckApartLetters(map);
            CheckApartRequest(map);
            CheckApartBook(map);
            CheckApartRotation(map);
            CheckApartWingPaint(map);
            CheckApartOnlyPartnerBed(map);
            CheckApartCompliance(map);
        }

        /// <summary>The cells the rig has an inmate in, as a failure's text.</summary>
        private static string ApartCells(CellRig s, params Pawn[] pawns) => string.Join(", ", pawns.Select(p => $"{p.LabelShort} in cell {s.CellOf(p)}"));

        // ---- the review splits a pair in one cell, and the arrival's bed --------------------------------------------------------------

        /// <summary>
        /// Two inmates in one cell with a free bed in another: with no order the review leaves them, with the order it moves one (the chronicle says whom it was for), the moved one cannot be moved back, and with the placement's
        /// term off (the negative run) the pair stays in one cell.
        /// </summary>
        private static void CheckApartCellSplit(Map map)
        {
            Guarded("keep apart: a pair in one cell is split at the review", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "keep apart cell", 2, 1, 1);
                if (s == null) return;
                s.Grades(1, 1, 1);
                Pawn a = s.Inmate(0, 0, 1), b = s.Inmate(0, 1, 1);
                if (s.CellOf(a) != 0 || s.CellOf(b) != 0)
                {
                    ApartFail($"cell split: premise: the pair is not in the first cell ({ApartCells(s, a, b)})");
                    return;
                }
                int moved = Cellmates.DailyReview(map);
                if (moved != 0 || s.CellOf(a) != 0 || s.CellOf(b) != 0)
                {
                    ApartFail($"cell split: premise: with no order the review moved {moved} ({ApartCells(s, a, b)}); two calm cellmates with no clash should have been left");
                    return;
                }
                if (KeepApart.Add(a, b, "feud", silent: true) != KeepApartResult.Added || !KeepApart.Ordered(a, b) || !KeepApart.Ordered(b, a))
                {
                    ApartFail("cell split: premise: the order was not written, or is not read both ways");
                    return;
                }
                // The negative run first: with the placement's term off the review has nothing against one cell, and they stay in it.
                KeepApart.TermsOff = true;
                r.Add(() => KeepApart.TermsOff = false);
                moved = Cellmates.DailyReview(map);
                KeepApart.TermsOff = false;
                if (moved != 0 || s.CellOf(a) != 0 || s.CellOf(b) != 0) ApartFail($"cell split: with the term off the review moved {moved} ({ApartCells(s, a, b)}): the negative run does not leave the pair in one cell");
                // With it: one of them goes to another cell, and the chronicle line names the other.
                int entries = r.NewEntries.Count();
                moved = Cellmates.DailyReview(map);
                Pawn mover = s.CellOf(a) != 0 ? a : s.CellOf(b) != 0 ? b : null, stays = mover == a ? b : a;
                if (moved != 1 || mover == null || s.CellOf(a) == s.CellOf(b))
                {
                    ApartFail($"cell split: the review moved {moved} and left the kept-apart pair as {ApartCells(s, a, b)} (a free bed was in cells 1 and 2)");
                    return;
                }
                string line = "RP_Chr_CellMoveApartCell".Translate(mover.LabelShort, stays.LabelShort).Resolve();
                if (!r.NewEntries.Skip(entries).Any(e => e.text == line)) ApartFail($"cell split: no chronicle line \"{line}\" (saw: {string.Join(" | ", r.NewEntries.Skip(entries).Select(e => e.text))})");
                // The bed they left is free and in the other's cell: they are not moved back into it, whoever asks, and no cell scores it as a fit.
                Building_Bed left = mover == a ? s.Bed(0, 0) : s.Bed(0, 1);
                float fit = Cellmates.Fit(left, mover.Inmate());
                if (Cellmates.Move(mover, mover.Inmate(), left) || s.CellOf(mover) == 0) ApartFail($"cell split: the mover was moved back into the other's cell ({ApartCells(s, a, b)})");
                if (fit > KeepApart.SameCellTerm / 2f) ApartFail($"cell split: a bed in the other's cell has the fit {fit}, not the pair term (about {KeepApart.SameCellTerm})");
                // What this tests: a pair that has been parted into two cells is not moved again by the next review (the parted pair is stable). It does not test the wing rule (no bed on another wing: CheckApartWings does), but it
                // only holds while no free bed is outside the rig's wing, which is asserted here; with one the review may rightly move one of them to it.
                bool outside = IntakeSystem.FreeBedsInOrder(map, s.level, s.gender).Exists(b0 => IntakeSystem.WingOf(b0) != s.level);
                int again = Cellmates.DailyReview(map);
                if (outside) RPLog.Message($"[RimPrison:test] keep apart: the parted pair's next review was not judged, a bed outside the rig's wing is free (the review moved {again})");
                else if (again != 0 || s.CellOf(a) == s.CellOf(b)) ApartFail($"cell split: the next review moved {again} and left the pair as {ApartCells(s, a, b)}; a parted pair with no free bed outside the wing should be left alone");
                RPLog.Message($"[RimPrison:test] keep apart: the pair in one cell was split ({ApartCells(s, a, b)}); the negative run left them together");
            });
        }

        /// <summary>
        /// Two wings: a pair in two cells of one wing with no bed free anywhere else stays, and the file and Intel lines say there is no bed on another wing; with a free bed in no wing the review moves one of them to it, and the
        /// chronicle says it was for the wing.
        /// </summary>
        private static void CheckApartWings(Map map)
        {
            Guarded("keep apart: a pair that shares a wing is moved only when a bed on another wing is free", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "keep apart wings", 1, 1, 1);
                if (s == null) return;
                if (s.level.wingOnly)
                {
                    RPLog.Message("[RimPrison:test] keep apart: wings not run, the rig's level keeps to its wing");
                    return;
                }
                s.Grades(1, 1, 1);
                // The third cell is in no wing (its paint taken off): a bed for them that is not in the wing.
                foreach (IntVec3 c in s.RoomOf(2).Cells) s.pm.zones.Set(c, null);
                Pawn a = s.Inmate(0, 0, 1), b = s.Inmate(1, 0, 1), filler = s.Inmate(2, 0, 1);
                if (IntakeSystem.WingOf(s.Bed(0, 0)) != s.level || IntakeSystem.WingOf(s.Bed(1, 0)) != s.level || IntakeSystem.WingOf(s.Bed(2, 0)) != null)
                {
                    ApartFail($"wings: premise: the rig's wings are not as built (cells 0 and 1 in {s.level.defName}, cell 2 in none; they are in {IntakeSystem.WingOf(s.Bed(0, 0))?.defName}, {IntakeSystem.WingOf(s.Bed(1, 0))?.defName}, {IntakeSystem.WingOf(s.Bed(2, 0))?.defName})");
                    return;
                }
                if (KeepApart.Add(a, b, "feud", silent: true) != KeepApartResult.Added)
                {
                    ApartFail("wings: premise: the order was not written");
                    return;
                }
                KeepApartOrder order = KeepApart.OrderBetween(a, b);
                int now = Find.TickManager.TicksGame;
                // No bed on another wing (the one that was is taken): they stay, and the line says why: each file names its own inmate, Intel's both.
                int moved = Cellmates.DailyReview(map);
                KeepApart.Refresh(order, now);
                if (moved != 0 || s.CellOf(a) != 0 || s.CellOf(b) != 1) ApartFail($"wings: with no bed on another wing the review moved {moved} ({ApartCells(s, a, b)})");
                if (order.blocker != "RP_KeepApartNoWing") ApartFail($"wings: the order's blocker is \"{order.blocker}\", not the wing's");
                foreach (Pawn who in new[] { a, b })
                {
                    string own = "RP_KeepApartNoWing".Translate(who.LabelShort).Resolve();
                    if (!ITab_InmateFile.StatusLines(who, who.Inmate(), now).Any(l => l.Item1 == own)) ApartFail($"wings: {who.LabelShort}'s file does not say \"{own}\" (their own name)");
                }
                string both = "RP_KeepApartNoWingEither".Translate(a.LabelShort, b.LabelShort).Resolve();
                if (!KeepApart.TroubleLines(order).Contains(both)) ApartFail($"wings: Intel's trouble lines do not say \"{both}\"");
                string listed = "RP_KeepApartFileLine".Translate(order.nameB, (order.sinceTick / GenDate.TicksPerDay + 1).ToString(), KeepApart.ReasonText("feud")).Resolve();
                if (!ITab_InmateFile.StatusLines(a, a.Inmate(), now).Any(l => l.Item1 == listed)) ApartFail($"wings: the file does not list the order as \"{listed}\"");
                if (!KeepApart.OrderLine(order).Contains(KeepApart.ReasonText("feud"))) ApartFail("wings: Intel's line for the order does not name its reason");
                // A bed free in no wing: one of them goes to it, and the line of the chronicle says it was for the wing.
                filler.ownership.UnclaimBed();
                Cellmates.InvalidateCells();
                KeepApart.Refresh(order, now);
                if (order.blocker != null) ApartFail($"wings: with a bed free in no wing the order still says \"{order.blocker}\"");
                int entries = r.NewEntries.Count();
                moved = Cellmates.DailyReview(map);
                Pawn mover = s.CellOf(a) == 2 ? a : s.CellOf(b) == 2 ? b : null;
                if (moved != 1 || mover == null)
                {
                    ApartFail($"wings: with a bed free in no wing the review moved {moved} ({ApartCells(s, a, b)})");
                    return;
                }
                Pawn other = mover == a ? b : a;
                string line = "RP_Chr_CellMoveApartWing".Translate(mover.LabelShort, other.LabelShort).Resolve();
                if (!r.NewEntries.Skip(entries).Any(e => e.text == line)) ApartFail($"wings: no chronicle line \"{line}\" (saw: {string.Join(" | ", r.NewEntries.Skip(entries).Select(e => e.text))})");
            });
        }

        /// <summary>
        /// An arrival whose friend lives in a cell with a free bed would go there (the control); kept apart from the friend, they are given the other cell; with the term off (the negative run) they go back to the friend's.
        /// </summary>
        private static void CheckApartArrival(Map map)
        {
            Guarded("keep apart: an arrival is not put in the cell of the one they are kept apart from", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "keep apart arrival", 2, 1);
                if (s == null) return;
                s.Grades(1, 1);
                Pawn a = s.Inmate(0, 0, 1), arrival = s.Inmate(1, 0, 1);
                arrival.ownership.UnclaimBed();
                Cellmates.InvalidateCells();
                arrival.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, a);
                if (!PrisonRelations.Friends(arrival, a)) { ApartFail("arrival: premise: the friendship did not take"); return; }
                List<Building_Bed> Free() => IntakeSystem.FreePrisonerBeds(map, s.level, s.gender);
                CompInmate ac = arrival.Inmate();
                Building_Bed pick = Cellmates.PickBed(Free(), ac);
                if (pick == null || pick.GetRoom() != s.RoomOf(0))
                {
                    ApartFail($"arrival: premise: an arrival whose friend lives in cell 0 was not given the free bed there ({(pick == null ? "no bed" : "a bed in room " + pick.GetRoom()?.ID)}); the cells are {string.Join(", ", s.beds.Select((c, i) => $"{i}: room {s.RoomOf(i)?.ID}"))}");
                    return;
                }
                if (KeepApart.Add(arrival, a, "feud", silent: true) != KeepApartResult.Added) { ApartFail("arrival: premise: the order was not written"); return; }
                pick = Cellmates.PickBed(Free(), ac);
                if (pick == null || pick.GetRoom() != s.RoomOf(1)) ApartFail($"arrival: kept apart from their friend they were given {(pick == null ? "no bed" : "a bed in room " + pick.GetRoom()?.ID)}, not the free bed of cell 1 (room {s.RoomOf(1)?.ID})");
                KeepApart.TermsOff = true;
                r.Add(() => KeepApart.TermsOff = false);
                pick = Cellmates.PickBed(Free(), ac);
                KeepApart.TermsOff = false;
                if (pick == null || pick.GetRoom() != s.RoomOf(0)) ApartFail("arrival: with the term off the arrival was not put back in their friend's cell: the check cannot tell the term from the rest");
            });
        }

        // ---- the yard, the class, the job ---------------------------------------------------------------------------------------------

        /// <summary>
        /// A free-time inmate beside a small yard patch picks it, unless the one they are kept apart from stands in it: then no pick, over a day's worth of draws, lands in it, nor does the free-time worker's own pick (the other pinned in it); lifted,
        /// it is picked again. Two patches (a <see cref="KeptScene"/>), both open to all.
        /// </summary>
        private static void CheckApartYardPick(Map map)
        {
            Guarded("keep apart: the yard pick keeps out of the other's patch", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "keep apart yard pick", RPDefOf.RP_Yard);
                if (s == null) return;
                Pawn p = KeptInmate(map, r, RPSecurityDefOf.RP_Medium, RPDefOf.RP_FreeTime), other = KeptInmate(map, r, RPSecurityDefOf.RP_Medium, RPDefOf.RP_FreeTime);
                string Seen() => $"{KeptPawnText(s.pm, p)}; {KeptPawnText(s.pm, other)}";
                void Pin(Pawn x) => x.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait, 2000), JobCondition.InterruptForced);
                KeptPlace(other, s.smallCell);
                KeptPlace(p, s.besideSmall);
                const int Draws = 600;
                var control = s.Draw(p, Draws);
                if (control.small == 0)
                {
                    RPLog.Message($"[RimPrison:test] keep apart: yard pick not run, an inmate beside the small patch never picked it with nobody kept from them ({KeptDrawText(control)}; {Seen()})");
                    return;
                }
                if (KeepApart.Add(p, other, "feud", silent: true) != KeepApartResult.Added) { ApartFail("yard pick: premise: the order was not written"); return; }
                // The other stands in the small patch and stays: a long Wait, so that their pather is not heading for another patch (which would be avoided as well: with every patch avoided the pick is the product's last resort).
                Pin(other);
                var listed = new List<int>();
                KeepApart.PartnerPatches(p, s.pm, listed);
                if (listed.Count != 1 || listed[0] != s.Small)
                {
                    ApartFail($"yard pick: premise: the patches the other is in or walking to are [{string.Join(", ", listed)}], not just the small patch {s.Small} (seen: {Seen()}; {s.Layout()})");
                    return;
                }
                var apart = s.Draw(p, Draws);
                if (apart.small != 0 || apart.big + apart.other == 0) ApartFail($"yard pick: with the one they are kept apart from standing in the small patch, the picks were {KeptDrawText(apart)} (seen: {Seen()}; {s.Layout()})");
                // The free-time regime's own pick: the worker, asked for a job by an inmate beside the small patch (in no patch, so it takes the cell pick, not a wander within a patch) with the other pinned in the small patch, three
                // hundred times. Real ticks judged about one pick a run (once in the yard zone the inmate only wanders), and the pick made with every patch avoided is the product's last resort, not a fault.
                // Without the avoidance (KeptRooms.PickCell leaving out KeepApart.PartnerPatches) the nearest patch - the small one - is picked about nine draws in ten: about 270 of 300 fail.
                const int WorkerDraws = 300;
                int inSmall = 0, inBig = 0, noJob = 0;
                for (int i = 0; i < WorkerDraws; i++)
                {
                    Job job = RegimeActivityWorker.WanderInZones(p, s.pm, RPDefOf.RP_FreeTime.zones);
                    if (job == null || !job.targetA.IsValid) noJob++;
                    else if (s.pm.zones.PatchAt(job.targetA.Cell) == s.Small) inSmall++;
                    else if (s.pm.zones.PatchAt(job.targetA.Cell) == s.Big) inBig++;
                }
                if (inSmall > 0 || inBig == 0) ApartFail($"yard pick: the free-time worker, asked {WorkerDraws} times for an inmate beside the small patch with the other pinned in it, gave {inSmall} in the small patch, {inBig} in the big one, {noJob} none (seen: {Seen()}; {s.Layout()})");
                // The other leaves the patch: it is free to be picked again (an order does not close a patch).
                KeptPlace(other, s.bigCell);
                Pin(other);
                var gone = s.Draw(p, Draws);
                if (gone.small == 0) ApartFail($"yard pick: with the other out of the small patch it was still never picked ({KeptDrawText(gone)}; {Seen()})");
            });
        }

        /// <summary>The first day (of the first 200) on which <paramref name="first"/> goes before <paramref name="second"/> (<see cref="KeepApart.Precedes(Pawn, Pawn, int)"/>), or -1.</summary>
        private static int ApartDay(Pawn first, Pawn second)
        {
            for (int day = 0; day < 200; day++)
                if (KeepApart.Precedes(first, second, day)) return day;
            return -1;
        }

        /// <summary>
        /// A class: of two inmates kept apart the one who goes first that day has the seat and the other is not admitted (the day forced, one day for each of the two to go first), the unrelated third is admitted either way;
        /// a cohort with no pair in it is the same list; lifted, all three are admitted.
        /// </summary>
        private static void CheckApartClass(Map map)
        {
            Guarded("keep apart: a class seats one of a pair", map, r =>
            {
                r.Add(() => KeepApart.TestDay = null);
                KeptScene s = KeptScene.Begin(map, r, "keep apart class", RPDefOf.RP_Yard);
                if (s == null) return;
                SecurityLevelDef med = RPSecurityDefOf.RP_Medium;
                Pawn m1 = KeptInmate(map, r, med), m2 = KeptInmate(map, r, med), m3 = KeptInmate(map, r, med);
                Pawn[] cohort = { m1, m2, m3 };
                var prog = new ProgramDef { zone = s.zone, standing = true };
                var admitted = new List<Pawn>();
                int capacity = 0;
                bool busy = false;
                bool Run() => ProgramSystem.TryFindVenue(map, s.pm, prog, out _, out capacity, out busy, cohort, admitted);
                string Names() => string.Join("+", admitted.Select(p => p.LabelShort));
                if (!Run() || admitted.Count != 3)
                {
                    string seen = $"a standing class of three admitted {admitted.Count} ({Names()}) with no order, the venue has {capacity} places{(busy ? " and is claimed" : "")} ({KeptZoneText(s.pm, s.zone)})";
                    // (A venue too small for three, or held by a gang's turf, is a prison that cannot give the premise; anything else is a control that failed.)
                    if (busy || capacity > 0 && capacity < 3) RPLog.Message("[RimPrison:test] keep apart: class not run, " + seen);
                    else ApartFail("class: control: " + seen);
                    return;
                }
                if (!ReferenceEquals(KeepApart.Cohort(cohort), cohort)) ApartFail("class: a cohort with no order in force was copied");
                if (KeepApart.Add(m1, m2, "warden", silent: true) != KeepApartResult.Added) { ApartFail("class: premise: the order was not written"); return; }
                int first = ApartDay(m1, m2), second = ApartDay(m2, m1);
                if (first < 0 || second < 0)
                {
                    ApartFail($"class: premise: in 200 days {(first < 0 ? m1 : m2).LabelShort} never went before {(first < 0 ? m2 : m1).LabelShort}");
                    return;
                }
                // Both directions: a day the first of the pair goes first, and a day the second does.
                foreach ((int day, Pawn keeps, Pawn waits) in new[] { (first, m1, m2), (second, m2, m1) })
                {
                    KeepApart.TestDay = day;
                    if (!Run() || admitted.Count != 2 || !admitted.Contains(keeps) || !admitted.Contains(m3) || admitted.Contains(waits))
                        ApartFail($"class: on day {day}, with {keeps.LabelShort} going before {waits.LabelShort}, a class of {string.Join("+", cohort.Select(p => p.LabelShort))} with the first two kept apart admitted {Names()}, not {keeps.LabelShort} and {m3.LabelShort}");
                }
                KeepApart.TestDay = null;
                KeepApart.Lift(KeepApart.OrderBetween(m1, m2), silent: true);
                if (!Run() || admitted.Count != 3) ApartFail($"class: with the order lifted the class admitted {Names()}, not all three");
            });
        }

        /// <summary>
        /// Two made-up inmates able to hold the workshop job (the programs it asks for passed), for a job check: the real inmates' jobs are put back at the end. False, with the reason logged, when the live prison cannot give the premise
        /// (the job is not one that medium security inmates may hold, or the kitchen's stand-down is on): the pair is not made then.
        /// </summary>
        private static bool ApartShopPair(Map map, Rollback r, out InmateJobDef shop, out Pawn x, out Pawn y)
        {
            shop = DefDatabase<InmateJobDef>.GetNamed("RP_JobWorkshop");
            var real = map.mapPawns.PrisonersOfColony.Select(p => p.Inmate()).Where(c => c != null).Select(c => (c, c.job, c.jobLocked)).ToList();
            r.Add(() =>
            {
                foreach ((CompInmate c, InmateJobDef job, bool locked) in real) (c.job, c.jobLocked) = (job, locked);
            });
            r.Add(() => KeepApart.TestDay = null);
            x = KeptInmate(map, r, RPSecurityDefOf.RP_Medium);
            y = KeptInmate(map, r, RPSecurityDefOf.RP_Medium);
            foreach (Pawn p in new[] { x, y })
            {
                CompInmate c = p.Inmate();
                c.tension = 0f;
                if (shop.requiredProgram is not { } prog || c.passedPrograms.Contains(prog)) continue;
                c.passedPrograms.Add(prog);
                r.Add(() => c.passedPrograms.Remove(prog));
            }
            if (LaborUtility.Eligible(x.Inmate(), shop) && LaborUtility.Eligible(y.Inmate(), shop)) return true;
            RPLog.Message($"[RimPrison:test] keep apart: jobs not run, a medium security inmate who has passed what the workshop asks for is not eligible for {shop.defName} (tension limit {shop.maxTension}, stood down {FoodScare.StoodDown(shop)})");
            return false;
        }

        /// <summary>
        /// Two inmates holding the workshop job: with no order both keep it; kept apart, the one who goes first that day keeps it and the other has none in that room (and is given none), a job the warden locked is kept and the other gives theirs
        /// up whoever goes first, two locked jobs are left as set - the day forced, a day for each of the two to go first. The real inmates' jobs are put back.
        /// </summary>
        private static void CheckApartJobs(Map map)
        {
            Guarded("keep apart: a pair does not hold jobs in one room", map, r =>
            {
                if (!ApartShopPair(map, r, out InmateJobDef shop, out Pawn low, out Pawn high)) return;
                CompInmate lc = low.Inmate(), hc = high.Inmate();
                void Reset(bool lowLocked = false, bool highLocked = false)
                {
                    (lc.job, lc.jobLocked, lc.tension) = (shop, lowLocked, 0f);
                    (hc.job, hc.jobLocked, hc.tension) = (shop, highLocked, 0f);
                }
                string Jobs() => $"{low.LabelShort} (id {low.thingIDNumber}, locked {lc.jobLocked}) {lc.job?.defName ?? "none"}, {high.LabelShort} (id {high.thingIDNumber}, locked {hc.jobLocked}) {hc.job?.defName ?? "none"}";
                Reset();
                LaborUtility.AssignJobs(map);
                if (lc.job != shop || hc.job != shop)
                {
                    ApartFail($"jobs: control: with no order and both able to hold it, the two did not both keep the workshop job ({Jobs()})");
                    return;
                }
                if (KeepApart.Add(low, high, "warden", silent: true) != KeepApartResult.Added) { ApartFail("jobs: premise: the order was not written"); return; }
                int lowDay = ApartDay(low, high), highDay = ApartDay(high, low);
                if (lowDay < 0 || highDay < 0)
                {
                    ApartFail($"jobs: premise: in 200 days {(lowDay < 0 ? low : high).LabelShort} never went before {(lowDay < 0 ? high : low).LabelShort}");
                    return;
                }
                // Starts from both holding the workshop each time (the rotation has its own check): one pass, with the roles as the day gives them.
                void Expect(string what, int day, bool lowLocked, bool highLocked, bool lowKeeps, bool highKeeps)
                {
                    KeepApart.TestDay = day;
                    Reset(lowLocked, highLocked);
                    LaborUtility.AssignJobs(map);
                    bool lowOk = lowKeeps ? lc.job == shop : lc.job?.zone != shop.zone, highOk = highKeeps ? hc.job == shop : hc.job?.zone != shop.zone;
                    if (!lowOk || !highOk) ApartFail($"jobs: on day {day}, {what}: {low.LabelShort} should {(lowKeeps ? "keep" : "have none in")} the workshop and {high.LabelShort} {(highKeeps ? "keep" : "have none in")} it ({Jobs()})");
                }
                foreach ((int day, bool lowFirst) in new[] { (lowDay, true), (highDay, false) })
                {
                    string first = (lowFirst ? low : high).LabelShort;
                    Expect($"with both jobs free, {first} going first", day, false, false, lowFirst, !lowFirst);
                    Expect($"with {first}'s job locked by the warden, {first} going first", day, lowFirst, !lowFirst, lowFirst, !lowFirst);
                    Expect($"with the job of the one who does not go first locked by the warden ({first} goes first)", day, !lowFirst, lowFirst, !lowFirst, lowFirst);
                    Expect($"with both jobs locked by the warden ({first} goes first)", day, true, true, true, true);
                }
                KeepApart.TestDay = null;
            });
        }

        /// <summary>The jobs but <paramref name="keep"/> turned away from everyone (a tension limit below any inmate's): a pair's loser who is dropped from the workshop is then left with no job, instead of one the job pass finds for them (cleaning, which has no zone and no security). Put back when the check ends.</summary>
        private static void ApartOnlyJobs(Rollback r, params InmateJobDef[] keep)
        {
            foreach (InmateJobDef job in DefDatabase<InmateJobDef>.AllDefsListForReading)
            {
                if (keep.Contains(job)) continue;
                float limit = job.maxTension;
                job.maxTension = -1f;
                r.Add(() => job.maxTension = limit);
            }
        }

        /// <summary>
        /// The room is shared out over the days between two with one job between them: with the real <see cref="LaborUtility.AssignJobs"/> run on successive days and the jobs not put back between passes (every other job turned away, so
        /// the one dropped from the workshop has no job), the one who goes first each day holds the workshop that day (the other has none in it), each of the pair holds it on some day, nothing changes hands between passes of one day;
        /// and a job the warden locked is never taken, whoever goes first.
        /// </summary>
        private static void CheckApartJobRotation(Map map)
        {
            Guarded("keep apart: a pair take turns at the workshop", map, r =>
            {
                if (!ApartShopPair(map, r, out InmateJobDef shop, out Pawn a, out Pawn b)) return;
                ApartOnlyJobs(r, shop);
                CompInmate ac = a.Inmate(), bc = b.Inmate();
                string Jobs() => $"{a.LabelShort} {ac.job?.defName ?? "none"} (locked {ac.jobLocked}), {b.LabelShort} {bc.job?.defName ?? "none"} (locked {bc.jobLocked})";
                (ac.job, ac.jobLocked, bc.job, bc.jobLocked) = (shop, false, shop, false);
                LaborUtility.AssignJobs(map);
                if (ac.job != shop || bc.job != shop)
                {
                    ApartFail($"job rotation: control: with no order and both able to hold it, the two did not both keep the workshop job ({Jobs()})");
                    return;
                }
                if (KeepApart.Add(a, b, "warden", silent: true) != KeepApartResult.Added) { ApartFail("job rotation: premise: the order was not written"); return; }
                int aDays = 0, bDays = 0;
                for (int day = 0; day < 40; day++)
                {
                    KeepApart.TestDay = day;
                    Pawn first = KeepApart.Precedes(a, b, day) ? a : b, dayHolder = null;
                    // (Three passes a day, the jobs left as the last one left them: the hourly pass.)
                    for (int pass = 0; pass < 3; pass++)
                    {
                        LaborUtility.AssignJobs(map);
                        bool aHolds = ac.job?.zone == shop.zone, bHolds = bc.job?.zone == shop.zone;
                        if (aHolds && bHolds)
                        {
                            ApartFail($"job rotation: on day {day}, pass {pass}, the pair kept apart both hold a job in the workshop ({Jobs()})");
                            return;
                        }
                        Pawn holder = aHolds ? a : bHolds ? b : null;
                        if (pass == 0)
                        {
                            dayHolder = holder;
                            if (holder != first) ApartFail($"job rotation: on day {day} {first.LabelShort} goes first but the workshop is held by {holder?.LabelShort ?? "nobody"} ({Jobs()})");
                            if (holder == a) aDays++;
                            else if (holder == b) bDays++;
                        }
                        else if (holder != dayHolder) ApartFail($"job rotation: on day {day} the workshop changed hands between passes, from {dayHolder?.LabelShort ?? "nobody"} to {holder?.LabelShort ?? "nobody"} ({Jobs()})");
                    }
                }
                if (aDays == 0 || bDays == 0) ApartFail($"job rotation: in 40 days {(aDays == 0 ? a : b).LabelShort} never held the workshop ({a.LabelShort} {aDays} days, {b.LabelShort} {bDays})");
                // A job the warden locked is not taken, on the days the other goes first as on the others.
                (ac.job, ac.jobLocked, bc.job, bc.jobLocked) = (shop, true, null, false);
                int otherFirst = 0;
                for (int day = 0; day < 40; day++)
                {
                    KeepApart.TestDay = day;
                    bool aFirst = KeepApart.Precedes(a, b, day);
                    if (!aFirst) otherFirst++;
                    LaborUtility.AssignJobs(map);
                    if (ac.job != shop || bc.job?.zone == shop.zone)
                    {
                        ApartFail($"job rotation: on day {day}, with {a.LabelShort}'s workshop job locked by the warden{(aFirst ? "" : " and the other going first")}, the job was taken or shared ({Jobs()})");
                        break;
                    }
                }
                if (otherFirst == 0) ApartFail($"job rotation: the locked job was never up against a pair member going first in 40 days, so the check proved nothing ({Jobs()})");
                KeepApart.TestDay = null;
            });
        }

        /// <summary>
        /// The rotation hands a room over only to one with no job: two of a pair kept apart who each have a job (the workshop and the laundry, both open to medium security) keep them, over 40 forced days of the real
        /// <see cref="LaborUtility.AssignJobs"/>, whoever goes first; and one kept apart from two who each hold a job, with none of their own, takes one of the two jobs on a day they go before both, not both.
        /// </summary>
        private static void CheckApartJobsKept(Map map)
        {
            Guarded("keep apart: a pair with a job each do not swap them", map, r =>
            {
                if (!ApartShopPair(map, r, out InmateJobDef shop, out Pawn a, out Pawn b)) return;
                InmateJobDef laundry = DefDatabase<InmateJobDef>.GetNamed("RP_JobLaundry");
                ApartOnlyJobs(r, shop, laundry);
                CompInmate ac = a.Inmate(), bc = b.Inmate();
                if (!LaborUtility.Eligible(ac, laundry) || !LaborUtility.Eligible(bc, laundry) || laundry.zone == shop.zone || laundry.zone == null || shop.zone == null)
                {
                    RPLog.Message($"[RimPrison:test] keep apart: jobs kept not run, a medium security inmate is not eligible for {laundry.defName}, or it shares a room with {shop.defName}");
                    return;
                }
                string Jobs() => $"{a.LabelShort} {ac.job?.defName ?? "none"}, {b.LabelShort} {bc.job?.defName ?? "none"}";
                (ac.job, ac.jobLocked, bc.job, bc.jobLocked) = (shop, false, laundry, false);
                LaborUtility.AssignJobs(map);
                if (ac.job != shop || bc.job != laundry)
                {
                    ApartFail($"jobs kept: control: with no order the two did not keep the jobs they were given ({Jobs()})");
                    return;
                }
                if (KeepApart.Add(a, b, "warden", silent: true) != KeepApartResult.Added) { ApartFail("jobs kept: premise: the order was not written"); return; }
                int aFirst = 0, bFirst = 0;
                for (int day = 0; day < 40; day++)
                {
                    KeepApart.TestDay = day;
                    if (KeepApart.Precedes(a, b, day)) aFirst++;
                    else bFirst++;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        LaborUtility.AssignJobs(map);
                        if (ac.job != shop || bc.job != laundry)
                        {
                            ApartFail($"jobs kept: on day {day}, pass {pass}, {(KeepApart.Precedes(a, b, day) ? a : b).LabelShort} going first, the jobs changed hands though each had one ({Jobs()})");
                            return;
                        }
                    }
                }
                if (aFirst == 0 || bFirst == 0) ApartFail($"jobs kept: in 40 days {(aFirst == 0 ? a : b).LabelShort} never went first, so one direction of the rotation was not tried ({a.LabelShort} {aFirst} days, {b.LabelShort} {bFirst})");

                // One kept apart from two: with no job, takes one of the two rooms on a day they go before both, and the other keeps theirs.
                Pawn f = KeptInmate(map, r, RPSecurityDefOf.RP_Medium);
                CompInmate fc = f.Inmate();
                fc.tension = 0f;
                if (shop.requiredProgram is { } prog && !fc.passedPrograms.Contains(prog))
                {
                    fc.passedPrograms.Add(prog);
                    r.Add(() => fc.passedPrograms.Remove(prog));
                }
                if (KeepApart.Add(f, a, "warden", silent: true) != KeepApartResult.Added || KeepApart.Add(f, b, "warden", silent: true) != KeepApartResult.Added) { ApartFail("jobs kept: premise: the orders of the third were not written"); return; }
                int both = -1;
                for (int day = 0; day < 200 && both < 0; day++)
                    if (KeepApart.Precedes(f, a, day) && KeepApart.Precedes(f, b, day)) both = day;
                if (both < 0)
                {
                    ApartFail($"jobs kept: premise: in 200 days {f.LabelShort} never went before both of the others");
                    return;
                }
                KeepApart.TestDay = both;
                (ac.job, bc.job, fc.job, fc.jobLocked) = (shop, laundry, null, false);
                LaborUtility.AssignJobs(map);
                int lost = (ac.job == null ? 1 : 0) + (bc.job == null ? 1 : 0);
                if (fc.job == null || lost != 1 || fc.job != shop && fc.job != laundry)
                    ApartFail($"jobs kept: on day {both}, with no job and going before both {a.LabelShort} and {b.LabelShort}, {f.LabelShort} should take one of their two jobs and leave the other (took {fc.job?.defName ?? "none"}; {Jobs()})");
                KeepApart.TestDay = null;
            });
        }

        // ---- the letters, and a request ---------------------------------------------------------------------------------------------------

        private static void CheckApartLetters(Map map)
        {
            CheckApartTrialLetter(map);
            CheckApartGangLetter(map);
            CheckApartDebtLetter(map);
        }

        /// <summary>The trial letter offers keeping the witnesses apart from the accused; choosing it writes an order for each inmate witness, for the trial, and is refused once there is nothing left to write.</summary>
        private static void CheckApartTrialLetter(Map map)
        {
            Guarded("keep apart: the trial letter offers it and writes it", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkillerap1"), victim = s.Inmate("Zvictimap1"), witness = s.Inmate("Zwitnessap1");
                MurderTrial t = s.Case(killer, victim);
                s.Witness(t, witness, known: true);
                Trials.Assemble(s.g, map, t);
                ChoiceLetter_PrisonDecision letter = s.Asked("RP_Decision_TrialCase");
                if (letter == null || (letter.money & Trials.CaseApart) == 0 || !letter.decision.Worker.Options(letter).Contains("RP_TrialCase_Apart"))
                {
                    ApartFail($"trial letter: the assembled case with an inmate witness did not offer keeping them apart (letter {letter != null}, flags {letter?.money}, options {string.Join(", ", letter?.decision.Worker.Options(letter) ?? new List<string>())})");
                    return;
                }
                int option = LetterOption(letter, "RP_TrialCase_Apart");
                var worker = new PrisonDecisionWorker_TrialCase();
                if (worker.Disabled(letter, option) != null) ApartFail($"trial letter: keeping the witnesses apart was refused with a witness here: \"{worker.Disabled(letter, option)}\"");
                PrisonDecisions.Choose(letter, option);
                KeepApartOrder order = KeepApart.OrderBetween(witness, killer);
                if (order == null || order.reason != "trial") ApartFail($"trial letter: choosing it wrote {(order == null ? "no order" : "an order for " + order.reason)} between the witness and the accused");
                if (worker.Disabled(letter, option) != "RP_TrialNoApart".Translate().Resolve()) ApartFail($"trial letter: keeping the witnesses apart was offered again with every witness kept apart already, or refused for another reason (\"{worker.Disabled(letter, option)}\")");
                // A letter from before the option (its flags have no such bit) offers what it did.
                letter.money &= ~Trials.CaseApart;
                if (letter.decision.Worker.Options(letter).Contains("RP_TrialCase_Apart")) ApartFail("trial letter: a letter written before the option offers it");
            });
        }

        /// <summary>
        /// The green-light letter has "keep them apart from the gang's highest-ranked member" before "do nothing" (which the timeout still picks); choosing it writes an order for the gang with the old gang's known leader, by rank,
        /// with the leader nowhere near the ex-member (the letter is read hours after the renouncement: nobody of the gang can get at them then, and the enforcer's lookup finds nobody). A leader the warden does not know is not named.
        /// </summary>
        private static void CheckApartGangLetter(Map map)
        {
            Guarded("keep apart: the green-light letter offers it and writes it", map, r =>
            {
                var s = new GangScene(map, r);
                Pawn boss = s.Inmate(s.ravens, GangRank.Leader, known: false), mate = s.Inmate(s.ravens, known: true), leaver = s.Inmate(s.ravens, known: false);
                GangExit.Renounce(leaver, leaver.Inmate());
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_LeftGang") && l.pawns.Contains(leaver));
                if (letter == null)
                {
                    ApartFail("green-light letter: premise: no letter after the renouncement");
                    return;
                }
                IReadOnlyList<string> options = letter.decision.Worker.Options(letter);
                int option = options.ToList().IndexOf("RP_LeftGang_Apart");
                if (option < 0 || options[options.Count - 1] != "RP_LeftGang_Nothing" || letter.decision.Worker.TimeoutChoice != options.Count - 1)
                {
                    ApartFail($"green-light letter: the options are [{string.Join(", ", options)}] and the timeout picks {letter.decision.Worker.TimeoutChoice}; \"keep apart\" should be one of them and \"do nothing\" the last and the timeout's");
                    return;
                }
                // The leader is in another room, far off: nobody of the gang can get at the leaver now.
                bool Far(IntVec3 c) => c.Standable(map) && c.GetFirstPawn(map) == null && c.DistanceTo(leaver.Position) > 25f && c.GetRoom(map) != leaver.GetRoom();
                IntVec3? found = map.AllCells.Where(Far).Select(c => (IntVec3?)c).FirstOrDefault();
                if (found is not { } far)
                {
                    RPLog.Message("[RimPrison:test] keep apart: the green-light letter was not run, no cell far from the ex-member to put the leader in");
                    return;
                }
                if (!Far(far))
                {
                    ApartFail($"green-light letter: premise: the cell picked for the leader ({far}) is not a free, standable cell in another room more than 25 cells from the ex-member");
                    return;
                }
                s.Place(boss, far);
                s.Place(mate, far);
                if (GangExit.Enforcer(leaver, leaver.Inmate(), map) != null) ApartFail("green-light letter: premise: somebody of the old gang can get at the ex-member from the far cell");
                // The warden knows of one member only: the leader is not named, the member is.
                if (letter.decision.Worker.Disabled(letter, option) != null) ApartFail($"green-light letter: keeping them apart was refused with a known member of the old gang in the prison: \"{letter.decision.Worker.Disabled(letter, option)}\"");
                mate.Inmate().gangKnown = false;
                if (letter.decision.Worker.Disabled(letter, option) != "RP_LeftGang_NoBoss".Translate().ToString()) ApartFail("green-light letter: keeping them apart was offered with nobody of the old gang known to the warden, or refused for another reason");
                (boss.Inmate().gangKnown, mate.Inmate().gangKnown) = (true, true);
                if (letter.decision.Worker.Disabled(letter, option) is { } why) ApartFail($"green-light letter: keeping them apart was refused with the gang's leader known and in the prison: \"{why}\"");
                PrisonDecisions.Choose(letter, option);
                KeepApartOrder order = KeepApart.OrderBetween(leaver, boss);
                if (order == null || order.reason != "gang") ApartFail($"green-light letter: choosing it wrote {(order == null ? "no order" : "an order for " + order.reason)} between the ex-member and the gang's leader");
                if (KeepApart.Ordered(leaver, mate)) ApartFail("green-light letter: an order was written against a member, not the highest rank");
                if (letter.decision.Worker.Disabled(letter, option) != "RP_LeftGang_AlreadyApart".Translate(boss.LabelShort).ToString()) ApartFail($"green-light letter: keeping them apart was offered again once they were, or refused for another reason (\"{letter.decision.Worker.Disabled(letter, option)}\")");
            });
        }

        /// <summary>The debt letter has "keep them apart" before "let it run" (the timeout's); choosing it writes an order for the debt and closes the arc, and leaves the debt as it was.</summary>
        private static void CheckApartDebtLetter(Map map)
        {
            Guarded("keep apart: the debt letter offers it and writes it", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                List<StoryArc> arcs0 = g.story.arcs;
                int nextArc0 = g.story.nextArcId, nextDebt0 = g.story.nextDebtId;
                List<InmateDebt> debts0 = g.story.debts.ToList();
                Dictionary<StoryArcDef, int> lastStart0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
                r.Add(() =>
                {
                    g.story.arcs = arcs0;
                    g.story.lastArcStart = lastStart0;
                    (g.story.nextArcId, g.story.nextDebtId) = (nextArc0, nextDebt0);
                    g.story.debts.Clear();
                    g.story.debts.AddRange(debts0);
                });
                g.story.arcs = arcs0.Where(a => a.def != RPDebtDefOf.RP_Arc_Debt).ToList();
                Pawn creditor = Throwaway(map, r), debtor = Throwaway(map, r);
                InmateDebt debt = DebtSystem.Open(creditor, debtor, DebtReason.Drugs, 5, 5);
                int dueTick = debt.dueTick, nextTick = debt.nextTick;
                // (The arc as the beating starts it, without its letter: the answer is what is under test.)
                StoryArc arc = g.story.TryStartArc(RPDebtDefOf.RP_Arc_Debt, debtor, creditor);
                if (arc == null)
                {
                    RPLog.Message("[RimPrison:test] keep apart: the debt letter was not run, the story would not start a debt arc now");
                    return;
                }
                arc.count = debt.id;
                arc.note = DebtSystem.ReasonText(debt);
                var worker = (DebtArc)arc.def.Worker;
                IReadOnlyList<string> options = worker.Options(arc);
                int apartAt = options.ToList().IndexOf("RP_Debt_Apart"), runAt = options.ToList().IndexOf("RP_Debt_LetRun");
                if (apartAt < 0 || runAt != options.Count - 1 || worker.TimeoutChoice(arc) != runAt)
                {
                    ApartFail($"debt letter: the options are [{string.Join(", ", options)}] and the timeout picks {worker.TimeoutChoice(arc)}; \"keep them apart\" should be one of them and \"let it run\" the last and the timeout's");
                    return;
                }
                if (worker.Disabled(arc, apartAt) is { } why) ApartFail($"debt letter: keeping them apart was refused: \"{why}\"");
                worker.Choose(arc, map, apartAt);
                KeepApartOrder order = KeepApart.OrderBetween(debtor, creditor);
                if (order == null || order.reason != "debt") ApartFail($"debt letter: choosing it wrote {(order == null ? "no order" : "an order for " + order.reason)} between the debtor and the creditor");
                if (!arc.done) ApartFail("debt letter: the arc was not closed by the answer");
                if (debt.amount != 5 || debt.dueTick != dueTick || debt.nextTick != nextTick) ApartFail("debt letter: keeping them apart changed the debt or its schedule");
            });
        }

        /// <summary>A cell request answered yes, for a conflict (a cellmate they hate), writes an order once they are moved; one to join a friend does not.</summary>
        private static void CheckApartRequest(Map map)
        {
            Guarded("keep apart: a cell request granted writes an order", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "keep apart request", 2, 1);
                if (s == null) return;
                s.Grades(1, 1);
                Pawn a = s.Inmate(0, 0, 1), b = s.Inmate(0, 1, 1);
                PrisonRelations.MakeEnemies(a, b);
                if (CellRequests.Open())
                {
                    RPLog.Message("[RimPrison:test] keep apart: request not run, a cell request is already open in the prison");
                    return;
                }
                ChoiceLetter_PrisonDecision letter = CellRequests.TryRaise(a, a.Inmate(), only: b);
                if (letter == null)
                {
                    ApartFail($"request: premise: no letter for a cellmate they are feuding with ({ApartCells(s, a, b)}, active {InmateUtility.IsActiveInmate(a)})");
                    return;
                }
                if (KeepApart.Ordered(a, b)) ApartFail("request: an order was there before the request was answered");
                PrisonDecisions.Choose(letter, 0);
                if (s.CellOf(a) == s.CellOf(b)) ApartFail($"request: premise: granting the request moved nobody ({ApartCells(s, a, b)})");
                else if (KeepApart.OrderBetween(a, b) is not { reason: "request" }) ApartFail("request: the request was granted and no order was written for it");
            });
        }

        // ---- the book, and what does not read it ---------------------------------------------------------------------------------------

        /// <summary>The daily pass drops an order whose party has left; the list stops at its cap; no order for the same inmate twice, nor for a pair that has one; a save block with a default.</summary>
        private static void CheckApartBook(Map map)
        {
            Guarded("keep apart: the book's daily pass, its cap and its limits", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                KeepApartBook book = g.keepApart;
                // (Whatever the run's earlier checks and play left in the book is the book's own business: the count below is of this check's orders. The rollback puts the book back.)
                book.orders.Clear();
                book.Changed();
                Pawn a = Throwaway(map, r), b = Throwaway(map, r), c = Throwaway(map, r);
                if (KeepApart.Add(a, a, "warden", silent: true) != KeepApartResult.Invalid) ApartFail("book: an inmate was kept apart from themselves");
                if (KeepApart.Add(a, b, "warden", silent: true) != KeepApartResult.Added || KeepApart.Add(b, a, "feud", silent: true) != KeepApartResult.Exists) ApartFail("book: a second order for the same pair was written, or the first was not");
                // The daily pass: one of the pair leaves, and their order goes; an order of two who are here stays.
                if (KeepApart.Add(b, c, "debt", silent: true) != KeepApartResult.Added) ApartFail("book: premise: the second pair's order was not written");
                int before = book.orders.Count;
                a.Destroy(DestroyMode.Vanish);
                KeepApart.Daily(map);
                if (book.orders.Count != before - 1 || KeepApart.OrderBetween(b, c) == null || book.Find(a.thingIDNumber, b.thingIDNumber) != null)
                    ApartFail($"book: after a party left the daily pass kept {book.orders.Count} of {before} orders (the one with the gone inmate should be the only one dropped)");
                // Not spawned is not gone: c is carried (still one of the prison's prisoners, held by another inmate) and the pass keeps the order.
                Pawn carrier = Throwaway(map, r);
                if (carrier.carryTracker.TryStartCarry(c) && !c.Spawned)
                {
                    KeepApart.Daily(map);
                    if (KeepApart.OrderBetween(b, c) == null) ApartFail("book: the daily pass dropped an order whose party was only being carried");
                    carrier.carryTracker.TryDropCarriedThing(carrier.Position, ThingPlaceMode.Near, out _);
                }
                else RPLog.Message("[RimPrison:test] keep apart: the carried party was not run, the throwaway could not carry the other");
                // Leaving for good (released, transferred, escaped, dead: Story.Notify_Leaving) ends their orders at once, and no one else's.
                KeepApart.Notify_Leaving(c);
                if (KeepApart.OrderBetween(b, c) != null) ApartFail("book: an order of a party who left for good was not ended when they left");
                if (KeepApart.Add(b, c, "debt", silent: true) != KeepApartResult.Added) ApartFail("book: premise: the order was not written again");
                // The cap: filled with orders of nobody here, then one real one fits and the next does not.
                book.orders.RemoveAll(o => o.Has(b.thingIDNumber));
                book.Changed();
                for (int i = book.orders.Count; i < KeepApartBook.Cap - 1; i++) book.Add(new KeepApartOrder { idA = -10000 - i, idB = -20000 - i, nameA = "x", nameB = "y", reason = "warden" });
                Pawn d = Throwaway(map, r), e = Throwaway(map, r);
                if (KeepApart.Add(b, c, "warden", silent: true) != KeepApartResult.Added || book.orders.Count != KeepApartBook.Cap) ApartFail($"book: the last order under the cap was not written ({book.orders.Count} orders)");
                if (KeepApart.Add(d, e, "warden", silent: true) != KeepApartResult.Full || book.orders.Count != KeepApartBook.Cap) ApartFail($"book: an order past the cap of {KeepApartBook.Cap} was written ({book.orders.Count} orders)");
                // The pair test is a lookup both ways, whatever the order of the ids.
                if (!KeepApart.Ordered(c, b) || !KeepApart.Ordered(b, c) || KeepApart.Ordered(b, d)) ApartFail("book: the pair lookup is not both ways, or finds a pair that has no order");
            });
        }

        /// <summary>Of a pair kept apart, who has the seat of a class (and the job of a room) changes from one day to the next, so no one of them always waits; the class cohort drops the one who does not go first today.</summary>
        private static void CheckApartRotation(Map map)
        {
            Guarded("keep apart: who waits for the seat changes with the day", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                g.keepApart.orders.Clear();
                g.keepApart.Changed();
                Pawn a = Throwaway(map, r), b = Throwaway(map, r), c = Throwaway(map, r);
                if (KeepApart.Add(a, b, "warden", silent: true) != KeepApartResult.Added)
                {
                    ApartFail("rotation: premise: the order was not written");
                    return;
                }
                int aFirst = 0, bFirst = 0;
                for (int day = 0; day < 40; day++)
                {
                    bool ab = KeepApart.Precedes(a, b, day), ba = KeepApart.Precedes(b, a, day);
                    if (ab == ba) ApartFail($"rotation: on day {day} {a.LabelShort} and {b.LabelShort} both go first or neither does");
                    if (ab) aFirst++;
                    else bFirst++;
                }
                if (aFirst == 0 || bFirst == 0) ApartFail($"rotation: in 40 days one of the pair always went first ({aFirst} / {bFirst}): the other would always wait");
                // The cohort of a class: the one who goes first today keeps the seat, the unordered third is untouched, and the cohort keeps its own order.
                IReadOnlyList<Pawn> cohort = KeepApart.Cohort(new List<Pawn> { a, b, c });
                Pawn keeps = KeepApart.Precedes(a, b) ? a : b, waits = keeps == a ? b : a;
                if (cohort.Count != 2 || !cohort.Contains(keeps) || cohort.Contains(waits) || !cohort.Contains(c)) ApartFail($"rotation: the cohort of the pair and a third was [{string.Join(", ", cohort.Select(p => p.LabelShort))}], not the one who goes first today ({keeps.LabelShort}) and the third");
                IReadOnlyList<Pawn> unordered = new List<Pawn> { a, c };
                if (!ReferenceEquals(KeepApart.Cohort(unordered), unordered)) ApartFail("rotation: a cohort with no pair in it was copied");
            });
        }

        /// <summary>
        /// Two kept apart in two cells of one wing's paint (the wing's cells and its corridor are one patch) are no breach of a yard: no line says so at the hourly pass. The same cells painted as a yard are (the control).
        /// </summary>
        private static void CheckApartWingPaint(Map map)
        {
            Guarded("keep apart: two in the same wing's paint record no breach", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "keep apart wing paint", 2, 1, 1);
                if (s == null) return;
                s.Grades(1, 1);
                Pawn a = s.Inmate(0, 0, 1), b = s.Inmate(1, 0, 1);
                PrisonZoneDef wing = s.pm.zones.ZoneAt(s.Bed(0, 0).Position);
                if (wing == null || wing.keepable || s.CellOf(a) == s.CellOf(b))
                {
                    ApartFail($"wing paint: premise: the rig's beds are not in a wing's paint (zone {wing?.defName ?? "none"}, keepable {wing?.keepable}) or the pair shares a cell");
                    return;
                }
                // The paint between the two beds too: the wing's cells and corridor in one patch, as a wing is painted.
                IntVec3 from = s.Bed(0, 0).Position, to = s.Bed(1, 0).Position;
                foreach (IntVec3 cell in CellRect.FromLimits(from, to))
                {
                    IntVec3 at = cell;
                    PrisonZoneDef was = s.pm.zones.ZoneAt(at);
                    r.Add(() => s.pm.zones.Set(at, was));
                }
                void Paint(PrisonZoneDef zone)
                {
                    foreach (IntVec3 cell in CellRect.FromLimits(from, to)) s.pm.zones.Set(cell, zone);
                }
                Paint(wing);
                KeptPlace(a, from);
                KeptPlace(b, to);
                int patch = s.pm.zones.PatchAt(from);
                if (patch < 0 || patch != s.pm.zones.PatchAt(to))
                {
                    ApartFail($"wing paint: premise: the two beds are not in one patch of the wing's paint (patches {patch} and {s.pm.zones.PatchAt(to)})");
                    return;
                }
                if (KeepApart.Add(a, b, "feud", silent: true) != KeepApartResult.Added)
                {
                    ApartFail("wing paint: premise: the order was not written");
                    return;
                }
                KeepApartOrder order = KeepApart.OrderBetween(a, b);
                int now = Find.TickManager.TicksGame;
                KeepApart.Refresh(order, now);
                if (order.breach != null) ApartFail($"wing paint: two in one wing's paint (different cells) were recorded as a breach: \"{order.breach}\"");
                // The control: the same cells as a yard are one patch of a shared room, and are a breach.
                Paint(RPDefOf.RP_Yard);
                KeepApart.Refresh(order, now);
                if (order.breach == null || order.breachDay != GenDate.DaysPassed) ApartFail("wing paint: control: two in one patch of a yard were not recorded as a breach, so the check cannot tell the filter from a line that never comes");
            });
        }

        /// <summary>
        /// A reclassification into a level whose only free bed is in the cell of someone the warden keeps them apart from: no room is offered (<see cref="InmateUtility.HasRoomIn"/>), the change is refused with the reason, or - on
        /// paper - made with the reason, and the hourly picker finds no proper bed either; with the order lifted the same bed is theirs. Not run when another free bed for the level exists in the prison.
        /// </summary>
        private static void CheckApartOnlyPartnerBed(Map map)
        {
            Guarded("keep apart: the only free bed is in the partner's cell", map, r =>
            {
                CellRig s = CellRig.Build(map, r, "keep apart only partner bed", 1, 2);
                if (s == null) return;
                s.Grades(1, 1);
                SecurityLevelDef target = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(l => l != s.level && !l.wingOnly);
                if (target == null)
                {
                    RPLog.Message("[RimPrison:test] keep apart: the only-partner-bed check was not run, no other level without a wing of its own");
                    return;
                }
                Pawn a = s.Inmate(0, 0, 1), b = s.Inmate(1, 0, 1);
                // The partner's cell is in no wing: its free bed is a bed for any level (the rig's other cell is in a wing and full).
                foreach (IntVec3 cell in s.RoomOf(1).Cells)
                {
                    IntVec3 at = cell;
                    PrisonZoneDef was = s.pm.zones.ZoneAt(at);
                    r.Add(() => s.pm.zones.Set(at, was));
                    s.pm.zones.Set(at, null);
                }
                Building_Bed only = s.Bed(1, 1);
                CompInmate ac = a.Inmate();
                if (KeepApart.Add(a, b, "feud", silent: true) != KeepApartResult.Added)
                {
                    ApartFail("only partner bed: premise: the order was not written");
                    return;
                }
                List<Building_Bed> free = IntakeSystem.FreeBedsInOrder(map, target, a.gender);
                if (!free.Contains(only) || free.Exists(bed => bed != only))
                {
                    RPLog.Message($"[RimPrison:test] keep apart: the only-partner-bed check was not run, the free beds for {target.defName} are not just the partner's ({free.Count} of them)");
                    return;
                }
                SecurityLevelDef was0 = ac.security;
                Building_Bed own = a.ownership.OwnedBed;
                if (InmateUtility.HasRoomIn(a, target) || !InmateUtility.OnlyPartnerBeds(a, target)) ApartFail("only partner bed: a level whose only free bed is in the partner's cell was said to have room, or not to be short for that reason");
                int live = LiveMessages().Count;
                bool changed = InmateUtility.ChangeSecurity(a, ac, target);
                string refusal = "RP_BedOnlyWithPartner".Translate(a.LabelShort, target.label).ToString();
                if (changed || ac.security != was0 || a.ownership.OwnedBed != own) ApartFail($"only partner bed: the reclassification was made ({changed}) or the inmate moved though the only bed was the partner's");
                if (!LiveMessages().Skip(Math.Max(0, live - 5)).Any(m => m.text == refusal)) ApartFail("only partner bed: the refusal did not say that the only bed is in the cell of someone they are kept apart from");
                // On paper (a sentence that cannot wait): reclassified, told, and not moved into the cell.
                live = LiveMessages().Count;
                changed = InmateUtility.ChangeSecurity(a, ac, target, onPaper: true);
                string stays = "RP_BedOnlyWithPartnerStays".Translate(a.LabelShort, target.label).ToString();
                if (!changed || ac.security != target || a.ownership.OwnedBed != own) ApartFail($"only partner bed: on paper the change was {(changed ? "made" : "refused")} and they are {(a.ownership.OwnedBed == own ? "in their own bed" : "in another bed")}");
                if (!LiveMessages().Skip(Math.Max(0, live - 5)).Any(m => m.text == stays)) ApartFail("only partner bed: the change on paper did not say that they stay because the only bed is the partner's");
                // The hourly picker for the wrong wing finds no proper bed for them either (it would have tried the cell every hour), and the move itself, the last guard, refuses it.
                if (Cellmates.LastUsable(a, IntakeSystem.FreePrisonerBeds(map, target, a.gender)) != null) ApartFail("only partner bed: the picker of the hourly wrong-wing move offers the partner's bed");
                if (Cellmates.Move(a, ac, only) || a.ownership.OwnedBed != own) ApartFail("only partner bed: the move itself took the partner's bed");
                // The order lifted: the same bed is theirs.
                KeepApart.Lift(KeepApart.OrderBetween(a, b), silent: true);
                if (Cellmates.LastUsable(a, IntakeSystem.FreePrisonerBeds(map, target, a.gender)) != only || !InmateUtility.HasRoomIn(a, target)) ApartFail("only partner bed: control: with the order lifted the bed is not offered, so the check cannot tell the order from a bed that was never free");
            });
        }

        /// <summary>The compliance reading does not read the orders: an inmate in a yard patch with the one they are kept apart from is as much in place as with no order.</summary>
        private static void CheckApartCompliance(Map map)
        {
            Guarded("keep apart: the compliance reading does not read the order", map, r =>
            {
                KeptScene s = KeptScene.Begin(map, r, "keep apart compliance", RPDefOf.RP_Yard);
                if (s == null) return;
                Pawn p = KeptInmate(map, r, RPSecurityDefOf.RP_Medium, RPDefOf.RP_FreeTime), other = KeptInmate(map, r, RPSecurityDefOf.RP_Medium, RPDefOf.RP_FreeTime);
                List<IntVec3> cells = s.pm.zones.CellsOfPatch(s.Small).Where(c => c.Standable(map)).ToList();
                if (cells.Count < 2)
                {
                    RPLog.Message("[RimPrison:test] keep apart: compliance not run, the small yard patch has fewer than two standable cells");
                    return;
                }
                // The control first: a cell of the patch where the inmate is known to be in place with no order, the other beside them.
                IntVec3? spot = null;
                foreach (IntVec3 cell in cells)
                {
                    KeptPlace(p, cell);
                    if (!RegimePlacement.InExpectedPlace(p, RPDefOf.RP_FreeTime, s.pm)) continue;
                    spot = cell;
                    break;
                }
                if (spot is not { } home)
                {
                    ApartFail($"compliance: premise: the inmate is out of place at every one of the {cells.Count} standable cells of the small yard patch with no order, so the control cannot be set up ({KeptPawnText(s.pm, p)}; {s.Layout()})");
                    return;
                }
                KeptPlace(p, home);
                KeptPlace(other, cells.First(c => c != home));
                bool without = RegimePlacement.InExpectedPlace(p, RPDefOf.RP_FreeTime, s.pm);
                bool together = s.pm.zones.PatchAt(p.Position) == s.pm.zones.PatchAt(other.Position);
                if (!without || !together)
                {
                    ApartFail($"compliance: premise: before the order the control is {(without ? "in" : "out of")} place and the pair {(together ? "is" : "is not")} in one patch ({KeptPawnText(s.pm, p)}, {KeptPawnText(s.pm, other)})");
                    return;
                }
                if (KeepApart.Add(p, other, "feud", silent: true) != KeepApartResult.Added) { ApartFail("compliance: premise: the order was not written"); return; }
                bool with = RegimePlacement.InExpectedPlace(p, RPDefOf.RP_FreeTime, s.pm);
                if (!with) ApartFail($"compliance: in a yard patch with the one they are kept apart from the inmate counted out of place, and in place with no order: the order is advice to the placement, not a rule the grade reads ({KeptPawnText(s.pm, p)})");
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------

        /// <summary>Every order as one comparable string (see <see cref="KeepApart.Describe"/>).</summary>
        private static string KeepApartDescribe(PrisonGame g) => KeepApart.Describe(g);

        /// <summary>An order between two inmates, off its defaults: a reason a letter gives, a tick of its own and the names it kept.</summary>
        private static void SeedKeepApartForReload(Map map)
        {
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(KeepApart.Orderable).Take(3).ToList();
            if (inmates.Count < 3)
            {
                problems0.Add("saveload: fewer than three inmates to keep apart");
                return;
            }
            KeepApartBook book = PrisonGame.Instance.keepApart;
            int now = Find.TickManager.TicksGame;
            book.Add(new KeepApartOrder { idA = inmates[0].thingIDNumber, idB = inmates[1].thingIDNumber, nameA = inmates[0].LabelShort, nameB = inmates[1].LabelShort, reason = "debt", sinceTick = now - 4321 });
            book.Add(new KeepApartOrder { idA = inmates[2].thingIDNumber, idB = inmates[0].thingIDNumber, nameA = inmates[2].LabelShort, nameB = inmates[0].LabelShort, reason = "trial", sinceTick = now - 1234 });
        }
    }
}
