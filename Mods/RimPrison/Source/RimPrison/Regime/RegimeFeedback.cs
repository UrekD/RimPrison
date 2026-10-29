using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>One level's day as the strip under its regime row shows it: for each hour the slot that ran, how many were in sample and what share of them stood where it wanted, the incidents and the meals missed. Bytes; not saved.</summary>
    public class RegimeDay
    {
        /// <summary>Per hour: the inmates in the sample (0: no numbers for that hour), the share of them in place (percent), the misconducts in the hour and the inmates who missed the meal (all clamped to a byte).</summary>
        public readonly byte[] sample = new byte[24], share = new byte[24], incidents = new byte[24], missed = new byte[24];

        /// <summary>The slot of each hour as it ran (the timetable with the day's orders on it).</summary>
        public readonly RegimeActivityDef[] ran = new RegimeActivityDef[24];

        public bool Has(int hour) => sample[hour] > 0;
    }

    /// <summary>
    /// What each slot of the regime did yesterday, the numbers behind the strip under each row of the Regime tab. The hour pass already walks the inmates for the compliance grade; once an hour, at the hour that ends a slot and
    /// before anyone is moved for the next one (<see cref="PrisonMap"/>'s hour pass, where the turf is sampled), this reads per level: the inmates in sample, how many stood where the slot wanted them (<see
    /// cref="RegimePlacement.InExpectedPlace"/>) and, for the last hour of a meal, how many hungry inmates had not eaten in it. Misconducts are noted as they are recorded (<see cref="NoteIncident"/>) and meals as they
    /// are eaten (<see cref="NoteMeal"/>). At the end of the day's last hour today's counts become yesterday's bytes (<see cref="Yesterday"/>) and a new day starts. Kept on the <see cref="PrisonMap"/>, not saved: a
    /// reload loses a day (the strip's tooltip says so for an hour it has no numbers for).
    /// </summary>
    public class RegimeFeedback
    {
        /// <summary>The last day's rows, by level (a level that had nobody in the sample has none).</summary>
        public Dictionary<SecurityLevelDef, RegimeDay> Yesterday = new Dictionary<SecurityLevelDef, RegimeDay>();

        /// <summary>The day in progress: counts per hour (inmates in sample, in place, incidents, missed) and the slot that ran.</summary>
        private sealed class Tally
        {
            public readonly int[] n = new int[24], ok = new int[24], incidents = new int[24], missed = new int[24];
            public readonly RegimeActivityDef[] ran = new RegimeActivityDef[24];
        }

        private readonly Dictionary<SecurityLevelDef, Tally> today = new Dictionary<SecurityLevelDef, Tally>();

        /// <summary>The tick each inmate (by thing id) last ate a meal, for the missed-meal count.</summary>
        private readonly Dictionary<int, int> ateTick = new Dictionary<int, int>();

        /// <summary>A check's stand-in for where inmates stand (null in a game): true for an inmate who is in place.</summary>
        internal static Func<Pawn, bool> TestPlaced;

        private Tally TallyOf(SecurityLevelDef level)
        {
            if (!today.TryGetValue(level, out Tally t)) today[level] = t = new Tally();
            return t;
        }

        /// <summary>A misconduct by an inmate of the level, in the hour (the slot it happened in).</summary>
        public void NoteIncident(SecurityLevelDef level, int hour)
        {
            if (level != null) TallyOf(level).incidents[hour]++;
        }

        /// <summary>An inmate ate a meal.</summary>
        public void NoteMeal(Pawn eater) => ateTick[eater.thingIDNumber] = Find.TickManager.TicksGame;

        /// <summary>Whether the inmate ate a meal at or after the tick.</summary>
        internal bool AteSince(Pawn p, int tick) => ateTick.TryGetValue(p.thingIDNumber, out int ate) && ate >= tick;

        /// <summary>
        /// The hour <paramref name="slotHour"/> has just ended (called at the hour change, before the regime moves anyone): who stood where during it, per level. After the day's last hour the day becomes yesterday's.
        /// </summary>
        public void Sample(Map map, PrisonMap pm, PrisonGame game, int slotHour)
        {
            int now = Find.TickManager.TicksGame, next = (slotHour + 1) % 24;
            // Per level, worked out once: the slot that ran, and the start of the meal when this was its last hour (else -1).
            var mealFrom = new Dictionary<SecurityLevelDef, int>();
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn p = prisoners[i];
                if (!InmateUtility.IsActiveInmate(p) || !RegimePlacement.InTheSample(p)) continue;
                CompInmate comp = p.Inmate();
                SecurityLevelDef level = comp.security ?? InmateUtility.DefaultSecurity;
                Tally t = TallyOf(level);
                if (!mealFrom.TryGetValue(level, out int from))
                {
                    t.ran[slotHour] = game.SlotAt(level, slotHour);
                    from = MealStart(game, level, slotHour, next, now);
                    mealFrom[level] = from;
                }
                RegimeActivityDef act = game.ActivityAt(comp, slotHour);
                if (game.Hiding(comp, act)) act = RPDefOf.RP_Lockup;
                t.n[slotHour]++;
                if (TestPlaced != null ? TestPlaced(p) : RegimePlacement.InExpectedPlace(p, act, pm)) t.ok[slotHour]++;
                if (from >= 0 && act == RPRegimeDefOf.RP_Eat && Hungry(p) && !HungerStrike.Fasting(comp) && !AteSince(p, from)) t.missed[slotHour]++;
            }
            if (slotHour == 23) Roll(now);
        }

        /// <summary>The start tick of the meal whose last hour is <paramref name="slotHour"/> (a run of eat slots that ends with it), or -1 when the hour was not the end of one.</summary>
        private static int MealStart(PrisonGame game, SecurityLevelDef level, int slotHour, int nextHour, int now)
        {
            if (game.SlotAt(level, slotHour) != RPRegimeDefOf.RP_Eat || game.SlotAt(level, nextHour) == RPRegimeDefOf.RP_Eat) return -1;
            int run = 1;
            while (run < 24 && game.SlotAt(level, (slotHour - run + 24) % 24) == RPRegimeDefOf.RP_Eat) run++;
            return now - run * GenDate.TicksPerHour;
        }

        private static bool Hungry(Pawn p) => p.needs?.food is { } food && food.CurLevelPercentage < food.PercentageThreshHungry;

        /// <summary>The day is over: today's counts become yesterday's bytes, and a new day starts (the meal times of those who are gone are forgotten).</summary>
        private void Roll(int now)
        {
            Yesterday = new Dictionary<SecurityLevelDef, RegimeDay>();
            foreach (KeyValuePair<SecurityLevelDef, Tally> kv in today)
            {
                var day = new RegimeDay();
                Tally t = kv.Value;
                for (int h = 0; h < 24; h++)
                {
                    day.ran[h] = t.ran[h];
                    day.sample[h] = (byte)Mathf.Min(t.n[h], 255);
                    day.share[h] = t.n[h] == 0 ? (byte)0 : (byte)Mathf.RoundToInt(100f * t.ok[h] / t.n[h]);
                    day.incidents[h] = (byte)Mathf.Min(t.incidents[h], 255);
                    day.missed[h] = (byte)Mathf.Min(t.missed[h], 255);
                }
                Yesterday[kv.Key] = day;
            }
            today.Clear();
            foreach (int id in ateTick.Where(kv => now - kv.Value > 2 * GenDate.TicksPerDay).Select(kv => kv.Key).ToList()) ateTick.Remove(id);
        }

        // ---- the words --------------------------------------------------------------------------------------------------------------

        /// <summary>The tooltip of one hour of the strip: "12:00 eat - 71% in place of 14, 2 incidents, 9 missed the meal". With no numbers for the hour it says why there may be none.</summary>
        public string Tip(SecurityLevelDef level, int hour)
        {
            if (!Yesterday.TryGetValue(level, out RegimeDay day) || !day.Has(hour)) return "RP_RegimeFeedbackNone".Translate(hour, level.label).Resolve();
            string line = "RP_RegimeFeedbackLine".Translate(hour, day.ran[hour]?.label ?? "?", (int)day.share[hour], (int)day.sample[hour]).Resolve();
            if (day.incidents[hour] > 0) line += ", " + (day.incidents[hour] == 1 ? "RP_RegimeFeedbackIncident".Translate() : "RP_RegimeFeedbackIncidents".Translate((int)day.incidents[hour])).Resolve();
            if (day.missed[hour] > 0) line += ", " + "RP_RegimeFeedbackMissed".Translate((int)day.missed[hour]).Resolve();
            return line;
        }
    }
}
