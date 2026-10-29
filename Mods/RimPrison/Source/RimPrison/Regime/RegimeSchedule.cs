using System.Collections.Generic;
using Verse;

namespace RimPrison
{
    /// <summary>24 hourly regime slots for one security level.</summary>
    public class RegimeSchedule : IExposable
    {
        public List<RegimeActivityDef> slots = new List<RegimeActivityDef>();

        public bool IsValid => slots != null && slots.Count == 24 && !slots.Contains(null);

        public RegimeActivityDef At(int hour) => slots[hour];

        public void Set(int hour, RegimeActivityDef def) => slots[hour] = def;

        public static RegimeSchedule FromDefault(SecurityLevelDef sec)
        {
            var s = new RegimeSchedule();
            for (int h = 0; h < 24; h++)
                s.slots.Add(h < sec.defaultRegime.Count && sec.defaultRegime[h] != null ? sec.defaultRegime[h] : RPDefOf.RP_Lockup);
            return s;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref slots, "slots", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                slots ??= new List<RegimeActivityDef>();
                for (int i = 0; i < slots.Count; i++) slots[i] ??= RPDefOf.RP_Lockup;
                while (slots.Count < 24) slots.Add(RPDefOf.RP_Lockup);
            }
        }
    }
}
