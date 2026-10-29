using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPDetoxDefOf
    {
        public static HediffDef RP_SupervisedDetox;
        public static ThoughtDef RP_GotClean;

        static RPDetoxDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPDetoxDefOf));
    }

    /// <summary>
    /// Supervised detox: many arrivals are addicts, and with no drugs inside they go into withdrawal at once (weeks of it,
    /// two or three addictions can keep them down for days). With a doctor on site, every inmate in withdrawal is under
    /// supervision: the worst of it is eased (a hediff that lasts a day past the doctor's last shift) and the addiction
    /// fades twice as fast. Coming off the last addiction under supervision is a chronicle line and a good few days.
    /// </summary>
    public static class Detox
    {
        public const int CoverTicks = GenDate.TicksPerDay + 2 * GenDate.TicksPerHour;
        /// <summary>Extra addiction severity lost per supervised day (vanilla fades most addictions by 0.0333 a day).</summary>
        public const float ExtraFadePerDay = 0.0333f;
        private static readonly List<Hediff_Addiction> tmp = new List<Hediff_Addiction>();

        public static bool DoctorOnSite(Map map)
        {
            foreach (Pawn s in StaffLives.OnSite(map))
                if (s.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Doctor && !s.Downed) return true;
            return false;
        }

        public static bool InWithdrawal(Pawn p)
        {
            List<Hediff> hediffs = p.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
                if (hediffs[i] is Hediff_Addiction a && a.CurStageIndex == 1) return true;
            return false;
        }

        public static Hediff Supervision(Pawn p) => p.health.hediffSet.GetFirstHediffOfDef(RPDetoxDefOf.RP_SupervisedDetox);

        /// <summary>Hourly: with a doctor here, everyone in withdrawal is (still) under supervision.</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            if (!DoctorOnSite(map)) return;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() == null || !InWithdrawal(p)) continue;
                Hediff h = Supervision(p) ?? p.health.AddHediff(RPDetoxDefOf.RP_SupervisedDetox);
                if (h.TryGetComp<HediffComp_Disappears>() is { } d) d.ticksToDisappear = CoverTicks;
                game.stats.detoxHours++;
            }
        }

        /// <summary>Daily: supervised addictions fade faster; coming off the last one is remembered.</summary>
        public static void Daily(PrisonGame game, Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() != null && Supervision(p) is { } h) Fade(game, p, h);
        }

        public static void Fade(PrisonGame game, Pawn p, Hediff supervision)
        {
            tmp.Clear();
            foreach (Hediff x in p.health.hediffSet.hediffs)
                if (x is Hediff_Addiction a) tmp.Add(a);
            // With none left, vanilla's own fade took the last one since the last pass: they are as clean as if this had.
            bool clean = tmp.Count == 0;
            foreach (Hediff_Addiction a in tmp)
            {
                if (a.Severity > ExtraFadePerDay)
                {
                    a.Severity -= ExtraFadePerDay;
                    continue;
                }
                p.health.RemoveHediff(a);
                clean = true;
            }
            tmp.Clear();
            if (!clean || p.health.hediffSet.HasHediff<Hediff_Addiction>()) return;
            // Clean: the supervision ends with them.
            float days = supervision.ageTicks / (float)GenDate.TicksPerDay;
            p.health.RemoveHediff(supervision);
            game.stats.gotClean++;
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPDetoxDefOf.RP_GotClean);
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_GotClean".Translate(p.LabelShort, days.ToString("0")), p);
            Wants.Refresh(p, p.Inmate());
        }
    }
}
