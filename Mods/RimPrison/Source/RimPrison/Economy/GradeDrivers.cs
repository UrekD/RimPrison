using Verse;

namespace RimPrison
{
    /// <summary>Why a graded area stands where it does: the day's numbers behind it in plain words, and how far it moved since the previous card (the overview's tile tooltips).</summary>
    public static class GradeDrivers
    {
        public static readonly string[] Areas = { "Safety", "Security", "Health", "Conditions", "Order", "Reform" };

        public static float Score(ReportCard c, string area) => area switch
        {
            "Safety" => c.safety,
            "Security" => c.security,
            "Health" => c.health,
            "Conditions" => c.conditions,
            "Order" => c.order,
            _ => c.reform,
        };

        /// <summary>The score as the tiles show it (rounded to a whole number), so "down 8" agrees with the two numbers the player reads.</summary>
        public static int Shown(float v) => float.IsFinite(v) ? (int)System.Math.Round(v, System.MidpointRounding.AwayFromZero) : 0;

        /// <summary>"3 violent incidents", "no deaths", "1 inmate": the count with the word in the number it takes (vanilla's English plural keeps the singular for any count under two, so zero is told here).</summary>
        private static string Words(int n, string key)
        {
            TaggedString word = key.Translate();
            return (n == 0 ? "RP_DeskNone".Translate().ToString() : n.ToString()) + " " + (n == 1 ? word.ToString() : Find.ActiveLanguageWorker.Pluralize(word));
        }

        private static int Percent(float share) => Shown(share * 100f);

        /// <summary>Only the inputs of this area, as the card kept them.</summary>
        public static string Drivers(string area, ReportCard c)
        {
            switch (area)
            {
                case "Safety":
                    return "RP_DeskSafety".Translate(Words(c.violent, "RP_DeskViolentIncident"), Words(c.deaths, "RP_DeskDeath"), Words(c.inmates, "RP_DeskInmate")).Resolve();
                case "Security":
                    return "RP_DeskSecurity".Translate(Words(c.escapes, "RP_DeskEscape"), Words(c.contrabandSmuggled, "RP_DeskContrabandItem"), c.contrabandFound, Words(c.ringsBusted, "RP_DeskRing")).Resolve();
                case "Health":
                    return "RP_DeskHealth".Translate(c.injured, Words(c.inmates, "RP_DeskInmate"), Words(c.deaths, "RP_DeskDeath")).Resolve();
                case "Conditions":
                    return "RP_DeskConditions".Translate(Percent(c.meanMood), Words(c.inmates, "RP_DeskInmate")).Resolve();
                case "Order":
                    return c.complianceSamples > 0
                        ? "RP_DeskOrder".Translate(Percent(c.complianceSum / c.complianceSamples), c.complianceSamples).Resolve()
                        : "RP_DeskOrderNoSamples".Translate(Shown(c.order)).Resolve();
                default:
                    string stayedOut = c.released > 0 ? "RP_DeskStayedOut".Translate(Percent(c.stayedOut)).Resolve() : "RP_DeskNobodyReleased".Translate().Resolve();
                    // (No share of paroles to tell when none came in the window, whether or not anybody was released in it: "0% of the releases" read as a bad parole record.)
                    return c.paroleShare > 0f
                        ? "RP_DeskReform".Translate(c.employed, Words(c.inmates, "RP_DeskInmate"), c.reformed, Percent(c.paroleShare), stayedOut).Resolve()
                        : "RP_DeskReformNoParole".Translate(c.employed, Words(c.inmates, "RP_DeskInmate"), c.reformed, stayedOut).Resolve();
            }
        }

        /// <summary>The tooltip's line: "Safety 72, down 8 since the last card - 3 violent incidents and no deaths among 40 inmates." Without a previous card (or one that kept no detail) there is no change to tell.</summary>
        public static string Line(string area, ReportCard card, ReportCard previous)
        {
            TaggedString name = ("RP_Grade_" + area).Translate();
            int now = Shown(Score(card, area));
            string drivers = Drivers(area, card);
            if (previous is not { detailed: true, Graded: true }) return "RP_DeskLine".Translate(name, now, drivers).Resolve();
            int diff = now - Shown(Score(previous, area));
            string change = diff > 0 ? "RP_DeskUp".Translate(diff).Resolve() : diff < 0 ? "RP_DeskDown".Translate(-diff).Resolve() : "RP_DeskSame".Translate().Resolve();
            return "RP_DeskLineMoved".Translate(name, now, change, drivers).Resolve();
        }

        /// <summary>A past day's card for the history bars: its six areas, or the word that it kept no detail.</summary>
        public static string Past(ReportCard card) => card is { detailed: true } ? card.Describe() : "RP_DeskNoDetail".Translate().Resolve();
    }
}
