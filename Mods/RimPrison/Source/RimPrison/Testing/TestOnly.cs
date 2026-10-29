using System;
using System.Reflection;
using HarmonyLib;

namespace RimPrison
{
    /// <summary>
    /// A Harmony patch class that only an autotest run wants (the per-frame test loop, the log and error taps, the headless autosave switch). <see cref="RimPrisonStartup"/> leaves
    /// these out of the patches it applies to every game, and <see cref="InstallTestPatches"/> applies them when a test is armed, so a player's game pays for none of them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TestOnlyAttribute : Attribute
    {
        /// <summary>Applies every <see cref="TestOnlyAttribute"/> patch class (as <see cref="PerfProbe.Install"/> applies its own); returns how many failed.</summary>
        public static int InstallTestPatches(Harmony harmony)
        {
            int ok = 0, failed = 0;
            foreach (Type type in typeof(TestOnlyAttribute).Assembly.GetTypes())
            {
                if (!type.IsDefined(typeof(TestOnlyAttribute), false) || !type.IsDefined(typeof(HarmonyPatch), false)) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    RPLog.Error($"test patch {type.Name} failed: {e.GetBaseException().Message}");
                }
            }
            RPLog.Message($"harmony: {ok} test patch classes applied, {failed} failed");
            return failed;
        }
    }
}
