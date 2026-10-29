using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    public enum HearingStage : byte
    {
        /// <summary>Scheduled at midnight; waiting for the board's hours and a free chair.</summary>
        Queued,
        /// <summary>Called: waiting for a guard to walk them in (or on their way).</summary>
        Called,
        /// <summary>In a chair before the board.</summary>
        Seated,
        /// <summary>The hearing is over; the warden's letter is waiting for an answer.</summary>
        Held,
    }

    /// <summary>
    /// One inmate's hearing, from the midnight pass that scheduled it to the board's verdict. It keeps people by id and name, like the chronicle does,
    /// and what the board had before it when it sat, so the letter and the verdict agree whatever changes while the letter waits.
    /// </summary>
    public class ParoleCase : IExposable
    {
        public int pawnId = -1;
        public string name;
        public HearingStage stage;
        public int scheduledTick, calledTick = -1, seatedTick = -1, heldTick = -1, escortId = -1;
        /// <summary>A guard walked them in; the warden's word is asked for (the closest call of the day); the victim's family wrote against them.</summary>
        public bool escorted, askWarden, victimLetter;
        /// <summary>The victim's family withdrew its letter after a restorative meeting that went well (see <see cref="Amends"/>): it no longer counts against them.</summary>
        public bool victimWithdrawn;
        public string escortName, boardName;
        /// <summary>The sitting: who spoke for and against, the board's odds with all of it counted, the reoffend risk, and the reasons that count against them (joined with |).</summary>
        public bool assessed;
        public string forText, againstText, reasons;
        public float chance, risk;

        public void ExposeData()
        {
            Scribe_Values.Look(ref pawnId, "pawnId", -1);
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref stage, "stage");
            Scribe_Values.Look(ref scheduledTick, "scheduled");
            Scribe_Values.Look(ref calledTick, "called", -1);
            Scribe_Values.Look(ref seatedTick, "seated", -1);
            Scribe_Values.Look(ref heldTick, "held", -1);
            Scribe_Values.Look(ref escortId, "escortId", -1);
            Scribe_Values.Look(ref escorted, "escorted");
            Scribe_Values.Look(ref askWarden, "askWarden");
            Scribe_Values.Look(ref victimLetter, "victimLetter");
            Scribe_Values.Look(ref victimWithdrawn, "victimWithdrawn");
            Scribe_Values.Look(ref escortName, "escortName");
            Scribe_Values.Look(ref boardName, "boardName");
            Scribe_Values.Look(ref assessed, "assessed");
            Scribe_Values.Look(ref forText, "forText");
            Scribe_Values.Look(ref againstText, "againstText");
            Scribe_Values.Look(ref reasons, "reasons");
            Scribe_Values.Look(ref chance, "chance");
            Scribe_Values.Look(ref risk, "risk");
        }
    }

    /// <summary>The hearing room: a zone with an office desk (the board's) and at least one chair.</summary>
    public static class HearingRooms
    {
        public const int MaxSeats = 4;

        public static List<Building> Desks(Map map, PrisonMap pm)
        {
            var list = new List<Building>();
            if (!pm.zones.Any(RPParoleDefOf.RP_HearingRoom)) return list;
            foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(RPAdminDefOf.RP_OfficeDesk))
                if (pm.zones.ZoneAt(b.Position) == RPParoleDefOf.RP_HearingRoom) list.Add(b);
            return list;
        }

        public static List<Building> Seats(Map map, PrisonMap pm)
        {
            var list = new List<Building>();
            if (!pm.zones.Any(RPParoleDefOf.RP_HearingRoom)) return list;
            // (The board's own chair, at the desk's interaction cell, is not a seat for the inmate; nor is one that is on fire.)
            var boardChairs = new HashSet<IntVec3>(Desks(map, pm).Where(d => d.def.hasInteractionCell).Select(d => d.InteractionCell));
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
                if (b.def.building is { isSittable: true } && pm.zones.ZoneAt(b.Position) == RPParoleDefOf.RP_HearingRoom && !boardChairs.Contains(b.Position) && !b.IsBurning()) list.Add(b);
            return list;
        }

        /// <summary>Hearings the room holds at once: one a chair, up to <see cref="MaxSeats"/>; 0 when it has no desk or no chair.</summary>
        public static int Capacity(Map map, PrisonMap pm) => pm == null ? 0 : Desks(map, pm).Count == 0 ? 0 : Mathf.Min(MaxSeats, Seats(map, pm).Count);

        public static bool Available(Map map, PrisonMap pm) => Capacity(map, pm) > 0;

        /// <summary>Somewhere to stand in the room that is no furniture's cell: where the escort waits.</summary>
        public static IntVec3 PostFor(Pawn pawn, PrisonMap pm)
        {
            List<IntVec3> cells = pm.zones.CellsOf(RPParoleDefOf.RP_HearingRoom);
            for (int tries = 0; tries < 24 && cells.Count > 0; tries++)
            {
                IntVec3 c = cells.RandomElement();
                if (c.Standable(pawn.Map) && c.GetEdifice(pawn.Map) == null && c.GetFirstBuilding(pawn.Map) == null && pawn.CanReach(c, PathEndMode.OnCell, Danger.Some)) return c;
            }
            return IntVec3.Invalid;
        }
    }

    /// <summary>
    /// Parole as a scene. With a hearing room (a desk and a chair in the hearing zone) the midnight pass no longer rolls for those who are due: it
    /// schedules as many hearings as the room can hold (one a chair an hour, between <see cref="StartHour"/> and <see cref="EndHour"/>; the rest are
    /// still rolled by the board at midnight, as without a room). At the board's hours the inmate is called, a guard walks them in, and they sit in
    /// a chair before the board (an administrator at the desk if there is one, else the warden's chair). Then the board weighs what the warden
    /// can see on the file (record, programs, work, tension) and who spoke: for them a trusted officer, the chaplain, a mentor; against them the
    /// victim's family when the crime was violent; and the band they fall in for reoffending. The closest call of the day goes to the warden as the
    /// decision letter, with all of that on it; the rest are decided at once. A refusal is written to the file with its reasons, and the inmate
    /// may set out to prove the board wrong. Without a room none of this happens, and the roll and the warden's letter are as they always were.
    /// </summary>
    public static class ParoleScene
    {
        public const int StartHour = 9, EndHour = 17;
        /// <summary>The time before the board (about twenty-five minutes); the longest the board waits for its administrator (an hour); how long a called inmate waits for an escort before
        /// they may walk in alone (two hours; never from maximum security); and how long a called inmate has to get there (four hours), or a queued one to be called (two days).</summary>
        public const int SittingTicks = 1500, BoardWaitTicks = 2500, UnescortedAfterTicks = 5000, CalledGiveUpTicks = 10000, QueuedGiveUpTicks = 2 * GenDate.TicksPerDay;
        /// <summary>What each speaker adds to (or takes from) the board's odds, as the letter states it.</summary>
        public const float OfficerFor = 0.10f, ChaplainFor = 0.05f, ChaplainGuidedFor = 0.08f, MentorFor = 0.05f, ProvedFor = 0.05f, VictimAgainst = 0.15f, VictimFor = 0.05f;
        /// <summary>The reoffend band: low risk helps, high risk hurts. The cut-offs are the file's.</summary>
        public const float LowRisk = 0.25f, MediumRisk = 0.45f, BandLow = 0.05f, BandHigh = -0.10f;
        public const float VictimLetterChance = 0.6f;
        /// <summary>The last of the board's odds: nothing is certain either way.</summary>
        public const float MaxChance = 0.95f;

        private static int Now => Find.TickManager.TicksGame;

        // ---- lookups ---------------------------------------------------------------------------------------------

        public static ParoleCase CaseOf(PrisonGame game, Pawn p)
        {
            if (p == null || game == null) return null;
            List<ParoleCase> cases = game.story.paroleCases;
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].pawnId == p.thingIDNumber) return cases[i];
            return null;
        }

        public static ParoleCase CaseOf(Pawn p) => CaseOf(PrisonGame.Instance, p);

        private static Pawn InmateOf(Map map, int id)
        {
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].thingIDNumber == id) return InmateUtility.IsActiveInmate(inmates[i]) ? inmates[i] : null;
            return null;
        }

        /// <summary>The prisoner with this id if they are alive and still on the map, active or not (carried when downed, out in a vanilla break).</summary>
        private static Pawn PrisonerOf(Map map, int id)
        {
            List<Pawn> prisoners = map.mapPawns.PrisonersOfColony;
            for (int i = 0; i < prisoners.Count; i++)
                if (prisoners[i].thingIDNumber == id) return prisoners[i].Dead || prisoners[i].Destroyed ? null : prisoners[i];
            return null;
        }

        public static string BandOf(float risk) => risk < LowRisk ? "RP_RiskLow" : risk < MediumRisk ? "RP_RiskMedium" : "RP_RiskHigh";

        public static float BandDelta(float risk) => risk < LowRisk ? BandLow : risk < MediumRisk ? 0f : BandHigh;

        private static string Signed(float delta) => (delta < 0f ? "-" : "+") + Mathf.Abs(Mathf.RoundToInt(delta * 100f)) + "%";

        /// <summary>"low (12%), +5% on the board's odds" (the band as the letter states it).</summary>
        public static string BandLine(ParoleCase hc) =>
            "RP_ParoleBandLine".Translate(BandOf(hc.risk).Translate(), hc.risk.ToStringPercent(), BandDelta(hc.risk) == 0f ? "RP_ParoleNoChange".Translate().ToString() : Signed(BandDelta(hc.risk))).ToString();

        // ---- midnight: who is heard in the room ----------------------------------------------------------------------

        /// <summary>
        /// Schedules those due for a hearing the room can hold (the closest calls first) and returns them; the rest are the board's to roll. The warden's
        /// letter goes to the closest call of those scheduled. Without a room (or with the day's places taken) nobody is scheduled.
        /// </summary>
        public static List<Pawn> Schedule(PrisonGame game, Map map, List<Pawn> due, out int overflow)
        {
            var scheduled = new List<Pawn>();
            overflow = 0;
            PrisonMap pm = PrisonMap.Of(map);
            int seats = HearingRooms.Capacity(map, pm);
            if (seats == 0) return scheduled;
            List<ParoleCase> cases = game.story.paroleCases;
            int free = seats * (EndHour - StartHour) - cases.Count(x => x.stage != HearingStage.Held);
            foreach (Pawn p in due.OrderBy(x => Mathf.Abs(ParoleSystem.ParoleChance(x.Inmate()) - 0.5f)))
            {
                if (scheduled.Count >= free) break;
                if (CaseOf(game, p) != null) continue;
                CompInmate c = p.Inmate();
                c.lastHearingTick = Now;
                // (A family that left a restorative meeting hostile writes for certain; one that made peace withdraws what it would have written.)
                bool letter = c.crime is { violent: true } && (Amends.Hostile(c) || Rand.Chance(VictimLetterChance));
                var hc = new ParoleCase { pawnId = p.thingIDNumber, name = p.LabelShort, scheduledTick = Now, victimLetter = letter, victimWithdrawn = letter && Amends.Made(c) };
                cases.Add(hc);
                scheduled.Add(p);
            }
            overflow = due.Count - scheduled.Count;
            // The warden hears the closest call of the day (one letter at a time, as ever).
            if (scheduled.Count > 0 && !cases.Any(x => x.askWarden) && !StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_Parole")))
                CaseOf(game, scheduled[0]).askWarden = true;
            return scheduled;
        }

        // ---- the hours of the board ----------------------------------------------------------------------------------

        /// <summary>Hourly: cases that cannot go on are dropped, and at the board's hours queued inmates are called into the free chairs.</summary>
        public static void Hourly(PrisonGame game, Map map, int hour)
        {
            List<ParoleCase> cases = game.story.paroleCases;
            if (cases.Count == 0) return;
            PrisonMap pm = PrisonMap.Of(map);
            int seats = HearingRooms.Capacity(map, pm), now = Now;
            for (int i = cases.Count - 1; i >= 0; i--)
            {
                ParoleCase hc = cases[i];
                Pawn p = InmateOf(map, hc.pawnId);
                if (hc.stage == HearingStage.Held)
                {
                    // The letter is the source of truth: the case stays while the inmate is alive and on the map, active or not (carried when downed, out in a vanilla
                    // break), and the decision worker answers it (or it times out into an abstention). One that was lost altogether does not hold the case for ever.
                    Pawn alive = PrisonerOf(map, hc.pawnId);
                    if (alive == null) cases.RemoveAt(i);
                    else if (now - hc.heldTick > 13 * GenDate.TicksPerHour && !StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_Parole") && l.pawns.Contains(alive)))
                        Verdict(game, hc, alive, 0);
                    continue;
                }
                bool stale = hc.stage == HearingStage.Queued ? now - hc.scheduledTick > QueuedGiveUpTicks
                    : hc.stage == HearingStage.Called ? now - hc.calledTick > CalledGiveUpTicks : now - hc.seatedTick > SittingTicks + 3 * BoardWaitTicks;
                if (p == null || seats == 0 || stale) Drop(game, hc, p ?? PrisonerOf(map, hc.pawnId), seats == 0);
            }
            if (seats == 0 || hour < StartHour || hour >= EndHour) return;
            int active = cases.Count(x => x.stage == HearingStage.Called || x.stage == HearingStage.Seated);
            foreach (ParoleCase hc in cases.Where(x => x.stage == HearingStage.Queued).ToList())
            {
                if (active >= seats) break;
                Pawn p = InmateOf(map, hc.pawnId);
                if (p == null || !Callable(game, p, p.Inmate())) continue;
                hc.stage = HearingStage.Called;
                hc.calledTick = now;
                active++;
                InmateUtility.InterruptForRegimeChange(p);
                if (AutoTest.Active) RPLog.Message($"[RimPrison:parole] {p.LabelShort} called to the hearing room");
            }
        }

        /// <summary>Can they be called now: in their own right mind, not confined, not under lockdown.</summary>
        private static bool Callable(PrisonGame game, Pawn p, CompInmate c) => p.Spawned && NotNow(game, p, c) == null;

        /// <summary>
        /// Why the board cannot hear them, or decide on them, now (a keyed phrase; null when it can): not in the game's hands (carried, out in a break), downed,
        /// in a mental state (a riot, an assault), under lockdown, confined, searched or off the regime, or on fire.
        /// </summary>
        private static string NotNow(PrisonGame game, Pawn p, CompInmate c)
        {
            if (c == null || !InmateUtility.IsActiveInmate(p)) return "RP_ParoleOff_Away";
            if (c.security is { escortOnly: true }) return "RP_ParoleOff_Supermax";
            if (Trials.OnRemand(game, p)) return "RP_ParoleOff_Remand";
            if (p.Downed) return "RP_ParoleOff_Downed";
            if (p.InMentalState) return "RP_ParoleOff_Mental";
            if (game.LockedDown(c)) return "RP_ParoleOff_Lockdown";
            if (c.forcedActivity != null || c.awaitingSearch || !c.activeFlag) return "RP_ParoleOff_Confined";
            if (p.IsBurning()) return "RP_ParoleOff_Fire";
            return null;
        }

        /// <summary>
        /// A hearing that cannot be held (no way in, no escort, no chair, no room): it is dropped and the board's roll takes its place, as it did before there was a
        /// room, so nobody is left out of consideration. An inmate who cannot be released just now (downed, in a mental state, under lockdown) is due again at midnight.
        /// </summary>
        public static void Drop(PrisonGame game, ParoleCase hc, Pawn p, bool roomGone = false)
        {
            game.story.paroleCases.Remove(hc);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:parole] hearing of {hc.name} dropped at stage {hc.stage}");
            if (p?.Inmate() is not { } c) return;
            if (!InmateUtility.IsActiveInmate(p) || p.Downed || p.InMentalState || game.LockedDown(c) || Trials.OnRemand(game, p))
            {
                c.lastHearingTick = -999999;
                return;
            }
            string why = (roomGone ? "RP_ParoleDropped_NoRoom" : "RP_ParoleDropped_" + hc.stage).Translate();
            Messages.Message("RP_ParoleDropped".Translate(p.LabelShort, why), p, MessageTypeDefOf.NeutralEvent, historical: false);
            if (!ParoleSystem.BoardDecides(p, c)) return;
            ParoleSystem.Grant(game, p);
            Messages.Message("RP_ParoleGranted".Translate(p.LabelShort), p, MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>The hearing ends without a verdict: the inmate is not in a state to be heard or paroled (the case is gone; they are due again after the usual gap).</summary>
        private static void CallOff(PrisonGame game, ParoleCase hc, Pawn p, string why)
        {
            game.story.paroleCases.Remove(hc);
            Messages.Message("RP_ParoleCalledOff".Translate(p.LabelShort, why.Translate()), p, MessageTypeDefOf.NeutralEvent, historical: false);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:parole] hearing of {hc.name} called off at stage {hc.stage}: {why}");
        }

        // ---- every 250 ticks: the sitting is over when the board is ready --------------------------------------------

        public static void Rare(PrisonGame game, Map map)
        {
            List<ParoleCase> cases = game.story.paroleCases;
            if (cases.Count == 0) return;
            PrisonMap pm = PrisonMap.Of(map);
            for (int i = cases.Count - 1; i >= 0; i--)
            {
                ParoleCase hc = cases[i];
                if (hc.stage == HearingStage.Queued || hc.stage == HearingStage.Held) continue;
                Pawn p = InmateOf(map, hc.pawnId);
                if (p == null) continue;
                CompInmate c = p.Inmate();
                // An escort that is no longer walking them in (an alarm took the guard) is no escort: they wait for another.
                if (hc.stage == HearingStage.Called && hc.escorted && !Escorting(map, hc, p)) Unescort(hc);
                // Called and nobody comes: after two hours they go alone, if they are free to and there is a chair to go to.
                if (hc.stage == HearingStage.Called && !hc.escorted && MayGoAlone(hc, c) && NotNow(game, p, c) == null && JobFor(p, c, pm) != null) InmateUtility.InterruptForRegimeChange(p);
                if (hc.stage != HearingStage.Seated) continue;
                // Rioting, assaulting, downed, locked down, on fire: whatever has become of them, the board does not decide on them.
                if (NotNow(game, p, c) is { } off)
                {
                    CallOff(game, hc, p, off);
                    continue;
                }
                // Got up and left their chair: called again, and walked in again.
                if (pm?.zones.ZoneAt(p.Position) != RPParoleDefOf.RP_HearingRoom)
                {
                    hc.stage = HearingStage.Called;
                    hc.calledTick = Now;
                    Unescort(hc);
                    continue;
                }
                if (Now - hc.seatedTick >= SittingTicks && BoardReady(map, pm, hc, out Pawn board)) Conclude(game, map, hc, p, board);
            }
        }

        private static bool Escorting(Map map, ParoleCase hc, Pawn inmate)
        {
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                if (colonists[i].thingIDNumber == hc.escortId) return colonists[i].CurJobDef == RPParoleDefOf.RP_EscortToHearing && colonists[i].CurJob.targetA.Thing == inmate;
            return false;
        }

        private static void Unescort(ParoleCase hc)
        {
            hc.escorted = false;
            hc.escortId = -1;
            hc.escortName = null;
        }

        /// <summary>An administrator at the desk is the board; none on site (or none who came within the hour) leaves the warden's chair.</summary>
        internal static bool BoardReady(Map map, PrisonMap pm, ParoleCase hc, out Pawn board)
        {
            board = null;
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            bool anyAdmin = false;
            for (int i = 0; i < colonists.Count; i++)
            {
                if (colonists[i].GetComp<CompStaff>()?.role != RPParoleDefOf.RP_Administrator) continue;
                anyAdmin = true;
                if (AtTheDesk(colonists[i], pm) && pm.zones.ZoneAt(colonists[i].Position) == RPParoleDefOf.RP_HearingRoom) board = colonists[i];
            }
            return board != null || !anyAdmin || Now - hc.seatedTick >= BoardWaitTicks;
        }

        /// <summary>On the sitting job, or (one at paperwork at the room's own desk is at that desk) at the hearing room's desk: the board.</summary>
        internal static bool AtTheDesk(Pawn p, PrisonMap pm)
        {
            Job job = p.CurJob;
            return job != null && (job.def == RPParoleDefOf.RP_SitBoard || job.def == RPAdminDefOf.RP_Paperwork && job.targetA.Thing is { } desk && pm.zones.ZoneAt(desk.Position) == RPParoleDefOf.RP_HearingRoom);
        }

        /// <summary>The board of a hearing that is sitting: on the sitting job, or at the hearing room's desk while a case is seated (a break would send them off mid-hearing, and the hearing falls back to the warden's chair).</summary>
        public static bool SittingNow(Pawn p, PrisonMap pm)
        {
            if (p.CurJobDef == RPParoleDefOf.RP_SitBoard) return true;
            if (!AtTheDesk(p, pm)) return false;
            List<ParoleCase> cases = PrisonGame.Instance.story.paroleCases;
            for (int i = 0; i < cases.Count; i++)
                if (cases[i].stage == HearingStage.Seated) return true;
            return false;
        }

        // ---- the inmate and the escort -----------------------------------------------------------------------------

        public static bool MayGoAlone(ParoleCase hc, CompInmate c) =>
            Now - hc.calledTick >= UnescortedAfterTicks && (c?.security == null || c.security.dangerFactor < 2f);

        /// <summary>The hearing job for an inmate whose case is on (null: none, no free chair, or still waiting for the escort).</summary>
        public static Job JobFor(Pawn p, CompInmate c, PrisonMap pm)
        {
            ParoleCase hc = CaseOf(p);
            if (hc == null || hc.stage != HearingStage.Called && hc.stage != HearingStage.Seated) return null;
            if (hc.stage == HearingStage.Called && !hc.escorted && !MayGoAlone(hc, c)) return null;
            foreach (Building seat in HearingRooms.Seats(p.Map, pm).InRandomOrder())
            {
                if (!p.CanReserveAndReach(seat, PathEndMode.OnCell, Danger.Some)) continue;
                Job job = JobMaker.MakeJob(RPParoleDefOf.RP_AttendHearing, seat);
                job.expiryInterval = GenDate.TicksPerHour * 3;
                // Walked in by a guard (whose escort job is a walk), at the guard's pace: at a jog they would reach the room long before them.
                if (hc.stage == HearingStage.Called && hc.escorted) job.locomotionUrgency = LocomotionUrgency.Walk;
                return job;
            }
            return null;
        }

        /// <summary>Called inmates nobody is walking in yet.</summary>
        public static IEnumerable<Pawn> ToEscort(Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null) yield break;
            foreach (ParoleCase hc in game.story.paroleCases)
                if (hc.stage == HearingStage.Called && !hc.escorted && InmateOf(map, hc.pawnId) is { } p) yield return p;
        }

        /// <summary>A guard is walking this inmate to their hearing.</summary>
        public static bool IsEscorted(Pawn inmate) => CaseOf(inmate) is { escorted: true };

        public static bool NeedsEscort(Pawn inmate) => CaseOf(inmate) is { stage: HearingStage.Called, escorted: false };

        /// <summary>The guard has the inmate by the arm: they walk in together.</summary>
        public static void Notify_Escorted(Pawn inmate, Pawn guard)
        {
            if (CaseOf(inmate) is not { stage: HearingStage.Called } hc) return;
            hc.escorted = true;
            hc.escortId = guard.thingIDNumber;
            hc.escortName = guard.LabelShort;
            // (The doors on the way to the hearing room let them through while the guard has them: a locked cell is no bar to it.)
            EscortWalk.Hold(inmate, guard, EscortKind.Hearing, guard.CurJob?.targetB.Cell ?? IntVec3.Invalid);
            // Whatever they were doing is over: they are wanted.
            if (inmate.CurJob is { } job && job.def != JobDefOf.Ingest) inmate.jobs.EndCurrentJob(JobCondition.InterruptForced);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:parole] {guard.LabelShort} is escorting {inmate.LabelShort} to the hearing room");
        }

        /// <summary>The guard's walk is over: if the inmate has not been seated, nobody is escorting them any more.</summary>
        public static void Notify_EscortEnded(Pawn inmate, Pawn guard)
        {
            if (CaseOf(inmate) is { stage: HearingStage.Called, escorted: true } hc && hc.escortId == guard.thingIDNumber) Unescort(hc);
            EscortWalk.Release(inmate, guard);
        }

        /// <summary>A beat of the guard's escort: the walk is made again when it is not there (a load) and the case is this guard's.</summary>
        public static void HoldAfterLoad(Pawn inmate, Pawn guard)
        {
            if (inmate.Inmate() is { leg: null } && CaseOf(inmate) is { stage: HearingStage.Called or HearingStage.Seated, escorted: true } hc && hc.escortId == guard.thingIDNumber)
                EscortWalk.Hold(inmate, guard, EscortKind.Hearing, guard.CurJob?.targetB.Cell ?? IntVec3.Invalid);
        }

        /// <summary>The inmate is now somewhere the board does not go to (supermax): the case is dropped, and nobody is escorted to a hearing that will not be held.</summary>
        public static void Notify_NoHearing(Pawn inmate)
        {
            if (CaseOf(inmate) is { } hc) PrisonGame.Instance.story.paroleCases.Remove(hc);
        }

        public static void Notify_Seated(Pawn inmate)
        {
            if (CaseOf(inmate) is not { stage: HearingStage.Called } hc) return;
            hc.stage = HearingStage.Seated;
            hc.seatedTick = Now;
            if (AutoTest.Active) RPLog.Message($"[RimPrison:parole] {inmate.LabelShort} sits before the board");
        }

        /// <summary>Is a board member needed at a desk now: somebody is on their way to the room or in it.</summary>
        public static bool BoardNeeded(PrisonGame game)
        {
            foreach (ParoleCase hc in game.story.paroleCases)
                if (hc.stage == HearingStage.Called || hc.stage == HearingStage.Seated) return true;
            return false;
        }

        // ---- what the board has before it ---------------------------------------------------------------------------

        /// <summary>
        /// Weighs the hearing: the odds from the file as the warden reads it (record, programs, work, tension), then who spoke and the reoffend band. All of
        /// it is on the letter; none of it is hidden (a gang, an innocence claim, a secret goal do not enter).
        /// </summary>
        public static void Assess(Map map, Pawn p, CompInmate c, ParoleCase hc)
        {
            var speaksFor = new List<string>();
            var speaksAgainst = new List<string>();
            var reasons = new List<string>();
            float delta = 0f;
            List<Pawn> staff = StaffCommute.AllStaff(map);
            // The officer who knows them: the one on staff who trusts them most.
            foreach (StaffMemory m in c.staffMemories.OrderByDescending(x => x.score))
            {
                if (m.score < StaffMemories.TrustedAt) break;
                Pawn officer = staff.Find(x => x.thingIDNumber == m.staffId);
                if (officer == null) continue;
                delta += OfficerFor;
                speaksFor.Add("RP_ParoleFor_Officer".Translate(officer.LabelShort, Signed(OfficerFor)));
                break;
            }
            if (c.religious && staff.Find(x => x.GetComp<CompStaff>()?.role == RPStaffRoleDefOf.RP_Chaplain) is { } chaplain)
            {
                float weight = FaithUtility.Guided(p) ? ChaplainGuidedFor : ChaplainFor;
                delta += weight;
                speaksFor.Add("RP_ParoleFor_Chaplain".Translate(chaplain.LabelShort, Signed(weight)));
            }
            if (Mentors.MentorOf(p, c) is { } mentor)
            {
                delta += MentorFor;
                speaksFor.Add("RP_ParoleFor_Mentor".Translate(mentor.LabelShort, Signed(MentorFor)));
            }
            if (WantWorker_ProveWrong.Proved(c))
            {
                delta += ProvedFor;
                speaksFor.Add("RP_ParoleFor_Proved".Translate(Signed(ProvedFor)));
            }
            // The victim's family, after a restorative meeting (see Amends): one that left hostile writes against them for certain, one that made peace withdraws its letter, and may speak for them.
            if (c.crime is { violent: true })
            {
                if (Amends.Hostile(c)) (hc.victimLetter, hc.victimWithdrawn) = (true, false);
                else if (hc.victimLetter && Amends.Made(c)) hc.victimWithdrawn = true;
            }
            bool victim = hc.victimLetter && !hc.victimWithdrawn && c.crime is { violent: true };
            if (victim)
            {
                delta -= VictimAgainst;
                speaksAgainst.Add("RP_ParoleAgainst_Victim".Translate(Signed(-VictimAgainst)));
            }
            else if (hc.victimLetter && hc.victimWithdrawn && c.crime is { violent: true }) speaksAgainst.Add("RP_ParoleAgainst_VictimWithdrawn".Translate());
            if (Amends.Supports(c) && c.crime is { violent: true })
            {
                delta += VictimFor;
                speaksFor.Add("RP_ParoleFor_Victim".Translate(Signed(VictimFor)));
            }
            float risk = ParoleSystem.ReoffendChance(c);
            delta += BandDelta(risk);
            // What counts against them, in the words of the file (only what the warden can see: the serious incidents that were seen).
            int now = Now;
            if (c.records.Exists(r => r.seen && now - r.tick < GenDate.TicksPerDay * WantWorker_Parole.RecentDays && r.def.severity >= 2)) reasons.Add("RP_ParoleWhy_Record".Translate());
            if (DrugTesting.FailedRecently(c)) reasons.Add("RP_ParoleWhy_Drugs".Translate());
            if (c.programsPassed == 0) reasons.Add("RP_ParoleWhy_NoPrograms".Translate());
            if (c.hoursWorked < 100) reasons.Add("RP_ParoleWhy_NoWork".Translate());
            if (c.tension > 50f) reasons.Add("RP_ParoleWhy_Tension".Translate());
            if (victim) reasons.Add("RP_ParoleWhy_Victim".Translate());
            if (risk >= MediumRisk) reasons.Add("RP_ParoleWhy_Risk".Translate());
            if (speaksFor.Count == 0) reasons.Add("RP_ParoleWhy_NoSpeaker".Translate());
            hc.risk = risk;
            hc.chance = Mathf.Clamp(ParoleSystem.ParoleChance(c) + delta, 0f, MaxChance);
            hc.forText = speaksFor.Count > 0 ? string.Join("; ", speaksFor) : "RP_ParoleNobody".Translate().ToString();
            hc.againstText = speaksAgainst.Count > 0 ? string.Join("; ", speaksAgainst) : "RP_ParoleNobody".Translate().ToString();
            hc.reasons = string.Join("|", reasons);
            hc.assessed = true;
        }

        /// <summary>The hearing is over: the board has what it needs. The closest call goes to the warden as a letter; the rest are decided now.</summary>
        public static void Conclude(PrisonGame game, Map map, ParoleCase hc, Pawn p, Pawn board)
        {
            hc.boardName = board?.LabelShort;
            Assess(map, p, p.Inmate(), hc);
            game.stats.paroleHearings++;
            if (AutoTest.Active) RPLog.Message($"[RimPrison:parole] hearing of {p.LabelShort}: board {hc.boardName ?? "the warden's chair"}, for [{hc.forText}], against [{hc.againstText}], risk {hc.risk:P0}, odds {hc.chance:P0}");
            if (hc.askWarden && !StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_Parole")))
            {
                hc.stage = HearingStage.Held;
                hc.heldTick = Now;
                ParoleHearings.AskWardenAfterSitting(game, map, p, hc);
                return;
            }
            Verdict(game, hc, p, 0);
        }

        /// <summary>
        /// The board's decision, with the warden's word (+1 recommend, -1 oppose, 0 none). Whatever has become of the inmate since the sitting counts: one who is
        /// downed, in a mental state, locked down or confined, or away, is not decided on (the hearing ends without a verdict), and a serious incident seen since the
        /// sitting is weighed again (the odds the letter stated are the most the board will go to).
        /// </summary>
        public static void Verdict(PrisonGame game, ParoleCase hc, Pawn p, int word)
        {
            game.story.paroleCases.Remove(hc);
            if (p == null) return;
            CompInmate c = p.Inmate();
            if (NotNow(game, p, c) is { } off)
            {
                CallOff(game, hc, p, off);
                return;
            }
            if (hc.assessed && hc.heldTick >= 0 && c.records.Exists(r => r.seen && r.tick >= hc.heldTick && r.def.severity >= 2))
            {
                float stated = hc.chance;
                Assess(p.Map, p, c, hc);
                hc.chance = Mathf.Min(stated, hc.chance);
            }
            bool paroled = ParoleHearings.Hold(game, p, word, hc);
            if (paroled) Messages.Message("RP_ParoleGranted".Translate(p.LabelShort), p, MessageTypeDefOf.PositiveEvent);
            else Messages.Message("RP_ParoleRefusedWhy".Translate(p.LabelShort, p.Inmate().paroleDenial), p, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>A refusal, written to the file: the reasons the warden could see (and the warden's own opposition), with the day. Returns the reasons in words.</summary>
        public static string NoteDenial(Pawn p, CompInmate c, ParoleCase hc, int word)
        {
            var why = new List<string>();
            if (!string.IsNullOrEmpty(hc.reasons)) why.AddRange(hc.reasons.Split('|'));
            if (word < 0) why.Add("RP_ParoleWhy_Warden".Translate());
            if (why.Count == 0) why.Add("RP_ParoleWhy_Luck".Translate());
            c.paroleDenial = string.Join(", ", why);
            c.paroleDenialTick = Now;
            return c.paroleDenial;
        }

        /// <summary>Everything saved about a case in one string (the saveload plan compares it).</summary>
        public static string Describe(ParoleCase hc) =>
            $"{hc.pawnId}:{hc.name}:{hc.stage}:{hc.scheduledTick}:{hc.calledTick}:{hc.seatedTick}:{hc.heldTick}:{hc.escortId}:{hc.escorted}{hc.askWarden}{hc.victimLetter}{hc.victimWithdrawn}{hc.assessed}:{hc.escortName}:{hc.boardName}:{hc.forText}:{hc.againstText}:{hc.reasons}:{hc.chance:F3}:{hc.risk:F3}";

        /// <summary>For the file: where their hearing stands, if it is on.</summary>
        public static string FileLine(Pawn p)
        {
            ParoleCase hc = CaseOf(p);
            return hc == null ? null : ("RP_FileStatusHearing_" + hc.stage).Translate().ToString();
        }
    }

    /// <summary>A called inmate is walked in: at the escort's side, the hearing room's chair, the board.</summary>
    public class JobDriver_AttendHearing : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            Toil go = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.OnCell);
            go.FailOn(() => ParoleScene.CaseOf(pawn) is not { stage: HearingStage.Called or HearingStage.Seated });
            // Walked in by a guard, they walk while the guard does: with the escort gone, they stop (unless they may go alone).
            go.FailOn(() => ParoleScene.CaseOf(pawn) is { stage: HearingStage.Called, escorted: false } hc && !ParoleScene.MayGoAlone(hc, pawn.Inmate()));
            yield return go;
            Toil sit = Toils_General.Wait(GenDate.TicksPerHour * 3);
            sit.socialMode = RandomSocialMode.Off;
            Action wait = sit.initAction;
            sit.initAction = () =>
            {
                wait?.Invoke();
                ParoleScene.Notify_Seated(pawn);
            };
            // The sitting ends when the board has decided or called off the hearing (the game's own beat concludes it, not this job). (The end conditions are
            // first checked as the toil starts, before it has seated them: the case is still Called then.)
            sit.AddEndCondition(() => ParoleScene.CaseOf(pawn) is { stage: HearingStage.Called or HearingStage.Seated } ? JobCondition.Ongoing : JobCondition.Succeeded);
            yield return sit;
        }
    }

    /// <summary>The guard collects the inmate and walks them to the hearing room, and waits there until the hearing is over.</summary>
    public class JobDriver_EscortToHearing : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            AddFinishAction(_ => ParoleScene.Notify_EscortEnded(Inmate, pawn));
            Toil meet = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            meet.FailOn(() => ParoleScene.CaseOf(Inmate) is not { stage: HearingStage.Called });
            yield return meet;
            yield return Toils_General.Do(() => ParoleScene.Notify_Escorted(Inmate, pawn));
            // (After a load the walk is not in the inmate's record: every beat of the escort makes it again while the case is this guard's, see ParoleScene.HoldAfterLoad.)
            Toil lead = Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            lead.tickIntervalAction = delta => ParoleScene.HoldAfterLoad(Inmate, pawn);
            yield return lead;
            Toil stand = Toils_General.Wait(GenDate.TicksPerHour * 2);
            stand.tickIntervalAction = delta => ParoleScene.HoldAfterLoad(Inmate, pawn);
            // (Over when the case is, or when it is no longer this guard's: the inmate got up and left their chair, and the case is called again for another escort. Holding on would keep the pass on a
            // walk home that is not this guard's.)
            stand.AddEndCondition(() => ParoleScene.CaseOf(Inmate) is { stage: HearingStage.Called or HearingStage.Seated, escorted: true } hc && hc.escortId == pawn.thingIDNumber ? JobCondition.Ongoing : JobCondition.Succeeded);
            yield return stand;
        }
    }

    /// <summary>Guards walk called inmates to the hearing room (an inmate waiting for an escort is the first call on a free guard, after an alarm).</summary>
    public class WorkGiver_EscortToHearing : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || PrisonGame.Instance == null || PrisonGame.Instance.story.paroleCases.Count == 0;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => ParoleScene.ToEscort(pawn.Map);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && ParoleScene.NeedsEscort(inmate) && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly, 1, -1, null, forced)
            && PrisonMap.Of(pawn.Map) is { } pm && HearingRooms.PostFor(pawn, pm).IsValid;

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            IntVec3 post = HearingRooms.PostFor(pawn, PrisonMap.Of(pawn.Map));
            if (!post.IsValid) return null;
            Job job = JobMaker.MakeJob(RPParoleDefOf.RP_EscortToHearing, t, post);
            job.count = 1;
            job.locomotionUrgency = LocomotionUrgency.Walk;
            return job;
        }
    }

    /// <summary>An administrator takes the board's seat at the desk in the hearing room while an inmate is on the way or before the board.</summary>
    public class WorkGiver_SitBoard : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(RPAdminDefOf.RP_OfficeDesk);

        public override PathEndMode PathEndMode => PathEndMode.InteractionCell;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => PrisonGame.Instance == null || !ParoleScene.BoardNeeded(PrisonGame.Instance);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t.Faction == pawn.Faction && !t.IsBurning() && PrisonMap.Of(pawn.Map)?.zones.ZoneAt(t.Position) == RPParoleDefOf.RP_HearingRoom
            && pawn.CanReserveAndReach(t, PathEndMode.InteractionCell, Danger.Some, 1, -1, null, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPParoleDefOf.RP_SitBoard, t);
    }

    /// <summary>At the desk until nobody is left to hear (a short while after the last inmate), two hours at the most.</summary>
    public class JobDriver_SitBoard : JobDriver
    {
        private int idleTicks;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            Toil sit = Toils_General.Wait(GenDate.TicksPerHour * 2);
            sit.handlingFacing = true;
            sit.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(job.targetA);
                idleTicks = ParoleScene.BoardNeeded(PrisonGame.Instance) ? 0 : idleTicks + delta;
                if (idleTicks >= 600) ReadyForNextToil();
            };
            yield return sit;
        }
    }
}
