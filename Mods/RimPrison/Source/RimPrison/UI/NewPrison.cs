using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>Settings for a quick-started prison game.</summary>
    public class NewPrisonOptions
    {
        public ScenarioDef scenario;
        public DifficultyDef difficulty;
        public int mapSize = 200;
        public string seed = GenText.RandomSeedString();
        /// <summary>Dev mode: the land owned beyond the facility on each side (north, east, south, west); null is the usual (see <see cref="PrisonGrounds.StartMargin"/>).</summary>
        public int[] groundsMargins;
        /// <summary>The prison you are given has a past (see <see cref="Handover"/>): its secrets sown and the old warden's note for the welcome letter. Off here, so a start that does not ask (every test's) is clean; the dialog turns it on.</summary>
        public bool inheritProblems;
    }

    /// <summary>
    /// Starts a Rim Prison game without the colony pages: a small world is generated in the background, a flat
    /// temperate tile is picked, the scenario's staff are generated, and the map loads straight away.
    /// </summary>
    public static class PrisonGameStarter
    {
        public static void Start(NewPrisonOptions o)
        {
            LongEventHandler.QueueLongEvent(() => Setup(o), "GeneratingMap", doAsynchronously: true, GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap);
        }

        /// <summary>Must run inside a long event; ends by loading the Play scene (PageUtility.InitGameStart).</summary>
        public static void Setup(NewPrisonOptions o)
        {
            Current.ProgramState = ProgramState.Entry;
            Game.ClearCaches();
            Current.Game = new Game();
            Current.Game.InitData = new GameInitData();
            Current.Game.Scenario = o.scenario.scenario;
            Find.Scenario.PreConfigure();
            Current.Game.storyteller = new Storyteller(RPDefOf.RP_TheBoard, o.difficulty ?? DifficultyDefOf.Rough);
            Current.Game.World = WorldGenerator.GenerateWorld(0.05f, o.seed, OverallRainfall.Normal, OverallTemperature.Normal, OverallPopulation.Normal, LandmarkDensity.Sparse);
            Find.GameInitData.startingTile = PickTile();
            Find.GameInitData.mapSize = o.mapSize;
            PrisonGrounds.PendingMargins = o.groundsMargins;
            Handover.Pending = o.inheritProblems;
            UseClassicIdeo();
            Find.Scenario.PostIdeoChosen();
            PageUtility.InitGameStart();
        }

        /// <summary>Flat or gently hilly land without killer summers or winters, else any settleable tile.</summary>
        private static PlanetTile PickTile()
        {
            PlanetTile tile = TileFinder.RandomSettlementTileFor(Faction.OfPlayer, mustBeAutoChoosable: true, extraValidator: t =>
            {
                Tile data = Find.WorldGrid[t];
                return data.hilliness <= Hilliness.SmallHills && GenTemperature.MaxTemperatureAtTile(t) < 30f && GenTemperature.MinTemperatureAtTile(t) > -12f;
            });
            return tile.Valid ? tile : TileFinder.RandomStartingTile();
        }

        /// <summary>
        /// Ideology: a classic ideoligion for everyone (as the "classic" preset does) so prison life is not drowned in
        /// rituals and xenotype precepts. Starting staff are generated after this, with it.
        /// </summary>
        private static void UseClassicIdeo()
        {
            if (!ModsConfig.IdeologyActive) return;
            FactionDef playerDef = Find.FactionManager.OfPlayer.def;
            CultureDef culture = null;
            foreach (CultureDef c in DefDatabase<CultureDef>.AllDefsListForReading)
                if (playerDef.allowedCultures.Contains(c)) culture = c;
            culture ??= DefDatabase<CultureDef>.AllDefsListForReading.RandomElement();
            Ideo classic = IdeoGenerator.GenerateClassicIdeo(culture, new IdeoGenerationParms(playerDef), noExpansionIdeo: false);
            Find.IdeoManager.Add(classic);
            classic.initialPlayerIdeo = true;
            Find.IdeoManager.classicMode = true;
            foreach (Faction f in Find.FactionManager.AllFactions)
            {
                if (f.ideos == null) continue;
                f.ideos.RemoveAll();
                f.ideos.SetPrimary(classic);
            }
            Find.IdeoManager.RemoveUnusedStartingIdeos();
        }
    }

    /// <summary>
    /// The new prison dialog: a name, the facility and the difficulty on the left; what the choice means on the right
    /// (the scenario's description and what you start with, the difficulty's description).
    /// </summary>
    public class Dialog_NewPrison : Window
    {
        private readonly NewPrisonOptions options = new NewPrisonOptions();
        private string name = PrisonNaming.Generate();
        private DifficultyDef hovered;
        private readonly int[] margins = { PrisonGrounds.StartMargin, PrisonGrounds.StartMargin, PrisonGrounds.StartMargin, PrisonGrounds.StartMargin };
        private readonly string[] marginBuffers = new string[4];

        public override Vector2 InitialSize => new Vector2(820f, 720f);

        public Dialog_NewPrison()
        {
            options.scenario = RPDefOf.RP_InheritedPrison;
            options.difficulty = DifficultyDefOf.Rough;
            // (On by default: the state penitentiary and the inherited facility come with a past. A prison built from an empty plot has none, and the choice is not shown.)
            options.inheritProblems = true;
            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "RP_NewPrisonTitle".Translate());
            Text.Font = GameFont.Small;
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(0f, 34f, inRect.width, 40f), "RP_NewPrisonHelp".Translate());
            GUI.color = Color.white;
            float top = 80f;
            var left = new Rect(0f, top, 360f, inRect.height - top - 50f);
            var right = new Rect(left.xMax + 20f, top, inRect.width - left.xMax - 20f, left.height);

            // Left: name, facility, difficulty, plot size.
            var list = new Listing_Standard();
            list.Begin(left);
            list.Label("RP_NewPrisonName".Translate());
            Rect nameRow = list.GetRect(30f);
            name = Widgets.TextField(new Rect(nameRow.x, nameRow.y, nameRow.width - 96f, nameRow.height), name);
            if (Widgets.ButtonText(new Rect(nameRow.xMax - 90f, nameRow.y, 90f, nameRow.height), "RP_NewPrisonRandomName".Translate())) name = PrisonNaming.Generate();
            list.Gap(10f);
            list.Label("RP_NewPrisonFacility".Translate());
            foreach (ScenarioDef def in new[] { RPDefOf.RP_InheritedPrison, RPDefOf.RP_StatePenitentiary, RPDefOf.RP_NewPrison })
            {
                if (list.RadioButton(def.LabelCap, options.scenario == def, 0f)) options.scenario = def;
            }
            // The handover: only for a prison that is handed over built.
            if (options.scenario != RPDefOf.RP_NewPrison) list.CheckboxLabeled("RP_NewPrisonHandover".Translate(), ref options.inheritProblems, "RP_NewPrisonHandoverTip".Translate());
            list.Gap(10f);
            list.Label("RP_NewPrisonDifficulty".Translate());
            hovered = null;
            foreach (DifficultyDef d in DefDatabase<DifficultyDef>.AllDefsListForReading)
            {
                if (d.isCustom) continue;
                Rect row = list.GetRect(0f);
                if (list.RadioButton(d.LabelCap, options.difficulty == d, 0f)) options.difficulty = d;
                if (Mouse.IsOver(new Rect(row.x, row.y, row.width, list.CurHeight - row.y))) hovered = d;
            }
            list.Gap(10f);
            list.Label("RP_NewPrisonMapSize".Translate(options.mapSize));
            // (The state penitentiary needs room: its walls and the road, landing pad and deliveries outside take 120 x 122 cells.)
            float minPlot = options.scenario == RPDefOf.RP_StatePenitentiary ? 200f : 150f;
            options.mapSize = Mathf.RoundToInt(list.Slider(Mathf.Max(options.mapSize, minPlot), minPlot, 275) / 25f) * 25;
            // The two settings that change how a prison is built (they are chosen outside the game, so a new warden is told where they are).
            list.Gap(14f);
            GUI.color = PrisonHUD.Muted;
            list.Label("RP_NewPrisonSettings".Translate(("RP_State_" + (RimPrisonMod.Settings.autoProcureBlueprints ? "On" : "Off")).Translate(), ("RP_State_" + (RimPrisonMod.Settings.simpleUtilities ? "On" : "Off")).Translate()));
            GUI.color = Color.white;
            if (list.ButtonText("RP_NewPrisonSettingsButton".Translate())) Find.WindowStack.Add(new Dialog_ModSettings(LoadedModManager.GetMod<RimPrisonMod>()));
            list.End();

            // Right: what the choice means.
            Widgets.DrawBoxSolid(right, new Color(0.1f, 0.11f, 0.13f));
            Rect inner = right.ContractedBy(12f);
            float y = inner.y;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, y, inner.width, 32f), name.NullOrEmpty() ? options.scenario.LabelCap.ToString() : name);
            y += 34f;
            Text.Font = GameFont.Small;
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(inner.x, y, inner.width, 22f), options.scenario.LabelCap);
            GUI.color = Color.white;
            y += 24f;
            string desc = options.scenario.description;
            float h = Text.CalcHeight(desc, inner.width);
            Widgets.Label(new Rect(inner.x, y, inner.width, h), desc);
            y += h + 6f;
            string summary = options.scenario.scenario.summary;
            h = Text.CalcHeight(summary, inner.width);
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(inner.x, y, inner.width, h), summary);
            GUI.color = Color.white;
            y += h + 10f;
            Widgets.DrawLineHorizontal(inner.x, y, inner.width);
            y += 8f;
            DifficultyDef shown = hovered ?? options.difficulty;
            Widgets.Label(new Rect(inner.x, y, inner.width, 22f), "RP_NewPrisonDifficultyIs".Translate(shown.LabelCap));
            y += 24f;
            GUI.color = PrisonHUD.Muted;
            // (What the level means for a prison and what it changes, from the table the game reads: not vanilla's text, which is about raids and crops.)
            string diffDesc = PrisonDifficulty.PaneText(shown, options.scenario.scenario.AllParts.OfType<ScenPart_PrisonStart>().FirstOrDefault() is { starterFacility: true, facility: Facility.Penitentiary });
            h = Text.CalcHeight(diffDesc, inner.width);
            Widgets.Label(new Rect(inner.x, y, inner.width, h), diffDesc);
            GUI.color = Color.white;

            // Dev mode: how much land the prison owns round its facility, a side at a time (the bottom row, left of Start: the panes keep their room).
            if (Prefs.DevMode)
            {
                var devRow = new Rect(0f, inRect.height - 34f, inRect.width - 220f, 28f);
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                float labelW = devRow.width - 4 * 56f;
                Widgets.Label(new Rect(devRow.x, devRow.y, labelW, devRow.height), "RP_NewPrisonDevMargins".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                for (int i = 0; i < 4; i++) Widgets.TextFieldNumeric(new Rect(devRow.x + labelW + i * 56f, devRow.y, 52f, devRow.height), ref margins[i], ref marginBuffers[i], 0f, 60f);
            }
            if (Widgets.ButtonText(new Rect(inRect.width - 200f, inRect.height - 40f, 200f, 40f), "RP_NewPrisonStart".Translate()))
            {
                options.groundsMargins = Prefs.DevMode && System.Array.Exists(margins, m => m != PrisonGrounds.StartMargin) ? (int[])margins.Clone() : null;
                PrisonNaming.Pending = name?.Trim().NullOrEmpty() != false ? null : name.Trim();
                Close();
                PrisonGameStarter.Start(options);
            }
        }
    }

    /// <summary>"New prison" on the main menu, right above "New colony".</summary>
    [HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
    public static class Patch_OptionListingUtility_DrawOptionListing
    {
        private static string newColonyLabel;

        public static void Prefix(List<ListableOption> optList)
        {
            if (Current.ProgramState != ProgramState.Entry) return;
            newColonyLabel ??= "NewColony".Translate();
            for (int i = 0; i < optList.Count; i++)
            {
                if (optList[i].label != newColonyLabel) continue;
                optList.Insert(i, new ListableOption("RP_NewPrison".Translate(), () => Find.WindowStack.Add(new Dialog_NewPrison())));
                return;
            }
        }
    }
}
