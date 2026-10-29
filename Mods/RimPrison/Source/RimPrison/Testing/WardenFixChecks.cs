using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The review of the warden on the floor (GA4 1.5): each hole it found, shown by a check that fails without the fix. The post is not vacant while the warden is away (a caravan, the world) and a warden
    /// who came back is let go when another was named; no caravan takes them; the breaks that would walk them off the floor, set them wild or free an inmate cannot happen, and they get the evening a
    /// member of staff gets at home; the inquiry cannot suspend them, no complaint is filed against them, they are not officer of the month, their work tab is shut; a walk broken off still starts its
    /// cooldown; a grudge may not be talked down; the word is looked at again when it is said; the earshot, the struck wing and the hostage's blow are what they say. Self-contained, like the rest.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckWardenFixes(Map map)
        {
            CheckWardenAway(map);
            CheckWardenDuplicates(map);
            CheckWardenBreaks(map);
            CheckWardenWork(map);
            CheckWardenGates(map);
            CheckWardenTalkDown(map);
            CheckWardenWordRecheck(map);
            CheckWardenEarshot(map);
            CheckWardenBlows(map);
            CheckWardenCounts(map);
        }

        /// <summary>The real warden steps out of the post for the length of the check (the role is theirs again after). False, with a failure, when there is none.</summary>
        private static bool StepOutRealWarden(WardenScene s, string what)
        {
            if (WardenSystem.InPost(s.map) is not { } real || real.GetComp<CompStaff>() is not { } rsc)
            {
                WardenFail($"{what}: could not run (no warden in post)");
                return false;
            }
            StaffRoleDef role = rsc.role;
            rsc.role = null;
            StaffCommute.StaffVersion++;
            s.r.Add(() =>
            {
                rsc.role = role;
                StaffCommute.StaffVersion++;
            });
            if (WardenSystem.InPost(s.map) != null)
            {
                WardenFail($"{what}: could not run (another warden is in post)");
                return false;
            }
            return true;
        }

        // ---- H1: the post is not empty while the warden is away -----------------------------------------------------------------------

        /// <summary>A warden in the world (left the map) or in a caravan has not left the post: no wait starts and nobody is named; with none away the wait starts. Nothing takes the warden in a caravan or a pod.</summary>
        private static void CheckWardenAway(Map map)
        {
            Guarded("warden away", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                if (!StepOutRealWarden(s, "away")) return;
                // Control: a post with nobody in it starts the wait, and names nobody before the days are out.
                g.wardenVacantTick = -1;
                WardenSystem.Daily(g, map, s.Now);
                if (g.wardenVacantTick != s.Now) WardenFail($"(control) an empty post did not start the wait ({g.wardenVacantTick})");
                g.wardenVacantTick = -1;

                // In the world, kept for a quest (an expedition, a loan to another faction): they have left the map and are not back.
                Pawn gone = s.Warden();
                gone.DeSpawn();
                Find.WorldPawns.PassToWorld(gone, PawnDiscardDecideMode.KeepForever);
                // (Vanilla hands a colonist it passes to the world to a random faction, so one who left on their own is no longer the player's and the post is theirs no more; a pawn who is kept for a
                // quest keeps their faction, and that is the case here.)
                gone.SetFaction(Faction.OfPlayer);
                r.Add(() =>
                {
                    if (Find.WorldPawns.Contains(gone)) Find.WorldPawns.RemovePawn(gone);
                });
                WardenSystem.Daily(g, map, s.Now);
                WardenSystem.Daily(g, map, s.Now + 3 * GenDate.TicksPerDay);
                if (g.wardenVacantTick != -1 || StaffCommute.AllStaff(map).Any(WardenSystem.IsWarden) || WardenSystem.Away(map) != gone)
                    WardenFail($"a warden away in the world: the wait is at {g.wardenVacantTick} (expected none), wardens on the staff {StaffCommute.AllStaff(map).Count(WardenSystem.IsWarden)} (expected 0: nobody is named over them), away {WardenSystem.Away(map)?.LabelShort ?? "nobody"} (the pawn: in the world {Find.WorldPawns.Contains(gone)}, faction {gone.Faction?.Name ?? "none"}, dead {gone.Dead}, warden {WardenSystem.IsWarden(gone)}, spawned {gone.Spawned})");
                Find.WorldPawns.RemovePawn(gone);

                // In a caravan: the same.
                Pawn walker = s.Warden();
                walker.DeSpawn(); // (a caravan's pawns are off the map: the forming of one takes them off)
                Caravan caravan = CaravanMaker.MakeCaravan(new[] { walker }, Faction.OfPlayer, map.Tile, addToWorldPawnsIfNotAlready: true);
                r.Add(() =>
                {
                    caravan.RemoveAllPawns();
                    if (!caravan.Destroyed) caravan.Destroy();
                });
                g.wardenVacantTick = -1;
                WardenSystem.Daily(g, map, s.Now);
                WardenSystem.Daily(g, map, s.Now + 3 * GenDate.TicksPerDay);
                if (g.wardenVacantTick != -1 || StaffCommute.AllStaff(map).Any(WardenSystem.IsWarden) || WardenSystem.Away(map) != walker)
                    WardenFail($"a warden away with a caravan: the wait is at {g.wardenVacantTick} (expected none), wardens on the staff {StaffCommute.AllStaff(map).Count(WardenSystem.IsWarden)} (expected 0), away {WardenSystem.Away(map)?.LabelShort ?? "nobody"} (the pawn: in the world {Find.WorldPawns.Contains(walker)}, faction {walker.Faction?.Name ?? "none"}, warden {WardenSystem.IsWarden(walker)}, in the caravan {caravan.ContainsPawn(walker)}; the staff's wardens: {string.Join(", ", StaffCommute.AllStaff(map).Where(WardenSystem.IsWarden).Select(x => $"{x.LabelShort} is the walker {x == walker}, spawned {x.Spawned}, on {x.Map?.uniqueID.ToString() ?? "no map"}"))})");
                caravan.RemoveAllPawns();
                caravan.Destroy();
                if (Find.WorldPawns.Contains(walker)) Find.WorldPawns.RemovePawn(walker);

                // No caravan or pod takes the warden (the dialogs list the sendable pawns).
                Pawn here = s.Warden();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                List<Pawn> sendable = CaravanFormingUtility.AllSendablePawns(map, allowEvenIfDowned: true);
                if (sendable.Contains(here)) WardenFail("the warden can be put on a caravan or in a pod");
                if (!sendable.Contains(guard)) WardenFail("(control) a guard on site is not on the list of those a caravan can take");

                // The evening a member of staff gets at home: the midnight pass lifts the warden's joy to a floor.
                WardenSystem.Daily(g, map, s.Now);
                if (here.needs?.joy is { } joy)
                {
                    joy.CurLevel = 0f;
                    WardenSystem.Daily(g, map, s.Now);
                    if (joy.CurLevel < WardenSystem.JoyFloor - 0.001f) WardenFail($"the warden's joy after midnight is {joy.CurLevel:0.00} (expected at least {WardenSystem.JoyFloor:0.00}: they never go home)");
                }
                else WardenFail("the warden has no joy need to lift");
            });
        }

        /// <summary>A warden who is back when another was named is let go, with a line; the one named last keeps the post.</summary>
        private static void CheckWardenDuplicates(Map map)
        {
            Guarded("warden duplicates", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                if (!StepOutRealWarden(s, "duplicates")) return;
                Pawn old = s.Warden(), named = s.Warden();
                old.GetComp<CompStaff>().hiredTick = s.Now - 5 * GenDate.TicksPerDay;
                named.GetComp<CompStaff>().hiredTick = s.Now - GenDate.TicksPerHour;
                if (WardenSystem.InPost(map) != named) WardenFail($"of two wardens the one in post is {WardenSystem.InPost(map)?.LabelShort} (expected the one named last, {named.LabelShort})");
                WardenSystem.Daily(g, map, s.Now);
                CompStaff oc = old.GetComp<CompStaff>();
                int wardens = StaffCommute.AllStaff(map).Count(WardenSystem.IsWarden);
                if (wardens != 1 || WardenSystem.InPost(map) != named || oc.role != null || old.Faction == Faction.OfPlayer)
                    WardenFail($"two wardens on the floor at midnight: {wardens} left (expected 1), the old one's role {oc.role?.defName ?? "none"} (expected none), still the player's {old.Faction == Faction.OfPlayer}");
                if (!r.NewEntries.Any(e => e.text == "RP_Chr_WardenRetired".Translate(old.LabelShort, named.LabelShort).Resolve())) WardenFail("no chronicle line for the old warden let go");
                // (The one that stays is not touched by it.)
                if (named.GetComp<CompStaff>().role == null || named.Faction != Faction.OfPlayer) WardenFail("the warden in post was let go with the other");
            });
        }

        // ---- H2: the breaks that would take the warden ---------------------------------------------------------------------------------

        private static void CheckWardenBreaks(Map map)
        {
            Guarded("warden breaks", map, r =>
            {
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                foreach (string name in new[] { "GiveUpExit", "RunWild", "Jailbreaker" })
                {
                    MentalBreakDef def = DefDatabase<MentalBreakDef>.GetNamedSilentFail(name);
                    if (def == null)
                    {
                        WardenFail($"the break {name} does not exist in this game (the check has nothing to bar)");
                        continue;
                    }
                    if (def.Worker.BreakCanOccur(w)) WardenFail($"the warden can have the extreme break {name}");
                    // (The control: the same break for a guard. Run wild asks for weather that suits them, and the jailbreaker for an inmate to free: neither is a sure thing in a test, and they are barred by the same line.)
                    if (name == "GiveUpExit" && !def.Worker.BreakCanOccur(guard)) WardenFail($"(control) a guard cannot have the extreme break {name}");
                }
                // The breaks the warden can have are the others: they cost the post nothing.
                if (DefDatabase<MentalBreakDef>.GetNamedSilentFail("Berserk") is { } berserk && !berserk.Worker.BreakCanOccur(w)) WardenFail("(control) the warden cannot have a berserk break (only the three that take them off the floor are barred)");
            });
        }

        // ---- M3: the work tab -------------------------------------------------------------------------------------------------------------

        private static void CheckWardenWork(Map map)
        {
            Guarded("warden work", map, r =>
            {
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                var open = new List<string>();
                foreach (WorkTypeDef t in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                {
                    bool kept = t == WorkTypeDefOf.Firefighter || t.defName is "Patient" or "PatientBedRest" or "BasicWorker";
                    if (!kept && (!w.WorkTypeIsDisabled(t) || w.workSettings.GetPriority(t) > 0)) open.Add(t.defName);
                }
                if (open.Count > 0) WardenFail($"work on the warden's tab that is open to the player: {string.Join(", ", open)}");
                // The emergencies and self-care are theirs as everyone's: what the patch leaves is what the pawn is by nature (a backstory can disable a work type), so the same pawn without the post is the measure.
                CompStaff wc = w.GetComp<CompStaff>();
                StaffRoleDef role = wc.role;
                wc.role = null;
                w.Notify_DisabledWorkTypesChanged();
                List<string> naturallyOff = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => StaffUtility.AlwaysAllowed(t) && w.WorkTypeIsDisabled(t)).Select(t => t.defName).ToList();
                wc.role = role;
                w.Notify_DisabledWorkTypesChanged();
                List<string> keptOff = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(t => StaffUtility.AlwaysAllowed(t) && w.WorkTypeIsDisabled(t)).Select(t => t.defName).ToList();
                if (!naturallyOff.SequenceEqual(keptOff)) WardenFail($"the post shut work the warden keeps (the emergencies and self-care): off with the post {string.Join(", ", keptOff)}, off by nature {string.Join(", ", naturallyOff)}");
                if (guard.WorkTypeIsDisabled(WorkTypeDefOf.Warden)) WardenFail("(control) a guard cannot do the warden's work type");
                // The tab is shut for a warden who was handed their post later (the post on a pawn that had a tab before it).
                Pawn late = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                late.workSettings.SetPriority(WorkTypeDefOf.Hauling, 3);
                late.GetComp<CompStaff>().role = RPWardenDefOf.RP_Warden;
                StaffCommute.StaffVersion++;
                StaffUtility.ApplyRole(late, RPWardenDefOf.RP_Warden, record: false);
                if (late.workSettings.GetPriority(WorkTypeDefOf.Hauling) > 0 || !late.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)) WardenFail("a pawn given the warden's post kept the hauling on their tab");
            });
        }

        // ---- H3, M2, M6: what is not the warden's to be given ------------------------------------------------------------------------

        private static void CheckWardenGates(Map map)
        {
            Guarded("warden gates", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                Pawn w = s.Warden();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                CompStaff gc = guard.GetComp<CompStaff>();

                // H3: the inquiry's "suspend the officer" is not an answer when the officer is the warden (or the state's chief): the answer would cost the least and do nothing.
                ChoiceLetter_PrisonDecision inquiry = ChoiceLetter_PrisonDecision.Make("RP_Decision_Inquiry", "x", "y", map);
                inquiry.pawns.Add(w);
                string why = inquiry.decision.Worker.Disabled(inquiry, 2);
                if (why == null || why != "RP_InquiryProtected".Translate(w.LabelShort).ToString()) WardenFail($"the inquiry offers to suspend the warden (disabled reason: {why ?? "none"})");
                inquiry.pawns.Clear();
                inquiry.pawns.Add(guard);
                if (inquiry.decision.Worker.Disabled(inquiry, 2) != null) WardenFail("(control) the inquiry cannot suspend a guard");
                gc.statePosted = true;
                if (inquiry.decision.Worker.Disabled(inquiry, 2) == null) WardenFail("the inquiry offers to suspend the state's chief");
                gc.statePosted = false;
                if (inquiry.decision.Worker.Disabled(inquiry, 0) != null || inquiry.decision.Worker.Disabled(inquiry, 1) != null) WardenFail("the inquiry's other answers are off for an officer who is protected");

                // The warden cannot be drafted (a drafted warden kills an inmate in a brawl and the inquiry would have nobody to suspend).
                bool button = w.drafter?.ShowDraftGizmo ?? false;
                if (w.drafter != null) w.drafter.Drafted = true;
                bool drafted = w.Drafted;
                if (w.drafter != null) w.drafter.Drafted = false;
                if (button || drafted) WardenFail($"the warden can be drafted (button {button}, drafted {drafted})");

                // M2: no complaint is filed against them, for a restraint or a search (the discipline letters would describe what does not happen).
                List<Complaint> complaints0 = g.story.complaints;
                g.story.complaints = new List<Complaint>();
                r.Add(() => g.story.complaints = complaints0);
                Pawn inmate = s.Inmate(null, 40f);
                CompInmate c = inmate.Inmate();
                Complaint against = ComplaintSystem.File(g, map, inmate, w, ComplaintKind.Force, false);
                for (int i = 0; i < 60; i++)
                {
                    ComplaintSystem.Notify_Restrained(w, inmate, c, wasAggro: false);
                    ComplaintSystem.Notify_Searched(w, inmate, found: true);
                }
                if (against != null || g.story.complaints.Count != 0) WardenFail($"a complaint was filed against the warden ({g.story.complaints.Count} on the desk)");
                Complaint forGuard = ComplaintSystem.File(g, map, inmate, guard, ComplaintKind.Force, false);
                if (forGuard == null) WardenFail("(control) a complaint cannot be filed against a guard");

                // M6: not officer of the month, whatever they have been longer than anyone (the award pays 200 out of wages to a post with no wage).
                var aside = new List<(CompStaff sc, int leave)>();
                foreach (Pawn real in StaffCommute.AllStaff(map).ToList())
                    if (real.GetComp<CompStaff>() is { } rsc && real != w && real != guard)
                    {
                        aside.Add((rsc, rsc.leaveAtTick));
                        rsc.leaveAtTick = int.MaxValue;
                    }
                r.Add(() =>
                {
                    foreach ((CompStaff sc, int leave) in aside) sc.leaveAtTick = leave;
                });
                CompStaff wc = w.GetComp<CompStaff>();
                (wc.commendations, gc.commendations) = (9, 1);
                wc.hiredTick = s.Now - 40 * GenDate.TicksPerDay;
                if (OfficerOfTheMonth.Candidate(map) != guard) WardenFail($"the officer of the month is {OfficerOfTheMonth.Candidate(map)?.LabelShort ?? "nobody"} (expected the guard: the warden is not up for it)");
            });
        }

        // ---- M5: a grudge may not be talked down -------------------------------------------------------------------------------------------

        private static void CheckWardenTalkDown(Map map)
        {
            Guarded("warden talk down", map, r =>
            {
                var s = new WardenScene(map, r);
                int now = s.Now;
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                Pawn target = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn p = s.Inmate(null, 40f);
                CompInmate c = p.Inmate();
                c.reputations.RemoveAll(x => x == ReputationDefOf.RP_Volatile);
                c.staffMemories.Clear();
                StaffMemories.Note(c, target, -70f);
                InmateWant Grudge() => c.want = new InmateWant { def = RPWantDefOf.RP_Want_Score, formedTick = now, deadlineTick = now + 5 * GenDate.TicksPerDay, announced = true, targetId = target.thingIDNumber, targetName = target.LabelShort, targetIsStaff = true };
                InmateWant grudge = Grudge();

                // The odds: a third to start from, a deep grudge less, one who trusts the warden more, a volatile one less. (Expected values are literals.)
                float none = WantWorker_Score.TalkDownChance(p, c, grudge, w);
                StaffMemories.Note(c, w, 50f);
                float trusting = WantWorker_Score.TalkDownChance(p, c, grudge, w);
                StaffMemories.Note(c, target, -30f);
                float deep = WantWorker_Score.TalkDownChance(p, c, grudge, w);
                c.reputations.Add(ReputationDefOf.RP_Volatile);
                float volatileOne = WantWorker_Score.TalkDownChance(p, c, grudge, w);
                c.reputations.Remove(ReputationDefOf.RP_Volatile);
                if (Mathf.Abs(none - 0.2375f) > 0.001f || Mathf.Abs(trusting - 0.6375f) > 0.001f || Mathf.Abs(deep - 0.45f) > 0.001f || Mathf.Abs(volatileOne - 0.30f) > 0.001f)
                    WardenFail($"the odds of talking a grudge down: {none:0.0000} with no trust and a grudge of -70 (expected 0.2375), {trusting:0.0000} trusted (0.6375), {deep:0.0000} at -100 (0.45), {volatileOne:0.0000} volatile (0.30)");

                // It fails: the grudge stands, they think the less of the warden, tension up, no kind thought, no line of a goal backed; the warden has had their word (the goal is not offered again).
                c.staffMemories.Clear();
                StaffMemories.Note(c, target, -70f);
                c.tension = 40f;
                grudge = Grudge();
                WardenSystem.ForcedRoll = false;
                try
                {
                    string told = WardenSystem.Word(w, p, WardenWord.Goal);
                    if (told == null || told != Wants.TalkFailedKey.Translate(p.LabelShort, Wants.Title(grudge)).ToString()) WardenFail($"a grudge that would not be talked down: the warden read \"{told}\"");
                    if (grudge.status == WantStatus.Lost || grudge.Resolved || !grudge.backed) WardenFail($"a grudge that would not be talked down was let go anyway (status {grudge.status}, backed {grudge.backed})");
                    if (Mathf.Abs(StaffMemories.ScoreFor(c, target) + 70f) > 0.01f) WardenFail($"a failed word moved their view of the guard ({StaffMemories.ScoreFor(c, target)}, expected -70)");
                    if (Mathf.Abs(StaffMemories.ScoreFor(c, w) + 4f) > 0.01f || Mathf.Abs(c.tension - 44f) > 0.01f) WardenFail($"a failed word: trust in the warden {StaffMemories.ScoreFor(c, w)} (expected -4), tension {c.tension} (expected 44)");
                    if (s.Has(p, RPWardenDefOf.RP_WardenHadAWord) || r.NewEntries.Any(e => e.text == "RP_Chr_WardenBacked".Translate(w.LabelShort, p.LabelShort, Wants.Title(grudge)).Resolve())) WardenFail("a failed word left a kind thought or a line of a goal backed");
                    if (c.wardenWordTick != now || sc.wordReadyTick <= 0 || WardenSystem.WordOptions(w, p).Any(o => o.kind == WardenWord.Goal)) WardenFail("a failed word did not spend the inmate's day and the warden's cooldown, or the goal is on offer again");

                    // It works: the goal is let go, and the warden's word for the guard counts a little (+10, not +25: the hate is still there).
                    c.staffMemories.Clear();
                    StaffMemories.Note(c, target, -70f);
                    c.wardenWordTick = -999999;
                    grudge = Grudge();
                    WardenSystem.ForcedRoll = true;
                    WardenSystem.Word(w, p, WardenWord.Goal);
                    float after = StaffMemories.ScoreFor(c, target);
                    if (grudge.status != WantStatus.Lost || Mathf.Abs(after + 60f) > 0.01f) WardenFail($"a grudge talked down: status {grudge.status} (expected Lost), their view of the guard {after} (expected -60: -70 and a word for them of 10)");
                    if (after <= StaffMemories.HatedAt) { /* (still hated: the word does not undo what was done) */ }
                    else WardenFail($"a word for the guard made them forgive a grudge of -70 (now {after}, past the hated line {StaffMemories.HatedAt})");
                }
                finally
                {
                    WardenSystem.ForcedRoll = null;
                }
            });
        }

        // ---- L9: the word is looked at again when it is said --------------------------------------------------------------------------------

        private static void CheckWardenWordRecheck(Map map)
        {
            Guarded("warden word recheck", map, r =>
            {
                var s = new WardenScene(map, r);
                int now = s.Now;
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                Pawn p = s.Inmate(null, 40f);
                CompInmate c = p.Inmate();
                int words0 = s.g.stats.wardenWords;
                // Recruited for a transfer (an informant, known and paid) between the order and the word: the word is not said, and costs nothing.
                c.informantPaid = true;
                c.reputations.Add(ReputationDefOf.RP_Snitch);
                c.revealed.Add(ReputationDefOf.RP_Snitch);
                WardenSystem.ForcedRoll = true;
                try
                {
                    string said = WardenSystem.Word(w, p, WardenWord.Informant);
                    if (said != null || sc.wordReadyTick > 0 || c.wardenWordTick > -999999 || s.g.stats.wardenWords != words0) WardenFail($"an informant sounded out again after they were recruited: told \"{said}\", cooldown {sc.wordReadyTick}, day {c.wardenWordTick}");
                }
                finally
                {
                    WardenSystem.ForcedRoll = null;
                    c.informantPaid = false;
                    c.reputations.Remove(ReputationDefOf.RP_Snitch);
                    c.revealed.Remove(ReputationDefOf.RP_Snitch);
                }
                // Spoken to today in between (by another order, say): not twice a day.
                c.wardenWordTick = now - 100;
                if (WardenSystem.Word(w, p, WardenWord.Chat) != null || sc.wordReadyTick > 0) WardenFail("a second word with the same inmate the same day was said when the order came back round");
                c.wardenWordTick = -999999;
                // A goal that is gone by the time the word is said.
                c.want = null;
                if (WardenSystem.Word(w, p, WardenWord.Goal) != null || sc.wordReadyTick > 0) WardenFail("a word about a goal that is no more was said as a few words");
                // (Control: the word is said when it is still so.)
                if (WardenSystem.Word(w, p, WardenWord.Chat) == null || sc.wordReadyTick <= 0) WardenFail("(control) a few words with an inmate not spoken to today were not said");

                // The targeter: not an inmate in the midst of a mental state.
                if (!WardenGizmos.CanTalkTo(p)) WardenFail("(control) the warden cannot walk up to an inmate for a word");
                if (!p.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true, transitionSilently: true)) WardenFail("could not give an inmate a mental state");
                else
                {
                    if (WardenGizmos.CanTalkTo(p)) WardenFail("the warden can walk up to an inmate in a mental state for a word");
                    p.mindState.mentalStateHandler.CurState?.RecoverFromState();
                }

                // What a word is told, and not told: a grudge or a wish to leave a gang the warden has not found is not on offer (it would name the gang), a hidden snitch asked says yes (the asking finds them).
                c.gang = DefDatabase<GangDef>.AllDefsListForReading.First();
                c.gangKnown = false;
                c.want = new InmateWant { def = RPGangStructureDefOf.RP_Want_LeaveGang, formedTick = now, deadlineTick = now + 5 * GenDate.TicksPerDay, announced = true };
                c.wardenWordTick = -999999;
                if (WardenSystem.WordOptions(w, p).Any(o => o.kind == WardenWord.Goal)) WardenFail("a wish to leave a gang nobody has found is on offer as a goal to back");
                c.gangKnown = true;
                if (!WardenSystem.WordOptions(w, p).Any(o => o.kind == WardenWord.Goal)) WardenFail("(control) a wish to leave a gang the warden knows of is not on offer");
                c.gang = null;
                c.gangKnown = false;
                c.want = null;
                c.reputations.Add(ReputationDefOf.RP_Snitch);
                c.revealed.Remove(ReputationDefOf.RP_Snitch);
                WardenSystem.ForcedRoll = false;
                try
                {
                    WardenSystem.Word(w, p, WardenWord.Informant);
                    if (!c.informantPaid || !c.KnownReputation(ReputationDefOf.RP_Snitch)) WardenFail($"a hidden snitch sounded out did not say yes ({c.informantPaid}, known {c.KnownReputation(ReputationDefOf.RP_Snitch)}) though the dice said no");
                }
                finally
                {
                    WardenSystem.ForcedRoll = null;
                    c.informantPaid = false;
                    c.reputations.Remove(ReputationDefOf.RP_Snitch);
                    c.revealed.Remove(ReputationDefOf.RP_Snitch);
                }
            });
        }

        // ---- L11: who hears -------------------------------------------------------------------------------------------------------------------

        private static void CheckWardenEarshot(Map map)
        {
            Guarded("warden earshot", map, r =>
            {
                var s = new WardenScene(map, r);
                Pawn w = s.Warden();
                PrisonZoneDef wing = WardenSystem.Wings(map).OrderByDescending(z => s.pm.zones.CellsOf(z).Count).FirstOrDefault();
                List<IntVec3> cells = wing == null ? new List<IntVec3>() : s.Cells(wing, w);
                if (cells.Count < 4)
                {
                    WardenFail($"earshot: could not run (room to stand in the wing: {cells.Count} cells)");
                    return;
                }
                IntVec3 at = cells[cells.Count / 2];
                List<IntVec3> near = GenRadial.RadialCellsAround(at, 3f, true).Where(c => c.InBounds(map) && c.Standable(map) && s.pm.zones.ZoneAt(c) == wing && c.GetFirstPawn(map) == null && c != at).ToList();
                if (near.Count < 2)
                {
                    WardenFail($"earshot: could not run (room near the warden: {near.Count} cells)");
                    return;
                }
                s.Place(w, at);
                Pawn calm = s.Inmate(near[0], 20f), raving = s.Inmate(near[1], 20f);
                if (!WardenSystem.NearOnWing(w, wing).Contains(calm)) WardenFail("(control) an inmate on the wing next to the warden does not hear them");
                if (!raving.mindState.mentalStateHandler.TryStartMentalState(MentalStateDefOf.Wander_Sad, forced: true, transitionSilently: true))
                {
                    WardenFail("earshot: could not give an inmate a mental state");
                    return;
                }
                if (WardenSystem.NearOnWing(w, wing).Contains(raving)) WardenFail("an inmate in a mental state is met on the warden's walk");
                raving.mindState.mentalStateHandler.CurState?.RecoverFromState();
            });
        }

        // ---- L3, L4: a blow to the warden ------------------------------------------------------------------------------------------------------

        private static void CheckWardenBlows(Map map)
        {
            Guarded("warden blows", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                PrisonZoneDef wing = WardenSystem.Wings(map).OrderByDescending(z => s.pm.zones.CellsOf(z).Count).FirstOrDefault();
                Pawn w = s.Warden();
                List<IntVec3> cells = wing == null ? new List<IntVec3>() : s.Cells(wing, w);
                List<IntVec3> canteen = s.Cells(RPDefOf.RP_Canteen, w);
                if (cells.Count < 4 || canteen.Count < 2)
                {
                    WardenFail($"blows: could not run (cells to stand on in the wing {cells.Count}, in the canteen {canteen.Count})");
                    return;
                }
                IntVec3 at = cells[cells.Count / 2];
                List<IntVec3> near = GenRadial.RadialCellsAround(at, 3f, true).Where(c => c.InBounds(map) && c.Standable(map) && s.pm.zones.ZoneAt(c) == wing && c.GetFirstPawn(map) == null && c != at).ToList();
                if (near.Count < 2)
                {
                    WardenFail($"blows: could not run (room next to the warden: {near.Count} cells)");
                    return;
                }
                s.Place(w, at);
                Pawn attacker = s.Inmate(near[0], 30f), inWing = s.Inmate(near[1], 20f), elsewhere = s.Inmate(canteen[0], 20f);
                foreach (Pawn p in new[] { inWing, elsewhere }) StaffMemories.Note(p.Inmate(), w, 40f);

                // The wing they were struck in hears of it, and no other (the letter says "the wing").
                WardenSystem.Notify_Struck(w, attacker, fresh: true);
                if (Mathf.Abs(inWing.Inmate().tension - 26f) > 0.01f || Mathf.Abs(elsewhere.Inmate().tension - 20f) > 0.01f)
                    WardenFail($"a blow to the warden in a wing: a trusting inmate of the wing has tension {inWing.Inmate().tension} (expected 26), one in the canteen {elsewhere.Inmate().tension} (expected 20: they did not hear)");

                // Held by a ringleader (the crisis's deadline lands a blow of its own): no second letter and no count.
                int struck = g.stats.wardenStruck;
                int letters = r.NewLetters.Count(l => LetterIs(l, "RP_WardenStruckLabel".Translate(w.LabelShort)));
                w.health.AddHediff(RPHostageDefOf.RP_Hostage);
                r.Add(() =>
                {
                    if (w.health.hediffSet.GetFirstHediffOfDef(RPHostageDefOf.RP_Hostage) is { } h) w.health.RemoveHediff(h);
                });
                WardenSystem.Notify_Struck(w, attacker, fresh: true);
                if (g.stats.wardenStruck != struck || r.NewLetters.Count(l => LetterIs(l, "RP_WardenStruckLabel".Translate(w.LabelShort))) != letters) WardenFail("a blow to a held warden was told as a blow to the warden, on top of the crisis's own letter");
            });
        }

        // ---- L6, L7: the warden is nobody's pay or average -------------------------------------------------------------------------------------

        private static void CheckWardenCounts(Map map)
        {
            Guarded("warden counts", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                // The scene's isolation: what a snitch's tip may reach is the made-up pawns, not the real ones (a tip marks a tunnel known, flags contraband, names a gang, puts suspicion on a guard).
                Pawn realInmate = map.mapPawns.PrisonersOfColonySpawned.FirstOrDefault(p => p.Inmate() != null);
                Pawn madeUp = s.Inmate(null, 30f);
                if (realInmate == null || InformantSystem.TipPool == null || InformantSystem.TipPool(realInmate) || !InformantSystem.TipPool(madeUp))
                    WardenFail($"the checks' scene lets a snitch's tip reach the real prison (pool {(InformantSystem.TipPool == null ? "none" : "set")}, a real inmate in it {realInmate != null && InformantSystem.TipPool != null && InformantSystem.TipPool(realInmate)}, a made-up one out of it {InformantSystem.TipPool != null && !InformantSystem.TipPool(madeUp)})");
                int heads0 = StaffStories.HazardHeads(map);
                s.g.story.pulse.Update(map);
                float avg0 = g.story.pulse.staffAvgMood;
                r.Add(() => g.story.pulse.Update(map));
                Pawn w = s.Warden();
                w.needs.mood.CurLevel = 0f;
                s.g.story.pulse.Update(map);
                if (StaffStories.HazardHeads(map) != heads0) WardenFail($"hazard pay is asked for the warden too ({StaffStories.HazardHeads(map)} heads, {heads0} before they came)");
                if (Mathf.Abs(g.story.pulse.staffAvgMood - avg0) > 0.001f) WardenFail($"the staff's mood average took in the warden ({avg0:0.000} -> {g.story.pulse.staffAvgMood:0.000} with a warden at mood 0)");
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                guard.needs.mood.CurLevel = 1f;
                s.g.story.pulse.Update(map);
                if (StaffStories.HazardHeads(map) != heads0 + 1 || Mathf.Abs(g.story.pulse.staffAvgMood - avg0) < 0.001f) WardenFail($"(control) a guard is not counted for hazard pay ({StaffStories.HazardHeads(map)}, expected {heads0 + 1}) or in the staff's mood average ({avg0:0.000} -> {g.story.pulse.staffAvgMood:0.000})");

                // L8: the prison suffers twice as much for a warden lost, and no more is earned for one freed (literals: a guard's end is +1 talked down or -5 killed).
                float freedWarden = HostageArc.ReputationChange(w, HostageHow.Talked, HostageFate.Freed), freedGuard = HostageArc.ReputationChange(guard, HostageHow.Talked, HostageFate.Freed);
                float lostWarden = HostageArc.ReputationChange(w, HostageHow.Killed, HostageFate.Dead), lostGuard = HostageArc.ReputationChange(guard, HostageHow.Killed, HostageFate.Dead);
                if (Mathf.Abs(freedWarden - 1f) > 0.001f || Mathf.Abs(freedGuard - 1f) > 0.001f || Mathf.Abs(lostWarden + 10f) > 0.001f || Mathf.Abs(lostGuard + 5f) > 0.001f)
                    WardenFail($"the prison's name at the end of a crisis: a warden talked out earns {freedWarden} (expected +1), a guard {freedGuard} (+1); a warden killed costs {lostWarden} (expected -10), a guard {lostGuard} (-5)");
            });
        }

        // ---- M1: a walk broken off ---------------------------------------------------------------------------------------------------------------

        /// <summary>Through real ticks: a walk that is broken off after its first meeting has still been walked (the cooldown runs), so that trust cannot be farmed by walking two stops and ordering another.</summary>
        private static void CheckWardenWalkBrokenOffTicks(Map map)
        {
            Guarded("warden walk broken off through ticks", map, r =>
            {
                var s = new WardenScene(map, r);
                PrisonGame g = s.g;
                Pawn w = s.Warden();
                CompStaff sc = w.GetComp<CompStaff>();
                PrisonZoneDef wing = WardenSystem.Wings(map).OrderByDescending(z => s.pm.zones.CellsOf(z).Count).FirstOrDefault();
                List<IntVec3> cells = wing == null ? new List<IntVec3>() : s.Cells(wing, w);
                if (cells.Count < 4)
                {
                    WardenFail($"could not run (room to stand in the wing: {cells.Count} cells)");
                    return;
                }
                s.Place(w, cells[cells.Count / 2]);
                List<IntVec3> stops = WardenSystem.WalkPlan(w, wing);
                List<IntVec3> near = stops.Count < 2 ? new List<IntVec3>() : GenRadial.RadialCellsAround(stops[0], 3f, true).Where(c => c.InBounds(map) && c.Standable(map) && s.pm.zones.ZoneAt(c) == wing && c.GetFirstPawn(map) == null).ToList();
                if (near.Count < 1)
                {
                    WardenFail($"could not run (stops {stops.Count}, room to stand near the first {near.Count})");
                    return;
                }
                Pawn calm = s.Inmate(near[0], 20f);
                sc.walkReadyTick = -1;
                int walks0 = g.stats.wardenWalks;
                s.Place(w, cells[cells.Count / 2]);
                if (!WardenOrders.Walk(w, wing)) WardenFail("the walk order was refused");
                int ticks = s.Run(() => StaffMemories.ScoreFor(calm.Inmate(), w) >= 2.9f, 2500, calm);
                if (StaffMemories.ScoreFor(calm.Inmate(), w) < 2.9f)
                {
                    WardenFail($"could not run (the walk met nobody in {ticks} ticks: job {w.CurJobDef?.defName ?? "none"} at {w.Position})");
                    return;
                }
                // The first stop is met; the walk is broken off (another order, a draft: the job ends).
                int now = s.Now;
                if (w.CurJobDef != RPWardenDefOf.RP_WardenWalk) WardenFail("could not run (the walk is over after the first meeting)");
                w.jobs.StopAll();
                if (g.stats.wardenWalks != walks0 || sc.walkReadyTick < now + (WardenSystem.WalkCooldownHours - 1) * GenDate.TicksPerHour)
                    WardenFail($"a walk broken off after its first meeting did not start the cooldown (ready at {sc.walkReadyTick}, now {now}, walks counted {g.stats.wardenWalks - walks0})");
                if (WardenSystem.Unready(w, sc.walkReadyTick) == null) WardenFail("the warden is ready to walk again at once after a walk broken off");
            });
        }
    }
}
