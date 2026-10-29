using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Release preparation and supervision (Story/ReleasePrep.cs, the Intake tab and the inmate file): Astra's headless proof. The release no longer rolls the reoffence at the gate (even with the roll forced to the old
    /// "reoffends", nothing is queued and nothing is counted: no preselected outcome); a case starts at the physical departure; two otherwise equal cases held at the same controlled review roll part ways because lodging was
    /// actually paid for in one and only promised in the other; each transition, bill, trust change, count and marshals' entry happens once; a payment never mends a new offence; a setback is reported, helped once at a price,
    /// and mended or not; an interview wins a job or not; the hazard of a whole period is the old single roll's chance (calibrated, not multiplied); the window of five days, the handlers' capacity and the three choices; the
    /// unresolved cohort is kept out of the stayed-out share; a parolee writes an ordinary letter before a later failure and says how they are now; an old (legacy) queued rearrest and an old release are as they were; the
    /// pawns the cases keep stay within the cap and are let go once. Self-contained, on throwaway inmates and staff, the rollback putting back the book, the funds, the counters and the register. The round trip, with a case
    /// in each state, is in the saveload plan (<see cref="SeedReleasePrepForReload"/>).
    /// </summary>
    public static partial class AutoTest
    {
        private static void RpFail(string check, string what) => problems0.Add($"release preparation ({check}): {what}");

        private static readonly string[] rpKeys =
        {
            "RP_Chr_HelpOffered", "RP_Chr_SetbackAddress", "RP_Chr_SetbackJob", "RP_Chr_SetbackMended", "RP_Chr_SupervisionDone", "RP_FilePrep", "RP_HelpAlready", "RP_HelpNoFunds", "RP_HelpNoSetback", "RP_HelpNote", "RP_HelpOffered",
            "RP_InterviewLost", "RP_InterviewWon", "RP_LodgingFunded", "RP_LodgingNote", "RP_LodgingUnfunded", "RP_ParoleLetter_Lodging", "RP_PrepBtnHelp", "RP_PrepBtnInterview", "RP_PrepBtnLodging", "RP_PrepBtnNone", "RP_PrepCapacityLine",
            "RP_PrepCasesHeader", "RP_PrepCohortLine", "RP_PrepHasJob", "RP_PrepHeader", "RP_PrepHeaderTip", "RP_PrepHearingDue", "RP_PrepHearingIn", "RP_PrepHelpTip", "RP_PrepHoused", "RP_PrepHousingFamily", "RP_PrepHousingHeld",
            "RP_PrepHousingLodged", "RP_PrepHousingNone", "RP_PrepJobHeld", "RP_PrepJobInterview", "RP_PrepJobNone", "RP_PrepJobTrade", "RP_PrepJobWaiting", "RP_PrepLater", "RP_PrepMore", "RP_PrepNoCapacity", "RP_PrepNoHandler",
            "RP_PrepNoQualification", "RP_PrepNoRows", "RP_PrepNotYet", "RP_PrepReleaseIn", "RP_PrepRowLine", "RP_PrepSupportInterview", "RP_PrepSupportLodging", "RP_PrepSupportNone", "RP_CaseAwaiting", "RP_CaseHelped", "RP_CaseNotHelped",
            "RP_CaseRearrested", "RP_CaseReviewing", "RP_CaseSetbackAddress", "RP_CaseSetbackJob", "RP_CaseStayedOut", "RP_CaseSupportDone", "RP_CaseSupportPromised", "RP_CaseSupportUnfunded", "RP_CaseVoided", "RP_SetbackAddressText",
            "RP_SetbackJobText", "RP_SetbackLabel", "RP_SetbackMended", "RP_SupervisionDone", "RP_PrepPromiseDropped",
        };

        private static void CheckReleasePrep(Map map)
        {
            // (What the checks must leave as it was: the live cases - every field, and whether their pawns are kept -, the board's trust and the clock.)
            PrisonGame g = PrisonGame.Instance;
            SupervisionBook live0 = g.supervision;
            // (Sentinels in the live book, so that the leak line has something to see when the live prison has no case yet - the events plan runs at day 0 to 3: one under review with a risk, its review due and its
            // pawn kept, and one waiting for the shuttle whose person is gone. A check that reviewed, began or voided a live case - one that forgot RpSetUp, say - changes them. This rollback takes them out again.)
            using var sentinels = new Rollback(map);
            Pawn sentinelPawn = Throwaway(map, sentinels, spawn: false);
            Find.WorldPawns.ForcefullyKeptPawns.Add(sentinelPawn);
            sentinels.Add(() => Find.WorldPawns.ForcefullyKeptPawns.Remove(sentinelPawn));
            SupervisionCase dueSentinel = RpCase(g, 0.5f, 7, false, false, false, sentinelPawn, add: true);
            (dueSentinel.name, dueSentinel.nextReviewTick) = ("Zrpsentinel", RpNow);
            SupervisionCase awaitingSentinel = RpCase(g, 0.5f, 7, false, false, false, add: true);
            (awaitingSentinel.name, awaitingSentinel.state, awaitingSentinel.departedTick, awaitingSentinel.nextReviewTick, awaitingSentinel.endTick) = ("Zrpsentinel2", CaseState.Awaiting, -1, -1, -1);
            string described0 = RpLiveState(g);
            float trust0 = g.story.boardTrust;
            int clock0 = RpNow;
            CheckRpTexts(map);
            CheckRpCalibration(map);
            CheckRpWindowAndHandlers(map);
            CheckRpProof(map);
            CheckRpPayment(map);
            CheckRpSetback(map);
            CheckRpInterview(map);
            CheckRpLegacy(map);
            CheckRpLetters(map);
            CheckRpLifecycle(map);
            CheckRpRegister(map);
            CheckRpIntakeLayout(map);
            string described1 = RpLiveState(g);
            if (g.supervision != live0 || described1 != described0) RpFail("leak", $"the checks changed the live supervision book: {described0} -> {described1}");
            if (!Mathf.Approximately(g.story.boardTrust, trust0)) RpFail("leak", $"the checks moved the board's trust: {trust0:F3} -> {g.story.boardTrust:F3}");
            if (RpNow != clock0) RpFail("leak", $"the checks left the clock at {RpNow}, not {clock0}");
            if (ReleasePrep.CapacityForChecks >= 0) RpFail("leak", "the checks left the handlers' capacity set for checks");
        }

        // ---- helpers ------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static int RpNow => Find.TickManager.TicksGame;

        /// <summary>The live book as the checks must leave it: every case's fields and whether its pawn is still kept in the world.</summary>
        private static string RpLiveState(PrisonGame g) =>
            g.supervision.Describe() + "|kept=" + string.Join(",", g.supervision.cases.Where(x => x.pawn != null).Select(x => $"{x.id}:{Find.WorldPawns.ForcefullyKeptPawns.Contains(x.pawn)}"));

        /// <summary>The day the checks' clock is moved on to (see <see cref="RpSetUp"/>): past the longest time a throwaway is said to have served (38 days of a 40-day sentence).</summary>
        private const int RpClockDay = 60;

        /// <summary>
        /// The first thing every release preparation check does. The clock moves on to day <see cref="RpClockDay"/> (the events plan runs at day 0 to 3: a throwaway "38 days into a 40-day sentence" would have been admitted before
        /// the game began, and a negative admission tick reads as nothing served - 40 days from release, in nobody's window). The board's trust is put back at the end (a rearrest's news and a stay-out move it). The check works
        /// on a supervision book of its own: no live case is reviewed, capped or let go of by a check, and the live book - the same object, the same cases - is put back. The handlers' capacity for checks is cleared at the end.
        /// The undo runs last to first, so these four run after the check's own.
        /// </summary>
        private static void RpSetUp(Rollback r)
        {
            PrisonGame g = PrisonGame.Instance;
            int clock0 = RpNow;
            float trust0 = g.story.boardTrust;
            SupervisionBook live = g.supervision;
            r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
            r.Add(() => g.story.boardTrust = trust0);
            r.Add(() => g.supervision = live);
            r.Add(() => ReleasePrep.CapacityForChecks = -1);
            Find.TickManager.DebugSetTicksGame(Math.Max(clock0, RpClockDay * GenDate.TicksPerDay));
            g.supervision = new SupervisionBook { nextId = live.nextId };
        }

        /// <summary>The marshals' hourly pass over the returns with only this person's on the list (a real return that falls due meanwhile - the clock is moved on - is not the check's to hand over).</summary>
        private static void RpReturnsOf(PrisonGame g, Map map, Pawn only)
        {
            List<Rearrest> others = g.returning.Where(x => x.pawn != only).ToList();
            g.returning.RemoveAll(x => x.pawn != only);
            try { Reoffenders.Hourly(g, map); }
            finally { g.returning.AddRange(others); }
        }

        /// <summary>A made-up inmate <paramref name="daysLeft"/> days from the end of a 40-day sentence, with no home, no job, no certificate and nothing arranged.</summary>
        private static Pawn RpInmate(Map map, Rollback r, float daysLeft, bool family = false, bool recentHearing = false)
        {
            PrisonGame g = PrisonGame.Instance;
            Pawn p = Throwaway(map, r);
            CompInmate c = p.Inmate();
            c.RefreshActiveFlag();
            (c.processed, c.awaitingSearch) = (true, false);
            (c.sentenceDays, c.daysAdded, c.admittedTick, c.lastHearingTick) = (40, 0, RpNow - Mathf.RoundToInt((40f - daysLeft) * GenDate.TicksPerDay), recentHearing ? RpNow : -999999);
            (c.innocent, c.jobWaiting, c.familyCount, c.releasePlan) = (false, false, family ? 2 : 0, ReleaseSupport.None);
            c.programsPassed = 0;
            c.hoursWorked = 0;
            c.relatives = new List<Relative>();
            c.reputations.Clear();
            c.passedPrograms.Clear();
            c.security = InmateUtility.DefaultSecurity;
            r.Add(() =>
            {
                g.shuttle.Forget(p);
                Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
            });
            return p;
        }

        /// <summary>The handlers' capacity the check tests: room for exactly this many more cases on top of the load now (whatever administrators and counsellors the live prison has). <see cref="RpSetUp"/> clears it.</summary>
        private static void RpRoomFor(Map map, int cases) => ReleasePrep.CapacityForChecks = ReleasePrep.Load(PrisonGame.Instance, map) + cases;

        /// <summary>A case as the book would hold it under review, with no pawn: for the hazard and the reviews.</summary>
        private static SupervisionCase RpCase(PrisonGame g, float risk, int days, bool paroled, bool job, bool address, Pawn pawn = null, bool add = false)
        {
            int now = RpNow;
            var sc = new SupervisionCase
            {
                version = Supervision.CurrentVersion, id = g.supervision.nextId++, name = "Zrp" + g.supervision.nextId, pawn = pawn, pawnId = pawn?.thingIDNumber ?? -1, paroled = paroled, state = CaseState.Reviewing, releasedTick = now, departedTick = now,
                nextReviewTick = now + GenDate.TicksPerDay, endTick = now + days * GenDate.TicksPerDay, reviewDays = days, baseRisk = risk, jobSecured = job, addressSecured = address,
            };
            sc.baseWeight = Supervision.TotalWeight(sc);
            if (add) g.supervision.cases.Add(sc);
            return sc;
        }

        /// <summary>The roll that lands the cause at this place (0 to 1) in the order of <see cref="Supervision.Causes"/> for the case now: a roll under the hazard.</summary>
        private static float RpRollFor(SupervisionCase sc, float place) => Supervision.Hazard(sc) * Mathf.Clamp(place, 0.001f, 0.999f);

        /// <summary>The way out: they step onto the shuttle (counted and the review begun by the game's own call), and the pawn is in the world as the shuttle leaves it.</summary>
        private static void RpDepart(Pawn p, Rollback r)
        {
            ShuttleSystem.Notify_Left(p);
            if (p.Spawned) p.DeSpawn();
            if (!Find.WorldPawns.Contains(p)) Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
            r.Add(() =>
            {
                if (Find.WorldPawns.Contains(p) && !p.Destroyed) Find.WorldPawns.RemovePawn(p);
                Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
            });
        }

        /// <summary>The marshals' pass over the rearrests with only this one on the list (a real rearrest that falls due in the live prison meanwhile is not the check's to process).</summary>
        private static void RpCheckRearrestsOf(PrisonGame g, Rearrest only)
        {
            List<Rearrest> others = g.rearrests.Where(x => x != only).ToList();
            g.rearrests.RemoveAll(x => x != only);
            try { ReleaseSystem.CheckRearrests(g); }
            finally { g.rearrests.AddRange(others); }
        }

        private static int RpBills(Rollback r) => r.NewLedgerEntries.Count(e => e.category == RPReleasePrepDefOf.RP_Resettlement);

        // ---- the definition and the texts --------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRpTexts(Map map)
        {
            Guarded("release preparation: the definition and every text", map, r =>
            {
                RpSetUp(r);
                PrisonGame g = PrisonGame.Instance;
                if (RPReleasePrepDefOf.RP_Resettlement == null || RPReleasePrepDefOf.RP_Resettlement.income) RpFail("definition", "the ledger category for resettlement support is missing or is income");
                foreach (string key in rpKeys)
                    if (!key.CanTranslate()) RpFail("texts", $"no keyed string {key}");
                if (Supervision.DayHazard(0.5f, 1) < 0.499f || Supervision.ReviewDaysServed < 1 || Supervision.SetbackReviews < 2) RpFail("definition", "the stated numbers do not allow a setback to be seen before it is reviewed");
                // The file's row, and the case's lines in each state, read with nothing left unfilled.
                RpRoomFor(map, 1);
                Pawn p = RpInmate(map, r, 3f);
                string row = ReleasePrep.FileLine(g, map, p, p.Inmate()) is { } line ? line.text : null;
                if (row == null || row.Contains("{") || row.Contains("RP_") || row.Contains("Translation error")) RpFail("texts", $"the file's release-planning row for somebody three days from release reads: {row ?? "(no row)"}");
                SupervisionCase sc = RpCase(g, 0.3f, 7, true, true, false);
                foreach (CaseState state in Enum.GetValues(typeof(CaseState)))
                    foreach (SetbackState setback in new[] { SetbackState.None, SetbackState.Pending })
                    {
                        (sc.state, sc.setbackState, sc.setback, sc.setbackAt, sc.outcome, sc.reason, sc.support, sc.supportDone) = (state, setback, CaseSetback.AddressWithdrawn, 0, state == CaseState.Resolved ? CaseOutcome.Rearrested : CaseOutcome.None, "RP_Violation_JobLost", ReleaseSupport.Lodging, true);
                        string text = ReleasePrep.CaseLine(sc);
                        if (text.Contains("{") || text.Contains("RP_") || text.Contains("Translation error")) RpFail("texts", $"the line for a case that is {state} with a setback {setback} reads: {text}");
                    }
                foreach (CaseOutcome outcome in Enum.GetValues(typeof(CaseOutcome)))
                {
                    (sc.state, sc.outcome) = (CaseState.Resolved, outcome);
                    string text = ReleasePrep.CaseLine(sc);
                    if (text.Contains("{") || text.Contains("RP_") || text.Contains("Translation error")) RpFail("texts", $"the line for a case that ended {outcome} reads: {text}");
                }
            });
        }

        // ---- calibration: the whole period is the old single roll ---------------------------------------------------------------------------------------------------------------

        private static void CheckRpCalibration(Map map)
        {
            Guarded("release preparation: the reviews together are the old single roll", map, r =>
            {
                RpSetUp(r);
                PrisonGame g = PrisonGame.Instance;
                // The arithmetic: the day's hazard compounds to exactly the old chance, for every length and every chance the old roll can have (5% to 90%).
                foreach (float risk in new[] { 0.05f, 0.2f, 0.55f, 0.9f })
                    foreach (int days in new[] { 1, 5, 7, 14 })
                    {
                        float whole = 1f - Mathf.Pow(1f - Supervision.DayHazard(risk, days), days);
                        if (Mathf.Abs(whole - risk) > 0.0005f) RpFail("calibration", $"{days} reviews at a hazard of {Supervision.DayHazard(risk, days):F4} a day make {whole:F4}, not the old chance {risk:F2}");
                    }
                // A case with nothing done for it is exactly the day's share, whatever it holds.
                foreach (bool job in new[] { false, true })
                    foreach (bool address in new[] { false, true })
                        foreach (bool paroled in new[] { false, true })
                        {
                            SupervisionCase sc = RpCase(g, 0.4f, 7, paroled, job, address);
                            if (Mathf.Abs(Supervision.Hazard(sc) - Supervision.DayHazard(0.4f, 7)) > 0.00001f)
                                RpFail("calibration", $"a case with a job {job}, an address {address}, paroled {paroled} and nothing done has a hazard of {Supervision.Hazard(sc):F5}, not the day's share {Supervision.DayHazard(0.4f, 7):F5}");
                        }
                // The whole process, run through (setbacks left unhelped, which end in the rearrest): the share rearrested is the old chance, not several times it.
                Rand.PushState(20261028);
                var bad = new List<string>();
                try
                {
                    foreach (float risk in new[] { 0.15f, 0.55f })
                        foreach ((bool job, bool address, bool paroled) in new[] { (false, false, true), (true, true, true), (true, false, false), (false, true, true) })
                        {
                            const int n = 3000;
                            int rearrested = 0;
                            for (int i = 0; i < n; i++)
                            {
                                SupervisionCase sc = RpCase(g, risk, 7, paroled, job, address);
                                for (int step = 0; step < 40; step++)
                                {
                                    ReviewStep s = Supervision.Decide(sc, Rand.Value, out _);
                                    if (s == ReviewStep.Breach)
                                    {
                                        rearrested++;
                                        break;
                                    }
                                    if (s == ReviewStep.StayedOut) break;
                                }
                            }
                            if (Mathf.Abs(rearrested / (float)n - risk) > 0.04f) bad.Add($"{risk:F2} with job {job} address {address} paroled {paroled}: {rearrested / (float)n:F3}");
                        }
                }
                finally { Rand.PopState(); }
                if (bad.Count > 0) RpFail("calibration", $"over many cases the share rearrested is not the old chance: {string.Join("; ", bad)}");
                // Somebody innocent is never rearrested, as before.
                SupervisionCase clean = RpCase(g, 0f, 7, true, false, false);
                ReviewStep last = ReviewStep.None;
                for (int i = 0; i < 7; i++) last = Supervision.Decide(clean, 0.0001f, out _);
                if (last != ReviewStep.StayedOut) RpFail("calibration", $"a case with no risk was {last} at the end of its review");
            });
        }

        // ---- the window, the handlers and the three choices --------------------------------------------------------------------------------------------------------------------

        private static void CheckRpWindowAndHandlers(Map map)
        {
            Guarded("release preparation: the window of five days and the handlers' capacity", map, r =>
            {
                RpSetUp(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn near = RpInmate(map, r, 2f, recentHearing: true), far = RpInmate(map, r, 30f), hearing = RpInmate(map, r, 23f), lifer = RpInmate(map, r, 2f), housed = RpInmate(map, r, 3f, family: true, recentHearing: true);
                // (Sentence 40 days: 23 days left is 17 served, three days to half of it, a hearing.)
                lifer.Inmate().sentenceDays = -1;
                List<ReleasePrep.PlanRow> rows = ReleasePrep.Rows(map);
                bool Has(Pawn p) => rows.Any(x => x.pawn == p);
                if (!Has(near) || !Has(hearing) || !Has(housed) || Has(far) || Has(lifer))
                    RpFail("window", $"the list holds the one two days from release {Has(near)}, the one three days from a hearing {Has(hearing)}, one with family {Has(housed)}, and not the one 30 days away {Has(far)} or a lifer {Has(lifer)}");
                else if (rows.First(x => x.pawn == hearing).hearing != true || rows.First(x => x.pawn == near).hearing) RpFail("window", "the row does not say whether it is a release or a hearing");
                int nearIndex = rows.FindIndex(x => x.pawn == near), housedIndex = rows.FindIndex(x => x.pawn == housed);
                if (nearIndex > housedIndex) RpFail("window", "the list is not soonest first");
                string notYet = ReleasePrep.Choose(g, map, far, ReleaseSupport.Lodging);
                if (notYet != "RP_PrepNotYet" || far.Inmate().releasePlan != ReleaseSupport.None) RpFail("window", $"somebody 30 days from release was given a promise ({notYet})");
                if (ReleasePrep.Choose(g, map, housed, ReleaseSupport.Lodging) != "RP_PrepHoused") RpFail("choices", "lodging was offered to somebody with a family to go home to");
                if (ReleasePrep.Choose(g, map, near, ReleaseSupport.Interview) != "RP_PrepNoQualification") RpFail("choices", "an interview was offered to somebody with no certificate from the vocational courses");
                // On the way out they leave the list.
                Pawn leaving = RpInmate(map, r, 1f);
                leaving.Inmate().innocent = true;
                ReleaseSystem.Release(g, leaving, record: false);
                if (ReleasePrep.Rows(map).Any(x => x.pawn == leaving)) RpFail("window", "somebody already on the way out is still on the list");

                // The handlers: three cases each, administrators and counsellors alike; a case that is promised or under review takes a place, one that is settled gives it back.
                int capacity0 = ReleasePrep.Capacity(map), handlers0 = ReleasePrep.Handlers(map);
                ThrowawayStaff(map, r, RPParoleDefOf.RP_Administrator);
                int capacity1 = ReleasePrep.Capacity(map);
                ThrowawayStaff(map, r, RPProgramDefOf.RP_Counsellor);
                int capacity2 = ReleasePrep.Capacity(map);
                if (capacity1 != capacity0 + ReleasePrep.CasesPerHandler || capacity2 != capacity1 + ReleasePrep.CasesPerHandler || ReleasePrep.Handlers(map) != handlers0 + 2)
                    RpFail("capacity", $"an administrator and a counsellor took the capacity from {capacity0} to {capacity1} and {capacity2} ({ReleasePrep.CasesPerHandler} each expected, {handlers0} handlers before)");
                // (From here the capacity is the check's own: four places on top of the load now. The live prison's administrators and counsellors would make it whatever they make it.)
                RpRoomFor(map, 4);
                var fillers = new List<SupervisionCase>();
                int toFill = ReleasePrep.Capacity(map) - ReleasePrep.Load(g, map);
                for (int i = 0; i < toFill; i++)
                {
                    SupervisionCase filler = RpCase(g, 0.3f, 7, true, false, false, add: true);
                    filler.support = ReleaseSupport.Lodging;
                    fillers.Add(filler);
                }
                string full = ReleasePrep.Choose(g, map, near, ReleaseSupport.Lodging);
                if (toFill != 4 || ReleasePrep.Load(g, map) != ReleasePrep.Capacity(map) || full != "RP_PrepNoCapacity" || near.Inmate().releasePlan != ReleaseSupport.None)
                    RpFail("capacity", $"with every place taken ({ReleasePrep.Load(g, map)} of {ReleasePrep.Capacity(map)}) a promise was {(full ?? "accepted")}, not refused for want of a place");
                // A case under review that is not the warden's takes no place; help for a setback takes one (and is refused when there is none).
                SupervisionCase unhandled = RpCase(g, 0.3f, 7, true, true, true, add: true);
                (unhandled.setback, unhandled.setbackState, unhandled.setbackAt) = (CaseSetback.JobLost, SetbackState.Pending, 0);
                g.ledger.funds = Mathf.Max(g.ledger.funds, 5000);
                if (Supervision.HelpRefusal(g, map, unhandled) != "RP_PrepNoCapacity") RpFail("capacity", $"help for a setback when every place is taken was {Supervision.HelpRefusal(g, map, unhandled) ?? "allowed"}");
                fillers[0].state = CaseState.Resolved;
                if (ReleasePrep.Choose(g, map, near, ReleaseSupport.Lodging) != null || near.Inmate().releasePlan != ReleaseSupport.Lodging)
                    RpFail("capacity", "a place freed by a settled case was not given to the next promise");
                if (ReleasePrep.Load(g, map) != ReleasePrep.Capacity(map)) RpFail("capacity", $"the load is {ReleasePrep.Load(g, map)} of {ReleasePrep.Capacity(map)} after a settled case gave its place to a promise");
                // Changing their mind gives the place back; the same choice twice is nothing.
                if (ReleasePrep.Choose(g, map, near, ReleaseSupport.None) != null || ReleasePrep.Load(g, map) != ReleasePrep.Capacity(map) - 1) RpFail("capacity", "a promise taken back did not give its place back");
                if (Supervision.HelpRefusal(g, map, unhandled) != null) RpFail("capacity", $"help for a setback was {Supervision.HelpRefusal(g, map, unhandled)} with a place free");
            });
        }

        // ---- Astra's proof: two equal cases, one controlled roll ---------------------------------------------------------------------------------------------------------------

        private static void CheckRpProof(Map map)
        {
            Guarded("release preparation: funded lodging against an unfunded promise, at one controlled roll", map, r =>
            {
                RpSetUp(r);
                const string check = "proof";
                PrisonGame g = PrisonGame.Instance;
                RpRoomFor(map, 4);
                g.ledger.funds = 20000;
                Pawn a = RpInmate(map, r, 2f), b = RpInmate(map, r, 2f);
                if (ReleasePrep.Choose(g, map, a, ReleaseSupport.Lodging) is { } refusedA || ReleasePrep.Choose(g, map, b, ReleaseSupport.Lodging) is { } refusedB)
                {
                    RpFail(check, "the lodging could not be promised to two inmates two days from release (premise)");
                    return;
                }
                float trust0 = g.story.boardTrust;
                int rearrests0 = g.rearrests.Count, reoffended0 = g.stats.reoffended, released0 = g.stats.released, lodged0 = g.stats.supervisionLodged, started0 = g.stats.supervisionStarted;
                // Save before departure: released with the old roll forced to "reoffends". Nothing is rolled at the gate any more: nothing queued, nothing counted, no trust, and the register's news is due for both.
                foreach (Pawn p in new[] { a, b })
                {
                    Rand.PushState(ParoleSeedFor(ParoleSystem.ReoffendChance(p.Inmate()), true));
                    try { ReleaseSystem.Release(g, p, paroled: true, recommended: true, record: false); }
                    finally { Rand.PopState(); }
                }
                OutboundEntry ea = g.shuttle.OutboundOf(a), eb = g.shuttle.OutboundOf(b);
                SupervisionCase ca = ea == null ? null : g.supervision.Find(ea.caseId), cb = eb == null ? null : g.supervision.Find(eb.caseId);
                if (ca == null || cb == null)
                {
                    RpFail(check, $"a release did not open a case (entries {ea != null}/{eb != null}, cases {ca != null}/{cb != null})");
                    return;
                }
                FormerInmate fa = g.story.formerInmates.Find(x => x.supervisionId == ca.id), fb = g.story.formerInmates.Find(x => x.supervisionId == cb.id);
                bool open = g.rearrests.Count == rearrests0 && g.stats.reoffended == reoffended0 && g.stats.released == released0 && Mathf.Approximately(g.story.boardTrust, trust0) && !ea.reoffends && !eb.reoffends
                            && ca.state == CaseState.Awaiting && cb.state == CaseState.Awaiting && ca.outcome == CaseOutcome.None && fa is { newsTick: >= 0 } && fb is { newsTick: >= 0 }
                            && g.stats.supervisionStarted == started0 && RpBills(r) == 0;
                if (!open)
                    RpFail(check, $"after the release with the old roll forced to yes: rearrests +{g.rearrests.Count - rearrests0}, reoffended +{g.stats.reoffended - reoffended0}, released +{g.stats.released - released0}, trust {trust0:F2}->{g.story.boardTrust:F2}, flags {ea.reoffends}/{eb.reoffends}, states {ca.state}/{cb.state}, news due {fa?.newsTick}/{fb?.newsTick}, a bill {RpBills(r)} (nothing was to be decided or paid before they left)");
                if (ca.support != ReleaseSupport.Lodging || cb.support != ReleaseSupport.Lodging || ca.addressSecured || cb.addressSecured) RpFail(check, "the promise did not reach the case, or an address was known before the lodging was paid");
                // Departure: A's lodging is paid for (the funds are there), B's is only promised (the funds are not).
                RpDepart(a, r);
                // (Told again: the way out is taken once, the lodging paid once.)
                g.ledger.funds = 20000;
                ShuttleSystem.Notify_Left(a);
                Supervision.Begin(g, ca);
                int billedA = RpBills(r);
                g.ledger.funds = 0;
                RpDepart(b, r);
                g.ledger.funds = 20000;
                Supervision.Begin(g, cb);
                bool departed = ca.state == CaseState.Reviewing && cb.state == CaseState.Reviewing && ca.departedTick > 0 && ca.nextReviewTick == ca.departedTick + GenDate.TicksPerDay && g.stats.supervisionStarted == started0 + 2
                                && g.stats.released == released0 + 2 && g.stats.reoffended == reoffended0;
                bool lodgingA = ca.supportFulfilled && ca.addressSecured && ca.lodgingPaid == Supervision.LodgingPrice && billedA == 1 && g.stats.supervisionLodged == lodged0 + 1 && fa.addressSecured && fa.lodged;
                bool lodgingB = cb.supportDone && !cb.supportFulfilled && !cb.addressSecured && cb.lodgingPaid == 0 && RpBills(r) == 1 && !fb.addressSecured && !fb.lodged;
                if (!departed || !lodgingA || !lodgingB)
                    RpFail(check, $"at the departure: both reviewing {departed} (started +{g.stats.supervisionStarted - started0}, released +{g.stats.released - released0}), A's lodging paid once {lodgingA} (bills {billedA}, paid {ca.lodgingPaid}, address {ca.addressSecured}, register {fa.addressSecured}/{fa.lodged}), B's promise not funded and nothing paid {lodgingB} (bills {RpBills(r)}, address {cb.addressSecured})");
                // The two cases are otherwise equal (the same risk and length), and held at the same roll: one that is under the unfunded case's hazard and over the funded one's.
                (ca.baseRisk, cb.baseRisk, ca.reviewDays, cb.reviewDays) = (0.5f, 0.5f, 7, 7);
                float hA = Supervision.Hazard(ca), hB = Supervision.Hazard(cb), roll = (hA + hB) / 2f;
                if (!(hA < hB)) RpFail(check, $"the funded lodging did not lower the hazard ({hA:F4} against the unfunded promise's {hB:F4})");
                int rearrestsBefore = g.rearrests.Count, reoffendedBefore = g.stats.reoffended, rearrestedBefore = g.stats.supervisionRearrested;
                Supervision.Review(g, ca, roll);
                Supervision.Review(g, cb, roll);
                bool fundedHolds = ca.state == CaseState.Reviewing && ca.outcome == CaseOutcome.None && ca.reviewsRolled == 1;
                bool unfundedBreaks = cb.state == CaseState.Resolved && cb.outcome == CaseOutcome.Rearrested && cb.rearrestQueued && cb.reason != null && g.rearrests.Count == rearrestsBefore + 1 && g.stats.reoffended == reoffendedBefore + 1 && g.stats.supervisionRearrested == rearrestedBefore + 1;
                if (!fundedHolds || !unfundedBreaks)
                    RpFail(check, $"at the same roll {roll:F4} (hazards {hA:F4} funded, {hB:F4} unfunded): the funded case holds {fundedHolds} ({ca.state}, {ca.outcome}), the unfunded breaks once {unfundedBreaks} ({cb.state}, {cb.outcome}, rearrests +{g.rearrests.Count - rearrestsBefore}, reoffended +{g.stats.reoffended - reoffendedBefore})");
                // Once: asked again, nothing happens twice.
                for (int i = 0; i < 3; i++) Supervision.Review(g, cb, roll);
                Supervision.Finish(g, cb, CaseOutcome.Rearrested);
                if (g.rearrests.Count != rearrestsBefore + 1 || g.stats.reoffended != reoffendedBefore + 1 || g.rearrests.Count(x => x.pawn == b) != 1) RpFail(check, $"a settled case was settled again: rearrests +{g.rearrests.Count - rearrestsBefore}, reoffended +{g.stats.reoffended - reoffendedBefore}");
                // The rearrest goes on as the marshals' own queue: the trust and the reputation cost and the clawback once, one entry for the return, and one marshal.
                Rearrest queued = g.rearrests.Last();
                float trustBefore = g.story.boardTrust, repBefore = g.reputation;
                int fundsAtNews = g.ledger.funds, violations0 = g.stats.paroleViolations;
                Rand.PushState(ParoleSeedFor(0.5f, true));
                try { RpCheckRearrestsOf(g, queued); }
                finally { Rand.PopState(); }
                RpCheckRearrestsOf(g, queued);
                bool news = queued.pawn == b && !g.rearrests.Contains(queued) && g.ledger.funds == fundsAtNews - ParoleSystem.ParoleBonus && Mathf.Approximately(g.story.boardTrust, Mathf.Max(ParoleHearings.TrustMin, trustBefore - 0.15f))
                            && g.stats.paroleViolations == violations0 + (queued.reason == ParoleLife.NewOffence ? 0 : 1) && g.returning.Count(x => x.pawn == b) == 1 && Mathf.Approximately(g.reputation, Mathf.Max(0f, Mathf.Max(0f, repBefore - 1f) - 1f));
                if (!news) RpFail(check, $"the rearrest's news: queued for {queued.pawn?.LabelShort}, funds {fundsAtNews}->{g.ledger.funds} (clawback {ParoleSystem.ParoleBonus} once), trust {trustBefore:F2}->{g.story.boardTrust:F2} (-0.15 once), reputation {repBefore:F1}->{g.reputation:F1} (-1 for the recommendation, -1 for the parolee), violations +{g.stats.paroleViolations - violations0}, on the way back {g.returning.Count(x => x.pawn == b)}");
                if (IntakeSystem.FreeBedCount(map, b.Inmate().security, gender: b.gender) > 0)
                {
                    queued.dueTick = RpNow - 1;
                    RpReturnsOf(g, map, b);
                    RpReturnsOf(g, map, b);
                    r.Add(() => g.shuttle.TakeMarshal(b));
                    int marshals = g.shuttle.marshals.Count(x => x.pawn == b);
                    if (marshals != 1 || g.returning.Any(x => x.pawn == b)) RpFail(check, $"the marshals have {marshals} entries for the rearrested one after two passes (once expected), still on the returning list {g.returning.Any(x => x.pawn == b)}");
                }
                else RPLog.Message("[RimPrison:test] release preparation proof: no free bed of their level, the marshals' entry was not looked at");
                // The funded case goes on to the end: stays out, the trust of a recommended parolee comes once, at the outcome.
                float trustAtEnd = g.story.boardTrust;
                int stayed0 = g.stats.supervisionStayedOut;
                for (int i = 0; i < 12; i++) Supervision.Review(g, ca, 0.9999f);
                bool stayed = ca.state == CaseState.Resolved && ca.outcome == CaseOutcome.StayedOut && ca.reviewsRolled == 7 && g.stats.supervisionStayedOut == stayed0 + 1 && g.stats.reoffended == reoffendedBefore + 1
                              && Mathf.Approximately(g.story.boardTrust, Mathf.Min(ParoleHearings.TrustMax, trustAtEnd + 0.05f)) && !g.rearrests.Any(x => x.pawn == a) && (ExInmates.EntryOf(a) != null || !Find.WorldPawns.ForcefullyKeptPawns.Contains(a));
                if (!stayed) RpFail(check, $"the funded case at the end of its review: {ca.state} {ca.outcome} after {ca.reviewsRolled} rolls, stayed out +{g.stats.supervisionStayedOut - stayed0}, trust {trustAtEnd:F2}->{g.story.boardTrust:F2} (+0.05 once), still kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(a)}");
            });
        }

        /// <summary>Payment never touches a new offence: a funded case whose roll lands on one is rearrested, and help is for a setback only.</summary>
        private static void CheckRpPayment(Map map)
        {
            Guarded("release preparation: payment cannot erase a new offence", map, r =>
            {
                RpSetUp(r);
                const string check = "payment";
                PrisonGame g = PrisonGame.Instance;
                g.ledger.funds = 20000;
                SupervisionCase sc = RpCase(g, 0.6f, 7, true, true, true, add: true);
                sc.support = ReleaseSupport.Lodging;
                (sc.supportDone, sc.supportFulfilled, sc.lodgingPaid, sc.helped, sc.helpPaid) = (true, true, Supervision.LodgingPrice, true, Supervision.HelpPrice);
                // The first cause is the new offence: a roll that lands there is a breach, with everything paid.
                string refusal = Supervision.HelpRefusal(g, map, sc);
                if (refusal != "RP_HelpNoSetback") RpFail(check, $"help was {refusal ?? "allowed"} for a case with no setback (nothing is to be paid for a new offence)");
                ReviewStep step = Supervision.Decide(sc, RpRollFor(sc, 0.05f), out string reason);
                if (step != ReviewStep.Breach || reason != ParoleLife.NewOffence) RpFail(check, $"a roll that lands on the new offence, with the lodging paid and help given, was {step} for {reason}");
            });
        }

        // ---- a setback and the bounded help ---------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRpSetback(Map map)
        {
            Guarded("release preparation: a setback is reported, helped once, mended or not", map, r =>
            {
                RpSetUp(r);
                const string check = "setback";
                PrisonGame g = PrisonGame.Instance;
                RpRoomFor(map, 3);
                g.ledger.funds = 20000;
                var entry = new FormerInmate { name = "Zrpsetback", number = 99981, paroled = true, releasedTick = RpNow, paroleEndTick = RpNow + 12 * GenDate.TicksPerDay, jobSecured = true, addressSecured = true, newsTick = -1 };
                g.story.formerInmates.Add(entry);
                string Letters(SupervisionCase c) => "RP_SetbackLabel".Translate(c.name).Resolve();
                // The job lost: place 0.7 of the hazard, in the order of the causes (new offence, reports, job lost, address left).
                SupervisionCase sc = RpCase(g, 0.5f, 7, true, true, true, add: true);
                entry.supervisionId = sc.id;
                sc.name = "Zrpsetback";
                float roll = RpRollFor(sc, 0.7f);
                int setbacks0 = g.stats.supervisionSetbacks, helped0 = g.stats.supervisionHelped, reoffended0 = g.stats.reoffended;
                Supervision.Review(g, sc, roll);
                bool reported = sc.setbackState == SetbackState.Pending && sc.setback == CaseSetback.JobLost && !sc.jobSecured && g.stats.supervisionSetbacks == setbacks0 + 1 && r.NewLetters.Count(l => l.Label.ToString() == Letters(sc)) == 1
                                && !entry.jobSecured && g.rearrests.All(x => x.name != sc.name) && g.stats.reoffended == reoffended0;
                if (!reported) RpFail(check, $"a job lost: {sc.setbackState} {sc.setback}, job held {sc.jobSecured}, setbacks +{g.stats.supervisionSetbacks - setbacks0}, letters {r.NewLetters.Count(l => l.Label.ToString() == Letters(sc))}, the register still says a job {entry.jobSecured}, nothing counted yet {g.stats.reoffended == reoffended0}");
                if (!ParoleLife.LetterLine(entry).Contains("RP_ParoleLetter_NoJob".Translate().Resolve())) RpFail(check, $"a letter after the job was lost still says: {ParoleLife.LetterLine(entry)}");
                // The next review is inside the wait: nothing yet (and no new roll: the day's trouble is already here).
                int held = sc.reviewsRolled;
                Supervision.Review(g, sc, 0.0001f);
                if (sc.state != CaseState.Reviewing || sc.setbackState != SetbackState.Pending || sc.reviewsRolled != held) RpFail(check, $"a review inside the wait of an open setback was {sc.state}, {sc.setbackState}, with {sc.reviewsRolled - held} new rolls");
                // The warden's help: once, the price paid now.
                int funds0 = g.ledger.funds;
                string first = Supervision.OfferHelp(g, map, sc), second = Supervision.OfferHelp(g, map, sc);
                int paid = funds0 - g.ledger.funds;
                if (first != null || second != "RP_HelpAlready" || paid != Supervision.HelpPrice || g.stats.supervisionHelped != helped0 + 1 || RpBills(r) != 1 || !sc.helped)
                    RpFail(check, $"help for the setback: first {first ?? "given"}, second {second ?? "given"}, paid {paid} (once, {Supervision.HelpPrice}), counted +{g.stats.supervisionHelped - helped0}, bills {RpBills(r)}");
                // Mended: the roll under the chance.
                Supervision.Review(g, sc, Supervision.HelpSuccess - 0.1f);
                bool mended = sc.state == CaseState.Reviewing && sc.setbackState == SetbackState.Recovered && sc.jobSecured && g.rearrests.All(x => x.name != sc.name) && entry.jobSecured && g.stats.reoffended == reoffended0;
                if (!mended) RpFail(check, $"help that worked: {sc.state}, {sc.setbackState}, job {sc.jobSecured}, the register {entry.jobSecured}, rearrested {g.rearrests.Any(x => x.name == sc.name)}");
                // Unhelped: the same setback ends in the rearrest at the review, for the term broken, once.
                SupervisionCase sc2 = RpCase(g, 0.5f, 7, true, true, true, add: true);
                sc2.name = "Zrpsetback2";
                Supervision.Review(g, sc2, RpRollFor(sc2, 0.7f));
                Supervision.Review(g, sc2, 0.0001f);
                int rearrests0 = g.rearrests.Count;
                Supervision.Review(g, sc2, 0.0001f);
                Rearrest lost = g.rearrests.Count > 0 ? g.rearrests.Last() : null;
                if (sc2.outcome != CaseOutcome.Rearrested || sc2.reason != "RP_Violation_JobLost" || lost == null || lost.reason != "RP_Violation_JobLost" || g.rearrests.Count != rearrests0 + 1 || g.stats.reoffended != reoffended0 + 1)
                    RpFail(check, $"an unhelped setback: {sc2.state} {sc2.outcome} for {sc2.reason}, rearrests +{g.rearrests.Count - rearrests0} (once), reoffended +{g.stats.reoffended - reoffended0}");
                // Helped and the roll over the chance: paid, and lost all the same (nothing is refunded).
                SupervisionCase sc3 = RpCase(g, 0.5f, 7, true, true, true, add: true);
                sc3.name = "Zrpsetback3";
                Supervision.Review(g, sc3, RpRollFor(sc3, 0.9f));
                bool addressSetback = sc3.setback == CaseSetback.AddressWithdrawn && sc3.setbackState == SetbackState.Pending;
                int funds1 = g.ledger.funds;
                Supervision.OfferHelp(g, map, sc3);
                Supervision.Review(g, sc3, 0.0001f);
                Supervision.Review(g, sc3, Supervision.HelpSuccess + 0.1f);
                if (!addressSetback || sc3.outcome != CaseOutcome.Rearrested || sc3.reason != "RP_Violation_AddressLeft" || funds1 - g.ledger.funds != Supervision.HelpPrice)
                    RpFail(check, $"help that did not work: the address setback {addressSetback}, {sc3.outcome} for {sc3.reason}, paid {funds1 - g.ledger.funds} (the price, kept)");
                // Somebody who had no terms is arrested for a new offence, whatever the setback was.
                SupervisionCase sc4 = RpCase(g, 0.5f, 7, false, true, true, add: true);
                sc4.name = "Zrpsetback4";
                Supervision.Review(g, sc4, RpRollFor(sc4, 0.7f));
                Supervision.Review(g, sc4, 0.0001f);
                Supervision.Review(g, sc4, 0.0001f);
                if (sc4.outcome != CaseOutcome.Rearrested || sc4.reason != ParoleLife.NewOffence) RpFail(check, $"a release at the end of the sentence, after a lost job: {sc4.outcome} for {sc4.reason} (a new offence expected: they had no terms)");
                // No help when there is nothing open, none for a case that is over.
                if (Supervision.HelpRefusal(g, map, sc4) != "RP_HelpNoSetback") RpFail(check, "help was offered for a case that is over");

                // The Intake tab's list: a setback waiting for its review is listed first, however many cases were opened before it (the help button is on its line and nowhere else; the old list, oldest first and
                // cut at 15 lines, left the help for the newest of 21 open cases out of reach). The rest fill the rows, and the ones left out are counted.
                var many = new List<SupervisionCase>();
                for (int i = 0; i < 20; i++) many.Add(RpCase(g, 0.3f, 7, false, true, true, add: true));
                SupervisionCase newest = many[many.Count - 1];
                (newest.name, newest.setback, newest.setbackState, newest.setbackAt) = ("Zrpnewest", CaseSetback.JobLost, SetbackState.Pending, newest.reviewsHeld);
                const int rows = 12;
                List<SupervisionCase> listed = ReleasePrep.ShownCases(g.supervision, rows, 3, out int notListed);
                int openCount = g.supervision.cases.Count(x => x.state != CaseState.Resolved), listedOpen = listed.Count(x => x.state != CaseState.Resolved);
                if (listed.FirstOrDefault() != newest || listedOpen != rows || listedOpen + notListed != openCount || listed.Count(x => x.state == CaseState.Resolved) != 3 || listed.Distinct().Count() != listed.Count)
                    RpFail(check, $"the Intake tab's list of {openCount} open cases: the newest, with a setback, is listed {(listed.Contains(newest) ? "at line " + (listed.IndexOf(newest) + 1) : "nowhere")} (first expected), {listedOpen} open listed ({rows}), {notListed} left out ({openCount - rows}), {listed.Count(x => x.state == CaseState.Resolved)} settled (3)");
            });
        }

        // ---- the interview ------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRpInterview(Map map)
        {
            Guarded("release preparation: an interview on a certificate wins a job or not", map, r =>
            {
                RpSetUp(r);
                const string check = "interview";
                PrisonGame g = PrisonGame.Instance;
                ProgramDef trade = DefDatabase<ProgramDef>.AllDefsListForReading.FirstOrDefault(d => d.trade);
                if (trade == null)
                {
                    RPLog.Message("[RimPrison:test] release preparation interview: the game has no program that certifies a trade, not tried");
                    return;
                }
                RpRoomFor(map, 4);
                g.ledger.funds = 20000;
                Pawn working = RpInmate(map, r, 2f);
                working.Inmate().passedPrograms.Add(trade);
                working.Inmate().jobWaiting = true;
                if (ReleasePrep.Choose(g, map, working, ReleaseSupport.Interview) != "RP_PrepHasJob") RpFail(check, "an interview was offered to somebody with a job waiting for them");
                (Pawn won, Pawn lost) = (RpInmate(map, r, 2f), RpInmate(map, r, 2f));
                foreach (Pawn p in new[] { won, lost })
                {
                    p.Inmate().passedPrograms.Add(trade);
                    if (ReleasePrep.Choose(g, map, p, ReleaseSupport.Interview) is { } why) RpFail(check, $"an interview could not be arranged for somebody with a certificate: {why}");
                }
                if (won.Inmate().releasePlan != ReleaseSupport.Interview) return;
                // (Released at the end of the sentence: on parole a certified trade may already have found them a job at the gate, and that is not what is looked at here.)
                foreach (Pawn p in new[] { won, lost }) ReleaseSystem.Release(g, p, record: false);
                SupervisionCase cw = g.supervision.Find(g.shuttle.OutboundOf(won).caseId), cl = g.supervision.Find(g.shuttle.OutboundOf(lost).caseId);
                RpDepart(won, r);
                RpDepart(lost, r);
                if (cw == null || cl == null || cw.support != ReleaseSupport.Interview || cw.jobSecured || cw.trade != trade) { RpFail(check, "the interview did not reach the case"); return; }
                float before = Supervision.Hazard(cw);
                int interviews0 = g.stats.supervisionInterviews, billsBefore = RpBills(r);
                Supervision.Review(g, cw, 0.9999f, interviewRoll: 0.1f);
                Supervision.Review(g, cl, 0.9999f, interviewRoll: 0.9f);
                bool wonJob = cw.jobSecured && cw.jobByInterview && cw.supportDone && Supervision.Hazard(cw) < before - 0.00001f && g.stats.supervisionInterviews == interviews0 + 2;
                bool lostJob = !cl.jobSecured && cl.supportDone && Mathf.Abs(Supervision.Hazard(cl) - before) < 0.00001f;
                if (!wonJob || !lostJob) RpFail(check, $"the interviews: the one that won has a job {cw.jobSecured} and a lower hazard {Supervision.Hazard(cw):F4} < {before:F4}, held {g.stats.supervisionInterviews - interviews0} of 2; the one that did not win has none {!cl.jobSecured} and the same hazard {Supervision.Hazard(cl):F4}");
                // Held once: the next review does not hold it again.
                Supervision.Review(g, cl, 0.9999f, interviewRoll: 0.1f);
                if (cl.jobSecured || g.stats.supervisionInterviews != interviews0 + 2 || RpBills(r) != billsBefore) RpFail(check, "an interview was held a second time, or cost money");
            });
        }

        // ---- the old ways stay as they were ---------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRpLegacy(Map map)
        {
            Guarded("release preparation: a queued rearrest and an old release are unchanged", map, r =>
            {
                RpSetUp(r);
                const string check = "legacy";
                PrisonGame g = PrisonGame.Instance;
                g.ledger.funds = 20000;
                // A rearrest queued before: still there, untouched by the passes of the new process, and processed once when it falls due and they have gone.
                Pawn old = RpInmate(map, r, 2f);
                old.DeSpawn();
                Find.WorldPawns.PassToWorld(old, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
                r.Add(() =>
                {
                    if (Find.WorldPawns.Contains(old) && !old.Destroyed) Find.WorldPawns.RemovePawn(old);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(old);
                });
                var legacy = new Rearrest { name = old.LabelShort, paroled = true, recommended = false, reason = "RP_Violation_NoWork", pawn = old, dueTick = RpNow + 3 * GenDate.TicksPerDay };
                g.rearrests.Add(legacy);
                Reoffenders.KeepForReturn(g, old);
                SupervisionCase other = RpCase(g, 0.5f, 7, true, false, false, add: true);
                for (int i = 0; i < 4; i++) Supervision.Review(g, other, 0.0001f);
                Supervision.Hourly(g, map);
                RpCheckRearrestsOf(g, legacy);
                bool intact = g.rearrests.Contains(legacy) && legacy.pawn == old && legacy.reason == "RP_Violation_NoWork" && legacy.dueTick > RpNow && g.rearrests.Count(x => x.pawn == old) == 1 && Find.WorldPawns.ForcefullyKeptPawns.Contains(old);
                if (!intact) RpFail(check, $"a rearrest queued before the new process was changed or lost while cases were reviewed (still queued {g.rearrests.Contains(legacy)}, reason {legacy.reason}, kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(old)})");
                legacy.dueTick = RpNow - 1;
                int reoffended0 = g.stats.reoffended, violations0 = g.stats.paroleViolations, fundsBefore = g.ledger.funds;
                Rand.PushState(ParoleSeedFor(0.5f, false));
                try { RpCheckRearrestsOf(g, legacy); }
                finally { Rand.PopState(); }
                RpCheckRearrestsOf(g, legacy);
                if (g.rearrests.Contains(legacy) || g.ledger.funds != fundsBefore - ParoleSystem.ParoleBonus || g.stats.paroleViolations != violations0 + 1 || g.stats.reoffended != reoffended0)
                    RpFail(check, $"the old rearrest, when due: still queued {g.rearrests.Contains(legacy)}, funds {fundsBefore}->{g.ledger.funds} (the clawback once), violations +{g.stats.paroleViolations - violations0}, reoffended +{g.stats.reoffended - reoffended0} (the old process counted it at the departure: not again)");
                // The old release (the roll at the gate, switched on for checks): a rearrest queued at once, the reoffence on the outbound entry, no case, no change to the register's link.
                Pawn gate = RpInmate(map, r, 2f);
                int cases0 = g.supervision.cases.Count, rearrests0 = g.rearrests.Count;
                Rand.PushState(ParoleSeedFor(ParoleSystem.ReoffendChance(gate.Inmate()), true));
                ReleaseSystem.LegacyReoffendRoll = true;
                try { ReleaseSystem.Release(g, gate, paroled: true, record: false); }
                finally
                {
                    ReleaseSystem.LegacyReoffendRoll = false;
                    Rand.PopState();
                }
                FormerInmate gateEntry = g.story.formerInmates.LastOrDefault();
                bool legacyRelease = g.shuttle.OutboundOf(gate) is { reoffends: true, caseId: -1 } && g.rearrests.Count == rearrests0 + 1 && g.supervision.cases.Count == cases0 && gateEntry is { supervisionId: -1, newsTick: -1 };
                r.Add(() => g.rearrests.RemoveAll(x => x.pawn == gate));
                if (!legacyRelease) RpFail(check, $"the old release: the reoffence on the entry {g.shuttle.OutboundOf(gate)?.reoffends}, the case id {g.shuttle.OutboundOf(gate)?.caseId}, rearrests +{g.rearrests.Count - rearrests0}, cases +{g.supervision.cases.Count - cases0}, the register's link {gateEntry?.supervisionId}");
                // A release on a cause (compassionate) is as it was: no case.
                Pawn kind = RpInmate(map, r, 2f);
                int cases1 = g.supervision.cases.Count;
                ReleaseSystem.Release(g, kind, record: false, cause: "RP_Cause_Compassionate");
                if (g.supervision.cases.Count != cases1 || g.shuttle.OutboundOf(kind) is not { caseId: -1 })
                    RpFail(check, $"a compassionate release opened a case (cases +{g.supervision.cases.Count - cases1})");
                // Nothing shows who is innocent: an innocent inmate with nothing arranged, paroled on a recommendation, is put under review like a guilty one beside them - a case each, read the same on the Intake tab, no trust
                // at the gate for either - and the innocent one's case has no risk and keeps nobody once they are gone.
                Pawn innocent = RpInmate(map, r, 2f), guilty = RpInmate(map, r, 2f);
                innocent.Inmate().innocent = true;
                float trustAtGate = g.story.boardTrust;
                ReleaseSystem.Release(g, innocent, paroled: true, recommended: true, record: false);
                ReleaseSystem.Release(g, guilty, paroled: true, recommended: true, record: false);
                SupervisionCase innocentCase = g.shuttle.OutboundOf(innocent) is { caseId: >= 0 } oi ? g.supervision.Find(oi.caseId) : null, guiltyCase = g.shuttle.OutboundOf(guilty) is { caseId: >= 0 } og ? g.supervision.Find(og.caseId) : null;
                string Reads(SupervisionCase c) => c == null ? "(no case)" : ReleasePrep.CaseLine(c).Replace(c.name, "?");
                if (innocentCase == null || guiltyCase == null || innocentCase.baseRisk != 0f || guiltyCase.baseRisk <= 0f || Reads(innocentCase) != Reads(guiltyCase) || !Mathf.Approximately(g.story.boardTrust, trustAtGate))
                    RpFail(check, $"an innocent release and a guilty one: cases {innocentCase != null}/{guiltyCase != null}, risks {innocentCase?.baseRisk}/{guiltyCase?.baseRisk}, the Intake tab reads '{Reads(innocentCase)}' and '{Reads(guiltyCase)}', trust {trustAtGate:F2}->{g.story.boardTrust:F2} at the gate");
                if (innocentCase != null)
                {
                    RpDepart(innocent, r);
                    if (innocentCase.state != CaseState.Reviewing || innocentCase.pawn != null || g.supervision.Holds(innocent))
                        RpFail(check, $"the innocent one's case after they left: {innocentCase.state}, still holds the pawn {innocentCase.pawn != null} (a case with no risk keeps nobody)");
                }
                // Innocent, with lodging arranged: reviewed all the same (the bill is the same whoever walks out), and never rearrested.
                RpRoomFor(map, 1);
                Pawn innocentPlanned = RpInmate(map, r, 2f);
                innocentPlanned.Inmate().innocent = true;
                ReleasePrep.Choose(g, map, innocentPlanned, ReleaseSupport.Lodging);
                ReleaseSystem.Release(g, innocentPlanned, record: false);
                SupervisionCase ic = g.shuttle.OutboundOf(innocentPlanned) is { caseId: >= 0 } ie ? g.supervision.Find(ie.caseId) : null;
                if (ic == null || ic.baseRisk != 0f) RpFail(check, $"an innocent inmate with lodging arranged: case {ic != null}, risk {ic?.baseRisk}");
                // The unresolved are not in the stayed-out share, the old ratio is unchanged without them.
                (int released0, int reoffendedAt) = (g.stats.released, g.stats.reoffended);
                (g.stats.released, g.stats.reoffended) = (10, 2);
                int open0 = g.supervision.OpenCount;
                float withOpen = PrisonGrading.StayedOutShare(g);
                bool share = open0 > 5 || Mathf.Abs(withOpen - (1f - 2f / (10 - open0))) < 0.0001f;
                var held = new List<SupervisionCase>();
                for (int i = 0; i < 2; i++) held.Add(RpCase(g, 0.3f, 7, true, false, false, add: true));
                float withTwoMore = PrisonGrading.StayedOutShare(g);
                bool excluded = open0 > 5 || Mathf.Abs(withTwoMore - (1f - 2f / (10 - open0 - 2))) < 0.0001f;
                foreach (SupervisionCase h in held) g.supervision.cases.Remove(h);
                (g.stats.released, g.stats.reoffended) = (released0, reoffendedAt);
                if (!share || !excluded) RpFail(check, $"the stayed-out share with releases under review: {withOpen:F3} with {open0} open (1 - 2/{10 - open0} expected), {withTwoMore:F3} with two more");
            });
        }

        // ---- letters ------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRpLetters(Map map)
        {
            Guarded("release preparation: a parolee writes before a later failure", map, r =>
            {
                RpSetUp(r);
                const string check = "letters";
                PrisonGame g = PrisonGame.Instance;
                Pawn reader = RpInmate(map, r, 20f);
                CompInmate rc = reader.Inmate();
                MailKindDef kind = DefDatabase<MailKindDef>.GetNamed("RP_Mail_Parolee");
                // (Nobody else under supervision writes while this is looked at.)
                int now = RpNow;
                var supervised = g.story.formerInmates.Where(e => e.paroled && ParoleLife.Supervised(e, now)).ToList();
                List<int> ends = supervised.Select(e => e.paroleEndTick).ToList();
                foreach (FormerInmate e in supervised) e.paroleEndTick = now - 1;
                r.Add(() =>
                {
                    for (int i = 0; i < supervised.Count; i++) supervised[i].paroleEndTick = ends[i];
                });
                // An old entry with no news (the register knew they would be rearrested): silent, as it was.
                var legacy = new FormerInmate { name = "Zrpold", number = 99982, paroled = true, releasedTick = now, paroleEndTick = now + 9 * GenDate.TicksPerDay, newsTick = -1, supervisionId = -1 };
                g.story.formerInmates.Add(legacy);
                bool legacySilent = !kind.Worker.CanSend(reader, rc);
                // A release under review: has left, nothing decided; and an ordinary letter can come from them.
                SupervisionCase sc = RpCase(g, 0.5f, 7, true, true, true, add: true);
                var fresh = new FormerInmate { name = "Zrpnew", number = 99983, paroled = true, releasedTick = now, paroleEndTick = now + 9 * GenDate.TicksPerDay, newsTick = -1, jobSecured = true, addressSecured = true, supervisionId = sc.id };
                g.story.formerInmates.Add(fresh);
                legacy.paroleEndTick = now - 1;
                bool writes = kind.Worker.CanSend(reader, rc);
                string line = ParoleLife.LetterLine(fresh);
                bool saysGetsOn = line.Contains("RP_ParoleLetter_Job".Translate().Resolve()) && line.Contains("RP_ParoleLetter_Family".Translate().Resolve());
                // Not before they have left; not after the arrest; yes after a quiet end.
                sc.state = CaseState.Awaiting;
                bool silentBefore = !kind.Worker.CanSend(reader, rc);
                sc.state = CaseState.Reviewing;
                int freshEnd = fresh.paroleEndTick;
                Supervision.Finish(g, sc, CaseOutcome.Rearrested);
                bool silentAfter = !kind.Worker.CanSend(reader, rc);
                // (Still silent once the settled case is pruned from the book, with news from them that came before the arrest: the arrest ended their parole term.)
                g.supervision.cases.Remove(sc);
                fresh.news = "RP_FormerNews_ParoleReport";
                bool silentPruned = !kind.Worker.CanSend(reader, rc) && fresh.paroleEndTick <= now;
                g.supervision.cases.Add(sc);
                (fresh.news, fresh.paroleEndTick) = (null, freshEnd);
                (sc.state, sc.outcome) = (CaseState.Resolved, CaseOutcome.StayedOut);
                bool writesAfterQuiet = kind.Worker.CanSend(reader, rc);
                r.Add(() => g.rearrests.RemoveAll(x => x.name == sc.name));
                if (!legacySilent || !writes || !saysGetsOn || !silentBefore || !silentAfter || !silentPruned || !writesAfterQuiet)
                    RpFail(check, $"letters from a parolee: an old entry with no news is silent {legacySilent}; one under review writes {writes} and says how they are ({line}) {saysGetsOn}; silent before they have left {silentBefore}, after the arrest {silentAfter}, after the arrest with the case pruned and old news on file {silentPruned} (parole ended {fresh.paroleEndTick <= now}), writing after a quiet end {writesAfterQuiet}");
                // A lodging that was paid for is in what they say.
                fresh.addressSecured = true;
                fresh.lodged = true;
                if (!ParoleLife.LetterLine(fresh).Contains("RP_ParoleLetter_Lodging".Translate().Resolve())) RpFail(check, $"a parolee in paid lodging says: {ParoleLife.LetterLine(fresh)}");
                // The news from outside waits while a setback is open.
                (sc.state, sc.outcome, sc.setbackState) = (CaseState.Reviewing, CaseOutcome.None, SetbackState.Pending);
                fresh.newsTick = now - 5;
                int news0 = g.stats.formerNews;
                // (The register is the check's own for the pass: a real entry whose news falls due now is not the check's to send. The rollback puts the register back.)
                g.story.formerInmates.RemoveAll(e => e != fresh);
                FormerInmates.Hourly(g, map);
                bool waited = fresh.newsTick > now && g.stats.formerNews == news0 && SupervisionOpenFor(g, fresh);
                if (!waited) RpFail(check, "word of how a parolee is getting on was sent while a setback was open");
                fresh.newsTick = -1;
            });
        }

        private static bool SupervisionOpenFor(PrisonGame g, FormerInmate e) => Supervision.SetbackOpen(g, e);

        // ---- the world pawns -------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRpLifecycle(Map map)
        {
            Guarded("release preparation: pawns kept within the cap, once, and let go", map, r =>
            {
                RpSetUp(r);
                const string check = "pawns";
                PrisonGame g = PrisonGame.Instance;
                // The cap: the oldest let go of their pawns past the limit, the case goes on.
                // (The book is the check's own - see RpSetUp: the cap counts every open case, and would let go of the live prison's.)
                var kept = new List<SupervisionCase>();
                for (int i = 0; i < Supervision.MaxKept + 3; i++)
                {
                    Pawn p = Throwaway(map, r, spawn: false);
                    Find.WorldPawns.ForcefullyKeptPawns.Add(p);
                    SupervisionCase sc = RpCase(g, 0.3f, 7, true, false, false, p, add: true);
                    kept.Add(sc);
                }
                Supervision.EnforceCap(g);
                int holding = g.supervision.cases.Count(x => x.pawn != null && x.state != CaseState.Resolved && kept.Contains(x));
                bool oldestLetGo = kept.Take(3).All(x => x.pawn == null && x.state == CaseState.Reviewing) && kept.Skip(3).All(x => x.pawn != null && Find.WorldPawns.ForcefullyKeptPawns.Contains(x.pawn));
                if (holding != Supervision.MaxKept || !oldestLetGo) RpFail(check, $"past the cap {holding} cases hold a pawn ({Supervision.MaxKept} expected), the three oldest let go and still under review {oldestLetGo}");
                foreach (SupervisionCase x in kept) g.supervision.cases.Remove(x);

                // The register and a case hold one pawn together: the register letting go does not drop it, the case's end does, once.
                Pawn both = Throwaway(map, r, spawn: false);
                Find.WorldPawns.ForcefullyKeptPawns.Add(both);
                SupervisionCase shared = RpCase(g, 0.3f, 7, true, false, false, both, add: true);
                var entry = new FormerInmate { name = "Zrpboth", number = 99984, pawn = both, supervisionId = shared.id };
                g.story.formerInmates.Add(entry);
                ExInmates.Unkeep(entry);
                bool heldByCase = entry.pawn == null && Find.WorldPawns.ForcefullyKeptPawns.Contains(both) && shared.pawn == both;
                Supervision.Finish(g, shared, CaseOutcome.StayedOut);
                Supervision.Finish(g, shared, CaseOutcome.StayedOut);
                bool letGo = shared.pawn == null && !Find.WorldPawns.ForcefullyKeptPawns.Contains(both);
                if (!heldByCase || !letGo) RpFail(check, $"the register let go of a pawn a case still holds: dropped {!heldByCase}; after the case ended they are still kept {!letGo}");

                // A rearrest takes the pawn over (kept for the return), once, with the case no longer holding it.
                Pawn back = Throwaway(map, r, spawn: false);
                Find.WorldPawns.ForcefullyKeptPawns.Add(back);
                SupervisionCase caught = RpCase(g, 0.3f, 7, true, false, false, back, add: true);
                int rearrests0 = g.rearrests.Count;
                caught.reason = "RP_Violation_Reports";
                Supervision.Finish(g, caught, CaseOutcome.Rearrested);
                Supervision.Finish(g, caught, CaseOutcome.Rearrested);
                bool taken = g.rearrests.Count == rearrests0 + 1 && g.rearrests.Last().pawn == back && caught.pawn == null && Find.WorldPawns.ForcefullyKeptPawns.Contains(back) && caught.rearrestQueued;
                r.Add(() => g.rearrests.RemoveAll(x => x.pawn == back));
                if (!taken) RpFail(check, $"a rearrest from a case: rearrests +{g.rearrests.Count - rearrests0} (once), the pawn kept for the return {Find.WorldPawns.ForcefullyKeptPawns.Contains(back)}, the case let go of it {caught.pawn == null}");

                // The same person waiting to be arrested already (from before) is not queued a second time.
                Pawn twice = Throwaway(map, r, spawn: false);
                Find.WorldPawns.ForcefullyKeptPawns.Add(twice);
                var waiting = new Rearrest { name = twice.LabelShort, pawn = twice, dueTick = RpNow + 99999, reason = ParoleLife.NewOffence };
                g.rearrests.Add(waiting);
                r.Add(() => g.rearrests.Remove(waiting));
                SupervisionCase dup = RpCase(g, 0.3f, 7, true, false, false, twice, add: true);
                dup.reason = ParoleLife.NewOffence;
                int count0 = g.rearrests.Count;
                Supervision.Finish(g, dup, CaseOutcome.Rearrested);
                if (g.rearrests.Count != count0 || g.rearrests.Count(x => x.pawn == twice) != 1) RpFail(check, "a person already waiting to be arrested was queued a second time by a case");

                // Nobody to review: died before leaving - voided, nothing counted, and the lodging promised to them not booked.
                g.ledger.funds = 20000;
                Pawn died = Throwaway(map, r, spawn: false);
                SupervisionCase gone = RpCase(g, 0.3f, 7, true, false, false, died, add: true);
                (gone.state, gone.departedTick, gone.support) = (CaseState.Awaiting, -1, ReleaseSupport.Lodging);
                died.health.SetDead();
                (int started0, int stayed0, int reoffended0, int released0) = (g.stats.supervisionStarted, g.stats.supervisionStayedOut, g.stats.reoffended, g.stats.released);
                Supervision.Hourly(g, map);
                if (gone.state != CaseState.Resolved || gone.outcome != CaseOutcome.Voided || g.stats.supervisionStarted != started0 || g.stats.supervisionStayedOut != stayed0 || g.stats.reoffended != reoffended0 || RpBills(r) != 0)
                    RpFail(check, $"a case whose person died before they left was {gone.state} {gone.outcome} and counted something (started +{g.stats.supervisionStarted - started0}, stayed out +{g.stats.supervisionStayedOut - stayed0}, reoffended +{g.stats.reoffended - reoffended0}, bills {RpBills(r)})");

                // Gone from the game, not dead (the world discarded them - the ambulance's way out): before they left, the review starts (and the release is counted, the shuttle never did) with nobody kept and no lodging
                // booked; under review, it goes on with nobody kept. Neither is voided.
                Pawn discarded = Throwaway(map, r, spawn: false), discardedLater = Throwaway(map, r, spawn: false);
                foreach (Pawn p in new[] { discarded, discardedLater }) Find.WorldPawns.ForcefullyKeptPawns.Add(p);
                SupervisionCase early = RpCase(g, 0.3f, 7, true, false, false, discarded, add: true), later = RpCase(g, 0.3f, 7, true, false, false, discardedLater, add: true);
                (early.state, early.departedTick, early.nextReviewTick, early.support) = (CaseState.Awaiting, -1, -1, ReleaseSupport.Lodging);
                discarded.Destroy(DestroyMode.Vanish);
                discardedLater.Destroy(DestroyMode.Vanish);
                (started0, released0) = (g.stats.supervisionStarted, g.stats.released);
                Supervision.Hourly(g, map);
                Supervision.Review(g, later, 0.9999f);
                bool earlyGoesOn = early.state == CaseState.Reviewing && early.pawn == null && early.supportDone && !early.supportFulfilled && RpBills(r) == 0 && g.stats.supervisionStarted == started0 + 1 && g.stats.released == released0 + 1;
                bool laterGoesOn = later.state == CaseState.Reviewing && later.outcome == CaseOutcome.None && later.pawn == null && later.reviewsRolled == 1 && !Find.WorldPawns.ForcefullyKeptPawns.Contains(discardedLater);
                if (!earlyGoesOn || !laterGoesOn)
                    RpFail(check, $"a discarded pawn: before they left the case is {early.state} (holds a pawn {early.pawn != null}, lodging booked {early.supportFulfilled}, bills {RpBills(r)}, started +{g.stats.supervisionStarted - started0}, released +{g.stats.released - released0}); under review it is {later.state} {later.outcome} after {later.reviewsRolled} roll(s), holds a pawn {later.pawn != null}, still kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(discardedLater)} (going on, not voided, expected)");

                // An inmate again while waiting for the shuttle (a recapture): voided, and never begun later. Released again before the hourly pass: the older case is closed by the new one - one person, one open case.
                Pawn again = RpInmate(map, r, 2f);
                ReleaseSystem.Release(g, again, record: false);
                SupervisionCase first = g.shuttle.OutboundOf(again) is { caseId: >= 0 } o1 ? g.supervision.Find(o1.caseId) : null;
                g.shuttle.Forget(again);
                again.guest.Released = false;
                ReleaseSystem.Release(g, again, record: false);
                SupervisionCase second = g.shuttle.OutboundOf(again) is { caseId: >= 0 } o2 ? g.supervision.Find(o2.caseId) : null;
                int openOfThem = g.supervision.cases.Count(x => x.pawnId == again.thingIDNumber && x.state != CaseState.Resolved);
                if (first == null || second == null || first == second || first.outcome != CaseOutcome.Voided || openOfThem != 1)
                    RpFail(check, $"released, back inside and released again: the first case {first?.state} {first?.outcome}, a second case {second != null && second != first}, {openOfThem} open for them (1 expected)");
                g.shuttle.Forget(again);
                again.guest.Released = false;
                (started0, released0) = (g.stats.supervisionStarted, g.stats.released);
                Supervision.Hourly(g, map);
                Supervision.Hourly(g, map);
                if (second != null && (second.outcome != CaseOutcome.Voided || g.stats.supervisionStarted != started0 || g.stats.released != released0))
                    RpFail(check, $"a case whose person is an inmate here again before they left was {second.state} {second.outcome} (started +{g.stats.supervisionStarted - started0}, released +{g.stats.released - released0}; voided, nothing counted, expected)");
                // Under review, and an inmate here again: voided at the review, no rearrest.
                Pawn inside = RpInmate(map, r, 2f);
                SupervisionCase back2 = RpCase(g, 0.5f, 7, true, false, false, inside, add: true);
                int rearrestsInside = g.rearrests.Count;
                Supervision.Review(g, back2, 0.0001f);
                if (back2.outcome != CaseOutcome.Voided || g.rearrests.Count != rearrestsInside) RpFail(check, $"a case under review whose person is an inmate here again was {back2.state} {back2.outcome} at a review that would have been a breach (voided expected)");
            });
        }

        // ---- the register: future reoffenders, the hire, letting go -------------------------------------------------------------------------------------------------------

        private static void CheckRpRegister(Map map)
        {
            Guarded("release preparation: the register and the cases share their people", map, r =>
            {
                RpSetUp(r);
                const string check = "register";
                PrisonGame g = PrisonGame.Instance;
                int now = RpNow;
                // A case under review whose person the register keeps for an application: the rearrest ends the register's story - declined, no news, the parole term over, its keep given to the rearrest.
                Pawn kept = Throwaway(map, r, spawn: false);
                Find.WorldPawns.ForcefullyKeptPawns.Add(kept);
                SupervisionCase sc = RpCase(g, 0.5f, 7, true, false, false, kept, add: true);
                var entry = new FormerInmate { name = "Zrpkept", number = 99985, paroled = true, releasedTick = now, paroleEndTick = now + 9 * GenDate.TicksPerDay, newsTick = now + 5 * GenDate.TicksPerDay, pawn = kept, supervisionId = sc.id, past = new ExInmatePast() };
                g.story.formerInmates.Add(entry);
                sc.reason = "RP_Violation_Reports";
                Supervision.Finish(g, sc, CaseOutcome.Rearrested);
                Rearrest queued = g.rearrests.LastOrDefault(x => x.name == sc.name);
                r.Add(() => g.rearrests.RemoveAll(x => x.pawn == kept));
                bool closed = entry.declined && entry.pawn == null && entry.newsTick < 0 && entry.paroleEndTick <= now && queued?.pawn == kept && Find.WorldPawns.ForcefullyKeptPawns.Contains(kept);
                if (!closed)
                    RpFail(check, $"after a rearrest the register's entry: declined {entry.declined}, still holds the pawn {entry.pawn != null}, news due {entry.newsTick}, parole ends {entry.paroleEndTick} (now {now}); the rearrest has the pawn {queued?.pawn == kept}, kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(kept)}");

                // A case that let go of its pawn past the cap borrows the register's at the rearrest: the return has somebody to bring back.
                Pawn lent = Throwaway(map, r, spawn: false);
                Find.WorldPawns.ForcefullyKeptPawns.Add(lent);
                SupervisionCase capped = RpCase(g, 0.5f, 7, true, false, false, add: true);
                capped.pawnId = lent.thingIDNumber;
                var lentEntry = new FormerInmate { name = "Zrplent", number = 99986, releasedTick = now, newsTick = -1, pawn = lent, supervisionId = capped.id, past = new ExInmatePast() };
                g.story.formerInmates.Add(lentEntry);
                capped.reason = ParoleLife.NewOffence;
                Supervision.Finish(g, capped, CaseOutcome.Rearrested);
                r.Add(() => g.rearrests.RemoveAll(x => x.pawn == lent));
                if (g.rearrests.LastOrDefault(x => x.name == capped.name)?.pawn != lent || !Find.WorldPawns.ForcefullyKeptPawns.Contains(lent) || lentEntry.pawn != null)
                    RpFail(check, $"a case with no pawn of its own at the rearrest did not borrow the register's (the rearrest has {g.rearrests.LastOrDefault(x => x.name == capped.name)?.pawn?.LabelShort ?? "nobody"})");

                // Hired from the register through the register's own hire (ExInmates.Hire, as the applicant's letter does it): the case is closed at the hire, before any review, with nothing to review, no rearrest,
                // nothing counted, and the pawn no longer kept. (Asked straight after the hire: a review would void the case of one of the player's own anyway, so only this shows the hire's own call.)
                (int rearrests0, int reoffended0, int stayed0) = (g.rearrests.Count, g.stats.reoffended, g.stats.supervisionStayedOut);
                StaffRoleDef cook = RPStaffRoleDefOf.RP_Cook;
                if (!Unlocks.IsUnlocked(cook) || !ExInmates.Roles.Contains(cook)) RPLog.Message("[RimPrison:test] release preparation register: the hire through the register not tried, the cook post is locked");
                else
                {
                    g.ledger.funds = Math.Max(g.ledger.funds, 100000);
                    FormerInmate hiredEntry = ExFormer(map, r, 5f, ExCleanPast(), cook);
                    Pawn hiredPawn = hiredEntry.pawn;
                    Find.WorldPawns.ForcefullyKeptPawns.Add(hiredPawn);
                    SupervisionCase hired = RpCase(g, 0.5f, 7, true, false, false, hiredPawn, add: true);
                    hiredEntry.supervisionId = hired.id;
                    bool hiredThere = ExInmates.Hire(g, map, hiredEntry, cook);
                    if (hiredThere) ExStaffGoes(r, hiredPawn);
                    if (!hiredThere) RPLog.Message($"[RimPrison:test] release preparation register: the register's hire of {hiredPawn.LabelShort} as a cook did not go through, the case at the hire not tried");
                    else if (hired.state != CaseState.Resolved || hired.outcome != CaseOutcome.Voided || hired.pawn != null || Find.WorldPawns.ForcefullyKeptPawns.Contains(hiredPawn) || g.rearrests.Count != rearrests0
                             || g.stats.reoffended != reoffended0 || g.stats.supervisionStayedOut != stayed0)
                        RpFail(check, $"a case whose person was hired onto the staff through the register: at the hire {hired.state} {hired.outcome}, still holds them {hired.pawn != null}, still kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(hiredPawn)}, rearrests +{g.rearrests.Count - rearrests0}, reoffended +{g.stats.reoffended - reoffended0} (voided at the hire, nothing queued or counted, expected)");
                }

                // One of the player's own by any other way (a pawn of the player's faction under review): voided at the review, not rearrested at a roll that would have been a breach.
                Pawn turned = Throwaway(map, r, spawn: false);
                Find.WorldPawns.ForcefullyKeptPawns.Add(turned);
                SupervisionCase ours = RpCase(g, 0.5f, 7, true, false, false, turned, add: true);
                Faction turnedFaction0 = turned.Faction;
                turned.SetFaction(Faction.OfPlayer);
                r.Add(() =>
                {
                    if (!turned.Destroyed && turned.Faction != turnedFaction0) turned.SetFaction(turnedFaction0);
                });
                int rearrestsOurs = g.rearrests.Count;
                Supervision.Review(g, ours, 0.0001f);
                if (ours.outcome != CaseOutcome.Voided || g.rearrests.Count != rearrestsOurs || Find.WorldPawns.ForcefullyKeptPawns.Contains(turned))
                    RpFail(check, $"a case under review whose person is of the player's faction was {ours.state} {ours.outcome} at a roll that would have been a breach, rearrests +{g.rearrests.Count - rearrestsOurs}, still kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(turned)} (voided, let go, expected)");

                // Letting go is shared: the register letting go of somebody waiting to be brought back, or a rearrest forgotten while the register keeps them, drops nobody's keep.
                Pawn returning = Throwaway(map, r, spawn: false);
                Find.WorldPawns.ForcefullyKeptPawns.Add(returning);
                var ret = new Rearrest { name = returning.LabelShort, pawn = returning, dueTick = now + 99999, reason = ParoleLife.NewOffence };
                g.returning.Add(ret);
                var retEntry = new FormerInmate { name = "Zrpreturning", number = 99988, releasedTick = now, newsTick = -1, pawn = returning };
                g.story.formerInmates.Add(retEntry);
                ExInmates.Unkeep(retEntry);
                bool keptForReturn = Find.WorldPawns.ForcefullyKeptPawns.Contains(returning);
                g.returning.Remove(ret);
                retEntry.pawn = returning;
                Reoffenders.Forget(ret);
                bool keptForRegister = Find.WorldPawns.ForcefullyKeptPawns.Contains(returning);
                ExInmates.Unkeep(retEntry);
                bool droppedAtLast = !Find.WorldPawns.ForcefullyKeptPawns.Contains(returning);
                if (!keptForReturn || !keptForRegister || !droppedAtLast)
                    RpFail(check, $"the shared keep: kept for the return after the register let go {keptForReturn}, kept for the register after the rearrest was forgotten {keptForRegister}, dropped once nobody holds them {droppedAtLast}");
            });
        }

        /// <summary>
        /// The Intake tab's scroll view (round 4: the release-plan tour's pane was empty, no error). The view is the content's height in the last frame; the release planning made the content taller than the first
        /// view, a wrapping listing put the rest in a second column off to the right and reported only that column's height, and within three frames the view was shorter than the first label, which went to a
        /// column of its own: nothing left in sight. OnGUI is never called headless, so this lays out the tab's own listing (<see cref="IntakeTab.NewListing"/>; rects and gaps, nothing drawn) frame after frame with
        /// the tab's feedback of its height into the next view, for content of the tour's shape: the help label three lines tall, then more than the first view in all.
        /// </summary>
        private static void CheckRpIntakeLayout(Map map)
        {
            Guarded("release preparation: the Intake tab's content height", map, r =>
            {
                const string check = "intake layout";
                const float width = 940f, spacing = 2f;
                var listingRect = HarmonyLib.AccessTools.Field(typeof(Listing), "listingRect");
                var curX = HarmonyLib.AccessTools.Field(typeof(Listing), "curX");
                var curY = HarmonyLib.AccessTools.Field(typeof(Listing), "curY");
                if (listingRect == null || curX == null || curY == null)
                {
                    RpFail(check, "Verse.Listing has no listingRect, curX or curY field any more: the probe cannot lay out the tab");
                    return;
                }
                float[] items = new[] { 66f }.Concat(Enumerable.Range(0, 44).Select(i => i % 2 == 0 ? 24f : 26f)).ToArray();
                float content = items.Sum() + spacing * items.Length;
                (float view, float firstX, int offColumn) Settle(Listing_Standard list)
                {
                    float view = IntakeTab.FirstViewHeight, firstX = 0f;
                    int offColumn = 0;
                    for (int frame = 0; frame < 10; frame++)
                    {
                        listingRect.SetValue(list, new Rect(0f, 0f, width, view));
                        curX.SetValue(list, 0f);
                        curY.SetValue(list, 0f);
                        list.ColumnWidth = width;
                        offColumn = 0;
                        for (int i = 0; i < items.Length; i++)
                        {
                            Rect rect = list.GetRect(items[i]);
                            if (i == 0) firstX = rect.x;
                            if (rect.x != 0f) offColumn++;
                            list.Gap(spacing);
                        }
                        view = list.CurHeight;
                    }
                    return (view, firstX, offColumn);
                }
                // (The probe first sees the old listing empty the pane, else it proves nothing.)
                (float oldView, float oldFirstX, _) = Settle(new Listing_Standard());
                if (oldView >= items[0] || oldFirstX == 0f)
                    RpFail(check, $"the probe no longer sees a wrapping listing empty the pane (view {oldView:F0}, the first label at x {oldFirstX:F0}): Verse.Listing has changed, the check needs a new look");
                (float tabView, float tabFirstX, int tabOff) = Settle(IntakeTab.NewListing());
                if (!Mathf.Approximately(tabView, content) || tabFirstX != 0f || tabOff > 0)
                    RpFail(check, $"the tab's listing settles at a view of {tabView:F0} for {content:F0} of content, the first label at x {tabFirstX:F0}, {tabOff} of {items.Length} rows outside the first column (the pane shows less than its content, or nothing)");
            });
        }

        // ---- the saveload plan -----------------------------------------------------------------------------------------------------------------------------------------------

        private static string ReleasePrepDescribe(PrisonGame g, Map map) =>
            $"{g.supervision.Describe()}|{g.stats.supervisionStarted}/{g.stats.supervisionStayedOut}/{g.stats.supervisionRearrested}/{g.stats.supervisionSetbacks}/{g.stats.supervisionHelped}/{g.stats.supervisionLodged}/{g.stats.supervisionInterviews}"
            + $"|plans={string.Join(",", map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { releasePlan: not ReleaseSupport.None }).OrderBy(p => p.thingIDNumber).Select(p => $"{p.thingIDNumber}:{p.Inmate().releasePlan}"))}"
            + $"|register={string.Join(",", g.story.formerInmates.Where(e => e.supervisionId >= 0).Select(e => $"{e.name}:{e.supervisionId}:{e.lodged}:{e.jobSecured}:{e.addressSecured}"))}"
            + $"|out={string.Join(",", g.shuttle.outbound.Where(e => e.caseId >= 0).Select(e => $"{e.pawn?.thingIDNumber}:{e.caseId}:{e.kind}:{e.counted}:{e.reoffends}"))}";

        private static bool rpSeeded;
        private static string rpSeedDescribed;
        private static int rpNextId0, rpPlanInmateId = -1, rpLeavingId = -1;
        private static ReleaseSupport rpPlanWas;
        private static int[] rpStatsBefore;
        private static Pawn rpWorldPawn;
        private static readonly List<int> rpSeedIds = new List<int>();

        private static int[] RpStats(PrisonStats s) => new[] { s.supervisionStarted, s.supervisionStayedOut, s.supervisionRearrested, s.supervisionSetbacks, s.supervisionHelped, s.supervisionLodged, s.supervisionInterviews };

        /// <summary>
        /// Saveload plan, before the snapshot: a release in flight (a made-up inmate released and on the shuttle's outbound list, the entry carrying the case's id; lodging promised, nothing paid), a case under review mid-setback
        /// with help given and a world pawn kept for it (and its register entry, lodged), a case under review whose pawn the world discarded (it comes back as nobody), a case that ended rearrested with its rearrest queued, one
        /// that stayed out, an old queued rearrest beside them, an inmate with an interview promised, and every counter and every saved field off its default.
        /// </summary>
        private static void SeedReleasePrepForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            Pawn planned = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null && p.IsPrisonerOfColony && !ShuttleSystem.OnTheWayOut(p)).OrderBy(p => p.thingIDNumber).FirstOrDefault();
            if (planned == null)
            {
                RPLog.Message("[RimPrison:test] saveload release preparation: not seeded (needs an inmate)");
                return;
            }
            int now = Find.TickManager.TicksGame, day = GenDate.TicksPerDay;
            rpStatsBefore = RpStats(g.stats);
            rpNextId0 = g.supervision.nextId;
            rpSeedIds.Clear();
            rpPlanInmateId = planned.thingIDNumber;
            rpPlanWas = planned.Inmate().releasePlan;
            planned.Inmate().releasePlan = ReleaseSupport.Interview;
            // (The one in flight is made up: released and on the way out, the shuttle's own entry carrying the case. Released, so the shuttle's hourly tidy-up keeps the entry; destroyed after the reload.)
            SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(x => Unlocks.IsUnlocked(x) && !x.wingOnly) ?? InmateUtility.DefaultSecurity;
            Pawn leaving = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++);
            if (Find.WorldPawns.Contains(leaving)) Find.WorldPawns.RemovePawn(leaving);
            GenSpawn.Spawn(leaving, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            InmateGenerator.MakeInmate(leaving, null);
            leaving.guest.Released = true;
            rpLeavingId = leaving.thingIDNumber;
            rpWorldPawn = InmateGenerator.Generate(InmateUtility.DefaultSecurity, 90700, null, InmateUtility.DefaultSecurity.crimes.FirstOrDefault());
            Find.WorldPawns.PassToWorld(rpWorldPawn, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
            Find.WorldPawns.ForcefullyKeptPawns.Add(rpWorldPawn);
            ProgramDef trade = DefDatabase<ProgramDef>.AllDefsListForReading.FirstOrDefault(d => d.trade);
            SupervisionCase Make(string name, CaseState state, Action<SupervisionCase> set)
            {
                var sc = new SupervisionCase { version = Supervision.CurrentVersion, id = g.supervision.nextId++, name = name, number = 88000 + g.supervision.nextId, state = state, releasedTick = now - 6 * day, reviewDays = 7, baseRisk = 0.45f, trade = trade, stay = 2, paroled = true };
                sc.baseWeight = Supervision.TotalWeight(sc);
                set(sc);
                g.supervision.cases.Add(sc);
                rpSeedIds.Add(sc.id);
                return sc;
            }
            SupervisionCase inFlight = Make("Zrpwaiting", CaseState.Awaiting, sc =>
            {
                (sc.pawn, sc.pawnId, sc.support, sc.recommended, sc.releasedTick) = (leaving, leaving.thingIDNumber, ReleaseSupport.Lodging, true, now - 1000);
                sc.addressSecured = false;
            });
            g.shuttle.Register(leaving, OutboundKind.Released, counted: true, caseId: inFlight.id);
            SupervisionCase mid = Make("Zrpmid", CaseState.Reviewing, sc =>
            {
                (sc.pawn, sc.pawnId, sc.departedTick, sc.nextReviewTick, sc.endTick, sc.reviewsRolled, sc.reviewsHeld) = (rpWorldPawn, rpWorldPawn.thingIDNumber, now - 3 * day, now + 5 * GenDate.TicksPerHour, now + 4 * day, 3, 4);
                (sc.jobSecured, sc.addressSecured, sc.addressByLodging, sc.support, sc.supportDone, sc.supportFulfilled, sc.lodgingPaid) = (false, true, true, ReleaseSupport.Lodging, true, true, Supervision.LodgingPrice);
                (sc.setback, sc.setbackState, sc.setbackAt, sc.setbackTick, sc.helped, sc.helpPaid, sc.recommended) = (CaseSetback.JobLost, SetbackState.Pending, 3, now - day, true, Supervision.HelpPrice, true);
            });
            // (Under review, the pawn discarded by the world before the save: it saves as nobody. The review goes on without one.)
            Make("Zrpgone", CaseState.Reviewing, sc =>
            {
                (sc.pawnId, sc.departedTick, sc.nextReviewTick, sc.endTick, sc.reviewsRolled, sc.reviewsHeld, sc.jobSecured) = (424242, now - 2 * day, now + 9 * GenDate.TicksPerHour, now + 5 * day, 2, 2, true);
            });
            Make("Zrpcaught", CaseState.Resolved, sc =>
            {
                (sc.departedTick, sc.outcome, sc.reason, sc.resolvedTick, sc.rearrestQueued, sc.reviewsRolled, sc.reviewsHeld, sc.releasedTick) = (now - 5 * day, CaseOutcome.Rearrested, "RP_Violation_Reports", now - 3 * day, true, 2, 2, now - 5 * day);
            });
            Make("Zrpclean", CaseState.Resolved, sc =>
            {
                (sc.departedTick, sc.outcome, sc.resolvedTick, sc.reviewsRolled, sc.reviewsHeld, sc.releasedTick, sc.jobSecured, sc.jobByInterview) = (now - 9 * day, CaseOutcome.StayedOut, now - 2 * day, 7, 7, now - 9 * day, true, true);
            });
            g.story.formerInmates.Add(new FormerInmate { name = "Zrpmid", number = mid.number, releasedTick = now - 3 * day, paroled = true, paroleEndTick = now + 6 * day, jobSecured = false, addressSecured = true, lodged = true, supervisionId = mid.id, newsTick = now + 4 * day });
            g.rearrests.Add(new Rearrest { name = "Zrplegacy", paroled = true, reason = "RP_Violation_NoWork", dueTick = now + 30 * day });
            (g.stats.supervisionStarted, g.stats.supervisionStayedOut, g.stats.supervisionRearrested, g.stats.supervisionSetbacks, g.stats.supervisionHelped, g.stats.supervisionLodged, g.stats.supervisionInterviews) = (rpStatsBefore[0] + 4, rpStatsBefore[1] + 1, rpStatsBefore[2] + 1, rpStatsBefore[3] + 2, rpStatsBefore[4] + 1, rpStatsBefore[5] + 1, rpStatsBefore[6] + 3);
            rpSeeded = true;
            rpSeedDescribed = ReleasePrepDescribe(g, map);
            RPLog.Message($"[RimPrison:test] saveload release preparation: {rpSeedDescribed.Length} characters seeded");
        }

        /// <summary>
        /// After the reload: the cases came back whole (the snapshot compared them too, the shuttle's entry with its case id among them), and the passes that follow neither queue a second rearrest nor bill twice. The release in
        /// flight leaves (the shuttle's own call, told twice) and its review begins once - the release counted once, its lodging paid once; the one mid-setback is reviewed (the roll over the chance of the help) and ends in one
        /// rearrest, the old queued rearrest beside it unchanged; the one whose pawn is nobody now goes on being reviewed; the ones already settled are not touched. Then the plan's seeds go.
        /// </summary>
        private static void CheckReleasePrepAfterReload(Map map)
        {
            if (!rpSeeded) return;
            PrisonGame g = PrisonGame.Instance;
            string after = ReleasePrepDescribe(g, map);
            SupervisionCase Seeded(string name) => g.supervision.cases.FirstOrDefault(x => x.name == name);
            SupervisionCase waiting = Seeded("Zrpwaiting"), mid = Seeded("Zrpmid"), gone = Seeded("Zrpgone"), caught = Seeded("Zrpcaught"), clean = Seeded("Zrpclean");
            Pawn leaving = waiting?.pawn;
            bool inFlight = leaving != null && leaving.thingIDNumber == rpLeavingId && g.shuttle.OutboundOf(leaving) is { kind: OutboundKind.Released, counted: true } entry && entry.caseId == waiting.id;
            bool whole = after == rpSeedDescribed && waiting != null && mid != null && gone != null && caught != null && clean != null && mid.pawn != null && mid.pawn.thingIDNumber == mid.pawnId && inFlight
                         && mid.setbackState == SetbackState.Pending && mid.helped && gone.pawn == null && gone.pawnId == 424242;
            RPLog.Message($"[RimPrison:test] saveload release preparation: {(whole ? "intact" : "changed")}, the release in flight {(inFlight ? "still on the shuttle's list with its case" : "lost its entry or its case")}");
            if (!whole) reloadProblems.Add($"the supervision book changed in the round trip: {rpSeedDescribed} -> {after}");
            if (waiting != null && mid != null && gone != null && caught != null && clean != null)
            {
                using (new Rollback(map))
                {
                    g.ledger.funds = 20000;
                    int funds0 = g.ledger.funds, rearrests0 = g.rearrests.Count, reoffended0 = g.stats.reoffended, started0 = g.stats.supervisionStarted, rearrestedCount0 = g.stats.supervisionRearrested, released0 = g.stats.released;
                    // The release in flight leaves: the shuttle's call (told twice), then the case told again - begun once, the release counted once, the lodging paid once.
                    if (waiting.state == CaseState.Awaiting && inFlight)
                    {
                        ShuttleSystem.Notify_Left(leaving);
                        ShuttleSystem.Notify_Left(leaving);
                        Supervision.Begin(g, waiting);
                        Supervision.Notify_Departed(g, waiting.id);
                        if (waiting.state != CaseState.Reviewing || funds0 - g.ledger.funds != Supervision.LodgingPrice || g.stats.supervisionStarted != started0 + 1 || g.stats.released != released0 + 1 || !waiting.supportFulfilled)
                            reloadProblems.Add($"release preparation: after the reload the release in flight left and began {waiting.state}, paid {funds0 - g.ledger.funds} (once, {Supervision.LodgingPrice}), started +{g.stats.supervisionStarted - started0}, released +{g.stats.released - released0} (once each)");
                    }
                    // (Gone already in the real ticks of the checks before this one: told, not failed - the snapshot above saw the entry come back.)
                    else if (waiting.state != CaseState.Awaiting) RPLog.Message($"[RimPrison:test] saveload release preparation: the release in flight had left before the check ({waiting.state}); its departure was not told again");
                    // The setback across the save: the review after it, the help given, a roll over the chance: one rearrest, however many times it is asked.
                    float trust0 = g.story.boardTrust;
                    Supervision.Review(g, mid, 0.95f);
                    Supervision.Review(g, mid, 0.95f);
                    Supervision.Review(g, mid, 0.95f);
                    Rearrest legacy = g.rearrests.FirstOrDefault(x => x.name == "Zrplegacy");
                    if (mid.outcome != CaseOutcome.Rearrested || mid.reason != "RP_Violation_JobLost" || g.rearrests.Count != rearrests0 + 1 || g.stats.reoffended != reoffended0 + 1 || g.stats.supervisionRearrested != rearrestedCount0 + 1 || g.rearrests.Count(x => x.name == "Zrpmid") != 1)
                        reloadProblems.Add($"release preparation: after the reload the setback ended {mid.state} {mid.outcome} for {mid.reason}, rearrests +{g.rearrests.Count - rearrests0} (once), reoffended +{g.stats.reoffended - reoffended0}");
                    if (legacy == null || legacy.reason != "RP_Violation_NoWork" || legacy.dueTick <= Find.TickManager.TicksGame) reloadProblems.Add("release preparation: after the reload the old queued rearrest was changed or lost");
                    // The pawn that came back as nobody: the review goes on (not voided).
                    Supervision.Review(g, gone, 0.9999f);
                    if (gone.state != CaseState.Reviewing || gone.outcome != CaseOutcome.None || gone.reviewsRolled != 3)
                        reloadProblems.Add($"release preparation: after the reload a case whose pawn the world had discarded was {gone.state} {gone.outcome} after its review ({gone.reviewsRolled} rolls; going on expected)");
                    Supervision.Review(g, caught, 0.0001f);
                    Supervision.Review(g, clean, 0.0001f);
                    if (g.rearrests.Count != rearrests0 + 1 || caught.reviewsHeld != 2 || clean.reviewsHeld != 7 || Mathf.Abs(g.story.boardTrust - trust0) > 0.0001f)
                        reloadProblems.Add("release preparation: after the reload a case that was already settled was reviewed again");
                    g.rearrests.RemoveAll(x => x.name == "Zrpmid" || x.name == "Zrplegacy");
                }
            }
            // The plan's seeds go: the cases, the register's entry, the plan, the counters, the pawns made for the cases.
            g.supervision.cases.RemoveAll(x => rpSeedIds.Contains(x.id));
            g.supervision.nextId = rpNextId0;
            g.story.formerInmates.RemoveAll(x => x.name == "Zrpmid");
            g.rearrests.RemoveAll(x => x.name == "Zrplegacy" || x.name == "Zrpmid");
            if (rpPlanInmateId >= 0 && map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.thingIDNumber == rpPlanInmateId)?.Inmate() is { } planned) planned.releasePlan = rpPlanWas;
            (g.stats.supervisionStarted, g.stats.supervisionStayedOut, g.stats.supervisionRearrested, g.stats.supervisionSetbacks, g.stats.supervisionHelped, g.stats.supervisionLodged, g.stats.supervisionInterviews) =
                (rpStatsBefore[0], rpStatsBefore[1], rpStatsBefore[2], rpStatsBefore[3], rpStatsBefore[4], rpStatsBefore[5], rpStatsBefore[6]);
            Pawn madeUp = leaving ?? map.mapPawns.AllPawns.FirstOrDefault(x => x.thingIDNumber == rpLeavingId) ?? Find.WorldPawns.AllPawnsAliveOrDead.FirstOrDefault(x => x.thingIDNumber == rpLeavingId);
            if (madeUp != null)
            {
                g.shuttle.Forget(madeUp);
                Find.WorldPawns.ForcefullyKeptPawns.Remove(madeUp);
                if (Find.WorldPawns.Contains(madeUp)) Find.WorldPawns.RemovePawn(madeUp);
                if (!madeUp.Destroyed) madeUp.Destroy();
            }
            Pawn seeded = rpWorldPawn != null ? Find.WorldPawns.AllPawnsAliveOrDead.FirstOrDefault(x => x.thingIDNumber == rpWorldPawn.thingIDNumber) : null;
            if (seeded != null)
            {
                Find.WorldPawns.ForcefullyKeptPawns.Remove(seeded);
                if (Find.WorldPawns.Contains(seeded)) Find.WorldPawns.RemoveAndDiscardPawnViaGC(seeded);
            }
            rpWorldPawn = null;
            rpLeavingId = -1;
            rpSeeded = false;
        }
    }
}
