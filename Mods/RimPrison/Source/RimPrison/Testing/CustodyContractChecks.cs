using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Custody contracts (Intake/CustodyContracts.cs, the Intake tab): Astra's headless proof. The definition and its texts; the offer (the official dossier only, three to five places of minimum and medium security, terms that outlast the window, a
    /// budgeted letter that stands for two days, one at a time, every 7-10 days once there is a classroom and a counsellor); a contract signed with room, a shuttle held back by weather, then a part of the cohort admitted (the beds there are), the rest left
    /// aboard when the shuttle lifts off, kept as the contract's own and admitted by the next, the payment and the window of each by their own admission, once; real attendance credited by the program system's own credit, the bonus paid once at the end of
    /// the window and never before, a course that ran with nobody sitting it earning nothing, a session after the window not counting; the ways out (a transfer, a release, a death, an escape, one the events did not tell, a return on a stay of its own)
    /// forfeiting the bonus and nothing more (an escape nobody sees too, as they leave the map); the sentence running from admission; and every rule of the stated pauses and cancellations (a window that ends, a day added for a shuttle called off or a roster the marshals filled, the state's special measures, no overcrowding, one at a time).
    /// Self-contained, on throwaway inmates and the live prison's own rooms and beds (the book, the shuttle's carried-over list, the beds, the weather, the oversight set aside and put back by the rollback). The round trip of the saved book, with a
    /// contract half way through, is in the saveload plan (<see cref="SeedCustodyForReload"/>). Premises the live prison may not give (a pad, a classroom, a counsellor, free beds) are told and not failed.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CustodyFail(string check, string what) => problems0.Add($"custody contracts ({check}): {what}");

        private static void CheckCustodyContracts(Map map)
        {
            CheckCustodyTexts(map);
            CheckCustodyOffer(map);
            CheckCustodyCadence(map);
            CheckCustodyShuttle(map);
            CheckCustodyAttendance(map);
            CheckCustodyDepartures(map);
            CheckCustodyUnseenEscape(map);
            CheckCustodySentenceClock(map);
            CheckCustodyRules(map);
        }

        // ---- helpers ------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The book as a check needs it: a new one (the game's put back), no special measures, funds to pay the transfers with.</summary>
        private static void CustodySetAside(PrisonGame g, Rollback r)
        {
            CustodyBook book0 = g.custody;
            StateOversight oversight0 = g.story.oversight;
            g.custody = new CustodyBook();
            g.story.oversight = null;
            r.Add(() => (g.custody, g.story.oversight) = (book0, oversight0));
            g.ledger.funds = 20000;
        }

        private static CustodyMember CustodyRow(SecurityLevelDef level, int fee = 150, int bonus = 450, int term = 12) =>
            new CustodyMember { level = level, crime = level.crimes.FirstOrDefault(), termDays = term, fee = fee, bonus = bonus };

        /// <summary>An offer of these places, signed.</summary>
        private static CustodyContract CustodySign(PrisonGame g, Map map, params CustodyMember[] rows)
        {
            int now = Find.TickManager.TicksGame;
            var offer = new CustodyOffer { id = g.custody.nextId++, def = CustodyContracts.Def, mapId = map.uniqueID, createdTick = now, expiresTick = now + 100000, rows = rows.ToList() };
            g.custody.offer = offer;
            return CustodyContracts.Accept(g, map, offer.id);
        }

        /// <summary>A made-up inmate admitted into the member's place, as the shuttle's unloading would (the payment on admission, the window).</summary>
        private static Pawn CustodyAdmit(PrisonGame g, Map map, Rollback r, CustodyMember m, Pawn existing = null)
        {
            Pawn p = existing ?? Throwaway(map, r, m.level);
            p.Inmate().processed = true;
            m.pawn = p;
            m.pawnId = p.thingIDNumber;
            r.Add(() =>
            {
                if (p.Corpse is { Destroyed: false } corpse) corpse.Destroy();
            });
            CustodyContracts.Notify_Admitted(g, p);
            return p;
        }

        private static List<LedgerEntry> CustodyEntries(Rollback r) => r.NewLedgerEntries.Where(e => e.category == RPCustodyDefOf.RP_Custody).ToList();

        // ---- the definition and its texts ----------------------------------------------------------------------------------------------------------------------------------------

        private static readonly string[] custodyKeys =
        {
            "RP_Chr_CustodyAccepted", "RP_Chr_CustodyCancelled", "RP_Chr_CustodyClosed", "RP_CustodyAPlace", "RP_CustodyAccepted", "RP_CustodyAdmitted", "RP_CustodyAlready", "RP_CustodyBedsLine", "RP_CustodyBedsPart",
            "RP_CustodyBonusNote", "RP_CustodyCancelled", "RP_CustodyClosed", "RP_CustodyDemand", "RP_CustodyDemandEnough", "RP_CustodyDemandLine", "RP_CustodyDemandShort", "RP_CustodyDossierLine", "RP_CustodyDropped",
            "RP_CustodyEnd_Died", "RP_CustodyEnd_Escaped", "RP_CustodyEnd_Gone", "RP_CustodyEnd_Late", "RP_CustodyEnd_Lost", "RP_CustodyEnd_Measures", "RP_CustodyEnd_Paroled", "RP_CustodyEnd_Released", "RP_CustodyEnd_Transferred",
            "RP_CustodyFeeNote", "RP_CustodyForfeit", "RP_CustodyHeader", "RP_CustodyHeaderTip", "RP_CustodyLabel", "RP_CustodyLastLine", "RP_CustodyMet", "RP_CustodyMissed", "RP_CustodyNextOffer", "RP_CustodyNextOfferNone",
            "RP_CustodyNoOfferClassroom", "RP_CustodyNoOfferContract", "RP_CustodyNoOfferLevels", "RP_CustodyNoOfferMeasures", "RP_CustodyNoOfferNow", "RP_CustodyNoOfferOpen", "RP_CustodyOfferGone", "RP_CustodyOfferOpenLine",
            "RP_CustodyRowDropped", "RP_CustodyRowForfeited", "RP_CustodyRowMet", "RP_CustodyRowMissed", "RP_CustodyRowPending", "RP_CustodyRowServing", "RP_CustodyRules", "RP_CustodySummary", "RP_CustodyText", "RP_Custody_Accept",
            "RP_Custody_Decline",
        };

        private static void CheckCustodyTexts(Map map)
        {
            Guarded("custody contracts: the definition and every text", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                CustodyContractDef def = CustodyContracts.Def;
                if (def == null || def.levels.Count == 0 || def.demandProgram == null || def.levels.Any(l => l == null || l.crimes.Count == 0))
                {
                    CustodyFail("definition", $"the rehabilitation contract is missing, or has no levels with crimes, or no course to measure the classroom against ({def?.defName})");
                    return;
                }
                if (def.cohort.min < 3 || def.cohort.max > 5 || def.serviceDays != 7 || def.levels.Any(l => l.defName is not ("RP_Minimum" or "RP_Medium")) || def.offerEveryDays.min != 7 || def.offerEveryDays.max != 10)
                    CustodyFail("definition", $"the terms are not a cohort of 3-5 minimum or medium inmates, a 7-day window and an offer every 7-10 days ({def.cohort}, {def.serviceDays} days, {string.Join("/", def.levels.Select(l => l.defName))}, {def.offerEveryDays})");
                if (def.minTermDays < def.serviceDays + 1) CustodyFail("definition", $"the shortest term ({def.minTermDays} days) does not outlast the window ({def.serviceDays} days)");
                if (RPCustodyDefOf.RP_Custody == null || !RPCustodyDefOf.RP_Custody.income) CustodyFail("definition", "the ledger category for custody contracts is missing or is not income");
                PrisonDecisionDef decision = PrisonDecisionDef.Named("RP_Decision_CustodyContract");
                // (A budgeted letter: not flagged urgent, and its answer time is more than the hours under which the budget lets a letter through, see StoryLetters.Urgent.)
                if (decision.urgent || def.offerHours <= StoryLetters.UrgentHours) CustodyFail("definition", $"the offer letter is not budgeted (urgent {decision.urgent}, stands {def.offerHours} hours, urgent below {StoryLetters.UrgentHours})");
                foreach (string key in custodyKeys)
                    if (!key.CanTranslate()) CustodyFail("texts", $"no keyed string {key}");
                foreach (OutboundKind kind in Enum.GetValues(typeof(OutboundKind)))
                    if (!("RP_CustodyEnd_" + kind).CanTranslate()) CustodyFail("texts", $"no keyed string RP_CustodyEnd_{kind} for a way out");
                // The offer's letter and every line of the Intake tab read, with nothing left unfilled.
                SecurityLevelDef level = def.levels[0];
                var offer = new CustodyOffer { id = 1, def = def, rows = new List<CustodyMember> { CustodyRow(level), CustodyRow(def.levels[def.levels.Count - 1]) } };
                string text = CustodyContracts.OfferText(map, offer).Resolve();
                if (text.Contains("{") || text.Contains("RP_Custody") || text.Contains("Translation error")) CustodyFail("texts", $"the offer's text has something unfilled in it: {text}");
                var c = new CustodyContract { def = def, serviceDays = def.serviceDays, requiredSessions = def.requiredSessions, arrivalDeadlineTick = Find.TickManager.TicksGame + 100000, members = offer.rows };
                CustodyMember m = c.members[0];
                foreach (CustodyState state in Enum.GetValues(typeof(CustodyState)))
                {
                    m.state = state;
                    m.admittedTick = Find.TickManager.TicksGame;
                    m.dueTick = Find.TickManager.TicksGame + 100000;
                    m.why = state == CustodyState.Forfeited ? "RP_CustodyEnd_Transferred" : state == CustodyState.Dropped ? "RP_CustodyEnd_Late" : null;
                    string line = CustodyContracts.MemberLine(c, m);
                    if (line.Contains("{") || line.Contains("RP_Custody") || line.Contains("Translation error")) CustodyFail("texts", $"the line for a member who is {state} has something unfilled in it: {line}");
                }
                string summary = CustodyContracts.SummaryLine(c);
                if (summary.Contains("{") || summary.Contains("RP_Custody")) CustodyFail("texts", $"the contract's summary has something unfilled in it: {summary}");
            });
        }

        // ---- the offer -----------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckCustodyOffer(Map map)
        {
            Guarded("custody contracts: the offer lists the official file and nothing hidden", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                CustodySetAside(g, r);
                CustodyContractDef def = CustodyContracts.Def;
                int number0 = g.intake.nextInmateNumber, offers0 = g.stats.custodyOffers;
                CustodyOffer offer = CustodyContracts.Offer(g, map);
                if (offer == null)
                {
                    CustodyFail("offer", "no offer was made");
                    return;
                }
                if (offer.rows.Count < def.cohort.min || offer.rows.Count > def.cohort.max) CustodyFail("offer", $"the offer lists {offer.rows.Count} places, not {def.cohort.min} to {def.cohort.max}");
                foreach (CustodyMember row in offer.rows)
                {
                    if (!def.levels.Contains(row.level) || row.crime == null || !row.level.crimes.Contains(row.crime) || row.termDays < def.minTermDays || row.fee <= 0 || row.bonus <= row.fee)
                        CustodyFail("offer", $"a place in the dossier is not an official file: {row.Describe()}");
                    if (row.pawn != null || row.pawnId >= 0 || row.state != CustodyState.Pending) CustodyFail("offer", $"a place already has a person, or is not waiting: {row.Describe()}");
                }
                // Nobody was made for it (a gang, a claim of innocence are rolled with the person, who does not exist yet): nothing hidden could have been told.
                if (g.intake.nextInmateNumber != number0) CustodyFail("offer", $"making the offer made {g.intake.nextInmateNumber - number0} inmates (their hidden file with them)");
                if (g.custody.offer != offer || g.stats.custodyOffers != offers0 + 1) CustodyFail("offer", "the offer was not kept on the book, or not counted");
                var letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_CustodyContract"));
                if (letter == null || letter.amount != offer.id || !letter.TimeoutActive)
                {
                    CustodyFail("offer", $"the offer's letter was not sent, or does not point at the offer, or does not run out ({letter?.amount} for {offer.id})");
                    return;
                }
                string text = letter.Text.Resolve();
                foreach (CustodyMember row in offer.rows)
                    if (!text.Contains(row.crime.label) || !text.Contains(row.level.label)) CustodyFail("offer", $"the letter does not show the file of a place ({row.level.label}, {row.crime.label})");
                if (text.IndexOf("innocen", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("gang", StringComparison.OrdinalIgnoreCase) >= 0) CustodyFail("offer", "the letter says something about a gang or about innocence, which an offer never reveals");
                if (!text.Contains(Money.Format(offer.rows.Sum(x => x.fee))) || !text.Contains(Money.Format(offer.rows.Sum(x => x.bonus)))) CustodyFail("offer", "the letter does not give the fees and the bonuses in all");
                // The classroom and the teachers, against what the cohort needs: the numbers the letter and the tab show are the prison's.
                CustodySupply s = CustodyContracts.Supply(map, def, offer.rows.Count);
                var venues = new List<(IntVec3 anchor, int places)>();
                ProgramSystem.ListVenues(map, PrisonMap.Of(map), def.demandProgram, venues);
                int rooms = venues.Count(v => v.places > 0), teachers = StaffCommute.AllStaff(map).Count(p => p.GetComp<CompStaff>()?.role is { } role && def.demandProgram.TeacherRoles.Contains(role) && ProgramSystem.CanTeach(p, role));
                if (s.rooms != rooms || s.teachers != teachers) CustodyFail("offer", $"the demand against the prison counts {s.rooms} rooms and {s.teachers} teachers, the prison has {rooms} and {teachers}");
                if (s.seatsAtOnce > 0 && s.classHours != def.requiredSessions * def.demandProgram.sessionHours * (int)Math.Ceiling(offer.rows.Count / (double)s.seatsAtOnce)) CustodyFail("offer", $"the class hours asked ({s.classHours}) are not the sessions each times the sittings the room needs");
                // One open offer at a time; no second one while it stands.
                if (CustodyContracts.NoOfferBecause(g, map) != "RP_CustodyNoOfferOpen") CustodyFail("offer", $"with an offer open the reason none is made is {CustodyContracts.NoOfferBecause(g, map)}");
                // Declined by the letter's own button: nothing is owed, nobody comes, and the offer is gone.
                int funds0 = g.ledger.funds;
                PrisonDecisions.Choose(letter, 1);
                if (g.custody.offer != null || g.custody.contract != null || g.ledger.funds != funds0 || g.intake.nextInmateNumber != number0) CustodyFail("offer", "declining the offer left an offer or a contract, or moved money or made inmates");
                // No answer is a no: a letter that lapses declines.
                CustodyOffer second = CustodyContracts.Offer(g, map);
                var secondLetter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_CustodyContract"));
                if (second == null || secondLetter == null || secondLetter == letter)
                {
                    CustodyFail("offer", "a second offer was not made after the first was declined");
                    return;
                }
                secondLetter.Lapse();
                if (g.custody.offer != null || g.custody.contract != null) CustodyFail("offer", "an offer left to lapse was not declined");
                // Signed by the letter's button: the offer becomes the contract, with the terms as signed, nobody made and nothing paid until they are admitted.
                CustodyOffer third = CustodyContracts.Offer(g, map);
                var thirdLetter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_CustodyContract"));
                if (third == null || thirdLetter == null) return;
                if (thirdLetter.decision.Worker.Disabled(thirdLetter, 0) != null) CustodyFail("offer", "signing is refused for an offer that stands");
                int day0 = Find.TickManager.TicksGame;
                PrisonDecisions.Choose(thirdLetter, 0);
                CustodyContract c = g.custody.contract;
                if (c == null || g.custody.offer != null || c.id != third.id || c.members.Count != third.rows.Count || c.serviceDays != def.serviceDays || c.requiredSessions != def.requiredSessions || c.def != def
                    || c.arrivalDeadlineTick != c.acceptedTick + def.arrivalDays * GenDate.TicksPerDay || c.acceptedTick < day0 || c.members.Any(m => m.state != CustodyState.Pending || m.pawn != null || m.pawnId >= 0))
                    CustodyFail("offer", $"signing did not make the contract as offered ({c?.Describe()})");
                if (g.ledger.funds != funds0 || g.intake.nextInmateNumber != number0) CustodyFail("offer", "signing paid or made somebody before anybody was admitted");
                if (thirdLetter.decision.Worker.Disabled(thirdLetter, 0) == null) CustodyFail("offer", "with a contract running, signing is not refused (one at a time)");
                if (CustodyContracts.NoOfferBecause(g, map) != "RP_CustodyNoOfferContract") CustodyFail("offer", $"with a contract running the reason no offer is made is {CustodyContracts.NoOfferBecause(g, map)}");
            });
        }

        // ---- the cadence ---------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckCustodyCadence(Map map)
        {
            Guarded("custody contracts: an offer every 7-10 days once there is a classroom and a counsellor", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                CustodySetAside(g, r);
                CustodyContractDef def = CustodyContracts.Def;
                int today = Ledger.CurrentDay;
                // No classroom (the course measured against is one nobody has a room for): no clock, no offer.
                ProgramDef bare = DefDatabase<ProgramDef>.AllDefsListForReading.FirstOrDefault(p =>
                {
                    var rooms = new List<(IntVec3 anchor, int places)>();
                    ProgramSystem.ListVenues(map, PrisonMap.Of(map), p, rooms);
                    return !rooms.Any(v => v.places > 0);
                });
                ProgramDef course0 = def.demandProgram;
                if (bare != null)
                {
                    def.demandProgram = bare;
                    r.Add(() => def.demandProgram = course0);
                    int letters0 = r.NewLetters.Count();
                    CustodyContracts.Daily(g, map);
                    if (g.custody.nextOfferDay != -1 || g.custody.offer != null || r.NewLetters.Count() != letters0) CustodyFail("cadence", $"with no classroom a pass started the clock or made an offer (next {g.custody.nextOfferDay})");
                    if (CustodyContracts.NoOfferBecause(g, map) != "RP_CustodyNoOfferClassroom") CustodyFail("cadence", $"with no classroom the reason is {CustodyContracts.NoOfferBecause(g, map)}");
                    def.demandProgram = course0;
                }
                else RPLog.Message("[RimPrison:test] custody cadence: every course has a room in this prison, the no-classroom case is not checked");
                if (!CustodyContracts.CanTeachHere(map, def))
                {
                    RPLog.Message("[RimPrison:test] custody cadence: the live prison has no classroom with seats or no counsellor, the offers are not checked");
                    return;
                }
                // The first pass with both starts the clock: 7 to 10 days off, no offer yet.
                CustodyContracts.Daily(g, map);
                if (g.custody.nextOfferDay < today + def.offerEveryDays.min || g.custody.nextOfferDay > today + def.offerEveryDays.max || g.custody.offer != null)
                    CustodyFail("cadence", $"the first pass set the next offer for day {g.custody.nextOfferDay} (today {today}), not 7 to 10 days off, or made one at once");
                CustodyContracts.Daily(g, map);
                if (g.custody.offer != null) CustodyFail("cadence", "an offer was made before it was due");
                // Due: one offer, and the next 7 to 10 days from it.
                g.custody.nextOfferDay = today;
                CustodyContracts.Daily(g, map);
                CustodyOffer offer = g.custody.offer;
                if (offer == null || g.custody.nextOfferDay < today + def.offerEveryDays.min || g.custody.nextOfferDay > today + def.offerEveryDays.max)
                    CustodyFail("cadence", $"a due offer was not made, or the one after it is not 7 to 10 days off (offer {offer != null}, next {g.custody.nextOfferDay}, today {today})");
                // With the offer open, or a contract running, no more.
                int made = g.stats.custodyOffers;
                g.custody.nextOfferDay = today;
                CustodyContracts.Daily(g, map);
                if (g.stats.custodyOffers != made) CustodyFail("cadence", "a second offer was made while one stood");
                g.custody.offer = null;
                g.custody.contract = new CustodyContract { def = def, members = new List<CustodyMember> { CustodyRow(def.levels[0]) } };
                CustodyContracts.Daily(g, map);
                if (g.stats.custodyOffers != made || g.custody.offer != null) CustodyFail("cadence", "an offer was made while a contract was running");
                // An offer whose letter is long gone does not stand for ever.
                g.custody.contract = null;
                g.custody.offer = new CustodyOffer { id = 99, def = def, createdTick = Find.TickManager.TicksGame, expiresTick = Find.TickManager.TicksGame - (StoryLetters.MaxHeldDays + 2) * GenDate.TicksPerDay };
                g.custody.nextOfferDay = today + 20;
                CustodyContracts.Daily(g, map);
                if (g.custody.offer != null) CustodyFail("cadence", "an offer that ran out days ago is still on the book");
            });
        }

        // ---- the shuttle: delayed, a part of the cohort, the rest kept ---------------------------------------------------------------------------------------------------------

        private static void CheckCustodyShuttle(Map map)
        {
            Guarded("custody contracts: a delayed shuttle, a part of the cohort, payment and window by admission", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                if (!ShuttleStage(map, r, "custody shuttle")) return;
                if (!ShuttleSystem.HasPad(map))
                {
                    RPLog.Message("[RimPrison:test] custody shuttle: no pad in this prison, not checked");
                    return;
                }
                CustodySetAside(g, r);
                List<Pawn> carried0 = g.shuttle.carriedOver;
                List<MarshalEntry> marshals0 = g.shuttle.marshals;
                g.shuttle.carriedOver = new List<Pawn>();
                g.shuttle.marshals = new List<MarshalEntry>();
                r.Add(() => (g.shuttle.carriedOver, g.shuttle.marshals) = (carried0, marshals0));
                SecurityLevelDef level = DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum");
                if (IntakeSystem.FreeBedCount(map, level) < 2)
                {
                    RPLog.Message($"[RimPrison:test] custody shuttle: fewer than two free {level.label} beds in this prison, not checked");
                    return;
                }
                // Exactly two free beds of the level: the cohort of three can only come in part (the others' beds are held by throwaway inmates).
                var fillers = new List<Pawn>();
                while (IntakeSystem.FreeBedCount(map, level) > 2 && fillers.Count < 80)
                {
                    Building_Bed bed = IntakeSystem.FreeBedsInOrder(map, level).FirstOrDefault();
                    Pawn filler = bed == null ? null : Throwaway(map, r, level);
                    if (filler == null || !filler.ownership.ClaimBedIfNonMedical(bed)) break;
                    fillers.Add(filler);
                }
                if (IntakeSystem.FreeBedCount(map, level) != 2)
                {
                    RPLog.Message($"[RimPrison:test] custody shuttle: could not leave exactly two free {level.label} beds ({IntakeSystem.FreeBedCount(map, level)}), not checked");
                    return;
                }
                var admitted = new List<Pawn>();
                r.Add(() =>
                {
                    foreach (Pawn p in admitted) ShuttleRemovePawn(p);
                });
                const int fee = 150, bonus = 450;
                CustodyContract c = CustodySign(g, map, CustodyRow(level, fee, bonus), CustodyRow(level, fee, bonus), CustodyRow(level, fee, bonus));
                if (c == null)
                {
                    CustodyFail("shuttle", "the contract was not signed");
                    return;
                }
                // (A pawn made for a member and left in the world, when a failure ends the check early, is given back.)
                r.Add(() =>
                {
                    foreach (CustodyMember m in c.members) ShuttleRemovePawn(m.pawn);
                });
                int number0 = g.intake.nextInmateNumber, funds0 = g.ledger.funds, deadline0 = c.arrivalDeadlineTick, extended = 0;
                // (What judges "nobody was made, admitted or paid" is the contract's own: its members hold no pawn, no ledger entry of the custody category, nothing paid. The prison's funds and inmate numbers are not: real ticks cross an
                // hour, and the hour's own pass spends and admits as it does - the commute's pass ran between the two looks in a run that failed - so they are only in the line, for whoever reads it.)
                string Drift() => $"funds {g.ledger.funds - funds0:+0;-0;0}, inmate numbers {g.intake.nextInmateNumber - number0}, custody ledger entries {CustodyEntries(r).Count}, the ledger's other new entries [{string.Join(", ", r.NewLedgerEntries.Where(e => e.category != RPCustodyDefOf.RP_Custody).Select(e => $"{e.category?.defName} {e.amount}"))}], hour {GenLocalDate.HourOfDay(map)}";

                // The weather holds the shuttle: nothing arrives, nothing is paid, nobody is made, the window is as it was.
                WeatherDef storm = DefDatabase<WeatherDef>.GetNamedSilentFail("RainyThunderstorm");
                WeatherDef weather0 = map.weatherManager.curWeather;
                r.Add(() => map.weatherManager.curWeather = weather0);
                ShuttleSystem.ClearSkiesForTests = false;
                ShuttleVisit v = null;
                if (storm != null)
                {
                    map.weatherManager.curWeather = storm;
                    map.weatherManager.curWeatherAge = 0;
                    ShuttleSystem.CallDaily(g, map);
                    v = g.shuttle.visit;
                    ShuttleTicks(() => false, 200);
                    bool held = g.shuttle.visit == v && v is { phase: ShuttlePhase.Holding, hold: ShuttleHold.Weather };
                    if (!held || c.members.Any(m => m.state != CustodyState.Pending || m.pawnId >= 0) || CustodyEntries(r).Count != 0 || c.paidAdmission != 0 || c.arrivalDeadlineTick != deadline0 || c.extensionDays != 0)
                        CustodyFail("shuttle", $"with the shuttle held by a storm ({held}) somebody was made, admitted or paid, or the window moved ({c.Describe()}; {Drift()})");
                    // Six hours of it and the state calls the shuttle off (the real call-off, not the contract's hook alone): nobody is made or paid, and a day is added to the arrival window.
                    v.calledTick = ShNow - ShuttleSystem.MaxHoldTicks - 1;
                    ShuttleTicks(() => g.shuttle.visit != v, 200);
                    bool calledOff = g.shuttle.visit == null;
                    if (!calledOff || c.members.Any(m => m.state != CustodyState.Pending || m.pawnId >= 0) || CustodyEntries(r).Count != 0 || c.paidAdmission != 0 || c.extensionDays != 1 || c.arrivalDeadlineTick != deadline0 + GenDate.TicksPerDay)
                        CustodyFail("shuttle", $"a shuttle called off after six hours of storm ({calledOff}) did not add a day to the arrival window, or somebody was made, admitted or paid ({c.Describe()}; {Drift()}; the window was to end at {deadline0 + GenDate.TicksPerDay})");
                    (deadline0, extended) = (c.arrivalDeadlineTick, c.extensionDays);
                    map.weatherManager.curWeather = DefDatabase<WeatherDef>.GetNamed("Clear");
                    map.weatherManager.curWeatherAge = 0;
                    ShuttleSystem.CallDaily(g, map);
                    v = g.shuttle.visit;
                }
                else
                {
                    RPLog.Message("[RimPrison:test] custody shuttle: no thunderstorm in this game, the delay is not checked");
                    ShuttleSystem.ClearSkiesForTests = true;
                    ShuttleSystem.CallDaily(g, map);
                    v = g.shuttle.visit;
                }
                int freeAtCall = IntakeSystem.FreeBedCount(map, level);
                int expected = Math.Min(3, freeAtCall);
                ShuttleTicks(() => g.shuttle.visit != v || v.phase == ShuttlePhase.Landed, 2500);
                if (g.shuttle.visit != v || v.phase != ShuttlePhase.Landed)
                {
                    CustodyFail("shuttle", $"the shuttle did not land ({v?.Describe()})");
                    return;
                }
                int rosterSize = v.rosterSize;
                ShuttleTicks(() => g.shuttle.visit != v || v.arrivalsDone, 3000);
                admitted.AddRange(c.members.Where(m => m.pawn != null).Select(m => m.pawn));
                List<CustodyMember> serving = c.members.Where(m => m.state == CustodyState.Serving).ToList();
                if (rosterSize != expected || serving.Count != expected || c.Count(CustodyState.Pending) != 3 - expected)
                    CustodyFail("shuttle", $"with {freeAtCall} free beds the shuttle brought {rosterSize} of the cohort and {serving.Count} are admitted, expected {expected} ({c.Describe()})");
                foreach (CustodyMember m in serving)
                {
                    Pawn p = m.pawn;
                    CompInmate ci = p?.Inmate();
                    // Their own admission: the window runs from it, on the person the dossier named.
                    if (p == null || ci == null || !p.IsPrisonerOfColony || ci.crime != m.crime || ci.security != m.level || ci.sentenceDays != m.termDays)
                        CustodyFail("shuttle", $"an admitted member is not the person the dossier named (crime {ci?.crime?.defName} for {m.crime?.defName}, level {ci?.security?.defName}, term {ci?.sentenceDays} for {m.termDays}): {m.Describe()}");
                    if (ci != null && ci.admittedTick != m.admittedTick) CustodyFail("shuttle", $"an admitted member's sentence clock ({ci.admittedTick}) does not start at their admission ({m.admittedTick}): made for the roster, they would be released before their window ends");
                    if (m.admittedTick < v.landedTick || m.admittedTick > Find.TickManager.TicksGame || m.dueTick != m.admittedTick + c.serviceDays * GenDate.TicksPerDay || m.dueTick == c.acceptedTick + c.serviceDays * GenDate.TicksPerDay || m.paid != fee || m.stay != (ci?.timesAdmitted ?? -1))
                        CustodyFail("shuttle", $"an admitted member's window does not start at their own admission, or their fee was not paid once ({m.Describe()}, landed {v.landedTick}, signed {c.acceptedTick})");
                }
                if (serving.Count >= 2 && serving.Select(m => m.admittedTick).Distinct().Count() != serving.Count) CustodyFail("shuttle", "two members were admitted at the same tick (the shuttle unloads one at a time): the windows were not made from each admission");
                List<LedgerEntry> fees = CustodyEntries(r);
                if (fees.Count != serving.Count || fees.Sum(e => e.amount) != fee * serving.Count || c.paidAdmission != fee * serving.Count)
                    CustodyFail("shuttle", $"the fees paid are {fees.Count} entries of {fees.Sum(e => e.amount)} (contract {c.paidAdmission}), expected {serving.Count} admissions of {fee}");
                if (c.Count(CustodyState.Pending) > 0 && c.members.Where(m => m.state == CustodyState.Pending).Any(m => m.pawn != null || m.pawnId >= 0)) CustodyFail("shuttle", "a member with no bed was made before there was a place for them");
                if (c.arrivalDeadlineTick != deadline0) CustodyFail("shuttle", "the arrival window moved for a shuttle that landed");
                EndShuttleVisit(g, map);
                if (c.Count(CustodyState.Pending) == 0)
                {
                    RPLog.Message("[RimPrison:test] custody shuttle: the whole cohort came on the first shuttle (beds changed under the check), the rest is not checked");
                    return;
                }

                // A bed free again: the next shuttle brings the rest, but lifts off with them still aboard. They stay the contract's own: pending, made once, on no list of the carried over, first on the next roster.
                int waiting = c.Count(CustodyState.Pending);
                for (int i = 0; i < waiting && i < fillers.Count; i++) fillers[i].ownership.UnclaimAll();
                // (With exactly two free beds to begin with there were no fillers to give a bed back: the bed comes free from a member who was admitted, who goes without for the rest of the check.)
                for (int i = fillers.Count; i < waiting && i - fillers.Count < serving.Count; i++) serving[i - fillers.Count].pawn?.ownership.UnclaimAll();
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v2 = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v2 || v2.phase == ShuttlePhase.Landed, 2500);
                if (g.shuttle.visit != v2 || v2.phase != ShuttlePhase.Landed)
                {
                    CustodyFail("shuttle", $"the second shuttle did not land ({v2?.Describe()})");
                    return;
                }
                int aboard = v2.rosterSize;
                ShuttleDepartIn(v2, 0);
                ShuttleTicks(() => g.shuttle.visit != v2, 2500);
                List<CustodyMember> left = c.members.Where(m => m.state == CustodyState.Pending).ToList();
                Pawn made = left.FirstOrDefault()?.pawn;
                if (aboard != waiting || left.Count != waiting || made == null || made.Spawned || g.shuttle.carriedOver.Contains(made) || !CustodyContracts.Holds(made) || !Find.WorldPawns.Contains(made))
                    CustodyFail("shuttle", $"after a shuttle lifted off with the cohort still aboard ({aboard} aboard), they are not kept as the contract's own (pending {left.Count}, made {made != null}, spawned {made?.Spawned}, carried over {g.shuttle.carriedOver.Contains(made)}, held {CustodyContracts.Holds(made)}, in the world {Find.WorldPawns.Contains(made)})");
                if (g.intake.nextInmateNumber != number0 + serving.Count + waiting) CustodyFail("shuttle", $"{g.intake.nextInmateNumber - number0} inmates were made, expected {serving.Count + waiting}");
                if (CustodyEntries(r).Count != serving.Count || c.extensionDays != extended) CustodyFail("shuttle", $"a shuttle that landed and lifted off with them aboard paid for them or moved the window ({CustodyEntries(r).Count} fees, {c.extensionDays} days added, expected {extended})");
                EndShuttleVisit(g, map);

                // The next shuttle brings the same people (nobody is made again) and they are admitted once, each paid once.
                ShuttleSystem.CallDaily(g, map);
                ShuttleVisit v3 = g.shuttle.visit;
                ShuttleTicks(() => g.shuttle.visit != v3 || v3.phase == ShuttlePhase.Landed, 2500);
                ShuttleTicks(() => g.shuttle.visit != v3 || v3.arrivalsDone, 3000);
                admitted.AddRange(c.members.Where(m => m.pawn != null).Select(m => m.pawn).Except(admitted));
                int servingNow = c.Count(CustodyState.Serving);
                CustodyMember again = c.members.FirstOrDefault(m => m.pawn == made);
                if (servingNow != serving.Count + waiting || again is not { state: CustodyState.Serving } || again.pawn?.Inmate()?.admittedTick != again.admittedTick || again.paid != fee || g.intake.nextInmateNumber != number0 + serving.Count + waiting || c.paidAdmission != fee * (serving.Count + waiting) || CustodyEntries(r).Count != serving.Count + waiting)
                    CustodyFail("shuttle", $"after the third shuttle {servingNow} are admitted (expected {serving.Count + waiting}), the carried-over member {(again == null ? "is gone" : again.state.ToString())}, fees {CustodyEntries(r).Count}, paid {c.paidAdmission}, inmates made {g.intake.nextInmateNumber - number0} ({c.Describe()})");
                if (map.mapPawns.PrisonersOfColonySpawned.Count(p => c.MemberOf(p) != null) != servingNow) CustodyFail("shuttle", "the cohort in the prison is not the cohort admitted: somebody is there twice or not at all");
                if (serving.Any(m => c.members.Contains(m) && m.paid != fee)) CustodyFail("shuttle", "a member admitted earlier was paid for again");
                EndShuttleVisit(g, map);
                RPLog.Message($"[RimPrison:test] custody shuttle: {serving.Count} of 3 admitted by the first shuttle (held by weather {storm != null}), {waiting} left aboard and kept, admitted by the third; fees {CustodyEntries(r).Sum(e => e.amount)}, {g.intake.nextInmateNumber - number0} inmates made");
            });
        }

        // ---- attendance and the bonus ---------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckCustodyAttendance(Map map)
        {
            Guarded("custody contracts: real attendance, the bonus once at the end of the window", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                CustodySetAside(g, r);
                PrisonMap pm = PrisonMap.Of(map);
                CustodyContractDef def = CustodyContracts.Def;
                ProgramDef edu = def.demandProgram;
                SecurityLevelDef level = def.levels[0];
                List<ProgramSession> sessions0 = pm.sessions;
                pm.sessions = new List<ProgramSession>();
                r.Add(() => pm.sessions = sessions0);
                CustodyContract c = CustodySign(g, map, CustodyRow(level, 150, 450), CustodyRow(level, 150, 400), CustodyRow(level, 150, 350), CustodyRow(level, 150, 300), CustodyRow(level, 150, 250));
                if (c == null)
                {
                    CustodyFail("attendance", "the contract was not signed");
                    return;
                }
                CustodyMember mA = c.members[0], mB = c.members[1], mC = c.members[2], mD = c.members[3], mE = c.members[4];
                Pawn a = CustodyAdmit(g, map, r, mA), b = CustodyAdmit(g, map, r, mB), cc = CustodyAdmit(g, map, r, mC), d = CustodyAdmit(g, map, r, mD), e = CustodyAdmit(g, map, r, mE);
                if (c.members.Any(m => m.state != CustodyState.Serving) || CustodyEntries(r).Count != 5) CustodyFail("attendance", $"five admissions did not make five serving members and five fees ({c.Describe()})");
                // Again for the same person: nothing (nobody is admitted twice, nor paid for twice).
                CustodyContracts.Notify_Admitted(g, a);
                if (CustodyEntries(r).Count != 5 || mA.paid != 150) CustodyFail("attendance", "admitting the same person again paid their fee again");
                int now = Find.TickManager.TicksGame;
                ProgramSession Session(IEnumerable<Pawn> attendees, IEnumerable<Pawn> listened, int taught = 3600)
                {
                    var s = new ProgramSession { program = edu, startTick = now - 3600, endTick = now, taughtTicks = taught, attendees = attendees.ToList() };
                    foreach (Pawn p in listened) s.listenedTicks[p] = 3600;
                    return s;
                }
                // D's window is over (not yet judged): what they sit after it is nobody's credit.
                mD.dueTick = now - 1;
                for (int i = 0; i < 4; i++)
                {
                    // The class ran with the teacher: A and E sat all four, B the first three; C was enrolled and never sat (a course that is switched on and unattended); D sat all four, after the end of their window.
                    ProgramSystem.Complete(pm, Session(new[] { a, b, cc, d, e }, new[] { a, e, d }.Concat(i < 3 ? new[] { b } : new Pawn[0])));
                }
                // And a class that ran with no teacher teaching: nobody is credited at all.
                ProgramSystem.Complete(pm, Session(new[] { cc }, new[] { cc }, taught: 0));
                if (mA.sessions != 4 || mB.sessions != 3 || mC.sessions != 0 || mD.sessions != 0 || mE.sessions != 4)
                    CustodyFail("attendance", $"sessions credited are A {mA.sessions} (4), B {mB.sessions} (3), C {mC.sessions} (0, never sat), D {mD.sessions} (0, after their window), E {mE.sessions} (4)");
                if (CustodyEntries(r).Count != 5) CustodyFail("attendance", "attending paid something before the end of anybody's window");
                // The windows end (A, B, C; E's is a day off): each is judged once, at the end of their own window and not before.
                mA.dueTick = mB.dueTick = mC.dueTick = now;
                int funds0 = g.ledger.funds, met0 = g.stats.custodyMet, missed0 = g.stats.custodyMissed, earned0 = g.stats.custodyEarned;
                CustodyContracts.Hourly(g, map);
                if (mA.state != CustodyState.Met || mB.state != CustodyState.Missed || mC.state != CustodyState.Missed || mD.state != CustodyState.Missed || mE.state != CustodyState.Serving)
                    CustodyFail("attendance", $"at the end of their windows A is {mA.state} (Met), B {mB.state} (Missed, 3 of 4), C {mC.state} (Missed, never sat), D {mD.state} (Missed), E {mE.state} (still Serving, the window runs)");
                List<LedgerEntry> bonuses = CustodyEntries(r).Where(en => en.amount > 0 && en.note != null && en.note.Contains(a.LabelShort)).ToList();
                if (g.ledger.funds - funds0 != mA.bonus || mA.bonusPaid != mA.bonus || mB.bonusPaid != 0 || mC.bonusPaid != 0 || mE.bonusPaid != 0 || c.paidBonus != mA.bonus || g.stats.custodyMet - met0 != 1 || g.stats.custodyMissed - missed0 != 3)
                    CustodyFail("attendance", $"the bonus of the one who attended was not paid exactly once, or another was paid ({g.ledger.funds - funds0} moved, A {mA.bonusPaid}, B {mB.bonusPaid}, C {mC.bonusPaid}, E {mE.bonusPaid}; met {g.stats.custodyMet - met0}, missed {g.stats.custodyMissed - missed0}; {bonuses.Count} entries naming A)");
                // Judged exactly once: the passes that follow pay nothing, and more sitting after the judgment changes nothing.
                for (int i = 0; i < 3; i++) CustodyContracts.Hourly(g, map);
                CustodyContracts.Notify_Attended(a);
                CustodyContracts.Notify_Admitted(g, a);
                if (g.ledger.funds - funds0 != mA.bonus || g.stats.custodyMet - met0 != 1 || g.stats.custodyMissed - missed0 != 3 || mA.sessions != 4 || g.custody.contract != c) CustodyFail("attendance", "a pass after the judgment paid again, judged again, counted a session after it or closed a contract that still has a window to run");
                // The last window ends: E is paid, once, and the contract is over with every member final.
                mE.dueTick = Find.TickManager.TicksGame;
                CustodyContracts.Hourly(g, map);
                CustodyContracts.Hourly(g, map);
                if (mE.state != CustodyState.Met || g.ledger.funds - funds0 != mA.bonus + mE.bonus || g.custody.contract != null || g.custody.last != c || c.outcome != CustodyOutcome.Completed || c.paidBonus != mA.bonus + mE.bonus || c.paidAdmission != 750 || g.stats.custodyEarned - earned0 != mA.bonus + mE.bonus)
                    CustodyFail("attendance", $"after the last window E is {mE.state}, {g.ledger.funds - funds0} was paid in bonuses (expected {mA.bonus + mE.bonus}), the contract is {(g.custody.contract == null ? "closed" : "still open")} as {c.outcome}");
                if (c.members.Any(m => m.pawn != null)) CustodyFail("attendance", "a finished contract still holds on to people");
                _ = (b, cc);
            });
        }

        // ---- the ways out ---------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckCustodyDepartures(Map map)
        {
            Guarded("custody contracts: leaving early forfeits the bonus and nothing more", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                CustodySetAside(g, r);
                CustodyContractDef def = CustodyContracts.Def;
                SecurityLevelDef level = def.levels[0];
                const int fee = 150, bonus = 450;
                CustodyContract c = CustodySign(g, map, Enumerable.Range(0, 8).Select(_ => CustodyRow(level, fee, bonus)).ToArray());
                if (c == null)
                {
                    CustodyFail("departures", "the contract was not signed");
                    return;
                }
                var pawns = c.members.Select(m => CustodyAdmit(g, map, r, m)).ToList();
                Pawn transferred = pawns[0], released = pawns[1], dead = pawns[2], escaped = pawns[3], gone = pawns[4], returned = pawns[5], stays = pawns[6], late = pawns[7];
                if (CustodyEntries(r).Count != 8 || CustodyEntries(r).Sum(en => en.amount) != 8 * fee) CustodyFail("departures", "eight admissions were not eight fees");
                r.Add(() =>
                {
                    foreach (Pawn p in new[] { transferred, released, late }) ShuttleRemovePawn(p);
                    g.fugitives.RemoveAll(f => f.pawn == escaped);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(escaped);
                });
                // The last window ended (with every session attended) and the hourly pass has not come to it: released now, they are judged on what they attended, and the bonus is theirs.
                CustodyMember lateMember = c.members[7];
                lateMember.sessions = c.requiredSessions;
                lateMember.dueTick = Find.TickManager.TicksGame - 1;
                int fundsBeforeLate = g.ledger.funds, forfeited0 = g.stats.custodyForfeited;
                ReleaseSystem.Release(g, late, record: false);
                if (lateMember.state != CustodyState.Met || g.ledger.funds - fundsBeforeLate != bonus || lateMember.bonusPaid != bonus) CustodyFail("departures", $"leaving after the end of their window, with the sessions attended, forfeited the bonus or was not paid once ({lateMember.state}, {g.ledger.funds - fundsBeforeLate} paid)");
                // Transferred out straight after admission (the admission payment is kept; a transfer costs more than it paid, and earns nothing).
                InmateUtility.TransferOut(transferred, transferred.Inmate());
                // Released by the warden.
                ReleaseSystem.Release(g, released, record: false);
                // Died.
                dead.Kill(null);
                // Escaped.
                EscapeUtility.Register(escaped, g, stowaway: false, escapedTick: Find.TickManager.TicksGame, map, escaped.Position, letter: false, line: false);
                // Gone by a road the events do not tell (off the map): the hourly pass sees it. Another who came back on a stay of their own.
                gone.DeSpawn();
                returned.Inmate().timesAdmitted++;
                // (The count of forfeits is from before the first of them: the four above are each counted as they leave, the two below by the hourly pass, and the one judged on its attendance is no forfeit.)
                int funds0 = g.ledger.funds, entries0 = CustodyEntries(r).Count;
                if (g.stats.custodyForfeited - forfeited0 != 4) CustodyFail("departures", $"a transfer, a release, a death and an escape were counted as {g.stats.custodyForfeited - forfeited0} forfeits, not four");
                if (c.members[0].state != CustodyState.Forfeited || c.members[0].why != "RP_CustodyEnd_Transferred" || c.members[1].state != CustodyState.Forfeited || c.members[1].why != "RP_CustodyEnd_Released"
                    || c.members[2].state != CustodyState.Forfeited || c.members[2].why != "RP_CustodyEnd_Died" || c.members[3].state != CustodyState.Forfeited || c.members[3].why != "RP_CustodyEnd_Escaped")
                    CustodyFail("departures", $"a transfer, a release, a death and an escape did not each forfeit their member at once with their reason ({string.Join(" | ", c.members.Take(4).Select(m => m.state + "/" + m.why))})");
                CustodyContracts.Hourly(g, map);
                if (c.members[4].state != CustodyState.Forfeited || c.members[4].why != "RP_CustodyEnd_Gone" || c.members[5].state != CustodyState.Forfeited || c.members[5].why != "RP_CustodyEnd_Gone" || c.members[6].state != CustodyState.Serving)
                    CustodyFail("departures", $"one who left the map and one back on a stay of their own were not forfeited by the hourly pass, or the one who stayed was ({c.members[4].state}/{c.members[4].why}, {c.members[5].state}/{c.members[5].why}, {c.members[6].state})");
                // Nothing more than the bonus: the fees stay, no entry of the contract is a charge, the count is one each, and the passes that follow change nothing.
                for (int i = 0; i < 3; i++) CustodyContracts.Hourly(g, map);
                if (c.members.Take(6).Any(m => m.paid != fee || m.bonusPaid != 0) || c.members.Take(6).Any(m => m.state != CustodyState.Forfeited) || CustodyEntries(r).Any(en => en.amount < 0) || CustodyEntries(r).Count != entries0 || c.paidAdmission != 8 * fee || c.paidBonus != bonus || g.stats.custodyForfeited - forfeited0 != 6)
                    CustodyFail("departures", $"leaving early took more than the bonus, or was counted twice ({CustodyEntries(r).Count} entries, paid {c.paidAdmission}, bonuses {c.paidBonus}, {g.stats.custodyForfeited - forfeited0} forfeited)");
                // The one who stayed attends and is paid at the end; the contract closes with six forfeited.
                CustodyMember last = c.members[6];
                for (int i = 0; i < c.requiredSessions; i++) CustodyContracts.Notify_Attended(stays);
                last.dueTick = Find.TickManager.TicksGame;
                CustodyContracts.Hourly(g, map);
                if (last.state != CustodyState.Met || g.custody.last != c || c.outcome != CustodyOutcome.Completed || c.Count(CustodyState.Forfeited) != 6 || g.ledger.funds - funds0 != bonus)
                    CustodyFail("departures", $"the one who stayed is {last.state}, the contract {(g.custody.contract == null ? "closed" : "open")} as {c.outcome} with {c.Count(CustodyState.Forfeited)} forfeited, {g.ledger.funds - funds0} paid in bonuses");
            });
        }

        /// <summary>
        /// An escape nobody sees: the count finds the cell empty hours later, perhaps after the end of the window, so the bonus is forfeit as they leave the map (the <c>ExitMap</c> hook), not when the books are opened. Driven through the real
        /// break for the edge of the map with nothing watching (the count's own scene), a day before their window ends.
        /// </summary>
        private static void CheckCustodyUnseenEscape(Map map)
        {
            Guarded("custody contracts: an escape nobody sees forfeits as they leave the map", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                CountScene t = CountScene.TryMake(map, r);
                if (t == null) return;
                CustodySetAside(g, r);
                CustodyContractDef def = CustodyContracts.Def;
                const int fee = 150, bonus = 450;
                CustodyContract c = CustodySign(g, map, CustodyRow(def.levels[0], fee, bonus));
                if (c == null)
                {
                    CustodyFail("unseen escape", "the contract was not signed");
                    return;
                }
                t.Guard();
                Pawn p = t.Inmate(t.s.cell);
                CustodyMember m = c.members[0];
                CustodyAdmit(g, map, r, m, p);
                CountSystem.StartEscaping(p, "check", EscapeWay.Walked);
                if (!CountSystem.IsHidden(p))
                {
                    RPLog.Message("[RimPrison:test] custody unseen escape: the break for the edge was seen (or no guard counts), the unseen escape is not checked");
                    return;
                }
                int escaped0 = g.stats.escaped, forfeited0 = g.stats.custodyForfeited, funds0 = g.ledger.funds;
                if (m.state != CustodyState.Serving) CustodyFail("unseen escape", $"a runner still on the map is {m.state}");
                p.ExitMap(false, Rot4.Invalid);
                HiddenEscape h = t.pm.count.HiddenOf(p);
                if (h == null || !h.Exited || g.stats.escaped != escaped0) CustodyFail("unseen escape", $"the premise: the escape is held for the count (record {h != null}, exited {h?.Exited}, escaped {escaped0} -> {g.stats.escaped})");
                if (m.state != CustodyState.Forfeited || m.why != "RP_CustodyEnd_Escaped" || g.stats.custodyForfeited != forfeited0 + 1 || m.bonusPaid != 0 || g.ledger.funds != funds0)
                    CustodyFail("unseen escape", $"an unseen escapee who left the map is {m.state}/{m.why} before the count, {g.stats.custodyForfeited - forfeited0} forfeits counted, {g.ledger.funds - funds0} moved");
                // The books are opened later, past the end of the window: once more they are not counted, nor paid.
                CustodyContracts.Notify_Departed(p, "RP_CustodyEnd_Escaped");
                CustodyContracts.Hourly(g, map);
                if (g.stats.custodyForfeited != forfeited0 + 1 || g.ledger.funds != funds0 || g.custody.last != c) CustodyFail("unseen escape", "an escape counted again, or paid, or the contract was not closed once its only member had gone");
            });
        }

        /// <summary>
        /// The sentence runs from the day the inmate steps in, as the window does: one made for a roster some days before the shuttle that admitted them is not let out before the end of their window (the term is never shorter
        /// than the window: <see cref="CustodyContractDef.minTermDays"/>).
        /// </summary>
        private static void CheckCustodySentenceClock(Map map)
        {
            Guarded("custody contracts: the sentence runs from admission, the window never outlasts it", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                CustodySetAside(g, r);
                CustodyContractDef def = CustodyContracts.Def;
                SecurityLevelDef level = def.levels[0];
                CustodyContract c = CustodySign(g, map, CustodyRow(level, 150, 450, def.minTermDays));
                if (c == null)
                {
                    CustodyFail("sentence clock", "the contract was not signed");
                    return;
                }
                CustodyMember m = c.members[0];
                Pawn p = Throwaway(map, r, level);
                CompInmate ci = p.Inmate();
                // (Made for a roster at the very beginning, and admitted much later: the oldest clock there can be.)
                ci.admittedTick = 1;
                ci.sentenceDays = m.termDays;
                CustodyAdmit(g, map, r, m, p);
                int now = Find.TickManager.TicksGame;
                if (ci.admittedTick != now || ci.admittedTick != m.admittedTick || ci.admittedTick + ci.TermDays * GenDate.TicksPerDay < m.dueTick)
                    CustodyFail("sentence clock", $"the sentence of a member admitted now runs from tick {ci.admittedTick} (their window from {m.admittedTick}, to {m.dueTick}): their term of {ci.TermDays} days ends {(ci.admittedTick + ci.TermDays * GenDate.TicksPerDay - m.dueTick) / (float)GenDate.TicksPerDay:0.0} days from the end of their window");
                // Not released at admission, as an old clock would have them.
                ReleaseSystem.ReleaseServedSentences(g, map, only: p);
                if (m.state != CustodyState.Serving || ShuttleSystem.OnTheWayOut(p)) CustodyFail("sentence clock", $"a member just admitted was let go at the next release pass ({m.state}/{m.why})");
            });
        }

        // ---- the stated pauses and cancellations ----------------------------------------------------------------------------------------------------------------------------------

        private static void CheckCustodyRules(Map map)
        {
            Guarded("custody contracts: the arrival window, a shuttle called off, the marshals' priority, the beds, the state's measures", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                // (The policy's own intake is off while the rosters are made: nobody but the cohort and the marshals' prisoner is made for them.)
                if (!ShuttleStage(map, r, "custody rules")) return;
                CustodySetAside(g, r);
                // (Nor does a roster made here send the prison's real carried-over inmates away: their list is set aside.)
                List<Pawn> carriedAside = g.shuttle.carriedOver;
                List<MarshalEntry> marshalsAside = g.shuttle.marshals;
                g.shuttle.carriedOver = new List<Pawn>();
                g.shuttle.marshals = new List<MarshalEntry>();
                r.Add(() => (g.shuttle.carriedOver, g.shuttle.marshals) = (carriedAside, marshalsAside));
                CustodyContractDef def = CustodyContracts.Def;
                SecurityLevelDef level = def.levels[0];
                int today = Ledger.CurrentDay, now = Find.TickManager.TicksGame;
                // The window ends with nobody admitted: both are dropped, unpaid, and the contract is called off (not a miss).
                CustodyContract c = CustodySign(g, map, CustodyRow(level), CustodyRow(level));
                if (c == null)
                {
                    CustodyFail("rules", "the contract was not signed");
                    return;
                }
                int funds0 = g.ledger.funds;
                CustodyContracts.Hourly(g, map);
                if (c.Count(CustodyState.Pending) != 2 || g.custody.contract != c) CustodyFail("rules", "the cohort was dropped before the arrival window was over");
                // A shuttle called off, and a roster the marshals filled, each add a day; no more than three.
                int deadline0 = c.arrivalDeadlineTick;
                CustodyContracts.Notify_ShuttleCalledOff(g);
                CustodyContracts.Notify_RosterFull(g);
                if (c.extensionDays != 2 || c.arrivalDeadlineTick != deadline0 + 2 * GenDate.TicksPerDay) CustodyFail("rules", $"a shuttle called off and a full roster added {c.extensionDays} days to the window, expected 2 ({c.arrivalDeadlineTick - deadline0} ticks)");
                for (int i = 0; i < 5; i++) CustodyContracts.Notify_ShuttleCalledOff(g);
                if (c.extensionDays != def.maxArrivalExtensionDays || c.arrivalDeadlineTick != deadline0 + def.maxArrivalExtensionDays * GenDate.TicksPerDay) CustodyFail("rules", $"the days added are not capped at {def.maxArrivalExtensionDays} ({c.extensionDays})");
                c.arrivalDeadlineTick = now;
                CustodyContracts.Hourly(g, map);
                if (c.members.Any(m => m.state != CustodyState.Dropped || m.why != "RP_CustodyEnd_Late") || g.custody.contract != null || g.custody.last != c || c.outcome != CustodyOutcome.Cancelled || g.ledger.funds != funds0 || c.paidAdmission != 0)
                    CustodyFail("rules", $"after the arrival window the cohort was not dropped unpaid and the contract called off ({c.Describe()}, funds moved {g.ledger.funds - funds0})");
                if (g.custody.nextOfferDay < today + def.offerEveryDays.min) CustodyFail("rules", $"after a contract was called off the next offer is {g.custody.nextOfferDay - today} days off");
                // With nobody waiting, a shuttle called off adds nothing to a window that is gone.
                CustodyContracts.Notify_ShuttleCalledOff(g);

                // The state's marshals' prisoners come first on the roster: a full roster leaves the cohort off, adds a day, and makes nobody.
                if (IntakeSystem.FreeBedCount(map, level) >= 3)
                {
                    c = CustodySign(g, map, CustodyRow(level), CustodyRow(level));
                    Pawn marshal = Throwaway(map, r, level, spawn: false);
                    g.shuttle.AddMarshal(marshal, MarshalKind.Returning, "x");
                    r.Add(() =>
                    {
                        g.shuttle.TakeMarshal(marshal);
                        Find.WorldPawns.ForcefullyKeptPawns.Remove(marshal);
                    });
                    int number0 = g.intake.nextInmateNumber, deadline1 = c.arrivalDeadlineTick;
                    List<Pawn> roster = IntakeSystem.GenerateRoster(g, map, out _, out _, maxCount: 1);
                    if (roster.Count != 1 || roster[0] != marshal || c.members.Any(m => m.pawnId >= 0) || g.intake.nextInmateNumber != number0 || c.extensionDays != 1 || c.arrivalDeadlineTick != deadline1 + GenDate.TicksPerDay)
                        CustodyFail("rules", $"with the roster full of the marshals' prisoner the cohort was not left off with a day added ({roster.Count} on the roster, first the marshal {roster.Count > 0 && roster[0] == marshal}, {g.intake.nextInmateNumber - number0} made, {c.extensionDays} days added)");
                    // With room for them: the marshals first and then the cohort, in the beds the level has, each made to the dossier (and the same ones again the next time: nobody is made twice).
                    List<Pawn> full = IntakeSystem.GenerateRoster(g, map, out _, out _, maxCount: 50);
                    int made = g.intake.nextInmateNumber - number0;
                    List<Pawn> again = IntakeSystem.GenerateRoster(g, map, out _, out _, maxCount: 50);
                    if (full.Count != 3 || full[0] != marshal || made != 2 || again.Count != 3 || !again.Skip(1).SequenceEqual(full.Skip(1)) || g.intake.nextInmateNumber != number0 + 2)
                        CustodyFail("rules", $"the roster with room is {full.Count} (marshal first {full.Count > 0 && full[0] == marshal}), {made} made, and asked again {again.Count} with the same cohort {again.Skip(1).SequenceEqual(full.Skip(1))}");
                    foreach (Pawn p in full.Skip(1)) r.Add(() => ShuttleRemovePawn(p));
                    g.custody.contract = null;
                    // (The marshals' prisoner goes off the list now: the next roster here must be the cohort's and nobody else's.)
                    g.shuttle.TakeMarshal(marshal);
                    Find.WorldPawns.ForcefullyKeptPawns.Remove(marshal);
                }
                else RPLog.Message($"[RimPrison:test] custody rules: fewer than three free {level.label} beds, the marshals' priority is not checked");

                // A level with no free bed (and overcrowding allowed): the member waits; nobody is crowded in for the contract.
                SecurityLevelDef bedless = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(l => !l.wingOnly && IntakeSystem.FreeBedCount(map, l) == 0);
                bool overcrowding0 = g.intake.allowOvercrowding;
                if (bedless != null)
                {
                    g.intake.allowOvercrowding = true;
                    r.Add(() => g.intake.allowOvercrowding = overcrowding0);
                    c = CustodySign(g, map, CustodyRow(bedless));
                    int number1 = g.intake.nextInmateNumber;
                    List<Pawn> roster = IntakeSystem.GenerateRoster(g, map, out _, out _, maxCount: 50);
                    if (c.members[0].pawnId >= 0 || roster.Count != 0 || g.intake.nextInmateNumber != number1)
                        CustodyFail("rules", $"a member of a level with no free bed was brought in over the beds ({bedless.defName}, overcrowding allowed)");
                    g.custody.contract = null;
                }
                else RPLog.Message("[RimPrison:test] custody rules: every level has a free bed, the no-overcrowding rule is not checked");

                // The state steps in: the open offer is withdrawn, the cohort not yet admitted is called off (unpaid), the admitted go on; no offer, no signing, no roster for the cohort while it lasts.
                c = CustodySign(g, map, CustodyRow(level), CustodyRow(level), CustodyRow(level));
                CustodyMember serving = c.members[0];
                Pawn servingPawn = CustodyAdmit(g, map, r, serving);
                g.custody.offer = new CustodyOffer { id = 77, def = def, createdTick = now, expiresTick = now + 100000, rows = new List<CustodyMember> { CustodyRow(level) } };
                int funds1 = g.ledger.funds, started0 = g.stats.measuresStarted;
                // (The state steps in as it does at midnight: the real start of the measures, with its overseer and its letter, which are taken away again.)
                var oversight = new StateOversight();
                g.story.oversight = oversight;
                r.Add(() =>
                {
                    g.stats.measuresStarted = started0;
                    if (oversight.overseer is { Destroyed: false } overseer)
                    {
                        g.home.Remove(overseer);
                        overseer.Destroy(DestroyMode.Vanish);
                    }
                    StaffCareers.InvalidateLeads();
                });
                SpecialMeasures.Start(g, map, oversight, MeasuresCause.Loan);
                if (g.custody.offer != null || c.members[1].state != CustodyState.Dropped || c.members[2].state != CustodyState.Dropped || c.members[1].why != "RP_CustodyEnd_Measures" || serving.state != CustodyState.Serving || g.custody.contract != c || g.ledger.funds != funds1)
                    CustodyFail("rules", $"the state's measures did not withdraw the offer and call off the cohort not yet admitted, or touched the admitted ({c.Describe()})");
                g.custody.contract = null;
                if (CustodyContracts.NoOfferBecause(g, map) != "RP_CustodyNoOfferMeasures") CustodyFail("rules", $"under the state's measures the reason no offer is made is {CustodyContracts.NoOfferBecause(g, map)}");
                CustodyOffer stale = new CustodyOffer { id = 78, def = def, rows = new List<CustodyMember> { CustodyRow(level) } };
                g.custody.offer = stale;
                if (CustodyContracts.Accept(g, map, 78) != null || g.custody.contract != null) CustodyFail("rules", "a contract was signed under the state's measures");
                g.custody.offer = null;
                // A member waiting when the measures are on: no roster brings them.
                g.custody.contract = c;
                c.members[1].state = CustodyState.Pending;
                c.members[1].why = null;
                int number2 = g.intake.nextInmateNumber;
                IntakeSystem.GenerateRoster(g, map, out _, out _, maxCount: 50);
                if (c.members[1].pawnId >= 0 || g.intake.nextInmateNumber != number2) CustodyFail("rules", "the shuttle's roster made a member of the cohort under the state's measures");
                // The midnight pass calls off whoever is still waiting while the measures last (a member aboard a shuttle when they began, say).
                CustodyContracts.Daily(g, map);
                if (c.members[1].state != CustodyState.Dropped || c.members[1].why != "RP_CustodyEnd_Measures" || serving.state != CustodyState.Serving || g.custody.offer != null)
                    CustodyFail("rules", $"the midnight pass under the state's measures did not call off a member still waiting, or touched the admitted ({c.members[1].state}/{c.members[1].why}, {serving.state})");
                g.story.oversight = null;
                g.custody.contract = null;
                _ = servingPawn;
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------------------------------------------------

        private static string CustodyDescribe(PrisonGame g) =>
            $"{g.custody.Describe()}|{g.stats.custodyOffers}/{g.stats.custodyAccepted}/{g.stats.custodyAdmitted}/{g.stats.custodyMet}/{g.stats.custodyEarned}/{g.stats.custodyMissed}/{g.stats.custodyForfeited}";

        private static bool custodySeeded;
        private static string custodySeedDescribed;
        private static CustodyBook custodyBefore;
        private static int[] custodyStatsBefore;
        private static Pawn custodyWorldPawn;

        private static CustodyMember CopyCustodyMember(CustodyMember m) => new CustodyMember
        {
            level = m.level, crime = m.crime, termDays = m.termDays, fee = m.fee, bonus = m.bonus, state = m.state, pawn = m.pawn, pawnId = m.pawnId, stay = m.stay, admittedTick = m.admittedTick, dueTick = m.dueTick, endTick = m.endTick,
            sessions = m.sessions, paid = m.paid, bonusPaid = m.bonusPaid, name = m.name, why = m.why,
        };

        private static CustodyContract CopyCustodyContract(CustodyContract c) => c == null ? null : new CustodyContract
        {
            id = c.id, def = c.def, mapId = c.mapId, acceptedTick = c.acceptedTick, arrivalDeadlineTick = c.arrivalDeadlineTick, extensionDays = c.extensionDays, serviceDays = c.serviceDays, requiredSessions = c.requiredSessions,
            members = c.members.Select(CopyCustodyMember).ToList(), paidAdmission = c.paidAdmission, paidBonus = c.paidBonus, outcome = c.outcome, endTick = c.endTick,
        };

        /// <summary>The game's own book as it was before the seed (a copy: the seed replaces the book, and the pawns the copy names are found again after the reload by their numbers).</summary>
        private static CustodyBook CopyCustodyBook(CustodyBook b) => new CustodyBook
        {
            nextId = b.nextId, nextOfferDay = b.nextOfferDay, contract = CopyCustodyContract(b.contract), last = CopyCustodyContract(b.last),
            offer = b.offer == null ? null : new CustodyOffer { id = b.offer.id, def = b.offer.def, mapId = b.offer.mapId, createdTick = b.offer.createdTick, expiresTick = b.offer.expiresTick, rows = b.offer.rows.Select(CopyCustodyMember).ToList() },
        };

        /// <summary>
        /// Saveload plan, before the snapshot: a book with an open offer, a contract half way through (one member whose window ends in some hours with the sessions attended, one serving with some sessions, one forfeited, one dropped,
        /// one pending with a made pawn kept in the world, one pending with no one made yet, an extended arrival window, money paid) and the last contract finished, with every field off its default.
        /// </summary>
        private static void SeedCustodyForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null && p.IsPrisonerOfColony && !ShuttleSystem.OnTheWayOut(p)).OrderBy(p => p.thingIDNumber).Take(2).ToList();
            CustodyContractDef def = CustodyContracts.Def;
            if (inmates.Count < 2 || def == null)
            {
                RPLog.Message("[RimPrison:test] saveload custody contracts: not seeded (needs two inmates)");
                return;
            }
            SecurityLevelDef min = def.levels[0], med = def.levels[def.levels.Count - 1];
            custodyBefore = CopyCustodyBook(g.custody);
            custodyStatsBefore = new[] { g.stats.custodyOffers, g.stats.custodyAccepted, g.stats.custodyAdmitted, g.stats.custodyMet, g.stats.custodyEarned, g.stats.custodyMissed, g.stats.custodyForfeited };
            int now = Find.TickManager.TicksGame;
            // A made pawn waiting in the world for the next shuttle (kept, as the shuttle keeps the ones it could not unload).
            custodyWorldPawn = InmateGenerator.Generate(min, 90500, null, min.crimes.FirstOrDefault());
            Find.WorldPawns.PassToWorld(custodyWorldPawn, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
            Pawn a = inmates[0], b = inmates[1];
            var contract = new CustodyContract
            {
                id = 5, def = def, mapId = map.uniqueID, acceptedTick = now - 6 * GenDate.TicksPerDay, arrivalDeadlineTick = now + GenDate.TicksPerDay, extensionDays = 2, serviceDays = 7, requiredSessions = 4, paidAdmission = 600, paidBonus = 0,
                members = new List<CustodyMember>
                {
                    new CustodyMember { level = min, crime = min.crimes.LastOrDefault(), termDays = 14, fee = 150, bonus = 450, state = CustodyState.Serving, pawn = a, pawnId = a.thingIDNumber, stay = a.Inmate().timesAdmitted, admittedTick = now - 6 * GenDate.TicksPerDay, dueTick = now + 6 * GenDate.TicksPerHour, sessions = 5, paid = 150, name = a.LabelShort },
                    new CustodyMember { level = med, crime = med.crimes.FirstOrDefault(), termDays = 11, fee = 250, bonus = 750, state = CustodyState.Serving, pawn = b, pawnId = b.thingIDNumber, stay = b.Inmate().timesAdmitted, admittedTick = now - 2 * GenDate.TicksPerDay, dueTick = now + 5 * GenDate.TicksPerDay, sessions = 2, paid = 250, name = b.LabelShort },
                    new CustodyMember { level = min, crime = min.crimes.FirstOrDefault(), termDays = 13, fee = 150, bonus = 450, state = CustodyState.Forfeited, pawnId = 4242, stay = 2, admittedTick = now - 3 * GenDate.TicksPerDay, dueTick = now + 4 * GenDate.TicksPerDay, endTick = now - GenDate.TicksPerDay, sessions = 1, paid = 150, name = "Zforfeit", why = "RP_CustodyEnd_Transferred" },
                    new CustodyMember { level = med, crime = med.crimes.LastOrDefault(), termDays = 12, fee = 250, bonus = 750, state = CustodyState.Dropped, endTick = now - 100, why = "RP_CustodyEnd_Measures" },
                    new CustodyMember { level = min, crime = min.crimes.FirstOrDefault(), termDays = 10, fee = 150, bonus = 450, state = CustodyState.Pending, pawn = custodyWorldPawn, pawnId = custodyWorldPawn.thingIDNumber, name = custodyWorldPawn.LabelShort },
                    new CustodyMember { level = med, crime = med.crimes.FirstOrDefault(), termDays = 15, fee = 250, bonus = 750 },
                },
            };
            var lastContract = new CustodyContract
            {
                id = 3, def = def, mapId = map.uniqueID, acceptedTick = now - 30 * GenDate.TicksPerDay, arrivalDeadlineTick = now - 26 * GenDate.TicksPerDay, serviceDays = 7, requiredSessions = 4, paidAdmission = 300, paidBonus = 450, outcome = CustodyOutcome.Completed, endTick = now - 15 * GenDate.TicksPerDay,
                members = new List<CustodyMember>
                {
                    new CustodyMember { level = min, crime = min.crimes.FirstOrDefault(), termDays = 12, fee = 150, bonus = 450, state = CustodyState.Met, pawnId = 777, stay = 1, admittedTick = now - 28 * GenDate.TicksPerDay, dueTick = now - 21 * GenDate.TicksPerDay, endTick = now - 21 * GenDate.TicksPerDay, sessions = 4, paid = 150, bonusPaid = 450, name = "Zmet" },
                    new CustodyMember { level = min, crime = min.crimes.FirstOrDefault(), termDays = 12, fee = 150, bonus = 450, state = CustodyState.Missed, pawnId = 778, stay = 1, admittedTick = now - 28 * GenDate.TicksPerDay, dueTick = now - 21 * GenDate.TicksPerDay, endTick = now - 21 * GenDate.TicksPerDay, sessions = 3, paid = 150, name = "Zmissed" },
                },
            };
            g.custody = new CustodyBook
            {
                nextId = 9, nextOfferDay = Ledger.CurrentDay + 8, contract = contract, last = lastContract,
                offer = new CustodyOffer { id = 8, def = def, mapId = map.uniqueID, createdTick = now - 1000, expiresTick = now + 40000, rows = new List<CustodyMember> { new CustodyMember { level = min, crime = min.crimes.FirstOrDefault(), termDays = 12, fee = 150, bonus = 450 }, new CustodyMember { level = med, crime = med.crimes.LastOrDefault(), termDays = 16, fee = 250, bonus = 750 } } },
            };
            g.stats.custodyOffers = 4;
            g.stats.custodyAccepted = 3;
            g.stats.custodyAdmitted = 7;
            g.stats.custodyMet = 2;
            g.stats.custodyEarned = 900;
            g.stats.custodyMissed = 1;
            g.stats.custodyForfeited = 1;
            custodySeeded = true;
            custodySeedDescribed = CustodyDescribe(g);
            RPLog.Message($"[RimPrison:test] saveload custody contracts: {custodySeedDescribed.Length} characters seeded");
        }

        /// <summary>
        /// After the reload: the book came back whole (the snapshot compared it too), and the passes that follow neither pay nor make anything twice. The member whose window ended with the sessions attended is paid once (the bonus is one entry however many passes follow); the one
        /// still serving, the forfeited and the dropped are not touched; the pending member keeps the pawn that was made for them (and no one is made for the other); a member admitted again is not paid again. Then the plan's seeds go.
        /// </summary>
        private static void CheckCustodyAfterReload(Map map)
        {
            if (!custodySeeded) return;
            PrisonGame g = PrisonGame.Instance;
            string after = CustodyDescribe(g);
            bool whole = after == custodySeedDescribed && g.custody.contract is { } c0 && c0.members.Count == 6 && g.custody.last is { members.Count: 2 } && g.custody.offer is { rows.Count: 2 };
            RPLog.Message($"[RimPrison:test] saveload custody contracts: {(whole ? "intact" : "changed")}");
            if (!whole) reloadProblems.Add($"the custody book changed in the round trip: {custodySeedDescribed} -> {after}");
            CustodyContract c = g.custody.contract;
            if (c != null)
            {
                using (new Rollback(map))
                {
                    int funds0 = g.ledger.funds, met0 = g.stats.custodyMet, number0 = g.intake.nextInmateNumber;
                    CustodyMember judged = c.members[0], serving = c.members[1], pending = c.members[4], unmade = c.members[5];
                    Pawn pendingPawn = pending.pawn;
                    // (Its window was seeded to end in some hours, so that no pass between the seed and the save judged it: the window ends now, after the reload.)
                    judged.dueTick = Find.TickManager.TicksGame;
                    for (int i = 0; i < 3; i++) CustodyContracts.Hourly(g, map);
                    if (judged.state != CustodyState.Met || g.ledger.funds - funds0 != judged.bonus || g.stats.custodyMet != met0 + 1 || judged.bonusPaid != judged.bonus || c.paidBonus != judged.bonus)
                        reloadProblems.Add($"custody contracts: after the reload the member whose window had ended was {judged.state} and {g.ledger.funds - funds0} was paid (expected {judged.bonus} once, {g.stats.custodyMet - met0} judged met)");
                    if (serving.state != CustodyState.Serving || c.members[2].state != CustodyState.Forfeited || c.members[3].state != CustodyState.Dropped || g.custody.contract != c)
                        reloadProblems.Add($"custody contracts: after the reload a member who was serving, forfeited or dropped was changed by the passes ({serving.state}, {c.members[2].state}, {c.members[3].state})");
                    CustodyContracts.Notify_Admitted(g, judged.pawn);
                    if (g.ledger.funds - funds0 != judged.bonus) reloadProblems.Add("custody contracts: admitting a member again after the reload paid their fee again");
                    if (pending.state != CustodyState.Pending || pending.pawn != pendingPawn || pendingPawn == null || !CustodyContracts.Holds(pendingPawn) || !Find.WorldPawns.Contains(pendingPawn) || unmade.pawnId >= 0 || g.intake.nextInmateNumber != number0)
                        reloadProblems.Add($"custody contracts: after the reload the pending member lost their pawn, or the member with none had one made ({pending.state}, pawn {pendingPawn != null}, kept in the world {pendingPawn != null && Find.WorldPawns.Contains(pendingPawn)}, unmade {unmade.pawnId})");
                    // A save taken across an admission: the pawn that was made before it, kept in the world and loaded again, steps in after the reload: paid once, their window and their sentence start now.
                    if (pending.pawn is { } stepsIn && stepsIn.Inmate() is { } stepsInComp)
                    {
                        int fundsBeforeAdmission = g.ledger.funds, admittedBefore = g.stats.custodyAdmitted, now = Find.TickManager.TicksGame;
                        CustodyContracts.Notify_Admitted(g, stepsIn);
                        CustodyContracts.Notify_Admitted(g, stepsIn);
                        if (pending.state != CustodyState.Serving || g.ledger.funds - fundsBeforeAdmission != pending.fee || pending.paid != pending.fee || g.stats.custodyAdmitted != admittedBefore + 1 || pending.admittedTick != now || pending.dueTick != now + c.serviceDays * GenDate.TicksPerDay || stepsInComp.admittedTick != now)
                            reloadProblems.Add($"custody contracts: a member admitted after the reload was {pending.state}, paid {g.ledger.funds - fundsBeforeAdmission} (fee {pending.fee}), counted {g.stats.custodyAdmitted - admittedBefore}, with a window to {pending.dueTick} and a sentence from {stepsInComp.admittedTick} (now {now})");
                    }
                }
            }
            // (The pawn made for the pending member, as it came back from the save: given back to the world's discard.)
            if (c?.members.Count > 4 && c.members[4].pawn is { } leftover && Find.WorldPawns.Contains(leftover)) Find.WorldPawns.RemoveAndDiscardPawnViaGC(leftover);
            custodyWorldPawn = null;
            PrisonStats st = g.stats;
            // (The game's own book back: the copy made before the seed, with the pawns it names found again among those that were loaded.)
            Pawn Loaded(Pawn old) => old == null ? null : map.mapPawns.AllPawnsSpawned.Concat(g.OffSite).Concat(Find.WorldPawns.AllPawnsAliveOrDead).FirstOrDefault(x => x.thingIDNumber == old.thingIDNumber);
            foreach (CustodyMember member in new[] { custodyBefore.contract, custodyBefore.last }.Where(x => x != null).SelectMany(x => x.members).Concat(custodyBefore.offer?.rows ?? new List<CustodyMember>()))
                member.pawn = Loaded(member.pawn);
            g.custody = custodyBefore;
            custodyBefore = null;
            (st.custodyOffers, st.custodyAccepted, st.custodyAdmitted, st.custodyMet, st.custodyEarned, st.custodyMissed, st.custodyForfeited) =
                (custodyStatsBefore[0], custodyStatsBefore[1], custodyStatsBefore[2], custodyStatsBefore[3], custodyStatsBefore[4], custodyStatsBefore[5], custodyStatsBefore[6]);
            custodySeeded = false;
        }
    }
}
