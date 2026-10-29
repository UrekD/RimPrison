using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPReleasePrepDefOf
    {
        public static LedgerCategoryDef RP_Resettlement;

        static RPReleasePrepDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPReleasePrepDefOf));
    }

    /// <summary>What the warden promised to arrange for someone's release (see <see cref="ReleasePrep"/>): nothing beyond what they have, lodging at a stated price, or a job interview for a qualification the vocational courses gave.</summary>
    public enum ReleaseSupport : byte { None, Lodging, Interview }

    /// <summary>A supervision case: released and waiting for the shuttle (nothing is counted yet), under review (from the physical departure), or settled.</summary>
    public enum CaseState : byte { Awaiting, Reviewing, Resolved }

    /// <summary>How a settled case ended: they stayed out for the whole period, were rearrested (once), or there was nobody to review (died, never left, back inside, or on the prison's own staff).</summary>
    public enum CaseOutcome : byte { None, StayedOut, Rearrested, Voided }

    public enum CaseSetback : byte { None, JobLost, AddressWithdrawn }

    public enum SetbackState : byte { None, Pending, Recovered, Failed }

    /// <summary>
    /// One release under supervision, keyed to a stable id (<see cref="id"/>, never reused): the baseline risk the old single roll would have used, what is known of their job and address, the support the warden promised and whether it
    /// was carried out, the next review, a setback that has happened and not yet been reviewed, and the outcome once there is one. <see cref="version"/> is 1 (a case a later version of the mod writes keeps its meaning; nothing reads a field it does not know).
    /// The person is kept as a world pawn (<see cref="pawn"/>) only while the case is open, within <see cref="Supervision.MaxKept"/>; a rearrest takes the pawn over (see <see cref="Reoffenders"/>).
    /// </summary>
    public class SupervisionCase : IExposable
    {
        public int version, id, pawnId = -1, number, stay;
        public string name;
        public Pawn pawn;
        public bool paroled, recommended;
        public CaseState state;
        public CaseOutcome outcome;
        /// <summary>When they were released, when they physically left (-1 not yet; the review days count from here), the next review, and the last tick of the period.</summary>
        public int releasedTick, departedTick = -1, nextReviewTick = -1, endTick = -1;
        /// <summary>Days of review, hazard rolls made so far, and every review held so far (a setback's wait counts reviews, so a late hourly pass cannot shorten it).</summary>
        public int reviewDays, reviewsRolled, reviewsHeld;
        /// <summary>The reoffend chance the old single roll would have used (taken at the release), and the weight of the causes in the state they left in (what a day's hazard is measured against).</summary>
        public float baseRisk, baseWeight;
        /// <summary>What is known of them: a job, an address, and where it came from (an interview, lodging the prison paid for).</summary>
        public bool jobSecured, addressSecured, jobByInterview, addressByLodging;
        public ProgramDef trade;
        public ReleaseSupport support;
        /// <summary>The support was carried out (lodging paid at the departure, the interview held), and for lodging the money paid.</summary>
        public bool supportDone, supportFulfilled;
        public int lodgingPaid;
        public CaseSetback setback;
        public SetbackState setbackState;
        public int setbackAt = -1, setbackTick = -1;
        /// <summary>The warden's bounded help for the open setback, and what it cost.</summary>
        public bool helped;
        public int helpPaid;
        public string reason;
        public int resolvedTick = -1;
        /// <summary>The rearrest was queued (once: nothing is queued twice for a case).</summary>
        public bool rearrestQueued;

        /// <summary>A case the warden is working: support promised, or help given.</summary>
        public bool Handled => support != ReleaseSupport.None || helped;

        /// <summary>The case keeps its pawn in the world (for the rearrest that may follow): open, with a pawn, and a risk to be rearrested at all. A case with no risk holds the reference only to see them leave.</summary>
        public bool Keeps => pawn != null && state != CaseState.Resolved && baseRisk > 0f;

        public void ExposeData()
        {
            Scribe_Values.Look(ref version, "version");
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref pawnId, "pawnId", -1);
            Scribe_Values.Look(ref number, "number");
            Scribe_Values.Look(ref stay, "stay");
            Scribe_Values.Look(ref name, "name");
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref paroled, "paroled");
            Scribe_Values.Look(ref recommended, "recommended");
            Scribe_Values.Look(ref state, "state");
            Scribe_Values.Look(ref outcome, "outcome");
            Scribe_Values.Look(ref releasedTick, "released");
            Scribe_Values.Look(ref departedTick, "departed", -1);
            Scribe_Values.Look(ref nextReviewTick, "nextReview", -1);
            Scribe_Values.Look(ref endTick, "end", -1);
            Scribe_Values.Look(ref reviewDays, "reviewDays");
            Scribe_Values.Look(ref reviewsRolled, "reviewsRolled");
            Scribe_Values.Look(ref reviewsHeld, "reviewsHeld");
            Scribe_Values.Look(ref baseRisk, "baseRisk");
            Scribe_Values.Look(ref baseWeight, "baseWeight");
            Scribe_Values.Look(ref jobSecured, "jobSecured");
            Scribe_Values.Look(ref addressSecured, "addressSecured");
            Scribe_Values.Look(ref jobByInterview, "jobByInterview");
            Scribe_Values.Look(ref addressByLodging, "addressByLodging");
            Scribe_Defs.Look(ref trade, "trade");
            Scribe_Values.Look(ref support, "support");
            Scribe_Values.Look(ref supportDone, "supportDone");
            Scribe_Values.Look(ref supportFulfilled, "supportFulfilled");
            Scribe_Values.Look(ref lodgingPaid, "lodgingPaid");
            Scribe_Values.Look(ref setback, "setback");
            Scribe_Values.Look(ref setbackState, "setbackState");
            Scribe_Values.Look(ref setbackAt, "setbackAt", -1);
            Scribe_Values.Look(ref setbackTick, "setbackTick", -1);
            Scribe_Values.Look(ref helped, "helped");
            Scribe_Values.Look(ref helpPaid, "helpPaid");
            Scribe_Values.Look(ref reason, "reason");
            Scribe_Values.Look(ref resolvedTick, "resolved", -1);
            Scribe_Values.Look(ref rearrestQueued, "rearrestQueued");
        }

        public string Describe() =>
            $"{id}/{version}/{name}#{number}:{stay}/{pawn?.thingIDNumber ?? -1}:{pawnId}/{state}/{outcome}/{releasedTick}/{departedTick}/{nextReviewTick}/{endTick}/{reviewsRolled}:{reviewsHeld}:{reviewDays}/{baseRisk:F3}:{baseWeight:F2}/{jobSecured}{addressSecured}{jobByInterview}{addressByLodging}/{trade?.defName}"
            + $"/{support}:{supportDone}:{supportFulfilled}:{lodgingPaid}/{setback}:{setbackState}:{setbackAt}:{setbackTick}/{helped}:{helpPaid}/{reason}/{resolvedTick}/{rearrestQueued}/{paroled}{recommended}";
    }

    /// <summary>The cases (<see cref="PrisonGame.supervision"/>): in release order, the open ones and the settled ones of the last while (<see cref="Supervision.MaxResolved"/>). Empty in an old save.</summary>
    public class SupervisionBook : IExposable
    {
        public int nextId = 1;
        public List<SupervisionCase> cases = new List<SupervisionCase>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref nextId, "nextId", 1);
            Scribe_Collections.Look(ref cases, "cases", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) cases ??= new List<SupervisionCase>();
        }

        public SupervisionCase Find(int id)
        {
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].id == id) return cases[i];
            return null;
        }

        /// <summary>Departed and not settled: the unresolved cohort (counted among the released, with no outcome yet).</summary>
        public int OpenCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < cases.Count; i++)
                    if (cases[i].state == CaseState.Reviewing) n++;
                return n;
            }
        }

        /// <summary>Cases the warden is working that are not settled: they take a place with an administrator or a counsellor (see <see cref="ReleasePrep.Capacity"/>).</summary>
        public int HandledOpen
        {
            get
            {
                int n = 0;
                for (int i = 0; i < cases.Count; i++)
                    if (cases[i].state != CaseState.Resolved && cases[i].Handled) n++;
                return n;
            }
        }

        public int Count(CaseOutcome outcome)
        {
            int n = 0;
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].state == CaseState.Resolved && cases[i].outcome == outcome) n++;
            return n;
        }

        /// <summary>An open case keeps this pawn in the world (see <see cref="SupervisionCase.Keeps"/>).</summary>
        public bool Holds(Pawn p)
        {
            if (p == null) return false;
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].pawn == p && cases[i].Keeps) return true;
            return false;
        }

        public string Describe() => $"{nextId}[{string.Join(";", cases.Select(x => x.Describe()))}]";
    }

    /// <summary>One thing that can end a review day badly: its keyed violation, its weight, and the setback it first shows as (none: it is a breach the day it happens).</summary>
    public struct SupervisionCause
    {
        public string key;
        public float weight;
        public CaseSetback setback;
    }

    /// <summary>What a review decided (see <see cref="Supervision.Decide"/>).</summary>
    public enum ReviewStep : byte { None, Waiting, Setback, Recovered, Breach, StayedOut }

    /// <summary>
    /// Supervision after release (gap analysis 6, Astra 3; DESIGN.md "Release preparation"). A release no longer rolls the reoffence at the gate: it opens a case, the case starts at the physical departure (the shuttle's
    /// <see cref="ShuttleSystem.Notify_Left"/>), and for <see cref="ReviewDaysServed"/> days (a parolee: their parole term, <see cref="ParoleReviewMin"/> to <see cref="ParoleReviewMax"/>) one review a day rolls a small hazard. The
    /// hazard is the old chance spread so that the whole period has exactly the old chance (1 - (1 - risk)^(1/days) a day), split over the causes the parolee's own situation allows (see <see cref="Causes"/>): a new offence and the weekly
    /// reports, a job and an address that is held (they can be lost: a setback) or is not (a breach the day it is missed). A lost job or an address withdrawn is reported and stays open for <see cref="SetbackReviews"/> reviews: unhelped
    /// it ends in the rearrest; the warden's bounded help (<see cref="HelpPrice"/>, once) mends it <see cref="HelpSuccess"/> of the time. Lodging actually paid for, or an interview that won a job, takes a cause away and lowers the hazard;
    /// a promise nobody paid for does not. Payment never touches a new offence. The ending is the old one: the rearrest goes on the marshals' queue (<see cref="Rearrest"/>, <see cref="Reoffenders"/>), once.
    /// </summary>
    public static class Supervision
    {
        public const int CurrentVersion = 1;
        public const int ReviewDaysServed = 7, ParoleReviewMin = 5, ParoleReviewMax = 14, SetbackReviews = 2, StaleAwaitingDays = 20;
        /// <summary>
        /// Open cases that keep a pawn in the world at once (<see cref="EnforceCap"/>). A prison releasing three to five a day has 25 to 40 cases under review (seven days and the wait for the shuttle); a case with no risk keeps none, and a
        /// case that let go of its pawn borrows the register's at the rearrest when the register still has them, so at this cap nearly every rearrest has somebody to bring back.
        /// </summary>
        public const int MaxKept = 40, MaxResolved = 60;
        public const int LodgingPrice = 150, HelpPrice = 100;
        public const float HelpSuccess = 0.6f, InterviewSuccess = 0.6f;
        /// <summary>The weights of the causes (a parolee's own violations, see <see cref="ParoleLife.PickViolation"/>, which these match).</summary>
        public const float NewOffenceWeight = 1f, ReportsWeight = 1f, JobLostWeight = 1f, NoWorkWeight = 1.5f, AddressLeftWeight = 0.5f, NoAddressWeight = 1.5f;

        /// <summary>For checks only: nothing is said (no message, letter or chronicle line), so that many cases can be run through without a flood.</summary>
        public static bool Silent;

        private static int Now => Find.TickManager.TicksGame;

        public static SupervisionBook Book => PrisonGame.Instance?.supervision;

        public static bool Holds(Pawn p) => Book?.Holds(p) == true;

        // ---- the hazard ----------------------------------------------------------------------------------------------

        /// <summary>The chance of trouble on one review so that <paramref name="days"/> of them together have the chance <paramref name="risk"/>.</summary>
        public static float DayHazard(float risk, int days) => risk <= 0f || days <= 0 ? 0f : 1f - Mathf.Pow(1f - Mathf.Min(risk, 0.99f), 1f / days);

        /// <summary>The causes a review can end badly for, in the state they are in now.</summary>
        public static List<SupervisionCause> Causes(SupervisionCase sc)
        {
            var list = new List<SupervisionCause> { new SupervisionCause { key = ParoleLife.NewOffence, weight = NewOffenceWeight } };
            if (sc.paroled) list.Add(new SupervisionCause { key = "RP_Violation_Reports", weight = ReportsWeight });
            list.Add(sc.jobSecured ? new SupervisionCause { key = "RP_Violation_JobLost", weight = JobLostWeight, setback = CaseSetback.JobLost } : new SupervisionCause { key = "RP_Violation_NoWork", weight = NoWorkWeight });
            list.Add(sc.addressSecured ? new SupervisionCause { key = "RP_Violation_AddressLeft", weight = AddressLeftWeight, setback = CaseSetback.AddressWithdrawn } : new SupervisionCause { key = "RP_Violation_NoAddress", weight = NoAddressWeight });
            return list;
        }

        public static float TotalWeight(SupervisionCase sc)
        {
            float total = 0f;
            foreach (SupervisionCause cause in Causes(sc)) total += cause.weight;
            return total;
        }

        /// <summary>
        /// The chance that one review finds trouble: the day's share of the baseline risk, in proportion to how much of the baseline's causes are still there. A case with nothing done for it is exactly the
        /// day's share (the total over the period is the old single roll's); lodging that was paid for takes a cause (no address) out, an interview that won a job takes another out.
        /// </summary>
        public static float Hazard(SupervisionCase sc)
        {
            float day = DayHazard(sc.baseRisk, sc.reviewDays);
            return sc.baseWeight <= 0f ? day : Mathf.Clamp01(day * TotalWeight(sc) / sc.baseWeight);
        }

        /// <summary>The violation the rearrest is written for: a parolee's own term broken, and a new offence for anybody who had no terms.</summary>
        public static string ReasonFor(SupervisionCase sc, string key) => sc.paroled ? key : ParoleLife.NewOffence;

        // ---- a case ----------------------------------------------------------------------------------------------------

        public static int ReviewDaysFor(FormerInmate e, bool paroled) =>
            paroled && e != null && e.paroleEndTick >= 0 ? Mathf.Clamp(Mathf.RoundToInt((e.paroleEndTick - e.releasedTick) / (float)GenDate.TicksPerDay), ParoleReviewMin, ParoleReviewMax) : ReviewDaysServed;

        /// <summary>
        /// At release: the case, with the baseline risk the old roll would have used (none for someone who is innocent: they are never rearrested, as they never were, but they are reviewed like anybody else so that nothing shows
        /// who is innocent) and what the warden had planned. An older case of the same person that is still open (released before, an inmate again, released again) is closed first: one person, one open case.
        /// </summary>
        public static SupervisionCase Create(PrisonGame game, Pawn p, CompInmate c, FormerInmate e, bool paroled, bool recommended)
        {
            SupervisionBook book = game.supervision;
            for (int i = 0; i < book.cases.Count; i++)
                if (book.cases[i].state != CaseState.Resolved && book.cases[i].pawnId == p.thingIDNumber) Finish(game, book.cases[i], CaseOutcome.Voided);
            var sc = new SupervisionCase
            {
                version = CurrentVersion,
                id = book.nextId++,
                pawn = p,
                pawnId = p.thingIDNumber,
                number = c.inmateNumber,
                stay = c.timesAdmitted,
                name = p.LabelShort,
                paroled = paroled,
                recommended = recommended,
                releasedTick = Now,
                reviewDays = ReviewDaysFor(e, paroled),
                baseRisk = c.innocent ? 0f : ParoleSystem.ReoffendChance(c),
                jobSecured = paroled ? e.jobSecured : c.jobWaiting,
                addressSecured = c.familyCount > 0,
                trade = Vocation.Trade(c),
            };
            sc.baseWeight = TotalWeight(sc);
            // (What was planned still has to make sense: lodging for somebody who has a home, an interview for somebody who has a job or no certificate, is nothing.)
            bool sensible = c.releasePlan == ReleaseSupport.Lodging && !sc.addressSecured || c.releasePlan == ReleaseSupport.Interview && sc.trade != null && !sc.jobSecured;
            sc.support = sensible ? c.releasePlan : ReleaseSupport.None;
            // (A promise that no longer makes sense at the gate - a parolee whose certified trade found them a job there, say - is dropped, and the warden is told: it gave its handler's place back.)
            if (!sensible && c.releasePlan != ReleaseSupport.None && !Silent)
                Messages.Message("RP_PrepPromiseDropped".Translate(sc.name, ReleasePrep.SupportText(c.releasePlan)), MessageTypeDefOf.NeutralEvent, historical: false);
            book.cases.Add(sc);
            e.supervisionId = sc.id;
            if (sc.Keeps) Find.WorldPawns.ForcefullyKeptPawns.Add(sc.pawn);
            EnforceCap(game);
            return sc;
        }

        /// <summary>
        /// The cap: past <see cref="MaxKept"/> cases under review that keep a pawn the oldest (the nearest the end of their review, so the least risk still to come) let go of theirs. The case goes on; a rearrest then borrows the
        /// register's pawn if it has one, and otherwise has nobody to bring back. A case still waiting for the shuttle is not counted: its pawn is on the map.
        /// </summary>
        public static void EnforceCap(PrisonGame game)
        {
            List<SupervisionCase> kept = game.supervision.cases.Where(x => x.Keeps && x.state == CaseState.Reviewing).ToList();
            for (int i = 0; i < kept.Count - MaxKept; i++) LetGo(game, kept[i]);
        }

        /// <summary>The case lets go of its pawn: no longer kept in the world unless the register, a rearrest or another case still holds them.</summary>
        private static void LetGo(PrisonGame game, SupervisionCase sc)
        {
            Pawn p = sc.pawn;
            sc.pawn = null;
            if (p != null && !HeldElsewhere(game, p)) Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
        }

        /// <summary>
        /// The world-pawn keep is one flag shared by everyone who keeps a former inmate (the register, a rearrest waiting, a return waiting, an open case): whoever lets go asks this first, after taking their own hold off, and drops
        /// the keep only when nobody else still holds them.
        /// </summary>
        public static bool HeldElsewhere(PrisonGame game, Pawn p) =>
            p != null && game != null && (ExInmates.EntryOf(p) != null || game.rearrests.Exists(r => r.pawn == p) || game.returning.Exists(r => r.pawn == p) || game.supervision.Holds(p));

        public static FormerInmate EntryOf(PrisonGame game, SupervisionCase sc) => game.story.formerInmates.Find(e => e.supervisionId == sc.id);

        /// <summary>A setback is open for this register entry's release (their news waits, see <see cref="FormerInmates.Hourly"/>).</summary>
        public static bool SetbackOpen(PrisonGame game, FormerInmate e) =>
            e.supervisionId >= 0 && game.supervision.Find(e.supervisionId) is { setbackState: SetbackState.Pending, state: not CaseState.Resolved };

        /// <summary>Whether a parolee under this case may write: they have left, and have not been rearrested.</summary>
        public static bool MayWrite(SupervisionCase sc) => sc.state == CaseState.Reviewing || sc.state == CaseState.Resolved && sc.outcome == CaseOutcome.StayedOut;

        /// <summary>Keeps the register's entry in step with what is known of them now, so a letter from them says how they are: a job lost is "still looking for work".</summary>
        private static void Sync(PrisonGame game, SupervisionCase sc)
        {
            if (!sc.paroled || EntryOf(game, sc) is not { } e) return;
            e.jobSecured = sc.jobSecured;
            e.addressSecured = sc.addressSecured;
            e.lodged = sc.addressSecured && sc.addressByLodging;
            if (!sc.jobSecured) e.tradeJob = null;
            else if (sc.jobByInterview) e.tradeJob = sc.trade;
        }

        // ---- the departure ---------------------------------------------------------------------------------------------

        public static void Notify_Departed(PrisonGame game, int caseId)
        {
            if (game?.supervision.Find(caseId) is { } sc) Begin(game, sc);
        }

        /// <summary>
        /// The review starts: from the moment they are gone. Lodging that was promised is paid for now (or not: the promise stands unfunded and does nothing). Somebody who died before they left has no review (voided); somebody
        /// gone from the game (discarded, or lost across a save) is reviewed all the same with nobody kept, and no lodging is booked for them. <paramref name="countRelease"/>: the release was never counted at a departure (the
        /// shuttle's entry is gone), so it is counted here.
        /// </summary>
        public static void Begin(PrisonGame game, SupervisionCase sc, bool countRelease = false)
        {
            if (sc.state != CaseState.Awaiting) return;
            if (sc.pawn is { Dead: true })
            {
                Finish(game, sc, CaseOutcome.Voided);
                return;
            }
            bool gone = sc.pawn == null || sc.pawn.Destroyed;
            if (sc.pawn is { Destroyed: true }) LetGo(game, sc);
            int now = Now;
            sc.state = CaseState.Reviewing;
            sc.departedTick = now;
            sc.nextReviewTick = now + GenDate.TicksPerDay;
            sc.endTick = now + sc.reviewDays * GenDate.TicksPerDay;
            game.stats.supervisionStarted++;
            if (countRelease) game.stats.released++;
            // (Their parole term, which ran from the release, runs at least as long as the review that starts now.)
            if (sc.paroled && EntryOf(game, sc) is { paroleEndTick: >= 0 } entry) entry.paroleEndTick = Mathf.Max(entry.paroleEndTick, sc.endTick);
            if (sc.support == ReleaseSupport.Lodging && !sc.supportDone)
            {
                if (gone) sc.supportDone = true;
                else FundLodging(game, sc);
            }
            // (Nobody is kept for a case that can never end in a rearrest: the reference was only to see them leave.)
            if (sc.baseRisk <= 0f) sc.pawn = null;
        }

        private static void FundLodging(PrisonGame game, SupervisionCase sc)
        {
            sc.supportDone = true;
            if (game.ledger.funds < LodgingPrice)
            {
                if (!Silent) Messages.Message("RP_LodgingUnfunded".Translate(sc.name, Money.Format(LodgingPrice)), MessageTypeDefOf.NegativeEvent, historical: false);
                return;
            }
            game.ledger.Add(RPReleasePrepDefOf.RP_Resettlement, -LodgingPrice, "RP_LodgingNote".Translate(sc.name));
            sc.lodgingPaid = LodgingPrice;
            sc.supportFulfilled = true;
            sc.addressSecured = true;
            sc.addressByLodging = true;
            game.stats.supervisionLodged++;
            Sync(game, sc);
            if (!Silent) Messages.Message("RP_LodgingFunded".Translate(sc.name, Money.Format(LodgingPrice)), MessageTypeDefOf.PositiveEvent, historical: false);
        }

        // ---- the reviews -----------------------------------------------------------------------------------------------

        /// <summary>Hourly: an awaiting case whose person is gone for good or never left is closed; a review that is due is held (one a day, so a late pass does not hold two).</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            List<SupervisionCase> cases = game.supervision.cases;
            if (cases.Count == 0) return;
            int now = Now;
            for (int i = 0; i < cases.Count; i++)
            {
                SupervisionCase sc = cases[i];
                if (sc.state == CaseState.Awaiting) CheckAwaiting(game, sc, now);
                else if (sc.state == CaseState.Reviewing && now >= sc.nextReviewTick) Review(game, sc, Rand.Value);
            }
            int settled = 0;
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].state == CaseState.Resolved) settled++;
            for (int i = 0; i < cases.Count && settled > MaxResolved; i++)
                if (cases[i].state == CaseState.Resolved)
                {
                    cases.RemoveAt(i--);
                    settled--;
                }
        }

        /// <summary>The hourly pass's look at the cases still waiting for the shuttle, and nothing else (no review): for a check that must not have one of them settled in the middle of what it counts.</summary>
        internal static void SettleAwaiting(PrisonGame game)
        {
            List<SupervisionCase> cases = game.supervision.cases;
            int now = Now;
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].state == CaseState.Awaiting) CheckAwaiting(game, cases[i], now);
        }

        private static void CheckAwaiting(PrisonGame game, SupervisionCase sc, int now)
        {
            Pawn p = sc.pawn;
            // (Died before they left, one of the colony's own now, an inmate here again - a recapture, a rearrest - or waiting far too long: nobody to review. The shuttle has dropped their entry for the same reasons.)
            if (p is { Dead: true } || IsOurs(p) || IsInmateAgain(p) || now - sc.releasedTick > StaleAwaitingDays * GenDate.TicksPerDay)
            {
                Finish(game, sc, CaseOutcome.Voided);
                return;
            }
            // (Gone from the game, or gone from the map with no shuttle to say so: they are out, and the review starts. The departure never counted the release: it is counted now.)
            if (p == null || p.Destroyed || !p.Spawned && p.holdingOwner == null && ShuttleSystem.Sys?.OutboundOf(p) == null) Begin(game, sc, countRelease: true);
        }

        /// <summary>On the map as an inmate of the prison again, not released.</summary>
        private static bool IsInmateAgain(Pawn p) => p is { Destroyed: false, Spawned: true, IsPrisonerOfColony: true, guest: { Released: false } };

        /// <summary>One of the player's own now (hired from the register, or freed into the colony): no longer the board's to supervise.</summary>
        private static bool IsOurs(Pawn p) => p is { Destroyed: false } && (p.Faction == Faction.OfPlayer || p.IsFreeColonist);

        /// <summary>
        /// One review. <paramref name="roll"/> is the day's roll (0 to 1), <paramref name="interviewRoll"/> the interview's, when one is held (a check holds both). A case that is not under review does nothing: a review is
        /// held once however many times it is asked.
        /// </summary>
        public static void Review(PrisonGame game, SupervisionCase sc, float roll, float? interviewRoll = null)
        {
            if (sc.state != CaseState.Reviewing) return;
            sc.nextReviewTick = (sc.nextReviewTick < 0 ? Now : sc.nextReviewTick) + GenDate.TicksPerDay;
            // (Died, hired onto the staff, or an inmate here again: nobody to review. Only a death or one of those: a pawn the world discarded is still a person out there, and the review goes on without one kept - as it does
            // after a load, where a discarded pawn comes back as nobody.)
            if (sc.pawn is { Dead: true } || IsOurs(sc.pawn) || IsInmateAgain(sc.pawn))
            {
                Finish(game, sc, CaseOutcome.Voided);
                return;
            }
            if (sc.pawn is { Destroyed: true }) LetGo(game, sc);
            if (sc.support == ReleaseSupport.Interview && !sc.supportDone) HoldInterview(game, sc, interviewRoll ?? Rand.Value);
            switch (Decide(sc, roll, out string reason))
            {
                case ReviewStep.Setback:
                    Announce(game, sc);
                    break;
                case ReviewStep.Recovered:
                    AnnounceRecovered(game, sc);
                    if (sc.reviewsRolled >= sc.reviewDays) Finish(game, sc, CaseOutcome.StayedOut);
                    break;
                case ReviewStep.Breach:
                    sc.reason = reason;
                    Finish(game, sc, CaseOutcome.Rearrested);
                    break;
                case ReviewStep.StayedOut:
                    Finish(game, sc, CaseOutcome.StayedOut);
                    break;
            }
        }

        /// <summary>
        /// What one review's roll decides, changing the case's own fields and nothing else (the game is touched by <see cref="Review"/>). A setback that is open waits out <see cref="SetbackReviews"/> reviews (no
        /// new roll: it is the day's trouble already) and then ends in a breach unless the warden's help was given and the roll is under <see cref="HelpSuccess"/>. Otherwise the roll is under the day's hazard or it is not;
        /// when it is, the roll's place within the hazard picks the cause.
        /// </summary>
        public static ReviewStep Decide(SupervisionCase sc, float roll, out string reason)
        {
            reason = null;
            sc.reviewsHeld++;
            if (sc.setbackState == SetbackState.Pending)
            {
                if (sc.reviewsHeld - sc.setbackAt < SetbackReviews) return ReviewStep.Waiting;
                if (sc.helped && roll < HelpSuccess)
                {
                    if (sc.setback == CaseSetback.JobLost) sc.jobSecured = true;
                    else
                    {
                        sc.addressSecured = true;
                        sc.addressByLodging = true;
                    }
                    sc.setbackState = SetbackState.Recovered;
                    return ReviewStep.Recovered;
                }
                sc.setbackState = SetbackState.Failed;
                reason = ReasonFor(sc, sc.setback == CaseSetback.JobLost ? "RP_Violation_JobLost" : "RP_Violation_AddressLeft");
                return ReviewStep.Breach;
            }
            float hazard = Hazard(sc);
            sc.reviewsRolled++;
            if (roll < hazard)
            {
                List<SupervisionCause> causes = Causes(sc);
                float total = 0f;
                foreach (SupervisionCause cause in causes) total += cause.weight;
                float at = (hazard > 0f ? roll / hazard : 0f) * total;
                SupervisionCause picked = causes[causes.Count - 1];
                foreach (SupervisionCause cause in causes)
                {
                    at -= cause.weight;
                    if (at < 0f)
                    {
                        picked = cause;
                        break;
                    }
                }
                if (picked.setback != CaseSetback.None)
                {
                    sc.setback = picked.setback;
                    sc.setbackState = SetbackState.Pending;
                    sc.setbackAt = sc.reviewsHeld;
                    sc.setbackTick = Now;
                    sc.helped = false;
                    sc.helpPaid = 0;
                    if (picked.setback == CaseSetback.JobLost)
                    {
                        sc.jobSecured = false;
                        sc.jobByInterview = false;
                    }
                    else
                    {
                        sc.addressSecured = false;
                        sc.addressByLodging = false;
                    }
                    return ReviewStep.Setback;
                }
                reason = ReasonFor(sc, picked.key);
                return ReviewStep.Breach;
            }
            return sc.reviewsRolled >= sc.reviewDays ? ReviewStep.StayedOut : ReviewStep.None;
        }

        /// <summary>The interview: held at the first review after the departure. It wins a job <see cref="InterviewSuccess"/> of the time (a job is one cause fewer); a lost one wins nothing, and is not asked again.</summary>
        private static void HoldInterview(PrisonGame game, SupervisionCase sc, float roll)
        {
            sc.supportDone = true;
            sc.supportFulfilled = true;
            game.stats.supervisionInterviews++;
            bool won = roll < InterviewSuccess && !sc.jobSecured;
            if (won)
            {
                sc.jobSecured = true;
                sc.jobByInterview = true;
                Sync(game, sc);
            }
            if (!Silent) Messages.Message((won ? "RP_InterviewWon" : "RP_InterviewLost").Translate(sc.name, sc.trade?.label ?? "?"), won ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NeutralEvent, historical: false);
        }

        private static void Announce(PrisonGame game, SupervisionCase sc)
        {
            game.stats.supervisionSetbacks++;
            Sync(game, sc);
            if (Silent) return;
            TaggedString text = (sc.setback == CaseSetback.JobLost ? "RP_SetbackJobText" : "RP_SetbackAddressText").Translate(sc.name, Mathf.Max(0, Mathf.RoundToInt((Now - sc.departedTick) / (float)GenDate.TicksPerDay)), SetbackReviews, Money.Format(HelpPrice));
            Story.Record(RPChronicleDefOf.RP_Chr_Release, 1, (sc.setback == CaseSetback.JobLost ? "RP_Chr_SetbackJob" : "RP_Chr_SetbackAddress").Translate(sc.name), sc.pawn);
            Find.LetterStack.ReceiveLetter("RP_SetbackLabel".Translate(sc.name), text, LetterDefOf.NegativeEvent);
        }

        private static void AnnounceRecovered(PrisonGame game, SupervisionCase sc)
        {
            Sync(game, sc);
            if (Silent) return;
            Messages.Message("RP_SetbackMended".Translate(sc.name), MessageTypeDefOf.PositiveEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Release, 1, "RP_Chr_SetbackMended".Translate(sc.name), sc.pawn);
        }

        // ---- the outcome -----------------------------------------------------------------------------------------------

        /// <summary>The case is settled, once: an outcome seen. Only here do the reoffence count and the board's trust move.</summary>
        public static void Finish(PrisonGame game, SupervisionCase sc, CaseOutcome outcome)
        {
            if (sc.state == CaseState.Resolved) return;
            sc.state = CaseState.Resolved;
            sc.outcome = outcome;
            sc.resolvedTick = Now;
            if (sc.setbackState == SetbackState.Pending) sc.setbackState = SetbackState.Failed;
            switch (outcome)
            {
                case CaseOutcome.StayedOut:
                    game.stats.supervisionStayedOut++;
                    ParoleHearings.Notify_Released(game, sc.recommended, false);
                    // (Said only of somebody the warden worked for or who mended a setback: every other release ends quietly, as it always did.)
                    if (!Silent && (sc.support != ReleaseSupport.None || sc.setback != CaseSetback.None))
                    {
                        Messages.Message("RP_SupervisionDone".Translate(sc.name, sc.reviewDays), MessageTypeDefOf.PositiveEvent, historical: false);
                        Story.Record(RPChronicleDefOf.RP_Chr_Release, 1, "RP_Chr_SupervisionDone".Translate(sc.name, sc.reviewDays), sc.pawn);
                    }
                    LetGo(game, sc);
                    break;
                case CaseOutcome.Rearrested:
                    game.stats.reoffended++;
                    game.stats.supervisionRearrested++;
                    QueueRearrest(game, sc);
                    break;
                default:
                    LetGo(game, sc);
                    break;
            }
        }

        /// <summary>
        /// The rearrest, on the marshals' queue exactly as a legacy one is (see <see cref="ReleaseSystem.CheckRearrests"/>): once, and not at all when the same person is already waiting to be arrested or brought back. The register's
        /// story of them ends here (no news, no application, no visit, no letter: their parole ends with the arrest), and a case that let go of its pawn past the cap borrows the register's, so the return has somebody to bring back.
        /// </summary>
        private static void QueueRearrest(PrisonGame game, SupervisionCase sc)
        {
            if (sc.rearrestQueued) return;
            sc.rearrestQueued = true;
            FormerInmate e = EntryOf(game, sc);
            Pawn p = sc.pawn;
            if (p == null && e?.pawn is { Dead: false, Destroyed: false } registered && registered.thingIDNumber == sc.pawnId) p = registered;
            sc.pawn = p;
            if (e != null) CloseEntry(e);
            if (p is { Dead: true } || p != null && (game.rearrests.Exists(r => r.pawn == p) || game.returning.Exists(r => r.pawn == p)))
            {
                LetGo(game, sc);
                return;
            }
            game.rearrests.Add(new Rearrest { name = sc.name, paroled = sc.paroled, recommended = sc.recommended, pawn = p, reason = sc.reason, dueTick = Now });
            if (p != null) Reoffenders.KeepForReturn(game, p);
            sc.pawn = null;
        }

        /// <summary>The register's entry of somebody rearrested: no news to come, never an applicant again, no letters (the parole term ends now), and the register's keep given up (the rearrest keeps them now).</summary>
        private static void CloseEntry(FormerInmate e)
        {
            e.newsTick = -1;
            e.declined = true;
            if (e.paroleEndTick > Now) e.paroleEndTick = Now;
            ExInmates.Unkeep(e);
        }

        /// <summary>Hired from the register onto the prison's staff: the case is closed with nothing to review (they are the prison's own now; a breach could only turn a member of staff back into an inmate).</summary>
        public static void Notify_Hired(PrisonGame game, FormerInmate e)
        {
            if (game == null || e.supervisionId < 0 || game.supervision.Find(e.supervisionId) is not { } sc || sc.state == CaseState.Resolved) return;
            sc.pawn = null;
            Finish(game, sc, CaseOutcome.Voided);
        }

        // ---- the warden's help -----------------------------------------------------------------------------------------

        /// <summary>Why the warden cannot offer help for this case, as a keyed string, or null: only an open setback can be helped (nothing mends a new offence), once, with the money, and a place with a handler.</summary>
        public static string HelpRefusal(PrisonGame game, Map map, SupervisionCase sc)
        {
            if (sc.state != CaseState.Reviewing || sc.setbackState != SetbackState.Pending) return "RP_HelpNoSetback";
            if (sc.helped) return "RP_HelpAlready";
            if (game.ledger.funds < HelpPrice) return "RP_HelpNoFunds";
            if (!sc.Handled && ReleasePrep.NoPlaceFor(game, map) is { } why) return why;
            return null;
        }

        /// <summary>Offers the bounded help: the price is paid now, whatever comes of it, and the setback is reviewed as before with the help given. Returns the refusal, or null when it was given.</summary>
        public static string OfferHelp(PrisonGame game, Map map, SupervisionCase sc)
        {
            string refusal = HelpRefusal(game, map, sc);
            if (refusal != null) return refusal;
            game.ledger.Add(RPReleasePrepDefOf.RP_Resettlement, -HelpPrice, "RP_HelpNote".Translate(sc.name));
            sc.helped = true;
            sc.helpPaid = HelpPrice;
            game.stats.supervisionHelped++;
            if (!Silent)
            {
                Messages.Message("RP_HelpOffered".Translate(sc.name, Money.Format(HelpPrice)), MessageTypeDefOf.NeutralEvent, historical: false);
                Story.Record(RPChronicleDefOf.RP_Chr_Release, 1, "RP_Chr_HelpOffered".Translate(sc.name, Money.Format(HelpPrice)), sc.pawn);
            }
            return null;
        }
    }

    /// <summary>
    /// Release preparation: who is within <see cref="WindowDays"/> days of release or a hearing, what is known of their job and address, and what the warden may arrange (lodging at <see cref="LodgingPrice"/>, a job interview for a
    /// certificate the vocational courses gave, or nothing) within the handling capacity of the administrators and counsellors (<see cref="CasesPerHandler"/> cases each at once). A promise is kept on the inmate
    /// (<see cref="CompInmate.releasePlan"/>) and becomes a supervision case's support at the release (see <see cref="Supervision"/>); lodging is paid, if the funds are there, at the physical departure.
    /// </summary>
    public static class ReleasePrep
    {
        public const int WindowDays = 5, CasesPerHandler = 3, LodgingPrice = Supervision.LodgingPrice;

        private static int Now => Find.TickManager.TicksGame;

        public static bool IsHandler(Pawn p) => p.GetComp<CompStaff>()?.role is { } role && (role == RPParoleDefOf.RP_Administrator || role == RPProgramDefOf.RP_Counsellor);

        /// <summary>The administrators and counsellors on the staff (on site or at home).</summary>
        public static int Handlers(Map map)
        {
            int n = 0;
            foreach (Pawn s in StaffCommute.AllStaff(map))
                if (IsHandler(s)) n++;
            return n;
        }

        /// <summary>For checks only: the capacity they test, whatever the live prison's administrators and counsellors would give (-1: off).</summary>
        public static int CapacityForChecks = -1;

        public static int Capacity(Map map) => CapacityForChecks >= 0 ? CapacityForChecks : Handlers(map) * CasesPerHandler;

        /// <summary>Cases at the handlers now: promises made to inmates still inside, and open supervision cases the warden is working.</summary>
        public static int Load(PrisonGame game, Map map)
        {
            int n = game.supervision.HandledOpen;
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
                if (prisoners[i].Inmate() is { releasePlan: not ReleaseSupport.None } && !ShuttleSystem.OnTheWayOut(prisoners[i])) n++;
            return n;
        }

        /// <summary>Why a new case cannot be taken (a keyed string), or null when a handler has room.</summary>
        public static string NoPlaceFor(PrisonGame game, Map map)
        {
            int capacity = Capacity(map);
            if (capacity == 0) return "RP_PrepNoHandler";
            return Load(game, map) >= capacity ? "RP_PrepNoCapacity" : null;
        }

        // ---- who is in the window ----------------------------------------------------------------------------------------

        public static float DaysToRelease(CompInmate c) => c.IsLifer ? float.MaxValue : Mathf.Max(0f, c.TermDays - c.DaysServed);

        /// <summary>Days to a hearing: past half the sentence and a hearing is due when the gap since the last has passed; before half, until half. None for a lifer, death row or a level with no parole.</summary>
        public static float DaysToHearing(CompInmate c, int now)
        {
            if (c.IsLifer || !ParoleSystem.MayBeParoled(c) || DeathRowSystem.Ext(c) != null) return float.MaxValue;
            float untilHalf = c.TermDays * 0.5f - c.DaysServed;
            float untilGap = (c.lastHearingTick + ParoleSystem.HearingGapDays * GenDate.TicksPerDay - now) / (float)GenDate.TicksPerDay;
            return Mathf.Max(0f, Mathf.Max(untilHalf, untilGap));
        }

        public static bool Eligible(Pawn p, CompInmate c) => c != null && p.Spawned && c.activeFlag && !ShuttleSystem.OnTheWayOut(p);

        public static bool InWindow(CompInmate c, int now) => Mathf.Min(DaysToRelease(c), DaysToHearing(c, now)) <= WindowDays;

        public struct PlanRow
        {
            public Pawn pawn;
            public CompInmate c;
            public float days, release;
            public bool hearing;
        }

        /// <summary>The inmates within the window of a release or a hearing, soonest first.</summary>
        public static List<PlanRow> Rows(Map map)
        {
            var rows = new List<PlanRow>();
            int now = Now;
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn p = prisoners[i];
                CompInmate c = p.Inmate();
                if (!Eligible(p, c)) continue;
                float release = DaysToRelease(c), hearing = DaysToHearing(c, now);
                if (Mathf.Min(release, hearing) > WindowDays) continue;
                rows.Add(new PlanRow { pawn = p, c = c, days = Mathf.Min(release, hearing), release = release, hearing = hearing < release });
            }
            // (Past half the sentence a hearing comes round every few days, so the nearer release is what tells them apart.)
            rows.Sort((a, b) => a.days != b.days ? a.days.CompareTo(b.days) : a.release.CompareTo(b.release));
            return rows;
        }

        // ---- what is known, and what the warden may arrange ------------------------------------------------------------------

        public static string JobText(CompInmate c) =>
            c.jobWaiting ? "RP_PrepJobWaiting".Translate().Resolve() : Vocation.Trade(c) is { } trade ? "RP_PrepJobTrade".Translate(trade.label).Resolve() : "RP_PrepJobNone".Translate().Resolve();

        public static string HousingText(CompInmate c) => c.familyCount > 0 ? "RP_PrepHousingFamily".Translate(c.familyCount).Resolve() : "RP_PrepHousingNone".Translate().Resolve();

        public static string SupportText(ReleaseSupport s) => s switch
        {
            ReleaseSupport.Lodging => "RP_PrepSupportLodging".Translate(Money.Format(LodgingPrice)).Resolve(),
            ReleaseSupport.Interview => "RP_PrepSupportInterview".Translate().Resolve(),
            _ => "RP_PrepSupportNone".Translate().Resolve(),
        };

        public static string WhenText(PlanRow row) =>
            (row.hearing ? row.days <= 0f ? "RP_PrepHearingDue".Translate() : "RP_PrepHearingIn".Translate(row.days.ToString("0.0")) : "RP_PrepReleaseIn".Translate(row.days.ToString("0.0"))).Resolve();

        /// <summary>
        /// Why the warden cannot arrange this for them (a keyed string), or null: not in the window, a home already, no certificate or a job waiting, no handler or no room. <paramref name="place"/>: the handlers' answer worked out
        /// once by the caller (the Intake tab asks for every button of every row each frame), or null to work it out here.
        /// </summary>
        public static string Refusal(PrisonGame game, Map map, Pawn p, CompInmate c, ReleaseSupport kind, System.Func<string> place = null)
        {
            if (kind == ReleaseSupport.None || c.releasePlan == kind) return null;
            if (!Eligible(p, c) || !InWindow(c, Now)) return "RP_PrepNotYet";
            if (kind == ReleaseSupport.Lodging && c.familyCount > 0) return "RP_PrepHoused";
            if (kind == ReleaseSupport.Interview)
            {
                if (!Vocation.HasTrade(c)) return "RP_PrepNoQualification";
                if (c.jobWaiting) return "RP_PrepHasJob";
            }
            return c.releasePlan == ReleaseSupport.None ? place != null ? place() : NoPlaceFor(game, map) : null;
        }

        /// <summary>The warden's choice for them: returns the refusal (a keyed string) or null when it is recorded. "None" is always allowed and gives the place back.</summary>
        public static string Choose(PrisonGame game, Map map, Pawn p, ReleaseSupport kind)
        {
            CompInmate c = p.Inmate();
            if (c == null) return "RP_PrepNotYet";
            if (kind == ReleaseSupport.None)
            {
                c.releasePlan = ReleaseSupport.None;
                return null;
            }
            string refusal = Refusal(game, map, p, c, kind);
            if (refusal != null) return refusal;
            c.releasePlan = kind;
            return null;
        }

        /// <summary>The release-planning row on the inmate's file: when, what is known, what is arranged, and the handlers' load. Only for somebody in the window or with something arranged.</summary>
        public static (string text, Color color)? FileLine(PrisonGame game, Map map, Pawn p, CompInmate c)
        {
            if (!Eligible(p, c) || !(InWindow(c, Now) || c.releasePlan != ReleaseSupport.None)) return null;
            float release = DaysToRelease(c), hearing = DaysToHearing(c, Now);
            var row = new PlanRow { pawn = p, c = c, days = Mathf.Min(release, hearing), release = release, hearing = hearing < release };
            string text = "RP_FilePrep".Translate(row.days > WindowDays ? "RP_PrepLater".Translate().Resolve() : WhenText(row), JobText(c), HousingText(c), SupportText(c.releasePlan), Load(game, map), Capacity(map)).Resolve();
            return (text, c.releasePlan != ReleaseSupport.None ? PrisonHUD.Good : PrisonHUD.Muted);
        }

        /// <summary>
        /// The cases the Intake tab lists, and how many open ones it leaves out: every case with a setback waiting for its review first, whatever their number (the help button is on their line, and the setback's letter sends
        /// the warden there), then the other open cases oldest first up to <paramref name="maxOpen"/> lines in all, then the last <paramref name="settled"/> settled ones, newest first.
        /// </summary>
        public static List<SupervisionCase> ShownCases(SupervisionBook book, int maxOpen, int settled, out int hidden)
        {
            var shown = new List<SupervisionCase>();
            List<SupervisionCase> cases = book.cases;
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].state == CaseState.Reviewing && cases[i].setbackState == SetbackState.Pending) shown.Add(cases[i]);
            hidden = 0;
            for (int i = 0; i < cases.Count; i++)
            {
                SupervisionCase sc = cases[i];
                if (sc.state == CaseState.Resolved || sc.state == CaseState.Reviewing && sc.setbackState == SetbackState.Pending) continue;
                if (shown.Count < maxOpen) shown.Add(sc);
                else hidden++;
            }
            for (int i = cases.Count - 1, n = 0; i >= 0 && n < settled; i--)
                if (cases[i].state == CaseState.Resolved)
                {
                    shown.Add(cases[i]);
                    n++;
                }
            return shown;
        }

        /// <summary>How an open or settled case reads on the Intake tab.</summary>
        public static string CaseLine(SupervisionCase sc)
        {
            string support = sc.support == ReleaseSupport.None ? "" : " " + (sc.supportDone ? (sc.supportFulfilled ? "RP_CaseSupportDone" : "RP_CaseSupportUnfunded") : "RP_CaseSupportPromised").Translate(SupportText(sc.support)).Resolve();
            if (sc.state == CaseState.Awaiting) return "RP_CaseAwaiting".Translate(sc.name).Resolve() + support;
            if (sc.state == CaseState.Resolved)
                return (sc.outcome == CaseOutcome.StayedOut ? "RP_CaseStayedOut".Translate(sc.name, sc.reviewDays) : sc.outcome == CaseOutcome.Rearrested ? "RP_CaseRearrested".Translate(sc.name, sc.reason?.Translate().Resolve() ?? "?") : "RP_CaseVoided".Translate(sc.name)).Resolve();
            string job = (sc.jobSecured ? (sc.jobByInterview ? "RP_PrepJobInterview" : "RP_PrepJobHeld") : "RP_PrepJobNone").Translate().Resolve();
            string home = (sc.addressSecured ? (sc.addressByLodging ? "RP_PrepHousingLodged" : "RP_PrepHousingHeld") : "RP_PrepHousingNone").Translate().Resolve();
            string setback = sc.setbackState == SetbackState.Pending
                ? " " + (sc.setback == CaseSetback.JobLost ? "RP_CaseSetbackJob" : "RP_CaseSetbackAddress").Translate(Mathf.Max(0, Supervision.SetbackReviews - (sc.reviewsHeld - sc.setbackAt)), (sc.helped ? "RP_CaseHelped" : "RP_CaseNotHelped").Translate().Resolve()).Resolve()
                : "";
            return "RP_CaseReviewing".Translate(sc.name, sc.reviewsRolled, sc.reviewDays, job, home).Resolve() + support + setback;
        }
    }
}
