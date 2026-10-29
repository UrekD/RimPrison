using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Cells by conduct (Policy, default on): every cell has a grade 0-5 from its room's impressiveness, every inmate an entitlement 0-5 that clean days and passed programs raise and a seen
    /// misconduct cuts. <see cref="Term"/> is kept apart from the social fit (<see cref="Cellmates.Fit"/>): it breaks ties between equally good places for an arrival and decides the daily review's grade moves, which never make the social fit worse; the review moves inmates toward the cell they have earned, and away from one they have not.
    /// Grades are read from the per-room cache <see cref="PrisonMap.GradeOf"/> (worked out on the bed refresh walk), never from the room itself.
    /// </summary>
    public static class CellQuality
    {
        public const int MaxGrade = 5;
        /// <summary>Where a new arrival starts: the grade of a plain cell.</summary>
        public const int PlainCell = 1;
        /// <summary>Clean days (no seen misconduct of severity 2 or more) for one step up: the reclassification clock's own default (<see cref="SecurityLevelDef.promoteAfterCleanDays"/>).</summary>
        public const int StepDays = 5;
        /// <summary>What a seen misconduct of at least <see cref="SevereFrom"/> costs in entitlement.</summary>
        public const int FallBy = 2, SevereFrom = 2;
        /// <summary>The cost of each grade below the entitlement (an unearned upgrade: only when a better free cell exists), and of each grade above it (a demotion should happen: it costs more).
        /// This term is apart from the social fit (<see cref="Cellmates.Fit"/>) and never added to it: it picks between equally good places and decides the grade moves, which may not make the social fit worse.</summary>
        public const float PerGradeBelow = 1f, PerGradeAbove = 1.5f;
        /// <summary>The review looks at an inmate for a grade move once the term is this bad (two grades below the entitlement, or two above it), and moves them only to a cell whose term is better by <see cref="MoveBetterBy"/>
        /// (one grade-step's worth: <see cref="PerGradeBelow"/>, the smaller step).</summary>
        public const float MoveAt = 2f, MoveBetterBy = PerGradeBelow;
        /// <summary>An arrival picks by the social fit; among the beds within this of the best of it, the cell's grade decides.</summary>
        public const float SocialBand = 1f;
        /// <summary>Days an inmate moved for their grade is left alone for it (see <see cref="CompInmate.gradeMovedTick"/>).</summary>
        public const int MoveCooldownDays = 3;
        /// <summary>A grade move needs the mismatch at two daily reviews in a row: the first review's is kept as <see cref="CompInmate.gradeMismatchTick"/>, and the next one counts it when it is no more than this long after
        /// (the daily pass is not on the tick) and not the same pass (at least half a day).</summary>
        /// <summary>No tick yet (the grade move's and the mismatch's marks).</summary>
        public const int NoTick = int.MinValue;

        public const int ConsecutiveFrom = GenDate.TicksPerDay / 2, ConsecutiveTo = GenDate.TicksPerDay * 3 / 2;
        public const int Unset = -999999;

        /// <summary>
        /// The grade of an impressiveness score: vanilla's own stage of it (awful below 20 = 0, dull 20 = 1, mediocre 30 = 2, decent 40 = 3, slightly impressive 50 = 4, somewhat impressive 65 and up = 5), so the grade
        /// reads the same as the room's label in the game. Impressiveness grows with space, beauty, wealth and cleanliness together, held down by whichever is lowest, and a small room is held down by its space
        /// (<c>RoomStatWorker_Impressiveness</c>): a bare 1x2 cell should land well under 20 (grade 0), a room with a lamp, a rug and a decent floor and some space in the 20s and 30s (1-2, 3 with more), and a roomy,
        /// furnished cell 50 and over (4-5). (Read off the formula, not measured in a game.)
        /// </summary>
        public static int GradeOf(float impressiveness) => Mathf.Clamp(RoomStatDefOf.Impressiveness.GetScoreStageIndex(impressiveness), 0, MaxGrade);

        /// <summary>The grade of a room as it is now. Walks the room's stats: for the bed refresh, not for a query (see <see cref="PrisonMap.GradeOf"/>).</summary>
        public static int GradeOf(Room room) => room == null ? 0 : GradeOf(room.GetStat(RoomStatDefOf.Impressiveness));

        /// <summary>The highest grade a room that sleeps this many can have: a shared cell is not a better one for its size (a dormitory's space made its impressiveness a 5, and every inmate in it lived "above
        /// what they had earned"). Three or more: <see cref="DormCap"/>; two: <see cref="DoubleCap"/>.</summary>
        public const int DormCap = 2, DoubleCap = 4;

        public static int CapForSleepers(int grade, int sleepers) => sleepers >= 3 ? Mathf.Min(grade, DormCap) : sleepers == 2 ? Mathf.Min(grade, DoubleCap) : grade;

        /// <summary>A cell's grade: its impressiveness's stage, capped by how many it sleeps.</summary>
        public static int GradeFor(Room room)
        {
            if (room == null) return 0;
            int sleepers = 0;
            foreach (Building_Bed b in room.ContainedBeds)
                if (b.ForPrisoners && !b.Medical) sleepers += b.SleepingSlotsCount;
            return CapForSleepers(GradeOf(room), sleepers);
        }

        /// <summary>Whether the policy is on and this inmate is under the cell ladder: not in protective custody, psychiatric care, supermax or on death row (their own rules decide where they live).</summary>
        public static bool Applies(CompInmate c) => PrisonGame.Instance is { cellsByConduct: true } && UnderLadder(c);

        /// <summary>An inmate whose cell is chosen by the ladder when the policy is on: not in protective custody, psychiatric care, supermax or on death row.</summary>
        public static bool UnderLadder(CompInmate c) =>
            c?.security != null && !Trials.IsProtected(c) && !PsychWing.IsIn(c) && !SupermaxSystem.IsIn(c) && DeathRowSystem.Ext(c) == null;

        /// <summary>The cost of living in this grade with this entitlement (0 or below).</summary>
        public static float TermOf(int grade, int entitlement) => grade >= entitlement ? -PerGradeAbove * (grade - entitlement) : -PerGradeBelow * (entitlement - grade);

        /// <summary>The cell's term for this inmate (see <see cref="PerGradeAbove"/>): 0 with the policy off, for an inmate the ladder does not cover, a solitary or padded bed, or a room not graded yet.</summary>
        public static float Term(Building_Bed bed, CompInmate c)
        {
            if (bed == null || !Applies(c) || PaddedCells.IsPadded(bed) || PunishmentUtility.IsSolitaryBed(bed)) return 0f;
            int grade = PrisonMap.Of(bed.Map)?.GradeOf(bed.GetRoom()) ?? -1;
            return grade < 0 ? 0f : TermOf(grade, c.entitlement);
        }

        /// <summary>The grade of the cell this bed is in, or -1 when it has none yet.</summary>
        public static int GradeAt(Building_Bed bed) => bed == null ? -1 : PrisonMap.Of(bed.Map)?.GradeOf(bed.GetRoom()) ?? -1;

        // ---- the entitlement ------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A seen misconduct of severity 2 or more: down by <see cref="FallBy"/> at once, and the clean days start again.</summary>
        public static void Notify_Misconduct(CompInmate c, MisconductDef def, bool seen)
        {
            if (!seen || def.severity < SevereFrom) return;
            c.entitlement = Mathf.Max(0, c.entitlement - FallBy);
            c.entitlementTick = Find.TickManager.TicksGame;
        }

        /// <summary>A passed program counts as a step (the clean days run on).</summary>
        public static void Notify_ProgramPassed(CompInmate c) => c.entitlement = Mathf.Min(MaxGrade, c.entitlement + 1);

        /// <summary>Whether the clean days since the clock last started are enough for a step (with an hour of slack: the daily pass is not on the tick).</summary>
        private static bool StepDue(CompInmate c, int now) => now - c.entitlementTick + GenDate.TicksPerHour >= StepDays * GenDate.TicksPerDay;

        /// <summary>Midnight, before the cell review: a step up for every <see cref="StepDays"/> clean days. The clock of a new arrival (or of an old save) starts at its first day here.</summary>
        public static void Daily(Map map)
        {
            int now = Find.TickManager.TicksGame;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { activeFlag: true } c) continue;
                if (c.entitlementTick == Unset)
                {
                    c.entitlementTick = now;
                    continue;
                }
                if (!StepDue(c, now)) continue;
                c.entitlementTick = now;
                c.entitlement = Mathf.Min(MaxGrade, c.entitlement + 1);
            }
        }

        /// <summary>Whole clean days still to go for the next step (at least 1; the full <see cref="StepDays"/> before the clock has started).</summary>
        public static int DaysToNext(CompInmate c, int now) =>
            c.entitlementTick == Unset ? StepDays : Mathf.Clamp(Mathf.CeilToInt((StepDays * GenDate.TicksPerDay - (now - c.entitlementTick)) / (float)GenDate.TicksPerDay), 1, StepDays);

        /// <summary>The last misconduct the staff saw of severity 2 or more, no longer ago than <paramref name="withinDays"/> days; null if none.</summary>
        public static MisconductRecord RecentSevere(CompInmate c, int now, int withinDays)
        {
            MisconductRecord found = null;
            foreach (MisconductRecord r in c.records)
                if (r.seen && r.def.severity >= SevereFrom && now - r.tick <= withinDays * GenDate.TicksPerDay && (found == null || r.tick > found.tick)) found = r;
            return found;
        }

        /// <summary>Days without a seen serious misconduct (since they came, if there was none).</summary>
        public static int CleanDays(CompInmate c, int now)
        {
            int last = int.MinValue / 2;
            foreach (MisconductRecord r in c.records)
                if (r.seen && r.def.severity >= SevereFrom && r.tick > last) last = r.tick;
            return Mathf.Min(Mathf.FloorToInt(c.DaysServed), (now - last) / GenDate.TicksPerDay);
        }

        // ---- the screens ----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The inmate file's line: what they are entitled to, where they live, and what earns the next step.</summary>
        public static (string, Color) FileLine(CompInmate c, int cellGrade, int now)
        {
            string next = c.entitlement >= MaxGrade ? "RP_FileEntitlementTop".Translate().ToString() : "RP_FileEntitlementNext".Translate(DaysToNext(c, now), c.entitlement + 1).ToString();
            string text = "RP_FileEntitlement".Translate(c.entitlement, cellGrade < 0 ? "?" : cellGrade.ToString(), next);
            Color color = cellGrade < 0 ? PrisonHUD.Muted : cellGrade == c.entitlement ? PrisonHUD.Good : Mathf.Abs(cellGrade - c.entitlement) >= 2 ? PrisonHUD.Warn : PrisonHUD.Muted;
            return (text, color);
        }
    }
}
