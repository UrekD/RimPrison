using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Prison alerts in the vanilla right-hand stack (found by reflection, no registration): emergencies first, then
    /// what the prison is missing. One shared snapshot, refreshed every couple of seconds, feeds them all.
    /// </summary>
    public static class PrisonAlerts
    {
        private static int frame = -999;
        private static Map cachedMap;
        public static readonly List<Pawn> Rioters = new List<Pawn>(), Escaping = new List<Pawn>(), HungerStrikers = new List<Pawn>(),
            Hungry = new List<Pawn>(), NoBed = new List<Pawn>(), Strikers = new List<Pawn>(), FluSick = new List<Pawn>(),
            HoleNoBed = new List<Pawn>(), Suspended = new List<Pawn>(), ReferralBlocked = new List<Pawn>(), Hostages = new List<Pawn>(), StuckArrivals = new List<Pawn>();
        public static readonly List<string> MissingZones = new List<string>(), ReferralLines = new List<string>(), ServiceLines = new List<string>();
        /// <summary>The inmates the last count found missing, one line each (name, where last seen, how long ago): see <see cref="CountSystem"/>.</summary>
        public static readonly List<string> Unaccounted = new List<string>();
        private static readonly List<Pawn> referred = new List<Pawn>();
        public static bool NoCells, NoShowers, NoToilets, NoGuards, NoChapel, ExecutionNoTable, BureaucracyStalled, ShuttleNoBeds, NoPad, NoStove;
        /// <summary>The map has pads and none can take the shuttle (roofed, or walled off from holding), and what holds the shuttle off a pad that is only taken for now.</summary>
        public static ShuttleSystem.PadProblem PadProblem;
        public static Thing PadBlocker;
        public static int LaundryBill, ShuttleHours, DailyWages;
        /// <summary>Letters waiting more than a day, and why (see <see cref="MailSystem.Backlog"/>).</summary>
        public static int MailOverdue;
        public static MailBacklog MailReason;
        public static bool ContractAtRisk, LowFunds;

        /// <summary>
        /// The shuttle is due in the next hours and every bed is taken. Not before the first cell exists: with no beds at all there is nothing to run out of and nothing the warden
        /// can fix in a few hours (the "no cells" line says what to build, and a new facility's first morning is not an emergency).
        /// </summary>
        public static bool ShuttleWithoutBeds(int perDay, int hoursToShuttle, int capacity, int freeBeds, bool allowOvercrowding) =>
            perDay > 0 && hoursToShuttle is >= 1 and <= 3 && capacity > 0 && freeBeds == 0 && !allowOvercrowding;

        public static Map Map => Find.CurrentMap is { IsPlayerHome: true } m && PrisonGame.Instance != null ? m : null;

        /// <summary>Recompute on the next check (the snapshot otherwise lasts 120 frames).</summary>
        public static void Invalidate() => frame = -999;

        public static void Refresh()
        {
            Map map = Map;
            if (map == null || (map == cachedMap && Time.frameCount - frame < 120)) return;
            frame = Time.frameCount;
            cachedMap = map;
            Rioters.Clear();
            Hostages.Clear();
            Escaping.Clear();
            HungerStrikers.Clear();
            Hungry.Clear();
            NoBed.Clear();
            Strikers.Clear();
            FluSick.Clear();
            HoleNoBed.Clear();
            Suspended.Clear();
            ReferralBlocked.Clear();
            StuckArrivals.Clear();
            ReferralLines.Clear();
            referred.Clear();
            MissingZones.Clear();
            ServiceLines.Clear();
            Unaccounted.Clear();
            PrisonGame game = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { } c) continue;
                if (p.MentalStateDef == RPMentalStateDefOf.RP_Rioting) Rioters.Add(p);
                // (A runaway nobody saw is one the warden has not been told of: the count will find them gone, or a sighting will tell.)
                else if (p.MentalStateDef == RPMentalStateDefOf.RP_Escaping && !CountSystem.IsHidden(p)) Escaping.Add(p);
                if (HungerStrike.Fasting(c)) HungerStrikers.Add(p);
                else if (p.needs?.food is { CurCategory: >= HungerCategory.UrgentlyHungry }) Hungry.Add(p);
                if (InmateUtility.IsActiveInmate(p) && p.ownership?.OwnedBed == null) NoBed.Add(p);
                if (Outbreaks.Active && c.activeFlag && Outbreaks.Sick(p)) FluSick.Add(p);
                if (c.activeFlag && c.referral != null && c.referral.Eligible(p, c)) referred.Add(p);
            }
            // Referred to a program that cannot run (no room, no teacher on site, or switched off): they wait for nothing.
            foreach (IGrouping<ProgramDef, Pawn> waiting in referred.GroupBy(x => x.Inmate().referral))
            {
                string status = ProgramSystem.Status(map, pm, game, waiting.Key, waiting.Count(), waiting.Count(), out ProgramStatusKind kind);
                bool starved = kind == ProgramStatusKind.Waiting && ProgramSystem.WaitedTooLong(game, waiting.Key);
                if (kind != ProgramStatusKind.Blocked && !starved) continue;
                ReferralBlocked.AddRange(waiting);
                ReferralLines.Add(starved
                    ? "RP_Alert_ReferralWaitedLine".Translate(waiting.Key.LabelCap, waiting.Count(), game.programs.WaitedHours(waiting.Key), status)
                    : "RP_Alert_ReferralBlockedLine".Translate(waiting.Key.LabelCap, waiting.Count(), status));
            }
            foreach (Pawn s in map.mapPawns.FreeColonistsSpawned)
            {
                if (HostageCrisis.IsHeld(s)) Hostages.Add(s);
                if (IncidentWorker_StaffStrike.Striking(s)) Strikers.Add(s);
            }
            // (Suspended staff are sent home, off the map: they are found among everyone employed.)
            foreach (Pawn s in StaffCommute.AllStaff(map))
                if (s.GetComp<CompStaff>() is { } suspension && CorruptionSystem.Suspended(suspension) && !CorruptionSystem.StoodDownOnly(suspension)) Suspended.Add(s);
            // Due in the hole with every solitary bed taken: they will be kept in their own cell instead.
            bool solitaryFree = false;
            foreach (Building_Bed bed in pm.PrisonerBeds)
                if (bed.Spawned && pm.zones.ZoneAt(bed.Position) == RPSecurityDefOf.RP_SolitaryZone && bed.AnyUnoccupiedSleepingSlot && bed.OwnersForReading.Count == 0)
                {
                    solitaryFree = true;
                    break;
                }
            if (!solitaryFree)
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate() is { pendingPunishmentHours: > 0, forcedActivity: null } && InmateUtility.IsActiveInmate(p)) HoleNoBed.Add(p);
            StorySystem story = game.story;
            ContractAtRisk = story.contractTarget > 0 && story.contractRunsLeft <= 1 && story.contractDelivered < story.contractTarget;
            DailyWages = StaffCareers.Payroll(StaffCommute.AllStaff(map));
            LowFunds = game.ledger.funds >= 0 && game.ledger.funds < 2 * DailyWages;
            NoCells = IntakeSystem.Capacity(map) == 0;
            foreach (PrisonZoneDef z in PrisonDiagnostics.EssentialZones())
                if (!pm.zones.Any(z)) MissingZones.Add(z.label);
            NoStove = PrisonDiagnostics.KitchenWithoutStove(map, pm);
            ServiceLines.AddRange(PrisonServices.Lines(map));
            NoShowers = !pm.Fixtures.Any(f => f.Props.need == RPDefOf.RP_Hygiene);
            NoToilets = !pm.Fixtures.Any(f => f.Props.need == RPDefOf.RP_Bladder);
            NoGuards = !StaffCommute.AllStaff(map).Any(GuardUtility.IsGuard);
            int religious = map.mapPawns.PrisonersOfColonySpawned.Count(p => p.Inmate() is { religious: true });
            NoChapel = religious > 0 && !pm.zones.Any(RPFaithDefOf.RP_Chapel);
            ExecutionNoTable = map.mapPawns.PrisonersOfColonySpawned.Any(p => p.Inmate() is { } c && DeathRowSystem.ExecutionDue(c)) && DeathRowSystem.FindTable(map, null) == null;
            BureaucracyStalled = game.bureaucracy.active != null && BureaucracySystem.Stalled(map);
            MailReason = game.mail.Backlog(map, out MailOverdue);
            LaundryBill = game.ledger.Yesterday is { } y && y.totals.TryGetValue(RPDefOf.RP_Laundry, out int laundry) && laundry < -50 ? -laundry : 0;
            ShuttleHours = ShuttleSystem.HoursToDaily(game, map);
            int perDay = game.intake.perDay.Values.Sum();
            // (The hours before it comes, not the hour it comes: by then the shuttle has been and gone down the roster that fits.)
            ShuttleNoBeds = ShuttleWithoutBeds(perDay, ShuttleHours, IntakeSystem.Capacity(map), IntakeSystem.FreeBedCount(map), game.intake.allowOvercrowding);
            // No pad to land on (the shuttle sets down on open ground): from the first day the warden has had to build one.
            NoPad = !ShuttleSystem.HasPad(map);
            PadProblem = ShuttleSystem.PadsProblem(map);
            PadBlocker = null;
            if (game.shuttle.visit is { phase: ShuttlePhase.Holding, hold: ShuttleHold.PadBlocked, blockedSince: >= 0 } held && Find.TickManager.TicksGame - held.blockedSince >= ShuttleSystem.PadBlockedNoticeTicks)
                PadBlocker = ShuttleSystem.PadBlocker(map);
            StuckArrivals.AddRange(game.shuttle.StuckArrivals(map));
            Unaccounted.AddRange(CountSystem.UnaccountedLines(map, pm));
        }
    }

    public abstract class Alert_Prison : Alert
    {
        protected abstract AlertReport Report();

        public override AlertReport GetReport()
        {
            if (PrisonAlerts.Map == null) return false;
            PrisonAlerts.Refresh();
            return Report();
        }
    }

    public abstract class Alert_PrisonCritical : Alert_Critical
    {
        protected abstract AlertReport Report();

        public override AlertReport GetReport()
        {
            if (PrisonAlerts.Map == null) return false;
            PrisonAlerts.Refresh();
            return Report();
        }
    }

    public class Alert_RP_Riot : Alert_PrisonCritical
    {
        public Alert_RP_Riot() => defaultLabel = "RP_Alert_Riot".Translate();
        public override string GetLabel() => "RP_Alert_RiotN".Translate(PrisonAlerts.Rioters.Count);
        public override TaggedString GetExplanation() => "RP_Alert_RiotDesc".Translate(Money.Format(RiotPolice.Cost));
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.Rioters);
    }

    public class Alert_RP_Hostage : Alert_PrisonCritical
    {
        public Alert_RP_Hostage() => defaultLabel = "RP_Alert_Hostage".Translate();
        public override string GetLabel() => "RP_Alert_HostageN".Translate(PrisonAlerts.Hostages.Count > 0 ? PrisonAlerts.Hostages[0].LabelShort : "?");
        public override TaggedString GetExplanation() => "RP_Alert_HostageDesc".Translate();
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.Hostages);
    }

    public class Alert_RP_Escape : Alert_PrisonCritical
    {
        public Alert_RP_Escape() => defaultLabel = "RP_Alert_Escape".Translate();
        public override TaggedString GetExplanation() => "RP_Alert_EscapeDesc".Translate();
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.Escaping);
    }

    /// <summary>The inmates the last count found missing (named apart from <see cref="Alert_RP_Missing"/>, which is about the rooms the prison lacks).</summary>
    public class Alert_RP_Unaccounted : Alert_Prison
    {
        public Alert_RP_Unaccounted()
        {
            defaultLabel = "RP_Alert_Unaccounted".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override string GetLabel() => "RP_Alert_UnaccountedN".Translate(PrisonAlerts.Unaccounted.Count);
        public override TaggedString GetExplanation() => "RP_Alert_UnaccountedDesc".Translate(string.Join("\n", PrisonAlerts.Unaccounted.Select(l => "• " + l)));
        protected override AlertReport Report() => PrisonAlerts.Unaccounted.Count > 0;
    }

    public class Alert_RP_Lockdown : Alert_Prison
    {
        public Alert_RP_Lockdown()
        {
            defaultLabel = "RP_Alert_Lockdown".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override string GetLabel()
        {
            PrisonGame g = PrisonGame.Instance;
            return "RP_Alert_LockdownHours".Translate(((g.lockdownUntilTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour).ToString("0"));
        }

        public override TaggedString GetExplanation() => "RP_Alert_LockdownDesc".Translate();
        protected override AlertReport Report() => PrisonGame.Instance.LockdownActive;
    }

    public class Alert_RP_StaffStrike : Alert_Prison
    {
        public Alert_RP_StaffStrike()
        {
            defaultLabel = "RP_Alert_StaffStrike".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override TaggedString GetExplanation() => "RP_Alert_StaffStrikeDesc".Translate();
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.Strikers);
    }

    public class Alert_RP_HungerStrike : Alert_Prison
    {
        public Alert_RP_HungerStrike()
        {
            defaultLabel = "RP_Alert_HungerStrike".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override string GetLabel() => "RP_Alert_HungerStrikeN".Translate(PrisonAlerts.HungerStrikers.Count);
        public override TaggedString GetExplanation() => "RP_Alert_HungerStrikeDesc".Translate();
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.HungerStrikers);
    }

    public class Alert_RP_ShuttleNoBeds : Alert_Prison
    {
        public Alert_RP_ShuttleNoBeds()
        {
            defaultLabel = "RP_Alert_ShuttleNoBeds".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override string GetLabel() => "RP_Alert_ShuttleNoBedsIn".Translate(PrisonAlerts.ShuttleHours);
        public override TaggedString GetExplanation() => "RP_Alert_ShuttleNoBedsDesc".Translate();
        protected override AlertReport Report() => PrisonAlerts.ShuttleNoBeds;
    }

    /// <summary>No shuttle pad: the state's shuttle sets down on whatever clear ground it finds near holding, and the released wait out in the open.</summary>
    public class Alert_RP_NoPad : Alert_Prison
    {
        public Alert_RP_NoPad()
        {
            defaultLabel = "RP_Alert_NoPad".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override TaggedString GetExplanation() => "RP_Alert_NoPadDesc".Translate();
        protected override AlertReport Report() => PrisonAlerts.NoPad;
    }

    /// <summary>The map has a pad and the shuttle cannot use it: a roof over it (the landing would smash it), or no way to it from holding.</summary>
    public class Alert_RP_PadUnusable : Alert_Prison
    {
        public Alert_RP_PadUnusable()
        {
            defaultLabel = "RP_Alert_PadUnusable".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override TaggedString GetExplanation() => ("RP_Alert_PadUnusableDesc_" + PrisonAlerts.PadProblem).Translate();
        protected override AlertReport Report() => PrisonAlerts.PadProblem != ShuttleSystem.PadProblem.None;
    }

    /// <summary>The shuttle waits in orbit for something to be taken off its pad.</summary>
    public class Alert_RP_PadBlocked : Alert_Prison
    {
        public Alert_RP_PadBlocked()
        {
            defaultLabel = "RP_Alert_PadBlocked".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override TaggedString GetExplanation() => "RP_Alert_PadBlockedDesc".Translate(PrisonAlerts.PadBlocker?.LabelShortCap ?? "?");
        protected override AlertReport Report() => PrisonAlerts.PadBlocker != null ? AlertReport.CulpritIs(PrisonAlerts.PadBlocker) : false;
    }

    /// <summary>New inmates who have been outside the perimeter for ten hours or more and cannot get in.</summary>
    public class Alert_RP_StuckArrivals : Alert_Prison
    {
        public Alert_RP_StuckArrivals()
        {
            defaultLabel = "RP_Alert_StuckArrivals".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override string GetLabel() => "RP_Alert_StuckArrivalsN".Translate(PrisonAlerts.StuckArrivals.Count);
        public override TaggedString GetExplanation() => "RP_Alert_StuckArrivalsDesc".Translate();
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.StuckArrivals);
    }

    public class Alert_RP_Debt : Alert_Prison
    {
        public Alert_RP_Debt()
        {
            defaultLabel = "RP_Alert_Debt".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override TaggedString GetExplanation() => "RP_Problem_Debt".Translate();
        protected override AlertReport Report() => PrisonGame.Instance.ledger.funds < 0;
    }

    public class Alert_RP_ExecutionNoTable : Alert_Prison
    {
        public Alert_RP_ExecutionNoTable()
        {
            defaultLabel = "RP_Alert_ExecutionNoTable".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override TaggedString GetExplanation() => "RP_Problem_NoExecutionTable".Translate();
        protected override AlertReport Report() => PrisonAlerts.ExecutionNoTable;
    }

    public class Alert_RP_Hungry : Alert_Prison
    {
        public Alert_RP_Hungry()
        {
            defaultLabel = "RP_Alert_Hungry".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel() => "RP_Alert_HungryN".Translate(PrisonAlerts.Hungry.Count);
        public override TaggedString GetExplanation() => PrisonDiagnostics.HungryLine(PrisonAlerts.Map, PrisonAlerts.Hungry.Count);
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.Hungry);
    }

    /// <summary>The meals and showers cannot keep up with the inmates (too far from the cells, too few counters, seats, showers, cooks): says what to build.</summary>
    public class Alert_RP_Services : Alert_Prison
    {
        public Alert_RP_Services()
        {
            defaultLabel = "RP_Alert_Services".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel() => "RP_Alert_ServicesN".Translate(PrisonAlerts.ServiceLines.Count);
        public override TaggedString GetExplanation() => string.Join("\n\n", PrisonAlerts.ServiceLines.Select(l => "• " + l));
        protected override AlertReport Report() => PrisonAlerts.ServiceLines.Count > 0;
    }

    public class Alert_RP_NoBed : Alert_Prison
    {
        public Alert_RP_NoBed()
        {
            defaultLabel = "RP_Alert_NoBed".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel() => "RP_Alert_NoBedN".Translate(PrisonAlerts.NoBed.Count);
        public override TaggedString GetExplanation() => "RP_Problem_NoBed".Translate(PrisonAlerts.NoBed.Count);
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.NoBed);
    }

    public class Alert_RP_Inspection : Alert_Prison
    {
        public Alert_RP_Inspection()
        {
            defaultLabel = "RP_Alert_Inspection".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        private static int HoursLeft => (PrisonGame.Instance.story.inspectionTick - Find.TickManager.TicksGame) / GenDate.TicksPerHour + 1;

        public override string GetLabel() => "RP_Alert_InspectionIn".Translate(HoursLeft);
        public override TaggedString GetExplanation() => "RP_Alert_InspectionDesc".Translate(HoursLeft, PrisonGrading.Letter(PrisonGame.Instance.lastGrade));
        protected override AlertReport Report() => PrisonGame.Instance.story.inspectionTick > Find.TickManager.TicksGame;
    }

    public class Alert_RP_Blackout : Alert_Prison
    {
        public Alert_RP_Blackout()
        {
            defaultLabel = "RP_Alert_Blackout".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override string GetLabel() =>
            "RP_Alert_BlackoutLeft".Translate((PrisonGame.Instance.blackoutUntilTick - Find.TickManager.TicksGame) / GenDate.TicksPerHour + 1);
        public override TaggedString GetExplanation() => "RP_Alert_BlackoutDesc".Translate();
        protected override AlertReport Report() => PrisonGame.Instance.BlackoutActive;
    }

    public class Alert_RP_Flu : Alert_Prison
    {
        public Alert_RP_Flu()
        {
            defaultLabel = "RP_Alert_Flu".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel() => "RP_Alert_FluN".Translate(PrisonAlerts.FluSick.Count);
        public override TaggedString GetExplanation() =>
            (PrisonGame.Instance.story.outbreakVaccinated ? "RP_Alert_FluDescVaccinated" : "RP_Alert_FluDesc").Translate(PrisonAlerts.FluSick.Count);
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.FluSick);
    }

    /// <summary>What the prison is missing to run at all (cells, the essential rooms: canteen, yard, showers, holding and a kitchen with a stove, fixtures, guards, offices).</summary>
    public class Alert_RP_Missing : Alert_Prison
    {
        public Alert_RP_Missing()
        {
            defaultLabel = "RP_Alert_Missing".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        private static IEnumerable<string> Lines()
        {
            if (PrisonAlerts.NoCells) yield return "RP_Problem_NoCells".Translate();
            foreach (string z in PrisonAlerts.MissingZones) yield return "RP_Problem_NoZone".Translate(z);
            if (PrisonAlerts.NoStove) yield return "RP_Problem_NoStove".Translate();
            if (PrisonAlerts.NoShowers) yield return "RP_Problem_NoShowers".Translate();
            if (PrisonAlerts.NoToilets) yield return "RP_Problem_NoToilets".Translate();
            if (PrisonAlerts.NoGuards) yield return "RP_Problem_NoGuards".Translate();
            if (PrisonAlerts.NoChapel) yield return "RP_Problem_NoChapel".Translate(PrisonAlerts.Map.mapPawns.PrisonersOfColonySpawned.Count(p => p.Inmate() is { religious: true }));
            if (PrisonAlerts.BureaucracyStalled) yield return "RP_Problem_BureaucracyStalled".Translate(PrisonGame.Instance.bureaucracy.active.label);
            if (PrisonAlerts.LaundryBill > 0) yield return "RP_Problem_LaundryBill".Translate(Money.Format(PrisonAlerts.LaundryBill));
        }

        public override string GetLabel() => "RP_Alert_MissingN".Translate(Lines().Count());
        public override TaggedString GetExplanation() => string.Join("\n\n", Lines().Select(l => "• " + l));
        protected override AlertReport Report() => Lines().Any();
    }

    public class Alert_RP_HoleNoBed : Alert_Prison
    {
        public Alert_RP_HoleNoBed()
        {
            defaultLabel = "RP_Alert_HoleNoBed".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel() => "RP_Alert_HoleNoBedN".Translate(PrisonAlerts.HoleNoBed.Count);
        public override TaggedString GetExplanation() => "RP_Alert_HoleNoBedDesc".Translate();
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.HoleNoBed);
    }

    public class Alert_RP_Suspended : Alert_Prison
    {
        public Alert_RP_Suspended() => defaultLabel = "RP_Alert_Suspended".Translate();
        public override string GetLabel() => "RP_Alert_SuspendedN".Translate(PrisonAlerts.Suspended.Count);
        public override TaggedString GetExplanation() => "RP_Alert_SuspendedDesc".Translate();
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.Suspended);
    }

    public class Alert_RP_ContractAtRisk : Alert_Prison
    {
        public Alert_RP_ContractAtRisk()
        {
            defaultLabel = "RP_Alert_ContractAtRisk".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override TaggedString GetExplanation()
        {
            StorySystem s = PrisonGame.Instance.story;
            return "RP_Alert_ContractAtRiskDesc".Translate(s.contractDelivered, s.contractTarget);
        }

        protected override AlertReport Report() => PrisonAlerts.ContractAtRisk;
    }

    /// <summary>The state has stepped in: days left to the review, where the targets stand, what is restricted.</summary>
    public class Alert_RP_SpecialMeasures : Alert_Prison
    {
        public Alert_RP_SpecialMeasures()
        {
            defaultLabel = "RP_Alert_Measures".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override string GetLabel() => "RP_Alert_MeasuresN".Translate(SpecialMeasures.State is { } o ? SpecialMeasures.DaysLeft(o) : 0);

        public override TaggedString GetExplanation()
        {
            PrisonGame game = PrisonGame.Instance;
            StateOversight o = game.story.oversight;
            string newInmates = SpecialMeasures.IntakeLimit == 0 ? "RP_Alert_MeasuresHeld".Translate().ToString() : "RP_Alert_MeasuresCapped".Translate(SpecialMeasures.IntakeLimit).ToString();
            return "RP_Alert_MeasuresDesc".Translate(SpecialMeasures.DaysLeft(o), SpecialMeasures.Targets(game, PrisonAlerts.Map, o), newInmates, SpecialMeasures.PaymentCut.ToStringPercent(), SpecialMeasures.SolitaryCapHours);
        }

        protected override AlertReport Report() => SpecialMeasures.Active;
    }

    public class Alert_RP_StateChief : Alert_Prison
    {
        public Alert_RP_StateChief()
        {
            defaultLabel = "RP_Alert_StateChief".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override TaggedString GetExplanation()
        {
            StateOversight o = PrisonGame.Instance.story.oversight;
            return "RP_Alert_StateChiefDesc".Translate(o.chief?.LabelShort ?? "?", SpecialMeasures.ChiefRecallDays, o.goodDays, SpecialMeasures.PaymentCut.ToStringPercent(), SpecialMeasures.SolitaryCapHours);
        }

        // (The restrictions go on while the chief is the state's: also in the gap between losing one and the next arriving.)
        protected override AlertReport Report() => SpecialMeasures.State is { underChief: true };
    }

    /// <summary>The state is watching: the record is close to a trigger (bad grades, deaths and escapes, a loan in default) and nothing has happened yet.</summary>
    public class Alert_RP_MeasuresWatch : Alert_Prison
    {
        public Alert_RP_MeasuresWatch()
        {
            defaultLabel = "RP_Alert_MeasuresWatch".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override TaggedString GetExplanation() =>
            "RP_Alert_MeasuresWatchDesc".Translate(string.Join("\n", SpecialMeasures.Watching(PrisonGame.Instance, PrisonAlerts.Map, PrisonGame.Instance.story.oversight)));

        protected override AlertReport Report() => SpecialMeasures.State is { } o && SpecialMeasures.Watching(PrisonGame.Instance, PrisonAlerts.Map, o).Count > 0;
    }

    public class Alert_RP_LowFunds : Alert_Prison
    {
        public Alert_RP_LowFunds() => defaultLabel = "RP_Alert_LowFunds".Translate();
        public override TaggedString GetExplanation() => "RP_Alert_LowFundsDesc".Translate(Money.Format(PrisonGame.Instance.ledger.funds), Money.Format(PrisonAlerts.DailyWages));
        protected override AlertReport Report() => PrisonAlerts.LowFunds;
    }

    public class Alert_RP_ReferralBlocked : Alert_Prison
    {
        public Alert_RP_ReferralBlocked()
        {
            defaultLabel = "RP_Alert_ReferralBlocked".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel() => "RP_Alert_ReferralBlockedN".Translate(PrisonAlerts.ReferralBlocked.Count);
        public override TaggedString GetExplanation() => "RP_Alert_ReferralBlockedDesc".Translate(string.Join("\n\n", PrisonAlerts.ReferralLines));
        protected override AlertReport Report() => AlertReport.CulpritsAre(PrisonAlerts.ReferralBlocked);
    }

    /// <summary>Letters have waited more than a day in the post: no mailroom, nobody to sort, or too much post for the sorters.</summary>
    public class Alert_RP_Mail : Alert_Prison
    {
        public Alert_RP_Mail()
        {
            defaultLabel = "RP_Alert_Mail".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel() => "RP_Alert_MailN".Translate(MailSystem.Letters(PrisonAlerts.MailOverdue));
        public override TaggedString GetExplanation() => ("RP_Alert_MailDesc_" + PrisonAlerts.MailReason).Translate(MailSystem.Letters(PrisonAlerts.MailOverdue));
        protected override AlertReport Report() => PrisonAlerts.MailReason != MailBacklog.None;
    }
}
