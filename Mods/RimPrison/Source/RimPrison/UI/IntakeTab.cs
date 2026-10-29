using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Intake tab: the shuttle's status, the intake hour, and how many of each security level the state sends.</summary>
    public static class IntakeTab
    {
        private static Vector2 scroll;
        internal const float FirstViewHeight = 900f;
        private static float viewHeight = FirstViewHeight;

        /// <summary>
        /// The tab's listing. One column: the view is the content's height in the last frame, and a listing that runs past it wraps into a second column (off to the right, clipped) and reports only that
        /// column's height, so the next view is shorter still, until not even the first label fits and the pane is empty for good (the release-plan tour, round 4).
        /// </summary>
        internal static Listing_Standard NewListing() => new Listing_Standard { maxOneColumn = true };

        private static float prepTop;
        private static bool scrollToPrep;

        /// <summary>For the UI tour: the pane scrolled down to the release planning once the tab has been drawn.</summary>
        internal static void ScrollToReleasePrep() => scrollToPrep = true;

        public static void Draw(Rect rect, PrisonGame game, Map map)
        {
            Listing_Standard list = NewListing();
            var view = new Rect(0f, 0f, rect.width - 16f, viewHeight);
            Widgets.BeginScrollView(rect, ref scroll, view);
            list.Begin(view);
            list.Label("RP_IntakeHelp".Translate());
            list.Gap();
            bool hasPad = ShuttleSystem.HasPad(map);
            GUI.color = hasPad ? PrisonHUD.Muted : PrisonHUD.Warn;
            list.Label((hasPad ? "RP_IntakePadBuilt" : "RP_IntakePadNone").Translate());
            GUI.color = Color.white;
            if (game.shuttle.visit is { } visit) list.Label(ShuttleSystem.StatusLine(visit));
            // The state's own prisoners (rearrested ex-inmates, recaptured escapees) wait for the next shuttle.
            if (game.shuttle.MarshalsWaiting() is var marshals and > 0) list.Label("RP_IntakeMarshals".Translate(marshals));
            list.Gap();
            list.Label("RP_IntakeHourLine".Translate(game.intake.hour));
            game.intake.hour = (int)list.Slider(game.intake.hour, 0, 23);
            list.Gap();
            foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(s => s.uiOrder))
            {
                // (Supermax is not sent by the state: nothing to ask for here.)
                if (sec.escortOnly || sec.referralOnly) continue;
                Rect row = list.GetRect(30f);
                if (Unlocks.GateOf(sec) is { } gate && !Unlocks.IsUnlocked(sec))
                {
                    GUI.color = Color.gray;
                    Widgets.Label(new Rect(row.x + 28f, row.y + 4f, 900f, 26f), sec.LabelCap + ": " + "RP_LockedBy".Translate(gate.label));
                    GUI.color = Color.white;
                    continue;
                }
                int n = game.intake.PerDay(sec);
                Widgets.DrawBoxSolid(new Rect(row.x, row.y + 5f, 20f, 20f), sec.color);
                var labelRect = new Rect(row.x + 28f, row.y + 4f, 260f, 26f);
                Widgets.Label(labelRect, "RP_IntakeLevelLine".Translate(sec.LabelCap, n));
                TooltipHandler.TipRegion(labelRect, sec.description);
                string money = "RP_IntakeMoney".Translate(Money.Format(sec.intakeGrant), Money.Format(sec.dailyPayment));
                if (sec.wingOnly)
                {
                    // (A wing kept for one sex counts the beds that sex may use: the other's beds are no room for them.)
                    WingGender kept = game.WingGenderFor(sec);
                    money += "  " + (kept == WingGender.All ? "RP_IntakeWingOnly".Translate(IntakeSystem.FreeBedCount(map, sec))
                        : "RP_IntakeWingOnlyFor".Translate(IntakeSystem.FreeBedCount(map, sec, gender: kept == WingGender.Men ? Gender.Male : Gender.Female), IntakeSystem.WhoWord(sec)));
                }
                var lessRect = new Rect(row.x + 290f, row.y, 30f, 28f);
                var moreRect = new Rect(row.x + 324f, row.y, 30f, 28f);
                TooltipHandler.TipRegion(lessRect, "RP_IntakeLessTip".Translate(sec.label));
                TooltipHandler.TipRegion(moreRect, "RP_IntakeMoreTip".Translate(sec.label));
                if (Widgets.ButtonText(lessRect, "-") && n > 0) game.intake.perDay[sec] = n - 1;
                if (Widgets.ButtonText(moreRect, "+") && n < 30) game.intake.perDay[sec] = n + 1;
                Widgets.Label(new Rect(row.x + 370f, row.y + 4f, 400f, 26f), money);
                // Who lives in the level's wing: everyone, or men or women only (men and women are kept apart in the cells either way).
                var sexRect = new Rect(row.x + 780f, row.y, 150f, 28f);
                WingGender wingGender = game.WingGenderFor(sec);
                if (Widgets.ButtonText(sexRect, ("RP_WingGender_" + wingGender).Translate()))
                    game.wingGenders[sec] = (WingGender)(((int)wingGender + 1) % 3);
                TooltipHandler.TipRegion(sexRect, "RP_WingGenderDesc".Translate(sec.label));
            }
            // Who is left to move out of a wing now kept for the other sex (the daily review does it as beds of theirs come free).
            if (Cellmates.InWrongWing(map, out int nowhereToGo) is var stranded and > 0)
            {
                GUI.color = PrisonHUD.Warn;
                list.Label("RP_IntakeWrongWing".Translate(stranded, nowhereToGo, Cellmates.WrongWingMovesPerDay));
                GUI.color = Color.white;
            }
            list.Gap();
            list.CheckboxLabeled("RP_AllowOvercrowding".Translate(), ref game.intake.allowOvercrowding, "RP_AllowOvercrowdingDesc".Translate());
            list.Gap();
            list.Label("RP_FreeBeds".Translate(IntakeSystem.FreeBedCount(map), IntakeSystem.Capacity(map)));
            if (Prefs.DevMode && list.ButtonText("DEV: run intake now")) IntakeSystem.RunIntake(game, map);
            list.Gap();
            DrawCustody(list, game, map);
            list.Gap();
            prepTop = list.CurHeight;
            DrawReleasePrep(list, game, map);
            viewHeight = list.CurHeight;
            list.End();
            Widgets.EndScrollView();
            if (scrollToPrep)
            {
                scroll.y = prepTop;
                scrollToPrep = false;
            }
        }

        private static List<ReleasePrep.PlanRow> planRows = new List<ReleasePrep.PlanRow>();
        private static int planTick = -999;
        private static Map planMap;
        private const int MaxRows = 12;

        /// <summary>Release planning: who is within five days of release or a hearing with what is known of their job and housing and the three choices, the handlers' load, the unresolved cohort apart from the outcomes, and the cases under review with their setbacks.</summary>
        private static void DrawReleasePrep(Listing_Standard list, PrisonGame game, Map map)
        {
            Text.Font = GameFont.Medium;
            Rect head = list.GetRect(Text.LineHeight);
            Widgets.Label(head, "RP_PrepHeader".Translate());
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(head, "RP_PrepHeaderTip".Translate());
            int handlers = ReleasePrep.Handlers(map);
            list.Label("RP_PrepCapacityLine".Translate(ReleasePrep.Load(game, map), handlers * ReleasePrep.CasesPerHandler, handlers, ReleasePrep.CasesPerHandler));
            // (The handlers' answer once a frame, not once for every button of every row: each asks for a pass over the prisoners.)
            string noPlace = ReleasePrep.NoPlaceFor(game, map);
            System.Func<string> place = () => noPlace;
            SupervisionBook book = game.supervision;
            GUI.color = PrisonHUD.Muted;
            list.Label("RP_PrepCohortLine".Translate(book.OpenCount, book.Count(CaseOutcome.StayedOut), book.Count(CaseOutcome.Rearrested)));
            GUI.color = Color.white;
            int tick = Find.TickManager.TicksGame;
            if (planMap != map || tick - planTick > 30 || tick < planTick)
            {
                planRows = ReleasePrep.Rows(map);
                (planMap, planTick) = (map, tick);
            }
            if (planRows.Count == 0)
            {
                GUI.color = PrisonHUD.Muted;
                list.Label("RP_PrepNoRows".Translate());
                GUI.color = Color.white;
            }
            for (int i = 0; i < planRows.Count && i < MaxRows; i++)
            {
                ReleasePrep.PlanRow row = planRows[i];
                if (row.pawn == null || row.pawn.Destroyed || row.c == null) continue;
                list.Label("RP_PrepRowLine".Translate(row.pawn.LabelShort, ReleasePrep.WhenText(row), ReleasePrep.JobText(row.c), ReleasePrep.HousingText(row.c)));
                Rect buttons = list.GetRect(26f);
                float w = (buttons.width - 16f) / 3f;
                PlanButton(new Rect(buttons.x, buttons.y, w, buttons.height), game, map, row, ReleaseSupport.Lodging, "RP_PrepBtnLodging".Translate(Money.Format(ReleasePrep.LodgingPrice)), place);
                PlanButton(new Rect(buttons.x + w + 8f, buttons.y, w, buttons.height), game, map, row, ReleaseSupport.Interview, "RP_PrepBtnInterview".Translate(), place);
                PlanButton(new Rect(buttons.x + 2f * (w + 8f), buttons.y, w, buttons.height), game, map, row, ReleaseSupport.None, "RP_PrepBtnNone".Translate(), place);
            }
            if (planRows.Count > MaxRows)
            {
                GUI.color = PrisonHUD.Muted;
                list.Label("RP_PrepMore".Translate(planRows.Count - MaxRows));
                GUI.color = Color.white;
            }
            // (A setback waiting for its review is always listed, first: the help button is on its line, and its letter sends the warden here. The other open cases fill the rest of the rows.)
            List<SupervisionCase> shown = ReleasePrep.ShownCases(book, MaxRows, 3, out int hidden);
            if (shown.Count == 0) return;
            list.Gap(4f);
            Widgets.Label(list.GetRect(Text.LineHeight), "RP_PrepCasesHeader".Translate());
            bool moreSaid = hidden == 0;
            foreach (SupervisionCase sc in shown)
            {
                if (!moreSaid && sc.state == CaseState.Resolved)
                {
                    moreSaid = true;
                    GUI.color = PrisonHUD.Muted;
                    list.Label("RP_PrepMore".Translate(hidden));
                }
                GUI.color = sc.state == CaseState.Resolved ? (sc.outcome == CaseOutcome.StayedOut ? PrisonHUD.Good : sc.outcome == CaseOutcome.Rearrested ? PrisonHUD.Warn : PrisonHUD.Muted) : sc.setbackState == SetbackState.Pending ? PrisonHUD.Warn : Color.white;
                list.Label(ReleasePrep.CaseLine(sc));
                GUI.color = Color.white;
                if (sc.state != CaseState.Reviewing || sc.setbackState != SetbackState.Pending) continue;
                Rect help = list.GetRect(26f);
                string refusal = Supervision.HelpRefusal(game, map, sc);
                var button = new Rect(help.x, help.y, 240f, help.height);
                TooltipHandler.TipRegion(button, refusal != null ? refusal.Translate().ToString() : "RP_PrepHelpTip".Translate(Supervision.HelpSuccess.ToStringPercent()).ToString());
                if (Widgets.ButtonText(button, "RP_PrepBtnHelp".Translate(Money.Format(Supervision.HelpPrice)), active: refusal == null))
                {
                    string why = Supervision.OfferHelp(game, map, sc);
                    if (why != null) Messages.Message(why.Translate(), MessageTypeDefOf.RejectInput, historical: false);
                }
            }
            if (!moreSaid)
            {
                GUI.color = PrisonHUD.Muted;
                list.Label("RP_PrepMore".Translate(hidden));
                GUI.color = Color.white;
            }
        }

        private static void PlanButton(Rect r, PrisonGame game, Map map, ReleasePrep.PlanRow row, ReleaseSupport kind, string label, System.Func<string> place)
        {
            string refusal = ReleasePrep.Refusal(game, map, row.pawn, row.c, kind, place);
            bool chosen = row.c.releasePlan == kind;
            if (chosen) Widgets.DrawHighlightSelected(r);
            if (refusal != null) TooltipHandler.TipRegion(r, refusal.Translate().ToString());
            if (!Widgets.ButtonText(r, label, active: refusal == null && !chosen)) return;
            string why = ReleasePrep.Choose(game, map, row.pawn, kind);
            if (why != null) Messages.Message(why.Translate(), MessageTypeDefOf.RejectInput, historical: false);
            planTick = -999;
        }

        /// <summary>The custody contract: the running one with its cohort and progress, else the open offer, else when the next is due (or why none is made now), and how the last one ended.</summary>
        private static void DrawCustody(Listing_Standard list, PrisonGame game, Map map)
        {
            CustodyBook b = game.custody;
            if (b == null) return;
            Text.Font = GameFont.Medium;
            Rect head = list.GetRect(Text.LineHeight);
            Widgets.Label(head, "RP_CustodyHeader".Translate());
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(head, "RP_CustodyHeaderTip".Translate());
            if (b.contract is { } c)
            {
                GUI.color = PrisonHUD.Muted;
                list.Label(CustodyContracts.SummaryLine(c));
                GUI.color = Color.white;
                foreach (CustodyMember m in c.members)
                {
                    GUI.color = m.state switch
                    {
                        CustodyState.Met => PrisonHUD.Good,
                        CustodyState.Missed or CustodyState.Forfeited or CustodyState.Dropped => PrisonHUD.Warn,
                        _ => Color.white,
                    };
                    list.Label(CustodyContracts.MemberLine(c, m));
                }
                GUI.color = Color.white;
                if (c.Count(CustodyState.Pending) > 0) list.Label("RP_CustodyBedsLine".Translate(CustodyContracts.BedsLine(map, c.members)));
                GUI.color = PrisonHUD.Muted;
                list.Label("RP_CustodyDemandLine".Translate(CustodyContracts.DemandLine(map, c.def, c.members.Count(m => m.Open))));
                GUI.color = Color.white;
            }
            else if (b.offer is { } offer)
            {
                list.Label("RP_CustodyOfferOpenLine".Translate(offer.rows.Count, Money.Format(offer.rows.Sum(r => r.fee)), Money.Format(offer.rows.Sum(r => r.bonus)),
                    CustodyContracts.OfferHoursLeft(game, offer)));
                // (The letter's own numbers are those of the day it came: these are the prison's now, for the warden preparing beds, a classroom and counsellors before answering.)
                list.Label("RP_CustodyBedsLine".Translate(CustodyContracts.BedsLine(map, offer.rows)));
                GUI.color = PrisonHUD.Muted;
                list.Label("RP_CustodyDemandLine".Translate(CustodyContracts.DemandLine(map, offer.def, offer.rows.Count)));
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = PrisonHUD.Muted;
                string why = CustodyContracts.NoOfferBecause(game, map);
                if (why != null && why != "RP_CustodyNoOfferContract" && why != "RP_CustodyNoOfferOpen") list.Label("RP_CustodyNoOfferNow".Translate(why.Translate()));
                else list.Label(b.nextOfferDay >= 0 ? "RP_CustodyNextOffer".Translate(b.nextOfferDay) : "RP_CustodyNextOfferNone".Translate());
                GUI.color = Color.white;
            }
            if (b.last is { } last)
            {
                GUI.color = PrisonHUD.Muted;
                list.Label("RP_CustodyLastLine".Translate(last.Count(CustodyState.Met), last.members.Count(m => m.state != CustodyState.Dropped), Money.Format(last.paidAdmission + last.paidBonus)));
                GUI.color = Color.white;
            }
        }
    }
}
