using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Conduct-based reclassification (policy, default on): every day, inmates with serious recent incidents (three
    /// fights, or one assault on staff or escape attempt, within three days, since their last move) are moved up a security level, and those with a clean record,
    /// low tension and a passed program are moved down a level. One step per move: the clocks restart, and a move only happens when there is a bed
    /// in the new wing. The
    /// state pays by level, so a promotion costs the prison money: good behaviour is rewarded at the warden's expense.
    /// </summary>
    public static class ReclassificationSystem
    {
        public const int DemoteWindowDays = 3;
        /// <summary>Weighted incidents (severity - 1 each: a fight 1, an assault on staff or an escape attempt 3).</summary>
        public const int DemoteAtScore = 3;

        /// <summary>
        /// The level this inmate's conduct moves them to today, or null: up in security (<paramref name="up"/>) after serious recent incidents, or one step down after a clean stretch. A patient of psychiatric care steps out
        /// to the level they came from (<see cref="PsychWing.StepOutLevel"/>), and only after clean days that are without a break too.
        /// </summary>
        public static SecurityLevelDef MoveFor(CompInmate c, int now, out bool up)
        {
            up = false;
            if (c.security == null || DeathRowSystem.Ext(c) != null) return null;
            int recent = 0, lastSevere = -999999;
            foreach (MisconductRecord r in c.records)
            {
                if (!r.seen || r.def.severity < 2) continue;
                // Incidents from before the last move were already acted on.
                if (now - r.tick < DemoteWindowDays * GenDate.TicksPerDay && r.tick > c.lastReclassTick) recent += r.def.severity - 1;
                if (r.tick > lastSevere) lastSevere = r.tick;
            }
            SecurityLevelDef sec = c.security;
            bool patient = PsychWing.IsIn(c);
            SecurityLevelDef stepDown = patient ? PsychWing.StepOutLevel(c) : sec.promoteTo;
            if (sec.demoteTo != null && recent >= DemoteAtScore && Unlocks.IsUnlocked(sec.demoteTo))
            {
                up = true;
                return sec.demoteTo;
            }
            if (stepDown != null && Unlocks.IsUnlocked(stepDown) && c.DaysServed >= sec.promoteAfterCleanDays
                && now - lastSevere >= sec.promoteAfterCleanDays * GenDate.TicksPerDay && now - c.lastReclassTick >= sec.promoteAfterCleanDays * GenDate.TicksPerDay
                && c.tension <= sec.promoteMaxTension
                && c.programsPassed >= sec.promotePrograms
                && !(patient && PsychWing.BrokeRecently(c)))
                return stepDown;
            return null;
        }

        public static void Daily(Map map, PrisonGame game)
        {
            if (!game.autoReclassify) return;
            var moves = new List<(Pawn p, SecurityLevelDef to, bool up)>();
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { activeFlag: true } c) continue;
                if (MoveFor(c, now, out bool up) is { } to) moves.Add((p, to, up));
            }
            int noRoom = 0;
            foreach ((Pawn p, SecurityLevelDef to, bool up) in moves)
            {
                // A move on paper only (no bed in the new wing) would mix regimes in a cell and pay the wrong rate.
                if (!InmateUtility.HasRoomIn(p, to))
                {
                    // (A wing kept for the other sex is told by name; the rest is counted.)
                    if (!InmateUtility.TellIfWingKeepsThemOut(p, to)) noRoom++;
                    continue;
                }
                bool fromPsych = PsychWing.IsIn(p.Inmate());
                InmateUtility.ChangeSecurity(p, p.Inmate(), to);
                if (up) game.stats.demoted++;
                else game.stats.promoted++;
                Messages.Message((up ? "RP_Demoted" : fromPsych ? "RP_PsychDischarged" : "RP_Promoted").Translate(p.LabelShort, to.label), p, up ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.PositiveEvent);
            }
            if (noRoom > 0) Messages.Message("RP_ReclassNoRoom".Translate(noRoom), MessageTypeDefOf.CautionInput);
        }
    }
}
