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
        // Danger memories, grants, a death on the staff, life after release, coverage, staff and inmate memories, the file's status lines, gang leaders, amnesty, remission, detox, family day and an officer too close. (the shared state is in EventsRun.cs)
        private sealed partial class EventsRun
        {
            private void EvDangerMemories()
            {
                // Danger memories: a guard put down by an inmate is shaken and wary of them (hesitates to restrain them,
                // on the record); a second attacker replaces the first; restraining them faces it down; it expires, and
                // it ends when the inmate leaves.
                if (StaffLives.OnSite(map).FirstOrDefault(GuardUtility.IsGuard) is { } shakenGuard
                    && map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null).Take(2).ToList() is { Count: 2 } pair)
                {
                    Pawn attacker = pair[0], other = pair[1];
                    CompStaff hsc = shakenGuard.GetComp<CompStaff>();
                    var memories = shakenGuard.needs.mood.thoughts.memories;
                    // Start clean: an assault earlier in the run may have left wariness and memories.
                    StaffWariness.Clear(hsc);
                    memories.RemoveMemoriesOfDef(RPWaryDefOf.RP_Shaken);
                    memories.RemoveMemoriesOfDef(RPWaryDefOf.RP_FacedDown);
                    float subdueBefore = PunishmentUtility.SubdueChance(shakenGuard, attacker), otherBefore = PunishmentUtility.SubdueChance(shakenGuard, other);
                    StaffWariness.Notify_HurtBy(shakenGuard, hsc, attacker);
                    float wary = PunishmentUtility.SubdueChance(shakenGuard, attacker), otherAfter = PunishmentUtility.SubdueChance(shakenGuard, other);
                    bool shaken = memories.Memories.Any(m => m.def == RPWaryDefOf.RP_Shaken && m.otherPawn == attacker);
                    // The description names the attacker, not the guard ({0} in a description is the thinker).
                    string shakenText = memories.Memories.FirstOrDefault(m => m.def == RPWaryDefOf.RP_Shaken)?.Description ?? "";
                    // (A short name is a part of some word in any sentence: the guard's name is looked for as a whole word, and not at all when the two share a first name.)
                    bool namesGuard = attacker.LabelShort != shakenGuard.LabelShort && System.Text.RegularExpressions.Regex.IsMatch(shakenText, @"\b" + System.Text.RegularExpressions.Regex.Escape(shakenGuard.LabelShort) + @"\b");
                    if (!shakenText.Contains(attacker.LabelShort) || namesGuard)
                        problems0.Add($"the shaken thought's description names the wrong pawn: {shakenText}");
                    if (!StaffWariness.WaryOf(hsc, attacker) || !shaken || StaffWariness.StatusLine(hsc) == null
                        || !UnityEngine.Mathf.Approximately(wary, subdueBefore * StaffWariness.SubdueFactor) || !UnityEngine.Mathf.Approximately(otherAfter, otherBefore))
                        problems0.Add($"a guard hurt by an inmate: wary {StaffWariness.WaryOf(hsc, attacker)}, shaken {shaken}, subdue {subdueBefore:P0} -> {wary:P0}, other {otherBefore:P0} -> {otherAfter:P0}");
                    // A second attacker takes the slot, and the first fear goes with it.
                    StaffWariness.Notify_HurtBy(shakenGuard, hsc, other);
                    if (StaffWariness.WaryOf(hsc, attacker) || !StaffWariness.WaryOf(hsc, other) || memories.Memories.Any(m => m.def == RPWaryDefOf.RP_Shaken && m.otherPawn == attacker))
                        problems0.Add("a second attacker did not replace the first (wariness and the shaken memory)");
                    // Facing down the one they fear.
                    int entries = g.story.entries.Count;
                    StaffWariness.Notify_Subdued(shakenGuard, other);
                    if (StaffWariness.WaryOf(hsc, other) || g.story.entries.Count <= entries || memories.Memories.Any(m => m.def == RPWaryDefOf.RP_Shaken && m.otherPawn == other)
                        || !memories.Memories.Any(m => m.def == RPWaryDefOf.RP_FacedDown && m.otherPawn == other))
                        problems0.Add("restraining the inmate who hurt them did not face it down");
                    // It ends when the inmate leaves, and it expires.
                    StaffWariness.Notify_HurtBy(shakenGuard, hsc, attacker);
                    StaffWariness.Notify_InmateLeaving(attacker);
                    if (StaffWariness.WaryOf(hsc, attacker)) problems0.Add("still wary of an inmate who left the prison");
                    StaffWariness.Notify_HurtBy(shakenGuard, hsc, attacker);
                    hsc.waryUntilTick = Find.TickManager.TicksGame;
                    if (StaffWariness.WaryOf(hsc, attacker) || StaffWariness.StatusLine(hsc) != null) problems0.Add("wariness did not expire");
                    StaffWariness.Clear(hsc);
                    memories.RemoveMemoriesOfDef(RPWaryDefOf.RP_Shaken);
                    RPLog.Message($"[RimPrison:test] wary: {shakenGuard.LabelShort} of {attacker.LabelShort}, subdue {subdueBefore:P0} -> {wary:P0}");
                }
            }

            private void EvGrants()
            {
                // Grants: abandoning gives the advance back (accept, abandon, accept paid it every time); a condition that
                // can no longer be met (an escape under "no escapes") withdraws the grant and claws the advance back.
                {
                    GrantDef secure = DefDatabase<GrantDef>.GetNamed("RP_Grant_Secure");
                    GrantDef paid = DefDatabase<GrantDef>.AllDefsListForReading.First(d => d.advance > 0);
                    var activeWas = new List<GrantState>(g.grants.active);
                    g.grants.active.RemoveAll(s => s.def == secure || s.def == paid);
                    int fundsWas = g.ledger.funds, escapedWas = g.stats.escaped;
                    g.grants.Accept(paid, map);
                    g.grants.Drop(g.grants.active.First(s => s.def == paid), failed: false);
                    g.grants.Accept(paid, map);
                    g.grants.Drop(g.grants.active.First(s => s.def == paid), failed: false);
                    if (g.ledger.funds != fundsWas) problems0.Add($"accept and abandon a grant twice: funds {fundsWas} -> {g.ledger.funds} (the {paid.advance} advance was kept)");
                    g.grants.Accept(secure, map);
                    g.stats.escaped++;
                    g.grants.Check(map);
                    if (g.grants.active.Any(s => s.def == secure) || g.ledger.funds != fundsWas)
                        problems0.Add($"a grant whose no-escapes condition failed was not withdrawn (active {g.grants.active.Any(s => s.def == secure)}, funds {fundsWas} -> {g.ledger.funds})");
                    g.stats.escaped = escapedWas;
                    g.grants.active.Clear();
                    g.grants.active.AddRange(activeWas);
                    foreach (var gl in Find.LetterStack.LettersListForReading.Where(l => l.Label.ToString() == "RP_GrantFailedLabel".Translate().ToString()).ToList())
                        Find.LetterStack.RemoveLetter(gl);
                    RPLog.Message($"[RimPrison:test] grants: {paid.defName} advance {paid.advance} returned on abandon; {secure.defName} withdrawn on an escape");
                }
            }

            private void EvDeathOnTheStaff()
            {
                // A death on the staff (the aftermath, called directly: the guard is not really killed mid-run): a milestone,
                // colleagues mourn, the inmate who did it is charged with murder, the family letter; paying lifts the staff.
                if (StaffLives.OnSite(map).Where(GuardUtility.IsGuard).Take(2).ToList() is { Count: 2 } dpair
                    && map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { } xc && !xc.IsLifer) is { } staffKiller)
                {
                    (Pawn dead, Pawn colleague) = (dpair[0], dpair[1]);
                    CompInmate kc2 = staffKiller.Inmate();
                    (int sentenceWas, int recordsWas, SecurityLevelDef secWas, int fundsWas) = (kc2.sentenceDays, kc2.records.Count, kc2.security, g.ledger.funds);
                    int deaths = g.stats.staffDeaths, murders = g.stats.murders, entries = g.story.entries.Count, trialsOpen2 = g.story.trials.Count;
                    StaffDeaths.Notify_Died(dead, dead.GetComp<CompStaff>(), new DamageInfo(DamageDefOf.Stab, 20f, instigator: staffKiller), null);
                    var dl3 = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_StaffDeath"));
                    bool mourning = colleague.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPStaffDeathDefOf.RP_ColleagueKilled && m.otherPawn == dead);
                    if (g.stats.staffDeaths != deaths + 1 || g.stats.murders != murders + 1 || !mourning || dl3 == null || g.story.entries.Count <= entries)
                        problems0.Add($"a death on the staff: counted {g.stats.staffDeaths - deaths}, murder charge {g.stats.murders - murders}, mourning {mourning}, letter {dl3 != null}");
                    else
                    {
                        dl3.answered = true;
                        PrisonDecisions.Choose(dl3, 0);
                        Find.LetterStack.RemoveLetter(dl3);
                        if (g.ledger.funds != fundsWas - StaffDeaths.Compensation || !colleague.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPStaffDeathDefOf.RP_FamilyLookedAfter))
                            problems0.Add($"compensating a dead officer's family: cost {fundsWas - g.ledger.funds}, staff lifted {colleague.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPStaffDeathDefOf.RP_FamilyLookedAfter)}");
                    }
                    RPLog.Message($"[RimPrison:test] staff death: {dead.LabelShort} by {staffKiller.LabelShort}, sentence {sentenceWas} -> {kc2.sentenceDays}");
                    // Undo the test's charge (and the case it opened).
                    if (g.story.trials.Count != trialsOpen2 + 1 || !g.story.trials[trialsOpen2].staffVictim) problems0.Add("the killer of an officer was charged and no staff-victim trial was set");
                    g.story.trials.RemoveRange(trialsOpen2, g.story.trials.Count - trialsOpen2);
                    kc2.sentenceDays = sentenceWas;
                    while (kc2.records.Count > recordsWas) kc2.records.RemoveAt(kc2.records.Count - 1);
                    if (kc2.security != secWas) InmateUtility.ChangeSecurity(staffKiller, kc2, secWas);
                    g.ledger.funds = fundsWas;
                    foreach (var ml3 in Find.LetterStack.LettersListForReading.Where(l => string.Equals(l.Label.ToString(), "RP_MurderLabel".Translate(staffKiller.LabelShort).ToString(), StringComparison.OrdinalIgnoreCase)).ToList())
                        Find.LetterStack.RemoveLetter(ml3);
                }
            }

            private void EvLifeAfterRelease()
            {
                // Life after release: the register keeps a released inmate with their friend inside and the officer they
                // trusted; their news, when it comes, is a thank-you to that officer, and the friend is glad of it.
                if (map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null).Take(2).ToList() is { Count: 2 } fpair
                    && StaffLives.OnSite(map).FirstOrDefault(GuardUtility.IsGuard) is { } kindGuard)
                {
                    (Pawn released, Pawn leftBehind) = (fpair[0], fpair[1]);
                    CompInmate lc2 = released.Inmate();
                    var memWas = new List<StaffMemory>(lc2.staffMemories);
                    lc2.staffMemories.Clear(); // (earlier in the run they may have grudges against this very guard)
                    bool jobWas = lc2.jobWaiting, friendsWere = PrisonRelations.Friends(leftBehind, released);
                    lc2.jobWaiting = false;
                    if (!friendsWere) leftBehind.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, released);
                    for (int i = 0; i < 3; i++) StaffMemories.Note(lc2, kindGuard, StaffMemories.Rescued);
                    FormerInmates.Notify_Released(g, released, lc2, paroled: false, reoffends: false);
                    FormerInmate entry = g.story.formerInmates.Last();
                    // (A friend inside keeps the pawn as a world pawn: this one is still on the map, so the keep is let go again.)
                    ExInmates.Unkeep(entry);
                    if (entry.name != released.LabelShort || !entry.friendIds.Contains(leftBehind.thingIDNumber) || entry.trustedId != kindGuard.thingIDNumber || entry.newsTick < 0)
                        problems0.Add($"the former-inmate register: name {entry.name}, friend {entry.friendIds.Contains(leftBehind.thingIDNumber)}, trusted {entry.trustedName}, news due {entry.newsTick}");
                    entry.newsTick = Find.TickManager.TicksGame;
                    FormerInmates.Hourly(g, map);
                    if (entry.news != "RP_FormerNews_Thanks" || !leftBehind.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPFormerDefOf.RP_FriendDoingWell)
                        || !kindGuard.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPFormerDefOf.RP_ThankedByFormer))
                        problems0.Add($"news from a former inmate: {entry.news}, friend glad {leftBehind.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPFormerDefOf.RP_FriendDoingWell)}");
                    // Someone who will reoffend sends no news (the rearrest is their news).
                    FormerInmates.Notify_Released(g, released, lc2, paroled: false, reoffends: true);
                    if (g.story.formerInmates.Last().newsTick >= 0) problems0.Add("a reoffender was due to send good news");
                    g.story.formerInmates.RemoveRange(g.story.formerInmates.Count - 2, 2);
                    // A full register drops the oldest entry with no news to come, never a letter still on its way.
                    var registerWas = new List<FormerInmate>(g.story.formerInmates);
                    g.story.formerInmates.Clear();
                    g.story.formerInmates.Add(new FormerInmate { name = "Pending", newsTick = Find.TickManager.TicksGame + 100000 });
                    for (int i = 1; i < FormerInmates.Max; i++) g.story.formerInmates.Add(new FormerInmate { name = "Done" + i });
                    FormerInmates.Notify_Released(g, released, lc2, paroled: false, reoffends: true);
                    if (g.story.formerInmates.Count != FormerInmates.Max || !g.story.formerInmates.Any(x => x.name == "Pending"))
                        problems0.Add("a full former-inmate register dropped a letter still on its way");
                    g.story.formerInmates.Clear();
                    g.story.formerInmates.AddRange(registerWas);
                    RPLog.Message($"[RimPrison:test] former inmates: {entry.name}: {entry.news.Translate(entry.name, entry.trustedName)}");
                    lc2.staffMemories.Clear();
                    lc2.staffMemories.AddRange(memWas);
                    lc2.jobWaiting = jobWas;
                    if (!friendsWere) leftBehind.relations.TryRemoveDirectRelation(RPRelationDefOf.RP_Friend, released);
                }
            }

            private void EvCoverageOverlay()
            {
                // Coverage overlay: what it shows on an inmate's cell is what the tension model counts as watching them.
                {
                    PrisonMap cpm = PrisonMap.Of(map);
                    cpm.Coverage.Rebuild();
                    var mismatches = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null)
                        .Where(x => !UnityEngine.Mathf.Approximately(cpm.Coverage.At(x.Position), TensionUtility.Watchers(x, cpm)))
                        .Select(x => $"{x.LabelShort} {cpm.Coverage.At(x.Position):F1} vs {TensionUtility.Watchers(x, cpm):F1}").ToList();
                    if (mismatches.Count > 0) problems0.Add("the coverage overlay disagrees with the watchers count: " + string.Join(", ", mismatches));
                    int watched = map.mapPawns.PrisonersOfColonySpawned.Count(x => cpm.Coverage.At(x.Position) >= 1f);
                    RPLog.Message($"[RimPrison:test] coverage: {watched} of {map.mapPawns.PrisonersOfColonySpawnedCount} inmates watched, overlay agrees with the watchers count");
                }
            }

            private void EvInmatesRememberStaff()
            {
                // Inmates remember staff: restrained three times by one guard, an inmate hates them (harder to restrain,
                // winds them up in the room, the one they go for); a rescue starts trust; feelings fade; a guard who shoots
                // an inmate dead is hated by the victim's friends.
                if (StaffLives.OnSite(map).Where(GuardUtility.IsGuard).Take(2).ToList() is { Count: 2 } gpair
                    && map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null).Take(2).ToList() is { Count: 2 } ipair)
                {
                    (Pawn hatedGuard, Pawn otherGuard, Pawn inmate, Pawn friend) = (gpair[0], gpair[1], ipair[0], ipair[1]);
                    CompInmate ic = inmate.Inmate(), fc3 = friend.Inmate();
                    var memWas = new List<StaffMemory>(ic.staffMemories);
                    var friendMemWas = new List<StaffMemory>(fc3.staffMemories);
                    // (The friend's feeling for the shooter is a grudge, which sets their goal aside: every inmate's goals go back too.)
                    string goalsBeforeShot = GoalSnapshot(map);
                    Action goalsBackAfterShot = RememberGoals(map.mapPawns.PrisonersOfColonySpawned);
                    ic.staffMemories.Clear();
                    fc3.staffMemories.Clear();
                    float subdueWas = PunishmentUtility.SubdueChance(hatedGuard, inmate);
                    for (int i = 0; i < 3; i++) StaffMemories.Note(ic, hatedGuard, StaffMemories.Restrained);
                    float subdueNow = PunishmentUtility.SubdueChance(hatedGuard, inmate);
                    if (StaffMemories.MostHated(ic)?.staffId != hatedGuard.thingIDNumber || !UnityEngine.Mathf.Approximately(subdueNow, subdueWas * StaffMemories.HatedSubdueFactor))
                        problems0.Add($"three restraints by one guard: hated {StaffMemories.MostHated(ic)?.name}, subdue {subdueWas:P0} -> {subdueNow:P0}");
                    if (StaffMemories.HatedAmong(ic, new[] { otherGuard, hatedGuard }) != hatedGuard) problems0.Add("an inmate who hates a guard did not single them out");
                    // In the same room, the hated guard winds them up. (Earlier checks changed doors and zones: the rooms are made up to date first, as a room looked up while they
                    // are dirty may be one that is already gone.)
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                    if (CellFinder.TryFindRandomReachableNearbyCell(inmate.Position, map, 2, TraverseParms.For(inmate), x => x.Standable(map) && x.GetRoom(map) == inmate.GetRoom(), null, out IntVec3 nearInmate))
                    {
                        IntVec3 guardWas = hatedGuard.Position;
                        hatedGuard.pather.StopDead();
                        hatedGuard.Position = nearInmate;
                        hatedGuard.Notify_Teleported();
                        // (A throw here is a problem of its own with what the call saw, not the end of the run: it once ended the whole test with a bare null reference.)
                        try
                        {
                            if (StaffMemories.RoomTension(inmate, ic) <= 0f) problems0.Add("a hated guard in the room did not wind the inmate up");
                        }
                        catch (Exception e)
                        {
                            problems0.Add($"staff memories: the room tension of {inmate.LabelShort} threw {e.GetType().Name} (spawned {inmate.Spawned}, map {inmate.Map != null}, held in {inmate.holdingOwner?.Owner.ToStringSafe() ?? "nothing"}, room {inmate.GetRoom() != null}, "
                                          + $"downed {inmate.Downed}, job {inmate.CurJobDef?.defName}); the guard {hatedGuard.LabelShort} spawned {hatedGuard.Spawned} at {hatedGuard.Position}, room {hatedGuard.GetRoom() != null}): {e.StackTrace?.Split('\n').FirstOrDefault()}");
                        }
                        hatedGuard.Position = guardWas;
                        hatedGuard.Notify_Teleported();
                    }
                    // Carried to a bed when hurt (twice): trust; carried while restrained (to punishment): nothing. (Nothing
                    // pending for them meanwhile: a punishment due also withholds the credit.)
                    (int pendingWas, RegimeActivityDef forcedWas) = (ic.pendingPunishmentHours, ic.forcedActivity);
                    (ic.pendingPunishmentHours, ic.forcedActivity) = (0, null);
                    for (int i = 0; i < 2; i++) StaffMemories.Notify_CarriedToBed(otherGuard, inmate);
                    if (StaffMemories.MostTrusted(ic)?.staffId != otherGuard.thingIDNumber) problems0.Add("being carried to a bed twice did not build trust");
                    Hediff restraint = inmate.health.AddHediff(RPSecurityDefOf.RP_Restrained);
                    float trustWas = StaffMemories.ScoreFor(ic, otherGuard);
                    StaffMemories.Notify_CarriedToBed(otherGuard, inmate);
                    if (StaffMemories.ScoreFor(ic, otherGuard) != trustWas) problems0.Add("a restrained inmate carried to punishment remembered it kindly");
                    inmate.health.RemoveHediff(restraint);
                    (ic.pendingPunishmentHours, ic.forcedActivity) = (pendingWas, forcedWas);
                    // Feelings fade.
                    float hate = StaffMemories.ScoreFor(ic, hatedGuard);
                    StaffMemories.Fade(ic);
                    if (StaffMemories.ScoreFor(ic, hatedGuard) <= hate) problems0.Add("feelings about staff did not fade");
                    // A friend shot dead.
                    bool wereFriends = PrisonRelations.Friends(friend, inmate);
                    if (!wereFriends) friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, inmate);
                    GangDef victimGang = ic.gang;
                    ic.gang = null; // (only the friend: no gang-mates left hating a guard for the rest of the run)
                    StaffMemories.Notify_ShotDead(inmate, hatedGuard);
                    ic.gang = victimGang;
                    if (StaffMemories.ScoreFor(fc3, hatedGuard) > StaffMemories.FriendShot + 0.01f) problems0.Add("a friend of an inmate shot dead does not hate the shooter");
                    RPLog.Message($"[RimPrison:test] staff memories: {inmate.LabelShort} hates {hatedGuard.LabelShort} (subdue {subdueWas:P0} -> {subdueNow:P0}), trusts {otherGuard.LabelShort}; {friend.LabelShort} hates the shooter ({StaffMemories.ScoreFor(fc3, hatedGuard):F0})");
                    if (!wereFriends) friend.relations.TryRemoveDirectRelation(RPRelationDefOf.RP_Friend, inmate);
                    ic.staffMemories.Clear();
                    ic.staffMemories.AddRange(memWas);
                    fc3.staffMemories.Clear();
                    fc3.staffMemories.AddRange(friendMemWas);
                    goalsBackAfterShot();
                    if (GoalSnapshot(map) != goalsBeforeShot) problems0.Add("the staff memories test left the inmates' goals changed: " + GoalDiff(goalsBeforeShot, GoalSnapshot(map)));
                }
            }

            private void EvInmateFileStatusLines()
            {
                // The inmate file's status lines: punishment due, a visit ban, a second stay, a job waiting, parole, risk.
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { forcedActivity: null } xc && !xc.IsLifer && DeathRowSystem.Ext(xc) == null) is { } filed)
                {
                    CompInmate fc2 = filed.Inmate();
                    (int pun, int ban, int adm, bool jw) = (fc2.pendingPunishmentHours, fc2.visitBanUntilTick, fc2.timesAdmitted, fc2.jobWaiting);
                    (fc2.pendingPunishmentHours, fc2.visitBanUntilTick, fc2.timesAdmitted, fc2.jobWaiting) = (12, Find.TickManager.TicksGame + 2 * GenDate.TicksPerDay, 2, true);
                    string lines = string.Join(" | ", ITab_InmateFile.StatusLines(filed, fc2, Find.TickManager.TicksGame).Select(x => x.Item1));
                    foreach (string key in new[] { "RP_FileStatusPunishmentDue", "RP_FileStatusVisitBan", "RP_FileStatusAdmitted", "RP_FileStatusJobWaiting" })
                        if (!lines.Contains(key.Translate("X").ToString().Split('X')[0].Trim())) problems0.Add($"the inmate file shows no {key} line: {lines}");
                    if (!lines.Contains("parole") || !lines.Contains("reoffending")) problems0.Add($"the inmate file shows no parole or risk line: {lines}");
                    (fc2.pendingPunishmentHours, fc2.visitBanUntilTick, fc2.timesAdmitted, fc2.jobWaiting) = (pun, ban, adm, jw);
                    // Friends and enemies still inside, and (past half the sentence) the next parole hearing once one has been held.
                    if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x != filed && x.Inmate() != null && !PrisonRelations.Friends(filed, x) && !PrisonRelations.Enemies(filed, x)) is { } flOther)
                    {
                        int flNow = Find.TickManager.TicksGame;
                        string FileLines() => string.Join(" | ", ITab_InmateFile.StatusLines(filed, fc2, flNow).Select(x => x.Item1));
                        // (Earlier events gave them friends and enemies: set those aside for the check, and put them back.)
                        var flKept = filed.relations.DirectRelations.Where(r => r.def == RPRelationDefOf.RP_Friend || r.def == RPRelationDefOf.RP_Enemy).Select(r => (r.def, r.otherPawn)).ToList();
                        foreach ((PawnRelationDef kd, Pawn ko) in flKept) filed.relations.RemoveDirectRelation(kd, ko);
                        string flQuiet = FileLines();
                        filed.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, flOther);
                        string flFriends = FileLines();
                        filed.relations.TryRemoveDirectRelation(RPRelationDefOf.RP_Friend, flOther);
                        filed.relations.AddDirectRelation(RPRelationDefOf.RP_Enemy, flOther);
                        string flEnemies = FileLines();
                        filed.relations.TryRemoveDirectRelation(RPRelationDefOf.RP_Enemy, flOther);
                        foreach ((PawnRelationDef kd, Pawn ko) in flKept) filed.relations.AddDirectRelation(kd, ko);
                        string flFriendKey = "RP_FileFriends".Translate("X").ToString().Split('X')[0], flEnemyKey = "RP_FileEnemies".Translate("X").ToString().Split('X')[0];
                        if (flQuiet.Contains(flFriendKey) || flQuiet.Contains(flEnemyKey)) problems0.Add($"the inmate file lists friends or enemies for an inmate with none: {flQuiet}");
                        if (!flFriends.Contains(flFriendKey + flOther.LabelShort)) problems0.Add($"the inmate file does not list a friend inside: {flFriends}");
                        if (!flEnemies.Contains(flEnemyKey + flOther.LabelShort)) problems0.Add($"the inmate file does not list an enemy inside: {flEnemies}");
                        (int sentenceWas, int heardWas) = (fc2.sentenceDays, fc2.lastHearingTick);
                        (fc2.sentenceDays, fc2.lastHearingTick) = (0, flNow - GenDate.TicksPerDay);
                        string flNext = FileLines();
                        fc2.lastHearingTick = flNow - ParoleSystem.HearingGapDays * GenDate.TicksPerDay - 1;
                        string flDue = FileLines();
                        (fc2.sentenceDays, fc2.lastHearingTick) = (sentenceWas, heardWas);
                        string flNextKey = "RP_FileStatusParoleNext".Translate("X").ToString().Split('X')[0].Trim();
                        if (!flNext.Contains(flNextKey)) problems0.Add($"the inmate file shows no next parole hearing a day after one: {flNext}");
                        if (flDue.Contains(flNextKey) || !flDue.Contains("Eligible for parole hearings")) problems0.Add($"the inmate file still shows a next hearing when one is due: {flDue}");
                    }
                    RPLog.Message($"[RimPrison:test] file status: {lines}");
                }
            }

            private void EvOneLeaderPerGang()
            {
                // One leader per gang: a newcomer joining a gang that has a leader inside never leads it. A notorious
                // inmate is a target (vulnerable), as the reputation says.
                // (Not anyone on the way out: a leader waiting for the shuttle does not hold the post, so a newcomer may take it.)
                if (map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null && !ShuttleSystem.OnTheWayOut(x)).Take(2).ToList() is { Count: 2 } gp)
                {
                    CompInmate lead = gp[0].Inmate(), joiner = gp[1].Inmate();
                    (GangDef lg, bool ll, GangDef jg, bool jl) = (lead.gang, lead.gangLeader, joiner.gang, joiner.gangLeader);
                    int secondLeaders = 0, joinedLed = 0;
                    for (int seed = 0; seed < 300; seed++)
                    {
                        lead.gang = DefDatabase<GangDef>.AllDefsListForReading[seed % DefDatabase<GangDef>.AllDefsListForReading.Count];
                        lead.gangLeader = true;
                        (joiner.gang, joiner.gangLeader) = (null, false);
                        Rand.PushState(seed);
                        GangUtility.RollAtIntake(gp[1], joiner);
                        Rand.PopState();
                        if (joiner.gang == lead.gang)
                        {
                            joinedLed++;
                            if (joiner.gangLeader) secondLeaders++;
                        }
                    }
                    if (secondLeaders > 0) problems0.Add($"{secondLeaders} of {joinedLed} newcomers to a gang with a leader inside became a second leader");
                    (lead.gang, lead.gangLeader, joiner.gang, joiner.gangLeader) = (lg, ll, jg, jl);
                    ReputationDef notorious = DefDatabase<ReputationDef>.GetNamed("RP_Notorious");
                    bool had = joiner.reputations.Contains(notorious);
                    if (!had) joiner.reputations.Add(notorious);
                    if (!FearSystem.Vulnerable(joiner)) problems0.Add("a notorious inmate is not a target (not vulnerable)");
                    if (!had) joiner.reputations.Remove(notorious);
                    RPLog.Message($"[RimPrison:test] gangs: {joinedLed} newcomers joined a led gang, {secondLeaders} became a second leader");
                }
            }

            private void EvDoubleAgentBurnt()
            {
                // A double agent burnt by the gang is no longer "known corrupt": bought again, internal affairs can catch them.
                if (StaffLives.OnSite(map).FirstOrDefault(GuardUtility.IsGuard) is { } agent && map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() != null) is { } burner)
                {
                    CompStaff asc = agent.GetComp<CompStaff>();
                    (bool da, bool ex, bool co, float sus) = (asc.doubleAgent, asc.exposedCorrupt, asc.corrupt, asc.suspicion);
                    (asc.doubleAgent, asc.exposedCorrupt, asc.corrupt) = (true, true, true);
                    CorruptionSystem.Burn(agent, asc, burner);
                    burner.MentalState?.RecoverFromState();
                    float susBefore = asc.suspicion;
                    CorruptionSystem.AddSuspicion(agent, asc, 5f, "RP_Sus_SeenTalking");
                    if (asc.exposedCorrupt || asc.doubleAgent || asc.suspicion <= susBefore)
                        problems0.Add($"a burnt double agent stays known corrupt and out of internal affairs' reach (suspicion {susBefore} -> {asc.suspicion})");
                    (asc.doubleAgent, asc.exposedCorrupt, asc.corrupt, asc.suspicion) = (da, ex, co, sus);
                    asc.evidence.Remove("RP_Sus_SeenTalking");
                    foreach (var bl2 in Find.LetterStack.LettersListForReading.Where(l => l.Label.ToString() == "RP_DoubleAgentBurntLabel".Translate().ToString()).ToList())
                        Find.LetterStack.RemoveLetter(bl2);
                }
            }

            private void EvContrabandAmnesty()
            {
                // Contraband amnesty: a calm inmate hands in pocket and stash; a dealer keeps theirs; it ends with a
                // tally, and another cannot be declared straight away.
                if (map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { } xc && InmateUtility.IsActiveInmate(x) && !x.InMentalState && !x.Downed && x.inventory != null
                            && !xc.HasReputation(RPReputationDefOf.RP_Extortionist) && !xc.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer)))
                        .Take(2).ToList() is { Count: 2 } amnestyPair)
                {
                    Pawn calm = amnestyPair[0], trader = amnestyPair[1];
                    CompInmate cc = calm.Inmate(), tc = trader.Inmate();
                    ReputationDef dealerRep = DefDatabase<ReputationDef>.GetNamed("RP_Dealer");
                    bool traderWas = tc.reputations.Contains(dealerRep);
                    if (!traderWas) tc.reputations.Add(dealerRep);
                    (float calmTension, float traderTension, float repWas) = (cc.tension, tc.tension, g.reputation);
                    cc.tension = 10f;
                    tc.tension = 10f;
                    ThingDef item = DefDatabase<ThingDef>.AllDefsListForReading.First(d => ContrabandUtility.Ext(d) != null);
                    calm.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(item));
                    cc.stash.TryAdd(ThingMaker.MakeThing(item), canMergeWithExistingStacks: false);
                    Thing kept = ThingMaker.MakeThing(item);
                    trader.inventory.innerContainer.TryAdd(kept, canMergeWithExistingStacks: false);
                    // A tip pending on the calm one, and a delivery traced to a guard: both gone with the hand-in.
                    cc.suspected = true;
                    cc.smuggledBy = StaffLives.OnSite(map).FirstOrDefault();
                    (g.amnestyUntilTick, g.lastAmnestyTick) = (-1, -999999);
                    Amnesty.Declare(g);
                    if (!Amnesty.Active(g) || Amnesty.DaysUntilReady(g) <= 0f || g.reputation >= repWas) problems0.Add("declaring an amnesty: not active, no cooldown or no reputation cost");
                    Amnesty.Hourly(g, map);
                    var carried = new List<Thing>();
                    ContrabandUtility.CarriedContraband(calm, carried);
                    if (carried.Count > 0 || cc.stash.Count > 0 || g.amnestyHandedIn < 2) problems0.Add($"an amnesty: the calm inmate kept {carried.Count} carried and {cc.stash.Count} hidden ({g.amnestyHandedIn} handed in)");
                    if (kept.Destroyed || !trader.inventory.innerContainer.Contains(kept)) problems0.Add("a dealer handed contraband in during an amnesty");
                    if (cc.suspected || cc.smuggledBy != null) problems0.Add("a hand-in left a tip pending or a guard traced through it");
                    // Once each: resupplied within the same amnesty, they keep it.
                    Thing again = ThingMaker.MakeThing(item);
                    calm.inventory.innerContainer.TryAdd(again, canMergeWithExistingStacks: false);
                    int tally = g.amnestyHandedIn;
                    Amnesty.Hourly(g, map);
                    if (again.Destroyed || g.amnestyHandedIn != tally) problems0.Add("an inmate handed in twice in one amnesty");
                    if (!again.Destroyed) { calm.inventory.innerContainer.Remove(again); again.Destroy(); }
                    // No second amnesty during the first or its cooldown.
                    int declaredAt = g.lastAmnestyTick;
                    Amnesty.Declare(g);
                    if (g.lastAmnestyTick != declaredAt) problems0.Add("an amnesty was declared during the last one");
                    g.amnestyUntilTick = Find.TickManager.TicksGame;
                    Amnesty.Hourly(g, map);
                    if (g.amnestyUntilTick != -1) problems0.Add("an amnesty did not end");
                    RPLog.Message($"[RimPrison:test] amnesty: {g.amnestyHandedIn} items handed in by {calm.LabelShort}, {trader.LabelShort} (dealer) kept theirs");
                    if (!kept.Destroyed) { trader.inventory.innerContainer.Remove(kept); kept.Destroy(); }
                    if (!traderWas) tc.reputations.Remove(dealerRep);
                    (cc.tension, tc.tension, g.reputation) = (calmTension, traderTension, repWas);
                }
            }

            private void EvRemission()
            {
                // Good behaviour remission: off, nothing; on, the tenth clean day takes a day off (never the last), a
                // fresh record resets the count, and days at stake make misconduct less likely.
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => Remission.Eligible(x.Inmate()) && x.Inmate().sentenceDays > 4) is { } good)
                {
                    CompInmate rc = good.Inmate();
                    bool policyWas = g.remission;
                    // Daily counts everyone: put every inmate's count back afterwards.
                    var countsWas = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() != null)
                        .ToDictionary(x => x.Inmate(), x => (x.Inmate().cleanDays, x.Inmate().lastCleanCheckTick));
                    List<MisconductRecord> recordsWas = rc.records;
                    int sentenceWas = rc.sentenceDays, earnedWas = rc.remissionEarned, remissionStat = g.stats.remissionDays;
                    rc.records = new List<MisconductRecord>();
                    rc.sentenceDays = UnityEngine.Mathf.CeilToInt(rc.DaysServed) + 5;
                    int sentence = rc.sentenceDays;
                    g.remission = false;
                    rc.cleanDays = 9;
                    Remission.Daily(g, map);
                    if (rc.cleanDays != 9 || rc.sentenceDays != sentence) problems0.Add("remission counted with the policy off");
                    if (!UnityEngine.Mathf.Approximately(Remission.MisconductFactor(rc), 1f)) problems0.Add("remission eased misconduct with the policy off");
                    g.remission = true;
                    Remission.Daily(g, map);
                    if (rc.cleanDays != 10 || rc.sentenceDays != sentence - 1 || rc.remissionEarned != earnedWas + 1 || g.stats.remissionDays != remissionStat + 1)
                        problems0.Add($"the tenth clean day: clean {rc.cleanDays}, sentence {sentence} -> {rc.sentenceDays}, earned {rc.remissionEarned}");
                    if (!UnityEngine.Mathf.Approximately(Remission.MisconductFactor(rc), Remission.AtStakeMisconduct)) problems0.Add("days at stake did not ease misconduct");
                    // Never the last day.
                    rc.sentenceDays = UnityEngine.Mathf.FloorToInt(rc.DaysServed) + 1; // under a day to go
                    int lastDay = rc.sentenceDays;
                    rc.cleanDays = 19;
                    Remission.Daily(g, map);
                    if (rc.sentenceDays != lastDay) problems0.Add("remission took the last day off a sentence");
                    // Something nobody saw: the record is clean, the streak stands. Caught at something: back to zero.
                    int streak = rc.cleanDays;
                    rc.records.Add(new MisconductRecord { def = DefDatabase<MisconductDef>.AllDefsListForReading.First(), tick = Find.TickManager.TicksGame, seen = false });
                    rc.lastCleanCheckTick = Find.TickManager.TicksGame - 1;
                    Remission.Daily(g, map);
                    if (rc.cleanDays != streak + 1) problems0.Add($"unseen misconduct broke a remission streak ({streak} -> {rc.cleanDays})");
                    rc.records.Add(new MisconductRecord { def = DefDatabase<MisconductDef>.AllDefsListForReading.First(), tick = Find.TickManager.TicksGame, seen = true });
                    rc.lastCleanCheckTick = Find.TickManager.TicksGame - 1;
                    Remission.Daily(g, map);
                    if (rc.cleanDays != 0 || !UnityEngine.Mathf.Approximately(Remission.MisconductFactor(rc), 1f)) problems0.Add($"misconduct on the record did not reset the clean days ({rc.cleanDays})");
                    RPLog.Message($"[RimPrison:test] remission: {good.LabelShort} sentence {sentence} -> {sentence - 1} on the tenth clean day");
                    (rc.records, rc.sentenceDays, rc.remissionEarned, g.remission) = (recordsWas, sentenceWas, earnedWas, policyWas);
                    foreach (var kv in countsWas) (kv.Key.cleanDays, kv.Key.lastCleanCheckTick) = kv.Value;
                }
                else problems0.Add("remission test: no inmate with a sentence to shorten");
            }

            private void EvSupervisedDetox()
            {
                // Supervised detox: in withdrawal with a doctor on site, eased and fading twice as fast; not when the craving
                // is satisfied or no doctor is here; the last addiction gone under supervision is a chronicle line.
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && !x.health.hediffSet.HasHediff<Hediff_Addiction>() && Detox.Supervision(x) == null) is { } addict
                    && Detox.DoctorOnSite(map))
                {
                    HediffDef addictionDef = ChemicalDefOf.Alcohol.addictionHediff;
                    Hediff addiction = addict.health.AddHediff(addictionDef);
                    addiction.Severity = 0.5f;
                    addict.needs.AddOrRemoveNeedsAsAppropriate();
                    var craving = (Need_Chemical)addict.needs.TryGetNeed(addictionDef.chemicalNeed);
                    craving.CurLevel = 1f;
                    Detox.Hourly(g, map);
                    bool satisfiedSupervised = Detox.Supervision(addict) != null;
                    craving.CurLevel = 0f;
                    float consciousnessWithdrawn = addict.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness);
                    var doctorsAway = StaffLives.OnSite(map).Where(x => x.GetComp<CompStaff>().role.defName == "RP_Doctor").Select(x => (x.GetComp<CompStaff>(), x.GetComp<CompStaff>().leaveUntilTick)).ToList();
                    foreach (var (dsc, _) in doctorsAway) dsc.leaveUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerHour;
                    Detox.Hourly(g, map);
                    bool noDoctorSupervised = Detox.Supervision(addict) != null;
                    foreach (var (dsc, leaveWas) in doctorsAway) dsc.leaveUntilTick = leaveWas;
                    int detoxHoursWas = g.stats.detoxHours, cleanWas = g.stats.gotClean;
                    Detox.Hourly(g, map);
                    Hediff supervision = Detox.Supervision(addict);
                    float consciousnessSupervised = addict.health.capacities.GetLevel(PawnCapacityDefOf.Consciousness);
                    float severityBefore = addiction.Severity;
                    if (supervision != null) Detox.Fade(g, addict, supervision);
                    float faded = severityBefore - addiction.Severity;
                    ChronicleEntry lastBeforeClean = g.story.entries.LastOrDefault();
                    addiction.Severity = 0.02f;
                    if (supervision != null) Detox.Fade(g, addict, supervision);
                    bool clean = !addict.health.hediffSet.HasHediff<Hediff_Addiction>() && Detox.Supervision(addict) == null && g.stats.gotClean == cleanWas + 1
                        && addict.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPDetoxDefOf.RP_GotClean) && g.story.entries.LastOrDefault() != lastBeforeClean;
                    if (satisfiedSupervised || noDoctorSupervised || supervision == null || g.stats.detoxHours <= detoxHoursWas || consciousnessSupervised <= consciousnessWithdrawn
                        || !UnityEngine.Mathf.Approximately(faded, Detox.ExtraFadePerDay) || !clean)
                        problems0.Add($"supervised detox: satisfied {satisfiedSupervised}, no doctor {noDoctorSupervised}, supervised {supervision != null}, consciousness {consciousnessWithdrawn:F2} -> {consciousnessSupervised:F2}, faded {faded:F4}, clean {clean}");
                    RPLog.Message($"[RimPrison:test] detox: {addict.LabelShort} consciousness {consciousnessWithdrawn:F2} -> {consciousnessSupervised:F2}, faded {faded:F4}/day extra, clean {clean}");
                    // Back as they were.
                    if (addict.health.hediffSet.GetFirstHediffOfDef(addictionDef) is { } left) addict.health.RemoveHediff(left);
                    if (Detox.Supervision(addict) is { } sup) addict.health.RemoveHediff(sup);
                    addict.needs.mood.thoughts.memories.RemoveMemoriesOfDef(RPDetoxDefOf.RP_GotClean);
                    addict.needs.AddOrRemoveNeedsAsAppropriate();
                    int keepEntries = lastBeforeClean == null ? 0 : g.story.entries.IndexOf(lastBeforeClean) + 1;
                    if (keepEntries > 0 || lastBeforeClean == null) g.story.entries.RemoveRange(keepEntries, g.story.entries.Count - keepEntries);
                    g.stats.gotClean = cleanWas;
                }
                else RPLog.Message("[RimPrison:test] detox: skipped (no doctor on site or no inmate free of addictions)");
            }

            private void EvFamilyDay()
            {
                var fd = ChoiceLetter_PrisonDecision.Make("RP_Decision_FamilyDay", "test", "test", map);
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(InmateUtility.IsActiveInmate) is { } kin) kin.Inmate().familyCount = System.Math.Max(1, kin.Inmate().familyCount);
                PrisonDecisions.Choose(fd, 0);
                if (!map.mapPawns.PrisonersOfColonySpawned.Any(x => x.needs.mood?.thoughts.memories.Memories.Any(m => m.def == RPWelfareDefOf.RP_FamilyDay) == true))
                    problems0.Add("a family day lifted nobody");
            }

            private void EvOfficerTooClose()
            {
                // An officer too close: a warning raises integrity; looking away always pleases the inmate.
                // (An officer not already carrying for someone: one bought earlier in the run would fail the last check.)
                if (StaffLives.OnSite(map).FirstOrDefault(x => GuardUtility.IsGuard(x) && !x.GetComp<CompStaff>().corrupt) is { } officer
                    && map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(InmateUtility.IsActiveInmate) is { } favourite)
                {
                    CompStaff osc = officer.GetComp<CompStaff>();
                    bool wasCorrupt = osc.corrupt;
                    float was = osc.integrity;
                    osc.integrity = 60f;
                    var tc = ChoiceLetter_PrisonDecision.Make("RP_Decision_TooClose", "test", "test", map, officer);
                    tc.pawns.Add(officer);
                    tc.pawns.Add(favourite);
                    PrisonDecisions.Choose(tc, 1);
                    if (osc.integrity < 64.9f) problems0.Add("a formal warning did not stiffen an officer's integrity");
                    PrisonDecisions.Choose(tc, 2);
                    if (!favourite.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPWelfareDefOf.RP_SpecialAttention)) problems0.Add("looking the other way changed nothing for the inmate");
                    if (osc.corrupt && osc.patron != favourite) problems0.Add("an officer who looked the other way is carrying for someone else");
                    osc.integrity = was;
                    if (!wasCorrupt) { osc.corrupt = false; osc.patron = null; osc.patronGang = null; }
                }
            }
        }
    }
}
