using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>Cumulative prison statistics (the report card reads these).</summary>
    public class PrisonStats : IExposable
    {
        public int admitted;
        public int released;
        public int escaped;
        public int deaths;
        public int assaults;
        public int reoffended;
        public int riots;
        public int misconducts;
        public int punishments;
        public int contrabandSmuggled;
        public int contrabandFound;
        public int visits;
        public int paroled;
        public int programsPassed;
        public int gangRecruits;
        public int tips;
        public int tunnelsDug;
        public int tunnelsFound;
        public int policeCallouts;
        public int transferredOut;
        public int grantsCompleted;
        public int violentDeaths;
        /// <summary>Inmates shot dead by armed staff under the armed response policy (subset of deaths, not of violentDeaths).</summary>
        public int shotByGuards;
        public int dogAlerts;
        public int prayers;
        public int therapySessions;
        public int hungerStrikes;
        public int drugDeals;
        public int extortions;
        public int lawsuits;
        public int appealsFiled;
        public int demandsMet;
        public int recaptured;
        public int promoted;
        public int riotJoined;
        public int stashesFound;
        public int visitorSearches;
        public int visitorContrabandFound;
        public int visitorsWavedThrough;
        public int phoneCalls;
        public int phoneTapIntel;
        public int friendships;
        public int storyBeats;
        public int staffInjured;
        public int commendations;
        public int staffWentHome;
        public int paroleRecommended;
        /// <summary>Hearings held in the hearing room, and parolees sent back for breaking their terms.</summary>
        public int paroleHearings, paroleViolations;
        public int reoffendersReturned;
        public int murders;
        public int inquiries;
        public int gangChallenges;
        public int staffBurnedOut;
        public int staffRetired;
        public int promotions;
        public int promotionsDeclined;
        public int resignations;
        public int overdoses;
        public int truces;
        public int trucesBroken;
        public int rookieMistakes;
        public int fluCases;
        public int exonerations;
        public int inspections;
        public int blackouts;
        public int weatherEvents;
        public int medicalEmergencies;
        public int foodScares;
        public int contractsMet, contractsMissed;
        /// <summary>Custody contracts (see <see cref="CustodyContracts"/>): offers made, signed, inmates admitted under one, bonuses earned (and their money), judged short of the sessions, and forfeited by leaving early.</summary>
        public int custodyOffers, custodyAccepted, custodyAdmitted, custodyMet, custodyEarned, custodyMissed, custodyForfeited;
        /// <summary>Supervision after release (see <see cref="Supervision"/>): reviews begun at the departure, outcomes seen (stayed out, rearrested), setbacks reported, bounded help given, lodging paid for and job interviews held.</summary>
        public int supervisionStarted, supervisionStayedOut, supervisionRearrested, supervisionSetbacks, supervisionHelped, supervisionLodged, supervisionInterviews;
        public int mentorships;
        public int fires;
        public int staffTrained;
        public int remissionDays;
        /// <summary>The sheet's answers (see <see cref="Adjudication"/>): cases decided, the days added to sentences (after the appeals), remission wiped, charges brought, days struck out on appeal.</summary>
        public int adjudicated, daysAddedGiven, remissionWiped, chargesBrought, daysStruckOut;
        public int staffBreaks;
        public int amnestyItems;
        public int detoxHours;
        public int gotClean;
        public int rescuesRemembered;
        public int formerNews;
        public int staffDeaths;
        /// <summary>Bodies the state shuttle took home from the morgue, bodies the workmen buried in the plot, and the families' letters that came (see <see cref="CondolenceSystem"/>, <see cref="Burial"/>).</summary>
        public int bodiesSent, buried, condolences;
        public int cellMoves;
        public int lightSanctions;
        public int familyDays;
        public int informantsRecruited;
        public int feuds;
        public int feudsMediated;
        public int feudFlashpoints;
        public int gangWars;
        public int gangWarsEnded;
        public int hostageCrises;
        public int hostagesFreed;
        public int hostagesKilled;
        /// <summary>The warden on the floor: canteens addressed, wings walked, words with inmates, blows they took (see <see cref="WardenSystem"/>).</summary>
        public int wardenAddresses, wardenWalks, wardenWords, wardenStruck;
        public int smugglersDetained;
        public int smugglersFled;
        public int arrestsMade;
        public int staffApproached;
        public int bribesRefused;
        public int staffDeliveries;
        public int staffSearches;
        public int staffCaught;
        public int iaCases;
        public int staffDismissed;
        public int staffArrested;
        public int doubleAgentTips;
        /// <summary>Unit officers' tips about their wing (see <see cref="UnitOfficers"/>).</summary>
        public int unitTips;
        public int demoted;
        public int riotsNegotiated;
        public int intakeSearches;
        public int inmateMeals;
        public int intakeUnsearched;
        public int demandsBroken;
        public int staffStrikes;
        public int executions;
        public int wrongfulExecutions;
        public int commuted;
        public int commissarySales;
        public int bribes;
        public int itemsExported;
        public int lettersDelivered;
        public int lettersWritten;
        public int mailContrabandFound;
        public int mailContrabandMissed;
        public int debtsFormed;
        public int debtsPaid;
        public int debtBeatings;
        public int cardGames;
        public int workouts;
        public int booksDelivered;
        public int booksRead;
        public int bookContraband;
        /// <summary>The state's oversight: special measures begun, lifted and failed (ending in its security chief), flagship status earned, legendary transfers received.</summary>
        public int measuresStarted, measuresLifted, measuresFailed, flagshipsEarned, legendsArrived;
        /// <summary>Smuggling rings broken whole, taken out in part, the links the warden has found, and members turned (see <see cref="RingSystem"/>).</summary>
        public int ringsBusted, ringsPartial, ringLinksFound, ringMembersTurned;
        /// <summary>Murder trials held, their verdicts, and witnesses who took back what they said (see <see cref="Trials"/>).</summary>
        public int trials, convictions, acquittals, witnessesRecanted;
        /// <summary>Complaints against officers that reached the warden, and how they ended (see <see cref="ComplaintSystem"/>).</summary>
        public int complaintsFiled, complaintsUpheld, complaintsDismissed, complaintsFalseFound;
        /// <summary>The state shuttle: landings, the cargo runs among them, the days one was called off, landings on clear ground instead of the pad.</summary>
        public int shuttleLandings, cargoRuns, shuttlesCancelled, shuttlesOffPad;
        /// <summary>Inmates who tried to stow away (caught at the ramp, or running for the shuttle) and those who got away on it; crates searched on the pad.</summary>
        public int stowawayAttempts, stowawaysCaught, stowawaysAway, cargoContrabandFound;
        /// <summary>Parcels of land bought, and the cells in them (see <see cref="PrisonGrounds"/>).</summary>
        public int landParcels, landCells;
        /// <summary>Drug tests the doctors completed, and the ones that came back positive (see <see cref="DrugTesting"/>).</summary>
        public int drugTests, drugPositives;

        public void ExposeData()
        {
            Scribe_Values.Look(ref admitted, "admitted");
            Scribe_Values.Look(ref released, "released");
            Scribe_Values.Look(ref escaped, "escaped");
            Scribe_Values.Look(ref deaths, "deaths");
            Scribe_Values.Look(ref assaults, "assaults");
            Scribe_Values.Look(ref reoffended, "reoffended");
            Scribe_Values.Look(ref riots, "riots");
            Scribe_Values.Look(ref misconducts, "misconducts");
            Scribe_Values.Look(ref punishments, "punishments");
            Scribe_Values.Look(ref contrabandSmuggled, "contrabandSmuggled");
            Scribe_Values.Look(ref contrabandFound, "contrabandFound");
            Scribe_Values.Look(ref visits, "visits");
            Scribe_Values.Look(ref paroled, "paroled");
            Scribe_Values.Look(ref programsPassed, "programsPassed");
            Scribe_Values.Look(ref gangRecruits, "gangRecruits");
            Scribe_Values.Look(ref tips, "tips");
            Scribe_Values.Look(ref tunnelsDug, "tunnelsDug");
            Scribe_Values.Look(ref tunnelsFound, "tunnelsFound");
            Scribe_Values.Look(ref policeCallouts, "policeCallouts");
            Scribe_Values.Look(ref transferredOut, "transferredOut");
            Scribe_Values.Look(ref grantsCompleted, "grantsCompleted");
            Scribe_Values.Look(ref violentDeaths, "violentDeaths");
            Scribe_Values.Look(ref shotByGuards, "shotByGuards");
            Scribe_Values.Look(ref dogAlerts, "dogAlerts");
            Scribe_Values.Look(ref prayers, "prayers");
            Scribe_Values.Look(ref therapySessions, "therapySessions");
            Scribe_Values.Look(ref hungerStrikes, "hungerStrikes");
            Scribe_Values.Look(ref drugDeals, "drugDeals");
            Scribe_Values.Look(ref extortions, "extortions");
            Scribe_Values.Look(ref lawsuits, "lawsuits");
            Scribe_Values.Look(ref appealsFiled, "appealsFiled");
            Scribe_Values.Look(ref demandsMet, "demandsMet");
            Scribe_Values.Look(ref recaptured, "recaptured");
            Scribe_Values.Look(ref promoted, "promoted");
            Scribe_Values.Look(ref riotJoined, "riotJoined");
            Scribe_Values.Look(ref stashesFound, "stashesFound");
            Scribe_Values.Look(ref visitorSearches, "visitorSearches");
            Scribe_Values.Look(ref visitorContrabandFound, "visitorContrabandFound");
            Scribe_Values.Look(ref visitorsWavedThrough, "visitorsWavedThrough");
            Scribe_Values.Look(ref phoneCalls, "phoneCalls");
            Scribe_Values.Look(ref phoneTapIntel, "phoneTapIntel");
            Scribe_Values.Look(ref friendships, "friendships");
            Scribe_Values.Look(ref storyBeats, "storyBeats");
            Scribe_Values.Look(ref staffInjured, "staffInjured");
            Scribe_Values.Look(ref commendations, "commendations");
            Scribe_Values.Look(ref staffWentHome, "staffWentHome");
            Scribe_Values.Look(ref paroleRecommended, "paroleRecommended");
            Scribe_Values.Look(ref paroleHearings, "paroleHearings");
            Scribe_Values.Look(ref paroleViolations, "paroleViolations");
            Scribe_Values.Look(ref reoffendersReturned, "reoffendersReturned");
            Scribe_Values.Look(ref murders, "murders");
            Scribe_Values.Look(ref inquiries, "inquiries");
            Scribe_Values.Look(ref gangChallenges, "gangChallenges");
            Scribe_Values.Look(ref staffBurnedOut, "staffBurnedOut");
            Scribe_Values.Look(ref staffRetired, "staffRetired");
            Scribe_Values.Look(ref promotions, "promotions");
            Scribe_Values.Look(ref promotionsDeclined, "promotionsDeclined");
            Scribe_Values.Look(ref resignations, "resignations");
            Scribe_Values.Look(ref overdoses, "overdoses");
            Scribe_Values.Look(ref truces, "truces");
            Scribe_Values.Look(ref trucesBroken, "trucesBroken");
            Scribe_Values.Look(ref rookieMistakes, "rookieMistakes");
            Scribe_Values.Look(ref fluCases, "fluCases");
            Scribe_Values.Look(ref exonerations, "exonerations");
            Scribe_Values.Look(ref inspections, "inspections");
            Scribe_Values.Look(ref blackouts, "blackouts");
            Scribe_Values.Look(ref weatherEvents, "weatherEvents");
            Scribe_Values.Look(ref medicalEmergencies, "medicalEmergencies");
            Scribe_Values.Look(ref foodScares, "foodScares");
            Scribe_Values.Look(ref contractsMet, "contractsMet");
            Scribe_Values.Look(ref custodyOffers, "custodyOffers");
            Scribe_Values.Look(ref custodyAccepted, "custodyAccepted");
            Scribe_Values.Look(ref custodyAdmitted, "custodyAdmitted");
            Scribe_Values.Look(ref custodyMet, "custodyMet");
            Scribe_Values.Look(ref custodyEarned, "custodyEarned");
            Scribe_Values.Look(ref custodyMissed, "custodyMissed");
            Scribe_Values.Look(ref custodyForfeited, "custodyForfeited");
            Scribe_Values.Look(ref supervisionStarted, "supervisionStarted");
            Scribe_Values.Look(ref supervisionStayedOut, "supervisionStayedOut");
            Scribe_Values.Look(ref supervisionRearrested, "supervisionRearrested");
            Scribe_Values.Look(ref supervisionSetbacks, "supervisionSetbacks");
            Scribe_Values.Look(ref supervisionHelped, "supervisionHelped");
            Scribe_Values.Look(ref supervisionLodged, "supervisionLodged");
            Scribe_Values.Look(ref supervisionInterviews, "supervisionInterviews");
            Scribe_Values.Look(ref contractsMissed, "contractsMissed");
            Scribe_Values.Look(ref mentorships, "mentorships");
            Scribe_Values.Look(ref fires, "fires");
            Scribe_Values.Look(ref staffTrained, "staffTrained");
            Scribe_Values.Look(ref remissionDays, "remissionDays");
            Scribe_Values.Look(ref adjudicated, "adjudicated");
            Scribe_Values.Look(ref daysAddedGiven, "daysAddedGiven");
            Scribe_Values.Look(ref remissionWiped, "remissionWiped");
            Scribe_Values.Look(ref chargesBrought, "chargesBrought");
            Scribe_Values.Look(ref daysStruckOut, "daysStruckOut");
            Scribe_Values.Look(ref staffBreaks, "staffBreaks");
            Scribe_Values.Look(ref amnestyItems, "amnestyItems");
            Scribe_Values.Look(ref detoxHours, "detoxHours");
            Scribe_Values.Look(ref gotClean, "gotClean");
            Scribe_Values.Look(ref rescuesRemembered, "rescuesRemembered");
            Scribe_Values.Look(ref formerNews, "formerNews");
            Scribe_Values.Look(ref staffDeaths, "staffDeaths");
            Scribe_Values.Look(ref bodiesSent, "bodiesSent");
            Scribe_Values.Look(ref buried, "buried");
            Scribe_Values.Look(ref condolences, "condolences");
            Scribe_Values.Look(ref cellMoves, "cellMoves");
            Scribe_Values.Look(ref lightSanctions, "lightSanctions");
            Scribe_Values.Look(ref familyDays, "familyDays");
            Scribe_Values.Look(ref informantsRecruited, "informantsRecruited");
            Scribe_Values.Look(ref feuds, "feuds");
            Scribe_Values.Look(ref feudsMediated, "feudsMediated");
            Scribe_Values.Look(ref feudFlashpoints, "feudFlashpoints");
            Scribe_Values.Look(ref gangWars, "gangWars");
            Scribe_Values.Look(ref gangWarsEnded, "gangWarsEnded");
            Scribe_Values.Look(ref hostageCrises, "hostageCrises");
            Scribe_Values.Look(ref hostagesFreed, "hostagesFreed");
            Scribe_Values.Look(ref hostagesKilled, "hostagesKilled");
            Scribe_Values.Look(ref wardenAddresses, "wardenAddresses");
            Scribe_Values.Look(ref wardenWalks, "wardenWalks");
            Scribe_Values.Look(ref wardenWords, "wardenWords");
            Scribe_Values.Look(ref wardenStruck, "wardenStruck");
            Scribe_Values.Look(ref smugglersDetained, "smugglersDetained");
            Scribe_Values.Look(ref smugglersFled, "smugglersFled");
            Scribe_Values.Look(ref arrestsMade, "arrestsMade");
            Scribe_Values.Look(ref staffApproached, "staffApproached");
            Scribe_Values.Look(ref bribesRefused, "bribesRefused");
            Scribe_Values.Look(ref staffDeliveries, "staffDeliveries");
            Scribe_Values.Look(ref staffSearches, "staffSearches");
            Scribe_Values.Look(ref staffCaught, "staffCaught");
            Scribe_Values.Look(ref iaCases, "iaCases");
            Scribe_Values.Look(ref staffDismissed, "staffDismissed");
            Scribe_Values.Look(ref staffArrested, "staffArrested");
            Scribe_Values.Look(ref doubleAgentTips, "doubleAgentTips");
            Scribe_Values.Look(ref unitTips, "unitTips");
            Scribe_Values.Look(ref demoted, "demoted");
            Scribe_Values.Look(ref riotsNegotiated, "riotsNegotiated");
            Scribe_Values.Look(ref intakeSearches, "intakeSearches");
            Scribe_Values.Look(ref inmateMeals, "inmateMeals");
            Scribe_Values.Look(ref intakeUnsearched, "intakeUnsearched");
            Scribe_Values.Look(ref demandsBroken, "demandsBroken");
            Scribe_Values.Look(ref staffStrikes, "staffStrikes");
            Scribe_Values.Look(ref executions, "executions");
            Scribe_Values.Look(ref wrongfulExecutions, "wrongfulExecutions");
            Scribe_Values.Look(ref commuted, "commuted");
            Scribe_Values.Look(ref commissarySales, "commissarySales");
            Scribe_Values.Look(ref bribes, "bribes");
            Scribe_Values.Look(ref itemsExported, "itemsExported");
            Scribe_Values.Look(ref lettersDelivered, "lettersDelivered");
            Scribe_Values.Look(ref lettersWritten, "lettersWritten");
            Scribe_Values.Look(ref mailContrabandFound, "mailContrabandFound");
            Scribe_Values.Look(ref mailContrabandMissed, "mailContrabandMissed");
            Scribe_Values.Look(ref debtsFormed, "debtsFormed");
            Scribe_Values.Look(ref debtsPaid, "debtsPaid");
            Scribe_Values.Look(ref debtBeatings, "debtBeatings");
            Scribe_Values.Look(ref cardGames, "cardGames");
            Scribe_Values.Look(ref workouts, "workouts");
            Scribe_Values.Look(ref booksDelivered, "booksDelivered");
            Scribe_Values.Look(ref booksRead, "booksRead");
            Scribe_Values.Look(ref bookContraband, "bookContraband");
            Scribe_Values.Look(ref measuresStarted, "measuresStarted");
            Scribe_Values.Look(ref measuresLifted, "measuresLifted");
            Scribe_Values.Look(ref measuresFailed, "measuresFailed");
            Scribe_Values.Look(ref flagshipsEarned, "flagshipsEarned");
            Scribe_Values.Look(ref legendsArrived, "legendsArrived");
            Scribe_Values.Look(ref ringsBusted, "ringsBusted");
            Scribe_Values.Look(ref ringsPartial, "ringsPartial");
            Scribe_Values.Look(ref ringLinksFound, "ringLinksFound");
            Scribe_Values.Look(ref ringMembersTurned, "ringMembersTurned");
            Scribe_Values.Look(ref trials, "trials");
            Scribe_Values.Look(ref convictions, "convictions");
            Scribe_Values.Look(ref acquittals, "acquittals");
            Scribe_Values.Look(ref witnessesRecanted, "witnessesRecanted");
            Scribe_Values.Look(ref complaintsFiled, "complaintsFiled");
            Scribe_Values.Look(ref complaintsUpheld, "complaintsUpheld");
            Scribe_Values.Look(ref complaintsDismissed, "complaintsDismissed");
            Scribe_Values.Look(ref complaintsFalseFound, "complaintsFalseFound");
            Scribe_Values.Look(ref shuttleLandings, "shuttleLandings");
            Scribe_Values.Look(ref cargoRuns, "cargoRuns");
            Scribe_Values.Look(ref shuttlesCancelled, "shuttlesCancelled");
            Scribe_Values.Look(ref shuttlesOffPad, "shuttlesOffPad");
            Scribe_Values.Look(ref stowawayAttempts, "stowawayAttempts");
            Scribe_Values.Look(ref stowawaysCaught, "stowawaysCaught");
            Scribe_Values.Look(ref stowawaysAway, "stowawaysAway");
            Scribe_Values.Look(ref landParcels, "landParcels");
            Scribe_Values.Look(ref landCells, "landCells");
            Scribe_Values.Look(ref cargoContrabandFound, "cargoContrabandFound");
            Scribe_Values.Look(ref drugTests, "drugTests");
            Scribe_Values.Look(ref drugPositives, "drugPositives");
        }
    }

    /// <summary>Midnight: government payments per inmate, staff wages, sentence completion.</summary>
    public static class DailySettlement
    {
        /// <summary>The state's payment for the day, and for how many inmates (not those whose execution is overdue, nor those waiting for the shuttle to leave: the payment ends with the stay).</summary>
        /// <summary>
        /// The day's grade goes into the history and becomes the last grade. An empty prison has no grade, as the overview says (a dash): nothing is added, and it does not count as a
        /// good day for the reputation, the grants' streaks or the state (see <see cref="NoteReputation"/>). Returns whether the day was graded.
        /// </summary>
        public static bool NoteGrade(PrisonGame game, ReportCard card)
        {
            if (!card.Graded) return false;
            game.lastGrade = card.Overall;
            game.RecordGrade(card.Overall, card);
            return true;
        }

        /// <summary>The graded day's effect on the grants' streaks and the reputation (nothing for a day nobody was graded on).</summary>
        public static void NoteReputation(PrisonGame game, ReportCard card)
        {
            if (!card.Graded) return;
            game.grants.Notify_DailyGrade(card.Overall);
            game.reputation = UnityEngine.Mathf.Clamp(game.reputation + (card.Overall - game.reputation) * 0.15f, 0f, 100f);
        }

        public static int DailyPayments(PrisonGame game, Map map, out int inmates) => DailyPayments(game, map, out inmates, game.PaymentMultiplier);

        /// <summary>The day's payment at a given multiplier (the grades' share is the payment with them less the payment without, see <see cref="PerDiem.GradeShare"/>).</summary>
        public static int DailyPayments(PrisonGame game, Map map, out int inmates, float multiplier)
        {
            int payments = 0;
            inmates = 0;
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                CompInmate c = prisoners[i].Inmate();
                if (c?.security == null || DeathRowSystem.ExecutionOverdue(c) || ShuttleSystem.OnTheWayOut(prisoners[i])) continue;
                payments += UnityEngine.Mathf.RoundToInt(SupermaxSystem.DailyPayment(c) * multiplier);
                inmates++;
            }
            return payments;
        }

        /// <summary>The day's per-diem on the ledger, its note saying what the week's grade added or took (<see cref="PerDiem.LedgerNote"/>). Returns the payment.</summary>
        public static int PayPerDiem(PrisonGame game, Map map)
        {
            int payments = DailyPayments(game, map, out int inmates);
            game.ledger.Add(RPDefOf.RP_InmatePayments, payments, PerDiem.LedgerNote(game, inmates, PerDiem.GradeShare(game, map)));
            return payments;
        }

        /// <summary>The staff's pay: the wages, and the handover's overtime worked since the last settlement (accrued as it was worked, whatever the settings are now) as a line of its own on the wages account, cleared once paid. Returns the whole of it.</summary>
        public static int PayWages(PrisonGame game, Map map)
        {
            List<Pawn> payroll = StaffCommute.AllStaff(map);
            int overtime = game.overtimeOwed, wages = StaffCareers.BasePay(payroll);
            game.ledger.Add(RPDefOf.RP_Wages, -wages, "RP_WagesNote".Translate(payroll.Count));
            if (overtime > 0) game.ledger.Add(RPDefOf.RP_Wages, -overtime, "RP_OvertimeNote".Translate(game.overtimeHours));
            (game.overtimeOwed, game.overtimeHours) = (0, 0);
            StaffAgreements.Notify_WagesPaid(game);
            return wages + overtime;
        }

        public static void Run(PrisonGame game, Map map)
        {
            int payments = PayPerDiem(game, map);
            PrisonRelations.Daily(map);
            game.mail.Daily(map);
            LibrarySystem.Daily(map);
            StaffLives.Daily(map);
            Kin.Daily(map);
            CorruptionSystem.Daily(map);
            ShiftSystem.Daily(map);
            StaffCareers.Daily(map);
            game.applicants.Daily();
            Mentors.Daily(map);
            StaffMemories.Daily(map);
            // (After the fade: the unit officers' day of trust, their wing's tip, and posts whose holder is no longer theirs to hold.)
            UnitOfficers.Daily(map);
            WardenSystem.Daily(game, map);
            ComplaintSystem.Daily(map);
            // (The court's daily pass looks after the accused and the witnesses of its own map: every home map's.)
            foreach (Map home in Find.Maps)
                if (home.IsPlayerHome) Trials.Daily(game, home);
            CellQuality.Daily(map);
            KeepApart.Daily(map);
            Cellmates.DailyReview(map);
            CellRequests.Daily(map);
            GangTurf.Daily(map);
            GangRanks.Daily(map);
            GangExit.Daily(map);
            ExInmates.Daily(game, map);
            DebtSystem.Daily(map);
            RingSystem.Daily(map);
            int informants = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { informantPaid: true, activeFlag: true } ic)
                {
                    if (ic.snitchExposed)
                    {
                        ic.informantPaid = false; // burned: no more tips, no more pay
                        continue;
                    }
                    ic.cash += InformantDecision.DailyPay;
                    informants++;
                }
            if (informants > 0) game.ledger.Add(RPDefOf.RP_Wages, -informants * InformantDecision.DailyPay, "RP_InformantPayNote".Translate(informants));

            int wages = PayWages(game, map);
            if (PhoneCalls.TapsActive(game) && PhoneCalls.MonitoredPhones(map) is var phones and > 0)
                game.ledger.Add(RPDefOf.RP_Wages, -phones * PhoneCalls.TapCostPerPhone, "RP_PhoneTapNote".Translate(phones));
            if (UtilitySystem.Active) game.ledger.Add(RPDefOf.RP_Utilities, -UtilitySystem.DailyBill(map), "RP_UtilitiesNote".Translate());
            game.ledger.Add(RPDefOf.RP_InmateWages, -game.today.inmateWages, "RP_InmateWagesNote".Translate());
            game.ledger.Add(RPDefOf.RP_CommissarySales, game.today.commissarySales, "RP_CommissaryNote".Translate());
            game.laundry.DailyTopUp(game, map.mapPawns.PrisonersOfColonySpawnedCount);

            Remission.Daily(game, map);
            Detox.Daily(game, map);
            ReleaseSystem.ReleaseServedSentences(game, map);
            ReclassificationSystem.Daily(map, game);
            SupermaxSystem.Daily(map, game);
            PsychWing.Daily(map, game);
            DrugTesting.Daily(map, game);
            ParoleSystem.DailyHearings(game, map);
            // (Tasks first: one whose target is gone ends, and one who is burned in place gives no tip today.)
            InformantTask.Daily(map);
            InformantSystem.DailyTips(map);
            Wants.Daily(map);

            ReportCard card = PrisonGrading.Compute(map, game);
            NoteGrade(game, card);
            WeeklyReport.Daily(game, map);
            Holidays.Daily(game, map);
            OfficerOfTheMonth.Daily(game, map);
            NoteReputation(game, card);
            DailyIncidents day = game.today;
            game.today = new DailyIncidents();
            game.story.dramaToday = 0;
            game.story.beatsToday = 0;
            StoryLetters.NewDay(game);

            Messages.Message("RP_DailySettlement".Translate(Money.FormatSigned(payments), Money.Format(wages), Money.Format(game.ledger.funds)),
                MessageTypeDefOf.NeutralEvent, historical: true);
            if (game.ledger.funds < 0)
                Messages.Message("RP_InDebt".Translate(), MessageTypeDefOf.NegativeEvent);
            DebtCrisis.Daily(game, map, payments);
            // After the loan's night (a loan in default counts it) and the day's grade: the state decides whether to step in.
            SpecialMeasures.Daily(game, map, card, day);
            // (After the state's measures: an offer is not made on the day they begin, and a cohort not yet admitted is called off.)
            CustodyContracts.Daily(game, map);
            // (After the new day's budget of letters is opened: the warning counts against today's, and is a day ahead of what it warns of.)
            Suppression.Daily(game, map);
            // Last: the letters that waited take what is left of the new day's budget, after the loan's and the state's own.
            StoryLetters.Daily(game, map);
        }
    }

    /// <summary>A released inmate who will be arrested again (announced a few days after release).</summary>
    public class Rearrest : IExposable
    {
        public string name;
        public bool paroled;
        /// <summary>The warden recommended them at the hearing.</summary>
        public bool recommended;
        /// <summary>Why they were arrested (a keyed phrase, see <see cref="ParoleLife"/>): a violation of their parole, or a new offence; null in an old save.</summary>
        public string reason;
        /// <summary>Kept (ForcefullyKeptPawns) so they can come back through the gate.</summary>
        public Pawn pawn;
        public int dueTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref paroled, "paroled");
            Scribe_Values.Look(ref recommended, "recommended");
            Scribe_Values.Look(ref reason, "reason");
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref dueTick, "dueTick");
        }
    }

    public static class ReleaseSystem
    {
        /// <summary>For checks only: the old release, which rolls the reoffence at the gate and queues the rearrest at once (no supervision case).</summary>
        public static bool LegacyReoffendRoll;

        /// <summary>Hourly: rearrests that have come to light.</summary>
        public static void CheckRearrests(PrisonGame game)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = game.rearrests.Count - 1; i >= 0; i--)
            {
                Rearrest r = game.rearrests[i];
                if (now < r.dueTick) continue;
                // Still on the map waiting for the shuttle: they have not left, so nothing has been found out about them yet.
                if (r.pawn is { Spawned: true } waiting && ShuttleSystem.OnTheWayOut(waiting)) continue;
                game.rearrests.RemoveAt(i);
                // Died before they could leave (or since): there is no one to arrest.
                if (r.pawn is { Dead: true })
                {
                    Reoffenders.Forget(r);
                    continue;
                }
                string why = r.reason?.Translate();
                if (r.paroled && r.reason != null && r.reason != ParoleLife.NewOffence) game.stats.paroleViolations++;
                Story.Record(RPChronicleDefOf.RP_Chr_Release, 2, why != null ? (r.paroled ? "RP_Chr_RearrestedParoleeFor" : "RP_Chr_RearrestedFor").Translate(r.name, why)
                    : (r.paroled ? "RP_Chr_RearrestedParolee" : "RP_Chr_Rearrested").Translate(r.name));
                ParoleHearings.Notify_Rearrested(game, r);
                // Half of them are sent back here (in one to three days); the rest go elsewhere.
                if (r.pawn != null && !r.pawn.Dead && Rand.Chance(0.5f))
                {
                    r.dueTick = now + Rand.RangeInclusive(1, 3) * GenDate.TicksPerDay;
                    game.returning.Add(r);
                }
                else Reoffenders.Forget(r);
                if (r.paroled)
                {
                    game.ledger.Add(RPDefOf.RP_Fines, -ParoleSystem.ParoleBonus, "RP_ParoleClawbackNote".Translate(r.name));
                    game.reputation = UnityEngine.Mathf.Max(0f, game.reputation - 1f);
                    Messages.Message(why != null ? "RP_RearrestedParoleeFor".Translate(r.name, Money.Format(ParoleSystem.ParoleBonus), why)
                        : "RP_RearrestedParolee".Translate(r.name, Money.Format(ParoleSystem.ParoleBonus)), MessageTypeDefOf.NegativeEvent);
                }
                else
                {
                    game.reputation = UnityEngine.Mathf.Max(0f, game.reputation - 0.5f);
                    Messages.Message(why != null ? "RP_RearrestedFor".Translate(r.name, why) : "RP_Rearrested".Translate(r.name), MessageTypeDefOf.NeutralEvent, historical: false);
                }
            }
        }

        /// <summary>Whether this inmate's sentence is served and nothing holds them: not for life, not on remand (an open murder case), and not already on the way out (released once; the shuttle has not come yet).</summary>
        internal static bool SentenceServed(PrisonGame game, Pawn p, CompInmate c) =>
            c != null && !c.IsLifer && c.security != null && c.DaysServed >= c.TermDays && !Trials.OnRemand(game, p) && !ShuttleSystem.OnTheWayOut(p);

        /// <param name="only">Just this inmate (for checks that must not release the real ones).</param>
        public static void ReleaseServedSentences(PrisonGame game, Map map, Pawn only = null)
        {
            List<Pawn> toRelease = null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (only != null && p != only) continue;
                CompInmate c = p.Inmate();
                // (An accused killer awaiting trial is on remand: their sentence ends, and they go, only when the court has sat.)
                if (!SentenceServed(game, p, c)) continue;
                // Someone on the floor (a wound, food poisoning) cannot be sent out of the gate: vanilla's release starts a walk to the exit, which logs an
                // error for a downed pawn. They go at the next pass, once they are up.
                if (p.Downed) continue;
                (toRelease ??= new List<Pawn>()).Add(p);
            }
            if (toRelease == null) return;
            foreach (Pawn p in toRelease) Release(game, p);
            Messages.Message("RP_Released".Translate(toRelease.Count), new LookTargets(toRelease), MessageTypeDefOf.PositiveEvent);
        }

        /// <param name="record">False when the caller writes its own chronicle entry (exoneration).</param>
        /// <param name="cause">Why they are let go when it is not the end of the sentence (a keyed string: compassionate release), written on the register and the outbound entry; an old or dying inmate let go on it does not reoffend.</param>
        public static void Release(PrisonGame game, Pawn p, bool paroled = false, bool recommended = false, bool record = true, string cause = null)
        {
            // (Once: someone waiting for the shuttle is not released a second time, with a second reoffence, a second record and a second line.)
            if (ShuttleSystem.OnTheWayOut(p)) return;
            // (A custody contract's bonus for them is forfeit, whatever way out they take.)
            CustodyContracts.Notify_Departed(p, paroled ? "RP_CustodyEnd_Paroled" : "RP_CustodyEnd_Released");
            // (A release on a cause - compassionate - is let go as it always was, with nothing to review. Every other release opens a supervision case: nothing is rolled now, the outcome is
            // found at the reviews that follow the departure, see Supervision. Checks that need the old roll at the gate set LegacyReoffendRoll.)
            CompInmate rc = p.Inmate();
            // (Somebody who is innocent is never rearrested, as ever, but is put under review like anybody else - a case with no risk - so that neither a missing row on the Intake tab nor the board's trust coming at the
            // gate shows who was innocent.)
            bool supervise = rc != null && cause == null && !LegacyReoffendRoll;
            bool reoffends = !supervise && rc != null && !rc.innocent && cause == null && Rand.Chance(ParoleSystem.ReoffendChance(rc));
            FormerInmate former = FormerInmates.Notify_Released(game, p, rc, paroled, reoffends);
            if (former != null) former.cause = cause;
            SupervisionCase supervised = supervise && former != null ? Supervision.Create(game, p, rc, former, paroled, recommended) : null;
            if (paroled) Wants.Notify_Paroled(p, rc);
            WinterClothing.Collect(p, game);
            PrisonRelations.Notify_Leaving(p);
            game.story.Notify_Leaving(p);
            // Anything queued for the cell bed (relaxing, lying down) must not run once the bed is no longer theirs.
            p.jobs?.ClearQueuedJobs();
            p.ownership?.UnclaimAll();
            ShuttleSystem.RegisteringOwnRelease = true;
            try
            {
                GenGuest.PrisonerRelease(p);
            }
            finally
            {
                ShuttleSystem.RegisteringOwnRelease = false;
            }
            CompInmate c = p.Inmate();
            c?.RefreshActiveFlag();
            c?.ResetForNewStay(); // a pending appeal is moot once they walk, and so is everything else of the stay
            // They leave by the state shuttle (the postfix on GenGuest.PrisonerRelease has put them on the way out: to the ramp if it is down, else to reception);
            // the release counts when they board.
            // (A release is counted, with the reoffence that follows it, when they leave: see ShuttleSystem.Notify_Left. Somebody who is not on the map is gone already.)
            bool onTheWay = p.Spawned && c != null;
            if (onTheWay)
            {
                game.shuttle.Register(p, paroled ? OutboundKind.Paroled : OutboundKind.Released, counted: true, reoffends: reoffends, caseId: supervised?.id ?? -1);
                if (game.shuttle.OutboundOf(p) is { } entry) entry.cause = cause;
            }
            if (record) Story.Record(RPChronicleDefOf.RP_Chr_Release, 1, (paroled ? "RP_Chr_Paroled" : "RP_Chr_Released").Translate(p.LabelShort, (c?.DaysServed ?? 0f).ToString("F0")), p);
            if (record && paroled && former != null) ParoleLife.RecordTerms(former, p);
            Story.RecordFirst("release", RPChronicleDefOf.RP_Chr_Release, "RP_Chr_FirstRelease".Translate(p.LabelShort), p);
            // (A case's trust comes at its outcome: a recommended parolee who stays out builds it when the review ends, not at the gate.)
            if (supervised == null) ParoleHearings.Notify_Released(game, recommended, reoffends);
            if (!onTheWay)
            {
                game.stats.released++;
                if (reoffends) game.stats.reoffended++;
                if (supervised != null) Supervision.Begin(game, supervised);
            }
            if (reoffends)
            {
                // The news reaches the prison a few days later; an early release that went wrong costs the bonus back.
                // A parolee comes back for the term they broke (or a new offence); someone who served their time, for a new offence.
                var r = new Rearrest { name = p.LabelShort, paroled = paroled, recommended = recommended, pawn = p, reason = paroled && former != null ? ParoleLife.PickViolation(former) : ParoleLife.NewOffence, dueTick = Find.TickManager.TicksGame + Rand.RangeInclusive(1, 4) * GenDate.TicksPerDay };
                game.rearrests.Add(r);
                Reoffenders.KeepForReturn(game, p);
            }
        }
    }
}
