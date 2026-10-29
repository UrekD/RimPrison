using System.Linq;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Plan "uitour" (windowed runs): steps through the UI on a real-time timer and logs
    /// "[RimPrison:ui] step N name" so an outside script can screenshot each step. Quits at the end.
    /// </summary>
    public static class UiTour
    {
        /// <summary>The compact toggle before the tour's staff-bar picture, and the pins the tour added (taken off again after it).</summary>
        private static bool tourBarCompact0;
        private static List<Pawn> tourBarPinned = new List<Pawn>();
        private static List<StaffBarGroup> tourBarCollapsed = new List<StaffBarGroup>();
        private static List<Pawn> tourDrafted = new List<Pawn>();
        private static List<Thing> tourPosts = new List<Thing>();
        /// <summary>The hiring subset's changes to put back: the live applicants' pool (null while nothing is changed), the funds before it and the funds it gives itself.</summary>
        private static ApplicantPool tourHiringLive;
        private static int tourHiringFunds0, tourHiringRich;
        /// <summary>The hud subset's changes to put back: the speed and the clock mode before it, and whether it has changed them.</summary>
        private static TimeSpeed tourHudSpeed0;
        private static bool tourHudTwelve0, tourHudChanged;
        /// <summary>The agreements the game had before the tour's demand letter and promise (put back at the end).</summary>
        private static StaffAgreementBook tourBook;
        /// <summary>The count subset's changes to put back: the map's live count state (null while nothing is changed) and the counts a day before it.</summary>
        private static CountState tourCountLive;
        private static int tourCountPerDay0;
        /// <summary>The speed before the count subset paused the game (its pictures are of states the clock must not move on: at the count's hour the beat starts a real count over the tour's state).</summary>
        private static TimeSpeed tourCountSpeed0;
        /// <summary>The funeral subset's dead inmate (by thing id), or -1.</summary>
        private static int tourFuneralDeadId = -1;
        /// <summary>The suppression subset's changes to put back: each inmate's suppression before the tour set it.</summary>
        private static readonly List<(CompInmate c, float suppressed)> tourSuppressed = new List<(CompInmate, float)>();

        /// <summary>The regime subset's changes to put back: the map's live feedback numbers and the game's orders (null while nothing is changed).</summary>
        private static RegimeFeedback tourRegimeFeedback0;
        private static Dictionary<SecurityLevelDef, DayOrders> tourRegimeOrders0;
        private static Dictionary<SecurityLevelDef, OrderDays> tourRegimeOrderDays0;

        /// <summary>The tools subset's changes to put back: the game's tool ledgers (null while nothing is changed) and the tool its theft put in an inmate's pocket.</summary>
        private static ToolLedgers tourToolsBook;
        private static (Pawn holder, Thing tool) tourToolsTheft;
        private static int tourToolsDecisions;

        /// <summary>The inspector subset's changes to put back: the game's inspector book and what it keeps beside it.</summary>
        private static (InspectionState book, int tick, int prep, int expose) tourInspect;

        /// <summary>The adjudication subset's changes to put back: the policy, the book and the days added it gave and the admission ticks it lowered (null while nothing is changed).</summary>
        private static AdjudicationBook tourAdjBook;
        private static bool tourAdjPolicy;
        private static List<(CompInmate c, int added, int admitted)> tourAdjAdded;

        /// <summary>The custody subset's changes to put back: the game's custody book and the count of offers (null while nothing is changed).</summary>
        private static CustodyBook tourCustodyBook;
        private static int tourCustodyOffers;

        /// <summary>The release subset's changes to put back: the game's supervision book and the sentences and plans of the inmates it set up (null while nothing is changed).</summary>
        private static SupervisionBook tourReleaseBook;
        private static List<(CompInmate c, int sentence, int added, int admitted, int lastHearing, ReleaseSupport plan)> tourReleaseInmates;
        private static TimeSpeed? tourReleaseSpeed0;
        /// <summary>The informant subset's changes to put back: the informant's file as it was (null while nothing is changed).</summary>
        private static (CompInmate c, List<ReputationDef> reps, List<ReputationDef> revealed, bool exposed, bool quit, int target, int since)? tourInformant;
        /// <summary>The riot review subset's change to put back: the game's riot book (null while nothing is changed).</summary>
        private static RiotBook tourRiotBook;
        /// <summary>The watch list subset's changes to put back: each inmate's watch-list fields as they were (null while nothing is changed).</summary>
        private static List<(Pawn p, int[] ticks, RiskTrigger ev, RiskTrigger cause, WatchAnswer answer)> tourWatch;
        /// <summary>The riot review subset's clock: the tick and the speed it found, how far it moved the clock on, and the letters whose timeouts moved with it (null while the clock is the game's).</summary>
        private static (int tick, TimeSpeed speed, int shift, List<LetterWithTimeout> letters)? tourRiotClock;

        /// <summary>
        /// The riot review subset's clock moved on (once): a tour game starts near tick 0, and its riots of up to 140 hours ago would start before the game, their ends below 0 (read as still going) and before
        /// any inmate's admission (read as gone). On to a week after the last admission, paused (an hour pass would close, answer or bill the tour's riots, or end a sentence); the letters on the stack keep their
        /// time left (the stack drops a timed-out letter on the next frame, ticking or not).
        /// </summary>
        private static void TourRiotClockOn(Map map)
        {
            if (tourRiotClock != null) return;
            int now = Find.TickManager.TicksGame;
            int admitted = map.mapPawns.PrisonersOfColonySpawned.Select(p => p.Inmate()?.admittedTick ?? -1).DefaultIfEmpty(-1).Max();
            int shift = System.Math.Max(0, admitted + 7 * GenDate.TicksPerDay - now);
            var letters = Find.LetterStack.LettersListForReading.OfType<LetterWithTimeout>().Where(l => l.TimeoutActive).ToList();
            foreach (LetterWithTimeout l in letters) l.disappearAtTick += shift;
            tourRiotClock = (now, Find.TickManager.CurTimeSpeed, shift, letters);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.TickManager.DebugSetTicksGame(now + shift);
        }

        /// <summary>The riot review subset's clock back (also the tour's end, should a step fail before its own): the tick, the speed and the letters' timeouts as they were.</summary>
        private static void TourRiotClockBack()
        {
            if (tourRiotClock is not { } clock) return;
            tourRiotClock = null;
            foreach (LetterWithTimeout l in clock.letters)
                if (l.TimeoutActive) l.disappearAtTick -= clock.shift;
            Find.TickManager.DebugSetTicksGame(clock.tick);
            Find.TickManager.CurTimeSpeed = clock.speed;
        }

        /// <summary>A riot of the tour's own on the inmates given (the first the ringleader): the first two seen, the rest not, a guard and an inmate hurt, a damage bill, a real grievance, what the prison lacked.</summary>
        private static RiotRecord TourRiot(PrisonGame game, Map map, List<Pawn> inmates, int hoursAgo)
        {
            int now = Find.TickManager.TicksGame;
            Pawn lead = inmates[0];
            var rec = new RiotRecord
            {
                id = game.riots.nextId++, mapId = map.uniqueID, level = lead.Inmate().security ?? InmateUtility.DefaultSecurity, startTick = now - hoursAgo * GenDate.TicksPerHour, lastRiotTick = now - (hoursAgo - 2) * GenDate.TicksPerHour,
                endTick = now - (hoursAgo - 3) * GenDate.TicksPerHour, ringleaderId = lead.thingIDNumber, ringleaderName = lead.LabelShort, staffHurt = new List<int> { 1 }, inmatesHurt = new List<int> { inmates[inmates.Count - 1].thingIDNumber },
                damage = 640f, grievance = NeedDefOf.Food, grievanceReal = true, needPercent = 24, missedMeals = 6, lockedHours = 10, nearestGuard = 17, dark = true, guardsRostered = 2, letterTick = now,
            };
            rec.wingInmates = map.mapPawns.PrisonersOfColonySpawned.Count(p => p.Inmate()?.security == rec.level);
            for (int i = 0; i < inmates.Count; i++) rec.participants.Add(new RiotParticipant { id = inmates[i].thingIDNumber, name = inmates[i].LabelShort, seen = i < 2 });
            rec.participants.Add(new RiotParticipant { id = 990001, name = "Zunseen", seen = false });
            // (What the wing was saying when it began: the report's line.)
            rec.rumour = new Rumour { kind = RumourKind.Supermax, level = rec.level, isTrue = false, distorted = true, reach = 0.7f, startTick = Mathf.Max(0, rec.startTick - 6 * GenDate.TicksPerHour), expireTick = now + GenDate.TicksPerDay };
            game.riots.records.Add(rec);
            return rec;
        }
        /// <summary>The per-diem subset's changes to put back: the grade history, the ledger as it was before its made-up payment, and the clock (null while nothing is changed).</summary>
        private static (List<float> history, List<float> rules, List<ReportCard> cards, int funds, List<LedgerEntry> recent, List<DayBook> days, List<Dictionary<LedgerCategoryDef, int>> totals)? tourPerDiem;
        private static (int tick, TimeSpeed speed, int shift, List<LetterWithTimeout> letters)? tourPerDiemClock;

        /// <summary>
        /// The per-diem subset's clock moved on (once) to an hour into day 8 as the Overview's title counts it (<see cref="PerDiem.FirstTick"/>), paused: a tour game starts near tick 0, and the grades count only from day 8 (never a tick below 0: only forward, and back to where it was). The
        /// letters on the stack keep their time left.
        /// </summary>
        private static void TourPerDiemClockOn()
        {
            if (tourPerDiemClock != null) return;
            int now = Find.TickManager.TicksGame;
            int shift = System.Math.Max(0, PerDiem.FirstTick + GenDate.TicksPerHour - now);
            var letters = Find.LetterStack.LettersListForReading.OfType<LetterWithTimeout>().Where(l => l.TimeoutActive).ToList();
            foreach (LetterWithTimeout l in letters) l.disappearAtTick += shift;
            tourPerDiemClock = (now, Find.TickManager.CurTimeSpeed, shift, letters);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            Find.TickManager.DebugSetTicksGame(now + shift);
        }

        /// <summary>The per-diem subset's end (also the tour's, should a step fail before its own): the grades, the ledger, the clock, the speed and the letters' timeouts as they were.</summary>
        private static void TourPerDiemBack()
        {
            PrisonGame game = PrisonGame.Instance;
            if (tourPerDiem is { } was && game != null)
            {
                game.gradeHistory.Clear();
                game.gradeHistory.AddRange(was.history);
                game.gradeRules.Clear();
                game.gradeRules.AddRange(was.rules);
                game.gradeCards.Clear();
                game.gradeCards.AddRange(was.cards);
                game.ledger.funds = was.funds;
                game.ledger.recent.Clear();
                game.ledger.recent.AddRange(was.recent);
                game.ledger.days.Clear();
                game.ledger.days.AddRange(was.days);
                for (int i = 0; i < was.days.Count; i++)
                {
                    was.days[i].totals.Clear();
                    foreach (KeyValuePair<LedgerCategoryDef, int> kv in was.totals[i]) was.days[i].totals[kv.Key] = kv.Value;
                }
            }
            tourPerDiem = null;
            if (tourPerDiemClock is { } clock)
            {
                tourPerDiemClock = null;
                foreach (LetterWithTimeout l in clock.letters)
                    if (l.TimeoutActive) l.disappearAtTick -= clock.shift;
                Find.TickManager.DebugSetTicksGame(clock.tick);
                Find.TickManager.CurTimeSpeed = clock.speed;
            }
            PrisonHUD.Invalidate();
        }

        /// <summary>The unit officer subset's changes to put back: the guard's post and patrol as they were (null while nothing is changed).</summary>
        private static (Pawn guard, SecurityLevelDef wing, int since, PrisonZoneDef prev, PrisonZoneDef patrol)? tourUnit, tourUnitHolder;
        /// <summary>The map's own rumours while the rumours tour shows made-up ones (null: not swapped).</summary>
        private static List<Rumour> tourRumours;
        private static Pawn tourRumourOrigin;
        private static (float tension, string cause)? tourRumourMood;
        /// <summary>The asks subset's changes to put back: the map's own requests and rumours, and the speed (null while nothing is swapped).</summary>
        private static (List<WardenAsk> asks, List<Rumour> rumours, TimeSpeed speed)? tourAsks;
        /// <summary>The standing subset's changes to put back: the game's orders, held letters, day's count, lately answered and budget switch (null while nothing is changed), and the letters it made.</summary>
        private static (Dictionary<string, string> orders, List<ChoiceLetter_PrisonDecision> deferred, int decisions, List<ChoiceLetter_PrisonDecision> answered, bool budget)? tourStanding;
        private static readonly List<ChoiceLetter_PrisonDecision> tourStandingLetters = new List<ChoiceLetter_PrisonDecision>();
        private static ChoiceLetter_PrisonDecision tourStandingAnswered;

        /// <summary>The amends subset's inmate and what it changed on them (null while nothing is changed), and its letter.</summary>
        private static (Pawn p, CrimeDef crime, int programs, AmendsState state, int tick)? tourAmends;
        private static ChoiceLetter_PrisonDecision tourAmendsLetter;

        /// <summary>The amends subset's end (also the tour's, should a step fail before it): the letter gone and out of the archive, the inmate's crime, programs and meeting as they were.</summary>
        private static void TourAmendsBack()
        {
            if (tourAmendsLetter != null)
            {
                tourAmendsLetter.answered = true;
                if (OnTheStack(tourAmendsLetter)) Find.LetterStack.RemoveLetter(tourAmendsLetter);
                Find.Archive.Remove(tourAmendsLetter);
                tourAmendsLetter = null;
            }
            if (tourAmends is { } was && was.p.Inmate() is { } c) (c.crime, c.programsPassed, c.amendsState, c.amendsTick) = (was.crime, was.programs, was.state, was.tick);
            tourAmends = null;
        }

        private const float StepSeconds = 25f;
        private static int step = -1;
        private static float nextAt;
        private static readonly List<(string name, System.Action action)> steps = new List<(string, System.Action)>();

        public static void Update()
        {
            if (steps.Count == 0) Build();
            // (The pictures of the prison itself are not covered by the letters that open as the clock runs.)
            if (Shots && Find.WindowStack.Windows.Any(w => w is Dialog_NodeTree))
                foreach (Dialog_NodeTree d in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) d.Close(doCloseSound: false);
            if (Time.realtimeSinceStartup < nextAt) return;
            step++;
            if (step >= steps.Count)
            {
                GroundsOverlay.ClearPreview();
                HiringBack();
                HudBack();
                CountBack();
                TourRiotClockBack();
                StandingBack();
                TourPerDiemBack();
                AsksBack();
                TourAmendsBack();
                Log.Message("[RimPrison:ui] done");
                Log.Message($"[RimPrison:test] {(AutoTest.ErrorCount == 0 ? "PASS" : "FAIL")} plan=uitour errors={AutoTest.ErrorCount} {string.Join(" | ", AutoTest.FirstErrors)}");
                Root.Shutdown();
                return;
            }
            try
            {
                steps[step].action();
                Log.Message($"[RimPrison:ui] step {step} {steps[step].name}");
            }
            catch (System.Exception e)
            {
                Log.Error($"[RimPrison] ui tour step {steps[step].name} failed: {e}");
                // (A step that pinned the parcel's picture and failed before the step that clears it: it would stay drawn.)
                GroundsOverlay.ClearPreview();
            }
            nextAt = Time.realtimeSinceStartup + StepSeconds;
        }

        private static bool OnTheStack(Letter l) => Find.LetterStack.LettersListForReading.Contains(l);

        /// <summary>The standing subset's end (also the tour's, should a step fail before it): its letters off the stack and out of the archive, the game's orders, held letters, count and budget switch as they were.</summary>
        private static void StandingBack()
        {
            foreach (ChoiceLetter_PrisonDecision l in tourStandingLetters)
            {
                if (OnTheStack(l)) Find.LetterStack.RemoveLetter(l);
                Find.Archive.Remove(l);
            }
            tourStandingLetters.Clear();
            tourStandingAnswered = null;
            OverviewTab.showDesk = false;
            PolicyTab.showStanding = false;
            PolicyTab.punishScroll = Vector2.zero;
            if (tourStanding is not { } t) return;
            PrisonGame game = PrisonGame.Instance;
            game.standingOrders.Clear();
            foreach (KeyValuePair<string, string> kv in t.orders) game.standingOrders[kv.Key] = kv.Value;
            game.story.deferredLetters = t.deferred;
            game.story.decisionsToday = t.decisions;
            game.standingAnswered.Clear();
            game.standingAnswered.AddRange(t.answered);
            StoryLetters.TestBudget = t.budget;
            tourStanding = null;
        }

        /// <summary>The hiring subset's end (also the tour's, should a step fail before it): the dialog closed, its own pool let go, the live pool and the funds as they were.</summary>
        private static void HiringBack()
        {
            if (tourHiringLive == null) return;
            PrisonGame game = PrisonGame.Instance;
            Find.WindowStack.TryRemove(typeof(Dialog_Applicants), doCloseSound: false);
            StaffUtility.CandidateUnfit = null;
            game.applicants.Clear();
            game.applicants = tourHiringLive;
            game.ledger.funds = tourHiringFunds0;
            tourHiringLive = null;
        }

        /// <summary>The hud subset's end (also the tour's, should a step fail before it): the world map left, the clock mode and the speed as they were.</summary>
        private static void HudBack()
        {
            if (!tourHudChanged) return;
            tourHudChanged = false;
            Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.None;
            Prefs.TwelveHourClockMode = tourHudTwelve0;
            Find.TickManager.CurTimeSpeed = tourHudSpeed0;
        }

        /// <summary>The asks subset's end (also the tour's, should a step fail before it): the map's own requests and rumours back, and the speed as it was.</summary>
        private static void AsksBack()
        {
            if (tourAsks is not { } kept) return;
            if (PrisonMap.Of(Find.CurrentMap) is { } pm) (pm.asks, pm.rumours) = (kept.asks, kept.rumours);
            Find.TickManager.CurTimeSpeed = kept.speed;
            tourAsks = null;
            PrisonHUD.Invalidate();
        }

        /// <summary>The count subset's end (also the tour's, should a step fail before it): the tip's box closed, the live count state and the counts a day as they were.</summary>
        private static void CountBack()
        {
            if (tourCountLive == null) return;
            Find.WindowStack.TryRemove(typeof(Dialog_MessageBox), doCloseSound: false);
            PrisonMap.Of(Find.CurrentMap).count = tourCountLive;
            PrisonGame.Instance.countsPerDay = tourCountPerDay0;
            Find.TickManager.CurTimeSpeed = tourCountSpeed0;
            tourCountLive = null;
            PrisonAlerts.Invalidate();
            PrisonHUD.Invalidate();
        }

        /// <summary>A count state of the tour's own on the map (the live one kept for <see cref="CountBack"/>), with <paramref name="perDay"/> counts a day.</summary>
        private static CountState CountFresh(Map map, int perDay)
        {
            PrisonMap pm = PrisonMap.Of(map);
            PrisonGame game = PrisonGame.Instance;
            if (tourCountLive == null)
            {
                (tourCountLive, tourCountPerDay0, tourCountSpeed0) = (pm.count, game.countsPerDay, Find.TickManager.CurTimeSpeed);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            }
            game.countsPerDay = perDay;
            pm.count = new CountState();
            PrisonAlerts.Invalidate();
            // (The panel's figures are cached for a minute of frames, and a windowed run draws few: without this the picture shows the step before's row.)
            PrisonHUD.Invalidate();
            return pm.count;
        }

        /// <summary>Plan "shots": a tour of the prison itself (not of the screens) that saves the pictures for the README, see tools/showcase-shots.sh.</summary>
        public static bool Shots;

        /// <summary>The camera on a named spot of the facility (<see cref="PrisonBuilder.TourSpot"/>), zoomed by <paramref name="zoom"/> (a factor on its own zoom).</summary>
        private static bool Look(Map map, string spot, float zoom = 1f)
        {
            if (PrisonBuilder.TourSpot(spot) is not { } s) return false;
            Find.CameraDriver.SetRootPosAndSize(PrisonBuilder.Origin(map).ToVector3Shifted() + new Vector3(s.x, 0f, s.z), s.size * zoom);
            return true;
        }

        /// <summary>
        /// Lets the game run (as the headless harness does, tick by tick) until the clock reads the given time (after <paramref name="extraDays"/> more midnights),
        /// then stands still for the picture: pawns are drawn where they are (a paused game would draw them where they were when the camera last saw them) and
        /// the letters that opened on the way are closed.
        /// </summary>
        private static void AdvanceTo(Map map, int hour, int minute = 0, int extraDays = 0)
        {
            float delta = hour + minute / 60f - GenLocalDate.HourFloat(map);
            if (delta < 0f) delta += 24f;
            int ticks = (int)(delta * GenDate.TicksPerHour) + extraDays * GenDate.TicksPerDay;
            for (int i = 0; i < ticks; i++) Find.TickManager.DoSingleTick();
            // (A power failure that the storyteller happened to send would leave the picture dark: it ends here, and the utilities switch everything back on.)
            if (PrisonGame.Instance.BlackoutActive)
            {
                PrisonGame.Instance.blackoutUntilTick = -1;
                for (int i = 0; i < 2 * GenTicks.TickRareInterval; i++) Find.TickManager.DoSingleTick();
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned) p.Drawer?.tweener?.ResetTweenedPosToRoot();
            foreach (Dialog_NodeTree d in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) d.Close(doCloseSound: false);
        }

        /// <summary>The whole hours to a tick (the hour that starts at it is this many hours on: a clock in the middle of an hour counts the rest of it).</summary>
        private static int HoursTo(int tick) => (tick - Find.TickManager.TicksGame + GenDate.TicksPerHour - 1) / GenDate.TicksPerHour;

        /// <summary>The picture list: places at the hour that shows them off (the clock only goes forward; each step is one saved picture).</summary>
        private static void BuildShots(Map map)
        {
            void Shot(string name, string spot, int hour, int minute = 0, float zoom = 1f, System.Action extra = null, int days = 0)
            {
                if (PrisonBuilder.TourSpot(spot) == null) return;
                steps.Add((name, () =>
                {
                    Find.WindowStack.TryRemove(typeof(MainTabWindow_Prison), doCloseSound: false);
                    foreach (Dialog_NodeTree d in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) d.Close(doCloseSound: false);
                    Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                    Find.Selector.ClearSelection();
                    if (hour >= 0) AdvanceTo(map, hour, minute, days);
                    extra?.Invoke();
                    Look(map, spot, zoom);
                }));
            }
            // A prison handed over with its past (RIMPRISON_TOUR=handover): the old warden's note first, as the new warden meets it.
            if (PrisonGame.Instance.handover)
                steps.Add(("handover-note", () =>
                {
                    Letter note = Find.LetterStack.LettersListForReading.FirstOrDefault(l => l.Label.ToString() == "RP_IntroLabel".Translate(PrisonGame.Instance.prisonName).ToString());
                    if (note == null) Log.Error("[RimPrison] ui tour: no welcome letter (the old warden's note)");
                    else note.OpenLetter();
                }));
            // Morning: the whole prison, a cell block and the gate while the inmates are at work; lunch in the canteen; free time in the yard; evening in the rooms;
            // then the night lockdown, from the same places and from the whole prison.
            // (The first picture is on the second morning: the inmates admitted at the start have been searched and are in their wings by then.)
            Shot("overview", "overview", 10, 15, days: 1);
            Shot("cell-block", "cells", -1);
            Shot("gate", "gate", -1);
            Shot("reception", "reception", -1);
            Shot("landing-area", "landing", -1);
            Shot("infirmary", "infirmary", -1);
            Shot("control-room", "control", -1);
            // (Lunch is at 12:00; the inmates are at the tables a few minutes later, in the penitentiary after a walk of sixty cells and more.)
            Shot("canteen-lunch", "canteen", 12, PrisonBuilder.Kind == Facility.Penitentiary ? 12 : 8);
            Shot("canteen-lunch-later", "canteen", 12, PrisonBuilder.Kind == Facility.Penitentiary ? 30 : 22);
            Shot("canteen-east-lunch", "canteen-east", 12, 32);
            Shot("workshop", "workshop", 14, 50);
            Shot("yard", "yard", 15, 50);
            Shot("visitation", "visitation", -1);
            Shot("library", "library", -1);
            // Rooms with no tour spot of their own: the camera frames the zone (its first patch) instead.
            void ZoneShot(string name, string[] zones, int hour = -1, int minute = 0, float zoom = 1f, float reach = 24f)
            {
                List<PrisonZoneDef> defs = zones.Select(z => DefDatabase<PrisonZoneDef>.GetNamedSilentFail(z)).Where(d => d != null).ToList();
                List<IntVec3> CellsNow() => defs.SelectMany(d => PrisonMap.Of(map)?.zones.CellsOf(d) ?? new List<IntVec3>()).ToList();
                if (CellsNow().Count == 0) return;
                steps.Add((name, () =>
                {
                    Find.WindowStack.TryRemove(typeof(MainTabWindow_Prison), doCloseSound: false);
                    Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                    Find.Selector.ClearSelection();
                    if (hour >= 0) AdvanceTo(map, hour, minute);
                    List<IntVec3> now = CellsNow();
                    if (now.Count == 0) return;
                    IntVec3 seed = now[0];
                    CellRect box = CellRect.FromCellList(now.Where(c => c.DistanceTo(seed) <= reach));
                    float size = Mathf.Max(8f, Mathf.Max(box.Height, box.Width * 9f / 16f) / 2f + 4f) * zoom;
                    Find.CameraDriver.SetRootPosAndSize(box.CenterVector3, size);
                }));
            }
            if (PrisonBuilder.TourSpot("chapel") != null) Shot("chapel", "chapel", -1);
            else ZoneShot("chapel", new[] { "RP_Chapel" });
            ZoneShot("supermax", new[] { "RP_SupermaxYard", "RP_WingSupermax" }, 16, 30, reach: 16f);
            ZoneShot("solitary", new[] { "RP_SolitaryZone" });
            if (DefDatabase<ThingDef>.GetNamedSilentFail("RP_OfficeDesk") is { } desk && map.listerThings.ThingsOfDef(desk).FirstOrDefault() is { } d0)
                steps.Add(("office", () =>
                {
                    Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                    Find.Selector.ClearSelection();
                    Find.CameraDriver.SetRootPosAndSize(d0.Position.ToVector3Shifted(), 9f);
                }));
            ZoneShot("morgue", new[] { "RP_Morgue" });
            ZoneShot("burial-plot", new[] { "RP_BurialPlot" });
            Shot("night-lockdown-overview", "overview", 23, 20, 1f, () => PrisonGame.Instance.StartLockdown(8));
            Shot("night-lockdown-cell-block", "cells", -1);
        }

        private static void Build()
        {
            Map map = Find.CurrentMap;
            if (Shots)
            {
                BuildShots(map);
                return;
            }
            steps.Add(("map", () =>
            {
                // Two weeks of grades for the overview's history strip (the tour starts on day one).
                PrisonGame.Instance.gradeHistory.Clear();
                foreach (float gv in new[] { 72f, 75f, 78f, 74f, 69f, 63f, 58f, 61f, 66f, 71f, 77f, 81f, 79f, 83f }) PrisonGame.Instance.gradeHistory.Add(gv);
                // Wings kept for one sex, for the Intake screen (the men in a wing now kept for women are the ones its line counts).
                foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading)
                    PrisonGame.Instance.wingGenders[sec] = sec.defName == "RP_Minimum" ? WingGender.Women : sec.defName == "RP_Medium" ? WingGender.Men : WingGender.All;
                // A state loan being paid back, for the finance screen.
                PrisonGame.Instance.story.loanOwed = 12650;
                // Something for the Intel screen to show: a paid informant, a truce between rival gangs, an appeal.
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(InmateUtility.IsActiveInmate).ToList();
                GangDef ga = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault(x => x.rivals.Count > 0);
                if (inmates.Count >= 4 && ga != null)
                {
                    CompInmate informant = inmates[0].Inmate();
                    informant.informantPaid = true;
                    informant.referral = DefDatabase<ProgramDef>.GetNamedSilentFail("RP_AngerManagement");
                    if (!informant.reputations.Contains(ReputationDefOf.RP_Snitch)) informant.reputations.Add(ReputationDefOf.RP_Snitch);
                    if (!informant.revealed.Contains(ReputationDefOf.RP_Snitch)) informant.revealed.Add(ReputationDefOf.RP_Snitch);
                    foreach ((Pawn x, GangDef gd, bool lead) in new[] { (inmates[1], ga, true), (inmates[2], ga.rivals[0], true) })
                    {
                        x.Inmate().gang = gd;
                        x.Inmate().gangKnown = true;
                        x.Inmate().gangLeader = lead;
                    }
                    PrisonGame.Instance.story.truces.Add(new GangTruce { a = ga, b = ga.rivals[0], untilTick = Find.TickManager.TicksGame + 5 * GenDate.TicksPerDay });
                    // The inmate file opened later is the first prisoner's: give them a mentor.
                    if (map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback() is { } filed && filed.Inmate() != null && inmates.FirstOrDefault(x => x != filed) is { } elder)
                        Mentors.Start(elder, filed);
                    // Remission on, the filed inmate with a day earned (the Policy row and the file chip).
                    PrisonGame.Instance.remission = true;
                    if (map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback()?.Inmate() is { } fc) (fc.cleanDays, fc.remissionEarned) = (13, 1);
                    // A smuggling ring half found (the courier and the outside supplier known, the dealer and an officer still "?"), hot enough to be worth a look, and a few marks on the map of finds.
                    inmates[2].Inmate().reputations.RemoveAll(rep => rep.behaviorClass == typeof(ReputationBehavior_Dealer));
                    inmates[2].Inmate().reputations.Add(DefDatabase<ReputationDef>.GetNamed("RP_Dealer"));
                    if (RingSystem.Form(inmates[2], map) is { } ringArc)
                    {
                        ringArc.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = inmates[3].thingIDNumber, name = inmates[3].LabelShort, known = true });
                        ringArc.ring.links.Add(new RingLink { role = RingRole.Relative, pawnId = inmates[3].thingIDNumber, name = "cousin Marta", known = true });
                        ringArc.ring.links.Add(new RingLink { role = RingRole.Officer, pawnId = 424242, name = "Nobody", known = false });
                        ringArc.ring.heat = 41f;
                    }
                    PrisonMap uiMap = PrisonMap.Of(map);
                    (int yx, int yz, _) = PrisonBuilder.TourSpot("yard").Value;
                    IntVec3 around = PrisonBuilder.Origin(map) + new IntVec3(yx, 0, yz);
                    uiMap.AddFind(around + new IntVec3(-6, 0, -4), ContrabandKind.Drug, FindSource.Stash);
                    uiMap.AddFind(around + new IntVec3(-2, 0, -2), ContrabandKind.Weapon, FindSource.Pockets);
                    uiMap.AddFind(around + new IntVec3(2, 0, -4), ContrabandKind.Phone, FindSource.Visitor);
                    uiMap.AddFind(around + new IntVec3(-3, 0, 1), ContrabandKind.Tool, FindSource.Mail);
                    // Seen violence for the danger map: two stabbings and a fight in the yard, a death and an assault on staff by the cells, a riot that started in the canteen.
                    foreach ((int dx, int dz, DangerKind dk) in new[] { (1, 1, DangerKind.Stabbing), (-4, -1, DangerKind.Stabbing), (2, -3, DangerKind.Fight), (-1, 3, DangerKind.Fight), (5, 0, DangerKind.Fight), (-6, 2, DangerKind.Death), (-5, 3, DangerKind.Assault) })
                        uiMap.AddIncident(around + new IntVec3(dx, 0, dz), dk);
                    if (PrisonBuilder.TourSpot("cells") is { } cellSpot)
                    {
                        IntVec3 cellsAt = PrisonBuilder.Origin(map) + new IntVec3(cellSpot.x, 0, cellSpot.z);
                        uiMap.AddIncident(cellsAt + new IntVec3(1, 0, 1), DangerKind.Assault);
                        uiMap.AddIncident(cellsAt + new IntVec3(3, 0, -1), DangerKind.Death);
                        uiMap.AddIncident(cellsAt + new IntVec3(-2, 0, 0), DangerKind.Riot);
                    }
                    inmates[3].Inmate().appealBacked = true;
                    inmates[3].Inmate().appealResolveTick = Find.TickManager.TicksGame + 3 * GenDate.TicksPerDay;
                    // A murder trial with its case assembled (the filed inmate is accused; two inmates came forward, one of them reported a threat) and two complaints against a guard (one waiting, one being investigated).
                    PrisonGame ug = PrisonGame.Instance;
                    int uiNow = Find.TickManager.TicksGame;
                    Pawn accused = map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback();
                    Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrFallback(GuardUtility.IsGuard);
                    if (accused != null && guard != null)
                    {
                        var trial = new MurderTrial
                        {
                            id = ug.story.nextTrialId++, killerId = accused.thingIDNumber, killerName = accused.LabelShort, victimId = 1, victimName = "Sam Archer", assembled = true, camera = true, weapon = true, priorRecord = 2,
                            chargedTick = uiNow - 2 * GenDate.TicksPerDay, assembleTick = uiNow - GenDate.TicksPerDay, trialTick = uiNow + 2 * GenDate.TicksPerDay,
                        };
                        trial.witnesses.Add(new TrialWitness { id = guard.thingIDNumber, name = guard.LabelShort, staff = true, known = true });
                        trial.witnesses.Add(new TrialWitness { id = inmates[1].thingIDNumber, name = inmates[1].LabelShort, known = true, pressure = 0.5f, pressureKnown = true });
                        trial.witnesses.Add(new TrialWitness { id = inmates[2].thingIDNumber, name = inmates[2].LabelShort, known = true });
                        ug.story.trials.Add(trial);
                        guard.GetComp<CompStaff>().complaintsUpheld = 1;
                        guard.GetComp<CompStaff>().lastUpheldTick = uiNow;
                        foreach (Pawn complainant in new[] { inmates[3], inmates[1] })
                            if (ComplaintSystem.File(ug, map, complainant, guard, complainant == inmates[3] ? ComplaintKind.Beating : ComplaintKind.Harassment, isTrue: true) is { } filedComplaint)
                            {
                                filedComplaint.dueTick = uiNow;
                                ComplaintSystem.Hourly(ug, map);
                                if (complainant == inmates[1]) ComplaintSystem.Investigate(ug, map, filedComplaint);
                            }
                    }
                }
                Find.WindowStack.TryRemove(typeof(MainTabWindow_Prison), doCloseSound: false);
                Look(map, "overview");
            }));
            steps.Add(("welcome-letter", () =>
            {
                // The new game's guide, opened as the player would from the letter stack.
                Letter welcome = Find.LetterStack.LettersListForReading.FirstOrDefault(l => l.Label.ToString() == "RP_IntroLabel".Translate(PrisonGame.Instance.prisonName).ToString());
                if (welcome == null) Log.Error("[RimPrison] ui tour: no welcome letter");
                else welcome.OpenLetter();
            }));
            // Every prison screen, through its toolbar button.
            foreach (MainTabWindow_Prison.Tab t in System.Enum.GetValues(typeof(MainTabWindow_Prison.Tab)))
            {
                MainTabWindow_Prison.Tab tab = t;
                steps.Add(("screen-" + tab.ToString().ToLowerInvariant(), () =>
                {
                    OverviewTab.showWings = false;
                    foreach (Dialog_NodeTree d in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) d.Close(doCloseSound: false);
                    MainTabWindow_Prison.Open(tab);
                }));
                // The Deployment screen's left column scrolled to its foot (the wing lockdowns, out of reach with every level open before it scrolled).
                if (tab == MainTabWindow_Prison.Tab.Deployment)
                    steps.Add(("screen-deployment-foot", () =>
                    {
                        MainTabWindow_Prison.Open(tab);
                        DeploymentTab.leftScroll = new Vector2(0f, 10000f);
                    }));
            }
            steps.Add(("inmate-inspect", () =>
            {
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback();
                if (p != null) CameraJumper.TryJumpAndSelect(p);
            }));
            steps.Add(("inmate-file", () =>
            {
                // Open the prison file tab of the selected inmate.
                if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect)
                    inspect.OpenTabType = typeof(ITab_InmateFile);
            }));
            steps.Add(("staff-record", () =>
            {
                Find.Selector.ClearSelection();
                // A guard's record: wary of an inmate who put them down, and low on morale.
                Pawn s = map.mapPawns.FreeColonistsSpawned.FirstOrFallback(x => GuardUtility.IsGuard(x))
                         ?? map.mapPawns.FreeColonistsSpawned.FirstOrFallback(x => x.GetComp<CompStaff>()?.role != null);
                if (s != null)
                {
                    if (map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback(x => x.Inmate() != null) is { } feared)
                        StaffWariness.Notify_HurtBy(s, s.GetComp<CompStaff>(), feared);
                    if (s.needs?.mood != null) s.needs.mood.CurLevel = 0.25f;
                    CameraJumper.TryJumpAndSelect(s);
                }
                if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect)
                    inspect.OpenTabType = typeof(ITab_StaffRecord);
            }));
            steps.Add(("door-inspect", () =>
            {
                Find.Selector.ClearSelection();
                CompPrisonDoor d = PrisonMap.Of(map).Doors.FirstOrFallback();
                if (d != null) CameraJumper.TryJumpAndSelect(d.parent);
            }));
            steps.Add(("coverage-overlay", () =>
            {
                Find.Selector.ClearSelection();
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                CoverageOverlay.Show = true;
                Look(map, "overview");
            }));
            steps.Add(("danger-overlay", () =>
            {
                CoverageOverlay.Show = false;
                Find.Selector.ClearSelection();
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                DangerOverlay.Show = true;
                Look(map, "yard", 0.8f);
            }));
            steps.Add(("zones-architect", () =>
            {
                DangerOverlay.Show = false;
                CoverageOverlay.Show = false;
                Find.Selector.ClearSelection();
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Architect, playSound: false);
                if (Find.WindowStack.WindowOfType<MainTabWindow_Architect>() is { } arch)
                {
                    var panels = (List<ArchitectCategoryTab>)HarmonyLib.AccessTools.Field(typeof(MainTabWindow_Architect), "desPanelsCached").GetValue(arch);
                    foreach (ArchitectCategoryTab t in panels)
                        if (t.def.defName == "RP_PrisonZones") arch.selectedDesPanel = t;
                }
                Look(map, "overview", 1.25f);
            }));
            steps.Add(("refer-menu", () =>
            {
                // The inmate list's right-click "Refer to a program" menu, as the player would open it.
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Inmates);
                Pawn p = map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback(x => x.Inmate() != null);
                if (p == null) return;
                var refer = HarmonyLib.AccessTools.Method(typeof(InmatesTab), "ReferOptions");
                if (refer?.Invoke(null, new object[] { p, p.Inmate() }) is List<FloatMenuOption> options) Find.WindowStack.Add(new FloatMenu(options));
            }));
            steps.Add(("alerts", () =>
            {
                // The timed states the alert stack reports: an inspection coming, a blackout, flu on the wing.
                Find.WindowStack.TryRemove(typeof(Dialog_NewPrison), doCloseSound: false);
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                // Vanilla shows no alerts in the first 600 ticks, and the tour never lets time run.
                Find.TutorialState.endTick = 600;
                PrisonGame g = PrisonGame.Instance;
                g.story.inspectionTick = Find.TickManager.TicksGame + 14 * GenDate.TicksPerHour;
                g.blackoutUntilTick = Find.TickManager.TicksGame + 3 * GenDate.TicksPerHour;
                g.story.outbreakUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
                Pawn sick = map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback();
                if (sick != null && !sick.health.hediffSet.HasHediff(Outbreaks.Flu)) sick.health.AddHediff(Outbreaks.Flu);
                PrisonAlerts.Invalidate();
                Look(map, "overview");
            }));
            steps.Add(("new-prison-dialog", () =>
            {
                // Which alerts the readout knows and which are up (the alert screenshot was taken a step earlier).
                if (Find.UIRoot is UIRoot_Play play)
                {
                    var all = (List<Alert>)HarmonyLib.AccessTools.Field(typeof(AlertsReadout), "AllAlerts").GetValue(play.alerts);
                    var active = (List<Alert>)HarmonyLib.AccessTools.Field(typeof(AlertsReadout), "activeAlerts").GetValue(play.alerts);
                    Log.Message($"[RimPrison:ui] alerts known: {all.Count} ({all.Count(a => a is Alert_Prison || a is Alert_PrisonCritical)} prison); active: {string.Join(", ", active.Select(a => a.GetType().Name))}");
                    foreach (Alert a in all.Where(a => a is Alert_Prison || a is Alert_PrisonCritical))
                    {
                        a.Recalculate();
                        if (a.Active) Log.Message($"[RimPrison:ui] prison alert active on recalculation: {a.GetType().Name} '{a.GetLabel()}'");
                    }
                }
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                Find.WindowStack.Add(new Dialog_NewPrison());
            }));
            steps.Add(("new-prison-dialog-penitentiary", () =>
            {
                // The same dialog with the state penitentiary chosen (its description, the plot size at least 200).
                Find.WindowStack.TryRemove(typeof(Dialog_NewPrison), doCloseSound: false);
                var dialog = new Dialog_NewPrison();
                var options = (NewPrisonOptions)HarmonyLib.AccessTools.Field(typeof(Dialog_NewPrison), "options").GetValue(dialog);
                options.scenario = DefDatabase<ScenarioDef>.GetNamed("RP_StatePenitentiary");
                options.mapSize = 200;
                Find.WindowStack.Add(dialog);
            }));
            // The state shuttle, in the air and on the ground (the tour never lets time run, so these tick the game by hand and stop at the moment shown).
            Skyfaller ShuttleInTheAir() => map.listerThings.ThingsOfDef(RPShuttleDefOf.RP_ShuttleIncoming).OfType<Skyfaller>().FirstOrDefault();
            steps.Add(("shuttle-landing", () =>
            {
                Find.WindowStack.TryRemove(typeof(Dialog_NewPrison), doCloseSound: false);
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                PrisonGame g = PrisonGame.Instance;
                ShuttleSystem.ClearSkiesForTests = true;
                SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.FirstOrDefault(x => Unlocks.IsUnlocked(x) && !x.wingOnly);
                g.intake.perDay[sec] = 4;
                g.intake.allowOvercrowding = true;
                g.supply.pending.Add(new SupplyOrder { thing = ThingDefOf.Steel, count = 140, cost = 0, arriveTick = Find.TickManager.TicksGame - 1 });
                ShuttleSystem.CallDaily(g, map);
                for (int i = 0; i < 900 && !(ShuttleInTheAir() is { ticksToImpact: <= 80 }); i++) Find.TickManager.DoSingleTick();
                g.intake.perDay[sec] = 0;
                Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                if (pad != null) Find.CameraDriver.SetRootPosAndSize(pad.Position.ToVector3Shifted() + new Vector3(2f, 0f, 3f), 16f);
                Log.Message($"[RimPrison:ui] shuttle in the air: {ShuttleInTheAir()?.ticksToImpact} ticks to impact, visit {g.shuttle.visit?.Describe()}");
            }));
            steps.Add(("shuttle-unloading", () =>
            {
                PrisonGame g = PrisonGame.Instance;
                ShuttleVisit v = g.shuttle.visit;
                // Down, two inmates off the ramp (a guard walking one to holding), the rest still aboard, the cargo not yet out.
                for (int i = 0; i < 1500 && v != null && g.shuttle.visit == v && !(v.phase == ShuttlePhase.Landed && v.steppedOff >= 2); i++) Find.TickManager.DoSingleTick();
                for (int i = 0; i < 40; i++) Find.TickManager.DoSingleTick();
                Log.Message($"[RimPrison:ui] shuttle unloading: {v?.Describe()}");
                if (v?.shuttle is { Spawned: true } landed)
                    foreach (IntVec3 c in GenAdj.OccupiedRect(landed.Position, Rot4.North, new IntVec2(9, 7)))
                        foreach (Thing t in c.GetThingList(map))
                            if (t.def.category == ThingCategory.Item) Log.Message($"[RimPrison:ui] item on the pad at {c}: {t.LabelShort} (forbidden {t.IsForbidden(Faction.OfPlayer)})");
            }));
            steps.Add(("shuttle-cargo", () =>
            {
                PrisonGame g = PrisonGame.Instance;
                ShuttleVisit v = g.shuttle.visit;
                // Everyone off, the supplies set down around the ramp and in the Deliveries zone.
                for (int i = 0; i < 1500 && v != null && g.shuttle.visit == v && !v.cargoDone; i++) Find.TickManager.DoSingleTick();
                Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                if (pad != null) Find.CameraDriver.SetRootPosAndSize(pad.Position.ToVector3Shifted() + new Vector3(6f, 0f, 3f), 22f);
                Log.Message($"[RimPrison:ui] shuttle cargo: {v?.Describe()}");
            }));
            steps.Add(("shuttle-leaving", () =>
            {
                PrisonGame g = PrisonGame.Instance;
                ShuttleVisit v = g.shuttle.visit;
                if (v != null && v.phase == ShuttlePhase.Landed) v.departTick = Find.TickManager.TicksGame + 1;
                for (int i = 0; i < 600 && v != null && g.shuttle.visit == v && v.phase != ShuttlePhase.Leaving; i++) Find.TickManager.DoSingleTick();
                for (int i = 0; i < 70; i++) Find.TickManager.DoSingleTick();
                Building pad = ShuttleSystem.Pads(map).FirstOrDefault();
                if (pad != null) Find.CameraDriver.SetRootPosAndSize(pad.Position.ToVector3Shifted() + new Vector3(2f, 0f, 3f), 16f);
                Log.Message($"[RimPrison:ui] shuttle leaving: {v?.Describe()}");
            }));
            steps.Add(("new-prison-dialog-hard", () =>
            {
                // The dialog with a harder difficulty and the new facility chosen (what the level changes, in the prison's words; the settings line).
                Find.WindowStack.TryRemove(typeof(Dialog_NewPrison), doCloseSound: false);
                var dialog = new Dialog_NewPrison();
                var options = (NewPrisonOptions)HarmonyLib.AccessTools.Field(typeof(Dialog_NewPrison), "options").GetValue(dialog);
                options.scenario = DefDatabase<ScenarioDef>.GetNamed("RP_NewPrison");
                options.difficulty = DefDatabase<DifficultyDef>.GetNamed("Hard");
                Find.WindowStack.Add(dialog);
            }));
            steps.Add(("learning-helper", () =>
            {
                // The learning helper with three of the prison's notes taught (a case opened, a ring link found, a stove billed).
                Find.WindowStack.TryRemove(typeof(Dialog_NewPrison), doCloseSound: false);
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                Concepts.Reset();
                foreach (string name in new[] { Concepts.Trial, Concepts.Ring, Concepts.Kitchen })
                {
                    ConceptDef def = ConceptDef.Named(name);
                    PlayerKnowledgeDatabase.SetKnowledge(def, 0f);
                    Concepts.Teach(name);
                }
                Prefs.AdaptiveTrainingEnabled = true;
                Look(map, "overview");
            }));
            // The grounds: the border with the land outside dimmed while the Architect is open, a parcel for sale on the map, the to-do line after a refused try, the Land tab after buying one.
            void LookAtGrounds(float margin = 8f)
            {
                CellRect rect = PrisonGrounds.Of(map).Rect;
                Find.CameraDriver.SetRootPosAndSize(rect.CenterCell.ToVector3Shifted(), rect.Height / 2f + margin);
            }
            steps.Add(("grounds-boundary", () =>
            {
                Find.WindowStack.TryRemove(typeof(Dialog_NewPrison), doCloseSound: false);
                Find.Selector.ClearSelection();
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Architect, playSound: false);
                LookAtGrounds();
            }));
            steps.Add(("grounds-parcel", () =>
            {
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                PrisonGrounds g = PrisonGrounds.Of(map);
                GroundsOverlay.ShowPreview(g.ParcelFor(GroundsSide.East).cells, pin: true);
                LookAtGrounds(14f);
            }));
            steps.Add(("grounds-todo", () =>
            {
                GroundsOverlay.ClearPreview();
                PrisonGrounds.Of(map).Notify_TriedOutside();
                Look(map, "overview");
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Overview);
            }));
            steps.Add(("grounds-buy", () =>
            {
                // (Bought a step ahead of its picture: the message that says so has gone by the time the Land tab is photographed.)
                PrisonGame g = PrisonGame.Instance;
                g.ledger.funds = System.Math.Max(g.ledger.funds, 30000);
                PrisonGrounds.Of(map).TryBuy(GroundsSide.North);
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                LookAtGrounds(14f);
            }));
            steps.Add(("grounds-bought", () => MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Land)));
            steps.Add(("overview-to-do", () =>
            {
                // The overview's to-do band with something to fix: no holding room, no morgue, a body on the floor.
                PrisonMap pm = PrisonMap.Of(map);
                foreach (PrisonZoneDef z in new[] { RPDefOf.RP_Holding, RPMorgueDefOf.RP_Morgue, RPDefOf.RP_Shower })
                    foreach (IntVec3 c in pm.zones.CellsOf(z).ToList()) pm.zones.Set(c, null);
                PrisonAlerts.Invalidate();
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Overview);
            }));
            steps.Add(("storyteller-in-game", () =>
            {
                // The in-game storyteller page: the Board is listed and its difficulty buttons are drawn (it used to be hidden, and with it the difficulty pane).
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                Find.WindowStack.Add(new Page_SelectStorytellerInGame());
            }));
            steps.Add(("storyteller-closed", () => Find.WindowStack.TryRemove(typeof(Page_SelectStorytellerInGame), doCloseSound: false)));
            // The warden on the floor, the psych wing, gang ranks and the way out, vocational training: their gizmos, menus, rows and letters.
            void Clear()
            {
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                foreach (Dialog_NodeTree d in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) d.Close(doCloseSound: false);
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                Find.Selector.ClearSelection();
            }
            steps.Add(("feature-warden-gizmos", () =>
            {
                Clear();
                if (WardenSystem.InPost(map) is { } warden) CameraJumper.TryJumpAndSelect(warden);
                else Log.Error("[RimPrison] ui tour: no warden in post");
            }));
            steps.Add(("feature-warden-word", () =>
            {
                // The "word with an inmate" menu, as the gizmo opens it once an inmate is picked.
                if (WardenSystem.InPost(map) is not { } warden || map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback(WardenGizmos.CanTalkTo) is not { } inmate) return;
                Find.CameraDriver.SetRootPosAndSize(inmate.DrawPos, 12f);
                var options = WardenSystem.WordOptions(warden, inmate).Select(o => new FloatMenuOption(o.disabled != null ? o.label + " (" + o.disabled + ")" : o.label, o.disabled != null ? null : () => { })).ToList();
                Find.WindowStack.Add(new FloatMenu(options, inmate.LabelShort));
            }));
            steps.Add(("feature-psych-regime", () =>
            {
                Clear();
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Regime);
            }));
            steps.Add(("feature-psych-rooms", () =>
            {
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Rooms);
                // Scrolled to the wings (the psych wing's row is among them): past the header and the rooms above.
                int rooms = DefDatabase<DesignationCategoryDef>.GetNamed("RP_PrisonZones").AllResolvedDesignators.OfType<Designator_PrisonZone>().Count(d => d.zone != null && d.zone.security == null);
                foreach (MainTabWindow_Prison w in Find.WindowStack.Windows.OfType<MainTabWindow_Prison>())
                    HarmonyLib.AccessTools.Field(typeof(MainTabWindow_Prison), "scroll").SetValue(w, new Vector2(0f, 30f + rooms * 44f));
            }));
            // (The feature steps are made for the penitentiary, which builds the psych wing and fills the gangs; in the whole tour of the inherited facility what is not there is noted, not an error.)
            void Missing(string what)
            {
                if (Find.Scenario?.AllParts.OfType<ScenPart_PrisonStart>().FirstOrDefault()?.facility == Facility.Penitentiary) Log.Error("[RimPrison] ui tour: " + what);
                else RPLog.Message($"[RimPrison] ui tour: {what} (not the penitentiary: the step shows what there is)");
            }
            steps.Add(("feature-padded-cell", () =>
            {
                Clear();
                if (map.listerThings.ThingsOfDef(RPPsychWingDefOf.RP_PaddedBed).FirstOrDefault() is { } padded)
                {
                    Find.CameraDriver.SetRootPosAndSize(padded.Position.ToVector3Shifted(), 9f);
                    Find.Selector.Select(padded, playSound: false);
                }
                else Missing("no padded bed");
            }));
            steps.Add(("feature-gang-chip", () =>
            {
                // A gang with its leader and a lieutenant, on the Inmates screen.
                Clear();
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(InmateUtility.IsActiveInmate).ToList();
                if (inmates.FirstOrDefault(x => x.Inmate().gang != null && x.Inmate().gangLeader) is { } leader
                    && inmates.FirstOrDefault(x => x != leader && x.Inmate().gang == null) is { } lieutenant)
                {
                    CompInmate lc = lieutenant.Inmate();
                    (lc.gang, lc.gangKnown) = (leader.Inmate().gang, true);
                    GangRanks.SetRank(lc, GangRank.Lieutenant);
                }
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Inmates);
            }));
            steps.Add(("feature-gang-letter", () =>
            {
                // A member who renounced the gang: the green light, and the decision letter it sends.
                Clear();
                Pawn leaver = map.mapPawns.PrisonersOfColonySpawned.Where(InmateUtility.IsActiveInmate).FirstOrDefault(x => x.Inmate().gang != null && !x.Inmate().gangLeader);
                if (leaver == null) { Missing("no gang member to leave"); return; }
                int before = Find.LetterStack.LettersListForReading.Count;
                GangExit.Renounce(leaver, leaver.Inmate());
                if (Find.LetterStack.LettersListForReading.Count > before) Find.LetterStack.LettersListForReading.Last().OpenLetter();
                else Missing($"no letter after {leaver.LabelShort} left the gang ({GangExit.Describe(leaver.Inmate())})");
            }));
            steps.Add(("feature-programs", () =>
            {
                Clear();
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Programs);
            }));
            steps.Add(("feature-staff-bar", () =>
            {
                // The colonist bar grouped by role (the warden, security, medical, programs, upkeep), a label under each group, the prison panel's bar button below it; the armed
                // guards drafted, so that their weapons show under the portraits and the security label goes below them (undrafted at the next step).
                Clear();
                tourDrafted = StaffCommute.AllStaff(map).Where(x => x.Spawned && x.drafter != null && !x.Drafted && x.equipment?.Primary != null && StaffBar.GroupOf(x) == StaffBarGroup.Security).ToList();
                foreach (Pawn d in tourDrafted) d.drafter.Drafted = true;
                Find.ColonistBar.MarkColonistsDirty();
            }));
            steps.Add(("feature-staff-bar-compact", () =>
            {
                // The compact bar: the warden and two pinned (a guard and a cook), with the panel's button saying so; put back after the picture.
                Clear();
                foreach (Pawn d in tourDrafted) if (d.Spawned && d.drafter != null) d.drafter.Drafted = false;
                tourDrafted.Clear();
                PrisonGame game = PrisonGame.Instance;
                (tourBarCompact0, tourBarPinned) = (game.staffBarCompact, new List<Pawn>());
                foreach (string role in new[] { "RP_Guard", "RP_Cook" })
                    if (StaffCommute.AllStaff(map).FirstOrDefault(x => x.Spawned && x.GetComp<CompStaff>()?.role?.defName == role && !StaffBar.IsPinned(x)) is { } pin)
                    {
                        StaffBar.SetPinned(game, pin, true);
                        tourBarPinned.Add(pin);
                    }
                StaffBar.SetCompact(game, true);
            }));
            steps.Add(("feature-staff-bar-collapsed", () =>
            {
                // Security and upkeep collapsed by their labels (the two pins stay in them), the others open; the compact toggle back off for it.
                PrisonGame game = PrisonGame.Instance;
                StaffBar.SetCompact(game, false);
                tourBarCollapsed = new List<StaffBarGroup>();
                foreach (StaffBarGroup c in new[] { StaffBarGroup.Security, StaffBarGroup.Upkeep })
                    if (!StaffBar.IsCollapsed(game, c))
                    {
                        StaffBar.SetCollapsed(game, c, true);
                        tourBarCollapsed.Add(c);
                    }
            }));
            steps.Add(("feature-staff-bar-back", () =>
            {
                // (Back as the game had it: only the tour's own pins and collapses come off.)
                PrisonGame game = PrisonGame.Instance;
                foreach (Pawn pin in tourBarPinned) StaffBar.SetPinned(game, pin, false);
                foreach (StaffBarGroup c in tourBarCollapsed) StaffBar.SetCollapsed(game, c, false);
                StaffBar.SetCompact(game, tourBarCompact0);
            }));
            steps.Add(("desk-wings", () =>
            {
                // The overview with its wing table open in place of the needs (the desk: the grade's drivers in the tiles' tips, the kitchen line, the panel's tense and letters counts).
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Overview);
                OverviewTab.showWings = true;
            }));
            steps.Add(("desk-wings-back", () => OverviewTab.showWings = false));
            steps.Add(("regime-strip", () =>
            {
                // The Regime tab with yesterday's numbers under each row (made up: the game has no yesterday on the tour's first day; the first level has no numbers for a few hours, as after a load).
                Clear();
                PrisonGame game = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                tourRegimeFeedback0 ??= pm.regime;
                pm.regime = new RegimeFeedback();
                int row = 0;
                foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(x => x.uiOrder).Where(Unlocks.IsUnlocked))
                {
                    var day = new RegimeDay();
                    for (int h = 0; h < 24; h++)
                    {
                        RegimeActivityDef act = game.RegimeFor(sec).At(h);
                        day.ran[h] = act;
                        day.sample[h] = (byte)(row == 0 && h >= 2 && h <= 4 ? 0 : 10 + 2 * row);
                        day.share[h] = (byte)(act.lockup ? 98 : act == RPRegimeDefOf.RP_Eat ? 71 : act == RPLaborDefOf.RP_Work ? 84 : 55 + (h * 7 + row * 13) % 40);
                        day.incidents[h] = (byte)(act == RPDefOf.RP_FreeTime && (h + row) % 3 == 0 ? 1 + (h + row) % 2 : 0);
                        day.missed[h] = (byte)(act == RPRegimeDefOf.RP_Eat && (h + row) % 2 == 0 ? 9 - row : 0);
                    }
                    pm.regime.Yesterday[sec] = day;
                    row++;
                }
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Regime);
            }));
            steps.Add(("regime-strip-tip", () =>
            {
                // (A tooltip needs the mouse over its cell, which a tour cannot do: its text is shown in a box, as the strip gives it - an hour with numbers, and one with none.)
                SecurityLevelDef sec = DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(x => x.uiOrder).First(Unlocks.IsUnlocked);
                RegimeFeedback fb = PrisonMap.Of(map).regime;
                int eat = Enumerable.Range(0, 24).FirstOrDefault(h => fb.Yesterday.TryGetValue(sec, out RegimeDay d) && d.Has(h) && d.missed[h] > 0);
                Find.WindowStack.Add(new Dialog_MessageBox(fb.Tip(sec, eat) + "\n\n" + fb.Tip(sec, 3), title: "The strip's hours, hovered"));
            }));
            steps.Add(("regime-orders", () =>
            {
                // Today's orders: the yard called off and an early lockup for the first level, a second shower for the next (the marks on the hours the orders changed, the buttons lit).
                Find.WindowStack.TryRemove(typeof(Dialog_MessageBox), doCloseSound: false);
                PrisonGame game = PrisonGame.Instance;
                tourRegimeOrders0 ??= game.dayOrders;
                tourRegimeOrderDays0 ??= game.orderDays;
                game.dayOrders = new Dictionary<SecurityLevelDef, DayOrders>();
                game.orderDays = new Dictionary<SecurityLevelDef, OrderDays>();
                List<SecurityLevelDef> levels = DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(x => x.uiOrder).Where(Unlocks.IsUnlocked).ToList();
                RegimeOrders.Give(game, map, levels[0], DayOrder.YardOff);
                RegimeOrders.Give(game, map, levels[0], DayOrder.EarlyLockup);
                if (levels.Count > 1) RegimeOrders.Give(game, map, levels[1], DayOrder.DoubleShower);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Regime);
            }));
            steps.Add(("regime-orders-menu", () =>
            {
                // The "Today..." button's menu for the second level: the three orders in full words, one of them in force.
                PrisonGame game = PrisonGame.Instance;
                SecurityLevelDef level = DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(x => x.uiOrder).Where(Unlocks.IsUnlocked).Skip(1).FirstOrDefault() ?? InmateUtility.DefaultSecurity;
                RegimeTab.OpenOrdersMenu(game, map, level);
            }));
            steps.Add(("regime-back", () =>
            {
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                Clear();
                PrisonGame game = PrisonGame.Instance;
                var levelsOrdered = game.dayOrders.Keys.ToList();
                var placed = levelsOrdered.Select(l => (l, RegimeOrders.PlacedBefore(l))).ToList();
                if (tourRegimeOrderDays0 != null) game.orderDays = tourRegimeOrderDays0;
                tourRegimeOrderDays0 = null;
                if (tourRegimeOrders0 != null) game.dayOrders = tourRegimeOrders0;
                if (tourRegimeFeedback0 != null) PrisonMap.Of(map).regime = tourRegimeFeedback0;
                tourRegimeOrders0 = null;
                tourRegimeFeedback0 = null;
                foreach ((SecurityLevelDef level, var was) in placed) RegimeOrders.Changed(level, was);
            }));
            // RIMPRISON_TOUR=regime: the Regime tab with the strip under each row and its tooltip, and today's orders.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "regime") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("regime-"));
            // RIMPRISON_TOUR=desk: the overview, with and without the wing table.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "desk") steps.RemoveAll(x => x.name is not ("map" or "screen-overview" or "desk-wings" or "desk-wings-back"));
            // RIMPRISON_TOUR=features: only the steps above (and the opening one that sets the gangs and the intel up).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "features") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("feature-"));
            // RIMPRISON_TOUR=fixes: the screens the first-hour review fixes changed (the to-do band, the Intel screen, the storyteller page).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "fixes") steps.RemoveAll(x => x.name is not ("map" or "overview-to-do" or "screen-intel" or "storyteller-in-game" or "storyteller-closed" or "welcome-letter"));
            // RIMPRISON_TOUR=shuttle: only the shuttle's steps (a quick look at it).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "shuttle") steps.RemoveAll(x => !x.name.StartsWith("shuttle-"));
            // RIMPRISON_TOUR=danger: the danger map on the ground and its hottest patches in the Deployment tab.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "danger") steps.RemoveAll(x => x.name is not ("map" or "screen-deployment" or "danger-overlay"));
            // RIMPRISON_TOUR=cells: the Rooms screen (each cell's grade) and an inmate's file (the entitlement line).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "cells") steps.RemoveAll(x => x.name is not ("map" or "screen-rooms" or "inmate-inspect" or "inmate-file"));
            steps.Add(("posts-placed", () =>
            {
                // Two guard posts in the yard (one all day, one 12-14 for the lunch hour), and the Deployment screen scrolled to the posts block; the next step shows the map, a guard walking to or standing at a post.
                Clear();
                IntVec3 yard = PrisonMap.Of(map).zones.CellsOf(RPDefOf.RP_Yard).Where(c => c.Standable(map) && c.GetEdifice(map) == null && c.GetFirstItem(map) == null).OrderBy(c => c.x + c.z).FirstOrDefault();
                if (!yard.IsValid || yard == default) return;
                foreach ((IntVec3 at, int from, int to) in new[] { (yard, 0, 0), (yard + new IntVec3(3, 0, 0), 12, 14) })
                    if (at.InBounds(map) && at.Standable(map) && !GuardPosts.PostNear(map, at, 0.5f))
                    {
                        Thing post = GenSpawn.Spawn(ThingMaker.MakeThing(RPPostDefOf.RP_GuardPost), at, map);
                        post.SetFaction(Faction.OfPlayer);
                        CompGuardPost comp = post.TryGetComp<CompGuardPost>();
                        (comp.fromHour, comp.toHour) = (from, to);
                        tourPosts.Add(post);
                    }
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Deployment);
                DeploymentTab.leftScroll = new Vector2(0f, 10000f);
            }));
            steps.Add(("posts-map", () =>
            {
                Clear();
                if (tourPosts.FirstOrDefault() is { Spawned: true } post) CameraJumper.TryJump(post);
            }));
            steps.Add(("posts-taken-down", () =>
            {
                foreach (Thing post in tourPosts) if (!post.Destroyed) post.Destroy();
                tourPosts.Clear();
            }));
            // RIMPRISON_TOUR=posts: guard posts placed, the Deployment screen's posts block, a guard at a post on the map.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "posts") steps.RemoveAll(x => x.name is not ("map" or "posts-placed" or "posts-map" or "posts-taken-down"));
            else steps.RemoveAll(x => x.name.StartsWith("posts-"));
            // The shot-caller's deal: the word's line on a known leader, the offer letter, Intel's gang row with a running deal (a breach on it), and the "End it" confirmation.
            Pawn dealLeader = null;
            Letter dealLetter = null;
            Pawn DealLeader()
            {
                if (dealLeader != null) return dealLeader;
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(InmateUtility.IsActiveInmate).ToList();
                dealLeader = inmates.FirstOrDefault(x => x.Inmate() is { gang: not null, gangKnown: true, gangLeader: true });
                if (dealLeader == null && inmates.FirstOrDefault(x => x.Inmate().security != null) is { } pick && DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault() is { } gang)
                {
                    (pick.Inmate().gang, pick.Inmate().gangKnown) = (gang, true);
                    GangRanks.SetRank(pick.Inmate(), GangRank.Leader);
                    dealLeader = pick;
                }
                return dealLeader;
            }
            steps.Add(("gangdeal-word", () =>
            {
                // The warden's word with the leader: the deal's line among the others.
                Clear();
                if (WardenSystem.InPost(map) is not { } warden || DealLeader() is not { } leader) { Missing("no warden in post, or no known gang leader for the deal's line"); return; }
                Find.CameraDriver.SetRootPosAndSize(leader.DrawPos, 12f);
                List<FloatMenuOption> options = WardenSystem.WordOptions(warden, leader).Select(o => new FloatMenuOption(o.disabled != null ? o.label + " (" + o.disabled + ")" : o.label, o.disabled != null ? null : () => { })).ToList();
                if (!WardenSystem.WordOptions(warden, leader).Any(o => o.kind == WardenWord.GangDeal)) Log.Error($"[RimPrison] ui tour: {leader.LabelShort} (a known leader) has no deal line among the warden's words");
                Find.WindowStack.Add(new FloatMenu(options, leader.LabelShort));
            }));
            steps.Add(("gangdeal-offer-letter", () =>
            {
                // The offer as the warden's word sends it, opened from the letter stack.
                Clear();
                if (WardenSystem.InPost(map) is not { } warden || DealLeader() is not { } leader) return;
                int before = Find.LetterStack.LettersListForReading.Count;
                GangDeals.Offer(warden, leader, leader.Inmate());
                dealLetter = Find.LetterStack.LettersListForReading.Count > before ? Find.LetterStack.LettersListForReading.Last() : null;
                if (dealLetter != null) dealLetter.OpenLetter();
                else Log.Error("[RimPrison] ui tour: the deal's offer sent no letter");
            }));
            steps.Add(("gangdeal-intel", () =>
            {
                // A running deal (the commissary, a breach overlooked) on Intel's gang row, with its days left and the "End it" button.
                Clear();
                if (dealLetter != null) Find.LetterStack.RemoveLetter(dealLetter);
                dealLetter = null;
                if (DealLeader() is not { } leader) return;
                if (GangDeals.Of(leader.Inmate().gang) == null)
                {
                    StoryArc arc = GangDeals.Start(map, leader, GangDealTerm.Commissary);
                    if (arc == null) { Log.Error($"[RimPrison] ui tour: no deal could be struck with {leader.LabelShort} ({GangDeals.Blocker(map, leader, leader.Inmate())})"); return; }
                    arc.deal.breaches = 1;
                    arc.deal.untilTick -= 3 * GenDate.TicksPerDay + 7 * GenDate.TicksPerHour;
                }
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intel);
            }));
            steps.Add(("gangdeal-end-confirm", () =>
            {
                // "End it" as the button opens it: the confirmation, over Intel.
                if (DealLeader()?.Inmate()?.gang is { } gang && GangDeals.Of(gang) is { } arc) IntelTab.ConfirmEndDeal(arc, map);
            }));
            steps.Add(("gangdeal-done", () =>
            {
                // The confirmation is not answered: the deal stays as it is.
                foreach (Dialog_MessageBox d in Find.WindowStack.Windows.OfType<Dialog_MessageBox>().ToList()) d.Close(doCloseSound: false);
            }));
            // RIMPRISON_TOUR=gangdeal: only the deal's steps (the opening step seeds the gangs).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "gangdeal") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("gangdeal-"));
            else steps.RemoveAll(x => x.name.StartsWith("gangdeal-"));
            // The care detail: a reserve of one, its guard and the oldest care waiting (a shut-in inmate made hungry, the request aged an hour and a half), on the Deployment screen scrolled to the block; all put back.
            (int size, List<int> pins) careWas = (0, new List<int>());
            // (The waits as they were, put back with the rest: a request aged for the picture would stay old, and its wait would stay in the longest.)
            (List<(CareRequest request, int firstTick)> waits, int longest, int longestInTrouble) careWaits = (new List<(CareRequest, int)>(), 0, 0);
            (Pawn inmate, float food, RegimeActivityDef forced, int until)? careInmate = null;
            // (The second picture's guard, hired for it and let go after, and the speed before the subset paused the game: a reserve guard who is let run takes the meal to the inmate within seconds and nothing waits.)
            Pawn careHired = null;
            TimeSpeed? careSpeed = null;
            steps.Add(("care-open", () =>
            {
                Clear();
                CareDetail care = CareDetail.Of(map);
                if (care == null) { Missing("no care detail on the map"); return; }
                careSpeed ??= Find.TickManager.CurTimeSpeed;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                careWas = (care.size, care.pins.ToList());
                care.pins.Clear();
                care.SetSize(1);
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(x => x.Inmate() is { activeFlag: true, forcedActivity: null } && !x.Downed && InmateUtility.CellOf(x) != null && x.needs?.food != null) is { } shut)
                {
                    CompInmate sc = shut.Inmate();
                    careInmate = (shut, shut.needs.food.CurLevel, sc.forcedActivity, sc.forcedUntilTick);
                    (sc.forcedActivity, sc.forcedUntilTick) = (RPDefOf.RP_Lockup, Find.TickManager.TicksGame + GenDate.TicksPerDay);
                    shut.needs.food.CurLevel = 0.1f;
                    InmateCandidates.Shared.Invalidate();
                }
                care.ForceRebuild();
                care.Tick(map);
                careWaits = (care.Queue.Select(q => (q, q.firstTick)).ToList(), care.longestWait, care.longestWaitInTrouble);
                foreach (CareRequest q in care.Queue) q.firstTick -= GenDate.TicksPerHour * 3 / 2;
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Deployment);
            }));
            steps.Add(("care-row", () =>
            {
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Deployment);
                DeploymentTab.leftScroll = new Vector2(0f, Mathf.Max(0f, DeploymentTab.careBlockY - 20f));
            }));
            steps.Add(("care-reserve", () =>
            {
                // A second guard on duty (hired for the picture, in the guards' work at every hour, with the warden's work on): the reserve of one has its member, and the oldest care waiting is shown with its wait. (The first picture is the facility as it is,
                // short of guards when only one is on duty.)
                Clear();
                CareDetail care = CareDetail.Of(map);
                if (care == null) { Missing("no care detail on the map"); return; }
                if (careHired == null)
                {
                    var before = new HashSet<Pawn>(StaffCommute.AllStaff(map));
                    if (!StaffUtility.Hire(map, RPSecurityDefOf.RP_Guard, free: true)) { Missing("no guard could be hired for the reserve"); return; }
                    careHired = StaffCommute.AllStaff(map).FirstOrDefault(x => !before.Contains(x));
                    if (careHired == null) { Missing("the hired guard is not on the staff"); return; }
                }
                if (careHired.timetable != null)
                    for (int h = 0; h < 24; h++) careHired.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                careHired.workSettings?.SetPriority(WorkTypeDefOf.Warden, 1);
                care.ForceRebuild();
                care.Tick(map);
                // (A request made by this rebuild is aged as the first was, and put back with it.)
                foreach (CareRequest q in care.Queue)
                {
                    if (careWaits.waits.Any(w => w.request == q)) continue;
                    careWaits.waits.Add((q, q.firstTick));
                    q.firstTick -= GenDate.TicksPerHour * 3 / 2;
                }
                if (care.Members.Count == 0) Missing("the reserve has no member with a second guard on duty");
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Deployment);
            }));
            steps.Add(("care-reserve-row", () =>
            {
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Deployment);
                DeploymentTab.leftScroll = new Vector2(0f, Mathf.Max(0f, DeploymentTab.careBlockY - 20f));
            }));
            steps.Add(("care-back", () =>
            {
                if (careHired != null && careHired.Spawned) StaffUtility.Fire(careHired, arrest: false, record: false);
                careHired = null;
                if (careSpeed is { } speed0) Find.TickManager.CurTimeSpeed = speed0;
                careSpeed = null;
                CareDetail care = CareDetail.Of(map);
                if (care != null)
                {
                    care.pins.Clear();
                    care.SetSize(careWas.size);
                    care.pins.AddRange(careWas.pins);
                    care.ForceRebuild();
                    foreach ((CareRequest request, int firstTick) in careWaits.waits) request.firstTick = firstTick;
                    (care.longestWait, care.longestWaitInTrouble) = (careWaits.longest, careWaits.longestInTrouble);
                }
                careWaits = (new List<(CareRequest, int)>(), 0, 0);
                if (careInmate is { } was && was.inmate.Inmate() is { } wc)
                {
                    (wc.forcedActivity, wc.forcedUntilTick) = (was.forced, was.until);
                    was.inmate.needs.food.CurLevel = was.food;
                    InmateCandidates.Shared.Invalidate();
                }
                careInmate = null;
            }));
            // RIMPRISON_TOUR=care: the care detail's row with a reserve of one: short of guards as the facility has them, then with a second guard on duty, its member and the oldest wait.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "care") steps.RemoveAll(x => x.name is not ("map" or "care-open" or "care-row" or "care-reserve" or "care-reserve-row" or "care-back"));
            else steps.RemoveAll(x => x.name.StartsWith("care-"));
            // The evacuation block of the Deployment screen: the assembly points (a yard patch and the next markable one, with their levels), and a drill of a wing (once one has its people in it): the walkers on the map, then the
            // accountability list running; the points and the drill put back (the clock it ran on to find people in a wing is not).
            (List<(PrisonZoneDef zone, int anchor, List<SecurityLevelDef> levels)> points, int drillTick)? evacWas = null;
            TimeSpeed? evacSpeed = null;
            steps.Add(("evac-points", () =>
            {
                Clear();
                PrisonMap pm = PrisonMap.Of(map);
                if (pm == null) { Missing("no prison map"); return; }
                EvacuationState st = pm.evac;
                evacSpeed ??= Find.TickManager.CurTimeSpeed;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                evacWas ??= (st.points.Select(p => (p.zone, p.anchor, p.levels.ToList())).ToList(), st.lastDrillTick);
                st.points.Clear();
                foreach (int i in Enumerable.Range(0, pm.zones.Patches.Count).Where(i => EvacuationSystem.MarkableZone(pm.zones.Patches[i].zone)).OrderBy(i => pm.zones.Patches[i].zone == RPDefOf.RP_Yard ? 0 : 1).Take(2).ToList())
                    EvacuationSystem.Mark(pm, i);
                if (st.points.Count == 0) Missing("no yard, kept room or holding patch to mark as an assembly point");
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Deployment);
            }));
            steps.Add(("evac-points-row", () =>
            {
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Deployment);
                DeploymentTab.leftScroll = new Vector2(0f, Mathf.Max(0f, EvacuationUI.blockY - 20f));
                EvacuationUI.scrollToBlock = true;
            }));
            steps.Add(("evac-drill", () =>
            {
                Clear();
                PrisonMap pm = PrisonMap.Of(map);
                EvacuationState st = pm?.evac;
                if (st == null) { Missing("no prison map"); return; }
                st.lastDrillTick = -999999;
                List<PrisonZoneDef> drillable = DefDatabase<PrisonZoneDef>.AllDefsListForReading.Where(z => z.security != null && pm.zones.Any(z) && Unlocks.IsUnlocked(z)).OrderBy(z => z.uiOrder).ToList();
                // The wing that can be drilled now with the most people standing in it, once at least three are (or all whose bed is there, when fewer sleep there); null when none is.
                PrisonZoneDef DrillWing(bool anyone)
                {
                    PrisonZoneDef best = null;
                    int bestInside = 0;
                    foreach (PrisonZoneDef z in drillable)
                    {
                        if (EvacuationSystem.CannotStart(map, z, drill: true) != null) continue;
                        List<(Pawn pawn, bool inside)> occupants = EvacuationSystem.Occupants(map, pm, z);
                        int inside = occupants.Count(o => o.inside);
                        if (inside > bestInside && (anyone || inside >= System.Math.Min(3, occupants.Count))) (best, bestInside) = (z, inside);
                    }
                    return best;
                }
                // (A drill moves only those standing in the wing: at an hour when everyone is out of it - the showers at six, the canteen, the yard - there is nobody to drill and it cannot start. The clock runs on, tick by tick
                // as the headless harness does, until a wing has its people in it, half a day at most.)
                int drillWaited = 0;
                PrisonZoneDef drillWing = DrillWing(anyone: false);
                for (; drillWing == null && drillWaited < GenDate.TicksPerHour * 12; drillWaited += EvacuationSystem.BeatTicks)
                {
                    for (int i = 0; i < EvacuationSystem.BeatTicks; i++) Find.TickManager.DoSingleTick();
                    drillWing = DrillWing(anyone: false);
                }
                drillWing ??= DrillWing(anyone: true);
                if (drillWaited > 0) RPLog.Message($"[RimPrison] ui tour: the clock ran {drillWaited / (float)GenDate.TicksPerHour:0.0}h for a wing with its people in it to drill");
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                if (drillWing == null) { Missing("no wing to drill: " + string.Join("; ", drillable.Select(z => z.label + " - " + (EvacuationSystem.CannotStart(map, z, true) ?? "?")))); return; }
                if (!EvacuationSystem.Start(map, drillWing, drill: true)) { Missing("the drill did not start: " + EvacuationSystem.CannotStart(map, drillWing, true)); return; }
                // (A few ticks, so that the roll call has its routes and the walkers are on their way, well short of the quarter hour a drill lasts at least; then the game stands still again, the pawns drawn where they are.)
                for (int i = 0; i < 150; i++) Find.TickManager.DoSingleTick();
                if (!st.Active) Missing("the drill was over before its picture");
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned) p.Drawer?.tweener?.ResetTweenedPosToRoot();
                foreach (Dialog_NodeTree d in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) d.Close(doCloseSound: false);
                // The map: the wing's people on their way (the roll call is the next picture).
                List<Pawn> drilled = st.entries.Where(e => e.Moving && e.pawn is { Spawned: true }).Select(e => e.pawn).ToList();
                if (drilled.Count > 0) Find.CameraDriver.SetRootPosAndSize(new Vector3(drilled.Average(p => p.DrawPos.x), 0f, drilled.Average(p => p.DrawPos.z)), 24f);
            }));
            steps.Add(("evac-drill-row", () =>
            {
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Deployment);
                DeploymentTab.leftScroll = new Vector2(0f, Mathf.Max(0f, EvacuationUI.blockY - 20f));
                EvacuationUI.scrollToBlock = true;
            }));
            steps.Add(("evac-back", () =>
            {
                EvacuationSystem.Reset(map);
                EvacuationUI.scrollToBlock = false;
                PrisonMap pm = PrisonMap.Of(map);
                if (pm != null && evacWas is { } was)
                {
                    pm.evac.points.Clear();
                    foreach ((PrisonZoneDef zone, int anchor, List<SecurityLevelDef> levels) in was.points) pm.evac.points.Add(new AssemblyPoint { zone = zone, anchor = anchor, levels = levels });
                    pm.evac.lastDrillTick = was.drillTick;
                }
                evacWas = null;
                if (evacSpeed is { } speed) Find.TickManager.CurTimeSpeed = speed;
                evacSpeed = null;
            }));
            // RIMPRISON_TOUR=evacuation: the Deployment screen's evacuation block with two assembly points marked, then a drill just begun (the map, then the accountability list).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "evacuation") steps.RemoveAll(x => x.name is not ("map" or "evac-points" or "evac-points-row" or "evac-drill" or "evac-drill-row" or "evac-back"));
            else steps.RemoveAll(x => x.name.StartsWith("evac-"));
            // RIMPRISON_TOUR=deployment: the Deployment screen, at its head and scrolled to its foot.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "deployment") steps.RemoveAll(x => x.name is not ("map" or "screen-deployment" or "screen-deployment-foot"));
            // The applicants' dialog: through the real entry (the Staff tab's hire button), on a pool of the tour's own (the live one put back, with the funds), for the guard post.
            StaffRoleDef hiringPost = RPSecurityDefOf.RP_Guard;
            steps.Add(("hiring-staff-tab", () =>
            {
                Clear();
                PrisonGame game = PrisonGame.Instance;
                if (tourHiringLive == null)
                {
                    (tourHiringLive, tourHiringFunds0) = (game.applicants, game.ledger.funds);
                    game.applicants = new ApplicantPool();
                }
                if (!Unlocks.IsUnlocked(hiringPost)) Log.Error("[RimPrison] ui tour: the guard post is locked, the applicants' dialog closes itself");
                tourHiringRich = System.Math.Max(tourHiringFunds0, StaffUtility.AgencyFee(hiringPost) * 2);
                game.ledger.funds = tourHiringRich;
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Staff);
            }));
            steps.Add(("hiring-applicants", () =>
            {
                HiringUI.Open(Find.CurrentMap, hiringPost);
                int made = PrisonGame.Instance.applicants.For(hiringPost).Count;
                if (made != ApplicantPool.PerRole) Log.Error($"[RimPrison] ui tour: the guard post has {made} applicants, {ApplicantPool.PerRole} expected");
            }));
            steps.Add(("hiring-funds-short", () =>
            {
                // One short of the post's fee: the Hire buttons greyed with their reason (and the agency's).
                PrisonGame.Instance.ledger.funds = hiringPost.hireCost - 1;
                HiringUI.Open(Find.CurrentMap, hiringPost);
            }));
            steps.Add(("hiring-nobody", () =>
            {
                // A post where nobody could be found: the pool let go and every candidate the generator makes unfit for the work (the real give-up, no fee).
                PrisonGame.Instance.ledger.funds = tourHiringRich;
                Find.WindowStack.TryRemove(typeof(Dialog_Applicants), doCloseSound: false);
                PrisonGame.Instance.applicants.Clear();
                StaffUtility.CandidateUnfit = _ => true;
                try { HiringUI.Open(Find.CurrentMap, hiringPost); }
                finally { StaffUtility.CandidateUnfit = null; }
                int made = PrisonGame.Instance.applicants.For(hiringPost).Count;
                if (made != 0) Log.Error($"[RimPrison] ui tour: the guard post has {made} applicants, none expected");
            }));
            steps.Add(("hiring-back", HiringBack));
            // RIMPRISON_TOUR=hiring: the Staff tab, the applicants' dialog (a full pool, funds too short, nobody found), all put back. Not in the whole tour: it swaps the applicants' pool and the funds.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "hiring") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("hiring-"));
            else steps.RemoveAll(x => x.name.StartsWith("hiring-"));
            // The count: the HUD's row in its states (next, counting, cleared, missed, unaccounted with its tip), the alert, the Policy row. A state of the tour's own on the map, the live one put back; the counting state is set
            // in the future so that no guard is sent and the beat does not end it while the picture is taken. The clock is put at 10:30 (no count's hour) and held there.
            steps.Add(("count-hud-next", () =>
            {
                Clear();
                Find.WindowStack.TryRemove(typeof(Dialog_MessageBox), doCloseSound: false);
                // An hour with no count (the default regime's hours are 22, 6, 14 and 2 at most, so 10:30 is clear of every count's window), then the clock stands still: a count's own beat cannot start under a picture.
                AdvanceTo(map, 10, 30);
                CountFresh(map, CountSystem.DefaultPerDay);
            }));
            steps.Add(("count-hud-counting", () =>
            {
                CountState c = CountFresh(map, CountSystem.DefaultPerDay);
                int later = Find.TickManager.TicksGame + 5 * GenDate.TicksPerDay;
                (c.startedTick, c.dueTick) = (later, later);
                c.wingsLeft = WardenSystem.Wings(map).Take(2).Select(w => (int)w.gridIndex).ToList();
            }));
            steps.Add(("count-hud-cleared", () => CountFresh(map, CountSystem.DefaultPerDay).lastClearedTick = Find.TickManager.TicksGame - 1500));
            steps.Add(("count-hud-missed", () => CountFresh(map, CountSystem.DefaultPerDay).lastMissedTick = Find.TickManager.TicksGame - 800));
            steps.Add(("count-hud-unaccounted", () =>
            {
                CountState c = CountFresh(map, CountSystem.DefaultPerDay);
                int now = Find.TickManager.TicksGame;
                c.unaccounted.Add(new CountEntry { pawnId = 900001, name = "Marek", place = "in the yard", escapedTick = now - 7 * GenDate.TicksPerHour });
                c.unaccounted.Add(new CountEntry { pawnId = 900002, name = "Dessa", place = "in a cell of the medium wing", escapedTick = now - 3 * GenDate.TicksPerHour });
                c.lastMissedTick = now - 800;
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
            }));
            steps.Add(("count-hud-unaccounted-tip", () =>
            {
                // (A tooltip needs the mouse over its row, which a tour cannot do: its text is shown in a box, as the HUD's row gives it.)
                PrisonGame game = PrisonGame.Instance;
                string tip = "RP_HudCountTip".Translate(CountSystem.HoursText(game)).ToString() + "\n\n" + string.Join("\n", CountSystem.UnaccountedLines(map, PrisonMap.Of(map)));
                Find.WindowStack.Add(new Dialog_MessageBox(tip, title: "The HUD's count row, hovered"));
            }));
            steps.Add(("count-alert", () =>
            {
                Find.WindowStack.TryRemove(typeof(Dialog_MessageBox), doCloseSound: false);
                PrisonAlerts.Invalidate();
                PrisonAlerts.Refresh();
            }));
            steps.Add(("count-policy", () =>
            {
                CountFresh(map, 3);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Policy);
            }));
            steps.Add(("count-policy-off", () =>
            {
                CountFresh(map, 0);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Policy);
            }));
            steps.Add(("count-back", () =>
            {
                Clear();
                CountBack();
            }));
            // RIMPRISON_TOUR=count: those steps. Not in the whole tour: it swaps the map's count state.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "count") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("count-"));
            else steps.RemoveAll(x => x.name.StartsWith("count-"));
            // Suppression made legible: the overview's wing table with inmates held down, the Programs screen's blocker when everyone is, and a held inmate's file with the date on its bar.
            steps.Add(("suppression-wings", () =>
            {
                // (Every other inmate held at 80, the rest quiet: the table's under-the-thumb column has numbers. Put back by the last step.)
                tourSuppressed.Clear();
                int n = 0;
                foreach (CompInmate c in map.mapPawns.PrisonersOfColonySpawned.Select(p => p.Inmate()).Where(c => c != null))
                {
                    tourSuppressed.Add((c, c.suppressed));
                    c.suppressed = n++ % 2 == 0 ? 80f : 10f;
                }
                DeskBoard.Reset();
                Clear();
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Overview);
                OverviewTab.showWings = true;
            }));
            steps.Add(("suppression-programs", () =>
            {
                // Every inmate held down: each class that would have had a cohort says that suppression is why it has none.
                foreach ((CompInmate c, float _) in tourSuppressed) c.suppressed = 80f;
                OverviewTab.showWings = false;
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Programs);
            }));
            steps.Add(("suppression-file", () =>
            {
                // The file of an inmate at 72 (five days from the line): the bar reads "suppressed 72 - clear in 5 days".
                Clear();
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback(p => p.Inmate() != null) is { } held)
                {
                    held.Inmate().suppressed = 72f;
                    CameraJumper.TryJumpAndSelect(held);
                    if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                    if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_InmateFile);
                }
            }));
            steps.Add(("suppression-back", () =>
            {
                foreach ((CompInmate c, float suppressed) in tourSuppressed) c.suppressed = suppressed;
                tourSuppressed.Clear();
                DeskBoard.Reset();
                Clear();
            }));
            // RIMPRISON_TOUR=suppression: those steps (the overview's wing table, the Programs blocker, the file's date). Not in the whole tour: it changes every inmate's suppression.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "suppression") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("suppression-"));
            else steps.RemoveAll(x => x.name.StartsWith("suppression-"));
            // Class bookings: the Programs tab with two bookings (one clashing with the other for the room and the teacher) and a miss with its reason, the booking window, the warning after three misses in a row. The bookings are the tour's own and are taken out again.
            var tourBooked = new List<ClassBooking>();
            ProgramDef tourBookEdu = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation"), tourBookRehab = DefDatabase<ProgramDef>.GetNamed("RP_SubstanceAbuse");
            steps.Add(("bookings-programs", () =>
            {
                Clear();
                PrisonMap bookPm = PrisonMap.Of(map);
                int bookHour = GenLocalDate.HourOfDay(map);
                ClassBooking bookEdu = bookPm.bookings.Add(tourBookEdu, (1 << ((bookHour + 2) % 24)) | (1 << ((bookHour + 3) % 24)));
                ClassBooking bookRehab = bookPm.bookings.Add(tourBookRehab, 1 << ((bookHour + 3) % 24));
                tourBooked.AddRange(new[] { bookEdu, bookRehab }.Where(x => x != null));
                // (A miss each, as the hourly pass would have kept it: no counsellor, then a room kept for other levels.)
                bookEdu?.NoteMissed((bookHour + 23) % 24, ClassMiss.NoTeacher);
                bookRehab?.NoteMissed((bookHour + 23) % 24, ClassMiss.VenueClosed);
                bookPm.bookings.version++;
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Programs);
            }));
            steps.Add(("bookings-window", () => ClassBookingUI.Open(tourBookEdu, map)));
            steps.Add(("bookings-warning", () =>
            {
                // Three misses in a row on the second booking: the row's warning.
                Find.WindowStack.TryRemove(typeof(Dialog_ClassBooking), doCloseSound: false);
                if (tourBooked.Count > 1)
                {
                    tourBooked[1].NoteMissed(GenLocalDate.HourOfDay(map), ClassMiss.NoSlot);
                    tourBooked[1].NoteMissed(GenLocalDate.HourOfDay(map), ClassMiss.NoTeacher);
                    PrisonMap.Of(map).bookings.version++;
                }
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Programs);
            }));
            steps.Add(("bookings-back", () =>
            {
                Clear();
                Find.WindowStack.TryRemove(typeof(Dialog_ClassBooking), doCloseSound: false);
                foreach (ClassBooking tourBook in tourBooked) PrisonMap.Of(map).bookings.Remove(tourBook);
                tourBooked.Clear();
            }));
            // RIMPRISON_TOUR=bookings: those steps. Not in the whole tour: it books classes.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "bookings") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("bookings-"));
            else steps.RemoveAll(x => x.name.StartsWith("bookings-"));
            // RIMPRISON_TOUR=grounds: the Land tab, the boundary on the map, the New prison dialog (its dev row).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "grounds") steps.RemoveAll(x => !x.name.StartsWith("grounds-") && x.name != "screen-land" && x.name != "new-prison-dialog");
            // The clock and the speed buttons in the prison panel: paused, at speed 1 and 3, in the 12-hour mode (a wider time), the bottom right without vanilla's, the world map with vanilla's back.
            steps.Add(("hud-paused", () =>
            {
                (tourHudSpeed0, tourHudTwelve0, tourHudChanged) = (Find.TickManager.CurTimeSpeed, Prefs.TwelveHourClockMode, true);
                Clear();
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            }));
            steps.Add(("hud-speed1", () => Find.TickManager.CurTimeSpeed = TimeSpeed.Normal));
            steps.Add(("hud-speed3", () => Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast));
            steps.Add(("hud-twelve-hour", () =>
            {
                Prefs.TwelveHourClockMode = true;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            }));
            steps.Add(("hud-bottom-right", () =>
            {
                // The bottom right with the weather and the temperature where the date and the buttons were (the panel's clock took them over).
                Prefs.TwelveHourClockMode = tourHudTwelve0;
                if (!PrisonHUD.ShowsClock(map)) Log.Error("[RimPrison] ui tour: the panel does not show the clock on the prison's map");
            }));
            steps.Add(("hud-world-map", () => Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.Planet));
            steps.Add(("hud-world-map-back", () =>
            {
                // (The picture of the world map is taken before this step runs, with vanilla's date and buttons at its bottom right.)
                if (PrisonHUD.ShowsClock(Find.CurrentMap)) Log.Error("[RimPrison] ui tour: the panel shows the clock on the world map");
                Find.World.renderer.wantedMode = RimWorld.Planet.WorldRenderMode.None;
            }));
            steps.Add(("hud-back", HudBack));
            // RIMPRISON_TOUR=hud: the panel's clock row (paused, speed 1, speed 3, 12-hour), the bottom right without vanilla's and the world map with them. Not in the whole tour: it changes the speed and goes to the world map.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "hud") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("hud-"));
            else steps.RemoveAll(x => x.name.StartsWith("hud-"));
            // RIMPRISON_TOUR=drugtest: drug testing on the screens - the Policy tab's row (the random mode and its number a day), an inmate's file with a positive test line, the Statistics rows.
            steps.Add(("drugtest-seed", () =>
            {
                PrisonGame game = PrisonGame.Instance;
                int now = Find.TickManager.TicksGame;
                (game.drugTests, game.drugTestsPerDay) = (DrugTestPolicy.TargetedAndRandom, 3);
                // (A day's numbers for the Policy row, and kits in stock so that the row says what it says in a prison that tests: yesterday's counts, not the "no kits" blocker.)
                (game.drugTestLog.day, game.drugTestLog.yesterdayTests, game.drugTestLog.yesterdayPositives) = (Ledger.CurrentDay, 5, 2);
                (game.stats.drugTests, game.stats.drugPositives) = (14, 5);
                if (DrugTesting.Kit != null && map.mapPawns.FreeColonistsSpawned.FirstOrFallback() is { } near)
                {
                    Thing kits = ThingMaker.MakeThing(DrugTesting.Kit);
                    kits.stackCount = 12;
                    GenPlace.TryPlaceThing(kits, near.Position, map, ThingPlaceMode.Near);
                }
                // An inmate who tested positive a few hours ago (their file's test line).
                if (map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback(x => x.Inmate() is { activeFlag: true }) is { } user)
                {
                    CompInmate c = user.Inmate();
                    (c.lastDrugUseTick, c.lastTestedTick, c.lastFailedTestTick, c.lastTestResult, c.lastTestRandom) = (now - 12000, now - 9000, now - 9000, DrugTestResult.Positive, true);
                    Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                    CameraJumper.TryJumpAndSelect(user);
                }
                else Log.Error("[RimPrison] ui tour: no inmate to seed a drug test on");
            }));
            steps.Add(("drugtest-policy", () =>
            {
                Find.Selector.ClearSelection();
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Policy);
            }));
            steps.Add(("drugtest-inmate-file", () =>
            {
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                Pawn tested = map.mapPawns.PrisonersOfColonySpawned.FirstOrFallback(x => x.Inmate() is { lastTestResult: DrugTestResult.Positive });
                if (tested == null) return;
                CameraJumper.TryJumpAndSelect(tested);
                if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_InmateFile);
            }));
            steps.Add(("drugtest-statistics", () =>
            {
                Find.Selector.ClearSelection();
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                StatisticsTab.TourScrollTo = StatGroup.Contraband;
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Statistics);
            }));
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "drugtest") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("drugtest-"));
            else steps.RemoveAll(x => x.name.StartsWith("drugtest-"));
            steps.Add(("agreement-letter", () =>
            {
                // The staff's demand letter as the real worker writes it (from a clean book: no promise running, no peace, nothing broken), open with its three answers.
                Clear();
                tourBook = PrisonGame.Instance.agreements;
                PrisonGame.Instance.agreements = new StaffAgreementBook();
                if (!DefDatabase<IncidentDef>.GetNamed("RP_StaffStrike").Worker.TryExecute(new IncidentParms { target = map, forced = true })) Log.Error("[RimPrison] ui tour: the staff's demand was not sent");
                else if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_StaffStrike")) is { } letter) letter.OpenLetter();
            }));
            steps.Add(("agreement-panel", () =>
            {
                // The Staff tab with a promise running (understaffing, made through the real Agree, part-way) and a rise that stands (a role that is not the guards').
                Clear();
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_StaffStrike")).ToList()) Find.LetterStack.RemoveLetter(l);
                PrisonGame game = PrisonGame.Instance;
                game.agreements = new StaffAgreementBook();
                Pawn rep = StaffAgreements.Representative(map, StaffGrievance.Understaffed);
                if (rep == null || !StaffAgreements.Agree(map, game, StaffGrievance.Understaffed, rep)) Log.Error("[RimPrison] ui tour: no promise could be made");
                else (game.agreements.active.hoursSeen, game.agreements.active.compliantHours) = (31, 27);
                StaffRoleDef other = StaffUtility.Roles.FirstOrDefault(x => !x.guardDuties && !x.warden && !x.visitor && x.dailyWage > 0 && Unlocks.IsUnlocked(x));
                if (other != null) game.agreements.rises.Add(new PayRise { role = other, agreedTick = Find.TickManager.TicksGame - 6 * GenDate.TicksPerDay, keptDay = Ledger.CurrentDay - 1 });
                else Log.Error("[RimPrison] ui tour: no role to give a rise");
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Staff);
            }));
            steps.Add(("agreement-back", () =>
            {
                // (The game's own book back: the tour's promise and rise were never made.)
                Clear();
                if (tourBook != null) PrisonGame.Instance.agreements = tourBook;
                tourBook = null;
            }));
            // RIMPRISON_TOUR=agreement: the demand letter with its three answers, the Staff tab's panel with a running promise and a standing rise.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "agreement") steps.RemoveAll(x => x.name is not ("map" or "agreement-letter" or "agreement-panel" or "agreement-back"));
            else steps.RemoveAll(x => x.name.StartsWith("agreement-"));
            steps.Add(("funeral-letter", () =>
            {
                // An inmate with three friends dies and is laid in the morgue, as a guard would leave one (the funeral is switched on for the tour): the warden's letter, open with its two answers.
                Clear();
                // (On the second morning, as the first picture of the full tour is: the inmates admitted at the start are searched and in their wings by then, and only an inmate in the regime's hands is let out for a service; on the first day nobody is to come and the funeral waits.)
                AdvanceTo(map, 10, 15, extraDays: 1);
                PrisonMap pm = PrisonMap.Of(map);
                // (The classes that began at ten hold their pupils through the next hour, and a pupil in one is not let out for a service - the tour's inmates are all in one: round 4's log. They are ended here, as a warden who wanted the service would let them out.)
                foreach (ProgramSession session in pm.sessions) session.endTick = Find.TickManager.TicksGame;
                ProgramSystem.CompleteFinished(pm);
                IntVec3 morgue = pm.zones.CellsOf(RPMorgueDefOf.RP_Morgue).FirstOrDefault(c => c.Standable(map) && c.GetFirstThing<Corpse>(map) == null);
                if (!morgue.IsValid || pm.zones.CellsOf(RPBurialDefOf.RP_BurialPlot).Count == 0)
                {
                    Log.Error("[RimPrison] ui tour: the prison has no free morgue cell or no burial plot for a funeral");
                    return;
                }
                Funerals.TestOn = true;
                // (A day with its letters already out would hold this one back for the morning.)
                PrisonGame.Instance.story.decisionsToday = 0;
                Pawn dead = InmateGenerator.Generate(InmateUtility.DefaultSecurity, 99100);
                GenSpawn.Spawn(dead, morgue, map);
                InmateGenerator.MakeInmate(dead, null);
                dead.Inmate().relatives.Clear();
                dead.Inmate().familyCount = 0;
                // (Friends who can be let out at the service's hour: a funeral nobody can come to is not held.)
                int hold = Funerals.NextHoldTick(PrisonGame.Instance, map, InmateUtility.DefaultSecurity);
                int holdHour = hold < 0 ? GenLocalDate.HourOfDay(map) : (GenLocalDate.HourOfDay(map) + HoursTo(hold)) % 24;
                var friends = map.mapPawns.PrisonersOfColonySpawned.Where(p => Funerals.CanComeOut(PrisonGame.Instance, pm, p, holdHour)).Take(3).ToList();
                foreach (Pawn friend in friends) friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, dead);
                if (friends.Count == 0)
                {
                    // (Why each is kept: the tour's own picture of the prison at that hour, for the next look.)
                    string kept = string.Join(", ", map.mapPawns.PrisonersOfColonySpawned.Select(p => $"{p.LabelShort}: {Funerals.WhyNotComeOut(PrisonGame.Instance, pm, p, holdHour) ?? "free"}"));
                    Log.Error($"[RimPrison] ui tour: no inmate can be let out at hour {holdHour} to mourn the dead one (the funeral would wait for nobody): {kept}");
                }
                tourFuneralDeadId = dead.thingIDNumber;
                dead.Kill(null);
                if (dead.Corpse is not { Spawned: true } body) Log.Error("[RimPrison] ui tour: the dead inmate left no body");
                else Funerals.Notify_Laid(body);
                if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Funeral")) is { } letter) letter.OpenLetter();
                else Log.Error("[RimPrison] ui tour: no funeral letter was sent");
            }));
            steps.Add(("funeral-gathering", () =>
            {
                // A funeral is chosen; the clock runs to its hour and forty minutes into it, with the mourners walking out, and the camera is on the place.
                Clear();
                if (Funerals.Of(tourFuneralDeadId) is not { state: FuneralState.Asked } f) return;
                if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_Funeral")) is { } letter)
                {
                    letter.answered = true;
                    PrisonDecisions.Choose(letter, 1);
                    Find.LetterStack.RemoveLetter(letter);
                }
                int ahead = HoursTo(f.holdTick);
                AdvanceTo(map, (GenLocalDate.HourOfDay(map) + Mathf.Max(1, ahead)) % 24, 40);
                if (f.state != FuneralState.Gathering || !f.cell.IsValid) Log.Error($"[RimPrison] ui tour: the funeral is not under way at its hour ({f.Describe()})");
                else Find.CameraDriver.SetRootPosAndSize(f.cell.ToVector3Shifted(), 14f);
            }));
            steps.Add(("funeral-back", () =>
            {
                // (The funeral is back as the game has it: off in a test run.)
                Funerals.TestOn = false;
                tourFuneralDeadId = -1;
            }));
            // RIMPRISON_TOUR=funeral: the letter, and the gathering at the grave (or in the chapel when the mourners cannot walk into the plot).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "funeral") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("funeral-"));
            else steps.RemoveAll(x => x.name.StartsWith("funeral-"));
            steps.Add(("inspector-letter", () =>
            {
                // The inspection's letter as the real visit writes it: the nights thinly guarded (nobody on the roster at night: put back at once) on top of what the prison gives, an inspector on the map, the report with its deadlines (from a clean book).
                Clear();
                PrisonGame game = PrisonGame.Instance;
                tourInspect = (game.story.inspection, game.story.inspectionTick, game.story.inspectionPrep, game.story.bribeExposeTick);
                game.story.inspection = new InspectionState();
                (game.story.inspectionTick, game.story.inspectionPrep, game.story.bribeExposeTick) = (-1, 0, -1);
                InspectionReport.TestStrip = new int[24];
                Inspections.Inspect(game, map);
                InspectionReport.TestStrip = null;
                if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter>().LastOrDefault(l => l.Label.ToString().Contains("State inspection")) is { } letter) letter.OpenLetter();
                else Log.Error("[RimPrison] ui tour: the inspection sent no letter");
            }));
            steps.Add(("inspector-todo", () =>
            {
                // The overview's to-do band with the inspector's findings as lines (time left on each, and the ones overdue).
                Clear();
                if (PrisonGame.Instance.story.inspection.findings.Count > 1) PrisonGame.Instance.story.inspection.findings[1].dueTick = Find.TickManager.TicksGame - GenDate.TicksPerHour;
                PrisonAlerts.Invalidate();
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Overview);
            }));
            steps.Add(("inspector-back", () =>
            {
                // (The game's own book back: the tour's visit was never. Its grade grant or fine, reputation, count and chronicle line are not undone: a tour of a throwaway game.)
                Clear();
                PrisonGame game = PrisonGame.Instance;
                if (tourInspect.book != null) (game.story.inspection, game.story.inspectionTick, game.story.inspectionPrep, game.story.bribeExposeTick) = tourInspect;
                tourInspect = default;
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => x.GetComp<CompStaff>()?.role == RPInspectorDefOf.RP_Inspector).ToList())
                {
                    p.GetComp<CompStaff>().role = null;
                    p.Destroy(DestroyMode.Vanish);
                }
                PrisonAlerts.Invalidate();
            }));
            // RIMPRISON_TOUR=inspector: the report letter and the to-do band with its lines. Not in the whole tour: it runs a real visit and brings a visitor (the book and the visitor put back).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "inspector") steps.RemoveAll(x => x.name is not ("map" or "inspector-letter" or "inspector-todo" or "inspector-back"));
            else steps.RemoveAll(x => x.name.StartsWith("inspector-"));
            steps.Add(("tools-letter", () =>
            {
                // The workshop's supply made from the prison's own presses, a tool stolen by an inmate and counted short by the staff, and the warden's letter open with its three answers (the count is switched on for the tour).
                Clear();
                PrisonGame game = PrisonGame.Instance;
                tourToolsBook = game.tools;
                game.tools = new ToolLedgers();
                ToolCount.TestOn = true;
                ToolCount.Hourly(game, map);
                ToolPool pool = game.tools.PoolOf(RPLaborDefOf.RP_Workshop);
                Pawn thief = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.Inmate() is { activeFlag: true } && p.inventory != null);
                if (pool == null || thief == null) { Log.Error("[RimPrison] ui tour: the prison has no workshop bench or no inmate for the tool count"); return; }
                ContrabandUtility.TheftRollForTests = null;
                Thing tool = ToolCount.Steal(thief, ContrabandKind.Tool, RPLaborDefOf.RP_Workshop);
                if (tool == null) { Log.Error("[RimPrison] ui tour: no tool could be taken from the workshop's supply"); return; }
                tourToolsTheft = (thief, tool);
                pool.period = 6;
                pool.issued = pool.owned;
                pool.countDue = true;
                pool.countDuePeriod = pool.period;
                tourToolsDecisions = PrisonGame.Instance.story.decisionsToday;
                PrisonGame.Instance.story.decisionsToday = 0;
                ToolCount.Counted(pool, null);
                if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_ToolCount")) is { } letter) letter.OpenLetter();
                else Log.Error("[RimPrison] ui tour: the count sent no letter");
            }));
            steps.Add(("tools-intel", () =>
            {
                // Intel's tools rows: the workshop as the staff counted it (a tool short, the output down, the button that replaces it) and the kitchen as issued.
                Clear();
                ToolCount.Hourly(PrisonGame.Instance, map);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intel);
            }));
            steps.Add(("tools-back", () =>
            {
                // (The game's own ledgers back, the stolen tool gone from the pocket, the letter off the stack: the tour's count was never.)
                Clear();
                PrisonGame game = PrisonGame.Instance;
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_ToolCount")).ToList()) Find.LetterStack.RemoveLetter(l);
                if (tourToolsTheft.tool != null)
                {
                    tourToolsTheft.holder.inventory.innerContainer.Remove(tourToolsTheft.tool);
                    tourToolsTheft.tool.Destroy();
                }
                tourToolsTheft = default;
                if (tourToolsBook != null)
                {
                    game.tools = tourToolsBook;
                    game.tools.Reindex();
                }
                tourToolsBook = null;
                game.story.decisionsToday = tourToolsDecisions;
                ToolCount.TestOn = false;
            }));
            // RIMPRISON_TOUR=tools: the letter and the Intel rows. Not in the whole tour: it swaps the game's tool ledgers (put back) and steals a tool.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "tools") steps.RemoveAll(x => x.name is not ("map" or "tools-letter" or "tools-intel" or "tools-back"));
            else steps.RemoveAll(x => x.name.StartsWith("tools-"));
            steps.Add(("adjudication-letter", () =>
            {
                // The sheet's letter as the warden gets it: the policy on, three cases (an officer named with a camera and a weapon, a camera alone, nobody who can swear to it) from a clean book, the letter opened.
                Clear();
                PrisonGame game = PrisonGame.Instance;
                tourAdjBook = game.adjudication;
                tourAdjPolicy = game.adjudicate;
                tourAdjAdded = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null).Select(p => (p.Inmate(), p.Inmate().daysAdded, p.Inmate().admittedTick)).ToList();
                game.adjudication = new AdjudicationBook();
                game.adjudicate = true;
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { activeFlag: true }).Take(3).ToList();
                Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrFallback(GuardUtility.IsGuard);
                if (inmates.Count == 0)
                {
                    Log.Error("[RimPrison] ui tour: no inmates for the adjudication sheet");
                    return;
                }
                var cases = new List<AdjudicationCase>();
                MisconductDef[] offences = { DefDatabase<MisconductDef>.GetNamed("RP_AssaultStaff"), DefDatabase<MisconductDef>.GetNamed("RP_EscapeAttempt"), DefDatabase<MisconductDef>.GetNamed("RP_Riot") };
                for (int i = 0; i < inmates.Count; i++)
                {
                    CompInmate c = inmates[i].Inmate();
                    var k = new AdjudicationCase
                    {
                        id = game.adjudication.nextId++, inmateId = inmates[i].thingIDNumber, inmateName = inmates[i].LabelShort, offence = offences[i], tick = Find.TickManager.TicksGame - (6 + 5 * i) * GenDate.TicksPerHour,
                        officer = i == 0 && guard != null, officerName = i == 0 ? guard?.LabelShort : null, camera = i <= 1, weapon = i == 0, record = i == 0 ? 2 : 0, victimId = i == 0 && guard != null ? guard.thingIDNumber : -1, victimName = i == 0 ? guard?.LabelShort : null,
                    };
                    // (The charge needs the prosecution's file; the tour's is the bare scene.)
                    if (i == 0 && guard != null) k.scene = new MurderTrial { offence = offences[0], killerId = k.inmateId, killerName = k.inmateName, victimId = guard.thingIDNumber, victimName = guard.LabelShort, staffVictim = true, weapon = true, camera = true };
                    game.adjudication.cases.Add(k);
                    cases.Add(k);
                    c.admittedTick = System.Math.Min(c.admittedTick, k.tick - GenDate.TicksPerDay);
                }
                Adjudication.SendSheet(game, map, cases).OpenLetter();
            }));
            steps.Add(("adjudication-window", () =>
            {
                // The window the letter opens: each case with its evidence and the four answers (the days to add on the third, the charge lit for the armed assault on staff, loss of remission greyed with its reason).
                foreach (Dialog_NodeTree d in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) d.Close(doCloseSound: false);
                PrisonGame game = PrisonGame.Instance;
                int sheet = game.adjudication.cases.FirstOrDefault()?.sheet ?? -1;
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(Adjudication.DecisionDef)).ToList()) Find.LetterStack.RemoveLetter(l);
                Find.WindowStack.Add(new Dialog_Adjudication(map, sheet));
            }));
            steps.Add(("adjudication-file", () =>
            {
                // The file of the inmate whose case was answered with days added: the sentence bar with the days added, and the line for what the sheet did.
                Find.WindowStack.TryRemove(typeof(Dialog_Adjudication), doCloseSound: false);
                PrisonGame game = PrisonGame.Instance;
                if (game.adjudication.cases.FirstOrDefault(k => k.answer == AdjudicationAnswer.Pending) is not { } k || CourtUtil.FindInmate(map, k.inmateId) is not { } p)
                {
                    Log.Error("[RimPrison] ui tour: no case on the sheet to answer");
                    return;
                }
                Adjudication.Answer(game, map, k, AdjudicationAnswer.DaysAdded, 10);
                CameraJumper.TryJumpAndSelect(p);
                if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_InmateFile);
            }));
            steps.Add(("adjudication-back", () =>
            {
                // (The game's own book, policy and days added back: the tour's cases were never.)
                Clear();
                Find.WindowStack.TryRemove(typeof(Dialog_Adjudication), doCloseSound: false);
                PrisonGame game = PrisonGame.Instance;
                if (tourAdjBook != null) (game.adjudication, game.adjudicate) = (tourAdjBook, tourAdjPolicy);
                tourAdjBook = null;
                if (tourAdjAdded != null)
                    foreach ((CompInmate c, int added, int admitted) in tourAdjAdded) (c.daysAdded, c.admittedTick) = (added, admitted);
                tourAdjAdded = null;
            }));
            steps.Add(("custody-offer", () =>
            {
                // The offer as the warden gets it: a new book, the offer made (its dossier, the fees and bonuses, what it asks of the classroom and the counsellors against what the prison has, the beds, the rules), its letter opened.
                Clear();
                PrisonGame game = PrisonGame.Instance;
                tourCustodyBook = game.custody;
                tourCustodyOffers = game.stats.custodyOffers;
                game.custody = new CustodyBook();
                if (CustodyContracts.Offer(game, map) == null)
                {
                    Log.Error("[RimPrison] ui tour: no custody offer could be made");
                    return;
                }
                if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_CustodyContract")) is { } letter) letter.OpenLetter();
            }));
            steps.Add(("custody-progress", () =>
            {
                // The Intake tab with a contract half way through: one serving (day 3, two of four sessions), one whose bonus was paid, one who left and forfeited it, one still on the way.
                Clear();
                PrisonGame game = PrisonGame.Instance;
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_CustodyContract")).ToList()) Find.LetterStack.RemoveLetter(l);
                CustodyContractDef def = CustodyContracts.Def;
                SecurityLevelDef min = def.levels[0], med = def.levels[def.levels.Count - 1];
                int now = Find.TickManager.TicksGame;
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null).Take(2).ToList();
                CustodyMember Row(SecurityLevelDef level, CustodyState state, int days, int sessions, Pawn who, string name, string why = null) => new CustodyMember
                {
                    level = level, crime = level.crimes.FirstOrDefault(), termDays = 12, fee = level == min ? 150 : 250, bonus = level == min ? 450 : 750, state = state, pawn = state == CustodyState.Serving ? who : null, pawnId = who?.thingIDNumber ?? -1,
                    stay = who?.Inmate()?.timesAdmitted ?? 1, admittedTick = state == CustodyState.Pending ? -1 : now - days * GenDate.TicksPerDay, dueTick = state == CustodyState.Pending ? -1 : now - days * GenDate.TicksPerDay + 7 * GenDate.TicksPerDay,
                    sessions = sessions, paid = state == CustodyState.Pending ? 0 : level == min ? 150 : 250, bonusPaid = state == CustodyState.Met ? 450 : 0, name = name ?? who?.LabelShort, why = why,
                };
                var members = new List<CustodyMember>
                {
                    Row(min, CustodyState.Serving, 3, 2, inmates.ElementAtOrDefault(0), null),
                    Row(med, CustodyState.Serving, 1, 0, inmates.ElementAtOrDefault(1), null),
                    Row(min, CustodyState.Met, 8, 4, null, "Dale"),
                    Row(med, CustodyState.Forfeited, 4, 1, null, "Rhys", "RP_CustodyEnd_Transferred"),
                    Row(min, CustodyState.Pending, 0, 0, null, null),
                };
                game.custody = new CustodyBook
                {
                    nextId = 5, nextOfferDay = Ledger.CurrentDay + 8,
                    contract = new CustodyContract { id = 4, def = def, mapId = map.uniqueID, acceptedTick = now - 5 * GenDate.TicksPerDay, arrivalDeadlineTick = now + GenDate.TicksPerDay, extensionDays = 1, serviceDays = 7, requiredSessions = 4, members = members, paidAdmission = 950, paidBonus = 450 },
                };
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intake);
            }));
            steps.Add(("custody-back", () =>
            {
                // (The game's own book back: the tour's offer and contract were never.)
                Clear();
                PrisonGame game = PrisonGame.Instance;
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_CustodyContract")).ToList()) Find.LetterStack.RemoveLetter(l);
                if (tourCustodyBook != null)
                {
                    game.custody = tourCustodyBook;
                    game.stats.custodyOffers = tourCustodyOffers;
                }
                tourCustodyBook = null;
            }));
            // RIMPRISON_TOUR=custody: the offer's letter and the Intake tab's contract rows. Not in the whole tour: it swaps the custody book.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "custody") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("custody-"));
            else steps.RemoveAll(x => x.name.StartsWith("custody-"));
            steps.Add(("informant-file", () =>
            {
                // A known informant set four days ago to watch the inmate with the most against them (a suspect if there is one): the file with the task's line and the "Task..." button.
                Clear();
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { activeFlag: true, security: not null }).ToList();
                if (inmates.Count < 2)
                {
                    Log.Error("[RimPrison] ui tour: no two inmates for an informant's task");
                    return;
                }
                Pawn informant = inmates.FirstOrDefault(p => !p.Inmate().gangLeader && p.Inmate().gang == null) ?? inmates[0];
                CompInmate c = informant.Inmate();
                tourInformant = (c, c.reputations.ToList(), c.revealed.ToList(), c.snitchExposed, c.informantQuit, c.taskTargetId, c.taskSinceTick);
                if (!c.reputations.Contains(ReputationDefOf.RP_Snitch)) c.reputations.Add(ReputationDefOf.RP_Snitch);
                if (!c.revealed.Contains(ReputationDefOf.RP_Snitch)) c.revealed.Add(ReputationDefOf.RP_Snitch);
                (c.snitchExposed, c.informantQuit) = (false, false);
                if (InformantTask.Targets(informant, c).FirstOrDefault().pawn is not { } target)
                {
                    Log.Error("[RimPrison] ui tour: nobody for the informant to watch");
                    return;
                }
                InformantTask.Assign(informant, c, target);
                c.taskSinceTick = System.Math.Max(0, Find.TickManager.TicksGame - 4 * GenDate.TicksPerDay);
                CameraJumper.TryJumpAndSelect(informant);
                if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_InmateFile);
            }));
            steps.Add(("informant-menu", () =>
            {
                // The "Task..." menu over the file: pull out first, then the suspects with what the desk knows of them, then the rest.
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                if (tourInformant is { } t && t.c.parent is Pawn informant) Find.WindowStack.Add(new FloatMenu(InformantTask.TaskOptions(informant, t.c), "RP_InformantTask_MenuTitle".Translate(informant.LabelShort)));
            }));
            steps.Add(("informant-move", () =>
            {
                // The second step, as choosing the target opens it: into the target's cell, onto their wing, or where they are (each with why not).
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                if (tourInformant is { } t && t.c.parent is Pawn informant && InformantTask.TargetOf(informant, t.c) is { } target)
                    Find.WindowStack.Add(new FloatMenu(InformantTask.MoveOptions(informant, t.c, target), "RP_InformantTask_MoveTitle".Translate(informant.LabelShort, target.LabelShort)));
            }));
            steps.Add(("informant-intel", () =>
            {
                // Intel's informants block: the task under the informant's row (the target, the days in place, the exposure band) and the "Pull out" button.
                Clear();
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intel);
            }));
            steps.Add(("informant-back", () =>
            {
                // (The informant's file as it was: the tour's task was never.)
                Clear();
                if (tourInformant is { } t)
                {
                    t.c.reputations = t.reps;
                    t.c.revealed = t.revealed;
                    (t.c.snitchExposed, t.c.informantQuit, t.c.taskTargetId, t.c.taskSinceTick) = (t.exposed, t.quit, t.target, t.since);
                }
                tourInformant = null;
            }));
            // RIMPRISON_TOUR=informant: the file with a task, the target menu, the move menu, Intel's task row. Not in the whole tour: it makes an inmate an informant.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "informant") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("informant-"));
            else steps.RemoveAll(x => x.name.StartsWith("informant-"));
            steps.Add(("unitofficer-row", () =>
            {
                // A guard named three and a half days ago to the first wing with an inmate on it (the medium wing if it has one): the Deployment tab scrolled to the unit officers' block.
                Clear();
                SecurityLevelDef wing = new[] { RPSecurityDefOf.RP_Medium }.Concat(DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(l => l.uiOrder))
                    .FirstOrDefault(l => UnitOfficers.WingZone(l) != null && map.mapPawns.PrisonersOfColonySpawned.Any(p => p.Inmate()?.security == l));
                Pawn guard = map.mapPawns.FreeColonistsSpawned.Where(p => p.GetComp<CompStaff>() is { role.guardDuties: true, Protected: false, leaveAtTick: < 0 }).OrderBy(p => p.thingIDNumber)
                    .FirstOrDefault(p => wing != null && UnitOfficers.Refusal(p, wing) == null);
                if (guard == null || wing == null)
                {
                    Log.Error($"[RimPrison] ui tour: no guard to name a unit officer (wing {wing?.defName})");
                    return;
                }
                CompStaff sc = guard.GetComp<CompStaff>();
                tourUnit = (guard, sc.unitWing, sc.unitSinceTick, sc.unitPrevZone, sc.patrolZone);
                // (A real officer of the wing, whom naming the tour's takes off, is put back too.)
                tourUnitHolder = UnitOfficers.HolderOf(map, wing) is { } was && was != guard && was.GetComp<CompStaff>() is { } wc ? (was, wc.unitWing, wc.unitSinceTick, wc.unitPrevZone, wc.patrolZone) : null;
                UnitOfficers.Assign(guard, wing, message: false);
                sc.unitSinceTick = System.Math.Max(0, Find.TickManager.TicksGame - 7 * GenDate.TicksPerDay / 2);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Deployment);
                DeploymentTab.leftScroll = new Vector2(0f, Mathf.Max(0f, DeploymentTab.unitBlockY - 20f));
            }));
            steps.Add(("unitofficer-menu", () =>
            {
                // The slot's menu over the tab: none (taking the officer off), then every guard with their shift; a guard who cannot be named is greyed with the reason.
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                DeploymentTab.leftScroll = new Vector2(0f, Mathf.Max(0f, DeploymentTab.unitBlockY - 20f));
                if (tourUnit is { } t && t.guard.GetComp<CompStaff>()?.unitWing is { } wing)
                    Find.WindowStack.Add(new FloatMenu(UnitOfficers.SlotOptions(map, wing), "RP_Unit_MenuTitle".Translate(UnitOfficers.WingLabel(wing))));
            }));
            steps.Add(("unitofficer-record", () =>
            {
                // The officer's staff record: the unit officer line (the wing, the days in post, the shift).
                Clear();
                if (tourUnit is not { } t) return;
                CameraJumper.TryJumpAndSelect(t.guard);
                if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_StaffRecord);
            }));
            steps.Add(("unitofficer-back", () =>
            {
                // (The guard as they were: the tour's post was never.)
                Clear();
                if (tourUnit is { } t && t.guard.GetComp<CompStaff>() is { } sc) (sc.unitWing, sc.unitSinceTick, sc.unitPrevZone, sc.patrolZone) = (t.wing, t.since, t.prev, t.patrol);
                if (tourUnitHolder is { } h && h.guard.GetComp<CompStaff>() is { } hc) (hc.unitWing, hc.unitSinceTick, hc.unitPrevZone, hc.patrolZone) = (h.wing, h.since, h.prev, h.patrol);
                (tourUnit, tourUnitHolder) = (null, null);
            }));
            // RIMPRISON_TOUR=unitofficer: the Deployment tab's unit officer row and its menu, the officer's staff record. Not in the whole tour: it names a guard.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "unitofficer") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("unitofficer-"));
            else steps.RemoveAll(x => x.name.StartsWith("unitofficer-"));
            steps.Add(("rumours-intel", () =>
            {
                // Intel's Rumours list: the yard rumour on the first wing with inmates (true, who started it known, all over the wing), a false supermax rumour on the next (not known yet, spreading) with the warden's
                // three buttons under each, and a death rumour overtaken an hour ago ("the guards killed Hale", and true). Near tick 0: no tick before 0.
                Clear();
                PrisonMap pm = PrisonMap.Of(map);
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && p.Inmate() is { security.escortOnly: false }).ToList();
                if (pm == null || inmates.Count == 0)
                {
                    Log.Error("[RimPrison] ui tour: no inmates for the rumours");
                    return;
                }
                tourRumours ??= pm.rumours;
                Pawn origin = inmates[0];
                tourRumourOrigin = origin;
                SecurityLevelDef first = origin.Inmate().security;
                SecurityLevelDef second = inmates.Select(p => p.Inmate().security).FirstOrDefault(l => l != first)
                    ?? DefDatabase<SecurityLevelDef>.AllDefsListForReading.First(l => l != first && !l.escortOnly);
                Pawn secondOrigin = inmates.FirstOrDefault(p => p.Inmate().security == second);
                int now = Find.TickManager.TicksGame, hour = GenDate.TicksPerHour, day = GenDate.TicksPerDay;
                pm.rumours = new List<Rumour>
                {
                    new Rumour { kind = RumourKind.YardOff, level = first, isTrue = true, reach = 0.9f, startTick = Mathf.Max(0, now - 5 * hour), expireTick = now + day + 20 * hour,
                        originId = origin.thingIDNumber, originName = origin.LabelShort, originKnown = true, truthKnown = true },
                    new Rumour { kind = RumourKind.Supermax, level = second, isTrue = false, distorted = true, reach = 0.45f, startTick = Mathf.Max(0, now - 3 * hour), expireTick = now + day + 21 * hour,
                        originId = secondOrigin?.thingIDNumber ?? -1, originName = secondOrigin?.LabelShort ?? "Hale" },
                    new Rumour { kind = RumourKind.Death, level = first, isTrue = true, distorted = true, reach = 0.6f, subject = "Hale", startTick = Mathf.Max(0, now - 8 * hour), expireTick = now + day,
                        endTick = Mathf.Max(0, now - hour), end = RumourEnd.Replaced, originName = "Gus", truthKnown = true },
                };
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intel);
            }));
            steps.Add(("rumours-file", () =>
            {
                // The file of the one who started the yard rumour: the tension cause with what the wing is saying, and the line "the wing is saying ... - they started it".
                Clear();
                if (tourRumourOrigin is { Spawned: true } p)
                {
                    CompInmate c = p.Inmate();
                    tourRumourMood ??= (c.tension, c.mainCause);
                    if (c.tension < 30f) c.tension = 45f;
                    c.mainCause = "RP_Cause_Rumour";
                    CameraJumper.TryJumpAndSelect(p);
                    if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                    if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_InmateFile);
                }
            }));
            steps.Add(("rumours-back", () =>
            {
                // (The map's own rumours back: the tour's were never told.)
                Clear();
                if (tourRumours != null && PrisonMap.Of(map) is { } pm) pm.rumours = tourRumours;
                if (tourRumourOrigin?.Inmate() is { } oc && tourRumourMood is { } mood) (oc.tension, oc.mainCause) = mood;
                tourRumours = null;
                tourRumourMood = null;
                tourRumourOrigin = null;
            }));
            steps.Add(("asks-hud", () =>
            {
                // The prison panel's line "4 ask to see you" (its tip names them): a hunger striker, a debtor after a beating, someone of a wing a false rumour runs on (the rumour made up too), a cell request nearly lapsed.
                // Paused, so that no hour drops a request whose reason the tour made up. Near tick 0: no tick before 0.
                Clear();
                PrisonMap pm = PrisonMap.Of(map);
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && p.Inmate() is { security.escortOnly: false }).Take(4).ToList();
                if (pm == null || inmates.Count == 0)
                {
                    Log.Error("[RimPrison] ui tour: no inmates for the requests to see the warden");
                    return;
                }
                tourAsks ??= (pm.asks, pm.rumours, Find.TickManager.CurTimeSpeed);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                int now = Find.TickManager.TicksGame, hour = GenDate.TicksPerHour, day = GenDate.TicksPerDay;
                WardenAskReason[] reasons = { WardenAskReason.HungerStrike, WardenAskReason.Debtor, WardenAskReason.Rumour, WardenAskReason.CellRequest };
                int[] ago = { 2, 9, 1, 20 };
                pm.asks = inmates.Select((p, i) => new WardenAsk { inmateId = p.thingIDNumber, reason = reasons[i], tick = Mathf.Max(0, now - ago[i] * hour) }).ToList();
                SecurityLevelDef wing = inmates[Mathf.Min(2, inmates.Count - 1)].Inmate().security;
                // (The rumour starts the tick its request was made: a request is tied to its rumour by that, see WardenAsks.RumourOf.)
                int rumourAsked = pm.asks[Mathf.Min(2, pm.asks.Count - 1)].tick;
                pm.rumours = new List<Rumour>
                {
                    new Rumour { kind = RumourKind.YardOff, level = wing, isTrue = false, distorted = true, reach = 0.4f, startTick = rumourAsked, expireTick = now + day, originName = "Hale" },
                };
                PrisonHUD.Invalidate();
            }));
            steps.Add(("asks-intel", () =>
            {
                // Intel's list: who asks, why, the hours left, and "Go" (the word, or the walk of the wing for the rumour).
                Clear();
                PrisonHUD.Invalidate();
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intel);
            }));
            steps.Add(("asks-back", () =>
            {
                // (The map's own requests and rumours back: the tour's were never made.)
                Clear();
                AsksBack();
            }));
            // RIMPRISON_TOUR=asks: the prison panel's count of who asks to see the warden, and Intel's list of them. Not in the whole tour: it swaps the map's requests and rumours.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "asks") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("asks-"));
            else steps.RemoveAll(x => x.name.StartsWith("asks-"));
            // RIMPRISON_TOUR=rumours: Intel's Rumours list and the file of the one who started one. Not in the whole tour: it swaps the map's rumours.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "rumours") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("rumours-"));
            else steps.RemoveAll(x => x.name.StartsWith("rumours-"));
            // The watch list (Programs/WatchList.cs): the letter of a flag, Intel's list with an inmate on watch, one in counselling, one nothing was done for and the open letter, and the file's line. Every tick is the clock's own or
            // later (a tour game starts near tick 0).
            steps.Add(("watch-letter", () =>
            {
                Clear();
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && !ShuttleSystem.OnTheWayOut(p) && p.Inmate().riskTick < 0).Take(4).ToList();
                Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(g => g.GetComp<CompStaff>()?.role is { guardDuties: true, warden: false });
                if (inmates.Count < 4 || guard == null)
                {
                    Log.Error($"[RimPrison] ui tour: no four inmates and a guard for the watch list ({inmates.Count}, guard {guard != null})");
                    return;
                }
                // (The hour's pass leaves them alone while the tour shows them.)
                WatchList.Pool = _ => false;
                tourWatch = inmates.Select(p =>
                {
                    CompInmate c = p.Inmate();
                    return (p, new[] { c.riskTick, c.watchUntilTick, c.crisisCount, c.riskEventTick, c.lowMoodTick, c.watchCheckTick, c.counselTick, c.riskEndTick, c.measureTick }, c.riskEvent, c.riskCause, c.watchAnswer);
                }).ToList();
                Pawn first = inmates[0];
                WatchList.Notify_Trigger(first, RiskTrigger.DearJohn);
                WatchList.Flag(first, first.Inmate(), map, guard, RiskFlagger.Guard).OpenLetter();
            }));
            steps.Add(("watch-intel", () =>
            {
                Clear();
                if (tourWatch == null) return;
                Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(g => g.GetComp<CompStaff>()?.role is { guardDuties: true, warden: false });
                int now = Find.TickManager.TicksGame;
                var answers = new[] { WatchAnswer.Watch, WatchAnswer.Counselling, WatchAnswer.Nothing };
                var causes = new[] { RiskTrigger.ParoleRefused, RiskTrigger.FamilyDeath, RiskTrigger.DebtBeating };
                for (int i = 1; i < tourWatch.Count; i++)
                {
                    Pawn p = tourWatch[i].p;
                    CompInmate c = p.Inmate();
                    WatchList.Notify_Trigger(p, causes[i - 1]);
                    WatchList.Flag(p, c, map, guard ?? p, RiskFlagger.Guard);
                    // (Answered: the letter is gone from the stack, and the period is a day or so along.)
                    foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(WatchList.DecisionDef) && l.subjectId == p.thingIDNumber).ToList())
                        Find.LetterStack.RemoveLetter(l);
                    // (Held back by the day's budget, the letter is taken off the held ones as well.)
                    PrisonGame.Instance?.story.deferredLetters.RemoveAll(l => l.Is(WatchList.DecisionDef) && l.subjectId == p.thingIDNumber);
                    (c.watchAnswer, c.watchUntilTick, c.watchCheckTick, c.measureTick, c.crisisCount) = (answers[i - 1], now + (int)((1.2f + 0.6f * i) * GenDate.TicksPerDay), now, now, i == 3 ? 1 : 0);
                }
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intel);
                IntelTab.TourScrollToWatch = true;
            }));
            steps.Add(("watch-file", () =>
            {
                // The file of the inmate on watch: the status line with the answer and the days left.
                Clear();
                if (tourWatch is not { Count: > 1 }) return;
                CameraJumper.TryJumpAndSelect(tourWatch[1].p);
                if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_InmateFile);
            }));
            steps.Add(("watch-back", () =>
            {
                // (The inmates as they were: the tour's flags were never.)
                Clear();
                IntelTab.TourScrollToWatch = false;
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(WatchList.DecisionDef)).ToList()) Find.LetterStack.RemoveLetter(l);
                // (And the ones the day's budget held back: they would reach the desk at midnight.)
                PrisonGame.Instance?.story.deferredLetters.RemoveAll(l => l.Is(WatchList.DecisionDef));
                if (tourWatch != null)
                    foreach ((Pawn p, int[] t, RiskTrigger ev, RiskTrigger cause, WatchAnswer answer) in tourWatch)
                    {
                        CompInmate c = p.Inmate();
                        (c.riskTick, c.watchUntilTick, c.crisisCount, c.riskEventTick, c.lowMoodTick, c.watchCheckTick, c.counselTick, c.riskEndTick, c.measureTick) = (t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8]);
                        (c.riskEvent, c.riskCause, c.watchAnswer) = (ev, cause, answer);
                    }
                tourWatch = null;
                WatchList.Pool = null;
            }));
            // RIMPRISON_TOUR=watch: the letter, Intel's watch list and the file's line. Not in the whole tour: it flags inmates.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "watch") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("watch-"));
            else steps.RemoveAll(x => x.name.StartsWith("watch-"));
            // RIMPRISON_TOUR=adjudication: the sheet's letter, its window and the file's sentence line. Not in the whole tour: it swaps the book and gives days.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "adjudication") steps.RemoveAll(x => x.name is not ("map" or "adjudication-letter" or "adjudication-window" or "adjudication-file" or "adjudication-back"));
            else steps.RemoveAll(x => x.name.StartsWith("adjudication-"));
            steps.Add(("release-plan", () =>
            {
                // The Intake tab's release planning: one inmate two days from release with lodging arranged, one three days from a hearing, one four days from release; and the cases under review, one of them mid-setback with its help button.
                Clear();
                PrisonGame game = PrisonGame.Instance;
                tourReleaseBook = game.supervision;
                // (Paused while the tour's cases are in the book: an hourly pass would review them - the setback below would end in a rearrest on the real queue, with the clawback from the real ledger.)
                tourReleaseSpeed0 ??= Find.TickManager.CurTimeSpeed;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                int now = Find.TickManager.TicksGame, day = GenDate.TicksPerDay;
                tourReleaseInmates = new List<(CompInmate, int, int, int, int, ReleaseSupport)>();
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { activeFlag: true, security.escortOnly: false } && !ShuttleSystem.OnTheWayOut(p)).Take(3).ToList();
                float[] left = { 2f, 23f, 4f };
                for (int i = 0; i < inmates.Count; i++)
                {
                    CompInmate c = inmates[i].Inmate();
                    tourReleaseInmates.Add((c, c.sentenceDays, c.daysAdded, c.admittedTick, c.lastHearingTick, c.releasePlan));
                    (c.sentenceDays, c.daysAdded, c.admittedTick, c.lastHearingTick) = (40, 0, now - Mathf.RoundToInt((40f - left[i]) * day), i == 0 || i == 2 ? now : -999999);
                    if (i == 0) c.releasePlan = ReleaseSupport.Lodging;
                }
                SupervisionCase Row(int id, string name, CaseState state)
                {
                    // (No review falls due while the tour's cases are shown, should the clock run all the same.)
                    return new SupervisionCase { version = 1, id = id, name = name, state = state, paroled = true, releasedTick = now - 4 * day, departedTick = now - 3 * day, nextReviewTick = now + 999 * day, endTick = now + 999 * day, reviewDays = 7, reviewsRolled = 3, reviewsHeld = 3, baseRisk = 0.4f };
                }
                SupervisionCase dale = Row(1, "Dale", CaseState.Reviewing), rhys = Row(2, "Rhys", CaseState.Reviewing), ines = Row(3, "Ines", CaseState.Awaiting), omar = Row(4, "Omar", CaseState.Resolved), pia = Row(5, "Pia", CaseState.Resolved);
                dale.setback = CaseSetback.JobLost;
                dale.setbackState = SetbackState.Pending;
                dale.setbackAt = 3;
                dale.reviewsHeld = 4;
                dale.addressSecured = dale.addressByLodging = dale.supportDone = dale.supportFulfilled = true;
                dale.support = ReleaseSupport.Lodging;
                rhys.jobSecured = rhys.jobByInterview = rhys.addressSecured = true;
                ines.support = ReleaseSupport.Lodging;
                ines.departedTick = -1;
                omar.outcome = CaseOutcome.StayedOut;
                omar.reviewsRolled = 7;
                pia.outcome = CaseOutcome.Rearrested;
                pia.reason = "RP_Violation_JobLost";
                game.supervision = new SupervisionBook { nextId = 6, cases = new List<SupervisionCase> { dale, rhys, ines, omar, pia } };
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intake);
                IntakeTab.ScrollToReleasePrep();
            }));
            steps.Add(("release-file", () =>
            {
                // The file of the inmate with lodging arranged: the release-planning row.
                Clear();
                if (tourReleaseInmates is { Count: > 0 } && tourReleaseInmates[0].c.Pawn is { } p)
                {
                    CameraJumper.TryJumpAndSelect(p);
                    if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                    if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_InmateFile);
                }
            }));
            steps.Add(("release-back", () =>
            {
                // (The game's own book, sentences and plans back: the tour's cases and dates were never.)
                Clear();
                PrisonGame game = PrisonGame.Instance;
                if (tourReleaseBook != null) game.supervision = tourReleaseBook;
                tourReleaseBook = null;
                if (tourReleaseInmates != null)
                    foreach ((CompInmate c, int sentence, int added, int admitted, int lastHearing, ReleaseSupport plan) in tourReleaseInmates) (c.sentenceDays, c.daysAdded, c.admittedTick, c.lastHearingTick, c.releasePlan) = (sentence, added, admitted, lastHearing, plan);
                tourReleaseInmates = null;
                if (tourReleaseSpeed0 is { } speed0) Find.TickManager.CurTimeSpeed = speed0;
                tourReleaseSpeed0 = null;
            }));
            // RIMPRISON_TOUR=release: the Intake tab's release planning and the file's row. Not in the whole tour: it swaps the book and moves sentences.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "release") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("release-"));
            else steps.RemoveAll(x => x.name.StartsWith("release-"));
            steps.Add(("riotreview-letter", () =>
            {
                // The morning after: the incident report of a riot (the ringleader and one more seen, the rest a count, the hurt, the damage bill, a real grievance, what the prison lacked) and its answers, the letter opened.
                Clear();
                PrisonGame game = PrisonGame.Instance;
                tourRiotBook ??= game.riots;
                game.riots = new RiotBook();
                // (Not one on the shuttle's list: the ringleader would be gone for "ship out".)
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && !ShuttleSystem.OnTheWayOut(p)).Take(4).ToList();
                if (inmates.Count < 2)
                {
                    Log.Error("[RimPrison] ui tour: no two inmates for the riot review");
                    return;
                }
                TourRiotClockOn(map);
                RiotRecord rec = TourRiot(game, map, inmates, 7);
                rec.letterTick = -1;
                RiotReview.SendLetter(game, map, rec).OpenLetter();
            }));
            steps.Add(("riotreview-intel", () =>
            {
                // Intel's riots: one restricted two days and charged, one negotiated with restitution and a second answer open, one amnesty, one with no reckoning, one waiting for the letter, one still going.
                Clear();
                PrisonGame game = PrisonGame.Instance;
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(RiotReview.DecisionDef) || l.Is(RiotReview.MoreDecisionDef)).ToList()) Find.LetterStack.RemoveLetter(l);
                tourRiotBook ??= game.riots;
                game.riots = new RiotBook();
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(InmateUtility.IsActiveInmate).ToList();
                if (inmates.Count < 4)
                {
                    Log.Error("[RimPrison] ui tour: no four inmates for the riot list");
                    return;
                }
                TourRiotClockOn(map);
                RiotRecord amnesty = TourRiot(game, map, inmates.Skip(2).Take(2).ToList(), 140);
                (amnesty.answers, amnesty.done, amnesty.amnestyUntilTick) = (new List<RiotAnswer> { RiotAnswer.Amnesty }, true, Find.TickManager.TicksGame + GenDate.TicksPerDay);
                RiotRecord none = TourRiot(game, map, inmates.Skip(1).Take(3).ToList(), 100);
                none.done = true;
                none.participants[0].seen = false;
                RiotRecord restricted = TourRiot(game, map, inmates.Take(4).ToList(), 60);
                (restricted.answers, restricted.done, restricted.restrictDays, restricted.charged) = (new List<RiotAnswer> { RiotAnswer.Restrict, RiotAnswer.Charge }, true, 2, 2);
                RiotRecord deal = TourRiot(game, map, inmates.Skip(1).Take(2).ToList(), 30);
                (deal.negotiated, deal.answers, deal.restitution) = (true, new List<RiotAnswer> { RiotAnswer.Restitution }, 210);
                RiotReview.SendMore(map, deal);
                RiotRecord waiting = TourRiot(game, map, inmates.Take(3).ToList(), 9);
                waiting.letterTick = -1;
                RiotReview.SendLetter(game, map, waiting);
                RiotRecord going = TourRiot(game, map, inmates.Skip(2).Take(2).ToList(), 1);
                (going.endTick, going.letterTick, going.lastRiotTick) = (-1, -1, Find.TickManager.TicksGame);
                foreach (Dialog_NodeTree d in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) d.Close(doCloseSound: false);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intel);
                IntelTab.TourScrollToRiots = true;
            }));
            steps.Add(("riotreview-back", () =>
            {
                // (The game's own book back: the tour's riots were never.)
                Clear();
                IntelTab.TourScrollToRiots = false;
                foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(RiotReview.DecisionDef) || l.Is(RiotReview.MoreDecisionDef)).ToList()) Find.LetterStack.RemoveLetter(l);
                if (tourRiotBook != null) PrisonGame.Instance.riots = tourRiotBook;
                tourRiotBook = null;
                TourRiotClockBack();
            }));
            // RIMPRISON_TOUR=riotreview: the morning letter and the Intel list. Not in the whole tour: it swaps the riot book.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "riotreview") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("riotreview-"));
            else steps.RemoveAll(x => x.name.StartsWith("riotreview-"));
            // Standing orders and the desk (Story/StandingOrders.cs, the overview's desk). The tour's letters name real inmates but hold no pawns: an answer to them does nothing.
            ChoiceLetter_PrisonDecision StandingLetter(string decision, string label, string text, int hours)
            {
                ChoiceLetter_PrisonDecision l = ChoiceLetter_PrisonDecision.Make(decision, label, text, map);
                if (hours > 0) l.StartTimeout(hours * GenDate.TicksPerHour);
                tourStandingLetters.Add(l);
                return l;
            }
            ChoiceLetter_PrisonDecision MentorLetter(int pair)
            {
                List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { security: not null }).ToList();
                Pawn old = inmates.ElementAtOrDefault(2 * pair), young = inmates.ElementAtOrDefault(2 * pair + 1);
                string a = old?.LabelShort ?? "Dale", b = young?.LabelShort ?? "Rhys";
                return StandingLetter("RP_Decision_Mentor", "RP_MentorLabel".Translate(a, b),
                    "RP_MentorText".Translate(a, old?.ageTracker.AgeBiologicalYears ?? 52, (old?.Inmate()?.DaysServed ?? 9f).ToString("0"), b, young?.ageTracker.AgeBiologicalYears ?? 21, Mentors.Days), 12);
            }
            steps.Add(("standing-letter", () =>
            {
                // A mentorship letter with "Do this from now on" ticked beside its answers.
                Clear();
                PrisonGame game = PrisonGame.Instance;
                tourStanding ??= (new Dictionary<string, string>(game.standingOrders), game.story.deferredLetters, game.story.decisionsToday, game.standingAnswered.ToList(), StoryLetters.TestBudget);
                game.standingOrders.Clear();
                game.story.deferredLetters = new List<ChoiceLetter_PrisonDecision>();
                ChoiceLetter_PrisonDecision letter = MentorLetter(0);
                letter.standingTicked = true;
                Find.LetterStack.ReceiveLetter(letter);
                letter.OpenLetter();
            }));
            steps.Add(("standing-policy", () =>
            {
                // The Policy tab's standing orders: three of them, each with "Lift".
                Clear();
                PrisonGame game = PrisonGame.Instance;
                foreach (ChoiceLetter_PrisonDecision l in tourStandingLetters.Where(OnTheStack).ToList()) Find.LetterStack.RemoveLetter(l);
                game.standingOrders["RP_Decision_Mentor"] = "RP_Mentor_Apart";
                game.standingOrders["RP_Decision_Funeral"] = "RP_Funeral_Quiet";
                game.standingOrders["RP_Decision_Adjudication"] = "RP_Adjudication_Policy";
                PolicyTab.showStanding = true;
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Policy);
            }));
            steps.Add(("standing-punishments", () =>
            {
                // The same tab on its punishments (the switch at the column's head): the rows in a scroll view, from the top.
                Clear();
                PolicyTab.showStanding = false;
                PolicyTab.punishScroll = Vector2.zero;
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Policy);
            }));
            steps.Add(("standing-punishments-end", () =>
            {
                // Scrolled to the foot (the scroll view keeps it in range): the last misconducts (drug use, a refused test, a false complaint) are shown and can be set.
                Clear();
                PolicyTab.showStanding = false;
                PolicyTab.punishScroll = new Vector2(0f, 100_000f);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Policy);
            }));
            steps.Add(("standing-answered", () =>
            {
                // A later mentorship letter, answered at once: the message with its "Review" link over the map.
                Clear();
                if (Find.MainTabsRoot.OpenTab != null) Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                ChoiceLetter_PrisonDecision letter = MentorLetter(1);
                Find.LetterStack.ReceiveLetter(letter);
                tourStandingAnswered = letter.answered ? letter : null;
                if (tourStandingAnswered == null) Log.Error("[RimPrison] ui tour: the standing order did not answer the mentorship letter");
            }));
            steps.Add(("standing-review", () =>
            {
                // The review: the letter as it was answered, read-only (only "close").
                Clear();
                StandingOrders.Review(tourStandingAnswered);
            }));
            steps.Add(("standing-desk", () =>
            {
                // The overview's desk: two open letters (one urgent), one held for midnight (held since a day ago, or since the game began), the day's budget used up, the lately answered under them.
                Clear();
                PrisonGame game = PrisonGame.Instance;
                StoryLetters.TestBudget = true;
                Find.LetterStack.ReceiveLetter(StandingLetter("RP_Decision_TalentShow", "RP_TalentShowLabel".Translate(), "RP_TalentShowText".Translate(Money.Format(IncidentWorker_TalentShow.Cost)), 20));
                Find.LetterStack.ReceiveLetter(StandingLetter("RP_Decision_FamilyDay", "RP_FamilyDayLabel".Translate(), "RP_FamilyDayLabel".Translate(), 6));
                int now = Find.TickManager.TicksGame;
                ChoiceLetter_PrisonDecision held = StandingLetter("RP_Decision_ExportContract", "RP_ContractLabel".Translate(), "RP_ContractLabel".Translate(), 0);
                (held.deferredTimeout, held.heldTick) = (24 * GenDate.TicksPerHour, System.Math.Max(0, now - GenDate.TicksPerDay));
                game.story.deferredLetters.Add(held);
                game.story.decisionsToday = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                OverviewTab.showDesk = true;
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Overview);
            }));
            steps.Add(("standing-back", () =>
            {
                // (The game's own orders, held letters and count back: the tour's letters were never.)
                Clear();
                StandingBack();
            }));
            // RIMPRISON_TOUR=standing: the letter's box, the Policy tab's orders and its punishments (top and foot of their scroll view), a letter answered with its review, the desk. Not in the whole tour: it swaps the orders and the held letters.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "standing") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("standing-"));
            else steps.RemoveAll(x => x.name.StartsWith("standing-"));
            // Pay by the grade (Economy/PerDiem.cs): the desk with a week of B grades on day 8, the grade's tooltip (what the B is worth a day), and the Finance screen with the day's per-diem line and its note.
            steps.Add(("perdiem-desk", () =>
            {
                Clear();
                Find.WindowStack.TryRemove(typeof(Dialog_MessageBox), doCloseSound: false);
                PrisonGame game = PrisonGame.Instance;
                tourPerDiem ??= (game.gradeHistory.ToList(), game.gradeRules.ToList(), game.gradeCards.ToList(), game.ledger.funds, game.ledger.recent.ToList(), game.ledger.days.ToList(),
                    game.ledger.days.Select(d => new Dictionary<LedgerCategoryDef, int>(d.totals)).ToList());
                TourPerDiemClockOn();
                game.gradeHistory.Clear();
                game.gradeHistory.AddRange(new[] { 58f, 62f, 78f, 74f, 81f, 69f, 77f, 80f, 72f });
                game.AlignGradeRules();
                PrisonHUD.Invalidate();
                OverviewTab.showWings = false;
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Overview);
            }));
            steps.Add(("perdiem-desk-tip", () =>
            {
                // (A tooltip needs the mouse over the grade, which a tour cannot do: its text is shown in a box, as the desk gives it.)
                PrisonGame game = PrisonGame.Instance;
                string tip = OverviewTab.GradeTip(PrisonGrading.Compute(map, game).Graded, PerDiem.DeskLine(game, map));
                Find.WindowStack.Add(new Dialog_MessageBox(tip, title: "The desk's grade, hovered"));
            }));
            steps.Add(("perdiem-finance", () =>
            {
                // The day's per-diem paid as at midnight (the ledger is put back at the end), its note saying what the week's B added.
                Find.WindowStack.TryRemove(typeof(Dialog_MessageBox), doCloseSound: false);
                DailySettlement.PayPerDiem(PrisonGame.Instance, map);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Finance);
            }));
            steps.Add(("perdiem-back", () =>
            {
                Clear();
                TourPerDiemBack();
            }));
            // RIMPRISON_TOUR=perdiem: the desk's grade tooltip and the ledger's line. Not in the whole tour: it moves the clock to day 8 and swaps the grades.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "perdiem") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("perdiem-"));
            else steps.RemoveAll(x => x.name.StartsWith("perdiem-"));
            // A restorative meeting (Story/Amends.cs): the counsellor's letter for an inmate inside for a violent crime with a program passed (the real letter, open with its two answers), then their file after a
            // meeting that went well (the family speaks for them; the day is now, never a tick below 0: a tour game starts near tick 0).
            steps.Add(("amends-letter", () =>
            {
                Clear();
                Pawn p = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { security: not null } c && InmateUtility.IsActiveInmate(x) && c.amendsState == AmendsState.None).OrderBy(x => x.thingIDNumber).FirstOrDefault();
                Pawn counsellor = Amends.Counsellor(map) ?? map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.GetComp<CompStaff>()?.role == RPProgramDefOf.RP_Counsellor);
                CrimeDef violent = DefDatabase<CrimeDef>.AllDefsListForReading.FirstOrDefault(d => d.violent);
                if (p == null || counsellor == null || violent == null)
                {
                    Log.Error($"[RimPrison] ui tour: no inmate ({p?.LabelShort}), counsellor ({counsellor?.LabelShort}) or violent crime for the restorative meeting");
                    return;
                }
                CompInmate c = p.Inmate();
                tourAmends = (p, c.crime, c.programsPassed, c.amendsState, c.amendsTick);
                c.crime = violent;
                c.programsPassed = System.Math.Max(1, c.programsPassed);
                tourAmendsLetter = Amends.Propose(PrisonGame.Instance, map, p, counsellor);
                tourAmendsLetter.OpenLetter();
            }));
            steps.Add(("amends-file", () =>
            {
                Clear();
                if (tourAmendsLetter != null)
                {
                    tourAmendsLetter.answered = true;
                    if (OnTheStack(tourAmendsLetter)) Find.LetterStack.RemoveLetter(tourAmendsLetter);
                }
                if (tourAmends is not { } was || !was.p.Spawned) return;
                CompInmate c = was.p.Inmate();
                (c.amendsState, c.amendsTick) = (AmendsState.Supports, System.Math.Max(0, Find.TickManager.TicksGame));
                CameraJumper.TryJumpAndSelect(was.p);
                if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_InmateFile);
            }));
            steps.Add(("amends-back", () =>
            {
                Clear();
                TourAmendsBack();
            }));
            // RIMPRISON_TOUR=amends: the counsellor's letter and the file after a good meeting. Not in the whole tour: it changes an inmate's crime and programs (put back).
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "amends") steps.RemoveAll(x => x.name != "map" && !x.name.StartsWith("amends-"));
            else steps.RemoveAll(x => x.name.StartsWith("amends-"));
            // The handover (Story/Handover.cs): the old warden's note is the welcome letter above. The Intel screen as the old warden left it (the secrets are hidden: the one thing on it is the guard's suspicion file on the staff
            // row), the chronicle's milestones with "before you" (the old warden's words) at the foot, and the overview's latest entries.
            steps.Add(("handover-intel", () =>
            {
                Clear();
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Intel);
            }));
            steps.Add(("handover-chronicle", () =>
            {
                // (Milestones only: the first day's forty admissions would push the old warden's lines off the page.)
                Clear();
                HarmonyLib.Traverse.Create(typeof(ChronicleTab)).Field("minImportance").SetValue(3);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Chronicle);
            }));
            steps.Add(("handover-overview", () =>
            {
                Clear();
                HarmonyLib.Traverse.Create(typeof(ChronicleTab)).Field("minImportance").SetValue(1);
                MainTabWindow_Prison.Open(MainTabWindow_Prison.Tab.Overview);
            }));
            // RIMPRISON_TOUR=handover: the note, the Intel screen, the chronicle and the overview, in a game that was handed over with its past (the tour then sows it: see AutoTest.HandoverWanted). Not in the whole tour,
            // which is of a clean start; the picture tour (tools/showcase-shots.sh) has the note as its first picture in that game.
            if (System.Environment.GetEnvironmentVariable("RIMPRISON_TOUR") == "handover") steps.RemoveAll(x => x.name != "welcome-letter" && !x.name.StartsWith("handover-"));
            else steps.RemoveAll(x => x.name.StartsWith("handover-"));
        }
    }
}
