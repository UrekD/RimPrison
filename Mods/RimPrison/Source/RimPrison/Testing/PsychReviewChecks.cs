using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The checks that came with the review of the psych wing (called from <see cref="CheckPsychWing"/>): a break only in the padded cell and only of the kinds that harm no one else (H1), a padded cell that is in
    /// the wing (H2, M4), the gang exit that does not send an inmate to psychiatric care (H3), the break count (M1, M2), the way out of the wing (M3), the trait case that is kept (M5), the warning with no doctor and
    /// the quiet cases (M6, L2), the dose that another doctor can take (L4) and the intake that sends none (L7). Each is built so that it fails without its fix.
    /// </summary>
    public static partial class AutoTest
    {
        private static SecurityLevelDef PsychLevelNamed(string defName) => DefDatabase<SecurityLevelDef>.GetNamed(defName);

        // ---- H1: where a break comes ----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A patient who has run out of cover breaks only in their padded cell (the hours go on counting outside it), and never into a berserk or a rage (those are the mood's own); the reason the letter gives names
        /// whether the cover was on.
        /// </summary>
        private static void CheckPsychBreaksOnlyInTheCell(Map map)
        {
            Guarded("psych review: a break comes only in the padded cell, and not an aggressive one", map, r =>
            {
                const string check = "break place";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level;
                s.Project(true);
                s.RegimeAllDay(level, RPDefOf.RP_Lockup);
                Pawn a = s.Inmate("Zph1", level, s.padded, s.CellP);
                CompInmate c = a.Inmate();
                r.Add(() => a.mindState.mentalStateHandler.Reset());
                c.unmedicatedHours = PsychWing.GraceHours + 10;
                // Out in the corridor (the canteen, the showers, the yard: anywhere that is not their cell): no break in three hundred hours, and the hours counted all the same.
                s.Place(a, s.Corridor);
                for (int h = 0; h < 300 && !a.InMentalState; h++) PsychWing.Hourly(a, c);
                if (a.InMentalState || c.breakTicks.Count > 0) PsychFail(check, $"a patient with no cover broke outside their padded cell (mental {a.MentalStateDef?.defName}, in room {a.GetRoom()?.ID}, their cell {s.padded.GetRoom()?.ID})");
                if (c.unmedicatedHours < 300) PsychFail(check, $"the hours without cover were not counted outside the cell ({c.unmedicatedHours})");
                a.mindState.mentalStateHandler.Reset();
                // Back in the cell: the roll is made, and a break comes (the chance is 8% an hour).
                s.Place(a, s.CellP);
                int broke = -1;
                for (int h = 1; h <= 300 && broke < 0; h++)
                {
                    PsychWing.Hourly(a, c);
                    if (a.InMentalState) broke = h;
                }
                if (broke < 0) PsychFail(check, "no break came in 300 hours without cover in their padded cell (the check measures nothing)");
                else if (!PaddedCells.InOwnCell(a, out _)) PsychFail(check, "the break did not come in the cell");
                a.mindState.mentalStateHandler.Reset();
                // The kind: the ones that harm no one else. (Berserk and the rages are a third of the weight of the mood's breaks that a prisoner can have.)
                int started = 0, aggressive = 0;
                for (int i = 0; i < 80; i++)
                {
                    a.mindState.mentalStateHandler.Reset();
                    if (!PsychWing.StartBreak(a)) continue;
                    started++;
                    if (a.MentalStateDef?.IsAggro == true) aggressive++;
                }
                a.mindState.mentalStateHandler.Reset();
                if (started < 20) PsychFail(check, $"only {started} of 80 breaks could be started (the check measures nothing)");
                if (aggressive > 0) PsychFail(check, $"{aggressive} of {started} forced breaks were aggressive ones");
                // The reason: with the medication on it is not that it ran out.
                string uncovered = PsychWing.BreakReason(a);
                PsychWing.Give(null, a);
                string covered = PsychWing.BreakReason(a);
                if (uncovered == covered) PsychFail(check, $"the break's reason is the same with the cover on and off (\"{covered}\")");
            });
        }

        // ---- H2, M4: a padded cell is in the wing -----------------------------------------------------------------------------------------------

        /// <summary>The first free spot of three by four cells on open ground, away from the unit (nothing on it, no roof, no zone), or invalid.</summary>
        private static IntVec3 PsychFreeSpot(PsychScene s)
        {
            CellRect unit = new CellRect(s.origin.x, s.origin.z, PsychScene.Width, PsychScene.Height).ExpandedBy(3);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(s.origin, 30f, true))
            {
                var spot = new CellRect(c.x, c.z, 3, 4);
                if (!spot.InBounds(s.map) || spot.Overlaps(unit)) continue;
                bool open = true;
                foreach (IntVec3 x in spot)
                    if (!x.Walkable(s.map) || x.Roofed(s.map) || x.GetEdifice(s.map) != null || x.GetTerrain(s.map).IsWater || s.pm.zones.ZoneAt(x) != null
                        || x.GetThingList(s.map).Any(t => t is Pawn || t.def.category is ThingCategory.Item or ThingCategory.Building || t.def.IsBlueprint || t.def.IsFrame))
                    {
                        open = false;
                        break;
                    }
                if (!open) continue;
                foreach (IntVec3 x in spot)
                    foreach (Thing plant in x.GetThingList(s.map).Where(t => t.def.category == ThingCategory.Plant).ToList())
                        plant.Destroy(DestroyMode.Vanish);
                return c;
            }
            return IntVec3.Invalid;
        }

        /// <summary>
        /// A padded bed pads only a cell of the psych wing, and only for the inmate whose bed it is: not a room shared with another bed, not a room off the wing's paint, not outdoors (where the room is the whole outdoors),
        /// not for a member of staff in a break, not for another inmate in the cell. A padded bed off the wing houses no one. And asking is cheap (the outdoor room is not enumerated).
        /// </summary>
        private static void CheckPsychPaddedNeedsTheWing(Map map)
        {
            Guarded("psych review: a padded bed pads only a cell of the wing, for the one whose bed it is", map, r =>
            {
                const string check = "padded bed";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef medium = PsychLevelNamed("RP_Medium"), level = s.level;
                s.Project(true);
                s.RegimeAllDay(medium, RPDefOf.RP_Lockup);
                ThingDef paddedDef = RPPsychWingDefOf.RP_PaddedBed;
                MentalStateDef sick = MentalStateDefOf.Wander_Psychotic;
                Pawn b = s.Inmate("Zpw1", medium, s.plain, s.CellQ);
                r.Add(() => b.mindState.mentalStateHandler.Reset());
                b.mindState.mentalStateHandler.TryStartMentalState(sick, forced: true, causedByMood: true);
                if (!b.InMentalState) PsychFail(check, "the throwaway did not break");
                bool Harmed(Pawn by, Thing thing)
                {
                    thing.HitPoints = thing.MaxHitPoints;
                    thing.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 20f, instigator: by));
                    bool harmed = thing.HitPoints < thing.MaxHitPoints;
                    thing.HitPoints = thing.MaxHitPoints;
                    return harmed;
                }
                // 1. A padded bed in a room with a plain bed too (a dormitory, a shared cell): nothing is padded.
                Building_Bed extra = s.ExtraBed(paddedDef, s.At(6, 3));
                if (PaddedCells.IsPadded(s.chairQ.GetRoom())) PsychFail(check, "a room with a plain bed and a padded one is a padded cell");
                if (!Harmed(b, s.chairQ)) PsychFail(check, "a chair in a room with a padded bed beside a plain bed was shielded from an inmate in a break");
                // 2. A padded bed alone in its room but not in the wing (unpainted): not a cell, and it houses no one.
                // (The control: the plain bed, unclaimed, is a free bed for medium security.)
                b.ownership?.UnclaimBed();
                s.pm.Notify_BedsChanged();
                s.pm.ForceDoorUpdate();
                if (!IntakeSystem.FreePrisonerBeds(map, medium, null).Contains(s.plain)) PsychFail(check, "set-up: the plain bed is not a free bed for medium security (the check measures nothing)");
                s.TakeOut(s.plain);
                if (!extra.Spawned || extra.GetRoom() == null) PsychFail(check, "set-up: the padded bed is not in a room");
                if (IntakeSystem.FreePrisonerBeds(map, medium, null).Contains(extra)) PsychFail(check, "a padded bed outside the wing is a free bed for a medium security inmate");
                b.ownership.ClaimBedIfNonMedical(extra);
                if (b.ownership.OwnedBed != extra) PsychFail(check, "set-up: the padded bed is not theirs");
                if (PaddedCells.IsPadded(extra.GetRoom())) PsychFail(check, "a padded bed alone in a room off the wing's paint makes a padded cell");
                if (!Harmed(b, s.chairQ)) PsychFail(check, "a chair beside a padded bed that is not in the wing was shielded from an inmate in a break");
                // 3. In the wing, alone in the room, and their bed: padded (the control: the check measures something).
                s.PaintPlainCell(true);
                if (!PaddedCells.IsPadded(extra.GetRoom())) PsychFail(check, "a padded bed alone in a room of the wing is no padded cell");
                if (Harmed(b, s.chairQ)) PsychFail(check, "the control: a chair in a padded cell of the wing was hurt by its inmate in a break (the check measures nothing)");
                // 4. Another inmate in the cell, in a break: it is not theirs.
                Pawn x = s.Inmate("Zpw2", medium, null, s.At(7, 3));
                r.Add(() => x.mindState.mentalStateHandler.Reset());
                x.mindState.mentalStateHandler.TryStartMentalState(sick, forced: true, causedByMood: true);
                if (!Harmed(x, s.chairQ)) PsychFail(check, "a chair in a padded cell was shielded from another inmate in a break, not the one whose bed it is");
                // 5. A member of staff in a break in the cell: the padding is for the inmate.
                Pawn guard = s.Staff("Zpw3", "RP_Guard", s.At(7, 5));
                r.Add(() => guard.mindState.mentalStateHandler.Reset());
                guard.mindState.mentalStateHandler.TryStartMentalState(sick, forced: true, causedByMood: true);
                if (!guard.InMentalState) PsychFail(check, "the guard did not break");
                else if (!Harmed(guard, s.chairQ)) PsychFail(check, "a chair in a padded cell was shielded from a member of staff in a break");
                // 6. A fire lit in the cell by an inmate who is not at home in it starts; by the one whose cell it is, it does not.
                if (PaddedCells.ShieldsCell(x, s.At(7, 5), map)) PsychFail(check, "a fire another inmate lights in a padded cell is stopped");
                if (!PaddedCells.ShieldsCell(b, s.At(7, 5), map)) PsychFail(check, "a fire the inmate lights in their own padded cell is not stopped");
                // 7. Outdoors: a padded bed painted as the wing, alone, on open ground: the room is the whole outdoors, and nothing in it is padded.
                IntVec3 spot = PsychFreeSpot(s);
                if (!spot.IsValid) PsychFail(check, "no open ground for the outdoor padded bed");
                else
                {
                    s.Paint(new CellRect(spot.x, spot.z, 3, 4), RPPsychWingDefOf.RP_WingPsych);
                    Building_Bed outdoor = s.ExtraBed(paddedDef, spot);
                    Thing chair = s.Thing<Thing>(DefDatabase<ThingDef>.GetNamed("DiningChair"), ThingDefOf.WoodLog, spot + new IntVec3(1, 0, 1));
                    Pawn o = s.Inmate("Zpw4", medium, outdoor, spot + new IntVec3(1, 0, 2));
                    r.Add(() => o.mindState.mentalStateHandler.Reset());
                    o.mindState.mentalStateHandler.TryStartMentalState(sick, forced: true, causedByMood: true);
                    if (o.ownership?.OwnedBed != outdoor) PsychFail(check, "set-up: the outdoor padded bed is not theirs");
                    if (PaddedCells.IsPadded(outdoor.GetRoom())) PsychFail(check, "the outdoors is a padded cell");
                    if (!Harmed(o, chair)) PsychFail(check, "a chair outdoors beside a padded bed was shielded from an inmate in a break");
                    // (And the question is cheap: the outdoor room's things are not listed for each blow.)
                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < 400; i++) PaddedCells.Shields(o, chair);
                    watch.Stop();
                    RPLog.Message($"[RimPrison:test] psych review: 400 questions outdoors took {watch.ElapsedMilliseconds} ms");
                    if (watch.ElapsedMilliseconds > 40) PsychFail(check, $"400 questions of whether a blow outdoors is padded took {watch.ElapsedMilliseconds} ms (the outdoor room's things are listed each time)");
                }
            });
        }

        // ---- H3: the gang exit ---------------------------------------------------------------------------------------------------------------------

        /// <summary>An ex-member's "another wing" is never the psych wing (referral only), even for a maximum security inmate and with a padded cell free.</summary>
        private static void CheckPsychGangExitSkipsPsych(Map map)
        {
            Guarded("psych review: the gang exit's other wing is never psychiatric care", map, r =>
            {
                const string check = "gang exit";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level, maximum = PsychLevelNamed("RP_Maximum");
                s.Project(true);
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                if (gang == null)
                {
                    PsychFail(check, "no gang def");
                    return;
                }
                Pawn p = s.Inmate("Zpx1", maximum, s.plain, s.CellQ);
                CompInmate c = p.Inmate();
                c.exGang = gang;
                r.Add(() => c.exGang = null);
                if (!InmateUtility.HasRoomIn(p, level)) PsychFail(check, "set-up: no padded cell is free for the ex-member (the check measures nothing)");
                SecurityLevelDef wing = GangExit.OtherWing(p, c);
                if (wing != null && wing.referralOnly) PsychFail(check, $"the other wing for a maximum security ex-member is {wing.defName}, a level the psychologist alone sends to");
            });
        }

        // ---- M1, M2: what is counted -----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A break is counted when the prison saw it: a vandalism or an assault on staff that was seen, a mood break; not an unwatched one, not a riot, not a restraint resisted, and one incident is one break (an
        /// assault that becomes a hostage-taking, a temper on the heels of a mood break).
        /// </summary>
        private static void CheckPsychCountsWhatThePrisonSaw(Map map)
        {
            Guarded("psych review: the count has the breaks the prison saw, once each", map, r =>
            {
                const string check = "count";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef medium = PsychLevelNamed("RP_Medium");
                s.Project(true);
                Pawn psychologist = s.Staff("Zpsy5", "RP_Psychologist", s.Corridor);
                Pawn a = s.Inmate("Zpc1", medium, s.plain, s.CellQ);
                CompInmate c = a.Inmate();
                MisconductDef vandal = DefDatabase<MisconductDef>.GetNamed("RP_Vandalism"), assault = DefDatabase<MisconductDef>.GetNamed("RP_AssaultStaff");
                r.Add(() => a.mindState.mentalStateHandler.Reset());
                void Earlier()
                {
                    for (int i = 0; i < c.breakTicks.Count; i++) c.breakTicks[i] -= GenDate.TicksPerDay;
                    c.lastBreakTick -= GenDate.TicksPerDay;
                }
                // Nobody saw it (no guard, no camera): not a fact the prison has, whatever the count would make of it.
                MisconductUtility.Record(a, c, s.pm, vandal, chronicle: false, unseen: true);
                Earlier();
                MisconductUtility.Record(a, c, s.pm, vandal, chronicle: false, unseen: true);
                if (c.breakTicks.Count != 0) PsychFail(check, $"two vandalisms nobody saw were counted ({c.breakTicks.Count})");
                InmateCandidates.Shared.Invalidate();
                if (PsychWing.ReviewDue(c) || PsychologyUtility.WantsSession(c)) PsychFail(check, "the psychologist is sent for breaks the prison never saw");
                // A riot, and a follower in one, are a protest and not a break; a restraint resisted, and the states set up by the story are none.
                MisconductUtility.Record(a, c, s.pm, RPRiotDefOf.RP_Riot, evidence: true, chronicle: false);
                MisconductUtility.Record(a, c, s.pm, RPRiotDefOf.RP_RiotJoined, evidence: true, chronicle: false);
                MisconductUtility.Record(a, c, s.pm, RPHostageDefOf.RP_HostageTaking, evidence: true, chronicle: false);
                a.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Assaulting, null, forced: true, otherPawn: psychologist);
                a.mindState.mentalStateHandler.Reset();
                a.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
                a.mindState.mentalStateHandler.Reset();
                if (c.breakTicks.Count != 0) PsychFail(check, $"a riot, a hostage taken or a restraint resisted was counted ({c.breakTicks.Count})");
                // Seen: counted. The assault that becomes a hostage-taking, in the same hour, is the same incident.
                MisconductUtility.Record(a, c, s.pm, assault, evidence: true, chronicle: false);
                a.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
                a.mindState.mentalStateHandler.Reset();
                MisconductUtility.Record(a, c, s.pm, RPHostageDefOf.RP_HostageTaking, evidence: true, chronicle: false);
                if (c.breakTicks.Count != 1) PsychFail(check, $"an assault that became a hostage-taking was counted {c.breakTicks.Count} times (one incident)");
                // A mood break on the heels of it is the same incident; one the next day is another.
                a.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true, causedByMood: true);
                a.mindState.mentalStateHandler.Reset();
                if (c.breakTicks.Count != 1) PsychFail(check, $"a mood break an hour after a temper was counted as another ({c.breakTicks.Count})");
                Earlier();
                MisconductUtility.Record(a, c, s.pm, vandal, evidence: true, chronicle: false);
                if (c.breakTicks.Count != 2 || PsychWing.CaseFor(a, c) == null) PsychFail(check, $"two seen incidents a day apart were {c.breakTicks.Count} breaks, case \"{PsychWing.CaseFor(a, c)}\"");
            });
        }

        // ---- M3: the way out ----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A patient steps out after clean days that have no break in them, to the level they came from (not always medium); protective custody is not referred (it is where a witness is safe, and the wing has the
        /// general population in its yard).
        /// </summary>
        private static void CheckPsychStepsOutWhereTheyCameFrom(Map map)
        {
            Guarded("psych review: a patient steps out to where they came from, after days without a break", map, r =>
            {
                const string check = "step out";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level, minimum = PsychLevelNamed("RP_Minimum"), medium = PsychLevelNamed("RP_Medium"), protective = PsychLevelNamed("RP_Protective");
                s.Project(true);
                // (The clock is put on forty days for these questions, so that fourteen clean days have passed since they came; put back at once, no tick runs in between.)
                int clock0 = Find.TickManager.TicksGame;
                int now = clock0 + 40 * GenDate.TicksPerDay;
                Pawn a = s.Inmate("Zpo1", level, s.padded, s.CellP);
                CompInmate c = a.Inmate();
                SecurityLevelDef Move()
                {
                    Find.TickManager.DebugSetTicksGame(now);
                    try
                    {
                        return ReclassificationSystem.MoveFor(c, now, out _);
                    }
                    finally
                    {
                        Find.TickManager.DebugSetTicksGame(clock0);
                    }
                }
                (c.admittedTick, c.lastReclassTick, c.tension, c.psychFrom, c.lastBreakTick) = (1, now - 20 * GenDate.TicksPerDay, 0f, minimum, -999999);
                SecurityLevelDef to = Move();
                if (to != minimum) PsychFail(check, $"a patient who came from minimum security steps out to {to?.defName ?? "nowhere"} after 20 clean days");
                // A break inside the clean days: they stay (a break at day 17 of 20 is five days ago, and fourteen are needed).
                c.lastBreakTick = now - 5 * GenDate.TicksPerDay;
                if (Move() is { } early) PsychFail(check, $"a patient who broke five days ago steps out to {early.defName}");
                c.lastBreakTick = now - 15 * GenDate.TicksPerDay;
                if (Move() != minimum) PsychFail(check, "a patient whose last break was fifteen days ago does not step out");
                // Where they came from is not known (or is a level they cannot go back to): the level's own step.
                c.psychFrom = null;
                if (Move() != level.promoteTo) PsychFail(check, "a patient from no known level does not step out to the level's own step");
                c.psychFrom = level;
                if (Move() != level.promoteTo) PsychFail(check, "a patient 'from' psychiatric care steps out to it");
                now = clock0;
                // The referral records where they came from, and the move out clears it.
                Pawn b = s.Inmate("Zpo2", minimum, s.plain, s.CellQ);
                CompInmate bc = b.Inmate();
                bc.breakTicks.AddRange(new[] { now - 2 * GenDate.TicksPerDay, now - GenDate.TicksPerDay });
                a.ownership.UnclaimBed();
                c.security = medium;
                s.pm.Notify_BedsChanged();
                InmateCandidates.Shared.Invalidate();
                Pawn psychologist = s.Staff("Zpsy6", "RP_Psychologist", s.Corridor);
                PsychologyUtility.Complete(psychologist, b);
                if (bc.security != level) PsychFail(check, $"the referral did not move them ({bc.security?.defName})");
                else if (bc.psychFrom != minimum) PsychFail(check, $"the referral did not remember the level they came from (psychFrom {bc.psychFrom?.defName})");
                // Protective custody is not referred, however many breaks.
                Pawn w = s.Inmate("Zpo3", protective, s.plain, s.CellQ);
                w.ownership?.UnclaimBed();
                b.ownership?.UnclaimBed();
                bc.security = medium;
                s.pm.Notify_BedsChanged();
                CompInmate wc = w.Inmate();
                wc.breakTicks.AddRange(new[] { now - 2 * GenDate.TicksPerDay, now - GenDate.TicksPerDay });
                InmateCandidates.Shared.Invalidate();
                if (PsychWing.CannotMove(w, wc) == null || PsychWing.ReviewDue(wc)) PsychFail(check, "an inmate in protective custody can be referred to the psych wing");
                PsychologyUtility.Complete(psychologist, w);
                if (wc.security != protective) PsychFail(check, $"an inmate in protective custody was moved to {wc.security?.defName}");
            });
        }

        // ---- M5, L2: the trait case ---------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A trait found at the first assessment is a case until the inmate is moved: not lost when the project is not done or no cell is free at that session; no message when the project is not done, none for who
        /// cannot be moved at all, and the one about no free cell once in two days, not at every session.
        /// </summary>
        private static void CheckPsychTraitCaseKept(Map map)
        {
            Guarded("psych review: a trait case is kept until they are moved, and told once", map, r =>
            {
                const string check = "trait case";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level, medium = PsychLevelNamed("RP_Medium");
                TraitDef pyro = PsychWing.Ext?.traits.FirstOrDefault();
                if (pyro == null)
                {
                    PsychFail(check, "the level names no trait");
                    return;
                }
                Pawn psychologist = s.Staff("Zpsy7", "RP_Psychologist", s.Corridor);
                Pawn t = s.Inmate("Zpt1", medium, s.plain, s.CellQ);
                CompInmate tc = t.Inmate();
                t.story.traits.GainTrait(new Trait(pyro, 0, true));
                r.Add(() => t.story.traits.allTraits.RemoveAll(x => x.def == pyro));
                bool Said(string fragment) => LiveMessages().Any(m => m.text.Contains(fragment));
                // The project is not done at the first assessment: nothing happens, and nothing is said.
                s.Project(false);
                tc.assessed = false;
                Messages.Clear();
                PsychologyUtility.Complete(psychologist, t);
                if (tc.security != medium) PsychFail(check, "moved to psychiatric care without the project");
                if (Said("stay where they are")) PsychFail(check, "the warden is told of a case that cannot be acted on until the project is done");
                // The project comes: the case is still there, the psychologist is sent, and they are moved at the next session.
                s.Project(true);
                InmateCandidates.Shared.Invalidate();
                if (!PsychWing.ReviewDue(tc) || !PsychologyUtility.WantsSession(tc)) PsychFail(check, "a trait found before the project was done is no case once it is");
                PsychologyUtility.Complete(psychologist, t);
                if (tc.security != level) PsychFail(check, $"the trait case was lost: {tc.security?.defName} after the project was done");
                if (tc.psychTraitCase) PsychFail(check, "the trait case is still open after the move");
                // Moved out again: not sent back for what they are.
                t.ownership?.UnclaimBed();
                InmateUtility.ChangeSecurity(t, tc, medium);
                InmateCandidates.Shared.Invalidate();
                if (PsychWing.ReviewDue(tc) || PsychologyUtility.WantsSession(tc)) PsychFail(check, "a patient who stepped out is sent back for a trait");
                // No cell free at the first assessment: told once, not at every session; a case that waits for a cell.
                Pawn u = s.Inmate("Zpt2", medium, s.plain, s.CellQ);
                CompInmate uc = u.Inmate();
                u.story.traits.GainTrait(new Trait(pyro, 0, true));
                r.Add(() => u.story.traits.allTraits.RemoveAll(x => x.def == pyro));
                Pawn other = s.Inmate("Zpt3", level, s.padded, s.CellP);
                s.pm.Notify_BedsChanged();
                uc.assessed = false;
                Messages.Clear();
                PsychologyUtility.Complete(psychologist, u);
                if (!Said("stay where they are")) PsychFail(check, "the warden is not told that the psychologist would put them in psychiatric care and there is no cell");
                Messages.Clear();
                PsychologyUtility.Complete(psychologist, u);
                if (Said("stay where they are")) PsychFail(check, "the same message at the next session, the same day");
                // A trait found in a supermax inmate is nothing to tell the warden (they cannot be moved at all), and the case is kept.
                Pawn m = s.Inmate("Zpt4", PsychLevelNamed("RP_Supermax"), null, s.CellQ);
                CompInmate mc = m.Inmate();
                m.story.traits.GainTrait(new Trait(pyro, 0, true));
                r.Add(() => m.story.traits.allTraits.RemoveAll(x => x.def == pyro));
                mc.assessed = false;
                Messages.Clear();
                PsychologyUtility.Complete(psychologist, m);
                if (Said("stay where they are")) PsychFail(check, "the warden is told that a supermax inmate with a trait cannot be moved");
                other.ownership?.UnclaimBed();
                other.Inmate().security = medium;
            });
        }

        // ---- M6, L2: no one to make the rounds -----------------------------------------------------------------------------------------------

        /// <summary>A prison with patients and no doctor who can make the rounds (none hired, or all on leave) is told so at midnight; with one it is not.</summary>
        private static void CheckPsychMedicsAndMessages(Map map)
        {
            Guarded("psych review: no doctor for the rounds is told", map, r =>
            {
                const string check = "no medic";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                SecurityLevelDef level = s.level;
                s.Project(true);
                ThingDef drug = RPPsychWingDefOf.RP_Antipsychotic;
                // (The drug is in stock, and the supply company's pending orders are the check's own: nothing is said about the drug.)
                List<SupplyOrder> pending0 = g.supply.pending.ToList();
                r.Add(() =>
                {
                    g.supply.pending.Clear();
                    g.supply.pending.AddRange(pending0);
                });
                s.Drug(s.At(3, 1), 40);
                Pawn p = s.Inmate("Zpm2", level, s.padded, s.CellP);
                bool Said() => LiveMessages().Any(m => m.text.Contains("no doctor on the staff"));
                // Every real doctor on leave: the warning.
                foreach (Pawn real in map.mapPawns.FreeColonistsSpawned.Where(WorkGiver_MedicationRound.IsRoundDoctor).ToList())
                {
                    CompStaff sc = real.GetComp<CompStaff>();
                    int was = sc.leaveAtTick;
                    r.Add(() => sc.leaveAtTick = was);
                    sc.leaveAtTick = Find.TickManager.TicksGame;
                }
                Messages.Clear();
                PsychWing.Daily(map, g);
                if (!Said()) PsychFail(check, "a prison with a patient and no doctor who can make rounds is not told");
                // A doctor on the staff: nothing said.
                Pawn doctor = s.Staff("Zpd5", "RP_Doctor", s.At(3, 1));
                if (!PsychWing.AnyMedic(map)) PsychFail(check, "a doctor on site is not a medic for the rounds");
                Messages.Clear();
                PsychWing.Daily(map, g);
                if (Said()) PsychFail(check, "the warning came with a doctor on the staff");
                doctor.Destroy(DestroyMode.Vanish);
                // No patient: nothing said either.
                p.Inmate().security = PsychLevelNamed("RP_Medium");
                Messages.Clear();
                PsychWing.Daily(map, g);
                if (Said()) PsychFail(check, "the warning came with no patients");
            });
        }

        // ---- L4: a second doctor and the dose ---------------------------------------------------------------------------------------------------

        /// <summary>A doctor may take a dose from a stack that another has part of reserved (the round takes one).</summary>
        private static void CheckPsychDoseReservation(Map map)
        {
            Guarded("psych review: a second doctor takes a dose from a stack the first has begun", map, r =>
            {
                const string check = "dose";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                SecurityLevelDef level = s.level;
                s.Project(true);
                s.RegimeAllDay(level, RPDefOf.RP_Lockup);
                Pawn patient = s.Inmate("Zpr3", level, s.padded, s.CellP);
                Pawn first = s.Staff("Zpd3", "RP_Doctor", s.At(3, 1));
                Pawn second = s.Staff("Zpd4", "RP_Doctor", s.At(2, 1));
                QuietTheRealStaff(map, r, "Doctor");
                Thing dose = s.Drug(s.At(5, 1), 2);
                Job held = JobMaker.MakeJob(RPPsychWingDefOf.RP_MedicationRound, patient, dose);
                held.count = 1;
                r.Add(() => map.reservationManager.ReleaseAllClaimedBy(first));
                if (!map.reservationManager.Reserve(first, held, dose, WorkGiver_MedicationRound.DoseReservers, 1)) PsychFail(check, "set-up: the first doctor could not reserve one dose");
                InmateCandidates.Shared.Invalidate();
                if (WorkGiver_MedicationRound.DoseFor(second) != dose) PsychFail(check, "a second doctor finds no dose in a stack of two when the first has one of them reserved");
            });
        }

        // ---- L7: no intake ------------------------------------------------------------------------------------------------------------------------

        /// <summary>The state sends no one to psychiatric care, whatever the intake setting and however many padded cells are free.</summary>
        private static void CheckPsychNoIntakeForPsych(Map map)
        {
            Guarded("psych review: the intake roster has no one for psychiatric care", map, r =>
            {
                const string check = "intake";
                PsychScene s = PsychScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                SecurityLevelDef level = s.level;
                s.Project(true);
                var perDay0 = new Dictionary<SecurityLevelDef, int>(g.intake.perDay);
                r.Add(() =>
                {
                    g.intake.perDay.Clear();
                    foreach (KeyValuePair<SecurityLevelDef, int> kv in perDay0) g.intake.perDay[kv.Key] = kv.Value;
                });
                foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading) g.intake.perDay[sec] = 0;
                g.intake.perDay[level] = 3;
                if (IntakeSystem.FreeBedCount(map, level) < 1) PsychFail(check, "set-up: no padded cell is free (the check measures nothing)");
                g.intake.perDay[PsychLevelNamed("RP_Medium")] = 2;
                List<Pawn> roster = IntakeSystem.GenerateRoster(g, map, out _, out _, 10);
                if (roster.Count == 0) PsychFail(check, "no one on the roster at all, for medium security either (the check measures nothing)");
                foreach (Pawn made in roster)
                {
                    CompInmate mc = made.Inmate();
                    if (mc?.security == level) PsychFail(check, $"{made.LabelShort} was on the intake roster for psychiatric care");
                    if (!made.Spawned && !made.Destroyed && g.shuttle.LivingCarriedOver().Contains(made) == false && g.shuttle.LivingMarshals().All(m => m.pawn != made))
                        r.Add(() =>
                        {
                            if (Find.WorldPawns.Contains(made)) Find.WorldPawns.RemovePawn(made);
                            if (!made.Destroyed) made.Destroy(DestroyMode.Vanish);
                        });
                }
            });
        }

    }
}
