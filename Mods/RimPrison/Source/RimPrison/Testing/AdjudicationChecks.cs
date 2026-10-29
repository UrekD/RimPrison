using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The adjudication sheet (Security/Adjudication.cs): with the policy off nothing changes; with it on a seen offence of severity 3 and over is queued with its evidence (the officer named, a camera, the weapon, the
    /// record) instead of taking the table's sanction; one letter a day lists yesterday's cases (six at most, the rest the next day, held to one by the day's budget); the four answers (the policy's sanction; loss of
    /// remission zeroes the earned days; days added move the term everywhere it is read, up to the cap, and are struck out on a jailhouse lawyer's appeal when nobody saw it; a charge opens the court case for an armed
    /// assault on staff, tried on its day with the table's days on a conviction); a case nobody answered takes the policy's sanction; the inspector counts the days added in a week. Self-contained on the live prison with
    /// the book swapped for an empty one (<see cref="AdjBegin"/>), throwaway inmates and officers, the clock on to day 10, nothing ticking, all put back. The round trip is in the saveload plan
    /// (<see cref="SeedAdjudicationForReload"/>). Premises the live prison may not give are told, not failed.
    /// </summary>
    public static partial class AutoTest
    {
        private static void AdjFail(string check, string what) => problems0.Add($"adjudication ({check}): {what}");

        private static void CheckAdjudication(Map map)
        {
            Rand.PushState(20261027);
            try
            {
                CheckAdjudicationPolicyOff(map);
                CheckAdjudicationQueue(map);
                CheckAdjudicationSheet(map);
                CheckAdjudicationSheetTiming(map);
                CheckAdjudicationMarks(map);
                CheckAdjudicationAnotherMap(map);
                CheckAdjudicationBudget(map);
                CheckAdjudicationDaysAdded(map);
                CheckAdjudicationRemission(map);
                CheckAdjudicationAppeal(map);
                CheckAdjudicationCharge(map);
                CheckAdjudicationUnanswered(map);
                CheckAdjudicationInspector(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        // ---- the scene ---------------------------------------------------------------------------------------------------------------------------------

        private static MisconductDef AdjDef(string name) => DefDatabase<MisconductDef>.GetNamed(name);

        /// <summary>
        /// A yard corner (the real trials and complaints set aside), the clock on to day 10, the sheet's book swapped for an empty one and the policy set, remission and the real jailhouse lawyers' standing put back when the
        /// check is done.
        /// </summary>
        private static CourtScene AdjBegin(Map map, Rollback r, bool on = true)
        {
            InspectorClockOn(r);
            var s = new CourtScene(map, r);
            PrisonGame g = s.g;
            (AdjudicationBook book0, bool on0, bool remission0) = (g.adjudication, g.adjudicate, g.remission);
            g.adjudication = new AdjudicationBook();
            g.adjudicate = on;
            r.Add(() => (g.adjudication, g.adjudicate, g.remission) = (book0, on0, remission0));
            // (What watches the prison is put aside, and put back last: the scene's own officers and camera are the only watchers, so what an officer or a camera saw is what the check made.)
            PrisonMap pm = s.pm;
            List<Pawn> guards0 = pm.Guards.ToList();
            List<CompSecurityCamera> cameras0 = pm.Cameras.ToList();
            List<CompGuardTower> towers0 = pm.Towers.ToList();
            r.Add(() =>
            {
                pm.Guards.Clear();
                pm.Guards.AddRange(guards0);
                pm.Cameras.Clear();
                pm.Cameras.AddRange(cameras0);
                pm.Towers.Clear();
                pm.Towers.AddRange(towers0);
            });
            pm.Guards.Clear();
            pm.Cameras.Clear();
            pm.Towers.Clear();
            return s;
        }

        /// <summary>The real jailhouse lawyers lose the reputation while the check runs (a lawyer anywhere in the prison strikes out days nobody saw), and get it back.</summary>
        private static void AdjNoRealLawyers(Map map, Rollback r)
        {
            foreach (CompInmate c in map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null && c.HasReputation(RPReputationDefOf.RP_JailhouseLawyer)).ToList())
            {
                CompInmate lawyer = c;
                lawyer.reputations.Remove(RPReputationDefOf.RP_JailhouseLawyer);
                r.Add(() => lawyer.reputations.Add(RPReputationDefOf.RP_JailhouseLawyer));
            }
        }

        private sealed class AdjSeed
        {
            public Pawn inmate, officer;
            public AdjudicationCase k;
            public bool stateStarted;
        }

        /// <summary>
        /// A serious offence recorded as the game records it (<see cref="MisconductUtility.Record"/>), by a throwaway inmate who was admitted days ago, with a sentence of 30 days. An officer beside them (on the prison's
        /// list of guards), a camera over the spot, the weapon in hand, the victim they are going for, found out by staff (<paramref name="evidence"/>) and offences already on their file are each on request. The place is
        /// lit, whatever the hour the check runs at. The case it queued, if one was, is on the result.
        /// </summary>
        private static AdjSeed AdjOffence(CourtScene s, Rollback r, string name, MisconductDef def, bool officer = false, bool weapon = false, bool camera = false, Pawn victim = null, bool evidence = false, int prior = 0)
        {
            Map map = s.map;
            Pawn p = s.Inmate(name);
            CompInmate c = p.Inmate();
            c.admittedTick = s.Now - 3 * GenDate.TicksPerDay;
            c.sentenceDays = 30;
            IntVec3 at = p.Position;
            bool lit0 = TensionUtility.TestGlow != null;
            TensionUtility.TestGlow ??= new Dictionary<IntVec3, float>();
            TensionUtility.TestGlow[at] = 1f;
            r.Add(() =>
            {
                if (!lit0) TensionUtility.TestGlow = null;
                else TensionUtility.TestGlow.Remove(at);
            });
            for (int i = 0; i < prior; i++) c.AddRecord(new MisconductRecord { def = AdjDef("RP_Fight"), tick = s.Now - (i + 2) * GenDate.TicksPerHour, seen = true });
            var seed = new AdjSeed { inmate = p };
            if (officer)
            {
                seed.officer = s.Guard("Zofficer" + name, at);
                s.pm.Guards.Add(seed.officer);
                r.Add(() => s.pm.Guards.Remove(seed.officer));
            }
            if (camera) SpawnDarkCamera(map, r, CellFinder.StandableCellNear(at, map, 4f));
            if (weapon && ThingMaker.MakeThing(DefDatabase<ThingDef>.GetNamed("RP_Shiv")) is ThingWithComps shiv) p.equipment.AddEquipment(shiv);
            if (victim != null) seed.stateStarted = p.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Assaulting, null, forced: true, otherPawn: victim);
            int before = s.g.adjudication.cases.Count;
            MisconductUtility.Record(p, c, s.pm, def, evidence: evidence, chronicle: false);
            if (victim != null) p.mindState.mentalStateHandler.Reset();
            // (Who watched has done so: the next offence of the scene is judged by what its own seed gives it.)
            s.pm.Guards.Clear();
            s.pm.Cameras.Clear();
            seed.k = s.g.adjudication.cases.Count > before ? s.g.adjudication.cases[s.g.adjudication.cases.Count - 1] : null;
            return seed;
        }

        /// <summary>The case happened yesterday (before this morning's sheet).</summary>
        private static void AdjYesterday(CourtScene s, AdjudicationCase k) => k.tick = s.Now - GenLocalDate.DayTick(s.map) - GenDate.TicksPerHour;

        private static IEnumerable<ChoiceLetter_PrisonDecision> AdjLetters(Rollback r) => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(Adjudication.DecisionDef));

        // ---- the policy off ------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationPolicyOff(Map map)
        {
            Guarded("adjudication: with the policy off a serious offence takes the table's sanction", map, r =>
            {
                CourtScene s = AdjBegin(map, r, on: false);
                AdjSeed a = AdjOffence(s, r, "Zadjoff1", AdjDef("RP_AssaultStaff"));
                CompInmate c = a.inmate.Inmate();
                if (a.k != null || s.g.adjudication.cases.Count > 0) AdjFail("policy off", "a case was queued with the policy off");
                int hours = s.g.punishments.KindFor(AdjDef("RP_AssaultStaff")) == Sanction.Solitary ? s.g.punishments.SolitaryHoursFor(AdjDef("RP_AssaultStaff")) : -1;
                if (hours > 0 && c.pendingPunishmentHours != hours) AdjFail("policy off", $"the hole was {c.pendingPunishmentHours} hours (the table gives {hours})");
                if (Adjudication.Takes(AdjDef("RP_AssaultStaff"))) AdjFail("policy off", "the sheet takes an offence with the policy off");
                if (hours > 0 && c.PolicyHoursForLatest(true) != hours) AdjFail("policy off", $"a restraint after it gives {c.PolicyHoursForLatest(true)} hours (the table gives {hours})");
            });
        }

        // ---- the queue ------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationQueue(Map map)
        {
            Guarded("adjudication: a serious seen offence is queued with its evidence instead of the table's sanction", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                MisconductDef assault = AdjDef("RP_AssaultStaff"), escape = AdjDef("RP_EscapeAttempt"), fight = AdjDef("RP_Fight");
                // Severity three and over, murder and the marks aside.
                if (!Adjudication.Takes(assault) || !Adjudication.Takes(escape) || !Adjudication.Takes(AdjDef("RP_Riot")) || Adjudication.Takes(fight) || Adjudication.Takes(RPDeathDefOf.RP_Murder))
                    AdjFail("queue", $"what the sheet takes: assault {Adjudication.Takes(assault)}, escape {Adjudication.Takes(escape)}, riot {Adjudication.Takes(AdjDef("RP_Riot"))}, a fight {Adjudication.Takes(fight)}, murder {Adjudication.Takes(RPDeathDefOf.RP_Murder)} (yes, yes, yes, no, no)");
                // An assault on staff seen by an officer, on camera, with a weapon and two serious offences before it.
                Pawn victim = s.Guard("Zadjvictim1");
                AdjSeed a = AdjOffence(s, r, "Zadjq1", assault, officer: true, weapon: true, camera: true, victim: victim, prior: 2);
                CompInmate c = a.inmate.Inmate();
                if (a.k == null)
                {
                    AdjFail("queue", "a seen assault on staff was not queued");
                    return;
                }
                if (!a.k.officer || a.k.officerName != a.officer.LabelShort || !a.k.camera || !a.k.weapon || a.k.record != 2 || a.k.answer != AdjudicationAnswer.Pending || a.k.sheet != -1)
                    AdjFail("queue", $"the case's evidence: officer {a.k.officer} ({a.k.officerName}, wanted {a.officer.LabelShort}), camera {a.k.camera}, weapon {a.k.weapon}, record {a.k.record} (wanted 2), {a.k.answer}, sheet {a.k.sheet}");
                if (c.pendingPunishmentHours != 0 || c.records.Any(x => x.def == AdjDef("RP_AssaultStaff") && x.punished)) AdjFail("queue", $"the table's hole was given anyway ({c.pendingPunishmentHours} hours)");
                if (c.PolicyHoursForLatest(false) != 0 || c.PolicyHoursForLatest(true) != CompInmate.ResistingHours)
                    AdjFail("queue", $"a restraint after it gives {c.PolicyHoursForLatest(false)}/{c.PolicyHoursForLatest(true)} hours (wanted 0, and {CompInmate.ResistingHours} for acting out), not the table's hole");
                if (a.stateStarted && (!a.k.Chargeable || a.k.victimId != victim.thingIDNumber || a.k.victimName != victim.LabelShort))
                    AdjFail("queue", $"an assault on staff with a weapon cannot be charged (scene {a.k.scene != null}, victim {a.k.victimName})");
                if (!a.stateStarted) RPLog.Message("[RimPrison:test] adjudication: the assault mental state could not be started on the throwaway (the charge's scene is not checked here)");
                // Nobody saw it but the noise: no officer, no camera, no weapon.
                AdjSeed quiet = AdjOffence(s, r, "Zadjq2", assault);
                if (quiet.k == null || quiet.k.officer || quiet.k.camera || quiet.k.weapon || quiet.k.Evidenced || quiet.k.Chargeable)
                    AdjFail("queue", $"an assault nobody saw has evidence: officer {quiet.k?.officer}, camera {quiet.k?.camera}, weapon {quiet.k?.weapon}, chargeable {quiet.k?.Chargeable}");
                // Found by staff directly counts as an officer.
                AdjSeed found = AdjOffence(s, r, "Zadjq3", escape, evidence: true);
                if (found.k == null || !found.k.officer || found.k.officerName != null) AdjFail("queue", $"an escape attempt staff found out has officer {found.k?.officer}, named {found.k?.officerName}");
                // A fight (severity 2) takes the table's sanction at once.
                AdjSeed brawl = AdjOffence(s, r, "Zadjq4", fight);
                if (brawl.k != null || brawl.inmate.Inmate().pendingPunishmentHours <= 0 && g.punishments.KindFor(fight) == Sanction.Solitary && g.punishments.SolitaryHoursFor(fight) > 0)
                    AdjFail("queue", $"a fight: queued {brawl.k != null}, hole {brawl.inmate.Inmate().pendingPunishmentHours} hours (the table's, at once)");
                if (g.adjudication.cases.Count != 3) AdjFail("queue", $"{g.adjudication.cases.Count} cases for three serious offences");
            });
        }

        // ---- the sheet -------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationSheet(Map map)
        {
            Guarded("adjudication: one letter the next morning lists yesterday's cases, the officer named", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                AdjSeed a = AdjOffence(s, r, "Zadjs1", assault, officer: true);
                AdjSeed today = AdjOffence(s, r, "Zadjs2", assault);
                if (a.k == null || today.k == null)
                {
                    AdjFail("sheet", "the seeded assaults were not queued");
                    return;
                }
                AdjYesterday(s, a.k);
                Adjudication.Morning(g, map);
                List<ChoiceLetter_PrisonDecision> letters = AdjLetters(r).ToList();
                if (letters.Count != 1)
                {
                    AdjFail("sheet", $"{letters.Count} sheets went out");
                    return;
                }
                string text = letters[0].Text.Resolve();
                if (!text.Contains(a.inmate.LabelShort) || !text.Contains(a.officer.LabelShort) || !text.Contains(assault.label)) AdjFail("sheet", $"the sheet does not name the inmate, the officer who saw it and the offence: {text}");
                if (text.Contains(today.inmate.LabelShort)) AdjFail("sheet", "today's case is on yesterday's sheet");
                if (a.k.sheet != letters[0].amount || today.k.sheet != -1 || letters[0].TimeoutActive == false) AdjFail("sheet", $"the sheet's number {letters[0].amount}, yesterday's case on sheet {a.k.sheet}, today's on {today.k.sheet}, answer time {letters[0].TimeoutActive}");
                if (Adjudication.Waiting(g, a.k.sheet).Count != 1) AdjFail("sheet", "the case is not waiting for an answer");
                // One a day: a second pass the same morning writes no second sheet.
                Adjudication.Morning(g, map);
                if (AdjLetters(r).Count() != 1) AdjFail("sheet", "a second sheet the same day");
                // The sheet's own answers (the window's work done by hand): the policy's sanction for the case.
                CompInmate c = a.inmate.Inmate();
                string fell = Adjudication.Answer(g, map, a.k, AdjudicationAnswer.Policy);
                int hours = g.punishments.KindFor(assault) == Sanction.Solitary ? g.punishments.SolitaryHoursFor(assault) : 0;
                if (fell != null || a.k.answer != AdjudicationAnswer.Policy || hours > 0 && c.pendingPunishmentHours < hours) AdjFail("sheet", $"the policy's answer: {a.k.answer}, hole {c.pendingPunishmentHours} hours (wanted {hours}), {fell}");
                // The file tells what the sheet did.
                if (!Adjudication.FileLines(a.inmate).Any(l => l.Contains(Adjudication.AnswerText(a.k)))) AdjFail("sheet", "the file has no line for the answered case");
            });
        }

        private static void CheckAdjudicationBudget(Map map)
        {
            Guarded("adjudication: six cases to a sheet, the rest the next day, and the day's budget holds it to one", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                var seeds = new List<AdjSeed>();
                for (int i = 0; i < 8; i++)
                {
                    AdjSeed a = AdjOffence(s, r, "Zadjb" + i, assault);
                    if (a.k == null)
                    {
                        AdjFail("budget", "a seeded assault was not queued");
                        return;
                    }
                    AdjYesterday(s, a.k);
                    a.k.tick -= i * 100;
                    seeds.Add(a);
                }
                Adjudication.Morning(g, map);
                ChoiceLetter_PrisonDecision first = AdjLetters(r).FirstOrDefault();
                if (first == null || Adjudication.Waiting(g, first.amount).Count != Adjudication.MaxPerSheet || g.adjudication.cases.Count(k => k.sheet < 0) != 2)
                {
                    AdjFail("budget", $"the first sheet has {(first == null ? 0 : Adjudication.Waiting(g, first.amount).Count)} cases (wanted {Adjudication.MaxPerSheet}), {g.adjudication.cases.Count(k => k.sheet < 0)} wait for the next day (wanted 2)");
                    return;
                }
                // The oldest six: the two newest (the first two seeded) are the ones left.
                if (g.adjudication.cases.Where(k => k.sheet < 0).Any(k => seeds.FindIndex(x => x.k == k) > 1)) AdjFail("budget", "a newer case was left off the sheet for an older one");
                // The sheet's answer time is up (the letter is answered by default): all six take the policy's sanction.
                first.Lapse();
                if (Adjudication.Waiting(g, first.amount).Count != 0 || g.adjudication.cases.Count(k => k.answer == AdjudicationAnswer.Policy) != Adjudication.MaxPerSheet)
                    AdjFail("budget", "the sheet left unanswered did not take the policy's sanction for every case on it");
                Find.LetterStack.RemoveLetter(first);
                // The next day the other two, on a sheet of their own.
                g.adjudication.lastSheetDay = -1;
                Adjudication.Morning(g, map);
                ChoiceLetter_PrisonDecision second = AdjLetters(r).LastOrDefault(l => l != first && !l.answered);
                if (second == null || Adjudication.Waiting(g, second.amount).Count != 2 || second.amount == first.amount) AdjFail("budget", $"the next day's sheet has {(second == null ? 0 : Adjudication.Waiting(g, second.amount).Count)} cases (wanted the two that were left)");
                // The day's budget spent: the sheet waits for tomorrow, and is still the one sheet (no second one is written behind it).
                StorySystem story = g.story;
                int decisions0 = story.decisionsToday;
                bool testBudget0 = StoryLetters.TestBudget;
                List<ChoiceLetter_PrisonDecision> deferred0 = story.deferredLetters.ToList();
                r.Add(() =>
                {
                    StoryLetters.TestBudget = testBudget0;
                    story.decisionsToday = decisions0;
                    story.deferredLetters.Clear();
                    story.deferredLetters.AddRange(deferred0);
                });
                if (second != null)
                {
                    Find.LetterStack.RemoveLetter(second);
                    second.answered = true;
                    foreach (AdjudicationCase k in Adjudication.Waiting(g, second.amount)) k.sheet = -1;
                }
                StoryLetters.TestBudget = true;
                story.decisionsToday = 999;
                int onDesk = AdjLetters(r).Count();
                g.adjudication.lastSheetDay = -1;
                Adjudication.Morning(g, map);
                if (AdjLetters(r).Count() != onDesk || !story.deferredLetters.Any(d => d.Is(Adjudication.DecisionDef) && !deferred0.Contains(d))) AdjFail("budget", "with the day's budget spent the sheet did not wait for tomorrow");
                g.adjudication.lastSheetDay = -1;
                Adjudication.Morning(g, map);
                if (story.deferredLetters.Count(d => d.Is(Adjudication.DecisionDef) && !deferred0.Contains(d)) != 1) AdjFail("budget", "a second sheet was written behind the one that waits");
                if (story.deferredLetters.FirstOrDefault(d => d.Is(Adjudication.DecisionDef) && !deferred0.Contains(d)) is { } held && StoryLetters.Urgent(held)) AdjFail("budget", "the sheet is an urgent letter (it must not be exempt from the budget)");
            });
        }

        // ---- the sheet's timing ------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationSheetTiming(Map map)
        {
            Guarded("adjudication: the sheet's answer time ends before the next morning's pass, and a window left open does not hold the day's sheet back", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                AdjSeed a = AdjOffence(s, r, "Zadjt1", assault, officer: true);
                AdjSeed b = AdjOffence(s, r, "Zadjt2", assault, officer: true);
                if (a.k == null || b.k == null)
                {
                    AdjFail("timing", "the seeded assaults were not queued");
                    return;
                }
                AdjYesterday(s, a.k);
                Adjudication.Morning(g, map);
                ChoiceLetter_PrisonDecision letter = AdjLetters(r).FirstOrDefault();
                if (letter == null)
                {
                    AdjFail("timing", "no sheet went out");
                    return;
                }
                // The letter times out (and its cases take the policy's sanction) before a pass a day later: one that ended on that very tick would still be out, and that day's sheet would not go.
                int left = letter.disappearAtTick - s.Now;
                if (!letter.TimeoutActive || left <= 0 || left > GenDate.TicksPerDay - GenDate.TicksPerHour)
                    AdjFail("timing", $"the sheet's answer time ends in {left / (float)GenDate.TicksPerHour:F1} hours (it must end more than an hour before the next morning's pass, a day on)");
                // The warden answered it in the window and left the window open overnight; the next morning there is a new case.
                Adjudication.Answer(g, map, a.k, AdjudicationAnswer.Policy);
                letter.answered = true;
                Find.LetterStack.RemoveLetter(letter);
                AdjYesterday(s, b.k);
                var window = new Dialog_Adjudication(map, -1);
                Find.WindowStack.Add(window);
                r.Add(() => Find.WindowStack.TryRemove(window, doCloseSound: false));
                g.adjudication.lastSheetDay = -1;
                int sheets = AdjLetters(r).Count();
                Adjudication.Morning(g, map);
                if (AdjLetters(r).Count() != sheets + 1 || b.k.sheet < 0) AdjFail("timing", "a window left open held back the next morning's sheet");
                Find.WindowStack.TryRemove(window, doCloseSound: false);
            });
        }

        // ---- what an answer marks --------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationMarks(Map map)
        {
            Guarded("adjudication: an answer marks its own case's record punished, not the newest other one, and solitary marks nothing yet", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                MisconductDef assault = AdjDef("RP_AssaultStaff"), fight = AdjDef("RP_Fight");
                PunishmentPolicy policy0 = g.punishments;
                r.Add(() => g.punishments = policy0);
                g.punishments = new PunishmentPolicy();
                g.punishments.SetKind(assault, Sanction.LoseVisits);
                g.punishments.Set(assault, 4);
                AdjSeed a = AdjOffence(s, r, "Zadjk1", assault, officer: true);
                if (a.k == null)
                {
                    AdjFail("marks", "the seeded assault was not queued");
                    return;
                }
                // A later offence on the file (a fight the sheet does not take, not punished yet): the policy's light sanction for the assault is not for it.
                CompInmate c = a.inmate.Inmate();
                c.AddRecord(new MisconductRecord { def = fight, tick = s.Now + 100, seen = true });
                Adjudication.Answer(g, map, a.k, AdjudicationAnswer.Policy);
                MisconductRecord own = c.records.Find(x => x.def == assault), other = c.records.Find(x => x.def == fight);
                if (c.visitBanUntilTick <= s.Now) AdjFail("marks", "the policy's sanction (no visits) was not imposed");
                if (own == null || !own.punished || other == null || other.punished) AdjFail("marks", $"a light sanction for the assault: the assault's record punished {own?.punished}, the later fight's {other?.punished} (wanted yes, no)");
                // Solitary is served later, by the guard who takes them: the record is not marked punished when the sheet is answered.
                g.punishments.SetKind(assault, Sanction.Solitary);
                g.punishments.Set(assault, 12);
                AdjSeed hole = AdjOffence(s, r, "Zadjk2", assault, officer: true);
                if (hole.k == null) return;
                Adjudication.Answer(g, map, hole.k, AdjudicationAnswer.Policy);
                if (hole.inmate.Inmate().pendingPunishmentHours <= 0 || hole.inmate.Inmate().records.Any(x => x.def == assault && x.punished))
                    AdjFail("marks", $"the policy's solitary: {hole.inmate.Inmate().pendingPunishmentHours} hours due, the record marked punished {hole.inmate.Inmate().records.Any(x => x.def == assault && x.punished)} (wanted due, not yet marked)");
            });
        }

        // ---- another home map --------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationAnotherMap(Map home)
        {
            Guarded("adjudication: a case of an inmate on another home map is decided on theirs", home, r =>
            {
                // (The pass runs once per home map over the game's book. The second map here is small and passed to the pass by hand, as in court regression L9: the inmate is on the home map.)
                var parent = (MapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.Debug_Arena);
                var around = new List<PlanetTile>();
                bool NoRoad(PlanetTile t)
                {
                    Find.WorldGrid.GetTileNeighbors(t, around);
                    return around.All(n => Find.WorldGrid.GetRoadDef(t, n) == null);
                }
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, validator: NoRoad))
                {
                    RPLog.Message("[RimPrison:test] adjudication: no world tile for a second map, the other map's pass is not checked");
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
                var arena = parent.GetComponent<DebugArena>();
                arena.lhs = new List<Pawn>();
                arena.rhs = new List<Pawn>();
                arena.callback = _ => { };
                CourtScene s = AdjBegin(home, r);
                PrisonGame g = s.g;
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                AdjSeed a = AdjOffence(s, r, "Zadjm1", assault, officer: true);
                AdjSeed b = AdjOffence(s, r, "Zadjm2", assault, officer: true);
                if (a.k == null || b.k == null)
                {
                    AdjFail("another map", "the seeded assaults were not queued");
                    return;
                }
                // (Yesterday by both maps' clocks: the pass lists what came before its own map's local midnight, and the second map's tile is a random one up to 27 tiles off, whose local time can be an hour or more
                // ahead of the home map's (time zones are whole hours of longitude, GenDate.TimeZoneAt) - one zone east, a case at the home map's 23:00 yesterday came at the second map's midnight, "today" there, held for
                // the next sheet, and the check failed with no letter, sheet -1 and Pending in four runs.)
                int homeDay = GenLocalDate.DayTick(home), secondDay = GenLocalDate.DayTick(second);
                foreach (AdjudicationCase k in new[] { a.k, b.k }) k.tick = s.Now - System.Math.Max(homeDay, secondDay) - GenDate.TicksPerHour;
                string Clocks() => $"local hour here {GenLocalDate.HourFloat(home):F1} (longitude {Find.WorldGrid.LongLatOf(home.Tile).x:F1}), on the second map {GenLocalDate.HourFloat(second):F1} (longitude {Find.WorldGrid.LongLatOf(second.Tile).x:F1}); "
                                   + $"the case {(s.Now - a.k.tick) / (float)GenDate.TicksPerHour:F1} hours ago, the second map's day began {secondDay / (float)GenDate.TicksPerHour:F1} hours ago";
                // The other map's pass comes first: it lists the home map's cases (they are not closed as gone), and a case is decided on the map its inmate is on.
                Adjudication.Morning(g, second);
                ChoiceLetter_PrisonDecision letter = AdjLetters(r).FirstOrDefault();
                if (letter == null || a.k.sheet < 0 || b.k.sheet < 0 || a.k.answer != AdjudicationAnswer.Pending)
                {
                    AdjFail("another map", $"the other map's pass did not list the cases of an inmate on the home map (letter {letter != null}, sheet {a.k.sheet}, {a.k.answer}; {Clocks()})");
                    return;
                }
                Adjudication.Answer(g, second, a.k, AdjudicationAnswer.DaysAdded, 10);
                if (a.k.answer != AdjudicationAnswer.DaysAdded || a.inmate.Inmate().daysAdded != 10)
                    AdjFail("another map", $"asked from the other map, a case of an inmate on the home map was {a.k.answer} with {a.inmate.Inmate().daysAdded} days added (wanted days added, 10)");
                // The sheet is left, and the next morning's pass (the other map's again) settles what is left: the policy's sanction, on the home map.
                letter.answered = true;
                Find.LetterStack.RemoveLetter(letter);
                g.adjudication.lastSheetDay = -1;
                Adjudication.Morning(g, second);
                if (b.k.answer != AdjudicationAnswer.Policy) AdjFail("another map", $"a case left on the sheet, settled by the other map's pass, is {b.k.answer} (wanted the policy's sanction)");
            });
        }

        // ---- days added -------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationDaysAdded(Map map)
        {
            Guarded("adjudication: days added move the term everywhere it is read, up to the cap, never for a lifer", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                // (Day 30 at least: an inmate admitted 22 days ago needs a clock past that, a negative tick of admission counts as no time served. AdjBegin's rollback puts the real clock back.)
                Find.TickManager.DebugSetTicksGame(System.Math.Max(Find.TickManager.TicksGame, 30 * GenDate.TicksPerDay));
                AdjNoRealLawyers(map, r);
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                AdjSeed a = AdjOffence(s, r, "Zadjd1", assault, officer: true);
                CompInmate c = a.inmate.Inmate();
                if (a.k == null)
                {
                    AdjFail("days added", "the seeded assault was not queued");
                    return;
                }
                // Twelve days to serve, thirteen served: out at midnight, unless the days are added.
                c.sentenceDays = 12;
                c.admittedTick = s.Now - 13 * GenDate.TicksPerDay;
                int parole0 = WantWorker_Parole.EligibleTick(c);
                if (!ReleaseSystem.SentenceServed(g, a.inmate, c)) AdjFail("days added", "the premise is wrong: thirteen days of twelve are not served");
                (int added0, int given0) = (c.daysAdded, g.stats.daysAddedGiven);
                string fell = Adjudication.Answer(g, map, a.k, AdjudicationAnswer.DaysAdded, 10);
                if (fell != null || c.daysAdded != added0 + 10 || c.TermDays != 22 || c.sentenceDays != 12 || a.k.struckOut || g.stats.daysAddedGiven != given0 + 10)
                    AdjFail("days added", $"ten days added: refused {fell}, added {c.daysAdded}, term {c.TermDays} (wanted 22 of a court's 12), struck out {a.k.struckOut}, stat +{g.stats.daysAddedGiven - given0}");
                // The release date moved, and the file shows it (and the register's bar reads the same term).
                if (ReleaseSystem.SentenceServed(g, a.inmate, c)) AdjFail("days added", "an inmate with ten days added to twelve was still released at thirteen");
                c.admittedTick = s.Now - 22 * GenDate.TicksPerDay;
                if (!ReleaseSystem.SentenceServed(g, a.inmate, c)) AdjFail("days added", "an inmate was not released when the added days were served");
                if (WantWorker_Parole.EligibleTick(c) - parole0 != 5 * GenDate.TicksPerDay + (c.admittedTick - (s.Now - 13 * GenDate.TicksPerDay)))
                    AdjFail("days added", $"the parole date did not move by half the added days ({(WantWorker_Parole.EligibleTick(c) - parole0 - (c.admittedTick - (s.Now - 13 * GenDate.TicksPerDay))) / (float)GenDate.TicksPerDay:F1} days for 5)");
                string file = ITab_InmateFile.SentenceText(c);
                if (!file.Contains("22") || !file.Contains("10")) AdjFail("days added", $"the file's sentence line reads \"{file}\" (wanted the term 22 and the 10 added)");
                if (!r.NewEntries.Any(e => e.text.Contains("22"))) AdjFail("days added", "the chronicle has no line for the days added and the new term");
                // The cap, and a camera alone is evidence.
                AdjSeed b = AdjOffence(s, r, "Zadjd2", assault, camera: true);
                if (b.k == null || !b.k.camera || !b.k.Evidenced || b.k.officer) AdjFail("days added", "a camera's sighting alone did not make the evidence");
                else
                {
                    Adjudication.Answer(g, map, b.k, AdjudicationAnswer.DaysAdded, 99);
                    if (b.inmate.Inmate().daysAdded != Adjudication.MaxDays || b.k.days != Adjudication.MaxDays || b.k.struckOut) AdjFail("days added", $"99 days asked: {b.inmate.Inmate().daysAdded} added (the cap is {Adjudication.MaxDays})");
                }
                // A lifer, and death row, have no sentence to add to: the policy's sanction instead, said why.
                AdjSeed life = AdjOffence(s, r, "Zadjd3", assault, officer: true);
                if (life.k == null) return;
                life.inmate.Inmate().sentenceDays = -1;
                string why = Adjudication.Refused(g, map, life.k, life.inmate.Inmate(), AdjudicationAnswer.DaysAdded);
                string lifeFell = Adjudication.Answer(g, map, life.k, AdjudicationAnswer.DaysAdded, 10);
                if (why == null || lifeFell != why || life.k.answer != AdjudicationAnswer.Policy || life.inmate.Inmate().daysAdded != 0 || life.inmate.Inmate().TermDays != -1)
                    AdjFail("days added", $"days added to a lifer: refused {why}, fell back with {lifeFell}, {life.k.answer}, added {life.inmate.Inmate().daysAdded}, term {life.inmate.Inmate().TermDays}");
                // Cutting the term (an appeal) takes the days added first and never goes below the days served.
                c.sentenceDays = 12;
                c.daysAdded = 10;
                c.admittedTick = s.Now - 5 * GenDate.TicksPerDay;
                int cut = c.CutTerm(15);
                if (cut != 15 || c.daysAdded != 0 || c.sentenceDays != 7 || c.TermDays != 7) AdjFail("days added", $"a cut of 15 days left {c.daysAdded} added and a court's {c.sentenceDays} (wanted 0 and 7, cut {cut})");
                if (c.CutTerm(99) != 2 || c.TermDays != 5) AdjFail("days added", $"a cut past the days served left the term at {c.TermDays} (wanted 5, the days served)");
            });
        }

        // ---- remission -----------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationRemission(Map map)
        {
            Guarded("adjudication: loss of remission zeroes the earned days", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                g.remission = true;
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                AdjSeed a = AdjOffence(s, r, "Zadjr1", assault, officer: true);
                CompInmate c = a.inmate.Inmate();
                if (a.k == null)
                {
                    AdjFail("remission", "the seeded assault was not queued");
                    return;
                }
                // Three days off earned (the sentence was 30, is 27), nine clean days towards the next.
                (c.sentenceDays, c.remissionEarned, c.cleanDays) = (27, 3, 9);
                int wiped0 = g.stats.remissionWiped;
                string fell = Adjudication.Answer(g, map, a.k, AdjudicationAnswer.LoseRemission);
                if (fell != null || c.remissionEarned != 0 || c.cleanDays != 0 || c.sentenceDays != 30 || a.k.days != 3 || g.stats.remissionWiped != wiped0 + 1 || c.pendingPunishmentHours != 0)
                    AdjFail("remission", $"loss of remission: refused {fell}, earned {c.remissionEarned}, clean days {c.cleanDays}, sentence {c.sentenceDays} (wanted 0, 0, 30), days given back {a.k.days}, the hole {c.pendingPunishmentHours} hours (none: it is instead of the table's)");
                if (!r.NewEntries.Any(e => e.text.Contains(a.inmate.LabelShort) && e.text.Contains("remission"))) AdjFail("remission", "the chronicle has no line for the remission lost");
                // Nothing earned, nothing to take: the policy's sanction instead, said why.
                AdjSeed none = AdjOffence(s, r, "Zadjr2", assault, officer: true);
                if (none.k == null) return;
                // (A clean run towards the next day off, nothing earned: still nothing to take, whatever the policy was before.)
                (none.inmate.Inmate().remissionEarned, none.inmate.Inmate().cleanDays) = (0, 5);
                string why = Adjudication.Refused(g, map, none.k, none.inmate.Inmate(), AdjudicationAnswer.LoseRemission);
                if (why == null || Adjudication.Answer(g, map, none.k, AdjudicationAnswer.LoseRemission) != why || none.k.answer != AdjudicationAnswer.Policy)
                    AdjFail("remission", $"remission asked for an inmate with none earned: refused {why}, {none.k.answer}");
            });
        }

        // ---- the appeal ------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationAppeal(Map map)
        {
            Guarded("adjudication: days added that nobody saw are appealed and struck out", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                AdjNoRealLawyers(map, r);
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                // No jailhouse lawyer in the prison: the days stand, and the wing calls it unfair.
                AdjSeed alone = AdjOffence(s, r, "Zadja1", assault);
                if (alone.k == null)
                {
                    AdjFail("appeal", "the seeded assault was not queued");
                    return;
                }
                if (alone.k.Evidenced) AdjFail("appeal", "an assault nobody saw is evidenced");
                Adjudication.Answer(g, map, alone.k, AdjudicationAnswer.DaysAdded, 8);
                CompInmate ac = alone.inmate.Inmate();
                if (ac.daysAdded != 8 || alone.k.struckOut || ac.mainCause != "RP_Cause_Unfair") AdjFail("appeal", $"days nobody saw, with no lawyer in the prison: added {ac.daysAdded} (wanted 8), struck out {alone.k.struckOut}, cause {ac.mainCause}");
                // A jailhouse lawyer: the appeal strikes them out, and the prison pays the costs.
                Pawn lawyer = s.Inmate("Zlawyer1");
                lawyer.Inmate().reputations.Add(RPReputationDefOf.RP_JailhouseLawyer);
                AdjSeed seeded = AdjOffence(s, r, "Zadja2", assault);
                if (seeded.k == null) return;
                (int funds0, int appeals0, int struck0, int given0) = (g.ledger.funds, g.stats.appealsFiled, g.stats.daysStruckOut, g.stats.daysAddedGiven);
                Adjudication.Answer(g, map, seeded.k, AdjudicationAnswer.DaysAdded, 12);
                CompInmate sc = seeded.inmate.Inmate();
                if (sc.daysAdded != 0 || !seeded.k.struckOut || seeded.k.answer != AdjudicationAnswer.DaysAdded || sc.TermDays != 30) AdjFail("appeal", $"days nobody saw, a lawyer in the prison: added {sc.daysAdded} (wanted 0), struck out {seeded.k.struckOut}, term {sc.TermDays}");
                if (g.ledger.funds != funds0 - ReputationBehavior_Lawyer.CourtCosts || g.stats.appealsFiled != appeals0 + 1 || g.stats.daysStruckOut != struck0 + 12 || g.stats.daysAddedGiven != given0)
                    AdjFail("appeal", $"the appeal's costs: funds {g.ledger.funds - funds0} (wanted -{ReputationBehavior_Lawyer.CourtCosts}), appeals +{g.stats.appealsFiled - appeals0}, days struck out +{g.stats.daysStruckOut - struck0} (wanted 12), days given +{g.stats.daysAddedGiven - given0} (wanted 0)");
                if (!Adjudication.FileLines(seeded.inmate).Any(l => l.Contains(Adjudication.AnswerText(seeded.k)))) AdjFail("appeal", "the file has no line for the days struck out");
                if (Adjudication.DaysAddedInWeek(g) != 8) AdjFail("appeal", $"the week's days added count {Adjudication.DaysAddedInWeek(g)} (wanted 8: the struck-out days are not days added)");
                // What an officer saw is not appealed away, lawyer or not.
                AdjSeed evidenced = AdjOffence(s, r, "Zadja3", assault, officer: true);
                if (evidenced.k == null) return;
                Adjudication.Answer(g, map, evidenced.k, AdjudicationAnswer.DaysAdded, 6);
                if (evidenced.inmate.Inmate().daysAdded != 6 || evidenced.k.struckOut) AdjFail("appeal", $"days an officer saw, a lawyer in the prison: added {evidenced.inmate.Inmate().daysAdded} (wanted 6), struck out {evidenced.k.struckOut}");
            });
        }

        // ---- the charge ------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationCharge(Map map)
        {
            Guarded("adjudication: a charge for an armed assault on staff opens a case the court tries on its day", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                if (HearingRooms.Available(map, s.pm))
                {
                    RPLog.Message("[RimPrison:test] adjudication: a hearing room is on the map, the charge's court day is not checked here");
                    return;
                }
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                Pawn victim = s.Guard("Zadjvictim2");
                AdjSeed armed = AdjOffence(s, r, "Zadjc1", assault, officer: true, weapon: true, victim: victim);
                AdjSeed unarmed = AdjOffence(s, r, "Zadjc2", assault, victim: victim);
                AdjSeed escape = AdjOffence(s, r, "Zadjc3", AdjDef("RP_EscapeAttempt"), weapon: true, evidence: true);
                if (armed.k == null || unarmed.k == null || escape.k == null)
                {
                    AdjFail("charge", "the seeded offences were not queued");
                    return;
                }
                if (!armed.stateStarted)
                {
                    RPLog.Message("[RimPrison:test] adjudication: the assault mental state could not be started on the throwaway, the charge is not checked");
                    return;
                }
                if (!armed.k.Chargeable || unarmed.k.Chargeable || escape.k.Chargeable)
                    AdjFail("charge", $"what can be charged: an armed assault on staff {armed.k.Chargeable}, an unarmed one {unarmed.k.Chargeable}, an armed escape attempt {escape.k.Chargeable} (yes, no, no)");
                CompInmate kc = armed.inmate.Inmate();
                if (Adjudication.Refused(g, map, unarmed.k, unarmed.inmate.Inmate(), AdjudicationAnswer.Charge) == null) AdjFail("charge", "an unarmed assault can be charged");
                int sentence0 = kc.sentenceDays;
                SecurityLevelDef level0 = kc.security;
                (int charges0, int trials0, int convictions0) = (g.stats.chargesBrought, g.stats.trials, g.stats.convictions);
                string fell = Adjudication.Answer(g, map, armed.k, AdjudicationAnswer.Charge);
                MurderTrial t = Trials.TrialOf(g, armed.inmate);
                if (fell != null || t == null)
                {
                    AdjFail("charge", $"the charge opened no case (refused {fell})");
                    return;
                }
                if (t.IsMurder || t.offence != assault || !t.staffVictim || t.victimId != victim.thingIDNumber || armed.k.trialId != t.id || armed.k.scene != null || g.stats.chargesBrought != charges0 + 1
                    || !t.weapon || !t.witnesses.Exists(w => w.staff) || t.priorRecord != 0 || t.trialTick - s.Now < (Trials.TrialDelayDays - 1) * GenDate.TicksPerDay)
                    AdjFail("charge", $"the case: offence {t.offence?.defName}, murder {t.IsMurder}, staff victim {t.staffVictim}, weapon {t.weapon}, an officer a witness {t.witnesses.Exists(w => w.staff)}, trial in {(t.trialTick - s.Now) / (float)GenDate.TicksPerDay:F1} days, charges +{g.stats.chargesBrought - charges0}");
                // (The scene's staff witnesses are whoever was in sight of the spot, nearest first: the officer beside the accused, or a live guard who happened to be closer.)
                string officerWitness = t.witnesses.FirstOrDefault(w => w.staff)?.name ?? armed.officer.LabelShort;
                if (!Trials.OnRemand(g, armed.inmate)) AdjFail("charge", "the accused is not on remand");
                string charge = r.NewLetters.OfType<ChoiceLetter>().Select(l => l.Label.Resolve() + " | " + l.Text.Resolve()).FirstOrDefault(x => x.StartsWith("RP_AdjChargeLabel".Translate(armed.inmate.LabelShort).Resolve())) ?? "";
                if (!charge.Contains(Trials.DateText(t)) || !charge.Contains(victim.LabelShort)) AdjFail("charge", $"the charge letter does not give the trial's day and the officer who was hit ({charge})");
                if (!Trials.FileLines(armed.inmate).Any(l => l.Contains(victim.LabelShort) && !l.ToLowerInvariant().Contains("murder"))) AdjFail("charge", "the file does not say what the accused is charged with, in the offence's words");
                if (Trials.Key(t, "RP_FileTrial") != "RP_FileTrial_Charge") AdjFail("charge", "the charge's words are the murder trial's");
                // A killing by the same accused while the case is open makes it a murder case (one case, tried for the death), and the victim of the killing is the case's.
                Pawn dead = s.Inmate("Zadjdead1");
                MurderTrial same = Trials.Open(g, map, dead, armed.inmate, kc, false, null);
                if (same != t || !t.IsMurder || t.victimId != dead.thingIDNumber || t.staffVictim) AdjFail("charge", $"a killing during the open charge: same case {same == t}, murder {t.IsMurder}, victim {t.victimName}");
                // Put back the charge for the verdict (the check is of the assault's trial).
                t.offence = assault;
                t.victimId = victim.thingIDNumber;
                t.victimName = victim.LabelShort;
                t.staffVictim = true;
                // The court sits on its day (nobody got at the officer: an officer who saw it, caught at the scene, convicts): the table's days are added, no more, and no move up a level.
                t.trialTick = s.Now;
                s.Hour(9);
                if (g.stats.trials != trials0 || t.courtTick >= 0) AdjFail("charge", "the court sat before ten in the morning");
                s.Hour(11);
                if (t.courtTick < 0 || kc.forcedActivity != RPDefOf.RP_Lockup) AdjFail("charge", $"off-site, the accused was not taken to court at eleven (court tick {t.courtTick})");
                s.Hour(16);
                if (g.stats.trials != trials0 + 1 || g.stats.convictions != convictions0 + 1 || g.story.trials.Contains(t))
                    AdjFail("charge", $"an armed assault an officer saw did not convict (trials +{g.stats.trials - trials0}, convictions +{g.stats.convictions - convictions0}, strength {Trials.Strength(t, map, wardenView: false):F2})");
                int added = kc.sentenceDays - sentence0;
                if (added < assault.chargeDays.min || added > assault.chargeDays.max || kc.IsLifer || kc.security != level0 || SupermaxSystem.IsIn(kc))
                    AdjFail("charge", $"a conviction added {added} days (the table gives {assault.chargeDays.min} to {assault.chargeDays.max}) and the level went {level0?.defName} -> {kc.security?.defName}");
                if (Trials.OnRemand(g, armed.inmate)) AdjFail("charge", "the accused is on remand after the verdict");
                string verdict = r.NewLetters.OfType<ChoiceLetter>().Select(l => l.Label.Resolve() + " | " + l.Text.Resolve()).FirstOrDefault(x => x.StartsWith("RP_TrialConvictedLabel".Translate(armed.inmate.LabelShort).Resolve())) ?? "";
                if (!verdict.Contains("armed assault") || verdict.Contains("maximum security") || !verdict.Contains(officerWitness)) AdjFail("charge", $"the verdict's letter ({verdict})");
                // A charge nobody can prove (no officer in sight, no camera): acquitted, and the offence stays on the file.
                Pawn victim2 = s.Guard("Zadjvictim3");
                AdjSeed weak = AdjOffence(s, r, "Zadjc4", assault, weapon: true, victim: victim2);
                if (weak.k == null || !weak.k.Chargeable)
                {
                    AdjFail("charge", "a second armed assault could not be charged");
                    return;
                }
                // (The scene as the live prison's own guards and cameras happened to see it is taken off: nobody saw this one but the officer it was done to, who is not a witness.)
                weak.k.scene.witnesses.Clear();
                (weak.k.scene.camera, weak.k.scene.caughtAtScene) = (false, false);
                // (An earlier murder on their file: an assault's acquittal must not take it off.)
                weak.inmate.Inmate().AddRecord(new MisconductRecord { def = RPDeathDefOf.RP_Murder, tick = s.Now - 5 * GenDate.TicksPerDay, seen = true });
                int sentence1 = weak.inmate.Inmate().sentenceDays;
                (int acquittals0, int records0) = (g.stats.acquittals, weak.inmate.Inmate().records.Count);
                Adjudication.Answer(g, map, weak.k, AdjudicationAnswer.Charge);
                MurderTrial wt = Trials.TrialOf(g, weak.inmate);
                if (wt == null) return;
                Trials.Verdict(g, map, wt);
                CompInmate wc = weak.inmate.Inmate();
                if (g.stats.acquittals != acquittals0 + 1 || wc.sentenceDays != sentence1 || wc.records.Count != records0 || !wc.records.Any(x => x.def == assault) || !wc.records.Any(x => x.def == RPDeathDefOf.RP_Murder))
                    AdjFail("charge", $"an unproved charge: acquittals +{g.stats.acquittals - acquittals0}, sentence {sentence1} -> {wc.sentenceDays}, records {records0} -> {wc.records.Count} (the assault stays on the file)");
                // A case from before charges has no offence: it is the murder case.
                var old = new MurderTrial();
                if (!old.IsMurder || Trials.Key(old, "RP_FileTrial") != "RP_FileTrial") AdjFail("charge", "a trial with no offence (a save from before charges) is not the murder case");
            });
        }

        // ---- unanswered ----------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationUnanswered(Map map)
        {
            Guarded("adjudication: a case nobody answers takes the policy's sanction", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                int hours = g.punishments.KindFor(assault) == Sanction.Solitary ? g.punishments.SolitaryHoursFor(assault) : 0;
                AdjSeed a = AdjOffence(s, r, "Zadju1", assault, officer: true);
                if (a.k == null)
                {
                    AdjFail("unanswered", "the seeded assault was not queued");
                    return;
                }
                AdjYesterday(s, a.k);
                Adjudication.Morning(g, map);
                ChoiceLetter_PrisonDecision letter = AdjLetters(r).FirstOrDefault();
                if (letter == null)
                {
                    AdjFail("unanswered", "no sheet went out");
                    return;
                }
                // The window was opened and left: the letter is gone, the case still waits (a sheet nobody answered is not settled while it is on the desk or a window is open).
                letter.answered = true;
                Find.LetterStack.RemoveLetter(letter);
                if (a.k.answer != AdjudicationAnswer.Pending) AdjFail("unanswered", "a case was answered without anyone answering it");
                // The next morning's pass settles it: the policy's sanction.
                g.adjudication.lastSheetDay = -1;
                Adjudication.Morning(g, map);
                if (a.k.answer != AdjudicationAnswer.Policy || hours > 0 && a.inmate.Inmate().pendingPunishmentHours < hours)
                    AdjFail("unanswered", $"the case left on the sheet: {a.k.answer}, the hole {a.inmate.Inmate().pendingPunishmentHours} hours (wanted the policy's {hours})");
                // An inmate who is gone before the sheet: no sheet for them (nothing to decide).
                AdjSeed gone = AdjOffence(s, r, "Zadju2", assault, officer: true);
                if (gone.k == null) return;
                AdjYesterday(s, gone.k);
                gone.inmate.Inmate().admittedTick = s.Now;
                g.adjudication.lastSheetDay = -1;
                Adjudication.Morning(g, map);
                if (gone.k.answer != AdjudicationAnswer.Lapsed || gone.k.sheet >= 0) AdjFail("unanswered", $"a case of an earlier stay is {gone.k.answer} on sheet {gone.k.sheet} (wanted closed, not listed)");
            });
        }

        // ---- the inspector -----------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckAdjudicationInspector(Map map)
        {
            Guarded("adjudication: the inspector counts the days added in a week", map, r =>
            {
                CourtScene s = AdjBegin(map, r);
                PrisonGame g = s.g;
                MisconductDef assault = AdjDef("RP_AssaultStaff");
                Pawn p = s.Inmate("Zadji1");
                AdjudicationCase Case(int days, int hoursAgo) => new AdjudicationCase
                {
                    id = g.adjudication.nextId++, inmateId = p.thingIDNumber, inmateName = p.LabelShort, offence = assault, tick = s.Now - hoursAgo * GenDate.TicksPerHour, officer = true,
                    answer = AdjudicationAnswer.DaysAdded, answerTick = s.Now - hoursAgo * GenDate.TicksPerHour, days = days, sheet = 1,
                };
                g.adjudication.cases.Add(Case(Adjudication.WeekLimit, 2));
                if (InspectorCandidate(g, map, InspectionReport.AddedKey) != null) AdjFail("inspector", $"{Adjudication.WeekLimit} days in a week (the limit) is written down");
                g.adjudication.cases.Add(Case(10, 30));
                InspectionFinding f = InspectorCandidate(g, map, InspectionReport.AddedKey);
                if (f == null || f.id != Adjudication.WeekLimit + 10)
                {
                    AdjFail("inspector", $"{Adjudication.WeekLimit + 10} days in a week is {(f == null ? "not written down" : "written with " + f.id + " days")}");
                    return;
                }
                string words = InspectionReport.Describe(f, map);
                if (!words.Contains((Adjudication.WeekLimit + 10).ToString()) || !words.Contains(Adjudication.WeekLimit.ToString())) AdjFail("inspector", $"the finding reads \"{words}\" (wanted the days and the limit)");
                if (InspectionReport.TabOf(InspectionReport.AddedKey) != MainTabWindow_Prison.Tab.Policy) AdjFail("inspector", "the finding's to-do line does not open the Policy tab");
                if (!InspectionReport.Present(f, g, map, null)) AdjFail("inspector", "the finding is not present while the days stand");
                // Struck out, or older than a week: not counted. The finding is gone when the week's days fall to the limit.
                g.adjudication.cases[1].struckOut = true;
                if (InspectionReport.Present(f, g, map, null) || InspectorCandidate(g, map, InspectionReport.AddedKey) != null) AdjFail("inspector", "days struck out on appeal are counted");
                g.adjudication.cases[1].struckOut = false;
                g.adjudication.cases[1].answerTick = s.Now - (Adjudication.WeekDays * 24 + 1) * GenDate.TicksPerHour;
                if (Adjudication.DaysAddedInWeek(g) != Adjudication.WeekLimit || InspectionReport.Present(f, g, map, null)) AdjFail("inspector", "days added more than a week ago are counted");
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The sheet's whole state as one comparable string: the policy, the book (every case with its evidence, answer and scene), the counters and the days added on each inmate who has some (an inmate who arrives or
        /// leaves while the game is saved and loaded, with none, is not the sheet's).
        /// </summary>
        private static string AdjudicationDescribe(PrisonGame g, Map map)
        {
            PrisonStats st = g.stats;
            return $"{g.adjudicate}/{g.adjudication.Describe()}/{st.adjudicated}/{st.daysAddedGiven}/{st.daysStruckOut}/{st.remissionWiped}/{st.chargesBrought}/days=" +
                   string.Join(",", map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { daysAdded: not 0 }).OrderBy(p => p.thingIDNumber).Select(p => $"{p.thingIDNumber}:{p.Inmate().daysAdded}"));
        }

        private static bool adjSeeded;
        private static string adjSeedDescribed;
        private static (bool adjudicate, AdjudicationBook book, int[] stats, List<(int id, int added)> added) adjBefore;

        /// <summary>
        /// Saveload plan, before the snapshot: the policy on, a book with a case waiting on a sheet (with its prosecution's file: an armed assault on staff, a witness, off every default), one answered with days struck out on
        /// appeal, one with remission lost, one charged, one queued for tomorrow, the counters off their defaults and days added on two inmates.
        /// </summary>
        private static void SeedAdjudicationForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null).OrderBy(p => p.thingIDNumber).Take(2).ToList();
            Pawn officer = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => x.GetComp<CompStaff>()?.role != null);
            if (inmates.Count < 2 || officer == null)
            {
                RPLog.Message("[RimPrison:test] saveload adjudication: not seeded (needs two inmates and an officer)");
                return;
            }
            PrisonStats st = g.stats;
            adjBefore = (g.adjudicate, g.adjudication, new[] { st.adjudicated, st.daysAddedGiven, st.daysStruckOut, st.remissionWiped, st.chargesBrought }, map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null).Select(p => (p.thingIDNumber, p.Inmate().daysAdded)).ToList());
            int now = Find.TickManager.TicksGame;
            Pawn a = inmates[0], b = inmates[1];
            var scene = new MurderTrial
            {
                id = -1, offence = AdjDef("RP_AssaultStaff"), killerId = a.thingIDNumber, killerName = a.LabelShort, victimId = officer.thingIDNumber, victimName = officer.LabelShort, staffVictim = true,
                caughtAtScene = true, camera = true, weapon = true, priorRecord = 2,
                witnesses = new List<TrialWitness> { new TrialWitness { id = officer.thingIDNumber, name = officer.LabelShort, staff = true, known = true } },
            };
            g.adjudicate = true;
            g.adjudication = new AdjudicationBook
            {
                nextId = 9, nextSheet = 4, lastSheetDay = 12,
                cases = new List<AdjudicationCase>
                {
                    new AdjudicationCase { id = 5, inmateId = a.thingIDNumber, inmateName = a.LabelShort, offence = AdjDef("RP_AssaultStaff"), tick = now - 5000, officer = true, officerName = officer.LabelShort, camera = true, weapon = true, record = 2, sheet = 3, victimId = officer.thingIDNumber, victimName = officer.LabelShort, scene = scene },
                    new AdjudicationCase { id = 6, inmateId = b.thingIDNumber, inmateName = b.LabelShort, offence = AdjDef("RP_EscapeAttempt"), tick = now - 9000, answer = AdjudicationAnswer.DaysAdded, answerTick = now - 3000, days = 12, struckOut = true, sheet = 2 },
                    new AdjudicationCase { id = 7, inmateId = a.thingIDNumber, inmateName = a.LabelShort, offence = AdjDef("RP_Riot"), tick = now - 9500, officer = true, answer = AdjudicationAnswer.LoseRemission, answerTick = now - 100, days = 3, sheet = 2 },
                    new AdjudicationCase { id = 8, inmateId = b.thingIDNumber, inmateName = b.LabelShort, offence = AdjDef("RP_RiotJoined"), tick = now - 700, camera = true, record = 1 },
                    new AdjudicationCase { id = 4, inmateId = b.thingIDNumber, inmateName = b.LabelShort, offence = AdjDef("RP_AssaultStaff"), tick = now - 9900, weapon = true, answer = AdjudicationAnswer.Charge, answerTick = now - 200, trialId = 77, sheet = 1, victimId = officer.thingIDNumber, victimName = officer.LabelShort },
                },
            };
            (st.adjudicated, st.daysAddedGiven, st.daysStruckOut, st.remissionWiped, st.chargesBrought) = (5, 17, 12, 2, 3);
            a.Inmate().daysAdded = 9;
            b.Inmate().daysAdded = 4;
            adjSeeded = true;
            adjSeedDescribed = AdjudicationDescribe(g, map);
            RPLog.Message($"[RimPrison:test] saveload adjudication: {adjSeedDescribed.Length} characters seeded");
        }

        /// <summary>After the reload: the book, the policy, the counters and the days added came back whole (the snapshot compared them too); then the plan's seeds go.</summary>
        private static void CheckAdjudicationAfterReload(Map map)
        {
            if (!adjSeeded) return;
            PrisonGame g = PrisonGame.Instance;
            string after = AdjudicationDescribe(g, map);
            bool whole = after == adjSeedDescribed && g.adjudication.cases.Count == 5 && g.adjudication.cases[0].scene is { offence: { } o } scene && o.defName == "RP_AssaultStaff" && scene.witnesses.Count == 1;
            RPLog.Message($"[RimPrison:test] saveload adjudication: {(whole ? "intact" : "changed")}");
            if (!whole) reloadProblems.Add($"the adjudication sheet's state changed in the round trip: {adjSeedDescribed} -> {after}");
            PrisonStats st = g.stats;
            (g.adjudicate, g.adjudication) = (adjBefore.adjudicate, adjBefore.book);
            (st.adjudicated, st.daysAddedGiven, st.daysStruckOut, st.remissionWiped, st.chargesBrought) = (adjBefore.stats[0], adjBefore.stats[1], adjBefore.stats[2], adjBefore.stats[3], adjBefore.stats[4]);
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } c) c.daysAdded = adjBefore.added.Find(x => x.id == p.thingIDNumber).added;
            adjSeeded = false;
        }
    }
}
