using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The inmate's prison file, Prison Architect style: a portrait header with security, crime and sentence; bars for
    /// tension (with its cause), fear, suppression and mood; reputation and gang chips (unknown ones as "?" until a
    /// psychologist assesses them); work and programs; family by name; the record; their life inside.
    /// </summary>
    public class ITab_InmateFile : ITab
    {
        private Vector2 scroll;
        private float lastHeight = 700f;

        public ITab_InmateFile()
        {
            size = new Vector2(520f, 560f);
            labelKey = "RP_TabInmateFile";
        }

        private Pawn Inmate => SelPawn is { IsPrisonerOfColony: true } p && p.Inmate()?.security != null ? p : null;

        public override bool IsVisible => Inmate != null;

        protected override void FillTab()
        {
            Pawn p = Inmate;
            if (p == null) return;
            CompInmate c = p.Inmate();
            Rect outer = new Rect(0f, 0f, size.x, size.y).ContractedBy(12f);
            var view = new Rect(0f, 0f, outer.width - 16f, lastHeight);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            float w = view.width;

            // Header: portrait, number and name, security pill and crime, sentence bar.
            GUI.DrawTexture(new Rect(0f, 0f, 64f, 64f), PortraitsCache.Get(p, new Vector2(64f, 64f), Rot4.South, new Vector3(0f, 0f, 0.3f), 1.6f));
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(72f, 0f, w - 72f - 120f, 30f), $"#{c.inmateNumber:D5}  {p.LabelShortCap}");
            Text.Font = GameFont.Small;
            if (DeathRowSystem.Ext(c) == null)
            {
                var move = new Rect(w - 116f, 2f, 116f, 26f);
                if (Widgets.ButtonText(move, "RP_FileMoveCell".Translate())) Find.WindowStack.Add(new FloatMenu(Cellmates.MoveOptions(p, c)));
                TooltipHandler.TipRegion(move, "RP_FileMoveCellTip".Translate());
            }
            var pill = new Rect(72f, 32f, Text.CalcSize(c.security.LabelCap).x + 14f, 20f);
            Widgets.DrawBoxSolid(pill, c.security.color * 0.6f);
            Widgets.DrawBox(pill);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(pill, c.security.LabelCap);
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(new Rect(pill.xMax + 8f, 33f, w - pill.xMax - 8f, 22f), c.crime?.LabelCap ?? "?");
            var sentence = new Rect(72f, 56f, w - 72f, 14f);
            if (DeathRowSystem.Status(c) is { } dr) Label(sentence, dr, new Color(0.75f, 0.25f, 0.3f));
            else if (c.IsLifer) Label(sentence, "RP_SentenceLife".Translate(c.DaysServed.ToString("0.0")), new Color(0.75f, 0.25f, 0.3f));
            else Bar(sentence, c.DaysServed / Mathf.Max(1f, c.TermDays), new Color(0.35f, 0.55f, 0.75f), SentenceText(c));
            y = 80f;

            // State bars, two columns.
            float half = (w - 10f) / 2f;
            Bar(new Rect(0f, y, half, 16f), c.tension / 100f, Color.Lerp(PrisonHUD.Good, PrisonHUD.Bad, c.tension / 100f),
                TensionBarText(p, c));
            float mood = p.needs.mood?.CurLevelPercentage ?? 0.5f;
            Bar(new Rect(half + 10f, y, half, 16f), mood, mood < 0.3f ? PrisonHUD.Bad : mood < 0.5f ? PrisonHUD.Warn : PrisonHUD.Good,
                "RP_FileMoodBar".Translate(mood.ToStringPercent()));
            y += 20f;
            Bar(new Rect(0f, y, half, 16f), c.fear / 100f, new Color(0.56f, 0.31f, 0.63f), "RP_FileFearBar".Translate(c.fear.ToString("F0")));
            var suppressedBar = new Rect(half + 10f, y, half, 16f);
            Bar(suppressedBar, c.suppressed / 100f, new Color(0.5f, 0.5f, 0.55f), Suppression.BarText(c));
            TooltipHandler.TipRegion(suppressedBar, "RP_FileSuppressedTip".Translate(Suppression.Line.ToString("F0"), Suppression.FallPerDay.ToString("0")));
            y += 22f;
            if (HungerStrike.Fasting(c)) Label(new Rect(0f, y, w, 20f), "RP_FileHungerStrike".Translate(), PrisonHUD.Bad);
            else Label(new Rect(0f, y, w, 20f), "RP_FileWorst".Translate(HungerStrike.WorstComplaint(p)), PrisonHUD.Muted);
            y += 24f;

            // Chips: gang, known reputations, unknown ones until assessed.
            float x = 0f;
            Text.Font = GameFont.Tiny;
            void Chip(string label, Color color, string tip = null)
            {
                float cw = Text.CalcSize(label).x + 10f;
                if (x + cw > w)
                {
                    x = 0f;
                    y += 22f;
                }
                var r = new Rect(x, y, cw, 18f);
                Widgets.DrawBoxSolidWithOutline(r, color * 0.3f, color);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(r, label);
                Text.Anchor = TextAnchor.UpperLeft;
                if (tip != null) TooltipHandler.TipRegion(r, tip);
                x += cw + 4f;
            }
            if (c.gang != null && c.gangKnown) Chip(c.gang.label + GangRanks.ChipSuffix(c), c.gang.color);
            else if (c.gang == null && GangExit.ExActive(c)) Chip("RP_FileExMember".Translate(c.exGang.label), c.exGang.color, "RP_FileExMemberTip".Translate(c.exGang.label, GangExit.HatedDays));
            foreach (ReputationDef r in c.reputations)
                if (c.KnownReputation(r)) Chip(r.label, new Color(0.75f, 0.75f, 0.8f), r.description);
            // Not assessed yet: one "?" for everyone (one per hidden trait gave away how many there were).
            if (!c.assessed) Chip("?", new Color(0.5f, 0.5f, 0.5f), "RP_FileUnknownRep".Translate());
            if (c.religious) Chip("RP_FileReligiousChip".Translate(), new Color(0.85f, 0.75f, 0.45f));
            // What is going on with them right now: the payroll, the courts, the infirmary, the governor.
            int now = Find.TickManager.TicksGame;
            if (c.informantPaid) Chip("RP_FileChipPaidInformant".Translate(Money.Format(InformantDecision.DailyPay)), PrisonHUD.Good, "RP_FileChipPaidInformantTip".Translate());
            if (c.appealResolveTick > now)
                Chip("RP_FileChipAppeal".Translate(((c.appealResolveTick - now) / (float)GenDate.TicksPerDay).ToString("0.0")), new Color(0.55f, 0.7f, 0.95f),
                    (c.appealBacked ? "RP_IntelAppealBacked" : "RP_IntelAppealAlone").Translate(((c.appealResolveTick - now) / (float)GenDate.TicksPerDay).ToString("0.0")));
            if (c.clemencyResolveTick > now) Chip("RP_FileChipClemency".Translate(), new Color(0.55f, 0.7f, 0.95f));
            if (c.lastMealGranted) Chip("RP_FileChipLastMeal".Translate(), new Color(0.6f, 0.6f, 0.6f));
            if (Outbreaks.Sick(p)) Chip("RP_FileChipFlu".Translate(), PrisonHUD.Warn);
            if (c.mentorId >= 0)
            {
                bool together = Mentors.Relief(p, c) > 0f;
                Chip("RP_FileChipMentor".Translate(c.mentorName ?? "?") + (together ? " ✓" : ""), together ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.45f, 0.55f, 0.45f),
                    "RP_FileChipMentorTip".Translate(c.mentorName ?? "?") + "\n\n" + (together ? "RP_FileChipMentorTogether" : "RP_FileChipMentorApart").Translate(c.mentorName ?? "?"));
            }
            bool earning = PrisonGame.Instance.remission && Remission.Eligible(c);
            if (earning && (c.remissionEarned > 0 || c.cleanDays > 0))
                Chip("RP_FileChipRemission".Translate(c.remissionEarned, c.cleanDays), new Color(0.55f, 0.75f, 0.55f),
                    "RP_FileChipRemissionTip".Translate(Remission.CleanDaysPerDayOff - c.cleanDays % Remission.CleanDaysPerDayOff));
            else if (c.remissionEarned > 0) Chip("RP_FileChipRemissionDone".Translate(c.remissionEarned), new Color(0.45f, 0.55f, 0.45f));
            if (StaffMemories.MostHated(c) is { } hated) Chip("RP_FileChipHates".Translate(hated.name), PrisonHUD.Bad, "RP_FileChipHatesTip".Translate(hated.name));
            if (StaffMemories.MostTrusted(c) is { } trusted) Chip("RP_FileChipTrusts".Translate(trusted.name), PrisonHUD.Good, "RP_FileChipTrustsTip".Translate(trusted.name));
            if (c.referral != null) Chip("RP_FileChipReferred".Translate(c.referral.label), new Color(0.35f, 0.55f, 0.75f));
            // Debts the warden knows of (the rest are not on any file).
            foreach (InmateDebt debt in DebtSystem.Known())
            {
                bool owes = debt.debtorId == p.thingIDNumber;
                if (!owes && debt.creditorId != p.thingIDNumber) continue;
                Chip(owes ? "RP_FileChipOwes".Translate(debt.creditorName, Money.Format(debt.amount)) : "RP_FileChipOwed".Translate(debt.debtorName, Money.Format(debt.amount)), owes ? PrisonHUD.Warn : new Color(0.7f, 0.7f, 0.5f),
                    "RP_FileChipDebtTip".Translate(debt.debtorName, debt.creditorName, Money.Format(debt.amount), DebtSystem.ReasonText(debt), debt.Overdue ? "RP_DebtOverdue".Translate() : "RP_DebtDue".Translate(((debt.dueTick - now) / (float)GenDate.TicksPerDay).ToString("0.0"))));
            }
            Text.Font = GameFont.Small;
            y += 26f;
            Widgets.DrawLineHorizontal(0f, y, w);
            y += 6f;

            // Where they stand: punishment, bans, parole, the odds outside, what the warden knows.
            foreach ((string text, Color color) in StatusLines(p, c, now))
            {
                float h = Text.CalcHeight(text, w);
                Label(new Rect(0f, y, w, h), text, color);
                y += h;
            }
            // The warden's keep-apart orders are listed above; the button picks another inmate to keep this one from (or lifts the order that is there).
            var apartButton = new Rect(w - 140f, y + 2f, 140f, 24f);
            TooltipHandler.TipRegion(apartButton, "RP_FileKeepApartTip".Translate(p.LabelShort));
            if (Widgets.ButtonText(apartButton, "RP_FileKeepApart".Translate())) KeepApartUI.OpenPicker(p);
            // A known informant can be set to watch someone (and pulled out again): the target first, then the move near them.
            if (InformantTask.CanBeTasked(p, c))
            {
                var taskButton = new Rect(apartButton.x - 144f, apartButton.y, 140f, 24f);
                TooltipHandler.TipRegion(taskButton, "RP_InformantTask_ButtonTip".Translate(p.LabelShort));
                if (Widgets.ButtonText(taskButton, "RP_InformantTask_Button".Translate())) Find.WindowStack.Add(new FloatMenu(InformantTask.TaskOptions(p, c), "RP_InformantTask_MenuTitle".Translate(p.LabelShort)));
            }
            y += 28f;
            Widgets.DrawLineHorizontal(0f, y + 2f, w);
            y += 6f;

            // What they want: the goal with its progress, and what stands in the way of it.
            if (Wants.Shown(c) is { } want)
            {
                bool done = want.Resolved;
                Color tone = want.status switch
                {
                    WantStatus.OnTrack => PrisonHUD.Good,
                    WantStatus.Slipping => PrisonHUD.Warn,
                    WantStatus.Fulfilled => new Color(0.45f, 0.65f, 0.45f),
                    _ => PrisonHUD.Muted,
                };
                var wantBar = new Rect(0f, y, w, 16f);
                Bar(wantBar, want.progress, tone, (done ? "RP_FileWantBarDone" : "RP_FileWantBar").Translate(Wants.Title(want), Wants.StatusLabel(want)));
                TooltipHandler.TipRegion(wantBar, done ? want.def.description : want.def.Worker.DeadlineTipKey.Translate(want.def.description, want.deadlineTick / GenDate.TicksPerDay + 1).Resolve());
                y += 20f;
                string detail = done ? "" : want.def.Worker.Detail(p, c, want).Resolve();
                if (detail.Length > 0)
                {
                    float h = Text.CalcHeight(detail, w);
                    Label(new Rect(0f, y, w, h), detail, PrisonHUD.Muted);
                    y += h;
                }
                Widgets.DrawLineHorizontal(0f, y + 2f, w);
                y += 6f;
            }

            // Work and reform.
            Widgets.Label(new Rect(0f, y, w, 22f), "RP_FileJob".Translate(c.job?.LabelCap ?? "RP_FileNone".Translate(), c.hoursWorked, Money.Format(c.cash)));
            y += 22f;
            x = 0f;
            Text.Font = GameFont.Tiny;
            foreach (ProgramDef prog in c.passedPrograms) Chip("✓ " + prog.label, PrisonHUD.Good);
            Text.Font = GameFont.Small;
            if (c.passedPrograms.Count > 0) y += 22f;
            foreach (var kv in c.programProgress.Where(kv => !c.passedPrograms.Contains(kv.Key)))
            {
                Bar(new Rect(0f, y, w, 14f), Mathf.Clamp01(kv.Value / (float)Mathf.Max(1, kv.Key.sessions)), new Color(0.35f, 0.55f, 0.75f),
                    "RP_FileProgramProgress".Translate(kv.Key.LabelCap, kv.Value));
                y += 18f;
            }
            // Family by name: in touch in white, lost or estranged muted.
            if (c.relatives.Count > 0 || c.familyCount > 0)
            {
                string family = string.Join(" · ", Relatives.Of(c).Select(r => r.CanVisit ? r.Label : $"<color=#888888>{r.Label}{(r.alive ? "" : " †")}</color>"));
                Widgets.Label(new Rect(0f, y, w, Text.CalcHeight(family, w)), "RP_FileFamilyNamed".Translate(family));
                y += Text.CalcHeight(family, w) + 4f;
            }
            else
            {
                Label(new Rect(0f, y, w, 20f), "RP_FileNoFamily".Translate(), PrisonHUD.Muted);
                y += 22f;
            }
            Widgets.DrawLineHorizontal(0f, y, w);
            y += 6f;

            // The record, newest first.
            // Only what the staff saw: an unseen line would give away a hidden reputation (dealing, extortion).
            List<MisconductRecord> shown = ShownRecords(c);
            Widgets.Label(new Rect(0f, y, w, 22f), "RP_FileRecord".Translate(shown.Count));
            y += 22f;
            foreach (MisconductRecord r in shown)
            {
                Widgets.DrawBoxSolid(new Rect(0f, y + 4f, 6f, 14f), r.def.loud ? PrisonHUD.Bad : PrisonHUD.Warn);
                Widgets.Label(new Rect(12f, y, w - 160f, 22f), r.def.LabelCap);
                GUI.color = PrisonHUD.Muted;
                string flags = r.punished ? "RP_FilePunished".Translate().ToString() : "";
                Widgets.Label(new Rect(w - 150f, y, 150f, 22f), "RP_FileAgo".Translate(((now - r.tick) / (float)GenDate.TicksPerDay).ToString("0.0")) + " " + flags);
                GUI.color = Color.white;
                y += 22f;
            }
            Widgets.DrawLineHorizontal(0f, y + 2f, w);
            y += 8f;

            // Life inside.
            var list = new Listing_Standard();
            list.Begin(new Rect(0f, y, w, 2000f));
            ITab_StaffRecord.StaffLifeSection(list, c.life, "RP_FileLife");
            y += list.CurHeight;
            list.End();
            lastHeight = y + 12f;
            Widgets.EndScrollView();
        }

        /// <summary>The disciplinary record as the warden may read it: what staff saw, newest first. An unseen line (dealing, extortion) would give a hidden reputation away.</summary>
        public static List<MisconductRecord> ShownRecords(CompInmate c)
        {
            var shown = c.records.FindAll(r => r.seen);
            shown.Reverse();
            return shown;
        }

        /// <summary>The sentence line of the file: days served of the term, and the days the warden's sheet added to it (see <see cref="Adjudication"/>).</summary>
        public static string SentenceText(CompInmate c) =>
            (c.daysAdded > 0 ? "RP_FileServedAdded".Translate(c.DaysServed.ToString("0.0"), c.TermDays, c.daysAdded) : "RP_FileServed".Translate(c.DaysServed.ToString("0.0"), c.TermDays)).Resolve();

        /// <summary>The tension bar's text: the level and the cause, as far as the warden may know it.</summary>
        public static string TensionBarText(Pawn p, CompInmate c) =>
            "RP_FileTensionBar".Translate(c.tension.ToString("F0"), c.tension < 10f ? "RP_Cause_Calm".Translate() : TensionUtility.CauseText(c));

        /// <summary>The file's status lines, only those that apply.</summary>
        public static IEnumerable<(string, Color)> StatusLines(Pawn p, CompInmate c, int now)
        {
            // Who they share a cell with (their home cell while in solitary), and how that goes: read from the cells worked out
            // once a second for the Rooms block, so the file and the block always agree on the number and the people.
            if (Cellmates.CellOfInmate(p, c) is { } cell)
            {
                List<Pawn> mates = cell.occupants.FindAll(m => m != p);
                if (mates.Count == 0) yield return ("RP_FileCellAlone".Translate(cell.number), PrisonHUD.Muted);
                else
                {
                    string names = string.Join(", ", mates.Select(m => m.LabelShort + (GangUtility.AreRivals(c.gang, m.Inmate()?.gang) && c.gangKnown && m.Inmate()?.gangKnown == true
                        ? " " + "RP_FileMateRival".Translate() : PrisonRelations.Friends(p, m) ? " " + "RP_FileMateFriend".Translate() : (p.relations?.OpinionOf(m) ?? 0) <= -20 ? " " + "RP_FileMateEnemy".Translate() : "")));
                    float t = cell.RowOf(p)?.knownTension ?? 0f;
                    yield return ("RP_FileCellmates".Translate(cell.number, names), t > 0.5f ? PrisonHUD.Warn : t < 0f ? PrisonHUD.Good : PrisonHUD.Muted);
                }
            }
            if (CellQuality.Applies(c) && InmateUtility.IsActiveInmate(p))
                yield return CellQuality.FileLine(c, Cellmates.CellOfInmate(p, c)?.grade ?? -1, now);
            if (Cellmates.WardenHold(c, now))
                yield return ("RP_FileWardenPlaced".Translate(((Cellmates.WardenHoldDays * GenDate.TicksPerDay - (now - c.wardenPlacedTick)) / (float)GenDate.TicksPerDay).ToString("0.0")), PrisonHUD.Muted);
            // An informant's task: whom they watch, the days in place and how exposed they are (only the warden sets one, so it is no secret of the file).
            if (InformantTask.Tasked(c))
                yield return (InformantTask.Line(p, c, now), InformantTask.BandColor(InformantTask.Term(p, c, shown: true)));
            // What their wing is saying (the rumour mill), and that they started it once the warden knows.
            if (Rumours.FileLine(p, c, now) is { } rumour) yield return rumour;
            float Days(int ticks) => ticks / (float)GenDate.TicksPerDay;
            if (c.forcedActivity != null && c.forcedUntilTick > now)
                yield return ("RP_FileStatusConfined".Translate(c.forcedActivity.label, ((c.forcedUntilTick - now) / (float)GenDate.TicksPerHour).ToString("0")), PrisonHUD.Warn);
            else if (c.pendingPunishmentHours > 0)
                yield return ("RP_FileStatusPunishmentDue".Translate(c.pendingPunishmentHours), PrisonHUD.Warn);
            if (c.yardBanUntilTick > now) yield return ("RP_FileStatusPrivilege".Translate("RP_Sanction_LoseYard".Translate(), ((c.yardBanUntilTick - now) / (float)GenDate.TicksPerHour).ToString("0")), PrisonHUD.Warn);
            if (c.shopBanUntilTick > now && !GangDeals.Waives(c, GangDealTerm.Commissary)) yield return ("RP_FileStatusPrivilege".Translate("RP_Sanction_LoseCommissary".Translate(), ((c.shopBanUntilTick - now) / (float)GenDate.TicksPerHour).ToString("0")), PrisonHUD.Warn);
            if (c.visitBanUntilTick > now) yield return ("RP_FileStatusVisitBan".Translate(Days(c.visitBanUntilTick - now).ToString("0.0")), PrisonHUD.Warn);
            if (WatchList.FileLine(c) is { } watch) yield return (watch, PrisonHUD.Warn);
            if (Detox.Supervision(p) != null) yield return ("RP_FileStatusDetox".Translate(), PrisonHUD.Good);
            else if (Detox.InWithdrawal(p)) yield return ("RP_FileStatusWithdrawal".Translate(), PrisonHUD.Warn);
            if (DrugTesting.FileLine(p, c, now) is { } drugTest)
                yield return (drugTest, c.lastTestResult == DrugTestResult.Negative ? PrisonHUD.Good : c.lastTestResult == DrugTestResult.Positive ? PrisonHUD.Bad : PrisonHUD.Warn);
            if (SupermaxSystem.IsIn(c))
                yield return ("RP_FileStatusSupermax".Translate(((now - c.lastReclassTick) / (float)GenDate.TicksPerDay).ToString("0"), SupermaxSystem.CleanDays(c), SupermaxSystem.StepDownCleanDays),
                    SupermaxSystem.CleanDays(c) >= SupermaxSystem.StepDownCleanDays ? PrisonHUD.Good : PrisonHUD.Muted);
            if (Suppression.Held(c)) yield return ("RP_FileSuppressed".Translate(c.suppressed.ToString("F0")), PrisonHUD.Muted);
            if (c.timesAdmitted > 1) yield return ("RP_FileStatusAdmitted".Translate(c.timesAdmitted), PrisonHUD.Muted);
            if (!c.IsLifer && DeathRowSystem.Ext(c) == null)
            {
                float toHalf = c.TermDays * 0.5f - c.DaysServed;
                float untilNext = c.lastHearingTick + ParoleSystem.HearingGapDays * GenDate.TicksPerDay - now;
                yield return (toHalf > 0f ? "RP_FileStatusParoleIn".Translate(toHalf.ToString("0.0"))
                    : untilNext > 0f ? "RP_FileStatusParoleNext".Translate(Days((int)untilNext).ToString("0.0")) : "RP_FileStatusParoleEligible".Translate(), PrisonHUD.Muted);
                float risk = ParoleSystem.ReoffendChance(c);
                yield return ("RP_FileStatusRisk".Translate(ParoleScene.BandOf(risk).Translate(), risk.ToStringPercent()),
                    risk < ParoleScene.LowRisk ? PrisonHUD.Good : risk < ParoleScene.MediumRisk ? PrisonHUD.Warn : PrisonHUD.Bad);
                // Where a hearing in the hearing room stands, and why the board turned them down last time (what the warden could see).
                if (ParoleScene.FileLine(p) is { } hearing) yield return (hearing, PrisonHUD.Muted);
                if (c.paroleDenial != null) yield return ("RP_FileStatusParoleDenied".Translate(c.paroleDenialTick / GenDate.TicksPerDay + 1, c.paroleDenial), PrisonHUD.Warn);
            }
            if (ReleasePrep.FileLine(PrisonGame.Instance, p.MapHeld ?? Find.CurrentMap, p, c) is { } prep) yield return prep;
            if (c.jobWaiting) yield return ("RP_FileStatusJobWaiting".Translate(), PrisonHUD.Good);
            if (Vocation.Trade(c) is { } trade) yield return ("RP_FileStatusTrade".Translate(trade.label), PrisonHUD.Good);
            // A restorative meeting with the victim's family: proposed, waiting, held and how it went.
            if (Amends.FileLine(c) is { } amends) yield return amends;
            // Friends and enemies still inside (the vanilla Social tab has the opinions behind them).
            var friends = new List<string>();
            var enemies = new List<string>();
            foreach (DirectPawnRelation r in p.relations?.DirectRelations ?? new List<DirectPawnRelation>())
            {
                if (r.otherPawn is not { } other || !other.IsPrisonerOfColony || other.Map != p.Map) continue;
                if (r.def == RPRelationDefOf.RP_Friend) friends.Add(other.LabelShort);
                else if (r.def == RPRelationDefOf.RP_Enemy) enemies.Add(other.LabelShort);
            }
            if (friends.Count > 0) yield return ("RP_FileFriends".Translate(string.Join(", ", friends)), PrisonHUD.Good);
            if (enemies.Count > 0) yield return ("RP_FileEnemies".Translate(string.Join(", ", enemies)), PrisonHUD.Warn);
            // Who the warden keeps them apart from, and what stands in the way today.
            foreach ((string, Color) line in KeepApart.FileLines(p)) yield return line;
            // Kin on the staff or inside (public: "brother of guard Hale").
            foreach (string kin in Kin.Lines(p)) yield return (kin, PrisonHUD.Muted);
            if (c.mail.Count > 0)
            {
                var letters = new List<string>();
                for (int i = c.mail.Count - 1; i >= 0; i--) letters.Add("RP_FileMailEntry".Translate(c.mail[i].kind.label, c.mail[i].tick / GenDate.TicksPerDay + 1));
                yield return ("RP_FileMail".Translate(string.Join(", ", letters)), PrisonHUD.Muted);
            }
            if (PrisonGame.Instance.mail.WaitingFor(p) is > 0 and var waitingMail)
                yield return ("RP_FileMailWaiting".Translate(MailSystem.Letters(waitingMail)), PrisonGame.Instance.mail.Overdue(p) > 0 ? PrisonHUD.Warn : PrisonHUD.Muted);
            // The courts and the complaints: only what the warden knows (a case is read from what the prosecution has told them, a complaint from what was decided).
            foreach (string line in Trials.FileLinesCached(p)) yield return (line, PrisonHUD.Warn);
            foreach (string line in Adjudication.FileLinesCached(p)) yield return (line, PrisonHUD.Muted);
            foreach (string line in ComplaintSystem.FileLines(p)) yield return (line, PrisonHUD.Muted);
            yield return ((c.assessed ? "RP_FileAssessed" : "RP_FileNotAssessed").Translate(), PrisonHUD.Muted);
        }

        private static void Label(Rect r, string text, Color color)
        {
            GUI.color = color;
            Widgets.Label(r, text);
            GUI.color = Color.white;
        }

        private static void Bar(Rect r, float frac, Color color, string text)
        {
            Widgets.DrawBoxSolid(r, new Color(0.12f, 0.12f, 0.14f));
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width * Mathf.Clamp01(frac), r.height), color);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(r.x + 4f, r.y, r.width - 4f, r.height), text);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }
    }
}
