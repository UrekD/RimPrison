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
        // Visit policies, fear, lockdown, contraband and phone taps, breaches, reclassification, corruption, feuds and friendships, the press, staff hurt, parole, reoffenders and a death's aftermath. (the shared state is in EventsRun.cs)
        private sealed partial class EventsRun
        {
            private void EvVisitPolicies()
            {
                // Visit policies: "no visits" for every level means nobody is visited, however lonely.
                foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading) g.visitPolicies[sec] = VisitPolicy.None;
                foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned)
                {
                    if (x.needs.TryGetNeed(RPVisitDefOf.RP_Family) is { } fam) fam.CurLevel = 0f;
                    if (x.Inmate() is { } xc && xc.familyCount == 0)
                    {
                        Relatives.Of(xc).Add(Relatives.Make());
                        Relatives.Recount(xc);
                    }
                }
                // (A visit already under way when the policy changed is not one scheduled under it: only the visits that start in the twenty hours count.)
                var visitedBefore = new HashSet<Pawn>(map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate()?.visitor != null));
                for (int t = 0; t < 20; t++) VisitationSystem.HourlyTick(map, g, 12);
                int visiting = map.mapPawns.PrisonersOfColonySpawned.Count(x => x.Inmate()?.visitor != null && !visitedBefore.Contains(x));
                g.visitPolicies.Clear();
                if (visiting > 0) problems0.Add($"{visiting} visits scheduled despite a no-visits policy");
            }

            private void EvFearAndProtectiveCustody()
            {
                // Fear: a terrified inmate asks for protective custody; accepting moves them.
                Pawn scared = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && x.Inmate().security?.defName != "RP_Protective");
                if (scared != null)
                {
                    CompInmate sc = scared.Inmate();
                    // (Their fear is put back after the request: a terrified inmate who found no protective bed stays in their cell, as they should, and the lockdown check below would count
                    // them as confined by something else than the lockdown: "fear 97, regime FreeTime, now Lockup", the flake of two events runs in six.)
                    float scaredFear0 = sc.fear;
                    sc.fear = 100f;
                    sc.lastPcRequestTick = -999999;
                    FearSystem.Hourly(scared, sc, PrisonMap.Of(map));
                    var pcLetter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_ProtectiveCustody"));
                    if (pcLetter == null) problems0.Add("no protective custody request from a terrified inmate");
                    else
                    {
                        // Only moved when a protective custody wing bed is free (no moves on paper, no open dorm beds).
                        bool pcRoom = PrisonMap.Of(map).PrisonerBeds.Any(b => b.Spawned && IntakeSystem.WingOf(b)?.defName == "RP_Protective" && b.OwnersForReading.Count < b.SleepingSlotsCount);
                        pcLetter.answered = true;
                        PrisonDecisions.Accept(pcLetter);
                        Find.LetterStack.RemoveLetter(pcLetter);
                        if ((sc.security?.defName == "RP_Protective") != pcRoom) problems0.Add($"accepted protective custody request: moved={sc.security?.defName == "RP_Protective"} room={pcRoom}");
                    }
                    sc.fear = scaredFear0;
                }
            }

            private void EvWingLockdown()
            {
                // Wing lockdown: only the locked level is confined.
                SecurityLevelDef minSec = DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum");
                // (What each was doing before it, against after: the locked level is in its cells, everyone else as they were. Read off the regime's slot alone, the expectation missed the other
                // ways an inmate is in their cell - fear of 60 or more hides them in free time (round 4 put back one check's fear 100; the events and real hours before this leave
                // others anywhere up to 100, see the "[fear]" lines), a yard ban, today's orders - and called those confined by the lockdown: "Latch RP_Medium now RP_Lockup, regime RP_FreeTime, fear 75".)
                int hour = GenLocalDate.HourOfDay(map);
                var before = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null).ToDictionary(x => x, x => g.CurrentActivity(x));
                g.StartWingLockdown(minSec, 1);
                bool Wrong(Pawn x) => x.Inmate() is { forcedActivity: null } xc && before.TryGetValue(x, out RegimeActivityDef was)
                    && (xc.security == minSec ? g.CurrentActivity(x) != RPDefOf.RP_Lockup : g.CurrentActivity(x) != was);
                if (map.mapPawns.PrisonersOfColonySpawned.Any(Wrong))
                {
                    // (Who: the inmate, their level, what they did before it and what the regime says, and the other ways the activity is changed (fear, a yard ban).)
                    IEnumerable<string> wrong = map.mapPawns.PrisonersOfColonySpawned.Where(Wrong)
                        .Select(x => $"{x.LabelShort} {x.Inmate().security?.defName} now {g.CurrentActivity(x)?.defName} (before {before[x]?.defName}), regime {g.RegimeFor(x.Inmate().security).At(hour)?.defName}, today {g.SlotAt(x.Inmate().security, hour)?.defName}, fear {x.Inmate().fear:F0} (hiding {g.Hiding(x.Inmate(), g.ActivityAt(x.Inmate(), hour))}), yard ban {Sanctions.Banned(x.Inmate(), Sanction.LoseYard)}, locked down {g.LockedDown(x.Inmate())}");
                    problems0.Add("wing lockdown confined the wrong inmates: " + string.Join("; ", wrong));
                }
                // (Who of the other levels sat it out in their cell for a reason of their own, told: the lockdown says nothing about them.)
                List<string> hiding = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { forcedActivity: null } xc && xc.security != minSec && before.TryGetValue(x, out RegimeActivityDef was)
                    && was == RPDefOf.RP_Lockup && g.SlotAt(xc.security, hour) != RPDefOf.RP_Lockup).Select(x => $"{x.LabelShort} (fear {x.Inmate().fear:F0}, yard ban {Sanctions.Banned(x.Inmate(), Sanction.LoseYard)})").ToList();
                if (hiding.Count > 0) RPLog.Message($"[RimPrison:test] wing lockdown: in their cells in free time before it, for a reason of their own (nothing the lockdown did can show on them): {string.Join(", ", hiding)}");
            }

            private void EvSmugglingMergesStacks()
            {
                // Smuggling the same thing twice merges the stacks: what Give returns must be the stack in their pockets.
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(InmateUtility.IsActiveInmate) is { } carrier
                    && ContrabandUtility.AllContraband.FirstOrDefault(d => ContrabandUtility.Ext(d).kind == ContrabandKind.Drug) is { } drug)
                {
                    ContrabandUtility.Give(carrier, drug);
                    Thing second = ContrabandUtility.Give(carrier, drug);
                    if (second != null && second.holdingOwner != carrier.inventory.innerContainer) problems0.Add("Give returned a smuggled item that merged away");
                    foreach (Thing t in carrier.inventory.innerContainer.Where(t => t.def == drug).ToList()) t.Destroy();
                }
            }

            private void EvCellStash()
            {
                // Cell stash: a cell search finds hidden contraband.
                Pawn stasher = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && InmateUtility.CellOf(x) != null);
                if (stasher != null && guard != null)
                {
                    CompInmate stc = stasher.Inmate();
                    for (int k = 0; k < 3; k++)
                        if (ContrabandUtility.GiveSmuggled(stasher, ContrabandKind.Drug) is { } item)
                            stasher.inventory.innerContainer.TryTransferToContainer(item, stc.stash, item.stackCount);
                    int hidden = stc.stash.Count;
                    // (Searches until something is found, a few at the most: an item is found with 85% times the guard's morale (a tired guard half of that, and one on the take
                    // a seventh), so two searches can miss everything, and did, now and then. What is tested is that a search finds, not by how much.)
                    int searches = 0;
                    while (searches < 25 && stc.stash.Count >= hidden)
                    {
                        TunnelSystem.SearchCell(guard, InmateUtility.CellOf(stasher));
                        searches++;
                    }
                    RPLog.Message($"[RimPrison:test] stash: {hidden} hidden, {stc.stash.Count} left after {searches} cell search(es)");
                    if (hidden == 0 || stc.stash.Count >= hidden) problems0.Add("cell search found nothing in a stash");
                }
            }

            private void EvPhoneTaps()
            {
                // Phone taps: a tense inmate arranges a handover on the payphone; the tap flags their next visit.
                Pawn caller = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && x.Inmate().visitor == null);
                if (caller != null && guard != null)
                {
                    CompInmate cc = caller.Inmate();
                    if (cc.familyCount == 0)
                    {
                        Relatives.Of(cc).Add(Relatives.Make());
                        Relatives.Recount(cc);
                    }
                    cc.tension = 60f;
                    Rand.PushState(7);
                    for (int k = 0; k < 40 && !cc.nextVisitFlagged; k++)
                    {
                        cc.smuggleArranged = false;
                        PhoneCalls.Notify_Call(caller, cc, tappable: true);
                    }
                    Rand.PopState();
                    if (!cc.nextVisitFlagged) problems0.Add("phone tap never flagged an arranged handover");
                    // Visitor search: the flagged visitor is searched thoroughly, caught, and visits are suspended.
                    else if (VisitationSystem.SpawnVisitor(map, caller, cc, booth: false))
                    {
                        cc.visitorContraband ??= ContrabandUtility.PickSmuggled(caller, ContrabandKind.Drug);
                        bool pending = cc.visitorSearchPending && cc.visitFlagged && !cc.nextVisitFlagged;
                        // A guard already on the search: no wave-through, however long the visitor has waited.
                        cc.visitorArrivedTick = Find.TickManager.TicksGame - VisitorSearch.WaveThroughTicks - 10;
                        Job claim = JobMaker.MakeJob(RPVisitorSearchDefOf.RP_SearchVisitor, cc.visitor, caller);
                        guard.Reserve(cc.visitor, claim);
                        if (VisitorSearch.Cleared(cc) || !cc.visitorSearchPending) problems0.Add("visitor waved through while a guard was searching them");
                        map.reservationManager.Release(cc.visitor, guard, claim);
                        // A tap during this visit flags the next one, not this one.
                        cc.nextVisitFlagged = true;
                        Pawn smuggler = cc.visitor;
                        int detained0 = g.stats.smugglersDetained;
                        // (A guard the sweep left corrupt can look away from a client's visitor: the search under test is an honest guard's.)
                        CompStaff searcherSc = guard.GetComp<CompStaff>();
                        bool searcherCorrupt = searcherSc.corrupt;
                        searcherSc.corrupt = false;
                        Rand.PushState(11);
                        VisitorSearch.Resolve(guard, cc.visitor, caller, cc);
                        Rand.PopState();
                        searcherSc.corrupt = searcherCorrupt;
                        if (g.stats.smugglersDetained != detained0) problems0.Add("smuggler counted as detained before the police collected them");
                        // Caught smugglers are held for the police (or bolted).
                        if (smuggler.GetLord()?.LordJob is not LordJob_Arrest && g.stats.smugglersFled == 0) problems0.Add("caught smuggler neither detained nor fled");
                        RPLog.Message($"[RimPrison:test] visitor search: pending={pending} ban={cc.visitBanUntilTick > Find.TickManager.TicksGame} visitor={cc.visitor?.LabelShort}");
                        if (!pending) problems0.Add("flagged visitor not queued for a search");
                        if (cc.visitBanUntilTick <= Find.TickManager.TicksGame || cc.visitor != null) problems0.Add("flagged smuggler not caught by a thorough search");
                        if (!cc.nextVisitFlagged) problems0.Add("a tap made during a visit was spent on that visit");
                        cc.nextVisitFlagged = false;
                    }
                }
            }

            private void EvBreachClassification()
            {
                // Breach classification: a door jammed open by an item is a real breach, not a "momentary" opening.
                if (map.listerBuildings.AllBuildingsColonistOfClass<Building_Door>().FirstOrDefault(d => d.Position.GetFirstItem(map) == null
                    && !map.mapPawns.AllPawnsSpawned.Any(x => x.Position.InHorDistOf(d.Position, 3f))) is { } jamDoor)
                {
                    jamDoor.StartManualOpenBy(null);
                    Thing jam = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Steel), jamDoor.Position, map);
                    bool free = jamDoor.FreePassage, momentary = EscapeUtility.OpenOnlyForAPawn(jamDoor);
                    jam.Destroy();
                    RPLog.Message($"[RimPrison:test] jammed door: freePassage={free} momentary={momentary}");
                    if (!free || momentary) problems0.Add("a door jammed open by an item counted as a momentary opening");
                }
            }

            private void EvReclassification()
            {
                // Reclassification: one cluster of fights moves an inmate one level, not two on consecutive days.
                Pawn brawler = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && x.Inmate().security?.demoteTo?.demoteTo != null);
                if (brawler != null)
                {
                    CompInmate bc = brawler.Inmate();
                    SecurityLevelDef from = bc.security, next = from.demoteTo;
                    bool room = InmateUtility.HasRoomIn(brawler, next);
                    MisconductDef fight = DefDatabase<MisconductDef>.GetNamed("RP_Fight");
                    for (int k = 0; k < 3; k++) bc.AddRecord(new MisconductRecord { def = fight, tick = Find.TickManager.TicksGame, seen = true });
                    ReclassificationSystem.Daily(map, g);
                    SecurityLevelDef after1 = bc.security;
                    ReclassificationSystem.Daily(map, g);
                    RPLog.Message($"[RimPrison:test] reclass: {from.defName} -> {after1.defName} -> {bc.security.defName} (room={room})");
                    if (after1 != (room ? next : from)) problems0.Add($"reclassification after three fights: {after1.defName} (room={room})");
                    if (bc.security != after1) problems0.Add("reclassification cascaded on the same incidents");
                }
            }

            private void EvRiotJoiners()
            {
                // Riot joiners: on record for it.
                Pawn follower = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && !x.InMentalState && !x.WorkTagIsDisabled(WorkTags.Violent));
                if (follower != null)
                {
                    CompInmate fc = follower.Inmate();
                    int before = fc.records.Count(r => r.def == RPRiotDefOf.RP_RiotJoined);
                    bool joined = MisconductWorker_Riot.Join(follower, fc, PrisonMap.Of(map), null);
                    if (joined) follower.MentalState.RecoverFromState();
                    if (joined && fc.records.Count(r => r.def == RPRiotDefOf.RP_RiotJoined) != before + 1) problems0.Add("riot joiner left no misconduct record");
                    if (joined && RPRiotDefOf.RP_Riot.Worker.CanDo(follower, fc)) problems0.Add("a fresh rioter could start another riot within the cooldown");
                }
            }

            private void EvInBedJoy()
            {
                // In-bed joy on a bed someone else holds (a guard who just put a restrained inmate there): no job, no error.
                Pawn lier = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && !x.InMentalState && !x.Downed
                    && x.ownership?.OwnedBed is { Spawned: true } ob && ob.CurOccupants.Count() == 0);
                // (Only when the guard can hold it: someone else may already have, which made the setup fail now and then.)
                if (lier != null && guard != null && guard.CanReserve(lier.ownership.OwnedBed))
                {
                    Building_Bed lb = lier.ownership.OwnedBed;
                    Job hold = JobMaker.MakeJob(JobDefOf.Wait, lb);
                    guard.Reserve(lb, hold, 1, 0);
                    lier.Position = lb.GetSleepingSlotPos(0);
                    lier.Notify_Teleported();
                    lier.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture), JobCondition.InterruptForced);
                    lier.jobs.posture = PawnPosture.LayingInBed;
                    bool inBed = lier.CurrentBed() == lb;
                    Job joy = DefDatabase<JoyGiverDef>.GetNamed("Pray").Worker.TryGiveJobWhileInBed(lier);
                    map.reservationManager.Release(lb, guard, hold);
                    lier.jobs.posture = PawnPosture.Standing;
                    lier.jobs.EndCurrentJob(JobCondition.InterruptForced);
                    RPLog.Message($"[RimPrison:test] in-bed joy on a held bed: inBed={inBed} job={joy?.def.defName}");
                    if (inBed && joy != null) problems0.Add("in-bed joy given on a bed reserved by someone else");
                }
            }

            private void EvCorruption()
            {
                // Corruption: a guard on an inmate's payroll looks away; a staff search catches them carrying; internal
                // affairs suspends, and the investigation ends in dismissal and arrest. A second one is turned.
                List<Pawn> guards = map.mapPawns.FreeColonistsSpawned.Where(GuardUtility.IsGuard).ToList();
                Pawn client = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x));
                if (guards.Count >= 3 && client != null)
                {
                    Pawn bent = guards[guards.Count - 1];
                    CompStaff bsc = bent.GetComp<CompStaff>();
                    bsc.corrupt = true;
                    bsc.patron = client;
                    bsc.patronGang = null;
                    if (!CorruptionSystem.LooksAway(bent, client)) problems0.Add("corrupt guard does not look away for their patron");
                    Pawn other = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x != client && InmateUtility.IsActiveInmate(x) && x.Inmate().gang == null);
                    if (other != null && CorruptionSystem.LooksAway(bent, other)) problems0.Add("corrupt guard looks away for a non-client");
                    StaffSearchPolicy before = g.staffSearch;
                    g.staffSearch = StaffSearchPolicy.All;
                    int caught0 = g.stats.staffCaught;
                    Rand.PushState(5);
                    for (int k = 0; k < 30 && g.stats.staffCaught == caught0; k++) CorruptionSystem.StartShift(bent, bsc);
                    Rand.PopState();
                    g.staffSearch = before;
                    var ia = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_InternalAffairs") && l.pawns.Contains(bent));
                    if (g.stats.staffCaught == caught0) problems0.Add("staff search never caught a carrying corrupt guard");
                    else if (ia == null) problems0.Add("no internal affairs case after a guard was caught carrying");
                    else
                    {
                        ia.answered = true;
                        PrisonDecisions.Choose(ia, 1);
                        Find.LetterStack.RemoveLetter(ia);
                        if (!CorruptionSystem.Suspended(bent) || GuardUtility.IsGuard(bent)) problems0.Add("suspended guard still on duty");
                        bsc.suspendedUntilTick = Find.TickManager.TicksGame - 1;
                        CorruptionSystem.Hourly(bent, bsc);
                        if (bent.Faction == Faction.OfPlayer || bent.GetLord()?.LordJob is not LordJob_Arrest) problems0.Add("guilty guard not dismissed and arrested after the investigation");
                    }
                    Pawn turned = guards[guards.Count - 2];
                    CompStaff tsc = turned.GetComp<CompStaff>();
                    tsc.corrupt = true;
                    tsc.patron = client;
                    CorruptionSystem.AddSuspicion(turned, tsc, 60f, "RP_Sus_Traced");
                    var ia2 = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_InternalAffairs") && l.pawns.Contains(turned));
                    if (ia2 == null) problems0.Add("no internal affairs case at 60% suspicion");
                    else
                    {
                        ia2.answered = true;
                        PrisonDecisions.Choose(ia2, 3);
                        Find.LetterStack.RemoveLetter(ia2);
                        if (!tsc.doubleAgent || CorruptionSystem.LooksAway(turned, client)) problems0.Add("turned guard is not a double agent");
                        int cases = g.stats.iaCases;
                        CorruptionSystem.AddSuspicion(turned, tsc, 80f, "RP_Sus_Traced");
                        if (g.stats.iaCases != cases) problems0.Add("internal affairs opened a case against our own double agent");
                    }
                }
            }

            private void EvFeuds()
            {
                // Feuds: two inmates of one wing become enemies; the warden's letter offers separation and mediation.
                wing = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && !g.story.InArc(x)).ToList();
                Pawn fa = wing.FirstOrDefault(), fb = wing.FirstOrDefault(x => x != fa && x.Inmate().security == fa?.Inmate().security);
                if (fa != null && fb != null)
                {
                    int opinionBefore = fa.relations.OpinionOf(fb);
                    StoryArc feud = FeudArc.Begin(fa, fb, "test");
                    var fl = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => feud != null && l.arcId == feud.id);
                    if (feud == null || !PrisonRelations.Enemies(fa, fb) || !PrisonRelations.Enemies(fb, fa)) problems0.Add("feud did not make them enemies");
                    else if (fl == null) problems0.Add("no decision letter for a feud");
                    else
                    {
                        // Being carried (to solitary, to a bed) does not end a feud.
                        // (Any free colonist on site will do as the carrier: the guard may be busy carrying someone already.)
                        Pawn feudCarrier = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(f => !f.Downed && f.carryTracker.CarriedThing == null);
                        if (feudCarrier == null || feudCarrier.carryTracker.TryStartCarry(fb, 1, reserve: false) != 1)
                            problems0.Add($"feud test: nobody could carry an inmate (carrier {feudCarrier?.LabelShort ?? "none"})");
                        else
                        {
                            bool valid = feud.def.Worker.Valid(feud);
                            feudCarrier.carryTracker.TryDropCarriedThing(feudCarrier.Position, ThingPlaceMode.Near, out _);
                            if (!valid) problems0.Add("a feud ends while one of them is carried");
                        }
                        int opinion = fa.relations.OpinionOf(fb);
                        bool canSeparate = fl.decision.Worker.Disabled(fl, 1) == null;
                        // (Their home cell, not the cell of the bed they own: an inmate in solitary or carried off hurt keeps the bed they are lying in, and a move changes the bed they go back to.)
                        Room before = Cellmates.Home(fb, fb.Inmate());
                        Building_Bed ownedBefore = fb.ownership?.OwnedBed, homeBefore = fb.Inmate().homeBed;
                        fl.answered = true;
                        PrisonDecisions.Choose(fl, canSeparate ? 1 : 0);
                        Find.LetterStack.RemoveLetter(fl);
                        Room after = Cellmates.Home(fb, fb.Inmate());
                        RPLog.Message($"[RimPrison:test] feud: opinion={opinion} separate={canSeparate} moved={after != before} (owned bed cell moved={InmateUtility.CellOf(fb) != ownedBefore?.GetRoom()}, "
                                      + $"away from the owned bed before={homeBefore != null}, after={fb.Inmate().homeBed != null})");
                        // Enemies think much less of each other (from wherever they started: kin or old friends may stay above 0).
                        // (Opinion stops at -100: two who already hated each other at -90 can only fall to it.)
                        if (opinion > System.Math.Max(-100, opinionBefore - 20)) problems0.Add($"enemies think no worse of each other (opinion {opinionBefore} -> {opinion})");
                        if (canSeparate && (after == before || after == Cellmates.Home(fa, fa.Inmate())))
                            problems0.Add($"separating a feud did not move them apart (home cell moved {after != before}, shares a cell with the other {after == Cellmates.Home(fa, fa.Inmate())}; owned bed {ownedBefore?.Label ?? "none"} -> {fb.ownership?.OwnedBed?.Label ?? "none"}, "
                                          + $"home bed {homeBefore?.Label ?? "none"} -> {fb.Inmate().homeBed?.Label ?? "none"})");
                        if (!canSeparate && feud.done) problems0.Add("letting a feud play out ended it at once");
                    }
                    // A second feud, mediated with a fixed outcome: the enmity goes if it works.
                    Pawn fc = wing.FirstOrDefault(x => x != fa && x != fb && !g.story.InArc(x));
                    Pawn fd = wing.FirstOrDefault(x => x != fa && x != fb && x != fc && !g.story.InArc(x));
                    if (fc != null && fd != null && FeudArc.Begin(fc, fd, "test") is { } feud2)
                    {
                        var fl2 = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.arcId == feud2.id);
                        if (fl2 != null)
                        {
                            fl2.answered = true;
                            Rand.PushState(3);
                            PrisonDecisions.Choose(fl2, 2);
                            Rand.PopState();
                            Find.LetterStack.RemoveLetter(fl2);
                            RPLog.Message($"[RimPrison:test] mediation: peace={feud2.done} enemies={PrisonRelations.Enemies(fc, fd)}");
                            if (feud2.done == PrisonRelations.Enemies(fc, fd)) problems0.Add("mediation outcome and relation disagree");
                        }
                    }
                }
            }

            private void EvFriendsStepIn()
            {
                // Friends step in: a friend of the one attacked goes for the attacker.
                {
                    List<Pawn> trio = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && !x.InMentalState && !x.Downed && x.Awake()
                        && !x.WorkTagIsDisabled(WorkTags.Violent)).Take(3).ToList();
                    if (trio.Count == 3)
                    {
                        Pawn att = trio[0], vic = trio[1], pal = trio[2];
                        if (!PrisonRelations.Friends(pal, vic)) pal.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, vic);
                        if (PrisonRelations.Enemies(att, vic)) att.relations.RemoveDirectRelation(RPRelationDefOf.RP_Enemy, vic);
                        pal.Position = vic.Position;
                        pal.Notify_Teleported();
                        PrisonRelations.Notify_Fight(att, vic);
                        bool steppedIn = pal.MentalStateDef == RPMentalStateDefOf.RP_AttackingInmate;
                        RPLog.Message($"[RimPrison:test] friend stepped in: {steppedIn} ({pal.MentalStateDef?.defName})");
                        if (!steppedIn) problems0.Add("a friend did not step in for the one attacked");
                        pal.MentalState?.RecoverFromState();
                    }
                }
            }

            private void EvFriendsInOpinion()
            {
                // Friends: the relation shows in opinion.
                Pawn fr1 = wing.LastOrDefault(), fr2 = wing.LastOrDefault(x => x != fr1 && !PrisonRelations.Enemies(fr1, x));
                if (fr1 != null && fr2 != null && !PrisonRelations.Friends(fr1, fr2))
                {
                    int before = fr1.relations.OpinionOf(fr2);
                    fr1.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, fr2);
                    if (fr1.relations.OpinionOf(fr2) < before + 25 || !PrisonRelations.Friends(fr2, fr1)) problems0.Add("friendship does not raise opinion both ways");
                }
            }

            private void EvFamiliesHaveNames()
            {
                // Families have names; familyCount is the living, in-touch ones.
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate() is { } fc0 && Relatives.Of(fc0).Count(r => r.CanVisit) != fc0.familyCount)
                        problems0.Add($"{p.LabelShort}: {fc0.familyCount} family but {Relatives.Of(fc0).Count(r => r.CanVisit)} named relatives in touch");
            }

            private void EvPress()
            {
                // The press: an interview's tone follows the report card; the outcome goes into the chronicle.
                if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_Press")) is { } press)
                {
                    int entries = g.story.entries.Count;
                    press.answered = true;
                    PrisonDecisions.Choose(press, 0);
                    Find.LetterStack.RemoveLetter(press);
                    if (g.story.entries.Count < entries + 2) problems0.Add("press decision left no chronicle entries");
                }
                else problems0.Add("no press request from the forced press event");
            }

            private void EvStaffHurtByAnInmate()
            {
                // Staff hurt by an inmate: colleagues feel it, and the staff ask for hazard pay; a commendation for a disarm.
                Pawn hurt = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(GuardUtility.IsGuard);
                Pawn culprit = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(InmateUtility.IsActiveInmate);
                if (hurt != null && culprit != null)
                {
                    g.story.lastHazardDemandTick = -999999;
                    StaffStories.Notify_StaffDowned(hurt, culprit);
                    var hz = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_HazardPay"));
                    if (hz == null) problems0.Add("no hazard pay demand after a guard went down");
                    else
                    {
                        hz.answered = true;
                        PrisonDecisions.Accept(hz);
                        Find.LetterStack.RemoveLetter(hz);
                        if (!map.mapPawns.FreeColonistsSpawned.Any(o => o.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(RPLifeDefOf.RP_HazardPay) != null))
                            problems0.Add("paid hazard pay but nobody noticed");
                    }
                    int before = hurt.GetComp<CompStaff>().commendations;
                    StaffStories.Notify_SubduedArmed(hurt, culprit);
                    if (hurt.GetComp<CompStaff>().commendations != before + 1) problems0.Add("commendation not counted");
                }
            }

            private void EvOrphanedCorruptGuard()
            {
                // A corrupt guard whose patron (no gang) leaves is off the payroll, not working for everyone.
                if (map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => GuardUtility.IsGuard(x) && x.GetComp<CompStaff>() is { corrupt: false }) is { } orphan
                    && map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x)) is { } exPatron)
                {
                    CompStaff osc = orphan.GetComp<CompStaff>();
                    osc.corrupt = true;
                    osc.patron = exPatron;
                    osc.patronGang = null;
                    CorruptionSystem.Notify_PatronGone(osc);
                    if (osc.corrupt) problems0.Add("corrupt guard stays corrupt after their patron left");
                }
            }

            private void EvParoleAskedOfTheWarden()
            {
                // Parole: the warden is asked about one hearing; a recommendation that backfires costs the board's trust.
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && !x.Inmate().IsLifer && DeathRowSystem.Ext(x.Inmate()) == null) is { } hearing)
                {
                    ParoleHearings.AskWarden(g, map, hearing);
                    var pl = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Parole"));
                    if (pl == null) problems0.Add("no parole hearing letter");
                    else
                    {
                        int entries = g.story.entries.Count;
                        pl.answered = true;
                        PrisonDecisions.Choose(pl, 1);
                        Find.LetterStack.RemoveLetter(pl);
                        if (g.story.entries.Count <= entries) problems0.Add("parole decision left no trace in the chronicle");
                    }
                    float trust = g.story.boardTrust;
                    ParoleHearings.Notify_Rearrested(g, new Rearrest { name = "test", recommended = true });
                    if (g.story.boardTrust >= trust) problems0.Add("a recommended parolee's rearrest did not cost the board's trust");
                }
            }

            private void EvReoffender()
            {
                // A reoffender comes back as the same person, on a second stay.
                if (map.mapPawns.PrisonersOfColonySpawned.LastOrDefault(x => InmateUtility.IsActiveInmate(x) && !x.Downed && DeathRowSystem.Ext(x.Inmate()) == null) is { } leaver
                    && IntakeSystem.FreePrisonerBeds(map, leaver.Inmate().security).Count > 0)
                {
                    leaver.Inmate().appealResolveTick = Find.TickManager.TicksGame + GenDate.TicksPerDay; // pending at release
                    // A job queued for their cell bed must not survive the release (it errored once the bed was unclaimed).
                    if (leaver.ownership?.OwnedBed is { } cellBed) leaver.jobs.jobQueue.EnqueueLast(JobMaker.MakeJob(JobDefOf.LayDown, cellBed));
                    leaver.Inmate().innocenceClaimed = true;
                    ReleaseSystem.Release(g, leaver);
                    if (leaver.jobs.jobQueue.Any(q => q.job.def == JobDefOf.LayDown)) problems0.Add("a job queued for the cell bed survived the release");
                    g.rearrests.RemoveAll(r => r.pawn == leaver);
                    leaver.DeSpawn();
                    Find.WorldPawns.PassToWorld(leaver, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
                    g.returning.Add(new Rearrest { name = leaver.LabelShort, pawn = leaver, dueTick = Find.TickManager.TicksGame });
                    Reoffenders.Hourly(g, map);
                    if (leaver.Spawned || !g.shuttle.IsMarshal(leaver)) problems0.Add($"a returning reoffender was put on the map by the hour they came due (spawned {leaver.Spawned}, waiting for the shuttle {g.shuttle.IsMarshal(leaver)})");
                    LandMarshalsNow(g, map);
                    RPLog.Message($"[RimPrison:test] reoffender: spawned={leaver.Spawned} prisoner={leaver.IsPrisonerOfColony} stays={leaver.Inmate()?.timesAdmitted}");
                    if (!leaver.Spawned || !leaver.IsPrisonerOfColony || leaver.Inmate()?.timesAdmitted != 2 || leaver.guest.Released)
                        problems0.Add("a returning reoffender did not come back as an inmate on their second stay");
                    else if (leaver.Inmate().appealResolveTick >= 0 || leaver.Inmate().innocenceClaimed)
                        problems0.Add("an appeal from the last stay followed a reoffender back in");
                }
            }

            private void EvDeathAftermath()
            {
                // Death aftermath (no one actually dies here): a murder charge, revenge, an inquiry, a cover-up exposed.
                {
                    List<Pawn> cast = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && DeathRowSystem.Ext(x.Inmate()) == null).Take(3).ToList();
                    if (cast.Count == 3)
                    {
                        Pawn victim = cast[0], killer = cast[1], pal = cast[2];
                        if (PrisonRelations.Enemies(pal, killer)) pal.relations.RemoveDirectRelation(RPRelationDefOf.RP_Enemy, killer);
                        if (!PrisonRelations.Friends(pal, victim)) pal.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, victim);
                        int trialsOpen = g.story.trials.Count;
                        DeathAftermath.Notify_Died(victim, killer, null, null);
                        if (!killer.Inmate().records.Any(r => r.def == RPDeathDefOf.RP_Murder)) problems0.Add("killer not charged with murder");
                        if (g.story.trials.Count != trialsOpen + 1 || Trials.TrialOf(g, killer) == null) problems0.Add("a killer was charged and no trial was set");
                        // (The case is the court checks' business, not this run's: it goes with the charge.)
                        g.story.trials.RemoveRange(trialsOpen, g.story.trials.Count - trialsOpen);
                        if (!PrisonRelations.Enemies(pal, killer)) problems0.Add("the victim's friend did not turn on the killer");
                        int inquiries = g.stats.inquiries;
                        DeathAftermath.Notify_Died(victim, null, HediffMaker.MakeHediff(HediffDefOf.Malnutrition, victim), null);
                        var iq = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Inquiry"));
                        if (g.stats.inquiries != inquiries + 1 || iq == null) problems0.Add("no inquiry into a death by neglect");
                        else
                        {
                            iq.answered = true;
                            PrisonDecisions.Choose(iq, 1);
                            Find.LetterStack.RemoveLetter(iq);
                        }
                        float rep = g.reputation;
                        g.story.coverUps.Add(new CoverUp { victim = victim.LabelShort, dueTick = Find.TickManager.TicksGame });
                        DeathAftermath.Hourly(g);
                        if (g.reputation >= rep || g.story.coverUps.Any(c => c.dueTick <= Find.TickManager.TicksGame)) problems0.Add("an exposed cover-up cost nothing");
                    }
                }
            }
        }
    }
}
