using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The prison's use of RimWorld's own learning helper (the readout at the top right, <see cref="ConceptDef"/> and <see cref="LessonAutoActivator"/>): a handful of notes, one for each deep
    /// system, that appear the first time the system matters (a zone painted, a trial opened, the first ring link found, the first try to build outside the grounds) and say what it is and what the warden can do about it. Vanilla's
    /// own notes (caravans, drafting, fishing...) have nothing to teach a warden and are held back; the helper itself follows the player's own setting (adaptive training), and a note
    /// is only shown until it is marked learned, which is kept across games like vanilla's.
    /// </summary>
    public static class Concepts
    {
        public const string Pad = "RP_Concept_Pad", Holding = "RP_Concept_Holding", Kitchen = "RP_Concept_Kitchen", Bureaucracy = "RP_Concept_Bureaucracy", HearingRoom = "RP_Concept_HearingRoom",
            Trial = "RP_Concept_Trial", Complaint = "RP_Concept_Complaint", Ring = "RP_Concept_Ring", Debt = "RP_Concept_Debt", Turf = "RP_Concept_Turf", Want = "RP_Concept_Want",
            Measures = "RP_Concept_Measures", Flagship = "RP_Concept_Flagship", Morgue = "RP_Concept_Morgue", Grounds = "RP_Concept_Grounds", Warden = "RP_Concept_Warden";

        /// <summary>Told already in this session (the call sites sit in places that run often: a screen drawn every frame).</summary>
        private static readonly HashSet<string> told = new HashSet<string>();

        /// <summary>The notes of this mod (the others are vanilla's).</summary>
        public static bool Ours(ConceptDef def) => def != null && def.defName.StartsWith("RP_Concept_");

        private static readonly System.Reflection.FieldInfo ActiveConcepts = AccessTools.Field(typeof(LearningReadout), "activeConcepts");

        /// <summary>
        /// Takes vanilla's notes (caravans, drafting...) off the helper's list: a save from before they were held back can come with several piled up. Returns how many.
        /// </summary>
        public static int DropVanillaNotes(LearningReadout readout)
        {
            if (readout == null || ActiveConcepts?.GetValue(readout) is not List<ConceptDef> active) return 0;
            return active.RemoveAll(c => !Ours(c));
        }

        /// <summary>Forget what was told this session (a new game starts again; a check does too).</summary>
        public static void Reset() => told.Clear();

        /// <summary>The note appears in the learning helper, once, as a lesson that matters (see <see cref="LessonAutoActivator.TeachOpportunity(ConceptDef, OpportunityType)"/>).</summary>
        public static void Teach(string defName)
        {
            if (PrisonGame.Instance == null || Current.ProgramState != ProgramState.Playing || !told.Add(defName)) return;
            if (DefDatabase<ConceptDef>.GetNamedSilentFail(defName) is { } def) LessonAutoActivator.TeachOpportunity(def, OpportunityType.Important);
        }

        /// <summary>The player did what the note was about: it counts as learned.</summary>
        public static void Demonstrated(string defName)
        {
            if (PrisonGame.Instance != null && DefDatabase<ConceptDef>.GetNamedSilentFail(defName) is { } def) PlayerKnowledgeDatabase.KnowledgeDemonstrated(def, KnowledgeAmount.Total);
        }

        /// <summary>A zone was painted: the note for that room, if it has one.</summary>
        public static void Painted(PrisonZoneDef zone)
        {
            if (zone == RPDefOf.RP_Holding) Teach(Holding);
            else if (zone == RPLaborDefOf.RP_Kitchen) Teach(Kitchen);
            else if (zone == RPParoleDefOf.RP_HearingRoom) Teach(HearingRoom);
            else if (zone == RPMorgueDefOf.RP_Morgue) Teach(Morgue);
        }

        /// <summary>One of the chronicle's firsts happened (see <see cref="Story.RecordFirst"/>).</summary>
        public static void Notify_First(string key)
        {
            if (key == "death") Teach(Morgue);
        }
    }

    /// <summary>
    /// Only the prison's own notes are taught in a prison game: vanilla's calls for caravans, drafting and the rest are dropped, and the helper does not pick a note of its own accord
    /// (every note here is triggered by what happened).
    /// </summary>
    [HarmonyPatch(typeof(LessonAutoActivator), nameof(LessonAutoActivator.TeachOpportunity), typeof(ConceptDef), typeof(Thing), typeof(OpportunityType))]
    public static class Patch_LessonAutoActivator_TeachOpportunity_Prison
    {
        public static bool Prefix(ConceptDef conc) => PrisonGame.Instance == null || Concepts.Ours(conc);
    }

    [HarmonyPatch(typeof(LessonAutoActivator), nameof(LessonAutoActivator.LessonAutoActivatorUpdate))]
    public static class Patch_LessonAutoActivator_Update_Prison
    {
        public static bool Prefix() => PrisonGame.Instance == null;
    }
}
