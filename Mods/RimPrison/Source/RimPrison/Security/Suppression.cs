using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Suppression made legible. What an inmate has been beaten down by (<see cref="CompInmate.suppressed"/>: the hole, a long confinement, the warden's firm word) costs them programs, riots and recovery above
    /// <see cref="Line"/>; this is where the desk reads it: how many are held down per wing, when a file clears, what a sanction's hours add, and "the lid comes off" - a wing where a quarter of the inmates (two at least) are about to
    /// fall back under the line, with tension high, is warned of a day ahead and has twice the riot odds for the day after. The wing numbers are counted from the inmates (<see cref="DeskBoard.Compute"/>); the one thing kept
    /// is the tick of a wing's last letter (<see cref="PrisonGame.lidTicks"/>), which is both the five-day gap and the date the spike runs from.
    /// </summary>
    public static class Suppression
    {
        /// <summary>Above this an inmate cannot take a program or join a riot and calms at half speed.</summary>
        public const float Line = 50f;

        /// <summary>What an hour served adds: the hole, the cell when there is no solitary bed (see <see cref="PunishmentUtility.Begin"/>), and a confinement sanction (<see cref="Sanctions.ConfinementSuppression"/>).</summary>
        public const float SolitaryPerHour = 2f, LockupPerHour = 1f;

        /// <summary>A wing whose mean tension is over this, and in which at least <see cref="LidShare"/> of the inmates (and <see cref="LidMinClearing"/> of them) are <see cref="Clearing"/>, is a lid about to come off.</summary>
        public const float LidTension = 60f;
        public const float LidShare = 0.25f;
        public const int LidMinClearing = 2;

        /// <summary>Fewer than this many inmates in a wing cannot riot (a riot needs three).</summary>
        public const int LidMinInmates = 3;

        /// <summary>The day after the letter, the wing's riot odds are this many times what they were, for a day; a wing is warned at most once in <see cref="LidGapDays"/> days.</summary>
        public const float RiotOddsFactor = 2f;
        public const int LidGapDays = 5;

        /// <summary>The ticks from the letter to the first tick of the spike, and how long it lasts.</summary>
        public const int LidWarnTicks = GenDate.TicksPerDay, LidSpikeTicks = GenDate.TicksPerDay;

        /// <summary>Suppression a day takes off (<see cref="TensionUtility.SuppressionDecayPerHour"/> an hour).</summary>
        public const float FallPerDay = TensionUtility.SuppressionDecayPerHour * 24f;

        /// <summary>The text without the colour Translate puts on its argument: the strings it goes into colour it once more, and a colour inside a colour reads as a doubled tag.</summary>
        private static string Plural(string one, string many, int n) => ColoredText.StripTags((n == 1 ? one : many).Translate(n).Resolve());

        public static bool Held(CompInmate c) => c.suppressed > Line;

        /// <summary>Held, and under the line within the day if nothing adds to it (the ones whose lid is about to come off).</summary>
        public static bool Clearing(CompInmate c) => Held(c) && c.suppressed <= Line + FallPerDay;

        /// <summary>Whole days until the inmate is back under <see cref="Line"/> if nothing adds to it (0: they are under it).</summary>
        public static int ClearDays(float suppressed) => suppressed > Line ? Mathf.CeilToInt((suppressed - Line) / FallPerDay) : 0;

        /// <summary>"in 4 days" for a file above the line (null under it): the date on the file's bar.</summary>
        public static string ClearText(float suppressed) =>
            suppressed > Line ? Plural("RP_Supp_ClearDay", "RP_Supp_ClearDays", ClearDays(suppressed)) : null;

        /// <summary>The text of the file's suppression bar: the number, and above the line the day it clears (the number rounded up there, so 50.2 does not read as the line itself).</summary>
        public static string BarText(CompInmate c) =>
            ClearText(c.suppressed) is { } clear ? "RP_FileSuppressedBarDated".Translate(Mathf.Ceil(c.suppressed).ToString("F0"), clear).Resolve() : "RP_FileSuppressedBar".Translate(c.suppressed.ToString("F0")).Resolve();

        /// <summary>What an hour of the sanction adds to suppression (0 for the ones that do not suppress).</summary>
        public static float PerHour(Sanction kind) => kind switch
        {
            Sanction.Solitary => SolitaryPerHour,
            Sanction.CellConfinement => Sanctions.ConfinementSuppression,
            _ => 0f,
        };

        /// <summary>The policy row's tooltip tail: what these hours of the sanction add to a clean file and how long that takes to clear (null when the sanction does not suppress).</summary>
        public static string SanctionTip(Sanction kind, int hours)
        {
            float rate = PerHour(kind);
            if (rate <= 0f) return null;
            float added = Mathf.Min(100f, hours * rate);
            // (All of it goes on when the sentence begins and the file wears off through the hours served, so the date counts from the end.)
            float atEnd = added - hours * FallPerDay / 24f;
            string rates = kind == Sanction.Solitary ? "RP_Supp_TipRateSolitary".Translate(rate.ToString("0.#"), LockupPerHour.ToString("0.#")).Resolve() : "RP_Supp_TipRate".Translate(rate.ToString("0.#")).Resolve();
            string result = added > Line
                ? "RP_Supp_TipOver".Translate(hours, added.ToString("F0"), Line.ToString("F0"), ClearText(atEnd)).Resolve()
                : "RP_Supp_TipUnder".Translate(hours, added.ToString("F0"), Line.ToString("F0")).Resolve();
            return rates + " " + result + " " + "RP_Supp_TipEffects".Translate(Line.ToString("F0"), FallPerDay.ToString("0")).Resolve();
        }

        // ---- The wings ---------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The wings with inmates held down: "Medium 4, Maximum 3" (null when none is).</summary>
        public static string HeldByWing(List<DeskBoard.WingRow> wings, out int total)
        {
            total = 0;
            var parts = new List<string>();
            for (int i = 0; i < wings.Count; i++)
            {
                if (wings[i].shut <= 0) continue;
                total += wings[i].shut;
                parts.Add("RP_Supp_WingCount".Translate(wings[i].level.LabelCap, wings[i].shut).Resolve());
            }
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        /// <summary>The weekly report's line: how many are held down and where.</summary>
        public static string ThumbLine(int held, string wings) =>
            (held == 1 ? "RP_Week_ThumbOne" : "RP_Week_Thumb").Translate(held, wings, Line.ToString("F0")).Resolve();

        // ---- The lid comes off -------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Whether a wing's reading is a lid about to come off: enough inmates to riot, a quarter of them (two at least) falling through the line within the day, and the wing tense.</summary>
        public static bool LidComing(DeskBoard.WingRow w) =>
            w.inmates >= LidMinInmates && w.clearing >= LidMinClearing && w.clearing >= LidShare * w.inmates && w.meanTension > LidTension;

        /// <summary>Whether the wing may be written to now: not in the last <see cref="LidGapDays"/> days.</summary>
        public static bool LidFree(PrisonGame game, SecurityLevelDef level, int now) =>
            !game.lidTicks.TryGetValue(level, out int last) || now - last >= LidGapDays * GenDate.TicksPerDay;

        /// <summary>Whether the wing is in the day its letter promised (one day after the letter, for a day).</summary>
        public static bool LidActive(PrisonGame game, SecurityLevelDef level, int now) =>
            level != null && game.lidTicks.Count > 0 && game.lidTicks.TryGetValue(level, out int sent) && now >= sent + LidWarnTicks && now < sent + LidWarnTicks + LidSpikeTicks;

        /// <summary>
        /// What a riot's weight and a rioter's pull are multiplied by for this inmate now: <see cref="RiotOddsFactor"/> in a wing in its day, for one the lid is off (not under the line), else 1; and the wing's own factor
        /// after a riot was answered (a restriction holds it down, an amnesty makes the next one likelier: <see cref="RiotReview.WingFactor"/>). A few lookups.
        /// </summary>
        public static float RiotFactor(CompInmate c)
        {
            if (c.security == null || PrisonGame.Instance is not { } game) return 1f;
            float wing = RiotReview.WingFactor(game, c.security);
            if (c.suppressed > Line) return wing;
            return LidActive(game, c.security, Find.TickManager.TicksGame) ? RiotOddsFactor * wing : wing;
        }

        /// <summary>Midnight: a wing whose lid is about to come off gets the letter, once in <see cref="LidGapDays"/> days. Returns how many were sent.</summary>
        public static int Daily(PrisonGame game, Map map)
        {
            int sent = 0;
            int now = Find.TickManager.TicksGame;
            foreach (DeskBoard.WingRow w in DeskBoard.Compute(game, map).wings)
            {
                if (!LidComing(w) || !LidFree(game, w.level, now)) continue;
                Send(game, map, w, now);
                sent++;
            }
            return sent;
        }

        /// <summary>The letter's text: the wing, who is falling through the line, and the day the spike begins by the calendar (the budget may hold the letter back, so it does not say "tomorrow").</summary>
        public static string LetterText(Map map, DeskBoard.WingRow w, int sentTick) =>
            "RP_LidText".Translate(w.level.LabelCap, w.clearing, w.inmates, w.meanTension.ToString("F0"), Line.ToString("F0"), RiotOddsFactor.ToString("0.#"),
                GenDate.DateFullStringAt(GenDate.TickGameToAbs(sentTick + LidWarnTicks), Find.WorldGrid.LongLatOf(map.Tile))).Resolve();

        private static void Send(PrisonGame game, Map map, DeskBoard.WingRow w, int now)
        {
            game.lidTicks[w.level] = now;
            string wing = w.level.LabelCap;
            var clearing = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } c && c.security == w.level && Clearing(c)) clearing.Add(p);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_LidComesOff", "RP_LidLabel".Translate(wing), LetterText(map, w, now), map, new LookTargets(clearing));
            // (Not urgent: a warning with a day in hand, so a budgeted letter that waits its turn like the rest; it stands on the desk until the day it warned of begins.)
            letter.StartTimeout(LidWarnTicks);
            Find.LetterStack.ReceiveLetter(letter);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_LidComing".Translate(wing, w.clearing, w.inmates, w.meanTension.ToString("F0")));
        }
    }

    /// <summary>The warden's notice that a wing's lid is about to come off: one answer (read), which does nothing (the spike runs from the letter's date whatever is chosen).</summary>
    public class PrisonDecisionWorker_Notice : PrisonDecisionWorker
    {
        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Noted;

        private static readonly string[] Noted = { "RP_LidNoted" };
    }
}
