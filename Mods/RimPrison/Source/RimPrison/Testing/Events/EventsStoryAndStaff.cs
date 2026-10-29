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
        // Story beats, hazard pay, staff lives, an overdose, a gang truce, a conned rookie, flu, innocence appeals, an inspection and a blackout. (the shared state is in EventsRun.cs)
        private sealed partial class EventsRun
        {
            private void EvStoryBeats()
            {
                // Story beats: a gang leadership challenge settled by hand, an informant turned, a talent put to use, birthdays.
                {
                    // The challenger (cast[1]) must be capable of violence, as the incident requires.
                    List<Pawn> cast = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && DeathRowSystem.Ext(x.Inmate()) == null && !x.InMentalState && !x.Downed)
                        .OrderBy(x => x.WorkTagIsDisabled(WorkTags.Violent) ? 1 : 0).Take(3).ToList();
                    if (cast.Count == 3) (cast[0], cast[1], cast[2]) = (cast[2], cast[0], cast[1]);
                    if (cast.Count < 3) problems0.Add($"only {cast.Count} inmates for the gang challenge test");
                    // The earlier tests leave most inmates in feuds and other arcs: end theirs so this one can start.
                    foreach (StoryArc a in g.story.arcs)
                        if (!a.done && a.pawns.Any(cast.Contains)) a.done = true;
                    GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                    if (cast.Count >= 3 && gang != null)
                    {
                        Pawn boss = cast[0], challenger = cast[1];
                        foreach (Pawn m in cast.Take(3))
                        {
                            m.Inmate().gang = gang;
                            m.Inmate().gangKnown = true;
                            m.Inmate().gangLeader = m == boss;
                        }
                        if (PrisonRelations.Enemies(challenger, boss)) challenger.relations.RemoveDirectRelation(RPRelationDefOf.RP_Enemy, boss);
                        StoryArc arc = g.story.TryStartArc(RPBeatDefOf.RP_Arc_GangChallenge, challenger, boss);
                        if (arc == null) problems0.Add("gang challenge arc did not start");
                        else
                        {
                            arc.note = gang.label;
                            arc.nextTick = Find.TickManager.TicksGame;
                            challenger.pather.StopDead();
                            challenger.Position = boss.Position;
                            challenger.Notify_Teleported();
                            RestUtility.WakeUp(challenger);
                            RestUtility.WakeUp(boss);
                            int challenges = g.stats.gangChallenges;
                            arc.def.Worker.Hourly(arc, map);
                            if (g.stats.gangChallenges != challenges + 1 || !arc.done)
                            {
                                problems0.Add("gang challenge never came to a showdown");
                                RPLog.Message($"[RimPrison:test] showdown failed: c spawned={challenger.Spawned} downed={challenger.Downed} awake={challenger.Awake()} mental={challenger.MentalStateDef?.defName} violent={!challenger.WorkTagIsDisabled(WorkTags.Violent)} job={challenger.CurJobDef?.defName} forced={challenger.Inmate().forcedActivity?.defName} | b spawned={boss.Spawned} downed={boss.Downed} awake={boss.Awake()} mental={boss.MentalStateDef?.defName} sameRoom={challenger.GetRoom() == boss.GetRoom()} nextTick={arc.nextTick - Find.TickManager.TicksGame}");
                            }
                            else if (boss.Inmate().gangLeader == challenger.Inmate().gangLeader || !PrisonRelations.Enemies(challenger, boss))
                                problems0.Add("gang challenge did not leave exactly one leader and two enemies");
                            challenger.mindState.mentalStateHandler.CurState?.RecoverFromState();
                        }
                        cast[2].Inmate().gangLeader = false;
                    }
                    // Informant and talent: anyone active outside the showdown.
                    List<Pawn> others = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && DeathRowSystem.Ext(x.Inmate()) == null && !cast.Take(2).Contains(x)).ToList();
                    if (others.Count < 2) problems0.Add($"only {others.Count} inmates for the informant and talent tests");
                    else
                    {
                        Pawn frightened = others[0];
                        CompInmate sc = frightened.Inmate();
                        sc.reputations.Remove(ReputationDefOf.RP_Snitch);
                        sc.revealed.Remove(ReputationDefOf.RP_Snitch);
                        sc.fear = 80f;
                        foreach (Pawn other in map.mapPawns.PrisonersOfColonySpawned)
                            if (other != frightened && other.Inmate() is { } oc) oc.fear = System.Math.Min(oc.fear, 10f);
                        DefDatabase<IncidentDef>.GetNamed("RP_InformantRecruit").Worker.TryExecute(new IncidentParms { target = map });
                        var il = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Informant"));
                        if (il == null) problems0.Add("no informant letter for a frightened inmate");
                        else
                        {
                            il.answered = true;
                            PrisonDecisions.Choose(il, 0);
                            Find.LetterStack.RemoveLetter(il);
                            Pawn turned = il.pawns[0];
                            if (!turned.Inmate().informantPaid || !turned.Inmate().HasReputation(ReputationDefOf.RP_Snitch)) problems0.Add("a paid informant is not on the payroll or not a snitch");
                        }
                        // Someone whose security level allows a skill job (protective custody and the like may not).
                        Pawn gifted = others.Skip(1).FirstOrDefault(x => DefDatabase<InmateJobDef>.AllDefsListForReading.Any(j => j.skill != null && !x.skills.GetSkill(j.skill).TotallyDisabled
                                          && (j.allowedSecurity.Count == 0 || j.allowedSecurity.Contains(x.Inmate().security)) && j.requiredProgram == null));
                        // (None at a level with a skill job: protective custody and the like; nothing to test then.)
                        if (gifted != null)
                        {
                            foreach (Pawn other in map.mapPawns.PrisonersOfColonySpawned) if (other.Inmate() is { } oc) oc.talentNoticed = other != gifted;
                            gifted.Inmate().talentNoticed = false;
                            // Gifted at whatever their wing's skill jobs need (a wing may only allow, say, a laundry or garden job).
                            foreach (InmateJobDef gj in DefDatabase<InmateJobDef>.AllDefsListForReading)
                                if (gj.skill != null && (gj.allowedSecurity.Count == 0 || gj.allowedSecurity.Contains(gifted.Inmate().security)) && gj.requiredProgram == null)
                                    gifted.skills.GetSkill(gj.skill).Level = 12;
                            InmateJobDef talentJob = IncidentWorker_Talent.TalentJob(gifted);
                            DefDatabase<IncidentDef>.GetNamed("RP_Talent").Worker.TryExecute(new IncidentParms { target = map });
                            var tl = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Talent"));
                            if (talentJob == null) problems0.Add("no prison job uses a skilled inmate's talent");
                            else if (tl == null) problems0.Add("no talent letter for a skilled inmate");
                            else
                            {
                                tl.answered = true;
                                PrisonDecisions.Accept(tl);
                                Find.LetterStack.RemoveLetter(tl);
                                if (gifted.Inmate().job != talentJob || !gifted.Inmate().jobLocked) problems0.Add("talent accepted but the inmate was not put on the job");
                            }
                        }
                        Pawn birthday = gifted ?? others[1];
                        int entries = g.story.entries.Count;
                        Patch_BirthdayInside.Postfix(birthday, 30);
                        if (g.story.entries.Count <= entries || birthday.needs.mood.thoughts.memories.Memories.All(m => m.def != RPBeatDefOf.RP_BirthdayInside && m.def != RPBeatDefOf.RP_BirthdayWithFriends))
                            problems0.Add("an inmate's birthday went unmarked");
                    }
                }
            }

            private void EvHazardPayIntegrity()
            {
                // Declining hazard pay must not turn a not-yet-rolled integrity (-1) into 0 (most corruptible, never rolled).
                if (map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.GetComp<CompStaff>() is { role: not null, leaveAtTick: < 0 }) is { } fresh)
                {
                    CompStaff fsc = fresh.GetComp<CompStaff>();
                    float was = fsc.integrity;
                    fsc.integrity = -1f;
                    var hl = ChoiceLetter_PrisonDecision.Make("RP_Decision_HazardPay", "test", "test", map, fresh);
                    PrisonDecisions.Decline(hl);
                    if (fsc.integrity >= 0f) problems0.Add($"declining hazard pay set an unrolled integrity to {fsc.integrity:F0}");
                    fsc.integrity = was;
                }
            }

            private void EvStaffLives()
            {
                // Staff lives: paid leave sends them home, an ignored burnout can end in a resignation, refusing an advance
                // makes a guard easier to buy, a retention bonus keeps a veteran.
                {
                    List<Pawn> staffNow = StaffLives.OnSite(map).ToList();
                    Pawn tired = staffNow.FirstOrDefault(x => GuardUtility.IsGuard(x));
                    if (tired != null)
                    {
                        CompStaff tsc = tired.GetComp<CompStaff>();
                        var bl = StaffLives.Ask("RP_Decision_Burnout", tired, "test", "test", 12);
                        bl.answered = true;
                        PrisonDecisions.Choose(bl, 0);
                        Find.LetterStack.RemoveLetter(bl);
                        if (!StaffLives.OnLeave(tsc) || !StaffCommute.ShouldBeHome(tired, tsc, 12)) problems0.Add("paid leave did not send a burnt-out officer home");
                        tsc.leaveUntilTick = -1;
                        // Integrity is 0..100: an advance raises it, a refusal drops it toward the fixers' reach.
                        float integrityBefore = tsc.integrity;
                        tsc.integrity = 70f;
                        tsc.debtAsked = true;
                        var al = StaffLives.Ask("RP_Decision_StaffDebt", tired, "test", "test", 24);
                        al.answered = true;
                        PrisonDecisions.Choose(al, 0);
                        Find.LetterStack.RemoveLetter(al);
                        if (tsc.integrity < 84.9f) problems0.Add($"a wage advance left integrity at {tsc.integrity:F1} (expected 85)");
                        tsc.integrity = 70f;
                        var dl = StaffLives.Ask("RP_Decision_StaffDebt", tired, "test", "test", 24);
                        dl.answered = true;
                        PrisonDecisions.Choose(dl, 1);
                        Find.LetterStack.RemoveLetter(dl);
                        if (tsc.integrity > 50.1f) problems0.Add($"refusing a guard's advance left integrity at {tsc.integrity:F1} (expected 50)");
                        tsc.integrity = integrityBefore;
                        var rl = StaffLives.Ask("RP_Decision_Retire", tired, "test", "test", 24);
                        rl.answered = true;
                        PrisonDecisions.Choose(rl, 0);
                        Find.LetterStack.RemoveLetter(rl);
                        if (tsc.role == null || tired.needs.mood.thoughts.memories.Memories.All(m => m.def != RPStaffLifeDefOf.RP_Valued)) problems0.Add("a retained veteran left or felt nothing");
                    }
                    // Debt crisis: three nights in the red bring the state's loan; taking it clears the debt, and a quarter of
                    // each day's payments (at least the minimum) pays it back.
                    {
                        PrisonGame dg = PrisonGame.Instance;
                        int fundsBefore = dg.ledger.funds;
                        float repBefore = dg.reputation;
                        dg.ledger.funds = -2500;
                        dg.story.debtDays = 0;
                        dg.story.loanOwed = 0;
                        for (int i = 0; i < DebtCrisis.DaysInDebt - 1; i++) DebtCrisis.Daily(dg, map, 0);
                        if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_DebtCrisis")))
                            problems0.Add("the state offered a loan before three nights in debt");
                        DebtCrisis.Daily(dg, map, 0);
                        var loanLetter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_DebtCrisis"));
                        if (loanLetter == null) problems0.Add("no state loan offered after three nights in debt");
                        else
                        {
                            loanLetter.answered = true;
                            PrisonDecisions.Choose(loanLetter, 0);
                            Find.LetterStack.RemoveLetter(loanLetter);
                            if (dg.ledger.funds != 10500 || dg.story.loanOwed != 14300)
                                problems0.Add($"the state loan left funds {dg.ledger.funds} (expected 10500) and {dg.story.loanOwed} owed (expected 14300)");
                            if (dg.story.debtDays != 0) problems0.Add($"taking the loan left {dg.story.debtDays} nights in debt on the count (expected 0)");
                            DebtCrisis.Daily(dg, map, 2000);
                            DebtCrisis.Daily(dg, map, 0);
                            if (dg.story.loanOwed != 14300 - 500 - DebtCrisis.MinRepayment || dg.ledger.funds != 10500 - 500 - DebtCrisis.MinRepayment)
                                problems0.Add($"loan repayment: {dg.story.loanOwed} owed and funds {dg.ledger.funds} (expected 13600 and 9800)");
                            if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_DebtCrisis")))
                                problems0.Add("a second loan was offered while one is outstanding");
                            RPLog.Message($"[RimPrison:test] state loan: owed {dg.story.loanOwed}, funds {dg.ledger.funds}");
                            // Paid off (a last instalment under the minimum) while still in debt: the next offer counts
                            // three fresh nights from the pay-off, not the nights in debt while the loan ran.
                            dg.ledger.funds = -5000;
                            dg.story.loanOwed = 300;
                            dg.story.debtDays = 7;
                            int offerNight = -1;
                            for (int night = 1; night <= 6 && offerNight < 0; night++)
                            {
                                DebtCrisis.Daily(dg, map, 0);
                                if (night == 2 && (dg.story.loanOwed != 0 || dg.ledger.funds != -5300))
                                    problems0.Add($"the last instalments left {dg.story.loanOwed} owed and funds {dg.ledger.funds} (expected 0 and -5300)");
                                if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_DebtCrisis"))) offerNight = night;
                            }
                            if (offerNight != 4) problems0.Add($"after paying off the loan in debt, the next offer came on night {offerNight} (expected 4: the pay-off night and two more)");
                            // Turned down: the offer comes back after five more nights in debt, not before.
                            var again = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_DebtCrisis"));
                            if (again != null)
                            {
                                again.answered = true;
                                PrisonDecisions.Choose(again, 1);
                                Find.LetterStack.RemoveLetter(again);
                                int reoffer = -1;
                                for (int night = 1; night <= 6 && reoffer < 0; night++)
                                {
                                    DebtCrisis.Daily(dg, map, 0);
                                    if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_DebtCrisis"))) reoffer = night;
                                }
                                if (dg.story.loanOwed != 0) problems0.Add("refusing the state loan still took one");
                                if (reoffer != DebtCrisis.ReofferDays) problems0.Add($"a refused loan was offered again on night {reoffer} (expected {DebtCrisis.ReofferDays})");
                                foreach (var dl2 in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_DebtCrisis")).ToList())
                                {
                                    dl2.answered = true;
                                    Find.LetterStack.RemoveLetter(dl2);
                                }
                            }
                        }
                        dg.ledger.funds = fundsBefore;
                        dg.reputation = repBefore;
                        dg.story.loanOwed = 0;
                        dg.story.debtDays = 0;
                    }
                    // A non-guard left to sink who is still miserable quits (keeps the guard roster intact for the run).
                    Pawn sinking = staffNow.FirstOrDefault(x => !GuardUtility.IsGuard(x) && x != tired
                                                                && staffNow.Count(o => o.GetComp<CompStaff>().role == x.GetComp<CompStaff>().role) >= 2);
                    if (sinking != null)
                    {
                        var sl = StaffLives.Ask("RP_Decision_Burnout", sinking, "test", "test", 12);
                        sl.answered = true;
                        PrisonDecisions.Choose(sl, 2);
                        Find.LetterStack.RemoveLetter(sl);
                        sinking.needs.mood.CurLevel = 0.05f;
                        int quit = g.stats.staffBurnedOut, resigned = g.stats.resignations;
                        // (Only a burnout counts as one: a long-serving officer with no promotion quits over pay.)
                        int burnedExpected = StaffCareers.ReasonFor(sinking, sinking.GetComp<CompStaff>()) == ResignReason.Burnout ? 1 : 0;
                        StaffLives.Daily(map);
                        if (g.stats.staffBurnedOut != quit + burnedExpected || g.stats.resignations != resigned + 1 || sinking.GetComp<CompStaff>().role != null) problems0.Add("an ignored, miserable officer never quit");
                    }
                }
            }

            private void EvOverdose()
            {
                // Overdose: the wing sees it, and a named dealer is searched when the warden goes after the supply.
                {
                    List<Pawn> pair = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && DeathRowSystem.Ext(x.Inmate()) == null).Take(2).ToList();
                    if (pair.Count == 2)
                    {
                        Pawn user = pair[0], seller = pair[1];
                        user.Inmate().lastDealerId = seller.thingIDNumber;
                        user.Inmate().lastOverdoseTick = -999999;
                        seller.Inmate().suspected = false;
                        Hediff od = HediffMaker.MakeHediff(HediffDefOf.DrugOverdose, user);
                        od.Severity = 0.6f;
                        user.health.AddHediff(od);
                        int ods = g.stats.overdoses;
                        Overdoses.Hourly(map);
                        var ol = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Overdose"));
                        if (g.stats.overdoses != ods + 1 || ol == null) problems0.Add("an overdose went unnoticed");
                        else
                        {
                            ol.answered = true;
                            PrisonDecisions.Choose(ol, 0);
                            Find.LetterStack.RemoveLetter(ol);
                            if (!seller.Inmate().suspected) problems0.Add("going after the supply did not put the named dealer under suspicion");
                            // "Get them help": a referral to treatment, which makes them eligible whatever their crime.
                            ProgramDef rehab = DefDatabase<ProgramDef>.GetNamed("RP_SubstanceAbuse");
                            user.Inmate().passedPrograms.Remove(rehab);
                            user.Inmate().suppressed = 0f;
                            PrisonDecisions.Choose(ol, 1);
                            if (user.Inmate().referral != rehab || !rehab.Eligible(user, user.Inmate())) problems0.Add("getting an overdose victim help did not refer them to treatment");
                            user.Inmate().referral = null;
                            // A referral never overrides suitability: spiritual guidance stays for the religious.
                            ProgramDef faith = DefDatabase<ProgramDef>.GetNamed("RP_SpiritualGuidance");
                            bool wasReligious = user.Inmate().religious;
                            user.Inmate().religious = false;
                            user.Inmate().referral = faith;
                            if (faith.Eligible(user, user.Inmate())) problems0.Add("a referral put a non-religious inmate in spiritual guidance");
                            user.Inmate().religious = wasReligious;
                            user.Inmate().referral = null;
                        }
                        if (user.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.DrugOverdose) is { } h) user.health.RemoveHediff(h);
                    }
                }
            }

            private void EvGangTruce()
            {
                // Gang truce: rivals stop being rivals while it holds, a fight across it breaks it; the sit-down either holds
                // or ends with the leaders as enemies.
                {
                    GangDef ga = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault(x => x.rivals.Count > 0);
                    GangDef gb = ga?.rivals[0];
                    List<Pawn> pair = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && DeathRowSystem.Ext(x.Inmate()) == null).Take(2).ToList();
                    if (ga != null && pair.Count == 2)
                    {
                        Pawn la = pair[0], lb = pair[1];
                        GangDef oldA = la.Inmate().gang, oldB = lb.Inmate().gang;
                        la.Inmate().gang = ga;
                        lb.Inmate().gang = gb;
                        g.story.truces.Add(new GangTruce { a = ga, b = gb, untilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay });
                        if (GangUtility.AreRivals(ga, gb)) problems0.Add("rival gangs still rivals during a truce");
                        GangTruces.Notify_Fight(la, lb);
                        if (!GangUtility.AreRivals(ga, gb) || g.stats.trucesBroken == 0) problems0.Add("a fight across a truce did not break it");
                        if (PrisonRelations.Enemies(la, lb)) la.relations.RemoveDirectRelation(RPRelationDefOf.RP_Enemy, lb);
                        var tl = ChoiceLetter_PrisonDecision.Make("RP_Decision_Truce", "test", "test", map, la);
                        tl.pawns.Add(la);
                        tl.pawns.Add(lb);
                        PrisonDecisions.Choose(tl, 0);
                        if (!GangTruces.InTruce(ga, gb) && !PrisonRelations.Enemies(la, lb)) problems0.Add("a hosted sit-down neither made a truce nor ended in a brawl");
                        if (GangTruces.InTruce(ga, gb))
                        {
                            int truces = g.story.truces.Count;
                            PrisonDecisions.Choose(tl, 0);
                            if (g.story.truces.Count > truces) problems0.Add("a second sit-down for the same gangs stacked a second truce");
                        }
                        g.story.truces.Clear();
                        // Leave them as they were for the later tests.
                        if (PrisonRelations.Enemies(la, lb)) la.relations.RemoveDirectRelation(RPRelationDefOf.RP_Enemy, lb);
                        la.mindState.mentalStateHandler.CurState?.RecoverFromState();
                        la.Inmate().gang = oldA;
                        lb.Inmate().gang = oldB;
                    }
                }
            }

            private void EvRookieConned()
            {
                // Rookie conned: a real phone in an inmate's pocket; writing them up gets the inmate searched.
                {
                    int before = g.stats.rookieMistakes;
                    // Someone with the money to make it worth a rookie's while.
                    if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(InmateUtility.IsActiveInmate) is { } payer) payer.Inmate().cash = System.Math.Max(payer.Inmate().cash, 20);
                    // The plan's event sweep already conned one rookie; give exactly one guard on site a clean slate.
                    List<Pawn> rookies = StaffLives.OnSite(map).Where(GuardUtility.IsGuard).ToList();
                    foreach (Pawn r in rookies) r.GetComp<CompStaff>().rookieConned = r != rookies.FirstOrDefault();
                    // (A corrupt guard is not a rookie to be conned: the sweep can leave every guard on site corrupt, so the one with the clean slate is honest for the incident.)
                    CompStaff cleanSc = rookies.FirstOrDefault()?.GetComp<CompStaff>();
                    bool cleanWasCorrupt = cleanSc?.corrupt ?? false;
                    if (cleanSc != null) cleanSc.corrupt = false;
                    IncidentDef rookieDef = DefDatabase<IncidentDef>.GetNamed("RP_RookieConned");
                    rookieDef.Worker.TryExecute(new IncidentParms { target = map });
                    if (cleanWasCorrupt) cleanSc.corrupt = true;
                    var rl = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Rookie"));
                    if (g.stats.rookieMistakes != before + 1 || rl == null)
                        problems0.Add("no rookie mistake with new guards on staff (guards on site, with days employed / corrupt / already conned: "
                                      + $"{string.Join(", ", rookies.Select(x => $"{x.LabelShort} {StaffLives.DaysEmployed(x.GetComp<CompStaff>()):F1}/{x.GetComp<CompStaff>().corrupt}/{x.GetComp<CompStaff>().rookieConned}"))}; "
                                      + $"all guards of the staff {StaffCommute.AllStaff(map).Count(GuardUtility.IsGuard)}, an inmate with tension 25 or cash 10: {map.mapPawns.PrisonersOfColonySpawned.Count(x => x.Inmate() is { } xc && InmateUtility.IsActiveInmate(x) && (xc.tension >= 25f || xc.cash >= 10))}, "
                                      + $"the hour {GenLocalDate.HourOfDay(map)})");
                    else
                    {
                        Pawn conner = rl.pawns.First(x => x.IsPrisonerOfColony);
                        if (!conner.inventory.innerContainer.Any(t => t.def.defName == "RP_Cellphone")) problems0.Add("the rookie's phone never reached the inmate");
                        rl.answered = true;
                        PrisonDecisions.Choose(rl, 0);
                        Find.LetterStack.RemoveLetter(rl);
                        if (!conner.Inmate().suspected) problems0.Add("writing the rookie up did not get the inmate searched");
                    }
                    // Once per rookie: with the only clean rookie used up, the beat cannot fire again.
                    if (rookieDef.Worker.TryExecute(new IncidentParms { target = map })) problems0.Add("the same rookie can be conned twice");
                }
            }

            private void EvFlu()
            {
                // Flu: spreads within a room; vaccination stops it.
                {
                    List<Pawn> trio = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && !x.health.hediffSet.HasHediff(Outbreaks.Flu) && !x.Downed).Take(3).ToList();
                    if (trio.Count == 3)
                    {
                        float chance = Outbreaks.SpreadChance;
                        Outbreaks.SpreadChance = 1f;
                        g.story.outbreakUntilTick = -1;
                        DefDatabase<IncidentDef>.GetNamed("RP_Outbreak").Worker.TryExecute(new IncidentParms { target = map });
                        var ob = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Outbreak"));
                        Pawn zero = ob?.pawns.FirstOrDefault();
                        if (!Outbreaks.Active || zero == null) problems0.Add("no flu outbreak");
                        else
                        {
                            Pawn near = trio.First(x => x != zero), far = trio.Last(x => x != zero && x != near);
                            void MoveTo(Pawn x)
                            {
                                x.pather.StopDead();
                                x.Position = zero.Position;
                                x.Notify_Teleported();
                            }
                            MoveTo(near);
                            Outbreaks.Hourly(map);
                            if (!near.health.hediffSet.HasHediff(Outbreaks.Flu)) problems0.Add("flu did not spread within a room");
                            // In the yard or another big space it takes close contact, not just the same room.
                            if (far.health.hediffSet.GetFirstHediffOfDef(Outbreaks.Flu) is { } caught) far.health.RemoveHediff(caught);
                            Room zr = zero.GetRoom();
                            if (zr != null && (zr.PsychologicallyOutdoors || zr.CellCount > Outbreaks.CloseRoomCells)
                                && zr.Cells.Where(c => c.Standable(map) && !map.mapPawns.PrisonersOfColonySpawned.Any(o => Outbreaks.Sick(o) && c.InHorDistOf(o.Position, 8f)))
                                    .TryRandomElement(out IntVec3 distant))
                            {
                                far.pather.StopDead();
                                far.Position = distant;
                                far.Notify_Teleported();
                                Outbreaks.Hourly(map);
                                if (far.health.hediffSet.HasHediff(Outbreaks.Flu)) problems0.Add("flu jumped across a big room to someone 8+ cells from anyone sick");
                            }
                            ob.answered = true;
                            PrisonDecisions.Choose(ob, 0);
                            Find.LetterStack.RemoveLetter(ob);
                            // With spread forced to certain, the first round may already have reached them elsewhere.
                            if (far.health.hediffSet.GetFirstHediffOfDef(Outbreaks.Flu) is { } early) far.health.RemoveHediff(early);
                            MoveTo(far);
                            Outbreaks.Hourly(map);
                            if (far.health.hediffSet.HasHediff(Outbreaks.Flu)) problems0.Add("flu spread after everyone was vaccinated");
                        }
                        Outbreaks.SpreadChance = chance;
                        g.story.outbreakUntilTick = -1;
                        foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned)
                            if (x.health.hediffSet.GetFirstHediffOfDef(Outbreaks.Flu) is { } h) x.health.RemoveHediff(h);
                    }
                }
            }

            private void EvInnocence()
            {
                // Innocence: backing a guilty inmate's appeal costs the board's trust; a backed innocent walks or is denied.
                {
                    List<Pawn> pair = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && DeathRowSystem.Ext(x.Inmate()) == null).Take(2).ToList();
                    if (pair.Count == 2)
                    {
                        Pawn guilty = pair[0], wronged = pair[1];
                        guilty.Inmate().innocent = false;
                        wronged.Inmate().innocent = true;
                        foreach (Pawn x in pair)
                        {
                            var il = ChoiceLetter_PrisonDecision.Make("RP_Decision_Innocence", "test", "test", map, x);
                            il.pawns.Add(x);
                            PrisonDecisions.Choose(il, 0);
                            if (!x.Inmate().appealBacked || x.Inmate().appealResolveTick < 0) problems0.Add("backing an appeal scheduled nothing");
                        }
                        float trust = g.story.boardTrust;
                        Innocence.Resolve(g, guilty, guilty.Inmate());
                        if (g.story.boardTrust >= trust || (guilty.guest?.Released ?? true)) problems0.Add("backing a guilty inmate's appeal cost nothing (or freed them)");
                        int ex = g.stats.exonerations;
                        Innocence.Resolve(g, wronged, wronged.Inmate());
                        bool walked = g.stats.exonerations == ex + 1 && (wronged.guest?.Released ?? true);
                        bool denied = wronged.IsPrisonerOfColony && wronged.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPInnocenceDefOf.RP_AppealDenied);
                        if (!walked && !denied) problems0.Add("a backed innocent's appeal neither freed them nor was denied");
                        foreach (Pawn x in pair) if (x.Inmate() is { } xc) xc.appealResolveTick = -1;
                    }
                }
            }

            private void EvInspection()
            {
                // Inspection: announced, the deep clean really cleans, the tour grades and pays; a bribe can come out.
                {
                    g.story.inspectionTick = -1;
                    IntVec3 dirty = map.areaManager.Home.ActiveCells.FirstOrDefault(c => c.Standable(map) && c.GetFirstBuilding(map) == null);
                    if (dirty.IsValid) FilthMaker.TryMakeFilth(dirty, map, ThingDefOf.Filth_Dirt);
                    DefDatabase<IncidentDef>.GetNamed("RP_Inspection").Worker.TryExecute(new IncidentParms { target = map });
                    var nl = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Inspection"));
                    if (g.story.inspectionTick < 0 || nl == null) problems0.Add("no inspection notice");
                    else
                    {
                        if (g.story.inspection.noticeKeys == null) problems0.Add("the inspection's notice did not take what the prison gave then (the inspector reads only that)");
                        nl.answered = true;
                        PrisonDecisions.Choose(nl, 0);
                        Find.LetterStack.RemoveLetter(nl);
                        if (g.story.inspectionPrep != 1 || map.listerFilthInHomeArea.FilthInHomeArea.Count > 0) problems0.Add("the deep clean left filth in the home area");
                        int inspections = g.stats.inspections;
                        g.story.inspectionTick = Find.TickManager.TicksGame;
                        Inspections.Hourly(g, map);
                        if (g.stats.inspections != inspections + 1 || g.story.inspectionTick >= 0 || g.story.inspectionPrep != 0) problems0.Add("the announced inspection never happened");
                        if (g.story.inspection.noticeKeys != null) problems0.Add("the visit kept the notice's snapshot");
                    }
                    float rep = g.reputation;
                    int funds = g.ledger.funds;
                    g.story.bribeExposeTick = Find.TickManager.TicksGame;
                    Inspections.Hourly(g, map);
                    if (g.reputation >= rep || g.ledger.funds >= funds) problems0.Add("an exposed inspector bribe cost nothing");
                    // (The visit above wrote the live prison's findings into the inspector's book and brought an inspector: the later checks see no to-do lines of a visit nobody asked for, and no visitor.)
                    if (g.story.inspection.inspector is not { Spawned: true } || g.story.inspection.findings.Count > InspectionReport.MaxFindings) problems0.Add("the announced inspection brought no inspector, or a report over its cap");
                    InspectorLetGo(map);
                    g.story.inspection = new InspectionState();
                    var hl = ChoiceLetter_PrisonDecision.Make("RP_Decision_Holiday", "test", "test", map);
                    hl.money = 10;
                    PrisonDecisions.Choose(hl, 0);
                    if (!map.mapPawns.PrisonersOfColonySpawned.Any(x => x.needs.mood?.thoughts.memories.Memories.Any(m => m.def == RPCalendarDefOf.RP_HolidayMeal) == true))
                        problems0.Add("the holiday meal cheered nobody");
                }
            }

            private void EvBlackout()
            {
                // Blackout: the electrician cuts it short; a lockdown lasts until the power is back.
                {
                    int now = Find.TickManager.TicksGame, lockdownWas = g.lockdownUntilTick, blackoutWas = g.blackoutUntilTick;
                    g.blackoutUntilTick = now + 5 * GenDate.TicksPerHour;
                    var bl = ChoiceLetter_PrisonDecision.Make("RP_Decision_Blackout", "test", "test", map);
                    PrisonDecisions.Choose(bl, 1);
                    if (g.blackoutUntilTick > now + IncidentWorker_Blackout.ElectricianHours * GenDate.TicksPerHour) problems0.Add("the emergency electrician did not shorten the blackout");
                    g.blackoutUntilTick = now + 3 * GenDate.TicksPerHour;
                    PrisonDecisions.Choose(bl, 0);
                    if (g.lockdownUntilTick < g.blackoutUntilTick) problems0.Add("the blackout lockdown ends before the power is back");
                    g.lockdownUntilTick = lockdownWas;
                    g.blackoutUntilTick = blackoutWas;
                }
            }
        }
    }
}
