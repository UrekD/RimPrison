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
        // The incidents fired and their decision letters answered, then the rules that touch inmates: winter jackets, cell fit and managed cells, graduated sanctions, demands, release and escape. (the shared state is in EventsRun.cs)
        private sealed partial class EventsRun
        {
            private void EvFireIncidents()
            {
                var cats = new[] { DefDatabase<IncidentCategoryDef>.GetNamed("RP_PrisonEvent"), DefDatabase<IncidentCategoryDef>.GetNamed("RP_StoryBeat") };
                foreach (IncidentDef inc in DefDatabase<IncidentDef>.AllDefsListForReading.Where(d => cats.Contains(d.category)))
                {
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(inc.category, map);
                    parms.forced = true;
                    bool can = inc.Worker.CanFireNow(parms);
                    bool ok = inc.Worker.TryExecute(parms);
                    RPLog.Message($"[RimPrison:test] event {inc.defName}: canFire={can} executed={ok}");
                }
            }

            private void EvAnswerDecisionLetters()
            {
                // Decision letters: accept the transfer; leave the staff demand unanswered (it must time out into a strike).
                foreach (Letter l in Find.LetterStack.LettersListForReading.ToList())
                    if (l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_TransferRequest"))
                    {
                        d.answered = true;
                        PrisonDecisions.Accept(d);
                        Find.LetterStack.RemoveLetter(d);
                    }
            }

            private void EvSpecialArrivalsKeepToTheirLevel()
            {
                // Special arrivals only take beds their level may use (no maximum-security celebrity in a minimum wing).
                SecurityLevelDef maxSec = DefDatabase<SecurityLevelDef>.GetNamed("RP_Maximum");
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate() is { security: { } ps } pc && ps == maxSec && IntakeSystem.WingOf(p.ownership?.OwnedBed) is { } w && w != maxSec)
                        problems0.Add($"maximum-security {p.LabelShort} housed in the {w.defName} wing");
            }

            private void EvTransferWithNoBedPaysNothing()
            {
                // A transfer accepted when no bed is left pays nothing.
                {
                    ChoiceLetter_PrisonDecision empty = ChoiceLetter_PrisonDecision.Make("RP_Decision_TransferRequest", "test", "test", map);
                    empty.amount = 0;
                    empty.money = 5000;
                    int funds0 = PrisonGame.Instance.ledger.funds;
                    PrisonDecisions.Accept(empty);
                    if (PrisonGame.Instance.ledger.funds != funds0) problems0.Add($"transfer premium paid with no arrivals ({PrisonGame.Instance.ledger.funds - funds0})");
                }
            }

            private void EvWinterJackets()
            {
                // Winter jackets: issue in a (forced) cold month, collect into the store when it warms up.
                g = PrisonGame.Instance;
                ThingDef jacket = DefDatabase<ThingDef>.GetNamed("Apparel_Jacket");
                WinterClothing.Morning(map, g, coldOverride: true);
                int wearing = map.mapPawns.PrisonersOfColonySpawned.Count(x => x.apparel.WornApparel.Any(a => a.def == jacket));
                WinterClothing.Morning(map, g, coldOverride: false);
                int stillWearing = map.mapPawns.PrisonersOfColonySpawned.Count(x => x.apparel.WornApparel.Any(a => a.def == jacket));
                RPLog.Message($"[RimPrison:test] jackets: issued {wearing}, after warm-up {stillWearing}, store {g.jacketStock}");
                if (wearing == 0 || stillWearing > 0 || g.jacketStock < wearing) problems0.Add($"winter jackets: issued {wearing}, kept {stillWearing}, store {g.jacketStock}");
            }

            private void EvCellmateFit()
            {
                // Cellmate fit: a rival gang member's room scores below an empty room of the same wing.
                List<Pawn> two = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.ownership.OwnedBed != null).Take(2).ToList();
                if (two.Count == 2)
                {
                    CompInmate a = two[0].Inmate(), b = two[1].Inmate();
                    GangDef saints = DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"), ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
                    GangDef ga = a.gang, gb = b.gang;
                    a.gang = saints;
                    b.gang = ravens;
                    float withRival = Cellmates.Fit(two[0].ownership.OwnedBed, b);
                    a.gang = ravens;
                    float withFriend = Cellmates.Fit(two[0].ownership.OwnedBed, b);
                    a.gang = ga;
                    b.gang = gb;
                    RPLog.Message($"[RimPrison:test] cellmate fit: rival {withRival}, same gang {withFriend}");
                    if (withRival >= withFriend) problems0.Add($"cellmate fit does not avoid rivals ({withRival} vs {withFriend})");
                }
            }

            private void EvManagedCells()
            {
                // Managed cells: two rival gang members made to share a cell (a second bed spawned in it) are split up by the
                // daily review, into the bed freed by the move; the file names who they share with. Everything put back.
                if (map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { forcedActivity: null } && InmateUtility.IsActiveInmate(x) && !x.Downed && x.ownership.OwnedBed != null)
                        .GroupBy(x => x.Inmate().security).FirstOrDefault(grp => grp.Count() >= 2)?.Take(2).ToList() is { } rivals)
                {
                    (Pawn ra, Pawn rb) = (rivals[0], rivals[1]);
                    CompInmate rac = ra.Inmate(), rbc = rb.Inmate();
                    (Building_Bed bedA, Building_Bed bedB) = (ra.ownership.OwnedBed, rb.ownership.OwnedBed);
                    (GangDef gA, GangDef gB, bool kA, bool kB) = (rac.gang, rbc.gang, rac.gangKnown, rbc.gangKnown);
                    ThingDef bedDef = DefDatabase<ThingDef>.GetNamed("Bed");
                    Room cellA = bedA.GetRoom();
                    IntVec3 spot = cellA.Cells.FirstOrDefault(x => GenAdj.OccupiedRect(x, Rot4.North, bedDef.size).Cells.All(y => y.InBounds(map) && y.Standable(map) && y.GetEdifice(map) == null
                        && y.GetRoom(map) == cellA && y.GetFirstItem(map) == null && y.GetFirstPawn(map) == null));
                    if (spot.IsValid && spot != default)
                    {
                        // (The faction before spawning: set after, the bed is never listed as a colony building.)
                        var extra = (Building_Bed)ThingMaker.MakeThing(bedDef, ThingDefOf.WoodLog);
                        extra.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(extra, spot, map, Rot4.North);
                        extra.ForPrisoners = true;
                        (rac.gang, rbc.gang) = (DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"), DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"));
                        (rac.gangKnown, rbc.gangKnown) = (true, true);
                        rb.ownership.UnclaimBed();
                        rb.ownership.ClaimBedIfNonMedical(extra);
                        bool sharing = Cellmates.Of(ra).Contains(rb);
                        PrisonMap.Of(map).ForceDoorUpdate(); // (the spare bed joins the prisoner bed list, refreshed every 250 ticks)
                        Cellmates.InvalidateCells(); // (beds claimed directly, behind the cell cache's back)
                        string fileLine = ITab_InmateFile.StatusLines(ra, rac, Find.TickManager.TicksGame).Select(x => x.Item1).FirstOrDefault() ?? "";
                        Building_Bed bedAWas = ra.ownership.OwnedBed, bedBWas = rb.ownership.OwnedBed;
                        Cellmates.DailyReview(map);
                        bool split = !Cellmates.Of(ra).Contains(rb);
                        int pairMoves = (ra.ownership.OwnedBed != bedAWas ? 1 : 0) + (rb.ownership.OwnedBed != bedBWas ? 1 : 0);
                        if (!sharing || !fileLine.Contains(rb.LabelShort) || !split || pairMoves != 1)
                            problems0.Add($"managed cells: sharing {sharing}, file \"{fileLine}\", split {split}, the pair moved {pairMoves} times (one is enough)");
                        RPLog.Message($"[RimPrison:test] cells: {ra.LabelShort} and {rb.LabelShort} (rival gangs) split by the daily review; file: {fileLine}");
                        // Put everything back.
                        ra.ownership.UnclaimBed();
                        rb.ownership.UnclaimBed();
                        extra.Destroy();
                        ra.ownership.ClaimBedIfNonMedical(bedA);
                        rb.ownership.ClaimBedIfNonMedical(bedB);
                        (rac.gang, rbc.gang, rac.gangKnown, rbc.gangKnown) = (gA, gB, kA, kB);
                    }
                    else RPLog.Message("[RimPrison:test] cells: skipped (no room for a second bed in the cell)");
                }
            }

            private void EvGraduatedSanctions()
            {
                // Graduated sanctions: by default a seen misconduct makes solitary due (as before); with the policy changed,
                // cell confinement starts at once with no escort, a yard ban turns free time into lockup (the shared cell door
                // stays open for the cellmates), a visit or shop ban takes effect, a warning only goes on the record. Two bans
                // of different kinds run side by side; an offence while confined extends the confinement; a guard's restraint
                // after an offence with a light sanction does not send them to the hole.
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { forcedActivity: null, pendingPunishmentHours: 0 } && InmateUtility.IsActiveInmate(x) && x.ownership?.OwnedBed != null) is { } offender)
                {
                    CompInmate oc = offender.Inmate();
                    MisconductDef offence = DefDatabase<MisconductDef>.AllDefsListForReading.First(d => d.punishmentHours > 0 && !d.loud);
                    MisconductDef loudOffence = DefDatabase<MisconductDef>.AllDefsListForReading.First(d => d.punishmentHours > 0 && d.loud);
                    PunishmentPolicy pol = g.punishments;
                    var kindsWas = new Dictionary<MisconductDef, Sanction>(pol.kinds);
                    (int banWas, float suppressedWas, float tensionWas) = (oc.visitBanUntilTick, oc.suppressed, oc.tension);
                    var recordsCopy = new List<MisconductRecord>(oc.records);
                    (int misconductsWas, int punishmentsWas, int lightWas, int todayMisWas, int todayViolentWas) = (g.stats.misconducts, g.stats.punishments, g.stats.lightSanctions, g.today.misconducts, g.today.violent);
                    int assaultsWas = g.stats.assaults;
                    ChronicleEntry lastEntry = g.story.entries.LastOrDefault();
                    int tNow = Find.TickManager.TicksGame;
                    int freeHour = Enumerable.Range(0, 24).Where(h => g.RegimeFor(oc.security ?? InmateUtility.DefaultSecurity).At(h) == RPDefOf.RP_FreeTime).DefaultIfEmpty(-1).First();
                    void Clear() => (oc.forcedActivity, oc.forcedUntilTick, oc.pendingPunishmentHours, oc.yardBanUntilTick, oc.shopBanUntilTick, oc.visitBanUntilTick) = (null, -1, 0, -1, -1, -1);
                    var results = new List<string>();
                    foreach (Sanction s in System.Enum.GetValues(typeof(Sanction)))
                    {
                        Clear();
                        pol.SetKind(offence, s);
                        MisconductUtility.Record(offender, oc, PrisonMap.Of(map), offence, evidence: true, chronicle: false);
                        bool ok = s switch
                        {
                            Sanction.Solitary => oc.pendingPunishmentHours > 0 && oc.forcedActivity == null,
                            Sanction.CellConfinement => oc.forcedActivity == RPDefOf.RP_Lockup && oc.forcedUntilTick > tNow && oc.pendingPunishmentHours == 0,
                            Sanction.LoseYard => Sanctions.Banned(oc, Sanction.LoseYard) && (freeHour < 0 || g.ActivityAt(oc, freeHour) == RPDefOf.RP_Lockup && g.ConfinedAlone(oc, freeHour)) && oc.pendingPunishmentHours == 0,
                            Sanction.LoseVisits => oc.visitBanUntilTick > tNow && oc.pendingPunishmentHours == 0,
                            Sanction.LoseCommissary => Sanctions.Banned(oc, Sanction.LoseCommissary) && oc.pendingPunishmentHours == 0,
                            _ => oc.forcedActivity == null && oc.pendingPunishmentHours == 0 && oc.yardBanUntilTick < 0 && oc.shopBanUntilTick < 0 && oc.visitBanUntilTick < 0 && oc.records.Last().punished,
                        };
                        results.Add($"{s}:{ok}");
                        if (!ok) problems0.Add($"the {s} sanction did not take effect (pending {oc.pendingPunishmentHours}, forced {oc.forcedActivity?.defName}, yard {oc.yardBanUntilTick}, shop {oc.shopBanUntilTick})");
                    }
                    // A free-time hour with no ban: not confined, the door is the regime's business.
                    Clear();
                    if (freeHour >= 0 && g.ConfinedAlone(oc, freeHour)) problems0.Add("an inmate with no sanction counts as confined alone");
                    // Both privileges lost: both bans hold.
                    pol.SetKind(offence, Sanction.LoseYard);
                    MisconductUtility.Record(offender, oc, PrisonMap.Of(map), offence, evidence: true, chronicle: false);
                    pol.SetKind(offence, Sanction.LoseCommissary);
                    MisconductUtility.Record(offender, oc, PrisonMap.Of(map), offence, evidence: true, chronicle: false);
                    bool bothBans = Sanctions.Banned(oc, Sanction.LoseYard) && Sanctions.Banned(oc, Sanction.LoseCommissary);
                    if (!bothBans) problems0.Add("a second privilege ban lifted the first");
                    // An offence while confined: the confinement runs longer and the offence is marked as punished.
                    Clear();
                    pol.SetKind(offence, Sanction.CellConfinement);
                    (oc.forcedActivity, oc.forcedUntilTick) = (RPDefOf.RP_Lockup, tNow + 1);
                    MisconductUtility.Record(offender, oc, PrisonMap.Of(map), offence, evidence: true, chronicle: false);
                    bool extended = oc.forcedUntilTick > tNow + 1 && oc.records.Last().punished;
                    if (!extended) problems0.Add($"an offence while confined did not extend the confinement (until {oc.forcedUntilTick - tNow} ticks from now)");
                    // A loud offence with a light sanction, then restrained: no hours in the hole.
                    Clear();
                    pol.SetKind(loudOffence, Sanction.LoseYard);
                    MisconductUtility.Record(offender, oc, PrisonMap.Of(map), loudOffence, evidence: true, chronicle: false);
                    int restraintHours = oc.PolicyHoursForLatest();
                    // A fight is an assault in the statistics; the non-violent offences before it were not.
                    if (loudOffence.assault != (g.stats.assaults == assaultsWas + 1)) problems0.Add($"assaults counted {g.stats.assaults - assaultsWas} for {loudOffence.defName} (assault {loudOffence.assault})");
                    if (restraintHours != 0 || oc.pendingPunishmentHours != 0) problems0.Add($"a restraint after a light-sanction offence still sends them to the hole ({restraintHours} h, pending {oc.pendingPunishmentHours})");
                    // ... but attacking staff on top of it is an act of its own; and a solitary offence keeps its hours.
                    if (oc.PolicyHoursForLatest(actingOut: true) != CompInmate.ResistingHours) problems0.Add("acting out beyond a light-sanction offence was not punished as resisting staff");
                    pol.SetKind(loudOffence, Sanction.Solitary);
                    Clear();
                    MisconductUtility.Record(offender, oc, PrisonMap.Of(map), loudOffence, evidence: true, chronicle: false);
                    int solitaryRestraint = oc.PolicyHoursForLatest();
                    // An offence nobody saw, the latest on the record: not what they were restrained for (the seen one before it is).
                    oc.records.Add(new MisconductRecord { def = loudOffence, tick = Find.TickManager.TicksGame, seen = false });
                    MisconductRecord seenOne = oc.records[oc.records.Count - 2];
                    (int seenTickWas, seenOne.tick) = (seenOne.tick, Find.TickManager.TicksGame - 2 * GenDate.TicksPerHour);
                    int unseenRestraint = oc.PolicyHoursForLatest();
                    seenOne.tick = seenTickWas;
                    oc.records.RemoveAt(oc.records.Count - 1);
                    if (unseenRestraint != CompInmate.ResistingHours) problems0.Add($"an unseen offence set a restraint's hours ({unseenRestraint} h, want {CompInmate.ResistingHours} for resisting)");
                    if (solitaryRestraint != pol.HoursFor(loudOffence) || solitaryRestraint <= 0) problems0.Add($"a restraint after a solitary offence gave {solitaryRestraint} h (policy {pol.HoursFor(loudOffence)})");
                    // Confinement when the hole is already due, or while in the hole: the hole covers it / it lasts longer.
                    Clear();
                    pol.SetKind(offence, Sanction.CellConfinement);
                    oc.pendingPunishmentHours = 1;
                    MisconductUtility.Record(offender, oc, PrisonMap.Of(map), offence, evidence: true, chronicle: false);
                    bool dueCovered = oc.forcedActivity == null && oc.pendingPunishmentHours == pol.HoursFor(offence);
                    Clear();
                    (oc.forcedActivity, oc.forcedUntilTick) = (RPSecurityDefOf.RP_Solitary, tNow + 1);
                    MisconductUtility.Record(offender, oc, PrisonMap.Of(map), offence, evidence: true, chronicle: false);
                    bool holeExtended = oc.forcedActivity == RPSecurityDefOf.RP_Solitary && oc.forcedUntilTick > tNow + 1;
                    if (!dueCovered || !holeExtended) problems0.Add($"cell confinement with the hole due ({dueCovered}) or under way ({holeExtended}) was lost");
                    // No visits: a visit under way is called off.
                    Clear();
                    pol.SetKind(offence, Sanction.LoseVisits);
                    oc.visitor = offender; // stand-in: any pawn marks a visit as arranged
                    MisconductUtility.Record(offender, oc, PrisonMap.Of(map), offence, evidence: true, chronicle: false);
                    if (oc.visitor != null) problems0.Add("a visit ban left the visit under way");
                    oc.visitor = null;
                    // A restraint with no offence of its own whose pat-down turns up contraband (light-sanctioned): the hours for
                    // resisting staff still apply (the pat-down's record must not stand in for the reason they were restrained).
                    if (StaffLives.OnSite(map).FirstOrDefault(GuardUtility.IsGuard) is { } restrainer)
                    {
                        Clear();
                        oc.records = new List<MisconductRecord>();
                        pol.SetKind(RPContrabandDefOf.RP_ContrabandPossession, Sanction.LoseCommissary);
                        ThingDef shiv = DefDatabase<ThingDef>.AllDefsListForReading.First(d => ContrabandUtility.Ext(d) != null);
                        offender.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(shiv));
                        CompStaff rsc = restrainer.GetComp<CompStaff>();
                        (int waryId, int waryUntil, string waryName) = (rsc.waryOfId, rsc.waryUntilTick, rsc.waryOfName);
                        var memoriesWas = oc.staffMemories.Select(m => (m.staffId, m.name, m.score, m.lastTick)).ToList();
                        // (A restraint is a bad memory of the guard, and one at -60 starts a grudge: the goal goes back too.)
                        string goalsBeforeCuffs = GoalSnapshot(map);
                        Action goalsBackAfterCuffs = RememberGoals(map.mapPawns.PrisonersOfColonySpawned);
                        // (A guard the sweep left corrupt looks away from a client's pockets: the pat-down under test is an honest guard's.)
                        bool restrainerCorrupt = rsc.corrupt;
                        rsc.corrupt = false;
                        PunishmentUtility.ResolveSubdue(restrainer, offender, alwaysSucceeds: true);
                        rsc.corrupt = restrainerCorrupt;
                        int patDownHours = oc.pendingPunishmentHours;
                        bool patDownFound = oc.records.Any(r => r.def == RPContrabandDefOf.RP_ContrabandPossession);
                        if (!patDownFound || patDownHours != CompInmate.ResistingHours) problems0.Add($"a restraint whose pat-down found contraband gave {patDownHours} h (found {patDownFound})");
                        if (offender.health.hediffSet.GetFirstHediffOfDef(RPSecurityDefOf.RP_Restrained) is { } cuffs) offender.health.RemoveHediff(cuffs);
                        foreach (Thing x in offender.inventory.innerContainer.Where(x => x.def == shiv).ToList()) x.Destroy();
                        (rsc.waryOfId, rsc.waryUntilTick, rsc.waryOfName) = (waryId, waryUntil, waryName);
                        oc.staffMemories = memoriesWas.Select(m => new StaffMemory { staffId = m.staffId, name = m.name, score = m.score, lastTick = m.lastTick }).ToList();
                        goalsBackAfterCuffs();
                        if (GoalSnapshot(map) != goalsBeforeCuffs) problems0.Add("the sanctions restraint test left the inmates' goals changed: " + GoalDiff(goalsBeforeCuffs, GoalSnapshot(map)));
                        RPLog.Message($"[RimPrison:test] sanctions restraint: pat-down found {patDownFound}, {patDownHours} h");
                    }
                    // A yard ban on one cellmate in free time: the shared cell door is not locked on the other. (The test prison
                    // has single cells: a second inmate is added to the bed's owners for the check, and taken off again.)
                    bool FreeAt(Pawn x) => InmateUtility.IsActiveInmate(x) && x.Inmate() is { forcedActivity: null } xc && !g.LockedDown(xc) && xc.yardBanUntilTick < 0
                                           && !g.ActivityAt(xc, freeHour).lockup && !g.Hiding(xc, g.ActivityAt(xc, freeHour));
                    CompPrisonDoor sharedDoor = freeHour < 0 ? null : PrisonMap.Of(map).Doors.FirstOrDefault(d => d.cellRoom != null && d.mode == PrisonDoorMode.Auto
                        && d.cellRoom.ContainedBeds.Count() == 1 && d.cellRoom.ContainedBeds.First().OwnersForReading.Count == 1 && FreeAt(d.cellRoom.ContainedBeds.First().OwnersForReading[0]));
                    Pawn mate = sharedDoor == null ? null : map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => FreeAt(x) && InmateUtility.CellOf(x) != sharedDoor.cellRoom);
                    if (sharedDoor != null && mate != null)
                    {
                        List<Pawn> owners = sharedDoor.cellRoom.ContainedBeds.First().OwnersForReading;
                        Pawn banned = owners[0];
                        CompInmate bc = banned.Inmate();
                        (int bannedWas, int lockupSinceWas) = (bc.yardBanUntilTick, sharedDoor.lockupSince);
                        owners.Add(mate);
                        bc.yardBanUntilTick = tNow + GenDate.TicksPerDay;
                        sharedDoor.lockupSince = -1;
                        bool doorOpen = PrisonMap.Of(map).CellDoorOpen(sharedDoor, g, tNow, freeHour) && sharedDoor.lockupSince < 0;
                        // Alone in the cell, the banned inmate is locked in.
                        owners.Remove(mate);
                        sharedDoor.lockupSince = -1;
                        PrisonMap.Of(map).CellDoorOpen(sharedDoor, g, tNow, freeHour);
                        bool aloneLocked = sharedDoor.lockupSince >= 0;
                        if (!doorOpen || !aloneLocked) problems0.Add($"a yard ban on {banned.LabelShort}: shared door open {doorOpen} (should be), alone locked {aloneLocked} (should be)");
                        (bc.yardBanUntilTick, sharedDoor.lockupSince) = (bannedWas, lockupSinceWas);
                        RPLog.Message($"[RimPrison:test] sanctions door: {banned.LabelShort} with {mate.LabelShort}, open {doorOpen}, alone locked {aloneLocked}");
                    }
                    else RPLog.Message($"[RimPrison:test] sanctions door: skipped (free hour {freeHour}, door {sharedDoor != null}, mate {mate != null})");
                    pol.kinds.Clear();
                    foreach (var kv in kindsWas) pol.kinds[kv.Key] = kv.Value;
                    Clear();
                    (oc.visitBanUntilTick, oc.suppressed, oc.tension) = (banWas, suppressedWas, tensionWas);
                    oc.records = recordsCopy;
                    (g.stats.misconducts, g.stats.punishments, g.stats.lightSanctions, g.today.misconducts, g.today.violent, g.stats.assaults) = (misconductsWas, punishmentsWas, lightWas, todayMisWas, todayViolentWas, assaultsWas);
                    offender.needs?.mood?.thoughts.memories.RemoveMemoriesOfDef(RPSecurityDefOf.RP_Sanctioned);
                    int keep = lastEntry == null ? 0 : g.story.entries.IndexOf(lastEntry) + 1;
                    if (keep > 0 || lastEntry == null) g.story.entries.RemoveRange(keep, g.story.entries.Count - keep);
                    RPLog.Message($"[RimPrison:test] sanctions ({offence.defName}/{loudOffence.defName}): {string.Join(" ", results)} both={bothBans} extended={extended} restraint={restraintHours}/{solitaryRestraint}");
                }
                else problems0.Add("sanctions test: no inmate with a bed and nothing pending");
            }

            private void EvHomeBedNeverOfferedFree()
            {
                // A bed someone will come back to (taken to solitary: the cell bed is their home bed) is never offered as free.
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { homeBed: null } && x.ownership.OwnedBed is { } ob && !PunishmentUtility.IsSolitaryBed(ob)
                        && ob.SleepingSlotsCount == 1) is { } inSolitary)
                {
                    Building_Bed home = inSolitary.ownership.OwnedBed;
                    inSolitary.ownership.UnclaimBed();
                    inSolitary.Inmate().homeBed = home;
                    bool offered = IntakeSystem.FreePrisonerBeds(map, inSolitary.Inmate().security).Contains(home);
                    inSolitary.Inmate().homeBed = null;
                    inSolitary.ownership.ClaimBedIfNonMedical(home);
                    if (offered) problems0.Add("the cell bed of an inmate in solitary was offered as free (they would come back to no bed)");
                }
            }

            private void EvDemands()
            {
                // Inmates' demands, put by a spokesperson: a kept promise (their standing grows), a broken one and a
                // refusal (they take it personally).
                foreach (float level in new[] { 1f, 0f })
                {
                    g.demand.cooldownUntilTick = -1;
                    DemandSystem.Raise(map, g, RPDefOf.RP_Hygiene);
                    var letter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Last(l => l.Is("RP_Decision_InmateDemand"));
                    Pawn spokes = letter.pawns.FirstOrDefault();
                    // (Without case: a name that opens the letter is capitalised, and a name can be lower-case.)
                    if (spokes == null || letter.Text.ToString().IndexOf(spokes.LabelShort, System.StringComparison.OrdinalIgnoreCase) < 0) problems0.Add($"a demand letter with no spokesperson named ({spokes?.LabelShort ?? "nobody"})");
                    letter.answered = true;
                    PrisonDecisions.Accept(letter);
                    Find.LetterStack.RemoveLetter(letter);
                    if (spokes != null && g.demand.spokesId != spokes.thingIDNumber) problems0.Add("a promised demand forgot who asked");
                    foreach (Pawn x in map.mapPawns.PrisonersOfColonySpawned)
                        if (x.needs.TryGetNeed(RPDefOf.RP_Hygiene) is { } h) h.CurLevel = level;
                    g.demand.deadlineTick = Find.TickManager.TicksGame;
                    DemandSystem.Evening(map, g);
                    ThoughtDef expected = level > 0.5f ? RPDemandDefOf.RP_SpokesHeard : RPDemandDefOf.RP_SpokesIgnored;
                    if (spokes != null && !spokes.needs.mood.thoughts.memories.Memories.Any(m => m.def == expected))
                        problems0.Add($"the spokesperson felt nothing about a {(level > 0.5f ? "kept" : "broken")} promise");
                    spokes?.needs.mood.thoughts.memories.RemoveMemoriesOfDef(expected);
                }
                if (g.stats.demandsMet != 1 || g.stats.demandsBroken != 1) problems0.Add($"demands: met {g.stats.demandsMet}, broken {g.stats.demandsBroken} (expected 1/1)");
                {
                    g.demand.cooldownUntilTick = -1;
                    DemandSystem.Raise(map, g, RPDefOf.RP_Hygiene);
                    var refused = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Last(l => l.Is("RP_Decision_InmateDemand"));
                    Pawn spokes = refused.pawns.FirstOrDefault();
                    float before = spokes?.Inmate().tension ?? 0f;
                    refused.answered = true;
                    PrisonDecisions.Decline(refused);
                    Find.LetterStack.RemoveLetter(refused);
                    if (spokes != null && (spokes.Inmate().tension < UnityEngine.Mathf.Min(100f, before + 8f + DemandSystem.SpokesRefusedTension) - 0.01f
                        || !spokes.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPDemandDefOf.RP_SpokesIgnored)))
                        problems0.Add($"a refused spokesperson: tension {before:F0} -> {spokes.Inmate().tension:F0}");
                    RPLog.Message($"[RimPrison:test] demands: spokesperson {spokes?.LabelShort}, refused tension {before:F0} -> {spokes?.Inmate().tension:F0}");
                    if (spokes == null) problems0.Add("a demand with no spokesperson");
                    // Someone else speaks next time (the same inmate does not take every answer).
                    Pawn next = DemandSystem.Spokesperson(map);
                    if (next != null && next == spokes && map.mapPawns.PrisonersOfColonySpawned.Count(x => InmateUtility.IsActiveInmate(x) && !x.Downed && !x.InMentalState && x.Inmate().forcedActivity == null) > 3)
                        problems0.Add($"{spokes.LabelShort} was picked to speak again straight away");
                    // A promise whose spokesperson is gone still resolves, under their name.
                    g.demand.cooldownUntilTick = -1;
                    DemandSystem.Accept(g, RPDefOf.RP_Hygiene, next);
                    g.demand.spokesId = int.MaxValue;
                    g.demand.deadlineTick = Find.TickManager.TicksGame;
                    int entries = g.story.entries.Count;
                    DemandSystem.Evening(map, g);
                    if (g.demand.Active || g.story.entries.Count <= entries || next != null && !g.story.entries.Last().text.Contains(next.LabelShort))
                        problems0.Add("a promise whose spokesperson was gone did not resolve under their name");
                }
            }

            private void EvReleasedAndDownedAreNotRescued()
            {
                // Released and downed: guards must not "rescue" them back into custody.
                guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(GuardUtility.IsGuard);
                // (One on their feet: releasing someone already down makes vanilla start a walk to the exit for a pawn that cannot path.)
                Pawn leaving = map.mapPawns.PrisonersOfColonySpawned.LastOrDefault(x => !x.Downed);
                if (guard != null && leaving != null)
                {
                    ReleaseSystem.Release(g, leaving);
                    HealthUtility.DamageUntilDowned(leaving, allowBleedingWounds: false);
                    bool rescue = new WorkGiver_RescueInmate { def = DefDatabase<WorkGiverDef>.GetNamed("RP_RescueInmate") }.HasJobOnThing(guard, leaving);
                    if (rescue) problems0.Add("a released, downed inmate would be carried back into custody");
                }
            }

            private void EvRiotGearKeepsTheirHat()
            {
                // Riot gear goes into free slots only: a staff member's own hat stays on.
                if (guard != null)
                {
                    ThingDef hat = DefDatabase<ThingDef>.GetNamed("Apparel_CowboyHat");
                    var own = (Apparel)ThingMaker.MakeThing(hat, GenStuff.DefaultStuffFor(hat));
                    guard.apparel.Wear(own, dropReplacedApparel: false);
                    StaffUtility.IssueGear(guard);
                    if (!guard.apparel.WornApparel.Contains(own)) problems0.Add("riot gear replaced a staff member's own hat");
                }
            }

            private void EvEscapeAndRecapture()
            {
                // An escape and a recapture: the escapee leaves the map, the police bring them back with a longer sentence.
                Pawn runner = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && !x.Downed && x.Inmate() is { IsLifer: false });
                if (runner != null)
                {
                    int sentence = runner.Inmate().sentenceDays;
                    runner.ExitMap(false, Rot4.North);
                    if (!g.fugitives.Any(f => f.pawn == runner))
                    {
                        g.fugitives.Add(new Fugitive { pawn = runner });
                        Find.WorldPawns.ForcefullyKeptPawns.Add(runner);
                    }
                    foreach (Fugitive f in g.fugitives) f.returnTick = Find.TickManager.TicksGame;
                    FugitiveSystem.Hourly(map, g);
                    // (No bed for their level and sex at the hour - the prison full: the state holds them a day and tries again, see FugitiveSystem.Hourly. That is the live prison's premise, told and not failed: the
                    // escapee left on the run at the hour was seen in 2 runs of 541, one of them on a branch without the release preparation.)
                    Fugitive held = g.fugitives.FirstOrDefault(f => f.pawn == runner);
                    int bedsForRunner = IntakeSystem.FreeBedCount(map, runner.Inmate()?.security, gender: runner.gender);
                    if (!runner.Spawned && held is { attempts: > 0 } && bedsForRunner == 0)
                    {
                        RPLog.Message($"[RimPrison:test] recapture: no free bed for {runner.LabelShort}'s level and sex at the hour, the state holds them a day ({held.attempts} attempt(s)); the return not tried");
                        return;
                    }
                    // (The police have them: the state's marshals bring them on the shuttle, so they are not on the map yet. The intake's own way in, without a shuttle, is what a landing does.)
                    if (runner.Spawned || !g.shuttle.IsMarshal(runner))
                        problems0.Add($"a recaptured escapee was put on the map by the hour of the recapture (spawned {runner.Spawned}, waiting for the shuttle {g.shuttle.IsMarshal(runner)}; still a fugitive {held != null}, attempts {held?.attempts}, free beds for them {bedsForRunner}, destroyed {runner.Destroyed}, held by {runner.ParentHolder?.ToString() ?? "nothing"})");
                    LandMarshalsNow(g, map);
                    RPLog.Message($"[RimPrison:test] recapture: spawned={runner.Spawned} prisoner={runner.IsPrisonerOfColony} sentence {sentence} -> {runner.Inmate().sentenceDays}");
                    if (!runner.Spawned || !runner.IsPrisonerOfColony || g.stats.recaptured != 1 || runner.Inmate().sentenceDays <= sentence)
                        problems0.Add("recaptured escapee did not come back as an inmate with a longer sentence");
                }
            }
        }
    }
}
