using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    public class RimPrisonSettings : ModSettings
    {
        /// <summary>Blueprints are paid for when placed and their materials are brought by the state shuttle.</summary>
        public bool autoProcureBlueprints = true;
        public bool showZoneOverlay = true;
        /// <summary>Everything that needs power is powered (mains); a daily utility bill instead of grids.</summary>
        public bool simpleUtilities = true;
        /// <summary>The prison's colonist bar is split into a group a role (see <see cref="StaffBar"/>); off, it is vanilla's one row.</summary>
        public bool groupColonistBar = true;
        /// <summary>An inmate at risk may come to a crisis and harm themself (see <see cref="WatchList"/>); off, the flag, the letter and the watch still happen and no crisis starts.</summary>
        public bool selfHarmCrises = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref autoProcureBlueprints, "autoProcureBlueprints", true);
            Scribe_Values.Look(ref showZoneOverlay, "showZoneOverlay", true);
            Scribe_Values.Look(ref simpleUtilities, "simpleUtilities", true);
            Scribe_Values.Look(ref groupColonistBar, "groupColonistBar", true);
            Scribe_Values.Look(ref selfHarmCrises, "selfHarmCrises", true);
        }
    }

    public class RimPrisonMod : Mod
    {
        public static RimPrisonSettings Settings;

        public RimPrisonMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimPrisonSettings>();
        }

        public override string SettingsCategory() => "Rim Prison";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);
            list.CheckboxLabeled("RP_Settings_AutoProcure".Translate(), ref Settings.autoProcureBlueprints, "RP_Settings_AutoProcureDesc".Translate());
            list.CheckboxLabeled("RP_Settings_ZoneOverlay".Translate(), ref Settings.showZoneOverlay);
            list.CheckboxLabeled("RP_Settings_SimpleUtilities".Translate(), ref Settings.simpleUtilities, "RP_Settings_SimpleUtilitiesDesc".Translate());
            bool grouped = Settings.groupColonistBar;
            list.CheckboxLabeled("RP_Settings_GroupColonistBar".Translate(), ref Settings.groupColonistBar, "RP_Settings_GroupColonistBarDesc".Translate());
            if (grouped != Settings.groupColonistBar) StaffBar.MarkDirty();
            list.CheckboxLabeled("RP_Settings_SelfHarm".Translate(), ref Settings.selfHarmCrises, "RP_Settings_SelfHarmDesc".Translate());
            list.End();
        }
    }

    // Applies every [HarmonyPatch] class in this assembly once the game has loaded its defs, and attaches the
    // prison comps to every door and humanlike race (works for modded races and doors without XML patches).
    [StaticConstructorOnStartup]
    public static class RimPrisonStartup
    {
        static RimPrisonStartup()
        {
            InjectComps();
            HideColonyButtons();
            PrisonZoneDef.AssignGridIndices();
            PatchAllSafely(new Harmony("UrekD.RimPrison"));
            LongEventHandler.ExecuteWhenFinished(Patch_DesignationCategoryDef_ResolveDesignators.EnsureAdded);
            AutoTest.CheckCommandLine();
            // The test's own patches, and (only when asked for: RIMPRISON_PROBE=1, the probe's timers change how a run plays out) its timers.
            if (AutoTest.Active) PatchFailures += TestOnlyAttribute.InstallTestPatches(new Harmony("UrekD.RimPrison.Test"));
            if (AutoTest.Active && System.Environment.GetEnvironmentVariable("RIMPRISON_PROBE") != null) PerfProbe.Install(new Harmony("UrekD.RimPrison.Perf"));
            if (AutoTest.Active && PatchFailures > 0)
            {
                // An unattended test would only hang until the harness timeout; fail fast instead.
                Log.Message($"[RimPrison:test] FAIL harmony: {PatchFailures} patch classes failed to apply");
                Root.Shutdown();
            }
            Log.Message("[RimPrison] loaded");
        }

        public static int PatchFailures;

        /// <summary>
        /// A prison has no world map, research tree, factions, quests, ideoligions, animals, mechs or timetables (shifts) to manage, and Work, Assign and History are reached from the prison screens: those
        /// main buttons (and their hotkeys) are hidden. Architect, the prison toolbar and Menu stay.
        /// </summary>
        private static readonly string[] HiddenButtons = { "World", "Research", "Factions", "Quests", "Ideos", "Animals", "Wildlife", "Mechs", "Schedule", "Work", "Assign", "History" };

        private static void HideColonyButtons()
        {
            foreach (string name in HiddenButtons)
                if (DefDatabase<MainButtonDef>.GetNamedSilentFail(name) is { } def)
                {
                    def.buttonVisible = false;
                    def.hotKey = null;
                }
        }

        /// <summary>Patches class by class so one broken target (game update) does not disable every other patch.</summary>
        private static void PatchAllSafely(Harmony harmony)
        {
            int ok = 0, failed = 0;
            foreach (System.Type type in typeof(RimPrisonStartup).Assembly.GetTypes())
            {
                // (The test-only patches wait for a test to be armed: see TestOnlyAttribute.)
                if (!type.IsDefined(typeof(HarmonyPatch), false) || type.IsDefined(typeof(TestOnlyAttribute), false)) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (System.Exception e)
                {
                    failed++;
                    RPLog.Error($"patch {type.Name} failed: {e.GetBaseException().Message}");
                }
            }
            RPLog.Message($"harmony: {ok} patch classes applied, {failed} failed");
            PatchFailures = failed;
        }

        private static void InjectComps()
        {
            int doors = 0, races = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.thingClass != null && typeof(Building_Door).IsAssignableFrom(def.thingClass))
                {
                    AddComp(def, new CompProperties { compClass = typeof(CompPrisonDoor) });
                    doors++;
                }
                else if (def.category == ThingCategory.Pawn && def.race != null && def.race.Humanlike)
                {
                    AddComp(def, new CompProperties { compClass = typeof(CompInmate) });
                    AddComp(def, new CompProperties { compClass = typeof(CompStaff) });
                    def.inspectorTabs ??= new List<System.Type>();
                    foreach (System.Type tab in new[] { typeof(ITab_InmateFile), typeof(ITab_StaffRecord) })
                    {
                        if (def.inspectorTabs.Contains(tab)) continue;
                        def.inspectorTabs.Add(tab);
                        def.inspectorTabsResolved ??= new List<InspectTabBase>();
                        def.inspectorTabsResolved.Add(InspectTabManager.GetSharedInstance(tab));
                    }
                    races++;
                }
            }
            RPLog.Message($"comps attached: {doors} door defs, {races} humanlike races");
        }

        private static void AddComp(ThingDef def, CompProperties props)
        {
            def.comps ??= new List<CompProperties>();
            foreach (CompProperties existing in def.comps)
                if (existing.compClass == props.compClass) return;
            def.comps.Add(props);
        }
    }

    public static class RPLog
    {
        public static void Message(string text) => Log.Message("[RimPrison] " + text);
        public static void Warning(string text) => Log.Warning("[RimPrison] " + text);
        public static void Error(string text) => Log.Error("[RimPrison] " + text);
    }
}
