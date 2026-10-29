using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of the murder trials and the complaints against officers (H1-H4, M1-M5, L1-L10; see <see cref="Trials"/> and <see cref="ComplaintSystem"/>). Each fails
    /// without its fix, is its own method called once from FireAllEvents (right after the court's own checks, <see cref="CheckCourtCases"/>), works on throwaway inmates and staff
    /// numbered from 91000 (with names of their own), runs no ticks (the hourly passes are called by hand), sets the real trials and complaints aside while it runs and puts back what it
    /// touches (<see cref="Rollback"/>).
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The events plan's entry: its own dice (the same every run), and nothing left behind on the map.</summary>
        private static void CheckCourtRegressions(Map map)
        {
            Rand.PushState(20261023);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            int checksBegan = Find.TickManager.TicksGame; // (a real shuttle that lands while the checks tick brings inmates of its own: not theirs to put away)
            int fugitives0 = PrisonGame.Instance.fugitives.Count;
            try
            {
                CheckRegRemandHoldsTheAccused(map);
                CheckRegEscapeeIsTriedAfterRecapture(map);
                CheckRegTransferOutClosesTheCaseAtACost(map);
                CheckRegBareHandsAreNoWeaponAndTheOddsHold(map);
                CheckRegLiesCoverEveryKindAndLeaveNoTell(map);
                CheckRegAnyGuardOnABadDayMayAbuse(map);
                CheckRegAbuseLockUpIsAnOrdinarySanction(map);
                CheckRegDisciplineIsNotInternalAffairs(map);
                CheckRegPressureWaitsForTheCaseAndFadesInProtection(map);
                CheckRegFalseComplaintsAreRarerAndSoftlyDismissed(map);
                CheckRegInvestigatingCostsSomething(map);
                CheckRegCellSearchComplaintNeedsTheOwnerThere(map);
                CheckRegHiddenBeatingLeavesNoTrace(map);
                CheckRegTrialDateIsADayTheCourtSits(map);
                CheckRegKnownCaseLeavesOutWitnessesWhoAreGone(map);
                CheckRegProtectionIsOfferedOnlyWhereItCanBeGiven(map);
                CheckRegInformantWhoLeftCannotBeAsked(map);
                CheckRegUpholdWithTheOfficerGoneSaysSo(map);
                CheckRegAbuseTargetsAreNotJustAnyone(map);
                CheckRegTrueComplaintsAreCountedAtTheDesk(map);
                CheckRegAnotherHomeMapDoesNotDropTheCases(map);
                CheckRegAcquittalRemovesThisCasesRecord(map);
            }
            finally
            {
                Rand.PopState();
            }
            List<Pawn> leftBehind = LeftOnTheMap(map, pawnsBefore, checksBegan);
            if (leftBehind.Count > 0) problems0.Add($"court regression checks left {string.Join(", ", leftBehind.Select(x => x.LabelShort))} on the map");
            if (PrisonGame.Instance.fugitives.Count != fugitives0) problems0.Add($"court regression checks left {PrisonGame.Instance.fugitives.Count - fugitives0} fugitive(s) behind");
        }

        private static int Days(float days) => (int)(days * GenDate.TicksPerDay);

        /// <summary>The real inmates are out of any guard's reach while a check runs (shut in the hole), so that only the check's own inmates can be a guard's target.</summary>
        private static void QuietTheRealInmates(Map map, Rollback r, IEnumerable<Pawn> except) => r.Add(QuietTheRealInmates(map, except));

        /// <summary>
        /// The same, and the way to put them back (for a check that has no <see cref="Rollback"/> of its own). It touches as little of the live prison as it can:
        /// - only inmates who are free to be shut in: not one already confined (solitary or a lockup, whose own timer ends it), nor with an open parole case (the hearing is called off for a confined inmate, for good),
        ///   nor on the way out of the prison;
        /// - they are shut in with no end time, which no sentence leaves them with (a sentence that begins during the check sets one), so on the way out it can tell its own confinement from the product's:
        ///   if it is still the check's own, the inmate is let out; if the product changed it meanwhile (a punishment began), the product's value stays;
        /// - a job taken off them (the hourly assignment skips the confined) is given back when they have none.
        /// </summary>
        private static Action QuietTheRealInmates(Map map, IEnumerable<Pawn> except)
        {
            var quieted = new List<(Pawn p, CompInmate c, InmateJobDef job)>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
            {
                CompInmate c = p.Inmate();
                if (c == null || c.forcedActivity != null || except.Any(i => i != null && i.Inmate() == c) || ParoleScene.CaseOf(p) != null || ShuttleSystem.OnTheWayOut(p)) continue;
                quieted.Add((p, c, c.job));
                c.forcedActivity = RPSecurityDefOf.RP_Solitary;
                c.forcedUntilTick = -1;
            }
            return () =>
            {
                foreach ((Pawn _, CompInmate c, InmateJobDef job) in quieted)
                {
                    if (c.forcedActivity == RPSecurityDefOf.RP_Solitary && c.forcedUntilTick == -1) c.forcedActivity = null;
                    if (c.job == null && job != null) c.job = job;
                }
            };
        }

        // ---- H1: an open case is remand -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// An inmate on a 12-day sentence kills on day 10: two days on, their sentence is served, but they are held (the case is open); no parole hearing is due and no appeal is heard; once
        /// the court has acquitted them, the same served sentence lets them go (so the hold was the case's).
        /// </summary>
        private static void CheckRegRemandHoldsTheAccused(Map map)
        {
            Guarded("court regression H1: an accused killer is on remand", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller60"), victim = s.Inmate("Zvictim60");
                CompInmate kc = killer.Inmate();
                // (The check lives in the first hours of a game, so the clock is moved on: day 10 of their stay now, day 12 below, and back at the end.)
                int clock0 = s.Now;
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                (kc.sentenceDays, kc.admittedTick, kc.lastHearingTick) = (12, clock0, -999999);
                Find.TickManager.DebugSetTicksGame(clock0 + Days(10));
                DeathAftermath.Notify_Died(victim, killer, null, null);
                MurderTrial t = Trials.TrialOf(g, killer);
                if (t == null)
                {
                    CourtScene.Fail("the killer was charged and no case was opened");
                    return;
                }
                // (What the prosecution has is not what is being tested: nothing, so that the court acquits.)
                t.witnesses.Clear();
                (t.camera, t.weapon, t.caughtAtScene, t.priorRecord) = (false, false, false, 0);
                t.avengers.Clear();
                // Day 12: the sentence is served.
                Find.TickManager.DebugSetTicksGame(clock0 + Days(12.5f));
                if (kc.DaysServed < 12f) CourtScene.Fail($"the clock was not moved on ({kc.DaysServed:F1} days served)");
                ReleaseSystem.ReleaseServedSentences(g, map, killer);
                s.Hour(11);
                if (!killer.IsPrisonerOfColony || killer.guest.Released || g.shuttle.OutboundOf(killer) != null || !g.story.trials.Contains(t))
                    CourtScene.Fail($"a 12-day sentence, a charge on day 10, was served on day 12 and the accused was released (released {killer.guest.Released}, on the way out {g.shuttle.OutboundOf(killer) != null}, case open {g.story.trials.Contains(t)}): the case was dropped");
                if (!Trials.OnRemand(g, killer)) CourtScene.Fail("an accused with an open case is not on remand");
                if (ParoleSystem.HearingDue(g, killer, kc)) CourtScene.Fail("an accused killer on remand was due a parole hearing");
                kc.appealResolveTick = s.Now - 1;
                if (Innocence.Ready(g, killer)) CourtScene.Fail("an accused killer on remand had their appeal heard");
                kc.appealResolveTick = -1;
                // The court acquits (nothing on the case): the served sentence and the hearing are theirs again.
                Trials.Verdict(g, map, t);
                if (g.story.trials.Contains(t) || Trials.OnRemand(g, killer)) CourtScene.Fail("the case was still open after the verdict");
                if (!ParoleSystem.HearingDue(g, killer, kc)) CourtScene.Fail("a parole hearing was not due after the case was over (the hold was not the case's)");
                ReleaseSystem.ReleaseServedSentences(g, map, killer);
                // (A release counts when they leave on the shuttle: here they are released and on the way out.)
                if (!killer.guest.Released || g.shuttle.OutboundOf(killer) is not { kind: OutboundKind.Released })
                    CourtScene.Fail($"after the acquittal the served sentence did not release them (released {killer.guest.Released}, on the way out {g.shuttle.OutboundOf(killer)?.kind.ToString() ?? "no"})");
            });
        }

        /// <summary>The accused escapes and the police are looking: the case stays open, and when they are brought back it is tried (on a new day if the old one passed).</summary>
        private static void CheckRegEscapeeIsTriedAfterRecapture(Map map)
        {
            Guarded("court regression H1: an escapee is tried after the recapture", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller61"), victim = s.Inmate("Zvictim61");
                MurderTrial t = s.Case(killer, victim);
                IntVec3 where = killer.Position;
                killer.DeSpawn();
                var fugitive = new Fugitive { pawn = killer, escapedTick = s.Now, returnTick = s.Now + GenDate.TicksPerDay };
                g.fugitives.Add(fugitive);
                r.Add(() => g.fugitives.Remove(fugitive));
                int trials0 = g.stats.trials;
                s.Hour(11);
                if (!g.story.trials.Contains(t) || t.fledTick < 0) CourtScene.Fail($"the case of an escapee the police are looking for was closed (open {g.story.trials.Contains(t)}, fled {t.fledTick})");
                // The day passes while they are away.
                t.trialTick = s.Now - 100;
                s.Hour(11);
                if (!g.story.trials.Contains(t) || g.stats.trials != trials0) CourtScene.Fail("the court sat on an accused who is on the run");
                // Brought back.
                GenSpawn.Spawn(killer, where, map);
                g.fugitives.Remove(fugitive);
                s.Hour(11);
                if (!g.story.trials.Contains(t) || t.fledTick >= 0 || t.trialTick - s.Now < GenDate.TicksPerDay || g.stats.trials != trials0)
                    CourtScene.Fail($"a recaptured accused was not tried on a new day (open {g.story.trials.Contains(t)}, fled {t.fledTick}, trial in {(t.trialTick - s.Now) / (float)GenDate.TicksPerDay:F1} days, trials +{g.stats.trials - trials0})");
                if (!Trials.FileLines(killer).Any(l => l.Contains(Trials.DateText(t)))) CourtScene.Fail("the accused's file does not give the new day of the trial");
                // One nobody is looking for any more is gone for good.
                MurderTrial lost = s.Case(victim, killer);
                victim.DeSpawn();
                s.Hour(11);
                if (g.story.trials.Contains(lost)) CourtScene.Fail("the case of an accused who is gone for good was not closed");
                GenSpawn.Spawn(victim, where, map);
            });
        }

        /// <summary>Shipping the accused out closes the case (they are gone), and costs the reputation of a case lost to tampering.</summary>
        private static void CheckRegTransferOutClosesTheCaseAtACost(Map map)
        {
            Guarded("court regression H1: a transfer out closes the case at the warden's cost", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller62"), victim = s.Inmate("Zvictim62");
                MurderTrial t = s.Case(killer, victim);
                g.reputation = 50f;
                g.ledger.funds = Math.Max(g.ledger.funds, InmateUtility.TransferCost + 1000);
                InmateUtility.TransferOut(killer, killer.Inmate());
                // (TransferOut itself takes 2: the case's own price is what is looked for.)
                if (g.story.trials.Contains(t) || g.reputation > 50f - 2f - Trials.PressHit + 0.01f)
                    CourtScene.Fail($"a transfer out left the case open ({g.story.trials.Contains(t)}) or cost no more than the transfer's own 2 reputation (reputation 50 -> {g.reputation})");
                if (!s.Told().Any(x => x.Contains("lapsed")) && !r.NewEntries.Any(e => e.text.Contains("lapsed"))) CourtScene.Fail("the chronicle did not say the case lapsed");
            });
        }

        // ---- H2: a real weapon only, and the odds ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A punch is not a weapon (the game names the fighter's own race as the weapon of a bare-handed blow). One officer who saw it convicts about two times in three, surely with the killer
        /// caught at the scene or a camera over it; inmates alone are a contest, three who testify convict surely, two four in five, one never, and tampering swings it.
        /// </summary>
        private static void CheckRegBareHandsAreNoWeaponAndTheOddsHold(Map map)
        {
            Guarded("court regression H2: no weapon in bare hands, and the odds", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkiller63"), victim = s.Inmate("Zvictim63");
                killer.equipment?.DestroyAllEquipment();
                // (One case an accused: a second killing is part of the open one, so each case here has an accused of its own.)
                Pawn killerB = s.Inmate("Zkillerb63"), killerC = s.Inmate("Zkillerc63");
                killerB.equipment?.DestroyAllEquipment();
                killerC.equipment?.DestroyAllEquipment();
                MurderTrial fists = Trials.Open(s.g, map, victim, killer, killer.Inmate(), false, new DamageInfo(DamageDefOf.Blunt, 10f, instigator: killer, weapon: ThingDefOf.Human));
                MurderTrial knife = Trials.Open(s.g, map, victim, killerB, killerB.Inmate(), false, new DamageInfo(DamageDefOf.Stab, 10f, instigator: killerB, weapon: ThingDefOf.MeleeWeapon_Knife));
                MurderTrial none = Trials.Open(s.g, map, victim, killerC, killerC.Inmate(), false, null);
                if (fists.weapon || none.weapon || !knife.weapon) CourtScene.Fail($"weapon in evidence: bare hands {fists.weapon}, no damage source {none.weapon}, a knife {knife.weapon} (only the knife is one)");
                // The odds, case by case (stripped of everything the scene gave them).
                Pawn guard = s.Guard("Zguard63"), guard2 = s.Guard("Zguardb63");
                Pawn[] inmates = { s.Inmate("Zseen63a"), s.Inmate("Zseen63b"), s.Inmate("Zseen63c") };
                float Chance(Action<MurderTrial> build)
                {
                    MurderTrial t = s.Case(killer, victim);
                    build(t);
                    return Trials.ConvictChance(Trials.Strength(t, map, wardenView: false));
                }
                float officerAlone = Chance(t => s.Witness(t, guard, known: true));
                if (officerAlone < 0.6f || officerAlone > 0.75f) CourtScene.Fail($"one officer who saw it convicts {officerAlone:P0} of the time on their own (60-75% wanted)");
                float caught = Chance(t =>
                {
                    s.Witness(t, guard, known: true);
                    t.caughtAtScene = true;
                });
                float onCamera = Chance(t =>
                {
                    s.Witness(t, guard, known: true);
                    t.camera = true;
                });
                if (caught < 1f || onCamera < 1f) CourtScene.Fail($"one officer who saw it, and the killer caught at the scene {caught:P0} or a camera over it {onCamera:P0}, did not convict surely");
                float oneInmate = Chance(t => s.Witness(t, inmates[0]));
                float twoInmates = Chance(t =>
                {
                    s.Witness(t, inmates[0]);
                    s.Witness(t, inmates[1]);
                });
                float threeInmates = Chance(t =>
                {
                    foreach (Pawn p in inmates) s.Witness(t, p);
                });
                if (oneInmate > 0.001f || twoInmates <= 0.5f || twoInmates >= 0.95f || threeInmates < 1f)
                    CourtScene.Fail($"inmate witnesses alone: one {oneInmate:P0}, two {twoInmates:P0}, three {threeInmates:P0} (never, a likely contest, surely)");
                // Tampering swings it: three who testify convict surely; with one got at it is a contest, with all of them it is an acquittal.
                MurderTrial tampered = s.Case(killer, victim);
                var ws = inmates.Select(p => s.Witness(tampered, p)).ToList();
                ws[0].pressure = 1.5f;
                float oneRecants = Trials.ConvictChance(Trials.Strength(tampered, map, wardenView: false));
                ws[1].pressure = ws[2].pressure = 1.5f;
                float allRecant = Trials.ConvictChance(Trials.Strength(tampered, map, wardenView: false));
                if (oneRecants <= 0f || oneRecants >= 1f || allRecant > 0.001f) CourtScene.Fail($"tampering with the witnesses: one got at {oneRecants:P0}, all of them {allRecant:P0} (a contest, then an acquittal)");
            });
        }

        // ---- H3: the truth of a complaint is not to be read off what is public ---------------------------------------------------------------------------

        /// <summary>
        /// A liar can say anything after a restraint, and after a search or a frisk only that they were picked on or beaten (never that they were restrained); some lies come with an old
        /// injury, so an injury is not proof.
        /// </summary>
        private static void CheckRegLiesCoverEveryKindAndLeaveNoTell(Map map)
        {
            Guarded("court regression H3: lies cover every kind", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                var afterRestraint = new HashSet<ComplaintKind>();
                var afterSearch = new HashSet<ComplaintKind>();
                for (int i = 0; i < 600; i++)
                {
                    afterRestraint.Add(ComplaintSystem.LieKind(false));
                    afterSearch.Add(ComplaintSystem.LieKind(true));
                }
                if (afterRestraint.Count != 4) CourtScene.Fail($"after a restraint a liar said only {string.Join("/", afterRestraint)}: a lie covers every kind");
                if (afterSearch.Contains(ComplaintKind.Force) || afterSearch.Contains(ComplaintKind.Punishment) || !afterSearch.Contains(ComplaintKind.Harassment) || !afterSearch.Contains(ComplaintKind.Beating))
                    CourtScene.Fail($"after a search a liar said {string.Join("/", afterSearch)} (a hard word or a beating, never a restraint)");
                // Through the game's own call: a schemer who is searched files only that they were picked on or beaten.
                Pawn officer = s.Guard("Zofficer64");
                var searchKinds = new HashSet<ComplaintKind>();
                int withInjury = 0, withoutInjury = 0;
                for (int i = 0; i < 80; i++)
                {
                    Pawn p = s.Inmate("Zliar64_" + i);
                    p.Inmate().reputations.Add(ReputationDefOf.RP_Instigator);
                    int wounds = p.health.hediffSet.hediffs.Count;
                    Complaint cm = i % 2 == 0 ? ComplaintSystem.Lie(g, officer, p, p.Inmate(), afterSearch: true) : ComplaintSystem.Lie(g, officer, p, p.Inmate(), afterSearch: false);
                    if (cm == null) continue;
                    if (cm.isTrue) CourtScene.Fail("a lie was filed as a true complaint");
                    if (i % 2 == 0) searchKinds.Add(cm.kind);
                    if (p.health.hediffSet.hediffs.Count > wounds) withInjury++;
                    else withoutInjury++;
                }
                if (searchKinds.Contains(ComplaintKind.Force) || searchKinds.Contains(ComplaintKind.Punishment)) CourtScene.Fail($"a lie after a search was filed as {string.Join("/", searchKinds)}");
                if (withInjury == 0 || withoutInjury == 0) CourtScene.Fail($"lies with an old injury {withInjury}, without {withoutInjury}: an injury is proof one way or the other");
            });
        }

        /// <summary>A guard with no brutal trait and good spirits never abuses; the same guard on a bad day (demoralised) sometimes does, so traits are not proof.</summary>
        private static void CheckRegAnyGuardOnABadDayMayAbuse(Map map)
        {
            Guarded("court regression H3: any guard on a bad day may abuse", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zguard65");
                guard.story.traits.allTraits.Clear();
                var inmates = new List<Pawn>();
                for (int i = 0; i < 3; i++) inmates.Add(s.Inmate("Zabused65" + i));
                foreach (Pawn p in inmates) s.Place(p, CellFinder.StandableCellNear(guard.Position, map, 3f));
                QuietTheRealInmates(map, r, inmates);
                int Abused() => inmates.Count(p => StaffMemories.ScoreFor(p.Inmate(), guard) <= -12f);
                if (guard.needs?.mood == null)
                {
                    CourtScene.Fail("the guard has no mood to be put in");
                    return;
                }
                // (The hourly pass skips a guard asleep, and the loops run no ticks: a fresh guard can be put to bed at an hour the shifts call sleep.)
                if (!guard.Awake()) RestUtility.WakeUp(guard);
                guard.needs.mood.CurLevel = 0.9f;
                if (ComplaintSystem.BadDay(guard) || ComplaintSystem.Brutality(guard) >= ComplaintSystem.AbuseFrom) CourtScene.Fail("a guard in good spirits with no traits is having a bad day");
                for (int i = 0; i < 8000; i++) ComplaintSystem.HourlyGuards(g, map);
                if (Abused() != 0) CourtScene.Fail("a guard in good spirits, with no brutal trait, abused someone");
                guard.needs.mood.CurLevel = 0.05f;
                if (!ComplaintSystem.BadDay(guard) || ComplaintSystem.Brutality(guard) >= ComplaintSystem.AbuseFrom) CourtScene.Fail($"a demoralised guard is not having a bad day, or is brutal by trait (brutality {ComplaintSystem.Brutality(guard):F1})");
                for (int i = 0; i < 8000; i++) ComplaintSystem.HourlyGuards(g, map);
                if (Abused() == 0)
                    CourtScene.Fail($"8000 hours of a demoralised guard with no brutal trait next to three inmates and not one was abused (awake {guard.Awake()}, downed {guard.Downed}, mental state {guard.InMentalState}, job {guard.CurJobDef?.defName ?? "none"}, "
                                    + $"lockdown {g.LockdownActive}, riot {RiotPolice.RiotOngoing(map)}, hostage crisis {HostageCrisis.Running(map) != null}, a target {ComplaintSystem.AbuseTarget(guard, map, g)?.LabelShort ?? "none"})");
            });
        }

        /// <summary>
        /// A lock-up a guard had no right to give is on the file as an ordinary sanction for a made-up reason, the one the warden's own policy gives (<see cref="ComplaintSystem.AbuseOffence"/>),
        /// so that nothing on the file is odd. The policy-driven rules (and what they must not do) are checked in <see cref="CheckGa4AbuseFollowsThePolicy"/>.
        /// </summary>
        private static void CheckRegAbuseLockUpIsAnOrdinarySanction(Map map)
        {
            Guarded("court regression H3: an abuse lock-up looks like any other", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zguard66"), inmate = s.Inmate("Zlocked66");
                CompInmate c = inmate.Inmate();
                c.forcedActivity = null;
                c.pendingPunishmentHours = 0;
                PunishmentPolicy policy0 = g.punishments;
                r.Add(() => g.punishments = policy0);
                g.punishments = new PunishmentPolicy();
                MisconductDef offence = ComplaintSystem.AbuseOffence(g);
                ComplaintSystem.Abuse(g, guard, inmate, ComplaintKind.Punishment);
                MisconductRecord rec = c.records.FirstOrDefault(x => x.def == offence);
                bool locked = c.forcedActivity == RPDefOf.RP_Lockup || c.pendingPunishmentHours > 0;
                if (rec == null || !rec.seen || !locked) CourtScene.Fail($"an abuse lock-up left no ordinary record to explain it (record {rec != null}, seen {rec?.seen}, locked up {locked}): the file shows a lock-up with no reason");
                else if (c.forcedActivity == RPDefOf.RP_Lockup && !rec.punished) CourtScene.Fail("the record behind an abuse lock-up is not marked as punished");
            });
        }

        // ---- H4 ------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Three upheld complaints are a disciplinary suspension, not an internal affairs case: nothing is added to the suspicion, no IA letter, and when it ends the officer is simply back
        /// (a corrupt one is not fired or arrested for it, a clean one's suspicion is not wiped). An IA suspension still reports (the control).
        /// </summary>
        private static void CheckRegDisciplineIsNotInternalAffairs(Map map)
        {
            Guarded("court regression H4: a disciplinary suspension is not an internal affairs one", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn bent = s.Guard("Zbent67"), clean = s.Guard("Zclean67"), suspect = s.Guard("Zsuspect67");
                CompStaff bc = bent.GetComp<CompStaff>(), cc = clean.GetComp<CompStaff>(), sc = suspect.GetComp<CompStaff>();
                bc.corrupt = true;
                bc.suspicion = 30f;
                bc.evidence.Add("RP_Sus_Traced");
                cc.suspicion = 30f;
                cc.evidence.Add("RP_Sus_Traced");
                int cases0 = g.stats.iaCases;
                foreach (Pawn officer in new[] { bent, clean })
                    for (int n = 1; n <= 3; n++)
                        ComplaintSystem.Uphold(g, map, new Complaint { id = 700 + n, inmateId = -1, inmateName = "Zcomplainant67" + n, officerId = officer.thingIDNumber, officerName = officer.LabelShort, stage = ComplaintStage.Waiting }, "RP_Chr_ComplaintUpheld");
                if (!CorruptionSystem.Disciplined(bc) || !CorruptionSystem.Disciplined(cc) || CorruptionSystem.IaSuspended(bc) || CorruptionSystem.IaSuspended(cc) || bc.iaOpen || cc.iaOpen || g.stats.iaCases != cases0 || s.Asked("RP_Decision_InternalAffairs") != null)
                    CourtScene.Fail($"three upheld complaints: disciplined {CorruptionSystem.Disciplined(bc)}/{CorruptionSystem.Disciplined(cc)}, IA suspended {CorruptionSystem.IaSuspended(bc)}/{CorruptionSystem.IaSuspended(cc)}, IA cases +{g.stats.iaCases - cases0}");
                if (bc.suspicion != 30f || cc.suspicion != 30f || bc.evidence.Count != 1 || cc.evidence.Count != 1) CourtScene.Fail($"upheld complaints changed internal affairs' suspicion or evidence ({bc.suspicion}/{cc.suspicion}, {bc.evidence.Count}/{cc.evidence.Count})");
                // The suspension runs out.
                foreach (CompStaff x in new[] { bc, cc }) x.disciplineUntilTick = s.Now - 1;
                bool bentBack = CorruptionSystem.TickSuspensions(bent, bc), cleanBack = CorruptionSystem.TickSuspensions(clean, cc);
                if (!bentBack || !cleanBack || bent.Destroyed || bent.Faction != Faction.OfPlayer || bc.exposedCorrupt || s.Told().Any(x => x.StartsWith("RP_IAGuiltyLabel".Translate().Resolve())))
                    CourtScene.Fail($"a corrupt officer was fired or exposed when their disciplinary suspension ended (back {bentBack}, faction {bent.Faction?.Name}, exposed {bc.exposedCorrupt})");
                if (bc.disciplineUntilTick >= 0 || bc.suspicion != 30f || cc.suspicion != 30f || bc.evidence.Count != 1 || cc.evidence.Count != 1)
                    CourtScene.Fail($"the end of a disciplinary suspension touched internal affairs' record (suspicion {bc.suspicion}/{cc.suspicion}, evidence {bc.evidence.Count}/{cc.evidence.Count})");
                // The control: a suspension of internal affairs' reports, and clears a clean officer's record.
                sc.suspicion = 30f;
                sc.evidence.Add("RP_Sus_Traced");
                CorruptionSystem.Suspend(suspect, sc);
                if (!CorruptionSystem.IaSuspended(sc) || CorruptionSystem.Disciplined(sc)) CourtScene.Fail("an internal affairs suspension was not one");
                sc.suspendedUntilTick = s.Now - 1;
                CorruptionSystem.TickSuspensions(suspect, sc);
                if (sc.suspicion != 0f || sc.evidence.Count != 0) CourtScene.Fail("an internal affairs suspension of a clean officer did not clear their record (the control)");
            });
        }

        // ---- M1 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The killer's gang gets at witnesses from the day the case is assembled, not before; in protective custody the pressure halves every day; the warden's view of a protected witness is honest.</summary>
        private static void CheckRegPressureWaitsForTheCaseAndFadesInProtection(Map map)
        {
            Guarded("court regression M1: pressure starts at the assembly and fades in protection", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                SecurityLevelDef pc = Trials.ProtectiveLevel;
                Pawn killer = s.Inmate("Zkiller68", s.ravens), victim = s.Inmate("Zvictim68"), witness = s.Inmate("Zwitness68");
                QuietTheRealInmates(map, r, new[] { killer, victim, witness });
                MurderTrial t = s.Case(killer, victim);
                TrialWitness w = s.Witness(t, witness);
                t.assembleTick = s.Now + GenDate.TicksPerDay;
                for (int i = 0; i < 400; i++) s.Hour(11);
                if (w.pressure > 0f || t.assembled) CourtScene.Fail($"a witness was got at before the case was assembled (pressure {w.pressure:F1}): the gang does not know who will testify yet");
                t.assembleTick = s.Now;
                s.Hour(11);
                for (int i = 0; i < 400 && w.pressure <= 0f; i++) s.Hour(11);
                if (!t.assembled || w.pressure <= 0f) CourtScene.Fail($"a witness was never got at after the case was assembled (assembled {t.assembled}, pressure {w.pressure:F1})");
                // In protective custody (set on the file: the beds are not the point) the pressure halves a day.
                if (pc == null)
                {
                    CourtScene.Fail("no protective custody level to test with");
                    return;
                }
                SecurityLevelDef was = witness.Inmate().security;
                witness.Inmate().fear = 0f;
                w.pressure = 1f;
                t.assembled = false;
                t.assembleTick = s.Now + 10 * GenDate.TicksPerDay;
                for (int i = 0; i < 24; i++) s.Hour(11);
                if (w.pressure < 0.99f) CourtScene.Fail($"pressure on a witness who is not protected faded ({w.pressure:F2})");
                witness.Inmate().security = pc;
                w.pressure = 1f;
                for (int i = 0; i < 24; i++) s.Hour(11);
                if (w.pressure < 0.45f || w.pressure > 0.55f || Trials.Recants(w, witness.Inmate())) CourtScene.Fail($"a day in protective custody left the pressure at {w.pressure:F2} (half was wanted; recants {Trials.Recants(w, witness.Inmate())})");
                // The warden's own view: a known threat counts against a witness on the wing, not against one in protective custody.
                (w.known, w.pressureKnown) = (true, true);
                t.assembled = true;
                float inCustody = Trials.Strength(t, map, wardenView: true);
                witness.Inmate().security = was;
                float onTheWing = Trials.Strength(t, map, wardenView: true);
                if (inCustody <= onTheWing) CourtScene.Fail($"the warden's view of a protected witness ({inCustody:F2}) is no better than of one on the wing who was threatened ({onTheWing:F2})");
            });
        }

        // ---- M2 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>One lie per inmate per shakedown; a letter nobody answered is a half-strength dismissal with no feeling of being ignored.</summary>
        private static void CheckRegFalseComplaintsAreRarerAndSoftlyDismissed(Map map)
        {
            Guarded("court regression M2: one lie per shakedown, a soft dismissal when unanswered", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer69"), schemer = s.Inmate("Zschemer69");
                schemer.Inmate().reputations.Add(ReputationDefOf.RP_Instigator);
                int shakedown0 = g.shakedownUntilTick;
                r.Add(() => g.shakedownUntilTick = shakedown0);
                g.shakedownUntilTick = s.Now + PrisonGame.ShakedownHours * GenDate.TicksPerHour;
                Complaint first = null;
                for (int i = 0; i < 400 && first == null; i++)
                {
                    ComplaintSystem.Notify_Searched(officer, schemer, found: true);
                    first = ComplaintSystem.OpenBy(g, schemer.thingIDNumber);
                }
                if (first == null)
                {
                    CourtScene.Fail("a schemer whose contraband was found filed nothing in 400 searches during a shakedown");
                    return;
                }
                // Closed long enough ago to file again: the shakedown still allows only the one.
                first.stage = ComplaintStage.Closed;
                first.closedTick = s.Now - Days(5);
                for (int i = 0; i < 400; i++) ComplaintSystem.Notify_Searched(officer, schemer, found: true);
                if (g.story.complaints.Count != 1) CourtScene.Fail($"a second lie by the same inmate in one shakedown ({g.story.complaints.Count} complaints)");
                g.shakedownUntilTick = -1;
                for (int i = 0; i < 400 && g.story.complaints.Count < 2; i++) ComplaintSystem.Notify_Searched(officer, schemer, found: true);
                if (g.story.complaints.Count < 2) CourtScene.Fail("the one-lie rule outlived the shakedown");
                // An unanswered letter.
                Pawn inmate = s.Inmate("Zcomplainant69"), friend = s.Inmate("Zfriend69");
                if (!friend.relations.DirectRelationExists(RPRelationDefOf.RP_Friend, inmate)) friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, inmate);
                r.Add(() => friend.relations.RemoveDirectRelation(RPRelationDefOf.RP_Friend, inmate));
                (inmate.Inmate().tension, friend.Inmate().tension) = (20f, 20f);
                (Complaint cm, ChoiceLetter_PrisonDecision letter) = RaiseComplaint(s, inmate, s.Guard("Zofficer69b"), ComplaintKind.Beating, true, camera: false);
                if (cm == null || letter == null)
                {
                    CourtScene.Fail("a complaint did not reach the desk");
                    return;
                }
                letter.disappearAtTick = s.Now - 1;
                int ignored0 = inmate.needs.mood.thoughts.memories.Memories.Count(m => m.def == RPComplaintDefOf.RP_WardenIgnored);
                PrisonDecisions.Choose(letter, letter.decision.Worker.TimeoutOption(letter));
                float rise = inmate.Inmate().tension - 20f;
                if (cm.outcome != ComplaintOutcome.Dismissed || rise < 7f || rise > 8f || inmate.needs.mood.thoughts.memories.Memories.Count(m => m.def == RPComplaintDefOf.RP_WardenIgnored) != ignored0)
                    CourtScene.Fail($"an unanswered letter was {cm.outcome}, tension +{rise:F1} (half of a dismissal's 15), and the inmate felt ignored {inmate.needs.mood.thoughts.memories.Memories.Count(m => m.def == RPComplaintDefOf.RP_WardenIgnored) - ignored0}");
                if (friend.Inmate().tension > 20f + 4f) CourtScene.Fail($"a soft dismissal raised the friend's tension by {friend.Inmate().tension - 20f:F1}");
            });
        }

        // ---- M3 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>An investigation stands the officer down (home, on full pay) until it reports, and the warden can have two at once.</summary>
        private static void CheckRegInvestigatingCostsSomething(Map map)
        {
            Guarded("court regression M3: investigating costs something", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn[] officers = { s.Guard("Zofficer70a"), s.Guard("Zofficer70b"), s.Guard("Zofficer70c") };
                Pawn[] inmates = { s.Inmate("Zcomplainant70a"), s.Inmate("Zcomplainant70b"), s.Inmate("Zcomplainant70c") };
                // (Two letters wait on the desk at a time: the third complaint reaches it once the first two have been sent off to be investigated.)
                var raised = new List<(Complaint cm, ChoiceLetter_PrisonDecision letter)>();
                for (int i = 0; i < 2; i++) raised.Add(RaiseComplaint(s, inmates[i], officers[i], ComplaintKind.Harassment, true, camera: true));
                if (raised.Any(x => x.letter == null))
                {
                    CourtScene.Fail("a complaint did not reach the desk");
                    return;
                }
                CompStaff first = officers[0].GetComp<CompStaff>();
                if (new PrisonDecisionWorker_Complaint().Disabled(raised[0].letter, 2) != null) CourtScene.Fail("investigating was refused with nothing under way");
                PrisonDecisions.Choose(raised[0].letter, 2);
                if (!CorruptionSystem.StoodDown(first) || GuardUtility.IsGuard(officers[0]) || !CorruptionSystem.Suspended(first) || first.standDownUntilTick != raised[0].cm.dueTick || CorruptionSystem.Disciplined(first))
                    CourtScene.Fail($"the officer under investigation was not stood down until it reports (stood down {CorruptionSystem.StoodDown(first)}, still a guard {GuardUtility.IsGuard(officers[0])})");
                PrisonDecisions.Choose(raised[1].letter, 2);
                raised.Add(RaiseComplaint(s, inmates[2], officers[2], ComplaintKind.Harassment, true, camera: true));
                if (raised[2].letter == null)
                {
                    CourtScene.Fail("the third complaint did not reach the desk");
                    return;
                }
                string full = new PrisonDecisionWorker_Complaint().Disabled(raised[2].letter, 2);
                if (full == null) CourtScene.Fail("a third investigation was offered while two were under way");
                ComplaintSystem.Decide(g, map, raised[2].cm, 2);
                if (raised[2].cm.stage != ComplaintStage.Waiting) CourtScene.Fail("a third investigation was started while two were under way");
                // One reports: the officer is back, and the third can go ahead.
                raised[0].cm.dueTick = s.Now;
                ComplaintSystem.Hourly(g, map);
                if (CorruptionSystem.StoodDown(first) || new PrisonDecisionWorker_Complaint().Disabled(raised[2].letter, 2) != null)
                    CourtScene.Fail($"after the investigation reported the officer was still stood down ({CorruptionSystem.StoodDown(first)}) or the next one was still refused");
            });
        }

        // ---- M4 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A cell turned over is complained of only by an owner who was there (or near), and what there is to see is read at the cell, not wherever they happen to be.</summary>
        private static void CheckRegCellSearchComplaintNeedsTheOwnerThere(Map map)
        {
            Guarded("court regression M4: a cell search is complained of by someone who was there", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zguard71"), away = s.Inmate("Zaway71"), present = s.Inmate("Zpresent71");
                IntVec3 far = s.FarFrom(guard.Position);
                if (!far.IsValid)
                {
                    CourtScene.Fail("no cell far from the guard to put the absent owner in");
                    return;
                }
                s.Place(away, far);
                s.Place(present, CellFinder.StandableCellNear(guard.Position, map, 4f));
                foreach (Pawn p in new[] { away, present }) p.Inmate().reputations.Add(ReputationDefOf.RP_Instigator);
                IntVec3 cell = guard.Position;
                for (int i = 0; i < 400; i++) ComplaintSystem.Notify_Searched(guard, away, found: true, cellAt: cell);
                if (ComplaintSystem.OpenBy(g, away.thingIDNumber) != null) CourtScene.Fail("an owner who was far from their cell when it was searched complained of the search");
                Complaint cm = null;
                for (int i = 0; i < 400 && cm == null; i++)
                {
                    ComplaintSystem.Notify_Searched(guard, present, found: true, cellAt: cell);
                    cm = ComplaintSystem.OpenBy(g, present.thingIDNumber);
                }
                if (cm == null) CourtScene.Fail("an owner who was near their cell when it was searched never complained");
                else if (cm.spot != cell) CourtScene.Fail($"a cell-search complaint reads its evidence at {cm.spot}, not at the cell {cell} (where the inmate stood: {present.Position})");
            });
        }

        // ---- M5 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A witness beaten for what they know: nothing moves that the warden can see (no misconduct or assault numbers, the pressurer's tension, the witness's shown cause); a witness who is
        /// already hurt is threatened, not beaten.
        /// </summary>
        private static void CheckRegHiddenBeatingLeavesNoTrace(Map map)
        {
            Guarded("court regression M5: a hidden beating leaves no trace", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller72", s.ravens), victim = s.Inmate("Zvictim72"), witness = s.Inmate("Zwitness72"), hurt = s.Inmate("Zhurt72");
                QuietTheRealInmates(map, r, new[] { killer, victim, witness, hurt });
                MurderTrial t = s.Case(killer, victim);
                TrialWitness w = s.Witness(t, witness), wh = s.Witness(t, hurt);
                killer.Inmate().tension = 50f;
                (int misconducts, int assaults, int todayMisconducts, int todayViolent, float tension) = (g.stats.misconducts, g.stats.assaults, g.today.misconducts, g.today.violent, killer.Inmate().tension);
                int wounds = witness.health.hediffSet.hediffs.Count;
                if (!Trials.TryPressure(g, map, t, w, witness, beating: true, allowReport: false)) CourtScene.Fail("the killer could not get at a witness");
                if (witness.health.hediffSet.hediffs.Count <= wounds || w.pressure < Trials.BeatingPressure) CourtScene.Fail("a beating did not hurt the witness");
                if (g.stats.misconducts != misconducts || g.stats.assaults != assaults || g.today.misconducts != todayMisconducts || g.today.violent != todayViolent || killer.Inmate().tension != tension)
                    CourtScene.Fail($"a hidden beating moved what the warden can see: misconducts {g.stats.misconducts - misconducts}, assaults {g.stats.assaults - assaults}, today {g.today.misconducts - todayMisconducts}/{g.today.violent - todayViolent}, the pressurer's tension {killer.Inmate().tension - tension}");
                if (witness.Inmate().mainCause == "RP_Cause_Unsafe") CourtScene.Fail("a beaten witness's file says they are afraid for their safety");
                if (!killer.Inmate().records.Any(x => x.def == RPMisconductDefOf.RP_Fight && !x.seen)) CourtScene.Fail("a hidden beating left no unseen trace on the pressurer's own file");
                // Already hurt: threatened, not beaten.
                // (Downed is not the same as unfit: forcing a pawn down can take one wound on an arm, and a pawn whose summary health is still at 0.6 or more is beaten, as a fit one is. The
                // check is about the unfit, so the set-up sees to it: a few blows to the torso until they are, which is what made it fail now and then.)
                HealthUtility.DamageUntilDowned(hurt, allowBleedingWounds: false);
                for (int blow = 0; blow < 12 && ComplaintSystem.Fit(hurt) && !hurt.Dead; blow++)
                    hurt.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 5f, 0f, -1f, null, hurt.RaceProps.body.corePart));
                // (Dead is not unfit: the blows to the torso can kill, and nobody gets at the dead.)
                if (ComplaintSystem.Fit(hurt) || hurt.Dead)
                {
                    RPLog.Message($"[RimPrison:test] court regression M5: the witness could not be made unfit (dead {hurt.Dead}), the 'already badly hurt' part was not checked");
                    return;
                }
                int hurtWounds = hurt.health.hediffSet.hediffs.Count;
                var before = new List<Hediff>(hurt.health.hediffSet.hediffs);
                Pawn pressurer = Trials.Pressurer(map, t, hurt);
                bool pressed = Trials.TryPressure(g, map, t, wh, hurt, beating: true, allowReport: false);
                if (hurt.health.hediffSet.hediffs.Count != hurtWounds || wh.pressure != Trials.ThreatPressure)
                    CourtScene.Fail($"a witness who was already badly hurt was beaten again (wounds {hurtWounds} -> {hurt.health.hediffSet.hediffs.Count}, pressure {wh.pressure}, got at {pressed} by {pressurer?.LabelShort ?? "nobody"}, downed {hurt.Downed}, dead {hurt.Dead}, fit {ComplaintSystem.Fit(hurt)}, new {string.Join(", ", hurt.health.hediffSet.hediffs.Where(h => !before.Contains(h)).Select(h => $"{h.def.defName} on {h.Part?.def.defName} {h.Severity:F2}"))})");
            });
        }

        // ---- L1 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The trial is set for a court morning four to five days on, whatever the hour of the charge (a killing after the court rises is not tried a day early).</summary>
        private static void CheckRegTrialDateIsADayTheCourtSits(Map map)
        {
            Guarded("court regression L1: the trial date is a day the court sits", map, r =>
            {
                var bad = new List<string>();
                for (int hour = 0; hour < 24; hour++)
                {
                    int tick = Trials.SittingTick(0, hour, Trials.TrialDelayDays);
                    int hourThen = (hour + tick / GenDate.TicksPerHour) % 24;
                    if (hourThen != Trials.CourtStartHour || tick < Trials.TrialDelayDays * GenDate.TicksPerDay || tick >= (Trials.TrialDelayDays + 1) * GenDate.TicksPerDay) bad.Add($"{hour}h -> {tick / (float)GenDate.TicksPerDay:F2} days, {hourThen}h");
                }
                if (bad.Count > 0) CourtScene.Fail($"a trial was set for a time other than a court morning four to five days on: {string.Join("; ", bad.Take(4))}");
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkiller73"), victim = s.Inmate("Zvictim73");
                MurderTrial t = s.Case(killer, victim);
                int hoursOn = (t.trialTick - s.Now) / GenDate.TicksPerHour;
                if ((GenLocalDate.HourOfDay(map) + hoursOn) % 24 != Trials.CourtStartHour) CourtScene.Fail($"a case opened now is set {hoursOn} hours on, which is not a court morning (it is {(GenLocalDate.HourOfDay(map) + hoursOn) % 24}h)");
            });
        }

        // ---- L2 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The case the warden reads does not list witnesses who have gone (dead, released, shipped out, off the staff) as testifying.</summary>
        private static void CheckRegKnownCaseLeavesOutWitnessesWhoAreGone(Map map)
        {
            Guarded("court regression L2: witnesses who are gone are not listed", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkiller74"), victim = s.Inmate("Zvictim74"), kept = s.Inmate("Zkept74"), gone = s.Inmate("Zgone74");
                Pawn officer = s.Guard("Zofficer74"), leaver = s.Guard("Zleaver74");
                MurderTrial t = s.Case(killer, victim);
                s.Witness(t, kept, known: true);
                s.Witness(t, gone, known: true);
                s.Witness(t, officer, known: true);
                s.Witness(t, leaver, known: true);
                gone.Destroy(DestroyMode.Vanish);
                leaver.Destroy(DestroyMode.Vanish);
                string text = string.Join(" / ", Trials.KnownCase(t, map));
                if (text.Contains(gone.LabelShort) || text.Contains(leaver.LabelShort) || !text.Contains(kept.LabelShort) || !text.Contains(officer.LabelShort))
                    CourtScene.Fail($"the case as the warden reads it lists witnesses who are gone, or leaves out those who are here ({text})");
            });
        }

        // ---- L3 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Moving witnesses to protective custody is not offered where nobody can be moved; after the trial they go back to the wing they came from (a daily pass retries one who had no room),
        /// unless they are vulnerable.
        /// </summary>
        private static void CheckRegProtectionIsOfferedOnlyWhereItCanBeGiven(Map map)
        {
            Guarded("court regression L3: protection is offered only where it can be given, and ends with the trial", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                SecurityLevelDef pc = Trials.ProtectiveLevel;
                if (pc == null)
                {
                    CourtScene.Fail("no protective custody level to test with");
                    return;
                }
                Pawn killer = s.Inmate("Zkiller75"), victim = s.Inmate("Zvictim75"), witness = s.Inmate("Zwitness75"), exposed = s.Inmate("Zexposed75"), waiting = s.Inmate("Zwaiting75");
                MurderTrial t = s.Case(killer, victim);
                foreach (Pawn p in new[] { witness, exposed, waiting }) s.Witness(t, p);
                Trials.Assemble(g, map, t);
                ChoiceLetter_PrisonDecision letter = s.Asked("RP_Decision_TrialCase");
                if (letter == null)
                {
                    CourtScene.Fail("the assembled case did not ask about the witnesses");
                    return;
                }
                SecurityLevelDef was = witness.Inmate().security;
                // Everyone who came forward is in protective custody already (set on the file: the beds are not the point): nothing to offer.
                foreach (Pawn p in new[] { witness, exposed, waiting })
                {
                    p.Inmate().security = pc;
                    p.Inmate().protectedFrom = was;
                }
                if (new PrisonDecisionWorker_TrialCase().Disabled(letter, LetterOption(letter, "RP_TrialCase_Protect")) == null) CourtScene.Fail("moving witnesses to protective custody was offered when nobody could be moved");
                // A bed on the old level for two of them (a bed of their own outside any wing is room anywhere that is not a wing of its own).
                Building_Bed GiveBed(Pawn p)
                {
                    var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
                    bed.SetFactionDirect(Faction.OfPlayer);
                    GenSpawn.Spawn(bed, CellFinder.StandableCellNear(p.Position, map, 4f), map);
                    r.Add(() =>
                    {
                        if (!bed.Destroyed) bed.Destroy(DestroyMode.Vanish);
                    });
                    bed.ForPrisoners = true;
                    p.ownership.ClaimBedIfNonMedical(bed);
                    return bed;
                }
                GiveBed(witness);
                GiveBed(exposed);
                if (!InmateUtility.HasRoomIn(witness, was) || !Unlocks.IsUnlocked(was))
                {
                    RPLog.Message($"[RimPrison:test] court regression L3: the witness could not be given room on {was.defName}; the return was not exercised");
                    return;
                }
                // (A free bed on the old level, if the prison has one, is room for the one without a bed of their own too: then that part is not exercised.)
                bool noRoomCase = !InmateUtility.HasRoomIn(waiting, was);
                if (!noRoomCase) RPLog.Message("[RimPrison:test] court regression L3: the prison has a free bed on the old level; the witness who finds no room was not exercised");
                exposed.Inmate().snitchExposed = true;
                Trials.Verdict(g, map, t);
                if (witness.Inmate().security != was || witness.Inmate().protectedFrom != null) CourtScene.Fail($"a witness moved to protective custody for a case stayed there after the trial ({witness.Inmate().security?.defName}, was {was.defName})");
                if (exposed.Inmate().security != pc || exposed.Inmate().protectedFrom != null) CourtScene.Fail("a vulnerable witness was sent back to the wing after the trial (or kept a note to go back)");
                if (noRoomCase && (waiting.Inmate().security != pc || waiting.Inmate().protectedFrom != was)) CourtScene.Fail("a witness with no room on their old wing was moved anyway (or forgot where they came from)");
                // Room is found: the daily pass sends them back.
                GiveBed(waiting);
                Trials.Daily(g, map);
                if (waiting.Inmate().security != was || waiting.Inmate().protectedFrom != null) CourtScene.Fail("a witness who had no room to go back to was not sent back by the daily pass once there was one");
            });
        }

        // ---- L4 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>An informant who has left (shipped out, released) cannot be asked to testify.</summary>
        private static void CheckRegInformantWhoLeftCannotBeAsked(Map map)
        {
            Guarded("court regression L4: an informant who has left cannot be asked", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller76", s.ravens), victim = s.Inmate("Zvictim76"), informant = s.Inmate("Zsnitch76");
                CompInmate ic = informant.Inmate();
                ic.reputations.Add(ReputationDefOf.RP_Snitch);
                ic.revealed.Add(ReputationDefOf.RP_Snitch);
                MurderTrial t = s.Case(killer, victim);
                Trials.Assemble(g, map, t);
                ChoiceLetter_PrisonDecision ask = s.Asked("RP_Decision_TrialCase");
                if (ask == null)
                {
                    CourtScene.Fail("the assembled case did not ask the warden about the known informant");
                    return;
                }
                if (new PrisonDecisionWorker_TrialCase().Disabled(ask, LetterOption(ask, "RP_TrialCase_Ask")) != null) CourtScene.Fail("asking an informant who is here was refused");
                informant.DeSpawn();
                if (new PrisonDecisionWorker_TrialCase().Disabled(ask, LetterOption(ask, "RP_TrialCase_Ask")) == null) CourtScene.Fail("asking an informant who has left was offered");
                if (Trials.AskInformant(map, t, informant) || t.informantId >= 0) CourtScene.Fail("an informant who has left was asked to testify");
                GenSpawn.Spawn(informant, s.Cell(0), map);
            });
        }

        // ---- L5 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Upholding a complaint against an officer who has gone says so, in the chronicle too (never "upheld - .").</summary>
        private static void CheckRegUpholdWithTheOfficerGoneSaysSo(Map map)
        {
            Guarded("court regression L5: upholding against an officer who left", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer77"), inmate = s.Inmate("Zcomplainant77");
                (Complaint cm, ChoiceLetter_PrisonDecision letter) = RaiseComplaint(s, inmate, officer, ComplaintKind.Beating, true, camera: false);
                if (cm == null || letter == null)
                {
                    CourtScene.Fail("a complaint did not reach the desk");
                    return;
                }
                officer.Destroy(DestroyMode.Vanish);
                PrisonDecisions.Choose(letter, 0);
                string gone = "RP_DisciplineGone".Translate().Resolve();
                if (!r.NewEntries.Any(e => e.text.Contains(gone)) || r.NewEntries.Any(e => e.text.Contains("- .")))
                    CourtScene.Fail($"an upheld complaint against an officer who left was written up as: {string.Join(" / ", r.NewEntries.Select(e => e.text))}");
            });
        }

        // ---- L6 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A guard's abuse does not pick on someone in a mental state, in protective custody or under lockdown (or during a riot or a lockdown at all).</summary>
        private static void CheckRegAbuseTargetsAreNotJustAnyone(Map map)
        {
            Guarded("court regression L6: who a guard can abuse", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                SecurityLevelDef pc = Trials.ProtectiveLevel;
                Pawn guard = s.Guard("Zguard78"), inmate = s.Inmate("Zabused78");
                s.Place(inmate, CellFinder.StandableCellNear(guard.Position, map, 3f));
                QuietTheRealInmates(map, r, new[] { inmate });
                CompInmate c = inmate.Inmate();
                if (ComplaintSystem.AbuseTarget(guard, map, g) != inmate)
                {
                    RPLog.Message("[RimPrison:test] court regression L6: the inmate beside the guard was not a candidate to begin with (out of sight?); the exclusions were not exercised");
                    return;
                }
                SecurityLevelDef was = c.security;
                if (pc != null)
                {
                    c.security = pc;
                    if (ComplaintSystem.AbuseTarget(guard, map, g) != null) CourtScene.Fail("a guard could abuse an inmate in protective custody");
                    c.security = was;
                }
                g.wingLockdowns[was] = s.Now + GenDate.TicksPerHour;
                r.Add(() => g.wingLockdowns.Remove(was));
                if (ComplaintSystem.AbuseTarget(guard, map, g) != null) CourtScene.Fail("a guard could abuse an inmate under lockdown");
                g.wingLockdowns.Remove(was);
                if (inmate.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true))
                {
                    if (ComplaintSystem.AbuseTarget(guard, map, g) != null) CourtScene.Fail("a guard could abuse an inmate in a mental state");
                    inmate.MentalState?.RecoverFromState();
                }
                else RPLog.Message("[RimPrison:test] court regression L6: the inmate could not be put in a mental state; that exclusion was not exercised");
            });
        }

        // ---- L8 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A true complaint that never reaches the desk (the inmate left first) is not counted against the officer.</summary>
        private static void CheckRegTrueComplaintsAreCountedAtTheDesk(Map map)
        {
            Guarded("court regression L8: true complaints are counted when they reach the desk", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer79"), inmate = s.Inmate("Zcomplainant79");
                CompStaff sc = officer.GetComp<CompStaff>();
                Complaint cm = ComplaintSystem.File(g, map, inmate, officer, ComplaintKind.Beating, isTrue: true);
                inmate.Destroy(DestroyMode.Vanish);
                cm.dueTick = s.Now;
                ComplaintSystem.Hourly(g, map);
                if (g.story.complaints.Contains(cm) || sc.trueComplaints != 0) CourtScene.Fail($"a complaint that never reached the desk was dropped {!g.story.complaints.Contains(cm)} and counted against the officer ({sc.trueComplaints})");
            });
        }

        // ---- L9 --------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The hourly pass runs once per home map, over the game's cases and complaints: on a map the accused or the complainant is not on, their case and complaint are left alone (the pass of the
        /// map they are on looks after them). The other map here is a small one nobody's home, passed to the pass by hand (the game never runs it there).
        /// </summary>
        private static void CheckRegAnotherHomeMapDoesNotDropTheCases(Map home)
        {
            Guarded("court regression L9: another map's pass leaves the cases alone", home, r =>
            {
                var parent = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Debug_Arena);
                var around = new List<PlanetTile>();
                bool NoRoad(PlanetTile t)
                {
                    Find.WorldGrid.GetTileNeighbors(t, around);
                    return around.All(n => Find.WorldGrid.GetRoadDef(t, n) == null);
                }
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, validator: NoRoad))
                {
                    problems0.Add("court regression L9: no world tile for a second map");
                    return;
                }
                parent.Tile = tile;
                parent.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(parent);
                Map second = null;
                r.Add(() =>
                {
                    if (second != null && Find.Maps.Contains(second)) Current.Game.DeinitAndRemoveMap(second, notifyPlayer: false);
                    if (!parent.Destroyed) parent.Destroy();
                });
                second = GetOrGenerateMapUtility.GetOrGenerateMap(parent.Tile, new IntVec3(40, 1, 40), null);
                var arena = parent.GetComponent<DebugArena>();
                arena.lhs = new List<Pawn>();
                arena.rhs = new List<Pawn>();
                arena.callback = _ => { };
                var s = new CourtScene(home, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller80"), victim = s.Inmate("Zvictim80"), officer = s.Guard("Zofficer80"), inmate = s.Inmate("Zcomplainant80");
                MurderTrial t = s.Case(killer, victim);
                Complaint cm = ComplaintSystem.File(g, home, inmate, officer, ComplaintKind.Harassment, isTrue: false);
                if (cm == null)
                {
                    CourtScene.Fail("a complaint was not filed");
                    return;
                }
                cm.dueTick = s.Now;
                Trials.Hourly(g, second, 11);
                ComplaintSystem.Hourly(g, second);
                if (!g.story.trials.Contains(t)) CourtScene.Fail("another map's hourly pass closed a case whose accused is on the home map");
                if (!g.story.complaints.Contains(cm) || cm.stage != ComplaintStage.Filed) CourtScene.Fail($"another map's hourly pass dropped or raised a complaint of an inmate on the home map (in the list {g.story.complaints.Contains(cm)}, stage {cm.stage})");
                // The home map's own pass still does both.
                ComplaintSystem.Hourly(g, home);
                if (cm.stage != ComplaintStage.Waiting) CourtScene.Fail("the home map's own pass did not raise the complaint");
            });
        }

        // ---- L10 -------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>An acquittal takes this case's murder off the file, not the latest one (a second case, charged later, keeps its own).</summary>
        private static void CheckRegAcquittalRemovesThisCasesRecord(Map map)
        {
            Guarded("court regression L10: an acquittal removes this case's record", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller81"), victim = s.Inmate("Zvictim81");
                CompInmate kc = killer.Inmate();
                MurderTrial t = s.Case(killer, victim);
                int later = s.Now + 100;
                kc.AddRecord(new MisconductRecord { def = RPDeathDefOf.RP_Murder, tick = t.chargedTick, seen = true });
                kc.AddRecord(new MisconductRecord { def = RPDeathDefOf.RP_Murder, tick = later, seen = true });
                Trials.Verdict(g, map, t); // nothing on the case: acquitted
                List<MisconductRecord> left = kc.records.Where(x => x.def == RPDeathDefOf.RP_Murder).ToList();
                if (left.Count != 1 || left[0].tick != later) CourtScene.Fail($"an acquittal removed the wrong murder from the file (left: {string.Join(", ", left.Select(x => x.tick - t.chargedTick))} ticks after the charge; the later one should be left)");
            });
        }
    }
}
