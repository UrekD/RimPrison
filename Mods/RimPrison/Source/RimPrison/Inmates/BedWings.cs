using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Vanilla's wardens carry a downed inmate, or escort one who has no bed they can reach, to the closest bed that is valid for the guard (<c>WorkGiver_Warden_TakeToBed</c>, <c>RestUtility.FindBedFor</c>),
    /// and the inmate takes it as their own: it knows no wings. A guard can go through the staff-only door of the supermax unit and into the solitary cells, so a protective custody inmate, a minimum
    /// security one, a man on death row woke up in a supermax cell (or the hole) behind a door they cannot open, too far from a canteen counter to walk to a meal, and starved there (the showcase's
    /// "a protective inmate sleeping more than 50 cells from a counter" and its starving punished inmates). The search is kept to the beds the prison would give them (<see cref="Allows"/>), as the
    /// prison's own searches (<see cref="IntakeSystem.FreePrisonerBeds"/>) always did.
    /// </summary>
    public static class BedWings
    {
        /// <summary>
        /// Whether the bed may be offered to this inmate by the vanilla search: their own bed (and the one they go back to) always; a medical bed; a bed of their level's wing or of no wing (a level that
        /// keeps to its wing only the wing's, <see cref="SecurityLevelDef.wingOnly"/>), in a wing that takes their sex; a solitary bed only when they are to serve a punishment.
        /// </summary>
        public static bool Allows(Building_Bed bed, Pawn sleeper, CompInmate c, Pawn traveler = null)
        {
            // (A dying inmate is offered the medical beds of their own wing first: see Hospice.)
            if (bed.Medical) return !Hospice.Restricts(bed, sleeper, c, traveler);
            if (bed.OwnersForReading.Contains(sleeper) || c.homeBed == bed) return true;
            SecurityLevelDef level = c.security;
            if (level == null) return true;
            if (PunishmentUtility.IsSolitaryBed(bed)) return c.forcedActivity != null || c.pendingPunishmentHours > 0 || sleeper.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained);
            SecurityLevelDef wing = IntakeSystem.WingOf(bed);
            if (wing != null && wing != level || wing == null && level.wingOnly) return false;
            return Cellmates.Accepts(bed, c);
        }
    }

    [HarmonyPatch(typeof(RestUtility), nameof(RestUtility.IsValidBedFor))]
    public static class Patch_RestUtility_IsValidBedFor_KeepToTheWing
    {
        public static void Postfix(Thing bedThing, Pawn sleeper, Pawn traveler, ref bool __result)
        {
            // (Asked for every bed of a search: the cheap tests first.)
            if (!__result || PrisonGame.Instance == null || bedThing is not Building_Bed bed || sleeper?.guest is not { IsPrisoner: true } || sleeper.Inmate() is not { activeFlag: true } c) return;
            if (!BedWings.Allows(bed, sleeper, c, traveler)) __result = false;
        }
    }
}
