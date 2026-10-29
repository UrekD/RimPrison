using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Intel, like Prison Architect's informants screen: what the warden knows about trouble brewing. The tense inmates
    /// are listed with their tension and its cause for everyone (the Inmates list and the prison file show both too);
    /// the rest is only what is known: gangs, suspects and pending searches, informants, feuds, and internal affairs.
    /// </summary>
    public static class IntelTab
    {
        /// <summary>How tall the columns were last time they were drawn: the scroll view is that tall (the first frame draws one too short, the second is right).</summary>
        private static float contentHeight;

        /// <summary>The width of the "End it" button of a gang's deal.</summary>
        private const float DealEndW = 70f;

        /// <summary>Where the riots' list began the last time the screen was drawn (for the UI tour's scroll to it).</summary>
        private static float riotsTop = -1f;

        /// <summary>For the UI tour: the next time the screen is drawn after the riots' list has been placed, it is scrolled to it (and the request is let go).</summary>
        internal static bool TourScrollToRiots;

        /// <summary>Where the watch list began the last time the screen was drawn, and the UI tour's request to scroll to it.</summary>
        private static float watchTop = -1f;

        internal static bool TourScrollToWatch;

        /// <summary>The columns in a scroll view: with trials, complaints, debts and rings listed the left one runs off the window.</summary>
        public static void Draw(Rect rect, Map map, ref Vector2 scroll)
        {
            if (TourScrollToRiots && riotsTop > 0f)
            {
                TourScrollToRiots = false;
                scroll.y = Mathf.Max(0f, riotsTop - 8f);
            }
            if (TourScrollToWatch && watchTop > 0f)
            {
                TourScrollToWatch = false;
                scroll.y = Mathf.Max(0f, watchTop - 8f);
            }
            var view = new Rect(0f, 0f, rect.width - 16f, Mathf.Max(rect.height, contentHeight));
            Widgets.BeginScrollView(rect, ref scroll, view);
            contentHeight = DrawColumns(new Rect(0f, 0f, view.width, view.height), map) + 12f;
            Widgets.EndScrollView();
        }

        /// <summary>"End it": ending a deal costs the leader's trust and starts the five days, so it asks first.</summary>
        internal static void ConfirmEndDeal(StoryArc arc, Map map) =>
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RP_IntelGangDealEndConfirm".Translate(arc.deal.leaderName, arc.deal.gang.label, GangDeals.CooldownDays), () => GangDeals.End(arc, GangDealEnd.Warden, map), destructive: true));

        /// <summary>Draws both columns inside the scroll view and returns how far down the longer one reaches.</summary>
        private static float DrawColumns(Rect rect, Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate()?.security != null).ToList();
            var left = new Rect(rect.x, rect.y, rect.width / 2f - 10f, rect.height);
            var right = new Rect(rect.x + rect.width / 2f + 10f, rect.y, rect.width / 2f - 10f, rect.height);

            float y = left.y;
            Header(left, ref y, "RP_IntelTense");
            var tense = inmates.Where(p => p.Inmate().tension >= 50f).OrderByDescending(p => p.Inmate().tension).Take(10).ToList();
            if (tense.Count == 0) Muted(left, ref y, "RP_IntelHint_Tense");
            foreach (Pawn p in tense)
            {
                CompInmate c = p.Inmate();
                var row = new Rect(left.x, y, left.width, 22f);
                TooltipHandler.TipRegion(row, "RP_IntelTip_Tense".Translate(p.LabelShortCap, c.tension.ToString("F0"), TensionUtility.CauseText(c)));
                Widgets.Label(new Rect(row.x, row.y, 120f, 22f), p.LabelShortCap.Truncate(118f));
                var bar = new Rect(row.x + 124f, row.y + 4f, 80f, 14f);
                Widgets.DrawBoxSolid(bar, new Color(0.12f, 0.12f, 0.14f));
                Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * c.tension / 100f, bar.height), Color.Lerp(PrisonHUD.Warn, PrisonHUD.Bad, (c.tension - 50f) / 50f));
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(bar.xMax + 6f, row.y, row.width - 210f, 22f), TensionUtility.CauseText(c).ToString().Truncate(row.width - 212f));
                GUI.color = Color.white;
                Click(row, p);
                y += 22f;
            }
            y += 8f;
            Header(left, ref y, "RP_IntelRumours");
            List<Rumour> rumours = Rumours.Shown(PrisonMap.Of(map), Find.TickManager.TicksGame);
            if (rumours.Count == 0) Muted(left, ref y, "RP_IntelHint_Rumours");
            // (The warden's readiness and the painted wings once a frame, not once a row: the canteens are a flood fill.)
            Pawn rumourWarden = null;
            string addressWhy = null;
            List<PrisonZoneDef> wings = null;
            if (rumours.Any(x => x.Live))
            {
                rumourWarden = WardenSystem.InPost(map);
                addressWhy = Rumours.AddressUnready(rumourWarden);
                wings = WardenSystem.Wings(map);
            }
            foreach (Rumour r in rumours) DrawRumour(left, ref y, r, map, rumourWarden, addressWhy, wings);
            y += 8f;
            Header(left, ref y, "RP_IntelGangs");
            var gangs = inmates.Where(p => p.Inmate().gang != null && p.Inmate().gangKnown).GroupBy(p => p.Inmate().gang).ToList();
            if (gangs.Count == 0) Muted(left, ref y, "RP_IntelNoGangs");
            foreach (IGrouping<GangDef, Pawn> g in gangs)
            {
                Pawn leader = g.FirstOrDefault(p => p.Inmate().gangLeader);
                bool rivals = gangs.Any(o => GangUtility.AreRivals(g.Key, o.Key));
                Widgets.DrawBoxSolid(new Rect(left.x, y + 4f, 10f, 14f), g.Key.color);
                var gangRow = new Rect(left.x, y, left.width, 22f);
                Widgets.Label(new Rect(left.x + 16f, y, left.width - 16f, 22f),
                    "RP_IntelGangLine".Translate(g.Key.LabelCap, g.Count(), leader?.LabelShort ?? "?").ToString() + (rivals ? "  " + "RP_IntelRivalsHere".Translate().ToString() : ""));
                TooltipHandler.TipRegion(gangRow, "RP_IntelTip_Gang".Translate(g.Key.LabelCap, g.Count(), leader?.LabelShort ?? "?").Resolve() + (rivals ? "\n\n" + "RP_IntelTip_GangRivals".Translate().Resolve() : ""));
                y += 22f;
                // The places they hold (the gang is known, or it would not be listed).
                if (GangTurf.HeldLine(map, g.Key) is { } holds)
                {
                    Concepts.Teach(Concepts.Turf);
                    GUI.color = PrisonHUD.Muted;
                    Widgets.Label(new Rect(left.x + 16f, y, left.width - 16f, 22f), holds.Truncate(left.width - 18f));
                    GUI.color = Color.white;
                    y += 22f;
                }
                // The warden's deal with the gang, if there is one: its term, the days left and the breaches, and the way out of it.
                if (GangDeals.Of(g.Key) is { } dealArc)
                {
                    var dealRow = new Rect(left.x + 16f, y, left.width - 16f, 22f);
                    var dealText = new Rect(dealRow.x, dealRow.y, dealRow.width - DealEndW - 4f, 22f);
                    GUI.color = PrisonHUD.Warn;
                    Widgets.Label(dealText, GangDeals.IntelLine(map, dealArc, full: false).Truncate(dealText.width - 2f));
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(dealText, GangDeals.IntelLine(map, dealArc, full: true) + "\n\n" + "RP_IntelTip_GangDeal".Translate(g.Key.LabelCap).Resolve());
                    var endButton = new Rect(dealRow.xMax - DealEndW, dealRow.y + 1f, DealEndW, 20f);
                    TooltipHandler.TipRegion(endButton, "RP_IntelTip_GangDealEnd".Translate(GangDeals.CooldownDays).Resolve());
                    if (Widgets.ButtonText(endButton, "RP_IntelGangDealEnd".Translate())) ConfirmEndDeal(dealArc, map);
                    y += 22f;
                }
            }
            // Truces between rival gangs, with the days they have left.
            int now = Find.TickManager.TicksGame;
            foreach (GangTruce t in game.story.truces.Where(t => t.untilTick > now))
            {
                GUI.color = PrisonHUD.Good;
                Widgets.Label(new Rect(left.x + 16f, y, left.width - 16f, 22f),
                    "RP_IntelTruceLine".Translate(t.a.LabelCap, t.b.LabelCap, ((t.untilTick - now) / (float)GenDate.TicksPerDay).ToString("0.0")));
                GUI.color = Color.white;
                y += 22f;
            }
            y += 8f;
            Header(left, ref y, "RP_IntelFeuds");
            var feuds = game.story.arcs.Where(a => !a.done && a.def == RPRelationDefOf.RP_Arc_Feud).ToList();
            if (feuds.Count == 0) Muted(left, ref y, "RP_IntelHint_Feuds");
            foreach (StoryArc a in feuds)
            {
                var feudRow = new Rect(left.x, y, left.width, 22f);
                var feudText = new Rect(feudRow.x, feudRow.y, feudRow.width - ApartButtonW - 4f, 22f);
                Widgets.Label(feudText, "RP_IntelFeudLine".Translate(a.A?.LabelShort ?? "?", a.B?.LabelShort ?? "?", a.note ?? "").ToString().Truncate(feudText.width - 2f));
                TooltipHandler.TipRegion(feudText, "RP_IntelTip_Feud".Translate(a.A?.LabelShort ?? "?", a.B?.LabelShort ?? "?"));
                if (a.A != null) Click(feudText, a.A);
                KeepApartUI.PairButton(new Rect(feudRow.xMax - ApartButtonW, feudRow.y + 1f, ApartButtonW, 20f), a.A, a.B, "feud");
                y += 22f;
            }
            // Debts the warden has heard of (a tip, a search, a beating); the rest stay hidden.
            y += 8f;
            Header(left, ref y, "RP_IntelDebts");
            List<InmateDebt> debts = DebtSystem.Known();
            if (debts.Count == 0) Muted(left, ref y, "RP_IntelHint_Debts");
            foreach (InmateDebt d in debts.Take(8))
            {
                Pawn debtor = inmates.Find(p => p.thingIDNumber == d.debtorId);
                var row = new Rect(left.x, y, left.width, 22f);
                Widgets.Label(new Rect(row.x, row.y, row.width - 90f - ApartButtonW - 4f, 22f), "RP_IntelDebtLine".Translate(d.debtorName, d.creditorName, Money.Format(d.amount), DebtSystem.ReasonText(d)).ToString().Truncate(row.width - 92f - ApartButtonW - 4f));
                KeepApartUI.PairButton(new Rect(row.xMax - 90f - ApartButtonW, row.y + 1f, ApartButtonW, 20f), debtor, inmates.Find(p => p.thingIDNumber == d.creditorId), "debt");
                GUI.color = d.Overdue ? PrisonHUD.Bad : PrisonHUD.Muted;
                Widgets.Label(new Rect(row.xMax - 86f, row.y, 86f, 22f), d.Overdue ? "RP_IntelDebtOverdue".Translate() : "RP_IntelDebtDue".Translate(((d.dueTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerDay).ToString("0.0")));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(row, "RP_IntelTip_Debt".Translate(d.debtorName, d.creditorName, Money.Format(d.amount), DebtSystem.ReasonText(d),
                    d.Overdue ? "RP_IntelDebtOverdue".Translate() : "RP_IntelDebtDue".Translate(((d.dueTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerDay).ToString("0.0"))));
                if (debtor != null) Click(row, debtor);
                y += 22f;
            }
            // The watch list: each inmate flagged at risk, the warden's answer (or the letter still open), the time left, and the way to end it.
            y += 8f;
            watchTop = y;
            Header(left, ref y, "RP_IntelWatch");
            List<Pawn> listed = WatchList.Listed(map);
            if (listed.Count == 0) Muted(left, ref y, "RP_IntelHint_Watch");
            foreach (Pawn p in listed) DrawWatchRow(left, ref y, p);
            // Appeals with the courts: backed by the warden or fought alone, and when the answer comes.
            var appeals = inmates.Where(p => p.Inmate().appealResolveTick > now).ToList();
            if (appeals.Count > 0)
            {
                y += 8f;
                Header(left, ref y, "RP_IntelAppeals");
                foreach (Pawn p in appeals)
                {
                    CompInmate c = p.Inmate();
                    var row = new Rect(left.x, y, left.width, 22f);
                    Widgets.Label(new Rect(row.x, row.y, 120f, 22f), p.LabelShortCap.Truncate(118f));
                    GUI.color = c.appealBacked ? PrisonHUD.Good : PrisonHUD.Muted;
                    Widgets.Label(new Rect(row.x + 124f, row.y, row.width - 124f, 22f),
                        (c.appealBacked ? "RP_IntelAppealBacked" : "RP_IntelAppealAlone").Translate(((c.appealResolveTick - now) / (float)GenDate.TicksPerDay).ToString("0.0")));
                    GUI.color = Color.white;
                    Click(row, p);
                    y += 22f;
                }
            }

            // Murder cases on their way to court: what the warden knows of each (the case as it looks, the witnesses who came forward, who is out of reach).
            y += 8f;
            Header(left, ref y, "RP_IntelTrials");
            if (game.story.trials.Count == 0) Muted(left, ref y, "RP_IntelHint_Trials");
            foreach (MurderTrial t in game.story.trials.Take(6))
            {
                Pawn killer = inmates.Find(p => p.thingIDNumber == t.killerId);
                var row = new Rect(left.x, y, left.width, 22f);
                // (The band is worked out from the witnesses on the map: kept for a second, not asked on every event of every frame.)
                string band = t.assembled ? Trials.KnownBandCached(t, map) : null;
                string line = Trials.Key(t, "RP_IntelTrialLine2").Translate(t.killerName, t.victimName).ToString() + "  " + (band != null ? "RP_IntelTrialCase".Translate(band) : "RP_IntelTrialCaseUnknown".Translate());
                float nextW = 96f;
                Widgets.Label(new Rect(row.x, row.y, row.width - nextW, 22f), line.Truncate(row.width - nextW - 2f));
                NextColumn(row, nextW, "RP_IntelNextCourt".Translate(Trials.DateText(t)));
                TooltipHandler.TipRegion(row, Trials.Key(t, "RP_IntelTip_Trial").Translate(t.killerName, t.victimName, Trials.DateText(t)) + "\n\n" + (band != null ? "RP_IntelTip_TrialBands".Translate(band) : "RP_IntelTip_TrialUnknown".Translate()));
                if (killer != null) Click(row, killer);
                y += 22f;
                var names = t.witnesses.Where(w => !w.staff && w.known).Select(w => w.name + (w.pressureKnown ? " " + "RP_TrialThreatenedMark".Translate() : "") + (inmates.Find(p => p.thingIDNumber == w.id)?.Inmate() is { } wc && Trials.IsProtected(wc) ? " " + "RP_IntelTrialProtected".Translate() : "")).ToList();
                if (names.Count > 0)
                {
                    var witnessRow = new Rect(left.x, y, left.width, 22f);
                    GUI.color = PrisonHUD.Muted;
                    Widgets.Label(new Rect(witnessRow.x, witnessRow.y, witnessRow.width - ApartButtonW - 4f, 22f), "RP_IntelTrialWitnesses".Translate(string.Join(", ", names)).ToString().Truncate(witnessRow.width - ApartButtonW - 6f));
                    GUI.color = Color.white;
                    KeepApartUI.WitnessButton(new Rect(witnessRow.xMax - ApartButtonW, witnessRow.y + 1f, ApartButtonW, 20f), killer, t.witnesses, inmates);
                    y += 22f;
                }
            }
            // Complaints against officers that are waiting for the warden or under investigation (what is decided is on the inmate's file).
            y += 8f;
            Header(left, ref y, "RP_IntelComplaints");
            var openComplaints = game.story.complaints.Where(c => c.stage == ComplaintStage.Waiting || c.stage == ComplaintStage.Investigating).Take(6).ToList();
            if (openComplaints.Count == 0) Muted(left, ref y, "RP_IntelHint_Complaints");
            foreach (Complaint cm in openComplaints)
            {
                Pawn complainant = inmates.Find(p => p.thingIDNumber == cm.inmateId);
                var row = new Rect(left.x, y, left.width, 22f);
                string state = cm.stage == ComplaintStage.Waiting ? "RP_IntelComplaintWaiting".Translate().ToString() : "RP_IntelComplaintInvestigating".Translate(Mathf.Max(0, (cm.dueTick - Find.TickManager.TicksGame) / GenDate.TicksPerHour)).ToString();
                // A complaint waiting for the warden still has its letter: reopen it from here (a letter closed with "postpone" is easy to lose among the others).
                ChoiceLetter_PrisonDecision letter = cm.stage == ComplaintStage.Waiting ? OpenDecision("RP_Decision_Complaint", d => d.amount == cm.id) : null;
                // (Or it is held back for tomorrow: over the day's budget of decisions, see StoryLetters.)
                bool held = cm.stage == ComplaintStage.Waiting && letter == null && HeldDecision("RP_Decision_Complaint", d => d.amount == cm.id);
                float buttonW = 66f, nextW = 104f;
                Widgets.Label(new Rect(row.x, row.y, row.width - buttonW - nextW, 22f), "RP_IntelComplaintLine2".Translate(cm.inmateName, cm.officerName).ToString().Truncate(row.width - buttonW - nextW - 2f));
                NextColumn(new Rect(row.x, row.y, row.width - buttonW, 22f), nextW, cm.stage == ComplaintStage.Waiting
                    ? (held ? "RP_IntelNextTomorrow".Translate() : letter != null && letter.TimeoutActive ? "RP_IntelNextAnswer".Translate(HoursLeft(letter.disappearAtTick)) : "RP_IntelComplaintWaiting".Translate())
                    : "RP_IntelNextReport".Translate(Mathf.Max(0, (cm.dueTick - Find.TickManager.TicksGame) / GenDate.TicksPerHour)));
                TooltipHandler.TipRegion(new Rect(row.x, row.y, row.width - buttonW, 22f), "RP_IntelTip_Complaint".Translate(cm.inmateName, cm.officerName, state));
                var reopen = new Rect(row.xMax - buttonW + 2f, row.y + 1f, buttonW - 2f, 20f);
                if (cm.stage == ComplaintStage.Waiting)
                {
                    TooltipHandler.TipRegion(reopen, (letter != null ? "RP_IntelReopenTip" : held ? "RP_IntelReopenHeldTip" : "RP_IntelReopenGoneTip").Translate());
                    if (Widgets.ButtonText(reopen, "RP_IntelReopen".Translate(), active: letter != null)) letter.OpenLetter();
                }
                if (complainant != null) Click(new Rect(row.x, row.y, row.width - buttonW, 22f), complainant);
                y += 22f;
            }
            // The last riots: when, which wing, who (only who was seen), and what was answered (the morning letter's report in the tooltip, and a way back into a letter still open).
            y += 8f;
            riotsTop = y;
            Header(left, ref y, "RP_IntelRiots");
            if (game.riots.records.Count == 0) Muted(left, ref y, "RP_IntelHint_Riots");
            for (int i = game.riots.records.Count - 1; i >= 0; i--) DrawRiotRow(left, ref y, game.riots.records[i], map, inmates);
            float leftEnd = y;

            y = right.y;
            // A contraband amnesty: declare one, or how it is going, or when the next can be.
            var amnestyRow = new Rect(right.x, y, right.width, 28f);
            if (Amnesty.Active(game))
            {
                GUI.color = PrisonHUD.Good;
                Widgets.Label(amnestyRow, "RP_AmnestyRunning".Translate(((game.amnestyUntilTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour).ToString("0"), game.amnestyHandedIn));
                GUI.color = Color.white;
            }
            else if (Amnesty.DaysUntilReady(game) > 0f)
                Widgets.ButtonText(amnestyRow.LeftPart(0.7f), "RP_AmnestyCooldown".Translate(Amnesty.DaysUntilReady(game).ToString("0.0")), active: false);
            else if (Widgets.ButtonText(amnestyRow.LeftPart(0.7f), "RP_AmnestyDeclare".Translate(Amnesty.Hours))) Amnesty.Declare(game);
            TooltipHandler.TipRegion(amnestyRow, "RP_AmnestyDesc".Translate(Amnesty.Hours, Amnesty.CooldownDays));
            y += 34f;
            // Who asks to see the warden, why and for how long yet, and the warden sent to them (the word, or the walk of their wing for a rumour).
            Header(right, ref y, "RP_IntelAsks");
            List<(WardenAsk ask, Pawn inmate)> asks = WardenAsks.Live(map);
            if (asks.Count == 0) Muted(right, ref y, "RP_IntelHint_Asks");
            else
            {
                Pawn askWarden = WardenSystem.InPost(map);
                List<PrisonZoneDef> askWings = asks.Any(x => x.ask.reason == WardenAskReason.Rumour) ? WardenSystem.Wings(map) : null;
                foreach ((WardenAsk ask, Pawn p) in asks) DrawAsk(right, ref y, ask, p, map, askWarden, askWings, now);
            }
            y += 8f;
            Header(right, ref y, "RP_IntelSuspects");
            var suspects = inmates.Where(p => p.Inmate() is { } c && (c.suspected || c.cellSearchPending || c.tunnelKnown)).ToList();
            if (suspects.Count == 0) Muted(right, ref y, "RP_IntelHint_Suspects");
            foreach (Pawn p in suspects)
            {
                CompInmate c = p.Inmate();
                var parts = new List<string>();
                if (c.suspected) parts.Add("RP_IntelSuspected".Translate());
                if (c.cellSearchPending) parts.Add("RP_IntelCellSearch".Translate());
                if (c.tunnelKnown) parts.Add("RP_IntelTunnel".Translate());
                var row = new Rect(right.x, y, right.width, 22f);
                Widgets.Label(new Rect(row.x, row.y, 120f, 22f), p.LabelShortCap.Truncate(118f));
                GUI.color = PrisonHUD.Warn;
                Widgets.Label(new Rect(row.x + 124f, row.y, row.width - 124f, 22f), string.Join(", ", parts));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(row, "RP_IntelTip_Suspect".Translate(p.LabelShortCap, string.Join(", ", parts)));
                Click(row, p);
                y += 22f;
            }
            y += 8f;
            Header(right, ref y, "RP_IntelInformants");
            var snitches = inmates.Where(p => p.Inmate().KnownReputation(ReputationDefOf.RP_Snitch)).ToList();
            if (snitches.Count == 0) Muted(right, ref y, "RP_IntelNoInformants");
            foreach (Pawn p in snitches)
            {
                CompInmate c = p.Inmate();
                var row = new Rect(right.x, y, right.width, 22f);
                Widgets.Label(new Rect(row.x, row.y, 120f, 22f), p.LabelShortCap.Truncate(118f));
                GUI.color = c.snitchExposed ? PrisonHUD.Bad : PrisonHUD.Good;
                string line = (c.snitchExposed ? "RP_IntelExposed" : "RP_IntelActive").Translate() + "  " + "RP_IntelFear".Translate(c.fear.ToString("F0"));
                if (c.informantPaid) line += "  " + "RP_IntelPaid".Translate(Money.Format(InformantDecision.DailyPay));
                else if (c.informantQuit) line += "  " + "RP_IntelQuit".Translate();
                Widgets.Label(new Rect(row.x + 124f, row.y, row.width - 124f - (c.informantPaid ? 96f : 0f), 22f), line);
                GUI.color = Color.white;
                // Paid informants can be taken off the payroll (no more pay; the arrangement ends quietly).
                var stopRect = new Rect(row.xMax - 92f, row.y + 1f, 92f, 20f);
                if (c.informantPaid) TooltipHandler.TipRegion(stopRect, "RP_IntelStopPayTip".Translate(p.LabelShort));
                if (c.informantPaid && Widgets.ButtonText(stopRect, "RP_IntelStopPay".Translate()))
                {
                    Pawn informant = p;
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RP_IntelStopPayConfirm".Translate(p.LabelShort), () => StopPaying(informant), destructive: true));
                }
                else Click(new Rect(row.x, row.y, row.width - (c.informantPaid ? 96f : 0f), row.height), p);
                TooltipHandler.TipRegion(new Rect(row.x, row.y, row.width - (c.informantPaid ? 96f : 0f), row.height), (c.snitchExposed ? "RP_IntelTip_InformantExposed" : "RP_IntelTip_Informant").Translate(p.LabelShortCap));
                y += 22f;
                if (InformantTask.Tasked(c)) DrawTaskRow(right, ref y, p, c, now);
            }
            // The keep-apart orders: who, since when and why, and what stands in the way of each today (no bed to part them, both in one yard patch, both on a job in one room).
            y += 8f;
            Header(right, ref y, "RP_IntelApart");
            game.keepApart.EnsureResolved();
            if (game.keepApart.orders.Count == 0) Muted(right, ref y, "RP_IntelHint_Apart");
            KeepApartOrder lifted = null;
            foreach (KeepApartOrder order in game.keepApart.orders)
            {
                var row = new Rect(right.x, y, right.width, 22f);
                var text = new Rect(row.x, row.y, row.width - ApartButtonW - 4f, 22f);
                Widgets.Label(text, KeepApart.OrderLine(order).Truncate(text.width - 2f));
                TooltipHandler.TipRegion(text, "RP_IntelTip_Apart".Translate(order.nameA, order.nameB));
                if (order.a is { Spawned: true }) Click(text, order.a);
                if (Widgets.ButtonText(new Rect(row.xMax - ApartButtonW, row.y + 1f, ApartButtonW, 20f), "RP_KeepApartLiftShort".Translate())) lifted = order;
                y += 22f;
                foreach (string trouble in KeepApart.TroubleLines(order)) MutedWarn(right, ref y, trouble);
            }
            if (lifted != null) KeepApart.Lift(lifted);
            // Smuggling rings the warden has found a link of: the chain as far as it is known, a marker while more links are suspected, and what to do about it.
            y += 8f;
            Header(right, ref y, "RP_IntelRings");
            List<StoryArc> rings = RingSystem.VisibleRings();
            if (rings.Count == 0) Muted(right, ref y, "RP_IntelNoRings");
            foreach (StoryArc ring in rings) DrawRing(right, ref y, ring, map);
            // The tools of the workshop and the kitchen as the staff last counted them back in (never what is true), and the way to replace the ones counted short.
            y += 8f;
            Header(right, ref y, "RP_IntelTools");
            if (game.tools.pools.Count == 0) Muted(right, ref y, "RP_IntelHint_Tools");
            foreach (ToolPool pool in game.tools.pools.OrderBy(t => t.zone.defName).ToList()) DrawToolRow(right, ref y, pool);
            y += 8f;
            Header(right, ref y, "RP_IntelStaff");
            var staff = StaffCommute.AllStaff(map).Where(s => s.GetComp<CompStaff>() is { } sc && (sc.suspicion >= 1f || sc.iaOpen || sc.doubleAgent || sc.exposedCorrupt || CorruptionSystem.IaSuspended(sc))).ToList();
            if (staff.Count == 0) Muted(right, ref y, "RP_IntelHint_Staff");
            foreach (Pawn s in staff)
            {
                CompStaff sc = s.GetComp<CompStaff>();
                string status = sc.doubleAgent ? "RP_StaffDoubleAgent".Translate().ToString()
                    : CorruptionSystem.IaSuspended(sc) ? "RP_StaffStatus_Suspended".Translate().ToString()
                    : sc.iaOpen ? "RP_StaffStatus_IA".Translate().ToString()
                    : sc.exposedCorrupt ? "RP_StaffStatus_Corrupt".Translate().ToString()
                    : "RP_StaffSuspicion".Translate(sc.suspicion.ToString("F0")).ToString();
                var row = new Rect(right.x, y, right.width, 22f);
                Widgets.Label(new Rect(row.x, row.y, 120f, 22f), s.LabelShortCap.Truncate(118f));
                GUI.color = new Color(0.7f, 0.5f, 0.85f);
                Widgets.Label(new Rect(row.x + 124f, row.y, row.width - 124f, 22f), status.Truncate(row.width - 126f));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(row, "RP_IntelTip_Staff".Translate(s.LabelShortCap, status));
                if (s.Spawned) Click(row, s);
                y += 22f;
            }
            return Mathf.Max(leftEnd, y);
        }

        /// <summary>One room's tool row: the staff's last count of it, and the button that pays for the ones it was short.</summary>
        private static void DrawToolRow(Rect col, ref float y, ToolPool pool)
        {
            var row = new Rect(col.x, y, col.width, 22f);
            var text = new Rect(row.x, row.y, row.width - ToolButtonW - 4f, 22f);
            GUI.color = pool.knownMissing > 0 ? PrisonHUD.Warn : Color.white;
            Widgets.Label(text, ToolCount.IntelLine(pool).Truncate(text.width - 2f));
            GUI.color = Color.white;
            TooltipHandler.TipRegion(text, "RP_IntelTip_Tools".Translate().Resolve() + (ToolCount.FoundLine(pool) is { Length: > 0 } found ? "\n\n" + found : ""));
            var button = new Rect(row.xMax - ToolButtonW, row.y + 1f, ToolButtonW, 20f);
            if (pool.knownMissing > 0)
            {
                int cost = ToolCount.ReplaceTotal(pool);
                TooltipHandler.TipRegion(button, "RP_IntelToolsReplaceTip".Translate(pool.knownMissing, Money.Format(cost)));
                if (Widgets.ButtonText(button, "RP_IntelToolsReplace".Translate(Money.Format(cost)), active: PrisonGame.Instance.ledger.CanAfford(cost))) ToolCount.Replace(pool);
            }
            y += 22f;
        }

        /// <summary>An informant's task under their row: whom they watch, the days in place, the exposure band, and the way out.</summary>
        private static void DrawTaskRow(Rect col, ref float y, Pawn p, CompInmate c, int now)
        {
            var row = new Rect(col.x + 16f, y, col.width - 16f, 22f);
            var text = new Rect(row.x, row.y, row.width - ApartButtonW - 4f, 22f);
            float term = InformantTask.Term(p, c, shown: true);
            GUI.color = InformantTask.BandColor(term);
            Widgets.Label(text, InformantTask.Line(p, c, now).Truncate(text.width - 2f));
            GUI.color = Color.white;
            TooltipHandler.TipRegion(text, "RP_IntelTip_InformantTask".Translate(p.LabelShort, (InformantTask.PerDay * 100f).ToString("0"), (InformantTask.GangPerDay * 100f).ToString("0"), (InformantTask.ActedOnBase * 100f).ToString("0")));
            if (InformantTask.TargetOf(p, c) is { } target) Click(text, target);
            var pull = new Rect(row.xMax - ApartButtonW, row.y + 1f, ApartButtonW, 20f);
            TooltipHandler.TipRegion(pull, "RP_IntelPullOutTip".Translate(p.LabelShort));
            if (Widgets.ButtonText(pull, "RP_IntelPullOut".Translate())) InformantTask.PullOut(p, c);
            y += 22f;
        }

        /// <summary>One riot: the line and what was answered (or where it stands), its report in the tooltip, and the letter reopened when it is still waiting.</summary>
        private static void DrawRiotRow(Rect col, ref float y, RiotRecord r, Map map, List<Pawn> inmates)
        {
            var row = new Rect(col.x, y, col.width, 22f);
            float buttonW = 66f, stateW = Mathf.Min(220f, col.width * 0.45f);
            ChoiceLetter_PrisonDecision letter = OpenDecision(RiotReview.MoreDecisionDef, d => d.amount == r.id) ?? OpenDecision(RiotReview.DecisionDef, d => d.amount == r.id);
            var text = new Rect(row.x, row.y, row.width - buttonW - stateW, 22f);
            Widgets.Label(text, RiotReview.ListLine(r, map).Truncate(text.width - 2f));
            NextColumn(new Rect(row.x, row.y, row.width - buttonW, 22f), stateW, RiotReview.StateText(r));
            var body = new Rect(row.x, row.y, row.width - buttonW, 22f);
            // (The report is worked out when the tooltip shows, not on every frame.)
            TooltipHandler.TipRegion(body, () => "RP_IntelTip_Riot".Translate(RiotReview.Label(r).Resolve() + "\n" + RiotReview.ReportText(r), RiotReview.AnswersText(r)).Resolve(), 771300 + r.id);
            var reopen = new Rect(row.xMax - buttonW + 2f, row.y + 1f, buttonW - 2f, 20f);
            if (letter != null)
            {
                TooltipHandler.TipRegion(reopen, "RP_IntelReopenTip".Translate());
                if (Widgets.ButtonText(reopen, "RP_IntelReopen".Translate())) letter.OpenLetter();
            }
            // (Only the ringleader the staff saw is a pawn to jump to: the list names nobody else.)
            if (r.RingleaderSeen && inmates.Find(p => p.thingIDNumber == r.ringleaderId) is { } lead) Click(body, lead);
            y += 22f;
        }

        /// <summary>The width of a rumour row's buttons.</summary>
        private const float RumourButtonW = 58f;

        /// <summary>One rumour: the wing, what it says, true or false once known, its strength (or how it ended); under a live one who started it (once known) and the warden's answers - the address in the wing's canteen, calm or firm, and the walk of the wing.</summary>
        private static void DrawRumour(Rect col, ref float y, Rumour r, Map map, Pawn warden, string addressWhy, List<PrisonZoneDef> wings)
        {
            int now = Find.TickManager.TicksGame;
            float strength = Rumours.Strength(r, now);
            var row = new Rect(col.x, y, col.width, 22f);
            GUI.color = r.Live ? Rumours.BandColor(strength) : PrisonHUD.Muted;
            Widgets.Label(row, Rumours.IntelLine(r, now).Truncate(row.width - 2f));
            GUI.color = Color.white;
            TooltipHandler.TipRegion(row, "RP_IntelTip_Rumour".Translate(Rumours.WingName(r.level), Rumours.Text(r), Rumours.TruthKey(r).Translate(), (strength * 100f).ToString("0"),
                Rumours.LifeDays.ToString("0"), Rumours.FirmDays));
            y += 22f;
            if (!r.Live) return;
            var sub = new Rect(col.x + 16f, y, col.width - 16f, 22f);
            float buttons = 3f * (RumourButtonW + 4f);
            var text = new Rect(sub.x, sub.y, sub.width - buttons, 22f);
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(text, (r.originKnown ? "RP_Rumour_StartedBy".Translate(r.originName ?? "?") : "RP_Rumour_StartedByUnknown".Translate()).ToString().Truncate(text.width - 2f));
            GUI.color = Color.white;
            if (r.originKnown && CourtUtil.FindInmate(map, r.originId) is { Spawned: true } origin) Click(text, origin);
            string walkWhy = Rumours.WalkUnready(warden, r.level, wings);
            var calm = new Rect(sub.xMax - buttons, sub.y + 1f, RumourButtonW, 20f);
            var firm = new Rect(calm.xMax + 4f, calm.y, RumourButtonW, 20f);
            var walk = new Rect(firm.xMax + 4f, calm.y, RumourButtonW, 20f);
            TooltipHandler.TipRegion(calm, addressWhy ?? "RP_IntelRumourCalmTip".Translate(Rumours.WingName(r.level)).Resolve());
            TooltipHandler.TipRegion(firm, addressWhy ?? "RP_IntelRumourFirmTip".Translate(Rumours.WingName(r.level), Rumours.FirmDays).Resolve());
            TooltipHandler.TipRegion(walk, walkWhy ?? "RP_IntelRumourWalkTip".Translate(Rumours.WingName(r.level)).Resolve());
            if (Widgets.ButtonText(calm, "RP_IntelRumourCalm".Translate(), active: addressWhy == null) && addressWhy == null) Rumours.OrderAddress(warden, r.level, firm: false);
            if (Widgets.ButtonText(firm, "RP_IntelRumourFirm".Translate(), active: addressWhy == null) && addressWhy == null) Rumours.OrderAddress(warden, r.level, firm: true);
            if (Widgets.ButtonText(walk, "RP_IntelRumourWalk".Translate(), active: walkWhy == null) && walkWhy == null) WardenOrders.Walk(warden, Rumours.WingZone(map, r.level, wings));
            y += 22f;
        }

        /// <summary>The width of a request row's "Go" and of its hours.</summary>
        private const float AskGoW = 50f, AskHoursW = 44f;

        /// <summary>One request to see the warden: who, why, the hours it has left, and "Go" (the word with them, or the walk of their wing for a rumour; off with the reason when the warden cannot).</summary>
        private static void DrawAsk(Rect col, ref float y, WardenAsk a, Pawn p, Map map, Pawn warden, List<PrisonZoneDef> wings, int now)
        {
            var row = new Rect(col.x, y, col.width, 22f);
            string reason = WardenAsks.ReasonText(map, p, p.Inmate(), a);
            var body = new Rect(row.x, row.y, row.width - AskGoW - AskHoursW - 4f, 22f);
            Widgets.Label(new Rect(body.x, body.y, 120f, 22f), p.LabelShortCap.Truncate(118f));
            GUI.color = PrisonHUD.Warn;
            Widgets.Label(new Rect(body.x + 124f, body.y, body.width - 124f, 22f), reason.Truncate(body.width - 126f));
            GUI.color = Color.white;
            NextColumn(new Rect(row.x, row.y, row.width - AskGoW - 4f, 22f), AskHoursW, "RP_IntelAskHours".Translate(WardenAsks.HoursLeft(a, now)));
            TooltipHandler.TipRegion(body, "RP_IntelTip_Ask".Translate(p.LabelShortCap, reason, WardenAsks.HoursLeft(a, now)).Resolve());
            Click(body, p);
            var go = new Rect(row.xMax - AskGoW, row.y + 1f, AskGoW, 20f);
            string why = WardenAsks.GoUnready(warden, a, p, wings);
            TooltipHandler.TipRegion(go, why ?? (a.reason == WardenAskReason.Rumour ? "RP_IntelAskGoWalkTip" : "RP_IntelAskGoWordTip").Translate(p.LabelShort).Resolve());
            if (Widgets.ButtonText(go, "RP_IntelAskGo".Translate(), active: why == null) && why == null) WardenAsks.Go(warden, a, p, wings);
            y += 22f;
        }

        /// <summary>What a watch-list row's button is: the letter on the desk to reopen, a letter held back by the day's budget (reopened tomorrow), a measure the warden can end, or none (nothing is done).</summary>
        public enum WatchButton
        {
            None,
            Reopen,
            Held,
            End,
        }

        /// <summary>The button of an inmate's watch-list row (<paramref name="letter"/>: the letter on the desk, for <see cref="WatchButton.Reopen"/>).</summary>
        public static WatchButton WatchRowButton(Pawn p, CompInmate c, out ChoiceLetter_PrisonDecision letter)
        {
            letter = null;
            if (c.watchAnswer == WatchAnswer.Pending)
            {
                bool Match(ChoiceLetter_PrisonDecision d) => d.subjectId == p.thingIDNumber && d.amount == c.riskTick;
                letter = OpenDecision(WatchList.DecisionDef, Match);
                if (letter != null) return WatchButton.Reopen;
                return HeldDecision(WatchList.DecisionDef, Match) ? WatchButton.Held : WatchButton.None;
            }
            return WatchList.CanEndMeasure(c) ? WatchButton.End : WatchButton.None;
        }

        /// <summary>
        /// One inmate on the watch list: the answer (or the letter still open), the days left (or the letter tomorrow), the cause and the mood in the tooltip, and the letter reopened or the measure ended (they stay on the
        /// list, at risk, until the period is over).
        /// </summary>
        private static void DrawWatchRow(Rect col, ref float y, Pawn p)
        {
            CompInmate c = p.Inmate();
            var row = new Rect(col.x, y, col.width, 22f);
            float buttonW = 66f, nextW = 96f;
            WatchButton kind = WatchRowButton(p, c, out ChoiceLetter_PrisonDecision letter);
            var text = new Rect(row.x, row.y, row.width - buttonW - nextW, 22f);
            GUI.color = WatchList.InCrisis(p) ? PrisonHUD.Bad : c.watchAnswer == WatchAnswer.Pending ? PrisonHUD.Warn : Color.white;
            Widgets.Label(text, "RP_IntelWatchLine".Translate(p.LabelShortCap, WatchList.AnswerText(c)).ToString().Truncate(text.width - 2f));
            GUI.color = Color.white;
            var body = new Rect(row.x, row.y, row.width - buttonW, 22f);
            NextColumn(body, nextW, kind == WatchButton.Held ? "RP_IntelNextTomorrow".Translate() : "RP_IntelWatchLeft".Translate(WatchList.DaysLeft(c).ToString("0.0")));
            TooltipHandler.TipRegion(body, "RP_IntelTip_Watch".Translate(p.LabelShortCap, WatchList.CauseText(c.riskCause), WatchList.AnswerText(c), WatchList.Mood(p).ToStringPercent(), c.crisisCount));
            Click(body, p);
            var button = new Rect(row.xMax - buttonW + 2f, row.y + 1f, buttonW - 2f, 20f);
            switch (kind)
            {
                case WatchButton.Reopen:
                case WatchButton.Held:
                    TooltipHandler.TipRegion(button, (kind == WatchButton.Reopen ? "RP_IntelReopenTip" : "RP_IntelReopenHeldTip").Translate());
                    if (Widgets.ButtonText(button, "RP_IntelReopen".Translate(), active: letter != null)) letter?.OpenLetter();
                    break;
                case WatchButton.End:
                    TooltipHandler.TipRegion(button, "RP_IntelWatchEndTip".Translate(p.LabelShort, WatchList.AnswerText(c)));
                    if (Widgets.ButtonText(button, "RP_IntelWatchEnd".Translate())) WatchList.EndByWarden(p);
                    break;
            }
            y += 22f;
        }

        /// <summary>The width of a tool row's replace button.</summary>
        private const float ToolButtonW = 110f;

        /// <summary>The whole hours until a tick (at least 0).</summary>
        private static int HoursLeft(int tick) => Mathf.Max(0, (tick - Find.TickManager.TicksGame) / GenDate.TicksPerHour);

        /// <summary>A decision letter still waiting in the stack (not answered), or null: what an Intel row reopens.</summary>
        public static ChoiceLetter_PrisonDecision OpenDecision(string decisionDef, System.Func<ChoiceLetter_PrisonDecision, bool> match)
        {
            foreach (Letter l in Find.LetterStack.LettersListForReading)
                if (l is ChoiceLetter_PrisonDecision { answered: false } d && d.Is(decisionDef) && match(d)) return d;
            return null;
        }

        /// <summary>Whether the letter is held back for tomorrow (over the day's budget of decisions, see <see cref="StoryLetters"/>): open as far as the systems go, but not on the desk yet.</summary>
        public static bool HeldDecision(string decisionDef, System.Func<ChoiceLetter_PrisonDecision, bool> match) =>
            StoryLetters.Open().Any(l => l is ChoiceLetter_PrisonDecision { answered: false } d && d.Is(decisionDef) && match(d) && !Find.LetterStack.LettersListForReading.Contains(d));

        /// <summary>The "next" column of a row: when the next thing about it happens, right-aligned in the grey of a note.</summary>
        private static void NextColumn(Rect row, float width, string text)
        {
            GUI.color = PrisonHUD.Muted;
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(new Rect(row.xMax - width, row.y, width, 22f), text.Truncate(width - 2f));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        /// <summary>One ring: its known links in a chain (and one marker for the rest), its heat, and the warden's moves (bust, turn).</summary>
        private static void DrawRing(Rect col, ref float y, StoryArc arc, Map map)
        {
            bool active = arc.stage == 0;
            var chain = new Rect(col.x, y, col.width - 84f, 22f);
            var parts = RingSystem.Chain(arc);
            string text = string.Join("  >  ", parts.Select(p => !p.Known ? p.text : p.link.handled ? p.text + " " + "RP_IntelRingHandledMark".Translate().ToString() : p.link.turned ? p.text + " " + "RP_IntelRingTurnedMark".Translate().ToString() : p.text));
            GUI.color = active ? Color.white : PrisonHUD.Muted;
            Widgets.Label(chain, text.Truncate(chain.width - 2f));
            GUI.color = Color.white;
            if (parts.Exists(p => !p.Known)) TooltipHandler.TipRegion(chain, "RP_IntelRingUnknownTip".Translate());
            GUI.color = active ? Color.Lerp(PrisonHUD.Muted, PrisonHUD.Bad, arc.ring.heat / 100f) : PrisonHUD.Muted;
            Text.Anchor = TextAnchor.UpperRight;
            // (The heat against the level at which the case is put to the warden, as a letter.)
            var heat = new Rect(chain.xMax, y, 82f, 22f);
            Widgets.Label(heat, active ? "RP_IntelRingHeatOf".Translate(arc.ring.heat.ToString("F0"), RingSystem.AskHeat.ToString("F0")) : "RP_IntelRingBroken".Translate());
            if (active) TooltipHandler.TipRegion(heat, "RP_IntelTip_RingHeat".Translate(arc.ring.heat.ToString("F0"), RingSystem.AskHeat.ToString("F0"), RingSystem.AskCooldownDays));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (RingSystem.Focus(arc) is { } focus) Click(chain, focus);
            y += 22f;
            if (!active) return;
            var bust = new Rect(col.x + 16f, y, 90f, 22f);
            List<RingLink> bustable = RingSystem.Bustable(arc);
            TooltipHandler.TipRegion(bust, "RP_IntelRingBustTip".Translate());
            if (Widgets.ButtonText(bust, "RP_IntelRingBust".Translate(), active: bustable.Count > 0))
            {
                StoryArc target = arc;
                string names = string.Join(", ", bustable.Select(l => RingSystem.DisplayName(l)));
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation((RingSystem.MoreSuspected(arc) ? "RP_IntelRingBustConfirm" : "RP_IntelRingBustConfirmWhole").Translate(names), () => RingSystem.Bust(target, map), destructive: true));
            }
            var turn = new Rect(bust.xMax + 8f, y, 90f, 22f);
            List<RingLink> turnable = RingSystem.Turnable(arc);
            TooltipHandler.TipRegion(turn, "RP_IntelRingTurnTip".Translate());
            if (Widgets.ButtonText(turn, "RP_IntelRingTurn".Translate(), active: turnable.Count > 0))
            {
                StoryArc target = arc;
                Find.WindowStack.Add(new FloatMenu(turnable.Select(l => new FloatMenuOption(RingSystem.LinkLine(l), () => RingSystem.Turn(target, l, map))).ToList()));
            }
            // The case put to the warden is still in the letters: reopen it from here (it decides itself when it runs out).
            var reopen = new Rect(turn.xMax + 8f, y, 90f, 22f);
            ChoiceLetter_PrisonDecision ask = OpenDecision("RP_Decision_Arc", d => d.arcId == arc.id);
            bool askHeld = ask == null && HeldDecision("RP_Decision_Arc", d => d.arcId == arc.id);
            TooltipHandler.TipRegion(reopen, (ask != null ? "RP_IntelReopenRingTip" : askHeld ? "RP_IntelReopenHeldTip" : "RP_IntelReopenGoneTip").Translate());
            if (Widgets.ButtonText(reopen, "RP_IntelReopen".Translate(), active: ask != null)) ask.OpenLetter();
            y += 28f;
        }

        /// <summary>No pay, no talk: the arrangement is over for good, and they don't like being dropped.</summary>
        private static void StopPaying(Pawn p)
        {
            CompInmate c = p.Inmate();
            if (c == null || !c.informantPaid) return;
            c.informantPaid = false;
            c.informantQuit = true;
            c.tension = Mathf.Min(100f, c.tension + 5f);
            Messages.Message("RP_IntelStoppedPay".Translate(p.LabelShort), p, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        private static void Header(Rect col, ref float y, string key)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(col.x, y, col.width, 30f), key.Translate());
            Text.Font = GameFont.Small;
            y += 30f;
        }

        /// <summary>The width of a row's keep-apart button.</summary>
        private const float ApartButtonW = 76f;

        private static void MutedWarn(Rect col, ref float y, string text)
        {
            GUI.color = PrisonHUD.Warn;
            Widgets.Label(new Rect(col.x + 16f, y, col.width - 16f, 22f), text.Truncate(col.width - 18f));
            GUI.color = Color.white;
            y += 22f;
        }

        private static void MutedText(Rect col, ref float y, string text)
        {
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(col.x, y, col.width, 22f), text.Truncate(col.width - 2f));
            GUI.color = Color.white;
            y += 22f;
        }

        /// <summary>A note in grey that wraps (the hints for an empty list say what starts an entry, in a sentence or two).</summary>
        private static void Muted(Rect col, ref float y, string key)
        {
            string text = key.Translate();
            float h = Mathf.Max(22f, Text.CalcHeight(text, col.width));
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(col.x, y, col.width, h), text);
            GUI.color = Color.white;
            y += h;
        }

        private static void Click(Rect row, Pawn p)
        {
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            if (Widgets.ButtonInvisible(row)) CameraJumper.TryJumpAndSelect(p);
        }
    }
}
