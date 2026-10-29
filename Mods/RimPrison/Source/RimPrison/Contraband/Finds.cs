using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>How a piece of contraband was found (the map of finds draws a different mark for each).</summary>
    public enum FindSource : byte
    {
        /// <summary>On the inmate, in a frisk or a pat-down.</summary>
        Pockets,
        /// <summary>Hidden in a cell.</summary>
        Stash,
        /// <summary>On a visitor, at reception.</summary>
        Visitor,
        /// <summary>In a letter, in the mailroom.</summary>
        Mail,
        /// <summary>Taken in a bust of a smuggling ring.</summary>
        Raid,
        /// <summary>In the crates a shuttle brought, searched on the pad.</summary>
        Cargo,
        /// <summary>A smokeleaf patch sown in the garden, pulled up.</summary>
        Garden,
    }

    /// <summary>One find on the map of where contraband turned up (see <see cref="PrisonMap.finds"/>): where, what, how, and when.</summary>
    public class ContrabandFind : IExposable
    {
        public IntVec3 cell;
        public ContrabandKind kind;
        public FindSource source;
        public int tick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref source, "source");
            Scribe_Values.Look(ref tick, "tick");
        }

        public string Describe() => $"{cell.x},{cell.z}:{kind}:{source}:{tick}";
    }

    /// <summary>Draws the map of finds next to the coverage overlay: a mark where each find in the last 30 days was made, fading with age.</summary>
    public static class FindsOverlay
    {
        public static Color ColorOf(ContrabandKind kind) => kind switch
        {
            ContrabandKind.Weapon => new Color(0.95f, 0.25f, 0.2f),
            ContrabandKind.Drug => new Color(0.75f, 0.45f, 0.95f),
            ContrabandKind.Tool => new Color(0.95f, 0.65f, 0.2f),
            ContrabandKind.Brew => new Color(0.85f, 0.8f, 0.3f),
            _ => new Color(0.3f, 0.85f, 0.95f),
        };

        /// <summary>A find is on the map for <see cref="PrisonMap.FindsDays"/> days (the list is only pruned when the next is made, so an old one may still be in it).</summary>
        public static bool Shown(ContrabandFind f, int now) => now - f.tick < PrisonMap.FindsDays * GenDate.TicksPerDay;

        public static void DrawMarks(PrisonMap pm)
        {
            int now = Find.TickManager.TicksGame;
            foreach (ContrabandFind f in pm.finds)
            {
                if (!Shown(f, now)) continue;
                float age = Mathf.Clamp01((now - f.tick) / (float)(PrisonMap.FindsDays * GenDate.TicksPerDay));
                Color col = ColorOf(f.kind);
                col.a = Mathf.Lerp(1f, 0.35f, age);
                GenMapUI.DrawThingLabel(f.cell.ToVector3Shifted().MapToUIPosition(), ("RP_FindGlyph_" + f.source).Translate(), col);
            }
        }
    }
}
