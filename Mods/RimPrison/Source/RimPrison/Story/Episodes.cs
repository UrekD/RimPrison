using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPEpisodeDefOf
    {
        public static ThoughtDef RP_WrittenUp;
        public static ThoughtDef RP_TruceHeld;

        static RPEpisodeDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPEpisodeDefOf));
    }

    /// <summary>A truce between two rival gangs: while it holds, they are not rivals.</summary>
    public class GangTruce : IExposable
    {
        public GangDef a, b;
        public int untilTick;

        public bool Covers(GangDef x, GangDef y) => (a == x && b == y || a == y && b == x) && untilTick > Find.TickManager.TicksGame;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref a, "a");
            Scribe_Defs.Look(ref b, "b");
            Scribe_Values.Look(ref untilTick, "until");
        }
    }

    /// <summary>Rival leaders ask for a sit-down. Host it (it may end in a brawl) or refuse.</summary>
    public static class GangTruces
    {
        public const int TruceDays = 7;

        public static bool InTruce(GangDef a, GangDef b)
        {
            List<GangTruce> truces = PrisonGame.Instance?.story.truces;
            if (truces == null) return false;
            for (int i = 0; i < truces.Count; i++)
                if (truces[i].Covers(a, b)) return true;
            return false;
        }

        public static void Hourly(PrisonGame game)
        {
            List<GangTruce> truces = game.story.truces;
            for (int i = truces.Count - 1; i >= 0; i--)
            {
                GangTruce t = truces[i];
                if (t.untilTick > Find.TickManager.TicksGame) continue;
                truces.RemoveAt(i);
                Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_TruceEnded".Translate(t.a.label, t.b.label));
                Messages.Message("RP_TruceEnded".Translate(t.a.label, t.b.label), MessageTypeDefOf.CautionInput);
            }
        }

        /// <summary>A fight across a truce breaks it.</summary>
        public static void Notify_Fight(Pawn attacker, Pawn target)
        {
            GangDef ga = attacker.Inmate()?.gang, gb = target.Inmate()?.gang;
            if (ga == null || gb == null || ga == gb) return;
            List<GangTruce> truces = PrisonGame.Instance.story.truces;
            if (truces.RemoveAll(x => x.Covers(ga, gb)) == 0) return;
            PrisonGame.Instance.stats.trucesBroken++;
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_TruceBroken".Translate(ga.label, gb.label, attacker.LabelShort), attacker, target);
            Messages.Message("RP_TruceBroken".Translate(ga.label, gb.label), attacker, MessageTypeDefOf.NegativeEvent);
            WindUp(attacker.Map, ga, gb, 10f);
        }

        public static void WindUp(Map map, GangDef a, GangDef b, float tension)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c && (c.gang == a || c.gang == b))
                    c.tension = Mathf.Clamp(c.tension + tension, 0f, 100f);
        }

        /// <summary>The leaders shook hands: a truce for <see cref="TruceDays"/>, tension down for both gangs.</summary>
        public static void Declare(Map map, GangDef a, GangDef b, Pawn la, Pawn lb)
        {
            PrisonGame game = PrisonGame.Instance;
            game.story.truces.Add(new GangTruce { a = a, b = b, untilTick = Find.TickManager.TicksGame + TruceDays * GenDate.TicksPerDay });
            game.stats.truces++;
            WindUp(map, a, b, -15f);
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true } c && (c.gang == a || c.gang == b)) p.needs.mood?.thoughts.memories.TryGainMemory(RPEpisodeDefOf.RP_TruceHeld);
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_Truce".Translate(a.label, b.label, TruceDays), la, lb);
        }

        public static Pawn LeaderOf(Map map, GangDef g) =>
            map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => InmateUtility.IsActiveInmate(p) && p.Inmate() is { gangLeader: true, gangKnown: true } c && c.gang == g);

        public static float SuccessChance(Map map, Pawn la, Pawn lb)
        {
            int guards = StaffLives.OnSite(map).Count(GuardUtility.IsGuard);
            return Mathf.Clamp(0.5f + 0.05f * Mathf.Min(guards, 4) + la.relations.OpinionOf(lb) / 200f, 0.2f, 0.9f);
        }
    }

    public class IncidentWorker_GangTruce : IncidentWorker_Prison
    {
        protected override int MinInmates => 4;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            foreach (GangDef a in DefDatabase<GangDef>.AllDefsListForReading.InRandomOrder())
                foreach (GangDef b in a.rivals)
                {
                    if (GangTruces.InTruce(a, b) || GangTruces.LeaderOf(map, a) is not { } la || GangTruces.LeaderOf(map, b) is not { } lb) continue;
                    ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Truce", "RP_TruceLabel".Translate(a.LabelCap, b.LabelCap),
                        "RP_TruceText".Translate(la.LabelShort, a.label, lb.LabelShort, b.label, GangTruces.TruceDays, GangTruces.SuccessChance(map, la, lb).ToStringPercent()), map, la);
                    letter.pawns.Add(la);
                    letter.pawns.Add(lb);
                    letter.StartTimeout(GenDate.TicksPerHour * 12);
                    Find.LetterStack.ReceiveLetter(letter);
                    return true;
                }
            return false;
        }
    }

    public class TruceDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Truce_Host", "RP_Truce_Refuse" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && (l.pawns.Count < 2 || l.pawns.Any(p => p == null || !p.Spawned || !InmateUtility.IsActiveInmate(p) || p.Inmate().gang == null)) ? "RP_TruceGone".Translate().ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (l.pawns.Count < 2 || l.pawns[0] == null || l.pawns[1] == null) return;
            Pawn la = l.pawns[0], lb = l.pawns[1];
            GangDef a = la.Inmate()?.gang, b = lb.Inmate()?.gang;
            if (a == null || b == null || a == b || GangTruces.InTruce(a, b)) return;
            if (option == 1)
            {
                GangTruces.WindUp(map, a, b, 5f);
                return;
            }
            if (Rand.Chance(GangTruces.SuccessChance(map, la, lb)))
            {
                GangTruces.Declare(map, a, b, la, lb);
                return;
            }
            // The sit-down goes wrong: words, then fists, and both gangs hear about it.
            PrisonRelations.MakeEnemies(la, lb);
            GangTruces.WindUp(map, a, b, 10f);
            if (la.Spawned && lb.Spawned && la.GetRoom() == lb.GetRoom() && !la.Downed && !lb.Downed)
                la.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_AttackingInmate, null, forced: true, otherPawn: lb);
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_TruceFailed".Translate(a.label, b.label, la.LabelShort, lb.LabelShort), la, lb);
        }
    }

    /// <summary>
    /// A rookie officer (first days on the job) was talked into bringing an inmate a phone. A senior officer noticed:
    /// search and write them up, pay for retraining, or let it slide (the phone stays, and so does the lesson).
    /// </summary>
    public class IncidentWorker_RookieConned : IncidentWorker_Prison
    {
        public const float RookieDays = 4f;
        public const int RetrainCost = 150;

        private static IEnumerable<Pawn> Rookies(Map map) =>
            StaffLives.Storied(map).Where(p => GuardUtility.IsGuard(p) && p.GetComp<CompStaff>() is { corrupt: false, rookieConned: false } sc && StaffLives.DaysEmployed(sc) < RookieDays);

        protected override bool CanFireNowSub(IncidentParms parms) => base.CanFireNowSub(parms) && Rookies((Map)parms.target).Any();

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            Pawn rookie = Rookies(map).RandomElementWithFallback();
            ThingDef phone = RPContrabandDefOf.RP_Cellphone;
            if (rookie == null) return false;
            Pawn inmate = RandomInmate(map, x => x.Inmate().tension >= 25f || x.Inmate().cash >= 10);
            if (inmate == null || ContrabandUtility.Give(inmate, phone) == null) return false;
            rookie.GetComp<CompStaff>().rookieConned = true;
            PrisonGame.Instance.stats.rookieMistakes++;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Rookie", "RP_RookieLabel".Translate(rookie.LabelShort),
                "RP_RookieText".Translate(rookie.LabelShort, inmate.LabelShort, Money.Format(RetrainCost)), map, rookie);
            letter.pawns.Add(rookie);
            letter.pawns.Add(inmate);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
            return true;
        }
    }

    public class RookieDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Rookie_WriteUp", "RP_Rookie_Retrain", "RP_Rookie_Slide" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 1 && PrisonGame.Instance.ledger.funds < IncidentWorker_RookieConned.RetrainCost
                ? "RP_CannotAfford".Translate(Money.Format(IncidentWorker_RookieConned.RetrainCost)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn rookie = l.pawns.FirstOrDefault(p => p is { Dead: false } && p.GetComp<CompStaff>()?.role != null);
            Pawn inmate = l.pawns.FirstOrDefault(p => p != null && p.IsPrisonerOfColony && p.Inmate() != null);
            CompStaff sc = rookie?.GetComp<CompStaff>();
            // Integrity is 0..100 (fixers approach under 65).
            if (option == 2)
            {
                if (sc != null && sc.integrity >= 0f) sc.integrity = Mathf.Max(0f, sc.integrity - 10f);
                return;
            }
            if (inmate != null && InmateUtility.IsActiveInmate(inmate)) inmate.Inmate().suspected = true;
            if (sc == null) return;
            if (option == 0)
            {
                if (sc.integrity >= 0f) sc.integrity = Mathf.Min(100f, sc.integrity + 5f);
                rookie.needs?.mood?.thoughts.memories.TryGainMemory(RPEpisodeDefOf.RP_WrittenUp);
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_RookieWrittenUp".Translate(rookie.LabelShort), rookie);
            }
            else
            {
                PrisonGame.Instance.ledger.Add(RPDefOf.RP_Wages, -IncidentWorker_RookieConned.RetrainCost, "RP_RetrainNote".Translate(rookie.LabelShort));
                if (sc.integrity >= 0f) sc.integrity = Mathf.Min(100f, sc.integrity + 10f);
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_RookieRetrained".Translate(rookie.LabelShort), rookie);
            }
        }
    }

    /// <summary>
    /// Flu on the wing: it spreads between inmates who share a room. Vaccinate everyone, isolate the sick in their
    /// cells, or let the doctors cope. The outbreak burns out after a few days.
    /// </summary>
    public static class Outbreaks
    {
        public const int OutbreakDays = 5, VaccineCost = 20, IsolateHours = 48;
        public static float SpreadChance = 0.02f;
        public const int CloseRoomCells = 40;
        public const float ContactRange = 3f;
        public static HediffDef Flu => RPVanillaDefOf.Flu;

        public static bool Active => PrisonGame.Instance.story.outbreakUntilTick > Find.TickManager.TicksGame;

        public static bool Sick(Pawn p) => p.health.hediffSet.GetFirstHediffOfDef(Flu) is { } h && !(h.TryGetComp<HediffComp_Immunizable>()?.FullyImmune ?? false);

        public static void Hourly(Map map)
        {
            StorySystem story = PrisonGame.Instance.story;
            if (!Active || story.outbreakVaccinated) return;
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            var sick = new List<Pawn>();
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { activeFlag: true } && Sick(inmates[i])) sick.Add(inmates[i]);
            foreach (Pawn s in sick)
            {
                if (story.outbreakIsolate) Isolate(s);
                Room room = s.GetRoom();
                if (room == null) continue;
                // Isolated and not yet back in the cell: being escorted, not mixing.
                if (story.outbreakIsolate && s.Inmate().forcedActivity == RPDefOf.RP_Lockup && room != InmateUtility.CellOf(s)) continue;
                // Close contact: the same cell or small room; in the yard, canteen and other big spaces, a few steps.
                bool bigRoom = room.PsychologicallyOutdoors || room.CellCount > CloseRoomCells;
                for (int i = 0; i < inmates.Count; i++)
                {
                    Pawn o = inmates[i];
                    if (o == s || o.GetRoom() != room || bigRoom && !o.Position.InHorDistOf(s.Position, ContactRange)) continue;
                    if (o.Inmate() is not { activeFlag: true } || o.health.hediffSet.HasHediff(Flu) || !Rand.Chance(SpreadChance)) continue;
                    o.health.AddHediff(Flu);
                    PrisonGame.Instance.stats.fluCases++;
                }
            }
        }

        public static void Isolate(Pawn p)
        {
            CompInmate c = p.Inmate();
            if (c == null || c.forcedActivity != null) return;
            c.forcedActivity = RPDefOf.RP_Lockup;
            c.forcedUntilTick = Find.TickManager.TicksGame + IsolateHours * GenDate.TicksPerHour;
        }
    }

    public class IncidentWorker_Outbreak : IncidentWorker_Prison
    {
        protected override int MinInmates => 6;

        protected override bool CanFireNowSub(IncidentParms parms) => base.CanFireNowSub(parms) && !Outbreaks.Active;

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            Pawn zero = RandomInmate(map, x => !x.health.hediffSet.HasHediff(Outbreaks.Flu));
            if (zero == null) return false;
            zero.health.AddHediff(Outbreaks.Flu);
            StorySystem story = PrisonGame.Instance.story;
            story.outbreakUntilTick = Find.TickManager.TicksGame + Outbreaks.OutbreakDays * GenDate.TicksPerDay;
            story.outbreakVaccinated = story.outbreakIsolate = false;
            PrisonGame.Instance.stats.fluCases++;
            int heads = map.mapPawns.PrisonersOfColonySpawnedCount;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Outbreak", "RP_OutbreakLabel".Translate(),
                "RP_OutbreakText".Translate(zero.LabelShort, Money.Format(heads * Outbreaks.VaccineCost), Outbreaks.IsolateHours), map, zero);
            letter.money = heads * Outbreaks.VaccineCost;
            letter.pawns.Add(zero);
            letter.StartTimeout(GenDate.TicksPerHour * 8);
            Find.LetterStack.ReceiveLetter(letter);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_Outbreak".Translate(zero.LabelShort), zero);
            return true;
        }
    }

    public class OutbreakDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Outbreak_Vaccinate", "RP_Outbreak_Isolate", "RP_Outbreak_Doctors" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) =>
            option == 0 && PrisonGame.Instance.ledger.funds < l.money ? "RP_CannotAfford".Translate(Money.Format(l.money)).ToString() : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            StorySystem story = PrisonGame.Instance.story;
            if (option == 0)
            {
                PrisonGame.Instance.ledger.Add(RPDefOf.RP_Supplies, -l.money, "RP_VaccineNote".Translate());
                story.outbreakVaccinated = true;
            }
            else if (option == 1)
            {
                story.outbreakIsolate = true;
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (p.Inmate() is { activeFlag: true } && Outbreaks.Sick(p)) Outbreaks.Isolate(p);
            }
        }
    }
}
