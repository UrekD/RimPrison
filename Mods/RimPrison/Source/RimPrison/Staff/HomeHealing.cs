using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// What a night at home does for a member of staff's health (stability round 31): the staff at home are held by the prison and nothing ticks them, so a wound or an illness they left with was exactly as bad when they came
    /// back. <see cref="Apply"/> gives them the time away, as vanilla would give a pawn lying down with no bed - in hour-sized steps through the public rates, never through the health tick itself (which can kill inside the
    /// call, rolls hediff givers and moves pregnancy and addiction on):
    /// <list type="bullet">
    /// <item>Injuries: vanilla's natural rate lying down (8 a day, 4 more for lying, no bed's), times each stage's <c>naturalHealingFactor</c>, the health scale and the injury healing factor; each step's share goes to one random
    /// injury that can heal (vanilla's heal is spread the same way, one injury per interval, not each); a tended injury gets the tended bonus (8 times 0.5 to 1.5 by the tend's quality) on top while its tend lasts (an injury's
    /// tend is permanent in vanilla: the whole time away). Nothing heals while they are starving. A permanent injury is left as it was.</item>
    /// <item>Bleeding: a wound sent home is taken as bandaged there (no blood is lost off site, and no new infection rolls); the blood loss they left with recovers at vanilla's rate (a third a day), and the wounds and missing parts
    /// are as much older when they come back (one that would have stopped bleeding by then does not bleed again at the gate).</item>
    /// <item>Illnesses they can become immune to (a plain hediff with the immunity comp: flu, plague, wound infection): the severity moves by the comps' rate and the immunity by its own, step by step together (an immunity that
    /// reaches full turns the severity's way, a tend with a duration runs down and helps only while it lasts). The immunity gains at the lying rate (vanilla's resting factor on the gain speed, which the pawn at the gate, standing,
    /// does not have). The severity never reaches the illness's lethal severity nor its first life-threatening or part-destroying stage, unless they left with more: nobody dies at home (a death off site is lost without a word) or
    /// has a part destroyed by it. A chronic illness, with no immunity to gain (asthma has the comp and none to gain), is left as it was (its stages can take a vital capacity to nothing inside the severity's own setter, which no clamp on the severity sees).</item>
    /// <item>Not touched: pregnancy, addiction, a drug's high, a timed hediff (shock, sickness), a hediff with a class of its own and anything else that ticks its own way; what ticks for hours (hediff givers, new illnesses, the rest of a
    /// hediff's comps) does not run.</item>
    /// </list>
    /// </summary>
    public static class HomeHealing
    {
        /// <summary>The step: an hour.</summary>
        public const int StepTicks = GenDate.TicksPerHour;

        /// <summary>The longest time away that counts (a month): a pass costs a few reads per hour, and what a month does not heal does not heal at home either.</summary>
        public const int MaxAwayTicks = 30 * GenDate.TicksPerDay;

        /// <summary>Natural healing a day lying with no bed (vanilla: 8, and 4 more for not standing) and the tended bonus's base; vanilla gives each per interval of <see cref="VanillaInterval"/> ticks, a hundredth of the number in points of health, so a day's worth.</summary>
        public const float NaturalPerDay = 12f, TendedPerDay = 8f;
        private const int VanillaInterval = 600;

        /// <summary>The blood loss that recovers in a day when nothing bleeds (vanilla's giver: a three-thousandth of a point a tick-sixty interval).</summary>
        public const float BloodLossRecoveryPerDay = 0.33333334f;

        /// <summary>How far under the lethal severity or the life-threatening stage an illness is held.</summary>
        public const float Margin = 0.001f;

        /// <summary>What <see cref="Apply"/> did, for a log line and the checks.</summary>
        public struct Result
        {
            public int steps;
            public float injuryHealed;
            public int removed;
        }

        /// <summary>The time away to give: none for a tick that is not before now (an old save, a clock put back), at most <see cref="MaxAwayTicks"/>.</summary>
        public static int AwayTicks(int since, int now) => since < 0 || since >= now ? 0 : Mathf.Min(now - since, MaxAwayTicks);

        /// <summary>Whether the pawn is starving now. The caller reads it as they left, before the spawn (which adds and removes needs as the tracker finds right, and takes the food need off a member of staff) and before the food is topped up.</summary>
        public static bool IsStarving(Pawn p) => p.needs?.food is { Starving: true };

        /// <summary>Gives the pawn (a member of staff, or a handler's dog) <paramref name="away"/> ticks of health at home. <paramref name="starving"/> is how they left (<see cref="IsStarving"/>, read before the spawn): one who left starving has no natural healing.</summary>
        public static Result Apply(Pawn p, int away, bool starving)
        {
            var result = new Result();
            if (p == null || p.Dead || away <= 0 || p.health?.hediffSet == null || !p.RaceProps.IsFlesh) return result;
            // (The severity each illness left with: the most it may go on to, if that is more than the stage that would kill.)
            var started = new Dictionary<Hediff, float>();
            foreach (Hediff h in p.health.hediffSet.hediffs)
                if (Steps(h)) started[h] = h.Severity;
            // (Their records of immunity are made before the first step as the health tick makes them: one for each illness they can become immune to.)
            foreach (Hediff h in started.Keys)
                p.health.immunity.TryAddImmunityRecord(h.def, h.def);
            int left = away;
            while (left > 0 && !p.Dead)
            {
                // (A step ends where a tend with a duration does, so that the tended rate is given for the hours it lasted and no more.)
                int dt = Mathf.Min(Mathf.Min(StepTicks, left), NextTendEnd(p.health.hediffSet, started));
                left -= dt;
                result.steps++;
                if (!Step(p, dt, starving, started, ref result)) break;
            }
            Age(p, away);
            return result;
        }

        /// <summary>The wounds and the missing parts are as much older as the time away (the bleeding of a wound stops by its age, a missing part stops bleeding when it is no longer fresh).</summary>
        private static void Age(Pawn p, int away)
        {
            bool aged = false;
            foreach (Hediff h in p.health.hediffSet.hediffs)
                if (h is Hediff_Injury or Hediff_MissingPart)
                {
                    h.ageTicks += away;
                    aged = true;
                }
            if (aged) p.health.hediffSet.DirtyCache();
        }

        /// <summary>A hediff that is an illness they can become immune to, whose severity moves by its comps' rates and nothing else (a plain hediff with the immunity comp, not an injury, not a class of its own).</summary>
        private static bool Steps(Hediff h)
        {
            // (The immunity comp alone is not enough: a chronic illness like asthma has one with no immunity to gain, and its stages can take a vital capacity to nothing inside the severity's setter.)
            if (h.GetType() != typeof(HediffWithComps) || ((HediffWithComps)h).comps == null || !h.def.PossibleToDevelopImmunityNaturally()) return false;
            foreach (HediffComp c in ((HediffWithComps)h).comps)
                if (c is HediffComp_Immunizable) return true;
            return false;
        }

        /// <summary>A tend that runs down with time (not an injury's, which is permanent) on an injury or an illness that is stepped.</summary>
        private static bool RunsDown(Hediff h, Dictionary<Hediff, float> started, out HediffComp_TendDuration tend)
        {
            tend = null;
            if (h is not HediffWithComps { comps: { } comps } || h is not Hediff_Injury && !started.ContainsKey(h)) return false;
            foreach (HediffComp c in comps)
                if (c is HediffComp_TendDuration { tendTicksLeft: > 0 } td && !td.TProps.TendIsPermanent)
                {
                    tend = td;
                    return true;
                }
            return false;
        }

        /// <summary>The ticks to the first tend that lapses (<see cref="int.MaxValue"/> for none).</summary>
        private static int NextTendEnd(HediffSet set, Dictionary<Hediff, float> started)
        {
            int next = int.MaxValue;
            foreach (Hediff h in set.hediffs)
                if (RunsDown(h, started, out HediffComp_TendDuration td)) next = Mathf.Min(next, td.tendTicksLeft);
            return next;
        }

        /// <summary>The factor the pawn's immunity gain gets for lying down (vanilla's resting part on the gain speed), which one standing does not have; 1 for one who is lying already (the stat has it).</summary>
        public static float LyingImmunityFactor(Pawn p)
        {
            if (p.InBed() || p.GetPosture() != PawnPosture.Standing && !p.Downed) return 1f;
            foreach (StatPart part in StatDefOf.ImmunityGainSpeed.parts)
                if (part is StatPart_Resting resting) return resting.factor;
            return 1f;
        }

        private static float SeverityPerDay(HediffWithComps h)
        {
            float perDay = 0f;
            foreach (HediffComp c in h.comps)
                if (c is HediffComp_SeverityModifierBase m) perDay += m.SeverityChangePerDay();
            return perDay;
        }

        /// <summary>The most an illness's severity may rise to at home: under its lethal severity and its first life-threatening or part-destroying stage, or what they left with if that was more.</summary>
        public static float Ceiling(Hediff h, float started)
        {
            float cap = float.MaxValue;
            if (h.def.lethalSeverity > 0f) cap = h.def.lethalSeverity - Margin;
            if (h.def.stages != null)
                foreach (HediffStage stage in h.def.stages)
                    if (stage.lifeThreatening || stage.destroyPart) cap = Mathf.Min(cap, stage.minSeverity - Margin);
            return Mathf.Max(cap, started);
        }

        /// <summary>One step of <paramref name="dt"/> ticks. False when there is nothing left to move (the rest of the time would change nothing).</summary>
        private static bool Step(Pawn p, int dt, bool starving, Dictionary<Hediff, float> started, ref Result result)
        {
            HediffSet set = p.health.hediffSet;
            bool moved = false;
            float days = dt / (float)GenDate.TicksPerDay;

            // Injuries: one that can heal gets the step's natural share, one that is tended the tended share.
            if (!starving)
            {
                var natural = new List<Hediff_Injury>();
                var tended = new List<Hediff_Injury>();
                foreach (Hediff h in set.hediffs)
                    if (h is Hediff_Injury injury && h.Severity > 0f)
                    {
                        if (injury.CanHealNaturally()) natural.Add(injury);
                        if (injury.CanHealFromTending()) tended.Add(injury);
                    }
                float scale = p.HealthScale * 0.01f * p.GetStatValue(StatDefOf.InjuryHealingFactor) * dt / VanillaInterval;
                if (natural.Count > 0)
                {
                    float rate = NaturalPerDay;
                    foreach (Hediff h in set.hediffs)
                    {
                        HediffStage stage = h.CurStage;
                        if (stage != null && stage.naturalHealingFactor != -1f) rate *= stage.naturalHealingFactor;
                    }
                    Heal(natural.RandomElement(), rate * scale, ref result);
                    moved = true;
                }
                if (tended.Count > 0)
                {
                    Hediff_Injury injury = tended.RandomElement();
                    float quality = injury.TryGetComp<HediffComp_TendDuration>()?.tendQuality ?? 0f;
                    Heal(injury, TendedPerDay * GenMath.LerpDouble(0f, 1f, 0.5f, 1.5f, Mathf.Clamp01(quality)) * scale, ref result);
                    moved = true;
                }
            }

            // Blood loss recovers; nothing bleeds off site.
            if (set.GetFirstHediffOfDef(HediffDefOf.BloodLoss) is { } loss)
            {
                loss.Severity -= BloodLossRecoveryPerDay * days;
                moved = true;
            }

            // Illnesses: severity and immunity together. The severity first (at the immunity they had), then the immunity.
            if (started.Count > 0)
            {
                List<Hediff> sick = null;
                foreach (KeyValuePair<Hediff, float> kv in started)
                    if (set.hediffs.Contains(kv.Key)) (sick ??= new List<Hediff>()).Add(kv.Key);
                if (sick != null)
                {
                    foreach (Hediff h in sick)
                    {
                        float perDay = SeverityPerDay((HediffWithComps)h);
                        if (perDay == 0f) continue;
                        h.Severity = Mathf.Min(h.Severity + perDay * days, Ceiling(h, started[h]));
                        moved = true;
                    }
                    var stepped = new HashSet<HediffDef>();
                    float lying = LyingImmunityFactor(p);
                    foreach (Hediff h in sick)
                        if (stepped.Add(h.def) && Record(p, h.def) is { } record)
                        {
                            record.immunity = Mathf.Clamp01(record.immunity + record.ImmunityChangePerTick(p, sick: true, h) * lying * dt);
                            moved = true;
                        }
                }
            }

            // The tends with a duration run down, and that is progress too (a step in which nothing heals is not the end of the time).
            foreach (Hediff h in set.hediffs)
                if (RunsDown(h, started, out HediffComp_TendDuration td))
                {
                    td.tendTicksLeft = Mathf.Max(0, td.tendTicksLeft - dt);
                    moved = true;
                }

            // What reached nothing is gone, as at the end of a health tick.
            for (int i = set.hediffs.Count - 1; i >= 0; i--)
            {
                Hediff h = set.hediffs[i];
                if (h.ShouldRemove && (h is Hediff_Injury || h.def == HediffDefOf.BloodLoss || started.ContainsKey(h)))
                {
                    p.health.RemoveHediff(h);
                    started.Remove(h);
                    result.removed++;
                }
            }
            return moved;
        }

        private static void Heal(Hediff_Injury injury, float amount, ref Result result)
        {
            float before = injury.Severity;
            injury.Heal(amount);
            result.injuryHealed += before - Mathf.Max(0f, injury.Severity);
        }

        private static ImmunityRecord Record(Pawn p, HediffDef def)
        {
            List<ImmunityRecord> list = p.health.immunity.ImmunityListForReading;
            for (int i = 0; i < list.Count; i++)
                if (list[i].hediffDef == def) return list[i];
            return null;
        }
    }
}
