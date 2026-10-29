using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Checks for the murder trial (<see cref="Trials"/>) and the complaints against officers (<see cref="ComplaintSystem"/>). Each is its own method called once from
    /// FireAllEvents (<see cref="CheckCourtCases"/>), works on throwaway inmates and staff numbered from 91000 (with names of their own), sets the real trials and complaints
    /// aside while it runs, runs no ticks (the hourly passes are called by hand) and puts back what it touches (<see cref="Rollback"/>).
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The events plan's entry: its own dice (the same every run), and nothing left behind on the map.</summary>
        private static void CheckCourtCases(Map map)
        {
            Rand.PushState(20261022);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            int checksBegan = Find.TickManager.TicksGame; // (a real shuttle that lands while the checks tick brings inmates of its own: not theirs to put away)
            var thingsBefore = map.listerThings.AllThings.Count(t => t.def == ThingDef.Named("RP_SecurityCamera"));
            try
            {
                CheckTrialCollectsWhatWasSeen(map);
                CheckTrialCameraAndWeaponWaitForTheCase(map);
                CheckTrialSeenMurderConvicts(map);
                CheckTrialUnseenMurderWithRecantingWitnessesAcquits(map);
                CheckTrialInformantTestifiesAndIsExposed(map);
                CheckTrialProtectedWitnessIsNotGotAt(map);
                CheckTrialStaffVictim(map);
                CheckTrialHiddenFactsStayHidden(map);
                CheckTrialAssemblyAsksTheWarden(map);
                CheckTrialByVideoFromTheHearingRoom(map);
                CheckTrialPeopleWhoLeave(map);
                CheckComplaintTrueOneInvestigatedIsUpheld(map);
                CheckComplaintFalseOneInvestigatedIsFoundOut(map);
                CheckComplaintDismissedTrueOneFeedsTheLawsuit(map);
                CheckComplaintNoEvidenceIsADismissal(map);
                CheckComplaintDisciplineClimbs(map);
                CheckComplaintHiddenTruthStaysOutOfTheLetter(map);
                CheckComplaintWhoFilesAndWhy(map);
                CheckComplaintGuardsWithABrutalStreakAbuse(map);
                CheckComplaintPeopleWhoLeave(map);
            }
            finally
            {
                Rand.PopState();
            }
            List<Pawn> leftBehind = LeftOnTheMap(map, pawnsBefore, checksBegan);
            if (leftBehind.Count > 0) problems0.Add($"court checks left {string.Join(", ", leftBehind.Select(x => x.LabelShort))} on the map");
            int cameras = map.listerThings.AllThings.Count(t => t.def == ThingDef.Named("RP_SecurityCamera"));
            if (cameras != thingsBefore) problems0.Add($"court checks left {cameras - thingsBefore} camera(s) on the map");
        }

        /// <summary>A corner of the yard for a court check: the real trials and complaints set aside, throwaway inmates, guards and a camera on request.</summary>
        private sealed class CourtScene
        {
            public readonly Map map;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly PrisonMap pm;
            public readonly Rollback r;
            public readonly GangDef ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
            private readonly List<IntVec3> yard;
            private int spot;

            public CourtScene(Map map, Rollback r)
            {
                this.map = map;
                this.r = r;
                pm = PrisonMap.Of(map);
                List<IntVec3> all = pm.zones.CellsOf(RPDefOf.RP_Yard).Where(c => c.Standable(map)).ToList();
                yard = all.Count == 0 ? all : all.OrderBy(c => c.DistanceToSquared(all[0])).Take(30).ToList();
                if (yard.Count < 6) throw new InvalidOperationException($"no yard to test in ({yard.Count} cells)");
                // (Out of their cells all day: a beating needs both of them out, and the hour the check happens to run at is nobody's business.)
                FreeTimeAllDay(g, r);
                List<MurderTrial> trials0 = g.story.trials;
                List<Complaint> complaints0 = g.story.complaints;
                (int nextTrial0, int nextComplaint0) = (g.story.nextTrialId, g.story.nextComplaintId);
                r.Add(() =>
                {
                    g.story.trials = trials0;
                    g.story.complaints = complaints0;
                    (g.story.nextTrialId, g.story.nextComplaintId) = (nextTrial0, nextComplaint0);
                });
                g.story.trials = new List<MurderTrial>();
                g.story.complaints = new List<Complaint>();
                // (The real known informants are not asked about: a case asks about the first it finds.)
                var informants = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null && c.KnownReputation(ReputationDefOf.RP_Snitch) && !c.informantQuit).ToList();
                foreach (CompInmate c in informants) c.informantQuit = true;
                r.Add(() =>
                {
                    foreach (CompInmate c in informants) c.informantQuit = false;
                });
            }

            /// <summary>A standable cell far from <paramref name="where"/> (out of sight of it), or invalid.</summary>
            public IntVec3 FarFrom(IntVec3 where)
            {
                foreach (IntVec3 c in map.AllCells)
                    if (c.Standable(map) && c.DistanceTo(where) > 45f) return c;
                return IntVec3.Invalid;
            }

            public int Now => Find.TickManager.TicksGame;

            public IntVec3 Cell(int i) => yard[i % yard.Count];

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            /// <summary>A new inmate in the yard: medium security, a sentence, calm, no reputations, no record, no feelings about staff.</summary>
            public Pawn Inmate(string name, GangDef gang = null)
            {
                Pawn p = Throwaway(map, r);
                p.Name = new NameSingle(name);
                CompInmate c = p.Inmate();
                (c.activeFlag, c.gang, c.gangKnown, c.gangLeader, c.cash, c.tension, c.fear, c.want, c.snitchExposed, c.informantQuit, c.informantPaid, c.forcedActivity) =
                    (true, gang, false, false, 0, 20f, 0f, null, false, false, false, null);
                (c.security, c.sentenceDays, c.pendingPunishmentHours) = (InmateUtility.DefaultSecurity, 12, 0);
                c.reputations.Clear();
                c.revealed.Clear();
                c.records.Clear();
                c.staffMemories.Clear();
                ClearAddictions(p);
                Place(p, Cell(spot++));
                return p;
            }

            /// <summary>A new guard on site, awake (all-work timetable, full rest), clean, with nothing on their record.</summary>
            /// <summary>
            /// A second guard, who works every hour, far from the scene (out of sight of everything in it): the officer a check stands down or sends home is not the only one on some hour
            /// (<see cref="CorruptionSystem.CanSpare"/>), whatever the prison's own guards happen to be at that moment (a run with few of them failed the three checks that rely on it, once in 35).
            /// </summary>
            public Pawn Cover(string name, Pawn officer)
            {
                IntVec3 far = FarFrom(officer.Position);
                return far.IsValid ? Guard(name, far) : Guard(name);
            }

            public Pawn Guard(string name, IntVec3? near = null)
            {
                Pawn s = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                s.Name = new NameSingle(name);
                if (s.timetable != null)
                    for (int hour = 0; hour < 24; hour++) s.timetable.SetAssignment(hour, TimeAssignmentDefOf.Work);
                if (s.needs?.rest != null) s.needs.rest.CurLevel = 1f;
                CompStaff sc = s.GetComp<CompStaff>();
                (sc.corrupt, sc.patron, sc.patronGang, sc.doubleAgent, sc.exposedCorrupt, sc.suspicion, sc.iaOpen, sc.suspendedUntilTick) = (false, null, null, false, false, 0f, false, -1);
                (sc.complaintsUpheld, sc.lastUpheldTick, sc.trueComplaints, sc.integrity) = (0, -1, 0, 90f);
                sc.evidence.Clear();
                Place(s, near is { } at ? CellFinder.StandableCellNear(at, map, 2f) : Cell(spot++));
                return s;
            }

            /// <summary>The case as the court would have it for a killing that nobody saw: opened by the game's own call, then stripped of what the scene gave it so a check puts back only what it means to test.</summary>
            public MurderTrial Case(Pawn killer, Pawn victim, bool staffVictim = false)
            {
                MurderTrial t = Trials.Open(g, map, victim, killer, killer.Inmate(), staffVictim, null);
                t.witnesses.Clear();
                (t.camera, t.weapon, t.caughtAtScene, t.priorRecord) = (false, false, false, 0);
                t.avengers.Clear();
                return t;
            }

            public TrialWitness Witness(MurderTrial t, Pawn p, bool known = false)
            {
                var w = new TrialWitness { id = p.thingIDNumber, name = p.LabelShort, staff = p.GetComp<CompStaff>()?.role != null && !p.IsPrisonerOfColony, known = known };
                t.witnesses.Add(w);
                return w;
            }

            public string LetterText(Letter l) => l.Label.Resolve() + " | " + (l as ChoiceLetter)?.Text.Resolve();

            public IEnumerable<string> Told() => r.NewEntries.Select(e => e.text).Concat(r.NewLetters.Select(LetterText));

            public ChoiceLetter_PrisonDecision Asked(string decision) => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is(decision));

            public static void Fail(string text) => problems0.Add("court: " + text);

            public void Hour(int hour) => Trials.Hourly(g, map, hour);
        }

        // ---- the case is built from what happened ------------------------------------------------------------------------------------------

        /// <summary>Who was in sight is on the case (a guard, an inmate); the killer's gang and friends, the downed and the far away are not. Staff are known at once, inmates are not.</summary>
        private static void CheckTrialCollectsWhatWasSeen(Map map)
        {
            Guarded("court: the case is built from who was in sight", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkiller40", s.ravens), victim = s.Inmate("Zvictim40");
                IntVec3 where = victim.Position;
                Pawn seen = s.Inmate("Zseen40"), mate = s.Inmate("Zmate40", s.ravens), friend = s.Inmate("Zfriend40"), downed = s.Inmate("Zdowned40"), far = s.Inmate("Zfar40");
                Pawn guard = s.Guard("Zguard40", where), farGuard = s.Guard("Zfarguard40");
                foreach (Pawn near in new[] { seen, mate, friend, downed }) s.Place(near, CellFinder.StandableCellNear(where, map, 3f));
                if (!friend.relations.DirectRelationExists(RPRelationDefOf.RP_Friend, killer)) friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, killer);
                r.Add(() => friend.relations.RemoveDirectRelation(RPRelationDefOf.RP_Friend, killer));
                HealthUtility.DamageUntilDowned(downed, allowBleedingWounds: false);
                IntVec3 away = s.FarFrom(where);
                if (away.IsValid) s.Place(far, away);
                s.Place(farGuard, away.IsValid ? away : where);
                MurderTrial t = Trials.Open(s.g, map, victim, killer, killer.Inmate(), false, null);
                bool Has(Pawn p) => t.witnesses.Exists(w => w.id == p.thingIDNumber);
                if (!Has(seen) || Has(mate) || Has(friend) || Has(downed) || Has(killer) || Has(victim) || away.IsValid && (Has(far) || Has(farGuard)))
                    CourtScene.Fail($"witnesses were {string.Join(", ", t.witnesses.Select(w => w.name))}: expected {seen.LabelShort} and not the killer's gang mate, friend, the downed, the far away");
                if (!Has(guard) || !t.witnesses.Find(w => w.id == guard.thingIDNumber).known || !t.caughtAtScene)
                    CourtScene.Fail($"a guard beside the killing was not on the case as a known witness who caught them (caught {t.caughtAtScene})");
                if (t.witnesses.Find(w => w.id == seen.thingIDNumber) is { known: true }) CourtScene.Fail("an inmate witness was known to the warden before the case was assembled");
                if (Trials.KnownCase(t).Any(l => l.Contains(seen.LabelShort))) CourtScene.Fail("the case as the warden reads it named an inmate witness before it was assembled");
            });
        }

        /// <summary>The camera over the spot and the weapon are on the case, hidden until it is assembled.</summary>
        private static void CheckTrialCameraAndWeaponWaitForTheCase(Map map)
        {
            Guarded("court: the camera and the weapon are known once the case is assembled", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkiller41"), victim = s.Inmate("Zvictim41");
                IntVec3 camSpot = CellFinder.StandableCellNear(victim.Position, map, 4f);
                var camera = (Building)ThingMaker.MakeThing(ThingDef.Named("RP_SecurityCamera"));
                camera.SetFactionDirect(Faction.OfPlayer);
                GenSpawn.Spawn(camera, camSpot, map);
                r.Add(() =>
                {
                    if (!camera.Destroyed) camera.Destroy(DestroyMode.Vanish);
                });
                if (camera.GetComp<CompPowerTrader>() is { } power) power.PowerOn = true;
                var dinfo = new DamageInfo(DamageDefOf.Stab, 10f, instigator: killer, weapon: ThingDefOf.MeleeWeapon_Knife);
                MurderTrial t = Trials.Open(s.g, map, victim, killer, killer.Inmate(), false, dinfo);
                if (!t.camera || !t.weapon) CourtScene.Fail($"the case has the camera {t.camera} and the weapon {t.weapon}: both were there");
                float before = Trials.Strength(t, map, wardenView: true);
                if (Trials.KnownCase(t).Any(l => l == "RP_TrialKnown_Camera".Translate().Resolve() || l == "RP_TrialKnown_Weapon".Translate().Resolve())) CourtScene.Fail("the camera or the weapon showed on the warden's case before it was assembled");
                Trials.Assemble(s.g, map, t);
                float after = Trials.Strength(t, map, wardenView: true);
                List<string> known = Trials.KnownCase(t);
                if (!known.Contains("RP_TrialKnown_Camera".Translate().Resolve()) || !known.Contains("RP_TrialKnown_Weapon".Translate().Resolve()) || after < Math.Min(1f, before + Trials.Camera + Trials.Weapon) - 0.001f)
                    CourtScene.Fail($"once assembled the warden's case should show the camera and the weapon (strength {before:F2} -> {after:F2}; lines: {string.Join(" / ", known)})");
            });
        }

        // ---- a seen murder convicts ------------------------------------------------------------------------------------------------------------

        /// <summary>A murder a guard saw: charged at once with no sentence added, tried on the day (off-site: away at court from ten, the verdict in the afternoon), convicted.</summary>
        private static void CheckTrialSeenMurderConvicts(Map map)
        {
            Guarded("court: a murder a guard saw convicts", map, r =>
            {
                var s = new CourtScene(map, r);
                if (HearingRooms.Available(map, s.pm))
                {
                    CourtScene.Fail("a hearing room is on the map before the check built one");
                    return;
                }
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller42"), victim = s.Inmate("Zvictim42");
                Pawn guard = s.Guard("Zguard42", victim.Position);
                CompInmate kc = killer.Inmate();
                int sentence0 = kc.sentenceDays;
                (int trials0, int convictions0, int murders0) = (g.stats.trials, g.stats.convictions, g.stats.murders);
                DeathAftermath.Notify_Died(victim, killer, null, null);
                MurderTrial t = Trials.TrialOf(g, killer);
                if (t == null)
                {
                    CourtScene.Fail("the killer was charged and no case was opened");
                    return;
                }
                if (g.stats.murders != murders0 + 1 || !kc.records.Any(x => x.def == RPDeathDefOf.RP_Murder) || kc.sentenceDays != sentence0 && !kc.IsLifer)
                    CourtScene.Fail($"the charge: murders +{g.stats.murders - murders0}, murder on the record {kc.records.Any(x => x.def == RPDeathDefOf.RP_Murder)}, sentence {sentence0} -> {kc.sentenceDays} (nothing is added before the trial)");
                if (t.trialTick - s.Now < (Trials.TrialDelayDays - 1) * GenDate.TicksPerDay || !t.witnesses.Exists(w => w.staff && w.id == guard.thingIDNumber) || !t.caughtAtScene)
                    CourtScene.Fail($"the case: trial in {(t.trialTick - s.Now) / (float)GenDate.TicksPerDay:F1} days, the guard a witness {t.witnesses.Exists(w => w.staff && w.id == guard.thingIDNumber)}, caught at the scene {t.caughtAtScene}");
                string charge = s.Told().FirstOrDefault(x => x.StartsWith("RP_MurderLabel".Translate(killer.LabelShort).Resolve())) ?? "";
                if (!charge.Contains(Trials.DateText(t)) || !charge.Contains(guard.LabelShort)) CourtScene.Fail($"the charge letter does not give the trial's day and the officer who saw it ({charge})");
                // The court sits on the day: nothing before ten, taken to court at ten, the verdict from four in the afternoon.
                t.trialTick = s.Now;
                s.Hour(9);
                if (g.stats.trials != trials0 || t.courtTick >= 0) CourtScene.Fail("the court sat before ten in the morning");
                s.Hour(11);
                if (t.courtTick < 0 || kc.forcedActivity != RPDefOf.RP_Lockup || g.stats.trials != trials0) CourtScene.Fail($"off-site, the accused was not taken to court at eleven (court tick {t.courtTick}, confined {kc.forcedActivity?.defName})");
                s.Hour(13);
                if (g.stats.trials != trials0) CourtScene.Fail("the verdict came before the afternoon");
                s.Hour(16);
                if (g.stats.trials != trials0 + 1 || g.stats.convictions != convictions0 + 1 || g.story.trials.Contains(t))
                    CourtScene.Fail($"a murder a guard saw did not convict (trials +{g.stats.trials - trials0}, convictions +{g.stats.convictions - convictions0}, strength {Trials.Strength(t, map, wardenView: false):F2})");
                if (!kc.IsLifer && kc.sentenceDays != sentence0 + Trials.ExtraDays) CourtScene.Fail($"a conviction left the sentence at {kc.sentenceDays} days (was {sentence0}: life or {Trials.ExtraDays} more)");
                if (kc.forcedActivity == RPDefOf.RP_Lockup && t.courtLock) CourtScene.Fail("the accused was kept in their cell after the verdict");
                if (!s.Told().Any(x => x.StartsWith("RP_TrialConvictedLabel".Translate(killer.LabelShort).Resolve()) && x.Contains("RP_TrialVenue_Court".Translate().Resolve()) && x.Contains(guard.LabelShort)))
                    CourtScene.Fail("no letter told the warden the killer was convicted, where, and who testified");
                if (!r.NewEntries.Any(e => e.text.Contains("convicted"))) CourtScene.Fail("the chronicle has no line for the conviction");
            });
        }

        // ---- an unseen one, with witnesses who are got at ------------------------------------------------------------------------------------------

        /// <summary>
        /// Nobody saw it but three inmates, who testify: the case convicts. The killer's gang gets at them (two threats each) and they recant on the stand: the court acquits, the charge
        /// comes off the file, a strong case lost to tampering costs reputation, and what was hidden comes out in the letter, not before.
        /// </summary>
        private static void CheckTrialUnseenMurderWithRecantingWitnessesAcquits(Map map)
        {
            Guarded("court: an unseen murder whose witnesses recant is acquitted", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller43", s.ravens), victim = s.Inmate("Zvictim43"), mate = s.Inmate("Zmate43", s.ravens), w1 = s.Inmate("Zwitnessa43"), w2 = s.Inmate("Zwitnessb43"), w3 = s.Inmate("Zwitnessc43");
                CompInmate kc = killer.Inmate();
                kc.AddRecord(new MisconductRecord { def = RPDeathDefOf.RP_Murder, tick = s.Now, seen = true });
                MurderTrial control = s.Case(killer, victim);
                s.Witness(control, w1);
                s.Witness(control, w2);
                s.Witness(control, w3);
                if (Trials.Strength(control, map, wardenView: false) < Trials.ConvictAt) CourtScene.Fail($"three inmate witnesses who testify are not enough to convict ({Trials.Strength(control, map, wardenView: false):F2})");
                // Nothing of it on any screen before the day.
                string before = string.Join(" / ", Trials.KnownCase(control)) + string.Join(" / ", Trials.FileLines(killer)) + string.Join(" / ", Trials.FileLines(w1));
                if (before.Contains(w1.LabelShort) || before.Contains(w2.LabelShort) || before.Contains(w3.LabelShort)) CourtScene.Fail($"the warden's screens named a witness before the case was assembled ({before})");
                // The same case, with nobody got at: it convicts.
                (int convictions0, int acquittals0, int recanted0) = (g.stats.convictions, g.stats.acquittals, g.stats.witnessesRecanted);
                Trials.Verdict(g, map, control);
                if (g.stats.convictions != convictions0 + 1) CourtScene.Fail("the case with three testifying witnesses and nobody got at did not convict");
                // Now with the gang at them.
                MurderTrial t = s.Case(killer, victim);
                TrialWitness t1 = s.Witness(t, w1), t2 = s.Witness(t, w2), t3 = s.Witness(t, w3);
                bool reached = true;
                for (int i = 0; i < 2; i++)
                {
                    reached &= Trials.TryPressure(g, map, t, t1, w1, beating: false, allowReport: false);
                    reached &= Trials.TryPressure(g, map, t, t2, w2, beating: false, allowReport: false);
                    reached &= Trials.TryPressure(g, map, t, t3, w3, beating: false, allowReport: false);
                }
                if (!reached || t1.pressure < 1f || t2.pressure < 1f || t3.pressure < 1f) CourtScene.Fail($"the killer's gang could not get at the witnesses (pressure {t1.pressure}/{t2.pressure}/{t3.pressure})");
                if (w1.Inmate().fear <= 0f || w1.Inmate().mainCause == null || w1.Inmate().mainCause == "RP_Cause_Unsafe") CourtScene.Fail($"a threatened witness was not afraid, or their file gives the tampering away (fear {w1.Inmate().fear}, cause {w1.Inmate().mainCause})");
                // (What the warden can see is the same as before: the threat is nowhere on their case.)
                if (Trials.KnownCase(t).Any(l => l.Contains(w1.LabelShort)) || t1.known || t1.pressureKnown) CourtScene.Fail("a witness who was got at, and did not say so, showed on the warden's case");
                g.reputation = 50f;
                Trials.Verdict(g, map, t);
                if (g.stats.acquittals != acquittals0 + 1 || g.stats.witnessesRecanted != recanted0 + 3 || !t1.recanted || !t2.recanted || !t3.recanted)
                    CourtScene.Fail($"witnesses who were got at did not recant: acquittals +{g.stats.acquittals - acquittals0}, recanted +{g.stats.witnessesRecanted - recanted0}");
                if (kc.records.Any(x => x.def == RPDeathDefOf.RP_Murder)) CourtScene.Fail("an acquitted killer kept the murder on their file");
                if (g.reputation >= 50f) CourtScene.Fail($"a strong case lost to witnesses who were got at cost no reputation ({g.reputation})");
                string told = string.Join(" / ", s.Told());
                if (!told.Contains("RP_TrialAcquittedLabel".Translate(killer.LabelShort).Resolve()) || !told.Contains(w1.LabelShort) || !told.Contains("RP_TrialCameOut_Recanted".Translate(w1.LabelShort).Resolve()))
                    CourtScene.Fail("the acquittal's letter did not tell the warden who recanted");
            });
        }

        // ---- an informant testifies --------------------------------------------------------------------------------------------------------------------

        /// <summary>A known informant asked by decision letter to testify makes the case stronger and is exposed on the day: known to all, afraid, and wanted by the killer's gang.</summary>
        private static void CheckTrialInformantTestifiesAndIsExposed(Map map)
        {
            Guarded("court: an informant who testifies is exposed", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller44", s.ravens), victim = s.Inmate("Zvictim44"), mate = s.Inmate("Zmate44", s.ravens), witness = s.Inmate("Zwitness44"), informant = s.Inmate("Zsnitch44");
                CompInmate ic = informant.Inmate();
                ic.reputations.Add(ReputationDefOf.RP_Snitch);
                ic.revealed.Add(ReputationDefOf.RP_Snitch);
                MurderTrial t = s.Case(killer, victim);
                s.Witness(t, witness);
                t.priorRecord = 2;
                float without = Trials.Strength(t, map, wardenView: false);
                Trials.Assemble(g, map, t);
                ChoiceLetter_PrisonDecision ask = s.Asked("RP_Decision_TrialCase");
                if (ask == null || ask.pawns.Count == 0 || ask.pawns[ask.pawns.Count - 1] != informant || (ask.money & Trials.CaseInformant) == 0)
                {
                    CourtScene.Fail("the assembled case did not ask the warden about the known informant (and only about known ones)");
                    return;
                }
                PrisonDecisions.Choose(ask, LetterOption(ask, "RP_TrialCase_Ask"));
                float with = Trials.Strength(t, map, wardenView: false);
                if (t.informantId != informant.thingIDNumber || with < without + Trials.Informant - 0.001f || with < Trials.ConvictAt)
                    CourtScene.Fail($"asking the informant did not make the case stronger and convicting ({without:F2} -> {with:F2}, informant {t.informantName})");
                if (ic.snitchExposed) CourtScene.Fail("the informant was exposed when asked, not when they testified");
                (int convictions0, int fear0) = (g.stats.convictions, (int)ic.fear);
                Trials.Verdict(g, map, t);
                bool hated = mate.needs.mood.thoughts.memories.Memories.Any(m => m.def.defName == "RP_HatesSnitch" && m.otherPawn == informant);
                if (!ic.snitchExposed || !ic.revealed.Contains(ReputationDefOf.RP_Snitch) || ic.fear < fear0 + 30f || !hated || g.stats.convictions != convictions0 + 1)
                    CourtScene.Fail($"the informant who testified: exposed {ic.snitchExposed}, fear {fear0} -> {ic.fear}, the killer's gang hates them {hated}, convicted {g.stats.convictions - convictions0}");
                if (!s.Told().Any(x => x.Contains("RP_TrialCameOut_Informant".Translate(informant.LabelShort).Resolve())) || !r.NewEntries.Any(e => e.text.Contains(informant.LabelShort) && e.text.Contains("every inmate")))
                    CourtScene.Fail("the letter and the chronicle did not say the informant testified and is known now");
            });
        }

        // ---- a protected witness ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>A witness in protective custody cannot be got at; the same witness on the wing can. A witness in the hole cannot either, nor can the killer's gang reach a wing they are not in.</summary>
        private static void CheckTrialProtectedWitnessIsNotGotAt(Map map)
        {
            Guarded("court: a protected witness is not got at", map, r =>
            {
                var s = new CourtScene(map, r);
                SecurityLevelDef pc = Trials.ProtectiveLevel;
                if (pc == null)
                {
                    CourtScene.Fail("no protective custody level to test with");
                    return;
                }
                Pawn killer = s.Inmate("Zkiller45", s.ravens), victim = s.Inmate("Zvictim45"), mate = s.Inmate("Zmate45", s.ravens), witness = s.Inmate("Zwitness45"), other = s.Inmate("Zother45");
                MurderTrial t = s.Case(killer, victim);
                TrialWitness w = s.Witness(t, witness, known: true);
                CompInmate wc = witness.Inmate();
                SecurityLevelDef was = wc.security;
                // On the wing: they can be reached.
                bool open = Trials.TryPressure(s.g, map, t, w, witness, beating: false, allowReport: false);
                if (!open || w.pressure <= 0f) CourtScene.Fail("a witness on the wing of the killer's gang could not be got at (the control)");
                w.pressure = 0f;
                // In protective custody (set on the file: the beds are not the point): out of reach.
                wc.security = pc;
                bool reached = Trials.TryPressure(s.g, map, t, w, witness, beating: true, allowReport: false);
                if (reached || w.pressure != 0f || Trials.Pressurer(map, t, witness) != null) CourtScene.Fail($"a witness in protective custody was got at (pressure {w.pressure})");
                // Not even by someone of the killer's gang who is in protective custody too (the same wing: only the custody itself protects them).
                (SecurityLevelDef killerWas, SecurityLevelDef mateWas) = (killer.Inmate().security, mate.Inmate().security);
                (killer.Inmate().security, mate.Inmate().security) = (pc, pc);
                foreach (Pawn real in map.mapPawns.PrisonersOfColonySpawned)
                    if (real.Inmate() is { gang: not null } rc && rc.gang == s.ravens && real != killer && real != mate && rc.security == pc) CourtScene.Fail($"the real inmate {real.LabelShort} of the Ravens is already in protective custody; the check cannot tell");
                if (Trials.Pressurer(map, t, witness) != null || Trials.TryPressure(s.g, map, t, w, witness, beating: true, allowReport: false)) CourtScene.Fail("a witness in protective custody was got at by someone of the killer's gang in the same wing");
                (killer.Inmate().security, mate.Inmate().security) = (killerWas, mateWas);
                wc.security = was;
                // In the hole.
                wc.forcedActivity = RPSecurityDefOf.RP_Solitary;
                if (Trials.Pressurer(map, t, witness) != null) CourtScene.Fail("a witness in the hole was got at");
                wc.forcedActivity = null;
                // On another wing than every one of the killer's people.
                (killer.Inmate().security, mate.Inmate().security) = (Celebrities.Maximum, Celebrities.Maximum);
                foreach (Pawn real in map.mapPawns.PrisonersOfColonySpawned)
                    if (real.Inmate() is { gang: not null } rc && rc.gang == s.ravens && real != killer && real != mate && real.Inmate().security == was)
                    {
                        SecurityLevelDef realWas = rc.security;
                        rc.security = Celebrities.Maximum;
                        r.Add(() => rc.security = realWas);
                    }
                if (Trials.Pressurer(map, t, witness) != null) CourtScene.Fail("the killer's gang got at a witness on a wing they are not on");
                (killer.Inmate().security, mate.Inmate().security) = (was, was);
                // The warden's decision letter moves the inmates who came forward to protective custody, where there is room.
                Trials.Assemble(s.g, map, t);
                ChoiceLetter_PrisonDecision protect = s.Asked("RP_Decision_TrialCase");
                if (protect == null || (protect.money & Trials.CaseProtect) == 0) CourtScene.Fail("the assembled case did not ask the warden about the witnesses");
                else if (Unlocks.IsUnlocked(pc) && InmateUtility.HasRoomIn(witness, pc))
                {
                    PrisonDecisions.Choose(protect, LetterOption(protect, "RP_TrialCase_Protect"));
                    if (wc.security != pc || Trials.Pressurer(map, t, witness) != null) CourtScene.Fail($"the decision to protect the witnesses left them in {wc.security?.defName}");
                }
                else RPLog.Message($"[RimPrison:test] court: protective custody is unlocked {Unlocks.IsUnlocked(pc)} with {IntakeSystem.FreePrisonerBeds(map, pc).Count} free bed(s) here; the decision's move was not exercised");
            });
        }

        // ---- a member of staff is the victim ---------------------------------------------------------------------------------------------------------------

        /// <summary>The killer of an officer is tried the same way, but a colleague's testimony counts for more, and the staff feel the verdict.</summary>
        private static void CheckTrialStaffVictim(Map map)
        {
            Guarded("court: a staff victim's killer is tried, with the staff witnesses counting more", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller46"), victimGuard = s.Guard("Zvictimg46"), colleague = s.Guard("Zcolleague46", victimGuard.Position);
                CompInmate kc = killer.Inmate();
                int murders0 = g.stats.murders;
                StaffDeaths.Notify_Died(victimGuard, victimGuard.GetComp<CompStaff>(), new DamageInfo(DamageDefOf.Stab, 20f, instigator: killer), null);
                MurderTrial t = Trials.TrialOf(g, killer);
                if (t == null || !t.staffVictim || g.stats.murders != murders0 + 1)
                {
                    CourtScene.Fail($"the killer of an officer was not charged and set for trial (case {t != null}, staff victim {t?.staffVictim}, murders +{g.stats.murders - murders0})");
                    return;
                }
                // (The charge and the question of the family are one letter: the staff death's.)
                string charge = s.Told().FirstOrDefault(x => x.StartsWith("RP_StaffDeathLabel".Translate(victimGuard.LabelShort).Resolve())) ?? "";
                if (!charge.Contains(Trials.DateText(t)) || !charge.Contains(killer.LabelShort) || !t.witnesses.Exists(w => w.staff && w.id == colleague.thingIDNumber)) CourtScene.Fail($"the letter for an officer's death does not carry the charge against their killer with the trial day, or the colleague beside them was not a witness ({charge})");
                if (s.Told().Any(x => x.StartsWith("RP_MurderLabel".Translate(killer.LabelShort).Resolve()))) CourtScene.Fail("an officer's killer was announced in a letter of its own besides the staff death's");
                t.witnesses.RemoveAll(w => w.id != colleague.thingIDNumber);
                (t.camera, t.weapon, t.caughtAtScene, t.priorRecord) = (false, false, false, 2);
                float staffCase = Trials.Strength(t, map, wardenView: false);
                t.staffVictim = false;
                float ordinary = Trials.Strength(t, map, wardenView: false);
                t.staffVictim = true;
                if (staffCase - ordinary < Trials.StaffVictimWitness - Trials.StaffWitness - 0.001f || staffCase < Trials.ConvictAt)
                    CourtScene.Fail($"a colleague's word did not count for more for an officer's killer ({ordinary:F2} -> {staffCase:F2})");
                int convictions0 = g.stats.convictions;
                Trials.Verdict(g, map, t);
                if (g.stats.convictions != convictions0 + 1) CourtScene.Fail("the killer of an officer, seen by a colleague, was not convicted");
                if (!colleague.needs.mood.thoughts.memories.Memories.Any(m => m.def == RPTrialDefOf.RP_TrialJustice)) CourtScene.Fail("the staff did not feel the verdict on the killer of one of their own");
            });
        }

        // ---- hidden facts do not leak ------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// What the prosecution has and the warden has not been told (a camera, the weapon, an inmate who saw it, a witness who was got at) changes nothing the warden can read: the same charge letter,
        /// chronicle line, file line and case, with it and without it; after the case is assembled, the same again whether a witness was threatened or not.
        /// </summary>
        private static void CheckTrialHiddenFactsStayHidden(Map map)
        {
            Guarded("court: what the warden has not been told stays out of letters and files", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller47", s.ravens), victim = s.Inmate("Zvictim47"), mate = s.Inmate("Zmate47", s.ravens), witness = s.Inmate("Zwitness47");
                CompInmate kc = killer.Inmate();
                // (A murder on the file shows the killer's gang the first time, and then no more: that is not what is being compared.)
                kc.gangKnown = true;
                IntVec3 where = victim.Position;
                var camera = (Building)ThingMaker.MakeThing(ThingDef.Named("RP_SecurityCamera"));
                camera.SetFactionDirect(Faction.OfPlayer);
                SecurityLevelDef sec0 = kc.security;
                string Charged(bool withHidden)
                {
                    s.Place(witness, withHidden ? CellFinder.StandableCellNear(where, map, 3f) : s.FarFrom(where));
                    if (withHidden)
                    {
                        GenSpawn.Spawn(camera, CellFinder.StandableCellNear(where, map, 4f), map);
                        if (camera.GetComp<CompPowerTrader>() is { } power) power.PowerOn = true;
                    }
                    int entries0 = g.story.entries.Count;
                    var letters0 = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
                    MurderTrial t = DeathAftermath.ChargeWithMurder(g, victim, killer, kc, map, false, withHidden ? new DamageInfo(DamageDefOf.Stab, 10f, instigator: killer, weapon: ThingDefOf.MeleeWeapon_Knife) : null);
                    if (withHidden && (!t.camera || !t.weapon || !t.witnesses.Exists(w => w.id == witness.thingIDNumber))) CourtScene.Fail($"the hidden run lacked its hidden facts (camera {t.camera}, weapon {t.weapon})");
                    string text = string.Join(" // ", Find.LetterStack.LettersListForReading.Where(l => !letters0.Contains(l)).Select(s.LetterText))
                                  + " // " + string.Join(" // ", g.story.entries.Skip(entries0).Select(e => e.text))
                                  + " // " + string.Join(" // ", Trials.FileLines(killer)) + " // " + string.Join(" // ", Trials.FileLines(witness)) + " // " + string.Join(" // ", Trials.KnownCase(t));
                    g.story.trials.Remove(t);
                    foreach (Letter l in Find.LetterStack.LettersListForReading.Where(l => !letters0.Contains(l)).ToList()) Find.LetterStack.RemoveLetter(l);
                    g.story.entries.RemoveRange(entries0, g.story.entries.Count - entries0);
                    kc.records.RemoveAll(x => x.def == RPDeathDefOf.RP_Murder);
                    kc.pendingPunishmentHours = 0;
                    if (kc.security != sec0) InmateUtility.ChangeSecurity(killer, kc, sec0);
                    if (withHidden && camera.Spawned) camera.DeSpawn();
                    return text;
                }
                string quietText = Charged(withHidden: false), hiddenText = Charged(withHidden: true);
                if (!camera.Destroyed) camera.Destroy(DestroyMode.Vanish);
                if (quietText != hiddenText) CourtScene.Fail($"the charge read differently with hidden facts behind it:\n  without: {quietText}\n  with:    {hiddenText}");
                // The same case assembled, with a witness threatened and not saying so, and with nobody threatened.
                string Assembled(bool threatened)
                {
                    MurderTrial t = s.Case(killer, victim);
                    TrialWitness w = s.Witness(t, witness);
                    if (threatened) Trials.TryPressure(g, map, t, w, witness, beating: false, allowReport: false);
                    var letters0 = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
                    Trials.Assemble(g, map, t);
                    string text = string.Join(" // ", Find.LetterStack.LettersListForReading.Where(l => !letters0.Contains(l)).Select(s.LetterText))
                                  + " // " + string.Join(" // ", Trials.FileLines(killer)) + " // " + string.Join(" // ", Trials.FileLines(witness)) + " // " + Trials.KnownBand(t, map) + " // " + string.Join(" // ", Trials.KnownCase(t));
                    g.story.trials.Remove(t);
                    foreach (Letter l in Find.LetterStack.LettersListForReading.Where(l => !letters0.Contains(l)).ToList()) Find.LetterStack.RemoveLetter(l);
                    return text;
                }
                string calm = Assembled(false), threatened = Assembled(true);
                if (calm != threatened) CourtScene.Fail($"the assembled case read differently when a witness had been threatened:\n  calm:       {calm}\n  threatened: {threatened}");
            });
        }

        // ---- the case is assembled and the warden is asked -----------------------------------------------------------------------------------------------

        /// <summary>A day after the charge the case is assembled by the game's hourly pass: the warden is told what it has, then asked about the witnesses (and a known informant); a witness who reports a threat becomes known.</summary>
        private static void CheckTrialAssemblyAsksTheWarden(Map map)
        {
            Guarded("court: the case is assembled a day after the charge", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller48", s.ravens), victim = s.Inmate("Zvictim48"), mate = s.Inmate("Zmate48", s.ravens), witness = s.Inmate("Zwitness48");
                MurderTrial t = s.Case(killer, victim);
                TrialWitness w = s.Witness(t, witness);
                t.assembleTick = s.Now + 100;
                s.Hour(10);
                if (t.assembled || w.known) CourtScene.Fail("the case was assembled before its day");
                t.assembleTick = s.Now;
                s.Hour(10);
                ChoiceLetter_PrisonDecision protect = s.Asked("RP_Decision_TrialCase");
                if (!t.assembled || !w.known || protect == null || !protect.pawns.Contains(witness) || !s.Told().Any(x => x.StartsWith("RP_TrialAssembledLabel".Translate(killer.LabelShort).Resolve()) && x.Contains(witness.LabelShort)))
                    CourtScene.Fail($"assembly: assembled {t.assembled}, witness known {w.known}, decision letter {protect != null}");
                if (!Trials.FileLines(witness).Any(l => l.Contains(killer.LabelShort))) CourtScene.Fail("a known witness has no line on their file");
                // A threatened witness who goes to the guards makes the threat known (and the case's mark on them).
                int tries = 0;
                while (!w.pressureKnown && tries++ < 40)
                {
                    w.pressure = 0f;
                    Trials.TryPressure(g, map, t, w, witness, beating: false, allowReport: true);
                }
                if (!w.pressureKnown || !Trials.KnownCase(t).Any(l => l.Contains("RP_TrialThreatenedMark".Translate().Resolve())) || !r.NewEntries.Any(e => e.text.Contains(witness.LabelShort) && e.text.Contains("threatened")))
                    CourtScene.Fail($"a witness who reported a threat did not make it known ({tries} tries)");
                // Declining the decision (the letter times out as a no): nothing moves.
                SecurityLevelDef was = witness.Inmate().security;
                PrisonDecisions.Choose(protect, LetterOption(protect, "RP_TrialCase_Neither"));
                if (witness.Inmate().security != was) CourtScene.Fail("leaving the witnesses where they are moved them");
            });
        }

        // ---- by video, from the hearing room ---------------------------------------------------------------------------------------------------------------------

        /// <summary>With a hearing room the court sits by video link between ten and the evening, the accused stays where they are, and the letter says where it was held.</summary>
        private static void CheckTrialByVideoFromTheHearingRoom(Map map)
        {
            Guarded("court: with a hearing room the trial is by video link", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "trial venue");
                if (b == null || !b.BuildRoom(1)) return;
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller49"), victim = s.Inmate("Zvictim49"), guard = s.Guard("Zguard49", victim.Position);
                MurderTrial t = s.Case(killer, victim);
                s.Witness(t, guard, known: true);
                t.caughtAtScene = true;
                t.trialTick = s.Now;
                int trials0 = g.stats.trials;
                s.Hour(9);
                if (g.stats.trials != trials0) CourtScene.Fail("the court sat before ten by video");
                s.Hour(11);
                if (g.stats.trials != trials0 + 1 || t.courtTick >= 0 || killer.Inmate().forcedActivity != null)
                    CourtScene.Fail($"with a hearing room the verdict was not the same morning, or the accused was taken away (trials +{g.stats.trials - trials0}, court tick {t.courtTick}, confined {killer.Inmate().forcedActivity?.defName})");
                if (!s.Told().Any(x => x.Contains("RP_TrialVenue_Room".Translate().Resolve()))) CourtScene.Fail("the verdict's letter did not say the court sat by video from the hearing room");
            });
        }

        // ---- whoever leaves drops out -------------------------------------------------------------------------------------------------------------------------

        /// <summary>The accused gone: the case closes. A witness gone: the court goes on without them. A corrupt officer on the killer's payroll forgets what they saw: counted for nothing, and internal affairs gets a trace.</summary>
        private static void CheckTrialPeopleWhoLeave(Map map)
        {
            Guarded("court: people who leave do not break a case", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller50"), victim = s.Inmate("Zvictim50"), gone = s.Inmate("Zgone50"), staff = s.Guard("Zstaff50"), kept = s.Inmate("Zkept50");
                // A witness who has left.
                MurderTrial t = s.Case(killer, victim);
                s.Witness(t, gone);
                s.Witness(t, kept);
                gone.Destroy(DestroyMode.Vanish);
                float strength = Trials.Strength(t, map, wardenView: false);
                if (Math.Abs(strength - Trials.InmateWitness) > 0.001f) CourtScene.Fail($"a witness who left still counted ({strength:F2}, expected {Trials.InmateWitness:F2})");
                (int trials0, int acquittals0) = (g.stats.trials, g.stats.acquittals);
                Trials.Verdict(g, map, t);
                if (g.stats.trials != trials0 + 1 || g.stats.acquittals != acquittals0 + 1) CourtScene.Fail("a case with one witness gone was not decided");
                // A corrupt officer who is the killer's man.
                MurderTrial u = s.Case(killer, victim);
                TrialWitness sw = s.Witness(u, staff, known: true);
                CompStaff sc = staff.GetComp<CompStaff>();
                (sc.corrupt, sc.patron) = (true, killer);
                float asReported = Trials.Strength(u, map, wardenView: true), real = Trials.Strength(u, map, wardenView: false);
                if (Math.Abs(asReported - Trials.StaffWitness) > 0.001f || real != 0f) CourtScene.Fail($"a corrupt officer on the killer's payroll should look like a witness ({asReported:F2}) and count for nothing ({real:F2})");
                Trials.Verdict(g, map, u);
                if (!sw.unreliable || sc.suspicion < 40f || !sc.evidence.Contains("RP_Sus_TrialMemory") || !s.Told().Any(x => x.Contains("RP_TrialCameOut_Forgot".Translate(staff.LabelShort).Resolve())))
                    CourtScene.Fail($"the officer who forgot left no trace (unreliable {sw.unreliable}, suspicion {sc.suspicion}, evidence {string.Join(",", sc.evidence)})");
                // The accused carried off by a guard (to a bed, to the hole) is not gone: the case goes on (a pawn in someone's arms is not on the map's spawned list).
                Pawn carried = s.Inmate("Zcarried50"), carrier = s.Guard("Zcarrier50");
                MurderTrial held = s.Case(carried, victim);
                s.Witness(held, kept);
                carrier.carryTracker.TryStartCarry(carried, 1);
                r.Add(() =>
                {
                    if (carrier.carryTracker.CarriedThing != null) carrier.carryTracker.TryDropCarriedThing(carrier.Position, ThingPlaceMode.Near, out Thing _);
                });
                bool carriedNow = !carried.Spawned;
                s.Hour(11);
                if (carriedNow && !g.story.trials.Contains(held)) CourtScene.Fail("a case was closed because its accused was being carried to a bed");
                g.story.trials.Remove(held);
                if (carrier.carryTracker.CarriedThing != null) carrier.carryTracker.TryDropCarriedThing(carrier.Position, ThingPlaceMode.Near, out Thing _);
                if (!carriedNow) RPLog.Message("[RimPrison:test] court: the accused could not be picked up; the carried case was not exercised");
                // The accused gone: the case is closed, nothing is counted.
                MurderTrial v = s.Case(killer, victim);
                killer.Destroy(DestroyMode.Vanish);
                trials0 = g.stats.trials;
                s.Hour(11);
                if (g.story.trials.Contains(v) || g.stats.trials != trials0 || !r.NewEntries.Any(e => e.text.Contains("closed"))) CourtScene.Fail("a case whose accused had gone was not closed quietly");
                // Even straight to a verdict (a letter answered late) nothing breaks.
                MurderTrial w2 = new MurderTrial { id = 9999, killerId = 424242, killerName = "Zghost50", victimName = "Zvictim50" };
                g.story.trials.Add(w2);
                Trials.Verdict(g, map, w2);
                if (g.story.trials.Contains(w2) || g.stats.trials != trials0) CourtScene.Fail("a verdict on an accused nobody can find counted as a trial");
            });
        }

        // ---- complaints ----------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The complaint on the desk: filed, raised by the hourly pass (not before), and returned as the warden's letter.</summary>
        private static (Complaint cm, ChoiceLetter_PrisonDecision letter) RaiseComplaint(CourtScene s, Pawn inmate, Pawn officer, ComplaintKind kind, bool isTrue, bool camera, params Pawn[] saw)
        {
            Complaint cm = ComplaintSystem.File(s.g, s.map, inmate, officer, kind, isTrue);
            if (cm == null) return (null, null);
            cm.camera = camera;
            cm.staffSaw.Clear();
            cm.inmatesSaw.Clear();
            foreach (Pawn p in saw)
                (p.IsPrisonerOfColony ? cm.inmatesSaw : cm.staffSaw).Add(p.thingIDNumber);
            cm.dueTick = s.Now;
            ComplaintSystem.Hourly(s.g, s.map);
            ChoiceLetter_PrisonDecision letter = s.r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Complaint") && l.amount == cm.id);
            return (cm, letter);
        }

        /// <summary>A complaint nobody has seen yet is nowhere; a complaint raised is a letter and a line on the file; investigated, upheld on what the evidence shows, with the officer disciplined and everyone's memory of it.</summary>
        private static void CheckComplaintTrueOneInvestigatedIsUpheld(Map map)
        {
            Guarded("court: a true complaint, investigated, is upheld with the evidence", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer51"), colleague = s.Guard("Zcolleague51"), inmate = s.Inmate("Zcomplainant51"), friend = s.Inmate("Zfriend51");
                if (!friend.relations.DirectRelationExists(RPRelationDefOf.RP_Friend, inmate)) friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, inmate);
                r.Add(() => friend.relations.RemoveDirectRelation(RPRelationDefOf.RP_Friend, inmate));
                CompStaff sc = officer.GetComp<CompStaff>();
                CompInmate ic = inmate.Inmate();
                ic.tension = 60f;
                // Filed, but not yet on the warden's desk: nothing shows anywhere.
                Complaint cm = ComplaintSystem.File(g, map, inmate, officer, ComplaintKind.Beating, isTrue: true);
                if (cm == null || cm.stage != ComplaintStage.Filed || sc.trueComplaints != 0)
                {
                    CourtScene.Fail($"a complaint was not filed, or counted against the officer before it reached the desk (true complaints {sc.trueComplaints})");
                    return;
                }
                if (ComplaintSystem.FileLines(inmate).Any() || s.Asked("RP_Decision_Complaint") != null) CourtScene.Fail("a complaint that had not reached the warden yet showed on a screen");
                if (ComplaintSystem.File(g, map, inmate, officer, ComplaintKind.Force, isTrue: true) != null) CourtScene.Fail("an inmate with a complaint open filed a second");
                // The camera over the spot and a colleague who was there are what an investigation will have.
                cm.camera = true;
                cm.staffSaw.Add(colleague.thingIDNumber);
                cm.dueTick = s.Now;
                int filed0 = g.stats.complaintsFiled;
                ComplaintSystem.Hourly(g, map);
                ChoiceLetter_PrisonDecision letter = s.r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Complaint") && l.amount == cm.id);
                if (cm.stage != ComplaintStage.Waiting || letter == null || g.stats.complaintsFiled != filed0 + 1 || sc.trueComplaints != 1 || !ComplaintSystem.FileLines(inmate).Any(l => l.Contains(officer.LabelShort)))
                {
                    CourtScene.Fail($"a complaint due was not raised or not counted when it reached the desk (stage {cm.stage}, letter {letter != null}, true complaints {sc.trueComplaints})");
                    return;
                }
                int upheld0 = g.stats.complaintsUpheld;
                PrisonDecisions.Choose(letter, 2);
                if (cm.stage != ComplaintStage.Investigating || cm.dueTick <= s.Now) CourtScene.Fail($"investigate did not start an investigation (stage {cm.stage}, due in {cm.dueTick - s.Now})");
                if (!ComplaintSystem.FileLines(inmate).Any(l => l.Contains("investigated"))) CourtScene.Fail("the inmate's file does not say the complaint is being investigated");
                cm.dueTick = s.Now;
                ComplaintSystem.Hourly(g, map);
                if (cm.outcome != ComplaintOutcome.Upheld || g.stats.complaintsUpheld != upheld0 + 1 || sc.complaintsUpheld != 1)
                    CourtScene.Fail($"a true complaint with the evidence was not upheld (outcome {cm.outcome}, upheld +{g.stats.complaintsUpheld - upheld0}, officer's count {sc.complaintsUpheld})");
                string told = string.Join(" / ", s.Told());
                if (!told.Contains("RP_ComplaintLook_Camera".Translate().Resolve()) || !told.Contains("RP_Discipline_Warning".Translate().Resolve()) || !told.Contains(colleague.LabelShort))
                    CourtScene.Fail($"the result did not give the evidence and the discipline ({told})");
                void Remembers(Pawn p, ThoughtDef def, string who)
                {
                    if (p.needs.mood.thoughts.memories.GetFirstMemoryOfDef(def) == null) CourtScene.Fail($"{who} has no memory of the upheld complaint ({def.defName})");
                }
                Remembers(officer, RPComplaintDefOf.RP_ComplaintUpheldOnMe, "the officer");
                Remembers(colleague, RPComplaintDefOf.RP_ComplaintColleague, "a colleague");
                Remembers(inmate, RPComplaintDefOf.RP_WardenListened, "the complainant");
                Remembers(friend, RPComplaintDefOf.RP_WardenListensWing, "the complainant's friend");
                if (ic.tension >= 60f) CourtScene.Fail($"an upheld complaint did not ease the complainant ({ic.tension})");
                if (!ComplaintSystem.StaffLine(officer, sc).Contains("1") || !ComplaintSystem.FileLines(inmate).Any(l => l.Contains("upheld"))) CourtScene.Fail("the officer's record or the inmate's file did not show the upheld complaint");
                if (ComplaintSystem.PromotionDelayDays(sc) != ComplaintSystem.UpheldPromotionDays) CourtScene.Fail($"an upheld complaint put the promotion off by {ComplaintSystem.PromotionDelayDays(sc)} days");
            });
        }

        /// <summary>A false complaint, investigated with the camera over the spot: found out. The inmate is on report for lying, the officer cleared, nobody disciplined.</summary>
        private static void CheckComplaintFalseOneInvestigatedIsFoundOut(Map map)
        {
            Guarded("court: a false complaint, investigated, is found out", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer52"), inmate = s.Inmate("Zliar52");
                s.Cover("Zcover52", officer);
                CompStaff sc = officer.GetComp<CompStaff>();
                (Complaint cm, ChoiceLetter_PrisonDecision letter) = RaiseComplaint(s, inmate, officer, ComplaintKind.Force, false, camera: true);
                if (cm == null || letter == null)
                {
                    CourtScene.Fail("a false complaint did not reach the desk");
                    return;
                }
                (int found0, int upheld0) = (g.stats.complaintsFalseFound, g.stats.complaintsUpheld);
                PrisonDecisions.Choose(letter, 2);
                cm.dueTick = s.Now;
                ComplaintSystem.Hourly(g, map);
                if (cm.outcome != ComplaintOutcome.FoundFalse || g.stats.complaintsFalseFound != found0 + 1 || g.stats.complaintsUpheld != upheld0 || sc.complaintsUpheld != 0 || sc.trueComplaints != 0)
                    CourtScene.Fail($"a false complaint with a camera on it was not found out (outcome {cm.outcome}, found +{g.stats.complaintsFalseFound - found0}, upheld +{g.stats.complaintsUpheld - upheld0}, officer upheld {sc.complaintsUpheld})");
                if (!inmate.Inmate().records.Any(x => x.def == RPComplaintDefOf.RP_FalseComplaint && x.seen)) CourtScene.Fail("the liar was not put on report");
                if (officer.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPCorruptionDefOf.RP_Exonerated) == null) CourtScene.Fail("the cleared officer has no memory of it");
                if (!s.Told().Any(x => x.StartsWith("RP_ComplaintResultLabel".Translate(inmate.LabelShort, officer.LabelShort).Resolve()) && x.Contains(" false.")) || !ComplaintSystem.FileLines(inmate).Any(l => l.Contains("found false")))
                    CourtScene.Fail("the result and the file did not say the complaint was false");
            });
        }

        /// <summary>Dismissing a true complaint raises the inmate's tension and their friend's; against an officer who has a pattern it goes to court a day or two later (a settlement, reputation, the lawsuit counter). A first true one, and a false one, do not.</summary>
        private static void CheckComplaintDismissedTrueOneFeedsTheLawsuit(Map map)
        {
            Guarded("court: dismissing a true complaint against an officer with a pattern is a lawsuit", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn repeat = s.Guard("Zrepeat53"), clean = s.Guard("Zclean53"), first = s.Inmate("Zfirst53"), second = s.Inmate("Zsecond53"), third = s.Inmate("Zthird53"), liar = s.Inmate("Zliar53"), friend = s.Inmate("Zfriend53");
                if (!friend.relations.DirectRelationExists(RPRelationDefOf.RP_Friend, second)) friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, second);
                r.Add(() => friend.relations.RemoveDirectRelation(RPRelationDefOf.RP_Friend, second));
                // The first true complaint against them: dismissed, no pattern yet.
                (Complaint c1, ChoiceLetter_PrisonDecision l1) = RaiseComplaint(s, first, repeat, ComplaintKind.Beating, true, camera: false);
                PrisonDecisions.Choose(l1, 1);
                if (c1.lawsuitTick >= 0 || c1.outcome != ComplaintOutcome.Dismissed) CourtScene.Fail($"a first true complaint against an officer was a lawsuit (outcome {c1.outcome}, lawsuit tick {c1.lawsuitTick})");
                // A false one against a clean officer, dismissed.
                (Complaint cf, ChoiceLetter_PrisonDecision lf) = RaiseComplaint(s, liar, clean, ComplaintKind.Force, false, camera: false);
                PrisonDecisions.Choose(lf, 1);
                if (cf.lawsuitTick >= 0) CourtScene.Fail("a false complaint, dismissed, became a lawsuit");
                // The second true complaint against the same officer: dismissed, it goes to court.
                CompInmate sic = second.Inmate();
                (sic.tension, friend.Inmate().tension) = (30f, 30f);
                (Complaint c2, ChoiceLetter_PrisonDecision l2) = RaiseComplaint(s, second, repeat, ComplaintKind.Harassment, true, camera: false);
                int dismissed0 = g.stats.complaintsDismissed;
                PrisonDecisions.Choose(l2, 1);
                if (c2.lawsuitTick < 0 || g.stats.complaintsDismissed != dismissed0 + 1) CourtScene.Fail($"a true complaint against an officer with a pattern, dismissed, was not set for court (lawsuit tick {c2.lawsuitTick})");
                if (sic.tension < 30f + 14f || friend.Inmate().tension < 30f + 6f || second.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPComplaintDefOf.RP_WardenIgnored) == null)
                    CourtScene.Fail($"dismissing a complaint did not wind the inmate and their friend up (tension {sic.tension}, friend {friend.Inmate().tension})");
                g.reputation = 50f;
                (int funds0, int lawsuits0, float reputation0) = (g.ledger.funds, g.stats.lawsuits, g.reputation);
                c2.lawsuitTick = s.Now;
                ComplaintSystem.Hourly(g, map);
                if (g.ledger.funds != funds0 - ComplaintSystem.Settlement || g.stats.lawsuits != lawsuits0 + 1 || g.reputation >= reputation0 || c2.lawsuitTick >= 0
                    || !s.Told().Any(x => x.StartsWith("RP_ComplaintLawsuitLabel".Translate(second.LabelShort).Resolve())))
                    CourtScene.Fail($"the lawsuit did not cost the settlement, the reputation and a letter (funds {funds0} -> {g.ledger.funds}, lawsuits +{g.stats.lawsuits - lawsuits0}, reputation {reputation0} -> {g.reputation})");
                // A third, true, upheld: no lawsuit (the warden listened).
                (Complaint c3, ChoiceLetter_PrisonDecision l3) = RaiseComplaint(s, third, repeat, ComplaintKind.Punishment, true, camera: false);
                PrisonDecisions.Choose(l3, 0);
                if (c3.lawsuitTick >= 0 || c3.outcome != ComplaintOutcome.Upheld) CourtScene.Fail("an upheld complaint became a lawsuit");
            });
        }

        /// <summary>An investigation with nothing to go on finds nothing either way: a dismissal for want of evidence (a smaller rise in tension), a letter that says so, and the truth stays hidden.</summary>
        private static void CheckComplaintNoEvidenceIsADismissal(Map map)
        {
            Guarded("court: an investigation with no evidence is a dismissal", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer54"), inmate = s.Inmate("Zcomplainant54");
                s.Cover("Zcover54", officer);
                inmate.Inmate().tension = 20f;
                (Complaint cm, ChoiceLetter_PrisonDecision letter) = RaiseComplaint(s, inmate, officer, ComplaintKind.Beating, true, camera: false);
                (int dismissed0, int upheld0, int found0) = (g.stats.complaintsDismissed, g.stats.complaintsUpheld, g.stats.complaintsFalseFound);
                PrisonDecisions.Choose(letter, 2);
                cm.dueTick = s.Now;
                ComplaintSystem.Hourly(g, map);
                if (cm.outcome != ComplaintOutcome.NoFinding || g.stats.complaintsDismissed != dismissed0 + 1 || g.stats.complaintsUpheld != upheld0 || g.stats.complaintsFalseFound != found0)
                    CourtScene.Fail($"an investigation with no evidence was {cm.outcome}, dismissed +{g.stats.complaintsDismissed - dismissed0}");
                if (officer.GetComp<CompStaff>().complaintsUpheld != 0 || !s.Told().Any(x => x.Contains("RP_ComplaintLook_Nothing".Translate().Resolve()) && x.Contains("lack of evidence"))) CourtScene.Fail("no finding disciplined the officer, or its letter did not say why");
                if (inmate.Inmate().tension < 20f + 7f || inmate.Inmate().tension >= 20f + 15f) CourtScene.Fail($"a dismissal for want of evidence changed tension by {inmate.Inmate().tension - 20f} (a smaller rise than a dismissal's 15 was expected)");
            });
        }

        /// <summary>A warning, then a day's pay, then three days at home (a suspension of its own, no business of internal affairs); each upheld complaint puts promotion off a week, and fades after a month.</summary>
        private static void CheckComplaintDisciplineClimbs(Map map)
        {
            Guarded("court: discipline climbs with upheld complaints", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer55");
                s.Cover("Zcover55", officer);
                CompStaff sc = officer.GetComp<CompStaff>();
                Complaint Next(int n) => new Complaint { id = 800 + n, inmateId = -1, inmateName = "Zcomplainant55" + n, officerId = officer.thingIDNumber, officerName = officer.LabelShort, stage = ComplaintStage.Waiting };
                int wage = StaffCareers.Wage(sc);
                string how1 = ComplaintSystem.Uphold(g, map, Next(1), "RP_Chr_ComplaintUpheld");
                int funds = g.ledger.funds;
                string how2 = ComplaintSystem.Uphold(g, map, Next(2), "RP_Chr_ComplaintUpheld");
                if (how1 != "RP_Discipline_Warning".Translate().Resolve() || g.ledger.funds != funds + wage || how2 != "RP_Discipline_Fine".Translate(Money.Format(wage)).Resolve())
                    CourtScene.Fail($"discipline after one upheld complaint was '{how1}', after two '{how2}' with the pay {funds} -> {g.ledger.funds} (a warning, then a day's pay of {wage})");
                if (CorruptionSystem.Suspended(sc) || sc.iaOpen) CourtScene.Fail("an officer was suspended or investigated before the third upheld complaint");
                string how3 = ComplaintSystem.Uphold(g, map, Next(3), "RP_Chr_ComplaintUpheld");
                if (!CorruptionSystem.Suspended(sc) || how3 != "RP_Discipline_Suspended".Translate(CorruptionSystem.SuspensionDays).Resolve()) CourtScene.Fail($"the third upheld complaint was '{how3}', and they were not suspended");
                if (sc.iaOpen || sc.suspicion > 0f || sc.evidence.Count > 0 || s.Asked("RP_Decision_InternalAffairs") != null || CorruptionSystem.IaSuspended(sc) || !CorruptionSystem.Disciplined(sc))
                    CourtScene.Fail($"three upheld complaints opened an internal affairs case or an IA suspension, or no disciplinary one (open {sc.iaOpen}, suspicion {sc.suspicion}, evidence {string.Join(",", sc.evidence)}, disciplined {CorruptionSystem.Disciplined(sc)})");
                if (ComplaintSystem.PromotionDelayDays(sc) != 3 * ComplaintSystem.UpheldPromotionDays) CourtScene.Fail($"three complaints put promotion off by {ComplaintSystem.PromotionDelayDays(sc)} days");
                // Promotion: eligible with the service and a clean record, until a complaint is upheld; it fades after a month.
                sc.disciplineUntilTick = -1;
                sc.complaintsUpheld = 0;
                sc.hiredTick = sc.roleSinceTick = s.Now - (int)((StaffCareers.DaysFor(1) + 1f) * GenDate.TicksPerDay);
                bool baseline = StaffCareers.Eligible(officer, sc);
                sc.complaintsUpheld = 1;
                sc.lastUpheldTick = s.Now;
                if (baseline && StaffCareers.Eligible(officer, sc)) CourtScene.Fail("an upheld complaint did not put the promotion off");
                sc.lastUpheldTick = s.Now - (ComplaintSystem.UpheldFadeDays + 1) * GenDate.TicksPerDay;
                ComplaintSystem.Daily(map);
                if (sc.complaintsUpheld != 0) CourtScene.Fail($"an upheld complaint did not fade after a month ({sc.complaintsUpheld})");
                if (!baseline) RPLog.Message("[RimPrison:test] court: the guard was not eligible for promotion to begin with; the delay was not exercised");
            });
        }

        /// <summary>A true complaint and a false one, with the same words, the same parties, the same kind: the same letter, the same line on the inmate's file, the same line on the Intel tab.</summary>
        private static void CheckComplaintHiddenTruthStaysOutOfTheLetter(Map map)
        {
            Guarded("court: whether a complaint is true is not in the letter or the file", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer56"), inmate = s.Inmate("Zcomplainant56");
                string Told(bool isTrue)
                {
                    g.story.complaints.Clear();
                    var letters0 = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
                    (Complaint cm, ChoiceLetter_PrisonDecision letter) = RaiseComplaint(s, inmate, officer, ComplaintKind.Harassment, isTrue, camera: true);
                    string text = s.LetterText(letter) + " // " + string.Join(" // ", ComplaintSystem.FileLines(inmate)) + " // " + ComplaintSystem.StaffLine(officer, officer.GetComp<CompStaff>()) + " // " + string.Join(" // ", letter.decision.Worker.Options(letter).Select(k => k.Translate().Resolve()));
                    Find.LetterStack.RemoveLetter(letter);
                    officer.GetComp<CompStaff>().trueComplaints = 0;
                    return text;
                }
                string honest = Told(true), lie = Told(false);
                if (honest != lie) CourtScene.Fail($"the complaint read differently when it was true:\n  true:  {honest}\n  false: {lie}");
            });
        }

        /// <summary>A guard with a brutal streak beats and the beaten inmate may complain; a manipulative or vindictive inmate who was only searched or restrained may lie; a calm inmate who was searched does not.</summary>
        private static void CheckComplaintWhoFilesAndWhy(Map map)
        {
            Guarded("court: who files complaints, and why", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn brute = s.Guard("Zbrute57"), gentle = s.Guard("Zgentle57");
                // (Their own traits are cleared first: a trait that conflicts with one they had is not gained.)
                brute.story.traits.allTraits.Clear();
                gentle.story.traits.allTraits.Clear();
                brute.story.traits.GainTrait(new Trait(TraitDefOf.Bloodlust));
                gentle.story.traits.GainTrait(new Trait(TraitDefOf.Kind));
                if (ComplaintSystem.Brutality(brute) < ComplaintSystem.AbuseFrom || ComplaintSystem.Brutality(gentle) >= ComplaintSystem.AbuseFrom) CourtScene.Fail($"brutality: Bloodlust {ComplaintSystem.Brutality(brute):F1} ({string.Join(",", brute.story.traits.allTraits.Select(t => t.def.defName))}), Kind {ComplaintSystem.Brutality(gentle):F1} ({string.Join(",", gentle.story.traits.allTraits.Select(t => t.def.defName))})");
                // A beating hurts, and the guard is hated for it; the beaten inmate files within a few of them (a model prisoner less).
                Pawn beaten = s.Inmate("Zbeaten57");
                int wounds0 = beaten.health.hediffSet.hediffs.Count;
                ComplaintSystem.Abuse(g, brute, beaten, ComplaintKind.Beating);
                if (beaten.health.hediffSet.hediffs.Count <= wounds0 || StaffMemories.ScoreFor(beaten.Inmate(), brute) > -20f) CourtScene.Fail($"a beating by a guard with a brutal streak left no wound ({wounds0} -> {beaten.health.hediffSet.hediffs.Count}) or no memory ({StaffMemories.ScoreFor(beaten.Inmate(), brute)})");
                Complaint filed = null;
                for (int i = 0; i < 40 && filed == null; i++) filed = ComplaintSystem.Notify_Incident(g, brute, s.Inmate("Zbeaten57" + i), ComplaintKind.Beating);
                if (filed == null || !filed.isTrue) CourtScene.Fail("forty beaten inmates and not one complaint, or a complaint about a real beating that is not true");
                // A calm inmate who was only searched never files (and one who has been found out does it rarely); a manipulative one who had contraband found often does, and it is a lie.
                Pawn calm = s.Inmate("Zcalm57");
                calm.story.traits.allTraits.RemoveAll(t => t.def == TraitDefOf.Psychopath || t.def == TraitDefOf.Abrasive);
                for (int i = 0; i < 300; i++) ComplaintSystem.Notify_Searched(gentle, calm, found: false);
                if (ComplaintSystem.OpenBy(g, calm.thingIDNumber) != null) CourtScene.Fail("a calm inmate who was only searched filed a complaint");
                Pawn schemer = s.Inmate("Zschemer57");
                schemer.Inmate().reputations.Add(ReputationDefOf.RP_Instigator);
                for (int i = 0; i < 300 && ComplaintSystem.OpenBy(g, schemer.thingIDNumber) == null; i++) ComplaintSystem.Notify_Searched(gentle, schemer, found: true);
                Complaint lie = ComplaintSystem.OpenBy(g, schemer.thingIDNumber);
                if (lie == null || lie.isTrue || gentle.GetComp<CompStaff>().trueComplaints != 0) CourtScene.Fail($"a manipulative inmate whose contraband was found filed {(lie == null ? "nothing in 300 searches" : "a true complaint")}");
                // A restraint that goes too far: the rough guard hurts a calm inmate within a few tries.
                int hurt = 0;
                for (int i = 0; i < 80 && hurt == 0; i++)
                {
                    Pawn p = s.Inmate("Zrestrained57" + i);
                    int before = p.health.hediffSet.hediffs.Count;
                    ComplaintSystem.Notify_Restrained(brute, p, p.Inmate(), wasAggro: false);
                    if (p.health.hediffSet.hediffs.Count > before) hurt++;
                }
                if (hurt == 0) CourtScene.Fail("eighty restraints by a guard with a brutal streak and not one rough one");
                // The real restraint (a guard who is Bloodlust, Psychopath, Brawler and Abrasive in one): the game's own call restrains them and may go too far, the inmate remembering it worse than a plain restraint.
                Pawn tyrant = s.Guard("Ztyrant57");
                tyrant.story.traits.allTraits.Clear();
                foreach (TraitDef def in new[] { TraitDefOf.Bloodlust, TraitDefOf.Psychopath, TraitDefOf.Brawler, TraitDefOf.Abrasive })
                    tyrant.story.traits.GainTrait(new Trait(def), suppressConflicts: true);
                int rough = 0;
                for (int i = 0; i < 60 && rough == 0; i++)
                {
                    Pawn p = s.Inmate("Zsubdued57" + i);
                    PunishmentUtility.ResolveSubdue(tyrant, p, alwaysSucceeds: true);
                    if (!p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) CourtScene.Fail("a restraint did not restrain");
                    if (StaffMemories.ScoreFor(p.Inmate(), tyrant) <= StaffMemories.Restrained - 9f) rough++;
                }
                if (rough == 0) CourtScene.Fail("sixty restraints by the roughest guard there is, through the game's own call, and not one went too far");
            });
        }

        /// <summary>The hourly pass: guards with a brutal streak abuse inmates near them (the game's own damage and job code runs under the loop over the colony's staff, which must not trip on what it changes).</summary>
        private static void CheckComplaintGuardsWithABrutalStreakAbuse(Map map)
        {
            Guarded("court: the hourly pass lets a brutal guard abuse an inmate", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn a = s.Guard("Ztyranta59"), b = s.Guard("Ztyrantb59", s.Cell(1)), kind = s.Guard("Zkind59", s.Cell(2));
                foreach (Pawn tyrant in new[] { a, b })
                {
                    tyrant.story.traits.allTraits.Clear();
                    foreach (TraitDef def in new[] { TraitDefOf.Bloodlust, TraitDefOf.Psychopath, TraitDefOf.Brawler, TraitDefOf.Abrasive }) tyrant.story.traits.GainTrait(new Trait(def), suppressConflicts: true);
                }
                kind.story.traits.allTraits.Clear();
                kind.story.traits.GainTrait(new Trait(TraitDefOf.Kind));
                // (In good spirits: a guard on a bad day may lose their temper whatever their traits, which another check is about.)
                if (kind.needs?.mood != null) kind.needs.mood.CurLevel = 0.9f;
                var inmates = new List<Pawn>();
                for (int i = 0; i < 3; i++) inmates.Add(s.Inmate("Zabused59" + i));
                foreach (Pawn p in inmates) s.Place(p, CellFinder.StandableCellNear(a.Position, map, 3f));
                // (Nobody else in the prison is a candidate: the real inmates are out of the guards' reach for the check.)
                var real = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null && !inmates.Exists(i => i.Inmate() == c)).Select(c => (c, c.forcedActivity)).ToList();
                foreach ((CompInmate c, RegimeActivityDef _) in real) c.forcedActivity = RPSecurityDefOf.RP_Solitary;
                r.Add(() =>
                {
                    foreach ((CompInmate c, RegimeActivityDef was) in real) c.forcedActivity = was;
                });
                for (int i = 0; i < 900; i++) ComplaintSystem.HourlyGuards(g, map);
                int abused = inmates.Count(p => StaffMemories.ScoreFor(p.Inmate(), a) <= -12f || StaffMemories.ScoreFor(p.Inmate(), b) <= -12f);
                if (abused == 0) CourtScene.Fail("900 hours with two guards of a brutal streak next to three inmates and not one was abused");
                if (inmates.Any(p => StaffMemories.ScoreFor(p.Inmate(), kind) < 0f)) CourtScene.Fail("a kind guard abused someone");
            });
        }

        /// <summary>The officer or the inmate gone: a waiting complaint is dropped, a decided one closes without a name to discipline, nothing throws.</summary>
        private static void CheckComplaintPeopleWhoLeave(Map map)
        {
            Guarded("court: people who leave do not break a complaint", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer58"), inmate = s.Inmate("Zcomplainant58"), second = s.Inmate("Zsecond58"), gone = s.Guard("Zgone58");
                // The officer leaves before the complaint reaches the desk: it is dropped.
                Complaint early = ComplaintSystem.File(g, map, inmate, gone, ComplaintKind.Force, isTrue: true);
                gone.Destroy(DestroyMode.Vanish);
                early.dueTick = s.Now;
                ComplaintSystem.Hourly(g, map);
                if (g.story.complaints.Contains(early) || r.NewLetters.Any(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_Complaint"))) CourtScene.Fail("a complaint against an officer who had left reached the desk");
                // The officer leaves while the letter waits: uphold closes it, nobody is disciplined, and it does not throw.
                (Complaint cm, ChoiceLetter_PrisonDecision letter) = RaiseComplaint(s, second, officer, ComplaintKind.Beating, true, camera: false);
                officer.Destroy(DestroyMode.Vanish);
                if (letter != null && new PrisonDecisionWorker_Complaint().Disabled(letter, 2) == null) CourtScene.Fail("investigating a complaint against an officer who has left was offered");
                int upheld0 = g.stats.complaintsUpheld;
                PrisonDecisions.Choose(letter, 0);
                if (cm.outcome != ComplaintOutcome.Upheld || g.stats.complaintsUpheld != upheld0 + 1) CourtScene.Fail("a complaint against an officer who left was not closed by the warden's answer");
            });
        }

        // ---- save and load ----------------------------------------------------------------------------------------------------------------------------------

        private static int courtSeedTrials = -1, courtSeedComplaints, courtSeedStaffId, courtSeedUpheld, courtSeedLastUpheld, courtSeedTrue, courtSeedLastUpheldMade;
        private static (int, int, int, int, int, int, int, int) courtSeedStats;
        private static (int discipline, int standDown) courtSeedOff, courtSeedOffMade;
        private static int courtSeedProtectedId = -1;
        private static SecurityLevelDef courtSeedProtectedWas;
        private static string courtSeedDescribed;

        /// <summary>A case with a hidden camera, a threatened witness, a staff witness and an informant asked, and one complaint in each stage, with their counters and an officer's record: all of it must make the round trip.</summary>
        private static void SeedCourtForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            int t0 = Find.TickManager.TicksGame;
            Pawn officer = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.GetComp<CompStaff>()?.role != null);
            if (officer == null)
            {
                problems0.Add("saveload: no member of staff to seed a complaint record with");
                return;
            }
            CompStaff sc = officer.GetComp<CompStaff>();
            (courtSeedTrials, courtSeedComplaints, courtSeedStaffId) = (g.story.trials.Count, g.story.complaints.Count, officer.thingIDNumber);
            (courtSeedUpheld, courtSeedLastUpheld, courtSeedTrue) = (sc.complaintsUpheld, sc.lastUpheldTick, sc.trueComplaints);
            courtSeedStats = (g.stats.trials, g.stats.convictions, g.stats.acquittals, g.stats.witnessesRecanted, g.stats.complaintsFiled, g.stats.complaintsUpheld, g.stats.complaintsDismissed, g.stats.complaintsFalseFound);
            (sc.complaintsUpheld, sc.lastUpheldTick, sc.trueComplaints) = (2, courtSeedLastUpheldMade = t0 - 1234, 3);
            courtSeedOff = (sc.disciplineUntilTick, sc.standDownUntilTick);
            (sc.disciplineUntilTick, sc.standDownUntilTick) = courtSeedOffMade = (t0 + 20 * GenDate.TicksPerDay, t0 + 21 * GenDate.TicksPerDay);
            (g.stats.trials, g.stats.convictions, g.stats.acquittals, g.stats.witnessesRecanted, g.stats.complaintsFiled, g.stats.complaintsUpheld, g.stats.complaintsDismissed, g.stats.complaintsFalseFound) = (7, 4, 3, 2, 9, 3, 4, 2);
            var trial = new MurderTrial
            {
                id = g.story.nextTrialId++, killerId = 424242, killerName = "Seedkiller", victimId = 434343, victimName = "Seedvictim", staffVictim = true, assembled = true, caughtAtScene = false, camera = true, weapon = true,
                priorRecord = 2, chargedTick = t0 - 5000, assembleTick = t0 - 2000, trialTick = t0 + 7 * GenDate.TicksPerDay, courtTick = -1, courtLock = true, courtLockUntil = t0 + 8888, fledTick = t0 - 777, informantId = 454545, informantName = "Seedsnitch",
            };
            trial.witnesses.Add(new TrialWitness { id = 464646, name = "Seedwitness", staff = false, known = true, pressure = 1.5f, pressureKnown = true });
            trial.witnesses.Add(new TrialWitness { id = officer.thingIDNumber, name = officer.LabelShort, staff = true, known = true });
            trial.witnesses.Add(new TrialWitness { id = 474747, name = "Seedhidden", staff = false, known = false, pressure = 0.5f });
            trial.avengers.AddRange(new[] { 1, 2, 3 });
            g.story.trials.Add(trial);
            ComplaintStage[] stages = { ComplaintStage.Filed, ComplaintStage.Waiting, ComplaintStage.Investigating, ComplaintStage.Closed };
            for (int i = 0; i < stages.Length; i++)
                g.story.complaints.Add(new Complaint
                {
                    id = g.story.nextComplaintId++, inmateId = 500000 + i, officerId = officer.thingIDNumber, inmateName = "Seedcomplainant" + i, officerName = officer.LabelShort, kind = (ComplaintKind)i, isTrue = i % 2 == 0, stage = stages[i],
                    outcome = i == 3 ? ComplaintOutcome.Dismissed : ComplaintOutcome.None, filedTick = t0 - 100 * i, dueTick = t0 + 100 * i, closedTick = i == 3 ? t0 - 50 : -1, lawsuitTick = i == 3 ? t0 + 4321 : -1,
                    spot = new IntVec3(10 + i, 0, 20 + i), camera = i % 2 == 1, staffSaw = new List<int> { officer.thingIDNumber }, inmatesSaw = new List<int> { 6, 7 },
                });
            // A witness kept in protective custody for a case, who goes back to their old level when it is over: who they go back to must survive the round trip.
            if (map.mapPawns.PrisonersOfColony.FirstOrDefault(x => x.Inmate() != null) is { } kept)
            {
                courtSeedProtectedId = kept.thingIDNumber;
                courtSeedProtectedWas = kept.Inmate().protectedFrom;
                kept.Inmate().protectedFrom = InmateUtility.DefaultSecurity;
            }
            courtSeedDescribed = CourtDescribe(g);
            RPLog.Message($"[RimPrison:test] saveload court: {g.story.trials.Count} case, {g.story.complaints.Count} complaints, officer {officer.LabelShort} with {sc.complaintsUpheld} upheld");
        }

        /// <summary>Everything the court and the complaints keep, as one comparable string.</summary>
        private static string CourtDescribe(PrisonGame g) =>
            $"{g.story.nextTrialId}/{g.story.nextComplaintId}:{string.Join(",", g.story.trials.Select(Trials.Describe))}:{string.Join(",", g.story.complaints.Select(ComplaintSystem.Describe))}:"
            + $"{g.stats.trials}/{g.stats.convictions}/{g.stats.acquittals}/{g.stats.witnessesRecanted}/{g.stats.complaintsFiled}/{g.stats.complaintsUpheld}/{g.stats.complaintsDismissed}/{g.stats.complaintsFalseFound}";

        /// <summary>The seeded case and complaints came back whole; then they go.</summary>
        private static void CheckCourtAfterReload(Map map)
        {
            if (courtSeedTrials < 0) return;
            PrisonGame g = PrisonGame.Instance;
            string after = CourtDescribe(g);
            Pawn officer = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.thingIDNumber == courtSeedStaffId);
            CompStaff sc = officer?.GetComp<CompStaff>();
            bool staffWhole = sc != null && (sc.complaintsUpheld, sc.lastUpheldTick, sc.trueComplaints) == (2, courtSeedLastUpheldMade, 3) && (sc.disciplineUntilTick, sc.standDownUntilTick) == courtSeedOffMade;
            Pawn keptWitness = map.mapPawns.PrisonersOfColony.FirstOrDefault(x => x.thingIDNumber == courtSeedProtectedId);
            bool protectedWhole = courtSeedProtectedId < 0 || keptWitness?.Inmate()?.protectedFrom == InmateUtility.DefaultSecurity;
            if (keptWitness?.Inmate() != null) keptWitness.Inmate().protectedFrom = courtSeedProtectedWas;
            courtSeedProtectedId = -1;
            bool whole = after == courtSeedDescribed && staffWhole && protectedWhole;
            RPLog.Message($"[RimPrison:test] saveload court: {(whole ? "intact" : "changed")}");
            if (!whole) reloadProblems.Add($"the court's state changed in the round trip: {courtSeedDescribed} -> {after} (officer's record whole {staffWhole}, a witness's old level kept {protectedWhole})");
            g.story.trials.RemoveRange(courtSeedTrials, g.story.trials.Count - courtSeedTrials);
            g.story.complaints.RemoveRange(courtSeedComplaints, g.story.complaints.Count - courtSeedComplaints);
            if (sc != null) (sc.complaintsUpheld, sc.lastUpheldTick, sc.trueComplaints, sc.disciplineUntilTick, sc.standDownUntilTick) = (courtSeedUpheld, courtSeedLastUpheld, courtSeedTrue, courtSeedOff.discipline, courtSeedOff.standDown);
            (g.stats.trials, g.stats.convictions, g.stats.acquittals, g.stats.witnessesRecanted, g.stats.complaintsFiled, g.stats.complaintsUpheld, g.stats.complaintsDismissed, g.stats.complaintsFalseFound) = courtSeedStats;
            courtSeedTrials = -1;
        }
    }
}
