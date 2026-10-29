using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>A memory of the inmate economy: it says what it is about only once the warden knows (see <see cref="EconomyThoughts"/>).</summary>
    public interface IEconomyThought
    {
        bool Shown { get; set; }
    }

    /// <summary>
    /// Vanilla shows every memory with a mood effect in the Needs tab, and a social one (with its label and the other pawn) in the Social tab. The economy's memories would
    /// give away what the warden has not been told: a debt, a card game nobody saw, a protection racket. So each reads as something generic (the def's own stage text) until
    /// the thing is known; then it reads as what it is (the keyed strings <c>RP_Thought_{def}_Label</c> and <c>_Description</c>). The mood and opinion offsets do not change.
    /// </summary>
    public static class EconomyThoughts
    {
        public static string Label(Thought t) => ("RP_Thought_" + t.def.defName + "_Label").Translate().CapitalizeFirst();

        public static string Description(Thought t) => ("RP_Thought_" + t.def.defName + "_Description").Translate();

        /// <summary>Gives the memory (hidden unless <paramref name="shown"/>); one already there for the same pawn takes the new event's visibility.</summary>
        public static void Gain(Pawn p, ThoughtDef def, Pawn other, bool shown)
        {
            MemoryThoughtHandler memories = p?.needs?.mood?.thoughts.memories;
            if (memories == null) return;
            memories.TryGainMemory(def, other);
            foreach (Thought_Memory m in memories.Memories)
                if (m.def == def && m is IEconomyThought e && (other == null || m.otherPawn == other)) e.Shown = shown;
        }

        /// <summary>The warden knows now: this pawn's memories about that other one read as what they are.</summary>
        public static void Reveal(Pawn p, Pawn other)
        {
            if (p?.needs?.mood?.thoughts.memories is not { } memories) return;
            foreach (Thought_Memory m in memories.Memories)
                if (m is IEconomyThought e && (m.otherPawn == null || m.otherPawn == other)) e.Shown = true;
        }
    }

    public class Thought_EconomyMemory : Thought_Memory, IEconomyThought
    {
        private bool shown;

        public bool Shown
        {
            get => shown;
            set => shown = value;
        }

        public override string LabelCap => shown ? EconomyThoughts.Label(this) : base.LabelCap;

        public override string Description => shown ? EconomyThoughts.Description(this) : base.Description;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref shown, "shown");
        }

        public override void CopyFrom(Thought_Memory m)
        {
            base.CopyFrom(m);
            if (m is IEconomyThought e) shown = e.Shown;
        }
    }

    public class Thought_EconomyMemorySocial : Thought_MemorySocial, IEconomyThought
    {
        private bool shown;

        public bool Shown
        {
            get => shown;
            set => shown = value;
        }

        public override string LabelCap => shown ? EconomyThoughts.Label(this) : base.LabelCap;

        public override string Description => shown ? EconomyThoughts.Description(this) : base.Description;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref shown, "shown");
        }

        public override void CopyFrom(Thought_Memory m)
        {
            base.CopyFrom(m);
            if (m is IEconomyThought e) shown = e.Shown;
        }
    }
}
