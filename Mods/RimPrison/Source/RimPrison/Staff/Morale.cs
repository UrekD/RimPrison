using Verse;
using Verse.AI;

namespace RimPrison
{
    public enum MoraleBand { Demoralised, Low, Steady, High }

    /// <summary>
    /// Staff morale (their mood) changes how well guards do the job: a demoralised guard misses things in a frisk or a
    /// cell search and is slower to get an inmate under control; one in high spirits is a little sharper. The bands follow
    /// the pawn's own break thresholds (traits move them): demoralised under the major break threshold (vanilla already
    /// alerts on it), low under halfway to the minor one; both sit below a normally run prison's staff moods (about
    /// 0.36-0.65 in soaks), so it bites when staff are neglected.
    /// </summary>
    public static class StaffMorale
    {
        public const float DemoralisedBelow = 0.2f, LowBelow = 0.275f, HighAbove = 0.7f;

        public static MoraleBand Band(Pawn staff)
        {
            float mood = staff?.needs?.mood?.CurLevelPercentage ?? 0.5f;
            MentalBreaker breaker = staff?.mindState?.mentalBreaker;
            float major = breaker?.BreakThresholdMajor ?? DemoralisedBelow, minor = breaker?.BreakThresholdMinor ?? 0.35f;
            return mood < major ? MoraleBand.Demoralised : mood < (major + minor) / 2f ? MoraleBand.Low : mood > HighAbove ? MoraleBand.High : MoraleBand.Steady;
        }

        /// <summary>Multiplier on a guard's chance to find contraband (frisks, cell searches).</summary>
        public static float SearchFactor(Pawn guard) => Band(guard) switch
        {
            MoraleBand.Demoralised => 0.6f,
            MoraleBand.Low => 0.85f,
            MoraleBand.High => 1.1f,
            _ => 1f,
        };

        /// <summary>Added to a guard's chance to subdue an inmate.</summary>
        public static float SubdueOffset(Pawn guard) => Band(guard) switch
        {
            MoraleBand.Demoralised => -0.2f,
            MoraleBand.Low => -0.075f,
            MoraleBand.High => 0.05f,
            _ => 0f,
        };

        /// <summary>A night guard's run of nights has gone on too long (<see cref="Roster.NightsAgainAfter"/>): the thought, kept fresh each night the run goes on.</summary>
        public static void NightsAgain(Pawn guard) => guard?.needs?.mood?.thoughts.memories.TryGainMemory(RPRosterDefOf.RP_NightsAgain);

        /// <summary>Taken off nights after a run of them resented: a fair rota.</summary>
        public static void FairRota(Pawn guard) => guard?.needs?.mood?.thoughts.memories.TryGainMemory(RPRosterDefOf.RP_FairRota);

        /// <summary>The prison did what it promised the staff (see <see cref="StaffAgreements"/>).</summary>
        public static void KeptTheirWord(Pawn staff) => staff?.needs?.mood?.thoughts.memories.TryGainMemory(RPAgreementDefOf.RP_KeptTheirWord);

        /// <summary>The prison did not: remembered for longer, and harder, than the bonus that was not paid.</summary>
        public static void BrokenPromise(Pawn staff) => staff?.needs?.mood?.thoughts.memories.TryGainMemory(RPAgreementDefOf.RP_BrokenPromise);

        /// <summary>A line for the staff record (null when steady).</summary>
        public static string StatusLine(Pawn staff) => Band(staff) switch
        {
            MoraleBand.Demoralised => "RP_MoraleDemoralised".Translate(),
            MoraleBand.Low => "RP_MoraleLow".Translate(),
            MoraleBand.High => "RP_MoraleHigh".Translate(),
            _ => null,
        };
    }
}
