using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPMentorDefOf
    {
        public static ThoughtDef RP_Mentoring;
        public static ThoughtDef RP_Mentored;
        public static ThoughtDef RP_MentorTurnedAway;

        static RPMentorDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPMentorDefOf));
    }

    /// <summary>
    /// An old hand takes a newcomer under their wing: a calm older inmate who has kept out of trouble and a young or new
    /// one at the same level. Let it happen (the default) and for ten days the newcomer calms down while the mentor is in
    /// the same room, and both feel better for it; but a mentor in a gang may be bringing the newcomer in. Keep them apart
    /// and the old hand takes it badly.
    /// </summary>
    public class IncidentWorker_Mentor : IncidentWorker_Prison
    {
        protected override int MinInmates => 4;

        protected override bool CanFireNowSub(IncidentParms parms) =>
            base.CanFireNowSub(parms) && !StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_Mentor"))
            && Mentors.AnyPair((Map)parms.target);

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            List<Pawn> mentors = map.mapPawns.PrisonersOfColonySpawned.Where(Mentors.CanMentor).ToList();
            foreach (Pawn mentor in mentors.InRandomOrder())
            {
                Pawn mentee = RandomInmate(map, x => Mentors.Fits(mentor, x));
                if (mentee == null) continue;
                mentor.Inmate().lastStoryTick = Find.TickManager.TicksGame;
                ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Mentor", "RP_MentorLabel".Translate(mentor.LabelShort, mentee.LabelShort),
                    "RP_MentorText".Translate(mentor.LabelShort, mentor.ageTracker.AgeBiologicalYears, mentor.Inmate().DaysServed.ToString("0"), mentee.LabelShort,
                        mentee.ageTracker.AgeBiologicalYears, Mentors.Days), map, new LookTargets(new[] { mentor, mentee }));
                letter.pawns.Add(mentor);
                letter.pawns.Add(mentee);
                letter.StartTimeout(GenDate.TicksPerHour * 12);
                Find.LetterStack.ReceiveLetter(letter);
                return true;
            }
            return false;
        }
    }

    public static class Mentors
    {
        public const int Days = 10, MinAge = 35, MaxMenteeAge = 25, CooldownDays = 5;
        public const float MenteeNewDays = 3f, MentorMaxTension = 25f, Calming = 1f, GangPullPerDay = 0.05f, NearCells = 10f;
        /// <summary>Days inside before someone counts as an old hand (a field: the day-one test plans lower it).</summary>
        public static float MentorMinDays = 5f;

        private static bool Settled(Pawn p, CompInmate c) =>
            InmateUtility.IsActiveInmate(p) && !p.Downed && !p.InMentalState && c.forcedActivity == null && DeathRowSystem.Ext(c) == null;

        private static bool Mentoring(Pawn p) => p.MapHeld.mapPawns.PrisonersOfColonySpawned.Any(x => x.Inmate()?.mentorId == p.thingIDNumber);

        /// <summary>Older, a while inside, calm, no trouble in three days, not guided or guiding, not in a story (or turned
        /// away) lately.</summary>
        public static bool CanMentor(Pawn p) => CanMentor(p, null);

        /// <param name="guiding">The ids of everyone who guides someone (one pass over the inmates), so a sweep over all of them does not look again for each; null: asked of the map.</param>
        private static bool CanMentor(Pawn p, HashSet<int> guiding)
        {
            if (p.Inmate() is not { } c || !Settled(p, c) || p.ageTracker.AgeBiologicalYears < MinAge || c.DaysServed < MentorMinDays
                || c.tension > MentorMaxTension || c.mentorId >= 0 || c.MisbehavedWithin(3 * GenDate.TicksPerDay)
                || Find.TickManager.TicksGame - c.lastStoryTick < CooldownDays * GenDate.TicksPerDay) return false;
            return !(guiding?.Contains(p.thingIDNumber) ?? Mentoring(p));
        }

        /// <summary>Young or new, with no one guiding them yet, not guiding anyone themselves.</summary>
        public static bool CanBeMentored(Pawn p) => CanBeMentored(p, null);

        private static bool CanBeMentored(Pawn p, HashSet<int> guiding) =>
            p.Inmate() is { } c && Settled(p, c) && c.mentorId < 0 && (c.DaysServed < MenteeNewDays || p.ageTracker.AgeBiologicalYears <= MaxMenteeAge)
            && !(guiding?.Contains(p.thingIDNumber) ?? Mentoring(p));

        public static bool Fits(Pawn mentor, Pawn mentee) => mentee != mentor && CanBeMentored(mentee) && mentee.Inmate().security == mentor.Inmate().security;

        private static readonly HashSet<int> guidingScratch = new HashSet<int>();
        private static readonly Dictionary<SecurityLevelDef, (int mentors, int mentees, int both)> levelScratch = new Dictionary<SecurityLevelDef, (int, int, int)>();

        /// <summary>
        /// Whether some inmate could mentor some other at the same security level. One pass: who guides whom, then per level how many could mentor, how many could be
        /// mentored, and how many are both (a pair is a mentor and someone else). It was every mentor against every inmate, each asking the map again who guides whom.
        /// </summary>
        public static bool AnyPair(Map map)
        {
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            guidingScratch.Clear();
            levelScratch.Clear();
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { mentorId: >= 0 } c) guidingScratch.Add(c.mentorId);
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn p = inmates[i];
                if (p.Inmate() is not { security: { } level }) continue;
                bool mentor = CanMentor(p, guidingScratch), mentee = CanBeMentored(p, guidingScratch);
                if (!mentor && !mentee) continue;
                levelScratch.TryGetValue(level, out var n);
                levelScratch[level] = (n.mentors + (mentor ? 1 : 0), n.mentees + (mentee ? 1 : 0), n.both + (mentor && mentee ? 1 : 0));
            }
            foreach (var n in levelScratch.Values)
                if (n.mentors * n.mentees - n.both > 0) return true;
            return false;
        }

        public static Pawn MentorOf(Pawn p, CompInmate c)
        {
            if (c.mentorId < 0 || p.MapHeld == null) return null;
            IReadOnlyList<Pawn> inmates = p.MapHeld.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].thingIDNumber == c.mentorId) return inmates[i];
            return null;
        }

        /// <summary>Tension relief this hour: the mentor is close (the same room; within a few steps in a big or outdoor
        /// one) and in a state to talk.</summary>
        public static float Relief(Pawn p, CompInmate c)
        {
            if (c.mentorId < 0) return 0f;
            Pawn m = MentorOf(p, c);
            return m != null && !m.Downed && !m.InMentalState && Together(p, m) ? Calming : 0f;
        }

        public static bool Together(Pawn a, Pawn b)
        {
            Room room = a.GetRoom();
            if (room == null || room != b.GetRoom()) return false;
            return (!room.PsychologicallyOutdoors && room.CellCount < 600) || a.Position.InHorDistOf(b.Position, NearCells);
        }

        public static void Start(Pawn mentor, Pawn mentee)
        {
            CompInmate c = mentee.Inmate();
            c.mentorId = mentor.thingIDNumber;
            c.mentorName = mentor.LabelShort;
            c.mentorUntilTick = Find.TickManager.TicksGame + Days * GenDate.TicksPerDay;
            mentor.needs.mood?.thoughts.memories.TryGainMemory(RPMentorDefOf.RP_Mentoring);
            mentee.needs.mood?.thoughts.memories.TryGainMemory(RPMentorDefOf.RP_Mentored);
            // (Somebody who takes an interest: a gang member being guided may begin to wonder.)
            GangExit.Doubt(mentee, c);
            PrisonGame.Instance.stats.mentorships++;
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_MentorStart".Translate(mentor.LabelShort, mentee.LabelShort), mentor, mentee);
        }

        /// <summary>Forget a mentorship (release, return to prison: a new stay starts without one).</summary>
        public static void Clear(CompInmate c)
        {
            c.mentorId = -1;
            c.mentorName = null;
            c.mentorUntilTick = -1;
        }

        /// <summary>Ended early: the good feeling goes with it.</summary>
        private static void EndEarly(Pawn mentee, CompInmate c, Pawn mentor)
        {
            Clear(c);
            mentee.needs?.mood?.thoughts.memories.RemoveMemoriesOfDef(RPMentorDefOf.RP_Mentored);
            mentor?.needs?.mood?.thoughts.memories.RemoveMemoriesOfDef(RPMentorDefOf.RP_Mentoring);
        }

        /// <summary>Someone left for good: a newcomer they guided loses them now, and their own mentorship ends.</summary>
        public static void Notify_Leaving(Pawn p)
        {
            if (p.Inmate() is { } own && own.mentorId >= 0) EndEarly(p, own, MentorOf(p, own));
            if (p.MapHeld == null) return;
            foreach (Pawn x in p.MapHeld.mapPawns.PrisonersOfColonySpawned)
                if (x.Inmate() is { } xc && xc.mentorId == p.thingIDNumber)
                {
                    Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_MentorGone".Translate(p.LabelShort, x.LabelShort), x);
                    EndEarly(x, xc, p);
                }
        }

        /// <summary>Daily: a mentor who left ends it (backstop for Notify_Leaving); a mentor's gang may draw the newcomer in
        /// while they are still at the same level; ten days and it is done.</summary>
        public static void Daily(Map map)
        {
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
            {
                if (p.Inmate() is not { } c || c.mentorId < 0) continue;
                Pawn m = MentorOf(p, c);
                if (m == null)
                {
                    Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_MentorGone".Translate(c.mentorName ?? "?", p.LabelShort), p);
                    EndEarly(p, c, null);
                    continue;
                }
                CompInmate mc = m.Inmate();
                // (Not the gang they left: it hates them.)
                if (mc?.gang != null && c.gang == null && !GangDeals.NoRecruiting(mc.gang) && !(GangExit.ExActive(c) && c.exGang == mc.gang) && mc.security == c.security && mc.forcedActivity == null && Rand.Chance(GangPullPerDay))
                {
                    // Like any recruit: the warden learns of it the usual ways (informants, being caught together).
                    c.gang = mc.gang;
                    PrisonGame.Instance.stats.gangRecruits++;
                    if (mc.gangKnown) Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_MentorRecruited".Translate(m.LabelShort, p.LabelShort, mc.gang.label), m, p);
                }
                if (now >= c.mentorUntilTick)
                {
                    Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_MentorDone".Translate(m.LabelShort, p.LabelShort), m, p);
                    Clear(c);
                }
            }
        }
    }

    public class MentorDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Mentor_Allow", "RP_Mentor_Apart" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 0;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn mentor = l.pawns.ElementAtOrDefault(0), mentee = l.pawns.ElementAtOrDefault(1);
            if (mentor == null || mentee == null || mentor.Dead || mentee.Dead || mentee.Inmate() is not { } c || mentor.Inmate() == null) return;
            if (option == 0)
            {
                if (c.mentorId < 0 && InmateUtility.IsActiveInmate(mentor) && InmateUtility.IsActiveInmate(mentee)) Mentors.Start(mentor, mentee);
                return;
            }
            mentor.needs.mood?.thoughts.memories.TryGainMemory(RPMentorDefOf.RP_MentorTurnedAway);
        }
    }
}
