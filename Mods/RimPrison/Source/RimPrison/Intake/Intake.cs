using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>How many inmates per security level arrive with the daily shuttle, and when.</summary>
    public class IntakePolicy : IExposable
    {
        public int hour = 9;
        /// <summary>The day the shuttle was last called: it comes once a day, whatever the hour is set to afterwards.</summary>
        public int lastRunDay = -1;
        public Dictionary<SecurityLevelDef, int> perDay = new Dictionary<SecurityLevelDef, int>();
        /// <summary>Accept inmates beyond free beds (they wait in holding and raise tension).</summary>
        public bool allowOvercrowding;
        public int nextInmateNumber = 1;

        public void EnsureDefaults()
        {
            foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading)
                if (!perDay.ContainsKey(sec)) perDay[sec] = sec.defaultIntakePerDay;
        }

        public int PerDay(SecurityLevelDef sec) => perDay.TryGetValue(sec, out int n) ? n : 0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref hour, "hour", 9);
            Scribe_Values.Look(ref lastRunDay, "lastRunDay", -1);
            Scribe_Collections.Look(ref perDay, "perDay", LookMode.Def, LookMode.Value);
            Scribe_Values.Look(ref allowOvercrowding, "allowOvercrowding");
            Scribe_Values.Look(ref nextInmateNumber, "nextInmateNumber", 1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                perDay ??= new Dictionary<SecurityLevelDef, int>();
                perDay.RemoveAll(kv => kv.Key == null);
            }
        }
    }

    /// <summary>Who may live in a wing (Intake tab): men and women are kept apart, by the wing when the warden says so and by the cell always (see <see cref="Cellmates"/>).</summary>
    public enum WingGender : byte
    {
        All,
        Men,
        Women,
    }

    public static class IntakeSystem
    {
        /// <summary>New arrivals not searched by then go to their cells anyway.</summary>
        public const int SearchTimeoutTicks = GenDate.TicksPerHour * 6;
        public const float ArrivalContrabandChance = 0.15f;

        /// <summary>Whether the daily shuttle is called at this hour: at the set hour, and not twice in a day (moving the hour past the present one after it came gives no second shuttle and grant).</summary>
        public static bool ShuttleDue(PrisonGame game, int hour)
        {
            int day = Ledger.CurrentDay;
            if (hour != game.intake.hour || game.intake.lastRunDay == day) return false;
            game.intake.lastRunDay = day;
            return true;
        }

        /// <summary>
        /// The inmates the state sends today: as many as policy and free cell beds allow, generated and given their file but not yet anywhere (the
        /// shuttle carries them; <see cref="RunIntake"/> takes them in at once). Under special measures the state caps the lot (or, once the term is
        /// extended, sends none): <paramref name="held"/> says it did, <paramref name="limit"/> by how much. <paramref name="maxCount"/> is what the shuttle can
        /// unload in its stay (<see cref="ShuttleSystem.MaxRoster"/>); whoever the last shuttle could not unload comes first (<see cref="ShuttleSystem.carriedOver"/>).
        /// </summary>
        public static List<Pawn> GenerateRoster(PrisonGame game, Map map, out bool held, out int limit, int maxCount = int.MaxValue)
        {
            var roster = new List<Pawn>();
            List<SecurityLevelDef> levels = DefDatabase<SecurityLevelDef>.AllDefsListForReading;
            // (Those on their way out are no longer the prison's to house: their beds are free.)
            int room = Mathf.CeilToInt(Capacity(map) * 1.25f) - ShuttleSystem.InmateCount(map);
            limit = SpecialMeasures.IntakeLimit;
            held = false;
            // The beds the inmates picked so far would take (nobody has one yet): a later level does not count them as free.
            var reserved = new Dictionary<Building_Bed, int>();
            // The state's marshals' prisoners (returning ex-inmates, recaptured escapees) come first, when there is a bed for them: they are the state's own, so the special measures' cap does not hold
            // them back, and they are not new inmates (nobody is generated for them, no grant is paid).
            int marshalsAboard = 0;
            foreach (MarshalEntry m in game.shuttle.LivingMarshals())
            {
                if (roster.Count >= maxCount) break;
                SecurityLevelDef msec = m.pawn.Inmate().security;
                List<Building_Bed> mBeds = FreePrisonerBeds(map, msec, m.pawn.gender);
                foreach (KeyValuePair<Building_Bed, int> kv in reserved)
                    for (int n = 0; n < kv.Value; n++) mBeds.Remove(kv.Key);
                if (mBeds.Count == 0 && (!game.intake.allowOvercrowding || room <= 0 || msec.wingOnly || !WingAllows(msec, m.pawn.gender))) continue;
                room--;
                if (Cellmates.PickBed(mBeds, m.pawn.Inmate()) is { } mBed) reserved[mBed] = (reserved.TryGetValue(mBed, out int mTaken) ? mTaken : 0) + 1;
                roster.Add(m.pawn);
                marshalsAboard++;
            }
            bool restricted = game.AnyWingRestricted();
            // A custody contract's cohort comes next (see CustodyContracts): the state's own first, then the ones the warden signed for, then the day's policy.
            if (AddCustodyCohort(game, map, roster, reserved, ref room, maxCount, restricted) && marshalsAboard > 0) CustodyContracts.Notify_RosterFull(game);
            List<Pawn> carried = game.shuttle.LivingCarriedOver();
            if (restricted) SettleCarriedOfTheWrongSex(game, map, carried);
            for (int l = 0; l < levels.Count && roster.Count < maxCount; l++)
            {
                SecurityLevelDef sec = levels[l];
                // (Supermax is never sent by the state: you move inmates there.)
                int want = Unlocks.IsUnlocked(sec) && !sec.escortOnly && !sec.referralOnly ? game.intake.PerDay(sec) : 0;
                List<Building_Bed> freeBeds = FreePrisonerBeds(map, sec);
                foreach (KeyValuePair<Building_Bed, int> kv in reserved)
                    for (int n = 0; n < kv.Value; n++) freeBeds.Remove(kv.Key);
                for (int i = 0; i < want; i++)
                {
                    if (roster.Count >= maxCount) break;
                    if (roster.Count - marshalsAboard >= limit)
                    {
                        held = true;
                        break;
                    }
                    bool overcrowded = game.intake.allowOvercrowding && room > 0 && !sec.wingOnly;
                    // The shuttle that could not unload them brings them first (one a bed of theirs, or overcrowding, can take now: one who cannot wait their turn and use up none of the day's); a new
                    // inmate is generated only when none of this level can come.
                    Pawn p = carried.Find(x => x.Inmate()?.security == sec && (freeBeds.Count > 0 && (!restricted || freeBeds.Exists(b => BedAllows(b, x.gender))) || overcrowded && WingAllows(sec, x.gender)));
                    if (p != null)
                    {
                        carried.Remove(p);
                        p.Inmate().carriedSkipDay = -1;
                    }
                    else
                    {
                        // Men and women are kept apart: with beds free in the wings of one sex only, the one who comes is of that sex; with none free (overcrowding), of the sex the level's wing is kept for.
                        Gender? sex = !restricted ? null : freeBeds.Count > 0 ? SexWithABed(freeBeds) : PolicySex(game, sec);
                        bool hasBed = freeBeds.Count > 0 && (sex == null || freeBeds.Exists(b => BedAllows(b, sex.Value)));
                        if (!hasBed && !overcrowded) break;
                        p = InmateGenerator.Generate(sec, game.intake.nextInmateNumber++, sex);
                    }
                    room--;
                    if (Cellmates.PickBed(freeBeds, p.Inmate()) is { } bed) reserved[bed] = (reserved.TryGetValue(bed, out int taken) ? taken : 0) + 1;
                    roster.Add(p);
                }
            }
            // Those who did not fit again today stay on the list for the next shuttle.
            game.shuttle.carriedOver.RemoveAll(x => roster.Contains(x));
            // Those whose level wants none (set to nothing, or not unlocked) are taken back by the state: they would only pile up, shuttle after shuttle, with nobody to come for them.
            foreach (Pawn gone in new List<Pawn>(game.shuttle.carriedOver))
                if (gone.Inmate()?.security is { } level && (!Unlocks.IsUnlocked(level) || game.intake.PerDay(level) <= 0))
                {
                    game.shuttle.carriedOver.Remove(gone);
                    if (Find.WorldPawns.Contains(gone)) Find.WorldPawns.RemoveAndDiscardPawnViaGC(gone);
                }
            return roster;
        }

        /// <summary>
        /// The pending members of the running custody contract that have a bed to come to: a pawn already made (carried over from a landing that could not unload them) or a new one made to the dossier's row (level, crime, term), each in a free
        /// bed of their level's wing or none (never overcrowding: the state asked for a place for them). A member with no bed waits for the next roster. True when the roster was full and a member who could have come was left off.
        /// </summary>
        private static bool AddCustodyCohort(PrisonGame game, Map map, List<Pawn> roster, Dictionary<Building_Bed, int> reserved, ref int room, int maxCount, bool restricted)
        {
            CustodyContract c = game.custody?.contract;
            if (c == null || SpecialMeasures.Restricted || c.mapId != map.uniqueID) return false;
            bool full = false;
            foreach (CustodyMember m in c.members)
            {
                if (m.state != CustodyState.Pending || m.level == null) continue;
                Pawn made = m.pawn;
                // (Aboard a shuttle already, or dead: the unloading and the hourly pass deal with them.)
                if (made != null && (made.Dead || made.Destroyed || made.Spawned || roster.Contains(made) || CustodyContracts.Aboard(game, made))) continue;
                if (made == null && m.pawnId >= 0) continue;
                List<Building_Bed> beds = FreePrisonerBeds(map, m.level, made?.gender);
                foreach (KeyValuePair<Building_Bed, int> kv in reserved)
                    for (int n = 0; n < kv.Value; n++) beds.Remove(kv.Key);
                Gender? sex = made != null ? made.gender : !restricted ? null : beds.Count > 0 ? SexWithABed(beds) : PolicySex(game, m.level);
                if (beds.Count == 0 || sex != null && made == null && !beds.Exists(b => BedAllows(b, sex.Value))) continue;
                if (roster.Count >= maxCount)
                {
                    full = true;
                    break;
                }
                Pawn p = made;
                if (p == null)
                {
                    p = InmateGenerator.Generate(m.level, game.intake.nextInmateNumber++, sex, m.crime);
                    // (The dossier's term, and no more: their file is otherwise rolled as any inmate's is, hidden parts included.)
                    p.Inmate().sentenceDays = m.termDays;
                    CustodyContracts.Notify_Generated(m, p);
                }
                room--;
                if (Cellmates.PickBed(beds, p.Inmate()) is { } bed) reserved[bed] = (reserved.TryGetValue(bed, out int taken) ? taken : 0) + 1;
                roster.Add(p);
            }
            return full;
        }

        /// <summary>The one sex the free beds are for, when the wings the warden keeps for men or women leave beds for one of them only (null: for both, or for neither).</summary>
        public static Gender? SexWithABed(List<Building_Bed> free)
        {
            bool men = free.Exists(b => BedAllows(b, Gender.Male)), women = free.Exists(b => BedAllows(b, Gender.Female));
            return men == women ? null : men ? Gender.Male : Gender.Female;
        }

        /// <summary>The one sex a level's wing is kept for (null: all inmates).</summary>
        private static Gender? PolicySex(PrisonGame game, SecurityLevelDef sec) => game.WingGenderFor(sec) switch { WingGender.Men => Gender.Male, WingGender.Women => Gender.Female, _ => null };

        /// <summary>"men" or "women": who a wing kept for one sex is kept for (for the messages).</summary>
        public static string WhoWord(SecurityLevelDef wing) => PrisonGame.Instance?.WingGenderFor(wing) == WingGender.Women ? "RP_WingWho_Women".Translate().ToString() : "RP_WingWho_Men".Translate().ToString();

        /// <summary>Whether any bed of the prison could take this sex at this level: the level's own wing is not kept for the other sex, or there is a bed in no wing (full or not) the level may use.</summary>
        public static bool LevelTakes(Map map, SecurityLevelDef sec, Gender gender)
        {
            if (WingAllows(sec, gender)) return true;
            if (sec.wingOnly || PrisonMap.Of(map) is not { } pm) return false;
            foreach (Building_Bed bed in pm.PrisonerBeds)
                if (bed.Spawned && bed.ForPrisoners && !bed.Medical && !PunishmentUtility.IsSolitaryBed(bed) && bed.CompAssignableToPawn != null && WingOf(bed) == null) return true;
            return false;
        }

        /// <summary>
        /// Passengers a shuttle could not unload who are of a sex no bed of their level will take now (see <see cref="LevelTakes"/>): they do not come on today's roster, and so use up none of any level's day. They
        /// wait the day (the warden may change the wing back); the next roster sends them to another prison, with a message naming them and the wing.
        /// </summary>
        private static void SettleCarriedOfTheWrongSex(PrisonGame game, Map map, List<Pawn> carried)
        {
            int day = Ledger.CurrentDay;
            for (int i = carried.Count - 1; i >= 0; i--)
            {
                Pawn p = carried[i];
                if (p.Inmate() is not { security: { } sec } c) continue;
                if (LevelTakes(map, sec, p.gender))
                {
                    c.carriedSkipDay = -1;
                    continue;
                }
                carried.RemoveAt(i);
                if (c.carriedSkipDay < 0)
                {
                    c.carriedSkipDay = day;
                    continue;
                }
                if (day <= c.carriedSkipDay) continue;
                game.shuttle.carriedOver.Remove(p);
                Messages.Message("RP_CarriedSentElsewhere".Translate(p.LabelShort, sec.label, WhoWord(sec)), MessageTypeDefOf.NeutralEvent, historical: false);
                if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemoveAndDiscardPawnViaGC(p);
            }
        }

        /// <summary>An arrival with no bed in a wing that keeps their sex out: the warden is told why (no bed of theirs is free in another wing).</summary>
        public static void TellIfSexLeftThemBedless(Pawn p, SecurityLevelDef sec)
        {
            if (sec == null || WingAllows(sec, p.gender)) return;
            Messages.Message("RP_ArrivalNoBedForSex".Translate(p.LabelShort, sec.label, WhoWord(sec)), p, MessageTypeDefOf.CautionInput, historical: false);
        }

        /// <summary>
        /// A new inmate steps into the prison: spawned at <paramref name="cell"/>, in uniform, with the best-fitting free bed in their wing (none
        /// under overcrowding), a line in the chronicle, and, with a holding zone, waiting there for a search (some bring something in). From the
        /// shuttle they are an arrival until they reach holding: guards walk them in and the gates are open to them on the way.
        /// </summary>
        public static void AdmitArrival(PrisonGame game, Map map, Pawn p, IntVec3 cell, bool fromShuttle)
        {
            CompInmate comp = p.Inmate();
            // The state's own prisoner, brought back by its marshals: not a new inmate, but the return the rearrest or the recapture was waiting for (their own arrival line, first on the roster).
            if (game.shuttle.TakeMarshal(p) is { } marshal)
            {
                AdmitMarshalsPrisoner(game, map, p, comp, cell, marshal, fromShuttle);
                return;
            }
            List<Building_Bed> freeBeds = FreePrisonerBeds(map, comp.security, p.gender);
            // Beds within the inmate's own wing first (end of the list), then the best cellmates.
            Building_Bed bed = Cellmates.PickBed(freeBeds, comp);
            // Their own things stay behind: vanilla drops them where they stand when the guest status changes (forbidden), which was junk in holding and on the ramp would be
            // junk that holds the next shuttle up.
            p.equipment?.DestroyAllEquipment();
            p.inventory?.DestroyAll();
            GenSpawn.Spawn(p, cell, map);
            InmateGenerator.MakeInmate(p, bed);
            if (bed == null) TellIfSexLeftThemBedless(p, comp.security);
            Story.Admitted(p, fromShuttle ? "RP_Chr_AdmittedShuttle" : "RP_Chr_Admitted");
            // One arrival in twelve is kin to a member of staff, and now and then to an inmate inside.
            Kin.OnArrived(p, map);
            // Intake processing: new arrivals wait in holding for a search; some bring something in.
            if (PrisonMap.Of(map)?.zones.Any(RPDefOf.RP_Holding) == true)
            {
                comp.awaitingSearch = true;
                comp.holdingSinceTick = Find.TickManager.TicksGame;
                if (Rand.Chance(ArrivalContrabandChance)) ContrabandUtility.GiveSmuggled(p);
            }
            // A custody contract's cohort member: the payment on admission, and the window starts.
            CustodyContracts.Notify_Admitted(game, p);
            if (fromShuttle) game.shuttle.AddArrival(p);
        }

        private static void AdmitMarshalsPrisoner(PrisonGame game, Map map, Pawn p, CompInmate comp, IntVec3 cell, MarshalEntry marshal, bool fromShuttle)
        {
            Building_Bed bed = Cellmates.PickBed(FreePrisonerBeds(map, comp.security, p.gender), comp);
            if (marshal.kind == MarshalKind.Recaptured) FugitiveSystem.Return(map, game, p, marshal.escapedTick, comp, bed, cell);
            else Reoffenders.Return(game, map, p, comp, bed, marshal.reason, cell);
            if (bed == null) TellIfSexLeftThemBedless(p, comp.security);
            // (A return counts as admitted in its own, as it always did; the shuttle and the intake count only the new inmates.)
            Messages.Message(("RP_MarshalsArrival_" + marshal.kind).Translate(p.LabelShort), new LookTargets(p), MessageTypeDefOf.NeutralEvent, historical: false);
            if (fromShuttle) game.shuttle.AddArrival(p);
        }

        /// <summary>Takes the day's inmates in at once, with no shuttle (the developer button and the tests): as many as policy and free cell beds allow.</summary>
        public static List<Pawn> RunIntake(PrisonGame game, Map map, bool silent = false)
        {
            var arrived = new List<Pawn>();
            int grant = 0;
            IntVec3 spawnRoot = IntakeSpot(map);
            List<Pawn> roster = GenerateRoster(game, map, out bool held, out int limit);
            int fromMarshals = 0;
            foreach (Pawn p in roster)
            {
                // (The state's own prisoners come back at no price, and count as admitted by their own return.)
                bool marshal = game.shuttle.IsMarshal(p);
                AdmitArrival(game, map, p, IntakeCell(map, spawnRoot), fromShuttle: false);
                if (marshal) fromMarshals++;
                else grant += p.Inmate().security.intakeGrant;
                arrived.Add(p);
            }
            if (held && !silent) Messages.Message((limit == 0 ? "RP_IntakeHeldBackAll" : "RP_IntakeHeldBack").Translate(limit), MessageTypeDefOf.NegativeEvent);
            if (AutoTest.Active) RPLog.Message($"[intake] arrived={arrived.Count} freeBeds={FreeBedCount(map)} capacity={Capacity(map)} inmates={map.mapPawns.PrisonersOfColonySpawnedCount}");
            if (arrived.Count > 0)
            {
                game.ledger.Add(RPDefOf.RP_IntakeGrants, grant, "RP_IntakeNote".Translate(arrived.Count));
                game.stats.admitted += arrived.Count - fromMarshals;
                if (!silent)
                    Messages.Message("RP_IntakeArrived".Translate(arrived.Count, Money.Format(grant)), new LookTargets(arrived), MessageTypeDefOf.NeutralEvent);
            }
            else if (!silent && FreeBedCount(map) == 0 && AnyWanted(game))
            {
                Messages.Message("RP_IntakeNoBeds".Translate(), MessageTypeDefOf.CautionInput);
            }
            return arrived;
        }

        public static bool AnyWanted(PrisonGame game)
        {
            foreach (KeyValuePair<SecurityLevelDef, int> kv in game.intake.perDay) if (kv.Value > 0) return true;
            return false;
        }

        private static readonly List<Building_Bed> scratchWing = new List<Building_Bed>(), scratchOpen = new List<Building_Bed>();

        /// <summary>
        /// Free cell bed slots. With a security level: beds in that level's wing first, then beds in no wing; beds
        /// in other levels' wings are never offered. Shuffled with the game's random numbers (the order picks who gets which bed), so
        /// the screens, which only count, ask <see cref="FreeBedCount"/>: a screen must not move the game's random sequence.
        /// </summary>
        public static List<Building_Bed> FreePrisonerBeds(Map map, SecurityLevelDef sec = null, Gender? gender = null)
        {
            var wing = new List<Building_Bed>();
            var open = new List<Building_Bed>();
            CollectFreeBeds(map, sec, wing, open, gender);
            wing.Shuffle();
            open.Shuffle();
            // Popped from the end: wing beds last in the list come out first.
            open.AddRange(wing);
            return open;
        }

        /// <summary>The beds <see cref="FreePrisonerBeds"/> would offer, in their fixed order (no shuffle, so no random draw is spent): for a reading that asks only whether a bed exists.</summary>
        public static List<Building_Bed> FreeBedsInOrder(Map map, SecurityLevelDef sec = null, Gender? gender = null)
        {
            var wing = new List<Building_Bed>();
            var open = new List<Building_Bed>();
            CollectFreeBeds(map, sec, wing, open, gender);
            open.AddRange(wing);
            return open;
        }

        /// <summary>How many slots <see cref="FreePrisonerBeds"/> would offer (<paramref name="inWingOnly"/>: only those in the level's own wing), without building or shuffling a list.</summary>
        public static int FreeBedCount(Map map, SecurityLevelDef sec = null, bool inWingOnly = false, Gender? gender = null)
        {
            scratchWing.Clear();
            scratchOpen.Clear();
            CollectFreeBeds(map, sec, scratchWing, scratchOpen, gender);
            int count = inWingOnly ? scratchWing.Count : scratchWing.Count + scratchOpen.Count;
            scratchWing.Clear();
            scratchOpen.Clear();
            return count;
        }

        /// <summary>Whether the warden lets this sex live in this wing (no wing: anyone).</summary>
        public static bool WingAllows(SecurityLevelDef wing, Gender gender)
        {
            if (wing == null || gender == Gender.None || PrisonGame.Instance == null) return true;
            WingGender policy = PrisonGame.Instance.WingGenderFor(wing);
            return policy == WingGender.All || (policy == WingGender.Men ? gender == Gender.Male : gender == Gender.Female);
        }

        /// <summary>Whether the bed's wing takes this sex (see <see cref="WingAllows"/>).</summary>
        public static bool BedAllows(Building_Bed bed, Gender gender) => WingAllows(WingOf(bed), gender);

        private static void CollectFreeBeds(Map map, SecurityLevelDef sec, List<Building_Bed> wing, List<Building_Bed> open, Gender? gender = null)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            // A bed someone will come back to (in solitary, or carried off hurt) is not free. (Not only the spawned: a
            // carried inmate holds theirs through the whole walk.)
            Dictionary<Building_Bed, int> held = null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
            {
                // (Read only: this is asked by the screens, which must not change the game. A stale home bed is let go in each inmate's hourly update.)
                if (!p.Dead && PunishmentUtility.HeldHomeBed(p, p.Inmate()) is { } hb)
                {
                    held ??= new Dictionary<Building_Bed, int>();
                    held.TryGetValue(hb, out int n);
                    held[hb] = n + 1;
                }
            }
            foreach (Building_Bed bed in pm.PrisonerBeds)
            {
                // (The list refreshes every 250 ticks and not while paused: a bed switched off or made medical since is not free.)
                if (!bed.Spawned || !bed.ForPrisoners || bed.Medical || PunishmentUtility.IsSolitaryBed(bed)) continue;
                CompAssignableToPawn assign = bed.CompAssignableToPawn;
                int taken = assign == null ? 0 : assign.AssignedPawnsForReading.Count + (held != null && held.TryGetValue(bed, out int h) ? h : 0);
                if (assign == null || taken >= assign.TotalSlots) continue;
                SecurityLevelDef bedWing = pm?.zones.ZoneAt(bed.Position)?.security;
                if (sec != null && bedWing != null && bedWing != sec) continue;
                if (sec is { wingOnly: true } && bedWing != sec) continue;
                if (gender != null && !WingAllows(bedWing, gender.Value)) continue;
                // (A level that keeps its inmates apart takes a cell of its own: one bed, one in the room.)
                if (sec is { escortOnly: true } or { paddedOnly: true } && !SingleCell(bed)) continue;
                // (A padded bed is a bed for the psych wing's cells and for nothing else: not one outside the wing or in a shared room, which houses no one, whatever the level.)
                if (PaddedCells.IsPadded(bed) && !InmateUtility.BedFitsLevel(bed, PsychWing.Level)) continue;
                // (Psychiatric care takes a padded cell: the bed is the padding.)
                if (sec is { paddedOnly: true } && !PaddedCells.IsPadded(bed)) continue;
                List<Building_Bed> into = sec != null && bedWing == sec ? wing : open;
                for (int s = taken; s < assign.TotalSlots; s++) into.Add(bed);
            }
        }

        /// <summary>Is this bed the only one in its room, with one place in it (a cell for one: supermax takes no other)?</summary>
        public static bool SingleCell(Building_Bed bed)
        {
            if (bed.SleepingSlotsCount != 1 || bed.GetRoom() is not { } room) return false;
            int beds = 0;
            foreach (Building_Bed other in room.ContainedBeds)
                if (other.ForPrisoners && !other.Medical && ++beds > 1) return false;
            return true;
        }

        public static SecurityLevelDef WingOf(Building_Bed bed) => bed == null ? null : PrisonMap.Of(bed.Map)?.zones.ZoneAt(bed.Position)?.security;

        public static int Capacity(Map map)
        {
            int n = 0;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return 0;
            foreach (Building_Bed bed in pm.PrisonerBeds)
                if (bed.Spawned && bed.CompAssignableToPawn != null && !PunishmentUtility.IsSolitaryBed(bed)) n += bed.CompAssignableToPawn.TotalSlots;
            return n;
        }

        /// <summary>A free standable holding cell (never outside the zone), else near the spawn root.</summary>
        public static IntVec3 IntakeCell(Map map, IntVec3 root)
        {
            List<IntVec3> holding = PrisonMap.Of(map)?.zones.CellsOf(RPDefOf.RP_Holding);
            if (holding != null && holding.Count > 0)
            {
                for (int i = 0; i < 20; i++)
                {
                    IntVec3 c = holding.RandomElement();
                    if (c.Standable(map) && c.GetFirstPawn(map) == null) return c;
                }
                foreach (IntVec3 c in holding) if (c.Standable(map)) return c;
            }
            return CellFinder.TryFindRandomCellNear(root, map, 4, c => c.Standable(map), out IntVec3 near) ? near : root;
        }

        /// <summary>Holding zone if painted, else the map edge closest to the map's prison (or a road edge).</summary>
        public static IntVec3 IntakeSpot(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            List<IntVec3> holding = pm?.zones.CellsOf(RPDefOf.RP_Holding);
            if (holding != null && holding.Count > 0) return holding.RandomElement();
            IntVec3 target = map.Center;
            if (CellFinder.TryFindRandomEdgeCellWith(c => c.Standable(map) && map.reachability.CanReach(c, target, Verse.AI.PathEndMode.OnCell, TraverseMode.PassDoors, Danger.Deadly), map, CellFinder.EdgeRoadChance_Always, out IntVec3 edge))
                return edge;
            return CellFinder.RandomEdgeCell(map);
        }
    }

    public static class InmateGenerator
    {
        private static PawnKindDef InmateKind => RPDefOf.RP_Inmate;

        public static Faction OutsideFaction => Find.FactionManager.FirstFactionOfDef(RPDefOf.RP_Outside) ?? Find.FactionManager.RandomNonHostileFaction(allowNonHumanlike: false);

        /// <param name="crime">The crime the file names (a custody contract's dossier says which), or null for one of the level's.</param>
        public static Pawn Generate(SecurityLevelDef sec, int number, Gender? gender = null, CrimeDef crime = null)
        {
            var request = new PawnGenerationRequest(InmateKind, OutsideFaction, PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f,
                allowFood: false, allowAddictions: true, inhabitant: false, fixedGender: gender);
            Pawn p = PawnGenerator.GeneratePawn(request);
            CompInmate comp = p.Inmate();
            if (crime != null) comp.crime = crime;
            // (Numbered first: a profile filled for a file with no number takes the next one off the counter, and this one would use up two.)
            comp.inmateNumber = number;
            AssignProfile(p, comp, sec);
            return p;
        }

        /// <summary>Fills the inmate record (also used for prisoners captured some other way).</summary>
        public static void AssignProfile(Pawn p, CompInmate comp, SecurityLevelDef sec)
        {
            if (comp == null) return;
            sec ??= comp.security ?? InmateUtility.DefaultSecurity;
            comp.security = sec;
            if (comp.crime == null && sec.crimes.Count > 0) comp.crime = sec.crimes.RandomElement();
            if (comp.crime != null)
                comp.sentenceDays = Rand.Chance(comp.crime.lifeChance) ? -1 : comp.crime.sentenceDays.RandomInRange;
            if (comp.admittedTick < 0)
            {
                comp.admittedTick = Find.TickManager.TicksGame;
                comp.innocent = Rand.Chance(Innocence.Chance); // death row rolls its own below
            }
            if (comp.reputations.Count == 0) ReputationUtility.Roll(p, comp);
            comp.religious = Rand.Chance(FaithUtility.ReligiousChance);
            comp.familyCount = Rand.Chance(comp.IsLifer ? 0.5f : 0.2f) ? 0 : Rand.RangeInclusive(1, 3);
            Relatives.Of(comp);
            if (comp.gang == null) GangUtility.RollAtIntake(p, comp);
            if (comp.appealsLeft < 0 && DeathRowSystem.Ext(comp) is { } dr) DeathRowSystem.OnAdmitted(comp, dr);
            if (comp.want == null) Wants.Roll(p, comp);
            if (comp.inmateNumber == 0 && PrisonGame.Instance != null) comp.inmateNumber = PrisonGame.Instance.intake.nextInmateNumber++;
        }

        /// <summary>Spawned pawn → prisoner of the player in uniform, with a bed if one was found.</summary>
        public static void MakeInmate(Pawn p, Building_Bed bed)
        {
            // Their own things go before the guest status changes: vanilla drops them where they stand then (forbidden), and a marshals' prisoner stands on the shuttle's ramp - junk that held
            // every shuttle after it up for an hour.
            p.equipment?.DestroyAllEquipment();
            p.inventory?.DestroyAll();
            p.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
            p.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.MaintainOnly);
            DressInUniform(p);
            // After SetGuestStatus: changing guest status unclaims beds.
            if (bed != null) p.ownership.ClaimBedIfNonMedical(bed);
            p.Inmate().RefreshActiveFlag();
            p.needs?.AddOrRemoveNeedsAsAppropriate();
        }

        public static void DressInUniform(Pawn p)
        {
            if (p.apparel == null) return;
            // Issued jackets go back to the store, not the bin.
            if (PrisonGame.Instance != null) PrisonGame.Instance.jacketStock += p.apparel.WornApparel.Count(a => a.def == RPVanillaDefOf.Apparel_Jacket && p.apparel.IsLocked(a));
            p.apparel.DestroyAll();
            Color color = p.Inmate()?.security?.color ?? new Color(0.9f, 0.45f, 0.1f);
            Wear(p, RPVanillaDefOf.Apparel_BasicShirt, color);
            Wear(p, RPVanillaDefOf.Apparel_Pants, color);
        }

        private static void Wear(Pawn p, ThingDef def, Color color)
        {
            if (def == null || !def.apparel.PawnCanWear(p)) return;
            var apparel = (Apparel)ThingMaker.MakeThing(def, ThingDefOf.Cloth);
            apparel.SetColor(color, reportFailure: false);
            p.apparel.Wear(apparel, dropReplacedApparel: false, locked: true);
        }
    }
}
