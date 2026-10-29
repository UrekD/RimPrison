using LudeonTK;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>The grounds in the debug actions menu (dev mode, the "Rim Prison" category): each side out or in by ten cells, all four sides, the whole map, the starting grounds. Free, like the Land section's dev buttons.</summary>
    public static class GroundsDebugActions
    {
        private const string Category = "Rim Prison";

        private static void Move(GroundsSide side, int cells)
        {
            PrisonGrounds g = PrisonGrounds.Of(Find.CurrentMap);
            if (g == null) return;
            g.MoveSide(side, cells);
            Say(g);
        }

        private static void MoveAll(int cells)
        {
            PrisonGrounds g = PrisonGrounds.Of(Find.CurrentMap);
            if (g == null) return;
            foreach (GroundsSide side in LandTab.Sides) g.MoveSide(side, cells);
            Say(g);
        }

        private static void Say(PrisonGrounds g) => Messages.Message($"Grounds: {g.Rect.Width} x {g.Rect.Height} cells ({g.Rect.minX},{g.Rect.minZ} to {g.Rect.maxX},{g.Rect.maxZ}).", MessageTypeDefOf.SilentInput, historical: false);

        [DebugAction(Category, "Grounds: north +10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void NorthOut() => Move(GroundsSide.North, 10);

        [DebugAction(Category, "Grounds: north -10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void NorthIn() => Move(GroundsSide.North, -10);

        [DebugAction(Category, "Grounds: east +10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EastOut() => Move(GroundsSide.East, 10);

        [DebugAction(Category, "Grounds: east -10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EastIn() => Move(GroundsSide.East, -10);

        [DebugAction(Category, "Grounds: south +10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SouthOut() => Move(GroundsSide.South, 10);

        [DebugAction(Category, "Grounds: south -10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SouthIn() => Move(GroundsSide.South, -10);

        [DebugAction(Category, "Grounds: west +10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WestOut() => Move(GroundsSide.West, 10);

        [DebugAction(Category, "Grounds: west -10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WestIn() => Move(GroundsSide.West, -10);

        [DebugAction(Category, "Grounds: every side +10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AllOut() => MoveAll(10);

        [DebugAction(Category, "Grounds: every side -10", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AllIn() => MoveAll(-10);

        [DebugAction(Category, "Grounds: whole map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void WholeMap()
        {
            PrisonGrounds g = PrisonGrounds.Of(Find.CurrentMap);
            g?.SetWholeMap();
            if (g != null) Say(g);
        }

        [DebugAction(Category, "Grounds: back to the starting grounds", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BackToStart()
        {
            PrisonGrounds g = PrisonGrounds.Of(Find.CurrentMap);
            g?.SetBackToStart();
            if (g != null) Say(g);
        }
    }
}
