using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for bugs found in review (B2-B16; B1 lives in <see cref="CheckParallelPrograms"/>, which runs the class
    /// through real ticks). Each is its own method called once from FireAllEvents, so its names stay its own. They work on
    /// throwaway pawns and put back what they touch (<see cref="Rollback"/>).
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>What a check changed in the shared prison state, put back on dispose, plus its own undo actions (run last to first).</summary>
        private sealed class Rollback : IDisposable
        {
            private readonly PrisonGame g = PrisonGame.Instance;
            private readonly List<Action> undo = new List<Action>();
            private readonly int funds0;
            private readonly float reputation0;
            private readonly List<LedgerEntry> recent0;
            private readonly List<DayBook> days0;
            private readonly List<Dictionary<LedgerCategoryDef, int>> totals0;
            private readonly List<ChronicleEntry> entries0;
            private readonly HashSet<string> firsts0;
            private readonly List<FormerInmate> former0;
            private readonly List<Rearrest> rearrests0, returning0;
            private readonly List<SupervisionCase> supervision0;
            private readonly List<Pawn> supervisionPawns0;
            private readonly int supervisionNext0;
            private readonly HashSet<Letter> letters0;
            private readonly PrisonStats stats0;
            private readonly DailyIncidents today0;
            private readonly Dictionary<SecurityLevelDef, int> misconductByLevel0;
            private readonly List<(CompInmate c, float tension, string cause)> tensions0;
            private readonly HashSet<Pawn> kept0;
            private readonly List<KeepApartOrder> apart0;
            private readonly List<int> handover0;
            private readonly int violentNotes0;
            private readonly List<RiotRecord> riots0;
            private readonly int riotNext0;
            private readonly PrisonMap pm0;
            private readonly List<Rumour> rumours0;
            private readonly List<WardenAsk> asks0;
            private readonly bool? rumourStart0, rumourTip0;

            public Rollback(Map map)
            {
                // (The rumour mill's dice are quiet in a check: a transfer, a death, an order or a search would else start a rumour half the time and write one more chronicle line, or a tip would be about talk. A rumour
                // check sets them on purpose; they are put back as they were on dispose.)
                (rumourStart0, rumourTip0) = (Rumours.ForceStart, Rumours.ForceTip);
                (Rumours.ForceStart, Rumours.ForceTip) = (false, false);
                funds0 = g.ledger.funds;
                reputation0 = g.reputation;
                recent0 = g.ledger.recent.ToList();
                days0 = g.ledger.days.ToList();
                totals0 = days0.Select(day => new Dictionary<LedgerCategoryDef, int>(day.totals)).ToList();
                entries0 = g.story.entries.ToList();
                firsts0 = new HashSet<string>(g.story.firsts);
                former0 = g.story.formerInmates.ToList();
                // (A release keeps its pawn as a world pawn, and so does a rearrest: a pawn the check made and kept is not left in the set once the check destroys it.)
                kept0 = new HashSet<Pawn>(Find.WorldPawns.ForcefullyKeptPawns);
                // (The keep-apart orders a letter or a request writes name pawns the check destroys: they would reach the next check's book.)
                apart0 = g.keepApart.orders.ToList();
                // (A check that runs the job pass on forced days leaves the day of the last handover on the real orders: it would hold back a real one.)
                handover0 = apart0.Select(o => o.handoverDay).ToList();
                // (A riot a check starts opens a record that would get its letter the next morning: the book is put back.)
                riots0 = g.riots.records.ToList();
                riotNext0 = g.riots.nextId;
                rearrests0 = g.rearrests.ToList();
                returning0 = g.returning.ToList();
                // (A release the check makes opens a supervision case for a pawn the check destroys: the book is put back to the cases it had.)
                supervision0 = g.supervision.cases.ToList();
                // (A release the check makes runs the cap over the live cases, which can let go of a live case's pawn - the field and the world pawn's keep: both are put back.)
                supervisionPawns0 = supervision0.Select(x => x.pawn).ToList();
                supervisionNext0 = g.supervision.nextId;
                letters0 = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
                stats0 = CopyOfCounters(g.stats);
                // (The violent deaths' notes go with the counter they explain: a check's own death - the hostage storm that kills a rioter, the death-danger scenes - was put back from the count and still named in its line.)
                violentNotes0 = ViolentDeathNotes.Count;
                today0 = CopyOfCounters(g.today);
                // (The counters' copy is shallow: the day's misconduct by level is a dictionary of its own, copied here and put back below, or every check that records a misconduct left it raised.)
                misconductByLevel0 = new Dictionary<SecurityLevelDef, int>(g.today.misconductByLevel);
                tensions0 = map.mapPawns.PrisonersOfColony.Where(x => x.Inmate() != null).Select(x => (x.Inmate(), x.Inmate().tension, x.Inmate().mainCause)).ToList();
                // (A transfer, a death, an order or a search the check makes can start a rumour on the live wing: the map's rumours are put back as they were, copies, since the check's passes change them in place.)
                pm0 = PrisonMap.Of(map);
                rumours0 = pm0?.rumours.Select(x => x.Copy()).ToList();
                // (A beating, a threat to a witness, a strike, a letter or a false rumour the check makes has an inmate ask to see the warden: the queue is put back as it was.)
                asks0 = pm0?.asks.Select(x => x.Copy()).ToList();
                // (A death or a letter gives colleagues and friends memories.)
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Concat(g.OffSite)) undo.Add(RememberMemories(p));
            }

            public void Add(Action action) => undo.Add(action);

            public IEnumerable<Letter> NewLetters => Find.LetterStack.LettersListForReading.Where(l => !letters0.Contains(l));

            public IEnumerable<ChronicleEntry> NewEntries => g.story.entries.Where(e => !entries0.Contains(e));

            public IEnumerable<LedgerEntry> NewLedgerEntries => g.ledger.recent.Where(e => !recent0.Contains(e));

            public void Dispose()
            {
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        undo[i]();
                    }
                    catch (Exception e)
                    {
                        problems0.Add("regression check cleanup threw: " + e.Message);
                    }
                }
                foreach (Letter l in NewLetters.ToList()) Find.LetterStack.RemoveLetter(l);
                g.ledger.funds = funds0;
                g.reputation = reputation0;
                g.ledger.recent.Clear();
                g.ledger.recent.AddRange(recent0);
                g.ledger.days.Clear();
                g.ledger.days.AddRange(days0);
                for (int i = 0; i < days0.Count; i++)
                {
                    days0[i].totals.Clear();
                    foreach (KeyValuePair<LedgerCategoryDef, int> kv in totals0[i]) days0[i].totals[kv.Key] = kv.Value;
                }
                g.story.entries.Clear();
                g.story.entries.AddRange(entries0);
                g.story.firsts.Clear();
                g.story.firsts.UnionWith(firsts0);
                g.story.formerInmates.Clear();
                g.story.formerInmates.AddRange(former0);
                // (Only the keeps of pawns the check made and destroyed: a real fugitive, arrival or reoffender kept during a check's real ticks keeps theirs.)
                Find.WorldPawns.ForcefullyKeptPawns.RemoveWhere(x => !kept0.Contains(x) && x.Destroyed);
                g.rearrests.Clear();
                g.rearrests.AddRange(rearrests0);
                g.returning.Clear();
                g.returning.AddRange(returning0);
                g.supervision.cases.Clear();
                g.supervision.cases.AddRange(supervision0);
                g.supervision.nextId = supervisionNext0;
                for (int i = 0; i < supervision0.Count; i++)
                {
                    SupervisionCase sc = supervision0[i];
                    // (Only a pawn the cap let go of - the case still open: a case the check's real ticks settled, or began for somebody gone, let go of its pawn for good.)
                    if (sc.pawn == null && supervisionPawns0[i] is { Destroyed: false } p && sc.state != CaseState.Resolved && !(sc.state == CaseState.Reviewing && sc.baseRisk <= 0f)) sc.pawn = p;
                    if (sc.Keeps && !sc.pawn.Destroyed) Find.WorldPawns.ForcefullyKeptPawns.Add(sc.pawn);
                }
                PutCountersBack(g.stats, stats0);
                if (ViolentDeathNotes.Count > violentNotes0) ViolentDeathNotes.RemoveRange(violentNotes0, ViolentDeathNotes.Count - violentNotes0);
                PutCountersBack(g.today, today0);
                g.today.misconductByLevel = new Dictionary<SecurityLevelDef, int>(misconductByLevel0);
                g.keepApart.orders.Clear();
                g.keepApart.orders.AddRange(apart0);
                for (int i = 0; i < apart0.Count; i++) apart0[i].handoverDay = handover0[i];
                g.keepApart.Changed();
                g.riots.records.Clear();
                g.riots.records.AddRange(riots0);
                g.riots.nextId = riotNext0;
                foreach ((CompInmate c, float tension, string cause) in tensions0) (c.tension, c.mainCause) = (tension, cause);
                if (pm0 != null && rumours0 != null) pm0.rumours = rumours0.Select(x => x.Copy()).ToList();
                if (pm0 != null && asks0 != null) pm0.asks = asks0.Select(x => x.Copy()).ToList();
                (Rumours.ForceStart, Rumours.ForceTip) = (rumourStart0, rumourTip0);
            }
        }

        /// <summary>Runs a check with its own rollback; a throw is a problem of that check, not the end of the run.</summary>
        private static void Guarded(string name, Map map, Action<Rollback> body)
        {
            using var rollback = new Rollback(map);
            int problems = problems0.Count;
            try
            {
                body(rollback);
                RPLog.Message($"[RimPrison:test] regression check \"{name}\" ran: {(problems0.Count == problems ? "ok" : "PROBLEM")}");
            }
            catch (Exception e)
            {
                problems0.Add($"{name}: threw {e}");
            }
        }

        private static int throwawayNumber = 91000;

        /// <summary>A made-up inmate (spawned at the intake and in uniform, or just generated), destroyed again by the rollback.</summary>
        private static Pawn Throwaway(Map map, Rollback r, SecurityLevelDef security = null, bool spawn = true)
        {
            Pawn p = InmateGenerator.Generate(security ?? InmateUtility.DefaultSecurity, throwawayNumber++);
            r.Add(() =>
            {
                if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
            });
            if (!spawn) return p;
            GenSpawn.Spawn(p, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            InmateGenerator.MakeInmate(p, null);
            NoMentalStateTraits(p);
            return p;
        }

        /// <summary>
        /// (A throwaway with a trait that rolls a mental state of its own - a pyromaniac's fire-starting spree, say - can have it start in the middle of a check that runs real ticks, and it drops its job: a supermax
        /// escort lost its guard 1900 ticks in. Those traits are taken off the made-up pawns.)
        /// </summary>
        private static void NoMentalStateTraits(Pawn p)
        {
            if (p.story?.traits == null) return;
            foreach (Trait t in p.story.traits.allTraits.Where(t => t.CurrentData.randomMentalState != null || t.CurrentData.forcedMentalState != null).ToList()) p.story.traits.RemoveTrait(t);
        }

        /// <summary>A new guard on site (or at home), not paid for or written up, removed again by the rollback.</summary>
        private static Pawn ThrowawayStaff(Map map, Rollback r, StaffRoleDef role, bool atHome = false)
        {
            Pawn p = StaffUtility.GenerateCandidate(role);
            GenSpawn.Spawn(p, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            StaffUtility.ApplyRole(p, role, record: false);
            NoMentalStateTraits(p);
            PrisonGame g = PrisonGame.Instance;
            r.Add(() =>
            {
                g.home.Remove(p);
                if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                StaffCareers.InvalidateLeads();
            });
            if (atHome) StaffCommute.Leave(p);
            return p;
        }

        // ---- B3: a map that is not the player's home runs no prison pass -----------------------------------------------

        /// <summary>
        /// A second map (another mod's site, a dev-mode arena) must not run the hourly prison pass: it has its own PrisonMap
        /// component, and whichever map's hour changed first would judge one-off timers (a promise, an inspection) against an empty
        /// map. The check generates a small map that is nobody's home, runs real ticks across a pass boundary and looks at the
        /// component's hour tracking, which the pass would have started.
        /// </summary>
        private static void CheckSecondMapRunsNoPrisonPass(Map home)
        {
            Guarded("second map", home, r =>
            {
                var parent = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Debug_Arena);
                // (A tile with no road: vanilla's map generation logs an error when a road has to leave a map this small and finds no exit.)
                var around = new List<PlanetTile>();
                bool NoRoad(PlanetTile t)
                {
                    Find.WorldGrid.GetTileNeighbors(t, around);
                    return around.All(n => Find.WorldGrid.GetRoadDef(t, n) == null);
                }
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, validator: NoRoad))
                {
                    problems0.Add("second map test: no world tile for a second map");
                    return;
                }
                parent.Tile = tile;
                parent.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(parent);
                Map second = null;
                r.Add(() =>
                {
                    if (second != null && Find.Maps.Contains(second)) Current.Game.DeinitAndRemoveMap(second, notifyPlayer: false);
                    if (!parent.Destroyed) parent.Destroy();
                });
                second = GetOrGenerateMapUtility.GetOrGenerateMap(parent.Tile, new IntVec3(40, 1, 40), null);
                // (An arena waits for its fighters; with none set up it would complain once.)
                var arena = parent.GetComponent<DebugArena>();
                arena.lhs = new List<Pawn>();
                arena.rhs = new List<Pawn>();
                arena.callback = _ => { };
                PrisonMap pm2 = PrisonMap.Of(second);
                PrisonMap pmHome = PrisonMap.Of(home);
                if (pm2 == null || second.IsPlayerHome || !home.IsPlayerHome)
                {
                    problems0.Add($"second map test: set-up is wrong (component {pm2 != null}, second map is a home {second.IsPlayerHome}, first is {home.IsPlayerHome})");
                    return;
                }
                int hour0 = Traverse.Create(pm2).Field("lastHour").GetValue<int>();
                int start = Find.TickManager.TicksGame;
                // Past the next multiple of 250 (the pass's beat) and on, a whole beat further.
                while (Find.TickManager.TicksGame - start < 260) Find.TickManager.DoSingleTick();
                int hour1 = Traverse.Create(pm2).Field("lastHour").GetValue<int>();
                int homeHour = Traverse.Create(pmHome).Field("lastHour").GetValue<int>();
                if (hour0 != -1 || hour1 != -1 || homeHour < 0)
                    problems0.Add($"second map test: the other map's prison pass ran: its hour tracking went {hour0} -> {hour1} (must stay -1), the home map's is {homeHour}");
            });
        }

        // ---- B4: the lone staffer on site dying must not end the game --------------------------------------------------

        /// <summary>
        /// The commute keeps one member of staff on site at night. Kill them and every spawned colonist is gone, with the rest of the
        /// staff at home (held by the prison, not on a map): vanilla would start its game-over countdown until the next shift came in.
        /// As a control, the same state with nobody at home does end the game, so the check is not vacuous.
        /// </summary>
        private static void CheckLoneStafferDyingKeepsTheGame(Map map)
        {
            Guarded("lone staffer", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn lone = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                var sentHome = new List<(Pawn pawn, IntVec3 spot, Pawn dog, IntVec3 dogSpot)>();
                foreach (Pawn other in map.mapPawns.FreeColonistsSpawned.Where(x => x != lone && x.GetComp<CompStaff>() is { role: not null }).ToList())
                {
                    Pawn dog = other.GetComp<CompStaff>().dog;
                    sentHome.Add((other, other.Position, dog, dog?.Position ?? IntVec3.Invalid));
                    StaffCommute.Leave(other);
                }
                r.Add(() =>
                {
                    Find.GameEnder.gameEnding = false;
                    foreach ((Pawn pawn, IntVec3 spot, Pawn dog, IntVec3 dogSpot) in sentHome)
                    {
                        if (g.home.Contains(pawn))
                        {
                            g.home.Remove(pawn);
                            GenSpawn.Spawn(pawn, CellFinder.StandableCellNear(spot, map, 6f), map);
                        }
                        if (dog != null && g.home.Contains(dog))
                        {
                            g.home.Remove(dog);
                            GenSpawn.Spawn(dog, CellFinder.StandableCellNear(dogSpot.IsValid ? dogSpot : spot, map, 6f), map);
                        }
                    }
                    StaffCareers.InvalidateLeads();
                });
                if (map.mapPawns.FreeColonistsSpawnedCount != 1 || sentHome.Count == 0)
                {
                    problems0.Add($"lone staffer test: set-up is wrong ({map.mapPawns.FreeColonistsSpawnedCount} colonists on site, {sentHome.Count} sent home)");
                    return;
                }
                lone.Kill(null);
                if (Find.Maps.Any(m => m.mapPawns.FreeColonistsSpawnedOrInPlayerEjectablePodsCount > 0))
                {
                    problems0.Add("lone staffer test: someone else is still a free colonist on a map");
                    return;
                }
                // Control: with the staff at home out of the picture it is vanilla's game over.
                List<Pawn> held = g.home.InnerListForReading.ToList();
                bool vanillaEnds;
                try
                {
                    foreach (Pawn p in held) g.home.Remove(p);
                    Find.GameEnder.gameEnding = false;
                    Find.GameEnder.CheckOrUpdateGameOver();
                    vanillaEnds = Find.GameEnder.gameEnding;
                }
                finally
                {
                    foreach (Pawn p in held) g.home.TryAdd(p);
                }
                Find.GameEnder.gameEnding = false;
                Find.GameEnder.CheckOrUpdateGameOver();
                bool ends = Find.GameEnder.gameEnding;
                Find.GameEnder.gameEnding = false;
                if (ends || !vanillaEnds)
                    problems0.Add($"lone staffer test: with the only staffer on site dead and {held.Count} at home the game is {(ends ? "ending" : "not ending")} (must not end), and with nobody at home it is {(vanillaEnds ? "ending" : "not ending")} (the control must end)");
                if (lone.Corpse is { Destroyed: false } corpse) corpse.Destroy();
            });
        }

        // ---- B5: an arrest in progress survives a save and load -------------------------------------------------------

        private static int arrestSuspectId = -1, arrestOfficerId = -1;

        /// <summary>Saveload plan, just before the save (after the snapshot): a member of staff is arrested, the officer on their way.</summary>
        private static void SeedArrestForReload(Map map)
        {
            Pawn suspect = StaffUtility.GenerateCandidate(RPSecurityDefOf.RP_Guard);
            GenSpawn.Spawn(suspect, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            StaffUtility.ApplyRole(suspect, RPSecurityDefOf.RP_Guard, record: false);
            if (!Detention.CallPolice(suspect))
            {
                problems0.Add("saveload: no arrest could be started");
                suspect.Destroy(DestroyMode.Vanish);
                return;
            }
            LordJob_Arrest job = suspect.GetLord()?.LordJob as LordJob_Arrest;
            Pawn officer = suspect.GetLord()?.ownedPawns.FirstOrDefault(x => x != suspect);
            bool granted = officer?.GetComp<CompStaff>() is { passLocked: true } && suspect.GetComp<CompStaff>() is { passLocked: true };
            if (job == null || officer == null || !granted) problems0.Add($"saveload: arrest started with a lord job {job != null}, an officer {officer != null}, both passing locked doors {granted}");
            (arrestSuspectId, arrestOfficerId) = (suspect.thingIDNumber, officer?.thingIDNumber ?? -1);
        }

        /// <summary>
        /// After the load (the pass flag is not saved): the arrest is still running, and a tick later the officer and the suspect may
        /// pass locked doors again; then the arrest is taken apart.
        /// </summary>
        private static void CheckArrestAfterReload(Map map)
        {
            if (arrestSuspectId < 0) return;
            Pawn suspect = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == arrestSuspectId);
            Pawn officer = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == arrestOfficerId);
            Lord lord = suspect?.GetLord();
            if (suspect == null || officer == null || lord?.LordJob is not LordJob_Arrest)
                problems0.Add($"saveload: the arrest did not survive the load (suspect {suspect != null}, officer {officer != null}, lord job {lord?.LordJob?.GetType().Name ?? "none"})");
            else
            {
                Find.TickManager.DoSingleTick();
                Find.TickManager.DoSingleTick();
                bool passes = officer.GetComp<CompStaff>() is { passLocked: true } && suspect.GetComp<CompStaff>() is { passLocked: true };
                if (!passes) problems0.Add("saveload: after the load the officer and the suspect of an arrest cannot pass locked doors");
            }
            foreach (Pawn p in new[] { suspect, officer })
                if (p != null && !p.Destroyed) p.Destroy(DestroyMode.Vanish);
            if (lord != null && map.lordManager.lords.Contains(lord)) map.lordManager.RemoveLord(lord);
            arrestSuspectId = arrestOfficerId = -1;
        }

        // ---- B2: nothing of one stay carries into the next ---------------------------------------------------------------

        /// <summary>
        /// An inmate walks out owing hours, with a shiv in the stash, a tunnel at 0.9, a restraint on, bans and a suspicion on file,
        /// then comes back as a reoffender: all of it is gone (the stash destroyed, the sentence served), staff memories stay.
        /// The dirt is put on again between the release and the return, which is what a stay left by another way would carry,
        /// so the return clears it by itself.
        /// </summary>
        private static void CheckNewStayStartsClean(Map map)
        {
            Guarded("new stay", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                // (Innocent: the release does not roll a reoffence of its own, which would keep the pawn for a return.)
                c.innocent = true;
                ThingDef shiv = DefDatabase<ThingDef>.AllDefsListForReading.First(d => ContrabandUtility.Ext(d) is { kind: ContrabandKind.Weapon });
                Building_Bed bed = pm.PrisonerBeds.FirstOrDefault();
                int now = Find.TickManager.TicksGame;
                void Dirty()
                {
                    c.stash.ClearAndDestroyContents();
                    c.stash.TryAdd(ThingMaker.MakeThing(shiv, shiv.MadeFromStuff ? GenStuff.DefaultStuffFor(shiv) : null));
                    c.pendingPunishmentHours = 12;
                    c.forcedActivity = RPDefOf.RP_Lockup;
                    c.forcedUntilTick = now + 5 * GenDate.TicksPerHour;
                    c.homeBed = bed;
                    c.tunnelProgress = 0.9f;
                    c.tunnelKnown = true;
                    c.suspected = true;
                    c.cellSearchPending = true;
                    c.yardBanUntilTick = c.shopBanUntilTick = c.visitBanUntilTick = now + GenDate.TicksPerDay;
                    c.hungerStrikeUntilTick = now + GenDate.TicksPerDay;
                    if (!p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) p.health.AddHediff(RPSecurityDefOf.RP_Restrained);
                }
                string Dirt()
                {
                    var found = new List<string>();
                    if (c.stash.Count > 0) found.Add("stash");
                    if (c.pendingPunishmentHours != 0) found.Add("owed hours");
                    if (c.forcedActivity != null || c.forcedUntilTick >= 0) found.Add("confinement");
                    if (c.homeBed != null) found.Add("home bed");
                    if (c.tunnelProgress != 0f || c.tunnelKnown) found.Add("tunnel");
                    if (c.suspected || c.cellSearchPending) found.Add("suspicion");
                    if (c.yardBanUntilTick >= 0 || c.shopBanUntilTick >= 0 || c.visitBanUntilTick >= 0) found.Add("bans");
                    if (c.hungerStrikeUntilTick >= 0) found.Add("hunger strike");
                    if (p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) found.Add("restraint");
                    return string.Join(", ", found);
                }
                StaffMemories.Note(c, 4242, "Officer Test", StaffMemories.Restrained);
                Dirty();
                if (Dirt().Split(',').Length != 9) problems0.Add("new stay test: the set-up did not take: " + Dirt());
                ReleaseSystem.Release(g, p, record: false);
                string afterRelease = Dirt();
                bool memoryKept = c.staffMemories.Any(m => m.staffId == 4242);
                if (afterRelease.Length > 0 || !memoryKept) problems0.Add($"new stay test: after release still dirty with [{afterRelease}], staff memory kept {memoryKept}");
                // Back in as a reoffender (out of the map, as the news of the rearrest finds them).
                if (p.Spawned) p.DeSpawn();
                Dirty();
                Reoffenders.Return(g, map, p, c, bed: null);
                string afterReturn = Dirt();
                if (afterReturn.Length > 0 || !c.staffMemories.Any(m => m.staffId == 4242) || c.timesAdmitted < 2)
                    problems0.Add($"new stay test: after the return still dirty with [{afterReturn}], staff memory kept {c.staffMemories.Any(m => m.staffId == 4242)}, admitted {c.timesAdmitted} times");
            });
        }

        // ---- B6 ---------------------------------------------------------------------------------------------------------

        /// <summary>A new sentence starting while a confinement runs never ends it sooner (and with none running it is the sentence's own hours).</summary>
        private static void CheckConfinementIsNotShortened(Map map)
        {
            Guarded("confinement", map, r =>
            {
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                int now = Find.TickManager.TicksGame;
                int long72 = now + 72 * GenDate.TicksPerHour;
                c.forcedActivity = RPDefOf.RP_Lockup;
                c.forcedUntilTick = long72;
                c.pendingPunishmentHours = 24;
                PunishmentUtility.Begin(p, c);
                int kept = c.forcedUntilTick;
                // A shorter confinement than the sentence: the sentence's hours.
                c.forcedActivity = RPDefOf.RP_Lockup;
                c.forcedUntilTick = now + 2 * GenDate.TicksPerHour;
                c.pendingPunishmentHours = 24;
                PunishmentUtility.Begin(p, c);
                int extended = c.forcedUntilTick;
                c.forcedActivity = null;
                c.forcedUntilTick = -1;
                c.pendingPunishmentHours = 24;
                PunishmentUtility.Begin(p, c);
                int fresh = c.forcedUntilTick;
                if (kept != long72 || extended != now + 24 * GenDate.TicksPerHour || fresh != now + 24 * GenDate.TicksPerHour)
                    problems0.Add($"confinement test: a 24 h sentence over a running 72 h confinement ended at +{(kept - now) / GenDate.TicksPerHour} h (72 expected), over a 2 h one at +{(extended - now) / GenDate.TicksPerHour} h (24), with none at +{(fresh - now) / GenDate.TicksPerHour} h (24)");
            });
        }

        // ---- B7 ---------------------------------------------------------------------------------------------------------

        /// <summary>With the list full, a weak new feeling is forgotten rather than pushing out an older, stronger one; a stronger newcomer replaces the weakest.</summary>
        private static void CheckStaffMemoryKeepsStrongest()
        {
            var c = new CompInmate();
            float[] scores = { -60f, -50f, -45f, -40f, -35f, -30f, -25f, -20f };
            for (int i = 0; i < scores.Length; i++) StaffMemories.Note(c, 100 + i, "officer " + i, scores[i]);
            StaffMemories.Note(c, 200, "frisker", StaffMemories.FriskFound);
            bool weakDropped = c.staffMemories.Count == StaffMemories.MaxPerInmate && c.staffMemories.All(m => m.staffId != 200) && c.staffMemories.Any(m => m.staffId == 107 && m.score == -20f);
            StaffMemories.Note(c, 201, "grudge", -22f);
            bool strongerKept = c.staffMemories.Count == StaffMemories.MaxPerInmate && c.staffMemories.Any(m => m.staffId == 201) && c.staffMemories.All(m => m.staffId != 107) && c.staffMemories.Any(m => m.staffId == 100 && m.score == -60f);
            RPLog.Message($"[RimPrison:test] regression check \"staff memory\" ran: {(weakDropped && strongerKept ? "ok" : "PROBLEM")}");
            if (!weakDropped || !strongerKept)
                problems0.Add($"staff memory test: weak newcomer dropped {weakDropped}, stronger newcomer replaced the weakest {strongerKept}; list: {string.Join(", ", c.staffMemories.Select(m => $"{m.staffId}:{m.score}"))}");
        }

        // ---- B10 --------------------------------------------------------------------------------------------------------

        /// <summary>A door's mode setting clears the map's reachability cache once on the next door pass, also when no inmate verdict flips (an outside or staff door, already shut to inmates, locked).</summary>
        private static void CheckDoorModeClearsReachability(Map map)
        {
            Guarded("door mode", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                CompPrisonDoor d = pm.Doors.FirstOrDefault(x => x.parent.Spawned);
                if (d == null)
                {
                    problems0.Add("door mode test: no door to lock");
                    return;
                }
                PrisonDoorMode mode0 = d.mode;
                r.Add(() =>
                {
                    d.mode = mode0;
                    pm.ForceDoorUpdate();
                });
                // Staff only and locked both shut the door to inmates: going from one to the other flips no inmate verdict.
                d.mode = PrisonDoorMode.StaffOnly;
                pm.ForceDoorUpdate();
                int before = pm.reachabilityClears;
                d.mode = PrisonDoorMode.Locked;
                pm.ForceDoorUpdate();
                int afterLock = pm.reachabilityClears;
                pm.ForceDoorUpdate();
                int afterQuiet = pm.reachabilityClears;
                d.mode = PrisonDoorMode.StaffOnly;
                pm.ForceDoorUpdate();
                int afterStaff = pm.reachabilityClears;
                if (afterLock != before + 1 || afterQuiet != afterLock || afterStaff != afterLock + 1)
                    problems0.Add($"door mode test: reachability clears {before} -> {afterLock} on StaffOnly -> Locked (one expected), {afterQuiet} with no change (none), {afterStaff} on Locked -> StaffOnly (one)");
            });
        }

        // ---- B11 --------------------------------------------------------------------------------------------------------

        /// <summary>The retirement letter works for a veteran who is off shift: "stay" pays the bonus and "send-off" retires them from home.</summary>
        private static void CheckRetirementLetterForStaffAtHome(Map map)
        {
            Guarded("retirement", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                var worker = new RetireDecision();
                ChoiceLetter_PrisonDecision Letter(Pawn staff)
                {
                    ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Retire", "test", "test", map);
                    letter.pawns.Add(staff);
                    return letter;
                }
                Pawn stays = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true), leaves = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true);
                if (!g.home.Contains(stays) || !g.home.Contains(leaves))
                {
                    problems0.Add("retirement test: the veterans did not go home");
                    return;
                }
                int funds0 = g.ledger.funds, retired0 = g.stats.staffRetired;
                worker.Choose(Letter(stays), map, 0);
                int stayPaid = funds0 - g.ledger.funds;
                bool stillThere = stays.GetComp<CompStaff>().role != null && g.home.Contains(stays);
                worker.Choose(Letter(leaves), map, 1);
                int leftPaid = funds0 - g.ledger.funds - stayPaid;
                bool gone = leaves.GetComp<CompStaff>().role == null && !g.home.Contains(leaves);
                if (stayPaid != StaffLives.RetentionBonus || !stillThere || leftPaid != StaffLives.SendOffCost || !gone || g.stats.staffRetired != retired0 + 1)
                    problems0.Add($"retirement test: staying at home paid {stayPaid} (expected {StaffLives.RetentionBonus}) and kept them {stillThere}; the send-off paid {leftPaid} (expected {StaffLives.SendOffCost}), they left {gone}, retired {g.stats.staffRetired - retired0}");
            });
        }

        // ---- B12 --------------------------------------------------------------------------------------------------------

        /// <summary>An innocent execution writes "later evidence says they were innocent" only when the truth comes out (with the fine and the letter), not when it stays hidden.</summary>
        private static void CheckInnocentExecutionLine(Map map)
        {
            Guarded("innocent execution", map, r =>
            {
                SecurityLevelDef row = DefDatabase<SecurityLevelDef>.GetNamed("RP_DeathRow");
                DeathRowExtension ext = row.GetModExtension<DeathRowExtension>();
                if (ext == null)
                {
                    problems0.Add("innocent execution test: the death row level has no extension");
                    return;
                }
                float chance0 = ext.exposedChance;
                r.Add(() => ext.exposedChance = chance0);
                (CompInmate c, float tension, string cause)[] tensions = map.mapPawns.PrisonersOfColony.Where(x => x.Inmate() != null).Select(x => (x.Inmate(), x.Inmate().tension, x.Inmate().mainCause)).ToArray();
                r.Add(() =>
                {
                    foreach ((CompInmate c, float tension, string cause) in tensions) (c.tension, c.mainCause) = (tension, cause);
                });
                string Hide(float exposure, out bool letter, out int fines)
                {
                    ext.exposedChance = exposure;
                    Pawn p = Throwaway(map, r, row);
                    p.Inmate().innocent = true;
                    string name = p.LabelShort;
                    int entries0 = PrisonGame.Instance.story.entries.Count, ledger0 = PrisonGame.Instance.ledger.recent.Count;
                    var letters0 = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
                    DeathRowSystem.Execute(p, null);
                    letter = Find.LetterStack.LettersListForReading.Any(l => !letters0.Contains(l));
                    fines = PrisonGame.Instance.ledger.recent.Skip(ledger0).Count(e => e.category == RPDefOf.RP_Fines);
                    List<ChronicleEntry> written = PrisonGame.Instance.story.entries.Skip(entries0).Where(e => e.cat == RPChronicleDefOf.RP_Chr_Death).ToList();
                    string innocentLine = "RP_Chr_ExecutedInnocent".Translate(name).Resolve(), plainLine = "RP_Chr_Executed".Translate(name).Resolve();
                    return written.Any(e => e.text == innocentLine) ? "innocent" : written.Any(e => e.text == plainLine) ? "plain" : "none";
                }
                string hidden = Hide(0f, out bool hiddenLetter, out int hiddenFines);
                string exposed = Hide(1f, out bool exposedLetter, out int exposedFines);
                if (hidden != "plain" || hiddenLetter || hiddenFines != 0 || exposed != "innocent" || !exposedLetter || exposedFines != 1)
                    problems0.Add($"innocent execution test: unexposed wrote the {hidden} line (plain expected), letter {hiddenLetter}, fines {hiddenFines}; exposed wrote the {exposed} line (innocent), letter {exposedLetter}, fines {exposedFines} (1)");
            });
        }

        // ---- B14 --------------------------------------------------------------------------------------------------------

        /// <summary>A teacher who cannot teach any more (on leave here) is not offered their own class, so the 250-tick nudge leaves them alone and a stand-in is tried.</summary>
        private static void CheckTeacherWhoCannotTeach(Map map)
        {
            Guarded("teacher who cannot teach", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                ProgramDef prog = null;
                Pawn teacher = null;
                foreach (ProgramDef candidate in DefDatabase<ProgramDef>.AllDefsListForReading)
                {
                    teacher = StaffLives.OnSite(map).FirstOrDefault(x => ProgramSystem.CanTeach(x, x.GetComp<CompStaff>().role) && candidate.TeacherRoles.Contains(x.GetComp<CompStaff>().role));
                    if (teacher == null) continue;
                    prog = candidate;
                    break;
                }
                if (teacher == null)
                {
                    problems0.Add("teacher test: no teacher on site who can teach");
                    return;
                }
                CompStaff sc = teacher.GetComp<CompStaff>();
                List<ProgramSession> sessions0 = pm.sessions;
                int leave0 = sc.leaveUntilTick;
                r.Add(() =>
                {
                    pm.sessions = sessions0;
                    sc.leaveUntilTick = leave0;
                });
                int now = Find.TickManager.TicksGame;
                var session = new ProgramSession { program = prog, startTick = now, endTick = now + GenDate.TicksPerHour, teacher = teacher };
                pm.sessions = new List<ProgramSession> { session };
                bool teaching = ProgramSystem.SessionToTeach(teacher, pm) == session;
                sc.leaveUntilTick = now + GenDate.TicksPerHour;
                bool offered = ProgramSystem.SessionToTeach(teacher, pm) != null;
                if (!teaching || offered)
                    problems0.Add($"teacher test: a teacher who can teach is offered their class {teaching} (true expected); on leave they are still offered it {offered} (false expected)");
            });
        }

        // ---- B15 --------------------------------------------------------------------------------------------------------

        /// <summary>The register of former inmates never drops a letter that is on its way: past the cap with all news due it grows; with an entry that has none, that one goes.</summary>
        private static void CheckFormerRegisterKeepsNews(Map map)
        {
            Guarded("former register", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn leaver = Throwaway(map, r, spawn: false);
                int now = Find.TickManager.TicksGame;
                List<FormerInmate> Register(int pendingFrom) => Enumerable.Range(0, FormerInmates.Max)
                    .Select(i => new FormerInmate { name = "former " + i, number = 80000 + i, newsTick = i >= pendingFrom ? now + GenDate.TicksPerDay : -1 }).ToList();
                g.story.formerInmates = Register(0);
                FormerInmate oldest = g.story.formerInmates[0];
                FormerInmates.Notify_Released(g, leaver, leaver.Inmate(), paroled: false, reoffends: false);
                bool grew = g.story.formerInmates.Count == FormerInmates.Max + 1 && g.story.formerInmates[0] == oldest;
                g.story.formerInmates = Register(1);
                FormerInmate noNews = g.story.formerInmates[0];
                FormerInmates.Notify_Released(g, leaver, leaver.Inmate(), paroled: false, reoffends: false);
                bool evicted = g.story.formerInmates.Count == FormerInmates.Max && !g.story.formerInmates.Contains(noNews);
                if (!grew || !evicted)
                    problems0.Add($"former register test: with every entry's news due the register grew without dropping the oldest {grew}; with one entry without news that one was evicted {evicted}");
            });
        }

        // ---- B16 --------------------------------------------------------------------------------------------------------

        /// <summary>Two inquiries open at once each answer in their own victim's name.</summary>
        private static void CheckOverlappingInquiries(Map map)
        {
            Guarded("inquiries", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                Pawn first = Throwaway(map, r, spawn: false), second = Throwaway(map, r, spawn: false);
                // (Two made-up names that differ.)
                for (int i = 0; i < 20 && first.LabelShort == second.LabelShort; i++) second.Name = new NameSingle("Second Victim " + i);
                string lastVictim0 = g.story.lastInquiryVictim;
                r.Add(() => g.story.lastInquiryVictim = lastVictim0);
                int inquiries0 = g.stats.inquiries;
                var letters0 = new HashSet<Letter>(Find.LetterStack.LettersListForReading);
                DeathAftermath.OpenInquiry(g, first, null, map);
                DeathAftermath.OpenInquiry(g, second, null, map);
                var opened = Find.LetterStack.LettersListForReading.Where(l => !letters0.Contains(l)).OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_Inquiry")).ToList();
                if (opened.Count != 2)
                {
                    problems0.Add($"inquiry test: {opened.Count} inquiry letters opened, two expected");
                    return;
                }
                // The first letter is answered after the second was opened.
                opened[0].decision.Worker.Choose(opened[0], map, 0);
                LedgerEntry fine = g.ledger.recent.LastOrDefault(e => e.category == RPDefOf.RP_Fines);
                string expected = "RP_InquiryNote".Translate(first.LabelShort).Resolve();
                if (fine?.note != expected)
                    problems0.Add($"inquiry test: the first inquiry fined \"{fine?.note}\", expected \"{expected}\" (the victim of the other inquiry is {second.LabelShort})");
            });
        }
    }
}
