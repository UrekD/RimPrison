using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Checks from stability round 31 (see WORKLOG): news from home puts an inmate on the watch list's triggers, and the time staff spend at home heals them (<see cref="HomeHealing"/>). The warden asks' checks of this round are
    /// in WardenAskChecks.cs. Each runs in its own <see cref="Guarded"/> scope from <c>FireAllEvents</c>; the clock is moved on for the time away (put back by the rollback) and no tick runs.
    /// </summary>
    public static partial class AutoTest
    {
        private static void S31Fail(string check, string what) => problems0.Add($"stability 31 ({check}): {what}");

        private const int S31Hour = GenDate.TicksPerHour;

        // ---- news from home ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// News from home that puts them at risk is a trigger for the watch list as the letter and the kin's death are (<see cref="IncidentWorker_NewsFromHome.Deliver"/>): a death in the family is <see cref="RiskTrigger.FamilyDeath"/>
        /// and a partner who leaves <see cref="RiskTrigger.DearJohn"/>, both at the tick of the news; a birth, money and a job are none. The trigger they carry before is cleared first (the mail or the kin's hooks may have given one).
        /// Fails without the hooks: the delivered news left no trigger.
        /// </summary>
        private static void CheckNewsFromHomeIsATrigger(Map map)
        {
            const string check = "news from home";
            Guarded("stability 31: news from home triggers the watch list", map, r =>
            {
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                c.relatives.Clear();
                c.relatives.Add(Relatives.Make("RP_Kin_Partner"));
                c.relatives.Add(Relatives.Make("RP_Kin_Mother"));
                Relatives.Recount(c);
                int clock0 = Find.TickManager.TicksGame;
                if (clock0 < 10) Find.TickManager.DebugSetTicksGame(10);
                r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
                int now = Find.TickManager.TicksGame;
                void Clear()
                {
                    (c.riskEvent, c.riskEventTick, c.paroleDenialTick, c.admittedTick) = (RiskTrigger.None, -1, -1, -1);
                }
                string Seen() => $"{c.riskEvent} at {c.riskEventTick} (trigger {WatchList.Trigger(p, c, now, out int at)} at {at})";

                // The news that is no trigger.
                foreach (IncidentWorker_NewsFromHome.News benign in new[] { IncidentWorker_NewsFromHome.News.Birth, IncidentWorker_NewsFromHome.News.Money, IncidentWorker_NewsFromHome.News.Job })
                {
                    Clear();
                    c.jobWaiting = false;
                    IncidentWorker_NewsFromHome.Deliver(p, c, benign);
                    if (c.riskEvent != RiskTrigger.None) S31Fail(check, $"{benign} news left a trigger: {Seen()}");
                }
                // A partner who leaves.
                Clear();
                IncidentWorker_NewsFromHome.Deliver(p, c, IncidentWorker_NewsFromHome.News.Leaving);
                if (c.riskEvent != RiskTrigger.DearJohn || c.riskEventTick != now || WatchList.Trigger(p, c, now, out _) != RiskTrigger.DearJohn)
                    S31Fail(check, $"news that the partner leaves did not make the Dear John trigger, now: {Seen()}");
                // A death in the family.
                Clear();
                IncidentWorker_NewsFromHome.Deliver(p, c, IncidentWorker_NewsFromHome.News.Death);
                if (c.riskEvent != RiskTrigger.FamilyDeath || c.riskEventTick != now || WatchList.Trigger(p, c, now, out _) != RiskTrigger.FamilyDeath)
                    S31Fail(check, $"news of a death in the family did not make the family death trigger, now: {Seen()}");
            });
        }

        // ---- staff heal at home -----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A made-up guard with a clean bill of health and a full stomach, on site, the clock far enough on to go away for days (put back by the rollback; call it first in a check).</summary>
        private static Pawn S31Guard(Map map, Rollback r)
        {
            int clock0 = Find.TickManager.TicksGame;
            r.Add(() => Find.TickManager.DebugSetTicksGame(clock0));
            if (clock0 < 10 * GenDate.TicksPerDay) Find.TickManager.DebugSetTicksGame(10 * GenDate.TicksPerDay);
            Pawn p = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
            S31Clean(p);
            return p;
        }

        private static void S31Clean(Pawn p)
        {
            foreach (Hediff h in p.health.hediffSet.hediffs.ToList()) p.health.RemoveHediff(h);
            if (p.needs?.food != null) p.needs.food.CurLevel = p.needs.food.MaxLevel;
        }

        private static Hediff_Injury S31Wound(Pawn p, HediffDef def, float severity, int partIndex = 0, bool permanent = false)
        {
            List<BodyPartRecord> parts = p.RaceProps.body.AllParts.Where(x => x.def == BodyPartDefOf.Arm || x.def == BodyPartDefOf.Leg).ToList();
            var wound = (Hediff_Injury)HediffMaker.MakeHediff(def, p, parts[partIndex % parts.Count]);
            wound.Severity = severity;
            p.health.AddHediff(wound);
            if (permanent && wound.TryGetComp<HediffComp_GetsPermanent>() is { } gp) gp.IsPermanent = true;
            return wound;
        }

        /// <summary>What can still heal on them: the severity of the injuries that are not permanent.</summary>
        private static float S31Healable(Pawn p) => p.health.hediffSet.hediffs.OfType<Hediff_Injury>().Where(h => !h.IsPermanent()).Sum(h => h.Severity);

        /// <summary>
        /// A commute through the real path: out of the gate (<see cref="StaffCommute.Leave"/>, which writes the tick), the clock on <paramref name="hours"/> hours, and back in (<see cref="StaffCommute.Arrive"/>). False when it did not happen.
        /// </summary>
        private static bool S31Commute(Map map, Pawn p, int hours, string check)
        {
            PrisonGame g = PrisonGame.Instance;
            CompStaff sc = p.GetComp<CompStaff>();
            StaffCommute.Leave(p);
            if (!g.home.Contains(p) || sc.homeSinceTick != Find.TickManager.TicksGame)
            {
                S31Fail(check, $"premise: {p.LabelShort} did not go home through Leave (at home {g.home.Contains(p)}, away since {sc.homeSinceTick}, now {Find.TickManager.TicksGame})");
                return false;
            }
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + hours * S31Hour);
            StaffCommute.Arrive(p, sc, map, g);
            if (!p.Spawned || g.home.Contains(p) || sc.homeSinceTick != -1)
            {
                S31Fail(check, $"premise: {p.LabelShort} did not come back through Arrive (spawned {p.Spawned}, at home {g.home.Contains(p)}, away since {sc.homeSinceTick}, -1 expected)");
                return false;
            }
            return true;
        }

        // Vanilla's own numbers, written out here and not read from the product's constants (a constant changed to a wrong value would change the expectation with it): a pawn lying with no bed heals 8 a day and 4 more for
        // lying (Pawn_HealthTracker), a tend adds 8 times 0.5 to 1.5 by its quality, blood loss recovers a third of a point a day, and lying doubles nothing else - the resting factor on the immunity gain speed is 1.1 (Stats_Pawns_General).
        private const float VanillaNaturalPerDay = 8f + 4f, VanillaTendedPerDay = 8f, VanillaBloodLossPerDay = 1f / 3f, VanillaRestingImmunity = 1.1f;

        /// <summary>What vanilla heals in a day lying down with no bed, for this pawn (the stat and the size of them): their share of <paramref name="hours"/> hours.</summary>
        private static float S31NaturalHeal(Pawn p, int hours) => VanillaNaturalPerDay * (hours / 24f) * p.HealthScale * p.GetStatValue(StatDefOf.InjuryHealingFactor);

        private static void CheckStaffHealAtHome(Map map)
        {
            CheckHomeHealsAWound(map);
            CheckHomeHealsWoundsOnceAndKeepsScars(map);
            CheckHomeTimeAwayIsBounded(map);
            CheckHomeTendedAndBleeding(map);
            CheckHomeWoundsAge(map);
            CheckHomeFlu(map);
            CheckHomeStarvingTend(map);
            CheckHomePlagueNearLethal(map);
            CheckHomeChronicLeftAlone(map);
            CheckHomeDogHeals(map);
            CheckHomeDogOnSite(map);
            CheckHomeEntryFailure(map);
            CheckHomeUntouched(map);
        }

        /// <summary>
        /// A 10-point cut on a guard sent home for 12 hours comes back at about 6 points less (vanilla's 12 a day lying with no bed, times their healing factor and size), measured at the departure and just after the arrival with no
        /// tick between. Fails without the healing: the cut is as it was, and an application that healed by the whole rate for each wound, or the whole cut, is out of the bounds.
        /// </summary>
        private static void CheckHomeHealsAWound(Map map)
        {
            const string check = "a wound at home";
            Guarded("stability 31: a guard's wound heals at home", map, r =>
            {
                Pawn p = S31Guard(map, r);
                S31Wound(p, HediffDefOf.Cut, 10f);
                float before = S31Healable(p), expected = S31NaturalHeal(p, 12);
                if (!S31Commute(map, p, 12, check)) return;
                float after = S31Healable(p), drop = before - after;
                if (Mathf.Abs(drop - expected) > 0.05f)
                    S31Fail(check, $"a 10-point cut sent home for 12 hours went from {before:F2} to {after:F2} (a drop of {drop:F2}, vanilla lying down with no bed {expected:F2}: the same within 0.05 expected)");
            });
        }

        /// <summary>
        /// Two wounds share the healing (vanilla heals one injury at each interval, not each: the drop is the same as for one wound, not doubled), a permanent scar is left as it was, and the cut beside it still heals. Fails without
        /// the sharing (the drop doubles) and without the scar's exclusion (the scar shrinks).
        /// </summary>
        private static void CheckHomeHealsWoundsOnceAndKeepsScars(Map map)
        {
            const string check = "wounds shared";
            Guarded("stability 31: two wounds share the healing and a scar stays", map, r =>
            {
                Pawn p = S31Guard(map, r);
                Hediff_Injury a = S31Wound(p, HediffDefOf.Cut, 10f, 0), b = S31Wound(p, HediffDefOf.Cut, 10f, 1), scar = S31Wound(p, HediffDefOf.Cut, 3f, 2, permanent: true);
                if (!scar.IsPermanent())
                {
                    RPLog.Message("[RimPrison:test] stability 31: a cut cannot be made permanent here, the scar not checked");
                    p.health.RemoveHediff(scar);
                    scar = null;
                }
                float before = S31Healable(p), expected = S31NaturalHeal(p, 12), scar0 = scar?.Severity ?? 0f;
                if (!S31Commute(map, p, 12, check)) return;
                float after = S31Healable(p), drop = before - after;
                if (Mathf.Abs(drop - expected) > 0.05f)
                    S31Fail(check, $"two 10-point cuts sent home for 12 hours went from {before:F2} to {after:F2} (a drop of {drop:F2}; the one healing shared between them, {expected:F2}, expected, not doubled)");
                if (scar != null && (!p.health.hediffSet.hediffs.Contains(scar) || Mathf.Abs(scar.Severity - scar0) > 0.0001f))
                    S31Fail(check, $"a permanent injury went from {scar0:F2} to {(p.health.hediffSet.hediffs.Contains(scar) ? scar.Severity.ToString("F2") : "gone")} at home (unchanged expected)");
                if (a.Severity >= 10f && b.Severity >= 10f) S31Fail(check, "neither cut healed");
            });
        }

        /// <summary>
        /// The time away is counted from the tick they left and read once: none at all (they came back the tick they went, an old save's -1, a tick that is in the future) changes nothing; two commutes in a row each heal for their own
        /// time (the second, of no time, nothing); the tick is -1 again once they are back. Fails without the saved tick: no time to measure, or the first commute's time given twice.
        /// </summary>
        private static void CheckHomeTimeAwayIsBounded(Map map)
        {
            const string check = "the time away";
            Guarded("stability 31: the time away is read once and never from nothing", map, r =>
            {
                Pawn p = S31Guard(map, r);
                CompStaff sc = p.GetComp<CompStaff>();
                S31Wound(p, HediffDefOf.Cut, 10f);
                float start = S31Healable(p);
                // No time away: the same tick.
                if (!S31Commute(map, p, 0, check)) return;
                if (Mathf.Abs(S31Healable(p) - start) > 0.0001f) S31Fail(check, $"a commute of no time healed the wound: {start:F2} -> {S31Healable(p):F2}");
                // An old save's -1, and a tick in the future (the clock put back): no healing on that arrival.
                PrisonGame g = PrisonGame.Instance;
                foreach ((string what, int ahead) in new[] { ("no tick at all (-1)", int.MinValue), ("a tick in the future", 5 * S31Hour) })
                {
                    StaffCommute.Leave(p);
                    sc.homeSinceTick = ahead == int.MinValue ? -1 : Find.TickManager.TicksGame + ahead;
                    // (An hour on: the future tick is still ahead of the clock when they come in.)
                    Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + S31Hour);
                    StaffCommute.Arrive(p, sc, map, g);
                    if (!p.Spawned || Mathf.Abs(S31Healable(p) - start) > 0.0001f || sc.homeSinceTick != -1)
                        S31Fail(check, $"an arrival with {what} healed the wound or left the tick: {start:F2} -> {S31Healable(p):F2}, tick {sc.homeSinceTick}");
                }
                if (HomeHealing.AwayTicks(-1, 100) != 0 || HomeHealing.AwayTicks(200, 100) != 0 || HomeHealing.AwayTicks(100, 100) != 0 || HomeHealing.AwayTicks(0, 100) != 100 || HomeHealing.AwayTicks(0, 100 * GenDate.TicksPerDay) != HomeHealing.MaxAwayTicks)
                    S31Fail(check, "the time away is not none for -1, a future tick and no time, the whole of a short one and the longest for a very long one");
                // Two commutes in a row: each its own time.
                float expected = S31NaturalHeal(p, 4);
                if (!S31Commute(map, p, 4, check)) return;
                float mid = S31Healable(p);
                if (Mathf.Abs(start - mid - expected) > 0.05f) S31Fail(check, $"the first of two commutes of 4 hours healed {start - mid:F2} ({expected:F2} expected)");
                if (!S31Commute(map, p, 4, check)) return;
                float end = S31Healable(p);
                if (Mathf.Abs(mid - end - expected) > 0.05f) S31Fail(check, $"the second of two commutes of 4 hours healed {mid - end:F2}, not {expected:F2} (the first one's time given twice, or none)");
            });
        }

        /// <summary>
        /// A tended wound heals the more for the whole time at home (an injury's tend is permanent in vanilla: its comp has no duration, so nothing runs down and the tended rate - 8 times 0.5 to 1.5 by the quality, on top of
        /// the natural one - lasts the 12 hours), and a bleeding one is taken as bandaged (the blood loss they left with recovers at a third a day and no more is lost). Fails without the tended share (the drop is the natural
        /// one) and without the blood loss recovery; an injury tend run down as a disease's (half-way through) fails it by the drop and the timer.
        /// </summary>
        private static void CheckHomeTendedAndBleeding(Map map)
        {
            const string check = "tended and bleeding";
            Guarded("stability 31: a tended wound heals more and the blood loss recovers", map, r =>
            {
                Pawn p = S31Guard(map, r);
                // (A wound too big for 12 hours to close - a 10-point one is closed by a quick healer and the drop stops at its severity - and the expectation capped at it all the same.)
                Hediff_Injury cut = S31Wound(p, HediffDefOf.Cut, 20f);
                HediffComp_TendDuration td = cut.TryGetComp<HediffComp_TendDuration>();
                if (td == null || !td.TProps.TendIsPermanent)
                {
                    RPLog.Message("[RimPrison:test] stability 31: a cut has no permanent tend comp here, the tended wound not checked");
                    return;
                }
                // Tended at quality 1, as vanilla's tend leaves it on an injury (a timer of 1 that nothing counts down).
                td.tendQuality = 1f;
                td.tendTicksLeft = 1;
                float scale = p.HealthScale * p.GetStatValue(StatDefOf.InjuryHealingFactor);
                float before = S31Healable(p);
                float expected = Mathf.Min(before, S31NaturalHeal(p, 12) + VanillaTendedPerDay * 1.5f * (12 / 24f) * scale);
                if (!S31Commute(map, p, 12, check)) return;
                float drop = before - S31Healable(p);
                if (Mathf.Abs(drop - expected) > 0.1f) S31Fail(check, $"a cut tended at full quality, 12 hours at home, healed {drop:F2} ({expected:F2} expected: the natural rate and the tended rate for all 12 hours, an injury's tend being permanent)");
                if (td.tendTicksLeft != 1) S31Fail(check, $"the permanent tend of a cut had {td.tendTicksLeft} ticks left after 12 hours at home (1, as vanilla leaves it, expected: nothing runs it down)");

                // Blood loss they left with recovers at vanilla's rate (a third of a point a day), and an open wound bleeds no more.
                Pawn q = S31Guard(map, r);
                S31Wound(q, HediffDefOf.Cut, 8f);
                Hediff loss = HediffMaker.MakeHediff(HediffDefOf.BloodLoss, q);
                loss.Severity = 0.5f;
                q.health.AddHediff(loss);
                if (!S31Commute(map, q, 12, check)) return;
                Hediff now = q.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss);
                float want = 0.5f - VanillaBloodLossPerDay * 0.5f;
                if (now == null || Mathf.Abs(now.Severity - want) > 0.01f)
                    S31Fail(check, $"blood loss of 0.5 after 12 hours at home is {(now == null ? "gone" : now.Severity.ToString("F3"))} ({want:F3} expected: it recovers at a third a day and the wound sent home loses no more)");
            });
        }

        /// <summary>
        /// The wounds and the missing parts are as much older when they come back as they were away: a wound that was bleeding at the departure and would have stopped by the time away's end does not bleed at the gate (vanilla stops
        /// the bleeding of an injury by its age), and a missing part's age moves on. A 20-point cut aged 80000 ticks, 24 hours away: it stops bleeding by about 110000 to 125000 ticks (by its severity, as it heals), which its age
        /// reaches only by the time away (140000), not as it was (80000, still bleeding). Fails without the ageing: the cut bleeds as before, and neither age moved.
        /// </summary>
        private static void CheckHomeWoundsAge(Map map)
        {
            const string check = "wounds age";
            Guarded("stability 31: wounds and missing parts are older after the time at home", map, r =>
            {
                Pawn p = S31Guard(map, r);
                Hediff_Injury cut = S31Wound(p, HediffDefOf.Cut, 20f);
                cut.ageTicks = 80000;
                var missing = HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, p, p.RaceProps.body.AllParts.First(x => x.def == BodyPartDefOf.Hand));
                p.health.AddHediff(missing);
                int missing0 = missing.ageTicks;
                float bleed0 = cut.BleedRate;
                if (bleed0 <= 0f)
                {
                    RPLog.Message("[RimPrison:test] stability 31: an untended cut does not bleed on this pawn, the bleeding by age not checked");
                    return;
                }
                if (!S31Commute(map, p, 24, check)) return;
                if (cut.ageTicks != 80000 + 24 * S31Hour || missing.ageTicks != missing0 + 24 * S31Hour)
                    S31Fail(check, $"after 24 hours at home the cut is {cut.ageTicks} ticks old (from 80000: {80000 + 24 * S31Hour} expected) and the missing part {missing.ageTicks} (from {missing0}: {missing0 + 24 * S31Hour} expected)");
                if (p.health.hediffSet.hediffs.Contains(cut) && cut.BleedRate > 0f)
                    S31Fail(check, $"a cut that stopped bleeding by its age during 24 hours at home bleeds {cut.BleedRate:F3} a day at the gate (it was {bleed0:F3} at the departure; none expected)");
            });
        }

        /// <summary>
        /// A flu advances by the comps' own rates with the immunity (12 hours of it: the severity by the rate at the start, the immunity by its own), and an immunity that reaches full part-way turns the severity's way: the stepping
        /// is not one multiplication of the start rates. Fails without the healing: both are as they were.
        /// </summary>
        private static void CheckHomeFlu(Map map)
        {
            const string check = "a flu at home";
            Guarded("stability 31: a flu advances with its immunity at home", map, r =>
            {
                HediffDef flu = DefDatabase<HediffDef>.GetNamedSilentFail("Flu");
                if (flu == null)
                {
                    RPLog.Message("[RimPrison:test] stability 31: no flu def, the flu not checked");
                    return;
                }
                Pawn p = S31Guard(map, r);
                Hediff h = HediffMaker.MakeHediff(flu, p);
                h.Severity = 0.3f;
                p.health.AddHediff(h);
                p.health.immunity.TryAddImmunityRecord(flu, flu);
                ImmunityRecord rec = p.health.immunity.ImmunityListForReading.Find(x => x.hediffDef == flu);
                HediffComp_Immunizable comp = h.TryGetComp<HediffComp_Immunizable>();
                if (rec == null || comp == null)
                {
                    S31Fail(check, "premise: no immunity record or no immunizable comp on a flu");
                    return;
                }
                rec.immunity = 0.2f;
                // (The expectation from the rates the comps state: the severity's, and the immunity's with vanilla's resting factor - the 12 hours are spent lying down, the guard at the gate is standing.)
                float perDay = comp.SeverityChangePerDay(), perTick = rec.ImmunityChangePerTick(p, true, h) * VanillaRestingImmunity;
                if (perTick <= 0f)
                {
                    RPLog.Message("[RimPrison:test] stability 31: this guard gains no immunity to a flu, the flu not checked");
                    return;
                }
                float sevWant = 0.3f + perDay * 0.5f, immWant = 0.2f + perTick * 12 * S31Hour;
                if (!S31Commute(map, p, 12, check)) return;
                if (Mathf.Abs(h.Severity - sevWant) > 0.002f || Mathf.Abs(rec.immunity - immWant) > 0.001f)
                    S31Fail(check, $"a flu at 0.30 with immunity 0.20, 12 hours at home: severity {h.Severity:F3} ({sevWant:F3} expected), immunity {rec.immunity:F3} ({immWant:F3} expected, gaining at the lying rate: the standing rate is a tenth less)");

                // An immunity that reaches full part-way (after 3.5 hours: in the fourth step): the severity turns (a start rate multiplied over the 12 hours would have it up). Derived from the pawn's own gain, not a number.
                Pawn q = S31Guard(map, r);
                Hediff hq = HediffMaker.MakeHediff(flu, q);
                hq.Severity = 0.3f;
                q.health.AddHediff(hq);
                q.health.immunity.TryAddImmunityRecord(flu, flu);
                ImmunityRecord rq = q.health.immunity.ImmunityListForReading.Find(x => x.hediffDef == flu);
                HediffComp_Immunizable cq = hq.TryGetComp<HediffComp_Immunizable>();
                float gain = rq.ImmunityChangePerTick(q, true, hq) * VanillaRestingImmunity * 3.5f * S31Hour;
                if (gain <= 0f || gain >= 1f)
                {
                    RPLog.Message($"[RimPrison:test] stability 31: this guard's gain of immunity in 3.5 hours is {gain:F2}, the flu's turn not checked");
                    return;
                }
                rq.immunity = 1f - gain;
                float notImmune = cq.SeverityChangePerDay(), immune = cq.Props.severityPerDayImmune;
                // (Four steps start not immune, the other eight immune.)
                float turnWant = 0.3f + notImmune * (4 / 24f) + immune * (8 / 24f);
                if (!S31Commute(map, q, 12, check)) return;
                if (rq.immunity < 0.9999f || Mathf.Abs(hq.Severity - turnWant) > 0.003f)
                    S31Fail(check, $"a flu with immunity reaching full after 3.5 of 12 hours at home: immunity {rq.immunity:F3} (full expected), severity {hq.Severity:F3} ({turnWant:F3} expected: 4 hours at {notImmune:F2} a day, 8 at {immune:F2})");
            });
        }

        /// <summary>
        /// A starving guard's flu, tended at full quality for 6.5 more hours, 12 hours at home: the tend runs down although nothing heals (one with nothing to heal is not the end of the time) and helps for the 6.5 hours only (a
        /// step that ended where the tend did: not for the whole of the seventh hour). Fails without the timers running in a step of their own: the tend is given for 7 hours (0.0773 a day for the half hour more).
        /// </summary>
        private static void CheckHomeStarvingTend(Map map)
        {
            const string check = "a starving guard's tend";
            Guarded("stability 31: a tend with a duration runs down to its hour at home", map, r =>
            {
                HediffDef flu = DefDatabase<HediffDef>.GetNamedSilentFail("Flu");
                if (flu == null)
                {
                    RPLog.Message("[RimPrison:test] stability 31: no flu def, the tend not checked");
                    return;
                }
                // (Staff have no food need - they bring their own lunch, Patch_Pawn_NeedsTracker_ShouldHaveNeed - so the need is given to this made-up guard the way the tracker adds one. The spawn on the way
                // back asks the tracker which needs are right for them and takes it off again (round 33, second round), so the rule is judged on how they left, read before the spawn: the need is gone by the
                // time the healing runs. The premise is built, and a build that did not take is a failure, not a skip.)
                Pawn p = S31Guard(map, r);
                if (p.needs?.food != null) S31Fail(check, "premise: a member of staff has a food need of their own (they bring their own lunch)");
                else HarmonyLib.AccessTools.Method(typeof(Pawn_NeedsTracker), "AddNeed")?.Invoke(p.needs, new object[] { NeedDefOf.Food });
                if (p.needs?.food == null)
                {
                    S31Fail(check, "premise: the food need could not be given to the guard, the starving tend not checked");
                    return;
                }
                Hediff h = HediffMaker.MakeHediff(flu, p);
                h.Severity = 0.3f;
                p.health.AddHediff(h);
                // (Starving: no injury heals, and a cut that cannot heal is the case the timers stopped in.)
                p.needs.food.CurLevel = 0f;
                S31Wound(p, HediffDefOf.Cut, 5f);
                p.health.immunity.TryAddImmunityRecord(flu, flu);
                ImmunityRecord rec = p.health.immunity.ImmunityListForReading.Find(x => x.hediffDef == flu);
                HediffComp_TendDuration td = h.TryGetComp<HediffComp_TendDuration>();
                HediffComp_Immunizable comp = h.TryGetComp<HediffComp_Immunizable>();
                if (rec == null || td == null || comp == null || td.TProps.TendIsPermanent || !p.needs.food.Starving)
                {
                    S31Fail(check, $"premise: no immunity record, tend comp with a duration or immunizable comp on a flu, or the guard is not starving (starving {p.needs.food.Starving})");
                    return;
                }
                rec.immunity = 0f;
                float cut0 = S31Healable(p), perDay = comp.SeverityChangePerDay();
                float perTick = rec.ImmunityChangePerTick(p, true, h) * VanillaRestingImmunity;
                if (perTick * 12 * S31Hour >= 0.9f)
                {
                    RPLog.Message("[RimPrison:test] stability 31: this guard gains immunity to a flu too fast for the tend to be checked");
                    return;
                }
                td.tendQuality = 1f;
                td.tendTicksLeft = 6 * S31Hour + S31Hour / 2;
                float want = 0.3f + perDay * 0.5f + td.TProps.severityPerDayTended * (6.5f / 24f);
                if (!S31Commute(map, p, 12, check)) return;
                if (td.tendTicksLeft != 0) S31Fail(check, $"a starving guard's flu tend of 6.5 hours has {td.tendTicksLeft} ticks left after 12 hours at home (none expected: the timer runs although nothing heals)");
                if (Mathf.Abs(h.Severity - want) > 0.0005f)
                    S31Fail(check, $"a starving guard's flu tended for 6.5 of 12 hours at home: severity {h.Severity:F4} ({want:F4} expected: untended {perDay:F3} a day, and {td.TProps.severityPerDayTended:F4} for the 6.5 tended hours)");
                if (Mathf.Abs(S31Healable(p) - cut0) > 0.0001f) S31Fail(check, $"a starving guard's cut healed at home: {cut0:F2} -> {S31Healable(p):F2}");
            });
        }

        /// <summary>
        /// A plague near its lethal severity, days at home, comes back alive and not past the stage that threatens their life (they were not given what would have killed them, nor what would have them down at the gate), and has
        /// moved as the immunity's turn gives: held at the stage's edge while it is not full, then down at the immune rate. The time is derived from the pawn's own gain of immunity (full after about 10 to 40 hours, then 20 more),
        /// so a slow or a quick one gives the same. Fails without the healing (nothing moved) and without the clamp (the severity went past what the plague kills at: a death at home).
        /// </summary>
        private static void CheckHomePlagueNearLethal(Map map)
        {
            const string check = "a plague near lethal";
            Guarded("stability 31: a plague near lethal comes back alive from home", map, r =>
            {
                HediffDef plague = DefDatabase<HediffDef>.GetNamedSilentFail("Plague");
                if (plague == null)
                {
                    RPLog.Message("[RimPrison:test] stability 31: no plague def, the plague not checked");
                    return;
                }
                Pawn p = S31Guard(map, r);
                Hediff h = HediffMaker.MakeHediff(plague, p);
                h.Severity = 0.88f;
                p.health.AddHediff(h);
                p.health.immunity.TryAddImmunityRecord(plague, plague);
                ImmunityRecord rec = p.health.immunity.ImmunityListForReading.Find(x => x.hediffDef == plague);
                HediffComp_Immunizable comp = h.TryGetComp<HediffComp_Immunizable>();
                float threat = plague.stages.First(s => s.lifeThreatening).minSeverity;
                if (rec == null || comp == null || h.Severity >= threat)
                {
                    S31Fail(check, $"premise: no immunity record or immunizable comp, or a plague at {h.Severity:F2} is already life threatening (from {threat:F2})");
                    return;
                }
                float perTick = rec.ImmunityChangePerTick(p, true, h) * VanillaRestingImmunity;
                if (perTick <= 0f)
                {
                    RPLog.Message("[RimPrison:test] stability 31: this guard gains no immunity to a plague, the plague not checked");
                    return;
                }
                // Full after toFull ticks (at most 40 hours: from the immunity they start with), and 20 hours more at home.
                int toFull = Mathf.CeilToInt(Mathf.Min(1f / perTick, 40 * S31Hour));
                rec.immunity = Mathf.Clamp01(1f - perTick * toFull);
                int fullStep = Mathf.CeilToInt(toFull / (float)S31Hour), hours = fullStep + 20;
                // (Held at the stage's edge, a margin under it, while not immune; down at the immune rate for the steps after the one that filled it.)
                float want = threat - 0.001f + comp.Props.severityPerDayImmune * (hours - fullStep) / 24f;
                bool back = S31Commute(map, p, hours, check);
                if (!back && !p.Dead) return;
                if (p.Dead || !p.health.hediffSet.hediffs.Contains(h))
                {
                    S31Fail(check, $"a plague at 0.88 for {hours} hours at home: dead {p.Dead}, still on them {p.health.hediffSet.hediffs.Contains(h)} (alive and ill expected)");
                    return;
                }
                if (h.Severity >= threat || h.Severity >= plague.lethalSeverity || rec.immunity < 0.9999f || Mathf.Abs(h.Severity - want) > 0.02f)
                    S31Fail(check, $"a plague at 0.88 for {hours} hours at home: severity {h.Severity:F3} ({want:F3} expected, under the life-threatening stage at {threat:F2}), immunity {rec.immunity:F3} (full expected)");
                if (p.Downed) S31Fail(check, $"the guard came back from {hours} hours at home with a plague downed");
            });
        }

        /// <summary>
        /// A chronic illness is left as it was, as is an illness with nothing to become immune to: asthma has the immunity comp but no immunity to gain (its severity rises on its own comp's rate and its stage at 0.3 takes a lung's
        /// efficiency from a tenth to three tenths, which can end a breath inside the setter), and food poisoning falls by a day's rate. Both are the same after 24 hours at home. Fails when anything with a severity rate is stepped.
        /// </summary>
        private static void CheckHomeChronicLeftAlone(Map map)
        {
            const string check = "chronic illness";
            Guarded("stability 31: a chronic illness and a non-immunizable one are left as they were at home", map, r =>
            {
                Pawn p = S31Guard(map, r);
                var held = new List<(Hediff h, float severity)>();
                HediffDef asthma = DefDatabase<HediffDef>.GetNamedSilentFail("Asthma"), poison = DefDatabase<HediffDef>.GetNamedSilentFail("FoodPoisoning");
                BodyPartRecord lung = p.RaceProps.body.AllParts.FirstOrDefault(x => x.def.defName == "Lung");
                if (asthma != null && lung != null)
                {
                    Hediff h = HediffMaker.MakeHediff(asthma, p, lung);
                    h.Severity = 0.25f;
                    p.health.AddHediff(h);
                    held.Add((h, 0.25f));
                }
                if (poison != null)
                {
                    Hediff h = HediffMaker.MakeHediff(poison, p);
                    h.Severity = 0.5f;
                    p.health.AddHediff(h);
                    held.Add((h, 0.5f));
                }
                if (held.Count == 0)
                {
                    RPLog.Message("[RimPrison:test] stability 31: no asthma and no food poisoning here, the chronic illness not checked");
                    return;
                }
                if (!S31Commute(map, p, 24, check)) return;
                foreach ((Hediff h, float severity) in held)
                    if (!p.health.hediffSet.hediffs.Contains(h) || Mathf.Abs(h.Severity - severity) > 0.0001f)
                        S31Fail(check, $"{h.def.defName} went from {severity:F3} to {(p.health.hediffSet.hediffs.Contains(h) ? h.Severity.ToString("F3") : "gone")} after 24 hours at home (as it was expected: nothing to become immune to, no stepping)");
                if (p.Dead) S31Fail(check, "the guard died at home");
            });
        }

        /// <summary>A husky for the handler, with a wound or two (6 points each) on parts that can take them; null (told) when there is none to give.</summary>
        private static Pawn S31Dog(Map map, Pawn handler, Rollback r, out float wounds)
        {
            wounds = 0f;
            PrisonGame g = PrisonGame.Instance;
            CompStaff sc = handler.GetComp<CompStaff>();
            PawnKindDef husky = DefDatabase<PawnKindDef>.GetNamedSilentFail("Husky");
            if (husky == null)
            {
                RPLog.Message("[RimPrison:test] stability 31: no husky, the dog not checked");
                return null;
            }
            Pawn dog = PawnGenerator.GeneratePawn(new PawnGenerationRequest(husky, Faction.OfPlayer));
            GenSpawn.Spawn(dog, CellFinder.StandableCellNear(handler.Position, map, 3f), map);
            r.Add(() =>
            {
                sc.dog = null;
                g.home.Remove(dog);
                if (!dog.Destroyed) dog.Destroy(DestroyMode.Vanish);
            });
            sc.dog = dog;
            S31Clean(dog);
            foreach (BodyPartRecord part in dog.RaceProps.body.AllParts.Where(x => x.depth == BodyPartDepth.Outside && x.coverageAbs > 0f && x.def.hitPoints >= 15).Take(2))
            {
                var cut = (Hediff_Injury)HediffMaker.MakeHediff(HediffDefOf.Cut, dog, part);
                cut.Severity = 6f;
                dog.health.AddHediff(cut);
                wounds += 6f;
            }
            if (wounds <= 0f)
            {
                RPLog.Message("[RimPrison:test] stability 31: no part to hurt on a husky, the dog not checked");
                return null;
            }
            return dog;
        }

        /// <summary>
        /// A handler's dog at home heals the same way (its own size and healing factor, and no more than its wounds hold), and comes back with them. Fails without the dog's healing: the wounds are as they were.
        /// </summary>
        private static void CheckHomeDogHeals(Map map)
        {
            const string check = "the dog at home";
            Guarded("stability 31: a handler's dog heals at home", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn p = S31Guard(map, r);
                if (S31Dog(map, p, r, out float wounds) is not { } dog) return;
                float before = S31Healable(dog), expected = Mathf.Min(S31NaturalHeal(dog, 12), wounds);
                if (!S31Commute(map, p, 12, check)) return;
                if (!dog.Spawned || g.home.Contains(dog)) S31Fail(check, $"the dog did not come back with its handler (spawned {dog.Spawned})");
                float drop = before - S31Healable(dog);
                if (Mathf.Abs(drop - expected) > 0.05f) S31Fail(check, $"a husky's {wounds:F0} points of cuts, 12 hours at home with its handler: healed {drop:F2} ({expected:F2} expected for its size and healing factor, no more than the wounds)");
            });
        }

        /// <summary>
        /// A dog that was not at home is not healed: it went on ticking on site (here it is taken out of the house and put back on the map before its handler comes in, as one carried or in a pod at the departure would be). Fails
        /// when any dog that is spawned at the arrival gets the time away: the wound is gone or smaller.
        /// </summary>
        private static void CheckHomeDogOnSite(Map map)
        {
            const string check = "a dog that stayed";
            Guarded("stability 31: a dog that was not at home is not healed for it", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn p = S31Guard(map, r);
                CompStaff sc = p.GetComp<CompStaff>();
                if (S31Dog(map, p, r, out _) is not { } dog) return;
                StaffCommute.Leave(p);
                if (g.home.Contains(dog))
                {
                    g.home.Remove(dog);
                    GenSpawn.Spawn(dog, CellFinder.StandableCellNear(map.Center, map, 20f), map);
                }
                float before = S31Healable(dog);
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 12 * S31Hour);
                StaffCommute.Arrive(p, sc, map, g);
                if (!p.Spawned) S31Fail(check, "premise: the handler did not come back");
                else if (Mathf.Abs(S31Healable(dog) - before) > 0.0001f) S31Fail(check, $"a dog that stayed on site was healed for its handler's 12 hours at home: {before:F2} -> {S31Healable(dog):F2}");
            });
        }

        /// <summary>
        /// A way in that is not found changes nothing, and the retry does the catch-up once: out for 12 hours, the gate not found (no healing, still at home, the tick as it was), an hour later found (13 hours given, the tick -1);
        /// a second arrival does not heal again. Guards the order in <see cref="StaffCommute.Arrive"/> (the time read and reset only past the bail-out): a read before it gives nothing on the retry.
        /// </summary>
        private static void CheckHomeEntryFailure(Map map)
        {
            const string check = "no way in";
            Guarded("stability 31: an arrival that finds no way in heals nothing and the retry once", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn p = S31Guard(map, r);
                CompStaff sc = p.GetComp<CompStaff>();
                S31Wound(p, HediffDefOf.Cut, 10f);
                float start = S31Healable(p);
                StaffCommute.Leave(p);
                int left = sc.homeSinceTick;
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 12 * S31Hour);
                r.Add(() => StaffCommute.ForceNoEntry = false);
                StaffCommute.ForceNoEntry = true;
                StaffCommute.Arrive(p, sc, map, g);
                StaffCommute.ForceNoEntry = false;
                if (p.Spawned || !g.home.Contains(p) || sc.homeSinceTick != left || Mathf.Abs(S31Healable(p) - start) > 0.0001f)
                    S31Fail(check, $"an arrival that found no way in changed something: spawned {p.Spawned}, at home {g.home.Contains(p)}, away since {sc.homeSinceTick} (from {left}), the wound {start:F2} -> {S31Healable(p):F2}");
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + S31Hour);
                StaffCommute.Arrive(p, sc, map, g);
                float expected = S31NaturalHeal(p, 13), drop = start - S31Healable(p);
                if (!p.Spawned || sc.homeSinceTick != -1 || Mathf.Abs(drop - expected) > 0.05f)
                    S31Fail(check, $"the retry after a failed arrival: spawned {p.Spawned}, tick {sc.homeSinceTick} (-1 expected), healed {drop:F2} ({expected:F2} expected for the 13 hours away, once)");
            });
        }

        /// <summary>
        /// What ticks its own way is left alone: a pregnancy and an addiction are as they were after a night at home, and so is a hediff that is no plain one (missing part). Fails if the application ticked the health: the
        /// addiction's and the pregnancy's severity moved.
        /// </summary>
        private static void CheckHomeUntouched(Map map)
        {
            const string check = "not touched";
            Guarded("stability 31: pregnancy, addiction and a missing part are left as they were at home", map, r =>
            {
                Pawn p = S31Guard(map, r);
                var missing = HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, p, p.RaceProps.body.AllParts.First(x => x.def == BodyPartDefOf.Hand));
                p.health.AddHediff(missing);
                var held = new List<(Hediff h, float severity)> { (missing, missing.Severity) };
                ChemicalDef chem = DefDatabase<ChemicalDef>.AllDefsListForReading.FirstOrDefault(c => c.addictionHediff != null);
                if (chem != null)
                {
                    Hediff addiction = HediffMaker.MakeHediff(chem.addictionHediff, p);
                    addiction.Severity = 0.7f;
                    p.health.AddHediff(addiction);
                    held.Add((addiction, 0.7f));
                }
                HediffDef pregnant = HediffDefOf.PregnantHuman;
                if (p.gender == Gender.Female && pregnant != null && p.ageTracker.AgeBiologicalYears >= 16)
                {
                    Hediff pregnancy = HediffMaker.MakeHediff(pregnant, p);
                    pregnancy.Severity = 0.4f;
                    p.health.AddHediff(pregnancy);
                    held.Add((pregnancy, 0.4f));
                }
                if (!S31Commute(map, p, 24, check)) return;
                foreach ((Hediff h, float severity) in held)
                    if (!p.health.hediffSet.hediffs.Contains(h) || Mathf.Abs(h.Severity - severity) > 0.0001f)
                        S31Fail(check, $"{h.def.defName} went from {severity:F3} to {(p.health.hediffSet.hediffs.Contains(h) ? h.Severity.ToString("F3") : "gone")} after 24 hours at home (as it was expected: it ticks its own way)");
            });
        }

        // ---- the saveload plan -------------------------------------------------------------------------------------------------------------------------------------

        private static List<(int id, int tick)> staffHomeBeforeSeed;

        /// <summary>Everyone employed here, on a map or at home, by id.</summary>
        private static IEnumerable<Pawn> S31Employed(Map map) =>
            map.mapPawns.FreeColonists.Concat(PrisonGame.Instance.OffSite).Where(p => p.GetComp<CompStaff>()?.role != null).Distinct().OrderBy(p => p.thingIDNumber);

        /// <summary>For the snapshot: the time each member of staff left home at (-1 for those on site), the ones at home included (the staff line visits only those on the map).</summary>
        private static string StaffHomeDescribe(Map map) => string.Join(",", S31Employed(map).Select(p => $"{p.thingIDNumber}:{p.GetComp<CompStaff>().homeSinceTick}:{StaffCommute.AtHome(p)}"));

        private static int staffHomeGuestId = -1, staffHomeGuestSince;
        private static float staffHomeGuestWound;

        /// <summary>
        /// The time at home of every member of staff off its default, for the round trip (the ones at home are saved as deep as the rest); put back by <see cref="RestoreStaffHomeAfterReload"/>. And a guard made for it, hurt and at home
        /// for 12 hours (what the reload is to find, and arrive: <see cref="CheckStaffHomeAfterReload"/>).
        /// </summary>
        private static void SeedStaffHomeForReload(Map map)
        {
            int now = Find.TickManager.TicksGame;
            staffHomeBeforeSeed = new List<(int id, int tick)>();
            int k = 0;
            foreach (Pawn p in S31Employed(map))
            {
                CompStaff sc = p.GetComp<CompStaff>();
                staffHomeBeforeSeed.Add((p.thingIDNumber, sc.homeSinceTick));
                sc.homeSinceTick = Mathf.Max(1, now - 1000 - 17 * k++);
            }
            StaffRoleDef role = RPSecurityDefOf.RP_Guard;
            Pawn guest = StaffUtility.GenerateCandidate(role);
            GenSpawn.Spawn(guest, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            StaffUtility.ApplyRole(guest, role, record: false);
            NoMentalStateTraits(guest);
            NoMentalStateHediffs(guest);
            S31Clean(guest);
            Hediff_Injury cut = S31Wound(guest, HediffDefOf.Cut, 10f);
            StaffCommute.Leave(guest);
            CompStaff gc = guest.GetComp<CompStaff>();
            gc.homeSinceTick = Mathf.Max(1, now - 12 * S31Hour);
            (staffHomeGuestId, staffHomeGuestSince, staffHomeGuestWound) = (guest.thingIDNumber, gc.homeSinceTick, cut.Severity);
            RPLog.Message($"[RimPrison:test] saveload staff at home: {StaffHomeDescribe(map)}");
        }

        /// <summary>
        /// The hurt guard at home came back from the round trip with the tick they left at and the wound they left with, and coming in heals it once, for the time away (an arrival after a reload heals as one before it, and the tick
        /// is read and reset); then the guard is taken out. Fails without the saved tick (none to heal for: the wound as it was) or with a double application.
        /// </summary>
        private static void CheckStaffHomeAfterReload(Map map)
        {
            if (staffHomeGuestId < 0) return;
            PrisonGame g = PrisonGame.Instance;
            Pawn guest = g.OffSite.FirstOrDefault(x => x.thingIDNumber == staffHomeGuestId);
            if (guest == null) reloadProblems.Add($"the hurt guard at home did not come back from the round trip (id {staffHomeGuestId})");
            else
            {
                CompStaff sc = guest.GetComp<CompStaff>();
                float before = S31Healable(guest);
                if (sc.homeSinceTick != staffHomeGuestSince || Mathf.Abs(before - staffHomeGuestWound) > 0.0001f)
                    reloadProblems.Add($"the hurt guard at home came back with the tick {sc.homeSinceTick} (from {staffHomeGuestSince}) and wounds of {before:F2} (from {staffHomeGuestWound:F2})");
                else
                {
                    int away = Find.TickManager.TicksGame - sc.homeSinceTick;
                    StaffCommute.Arrive(guest, sc, map, g);
                    float drop = before - S31Healable(guest), expected = S31NaturalHeal(guest, 1) * away / S31Hour;
                    if (!guest.Spawned || sc.homeSinceTick != -1 || Mathf.Abs(drop - expected) > 0.05f)
                        reloadProblems.Add($"the hurt guard who came in after the reload: spawned {guest.Spawned}, tick {sc.homeSinceTick} (-1 expected), healed {drop:F2} ({expected:F2} expected for {away / (float)S31Hour:F1} hours, once)");
                }
                g.home.Remove(guest);
                if (!guest.Destroyed) guest.Destroy(DestroyMode.Vanish);
                StaffCareers.InvalidateLeads();
            }
            staffHomeGuestId = -1;
        }

        /// <summary>The seeded times are put back as they were (one at home would be healed for them at the next arrival), after the check of the hurt guard.</summary>
        private static void RestoreStaffHomeAfterReload(Map map)
        {
            CheckStaffHomeAfterReload(map);
            if (staffHomeBeforeSeed == null) return;
            Dictionary<int, Pawn> byId = S31Employed(map).ToDictionary(p => p.thingIDNumber);
            foreach ((int id, int tick) in staffHomeBeforeSeed)
                if (byId.TryGetValue(id, out Pawn p)) p.GetComp<CompStaff>().homeSinceTick = tick;
            staffHomeBeforeSeed = null;
        }
    }
}
