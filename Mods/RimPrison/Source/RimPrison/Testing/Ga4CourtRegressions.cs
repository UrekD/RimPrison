using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the court and complaint items of gap analysis 4 (section 4a: H1, H2, H5, M1-M5, M10, M11 and the court lows). Each fails without its fix; they work on
    /// throwaway inmates and staff (see <see cref="CourtScene"/>), run no ticks (the passes are called by hand) and put back what they touch.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckGa4Court(Map map)
        {
            Rand.PushState(20261024);
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            int checksBegan = Find.TickManager.TicksGame; // (a real shuttle that lands while the checks tick brings inmates of its own: not theirs to put away)
            int fugitives0 = PrisonGame.Instance.fugitives.Count;
            try
            {
                CheckGa4NoHarmedMe(map);
                CheckGa4AbuseFollowsThePolicy(map);
                CheckGa4BleedOutAfterAnAssaultIsMurder(map);
                CheckGa4NoReleaseOnRemand(map);
                CheckGa4InvestigationWithoutFindingIsNoLawsuit(map);
                CheckGa4NoSendingTheOnlyGuardHome(map);
                CheckGa4CourtLockUpEndsOnlyTheCourtsOwn(map);
                CheckGa4BeatingNeedsBothOutOfTheirCells(map);
                CheckGa4AccusedIsNotProtectedAsAWitness(map);
                CheckGa4RiotPoliceAreNoWitnesses(map);
                CheckGa4SecondKillingJoinsTheCase(map);
                CheckGa4EarlierMurderCountsOnTheRecord(map);
                CheckGa4WeeklyReportNamesOnlyWhatWasSeen(map);
                CheckGa4StateChiefDoesNotInjure(map);
                CheckGa4HostageCrisisHoldsTheCourtAndTheGuards(map);
            }
            finally
            {
                Rand.PopState();
            }
            List<Pawn> leftBehind = LeftOnTheMap(map, pawnsBefore, checksBegan);
            if (leftBehind.Count > 0) problems0.Add($"gap analysis 4 court checks left {string.Join(", ", leftBehind.Select(x => x.LabelShort))} on the map");
            if (PrisonGame.Instance.fugitives.Count != fugitives0) problems0.Add($"gap analysis 4 court checks left {PrisonGame.Instance.fugitives.Count - fugitives0} fugitive(s) behind");
        }

        private static void Ga4Fail(string text) => problems0.Add("gap analysis 4: " + text);

        /// <summary>The default regime is free time all day (so nobody is locked up by the hour a check happens to run at), put back by the rollback.</summary>
        private static void FreeTimeAllDay(PrisonGame g, Rollback r)
        {
            RegimeSchedule schedule = g.RegimeFor(InmateUtility.DefaultSecurity);
            List<RegimeActivityDef> was = schedule.slots.ToList();
            r.Add(() =>
            {
                for (int h = 0; h < 24; h++) schedule.Set(h, was[h]);
            });
            for (int h = 0; h < 24; h++) schedule.Set(h, RPDefOf.RP_FreeTime);
        }

        private static bool HasHarmedMe(Pawn p) => p.needs?.mood?.thoughts.memories.Memories.Any(m => m.def == ThoughtDefOf.HarmedMe) == true;

        // ---- H1: vanilla's "Harmed me" tells which complaints are true and who got at a witness ----------------------------------------------------

        /// <summary>
        /// A beating by a guard (complaints) and one by the killer's gang (witness pressure) leave no "Harmed me" memory naming the attacker: it would show, in the mood and social tabs, which
        /// complaints are true (a liar's old injury has no attacker) and who got at a witness, days before the verdict.
        /// </summary>
        private static void CheckGa4NoHarmedMe(Map map)
        {
            Guarded("gap analysis 4 H1: no 'harmed me' after an abuse or a pressure", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn guard = s.Guard("Zguard801"), beaten = s.Inmate("Zbeaten801");
                int wounds = beaten.health.hediffSet.hediffs.Count;
                ComplaintSystem.Abuse(g, guard, beaten, ComplaintKind.Beating);
                if (beaten.health.hediffSet.hediffs.Count <= wounds) Ga4Fail("H1: the check's beating by a guard did not hurt anyone, so there is nothing to look at");
                else if (HasHarmedMe(beaten)) Ga4Fail("H1: an inmate beaten by a guard has a \"harmed me\" memory naming the guard (a true complaint would show in the mood tab, a lie's old injury never does)");
                Pawn killer = s.Inmate("Zkiller801", s.ravens), victim = s.Inmate("Zvictim801"), witness = s.Inmate("Zwitness801");
                QuietTheRealInmates(map, r, new[] { killer, victim, witness, beaten });
                MurderTrial t = s.Case(killer, victim);
                TrialWitness w = s.Witness(t, witness);
                int witnessWounds = witness.health.hediffSet.hediffs.Count;
                if (!Trials.TryPressure(g, map, t, w, witness, beating: true, allowReport: false)) Ga4Fail("H1: the killer could not get at a witness");
                else if (witness.health.hediffSet.hediffs.Count <= witnessWounds) Ga4Fail("H1: the beating of a witness did not hurt them, so there is nothing to look at");
                else if (HasHarmedMe(witness)) Ga4Fail("H1: a witness beaten by the killer's gang has a \"harmed me\" memory naming the attacker (it shows who got at them before the verdict)");
            });
        }

        // ---- H2: an abuse lock-up can be told from a real sanction ------------------------------------------------------------------------------------

        /// <summary>
        /// The made-up lock-up follows the warden's policy for the offence it is written up as: verbal abuse where the policy gives it a sanction (and then exactly that one, not always 8 hours of
        /// cell), else the mildest ordinary offence whose policy is a lock-up; tension moves as it does for that offence (the old code raised it by 8 where an insult relieves it).
        /// </summary>
        private static void CheckGa4AbuseFollowsThePolicy(Map map)
        {
            Guarded("gap analysis 4 H2: an abuse lock-up follows the warden's policy", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                PunishmentPolicy policy0 = g.punishments;
                r.Add(() => g.punishments = policy0);
                g.punishments = new PunishmentPolicy();
                Pawn guard = s.Guard("Zguard802"), inmate = s.Inmate("Zlocked802");
                CompInmate c = inmate.Inmate();
                void Clear()
                {
                    c.records.Clear();
                    (c.forcedActivity, c.forcedUntilTick, c.pendingPunishmentHours, c.visitBanUntilTick, c.yardBanUntilTick, c.shopBanUntilTick, c.tension) = (null, -1, 0, -1, -1, -1, 30f);
                }
                // The default policy gives an insult nothing: the lock-up is written up as the mildest ordinary offence that has one.
                Clear();
                MisconductDef offence = ComplaintSystem.AbuseOffence(g);
                if (offence == RPComplaintDefOf.RP_Insult || offence.severity > 2 || offence.assault || g.punishments.HoursFor(offence) <= 0 || g.punishments.KindFor(offence) != Sanction.Solitary)
                    Ga4Fail($"H2: with the default policy (nothing for verbal abuse) the lock-up is written up as {offence?.defName}, not a minor offence the policy locks up for");
                ComplaintSystem.Abuse(g, guard, inmate, ComplaintKind.Punishment);
                MisconductRecord rec = c.records.LastOrDefault();
                if (rec == null || rec.def != offence || !rec.seen || c.pendingPunishmentHours != g.punishments.SolitaryHoursFor(offence) || c.records.Any(x => x.def == RPComplaintDefOf.RP_Insult))
                    Ga4Fail($"H2: the abuse lock-up left record {rec?.def.defName} (seen {rec?.seen}), {c.pendingPunishmentHours} hours due (the policy for {offence?.defName}: {g.punishments.SolitaryHoursFor(offence)}), and an insult on the file {c.records.Any(x => x.def == RPComplaintDefOf.RP_Insult)}");
                if (c.tension > 30f) Ga4Fail($"H2: the abuse lock-up raised the inmate's tension (30 -> {c.tension}), where the offence relieves it");
                // The warden gives verbal abuse a sanction of its own (a visiting ban, not a lock-up): that is what the abuse earns.
                Clear();
                g.punishments.SetKind(RPComplaintDefOf.RP_Insult, Sanction.LoseVisits);
                g.punishments.Set(RPComplaintDefOf.RP_Insult, 4);
                ComplaintSystem.Abuse(g, guard, inmate, ComplaintKind.Punishment);
                int hoursBan = (c.visitBanUntilTick - s.Now) / GenDate.TicksPerHour;
                if (c.records.LastOrDefault()?.def != RPComplaintDefOf.RP_Insult || hoursBan < 3 || hoursBan > 4 || c.pendingPunishmentHours > 0 || c.forcedActivity != null)
                    Ga4Fail($"H2: with a 4-hour visiting ban for verbal abuse, the abuse earned record {c.records.LastOrDefault()?.def.defName}, a ban of {hoursBan} h, {c.pendingPunishmentHours} hours in the hole, confined {c.forcedActivity?.defName}: it must earn exactly the ban");
                // Nothing in the policy locks anyone up for a minor offence, and verbal abuse earns nothing: the abuse is a record with no sanction behind it.
                Clear();
                g.punishments = new PunishmentPolicy();
                foreach (MisconductDef d in DefDatabase<MisconductDef>.AllDefsListForReading) g.punishments.Set(d, 0);
                if (ComplaintSystem.AbuseOffence(g) != RPComplaintDefOf.RP_Insult) Ga4Fail("H2: with no sanction in the policy for any minor offence the abuse is not written up as verbal abuse");
                ComplaintSystem.Abuse(g, guard, inmate, ComplaintKind.Punishment);
                if (c.pendingPunishmentHours != 0 || c.forcedActivity != null) Ga4Fail("H2: an abuse lock-up was imposed though the policy gives nothing for any minor offence");
            });
        }

        // ---- H5: a stabbed inmate who bleeds out is a murder ---------------------------------------------------------------------------------------------

        /// <summary>
        /// An inmate hurt by another who bleeds out (or dies of the infection) within a day is that inmate's murder (a case, the charge on their file), read where the blow was struck; later, or
        /// with the assailant gone, it is the inquiry into neglect it always was. The damage patch is what remembers the assailant (and saves it).
        /// </summary>
        private static void CheckGa4BleedOutAfterAnAssaultIsMurder(Map map)
        {
            Guarded("gap analysis 4 H5: a bleed-out after an assault is murder", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller803", s.ravens), victim = s.Inmate("Zvictim803"), bystander = s.Inmate("Zbystander803");
                CompInmate vc = victim.Inmate();
                // A blow from the killer is what the patch remembers (a bystander's is the last one if it comes after).
                // (A blow that lands: armour that stops a cut of three, or a part that cannot be cut, leaves no damage dealt and nothing for the patch to remember. The blow goes through apparel, to an arm.)
                BodyPartRecord arm = victim.RaceProps.body.AllParts.FirstOrDefault(part => part.def == BodyPartDefOf.Arm && !victim.health.hediffSet.PartIsMissing(part));
                DamageWorker.DamageResult hit = victim.TakeDamage(new DamageInfo(DamageDefOf.Cut, 3f, 5f, -1f, killer, arm));
                if (vc.assailantId != killer.thingIDNumber || vc.assaultTick != s.Now || vc.assaultCell != victim.PositionHeld)
                    Ga4Fail($"H5: the blow of another inmate was not remembered (assailant {vc.assailantId} of {killer.thingIDNumber}, tick {vc.assaultTick} of {s.Now}, cell {vc.assaultCell} of {victim.PositionHeld}; damage dealt {hit?.totalDamageDealt}, arm {arm?.def.defName}, dead {victim.Dead}, hediffs [{string.Join(", ", victim.health.hediffSet.hediffs.Select(x => x.def.defName))}], wearing [{string.Join(", ", victim.apparel?.WornApparel.Select(x => x.def.defName) ?? new string[0])}])");
                // A guard's blow is no inmate's: the record stays the inmate's.
                Pawn guard = s.Guard("Zguard803");
                victim.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 1f, 0f, -1f, guard));
                if (vc.assailantId != killer.thingIDNumber) Ga4Fail("H5: a guard's blow replaced the inmate who hurt them as the assailant");
                // They bleed out where they lay down, far from where they were hit.
                vc.assaultCell = bystander.Position;
                s.Place(victim, CellFinder.StandableCellNear(victim.Position, map, 6f));
                var bloodLoss = HediffMaker.MakeHediff(HediffDefOf.BloodLoss, victim);
                int inquiries0 = g.stats.inquiries, murders0 = g.stats.murders;
                DeathAftermath.Notify_Died(victim, null, bloodLoss, null);
                MurderTrial t = Trials.TrialOf(g, killer);
                if (t == null || g.stats.murders != murders0 + 1 || g.stats.inquiries != inquiries0 || s.Asked("RP_Decision_Inquiry") != null)
                    Ga4Fail($"H5: an inmate who bled out within a day of another's blow brought no murder charge (case {t != null}, murders +{g.stats.murders - murders0}) or a neglect inquiry (inquiries +{g.stats.inquiries - inquiries0})");
                if (!killer.Inmate().records.Any(x => x.def == RPDeathDefOf.RP_Murder)) Ga4Fail("H5: the charge is not on the assailant's file");
                // The same blow a day and a bit earlier: neglect, as ever (and an infection within the day is a murder too).
                Pawn killer2 = s.Inmate("Zkiller804"), victim2 = s.Inmate("Zvictim804");
                CompInmate v2 = victim2.Inmate();
                (v2.assailantId, v2.assaultTick, v2.assaultCell) = (killer2.thingIDNumber, s.Now - DeathAftermath.AssaultMurderTicks - 10, victim2.Position);
                DeathAftermath.Notify_Died(victim2, null, HediffMaker.MakeHediff(HediffDefOf.BloodLoss, victim2), null);
                if (Trials.TrialOf(g, killer2) != null || g.stats.inquiries != inquiries0 + 1) Ga4Fail("H5: a death from a wound two days old was a murder, or brought no inquiry into neglect");
                Pawn killer3 = s.Inmate("Zkiller805"), victim3 = s.Inmate("Zvictim805");
                CompInmate v3 = victim3.Inmate();
                (v3.assailantId, v3.assaultTick, v3.assaultCell) = (killer3.thingIDNumber, s.Now - 100, victim3.Position);
                DeathAftermath.Notify_Died(victim3, null, HediffMaker.MakeHediff(HediffDefOf.WoundInfection, victim3), null);
                if (Trials.TrialOf(g, killer3) == null) Ga4Fail("H5: an inmate who died of the infection of another's blow within a day brought no murder charge");
                // Starvation is nobody's blow.
                Pawn killer4 = s.Inmate("Zkiller806"), victim4 = s.Inmate("Zvictim806");
                (victim4.Inmate().assailantId, victim4.Inmate().assaultTick) = (killer4.thingIDNumber, s.Now - 100);
                DeathAftermath.Notify_Died(victim4, null, HediffMaker.MakeHediff(HediffDefOf.Malnutrition, victim4), null);
                if (Trials.TrialOf(g, killer4) != null) Ga4Fail("H5: an inmate who starved was somebody's victim");
            });
        }

        // ---- M1: remand is dodged through vanilla's Release order ----------------------------------------------------------------------------------------

        private static void CheckGa4NoReleaseOnRemand(Map map)
        {
            Guarded("gap analysis 4 M1: vanilla's release order is refused on remand", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn accused = s.Inmate("Zaccused807"), victim = s.Inmate("Zvictim807"), free = s.Inmate("Zfree807");
                s.Case(accused, victim);
                accused.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.Release);
                if (accused.guest.ExclusiveInteractionMode == PrisonerInteractionModeDefOf.Release) Ga4Fail("M1: the Release order was given to an accused on remand");
                // Given before the charge, it is called off when a guard comes for it.
                var worker = new WorkGiver_Warden_ReleasePrisoner();
                Pawn guard = s.Guard("Zguard807");
                PrisonerInteractionModeDef before = free.guest.ExclusiveInteractionMode;
                free.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.Release);
                if (free.guest.ExclusiveInteractionMode != PrisonerInteractionModeDefOf.Release) Ga4Fail("M1: the Release order was refused for an inmate with no case");
                s.Case(free, accused);
                Job job = worker.JobOnThing(guard, free, forced: true);
                if (job != null || free.guest.ExclusiveInteractionMode == PrisonerInteractionModeDefOf.Release)
                    Ga4Fail($"M1: a guard coming for the release of an accused on remand was given a job ({job?.def.defName}) or the order stayed ({free.guest.ExclusiveInteractionMode?.defName})");
                free.guest.SetExclusiveInteraction(before);
            });
        }

        // ---- M2: investigating can still end in a lawsuit --------------------------------------------------------------------------------------------------

        private static void CheckGa4InvestigationWithoutFindingIsNoLawsuit(Map map)
        {
            Guarded("gap analysis 4 M2: an investigation that finds nothing never becomes a lawsuit", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer808"), inmate = s.Inmate("Zcomplainant808"), inmate2 = s.Inmate("Zcomplainant808b");
                officer.GetComp<CompStaff>().trueComplaints = 2;
                Complaint investigated = ComplaintSystem.File(g, map, inmate, officer, ComplaintKind.Beating, isTrue: true);
                Complaint dismissed = ComplaintSystem.File(g, map, inmate2, officer, ComplaintKind.Beating, isTrue: true);
                if (investigated == null || dismissed == null)
                {
                    Ga4Fail("M2: the complaints could not be filed");
                    return;
                }
                (investigated.camera, investigated.stage) = (false, ComplaintStage.Investigating);
                investigated.staffSaw.Clear();
                investigated.inmatesSaw.Clear();
                ComplaintSystem.Conclude(g, map, investigated);
                if (investigated.outcome != ComplaintOutcome.NoFinding || investigated.lawsuitTick >= 0)
                    Ga4Fail($"M2: an investigation that found nothing ended {investigated.outcome} with a lawsuit due at {investigated.lawsuitTick} (the warden did the responsible thing, and a suit says they had dismissed it)");
                dismissed.stage = ComplaintStage.Waiting;
                ComplaintSystem.Decide(g, map, dismissed, 1);
                if (dismissed.lawsuitTick < 0) Ga4Fail("M2: a true complaint against an officer with a pattern that the warden dismissed no longer goes to court (the control)");
            });
        }

        // ---- M3: no staffing floor before sending an officer home ------------------------------------------------------------------------------------------

        private static void CheckGa4NoSendingTheOnlyGuardHome(Map map)
        {
            Guarded("gap analysis 4 M3: the only guard on their hours is not sent home", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn officer = s.Guard("Zofficer809"), inmate = s.Inmate("Zcomplainant809");
                // Every real guard sleeps all day, so that the check's officer is the only one at work.
                var real = StaffCommute.AllStaff(map).Where(x => x != officer && GuardUtility.IsGuard(x) && x.timetable != null).ToList();
                var timetables = real.Select(x => Enumerable.Range(0, 24).Select(h => x.timetable.GetAssignment(h)).ToList()).ToList();
                r.Add(() =>
                {
                    for (int i = 0; i < real.Count; i++)
                        for (int h = 0; h < 24; h++) real[i].timetable.SetAssignment(h, timetables[i][h]);
                });
                foreach (Pawn x in real)
                    for (int h = 0; h < 24; h++) x.timetable.SetAssignment(h, TimeAssignmentDefOf.Sleep);
                if (CorruptionSystem.CanSpare(officer, map)) Ga4Fail("M3: the only guard at work on any hour can be spared");
                Complaint cm = ComplaintSystem.File(g, map, inmate, officer, ComplaintKind.Harassment, isTrue: false);
                cm.stage = ComplaintStage.Waiting;
                string why = ComplaintSystem.CannotInvestigate(g, map, cm);
                if (why == null) Ga4Fail("M3: the only guard on their hours can be stood down for an investigation");
                // The third upheld complaint is a suspension, which the only guard does not serve at home: the pay is docked instead.
                CompStaff sc = officer.GetComp<CompStaff>();
                sc.complaintsUpheld = 2;
                ComplaintSystem.Uphold(g, map, cm, "RP_Chr_ComplaintUpheld");
                int fined = r.NewLedgerEntries.Where(e => e.category == RPDefOf.RP_Wages && e.amount == StaffCareers.Wage(sc)).Count();
                if (CorruptionSystem.Disciplined(sc) || sc.complaintsUpheld != 3 || fined == 0)
                    Ga4Fail($"M3: the only guard was suspended (disciplined {CorruptionSystem.Disciplined(sc)}) or their pay was not docked instead (upheld {sc.complaintsUpheld}, ledger lines of a day's pay {fined})");
                // Another guard on their hours: the officer can be spared.
                Pawn cover = s.Guard("Zcover809");
                if (!CorruptionSystem.CanSpare(officer, map)) Ga4Fail("M3: with another guard working every hour the officer cannot be spared");
                Complaint cm2 = ComplaintSystem.File(g, map, s.Inmate("Zcomplainant809b"), officer, ComplaintKind.Harassment, isTrue: false);
                cm2.stage = ComplaintStage.Waiting;
                if (ComplaintSystem.CannotInvestigate(g, map, cm2) != null) Ga4Fail("M3: with cover on their hours the officer could not be stood down for an investigation");
                sc.complaintsUpheld = 2;
                ComplaintSystem.Uphold(g, map, cm2, "RP_Chr_ComplaintUpheld");
                if (!CorruptionSystem.Disciplined(sc)) Ga4Fail("M3: with cover the third upheld complaint did not send the officer home (the control)");
                sc.disciplineUntilTick = -1;
            });
        }

        // ---- M4: the end of the court day ends a real punishment -------------------------------------------------------------------------------------------

        private static void CheckGa4CourtLockUpEndsOnlyTheCourtsOwn(Map map)
        {
            Guarded("gap analysis 4 M4: the end of the court day leaves a real lock-up alone", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                if (HearingRooms.Available(map, s.pm))
                {
                    Ga4Fail("M4: a hearing room is on the map before the check built one");
                    return;
                }
                Pawn killer = s.Inmate("Zkiller810"), victim = s.Inmate("Zvictim810");
                CompInmate kc = killer.Inmate();
                MurderTrial t = s.Case(killer, victim);
                t.trialTick = s.Now - 10;
                s.Hour(11);
                if (!t.courtLock || kc.forcedActivity != RPDefOf.RP_Lockup)
                {
                    Ga4Fail($"M4: off-site, the accused was not taken to court (court lock {t.courtLock}, confined {kc.forcedActivity?.defName})");
                    return;
                }
                // A sanction is imposed while they are away: it extends the confinement the way a second lock-up does (Sanctions.Impose).
                int real = s.Now + 30 * GenDate.TicksPerHour;
                kc.forcedUntilTick = Math.Max(kc.forcedUntilTick, real);
                s.Hour(16);
                if (kc.forcedActivity != RPDefOf.RP_Lockup || kc.forcedUntilTick != real)
                    Ga4Fail($"M4: the verdict ended a real 30-hour lock-up imposed during the court day (confined {kc.forcedActivity?.defName}, until {(kc.forcedUntilTick - s.Now) / GenDate.TicksPerHour} h)");
                // Nothing else imposed: the verdict ends the court's own.
                Pawn killer2 = s.Inmate("Zkiller811"), victim2 = s.Inmate("Zvictim811");
                MurderTrial t2 = s.Case(killer2, victim2);
                t2.trialTick = s.Now - 10;
                s.Hour(11);
                s.Hour(16);
                if (killer2.Inmate().forcedActivity != null) Ga4Fail("M4: the court's own lock-up was not ended by the verdict (the control)");
            });
        }

        // ---- M5: witnesses are got at regardless of where anyone is ---------------------------------------------------------------------------------------

        private static void CheckGa4BeatingNeedsBothOutOfTheirCells(Map map)
        {
            Guarded("gap analysis 4 M5: a beating needs both out of their cells", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller812", s.ravens), victim = s.Inmate("Zvictim812"), witness = s.Inmate("Zwitness812");
                QuietTheRealInmates(map, r, new[] { killer, victim, witness });
                MurderTrial t = s.Case(killer, victim);
                TrialWitness w = s.Witness(t, witness);
                RegimeSchedule schedule = g.RegimeFor(InmateUtility.DefaultSecurity);
                // At three in the morning, in lockup: a threat gets through the door, a beating does not.
                for (int h = 0; h < 24; h++) schedule.Set(h, RPDefOf.RP_Lockup);
                int wounds = witness.health.hediffSet.hediffs.Count;
                Trials.TryPressure(g, map, t, w, witness, beating: true, allowReport: false);
                if (witness.health.hediffSet.hediffs.Count != wounds || w.pressure != Trials.ThreatPressure)
                    Ga4Fail($"M5: with both locked in their cells the witness was beaten (wounds {wounds} -> {witness.health.hediffSet.hediffs.Count}, pressure {w.pressure}): a threat is all that can be passed on");
                for (int h = 0; h < 24; h++) schedule.Set(h, RPDefOf.RP_FreeTime);
                w.pressure = 0f;
                Trials.TryPressure(g, map, t, w, witness, beating: true, allowReport: false);
                if (witness.health.hediffSet.hediffs.Count <= wounds || w.pressure != Trials.BeatingPressure) Ga4Fail("M5: out of their cells the witness was not beaten (the control)");
            });
        }

        // ---- M10: an accused killer can be moved into protective custody as a witness ---------------------------------------------------------------------

        private static void CheckGa4AccusedIsNotProtectedAsAWitness(Map map)
        {
            Guarded("gap analysis 4 M10: an accused is not protected as a witness", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn accused = s.Inmate("Zaccused813"), victim = s.Inmate("Zvictim813"), other = s.Inmate("Zother813"), free = s.Inmate("Zwitness813");
                s.Case(accused, victim);
                if (Trials.ProtectableAsWitness(g, accused, accused.Inmate())) Ga4Fail("M10: an accused killer on remand may be moved to protective custody as a witness");
                if (!Trials.ProtectableAsWitness(g, free, free.Inmate())) Ga4Fail("M10: an inmate with no case against them may not be moved to protective custody as a witness (the control)");
                // As one of another case's witnesses, through the game's own call.
                MurderTrial t = s.Case(other, victim);
                TrialWitness w = s.Witness(t, accused, known: true);
                SecurityLevelDef was = accused.Inmate().security;
                int moved = Trials.ProtectWitnesses(map, t);
                if (moved != 0 || accused.Inmate().security != was || accused.Inmate().protectedFrom != null) Ga4Fail("M10: the accused was moved to protective custody as a witness through the case's own call");
            });
        }

        // ---- M11: riot police are counted as staff witnesses and then leave ---------------------------------------------------------------------------------

        private static void CheckGa4RiotPoliceAreNoWitnesses(Map map)
        {
            Guarded("gap analysis 4 M11: temporary staff are no witnesses", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkiller814"), victim = s.Inmate("Zvictim814");
                IntVec3 where = victim.Position;
                Pawn permanent = s.Guard("Zguard814", where), police = s.Guard("Zpolice814", where);
                police.GetComp<CompStaff>().leaveAtTick = s.Now + 3 * GenDate.TicksPerHour;
                MurderTrial t = Trials.Open(s.g, map, victim, killer, killer.Inmate(), false, null);
                bool Has(Pawn p) => t.witnesses.Exists(w => w.id == p.thingIDNumber);
                if (!Has(permanent) || Has(police))
                    Ga4Fail($"M11: witnesses were {string.Join(", ", t.witnesses.Select(w => w.name))}: expected {permanent.LabelShort} and not {police.LabelShort}, a squad called in for a few hours who will have gone by the time the case is assembled");
            });
        }

        // ---- court lows --------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A second killing by the same accused is part of the open case, not a second case; and an earlier murder is on the record of a later one.</summary>
        private static void CheckGa4SecondKillingJoinsTheCase(Map map)
        {
            Guarded("gap analysis 4 low: a second killing joins the open case", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller815"), first = s.Inmate("Zvictim815a"), second = s.Inmate("Zvictim815b");
                Pawn witness = s.Guard("Zwitness815", second.Position);
                MurderTrial t = s.Case(killer, first);
                MurderTrial again = Trials.Open(g, map, second, killer, killer.Inmate(), false, null);
                int cases = g.story.trials.Count(x => x.killerId == killer.thingIDNumber);
                if (cases != 1 || again != t || !t.witnesses.Exists(w => w.id == witness.thingIDNumber))
                    Ga4Fail($"low: a second killing by an accused with an open case left {cases} cases (the same case {again == t}) and the officer who saw it {(t.witnesses.Exists(w => w.id == witness.thingIDNumber) ? "on" : "off")} the case");
                // Closing the one case closes the lot: nothing is left open for the accused.
                Trials.Notify_TransferredOut(g, killer);
                if (Trials.TrialOf(g, killer) != null) Ga4Fail("low: after the case was closed another was still open against the accused");
            });
        }

        private static void CheckGa4EarlierMurderCountsOnTheRecord(Map map)
        {
            Guarded("gap analysis 4 low: an earlier murder counts on the record", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn killer = s.Inmate("Zkiller816"), victim = s.Inmate("Zvictim816");
                killer.Inmate().AddRecord(new MisconductRecord { def = RPDeathDefOf.RP_Murder, tick = s.Now - GenDate.TicksPerDay, seen = true });
                MurderTrial t = Trials.Open(s.g, map, victim, killer, killer.Inmate(), false, null);
                if (t.priorRecord != 1) Ga4Fail($"low: a killer with an earlier murder on their file had {t.priorRecord} serious offences on the record of the case (1 expected)");
            });
        }

        private static void CheckGa4WeeklyReportNamesOnlyWhatWasSeen(Map map)
        {
            Guarded("gap analysis 4 low: the weekly report counts only seen records", map, r =>
            {
                var s = new CourtScene(map, r);
                Pawn hidden = s.Inmate("Zhidden817"), shown = s.Inmate("Zshown817");
                QuietTheRealInmates(map, r, new[] { hidden, shown });
                foreach (Pawn real in map.mapPawns.PrisonersOfColony.Where(x => x != hidden && x != shown && x.Inmate() != null))
                {
                    CompInmate rc = real.Inmate();
                    bool active = rc.activeFlag;
                    rc.activeFlag = false;
                    r.Add(() => rc.activeFlag = active);
                }
                for (int i = 0; i < 4; i++) hidden.Inmate().AddRecord(new MisconductRecord { def = RPMisconductDefOf.RP_Fight, tick = s.Now, seen = false });
                for (int i = 0; i < 2; i++) shown.Inmate().AddRecord(new MisconductRecord { def = RPMisconductDefOf.RP_Fight, tick = s.Now, seen = true });
                (Pawn worst, int count) = WeeklyReport.BusiestInmate(map, s.Now - GenDate.TicksPerDay);
                if (worst != shown || count != 2) Ga4Fail($"low: the week's busiest inmate was {worst?.LabelShort} with {count} records (the one with two seen, not the one with four nobody saw)");
            });
        }

        private static void CheckGa4StateChiefDoesNotInjure(Map map)
        {
            Guarded("gap analysis 4 low: the state chief's force injures nobody", map, r =>
            {
                var s = new CourtScene(map, r);
                PrisonGame g = s.g;
                Pawn chief = s.Guard("Zchief818"), inmate = s.Inmate("Zrestrained818");
                chief.story.traits.allTraits.Clear();
                chief.story.traits.GainTrait(new Trait(TraitDefOf.Bloodlust));
                chief.GetComp<CompStaff>().statePosted = true;
                r.Add(() => chief.GetComp<CompStaff>().statePosted = false);
                int wounds = inmate.health.hediffSet.hediffs.Count;
                for (int i = 0; i < 400; i++) ComplaintSystem.Notify_Restrained(chief, inmate, inmate.Inmate(), wasAggro: false);
                if (inmate.health.hediffSet.hediffs.Count != wounds) Ga4Fail("low: the state's own chief injured an inmate in a restraint, where a complaint against them is refused");
            });
        }

        /// <summary>A hostage crisis holds the court's day (nobody is taken to court) and the guards' abuse: the guards have a crisis on their hands.</summary>
        private static void CheckGa4HostageCrisisHoldsTheCourtAndTheGuards(Map map)
        {
            HostageCheck("court and abuse wait for a crisis", map, h =>
            {
                StoryArc arc = h.Begin(out Pawn hostage, out Pawn leader);
                if (arc == null) return;
                // (The hostage-takers are out of their riot state, which the guards' own gate (a riot, a lockdown) would answer to: the crisis is what is left to hold them back.)
                foreach (Pawn taker in arc.pawns.Skip(1)) taker.MentalState?.RecoverFromState();
                if (RiotPolice.RiotOngoing(map)) Ga4Fail("low: a riot is on in the check's set-up, which the guards' own gate answers to: the hostage gate cannot be told apart");
                var s = new CourtScene(map, h.r);
                PrisonGame g = s.g;
                Pawn killer = s.Inmate("Zkiller819"), victim = s.Inmate("Zvictim819");
                MurderTrial t = s.Case(killer, victim);
                // (Out of the way: the only inmate near the guard below is the one that could be abused.)
                IntVec3 away = s.FarFrom(killer.Position);
                if (away.IsValid)
                {
                    s.Place(killer, away);
                    s.Place(victim, away);
                }
                t.trialTick = s.Now - 10;
                s.Hour(11);
                if (t.courtTick >= 0 || killer.Inmate().forcedActivity != null) Ga4Fail("low: the accused was taken to court in the middle of a hostage crisis");
                // The guards' abuse: a guard with a brutal streak next to an inmate, many hours, nobody is abused.
                Pawn brute = s.Guard("Zbrute819");
                brute.story.traits.allTraits.Clear();
                brute.story.traits.GainTrait(new Trait(TraitDefOf.Bloodlust));
                Pawn target = s.Inmate("Zabused819");
                s.Place(target, CellFinder.StandableCellNear(brute.Position, map, 3f));
                QuietTheRealInmates(map, h.r, new[] { killer, victim, target });
                for (int i = 0; i < 12000; i++) ComplaintSystem.HourlyGuards(g, map);
                if (StaffMemories.ScoreFor(target.Inmate(), brute) <= -12f) Ga4Fail("low: a guard abused an inmate in the middle of a hostage crisis");
                HostageCrisis.Release(hostage);
                arc.done = true;
                t.trialTick = s.Now - 10;
                s.Hour(11);
                if (t.courtTick < 0) Ga4Fail("low: the court's day was not kept once the crisis was over (the control)");
                for (int i = 0; i < 12000 && StaffMemories.ScoreFor(target.Inmate(), brute) > -12f; i++) ComplaintSystem.HourlyGuards(g, map);
                if (StaffMemories.ScoreFor(target.Inmate(), brute) > -12f) Ga4Fail($"low: the guard with a brutal streak never abused anyone in 12000 hours with no crisis running (the control: the check does not look at anything; guard awake {brute.Awake()}, downed {brute.Downed}, mental {brute.InMentalState}, posted {brute.GetComp<CompStaff>()?.statePosted}, target {ComplaintSystem.AbuseTarget(brute, map, g)?.LabelShort ?? "none"}, target in sight {target.Position.InHorDistOf(brute.Position, 6f)}, target lock-up {target.Inmate().forcedActivity != null})");
            });
        }
    }
}
