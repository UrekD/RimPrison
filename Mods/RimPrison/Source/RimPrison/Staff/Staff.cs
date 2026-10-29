using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace RimPrison
{
    /// <summary>The staff record: role decides allowed work and daily wage.</summary>
    public class CompStaff : ThingComp
    {
        public StaffRoleDef role;
        /// <summary>hiredTick / passedOverTick before there is one (any other value, negative or not, is a real tick).</summary>
        public const int NotHired = -1, NotPassedOver = -1;
        public int hiredTick = NotHired;
        /// <summary>When they took their present role (-1: since they were hired): promotion counts the service in it, not the service before a retraining.</summary>
        public int roleSinceTick = -1;
        /// <summary>Guard deployment: patrol only this zone (null = wherever inmates are).</summary>
        public PrisonZoneDef patrolZone;
        /// <summary>Temporary staff (riot police) leave at this tick.</summary>
        public int leaveAtTick = -1;
        public int bribesTaken;
        /// <summary>Posted by the state (its security chief, see <see cref="SpecialMeasures"/>): cannot be let go, and the warden does not set their patrols.</summary>
        public bool statePosted;
        /// <summary>The state's chief and the warden on the floor: nothing here is the warden's to let go, send home, retrain, retire or lead astray (see <see cref="WardenSystem"/>).</summary>
        public bool Protected => statePosted || role is { warden: true };
        /// <summary>The warden's abilities are ready again at these ticks (-1: ready): addressing the canteen, walking a wing, a word with an inmate.</summary>
        public int addressReadyTick = -1, walkReadyTick = -1, wordReadyTick = -1;
        /// <summary>Known to the warden (caught, convicted or turned).</summary>
        public bool exposedCorrupt;
        /// <summary>Hidden 0..100 (-1: not rolled yet); fixers approach staff below 65.</summary>
        public float integrity = -1f;
        public bool corrupt;
        /// <summary>The inmate (and their gang) this corrupt member of staff works for.</summary>
        public Pawn patron;
        public GangDef patronGang;
        public bool carryingToday;
        public int lastApproachTick = -999999;
        /// <summary>Internal affairs' suspicion 0..100 and the reasons behind it.</summary>
        public float suspicion;
        public List<string> evidence = new List<string>();
        /// <summary>Under arrest (officer or suspect): may pass locked doors. Read on path threads.</summary>
        [Unsaved] public volatile bool passLocked;
        public bool iaOpen;
        public int suspendedUntilTick = -1;
        /// <summary>A disciplinary suspension (upheld complaints) and a stand-down on full pay while a complaint is investigated: off duty until then, and no business of internal affairs.</summary>
        public int disciplineUntilTick = -1, standDownUntilTick = -1;
        /// <summary>Turned by the warden: still on the payroll, reporting on their clients.</summary>
        public bool doubleAgent;
        public int commendations;
        /// <summary>Complaints upheld against them lately (each fades after a month; each puts a promotion off, the third is a suspension), when the last was, and how many true complaints
        /// reached the warden's desk against them (hidden: a pattern, and a permanent one: it does not fade like the upheld count).</summary>
        public int complaintsUpheld, lastUpheldTick = -1, trueComplaints;
        /// <summary>Career rank: 0 officer, 1 senior, 2 lead (see StaffCareers). Old saves have none: officer.</summary>
        public int rank;
        /// <summary>Passed over for promotion at this tick (not offered again for a while).</summary>
        public int passedOverTick = NotPassedOver;
        /// <summary>Paid leave (burnout): at home until this tick.</summary>
        public int leaveUntilTick = -1;
        /// <summary>Away on a training course (the leave above); trained last at this tick.</summary>
        public bool trainingPending;
        /// <summary>Their leave is a rest (burnout), whatever else it covers (a course booked before it).</summary>
        public bool restPending;
        public int lastTrainedTick = -1;
        /// <summary>Wary of the inmate who put them down (thing ID, -1 none; the name outlives them) until the tick.</summary>
        public int waryOfId = -1, waryUntilTick = -1;
        /// <summary>An inmate's blow last landed on them at this tick (-1 never): a wound that bleeds out later is that inmate's doing (see <see cref="DeathAftermath.RecentlyAssaulted"/>).</summary>
        public int assaultedTick = -1;
        public string waryOfName;
        /// <summary>A burnout warning was ignored at this tick (they may quit while still miserable).</summary>
        public int burnoutIgnoredTick = -1;
        public bool debtAsked, retireAsked, rookieConned;
        /// <summary>Service record (see Story).</summary>
        public List<LifeEntry> life = new List<LifeEntry>();
        /// <summary>K9 handler's dog.</summary>
        public Pawn dog;
        /// <summary>A dead K9 is replaced from the kennel at this tick.</summary>
        public int dogReplaceTick = -1;
        /// <summary>Guard shift: sleeps by day, works by night.</summary>
        public bool nightShift;
        /// <summary>The shift the warden pinned them to (the roster; auto: the daily balance decides) and the nights worked in a row (see <see cref="Roster.NightsAgainAfter"/>).</summary>
        public ShiftPin shiftPin;
        public int nightsRun;
        /// <summary>The day (<see cref="Ledger.CurrentDay"/>) of the last night worked, which the run of nights goes by: a night missed (leave, a suspension, a day off) breaks the run. -1 none.</summary>
        public int lastNightDay = -1;
        /// <summary>On strike (no work) until this tick.</summary>
        public int strikeUntilTick = -1;
        /// <summary>Let go while neither on a map nor at home (carried, in a pod): done once they are back on a map. 0 none; 1 let go, 2 let go and arrested; 4 added: not written up.</summary>
        public int pendingFire;
        /// <summary>The record of a former inmate of this prison who was hired from the register (see <see cref="ExInmates"/>), else null.</summary>
        public ExInmatePast formerPast;
        /// <summary>A guard with kin inside was asked where they want to stand (once, see <see cref="Kin.AskIfDue"/>), and the answer: kept off the wing their kin is in (no patrol zone there, no post there, no patrol among its inmates).</summary>
        public bool kinAsked, kinKeepOff;
        /// <summary>Unit officer (see <see cref="UnitOfficers"/>): the wing they were named to (null: none), when (the days in post; -1 none), and the patrol zone they had before, put back when they leave the post.</summary>
        public SecurityLevelDef unitWing;
        public int unitSinceTick = -1;
        public PrisonZoneDef unitPrevZone;

        public Pawn Pawn => parent as Pawn;

        public override void PostExposeData()
        {
            Scribe_Defs.Look(ref role, "rpRole");
            Scribe_Values.Look(ref hiredTick, "rpHired", NotHired);
            Scribe_Values.Look(ref roleSinceTick, "rpRoleSince", -1);
            Scribe_Defs.Look(ref patrolZone, "rpPatrolZone");
            Scribe_Values.Look(ref leaveAtTick, "rpLeaveAt", -1);
            Scribe_Values.Look(ref leaveUntilTick, "rpLeaveUntil", -1);
            Scribe_Values.Look(ref trainingPending, "rpTrainingPending");
            Scribe_Values.Look(ref restPending, "rpRestPending");
            Scribe_Values.Look(ref lastTrainedTick, "rpLastTrained", -1);
            Scribe_Values.Look(ref waryOfId, "rpWaryOf", -1);
            Scribe_Values.Look(ref assaultedTick, "rpAssaulted", -1);
            Scribe_Values.Look(ref waryUntilTick, "rpWaryUntil", -1);
            Scribe_Values.Look(ref waryOfName, "rpWaryOfName");
            Scribe_Values.Look(ref burnoutIgnoredTick, "rpBurnoutIgnored", -1);
            Scribe_Values.Look(ref debtAsked, "rpDebtAsked");
            Scribe_Values.Look(ref retireAsked, "rpRetireAsked");
            Scribe_Values.Look(ref rookieConned, "rpRookieConned");
            Scribe_Values.Look(ref bribesTaken, "rpBribes");
            Scribe_Values.Look(ref exposedCorrupt, "rpExposedCorrupt");
            Scribe_Values.Look(ref integrity, "rpIntegrity", -1f);
            Scribe_Values.Look(ref corrupt, "rpCorrupt");
            Scribe_References.Look(ref patron, "rpPatron");
            Scribe_Defs.Look(ref patronGang, "rpPatronGang");
            Scribe_Values.Look(ref carryingToday, "rpCarrying");
            Scribe_Values.Look(ref lastApproachTick, "rpLastApproach", -999999);
            Scribe_Values.Look(ref suspicion, "rpSuspicion");
            Scribe_Collections.Look(ref evidence, "rpEvidence", LookMode.Value);
            Scribe_Values.Look(ref iaOpen, "rpIaOpen");
            Scribe_Values.Look(ref suspendedUntilTick, "rpSuspendedUntil", -1);
            Scribe_Values.Look(ref disciplineUntilTick, "rpDisciplineUntil", -1);
            Scribe_Values.Look(ref standDownUntilTick, "rpStandDownUntil", -1);
            Scribe_Values.Look(ref doubleAgent, "rpDoubleAgent");
            Scribe_Collections.Look(ref life, "rpLife", LookMode.Deep);
            Scribe_Values.Look(ref commendations, "rpCommendations");
            Scribe_Values.Look(ref rank, "rpRank");
            Scribe_Values.Look(ref complaintsUpheld, "rpComplaintsUpheld");
            Scribe_Values.Look(ref lastUpheldTick, "rpLastUpheld", -1);
            Scribe_Values.Look(ref trueComplaints, "rpTrueComplaints");
            Scribe_Values.Look(ref passedOverTick, "rpPassedOver", NotPassedOver);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                evidence ??= new List<string>();
                life ??= new List<LifeEntry>();
            }
            Scribe_References.Look(ref dog, "rpDog");
            Scribe_Values.Look(ref strikeUntilTick, "rpStrikeUntil", -1);
            Scribe_Values.Look(ref dogReplaceTick, "rpDogReplace", -1);
            Scribe_Values.Look(ref nightShift, "rpNightShift");
            Scribe_Values.Look(ref shiftPin, "rpShiftPin");
            Scribe_Values.Look(ref nightsRun, "rpNightsRun");
            Scribe_Values.Look(ref lastNightDay, "rpLastNightDay", -1);
            Scribe_Values.Look(ref pendingFire, "rpPendingFire");
            Scribe_Values.Look(ref statePosted, "rpStatePosted");
            Scribe_Values.Look(ref addressReadyTick, "rpAddressReady", -1);
            Scribe_Values.Look(ref walkReadyTick, "rpWalkReady", -1);
            Scribe_Values.Look(ref wordReadyTick, "rpWordReady", -1);
            Scribe_Deep.Look(ref formerPast, "rpFormerPast");
            Scribe_Values.Look(ref kinAsked, "rpKinAsked");
            Scribe_Values.Look(ref kinKeepOff, "rpKinKeepOff");
            Scribe_Defs.Look(ref unitWing, "rpUnitWing");
            Scribe_Values.Look(ref unitSinceTick, "rpUnitSince", -1);
            Scribe_Defs.Look(ref unitPrevZone, "rpUnitPrevZone");
            // (A wing whose def is gone loads as no post: its days and the patrol to put back go with it.)
            if (Scribe.mode == LoadSaveMode.PostLoadInit && unitWing == null) (unitSinceTick, unitPrevZone) = (-1, null);
        }

        /// <summary>Back on a map after being let go while carried or in a pod: now they go.</summary>
        public void ResolvePendingFire()
        {
            int pending = pendingFire;
            pendingFire = 0;
            if (pending != 0 && role != null && Pawn is { Spawned: true, Dead: false }) StaffUtility.Fire(Pawn, arrest: (pending & 2) != 0, record: (pending & 4) == 0);
        }

        public override string CompInspectStringExtra()
        {
            if (role == null || Pawn.Faction != Faction.OfPlayer) return null;
            return "RP_StaffLine".Translate(StaffCareers.TitleCap(this), Money.Format(StaffCareers.Wage(this))) + StatusLines();
        }

        /// <summary>Standing beyond the role: double agent, known corruption, suspension, internal affairs.</summary>
        public string StatusLines()
        {
            string line = commendations > 0 ? "\n" + "RP_StaffCommendations".Translate(commendations) : "";
            line += ComplaintSystem.StaffLine(Pawn, this);
            if (HostageCrisis.IsHeld(Pawn)) line += "\n" + "RP_StaffHeld".Translate();
            if (formerPast != null) line += "\n" + "RP_StaffFormerInmate".Translate(formerPast.daysServed.ToString("0"));
            foreach (string kin in Kin.Lines(Pawn)) line += "\n" + kin;
            if (kinKeepOff) line += "\n" + "RP_StaffKeptOff".Translate();
            if (UnitOfficers.RecordLine(this) is { } unit) line += "\n" + unit;
            if (statePosted) line += "\n" + "RP_StaffStatePosted".Translate();
            if (role is { warden: true }) line += "\n" + "RP_StaffWarden".Translate();
            if (doubleAgent) line += "\n" + "RP_StaffDoubleAgent".Translate();
            else if (exposedCorrupt) line += "\n" + "RP_StaffCorrupt".Translate(bribesTaken);
            if (CorruptionSystem.Suspended(this))
                line += "\n" + (CorruptionSystem.StoodDownOnly(this) ? "RP_StaffStoodDown" : CorruptionSystem.IaSuspended(this) ? "RP_StaffSuspended" : "RP_StaffDisciplined").Translate(((CorruptionSystem.OffUntil(this) - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour).ToString("F0"));
            else if (suspicion >= 1f && !doubleAgent) line += "\n" + "RP_StaffSuspicion".Translate(suspicion.ToString("F0"));
            return line;
        }

        public override void CompTickInterval(int delta)
        {
            if (pendingFire != 0 && Pawn?.Spawned == true && !HostageCrisis.IsHeld(Pawn))
            {
                ResolvePendingFire();
                return;
            }
            if (role != null && parent.IsHashIntervalTick(GenDate.TicksPerHour, delta) && Pawn?.Spawned == true && Pawn.Faction == Faction.OfPlayer)
                CorruptionSystem.Hourly(Pawn, this);
            if (role is { warden: true } && parent.IsHashIntervalTick(250, delta) && Pawn?.Spawned == true && Pawn.Faction == Faction.OfPlayer) WardenSystem.Rare(Pawn);
            if (strikeUntilTick >= 0 && parent.IsHashIntervalTick(250, delta) && Find.TickManager.TicksGame >= strikeUntilTick) strikeUntilTick = -1;
            // A course whose time ran out while they were kept on site (the prison is never left empty): done in-house.
            if (trainingPending && parent.IsHashIntervalTick(250, delta)) StaffTraining.CheckInHouse(Pawn, this);
            if (leaveAtTick >= 0 && parent.IsHashIntervalTick(250, delta) && Find.TickManager.TicksGame >= leaveAtTick && Pawn?.Spawned == true)
            {
                leaveAtTick = -1;
                // (A visiting official goes without a word in the chronicle or a message: they were never let go.)
                StaffUtility.Fire(Pawn, record: role?.visitor != true);
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (Pawn.Faction != Faction.OfPlayer || !Pawn.IsColonist) yield break;
            // Anyone on the payroll can be pinned to the compact colonist bar (the warden is always on it).
            // (Not the warden, always on the bar, nor the state's appointees, whose one gizmo is the locked one.)
            if (role != null && !Protected) yield return StaffBar.PinGizmo(Pawn);
            // The state's own chief: nothing here is the warden's to change.
            if (statePosted)
            {
                var locked = new Command_Action { defaultLabel = "RP_StatePostedGizmo".Translate(), defaultDesc = "RP_StatePostedGizmoDesc".Translate(), icon = TexCommand.Attack };
                locked.Disable("RP_StatePostedGizmoDesc".Translate());
                yield return locked;
            }
            // A hostage: the warden can always bring the options back.
            if (HostageCrisis.IsHeld(Pawn) && HostageCrisis.Running() is { } crisis && crisis.A == Pawn)
            {
                yield return new Command_Action
                {
                    defaultLabel = "RP_Hostage_Decide".Translate(),
                    defaultDesc = "RP_Hostage_DecideDesc".Translate(),
                    icon = TexCommand.Attack,
                    action = () =>
                    {
                        ((HostageArc)crisis.def.Worker).Reask(crisis);
                        // (Straight to the options, not a letter to open as a second step.)
                        Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.arcId == crisis.id && !l.answered)?.OpenLetter();
                    },
                };
            }
            // (Taken hostage, the state's chief gets the crisis button above like anyone else: the crisis is the warden's to answer.)
            if (statePosted) yield break;
            // The warden on the floor: three things to do, and nothing to change about them (no role, no patrol zone, no letting go).
            if (role is { warden: true })
            {
                foreach (Gizmo g in WardenGizmos.For(Pawn, this)) yield return g;
                yield break;
            }
            if (GuardUtility.IsGuard(Pawn))
            {
                yield return new Command_Action
                {
                    defaultLabel = patrolZone != null ? "RP_PatrolZone".Translate(patrolZone.label) : "RP_PatrolAnywhere".Translate(),
                    defaultDesc = "RP_PatrolDesc".Translate(),
                    icon = TexCommand.SquadAttack,
                    action = () =>
                    {
                        // (A unit officer given another zone leaves the post: see UnitOfficers.SetPatrol.)
                        var options = new List<FloatMenuOption> { new FloatMenuOption("RP_PatrolAnywhere".Translate(), () => UnitOfficers.SetPatrol(Pawn, this, null)) };
                        foreach (PrisonZoneDef z in DefDatabase<PrisonZoneDef>.AllDefsListForReading)
                        {
                            if (!z.inmateAccess || !Kin.PatrolAllowed(Pawn, z)) continue;
                            PrisonZoneDef chosen = z;
                            options.Add(new FloatMenuOption(z.LabelCap, () => UnitOfficers.SetPatrol(Pawn, this, chosen)));
                        }
                        Find.WindowStack.Add(new FloatMenu(options));
                    },
                };
            }
            yield return new Command_Action
            {
                defaultLabel = "RP_ChangeRole".Translate(),
                defaultDesc = "RP_ChangeRoleDesc".Translate(),
                icon = TexCommand.Attack,
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    foreach (StaffRoleDef r in StaffUtility.Roles)
                    {
                        if (r == role || !Unlocks.IsUnlocked(r)) continue;
                        StaffRoleDef chosen = r;
                        // Retraining into a better-paid role costs the difference in hiring fees (weapons, dogs...).
                        int fee = Mathf.Max(0, r.hireCost - (role?.hireCost ?? 0));
                        string label = fee > 0 ? "RP_ChangeRoleOptionFee".Translate(r.LabelCap, Money.Format(r.dailyWage), Money.Format(fee)) : "RP_ChangeRoleOption".Translate(r.LabelCap, Money.Format(r.dailyWage));
                        options.Add(new FloatMenuOption(label, () =>
                        {
                            if (fee > 0 && !PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Hiring, fee, chosen.LabelCap))
                            {
                                Messages.Message("RP_CannotAfford".Translate(Money.Format(fee)), MessageTypeDefOf.RejectInput, historical: false);
                                return;
                            }
                            StaffUtility.ApplyRole(Pawn, chosen);
                        }));
                    }
                    options.Add(new FloatMenuOption("RP_Fire".Translate(), () => StaffUtility.Fire(Pawn)));
                    Find.WindowStack.Add(new FloatMenu(options));
                },
            };
        }
    }

    public static class StaffUtility
    {
        public static List<StaffRoleDef> Roles
        {
            get
            {
                var list = new List<StaffRoleDef>(DefDatabase<StaffRoleDef>.AllDefsListForReading);
                list.RemoveAll(r => !r.hireable);
                list.SortBy(r => r.uiOrder);
                return list;
            }
        }

        /// <summary>The state's chief and the warden on the floor (see <see cref="CompStaff.Protected"/>).</summary>
        public static bool IsProtected(Pawn p) => p?.GetComp<CompStaff>() is { Protected: true };

        /// <summary>Work types every staff member keeps (emergencies and self-care).</summary>
        internal static bool AlwaysAllowed(WorkTypeDef w) => w == WorkTypeDefOf.Firefighter || w == RPVanillaDefOf.Patient || w == RPVanillaDefOf.PatientBedRest || w == RPVanillaDefOf.BasicWorker;

        /// <param name="record">Into the chronicle and their service record (not for temporary riot police).</param>
        public static void ApplyRole(Pawn p, StaffRoleDef role, bool record = true)
        {
            CompStaff comp = p.GetComp<CompStaff>();
            if (comp == null) return;
            StaffRoleDef old = comp.role;
            comp.role = role;
            StaffCommute.StaffVersion++;
            StaffBar.MarkDirty();
            // Another role starts at the bottom again (the rank and the 15% per rank belong to the role they earned it in).
            if (old != role)
            {
                comp.rank = 0;
                if (old != null) comp.roleSinceTick = Find.TickManager.TicksGame; // (a retraining; the first role is the hire)
                StaffCareers.InvalidateLeads();
                // Retrained out of guard work: a unit officer's post goes with it.
                if (role is not { guardDuties: true }) UnitOfficers.Unassign(p, comp);
            }
            if (record && role != null && old != role)
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, old == null ? "RP_Chr_Hired".Translate(p.LabelShort, role.label) : "RP_Chr_Retrained".Translate(p.LabelShort, old.label, role.label), p);
            // Role equipment: hand in the old role's weapon and dog, receive the new role's.
            if (old?.weapon != null && old.weapon != role?.weapon) ReturnWeapons(p, old.weapon);
            if (role?.weapon != null && p.Spawned && p.equipment?.Primary?.def != role.weapon) IssueWeapon(p, role);
            if (comp.dog != null && role?.companion == null) K9Utility.Notify_HandlerGone(comp);
            if (role?.companion != null && comp.dog == null && p.Spawned) K9Utility.IssueDog(p, comp);
            if (p.Spawned) IssueGear(p);
            if (comp.hiredTick == CompStaff.NotHired) comp.hiredTick = Find.TickManager.TicksGame;
            p.needs?.AddOrRemoveNeedsAsAppropriate();
            if (p.workSettings == null) return;
            p.workSettings.EnableAndInitializeIfNotAlreadyInitialized();
            // The warden's work tab is shut (see Patch_Pawn_GetDisabledWorkTypes_Warden): what the candidate was given before the role is taken off.
            if (role is { warden: true } || old is { warden: true }) p.Notify_DisabledWorkTypesChanged();
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (p.WorkTypeIsDisabled(w)) continue;
                int idx = role.workTypes.IndexOf(w);
                // The work type a class is taught through is always top priority: with manual priorities the class would otherwise wait behind the role's other work.
                int prio = idx >= 0 ? ProgramSystem.IsTeachWorkType(w) ? 1 : Mathf.Min(1 + idx, 4) : AlwaysAllowed(w) ? 1 : 0;
                p.workSettings.SetPriority(w, prio);
            }
        }

        /// <summary>The colonist best suited to a role: capable of its first work type, then by key skills.</summary>
        public static Pawn BestCandidate(List<Pawn> pawns, StaffRoleDef role)
        {
            Pawn best = null;
            float bestScore = float.MinValue;
            foreach (Pawn p in pawns)
            {
                float score = 0f;
                foreach (SkillDef s in role.keySkills) score += p.skills?.GetSkill(s)?.Level ?? 0;
                for (int i = 0; i < role.workTypes.Count; i++)
                    if (p.WorkTypeIsDisabled(role.workTypes[i])) score -= i == 0 ? 1000f : 20f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>A check's seam: the chronological age (in years) the generated candidates are made with (null: the game's own, as always).</summary>
        public static float? CandidateChronologicalAge;

        /// <summary>With the seam above set, the candidates' biological age (the generator raises the chronological age to it when it is the greater: a random biological age past the seam's 75 made a candidate of 78.6).</summary>
        private const float CandidateBiologicalAgeCap = 30f;

        /// <summary>A check's seam: a test that marks a generated candidate unfit for the role, as if they could not do its work (null: none).</summary>
        internal static Func<Pawn, bool> CandidateUnfit;

        /// <summary>A candidate who can do the role's work, or the last one made when twenty in a row could not (the appointees, the overseer and the riot police
        /// must have somebody; a hire goes through <see cref="TryGenerateCandidate"/> and refuses instead).</summary>
        public static Pawn GenerateCandidate(StaffRoleDef role)
        {
            TryGenerateCandidate(role, out Pawn p);
            return p;
        }

        /// <summary>A check's seam: how many times a hire told the population watcher that a colonist was gained (see <see cref="NotifyGainedColonist"/>).</summary>
        internal static int GainedColonistNotices;

        /// <summary>A colonist joined the player's side: the storyteller's population adaptation starts again (as <c>Pawn.SetFaction</c> and the generator tell it).</summary>
        internal static void NotifyGainedColonist(Pawn p)
        {
            GainedColonistNotices++;
            Find.StoryWatcher.watcherPopAdaptation.Notify_PawnEvent(p, PopAdaptationEvent.GainedColonist);
        }

        /// <summary>
        /// Up to twenty tries at a candidate able to do the role's work: false (with the last one made in <paramref name="p"/>, which the caller lets go) when none could. Every unfit candidate before it is discarded here.
        /// <paramref name="keepAdaptation"/>: the generator tells the population watcher about each pawn it makes for the player's side, which a candidate who may never join is not: the watcher's state is put back
        /// after each, and whoever hires the candidate tells it (see <see cref="NotifyGainedColonist"/>).
        /// </summary>
        public static bool TryGenerateCandidate(StaffRoleDef role, out Pawn p, bool keepAdaptation = false)
        {
            p = null;
            StoryWatcher_PopAdaptation watcher = Find.StoryWatcher.watcherPopAdaptation;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                // (The one before could not do the work.)
                if (p != null) ApplicantPool.Release(p);
                var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true, canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f, mustBeCapableOfViolence: role.requiresViolence, fixedChronologicalAge: CandidateChronologicalAge,
                    fixedBiologicalAge: CandidateChronologicalAge is { } years ? Math.Min(years, CandidateBiologicalAgeCap) : null);
                float adaptDays = watcher.AdaptDays;
                try
                {
                    p = PawnGenerator.GeneratePawn(request);
                }
                finally
                {
                    if (keepAdaptation)
                    {
                        watcher.ResetAdaptDays();
                        watcher.Debug_OffsetAdaptDays(adaptDays);
                    }
                }
                bool ok = !role.requiresViolence || !p.WorkTagIsDisabled(WorkTags.Violent);
                foreach (WorkTypeDef w in role.workTypes)
                    if (p.WorkTypeIsDisabled(w)) ok = false;
                if (CandidateUnfit != null && CandidateUnfit(p)) ok = false;
                foreach (SkillDef s in role.keySkills)
                {
                    SkillRecord rec = p.skills?.GetSkill(s);
                    if (rec != null && rec.Level < 6) rec.Level = Rand.RangeInclusive(6, 10);
                }
                if (ok) return true;
            }
            return false;
        }

        /// <summary>The agency's fee for a hire at once, with nobody to choose from: a multiple of the post's own (see <see cref="Dialog_Applicants"/>).</summary>
        public const int AgencyFeeFactor = 2;

        public static int AgencyFee(StaffRoleDef role) => role.hireCost * AgencyFeeFactor;

        /// <summary>Hires a member of staff into the prison; <paramref name="free"/>: they came with the facility (no hiring fee).</summary>
        public static bool Hire(Map map, StaffRoleDef role, bool free = false) => Hire(map, role, free, null);

        /// <summary>The agency's hire: a stranger, as an ordinary hire, at <see cref="AgencyFee"/>.</summary>
        public static bool HireThroughAgency(Map map, StaffRoleDef role) => Hire(map, role, free: false, null, fee: AgencyFee(role));

        /// <param name="candidate">Somebody already made (a former inmate, see <see cref="ExInmates"/>, or an applicant), who must be ready to spawn as one of the player's: else a candidate is generated.</param>
        /// <param name="record">False when the caller writes the chronicle line and the message itself.</param>
        /// <param name="applicant">The candidate applied through the applicants' list (see <see cref="ApplicantPool"/>): an ordinary hire in everything, the roll of kin included. A former inmate keeps theirs: the register tells their story.</param>
        /// <param name="fee">What it costs when it is not the post's own fee (the agency's); -1: the post's.</param>
        internal static bool Hire(Map map, StaffRoleDef role, bool free, Pawn candidate, bool record = true, bool applicant = false, int fee = -1)
        {
            PrisonGame game = PrisonGame.Instance;
            int cost = fee >= 0 ? fee : role.hireCost;
            if (!Unlocks.IsUnlocked(role)) return false;
            if (!free && !game.ledger.CanAfford(cost))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(cost)), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            // Somebody who can do the work, found before anything is paid: twenty candidates in a row who could not is no hire and no fee (it used to hire and charge for the twentieth).
            Pawn p = candidate;
            if (p == null && !TryGenerateCandidate(role, out p, keepAdaptation: true))
            {
                ApplicantPool.Release(p);
                Messages.Message("RP_NoCandidate".Translate(role.label), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            if (!free && !game.ledger.TrySpend(RPDefOf.RP_Hiring, cost, role.LabelCap))
            {
                if (candidate == null) ApplicantPool.Release(p);
                Messages.Message("RP_CannotAfford".Translate(Money.Format(cost)), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            IntVec3 spot = IntakeSystem.IntakeSpot(map);
            if (!CellFinder.TryFindRandomCellNear(spot, map, 5, c => c.Standable(map), out IntVec3 cell)) cell = spot;
            GenSpawn.Spawn(p, cell, map);
            // (A candidate made for the hire, or an applicant, joins now: the population watcher hears of it once, here.)
            if (candidate == null || applicant) NotifyGainedColonist(p);
            ApplyRole(p, role, record);
            DressInUniform(p, role);
            if (record) Messages.Message("RP_Hired".Translate(p.LabelShort, role.label), p, MessageTypeDefOf.PositiveEvent);
            // One hire in twelve is kin to an inmate (a former inmate's own story is told by the register, and the facility's own staff came before any inmate).
            if ((candidate == null || applicant) && !free) Kin.OnHired(p, map);
            return true;
        }

        /// <summary>Riot gear (flak vest, helmet) for staff with guard duties once the project is done.</summary>
        public static void IssueGear(Pawn p)
        {
            if (p.GetComp<CompStaff>()?.role is not { guardDuties: true } role || p.apparel == null || !Unlocks.Feature("RiotGear")) return;
            foreach (ThingDef d in new[] { RPVanillaDefOf.Apparel_FlakVest, RPVanillaDefOf.Apparel_SimpleHelmet })
            {
                if (!d.apparel.PawnCanWear(p) || p.apparel.WornApparel.Any(a => a.def == d)) continue;
                // Never take away their own helmet or armour: issue only into a free slot.
                if (p.apparel.WornApparel.Any(w => !ApparelUtility.CanWearTogether(d, w.def, p.RaceProps.body))) continue;
                var a = (Apparel)ThingMaker.MakeThing(d, d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null);
                a.SetColor(role.uniformColor, reportFailure: false);
                p.apparel.Wear(a, dropReplacedApparel: false);
            }
        }

        /// <summary>Hands in every issued weapon of this kind (equipped or carried).</summary>
        public static void ReturnWeapons(Pawn p, ThingDef weapon)
        {
            if (p.equipment?.Primary?.def == weapon) p.equipment.DestroyEquipment(p.equipment.Primary);
            if (p.inventory == null) return;
            foreach (Thing t in p.inventory.innerContainer.ToList())
                if (t.def == weapon) t.Destroy();
        }

        public static void IssueWeapon(Pawn p, StaffRoleDef role)
        {
            if (role.weapon == null || p.equipment == null) return;
            p.equipment.DestroyAllEquipment();
            var gun = (ThingWithComps)ThingMaker.MakeThing(role.weapon, role.weapon.MadeFromStuff ? GenStuff.DefaultStuffFor(role.weapon) : null);
            p.equipment.AddEquipment(gun);
        }

        public static void DressInUniform(Pawn p, StaffRoleDef role)
        {
            if (p.apparel == null) return;
            foreach (Apparel a in p.apparel.WornApparel)
                if (a.def.apparel.layers.Contains(ApparelLayerDefOf.Shell) || a.def.apparel.layers.Contains(ApparelLayerDefOf.OnSkin))
                    a.SetColor(role.uniformColor, reportFailure: false);
        }

        /// <summary>
        /// Let go while at home (off shift): they simply do not come back. No arrest is possible from here; the record
        /// and the message still say so.
        /// </summary>
        private static void FireOffSite(Pawn p, bool record)
        {
            PrisonGame game = PrisonGame.Instance;
            CompStaff sc = p.GetComp<CompStaff>();
            if (game == null || sc?.role == null || !game.home.Contains(p)) return;
            if (record && sc.leaveAtTick < 0) Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_StaffLeft".Translate(p.LabelShort, StaffCareers.Title(sc)), p);
            UnitOfficers.Unassign(p, sc);
            sc.role = null;
            StaffCommute.StaffVersion++;
            sc.trainingPending = false;
            if (sc.dog is { Dead: false } dog && game.home.Contains(dog)) game.home.Remove(dog);
            K9Utility.Notify_HandlerGone(sc);
            game.home.Remove(p);
            if (InmateGenerator.OutsideFaction is { } outside) p.SetFaction(outside);
            Find.WorldPawns.PassToWorld(p, RimWorld.Planet.PawnDiscardDecideMode.Discard);
            if (record) Messages.Message("RP_Fired".Translate(p.LabelShort), MessageTypeDefOf.NeutralEvent);
        }

        /// <summary>Let go: they walk off the map, or (<paramref name="arrest"/>) wait for the police officer.</summary>
        /// <param name="record">False when the caller reports the departure itself (resignation, retirement).</param>
        public static void Fire(Pawn p, bool arrest = false, bool record = true)
        {
            // The state's security chief is not the warden's to let go (the state recalls them itself, see SpecialMeasures).
            if (SpecialMeasures.Posted(p) && !SpecialMeasures.Recalling)
            {
                Messages.Message("RP_StatePostedCannotFire".Translate(p.LabelShort), p, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            // Nor is the warden on the floor: they are the warden. Only a death or the end of the game takes them.
            if (WardenSystem.IsWarden(p))
            {
                Messages.Message("RP_WardenCannotFire".Translate(p.LabelShort), p, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            StaffCareers.InvalidateLeads();
            // A held hostage is not let go while the crisis holds them (the stand-off would end with no letter, no calm and no consequence): the
            // dismissal waits until they are free.
            if (HostageCrisis.IsHeld(p) && p.GetComp<CompStaff>() is { role: not null } held)
            {
                held.pendingFire = 1 | (arrest ? 2 : 0) | (record ? 0 : 4);
                return;
            }
            if (p.Map == null)
            {
                CompStaff offSite = p.GetComp<CompStaff>();
                if (PrisonGame.Instance?.home.Contains(p) == true) FireOffSite(p, record);
                // Carried, or in a pod: neither on a map nor at home. They are let go as soon as they are back on one.
                else if (offSite?.role != null && !p.Dead && !p.Destroyed) offSite.pendingFire = 1 | (arrest ? 2 : 0) | (record ? 0 : 4);
                return;
            }
            CompStaff sc = p.GetComp<CompStaff>();
            if (record && sc.role != null && sc.leaveAtTick < 0)
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, arrest ? 2 : 1, (arrest ? "RP_Chr_StaffArrested" : "RP_Chr_StaffLeft").Translate(p.LabelShort, StaffCareers.Title(sc)), p);
            if (sc.role?.weapon != null) ReturnWeapons(p, sc.role.weapon);
            // Never walk off the map carrying someone (riot police leaving mid-carry would kidnap the inmate).
            if (p.carryTracker?.CarriedThing != null) p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out _);
            // (Let go, resigned or retired: a unit officer's post is cleared, and the slot is free.)
            UnitOfficers.Unassign(p, sc);
            sc.role = null;
            StaffCommute.StaffVersion++;
            StaffBar.MarkDirty();
            sc.trainingPending = false;
            K9Utility.Notify_HandlerGone(sc);
            p.needs?.AddOrRemoveNeedsAsAppropriate();
            Faction outside = InmateGenerator.OutsideFaction;
            if (outside == null) return;
            p.SetFaction(outside);
            if (arrest && Detention.CallPolice(p))
            {
                Messages.Message("RP_FiredArrested".Translate(p.LabelShort), p, MessageTypeDefOf.NeutralEvent);
                return;
            }
            LordMaker.MakeNewLord(outside, new LordJob_ExitMapBest(Verse.AI.LocomotionUrgency.Walk, canDig: false), p.Map, new List<Pawn> { p });
            if (record) Messages.Message("RP_Fired".Translate(p.LabelShort), p, MessageTypeDefOf.NeutralEvent);
        }
    }
}
