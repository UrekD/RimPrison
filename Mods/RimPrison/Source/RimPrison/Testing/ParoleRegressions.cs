using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of the parole scene (B1-B10, and the gaps it found in <see cref="ParoleTest"/>). Each is its own method, called once
    /// from FireAllEvents, and each fails without the fix it is named for: it looks at what only the fixed code does. They work on throwaway inmates in a hearing
    /// room built for the check (<see cref="ParoleBench"/>), drive the game's own calls, seed the dice where a roll decides, and put back what they touch in a
    /// finally (cases, letters, zones, furniture, mental states, lockdown, carried pawns, real inmates' hearing state, kept pawns).
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The real inmates' hearing state, put back by the rollback (the real midnight and hourly passes run during real ticks and may touch it).</summary>
        private static void PutBackRealInmates(Map map, Rollback r)
        {
            var real = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null).Select(c => (c, c.lastHearingTick, c.want, c.paroleDenial, c.paroleDenialTick)).ToList();
            r.Add(() =>
            {
                foreach ((CompInmate c, int last, InmateWant want, string denial, int denialTick) in real)
                {
                    (c.lastHearingTick, c.paroleDenial, c.paroleDenialTick) = (last, denial, denialTick);
                    // (Only a goal a hearing gave them is taken back: the rest of their goals have lived on through the ticks, and others read them.)
                    if (c.want != want && c.want?.def == RPParoleDefOf.RP_Want_ProveWrong) c.want = want;
                }
            });
        }

        /// <summary>Whoever the check had kept in the world (for a return through the gate) is let go again.</summary>
        private static void PutBackKeptPawns(Rollback r)
        {
            List<Pawn> kept = Find.WorldPawns.ForcefullyKeptPawns.ToList();
            r.Add(() =>
            {
                foreach (Pawn x in Find.WorldPawns.ForcefullyKeptPawns.ToList())
                    if (!kept.Contains(x)) Find.WorldPawns.ForcefullyKeptPawns.Remove(x);
            });
        }

        /// <summary>
        /// A hearing room in the yard (a desk and chairs on a free five by three patch), with the parole cases, letters and real inmates' state of the map
        /// set aside and put back by the rollback, and the throwaways a check needs.
        /// </summary>
        private sealed class ParoleBench
        {
            public readonly Map map;
            public readonly PrisonMap pm;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly Rollback r;
            public readonly CrimeDef calm = DefDatabase<CrimeDef>.AllDefsListForReading.FirstOrDefault(d => !d.violent);
            public CellRect block;
            public Building desk;
            public readonly List<Building> chairs = new List<Building>();
            public IntVec3 start;
            private readonly string check;
            private readonly List<(CompInmate c, bool active)> active0;

            private ParoleBench(Map map, Rollback r, string check)
            {
                this.map = map;
                this.r = r;
                this.check = check;
                pm = PrisonMap.Of(map);
                List<ParoleCase> cases0 = g.story.paroleCases.ToList();
                r.Add(() =>
                {
                    g.story.paroleCases.Clear();
                    g.story.paroleCases.AddRange(cases0);
                });
                g.story.paroleCases.Clear();
                List<ChoiceLetter_PrisonDecision> waiting = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_Parole")).ToList();
                foreach (ChoiceLetter_PrisonDecision l in waiting) Find.LetterStack.RemoveLetter(l);
                r.Add(() =>
                {
                    foreach (ChoiceLetter_PrisonDecision l in waiting)
                        if (!Find.LetterStack.LettersListForReading.Contains(l)) Find.LetterStack.ReceiveLetter(l);
                });
                active0 = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null).Select(c => (c, c.activeFlag)).ToList();
                r.Add(() => Quiet(false));
                // (No lockdown, prison-wide or in a wing, while the check runs.)
                int lockdown0 = g.lockdownUntilTick;
                var wings0 = new Dictionary<SecurityLevelDef, int>(g.wingLockdowns);
                g.lockdownUntilTick = -1;
                g.wingLockdowns.Clear();
                r.Add(() =>
                {
                    g.lockdownUntilTick = lockdown0;
                    g.wingLockdowns.Clear();
                    foreach (KeyValuePair<SecurityLevelDef, int> kv in wings0) g.wingLockdowns[kv.Key] = kv.Value;
                });
                PutBackRealInmates(map, r);
                PutBackKeptPawns(r);
            }

            public static ParoleBench Begin(Map map, Rollback r, string check)
            {
                var bench = new ParoleBench(map, r, check);
                if (bench.calm != null) return bench;
                bench.Fail("the game has no non-violent crime to try");
                return null;
            }

            public void Fail(string what) => problems0.Add($"parole regression ({check}): {what}");

            public int Now => Find.TickManager.TicksGame;

            /// <summary>(Only the throwaways are due when the midnight pass is called by hand.)</summary>
            public void Quiet(bool on)
            {
                foreach ((CompInmate c, bool active) in active0) c.activeFlag = !on && active;
            }

            /// <summary>An inmate with a forty-day sentence, a clean sheet, no programs and tension 40 (the board's odds are about a third).</summary>
            public Pawn Hopeful()
            {
                Pawn p = Throwaway(map, r);
                CompInmate c = p.Inmate();
                c.RefreshActiveFlag();
                (c.processed, c.awaitingSearch, c.want, c.records) = (true, false, null, new List<MisconductRecord>());
                (c.sentenceDays, c.admittedTick, c.lastHearingTick, c.programsPassed, c.hoursWorked, c.tension) = (40, 0, -999999, 0, 0, 40f);
                (c.paroleDenial, c.paroleDenialTick, c.crime, c.jobWaiting, c.familyCount, c.mentorId, c.religious, c.innocent) = (null, -1, calm, false, 0, -1, false, false);
                (c.relatives, c.staffMemories, c.gang, c.forcedActivity) = (new List<Relative>(), new List<StaffMemory>(), null, null);
                c.reputations.Clear();
                c.passedPrograms.Clear();
                c.security = InmateUtility.DefaultSecurity;
                return p;
            }

            public void Place(Pawn p, IntVec3 cell)
            {
                p.pather.StopDead();
                p.Position = cell;
                p.Notify_Teleported();
            }

            /// <summary>A cell of the room with nothing built on it.</summary>
            public IntVec3 FreeRoomCell() => block.Cells.First(c => c.GetFirstBuilding(map) == null);

            public Thing Put(ThingDef def, IntVec3 cell, ThingDef stuff = null)
            {
                Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
                t.SetFactionDirect(Faction.OfPlayer);
                GenSpawn.Spawn(t, cell, map, Rot4.North);
                r.Add(() =>
                {
                    if (!t.Destroyed) t.Destroy(DestroyMode.Vanish);
                });
                return t;
            }

            /// <summary>The room: the hearing zone on a free patch of the yard, the board's desk, and this many chairs (up to four). False (with a problem noted) when the yard has no patch.</summary>
            public bool BuildRoom(int chairCount)
            {
                IntVec3 corner = IntVec3.Invalid;
                foreach (IntVec3 cell in pm.zones.CellsOf(RPDefOf.RP_Yard))
                {
                    var rect = new CellRect(cell.x, cell.z, 5, 3);
                    if (rect.Cells.All(x => x.InBounds(map) && x.Standable(map) && pm.zones.ZoneAt(x) == RPDefOf.RP_Yard && x.GetThingList(map).Count == 0))
                    {
                        corner = cell;
                        break;
                    }
                }
                if (!corner.IsValid)
                {
                    Fail("no free five by three patch in the yard to build the hearing room on");
                    return false;
                }
                block = new CellRect(corner.x, corner.z, 5, 3);
                foreach (IntVec3 cell in block.Cells)
                {
                    PrisonZoneDef was = pm.zones.ZoneAt(cell);
                    r.Add(() => pm.zones.Set(cell, was));
                    pm.zones.Set(cell, RPParoleDefOf.RP_HearingRoom);
                }
                desk = (Building)Put(RPAdminDefOf.RP_OfficeDesk, new IntVec3(corner.x + 2, 0, corner.z + 2));
                int[] chairX = { 1, 3, 0, 4 };
                for (int i = 0; i < chairCount && i < chairX.Length; i++) chairs.Add((Building)Put(ThingDefOf.DiningChair, new IntVec3(corner.x + chairX[i], 0, corner.z)));
                // (Earlier checks change doors and zones behind the reachability cache's back: the room is walked from scratch.)
                map.reachability.ClearCache();
                start = CellFinder.TryFindRandomCellNear(block.CenterCell, map, 9, x => x.Standable(map) && !block.Contains(x) && pm.zones.ZoneAt(x) == RPDefOf.RP_Yard && x.GetFirstBuilding(map) == null, out IntVec3 near) ? near : block.CenterCell;
                return true;
            }

            public ParoleCase Add(Pawn p, HearingStage stage, bool askWarden = false)
            {
                var hc = new ParoleCase { pawnId = p.thingIDNumber, name = p.LabelShort, stage = stage, scheduledTick = Now, calledTick = Now, seatedTick = Now, askWarden = askWarden };
                g.story.paroleCases.Add(hc);
                return hc;
            }

            /// <summary>A hearing held and waiting for the warden's answer, with its letter.</summary>
            public (ParoleCase hc, ChoiceLetter_PrisonDecision letter) Held(Pawn p, HearingStage stage = HearingStage.Held)
            {
                ParoleCase hc = Add(p, stage, askWarden: true);
                hc.heldTick = Now;
                ParoleScene.Assess(map, p, p.Inmate(), hc);
                ParoleHearings.AskWardenAfterSitting(g, map, p, hc);
                return (hc, Letter(p));
            }

            public ChoiceLetter_PrisonDecision Letter(Pawn who) =>
                Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Parole") && l.pawns.Contains(who));

            public void Answer(ChoiceLetter_PrisonDecision letter, int option, int seed)
            {
                letter.answered = true;
                Rand.PushState(seed);
                try { PrisonDecisions.Choose(letter, option); }
                finally { Rand.PopState(); }
                Find.LetterStack.RemoveLetter(letter);
            }

            public void DropLetters()
            {
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_Parole")).ToList())
                {
                    l.answered = true;
                    Find.LetterStack.RemoveLetter(l);
                }
            }

            /// <summary>Runs <paramref name="act"/> with the dice seeded so that its first roll at <paramref name="chance"/> is <paramref name="outcome"/>.</summary>
            public void Seeded(float chance, bool outcome, Action act)
            {
                Rand.PushState(ParoleSeedFor(chance, outcome));
                try { act(); }
                finally { Rand.PopState(); }
            }

            /// <summary>The first draw is <paramref name="outcome"/> for every chance the board can have (0.02 to 0.95): a roll that goes one way whatever the odds.</summary>
            public static int SeedForAnyOdds(bool outcome) => ParoleSeedFor(outcome ? 0.02f : 0.95f, outcome);

            public Pawn Guard(IntVec3 near)
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Place(guard, CellFinder.StandableCellNear(near, map, 3f));
                return guard;
            }
        }

        /// <summary>A way an inmate can be put out of the board's reach for a moment (a fresh inmate each time): how to start it, how to end it.</summary>
        private static List<(string name, Func<Pawn, bool> start, Action<Pawn> end)> ParoleBlocks(ParoleBench b, Pawn other)
        {
            PrisonGame g = b.g;
            int lockdown0 = g.lockdownUntilTick;
            return new List<(string, Func<Pawn, bool>, Action<Pawn>)>
            {
                ("rioting", p => p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, "test", forced: true), p => p.mindState.mentalStateHandler.Reset()),
                ("assaulting", p => p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Assaulting, null, forced: true, otherPawn: other), p => p.mindState.mentalStateHandler.Reset()),
                ("downed", p =>
                {
                    HealthUtility.DamageUntilDowned(p, allowBleedingWounds: false);
                    return p.Downed;
                }, p => { }),
                ("locked down", p =>
                {
                    g.lockdownUntilTick = b.Now + GenDate.TicksPerHour;
                    return g.LockedDown(p.Inmate());
                }, p => g.lockdownUntilTick = lockdown0),
                ("on fire", p =>
                {
                    p.TryAttachFire(0.5f, null);
                    return p.IsBurning();
                }, p => p.GetAttachment(ThingDefOf.Fire)?.Destroy()),
            };
        }

        // ---- B1: a seated hearing is concluded whatever has happened to the inmate in the chair ----------------------------------

        /// <summary>
        /// A hearing whose inmate is rioting, assaulting, downed, under lockdown or on fire when the board is ready ends without a verdict (no hearing counted, no
        /// letter, no release); a healthy one in the chair is concluded, so the check proves its own set-up.
        /// </summary>
        private static void CheckParoleSeatedHearingNeedsAnInmateInReach(Map map)
        {
            Guarded("parole B1: seated hearing", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B1 seated");
                if (b == null || !b.BuildRoom(2)) return;
                Pawn other = b.Hopeful();
                IntVec3 chair = b.FreeRoomCell();
                (string name, Func<Pawn, bool> start, Action<Pawn> end)[] cases = { ("healthy", p => true, p => { }) };
                foreach (var block in cases.Concat(ParoleBlocks(b, other)))
                {
                    b.DropLetters();
                    b.g.story.paroleCases.Clear();
                    Pawn p = b.Hopeful();
                    b.Place(p, chair);
                    ParoleCase hc = b.Add(p, HearingStage.Seated, askWarden: true);
                    hc.seatedTick = b.Now - ParoleScene.SittingTicks - ParoleScene.BoardWaitTicks - 1;
                    bool started = false;
                    try
                    {
                        started = block.start(p);
                        if (!started)
                        {
                            b.Fail($"could not put the inmate in the state '{block.name}'");
                            continue;
                        }
                        int heard0 = b.g.stats.paroleHearings;
                        ParoleScene.Rare(b.g, map);
                        bool concluded = b.g.stats.paroleHearings == heard0 + 1 && hc.stage == HearingStage.Held && b.Letter(p) != null;
                        bool endedClean = b.g.stats.paroleHearings == heard0 && !b.g.story.paroleCases.Contains(hc) && b.Letter(p) == null && !p.guest.Released;
                        if (block.name == "healthy" ? !concluded : !endedClean)
                            b.Fail($"seated and {block.name}: hearing counted {b.g.stats.paroleHearings - heard0}, case {(b.g.story.paroleCases.Contains(hc) ? hc.stage.ToString() : "gone")}, letter {b.Letter(p) != null} "
                                   + (block.name == "healthy" ? "(a healthy inmate in the chair is to be concluded)" : "(the hearing is to end without a verdict)"));
                    }
                    finally
                    {
                        if (started) block.end(p);
                        b.DropLetters();
                    }
                }
            });
        }

        /// <summary>
        /// The warden's answer (or the sitting decided at once) for an inmate who has since gone down, rioted, assaulted, been locked down, caught fire or been
        /// confined is not a verdict: the hearing ends there, and Hold (the roll, the release, the file) does not run.
        /// </summary>
        private static void CheckParoleVerdictRechecksTheInmate(Map map)
        {
            Guarded("parole B1: verdict", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B1 verdict");
                if (b == null) return;
                Pawn other = b.Hopeful();
                var states = ParoleBlocks(b, other);
                states.Add(("confined", p => (p.Inmate().forcedActivity = RPDefOf.RP_Lockup) != null, p => p.Inmate().forcedActivity = null));
                states.Insert(0, ("healthy", p => true, p => { }));
                foreach ((string name, Func<Pawn, bool> start, Action<Pawn> end) in states)
                {
                    b.DropLetters();
                    b.g.story.paroleCases.Clear();
                    Pawn p = b.Hopeful();
                    (ParoleCase hc, ChoiceLetter_PrisonDecision letter) = b.Held(p);
                    CompInmate c = p.Inmate();
                    c.lastHearingTick = 12345;
                    bool started = false;
                    try
                    {
                        started = start(p);
                        if (!started)
                        {
                            b.Fail($"could not put the inmate in the state '{name}'");
                            continue;
                        }
                        if (letter == null)
                        {
                            b.Fail("the warden's letter for the held case was not sent");
                            continue;
                        }
                        int paroled0 = b.g.stats.paroled;
                        // (The roll goes the inmate's way whatever the odds, so that a verdict that goes ahead is plain to see.)
                        b.Answer(letter, 0, ParoleBench.SeedForAnyOdds(true));
                        bool decided = c.lastHearingTick != 12345;
                        bool gone = !b.g.story.paroleCases.Contains(hc);
                        if (name == "healthy" ? !decided || !p.guest.Released : decided || p.guest.Released || b.g.stats.paroled != paroled0)
                            b.Fail($"the warden's answer for an inmate {name}: hearing decided {decided}, released {p.guest.Released}, paroled {b.g.stats.paroled - paroled0} "
                                   + (name == "healthy" ? "(a healthy inmate is to be decided on)" : "(the hearing is to end without a verdict)"));
                        if (!gone) b.Fail($"the case of an inmate {name} outlived the answer ({hc.stage})");
                    }
                    finally
                    {
                        if (started) end(p);
                    }
                }
            });
        }

        /// <summary>A letter answered for a case that is not a held one (one seated, say) is no verdict for it.</summary>
        private static void CheckParoleLetterDecidesOnlyAHeldCase(Map map)
        {
            Guarded("parole: letter and stage", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "letter for a case not held");
                if (b == null) return;
                Pawn p = b.Hopeful();
                (ParoleCase hc, ChoiceLetter_PrisonDecision letter) = b.Held(p, HearingStage.Seated);
                p.Inmate().lastHearingTick = 12345;
                if (letter == null)
                {
                    b.Fail("no letter to answer");
                    return;
                }
                b.Answer(letter, 0, ParoleBench.SeedForAnyOdds(true));
                if (!b.g.story.paroleCases.Contains(hc) || p.guest.Released || p.Inmate().lastHearingTick != 12345)
                    b.Fail($"an answered letter decided a case that is {hc.stage}: case kept {b.g.story.paroleCases.Contains(hc)}, released {p.guest.Released}");
            });
        }

        // ---- B2: a dropped hearing is decided by the roll ---------------------------------------------------------------------------

        /// <summary>
        /// A hearing dropped for want of a way in (called and never brought, queued and never called) is decided by the board's roll at once, as it was before there
        /// was a room: refused or granted, the inmate counts as heard (not due again for the usual gap), and the midnight pass does not take them again; one who cannot be
        /// released just now (in a mental state) is due again at midnight.
        /// </summary>
        private static void CheckParoleDroppedHearingFallsBackToTheRoll(Map map)
        {
            Guarded("parole B2: dropped hearing", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B2 dropped");
                if (b == null || !b.BuildRoom(1)) return;
                PrisonGame g = b.g;
                // Refused: still heard, and not taken again at midnight.
                foreach (HearingStage stage in new[] { HearingStage.Called, HearingStage.Queued })
                {
                    Pawn p = b.Hopeful();
                    CompInmate c = p.Inmate();
                    ParoleCase hc = b.Add(p, stage);
                    (hc.calledTick, hc.scheduledTick) = (b.Now - ParoleScene.CalledGiveUpTicks - 10, b.Now - ParoleScene.QueuedGiveUpTicks - 10);
                    int paroled0 = g.stats.paroled;
                    b.Seeded(ParoleSystem.ParoleChance(c), false, () => ParoleScene.Hourly(g, map, 20));
                    bool rolled = !g.story.paroleCases.Contains(hc) && Math.Abs(c.lastHearingTick - b.Now) < 5 && !p.guest.Released && g.stats.paroled == paroled0;
                    b.Quiet(true);
                    c.sentenceDays = 0;
                    ParoleSystem.DailyHearings(g, map);
                    c.sentenceDays = 40;
                    b.Quiet(false);
                    bool again = ParoleScene.CaseOf(g, p) != null;
                    if (again) g.story.paroleCases.RemoveAll(x => x.pawnId == p.thingIDNumber);
                    if (!rolled || again)
                        b.Fail($"a {stage} hearing that was dropped: decided by the roll {rolled} (last heard {(c.lastHearingTick == -999999 ? "never" : (b.Now - c.lastHearingTick) + " ticks ago")}), taken again by the midnight pass {again}");
                }
                // Granted.
                {
                    Pawn p = b.Hopeful();
                    ParoleCase hc = b.Add(p, HearingStage.Called);
                    hc.calledTick = b.Now - ParoleScene.CalledGiveUpTicks - 10;
                    int paroled0 = g.stats.paroled;
                    b.Seeded(ParoleSystem.ParoleChance(p.Inmate()), true, () => ParoleScene.Hourly(g, map, 20));
                    if (g.stats.paroled != paroled0 + 1 || !p.guest.Released || g.story.paroleCases.Contains(hc))
                        b.Fail($"a dropped hearing whose roll goes their way: paroled {g.stats.paroled - paroled0}, released {p.guest.Released}");
                }
                // In a mental state: not released; due again at midnight.
                {
                    Pawn p = b.Hopeful();
                    Pawn other = b.Hopeful();
                    ParoleCase hc = b.Add(p, HearingStage.Called);
                    hc.calledTick = b.Now - ParoleScene.CalledGiveUpTicks - 10;
                    p.Inmate().lastHearingTick = b.Now;
                    int paroled0 = g.stats.paroled;
                    bool started = p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Assaulting, null, forced: true, otherPawn: other);
                    try
                    {
                        b.Seeded(0.5f, true, () => ParoleScene.Hourly(g, map, 20));
                        if (!started || g.story.paroleCases.Contains(hc) || g.stats.paroled != paroled0 || p.guest.Released || p.Inmate().lastHearingTick != -999999)
                            b.Fail($"a dropped hearing of an inmate in a mental state: started {started}, case gone {!g.story.paroleCases.Contains(hc)}, paroled {g.stats.paroled - paroled0}, last heard {p.Inmate().lastHearingTick} (due again at midnight: -999999)");
                    }
                    finally { p.mindState.mentalStateHandler.Reset(); }
                }
            });
        }

        /// <summary>A hearing room that is gone (its chairs or desk removed) drops every case, and the board rolls for each of them.</summary>
        private static void CheckParoleHearingRoomGoneFallsBackToTheRoll(Map map)
        {
            Guarded("parole B2: room gone", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B2 room gone");
                if (b == null) return;
                PrisonGame g = b.g;
                if (HearingRooms.Capacity(map, b.pm) != 0)
                {
                    b.Fail("the map already has a working hearing room: nothing to take away");
                    return;
                }
                Pawn p = b.Hopeful();
                ParoleCase hc = b.Add(p, HearingStage.Queued);
                b.Seeded(ParoleSystem.ParoleChance(p.Inmate()), false, () => ParoleScene.Hourly(g, map, 20));
                if (g.story.paroleCases.Contains(hc) || Math.Abs(p.Inmate().lastHearingTick - b.Now) >= 5 || p.guest.Released)
                    b.Fail($"a case with the hearing room gone: case gone {!g.story.paroleCases.Contains(hc)}, last heard {(p.Inmate().lastHearingTick == -999999 ? "never" : (b.Now - p.Inmate().lastHearingTick) + " ticks ago")}");
            });
        }

        // ---- B3: letters from parolees who are rearrested ---------------------------------------------------------------------------

        /// <summary>A parolee who will be rearrested (the register knows: their news never comes) writes no letters, even to a friend of theirs; one who keeps to the terms does.</summary>
        private static void CheckParoleLettersNotFromRearrestedParolees(Map map)
        {
            Guarded("parole B3: letters", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B3 letters");
                if (b == null) return;
                PrisonGame g = b.g;
                int now = b.Now;
                Pawn reader = b.Hopeful();
                CompInmate c = reader.Inmate();
                c.mail = new List<MailRecord>();
                MailKindDef kind = DefDatabase<MailKindDef>.GetNamed("RP_Mail_Parolee");
                // (Nobody else on the register is under supervision for the moment.)
                var supervised = g.story.formerInmates.Where(e => e.paroled && ParoleLife.Supervised(e, now)).Select(e => (e, paroleEndTick: e.paroleEndTick)).ToList();
                foreach (var s in supervised) s.e.paroleEndTick = now - 1;
                try
                {
                    var rearrested = new FormerInmate { name = "Rearrestseed", paroled = true, releasedTick = now - 1000, paroleEndTick = now + 10 * GenDate.TicksPerDay, newsTick = -1, friendIds = { reader.thingIDNumber } };
                    g.story.formerInmates.Add(rearrested);
                    bool alone = kind.Worker.CanSend(reader, c);
                    var keeping = new FormerInmate { name = "Keeperseed", paroled = true, releasedTick = now - 1000, paroleEndTick = now + 10 * GenDate.TicksPerDay, newsTick = now + GenDate.TicksPerDay };
                    g.story.formerInmates.Add(keeping);
                    bool delivered = kind.Worker.Deliver(reader, c, null);
                    bool namesRearrested = r.NewEntries.Any(e => e.text.Contains("Rearrestseed")), namesKeeping = r.NewEntries.Any(e => e.text.Contains("Keeperseed"));
                    if (alone || !delivered || namesRearrested || !namesKeeping)
                        b.Fail($"letters from parolees: one rearrested alone could write {alone}, with one keeping to the terms the letter was delivered {delivered} and named the rearrested one {namesRearrested}, the other {namesKeeping}");
                }
                finally
                {
                    foreach (var s in supervised) s.e.paroleEndTick = s.paroleEndTick;
                }
            });
        }

        // ---- B4: the escort is a guard, not a flag ----------------------------------------------------------------------------

        /// <summary>
        /// An escort that stops (the guard's job ended, or the guard is no longer on it) is no escort: the flag is cleared at once when the job ends, and by the
        /// beat when the guard is found doing something else. A maximum-security inmate then waits for another escort (no hearing job), and one already walking
        /// stops walking.
        /// </summary>
        private static void CheckParoleEscortEndsWithTheEscort(Map map)
        {
            Guarded("parole B4: escort", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B4 escort");
                if (b == null || !b.BuildRoom(1)) return;
                PrisonGame g = b.g;
                Pawn p = b.Hopeful();
                CompInmate c = p.Inmate();
                c.security = DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderByDescending(d => d.dangerFactor).First();
                b.Place(p, b.start);
                ParoleCase hc = b.Add(p, HearingStage.Called);
                // A walk that goes on without the escort ends (the inmate was given the hearing job while the guard was with them; the guard's job then ends).
                Building seat = b.chairs[0];
                hc.escorted = true;
                p.jobs.StartJob(JobMaker.MakeJob(RPParoleDefOf.RP_AttendHearing, seat), JobCondition.InterruptForced);
                hc.escorted = false;
                // (The job's own driver is ticked, not the game: a real guard could take the escort job while the clock runs.)
                for (int i = 0; i < 3 && p.CurJobDef == RPParoleDefOf.RP_AttendHearing; i++) p.jobs.curDriver.DriverTick();
                bool stopped = p.CurJobDef != RPParoleDefOf.RP_AttendHearing;
                if (!stopped)
                {
                    b.Fail("a maximum-security inmate went on walking to the hearing room with no escort");
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                }
                // The guard's job is the escort: while it lasts the flag stands, and when it ends before the inmate is seated it goes.
                Pawn guard = b.Guard(p.Position);
                IntVec3 post = HearingRooms.PostFor(guard, b.pm);
                if (!post.IsValid)
                {
                    b.Fail("no post in the room for the escort");
                    return;
                }
                guard.jobs.StartJob(JobMaker.MakeJob(RPParoleDefOf.RP_EscortToHearing, p, post), JobCondition.InterruptForced);
                ParoleScene.Notify_Escorted(p, guard);
                ParoleScene.Rare(g, map);
                bool whileGoing = hc.escorted && hc.escortId == guard.thingIDNumber;
                guard.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                bool atOnce = !hc.escorted && hc.escortId == -1 && ParoleScene.JobFor(p, c, b.pm) == null;
                // Found doing something else at the beat (the guard's job did not end through its own driver).
                hc.escorted = true;
                hc.escortId = guard.thingIDNumber;
                ParoleScene.Rare(g, map);
                bool byBeat = !hc.escorted && hc.escortId == -1;
                if (!whileGoing || !atOnce || !byBeat)
                    b.Fail($"the escort and the flag: kept while the guard's job runs {whileGoing}, cleared when it ends (and no hearing job for a maximum-security inmate) {atOnce}, cleared by the beat for a guard on something else {byBeat}");
            });
        }

        /// <summary>An inmate who got up from the chair is called again, from now, and needs a new escort (the old one is not walking them).</summary>
        private static void CheckParoleSeatedThenLeftIsCalledAgainWithoutEscort(Map map)
        {
            Guarded("parole B4: left the chair", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B4 left the chair");
                if (b == null || !b.BuildRoom(1)) return;
                Pawn p = b.Hopeful();
                b.Place(p, b.start);
                ParoleCase hc = b.Add(p, HearingStage.Seated);
                hc.calledTick = b.Now - ParoleScene.CalledGiveUpTicks + 100;
                hc.escorted = true;
                hc.escortId = 4242;
                hc.escortName = "Gone Guard";
                ParoleScene.Rare(b.g, map);
                if (hc.stage != HearingStage.Called || hc.escorted || hc.escortId != -1 || b.Now - hc.calledTick > 5)
                    b.Fail($"an inmate out of their chair: {hc.stage}, escorted {hc.escorted}, called {b.Now - hc.calledTick} ticks ago (called again, unescorted, from now)");
            });
        }

        // ---- B5: what happens while the warden's letter waits ----------------------------------------------------------------------

        /// <summary>A serious incident seen after the sitting is weighed when the letter is answered: the odds the letter stated are the most, and the reason is written.</summary>
        private static void CheckParoleVerdictCountsAnIncidentSinceTheSitting(Map map)
        {
            Guarded("parole B5: incident while the letter waits", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B5 incident");
                if (b == null) return;
                Pawn p = b.Hopeful();
                CompInmate c = p.Inmate();
                (ParoleCase hc, ChoiceLetter_PrisonDecision letter) = b.Held(p);
                float stated = hc.chance;
                string record = "RP_ParoleWhy_Record".Translate().ToString();
                if (letter == null || (hc.reasons ?? "").Contains(record))
                {
                    b.Fail("the set-up is wrong: no letter, or the incident is already on the file");
                    return;
                }
                c.records.Add(new MisconductRecord { def = DefDatabase<MisconductDef>.AllDefsListForReading.First(d => d.severity >= 2), tick = b.Now, seen = true });
                b.Answer(letter, 2, ParoleBench.SeedForAnyOdds(false));
                if (hc.chance > stated - 0.1f || !(hc.reasons ?? "").Contains(record) || !(c.paroleDenial ?? "").Contains(record))
                    b.Fail($"an incident seen since the sitting: the odds stood at {stated:P0} and are {hc.chance:P0} (-15% expected), reasons [{hc.reasons}], on the file [{c.paroleDenial}]");
            });
        }

        // ---- B6: prove the board wrong ----------------------------------------------------------------------------------------------

        /// <summary>
        /// "Prove the board wrong" is not fulfilled by an incident ageing out of the board's five days, nor by the warden's opposition wearing off (it angers them after
        /// the goal has taken its measure, so the goal's baseline is the tension they had).
        /// </summary>
        private static void CheckParoleProveWrongIsNotMetByWaiting(Map map)
        {
            Guarded("parole B6: prove the board wrong", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B6 prove wrong");
                if (b == null) return;
                // An incident seen two days before the refusal: in the board's five days then, out of them three days later.
                Pawn p = b.Hopeful();
                CompInmate c = p.Inmate();
                var incident = new MisconductRecord { def = DefDatabase<MisconductDef>.AllDefsListForReading.First(d => d.severity >= 2), tick = b.Now - 2 * GenDate.TicksPerDay, seen = true };
                c.records.Add(incident);
                c.paroleDenial = "serious incidents on their record";
                c.paroleDenialTick = b.Now;
                Wants.Notify_ParoleReasons(p, c);
                InmateWant want = c.want;
                if (want?.def != RPParoleDefOf.RP_Want_ProveWrong)
                {
                    b.Fail($"the refusal did not give the goal ({want?.def?.defName})");
                    return;
                }
                incident.tick = b.Now - 5 * GenDate.TicksPerDay - 100;
                Wants.Hourly(p, c);
                if (want.status == WantStatus.Fulfilled)
                    b.Fail("the goal was met by an incident ageing out of the board's five days (+15% with no effort)");
                // The warden's opposition: the goal's baseline is the file before the anger.
                Pawn q = b.Hopeful();
                CompInmate cq = q.Inmate();
                float calmOdds = ParoleSystem.ParoleChance(cq, recentIncidents: false);
                (ParoleCase hc, ChoiceLetter_PrisonDecision letter) = b.Held(q);
                if (letter == null)
                {
                    b.Fail("no letter to answer");
                    return;
                }
                b.Answer(letter, 1, ParoleBench.SeedForAnyOdds(false));
                InmateWant wantQ = cq.want;
                if (wantQ?.def != RPParoleDefOf.RP_Want_ProveWrong || Mathf.Abs(wantQ.baseline - calmOdds) > 0.001f || cq.tension < 54f)
                    b.Fail($"opposed by the warden: the goal {wantQ?.def?.defName} took a baseline of {wantQ?.baseline:P1} for a file that stood at {calmOdds:P1} before the anger (tension {cq.tension:F0}, 55 expected)");
            });
        }

        // ---- B7: the called-alone beat ------------------------------------------------------------------------------------------------

        /// <summary>
        /// A called inmate who may go alone is turned off their regime job only when there is a hearing job to give them: not under lockdown, and not with every chair
        /// taken; a free one with a free chair is (so the check proves its own set-up).
        /// </summary>
        private static void CheckParoleCalledAloneBeatDoesNotChurn(Map map)
        {
            Guarded("parole B7: churn", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B7 churn");
                if (b == null || !b.BuildRoom(1)) return;
                PrisonGame g = b.g;
                int lockdown0 = g.lockdownUntilTick;
                Pawn blocker = b.Hopeful();
                Pawn p = b.Hopeful();
                b.Place(p, b.FreeRoomCell());
                ParoleCase hc = b.Add(p, HearingStage.Called);
                hc.calledTick = b.Now - ParoleScene.UnescortedAfterTicks;
                // (A job is known by its id: an ended job goes back to the game's pool and is handed out again as the next one, so the object proves nothing.)
                int Wait()
                {
                    Job job = JobMaker.MakeJob(JobDefOf.Wait);
                    job.expiryInterval = 600;
                    p.jobs.StartJob(job, JobCondition.InterruptForced);
                    return job.loadID;
                }
                bool Still(int id) => p.CurJob?.loadID == id;
                try
                {
                    // Under lockdown the regime gives the same job back: leave it alone.
                    g.lockdownUntilTick = b.Now + GenDate.TicksPerHour;
                    int job = Wait();
                    ParoleScene.Rare(g, map);
                    bool lockdownKept = Still(job);
                    g.lockdownUntilTick = lockdown0;
                    // Every chair taken: nowhere to send them.
                    foreach (Building chair in b.chairs) map.reservationManager.Reserve(blocker, JobMaker.MakeJob(JobDefOf.Wait), chair);
                    job = Wait();
                    ParoleScene.Rare(g, map);
                    bool chairsKept = Still(job);
                    map.reservationManager.ReleaseAllClaimedBy(blocker);
                    // A free inmate and a free chair: they are called off the job.
                    job = Wait();
                    ParoleScene.Rare(g, map);
                    bool calledOff = !Still(job);
                    string why = calledOff ? "" : $" (hearing job {ParoleScene.JobFor(p, p.Inmate(), b.pm)?.def.defName ?? "none"}, escorted {hc.escorted}, alone after {b.Now - hc.calledTick} of {ParoleScene.UnescortedAfterTicks} ticks, unreachable chair {b.chairs.Any(ch => !p.CanReach(ch, PathEndMode.OnCell, Danger.Some))}, reserved by {map.reservationManager.FirstRespectedReserver(b.chairs[0], p)?.LabelShort ?? "nobody"})";
                    if (!lockdownKept || !chairsKept || !calledOff)
                        b.Fail($"the beat for a called inmate who may go alone: job left alone under lockdown {lockdownKept}, with every chair taken {chairsKept}; ended when they are free and a chair is free {calledOff}{why}");
                }
                finally
                {
                    g.lockdownUntilTick = lockdown0;
                    map.reservationManager.ReleaseAllClaimedBy(blocker);
                    p.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                }
            });
        }

        // ---- B8: the room used by something else -------------------------------------------------------------------------------------

        /// <summary>The board's own chair (at the desk's interaction cell) and a chair on fire are no seats for an inmate.</summary>
        private static void CheckParoleSeatsAreForInmates(Map map)
        {
            Guarded("parole B8: seats", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B8 seats");
                if (b == null || !b.BuildRoom(1)) return;
                // (A wooden chair for the fire: the room's other one, made of the default stuff, may not burn.)
                Thing burning = b.Put(ThingDefOf.DiningChair, new IntVec3(b.block.minX + 3, 0, b.block.minZ), ThingDefOf.WoodLog);
                int plain = HearingRooms.Seats(map, b.pm).Count;
                IntVec3 deskChair = b.desk.def.hasInteractionCell ? b.desk.InteractionCell : IntVec3.Invalid;
                if (plain != 2 || !deskChair.IsValid || !b.block.Contains(deskChair) || deskChair.GetFirstBuilding(map) != null)
                {
                    b.Fail($"the set-up is wrong: {plain} seats of 2, the desk's interaction cell {deskChair} (in the room {deskChair.IsValid && b.block.Contains(deskChair)})");
                    return;
                }
                b.Put(ThingDefOf.DiningChair, deskChair);
                int withBoardChair = HearingRooms.Seats(map, b.pm).Count, capacity = HearingRooms.Capacity(map, b.pm);
                // (A building burns by a fire in its cell, which is spawned here and not ticked.)
                Thing fire = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Fire), burning.Position, map, Rot4.North);
                try
                {
                    int withFire = HearingRooms.Seats(map, b.pm).Count;
                    if (!burning.IsBurning() || withFire != 1)
                        b.Fail($"a chair on fire: burning {burning.IsBurning()}, {withFire} seats of 1 expected");
                }
                finally
                {
                    if (!fire.Destroyed) fire.Destroy();
                }
                if (withBoardChair != 2 || capacity != 2)
                    b.Fail($"a chair at the desk's interaction cell counted as a seat: {withBoardChair} seats and room for {capacity} (2 and 2 expected)");
            });
        }

        /// <summary>An administrator at paperwork at the room's own desk is the board (they are at that desk); and paperwork is not given at that desk while an inmate is on the way.</summary>
        private static void CheckParoleBoardAtThePaperworkDesk(Map map)
        {
            Guarded("parole B8: paperwork desk", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B8 paperwork");
                if (b == null || !b.BuildRoom(1)) return;
                // (The paperwork job ends at once when no bureaucracy project is under way, which depends on the game's own day: one is made the active one, and put back.)
                BureaucracySystem bureaucracy = b.g.bureaucracy;
                (BureaucracyDef active0, float progress0) = (bureaucracy.active, bureaucracy.progress);
                r.Add(() => (bureaucracy.active, bureaucracy.progress) = (active0, progress0));
                bureaucracy.active ??= DefDatabase<BureaucracyDef>.AllDefsListForReading.First();
                Pawn admin = ThrowawayStaff(map, r, RPParoleDefOf.RP_Administrator);
                IntVec3 at = b.desk.def.hasInteractionCell ? b.desk.InteractionCell : b.FreeRoomCell();
                b.Place(admin, at);
                var giver = new WorkGiver_Paperwork();
                bool freeToWork = giver.HasJobOnThing(admin, b.desk);
                Pawn p = b.Hopeful();
                ParoleCase hc = b.Add(p, HearingStage.Seated);
                bool heldBack = !giver.HasJobOnThing(admin, b.desk);
                // The paperwork job (two hours) that was started before the inmate came: the administrator at that desk is the board.
                admin.jobs.StartJob(JobMaker.MakeJob(RPAdminDefOf.RP_Paperwork, b.desk), JobCondition.InterruptForced);
                try
                {
                    bool board = ParoleScene.BoardReady(map, b.pm, hc, out Pawn named) && named == admin;
                    if (!freeToWork || !heldBack || !board)
                        b.Fail($"paperwork at the hearing room's desk: offered with nobody waiting {freeToWork}, held back with an inmate before the board {heldBack}; the administrator at the desk is the board {board} "
                               + $"(board named {named?.LabelShort ?? "nobody"}, a project under way {bureaucracy.active != null}, the administrator {admin.LabelShort} does {admin.CurJobDef?.defName ?? "nothing"} at {admin.Position - new IntVec3(b.block.minX, 0, b.block.minZ)} in {b.pm.zones.ZoneAt(admin.Position)?.defName ?? "no zone"}, "
                               + $"spawned {admin.Spawned}, downed {admin.Downed}, mental {admin.InMentalState}, the desk's interaction cell {(b.desk.def.hasInteractionCell ? (b.desk.InteractionCell - new IntVec3(b.block.minX, 0, b.block.minZ)).ToString() : "none")}, other administrators on the map: "
                               + $"{string.Join(", ", map.mapPawns.FreeColonistsSpawned.Where(x => x != admin && x.GetComp<CompStaff>()?.role == RPParoleDefOf.RP_Administrator).Select(x => x.LabelShort + " " + x.CurJobDef?.defName))})");
                }
                finally { admin.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false); }
            });
        }

        // ---- stability: the walk in is a walk for both ----------------------------------------------------------------------------------------

        /// <summary>
        /// A guard walks the called inmate in (the escort's job is a walk): the inmate's own job is a walk too. At a jog they came apart by the length of the way and reached the room
        /// well ahead of the escort (up to twelve cells and more in the events plan's hearing).
        /// </summary>
        private static void CheckParoleEscortedInmateWalksAtTheGuardsPace(Map map)
        {
            Guarded("parole: the escorted walk", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "escorted walk");
                if (b == null || !b.BuildRoom(1)) return;
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                b.Place(guard, b.start);
                Pawn p = b.Hopeful();
                b.Place(p, b.start);
                ParoleCase hc = b.Add(p, HearingStage.Called);
                hc.escorted = true;
                Job attend = ParoleScene.JobFor(p, p.Inmate(), b.pm);
                Job escort = new WorkGiver_EscortToHearing().JobOnThing(guard, p);
                if (attend == null || attend.def != RPParoleDefOf.RP_AttendHearing || escort == null || attend.locomotionUrgency != escort.locomotionUrgency || attend.locomotionUrgency != LocomotionUrgency.Walk)
                    b.Fail($"an inmate walked in by a guard: their job is {attend?.def.defName} at {attend?.locomotionUrgency}, the escort's {escort?.def.defName} at {escort?.locomotionUrgency} (both a walk expected)");
            });
        }

        // ---- B9: a held case and a briefly absent inmate -----------------------------------------------------------------------------

        /// <summary>
        /// A held case is kept while its inmate is carried (downed) or otherwise off the map's active list, so the warden's letter, when it is answered, decides
        /// the hearing the board held (with its reasons on the file), not a new roll on the old path.
        /// </summary>
        private static void CheckParoleHeldCaseSurvivesAnInmateBeingCarried(Map map)
        {
            Guarded("parole B9: held case", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B9 held case");
                if (b == null || !b.BuildRoom(1)) return;
                Pawn p = b.Hopeful();
                b.Place(p, b.start);
                Pawn guard = b.Guard(b.start);
                (ParoleCase hc, ChoiceLetter_PrisonDecision letter) = b.Held(p);
                if (letter == null)
                {
                    b.Fail("no letter to answer");
                    return;
                }
                bool carried = guard.carryTracker.TryStartCarry(p, 1, reserve: false) == 1;
                bool kept;
                try
                {
                    if (!carried)
                    {
                        b.Fail("the guard could not pick the inmate up");
                        return;
                    }
                    ParoleScene.Hourly(b.g, map, 20);
                    kept = b.g.story.paroleCases.Contains(hc);
                }
                finally { guard.carryTracker.TryDropCarriedThing(guard.Position, ThingPlaceMode.Near, out Thing _); }
                if (!p.Spawned)
                {
                    b.Fail("the inmate could not be put down again");
                    return;
                }
                b.Answer(letter, 2, ParoleBench.SeedForAnyOdds(false));
                string reason = "RP_ParoleWhy_NoPrograms".Translate().ToString();
                bool decidedByTheHearing = (p.Inmate().paroleDenial ?? "").Contains(reason);
                if (!kept || !decidedByTheHearing)
                    b.Fail($"a held case while its inmate was carried: kept {kept}; the answer decided the hearing with its reasons [{p.Inmate().paroleDenial}] {decidedByTheHearing}");
            });
        }

        // ---- B10: the parole report after the parole is over -------------------------------------------------------------------------

        /// <summary>News from a parolee whose term has ended is never "reports to the parole officer every week"; while the term runs it can be.</summary>
        private static void CheckParoleReportNewsOnlyWhileSupervised(Map map)
        {
            Guarded("parole B10: report news", map, r =>
            {
                ParoleBench b = ParoleBench.Begin(map, r, "B10 report news");
                if (b == null) return;
                PrisonGame g = b.g;
                int now = b.Now;
                var over = new FormerInmate { name = "Overseed", paroled = true, releasedTick = now - 9 * GenDate.TicksPerDay, paroleEndTick = now - GenDate.TicksPerDay };
                var running = new FormerInmate { name = "Runningseed", paroled = true, releasedTick = now - 1000, paroleEndTick = now + 5 * GenDate.TicksPerDay };
                bool afterTerm = false;
                for (int seed = 1; seed < 60 && !afterTerm; seed++)
                {
                    Rand.PushState(seed);
                    try { FormerInmates.Deliver(g, map, over); }
                    finally { Rand.PopState(); }
                    afterTerm = over.news == "RP_FormerNews_ParoleReport";
                }
                b.Seeded(0.5f, true, () => FormerInmates.Deliver(g, map, running));
                if (afterTerm || running.news != "RP_FormerNews_ParoleReport")
                    b.Fail($"parole report news: after the term {(afterTerm ? "came" : "never came")} (never expected), during it {running.news} (the report expected)");
            });
        }
    }
}
