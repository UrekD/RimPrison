using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>
    /// The state shuttle: the daily one brings the new inmates and takes the released away, cargo runs bring the supplies between the daily ones. It
    /// lands on the warden's pad (or on clear ground near holding or the gate, when there is none or it is blocked), waits a set time and flies
    /// away. See DESIGN.md ("The state shuttle").
    /// </summary>
    public partial class ShuttleSystem : IExposable
    {
        /// <summary>How long the daily shuttle waits on the ground, and a cargo run.</summary>
        public const int DailyStayTicks = GenDate.TicksPerHour * 2, CargoStayTicks = GenDate.TicksPerHour;
        /// <summary>The pad is blocked: it waits this long for it to be cleared, then lands nearby.</summary>
        public const int PadWaitTicks = GenDate.TicksPerHour;
        /// <summary>A shuttle held by weather or a threat gives up for the day after this long.</summary>
        public const int MaxHoldTicks = GenDate.TicksPerHour * 6;
        /// <summary>Between the steps of the unloading (a passenger walks down the ramp, a stack of crates is set down).</summary>
        public const int PassengerStepTicks = 75, CargoStepTicks = 30;
        /// <summary>Cargo runs: the last run this long ago (at most four a day, so the pad is not busy all day), and the next daily shuttle this many hours off.</summary>
        public const int CargoGapTicks = GenDate.TicksPerHour * 6, CargoSkipIfDailyWithinHours = 3, CargoRunLoad = 80, CargoRunWaitTicks = GenDate.TicksPerHour * 4;
        /// <summary>An arrival waits this long for an escort before they walk to holding on their own, and gives up on the whole walk after the second.</summary>
        public const int EscortWaitTicks = 1800, ArrivalTimeoutTicks = GenDate.TicksPerHour * 10;
        /// <summary>The boarding closes this long before lift-off.</summary>
        public const int BoardingClosesTicks = 200;
        public const float StowawayChance = 0.12f, StowawayCatchChance = 0.7f, CargoContrabandChance = 0.12f, CargoSearchChance = 0.75f;
        public const int StowawayRadius = 30;
        /// <summary>The stowaway is rolled this long after landing: the guards on pad duty have had time to get to their posts.</summary>
        public const int StowawayDelayTicks = 600;
        /// <summary>A shuttle with passengers or crates still to unload at the end of its stay waits for them, up to this much longer; what is left then goes back.</summary>
        public const int OvertimeTicks = GenDate.TicksPerHour * 2;
        /// <summary>The most passengers a daily shuttle brings: one steps off every <see cref="PassengerStepTicks"/> (and up to 25 ticks more) after the first minute, within the stay.</summary>
        public const int MaxRoster = (DailyStayTicks - 60) / (PassengerStepTicks + 25);
        /// <summary>The search for clear ground when there is no pad is made at most this often.</summary>
        public const int SiteSearchEveryTicks = GenDate.TicksPerHour;
        /// <summary>A pad blocked by something that does not move (an item, a building) is told to the warden after this long (a passer-by is not).</summary>
        public const int PadBlockedNoticeTicks = 600;

        public ShuttleVisit visit;
        /// <summary>The daily shuttle has been called but the pad is taken by another visit: it lands when that one is gone.</summary>
        public bool dailyPending;
        public int lastCargoTick = -999999;
        /// <summary>The day the state last wrote to say it keeps its prisoners off the shuttle (under special measures), and how many days pass between such letters.</summary>
        public int lastHeldLetterDay = -999;
        public const int HeldLetterEveryDays = 5;
        public List<OutboundEntry> outbound = new List<OutboundEntry>();
        public List<ArrivalEntry> arrivals = new List<ArrivalEntry>();
        /// <summary>Where the shuttle sets down when there is no usable pad (kept while it stays clear).</summary>
        public IntVec3 fallbackSite = IntVec3.Invalid;
        /// <summary>Guards put on the pad while the shuttle is down (Deployment).</summary>
        public int padGuards = 1;
        public const int MaxPadGuards = 3;
        /// <summary>The inmate who is trying to stow away on this visit.</summary>
        public Pawn stowaway;
        /// <summary>
        /// Visits held up (once each), and what the pad's guards did not find in the crates (kept off the statistics screen on purpose: a count of what got past would tell the warden that something did).
        /// Every other count of the shuttle lives in <see cref="PrisonStats"/> alone.
        /// </summary>
        public int delayedVisits, cargoContrabandMissed;
        /// <summary>Arrivals a guard has walked to holding (this session's count, not saved).</summary>
        public int escortsStarted;
        /// <summary>New inmates the shuttle could not unload before it had to leave: kept in the world, they come with the next daily shuttle (before anyone new is generated).</summary>
        public List<Pawn> carriedOver = new List<Pawn>();
        /// <summary>The state's marshals' prisoners (returning ex-inmates, recaptured escapees) waiting for the next daily shuttle: they come first on its roster.</summary>
        public List<MarshalEntry> marshals = new List<MarshalEntry>();
        /// <summary>A marshals' prisoner who has waited this long for a shuttle that could take them (no bed, no shuttle) is sent to another prison.</summary>
        public const int MarshalWaitDays = 6;

        private static int Now => Find.TickManager.TicksGame;

        public static ShuttleSystem Sys => PrisonGame.Instance?.shuttle;

        // ---- queries ------------------------------------------------------------------------------------------

        public static ShuttleVisit VisitOf(Building_StateShuttle s) => Sys?.visit is { } v && v.shuttle == s ? v : null;

        public static ShuttleVisit Current => Sys?.visit;

        /// <summary>The shuttle is on the ground and its boarding is open (the last stretch before lift-off is closed, so nobody walks to a shuttle that is leaving).</summary>
        public static Building_StateShuttle Boardable(Map map)
        {
            ShuttleVisit v = Current;
            if (v is not { phase: ShuttlePhase.Landed } || v.shuttle is not { Spawned: true } s || s.Map != map) return null;
            return BoardingOpenFor(v, s) ? s : null;
        }

        public static bool BoardingOpen(Building_StateShuttle s) => VisitOf(s) is { phase: ShuttlePhase.Landed } v && BoardingOpenFor(v, s);

        /// <summary>
        /// Boarding is open until the last stretch before lift-off, and for as long as the shuttle is staying on for what is left to unload (its lift-off is put back at the closing time:
        /// pawns tick before the game components, so a boarding walk's fail condition would meet a closed shuttle for one tick every time it is put back, and every walk in progress would fail).
        /// </summary>
        public static bool BoardingOpenFor(ShuttleVisit v, Building_StateShuttle s) => v.departTick - Now > BoardingClosesTicks || Staying(v, s);

        /// <summary>The shuttle is staying on for what is left to unload (its lift-off keeps being put back, up to <see cref="HardDepartTick"/>).</summary>
        public static bool Staying(ShuttleVisit v, Building_StateShuttle s) => v.departTick < HardDepartTick(v) && Unloading(v, s);

        /// <summary>In overtime: the stay has run out and the shuttle waits for the unloading, so "lifting off in 0.1 h" would be wrong (the status says it is unloading).</summary>
        public static bool InOvertime(ShuttleVisit v) => v is { phase: ShuttlePhase.Landed, stretched: true } && v.shuttle is { Spawned: true } s && Staying(v, s);

        /// <summary>Hours until the next daily shuttle is called (0: this hour).</summary>
        public static int HoursToDaily(PrisonGame game, Map map) => (game.intake.hour - GenLocalDate.HourOfDay(map) + 24) % 24;

        public static string StatusLine(ShuttleVisit v)
        {
            switch (v.phase)
            {
                case ShuttlePhase.Landed:
                    float hours = Mathf.Max(0f, (v.departTick - Now) / (float)GenDate.TicksPerHour);
                    string line = InOvertime(v) ? "RP_ShuttleStatusUnloading".Translate(Mathf.Max(0f, (HardDepartTick(v) - Now) / (float)GenDate.TicksPerHour).ToString("0.0"), v.boarded).ToString()
                        : "RP_ShuttleStatusLanded".Translate(hours.ToString("0.0"), v.boarded).ToString();
                    if (v.steppedOff < v.rosterSize) line += " " + "RP_ShuttleStatusAboard".Translate(v.rosterSize - v.steppedOff);
                    if (v.cargo.Count > 0) line += " " + "RP_ShuttleStatusCargo".Translate(v.cargo.Sum(o => o.count));
                    return line;
                case ShuttlePhase.Inbound:
                    return "RP_ShuttleStatusInbound".Translate().ToString();
                case ShuttlePhase.Leaving:
                    return "RP_ShuttleStatusLeaving".Translate().ToString();
                default:
                    return ("RP_ShuttleStatusHolding_" + v.hold).Translate().ToString();
            }
        }

        internal void Prune(Map map)
        {
            // (Somebody who is an inmate again, not released, came back: a reoffender or a recapture.)
            outbound.RemoveAll(e => e.pawn == null || e.pawn.Dead || e.pawn.Destroyed || e.pawn.Spawned && e.pawn.IsPrisonerOfColony && e.pawn.guest is { Released: false } || e.pawn.IsFreeColonist);
            for (int i = arrivals.Count - 1; i >= 0; i--)
            {
                ArrivalEntry a = arrivals[i];
                if (a.pawn == null || a.pawn.Dead || a.pawn.Destroyed || !a.pawn.Spawned)
                {
                    ClearTransit(a.pawn);
                    arrivals.RemoveAt(i);
                }
                // Ten hours and not inside yet: they stay on the way in (every door that is not locked or staff's stays open to them) and the warden is told (an alert).
                else if (Now - a.sinceTick > ArrivalTimeoutTicks && IsInside(a.pawn))
                {
                    ClearTransit(a.pawn);
                    arrivals.RemoveAt(i);
                }
            }
        }

        // ---- scribe ---------------------------------------------------------------------------------------------

        public void ExposeData()
        {
            Scribe_Deep.Look(ref visit, "visit");
            Scribe_Values.Look(ref dailyPending, "dailyPending");
            Scribe_Values.Look(ref lastCargoTick, "lastCargoTick", -999999);
            Scribe_Values.Look(ref lastHeldLetterDay, "lastHeldLetterDay", -999);
            Scribe_Collections.Look(ref outbound, "outbound", LookMode.Deep);
            Scribe_Collections.Look(ref arrivals, "arrivals", LookMode.Deep);
            Scribe_Collections.Look(ref carriedOver, "carriedOver", LookMode.Reference);
            Scribe_Collections.Look(ref marshals, "marshals", LookMode.Deep);
            Scribe_Values.Look(ref fallbackSite, "fallbackSite", IntVec3.Invalid);
            Scribe_Values.Look(ref padGuards, "padGuards", 1);
            Scribe_References.Look(ref stowaway, "stowaway");
            Scribe_Values.Look(ref delayedVisits, "delayedVisits");
            Scribe_Values.Look(ref cargoContrabandMissed, "cargoContrabandMissed");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                outbound ??= new List<OutboundEntry>();
                outbound.RemoveAll(e => e == null || e.pawn == null);
                arrivals ??= new List<ArrivalEntry>();
                arrivals.RemoveAll(e => e == null || e.pawn == null);
                carriedOver ??= new List<Pawn>();
                carriedOver.RemoveAll(p => p == null);
                marshals ??= new List<MarshalEntry>();
                marshals.RemoveAll(e => e == null || e.pawn == null);
                foreach (MarshalEntry e in marshals) Find.WorldPawns.ForcefullyKeptPawns.Add(e.pawn);
                foreach (ArrivalEntry e in arrivals)
                    if (e.pawn.Inmate() is { } c) c.inTransit = true;
            }
        }

        /// <summary>The state of the shuttle in one string (the saveload plan compares it before and after).</summary>
        public string Describe() =>
            $"{visit?.Describe()}|{dailyPending}|{lastCargoTick}|{fallbackSite}|{padGuards}|{delayedVisits}:{cargoContrabandMissed}:{lastHeldLetterDay}"
            + $"|out[{string.Join(",", outbound.Select(e => e.pawn?.thingIDNumber + ":" + e.kind + (e.counted ? "" : "!") + (e.reoffends ? "r" : "") + (e.caseId >= 0 ? "c" + e.caseId : "") + "@" + e.sinceTick + "/" + e.escortId + "/" + e.escortTick + (e.cause != null ? "/" + e.cause : "")))}]"
            + $"|in[{string.Join(",", arrivals.Select(e => e.pawn?.thingIDNumber + ":" + e.escortId + "@" + e.sinceTick + "/" + e.walkOnTick + "/" + e.escortTick))}]|{stowaway?.thingIDNumber}"
            + $"|carried[{string.Join(",", carriedOver.Select(p => p?.thingIDNumber + ":" + p?.Inmate()?.carriedSkipDay))}]"
            + $"|marshals[{string.Join(",", marshals.Select(e => e.pawn?.thingIDNumber + ":" + e.kind + ":" + e.reason + ":" + e.escapedTick + "@" + e.sinceTick))}]";
    }
}
