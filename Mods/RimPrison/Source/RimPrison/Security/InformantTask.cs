using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Tasking an informant (gap analysis 7, item 3): the warden points a known informant at one inmate. The informant's tip looks at the target first (see <see cref="InformantSystem.TryTip"/>), and their chance of being made
    /// grows with the days in place: <see cref="Term"/> is added to the chance that a tip acted on gives them away, and a quarter of it is rolled each day on its own (<see cref="Daily"/>), faster when the target's gang has
    /// someone else on the target's wing. The days in place count only the hours the informant is in reach of the target (<see cref="InReach"/>: the tip's reach): they start when the informant first is, and an hour out of
    /// reach does not count (<see cref="Hourly"/>). One who no longer trusts the warden (at or under <see cref="StaffMemories.HatedAt"/>) may feed a wrong lead, which the desk shows as an ordinary tip. The move near the
    /// target is offered with the task, never forced (<see cref="MoveOptions"/>, through <see cref="Cellmates.Move"/> and keep-apart). Saved on the informant's file: <see cref="CompInmate.taskTargetId"/> and
    /// <see cref="CompInmate.taskSinceTick"/> (tasked: the target and the start of the days in place, -1 not in place yet; pulled out, see <see cref="End"/>: the target, coded under -1, and the tick their wariness is gone).
    /// The task ends when either leaves, dies or is released (<see cref="Notify_Leaving"/>, and the daily pass as the backstop), when the informant is burned, or when the warden pulls them out.
    /// </summary>
    public static class InformantTask
    {
        /// <summary>The exposure term per day in place, and per day when the target's gang has someone else on the target's wing; never above <see cref="TermCap"/>.</summary>
        public const float PerDay = 0.03f, GangPerDay = 0.06f, TermCap = 0.6f;

        /// <summary>A tip acted on gives the informant away at this chance (the old 30%), plus the term when the tip was about their target; never above <see cref="ActedOnCap"/>.</summary>
        public const float ActedOnBase = 0.3f, ActedOnCap = 0.95f;

        /// <summary>Each day in place the informant is made on their own at this share of the term (a quarter: day 6 without the gang 4.5%, with it 9%), on a day they are in reach of the target.</summary>
        public const float DailyShare = 0.25f;

        /// <summary>The chance an informant who no longer trusts the warden feeds a wrong lead about the target instead of what they know.</summary>
        public const float WrongLeadChance = 0.3f;

        /// <summary>The exposure band: low under <see cref="RisingAt"/>, rising under <see cref="HighAt"/>, high from there.</summary>
        public const float RisingAt = 0.15f, HighAt = 0.35f;

        /// <summary>The target menu lists at most this many inmates (every suspect first, then the nearest of the rest).</summary>
        public const int MenuCap = 40;

        /// <summary>The tip's reach in cells (see <see cref="InReach"/>).</summary>
        public const float ReachCells = 30f;

        /// <summary>For a test: the roll of an exposure (0..1, exposed under the chance) instead of the dice.</summary>
        internal static float? ExposureRoll;

        /// <summary>For a test: whether a wrong lead is fed when it can be, instead of the dice.</summary>
        internal static bool? WrongLeadRoll;

        /// <summary>For a test: the inmates <see cref="GangOnWing"/> looks at besides the target (null: every one), so that the live prison's gangs do not count.</summary>
        internal static System.Func<Pawn, bool> GangPool;

        private static int Now => Find.TickManager.TicksGame;

        public static bool Tasked(CompInmate c) => c != null && c.taskTargetId >= 0;

        /// <summary>Tasked, and the days in place have started (they have been in reach of the target).</summary>
        public static bool InPlace(CompInmate c) => Tasked(c) && c.taskSinceTick >= 0;

        /// <summary>The inmate an informant was pulled out from while the wariness lasts (see <see cref="End"/>), -1 for none.</summary>
        private static int Remembered(CompInmate c) => c != null && c.taskTargetId <= -2 ? -2 - c.taskTargetId : -1;

        private static void Forget(CompInmate c) => (c.taskTargetId, c.taskSinceTick) = (-1, -1);

        /// <summary>The inmate this informant is watching, spawned on their map, or null (the screens and the tips: cheap).</summary>
        public static Pawn TargetOf(Pawn snitch, CompInmate sc)
        {
            if (!Tasked(sc) || snitch.MapHeld == null) return null;
            IReadOnlyList<Pawn> inmates = snitch.MapHeld.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].thingIDNumber == sc.taskTargetId) return inmates[i];
            return null;
        }

        /// <summary>As <see cref="TargetOf"/>, and else anywhere on the map out of sight (carried by a guard, in a container, a corpse): the daily pass, which must not end a task on someone still in the prison.</summary>
        internal static Pawn FindTarget(Pawn snitch, CompInmate sc)
        {
            if (TargetOf(snitch, sc) is { } spawned) return spawned;
            if (!Tasked(sc) || snitch.MapHeld == null) return null;
            foreach (Pawn p in snitch.MapHeld.mapPawns.AllPawnsUnspawned)
                if (p.thingIDNumber == sc.taskTargetId) return p;
            return null;
        }

        /// <summary>A known informant the warden can point at someone: in the prison, not burned, not quit, not on the way out.</summary>
        public static bool CanBeTasked(Pawn p, CompInmate c) => c != null && InmateUtility.IsActiveInmate(p) && !Retired(p, c);

        /// <summary>An informant whose task is over for good: no longer known as one, burned, quit, or on the way out.</summary>
        private static bool Retired(Pawn p, CompInmate c) => !c.KnownReputation(ReputationDefOf.RP_Snitch) || c.snitchExposed || c.informantQuit || ShuttleSystem.OnTheWayOut(p);

        /// <summary>An inmate the informant can be pointed at: someone else in the prison, not on the way out.</summary>
        public static bool Targetable(Pawn snitch, Pawn o) => o != snitch && o.Inmate() is { security: not null } && InmateUtility.IsActiveInmate(o) && !ShuttleSystem.OnTheWayOut(o);

        /// <summary>A target who has left for good, as far as the informant goes: dead, no longer a prisoner, released or on the way out, or off the informant's map.</summary>
        private static bool Gone(Pawn snitch, Pawn target) => target.Dead || !target.IsPrisonerOfColony || ShuttleSystem.OnTheWayOut(target) || target.MapHeld != snitch.MapHeld;

        /// <summary>Whether the informant is in reach of the target, as the tip sees it: within <see cref="ReachCells"/>, or in one cell or one wing (<see cref="KeepApart.Shares"/>).</summary>
        public static bool InReach(Pawn snitch, Pawn target) =>
            snitch.MapHeld != null && target.MapHeld == snitch.MapHeld && (target.PositionHeld.InHorDistOf(snitch.PositionHeld, ReachCells) || KeepApart.Shares(snitch, target, out _));

        public static float DaysInPlace(CompInmate c, int now) => !InPlace(c) ? 0f : Mathf.Max(0, now - c.taskSinceTick) / (float)GenDate.TicksPerDay;

        /// <summary>The wing of the bed their cell is built round (null: a bed in no wing).</summary>
        private static SecurityLevelDef WingOfHome(Pawn p, CompInmate c) => IntakeSystem.WingOf(Cellmates.HomeBed(p, c));

        /// <summary>
        /// Whether someone of the target's gang (not the target, not the informant) lives on the target's wing (the same level, for a target whose bed is in no wing). One pass over the prisoners. <paramref name="known"/>: as
        /// the desk sees it, only a gang the warden knows of, on both the target and the other (the screens' band; the dice use every gang).
        /// </summary>
        public static bool GangOnWing(Pawn target, Pawn snitch, bool known = false)
        {
            CompInmate tc = target?.Inmate();
            if (tc?.gang == null || known && !tc.gangKnown || target.MapHeld == null) return false;
            SecurityLevelDef wing = WingOfHome(target, tc);
            foreach (Pawn o in target.MapHeld.mapPawns.PrisonersOfColonySpawned)
            {
                if (o == target || o == snitch || GangPool != null && !GangPool(o) || o.Inmate() is not { activeFlag: true } oc || oc.gang != tc.gang || known && !oc.gangKnown) continue;
                if (wing != null ? WingOfHome(o, oc) == wing : oc.security == tc.security) return true;
            }
            return false;
        }

        private static readonly Dictionary<int, (int tick, bool on)> gangCache = new Dictionary<int, (int, bool)>();
        private static Game gangCacheGame;

        /// <summary><see cref="GangOnWing"/> as the desk sees it (known gangs only), for the screens, worked out again every 250 ticks (they ask every frame); forgotten when another game is loaded.</summary>
        private static bool GangOnWingShown(Pawn target, Pawn snitch)
        {
            if (gangCacheGame != Current.Game)
            {
                gangCache.Clear();
                gangCacheGame = Current.Game;
            }
            int now = Now;
            if (gangCache.TryGetValue(snitch.thingIDNumber, out var hit) && now >= hit.tick && now - hit.tick < 250) return hit.on;
            bool on = GangOnWing(target, snitch, known: true);
            gangCache[snitch.thingIDNumber] = (now, on);
            return on;
        }

        /// <summary>The exposure term after this many days in place.</summary>
        public static float Term(float days, bool gangOnWing) => Mathf.Min(TermCap, Mathf.Max(0f, days) * (gangOnWing ? GangPerDay : PerDay));

        /// <summary>
        /// The term of this informant now (0 untasked, not in place yet, or with the target gone). <paramref name="shown"/>: the screens' term, which counts the target's gang only when the desk knows it (and so may read
        /// lower than the dice's).
        /// </summary>
        public static float Term(Pawn snitch, CompInmate sc, bool shown = false)
        {
            if (!InPlace(sc) || TargetOf(snitch, sc) is not { } target) return 0f;
            return Term(DaysInPlace(sc, Now), shown ? GangOnWingShown(target, snitch) : GangOnWing(target, snitch));
        }

        /// <summary>The chance that a tip acted on about <paramref name="target"/> gives this informant away: the old 30%, and the term when the target is theirs.</summary>
        public static float ActedOnChance(Pawn snitch, CompInmate sc, Pawn target) =>
            Mathf.Min(ActedOnCap, ActedOnBase + (Tasked(sc) && target != null && sc.taskTargetId == target.thingIDNumber ? Term(snitch, sc) : 0f));

        public static string BandKey(float term) => term < RisingAt ? "RP_InformantBand_Low" : term < HighAt ? "RP_InformantBand_Rising" : "RP_InformantBand_High";

        public static Color BandColor(float term) => term < RisingAt ? PrisonHUD.Good : term < HighAt ? PrisonHUD.Warn : PrisonHUD.Bad;

        /// <summary>An exposure roll (<see cref="ExposureRoll"/> for a test).</summary>
        public static bool Roll(float chance) => ExposureRoll is { } roll ? roll < chance : Rand.Chance(chance);

        /// <summary>Whether this informant's trust in the warden in post is at or under the mark of hatred (<see cref="StaffMemories.HatedAt"/>): one who may feed a wrong lead.</summary>
        public static bool Distrusts(Pawn snitch, CompInmate sc) => WardenSystem.InPost(snitch.MapHeld) is { } warden && StaffMemories.ScoreFor(sc, warden) <= StaffMemories.HatedAt;

        /// <summary>
        /// A wrong lead (<see cref="WrongLeadChance"/>, for an informant who <see cref="Distrusts"/> the warden): the target is said to carry contraband they do not have, in the words of an ordinary tip, and costs what such
        /// a tip costs (it counts as a tip, the target is due a targeted drug test until searched, and the frisk is a frisk). The frisk finds nothing and clears the flag; nothing found, nobody is given away. Null when it is not
        /// fed (they carry something, so it would be true; they are flagged already; the dice).
        /// </summary>
        internal static string WrongLead(Pawn snitch, CompInmate sc, Pawn target, CompInmate tc)
        {
            if (tc == null || tc.suspected || ContrabandUtility.CarriesContraband(target) || !Distrusts(snitch, sc) || !(WrongLeadRoll ?? Rand.Chance(WrongLeadChance))) return null;
            tc.suspected = true;
            tc.informedBy = snitch;
            return "RP_TipContraband".Translate(target.LabelShort);
        }

        // ---- the warden's orders ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Points the informant at the target (one at a time: another target replaces the old one, and the days in place start again). The days start now when the informant is in reach of the target already, else when they
        /// first are (<see cref="Hourly"/>); pointed again at an inmate they were pulled out from while that one is still wary, they pick up what is left of the days (see <see cref="End"/>).
        /// </summary>
        public static void Assign(Pawn snitch, CompInmate sc, Pawn target)
        {
            int now = Now, since = -1;
            if (Remembered(sc) == target.thingIDNumber && sc.taskSinceTick > now) since = Mathf.Max(0, now - (sc.taskSinceTick - now));
            else if (InReach(snitch, target)) since = now;
            sc.taskTargetId = target.thingIDNumber;
            sc.taskSinceTick = since;
            gangCache.Remove(snitch.thingIDNumber);
            Messages.Message("RP_InformantTask_Set".Translate(snitch.LabelShort, target.LabelShort), snitch, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>
        /// The task is over. <paramref name="messageKey"/>: a line for the warden ({0} the informant, {1} the target's name or "?"), null for none. <paramref name="remember"/> (the warden pulls them out): the target stays wary
        /// of them as long as the days in place were (it fades at the rate it grew), and pointing them at that inmate again within it picks up what is left (<see cref="Assign"/>). Saved in the same two fields:
        /// <see cref="CompInmate.taskTargetId"/> = -2 - the target, <see cref="CompInmate.taskSinceTick"/> = the tick it is gone. An informant pulled out and not tasked has nothing else of a task.
        /// </summary>
        public static void End(Pawn snitch, CompInmate sc, string messageKey = null, string targetName = null, bool remember = false)
        {
            if (!Tasked(sc))
            {
                if (Remembered(sc) >= 0) Forget(sc);
                return;
            }
            string name = targetName ?? TargetOf(snitch, sc)?.LabelShort ?? "?";
            int now = Now;
            if (remember && sc.taskSinceTick >= 0 && now > sc.taskSinceTick) (sc.taskTargetId, sc.taskSinceTick) = (-2 - sc.taskTargetId, now + (now - sc.taskSinceTick));
            else Forget(sc);
            gangCache.Remove(snitch.thingIDNumber);
            if (messageKey != null) Messages.Message(messageKey.Translate(snitch.LabelShort, name), snitch, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>The warden pulls the informant out (the file's menu, Intel's button). The target's wariness is kept (see <see cref="End"/>).</summary>
        public static void PullOut(Pawn snitch, CompInmate sc) => End(snitch, sc, "RP_InformantTask_PulledOutMsg", remember: true);

        /// <summary>Why the warden might suspect this inmate, as far as the desk knows: flagged, a search due, a known tunnel, a known gang, a link of a ring found, a debt heard of.</summary>
        public static List<string> SuspectReasons(Pawn o, CompInmate oc, HashSet<int> ringIds, HashSet<int> debtIds)
        {
            var why = new List<string>();
            if (oc.suspected) why.Add("RP_InformantTask_WhyFlagged".Translate());
            if (oc.tunnelKnown) why.Add("RP_InformantTask_WhyTunnel".Translate());
            else if (oc.cellSearchPending) why.Add("RP_InformantTask_WhySearch".Translate());
            if (oc.gang != null && oc.gangKnown) why.Add(oc.gang.label);
            if (ringIds.Contains(o.thingIDNumber)) why.Add("RP_InformantTask_WhyRing".Translate());
            if (debtIds.Contains(o.thingIDNumber)) why.Add("RP_InformantTask_WhyDebt".Translate());
            return why;
        }

        /// <summary>The target menu's inmates: every suspect first (the most reasons first), then the rest nearest the informant first; at most <see cref="MenuCap"/>, never the informant or their present target.</summary>
        public static List<(Pawn pawn, List<string> why)> Targets(Pawn snitch, CompInmate sc)
        {
            var found = new List<(Pawn pawn, List<string> why)>();
            if (snitch.MapHeld is not { } map) return found;
            var ringIds = new HashSet<int>();
            foreach (StoryArc arc in RingSystem.VisibleRings())
                foreach (RingLink l in arc.ring.links)
                    if (l.known && !l.handled && l.pawnId >= 0) ringIds.Add(l.pawnId);
            var debtIds = new HashSet<int>();
            foreach (InmateDebt d in DebtSystem.Known())
            {
                debtIds.Add(d.debtorId);
                debtIds.Add(d.creditorId);
            }
            foreach (Pawn o in map.mapPawns.PrisonersOfColonySpawned)
                if (Targetable(snitch, o) && o.thingIDNumber != sc.taskTargetId) found.Add((o, SuspectReasons(o, o.Inmate(), ringIds, debtIds)));
            IntVec3 from = snitch.PositionHeld;
            return found.OrderByDescending(x => x.why.Count > 0).ThenByDescending(x => x.why.Count).ThenBy(x => x.pawn.PositionHeld.DistanceToSquared(from)).Take(MenuCap).ToList();
        }

        /// <summary>The file's "Task..." menu: pull out (when tasked), then the targets; choosing one sets the task and offers the move (<see cref="MoveOptions"/>).</summary>
        public static List<FloatMenuOption> TaskOptions(Pawn snitch, CompInmate sc)
        {
            var options = new List<FloatMenuOption>();
            if (Tasked(sc)) options.Add(new FloatMenuOption("RP_InformantTask_PullOut".Translate(TargetOf(snitch, sc)?.LabelShort ?? "?"), () => PullOut(snitch, sc)));
            foreach ((Pawn o, List<string> why) in Targets(snitch, sc))
            {
                Pawn target = o;
                string label = why.Count > 0 ? "RP_InformantTask_TargetSuspect".Translate(o.LabelShort, string.Join(", ", why)).ToString() : o.LabelShort;
                options.Add(new FloatMenuOption(label, () =>
                {
                    if (!CanBeTasked(snitch, sc) || !Targetable(snitch, target)) return;
                    Assign(snitch, sc, target);
                    Find.WindowStack.Add(new FloatMenu(MoveOptions(snitch, sc, target), "RP_InformantTask_MoveTitle".Translate(snitch.LabelShort, target.LabelShort)));
                }));
            }
            if (options.Count == 0) options.Add(new FloatMenuOption("RP_InformantTask_NoTargets".Translate(), null));
            return options;
        }

        /// <summary>
        /// Why the informant cannot be moved into the target's cell now (a keyed phrase, resolved), or null with the free bed they would take: a keep-apart order between the two or with someone who lives in that cell (named),
        /// death row, the same cell already, no free bed there that they may have (their level, their sex, the wing's rule).
        /// </summary>
        public static string CellMoveRefusal(Pawn snitch, CompInmate sc, Pawn target, out Building_Bed bed)
        {
            bed = null;
            if (KeepApart.Ordered(snitch, target)) return "RP_InformantTask_WhyKeptApart".Translate(target.LabelShort);
            if (DeathRowSystem.Ext(sc) != null || snitch.ownership == null || snitch.MapHeld == null) return "RP_InformantTask_WhyNoMove".Translate(snitch.LabelShort);
            if (Cellmates.Share(snitch, target)) return "RP_InformantTask_WhySameCell".Translate();
            Room cell = Cellmates.Home(target, target.Inmate());
            if (cell == null) return "RP_InformantTask_WhyNoCell".Translate(target.LabelShort);
            Pawn partner = null;
            foreach (Building_Bed b in IntakeSystem.FreePrisonerBeds(snitch.MapHeld, sc.security, snitch.gender))
            {
                if (b.GetRoom() != cell || !Cellmates.Accepts(b, sc)) continue;
                // (Cellmates.Move refuses a cell that holds someone they are kept apart from: said here with the name.)
                if (KeepApart.PartnerIn(b, snitch) is { } m)
                {
                    partner = m;
                    continue;
                }
                bed = b;
                return null;
            }
            return partner != null ? "RP_InformantTask_WhyKeptApart".Translate(partner.LabelShort).ToString() : "RP_InformantTask_WhyNoBedCell".Translate(snitch.LabelShort).ToString();
        }

        /// <summary>Why the informant cannot be moved onto the target's wing now, or null with the bed they would take (the best fit of the free beds there, as the review picks one).</summary>
        public static string WingMoveRefusal(Pawn snitch, CompInmate sc, Pawn target, out Building_Bed bed)
        {
            bed = null;
            if (KeepApart.Ordered(snitch, target)) return "RP_InformantTask_WhyKeptApart".Translate(target.LabelShort);
            if (DeathRowSystem.Ext(sc) != null || snitch.ownership == null || snitch.MapHeld == null) return "RP_InformantTask_WhyNoMove".Translate(snitch.LabelShort);
            SecurityLevelDef wing = WingOfHome(target, target.Inmate());
            if (wing == null) return "RP_InformantTask_WhyNoWing".Translate(target.LabelShort);
            if (WingOfHome(snitch, sc) == wing) return "RP_InformantTask_WhySameWing".Translate();
            bed = Cellmates.BestFree(snitch, sc, allowed: b => IntakeSystem.WingOf(b) == wing);
            return bed == null ? "RP_InformantTask_WhyNoBedWing".Translate(snitch.LabelShort, wing.label).ToString() : null;
        }

        /// <summary>The second step: into the target's cell, onto their wing, or where they are (each move with the cell it would be and how they would get on there, as the move menu shows it; or why not).</summary>
        public static List<FloatMenuOption> MoveOptions(Pawn snitch, CompInmate sc, Pawn target)
        {
            var options = new List<FloatMenuOption>();
            string cellWhy = CellMoveRefusal(snitch, sc, target, out Building_Bed cellBed);
            options.Add(new FloatMenuOption(Labelled("RP_InformantTask_MoveCell".Translate(target.LabelShort), cellWhy ?? Fit(snitch, sc, cellBed)), cellWhy == null ? () => MoveNear(snitch, sc, target, cell: true) : null));
            string wingWhy = WingMoveRefusal(snitch, sc, target, out Building_Bed wingBed);
            options.Add(new FloatMenuOption(Labelled("RP_InformantTask_MoveWing".Translate(target.LabelShort), wingWhy ?? Fit(snitch, sc, wingBed)), wingWhy == null ? () => MoveNear(snitch, sc, target, cell: false) : null));
            options.Add(new FloatMenuOption("RP_InformantTask_Stay".Translate(snitch.LabelShort), () => { }));
            return options;
        }

        private static string Labelled(string label, string why) => why == null ? label : label + " (" + why + ")";

        /// <summary>The cell a move would take them to and the move menu's verdict on it (good, tense, rivals: a hidden gang shows as tension, never by name).</summary>
        private static string Fit(Pawn snitch, CompInmate sc, Building_Bed bed) =>
            "RP_InformantTask_MoveFit".Translate(Cellmates.CellAt(snitch.MapHeld, bed, fresh: true)?.number ?? 0, Cellmates.Verdict(bed, sc).Key().Translate());

        /// <summary>
        /// The move, asked again now (the menu may be some seconds old): through <see cref="Cellmates.Move"/> as the warden's own placement. False with a line for the warden when it cannot be done. Done, the informant is in
        /// reach of the target and the days in place start, if they had not.
        /// </summary>
        public static bool MoveNear(Pawn snitch, CompInmate sc, Pawn target, bool cell)
        {
            string why = cell ? CellMoveRefusal(snitch, sc, target, out Building_Bed bed) : WingMoveRefusal(snitch, sc, target, out bed);
            if (why != null || !InmateUtility.IsActiveInmate(snitch))
            {
                Messages.Message("RP_InformantTask_MoveRefused".Translate(snitch.LabelShort, why ?? "?"), snitch, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            if (!Cellmates.MoveByWarden(snitch, sc, bed, Cellmates.CellAt(snitch.MapHeld, bed, fresh: true)?.number ?? 0)) return false;
            if (Tasked(sc) && sc.taskTargetId == target.thingIDNumber && sc.taskSinceTick < 0) sc.taskSinceTick = Now;
            return true;
        }

        // ---- the passes -------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Hourly: the days in place count only the hours in reach of the target. One in reach whose days have not started starts them now; one out of reach (or whose target is out of sight) has the hour taken off theirs
        /// (the start moves on by the hour, never past now). <paramref name="only"/>: just this informant (for checks that must not touch the real ones). One pass over the prisoners, a reach test per tasked informant.
        /// </summary>
        public static void Hourly(Map map, Pawn only = null)
        {
            if (map == null) return;
            int now = Now;
            foreach (Pawn snitch in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (only != null && snitch != only || snitch.Inmate() is not { } sc || !Tasked(sc)) continue;
                bool reach = TargetOf(snitch, sc) is { } target && InReach(snitch, target);
                if (reach && sc.taskSinceTick < 0) sc.taskSinceTick = now;
                else if (!reach && sc.taskSinceTick >= 0) sc.taskSinceTick = Mathf.Min(now, sc.taskSinceTick + GenDate.TicksPerHour);
            }
        }

        /// <summary>
        /// Daily, before the tips: a task whose target is gone (dead, released, transferred, on the way out, off the map: the backstop of <see cref="Notify_Leaving"/>) or whose informant is done with it (no longer known
        /// as one, burned, quit, on the way out) ends. A target out of sight (carried, in a container) or breaking out, or an informant out of reach of them or not active today, keeps the task and risks nothing today. The
        /// rest roll <see cref="DailyShare"/> of the term, and one who loses it is burned (<see cref="InformantSystem.Expose"/>). The wariness of a target the informant was pulled out from is forgotten once it is gone.
        /// <paramref name="only"/>: just this informant (for checks that must not touch the real ones).
        /// </summary>
        public static void Daily(Map map, Pawn only = null)
        {
            if (map == null) return;
            int now = Now;
            List<Pawn> tasked = null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (only != null && p != only || p.Inmate() is not { } c) continue;
                if (Tasked(c)) (tasked ??= new List<Pawn>()).Add(p);
                else if (Remembered(c) >= 0 && c.taskSinceTick <= now) Forget(c);
            }
            if (tasked == null) return;
            foreach (Pawn snitch in tasked)
            {
                CompInmate sc = snitch.Inmate();
                Pawn target = FindTarget(snitch, sc);
                if (target == null) End(snitch, sc, "RP_InformantTask_EndedLost");
                else if (Gone(snitch, target)) End(snitch, sc, "RP_InformantTask_EndedGone", target.LabelShort);
                else if (Retired(snitch, sc)) End(snitch, sc);
                else if (!InmateUtility.IsActiveInmate(snitch) || !Targetable(snitch, target) || !InReach(snitch, target)) continue;
                else if (Roll(Term(snitch, sc) * DailyShare)) InformantSystem.Expose(snitch, sc, target, target.Inmate(), burnedInPlace: true);
            }
        }

        /// <summary>Someone left for good (released, transferred, escaped, dead): their own task ends, and so does every task on them (the informant's file says so); a wariness of them is forgotten.</summary>
        public static void Notify_Leaving(Pawn p)
        {
            if (p.Inmate() is { } own) End(p, own);
            if (p.MapHeld == null) return;
            foreach (Pawn x in p.MapHeld.mapPawns.PrisonersOfColonySpawned)
            {
                if (x == p || x.Inmate() is not { } xc) continue;
                if (xc.taskTargetId == p.thingIDNumber) End(x, xc, "RP_InformantTask_EndedGone", p.LabelShort);
                else if (Remembered(xc) == p.thingIDNumber) Forget(xc);
            }
        }

        // ---- what the screens show --------------------------------------------------------------------------------------------

        /// <summary>The task as the file and Intel show it: the target, the days in place (or not near them yet) and the exposure band as the desk sees it.</summary>
        public static string Line(Pawn snitch, CompInmate sc, int now)
        {
            string name = TargetOf(snitch, sc)?.LabelShort ?? "?";
            if (!InPlace(sc)) return "RP_InformantTask_LineNotYet".Translate(name);
            return "RP_InformantTask_Line".Translate(name, DaysInPlace(sc, now).ToString("0.0"), BandKey(Term(snitch, sc, shown: true)).Translate());
        }

        /// <summary>For the saveload snapshot.</summary>
        public static string Describe(CompInmate c) => $"{c.taskTargetId}:{c.taskSinceTick}";
    }
}
