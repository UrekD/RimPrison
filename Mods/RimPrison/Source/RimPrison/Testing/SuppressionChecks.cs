using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Suppression made legible (Security/Suppression.cs): the desk's wing table and the weekly report count who is held down; the Programs screen names suppression when it is why a class has nobody; the policy
    /// row's tooltip gives the hours of each sanction; a wing whose lid is about to come off (held down, nearly clear, tense) gets a budgeted letter a day ahead and twice the riot odds for the day after, once in
    /// five days; and the file's bar reads as a date. Self-contained, on throwaway inmates: every inmate's suppression, tension and the wings' letter ticks put back by the rollback. The round trip of the one saved
    /// field (<see cref="PrisonGame.lidTicks"/>) is in the saveload plan (<see cref="SeedSuppressionForReload"/>).
    /// </summary>
    public static partial class AutoTest
    {
        private static void SuppFail(string check, string what) => problems0.Add($"suppression ({check}): {what}");

        private static void CheckSuppression(Map map)
        {
            CheckSuppressionWingsAndReport(map);
            CheckSuppressionProgramsBlocker(map);
            CheckSuppressionPolicyTip(map);
            CheckSuppressionFileDate(map);
            CheckSuppressionLidRule(map);
            CheckSuppressionLidLetterAndSpike(map);
        }

        /// <summary>Every inmate's suppression and tension, and the wings' letter ticks, put back by the rollback.</summary>
        private static void SuppSetAside(Map map, Rollback r)
        {
            PrisonGame g = PrisonGame.Instance;
            List<(CompInmate c, float suppressed, float tension)> saved = map.mapPawns.PrisonersOfColony.Select(p => p.Inmate()).Where(c => c != null).Select(c => (c, c.suppressed, c.tension)).ToList();
            var lid0 = new Dictionary<SecurityLevelDef, int>(g.lidTicks);
            r.Add(() =>
            {
                foreach ((CompInmate c, float suppressed, float tension) in saved) (c.suppressed, c.tension) = (suppressed, tension);
                g.lidTicks.Clear();
                foreach (KeyValuePair<SecurityLevelDef, int> kv in lid0) g.lidTicks[kv.Key] = kv.Value;
            });
            g.lidTicks.Clear();
        }

        private static IEnumerable<CompInmate> SuppInmates(Map map) => map.mapPawns.PrisonersOfColony.Select(p => p.Inmate()).Where(c => c != null);

        private static void SuppSetAll(Map map, float suppressed, float tension)
        {
            foreach (CompInmate c in SuppInmates(map)) (c.suppressed, c.tension) = (suppressed, tension);
        }

        // ---- The wing table and the weekly report -----------------------------------------------------------------------------------------------------------------------------------

        private static void CheckSuppressionWingsAndReport(Map map)
        {
            Guarded("suppression: the wing table and the weekly report count who is held down", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                SuppSetAside(map, r);
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                SuppSetAll(map, 10f, 20f);
                // Held is above the line: 80 and 70 are, 50 is not.
                Pawn[] made = { Throwaway(map, r, level), Throwaway(map, r, level), Throwaway(map, r, level) };
                (made[0].Inmate().suppressed, made[1].Inmate().suppressed, made[2].Inmate().suppressed) = (80f, 70f, Suppression.Line);
                DeskBoard.WingRow row = DeskBoard.Compute(g, map).wings.FirstOrDefault(w => w.level == level);
                if (row == null || row.inmates < 3)
                {
                    SuppFail("wing table", $"the wing table has {(row == null ? "no row" : row.inmates + " inmates")} for the level the check put three inmates in");
                    return;
                }
                float expected = (200f + 10f * (row.inmates - 3)) / row.inmates;
                if (row.shut != 2 || Mathf.Abs(row.meanSuppressed - expected) > 0.01f)
                    SuppFail("wing table", $"three inmates at 80, 70 and 50 among {row.inmates - 3} at 10 give {row.shut} held and a mean of {row.meanSuppressed:F2} (wanted 2 held, mean {expected:F2})");
                // Other levels' rows count their own: none of the check's inmates are in them.
                foreach (DeskBoard.WingRow other in DeskBoard.Compute(g, map).wings.Where(w => w.level != level))
                    if (other.shut != 0) SuppFail("wing table", $"the {other.level.defName} row counts {other.shut} held down with every inmate but three at 10");
                string wings = Suppression.HeldByWing(DeskBoard.Compute(g, map).wings, out int held);
                if (held != 2 || wings == null || !wings.Contains(level.LabelCap)) SuppFail("wing table", $"the held-down count by wing says {held} in \"{wings}\" (wanted 2, naming {level.LabelCap})");
                // The weekly report: a line with the count and the wing, and none when nobody is held.
                WeekSnapshot week0 = g.week;
                int weeks0 = g.weeksReported;
                r.Add(() => (g.week, g.weeksReported) = (week0, weeks0));
                g.week = WeekSnapshot.Take(g, map);
                WeeklyReport.Send(g, map);
                string report = (r.NewLetters.LastOrDefault() as ChoiceLetter)?.Text.ToString() ?? "";
                string line = Suppression.ThumbLine(held, wings);
                if (!report.Contains(line)) SuppFail("weekly report", $"the weekly report has no under-the-thumb line \"{line}\" with two held down: {report}");
                // One held down reads in the singular.
                string one = Suppression.ThumbLine(1, wings);
                if (one.Contains("1 inmates") || one != "RP_Week_ThumbOne".Translate(1, wings, Suppression.Line.ToString("F0")).Resolve()) SuppFail("weekly report", $"one held down reads \"{one}\" (wanted the singular line)");
                SuppSetAll(map, 0f, 20f);
                g.week = WeekSnapshot.Take(g, map);
                WeeklyReport.Send(g, map);
                string quiet = (r.NewLetters.LastOrDefault() as ChoiceLetter)?.Text.ToString() ?? "";
                string head = "RP_Week_Thumb".Translate(0, "", "").Resolve().Split(new[] { " - " }, System.StringSplitOptions.None)[0];
                if (quiet.Contains(head)) SuppFail("weekly report", $"the weekly report has an under-the-thumb line with nobody held down: {quiet}");
                RPLog.Message($"[RimPrison:test] suppression: the {level.defName} row counts {row.shut} held of {row.inmates} (mean suppression {row.meanSuppressed:F1}), and the weekly report says \"{line}\"");
            });
        }

        // ---- The Programs screen ---------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckSuppressionProgramsBlocker(Map map)
        {
            Guarded("suppression: the Programs screen names suppression when it is why a class has nobody", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                SuppSetAside(map, r);
                ProgramDef prog = DefDatabase<ProgramDef>.AllDefsListForReading.FirstOrDefault(p => p.everyone && p.requiresProgram == null && !p.religiousOnly && p.examSkill == null && g.programs.IsRunning(p) && ProgramSystem.SessionFor(pm, p) == null);
                if (prog == null)
                {
                    RPLog.Message("[RimPrison:test] suppression: no program for everyone with no session running, the Programs blocker is not checked");
                    return;
                }
                Pawn inmate = Throwaway(map, r);
                SuppSetAll(map, 0f, 20f);
                if (!prog.Eligible(inmate, inmate.Inmate()) || prog.ShutOut(inmate, inmate.Inmate()))
                {
                    RPLog.Message($"[RimPrison:test] suppression: a new inmate is not eligible for {prog.defName} with a clean file, the Programs blocker is not checked");
                    return;
                }
                // Held down: not eligible, and the class is short of them (a pass over everyone, the Programs screen's own counts).
                SuppSetAll(map, 80f, 20f);
                ProgramSystem.Count(map, prog, out int eligible, out _, out int referred);
                int shut = ProgramSystem.ShutOutCount(map, prog);
                if (prog.Eligible(inmate, inmate.Inmate()) || !prog.ShutOut(inmate, inmate.Inmate()) || eligible != 0 || shut < 1)
                    SuppFail("programs", $"with every inmate at 80 suppression the new inmate is eligible {prog.Eligible(inmate, inmate.Inmate())}, shut out {prog.ShutOut(inmate, inmate.Inmate())}, {eligible} are eligible and {shut} are shut out of {prog.defName}");
                string status = ProgramSystem.Status(map, pm, g, prog, eligible, referred, out ProgramStatusKind kind);
                string wanted = "RP_ProgramStatusSuppressed".Translate(shut, Suppression.Line.ToString("F0")).Resolve();
                if (status != wanted || kind != ProgramStatusKind.Blocked) SuppFail("programs", $"the status of {prog.defName} with nobody eligible and {shut} held down is \"{status}\" ({kind}), wanted \"{wanted}\" as a blocker");
                // Nobody held and nobody eligible (a class nobody could take) is still the old line.
                SuppSetAll(map, 0f, 20f);
                ProgramSystem.Count(map, prog, out eligible, out _, out referred);
                if (ProgramSystem.ShutOutCount(map, prog) != 0 || !prog.Eligible(inmate, inmate.Inmate()) || eligible < 1)
                    SuppFail("programs", $"with nobody held down {ProgramSystem.ShutOutCount(map, prog)} are shut out and {eligible} eligible for {prog.defName}");
                string open = ProgramSystem.Status(map, pm, g, prog, eligible, referred, out _);
                if (open == wanted) SuppFail("programs", "with nobody held down the status of a class that has its inmates still names suppression");
                // One student and others held down: the status names suppression; with nobody held it is the plain "a class needs two".
                Pawn second = Throwaway(map, r);
                SuppSetAll(map, 80f, 20f);
                inmate.Inmate().suppressed = 0f;
                ProgramSystem.Count(map, prog, out eligible, out _, out referred);
                shut = ProgramSystem.ShutOutCount(map, prog);
                if (eligible != 1 || referred != 0 || shut < 1 || !Suppression.Held(second.Inmate()))
                    SuppFail("programs", $"one inmate at 0 among others at 80: {eligible} eligible, {referred} referred, {shut} shut out of {prog.defName} (wanted one, none, at least one)");
                string oneWanted = "RP_ProgramStatusSuppressedOne".Translate(shut, Suppression.Line.ToString("F0")).Resolve();
                string oneLine = ProgramSystem.OneStudentStatus(map, prog);
                if (oneLine != oneWanted) SuppFail("programs", $"the one-student line of {prog.defName} with {shut} held down is \"{oneLine}\", wanted \"{oneWanted}\"");
                // (Through the whole status when nothing else blocks the class: a room and a teacher.)
                if (ProgramSystem.Blocker(map, pm, prog) == ProgramBlock.None && ProgramSystem.Status(map, pm, g, prog, eligible, referred, out _) != oneWanted)
                    SuppFail("programs", $"the status of {prog.defName} with one eligible and {shut} held down is not the suppression line");
                SuppSetAll(map, 0f, 20f);
                if (ProgramSystem.OneStudentStatus(map, prog) != "RP_ProgramStatusOne".Translate().Resolve()) SuppFail("programs", "with nobody held down the one-student line names suppression");
                string nobody = ProgramSystem.Status(map, pm, g, prog, 0, 0, out _);
                if (nobody != "RP_ProgramStatusNobody".Translate().Resolve()) SuppFail("programs", $"with nobody eligible and nobody held down the status is \"{nobody}\", the old line was wanted");
                RPLog.Message($"[RimPrison:test] suppression: the Programs screen says \"{wanted}\" for {prog.defName} when its inmates are held down, and the old line otherwise");
            });
        }

        // ---- The policy row's tooltip -----------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The text without its colour tags (Translate colours its arguments, so the same words can carry them differently).</summary>
        private static string Plain(string text) => ColoredText.StripTags(text ?? "");

        private static void CheckSuppressionPolicyTip(Map map)
        {
            Guarded("suppression: the policy row's tooltip gives the hours of each sanction", map, r =>
            {
                // The hole at 2 an hour: 24 hours (48, under the line), 48 hours (96 at the start, 86 at the end: clear 8 days after the sentence), 72 (capped at 100, 85 at the end: 7 days).
                string day = Plain(Suppression.SanctionTip(Sanction.Solitary, 24));
                string two = Plain(Suppression.SanctionTip(Sanction.Solitary, 48));
                string three = Plain(Suppression.SanctionTip(Sanction.Solitary, 72));
                string cell = Plain(Suppression.SanctionTip(Sanction.CellConfinement, 48));
                if (!day.Contains(Plain("RP_Supp_TipUnder".Translate(24, "48", "50").Resolve())) || day.Contains("clear in")) SuppFail("policy tooltip", $"24 hours of solitary says \"{day}\" (wanted 48 on a clean file, under the line, no date)");
                if (!two.Contains(Plain("RP_Supp_TipOver".Translate(48, "96", "50", "RP_Supp_ClearDays".Translate(8)).Resolve()))) SuppFail("policy tooltip", $"48 hours of solitary says \"{two}\" (wanted 96, over the line, clear in 8 days after the sentence)");
                if (!three.Contains(Plain("RP_Supp_TipOver".Translate(72, "100", "50", "RP_Supp_ClearDays".Translate(7)).Resolve()))) SuppFail("policy tooltip", $"72 hours of solitary says \"{three}\" (wanted 100 at most, clear in 7 days after the sentence)");
                if (!cell.Contains(Plain("RP_Supp_TipUnder".Translate(48, "24", "50").Resolve()))) SuppFail("policy tooltip", $"48 hours of cell confinement says \"{cell}\" (wanted 24: half an hour's worth, under the line)");
                if (!two.Contains(Plain("RP_Supp_TipRateSolitary".Translate("2", "1").Resolve())) || !cell.Contains(Plain("RP_Supp_TipRate".Translate("0.5").Resolve()))) SuppFail("policy tooltip", "the tooltips do not give the suppression an hour (2 in the hole, 1 in a cell for want of a bed, 0.5 for a confinement)");
                foreach (Sanction kind in new[] { Sanction.LoseYard, Sanction.LoseVisits, Sanction.LoseCommissary, Sanction.Warning })
                    if (Suppression.SanctionTip(kind, 48) != null) SuppFail("policy tooltip", $"{kind} gives a suppression tooltip though it does not suppress");
                // What the tooltip says is what happens: the constants it reads are the ones the sentence and the confinement add.
                if (Suppression.PerHour(Sanction.Solitary) != Suppression.SolitaryPerHour || Suppression.PerHour(Sanction.CellConfinement) != Sanctions.ConfinementSuppression)
                    SuppFail("policy tooltip", "the tooltip's rates are not the sentence's and the confinement's");
                // The date is true: serve the hours on a clean file (all of it goes on at the start, the hourly pass wears it off through the hours) and the file's own date is the tooltip's.
                SuppSetAside(map, r);
                Pawn served = Throwaway(map, r);
                CompInmate sc = served.Inmate();
                PrisonMap pm = PrisonMap.Of(map);
                foreach (int hours in new[] { 36, 48, 72 })
                {
                    sc.suppressed = Mathf.Min(100f, hours * Suppression.SolitaryPerHour);
                    for (int h = 0; h < hours; h++) TensionUtility.HourlyUpdate(served, sc, pm, 0f);
                    string wanted = Plain("RP_Supp_TipOver".Translate(hours, Mathf.Min(100f, hours * Suppression.SolitaryPerHour).ToString("F0"), "50", Suppression.ClearText(sc.suppressed)).Resolve());
                    if (!Plain(Suppression.SanctionTip(Sanction.Solitary, hours)).Contains(wanted)) SuppFail("policy tooltip", $"{hours} hours of solitary served leave {sc.suppressed:F1} ({Suppression.ClearText(sc.suppressed)} from then); the tooltip says \"{Suppression.SanctionTip(Sanction.Solitary, hours)}\"");
                }
                RPLog.Message("[RimPrison:test] suppression: the policy tooltip gives 2 an hour for the hole and 0.5 for a confinement, what the hours come to and the days to clear, and nothing for the sanctions that do not suppress");
            });
        }

        // ---- The file's date --------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckSuppressionFileDate(Map map)
        {
            Guarded("suppression: the file's bar reads as a date", map, r =>
            {
                SuppSetAside(map, r);
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                PrisonMap pm = PrisonMap.Of(map);
                // 70 is four days from the line (5 a day), 72 five, 51 one; at the line or under it the bar carries no date.
                (float suppressed, int days)[] table = { (70f, 4), (72f, 5), (51f, 1), (100f, 10), (50.2f, 1), (55f, 1), (55.5f, 2) };
                foreach ((float suppressed, int days) in table)
                {
                    c.suppressed = suppressed;
                    string wanted = "RP_FileSuppressedBarDated".Translate(Mathf.Ceil(suppressed).ToString("F0"), (days == 1 ? "RP_Supp_ClearDay" : "RP_Supp_ClearDays").Translate(days)).Resolve();
                    if (Suppression.BarText(c) != wanted || Suppression.ClearDays(suppressed) != days) SuppFail("file", $"suppression {suppressed} reads \"{Suppression.BarText(c)}\" ({Suppression.ClearDays(suppressed)} days), wanted \"{wanted}\"");
                }
                foreach (float quiet in new[] { 50f, 30f, 0f })
                {
                    c.suppressed = quiet;
                    if (Suppression.BarText(c) != "RP_FileSuppressedBar".Translate(quiet.ToString("F0")).Resolve() || Suppression.ClearText(quiet) != null) SuppFail("file", $"suppression {quiet} reads \"{Suppression.BarText(c)}\" (wanted the bar with no date)");
                }
                // Just over the line does not read as the line: 50.2 shows as 51 (it is held), not as a "suppressed 50 - clear in 1 day".
                c.suppressed = 50.2f;
                if (Suppression.BarText(c).Contains("suppressed 50 ")) SuppFail("file", $"suppression 50.2 reads \"{Suppression.BarText(c)}\" (a held file that reads as the line)");
                // (The day's fall is the hourly pass's rate times 24 by definition; the pass below is what takes it off.)
                // The date is true: a day of the game's own hourly pass takes off what the date counts a day as (one pass at the file's own rate, 24 of them).
                c.suppressed = 72f;
                for (int h = 0; h < 24; h++) TensionUtility.HourlyUpdate(p, c, pm, 0f);
                if (Mathf.Abs(72f - c.suppressed - Suppression.FallPerDay) > 0.01f) SuppFail("file", $"a day of hourly passes took {72f - c.suppressed:F3} off 72 (the date counts {Suppression.FallPerDay:F3} a day)");
                // The file's status line (above the line) is still there.
                c.suppressed = 72f;
                if (!ITab_InmateFile.StatusLines(p, c, Find.TickManager.TicksGame).Any(l => l.Item1 == "RP_FileSuppressed".Translate("72").Resolve())) SuppFail("file", "the file has lost its suppressed status line above 50");
                RPLog.Message("[RimPrison:test] suppression: the file's bar reads \"suppressed 70 - clear in 4 days\" (5 a day, the pass's own rate), and no date at 50 or under");
            });
        }

        // ---- The lid comes off ----------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The rule as a table: three inmates at least, a quarter of them (two at least) falling through the line within the day, mean tension over 60.</summary>
        private static void CheckSuppressionLidRule(Map map)
        {
            SecurityLevelDef level = InmateUtility.DefaultSecurity;
            (int inmates, int clearing, float tension, bool lid)[] table =
            {
                (3, 2, 70f, true),
                (3, 3, 70f, true),
                (8, 2, 60.5f, true),     // exactly a quarter
                (8, 3, 70f, true),
                (20, 5, 70f, true),
                (20, 4, 70f, false),     // under a quarter
                (9, 2, 70f, false),      // 2 of 9: under a quarter (2.25)
                (8, 1, 70f, false),      // one is not enough, though it is over a quarter of four
                (4, 1, 90f, false),
                (3, 0, 90f, false),      // nobody about to cross (a wing held steady, or held for long yet)
                (3, 2, 60f, false),      // tension not over 60
                (3, 3, 30f, false),
                (2, 2, 70f, false),      // two cannot riot
            };
            foreach ((int inmates, int clearing, float tension, bool lid) in table)
            {
                var row = new DeskBoard.WingRow { level = level, inmates = inmates, clearing = clearing, meanTension = tension };
                if (Suppression.LidComing(row) != lid) SuppFail("lid rule", $"{inmates} inmates with {clearing} clearing and mean tension {tension}: the lid is {(lid ? "not " : "")}coming, wanted the other");
            }
            // Who is clearing: held, and under the line within the day (50 is not held, 55 is clearing, 55.5 is not).
            var c = new CompInmate();
            foreach ((float suppressed, bool clearing) in new[] { (0f, false), (50f, false), (50.1f, true), (55f, true), (55.5f, false), (100f, false) })
            {
                c.suppressed = suppressed;
                if (Suppression.Clearing(c) != clearing) SuppFail("lid rule", $"an inmate at suppression {suppressed} is {(clearing ? "not " : "")}clearing, wanted the other");
            }
        }

        /// <summary>A wing of three inmates, every other inmate quiet (no suppression, tension 20), the wing's own at a mean suppression of 53 and tension 80.</summary>
        private static (SecurityLevelDef level, Pawn[] inmates) SuppLidWing(Map map, Rollback r)
        {
            SuppSetAside(map, r);
            SecurityLevelDef level = InmateUtility.DefaultSecurity;
            Pawn[] made = { Throwaway(map, r, level), Throwaway(map, r, level), Throwaway(map, r, level) };
            SuppSetAll(map, 0f, 20f);
            foreach (CompInmate c in SuppInmates(map).Where(c => c.security == level)) (c.suppressed, c.tension) = (53f, 80f);
            return (level, made);
        }

        private static IEnumerable<ChoiceLetter_PrisonDecision> SuppLidLetters(Rollback r, SecurityLevelDef level) =>
            r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_LidComesOff") && l.Label.Resolve() == "RP_LidLabel".Translate(level.LabelCap).Resolve());

        private static void CheckSuppressionLidLetterAndSpike(Map map)
        {
            Guarded("suppression: the lid letter a day ahead, the riot-odds spike for a day, once in five days", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                (SecurityLevelDef level, Pawn[] made) = SuppLidWing(map, r);
                int now = Find.TickManager.TicksGame;
                DeskBoard.WingRow row = DeskBoard.Compute(g, map).wings.FirstOrDefault(w => w.level == level);
                if (row == null || !Suppression.LidComing(row))
                {
                    SuppFail("lid letter", $"the wing the check built (suppression 53 and tension 80 for all, {made.Length}+ inmates) is not a lid coming: {(row == null ? "no row" : $"{row.inmates} inmates, {row.clearing} clearing, tension {row.meanTension:F1}")}");
                    return;
                }
                // Tension not over 60, suppression far from the line: no letter, nothing saved.
                foreach ((float suppressed, float tension, string why) in new[] { (53f, 55f, "tension 55"), (53f, 60f, "tension exactly 60"), (80f, 80f, "suppression 80"), (50f, 80f, "suppression exactly 50"), (30f, 80f, "suppression 30"), (55.5f, 80f, "suppression 55.5 (more than a day from the line)") })
                {
                    foreach (CompInmate c in SuppInmates(map).Where(c => c.security == level)) (c.suppressed, c.tension) = (suppressed, tension);
                    if (Suppression.Daily(g, map) != 0 || SuppLidLetters(r, level).Any() || g.lidTicks.Count != 0) SuppFail("lid letter", $"a wing with {why} got a letter or a date ({SuppLidLetters(r, level).Count()} letters, {g.lidTicks.Count} dates)");
                }
                // Bimodal: one at 100 and the rest at 30 - a mean that could pass for a lid, and nobody about to cross the line.
                foreach (CompInmate c in SuppInmates(map).Where(c => c.security == level)) (c.suppressed, c.tension) = (30f, 80f);
                made[0].Inmate().suppressed = 100f;
                if (DeskBoard.Compute(g, map).wings.FirstOrDefault(w => w.level == level) is { clearing: > 0 }) SuppFail("lid letter", "a wing with one inmate at 100 and the rest at 30 has inmates falling through the line within the day");
                if (Suppression.Daily(g, map) != 0 || SuppLidLetters(r, level).Any() || g.lidTicks.Count != 0) SuppFail("lid letter", "a wing with one inmate at 100 and the rest at 30 got a letter or a date");
                foreach (CompInmate c in SuppInmates(map).Where(c => c.security == level)) (c.suppressed, c.tension) = (53f, 80f);
                // The letter: one, for the wing, a decision letter that is not urgent, with the day it warns of in hand.
                int sent = Suppression.Daily(g, map);
                List<ChoiceLetter_PrisonDecision> letters = SuppLidLetters(r, level).ToList();
                if (sent != 1 || letters.Count != 1 || !g.lidTicks.TryGetValue(level, out int at) || at != now)
                {
                    SuppFail("lid letter", $"a wing at suppression 53 and tension 80 got {sent} letter(s) ({letters.Count} for the wing) and date {(g.lidTicks.TryGetValue(level, out int t) ? t : -1)} (wanted one, dated {now})");
                    return;
                }
                ChoiceLetter_PrisonDecision letter = letters[0];
                if (StoryLetters.Urgent(letter)) SuppFail("lid letter", "the lid letter is urgent (a budgeted warning with a day in hand was wanted)");
                string letterText = Suppression.LetterText(map, row, now);
                string spikeDay = GenDate.DateFullStringAt(GenDate.TickGameToAbs(now + Suppression.LidWarnTicks), Find.WorldGrid.LongLatOf(map.Tile));
                if (letter.Text.Resolve() != letterText || !letterText.Contains(level.LabelCap) || !letterText.Contains(spikeDay) || letterText.Contains("RP_LidText"))
                    SuppFail("lid letter", $"the lid letter reads \"{letter.Text}\" (wanted the translated text naming the wing and the day the spike begins, {spikeDay})");
                if (letter.Text.Resolve().Contains("tomorrow")) SuppFail("lid letter", "the lid letter promises \"tomorrow\" (a budgeted letter may arrive later)");
                if (letter.lookTargets == null || !letter.lookTargets.IsValid) SuppFail("lid letter", "the lid letter has no look targets (the inmates falling through the line)");
                if (letter.Choices.Count() < 2) SuppFail("lid letter", "the lid letter cannot be read (no answer to give)");
                // Not twice in five days: nothing at once, nothing at 4 days 23 hours, another from the fifth day.
                if (Suppression.Daily(g, map) != 0) SuppFail("lid letter", "a second letter for the same wing the same day");
                g.lidTicks[level] = now - Suppression.LidGapDays * GenDate.TicksPerDay + GenDate.TicksPerHour;
                if (Suppression.Daily(g, map) != 0) SuppFail("lid letter", "a second letter for the wing within five days");
                g.lidTicks[level] = now - Suppression.LidGapDays * GenDate.TicksPerDay;
                if (Suppression.Daily(g, map) != 1 || g.lidTicks[level] != now) SuppFail("lid letter", "no letter for the wing five days after the last");
                // Budgeted: with the day's budget spent the letter waits for tomorrow (and is not on the desk).
                StorySystem story = g.story;
                int decisions0 = story.decisionsToday;
                bool testBudget0 = StoryLetters.TestBudget;
                List<ChoiceLetter_PrisonDecision> deferred0 = story.deferredLetters.ToList();
                r.Add(() =>
                {
                    StoryLetters.TestBudget = testBudget0;
                    story.decisionsToday = decisions0;
                    story.deferredLetters.Clear();
                    story.deferredLetters.AddRange(deferred0);
                });
                int onDesk = SuppLidLetters(r, level).Count();
                StoryLetters.TestBudget = true;
                story.decisionsToday = 999;
                g.lidTicks.Clear();
                Suppression.Daily(g, map);
                if (SuppLidLetters(r, level).Count() != onDesk || !story.deferredLetters.Any(d => d.Is("RP_Decision_LidComesOff") && !deferred0.Contains(d)))
                    SuppFail("lid letter", "with the day's budget of letters spent the lid letter did not wait for tomorrow");
                StoryLetters.TestBudget = testBudget0;
                story.decisionsToday = decisions0;
                // The spike: from a day after the letter, for a day; for an inmate the lid is off for (under the line), in that wing only.
                g.lidTicks.Clear();
                CompInmate inWing = made[0].Inmate();
                Pawn elsewhere = Throwaway(map, r, DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(s => s != level && s.uiOrder != level.uiOrder));
                inWing.suppressed = 10f;
                float Factor(int lidAt, CompInmate c)
                {
                    g.lidTicks[level] = lidAt;
                    return Suppression.RiotFactor(c);
                }
                const int Day = GenDate.TicksPerDay;
                if (Suppression.RiotFactor(inWing) != 1f) SuppFail("spike", "a wing with no letter has riot odds changed");
                (int sentAt, float wanted, string why)[] windows =
                {
                    (now, 1f, "the day of the letter"),
                    (now - Day + 1, 1f, "a tick before the day after"),
                    (now - Day, Suppression.RiotOddsFactor, "the first tick of the day after the letter"),
                    (now - 2 * Day + 1, Suppression.RiotOddsFactor, "the last tick of that day"),
                    (now - 2 * Day, 1f, "the tick after that day"),
                    (now - 4 * Day, 1f, "days after"),
                };
                foreach ((int sentAt, float wanted, string why) in windows)
                    if (Factor(sentAt, inWing) != wanted) SuppFail("spike", $"{why}: the wing's riot odds are x{Factor(sentAt, inWing)} (wanted x{wanted})");
                // Only for those the lid is off for, and only in their wing.
                g.lidTicks[level] = now - Day;
                inWing.suppressed = 60f;
                if (Suppression.RiotFactor(inWing) != 1f) SuppFail("spike", "an inmate still held down (60) has riot odds changed in the wing's day");
                inWing.suppressed = Suppression.Line;
                if (Suppression.RiotFactor(inWing) != Suppression.RiotOddsFactor) SuppFail("spike", "an inmate at the line (50: not held) has no spike in the wing's day");
                if (elsewhere != null && elsewhere.Inmate().security != level && Suppression.RiotFactor(elsewhere.Inmate()) != 1f) SuppFail("spike", "another wing's inmate has riot odds changed in the wing's day");
                // The weight a riot has in the picker is the factor times what it had (the three are tense enough and together: a riot is possible).
                inWing.suppressed = 10f;
                foreach (Pawn m in made) m.Inmate().tension = 80f;
                g.lidTicks.Clear();
                MisconductUtility.BuildCandidates(made[0], inWing, RPRiotDefOf.RP_Riot);
                float plain = MisconductUtility.CandidateWeight(RPRiotDefOf.RP_Riot);
                g.lidTicks[level] = now - Day;
                MisconductUtility.BuildCandidates(made[0], inWing, RPRiotDefOf.RP_Riot);
                float spiked = MisconductUtility.CandidateWeight(RPRiotDefOf.RP_Riot);
                if (plain <= 0f) RPLog.Message("[RimPrison:test] suppression: a riot is not possible among the three throwaways (no weight), the riot's weight in the picker is not checked");
                else if (Mathf.Abs(spiked - plain * Suppression.RiotOddsFactor) > 0.001f) SuppFail("spike", $"a riot weighs {plain:F2} without the spike and {spiked:F2} with it (wanted x{Suppression.RiotOddsFactor})");
                RPLog.Message($"[RimPrison:test] suppression: the lid letter is sent a day ahead, not urgent and budgeted, once in {Suppression.LidGapDays} days; the wing's riot odds are x{Suppression.RiotOddsFactor} for the day after it (a riot's weight {plain:F2} -> {spiked:F2}), for those the lid is off for");
            });
        }

        // ---- The saveload plan -----------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The wings' letter ticks as one comparable string.</summary>
        private static string SuppressionDescribe(PrisonGame g) => string.Join(",", g.lidTicks.OrderBy(kv => kv.Key.defName).Select(kv => $"{kv.Key.defName}:{kv.Value}"));

        private static readonly List<SecurityLevelDef> suppressionSeeded = new List<SecurityLevelDef>();
        private static Dictionary<SecurityLevelDef, int> suppressionLid0;

        /// <summary>Saveload plan, before the snapshot: two wings with a letter tick that is not the default (-), neither a round number. <see cref="CheckSuppressionAfterReload"/> puts them back.</summary>
        private static void SeedSuppressionForReload()
        {
            PrisonGame g = PrisonGame.Instance;
            suppressionLid0 = new Dictionary<SecurityLevelDef, int>(g.lidTicks);
            int now = Find.TickManager.TicksGame;
            int offset = 1234;
            foreach (SecurityLevelDef level in DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(s => s.uiOrder).Take(2))
            {
                g.lidTicks[level] = now - offset;
                offset += 4321;
                suppressionSeeded.Add(level);
            }
        }

        /// <summary>After the reload: the ticks came back whole (the snapshot compared them), and are put back as the game had them.</summary>
        private static void CheckSuppressionAfterReload()
        {
            PrisonGame g = PrisonGame.Instance;
            if (suppressionLid0 == null) return;
            if (suppressionSeeded.Count == 0 || suppressionSeeded.Any(l => !g.lidTicks.ContainsKey(l))) reloadProblems.Add($"saveload: the wings' lid ticks did not come back from the save ({SuppressionDescribe(g)})");
            else RPLog.Message("[RimPrison:test] saveload suppression: the wings' last lid letters are intact");
            g.lidTicks.Clear();
            foreach (KeyValuePair<SecurityLevelDef, int> kv in suppressionLid0) g.lidTicks[kv.Key] = kv.Value;
            suppressionSeeded.Clear();
            suppressionLid0 = null;
        }
    }
}
