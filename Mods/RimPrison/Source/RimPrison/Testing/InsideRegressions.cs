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
    /// Regression checks for the review of the contraband made inside, the wing policy for men and women, the marshals' prisoners and the escorted transfers (H1, M1-M6, L1-L7). Each is self-contained (throwaway inmates and
    /// staff, beds spawned and wings painted for the check, the policy and the intake set aside), runs no ticks and puts back what it touches. Each fails on the code it was written against: how that was shown is in the WORKLOG entry.
    /// </summary>
    public static partial class AutoTest
    {
        private static void RunInsideRegressions(Map map)
        {
            CheckInsideReclassificationHonoursTheWingPolicy(map);
            CheckInsideCarriedOverOfTheOtherSexDoNotEatTheQuota(map);
            CheckInsideOvercrowdedSingleSexLevelTakesThatSex(map);
            CheckInsideArrivalWithNoBedOfTheirSexIsToldOf(map);
            CheckInsideBedlessInmateOfTheOtherSexAsksNoBedList(map);
            CheckInsideEventAdmissionsHonourTheWingPolicy(map);
            CheckInsideWrongWingMovesHaveAQuotaOfTheirOwn(map);
            CheckInsideTwoGardenersHaveALinkEach(map);
            CheckInsideGardenerLeavingTakesTheGardenLinkWithThem(map);
            CheckInsideHarvestIsAtThePatchByTheGardenersWork(map);
            CheckInsideHarvestIsNotASmuggledThing(map);
            CheckInsideMarshalsPrisonerWhoWaitedTooLongIsToldOf(map);
            CheckInsideSpecialMeasuresCapDoesNotHoldTheMarshalsPrisoners(map);
            CheckInsideMadeInsideThingsAreNotTradeableOrPodContents(map);
            CheckInsideMashKeepsItsStartWhenSplit(map);
        }

        // ---- helpers ----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The intake set aside for the check: nothing wanted at any level, no marshals' prisoners, nobody carried over, no overcrowding; all put back (and the numbers of inmates not used up).</summary>
        private static void InsideQuietIntake(PrisonGame g, Rollback r)
        {
            var perDay0 = new Dictionary<SecurityLevelDef, int>(g.intake.perDay);
            int number0 = g.intake.nextInmateNumber;
            bool overcrowding0 = g.intake.allowOvercrowding;
            List<MarshalEntry> marshals0 = g.shuttle.marshals.ToList();
            List<Pawn> carried0 = g.shuttle.carriedOver.ToList();
            r.Add(() =>
            {
                foreach (KeyValuePair<SecurityLevelDef, int> kv in perDay0) g.intake.perDay[kv.Key] = kv.Value;
                g.intake.nextInmateNumber = number0;
                g.intake.allowOvercrowding = overcrowding0;
                g.shuttle.marshals.Clear();
                g.shuttle.marshals.AddRange(marshals0);
                g.shuttle.carriedOver.Clear();
                g.shuttle.carriedOver.AddRange(carried0);
            });
            foreach (SecurityLevelDef sec in perDay0.Keys.ToList()) g.intake.perDay[sec] = 0;
            g.intake.allowOvercrowding = false;
            g.shuttle.marshals.Clear();
            g.shuttle.carriedOver.Clear();
        }

        /// <summary>The wing policy as it is now, put back on dispose.</summary>
        private static void InsidePolicyBack(PrisonGame g, Rollback r)
        {
            var wings0 = new Dictionary<SecurityLevelDef, WingGender>(g.wingGenders);
            r.Add(() =>
            {
                g.wingGenders.Clear();
                foreach (KeyValuePair<SecurityLevelDef, WingGender> kv in wings0) g.wingGenders[kv.Key] = kv.Value;
            });
        }

        /// <summary>Takes this pawn (a generated one, not on the map) out of the game for good when the check is over.</summary>
        private static void InsideDiscardLater(Rollback r, IEnumerable<Pawn> pawns)
        {
            foreach (Pawn p in pawns.ToList())
                r.Add(() =>
                {
                    if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                    if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                });
        }

        /// <summary>The level's gate marked done for the check (the bureaucracy as it was is put back).</summary>
        private static void InsideUnlock(PrisonGame g, Rollback r, SecurityLevelDef sec)
        {
            BureaucracyDef gate = Unlocks.GateOf(sec);
            if (gate == null || g.bureaucracy.IsDone(gate)) return;
            g.bureaucracy.done.Add(gate);
            r.Add(() => g.bureaucracy.done.Remove(gate));
        }

        /// <summary>A level with no free bed at all and no wing of its own to fill (the intake's overcrowded case), unlocked for the check; null if the prison has none.</summary>
        private static SecurityLevelDef InsideFullLevel(Map map, PrisonGame g, Rollback r)
        {
            foreach (SecurityLevelDef s in DefDatabase<SecurityLevelDef>.AllDefsListForReading)
            {
                if (s.wingOnly || IntakeSystem.FreeBedCount(map, s) != 0) continue;
                InsideUnlock(g, r, s);
                return s;
            }
            return null;
        }

        /// <summary>Paints the cells of inmates' rooms as this wing, with a spare bed in each, until the level has this many free beds (or there is no room for more). The paint and the beds are taken back by the rollback. Returns how many are free.</summary>
        private static int InsidePaintedBeds(Map map, Rollback r, PrisonMap pm, PrisonZoneDef zone, SecurityLevelDef sec, int want, Gender? gender = null)
        {
            var painted = new HashSet<Room>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
            {
                if (IntakeSystem.FreeBedCount(map, sec, gender: gender) >= want) break;
                if (p.Inmate() is not { } || p.ownership?.OwnedBed?.GetRoom() is not { } room || !painted.Add(room)) continue;
                InsideZone(pm, r, zone, room.Cells);
                while (IntakeSystem.FreeBedCount(map, sec, gender: gender) < want && InsideSpareBed(map, room, r) != null) { }
            }
            return IntakeSystem.FreeBedCount(map, sec, gender: gender);
        }

        /// <summary>A generated inmate of this level and sex waiting aboard the shuttle that could not unload them (on the world's books, as in the game).</summary>
        private static Pawn InsideCarriedOver(PrisonGame g, Rollback r, SecurityLevelDef sec, Gender gender)
        {
            Pawn p = InmateGenerator.Generate(sec, g.intake.nextInmateNumber++, gender);
            if (!Find.WorldPawns.Contains(p) && !p.Spawned) Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
            g.shuttle.carriedOver.Add(p);
            InsideDiscardLater(r, new[] { p });
            return p;
        }

        // ---- H1: a reclassification into a wing kept for the other sex --------------------------------------------------------------------------------

        /// <summary>
        /// A woman put in protective custody when that wing is kept for men: there is no room for her (the "no room" answer, naming her and the wing), and a reclassification that is asked for anyway does not leave her reclassified in
        /// her old cell (the old code set the level first and then found no bed of hers to move her to, every hour for ever). A man has the same bed and goes there.
        /// </summary>
        private static void CheckInsideReclassificationHonoursTheWingPolicy(Map map)
        {
            Guarded("inside: a reclassification into a wing kept for the other sex", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                SecurityLevelDef pc = RPSecurityDefOf.RP_Protective;
                (Pawn a, Pawn b) = CellsPair(map);
                if (a == null || pc == null)
                {
                    RPLog.Message("[RimPrison:test] inside: reclassification into a single-sex wing skipped (no two inmates in different cells)");
                    return;
                }
                CompInmate ac = a.Inmate();
                Building_Bed bedA = a.ownership.OwnedBed, bedB = b.ownership.OwnedBed;
                Room roomB = bedB.GetRoom();
                Building_Bed spare = InsideSpareBed(map, roomB, r);
                if (spare == null || IntakeSystem.WingOf(bedA) == pc)
                {
                    RPLog.Message("[RimPrison:test] inside: reclassification into a single-sex wing skipped (no room for a second bed in a cell)");
                    return;
                }
                InsidePolicyBack(g, r);
                (SecurityLevelDef sec0, Gender gender0, int reclass0) = (ac.security, a.gender, ac.lastReclassTick);
                r.Add(() =>
                {
                    (ac.security, a.gender, ac.lastReclassTick) = (sec0, gender0, reclass0);
                    a.ownership.UnclaimBed();
                    a.ownership.ClaimBedIfNonMedical(bedA);
                    InmateGenerator.DressInUniform(a);
                    Cellmates.InvalidateCells();
                });
                // (b's cell is the protective wing for the check: the spare bed is free there, and only there.)
                InsideZone(pm, r, DefDatabase<PrisonZoneDef>.GetNamed("RP_WingProtective"), roomB.Cells);
                // A man, a wing for men: room, and he goes there.
                g.wingGenders[pc] = WingGender.Men;
                a.gender = Gender.Male;
                if (!InmateUtility.HasRoomIn(a, pc)) InsideFail("a man had no room in a protective custody wing kept for men, with a bed free there");
                InmateUtility.ChangeSecurity(a, ac, pc);
                if (ac.security != pc || IntakeSystem.WingOf(a.ownership.OwnedBed) != pc) InsideFail($"a man put in protective custody (kept for men) did not end up in a bed of the wing (level {ac.security?.defName}, wing {IntakeSystem.WingOf(a.ownership.OwnedBed)?.defName})");
                a.ownership.UnclaimBed();
                a.ownership.ClaimBedIfNonMedical(bedA);
                ac.security = sec0;
                Cellmates.InvalidateCells();
                // A woman, a wing for men: no room, and a reclassification asked for is refused, with a message naming her and the wing.
                a.gender = Gender.Female;
                string refused = "RP_WingKeptForOther".Translate(a.LabelShort, pc.label, "RP_WingWho_Men".Translate()).ToString();
                // (A message is shown with its first letter capitalised: a name that starts in lower case, "van der Wal", would never match the plain text.)
                string refusedShown = refused.CapitalizeFirst();
                LiveMessages().RemoveAll(m => m.text == refusedShown);
                if (InmateUtility.HasRoomIn(a, pc)) InsideFail("a woman had room in a protective custody wing kept for men (the PC letter would say yes, then leave her in her cell)");
                InmateUtility.ChangeSecurity(a, ac, pc);
                if (ac.security != sec0 || a.ownership.OwnedBed != bedA) InsideFail($"a woman put in a wing kept for men was reclassified in place (level {ac.security?.defName} from {sec0?.defName}, her bed changed {a.ownership.OwnedBed != bedA})");
                if (!LiveMessages().Any(m => m.text == refusedShown)) InsideFail("the warden was not told why the woman could not go to the wing kept for men");
                if (InmateUtility.NoRoomText(a, pc).ToString() != refused) InsideFail("the answer to the PC letter's \"no room\" for a woman, with the wing kept for men, does not name her and the wing");
                // A sentence that cannot wait (a commuted death sentence) reclassifies all the same, tells the warden she stays where she is, and the hourly pass moves her when a bed of hers is free.
                string stays = "RP_WingKeptForOtherStays".Translate(a.LabelShort, pc.label, "RP_WingWho_Men".Translate()).ToString().CapitalizeFirst();
                LiveMessages().RemoveAll(m => m.text == stays);
                if (!InmateUtility.ChangeSecurity(a, ac, pc, onPaper: true) || ac.security != pc || a.ownership.OwnedBed != bedA) InsideFail("a reclassification that cannot wait did not go through on paper, with her in her old bed");
                if (!LiveMessages().Any(m => m.text == stays)) InsideFail("a reclassification on paper into a wing kept for the other sex told the warden nothing");
                ac.security = sec0;
                // A woman, a wing for women (and for all): room.
                foreach (WingGender policy in new[] { WingGender.Women, WingGender.All })
                {
                    g.wingGenders[pc] = policy;
                    if (!InmateUtility.HasRoomIn(a, pc)) InsideFail($"a woman had no room in a protective custody wing set to {policy}, with a bed free there");
                }
            });
        }

        // ---- M1: carried-over inmates of the sex a level does not take ----------------------------------------------------------------------------------

        /// <summary>
        /// Two women left aboard by a shuttle that ran out of time, the level set to men only, two a day wanted and beds free for men: the day's two come (men), and the women do not use up the quota by being skipped (the old loop
        /// counted each skip as one of the day's two, and every day after).
        /// </summary>
        private static void CheckInsideCarriedOverOfTheOtherSexDoNotEatTheQuota(Map map)
        {
            Guarded("inside: carried-over inmates of the other sex do not use up the quota", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                InsideUnlock(g, r, level);
                InsideFreeBeds(map, r, level, 6);
                List<Building_Bed> offered = IntakeSystem.FreePrisonerBeds(map, level);
                if (offered.Count < 4 || offered.Any(x => IntakeSystem.WingOf(x) == null) || SpecialMeasures.IntakeLimit < 2)
                {
                    RPLog.Message($"[RimPrison:test] inside: carried-over of the other sex not checked (free beds at this level {offered.Count}, some in no wing {offered.Any(x => IntakeSystem.WingOf(x) == null)}, limit {SpecialMeasures.IntakeLimit})");
                    return;
                }
                InsideQuietIntake(g, r);
                InsidePolicyBack(g, r);
                g.wingGenders[level] = WingGender.Men;
                g.intake.perDay[level] = 2;
                Pawn w1 = InsideCarriedOver(g, r, level, Gender.Female), w2 = InsideCarriedOver(g, r, level, Gender.Female);
                List<Pawn> roster = IntakeSystem.GenerateRoster(g, map, out _, out _, 8);
                InsideDiscardLater(r, roster.Where(x => x != w1 && x != w2));
                if (roster.Count != 2 || roster.Any(x => x.gender != Gender.Male || x == w1 || x == w2))
                    InsideFail($"two carried-over women at a level for men only used up the quota: the roster is {roster.Count} ({string.Join(", ", roster.Select(x => x.gender))}), 2 men expected");
                if (!g.shuttle.carriedOver.Contains(w1) || !g.shuttle.carriedOver.Contains(w2)) InsideFail("a carried-over woman who waits for a bed of hers was taken off the list on the first day");
                // The same day again: still waiting. A day on: sent to another prison, with a message naming her and the wing. The one who is not yet a day: waits.
                int today = Ledger.CurrentDay;
                roster = IntakeSystem.GenerateRoster(g, map, out _, out _, 8);
                InsideDiscardLater(r, roster.Where(x => x != w1 && x != w2));
                if (!g.shuttle.carriedOver.Contains(w1) || !g.shuttle.carriedOver.Contains(w2) || roster.Count != 2) InsideFail($"two carried-over women on the same day a second time: waiting {g.shuttle.carriedOver.Count(x => x == w1 || x == w2)}, roster {roster.Count}");
                string sent = "RP_CarriedSentElsewhere".Translate(w1.LabelShort, level.label, "RP_WingWho_Men".Translate()).ToString().CapitalizeFirst();
                LiveMessages().RemoveAll(m => m.text == sent);
                w1.Inmate().carriedSkipDay = today - 1;
                roster = IntakeSystem.GenerateRoster(g, map, out _, out _, 8);
                InsideDiscardLater(r, roster.Where(x => x != w1 && x != w2));
                if (g.shuttle.carriedOver.Contains(w1) || !g.shuttle.carriedOver.Contains(w2) || roster.Count != 2) InsideFail($"a carried-over woman a day on: still carried over {g.shuttle.carriedOver.Contains(w1)}, the other one {g.shuttle.carriedOver.Contains(w2)}, roster {roster.Count} (2 men)");
                if (!LiveMessages().Any(m => m.text == sent)) InsideFail("a woman sent to another prison for want of a wing that takes her was sent with no message naming her");
                // The wing set back to all: the one still waiting comes first, before anyone generated, and her wait is forgotten.
                g.wingGenders[level] = WingGender.All;
                roster = IntakeSystem.GenerateRoster(g, map, out _, out _, 8);
                InsideDiscardLater(r, roster.Where(x => x != w1 && x != w2));
                if (!roster.Contains(w2) || w2.Inmate().carriedSkipDay != -1 || roster.Count != 2) InsideFail($"with the wing set back to all, the woman who waited came {roster.Contains(w2)} (wait forgotten {w2.Inmate().carriedSkipDay == -1}), roster {roster.Count}");
            });
        }

        // ---- M2: an overcrowded roster for a single-sex level -------------------------------------------------------------------------------------------

        /// <summary>
        /// With the level full and overcrowding allowed, a level kept for women takes women: the old roster fell back to the generator's own mix when no bed was free, and the other sex arrived at a level whose only wing would never take them.
        /// </summary>
        private static void CheckInsideOvercrowdedSingleSexLevelTakesThatSex(Map map)
        {
            Guarded("inside: an overcrowded level kept for one sex", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                SecurityLevelDef level = InsideFullLevel(map, g, r);
                int Room() => Mathf.CeilToInt(IntakeSystem.Capacity(map) * 1.25f) - ShuttleSystem.InmateCount(map);
                // (The overcrowding the state allows is a quarter on top of the beds: spare beds in the cells of other wings make room for a dozen, and give this level none.)
                if (level != null)
                    foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
                    {
                        if (Room() >= 12) break;
                        if (p.Inmate() == null || p.ownership?.OwnedBed is not { } own || IntakeSystem.WingOf(own) is not { } wing || wing == level || own.GetRoom() is not { } cell) continue;
                        InsideSpareBed(map, cell, r);
                    }
                int room = Room();
                int want = Math.Min(12, room);
                if (level == null || want < 8 || SpecialMeasures.IntakeLimit < want || IntakeSystem.FreeBedCount(map, level) != 0)
                {
                    RPLog.Message($"[RimPrison:test] inside: the overcrowded single-sex roster was not checked (a level with no free bed: {level?.defName}, room {room}, limit {SpecialMeasures.IntakeLimit})");
                    return;
                }
                InsideQuietIntake(g, r);
                InsidePolicyBack(g, r);
                g.wingGenders[level] = WingGender.Women;
                g.intake.perDay[level] = want;
                g.intake.allowOvercrowding = true;
                List<Pawn> roster = IntakeSystem.GenerateRoster(g, map, out _, out _, 20);
                InsideDiscardLater(r, roster);
                if (roster.Count != want || roster.Any(x => x.gender != Gender.Female))
                    InsideFail($"a full level kept for women, overcrowding allowed: {roster.Count} came of {want} wanted, {roster.Count(x => x.gender == Gender.Male)} of them men");
            });
        }

        /// <summary>An arrival with no bed in a level kept for the other sex (the warden changed the policy after the roster was made) steps off with a message naming them and the wing.</summary>
        private static void CheckInsideArrivalWithNoBedOfTheirSexIsToldOf(Map map)
        {
            Guarded("inside: an arrival with no bed of their sex is told of", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                SecurityLevelDef level = InsideFullLevel(map, g, r);
                if (level == null)
                {
                    RPLog.Message("[RimPrison:test] inside: arrival with no bed of their sex not checked (no level with no free bed)");
                    return;
                }
                InsidePolicyBack(g, r);
                Pawn woman = Throwaway(map, r, level, spawn: false);
                woman.gender = Gender.Female;
                g.wingGenders[level] = WingGender.Men;
                // (Shown with its first letter capitalised, and it starts with her name: "van der Wal" is "Van der Wal" in the message.)
                string told = "RP_ArrivalNoBedForSex".Translate(woman.LabelShort, level.label, "RP_WingWho_Men".Translate()).ToString().CapitalizeFirst();
                LiveMessages().RemoveAll(m => m.text == told);
                IntakeSystem.AdmitArrival(g, map, woman, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), fromShuttle: false);
                if (woman.ownership?.OwnedBed != null) InsideFail("an arrival of the wrong sex for a full level kept for men was given a bed");
                if (!LiveMessages().Any(m => m.text == told)) InsideFail("an arrival who could have no bed because the level's wing is kept for the other sex came with no message naming her and the wing");
            });
        }

        /// <summary>
        /// An inmate with no bed, of a sex the level's free beds are not for, asks the game for no list of free beds every hour: the old gender-blind count said there were free beds, and a shuffled list was built (and the game's random
        /// numbers used) for nothing, for every such inmate every hour. The probe is the random numbers: asking for nothing leaves them as they were.
        /// </summary>
        private static void CheckInsideBedlessInmateOfTheOtherSexAsksNoBedList(Map map)
        {
            Guarded("inside: a bedless inmate of the other sex asks for no bed list", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                (Pawn a, Pawn b) = CellsPair(map);
                if (a == null)
                {
                    RPLog.Message("[RimPrison:test] inside: bedless inmate of the other sex skipped (no two inmates in different cells)");
                    return;
                }
                CompInmate ac = a.Inmate();
                SecurityLevelDef level = ac.security;
                Building_Bed bedA = a.ownership.OwnedBed;
                InsideFreeBeds(map, r, level, 4);
                List<Building_Bed> offered = IntakeSystem.FreePrisonerBeds(map, level);
                if (offered.Count < 3 || offered.Any(x => IntakeSystem.WingOf(x) == null))
                {
                    RPLog.Message($"[RimPrison:test] inside: bedless inmate of the other sex not checked (free beds {offered.Count}, some in no wing {offered.Any(x => IntakeSystem.WingOf(x) == null)})");
                    return;
                }
                InsidePolicyBack(g, r);
                Gender gender0 = a.gender;
                Building_Bed home0 = ac.homeBed;
                r.Add(() =>
                {
                    a.gender = gender0;
                    ac.homeBed = home0;
                    a.ownership.UnclaimBed();
                    a.ownership.ClaimBedIfNonMedical(bedA);
                    Cellmates.InvalidateCells();
                });
                a.gender = Gender.Female;
                g.wingGenders[level] = WingGender.Men;
                a.ownership.UnclaimBed();
                ac.homeBed = null;
                int expected, actual;
                Rand.PushState(777);
                expected = Rand.Int;
                Rand.PopState();
                Rand.PushState(777);
                InmateUtility.TryReassignBed(a, ac);
                actual = Rand.Int;
                Rand.PopState();
                if (a.ownership.OwnedBed != null) InsideFail("a woman with no bed was given one in a wing kept for men");
                if (actual != expected) InsideFail("a bedless woman, with free beds only in a wing kept for men, still had a list of free beds built and shuffled for her (the random numbers moved)");
            });
        }

        // ---- M3: events that admit inmates straight into a bed -------------------------------------------------------------------------------------------

        /// <summary>
        /// The celebrity, the state's transfer request and the guard who carries a downed inmate to a bed all took a free bed whatever the wing policy said (and the first two a random sex). With the maximum wing kept for women: the celebrity
        /// is a woman in a bed of the wing (ten times, as the sex is a coin), the transfers are all women (three times four), and the carried woman is not given a bed of a wing kept for men.
        /// </summary>
        private static void CheckInsideEventAdmissionsHonourTheWingPolicy(Map map)
        {
            Guarded("inside: events admit by the wing policy", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                SecurityLevelDef max = Celebrities.Maximum;
                if (SpecialMeasures.IntakeLimit == 0)
                {
                    RPLog.Message("[RimPrison:test] inside: event admissions skipped (the state sends nobody)");
                    return;
                }
                InsideUnlock(g, r, max);
                InsideQuietIntake(g, r);
                InsidePolicyBack(g, r);
                var before = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
                r.Add(() =>
                {
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => !before.Contains(x)).ToList()) p.Destroy(DestroyMode.Vanish);
                });
                PrisonZoneDef wingMax = DefDatabase<PrisonZoneDef>.GetNamed("RP_WingMaximum");
                int free = InsidePaintedBeds(map, r, pm, wingMax, max, 4);
                if (free < 4)
                {
                    RPLog.Message($"[RimPrison:test] inside: event admissions not checked (only {free} free maximum beds could be made)");
                    return;
                }
                g.wingGenders[max] = WingGender.Women;
                // The celebrity.
                for (int i = 0; i < 10; i++)
                {
                    Pawn star = Celebrities.Admit(map, "RP_Chr_AdmittedCelebrity", 4);
                    if (star == null)
                    {
                        InsideFail("the celebrity found no bed in a maximum wing kept for women with free beds");
                        break;
                    }
                    if (star.gender != Gender.Female || IntakeSystem.WingOf(star.ownership?.OwnedBed) != max)
                    {
                        InsideFail($"a celebrity admitted to a maximum wing kept for women is {star.gender}, in a bed of the {IntakeSystem.WingOf(star.ownership?.OwnedBed)?.defName ?? "no"} wing");
                        star.Destroy(DestroyMode.Vanish);
                        break;
                    }
                    star.Destroy(DestroyMode.Vanish);
                }
                // The transfer request, three times four.
                for (int round = 0; round < 3; round++)
                {
                    var known = new HashSet<Pawn>(map.mapPawns.PrisonersOfColonySpawned);
                    ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_TransferRequest", "transfer", "four transfers", map);
                    letter.amount = 4;
                    letter.money = 4000;
                    new PrisonDecisionWorker_TransferRequest().Accept(letter, map);
                    List<Pawn> arrived = map.mapPawns.PrisonersOfColonySpawned.Where(x => !known.Contains(x)).ToList();
                    if (arrived.Count == 0) InsideFail("a transfer request put nobody in the free maximum beds");
                    if (arrived.Any(x => x.gender != Gender.Female || IntakeSystem.WingOf(x.ownership?.OwnedBed) != max))
                        InsideFail($"a transfer request put {arrived.Count(x => x.gender != Gender.Female)} man(men) of {arrived.Count} in a maximum wing kept for women");
                    foreach (Pawn p in arrived) p.Destroy(DestroyMode.Vanish);
                }
                // The guard who carries a downed woman to a bed: not to a bed of a wing kept for men.
                g.wingGenders[max] = WingGender.Men;
                (Pawn a, Pawn b) = CellsPair(map);
                if (a == null) return;
                CompInmate ac = a.Inmate();
                (SecurityLevelDef sec0, Gender gender0, Building_Bed bedA) = (ac.security, a.gender, a.ownership.OwnedBed);
                r.Add(() =>
                {
                    (ac.security, a.gender) = (sec0, gender0);
                    a.ownership.UnclaimBed();
                    a.ownership.ClaimBedIfNonMedical(bedA);
                    Cellmates.InvalidateCells();
                });
                ac.security = max;
                a.gender = Gender.Female;
                a.ownership.UnclaimBed();
                Pawn carrier = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                IntVec3 near = pm.zones.CellsOf(wingMax).FirstOrDefault(x => x.Standable(map));
                if (near.IsValid && near != default)
                {
                    carrier.Position = near;
                    carrier.Notify_Teleported(true, true);
                }
                System.Reflection.MethodInfo bedFor = typeof(WorkGiver_RescueInmate).GetMethod("BedFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (bedFor == null)
                {
                    InsideFail("the rescue work giver's BedFor was not found to test");
                    return;
                }
                var given = (Building_Bed)bedFor.Invoke(null, new object[] { a, carrier });
                if (given != null && !given.Medical && IntakeSystem.WingOf(given) == max)
                    InsideFail("a downed woman was carried to a bed of a wing kept for men");
            });
        }

        // ---- M4: moving out the other sex from a wing now kept for one ---------------------------------------------------------------------------------

        /// <summary>
        /// Four men in a wing the warden has just set to women only, four beds in no wing free: one daily review moves all four (the old review counted them in the two moves a day it has, and they went first, taking the places of the cells
        /// with rival gangs for as long as they were stranded).
        /// </summary>
        private static void CheckInsideWrongWingMovesHaveAQuotaOfTheirOwn(Map map)
        {
            Guarded("inside: wrong-wing moves have a quota of their own", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                int now = Find.TickManager.TicksGame;
                // The four: active inmates with a bed of their own in one wing (each in a cell of their own as far as there are cells), the rest of the prison held where it is.
                List<Pawn> able = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { } xc && InmateUtility.IsActiveInmate(x) && !x.Downed && !x.InMentalState && xc.forcedActivity == null
                    && DeathRowSystem.Ext(xc) == null && x.ownership?.OwnedBed is { } xb && !PunishmentUtility.IsSolitaryBed(xb) && xb.GetRoom() != null && IntakeSystem.WingOf(xb) is { } xw && xw == xc.security).ToList();
                SecurityLevelDef wing = able.GroupBy(x => IntakeSystem.WingOf(x.ownership.OwnedBed)).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
                List<Pawn> four = wing == null ? new List<Pawn>() : able.Where(x => IntakeSystem.WingOf(x.ownership.OwnedBed) == wing).Take(4).ToList();
                if (four.Count < 4 || wing.wingOnly)
                {
                    RPLog.Message($"[RimPrison:test] inside: wrong-wing quota not checked ({four.Count} inmates in one wing)");
                    return;
                }
                InsidePolicyBack(g, r);
                var held = map.mapPawns.PrisonersOfColony.Where(x => x.Inmate() != null).Select(x => (c: x.Inmate(), tick: x.Inmate().wardenPlacedTick, gender: x.gender, pawn: x)).ToList();
                r.Add(() =>
                {
                    foreach ((CompInmate c, int tick, Gender gender, Pawn pawn) in held)
                    {
                        c.wardenPlacedTick = tick;
                        pawn.gender = gender;
                    }
                });
                var beds0 = four.ToDictionary(x => x, x => x.ownership.OwnedBed);
                r.Add(() =>
                {
                    foreach (Pawn p in four)
                    {
                        p.ownership.UnclaimBed();
                        p.ownership.ClaimBedIfNonMedical(beds0[p]);
                    }
                    Cellmates.InvalidateCells();
                });
                foreach ((CompInmate c, int _, Gender _, Pawn pawn) in held) c.wardenPlacedTick = now;
                foreach (Pawn p in four)
                {
                    p.gender = Gender.Male;
                    p.Inmate().wardenPlacedTick = -999999;
                }
                // The wing is set to women only: the four men are stranded, and the Intake tab's count says so (and that none of them has a bed to go to yet).
                g.wingGenders[wing] = WingGender.Women;
                Cellmates.InvalidateCells();
                int stranded0 = Cellmates.InWrongWing(map, out int nowhere0);
                bool noOpenBeds = IntakeSystem.FreeBedCount(map, wing, gender: Gender.Male) == 0;
                // (Every other man of the wing is stranded too, with a bed to go to or not by the level on his own file: the four are the ones counted on.)
                if (stranded0 < 4 || noOpenBeds && nowhere0 < 4) InsideFail($"four men in a wing just set to women only: the Intake tab counts {stranded0} stranded, {nowhere0} with nowhere to go (4 or more of each, the four having none while no bed is free)");
                // Beds in no wing: rooms of inmates who are not among the four, painted over, with a spare bed each, until the four can all go.
                int open = 0;
                var rooms = new HashSet<Room>(four.Select(x => x.ownership.OwnedBed.GetRoom()));
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
                {
                    if (open >= 4) break;
                    if (p.Inmate() == null || p.ownership?.OwnedBed?.GetRoom() is not { } room || !rooms.Add(room)) continue;
                    InsideZone(pm, r, null, room.Cells);
                    while (IntakeSystem.FreeBedCount(map, wing, gender: Gender.Male) < 4 && InsideSpareBed(map, room, r) != null) { }
                    open = IntakeSystem.FreeBedCount(map, wing, gender: Gender.Male);
                }
                open = IntakeSystem.FreeBedCount(map, wing, gender: Gender.Male);
                if (open < 4)
                {
                    RPLog.Message($"[RimPrison:test] inside: wrong-wing quota not checked (only {open} beds in no wing could be made)");
                    return;
                }
                Cellmates.InvalidateCells();
                // (Counted again: a room painted over for its spare bed may be that of another man the wing now refuses, who is then in a bed of no wing and not stranded any more.)
                int stranded0b = Cellmates.InWrongWing(map, out _);
                int moved = Cellmates.DailyReview(map);
                int settled = four.Count(x => IntakeSystem.WingOf(x.ownership.OwnedBed) == null);
                if (moved != 4 || settled != 4) InsideFail($"four men in a wing kept for women, four open beds: one daily review moved {moved} and {settled} of the four are in a bed of no wing (4 expected)");
                int stranded1 = Cellmates.InWrongWing(map, out _);
                if (stranded1 != stranded0b - 4) InsideFail($"the Intake tab counts {stranded1} still to move after the review moved four of {stranded0b} ({stranded0} before the rooms were painted)");
            });
        }

        // ---- M5: the garden's link, per gardener ---------------------------------------------------------------------------------------------------------

        /// <summary>A gardener inside for smokeleaf growing, a courier in the ring, with the garden painted around them (everything put back).</summary>
        private static Pawn InsideGardener(RingScene s, StoryArc ring, string name, bool courier = true)
        {
            Pawn p = s.Inmate(name);
            CompInmate c = p.Inmate();
            (InmateJobDef job0, CrimeDef crime0) = (c.job, c.crime);
            s.r.Add(() => (c.job, c.crime) = (job0, crime0));
            (c.job, c.crime) = (DefDatabase<InmateJobDef>.GetNamed("RP_JobGarden"), RPContrabandDefOf.RP_Crime_SmokeleafGrowing);
            InsideZone(s.pm, s.r, RPLaborDefOf.RP_Garden, GenRadial.RadialCellsAround(p.Position, 3.5f, true).Where(x => x.InBounds(s.map)));
            if (courier && ring != null) ring.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = p.thingIDNumber, name = p.LabelShort });
            return p;
        }

        /// <summary>
        /// Two gardeners in one ring each have the garden as their own link (the old ring shared the first's, so the second's harvests fed the first's link, finding the second's patch revealed nothing, and a bust that took out "the
        /// garden" cleared the first's patch only).
        /// </summary>
        private static void CheckInsideTwoGardenersHaveALinkEach(Map map)
        {
            Guarded("inside: two gardeners in one ring", map, r =>
            {
                var s = new RingScene(map, r);
                PrisonMap pm = s.pm;
                InsideOwnPatches(pm, r);
                Pawn dealer = s.Inmate("Zdealer851", dealer: true);
                StoryArc ring = s.Form(dealer);
                s.Assess(dealer);
                Pawn g1 = InsideGardener(s, ring, "Zgardener851a"), g2 = InsideGardener(s, ring, "Zgardener851b");
                CompInmate c1 = g1.Inmate(), c2 = g2.Inmate();
                pm.Guards.Clear();
                GardenPatches.Notify_Gardening(g1, c1, pm, force: true);
                GardenPatches.Notify_Gardening(g2, c2, pm, force: true);
                RingSystem.Notify_BroughtIn(g1, RingSystem.GardenSource, RPContrabandDefOf.SmokeleafJoint);
                RingSystem.Notify_BroughtIn(g2, RingSystem.GardenSource, RPContrabandDefOf.SmokeleafJoint);
                List<RingLink> gardens = ring.ring.links.Where(RingSystem.IsGarden).ToList();
                if (gardens.Count != 2 || !gardens.Exists(l => l.pawnId == g1.thingIDNumber) || !gardens.Exists(l => l.pawnId == g2.thingIDNumber))
                    InsideFail($"two gardeners carrying for one ring: {gardens.Count} garden link(s) ({string.Join(", ", gardens.Select(l => l.pawnId))}), one each expected");
                // The second gardener's patch is found by a cell search: their garden link is the one learned.
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                if (GardenPatches.Search(guard, g2, c2, 1f) != 1) InsideFail("a cell search of the second gardener's cell did not find their patch");
                RingLink second = gardens.Find(l => l.pawnId == g2.thingIDNumber), first = gardens.Find(l => l.pawnId == g1.thingIDNumber);
                if (second is not { known: true } || first is { known: true }) InsideFail($"finding the second gardener's patch showed their garden link {second?.known} and the first's {first?.known} (their own, and not the other's)");
                // A bust that takes out the gardens takes out both patches.
                GardenPatches.Notify_Gardening(g2, c2, pm, force: true);
                RingSystem.Notify_Assessed(g1, 1f);
                RingSystem.Notify_Assessed(g2, 1f);
                foreach (RingLink l in gardens)
                {
                    l.known = true;
                    l.handled = false;
                }
                if (pm.patches.Count != 2) InsideFail($"the check's set-up failed: {pm.patches.Count} patches");
                RingSystem.Bust(ring, map);
                if (pm.patches.Count != 0) InsideFail($"a bust that took out the gardens of two gardeners left {pm.patches.Count} patch(es) in the garden");
            });
        }

        /// <summary>
        /// A gardener who leaves takes their garden link with them (the old ring handed a known one to the dealer: "the garden" stayed in the chain for good, a bust took it out with nothing to clear, and the next gardener's patch
        /// fed that stale link, so finding it revealed nothing).
        /// </summary>
        private static void CheckInsideGardenerLeavingTakesTheGardenLinkWithThem(Map map)
        {
            Guarded("inside: a gardener leaves with a known garden link", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                var s = new RingScene(map, r);
                PrisonMap pm = s.pm;
                InsideOwnPatches(pm, r);
                Pawn dealer = s.Inmate("Zdealer852", dealer: true);
                StoryArc ring = s.Form(dealer);
                s.Assess(dealer);
                Pawn g1 = InsideGardener(s, ring, "Zgardener852a");
                CompInmate c1 = g1.Inmate();
                pm.Guards.Clear();
                GardenPatches.Notify_Gardening(g1, c1, pm, force: true);
                RingSystem.Notify_BroughtIn(g1, RingSystem.GardenSource, RPContrabandDefOf.SmokeleafJoint);
                RingLink garden = ring.ring.links.Find(RingSystem.IsGarden);
                if (garden == null)
                {
                    InsideFail("the check's set-up failed: no garden link");
                    return;
                }
                garden.known = true;
                g.story.Notify_Leaving(g1);
                if (ring.ring.links.Any(RingSystem.IsGarden)) InsideFail($"the garden link stayed in the ring after its gardener left (now the link of {ring.ring.links.Find(RingSystem.IsGarden)?.pawnId}, the dealer is {dealer.thingIDNumber})");
                if (RingSystem.Chain(ring).Any(p => p.link != null && RingSystem.IsGarden(p.link))) InsideFail("the chain still shows the garden after its gardener left");
                // The next gardener's patch feeds a link of their own, and finding it shows that.
                Pawn g3 = InsideGardener(s, ring, "Zgardener852b");
                CompInmate c3 = g3.Inmate();
                GardenPatches.Notify_Gardening(g3, c3, pm, force: true);
                RingSystem.Notify_BroughtIn(g3, RingSystem.GardenSource, RPContrabandDefOf.SmokeleafJoint);
                RingLink next = ring.ring.links.Find(RingSystem.IsGarden);
                if (next == null || next.pawnId != g3.thingIDNumber) InsideFail($"the next gardener's harvest fed a link of {next?.pawnId} (their own is {g3.thingIDNumber})");
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                GardenPatches.Search(guard, g3, c3, 1f);
                if (next is not { known: true }) InsideFail("finding the next gardener's patch did not show the garden's link");
            });
        }

        // ---- M6: the harvest is at the patch, by the gardener's work --------------------------------------------------------------------------------------

        /// <summary>
        /// A ripe patch is harvested by its gardener at their garden job, near the patch, and not by any work anywhere (the old rule: any job that counts as work, and the watch kept where the inmate was): a gardener moved to the
        /// laundry harvested at the far end of the prison; a gardener at their job but far from the patch harvested too.
        /// </summary>
        private static void CheckInsideHarvestIsAtThePatchByTheGardenersWork(Map map)
        {
            Guarded("inside: the harvest is at the patch, by the gardener's work", map, r =>
            {
                var s = new RingScene(map, r);
                PrisonMap pm = s.pm;
                InsideOwnPatches(pm, r);
                Pawn gardener = InsideGardener(s, null, "Zgardener853", courier: false);
                CompInmate c = gardener.Inmate();
                pm.Guards.Clear();
                GardenPatches.Notify_Gardening(gardener, c, pm, force: true);
                GardenPatch patch = GardenPatches.PatchOf(pm, gardener);
                if (patch == null)
                {
                    InsideFail("the check's set-up failed: no patch");
                    return;
                }
                // (The real staff are set aside: a staff gardener or a guard about the place finds a patch at their own odds, an hour's pass here is that pass, and the patch pulled up is no harvest.)
                foreach (CompStaff real in map.mapPawns.FreeColonistsSpawned.Select(x => x.GetComp<CompStaff>()).Where(x => x?.role != null).ToList())
                {
                    StaffRoleDef role0 = real.role;
                    r.Add(() => real.role = role0);
                    real.role = null;
                }
                Job job0 = gardener.jobs.curJob;
                r.Add(() => gardener.jobs.curJob = job0);
                int FarFrom(IntVec3 cell) => cell.x + 40 < map.Size.x ? cell.x + 40 : cell.x - 40;
                IntVec3 far = new IntVec3(FarFrom(gardener.Position), 0, gardener.Position.z);
                gardener.jobs.curJob = JobMaker.MakeJob(JobDefOf.Clean);
                // Moved to cleaning (the far end of the prison): ripe, unwatched, at work, and the patch is not there.
                InmateJobDef cleaning = DefDatabase<InmateJobDef>.GetNamed("RP_JobCleaning");
                InmateJobDef garden = c.job;
                c.job = cleaning;
                patch.cell = far;
                patch.sownTick = Find.TickManager.TicksGame - GardenPatches.RipeTicks;
                GardenPatches.Hourly(map, pm);
                if (patch.harvests != 0 || RingSystem.DrugsCarried(gardener) != 0) InsideFail("a gardener moved to another job harvested the patch while they cleaned, nowhere near it");
                // At their garden job, but nowhere near the patch.
                c.job = garden;
                GardenPatches.Hourly(map, pm);
                if (patch.harvests != 0 || RingSystem.DrugsCarried(gardener) != 0) InsideFail("a gardener at their garden job harvested a patch that was nowhere near them");
                // At their garden job, by the patch: harvested.
                patch.cell = gardener.Position;
                patch.sownTick = Find.TickManager.TicksGame - GardenPatches.RipeTicks;
                GardenPatches.Hourly(map, pm);
                if (patch.harvests != 1 || RingSystem.DrugsCarried(gardener) == 0 && !gardener.inventory.innerContainer.Any(x => x.def == RPContrabandDefOf.SmokeleafJoint)) InsideFail($"a gardener at their garden job, by a ripe patch, did not harvest it ({patch.harvests} harvests)");
                // A gardener whose garden job is gone does not keep the patch for ever: not worked for eight days, it is overgrown (and until then it is still there).
                c.job = null;
                patch.tendedTick = Find.TickManager.TicksGame - GardenPatches.OvergrownTicks + GenDate.TicksPerHour;
                GardenPatches.Hourly(map, pm);
                if (pm.patches.Count != 1) InsideFail("a patch of a gardener who had not worked the garden for just under eight days was overgrown");
                patch.tendedTick = Find.TickManager.TicksGame - GardenPatches.OvergrownTicks;
                GardenPatches.Hourly(map, pm);
                if (pm.patches.Count != 0) InsideFail("a patch of a gardener with no garden job for eight days is still there");
            });
        }

        // ---- L1: a harvest is not a smuggled thing -------------------------------------------------------------------------------------------------------

        /// <summary>The Statistics tab counts what was smuggled in; what the garden grows is made inside, and a count that rose by one every five days with no visitor, letter or delivery to explain it would hint at an inside source.</summary>
        private static void CheckInsideHarvestIsNotASmuggledThing(Map map)
        {
            Guarded("inside: a harvest is not counted as smuggled in", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                var s = new RingScene(map, r);
                PrisonMap pm = s.pm;
                InsideOwnPatches(pm, r);
                Pawn gardener = InsideGardener(s, null, "Zgardener854", courier: false);
                CompInmate c = gardener.Inmate();
                pm.Guards.Clear();
                GardenPatches.Notify_Gardening(gardener, c, pm, force: true);
                GardenPatch patch = GardenPatches.PatchOf(pm, gardener);
                if (patch == null)
                {
                    InsideFail("the check's set-up failed: no patch");
                    return;
                }
                int total = g.stats.contrabandSmuggled, today = g.today.contrabandSmuggled;
                if (!GardenPatches.Harvest(gardener, c, patch)) InsideFail("a ripe patch gave nothing to harvest");
                if (g.stats.contrabandSmuggled != total || g.today.contrabandSmuggled != today)
                    InsideFail($"a harvest raised what the Statistics tab calls smuggled in ({total} -> {g.stats.contrabandSmuggled}, today {today} -> {g.today.contrabandSmuggled})");
                // (Still the pockets' contraband: a frisk finds it.)
                if (!gardener.inventory.innerContainer.Any(x => x.def == RPContrabandDefOf.SmokeleafJoint)) InsideFail("the harvest left no joints in the gardener's pockets");
            });
        }

        // ---- L2: the marshals' prisoner who waited too long -------------------------------------------------------------------------------------------------

        /// <summary>A prisoner of the state's marshals who has waited the six days is sent to another prison, and the warden is told (a message and a chronicle line naming them); the old pass dropped them without a word.</summary>
        private static void CheckInsideMarshalsPrisonerWhoWaitedTooLongIsToldOf(Map map)
        {
            Guarded("inside: a marshals' prisoner who waited too long is told of", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                (Pawn runner, Pawn leaver) = MarshalsPair(map, r, g);
                g.shuttle.AddMarshal(leaver, MarshalKind.Returning, ParoleLife.NewOffence);
                g.shuttle.AddMarshal(runner, MarshalKind.Recaptured, escapedTick: Math.Max(1, Find.TickManager.TicksGame - 3 * GenDate.TicksPerDay));
                MarshalEntry waited = g.shuttle.MarshalOf(leaver);
                waited.sinceTick = Find.TickManager.TicksGame - (ShuttleSystem.MarshalWaitDays + 1) * GenDate.TicksPerDay;
                string told = "RP_MarshalsGaveUp".Translate(leaver.LabelShort, ShuttleSystem.MarshalWaitDays).ToString().CapitalizeFirst();
                LiveMessages().RemoveAll(m => m.text == told);
                int entries0 = r.NewEntries.Count();
                // (The Intake tab's count leaves the one who waited too long out, and changes nothing: a screen must not.)
                if (g.shuttle.MarshalsWaiting() != 1 || g.shuttle.marshals.Count != 2) InsideFail($"the Intake tab would count {g.shuttle.MarshalsWaiting()} prisoners of the marshals waiting (1: one has waited the six days), and the list has {g.shuttle.marshals.Count} (2: counting changes nothing)");
                g.shuttle.LivingMarshals();
                if (g.shuttle.IsMarshal(leaver) || !g.shuttle.IsMarshal(runner)) InsideFail("the one who waited past the limit was kept, or the one who had not was dropped");
                if (!LiveMessages().Any(m => m.text == told)) InsideFail("a prisoner of the marshals who waited the six days was dropped with no message");
                if (r.NewEntries.Count() <= entries0 || !r.NewEntries.Any(e => e.text == "RP_Chr_MarshalsGaveUp".Translate(leaver.LabelShort).ToString())) InsideFail("a prisoner of the marshals who waited the six days was dropped with no line in the chronicle");
            });
        }

        /// <summary>
        /// Under the state's special measures the shuttle brings a few new inmates a day (none once the term is extended), and the state's own prisoners are not among those it holds back or counts: with two of them waiting and
        /// two new inmates wanted, the first term's roster is the two and then two new ones; once the term is extended it is the two alone, and the new inmates are the ones held back. (The review's gap: never exercised with them aboard.)
        /// </summary>
        private static void CheckInsideSpecialMeasuresCapDoesNotHoldTheMarshalsPrisoners(Map map)
        {
            Guarded("inside: the special measures' cap and the marshals' prisoners", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                SecurityLevelDef sec = InmateUtility.DefaultSecurity;
                InsideUnlock(g, r, sec);
                if (InsideFreeBeds(map, r, sec, 6) < 5)
                {
                    RPLog.Message("[RimPrison:test] inside: the special measures' cap with the marshals' prisoners not checked (fewer than five free beds at the level)");
                    return;
                }
                InsideQuietIntake(g, r);
                (Pawn runner, Pawn leaver) = MarshalsPair(map, r, g);
                g.shuttle.AddMarshal(runner, MarshalKind.Recaptured, escapedTick: Math.Max(1, Find.TickManager.TicksGame - 3 * GenDate.TicksPerDay));
                g.shuttle.AddMarshal(leaver, MarshalKind.Returning, ParoleLife.NewOffence);
                g.intake.perDay[sec] = 2;
                StateOversight oversight0 = g.story.oversight;
                var oversight = new StateOversight { active = true, extensions = 0 };
                r.Add(() => g.story.oversight = oversight0);
                g.story.oversight = oversight;
                List<Pawn> first = IntakeSystem.GenerateRoster(g, map, out bool held, out int limit, 20);
                InsideDiscardLater(r, first.Where(x => x != runner && x != leaver));
                if (limit != SpecialMeasures.IntakeCapPerDay || first.Count != 4 || first[0] != runner || first[1] != leaver)
                    InsideFail($"first term of special measures, two of the state's own and two new inmates wanted: the roster is {first.Count} (marshals first {first.Count > 1 && first[0] == runner && first[1] == leaver}), limit {limit}, 4 expected");
                oversight.extensions = 1;
                List<Pawn> extended = IntakeSystem.GenerateRoster(g, map, out held, out limit, 20);
                InsideDiscardLater(r, extended.Where(x => x != runner && x != leaver));
                if (limit != 0 || extended.Count != 2 || extended[0] != runner || extended[1] != leaver || !held)
                    InsideFail($"special measures extended: the roster is {extended.Count} (the state's own two, nobody new), limit {limit}, new inmates held back {held}");
            });
        }

        // ---- L4: nothing made of the prison's contraband falls from the sky -----------------------------------------------------------------------------------

        /// <summary>The mash, the stolen tools and the cellphone are not for trade and are kept out of the vanilla cargo pod (the pod takes any item a trader can sell worth one to forty silver).</summary>
        private static void CheckInsideMadeInsideThingsAreNotTradeableOrPodContents(Map map)
        {
            Guarded("inside: contraband is not trade goods or pod contents", map, r =>
            {
                foreach (string name in new[] { "RP_HoochMash", "RP_Tools", "RP_Cellphone" })
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamed(name);
                    if (def.tradeability != Tradeability.None || def.tradeability.TraderCanSell() || !def.preventSpawningInResourcePod)
                        InsideFail($"{name} can be traded ({def.tradeability}) or fall in a cargo pod (kept out {def.preventSpawningInResourcePod})");
                }
            });
        }

        // ---- L5: a piece split off a lot of mash keeps the lot's clock ---------------------------------------------------------------------------------------

        /// <summary>A piece split off a stack of mash is as old as the stack (a split-off thing is made new, and the old comp stamped it with now).</summary>
        private static void CheckInsideMashKeepsItsStartWhenSplit(Map map)
        {
            Guarded("inside: split mash keeps its start", map, r =>
            {
                Thing mash = ThingMaker.MakeThing(RPContrabandDefOf.RP_HoochMash);
                mash.stackCount = 3;
                int started = Find.TickManager.TicksGame - 5000;
                mash.TryGetComp<CompHoochMash>().sinceTick = started;
                Thing piece = mash.SplitOff(1);
                int pieceStart = piece.TryGetComp<CompHoochMash>()?.sinceTick ?? -1;
                if (pieceStart != started) InsideFail($"a piece split off mash that had worked {Find.TickManager.TicksGame - started} ticks started over ({Find.TickManager.TicksGame - pieceStart} ticks)");
                piece.Destroy();
                mash.Destroy();
            });
        }
    }
}
