using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Inmates' demands: when one need is badly unmet across the population, the inmates put a demand to the warden,
    /// through a spokesperson (a gang leader if one is known, else the best talker). Promise to fix it and deliver within
    /// two days (calm, better reputation, the spokesperson's standing grows), break the promise (fury, and the
    /// spokesperson was made a fool of), or refuse outright (resentment now, the spokesperson most of all).
    /// A demand about the outdoors or joy names the timetable: the letter asks one level for another hour in the yard (see
    /// <see cref="DemandSystem.TimetableNeed"/>), and the promise is judged on the schedule that level ran in the last day of it.
    /// </summary>
    public class InmateDemand : IExposable
    {
        public NeedDef need;
        public int deadlineTick = -1;
        /// <summary>Who put the demand (thing ID, -1 none; the name outlives them).</summary>
        public int spokesId = -1;
        public string spokesName;
        public int cooldownUntilTick = -1;
        /// <summary>A demand that names the timetable (null level: it does not): the level it was put for, the slot it asks for more of, the number of such slots a day it was promised (the level's count then, and one more) and
        /// the hours of the last day before the deadline in which that slot ran (see <see cref="DemandSystem.TallySlotHour"/>).</summary>
        public SecurityLevelDef slotLevel;
        public RegimeActivityDef slotActivity;
        public int slotTarget, slotRan;

        public bool Active => need != null && deadlineTick > 0;

        /// <summary>A promise of more of a slot (judged on the schedule, not the need's average).</summary>
        public bool Timetable => slotLevel != null && slotActivity != null && DemandSystem.TimetableNeed(need);

        public void ExposeData()
        {
            Scribe_Defs.Look(ref need, "need");
            Scribe_Values.Look(ref deadlineTick, "deadlineTick", -1);
            Scribe_Values.Look(ref cooldownUntilTick, "cooldownUntilTick", -1);
            Scribe_Values.Look(ref spokesId, "spokesId", -1);
            Scribe_Values.Look(ref spokesName, "spokesName");
            Scribe_Defs.Look(ref slotLevel, "slotLevel");
            Scribe_Defs.Look(ref slotActivity, "slotActivity");
            Scribe_Values.Look(ref slotTarget, "slotTarget");
            Scribe_Values.Look(ref slotRan, "slotRan");
        }
    }

    [DefOf]
    public static class RPDemandDefOf
    {
        public static ThoughtDef RP_SpokesHeard;
        public static ThoughtDef RP_SpokesIgnored;

        static RPDemandDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPDemandDefOf));
    }

    public static class DemandSystem
    {
        public const float LowLevel = 0.3f;
        public const float LowShare = 0.4f;
        public const float MetAverage = 0.5f;
        public const int Hours = 48;
        public const float SpokesRefusedTension = 12f, SpokesBrokenTension = 15f, SpokesKeptTension = -10f;
        public const int SpokesRestDays = 7;
        /// <summary>A timetable promise is judged on the last day before its deadline (the warden has the first one to put the hour in), and a level is not asked to be given more than this many of a slot.</summary>
        public const int SlotWindowHours = 24, MaxAskedSlots = 8;

        /// <summary>Who speaks for the inmates: someone who has not spoken this week if possible (one person does not take
        /// every answer), then a known gang leader, then the best talker, the calmer of equals. Not from the hole or
        /// death row.</summary>
        public static Pawn Spokesperson(Map map)
        {
            int now = Find.TickManager.TicksGame;
            return map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && !p.Downed && !p.InMentalState
                                                                    && p.Inmate().forcedActivity == null && DeathRowSystem.Ext(p.Inmate()) == null)
                .OrderBy(p => now - p.Inmate().lastSpokeTick < SpokesRestDays * GenDate.TicksPerDay ? 1 : 0)
                .ThenByDescending(p => p.Inmate() is { gangLeader: true, gangKnown: true } ? 1 : 0)
                .ThenByDescending(p => p.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 0)
                .ThenBy(p => p.Inmate().tension).FirstOrDefault();
        }

        public static Pawn InmateById(Map map, int id)
        {
            if (id < 0) return null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.thingIDNumber == id) return p;
            return null;
        }

        private static void Spokes(Pawn p, float tension, ThoughtDef thought)
        {
            if (p?.Inmate() is not { } c || !InmateUtility.IsActiveInmate(p)) return;
            c.tension = Mathf.Clamp(c.tension + tension, 0f, 100f);
            p.needs?.mood?.thoughts.memories.TryGainMemory(thought);
        }

        /// <summary>Needs the inmates make demands about: things the regime and the warden control (not comfort, beauty
        /// or chemical cravings, which are chronically low in any prison).</summary>
        public static IEnumerable<NeedDef> CandidateNeeds()
        {
            yield return NeedDefOf.Food;
            yield return RPVanillaDefOf.Joy;
            yield return RPVanillaDefOf.Outdoors;
            yield return RPDefOf.RP_Hygiene;
            yield return RPDefOf.RP_Bladder;
            yield return RPVisitDefOf.RP_Family;
            yield return RPDefOf.RP_CleanClothes;
        }

        /// <summary>Average of a need across active inmates, and the share below <see cref="LowLevel"/>.</summary>
        public static (float avg, float lowShare, int n) Survey(Map map, NeedDef need)
        {
            float sum = 0f;
            int n = 0, low = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!InmateUtility.IsActiveInmate(p) || p.needs?.TryGetNeed(need) is not { } nd) continue;
                sum += nd.CurLevelPercentage;
                n++;
                if (nd.CurLevelPercentage < LowLevel) low++;
            }
            return n == 0 ? (1f, 0f, 0) : (sum / n, low / (float)n, n);
        }

        /// <summary>Whether a demand about this need asks for a slot of the timetable (the outdoors and joy: the hours in the yard) instead of an average.</summary>
        public static bool TimetableNeed(NeedDef need) => need != null && (need == RPVanillaDefOf.Outdoors || need == RPVanillaDefOf.Joy);

        /// <summary>The slot the level is asked to have more of: free time (the yard and the common rooms), or the hour alone in the yard for a level that never leaves its cells without a guard.</summary>
        public static RegimeActivityDef SlotFor(SecurityLevelDef level) => level.escortOnly ? RPSupermaxDefOf.RP_YardAlone : RPDefOf.RP_FreeTime;

        /// <summary>How many hours of the slot the level's timetable has (the timetable itself, without today's orders).</summary>
        public static int SlotCount(PrisonGame game, SecurityLevelDef level, RegimeActivityDef slot) => game.RegimeFor(level).slots.Count(s => s == slot);

        /// <summary>
        /// The level to put the demand for: the one with the biggest share of its inmates low on the need (at least two inmates in it, and not one that has the slot most of the day already), the fuller wing when
        /// they tie. Null when no level qualifies (the demand is then judged on the need's average, as the others are).
        /// </summary>
        public static SecurityLevelDef WorstLevel(Map map, PrisonGame game, NeedDef need)
        {
            var count = new Dictionary<SecurityLevelDef, int>();
            var low = new Dictionary<SecurityLevelDef, int>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!InmateUtility.IsActiveInmate(p) || p.Inmate().security is not { } level || p.needs?.TryGetNeed(need) is not { } nd) continue;
                count[level] = count.TryGetValue(level, out int n) ? n + 1 : 1;
                if (nd.CurLevelPercentage < LowLevel) low[level] = low.TryGetValue(level, out int l) ? l + 1 : 1;
            }
            SecurityLevelDef best = null;
            float bestShare = -1f;
            foreach (KeyValuePair<SecurityLevelDef, int> kv in count.OrderBy(kv => kv.Key.uiOrder))
            {
                if (kv.Value < 2 || SlotCount(game, kv.Key, SlotFor(kv.Key)) >= MaxAskedSlots) continue;
                float share = low.TryGetValue(kv.Key, out int l) ? l / (float)kv.Value : 0f;
                if (share > bestShare || (share == bestShare && kv.Value > count[best])) (best, bestShare) = (kv.Key, share);
            }
            return best;
        }

        /// <summary>The slot asked for, in the letter's words: "a yard hour", "a second yard hour" - the one after the <paramref name="have"/> the level has.</summary>
        public static string AskedSlot(SecurityLevelDef level, int have)
        {
            string yard = (level.escortOnly ? "RP_DemandSlot_YardAlone" : "RP_DemandSlot_Yard").Translate().Resolve();
            return have switch
            {
                0 => "RP_DemandSlot_First".Translate(yard).Resolve(),
                1 => "RP_DemandSlot_Second".Translate(yard).Resolve(),
                2 => "RP_DemandSlot_Third".Translate(yard).Resolve(),
                _ => "RP_DemandSlot_More".Translate(yard, have + 1).Resolve(),
            };
        }

        /// <summary>Hourly: count a timetable promise's slot in its last day, and judge a promise when it is due.</summary>
        public static void Hourly(Map map, PrisonGame game)
        {
            InmateDemand d = game.demand;
            // (Once per hour, not once per home map: one prison.)
            if (d.Active && d.Timetable && map == Find.AnyPlayerHomeMap && Find.TickManager.TicksGame >= d.deadlineTick - SlotWindowHours * GenDate.TicksPerHour && Find.TickManager.TicksGame < d.deadlineTick)
                TallySlotHour(game, d, GenLocalDate.HourOfDay(map));
            if (d.Active && Find.TickManager.TicksGame >= d.deadlineTick) Judge(map, game, d);
        }

        /// <summary>
        /// The hour that has just started is one of the level's slot, as the timetable and the day's orders have it (the slots a level ran, over the day, are what a promise of more of them is kept with). An hour the
        /// level spends under a lockdown (its own or the prison's) is not one: nobody left their cell.
        /// </summary>
        public static void TallySlotHour(PrisonGame game, InmateDemand d, int hour)
        {
            if (game.LevelLockedDown(d.slotLevel)) return;
            if (game.SlotAt(d.slotLevel, hour) == d.slotActivity) d.slotRan++;
        }

        /// <summary>Evening: raise a demand (one at a time).</summary>
        public static void Evening(Map map, PrisonGame game)
        {
            InmateDemand d = game.demand;
            int now = Find.TickManager.TicksGame;
            if (d.Active)
            {
                if (now >= d.deadlineTick) Judge(map, game, d);
                return;
            }
            if (now < d.cooldownUntilTick || map.mapPawns.PrisonersOfColonySpawnedCount < 4) return;
            NeedDef worst = null;
            float worstShare = LowShare;
            foreach (NeedDef nd in CandidateNeeds())
            {
                (float avg, float share, int n) = Survey(map, nd);
                if (n >= 4 && share >= worstShare)
                {
                    worstShare = share;
                    worst = nd;
                }
            }
            if (worst != null) Raise(map, game, worst);
        }

        /// <param name="level">The level a demand about the outdoors or joy is put for (null: the one worst off, see <see cref="WorstLevel"/>).</param>
        public static void Raise(Map map, PrisonGame game, NeedDef need, SecurityLevelDef level = null)
        {
            Pawn spokes = Spokesperson(map);
            string name = spokes?.LabelShort ?? "RP_DemandNoSpokes".Translate().ToString();
            SecurityLevelDef slotLevel = TimetableNeed(need) ? level ?? WorstLevel(map, game, need) : null;
            int have = slotLevel != null ? SlotCount(game, slotLevel, SlotFor(slotLevel)) : 0;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_InmateDemand", "RP_DemandLabel".Translate(),
                slotLevel != null ? "RP_DemandSlotText".Translate(need.label, Hours, name, AskedSlot(slotLevel, have), slotLevel.label) : "RP_DemandText".Translate(need.label, Hours, name), map, spokes != null ? new LookTargets(spokes) : null);
            letter.need = need;
            // (The level and the number of slots a day the promise is for: the answer is made on what the letter said.)
            if (slotLevel != null)
            {
                letter.note = slotLevel.defName;
                letter.amount = have + 1;
            }
            if (spokes != null)
            {
                letter.pawns.Add(spokes);
                spokes.Inmate().lastSpokeTick = Find.TickManager.TicksGame;
            }
            letter.StartTimeout(GenDate.TicksPerHour * 8);
            game.demand.cooldownUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay * 4;
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <param name="slotLevel">For a promise about the outdoors or joy: the level and the number of slots a day it is for (null and 0: the worst level now, one more than it has; the riot and hostage deals have no letter to name one).</param>
        public static void Accept(PrisonGame game, NeedDef need, Pawn spokes = null, SecurityLevelDef slotLevel = null, int slotTarget = 0) =>
            Accept(game, need, spokes, slotLevel, slotTarget, worstLevelIfNone: true);

        /// <summary>The demand letter's answer: the promise is made on what the letter said, so a letter that named no level (no level qualified when it was written, or it is from an older save) makes a promise judged on the need's average.</summary>
        public static void AcceptLetter(PrisonGame game, NeedDef need, Pawn spokes, SecurityLevelDef slotLevel, int slotTarget) =>
            Accept(game, need, spokes, slotLevel, slotTarget, worstLevelIfNone: false);

        private static void Accept(PrisonGame game, NeedDef need, Pawn spokes, SecurityLevelDef slotLevel, int slotTarget, bool worstLevelIfNone)
        {
            // A promise swept aside by a new one (a riot deal): whoever won the old one was made a fool of.
            if (game.demand.Active && game.demand.spokesId >= 0 && game.demand.spokesId != (spokes?.thingIDNumber ?? -1))
                foreach (Map m in Find.Maps)
                    if (InmateById(m, game.demand.spokesId) is { } old) Spokes(old, 0f, RPDemandDefOf.RP_SpokesIgnored);
            game.demand.need = need;
            game.demand.spokesId = spokes?.thingIDNumber ?? -1;
            game.demand.spokesName = spokes?.LabelShort;
            game.demand.deadlineTick = Find.TickManager.TicksGame + Hours * GenDate.TicksPerHour;
            SetSlot(game, need, slotLevel, slotTarget, worstLevelIfNone);
            // A promise to the inmates ends any hunger strike: they got what the strike was for.
            foreach (Map map in Find.Maps)
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate() is { } c && HungerStrike.Fasting(c))
                    {
                        c.hungerStrikeUntilTick = -1;
                        Messages.Message("RP_HungerStrikeEnded".Translate(p.LabelShort), p, MessageTypeDefOf.NeutralEvent, historical: false);
                    }
        }

        /// <summary>A promise about the outdoors or joy names the timetable: the level, the slot and how many of it a day (and nothing yet counted); any other promise names none.</summary>
        private static void SetSlot(PrisonGame game, NeedDef need, SecurityLevelDef level, int target, bool worstLevelIfNone)
        {
            InmateDemand d = game.demand;
            (d.slotLevel, d.slotActivity, d.slotTarget, d.slotRan) = (null, null, 0, 0);
            if (!TimetableNeed(need)) return;
            if (level == null && worstLevelIfNone && Find.AnyPlayerHomeMap is { } home) level = WorstLevel(home, game, need);
            if (level == null) return;
            d.slotLevel = level;
            d.slotActivity = SlotFor(level);
            d.slotTarget = target > 0 ? target : SlotCount(game, level, d.slotActivity) + 1;
            Messages.Message("RP_DemandSlotPromised".Translate(AskedSlot(level, d.slotTarget - 1), level.label, Hours), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        public static void Refuse(Map map, NeedDef need, Pawn spokes = null)
        {
            Shift(map, 8f, "RP_Cause_DemandRefused");
            Spokes(spokes, SpokesRefusedTension, RPDemandDefOf.RP_SpokesIgnored);
            Messages.Message("RP_DemandRefused".Translate(need.label), MessageTypeDefOf.NegativeEvent);
        }

        private static void Judge(Map map, PrisonGame game, InmateDemand d)
        {
            (float avg, _, _) = Survey(map, d.need);
            // A promise of more of a slot is kept by the schedule (the slots the level ran in the last day), not by what the need has come to.
            bool slot = d.Timetable;
            bool met = slot ? d.slotRan >= d.slotTarget : avg >= MetAverage;
            Pawn spokes = InmateById(map, d.spokesId);
            Spokes(spokes, met ? SpokesKeptTension : SpokesBrokenTension, met ? RPDemandDefOf.RP_SpokesHeard : RPDemandDefOf.RP_SpokesIgnored);
            if (met)
            {
                Shift(map, -15f, null);
                game.reputation = Mathf.Min(100f, game.reputation + 2f);
                game.stats.demandsMet++;
            }
            else
            {
                Shift(map, 15f, "RP_Cause_BrokenPromise");
                game.stats.demandsBroken++;
            }
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, d.spokesName != null
                ? (met ? "RP_Chr_PromiseKeptTo" : "RP_Chr_PromiseBrokenTo").Translate(d.need.label, d.spokesName)
                : (met ? "RP_Chr_PromiseKept" : "RP_Chr_PromiseBroken").Translate(d.need.label), spokes != null ? new[] { spokes } : new Pawn[0]);
            TaggedString text = slot ? (met ? "RP_DemandSlotMetText" : "RP_DemandSlotBrokenText").Translate(d.need.label, d.slotLevel.label, d.slotRan, d.slotTarget)
                : (met ? "RP_DemandMetText" : "RP_DemandBrokenText").Translate(d.need.label, avg.ToStringPercent());
            Find.LetterStack.ReceiveLetter((met ? "RP_DemandMetLabel" : "RP_DemandBrokenLabel").Translate(), text, met ? LetterDefOf.PositiveEvent : LetterDefOf.NegativeEvent);
            d.need = null;
            (d.slotLevel, d.slotActivity, d.slotTarget, d.slotRan) = (null, null, 0, 0);
            d.deadlineTick = -1;
            d.spokesId = -1;
            d.spokesName = null;
        }

        private static void Shift(Map map, float tension, string cause)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } c)
                {
                    c.tension = Mathf.Clamp(c.tension + tension, 0f, 100f);
                    if (cause != null) c.mainCause = cause;
                }
        }
    }
}
