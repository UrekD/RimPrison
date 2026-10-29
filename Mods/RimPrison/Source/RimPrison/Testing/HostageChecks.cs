using System;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The hostage crisis (events plan; its round trip through a save is in the saveload plan). Everything is built from throwaway
    /// pawns in the starter prison's canteen and workshop: a crisis starts from a riot (with a ringleader next to a guard, the seizure's
    /// chance roll forced; and not when the dice say no or nobody is in reach) and from an assault in the workshop, holds the room (the
    /// doors on its border refuse staff coming in, the held cells are the zone), exempts the hostage from the commute and from guard
    /// duty, and asks the warden once; every ending is driven through the decision (rolls seeded where there is a roll): a trusted
    /// officer talking them down, failed talks and then giving the grievance (a promise), cutting power and water and then storming
    /// it (the riot police spawn, the arc ends), a storm that kills the hostage and a rioter (a murder charge, an inquiry), the
    /// deadline (the hostage hurt, then killed or let go) and the hostage-takers giving up or leaving; also the held wing.
    /// Its own method so its names stay its own; what it changes is put back (the rollback, and the rest here).
    /// </summary>
    public static partial class AutoTest
    {
        private static void HostageTest(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            StoryArcDef arcDef = RPHostageDefOf.RP_Arc_Hostage;
            var worker = (HostageArc)arcDef.Worker;
            void Fail(string what) => problems0.Add("hostage: " + what);
            if (HostageCrisis.Running() != null)
            {
                Fail("could not run (a crisis is already running)");
                return;
            }
            IntVec3 canteenCell = PrisonBuilder.CanteenCell(map), workshopCell = PrisonBuilder.WorkshopCell(map), corridorCell = PrisonBuilder.CorridorCell(map);
            Room canteen = canteenCell.GetRoom(map), workshop = workshopCell.GetRoom(map), corridor = corridorCell.GetRoom(map);
            if (HostageCrisis.HoldRoom(canteenCell, map) == null || HostageCrisis.HoldRoom(workshopCell, map) == null || HostageCrisis.HoldRoom(corridorCell, map) == null
                || RPDefOf.RP_Canteen == null || pm.zones.ZoneAt(workshopCell) != RPLaborDefOf.RP_Workshop)
            {
                Fail("could not run (the starter prison's canteen, workshop and corridor are not rooms the rioters could hold)");
                return;
            }
            using var r = new Rollback(map);
            var started = new List<StoryArc>();
            var undo = new List<Action>();
            var buried = new List<Pawn>();
            bool hadStart = g.story.lastArcStart.TryGetValue(arcDef, out int start0);
            (int drama0, int beats0, int lastRiot0) = (g.story.dramaToday, g.story.beatsToday, g.story.lastRiotTick);
            (NeedDef need0, int deadline0, int spokes0, string spokesName0, int cooldown0) = (g.demand.need, g.demand.deadlineTick, g.demand.spokesId, g.demand.spokesName, g.demand.cooldownUntilTick);
            // Nobody real is taken, tense or counted as present: the staff on the payroll are out of reach of the check (nothing ticks meanwhile),
            // and the real inmates are calm so that none joins the riots here.
            foreach (Pawn real in StaffCommute.AllStaff(map))
                if (real.GetComp<CompStaff>() is { } rsc)
                {
                    int away = rsc.leaveAtTick;
                    rsc.leaveAtTick = int.MaxValue;
                    undo.Add(() => rsc.leaveAtTick = away);
                }
            foreach (Pawn calm in map.mapPawns.PrisonersOfColonySpawned)
                if (calm.Inmate() is { } cc) cc.tension = 0f;
            var results = new List<string>();
            try
            {
                PrisonStats st = g.stats;
                // ---- helpers ------------------------------------------------------------------------------------------
                List<IntVec3> Spots(IntVec3 centre, Room room) =>
                    GenRadial.RadialCellsAround(centre, 6f, true).Where(c => c.InBounds(map) && c.Standable(map) && c.GetRoom(map) == room && c.GetEdifice(map) == null).ToList();
                List<IntVec3> canteenSpots = Spots(canteenCell, canteen), workshopSpots = Spots(workshopCell, workshop);
                if (canteenSpots.Count < 8 || workshopSpots.Count < 8)
                {
                    Fail($"could not run (room to stand: {canteenSpots.Count} cells in the canteen, {workshopSpots.Count} in the workshop)");
                    return;
                }
                void Put(Pawn p, List<IntVec3> spots, int i)
                {
                    p.pather?.StopDead();
                    p.jobs?.StopAll();
                    p.Position = spots[i];
                    p.Notify_Teleported();
                }
                // (The staff of one check do not stand about for the next: the nearest one is the one a ringleader takes.)
                var madeStaff = new List<Pawn>();
                void ClearStaff()
                {
                    foreach (Pawn gone in madeStaff)
                        if (!gone.Destroyed) gone.Destroy(DestroyMode.Vanish);
                    madeStaff.Clear();
                }
                Pawn Staff(StaffRoleDef role, List<IntVec3> spots = null, int i = 0)
                {
                    Pawn s = ThrowawayStaff(map, r, role);
                    madeStaff.Add(s);
                    if (spots != null) Put(s, spots, i);
                    return s;
                }
                Pawn Rioter(List<IntVec3> spots, int i)
                {
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        Pawn p = Throwaway(map, r);
                        if (p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                        r.Add(() => p.ownership?.UnclaimAll()); // (a demotion may have given it a bed: it goes back before the pawn is destroyed)
                        buried.Add(p);
                        Put(p, spots, i);
                        p.Inmate().tension = 80f;
                        // (An ordinary short sentence: a graver conviction is kept by the charge, which the checks of the charge look at.)
                        p.Inmate().crime = DefDatabase<CrimeDef>.GetNamed("RP_Crime_Racketeering");
                        p.Inmate().sentenceDays = 30;
                        return p;
                    }
                    return null;
                }
                bool Has(Pawn p, ThoughtDef d) => p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(d) != null;
                bool Told(string line) => r.NewEntries.Any(e => e.text == line);
                int Now() => Find.TickManager.TicksGame;
                ChoiceLetter_PrisonDecision Open(StoryArc a) => Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.arcId == a.id && !l.answered);
                int Letters(StoryArc a) => Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.arcId == a.id && !l.answered);
                int Seed(Func<bool> wanted)
                {
                    for (int seed = 1; seed < 5000; seed++)
                    {
                        Rand.PushState(seed);
                        bool ok = wanted();
                        Rand.PopState();
                        if (ok) return seed;
                    }
                    return -1;
                }
                // The warden's answer through the open letter, the way the letter does it, with the dice seeded when there is a roll.
                bool Answer(StoryArc a, int option, string what, int seed = 0)
                {
                    ChoiceLetter_PrisonDecision letter = Open(a);
                    if (letter == null)
                    {
                        Fail($"no open decision letter to answer ({what})");
                        return false;
                    }
                    if (seed < 0)
                    {
                        Fail($"no seed gives the roll this check needs ({what})");
                        return false;
                    }
                    letter.answered = true;
                    if (seed > 0) Rand.PushState(seed);
                    try { PrisonDecisions.Choose(letter, option); }
                    finally
                    {
                        if (seed > 0) Rand.PopState();
                    }
                    Find.LetterStack.RemoveLetter(letter);
                    return true;
                }
                // A crisis from nothing: the hostage (a guard), the ringleader and followers rioting, all in the canteen next to each other.
                StoryArc Begin(out Pawn hostage, out Pawn leader, int followers = 1, List<IntVec3> spots = null, Room room = null)
                {
                    foreach (StoryArc running in g.story.arcs.Where(a => !a.done && a.def == arcDef).ToList())
                    {
                        running.done = true;
                        HostageCrisis.Release(running.A);
                    }
                    g.story.lastArcStart.Remove(arcDef);
                    ClearStaff();
                    spots ??= canteenSpots;
                    hostage = Staff(RPSecurityDefOf.RP_Guard, spots, 1);
                    leader = Rioter(spots, 0);
                    var crew = new List<Pawn>();
                    for (int i = 0; i < followers; i++)
                    {
                        Pawn follower = Rioter(spots, 2 + i);
                        if (follower == null) continue;
                        follower.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
                        crew.Add(follower);
                    }
                    if (hostage == null || leader == null)
                    {
                        Fail("could not run (no violent-capable inmate to be the ringleader)");
                        return null;
                    }
                    StoryArc arc = HostageCrisis.Seize(map, leader, leader.Inmate(), crew, hostage, HostageCrisis.Riot);
                    if (arc != null) started.Add(arc);
                    return arc;
                }
                // The hostage is let go as the crisis ends.
                bool Freed(Pawn hostage) => !hostage.Dead && !HostageCrisis.IsHeld(hostage) && hostage.MentalStateDef != RPHostageDefOf.RP_BeingHeld;
                string Line(string key, Pawn hostage, Pawn leader, string detail = "") =>
                    key.Translate(hostage.LabelShort, StaffCareers.Title(hostage.GetComp<CompStaff>()), leader.LabelShort, detail).Resolve();

                // ---- the dice say no, or nobody is in reach: no crisis, and the riot is the riot it was ------------------
                {
                    HostageCrisis.ForcedRoll = false;
                    ClearStaff();
                    Pawn guard = Staff(RPSecurityDefOf.RP_Guard, canteenSpots, 1);
                    Pawn boss = Rioter(canteenSpots, 0), second = Rioter(canteenSpots, 2), third = Rioter(canteenSpots, 3);
                    if (boss == null || second == null || third == null) Fail("could not run (no three violent-capable inmates for a riot)");
                    else
                    {
                        int riots0 = st.riots;
                        bool riot = RPRiotDefOf.RP_Riot.Worker.TryExecute(boss, boss.Inmate());
                        bool negotiation = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_RiotNegotiation"));
                        bool noCrisis = HostageCrisis.Running() == null && !HostageCrisis.IsHeld(guard);
                        results.Add($"dice-say-no riot {riot}/negotiation letter {negotiation}/no crisis {noCrisis}");
                        if (!riot || !negotiation || !noCrisis || st.riots != riots0 + 1)
                            Fail($"with the dice saying no, the riot did not run as it always did (riot {riot}, negotiation letter {negotiation}, no crisis {noCrisis}, riots +{st.riots - riots0})");
                        foreach (Pawn p in new[] { boss, second, third })
                        {
                            p.MentalState?.RecoverFromState();
                            p.Inmate().tension = 0f; // (spent: the next riot is not theirs)
                        }
                        // Nobody in reach: the same with the roll forced to yes, the ringleader out of the guard's room.
                        HostageCrisis.ForcedRoll = true;
                        Put(guard, canteenSpots, 7);
                        Pawn alone = Rioter(workshopSpots, 0);
                        Pawn alone2 = Rioter(workshopSpots, 1), alone3 = Rioter(workshopSpots, 2);
                        if (alone == null || alone2 == null || alone3 == null) Fail("could not run (no three more inmates for a riot out of reach)");
                        else
                        {
                            bool reachable = HostageCrisis.Candidate(alone, alone.Inmate(), null) != null;
                            bool riot2 = RPRiotDefOf.RP_Riot.Worker.TryExecute(alone, alone.Inmate());
                            bool none = HostageCrisis.Running() == null && !HostageCrisis.IsHeld(guard);
                            results.Add($"nobody-in-reach candidate {reachable}/no crisis {none}");
                            if (reachable || !riot2 || !none) Fail($"a ringleader with nobody in reach took a hostage or the riot did not run (candidate {reachable}, riot {riot2}, no crisis {none})");
                            foreach (Pawn p in new[] { alone, alone2, alone3 })
                            {
                                p.MentalState?.RecoverFromState();
                                p.Inmate().tension = 0f;
                            }
                        }
                        HostageCrisis.ForcedRoll = null;
                    }
                }

                // ---- a riot with a ringleader next to a guard, the roll forced: the seizure and the stand-off ------------
                {
                    HostageCrisis.ForcedRoll = true;
                    g.story.lastArcStart.Remove(arcDef);
                    ClearStaff();
                    Pawn guard = Staff(RPSecurityDefOf.RP_Guard, canteenSpots, 1);
                    Pawn insider = Staff(RPSecurityDefOf.RP_Guard, canteenSpots, 6);
                    Pawn boss = Rioter(canteenSpots, 0), second = Rioter(canteenSpots, 2), third = Rioter(canteenSpots, 3);
                    Pawn officer = Staff(RPSecurityDefOf.RP_Guard);
                    Pawn psych = Staff(DefDatabase<StaffRoleDef>.GetNamed("RP_Psychologist"));
                    if (boss == null || second == null || third == null) Fail("could not run (no three violent-capable inmates for the riot that takes a hostage)");
                    else
                    {
                        int crises0 = st.hostageCrises, riots0 = st.riots;
                        CompInmate bc = boss.Inmate();
                        List<Letter> lettersBefore = Find.LetterStack.LettersListForReading.ToList();
                        bool riot = RPRiotDefOf.RP_Riot.Worker.TryExecute(boss, bc);
                        HostageCrisis.ForcedRoll = null;
                        StoryArc arc = HostageCrisis.Running();
                        if (arc != null) started.Add(arc);
                        List<ChoiceLetter_PrisonDecision> asked = Find.LetterStack.LettersListForReading.Where(l => !lettersBefore.Contains(l)).OfType<ChoiceLetter_PrisonDecision>().ToList();
                        // The nearest member of staff (nobody is hated): the guard next to them, not the insider further off.
                        bool seized = riot && arc != null && arc.A == guard && arc.stage == HostageCrisis.Standoff && arc.B == boss && arc.pawns.Count >= 3 && HostageNote.Of(arc).trigger == HostageCrisis.Riot;
                        bool hostageState = HostageCrisis.IsHeld(guard) && guard.MentalStateDef == RPHostageDefOf.RP_BeingHeld && guard.MentalState.causedByPawn == boss;
                        bool onlyOneLetter = arc != null && Letters(arc) == 1 && !asked.Any(l => l.Is("RP_Decision_RiotNegotiation")) && st.hostageCrises == crises0 + 1 && st.riots == riots0 + 1;
                        bool ringleader = boss.MentalStateDef == RPMentalStateDefOf.RP_Rioting && second.MentalStateDef == RPMentalStateDefOf.RP_Rioting;
                        results.Add($"riot seized {seized}/held {hostageState}/one letter {onlyOneLetter}/rioting {ringleader}");
                        if (!seized || !hostageState || !onlyOneLetter || !ringleader)
                            Fail($"a riot with a ringleader next to a guard did not become a stand-off (seized {seized} arc {arc != null} stage {arc?.stage} cast {arc?.pawns.Count}, hostage kept {hostageState}, one decision letter and no negotiation letter {onlyOneLetter}, rioting {ringleader})");
                        if (arc != null && arc.A == guard)
                        {
                            // The letter says what the options are, the milestone has the officer's name.
                            IReadOnlyList<string> options = arc.def.Worker.Options(arc);
                            if (options == null || options.Count != 5 || arc.def.Worker.TimeoutChoice(arc) != HostageArc.WaitOption || options[HostageArc.WaitOption] != "RP_Hostage_Wait")
                                Fail($"the stand-off's options are not talk, give in, cut, storm, wait with waiting as the timeout answer ({string.Join(",", options ?? new string[0])})");
                            ChronicleEntry milestone = r.NewEntries.FirstOrDefault(e => e.importance == 3 && e.text.Contains(guard.LabelShort) && e.text.Contains(boss.LabelShort) && e.ids.Contains(guard.thingIDNumber));
                            if (milestone == null) Fail("the chronicle has no milestone naming the officer and the ringleader for the seizure");
                            if (GuardUtility.IsGuard(guard)) Fail("a held guard still counts as a guard on duty");
                            // The held job: within a second or so of game time the think tree gives the hostage the held job (the staff they were doing nothing else).
                            for (int tick = 0; tick < 60 && guard.CurJobDef != RPHostageDefOf.RP_HeldHostage; tick++) Find.TickManager.DoSingleTick();
                            if (guard.CurJobDef != RPHostageDefOf.RP_HeldHostage) Fail($"the hostage was not given the held job (job {guard.CurJobDef?.defName})");
                            // The room is held: border doors refuse staff, inside or out (staff inside are let out by a job), the held cells are the zone.
                            pm.ForceDoorUpdate();
                            List<CompPrisonDoor> heldDoors = pm.Doors.Where(d => d.heldCells != null).ToList();
                            bool zone = pm.heldCells != null && pm.heldCells.Contains(map.cellIndices.CellToIndex(guard.Position)) && pm.heldCells.Count == canteen.CellCount;
                            bool borders = heldDoors.Count >= 1 && heldDoors.All(d => GenAdj.CardinalDirections.Any(dir => (d.parent.Position + dir).GetRoom(map) == canteen));
                            bool outsideRefused = heldDoors.Count > 0 && heldDoors.All(d => !d.Door.PawnCanOpen(officer)), insideRefused = heldDoors.Count > 0 && heldDoors.All(d => !d.Door.PawnCanOpen(insider));
                            results.Add($"held cells {pm.heldCells?.Count}/{canteen.CellCount}/border doors {heldDoors.Count}/outside refused {outsideRefused}/inside refused {insideRefused}");
                            if (!zone || !borders || !outsideRefused || !insideRefused)
                                Fail($"the held room's doors and zone are wrong (zone {zone} {pm.heldCells?.Count} of {canteen.CellCount} cells, border doors only around the canteen {borders} ({heldDoors.Count}), staff outside refused {outsideRefused}, staff inside refused too (the verdict does not depend on where they stand; a job lets them out) {insideRefused})");
                            // The hostage does not go home when their shift ends; somebody else off shift does.
                            int hour = GenLocalDate.HourOfDay(map);
                            TimeAssignmentDef was = guard.timetable.GetAssignment(hour), insiderWas = insider.timetable.GetAssignment(hour);
                            guard.timetable.SetAssignment(hour, TimeAssignmentDefOf.Sleep);
                            insider.timetable.SetAssignment(hour, TimeAssignmentDefOf.Sleep);
                            bool homeHeld = StaffCommute.ShouldBeHome(guard, guard.GetComp<CompStaff>(), hour), homeOther = StaffCommute.ShouldBeHome(insider, insider.GetComp<CompStaff>(), hour);
                            guard.timetable.SetAssignment(hour, was);
                            insider.timetable.SetAssignment(hour, insiderWas);
                            if (homeHeld || !homeOther) Fail($"the held guard's shift end sends them home (held {homeHeld}, control {homeOther}, must be false/true)");
                            // Talk: the odds with an officer the ringleader trusts, a psychologist, and neither; the roll is seeded.
                            float alone = HostageArc.TalkChance(arc, map, out Pawn nobody);
                            bc.staffMemories.Clear();
                            StaffMemories.Note(bc, officer, 60f);
                            float withOfficer = HostageArc.TalkChance(arc, map, out Pawn negotiator);
                            if (nobody != psych || negotiator != officer || withOfficer < alone + 0.25f)
                                Fail($"talking odds: alone {alone:F2} with the psychologist as the one who talks ({nobody == psych}), with a trusted officer {withOfficer:F2} (he talks: {negotiator == officer}); the officer must add 0.25 or more");
                            SecurityLevelDef before = bc.security;
                            SecurityLevelDef expectedWing = before?.demoteTo is { } up && Unlocks.IsUnlocked(up) && InmateUtility.HasRoomIn(boss, up) ? up : before;
                            int freed0 = st.hostagesFreed;
                            int seedTalk = Seed(() => Rand.Chance(withOfficer));
                            if (Answer(arc, 0, "talk with a trusted officer", seedTalk))
                            {
                                string expected = Line("RP_Chr_HostageEnd_Talked", guard, boss, officer.LabelShort);
                                CompStaff gsc = guard.GetComp<CompStaff>();
                                bool released = arc.done && Freed(guard) && !second.InMentalState && !boss.InMentalState && st.hostagesFreed == freed0 + 1;
                                bool memories = Has(guard, RPHostageDefOf.RP_TakenHostage) && Has(officer, RPLifeDefOf.RP_ColleagueHurt) && StaffWariness.WaryOf(gsc, boss) && StaffCareers.ReasonFor(guard, gsc) == ResignReason.Danger;
                                bool charged = bc.records.Any(rec => rec.def == RPHostageDefOf.RP_HostageTaking) && bc.crime?.defName == "RP_Crime_Kidnapping" && bc.sentenceDays < 0 && bc.security == expectedWing
                                               && (bc.pendingPunishmentHours > 0 || bc.forcedActivity != null || bc.yardBanUntilTick > Now() || bc.shopBanUntilTick > Now() || bc.visitBanUntilTick > Now());
                                bool told = Told(expected) && r.NewEntries.Any(e => e.importance == 3 && e.text == expected && e.ids.Contains(guard.thingIDNumber));
                                bool letter = r.NewLetters.Any(l => string.Equals(l.Label.ToString(), "RP_HostageFreedLabel".Translate(guard.LabelShort).ToString(), StringComparison.OrdinalIgnoreCase));
                                pm.ForceDoorUpdate();
                                bool opened = pm.heldCells == null && pm.Doors.All(d => d.heldCells == null) && GuardUtility.IsGuard(guard);
                                results.Add($"talk released {released}/memories {memories}/charged {charged}/told {told}/doors open {opened}");
                                if (!released || !memories || !charged || !told || !letter || !opened)
                                    Fail($"a trusted officer's talk did not end it as it should (released {released}, hostage memory + colleague memory + wary + danger {memories}, ringleader charged and sanctioned {charged}, line {told}, letter {letter}, doors open and back on duty {opened})");
                            }
                        }
                    }
                    HostageCrisis.ForcedRoll = null;
                }

                // ---- failed talks, then the grievance: a promise; the ringleader becomes the one it was made to ---------
                {
                    StoryArc arc = Begin(out Pawn guard, out Pawn boss);
                    if (arc != null)
                    {
                        float chance = HostageArc.TalkChance(arc, map, out _);
                        int tension0 = (int)boss.Inmate().tension;
                        int lose = Seed(() => !Rand.Chance(chance));
                        Answer(arc, 0, "a talk that fails", lose);
                        bool again = !arc.done && arc.count == 1 && Letters(arc) == 1 && HostageCrisis.IsHeld(guard) && boss.Inmate().tension > tension0 && worker.Disabled(arc, 0) == null;
                        float worse = HostageArc.TalkChance(arc, map, out _);
                        lose = Seed(() => !Rand.Chance(worse));
                        Answer(arc, 0, "a second talk that fails", lose);
                        bool spent = !arc.done && arc.count == 2 && worker.Disabled(arc, 0) != null && HostageArc.TalkChance(arc, map, out _) < chance;
                        NeedDef grievance = HostageNote.Of(arc).need;
                        float rep0 = g.reputation;
                        int negotiated0 = st.riotsNegotiated;
                        Pawn riotOf = HostageCrisis.Leader(arc);
                        bool conceded = Answer(arc, 1, "give them what they want");
                        bool promise = conceded && arc.done && g.demand.Active && g.demand.need == grievance && g.demand.spokesId == riotOf.thingIDNumber && g.reputation < rep0 && st.riotsNegotiated == negotiated0 + 1;
                        bool calm = Freed(guard) && !boss.InMentalState && Told(Line("RP_Chr_HostageEnd_Conceded", guard, boss, grievance?.label ?? ""));
                        results.Add($"failed talks {again}/{spent}, conceded promise {promise}/calm {calm}");
                        if (!again || !spent) Fail($"failed talks did not leave it going with a worse chance and a new letter (again {again}, spent after two {spent})");
                        if (!promise || !calm) Fail($"giving the grievance did not make a promise to the ringleader, free the hostage and calm the rioters (promise {promise}, free and calm {calm})");
                    }
                }

                // ---- cut power and water, then storm it: the riot police spawn and the arc ends ----------------------------
                {
                    StoryArc arc = Begin(out Pawn guard, out Pawn boss);
                    if (arc != null)
                    {
                        pm.ForceDoorUpdate();
                        Building powered = pm.heldCells == null ? null : map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.GetComp<CompPowerTrader>() is { Props.PowerConsumption: > 0f } && pm.heldCells.Contains(map.cellIndices.CellToIndex(b.Position)));
                        Answer(arc, 2, "cut power and water");
                        pm.ForceDoorUpdate();
                        UtilitySystem.Tick(map);
                        boss.needs.food.CurLevel = 0.9f * boss.needs.food.MaxLevel;
                        float food0 = boss.needs.food.CurLevel;
                        worker.Hourly(arc, map);
                        bool cut = !arc.done && arc.markTick != HostageCrisis.NoMark && worker.Disabled(arc, 2) != null && pm.heldPowerCut && Letters(arc) == 1;
                        bool dark = powered != null && (!UtilitySystem.Active || !powered.GetComp<CompPowerTrader>().PowerOn);
                        bool hungrier = boss.needs.food.CurLevel < food0;
                        if (powered == null) Fail("could not check the dark rooms (nothing in the canteen draws power)");
                        results.Add($"cut {cut}/dark {dark}/hungrier {hungrier}");
                        if (!cut || powered != null && !dark || !hungrier) Fail($"cutting power and water did not do it (the arc marked and the option spent {cut}, the lamp is dark {dark}, the rioters hungrier {hungrier})");
                        // Storm it: no money, no storm; then with the money and a roll that frees the hostage and hurts nobody.
                        g.ledger.funds = 0;
                        bool broke = worker.Disabled(arc, 3) != null;
                        g.ledger.funds = RiotPolice.Cost * 3;
                        int calls0 = st.policeCallouts;
                        int policeBefore = map.mapPawns.FreeColonistsSpawned.Count(p => p.GetComp<CompStaff>()?.role?.defName == "RP_RiotPolice");
                        int seedStorm = Seed(() =>
                        {
                            (float o, float c) = HostageCrisis.StormRolls();
                            return HostageCrisis.StormFate(arc, o) == HostageFate.Freed && c >= HostageCrisis.StormCasualty;
                        });
                        bool stormed = Answer(arc, 3, "storm it", seedStorm);
                        List<Pawn> squad = map.mapPawns.FreeColonistsSpawned.Where(p => p.GetComp<CompStaff>()?.role?.defName == "RP_RiotPolice").ToList();
                        bool police = st.policeCallouts == calls0 + 1 && squad.Count == policeBefore + RiotPolice.SquadSize;
                        bool over = stormed && arc.done && Freed(guard) && !boss.InMentalState && !boss.HostileTo(guard) && Told(Line("RP_Chr_HostageEnd_Stormed_Freed", guard, boss));
                        pm.ForceDoorUpdate();
                        UtilitySystem.Tick(map);
                        bool lit = powered == null || powered.GetComp<CompPowerTrader>().PowerOn;
                        results.Add($"storm broke {broke}/police {police}/over {over}/lights back {lit}");
                        if (!broke || !police || !over || !lit)
                            Fail($"storming it did not work (refused without the money {broke}, the squad of {RiotPolice.SquadSize} arrived {police} ({squad.Count - policeBefore}), the arc ended with the hostage free and the rioters stood down, not hostile to them {over}, power back {lit})");
                        foreach (Pawn officerOfSquad in squad) officerOfSquad.Destroy(DestroyMode.Vanish);
                        boss.MentalState?.RecoverFromState();
                    }
                }

                // ---- the riot's own letter answered while a hostage is held, and the riot police called from the prison pane --------------------------
                {
                    StoryArc arc = Begin(out Pawn guard, out Pawn boss);
                    if (arc != null)
                    {
                        NeedDef need = HostageNote.Of(arc).need;
                        ChoiceLetter_PrisonDecision riotLetter = ChoiceLetter_PrisonDecision.Make("RP_Decision_RiotNegotiation", "riot", "riot", map);
                        riotLetter.need = need;
                        riotLetter.amount = boss.thingIDNumber;
                        riotLetter.pawns = arc.pawns.Skip(1).ToList();
                        RiotNegotiation.Negotiate(map, riotLetter);
                        bool deal = arc.done && Freed(guard) && g.demand.Active && g.demand.need == need && g.demand.spokesId == boss.thingIDNumber && Told(Line("RP_Chr_HostageEnd_Conceded", guard, boss, need?.label ?? "?"));
                        results.Add($"riot deal covers the hostage {deal}");
                        if (!deal) Fail($"negotiating the riot while a hostage was held did not let the hostage go (done {arc.done}, free {Freed(guard)}, promise {g.demand.Active})");
                    }
                    arc = Begin(out guard, out boss);
                    if (arc != null)
                    {
                        g.ledger.funds = RiotPolice.Cost * 3;
                        int calls0 = st.policeCallouts;
                        int seedCall = Seed(() =>
                        {
                            (float o, float c) = HostageCrisis.StormRolls();
                            return HostageCrisis.StormFate(arc, o) == HostageFate.Freed && c >= HostageCrisis.StormCasualty;
                        });
                        if (seedCall < 0) Fail("no seed frees the hostage in a storm called from the prison pane");
                        else
                        {
                            Rand.PushState(seedCall);
                            try { RiotPolice.Call(map); }
                            finally { Rand.PopState(); }
                        }
                        List<Pawn> squad = map.mapPawns.FreeColonistsSpawned.Where(p => p.GetComp<CompStaff>()?.role?.defName == "RP_RiotPolice").ToList();
                        bool stormed = arc.done && Freed(guard) && st.policeCallouts == calls0 + 1 && Told(Line("RP_Chr_HostageEnd_Stormed_Freed", guard, boss));
                        results.Add($"police called in a crisis storms it {stormed}");
                        if (!stormed) Fail($"calling the riot police during a hostage crisis did not storm it (done {arc.done}, free {Freed(guard)}, callouts +{st.policeCallouts - calls0})");
                        foreach (Pawn officerOfSquad in squad) officerOfSquad.Destroy(DestroyMode.Vanish);
                        boss.MentalState?.RecoverFromState();
                    }
                }

                // ---- a storm that kills the hostage and a rioter: the murder charge, an inquiry ---------------------------
                {
                    StoryArc arc = Begin(out Pawn guard, out Pawn boss, followers: 1);
                    if (arc != null)
                    {
                        Pawn follower = arc.pawns.Count > 2 ? arc.pawns[2] : null;
                        g.ledger.funds = RiotPolice.Cost * 3;
                        int deaths0 = st.staffDeaths;
                        int seedStorm = Seed(() =>
                        {
                            (float o, float c) = HostageCrisis.StormRolls();
                            return HostageCrisis.StormFate(arc, o) == HostageFate.Dead && c < HostageCrisis.StormCasualty;
                        });
                        bool stormed = Answer(arc, 3, "a storm that goes wrong", seedStorm);
                        List<Pawn> squad = map.mapPawns.FreeColonistsSpawned.Where(p => p.GetComp<CompStaff>()?.role?.defName == "RP_RiotPolice").ToList();
                        BodyLeft(guard, follower);
                        bool dead = stormed && guard.Dead && follower != null && follower.Dead && arc.done && !boss.Dead && st.staffDeaths == deaths0 + 1;
                        bool charged = boss.Inmate().records.Any(rec => rec.def == RPHostageDefOf.RP_HostageTaking) && boss.Inmate().records.Any(rec => rec.def == RPDeathDefOf.RP_Murder) && boss.Inmate().crime?.defName == "RP_Crime_CapitalMurder";
                        bool inquiry = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_Inquiry"));
                        bool told = Told(Line("RP_Chr_HostageEnd_Stormed_Dead", guard, boss));
                        results.Add($"storm killed hostage {guard.Dead}/rioter {follower?.Dead}/charged {charged}/inquiry {inquiry}/told {told}");
                        if (!dead || !charged || !inquiry || !told)
                            Fail($"a storm that goes wrong did not end as it should (hostage and a rioter dead, one staff death, the ringleader alive {dead}, a capital charge {charged}, an inquiry letter {inquiry}, the line {told})");
                        foreach (Pawn officerOfSquad in squad) officerOfSquad.Destroy(DestroyMode.Vanish);
                        boss.MentalState?.RecoverFromState();
                    }
                }

                // ---- the deadline's blows land whatever the uniform's armour (the staff's synthread turns or halves some, and now and then both) ----------
                {
                    Pawn guard = Staff(RPSecurityDefOf.RP_Guard, canteenSpots, 1);
                    // (Whole: a guard generated with a peg leg took a blow on the leg as a crack the leg's few hit points cap - 4.4 of a blow of 5 or more - and the sum fell short with no armour in it.)
                    guard.health.Reset();
                    int weak = 0;
                    string weakSample = null;
                    for (int blows = 0; blows < 300; blows++)
                    {
                        // (Three hundred blows in no time at all: a guard the pain puts on the floor starts a job for it at every blow, and vanilla stops a pawn that is given more than ten
                        // in one tick. The count is for ticks that pass, so it is cleared by hand.)
                        Traverse.Create(guard.jobs).Field("jobsGivenThisTick").SetValue(0);
                        HostageCrisis.Hurt(guard, null);
                        // (Two blows of 5 to 8 at full strength: no less than 10 in all.)
                        if (guard.health.hediffSet.hediffs.OfType<Hediff_Injury>().Sum(wound => wound.Severity) < 9.99f * guard.GetStatValue(StatDefOf.IncomingDamageFactor))
                        {
                            weak++;
                            weakSample ??= $"the first: {string.Join(", ", guard.health.hediffSet.hediffs.Select(h => $"{h.def.defName} {h.Part?.def.defName} {h.Severity:F1}"))}, incoming damage x{guard.GetStatValue(StatDefOf.IncomingDamageFactor):F2}, wearing {string.Join("/", guard.apparel.WornApparel.Select(a => a.def.defName))}, missing parts {guard.health.hediffSet.GetMissingPartsCommonAncestors().Count}";
                        }
                        foreach (Hediff_Injury wound in guard.health.hediffSet.hediffs.OfType<Hediff_Injury>().ToList()) guard.health.RemoveHediff(wound);
                        // (Armour that turns a blow is worn by it: the same uniform every time.)
                        foreach (Apparel worn in guard.apparel.WornApparel) worn.HitPoints = worn.MaxHitPoints;
                    }
                    results.Add($"blows land {weak == 0}");
                    if (weak > 0) Fail($"the deadline's blows were turned or halved by the guard's uniform {weak} times in 300 (they have to land: the letter says the hostage is hurt); {weakSample}");
                }

                // ---- the deadline: the hostage is hurt, then killed; or the hostage-takers lose their nerve ------------------
                {
                    StoryArc arc = Begin(out Pawn guard, out Pawn boss);
                    if (arc != null)
                    {
                        arc.nextTick = Now() - 1;
                        worker.Hourly(arc, map);
                        bool hurt = !arc.done && arc.stage == HostageCrisis.Deadline && guard.health.hediffSet.hediffs.Any(h => h is Hediff_Injury) && arc.nextTick > Now() && Letters(arc) == 1 && !guard.Dead
                                    && Told("RP_Chr_HostageHurt".Translate(guard.LabelShort, boss.LabelShort).Resolve());
                        float kill = HostageCrisis.KillChance(arc);
                        arc.nextTick = Now() - 1;
                        int deaths0 = st.staffDeaths;
                        int seedKill = Seed(() => Rand.Chance(kill));
                        if (seedKill < 0) Fail("no seed kills the hostage at the last deadline");
                        else
                        {
                            Rand.PushState(seedKill);
                            try { worker.Hourly(arc, map); }
                            finally { Rand.PopState(); }
                        }
                        BodyLeft(guard);
                        bool killed = arc.done && guard.Dead && st.staffDeaths == deaths0 + 1 && Told(Line("RP_Chr_HostageEnd_Deadline", guard, boss))
                                      && boss.Inmate().records.Any(rec => rec.def == RPDeathDefOf.RP_Murder) && boss.Inmate().crime?.defName == "RP_Crime_CapitalMurder";
                        // Nothing in all this ends the game (vanilla's check, with staff on the map and at home).
                        Find.GameEnder.gameEnding = false;
                        Find.GameEnder.CheckOrUpdateGameOver();
                        bool gameGoes = !Find.GameEnder.gameEnding;
                        results.Add($"deadline hurt {hurt}/killed {killed}/game goes on {gameGoes}");
                        if (!hurt) Fail($"the first deadline did not hurt the hostage and ask again (hurt {hurt}, stage {arc.stage})");
                        if (!killed || !gameGoes) Fail($"the last deadline did not kill the hostage through the staff death path (killed {killed}, game goes on {gameGoes})");
                        boss.MentalState?.RecoverFromState();
                    }
                    // ...or they lose their nerve at the last deadline: let go, hurt, remembered.
                    arc = Begin(out guard, out boss);
                    if (arc != null)
                    {
                        arc.nextTick = Now() - 1;
                        worker.Hourly(arc, map);
                        float kill = HostageCrisis.KillChance(arc);
                        arc.nextTick = Now() - 1;
                        int seedSpare = Seed(() => !Rand.Chance(kill));
                        if (seedSpare < 0) Fail("no seed spares the hostage at the last deadline");
                        else
                        {
                            Rand.PushState(seedSpare);
                            try { worker.Hourly(arc, map); }
                            finally { Rand.PopState(); }
                        }
                        bool spared = arc.done && Freed(guard) && Told(Line("RP_Chr_HostageEnd_GaveUp", guard, boss)) && Has(guard, RPHostageDefOf.RP_TakenHostage) && guard.health.hediffSet.hediffs.Any(h => h is Hediff_Injury) && !boss.InMentalState;
                        results.Add($"deadline spared {spared}");
                        if (!spared) Fail("losing their nerve at the last deadline did not let the hostage go (hurt and remembered) and calm the rioters");
                    }
                }

                // ---- nobody beats the hostage; one who dies anyway is a death in the crisis, not a quiet end ---------------------------------
                {
                    // Rioting inmates are not hostile to the held guard (vanilla's pather melees whoever hostile stands in the way: that is how they beat one
                    // to death in seconds), and a melee job against the hostage is refused whoever starts it.
                    StoryArc arc = Begin(out Pawn guard, out Pawn boss, followers: 1);
                    if (arc != null && arc.pawns.Count > 2)
                    {
                        Pawn taker = arc.pawns[2];
                        bool calm = !boss.HostileTo(guard) && !taker.HostileTo(guard) && !guard.HostileTo(taker) && !guard.HostileTo(boss);
                        bool stillHostile = taker.HostileTo(Faction.OfPlayer);
                        taker.jobs.StopAll();
                        taker.jobs.StartJob(JobMaker.MakeJob(JobDefOf.AttackMelee, guard), JobCondition.InterruptForced);
                        bool refused = taker.CurJob?.def != JobDefOf.AttackMelee || taker.CurJob.targetA.Thing != guard;
                        taker.jobs.StopAll();
                        results.Add($"hostage not hostile {calm}/riot still hostile to the prison {stillHostile}/melee refused {refused}");
                        if (!calm || !stillHostile || !refused)
                            Fail($"the rioters are hostile to, or can start a fight with, the held hostage (not hostile either way {calm}, still hostile to the rest of the prison {stillHostile}, melee refused {refused})");
                        // A taker kills them in a fight, outside the deadline: the killed ending, the staff death path, the charges (not "the stand-off ended").
                        int deathsBefore = st.staffDeaths, killedBefore = st.hostagesKilled;
                        guard.Kill(new DamageInfo(DamageDefOf.Blunt, 200f, 999f, -1f, taker));
                        BodyLeft(guard);
                        bool fought = arc.done && arc.stage == HostageCrisis.Resolved && guard.Dead && st.staffDeaths == deathsBefore + 1 && st.hostagesKilled == killedBefore + 1;
                        bool told = Told(Line("RP_Chr_HostageEnd_Killed", guard, boss, taker.LabelShort)) && !Told(Line("RP_Chr_HostageEnd_Gone", guard, boss));
                        bool chargedBoth = boss.Inmate().records.Any(rec => rec.def == RPHostageDefOf.RP_HostageTaking) && boss.Inmate().crime?.defName == "RP_Crime_CapitalMurder"
                                           && taker.Inmate().records.Any(rec => rec.def == RPDeathDefOf.RP_Murder);
                        bool letters = r.NewLetters.Any(l => string.Equals(l.Label.ToString(), "RP_HostageLostLabel".Translate(guard.LabelShort).ToString(), StringComparison.OrdinalIgnoreCase));
                        results.Add($"killed in a fight: ended {fought}/told {told}/charged {chargedBoth}/letter {letters}");
                        if (!fought || !told || !chargedBoth || !letters)
                            Fail($"a hostage killed by a taker outside the deadline did not end the crisis as a death (ended with one staff death and one hostage killed {fought}, the killed line and not the quiet one {told}, the ringleader and the killer charged {chargedBoth}, the warden's letter {letters})");
                        boss.MentalState?.RecoverFromState();
                        taker.MentalState?.RecoverFromState();
                    }
                    // ...or they die of a wound, nobody's blow: still a death, with its own line.
                    arc = Begin(out guard, out boss);
                    if (arc != null)
                    {
                        int killedBefore = st.hostagesKilled;
                        guard.Kill(null);
                        BodyLeft(guard);
                        bool died = arc.done && guard.Dead && st.hostagesKilled == killedBefore + 1 && Told(Line("RP_Chr_HostageEnd_Died", guard, boss)) && !Told(Line("RP_Chr_HostageEnd_Gone", guard, boss));
                        // (And the hourly pass would have let a dead hostage's arc run to the same ending: a killed pawn is destroyed, which once made the arc invalid.)
                        bool valid = worker.Valid(arc);
                        results.Add($"died of a wound {died}/dead hostage keeps the arc valid {valid}");
                        if (!died || !valid) Fail($"a hostage who died with nobody's blow behind it did not end the crisis as a death (ended as one {died}, the arc still valid for the hourly pass {valid})");
                        boss.MentalState?.RecoverFromState();
                    }
                }

                // ---- a ringleader who gets at a guard in the fight (the guards come for the rioters): the same stand-off ------------------------
                {
                    g.story.lastArcStart.Remove(arcDef);
                    foreach (StoryArc running in g.story.arcs.Where(a => !a.done && a.def == arcDef).ToList())
                    {
                        running.done = true;
                        HostageCrisis.Release(running.A);
                    }
                    ClearStaff();
                    Pawn guard = Staff(RPSecurityDefOf.RP_Guard, workshopSpots, 1);
                    Pawn boss = Rioter(workshopSpots, 0), follower = Rioter(workshopSpots, 2);
                    if (boss == null || follower == null) Fail("could not run (no inmates for a riot's fight)");
                    else
                    {
                        // The rioter goes for the nearest member of staff within 14 cells (`JobGiver_Riot`), and the workshop is where the prison's own workmen stand: one at the
                        // guard's distance is the one they go for, and is out of reach of a seizure (set out of reach above). The real staff nearby are set down at the canteen for the check.
                        foreach (Pawn real in map.mapPawns.FreeColonistsSpawned.Where(x => x != guard && (x.Position - boss.Position).LengthHorizontalSquared < 15 * 15).ToList())
                        {
                            IntVec3 was = real.Position;
                            real.pather?.StopDead();
                            real.Position = CellFinder.RandomClosewalkCellNear(canteenCell, map, 5);
                            real.Notify_Teleported();
                            undo.Add(() =>
                            {
                                if (!real.Spawned) return;
                                real.pather?.StopDead();
                                real.Position = was;
                                real.Notify_Teleported();
                            });
                        }
                        foreach (Pawn p in new[] { boss, follower }) p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
                        var giver = new JobGiver_Riot();
                        // Not the ringleader (a follower, with no record of starting it): they only fight.
                        HostageCrisis.ForcedRoll = true;
                        giver.TryIssueJobPackage(follower, default);
                        bool followerFights = HostageCrisis.Running() == null;
                        // The ringleader, the dice saying no, then yes.
                        boss.Inmate().AddRecord(new MisconductRecord { def = RPRiotDefOf.RP_Riot, tick = Now(), seen = true });
                        HostageCrisis.ForcedRoll = false;
                        giver.TryIssueJobPackage(boss, default);
                        bool diceNo = HostageCrisis.Running() == null;
                        HostageCrisis.ForcedRoll = true;
                        // (What the giver sees, for the failure text: the nearest member of staff to the ringleader, and whether the guard could be taken.)
                        Pawn nearestStaff = map.mapPawns.FreeColonistsSpawned.Where(s => !s.Downed).OrderBy(s => (s.Position - boss.Position).LengthHorizontalSquared).FirstOrDefault();
                        string sees = $"nearest {nearestStaff?.LabelShort} {(nearestStaff == null ? 0f : (nearestStaff.Position - boss.Position).LengthHorizontal):0.0} cells away{(nearestStaff == guard ? " (the guard)" : "")}; "
                                      + $"guard can be taken {HostageCrisis.CanBeTaken(guard)} (spawned {guard.Spawned}, leaves {guard.GetComp<CompStaff>()?.leaveAtTick}, on leave {StaffLives.OnLeave(guard.GetComp<CompStaff>())}, mental {guard.InMentalState}, in an arc {g.story.InArc(guard)}), "
                                      + $"candidate {HostageCrisis.Candidate(boss, boss.Inmate(), guard)?.LabelShort ?? "none"}, boss rioting {boss.MentalStateDef?.defName}, ringleader {HostageCrisis.IsRingleader(boss)}, "
                                      + $"in room {boss.GetRoom() == guard.GetRoom()}, hold room {HostageCrisis.HoldRoom(boss.Position, map) != null}, a crisis running {HostageCrisis.Running() != null}";
                        giver.TryIssueJobPackage(boss, default);
                        HostageCrisis.ForcedRoll = null;
                        StoryArc arc = HostageCrisis.Running();
                        if (arc != null) started.Add(arc);
                        bool taken = arc != null && arc.A == guard && arc.B == boss && arc.pawns.Contains(follower) && HostageCrisis.IsHeld(guard);
                        results.Add($"fight: follower only fights {followerFights}/dice no {diceNo}/ringleader took the guard {taken}");
                        if (!followerFights || !diceNo || !taken)
                            Fail($"a ringleader getting at a guard did not take them, or a follower or the dice did (follower alone started one {!followerFights}, dice said no and one started {!diceNo}, the ringleader seized the guard with the roll forced {taken}; {sees})");
                        if (arc != null) worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                        foreach (Pawn p in new[] { boss, follower }) p.MentalState?.RecoverFromState();
                    }
                }

                // ---- the hostage-takers give up, or leave: a follower takes over; nobody left ends it -----------------------
                {
                    StoryArc arc = Begin(out Pawn guard, out Pawn boss, followers: 2);
                    if (arc != null)
                    {
                        Pawn second = arc.pawns[2], third = arc.pawns[3];
                        g.story.Notify_Leaving(boss);
                        bool stoodIn = !arc.done && HostageCrisis.Leader(arc) == second && arc.pawns.Count == 3 && !arc.pawns.Contains(boss);
                        g.story.Notify_Leaving(second);
                        bool again = !arc.done && HostageCrisis.Leader(arc) == third;
                        // The last of them stops: that is giving up.
                        third.MentalState?.RecoverFromState();
                        worker.Hourly(arc, map);
                        bool gaveUp = arc.done && Freed(guard) && Told(Line("RP_Chr_HostageEnd_GaveUp", guard, third)) && !boss.Inmate().records.Any(rec => rec.def == RPHostageDefOf.RP_HostageTaking);
                        results.Add($"leaders leave {stoodIn}/{again}, gave up {gaveUp}");
                        if (!stoodIn || !again || !gaveUp) Fail($"hostage-takers leaving or giving up did not play out (a follower takes over {stoodIn}/{again}, the arc ended with the last giving up and nobody charged who was not there {gaveUp})");
                        boss.MentalState?.RecoverFromState();
                    }
                    // A hostage let go by somebody else (fired, gone) ends it without the takers' consequences.
                    arc = Begin(out guard, out boss);
                    if (arc != null)
                    {
                        string line = Line("RP_Chr_HostageEnd_Gone", guard, boss);
                        g.story.Notify_Leaving(guard);
                        bool gone = arc.done && Freed(guard) && Told(line);
                        results.Add($"hostage gone {gone}");
                        if (!gone) Fail("the hostage leaving did not end the crisis quietly");
                        boss.MentalState?.RecoverFromState();
                    }
                }

                // ---- the wing: five rioters hold the canteen and the corridor it opens on (painted as one zone) ---------------
                {
                    var painted = corridor.Cells.Select(c => (c, was: pm.zones.ZoneAt(c))).ToList();
                    undo.Add(() =>
                    {
                        foreach ((IntVec3 c, PrisonZoneDef was) in painted) pm.zones.Set(c, was);
                    });
                    PrisonZoneDef canteenZone = pm.zones.ZoneAt(canteenCell);
                    foreach ((IntVec3 c, _) in painted) pm.zones.Set(c, canteenZone);
                    StoryArc arc = Begin(out Pawn guard, out Pawn boss, followers: HostageCrisis.WingTakers - 1);
                    if (arc != null)
                    {
                        pm.ForceDoorUpdate();
                        HostageNote note = HostageNote.Of(arc);
                        HashSet<Room> rooms = HostageHold.Rooms(map);
                        int doors = pm.Doors.Count(d => d.heldCells != null);
                        bool wing = note.wing && rooms != null && rooms.Contains(canteen) && rooms.Contains(corridor) && pm.heldCells != null && pm.heldCells.Contains(map.cellIndices.CellToIndex(painted[0].c)) && doors > 3;
                        results.Add($"wing {wing}/rooms {rooms?.Count}/doors {doors}");
                        if (!wing) Fail($"five rioters did not hold the wing (note says wing {note.wing}, rooms {rooms?.Count}, border doors {doors} (more than the canteen's three), corridor held {rooms?.Contains(corridor)})");
                        worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                        pm.ForceDoorUpdate();
                        if (pm.heldCells != null || pm.Doors.Any(d => d.heldCells != null)) Fail("the wing stayed held after the crisis ended");
                    }
                }

                // ---- an assault in the workshop (the roll forced) takes the target; nobody but a staff member in the workshop or the kitchen starts one ---------
                {
                    g.story.lastArcStart.Remove(arcDef);
                    foreach (StoryArc running in g.story.arcs.Where(a => !a.done && a.def == arcDef).ToList())
                    {
                        running.done = true;
                        HostageCrisis.Release(running.A);
                    }
                    ClearStaff();
                    Pawn guard = Staff(RPSecurityDefOf.RP_Guard, workshopSpots, 1);
                    Pawn attacker = Rioter(workshopSpots, 0);
                    if (attacker == null) Fail("could not run (no violent-capable inmate for the assault)");
                    else
                    {
                        HostageCrisis.ForcedRoll = true;
                        bool assaulted = AssaultMisconduct().Worker.TryExecute(attacker, attacker.Inmate());
                        HostageCrisis.ForcedRoll = null;
                        StoryArc arc = HostageCrisis.Running();
                        if (arc != null) started.Add(arc);
                        pm.ForceDoorUpdate();
                        bool taken = assaulted && arc != null && arc.A == guard && arc.B == attacker && HostageNote.Of(arc).trigger == HostageCrisis.Assault && attacker.MentalStateDef == RPMentalStateDefOf.RP_Rioting
                                     && HostageCrisis.IsHeld(guard) && pm.heldCells != null && pm.heldCells.Contains(map.cellIndices.CellToIndex(guard.Position)) && pm.heldCells.Count == workshop.CellCount;
                        results.Add($"workshop assault took the guard {taken}");
                        if (!taken) Fail($"an assault in the workshop with the roll forced did not seize the nearest staff member and hold the workshop (assault {assaulted}, arc {arc != null}, hostage {arc?.A?.LabelShort}, state {attacker.MentalStateDef?.defName}, held cells {pm.heldCells?.Count} of {workshop.CellCount})");
                        // Outside the workshop and the kitchen the same assault, roll forced, is just an assault.
                        if (arc != null) worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Freed);
                        attacker.MentalState?.RecoverFromState();
                        g.story.lastArcStart.Remove(arcDef);
                        ClearStaff();
                        Pawn bystander = Staff(RPSecurityDefOf.RP_Guard, canteenSpots, 1);
                        Pawn brawler = Rioter(canteenSpots, 0);
                        if (brawler != null)
                        {
                            HostageCrisis.ForcedRoll = true;
                            bool plain = AssaultMisconduct().Worker.TryExecute(brawler, brawler.Inmate());
                            HostageCrisis.ForcedRoll = null;
                            bool nothing = HostageCrisis.Running() == null && !HostageCrisis.IsHeld(bystander);
                            results.Add($"canteen assault stays an assault {plain && nothing}");
                            if (!plain || !nothing) Fail($"an assault in the canteen became a hostage-taking (assault {plain}, no crisis {nothing})");
                            brawler.MentalState?.RecoverFromState();
                        }
                    }
                }

                // ---- nothing stands the hostage up by itself: the sweep lets go of a hostage whose crisis is gone -------------
                {
                    ClearStaff();
                    Pawn stray = Staff(RPSecurityDefOf.RP_Guard, canteenSpots, 1);
                    stray.health.AddHediff(RPHostageDefOf.RP_Hostage);
                    stray.mindState.mentalStateHandler.TryStartMentalState(RPHostageDefOf.RP_BeingHeld, null, forced: true);
                    HostageCrisis.Sweep(map);
                    bool let = Freed(stray);
                    results.Add($"stray hostage let go {let}");
                    if (!let) Fail("a hostage with no crisis behind them was not let go by the hourly sweep");
                }
                RPLog.Message($"[RimPrison:test] hostage: {string.Join("; ", results)}; crises {st.hostageCrises} freed {st.hostagesFreed} killed {st.hostagesKilled}");
            }
            finally
            {
                HostageCrisis.ForcedRoll = null;
                foreach (StoryArc over in started)
                {
                    over.done = true;
                    HostageCrisis.Release(over.A);
                }
                foreach (Pawn p in buried) p.MentalState?.RecoverFromState();
                foreach (Letter extra in r.NewLetters.ToList())
                {
                    if (extra is ChoiceLetter_PrisonDecision decision) decision.answered = true;
                    Find.LetterStack.RemoveLetter(extra);
                }
                BodyLeft(buried.ToArray());
                for (int i = undo.Count - 1; i >= 0; i--) undo[i]();
                (g.demand.need, g.demand.deadlineTick, g.demand.spokesId, g.demand.spokesName, g.demand.cooldownUntilTick) = (need0, deadline0, spokes0, spokesName0, cooldown0);
                (g.story.dramaToday, g.story.beatsToday, g.story.lastRiotTick) = (drama0, beats0, lastRiot0);
                if (hadStart) g.story.lastArcStart[arcDef] = start0;
                else g.story.lastArcStart.Remove(arcDef);
                g.story.arcs.RemoveAll(a => a.def == arcDef && started.Contains(a));
                Find.GameEnder.gameEnding = false;
                pm.ForceDoorUpdate();
                PrisonAlerts.Invalidate();
            }
        }

        private static MisconductDef AssaultMisconduct() => DefDatabase<MisconductDef>.GetNamed("RP_AssaultStaff");

        /// <summary>The dead of a scene are not left lying around.</summary>
        private static void BodyLeft(params Pawn[] dead)
        {
            foreach (Pawn p in dead)
                if (p is { Dead: true } && p.Corpse is { Destroyed: false } corpse) corpse.Destroy();
        }

        // ---- the hostage plan: a crisis nobody answers runs its course in real time ---------------------------------------

        private static bool hostageSoakStarted, hostageSoakAngry, hostageSoakBeaten, hostageSoakEntered;
        private static readonly HashSet<int> hostageSoakInside = new HashSet<int>();
        /// <summary>Staff next to the held cells at the seizure (one step and they are in), and those of them who went in within <see cref="ThresholdGraceTicks"/> and have not come out since.</summary>
        private static readonly HashSet<int> hostageSoakThreshold = new HashSet<int>(), hostageSoakThresholdIn = new HashSet<int>();
        private const int ThresholdGraceTicks = 300;
        private static int hostageSoakArcId = -1, hostageSoakStartTick, hostageSoakHostageId = -1, hostageSoakInjuries;
        private static string hostageSoakHostage;

        /// <summary>
        /// Hostage plan, two hours in: a real guard is seized in the canteen by the most tense inmate, two more inmates rioting beside them,
        /// and nobody answers the letters. Real ticks do the rest: the stand-off, the timeouts, the deadlines, the held doors against the
        /// guards, the hostage hurt, then let go or killed (<see cref="EvaluateHostageSoak"/> looks at how it ended).
        /// </summary>
        private static void StartHostageSoak(Map map)
        {
            hostageSoakStarted = true;
            PrisonGame g = PrisonGame.Instance;
            IntVec3 centre = PrisonBuilder.CanteenCell(map);
            Room canteen = centre.GetRoom(map);
            List<IntVec3> spots = GenRadial.RadialCellsAround(centre, 6f, true).Where(c => c.InBounds(map) && c.Standable(map) && c.GetRoom(map) == canteen && c.GetEdifice(map) == null).ToList();
            Pawn guard = map.mapPawns.FreeColonistsSpawned.Where(x => HostageCrisis.CanBeTaken(x) && GuardUtility.IsGuard(x)).OrderBy(x => x.thingIDNumber).FirstOrDefault();
            List<Pawn> crew = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && !x.Downed && !x.InMentalState && !x.WorkTagIsDisabled(WorkTags.Violent)
                                                                              && !g.story.InArc(x) && x.Inmate() is { } c && DeathRowSystem.Ext(c) == null && x.guest?.Released == false)
                .OrderByDescending(x => x.Inmate().tension).Take(3).ToList();
            if (spots.Count < 4 || guard == null || crew.Count < 3)
            {
                problems0.Add($"hostage plan: could not seed the crisis (canteen spots {spots.Count}, a guard {guard != null}, inmates {crew.Count})");
                return;
            }
            Pawn leader = crew[0];
            leader.Inmate().records.Add(new MisconductRecord { def = RPRiotDefOf.RP_Riot, tick = Find.TickManager.TicksGame, seen = true });
            if (hostageSoakAngry)
                foreach (Pawn p in crew) p.Inmate().tension = 100f;
            for (int i = 0; i < crew.Count; i++)
            {
                Pawn p = crew[i];
                p.pather?.StopDead();
                p.jobs?.StopAll();
                p.Position = spots[i];
                p.Notify_Teleported();
                if (i > 0) p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
            }
            guard.pather?.StopDead();
            guard.jobs?.StopAll();
            guard.Position = spots[3];
            guard.Notify_Teleported();
            StoryArc arc = HostageCrisis.Seize(map, leader, leader.Inmate(), crew.Skip(1), guard, HostageCrisis.Riot);
            if (arc == null)
            {
                problems0.Add("hostage plan: the crisis did not start");
                return;
            }
            (hostageSoakArcId, hostageSoakStartTick, hostageSoakHostageId, hostageSoakHostage) = (arc.id, Find.TickManager.TicksGame, guard.thingIDNumber, guard.LabelShort);
            // Staff already in the held room at the seizure may be there (they are let out); nobody else may come in. (Nor one on its threshold: a guard in the doorway, or a step
            // from it, at the seizure went on in, and was put down by the ringleader the next second; the door was open under them, and nothing that closes a door moves a pawn.)
            PrisonMap.Of(map).ForceDoorUpdate();
            hostageSoakInside.Clear();
            hostageSoakThreshold.Clear();
            hostageSoakThresholdIn.Clear();
            foreach (Pawn s in map.mapPawns.FreeColonistsSpawned)
                if (HostageHold.InHeld(map, s.Position)) hostageSoakInside.Add(s.thingIDNumber);
                else if (GenAdj.CellsAdjacent8Way(s).Any(c => c.InBounds(map) && HostageHold.InHeld(map, c))) hostageSoakThreshold.Add(s.thingIDNumber);
            hostageSoakInjuries = guard.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
            RPLog.Message($"[RimPrison:test] hostage plan: {guard.LabelShort} taken by {leader.LabelShort} and {crew.Count - 1} more at tick {hostageSoakStartTick}; nobody answers");
        }

        /// <summary>
        /// Hostage plan, every second or so: nobody lays a hand on the hostage while the stand-off is on (the crisis' own harm comes with the
        /// first deadline, which leaves the stand-off stage). The rioters used to beat the hostage to death in seconds: vanilla's pather melees
        /// whoever hostile blocks the way.
        /// </summary>
        private static void WatchHostageSoak(Map map)
        {
            if (hostageSoakArcId < 0 || Find.TickManager.TicksGame % 60 != 0) return;
            StoryArc arc = PrisonGame.Instance.story.ArcById(hostageSoakArcId);
            // The held doors keep staff out in real ticks (the guards go for the rioters otherwise): nobody who was not inside at the seizure stands in the held cells.
            if (!hostageSoakEntered && arc is { done: false } && PrisonMap.Of(map).heldCells != null)
                foreach (Pawn s in map.mapPawns.FreeColonistsSpawned)
                {
                    if (s.GetComp<CompStaff>()?.role == null || s.thingIDNumber == hostageSoakHostageId || hostageSoakInside.Contains(s.thingIDNumber)) continue;
                    bool inHeld = HostageHold.InHeld(map, s.Position);
                    // (One on the threshold at the seizure may stumble in within the grace, and stay in; once out, or in only after it, they are as anyone else.)
                    if (hostageSoakThreshold.Contains(s.thingIDNumber))
                    {
                        bool grace = Find.TickManager.TicksGame - hostageSoakStartTick < ThresholdGraceTicks;
                        if (inHeld && (grace || hostageSoakThresholdIn.Contains(s.thingIDNumber))) { hostageSoakThresholdIn.Add(s.thingIDNumber); continue; }
                        if (!inHeld)
                        {
                            hostageSoakThresholdIn.Remove(s.thingIDNumber);
                            if (!grace) hostageSoakThreshold.Remove(s.thingIDNumber);
                            continue;
                        }
                    }
                    if (!inHeld) continue;
                    hostageSoakEntered = true;
                    problems0.Add($"hostage plan: {s.LabelShort} walked into the held room during the stand-off (the border doors let staff in): at {s.Position} on {s.CurJobDef?.defName}, {Find.TickManager.TicksGame - hostageSoakStartTick} ticks after the seizure, downed {s.Downed}, carried by {s.CarriedBy?.LabelShort ?? "nobody"}, on the threshold at the seizure {hostageSoakThreshold.Contains(s.thingIDNumber)}");
                    break;
                }
            if (hostageSoakBeaten) return;
            if (arc == null || arc.done || arc.stage != HostageCrisis.Standoff || arc.A is not { Dead: false } hostage) return;
            if (hostage.health.hediffSet.hediffs.Count(h => h is Hediff_Injury) <= hostageSoakInjuries) return;
            hostageSoakBeaten = true;
            problems0.Add($"hostage plan: {hostage.LabelShort} was hurt during the stand-off, before any deadline (rioters hit the hostage)");
        }

        /// <summary>Hostage plan, at the end: the crisis ended by itself (the deadlines and the hostage-takers' nerve), cleanly.</summary>
        private static void EvaluateHostageSoak(Map map, List<string> problems)
        {
            PrisonGame g = PrisonGame.Instance;
            if (hostageSoakArcId < 0)
            {
                problems.Add("hostage plan: no crisis was ever seeded");
                return;
            }
            StoryArc arc = g.story.ArcById(hostageSoakArcId);
            Pawn hostage = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == hostageSoakHostageId);
            bool ended = arc == null || arc.done;
            ChronicleEntry endLine = g.story.entries.LastOrDefault(e => e.ids.Contains(hostageSoakHostageId) && e.importance == 3 && e.tick > hostageSoakStartTick);
            string how = endLine?.text ?? "(no end line)";
            RPLog.Message($"[RimPrison:test] hostage soak: {hostageSoakHostage} {(ended ? "ended" : "STILL RUNNING")} after {((endLine?.tick ?? Find.TickManager.TicksGame) - hostageSoakStartTick) / (float)GenDate.TicksPerHour:F1} h; {how}; hostage {(hostage == null ? "gone" : hostage.Dead ? "dead" : "alive")}; crises {g.stats.hostageCrises} freed {g.stats.hostagesFreed} killed {g.stats.hostagesKilled}");
            if (!ended) problems.Add("hostage plan: the crisis nobody answered was still running at the end of the run");
            // Past the first deadline the hostage was hurt (the chronicle has the line), as the letters say.
            if (ended && arc != null && endLine != null && endLine.tick - hostageSoakStartTick > HostageCrisis.StandoffHours * GenDate.TicksPerHour + GenDate.TicksPerHour
                && !arc.pawns.Skip(1).Any(p => g.story.entries.Any(e => e.tick > hostageSoakStartTick && e.text == "RP_Chr_HostageHurt".Translate(hostageSoakHostage, p.LabelShort).Resolve())))
                problems.Add("hostage plan: the crisis ran past the first deadline and the hostage was not hurt");
            if (hostage is { Dead: false } && (HostageCrisis.IsHeld(hostage) || hostage.MentalStateDef == RPHostageDefOf.RP_BeingHeld)) problems.Add("hostage plan: the hostage is still held after the crisis ended");
            if (PrisonMap.Of(map).heldCells != null) problems.Add("hostage plan: a room is still held after the crisis ended");
            if (ended && g.stats.hostagesFreed + g.stats.hostagesKilled == 0) problems.Add("hostage plan: the crisis ended without a hostage freed or killed");
            if (ended && how == "(no end line)") problems.Add("hostage plan: the crisis ended without a milestone in the chronicle");
            if (Find.GameEnder.gameEnding) problems.Add("hostage plan: the crisis ended the game");
        }

        // ---- the saveload plan: a crisis in progress goes through a save ------------------------------------------------

        private static int hostageSeedHostage = -1, hostageSeedLeader = -1, hostageSeedId = -1;
        private static string hostageSeedState;

        /// <summary>What a crisis in progress is, as one comparable string (the round trip must not change it).</summary>
        private static string HostageState(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            pm.ForceDoorUpdate();
            StoryArc arc = g.story.arcs.FirstOrDefault(a => !a.done && a.def == RPHostageDefOf.RP_Arc_Hostage);
            if (arc == null) return "none";
            Pawn hostage = arc.A;
            return $"{arc.id}:{arc.note}:{arc.stage}:{arc.count}:{arc.markTick}:{arc.nextTick}:{arc.startTick}:{string.Join("+", arc.pawns.Select(p => p.thingIDNumber))}"
                   + $" hostage={HostageCrisis.IsHeld(hostage)}/{hostage.MentalStateDef?.defName}/{hostage.MentalState?.causedByPawn?.thingIDNumber}/{hostage.Position}"
                   + $" held={pm.heldCells?.Count}/doors={pm.Doors.Count(d => d.heldCells != null)}/cut={pm.heldPowerCut}"
                   + $" takers={string.Join("+", HostageCrisis.Takers(arc).Select(p => p.thingIDNumber))} letters={Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.arcId == arc.id && !l.answered)}";
        }

        /// <summary>Saveload plan, before the snapshot: a guard held by an inmate in the canteen, mid stand-off (a failed talk, utilities cut).</summary>
        private static void SeedHostageForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            IntVec3 centre = PrisonBuilder.CanteenCell(map);
            Room canteen = centre.GetRoom(map);
            List<IntVec3> spots = GenRadial.RadialCellsAround(centre, 6f, true).Where(c => c.InBounds(map) && c.Standable(map) && c.GetRoom(map) == canteen && c.GetEdifice(map) == null).ToList();
            Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => HostageCrisis.CanBeTaken(x) && !x.Drafted);
            Pawn leader = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => InmateUtility.IsActiveInmate(x) && !x.Downed && !x.InMentalState && !x.WorkTagIsDisabled(WorkTags.Violent) && !g.story.InArc(x) && x.Inmate() is { } c && DeathRowSystem.Ext(c) == null);
            if (spots.Count < 2 || guard == null || leader == null)
            {
                problems0.Add($"saveload: no crisis could be seeded (canteen spots {spots.Count}, a member of staff {guard != null}, an inmate {leader != null})");
                return;
            }
            foreach (Pawn p in new[] { leader, guard })
            {
                p.pather?.StopDead();
                p.jobs?.StopAll();
            }
            leader.Position = spots[0];
            leader.Notify_Teleported();
            guard.Position = spots[1];
            guard.Notify_Teleported();
            g.story.lastArcStart.Remove(RPHostageDefOf.RP_Arc_Hostage);
            StoryArc arc = HostageCrisis.Seize(map, leader, leader.Inmate(), new Pawn[0], guard, HostageCrisis.Riot);
            if (arc == null)
            {
                problems0.Add("saveload: the seeded hostage crisis did not start");
                return;
            }
            // Mid stand-off: a talk that failed, power and water cut a while ago.
            arc.count = 1;
            arc.markTick = Find.TickManager.TicksGame - 1234;
            (hostageSeedHostage, hostageSeedLeader, hostageSeedId) = (guard.thingIDNumber, leader.thingIDNumber, arc.id);
            hostageSeedState = HostageState(map);
            RPLog.Message($"[RimPrison:test] saveload hostage: {guard.LabelShort} held by {leader.LabelShort}, {hostageSeedState}");
        }

        /// <summary>After the load: the same crisis (arc, hostage, state, the held room's doors, the letter), still running; then it is ended.</summary>
        private static void CheckHostageAfterReload(Map map)
        {
            if (hostageSeedId < 0) return;
            PrisonGame g = PrisonGame.Instance;
            string after = HostageState(map);
            StoryArc arc = g.story.arcs.FirstOrDefault(a => !a.done && a.def == RPHostageDefOf.RP_Arc_Hostage);
            Pawn guard = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == hostageSeedHostage);
            bool intact = after == hostageSeedState && arc != null && arc.id == hostageSeedId && guard != null && arc.A == guard && guard.MentalState is MentalState_BeingHeld
                          && guard.health.hediffSet.HasHediff(RPHostageDefOf.RP_Hostage);
            RPLog.Message($"[RimPrison:test] saveload hostage: {(intact ? "intact" : "changed")} ({after})");
            if (!intact) reloadProblems.Add($"a hostage crisis in progress changed in the round trip: {hostageSeedState} -> {after}");
            else
            {
                // It goes on: the held room's doors refuse staff, and the crisis ends through its own path, with the hostage let go.
                var worker = (HostageArc)arc.def.Worker;
                worker.Finish(arc, map, HostageHow.Talked, HostageFate.Freed, "the test");
                PrisonMap.Of(map).ForceDoorUpdate();
                if (HostageCrisis.IsHeld(guard) || guard.MentalStateDef == RPHostageDefOf.RP_BeingHeld || PrisonMap.Of(map).heldCells != null)
                    reloadProblems.Add("the crisis loaded from a save did not end cleanly");
            }
            hostageSeedId = hostageSeedHostage = hostageSeedLeader = -1;
        }
    }
}
