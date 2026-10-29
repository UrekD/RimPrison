using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>Who gets a Rim Prison need.</summary>
    public class NeedScopeExtension : DefModExtension
    {
        public bool inmatesOnly = true;
        /// <summary>Only inmates rolled as religious.</summary>
        public bool religiousOnly;
        public float fallPerDayAsleepFactor = 0.5f;
        /// <summary>Thresholds drawn on the need bar.</summary>
        public List<float> thresholds = new List<float>();
        /// <summary>When the need hits zero: filth dropped, memory gained, hygiene lost, need reset (bladder).</summary>
        public ThingDef accidentFilth;
        public ThoughtDef accidentThought;
        public float accidentHygieneLoss;
        /// <summary>Paused while the pawn is downed or on medical bed rest (staff bring a bedpan).</summary>
        public bool pausedWhileBedridden;
        /// <summary>Paused for inmates whose level eats in the cell (death row): they never get to the yard, so the need is not theirs to lose.</summary>
        public bool pausedForCellMeals;
        /// <summary>Only inmates who can read and write (not barred from intellectual work).</summary>
        public bool literateOnly;
    }

    /// <summary>A need that drains at <see cref="NeedDef.fallPerDay"/> (slower asleep) and is refilled by fixtures.</summary>
    public class Need_Fixture : Need
    {
        private NeedScopeExtension Ext => def.GetModExtension<NeedScopeExtension>();

        public Need_Fixture(Pawn pawn) : base(pawn)
        {
        }

        public override void SetInitialLevel() => CurLevel = Rand.Range(0.6f, 1f);

        public override int GUIChangeArrow => IsFrozen ? 0 : -1;

        /// <summary>Per-pawn multiplier on the fall rate. Keyed on the def as well as overridable, because needs from
        /// older saves keep the class they were saved with.</summary>
        protected virtual float FallFactor => def == RPFaithDefOf.RP_Faith && FaithUtility.Guided(pawn) ? 0.5f : 1f;

        public override void NeedInterval()
        {
            if (threshPercents == null)
            {
                threshPercents = new List<float>();
                if (Ext != null) threshPercents.AddRange(Ext.thresholds);
            }
            if (IsFrozen) return;
            if (Ext?.pausedWhileBedridden == true && (pawn.Downed || (pawn.InBed() && HealthAIUtility.ShouldSeekMedicalRest(pawn)))) return;
            if (Ext?.pausedForCellMeals == true && pawn.Inmate() is { security.cellMeals: true }) return;
            float fall = def.fallPerDay * FallFactor * (150f / GenDate.TicksPerDay);
            if (!pawn.Awake()) fall *= Ext?.fallPerDayAsleepFactor ?? 0.5f;
            CurLevel = Mathf.Max(0f, CurLevel - fall);
            if (CurLevel <= 0f && Ext?.accidentThought != null) Accident(Ext);
        }

        private void Accident(NeedScopeExtension ext)
        {
            CurLevel = 1f;
            if (pawn.Spawned && ext.accidentFilth != null) FilthMaker.TryMakeFilth(pawn.Position, pawn.Map, ext.accidentFilth, pawn.LabelIndefinite(), 2);
            pawn.needs.mood?.thoughts.memories.TryGainMemory(ext.accidentThought);
            if (ext.accidentHygieneLoss > 0f && pawn.needs.TryGetNeed(RPDefOf.RP_Hygiene) is { } hygiene) hygiene.CurLevel -= ext.accidentHygieneLoss;
        }
    }

    /// <summary>Literacy starts low (most inmates have not read for a while): the library has something to do, and the exam bonus is earned.</summary>
    public class Need_Literacy : Need_Fixture
    {
        public Need_Literacy(Pawn pawn) : base(pawn)
        {
        }

        public override void SetInitialLevel() => CurLevel = Rand.Range(0.25f, 0.7f);
    }

    [HarmonyPatch(typeof(Pawn_NeedsTracker), "ShouldHaveNeed")]
    public static class Patch_Pawn_NeedsTracker_ShouldHaveNeed
    {
        public static void Postfix(NeedDef nd, Pawn ___pawn, ref bool __result)
        {
            if (!__result) return;
            // Staff bring their own lunch (covered by wages): canteen food is for inmates.
            if (nd == NeedDefOf.Food && ___pawn.Faction != null && ___pawn.Faction.IsPlayer && (___pawn.GetComp<CompStaff>()?.role != null || K9Utility.IsK9(___pawn)))
            {
                __result = false;
                return;
            }
            NeedScopeExtension ext = nd.GetModExtension<NeedScopeExtension>();
            if (ext != null && ext.inmatesOnly && !___pawn.IsPrisonerOfColony) __result = false;
            if (ext != null && ext.religiousOnly && ___pawn.GetComp<CompInmate>() is not { religious: true }) __result = false;
            // (Backstory, traits and genes only: the needs are built while a pawn loads, before its health is there to ask.)
            if (ext != null && ext.literateOnly && ___pawn.story != null && (___pawn.story.DisabledWorkTagsBackstoryTraitsAndGenes & WorkTags.Intellectual) != 0) __result = false;
        }
    }

    /// <summary>Thought stage picked from a need level: stage i is active when level is below thresholds[i].</summary>
    public class NeedThoughtExtension : DefModExtension
    {
        public NeedDef need;
        /// <summary>Upper bounds per stage, ascending; a final stage with bound > 1 can act as a "satisfied" bonus.</summary>
        public List<float> belowLevel = new List<float>();
        public float aboveLevelForLast = -1f;
    }

    public class ThoughtWorker_NeedLevel : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            NeedThoughtExtension ext = def.GetModExtension<NeedThoughtExtension>();
            Need need = ext?.need == null ? null : p.needs?.TryGetNeed(ext.need);
            if (need == null) return ThoughtState.Inactive;
            float level = need.CurLevel;
            for (int i = 0; i < ext.belowLevel.Count; i++)
                if (level < ext.belowLevel[i]) return ThoughtState.ActiveAtStage(i);
            if (ext.aboveLevelForLast >= 0f && level >= ext.aboveLevelForLast && def.stages.Count > ext.belowLevel.Count)
                return ThoughtState.ActiveAtStage(ext.belowLevel.Count);
            return ThoughtState.Inactive;
        }
    }
}
