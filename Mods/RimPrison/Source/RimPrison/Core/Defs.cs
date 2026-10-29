using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>A painted prison zone type (PA "room"): canteen, yard, showers, ...</summary>
    public class PrisonZoneDef : Def
    {
        public Color color = Color.white;
        /// <summary>Inmates may be here when the regime sends them (canteen, yard, ...).</summary>
        public bool inmateAccess = true;
        /// <summary>Inmates may never be here; doors into it are staff-only for them.</summary>
        public bool staffOnly;
        /// <summary>A gang can hold a patch of this zone as its turf (see <see cref="GangTurf"/>).</summary>
        public bool gangTurf;
        public int uiOrder;
        /// <summary>A kind inmates share (yard, canteen, showers...): each patch of it can be kept for some security levels (see <see cref="ZoneGrid.SetKept"/>).</summary>
        public bool keepable;
        /// <summary>Things the zone needs to work; listed as problems in the zone readout when missing.</summary>
        public List<ThingDef> requiredThings = new List<ThingDef>();

        /// <summary>Cell-block wing: beds painted with it only take inmates of this security level.</summary>
        public SecurityLevelDef security;

        /// <summary>A yard that only this level uses, one inmate at a time (see <see cref="SecurityLevelDef.yardAlone"/>): no other level's free time goes there.</summary>
        public SecurityLevelDef yardFor;

        /// <summary>1-based index in the map's zone grid (0 = no zone).</summary>
        [Unsaved] public byte gridIndex;

        private static PrisonZoneDef[] byGridIndex = new PrisonZoneDef[1];

        public static PrisonZoneDef FromGridIndex(byte index) => index < byGridIndex.Length ? byGridIndex[index] : null;

        public static void AssignGridIndices()
        {
            List<PrisonZoneDef> defs = DefDatabase<PrisonZoneDef>.AllDefsListForReading;
            if (defs.Count > 254) throw new InvalidOperationException("too many PrisonZoneDefs for a byte grid");
            byGridIndex = new PrisonZoneDef[defs.Count + 1];
            for (int i = 0; i < defs.Count; i++)
            {
                defs[i].gridIndex = (byte)(i + 1);
                byGridIndex[i + 1] = defs[i];
            }
        }
    }

    /// <summary>One regime slot type. Behavior lives in the worker.</summary>
    public class RegimeActivityDef : Def
    {
        public Color color = Color.gray;
        /// <summary>Inmates are confined to their cells; cell doors lock once the occupants are inside.</summary>
        public bool lockup;
        /// <summary>Zones the activity takes place in, in preference order.</summary>
        public List<PrisonZoneDef> zones = new List<PrisonZoneDef>();
        /// <summary>Activity used instead when none of the zones exist on the map.</summary>
        public RegimeActivityDef fallback;
        public Type workerClass = typeof(RegimeActivityWorker);
        /// <summary>Offered in the regime painter (solitary is forced by punishment, not scheduled).</summary>
        public bool paintable = true;

        [Unsaved] private RegimeActivityWorker workerInt;

        public RegimeActivityWorker Worker
        {
            get
            {
                if (workerInt == null)
                {
                    workerInt = (RegimeActivityWorker)Activator.CreateInstance(workerClass);
                    workerInt.def = this;
                }
                return workerInt;
            }
        }
    }

    /// <summary>Security category of an inmate: pay, uniform and default regime.</summary>
    public class SecurityLevelDef : Def
    {
        public Color color = Color.white;
        /// <summary>Short name for tables (Min, Med, Max).</summary>
        public string labelShort;
        public int intakeGrant;
        public int dailyPayment;
        public int defaultIntakePerDay;
        /// <summary>Multiplier on misconduct chances.</summary>
        public float dangerFactor = 1f;
        public int uiOrder;
        /// <summary>24 activity defNames, hour 0 first.</summary>
        public List<RegimeActivityDef> defaultRegime = new List<RegimeActivityDef>();
        public List<CrimeDef> crimes = new List<CrimeDef>();
        /// <summary>Conduct ladder: the level for well-behaved inmates (lower security) and for repeat offenders.</summary>
        public SecurityLevelDef promoteTo;
        public SecurityLevelDef demoteTo;
        public int promoteAfterCleanDays = 5;
        public float promoteMaxTension = 30f;
        public int promotePrograms = 1;
        /// <summary>Only housed in beds painted with this level's wing (death row), never overcrowded.</summary>
        public bool wingOnly;
        /// <summary>Meals are brought to the cell (no canteen time in the regime).</summary>
        public bool cellMeals;
        /// <summary>Leaves the cell only with a guard (supermax): the cell door is locked in every hour of the regime, a guard collects them for the shower and the yard and walks them back
        /// (see <see cref="EscortWalk"/>), and nothing else in the regime takes them out.</summary>
        public bool escortOnly;
        /// <summary>Takes the yard alone: only the yard zones that name this level (<see cref="PrisonZoneDef.yardFor"/>), and the slot is skipped while another inmate is in that patch.</summary>
        public bool yardAlone;
        /// <summary>Housed only in a padded cell: a bed of its own in a room of its own, and the bed a padded one (see <see cref="PaddedCells"/>). Psychiatric care.</summary>
        public bool paddedOnly;
        /// <summary>The state does not send inmates to this level (no intake row, none on the roster): they come only by being moved here.</summary>
        public bool referralOnly;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors()) yield return e;
            if (defaultRegime.Count != 24) yield return $"defaultRegime has {defaultRegime.Count} entries, needs 24";
            if (GetModExtension<DeathRowExtension>() is { commuteTo: null }) yield return "DeathRowExtension needs commuteTo";
        }
    }

    public class CrimeDef : Def
    {
        public IntRange sentenceDays = new IntRange(10, 20);
        /// <summary>Chance that the sentence is life (no release).</summary>
        public float lifeChance;
        public bool violent;
        public bool drugRelated;
    }

    public class LedgerCategoryDef : Def
    {
        public bool income;
        public int uiOrder;
    }

    /// <summary>A hireable staff role: allowed work, wage, and how many the candidate pool offers.</summary>
    public class StaffRoleDef : Def
    {
        public int dailyWage;
        public int hireCost;
        public List<WorkTypeDef> workTypes = new List<WorkTypeDef>();
        /// <summary>Skills the generated candidate is guaranteed to be decent at.</summary>
        public List<SkillDef> keySkills = new List<SkillDef>();
        public Color uniformColor = Color.white;
        public int uiOrder;
        /// <summary>Shown in the hiring list (riot police are called in, not hired).</summary>
        public bool hireable = true;
        /// <summary>Candidates must be capable of violence (guards, riot police).</summary>
        public bool requiresViolence;
        /// <summary>Issued on hiring (armed guards).</summary>
        public ThingDef weapon;
        /// <summary>Does guard work: patrols, searches, responds to trouble, counts as a watcher, mans towers.</summary>
        public bool guardDuties;
        /// <summary>Animal issued with the role (K9 handler's dog).</summary>
        public PawnKindDef companion;
        /// <summary>A visiting official (the state's overseer): leaves at the end of the visit without a note in the chronicle.</summary>
        public bool visitor;
        /// <summary>The warden on the floor (see <see cref="WardenSystem"/>): the player's own presence, who cannot be let go, sent home, put on a shift, retrained or led astray.</summary>
        public bool warden;
        /// <summary>Which group of the colonist bar the role's staff stand in (see <see cref="StaffBar"/>); a role that says nothing is in the last one.</summary>
        public StaffBarGroup barGroup = StaffBarGroup.Other;
    }

    [DefOf]
    public static class RPStaffRoleDefOf
    {
        public static StaffRoleDef RP_Chaplain;
        public static StaffRoleDef RP_Psychologist;
        public static StaffRoleDef RP_Janitor;
        public static StaffRoleDef RP_Cook;
        public static StaffRoleDef RP_Doctor;
        public static StaffRoleDef RP_Workman;
        public static StaffRoleDef RP_ArmedGuard;
        public static StaffRoleDef RP_RiotPolice;

        static RPStaffRoleDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPStaffRoleDefOf));
    }

    /// <summary>Core defs the game has no DefOf for (so that no def name is a string in the code).</summary>
    [DefOf]
    public static class RPVanillaDefOf
    {
        public static MainButtonDef Work;
        public static MainButtonDef Assign;

        public static HediffDef HeartAttack;
        public static HediffDef Flu;

        public static TraitDef Nerves;

        public static NeedDef Joy;
        public static NeedDef Outdoors;

        public static WorkTypeDef Cooking;
        public static WorkTypeDef Patient;
        public static WorkTypeDef PatientBedRest;
        public static WorkTypeDef BasicWorker;

        public static WorkGiverDef GrowerSow;
        public static WorkGiverDef GrowerHarvest;

        public static MentalBreakDef GiveUpExit;
        public static MentalBreakDef RunWild;
        public static MentalBreakDef Jailbreaker;

        public static ThingDef Apparel_Jacket;
        public static ThingDef Apparel_BasicShirt;
        public static ThingDef Apparel_Pants;
        public static ThingDef Apparel_FlakVest;
        public static ThingDef Apparel_SimpleHelmet;
        public static ThingDef MeleeWeapon_Club;

        public static RecipeDef CookMealSimple;
        public static RecipeDef CookMealSimpleBulk;
        public static RecipeDef CookMealFine_Veg;
        public static RecipeDef CookMealFineBulk_Veg;

        public static SoundDef Shuttle_PawnLoaded;

        static RPVanillaDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPVanillaDefOf));
    }

    [DefOf]
    public static class RPDefOf
    {
        public static PrisonZoneDef RP_Canteen;
        public static PrisonZoneDef RP_Yard;
        public static PrisonZoneDef RP_CommonRoom;
        public static PrisonZoneDef RP_Shower;
        public static PrisonZoneDef RP_Deliveries;
        public static PrisonZoneDef RP_Exports;
        public static PrisonZoneDef RP_Holding;
        public static PrisonZoneDef RP_StaffOnly;

        public static DesignationCategoryDef RP_PrisonZones;

        public static ScenarioDef RP_InheritedPrison;
        public static ScenarioDef RP_StatePenitentiary;
        public static ScenarioDef RP_NewPrison;

        public static StorytellerDef RP_TheBoard;

        public static PawnKindDef RP_Inmate;

        public static ThingDef RP_SecurityCamera;

        public static LetterDef RP_DecisionLetter;

        public static ThoughtDef RP_CellTurnedOver;
        public static ThoughtDef RP_Grudge;

        public static ReputationDef RP_Notorious;

        public static SupplyCatalogDef RP_SupplyCatalog;

        public static RegimeActivityDef RP_Sleep;
        public static RegimeActivityDef RP_Lockup;
        public static RegimeActivityDef RP_FreeTime;
        public static RegimeActivityDef RP_ShowerTime;

        public static LedgerCategoryDef RP_InmatePayments;
        public static LedgerCategoryDef RP_IntakeGrants;
        public static LedgerCategoryDef RP_Wages;
        public static LedgerCategoryDef RP_Construction;
        public static LedgerCategoryDef RP_Supplies;
        public static LedgerCategoryDef RP_Hiring;
        public static LedgerCategoryDef RP_Bureaucracy;
        public static LedgerCategoryDef RP_Land;
        public static LedgerCategoryDef RP_ExportSales;
        public static LedgerCategoryDef RP_Fines;
        public static LedgerCategoryDef RP_Grants;
        public static LedgerCategoryDef RP_Programs;
        public static LedgerCategoryDef RP_Utilities;
        public static LedgerCategoryDef RP_InmateWages;
        public static LedgerCategoryDef RP_CommissarySales;
        public static LedgerCategoryDef RP_Laundry;
        public static LedgerCategoryDef RP_StateLoan;
        public static LedgerCategoryDef RP_LoanRepayment;

        public static NeedDef RP_Hygiene;
        public static NeedDef RP_CleanClothes;
        public static NeedDef RP_Bladder;

        public static JobDef RP_UseFixture;

        public static FactionDef RP_Outside;

        static RPDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPDefOf));
    }
}
