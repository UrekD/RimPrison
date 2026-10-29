using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPGangWarDefOf
    {
        public static StoryArcDef RP_Arc_GangWar;

        static RPGangWarDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPGangWarDefOf));
    }

    /// <summary>
    /// Two rival gangs at war, from the gang war incident. The warden is asked what to do (lock the wings down, host
    /// talks, ship a leader out, or let it play out); then every day or so a member of one gang jumps a member of the
    /// other when they meet, preferably where no guard can see. Three attacks in, it escalates: everyone is on edge and
    /// the warden is asked again (and failed talks escalate it at once). It ends in a truce, when a leader leaves, when
    /// a gang has nobody left inside, or when both gangs have run out of anger. The cast is a leader (or any member) of
    /// each gang, but the war is between the gangs (the two defNames, and the gang that struck last, are in
    /// <see cref="StoryArc.note"/>), so it outlives any one member. If either gang holds turf, the war is fought over a patch of it (the
    /// arc's <see cref="StoryArc.stake"/>): attacks prefer the stake, then turf of either gang, and when the war ends a patch the invaders
    /// raided more than the holders answered goes to them (<see cref="GangTurf.SettleWar"/>), with its own line.
    /// </summary>
    public class GangWarArc : StoryArcWorker
    {
        public const int LockdownHours = 12, WearyHours = 48;
        public const float AttackTension = 5f, EscalationTension = 10f, WearyTension = 30f, HarderTalks = 0.2f;
        public const int Declared = 1, TitForTat = 2, Escalated = 3;
        /// <summary>The "not counting" value of <see cref="StoryArc.markTick"/> (any other value, negative or not, is a tick).</summary>
        public const int NoMark = -1;
        private const int AttacksToEscalate = 3;
        private static readonly string[] Keys = { "RP_GangWar_Lockdown", "RP_GangWar_Talks", "RP_GangWar_Transfer", "RP_GangWar_Wait" };

        /// <summary>The gangs of a war (from its note, "a|b" and, once someone has struck, "a|b|last"); false when the note or a def is gone.</summary>
        public static bool Gangs(StoryArc arc, out GangDef a, out GangDef b)
        {
            a = b = null;
            string[] parts = arc.note?.Split('|');
            if (parts == null || parts.Length < 2) return false;
            a = DefDatabase<GangDef>.GetNamedSilentFail(parts[0]);
            b = DefDatabase<GangDef>.GetNamedSilentFail(parts[1]);
            return a != null && b != null;
        }

        /// <summary>The gang that struck last (null before the first attack).</summary>
        private static GangDef LastStriker(StoryArc arc)
        {
            string[] parts = arc.note?.Split('|');
            return parts != null && parts.Length > 2 ? DefDatabase<GangDef>.GetNamedSilentFail(parts[2]) : null;
        }

        /// <summary>Does gang <paramref name="a"/> go first: the gang that did not strike last does, whatever the count (a goes first before anyone has struck).</summary>
        public static bool AFirst(StoryArc arc, GangDef a) => LastStriker(arc) != a;

        private static string Note(GangDef a, GangDef b, GangDef last) => last == null ? a.defName + "|" + b.defName : a.defName + "|" + b.defName + "|" + last.defName;

        private static int NextAttackTick(int now) => now + Rand.RangeInclusive(12, 36) * GenDate.TicksPerHour;

        /// <summary>Is there a war running between these two (either way round)?</summary>
        public static bool AtWar(GangDef a, GangDef b)
        {
            List<StoryArc> arcs = PrisonGame.Instance?.story.arcs;
            if (arcs == null) return false;
            for (int i = 0; i < arcs.Count; i++)
                if (!arcs[i].done && arcs[i].def == RPGangWarDefOf.RP_Arc_GangWar && Gangs(arcs[i], out GangDef x, out GangDef y) && (x == a && y == b || x == b && y == a)) return true;
            return false;
        }

        /// <summary>No war between them yet (and neither gang under a shot-caller's deal: see <see cref="GangDeals"/>), room for another war, and someone from each gang to cast.</summary>
        public static bool CanStart(Map map, GangDef a, GangDef b)
        {
            if (GangDeals.Holds(a) || GangDeals.Holds(b) || AtWar(a, b) || Representative(map, a, null) == null || Representative(map, b, null) == null) return false;
            int running = 0;
            foreach (StoryArc arc in PrisonGame.Instance.story.arcs)
                if (!arc.done && arc.def == RPGangWarDefOf.RP_Arc_GangWar) running++;
            return running < RPGangWarDefOf.RP_Arc_GangWar.maxConcurrent;
        }

        /// <summary>Starts the war (the warden is asked once <see cref="Declare"/> is called), or null if it cannot start.</summary>
        public static StoryArc Start(Map map, GangDef a, GangDef b)
        {
            if (!CanStart(map, a, b)) return null;
            StoryArc arc = PrisonGame.Instance.story.TryStartArc(RPGangWarDefOf.RP_Arc_GangWar, Representative(map, a, null), Representative(map, b, null));
            if (arc == null) return null;
            arc.note = Note(a, b, null);
            arc.stage = Declared;
            arc.stake = GangTurf.PickStake(map, a, b);
            // If the letter is never answered the war starts anyway, once its time is up.
            arc.nextTick = Find.TickManager.TicksGame + (arc.def.decisionHours + 1) * GenDate.TicksPerHour;
            PrisonGame.Instance.stats.gangWars++;
            return arc;
        }

        /// <summary>An inmate who can be cast or talked to: active, and not waiting for the gallows.</summary>
        private static bool Eligible(Pawn p) => InmateUtility.IsActiveInmate(p) && DeathRowSystem.Ext(p.Inmate()) == null;

        /// <summary>
        /// The gang's leader, else any member, who is not yet in a story (and is not <paramref name="except"/>). A stand-in
        /// (<paramref name="knownOnly"/>) must be someone the warden knows is in the gang, or the war would give them away.
        /// </summary>
        private static Pawn Representative(Map map, GangDef g, Pawn except, bool knownOnly = false)
        {
            Pawn member = null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p == except || !Eligible(p) || p.Inmate().gang != g || knownOnly && !Known(p) || PrisonGame.Instance.story.InArc(p)) continue;
                if (p.Inmate().gangLeader) return p;
                member ??= p;
            }
            return member;
        }

        /// <summary>The warden knows this inmate's gang (as the file shows it): only then may a line or letter name it.</summary>
        private static bool Known(Pawn p) => p?.Inmate() is { gang: not null, gangKnown: true };

        /// <summary>The gang's leader as far as the warden knows (like <see cref="GangTruces.LeaderOf"/>): talks and transfers only consider known leaders.</summary>
        public static Pawn LeaderOf(Map map, GangDef g)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (Eligible(p) && p.Inmate() is { gangLeader: true, gangKnown: true } c && c.gang == g) return p;
            return null;
        }

        /// <summary>The leader a transfer sends away: the wilder of the two, or the only one inside.</summary>
        private static Pawn TransferLeader(Map map, GangDef a, GangDef b)
        {
            Pawn la = LeaderOf(map, a), lb = LeaderOf(map, b);
            return la == null ? lb : lb == null ? la : la.Inmate().tension >= lb.Inmate().tension ? la : lb;
        }

        private static TaggedString TransferWho(Map map, GangDef a, GangDef b) =>
            TransferLeader(map, a, b) is { } l ? "RP_GangWar_LeaderNow".Translate(l.LabelShort, l.Inmate().gang.label) : "RP_GangWar_LeaderNone".Translate();

        /// <summary>Someone from the gang is inside, also while carried (to a bed, to solitary).</summary>
        private static bool HasMembers(GangDef g)
        {
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                    if (p.Inmate()?.gang == g && !p.guest.Released && !p.Dead) return true;
            }
            return false;
        }

        private static Map MapOf(StoryArc arc) => arc.A?.MapHeld ?? Find.AnyPlayerHomeMap;

        /// <summary>Sends the first decision letter, which is the war's only letter (<paramref name="members"/> inmates are wound up).</summary>
        public void Declare(StoryArc arc, Map map, int members)
        {
            if (!Gangs(arc, out GangDef a, out GangDef b)) return;
            TaggedString text = "RP_GangWarText".Translate(a.label, b.label, members, LockdownHours, Money.Format(InmateUtility.TransferCost), TransferWho(map, a, b));
            // (Both gangs were just revealed, so the place they fight over may be named.)
            if (GangTurf.StakeName(map, arc.stake) is { } place) text += "\n\n" + "RP_GangWarStake".Translate(place);
            AskWarden(arc, map, "RP_GangWarLabel".Translate(a.LabelCap, b.LabelCap), text);
        }

        /// <summary>The war goes on while both gangs have someone inside; a leader or cast member leaving is <see cref="Notify_Leaving"/>.</summary>
        public override bool Valid(StoryArc arc) => Gangs(arc, out GangDef a, out GangDef b) && HasMembers(a) && HasMembers(b);

        public override void Notify_Leaving(StoryArc arc, Pawn p)
        {
            int seat = arc.pawns.IndexOf(p);
            CompInmate c = p?.Inmate();
            GangDef g = c?.gang;
            bool ofWar = false;
            if (g != null && Gangs(arc, out GangDef a, out GangDef b) && (g == a || g == b))
            {
                if (c.gangLeader)
                {
                    arc.done = true;
                    // (An unknown leader leaving is told without their name or gang, and the line is not put on their file.)
                    if (c.gangKnown) Finish(arc, "RP_Chr_GangWarEnd_Leader", a, b, p.LabelShort, g.label);
                    else
                    {
                        arc.pawns.RemoveAll(x => x == p);
                        Finish(arc, "RP_Chr_GangWarEnd_LeaderUnknown", a, b, "", "");
                    }
                    arc.pawns.RemoveAll(x => x == p); // nobody keeps a reference to a pawn about to be discarded
                    return;
                }
                ofWar = true;
            }
            // Out of the cast whatever their gang is now (it may have changed since); a member of the war's gangs is replaced.
            arc.pawns.RemoveAll(x => x == p);
            if (seat >= 0 && ofWar && (p.MapHeld ?? Find.AnyPlayerHomeMap) is { } map && Representative(map, g, p, knownOnly: true) is { } stand)
                arc.pawns.Insert(Mathf.Min(seat, arc.pawns.Count), stand);
        }

        /// <summary>One gang has nobody left inside.</summary>
        public override void Cancelled(StoryArc arc)
        {
            if (!Gangs(arc, out GangDef a, out GangDef b)) return;
            GangDef empty = HasMembers(a) ? b : a;
            Finish(arc, "RP_Chr_GangWarEnd_Empty", a, b, "", empty.label);
        }

        private void End(StoryArc arc, string key, GangDef a, GangDef b)
        {
            arc.done = true;
            Finish(arc, key, a, b, "", "");
        }

        private static void Finish(StoryArc arc, string key, GangDef a, GangDef b, string who, string gang)
        {
            PrisonGame.Instance.stats.gangWarsEnded++;
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, key.Translate(a.label, b.label, who, gang), arc.pawns.ToArray());
            // The stake: what the war won or lost (a truce settles nothing).
            if (MapOf(arc) is { } map) GangTurf.SettleWar(map, a, b, GangTurf.IsPeace(key));
        }

        public override IReadOnlyList<string> Options(StoryArc arc) => Keys;

        public override int TimeoutChoice(StoryArc arc) => 3;

        public override string Disabled(StoryArc arc, int option)
        {
            if (!Gangs(arc, out GangDef a, out GangDef b) || MapOf(arc) is not { } map) return null;
            if (option == 1 && (LeaderOf(map, a) == null || LeaderOf(map, b) == null)) return "RP_GangWar_TalksNoLeader".Translate();
            if (option != 2) return null;
            if (LeaderOf(map, a) == null && LeaderOf(map, b) == null) return "RP_GangWar_NoLeader".Translate();
            return PrisonGame.Instance.ledger.funds < InmateUtility.TransferCost ? "RP_CannotAfford".Translate(Money.Format(InmateUtility.TransferCost)).ToString() : null;
        }

        public override void Choose(StoryArc arc, Map map, int option)
        {
            if (!Gangs(arc, out GangDef a, out GangDef b) || EndIfTruce(arc, a, b)) return;
            switch (option)
            {
                case 0:
                    LockDown(arc, map, a, b);
                    break;
                case 1:
                    Pawn la = LeaderOf(map, a), lb = LeaderOf(map, b);
                    if (la == null || lb == null) Messages.Message("RP_GangWar_TalksNoLeader".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                    else HostTalks(arc, map, Rand.Chance(TalksChance(arc, map, la, lb)));
                    break;
                case 2:
                    // The wilder of the two leaders goes (the letter says who); on success Notify_Leaving ends the war.
                    Pawn leader = TransferLeader(map, a, b);
                    if (leader == null) Messages.Message("RP_GangWar_NoLeader".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                    else InmateUtility.TransferOut(leader, leader.Inmate());
                    break;
                default:
                    StartFighting(arc, Find.TickManager.TicksGame);
                    break;
            }
        }

        /// <summary>The leaders' odds of a truce: the usual, and harder once the war has escalated.</summary>
        public static float TalksChance(StoryArc arc, Map map, Pawn la, Pawn lb) =>
            Mathf.Clamp(GangTruces.SuccessChance(map, la, lb) - (arc.stage >= Escalated ? HarderTalks : 0f), 0.05f, 0.9f);

        /// <summary>A truce from elsewhere (a sit-down the leaders asked for) is the end of the war too, as soon as it is seen.</summary>
        private bool EndIfTruce(StoryArc arc, GangDef a, GangDef b)
        {
            if (!GangTruces.InTruce(a, b)) return false;
            End(arc, "RP_Chr_GangWarEnd_Truce", a, b);
            return true;
        }

        /// <summary>The sit-down between the leaders: a truce ends the war, a failure escalates it at once.</summary>
        public void HostTalks(StoryArc arc, Map map, bool success)
        {
            if (!Gangs(arc, out GangDef a, out GangDef b) || EndIfTruce(arc, a, b)) return;
            Pawn la = LeaderOf(map, a), lb = LeaderOf(map, b);
            if (success)
            {
                GangTruces.Declare(map, a, b, la, lb);
                End(arc, "RP_Chr_GangWarEnd_Truce", a, b);
                return;
            }
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_GangWarTalksFailed".Translate(a.label, b.label), la, lb);
            if (arc.stage < Escalated) Escalate(arc, map, a, b);
            else GangTruces.WindUp(map, a, b, EscalationTension);
        }

        /// <summary>Both gangs' wings locked down (only the wings of members the warden knows are in a gang); the first attack is not due until it is over.</summary>
        private void LockDown(StoryArc arc, Map map, GangDef a, GangDef b)
        {
            var wings = new HashSet<SecurityLevelDef>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true, gangKnown: true, security: { } sec } c && (c.gang == a || c.gang == b)) wings.Add(sec);
            foreach (SecurityLevelDef sec in wings) PrisonGame.Instance.StartWingLockdown(sec, LockdownHours);
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_GangWarLockdown".Translate(a.label, b.label, LockdownHours));
            StartFighting(arc, Find.TickManager.TicksGame + LockdownHours * GenDate.TicksPerHour);
        }

        /// <summary>The warden has answered (or not): the war moves on to tit-for-tat, first attack due after <paramref name="from"/>.</summary>
        private void StartFighting(StoryArc arc, int from)
        {
            if (arc.stage == Declared)
            {
                arc.stage = TitForTat;
                arc.nextTick = NextAttackTick(from);
            }
            else arc.nextTick = Mathf.Max(arc.nextTick, from);
        }

        /// <summary>Everyone is on edge and the warden is asked again (from the third attack, or straight from failed talks).</summary>
        private void Escalate(StoryArc arc, Map map, GangDef a, GangDef b)
        {
            // (From the declaration, the first attack was due once the letter timed out: it is due a day or so from now instead.)
            if (arc.stage == Declared) arc.nextTick = NextAttackTick(Find.TickManager.TicksGame);
            arc.stage = Escalated;
            GangTruces.WindUp(map, a, b, EscalationTension);
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_GangWarEscalated".Translate(a.label, b.label));
            AskWarden(arc, map, "RP_GangWarEscalatedLabel".Translate(a.LabelCap, b.LabelCap),
                "RP_GangWarEscalatedText".Translate(a.label, b.label, Money.Format(InmateUtility.TransferCost), TransferWho(map, a, b)));
        }

        public override void Hourly(StoryArc arc, Map map)
        {
            if (!Gangs(arc, out GangDef a, out GangDef b)) return;
            int now = Find.TickManager.TicksGame;
            if (EndIfTruce(arc, a, b)) return;
            // A cast member who is gone without Notify_Leaving (dead, discarded) is dealt with now; a leader ends the war, and once it is over nobody else counts.
            foreach (Pawn p in arc.pawns.ToArray())
            {
                if (p != null && !p.Dead && !p.Destroyed) continue;
                Notify_Leaving(arc, p);
                if (arc.done) return;
            }
            if (arc.stage == Declared)
            {
                if (now >= arc.nextTick) StartFighting(arc, now);
                return;
            }
            if (WarWeary(arc, map, a, b, now))
            {
                End(arc, "RP_Chr_GangWarEnd_Weary", a, b);
                return;
            }
            if (now < arc.nextTick || !TryAttack(arc, map, a, b, now)) return;
            arc.count++;
            arc.nextTick = NextAttackTick(now);
            if (arc.stage == TitForTat && arc.count >= AttacksToEscalate) Escalate(arc, map, a, b);
        }

        /// <summary>Every member of both gangs below <see cref="WearyTension"/> for <see cref="WearyHours"/> straight (tracked in the arc's mark).</summary>
        private static bool WarWeary(StoryArc arc, Map map, GangDef a, GangDef b, int now)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c && (c.gang == a || c.gang == b) && c.tension >= WearyTension)
                {
                    arc.markTick = NoMark;
                    return false;
                }
            if (arc.markTick == NoMark) arc.markTick = now;
            return now - arc.markTick >= WearyHours * GenDate.TicksPerHour;
        }

        /// <summary>
        /// A member of one gang goes for a member of the other in the same room (the gang that did not strike last
        /// goes first, else the other; the last striker is kept in the arc's note). Where no guard can see is preferred; a watched fight only happens once the
        /// attack is half a day overdue.
        /// </summary>
        private static bool TryAttack(StoryArc arc, Map map, GangDef a, GangDef b, int now)
        {
            bool aFirst = AFirst(arc, a), overdue = now - arc.nextTick > 12 * GenDate.TicksPerHour;
            Pawn attacker = null, victim = null;
            bool started = false;
            // (If the first gang's attacker cannot take the state, the other gang's pair gets its turn.)
            foreach ((GangDef side, GangDef rival) in aFirst ? new[] { (a, b), (b, a) } : new[] { (b, a), (a, b) })
                if (PickFight(map, side, rival, overdue, arc.stake, out attacker, out victim)
                    && attacker.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_AttackingInmate, null, forced: true, otherPawn: victim))
                {
                    started = true;
                    break;
                }
            if (!started) return false;
            ContrabandUtility.Notify_ViolenceStarting(attacker);
            CompInmate ac = attacker.Inmate();
            ac.mainCause = "RP_Cause_GangWar";
            GangDef from = ac.gang, to = victim.Inmate().gang;
            arc.note = Note(a, b, from);
            GangTurf.Notify_WarBlow(map, victim.Position, from, to);
            // (The war's own act is not loud: out of every guard's and camera's sight nothing is seen, and nobody is revealed. Seen, it puts the
            // attacker's gang on their file: so it is recorded before the line, which names only the gangs the warden knows.)
            MisconductUtility.Record(attacker, ac, PrisonMap.Of(map), RPMisconductDefOf.RP_Ambush, chronicle: false);
            // (A victim whose gang the warden does not know is not on the record, which would give it away.)
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, AttackLine(attacker, from, victim, to), Known(victim) ? new[] { attacker, victim } : new[] { attacker });
            GangTruces.WindUp(map, a, b, AttackTension);
            return true;
        }

        /// <summary>"X of the Ravens went for Y of the Saints", naming a side only when the warden knows that inmate's gang.</summary>
        private static string AttackLine(Pawn attacker, GangDef from, Pawn victim, GangDef to)
        {
            bool fromKnown = Known(attacker), toKnown = Known(victim);
            if (fromKnown && toKnown) return "RP_Chr_GangWarAttack".Translate(attacker.LabelShort, from.label, victim.LabelShort, to.label);
            if (fromKnown) return "RP_Chr_GangWarAttack_AttackerKnown".Translate(attacker.LabelShort, from.label, victim.LabelShort);
            if (toKnown) return "RP_Chr_GangWarAttack_VictimKnown".Translate(attacker.LabelShort, victim.LabelShort, to.label);
            return "RP_Chr_GangWarAttack_Unknown".Translate(attacker.LabelShort, victim.LabelShort);
        }

        /// <summary>Picks a pair at random out of those on the war's ground when there are any (the stake first, then turf of either gang), else of any.</summary>
        private static bool PickFight(Map map, GangDef from, GangDef to, bool allowWatched, int stake, out Pawn attacker, out Pawn victim)
        {
            attacker = victim = null;
            PrisonMap pm = PrisonMap.Of(map);
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            Pawn watchedAttacker = null, watchedVictim = null;
            // (Per group, the best ground seen so far and how many pairs there were on it.)
            int unwatched = 0, watched = 0, unwatchedRank = -1, watchedRank = -1;
            // The other gang's members who could be hit, found once (in the inmates' order) instead of by every attacker walking every inmate.
            var targets = new List<Pawn>();
            for (int j = 0; j < inmates.Count; j++)
                if (inmates[j].Inmate()?.gang == to && Ready(inmates[j])) targets.Add(inmates[j]);
            for (int i = 0; i < inmates.Count && targets.Count > 0; i++)
            {
                Pawn x = inmates[i];
                if (x.Inmate()?.gang != from || !Ready(x) || x.WorkTagIsDisabled(WorkTags.Violent)) continue;
                // (As the record counts it, the one seen rule: any guard, camera or tower that sees them, however little.)
                bool seen = TensionUtility.Watched(x, pm);
                for (int j = 0; j < targets.Count; j++)
                {
                    Pawn y = targets[j];
                    if (x.GetRoom() is not { } room || room != y.GetRoom()
                        || !x.Position.InHorDistOf(y.Position, 12f) || !GenSight.LineOfSight(x.Position, y.Position, map)) continue;
                    // Reservoir pick: every candidate pair on the best ground is equally likely.
                    int rank = GangTurf.WarRank(pm, y.Position, from, to, stake);
                    if (!seen)
                    {
                        if (rank > unwatchedRank) (unwatchedRank, unwatched) = (rank, 0);
                        if (rank == unwatchedRank && Rand.Range(0, ++unwatched) == 0) (attacker, victim) = (x, y);
                    }
                    else
                    {
                        if (rank > watchedRank) (watchedRank, watched) = (rank, 0);
                        if (rank == watchedRank && Rand.Range(0, ++watched) == 0) (watchedAttacker, watchedVictim) = (x, y);
                    }
                }
            }
            if (attacker != null) return true;
            if (!allowWatched || watchedAttacker == null) return false;
            (attacker, victim) = (watchedAttacker, watchedVictim);
            return true;
        }

        private static bool Ready(Pawn p) => InmateUtility.IsActiveInmate(p) && !p.Downed && !p.InMentalState && p.Awake();
    }
}
