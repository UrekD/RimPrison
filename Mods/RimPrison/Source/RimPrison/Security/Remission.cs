using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Good behaviour remission (a policy, off by default): every ten clean days take a day off an inmate's sentence.
    /// With days at stake (three clean days or more) inmates think twice: misconduct is a little less likely. The cost is
    /// shorter stays, so fewer days of state payments. Not for lifers or death row. Only misconduct on the record (seen by
    /// staff or cameras, as at parole) breaks the streak: an administrative decision goes by what the prison knows.
    /// </summary>
    public static class Remission
    {
        public const int CleanDaysPerDayOff = 10, AtStakeDays = 3;
        public const float AtStakeMisconduct = 0.85f;

        public static bool Eligible(CompInmate c) => c?.security != null && c.activeFlag && !c.IsLifer && DeathRowSystem.Ext(c) == null;

        /// <summary>Multiplier on the hourly misconduct chance.</summary>
        public static float MisconductFactor(CompInmate c) =>
            PrisonGame.Instance is { remission: true } && Eligible(c) && c.cleanDays >= AtStakeDays ? AtStakeMisconduct : 1f;

        /// <summary>Midnight, before releases: count the clean day, take a day off every tenth (never the last day).</summary>
        public static void Daily(PrisonGame game, Map map)
        {
            if (!game.remission) return;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = p.Inmate();
                if (!Eligible(c)) continue;
                // Seen misconduct since the last count (a day the inmate was not counted does not hide it).
                int since = c.lastCleanCheckTick >= 0 ? c.lastCleanCheckTick : Find.TickManager.TicksGame - GenDate.TicksPerDay;
                c.lastCleanCheckTick = Find.TickManager.TicksGame;
                if (c.records.Any(rec => rec.seen && !rec.def.mark && rec.tick > since))
                {
                    c.cleanDays = 0;
                    continue;
                }
                c.cleanDays++;
                if (c.cleanDays % CleanDaysPerDayOff != 0 || c.TermDays - c.DaysServed <= 1f) continue;
                c.sentenceDays--;
                c.remissionEarned++;
                game.stats.remissionDays++;
            }
        }

        /// <summary>A new stay starts with a clean slate.</summary>
        public static void Clear(CompInmate c)
        {
            c.cleanDays = 0;
            c.remissionEarned = 0;
            c.lastCleanCheckTick = -1;
        }
    }
}
