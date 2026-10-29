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
        // Off-shift staff, the kitchen shift, scuffles through walls, pastimes, officer of the month, informants, the menu, weather, a laid-up host, the weekly report, then the statistics, alerts, careers, shuttle and supermax checks and the hunger strike. (the shared state is in EventsRun.cs)
        private sealed partial class EventsRun
        {
            private void EvOffShiftStaff()
            {
                // Off-shift staff (at home) are still reached by decisions: a warning lands, and a dismissal takes them off the books.
                {
                    // Send a guard and a spare non-guard home now (setup happens while everyone is on site).
                    foreach (Pawn x in StaffLives.OnSite(map).Where(GuardUtility.IsGuard).Take(1)
                                 .Concat(StaffLives.OnSite(map).Where(x => !x.GetComp<CompStaff>().role.guardDuties
                                     && StaffCommute.AllStaff(map).Count(o => o.GetComp<CompStaff>()?.role == x.GetComp<CompStaff>().role) >= 2).Take(1)).ToList())
                    {
                        x.DeSpawn();
                        g.home.TryAdd(x);
                    }
                    RPLog.Message($"[RimPrison:test] on site before: {string.Join(", ", StaffLives.OnSite(map).Select(x => x.LabelShort + ":" + x.GetComp<CompStaff>().role.defName))}");
                    List<Pawn> away = g.OffSite.Where(x => x.GetComp<CompStaff>()?.role != null).ToList();
                    RPLog.Message($"[RimPrison:test] off-site staff at setup: {string.Join(", ", away.Select(x => x.LabelShort + ":" + x.GetComp<CompStaff>().role.defName))}");
                    if (away.FirstOrDefault(x => GuardUtility.IsGuard(x) || x.GetComp<CompStaff>().role.guardDuties) is { } awayGuard
                        && map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(InmateUtility.IsActiveInmate) is { } pal)
                    {
                        CompStaff asc = awayGuard.GetComp<CompStaff>();
                        float was = asc.integrity;
                        asc.integrity = 60f;
                        var tc = ChoiceLetter_PrisonDecision.Make("RP_Decision_TooClose", "test", "test", map, awayGuard);
                        tc.pawns.Add(awayGuard);
                        tc.pawns.Add(pal);
                        PrisonDecisions.Choose(tc, 1);
                        if (asc.integrity < 64.9f) problems0.Add("a formal warning never reached an officer at home");
                        asc.integrity = was;
                    }
                    Pawn spare = away.FirstOrDefault(x => !x.GetComp<CompStaff>().role.guardDuties
                        && StaffCommute.AllStaff(map).Count(o => o.GetComp<CompStaff>()?.role == x.GetComp<CompStaff>().role) >= 2);
                    // The events plan has one of each: its janitor is the one nobody's checks depend on.
                    if (spare == null && StaffLives.OnSite(map).FirstOrDefault(x => x.GetComp<CompStaff>().role.defName == "RP_Janitor") is { } janitor)
                    {
                        janitor.DeSpawn();
                        g.home.TryAdd(janitor);
                        spare = janitor;
                    }
                    if (spare != null)
                    {
                        StaffUtility.Fire(spare);
                        if (spare.GetComp<CompStaff>().role != null || g.home.Contains(spare)) problems0.Add("firing someone at home left them on the books");
                    }
                }
            }

            private void EvKitchenShift()
            {
                // Kitchen shift: with an inmate cook on duty in the kitchen, the staff cook's meal bills there pause.
                {
                    PrisonMap kpm = PrisonMap.Of(map);
                    Building stove = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => InmateJobWorker_Kitchen.IsStove(b) && kpm.zones.ZoneAt(b.Position) == RPLaborDefOf.RP_Kitchen);
                    Bill_Production mealBill = (stove as IBillGiver)?.BillStack.Bills.OfType<Bill_Production>().FirstOrDefault(b => b.recipe.ProducedThingDef?.IsIngestible == true);
                    Pawn cook = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && x.Inmate().forcedActivity == null);
                    if (stove != null && mealBill != null && cook != null)
                    {
                        CompInmate kc = cook.Inmate();
                        InmateJobDef jobWas = kc.job;
                        IntVec3 posWas = cook.Position;
                        kc.job = DefDatabase<InmateJobDef>.GetNamed("RP_JobKitchen");
                        kc.forcedActivity = RPLaborDefOf.RP_Work;
                        kc.forcedUntilTick = -1;
                        cook.pather.StopDead();
                        cook.Position = stove.InteractionCell;
                        cook.Notify_Teleported();
                        // Raw food kept in the kitchen: the inmate can cook, so the kitchen is theirs.
                        Thing potatoes = ThingMaker.MakeThing(ThingDefOf.RawPotatoes);
                        potatoes.stackCount = 20;
                        IntVec3 shelf = CellFinder.StandableCellNear(stove.InteractionCell, map, 3f, c => kpm.zones.ZoneAt(c) == RPLaborDefOf.RP_Kitchen);
                        if (shelf.IsValid) GenSpawn.Spawn(potatoes, shelf, map);
                        KitchenShift.Reset();
                        if (mealBill.ShouldDoNow()) problems0.Add("the staff cook's meal bill runs while an inmate cook is on duty in the kitchen");
                        kc.job = jobWas;
                        kc.forcedActivity = null;
                        cook.Position = posWas;
                        cook.Notify_Teleported();
                        KitchenShift.Reset();
                    }
                }
            }

            private void EvScuffleNeverAcrossWalls()
            {
                // A scuffle never starts between inmates in different rooms (through a wall, between locked cells).
                {
                    List<Pawn> apart = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && !x.Downed && !x.InMentalState).ToList();
                    // Close enough (12 cells) that only the room check keeps them apart.
                    Pawn x1 = null, x2 = null;
                    foreach (Pawn a in apart)
                        if (apart.FirstOrDefault(b => b.GetRoom() != a.GetRoom() && b.Position.InHorDistOf(a.Position, 10f)) is { } b2)
                        {
                            x1 = a;
                            x2 = b2;
                            break;
                        }
                    RPLog.Message($"[RimPrison:test] scuffle check pair: {x1?.LabelShort} / {x2?.LabelShort}");
                    if (x1 != null && x2 != null)
                    {
                        RestUtility.WakeUp(x1);
                        RestUtility.WakeUp(x2);
                        if (Scuffles.TryStart(map, (a, b) => a == x1 && b == x2 || a == x2 && b == x1)) problems0.Add("a scuffle started between inmates in different rooms");
                    }
                }
            }

            private void EvPastimes()
            {
                // Pastimes: the big match and a talent show cheer the wing; a pressured new arrival joins or pays for refusing.
                {
                    var bm = ChoiceLetter_PrisonDecision.Make("RP_Decision_BigMatch", "test", "test", map);
                    PrisonDecisions.Choose(bm, 0);
                    if (!map.mapPawns.PrisonersOfColonySpawned.Any(x => x.needs.mood?.thoughts.memories.Memories.Any(m => m.def == RPPastimeDefOf.RP_BigMatch) == true))
                        problems0.Add("nobody listened to the big match");
                    var ts = ChoiceLetter_PrisonDecision.Make("RP_Decision_TalentShow", "test", "test", map);
                    PrisonDecisions.Choose(ts, 0);
                    if (IncidentWorker_TalentShow.BestPerformer(map) is not { } star || star.needs.mood.thoughts.memories.Memories.All(m => m.def != RPPastimeDefOf.RP_StarOfTheShow))
                        problems0.Add("the talent show had no star");
                    GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                    List<Pawn> recruitPair = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && DeathRowSystem.Ext(x.Inmate()) == null && !x.Downed).Take(2).ToList();
                    if (gang != null && recruitPair.Count == 2)
                    {
                        Pawn boss = recruitPair[0], newcomer = recruitPair[1];
                        CompInmate bc = boss.Inmate(), fc = newcomer.Inmate();
                        (GangDef bg, bool bl, bool bk) = (bc.gang, bc.gangLeader, bc.gangKnown);
                        GangDef fg = fc.gang;
                        bc.gang = gang;
                        bc.gangLeader = bc.gangKnown = true;
                        fc.gang = null;
                        var il = ChoiceLetter_PrisonDecision.Make("RP_Decision_Initiation", "test", "test", map, newcomer);
                        il.pawns.Add(newcomer);
                        il.pawns.Add(boss);
                        il.amount = gang.shortHash;
                        float tension = bc.tension;
                        PrisonDecisions.Choose(il, 1);
                        if (bc.tension <= tension && bc.tension < 100f) problems0.Add("a word with the gang leader changed nothing");
                        fc.gang = null;
                        int entries = g.story.entries.Count;
                        PrisonDecisions.Choose(il, 2);
                        bool joined = fc.gang == gang;
                        bool refused = g.story.entries.Skip(entries).Any(e => e.text.Contains(newcomer.LabelShort));
                        if (!joined && !refused) problems0.Add("a pressured new arrival neither joined nor refused");
                        (bc.gang, bc.gangLeader, bc.gangKnown) = (bg, bl, bk);
                        fc.gang = fg;
                        foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned)
                            if (x.MentalStateDef == RPMentalStateDefOf.RP_Assaulting || x.MentalStateDef == RPMentalStateDefOf.RP_AttackingInmate) x.mindState.mentalStateHandler.CurState.RecoverFromState();
                    }
                }
            }

            private void EvOfficerOfTheMonth()
            {
                // Officer of the month: the most commended clean officer; the award lifts them and raises integrity.
                if (OfficerOfTheMonth.Candidate(map) is { } best)
                {
                    CompStaff bsc = best.GetComp<CompStaff>();
                    if (StaffCommute.AllStaff(map).Any(o => o.GetComp<CompStaff>() is { role: not null, leaveAtTick: < 0, exposedCorrupt: false, iaOpen: false } osc
                                                             && osc.suspicion < 20f && osc.commendations > bsc.commendations))
                        problems0.Add("the officer of the month is not the most commended");
                    float was = bsc.integrity;
                    bsc.integrity = 50f;
                    var om = ChoiceLetter_PrisonDecision.Make("RP_Decision_OfficerOfMonth", "test", "test", map, best);
                    om.pawns.Add(best);
                    PrisonDecisions.Choose(om, 0);
                    if (bsc.integrity < 59.9f || best.needs?.mood?.thoughts.memories.Memories.All(m => m.def != RPRecognitionDefOf.RP_OfficerOfTheMonth) != false)
                        problems0.Add("officer of the month: no lift or no integrity");
                    bsc.integrity = was;
                }
            }

            private void EvInformantOffThePayroll()
            {
                // An informant taken off the payroll stops talking (they used to keep tipping for free).
                {
                    List<Pawn> snitches = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { activeFlag: true } xc && xc.HasReputation(ReputationDefOf.RP_Snitch) && !xc.snitchExposed).ToList();
                    Pawn talker = snitches.FirstOrDefault() ?? map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && !x.Inmate().snitchExposed);
                    if (talker != null)
                    {
                        CompInmate tc = talker.Inmate();
                        bool hadRep = tc.HasReputation(ReputationDefOf.RP_Snitch);
                        if (!hadRep) tc.reputations.Add(ReputationDefOf.RP_Snitch);
                        foreach (Pawn o in snitches) if (o != talker) o.Inmate().informantQuit = true; // only this one talks
                        int before = g.stats.tips;
                        for (int i = 0; i < 30; i++) InformantSystem.DailyTips(map);
                        int talking = g.stats.tips - before;
                        tc.informantQuit = true;
                        before = g.stats.tips;
                        for (int i = 0; i < 30; i++) InformantSystem.DailyTips(map);
                        RPLog.Message($"[RimPrison:test] informant tips: talking {talking}, after quitting {g.stats.tips - before}");
                        if (talking > 0 && g.stats.tips != before) problems0.Add("an informant off the payroll kept tipping");
                        foreach (Pawn o in snitches) o.Inmate().informantQuit = false;
                        if (!hadRep) tc.reputations.Remove(ReputationDefOf.RP_Snitch);
                        foreach (Letter tl in Find.LetterStack.LettersListForReading.Where(x => x.Label.ToString() == "RP_TipLabel".Translate().ToString()).ToList()) Find.LetterStack.RemoveLetter(tl);
                    }
                }
            }

            private void EvKitchenMenu()
            {
                // Kitchen menu: fine meals swap the staff meal bills (settings kept), and back again.
                {
                    Building stove = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b is IBillGiver gv && gv.BillStack.Bills.Any(x => x.recipe == KitchenMenuPolicy.Recipe(KitchenMenu.Simple)));
                    if (stove is IBillGiver giver)
                    {
                        var simpleBill = giver.BillStack.Bills.OfType<Bill_Production>().First(x => x.recipe == KitchenMenuPolicy.Recipe(KitchenMenu.Simple));
                        // The player's settings must survive the swap, and the bill keeps its place in the list.
                        simpleBill.pauseWhenSatisfied = true;
                        simpleBill.allowedSkillRange = new IntRange(4, 20);
                        simpleBill.ingredientSearchRadius = 21f;
                        int target = simpleBill.targetCount, index = giver.BillStack.Bills.IndexOf(simpleBill);
                        System.Action untrainCook = TrainACookForTheFineMeal(map); // (the swap is refused with nobody who can cook the fine meal)
                        KitchenMenuPolicy.Apply(map, KitchenMenu.Fine);
                        var fine = giver.BillStack.Bills.OfType<Bill_Production>().FirstOrDefault(x => x.recipe == KitchenMenuPolicy.Recipe(KitchenMenu.Fine));
                        if (fine == null || fine.targetCount != target || giver.BillStack.Bills.Any(x => x.recipe == KitchenMenuPolicy.Recipe(KitchenMenu.Simple)))
                            problems0.Add("the fine menu did not swap the stove's meal bill");
                        else if (!fine.pauseWhenSatisfied || fine.allowedSkillRange.min != 4 || fine.ingredientSearchRadius != 21f || giver.BillStack.Bills.IndexOf(fine) != index)
                            problems0.Add("the fine menu lost the meal bill's settings or its place");
                        KitchenMenuPolicy.Apply(map, KitchenMenu.Simple);
                        if (!giver.BillStack.Bills.Any(x => x.recipe == KitchenMenuPolicy.Recipe(KitchenMenu.Simple))) problems0.Add("the simple menu did not come back");
                        untrainCook();
                    }
                }
            }

            private void EvHeatWaveOrColdSnap()
            {
                // Heat wave or cold snap: a vanilla condition; relief is paid for and lasts as long as the weather.
                {
                    if (WeatherStrain.Active(map) is { } old) old.End();
                    RPLog.Message($"[RimPrison:test] weather: seasonal {map.mapTemperature.SeasonalTemp:F0} C, fitting {WeatherStrain.Fitting(map)?.defName ?? "none"}");
                    if (WeatherStrain.Fitting(map) != null)
                    DefDatabase<IncidentDef>.GetNamed("RP_Weather").Worker.TryExecute(new IncidentParms { target = map });
                    var wl = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Weather"));
                    GameCondition weather = WeatherStrain.Active(map);
                    if (WeatherStrain.Fitting(map) == null) { } // mild season (15-20 C) or freezing: vanilla would send none either
                    else if (weather == null || wl == null) problems0.Add("no heat wave or cold snap");
                    else
                    {
                        int funds = g.ledger.funds;
                        wl.answered = true;
                        PrisonDecisions.Choose(wl, 0);
                        Find.LetterStack.RemoveLetter(wl);
                        if (g.ledger.funds >= funds || !WeatherStrain.Relieved(map, weather)) problems0.Add("weather relief was not bought for this heat wave or cold snap");
                        // Someone already in the heat or the cold feels it: a third of the strain with relief, the full strain without.
                        if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true }
                                && (x.AmbientTemperature > WeatherStrain.HotAbove || x.AmbientTemperature < WeatherStrain.ColdBelow)) is { } exposed)
                        {
                            float relieved = WeatherStrain.For(exposed, out string cause);
                            int mapWas = g.story.weatherReliefMapId;
                            g.story.weatherReliefMapId = -1;
                            float full = WeatherStrain.For(exposed, out _);
                            g.story.weatherReliefMapId = mapWas;
                            RPLog.Message($"[RimPrison:test] weather strain: {exposed.LabelShort} at {exposed.AmbientTemperature:F0} C, {cause}: {relieved:F2}/h relieved, {full:F2}/h without");
                            if (full <= 0f || relieved >= full) problems0.Add("the heat or the cold wore nobody down, or relief did not help");
                        }
                        weather.End();
                        g.story.weatherReliefMapId = g.story.weatherReliefStartTick = -1;
                    }
                }
            }

            private void EvVisitorGoesHomeWhenTheHostIsLaidUp()
            {
                // A visitor goes home at once when the inmate they came for is laid up (they used to wait out 5 hours).
                {
                    Pawn host = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && x.Inmate().visitor == null && !x.Downed);
                    if (host != null && PrisonMap.Of(map).zones.Any(RPVisitDefOf.RP_Visitation) && VisitationSystem.SpawnVisitor(map, host, host.Inmate(), booth: false))
                    {
                        Pawn guest = host.Inmate().visitor;
                        Hediff knockout = HediffMaker.MakeHediff(HediffDefOf.Anesthetic, host);
                        knockout.Severity = 1f;
                        host.health.AddHediff(knockout);
                        HarmonyLib.AccessTools.Method(typeof(CompInmate), "Rare").Invoke(host.Inmate(), null);
                        bool guestLeaving = guest?.GetLord()?.CurLordToil is LordToil_ExitMap;
                        RPLog.Message($"[RimPrison:test] laid-up visit: downed={host.Downed} visitor cleared={host.Inmate().visitor == null} leaving={guestLeaving}");
                        if (!host.Downed || host.Inmate().visitor != null || !guestLeaving) problems0.Add("a visitor kept waiting for an inmate who was laid up");
                        if (host.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Anesthetic) is { } k) host.health.RemoveHediff(k);
                    }
                }
            }

            private void EvWeeklyReport()
            {
                // Weekly report: a letter with the week's numbers and headlines.
                {
                    g.week ??= WeekSnapshot.Take(g, map);
                    g.week.startTick -= 2 * GenDate.TicksPerDay; // include the setup's stories
                    // ...and the session's contracts, fires, food scares, mentorships and courses.
                    (g.week.contractsMet, g.week.contractsMissed, g.week.fires, g.week.foodScares, g.week.mentorships, g.week.staffTrained, g.week.remissionDays) = (0, 0, 0, 0, 0, 0, 0);
                    int weeks = g.weeksReported, letters = Find.LetterStack.LettersListForReading.Count;
                    WeeklyReport.Send(g, map);
                    Letter wl = Find.LetterStack.LettersListForReading.LastOrDefault();
                    if (g.weeksReported != weeks + 1 || Find.LetterStack.LettersListForReading.Count != letters + 1 || wl == null)
                        problems0.Add("no weekly report letter");
                    else if (!((wl as ChoiceLetter)?.Text.ToString() ?? "").Contains("RP_Week_Life".Translate("").ToString().TrimEnd('.')))
                        problems0.Add("the weekly report has no work-and-life line after a week of contracts, fires and courses");
                    else if (PerDiem.WeekLine(g, map) is { } weekPerDiem && !((wl as ChoiceLetter)?.Text.ToString() ?? "").Contains(weekPerDiem))
                        problems0.Add($"the weekly report has no per-diem line (expected \"{weekPerDiem}\")");
                    else RPLog.Message($"[RimPrison:test] weekly report: {wl.Label}\n{(wl as ChoiceLetter)?.Text}");
                    g.week = WeekSnapshot.Take(g, map);
                }
            }

            private void EvStatisticsTable()
            {
                CheckStatisticsTable();
            }

            private void EvNewAlerts()
            {
                CheckNewAlerts(map);
            }

            private void EvHiddenTensionCause()
            {
                CheckHiddenTensionCause(map);
            }

            private void EvCareers()
            {
                CheckCareers(map);
            }

            private void EvShuttleChecks()
            {
                // The state shuttle (ShuttleChecks.cs): it lands on the pad or near holding, unloads, delivers cargo, takes the released, waits for a clear pad, is held by weather. Last: these run
                // hours of real ticks (the time of day matters to some checks above), and they need the prison as the others left it.
                RunShuttleChecks(map);
            }

            private void EvSupermax()
            {
                // Supermax (SupermaxChecks.cs): the level and its regime, the cell locked in every hour, never the canteen, the walk to the shower and the yard alone, who is sent there, the long stay and the step-down. Self-contained: a small unit
                // on open ground outside the compound, throwaway inmates and guards, rolled back. After the shuttle's: it runs real ticks too (visits start, a daily shuttle may land), which the checks above that read who is visiting would see.
                CheckSupermax(map);
            }

            private void EvHungerStrike()
            {
                // Hunger strike needs miserable inmates: put three on strike directly.
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.Take(3))
                    p.Inmate().hungerStrikeUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay * 2;
            }
        }
    }
}
