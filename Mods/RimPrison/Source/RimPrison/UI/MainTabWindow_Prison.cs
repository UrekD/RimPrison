using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimPrison
{
    /// <summary>
    /// The warden's screens. Each toolbar button (Reports, Regime, Policy, Staff, Logistics, Office, Rooms) is a
    /// subclass showing its own subset of these tabs (see Toolbar.cs).
    /// </summary>
    public class MainTabWindow_Prison : MainTabWindow
    {
        public enum Tab { Overview, Chronicle, Grants, Bureaucracy, Inmates, Regime, Intake, Programs, Policy, Staff, Finance, Supply, Rooms, Intel, Deployment, Statistics, Land }

        private Tab tab = Tab.Overview;

        /// <summary>The tabs this window shows (one: no tab strip).</summary>
        protected virtual Tab[] Tabs => (Tab[])System.Enum.GetValues(typeof(Tab));

        private static Dictionary<Tab, MainButtonDef> buttonFor;

        /// <summary>The toolbar button whose window shows <paramref name="tab"/>.</summary>
        public static MainButtonDef ButtonFor(Tab tab)
        {
            if (buttonFor == null)
            {
                buttonFor = new Dictionary<Tab, MainButtonDef>();
                foreach (MainButtonDef def in DefDatabase<MainButtonDef>.AllDefsListForReading)
                    if (def.tabWindowClass != null && typeof(MainTabWindow_Prison).IsAssignableFrom(def.tabWindowClass)
                        && System.Activator.CreateInstance(def.tabWindowClass) is MainTabWindow_Prison w)
                        foreach (Tab t in w.Tabs)
                            if (!buttonFor.ContainsKey(t)) buttonFor[t] = def;
            }
            return buttonFor.TryGetValue(tab, out MainButtonDef d) ? d : null;
        }

        /// <summary>Opens the screen that shows a tab (prison panel shortcuts).</summary>
        public static void Open(Tab tab)
        {
            MainButtonDef def = ButtonFor(tab);
            if (def == null) return;
            if (Find.MainTabsRoot.OpenTab != def) Find.MainTabsRoot.SetCurrentTab(def, playSound: false);
            (def.TabWindow as MainTabWindow_Prison)?.SetTab((int)tab);
        }

        /// <summary>For the UI tour (screenshots): switch tab by index.</summary>
        public void SetTab(int index)
        {
            tab = (Tab)index;
            scroll = Vector2.zero;
        }

        public static int TabCount => System.Enum.GetValues(typeof(Tab)).Length;
        private Vector2 scroll;
        private readonly RegimeTab regime = new RegimeTab();

        public override Vector2 RequestedTabSize => new Vector2(1010f, 640f);

        public override void DoWindowContents(Rect inRect)
        {
            PrisonGame game = PrisonGame.Instance;
            Map map = Find.CurrentMap;
            if (game == null || map == null) return;
            Tab[] mine = Tabs;
            if (System.Array.IndexOf(mine, tab) < 0) tab = mine[0];
            Rect body = inRect;
            if (mine.Length > 1)
            {
                var tabs = new List<TabRecord>();
                foreach (Tab t in mine)
                {
                    Tab captured = t;
                    tabs.Add(new TabRecord(("RP_Tab_" + t).Translate(), () => { tab = captured; scroll = Vector2.zero; }, tab == t));
                }
                body.yMin += 32f;
                TabDrawer.DrawTabs(body, tabs);
            }
            body = body.ContractedBy(10f);
            switch (tab)
            {
                case Tab.Overview: OverviewTab.Draw(body, game, map); break;
                case Tab.Inmates: InmatesTab.Draw(body, map, ref scroll); break;
                case Tab.Chronicle: ChronicleTab.Draw(body, game, map, ref scroll); break;
                case Tab.Grants: GrantsTab.Draw(body, game, map, ref scroll); break;
                case Tab.Bureaucracy: BureaucracyTab.Draw(body, game, map, ref scroll); break;
                case Tab.Programs: ProgramsTab.Draw(body, game, map, ref scroll); break;
                case Tab.Policy: PolicyTab.Draw(body, game); break;
                case Tab.Regime: regime.Draw(body, game); break;
                case Tab.Intake: IntakeTab.Draw(body, game, map); break;
                case Tab.Staff: StaffTab.Draw(body, game, map, ref scroll); break;
                case Tab.Finance: FinanceTab.Draw(body, game, ref scroll); break;
                case Tab.Supply: SupplyTab.Draw(body, game, map, ref scroll); break;
                case Tab.Rooms: RoomsTab.Draw(body, map, ref scroll); break;
                case Tab.Intel: IntelTab.Draw(body, map, ref scroll); break;
                case Tab.Deployment: DeploymentTab.Draw(body, map, ref scroll); break;
                case Tab.Statistics: StatisticsTab.Draw(body, game, map, ref scroll); break;
                case Tab.Land: LandTab.Draw(body, game, map); break;
            }
        }
    }
}
