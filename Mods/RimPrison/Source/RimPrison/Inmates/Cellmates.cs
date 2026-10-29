using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace RimPrison
{
    /// <summary>The free beds of one pass over the keep-apart orders, by level and sex, and who is away from home: each worked out when first asked and kept for the pass (nobody is moved in it).</summary>
    public sealed class FreeBedPass
    {
        public readonly Map map;
        private readonly Dictionary<(SecurityLevelDef, Gender), List<Building_Bed>> free = new Dictionary<(SecurityLevelDef, Gender), List<Building_Bed>>();
        private List<Pawn> away;

        public FreeBedPass(Map map) => this.map = map;

        public List<Building_Bed> Free(SecurityLevelDef level, Gender gender)
        {
            if (!free.TryGetValue((level, gender), out List<Building_Bed> list)) free[(level, gender)] = list = IntakeSystem.FreeBedsInOrder(map, level, gender);
            return list;
        }

        public List<Pawn> Away => away ??= Cellmates.AwayFromHome(map, new List<Pawn>());
    }

    /// <summary>
    /// Shared cells and dormitories: who sleeps next to whom matters. Every arrival (the shuttle, a returning reoffender, a
    /// change of security level, a lost bed) goes where they are least likely to clash (no rival gangs, no predators next
    /// to the weak, same gang if possible); every hour cellmates who dislike each other wind each other up while friends
    /// calm each other down; and once a day the worst pairing in the prison is broken up when a better bed is free.
    /// </summary>
    public static class Cellmates
    {
        private static readonly List<Pawn> tmp = new List<Pawn>();

        /// <summary>The bed their cell is built around: the one they own, or, while they are in solitary or carried off
        /// hurt, the one they go back to.</summary>
        public static Building_Bed HomeBed(Pawn p, CompInmate c)
        {
            PunishmentUtility.SyncHomeBed(p, c);
            return c?.homeBed is { Spawned: true } home ? home : p.ownership?.OwnedBed;
        }

        public static Room Home(Pawn p, CompInmate c) => HomeBed(p, c)?.GetRoom();

        /// <summary>Whether they live in the same cell (an inmate in solitary still lives in their home cell).</summary>
        public static bool Share(Pawn a, Pawn b)
        {
            Room cell = Home(a, a.Inmate());
            return cell != null && cell == Home(b, b.Inmate());
        }

        /// <summary>Other inmates who own a bed in the same room right now (empty for single cells, and for someone in solitary).</summary>
        public static List<Pawn> Of(Pawn p)
        {
            tmp.Clear();
            Room cell = InmateUtility.CellOf(p);
            if (cell == null) return tmp;
            foreach (Building_Bed bed in cell.ContainedBeds)
                foreach (Pawn owner in bed.OwnersForReading)
                    if (owner != p && owner.IsPrisonerOfColony) tmp.Add(owner);
            return tmp;
        }

        /// <summary>The other inmates who live in their cell: Of(), plus the ones away in solitary or carried off whose home it is.
        /// A new list each call (it walks the room, so not for per-frame use: the cell block caches the same thing).</summary>
        public static List<Pawn> HomeMates(Pawn p, CompInmate c)
        {
            var mates = new List<Pawn>();
            Room cell = Home(p, c);
            if (cell == null) return mates;
            foreach (Building_Bed bed in cell.ContainedBeds)
                foreach (Pawn owner in bed.OwnersForReading)
                    if (owner != p && owner.IsPrisonerOfColony && !mates.Contains(owner)) mates.Add(owner);
            if (p.MapHeld == null) return mates;
            foreach (Pawn o in p.MapHeld.mapPawns.PrisonersOfColony)
            {
                PunishmentUtility.SyncHomeBed(o, o.Inmate());
                if (o != p && !o.Dead && o.Inmate()?.homeBed is { Spawned: true } home && o.ownership?.OwnedBed != home && home.GetRoom() == cell && !mates.Contains(o)) mates.Add(o);
            }
            return mates;
        }

        /// <summary>Hourly tension from cellmates (positive winds up, negative calms).</summary>
        public static float Tension(Pawn p, CompInmate c) => TensionAmong(p, c, Of(p));

        /// <summary>
        /// The hourly tension from these cellmates. <paramref name="knownOnly"/>: only what the warden knows of (gangs once both are
        /// known, reputations that are public or revealed), for what the screens show; what happens in the game uses every fact.
        /// </summary>
        public static float TensionAmong(Pawn p, CompInmate c, List<Pawn> mates, bool knownOnly = false)
        {
            float t = 0f;
            for (int i = 0; i < mates.Count; i++)
            {
                Pawn m = mates[i];
                CompInmate mc = m.Inmate();
                if (mc == null) continue;
                int opinion = p.relations?.OpinionOf(m) ?? 0;
                bool gangsKnown = !knownOnly || c.gangKnown && mc.gangKnown;
                if (gangsKnown && GangUtility.AreRivals(c.gang, mc.gang)) t += 2f;
                else if (gangsKnown && c.gang != null && c.gang == mc.gang) t -= 0.5f;
                if (opinion <= -20) t += 1.5f;
                else if (opinion >= 20) t -= 0.5f;
                ReputationDef extortionist = RPReputationDefOf.RP_Extortionist;
                if (knownOnly ? mc.KnownReputation(extortionist) && !c.KnownReputation(extortionist) : mc.HasReputation(extortionist) && !c.HasReputation(extortionist)) t += 0.5f;
            }
            return t;
        }

        public const float BadFit = -8f, BetterBy = 5f;
        public const int MovesPerDay = 2;
        /// <summary>The daily review's moves of inmates out of a wing now kept for the other sex: a quota of their own, so that a stranded backlog neither waits behind the cells with rival gangs nor holds them up.</summary>
        public const int WrongWingMovesPerDay = 4;
        /// <summary>Days the daily review leaves alone a placement the warden chose.</summary>
        public const int WardenHoldDays = 5;

        /// <summary>The warden has just chosen where this inmate lives (see <see cref="CompInmate.wardenPlacedTick"/>).</summary>
        public static void PlacedByWarden(CompInmate c)
        {
            if (c != null) c.wardenPlacedTick = Find.TickManager.TicksGame;
        }

        public static bool WardenHold(CompInmate c, int now) => now - c.wardenPlacedTick < WardenHoldDays * GenDate.TicksPerDay;

        /// <summary>How well a newcomer would fit with the current owners of the bed's room (higher is better); the
        /// inmate themselves is not counted, so their own bed can be scored too.</summary>
        public static float Fit(Building_Bed bed, CompInmate newcomer) => Fit(bed, newcomer, null);

        /// <summary>The social fit: the wing, the cellmates (gangs, feuds, reputations), a single cell preferred. The cell's grade is not in it (see <see cref="CellQuality.Term"/>): a clash is never traded for a nicer cell.</summary>
        private static float Fit(Building_Bed bed, CompInmate newcomer, List<Pawn> away) =>
            // A wing kept for the other sex is no place for them at all.
            !Accepts(bed, newcomer) ? ExcludedFit :
            // Their own wing beats any cellmate consideration.
            (IntakeSystem.WingOf(bed) == newcomer.security ? 20f : 0f) + MateScore(bed, newcomer, true, away) + KeepApart.WingTerm(bed, newcomer);

        /// <summary>The fit of a bed in a wing the warden keeps for the other sex (below anything a cellmate can make of it).</summary>
        public const float ExcludedFit = -1000f;

        /// <summary>The cell term for a man and a woman under one roof (men and women are kept apart): as bad as a rival gang, so the daily review splits such a cell when a clearly better bed is free.</summary>
        public const float MixedCell = -10f;

        private static Gender GenderOf(CompInmate c) => (c.parent as Pawn)?.gender ?? Gender.None;

        /// <summary>Whether the bed's wing takes this inmate (the warden's wing policy for who lives there).</summary>
        public static bool Accepts(Building_Bed bed, CompInmate c) => IntakeSystem.BedAllows(bed, GenderOf(c));

        private static bool Mixed(Pawn a, Pawn b) => a.gender != Gender.None && b.gender != Gender.None && a.gender != b.gender;

        private static readonly List<Pawn> owners = new List<Pawn>();
        private static readonly List<Pawn> awayScratch = new List<Pawn>();

        /// <summary>
        /// The prisoners away from the bed they go back to (in the hole, carried off hurt), as they are now: one pass over the prisoners, for a search that scores many beds
        /// (a newcomer picking among every free bed asked each of them, one by one).
        /// </summary>
        internal static List<Pawn> AwayFromHome(Map map, List<Pawn> into)
        {
            into.Clear();
            if (map == null) return into;
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
            {
                if (p.Dead) continue;
                PunishmentUtility.SyncHomeBed(p, p.Inmate());
                if (p.Inmate()?.homeBed is { Spawned: true } home && p.ownership?.OwnedBed != home) into.Add(p);
            }
            return into;
        }

        /// <summary>Everyone who owns a bed in the room, into <see cref="owners"/> (one walk of the room); <paramref name="away"/> is <see cref="AwayFromHome"/> if the caller has it already.</summary>
        private static List<Pawn> OwnersOf(Room room, List<Pawn> away = null)
        {
            owners.Clear();
            foreach (Building_Bed other in room.ContainedBeds) owners.AddRange(other.OwnersForReading);
            // Whoever is away in the hole still lives here: a rival must not be put into their cell.
            if (room.Map != null)
            {
                away ??= AwayFromHome(room.Map, awayScratch);
                for (int i = 0; i < away.Count; i++)
                {
                    Pawn p = away[i];
                    if (owners.Contains(p)) continue;
                    if (p.Inmate()?.homeBed is { Spawned: true } home && p.ownership?.OwnedBed != home && home.GetRoom() == room) owners.Add(p);
                }
            }
            return owners;
        }

        /// <summary>One cellmate's part of the score (negative: a clash). <paramref name="feuds"/> counts a feud (the feud's
        /// own letter offers the split, so the daily review leaves it out).</summary>
        private static float PairScore(Pawn self, CompInmate me, Pawn owner, CompInmate oc, bool feuds)
        {
            float score = -1f; // a single cell is better than a shared one
            if (self != null && Mixed(self, owner)) score += MixedCell;
            // The warden keeps them apart: never one cell (a bed of theirs in another is always a better one).
            if (self != null && KeepApart.Ordered(self, owner)) score += KeepApart.SameCellTerm;
            if (GangUtility.AreRivals(me.gang, oc.gang)) score -= 10f;
            else if (me.gang != null && me.gang == oc.gang) score += 3f;
            if (oc.HasReputation(RPReputationDefOf.RP_Extortionist) || me.HasReputation(RPReputationDefOf.RP_Extortionist)) score -= 4f;
            if (oc.HasReputation(ReputationDefOf.RP_Volatile) && me.HasReputation(ReputationDefOf.RP_Volatile)) score -= 3f;
            // A feud or a friendship counts as much as a gang does.
            if (self != null)
            {
                if (PrisonRelations.Enemies(self, owner)) { if (feuds) score -= 8f; }
                else if (PrisonRelations.Friends(self, owner)) score += 3f;
            }
            return score;
        }

        private static float MateScoreAmong(CompInmate newcomer, List<Pawn> others, bool feuds)
        {
            float score = 0f;
            Pawn self = newcomer.parent as Pawn;
            for (int i = 0; i < others.Count; i++)
                if (others[i].Inmate() is { } oc && oc != newcomer) score += PairScore(self, newcomer, others[i], oc, feuds);
            return score;
        }

        /// <summary>The cellmates part of the fit: 0 alone, below zero with people they clash with.</summary>
        public static float MateScore(Building_Bed bed, CompInmate newcomer, bool feuds = true) => MateScore(bed, newcomer, feuds, null);

        private static float MateScore(Building_Bed bed, CompInmate newcomer, bool feuds, List<Pawn> away) =>
            bed.GetRoom() is { } room ? MateScoreAmong(newcomer, OwnersOf(room, away), feuds) : 0f;

        /// <summary>Takes the best-fitting bed out of the free list (the list's order is the tie-breaker: wing beds come last). The social fit decides; among the beds within <see cref="CellQuality.SocialBand"/> of the best
        /// of it, the cell's grade against what the newcomer has earned (<see cref="CellQuality.Term"/>) does.</summary>
        public static Building_Bed PickBed(List<Building_Bed> free, CompInmate newcomer)
        {
            if (free.Count == 0) return null;
            // (Only the beds their sex may have: none of them, and none is picked.)
            if (PrisonGame.Instance != null && PrisonGame.Instance.AnyWingRestricted() && !free.Exists(b => Accepts(b, newcomer))) return null;
            List<Pawn> away = AwayFromHome(free[0].Map, new List<Pawn>());
            var social = new float[free.Count];
            float bestSocial = float.MinValue;
            for (int i = 0; i < free.Count; i++)
            {
                social[i] = Fit(free[i], newcomer, away);
                if (social[i] > bestSocial) bestSocial = social[i];
            }
            // (The bed the social fit alone would take, as before the grades: later in the list on a tie. The grade breaks ties only among beds that hold no clash for them - a rival gang, a feud, an
            // extortion pair, the other sex: a friend's or a gang's points can bring a cell with an enemy in it to within the band of an empty one - and that bed is always one of them.)
            int socialBest = -1;
            for (int i = free.Count - 1; i >= 0; i--)
                if (socialBest < 0 || social[i] > social[socialBest]) socialBest = i;
            Pawn self = newcomer.parent as Pawn;
            int best = -1;
            float bestQuality = 0f;
            for (int i = free.Count - 1; i >= 0; i--)
            {
                if (social[i] < bestSocial - CellQuality.SocialBand) continue;
                if (i != socialBest && self != null && ClashesIn(free[i], self, newcomer, away)) continue;
                float quality = CellQuality.Term(free[i], newcomer);
                if (best >= 0 && !(quality > bestQuality || quality == bestQuality && social[i] > social[best])) continue;
                best = i;
                bestQuality = quality;
            }
            Building_Bed bed = free[best];
            free.RemoveAt(best);
            return bed;
        }

        /// <summary>The best-fitting free bed the inmate could move to (not in their own cell) whose social fit is at least
        /// <paramref name="minFit"/> and that passes <paramref name="allowed"/>; null when there is none. The social fit decides; the cell's grade only breaks a tie between beds equally good socially.</summary>
        public static Building_Bed BestFree(Pawn p, CompInmate c, float minFit = float.MinValue, System.Func<Building_Bed, bool> allowed = null) =>
            BestFree(p, c, minFit, allowed, IntakeSystem.FreePrisonerBeds(p.MapHeld, c.security, p.gender));

        private static Building_Bed BestFree(Pawn p, CompInmate c, float minFit, System.Func<Building_Bed, bool> allowed, List<Building_Bed> free, List<Pawn> away = null)
        {
            Room own = Home(p, c);
            Building_Bed best = null;
            float bestFit = minFit, bestQuality = 0f;
            if (free.Count > 0) away ??= AwayFromHome(p.MapHeld, new List<Pawn>());
            foreach (Building_Bed b in free)
            {
                // (Never a bed in the cell of someone the warden keeps them apart from: the pickers that feed <see cref="Move"/> leave those out, as they leave out a wing kept for the other sex.)
                if (own != null && b.GetRoom() == own || allowed != null && !allowed(b) || !Accepts(b, c) || KeepApart.RoomHoldsPartner(b, p)) continue;
                float f = Fit(b, c, away);
                if (f < bestFit) continue;
                float quality = CellQuality.Term(b, c);
                if (best != null && f == bestFit && quality < bestQuality) continue;
                bestFit = f;
                bestQuality = quality;
                best = b;
            }
            return best;
        }

        /// <summary>
        /// Whether a free bed would take this inmate away from the cell and wing they share with someone the warden keeps them apart from (the review's own test, on the beds as they are now: not shuffled, so no random draw
        /// is spent). <paramref name="pass"/>: the free beds of the hourly pass, worked out once for all its orders.
        /// </summary>
        public static bool CanBeParted(Pawn p, CompInmate c, FreeBedPass pass = null)
        {
            if (p.ownership?.OwnedBed is not { } own || p.MapHeld == null) return false;
            bool shared = pass != null && pass.map == p.MapHeld;
            // (The pass's away list serves the own bed's fit too: no scan of the prisoners for each party asked.)
            List<Pawn> away = shared ? pass.Away : null;
            return BestFree(p, c, Fit(own, c, away) + BetterBy, null, shared ? pass.Free(c.security, p.gender) : IntakeSystem.FreeBedsInOrder(p.MapHeld, c.security, p.gender), away) != null;
        }

        /// <summary>Takes out of a free list the beds in the cell of someone the warden keeps this inmate apart from (for a picker that takes a bed from the list and moves them into it); how many were taken out.</summary>
        public static int DropPartnerBeds(Pawn p, List<Building_Bed> free) => KeepApart.Any ? free.RemoveAll(b => KeepApart.RoomHoldsPartner(b, p)) : 0;

        /// <summary>The last bed of a free list that is not in the cell of someone the warden keeps this inmate apart from (the wing beds come last in the list), or null.</summary>
        public static Building_Bed LastUsable(Pawn p, List<Building_Bed> free)
        {
            for (int i = free.Count - 1; i >= 0; i--)
                if (!KeepApart.RoomHoldsPartner(free[i], p)) return free[i];
            return null;
        }

        /// <summary>Whether a bed's cell holds anyone this inmate must not be put with for a grade: a rival gang, a feud, an extortion pair (either of them an extortionist), or the other sex.</summary>
        private static bool ClashesIn(Building_Bed bed, Pawn p, CompInmate c, List<Pawn> away)
        {
            if (bed.GetRoom() is not { } room) return false;
            foreach (Pawn o in OwnersOf(room, away))
            {
                if (o == p || o.Inmate() is not { } oc || oc == c) continue;
                if (GangUtility.AreRivals(c.gang, oc.gang) || PrisonRelations.Enemies(p, o) || Mixed(p, o) || KeepApart.Ordered(p, o)
                    || oc.HasReputation(RPReputationDefOf.RP_Extortionist) || c.HasReputation(RPReputationDefOf.RP_Extortionist)) return true;
            }
            return false;
        }

        /// <summary>
        /// The free bed a grade move would take this inmate to, or null: a cell they may have whose social fit is no worse than where they live now (worked out now, after the moves already made this pass), holding no rival gang,
        /// feud, extortion pair or other sex, and whose grade term is better than their present one by at least <see cref="CellQuality.MoveBetterBy"/>. The best term wins, then the better social fit.
        /// </summary>
        public static Building_Bed BestFreeForGrade(Pawn p, CompInmate c)
        {
            if (p.ownership?.OwnedBed is not { } from) return null;
            List<Building_Bed> free = IntakeSystem.FreePrisonerBeds(p.MapHeld, c.security, p.gender);
            if (free.Count == 0) return null;
            Room own = Home(p, c);
            List<Pawn> away = AwayFromHome(p.MapHeld, new List<Pawn>());
            float socialNow = Fit(from, c, away), needed = CellQuality.Term(from, c) + CellQuality.MoveBetterBy;
            Building_Bed best = null;
            float bestQuality = 0f, bestSocial = 0f;
            foreach (Building_Bed b in free)
            {
                if (own != null && b.GetRoom() == own || !Accepts(b, c) || PaddedCells.IsPadded(b)) continue;
                float quality = CellQuality.Term(b, c);
                if (quality < needed) continue;
                float social = Fit(b, c, away);
                if (social < socialNow || best != null && !(quality > bestQuality || quality == bestQuality && social > bestSocial) || ClashesIn(b, p, c, away)) continue;
                best = b;
                (bestQuality, bestSocial) = (quality, social);
            }
            return best;
        }

        /// <summary>
        /// Gives the inmate this bed (one a free-bed search offered them) and takes them out of the old one. Someone in
        /// solitary or carried off hurt keeps their cell for later: the bed they go back to is the new one, or
        /// <see cref="PunishmentUtility.End"/> would send them back to the old cell. Never leaves them without a bed: the bed is
        /// checked as it is now (the prisoner bed list is up to 250 ticks old, and does not refresh while paused), and vanilla's
        /// claim lets go of the old bed itself, so if it refuses anyway the old bed is taken back. A deliberate move of an
        /// inmate who has a bed goes through here (a newcomer's first bed, and an inmate who lost theirs, are claimed directly).
        /// </summary>
        public static bool Move(Pawn p, CompInmate c, Building_Bed bed)
        {
            if (bed == null || !bed.Spawned || !bed.ForPrisoners || bed.Medical || p.ownership == null || p.MapHeld == null) return false;
            Building_Bed old = p.ownership.OwnedBed;
            if (old == bed || c.homeBed == bed || !Accepts(bed, c) || KeepApart.RoomHoldsPartner(bed, p) || !IntakeSystem.FreePrisonerBeds(p.MapHeld, c.security, p.gender).Contains(bed)) return false;
            if (c.homeBed != null && old != c.homeBed) c.homeBed = bed;
            else
            {
                p.ownership.ClaimBedIfNonMedical(bed);
                if (p.ownership.OwnedBed != bed)
                {
                    if (old != null && old.Spawned && p.ownership.OwnedBed != old) p.ownership.ClaimBedIfNonMedical(old);
                    return false;
                }
                c.homeBed = null;
            }
            InvalidateCells();
            PrisonGame.Instance.stats.cellMoves++;
            return true;
        }

        /// <summary>Whether the daily review would try to move this inmate: badly placed (a feud is left to its own letter), not
        /// held by a choice of the warden's (their own, or a cellmate's they clash with), not being punished, downed or in a
        /// mental state, and not on the roll call of a running evacuation (its bed owners never change).</summary>
        public static bool ReviewWould(Pawn p, CompInmate c)
        {
            if (!InmateUtility.IsActiveInmate(p) || p.Downed || p.InMentalState || c.forcedActivity != null || EvacuationSystem.HoldsBed(p)) return false;
            if (p.ownership?.OwnedBed is not { } own || PunishmentUtility.IsSolitaryBed(own)) return false;
            int now = Find.TickManager.TicksGame;
            // The warden keeps them apart from someone in their cell or wing (and has not placed them since the order): a bed of theirs in another cell or wing, if one is free.
            if (KeepApart.Violation(p, c, now, out _, out _) != null) return true;
            if (WardenHold(c, now)) return false;
            // In a wing now kept for the other sex: they go to the first bed the review finds them.
            if (!Accepts(own, c)) return true;
            List<Pawn> mates = Of(p);
            if (mates.Count == 0) return false;
            Pawn self = p;
            foreach (Pawn m in mates)
                if (m.Inmate() is { } mc && WardenHold(mc, now) && PairScore(self, c, m, mc, true) < -1f) return false;
            return MateScore(own, c, feuds: false) <= BadFit;
        }

        /// <summary>
        /// Whether the daily review would try to move this inmate for their cell's grade: the policy covers them, they live two or more grades from what they have earned (see <see cref="CellQuality.MoveAt"/>), and the
        /// same holds as for <see cref="ReviewWould"/> apply (the warden's own placement, or a cellmate's they clash with, punishment, being downed or in a mental state), and they were not moved for their grade in the last
        /// <see cref="CellQuality.MoveCooldownDays"/> days. A cell with a bad fit is <see cref="ReviewWould"/>'s. (The daily review also wants the mismatch at two reviews in a row: <see cref="GradeMismatchConfirmed"/>.)
        /// </summary>
        public static bool ReviewWouldForGrade(Pawn p, CompInmate c)
        {
            if (!CellQuality.Applies(c) || !InmateUtility.IsActiveInmate(p) || p.Downed || p.InMentalState || c.forcedActivity != null || EvacuationSystem.HoldsBed(p)) return false;
            if (p.ownership?.OwnedBed is not { } own || PunishmentUtility.IsSolitaryBed(own) || !Accepts(own, c)) return false;
            int now = Find.TickManager.TicksGame;
            if (WardenHold(c, now) || c.gradeMovedTick != CellQuality.NoTick && now - c.gradeMovedTick < CellQuality.MoveCooldownDays * GenDate.TicksPerDay || CellQuality.Term(own, c) > -CellQuality.MoveAt) return false;
            foreach (Pawn m in Of(p))
                if (m.Inmate() is { } mc && WardenHold(mc, now) && PairScore(p, c, m, mc, true) < -1f) return false;
            return true;
        }

        /// <summary>Whether this inmate lives in a bed of a wing the warden keeps for the other sex.</summary>
        public static bool InWingKeptForTheOtherSex(Pawn p, CompInmate c) => p.ownership?.OwnedBed is { } own && !PunishmentUtility.IsSolitaryBed(own) && !Accepts(own, c);

        /// <summary>
        /// The inmates who live in a wing kept for the other sex, and how many of them have no free bed of theirs to go to now (the Intake tab says it; the daily review moves them out where there is room,
        /// <see cref="WrongWingMovesPerDay"/> a day).
        /// </summary>
        public static int InWrongWing(Map map, out int withNowhereToGo)
        {
            int n = 0;
            withNowhereToGo = 0;
            if (PrisonGame.Instance == null || !PrisonGame.Instance.AnyWingRestricted()) return 0;
            Dictionary<(SecurityLevelDef, Gender), bool> room = null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { security: { } sec } c || !InWingKeptForTheOtherSex(p, c)) continue;
                n++;
                room ??= new Dictionary<(SecurityLevelDef, Gender), bool>();
                if (!room.TryGetValue((sec, p.gender), out bool any))
                    room[(sec, p.gender)] = any = IntakeSystem.FreeBedCount(map, sec, gender: p.gender) > 0;
                if (!any) withNowhereToGo++;
            }
            return n;
        }

        /// <summary>The daily review sees this inmate's grade mismatch: true when it was seen at the review before as well (a room that is dirty for one day moves no one). Keeps the sighting for the next review.</summary>
        private static bool GradeMismatchConfirmed(CompInmate c, int now)
        {
            int since = now - c.gradeMismatchTick;
            bool confirmed = c.gradeMismatchTick != CellQuality.NoTick && since >= CellQuality.ConsecutiveFrom && since <= CellQuality.ConsecutiveTo;
            // (The same pass asked again keeps the first sighting; a gap of more than a day, or none yet, starts over.)
            if (confirmed || c.gradeMismatchTick == CellQuality.NoTick || since > CellQuality.ConsecutiveTo || since < 0) c.gradeMismatchTick = now;
            return confirmed;
        }

        /// <summary>Daily: move the worst-placed inmates (a bad fit with their cellmates, judged on the social fit alone) to a clearly better free bed in
        /// their wing, a couple a day, and (a quota of their own) those who live in a wing now kept for the other sex to any bed of theirs. Not anyone being punished, downed or in a mental state, nor a placement the warden chose.
        /// With cells by conduct on, an inmate two or more grades from what they have earned, at two reviews in a row and not moved for it in the last days, is a candidate too (<see cref="ReviewWouldForGrade"/>), after all the
        /// bad fits, in the same quota: they move only to a free cell whose social fit is no worse than where they live and that holds no clash (<see cref="BestFreeForGrade"/>). Each candidate's own fit is worked out just before
        /// the choice, after the moves already made this pass.</summary>
        public static int DailyReview(Map map)
        {
            int now = Find.TickManager.TicksGame;
            var ordinary = new List<(Pawn p, CompInmate c, float rank)>();
            var grade = new List<(Pawn p, CompInmate c, float rank)>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { } c) continue;
                if (ReviewWould(p, c)) ordinary.Add((p, c, Fit(p.ownership.OwnedBed, c)));
                else if (ReviewWouldForGrade(p, c) && GradeMismatchConfirmed(c, now)) grade.Add((p, c, CellQuality.Term(p.ownership.OwnedBed, c)));
            }
            int moved = 0, movedOut = 0;
            ordinary.SortBy(x => x.rank);
            grade.SortBy(x => x.rank);
            // (A wing kept for the other sex has no bed they could go to, for everyone of their sex and level at once: asked once.)
            HashSet<(SecurityLevelDef, Gender)> nowhere = null;
            foreach ((Pawn p, CompInmate c, _) in ordinary)
            {
                bool wrongWing = InWingKeptForTheOtherSex(p, c);
                if (wrongWing ? movedOut >= WrongWingMovesPerDay : moved >= MovesPerDay) continue;
                // Still badly placed? (An earlier move this pass may have taken the cellmate away.)
                if (!ReviewWould(p, c)) continue;
                // (Kept apart from someone: the line of the chronicle says so, and whom. Worked out now, before the move that ends it.)
                KeepApartOrder apart = KeepApart.Violation(p, c, now, out Pawn partner, out bool apartCell);
                if (wrongWing && nowhere != null && nowhere.Contains((c.security, p.gender))) continue;
                Building_Bed best = BestFree(p, c, Fit(p.ownership.OwnedBed, c) + BetterBy);
                if (best == null)
                {
                    if (wrongWing) (nowhere ??= new HashSet<(SecurityLevelDef, Gender)>()).Add((c.security, p.gender));
                    continue;
                }
                if (!Move(p, c, best)) continue;
                if (wrongWing) movedOut++;
                else moved++;
                if (apart != null) Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, (apartCell ? "RP_Chr_CellMoveApartCell" : "RP_Chr_CellMoveApartWing").Translate(p.LabelShort, partner.LabelShort), p, partner);
                else Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_CellMove".Translate(p.LabelShort), p);
            }
            foreach ((Pawn p, CompInmate c, _) in grade)
            {
                if (moved >= MovesPerDay) break;
                if (!ReviewWouldForGrade(p, c)) continue;
                Building_Bed best = BestFreeForGrade(p, c);
                if (best == null) continue;
                int gradeFrom = CellQuality.GradeAt(p.ownership.OwnedBed);
                if (!Move(p, c, best)) continue;
                moved++;
                (c.gradeMovedTick, c.gradeMismatchTick) = (now, CellQuality.NoTick);
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, GradeMoveText(p, c, gradeFrom, now), p);
            }
            return moved + movedOut;
        }

        /// <summary>The chronicle line of a move for the cell's grade: up for the clean days, down for a seen fight or just for living above what they have earned.</summary>
        private static TaggedString GradeMoveText(Pawn p, CompInmate c, int gradeFrom, int now)
        {
            if (gradeFrom < c.entitlement) return "RP_Chr_CellMoveUp".Translate(p.LabelShort, CellQuality.CleanDays(c, now));
            return CellQuality.RecentSevere(c, now, CellQuality.StepDays * 2) is { } why
                ? "RP_Chr_CellMoveDown".Translate(p.LabelShort, why.def.label)
                : "RP_Chr_CellMoveDownPlain".Translate(p.LabelShort);
        }

        /// <summary>How a cell looks to the warden: nothing wrong, tense, or known rivals under one roof.</summary>
        public enum CellFit { Good, Tense, Rivals }

        public static string Key(this CellFit f) => f == CellFit.Good ? "RP_CellFitGood" : f == CellFit.Tense ? "RP_CellFitTense" : "RP_CellFitRivals";

        public static UnityEngine.Color ColorOf(this CellFit f) => f == CellFit.Good ? PrisonHUD.Good : f == CellFit.Tense ? PrisonHUD.Warn : PrisonHUD.Bad;

        private static CellFit VerdictAmong(CompInmate who, List<Pawn> others)
        {
            foreach (Pawn owner in others)
                if (owner.Inmate() is { } oc && oc != who && who.gangKnown && oc.gangKnown && GangUtility.AreRivals(who.gang, oc.gang)) return CellFit.Rivals;
            return MateScoreAmong(who, others, true) <= -2f ? CellFit.Tense : CellFit.Good;
        }

        /// <summary>How the inmate would get on in this bed's cell. Rivals only show as such once both gangs are known to the
        /// warden (a hidden gang shows up as plain tension, never by name).</summary>
        public static CellFit Verdict(Building_Bed bed, CompInmate who) =>
            bed.GetRoom() is { } room ? VerdictAmong(who, OwnersOf(room)) : CellFit.Good;

        /// <summary>One cell (a room with prisoner beds) as the warden sees it, worked out once per refresh of <see cref="Cells"/>.</summary>
        public class CellInfo
        {
            public int number;
            public Room room;
            public SecurityLevelDef wing;
            public readonly List<Building_Bed> beds = new List<Building_Bed>();
            /// <summary>Everyone who lives here, including those away in solitary or carried off hurt (their bed is held for them).</summary>
            public readonly List<Pawn> occupants = new List<Pawn>();
            public readonly List<Pawn> away = new List<Pawn>();
            public int slots;
            /// <summary>The cell's grade 0-5 (see <see cref="CellQuality"/>), -1 while its room has not been graded yet.</summary>
            public int grade = -1;
            public readonly List<Occupant> rows = new List<Occupant>();
            /// <summary>The worst anyone here has it.</summary>
            public CellFit worst;

            public int Free => System.Math.Max(0, slots - occupants.Count);

            public Occupant? RowOf(Pawn p)
            {
                foreach (Occupant o in rows) if (o.pawn == p) return o;
                return null;
            }

            /// <summary>Each occupant's verdict (their bed against the others who are there) and the hourly tension it brings.</summary>
            internal void Evaluate()
            {
                var present = new List<Pawn>();
                foreach (Pawn o in occupants) if (!away.Contains(o)) present.Add(o);
                var others = new List<Pawn>();
                worst = CellFit.Good;
                foreach (Pawn o in occupants)
                {
                    if (o.Inmate() is not { } c) continue;
                    others.Clear();
                    foreach (Pawn x in present) if (x != o) others.Add(x);
                    CellFit fit = VerdictAmong(c, others);
                    bool here = !away.Contains(o);
                    rows.Add(new Occupant { pawn = o, fit = fit, tension = here ? TensionAmong(o, c, others) : 0f, knownTension = here ? TensionAmong(o, c, others, knownOnly: true) : 0f });
                    if (fit > worst) worst = fit;
                }
            }
        }

        public struct Occupant
        {
            public Pawn pawn;
            public CellFit fit;
            public float tension;
            /// <summary>The tension as far as the warden knows its causes (what the screens show).</summary>
            public float knownTension;
        }

        public const int CacheTicks = 60;
        public const float CacheSeconds = 1f;
        private static List<CellInfo> cache;
        private static Map cacheMap;
        private static int cacheTick;
        private static float cacheReal;

        /// <summary>Forget the cached cells (something that changes them just happened).</summary>
        public static void InvalidateCells() => cache = null;

        /// <summary>
        /// The prison's cells (a shared dormitory is one cell), numbered by <see cref="AssignNumbers"/>. Worked out once and
        /// kept for <see cref="CacheTicks"/> ticks or <see cref="CacheSeconds"/> of real time, whichever is less (a paused game
        /// still refreshes), or until a move: the cell block draws it every frame. Do not change the list. <paramref name="fresh"/>
        /// works it out again now (for a click, or a letter's text).
        /// </summary>
        public static List<CellInfo> Cells(Map map, bool fresh = false)
        {
            int now = Find.TickManager.TicksGame;
            float real = UnityEngine.Time.realtimeSinceStartup;
            if (!fresh && cache != null && cacheMap == map && now >= cacheTick && now - cacheTick < CacheTicks && real - cacheReal < CacheSeconds) return cache;
            var cells = new List<CellInfo>();
            PrisonMap pm = PrisonMap.Of(map);
            if (pm != null)
            {
                var byRoom = new Dictionary<object, CellInfo>();
                var byBed = new Dictionary<Building_Bed, CellInfo>();
                // (Checked as they are now: the list is up to 250 ticks old.)
                foreach (Building_Bed bed in pm.PrisonerBeds.OrderBy(b => b.thingIDNumber))
                {
                    if (!bed.Spawned || !bed.ForPrisoners || bed.Medical || PunishmentUtility.IsSolitaryBed(bed) || bed.CompAssignableToPawn == null) continue;
                    object key = (object)bed.GetRoom() ?? bed;
                    if (!byRoom.TryGetValue(key, out CellInfo cell))
                    {
                        byRoom[key] = cell = new CellInfo { room = bed.GetRoom(), wing = IntakeSystem.WingOf(bed) };
                        cells.Add(cell);
                    }
                    cell.beds.Add(bed);
                    byBed[bed] = cell;
                    cell.slots += bed.CompAssignableToPawn.TotalSlots;
                    foreach (Pawn owner in bed.OwnersForReading)
                        if (owner.IsPrisonerOfColony && !cell.occupants.Contains(owner)) cell.occupants.Add(owner);
                }
                // Whoever is away (solitary, carried off hurt) still lives here, and their bed is not free.
                foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                {
                    PunishmentUtility.SyncHomeBed(p, p.Inmate());
                    if (!p.Dead && p.Inmate()?.homeBed is { } home && p.ownership?.OwnedBed != home && byBed.TryGetValue(home, out CellInfo homeCell) && !homeCell.occupants.Contains(p))
                    {
                        homeCell.occupants.Add(p);
                        homeCell.away.Add(p);
                    }
                }
                int[] numbers = AssignNumbers(cells.ConvertAll(x => x.beds.ConvertAll(b => map.cellIndices.CellToIndex(b.Position)).ToArray()), pm.cellNumbers, ref pm.nextCellNumber);
                for (int i = 0; i < cells.Count; i++)
                {
                    cells[i].number = numbers[i];
                    cells[i].grade = pm.GradeOf(cells[i].room);
                    cells[i].Evaluate();
                }
                cells.Sort((x, y) => x.number.CompareTo(y.number));
            }
            cache = cells;
            cacheMap = map;
            cacheTick = now;
            cacheReal = real;
            return cells;
        }

        /// <summary>
        /// Stable cell numbers. Each cell (given as the map cell index of each of its beds, in the order the beds were built)
        /// keeps the lowest number any of its beds already carries, handed out by position: a bed replaced where it stood, a bed
        /// switched off and on again, or a cell zoned as solitary and back keeps its number, and the others never shift. Cells
        /// that merge keep the lower number; a split gives the old number to one half and a new one to the other. A number
        /// is never given twice. New cells (and cells with only new beds) take the next number, in the order given.
        /// </summary>
        public static int[] AssignNumbers(List<int[]> cells, Dictionary<int, int> numbers, ref int next)
        {
            var wanted = new int[cells.Count];
            for (int i = 0; i < cells.Count; i++)
            {
                wanted[i] = int.MaxValue;
                foreach (int idx in cells[i])
                    if (numbers.TryGetValue(idx, out int n) && n > 0 && n < wanted[i]) wanted[i] = n;
            }
            var order = new List<int>();
            for (int i = 0; i < cells.Count; i++) order.Add(i);
            order.Sort((x, y) => wanted[x] != wanted[y] ? wanted[x].CompareTo(wanted[y]) : x.CompareTo(y));
            var used = new HashSet<int>();
            var result = new int[cells.Count];
            foreach (int i in order)
            {
                int number = wanted[i] != int.MaxValue && !used.Contains(wanted[i]) ? wanted[i] : next++;
                used.Add(number);
                result[i] = number;
                foreach (int idx in cells[i]) numbers[idx] = number;
            }
            return result;
        }

        /// <summary>The cell this bed belongs to (null for a bed that is no cell's, such as a solitary one).</summary>
        public static CellInfo CellAt(Map map, Building_Bed bed, bool fresh = false)
        {
            if (bed == null || map == null) return null;
            foreach (CellInfo cell in Cells(map, fresh))
                if (cell.beds.Contains(bed)) return cell;
            return null;
        }

        /// <summary>The cell an inmate lives in (their home cell while in solitary), or null.</summary>
        public static CellInfo CellOfInmate(Pawn p, CompInmate c, bool fresh = false) => CellAt(p.MapHeld, HomeBed(p, c), fresh);

        /// <summary>The move menu: every cell with a free bed this inmate may use (their wing, and others their security
        /// level allows), who lives there and how that would go. Choosing one moves them the way the daily review does.</summary>
        public static List<FloatMenuOption> MoveOptions(Pawn p, CompInmate c)
        {
            var found = new List<(CellFit verdict, float fit, FloatMenuOption option)>();
            Map map = p.MapHeld;
            if (map != null && p.ownership != null)
            {
                Room own = Home(p, c);
                List<Building_Bed> free = IntakeSystem.FreePrisonerBeds(map, c.security, p.gender);
                foreach (CellInfo cell in Cells(map, fresh: true))
                {
                    if (cell.room != null && cell.room == own) continue;
                    // (The warden keeps them apart from someone who lives there: shown, but not to be chosen.)
                    if (cell.occupants.Find(o => o != p && KeepApart.Ordered(p, o)) is { } kept && cell.beds.Exists(b => free.Contains(b) && Accepts(b, c)))
                    {
                        found.Add((CellFit.Rivals, float.MinValue, new FloatMenuOption("RP_MoveCellKeptApart".Translate(cell.number, kept.LabelShort), null)));
                        continue;
                    }
                    Building_Bed target = null;
                    float bestFit = float.MinValue;
                    foreach (Building_Bed b in cell.beds)
                        if (free.Contains(b) && Accepts(b, c) && Fit(b, c) is var f && f > bestFit)
                        {
                            bestFit = f;
                            target = b;
                        }
                    if (target == null) continue;
                    var mates = new List<string>();
                    foreach (Pawn o in cell.occupants) if (o != p) mates.Add(o.LabelShort);
                    CellFit verdict = Verdict(target, c);
                    string label = "RP_MoveCellOption".Translate(cell.number, cell.wing?.label ?? "RP_MoveCellNoWing".Translate().ToString(),
                        mates.Count == 0 ? "RP_MoveCellEmpty".Translate().ToString() : "RP_MoveCellWith".Translate(string.Join(", ", mates)).ToString(), verdict.Key().Translate());
                    Building_Bed chosen = target;
                    int number = cell.number;
                    found.Add((verdict, bestFit, new FloatMenuOption(label, () => MoveByWarden(p, c, chosen, number))));
                }
            }
            // What the warden can see first (a cell that looks fine, then a tense one, then rivals), the best fit within that: the
            // fit itself counts hidden gangs, which would give the ordering away.
            List<FloatMenuOption> options = found.OrderBy(x => x.verdict).ThenByDescending(x => x.fit).Select(x => x.option).ToList();
            if (options.Count == 0) options.Add(new FloatMenuOption("RP_MoveCellNone".Translate(), null));
            return options;
        }

        /// <summary>The warden's own move (the move menu, an informant's task): through <see cref="Move"/>, held as the warden's placement, with the chronicle's line. False when it could not be done.</summary>
        internal static bool MoveByWarden(Pawn p, CompInmate c, Building_Bed bed, int cellNumber)
        {
            if (!InmateUtility.IsActiveInmate(p) || !Move(p, c, bed))
            {
                Messages.Message("RP_MoveCellFailed".Translate(p.LabelShort), p, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            PlacedByWarden(c);
            List<Pawn> mates = HomeMates(p, c);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_CellMoveWarden".Translate(p.LabelShort, cellNumber, mates.Count == 0 ? "RP_MoveCellEmpty".Translate().ToString() : "RP_MoveCellWith".Translate(string.Join(", ", mates.ConvertAll(x => x.LabelShort))).ToString()), p);
            Messages.Message("RP_MoveCellDone".Translate(p.LabelShort, cellNumber), p, MessageTypeDefOf.NeutralEvent, historical: false);
            return true;
        }
    }
}
