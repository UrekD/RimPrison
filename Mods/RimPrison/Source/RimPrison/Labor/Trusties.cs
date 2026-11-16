using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPTrustyDefOf
    {
        public static PrisonZoneDef RP_OutsideWork;
        public static InmateJobDef RP_JobOutside;
        public static LedgerCategoryDef RP_GroundsContract;
        public static SecurityLevelDef RP_Minimum;

        static RPTrustyDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPTrustyDefOf));
    }

    /// <summary>Why the outside work party is not out (or that it is).</summary>
    public enum PartyState : byte
    {
        /// <summary>No trusty holds the outside job: nothing to run.</summary>
        Idle,
        Out,
        /// <summary>No zone, or none of it in the open.</summary>
        NoZone,
        Riot,
        Hostage,
        /// <summary>Hostile pawns that threaten the colony are on the map (a raid, manhunters).</summary>
        Raid,
        Evacuation,
        Lockdown,
        /// <summary>An escape somebody has seen is on.</summary>
        Escape,
        /// <summary>Too few guards near the zone for the trusties holding the job.</summary>
        Short,
        /// <summary>Called in, and held in for an hour.</summary>
        Hold,
    }

    /// <summary>The outside work party of a map: the warden's guard ratio, the day's credited hours (booked at midnight as the grounds contract) and where the party stands. See <see cref="Trusties"/>.</summary>
    public class TrustyParty : IExposable
    {
        /// <summary>One guard near the zone for this many trusties (<see cref="Trusties.MinRatio"/> to <see cref="Trusties.MaxRatio"/>).</summary>
        public int ratio = Trusties.DefaultRatio;
        /// <summary>Trusty-hours worked in the zone since the last booking.</summary>
        public int hours;
        /// <summary>The party is not sent out again before this tick (-1: no hold): it was called in.</summary>
        public int holdUntilTick = -1;

        /// <summary>The party is out: the trusties on the outside job may go to the zone in their work hours. Saved with <see cref="shortSinceTick"/>, so a load in the middle of a shortfall's hour of grace goes on with it.</summary>
        public bool isOut;
        public PartyState state;
        // Worked out by the map's beat (see Trusties.Beat); a load starts them over, and the first beat sets them.
        /// <summary>Trusties on the job, guards near, and guards the job needs (for the Deployment tab).</summary>
        public int holders, trusties, near, needed;
        /// <summary>Since when the party has been short of guards while out (-1: it is not).</summary>
        public int shortSinceTick = -1;
        /// <summary>The hour of the last hourly pass (-1: none since the load).</summary>
        public int lastHour = -1;
        /// <summary>Pass holders out in the open who cannot reach their beds (thing ids): a guard is asked to walk them in.</summary>
        public readonly HashSet<int> stranded = new HashSet<int>();
        /// <summary>Pass holders out in the open behind a locked gate (thing ids): no guard can walk them in, the warden is told (once a day).</summary>
        public readonly HashSet<int> lockedOut = new HashSet<int>();
        /// <summary>Who the warden was told about today (thing ids), and the day of it. Not saved: a load tells again, once.</summary>
        public readonly HashSet<int> toldToday = new HashSet<int>();
        public int toldDay = -1;
        /// <summary>The open guard posts near the zone, as of the last beat the party was short (for the tab's line).</summary>
        public int postsNear;
        /// <summary>The zone's cells' bounds and the cells within the supervision radius of them, as of <see cref="geoVersion"/>.</summary>
        public CellRect nearRect;
        public int geoVersion = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref ratio, "ratio", Trusties.DefaultRatio);
            Scribe_Values.Look(ref hours, "hours");
            Scribe_Values.Look(ref holdUntilTick, "holdUntil", -1);
            Scribe_Values.Look(ref isOut, "isOut");
            Scribe_Values.Look(ref shortSinceTick, "shortSince", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) ratio = Mathf.Clamp(ratio, Trusties.MinRatio, Trusties.MaxRatio);
        }
    }

    /// <summary>
    /// Trusties and the outside work party. A minimum-security inmate with a high conduct grade can be made a trusty by the warden (<see cref="CanGrant"/>): they work in an outside work zone beyond the wall
    /// (<see cref="PrisonZoneDef.outsideWork"/>), up to a gate, in their work hours, while enough guards stand near it (the party, see <see cref="Beat"/>). Only a trusty holds the outside job
    /// (<see cref="MayHoldOutsideJob"/>); the status goes with a seen serious misconduct, any escape, a change of level or the stay's end (<see cref="Revoke"/>). The gate is an Auto door that the
    /// door pass names (<see cref="PrisonMap.ComputeInmatePass"/>); the trusty's pass is worked out in <see cref="CompInmate.UpdateWorkPass"/>.
    /// </summary>
    public static class Trusties
    {
        /// <summary>The conduct grade a trusty needs to be made one (0-5, see <see cref="CellQuality"/>), the days without a seen serious misconduct, and the tension up to which they are made.</summary>
        public const int MinGrade = 4, SevereDays = 10;
        public const float MaxGrantTension = 40f;
        public const int MinRatio = 2, MaxRatio = 4, DefaultRatio = 3;
        /// <summary>Dollars the state pays for a trusty-hour in the zone (the grounds contract).</summary>
        public const int PayPerHour = 6;
        /// <summary>The party is not sent out again for an hour after it was called in; a shortfall of guards calls it in after an hour of it.</summary>
        public const int HoldTicks = GenDate.TicksPerHour, ShortTicks = GenDate.TicksPerHour;
        /// <summary>A guard is near a trusty (or the zone) within this many cells (the sight of a guard, <see cref="TensionUtility.GuardRadius"/>).</summary>
        public const float SupervisionRadius = TensionUtility.GuardRadius;
        /// <summary>A trusty this near the zone is still at their work (see <see cref="Exempt"/>).</summary>
        public const int ExemptRadius = 3;
        /// <summary>One work place per this many zone cells.</summary>
        public const int CellsPerPlace = 30;
        /// <summary>The walk-off chance per hour, before the factors (see <see cref="WalkOffChance"/>).</summary>
        public const float WalkOffBase = 0.0008f;

        /// <summary>For the checks: the walk-off chance is this when set (a roll pinned to 0 or 1).</summary>
        internal static float? WalkOffOverride = null;
        /// <summary>For the checks: whether a hostile threat is on the map, instead of asking the game (the events plan's raids come and go).</summary>
        internal static bool? ThreatForTests = null;
        /// <summary>For the checks: when set, only these pawns' escapes count as one somebody has seen (the events plan's own runaways must not call a scene's party in under it).</summary>
        internal static HashSet<Pawn> EscapeScope = null;
        /// <summary>For the checks: how many times the exemption was asked and answered yes at the escape rule, and how many walk-offs were rolled.</summary>
        internal static int exemptHits, walkOffRolls;
        /// <summary>For the checks: how many times the warden was told that one is outside behind a shut gate.</summary>
        internal static int lockedOutTold;

        private static readonly List<Pawn> trusties = new List<Pawn>(), holders = new List<Pawn>(), passHolders = new List<Pawn>();
        private static readonly HashSet<int> counted = new HashSet<int>();

        public static bool IsTrusty(Pawn p) => p?.Inmate() is { IsTrusty: true };

        /// <summary>The one rule for the outside job: only a trusty may hold it, locked by the warden or not.</summary>
        public static bool MayHoldOutsideJob(CompInmate c) => c is { IsTrusty: true };

        /// <summary>May this inmate hold this job (every job but the outside one: yes).</summary>
        public static bool MayHold(CompInmate c, InmateJobDef job) => job != RPTrustyDefOf.RP_JobOutside || MayHoldOutsideJob(c);

        public static bool PartyOut(Map map) => PrisonMap.Of(map)?.party is { isOut: true };

        // ---- the status -------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Whether the warden can make them a trusty now, and when not why not (the first reason).</summary>
        public static bool CanGrant(Pawn p, out string why)
        {
            why = null;
            CompInmate c = p?.Inmate();
            int now = Find.TickManager.TicksGame;
            if (c?.security == null || !InmateUtility.IsActiveInmate(p) || ShuttleSystem.OnTheWayOut(p)) why = "RP_TrustyNo_NotHere".Translate(p?.LabelShort ?? "?").Resolve();
            else if (c.IsTrusty) why = "RP_TrustyNo_Already".Translate(p.LabelShort).Resolve();
            else if (c.security != RPTrustyDefOf.RP_Minimum) why = "RP_TrustyNo_Level".Translate(p.LabelShort, RPTrustyDefOf.RP_Minimum.label).Resolve();
            else if (c.entitlement < MinGrade) why = "RP_TrustyNo_Grade".Translate(p.LabelShort, c.entitlement, MinGrade).Resolve();
            else if (CellQuality.RecentSevere(c, now, SevereDays) is { } severe) why = "RP_TrustyNo_Misconduct".Translate(p.LabelShort, severe.def.label, SevereDays).Resolve();
            else if (c.tension > MaxGrantTension) why = "RP_TrustyNo_Tension".Translate(p.LabelShort, Mathf.RoundToInt(c.tension), Mathf.RoundToInt(MaxGrantTension)).Resolve();
            else if (WatchList.OnList(c)) why = "RP_TrustyNo_WatchList".Translate(p.LabelShort).Resolve();
            else if (p.InMentalState) why = "RP_TrustyNo_Mental".Translate(p.LabelShort).Resolve();
            else if (c.forcedActivity != null) why = "RP_TrustyNo_Confined".Translate(p.LabelShort).Resolve();
            return why == null;
        }

        /// <summary>The warden makes them a trusty. The job is the daily pass's (or the warden's job menu's) to give.</summary>
        public static bool Grant(Pawn p)
        {
            if (!CanGrant(p, out string why))
            {
                if (why != null) Messages.Message(why, p, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            p.Inmate().trustySinceTick = Mathf.Max(0, Find.TickManager.TicksGame);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_TrustyMade".Translate(p.LabelShort), p);
            Messages.Message("RP_TrustyMade".Translate(p.LabelShort), p, MessageTypeDefOf.PositiveEvent, historical: false);
            return true;
        }

        /// <summary>
        /// The status ends. The outside job goes with it (locked or not) and the inmate is sent to look for what the regime has for them now (queued: this is called from job drivers). The work pass is not
        /// taken at once, so one standing in the zone can walk back in (see <see cref="CompInmate.UpdateWorkPass"/>). On one who is no trusty it only drops an outside job, silently.
        /// </summary>
        public static void Revoke(Pawn p, string reason, bool silent)
        {
            CompInmate c = p?.Inmate();
            if (c == null) return;
            bool was = c.IsTrusty, hadJob = c.job == RPTrustyDefOf.RP_JobOutside;
            if (hadJob)
            {
                c.job = null;
                c.jobLocked = false;
            }
            if (was)
            {
                c.trustySinceTick = -1;
                if (!silent)
                {
                    Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_TrustyLost".Translate(p.LabelShort, reason), p);
                    Messages.Message("RP_TrustyLost".Translate(p.LabelShort, reason), p, MessageTypeDefOf.NegativeEvent, historical: false);
                }
            }
            if (hadJob || c.outsidePass) Interrupt(p);
        }

        /// <summary>Sent to look for something to do now, in the next few ticks (never ends a job on the spot: the callers are job drivers).</summary>
        private static void Interrupt(Pawn p)
        {
            if (p.Spawned && !p.Dead && p.Map != null) PrisonMap.Of(p.Map)?.QueueRegimeInterrupt(p);
        }

        /// <summary>The stay is over (released, transferred, dead, escaped off the map): the status and the job go quietly (see <see cref="StorySystem.Notify_Leaving"/>).</summary>
        public static void Notify_Leaving(Pawn p)
        {
            if (p?.Inmate() is not { } c) return;
            Revoke(p, null, silent: true);
            DropOutsidePass(p, c);
        }

        /// <summary>An escape begins (any: a walk-off, a misconduct, a tunnel, a stowaway, the breach rule): the status and the job go without a word (the escape's own message, or the count's letter, tells it) and the outside pass ends on the spot, before any pass is worked out again.</summary>
        public static void Notify_EscapeStarted(Pawn p)
        {
            if (p?.Inmate() is not { } c) return;
            Revoke(p, null, silent: true);
            DropOutsidePass(p, c);
        }

        /// <summary>An escape's start or a stay's end: whatever the inmate held of the outside is not theirs, and the pass is gone on the spot. (A reclassification or the warden's own button only revoke: the one standing outside keeps the way back.)</summary>
        private static void DropOutsidePass(Pawn p, CompInmate c)
        {
            if (!c.outsidePass && c.workPass != RPTrustyDefOf.RP_OutsideWork) return;
            c.outsidePass = false;
            if (c.workPass == RPTrustyDefOf.RP_OutsideWork) c.workPass = null;
            if (p.Spawned) PrisonMap.Of(p.Map)?.Notify_WorkPassChanged(p);
        }

        // ---- the gate and the pass ----------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The inmate's side of the gate is space the minimum level may use: an unzoned corridor or a room of a kind inmates share, not holding, the visiting room, a yard for one level alone or another level's wing, and not a patch kept
        /// from the level (see <see cref="ZoneGrid.SetKept"/>).
        /// </summary>
        public static bool GateInmateSide(PrisonMap pm, PrisonZoneDef zone, IntVec3 cell)
        {
            if (zone != null && (zone == RPDefOf.RP_Holding || zone == RPVisitDefOf.RP_Visitation || zone.yardFor != null || zone.security != null && zone.security != RPTrustyDefOf.RP_Minimum || zone.staffOnly)) return false;
            return pm.zones.Allows(cell, RPTrustyDefOf.RP_Minimum);
        }

        /// <summary>
        /// The return token of the outside pass, worked out at every look (<see cref="CompInmate.UpdateWorkPass"/>): given to a trusty on the outside job in their work hours while the party is out (never to anyone else, standing where
        /// they like), and kept by one who holds it while they stand out in the open on owned ground, so the way back through the gate is theirs (a revoked trusty, a job changed, a party called in, a zone erased). It ends at their
        /// first look back inside, at an escape's start (<see cref="Notify_EscapeStarted"/>) and with the stay. A mental state does not end it: the pass is shut during the state (<see cref="HoldsPass"/>) and comes back after.
        /// </summary>
        internal static bool WantsOutsideToken(Pawn p, CompInmate c, PrisonMap pm)
        {
            if (!p.InMentalState && c.IsTrusty && c.forcedActivity == null && c.job == RPTrustyDefOf.RP_JobOutside && pm.party.isOut && p.Spawned && PrisonGrounds.Allows(p.Map, p.Position)
                && PrisonGame.Instance.CurrentActivity(p) == RPLaborDefOf.RP_Work) return true;
            return c.outsidePass && OnOutsideGround(p, pm);
        }

        /// <summary>Out in the open on owned ground: where the return token is kept, and where a pass holder is under the trusty's rule for a walk-off (<see cref="Exempt"/>).</summary>
        private static bool OnOutsideGround(Pawn p, PrisonMap pm) => p.Spawned && pm.IsOutsideSpace(p.Position) && PrisonGrounds.Allows(p.Map, p.Position);

        /// <summary>The token opens the gate: not while the pawn is in a mental state.</summary>
        internal static bool HoldsPass(Pawn p, CompInmate c) => c.outsidePass && !p.InMentalState;

        /// <summary>A mental state begins: the gate is shut to the pawn on the spot (the token stays: the pass is theirs again when it ends, see <see cref="WantsOutsideToken"/>). An escape has dropped the token already.</summary>
        internal static void Notify_MentalStateStarted(Pawn p)
        {
            if (p?.Inmate() is not { activeFlag: true } c || c.workPass != RPTrustyDefOf.RP_OutsideWork) return;
            c.workPass = null;
            if (p.Spawned) PrisonMap.Of(p.Map)?.Notify_WorkPassChanged(p);
        }

        /// <summary>
        /// Out in the open on a trusty's account, so the old rule for a breach does not apply (see <see cref="EscapeUtility.HourlyCheck"/>): not in a mental state, and either holding the outside pass (the return token)
        /// or a trusty standing in the zone or within <see cref="ExemptRadius"/> cells of it - both out in the open on owned ground, where the pass may be held (a trusty in the wing beside the zone, or far off the
        /// grounds, keeps the old rule). Anyone else keeps it too.
        /// </summary>
        public static bool Exempt(Pawn p, CompInmate c)
        {
            if (p.InMentalState || !p.Spawned || PrisonMap.Of(p.Map) is not { } pm || !OnOutsideGround(p, pm)) return false;
            if (c.outsidePass) return true;
            if (!c.IsTrusty || !pm.zones.Any(RPTrustyDefOf.RP_OutsideWork)) return false;
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(p.Position, ExemptRadius, true))
                if (cell.InBounds(p.Map) && pm.zones.ZoneAt(cell) == RPTrustyDefOf.RP_OutsideWork) return true;
            return false;
        }

        /// <summary>The hourly check of one the exemption covers: they may walk off, at the odds of <see cref="WalkOffChance"/>.</summary>
        internal static void RollWalkOff(Pawn p, CompInmate c)
        {
            exemptHits++;
            PrisonMap pm = PrisonMap.Of(p.Map);
            if (pm == null) return;
            walkOffRolls++;
            if (Rand.Chance(WalkOffChance(p, c, pm))) CountSystem.StartEscaping(p, "RP_TrustyWalkedOff".Translate(), EscapeWay.Walked);
        }

        /// <summary>
        /// The chance in an hour that a trusty out on the work walks off: 0.08% for a calm one with a guard in sight, times (1 + tension/50), twice in the dark, twice for an escape artist, a quarter for a model prisoner, six times with no
        /// guard in sight, three times while the party is called in and they linger. About 0.4% a day for a calm, watched trusty in five work hours.
        /// </summary>
        public static float WalkOffChance(Pawn p, CompInmate c, PrisonMap pm)
        {
            if (WalkOffOverride is { } forced) return forced;
            float chance = WalkOffBase * (1f + c.tension / 50f);
            if (TensionUtility.Dark(p.Position, p.Map, pm)) chance *= 2f;
            if (c.HasReputation(ReputationDefOf.RP_EscapeArtist)) chance *= 2f;
            if (c.HasReputation(ReputationDefOf.RP_ModelPrisoner)) chance *= 0.25f;
            if (TensionUtility.GuardsNear(p, pm) == 0) chance *= 6f;
            if (!pm.party.isOut) chance *= 3f;
            return chance;
        }

        // ---- stranded ------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Out in the open with the outside pass and no way back to the bed (the zone erased, the gate rebuilt, or set Staff only): a guard is asked to walk them in (see <see cref="SupermaxSystem.DueFor"/>). A gate that is Locked stays locked for the escort too: the warden is told instead (see <see cref="FindStranded"/>).</summary>
        public static bool Stranded(Pawn p) => p.Map != null && p.Inmate() is { outsidePass: true } && PrisonMap.Of(p.Map)?.party.stranded.Contains(p.thingIDNumber) == true;

        /// <summary>Out in the open and cannot get back by themselves: stranded (a guard walks them in) or behind a locked gate (the warden is told). Not out of place for the count.</summary>
        public static bool AwaitingWayBack(Pawn p) => p.Map != null && p.Inmate() is { outsidePass: true } && PrisonMap.Of(p.Map)?.party is { } party && (party.stranded.Contains(p.thingIDNumber) || party.lockedOut.Contains(p.thingIDNumber));

        // ---- the party ----------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The party, once a beat (250 ticks) per map. Out when nothing calls it in: no riot, hostage crisis, raid (a hostile threat on the map), evacuation, lockdown of the minimum wing or escape somebody has seen, a zone in the open, and enough guards
        /// near it for the trusties on the job (<see cref="Supervisors"/>; the party is called in only after an hour of a shortfall, and is not sent out again for an hour after it was called in). Hours are the inmates' own work slots.
        /// A flip gives or takes the pass of the trusties at once and sends them to look for something to do; once an hour the running jobs are checked (<see cref="JobInZone"/>), and the pass holders who cannot get back are found.
        /// </summary>
        internal static void Beat(Map map, PrisonMap pm)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null) return;
            TrustyParty party = pm.party;
            int now = Find.TickManager.TicksGame;
            trusties.Clear();
            holders.Clear();
            passHolders.Clear();
            wingTargets.Clear();
            bool riot = false, escape = false;
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn p = prisoners[i];
                MentalStateDef mental = p.MentalStateDef;
                if (mental == RPMentalStateDefOf.RP_Rioting) riot = true;
                else if (mental == RPMentalStateDefOf.RP_Escaping && !CountSystem.IsHidden(p) && (EscapeScope == null || EscapeScope.Contains(p))) escape = true;
                if (p.Inmate() is not { activeFlag: true } c) continue;
                if (c.IsTrusty)
                {
                    trusties.Add(p);
                    if (c.job == RPTrustyDefOf.RP_JobOutside) holders.Add(p);
                }
                if (c.outsidePass) passHolders.Add(p);
            }
            party.trusties = trusties.Count;
            party.holders = holders.Count;
            List<IntVec3> zoneCells = pm.zones.CellsOf(RPTrustyDefOf.RP_OutsideWork);
            party.near = 0;
            party.needed = 0;
            PartyState state;
            if (holders.Count == 0) state = PartyState.Idle;
            else
            {
                int usable = UsableCells(map, zoneCells);
                party.needed = Mathf.CeilToInt(holders.Count / (float)party.ratio);
                party.near = usable == 0 ? 0 : Supervisors(map, pm, party, zoneCells);
                bool isShort = party.near * party.ratio < holders.Count;
                if (usable == 0) state = PartyState.NoZone;
                else if (riot) state = PartyState.Riot;
                else if (HostageCrisis.Running(map) != null) state = PartyState.Hostage;
                else if (ThreatForTests ?? GenHostility.AnyHostileActiveThreatToPlayer(map)) state = PartyState.Raid;
                else if (EvacuationSystem.Running(map)) state = PartyState.Evacuation;
                else if (game.LevelLockedDown(RPTrustyDefOf.RP_Minimum)) state = PartyState.Lockdown;
                else if (escape) state = PartyState.Escape;
                else if (isShort)
                {
                    // (Out already: one hour of a shortfall before they are called in.)
                    if (party.isOut)
                    {
                        if (party.shortSinceTick < 0) party.shortSinceTick = now;
                        state = now - party.shortSinceTick >= ShortTicks ? PartyState.Short : PartyState.Out;
                    }
                    else state = PartyState.Short;
                }
                else
                {
                    party.shortSinceTick = -1;
                    state = party.isOut || now >= party.holdUntilTick ? PartyState.Out : PartyState.Hold;
                }
            }
            party.state = state;
            party.postsNear = state == PartyState.Short ? PostsNearZone(pm) : 0;
            bool nowOut = state == PartyState.Out;
            if (nowOut != party.isOut)
            {
                party.isOut = nowOut;
                party.shortSinceTick = -1;
                // (Held in for an hour after a recall for a reason: not when the last holder's job was only changed.)
                if (!nowOut && state != PartyState.Idle) party.holdUntilTick = now + HoldTicks;
                // Gives or takes the pass of the trusties on the job now (none can leave in the lag before their own look), and sends them to look for what the regime has for them: out to the zone, or back to the cell.
                for (int i = 0; i < holders.Count; i++)
                {
                    holders[i].Inmate().UpdateWorkPass();
                    if (!nowOut || game.CurrentActivity(holders[i]) == RPLaborDefOf.RP_Work) Interrupt(holders[i]);
                }
                // (The ones who hold the pass and are no longer on the job (the status taken) look too: they are on their way in.)
                if (!nowOut)
                    for (int i = 0; i < passHolders.Count; i++)
                        if (!holders.Contains(passHolders[i])) passHolders[i].Inmate().UpdateWorkPass();
                if (AutoTest.Active) RPLog.Message($"[trusty] the party is {(nowOut ? "out" : "called in (" + state + ")")} on {map}: {holders.Count} on the job, {party.near} guards near");
            }
            FindStranded(pm, party);
            int hour = GenLocalDate.HourOfDay(map);
            if (hour != party.lastHour)
            {
                party.lastHour = hour;
                // (First beat after a load too: a job that was running across a save is looked at again.)
                for (int i = 0; i < holders.Count; i++)
                    if (holders[i].Spawned && holders[i].CurJob is { } job && !JobInZone(job, pm)) Interrupt(holders[i]);
            }
        }

        /// <summary>
        /// Pass holders out in the open who cannot reach their bed, or the wing when they have none (a gate rebuilt, an erased zone, a gate that is locked): a guard walks them in through the doors on the way (see
        /// <see cref="SupermaxSystem.DueFor"/>). A gate that is locked stays locked for everyone, guards' escort included: the holder behind it is not in the escort's list, and the warden is told once a day
        /// (<see cref="TellLockedOut"/>). Few, and only these are asked, every beat.
        /// </summary>
        private static void FindStranded(PrisonMap pm, TrustyParty party)
        {
            party.stranded.Clear();
            party.lockedOut.Clear();
            int day = GenDate.DaysPassed;
            if (party.toldDay != day)
            {
                party.toldDay = day;
                party.toldToday.Clear();
            }
            for (int i = 0; i < passHolders.Count; i++)
            {
                Pawn p = passHolders[i];
                CompInmate c = p.Inmate();
                if (!p.Spawned || p.Downed || p.InMentalState || c.leg != null || !pm.IsOutsideSpace(p.Position)) continue;
                if (p.ownership?.OwnedBed is { Spawned: true } bed ? p.CanReach(bed, PathEndMode.Touch, Danger.Deadly) : WingCell(p).IsValid) continue;
                CompPrisonDoor blocked = BlockedGate(pm, p);
                if (blocked == null) party.stranded.Add(p.thingIDNumber);
                else
                {
                    // (A staff-only gate is shut to the inmate, but a guard's escort opens it for the walk.)
                    if (blocked.mode == PrisonDoorMode.Locked) party.lockedOut.Add(p.thingIDNumber);
                    else party.stranded.Add(p.thingIDNumber);
                    // (Told when the work is not what keeps them outside: the party is in, the job is not the outside one, or the work slot is over - the party stays out through the evening and the night. Outside the gate at work, a shut gate is a thing for the evening.)
                    if (!AtTheOutsideWork(p, c, party)) TellLockedOut(p, blocked, party);
                }
            }
        }

        /// <summary>On the outside job in the work slot with the party out: what keeps a trusty on the strip is their work (between two cuts too, which is why this is not <see cref="IsWorkingOutside"/>).</summary>
        private static bool AtTheOutsideWork(Pawn p, CompInmate c, TrustyParty party) =>
            party.isOut && c.job == RPTrustyDefOf.RP_JobOutside && PrisonGame.Instance is { } game && game.CurrentActivity(p) == RPLaborDefOf.RP_Work;

        /// <summary>
        /// The gate that is shut to this pawn's way in, when there is one: of the gates of the zone that open onto the ground they stand on (the district they are in), a Locked one when none of them opens at all (no Auto gate, and no
        /// Staff-only one for a guard's escort to take), else a Staff-only one when none is Auto. Null when a gate of theirs opens (the way home is blocked for another reason, and the escort takes it) or none is theirs.
        /// A gate on the far side of the prison's grounds, in another district, has nothing to do with them.
        /// </summary>
        internal static CompPrisonDoor BlockedGate(PrisonMap pm, Pawn p)
        {
            District own = p.GetDistrict();
            CompPrisonDoor locked = null, staffOnly = null;
            foreach (CompPrisonDoor d in pm.Doors)
            {
                if (!d.trustyGate || !d.Door.Spawned || !Adjoins(d.Door, own, pm.map)) continue;
                switch (d.mode)
                {
                    case PrisonDoorMode.Locked:
                        locked ??= d;
                        break;
                    case PrisonDoorMode.StaffOnly:
                        staffOnly ??= d;
                        break;
                    default:
                        return null;
                }
            }
            return staffOnly ?? locked;
        }

        /// <summary>The door opens onto this district (a cell beside it is in it). No district for the pawn (standing in a doorway): every gate is theirs.</summary>
        private static bool Adjoins(Building_Door door, District district, Map map)
        {
            if (district == null) return true;
            IntVec3 at = door.Position;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 beside = at + GenAdj.CardinalDirections[i];
                if (beside.InBounds(map) && beside.GetDistrict(map) == district) return true;
            }
            return false;
        }

        /// <summary>The warden is told that one is outside the wall behind a shut gate: once a day for each.</summary>
        private static void TellLockedOut(Pawn p, CompPrisonDoor gate, TrustyParty party)
        {
            if (!party.toldToday.Add(p.thingIDNumber)) return;
            lockedOutTold++;
            Messages.Message("RP_TrustyLockedOut".Translate(p.LabelShort, ("RP_DoorMode_" + gate.mode).Translate().Resolve().ToLower()), p, MessageTypeDefOf.NegativeEvent, historical: false);
        }

        /// <summary>
        /// A cell of their level's wing in the prison that the pawn can walk to by themselves (the first one): what a pass holder with no bed has for a way home, so that one who cannot reach any is stranded. Invalid when there is
        /// none. It is no target for a guard's walk (see <see cref="WingTarget"/>): it says nothing of the walk with an escort, which opens the doors on its way.
        /// </summary>
        public static IntVec3 WingCell(Pawn p)
        {
            if (!p.Spawned || p.Inmate() is not { security: { } level } || UnitOfficers.WingZone(level) is not { } zone || PrisonMap.Of(p.Map) is not { } pm) return IntVec3.Invalid;
            List<IntVec3> cells = pm.zones.CellsOf(zone);
            for (int i = 0; i < cells.Count && i < WingAloneLooked; i++)
                if (cells[i].Standable(p.Map) && !pm.IsOutsideSpace(cells[i]) && p.CanReach(cells[i], PathEndMode.OnCell, Danger.Deadly)) return cells[i];
            return IntVec3.Invalid;
        }

        /// <summary>How many cells of the wing zone are looked at: for the first one a pass holder with no bed can reach alone (every beat they are outside), and for the nearest one a guard walks them to (when a guard is asked).</summary>
        private const int WingAloneLooked = 400, WingCellsLooked = 2000;

        private static readonly List<(int dist, IntVec3 cell)> wingPlain = new List<(int, IntVec3)>();

        /// <summary>What <see cref="WingTarget"/> answered this beat, by inmate and guard (thing ids; -1 for no guard): a look is a scan of up to <see cref="WingCellsLooked"/> cells and a path search for each candidate, asked for every stranded holder by every guard that scans. Emptied by the beat and by a repaint.</summary>
        private static readonly Dictionary<(int inmate, int guard), IntVec3> wingTargets = new Dictionary<(int, int), IntVec3>();
        private static int wingTargetsVersion = -1;

        /// <summary>For the checks: nothing of what <see cref="WingTarget"/> answered stands (the world a check changed between two looks).</summary>
        internal static void ForgetWingTargets() => wingTargets.Clear();

        /// <summary>A game was loaded or begun: nothing the last one's beats kept stands (the wing targets are by thing ids, which a reloaded save gives again to the same pawns, and the cells of the map it was asked on).</summary>
        public static void Reset()
        {
            wingTargets.Clear();
            wingTargetsVersion = -1;
            trusties.Clear();
            holders.Clear();
            passHolders.Clear();
            counted.Clear();
        }

        /// <summary>
        /// Where a guard walks a pass holder who has no bed: the cell of their level's wing, in the prison, nearest to them, that is not in a cell (a room with a bed in it: the guard's walk opens no other inmate's cell door, see
        /// <see cref="EscortWalk.PlanDoors"/>, so a cell is no place to bring them), and that the guard can reach when a guard is given. Not the inmate's own reach: one who is stranded cannot reach a cell by themselves, that is why
        /// a guard walks them (the doors on the guard's way open to them). Invalid when there is none. Asked once when the walk is planned: the leg keeps the cell (see <see cref="EscortWalk.HomeCell"/>), and the answer is kept for the beat.
        /// </summary>
        public static IntVec3 WingTarget(Pawn p, Pawn guard = null)
        {
            if (!p.Spawned || p.Inmate() is not { security: { } level } || UnitOfficers.WingZone(level) is not { } zone || PrisonMap.Of(p.Map) is not { } pm) return IntVec3.Invalid;
            // (A repaint is a new wing: nothing kept from before it stands.)
            if (wingTargetsVersion != pm.zones.Version)
            {
                wingTargetsVersion = pm.zones.Version;
                wingTargets.Clear();
            }
            (int, int) key = (p.thingIDNumber, guard?.thingIDNumber ?? -1);
            if (wingTargets.TryGetValue(key, out IntVec3 known)) return known;
            List<IntVec3> cells = pm.zones.CellsOf(zone);
            wingPlain.Clear();
            for (int i = 0; i < cells.Count && i < WingCellsLooked; i++)
            {
                IntVec3 c = cells[i];
                if (c.Standable(p.Map) && !pm.IsOutsideSpace(c) && c.GetRoom(p.Map) is not { IsPrisonCell: true }) wingPlain.Add(((c - p.Position).LengthHorizontalSquared, c));
            }
            wingPlain.Sort((a, b) => a.dist.CompareTo(b.dist));
            IntVec3 found = IntVec3.Invalid;
            for (int i = 0; i < wingPlain.Count && !found.IsValid; i++)
                if (guard == null || guard.CanReach(wingPlain[i].cell, PathEndMode.OnCell, Danger.Deadly)) found = wingPlain[i].cell;
            return wingTargets[key] = found;
        }

        /// <summary>One with no bed to go to who a guard is walking in (or is to): out in the open on the pass, or on the way back already - they are walked to their wing.</summary>
        public static bool WingBound(Pawn p) => p.Spawned && p.Inmate() is { security: not { escortOnly: true } } c && p.ownership?.OwnedBed is not { Spawned: true } && (c.outsidePass || c.leg is { kind: EscortKind.Home });

        /// <summary>One of those who a guard brought in: standing in the prison again, which is what a bed's room is for the others (see <see cref="EscortWalk.SeenIn"/>).</summary>
        public static bool BackInWithoutBed(Pawn p) => WingBound(p) && p.Inmate().leg is { kind: EscortKind.Home } && PrisonMap.Of(p.Map) is { } pm && !pm.IsOutsideSpace(p.Position);

        /// <summary>How many of the zone's cells are in the open (their district touches the map's edge): none, and the party stays in.</summary>
        private static int UsableCells(Map map, List<IntVec3> zoneCells)
        {
            int n = 0;
            for (int i = 0; i < zoneCells.Count; i++)
                if (zoneCells[i].GetDistrict(map) is { TouchesMapEdge: true }) n++;
            return n;
        }

        /// <summary>
        /// The guards who watch the work: on duty and within <see cref="SupervisionRadius"/> cells of a trusty who is out in the open (a trusty still inside does not count: a guard beside them watches nobody out); with nobody
        /// out yet, on duty near any cell of the zone, and the open guard posts near it that a guard is manning or on the way to (<see cref="CompGuardPost.Stander"/>, counted once with the guard on it; an open post nobody
        /// is coming to is no supervision). The tab shows it.
        /// </summary>
        private static int Supervisors(Map map, PrisonMap pm, TrustyParty party, List<IntVec3> zoneCells)
        {
            EnsureGeo(pm, party, zoneCells);
            int near = 0;
            counted.Clear();
            bool anyOut = false;
            for (int i = 0; i < trusties.Count; i++)
                if (OutOnTheJob(trusties[i], pm)) anyOut = true;
            List<Pawn> guards = pm.Guards;
            for (int i = 0; i < guards.Count; i++)
            {
                Pawn g = guards[i];
                if (!GuardPosts.OnDuty(g) || g.Map != map) continue;
                if (anyOut ? NearATrustyOutside(g.Position, pm) : NearZone(party, zoneCells, g.Position)) near++;
                else continue;
                counted.Add(g.thingIDNumber);
            }
            if (anyOut) return near;
            List<Thing> posts = map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost);
            for (int i = 0; i < posts.Count; i++)
            {
                if (posts[i].TryGetComp<CompGuardPost>() is not { OpenNow: true, Stander: { } stander } post || !NearZone(party, zoneCells, post.parent.Position)) continue;
                if (counted.Add(stander.thingIDNumber)) near++;
            }
            return near;
        }

        /// <summary>A trusty out in the open on the pass: the party's supervision is measured from where they stand.</summary>
        private static bool OutOnTheJob(Pawn p, PrisonMap pm) => p.Spawned && p.Inmate() is { outsidePass: true } && pm.IsOutsideSpace(p.Position);

        /// <summary>The guard posts near the zone that are open now (a post's hours cover it): what makes a guard come, and what the tab says is missing when there are none.</summary>
        public static int PostsNearZone(PrisonMap pm)
        {
            List<IntVec3> zoneCells = pm.zones.CellsOf(RPTrustyDefOf.RP_OutsideWork);
            EnsureGeo(pm, pm.party, zoneCells);
            int n = 0;
            List<Thing> posts = pm.map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost);
            for (int i = 0; i < posts.Count; i++)
                if (posts[i].TryGetComp<CompGuardPost>() is { OpenNow: true } post && NearZone(pm.party, zoneCells, post.parent.Position)) n++;
            return n;
        }

        private static bool NearATrustyOutside(IntVec3 at, PrisonMap pm)
        {
            for (int i = 0; i < trusties.Count; i++)
                if (trusties[i].Position.InHorDistOf(at, SupervisionRadius) && OutOnTheJob(trusties[i], pm)) return true;
            return false;
        }

        private static void EnsureGeo(PrisonMap pm, TrustyParty party, List<IntVec3> zoneCells)
        {
            if (party.geoVersion == pm.zones.Version) return;
            party.geoVersion = pm.zones.Version;
            if (zoneCells.Count == 0)
            {
                party.nearRect = CellRect.Empty;
                return;
            }
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            for (int i = 0; i < zoneCells.Count; i++)
            {
                IntVec3 c = zoneCells[i];
                if (c.x < minX) minX = c.x;
                if (c.z < minZ) minZ = c.z;
                if (c.x > maxX) maxX = c.x;
                if (c.z > maxZ) maxZ = c.z;
            }
            party.nearRect = CellRect.FromLimits(minX, minZ, maxX, maxZ).ExpandedBy(Mathf.CeilToInt(SupervisionRadius));
        }

        /// <summary>Within <see cref="SupervisionRadius"/> of a cell of the zone.</summary>
        private static bool NearZone(TrustyParty party, List<IntVec3> zoneCells, IntVec3 at)
        {
            if (!party.nearRect.Contains(at)) return false;
            for (int i = 0; i < zoneCells.Count; i++)
                if (zoneCells[i].InHorDistOf(at, SupervisionRadius)) return true;
            return false;
        }

        /// <summary>The cell is in the outside work zone and in the open (its district touches the map's edge): a place the party works.</summary>
        public static bool InZone(PrisonMap pm, IntVec3 c) =>
            c.InBounds(pm.map) && pm.zones.ZoneAt(c) == RPTrustyDefOf.RP_OutsideWork && c.GetDistrict(pm.map) is { TouchesMapEdge: true };

        /// <summary>
        /// Every target of the job lies in the zone and in the open (the queued ones and an obstruction cut included). A haul that clears a sow's way takes what is in the zone to a cell of the zone: its source is looked at
        /// from the start, and not once the thing is in the hands (its position is then the carrier's, wherever they walk); the destination is the zone's always.
        /// </summary>
        public static bool JobInZone(Job job, PrisonMap pm)
        {
            if (job == null) return true;
            // (Walking to the zone, waiting and laying down are not work on a target.)
            if (job.def != JobDefOf.CutPlant && job.def != JobDefOf.Harvest && job.def != JobDefOf.Sow && job.def != JobDefOf.HaulToCell) return true;
            bool Ok(LocalTargetInfo t) => !t.IsValid || InZone(pm, t.Cell);
            bool carried = job.def == JobDefOf.HaulToCell && job.targetA.Thing is { Spawned: false };
            if (!carried && !Ok(job.targetA) || !Ok(job.targetB)) return false;
            if (!Ok(job.targetC)) return false;
            if (job.targetQueueA != null)
                for (int i = 0; i < job.targetQueueA.Count; i++)
                    if (!Ok(job.targetQueueA[i])) return false;
            if (job.targetQueueB != null)
                for (int i = 0; i < job.targetQueueB.Count; i++)
                    if (!Ok(job.targetQueueB[i])) return false;
            return true;
        }

        /// <summary>Doing the outside work right now: a cut, a sow, a harvest or a haul that clears a sow's way, on a target in the zone (paid, and counted as hours worked).</summary>
        public static bool IsWorkingOutside(Pawn p)
        {
            JobDef d = p.CurJobDef;
            if (d != JobDefOf.CutPlant && d != JobDefOf.HaulToCell && d != JobDefOf.Sow && d != JobDefOf.Harvest) return false;
            return p.Spawned && p.CurJob is { } job && job.targetA.IsValid && PrisonMap.Of(p.Map) is { } pm && JobInZone(job, pm);
        }

        // ---- pay -----------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>An hour of the work is done: the state's pay for it is counted for the day (see <see cref="Daily"/>).</summary>
        internal static void Notify_WorkedOutside(PrisonMap pm) => pm.party.hours++;

        /// <summary>Midnight: the day's trusty-hours are paid as one line of the grounds contract.</summary>
        public static void Daily(PrisonGame game, Map map)
        {
            if (PrisonMap.Of(map)?.party is not { hours: > 0 } party) return;
            int pay = party.hours * PayPerHour;
            game.ledger.Add(RPTrustyDefOf.RP_GroundsContract, pay, "RP_GroundsContractNote".Translate(party.hours));
            party.hours = 0;
        }

        // ---- the text ------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>"1 guard", "3 guards": the count with its noun in the singular for one.</summary>
        public static string GuardCount(int n) => (n == 1 ? "RP_TrustyN_Guard" : "RP_TrustyN_Guards").Translate(n).Resolve();

        /// <summary>"1 trusty", "3 trusties".</summary>
        public static string TrustyCount(int n) => (n == 1 ? "RP_TrustyN_Trusty" : "RP_TrustyN_Trusties").Translate(n).Resolve();

        /// <summary>The party's line for the Deployment tab: out, or why not.</summary>
        public static string StateText(PrisonMap pm)
        {
            TrustyParty party = pm.party;
            return party.state switch
            {
                PartyState.Out => "RP_TrustyParty_Out".Translate(TrustyCount(party.holders), GuardCount(party.near)).Resolve(),
                PartyState.Idle => "RP_TrustyParty_Idle".Translate().Resolve(),
                PartyState.NoZone => "RP_TrustyParty_NoZone".Translate().Resolve(),
                PartyState.Riot => "RP_TrustyParty_Riot".Translate().Resolve(),
                PartyState.Hostage => "RP_TrustyParty_Hostage".Translate().Resolve(),
                PartyState.Raid => "RP_TrustyParty_Raid".Translate().Resolve(),
                PartyState.Evacuation => "RP_TrustyParty_Evacuation".Translate().Resolve(),
                PartyState.Lockdown => "RP_TrustyParty_Lockdown".Translate().Resolve(),
                PartyState.Escape => "RP_TrustyParty_Escape".Translate().Resolve(),
                PartyState.Hold => "RP_TrustyParty_Hold".Translate().Resolve(),
                // (No guard near the zone: no post covers it, or the posts that do have nobody on them or on the way - a post nobody mans is no supervision, and the line says which.)
                _ => party.near == 0
                    ? (party.postsNear == 0 ? "RP_TrustyParty_NoPost".Translate().Resolve() : "RP_TrustyParty_Unmanned".Translate(party.postsNear, party.holders).Resolve())
                    : "RP_TrustyParty_Short".Translate(GuardCount(party.near), TrustyCount(party.holders)).Resolve(),
            };
        }
    }

    /// <summary>Any mental break of an inmate shuts the outside gate to them at once: the pass is taken on the spot, not at the next look (<see cref="Trusties.Notify_MentalStateStarted"/>).</summary>
    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    public static class Patch_MentalStateHandler_TryStartMentalState_Trusty
    {
        private static readonly AccessTools.FieldRef<MentalStateHandler, Pawn> PawnOf = AccessTools.FieldRefAccess<MentalStateHandler, Pawn>("pawn");

        public static void Postfix(MentalStateHandler __instance, bool __result)
        {
            if (__result && PrisonGame.Instance != null) Trusties.Notify_MentalStateStarted(PawnOf(__instance));
        }
    }
}
