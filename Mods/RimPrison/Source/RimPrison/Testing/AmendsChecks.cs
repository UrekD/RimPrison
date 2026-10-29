using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    /// <summary>
    /// A restorative meeting with the victim's family (<see cref="Amends"/>, gap analysis 7, item 8). The headless proof: two equal throwaway inmates inside for a violent crime with a hearing that has the
    /// family's letter, a good meeting forced for one - their odds at the board are higher by exactly <see cref="ParoleScene.VictimAgainst"/> and the reasons drop the family, the control's are unchanged; a bad
    /// one adds tension once. Then the family's support, the reoffend band's step, the proposal (who may have one, one open per inmate, the letter, approve and decline, the fee, the retry), what the hourly
    /// pass keeps in step (a lapse refunded), the meeting through the visitation system (the family is no relative, smuggles nothing and is searched; a visit that ends without the talk waits for the next;
    /// the talk's end is the outcome), the odds' parts, once per stay, the goal, the file and the keys. The saveload plan seeds the state (<see cref="SeedAmendsForReload"/>). Self-contained, run last in the
    /// events plan; checks that need time to pass move the clock forward (never below tick 0) and the rollback moves it back. Written without a game run.
    /// </summary>
    public static partial class AutoTest
    {
        private static void AmendsFail(string check, string what) => problems0.Add($"amends ({check}): {what}");

        private static readonly string[] amendsKeys =
        {
            "RP_AmendsLabel", "RP_AmendsText", "RP_AmendsWhy_Programs", "RP_AmendsWhy_Want", "RP_AmendsOdds_Tension", "RP_AmendsOdds_Counsellor", "RP_AmendsOdds_NoCounsellor", "RP_AmendsOdds_Programs",
            "RP_AmendsOdds_Want", "RP_AmendsOdds_Searched", "RP_Amends_Approve", "RP_Amends_Decline", "RP_AmendsNote", "RP_AmendsRefundNote", "RP_AmendsApproved", "RP_AmendsNoLonger", "RP_AmendsLapsed",
            "RP_AmendsVisitorArrived", "RP_AmendsSearchedPart", "RP_AmendsLeftRefunded", "RP_AmendsWith", "RP_AmendsWithNobody", "RP_AmendsGoodLabel", "RP_AmendsWithdrawnText", "RP_AmendsSupportsText", "RP_AmendsBadLabel", "RP_AmendsHostileText",
            "RP_AmendsPress", "RP_AmendsNoPress", "RP_Chr_AmendsWithdrawn", "RP_Chr_AmendsSupports", "RP_Chr_AmendsHostile", "RP_Chr_AmendsHostilePress", "RP_Cause_AmendsWentBadly",
            "RP_ParoleAgainst_VictimWithdrawn", "RP_ParoleFor_Victim", "RP_Want_Reason_AmendsDeclined", "RP_Want_Reason_AmendsRefused",
        };

        private static void CheckAmends(Map map)
        {
            CheckAmendsHearing(map);
            CheckAmendsSupport(map);
            CheckAmendsBand(map);
            CheckAmendsBad(map);
            CheckAmendsProposal(map);
            CheckAmendsKeptInStep(map);
            CheckAmendsMeeting(map);
            CheckAmendsOdds(map);
            CheckAmendsOncePerStay(map);
            CheckAmendsWant(map);
            CheckAmendsWords(map);
            // Round 2 (the review's items).
            CheckAmendsVisitorDeath(map);
            CheckAmendsLeaving(map);
            CheckAmendsAssessedCase(map);
            CheckAmendsTable(map);
            CheckAmendsNowhere(map);
            CheckAmendsLetterStates(map);
            CheckAmendsHourly(map);
        }

        private static CrimeDef AmendsCrime(bool violent) => DefDatabase<CrimeDef>.AllDefsListForReading.FirstOrDefault(d => d.violent == violent);

        /// <summary>
        /// A throwaway inmate with a plain file (a forty-day sentence from now, so no hearing is near; no incidents, speakers, goal or gang; none of the traits the odds read), inside for a violent crime
        /// (or not), with programs passed and tension given. The live inmates' meetings are kept by the rollback too (an hourly pass in a check reaches them).
        /// </summary>
        private static Pawn AmendsInmate(Map map, Rollback r, bool violent = true, int programs = 3, float tension = 40f)
        {
            Pawn p = Throwaway(map, r);
            CompInmate c = p.Inmate();
            c.RefreshActiveFlag();
            (c.processed, c.awaitingSearch, c.want, c.records) = (true, false, null, new List<MisconductRecord>());
            (c.sentenceDays, c.daysAdded, c.admittedTick, c.lastHearingTick, c.programsPassed, c.hoursWorked, c.tension) = (40, 0, Find.TickManager.TicksGame, -999999, programs, 0, tension);
            (c.paroleDenial, c.paroleDenialTick, c.crime, c.jobWaiting, c.familyCount, c.mentorId, c.religious, c.innocent) = (null, -1, AmendsCrime(violent), false, 0, -1, false, false);
            (c.relatives, c.staffMemories, c.gang, c.forcedActivity, c.visitBanUntilTick, c.headsUpTick) = (new List<Relative>(), new List<StaffMemory>(), null, null, -1, -1);
            (c.amendsState, c.amendsTick) = (AmendsState.None, -1);
            c.reputations.Clear();
            c.passedPrograms.Clear();
            c.security = InmateUtility.DefaultSecurity;
            if (p.story?.traits is { } traits)
                foreach (Trait t in traits.allTraits.Where(t => t.def == TraitDefOf.Kind || t.def == TraitDefOf.Psychopath || t.def == TraitDefOf.Bloodlust || t.def == TraitDefOf.Abrasive).ToList()) traits.RemoveTrait(t);
            return p;
        }

        /// <summary>Every live inmate's meeting put back by the rollback, and the forced dice cleared.</summary>
        private static void AmendsKeep(Map map, Rollback r)
        {
            var kept = map.mapPawns.PrisonersOfColony.Where(x => x.Inmate() != null).Select(x => (c: x.Inmate(), state: x.Inmate().amendsState, tick: x.Inmate().amendsTick)).ToList();
            r.Add(() =>
            {
                foreach ((CompInmate c, AmendsState state, int tick) in kept) (c.amendsState, c.amendsTick) = (state, tick);
                (Amends.ForceOutcome, Amends.ForcePress, Amends.NoRoomForChecks) = (null, null, false);
            });
        }

        /// <summary>The clock moved forward to <paramref name="tick"/> (never below 0, never back), and back by the rollback.</summary>
        private static void AmendsClock(Rollback r, int tick)
        {
            int clock0 = Find.TickManager.TicksGame;
            r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
            Find.TickManager.DebugSetTicksGame(System.Math.Max(clock0, System.Math.Max(0, tick)));
        }

        private static ParoleCase AmendsProbe(Map map, Pawn p, bool letter = true)
        {
            var probe = new ParoleCase { pawnId = p.thingIDNumber, name = p.LabelShort, victimLetter = letter };
            ParoleScene.Assess(map, p, p.Inmate(), probe);
            return probe;
        }

        /// <summary>The meeting concluded with a forced outcome (the family's relative at the table is not needed for the outcome).</summary>
        private static bool AmendsForce(Pawn p, AmendsState outcome, bool press = false, Pawn counsellor = null)
        {
            CompInmate c = p.Inmate();
            c.amendsState = AmendsState.Meeting;
            (Amends.ForceOutcome, Amends.ForcePress) = (outcome, press);
            try { return Amends.Conclude(p, c, counsellor); }
            finally { (Amends.ForceOutcome, Amends.ForcePress) = (null, null); }
        }

        private static void CheckAmendsHearing(Map map)
        {
            Guarded("amends: a good meeting withdraws the family's letter, the control's hearing is unchanged", map, r =>
            {
                AmendsKeep(map, r);
                if (AmendsCrime(true) == null) { AmendsFail("hearing", "the game has no violent crime to try"); return; }
                PrisonGame g = PrisonGame.Instance;
                Pawn a = AmendsInmate(map, r), b = AmendsInmate(map, r);
                CompInmate ca = a.Inmate(), cb = b.Inmate();
                ParoleCase a0 = AmendsProbe(map, a), b0 = AmendsProbe(map, b);
                if (Mathf.Abs(a0.chance - b0.chance) > 0.0001f || ParoleScene.BandOf(a0.risk) != "RP_RiskLow")
                {
                    AmendsFail("hearing", $"premise: the two equal inmates read {a0.chance:F3} and {b0.chance:F3} at the board (risk {a0.risk:F2}, expected equal odds and the low band), for [{a0.forText}] / [{b0.forText}]");
                    return;
                }
                // A hearing already queued for the one who meets the family (not yet sat, so not assessed), with the family's letter: the meeting withdraws it there too. (One already
                // assessed is left as the board saw it: CheckAmendsAssessedCase.)
                var queued = new ParoleCase { pawnId = a.thingIDNumber, name = a.LabelShort, scheduledTick = Find.TickManager.TicksGame, victimLetter = true };
                g.story.paroleCases.Add(queued);
                r.Add(() => g.story.paroleCases.Remove(queued));
                float tension0 = ca.tension;
                bool concluded = AmendsForce(a, AmendsState.Withdrawn);
                if (!concluded || ca.amendsState != AmendsState.Withdrawn || Mathf.Abs(ca.tension - (tension0 - Amends.GoodTension)) > 0.001f || !queued.victimWithdrawn)
                    AmendsFail("hearing", $"a good meeting: concluded {concluded}, state {ca.amendsState}, tension {tension0:F0} -> {ca.tension:F0} (expected -{Amends.GoodTension:F0}), the queued hearing's letter withdrawn {queued.victimWithdrawn}");
                // (The tension's own effect on the odds is not this check's: equal again.)
                ca.tension = cb.tension;
                ParoleCase a1 = AmendsProbe(map, a), b1 = AmendsProbe(map, b);
                string why = "RP_ParoleWhy_Victim".Translate();
                float gain = a1.chance - b1.chance;
                if (Mathf.Abs(gain - ParoleScene.VictimAgainst) > 0.0001f || !a1.victimWithdrawn || b1.victimWithdrawn || a1.reasons.Contains(why) || !b1.reasons.Contains(why) || Mathf.Abs(b1.chance - b0.chance) > 0.0001f)
                    AmendsFail("hearing", $"after the meeting the board reads {a1.chance:F3} for them and {b1.chance:F3} for the control (gain {gain:F3}, expected {ParoleScene.VictimAgainst:F2}; the control was {b0.chance:F3}); withdrawn {a1.victimWithdrawn}/{b1.victimWithdrawn}; reasons [{a1.reasons}] / [{b1.reasons}]");
                if (!a1.againstText.Contains("RP_ParoleAgainst_VictimWithdrawn".Translate()) || !b1.againstText.Contains("RP_ParoleAgainst_Victim".Translate(AmendsSigned(-ParoleScene.VictimAgainst))))
                    AmendsFail("hearing", $"the letters' against lines - [{a1.againstText}] (expected the withdrawn letter) and [{b1.againstText}] (expected the family's letter)");
                RPLog.Message($"[RimPrison:test] amends: hearing odds {b0.chance:P1} for both, then {a1.chance:P1} after the meeting against the control's {b1.chance:P1}; reasons [{a1.reasons}] / [{b1.reasons}]");
            });
        }

        private static string AmendsSigned(float delta) => (delta < 0f ? "-" : "+") + Mathf.Abs(Mathf.RoundToInt(delta * 100f)) + "%";

        private static void CheckAmendsSupport(Map map)
        {
            Guarded("amends: a family that turns to support speaks for them at the stated weight", map, r =>
            {
                AmendsKeep(map, r);
                Pawn a = AmendsInmate(map, r), b = AmendsInmate(map, r);
                AmendsForce(a, AmendsState.Supports);
                a.Inmate().tension = b.Inmate().tension;
                ParoleCase a1 = AmendsProbe(map, a), b1 = AmendsProbe(map, b), a2 = AmendsProbe(map, a, letter: false), b2 = AmendsProbe(map, b, letter: false);
                string line = "RP_ParoleFor_Victim".Translate(AmendsSigned(ParoleScene.VictimFor));
                if (Mathf.Abs(a1.chance - b1.chance - (ParoleScene.VictimAgainst + ParoleScene.VictimFor)) > 0.0001f || Mathf.Abs(a2.chance - b2.chance - ParoleScene.VictimFor) > 0.0001f || !a1.forText.Contains(line) || !a2.forText.Contains(line))
                    AmendsFail("support", $"with the family's letter {a1.chance:F3} against {b1.chance:F3} (expected +{ParoleScene.VictimAgainst + ParoleScene.VictimFor:F2}), without it {a2.chance:F3} against {b2.chance:F3} (expected +{ParoleScene.VictimFor:F2}); for [{a1.forText}]");
                RPLog.Message($"[RimPrison:test] amends: a supporting family - {a1.chance:P0} against {b1.chance:P0} with the letter, {a2.chance:P0} against {b2.chance:P0} without; for [{a1.forText}]");
            });
        }

        private static void CheckAmendsBand(Map map)
        {
            Guarded("amends: the reoffend band drops a step", map, r =>
            {
                AmendsKeep(map, r);
                var seen = new List<string>();
                foreach ((int programs, string from, string to) in new[] { (0, "RP_RiskHigh", "RP_RiskMedium"), (1, "RP_RiskMedium", "RP_RiskLow"), (3, "RP_RiskLow", "RP_RiskLow") })
                {
                    Pawn p = AmendsInmate(map, r, programs: programs);
                    CompInmate c = p.Inmate();
                    float before = ParoleSystem.ReoffendChance(c);
                    c.amendsState = AmendsState.Withdrawn;
                    float after = ParoleSystem.ReoffendChance(c);
                    c.amendsState = AmendsState.Supports;
                    float supports = ParoleSystem.ReoffendChance(c);
                    c.amendsState = AmendsState.Hostile;
                    float hostile = ParoleSystem.ReoffendChance(c);
                    if (ParoleScene.BandOf(before) != from || ParoleScene.BandOf(after) != to || after > before || supports != after || hostile != before)
                        AmendsFail("band", $"{programs} program(s): risk {before:F2} ({ParoleScene.BandOf(before)}, expected {from}) -> {after:F2} ({ParoleScene.BandOf(after)}, expected {to}); supporting {supports:F2}, hostile {hostile:F2} (expected the step, and none)");
                    seen.Add($"{before:P0}->{after:P0}");
                }
                RPLog.Message($"[RimPrison:test] amends: the reoffend band a step lower - {string.Join(", ", seen)}");
            });
        }

        private static void CheckAmendsBad(Map map)
        {
            Guarded("amends: a bad meeting adds tension once, the family writes, the press may hear", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                Pawn a = AmendsInmate(map, r, tension: 30f), b = AmendsInmate(map, r, tension: 30f);
                CompInmate ca = a.Inmate(), cb = b.Inmate();
                // (The goal forms only where a meeting could be held: a visitation room, the level visited across a table.)
                bool goal = Wants.Form(a, ca, RPAmendsDefOf.RP_Want_Amends) != null;
                if (!goal) RPLog.Message("[RimPrison:test] amends (bad): the goal does not form here (no visitation room, or the default level is not visited across a table) - its loss not checked");
                var queued = new ParoleCase { pawnId = a.thingIDNumber, name = a.LabelShort, scheduledTick = Find.TickManager.TicksGame, victimLetter = false };
                g.story.paroleCases.Add(queued);
                r.Add(() => g.story.paroleCases.Remove(queued));
                g.reputation = 50f;
                int entries = r.NewEntries.Count();
                bool first = AmendsForce(a, AmendsState.Hostile, press: true);
                float once = ca.tension, rep = g.reputation;
                // (Again, with the meeting over: nothing to conclude, no second spike.)
                bool second = Amends.Conclude(a, ca, null);
                string pressLine = "RP_Chr_AmendsHostilePress".Translate(a.LabelShort, "RP_AmendsWithNobody".Translate().ToString()).Resolve();
                bool chronicled = r.NewEntries.Skip(entries).Any(e => e.text == pressLine);
                if (!first || second || Mathf.Abs(once - (30f + Amends.BadTension)) > 0.001f || Mathf.Abs(ca.tension - once) > 0.001f || ca.amendsState != AmendsState.Hostile || Mathf.Abs(rep - (50f - Amends.PressHit)) > 0.001f || !chronicled)
                    AmendsFail("bad", $"concluded {first} then {second} (expected once), tension 30 -> {once:F0} -> {ca.tension:F0} (expected +{Amends.BadTension:F0} once), state {ca.amendsState}, reputation 50 -> {rep:F1} (expected -{Amends.PressHit:F0}), the chronicle's press line {chronicled}");
                if (goal && ca.want is not { status: WantStatus.Lost } || ca.mainCause != "RP_Cause_AmendsWentBadly" || !queued.victimLetter)
                    AmendsFail("bad", $"the goal {ca.want?.def?.defName}:{ca.want?.status} (expected lost), the cause {ca.mainCause}, the queued hearing's letter {queued.victimLetter} (expected written)");
                // A hostile family writes against them at a hearing that had no letter.
                ParoleCase probe = AmendsProbe(map, a, letter: false);
                if (!probe.victimLetter || !probe.reasons.Contains("RP_ParoleWhy_Victim".Translate()))
                    AmendsFail("bad", $"a hearing without the family's letter after a hostile meeting: letter {probe.victimLetter}, reasons [{probe.reasons}] (expected the family against them)");
                // Without the press: the reputation stands.
                AmendsForce(b, AmendsState.Hostile, press: false);
                if (Mathf.Abs(g.reputation - rep) > 0.001f || Mathf.Abs(cb.tension - (30f + Amends.BadTension)) > 0.001f)
                    AmendsFail("bad", $"a bad meeting the press did not hear of: reputation {rep:F1} -> {g.reputation:F1} (expected unchanged), tension {cb.tension:F0}");
                RPLog.Message($"[RimPrison:test] amends: a bad meeting - tension 30 -> {once:F0}, again {second}, reputation 50 -> {rep:F1}, the goal {ca.want?.status}");
            });
        }

        /// <summary>A counsellor who can work on site (one of the live staff, else one of the check's own), or null with the reason told.</summary>
        private static Pawn AmendsCounsellor(Map map, Rollback r, string check)
        {
            Pawn counsellor = Amends.Counsellor(map) ?? ThrowawayStaff(map, r, RPProgramDefOf.RP_Counsellor);
            if (ProgramSystem.CanTeach(counsellor, RPProgramDefOf.RP_Counsellor)) return counsellor;
            RPLog.Message($"[RimPrison:test] amends ({check}): no counsellor who can work (the counselling work is off for them), not checked");
            return null;
        }

        private static ChoiceLetter_PrisonDecision AmendsLetter(Rollback r, Pawn p) =>
            r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is(Amends.Decision) && l.pawns.Contains(p));

        private static void AmendsAnswer(ChoiceLetter_PrisonDecision letter, int option)
        {
            letter.answered = true;
            PrisonDecisions.Choose(letter, option);
            Find.LetterStack.RemoveLetter(letter);
        }

        private static void CheckAmendsProposal(Map map)
        {
            Guarded("amends: the counsellor proposes, the warden approves or declines", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                Pawn counsellor = AmendsCounsellor(map, r, "proposal");
                if (counsellor == null) return;
                if (g.VisitPolicyFor(InmateUtility.DefaultSecurity) != VisitPolicy.Contact)
                {
                    RPLog.Message($"[RimPrison:test] amends (proposal): the default level is visited by {g.VisitPolicyFor(InmateUtility.DefaultSecurity)}, not across a table - not checked");
                    return;
                }
                if (!Amends.HasRoom(map))
                {
                    RPLog.Message("[RimPrison:test] amends (proposal): the prison has no visitation room - not checked");
                    return;
                }
                Pawn ready = AmendsInmate(map, r, programs: 1), calm = AmendsInmate(map, r, violent: false), unready = AmendsInmate(map, r, programs: 0), hearing = AmendsInmate(map, r), declined = AmendsInmate(map, r);
                var hc = new ParoleCase { pawnId = hearing.thingIDNumber, name = hearing.LabelShort, scheduledTick = Find.TickManager.TicksGame };
                g.story.paroleCases.Add(hc);
                r.Add(() => g.story.paroleCases.Remove(hc));
                string Why(Pawn p) => Amends.Ineligible(g, p, p.Inmate()) ?? "eligible";
                (string ready0, string calm0, string unready0, string hearing0) = (Why(ready), Why(calm), Why(unready), Why(hearing));
                Wants.Form(unready, unready.Inmate(), RPAmendsDefOf.RP_Want_Amends);
                string unready1 = Why(unready);
                // A hearing due within a day (the sentence half served now) is the same as one queued.
                CompInmate cd = declined.Inmate();
                cd.sentenceDays = 0;
                string due = Why(declined);
                cd.sentenceDays = 40;
                if (ready0 != "eligible" || calm0 != "RP_AmendsNo_NotViolent" || unready0 != "RP_AmendsNo_NotReady" || unready1 != "eligible" || hearing0 != "RP_AmendsNo_Hearing" || due != "RP_AmendsNo_Hearing")
                    AmendsFail("proposal", $"who may have one - a program passed {ready0}, not violent {calm0}, no program {unready0} then with the goal {unready1}, a hearing queued {hearing0}, a hearing due {due} (expected eligible, NotViolent, NotReady, eligible, Hearing, Hearing)");
                // The letter, one open at a time.
                ChoiceLetter_PrisonDecision letter = Amends.Propose(g, map, ready, counsellor);
                CompInmate c = ready.Inmate();
                string text = letter.Text.Resolve();
                if (AmendsLetter(r, ready) != letter || c.amendsState != AmendsState.Proposed || !Amends.LetterOpen(ready) || Why(ready) != "RP_AmendsNo_Open" || !text.Contains(Money.Format(Amends.Fee)) || !text.Contains(counsellor.LabelShort))
                    AmendsFail("proposal", $"the letter on the stack {AmendsLetter(r, ready) == letter}, state {c.amendsState}, open {Amends.LetterOpen(ready)}, eligible again {Why(ready)}, the fee and the counsellor in its text {text.Contains(Money.Format(Amends.Fee))}/{text.Contains(counsellor.LabelShort)}");
                // Short of the fee, the approval is disabled; with it, approved and paid.
                int funds0 = g.ledger.funds;
                g.ledger.funds = Amends.Fee - 1;
                string disabled = letter.decision.Worker.Disabled(letter, 0);
                g.ledger.funds = System.Math.Max(0, funds0) + Amends.Fee;
                int before = g.ledger.funds, entries = r.NewLedgerEntries.Count();
                AmendsAnswer(letter, 0);
                LedgerEntry paid = r.NewLedgerEntries.Skip(entries).FirstOrDefault(e => e.category == RPDefOf.RP_Programs);
                if (disabled == null || c.amendsState != AmendsState.Approved || c.amendsTick != Find.TickManager.TicksGame || g.ledger.funds != before - Amends.Fee || paid == null || paid.amount != -Amends.Fee)
                    AmendsFail("proposal", $"approved - disabled short of the fee {disabled != null}, state {c.amendsState} at {c.amendsTick}, funds {before} -> {g.ledger.funds} (expected -{Amends.Fee}), the ledger line {paid?.amount} \"{paid?.note}\"");
                // Declined: not again for a few days, then again.
                ChoiceLetter_PrisonDecision second = Amends.Propose(g, map, declined, counsellor);
                AmendsAnswer(second, 1);
                string lately = Why(declined);
                AmendsClock(r, Find.TickManager.TicksGame + Amends.RetryDays * GenDate.TicksPerDay + GenDate.TicksPerHour);
                string later = Why(declined);
                if (cd.amendsState != AmendsState.Declined || lately != "RP_AmendsNo_Lately" || later != "eligible")
                    AmendsFail("proposal", $"declined - state {cd.amendsState}, at once {lately}, {Amends.RetryDays} days later {later} (expected Lately, then eligible)");
                RPLog.Message($"[RimPrison:test] amends: proposal - {ready0}/{calm0}/{unready0}/{unready1}/{hearing0}/{due}, approved {c.amendsState}, declined {lately} then {later}");
            });
        }

        private static void CheckAmendsKeptInStep(Map map)
        {
            Guarded("amends: the hourly pass keeps the meetings in step", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                Pawn approved = AmendsInmate(map, r), orphan = AmendsInmate(map, r), left = AmendsInmate(map, r);
                int now0 = Find.TickManager.TicksGame;
                (approved.Inmate().amendsState, approved.Inmate().amendsTick) = (AmendsState.Approved, now0);
                (orphan.Inmate().amendsState, orphan.Inmate().amendsTick) = (AmendsState.Proposed, now0);
                (left.Inmate().amendsState, left.Inmate().amendsTick) = (AmendsState.Meeting, now0);
                // Within the days: nothing lapses; a proposal without its letter and a meeting without its family are put right.
                Amends.HourlyFor(g, approved, approved.Inmate(), Find.TickManager.TicksGame);
                Amends.HourlyFor(g, orphan, orphan.Inmate(), Find.TickManager.TicksGame);
                Amends.HourlyFor(g, left, left.Inmate(), Find.TickManager.TicksGame);
                AmendsState early = approved.Inmate().amendsState;
                AmendsClock(r, now0 + Amends.ApprovedDays * GenDate.TicksPerDay + GenDate.TicksPerHour);
                int funds = g.ledger.funds, entries = r.NewLedgerEntries.Count();
                Amends.HourlyFor(g, approved, approved.Inmate(), Find.TickManager.TicksGame);
                LedgerEntry refund = r.NewLedgerEntries.Skip(entries).FirstOrDefault(e => e.category == RPDefOf.RP_Programs);
                if (early != AmendsState.Approved || orphan.Inmate().amendsState != AmendsState.None || left.Inmate().amendsState != AmendsState.Approved
                    || approved.Inmate().amendsState != AmendsState.Lapsed || g.ledger.funds != funds + Amends.Fee || refund?.amount != Amends.Fee)
                    AmendsFail("kept in step", $"approved within the days {early}, after {Amends.ApprovedDays} days {approved.Inmate().amendsState} (refund {refund?.amount}, funds {funds} -> {g.ledger.funds}); a proposal without its letter {orphan.Inmate().amendsState}; a meeting without its family {left.Inmate().amendsState}");
                RPLog.Message($"[RimPrison:test] amends: kept in step - lapsed {approved.Inmate().amendsState} refunded {refund?.amount}, orphan {orphan.Inmate().amendsState}, left {left.Inmate().amendsState}");
            });
        }

        private static void CheckAmendsMeeting(Map map)
        {
            Guarded("amends: the family comes through the visitation room", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                if (pm == null || !pm.zones.Any(RPVisitDefOf.RP_Visitation)) { RPLog.Message("[RimPrison:test] amends (meeting): the prison has no visitation room, not checked"); return; }
                Pawn counsellor = AmendsCounsellor(map, r, "meeting");
                if (counsellor == null) return;
                Pawn p = AmendsInmate(map, r);
                CompInmate c = p.Inmate();
                (c.amendsState, c.amendsTick) = (AmendsState.Approved, Find.TickManager.TicksGame);
                var visitors = new List<Pawn>();
                r.Add(() =>
                {
                    foreach (Pawn v in visitors)
                        if (!v.Destroyed) v.Destroy(DestroyMode.Vanish);
                });
                if (!Amends.CallFamily(map, p, c) || c.visitor == null) { RPLog.Message("[RimPrison:test] amends (meeting): no way in or no spot in the visitation room for the family, not checked"); return; }
                Pawn family = c.visitor;
                visitors.Add(family);
                bool searched = VisitorSearch.PolicyFor(c, g) != VisitorSearchPolicy.None;
                if (c.amendsState != AmendsState.Meeting || c.visitorRelative != null || c.visitorFormer || c.visitorContraband != null || c.visitorSearchPending != searched || c.boothVisit || VisitationSystem.InmateOf(family) != p)
                    AmendsFail("meeting", $"the family called - state {c.amendsState}, relative {c.visitorRelative ?? "none"}, former {c.visitorFormer}, contraband {c.visitorContraband?.defName ?? "none"}, search pending {c.visitorSearchPending} (expected {searched}), booth {c.boothVisit}, the lord's inmate {VisitationSystem.InmateOf(family)?.LabelShort}");
                // In the room: the counsellor's work finds the family; the inmate waits for the counsellor, an hour at the most.
                c.visitorArrivedTick = Find.TickManager.TicksGame;
                Amends.Notify_FamilyArrived(p, c);
                bool listed = Amends.FamiliesWaiting(map).Contains(family);
                bool waits = !Amends.ReadyToTalk(p, c) || Amends.AtTheTable(p, family) != null;
                AmendsClock(r, Find.TickManager.TicksGame + Amends.CounsellorWaitTicks);
                bool goesAhead = Amends.ReadyToTalk(p, c);
                if (!listed || !waits || !goesAhead)
                    AmendsFail("meeting", $"in the room - listed for the counsellor {listed}, the inmate waits for the counsellor {waits}, goes ahead after an hour {goesAhead}");
                // The family leaves without the talk: the meeting waits for the next visiting hours.
                VisitationSystem.EndVisit(c, family);
                AmendsState afterLeaving = c.amendsState;
                if (afterLeaving != AmendsState.Approved || c.visitor != null) AmendsFail("meeting", $"a family that left without the talk - state {afterLeaving} (expected Approved), visitor {c.visitor?.LabelShort ?? "none"}");
                // Again, and the talk is held: the outcome, and nothing of a family visit.
                if (!Amends.CallFamily(map, p, c) || c.visitor == null) { AmendsFail("meeting", "the family could not be called a second time"); return; }
                Pawn again = c.visitor;
                visitors.Add(again);
                Need need = p.needs.TryGetNeed(RPVisitDefOf.RP_Family);
                float need0 = need?.CurLevel ?? -1f;
                int lastVisit = c.lastVisitTick, cash = c.cash;
                Amends.ForceOutcome = AmendsState.Supports;
                try { VisitationSystem.Complete(p, again); }
                finally { Amends.ForceOutcome = null; }
                if (c.amendsState != AmendsState.Supports || c.visitor != null || (need?.CurLevel ?? -1f) != need0 || c.lastVisitTick != lastVisit || c.cash != cash)
                    AmendsFail("meeting", $"the talk held - state {c.amendsState} (expected Supports), visitor {c.visitor?.LabelShort ?? "none"}, family need {need0:F2} -> {need?.CurLevel:F2}, last family visit {lastVisit} -> {c.lastVisitTick}, cash {cash} -> {c.cash} (expected none of a family visit)");
                RPLog.Message($"[RimPrison:test] amends: meeting - {family.LabelShort} came (searched {searched}), left without the talk ({afterLeaving}); {again.LabelShort} came again, outcome {c.amendsState}; counsellor {counsellor.LabelShort}");
            });
        }

        private static void CheckAmendsOdds(Map map)
        {
            Guarded("amends: the odds and their parts", map, r =>
            {
                AmendsKeep(map, r);
                Pawn counsellor = AmendsCounsellor(map, r, "odds");
                Pawn p = AmendsInmate(map, r, programs: 0, tension: 50f);
                CompInmate c = p.Inmate();
                int social = counsellor?.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 0;
                var parts = new List<string>();
                float plain = Amends.Odds(p, c, null, parts);
                c.tension = 0f;
                float calm = Amends.Odds(p, c, null);
                c.tension = 50f;
                float with = counsellor != null ? Amends.Odds(p, c, counsellor) : -1f;
                c.programsPassed = 2;
                float two = Amends.Odds(p, c, null);
                c.programsPassed = 5;
                float five = Amends.Odds(p, c, null);
                c.programsPassed = 0;
                float searched = Amends.Odds(p, c, null, stripSearched: true);
                bool goal = Wants.Form(p, c, RPAmendsDefOf.RP_Want_Amends) != null;
                if (!goal) RPLog.Message("[RimPrison:test] amends (odds): the goal does not form here (no visitation room, or the default level is not visited across a table) - its part not checked");
                float wanting = Amends.Odds(p, c, null);
                bool Near(float x, float y) => Mathf.Abs(x - y) < 0.0001f;
                if (!Near(plain, Amends.BaseOdds - Amends.NoCounsellor) || parts.Count != 2 || !Near(calm - plain, Amends.TensionSwing) || counsellor != null && !Near(with - plain, Amends.SocialPerLevel * social + Amends.NoCounsellor)
                    || !Near(two - plain, 2 * Amends.PerProgram) || !Near(five - plain, Amends.ProgramCap) || !Near(plain - searched, Amends.StripSearched) || goal && !Near(wanting - plain, Amends.WantBonus))
                    AmendsFail("odds", $"plain {plain:F3} ({string.Join("; ", parts)}), calm {calm:F3}, with {counsellor?.LabelShort} (Social {social}) {with:F3}, two programs {two:F3}, five {five:F3}, searched {searched:F3}, with the goal {wanting:F3}");
                RPLog.Message($"[RimPrison:test] amends: odds - plain {plain:P0}, calm {calm:P0}, with the counsellor {with:P0}, programs {two:P0}/{five:P0}, searched {searched:P0}, the goal {wanting:P0}");
            });
        }

        private static void CheckAmendsOncePerStay(Map map)
        {
            Guarded("amends: once per stay", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                Pawn p = AmendsInmate(map, r);
                CompInmate c = p.Inmate();
                var seen = new List<string>();
                foreach (AmendsState outcome in new[] { AmendsState.Withdrawn, AmendsState.Supports, AmendsState.Hostile })
                {
                    (c.amendsState, c.amendsTick) = (outcome, Find.TickManager.TicksGame);
                    string why = Amends.Ineligible(g, p, c);
                    bool form = RPAmendsDefOf.RP_Want_Amends.Worker.CanForm(p, c);
                    if (why != "RP_AmendsNo_Held" || form) AmendsFail("once", $"after a {outcome} meeting - {why ?? "eligible"} (expected Held), the goal may form {form}");
                    seen.Add($"{outcome}={why}");
                }
                c.ResetForNewStay();
                if (c.amendsState != AmendsState.None || c.amendsTick != -1) AmendsFail("once", $"a new stay - state {c.amendsState} at {c.amendsTick} (expected None at -1)");
                RPLog.Message($"[RimPrison:test] amends: once per stay - {string.Join(", ", seen)}, a new stay {c.amendsState}@{c.amendsTick}");
            });
        }

        private static void CheckAmendsWant(Map map)
        {
            Guarded("amends: the goal to make amends", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                InmateWantDef def = RPAmendsDefOf.RP_Want_Amends;
                Pawn good = AmendsInmate(map, r), bad = AmendsInmate(map, r), turned = AmendsInmate(map, r), calm = AmendsInmate(map, r, violent: false);
                if (!Amends.CouldMeet(good, good.Inmate()))
                {
                    RPLog.Message("[RimPrison:test] amends (want): no meeting could be held here (no visitation room, or the default level is not visited across a table) - the goal not checked");
                    return;
                }
                bool calmForms = def.Worker.CanForm(calm, calm.Inmate());
                foreach (Pawn p in new[] { good, bad, turned }) Wants.Form(p, p.Inmate(), def);
                if (calmForms || new[] { good, bad, turned }.Any(p => p.Inmate().want?.def != def))
                {
                    AmendsFail("want", $"a non-violent inmate may want it {calmForms}; formed for the violent - {string.Join(", ", new[] { good, bad, turned }.Select(p => p.Inmate().want?.def?.defName ?? "none"))}");
                    return;
                }
                AmendsForce(good, AmendsState.Withdrawn);
                AmendsForce(bad, AmendsState.Hostile);
                // Turned down by the warden: a setback, tried once more.
                CompInmate ct = turned.Inmate();
                (ct.amendsState, ct.amendsTick) = (AmendsState.Proposed, Find.TickManager.TicksGame);
                Amends.Decline(turned, ct);
                if (good.Inmate().want.status != WantStatus.Fulfilled || bad.Inmate().want.status != WantStatus.Lost || ct.amendsState != AmendsState.Declined || ct.want.status != WantStatus.OnTrack || ct.want.renewals != 1)
                    AmendsFail("want", $"a good meeting {good.Inmate().want.status} (expected Fulfilled), a bad one {bad.Inmate().want.status} (expected Lost), turned down {ct.amendsState}, the goal {ct.want.status} with {ct.want.renewals} setback(s) (expected Declined, OnTrack, 1)");
                TaggedString detail = def.Worker.Detail(turned, ct, ct.want);
                if (detail.NullOrEmpty()) AmendsFail("want", $"the goal's line in the file is empty for state {ct.amendsState}");
                RPLog.Message($"[RimPrison:test] amends: the goal - good {good.Inmate().want.status}, bad {bad.Inmate().want.status}, turned down {ct.want.status} ({detail})");
            });
        }

        private static void CheckAmendsWords(Map map)
        {
            Guarded("amends: the file's lines and the keys", map, r =>
            {
                AmendsKeep(map, r);
                var missing = amendsKeys.Where(k => !k.CanTranslate()).ToList();
                foreach (AmendsState s in System.Enum.GetValues(typeof(AmendsState)))
                {
                    if (!("RP_Want_Amends_Detail_" + s).CanTranslate()) missing.Add("RP_Want_Amends_Detail_" + s);
                    if (s != AmendsState.None && !("RP_FileAmends_" + s).CanTranslate()) missing.Add("RP_FileAmends_" + s);
                }
                foreach (string why in new[] { "Away", "NotViolent", "Held", "Open", "Lately", "NotReady", "NoTable", "NoRoom", "Banned", "Remand", "Hearing" })
                    if (!("RP_AmendsNo_" + why).CanTranslate()) missing.Add("RP_AmendsNo_" + why);
                if (missing.Count > 0) AmendsFail("words", $"keys with no text - {string.Join(", ", missing)}");
                Pawn p = AmendsInmate(map, r);
                CompInmate c = p.Inmate();
                var lines = new List<string>();
                foreach (AmendsState s in System.Enum.GetValues(typeof(AmendsState)))
                {
                    (c.amendsState, c.amendsTick) = (s, Find.TickManager.TicksGame);
                    (string, Color)? line = Amends.FileLine(c);
                    if (line.HasValue != (s != AmendsState.None)) AmendsFail("words", $"the file's line for {s} - {line?.Item1 ?? "none"}");
                    if (line.HasValue) lines.Add(line.Value.Item1);
                }
                if (lines.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, ": [A-Za-z]"))) AmendsFail("words", "a file line has a colon before a word (the game would capitalise it)");
                // The goal's line before anything is proposed names the fee as money.
                (c.amendsState, c.amendsTick) = (AmendsState.None, -1);
                string detail = RPAmendsDefOf.RP_Want_Amends.Worker.Detail(p, c, null).Resolve();
                if (!detail.Contains(Money.Format(Amends.Fee))) AmendsFail("words", $"the goal's line with nothing proposed - \"{detail}\" (expected the fee {Money.Format(Amends.Fee)})");
                RPLog.Message($"[RimPrison:test] amends: {amendsKeys.Length} keys, file lines - {string.Join(" | ", lines)}");
            });
        }

        // ---- round 2: the review's items ------------------------------------------------------------------------------------

        /// <summary>Visitors the check called, destroyed by the rollback (registered after the throwaway inmates, so they go first).</summary>
        private static List<Pawn> AmendsVisitors(Rollback r)
        {
            var visitors = new List<Pawn>();
            r.Add(() =>
            {
                foreach (Pawn v in visitors)
                    if (!v.Destroyed) v.Destroy(DestroyMode.Vanish);
            });
            return visitors;
        }

        private static int AmendsRefunds(Rollback r, int from) => r.NewLedgerEntries.Skip(from).Count(e => e.category == RPDefOf.RP_Programs && e.amount == Amends.Fee);

        private static void CheckAmendsVisitorDeath(Map map)
        {
            Guarded("amends: the victim's relative who dies after the meeting is no family of the inmate's", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                if (!Amends.HasRoom(map)) { RPLog.Message("[RimPrison:test] amends (visitor death): the prison has no visitation room, not checked"); return; }
                Pawn p = AmendsInmate(map, r);
                CompInmate c = p.Inmate();
                // (A relative of their own, who could be taken for the dead one.)
                Relative kin = Relatives.Make();
                c.relatives.Add(kin);
                c.familyCount = 1;
                List<Pawn> visitors = AmendsVisitors(r);
                bool Grieves() => p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPLifeDefOf.RP_FamilyDied) != null;
                // The victim's relative dies with the visit over: nothing of a death in the inmate's family (no tension, thought, letter or chronicle line, no relative marked dead).
                void DiesWalkingOut(Pawn v, string when)
                {
                    float tension0 = c.tension;
                    int letters0 = r.NewLetters.Count(), entries0 = r.NewEntries.Count();
                    bool mourned = VisitorDeaths.Notify_Died(v);
                    if (mourned || Mathf.Abs(c.tension - tension0) > 0.001f || Grieves() || r.NewLetters.Count() != letters0 || r.NewEntries.Count() != entries0 || !kin.alive)
                        AmendsFail("visitor death", $"the victim's relative dies walking out {when} - taken for family {mourned}, tension {tension0:F0} -> {c.tension:F0}, grief {Grieves()}, letters +{r.NewLetters.Count() - letters0}, chronicle +{r.NewEntries.Count() - entries0}, {kin.Label} alive {kin.alive} (expected none of a death in the family)");
                }
                (c.amendsState, c.amendsTick) = (AmendsState.Approved, Find.TickManager.TicksGame);
                if (!Amends.CallFamily(map, p, c) || c.visitor == null) { RPLog.Message("[RimPrison:test] amends (visitor death): no way in or no spot in the visitation room for the family, not checked"); return; }
                Pawn left = c.visitor;
                visitors.Add(left);
                // The meeting ends without the talk (the family harmed, or the lord's time up): the visit is cleared and the inmate's state is back to approved, but the lord still knows whom they came for.
                VisitationSystem.EndVisit(c, left);
                if (c.visitor != null || c.amendsState != AmendsState.Approved || VisitationSystem.InmateOf(left) != p || VisitationSystem.VisitOf(left) is not { ForAmends: true })
                {
                    AmendsFail("visitor death", $"premise: after the visit ended - visitor {c.visitor?.LabelShort ?? "none"}, state {c.amendsState} (expected Approved), the lord's inmate {VisitationSystem.InmateOf(left)?.LabelShort ?? "none"}, the visit marked for amends {VisitationSystem.VisitOf(left)?.ForAmends}");
                    return;
                }
                DiesWalkingOut(left, "after a meeting that was not held");
                // Again after a meeting that was held (the outcome in, the family walking out).
                if (!Amends.CallFamily(map, p, c) || c.visitor == null) { AmendsFail("visitor death", "the family could not be called a second time"); return; }
                Pawn met = c.visitor;
                visitors.Add(met);
                Amends.ForceOutcome = AmendsState.Withdrawn;
                try { VisitationSystem.Complete(p, met); }
                finally { Amends.ForceOutcome = null; }
                if (c.amendsState != AmendsState.Withdrawn || c.visitor != null) { AmendsFail("visitor death", $"premise: the meeting held - state {c.amendsState} (expected Withdrawn), visitor {c.visitor?.LabelShort ?? "none"}"); return; }
                DiesWalkingOut(met, "after the meeting");
                // The control: a relative of their own who dies walking out is mourned (the path the victim's relative is kept off). A former inmate's kept entry keeps them off it too.
                if (!VisitationSystem.SpawnVisitor(map, p, c, booth: false) || c.visitor == null) { RPLog.Message("[RimPrison:test] amends (visitor death): the inmate's own family could not be called, the control not checked"); return; }
                Pawn own = c.visitor;
                visitors.Add(own);
                VisitationSystem.EndVisit(c, own);
                var entry = new FormerInmate { pawn = own, name = own.LabelShort };
                g.story.formerInmates.Add(entry);
                bool asFormer = VisitorDeaths.Notify_Died(own);
                g.story.formerInmates.Remove(entry);
                float before = c.tension;
                bool mournedOwn = VisitorDeaths.Notify_Died(own);
                if (asFormer || !mournedOwn || c.tension <= before || kin.alive)
                    AmendsFail("visitor death", $"the control - a former inmate walking out taken for family {asFormer} (expected not); their own relative mourned {mournedOwn}, tension {before:F0} -> {c.tension:F0}, {kin.Label} alive {kin.alive} (expected mourned, marked dead)");
                RPLog.Message($"[RimPrison:test] amends: visitor death - the victim's relative after the meeting is no family, their own relative {(mournedOwn ? "mourned" : "not mourned")}");
            });
        }

        private static void CheckAmendsLeaving(Map map)
        {
            Guarded("amends: leaving with a meeting approved refunds the fee, and a release's supervision case reads the step", map, r =>
            {
                AmendsKeep(map, r);
                RpSetUp(r);
                PrisonGame g = PrisonGame.Instance;
                int now = Find.TickManager.TicksGame;
                // Released with a meeting approved: the fee comes back before the stay is cleared.
                Pawn released = RpInmate(map, r, 2f);
                CompInmate cr = released.Inmate();
                cr.crime = AmendsCrime(true);
                (cr.amendsState, cr.amendsTick) = (AmendsState.Approved, now);
                int from = r.NewLedgerEntries.Count();
                ReleaseSystem.Release(g, released, record: false);
                int refundReleased = AmendsRefunds(r, from);
                if (refundReleased != 1 || cr.amendsState != AmendsState.None)
                    AmendsFail("leaving", $"released with a meeting approved - {refundReleased} refund(s) of {Money.Format(Amends.Fee)} (expected 1), state {cr.amendsState} (expected None, the stay cleared)");
                // Made amends and released under supervision: the case's baseline is the band's step, read before the stay is cleared.
                Pawn made = RpInmate(map, r, 2f);
                CompInmate cm = made.Inmate();
                cm.crime = AmendsCrime(true);
                cm.programsPassed = 1;
                float raw = ParoleSystem.ReoffendChance(cm);
                (cm.amendsState, cm.amendsTick) = (AmendsState.Withdrawn, now);
                float stepped = ParoleSystem.ReoffendChance(cm);
                if (Mathf.Abs(stepped - Amends.StepDown(raw)) > 0.0001f || stepped >= raw)
                    AmendsFail("leaving", $"premise: the reoffend chance {raw:F2} -> {stepped:F2} after the meeting (expected the step to {Amends.StepDown(raw):F2})");
                else
                {
                    ReleaseSystem.Release(g, made, record: false);
                    SupervisionCase sc = g.shuttle.OutboundOf(made) is { } outbound ? g.supervision.Find(outbound.caseId) : null;
                    if (sc == null || Mathf.Abs(sc.baseRisk - Amends.StepDown(raw)) > 0.0001f)
                        AmendsFail("leaving", $"released after making amends - the supervision case {(sc == null ? "not opened" : $"baseline {sc.baseRisk:F2}")} (expected {Amends.StepDown(raw):F2}, the step from {raw:F2})");
                }
                // Gone another way (a transfer, a death: what they call) with a meeting approved - refunded once, the meeting lapsed; a proposal has nothing to give back.
                Pawn gone = AmendsInmate(map, r), proposed = AmendsInmate(map, r);
                (gone.Inmate().amendsState, gone.Inmate().amendsTick) = (AmendsState.Approved, now);
                (proposed.Inmate().amendsState, proposed.Inmate().amendsTick) = (AmendsState.Proposed, now);
                from = r.NewLedgerEntries.Count();
                g.story.Notify_Leaving(gone);
                g.story.Notify_Leaving(gone);
                g.story.Notify_Leaving(proposed);
                int refundGone = AmendsRefunds(r, from);
                if (refundGone != 1 || gone.Inmate().amendsState != AmendsState.Lapsed || gone.Inmate().amendsTick != now || proposed.Inmate().amendsState != AmendsState.Proposed)
                    AmendsFail("leaving", $"gone with a meeting approved (told twice) and with one proposed - {refundGone} refund(s) (expected 1), states {gone.Inmate().amendsState}@{gone.Inmate().amendsTick} and {proposed.Inmate().amendsState} (expected Lapsed@{now}, Proposed)");
                // Gone with the family at the table: they are sent home too.
                if (!Amends.HasRoom(map)) { RPLog.Message("[RimPrison:test] amends (leaving): the prison has no visitation room, the meeting under way not checked"); return; }
                Pawn meeting = AmendsInmate(map, r);
                CompInmate cmt = meeting.Inmate();
                (cmt.amendsState, cmt.amendsTick) = (AmendsState.Approved, now);
                List<Pawn> visitors = AmendsVisitors(r);
                if (!Amends.CallFamily(map, meeting, cmt) || cmt.visitor == null) { RPLog.Message("[RimPrison:test] amends (leaving): no way in or no spot for the family, the meeting under way not checked"); return; }
                Pawn family = cmt.visitor;
                visitors.Add(family);
                from = r.NewLedgerEntries.Count();
                g.story.Notify_Leaving(meeting);
                bool sentHome = family.GetLord()?.CurLordToil is LordToil_ExitMap;
                if (AmendsRefunds(r, from) != 1 || cmt.visitor != null || cmt.amendsState != AmendsState.Lapsed || !sentHome)
                    AmendsFail("leaving", $"gone with the family at the table - {AmendsRefunds(r, from)} refund(s) (expected 1), visitor {cmt.visitor?.LabelShort ?? "none"}, state {cmt.amendsState} (expected Lapsed), the family sent home {sentHome}");
                RPLog.Message($"[RimPrison:test] amends: leaving - released {refundReleased}, gone {refundGone} refund(s); the supervision baseline {raw:P0} -> {stepped:P0}");
            });
        }

        private static void CheckAmendsAssessedCase(Map map)
        {
            Guarded("amends: a meeting that ends after the hearing was assessed leaves that case as the board saw it", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                if (AmendsCrime(true) == null) { AmendsFail("assessed", "the game has no violent crime to try"); return; }
                Pawn good = AmendsInmate(map, r), bad = AmendsInmate(map, r);
                int now = Find.TickManager.TicksGame;
                // A hearing that has sat: assessed, the odds and who spoke stated, the warden's letter out.
                ParoleCase Sat(Pawn p, bool letter)
                {
                    var hc = new ParoleCase { pawnId = p.thingIDNumber, name = p.LabelShort, stage = HearingStage.Held, scheduledTick = now, victimLetter = letter };
                    ParoleScene.Assess(map, p, p.Inmate(), hc);
                    g.story.paroleCases.Add(hc);
                    r.Add(() => g.story.paroleCases.Remove(hc));
                    return hc;
                }
                ParoleCase withLetter = Sat(good, true), noLetter = Sat(bad, false);
                string goodBefore = ParoleScene.Describe(withLetter), badBefore = ParoleScene.Describe(noLetter);
                AmendsForce(good, AmendsState.Withdrawn);
                AmendsForce(bad, AmendsState.Hostile);
                if (ParoleScene.Describe(withLetter) != goodBefore || ParoleScene.Describe(noLetter) != badBefore)
                    AmendsFail("assessed", $"an assessed case changed under a meeting - a good one: withdrawn {withLetter.victimWithdrawn} (expected false) at {withLetter.chance:P0}, against [{withLetter.againstText}]; a bad one: the family's letter {noLetter.victimLetter} (expected false) at {noLetter.chance:P0}");
                // The hearings after it read the meeting.
                ParoleCase next = AmendsProbe(map, good), nextBad = AmendsProbe(map, bad, letter: false);
                if (!next.victimWithdrawn || !nextBad.victimLetter)
                    AmendsFail("assessed", $"the next hearing - the letter withdrawn {next.victimWithdrawn} after a good meeting, the family writing {nextBad.victimLetter} after a bad one (expected both)");
                RPLog.Message($"[RimPrison:test] amends: assessed cases kept as the board saw them ({withLetter.chance:P0}, {noLetter.chance:P0}); the next hearing withdrawn {next.victimWithdrawn}, written {nextBad.victimLetter}");
            });
        }

        private static void CheckAmendsTable(Map map)
        {
            Guarded("amends: the counsellor gets up when the inmate is gone or locked down, and a class may call them before the talk", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                if (!Amends.HasRoom(map)) { RPLog.Message("[RimPrison:test] amends (table): the prison has no visitation room, not checked"); return; }
                Pawn p = AmendsInmate(map, r);
                CompInmate c = p.Inmate();
                if (g.LockedDown(c)) { RPLog.Message("[RimPrison:test] amends (table): a lockdown is on, not checked"); return; }
                (c.amendsState, c.amendsTick) = (AmendsState.Approved, Find.TickManager.TicksGame);
                List<Pawn> visitors = AmendsVisitors(r);
                if (!Amends.CallFamily(map, p, c) || c.visitor == null) { RPLog.Message("[RimPrison:test] amends (table): no way in or no spot for the family, not checked"); return; }
                Pawn family = c.visitor;
                visitors.Add(family);
                bool on = !Amends.TableOver(p, family);
                int lock0 = g.lockdownUntilTick;
                r.Add(() => g.lockdownUntilTick = lock0);
                g.lockdownUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerHour;
                bool lockedDown = Amends.TableOver(p, family);
                g.lockdownUntilTick = lock0;
                // (Released, or on the way out: no longer an active inmate. A transfer or a death takes them off the map and ends the meeting with it: CheckAmendsLeaving.)
                p.guest.Released = true;
                bool gone = Amends.TableOver(p, family);
                p.guest.Released = false;
                bool back = !Amends.TableOver(p, family);
                if (!on || !lockedDown || !gone || !back)
                    AmendsFail("table", $"the counsellor stays at the table during the meeting {on}, gets up under a lockdown {lockedDown}, with the inmate released {gone}, stays again {back} (expected all)");
                // A class may call the counsellor away while the inmate has not sat down with the family; not once they talk.
                Pawn counsellor = ThrowawayStaff(map, r, RPProgramDefOf.RP_Counsellor);
                if (!ProgramSystem.CanTeach(counsellor, RPProgramDefOf.RP_Counsellor)) { RPLog.Message("[RimPrison:test] amends (table): the check's counsellor cannot work, the class's call not checked"); return; }
                counsellor.jobs.StartJob(JobMaker.MakeJob(RPAmendsDefOf.RP_AttendAmends, family, p), JobCondition.InterruptForced);
                if (counsellor.CurJobDef != RPAmendsDefOf.RP_AttendAmends) { RPLog.Message("[RimPrison:test] amends (table): the counsellor could not set out for the family (no way there), the class's call not checked"); return; }
                bool beforeTalk = ProgramSystem.CanInterrupt(counsellor);
                p.jobs.StartJob(JobMaker.MakeJob(RPVisitDefOf.RP_VisitTalk, family), JobCondition.InterruptForced);
                bool talking = p.CurJobDef == RPVisitDefOf.RP_VisitTalk;
                bool duringTalk = ProgramSystem.CanInterrupt(counsellor);
                if (talking) p.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                // At the table (beside the family), the talk's end rolls with them and with the search at the gate: the odds the outcome letter states are those.
                counsellor.Position = family.Position;
                Pawn atTable = Amends.AtTheTable(p, family);
                c.visitorStripSearched = true;
                string expected = Amends.Odds(p, c, counsellor, stripSearched: true).ToStringPercent();
                int letters0 = r.NewLetters.Count();
                Amends.ForceOutcome = AmendsState.Withdrawn;
                try { VisitationSystem.Complete(p, family); }
                finally { Amends.ForceOutcome = null; }
                string outcome = r.NewLetters.Skip(letters0).OfType<ChoiceLetter>().LastOrDefault()?.Text.Resolve() ?? "";
                string with = "RP_AmendsWith".Translate(counsellor.LabelShort).Resolve();
                if (atTable != counsellor || !outcome.Contains(with) || !outcome.Contains($"(the odds were {expected})"))
                    AmendsFail("table", $"the counsellor beside the family is at the table {atTable == counsellor}; the outcome letter names them {outcome.Contains(with)} and states the odds with the search at the gate, {expected} - \"{outcome}\"");
                if (!counsellor.Destroyed && counsellor.CurJob != null) counsellor.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                if (!beforeTalk || talking && duringTalk)
                    AmendsFail("table", $"a class may call the counsellor before the talk {beforeTalk} (expected yes), during it {(talking ? duringTalk.ToString() : "not seen")} (expected no)");
                if (!talking) RPLog.Message("[RimPrison:test] amends (table): the inmate could not set out for the family (no way there), the call during the talk not checked");
                RPLog.Message($"[RimPrison:test] amends: the table - gets up under a lockdown {lockedDown}, released {gone}; a class may call before the talk {beforeTalk}, during it {duringTalk}");
            });
        }

        private static void CheckAmendsNowhere(Map map)
        {
            Guarded("amends: no proposal and no goal where no meeting could be held", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                InmateWantDef def = RPAmendsDefOf.RP_Want_Amends;
                Pawn p = AmendsInmate(map, r);
                CompInmate c = p.Inmate();
                if (!Amends.CouldMeet(p, c)) { RPLog.Message("[RimPrison:test] amends (nowhere): no meeting could be held here (no visitation room, or the default level is not visited across a table), not checked"); return; }
                string Why() => Amends.Ineligible(g, p, c) ?? "eligible";
                (bool formsHere, string whyHere) = (def.Worker.CanForm(p, c), Why());
                // No visitation room: the visitation pass calls nobody, so the meeting could only lapse.
                Amends.NoRoomForChecks = true;
                (bool formsNoRoom, string whyNoRoom) = (def.Worker.CanForm(p, c), Why());
                Amends.NoRoomForChecks = false;
                // Supermax (nobody visits), and a level seen only through glass.
                SecurityLevelDef supermax = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(s => s.escortOnly);
                bool formsSupermax = false;
                if (supermax != null)
                {
                    c.security = supermax;
                    formsSupermax = def.Worker.CanForm(p, c);
                    c.security = InmateUtility.DefaultSecurity;
                }
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                bool had = g.visitPolicies.TryGetValue(level, out VisitPolicy policy0);
                void PutBack()
                {
                    if (had) g.visitPolicies[level] = policy0;
                    else g.visitPolicies.Remove(level);
                }
                r.Add(PutBack);
                g.visitPolicies[level] = VisitPolicy.Booth;
                (bool formsGlass, string whyGlass) = (def.Worker.CanForm(p, c), Why());
                PutBack();
                if (!formsHere || whyHere != "eligible" || formsNoRoom || whyNoRoom != "RP_AmendsNo_NoRoom" || formsSupermax || formsGlass || whyGlass != "RP_AmendsNo_NoTable")
                    AmendsFail("nowhere", $"with a room and a table - the goal may form {formsHere}, {whyHere}; no room - {formsNoRoom}, {whyNoRoom}; supermax ({supermax?.defName ?? "none"}) - {formsSupermax}; glass only - {formsGlass}, {whyGlass} (expected true/eligible, then false with NoRoom, false, false with NoTable)");
                RPLog.Message($"[RimPrison:test] amends: nowhere to meet - no room {whyNoRoom}, glass {whyGlass}, the goal forms {formsNoRoom}/{formsSupermax}/{formsGlass}");
            });
        }

        private static void CheckAmendsLetterStates(Map map)
        {
            Guarded("amends: the letter states what is rolled - the search at the gate, the tension with the goal, the press", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                Pawn counsellor = AmendsCounsellor(map, r, "letter");
                if (counsellor == null) return;
                Pawn p = AmendsInmate(map, r);
                CompInmate c = p.Inmate();
                bool goal = Wants.Form(p, c, RPAmendsDefOf.RP_Want_Amends) != null;
                if (!goal) RPLog.Message("[RimPrison:test] amends (letter): the goal does not form here - the tension stated without it");
                VisitorSearchPolicy search0 = g.visitorSearch;
                r.Add(() => g.visitorSearch = search0);
                g.visitorSearch = VisitorSearchPolicy.Thorough;
                float searchedOdds = Amends.Odds(p, c, counsellor, stripSearched: true);
                string thorough = Amends.Propose(g, map, p, counsellor).Text.Resolve();
                c.amendsState = AmendsState.None;
                g.visitorSearch = VisitorSearchPolicy.PatDown;
                string patDown = Amends.Propose(g, map, p, counsellor).Text.Resolve();
                c.amendsState = AmendsState.None;
                g.visitorSearch = search0;
                string part = "RP_AmendsSearchedPart".Translate(Amends.StripSearched.ToStringPercent(), searchedOdds.ToStringPercent()).Resolve();
                float relief = Amends.GoodTension + (goal ? Wants.FulfilledRelief : 0f);
                string falls = $"falls by {relief:0}", press = $"-{Amends.PressHit:0} reputation", perLevel = AmendsSigned(Amends.SocialPerLevel);
                if (!thorough.Contains(part) || patDown.Contains(part) || !patDown.Contains(falls) || !patDown.Contains(press) || !patDown.Contains(perLevel))
                    AmendsFail("letter", $"the search's part with a thorough search {thorough.Contains(part)} and with a pat-down {patDown.Contains(part)} (expected only the first: \"{part}\"); \"{falls}\" {patDown.Contains(falls)}, \"{press}\" {patDown.Contains(press)}, Social per level {perLevel} {patDown.Contains(perLevel)}");
                // A good meeting takes off what the letter said.
                c.tension = 60f;
                AmendsForce(p, AmendsState.Withdrawn);
                if (Mathf.Abs(60f - c.tension - relief) > 0.001f)
                    AmendsFail("letter", $"a good meeting took the tension 60 -> {c.tension:F0} (the letter said by {relief:0}{(goal ? ", the goal reached with it" : "")})");
                RPLog.Message($"[RimPrison:test] amends: the letter - the search's part \"{part}\", tension by {relief:0}, a good meeting 60 -> {c.tension:F0}");
            });
        }

        private static void CheckAmendsHourly(Map map)
        {
            Guarded("amends: the 10:00 pass proposes for the goal's holder first, and the visitation pass calls an approved family", map, r =>
            {
                AmendsKeep(map, r);
                PrisonGame g = PrisonGame.Instance;
                Pawn counsellor = AmendsCounsellor(map, r, "hourly");
                if (counsellor == null) return;
                Pawn wanting = AmendsInmate(map, r, programs: 0), schooled = AmendsInmate(map, r, programs: 99);
                CompInmate cw = wanting.Inmate(), cs = schooled.Inmate();
                if (!Amends.CouldMeet(wanting, cw) || Wants.Form(wanting, cw, RPAmendsDefOf.RP_Want_Amends) == null)
                {
                    RPLog.Message("[RimPrison:test] amends (hourly): no meeting could be held here, or the goal did not form - not checked");
                    return;
                }
                int now = Find.TickManager.TicksGame;
                // (The live inmates out of the running - declined just now, which the hourly pass leaves alone - and put back by the rollback.)
                foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned)
                    if (x != wanting && x != schooled && x.Inmate() is { } xc) (xc.amendsState, xc.amendsTick) = (AmendsState.Declined, now);
                Amends.Hourly(g, map, Amends.ProposeHour + 1);
                (AmendsState wrongHour, AmendsState wrongHourOther) = (cw.amendsState, cs.amendsState);
                Amends.Hourly(g, map, Amends.ProposeHour);
                bool letter = AmendsLetter(r, wanting) != null;
                if (wrongHour != AmendsState.None || wrongHourOther != AmendsState.None || cw.amendsState != AmendsState.Proposed || cs.amendsState != AmendsState.None || !letter)
                    AmendsFail("hourly", $"at {Amends.ProposeHour + 1}:00 {wrongHour}/{wrongHourOther} (expected nothing proposed); at {Amends.ProposeHour}:00 the goal's holder {cw.amendsState} (letter {letter}), 99 programs {cs.amendsState} (expected Proposed, None: the goal first)");
                // The visitation pass: the approved meeting calls the family, the visit marked as the victim's.
                (cw.amendsState, cw.amendsTick) = (AmendsState.Approved, now);
                int hour = GenLocalDate.HourOfDay(map);
                if (!VisitationSystem.CanBeVisited(wanting, cw, g, hour, 0, 0, out VisitPolicy policy) || policy != VisitPolicy.Contact)
                {
                    RPLog.Message($"[RimPrison:test] amends (hourly): {wanting.LabelShort} could not be visited at a table at {hour}:00 (policy {policy}), the visitation pass not checked");
                    return;
                }
                int active = 0;
                Amends.HourlyVisits(map, g, hour, VisitationSystem.MaxConcurrentVisits, 0, 0, ref active);
                Pawn family = cw.visitor;
                if (family != null) AmendsVisitors(r).Add(family);
                if (family == null)
                {
                    RPLog.Message("[RimPrison:test] amends (hourly): the visitation pass called nobody (no way in or no spot for the family), not checked");
                    return;
                }
                if (active != 1 || cw.amendsState != AmendsState.Meeting || cw.visitorRelative != null || VisitationSystem.VisitOf(family) is not { ForAmends: true })
                    AmendsFail("hourly", $"the visitation pass - {active} called (expected 1), state {cw.amendsState} (expected Meeting), relative {cw.visitorRelative ?? "none"}, the visit marked for amends {VisitationSystem.VisitOf(family)?.ForAmends}");
                RPLog.Message($"[RimPrison:test] amends: the hourly passes - the goal's holder proposed before 99 programs, {family.LabelShort} called at {hour}:00");
            });
        }

        // ---- the round trip -----------------------------------------------------------------------------------------------

        private static readonly List<(int id, AmendsState state, int tick, string seeded)> amendsSeeds = new List<(int, AmendsState, int, string)>();

        /// <summary>The visit under way is the victim's family's (the flag on the visit's lord, <see cref="LordJob_VisitInmate.ForAmends"/>), for the snapshot.</summary>
        private static string AmendsVisitMark(CompInmate c) => c.visitor != null && VisitationSystem.VisitOf(c.visitor) is { ForAmends: true } ? "+family" : "";

        /// <summary>For the saveload snapshot: every inmate whose meeting is not at its default (and whether the visit under way is marked as the victim's family's).</summary>
        private static string AmendsDescribe(Map map) =>
            string.Join(",", map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { } c && (c.amendsState != AmendsState.None || c.amendsTick != -1)).OrderBy(p => p.thingIDNumber)
                .Select(p => $"{p.thingIDNumber}:{Amends.Describe(p.Inmate())}{AmendsVisitMark(p.Inmate())}"));

        /// <summary>
        /// Saveload plan, before the snapshot: two inmates with a meeting off its default - one approved whose family is called at once (a meeting under way, the visit's lord marked as the victim's family's;
        /// approved and waiting if the family cannot come), one that went well and the family speaks for them. The parole case's withdrawn flag is seeded with the hearings (<see cref="SeedParoleForReload"/>).
        /// </summary>
        private static void SeedAmendsForReload(Map map)
        {
            amendsSeeds.Clear();
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { amendsState: AmendsState.None, visitor: null }).OrderBy(p => p.thingIDNumber).Take(2).ToList();
            if (inmates.Count < 2)
            {
                RPLog.Message("[RimPrison:test] saveload amends: not seeded (needs two inmates with no visit on)");
                return;
            }
            int now = Find.TickManager.TicksGame;
            (AmendsState state, int tick)[] seeds = { (AmendsState.Approved, now), (AmendsState.Supports, System.Math.Max(0, now - 1234)) };
            for (int i = 0; i < 2; i++)
            {
                CompInmate c = inmates[i].Inmate();
                AmendsState state0 = c.amendsState;
                int tick0 = c.amendsTick;
                (c.amendsState, c.amendsTick) = seeds[i];
                if (i == 0 && !(Amends.HasRoom(map) && Amends.CallFamily(map, inmates[i], c)))
                    RPLog.Message("[RimPrison:test] saveload amends: the family could not be called (no visitation room, no way in or no spot) - the visit's flag not seeded");
                amendsSeeds.Add((inmates[i].thingIDNumber, state0, tick0, Amends.Describe(c) + AmendsVisitMark(c)));
            }
            RPLog.Message($"[RimPrison:test] saveload amends: {AmendsDescribe(map)}");
        }

        /// <summary>After the reload: each seeded meeting came back whole (with its visit's flag), and is put back as it was (the seeded family sent off and removed).</summary>
        private static void CheckAmendsAfterReload(Map map)
        {
            foreach ((int id, AmendsState state, int tick, string seeded) in amendsSeeds)
            {
                CompInmate c = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.thingIDNumber == id)?.Inmate();
                string after = c == null ? "(inmate gone)" : Amends.Describe(c) + AmendsVisitMark(c);
                RPLog.Message($"[RimPrison:test] saveload amends: {id} {(after == seeded ? "intact" : "changed")} ({after})");
                if (after != seeded) reloadProblems.Add($"a restorative meeting changed in the round trip: {seeded} -> {after}");
                if (c == null) continue;
                if (c.amendsState == AmendsState.Meeting && c.visitor is { } family)
                {
                    VisitationSystem.EndVisit(c, family);
                    if (!family.Destroyed) family.Destroy(DestroyMode.Vanish);
                }
                (c.amendsState, c.amendsTick) = (state, tick);
            }
            amendsSeeds.Clear();
        }
    }
}
