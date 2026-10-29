using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Parole as a scene and life on parole (events plan, and the round trip in the saveload plan). The check builds a hearing room in the yard (a
    /// desk and two chairs on a free patch), and drives every step through the game's own calls, with the dice seeded where a roll decides:
    /// no room (the roll and the old letter), the room as a desk and chairs, the midnight pass (scheduling, the day's places, the warden's letter
    /// for the closest call), the board's hours, the call, a real guard walking the inmate in and the sitting (real ticks), the letter with its
    /// speakers and band, each speaker's stated amount, a refusal (reasons on the file, the goal turning into proving the board wrong, and
    /// proving it), a grant with its terms in the register, a parolee rearrested for the term they broke (the chronicle and the letter on their
    /// return name it), and the news and the letter from outside. Works on throwaway pawns and furniture and puts back what it touches.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>What the saveload plan seeds for the parole round trip: the case, and what it must read after the reload.</summary>
        private static string paroleSeedCase;
        private static int paroleSeedInmateId = -1, paroleSeedDenialTick = -1;
        private static string paroleSeedDenial;

        /// <summary>The first draw of a seeded stream is <paramref name="outcome"/> for this chance: the seed to push before the call whose first roll is that chance.</summary>
        private static int ParoleSeedFor(float chance, bool outcome)
        {
            for (int seed = 1; seed < 20000; seed++)
            {
                Rand.PushState(seed);
                bool roll = Rand.Chance(chance);
                Rand.PopState();
                if (roll == outcome) return seed;
            }
            return -1;
        }

        /// <summary>The first <paramref name="draws"/> draws of a seeded stream are all <paramref name="outcome"/> for this chance.</summary>
        private static int ParoleSeedForRun(float chance, int draws, bool outcome)
        {
            for (int seed = 1; seed < 20000; seed++)
            {
                Rand.PushState(seed);
                bool all = true;
                for (int i = 0; i < draws && all; i++) all = Rand.Chance(chance) == outcome;
                Rand.PopState();
                if (all) return seed;
            }
            return -1;
        }

        private static void ParoleTest(Map map)
        {
            Guarded("parole", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                int Now() => Find.TickManager.TicksGame;
                void Fail(string what) => problems0.Add("parole test: " + what);
                void Landed(bool ok, string what)
                {
                    if (!ok) Fail($"the seeded roll for {what} did not land as drawn (a new Rand call before the decisive roll shifts every seeded outcome, and the failures that follow are its)");
                }
                string log = "";
                int problemsBefore = problems0.Count;

                // ---- what is put back ---------------------------------------------------------------------------------
                List<ParoleCase> cases0 = g.story.paroleCases.ToList();
                r.Add(() =>
                {
                    g.story.paroleCases.Clear();
                    g.story.paroleCases.AddRange(cases0);
                });
                g.story.paroleCases.Clear();
                // (A parole letter already waiting for some other inmate would hold back the warden's letter for the closest call: they are set aside and put back.)
                List<ChoiceLetter_PrisonDecision> waitingLetters = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_Parole")).ToList();
                foreach (ChoiceLetter_PrisonDecision l in waitingLetters) Find.LetterStack.RemoveLetter(l);
                r.Add(() =>
                {
                    foreach (ChoiceLetter_PrisonDecision l in waitingLetters)
                        if (!Find.LetterStack.LettersListForReading.Contains(l)) Find.LetterStack.ReceiveLetter(l);
                });
                var realInmates = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null).ToList();
                var active0 = realInmates.Select(c => (c, c.activeFlag)).ToList();
                // (Only the throwaways are due when the midnight pass is called by hand; the ticks put the real inmates' flags back by themselves.)
                void Quiet(bool on)
                {
                    foreach ((CompInmate c, bool active) in active0) c.activeFlag = !on && active;
                }
                r.Add(() => Quiet(false));
                // (The hearing room stays up during the real ticks below: the real midnight and hourly passes may schedule, drop or hear real inmates meanwhile, and the
                // throwaways released here are kept in the world for their return.)
                PutBackRealInmates(map, r);
                PutBackKeptPawns(r);
                ChoiceLetter_PrisonDecision ParoleLetter(Pawn who) =>
                    Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Parole") && l.pawns.Contains(who));
                void DropLetters()
                {
                    foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_Parole")).ToList())
                    {
                        l.answered = true;
                        Find.LetterStack.RemoveLetter(l);
                    }
                }
                r.Add(DropLetters);
                void Answer(ChoiceLetter_PrisonDecision letter, int option, int seed)
                {
                    letter.answered = true;
                    Rand.PushState(seed);
                    try { PrisonDecisions.Choose(letter, option); }
                    finally { Rand.PopState(); }
                    Find.LetterStack.RemoveLetter(letter);
                }
                bool Memory(Pawn p, ThoughtDef def) => p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(def) != null;
                CrimeDef calm = DefDatabase<CrimeDef>.AllDefsListForReading.FirstOrDefault(d => !d.violent), violent = DefDatabase<CrimeDef>.AllDefsListForReading.FirstOrDefault(d => d.violent);
                if (calm == null || violent == null)
                {
                    Fail("the game has no violent and no non-violent crime to try");
                    return;
                }

                // A throwaway inmate with a forty-day sentence, a clean sheet and a file with nothing in it but tension 40 (the board's odds are 33%). (The test's clock is hours old,
                // so nobody has served half of anything: the midnight pass is called with the sentence at 0 days, which is half served, and put back.)
                void Due(CompInmate c, bool due) => c.sentenceDays = due ? 0 : 40;
                Pawn Hopeful()
                {
                    Pawn p = Throwaway(map, r);
                    CompInmate c = p.Inmate();
                    c.RefreshActiveFlag();
                    (c.processed, c.awaitingSearch, c.want, c.records) = (true, false, null, new List<MisconductRecord>());
                    (c.sentenceDays, c.admittedTick, c.lastHearingTick, c.programsPassed, c.hoursWorked, c.tension) = (40, 0, -999999, 0, 0, 40f);
                    (c.paroleDenial, c.paroleDenialTick, c.crime, c.jobWaiting, c.familyCount, c.mentorId, c.religious, c.innocent) = (null, -1, calm, false, 0, -1, false, false);
                    (c.relatives, c.staffMemories, c.gang, c.forcedActivity) = (new List<Relative>(), new List<StaffMemory>(), null, null);
                    c.reputations.Clear();
                    c.passedPrograms.Clear();
                    c.security = InmateUtility.DefaultSecurity;
                    return p;
                }
                ParoleCase Probe(Pawn p, bool victim = false)
                {
                    var probe = new ParoleCase { pawnId = p.thingIDNumber, name = p.LabelShort, victimLetter = victim };
                    ParoleScene.Assess(map, p, p.Inmate(), probe);
                    return probe;
                }
                void Place(Pawn p, IntVec3 cell)
                {
                    p.pather.StopDead();
                    p.Position = cell;
                    p.Notify_Teleported();
                }
                string Pct(float v) => (v < 0f ? "-" : "+") + Mathf.Abs(Mathf.RoundToInt(v * 100f)) + "%";

                // ---- no hearing room: the roll, and the warden's letter, as they always were ---------------------------------
                Pawn h1 = Hopeful();
                CompInmate c1 = h1.Inmate();
                // A free patch of the yard for the room (five by three).
                IntVec3 corner = IntVec3.Invalid;
                foreach (IntVec3 cell in pm.zones.CellsOf(RPDefOf.RP_Yard))
                {
                    var rect = new CellRect(cell.x, cell.z, 5, 3);
                    if (rect.Cells.All(x => x.InBounds(map) && x.Standable(map) && pm.zones.ZoneAt(x) == RPDefOf.RP_Yard && x.GetThingList(map).Count == 0))
                    {
                        corner = cell;
                        break;
                    }
                }
                if (!corner.IsValid)
                {
                    Fail("no free five by three patch in the yard to build the hearing room on");
                    return;
                }
                var block = new CellRect(corner.x, corner.z, 5, 3);
                Quiet(true);
                Due(c1, true);
                ParoleSystem.DailyHearings(g, map);
                Due(c1, false);
                Quiet(false);
                bool oldLetter = ParoleLetter(h1) != null;
                if (!oldLetter || g.story.paroleCases.Count != 0)
                    Fail($"without a hearing room the closest call must come to the warden as before (letter {oldLetter}) and nobody is scheduled ({g.story.paroleCases.Count}); due: lifer {c1.IsLifer} security {c1.security?.defName} active {c1.activeFlag} "
                         + $"served {c1.DaysServed:F1} of {c1.sentenceDays}, last hearing {c1.lastHearingTick}, spawned prisoner {map.mapPawns.PrisonersOfColonySpawned.Contains(h1)}, parole letters waiting {Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.Is("RP_Decision_Parole"))}");
                DropLetters();
                c1.lastHearingTick = -999999;
                log += $"noRoom={oldLetter}";

                // ---- the room is a desk and a chair in the hearing zone -----------------------------------------------------
                foreach (IntVec3 cell in block.Cells)
                {
                    PrisonZoneDef was = pm.zones.ZoneAt(cell);
                    r.Add(() => pm.zones.Set(cell, was));
                    pm.zones.Set(cell, RPParoleDefOf.RP_HearingRoom);
                }
                void Put(ThingDef def, IntVec3 cell)
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                    t.SetFactionDirect(Faction.OfPlayer);
                    GenSpawn.Spawn(t, cell, map, Rot4.North);
                    r.Add(() =>
                    {
                        if (!t.Destroyed) t.Destroy(DestroyMode.Vanish);
                    });
                }
                int zoneOnly = HearingRooms.Capacity(map, pm);
                Put(RPAdminDefOf.RP_OfficeDesk, new IntVec3(corner.x + 2, 0, corner.z + 2));
                int deskOnly = HearingRooms.Capacity(map, pm);
                Put(ThingDefOf.DiningChair, new IntVec3(corner.x + 1, 0, corner.z));
                int oneChair = HearingRooms.Capacity(map, pm);
                Put(ThingDefOf.DiningChair, new IntVec3(corner.x + 3, 0, corner.z));
                int twoChairs = HearingRooms.Capacity(map, pm);
                if (zoneOnly != 0 || deskOnly != 0 || oneChair != 1 || twoChairs != 2) Fail($"hearing room capacity: zone alone {zoneOnly}, with the desk {deskOnly}, one chair {oneChair}, two chairs {twoChairs} (0, 0, 1, 2 expected)");
                log += $" capacity={zoneOnly}/{deskOnly}/{oneChair}/{twoChairs}";
                IntVec3 start = CellFinder.TryFindRandomCellNear(block.CenterCell, map, 9, x => x.Standable(map) && !block.Contains(x) && pm.zones.ZoneAt(x) == RPDefOf.RP_Yard && x.GetFirstBuilding(map) == null, out IntVec3 near) ? near : block.CenterCell;
                Place(h1, start);

                // ---- the midnight pass schedules the hearing, the day's places run out, the closest call is the warden's --------
                Quiet(true);
                int hearingsBefore = g.stats.paroleHearings;
                Due(c1, true);
                ParoleSystem.DailyHearings(g, map);
                Due(c1, false);
                Quiet(false);
                ParoleCase hc1 = ParoleScene.CaseOf(g, h1);
                bool scheduled = hc1 is { stage: HearingStage.Queued, askWarden: true, victimLetter: false } && ParoleLetter(h1) == null && Math.Abs(c1.lastHearingTick - Now()) < 5;
                if (!scheduled) Fail($"the midnight pass with a hearing room: case {(hc1 == null ? "none" : hc1.stage + (hc1.askWarden ? " for the warden" : ""))}, a letter at once {ParoleLetter(h1) != null}");
                if (hc1 == null) return;
                // (Fifteen places taken of sixteen, with this hearing: of two due, one is scheduled, one is the board's to roll, and the warden has the first one's letter only.)
                Pawn h2 = Hopeful(), h3 = Hopeful();
                for (int i = 0; i < 14; i++) g.story.paroleCases.Add(new ParoleCase { pawnId = -100 - i, name = "dummy", scheduledTick = Now() });
                List<Pawn> placed = ParoleScene.Schedule(g, map, new List<Pawn> { h2, h3 }, out int overflow);
                bool places = placed.Count == 1 && overflow == 1 && ParoleScene.CaseOf(g, placed[0]) is { askWarden: false };
                g.story.paroleCases.RemoveAll(x => x.pawnId < 0 || x.pawnId == h2.thingIDNumber || x.pawnId == h3.thingIDNumber);
                if (!places) Fail($"with one place of sixteen left, two due: {placed.Count} scheduled, {overflow} left to the board's roll (1 and 1 expected)");
                log += $" scheduled={scheduled} places={places}";

                // ---- the board's hours: not at night, then called --------------------------------------------------------------
                ParoleScene.Hourly(g, map, 20);
                bool nightQueued = hc1?.stage == HearingStage.Queued;
                ParoleScene.Hourly(g, map, 10);
                bool called = hc1?.stage == HearingStage.Called;
                if (!nightQueued || !called) Fail($"the board's hours: at 20:00 the case stood {(nightQueued ? "queued" : "elsewhere")}, at 10:00 {hc1?.stage}");

                // ---- a guard walks them in, they sit before the board, the decision letter lists the speakers and the band --------
                // (The prison's own guards are set aside for the walk: one who takes the escort job, the game's own choice among the guards, is drawn off to a riot, a shift's end or a break while the inmate
                // sits before the board - found in the yard when the letter came - and the check's own guard, whose name the letter must carry, is not the one who walked them in.)
                // (They are given their roles back as soon as the walk-in is over, not at the end of the block: the administrator's long wait below and the walk-in alone run with the live prison's guards on duty.)
                var realGuards = map.mapPawns.FreeColonistsSpawned.Select(x => x.GetComp<CompStaff>()).Where(x => x?.role is { guardDuties: true }).Select(x => (staff: x, role: x.role)).ToList();
                void ReturnTheGuards()
                {
                    foreach ((CompStaff staff, StaffRoleDef role) in realGuards) staff.role = role;
                }
                foreach ((CompStaff staff, StaffRoleDef _) in realGuards) staff.role = null;
                r.Add(ReturnTheGuards);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Place(guard, CellFinder.StandableCellNear(start, map, 3f));
                StaffMemories.Note(c1, guard, 40f);
                var giver = new WorkGiver_EscortToHearing();
                Job offered = giver.HasJobOnThing(guard, h1) ? giver.JobOnThing(guard, h1) : null;
                bool offeredOk = offered != null && offered.def == RPParoleDefOf.RP_EscortToHearing && offered.targetA.Thing == h1 && pm.zones.ZoneAt(offered.targetB.Cell) == RPParoleDefOf.RP_HearingRoom;
                if (!offeredOk) Fail($"the guard was not offered the escort job for the called inmate (job {offered?.def.defName})");
                bool fileShows = ITab_InmateFile.StatusLines(h1, c1, Now()).Any(x => x.Item1 == "RP_FileStatusHearing_Called".Translate().ToString()) && ParoleScene.FileLine(h1) == "RP_FileStatusHearing_Called".Translate().ToString();
                if (!fileShows) Fail("the inmate's file does not say that they are called to the parole hearing");
                bool sawEscortJob = false, sawEscorted = false, sawSeated = false;
                // (Guard and inmate walk in together: while the inmate is called and escorted, the escort is at their side, and it waits in the room for the hearing.)
                int walkSamples = 0;
                float maxApart = 0f;
                // (The guard waits in the room while the inmate is seated, and no longer than two hours: the board can be later than that (an administrator who is on a break, a long sitting), and the guard goes
                // back to their rounds; so "in the room" is asked while the inmate sits, not at the verdict.)
                bool escortInTheRoom = false;
                int seatedAt = -1, escortLeftAt = -1;
                ChoiceLetter_PrisonDecision letter = null;
                int ticked = 0;
                var trace = new System.Text.StringBuilder();
                while (ticked < 9000 && letter == null)
                {
                    Find.TickManager.DoSingleTick();
                    ticked++;
                    if (ticked % 150 == 1 && ticked < 3000)
                        trace.Append($" [{ticked}: {hc1.stage}, inmate {h1.Position - corner} {h1.CurJobDef?.defName} toil {h1.jobs.curDriver?.CurToilIndex} moving {h1.pather.Moving} dest {h1.pather.Destination.Cell - corner}; escort {(map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == hc1.escortId) is { } esc ? (esc.Position - corner) + " " + esc.CurJobDef?.defName : "-")}]");
                    if (ticked % 10 == 0 && map.mapPawns.FreeColonistsSpawned.Any(x => x.CurJobDef == RPParoleDefOf.RP_EscortToHearing && x.CurJob.targetA.Thing == h1)) sawEscortJob = true;
                    if (ticked % 10 == 0 && hc1.escorted && hc1.stage == HearingStage.Called && map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == hc1.escortId) is { } walker)
                    {
                        walkSamples++;
                        maxApart = Mathf.Max(maxApart, walker.Position.DistanceTo(h1.Position));
                    }
                    sawEscorted |= hc1.escorted;
                    sawSeated |= hc1.stage >= HearingStage.Seated;
                    if (ticked % 10 == 0 && hc1.stage >= HearingStage.Seated && hc1.stage != HearingStage.Held)
                    {
                        if (seatedAt < 0) seatedAt = ticked;
                        Pawn sitter = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == hc1.escortId);
                        bool inRoom = sitter != null && pm.zones.ZoneAt(sitter.Position) == RPParoleDefOf.RP_HearingRoom;
                        escortInTheRoom |= inRoom;
                        if (!inRoom && escortInTheRoom && escortLeftAt < 0) escortLeftAt = ticked;
                    }
                    letter = ParoleLetter(h1);
                }
                ReturnTheGuards();
                if (letter == null || !sawEscortJob || !sawEscorted || !sawSeated)
                {
                    Fail($"the hearing in real ticks: after {ticked} ticks the case is at {hc1.stage}, escort job seen {sawEscortJob}, escorted {sawEscorted}, seated {sawSeated}, letter {letter != null}; "
                         + $"the guard {guard.LabelShort} does {guard.CurJobDef?.defName}, the inmate {h1.CurJobDef?.defName} at {h1.Position - corner} (toil {h1.jobs.curDriver?.CurToilIndex} {h1.jobs.curDriver?.CurToilString}, moving {h1.pather.Moving}, target {h1.CurJob?.targetA.Cell - corner}/{h1.CurJob?.targetA.Thing}) trace{trace}");
                    return;
                }
                Pawn escortPawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == hc1.escortId);
                bool walkedTogether = walkSamples > 0 && maxApart <= 12f && escortPawn != null && escortInTheRoom;
                if (!walkedTogether) Fail($"the guard did not walk the inmate in: {walkSamples} samples of an escorted walk, never more than {maxApart:F1} cells apart (12 allowed), escort {hc1.escortName} stands at {(escortPawn == null ? "(gone)" : (escortPawn.Position - corner) + " " + pm.zones.ZoneAt(escortPawn.Position)?.defName)}, was in the hearing room while the inmate sat {escortInTheRoom} (seated at tick {seatedAt}, left at {escortLeftAt}, the verdict at {ticked})");
                string text = letter.Text.ToString();
                bool counted = g.stats.paroleHearings == hearingsBefore + 1;
                if (!counted) Fail($"the hearing was counted {g.stats.paroleHearings - hearingsBefore} times (once expected)");
                bool listed = text.Contains(guard.LabelShort) && text.Contains("+10%") && text.Contains(ParoleScene.BandLine(hc1)) && text.Contains(hc1.escortName) && text.Contains("RP_ParoleNobody".Translate().ToString())
                              && hc1.stage == HearingStage.Held && hc1.assessed;
                if (!listed) Fail($"the decision letter does not list the officer who speaks for them ({guard.LabelShort}, +10%), the band ({ParoleScene.BandLine(hc1)}), the escort ({hc1.escortName}) and nobody against:\n{text}");
                log += $" hearing={listed} walked={walkedTogether} ticks={ticked}";

                // ---- each speaker is worth what the letter says -------------------------------------------------------------
                Pawn h4 = Hopeful();
                CompInmate c4 = h4.Inmate();
                ParoleCase none = Probe(h4);
                float baseOdds = ParoleSystem.ParoleChance(c4), risk4 = ParoleSystem.ReoffendChance(c4);
                bool baseOk = Mathf.Abs(none.chance - (baseOdds + ParoleScene.BandHigh)) < 0.001f && ParoleScene.BandOf(risk4) == "RP_RiskHigh" && none.forText == "RP_ParoleNobody".Translate().ToString()
                              && none.reasons.Contains("RP_ParoleWhy_NoSpeaker".Translate().ToString()) && none.reasons.Contains("RP_ParoleWhy_Risk".Translate().ToString());
                if (!baseOk) Fail($"a file with no speaker: odds {none.chance:P1} for the file's {baseOdds:P1} and the high band, for [{none.forText}], reasons [{none.reasons}]");
                StaffMemories.Note(c4, guard, 40f);
                ParoleCase withOfficer = Probe(h4);
                bool officerOk = Mathf.Abs(withOfficer.chance - none.chance - ParoleScene.OfficerFor) < 0.001f && withOfficer.forText.Contains(guard.LabelShort) && withOfficer.forText.Contains(Pct(ParoleScene.OfficerFor))
                                 && !withOfficer.reasons.Contains("RP_ParoleWhy_NoSpeaker".Translate().ToString());
                if (!officerOk) Fail($"a trusted officer: {none.chance:P1} -> {withOfficer.chance:P1} (+{ParoleScene.OfficerFor:P0} stated), for [{withOfficer.forText}]");
                // The officer must still be on the staff: one who left does not speak.
                c4.staffMemories.Clear();
                StaffMemories.Note(c4, 987654, "Officer Gone", 60f);
                bool goneOk = Mathf.Abs(Probe(h4).chance - none.chance) < 0.001f;
                if (!goneOk) Fail("an officer who is no longer on the staff spoke for them");
                c4.staffMemories.Clear();
                Pawn chaplain = ThrowawayStaff(map, r, DefDatabase<StaffRoleDef>.GetNamed("RP_Chaplain"));
                c4.religious = true;
                ParoleCase withChaplain = Probe(h4);
                c4.passedPrograms.Add(DefDatabase<ProgramDef>.AllDefsListForReading.First(d => d.deepensFaith));
                ParoleCase withGuidance = Probe(h4);
                bool chaplainOk = Mathf.Abs(withChaplain.chance - none.chance - ParoleScene.ChaplainFor) < 0.001f && withChaplain.forText.Contains(chaplain.LabelShort)
                                  && Mathf.Abs(withGuidance.chance - none.chance - ParoleScene.ChaplainGuidedFor) < 0.001f;
                if (!chaplainOk) Fail($"the chaplain: {none.chance:P1} -> {withChaplain.chance:P1} (+{ParoleScene.ChaplainFor:P0}), after spiritual guidance {withGuidance.chance:P1} (+{ParoleScene.ChaplainGuidedFor:P0})");
                (c4.religious, c4.passedPrograms) = (false, new List<ProgramDef>());
                Pawn mentor = Throwaway(map, r);
                c4.mentorId = mentor.thingIDNumber;
                ParoleCase withMentor = Probe(h4);
                bool mentorOk = Mathf.Abs(withMentor.chance - none.chance - ParoleScene.MentorFor) < 0.001f && withMentor.forText.Contains(mentor.LabelShort);
                if (!mentorOk) Fail($"a mentor: {none.chance:P1} -> {withMentor.chance:P1} (+{ParoleScene.MentorFor:P0}), for [{withMentor.forText}]");
                c4.mentorId = -1;
                // Against them: the victim's family, for a violent crime only.
                c4.crime = violent;
                ParoleCase withVictim = Probe(h4, victim: true);
                c4.crime = calm;
                ParoleCase calmVictim = Probe(h4, victim: true);
                bool victimOk = Mathf.Abs(withVictim.chance - none.chance + ParoleScene.VictimAgainst) < 0.001f && withVictim.againstText.Contains(Pct(-ParoleScene.VictimAgainst)) && withVictim.reasons.Contains("RP_ParoleWhy_Victim".Translate().ToString())
                                && Mathf.Abs(calmVictim.chance - none.chance) < 0.001f;
                if (!victimOk) Fail($"the victim's family: {none.chance:P1} -> {withVictim.chance:P1} (-{ParoleScene.VictimAgainst:P0}) for a violent crime, {calmVictim.chance:P1} for another; against [{withVictim.againstText}]");
                // The band: low helps, medium is neutral, high hurts (programs and work move it).
                (c4.programsPassed, c4.hoursWorked) = (2, 100);
                ParoleCase low = Probe(h4);
                bool lowOk = ParoleScene.BandOf(low.risk) == "RP_RiskLow" && Mathf.Abs(low.chance - ParoleSystem.ParoleChance(c4) - ParoleScene.BandLow) < 0.001f && ParoleScene.BandLine(low).Contains(Pct(ParoleScene.BandLow));
                (c4.programsPassed, c4.hoursWorked) = (1, 0);
                ParoleCase medium = Probe(h4);
                bool mediumOk = ParoleScene.BandOf(medium.risk) == "RP_RiskMedium" && Mathf.Abs(medium.chance - ParoleSystem.ParoleChance(c4)) < 0.001f;
                if (!lowOk || !mediumOk) Fail($"the reoffend band: low {low.risk:P0} gave {low.chance:P1} for the file's {ParoleSystem.ParoleChance(c4):P1} (+{ParoleScene.BandLow:P0} stated), medium {medium.risk:P0} gave {medium.chance:P1}");
                (c4.programsPassed, c4.hoursWorked) = (0, 0);
                // Hidden facts stay hidden: a gang nobody has found, and a claim of innocence, are on no letter and move no odds.
                c4.gang = DefDatabase<GangDef>.AllDefsListForReading.First();
                (c4.gangKnown, c4.innocent) = (false, true);
                ParoleCase secret = Probe(h4);
                bool hiddenOk = Mathf.Abs(secret.chance - none.chance) < 0.001f && !(secret.forText + secret.againstText + secret.reasons).Contains(c4.gang.label) && secret.reasons == none.reasons;
                if (!hiddenOk) Fail($"a hidden gang and a claim of innocence changed what the board has before it ({secret.chance:P1} for {none.chance:P1}, [{secret.reasons}])");
                (c4.gang, c4.gangKnown, c4.innocent) = (null, false, false);
                log += $" speakers={officerOk && goneOk && chaplainOk && mentorOk && victimOk && lowOk && mediumOk} hidden={hiddenOk}";

                // ---- a refusal: reasons on the file, and the goal turns into proving the board wrong ---------------------------------
                InmateWant parole = Wants.Form(h1, c1, RPWantDefOf.RP_Want_Parole);
                if (parole == null)
                {
                    Fail("no make parole goal for the inmate at the hearing");
                    return;
                }
                parole.renewals = parole.def.maxRenewals;
                float odds1 = hc1.chance;
                float final1 = Mathf.Clamp(odds1 - 0.3f, 0.02f, 0.95f);
                Answer(letter, 1, ParoleSeedFor(final1, false));
                Landed(!h1.guest.Released, "the warden's opposition (a refusal)");
                string denial = c1.paroleDenial ?? "";
                string[] expectedReasons = { "RP_ParoleWhy_NoPrograms", "RP_ParoleWhy_NoWork", "RP_ParoleWhy_Risk", "RP_ParoleWhy_Warden" };
                bool reasonsOk = expectedReasons.All(k => denial.Contains(k.Translate().ToString())) && !denial.Contains("RP_ParoleWhy_NoSpeaker".Translate().ToString()) && c1.paroleDenialTick >= Now() - 5;
                bool lineOk = Lines(r, "RP_Chr_ParoleDeniedWhy".Translate(h1.LabelShort, denial).Resolve()) == 1;
                bool fileOk = ITab_InmateFile.StatusLines(h1, c1, Now()).Any(x => x.Item1.Contains(denial) && x.Item1.Contains((c1.paroleDenialTick / GenDate.TicksPerDay + 1).ToString()));
                bool heldGone = ParoleScene.CaseOf(g, h1) == null && ParoleLetter(h1) == null && !h1.guest.Released;
                bool proving = c1.want is { def: var pd, status: WantStatus.OnTrack } && pd == RPParoleDefOf.RP_Want_ProveWrong && c1.want.note == "RP_ParoleWhy_NoPrograms".Translate().ToString() && parole.status == WantStatus.Lost;
                if (!reasonsOk || !lineOk || !fileOk || !heldGone || !proving)
                    Fail($"a refusal at the hearing: reasons on the file [{denial}] ok {reasonsOk}, chronicle line {lineOk}, the file shows it {fileOk}, case and letter gone {heldGone}; "
                         + $"the goal {c1.want?.def?.defName}/{c1.want?.status} ({c1.want?.note}), the old one {parole.status}");
                log += $" refused={reasonsOk && lineOk && fileOk && proving}";
                // Proving them wrong: odds up by the gain, and the next hearing counts it; a serious incident seen since ends it.
                InmateWant prove = c1.want;
                (c1.programsPassed, c1.hoursWorked) = (2, 100);
                Wants.Hourly(h1, c1);
                bool proved = prove is { status: WantStatus.Fulfilled } && WantWorker_ProveWrong.Proved(c1);
                c1.reputations.Clear();
                ParoleCase afterProof = Probe(h1);
                bool proofCounts = afterProof.forText.Contains("RP_ParoleFor_Proved".Translate(Pct(ParoleScene.ProvedFor)).ToString());
                if (!proved || !proofCounts) Fail($"proving the board wrong: goal {prove?.status}, counted at the next hearing {proofCounts}");
                Pawn h5 = Hopeful();
                CompInmate c5 = h5.Inmate();
                c5.paroleDenial = "no program passed";
                c5.paroleDenialTick = Now() - 100;
                Wants.Notify_ParoleReasons(h5, c5);
                InmateWant prove5 = c5.want;
                c5.records.Add(new MisconductRecord { def = DefDatabase<MisconductDef>.AllDefsListForReading.First(d => d.severity >= 2), tick = Now() + 1, seen = true });
                Wants.Hourly(h5, c5);
                bool relapse = prove5 is { def: var d5, status: WantStatus.Lost } && d5 == RPParoleDefOf.RP_Want_ProveWrong;
                if (!relapse) Fail($"a serious incident seen after the refusal left the goal at {prove5?.status}");
                log += $" prove={proved && proofCounts && relapse}";

                // ---- an administrator at the desk is the board ---------------------------------------------------------------------
                // (The prison's own administrator is set aside for the hearing: they sit at the desk and hold it - a reservation the throwaway cannot get past - and get up from it again when their paperwork is
                // done, and the board is then whoever is there when the hearing looks, or the warden's chair after the wait: what the check sees is not under its hand. They are given no work, and the
                // throwaway is the one administrator on staff and at the desk.)
                foreach (Pawn real in map.mapPawns.FreeColonistsSpawned.Where(x => x.GetComp<CompStaff>()?.role == RPParoleDefOf.RP_Administrator).ToList())
                {
                    CompStaff realStaff = real.GetComp<CompStaff>();
                    realStaff.role = null;
                    var priorities0 = DefDatabase<WorkTypeDef>.AllDefsListForReading.Select(w => (w, real.workSettings?.GetPriority(w) ?? 0)).ToList();
                    r.Add(() =>
                    {
                        realStaff.role = RPParoleDefOf.RP_Administrator;
                        if (real.workSettings != null) foreach ((WorkTypeDef w, int priority) in priorities0) real.workSettings.SetPriority(w, priority);
                    });
                    if (real.workSettings != null) foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading) real.workSettings.SetPriority(w, 0);
                    real.jobs.EndCurrentJob(JobCondition.InterruptForced);
                }
                Pawn admin = ThrowawayStaff(map, r, RPParoleDefOf.RP_Administrator);
                // (At the hearing room's own desk: a walk from the intake across a prison can outlast the time the hearing waits for a board, and it sits with the warden's chair.)
                Thing boardDesk = map.listerBuildings.AllBuildingsColonistOfDef(RPAdminDefOf.RP_OfficeDesk).FirstOrDefault(d => pm.zones.ZoneAt(d.Position) == RPParoleDefOf.RP_HearingRoom && !d.IsBurning());
                Place(admin, boardDesk != null && boardDesk.InteractionCell.Standable(map) ? boardDesk.InteractionCell : CellFinder.StandableCellNear(start, map, 3f));
                // (A throwaway can be spawned tired and go to bed halfway through the sitting.)
                if (admin.needs?.rest != null) admin.needs.rest.CurLevel = 1f;
                // (Nor sent to bed to heal: a generated member of staff may come with a wound or an illness that wants medical rest, and then lies there through the whole sitting.)
                bool medicalRest = HealthAIUtility.ShouldSeekMedicalRest(admin);
                if (medicalRest) admin.health.Reset();
                // (Nor slip off for a staff break: a throwaway, so its timetable needs no putting back.)
                if (admin.timetable != null) for (int hr = 0; hr < 24; hr++) admin.timetable.SetAssignment(hr, TimeAssignmentDefOf.Work);
                // (The staff room stays: a break does not take the board out of a sitting, StaffBreaks.Busy, and the check's own timetable is rewritten below.)
                Pawn h11 = Hopeful();
                Place(h11, start);
                var hc11 = new ParoleCase { pawnId = h11.thingIDNumber, name = h11.LabelShort, scheduledTick = Now(), askWarden = true };
                g.story.paroleCases.Add(hc11);
                ParoleScene.Hourly(g, map, 10);
                // (The prison's own administrator, hired at the start, is on staff too and may be the one at the desk: the board is whichever administrator sat, seen by name.)
                var sat = new HashSet<string>();
                ChoiceLetter_PrisonDecision letter11 = null;
                for (int t = 0; t < 9000 && letter11 == null; t++)
                {
                    Find.TickManager.DoSingleTick();
                    // (The prison's hourly pass writes every member of staff's shift into their timetable, the pinned hours of this one with it: written again.)
                    if (t % 60 == 0 && admin.timetable != null)
                        for (int hr = 0; hr < 24; hr++) admin.timetable.SetAssignment(hr, TimeAssignmentDefOf.Work);
                    // (At the desk: on the sitting job, or at paperwork at the hearing room's own desk, as the hearing counts one.)
                    if (t % 10 == 0 && ParoleScene.BoardReady(map, pm, hc11, out Pawn atTheDesk) && atTheDesk != null) sat.Add(atTheDesk.LabelShort);
                    letter11 = ParoleLetter(h11);
                }
                bool sawBoard = sat.Count > 0;
                bool adminBoard = letter11 != null && sawBoard && hc11.boardName == admin.LabelShort && sat.Contains(hc11.boardName) && letter11.Text.ToString().Contains("RP_ParoleBoard_Admin".Translate(hc11.boardName).ToString());
                if (!adminBoard) Fail($"with an administrator on staff the board is the administrator at the desk: letter {letter11 != null}, administrators seen sitting [{string.Join(", ", sat)}] (ours is {admin.LabelShort}), board named {hc11.boardName} (stage {hc11.stage}, the administrator does {admin.CurJobDef?.defName}; needed medical rest at the start {medicalRest}, now {HealthAIUtility.ShouldSeekMedicalRest(admin)}, rest {admin.needs?.rest?.CurLevel:F2}, downed {admin.Downed}, mental {admin.InMentalState}, "
                         + $"timetable {admin.timetable?.CurrentAssignment?.defName}, hediffs [{string.Join(", ", admin.health.hediffSet.hediffs.Select(x => x.def.defName))}]; the inmate {h11.LabelShort} "
                         + $"({h11.Inmate()?.security?.defName}) at {h11.Position} on {h11.CurJobDef?.defName ?? "no job"}, in the hearing room {pm.zones.ZoneAt(h11.Position) == RPParoleDefOf.RP_HearingRoom}, reaches the desk {boardDesk != null && h11.CanReach(boardDesk, PathEndMode.Touch, Danger.Deadly)}, "
                         + $"downed {h11.Downed}, mental {h11.MentalStateDef?.defName ?? "none"}, forced {h11.Inmate()?.forcedActivity?.defName ?? "none"}, escorted by {map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.CurJob?.targetA.Thing == h11)?.LabelShort ?? "nobody"})");
                if (letter11 != null) Answer(letter11, 2, 1);
                // No guard comes: they may walk in alone after two hours (never from maximum security), and not before.
                Pawn h2b = Hopeful();
                Place(h2b, start);
                var lone = new ParoleCase { pawnId = h2b.thingIDNumber, name = h2b.LabelShort, stage = HearingStage.Called, calledTick = Now() };
                g.story.paroleCases.Add(lone);
                bool waits = ParoleScene.JobFor(h2b, h2b.Inmate(), pm) == null;
                lone.calledTick = Now() - ParoleScene.UnescortedAfterTicks;
                bool walksIn = ParoleScene.JobFor(h2b, h2b.Inmate(), pm) is { } alone && alone.def == RPParoleDefOf.RP_AttendHearing;
                h2b.Inmate().security = DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderByDescending(d => d.dangerFactor).First();
                bool maxWaits = ParoleScene.JobFor(h2b, h2b.Inmate(), pm) == null;
                g.story.paroleCases.Remove(lone);
                // A hearing that cannot be held is dropped and the board rolls instead (see ParoleRegressions.cs): a queued one after two days, a called one after four hours.
                Pawn h3b = Hopeful();
                h3b.Inmate().lastHearingTick = Now();
                g.story.paroleCases.Add(new ParoleCase { pawnId = h3b.thingIDNumber, name = h3b.LabelShort, scheduledTick = Now() - ParoleScene.QueuedGiveUpTicks - 10 });
                Pawn h4b = Hopeful();
                h4b.Inmate().lastHearingTick = Now();
                g.story.paroleCases.Add(new ParoleCase { pawnId = h4b.thingIDNumber, name = h4b.LabelShort, stage = HearingStage.Called, calledTick = Now() - ParoleScene.CalledGiveUpTicks - 10 });
                // (Dropped, they are decided by the board's roll: refused here, both draws.)
                Rand.PushState(ParoleSeedForRun(ParoleSystem.ParoleChance(h3b.Inmate()), 2, false));
                try { ParoleScene.Hourly(g, map, 20); }
                finally { Rand.PopState(); }
                bool dropped = ParoleScene.CaseOf(g, h3b) == null && ParoleScene.CaseOf(g, h4b) == null && !h3b.guest.Released && !h4b.guest.Released
                               && Math.Abs(h3b.Inmate().lastHearingTick - Now()) < 5 && Math.Abs(h4b.Inmate().lastHearingTick - Now()) < 5;
                if (!waits || !walksIn || !maxWaits || !dropped)
                    Fail($"fallbacks: waits for an escort at first {waits}, walks in alone after two hours {walksIn}, maximum security never alone {maxWaits}; stale hearings dropped and decided by the board's roll {dropped}");
                log += $" board={adminBoard} fallbacks={waits && walksIn && maxWaits && dropped}";

                // ---- a hearing decided at once (no warden's letter) that grants parole: terms in the register --------------------------
                Pawn h7 = Hopeful();
                CompInmate c7 = h7.Inmate();
                InmateWant parole7 = Wants.Form(h7, c7, RPWantDefOf.RP_Want_Parole);
                var hc7 = new ParoleCase { pawnId = h7.thingIDNumber, name = h7.LabelShort, stage = HearingStage.Seated, calledTick = Now(), seatedTick = Now() };
                g.story.paroleCases.Add(hc7);
                float odds7 = Probe(h7).chance;
                int paroledBefore = g.stats.paroled, formerBefore = g.story.formerInmates.Count, heardBefore = g.stats.paroleHearings;
                Quiet(false);
                Rand.PushState(ParoleSeedFor(Mathf.Clamp(odds7, 0.02f, 0.95f), true));
                try { ParoleScene.Conclude(g, map, hc7, h7, null); }
                finally { Rand.PopState(); }
                Landed(g.stats.paroled == paroledBefore + 1, "the board's grant at a hearing decided at once");
                FormerInmate out7 = g.story.formerInmates.LastOrDefault();
                int days7 = Mathf.RoundToInt(Mathf.Clamp(c7.sentenceDays - c7.DaysServed, ParoleLife.MinDays, ParoleLife.MaxDays));
                bool granted = g.stats.paroled == paroledBefore + 1 && g.story.formerInmates.Count == formerBefore + 1 && out7 is { paroled: true, jobSecured: false, addressSecured: false } && out7.name == h7.LabelShort
                               && out7.paroleEndTick - out7.releasedTick == days7 * GenDate.TicksPerDay
                               && ParoleScene.CaseOf(g, h7) == null && hc7.boardName == null && g.stats.paroleHearings == heardBefore + 1 && parole7?.status == WantStatus.Fulfilled && h7.guest.Released;
                string terms = "RP_Chr_ParoleTerms".Translate(h7.LabelShort, "RP_ParoleTerm_JobFind".Translate().Resolve(), "RP_ParoleTerm_AddressHostel".Translate().Resolve(), days7).Resolve();
                bool termsLine = Lines(r, terms) == 1;
                if (!granted || !termsLine) Fail($"a hearing decided at once, granted: register {out7?.name} paroled {out7?.paroled} job {out7?.jobSecured} address {out7?.addressSecured}, case gone {ParoleScene.CaseOf(g, h7) == null}, goal {parole7?.status}, released {h7.guest.Released}, the terms line {termsLine}");
                log += $" granted={granted && termsLine}";

                // ---- life on parole: a parolee who breaks a term is rearrested for it, and the letter that brings them back says so -----
                Pawn h8 = Hopeful();
                CompInmate c8 = h8.Inmate();
                c8.RefreshActiveFlag();
                int rearrestsBefore = g.rearrests.Count, violationsBefore = g.stats.paroleViolations;
                Rand.PushState(ParoleSeedFor(ParoleSystem.ReoffendChance(c8), true));
                // (The old release: the roll at the gate and the rearrest queued at once. A normal release now opens a supervision case, see ReleasePrepChecks.)
                ReleaseSystem.LegacyReoffendRoll = true;
                try { ReleaseSystem.Release(g, h8, paroled: true); }
                finally
                {
                    ReleaseSystem.LegacyReoffendRoll = false;
                    Rand.PopState();
                }
                Rearrest back = g.rearrests.Count == rearrestsBefore + 1 ? g.rearrests.Last() : null;
                Landed(back != null, "a release that reoffends");
                string[] unsecured = { ParoleLife.NewOffence, "RP_Violation_NoWork", "RP_Violation_NoAddress", "RP_Violation_Reports" };
                bool caused = back != null && back.pawn == h8 && back.paroled && unsecured.Contains(back.reason);
                if (!caused) Fail($"a parolee who reoffends has a cause on their rearrest: {(back == null ? "no rearrest" : back.reason)} (one of {string.Join(", ", unsecured)} expected, with no job and no home)");
                else
                {
                    // (The news only comes of somebody who has left: they board the shuttle, and are out of the map and kept for their return.)
                    ShuttleSystem.Notify_Left(h8);
                    h8.DeSpawn();
                    Find.WorldPawns.PassToWorld(h8, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
                    r.Add(() => Find.WorldPawns.ForcefullyKeptPawns.Remove(h8));
                    Rand.PushState(ParoleSeedFor(0.5f, true));
                    back.dueTick = Now() - 1;
                    try { ReleaseSystem.CheckRearrests(g); }
                    finally { Rand.PopState(); }
                    string why = back.reason.Translate();
                    string news = "RP_Chr_RearrestedParoleeFor".Translate(h8.LabelShort, why).Resolve();
                    bool newsOk = Lines(r, news) == 1 && g.stats.paroleViolations == violationsBefore + (back.reason == ParoleLife.NewOffence ? 0 : 1) && g.returning.Contains(back);
                    // On the way back: the news found them; a bed for them, and the return letter and chronicle name the cause.
                    back.dueTick = Now() - 1;
                    // (A bed for them: one of the cells is emptied for the moment, and its owner put back in it afterwards.)
                    foreach (Building_Bed cellBed in pm.PrisonerBeds.ToList())
                    {
                        if (IntakeSystem.FreePrisonerBeds(map, c8.security).Count > 0) break;
                        if (!cellBed.Spawned || cellBed.CompAssignableToPawn is not { } assign || assign.AssignedPawnsForReading.Count != 1) continue;
                        Pawn owner = assign.AssignedPawnsForReading[0];
                        assign.ForceRemovePawn(owner);
                        if (IntakeSystem.FreePrisonerBeds(map, c8.security).Count == 0)
                        {
                            assign.TryAssignPawn(owner);
                            continue;
                        }
                        r.Add(() =>
                        {
                            if (owner.Destroyed || !cellBed.Spawned || cellBed.CompAssignableToPawn is not { } back || back.AssignedPawnsForReading.Contains(owner)) return;
                            foreach (Pawn other in back.AssignedPawnsForReading.ToList()) back.ForceRemovePawn(other);
                            back.TryAssignPawn(owner);
                        });
                    }
                    bool viaHourly = IntakeSystem.FreePrisonerBeds(map, c8.security).Count > 0;
                    if (viaHourly)
                    {
                        Reoffenders.Hourly(g, map);
                        LandMarshalsNow(g, map);
                    }
                    else Reoffenders.Return(g, map, h8, c8, bed: null, back.reason);
                    string label = "RP_ReturnedLabel".Translate(h8.LabelShort);
                    Letter returned = r.NewLetters.LastOrDefault(l => l.Label.ToString() == label);
                    bool returnOk = h8.Spawned && c8.timesAdmitted == 2 && returned is ChoiceLetter { } rl && rl.Text.ToString().Contains(back.reason.Translate().ToString())
                                    && Lines(r, "RP_Chr_ReturnedFor".Translate(h8.LabelShort, c8.crime?.label ?? "?", c8.timesAdmitted, why).Resolve()) == 1;
                    if (!newsOk || !returnOk)
                        Fail($"a parolee rearrested for {back.reason}: the news names it {newsOk}, the return (via the hour {viaHourly}) with the cause in the letter and the chronicle {returnOk}");
                    log += $" violation={newsOk && returnOk} via={(viaHourly ? "hourly" : "direct")}";
                }
                // The cause fits the terms: with a job and a home to go to the broken term is lost, not never found; and over many draws every cause is possible.
                var unplaced = new FormerInmate { jobSecured = false, addressSecured = false };
                var placedOut = new FormerInmate { jobSecured = true, addressSecured = true };
                Rand.PushState(4711);
                var seenUnplaced = new HashSet<string>();
                var seenPlaced = new HashSet<string>();
                try
                {
                    for (int i = 0; i < 400; i++)
                    {
                        seenUnplaced.Add(ParoleLife.PickViolation(unplaced));
                        seenPlaced.Add(ParoleLife.PickViolation(placedOut));
                    }
                }
                finally { Rand.PopState(); }
                bool fits = seenUnplaced.SetEquals(unsecured) && seenPlaced.SetEquals(new[] { ParoleLife.NewOffence, "RP_Violation_JobLost", "RP_Violation_AddressLeft", "RP_Violation_Reports" });
                if (!fits) Fail($"causes for a parolee with no job and no home: {string.Join(",", seenUnplaced)}; with both: {string.Join(",", seenPlaced)}");
                // Someone who served their sentence has no terms: arrested again, it is for a new offence.
                Pawn h9 = Hopeful();
                int rearrestsAt = g.rearrests.Count;
                Rand.PushState(ParoleSeedFor(ParoleSystem.ReoffendChance(h9.Inmate()), true));
                ReleaseSystem.LegacyReoffendRoll = true;
                try { ReleaseSystem.Release(g, h9); }
                finally
                {
                    ReleaseSystem.LegacyReoffendRoll = false;
                    Rand.PopState();
                }
                Rearrest served = g.rearrests.Count == rearrestsAt + 1 ? g.rearrests.Last() : null;
                Landed(served != null, "a sentence served that reoffends");
                FormerInmate walked = g.story.formerInmates.LastOrDefault();
                bool noTerms = served is { paroled: false, reason: ParoleLife.NewOffence } && walked is { paroled: false, paroleEndTick: -1 };
                if (!noTerms) Fail($"someone released at the end of their sentence: reason {served?.reason}, supervised until {walked?.paroleEndTick}");
                log += $" causes={fits} sentenceServed={noTerms}";

                // ---- news and letters from outside name the terms ---------------------------------------------------------------------
                FormerInmate parolee = g.story.formerInmates.FirstOrDefault(e => e.name == h7.LabelShort && e.paroled);
                if (parolee == null)
                {
                    Fail("the paroled inmate is not in the register for the news and the letter");
                    return;
                }
                parolee.trustedId = -1;
                parolee.jobWaiting = false;
                Rand.PushState(ParoleSeedFor(0.5f, true));
                try { FormerInmates.Deliver(g, map, parolee); }
                finally { Rand.PopState(); }
                Landed(parolee.news == "RP_FormerNews_ParoleReport", "the parole report in the news");
                bool reportNews = parolee.news == "RP_FormerNews_ParoleReport" && Lines(r, "RP_Chr_NewsFromOutside".Translate("RP_FormerNews_ParoleReport".Translate(parolee.name, "?")).Resolve()) == 1;
                var freed = new FormerInmate { name = "Freed", paroled = false, paroleEndTick = -1 };
                bool neverReport = true;
                for (int seed = 1; seed < 40; seed++)
                {
                    Rand.PushState(seed);
                    try { FormerInmates.Deliver(g, map, freed); }
                    finally { Rand.PopState(); }
                    neverReport &= freed.news != "RP_FormerNews_ParoleReport";
                }
                if (!reportNews || !neverReport) Fail($"news from a parolee: {parolee.news} (the report variant expected), a released one never got it {neverReport}");
                Pawn h10 = Hopeful();
                CompInmate c10 = h10.Inmate();
                c10.tension = 50f;
                c10.mail = new List<MailRecord>();
                MailKindDef kindOfParolee = DefDatabase<MailKindDef>.GetNamed("RP_Mail_Parolee");
                // (Paroled at the hearing above, their supervision case starts when they leave: a parolee writes once gone, not while waiting for the shuttle. They leave now - the rollback puts the book and the counters back.)
                if (parolee.supervisionId >= 0) Supervision.Notify_Departed(g, parolee.supervisionId);
                bool sendsWithParolee = kindOfParolee.Worker.CanSend(h10, c10);
                int paroleeEnd = parolee.paroleEndTick;
                // (A register with no parolee under supervision has nobody to write.)
                var supervisedNow = g.story.formerInmates.Where(e => e.paroled && ParoleLife.Supervised(e, Now())).ToList();
                foreach (FormerInmate e in supervisedNow) e.paroleEndTick = Now() - 1;
                bool silentWithout = !kindOfParolee.Worker.CanSend(h10, c10);
                foreach (FormerInmate e in supervisedNow) e.paroleEndTick = e == parolee ? paroleeEnd : e.paroleEndTick;
                parolee.paroleEndTick = paroleeEnd;
                foreach (FormerInmate e in supervisedNow) if (e != parolee) e.paroleEndTick = Now() + GenDate.TicksPerDay;
                MailLetter mailed = g.mail.Compose(h10, c10, kindOfParolee, hides: false);
                g.mail.waiting.Add(mailed);
                Pawn sorter = guard;
                g.mail.Sort(mailed, sorter, 0.999f);
                bool delivered = c10.mail.Count == 1 && c10.mail[0].kind == kindOfParolee && Mathf.Abs(c10.tension - 46f) < 0.01f && Memory(h10, DefDatabase<ThoughtDef>.GetNamed("RP_LetterParolee"))
                                 && r.NewEntries.Any(e => e.text.Contains(h10.LabelShort) && e.text.Contains("RP_ParoleLetter_NoJob".Translate().ToString()) && e.text.Contains("RP_ParoleLetter_Hostel".Translate().ToString()));
                if (!sendsWithParolee || !silentWithout || !delivered)
                    Fail($"the letter from a parolee: can come while one is supervised {sendsWithParolee}, not when none is {silentWithout}, delivered with the terms in it {delivered} (tension {c10.tension:F0}, {c10.mail.Count} on file)");
                log += $" outside={reportNews && neverReport && delivered}";

                RPLog.Message($"[RimPrison:test] parole: {log} problems={problems0.Count - problemsBefore}");
            });
        }

        // ---- the round trip -----------------------------------------------------------------------------------------------

        /// <summary>The cases the saveload plan seeded (a hearing waiting for the warden with its letter, one called and walking in, one seated) and what each must read after the reload.</summary>
        private static readonly List<(int inmateId, string described)> paroleSeedCases = new List<(int, string)>();
        private static int paroleSeedDeniedTick;

        /// <summary>The saveload plan seeds hearings at each stage, the warden's letter for the held one, an inmate's refusal, a parolee with terms and a rearrest with its cause.</summary>
        private static void SeedParoleForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonGame game = g;
            // (Inmates the board could hear: in their own right mind, free of the regime's confinement, not locked down.)
            List<Pawn> free = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { security: not null, forcedActivity: null, awaitingSearch: false, activeFlag: true } c && !x.Downed && !x.InMentalState && !game.LockedDown(c)).ToList();
            if (free.Count < 3)
            {
                problems0.Add($"saveload: {free.Count} inmates the board could hear to seed parole hearings with (3 needed)");
                return;
            }
            Pawn p = free[0];
            int now = Find.TickManager.TicksGame;
            var hc = new ParoleCase
            {
                pawnId = p.thingIDNumber, name = p.LabelShort, stage = HearingStage.Held, scheduledTick = now - 5000, calledTick = now - 4000, seatedTick = now - 3000, heldTick = now - 1000, escortId = 4242,
                escorted = true, askWarden = true, victimLetter = true, victimWithdrawn = true, escortName = "Seedguard", boardName = "Seedadmin", assessed = true, forText = "Seedguard, an officer (+10%)", againstText = "a letter (-15%)",
                reasons = "no program passed|too few hours worked", chance = 0.371f, risk = 0.412f,
            };
            g.story.paroleCases.Add(hc);
            // A hearing mid-way across the reload: one called with a guard on the way, one in the chair.
            var called = new ParoleCase { pawnId = free[1].thingIDNumber, name = free[1].LabelShort, stage = HearingStage.Called, scheduledTick = now - 9000, calledTick = now - 700, escortId = 4343, escorted = true, escortName = "Walkguard", victimLetter = true };
            var seated = new ParoleCase { pawnId = free[2].thingIDNumber, name = free[2].LabelShort, stage = HearingStage.Seated, scheduledTick = now - 9000, calledTick = now - 2500, seatedTick = now - 300, escortId = 4444, escorted = true, escortName = "Sitguard", askWarden = true };
            g.story.paroleCases.Add(called);
            g.story.paroleCases.Add(seated);
            paroleSeedCases.Clear();
            foreach (ParoleCase c in new[] { hc, called, seated }) paroleSeedCases.Add((c.pawnId, ParoleScene.Describe(c)));
            (paroleSeedCase, paroleSeedInmateId) = (ParoleScene.Describe(hc), p.thingIDNumber);
            (paroleSeedDenial, paroleSeedDenialTick) = (p.Inmate().paroleDenial, p.Inmate().paroleDenialTick);
            p.Inmate().paroleDenial = "no program passed, too few hours worked";
            p.Inmate().paroleDenialTick = paroleSeedDeniedTick = now - 777;
            // The warden's letter for the held hearing (it is answered after the reload).
            ParoleHearings.AskWardenAfterSitting(g, map, p, hc);
            g.story.formerInmates.Add(new FormerInmate { name = "Paroleseed", number = 99998, paroled = true, releasedTick = now - 3000, paroleEndTick = now + 12345, jobSecured = true, addressSecured = false });
            g.rearrests.Add(new Rearrest { name = "Paroleseed", paroled = true, reason = "RP_Violation_NoWork", dueTick = now + 99999 });
            RPLog.Message($"[RimPrison:test] saveload parole: a hearing held for {p.LabelShort} with the warden's letter, one called ({called.name}) and one seated ({seated.name}), a refusal on their file, a parolee with terms and a rearrest for failing to find work");
        }

        /// <summary>
        /// After the reload: the cases at every stage, the refusal, the parolee's terms and the rearrest's cause came back whole; the warden's letter, answered, reaches the
        /// verdict for the held case (the reasons on the file are the case's own); then they go.
        /// </summary>
        private static void CheckParoleAfterReload(Map map)
        {
            if (paroleSeedInmateId < 0) return;
            PrisonGame g = PrisonGame.Instance;
            ParoleCase hc = g.story.paroleCases.FirstOrDefault(x => x.pawnId == paroleSeedInmateId);
            string after = hc == null ? "(gone)" : ParoleScene.Describe(hc);
            Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.thingIDNumber == paroleSeedInmateId);
            CompInmate c = p?.Inmate();
            FormerInmate e = g.story.formerInmates.FirstOrDefault(x => x.name == "Paroleseed");
            Rearrest ra = g.rearrests.FirstOrDefault(x => x.name == "Paroleseed");
            bool caseOk = after == paroleSeedCase;
            bool denialOk = c != null && c.paroleDenial == "no program passed, too few hours worked" && c.paroleDenialTick > 0;
            bool termsOk = e is { paroled: true, jobSecured: true, addressSecured: false } && e.paroleEndTick > e.releasedTick;
            bool reasonOk = ra is { paroled: true, reason: "RP_Violation_NoWork" };
            RPLog.Message($"[RimPrison:test] saveload parole: case {(caseOk ? "intact" : "changed")}, refusal {(denialOk ? "intact" : "changed")}, terms {(termsOk ? "intact" : "changed")}, rearrest cause {(reasonOk ? "intact" : "changed")}");
            if (!caseOk) reloadProblems.Add($"a parole hearing changed in the round trip: {paroleSeedCase} -> {after}");
            if (!denialOk || !termsOk || !reasonOk) reloadProblems.Add($"parole after reload: refusal {denialOk}, terms {termsOk}, rearrest cause {reasonOk}");
            // Hearings at the other stages.
            foreach ((int id, string described) in paroleSeedCases.Skip(1))
            {
                ParoleCase other = g.story.paroleCases.FirstOrDefault(x => x.pawnId == id);
                string now = other == null ? "(gone)" : ParoleScene.Describe(other);
                bool stageOk = now == described;
                RPLog.Message($"[RimPrison:test] saveload parole: {other?.stage.ToString() ?? "(gone)"} hearing {(stageOk ? "intact" : "changed")}");
                if (!stageOk) reloadProblems.Add($"a parole hearing at stage {described.Split(':')[2]} changed in the round trip: {described} -> {now}");
                g.story.paroleCases.RemoveAll(x => x.pawnId == id);
            }
            // The letter, answered after the reload, is the verdict of the hearing that was held: a refusal (every roll goes against them) with the case's reasons.
            ChoiceLetter_PrisonDecision letter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Parole") && p != null && l.pawns.Contains(p));
            if (letter == null) reloadProblems.Add("the warden's letter for the held parole hearing did not survive the reload");
            else if (hc != null && c != null)
            {
                letter.answered = true;
                Rand.PushState(ParoleSeedFor(0.95f, false));
                try { PrisonDecisions.Choose(letter, 2); }
                finally { Rand.PopState(); }
                Find.LetterStack.RemoveLetter(letter);
                bool decided = c.paroleDenialTick > paroleSeedDeniedTick && c.paroleDenial == "no program passed, too few hours worked";
                bool gone = ParoleScene.CaseOf(g, p) == null;
                RPLog.Message($"[RimPrison:test] saveload parole: the letter answered after the reload: case {(gone ? "gone" : "kept")}, refusal with the case's reasons {(decided ? "written" : "not written")}");
                if (!gone || !decided) reloadProblems.Add($"the warden's letter answered after the reload: case gone {gone}, refusal with the held case's reasons written {decided} (denial [{c.paroleDenial}] at {c.paroleDenialTick}, seeded at {paroleSeedDeniedTick})");
            }
            g.story.paroleCases.RemoveAll(x => x.pawnId == paroleSeedInmateId);
            if (c != null) (c.paroleDenial, c.paroleDenialTick) = (paroleSeedDenial, paroleSeedDenialTick);
            g.story.formerInmates.RemoveAll(x => x.name == "Paroleseed");
            g.rearrests.RemoveAll(x => x.name == "Paroleseed");
            paroleSeedInmateId = -1;
        }
    }
}
