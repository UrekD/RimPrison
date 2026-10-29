using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPVocationDefOf
    {
        public static ProgramDef RP_KitchenSafety;
        public static ProgramDef RP_Tailoring;
        public static InmateJobDef RP_JobTailoring;

        static RPVocationDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPVocationDefOf));
    }

    /// <summary>
    /// Vocational training (gap analysis 4, section 1.7): what the courses that teach a job or a trade change. Kitchen safety gates the kitchen job and lowers the food scare's chance
    /// (<see cref="FoodScare.SafetyFactor"/>); tailoring opens the tailoring job (<see cref="Tailoring"/>); a program that certifies a trade (<see cref="ProgramDef.trade"/>) sends its graduate out with a better
    /// chance of a job in place on parole (<see cref="ParoleLife.JobInPlaceChance"/>) and a lower reoffend band (<see cref="ParoleSystem.ReoffendChance"/>); and the certificate is the qualification a job interview
    /// can be arranged on before release (<see cref="ReleasePrep"/>: the warden's interview wins a job after the departure, see <see cref="Supervision"/>).
    /// </summary>
    public static class Vocation
    {
        /// <summary>The trade this inmate is certified in (the first they passed), or null.</summary>
        public static ProgramDef Trade(CompInmate c)
        {
            foreach (ProgramDef p in c.passedPrograms)
                if (p.trade) return p;
            return null;
        }

        public static bool HasTrade(CompInmate c) => Trade(c) != null;
    }
}
