using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>Where the prison stood when the week began (for the weekly report's deltas).</summary>
    public class WeekSnapshot : IExposable
    {
        public int startTick, inmates, admitted, released, escaped, deaths, misconducts, riots;
        public int staff = -1, burnedOut, retired, dismissed;
        /// <summary>Resignations of any reason (-1 in a snapshot from before they were told apart from burnouts: the burnouts stand in).</summary>
        public int resigned = -1;
        /// <summary>-1 in a snapshot from before these were counted (the line is skipped that week).</summary>
        public int contractsMet = -1, contractsMissed, fires, foodScares, mentorships, staffTrained, remissionDays;
        public float grade = -1f, reputation;

        public static WeekSnapshot Take(PrisonGame game, Map map) => new WeekSnapshot
        {
            startTick = Find.TickManager.TicksGame,
            inmates = map.mapPawns.PrisonersOfColonySpawnedCount,
            admitted = game.stats.admitted,
            released = game.stats.released,
            escaped = game.stats.escaped,
            deaths = game.stats.deaths,
            misconducts = game.stats.misconducts,
            riots = game.stats.riots,
            staff = StaffCommute.AllStaff(map).Count,
            burnedOut = game.stats.staffBurnedOut,
            resigned = game.stats.resignations,
            retired = game.stats.staffRetired,
            dismissed = game.stats.staffDismissed,
            contractsMet = game.stats.contractsMet,
            contractsMissed = game.stats.contractsMissed,
            fires = game.stats.fires,
            foodScares = game.stats.foodScares,
            mentorships = game.stats.mentorships,
            staffTrained = game.stats.staffTrained,
            remissionDays = game.stats.remissionDays,
            grade = game.lastGrade,
            reputation = game.reputation,
        };

        public void ExposeData()
        {
            Scribe_Values.Look(ref startTick, "start");
            Scribe_Values.Look(ref inmates, "inmates");
            Scribe_Values.Look(ref admitted, "admitted");
            Scribe_Values.Look(ref released, "released");
            Scribe_Values.Look(ref escaped, "escaped");
            Scribe_Values.Look(ref deaths, "deaths");
            Scribe_Values.Look(ref misconducts, "misconducts");
            Scribe_Values.Look(ref riots, "riots");
            Scribe_Values.Look(ref staff, "staff", -1);
            Scribe_Values.Look(ref burnedOut, "burnedOut");
            Scribe_Values.Look(ref resigned, "resigned", -1);
            Scribe_Values.Look(ref retired, "retired");
            Scribe_Values.Look(ref dismissed, "dismissed");
            Scribe_Values.Look(ref contractsMet, "contractsMet", -1);
            Scribe_Values.Look(ref contractsMissed, "contractsMissed");
            Scribe_Values.Look(ref fires, "fires");
            Scribe_Values.Look(ref foodScares, "foodScares");
            Scribe_Values.Look(ref mentorships, "mentorships");
            Scribe_Values.Look(ref staffTrained, "staffTrained");
            Scribe_Values.Look(ref remissionDays, "remissionDays");
            Scribe_Values.Look(ref grade, "grade", -1f);
            Scribe_Values.Look(ref reputation, "reputation");
        }
    }

    /// <summary>
    /// Every seventh day, "The week inside": the week's numbers against where it began, the money, the grade, the
    /// biggest stories from the chronicle, and the inmate who caused the most trouble.
    /// </summary>
    public static class WeeklyReport
    {
        public const int Days = 7, Headlines = 5;

        /// <summary>After the daily settlement (grade computed).</summary>
        public static void Daily(PrisonGame game, Map map)
        {
            if (game.week == null)
            {
                game.week = WeekSnapshot.Take(game, map);
                return;
            }
            if (Find.TickManager.TicksGame - game.week.startTick < Days * GenDate.TicksPerDay - GenDate.TicksPerHour) return;
            Send(game, map);
            game.week = WeekSnapshot.Take(game, map);
        }

        public static void Send(PrisonGame game, Map map)
        {
            WeekSnapshot w = game.week;
            int weekNo = game.weeksReported + 1;
            game.weeksReported = weekNo;
            var sb = new StringBuilder();
            int inmates = map.mapPawns.PrisonersOfColonySpawnedCount;
            sb.AppendLine("RP_Week_Population".Translate(inmates, Signed(inmates - w.inmates), game.stats.admitted - w.admitted, game.stats.released - w.released));
            int escaped = game.stats.escaped - w.escaped, deaths = game.stats.deaths - w.deaths, riots = game.stats.riots - w.riots;
            sb.AppendLine("RP_Week_Trouble".Translate(game.stats.misconducts - w.misconducts, riots, escaped, deaths));
            // The ones held down: shut out of programs and slow to calm (the trade-off of a quiet wing).
            if (Suppression.HeldByWing(DeskBoard.Compute(game, map).wings, out int held) is { } heldWings)
                sb.AppendLine(Suppression.ThumbLine(held, heldWings));
            // The levels the warden kept in with day orders more often than not.
            if (RegimeOrders.HabitLevels(game, map) is { Count: > 0 } habit)
                sb.AppendLine("RP_Week_Orders".Translate(string.Join(", ", habit.Select(h => "RP_Week_OrdersLevel".Translate(h.level.label, h.days).Resolve())), RegimeOrders.HabitDays, RegimeOrders.HabitOver));
            // Staff: headcount and who left (a snapshot from an older save has no staff count: skip the change).
            int staffNow = StaffCommute.AllStaff(map).Count;
            if (w.staff >= 0)
                sb.AppendLine("RP_Week_Staff".Translate(staffNow, Signed(staffNow - w.staff),
                    w.resigned >= 0 ? game.stats.resignations - w.resigned : game.stats.staffBurnedOut - w.burnedOut, game.stats.staffRetired - w.retired, game.stats.staffDismissed - w.dismissed));
            // Work and life: contracts, fires, food scares, mentorships, courses (only what happened).
            if (w.contractsMet >= 0)
            {
                var parts = new List<string>();
                int met = game.stats.contractsMet - w.contractsMet, missed = game.stats.contractsMissed - w.contractsMissed;
                if (met + missed > 0) parts.Add("RP_Week_Contracts".Translate(met, missed));
                Add(parts, "RP_Week_Fires", game.stats.fires - w.fires);
                Add(parts, "RP_Week_FoodScares", game.stats.foodScares - w.foodScares);
                Add(parts, "RP_Week_Mentorships", game.stats.mentorships - w.mentorships);
                Add(parts, "RP_Week_Trained", game.stats.staffTrained - w.staffTrained);
                Add(parts, "RP_Week_Remission", game.stats.remissionDays - w.remissionDays);
                if (parts.Count > 0) sb.AppendLine("RP_Week_Life".Translate(string.Join(" · ", parts)));
            }
            // Money: the ledger's day books for the week.
            int firstDay = Ledger.CurrentDay - Days + 1; // today's book opened at this midnight; seven books, no overlap
            List<DayBook> books = game.ledger.days.Where(d => d.day >= firstDay).ToList();
            int income = books.Sum(d => d.Income), expenses = books.Sum(d => d.Expenses);
            sb.AppendLine("RP_Week_Money".Translate(Money.Format(income), Money.Format(expenses), Money.Format(income - expenses), Money.Format(game.ledger.funds)));
            // What the grades are worth to the state's per-diem now (and when they start counting).
            if (PerDiem.WeekLine(game, map) is { } perDiem) sb.AppendLine(perDiem);
            if (w.grade >= 0f)
                sb.AppendLine("RP_Week_Grade".Translate(PrisonGrading.Letter(w.grade), PrisonGrading.Letter(game.lastGrade), game.reputation.ToString("F0"), Signed(UnityEngine.Mathf.RoundToInt(game.reputation - w.reputation))));
            // The goals the warden knows of that come due first (a word with the inmate before it is missed is worth most there).
            List<string> goals = NearestGoals(map, 2);
            if (goals.Count > 0) sb.AppendLine("RP_Week_Wants".Translate(string.Join("; ", goals)));
            // Headlines: the most important stories of the week, newest first among equals.
            List<ChronicleEntry> top = game.story.entries.Where(e => e.tick >= w.startTick && e.importance >= 2)
                .OrderByDescending(e => e.importance).ThenByDescending(e => e.tick).Take(Headlines).ToList();
            sb.AppendLine();
            if (top.Count > 0)
            {
                sb.AppendLine("RP_Week_Headlines".Translate());
                foreach (ChronicleEntry e in top) sb.AppendLine("  • " + e.text);
            }
            else sb.AppendLine("RP_Week_Quiet".Translate());
            // The inmate who kept the officers busiest.
            (Pawn worst, int worstCount) = BusiestInmate(map, w.startTick);
            if (worst != null && worstCount >= 2)
            {
                sb.AppendLine();
                sb.AppendLine("RP_Week_Worst".Translate(worst.LabelShort, worstCount));
            }
            Find.LetterStack.ReceiveLetter("RP_Week_Label".Translate(game.prisonName ?? "", weekNo), sb.ToString().TrimEnd(), LetterDefOf.NeutralEvent);
        }

        /// <summary>The goals still to be settled that the warden knows of (not secret, and none that would name a gang not found yet), the <paramref name="count"/> with the nearest deadline: "Marta - parole, slipping, 2 days left".</summary>
        public static List<string> NearestGoals(Map map, int count)
        {
            var found = new List<(int deadline, string line)>();
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c && Wants.Shown(c) is { Resolved: false } want)
                    found.Add((want.deadlineTick, "RP_Week_WantItem".Translate(p.LabelShort, Wants.Title(want), Wants.StatusLabel(want), System.Math.Max(0, (want.deadlineTick - now) / GenDate.TicksPerDay)).Resolve()));
            return found.OrderBy(f => f.deadline).Take(count).Select(f => f.line).ToList();
        }

        /// <summary>The inmate with the most records since <paramref name="sinceTick"/> (the ones an officer saw: what nobody saw is not on the warden's file, and the week's report must not name it), and how many.</summary>
        internal static (Pawn inmate, int count) BusiestInmate(Map map, int sinceTick)
        {
            Pawn worst = null;
            int worstCount = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { activeFlag: true } c) continue;
                int n = c.records.Count(r => r.seen && !r.def.mark && r.tick >= sinceTick);
                if (n > worstCount)
                {
                    worst = p;
                    worstCount = n;
                }
            }
            return (worst, worstCount);
        }

        private static string Signed(int n) => n > 0 ? "+" + n : n.ToString();

        private static void Add(List<string> parts, string key, int n)
        {
            if (n > 0) parts.Add(key.Translate(n));
        }
    }
}
