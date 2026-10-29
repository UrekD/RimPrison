using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>How staff are checked when they come on shift.</summary>
    public enum StaffSearchPolicy : byte
    {
        None,
        Random,
        All,
    }

    [DefOf]
    public static class RPCorruptionDefOf
    {
        public static MisconductDef RP_Bribery;
        public static ThoughtDef RP_SearchedAtWork;
        public static ThoughtDef RP_ColleagueSacked;
        public static ThoughtDef RP_FalselyAccused;
        public static ThoughtDef RP_Exonerated;

        static RPCorruptionDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCorruptionDefOf));
    }

    /// <summary>
    /// Staff corruption as a story: every member of staff has a hidden integrity (traits, then mood over time). Fixers
    /// inside (dealers, gang leaders, extortionists, inmates with cash) sound out staff who look weak; those who say
    /// yes go on the payroll of that inmate and their gang. A corrupt guard comes in carrying on some shifts and passes
    /// it on, waves through their clients' visitors and goes easy on their searches. Those who say no may report the
    /// approach. Traces (contraband traced back, informants' tips — not always true —, phone taps, a staff search at
    /// the start of a shift) build up suspicion; past a threshold internal affairs opens a case and the warden decides:
    /// let it go, suspend and investigate, fire (with an arrest if they were dirty), or try to turn them into a double
    /// agent who feeds you their clients' stashes until someone finds out.
    /// </summary>
    public static class CorruptionSystem
    {
        public const float ApproachBelow = 65f;
        public const float CaseAt = 50f;
        public const int SuspensionDays = 3;
        public const int BribePrice = 15;

        /// <summary>For tests: how many daily passes have run (the settlement calls it at midnight).</summary>
        public static int DailyPasses;

        public static bool Suspended(Pawn p) => p.GetComp<CompStaff>() is { } sc && Suspended(sc);

        /// <summary>Off duty and at home for any reason: an internal affairs suspension, a disciplinary one (upheld complaints), or a stand-down while a complaint is investigated.</summary>
        public static bool Suspended(CompStaff sc) => IaSuspended(sc) || Disciplined(sc) || StoodDown(sc);

        /// <summary>Suspended by the warden pending an internal affairs investigation (ends in a verdict on corruption: see <see cref="EndSuspension"/>).</summary>
        public static bool IaSuspended(CompStaff sc) => sc.suspendedUntilTick > Find.TickManager.TicksGame;

        /// <summary>Suspended for upheld complaints: nothing to do with internal affairs, and it ends with the officer back on duty and nothing else.</summary>
        public static bool Disciplined(CompStaff sc) => sc.disciplineUntilTick > Find.TickManager.TicksGame;

        /// <summary>Sent home on full pay while a complaint against them is investigated.</summary>
        public static bool StoodDown(CompStaff sc) => sc.standDownUntilTick > Find.TickManager.TicksGame;

        public static bool StoodDownOnly(CompStaff sc) => StoodDown(sc) && !IaSuspended(sc) && !Disciplined(sc);

        /// <summary>When they are back (the latest of the three).</summary>
        public static int OffUntil(CompStaff sc) => Mathf.Max(sc.suspendedUntilTick, Mathf.Max(sc.disciplineUntilTick, sc.standDownUntilTick));

        public static int ShiftStartHour(CompStaff sc) => sc.nightShift ? 18 : 6;

        /// <summary>Is this inmate one of the corrupt staff member's clients (their patron, the patron's gang, or anyone
        /// with cash for a freelancer whose patron is gone)?</summary>
        public static bool IsClient(CompStaff sc, Pawn inmate, CompInmate c)
        {
            if (!sc.corrupt || sc.doubleAgent || c == null) return false;
            if (sc.patron == null && sc.patronGang == null) return false;
            return inmate == sc.patron || (sc.patronGang != null && c.gang == sc.patronGang);
        }

        /// <summary>A corrupt searcher does not find what their clients carry (mostly).</summary>
        public static bool LooksAway(Pawn searcher, Pawn inmate) =>
            searcher?.GetComp<CompStaff>() is { corrupt: true } sc && IsClient(sc, inmate, inmate.Inmate());

        /// <summary>Hourly per member of staff on the map.</summary>
        public static void Hourly(Pawn staff, CompStaff sc)
        {
            if (sc.leaveAtTick >= 0 || sc.Protected) return; // riot police and other temporary staff, the state's own chief and the warden
            if (sc.integrity < 0f) sc.integrity = RollIntegrity(staff);
            if (!TickSuspensions(staff, sc)) return;
            int hour = GenLocalDate.HourOfDay(staff);
            if (Suspended(sc)) return;
            if (hour == ShiftStartHour(sc)) StartShift(staff, sc);
            if (!sc.corrupt) TryApproach(staff, sc);
            else if (sc.doubleAgent) DoubleAgentHour(staff, sc, hour);
            else CorruptHour(staff, sc);
        }

        /// <summary>
        /// A suspension or stand-down that has run out ends: an internal affairs one reports (<see cref="EndSuspension"/>), a disciplinary one only brings them back from suspension (their
        /// suspicion and evidence are left as they are), a stand-down just ends. False when they are no longer here to go on with.
        /// </summary>
        internal static bool TickSuspensions(Pawn staff, CompStaff sc)
        {
            int now = Find.TickManager.TicksGame;
            if (sc.standDownUntilTick >= 0 && now >= sc.standDownUntilTick)
            {
                sc.standDownUntilTick = -1;
                Roster.Invalidate();
            }
            if (sc.disciplineUntilTick >= 0 && now >= sc.disciplineUntilTick)
            {
                sc.disciplineUntilTick = -1;
                Roster.Invalidate();
                Messages.Message("RP_DisciplineBack".Translate(staff.LabelShort), staff, MessageTypeDefOf.NeutralEvent, historical: false);
            }
            if (sc.suspendedUntilTick >= 0 && now >= sc.suspendedUntilTick)
            {
                sc.suspendedUntilTick = -1;
                Roster.Invalidate();
                EndSuspension(staff, sc);
                if (staff.Destroyed || !staff.Spawned || sc.role == null || staff.Faction != Faction.OfPlayer) return false;
            }
            return true;
        }

        internal static float RollIntegrity(Pawn staff)
        {
            float v = Rand.Range(45f, 95f);
            if (staff.story?.traits is { } traits)
            {
                if (traits.HasTrait(TraitDefOf.Greedy)) v -= 30f;
                if (traits.HasTrait(TraitDefOf.Kind)) v += 10f;
            }
            return Mathf.Clamp(v, 5f, 100f);
        }

        /// <summary>
        /// Daily, for everyone employed here, on site or at home (day staff are at home at midnight): integrity drifts, a gone
        /// patron is noticed, and a case that crossed its threshold while the suspect was away (or before this check existed) opens.
        /// </summary>
        public static void Daily(Map map)
        {
            DailyPasses++;
            foreach (Pawn staff in StaffCommute.AllStaff(map))
            {
                CompStaff sc = staff.GetComp<CompStaff>();
                if (sc.leaveAtTick >= 0 || sc.Protected || staff.Faction != Faction.OfPlayer) continue; // riot police and other temporary staff, the state's own chief and the warden
                if (sc.integrity < 0f) sc.integrity = RollIntegrity(staff);
                Drift(staff, sc);
                if (sc.patron != null && PatronGone(sc.patron)) Notify_PatronGone(sc);
                if (sc.suspicion >= CaseAt) AddSuspicion(staff, sc, 0f, null);
            }
        }

        /// <summary>A miserable job wears people down; a good one builds loyalty.</summary>
        internal static void Drift(Pawn staff, CompStaff sc)
        {
            float mood = staff.needs?.mood?.CurLevelPercentage ?? 0.5f;
            if (mood < 0.3f) sc.integrity -= 2f;
            else if (mood > 0.7f) sc.integrity += 0.5f;
            sc.integrity = Mathf.Clamp(sc.integrity, 0f, 100f);
        }

        /// <summary>Coming on shift: a corrupt member of staff may be carrying today; the staff search may catch it.</summary>
        internal static void StartShift(Pawn staff, CompStaff sc)
        {
            PrisonGame game = PrisonGame.Instance;
            sc.carryingToday = sc.corrupt && !sc.doubleAgent && Rand.Chance(0.5f);
            bool searched = game.staffSearch == StaffSearchPolicy.All || (game.staffSearch == StaffSearchPolicy.Random && Rand.Chance(0.3f));
            if (!searched) return;
            game.stats.staffSearches++;
            staff.needs?.mood?.thoughts.memories.TryGainMemory(RPCorruptionDefOf.RP_SearchedAtWork);
            if (!sc.carryingToday || !Rand.Chance(0.75f)) return;
            sc.carryingToday = false;
            game.stats.staffCaught++;
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_StaffCaught".Translate(staff.LabelShort), staff);
            Messages.Message("RP_StaffCaughtCarrying".Translate(staff.LabelShort), staff, MessageTypeDefOf.NegativeEvent);
            AddSuspicion(staff, sc, 100f, "RP_Sus_CaughtCarrying");
        }

        /// <summary>A fixer sounds out a member of staff who looks weak.</summary>
        private static void TryApproach(Pawn staff, CompStaff sc)
        {
            int now = Find.TickManager.TicksGame;
            if (sc.integrity >= ApproachBelow || now - sc.lastApproachTick < GenDate.TicksPerDay * 2 || !staff.Awake() || !Rand.Chance(0.04f)) return;
            Pawn fixer = null;
            foreach (Pawn p in staff.Map.mapPawns.PrisonersOfColonySpawned)
                if (p.Position.InHorDistOf(staff.Position, 8f) && p.Awake() && !p.InMentalState && p.Inmate() is { activeFlag: true } pc && IsFixer(pc))
                {
                    fixer = p;
                    break;
                }
            if (fixer == null) return;
            Approach(staff, sc, fixer, 0f);
        }

        /// <summary>The odds that a member of staff says yes to a fixer (before the clamp to 0..1): weak integrity, a low mood and greed raise them, <paramref name="pressure"/> is added.</summary>
        internal static float ApproachOdds(Pawn staff, CompStaff sc, float pressure)
        {
            float mood = staff.needs?.mood?.CurLevelPercentage ?? 0.5f;
            return (ApproachBelow - sc.integrity) / 50f + (mood < 0.3f ? 0.2f : 0f) + (staff.story?.traits?.HasTrait(TraitDefOf.Greedy) == true ? 0.15f : 0f) + pressure;
        }

        /// <summary>
        /// The odds for this fixer in particular (what <see cref="Approach"/> rolls against): <see cref="ApproachOdds"/> with <paramref name="pressure"/>, and <see cref="Kin.CorruptionLean"/> more when the
        /// fixer, or the gang or smuggling ring they are in, holds the officer's kin (an officer with a brother inside is more open to the people who have him). Kinship does not make anyone a target
        /// of the fixers (that still takes weak integrity, see <see cref="ApproachBelow"/>): it only weighs the answer. A unit officer leans the same way towards a fixer on their wing, more with each day in post
        /// (<see cref="UnitOfficers.Lean"/>: they know him, he knows their families).
        /// </summary>
        internal static float OddsFor(Pawn staff, CompStaff sc, Pawn fixer, float pressure) => ApproachOdds(staff, sc, pressure + Kin.Leverage(staff, fixer) + UnitOfficers.Lean(staff, fixer));

        /// <summary>
        /// A fixer's offer, answered: yes puts them on the fixer's payroll, no may be reported. <paramref name="pressure"/> is added to the odds of yes (an old gang leaning on
        /// somebody who used to be one of theirs, see <see cref="ExInmates"/>). True when they said yes.
        /// </summary>
        internal static bool Approach(Pawn staff, CompStaff sc, Pawn fixer, float pressure)
        {
            int now = Find.TickManager.TicksGame;
            CompInmate fc = fixer.Inmate();
            sc.lastApproachTick = now;
            PrisonGame game = PrisonGame.Instance;
            game.stats.staffApproached++;
            if (Rand.Chance(Mathf.Clamp01(OddsFor(staff, sc, fixer, pressure))))
            {
                sc.corrupt = true;
                sc.patron = fixer;
                sc.patronGang = fc.gang;
                if (OtherStaffNear(staff)) AddSuspicion(staff, sc, 10f, "RP_Sus_SeenTalking");
                return true;
            }
            sc.integrity = Mathf.Min(100f, sc.integrity + 3f);
            if (sc.integrity < 50f || !Rand.Chance(0.6f)) return false;
            game.stats.bribesRefused++;
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_BribeRefused".Translate(staff.LabelShort, fixer.LabelShort), staff, fixer);
            MisconductUtility.Record(fixer, fc, PrisonMap.Of(fixer.Map), RPCorruptionDefOf.RP_Bribery, evidence: true, chronicle: false);
            Messages.Message("RP_BribeReported".Translate(staff.LabelShort, fixer.LabelShort), fixer, MessageTypeDefOf.NeutralEvent);
            return false;
        }

        public static bool IsFixer(CompInmate c) =>
            c.gangLeader || c.cash >= 40 || c.HasReputation(RPReputationDefOf.RP_Extortionist)
            || c.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer));

        private static bool OtherStaffNear(Pawn staff)
        {
            foreach (Pawn o in staff.Map.mapPawns.FreeColonistsSpawned)
                if (o != staff && o.Awake() && o.GetComp<CompStaff>()?.role != null && o.Position.InHorDistOf(staff.Position, 10f)) return true;
            return false;
        }

        /// <summary>On the payroll: on a day they came in carrying, pass it to a client (arranged orders first).</summary>
        private static void CorruptHour(Pawn staff, CompStaff sc)
        {
            if (sc.patron != null && PatronGone(sc.patron)) Notify_PatronGone(sc);
            if (!sc.corrupt) return;
            if (!sc.carryingToday || !staff.Awake() || staff.InBed() || staff.Downed || staff.InMentalState || !Rand.Chance(0.25f)) return;
            // A handover needs the client within reach, awake, and both in the same room.
            Pawn client = null;
            Room room = staff.GetRoom();
            foreach (Pawn p in staff.Map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { activeFlag: true } c || !IsClient(sc, p, c)) continue;
                // Taken out of a smuggling ring lately: nothing comes in for them, by a guard either.
                if (RingSystem.SupplyCut(p)) continue;
                if (!p.Awake() || p.Downed || p.GetRoom() != room || !p.Position.InHorDistOf(staff.Position, 12f)) continue;
                if (client == null || (c.smuggleArranged && client.Inmate().smuggleArranged == false)) client = p;
            }
            if (client == null) return;
            CompInmate cc = client.Inmate();
            bool dealer = cc.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer));
            Thing given = ContrabandUtility.GiveSmuggled(client, dealer ? ContrabandKind.Drug : null, forDebt: true);
            if (given == null) return;
            // (A courier of a ring carries it for the dealer.)
            RingSystem.Notify_Delivered(client, given.def);
            cc.cash = Mathf.Max(0, cc.cash - BribePrice);
            cc.smuggledBy = staff;
            cc.smuggleArranged = false;
            sc.carryingToday = false;
            sc.bribesTaken++;
            PrisonGame.Instance.stats.bribes++;
            PrisonGame.Instance.stats.staffDeliveries++;
        }

        /// <summary>Turned: once a day they point at a client's stash, until the gang works it out.</summary>
        private static void DoubleAgentHour(Pawn staff, CompStaff sc, int hour)
        {
            if (hour != 12) return;
            Pawn mark = null;
            foreach (Pawn p in staff.Map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { activeFlag: true, cellSearchPending: false } c && (p == sc.patron || (sc.patronGang != null && c.gang == sc.patronGang))
                    && (c.stash.Count > 0 || ContrabandUtility.CarriesContraband(p)))
                {
                    mark = p;
                    break;
                }
            if (mark == null) return;
            CompInmate mc = mark.Inmate();
            mc.cellSearchPending = true;
            mc.suspected = true;
            PrisonGame.Instance.stats.doubleAgentTips++;
            Messages.Message("RP_DoubleAgentTip".Translate(staff.LabelShort, mark.LabelShort), mark, MessageTypeDefOf.PositiveEvent, historical: false);
            if (Rand.Chance(0.12f)) Burn(staff, sc, mark);
        }

        /// <summary>Burnt: the gang works out who has been talking. Off the gang's payroll and no longer "known corrupt"
        /// (the deal with the warden is over): if a fixer buys them again, internal affairs can catch them like anyone.</summary>
        public static void Burn(Pawn staff, CompStaff sc, Pawn mark)
        {
            sc.doubleAgent = false;
            sc.corrupt = false;
            sc.exposedCorrupt = false;
            Pawn avenger = mark.Awake() && !mark.InMentalState ? mark : null;
            sc.patron = null;
            sc.patronGang = null;
            avenger?.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Assaulting, null, forced: true, otherPawn: staff);
            Find.LetterStack.ReceiveLetter("RP_DoubleAgentBurntLabel".Translate(), "RP_DoubleAgentBurntText".Translate(staff.LabelShort, mark.LabelShort),
                LetterDefOf.NegativeEvent, staff);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_DoubleAgentBurnt".Translate(staff.LabelShort, mark.LabelShort), staff, mark);
        }

        /// <summary>
        /// The patron is no longer one of ours: dead, released, or gone for good. A transfer that leaves by the shuttle (or straight off the floor) is discarded without vanilla's exit, which is what
        /// clears the guest status: such a pawn is still a prisoner of the colony and not dead, so it is told by being destroyed or discarded.
        /// </summary>
        public static bool PatronGone(Pawn patron) => patron.Dead || patron.Destroyed || patron.Discarded || !patron.IsPrisonerOfColony;

        /// <summary>
        /// Their patron left: they keep working for the gang if there is one; otherwise they are off the payroll (and,
        /// with integrity still low, open to the next fixer).
        /// </summary>
        public static void Notify_PatronGone(CompStaff sc)
        {
            sc.patron = null;
            if (sc.patronGang != null) return;
            if (sc.doubleAgent)
            {
                // Nobody left to report on: the deal with the warden is over (as after a burn, without the gang's revenge).
                sc.doubleAgent = false;
                sc.exposedCorrupt = false;
            }
            sc.corrupt = false;
            sc.carryingToday = false;
        }

        /// <summary>Contraband found on an inmate: maybe it is traced back to who brought it in.</summary>
        public static void Notify_ContrabandFound(Pawn inmate, CompInmate c, string how = "find")
        {
            Pawn staff = c.smuggledBy;
            c.smuggledBy = null;
            bool traced = staff != null && !staff.Destroyed && staff.GetComp<CompStaff>() is { doubleAgent: false, exposedCorrupt: false } && Rand.Chance(0.6f);
            // (A ring the find belongs to heats up, and gives up the link it came through.)
            RingSystem.Notify_Found(inmate, c, staff, traced, how);
            if (traced) AddSuspicion(staff, staff.GetComp<CompStaff>(), 40f, "RP_Sus_Traced");
        }

        /// <summary>A phone tap overheard an inmate arranging a delivery: if a guard is their supplier, maybe a name.</summary>
        public static void Notify_TappedArrangement(Pawn inmate, CompInmate c)
        {
            // (A ring's member on the phone gives up a link of it: the officer who brings it in, else the supplier.)
            RingSystem.Notify_Tapped(inmate);
            foreach (Pawn s in inmate.Map.mapPawns.FreeColonistsSpawned)
                if (s.GetComp<CompStaff>() is { } sc && IsClient(sc, inmate, c) && Rand.Chance(0.5f))
                {
                    AddSuspicion(s, sc, 30f, "RP_Sus_Tapped");
                    return;
                }
        }

        public static void AddSuspicion(Pawn staff, CompStaff sc, float amount, string reasonKey)
        {
            // The warden already knows (turned, or exposed): nothing for internal affairs to find.
            if (sc.doubleAgent || sc.exposedCorrupt || sc.role == null) return;
            sc.suspicion = Mathf.Min(100f, sc.suspicion + amount);
            if (reasonKey != null && !sc.evidence.Contains(reasonKey)) sc.evidence.Add(reasonKey);
            // A suspect at home gets their case too: suspending needs no one on site (it is a date), and while suspended they stay home.
            // (Not while they are suspended: the investigation under way is the case.)
            if (sc.suspicion >= CaseAt && !sc.iaOpen && !IaSuspended(sc) && !staff.Destroyed && !staff.Dead && staff.Faction == Faction.OfPlayer) OpenCase(staff, sc);
        }

        private static void OpenCase(Pawn staff, CompStaff sc)
        {
            sc.iaOpen = true;
            PrisonGame.Instance.stats.iaCases++;
            var lines = new List<string>();
            foreach (string key in sc.evidence) lines.Add("  - " + key.Translate(staff.LabelShort));
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_InternalAffairs", "RP_IALabel".Translate(staff.LabelShort),
                "RP_IAText".Translate(staff.LabelShort, sc.role == null ? "" : StaffCareers.Title(sc), string.Join("\n", lines), sc.suspicion.ToString("F0")), staff.Map ?? Find.AnyPlayerHomeMap, staff);
            letter.pawns.Add(staff);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_IACase".Translate(staff.LabelShort), staff);
            letter.StartTimeout(GenDate.TicksPerDay * 2);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>Suspension over: the investigation reports.</summary>
        private static void EndSuspension(Pawn staff, CompStaff sc)
        {
            if (sc.corrupt && !sc.doubleAgent)
            {
                Find.LetterStack.ReceiveLetter("RP_IAGuiltyLabel".Translate(), "RP_IAGuiltyText".Translate(staff.LabelShort, sc.bribesTaken), LetterDefOf.PositiveEvent, staff);
                Dismiss(staff, sc, arrest: true);
                return;
            }
            sc.suspicion = 0f;
            sc.evidence.Clear();
            staff.needs?.mood?.thoughts.memories.TryGainMemory(RPCorruptionDefOf.RP_Exonerated);
            Messages.Message("RP_IACleared".Translate(staff.LabelShort), staff, MessageTypeDefOf.PositiveEvent);
        }

        public static void Dismiss(Pawn staff, CompStaff sc, bool arrest)
        {
            PrisonGame game = PrisonGame.Instance;
            game.stats.staffDismissed++;
            if (arrest) sc.exposedCorrupt = true;
            StaffUtility.Fire(staff, arrest);
        }

        /// <summary>Turns a corrupt member of staff into a double agent: still on the payroll, reporting on their clients.</summary>
        public static void Turn(Pawn staff, CompStaff sc, Map map)
        {
            sc.suspicion = 0f;
            sc.evidence.Clear();
            sc.doubleAgent = true;
            sc.exposedCorrupt = true;
            // What they brought in before is no longer a trace back to them.
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } pc && pc.smuggledBy == staff) pc.smuggledBy = null;
            Messages.Message("RP_IATurned".Translate(staff.LabelShort), staff, MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>
        /// Whether this guard can be sent home (a stand-down, a disciplinary suspension): another guard who is still on duty must work every hour they do, so the only night guard is not sent
        /// home to leave the prison with nobody to answer a riot. Not a guard, or already away: yes.
        /// </summary>
        public static bool CanSpare(Pawn officer, Map map)
        {
            if (officer == null || officer.timetable == null || !GuardUtility.IsGuard(officer)) return true;
            List<Pawn> others = StaffCommute.AllStaff(map).Where(o => o != officer && GuardUtility.IsGuard(o)).ToList();
            for (int hour = 0; hour < 24; hour++)
                if (officer.timetable.GetAssignment(hour) != TimeAssignmentDefOf.Sleep && !others.Exists(o => o.timetable != null && o.timetable.GetAssignment(hour) != TimeAssignmentDefOf.Sleep)) return false;
            return true;
        }

        /// <summary>An internal affairs suspension (or the inquiry's): the investigation reports when it ends.</summary>
        public static void Suspend(Pawn staff, CompStaff sc)
        {
            if (sc.role is { warden: true }) return; // (the warden is never sent home, whoever asks)
            sc.suspendedUntilTick = Find.TickManager.TicksGame + SuspensionDays * GenDate.TicksPerDay;
            SendHome(staff, sc);
        }

        /// <summary>The disciplinary suspension for upheld complaints: days at home, and nothing of internal affairs in it.</summary>
        public static void Discipline(Pawn staff, CompStaff sc)
        {
            if (sc.role is { warden: true }) return;
            sc.disciplineUntilTick = Mathf.Max(sc.disciplineUntilTick, Find.TickManager.TicksGame + SuspensionDays * GenDate.TicksPerDay);
            SendHome(staff, sc);
        }

        /// <summary>Home on full pay (wages are paid as ever) until <paramref name="until"/>: the officer a complaint is being investigated against.</summary>
        public static void StandDown(Pawn staff, CompStaff sc, int until)
        {
            if (sc.role is { warden: true }) return;
            sc.standDownUntilTick = Mathf.Max(sc.standDownUntilTick, until);
            SendHome(staff, sc);
        }

        private static void SendHome(Pawn staff, CompStaff sc)
        {
            sc.carryingToday = false;
            StaffCareers.InvalidateLeads();
            Roster.Invalidate(); // (the roster's strip: they are in no hour now)
            if (staff.Spawned && staff.CurJob is { } job && job.def.casualInterruptible) staff.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }
    }

    /// <summary>Internal affairs: let it go / suspend and investigate / fire / try to turn them.</summary>
    public class PrisonDecisionWorker_InternalAffairs : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_IA_Ignore", "RP_IA_Suspend", "RP_IA_Fire", "RP_IA_Turn" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn staff = l.pawns.Count > 0 ? l.pawns[0] : null;
            if (staff?.GetComp<CompStaff>() is { } sc0) sc0.iaOpen = false;
            if (staff == null || staff.Destroyed || staff.Dead || staff.GetComp<CompStaff>() is not { role: not null } sc) return;
            switch (option)
            {
                case 1:
                    CorruptionSystem.Suspend(staff, sc);
                    Messages.Message("RP_IASuspended".Translate(staff.LabelShort, CorruptionSystem.SuspensionDays), staff, MessageTypeDefOf.NeutralEvent);
                    break;
                case 2:
                    if (sc.corrupt) Messages.Message("RP_IAFiredGuilty".Translate(staff.LabelShort), staff, MessageTypeDefOf.PositiveEvent);
                    else
                    {
                        Messages.Message("RP_IAFiredInnocent".Translate(staff.LabelShort), staff, MessageTypeDefOf.NegativeEvent);
                        foreach (Pawn o in map.mapPawns.FreeColonistsSpawned)
                            if (o != staff && o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(RPCorruptionDefOf.RP_ColleagueSacked);
                    }
                    CorruptionSystem.Dismiss(staff, sc, arrest: sc.corrupt);
                    break;
                case 3:
                    if (sc.corrupt) CorruptionSystem.Turn(staff, sc, map);
                    else
                    {
                        sc.suspicion = 0f;
                        sc.evidence.Clear();
                        // Clean, and now insulted.
                        sc.integrity = Mathf.Max(0f, sc.integrity - 10f);
                        staff.needs?.mood?.thoughts.memories.TryGainMemory(RPCorruptionDefOf.RP_FalselyAccused);
                        Messages.Message("RP_IATurnInnocent".Translate(staff.LabelShort), staff, MessageTypeDefOf.NegativeEvent);
                    }
                    break;
                default:
                    sc.suspicion *= 0.5f;
                    break;
            }
        }
    }
}
