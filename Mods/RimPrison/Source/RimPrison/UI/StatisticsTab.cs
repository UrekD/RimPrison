using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>The headings the statistics screen groups its counters under.</summary>
    public enum StatGroup { Security, Contraband, InmateLife, Staff, Programs, Economy, Story }

    /// <summary>How a counter's number is written: a plain count, money, or a number of hours or days.</summary>
    public enum StatUnit { Count, Money, Hours, Days }

    /// <summary>
    /// One line of the statistics screen: the <see cref="PrisonStats"/> counter (field) it shows, how to read it, and where it is
    /// listed. The label is the keyed string RP_Stat_{counter}; the tooltip, when the label is not obvious enough,
    /// RP_StatTip_{counter}.
    /// </summary>
    public class StatRow
    {
        public readonly string counter;
        public readonly Func<PrisonStats, int> get;
        public readonly StatGroup group;
        public readonly StatUnit unit;

        public StatRow(StatGroup group, string counter, Func<PrisonStats, int> get, StatUnit unit)
        {
            this.group = group;
            this.counter = counter;
            this.get = get;
            this.unit = unit;
        }

        public string LabelKey => "RP_Stat_" + counter;
        public string TipKey => "RP_StatTip_" + counter;

        private string label;
        private LoadedLanguage labelLanguage;

        /// <summary>The label as written in the active language (translated once, not on every GUI event).</summary>
        public string Label
        {
            get
            {
                if (label == null || labelLanguage != LanguageDatabase.activeLanguage)
                {
                    label = LabelKey.Translate().CapitalizeFirst().Resolve();
                    labelLanguage = LanguageDatabase.activeLanguage;
                }
                return label;
            }
        }

        public string Format(int value)
        {
            switch (unit)
            {
                case StatUnit.Money: return Money.Format(value);
                case StatUnit.Hours: return "RP_StatsHours".Translate(value.ToString("N0"));
                case StatUnit.Days: return "RP_StatsDays".Translate(value.ToString("N0"));
                default: return value.ToString("N0");
            }
        }
    }

    /// <summary>
    /// The Statistics screen: every lifetime counter the prison keeps, grouped under headings in columns, with the
    /// escaped inmates still at large on top. A counter that is still zero is shown muted rather than hidden, so the
    /// warden can see what has not happened yet.
    /// </summary>
    public static class StatisticsTab
    {
        private const float RowH = 24f, HeadH = 34f, ColGap = 24f, ValueW = 84f, Pad = 4f;

        private static StatRow R(StatGroup group, string counter, Func<PrisonStats, int> get, StatUnit unit = StatUnit.Count) => new StatRow(group, counter, get, unit);

        /// <summary>
        /// Every counter, listed by hand so each has a keyed label and a place on the screen. The events test checks that
        /// no <see cref="PrisonStats"/> field is missing from here.
        /// </summary>
        public static readonly List<StatRow> Rows = new List<StatRow>
        {
            R(StatGroup.Security, "misconducts", s => s.misconducts),
            R(StatGroup.Security, "punishments", s => s.punishments),
            R(StatGroup.Security, "lightSanctions", s => s.lightSanctions),
            R(StatGroup.Security, "assaults", s => s.assaults),
            R(StatGroup.Security, "riots", s => s.riots),
            R(StatGroup.Security, "riotJoined", s => s.riotJoined),
            R(StatGroup.Security, "riotsNegotiated", s => s.riotsNegotiated),
            R(StatGroup.Security, "policeCallouts", s => s.policeCallouts),
            R(StatGroup.Security, "hungerStrikes", s => s.hungerStrikes),
            R(StatGroup.Security, "demandsMet", s => s.demandsMet),
            R(StatGroup.Security, "demandsBroken", s => s.demandsBroken),
            R(StatGroup.Security, "escaped", s => s.escaped),
            R(StatGroup.Security, "stowawayAttempts", s => s.stowawayAttempts),
            R(StatGroup.Security, "stowawaysCaught", s => s.stowawaysCaught),
            R(StatGroup.Security, "stowawaysAway", s => s.stowawaysAway),
            R(StatGroup.Security, "recaptured", s => s.recaptured),
            R(StatGroup.Security, "tunnelsDug", s => s.tunnelsDug),
            R(StatGroup.Security, "tunnelsFound", s => s.tunnelsFound),
            R(StatGroup.Security, "tips", s => s.tips),
            R(StatGroup.Security, "phoneCalls", s => s.phoneCalls),
            R(StatGroup.Security, "phoneTapIntel", s => s.phoneTapIntel),
            R(StatGroup.Security, "informantsRecruited", s => s.informantsRecruited),
            R(StatGroup.Security, "gangRecruits", s => s.gangRecruits),
            R(StatGroup.Security, "gangChallenges", s => s.gangChallenges),
            R(StatGroup.Security, "truces", s => s.truces),
            R(StatGroup.Security, "trucesBroken", s => s.trucesBroken),
            R(StatGroup.Security, "gangWars", s => s.gangWars),
            R(StatGroup.Security, "gangWarsEnded", s => s.gangWarsEnded),
            R(StatGroup.Security, "hostageCrises", s => s.hostageCrises),
            R(StatGroup.Security, "hostagesFreed", s => s.hostagesFreed),
            R(StatGroup.Security, "hostagesKilled", s => s.hostagesKilled),
            R(StatGroup.Security, "wardenAddresses", s => s.wardenAddresses),
            R(StatGroup.Security, "wardenWalks", s => s.wardenWalks),
            R(StatGroup.Security, "wardenWords", s => s.wardenWords),
            R(StatGroup.Security, "wardenStruck", s => s.wardenStruck),
            R(StatGroup.Security, "shotByGuards", s => s.shotByGuards),
            R(StatGroup.Security, "violentDeaths", s => s.violentDeaths),
            R(StatGroup.Security, "murders", s => s.murders),
            R(StatGroup.Security, "trials", s => s.trials),
            R(StatGroup.Security, "adjudicated", s => s.adjudicated),
            R(StatGroup.Security, "daysAddedGiven", s => s.daysAddedGiven, StatUnit.Days),
            R(StatGroup.Security, "daysStruckOut", s => s.daysStruckOut, StatUnit.Days),
            R(StatGroup.Security, "remissionWiped", s => s.remissionWiped),
            R(StatGroup.Security, "chargesBrought", s => s.chargesBrought),
            R(StatGroup.Security, "convictions", s => s.convictions),
            R(StatGroup.Security, "acquittals", s => s.acquittals),
            R(StatGroup.Security, "witnessesRecanted", s => s.witnessesRecanted),
            R(StatGroup.Security, "complaintsFiled", s => s.complaintsFiled),
            R(StatGroup.Security, "complaintsUpheld", s => s.complaintsUpheld),
            R(StatGroup.Security, "complaintsDismissed", s => s.complaintsDismissed),
            R(StatGroup.Security, "complaintsFalseFound", s => s.complaintsFalseFound),
            R(StatGroup.Security, "inquiries", s => s.inquiries),

            R(StatGroup.Contraband, "contrabandSmuggled", s => s.contrabandSmuggled),
            R(StatGroup.Contraband, "contrabandFound", s => s.contrabandFound),
            R(StatGroup.Contraband, "stashesFound", s => s.stashesFound),
            R(StatGroup.Contraband, "dogAlerts", s => s.dogAlerts),
            R(StatGroup.Contraband, "intakeSearches", s => s.intakeSearches),
            R(StatGroup.Contraband, "intakeUnsearched", s => s.intakeUnsearched),
            R(StatGroup.Contraband, "visitorSearches", s => s.visitorSearches),
            R(StatGroup.Contraband, "visitorContrabandFound", s => s.visitorContrabandFound),
            R(StatGroup.Contraband, "visitorsWavedThrough", s => s.visitorsWavedThrough),
            R(StatGroup.Contraband, "mailContrabandFound", s => s.mailContrabandFound),
            R(StatGroup.Contraband, "mailContrabandMissed", s => s.mailContrabandMissed),
            R(StatGroup.Contraband, "cargoContrabandFound", s => s.cargoContrabandFound),
            R(StatGroup.Contraband, "bookContraband", s => s.bookContraband),
            R(StatGroup.Contraband, "smugglersDetained", s => s.smugglersDetained),
            R(StatGroup.Contraband, "smugglersFled", s => s.smugglersFled),
            R(StatGroup.Contraband, "amnestyItems", s => s.amnestyItems),
            R(StatGroup.Contraband, "ringLinksFound", s => s.ringLinksFound),
            R(StatGroup.Contraband, "ringsPartial", s => s.ringsPartial),
            R(StatGroup.Contraband, "ringsBusted", s => s.ringsBusted),
            R(StatGroup.Contraband, "ringMembersTurned", s => s.ringMembersTurned),
            R(StatGroup.Contraband, "drugDeals", s => s.drugDeals),
            R(StatGroup.Contraband, "extortions", s => s.extortions),
            R(StatGroup.Contraband, "overdoses", s => s.overdoses),
            R(StatGroup.Contraband, "drugTests", s => s.drugTests),
            R(StatGroup.Contraband, "drugPositives", s => s.drugPositives),

            R(StatGroup.InmateLife, "admitted", s => s.admitted),
            R(StatGroup.InmateLife, "released", s => s.released),
            R(StatGroup.InmateLife, "transferredOut", s => s.transferredOut),
            R(StatGroup.InmateLife, "deaths", s => s.deaths),
            R(StatGroup.InmateLife, "executions", s => s.executions),
            R(StatGroup.InmateLife, "wrongfulExecutions", s => s.wrongfulExecutions),
            R(StatGroup.InmateLife, "commuted", s => s.commuted),
            R(StatGroup.InmateLife, "visits", s => s.visits),
            R(StatGroup.InmateLife, "familyDays", s => s.familyDays),
            R(StatGroup.InmateLife, "lettersDelivered", s => s.lettersDelivered),
            R(StatGroup.InmateLife, "lettersWritten", s => s.lettersWritten),
            R(StatGroup.InmateLife, "workouts", s => s.workouts),
            R(StatGroup.InmateLife, "booksDelivered", s => s.booksDelivered),
            R(StatGroup.InmateLife, "booksRead", s => s.booksRead),
            R(StatGroup.InmateLife, "prayers", s => s.prayers),
            R(StatGroup.InmateLife, "friendships", s => s.friendships),
            R(StatGroup.InmateLife, "feuds", s => s.feuds),
            R(StatGroup.InmateLife, "feudsMediated", s => s.feudsMediated),
            R(StatGroup.InmateLife, "feudFlashpoints", s => s.feudFlashpoints),
            R(StatGroup.InmateLife, "cellMoves", s => s.cellMoves),
            R(StatGroup.InmateLife, "promoted", s => s.promoted),
            R(StatGroup.InmateLife, "demoted", s => s.demoted),
            R(StatGroup.InmateLife, "inmateMeals", s => s.inmateMeals),
            R(StatGroup.InmateLife, "fluCases", s => s.fluCases),
            R(StatGroup.InmateLife, "medicalEmergencies", s => s.medicalEmergencies),
            R(StatGroup.InmateLife, "foodScares", s => s.foodScares),

            R(StatGroup.Staff, "staffInjured", s => s.staffInjured),
            R(StatGroup.Staff, "staffDeaths", s => s.staffDeaths),
            R(StatGroup.InmateLife, "bodiesSent", s => s.bodiesSent),
            R(StatGroup.InmateLife, "buried", s => s.buried),
            R(StatGroup.InmateLife, "condolences", s => s.condolences),
            R(StatGroup.Staff, "staffWentHome", s => s.staffWentHome),
            R(StatGroup.Staff, "staffBreaks", s => s.staffBreaks),
            R(StatGroup.Staff, "staffTrained", s => s.staffTrained),
            R(StatGroup.Staff, "commendations", s => s.commendations),
            R(StatGroup.Staff, "rescuesRemembered", s => s.rescuesRemembered),
            R(StatGroup.Staff, "staffBurnedOut", s => s.staffBurnedOut),
            R(StatGroup.Staff, "staffRetired", s => s.staffRetired),
            R(StatGroup.Staff, "promotions", s => s.promotions),
            R(StatGroup.Staff, "promotionsDeclined", s => s.promotionsDeclined),
            R(StatGroup.Staff, "resignations", s => s.resignations),
            R(StatGroup.Staff, "staffStrikes", s => s.staffStrikes),
            R(StatGroup.Staff, "rookieMistakes", s => s.rookieMistakes),
            R(StatGroup.Staff, "staffApproached", s => s.staffApproached),
            R(StatGroup.Staff, "bribesRefused", s => s.bribesRefused),
            R(StatGroup.Staff, "bribes", s => s.bribes),
            R(StatGroup.Staff, "staffDeliveries", s => s.staffDeliveries),
            R(StatGroup.Staff, "staffSearches", s => s.staffSearches),
            R(StatGroup.Staff, "staffCaught", s => s.staffCaught),
            R(StatGroup.Staff, "doubleAgentTips", s => s.doubleAgentTips),
            R(StatGroup.Staff, "unitTips", s => s.unitTips),
            R(StatGroup.Staff, "iaCases", s => s.iaCases),
            R(StatGroup.Staff, "staffDismissed", s => s.staffDismissed),
            R(StatGroup.Staff, "arrestsMade", s => s.arrestsMade),
            R(StatGroup.Staff, "staffArrested", s => s.staffArrested),

            R(StatGroup.Programs, "programsPassed", s => s.programsPassed),
            R(StatGroup.Programs, "therapySessions", s => s.therapySessions),
            R(StatGroup.Programs, "detoxHours", s => s.detoxHours, StatUnit.Hours),
            R(StatGroup.Programs, "gotClean", s => s.gotClean),
            R(StatGroup.Programs, "mentorships", s => s.mentorships),
            R(StatGroup.Programs, "remissionDays", s => s.remissionDays, StatUnit.Days),
            R(StatGroup.Programs, "paroled", s => s.paroled),
            R(StatGroup.Programs, "paroleRecommended", s => s.paroleRecommended),
            R(StatGroup.Programs, "paroleHearings", s => s.paroleHearings),
            R(StatGroup.Programs, "paroleViolations", s => s.paroleViolations),
            R(StatGroup.Programs, "reoffended", s => s.reoffended),
            R(StatGroup.Programs, "reoffendersReturned", s => s.reoffendersReturned),
            R(StatGroup.Programs, "supervisionStarted", s => s.supervisionStarted),
            R(StatGroup.Programs, "supervisionStayedOut", s => s.supervisionStayedOut),
            R(StatGroup.Programs, "supervisionRearrested", s => s.supervisionRearrested),
            R(StatGroup.Programs, "supervisionSetbacks", s => s.supervisionSetbacks),
            R(StatGroup.Programs, "supervisionHelped", s => s.supervisionHelped),
            R(StatGroup.Programs, "supervisionLodged", s => s.supervisionLodged),
            R(StatGroup.Programs, "supervisionInterviews", s => s.supervisionInterviews),
            R(StatGroup.Programs, "formerNews", s => s.formerNews),
            R(StatGroup.Programs, "appealsFiled", s => s.appealsFiled),
            R(StatGroup.Programs, "exonerations", s => s.exonerations),

            R(StatGroup.Economy, "commissarySales", s => s.commissarySales, StatUnit.Money),
            R(StatGroup.Economy, "debtsFormed", s => s.debtsFormed),
            R(StatGroup.Economy, "debtsPaid", s => s.debtsPaid),
            R(StatGroup.Economy, "debtBeatings", s => s.debtBeatings),
            R(StatGroup.Economy, "cardGames", s => s.cardGames),
            R(StatGroup.Story, "measuresStarted", s => s.measuresStarted),
            R(StatGroup.Story, "measuresLifted", s => s.measuresLifted),
            R(StatGroup.Story, "measuresFailed", s => s.measuresFailed),
            R(StatGroup.Story, "flagshipsEarned", s => s.flagshipsEarned),
            R(StatGroup.Story, "legendsArrived", s => s.legendsArrived),
            R(StatGroup.Economy, "itemsExported", s => s.itemsExported),
            R(StatGroup.Economy, "shuttleLandings", s => s.shuttleLandings),
            R(StatGroup.Economy, "cargoRuns", s => s.cargoRuns),
            R(StatGroup.Economy, "shuttlesOffPad", s => s.shuttlesOffPad),
            R(StatGroup.Economy, "shuttlesCancelled", s => s.shuttlesCancelled),
            R(StatGroup.Economy, "contractsMet", s => s.contractsMet),
            R(StatGroup.Economy, "contractsMissed", s => s.contractsMissed),
            R(StatGroup.Economy, "custodyOffers", s => s.custodyOffers),
            R(StatGroup.Economy, "custodyAccepted", s => s.custodyAccepted),
            R(StatGroup.Economy, "custodyAdmitted", s => s.custodyAdmitted),
            R(StatGroup.Economy, "custodyMet", s => s.custodyMet),
            R(StatGroup.Economy, "custodyEarned", s => s.custodyEarned, StatUnit.Money),
            R(StatGroup.Economy, "custodyMissed", s => s.custodyMissed),
            R(StatGroup.Economy, "custodyForfeited", s => s.custodyForfeited),
            R(StatGroup.Economy, "grantsCompleted", s => s.grantsCompleted),
            R(StatGroup.Economy, "landParcels", s => s.landParcels),
            R(StatGroup.Economy, "landCells", s => s.landCells),
            R(StatGroup.Economy, "lawsuits", s => s.lawsuits),
            R(StatGroup.Economy, "inspections", s => s.inspections),

            R(StatGroup.Story, "storyBeats", s => s.storyBeats),
            R(StatGroup.Story, "blackouts", s => s.blackouts),
            R(StatGroup.Story, "weatherEvents", s => s.weatherEvents),
            R(StatGroup.Story, "fires", s => s.fires),
        };

        private static readonly StatGroup[] Groups = (StatGroup[])Enum.GetValues(typeof(StatGroup));

        private static readonly Dictionary<StatGroup, List<StatRow>> byGroup = Groups.ToDictionary(g => g, g => Rows.Where(r => r.group == g).ToList());

        /// <summary>For the UI tour: the next time the screen is drawn it is scrolled to the head of this group (and the request is let go).</summary>
        internal static StatGroup? TourScrollTo;

        public static void Draw(Rect rect, PrisonGame game, Map map, ref Vector2 scroll)
        {
            float width = rect.width - 16f;
            int cols = width >= 900f ? 3 : 2;
            float colW = (width - (cols - 1) * ColGap) / cols;
            List<Fugitive> fugitives = game.fugitives.Where(f => f?.pawn != null).ToList();
            if (TourScrollTo is { } scrollTo)
            {
                TourScrollTo = null;
                float top = HeadH + Math.Max(1, fugitives.Count) * RowH + 10f;
                foreach (StatGroup before in Groups)
                {
                    if (before == scrollTo) break;
                    top += HeadH + Mathf.CeilToInt(byGroup[before].Count / (float)cols) * RowH + 10f;
                }
                scroll.y = top;
            }
            float height = HeadH + Math.Max(1, fugitives.Count) * RowH + 10f;
            foreach (StatGroup g in Groups) height += HeadH + Mathf.CeilToInt(byGroup[g].Count / (float)cols) * RowH + 10f;
            var view = new Rect(0f, 0f, width, height);
            Widgets.BeginScrollView(rect, ref scroll, view);
            float y = 0f;
            Header(ref y, width, "RP_StatsGroup_Fugitives");
            DrawFugitives(ref y, width, fugitives);
            PrisonStats stats = game.stats;
            foreach (StatGroup g in Groups)
            {
                Header(ref y, width, "RP_StatsGroup_" + g);
                List<StatRow> rows = byGroup[g];
                int perCol = Mathf.CeilToInt(rows.Count / (float)cols);
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = new Rect((i / perCol) * (colW + ColGap), y + (i % perCol) * RowH, colW, RowH);
                    // (Only the rows in view: a hundred and thirty rows of text and tooltips on every GUI event, most of them off the screen.)
                    if (r.yMax >= scroll.y && r.y <= scroll.y + rect.height) DrawRow(r, rows[i], stats);
                }
                y += perCol * RowH + 10f;
            }
            Widgets.EndScrollView();
        }

        private static void Header(ref float y, float width, string key)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, width, 30f), key.Translate());
            Text.Font = GameFont.Small;
            Widgets.DrawLineHorizontal(0f, y + 30f, width);
            y += HeadH;
        }

        private static void DrawRow(Rect r, StatRow row, PrisonStats stats)
        {
            int value = row.get(stats);
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            GUI.color = value == 0 ? PrisonHUD.Muted : Color.white;
            Widgets.Label(new Rect(r.x + Pad, r.y, r.width - ValueW - 2 * Pad, r.height), row.Label.Truncate(r.width - ValueW - 2 * Pad));
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(new Rect(r.xMax - ValueW - Pad, r.y, ValueW, r.height), row.Format(value));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (Mouse.IsOver(r) && row.TipKey.CanTranslate()) TooltipHandler.TipRegion(r, row.TipKey.Translate());
        }

        /// <summary>Escaped inmates the police are still looking for: name, crime and how long they have been out.</summary>
        private static void DrawFugitives(ref float y, float width, List<Fugitive> fugitives)
        {
            if (fugitives.Count == 0)
            {
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(Pad, y, width, RowH), "RP_StatsNoFugitives".Translate());
                GUI.color = Color.white;
                y += RowH + 10f;
                return;
            }
            int now = Find.TickManager.TicksGame;
            foreach (Fugitive f in fugitives)
            {
                var r = new Rect(0f, y, width, RowH);
                if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
                Widgets.Label(new Rect(Pad, y, 240f, RowH), f.pawn.LabelShortCap.Truncate(236f));
                string crime = f.pawn.Inmate()?.crime?.LabelCap ?? "?";
                Widgets.Label(new Rect(260f, y, width - 260f - 170f, RowH), crime.Truncate(width - 260f - 172f));
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(width - 170f - Pad, y, 170f, RowH),
                    f.escapedTick < 0 ? "RP_StatsAtLargeUnknown".Translate() : "RP_StatsAtLarge".Translate(((now - f.escapedTick) / (float)GenDate.TicksPerDay).ToString("0.#")));
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(r, "RP_StatsFugitiveTip".Translate(f.pawn.LabelShort));
                y += RowH;
            }
            y += 10f;
        }
    }
}
