using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The inmates each guard work giver could act on right now, as short lists built from one pass over the prisoners (at most once a tick for
    /// each list, and only when its giver asks). Vanilla's work scan checks that a guard can reach every thing a giver offers before the giver is
    /// asked whether it has a job there, so a giver that offered every inmate cost a reachability query per inmate per guard scan, to be told no.
    /// Each list holds the inmates that pass the giver's own guard-independent tests (the same methods its <c>HasJobOnThing</c> starts with); an
    /// empty list lets the giver skip the scan altogether.
    /// </summary>
    public sealed class InmateCandidates
    {
        /// <summary>The one set of lists (each is stamped with the tick and the map it was built for).</summary>
        public static readonly InmateCandidates Shared = new InmateCandidates();

        private sealed class Entry
        {
            public readonly List<Thing> list = new List<Thing>();
            public readonly Func<Pawn, bool> test;
            public int tick = -1, mapId = -1;

            public Entry(Func<Pawn, bool> test) => this.test = test;
        }

        private readonly Entry subdue = new Entry(GuardUtility.NeedsSubduing), punishmentCarry = new Entry(WorkGiver_TakeToPunishment.Candidate),
            rescue = new Entry(WorkGiver_RescueInmate.Candidate), execution = new Entry(WorkGiver_CarryOutExecution.Candidate), therapy = new Entry(WorkGiver_Therapy.Candidate),
            frisk = new Entry(WorkGiver_Frisk.Candidate), armed = new Entry(ArmedResponse.ShouldShoot), escort = new Entry(SupermaxSystem.Candidate),
            exitSession = new Entry(WorkGiver_GangExitSession.Candidate), medication = new Entry(WorkGiver_MedicationRound.Candidate), evacuate = new Entry(EvacuationSystem.NeedsGuard),
            watchCheck = new Entry(WorkGiver_WatchCheck.Candidate), crisis = new Entry(WorkGiver_CrisisResponse.Candidate), counselling = new Entry(WorkGiver_WatchCounselling.Candidate);

        // Vanilla's warden givers (see VanillaGiverLists): the inmates each could act on, whoever the guard is.
        private readonly Entry chat = new Entry(VanillaGiverLists.ChatCandidate), convert = new Entry(VanillaGiverLists.ConvertCandidate),
            interrogate = new Entry(VanillaGiverLists.InterrogateCandidate), release = new Entry(VanillaGiverLists.ReleaseCandidate), doExecution = new Entry(VanillaGiverLists.ExecutionCandidate),
            feedPrisoner = new Entry(VanillaGiverLists.FeedPrisonerCandidate), takeToBed = new Entry(VanillaGiverLists.TakeToBedCandidate);

        // Vanilla's doctor givers: their own lists (everybody who is hurt, hungry or downed), with the inmates who could not need them taken out.
        private readonly Entry tend = new Entry(VanillaGiverLists.TendCandidate), tendHumanlike = new Entry(VanillaGiverLists.TendCandidate),
            feedPatient = new Entry(VanillaGiverLists.FeedPatientCandidate), feedAnimals = new Entry(_ => false), rescueDowned = new Entry(VanillaGiverLists.RescueDownedCandidate);

        public List<Thing> Chat(Map map) => Get(chat, map);

        public List<Thing> Convert(Map map) => Get(convert, map);

        public List<Thing> Interrogate(Map map) => Get(interrogate, map);

        public List<Thing> Release(Map map) => Get(release, map);

        public List<Thing> DoExecution(Map map) => Get(doExecution, map);

        public List<Thing> FeedPrisoner(Map map) => Get(feedPrisoner, map);

        public List<Thing> TakeToBed(Map map) => Get(takeToBed, map);

        public List<Thing> Tend(Map map, IEnumerable<Thing> vanilla) => Filtered(tend, map, vanilla);

        public List<Thing> TendHumanlike(Map map, IEnumerable<Thing> vanilla) => Filtered(tendHumanlike, map, vanilla);

        public List<Thing> FeedPatient(Map map, IEnumerable<Thing> vanilla) => Filtered(feedPatient, map, vanilla);

        /// <summary>The animals' feeding giver never feeds an inmate (<c>feedAnimalsOnly</c>): they are all left out of its list.</summary>
        public List<Thing> FeedAnimals(Map map, IEnumerable<Thing> vanilla) => Filtered(feedAnimals, map, vanilla);

        public List<Thing> RescueDowned(Map map, IEnumerable<Thing> vanilla) => Filtered(rescueDowned, map, vanilla);

        /// <summary>Acting out or due for punishment (<see cref="GuardUtility.NeedsSubduing"/>).</summary>
        public List<Thing> Subdue(Map map) => Get(subdue, map);

        /// <summary>Downed with punishment pending (<see cref="WorkGiver_TakeToPunishment.Candidate"/>).</summary>
        public List<Thing> PunishmentCarry(Map map) => Get(punishmentCarry, map);

        /// <summary>Downed in custody outside a bed (<see cref="WorkGiver_RescueInmate.Candidate"/>).</summary>
        public List<Thing> Rescue(Map map) => Get(rescue, map);

        /// <summary>The execution is due and they are down (<see cref="WorkGiver_CarryOutExecution.Candidate"/>).</summary>
        public List<Thing> Execution(Map map) => Get(execution, map);

        /// <summary>Wants an assessment or a session (<see cref="WorkGiver_Therapy.Candidate"/>).</summary>
        public List<Thing> Therapy(Map map) => Get(therapy, map);

        /// <summary>Wants out of a gang and has not had the session yet (<see cref="WorkGiver_GangExitSession.Candidate"/>).</summary>
        public List<Thing> ExitSession(Map map) => Get(exitSession, map);

        /// <summary>Due a frisk (<see cref="WorkGiver_Frisk.Candidate"/>).</summary>
        public List<Thing> Frisk(Map map) => Get(frisk, map);

        /// <summary>Armed response may shoot them (<see cref="ArmedResponse.ShouldShoot"/>).</summary>
        public List<Thing> Armed(Map map) => Get(armed, map);

        /// <summary>Supermax inmates in their cells who are due for the shower or the yard (<see cref="SupermaxSystem.Candidate"/>).</summary>
        public List<Thing> Escort(Map map) => Get(escort, map);

        /// <summary>Waiting for a guard (or a carrier) in an evacuation (<see cref="EvacuationSystem.NeedsGuard(Pawn)"/>).</summary>
        public List<Thing> Evacuate(Map map) => Get(evacuate, map);

        /// <summary>In psychiatric care with their cover running out or gone (<see cref="WorkGiver_MedicationRound.Candidate"/>).</summary>
        public List<Thing> Medication(Map map) => Get(medication, map);

        /// <summary>On watch and due the half-hourly check (<see cref="WorkGiver_WatchCheck.Candidate"/>).</summary>
        public List<Thing> WatchCheck(Map map) => Get(watchCheck, map);

        /// <summary>In a crisis the staff have seen, or on watch (<see cref="WorkGiver_CrisisResponse.Candidate"/>).</summary>
        public List<Thing> Crisis(Map map) => Get(crisis, map);

        /// <summary>On the watch list with counselling, and due the day's session (<see cref="WorkGiver_WatchCounselling.Candidate"/>).</summary>
        public List<Thing> Counselling(Map map) => Get(counselling, map);

        /// <summary>Forget the lists built this tick (tests change inmates within a tick).</summary>
        public void Invalidate()
        {
            subdue.tick = punishmentCarry.tick = rescue.tick = execution.tick = therapy.tick = frisk.tick = armed.tick = escort.tick = exitSession.tick = medication.tick = evacuate.tick = watchCheck.tick = crisis.tick = counselling.tick = -1;
            chat.tick = convert.tick = interrogate.tick = release.tick = doExecution.tick = feedPrisoner.tick = takeToBed.tick = -1;
            tend.tick = tendHumanlike.tick = feedPatient.tick = feedAnimals.tick = rescueDowned.tick = -1;
        }

        /// <summary>
        /// A doctor giver's own list (hurt pawns, hungry pawns, downed pawns: the colonists and the animals among them, too), with the inmates in it who fail the giver's tests for every doctor
        /// taken out. Built once a tick, from the first list the giver gave.
        /// </summary>
        private static List<Thing> Filtered(Entry e, Map map, IEnumerable<Thing> vanilla)
        {
            int now = Find.TickManager.TicksGame;
            if (e.tick == now && e.mapId == map.uniqueID) return e.list;
            e.tick = now;
            e.mapId = map.uniqueID;
            e.list.Clear();
            foreach (Thing t in vanilla)
                if (t is not Pawn { IsPrisonerOfColony: true } p || e.test(p)) e.list.Add(t);
            return e.list;
        }

        private static List<Thing> Get(Entry e, Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (e.tick == now && e.mapId == map.uniqueID) return e.list;
            e.tick = now;
            e.mapId = map.uniqueID;
            e.list.Clear();
            IReadOnlyList<Pawn> prisoners = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < prisoners.Count; i++)
                if (e.test(prisoners[i])) e.list.Add(prisoners[i]);
            return e.list;
        }
    }
}
