using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The warden's desk explains itself (<see cref="GradeDrivers"/>, <see cref="DeskBoard"/>, <see cref="MealTally"/>, the panel's counts): the card keeps the numbers it was graded on, the wing block and the
    /// kitchen line count what they say, and the panel's two counts move with the prison. Self-contained (throwaway inmates and staff, rolled back), run among the events plan's other checks.
    /// </summary>
    public static partial class AutoTest
    {
        private static void DeskFail(string what) => problems0.Add("warden's desk: " + what);

        private static void CheckDesk(Map map)
        {
            CheckDeskCardKeepsItsDrivers(map);
            CheckDeskWingBlock(map);
            CheckDeskKitchenLine(map);
            CheckDeskPanelCounts(map);
        }

        /// <summary>The inmates a card is worked out for: those not waiting for the shuttle to leave.</summary>
        private static List<Pawn> CardInmates(Map map) => map.mapPawns.PrisonersOfColonySpawned.Where(p => !ShuttleSystem.OnTheWayOut(p)).ToList();

        private static void CheckDeskCardKeepsItsDrivers(Map map)
        {
            Guarded("the report card keeps the numbers it was graded on", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                (List<float> history0, List<float> rules0, List<ReportCard> cards0, float lastGrade0) = (g.gradeHistory.ToList(), g.gradeRules.ToList(), g.gradeCards.ToList(), g.lastGrade);
                r.Add(() =>
                {
                    g.gradeHistory.Clear();
                    g.gradeHistory.AddRange(history0);
                    g.gradeRules.Clear();
                    g.gradeRules.AddRange(rules0);
                    g.gradeCards.Clear();
                    g.gradeCards.AddRange(cards0);
                    g.lastGrade = lastGrade0;
                });
                // Two throwaway inmates, one of them hurt, on a day with known incidents.
                Pawn hurt = Throwaway(map, r), well = Throwaway(map, r);
                hurt.health.AddHediff(HediffDefOf.Cut, hurt.RaceProps.body.corePart);
                (g.today.violent, g.today.deaths, g.today.escapes, g.today.contrabandSmuggled, g.today.contrabandFound, g.today.ringsBusted) = (3, 1, 2, 4, 2, 1);
                (g.today.complianceSamples, g.today.complianceSum) = (5, 3.5f);
                ReportCard card = PrisonGrading.Compute(map, g);

                // Each stored number against what it was counted from.
                List<Pawn> inmates = CardInmates(map);
                Dictionary<string, (float stored, float expected)> numbers = new Dictionary<string, (float, float)>
                {
                    ["violent"] = (card.violent, 3), ["deaths"] = (card.deaths, 1), ["escapes"] = (card.escapes, 2), ["smuggled"] = (card.contrabandSmuggled, 4), ["found"] = (card.contrabandFound, 2),
                    ["rings"] = (card.ringsBusted, 1), ["compliance samples"] = (card.complianceSamples, 5), ["compliance sum"] = (card.complianceSum, 3.5f), ["inmates"] = (card.inmates, inmates.Count),
                    ["injured"] = (card.injured, inmates.Count(p => p.health.HasHediffsNeedingTend() || p.Downed)),
                    ["employed"] = (card.employed, inmates.Count(p => p.Inmate() is { job: not null } w && Ledger.CurrentDay - w.lastWorkDay <= 1)),
                    ["reformed"] = (card.reformed, inmates.Count(p => p.Inmate() is { passedPrograms.Count: > 0 })),
                    ["mean mood"] = (card.meanMood, inmates.Sum(p => p.needs.mood?.CurLevelPercentage ?? 0.5f) / Mathf.Max(1, inmates.Count)),
                    ["parole share"] = (card.paroleShare, PrisonGrading.RecentParoleShare(g)),
                    ["released"] = (card.released, g.stats.released),
                    ["stayed out"] = (card.stayedOut, g.stats.released - g.supervision.OpenCount is var seen && seen > 0 ? 1f - Mathf.Min(g.stats.reoffended, seen) / (float)seen : 0.5f),
                };
                if (!card.detailed) DeskFail("a card worked out now is not marked as one that kept its numbers");
                if (card.injured < 1) DeskFail("the check's own premise: the cut inmate is not counted as hurt");
                foreach (KeyValuePair<string, (float stored, float expected)> kv in numbers)
                    if (Mathf.Abs(kv.Value.stored - kv.Value.expected) > 0.001f) DeskFail($"the card kept {kv.Key} as {kv.Value.stored}, it was counted as {kv.Value.expected}");

                // The areas are what those stored numbers make: the grade was worked out from them and from nothing else.
                float pc = Mathf.Max(5, card.inmates), n = Mathf.Max(1, card.inmates);
                (string area, float stored, float fromDrivers)[] areas =
                {
                    ("safety", card.safety, Mathf.Clamp(100f - 120f * card.violent / pc - 200f * card.deaths / pc, 0f, 100f)),
                    ("security", card.security, Mathf.Clamp(100f - 400f * card.escapes / pc - 30f * card.contrabandSmuggled / pc + 15f * Mathf.Min(card.contrabandFound, card.contrabandSmuggled) / pc
                                                            + PrisonGrading.RingBustBonus * Mathf.Min(2, card.ringsBusted), 0f, 100f)),
                    ("health", card.health, Mathf.Clamp(100f - 150f * card.injured / n - 240f * card.deaths / pc, 0f, 100f)),
                    ("conditions", card.conditions, Mathf.Clamp(card.meanMood * 130f - 5f, 0f, 100f)),
                    ("order", card.order, card.complianceSamples == 0 ? 75f : Mathf.Clamp(card.complianceSum / card.complianceSamples * 110f, 0f, 100f)),
                    ("reform", card.reform, Mathf.Clamp(25f + 40f * card.employed / n + 15f * card.reformed / n + 10f * card.paroleShare + 20f * card.stayedOut, 0f, 100f)),
                };
                foreach ((string area, float stored, float fromDrivers) in areas)
                    if (Mathf.Abs(stored - fromDrivers) > 0.01f) DeskFail($"{area} is {stored:F2} on the card, its stored drivers make {fromDrivers:F2}");

                // The tooltip's words: the violent count and the inmate count for safety, the change since the previous card, and nothing to compare with an old save's card.
                string line = GradeDrivers.Line("Safety", card, null);
                if (!line.Contains("3 violent incidents") || !line.Contains("1 death") || !line.Contains(card.inmates + " inmate") || line.Contains("since"))
                    DeskFail($"the safety tooltip reads \"{line}\" (3 violent incidents, 1 death and {card.inmates} inmates, with no change to tell, expected)");
                ReportCard before = new ReportCard { detailed = true, inmates = card.inmates, safety = card.safety + 8f };
                int moved = GradeDrivers.Shown(before.safety) - GradeDrivers.Shown(card.safety);
                string withPrevious = GradeDrivers.Line("Safety", card, before);
                if (!withPrevious.Contains($"down {moved}") || !withPrevious.Contains("since the last card") || !withPrevious.Contains("3 violent incidents"))
                    DeskFail($"with a previous card {moved} points higher the safety tooltip reads \"{withPrevious}\"");
                before.safety = card.safety - 5f;
                moved = GradeDrivers.Shown(card.safety) - GradeDrivers.Shown(before.safety);
                if (card.safety >= 5f && !GradeDrivers.Line("Safety", card, before).Contains($"up {moved}")) DeskFail($"with a previous card {moved} points lower the safety tooltip does not say up {moved}");
                if (GradeDrivers.Line("Safety", card, new ReportCard()).Contains("since")) DeskFail("a previous card with no detail was still compared with");
                foreach (string area in GradeDrivers.Areas)
                    if (!GradeDrivers.Line(area, card, null).Contains(("RP_Grade_" + area).Translate().ToString())) DeskFail($"the {area} tooltip does not name the area");

                // Stored with the day, found again by the history's index; an old save's days have cards that say there is no detail.
                g.gradeHistory.Clear();
                g.gradeRules.Clear();
                g.gradeCards.Clear();
                bool graded = DailySettlement.NoteGrade(g, card);
                if (!graded || g.CardAt(0) != card) DeskFail("the day's grade was not entered in the history with the card it was worked out from");
                g.gradeHistory.Clear();
                g.gradeRules.Clear();
                g.gradeCards.Clear();
                g.gradeHistory.AddRange(new[] { 50f, 58f, 62f });
                g.AlignGradeRules();
                if (g.gradeCards.Count != 3 || g.gradeCards.Any(c => c.detailed) || g.CardAt(1) == null || GradeDrivers.Past(g.CardAt(1)) != "RP_DeskNoDetail".Translate().ToString())
                    DeskFail("the days of a save from before the cards were kept do not show \"no detail\"");
                g.RecordGrade(70f, card);
                if (g.CardAt(3) != card || g.gradeCards.Count != 4 || g.CardAt(0) == card || !GradeDrivers.Past(g.CardAt(3)).Contains(PrisonGrading.Letter(card.safety))) DeskFail("a day recorded after an old save's days did not keep its card at its own place");
            });
        }

        /// <summary>The row of a security level in a count of the wings (null: it has none).</summary>
        private static DeskBoard.WingRow WingRowOf(List<DeskBoard.WingRow> rows, SecurityLevelDef level) => rows.Find(w => w.level == level);

        private static void CheckDeskWingBlock(Map map)
        {
            Guarded("the wing block counts each level that holds inmates", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                SecurityLevelDef medium = RPSecurityDefOf.RP_Medium, maximum = RPSecurityDefOf.RP_Maximum;
                PrisonZoneDef wing = DefDatabase<PrisonZoneDef>.AllDefsListForReading.FirstOrDefault(z => z.security == medium);
                if (wing == null) { DeskFail("the check's own premise: no wing zone belongs to the medium level"); return; }
                Dictionary<SecurityLevelDef, int> misconduct0 = new Dictionary<SecurityLevelDef, int>(g.today.misconductByLevel);
                r.Add(() =>
                {
                    g.today.misconductByLevel.Clear();
                    foreach (KeyValuePair<SecurityLevelDef, int> kv in misconduct0) g.today.misconductByLevel[kv.Key] = kv.Value;
                    DeskBoard.Reset();
                });
                DeskBoard.Reset();
                List<DeskBoard.WingRow> before = DeskBoard.Compute(g, map).wings;
                DeskBoard.WingRow mediumBefore = WingRowOf(before, medium), maximumBefore = WingRowOf(before, maximum);
                // (The day's misconduct per level, before the check: a level with no row now can have misconduct today all the same, by inmates who were in it this morning - a census run's
                // maximum row "moved" from 0 to 1 that way.)
                int mediumMisconduct0 = g.today.misconductByLevel.TryGetValue(medium, out int mm) ? mm : 0, maximumMisconduct0 = g.today.misconductByLevel.TryGetValue(maximum, out int xm) ? xm : 0;
                // A level that holds nobody has no row (any level the live prison has nobody in).
                SecurityLevelDef empty = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(l => l != medium && l != maximum && WingRowOf(before, l) == null);

                // Two medium inmates, one tense and one calm; one maximum inmate, calm.
                Pawn tense = Throwaway(map, r, medium), calm = Throwaway(map, r, medium), other = Throwaway(map, r, maximum);
                if (tense.Inmate().security != medium || calm.Inmate().security != medium || other.Inmate().security != maximum) { DeskFail("the check's own premise: the throwaways did not get the levels asked for"); return; }

                // Two guards standing in the medium wing, one at work and one on a break, and a third at work standing in no wing.
                int hour = GenLocalDate.HourOfDay(map);
                Pawn atWork = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), onBreak = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                atWork.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                onBreak.timetable.SetAssignment(hour, TimeAssignmentDefOf.Joy);
                foreach (Pawn guard in new[] { atWork, onBreak })
                {
                    IntVec3 cell = guard.Position;
                    PrisonZoneDef zone0 = pm.zones.ZoneAt(cell);
                    r.Add(() => pm.zones.Set(cell, zone0));
                    pm.zones.Set(cell, wing);
                }
                // The real path that counts a misconduct (nobody saw it: no sanction, no danger on the map), by a medium inmate.
                MisconductDef def = DefDatabase<MisconductDef>.AllDefsListForReading.First(d => !d.letter && !d.loud && d.severity < 3);
                MisconductUtility.Record(calm, calm.Inmate(), pm, def, chronicle: false, unseen: true);
                // (After the record: a misconduct takes some tension off.)
                (tense.Inmate().tension, calm.Inmate().tension, other.Inmate().tension) = (80f, 10f, 20f);

                (List<DeskBoard.WingRow> rows, _) = DeskBoard.Compute(g, map);
                DeskBoard.WingRow m = WingRowOf(rows, medium), x = WingRowOf(rows, maximum);
                if (m == null || x == null) { DeskFail($"a level holding inmates has no row (medium {m != null}, maximum {x != null})"); return; }
                List<Pawn> inMedium = CardInmates(map).Where(p => p.Inmate()?.security == medium).ToList();
                int guardsInMedium = map.mapPawns.FreeColonistsSpawned.Count(p => GuardUtility.IsGuard(p) && DeskBoard.OnDuty(p, hour) && pm.zones.ZoneAt(p.Position)?.security == medium);
                if (m.inmates != inMedium.Count || m.inmates != (mediumBefore?.inmates ?? 0) + 2) DeskFail($"the medium row counts {m.inmates} inmates ({inMedium.Count} are there, {(mediumBefore?.inmates ?? 0) + 2} expected)");
                if (m.tense != inMedium.Count(p => p.Inmate().tension >= 60f) || m.tense != (mediumBefore?.tense ?? 0) + 1) DeskFail($"the medium row counts {m.tense} tense ({(mediumBefore?.tense ?? 0) + 1} expected)");
                float meanMedium = inMedium.Sum(p => p.Inmate().tension) / inMedium.Count;
                if (Mathf.Abs(m.meanTension - meanMedium) > 0.01f || Mathf.Abs(m.meanTension - ((mediumBefore?.meanTension ?? 0f) * (mediumBefore?.inmates ?? 0) + 90f) / m.inmates) > 0.01f)
                    DeskFail($"the medium row's mean tension is {m.meanTension:F2} (the inmates' own mean is {meanMedium:F2})");
                if (m.misconduct != mediumMisconduct0 + 1) DeskFail($"the medium row counts {m.misconduct} misconducts today (one more than the {mediumMisconduct0} before expected: a misconduct was recorded)");
                if (m.guards != guardsInMedium || guardsInMedium < 1 || mediumBefore != null && m.guards != mediumBefore.guards + 1)
                    DeskFail($"the medium row counts {m.guards} guards (on duty and standing in the wing: {guardsInMedium}; one more than the {mediumBefore?.guards} before expected, the guard on a break not counted)");
                if (x.inmates != (maximumBefore?.inmates ?? 0) + 1 || x.tense != (maximumBefore?.tense ?? 0) || x.misconduct != maximumMisconduct0 || maximumBefore != null && x.guards != maximumBefore.guards)
                    DeskFail($"the maximum row ({x.inmates} inmates, {x.tense} tense, {x.misconduct} misconducts, {x.guards} guards) moved with the medium wing's changes");
                if (empty != null && WingRowOf(rows, empty) != null) DeskFail($"{empty.defName} holds nobody and has a row");
                if (rows.Select(w => w.level.uiOrder).SequenceEqual(rows.Select(w => w.level.uiOrder).OrderBy(i => i)) == false) DeskFail("the rows are not in the screens' order of levels");

                // Counted at most every few seconds: a change shows after the cache is forgotten and not before (the check runs no ticks).
                DeskBoard.Reset();
                int tenseThen = WingRowOf(DeskBoard.Current(g, map).wings, medium).tense;
                tense.Inmate().tension = 5f;
                int tenseCached = WingRowOf(DeskBoard.Current(g, map).wings, medium).tense;
                DeskBoard.Reset();
                int tenseFresh = WingRowOf(DeskBoard.Current(g, map).wings, medium).tense;
                if (tenseThen != m.tense || tenseCached != tenseThen || tenseFresh != tenseThen - 1) DeskFail($"the wing count is not kept between refreshes: {tenseThen} tense, then {tenseCached} after a change, then {tenseFresh} when counted again");
            });
        }

        private static void CheckDeskKitchenLine(Map map)
        {
            Guarded("the kitchen line counts meals cooked, eaten, waiting, hungry inmates and the cooks", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                r.Add(DeskBoard.Reset);
                Pawn inmate = Throwaway(map, r), staffEater = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor);
                inmate.needs.food.CurLevelPercentage = 0.9f;
                DeskBoard.KitchenLine before = DeskBoard.Compute(g, map).kitchen;

                // Cooked: a stove's bill that finishes (the game's own path: the patch on the bill counts it), a bulk bill's four meals, a bill that makes no meal.
                (int cooked0, int eaten0) = (g.today.mealsCooked, g.today.mealsEaten);
                Bill_Production Bill(string recipe) => new Bill_Production(DefDatabase<RecipeDef>.GetNamed(recipe)) { repeatMode = BillRepeatModeDefOf.Forever };
                Bill("CookMealSimple").Notify_IterationCompleted(staffEater, new List<Thing>());
                if (g.today.mealsCooked != cooked0 + 1) DeskFail($"a finished meal bill moved the day's meals cooked from {cooked0} to {g.today.mealsCooked}");
                Bill("CookMealSimpleBulk").Notify_IterationCompleted(staffEater, new List<Thing>());
                if (g.today.mealsCooked != cooked0 + 5) DeskFail($"a finished bulk meal bill left the day's meals cooked at {g.today.mealsCooked} (four more than the one before expected)");
                RecipeDef notAMeal = DefDatabase<RecipeDef>.AllDefsListForReading.First(rd => rd.products is { Count: > 0 } && rd.products.All(pr => !MealCounters.IsMeal(pr.thingDef)));
                new Bill_Production(notAMeal) { repeatMode = BillRepeatModeDefOf.Forever }.Notify_IterationCompleted(staffEater, new List<Thing>());
                if (g.today.mealsCooked != cooked0 + 5) DeskFail($"a bill that makes {notAMeal.defName} counted as meals cooked");
                if (DeskBoard.Compute(g, map).kitchen.cooked != g.today.mealsCooked) DeskFail("the kitchen line's meals cooked is not the day's count");

                // Eaten: an inmate eats a meal (the game's own path), a member of staff eats one, an inmate eats raw food.
                Thing Meal(Pawn at, string def)
                {
                    Thing t = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed(def));
                    GenPlace.TryPlaceThing(t, at.Position, map, ThingPlaceMode.Near);
                    r.Add(() =>
                    {
                        if (!t.Destroyed) t.Destroy();
                    });
                    return t;
                }
                Thing meal = Meal(inmate, "MealSimple");
                if (meal.Destroyed) { DeskFail("the check's own premise: the meal was merged into a stack"); return; }
                meal.Ingested(inmate, meal.GetStatValue(StatDefOf.Nutrition));
                if (g.today.mealsEaten != eaten0 + 1) DeskFail($"an inmate eating a meal moved the day's meals eaten from {eaten0} to {g.today.mealsEaten}");
                Thing staffMeal = Meal(staffEater, "MealSimple");
                staffMeal.Ingested(staffEater, staffMeal.GetStatValue(StatDefOf.Nutrition));
                Thing raw = Meal(inmate, "RawPotatoes");
                raw.Ingested(inmate, 0.1f);
                if (g.today.mealsEaten != eaten0 + 1) DeskFail($"a member of staff eating a meal or an inmate eating raw food moved the day's meals eaten to {g.today.mealsEaten}");
                if (DeskBoard.Compute(g, map).kitchen.eaten != g.today.mealsEaten) DeskFail("the kitchen line's meals eaten is not the day's count");

                // Hungry now: food under a quarter. On the counters: a meal on a serving counter's cell. Cooks: staff cooks at work, not on a break.
                inmate.needs.food.CurLevelPercentage = 0.1f;
                int hour = GenLocalDate.HourOfDay(map);
                Pawn cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook), cookOnBreak = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook), cookAtHome = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook, atHome: true);
                cook.timetable.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                cookOnBreak.timetable.SetAssignment(hour, TimeAssignmentDefOf.Joy);
                DeskBoard.KitchenLine after = DeskBoard.Compute(g, map).kitchen;
                if (after.hungry != before.hungry + 1) DeskFail($"an inmate whose food fell to 10% moved the hungry count from {before.hungry} to {after.hungry}");
                if (after.cooks != before.cooks + 1) DeskFail($"a cook put on duty (and two who are on a break and at home) moved the cooks from {before.cooks} to {after.cooks}");
                List<Building_Storage> counters = MealCounters.On(map);
                if (counters.Count > 0)
                {
                    Building_Storage counter = counters[0];
                    IntVec3 slot = counter.AllSlotCellsList()[0];
                    Thing onCounter = ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("MealSimple"));
                    GenSpawn.Spawn(onCounter, slot, map);
                    r.Add(() =>
                    {
                        if (!onCounter.Destroyed) onCounter.Destroy();
                    });
                    int waiting = counters.Sum(MealCounters.Meals);
                    if (DeskBoard.Compute(g, map).kitchen.onCounters != waiting || waiting != after.onCounters + 1) DeskFail($"a meal put on a serving counter moved the meals waiting from {after.onCounters} to {DeskBoard.Compute(g, map).kitchen.onCounters}");
                }
                else RPLog.Message("[RimPrison:test] the desk's kitchen line: no serving counter in this prison, the meals waiting were not exercised");
            });
        }

        private static void CheckDeskPanelCounts(Map map)
        {
            Guarded("the panel's tense count and open letters count move with the prison", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                c.tension = 10f;
                (int tense, int letters) = PrisonHUD.DeskCounts(map);
                c.tension = 59f;
                if (PrisonHUD.DeskCounts(map).openLetters != letters || PrisonHUD.DeskCounts(map).tense != tense) DeskFail("an inmate at tension 59 is counted as tense");
                c.tension = 60f;
                if (PrisonHUD.DeskCounts(map).tense != tense + 1) DeskFail($"an inmate made tense (tension 60) moved the panel's tense count from {tense} to {PrisonHUD.DeskCounts(map).tense}");
                c.tension = 95f;
                if (PrisonHUD.DeskCounts(map).tense != tense + 1) DeskFail("an inmate at tension 95 is not counted once");
                c.tension = 10f;
                if (PrisonHUD.DeskCounts(map).tense != tense) DeskFail("the tense count did not fall back when the inmate calmed down");

                // A decision letter on the stack, one answered, one held back for tomorrow.
                ChoiceLetter_PrisonDecision open = ChoiceLetter_PrisonDecision.Make("RP_Decision_Arc", "desk check", "desk check", map);
                Find.LetterStack.ReceiveLetter(open);
                if (PrisonHUD.DeskCounts(map).openLetters != letters + 1) DeskFail($"a decision letter on the stack moved the open letters from {letters} to {PrisonHUD.DeskCounts(map).openLetters}");
                ChoiceLetter_PrisonDecision answered = ChoiceLetter_PrisonDecision.Make("RP_Decision_Arc", "desk check", "desk check", map);
                answered.answered = true;
                Find.LetterStack.ReceiveLetter(answered);
                ChoiceLetter_PrisonDecision held = ChoiceLetter_PrisonDecision.Make("RP_Decision_Arc", "desk check", "desk check", map);
                g.story.deferredLetters.Add(held);
                r.Add(() => g.story.deferredLetters.Remove(held));
                if (PrisonHUD.DeskCounts(map).openLetters != letters + 1) DeskFail($"an answered letter or one held for tomorrow was counted among the open ones ({PrisonHUD.DeskCounts(map).openLetters}, {letters + 1} expected)");
                Find.LetterStack.RemoveLetter(open);
                if (PrisonHUD.DeskCounts(map).openLetters != letters) DeskFail("the open letters did not fall when the letter left the stack");
            });
        }
    }
}
