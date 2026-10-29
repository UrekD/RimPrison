using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    public class LedgerEntry : IExposable
    {
        public int tick;
        public LedgerCategoryDef category;
        public int amount;
        public string note;

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Defs.Look(ref category, "category");
            Scribe_Values.Look(ref amount, "amount");
            Scribe_Values.Look(ref note, "note");
        }
    }

    /// <summary>Totals per category for one day.</summary>
    public class DayBook : IExposable
    {
        public int day;
        public Dictionary<LedgerCategoryDef, int> totals = new Dictionary<LedgerCategoryDef, int>();

        public int Income
        {
            get
            {
                int sum = 0;
                foreach (KeyValuePair<LedgerCategoryDef, int> kv in totals) if (kv.Value > 0) sum += kv.Value;
                return sum;
            }
        }

        public int Expenses
        {
            get
            {
                int sum = 0;
                foreach (KeyValuePair<LedgerCategoryDef, int> kv in totals) if (kv.Value < 0) sum -= kv.Value;
                return sum;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref day, "day");
            Scribe_Collections.Look(ref totals, "totals", LookMode.Def, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                totals ??= new Dictionary<LedgerCategoryDef, int>();
                totals.RemoveAll(kv => kv.Key == null);
            }
        }
    }

    /// <summary>The prison's money: balance, recent transactions and per-day books.</summary>
    public class Ledger : IExposable
    {
        public const int MaxRecent = 200;
        public const int MaxDays = 60;

        public int funds;
        public List<LedgerEntry> recent = new List<LedgerEntry>();
        public List<DayBook> days = new List<DayBook>();

        /// <summary>Local day at the prison (settlement runs at local midnight).</summary>
        public static int CurrentDay
        {
            get
            {
                Map home = Find.AnyPlayerHomeMap;
                return home == null ? GenDate.DaysPassed : GenLocalDate.DayOfYear(home) + GenLocalDate.Year(home) * GenDate.DaysPerYear;
            }
        }

        public DayBook Today
        {
            get
            {
                int day = CurrentDay;
                if (days.Count == 0 || days[days.Count - 1].day != day)
                {
                    days.Add(new DayBook { day = day });
                    if (days.Count > MaxDays) days.RemoveAt(0);
                }
                return days[days.Count - 1];
            }
        }

        public DayBook Yesterday => days.Count >= 2 && days[days.Count - 1].day == CurrentDay ? days[days.Count - 2] :
            days.Count >= 1 && days[days.Count - 1].day == CurrentDay - 1 ? days[days.Count - 1] : null;

        public bool CanAfford(int amount) => amount <= 0 || funds >= amount;

        /// <summary>Records a signed amount (income positive, expense negative).</summary>
        public void Add(LedgerCategoryDef category, int amount, string note = null)
        {
            if (amount == 0) return;
            funds += amount;
            DayBook today = Today;
            today.totals.TryGetValue(category, out int cur);
            today.totals[category] = cur + amount;
            recent.Add(new LedgerEntry { tick = Find.TickManager.TicksGame, category = category, amount = amount, note = note });
            if (recent.Count > MaxRecent) recent.RemoveRange(0, recent.Count - MaxRecent);
        }

        /// <summary>Spends if the balance covers it; returns false (and records nothing) otherwise.</summary>
        public bool TrySpend(LedgerCategoryDef category, int amount, string note = null)
        {
            if (!CanAfford(amount)) return false;
            Add(category, -amount, note);
            return true;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref funds, "funds");
            Scribe_Collections.Look(ref recent, "recent", LookMode.Deep);
            Scribe_Collections.Look(ref days, "days", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                recent ??= new List<LedgerEntry>();
                recent.RemoveAll(e => e?.category == null);
                days ??= new List<DayBook>();
            }
        }
    }

    public static class Money
    {
        public static string Format(int amount) => (amount < 0 ? "-$" : "$") + System.Math.Abs(amount).ToString("N0");
        public static string FormatSigned(int amount) => (amount >= 0 ? "+" : "") + Format(amount);
    }
}
