using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Unit officers (gap analysis 7, item 2): one guard named to know one wing. The Deployment tab has a slot per wing with cells; a guard holds one slot at most, the default is none. Naming sets the guard's patrol
    /// zone to the wing (the zone they had is put back when they leave the post); a guard kept off the wing by kin (<see cref="Kin.KeepsOffZone"/>) cannot be named there. Saved on <see cref="CompStaff"/>:
    /// <see cref="CompStaff.unitWing"/>, <see cref="CompStaff.unitSinceTick"/> (the days in post: rotation starts them again) and <see cref="CompStaff.unitPrevZone"/> (the patrol zone to put back).
    /// While serving (<see cref="Serving"/>: a guard on duty or at home, not on leave, not suspended, and not while the state's chief deploys the guards) the days in post pay and cost:
    /// <list type="bullet">
    /// <item>a daily trust drift of <see cref="TrustPerDay"/> (a sixth of one restraint's -15) towards the officer for the wing's inmates who were near them that day (<see cref="Hourly"/> notes who, <see cref="Daily"/> pays
    /// it), only while the inmate's score of the officer is 0 or more and never lifting it past <see cref="DriftCeiling"/>: being around makes acquaintances, not forgiveness, so a brutal officer stays hated;</item>
    /// <item>a daily chance (<see cref="TipChance"/>) of one hidden fact about the wing brought to the desk, through the informant's tip ranking (<see cref="InformantSystem.StaffTip"/>; an officer on a fixer's payroll
    /// says nothing of the patron, nor of the patron's gang);</item>
    /// <item>fewer complaints filed against them from the wing by inmates who do not hold a grudge against them (<see cref="ComplaintFactor"/>);</item>
    /// <item>a lean towards yes when a fixer on the wing sounds them out (<see cref="Lean"/>), growing with the days as kin's leverage is a lean.</item>
    /// </list>
    /// Fired, quit, dead or retrained out of guard work: the post is cleared (<see cref="Unassign"/>). On leave or suspended: the slot is vacant until they are back, and the days are kept and stand still (each midnight
    /// away moves <see cref="CompStaff.unitSinceTick"/> a day on); naming someone else meanwhile ends their post.
    /// </summary>
    public static class UnitOfficers
    {
        /// <summary>The daily trust a wing's inmate who was near the officer that day gives them (while their score of the officer is 0 or more), and the score past which presence alone adds nothing.</summary>
        public const float TrustPerDay = 2.5f, DriftCeiling = 50f;

        /// <summary>Familiarity is the days in post over this many (0..1): full at ten days.</summary>
        public const float FamiliarDays = 10f;

        /// <summary>The daily chance of a staff-side tip: a base, more per day in post, never above the cap (day 0 5%, day 5 15%, from day 10 25%).</summary>
        public const float TipBase = 0.05f, TipPerDay = 0.02f, TipCap = 0.25f;

        /// <summary>Complaints from the wing against the officer are filed at most this much less often (at full familiarity).</summary>
        public const float ComplaintCut = 0.4f;

        /// <summary>The lean towards yes for a fixer on the officer's wing, per day in post, never above <see cref="Kin.CorruptionLean"/> (ten days).</summary>
        public const float LeanPerDay = 0.015f;

        public static float LeanCap => Kin.CorruptionLean;

        /// <summary>For a test: whether the state's chief deploys the guards (null: as the game says).</summary>
        internal static bool? ChiefOverride;

        /// <summary>For a test: whether the daily tip comes (null: the dice).</summary>
        internal static bool? TipRoll;

        /// <summary>For a test: the inmates the hourly presence may note (null: every one), so that the live prison's inmates near a made-up officer are not paid.</summary>
        internal static System.Func<Pawn, bool> PresencePool;

        private static int Now => Find.TickManager.TicksGame;

        // ---- the wings ---------------------------------------------------------------------------------------------------

        private static Dictionary<SecurityLevelDef, PrisonZoneDef> zoneOf;

        /// <summary>The painted wing zone of a security level (null: none).</summary>
        public static PrisonZoneDef WingZone(SecurityLevelDef level)
        {
            if (level == null) return null;
            if (zoneOf == null)
            {
                zoneOf = new Dictionary<SecurityLevelDef, PrisonZoneDef>();
                foreach (PrisonZoneDef z in DefDatabase<PrisonZoneDef>.AllDefsListForReading)
                    if (z.security != null && !zoneOf.ContainsKey(z.security)) zoneOf[z.security] = z;
            }
            return zoneOf.TryGetValue(level, out PrisonZoneDef zone) ? zone : null;
        }

        public static string WingLabel(SecurityLevelDef level) => WingZone(level)?.label ?? level?.label ?? "?";

        // ---- the state -----------------------------------------------------------------------------------------------------

        public static float DaysInPost(CompStaff sc) => sc?.unitWing == null || sc.unitSinceTick < 0 ? 0f : Mathf.Max(0, Now - sc.unitSinceTick) / (float)GenDate.TicksPerDay;

        public static float Familiarity(CompStaff sc) => Mathf.Clamp01(DaysInPost(sc) / FamiliarDays);

        /// <summary>The state's chief deploys the guards (<see cref="SpecialMeasures.DeployedZone"/>): the officers' effects wait.</summary>
        public static bool EffectsSuspended => ChiefOverride ?? SpecialMeasures.ChiefInPost;

        /// <summary>Away (on leave, on a course, suspended or stood down): the slot is vacant, the days are kept.</summary>
        public static bool Away(CompStaff sc) => sc.role != null && !StaffCareers.Available(sc);

        /// <summary>Named, a guard, and serving: not away and not under the state's chief.</summary>
        public static bool Serving(CompStaff sc) => sc?.unitWing != null && sc.role is { guardDuties: true } && !sc.Protected && StaffCareers.Available(sc) && !EffectsSuspended;

        /// <summary>The guard named to this wing (serving or away), or null.</summary>
        public static Pawn HolderOf(Map map, SecurityLevelDef wing)
        {
            if (map == null || wing == null) return null;
            List<Pawn> staff = StaffCommute.AllStaff(map);
            for (int i = 0; i < staff.Count; i++)
                if (staff[i].GetComp<CompStaff>() is { } sc && sc.unitWing == wing) return staff[i];
            return null;
        }

        // ---- naming, rotating, vacating -----------------------------------------------------------------------------------------

        /// <summary>Why this guard cannot be named to the wing now (a phrase, resolved), or null.</summary>
        public static string Refusal(Pawn g, SecurityLevelDef wing)
        {
            if (g?.GetComp<CompStaff>() is not { } sc || sc.role is not { guardDuties: true }) return "RP_Unit_WhyNotGuard".Translate();
            if (sc.Protected) return "RP_Unit_WhyProtected".Translate();
            if (sc.leaveAtTick >= 0) return "RP_Unit_WhyCallout".Translate();
            PrisonZoneDef zone = WingZone(wing);
            if (zone == null) return "RP_Unit_WhyNoWing".Translate();
            if (sc.unitWing == wing) return "RP_Unit_WhyAlready".Translate();
            if (Kin.KeepsOffZone(g, zone)) return "RP_Unit_WhyKin".Translate();
            if (!StaffCareers.Available(sc)) return "RP_Unit_WhyAway".Translate();
            return null;
        }

        /// <summary>
        /// Names the guard to the wing: null when done, else why not (and the warden is told). Whoever held the wing leaves it (their patrol put back); a guard who held another wing moves (rotation: the days start again,
        /// and the patrol zone they had before their first post is still the one put back).
        /// </summary>
        public static string Assign(Pawn g, SecurityLevelDef wing, bool message = true)
        {
            string why = Refusal(g, wing);
            if (why != null)
            {
                if (message) Messages.Message("RP_Unit_Refused".Translate(g.LabelShort, WingLabel(wing), why), g, MessageTypeDefOf.RejectInput, historical: false);
                return why;
            }
            CompStaff sc = g.GetComp<CompStaff>();
            if (HolderOf(g.MapHeld ?? Find.AnyPlayerHomeMap, wing) is { } holder && holder != g) Unassign(holder, holder.GetComp<CompStaff>(), message ? "RP_Unit_Replaced" : null);
            if (sc.unitWing == null) sc.unitPrevZone = sc.patrolZone;
            (sc.unitWing, sc.unitSinceTick) = (wing, Now);
            sc.patrolZone = WingZone(wing);
            TakeSeen(g.thingIDNumber);
            if (message) Messages.Message("RP_Unit_Named".Translate(g.LabelShort, WingLabel(wing)), g, MessageTypeDefOf.NeutralEvent, historical: false);
            return null;
        }

        /// <summary>Off the post (taken off, replaced, fired, dead, retrained, kept off by kin): the days are gone and the patrol zone they had before is put back, if they may still patrol it. <paramref name="messageKey"/>: {0} the guard, {1} the wing.</summary>
        public static void Unassign(Pawn g, CompStaff sc, string messageKey = null)
        {
            if (sc?.unitWing == null) return;
            string label = WingLabel(sc.unitWing);
            sc.patrolZone = sc.unitPrevZone != null && (g == null || Kin.PatrolAllowed(g, sc.unitPrevZone)) ? sc.unitPrevZone : null;
            (sc.unitWing, sc.unitSinceTick, sc.unitPrevZone) = (null, -1, null);
            if (g != null) TakeSeen(g.thingIDNumber);
            if (messageKey != null && g != null) Messages.Message(messageKey.Translate(g.LabelShort, label), g, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>The patrol menus: another zone for a unit officer takes them off the post first (their days end), then the zone is theirs.</summary>
        public static void SetPatrol(Pawn g, CompStaff sc, PrisonZoneDef zone)
        {
            if (sc.unitWing != null && zone != WingZone(sc.unitWing)) Unassign(g, sc, "RP_Unit_OffByPatrol");
            sc.patrolZone = zone;
        }

        /// <summary>A guard who asked to be kept off their kin's wing, or whose kin was moved onto theirs: off the post.</summary>
        public static void Notify_KeptOff(Pawn g, CompStaff sc)
        {
            if (sc?.unitWing != null && Kin.KeepsOffZone(g, WingZone(sc.unitWing))) Unassign(g, sc, "RP_Unit_KinVacated");
        }

        // ---- the passes ------------------------------------------------------------------------------------------------------------

        /// <summary>Who of the wing was near each officer today (by officer, the inmates' ids): not saved, a reload loses the day's presence so far.</summary>
        private static readonly Dictionary<int, HashSet<int>> seen = new Dictionary<int, HashSet<int>>();
        private static Game seenGame;

        private static void CheckGame()
        {
            if (seenGame == Current.Game) return;
            seen.Clear();
            seenGame = Current.Game;
        }

        private static HashSet<int> Seen(int officerId)
        {
            CheckGame();
            if (!seen.TryGetValue(officerId, out HashSet<int> set)) seen[officerId] = set = new HashSet<int>();
            return set;
        }

        private static HashSet<int> TakeSeen(int officerId)
        {
            CheckGame();
            if (!seen.TryGetValue(officerId, out HashSet<int> set)) return null;
            seen.Remove(officerId);
            return set;
        }

        /// <summary>For the checks: how many of the wing were near the officer today.</summary>
        internal static int SeenToday(Pawn officer)
        {
            CheckGame();
            return seen.TryGetValue(officer.thingIDNumber, out HashSet<int> set) ? set.Count : 0;
        }

        /// <summary>
        /// Hourly: each serving officer on the map, awake, in their right mind and not held by hostage-takers, notes the wing's inmates near them (the same room, and within <see cref="StaffMemories.NearCells"/> in a big or
        /// outdoor one). One pass over the prisoners per officer (at most one officer per wing). <paramref name="only"/>: just this officer (for checks that must not touch the real ones).
        /// </summary>
        public static void Hourly(Map map, Pawn only = null)
        {
            if (map == null) return;
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            IReadOnlyList<Pawn> inmates = null;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn g = colonists[i];
                if (only != null && g != only || g.GetComp<CompStaff>() is not { unitWing: not null } sc || !Serving(sc) || g.Downed || !g.Awake() || g.InMentalState || HostageCrisis.IsHeld(g)
                    || g.GetRoom() is not { } room) continue;
                inmates ??= map.mapPawns.PrisonersOfColonySpawned;
                HashSet<int> set = null;
                for (int j = 0; j < inmates.Count; j++)
                {
                    Pawn p = inmates[j];
                    if (p.Inmate() is not { } c || c.security != sc.unitWing || PresencePool != null && !PresencePool(p) || !InmateUtility.IsActiveInmate(p) || !StaffMemories.Near(g, p, room)) continue;
                    (set ??= Seen(g.thingIDNumber)).Add(p.thingIDNumber);
                }
            }
        }

        /// <summary>
        /// Daily (after the feelings fade): a post held by someone who is no longer a guard here, or who is kept off the wing by kin, is cleared, and of two holders of one wing the earlier named is taken off
        /// (<see cref="ClearDoubledPosts"/>); each serving officer is paid the day's trust by the wing's inmates who were near them, and may bring a hidden fact about the wing to the desk (a letter). An officer away gets
        /// nothing today and loses nothing, and the day does not count in their days in post; under the state's chief they get nothing and the days run on. The map is this pass's, not the officer's: one at home at
        /// midnight (every day-shift guard, who leaves at the end of the day) is on no map. <paramref name="only"/>: just this officer (the checks).
        /// </summary>
        public static void Daily(Map map, Pawn only = null)
        {
            if (map == null) return;
            CheckGame();
            if (only == null) ClearDoubledPosts(StaffCommute.AllStaff(map));
            foreach (Pawn g in StaffCommute.AllStaff(map))
            {
                if (only != null && g != only || g.GetComp<CompStaff>() is not { unitWing: not null } sc) continue;
                if (sc.role is not { guardDuties: true } || sc.Protected || sc.leaveAtTick >= 0 || g.Faction != Faction.OfPlayer)
                {
                    Unassign(g, sc);
                    continue;
                }
                if (Kin.KeepsOffZone(g, WingZone(sc.unitWing)))
                {
                    Unassign(g, sc, "RP_Unit_KinVacated");
                    continue;
                }
                HashSet<int> near = TakeSeen(g.thingIDNumber);
                // Away at midnight (on leave, on a course, suspended, stood down): the day is not one in post.
                if (Away(sc) && sc.unitSinceTick >= 0) sc.unitSinceTick = Mathf.Min(Now, sc.unitSinceTick + GenDate.TicksPerDay);
                if (!Serving(sc)) continue;
                if (near is { Count: > 0 }) Drift(g, sc, map, near);
                if (!(TipRoll ?? Rand.Chance(TipChance(sc))) || StaffTip(g, sc, map) is not { } tip) continue;
                PrisonGame.Instance.stats.unitTips++;
                // (The officer is pointed at only when on the map: one at home is on none.)
                LookTargets look = g.Spawned ? new LookTargets(g) : LookTargets.Invalid;
                Find.LetterStack.ReceiveLetter("RP_Unit_TipLabel".Translate(), "RP_Unit_TipText".Translate(g.LabelShort, WingLabel(sc.unitWing), TenureText(sc), tip), LetterDefOf.NeutralEvent, look);
            }
            if (only == null) seen.Clear();
        }

        /// <summary>How long they have been on the wing, for the tip's letter: new to it (under a day), a day, or the whole days.</summary>
        private static string TenureText(CompStaff sc)
        {
            int days = Mathf.FloorToInt(DaysInPost(sc));
            return days < 1 ? "RP_Unit_TenureNew".Translate() : days == 1 ? "RP_Unit_TenureDay".Translate() : "RP_Unit_TenureDays".Translate(days);
        }

        /// <summary>
        /// Two holders of one wing (one who left the map without being let go - carried off, kidnapped, on a caravan - kept the post, someone else was named, and the first came back): the earlier named is taken off, as
        /// naming someone else does. Returns how many were taken off.
        /// </summary>
        internal static int ClearDoubledPosts(IEnumerable<Pawn> staff)
        {
            var holders = new Dictionary<SecurityLevelDef, Pawn>();
            int cleared = 0;
            foreach (Pawn g in staff.ToList())
            {
                if (g.GetComp<CompStaff>() is not { unitWing: not null } sc) continue;
                if (!holders.TryGetValue(sc.unitWing, out Pawn other))
                {
                    holders[sc.unitWing] = g;
                    continue;
                }
                CompStaff oc = other.GetComp<CompStaff>();
                (Pawn keep, Pawn drop, CompStaff dropComp) = sc.unitSinceTick > oc.unitSinceTick ? (g, other, oc) : (other, g, sc);
                holders[sc.unitWing] = keep;
                Unassign(drop, dropComp, "RP_Unit_Replaced");
                cleared++;
            }
            return cleared;
        }

        /// <summary>
        /// The day's trust: <see cref="TrustPerDay"/> from each inmate of the wing who was near the officer (and is still on it), only while their score of the officer is 0 or more (being around makes acquaintances,
        /// not forgiveness: a grudge fades as any feeling does, and presence adds to it only once it has), and never past <see cref="DriftCeiling"/>.
        /// </summary>
        internal static void Drift(Pawn g, CompStaff sc, Map map, HashSet<int> near)
        {
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn p = inmates[i];
                if (!near.Contains(p.thingIDNumber) || p.Inmate() is not { } c || c.security != sc.unitWing) continue;
                float score = StaffMemories.ScoreFor(c, g);
                if (score >= 0f && score < DriftCeiling) StaffMemories.Note(c, g, Mathf.Min(TrustPerDay, DriftCeiling - score));
            }
        }

        /// <summary>The day's chance of a staff-side tip for this officer.</summary>
        public static float TipChance(CompStaff sc) => Mathf.Min(TipCap, TipBase + TipPerDay * DaysInPost(sc));

        /// <summary>
        /// One hidden fact about the officer's wing, as an informant's tip finds it (a tunnel, contraband, a gang tie, a ring's link, a debt), or null. The wing's inmates only (and those a test confines the tips to), on
        /// <paramref name="map"/> (the daily pass's: the officer may be at home, on no map). An officer on a fixer's payroll (not turned) leaves their patron out, and does not give away the patron's gang.
        /// </summary>
        public static string StaffTip(Pawn g, CompStaff sc, Map map)
        {
            if (sc?.unitWing == null || map == null) return null;
            Pawn patron = sc.corrupt && !sc.doubleAgent ? sc.patron : null;
            GangDef patronGang = sc.corrupt && !sc.doubleAgent ? sc.patronGang ?? patron?.Inmate()?.gang : null;
            var wing = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p != patron && p.Inmate() is { } c && c.security == sc.unitWing && InmateUtility.IsActiveInmate(p) && (InformantSystem.TipPool == null || InformantSystem.TipPool(p))) wing.Add(p);
            return wing.Count == 0 ? null : InformantSystem.StaffTip(g, wing, map, patronGang);
        }

        // ---- the effects -----------------------------------------------------------------------------------------------------------

        /// <summary>Added to the odds that the officer says yes to a fixer on their wing (<see cref="CorruptionSystem.OddsFor"/>): <see cref="LeanPerDay"/> a day in post, at most <see cref="LeanCap"/>.</summary>
        public static float Lean(Pawn staff, Pawn fixer)
        {
            if (staff?.GetComp<CompStaff>() is not { } sc || !Serving(sc) || fixer?.Inmate() is not { } fc || fc.security != sc.unitWing) return 0f;
            return Mathf.Min(LeanCap, LeanPerDay * DaysInPost(sc));
        }

        /// <summary>
        /// What a complaint's filing odds are multiplied by when it is against the officer from an inmate of their wing (<see cref="ComplaintSystem"/>): 1 less <see cref="ComplaintCut"/> times familiarity, only from an inmate
        /// whose score of the officer is 0 or more (someone they know and hold no grudge against; one who resents them files as often as anywhere).
        /// </summary>
        public static float ComplaintFactor(Pawn officer, CompInmate c)
        {
            if (officer?.GetComp<CompStaff>() is not { } sc || !Serving(sc) || c == null || c.security != sc.unitWing || StaffMemories.ScoreFor(c, officer) < 0f) return 1f;
            return 1f - ComplaintCut * Familiarity(sc);
        }

        // ---- the screens ---------------------------------------------------------------------------------------------------------

        /// <summary>The shift they work and what it leaves uncovered.</summary>
        public static string ShiftNote(CompStaff sc) => (sc.nightShift ? "RP_Unit_WorksNights" : "RP_Unit_WorksDays").Translate();

        /// <summary>The Deployment row's second line: vacant, away (the slot vacant, the days kept), waiting under the state's chief, or the days in post and the shift.</summary>
        public static string SlotLine(Pawn holder)
        {
            if (holder?.GetComp<CompStaff>() is not { unitWing: not null } sc) return "RP_Unit_SlotVacant".Translate();
            string days = DaysInPost(sc).ToString("0.0");
            if (Away(sc)) return "RP_Unit_SlotAway".Translate(holder.LabelShort, days);
            if (EffectsSuspended) return "RP_Unit_SlotChief".Translate(days);
            return "RP_Unit_SlotLine".Translate(days, ShiftNote(sc));
        }

        /// <summary>The staff record's line (null: not a unit officer).</summary>
        public static string RecordLine(CompStaff sc)
        {
            if (sc?.unitWing == null) return null;
            string line = "RP_Unit_RecordLine".Translate(WingLabel(sc.unitWing), DaysInPost(sc).ToString("0.0"), ShiftNote(sc));
            if (Away(sc)) line += " " + "RP_Unit_RecordAway".Translate();
            else if (EffectsSuspended) line += " " + "RP_Unit_RecordChief".Translate();
            return line;
        }

        /// <summary>The slot's menu: none (taking the holder off), then every guard; one who cannot be named is shown greyed with the reason, and naming over a holder who is away says it ends their post.</summary>
        public static List<FloatMenuOption> SlotOptions(Map map, SecurityLevelDef wing)
        {
            var options = new List<FloatMenuOption>();
            Pawn holder = HolderOf(map, wing);
            Pawn awayHolder = holder?.GetComp<CompStaff>() is { } hc && Away(hc) ? holder : null;
            options.Add(holder != null
                ? new FloatMenuOption("RP_Unit_MenuNoneTake".Translate(holder.LabelShort), () => Unassign(holder, holder.GetComp<CompStaff>(), "RP_Unit_TakenOff"))
                : new FloatMenuOption("RP_Unit_MenuNone".Translate(), () => { }));
            foreach (Pawn g in StaffCommute.AllStaff(map).Where(p => p.GetComp<CompStaff>() is { role: { guardDuties: true, warden: false } }).OrderBy(p => p.thingIDNumber))
            {
                Pawn guard = g;
                CompStaff sc = g.GetComp<CompStaff>();
                string label = "RP_Unit_MenuGuard".Translate(g.LabelShortCap, ShiftNote(sc)).ToString();
                if (Refusal(g, wing) is { } why)
                {
                    options.Add(new FloatMenuOption("RP_Unit_MenuWhy".Translate(label, why), null));
                    continue;
                }
                if (sc.unitWing != null) label = "RP_Unit_MenuMoves".Translate(label, WingLabel(sc.unitWing)).ToString();
                else if (Kin.InmatesOf(g).Exists(k => k.Inmate()?.security == wing)) label = "RP_Unit_MenuKin".Translate(label).ToString();
                if (awayHolder != null) label = "RP_Unit_MenuReplacesAway".Translate(label, awayHolder.LabelShort).ToString();
                options.Add(new FloatMenuOption(label, () => Assign(guard, wing)));
            }
            return options;
        }

        /// <summary>For the saveload snapshot.</summary>
        public static string Describe(CompStaff sc) => sc == null ? "-" : $"{sc.unitWing?.defName}:{sc.unitSinceTick}:{sc.unitPrevZone?.defName}";
    }
}
