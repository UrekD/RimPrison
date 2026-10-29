using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>The Staff tab: the header, the hire band and the roster.</summary>
    public static class StaffTab
    {
        public static void Draw(Rect rect, PrisonGame game, Map map, ref Vector2 scroll)
        {
            List<Pawn> staff = StaffCommute.AllStaff(map);
            int wages = StaffCareers.Payroll(staff);
            int guards = staff.Count(p => p.GetComp<CompStaff>().role.guardDuties);
            int inmates = map.mapPawns.PrisonersOfColonySpawnedCount;
            // Header: the numbers that matter, and the vanilla work / clothing windows (their buttons are hidden).
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, 300f, 30f), "RP_StaffHeader".Translate(staff.Count));
            Text.Font = GameFont.Small;
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(rect.x + 160f, rect.y + 6f, 420f, 24f),
                "RP_StaffHeaderLine".Translate(Money.Format(wages), guards > 0 ? (inmates / (float)guards).ToString("0.0") : "-"));
            GUI.color = Color.white;
            var work = new Rect(rect.xMax - 300f, rect.y, 145f, 26f);
            var assign = new Rect(work.xMax + 10f, rect.y, 145f, 26f);
            TooltipHandler.TipRegion(work, "RP_StaffWorkButtonTip".Translate());
            TooltipHandler.TipRegion(assign, "RP_StaffAssignButtonTip".Translate());
            if (Widgets.ButtonText(work, "RP_StaffWorkButton".Translate())) Find.MainTabsRoot.SetCurrentTab(RPVanillaDefOf.Work);
            if (Widgets.ButtonText(assign, "RP_StaffAssignButton".Translate())) Find.MainTabsRoot.SetCurrentTab(RPVanillaDefOf.Assign);

            // The roster: the guards on duty by hour, and the handover (the strip is worked out once an hour, not here).
            float rosterY = rect.y + 34f;
            DrawRoster(new Rect(rect.x, rosterY, rect.width, RosterH - 6f), game, map);
            // The promises to the staff and the rises that stand: a row each below the roster, the list moved down by the rows (nothing when there are none).
            float panelH = StaffAgreements.PanelLines(game) * StaffAgreements.PanelLineH;
            if (panelH > 0f) DrawAgreements(new Rect(rect.x, rosterY + RosterH - 2f, rect.width, panelH), game, map);

            // Hire band at the bottom: one button per unlocked role.
            List<StaffRoleDef> roles = StaffUtility.Roles.Where(Unlocks.IsUnlocked).ToList();
            int perRow = Mathf.Max(1, Mathf.FloorToInt((rect.width + 6f) / 156f));
            float bandH = Mathf.CeilToInt(roles.Count / (float)perRow) * 50f + 24f;
            var band = new Rect(rect.x, rect.yMax - bandH, rect.width, bandH);
            Widgets.Label(new Rect(band.x, band.y, band.width, 22f), "RP_HireHelp".Translate());
            for (int i = 0; i < roles.Count; i++)
            {
                StaffRoleDef role = roles[i];
                var b = new Rect(band.x + i % perRow * 156f, band.y + 24f + i / perRow * 50f, 150f, 44f);
                bool afford = game.ledger.funds >= role.hireCost;
                if (Widgets.ButtonText(b, "")) HiringUI.Open(map, role);
                int count = staff.Count(p => p.GetComp<CompStaff>().role == role);
                Text.Anchor = TextAnchor.UpperCenter;
                Widgets.Label(new Rect(b.x, b.y + 4f, b.width, 20f), $"{role.LabelCap} ({count})");
                Text.Font = GameFont.Tiny;
                GUI.color = afford ? PrisonHUD.Muted : PrisonHUD.Bad;
                Widgets.Label(new Rect(b.x, b.y + 24f, b.width, 18f), "RP_HireCost".Translate(Money.Format(role.hireCost), Money.Format(StaffCareers.WageAt(role, 0))));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(b, role.description);
            }

            // Roster: column headings, then one row per member of staff.
            GUI.color = PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            float hy = rect.y + 38f + RosterH + panelH;
            (float x, string key)[] cols =
            {
                (40f, "RP_StaffColName"), (182f, "RP_StaffColRole"), (294f, "RP_StaffColStatus"), (430f, "RP_StaffColWage"),
                (500f, "RP_StaffColService"), (570f, "RP_StaffColCommended"), (612f, "RP_StaffColShift"), (690f, "RP_StaffColPatrol"), (850f, "RP_StaffColPin"),
            };
            for (int c = 0; c < cols.Length; c++)
            {
                // Each heading's tooltip region ends where the next column begins.
                var head = new Rect(rect.x + cols[c].x, hy, c + 1 < cols.Length ? cols[c + 1].x - cols[c].x - 2f : 120f, 18f);
                Widgets.Label(new Rect(head.x, head.y, 120f, head.height), cols[c].key.Translate());
                if (cols[c].key == "RP_StaffColCommended") TooltipHandler.TipRegion(head, "RP_StaffColCommendedTip".Translate());
            }
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            var outer = new Rect(rect.x, rect.y + 58f + RosterH + panelH, rect.width, band.y - rect.y - 66f - RosterH - panelH);
            var view = new Rect(0f, 0f, outer.width - 16f, staff.Count * 38f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            int hour = GenLocalDate.HourOfDay(map);
            for (int i = 0; i < staff.Count; i++)
            {
                Pawn p = staff[i];
                CompStaff sc = p.GetComp<CompStaff>();
                var row = new Rect(0f, y, view.width, 36f);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                GUI.DrawTexture(new Rect(row.x + 2f, row.y + 2f, 32f, 32f), PortraitsCache.Get(p, new Vector2(32f, 32f), Rot4.South, new Vector3(0f, 0f, 0.3f), 1.6f));
                float ty = row.y + 8f;
                Widgets.Label(new Rect(row.x + 40f, ty, 140f, 24f), p.LabelShortCap.Truncate(138f));
                Widgets.Label(new Rect(row.x + 182f, ty, 110f, 24f), StaffCareers.TitleCap(sc).Truncate(108f));
                (string status, Color color) = StaffStatus(p, sc, hour);
                var chip = new Rect(row.x + 294f, row.y + 8f, 126f, 20f);
                Widgets.DrawBoxSolid(chip, new Color(color.r * 0.35f, color.g * 0.35f, color.b * 0.35f));
                Widgets.DrawBox(chip);
                GUI.color = color;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(chip, status);
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Widgets.Label(new Rect(row.x + 430f, ty, 70f, 24f), Money.Format(StaffCareers.Wage(sc)));
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(row.x + 500f, ty, 70f, 24f), sc.hiredTick == CompStaff.NotHired ? "-" : "RP_StaffDays".Translate(((Find.TickManager.TicksGame - sc.hiredTick) / (float)GenDate.TicksPerDay).ToString("0")).ToString());
                GUI.color = Color.white;
                if (sc.commendations > 0)
                {
                    var star = new Rect(row.x + 570f, ty, 40f, 24f);
                    Widgets.Label(star, "★" + sc.commendations);
                    TooltipHandler.TipRegion(star, "RP_StaffCommendations".Translate(sc.commendations));
                }
                Widgets.Label(new Rect(row.x + 612f, ty, 70f, 24f), (sc.nightShift ? "RP_StaffNightShift" : "RP_StaffDayShift").Translate());
                // (The state's chief sets the patrols while they are here: no zone to pick for them.)
                if (sc.role.guardDuties && !sc.statePosted)
                {
                    var patrol = new Rect(row.x + 690f, row.y + 6f, 150f, 24f);
                    if (Widgets.ButtonText(patrol, sc.patrolZone?.LabelCap ?? "RP_PatrolAnywhereShort".Translate()))
                    {
                        var options = new List<FloatMenuOption> { new FloatMenuOption("RP_PatrolAnywhere".Translate(), () => UnitOfficers.SetPatrol(p, sc, null)) };
                        foreach (PrisonZoneDef z in DefDatabase<PrisonZoneDef>.AllDefsListForReading)
                        {
                            if (!z.inmateAccess || !Kin.PatrolAllowed(p, z)) continue;
                            PrisonZoneDef chosen = z;
                            options.Add(new FloatMenuOption(z.LabelCap, () => UnitOfficers.SetPatrol(p, sc, chosen)));
                        }
                        Find.WindowStack.Add(new FloatMenu(options));
                    }
                    TooltipHandler.TipRegion(patrol, "RP_PatrolDesc".Translate());
                }
                // The shift pin (the state's chief is posted by the state, the warden keeps their own hours: no pin for them).
                if (Roster.OnRoster(sc) && !sc.statePosted)
                {
                    var pin = new Rect(row.x + 850f, row.y + 6f, 100f, 24f);
                    if (Widgets.ButtonText(pin, Roster.PinLabel(sc.shiftPin))) Roster.SetPin(sc, Roster.NextPin(sc.shiftPin));
                    TooltipHandler.TipRegion(pin, "RP_ShiftPinTip".Translate());
                }
                var click = new Rect(row.x, row.y, 680f, row.height);
                if (p.Spawned && Widgets.ButtonInvisible(click)) CameraJumper.TryJumpAndSelect(p);
                y += 38f;
            }
            Widgets.EndScrollView();
        }

        /// <summary>One row for the promise that is running and one for each rise that stands, each a short line (the term and the whole of it are in the tooltip), and a button to take a rise back at the row's right.</summary>
        private static void DrawAgreements(Rect rect, PrisonGame game, Map map)
        {
            StaffAgreementBook book = game.agreements;
            float y = rect.y;
            const float buttonW = 160f;
            float lineW = rect.width - buttonW - 10f;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            if (book.active is { } a)
            {
                GUI.color = a.grievance == StaffGrievance.Safety && a.hurtTick >= 0 ? PrisonHUD.Bad : PrisonHUD.Warn;
                var line = new Rect(rect.x, y, lineW, StaffAgreements.PanelLineH);
                Widgets.Label(line, StaffAgreements.ActiveShort(a).Truncate(line.width));
                TooltipHandler.TipRegion(line, new TipSignal(StaffAgreements.ActiveLine(a), 71230520));
                if (a.grievance == StaffGrievance.Pay && book.rises.FirstOrDefault(r => r.role == a.role) is { } own)
                    DrawTakeBack(new Rect(rect.xMax - buttonW, y + 1f, buttonW, StaffAgreements.PanelLineH - 2f), game, map, own);
                y += StaffAgreements.PanelLineH;
            }
            for (int i = 0; i < book.rises.Count; i++)
            {
                PayRise rise = book.rises[i];
                // (The rise a pay promise is still being kept with is on the promise's own row, with the button.)
                if (StaffAgreements.OnTheActiveLine(book, rise)) continue;
                GUI.color = StaffAgreements.TakingBackBreaks(rise) ? PrisonHUD.Warn : PrisonHUD.Muted;
                var line = new Rect(rect.x, y, lineW, StaffAgreements.PanelLineH);
                Widgets.Label(line, StaffAgreements.RiseShort(rise).Truncate(line.width));
                TooltipHandler.TipRegion(line, new TipSignal(StaffAgreements.RiseLine(rise), 71230521 + i));
                DrawTakeBack(new Rect(rect.xMax - buttonW, y + 1f, buttonW, StaffAgreements.PanelLineH - 2f), game, map, rise);
                // (A rise taken back is gone from the list: the rest are drawn at the next frame.)
                if (i >= book.rises.Count || book.rises[i] != rise) break;
                y += StaffAgreements.PanelLineH;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void DrawTakeBack(Rect button, PrisonGame game, Map map, PayRise rise)
        {
            TooltipHandler.TipRegion(button, "RP_TakeBackRiseTip".Translate());
            if (!Widgets.ButtonText(button, "RP_TakeBackRise".Translate())) return;
            if (StaffAgreements.TakingBackBreaks(rise))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RP_TakeBackRiseConfirm".Translate(StaffAgreements.Plural(rise.role)), () => StaffAgreements.TakeBack(map, game, rise), destructive: true));
            else StaffAgreements.TakeBack(map, game, rise);
        }

        /// <summary>The roster's strip of guards on duty, with its split and the handover's setting beside it.</summary>
        private const float RosterH = 56f;

        private static void DrawRoster(Rect rect, PrisonGame game, Map map)
        {
            var strip = new Rect(rect.x, rect.y + 2f, 576f, rect.height - 2f);
            Roster.DrawStrip(strip, map);
            float x = strip.xMax + 14f;
            (int nights, int days, int pinned) = Roster.Split(map);
            Text.Font = GameFont.Tiny;
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(x, rect.y, rect.xMax - x, 16f), Roster.Heading(map));
            if (game.guardNightShift) Widgets.Label(new Rect(x, rect.y + 16f, rect.xMax - x, 16f), "RP_RosterSplit".Translate(nights, days, pinned));
            else
            {
                GUI.color = PrisonHUD.Warn;
                Widgets.Label(new Rect(x, rect.y + 16f, rect.xMax - x, 16f), "RP_RosterPolicyOff".Translate().Truncate(rect.xMax - x));
            }
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            // The handover: 0-3 hours past 22:00, at overtime pay.
            var label = new Rect(x, rect.y + 32f, 54f, 22f);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(label.x, label.y + 3f, label.width, 18f), "RP_RosterHandover".Translate());
            Text.Font = GameFont.Small;
            var minus = new Rect(label.xMax + 2f, label.y, 24f, 22f);
            var value = new Rect(minus.xMax + 2f, label.y, 100f, 22f);
            var plus = new Rect(value.xMax + 2f, label.y, 24f, 22f);
            if (Widgets.ButtonText(minus, "-") && game.handoverHours > 0) { game.handoverHours--; Roster.Invalidate(); }
            if (Widgets.ButtonText(plus, "+") && game.handoverHours < Roster.MaxHandover) { game.handoverHours++; Roster.Invalidate(); }
            Text.Anchor = TextAnchor.MiddleCenter;
            Text.Font = GameFont.Tiny;
            Widgets.Label(value, game.handoverHours == 0 ? "RP_RosterHandoverNone".Translate() : "RP_RosterHandoverValue".Translate(game.handoverHours));
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            // (The cost is worked out when the tooltip is shown, not every frame: one delegate for all frames, reading the map from a field.)
            handoverTipMap = map;
            TooltipHandler.TipRegion(new Rect(label.x, label.y, plus.xMax - label.x, 22f), handoverTip, 71230411);
        }

        private static Map handoverTipMap;

        private static readonly System.Func<string> handoverTip = () => "RP_RosterHandoverTip".Translate(Roster.OvertimeRate.ToString("0.0#") + "x", Money.Format(HandoverCost(handoverTipMap))).Resolve();

        /// <summary>What the handover costs a day: the overtime of the guards on the roster now at the hours set (one hour when it is off, as an example).</summary>
        private static int HandoverCost(Map map)
        {
            int hours = Mathf.Max(1, ShiftSystem.HandoverHours), cost = 0;
            foreach (Pawn p in StaffCommute.AllStaff(map))
                if (p.GetComp<CompStaff>() is { } sc && ShiftSystem.StaysForHandover(sc) && StaffCareers.Available(sc)) cost += StaffCareers.OvertimeFor(sc, hours);
            return cost;
        }

        /// <summary>What a member of staff is up to, as a coloured chip.</summary>
        private static (string, Color) StaffStatus(Pawn p, CompStaff sc, int hour)
        {
            if (CorruptionSystem.Suspended(sc)) return ((CorruptionSystem.StoodDownOnly(sc) ? "RP_StaffStatus_StoodDown" : "RP_StaffStatus_Suspended").Translate(), CorruptionSystem.StoodDownOnly(sc) ? PrisonHUD.Warn : PrisonHUD.Bad);
            if (StaffCommute.AtHome(p)) return ("RP_StaffStatus_Home".Translate(), new Color(0.31f, 0.56f, 0.79f));
            if (IncidentWorker_StaffStrike.Striking(p)) return ("RP_StaffStatus_Strike".Translate(), PrisonHUD.Bad);
            if (p.Downed || p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p)) return ("RP_StaffStatus_Hurt".Translate(), new Color(1f, 0.6f, 0.2f));
            if (sc.iaOpen) return ("RP_StaffStatus_IA".Translate(), new Color(0.56f, 0.31f, 0.63f));
            if (sc.exposedCorrupt && !sc.doubleAgent) return ("RP_StaffStatus_Corrupt".Translate(), new Color(0.6f, 0.15f, 0.15f));
            if (p.timetable?.GetAssignment(hour) == TimeAssignmentDefOf.Joy) return ("RP_StaffStatus_Break".Translate(), PrisonHUD.Warn);
            // (The warden never goes home: asleep in their own bed.)
            if (p.timetable?.GetAssignment(hour) == TimeAssignmentDefOf.Sleep) return ((sc.role.warden ? "RP_StaffStatus_Resting" : "RP_StaffStatus_GoingHome").Translate(), new Color(0.31f, 0.56f, 0.79f));
            return ("RP_StaffStatus_Duty".Translate(), PrisonHUD.Good);
        }
    }
}
