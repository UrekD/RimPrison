using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Policy tab: standing policies on the left, the punishment per misconduct on the right.</summary>
    public static class PolicyTab
    {
        /// <summary>The cut labels of the punishment rows, by label, row width and the rest of the row's text (the hours): one measure per row and change, not one per frame.</summary>
        private static readonly Dictionary<(string, float, string), string> cutLabels = new Dictionary<(string, float, string), string>();

        private static string CutLabel(string label, float width, string rest)
        {
            if (cutLabels.TryGetValue((label, width, rest), out string cut)) return cut;
            if (cutLabels.Count > 500) cutLabels.Clear();
            return cutLabels[(label, width, rest)] = label.Truncate(width - Text.CalcSize(rest).x);
        }

        /// <summary>The ids of the sanction buttons' tooltips (one per row).</summary>
        private const int SanctionTipId = 8_520_000;

        /// <summary>The right column shows the standing orders instead of the punishments.</summary>
        public static bool showStanding;

        /// <summary>The punishments' scroll position (the tour sets it to show the last rows) and the height of their rows at the last draw.</summary>
        public static Vector2 punishScroll;
        private static float punishHeight;

        public static void Draw(Rect rect, PrisonGame game)
        {
            // Left: standing policies. Right: punishment per misconduct (one compact row each).
            var left = new Rect(rect.x, rect.y, rect.width * 0.4f - 10f, rect.height);
            var right = new Rect(rect.x + rect.width * 0.4f + 10f, rect.y, rect.width * 0.6f - 10f, rect.height);
            var list = new Listing_Standard();
            list.Begin(left);
            bool nightShift0 = game.guardNightShift;
            list.CheckboxLabeled("RP_NightShift".Translate(), ref game.guardNightShift, "RP_NightShiftDesc".Translate());
            // The policy itself takes effect at the next hour, not at the next daily balance.
            if (nightShift0 != game.guardNightShift) game.lastShiftBalanceDay = -1;
            list.CheckboxLabeled("RP_AutoReclassify".Translate(), ref game.autoReclassify, "RP_AutoReclassifyDesc".Translate());
            list.CheckboxLabeled("RP_CellsByConduct".Translate(), ref game.cellsByConduct, "RP_CellsByConductDesc".Translate(CellQuality.StepDays, CellQuality.FallBy, CellQuality.MaxGrade));
            list.CheckboxLabeled("RP_WinterJackets".Translate(), ref game.winterJackets, "RP_WinterJacketsDesc".Translate(WinterClothing.ColdBelow.ToStringTemperature("F0")));
            list.GapLine();
            // (Armed response is set in Deployment.)
            list.Label("RP_VisitPolicyLabel".Translate());
            foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(x => x.uiOrder))
            {
                if (!Unlocks.IsUnlocked(sec)) continue;
                Rect row = list.GetRect(26f);
                Widgets.Label(row.LeftPart(0.55f), "   " + sec.LabelCap);
                VisitPolicy cur = game.VisitPolicyFor(sec);
                if (Widgets.ButtonText(row.RightPart(0.43f), ("RP_Visit_" + cur).Translate()))
                    game.visitPolicies[sec] = (VisitPolicy)(((int)cur + 1) % 3);
                TooltipHandler.TipRegion(row, "RP_VisitPolicyDesc".Translate());
            }
            Rect searchRow = list.GetRect(26f);
            Widgets.Label(searchRow.LeftPart(0.55f), "RP_VisitorSearchLabel".Translate());
            if (Widgets.ButtonText(searchRow.RightPart(0.43f), ("RP_VisitorSearch_" + game.visitorSearch).Translate()))
                game.visitorSearch = (VisitorSearchPolicy)(((int)game.visitorSearch + 1) % 3);
            TooltipHandler.TipRegion(searchRow, "RP_VisitorSearchDesc".Translate());
            Rect mailRow = list.GetRect(26f);
            Widgets.Label(mailRow.LeftPart(0.55f), "RP_MailPolicyLabel".Translate());
            if (Widgets.ButtonText(mailRow.RightPart(0.43f), ("RP_MailPolicy_" + game.mailPolicy).Translate()))
                game.mailPolicy = game.mailPolicy == MailPolicy.SpotChecks ? MailPolicy.ReadAll : MailPolicy.SpotChecks;
            TooltipHandler.TipRegion(mailRow, "RP_MailPolicyDesc".Translate(MailSystem.ReadAllChance.ToStringPercent(), (MailSystem.ReadAllChance * MailSystem.SpotCheckShare).ToStringPercent(),
                MailSystem.ReadAllPrivacyPenalty.ToStringPercent()));
            Rect menuRow = list.GetRect(26f);
            Widgets.Label(menuRow.LeftPart(0.55f), "RP_KitchenMenuLabel".Translate());
            if (Widgets.ButtonText(menuRow.RightPart(0.43f), ("RP_KitchenMenu_" + game.kitchenMenu).Translate()))
            {
                game.kitchenMenu = game.kitchenMenu == KitchenMenu.Simple ? KitchenMenu.Fine : KitchenMenu.Simple;
                foreach (Map home in Find.Maps.Where(m => m.IsPlayerHome)) KitchenMenuPolicy.Apply(home, game.kitchenMenu);
            }
            TooltipHandler.TipRegion(menuRow, "RP_KitchenMenuDesc".Translate());
            Rect countRow = list.GetRect(26f);
            Widgets.Label(countRow.LeftPart(0.55f), "RP_CountsLabel".Translate());
            Map prisonMap = Find.AnyPlayerHomeMap;
            string countText = game.countsPerDay <= 0 || prisonMap == null ? "RP_CountsOff".Translate().ToString()
                : "RP_CountsValue".Translate(game.countsPerDay, CountSystem.NextHour(game, GenLocalDate.HourOfDay(prisonMap)).ToString("00")).ToString();
            if (Widgets.ButtonText(countRow.RightPart(0.43f), countText))
                game.countsPerDay = (game.countsPerDay + 1) % (CountSystem.MaxPerDay + 1);
            TooltipHandler.TipRegion(countRow, "RP_CountsDesc".Translate(CountSystem.HoursText(game)));
            Rect remissionRow = list.GetRect(26f);
            Widgets.Label(remissionRow.LeftPart(0.55f), "RP_RemissionLabel".Translate());
            if (Widgets.ButtonText(remissionRow.RightPart(0.43f), (game.remission ? "RP_RemissionOn" : "RP_RemissionOff").Translate()))
                game.remission = !game.remission;
            TooltipHandler.TipRegion(remissionRow, "RP_RemissionDesc".Translate(Remission.CleanDaysPerDayOff, Remission.AtStakeDays,
                (1f - Remission.AtStakeMisconduct).ToStringPercent()));
            Rect adjudicateRow = list.GetRect(26f);
            Widgets.Label(adjudicateRow.LeftPart(0.55f), "RP_AdjudicateLabel".Translate());
            if (Widgets.ButtonText(adjudicateRow.RightPart(0.43f), (game.adjudicate ? "RP_AdjudicateOn" : "RP_AdjudicateOff").Translate()))
                game.adjudicate = !game.adjudicate;
            TooltipHandler.TipRegion(adjudicateRow, "RP_AdjudicateDesc".Translate(Adjudication.MinSeverity, Adjudication.MaxPerSheet, Adjudication.MaxDays));
            if (Adjudication.Waiting(game) is { Count: > 0 } waitingCases && Find.AnyPlayerHomeMap is { } sheetMap)
            {
                Rect sheetRow = list.GetRect(26f);
                if (Widgets.ButtonText(sheetRow, "RP_AdjudicateOpen".Translate(waitingCases.Count))) Dialog_Adjudication.Open(sheetMap);
            }
            Rect staffRow = list.GetRect(26f);
            Widgets.Label(staffRow.LeftPart(0.55f), "RP_StaffSearchLabel".Translate());
            if (Widgets.ButtonText(staffRow.RightPart(0.43f), ("RP_StaffSearch_" + game.staffSearch).Translate()))
                game.staffSearch = (StaffSearchPolicy)(((int)game.staffSearch + 1) % 3);
            TooltipHandler.TipRegion(staffRow, "RP_StaffSearchDesc".Translate());
            Rect drugRow = list.GetRect(26f);
            Widgets.Label(drugRow.LeftPart(0.55f), "RP_DrugTestLabel".Translate());
            if (Widgets.ButtonText(drugRow.RightPart(0.43f), ("RP_DrugTest_" + game.drugTests).Translate()))
            {
                game.drugTests = (DrugTestPolicy)(((int)game.drugTests + 1) % 3);
                // (The kits are ordered at once, not at the next midnight.)
                if (Find.AnyPlayerHomeMap is { } testHome) DrugTesting.EnsureStock(testHome, game);
            }
            TooltipHandler.TipRegion(drugRow, "RP_DrugTestDesc".Translate(DrugTesting.TestTicks, DrugTesting.MaxPerHour));
            if (game.drugTests == DrugTestPolicy.TargetedAndRandom)
            {
                Rect perDayRow = list.GetRect(26f);
                Widgets.Label(perDayRow.LeftPart(0.55f), "RP_DrugTestPerDay".Translate(game.drugTestsPerDay));
                game.drugTestsPerDay = Mathf.RoundToInt(Widgets.HorizontalSlider(perDayRow.RightPart(0.43f).ContractedBy(0f, 6f), game.drugTestsPerDay, DrugTesting.MinPerDay, DrugTesting.MaxPerDay));
            }
            if (game.drugTests != DrugTestPolicy.Off && Find.AnyPlayerHomeMap is { } drugMap)
            {
                int day = Ledger.CurrentDay;
                string blocker = DrugTesting.Blocker(drugMap);
                string status = "RP_DrugTestStatus".Translate(DrugTesting.KitCount(drugMap), game.drugTestLog.TestsYesterday(day), game.drugTestLog.PositivesYesterday(day)).Resolve();
                list.Label(blocker != null ? status + " " + blocker.Translate().Resolve() : status);
            }
            if (Unlocks.Feature(PhoneCalls.TapFeature))
                list.CheckboxLabeled("RP_PhoneTaps".Translate(), ref game.phoneTaps, "RP_PhoneTapsDesc".Translate(PhoneCalls.TapCostPerPhone));
            list.End();

            // The right column: the punishments, or the warden's standing orders (a pair of buttons at its head picks which).
            float half = (right.width - 6f) / 2f;
            var punishRect = new Rect(right.x, right.y, half, 26f);
            var standingRect = new Rect(punishRect.xMax + 6f, right.y, half, 26f);
            if (Widgets.ButtonText(punishRect, "RP_PolicyShowPunishments".Translate())) showStanding = false;
            if (Widgets.ButtonText(standingRect, "RP_StandingOrdersHeading".Translate(game.standingOrders.Count))) showStanding = true;
            TooltipHandler.TipRegion(standingRect, "RP_StandingOrdersTip".Translate());
            // (The one shown is framed.)
            Widgets.DrawBox(showStanding ? standingRect : punishRect, 2);
            right.yMin += 32f;
            if (showStanding)
            {
                StandingOrders.DrawSection(right, game);
                return;
            }
            // (In a scroll view: the help, the measures' note and a row per misconduct are more than the column holds at 1280 x 720. Its height is the last frame's.)
            var view = new Rect(0f, 0f, right.width - 16f, Mathf.Max(punishHeight, right.height));
            Widgets.BeginScrollView(right, ref punishScroll, view);
            list = new Listing_Standard { maxOneColumn = true };
            list.Begin(view);
            list.Label("RP_PolicyHelp".Translate());
            if (SpecialMeasures.Restricted) list.Label("RP_PolicyMeasuresLock".Translate(SpecialMeasures.SolitaryCapHours));
            list.Gap();
            int sanctionRow = 0;
            foreach (MisconductDef def in DefDatabase<MisconductDef>.AllDefsListForReading)
            {
                if (def.sanctionAs != null || def.punishmentHours <= 0 && def.severity < 2) continue;
                int hours = game.punishments.HoursFor(def);
                Sanction kind = game.punishments.KindFor(def);
                Rect row = list.GetRect(28f);
                // (What is set is what is shown and dragged; the state's cap on solitary is noted beside it.)
                int inForce = kind == Sanction.Solitary ? game.punishments.SolitaryHoursFor(def) : hours;
                string RowText(string label) => kind == Sanction.Warning ? label
                    : inForce != hours ? "RP_PolicyRowCapped".Translate(label, hours, inForce).ToString() : "RP_PolicyRow".Translate(label, hours).ToString();
                // (A long label is cut, not the hours: a row is one line high, and "Taking a member of staff hostage: 72h" wrapped its hours onto a second line nobody saw. The tooltip has it whole.)
                Rect labelRect = row.LeftPart(0.40f);
                string label = def.LabelCap.ToString(), shown = CutLabel(label, labelRect.width, RowText(""));
                Widgets.Label(labelRect, RowText(shown));
                if (shown != label) TooltipHandler.TipRegion(labelRect, label);
                // The sanction: click to cycle (solitary, cell, yard, visits, commissary, warning).
                var kindRect = new Rect(row.x + row.width * 0.41f, row.y + 2f, row.width * 0.22f, 24f);
                if (Widgets.ButtonText(kindRect, ("RP_Sanction_" + kind).Translate()))
                    game.punishments.SetKind(def, (Sanction)(((int)kind + 1) % System.Enum.GetValues(typeof(Sanction)).Length));
                // (What an hour of it adds to suppression, and what these hours come to: built on hover, one id a row.)
                string sanctionDesc = ("RP_SanctionDesc_" + kind).Translate();
                if (Suppression.PerHour(kind) > 0f) TooltipHandler.TipRegion(kindRect, () => sanctionDesc + "\n\n" + Suppression.SanctionTip(kind, inForce), SanctionTipId + sanctionRow);
                else TooltipHandler.TipRegion(kindRect, sanctionDesc);
                sanctionRow++;
                if (kind != Sanction.Warning)
                {
                    int newHours = Mathf.RoundToInt(Widgets.HorizontalSlider(new Rect(kindRect.xMax + 6f, row.y, row.xMax - kindRect.xMax - 6f, row.height).ContractedBy(0f, 8f), hours, 0f, 72f));
                    if (newHours != hours) game.punishments.Set(def, newHours);
                }
                list.Gap(4f);
            }
            punishHeight = list.CurHeight + 4f;
            list.End();
            Widgets.EndScrollView();
        }
    }
}
