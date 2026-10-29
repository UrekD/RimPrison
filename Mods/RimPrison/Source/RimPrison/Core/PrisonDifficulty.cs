using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// What the game's difficulty (the storyteller's difficulty, chosen in the new prison dialog) changes in a prison. Vanilla's own factors are about raids, crops and trade, and
    /// a prison has none of those, so the level is mapped onto five prison levers, with "strive to survive" (the dialog's default, and what every test runs on) as the neutral
    /// level where every factor is one: how often things happen inside the walls, how strict the state is before it steps in (the grade below which a day counts as bad, the
    /// week's limit of deaths and escapes), the funds the prison starts with and what the state asks for land. The dialog's difficulty pane prints this table, so the text cannot drift from what the game does.
    /// </summary>
    public static class PrisonDifficulty
    {
        /// <summary>The levers of one level (0 peaceful to 5 losing is fun).</summary>
        public readonly struct Levers
        {
            /// <summary>Incidents and story beats come this many times as often as the neutral level's.</summary>
            public readonly float events;
            /// <summary>The starting funds are this many times the neutral level's.</summary>
            public readonly float funds;
            /// <summary>A day graded below this counts as bad towards special measures (the neutral level: 55, a C).</summary>
            public readonly float badGrade;
            /// <summary>The week's limit of deaths and escapes before the state steps in is this many times the neutral level's.</summary>
            public readonly float weeklyLimit;
            /// <summary>The state's price for a parcel of land is this many times the neutral level's (see <see cref="PrisonGrounds"/>).</summary>
            public readonly float land;

            public Levers(float events, float funds, float badGrade, float weeklyLimit, float land)
            {
                this.events = events;
                this.funds = funds;
                this.badGrade = badGrade;
                this.weeklyLimit = weeklyLimit;
                this.land = land;
            }
        }

        /// <summary>The level the neutral factors belong to ("strive to survive").</summary>
        public const int NeutralLevel = 3;

        private static readonly Levers[] Table =
        {
            new Levers(0.6f, 1.6f, 40f, 1.5f, 0.6f),
            new Levers(0.8f, 1.3f, 45f, 1.25f, 0.8f),
            new Levers(0.9f, 1.15f, 50f, 1.1f, 0.9f),
            new Levers(1f, 1f, 55f, 1f, 1f),
            new Levers(1.15f, 0.85f, 60f, 0.85f, 1.2f),
            new Levers(1.3f, 0.7f, 65f, 0.7f, 1.5f),
        };

        public static Levers LeversOf(int level) => Table[Mathf.Clamp(level, 0, Table.Length - 1)];

        /// <summary>The vanilla levels by def name (the def's own level number is obsolete in 1.7).</summary>
        private static readonly string[] LevelNames = { "Peaceful", "Easy", "Medium", "Rough", "Hard", "Extreme" };

        /// <summary>The levers of a difficulty def (a custom or modded one without a level of its own: the neutral level's).</summary>
        public static Levers LeversOf(DifficultyDef def)
        {
            int level = def == null || def.isCustom ? -1 : System.Array.IndexOf(LevelNames, def.defName);
            return LeversOf(level < 0 ? NeutralLevel : level);
        }

        /// <summary>The levers in force in this game.</summary>
        public static Levers Current => LeversOf(Find.Storyteller?.difficultyDef);

        public static float EventFactor => Current.events;

        public static float BadGrade => Current.badGrade;

        /// <summary>The starting funds at this difficulty (the state penitentiary starts from a larger sum, <see cref="PrisonBuilder.PenStartFunds"/>: it comes with a payroll of forty).</summary>
        public static int StartingFunds(DifficultyDef def, bool penitentiary = false) =>
            Mathf.RoundToInt((penitentiary ? PrisonBuilder.PenStartFunds : PrisonConstants.StartingFunds) * LeversOf(def).funds);

        /// <summary>One line that says what the level changes, from the table (the dialog's difficulty pane shows it under the level's own description).</summary>
        public static string Describe(DifficultyDef def, bool penitentiary = false)
        {
            Levers l = LeversOf(def);
            return "RP_DifficultyLevers".Translate(Mathf.RoundToInt(l.events * 100f), Mathf.RoundToInt(l.badGrade), Mathf.RoundToInt(l.weeklyLimit * 100f), Money.Format(StartingFunds(def, penitentiary)), (def == null ? 0f : def.colonistMoodOffset).ToString("+0;-0;0"), Mathf.RoundToInt(l.land * 100f)).Resolve();
        }

        /// <summary>The pane's text for a difficulty: what the level means for a prison, and what it changes (never vanilla's raid text).</summary>
        public static string PaneText(DifficultyDef def, bool penitentiary = false)
        {
            string flavour = ("RP_Difficulty_" + def.defName).TryTranslate(out TaggedString text) ? text.Resolve() + "\n\n" : "";
            return flavour + Describe(def, penitentiary);
        }
    }
}
