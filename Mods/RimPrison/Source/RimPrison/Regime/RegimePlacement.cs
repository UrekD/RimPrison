using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>Where the regime wants an inmate right now (used by the compliance grade and the tests).</summary>
    public static class RegimePlacement
    {
        /// <summary>Is the inmate where the regime wants them (own cell for lockup/sleep, the activity's zones otherwise, in the patches kept for their level or for all)?</summary>
        public static bool InExpectedPlace(Pawn p, RegimeActivityDef act, PrisonMap pm)
        {
            if (!p.Inmate().processed) return true;
            // Being moved in an evacuation (out of a wing that burns, or back to the bed): where the prison has put them.
            if (EvacuationSystem.Involves(p)) return true;
            // A meal the survival fallback sent them to (no patch their level may use had one to reach) is eaten in place, wherever it lies.
            if (KeptRooms.EatingOutOfNecessity(p)) return true;
            // Out of the cell only with a guard (supermax): in the cell, or on a walk with one. Anywhere else (the general showers, say) is where nobody should find them, whatever the slot says.
            if (p.Inmate() is { security: { escortOnly: true }, forcedActivity: null } escortOnly && !act.lockup)
                return escortOnly.leg != null || p.GetRoom() == InmateUtility.CellOf(p);
            SecurityLevelDef level = p.Inmate().security;
            // In a patch kept for other levels is out of place even in a class, with a visitor or on the way to a hearing (the room was kept after they were sent there, or they moved up or down a level).
            // Except assigned work (the cleaner, the clerk, the laundry and the kitchen go where the job is, kept room or not); the survival meal is let through at the top.
            if (!pm.zones.Allows(p.Position, level) && !LaborUtility.IsWorking(p, p.Inmate())) return false;
            if (Sanctioned(p)) return true;
            // Work: where their job is (the zone the job waits in, or what they are doing for it); no job or no place for it, in the cell, as the work worker sends them.
            if (act == RPLaborDefOf.RP_Work && p.Inmate().job is { } job && (job.requiredProgram == null || p.Inmate().passedPrograms.Contains(job.requiredProgram)))
            {
                if (LaborUtility.IsWorking(p, p.Inmate())) return true;
                if (job.zone != null && pm.zones.Any(job.zone)) return pm.zones.ZoneAt(p.Position) == job.zone;
                return p.GetRoom() == InmateUtility.CellOf(p);
            }
            // (A zone with no patch their level may use is as good as none: the regime sends them on to the fallback, and so is this.)
            while (act != null && !act.lockup && act.zones.Count > 0 && !act.zones.Exists(z => pm.zones.AnyFor(z, level))) act = act.fallback;
            if (act == null) return true;
            if (act.lockup || act.zones.Count == 0) return p.GetRoom() == InmateUtility.CellOf(p);
            PrisonZoneDef here = pm.zones.ZoneAt(p.Position);
            if (act.zones.Contains(here)) return true;
            // Free time after a shower: anywhere free time allows.
            return act.fallback != null && act.fallback.zones.Contains(here);
        }

        /// <summary>
        /// Somewhere the regime itself has sent them, whatever slot it is: in a class or a service (programs and the chapel use the same job), at a funeral, at a parole hearing, with a visitor, or hurt (in a
        /// medical bed). Counting these as out of place made a prison that runs programs and services look less compliant the more it ran them.
        /// </summary>
        public static bool Sanctioned(Pawn p)
        {
            if (p.Inmate() is not { forcedActivity: null }) return false;
            JobDef job = p.CurJobDef;
            if (job == RPProgramDefOf.RP_AttendClass || job == RPParoleDefOf.RP_AttendHearing || job == RPVisitDefOf.RP_VisitTalk || job == RPFuneralDefOf.RP_AttendFuneral) return true;
            return p.InBed() && p.CurrentBed().Medical;
        }

        /// <summary>
        /// Whether the inmate is part of the compliance reading: not one who lies downed. They are not in place and not out of it (counted as in place, the fights that down them paid the "order" grade: with 200-700 downed
        /// inmate-hours a week that was up to ten points of compliance in a violent prison), and the health grade already holds the injured against the prison.
        /// </summary>
        public static bool InTheSample(Pawn p) => !p.Downed;
    }
}
