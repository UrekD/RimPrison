using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPExInmateDefOf
    {
        public static ThoughtDef RP_FriendHired;

        static RPExInmateDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPExInmateDefOf));
    }

    /// <summary>What the prison knew of someone at the gate, kept for the application and the hire, and on the staff record of one who was hired (strings, numbers and defs: no pawn).</summary>
    public class ExInmatePast : IExposable
    {
        public string crime;
        public float daysServed;
        /// <summary>Serious incidents on the file (misconducts seen, of severity two or more).</summary>
        public int troubles;
        /// <summary>Programs passed (not the first parts of a course).</summary>
        public int programs;
        public ProgramDef trade;
        public GangDef gang;
        /// <summary>Their rank in it (see <see cref="GangRank"/>).</summary>
        public byte gangRank;
        /// <summary>The warden knew the gang at the gate: the letter says so only then.</summary>
        public bool gangKnown;
        public bool dealer, ring;

        public static ExInmatePast Of(Pawn p, CompInmate c) => new ExInmatePast
        {
            crime = c.crime?.label,
            daysServed = c.DaysServed,
            troubles = c.records.Count(r => r.seen && r.def.severity >= 2),
            programs = c.programsPassed,
            trade = Vocation.Trade(c),
            gang = c.gang,
            gangRank = (byte)GangRanks.Of(c),
            gangKnown = c.gangKnown,
            dealer = c.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer)),
            ring = RingSystem.LinkOf(p, out _) != null,
        };

        public ExInmatePast Copy() => (ExInmatePast)MemberwiseClone();

        public string Describe() => $"{crime}/{daysServed:F1}/{troubles}/{programs}/{trade?.defName}/{gang?.defName}:{gangRank}/{gangKnown}{dealer}{ring}";

        public void ExposeData()
        {
            Scribe_Values.Look(ref crime, "crime");
            Scribe_Values.Look(ref daysServed, "days");
            Scribe_Values.Look(ref troubles, "troubles");
            Scribe_Values.Look(ref programs, "programs");
            Scribe_Defs.Look(ref trade, "trade");
            Scribe_Defs.Look(ref gang, "gang");
            Scribe_Values.Look(ref gangRank, "gangRank");
            Scribe_Values.Look(ref gangKnown, "gangKnown");
            Scribe_Values.Look(ref dealer, "dealer");
            Scribe_Values.Look(ref ring, "ring");
        }
    }

    /// <summary>
    /// Former inmates come back (gap analysis 5, 2.5). Someone released with a trade, a passed program or a friend still inside is kept as a world pawn (like a reoffender, with a cap of their own,
    /// <see cref="MaxKept"/>, the oldest released going first). A few days to two weeks after they walked out they may apply for work (a decision letter with their record: hire them as a cook, a
    /// workman or a gardener, never a guard or anyone with keys, or turn them down for good); one who is hired starts with the integrity their record earns (a clean record and a program, higher
    /// than a fresh hire's; a gang past, lower), and an inmate of their old gang may lean on them later. A former inmate with a friend inside may also come to visit as themselves, through the
    /// visitation system, and go back to the world, still kept, when the visit is over.
    /// </summary>
    public static class ExInmates
    {
        public const int MaxKept = 20, MinApplyDays = 3, MaxApplyDays = 14, VisitCooldownDays = 5, LetterDays = 2;
        /// <summary>Per kept former inmate per day (the application), per hour of visiting time (a visit), per day per hired one with a gang past (the old gang leaning on them).</summary>
        public const float ApplyChance = 0.12f, VisitChance = 0.02f, LeanChance = 0.05f;
        /// <summary>Added to the odds that a hired former gang member says yes to the old gang.</summary>
        public const float LeanPressure = 0.25f;
        /// <summary>An upkeep post (cook, workman, gardener) is open while there are fewer on the staff than one per this many inmates (at least one).</summary>
        public static int InmatesPerPost = 40;
        public const float CleanBonus = 8f, TroubleMalus = 4f, ProgramBonus = 6f, TradeBonus = 4f, GangMalus = 12f, GangRankMalus = 6f, DealerMalus = 6f, RingMalus = 4f;

        private static int Now => Find.TickManager.TicksGame;

        /// <summary>The posts a former inmate may be hired into, in the order of the letter's choices: no guard, warden or any role with keys.</summary>
        public static StaffRoleDef[] Roles => new[] { RPStaffRoleDefOf.RP_Cook, RPStaffRoleDefOf.RP_Workman, RPLaborDefOf.RP_Gardener };

        // ---- kept ------------------------------------------------------------------------------------------------

        /// <summary>At release: their record is taken, and one with something to come back for is kept (a reoffender is kept already, for the return).</summary>
        public static void Notify_Released(FormerInmate e, Pawn p, CompInmate c, bool reoffends)
        {
            e.past = ExInmatePast.Of(p, c);
            if (reoffends || !(e.past.programs > 0 || e.past.trade != null || FriendsInside(p, p.MapHeld ?? Find.AnyPlayerHomeMap).Count > 0)) return;
            e.pawn = p;
            Find.WorldPawns.ForcefullyKeptPawns.Add(p);
        }

        /// <summary>The cap: past <see cref="MaxKept"/> kept pawns the oldest released are let go.</summary>
        public static void EnforceCap(PrisonGame game)
        {
            // (One whose application letter is open goes last: a letter whose applicant is gone answers itself.)
            List<FormerInmate> kept = game.story.formerInmates.Where(x => x.pawn != null).OrderBy(x => LetterOpen(x) ? 1 : 0).ThenBy(x => x.releasedTick).ToList();
            for (int i = 0; i < kept.Count - MaxKept; i++) Unkeep(kept[i]);
        }

        public static void Unkeep(FormerInmate e)
        {
            Pawn p = e.pawn;
            if (p == null) return;
            e.pawn = null;
            // (A supervision case still under review, a rearrest or a return waiting for a bed keeps the pawn too: the register lets go of it, and the keep goes only when nobody else holds them.)
            if (!Supervision.HeldElsewhere(PrisonGame.Instance, p)) Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
        }

        public static FormerInmate EntryOf(Pawn p) => p == null ? null : PrisonGame.Instance?.story.formerInmates.Find(e => e.pawn == p);

        /// <summary>Asked for work and not answered yet.</summary>
        public static bool LetterOpen(FormerInmate e) => e.applied && !e.declined && !e.hired;

        /// <summary>Still a story to come: the application window is open, or the letter is out.</summary>
        private static bool StoryOpen(FormerInmate e, int now) => !e.hired && !e.declined && (e.applied || now - e.releasedTick < MaxApplyDays * GenDate.TicksPerDay);

        /// <summary>The inmates they have a friend in (the friend inside relation survives release).</summary>
        public static List<Pawn> FriendsInside(Pawn former, Map map)
        {
            var list = new List<Pawn>();
            if (former?.relations == null || map == null) return list;
            foreach (DirectPawnRelation r in former.relations.DirectRelations)
                if (r.def == RPRelationDefOf.RP_Friend && r.otherPawn is { } o && o != former && o.Spawned && o.Map == map && InmateUtility.IsActiveInmate(o) && o.Inmate() != null) list.Add(o);
            return list;
        }

        /// <summary>Daily: those who are gone or have nothing left to come back for are let go, the application roll, and the old gang's pressure.</summary>
        public static void Daily(PrisonGame game, Map map)
        {
            int now = Now;
            foreach (FormerInmate e in game.story.formerInmates)
            {
                if (e.pawn == null) continue;
                if (e.pawn.Dead || e.pawn.Destroyed || e.pawn.Discarded || (!StoryOpen(e, now) && FriendsInside(e.pawn, map).Count == 0)) Unkeep(e);
            }
            ApplicationRoll(game, map);
            Pressure(game, map);
        }

        // ---- the application -------------------------------------------------------------------------------------

        /// <summary>The posts open to hire into now: unlocked, and fewer on the staff than the prison wants (see <see cref="InmatesPerPost"/>).</summary>
        public static List<StaffRoleDef> Vacancies(Map map)
        {
            var open = new List<StaffRoleDef>();
            int want = Mathf.Max(1, Mathf.CeilToInt(map.mapPawns.PrisonersOfColonySpawnedCount / (float)InmatesPerPost));
            List<Pawn> staff = StaffCommute.AllStaff(map);
            foreach (StaffRoleDef role in Roles)
                if (Unlocks.IsUnlocked(role) && staff.Count(s => s.GetComp<CompStaff>()?.role == role) < want) open.Add(role);
            return open;
        }

        /// <summary>Whether they could do the work of a post (the first of its work types, which is what it is for).</summary>
        public static bool CanDo(Pawn p, StaffRoleDef role) => role.workTypes.Count == 0 || !p.WorkTypeIsDisabled(role.workTypes[0]);

        /// <summary>Their trade is for this post: the skill it was examined in is one a work type of the post is done with.</summary>
        public static bool TradeFits(ProgramDef trade, StaffRoleDef role) =>
            trade?.examSkill != null && role.workTypes.Exists(w => w.relevantSkills != null && w.relevantSkills.Contains(trade.examSkill));

        /// <summary>How well a post suits them: a trade for it first, then their skills in what it asks.</summary>
        public static float Fit(Pawn p, ExInmatePast past, StaffRoleDef role)
        {
            float score = TradeFits(past?.trade, role) ? 100f : 0f;
            foreach (SkillDef s in role.keySkills) score += p.skills?.GetSkill(s)?.Level ?? 0;
            return score;
        }

        private static bool CanApply(FormerInmate e, int now) =>
            e.pawn is { Dead: false, Destroyed: false, Spawned: false } && e.past != null && !e.hired && !e.declined && !e.applied
            && now - e.releasedTick >= MinApplyDays * GenDate.TicksPerDay && now - e.releasedTick <= MaxApplyDays * GenDate.TicksPerDay;

        /// <summary>
        /// The day's roll: each kept former inmate who may apply (not within <see cref="MinApplyDays"/> days of release, not after <see cref="MaxApplyDays"/>, never again after an answer) and
        /// could do the work of an open post asks, at <paramref name="chance"/> (a check's own odds); one letter at a time. Returns the letters sent.
        /// </summary>
        public static int ApplicationRoll(PrisonGame game, Map map, float? chance = null)
        {
            if (StoryLetters.AnyOpen("RP_Decision_ExInmate")) return 0;
            List<StaffRoleDef> vacant = Vacancies(map);
            if (vacant.Count == 0) return 0;
            int now = Now;
            foreach (FormerInmate e in game.story.formerInmates)
            {
                if (!CanApply(e, now) || !vacant.Exists(r => CanDo(e.pawn, r)) || !Rand.Chance(chance ?? ApplyChance)) continue;
                Apply(map, e, vacant);
                return 1;
            }
            return 0;
        }

        private static void Apply(Map map, FormerInmate e, List<StaffRoleDef> vacant)
        {
            Pawn p = e.pawn;
            ExInmatePast past = e.past;
            e.applied = true;
            string fit = vacant.Where(r => CanDo(p, r)).OrderByDescending(r => Fit(p, past, r)).FirstOrDefault() is { } best
                ? "RP_ExInmate_BestFit".Translate(best.label, Money.Format(best.hireCost)).ToString() : "RP_ExInmate_NoFit".Translate().ToString();
            string conduct = past.troubles == 0 ? "RP_ExInmate_ConductClean".Translate().ToString() : "RP_ExInmate_ConductTroubles".Translate(past.troubles).ToString();
            string programs = past.programs > 0 ? "RP_ExInmate_Programs".Translate(past.programs).ToString() : "RP_ExInmate_NoPrograms".Translate().ToString();
            string trade = past.trade != null ? "RP_ExInmate_Trade".Translate(past.trade.label).ToString() : "RP_ExInmate_NoTrade".Translate().ToString();
            string gang = past.gang != null && past.gangKnown ? "RP_ExInmate_Gang".Translate(past.gang.label).ToString() : "RP_ExInmate_NoGang".Translate().ToString();
            int days = Mathf.Max(0, (Now - e.releasedTick) / GenDate.TicksPerDay);
            TaggedString text = "RP_ExInmateText".Translate(p.LabelShort, days, past.daysServed.ToString("0"), past.crime ?? "?", conduct, programs, trade, gang, fit);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_ExInmate", "RP_ExInmateLabel".Translate(p.LabelShort), text, map);
            letter.pawns.Add(p);
            letter.subjectId = p.thingIDNumber;
            letter.StartTimeout(LetterDays * GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>The entry a letter is about (null when they are gone).</summary>
        public static FormerInmate Applicant(ChoiceLetter_PrisonDecision l) =>
            PrisonGame.Instance?.story.formerInmates.Find(e => e.pawn != null && e.pawn.thingIDNumber == l.subjectId);

        /// <summary>Turned down, or left unanswered: never asked again for them.</summary>
        public static void Decline(FormerInmate e)
        {
            e.declined = true;
            // (Kept on only for a friend inside, and a visit.)
            if (e.pawn != null && FriendsInside(e.pawn, Find.AnyPlayerHomeMap).Count == 0) Unkeep(e);
        }

        // ---- the hire --------------------------------------------------------------------------------------------

        /// <summary>How much their record moves the integrity a fresh hire would roll: a clean record, a program and a trade up, trouble, a gang past (more the higher their rank), a dealer's or a ring's past down.</summary>
        public static float IntegrityShift(ExInmatePast past)
        {
            float shift = past.troubles == 0 ? CleanBonus : -TroubleMalus * Mathf.Min(past.troubles, 3);
            if (past.programs > 0) shift += ProgramBonus;
            if (past.trade != null) shift += TradeBonus;
            if (past.gang != null) shift -= GangMalus + GangRankMalus * past.gangRank;
            if (past.dealer) shift -= DealerMalus;
            if (past.ring) shift -= RingMalus;
            return shift;
        }

        /// <summary>The integrity they start with, from a fresh hire's roll and their record.</summary>
        public static float StartingIntegrity(float freshRoll, ExInmatePast past) => Mathf.Clamp(freshRoll + IntegrityShift(past), 5f, 100f);

        /// <summary>Hires the applicant into the post through the staff hire (the hiring fee as for anyone), with integrity from the record. False when they cannot be hired (nothing is changed).</summary>
        public static bool Hire(PrisonGame game, Map map, FormerInmate e, StaffRoleDef role)
        {
            Pawn p = e.pawn;
            if (p == null || p.Dead || p.Destroyed || p.Spawned || e.past == null || e.hired || !Roles.Contains(role)) return false;
            if (!Unlocks.IsUnlocked(role) || !CanDo(p, role) || !game.ledger.CanAfford(role.hireCost)) return false;
            // (Rolled first: the roll is the same as a fresh hire's, and a check can pin it.)
            float integrity = StartingIntegrity(CorruptionSystem.RollIntegrity(p), e.past);
            List<Pawn> friends = FriendsInside(p, map);
            // Out of the world, one of the player's; the keep goes with the world pawn (spawning takes them out of it).
            if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
            Faction faction0 = p.Faction;
            p.SetFaction(Faction.OfPlayer);
            if (p.guest != null)
            {
                p.guest.SetGuestStatus(null);
                p.guest.Released = false;
            }
            p.apparel?.UnlockAll();
            if (!StaffUtility.Hire(map, role, free: false, p, record: false))
            {
                // (Not hired: not one of the player's either. Left in the player's faction, their open supervision case would be voided at its next review as one of the prison's own.)
                if (p.Faction != faction0) p.SetFaction(faction0);
                Find.WorldPawns.ForcefullyKeptPawns.Add(p);
                if (!Find.WorldPawns.Contains(p)) Find.WorldPawns.PassToWorld(p);
                return false;
            }
            CompStaff sc = p.GetComp<CompStaff>();
            sc.formerPast = e.past.Copy();
            sc.integrity = integrity;
            e.hired = true;
            e.pawn = null;
            // (Working here now: no "news from outside" about them, and the parole board's supervision is the prison's own eye on them - their supervision case, if it is still open, is closed with nothing to review.)
            e.newsTick = -1;
            Supervision.Notify_Hired(game, e);
            Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
            foreach (Pawn f in friends) f.needs?.mood?.thoughts.memories.TryGainMemory(RPExInmateDefOf.RP_FriendHired);
            string days = e.past.daysServed.ToString("0");
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_HiredFormer".Translate(p.LabelShort, role.label, days), p);
            Messages.Message("RP_HiredFormer".Translate(p.LabelShort, role.label, days), p, MessageTypeDefOf.PositiveEvent);
            return true;
        }

        // ---- inside reacts ---------------------------------------------------------------------------------------

        /// <summary>The inmate of the gang who would lean on them: the highest rank among those who can be reached.</summary>
        public static Pawn OldGangMate(Map map, GangDef gang)
        {
            Pawn best = null;
            int bestRank = -1;
            foreach (Pawn o in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (o.Inmate() is not { activeFlag: true, gang: { } g } oc || g != gang || o.Downed || o.InMentalState) continue;
                int rank = (int)GangRanks.Of(oc);
                if (rank > bestRank) (best, bestRank) = (o, rank);
            }
            return best;
        }

        /// <summary>
        /// Daily: someone of the old gang inside may lean on a hired former member, at <paramref name="chance"/> (a check's own odds). It is the corruption system's own offer with the odds
        /// raised (<see cref="LeanPressure"/>): yes puts them on the gang's payroll, no may be reported. Returns who was leaned on.
        /// </summary>
        public static int Pressure(PrisonGame game, Map map, float? chance = null)
        {
            int leaned = 0;
            foreach (Pawn s in StaffCommute.AllStaff(map).ToList())
            {
                if (s.GetComp<CompStaff>() is not { formerPast: { gang: { } gang }, corrupt: false, role: not null } sc || sc.integrity < 0f || sc.Protected || sc.leaveAtTick >= 0
                    || s.Faction != Faction.OfPlayer || CorruptionSystem.Suspended(sc) || Now - sc.lastApproachTick < GenDate.TicksPerDay * 2) continue;
                // (Only on site: a member of staff at home has no map, and the offer looks for staff near them - it threw for a cook at home at midnight.)
                if (!s.Spawned || s.Map != map) continue;
                if (!Rand.Chance(chance ?? LeanChance) || OldGangMate(map, gang) is not { } mate) continue;
                CorruptionSystem.Approach(s, sc, mate, LeanPressure);
                leaned++;
            }
            return leaned;
        }

        // ---- the visit -------------------------------------------------------------------------------------------

        /// <summary>The hour's visits from former inmates: kept ones with a friend inside who can be visited now, at <paramref name="chance"/> each (a check's own odds).</summary>
        public static void HourlyVisits(Map map, PrisonGame game, int hour, int maxVisits, int booths, ref int active, ref int boothsInUse, float? chance = null)
        {
            int now = Now;
            foreach (FormerInmate e in game.story.formerInmates)
            {
                if (active >= maxVisits) return;
                if (e.pawn is not { Dead: false, Destroyed: false, Spawned: false } p || e.past == null || e.hired || LetterOpen(e)) continue;
                if (now - e.releasedTick < MinApplyDays * GenDate.TicksPerDay || (e.lastVisitTick >= 0 && now - e.lastVisitTick < VisitCooldownDays * GenDate.TicksPerDay)) continue;
                // (Thorough searches put visitors off, as they do families.)
                if (!Rand.Chance((chance ?? VisitChance) * (game.visitorSearch == VisitorSearchPolicy.Thorough ? 0.7f : 1f))) continue;
                Pawn friend = null;
                VisitPolicy policy = VisitPolicy.None;
                foreach (Pawn f in FriendsInside(p, map))
                    if (VisitationSystem.CanBeVisited(f, f.Inmate(), game, hour, booths, boothsInUse, out policy))
                    {
                        friend = f;
                        break;
                    }
                if (friend == null || !VisitationSystem.SpawnVisitor(map, friend, friend.Inmate(), policy == VisitPolicy.Booth, e)) continue;
                e.lastVisitTick = now;
                active++;
                if (policy == VisitPolicy.Booth) boothsInUse++;
            }
        }

        /// <summary>The kept pawn, ready to spawn as a visitor (outside the gate, not a guest of the prison any more).</summary>
        public static Pawn PrepareVisitor(FormerInmate e, Faction outside)
        {
            Pawn p = e.pawn;
            if (outside != null && p.Faction != outside) p.SetFaction(outside);
            if (p.guest != null) p.guest.Released = false;
            // (Out of the locked uniform the intake put on them: they come as a visitor, not dressed as an inmate of their old wing.)
            p.apparel?.UnlockAll();
            return p;
        }

        /// <summary>On the map as a visitor: kept all the same (spawning let the keep go with the world pawn), so the world takes them back when they leave.</summary>
        public static void KeepWhileOut(Pawn p) => Find.WorldPawns.ForcefullyKeptPawns.Add(p);

        /// <summary>A visit that could not start: back to the world, still kept.</summary>
        public static void ReturnFromVisit(Pawn p)
        {
            KeepWhileOut(p);
            if (p.Spawned) p.DeSpawn();
            if (!Find.WorldPawns.Contains(p)) Find.WorldPawns.PassToWorld(p);
        }

        /// <summary>A visit by a former inmate is more likely to carry something in when they were a dealer or in a ring; a visit that nothing can reach (a supply cut) stays at nothing.</summary>
        public static float SmuggleChance(float chance, ExInmatePast past) => chance <= 0f || !(past.dealer || past.ring) ? chance : Mathf.Min(0.9f, chance * 2.5f);
    }

    /// <summary>The letter of a former inmate asking for work: hire them into one of the upkeep posts, or turn them down for good.</summary>
    public class ExInmateDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_ExInmate_Cook", "RP_ExInmate_Workman", "RP_ExInmate_Gardener", "RP_ExInmate_Decline" };
        private const int DeclineOption = 3;

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        // (Unanswered is a no.)
        public override int TimeoutChoice => DeclineOption;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option == DeclineOption) return null;
            if (ExInmates.Applicant(l) is not { pawn: { Dead: false, Destroyed: false, Spawned: false } p, hired: false })
                return "RP_ExInmate_Gone".Translate();
            StaffRoleDef role = ExInmates.Roles[option];
            if (!Unlocks.IsUnlocked(role)) return "RP_ExInmate_Locked".Translate(role.label);
            if (!ExInmates.CanDo(p, role)) return "RP_ExInmate_Cannot".Translate(p.LabelShort, role.label);
            return PrisonGame.Instance.ledger.CanAfford(role.hireCost) ? null : "RP_CannotAfford".Translate(Money.Format(role.hireCost));
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            FormerInmate e = ExInmates.Applicant(l);
            if (e == null) return;
            if (option != DeclineOption && ExInmates.Hire(PrisonGame.Instance, map, e, ExInmates.Roles[option])) return;
            ExInmates.Decline(e);
        }
    }
}
