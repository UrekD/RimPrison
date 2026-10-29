using System.Text.RegularExpressions;
using HarmonyLib;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Vanilla highlights money in text with "\$\d+\.?\d*", which stops at a thousands separator: "$25,758" came out as a
    /// gold "$25" and a plain ",758" in every letter. The highlight is stretched over the separated groups.
    /// </summary>
    [HarmonyPatch(typeof(ColoredText), nameof(ColoredText.Resolve))]
    public static class Patch_ColoredText_MoneyHighlight
    {
        private static readonly Regex Split = new Regex(@"(<color=#DBB40CFF>-?\$\d+)</color>((?:,\d{3})+)", RegexOptions.Compiled);

        public static void Postfix(ref string __result)
        {
            if (__result != null && __result.Contains("</color>,")) __result = Split.Replace(__result, "$1$2</color>");
        }
    }
}
