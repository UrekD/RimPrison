using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>A state grant: accept it for an advance, meet its conditions for the payout. Also the onboarding path.</summary>
    public class GrantDef : Def
    {
        public int advance;
        public int reward;
        public int uiOrder;
        public List<GrantDef> prerequisites = new List<GrantDef>();
        public List<GrantCondition> conditions = new List<GrantCondition>();
    }

    /// <summary>One requirement of a grant. Subclasses are configured in XML.</summary>
    public abstract class GrantCondition
    {
        /// <summary>Defs the condition needs; the grant is only offered once all are unlocked.</summary>
        public virtual IEnumerable<Def> RequiredDefs => Enumerable.Empty<Def>();

        /// <summary>Current progress for display, e.g. "6 / 8 cells".</summary>
        public abstract string Progress(Map map, PrisonGame game, GrantState state);

        public abstract bool Met(Map map, PrisonGame game, GrantState state);

        /// <summary>Can no longer be met (the grant is withdrawn).</summary>
        public virtual bool Failed(Map map, PrisonGame game, GrantState state) => false;

        /// <summary>Called on acceptance: remember baselines ("since accepting").</summary>
        public virtual void OnAccepted(Map map, PrisonGame game, GrantState state)
        {
        }
    }

    public class GrantCondition_CellBeds : GrantCondition
    {
        public int count = 8;
        public override string Progress(Map map, PrisonGame game, GrantState s) => "RP_GrantCells".Translate(IntakeSystem.Capacity(map), count);
        public override bool Met(Map map, PrisonGame game, GrantState s) => IntakeSystem.Capacity(map) >= count;
    }

    public class GrantCondition_Zone : GrantCondition
    {
        public PrisonZoneDef zone;
        public int cells = 12;
        public override IEnumerable<Def> RequiredDefs { get { yield return zone; } }
        public override string Progress(Map map, PrisonGame game, GrantState s) => "RP_GrantZone".Translate(zone.label, PrisonMap.Of(map).zones.CellsOf(zone).Count, cells);
        public override bool Met(Map map, PrisonGame game, GrantState s) => PrisonMap.Of(map).zones.CellsOf(zone).Count >= cells;
    }

    public class GrantCondition_Buildings : GrantCondition
    {
        public ThingDef thing;
        public int count = 1;
        public PrisonZoneDef inZone;
        public override IEnumerable<Def> RequiredDefs { get { yield return thing; if (inZone != null) yield return inZone; } }

        private int Count(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            int n = 0;
            foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(thing))
                if (inZone == null || pm.zones.ZoneAt(b.Position) == inZone) n++;
            return n;
        }

        public override string Progress(Map map, PrisonGame game, GrantState s) => "RP_GrantBuildings".Translate(thing.label, Count(map), count);
        public override bool Met(Map map, PrisonGame game, GrantState s) => Count(map) >= count;
    }

    public class GrantCondition_Staff : GrantCondition
    {
        public StaffRoleDef role;
        public int count = 1;
        public override IEnumerable<Def> RequiredDefs { get { yield return role; } }

        private static int Count(Map map, StaffRoleDef role) => StaffCommute.AllStaff(map).Count(p => p.GetComp<CompStaff>()?.role == role);
        public override string Progress(Map map, PrisonGame game, GrantState s) => "RP_GrantStaff".Translate(role.label, Count(map, role), count);
        public override bool Met(Map map, PrisonGame game, GrantState s) => Count(map, role) >= count;
    }

    public class GrantCondition_Inmates : GrantCondition
    {
        public SecurityLevelDef security;
        public int count = 10;
        public override IEnumerable<Def> RequiredDefs { get { if (security != null) yield return security; } }

        private int Count(Map map) => map.mapPawns.PrisonersOfColonySpawned.Count(p => security == null || p.Inmate()?.security == security);
        public override string Progress(Map map, PrisonGame game, GrantState s) => "RP_GrantInmates".Translate(security?.label ?? "RP_AnySecurity".Translate().ToString(), Count(map), count);
        public override bool Met(Map map, PrisonGame game, GrantState s) => Count(map) >= count;
    }

    /// <summary>A PrisonStats counter must rise by <see cref="amount"/> after the grant is accepted.</summary>
    public class GrantCondition_StatGain : GrantCondition
    {
        public string stat;
        public int amount = 1;
        [Unsaved] private System.Reflection.FieldInfo field;

        private int Value(PrisonGame game)
        {
            field ??= AccessTools.Field(typeof(PrisonStats), stat);
            return field == null ? 0 : (int)field.GetValue(game.stats);
        }

        public override void OnAccepted(Map map, PrisonGame game, GrantState s) => s.baselines[stat] = Value(game);

        private int Gain(PrisonGame game, GrantState s) => Value(game) - (s.baselines.TryGetValue(stat, out int b) ? b : 0);
        public override string Progress(Map map, PrisonGame game, GrantState s) => "RP_GrantStat".Translate(("RP_Stat_" + stat).Translate(), Gain(game, s), amount);
        public override bool Met(Map map, PrisonGame game, GrantState s) => Gain(game, s) >= amount;
    }

    /// <summary>Hold a minimum report-card grade for N consecutive days.</summary>
    public class GrantCondition_GradeDays : GrantCondition
    {
        public float minGrade = 70f;
        public int days = 3;
        public override string Progress(Map map, PrisonGame game, GrantState s) => "RP_GrantGrade".Translate(PrisonGrading.Letter(minGrade), s.goodDays, days);
        public override bool Met(Map map, PrisonGame game, GrantState s) => s.goodDays >= days;
    }

    /// <summary>No escapes for N days since accepting.</summary>
    public class GrantCondition_NoEscapes : GrantCondition
    {
        public int days = 5;

        public override void OnAccepted(Map map, PrisonGame game, GrantState s) => s.baselines["escaped"] = game.stats.escaped;

        private bool Clean(PrisonGame game, GrantState s) => game.stats.escaped == (s.baselines.TryGetValue("escaped", out int b) ? b : 0);
        private float DaysSince(GrantState s) => (Find.TickManager.TicksGame - s.acceptedTick) / (float)GenDate.TicksPerDay;
        public override string Progress(Map map, PrisonGame game, GrantState s) =>
            Clean(game, s) ? "RP_GrantNoEscapes".Translate(DaysSince(s).ToString("F1"), days) : "RP_GrantNoEscapesFailed".Translate();
        public override bool Met(Map map, PrisonGame game, GrantState s) => Clean(game, s) && DaysSince(s) >= days;
        public override bool Failed(Map map, PrisonGame game, GrantState s) => !Clean(game, s);
    }

    public class GrantState : IExposable
    {
        public GrantDef def;
        public int acceptedTick;
        public int goodDays;
        public Dictionary<string, int> baselines = new Dictionary<string, int>();

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref acceptedTick, "acceptedTick");
            Scribe_Values.Look(ref goodDays, "goodDays");
            Scribe_Collections.Look(ref baselines, "baselines", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) baselines ??= new Dictionary<string, int>();
        }
    }

    public class GrantSystem : IExposable
    {
        public List<GrantState> active = new List<GrantState>();
        public List<GrantDef> completed = new List<GrantDef>();

        public IEnumerable<GrantDef> Available =>
            DefDatabase<GrantDef>.AllDefsListForReading.Where(g => !completed.Contains(g) && !active.Any(a => a.def == g) && g.prerequisites.All(completed.Contains)
                                                        && g.conditions.All(c => c.RequiredDefs.All(Unlocks.IsUnlocked))).OrderBy(g => g.uiOrder);

        public void Accept(GrantDef def, Map map)
        {
            var state = new GrantState { def = def, acceptedTick = Find.TickManager.TicksGame };
            foreach (GrantCondition c in def.conditions) c.OnAccepted(map, PrisonGame.Instance, state);
            active.Add(state);
            if (def.advance > 0) PrisonGame.Instance.ledger.Add(RPDefOf.RP_Grants, def.advance, "RP_GrantAdvanceNote".Translate(def.label));
        }

        /// <summary>Abandoned by the warden or withdrawn by the state: the advance goes back (else accept, abandon and
        /// accept again would pay it every time); the grant can be taken up again later, from the start.</summary>
        public void Drop(GrantState s, bool failed)
        {
            active.Remove(s);
            PrisonGame game = PrisonGame.Instance;
            if (s.def.advance > 0) game.ledger.Add(RPDefOf.RP_Grants, -s.def.advance, "RP_GrantAdvanceBackNote".Translate(s.def.label));
            if (failed)
                Find.LetterStack.ReceiveLetter("RP_GrantFailedLabel".Translate(), "RP_GrantFailedText".Translate(s.def.label, Money.Format(s.def.advance)), LetterDefOf.NegativeEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, (failed ? "RP_Chr_GrantFailed" : "RP_Chr_GrantAbandoned").Translate(s.def.label));
        }

        /// <summary>Hourly: pay out finished grants; withdraw failed ones.</summary>
        public void Check(Map map)
        {
            PrisonGame game = PrisonGame.Instance;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                GrantState s = active[i];
                if (s.def == null)
                {
                    active.RemoveAt(i);
                    continue;
                }
                if (s.def.conditions.Any(c => c.Failed(map, game, s)))
                {
                    Drop(s, failed: true);
                    continue;
                }
                bool met = true;
                foreach (GrantCondition c in s.def.conditions)
                    if (!c.Met(map, game, s)) met = false;
                if (!met) continue;
                active.RemoveAt(i);
                completed.Add(s.def);
                int reward = SpecialMeasures.GrantReward(s.def.reward);
                game.ledger.Add(RPDefOf.RP_Grants, reward, "RP_GrantRewardNote".Translate(s.def.label));
                game.stats.grantsCompleted++;
                Find.LetterStack.ReceiveLetter("RP_GrantDoneLabel".Translate(), "RP_GrantDoneText".Translate(s.def.label, Money.Format(reward)), LetterDefOf.PositiveEvent);
            }
        }

        /// <summary>Daily, after the report card: grade streaks.</summary>
        public void Notify_DailyGrade(float grade)
        {
            foreach (GrantState s in active)
                foreach (GrantCondition c in s.def.conditions)
                    if (c is GrantCondition_GradeDays g) s.goodDays = grade >= g.minGrade ? s.goodDays + 1 : 0;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref active, "active", LookMode.Deep);
            Scribe_Collections.Look(ref completed, "completed", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                active ??= new List<GrantState>();
                active.RemoveAll(a => a?.def == null);
                completed ??= new List<GrantDef>();
                completed.RemoveAll(g => g == null);
            }
        }
    }
}
