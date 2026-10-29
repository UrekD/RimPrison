using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// What the overview's wing block and kitchen line show, counted in one pass over the inmates and the staff and kept for <see cref="RefreshTicks"/> (the screen asks every frame, the prison
    /// does not change that fast). Read-only: nothing here changes how the prison runs.
    /// </summary>
    public static class DeskBoard
    {
        /// <summary>An inmate at or above this tension is tense (the inmates list's filter, the storyteller's count).</summary>
        public const float TenseAt = 60f;

        /// <summary>An inmate whose food is under this share is hungry now.</summary>
        public const float HungryBelow = 0.25f;

        public const int RefreshTicks = 250;

        /// <summary>One security level that holds inmates: who is in it, how tense and how held down they are, what they did today, and the guards standing in its wing zones now.</summary>
        public class WingRow
        {
            public SecurityLevelDef level;
            public int inmates, tense, misconduct, guards;
            /// <summary>Inmates held down (suppression over <see cref="Suppression.Line"/>: shut out of programs, no riot to join, slow to calm).</summary>
            public int shut;
            /// <summary>Of those, the ones under the line within the day (<see cref="Suppression.Clearing"/>).</summary>
            public int clearing;
            public float meanTension, meanSuppressed;
        }

        public class KitchenLine
        {
            public int cooked, eaten, onCounters, hungry, cooks;
        }

        private static readonly List<WingRow> rows = new List<WingRow>();
        private static KitchenLine kitchen = new KitchenLine();
        private static int stampTick = -1, stampMap = -1, stampFrame = -1;
        private const int RefreshFrames = 120;

        /// <summary>The same test as the panel's staff count: at work now, not at home, not on strike and not on a break.</summary>
        public static bool OnDuty(Pawn p, int hour) => !StaffCommute.AtHome(p) && !IncidentWorker_StaffStrike.Striking(p) && p.timetable?.GetAssignment(hour) != TimeAssignmentDefOf.Joy;

        public static bool Tense(CompInmate c) => c.tension >= TenseAt;

        private static bool Counted(Pawn p) => p.Inmate()?.security != null && !ShuttleSystem.OnTheWayOut(p);

        /// <summary>The rows and the kitchen line, from the cache while it is fresh (and for this map).</summary>
        public static (List<WingRow> wings, KitchenLine kitchen) Current(PrisonGame game, Map map)
        {
            int now = Find.TickManager.TicksGame;
            // (And every few seconds of frames: the clock stands still while the game is paused, and a level changed on another screen would show only once it ran again.)
            if (stampTick < 0 || now < stampTick || now - stampTick >= RefreshTicks || stampMap != map.uniqueID || UnityEngine.Time.frameCount - stampFrame >= RefreshFrames || UnityEngine.Time.frameCount < stampFrame)
            {
                (List<WingRow> fresh, KitchenLine line) = Compute(game, map);
                rows.Clear();
                rows.AddRange(fresh);
                kitchen = line;
                (stampTick, stampMap, stampFrame) = (now, map.uniqueID, UnityEngine.Time.frameCount);
            }
            return (rows, kitchen);
        }

        /// <summary>Forget what was counted (a check changes the prison within a tick).</summary>
        public static void Reset() => stampTick = -1;

        /// <summary>The count itself, never cached: one row per security level (in the screens' order) that holds inmates, and the kitchen line.</summary>
        public static (List<WingRow> wings, KitchenLine kitchen) Compute(PrisonGame game, Map map)
        {
            var byLevel = new Dictionary<SecurityLevelDef, WingRow>();
            var line = new KitchenLine { cooked = game.today.mealsCooked, eaten = game.today.mealsEaten };
            var tensionSum = new Dictionary<SecurityLevelDef, float>();
            var suppressedSum = new Dictionary<SecurityLevelDef, float>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!Counted(p)) continue;
                if (p.needs?.food is { } food && food.CurLevelPercentage < HungryBelow) line.hungry++;
                CompInmate c = p.Inmate();
                if (!byLevel.TryGetValue(c.security, out WingRow row))
                {
                    byLevel[c.security] = row = new WingRow { level = c.security, misconduct = game.today.MisconductsOn(c.security) };
                    tensionSum[c.security] = suppressedSum[c.security] = 0f;
                }
                row.inmates++;
                tensionSum[c.security] += c.tension;
                suppressedSum[c.security] += c.suppressed;
                if (Tense(c)) row.tense++;
                if (Suppression.Held(c)) row.shut++;
                if (Suppression.Clearing(c)) row.clearing++;
            }
            PrisonMap pm = PrisonMap.Of(map);
            int hour = GenLocalDate.HourOfDay(map);
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.GetComp<CompStaff>() is not { role: { } role } || !OnDuty(p, hour)) continue;
                if (role == RPStaffRoleDefOf.RP_Cook) line.cooks++;
                if (!GuardUtility.IsGuard(p) || pm?.zones.ZoneAt(p.Position) is not { security: { } wing } || !byLevel.TryGetValue(wing, out WingRow guarded)) continue;
                guarded.guards++;
            }
            foreach (Building_Storage counter in MealCounters.On(map)) line.onCounters += MealCounters.Meals(counter);
            var wings = new List<WingRow>(byLevel.Values);
            foreach (WingRow row in wings)
            {
                row.meanTension = tensionSum[row.level] / row.inmates;
                row.meanSuppressed = suppressedSum[row.level] / row.inmates;
            }
            wings.Sort((a, b) => a.level.uiOrder.CompareTo(b.level.uiOrder));
            return (wings, line);
        }

        /// <summary>The inmates at tension <see cref="TenseAt"/> and over (the panel's "tense" count).</summary>
        public static int TenseCount(Map map)
        {
            int n = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { security: not null } c && Tense(c)) n++;
            return n;
        }

        /// <summary>One letter on the desk: open on the stack (with the hours left to answer it, -1 for none), or held for midnight by the day's budget (with the hours before it lapses unanswered).</summary>
        public class DeskLetter
        {
            public ChoiceLetter_PrisonDecision letter;
            public bool held;
            /// <summary>Open: the hours left to answer (-1: no deadline). Held: the answer time it gets when it is delivered (-1: none).</summary>
            public int hoursLeft = -1;
            /// <summary>Held: the hours before it lapses as unanswered if no day's budget has room for it, to the midnight that lapses it (<see cref="StoryLetters.LapseTick"/>; -1: it does not lapse).</summary>
            public int lapseHours = -1;
            public bool urgent;
            /// <summary>The deadline as the desk shows it (worked out at the first draw: the desk is kept for a while).</summary>
            public string deadline;
        }

        /// <summary>The desk (the overview's letters view, the panel's letters tip): the open and the held letters and the day's budget of decisions.</summary>
        public class DeskState
        {
            public readonly List<DeskLetter> open = new List<DeskLetter>(), held = new List<DeskLetter>();
            public int used, budget;
            /// <summary>The budget is in force (an autotest run turns it off unless a check wants it).</summary>
            public bool budgetOn;

            public int Left => Mathf.Max(0, budget - used);

            private string head;

            /// <summary>The day's budget used and left, and the open and held counts, in one line.</summary>
            public string HeadLine() => head ??= budgetOn
                ? "RP_DeskBudgetLine".Translate(used, budget, Left, open.Count, held.Count).Resolve()
                : "RP_DeskBudgetOff".Translate(open.Count, held.Count).Resolve();
        }

        public static int HoursCeil(int ticks) => ticks <= 0 ? 0 : (ticks + GenDate.TicksPerHour - 1) / GenDate.TicksPerHour;

        /// <summary>The desk now (one pass over the letter stack and the held letters: cheap enough to work out when it is drawn).</summary>
        public static DeskState Desk(PrisonGame game, Map map) => Desk(game, map, Find.TickManager.TicksGame);

        /// <summary>The desk at a given tick (a check asks about letters held for days in a game hours old).</summary>
        public static DeskState Desk(PrisonGame game, Map map, int now)
        {
            var s = new DeskState { used = game.story.decisionsToday, budget = StoryLetters.Budget(map?.mapPawns.PrisonersOfColonySpawnedCount ?? 0), budgetOn = StoryLetters.Active };
            List<Letter> stack = Find.LetterStack.LettersListForReading;
            for (int i = 0; i < stack.Count; i++)
            {
                if (stack[i] is not ChoiceLetter_PrisonDecision { answered: false } d) continue;
                s.open.Add(new DeskLetter { letter = d, hoursLeft = d.TimeoutActive ? HoursCeil(d.disappearAtTick - now) : -1, urgent = StoryLetters.Urgent(d) });
            }
            // (Soonest deadline first; those without one last, in the order they came: a stable sort, List.Sort is not.)
            if (s.open.Count > 1)
            {
                List<DeskLetter> sorted = s.open.OrderBy(x => x.hoursLeft < 0 ? int.MaxValue : x.hoursLeft).ToList();
                s.open.Clear();
                s.open.AddRange(sorted);
            }
            foreach (ChoiceLetter_PrisonDecision d in game.story.deferredLetters)
            {
                if (d == null || d.answered) continue;
                // (It lapses at a midnight, the first after it has waited its days: not at the hour it was held.)
                int lapse = StoryLetters.LapseTick(d);
                s.held.Add(new DeskLetter { letter = d, held = true, hoursLeft = d.deferredTimeout > 0 ? HoursCeil(d.deferredTimeout) : -1, lapseHours = lapse >= 0 ? HoursCeil(lapse - now) : -1 });
            }
            return s;
        }

        /// <summary>Prison decision letters on the letter stack that nobody has answered yet (the ones the day's budget holds back for tomorrow are not on the stack: they are not counted).</summary>
        public static int OpenLetters()
        {
            int n = 0;
            List<Letter> stack = Find.LetterStack.LettersListForReading;
            for (int i = 0; i < stack.Count; i++)
                if (stack[i] is ChoiceLetter_PrisonDecision { answered: false }) n++;
            return n;
        }
    }
}
