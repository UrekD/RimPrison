using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPCareDefOf
    {
        public static WorkGiverDef RP_RescueInmate;
        public static WorkGiverDef RP_DeliverCellMeal;
        public static WorkGiverDef RP_FeedInjuredInmate;
        public static WorkGiverDef FeedPrisoner;

        static RPCareDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCareDefOf));
    }

    /// <summary>What an inmate is waiting for from the staff, in the order a reserve guard takes them (a downed inmate first, a walk back to the cell last).</summary>
    public enum CareKind : byte
    {
        /// <summary>Downed or bleeding outside a bed: carried to one.</summary>
        Rescue,
        /// <summary>Shut in (a cell-meal level, the hole, a lockdown, no way to a counter) and hungry: a meal brought.</summary>
        Meal,
        /// <summary>Hungry in bed and fed by hand.</summary>
        Feed,
        /// <summary>A supermax inmate out of the cell with nobody: walked back.</summary>
        Return,
        /// <summary>On watch (see <see cref="WatchList"/>) and due the half-hourly check: a guard looks in.</summary>
        Watch,
    }

    /// <summary>One inmate waiting for one kind of care. Not saved: the queue is built again from the inmates after a load.</summary>
    public sealed class CareRequest
    {
        public Pawn inmate;
        public CareKind kind;
        /// <summary>The tick the request was first seen (after a load, the first look at it: a wait is counted from there).</summary>
        public int firstTick;
        /// <summary>A member of staff is on the job for them now.</summary>
        public bool served;
        internal int seenTick;
        internal bool counted;
    }

    /// <summary>
    /// The care reserve of a map (Deployment tab): 0-3 guards on duty who are kept for the care jobs that already exist (a cell meal, feeding by hand, carrying the downed to a bed, walking a supermax inmate back to the cell) while the
    /// rest answer the trouble. The reserve is picked each hour from the guards on duty on site (preferring those already on care work), or named by the warden (a pin); a reserve guard takes no restraint, punishment carry, search, armed
    /// response or alarm unless an inmate acting out is within <see cref="NearTroubleRadius"/> of them or of the inmate they are tending, or the trouble is an escape or aimed at staff, wherever it is (<see cref="Holds"/>), and is given a care job
    /// first (<see cref="CareJobFor"/>). The automatic pick never takes the last guard on duty (one stays outside the reserve; a guard the warden names may be the last). It gains no skill: the doctors still do the medicine. With a size of 0 (the default, and every old save) nothing here runs and nothing is different.
    /// Saved: the size and the pins (by id). The members, the queue and the waits are built again after a load.
    /// </summary>
    public sealed class CareDetail : IExposable
    {
        public const int MaxReserve = 3;

        /// <summary>The kinds of care there are (<see cref="CareKind"/>): the keys of the requests and the counts are made with it.</summary>
        public const int KindCount = 5;

        /// <summary>Trouble this close to a reserve guard, or to the inmate they are tending, is not held off: it is in their face (cells).</summary>
        public const float NearTroubleRadius = 8f;

        /// <summary>The queue is worked out once in this many ticks (the map's beat), and when it is asked for and older.</summary>
        public const int QueueTicks = 250;

        /// <summary>A request the giver offered a guard no job for is not asked about again for this long (ticks).</summary>
        public const int RetryTicks = 120;

        /// <summary>The most requests one look for a care job (<see cref="CareJobFor"/>) asks the work givers about (each is a food, bed and path search), and the most of one kind of food errand that fail in one look before the rest of the kind wait (<see cref="RetryTicks"/>).</summary>
        public const int MaxTries = 4, MaxKindFailures = 3;
        private readonly Dictionary<long, int> retryAt = new Dictionary<long, int>();

        // ---- saved ----------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The number of guards kept for care (0: none, the automatic allocation as it always was).</summary>
        public int size;

        /// <summary>The guards the warden named, by thing id, in the order named (never more than <see cref="size"/>).</summary>
        public List<int> pins = new List<int>();

        // ---- rebuilt after a load -------------------------------------------------------------------------------------------------------------------------------------

        private readonly HashSet<Pawn> members = new HashSet<Pawn>();
        private readonly List<Pawn> memberList = new List<Pawn>();
        private readonly List<(Pawn pawn, string why)> unavailable = new List<(Pawn, string)>();
        private int pickedSlot = -1, releasedSlot = -1, rosterSeen = -1, staffSeen = -1;
        private bool dirty = true;

        /// <summary>The guards on duty who could answer the trouble, as of the last pick (the reserve's members among them), and whether the pick left a place empty to keep one outside the reserve.</summary>
        public int OnDuty { get; private set; }
        public bool Floored { get; private set; }

        private readonly Dictionary<long, CareRequest> requests = new Dictionary<long, CareRequest>();
        private readonly List<CareRequest> queue = new List<CareRequest>();
        private readonly List<CareRequest> scratch = new List<CareRequest>();
        private readonly HashSet<long> tended = new HashSet<long>();
        private int queueTick = -QueueTicks * 2;
        private Map queueMap;
        private int waiting;

        /// <summary>The longest a care request has waited for a member of staff to take it (or has waited so far), in ticks; and the longest of those that waited while an inmate was acting out or owed a restraint. For the showcase's log line; not saved.</summary>
        public int longestWait, longestWaitInTrouble;

        /// <summary>For the checks: how many times the queue was worked out.</summary>
        public int builds;

        /// <summary>For the checks: the reserve's hooks off altogether (nobody is in the reserve, whatever is set).</summary>
        internal static bool Disabled;

        /// <summary>For the checks: the automatic pick (and what counts as a guard on duty) is limited to these guards (the real staff are neither picked nor counted), null for the game.</summary>
        internal static HashSet<Pawn> OnlyThese;

        public IReadOnlyList<Pawn> Members => memberList;

        /// <summary>The guards named for the reserve who cannot serve now, and why (a translation key).</summary>
        public IReadOnlyList<(Pawn pawn, string why)> Unavailable => unavailable;

        /// <summary>Every care request, the oldest of each kind first (as of the last look, see <see cref="QueueFor"/>).</summary>
        public IReadOnlyList<CareRequest> Queue => queue;

        public int WaitingCount => waiting;

        public void ExposeData()
        {
            Scribe_Values.Look(ref size, "size", 0);
            Scribe_Collections.Look(ref pins, "pins", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                pins ??= new List<int>();
                size = UnityEngine.Mathf.Clamp(size, 0, MaxReserve);
                if (pins.Count > size) pins.RemoveRange(size, pins.Count - size);
                dirty = true;
            }
        }

        public static CareDetail Of(Map map) => PrisonMap.Of(map)?.care;

        // ---- who may be the reserve ----------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Why this guard cannot serve in the reserve now (a translation key), or null when they can: on the roster, employed, not suspended or on leave, not held hostage, not on strike, on site and on shift, on their feet.</summary>
        public static string Why(Pawn p)
        {
            if (p == null || p.Dead || p.Destroyed || p.GetComp<CompStaff>() is not { role: { guardDuties: true, warden: false } } sc) return "RP_CareWhy_NotGuard";
            if (CorruptionSystem.Suspended(sc)) return "RP_CareWhy_Suspended";
            if (!StaffCareers.Available(sc)) return "RP_CareWhy_Leave";
            if (HostageCrisis.IsHeld(p)) return "RP_CareWhy_Hostage";
            // (A striking guard is no reserve: there is no silent exemption from a strike. They take no work but the fire and the injured, see Patch_JobGiver_Work_Strike.)
            if (IncidentWorker_StaffStrike.Striking(p)) return "RP_CareWhy_Strike";
            if (!p.Spawned || StaffCommute.AtHome(p) || p.timetable != null && p.timetable.CurrentAssignment != TimeAssignmentDefOf.Work) return "RP_CareWhy_OffShift";
            if (p.Downed) return "RP_CareWhy_Down";
            return null;
        }

        /// <summary>
        /// Why this guard cannot be taken into the reserve now (a translation key), or null: <see cref="Why"/>, and standing on a tower or a post (up to an hour held there, with no care done), drafted, in a mental state, or with no care work to do (the warden's work off).
        /// The pick asks it of the guards the warden named as well as the others: a named guard who is none of these is listed as unavailable until they are.
        /// </summary>
        public static string Unfit(Pawn p) =>
            Why(p) ?? (OnTowerOrPost(p) ? "RP_CareWhy_Post" : p.Drafted ? "RP_CareWhy_Drafted" : p.InMentalState ? "RP_CareWhy_Mental" : !CanDoCare(p) ? "RP_CareWhy_NoCare" : null);

        /// <summary>On a tower or a post: they stand where they are (a pick leaves them out, and the reserve is not sent to either).</summary>
        public static bool OnTowerOrPost(Pawn p) => p.CurJobDef == RPArmedDefOf.RP_ManTower || p.CurJobDef == RPPostDefOf.RP_ManPost;

        /// <summary>This guard is in the reserve right now: named in the set, and still able to serve (a shift ending, a suspension or a strike takes them out of it at once, the set is only rebuilt at the map's beat).</summary>
        public static bool IsReserve(Pawn p)
        {
            if (Disabled || p?.Map == null || PrisonMap.Of(p.Map)?.care is not { size: > 0 } care || care.members.Count == 0) return false;
            return care.members.Contains(p) && Why(p) == null;
        }

        /// <summary>The jobs that are care: a meal carried, a feeding, a downed inmate carried to a bed, an inmate walked back to the cell, a look in on an inmate on watch.</summary>
        public static bool IsCareJob(Job job) =>
            job != null && (job.def == JobDefOf.DeliverFood || job.def == JobDefOf.FeedPatient || job.def == JobDefOf.TakeWoundedPrisonerToBed || job.def == JobDefOf.EscortPrisonerToBed || SupermaxSystem.IsFetch(job)
                            || job.def == RPWatchDefOf.RP_WatchCheck);

        /// <summary>The inmate a care job is for (null: the guard is on no care job).</summary>
        public static Pawn PatientOf(Pawn guard)
        {
            Job job = guard.CurJob;
            if (!IsCareJob(job)) return null;
            foreach (TargetIndex index in new[] { TargetIndex.A, TargetIndex.B })
                if (job.GetTarget(index).Thing is Pawn { IsPrisonerOfColony: true } patient && patient != guard) return patient;
            return null;
        }

        /// <summary>
        /// The trouble is aimed at the staff: an inmate attacking a member of staff (an assault, a rioter going for one) or holding one hostage (or one of the hostage-takers). The danger is to the staff wherever it is, so it is immediate danger for the reserve.
        /// </summary>
        public static bool AimedAtStaff(Pawn trouble)
        {
            if (trouble.MentalState is MentalState_Assaulting { Target: { } target } && target.GetComp<CompStaff>() != null) return true;
            if (HostageCrisis.Holding(trouble)) return true;
            return trouble.CurJob is { def: var def } job && def == JobDefOf.AttackMelee && job.targetA.Thing is Pawn victim && victim.GetComp<CompStaff>() != null;
        }

        /// <summary>
        /// A reserve guard is kept from this trouble (a restraint, a punishment carry, an armed response, an alarm). Not held: a guard who is not in the reserve; an escape, wherever it is (an emptied prison is no staffing trade-off); trouble aimed at
        /// staff, wherever it is; an inmate acting out within <see cref="NearTroubleRadius"/> of the guard or of the inmate they are tending (immediate danger). An inmate who is calm and owes a restraint or a carry is held, however near.
        /// </summary>
        public static bool Holds(Pawn guard, Pawn trouble)
        {
            if (!IsReserve(guard)) return false;
            if (!trouble.Spawned) return true;
            if (trouble.MentalState is MentalState_Escaping || AimedAtStaff(trouble)) return false;
            if (!GuardUtility.ActingOut(trouble)) return true;
            if (guard.Position.InHorDistOf(trouble.Position, NearTroubleRadius)) return false;
            return !(PatientOf(guard) is { Spawned: true } patient && patient.Position.InHorDistOf(trouble.Position, NearTroubleRadius));
        }

        // ---- the policy ----------------------------------------------------------------------------------------------------------------------------------------------

        public void SetSize(int n)
        {
            n = UnityEngine.Mathf.Clamp(n, 0, MaxReserve);
            if (n == size) return;
            size = n;
            if (pins.Count > size) pins.RemoveRange(size, pins.Count - size);
            // (A smaller reserve is smaller at once: the guards past the new size are no reserve before the next beat.)
            while (memberList.Count > size)
            {
                members.Remove(memberList[memberList.Count - 1]);
                memberList.RemoveAt(memberList.Count - 1);
            }
            dirty = true;
            if (size == 0) Forget();
        }

        public bool IsPinned(Pawn p) => p != null && pins.Contains(p.thingIDNumber);

        /// <summary>Names the guard for the reserve (or takes the name off); false when all of the reserve's places are named already or the reserve is 0.</summary>
        public bool TogglePin(Pawn p)
        {
            if (p == null) return false;
            if (!pins.Remove(p.thingIDNumber))
            {
                if (pins.Count >= size) return false;
                pins.Add(p.thingIDNumber);
            }
            dirty = true;
            return true;
        }

        /// <summary>Whether the reserve was let go for the rest of this hour.</summary>
        public bool Released(Map map) => releasedSlot == SupermaxSystem.SlotOf(map);

        /// <summary>Empties the reserve for the rest of the hour (the next hour's pick fills it again): its guards answer the trouble like the others. Not saved. Says which care requests now wait.</summary>
        public void Release(Map map)
        {
            if (size <= 0 || Released(map)) return;
            releasedSlot = SupermaxSystem.SlotOf(map);
            members.Clear();
            memberList.Clear();
            QueueFor(map);
            Messages.Message(ReleaseSummary(), MessageTypeDefOf.CautionInput, historical: false);
        }

        /// <summary>The reserve is called back before the hour is out (filled at once, not at the map's next beat).</summary>
        public void Resume(Map map)
        {
            releasedSlot = -1;
            dirty = true;
            Tick(map);
        }

        /// <summary>What the reserve's release leaves waiting: the requests nobody has taken, by kind, with the oldest.</summary>
        public string ReleaseSummary()
        {
            if (waiting == 0) return "RP_CareReleasedNone".Translate();
            var counts = new int[KindCount];
            foreach (CareRequest q in queue)
                if (!q.served) counts[(int)q.kind]++;
            var sb = new StringBuilder();
            for (int k = 0; k < counts.Length; k++)
            {
                if (counts[k] == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(("RP_CareKindCount_" + (CareKind)k).Translate(counts[k]));
            }
            return "RP_CareReleased".Translate(waiting, sb.ToString());
        }

        // ---- the pick --------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>Drops everything the reserve built (the size is 0: nothing is kept back).</summary>
        private void Forget()
        {
            members.Clear();
            memberList.Clear();
            unavailable.Clear();
            requests.Clear();
            retryAt.Clear();
            queue.Clear();
            waiting = 0;
            releasedSlot = -1;
            pickedSlot = -1;
        }

        /// <summary>The map's beat (every 250 ticks): the reserve is picked again when the hour changed, the policy did, a member can no longer serve, a place is empty or the roster changed; the queue is worked out.</summary>
        public void Tick(Map map)
        {
            if (size <= 0)
            {
                if (members.Count > 0) Forget();
                return;
            }
            int slot = SupermaxSystem.SlotOf(map);
            bool fresh = slot != pickedSlot || dirty;
            bool released = releasedSlot == slot;
            bool changed = Roster.Version != rosterSeen || StaffCommute.StaffVersion != staffSeen;
            bool vacancy = !released && members.Count < size;
            bool stale = false;
            foreach (Pawn m in members)
                if (Unfit(m) != null) stale = true;
            // (A guard the warden named who was not fit to take (on a post, say) and is now: back in the reserve at this beat, not at the next hour's pick.)
            foreach ((Pawn p, string _) in unavailable)
                if (Unfit(p) == null) stale = true;
            if (!released && (fresh || changed || vacancy || stale)) Pick(map, slot, fresh);
            else if (released && fresh) Pick(map, slot, true);
            QueueFor(map);
        }

        /// <summary>
        /// The reserve picked: the guards the warden named who can serve (a named guard who cannot, for any reason <see cref="Unfit"/> gives, is listed as unavailable, with the reason, and the pin stays for when they can; one who is dead, let go or no
        /// longer a guard loses it), then the others on duty on site and not on a tower or post: on the hour (<paramref name="fresh"/>) those already on care work first, else the ones who are in it already; then the idle, then the rest. By id within
        /// each. The others are never the last guard on duty who could answer the trouble (one stays outside the reserve, <see cref="Floored"/>); a named guard is the warden's own choice and may be.
        /// </summary>
        private void Pick(Map map, int slot, bool fresh)
        {
            var previous = new HashSet<Pawn>(members);
            members.Clear();
            memberList.Clear();
            unavailable.Clear();
            (pickedSlot, rosterSeen, staffSeen, dirty) = (slot, Roster.Version, StaffCommute.StaffVersion, false);
            if (releasedSlot == slot) return;
            List<Pawn> staff = StaffCommute.AllStaff(map);
            for (int i = 0; i < pins.Count; i++)
            {
                int id = pins[i];
                Pawn p = null;
                for (int s = 0; s < staff.Count && p == null; s++)
                    if (staff[s].thingIDNumber == id) p = staff[s];
                if (p == null || p.Dead || p.Destroyed || p.GetComp<CompStaff>()?.role is not { guardDuties: true, warden: false })
                {
                    pins.RemoveAt(i--);
                    continue;
                }
                string why = Unfit(p);
                if (why != null) unavailable.Add((p, why));
                else if (members.Count < size) Add(p);
            }
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            OnDuty = 0;
            Floored = false;
            for (int i = 0; i < colonists.Count; i++)
                if ((OnlyThese == null || OnlyThese.Contains(colonists[i])) && Why(colonists[i]) == null && !colonists[i].Drafted && !colonists[i].InMentalState) OnDuty++;
            if (members.Count >= size) return;
            var candidates = new List<(Pawn pawn, int bucket)>();
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn p = colonists[i];
                if (OnlyThese != null && !OnlyThese.Contains(p) || members.Contains(p) || Unfit(p) != null) continue;
                bool onCare = IsCareJob(p.CurJob), was = previous.Contains(p);
                int bucket = fresh ? (onCare ? 0 : was ? 1 : 2) : (was ? 0 : onCare ? 1 : 2);
                if (bucket == 2 && !Idle(p)) bucket = 3;
                candidates.Add((p, bucket));
            }
            candidates.Sort((a, b) => a.bucket != b.bucket ? a.bucket.CompareTo(b.bucket) : a.pawn.thingIDNumber.CompareTo(b.pawn.thingIDNumber));
            foreach ((Pawn pawn, int _) in candidates)
            {
                if (members.Count >= size) break;
                // (The last guard on duty who could answer the trouble stays out: an empty prison is not a staffing trade-off. Only the warden's own naming gets past it.)
                if (members.Count >= OnDuty - 1)
                {
                    Floored = true;
                    break;
                }
                Add(pawn);
            }
        }

        private void Add(Pawn p)
        {
            members.Add(p);
            memberList.Add(p);
        }

        private static bool Idle(Pawn p)
        {
            JobDef d = p.CurJobDef;
            return d == null || d == RPSecurityDefOf.RP_Patrol || d == JobDefOf.Wait_Wander || d == JobDefOf.GotoWander || d == JobDefOf.Wait || d == JobDefOf.Wait_MaintainPosture;
        }

        // ---- the queue -------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The care requests, one pass over the inmates (never a search for a path): each inmate who is waiting for a meal, a feeding, a carry to a bed or a walk back, by the guard-independent tests of the work givers that do them. Worked out once
        /// in <see cref="QueueTicks"/>; the first tick each request was seen is kept while it stays.
        /// </summary>
        public IReadOnlyList<CareRequest> QueueFor(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (queueMap == map && now - queueTick < QueueTicks && now >= queueTick) return queue;
            (queueMap, queueTick) = (map, now);
            builds++;
            tended.Clear();
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                if (colonists[i].CurJob is { } job && KindOf(job) is { } kind && PatientOf(colonists[i]) is { } patient) tended.Add(Key(patient, kind));
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
            {
                Pawn p = prisoners[i];
                if (p.Inmate() is not { activeFlag: true }) continue;
                if (WorkGiver_RescueInmate.CareCandidate(p)) Note(p, CareKind.Rescue, now);
                // (A meal or a feeding nobody can bring - a berserk inmate, a cell that is not secure, a forbidden one - is no wait the staff could end: the work giver's own gates, see Takeable.)
                if (WorkGiver_DeliverCellMeal.MealCandidate(p) && Takeable(p)) Note(p, CareKind.Meal, now);
                if (WorkGiver_FeedInjuredInmate.Candidate(p) && Takeable(p)) Note(p, CareKind.Feed, now);
                if (SupermaxSystem.ReturnDue(p)) Note(p, CareKind.Return, now);
                if (WatchList.CheckDue(p)) Note(p, CareKind.Watch, now);
            }
            queue.Clear();
            waiting = 0;
            bool trouble = false, troubleKnown = false;
            foreach (KeyValuePair<long, CareRequest> kv in requests.ToList())
            {
                CareRequest q = kv.Value;
                if (q.seenTick != now) { requests.Remove(kv.Key); continue; }
                queue.Add(q);
                int wait = now - q.firstTick;
                if (!q.served)
                {
                    waiting++;
                    if (!troubleKnown) (trouble, troubleKnown) = (InmateCandidates.Shared.Subdue(map).Count > 0, true);
                    Longest(wait, trouble);
                }
                else if (!q.counted)
                {
                    q.counted = true;
                    if (!troubleKnown) (trouble, troubleKnown) = (InmateCandidates.Shared.Subdue(map).Count > 0, true);
                    Longest(wait, trouble);
                }
            }
            queue.Sort((a, b) => a.kind != b.kind ? a.kind.CompareTo(b.kind) : a.firstTick != b.firstTick ? a.firstTick.CompareTo(b.firstTick) : a.inmate.thingIDNumber.CompareTo(b.inmate.thingIDNumber));
            return queue;
        }

        private void Longest(int wait, bool trouble)
        {
            if (wait > longestWait) longestWait = wait;
            if (trouble && wait > longestWaitInTrouble) longestWaitInTrouble = wait;
        }

        private static long Key(Pawn inmate, CareKind kind) => (long)inmate.thingIDNumber * KindCount + (int)kind;

        /// <summary>The kind of care a job is, or null (not a care job). A meal carried is served to a meal request only, not to the same inmate's feeding or carry.</summary>
        private static CareKind? KindOf(Job job)
        {
            if (job == null) return null;
            if (job.def == JobDefOf.DeliverFood) return CareKind.Meal;
            if (job.def == JobDefOf.FeedPatient) return CareKind.Feed;
            if (job.def == JobDefOf.TakeWoundedPrisonerToBed || job.def == JobDefOf.EscortPrisonerToBed) return CareKind.Rescue;
            if (job.def == RPWatchDefOf.RP_WatchCheck) return CareKind.Watch;
            return SupermaxSystem.IsFetch(job) ? CareKind.Return : null;
        }

        /// <summary>What the work givers of a meal and of feeding by hand test before anything else (<c>WorkGiver_Warden.ShouldTakeCareOfPrisoner</c>): not in an aggressive mental state, secure, not forbidden.</summary>
        private static bool Takeable(Pawn inmate) => !inmate.InAggroMentalState && inmate.guest != null && inmate.guest.PrisonerIsSecure && !inmate.IsForbidden(Faction.OfPlayer);

        private void Note(Pawn inmate, CareKind kind, int now)
        {
            long key = Key(inmate, kind);
            if (!requests.TryGetValue(key, out CareRequest q)) requests[key] = q = new CareRequest { inmate = inmate, kind = kind, firstTick = now };
            q.seenTick = now;
            q.served = tended.Contains(key);
        }

        /// <summary>The request that has waited longest of those nobody has taken (null: none), and how long.</summary>
        public CareRequest OldestWaiting(out int ticks)
        {
            CareRequest oldest = null;
            foreach (CareRequest q in queue)
                if (!q.served && (oldest == null || q.firstTick < oldest.firstTick)) oldest = q;
            ticks = oldest == null ? 0 : Find.TickManager.TicksGame - oldest.firstTick;
            return oldest;
        }

        // ---- the care first ----------------------------------------------------------------------------------------------------------------------------------------

        private static WorkGiverDef GiverOf(CareKind kind) =>
            kind switch
            {
                CareKind.Rescue => RPCareDefOf.RP_RescueInmate, CareKind.Meal => RPCareDefOf.RP_DeliverCellMeal, CareKind.Feed => RPCareDefOf.RP_FeedInjuredInmate, CareKind.Watch => RPWatchDefOf.RP_WatchCheckGiver,
                _ => RPSupermaxDefOf.RP_EscortInmate_WorkGiver,
            };

        /// <summary>The care job this reserve guard is given first, from the queue (the downed first, then meals, feeding, the walks back, the watch's checks; the nearest within a kind): the existing work givers' own jobs, none when no request is one this guard can take now.</summary>
        public static Job CareJobFor(Pawn guard)
        {
            if (guard.Map is not { } map || Of(map) is not { size: > 0 } care) return null;
            care.QueueFor(map);
            if (care.queue.Count == 0) return null;
            care.scratch.Clear();
            care.scratch.AddRange(care.queue);
            care.scratch.Sort((a, b) => a.kind != b.kind ? a.kind.CompareTo(b.kind) : guard.Position.DistanceToSquared(a.inmate.Position).CompareTo(guard.Position.DistanceToSquared(b.inmate.Position)));
            int now = Find.TickManager.TicksGame;
            int tries = 0, mealFails = 0, feedFails = 0;
            foreach (CareRequest q in care.scratch)
            {
                // (A request somebody is on already is theirs: the full giver would only fail on the reservation.)
                if (q.served || q.inmate is not { Spawned: true, Dead: false } inmate) continue;
                // (A request this guard was offered no job for is not asked about again for a while: the givers' food, bed and path searches are the cost, and a request nobody can serve is asked about by every think. Neither are
                // more than a few asked about in one look, nor the rest of a kind of food errand once a few of it have found no food for this guard: the search for the others would find none either.)
                long key = ((long)guard.thingIDNumber * 100003 + inmate.thingIDNumber) * KindCount + (int)q.kind, kindKey = -1 - ((long)guard.thingIDNumber * KindCount + (int)q.kind);
                if (care.retryAt.TryGetValue(key, out int retry) && now < retry) continue;
                if (care.retryAt.TryGetValue(kindKey, out int kindRetry) && now < kindRetry) continue;
                // (An inmate the guard cannot get to is no one to ask the givers about: the region reachability is cached, the food and bed searches are not.)
                if (!guard.CanReach(inmate, PathEndMode.Touch, Danger.Deadly)) continue;
                if (TryGive(guard, q.kind, inmate) is { } job) return job;
                if (care.retryAt.Count > 512) care.retryAt.Clear();
                care.retryAt[key] = now + RetryTicks;
                if (q.kind == CareKind.Meal && ++mealFails >= MaxKindFailures || q.kind == CareKind.Feed && ++feedFails >= MaxKindFailures) care.retryAt[kindKey] = now + RetryTicks;
                if (++tries >= MaxTries) break;
            }
            return null;
        }

        /// <summary>The job the work givers of this kind of care offer this guard for this inmate, or null (the giver's own tests, reservation and way there among them: the queue only says who is waiting).</summary>
        public static Job TryGive(Pawn guard, CareKind kind, Pawn inmate)
        {
            if (!MayWork(guard, GiverOf(kind))) return null;
            Job job = GiveFrom(guard, kind, inmate, GiverOf(kind));
            if (job == null && kind == CareKind.Feed && MayWork(guard, RPCareDefOf.FeedPrisoner)) job = GiveFrom(guard, kind, inmate, RPCareDefOf.FeedPrisoner);
            return job;
        }

        /// <summary>The guard can take at least one kind of care by their work settings and capacities (a guard with the warden's work off would be kept from the trouble and could do no care).</summary>
        public static bool CanDoCare(Pawn guard) => MayWork(guard, RPCareDefOf.RP_DeliverCellMeal) || MayWork(guard, RPCareDefOf.RP_RescueInmate) || MayWork(guard, RPCareDefOf.RP_FeedInjuredInmate);

        private static bool MayWork(Pawn guard, WorkGiverDef def) =>
            def != null && guard.workSettings?.WorkIsActive(def.workType) == true && !guard.WorkTypeIsDisabled(def.workType) && def.Worker is WorkGiver_Scanner scanner && scanner.MissingRequiredCapacity(guard) == null;

        private static Job GiveFrom(Pawn guard, CareKind kind, Pawn inmate, WorkGiverDef def)
        {
            var scanner = (WorkGiver_Scanner)def.Worker;
            Job job;
            if (kind == CareKind.Rescue || kind == CareKind.Return || kind == CareKind.Watch)
            {
                if (!scanner.HasJobOnThing(guard, inmate)) return null;
                job = scanner.JobOnThing(guard, inmate);
                if (job != null && kind == CareKind.Return && !SupermaxSystem.IsFetch(job)) return null;
            }
            else job = scanner.JobOnThing(guard, inmate);
            if (job != null) job.workGiverDef = def;
            return job;
        }

        // ---- for the screens and the tests ----------------------------------------------------------------------------------------------------------------------------

        /// <summary>The state on one line (the saveload plan compares it across a round trip): the size and the pins by id, in the order named.</summary>
        public string Describe() => $"{size}:{string.Join("/", pins)}";

        /// <summary>For a check: the next look works everything out again (the size and the pins as they are).</summary>
        internal void ForceRebuild()
        {
            dirty = true;
            retryAt.Clear();
            queueTick = -QueueTicks * 2;
        }
    }

    /// <summary>
    /// A reserve guard is given a care job before the ordinary work (the non-emergency work node: a rescue, which is an emergency giver, is theirs anyway, and the restraints and the rest they do not take, see <see cref="CareDetail.Holds"/>).
    /// A guard who is not in the reserve, and any guard when the reserve is 0, goes straight through to vanilla's scan.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    public static class Patch_JobGiver_Work_CareFirst
    {
        /// <summary>For the checks: the care first off alone (the reserve's holds stay), to show what the work node gives the same guard without it.</summary>
        internal static bool Off;

        public static bool Prefix(JobGiver_Work __instance, Pawn pawn, ref ThinkResult __result)
        {
            // (Vanilla's first test is for a pawn in labour: the work node gives them nothing, and neither does the care first.)
            if (Off || __instance.emergency || !CareDetail.IsReserve(pawn) || pawn.RaceProps.Humanlike && pawn.health.hediffSet.InLabor()) return true;
            Job job = CareDetail.CareJobFor(pawn);
            if (job == null) return true;
            __result = new ThinkResult(job, __instance, JobTag.MiscWork);
            return false;
        }
    }
}
