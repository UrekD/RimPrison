using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPAmendsDefOf
    {
        public static InmateWantDef RP_Want_Amends;
        public static ThoughtDef RP_MadeAmends;
        public static ThoughtDef RP_AmendsRefused;
        public static JobDef RP_AttendAmends;

        static RPAmendsDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPAmendsDefOf));
    }

    /// <summary>Where an inmate's restorative meeting stands (saved on <see cref="CompInmate.amendsState"/>, with the tick of the last change in <see cref="CompInmate.amendsTick"/>).</summary>
    public enum AmendsState : byte
    {
        None,
        /// <summary>The counsellor's letter waits for the warden's answer.</summary>
        Proposed,
        /// <summary>Approved and paid for: waiting for visiting hours, a free table and a counsellor on site.</summary>
        Approved,
        /// <summary>The victim's relative is on the way or in the visitation room (the inmate's <see cref="CompInmate.visitor"/>).</summary>
        Meeting,
        /// <summary>The warden said no (or let the letter lapse): the counsellor may propose it again after <see cref="Amends.RetryDays"/>.</summary>
        Declined,
        /// <summary>Approved, but the meeting never happened in <see cref="Amends.ApprovedDays"/> days (the fee is refunded); it may be proposed again.</summary>
        Lapsed,
        /// <summary>It went well: the family withdrew its letter to the parole board.</summary>
        Withdrawn,
        /// <summary>It went well enough that the family speaks for them at the board.</summary>
        Supports,
        /// <summary>It went badly: the family left hostile.</summary>
        Hostile,
    }

    /// <summary>
    /// A restorative meeting with the victim's family (gap analysis 7, item 8). For an inmate inside for a violent crime who has passed a program or holds the "make amends" goal, the counsellor
    /// proposes a meeting (a decision letter, which goes against the day's budget like any other; one a day at most, one open per inmate, never with a hearing within a day). The warden approves it,
    /// which costs <see cref="Fee"/> and the counsellor's hours, or declines. Approved, a relative of the victim comes to the visitation room as a visitor who is not the inmate's family (searched at the
    /// gate like any visitor, smuggling nothing), the inmate is brought as for a visit and the counsellor sits with them. The outcome rolls on the odds the letter states (<see cref="Odds"/>: the inmate's
    /// tension and traits, the counsellor at the table and their Social skill, the programs on the inmate's record, the goal, a thorough search at the gate). Well: the family's letter to the parole board
    /// is withdrawn (or the family speaks for them, <see cref="ParoleScene.VictimFor"/>), tension falls and the reoffend band drops a step (<see cref="StepDown"/>). Badly: the family is hostile (it writes
    /// against them at every hearing), the press may hear of it (reputation), tension spikes. Once per stay.
    /// </summary>
    public static class Amends
    {
        public const int Fee = 120;
        /// <summary>The hour the counsellor puts a proposal to the warden (one a day at most).</summary>
        public const int ProposeHour = 10;
        /// <summary>The days an approved meeting waits for the family before it lapses (the fee refunded), and the days before a declined or lapsed one may be proposed again.</summary>
        public const int ApprovedDays = 3, RetryDays = 5;
        /// <summary>The counsellor's hours, as the letter states them (the walk and the meeting).</summary>
        public const int CounsellorHours = 2;
        /// <summary>How long a meeting waits at the table for the counsellor before it goes ahead without them.</summary>
        public const int CounsellorWaitTicks = GenDate.TicksPerHour;
        /// <summary>Tension lost on a good meeting, and gained on a bad one.</summary>
        public const float GoodTension = 20f, BadTension = 25f;
        /// <summary>The share of good meetings in which the family turns to support them; the chance the press hears of a bad one, and what it costs.</summary>
        public const float SupportShare = 0.35f, PressChance = 0.4f, PressHit = 2f;
        /// <summary>The odds: a base, the swing of tension (full at 0, none at <see cref="TensionPivot"/>, against at 100), traits, the counsellor's Social per level or their absence, each program passed (to a cap), the goal, a thorough search.</summary>
        public const float BaseOdds = 0.4f, TensionSwing = 0.3f, TensionPivot = 50f, KindBonus = 0.15f, PsychopathMalus = 0.25f, BloodlustMalus = 0.1f, AbrasiveMalus = 0.1f,
            SocialPerLevel = 0.02f, NoCounsellor = 0.15f, PerProgram = 0.05f, ProgramCap = 0.15f, WantBonus = 0.1f, StripSearched = 0.1f, MinOdds = 0.05f, MaxOdds = 0.9f;

        public const string Decision = "RP_Decision_Amends";

        /// <summary>A check forces the outcome (Withdrawn, Supports or Hostile) and whether the press hears of a bad one; null: the dice.</summary>
        internal static AmendsState? ForceOutcome;
        internal static bool? ForcePress;

        private static int Now => Find.TickManager.TicksGame;

        // ---- the state, as the rest of the game reads it ------------------------------------------------------------

        /// <summary>It went well: the family withdrew its letter (or speaks for them). The reoffend band is a step lower.</summary>
        public static bool Made(CompInmate c) => c != null && c.amendsState is AmendsState.Withdrawn or AmendsState.Supports;

        public static bool Supports(CompInmate c) => c?.amendsState == AmendsState.Supports;

        public static bool Hostile(CompInmate c) => c?.amendsState == AmendsState.Hostile;

        /// <summary>The meeting has been held this stay (once per stay).</summary>
        public static bool Held(CompInmate c) => c != null && c.amendsState >= AmendsState.Withdrawn;

        /// <summary>The reoffend chance a step of the band lower: high to the top of medium, medium to the top of low (low stays as it is).</summary>
        public static float StepDown(float risk) =>
            risk >= ParoleScene.MediumRisk ? ParoleScene.MediumRisk - 0.01f : risk >= ParoleScene.LowRisk ? ParoleScene.LowRisk - 0.01f : risk;

        public static bool HoldsWant(CompInmate c) => c?.want is { def: not null, Resolved: false } w && w.def == RPAmendsDefOf.RP_Want_Amends;

        /// <summary>The tension a good meeting takes off: <see cref="GoodTension"/>, and the goal reached on top for one who holds it (<see cref="Wants.FulfilledRelief"/>).</summary>
        public static float Relief(CompInmate c) => GoodTension + (HoldsWant(c) ? Wants.FulfilledRelief : 0f);

        /// <summary>The prison has a visitation room (without one the visitation pass calls nobody, so an approved meeting could only lapse).</summary>
        public static bool HasRoom(Map map) => !NoRoomForChecks && map != null && PrisonMap.Of(map) is { } pm && pm.zones.Any(RPVisitDefOf.RP_Visitation);

        /// <summary>A check reads the prison as having no visitation room (without painting the live one out).</summary>
        internal static bool NoRoomForChecks;

        /// <summary>A meeting could be held for this inmate at all: a level visited across a table (not supermax), and a visitation room.</summary>
        public static bool CouldMeet(Pawn p, CompInmate c) =>
            c?.security is { escortOnly: false } && PrisonGame.Instance is { } game && game.VisitPolicyFor(c.security) == VisitPolicy.Contact && HasRoom(p?.MapHeld);

        /// <summary>A hearing is on (a case in the hearing room) or due within a day.</summary>
        public static bool HearingWithinADay(PrisonGame game, Pawn p, CompInmate c)
        {
            if (ParoleScene.CaseOf(game, p) != null) return true;
            if (c.IsLifer || DeathRowSystem.Ext(c) != null || !ParoleSystem.MayBeParoled(c)) return false;
            return WantWorker_Parole.HearingTick(c) - Now < GenDate.TicksPerDay;
        }

        /// <summary>A counsellor on site who can work (the one with the best Social skill), or null.</summary>
        public static Pawn Counsellor(Map map)
        {
            Pawn best = null;
            int bestSkill = -1;
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                if (!ProgramSystem.CanTeach(colonists[i], RPProgramDefOf.RP_Counsellor)) continue;
                int skill = Social(colonists[i]);
                if (skill <= bestSkill) continue;
                best = colonists[i];
                bestSkill = skill;
            }
            return best;
        }

        private static int Social(Pawn p) => p?.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 0;

        /// <summary>
        /// Whether the counsellor may propose a meeting for this inmate now (null), or why not (a keyed phrase): an active inmate of a level visited across a table, inside for a violent crime,
        /// with a program passed or the goal, no meeting this stay and none open, not lately declined, a visitation room, no visiting ban, not on remand for a killing inside, no hearing within a day.
        /// </summary>
        public static string Ineligible(PrisonGame game, Pawn p, CompInmate c)
        {
            if (c?.security == null || !InmateUtility.IsActiveInmate(p) || c.security.escortOnly) return "RP_AmendsNo_Away";
            if (c.crime is not { violent: true }) return "RP_AmendsNo_NotViolent";
            if (Held(c)) return "RP_AmendsNo_Held";
            if (c.amendsState is AmendsState.Proposed or AmendsState.Approved or AmendsState.Meeting) return "RP_AmendsNo_Open";
            if (c.amendsState is AmendsState.Declined or AmendsState.Lapsed && Now - c.amendsTick < RetryDays * GenDate.TicksPerDay) return "RP_AmendsNo_Lately";
            if (c.programsPassed <= 0 && !HoldsWant(c)) return "RP_AmendsNo_NotReady";
            if (game.VisitPolicyFor(c.security) != VisitPolicy.Contact) return "RP_AmendsNo_NoTable";
            if (!HasRoom(p.Map)) return "RP_AmendsNo_NoRoom";
            if (c.visitBanUntilTick > Now) return "RP_AmendsNo_Banned";
            if (Trials.OnRemand(game, p)) return "RP_AmendsNo_Remand";
            if (HearingWithinADay(game, p, c)) return "RP_AmendsNo_Hearing";
            return null;
        }

        // ---- the odds ----------------------------------------------------------------------------------------------

        /// <summary>
        /// The chance the meeting goes well, and its parts in words (for the letter and the log): tension, traits, the counsellor at the table (their Social skill) or nobody, the programs passed,
        /// the goal, a thorough search at the gate (only at the meeting: <paramref name="stripSearched"/>).
        /// </summary>
        public static float Odds(Pawn p, CompInmate c, Pawn counsellor, List<string> parts = null, bool stripSearched = false)
        {
            float chance = BaseOdds;
            void Part(float delta, string line)
            {
                chance += delta;
                parts?.Add(line + " " + Signed(delta));
            }
            Part(Mathf.Clamp(TensionSwing * (1f - c.tension / TensionPivot), -TensionSwing, TensionSwing), "RP_AmendsOdds_Tension".Translate(c.tension.ToString("0")));
            TraitSet traits = p.story?.traits;
            if (traits != null)
            {
                if (traits.HasTrait(TraitDefOf.Kind)) Part(KindBonus, TraitDefOf.Kind.DataAtDegree(0).label);
                if (traits.HasTrait(TraitDefOf.Psychopath)) Part(-PsychopathMalus, TraitDefOf.Psychopath.DataAtDegree(0).label);
                if (traits.HasTrait(TraitDefOf.Bloodlust)) Part(-BloodlustMalus, TraitDefOf.Bloodlust.DataAtDegree(0).label);
                if (traits.HasTrait(TraitDefOf.Abrasive)) Part(-AbrasiveMalus, TraitDefOf.Abrasive.DataAtDegree(0).label);
            }
            if (counsellor != null) Part(SocialPerLevel * Social(counsellor), "RP_AmendsOdds_Counsellor".Translate(counsellor.LabelShort, Social(counsellor)));
            else Part(-NoCounsellor, "RP_AmendsOdds_NoCounsellor".Translate());
            if (c.programsPassed > 0) Part(Mathf.Min(ProgramCap, PerProgram * c.programsPassed), "RP_AmendsOdds_Programs".Translate(c.programsPassed));
            if (HoldsWant(c)) Part(WantBonus, "RP_AmendsOdds_Want".Translate());
            if (stripSearched) Part(-StripSearched, "RP_AmendsOdds_Searched".Translate());
            return Mathf.Clamp(chance, MinOdds, MaxOdds);
        }

        private static string Signed(float delta) => (delta < 0f ? "-" : "+") + Mathf.Abs(Mathf.RoundToInt(delta * 100f)) + "%";

        // ---- the counsellor proposes ---------------------------------------------------------------------------------

        /// <summary>
        /// Hourly: each inmate's meeting is kept in step (a proposal whose letter is gone, a family that left without a meeting, an approval that waited too long), and at <see cref="ProposeHour"/> the
        /// counsellor proposes one meeting (the goal's holders first, then the most programs passed).
        /// </summary>
        public static void Hourly(PrisonGame game, Map map, int hour)
        {
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            int now = Now;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { amendsState: not AmendsState.None } c) HourlyFor(game, inmates[i], c, now);
            if (hour != ProposeHour || Counsellor(map) is not { } counsellor) return;
            Pawn best = null;
            for (int i = 0; i < inmates.Count; i++)
            {
                CompInmate c = inmates[i].Inmate();
                if (c == null || Ineligible(game, inmates[i], c) != null) continue;
                if (best == null || Rank(c) > Rank(best.Inmate())) best = inmates[i];
            }
            if (best != null) Propose(game, map, best, counsellor);
        }

        private static int Rank(CompInmate c) => (HoldsWant(c) ? 100 : 0) + c.programsPassed;

        /// <summary>One inmate's meeting kept in step (see <see cref="Hourly"/>).</summary>
        public static void HourlyFor(PrisonGame game, Pawn p, CompInmate c, int now)
        {
            switch (c.amendsState)
            {
                case AmendsState.Proposed:
                    if (!LetterOpen(p)) c.amendsState = AmendsState.None;
                    break;
                case AmendsState.Meeting:
                    if (c.visitor == null) c.amendsState = AmendsState.Approved;
                    break;
                case AmendsState.Approved:
                    if (now - c.amendsTick <= ApprovedDays * GenDate.TicksPerDay) break;
                    c.amendsState = AmendsState.Lapsed;
                    c.amendsTick = now;
                    game.ledger.Add(RPDefOf.RP_Programs, Fee, "RP_AmendsRefundNote".Translate(p.LabelShort));
                    Messages.Message("RP_AmendsLapsed".Translate(p.LabelShort, Money.Format(Fee)), p, MessageTypeDefOf.NeutralEvent, historical: false);
                    break;
            }
        }

        /// <summary>The counsellor's letter for this inmate is open (on the stack or held for tomorrow).</summary>
        public static bool LetterOpen(Pawn p) => StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Any(l => !l.answered && l.Is(Decision) && l.pawns.Contains(p));

        /// <summary>The counsellor's letter: who, why, the odds and their parts, the fee and the hours, and what each outcome means.</summary>
        public static ChoiceLetter_PrisonDecision Propose(PrisonGame game, Map map, Pawn p, Pawn counsellor)
        {
            CompInmate c = p.Inmate();
            var parts = new List<string>();
            float chance = Odds(p, c, counsellor, parts);
            float without = Odds(p, c, null);
            string why = HoldsWant(c) ? "RP_AmendsWhy_Want".Translate().ToString() : "RP_AmendsWhy_Programs".Translate(c.programsPassed).ToString();
            // (A thorough search at the gate costs the family's goodwill: stated when the prison searches visitors thoroughly now.)
            string searched = VisitorSearch.PolicyFor(c, game) == VisitorSearchPolicy.Thorough
                ? " " + "RP_AmendsSearchedPart".Translate(StripSearched.ToStringPercent(), Odds(p, c, counsellor, stripSearched: true).ToStringPercent()).Resolve()
                : "";
            TaggedString text = "RP_AmendsText".Translate(counsellor.LabelShort, p.LabelShort, c.crime?.label ?? "?", CounsellorHours, Money.Format(Fee), why, chance.ToStringPercent(), string.Join("; ", parts),
                without.ToStringPercent(), Signed(-ParoleScene.VictimAgainst), Signed(ParoleScene.VictimFor), Relief(c).ToString("0"), BadTension.ToString("0"), PressChance.ToStringPercent(), PressHit.ToString("0"),
                searched, Signed(SocialPerLevel));
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(Decision, "RP_AmendsLabel".Translate(p.LabelShort), text, map, new LookTargets(new[] { p, counsellor }));
            letter.pawns.Add(p);
            letter.money = Fee;
            letter.StartTimeout(GenDate.TicksPerDay);
            c.amendsState = AmendsState.Proposed;
            c.amendsTick = Now;
            Find.LetterStack.ReceiveLetter(letter);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:amends] {counsellor.LabelShort} proposes a meeting for {p.LabelShort}: odds {chance:P0} ({string.Join("; ", parts)})");
            return letter;
        }

        /// <summary>The warden approves: the fee is paid and the family is called for the next visiting hours with a counsellor on site.</summary>
        public static bool Approve(Pawn p, CompInmate c, Map map)
        {
            if (c == null || c.amendsState != AmendsState.Proposed || !InmateUtility.IsActiveInmate(p))
            {
                if (c is { amendsState: AmendsState.Proposed }) c.amendsState = AmendsState.None;
                Messages.Message("RP_AmendsNoLonger".Translate(p?.LabelShort ?? "?"), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            if (!PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Programs, Fee, "RP_AmendsNote".Translate(p.LabelShort)))
            {
                c.amendsState = AmendsState.None;
                Messages.Message("RP_CannotAfford".Translate(Money.Format(Fee)), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            c.amendsState = AmendsState.Approved;
            c.amendsTick = Now;
            Messages.Message("RP_AmendsApproved".Translate(p.LabelShort), p, MessageTypeDefOf.SilentInput, historical: false);
            Wants.Refresh(p, c);
            return true;
        }

        /// <summary>The warden declines (or lets the letter lapse): the counsellor may ask again after <see cref="RetryDays"/>; a goal to make amends is set back.</summary>
        public static void Decline(Pawn p, CompInmate c)
        {
            if (c == null || c.amendsState != AmendsState.Proposed) return;
            c.amendsState = AmendsState.Declined;
            c.amendsTick = Now;
            if (HoldsWant(c)) Wants.Thwart(p, c, c.want, "RP_Want_Reason_AmendsDeclined");
        }

        // ---- the meeting ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The visitation pass (visiting hours): an approved meeting calls the family when a counsellor is on site and the inmate could be visited at a table now. Before the families' own visits,
        /// and within the room's places.
        /// </summary>
        public static void HourlyVisits(Map map, PrisonGame game, int hour, int maxVisits, int booths, int boothsInUse, ref int active)
        {
            if (active >= maxVisits || Counsellor(map) == null) return;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (active >= maxVisits) return;
                if (p.Inmate() is not { amendsState: AmendsState.Approved } c) continue;
                if (!VisitationSystem.CanBeVisited(p, c, game, hour, booths, boothsInUse, out VisitPolicy policy) || policy != VisitPolicy.Contact) continue;
                if (CallFamily(map, p, c)) active++;
            }
        }

        /// <summary>The victim's relative sets out for the visitation room (a visitor who is not the inmate's family).</summary>
        public static bool CallFamily(Map map, Pawn p, CompInmate c)
        {
            if (c.amendsState != AmendsState.Approved || !VisitationSystem.SpawnVisitor(map, p, c, booth: false, amends: true)) return false;
            c.amendsState = AmendsState.Meeting;
            meetingsStamp = -1;
            if (AutoTest.Active) RPLog.Message($"[RimPrison:amends] the victim's family ({c.visitor?.LabelShort}) is on the way for {p.LabelShort}");
            return true;
        }

        /// <summary>A visit ends (<see cref="VisitationSystem.EndVisit"/>): a meeting that was not held waits for the family to come again.</summary>
        public static void Notify_VisitEnded(CompInmate c)
        {
            if (c.amendsState == AmendsState.Meeting) c.amendsState = AmendsState.Approved;
            meetingsStamp = -1;
        }

        /// <summary>
        /// The inmate leaves for good (released, paroled, transferred, dead, escaped: <see cref="StorySystem.Notify_Leaving"/>) with a meeting approved or under way: the family is sent home, the
        /// meeting lapses and the fee is refunded. (Before a release clears the stay; a transfer or a death leaves the state on a pawn the hourly pass never sees again.)
        /// </summary>
        public static void Notify_Leaving(Pawn p)
        {
            if (p?.Inmate() is not { amendsState: AmendsState.Approved or AmendsState.Meeting } c) return;
            if (c.amendsState == AmendsState.Meeting && c.visitor != null) VisitationSystem.EndVisit(c, c.visitor);
            c.amendsState = AmendsState.Lapsed;
            c.amendsTick = Now;
            meetingsStamp = -1;
            PrisonGame.Instance.ledger.Add(RPDefOf.RP_Programs, Fee, "RP_AmendsRefundNote".Translate(p.LabelShort));
            Messages.Message("RP_AmendsLeftRefunded".Translate(p.LabelShort, Money.Format(Fee)), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>The family reached the visitation room: a counsellor who can drop what they are doing comes to the table (not one teaching a class: the class goes first, and the family waits for them, an hour at the most).</summary>
        public static void Notify_FamilyArrived(Pawn p, CompInmate c)
        {
            if (c.amendsState != AmendsState.Meeting || !p.Spawned) return;
            meetingsStamp = -1;
            if (TableOver(p, c.visitor)) return;
            if (Counsellor(p.Map) is { } counsellor && counsellor.CurJob != null && counsellor.CurJobDef != RPAmendsDefOf.RP_AttendAmends && counsellor.CurJobDef != RPProgramDefOf.RP_TeachClass
                && ProgramSystem.CanInterrupt(counsellor))
                counsellor.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        /// <summary>The counsellor sitting with this inmate's family: on the job, at the table (near the inmate or the visitor), or null.</summary>
        public static Pawn AtTheTable(Pawn inmate, Pawn visitor)
        {
            if (visitor == null || !visitor.Spawned) return null;
            IReadOnlyList<Pawn> colonists = visitor.Map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn s = colonists[i];
                if (s.CurJobDef == RPAmendsDefOf.RP_AttendAmends && s.CurJob.targetA.Thing == visitor && (s.Position.InHorDistOf(visitor.Position, 5f) || inmate.Spawned && s.Position.InHorDistOf(inmate.Position, 5f)))
                    return s;
            }
            return null;
        }

        /// <summary>The inmate may sit down with the family: no meeting (any visit), or the counsellor is at the table, or none is on site, or the family has waited <see cref="CounsellorWaitTicks"/>.</summary>
        public static bool ReadyToTalk(Pawn inmate, CompInmate c)
        {
            if (c.amendsState != AmendsState.Meeting || c.visitor is not { Spawned: true } v) return true;
            if (c.visitorArrivedTick >= 0 && Now - c.visitorArrivedTick >= CounsellorWaitTicks) return true;
            return AtTheTable(inmate, v) != null || Counsellor(v.Map) == null;
        }

        private static readonly List<Pawn> meetingVisitors = new List<Pawn>();
        private static int meetingsStamp = -1;
        private static Map meetingsMap;

        /// <summary>A game is loaded or started (<see cref="PrisonGame.FinalizeInit"/>): the families cache lets go of the last game's map and visitors.</summary>
        public static void Reset()
        {
            meetingVisitors.Clear();
            meetingsMap = null;
            meetingsStamp = -1;
        }

        /// <summary>The families waiting in the visitation room for a meeting (for the counsellor's work scan: rebuilt at most every 250 ticks, or when a meeting starts or ends).</summary>
        public static List<Pawn> FamiliesWaiting(Map map)
        {
            int now = Now;
            if (map == meetingsMap && meetingsStamp >= 0 && now >= meetingsStamp && now - meetingsStamp < 250) return meetingVisitors;
            meetingsMap = map;
            meetingsStamp = now;
            meetingVisitors.Clear();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { amendsState: AmendsState.Meeting, visitor: { Spawned: true } v, visitorArrivedTick: >= 0 }) meetingVisitors.Add(v);
            return meetingVisitors;
        }

        /// <summary>
        /// The counsellor at the table may get up: the meeting is over (or is no longer this family's), the inmate is gone (dead, off the map, released or on the way out), or a lockdown holds
        /// them (a visit is not ended by one, so the family may still be waiting, but nobody is brought out to them).
        /// </summary>
        public static bool TableOver(Pawn inmate, Pawn visitor) =>
            inmate?.Inmate() is not { amendsState: AmendsState.Meeting } c || c.visitor != visitor || inmate.Dead || !inmate.Spawned || !InmateUtility.IsActiveInmate(inmate)
            || PrisonGame.Instance?.LockedDown(c) == true;

        /// <summary>A counsellor sitting in on a meeting may be called away (to a class that starts, <see cref="ProgramSystem.CanInterrupt"/>) while the inmate has not sat down with the family yet.</summary>
        public static bool MayLeaveTable(Job job) =>
            job?.targetB.Thing is not Pawn inmate || inmate.CurJobDef != RPVisitDefOf.RP_VisitTalk || inmate.CurJob.targetA.Thing != job.targetA.Thing;

        /// <summary>The inmate a family came to meet (a meeting under way), or null.</summary>
        public static Pawn InmateMeeting(Pawn visitor) =>
            VisitationSystem.InmateOf(visitor) is { } p && p.Inmate() is { amendsState: AmendsState.Meeting } c && c.visitor == visitor ? p : null;

        /// <summary>The talk is over (<see cref="VisitationSystem.Complete"/>): the outcome, with whoever is at the table.</summary>
        public static void Complete(Pawn p, CompInmate c, Pawn visitor) => Conclude(p, c, AtTheTable(p, visitor), c.visitorStripSearched);

        /// <summary>
        /// The outcome of a meeting that is under way (once: a meeting already concluded is not concluded again). Returns false when there was none to conclude.
        /// </summary>
        public static bool Conclude(Pawn p, CompInmate c, Pawn counsellor, bool stripSearched = false)
        {
            if (c == null || c.amendsState != AmendsState.Meeting) return false;
            var parts = new List<string>();
            float chance = Odds(p, c, counsellor, parts, stripSearched);
            // (What the letter says the tension falls by: the goal, reached below, takes its own share for one who holds it.)
            float relief = Relief(c);
            AmendsState outcome = ForceOutcome ?? (Rand.Chance(chance) ? (Rand.Chance(SupportShare) ? AmendsState.Supports : AmendsState.Withdrawn) : AmendsState.Hostile);
            c.amendsState = outcome;
            c.amendsTick = Now;
            PrisonGame game = PrisonGame.Instance;
            string with = counsellor != null ? "RP_AmendsWith".Translate(counsellor.LabelShort).ToString() : "RP_AmendsWithNobody".Translate().ToString();
            // (A case still to sit is marked now, and is read again at the sitting anyway (ParoleScene.Assess). One already assessed - the board's odds worked out and stated, the
            // warden's letter perhaps out - is left as it is: the family's change of heart counts at the hearings that come after it.)
            ParoleCase hc = ParoleScene.CaseOf(game, p) is { assessed: false } open ? open : null;
            if (outcome != AmendsState.Hostile)
            {
                c.tension = Mathf.Max(0f, c.tension - GoodTension);
                if (hc != null && hc.victimLetter) hc.victimWithdrawn = true;
                p.needs?.mood?.thoughts.memories.TryGainMemory(RPAmendsDefOf.RP_MadeAmends);
                bool supports = outcome == AmendsState.Supports;
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, (supports ? "RP_Chr_AmendsSupports" : "RP_Chr_AmendsWithdrawn").Translate(p.LabelShort, with), p, counsellor);
                Find.LetterStack.ReceiveLetter("RP_AmendsGoodLabel".Translate(p.LabelShort), (supports ? "RP_AmendsSupportsText" : "RP_AmendsWithdrawnText").Translate(p.LabelShort, with, chance.ToStringPercent(), relief.ToString("0"),
                    Signed(-ParoleScene.VictimAgainst), Signed(ParoleScene.VictimFor)), LetterDefOf.PositiveEvent, p);
            }
            else
            {
                c.tension = Mathf.Min(100f, c.tension + BadTension);
                c.mainCause = "RP_Cause_AmendsWentBadly";
                if (hc != null && c.crime is { violent: true }) (hc.victimLetter, hc.victimWithdrawn) = (true, false);
                p.needs?.mood?.thoughts.memories.TryGainMemory(RPAmendsDefOf.RP_AmendsRefused);
                bool press = ForcePress ?? Rand.Chance(PressChance);
                if (press) game.reputation = Mathf.Max(0f, game.reputation - PressHit);
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, (press ? "RP_Chr_AmendsHostilePress" : "RP_Chr_AmendsHostile").Translate(p.LabelShort, with), p, counsellor);
                Find.LetterStack.ReceiveLetter("RP_AmendsBadLabel".Translate(p.LabelShort), "RP_AmendsHostileText".Translate(p.LabelShort, with, chance.ToStringPercent(), BadTension.ToString("0"),
                    press ? "RP_AmendsPress".Translate(PressHit.ToString("0")).ToString() : "RP_AmendsNoPress".Translate().ToString()), LetterDefOf.NegativeEvent, p);
            }
            Wants.Refresh(p, c);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:amends] meeting for {p.LabelShort}, {with}: odds {chance:P0} ({string.Join("; ", parts)}), outcome {outcome}");
            return true;
        }

        // ---- the file --------------------------------------------------------------------------------------------------

        /// <summary>Where the meeting stands, for the inmate's file (null: nothing to say).</summary>
        public static (string, Color)? FileLine(CompInmate c)
        {
            int day = c.amendsTick / GenDate.TicksPerDay + 1;
            switch (c.amendsState)
            {
                case AmendsState.Proposed: return ("RP_FileAmends_Proposed".Translate(), PrisonHUD.Muted);
                case AmendsState.Approved: return ("RP_FileAmends_Approved".Translate((c.amendsTick + ApprovedDays * GenDate.TicksPerDay) / GenDate.TicksPerDay + 1), PrisonHUD.Muted);
                case AmendsState.Meeting: return ("RP_FileAmends_Meeting".Translate(), PrisonHUD.Muted);
                case AmendsState.Declined: return ("RP_FileAmends_Declined".Translate(day), PrisonHUD.Muted);
                case AmendsState.Lapsed: return ("RP_FileAmends_Lapsed".Translate(day), PrisonHUD.Muted);
                case AmendsState.Withdrawn: return ("RP_FileAmends_Withdrawn".Translate(day), PrisonHUD.Good);
                case AmendsState.Supports: return ("RP_FileAmends_Supports".Translate(day), PrisonHUD.Good);
                case AmendsState.Hostile: return ("RP_FileAmends_Hostile".Translate(day), PrisonHUD.Bad);
                default: return null;
            }
        }

        /// <summary>For the saveload snapshot.</summary>
        public static string Describe(CompInmate c) => $"{c.amendsState}@{c.amendsTick}";
    }

    /// <summary>The counsellor's letter: approve (the fee and the counsellor's hours) or decline (also when it lapses).</summary>
    public class PrisonDecisionWorker_Amends : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Amends_Approve", "RP_Amends_Decline" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < Amends.Fee ? "RP_CannotAfford".Translate(Money.Format(Amends.Fee)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            CompInmate c = p?.Inmate();
            if (option == 0) Amends.Approve(p, c, map);
            else Amends.Decline(p, c);
        }
    }

    /// <summary>A counsellor sits with the inmate and the victim's family while they meet (an emergency giver of the counselling work, like a class).</summary>
    public class WorkGiver_AttendAmends : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            pawn.GetComp<CompStaff>()?.role != RPProgramDefOf.RP_Counsellor || PrisonGame.Instance == null || Amends.FamiliesWaiting(pawn.Map).Count == 0;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => Amends.FamiliesWaiting(pawn.Map).Where(v => v.Spawned).ToList();

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            // (Not to a table the counsellor would get up from at once: a lockdown, the inmate gone.)
            if (t is not Pawn visitor || Amends.InmateMeeting(visitor) is not { } inmate || Amends.TableOver(inmate, visitor) || !ProgramSystem.CanTeach(pawn, RPProgramDefOf.RP_Counsellor)) return false;
            // (One counsellor a table. The family is not reserved: a guard's search reserves them, and a reservation of ours would read as a search under way.)
            IReadOnlyList<Pawn> colonists = pawn.Map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                if (colonists[i] != pawn && colonists[i].CurJobDef == RPAmendsDefOf.RP_AttendAmends && colonists[i].CurJob.targetA.Thing == visitor) return false;
            return pawn.CanReach(visitor, PathEndMode.Touch, Danger.Some);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPAmendsDefOf.RP_AttendAmends, t, Amends.InmateMeeting((Pawn)t));
    }

    /// <summary>
    /// To the family's table, and there until the meeting is over (four hours at the most), the inmate is gone or a lockdown holds them (<see cref="Amends.TableOver"/>). A class that starts may call
    /// the counsellor away while the inmate has not sat down yet (<see cref="Amends.MayLeaveTable"/>).
    /// </summary>
    public class JobDriver_AttendAmends : JobDriver
    {
        private Pawn Visitor => (Pawn)job.targetA.Thing;
        private Pawn Inmate => job.targetB.Thing as Pawn;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Amends.TableOver(Inmate, Visitor));
            Toil go = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return go;
            Toil sit = Toils_General.Wait(GenDate.TicksPerHour * 4);
            sit.handlingFacing = true;
            sit.socialMode = RandomSocialMode.Off;
            sit.tickIntervalAction = delta =>
            {
                // (The family may have walked on to their seat since the counsellor reached them: back to their side, or the table would not count them as there - Amends.AtTheTable.)
                if (pawn.IsHashIntervalTick(250, delta) && !pawn.Position.InHorDistOf(Visitor.Position, 3f))
                {
                    JumpToToil(go);
                    return;
                }
                pawn.rotationTracker.FaceTarget(Inmate is { Spawned: true } i && i.CurJobDef == RPVisitDefOf.RP_VisitTalk ? i : Visitor);
                if (pawn.IsHashIntervalTick(500, delta)) MoteMaker.MakeInteractionBubble(pawn, Visitor, ThingDefOf.Mote_Speech, InteractionDefOf.Chitchat.GetSymbol());
            };
            yield return sit;
        }
    }
}
