using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    public static partial class AutoTest
    {
        // The medical emergency, the food scare, the export contract, mentoring, staff morale, the kitchen fire, staff training and the staff room. (the shared state is in EventsRun.cs)
        private sealed partial class EventsRun
        {
            private void EvMedicalEmergency()
            {
                // Medical emergency: paramedics stabilise; a heart-attack death after they were refused is neglect.
                int before = g.stats.medicalEmergencies;
                DefDatabase<IncidentDef>.GetNamed("RP_MedicalEmergency").Worker.TryExecute(new IncidentParms { target = map });
                var ml = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Medical"));
                Pawn heart = ml?.pawns.FirstOrDefault();
                if (g.stats.medicalEmergencies != before + 1 || heart == null || !heart.health.hediffSet.HasHediff(IncidentWorker_MedicalEmergency.HeartAttack))
                    problems0.Add("no heart attack in a medical emergency");
                else
                {
                    ml.answered = true;
                    PrisonDecisions.Choose(ml, 0);
                    Find.LetterStack.RemoveLetter(ml);
                    if (heart.health.hediffSet.HasHediff(IncidentWorker_MedicalEmergency.HeartAttack)) problems0.Add("the paramedics did not stabilise a heart attack");
                    Hediff ha = HediffMaker.MakeHediff(IncidentWorker_MedicalEmergency.HeartAttack, heart);
                    int inquiries = g.stats.inquiries;
                    heart.Inmate().careRefusedTick = -1;
                    DeathAftermath.Notify_Died(heart, null, ha, null);
                    if (g.stats.inquiries != inquiries) problems0.Add("a heart attack with paramedics called opened a neglect inquiry");
                    heart.Inmate().careRefusedTick = Find.TickManager.TicksGame;
                    DeathAftermath.Notify_Died(heart, null, ha, null);
                    if (g.stats.inquiries != inquiries + 1) problems0.Add("a heart-attack death after refusing paramedics was not treated as neglect");
                    heart.Inmate().careRefusedTick = -1;
                    foreach (var iq in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_Inquiry")).ToList())
                    {
                        iq.answered = true;
                        PrisonDecisions.Choose(iq, 0);
                        Find.LetterStack.RemoveLetter(iq);
                    }
                }
            }

            private void EvFoodScare()
            {
                // Food scare: inmates fall ill; carrying on sets up a second wave, a kitchen scrub clears the filth and
                // calls the wave off, standing the crew down takes their kitchen jobs.
                {
                    int Sick() => map.mapPawns.PrisonersOfColonySpawned.Count(x => x.health.hediffSet.HasHediff(HediffDefOf.FoodPoisoning));
                    void Dirty()
                    {
                        int spots = System.Math.Max(FoodScare.DirtyKitchen, FoodScare.Kitchen(map).Count / FoodScare.CellsPerFilth + 1);
                        foreach (IntVec3 cell in FoodScare.Kitchen(map).Where(x => x.Standable(map)).Take(spots))
                            FilthMaker.TryMakeFilth(cell, map, ThingDefOf.Filth_Dirt);
                    }
                    Dirty();
                    int scaresBefore = g.stats.foodScares, sickBefore = Sick();
                    // (Not the downed or those in the hole: a run with many in solitary may have fewer than two to poison.)
                    int eligibleToPoison = map.mapPawns.PrisonersOfColonySpawned.Count(x => InmateUtility.IsActiveInmate(x) && !x.Downed && x.Inmate().forcedActivity == null
                                                                                          && !x.health.hediffSet.HasHediff(HediffDefOf.FoodPoisoning));
                    if (eligibleToPoison == 0) RPLog.Message("[RimPrison:test] food scare: skipped (every inmate is already ill, downed or confined)");
                    bool fired = eligibleToPoison > 0 && DefDatabase<IncidentDef>.GetNamed("RP_FoodScare").Worker.TryExecute(new IncidentParms { target = map });
                    var fs = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_FoodScare"));
                    if (eligibleToPoison > 0 && (!fired || fs == null || g.stats.foodScares != scaresBefore + 1 || Sick() < sickBefore + System.Math.Min(2, eligibleToPoison)))
                        problems0.Add($"a food scare made {Sick() - sickBefore} inmates ill (fired={fired}, letter={fs != null})");
                    else if (eligibleToPoison > 0)
                    {
                        fs.answered = true;
                        PrisonDecisions.Choose(fs, 2);
                        Find.LetterStack.RemoveLetter(fs);
                        if (g.story.foodScareTick < 0 || g.story.foodScareChance < 0.59f)
                            problems0.Add($"carrying on in a dirty kitchen set up a second wave at {g.story.foodScareChance:P0} (expected 60%)");
                        g.story.foodScareTick = Find.TickManager.TicksGame;
                        g.story.foodScareChance = 1f;
                        // Everyone well again first (the plan's own run of every event may have made them all ill).
                        foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned)
                            if (x.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.FoodPoisoning) is { } fp) x.health.RemoveHediff(fp);
                        int sickMid = Sick();
                        // Not yet due: nothing happens.
                        g.story.foodScareTick = Find.TickManager.TicksGame + 1000;
                        FoodScare.Hourly(g, map);
                        if (Sick() != sickMid || g.story.foodScareTick < 0) problems0.Add("a second wave of food poisoning came before it was due");
                        g.story.foodScareTick = Find.TickManager.TicksGame;
                        FoodScare.Hourly(g, map);
                        if (Sick() <= sickMid || g.story.foodScareTick >= 0) problems0.Add("no second wave of food poisoning when one was due");
                        // Due but the dice say no: no one ill, and nothing left pending.
                        g.story.foodScareTick = Find.TickManager.TicksGame;
                        g.story.foodScareChance = 0f;
                        int sickLate = Sick();
                        FoodScare.Hourly(g, map);
                        if (Sick() != sickLate || g.story.foodScareTick >= 0) problems0.Add("a second wave that missed its roll still made inmates ill or stayed pending");
                        Dirty();
                        g.story.foodScareTick = Find.TickManager.TicksGame + 1000;
                        int fundsScrub = g.ledger.funds;
                        var scrub = ChoiceLetter_PrisonDecision.Make("RP_Decision_FoodScare", "test", "test", map);
                        scrub.answered = true;
                        PrisonDecisions.Choose(scrub, 0);
                        if (FoodScare.KitchenFilth(map) != 0 || g.story.foodScareTick >= 0 || g.ledger.funds != fundsScrub - IncidentWorker_FoodScare.ScrubCost)
                            problems0.Add($"the kitchen scrub left {FoodScare.KitchenFilth(map)} filth, a second wave due at {g.story.foodScareTick} and cost {fundsScrub - g.ledger.funds}");
                        // A clean kitchen halves the odds; standing the crew down halves them again.
                        if (!UnityEngine.Mathf.Approximately(FoodScare.SecondWaveChance(map, false), 0.3f) || !UnityEngine.Mathf.Approximately(FoodScare.SecondWaveChance(map, true), 0.15f))
                            problems0.Add($"second wave odds in a clean kitchen {FoodScare.SecondWaveChance(map, false):P0} / stood down {FoodScare.SecondWaveChance(map, true):P0} (expected 30% / 15%)");
                        // Stood down: the crew loses the kitchen and nobody else gets it until the day is over; then it reopens.
                        InmateJobDef kitchenJob = DefDatabase<InmateJobDef>.GetNamed("RP_JobKitchen");
                        // (One the kitchen job is open to in every way but the course and the stand-down: the job is minimum-security work, and a calm inmate.)
                        // (The live prison may have nobody calm and free at this moment: a level the job takes is enough, and the cook is calmed and freed for the check.)
                        if ((map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true, jobLocked: false } c && LaborUtility.Eligible(c, kitchenJob, forProgram: true))
                             ?? map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true, jobLocked: false } c && c.security != null
                                 && (kitchenJob.allowedSecurity.Count == 0 || kitchenJob.allowedSecurity.Contains(c.security)))) is { } cook)
                        {
                            float cookTensionWas = cook.Inmate().tension;
                            RegimeActivityDef cookForcedWas = cook.Inmate().forcedActivity;
                            cook.Inmate().tension = 0f;
                            cook.Inmate().forcedActivity = null;
                            InmateJobDef cookJobWas = cook.Inmate().job;
                            // (The kitchen job asks for kitchen safety: the test cook has passed it, so that it is the stand-down that takes the job from them and not the missing course. They keep the job before it.)
                            ProgramDef safetyCourse = RPVocationDefOf.RP_KitchenSafety;
                            bool safetyWas = cook.Inmate().passedPrograms.Contains(safetyCourse);
                            cook.Inmate().passedPrograms.Add(safetyCourse);
                            cook.Inmate().job = kitchenJob;
                            LaborUtility.AssignJobs(map);
                            if (cook.Inmate().job != kitchenJob) problems0.Add("the stand-down check's cook, trained for the kitchen, lost the kitchen job before any stand-down");
                            var down = ChoiceLetter_PrisonDecision.Make("RP_Decision_FoodScare", "test", "test", map);
                            down.answered = true;
                            PrisonDecisions.Choose(down, 1);
                            LaborUtility.AssignJobs(map);
                            int cooks = map.mapPawns.PrisonersOfColonySpawned.Count(x => x.Inmate()?.job == kitchenJob);
                            if (cook.Inmate().job == kitchenJob || cooks > 0 || cook.Inmate().jobLocked)
                                problems0.Add($"standing the kitchen crew down left {cooks} inmates in the kitchen (locked={cook.Inmate().jobLocked})");
                            if (!safetyWas) cook.Inmate().passedPrograms.Remove(safetyCourse);
                            cook.Inmate().job = cookJobWas;
                            cook.Inmate().tension = cookTensionWas;
                            cook.Inmate().forcedActivity = cookForcedWas;
                            g.story.kitchenStandDownUntilTick = Find.TickManager.TicksGame - 1;
                            if (FoodScare.StoodDown(kitchenJob)) problems0.Add("the kitchen stayed closed to inmates after the stand-down ended");
                        }
                        else problems0.Add("no inmate the kitchen job is open to, to test the kitchen stand-down with");
                        g.story.foodScareTick = -1;
                        g.story.kitchenStandDownUntilTick = -1;
                    }
                }
            }

            private void EvExportContract()
            {
                // Export contract: none for a prison that sells no plates; the offer is a third over the recent rate; signed, plates sold in the morning export run count,
                // and the full count pays the bonus; a missed deadline costs the penalty.
                {
                    int fundsWas = g.ledger.funds;
                    float repWas = g.reputation;
                    // (The workshop's own plates waiting in the Exports zone for the morning run are sold with the contract's: "136 delivered" of 130. They are set aside, and put back after.)
                    var realPlates = PrisonMap.Of(map).zones.CellsOf(DefDatabase<PrisonZoneDef>.GetNamed("RP_Exports")).SelectMany(x => x.GetThingList(map)).Where(x => x.def == ExportContracts.Plates).Select(x => (thing: x, at: x.Position)).ToList();
                    foreach ((Thing thing, IntVec3 _) in realPlates) thing.DeSpawn();
                    g.story.contractTarget = 0;
                    foreach (var old in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_ExportContract")).ToList())
                    {
                        old.answered = true;
                        Find.LetterStack.RemoveLetter(old);
                    }
                    float rateWas = g.story.platesPerDay;
                    g.story.platesPerDay = 0f;
                    if (DefDatabase<IncidentDef>.GetNamed("RP_ExportContract").Worker.TryExecute(new IncidentParms { target = map }))
                        problems0.Add("an export contract was offered to a prison that sells no plates");
                    // A stockpile sold off at once counts for at most twice the rate (from 0: 20, so the rate is 6); a second
                    // map's run the same day adds to it (+3) without decaying it again.
                    g.story.lastExportRunDay = -1;
                    ExportContracts.Notify_ExportRun(g, 100);
                    ExportContracts.Notify_ExportRun(g, 10);
                    if (!UnityEngine.Mathf.Approximately(g.story.platesPerDay, 9f)) problems0.Add($"a 100-plate dump and a second run of 10 the same day made the rate {g.story.platesPerDay:F1} (expected 9)");
                    g.story.platesPerDay = 40f;
                    int presses = 40;
                    DefDatabase<IncidentDef>.GetNamed("RP_ExportContract").Worker.TryExecute(new IncidentParms { target = map });
                    var cl = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_ExportContract"));
                    IntVec3 exportCell = PrisonMap.Of(map).zones.CellsOf(DefDatabase<PrisonZoneDef>.GetNamed("RP_Exports")).FirstOrDefault(x => x.Standable(map) && x.GetFirstItem(map) == null);
                    if (cl == null || cl.amount != 260 || !exportCell.IsValid)
                        problems0.Add($"no 260-plate export contract offered at 40 plates a day (letter={cl != null}, amount={cl?.amount}, exports cell={exportCell.IsValid})");
                    else
                    {
                        cl.answered = true;
                        PrisonDecisions.Choose(cl, 0);
                        Find.LetterStack.RemoveLetter(cl);
                        int target = g.story.contractTarget, met = g.stats.contractsMet;
                        if (target != cl.amount || g.story.contractRunsLeft != ExportContracts.Days || g.story.contractDueTick <= Find.TickManager.TicksGame)
                            problems0.Add($"signing the export contract set target {target}, {g.story.contractRunsLeft} runs, backstop {g.story.contractDueTick}");
                        void Ship(int n)
                        {
                            // Full stacks on free cells of the Exports zone, then the morning run.
                            foreach (IntVec3 cell in PrisonMap.Of(map).zones.CellsOf(DefDatabase<PrisonZoneDef>.GetNamed("RP_Exports")).Where(x => x.Standable(map) && x.GetFirstItem(map) == null).ToList())
                            {
                                if (n <= 0) break;
                                Thing plates = ThingMaker.MakeThing(ExportContracts.Plates);
                                plates.stackCount = System.Math.Min(n, ExportContracts.Plates.stackLimit);
                                n -= plates.stackCount;
                                GenSpawn.Spawn(plates, cell, map);
                            }
                            g.story.lastExportRunDay = -1; // a new morning
                            LaborUtility.SellExports(map, g);
                        }
                        Ship(target / 2);
                        if (g.story.contractDelivered != target / 2 || g.stats.contractsMet != met) problems0.Add($"half a contract shipped: {g.story.contractDelivered} delivered, met {g.stats.contractsMet - met}");
                        Ship(target - target / 2);
                        int bonus = ExportContracts.Bonus(target);
                        if (g.stats.contractsMet != met + 1 || g.story.contractTarget != 0 || !g.ledger.recent.Any(e => e.amount == bonus && e.category == RPDefOf.RP_ExportSales))
                            problems0.Add($"a delivered export contract paid no {bonus} bonus (met {g.stats.contractsMet - met}, target left {g.story.contractTarget})");
                        // The deadline is the fifth export run: the plates of the last run still count, and a run short misses.
                        void Mornings(int runs, int plates)
                        {
                            for (int i = 0; i < runs; i++)
                            {
                                g.story.lastExportRunDay = -1;
                                ExportContracts.Notify_ExportRun(g, i == runs - 1 ? plates : 0);
                            }
                        }
                        int missed = g.stats.contractsMissed, metBefore = g.stats.contractsMet;
                        ExportContracts.Sign(g.story, 50);
                        Mornings(ExportContracts.Days, 50);
                        if (g.stats.contractsMet != metBefore + 1 || g.stats.contractsMissed != missed) problems0.Add("plates sold at the last export run of a contract did not count");
                        ExportContracts.Sign(g.story, 50);
                        Mornings(ExportContracts.Days - 1, 0);
                        if (g.story.contractTarget == 0) problems0.Add($"an export contract was missed with a run left");
                        int fundsBeforeMiss = g.ledger.funds;
                        float repBeforeMiss = g.reputation;
                        Mornings(1, 49);
                        if (g.stats.contractsMissed != missed + 1 || g.story.contractTarget != 0 || g.ledger.funds != fundsBeforeMiss - ExportContracts.Penalty(50)
                            || g.reputation > repBeforeMiss - ExportContracts.ReputationCost + 0.01f)
                            problems0.Add($"a missed export contract: missed {g.stats.contractsMissed - missed}, cost {fundsBeforeMiss - g.ledger.funds} (expected {ExportContracts.Penalty(50)}), rep {repBeforeMiss:F1} -> {g.reputation:F1}");
                        // No export runs at all (the Exports zone went): the backstop ends it.
                        ExportContracts.Sign(g.story, 50);
                        g.story.contractDueTick = Find.TickManager.TicksGame;
                        ExportContracts.Hourly(g);
                        if (g.stats.contractsMissed != missed + 2 || g.story.contractTarget != 0) problems0.Add("a contract with no export runs never ended");
                        RPLog.Message($"[RimPrison:test] export contract: {target} plates at {presses} a day, bonus {bonus}, penalty on 50 {ExportContracts.Penalty(50)}");
                    }
                    g.ledger.funds = fundsWas;
                    g.reputation = repWas;
                    g.story.platesPerDay = rateWas;
                    g.story.lastExportRunDay = -1;
                    foreach ((Thing thing, IntVec3 at) in realPlates) GenPlace.TryPlaceThing(thing, at, map, ThingPlaceMode.Near);
                }
            }

            private void EvMentor()
            {
                // Mentor: an older calm inmate a while inside and a newcomer at the same level; let it happen and the
                // newcomer calms near them; the ten days or the mentor leaving ends it; a mentor in a gang may pull the
                // newcomer in; kept apart, the old hand takes it badly and is not offered again for a while.
                {
                    int now = Find.TickManager.TicksGame;
                    // The plan runs on day one: nobody can have five days inside yet. Mentorships the run already made are
                    // set aside (they can use up every pair), and put back after.
                    var mentorshipsWas = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate()?.mentorId >= 0)
                        .ToDictionary(x => x.Inmate(), x => (x.Inmate().mentorId, x.Inmate().mentorName, x.Inmate().mentorUntilTick));
                    foreach (CompInmate mc0 in mentorshipsWas.Keys) Mentors.Clear(mc0);
                    float minDaysWas = Mentors.MentorMinDays;
                    Mentors.MentorMinDays = 0f;
                    Pawn oldHand = null;
                    long ageWas = 0;
                    float tensionWas = 0f;
                    int admittedWas = 0, storyWas = 0;
                    List<MisconductRecord> recordsWas = null;
                    foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null))
                    {
                        CompInmate xc = x.Inmate();
                        // The plan's run of every event leaves most inmates with a fresh record: set it aside for the old hand.
                        (ageWas, tensionWas, admittedWas, storyWas, recordsWas) = (x.ageTracker.AgeBiologicalTicks, xc.tension, xc.admittedTick, xc.lastStoryTick, xc.records);
                        x.ageTracker.AgeBiologicalTicks = 45L * GenDate.TicksPerYear;
                        xc.tension = 5f;
                        xc.admittedTick = 0;
                        xc.lastStoryTick = -999999;
                        xc.records = new List<MisconductRecord>();
                        if (Mentors.CanMentor(x) && map.mapPawns.PrisonersOfColonySpawned.Any(y => Mentors.Fits(x, y)))
                        {
                            oldHand = x;
                            break;
                        }
                        (x.ageTracker.AgeBiologicalTicks, xc.tension, xc.admittedTick, xc.lastStoryTick, xc.records) = (ageWas, tensionWas, admittedWas, storyWas, recordsWas);
                    }
                    // (Only a failure when two settled, active inmates share a level: else there is honestly no pair.)
                    bool pairPossible = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && !x.Downed && !x.InMentalState && x.Inmate().forcedActivity == null && DeathRowSystem.Ext(x.Inmate()) == null)
                        .GroupBy(x => x.Inmate().security).Any(grp => grp.Count() >= 2);
                    if (oldHand == null && !pairPossible) RPLog.Message("[RimPrison:test] mentor: skipped (no two settled, active inmates at one level)");
                    else if (oldHand == null)
                        problems0.Add("mentor test: no inmate could be made an old hand with a newcomer at their level ("
                                      + string.Join(", ", map.mapPawns.PrisonersOfColonySpawned.Select(x => $"{x.LabelShort}:{x.Inmate()?.security?.defName}/{x.Inmate()?.forcedActivity?.defName}/{(x.Downed ? "downed" : "")}{(x.InMentalState ? "mental" : "")}")) + ")");
                    else
                    {
                        CompInmate oc = oldHand.Inmate();
                        // Not from the hole.
                        oc.forcedActivity = RPDefOf.RP_Lockup;
                        if (Mentors.CanMentor(oldHand)) problems0.Add("an inmate confined to their cell could mentor");
                        oc.forcedActivity = null;
                        foreach (var old in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_Mentor")).ToList())
                        {
                            old.answered = true;
                            Find.LetterStack.RemoveLetter(old);
                        }
                        DefDatabase<IncidentDef>.GetNamed("RP_Mentor").Worker.TryExecute(new IncidentParms { target = map });
                        var ml2 = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Mentor"));
                        Pawn mentor = ml2?.pawns.ElementAtOrDefault(0), newcomer = ml2?.pawns.ElementAtOrDefault(1);
                        // The prepared old hand, or anyone else who qualifies on their own: either way a same-level pair.
                        if (mentor?.Inmate() == null || newcomer?.Inmate()?.security != mentor.Inmate().security)
                            problems0.Add($"no mentor letter for an older, calm inmate (letter={ml2 != null}, cast {mentor?.LabelShort})");
                        else
                        {
                            ml2.answered = true;
                            PrisonDecisions.Choose(ml2, 0);
                            Find.LetterStack.RemoveLetter(ml2);
                            CompInmate nc = newcomer.Inmate();
                            if (nc.mentorId != mentor.thingIDNumber || nc.mentorUntilTick <= now
                                || !newcomer.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPMentorDefOf.RP_Mentored))
                                problems0.Add($"letting a mentorship happen did not start it (mentor {nc.mentorId} vs {mentor.thingIDNumber})");
                            if (Mentors.CanMentor(mentor) || Mentors.CanBeMentored(newcomer) || Mentors.CanBeMentored(mentor))
                                problems0.Add("a mentor or their newcomer could be cast in a second mentorship");
                            if (!Mentors.Together(newcomer, mentor) && Mentors.Relief(newcomer, nc) > 0f) problems0.Add("a mentor away from their newcomer calmed them");
                            if (CellFinder.TryFindRandomReachableNearbyCell(mentor.Position, map, 2, TraverseParms.For(mentor), x => x.Standable(map) && x.GetRoom(map) == mentor.GetRoom(), null, out IntVec3 near))
                            {
                                newcomer.pather.StopDead();
                                newcomer.Position = near;
                                newcomer.Notify_Teleported();
                                if (Mentors.Relief(newcomer, nc) <= 0f) problems0.Add("a mentor right next to their newcomer did not calm them");
                            }
                            // A mentor in a gang, at the same level, draws the newcomer in sooner or later (fixed seeds).
                            CompInmate mc2 = mentor.Inmate();
                            GangDef gangWas = mc2.gang, newcomerGangWas = nc.gang;
                            bool knownWas = nc.gangKnown;
                            mc2.gang = DefDatabase<GangDef>.AllDefsListForReading.First();
                            nc.gang = null;
                            nc.gangKnown = false;
                            int pulledAfter = -1;
                            for (int day = 0; day < 300 && pulledAfter < 0; day++)
                            {
                                Rand.PushState(day);
                                Mentors.Daily(map);
                                Rand.PopState();
                                if (nc.gang != null) pulledAfter = day;
                            }
                            if (nc.gang != mc2.gang || nc.gangKnown) problems0.Add($"a mentor's gang never drew the newcomer in (or made it known at once): gang {nc.gang?.defName}, known {nc.gangKnown}");
                            (mc2.gang, nc.gang, nc.gangKnown) = (gangWas, newcomerGangWas, knownWas);
                            // Ten days on: it ends with a chronicle line.
                            int chronicle = g.story.entries.Count;
                            nc.mentorUntilTick = now;
                            Mentors.Daily(map);
                            if (nc.mentorId >= 0 || g.story.entries.Count <= chronicle) problems0.Add("a mentorship did not end after its days");
                            // The mentor gone without notice (daily sweep), and leaving for good (at once): both end it.
                            Mentors.Start(mentor, newcomer);
                            nc.mentorId = int.MaxValue;
                            chronicle = g.story.entries.Count;
                            Mentors.Daily(map);
                            if (nc.mentorId >= 0 || g.story.entries.Count <= chronicle || newcomer.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPMentorDefOf.RP_Mentored))
                                problems0.Add("a mentorship outlived its mentor, unrecorded or still felt");
                            Mentors.Start(mentor, newcomer);
                            Mentors.Notify_Leaving(mentor);
                            if (nc.mentorId >= 0) problems0.Add("a mentor leaving the prison did not end the mentorship");
                            // Kept apart, and not offered again for a while.
                            var apartLetter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Mentor", "test", "test", map);
                            apartLetter.pawns.Add(mentor);
                            apartLetter.pawns.Add(newcomer);
                            apartLetter.answered = true;
                            PrisonDecisions.Choose(apartLetter, 1);
                            if (nc.mentorId >= 0 || !mentor.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPMentorDefOf.RP_MentorTurnedAway))
                                problems0.Add("keeping a mentor and newcomer apart started it anyway or left the mentor unbothered");
                            if (Mentors.CanMentor(mentor)) problems0.Add("a mentor turned away could be offered again at once");
                            RPLog.Message($"[RimPrison:test] mentor: {mentor.LabelShort} and {newcomer.LabelShort}, gang pull after {pulledAfter + 1} days");
                        }
                        (oldHand.ageTracker.AgeBiologicalTicks, oc.tension, oc.admittedTick, oc.records) = (ageWas, tensionWas, admittedWas, recordsWas);
                    }
                    Mentors.MentorMinDays = minDaysWas;
                    foreach (var kv in mentorshipsWas) (kv.Key.mentorId, kv.Key.mentorName, kv.Key.mentorUntilTick) = kv.Value;
                }
            }

            private void EvStaffMorale()
            {
                // Staff morale: a demoralised guard finds less in a frisk and a cell search (same dice) and restrains
                // worse; the pat-down of a restraint stays certain; a steady guard is just that.
                if (StaffLives.OnSite(map).FirstOrDefault(x => GuardUtility.IsGuard(x) && x.needs?.mood != null) is { } moody
                    && map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { } xc && xc.stash.Count == 0 && x.inventory != null) is { } searched)
                {
                    float moodWas = moody.needs.mood.CurLevel;
                    ThingDef contraband = DefDatabase<ThingDef>.AllDefsListForReading.First(d => ContrabandUtility.Ext(d) != null);
                    CompInmate sc2 = searched.Inmate();
                    int pendingWas = sc2.pendingPunishmentHours;
                    int SearchFinds(float mood)
                    {
                        moody.needs.mood.CurLevel = mood;
                        for (int i = 0; i < 40; i++) sc2.stash.TryAdd(ThingMaker.MakeThing(contraband), canMergeWithExistingStacks: false);
                        Rand.PushState(4242);
                        int found = StashUtility.Search(moody, searched, sc2);
                        Rand.PopState();
                        sc2.stash.ClearAndDestroyContents();
                        return found;
                    }
                    int FriskFinds(float mood, float chance)
                    {
                        moody.needs.mood.CurLevel = mood;
                        // (At most the check's 40: one they carried already from the live prison made a pat-down that finds everything read 41.)
                        for (int i = 0; i < 40; i++) searched.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(contraband), canMergeWithExistingStacks: false);
                        Rand.PushState(4243);
                        int found = UnityEngine.Mathf.Min(40, ContrabandUtility.Search(moody, searched, chance));
                        Rand.PopState();
                        foreach (Thing t in searched.inventory.innerContainer.Where(t => t.def == contraband).ToList()) t.Destroy();
                        return found;
                    }
                    // The bands follow the guard's own break thresholds (iron-willed or nervous): moods picked from them.
                    MentalBreaker mb = moody.mindState.mentalBreaker;
                    float lowMood = UnityEngine.Mathf.Max(0.01f, mb.BreakThresholdMajor * 0.5f), steadyMood = UnityEngine.Mathf.Clamp((mb.BreakThresholdMajor + mb.BreakThresholdMinor) / 2f + 0.1f, 0.5f, 0.65f);
                    int steadyFinds = SearchFinds(steadyMood), lowFinds = SearchFinds(lowMood);
                    int steadyFrisk = FriskFinds(steadyMood, 0.8f), lowFrisk = FriskFinds(lowMood, 0.8f), lowPatDown = FriskFinds(lowMood, 1f);
                    moody.needs.mood.CurLevel = steadyMood;
                    MoraleBand steadyBand = StaffMorale.Band(moody);
                    float steadySubdue = PunishmentUtility.SubdueChance(moody, searched);
                    moody.needs.mood.CurLevel = lowMood;
                    float lowSubdue = PunishmentUtility.SubdueChance(moody, searched);
                    if (StaffMorale.Band(moody) != MoraleBand.Demoralised || steadyBand != MoraleBand.Steady || lowFinds >= steadyFinds || lowFrisk >= steadyFrisk
                        || lowPatDown != 40 || lowSubdue > steadySubdue)
                        problems0.Add($"a demoralised guard: band {StaffMorale.Band(moody)} (steady {steadyBand}), cell search {lowFinds} vs {steadyFinds}, frisk {lowFrisk} vs {steadyFrisk}, restraint pat-down {lowPatDown} of 40, subdue {lowSubdue:P0} vs {steadySubdue:P0}");
                    RPLog.Message($"[RimPrison:test] morale: cell search {lowFinds} vs {steadyFinds} of 40, frisk {lowFrisk} vs {steadyFrisk}, pat-down {lowPatDown}, subdue {lowSubdue:P0} vs {steadySubdue:P0}");
                    moody.needs.mood.CurLevel = moodWas;
                    sc2.pendingPunishmentHours = pendingWas;
                }
            }

            private void EvKitchenFire()
            {
                // Kitchen fire: it starts at a kitchen stove; the brigade, called and paid, puts every fire out.
                {
                    int firesBefore = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count, statBefore = g.stats.fires;
                    bool lit = DefDatabase<IncidentDef>.GetNamed("RP_KitchenFire").Worker.TryExecute(new IncidentParms { target = map });
                    var fl3 = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_KitchenFire"));
                    int burning = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
                    bool byStove = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Any(f => IncidentWorker_KitchenFire.KitchenStoves(map).Any(st => f.Position.InHorDistOf(st.Position, 2f)));
                    if (!lit || fl3 == null || burning <= firesBefore || g.stats.fires != statBefore + 1 || !byStove)
                        problems0.Add($"no kitchen fire (lit={lit}, letter={fl3 != null}, fires {firesBefore} -> {burning}, by a stove {byStove})");
                    else
                    {
                        int fundsFire = g.ledger.funds;
                        fl3.answered = true;
                        PrisonDecisions.Choose(fl3, 0);
                        Find.LetterStack.RemoveLetter(fl3);
                        if (g.story.fireBrigadeTick < 0 || g.ledger.funds != fundsFire - IncidentWorker_KitchenFire.BrigadeCost)
                            problems0.Add($"calling the fire brigade: due {g.story.fireBrigadeTick}, cost {fundsFire - g.ledger.funds}");
                        FireBrigade.Hourly(g, map);
                        if (map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count > 0 || g.story.fireBrigadeTick >= 0)
                            problems0.Add($"the fire brigade left {map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count} fires burning");
                        // No fire left: no callout to pay for.
                        var none = ChoiceLetter_PrisonDecision.Make("RP_Decision_KitchenFire", "test", "test", map);
                        int fundsNone = g.ledger.funds;
                        PrisonDecisions.Choose(none, 0);
                        if (none.decision.Worker.Disabled(none, 0) == null || g.ledger.funds != fundsNone || g.story.fireBrigadeTick >= 0)
                            problems0.Add("the fire brigade could be called (and paid) with no fire burning");
                        g.ledger.funds = fundsFire;
                        // One left to the staff, lit at the next hour (after the kitchen tests below, which need a cold
                        // stove), on the stove itself: an unattended stove burns down in hours, so out with the stove
                        // still standing means they fought it (checked hourly, judged at the end).
                        staffFirePending = true;
                    }
                }
            }

            private void EvStaffTraining()
            {
                // Staff training: paid, a day off site, back through the gate two levels better and pleased; not again
                // for a fortnight.
                {
                    int hourNow = GenLocalDate.HourOfDay(map);
                    Pawn trainee = StaffLives.OnSite(map).FirstOrDefault(x => x.GetComp<CompStaff>() is { } xsc && StaffTraining.CannotGo(x, xsc) == null
                        && x.timetable?.GetAssignment(hourNow) != TimeAssignmentDefOf.Sleep);
                    if (trainee == null) problems0.Add("staff training: nobody on site could be sent on a course");
                    else
                    {
                        CompStaff tsc2 = trainee.GetComp<CompStaff>();
                        SkillDef skill = StaffTraining.SkillFor(tsc2.role);
                        int levelWas = trainee.skills.GetSkill(skill).Level, fundsWas = g.ledger.funds, trained = g.stats.staffTrained;
                        bool sent = StaffTraining.Send(trainee, tsc2);
                        if (!sent || g.ledger.funds != fundsWas - StaffTraining.Cost || !StaffCommute.ShouldBeHome(trainee, tsc2, hourNow)
                            || StaffTraining.CannotGo(trainee, tsc2) == null)
                            problems0.Add($"sending {trainee.LabelShort} on a course: sent {sent}, cost {fundsWas - g.ledger.funds}, should be away {StaffCommute.ShouldBeHome(trainee, tsc2, hourNow)}");
                        if (sent)
                        {
                        // Off site, the course over, back for the shift.
                        if (trainee.Spawned)
                        {
                            trainee.jobs.StopAll();
                            StaffCommute.Leave(trainee);
                        }
                        // Burnout leave granted while away on the course: they come back trained and rested.
                        tsc2.restPending = true;
                        tsc2.leaveUntilTick = Find.TickManager.TicksGame - 1;
                        StaffCommute.Hourly(map, g, hourNow);
                        if (!trainee.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPStaffLifeDefOf.RP_Rested) || tsc2.restPending)
                            problems0.Add("a course and burnout leave in one absence: no rest on return");
                        if (!trainee.Spawned || tsc2.trainingPending || trainee.skills.GetSkill(skill).Level != System.Math.Min(20, levelWas + StaffTraining.Levels)
                            || !trainee.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPTrainingDefOf.RP_Trained) || g.stats.staffTrained != trained + 1)
                            problems0.Add($"{trainee.LabelShort} back from a {skill.label} course: on site {trainee.Spawned}, pending {tsc2.trainingPending}, level {levelWas} -> {trainee.skills.GetSkill(skill).Level}");
                        if (StaffTraining.CannotGo(trainee, tsc2) == null) problems0.Add("a trainee could be sent on another course straight away");
                        RPLog.Message($"[RimPrison:test] training: {trainee.LabelShort} {skill.label} {levelWas} -> {trainee.skills.GetSkill(skill).Level}");
                        }
                        // Kept on site the whole day: done in-house; temporary callout staff and the top levels cannot go.
                        if (StaffLives.OnSite(map).FirstOrDefault(x => x != trainee && x.GetComp<CompStaff>() is { } xsc && StaffTraining.CannotGo(x, xsc) == null) is { } stayer)
                        {
                            CompStaff ssc = stayer.GetComp<CompStaff>();
                            SkillDef sskill = StaffTraining.SkillFor(ssc.role);
                            int sLevel = stayer.skills.GetSkill(sskill).Level;
                            ssc.leaveAtTick = Find.TickManager.TicksGame + 1000;
                            if (StaffTraining.CannotGo(stayer, ssc) == null) problems0.Add("temporary callout staff could be sent on a course");
                            ssc.leaveAtTick = -1;
                            StaffTraining.Send(stayer, ssc);
                            ssc.leaveUntilTick = Find.TickManager.TicksGame - 1;
                            StaffTraining.CheckInHouse(stayer, ssc);
                            if (ssc.trainingPending || stayer.skills.GetSkill(sskill).Level != System.Math.Min(StaffTraining.CourseCap, sLevel + StaffTraining.Levels))
                                problems0.Add($"a course run out on site was not completed in-house (pending {ssc.trainingPending}, level {sLevel} -> {stayer.skills.GetSkill(sskill).Level})");
                            ssc.lastTrainedTick = -1;
                            stayer.skills.GetSkill(sskill).Level = StaffTraining.CourseCap - 1;
                            if (StaffTraining.CannotGo(stayer, ssc) == null) problems0.Add($"a course was offered at level {StaffTraining.CourseCap - 1} (they only go to {StaffTraining.CourseCap})");
                            stayer.skills.GetSkill(sskill).Level = sLevel;
                        }
                        g.ledger.funds = fundsWas;
                    }
                }
            }

            private void EvStaffRoom()
            {
                // Staff room: the break's lift grows with the room (shabby, decent, fine); one lift at a time, at the
                // latest room's stage; two breakers never get the same seat; guards keep their posts in a lockdown.
                if (StaffBreaks.StageFor(5f) != 0 || StaffBreaks.StageFor(50f) != 1 || StaffBreaks.StageFor(80f) != 2)
                    problems0.Add("staff room break stages do not follow impressiveness");
                {
                    // (Whoever is free at this moment is up to the day: on a break, eating, at a task a break must not cut short. A guard or two are made up to fill the places.)
                    using var roomRb = new Rollback(map);
                    List<Pawn> onSite = StaffLives.OnSite(map).Where(x => !StaffBreaks.Busy(x, map)).Take(2).ToList();
                    int realFree = onSite.Count;
                    while (onSite.Count < 2) onSite.Add(ThrowawayStaff(map, roomRb, RPSecurityDefOf.RP_Guard));
                    if (onSite.Count == 2 && PrisonMap.Of(map) is { } spm)
                    {
                        StaffBreaks.GiveLift(onSite[0]);
                        StaffBreaks.GiveLift(onSite[0]);
                        var lifts = onSite[0].needs.mood.thoughts.memories.Memories.Where(m => m.def == RPStaffRoomDefOf.RP_ProperBreak).ToList();
                        int expectStage = StaffBreaks.StageFor(onSite[0].GetRoom()?.GetStat(RoomStatDefOf.Impressiveness) ?? 0f);
                        if (lifts.Count != 1 || lifts[0].CurStageIndex != expectStage) problems0.Add($"break lifts: {lifts.Count} at stage {lifts.FirstOrDefault()?.CurStageIndex} (expected 1 at {expectStage})");
                        bool sentA = StaffBreaks.TrySend(onSite[0], spm), sentB = StaffBreaks.TrySend(onSite[1], spm);
                        IntVec3 a1 = onSite[0].CurJob?.targetA.Cell ?? IntVec3.Invalid, b1 = onSite[1].CurJob?.targetA.Cell ?? IntVec3.Invalid;
                        if (!sentA || !sentB || a1 == b1) problems0.Add($"two staff sent on a break: sent {sentA}/{sentB}, spots {a1} and {b1}");
                        foreach (Pawn x in onSite) x.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                        Pawn guardOnSite = StaffLives.OnSite(map).FirstOrDefault(GuardUtility.IsGuard);
                        if (guardOnSite != null)
                        {
                            int lockWas = g.lockdownUntilTick;
                            g.lockdownUntilTick = Find.TickManager.TicksGame + 1000;
                            if (!StaffBreaks.Busy(guardOnSite, map)) problems0.Add("a guard would leave their post for a break during a lockdown");
                            g.lockdownUntilTick = lockWas;
                        }
                        RPLog.Message($"[RimPrison:test] staff room: breaks to {a1} and {b1}, lift stage {expectStage} ({realFree} of the staff free, {2 - realFree} made up)");
                    }
                    else problems0.Add("staff room test: fewer than two staff free on site");
                }
            }
        }
    }
}
