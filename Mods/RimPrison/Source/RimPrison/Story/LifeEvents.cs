using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPLifeDefOf
    {
        public static ThoughtDef RP_FamilyDied;
        public static ThoughtDef RP_NewInFamily;
        public static ThoughtDef RP_PartnerLeft;
        public static ThoughtDef RP_JobWaiting;
        public static ThoughtDef RP_ColleagueHurt;
        public static ThoughtDef RP_Commended;
        public static ThoughtDef RP_HazardPay;

        static RPLifeDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPLifeDefOf));
    }

    /// <summary>Someone on the outside, by name: who visits, who writes, who dies.</summary>
    public class Relative : IExposable
    {
        public string name;
        /// <summary>Keyed kind ("RP_Kin_Daughter"...).</summary>
        public string kind;
        public bool alive = true;
        public bool estranged;

        public bool CanVisit => alive && !estranged;

        public string Label => "RP_KinLabel".Translate(kind.Translate(), name);

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "n");
            Scribe_Values.Look(ref kind, "k");
            Scribe_Values.Look(ref alive, "a", true);
            Scribe_Values.Look(ref estranged, "e");
        }
    }

    public static class Relatives
    {
        private static readonly (string kind, Gender gender)[] Kinds =
        {
            ("RP_Kin_Mother", Gender.Female), ("RP_Kin_Father", Gender.Male), ("RP_Kin_Sister", Gender.Female), ("RP_Kin_Brother", Gender.Male),
            ("RP_Kin_Daughter", Gender.Female), ("RP_Kin_Son", Gender.Male), ("RP_Kin_Partner", Gender.None), ("RP_Kin_Cousin", Gender.None),
        };

        /// <summary>Names the family behind <see cref="CompInmate.familyCount"/> (once; lazily for older saves).</summary>
        public static List<Relative> Of(CompInmate c)
        {
            if (c.relatives.Count == 0 && c.familyCount > 0)
                for (int i = 0; i < c.familyCount; i++) c.relatives.Add(Make(i == 0 && Rand.Chance(0.5f) ? "RP_Kin_Partner" : null));
            return c.relatives;
        }

        public static Relative Make(string kind = null)
        {
            (string k, Gender g) = Kinds.RandomElement();
            if (kind != null) k = kind;
            if (g == Gender.None) g = Rand.Bool ? Gender.Female : Gender.Male;
            string name = PawnNameDatabaseShuffled.BankOf(PawnNameCategory.HumanStandard).GetName(PawnNameSlot.First, g, checkIfAlreadyUsed: false);
            return new Relative { name = name, kind = k };
        }

        /// <summary>A relative who can visit or write, and is not an outside supplier the warden has barred (<see cref="RingSystem.SupplierBarred"/>).</summary>
        public static Relative RandomVisitor(CompInmate c)
        {
            List<Relative> rel = Of(c);
            return rel.Where(r => r.CanVisit && !RingSystem.SupplierBarred(r.Label)).TryRandomElement(out Relative r0) ? r0 : null;
        }

        /// <summary>The inmate has family who could come, and every one of them is barred.</summary>
        public static bool AllBarred(CompInmate c) => Of(c).Exists(r => r.CanVisit) && !Of(c).Exists(r => r.CanVisit && !RingSystem.SupplierBarred(r.Label));

        /// <summary>Living, in-touch family (what familyCount means from now on).</summary>
        public static void Recount(CompInmate c) => c.familyCount = Of(c).Count(r => r.CanVisit);
    }

    /// <summary>
    /// News from home about someone with a name: a death, a birth, a partner who leaves, money sent in, a job waiting
    /// outside. Grief and heartbreak are tension; good news is mood, and a job waiting makes reoffending less likely.
    /// </summary>
    public class IncidentWorker_NewsFromHome : IncidentWorker_Prison
    {
        internal enum News { Death, Birth, Leaving, Money, Job }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Pawn p = RandomInmate((Map)parms.target, x => Relatives.Of(x.Inmate()).Exists(r => r.CanVisit));
            if (p == null) return false;
            CompInmate c = p.Inmate();
            Relative partner = Relatives.Of(c).Find(r => r.kind == "RP_Kin_Partner" && r.CanVisit);
            var options = new List<(News, float)> { (News.Death, 0.25f), (News.Birth, 0.2f), (News.Money, 0.2f), (News.Job, c.jobWaiting ? 0f : 0.15f), (News.Leaving, partner != null ? 0.2f : 0f) };
            return Deliver(p, c, options.RandomElementByWeight(o => o.Item2).Item1);
        }

        /// <summary>The news reaches them (a letter, the chronicle, the goal that hangs on the family); someone in touch with them must be there to have it.</summary>
        internal static bool Deliver(Pawn p, CompInmate c, News news)
        {
            List<Relative> rel = Relatives.Of(c);
            Relative partner = rel.Find(r => r.kind == "RP_Kin_Partner" && r.CanVisit);
            Relative who = rel.FindAll(r => r.CanVisit).RandomElement();
            TaggedString text;
            LetterDef letter = LetterDefOf.NeutralEvent;
            int importance = 1;
            switch (news)
            {
                case News.Death:
                    who.alive = false;
                    c.tension = Mathf.Min(100f, c.tension + 35f);
                    c.mainCause = "RP_Cause_Grief";
                    p.needs.mood?.thoughts.memories.TryGainMemory(RPLifeDefOf.RP_FamilyDied);
                    text = "RP_News_Death".Translate(p.LabelShort, who.Label);
                    letter = LetterDefOf.NegativeEvent;
                    importance = 2;
                    break;
                case News.Birth:
                    Relative baby = Relatives.Make(Rand.Bool ? "RP_Kin_Grandchild" : "RP_Kin_Niece");
                    rel.Add(baby);
                    p.needs.mood?.thoughts.memories.TryGainMemory(RPLifeDefOf.RP_NewInFamily);
                    if (p.needs.TryGetNeed(RPVisitDefOf.RP_Family) is { } fam) fam.CurLevel += 0.3f;
                    text = "RP_News_Birth".Translate(p.LabelShort, baby.name, who.Label);
                    letter = LetterDefOf.PositiveEvent;
                    break;
                case News.Leaving:
                    who = partner;
                    partner.estranged = true;
                    c.tension = Mathf.Min(100f, c.tension + 20f);
                    c.mainCause = "RP_Cause_Heartbreak";
                    p.needs.mood?.thoughts.memories.TryGainMemory(RPLifeDefOf.RP_PartnerLeft);
                    text = "RP_News_Leaving".Translate(p.LabelShort, partner.Label);
                    letter = LetterDefOf.NegativeEvent;
                    importance = 2;
                    break;
                case News.Money:
                    int cash = Rand.RangeInclusive(30, 80);
                    c.cash += cash;
                    text = "RP_News_Money".Translate(p.LabelShort, who.Label, Money.Format(cash));
                    break;
                default:
                    c.jobWaiting = true;
                    p.needs.mood?.thoughts.memories.TryGainMemory(RPLifeDefOf.RP_JobWaiting);
                    text = "RP_News_Job".Translate(p.LabelShort, who.Label);
                    letter = LetterDefOf.PositiveEvent;
                    break;
            }
            Relatives.Recount(c);
            Find.LetterStack.ReceiveLetter("RP_NewsLabel".Translate(p.LabelShort), text, letter, p);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, importance, text, p);
            if (news != News.Money && news != News.Job) Wants.Notify_FamilyNews(p, c, news == News.Death ? FamilyNews.Death : news == News.Birth ? FamilyNews.Birth : FamilyNews.Leaving);
            return true;
        }
    }

    /// <summary>
    /// The press want a story: likelier after a dramatic week (milestones in the chronicle). Give them an interview,
    /// refuse (secretive), or stage it (a lockdown during the visit: tidy, but everyone can tell). The tone follows
    /// how the prison is actually doing.
    /// </summary>
    public class IncidentWorker_PressInterest : IncidentWorker_Prison
    {
        protected override int MinInmates => 3;

        public static int RecentMilestones(PrisonGame game)
        {
            int since = Find.TickManager.TicksGame - 5 * GenDate.TicksPerDay, n = 0;
            List<ChronicleEntry> all = game.story.entries;
            for (int i = all.Count - 1; i >= 0 && all[i].tick >= since; i--)
                if (all[i].importance >= 3) n++;
            return n;
        }

        public override float ChanceFactorNow(IIncidentTarget target) =>
            base.ChanceFactorNow(target) * (PrisonGame.Instance == null ? 1f : 0.3f + 0.5f * Mathf.Min(4, RecentMilestones(PrisonGame.Instance)));

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = (Map)parms.target;
            Send(map, PressDecision.Topic(PrisonGame.Instance));
            return true;
        }

        /// <summary>The press's request on a topic: the incident's own, and the one a deal with a gang's leader that got out brings (<see cref="GangDeals"/>).</summary>
        public static void Send(Map map, string topic)
        {
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Press", "RP_PressLabel".Translate(),
                "RP_PressText".Translate(topic), map);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
        }
    }

    public class PressDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Press_Interview", "RP_Press_Refuse", "RP_Press_Stage" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        /// <summary>What the story is about: the most dramatic thing lately, or the prison itself.</summary>
        public static string Topic(PrisonGame game)
        {
            ChronicleEntry best = null;
            int since = Find.TickManager.TicksGame - 5 * GenDate.TicksPerDay;
            foreach (ChronicleEntry e in game.story.entries)
                if (e.tick >= since && e.importance >= 2 && (best == null || e.importance >= best.importance)) best = e;
            return best?.text ?? "RP_PressNoTopic".Translate().ToString();
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            float grade = game.lastGrade >= 0f ? game.lastGrade : 50f;
            float delta;
            string key;
            switch (option)
            {
                case 0:
                    // An open door: the story is whatever the prison really is.
                    delta = grade >= 70f ? 3f : grade <= 45f ? -4f : 0.5f;
                    key = grade >= 70f ? "RP_Press_Good" : grade <= 45f ? "RP_Press_Bad" : "RP_Press_Fair";
                    // A deal with a gang's leader is what the prison really is, and the open door shows it.
                    if (GangDeals.Any)
                    {
                        delta -= GangDeals.PressPenalty;
                        key = "RP_Press_Deal";
                    }
                    break;
                case 2:
                    game.lockdownUntilTick = Mathf.Max(game.lockdownUntilTick, Find.TickManager.TicksGame + 4 * GenDate.TicksPerHour);
                    foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                        if (p.Inmate() is { } c) c.tension = Mathf.Min(100f, c.tension + 5f);
                    delta = 1f;
                    key = "RP_Press_Staged";
                    break;
                default:
                    delta = -1f;
                    key = "RP_Press_Refused";
                    break;
            }
            game.reputation = Mathf.Clamp(game.reputation + delta, 0f, 100f);
            TaggedString headline = key.Translate(delta.ToString("+0.#;-0.#"));
            Messages.Message(headline, delta >= 0f ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 2, headline);
        }
    }

    /// <summary>
    /// Staff in harm's way: a member of staff downed by an inmate shakes everyone (mood) and the staff ask for hazard
    /// pay; a guard who subdues an armed inmate is commended.
    /// </summary>
    public static class StaffStories
    {
        public const int HazardPayPerHead = 40;

        /// <summary>Whom a hazard pay demand is for: the permanent staff, the warden not among them (they are on no payroll).</summary>
        public static int HazardHeads(Map map) => map.mapPawns.FreeColonistsSpawned.Count(o => o.GetComp<CompStaff>() is { role: { warden: false }, leaveAtTick: < 0 });

        public static void Notify_StaffDowned(Pawn staff, Pawn attacker)
        {
            if (staff.Map == null || staff.GetComp<CompStaff>() is not { role: not null } sc || sc.leaveAtTick >= 0) return;
            PrisonGame game = PrisonGame.Instance;
            game.stats.staffInjured++;
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_StaffHurt".Translate(staff.LabelShort, StaffCareers.Title(sc), attacker.LabelShort), staff, attacker);
            StaffWariness.Notify_HurtBy(staff, sc, attacker);
            foreach (Pawn o in staff.Map.mapPawns.FreeColonistsSpawned)
                if (o != staff && o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(RPLifeDefOf.RP_ColleagueHurt, staff);
            // One hazard pay demand at a time, and not every scuffle.
            int now = Find.TickManager.TicksGame;
            if (now - game.story.lastHazardDemandTick < 3 * GenDate.TicksPerDay) return;
            game.story.lastHazardDemandTick = now;
            int heads = HazardHeads(staff.Map);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_HazardPay", "RP_HazardLabel".Translate(),
                "RP_HazardText".Translate(staff.LabelShort, Money.Format(heads * HazardPayPerHead)), staff.Map, staff);
            letter.money = heads * HazardPayPerHead;
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>A guard restrained an inmate with a weapon in hand.</summary>
        public static void Notify_SubduedArmed(Pawn guard, Pawn inmate)
        {
            if (guard.GetComp<CompStaff>() is not { } sc) return;
            sc.commendations++;
            PrisonGame.Instance.stats.commendations++;
            guard.needs?.mood?.thoughts.memories.TryGainMemory(RPLifeDefOf.RP_Commended);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_Commended".Translate(guard.LabelShort, inmate.LabelShort, inmate.equipment?.Primary?.LabelNoCount ?? "?"), guard, inmate);
        }
    }

    public class PrisonDecisionWorker_HazardPay : PrisonDecisionWorker
    {
        public override void Accept(ChoiceLetter_PrisonDecision l, Map map)
        {
            if (!PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Wages, l.money, "RP_HazardNote".Translate()))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(l.money)), MessageTypeDefOf.RejectInput, historical: false);
                Decline(l, map);
                return;
            }
            foreach (Pawn o in map.mapPawns.FreeColonistsSpawned)
                if (o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(RPLifeDefOf.RP_HazardPay);
        }

        public override void Decline(ChoiceLetter_PrisonDecision l, Map map)
        {
            // Nobody walks out over it today; it sits with them (a strike gets likelier as their mood drops).
            foreach (Pawn o in map.mapPawns.FreeColonistsSpawned)
                if (o.GetComp<CompStaff>() is { role: not null, integrity: >= 0f } sc) sc.integrity = Mathf.Max(0f, sc.integrity - 3f); // -1: not rolled yet
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
    public static class Patch_MakeDowned_StaffHurt
    {
        public static void Postfix(Pawn ___pawn, DamageInfo? dinfo)
        {
            if (PrisonGame.Instance == null || ___pawn.Faction != Faction.OfPlayer || dinfo?.Instigator is not Pawn attacker || !attacker.IsPrisonerOfColony) return;
            StaffStories.Notify_StaffDowned(___pawn, attacker);
        }
    }
}
