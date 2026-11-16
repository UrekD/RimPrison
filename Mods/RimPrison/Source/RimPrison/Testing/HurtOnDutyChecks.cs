using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Checks for hurt on duty (<see cref="HurtOnDuty"/>, see WORKLOG): what makes a case and how bad it is, one case and one letter (a second hook joins, a different attacker is a new case, four guards in an hour share a
    /// letter), the minor case with no letter, the leave (paid, off the roster, back with the reason cleared and no rest), compensation paid once (a standing order, a held letter that lapses, no money), back on the next
    /// shift, the wariness on return, a lasting injury's pension and desk post, the officer let go or dead, the hostage path, the in-house expiry, the leaves that do not shorten each other, the record, and the round trip
    /// through a save (the seed and the check after the reload are below). Each runs in its own <see cref="Guarded"/> scope from <c>FireAllEvents</c>, the clock moved on where the check wants days (put back by the rollback).
    /// </summary>
    public static partial class AutoTest
    {
        private static void HodFail(string check, string what) => problems0.Add($"hurt on duty ({check}): {what}");

        private const int HodDay = GenDate.TicksPerDay, HodHour = GenDate.TicksPerHour;

        /// <summary>A wound that does not bleed (vanilla has no named constant for it).</summary>
        private static HediffDef HodBruise => DefDatabase<HediffDef>.GetNamed("Bruise");

        /// <summary>A text without its colour tags (a resolved keyed string colours its numbers, money and dates).</summary>
        private static string HodPlain(string text) => ColoredText.StripTags(text ?? "");

        private static bool HodHas(Pawn p, ThoughtDef def, Pawn from = null) =>
            p.needs?.mood?.thoughts.memories.Memories.Any(m => m.def == def && (from == null || m.otherPawn == from)) == true;

        private static int HodMemories(Pawn p, ThoughtDef def) => p.needs?.mood?.thoughts.memories.Memories.Count(m => m.def == def) ?? 0;

        /// <summary>
        /// The game's own memory pass for an hour (the mood need's interval, 150 ticks, an hour of it): vanilla discards a social memory with no opinion offset at its first, so a memory that is still there after it is
        /// one that lasts. (Calling it does not move the clock.)
        /// </summary>
        private static void HodAnHourOfMemories(Pawn p)
        {
            for (int i = 0; i < GenDate.TicksPerHour / 150 + 1; i++) p.needs?.mood?.thoughts.memories.MemoryThoughtInterval();
        }

        /// <summary>The state the checks share and the rollback does not put back: the case counter, the hazard pay's clock, the day's decisions and the budget switch, the held letters, the standing order. Funds are ample.</summary>
        private static void HodBegin(Rollback r)
        {
            PrisonGame g = PrisonGame.Instance;
            (int id0, int hazard0, int today0, bool budget0) = (g.story.nextInjuryCaseId, g.story.lastHazardDemandTick, g.story.decisionsToday, StoryLetters.TestBudget);
            List<ChoiceLetter_PrisonDecision> held0 = g.story.deferredLetters.ToList();
            bool hadOrder = g.standingOrders.TryGetValue(HurtOnDuty.DecisionDef, out string order0);
            r.Add(() =>
            {
                (g.story.nextInjuryCaseId, g.story.lastHazardDemandTick, g.story.decisionsToday, StoryLetters.TestBudget) = (id0, hazard0, today0, budget0);
                g.story.deferredLetters.Clear();
                g.story.deferredLetters.AddRange(held0);
                if (hadOrder) g.standingOrders[HurtOnDuty.DecisionDef] = order0;
                else g.standingOrders.Remove(HurtOnDuty.DecisionDef);
            });
            g.ledger.funds = 10000;
        }

        /// <summary>The hurt-on-duty letters a check made: on the stack, and the ones held for the next day.</summary>
        private static List<ChoiceLetter_PrisonDecision> HodLetters(Rollback r) =>
            r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Concat(PrisonGame.Instance.story.deferredLetters).Where(d => d.Is(HurtOnDuty.DecisionDef)).Distinct().ToList();

        /// <summary>The warden's answer by the letter's own path (answered, chosen, taken off the stack).</summary>
        private static void HodAnswer(ChoiceLetter_PrisonDecision letter, string key, string check)
        {
            IReadOnlyList<string> options = letter.decision.Worker.Options(letter);
            int index = -1;
            for (int i = 0; i < options.Count; i++)
                if (options[i] == key) index = i;
            if (index < 0)
            {
                HodFail(check, $"the letter does not offer {key} (it offers {string.Join(", ", options)})");
                return;
            }
            if (letter.decision.Worker.Disabled(letter, index) is { } why)
            {
                HodFail(check, $"the answer {key} is disabled on the letter: {why}");
                return;
            }
            letter.answered = true;
            PrisonDecisions.Choose(letter, index);
            Find.LetterStack.RemoveLetter(letter);
        }

        private static readonly System.Reflection.MethodInfo MakeDownedMethod = HarmonyLib.AccessTools.Method(typeof(Pawn_HealthTracker), "MakeDowned");

        /// <summary>
        /// An officer put down by an inmate: the game's own downing (<c>MakeDowned</c>, the one the product's postfix is on, with the inmate as the instigator). (<c>forceDowned</c> and a state check do nothing: vanilla only
        /// downs a pawn whose pain or movement says so.) False when the officer is not down after it.
        /// </summary>
        private static bool HodDown(Pawn guard, Pawn inmate)
        {
            if (!guard.Downed) MakeDownedMethod.Invoke(guard.health, new object[] { new DamageInfo(DamageDefOf.Blunt, 1f, 0f, -1f, inmate), null });
            return guard.Downed;
        }

        /// <summary>A wound that is permanent for certain (a cut: the bruise has no way to become permanent, so it would be a plain wound).</summary>
        private static Hediff_Injury HodPermanent(Pawn p, string check)
        {
            Hediff_Injury scar = S31Wound(p, HediffDefOf.Cut, 2f, permanent: true);
            if (!scar.IsPermanent()) HodFail(check, "premise: the wound made permanent is not permanent");
            return scar;
        }

        /// <summary>
        /// The officer's case state as it is, for a check that hurts one of the live roster (a case puts a real guard on a paid leave that nothing else puts back, and the careers check later in the run finds them
        /// off site): the returned action puts it back - the cases, the leave and its reason, the open case, the counter, the letters it sent.
        /// </summary>
        private static System.Action HodSnapshot(Pawn p)
        {
            CompStaff sc = p.GetComp<CompStaff>();
            PrisonGame g = PrisonGame.Instance;
            (List<InjuryCase> cases0, int case0, int until0, int reason0, int rushed0, bool rest0, int id0) = (sc.injuries.ToList(), sc.injuryCaseId, sc.leaveUntilTick, sc.leaveReason, sc.rushedBackUntilTick, sc.restPending, g.story.nextInjuryCaseId);
            var letters0 = new HashSet<Letter>(StoryLetters.Open());
            return () =>
            {
                sc.injuries = cases0;
                (sc.injuryCaseId, sc.leaveUntilTick, sc.leaveReason, sc.rushedBackUntilTick, sc.restPending, g.story.nextInjuryCaseId) = (case0, until0, reason0, rushed0, rest0, id0);
                foreach (ChoiceLetter_PrisonDecision d in StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Where(x => x.Is(HurtOnDuty.DecisionDef) && !letters0.Contains(x)).ToList())
                {
                    d.answered = true;
                    g.story.deferredLetters.Remove(d);
                    Find.LetterStack.RemoveLetter(d);
                }
                Roster.Invalidate();
                StaffCareers.InvalidateLeads();
            };
        }

        /// <summary>One real tick of the officer's comp at a tick their hourly work is due (the clock is moved on to the next one; put back by the rollback of <see cref="S31Guard"/>): what the game does once an hour.</summary>
        private static void HodHourlyTick(Pawn p)
        {
            int into = ((Find.TickManager.TicksGame + p.thingIDNumber.HashOffset()) % HodHour + HodHour) % HodHour;
            if (into != 0) Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + HodHour - into);
            p.GetComp<CompStaff>().CompTickInterval(1);
        }

        // ---- how bad ---------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The severity is read off what is visible and as young as the incident: no wound or a bruise is minor, a wound of 10 HP or a bleeding one is serious, a permanent visible injury or a lost part is lasting - and a
        /// permanent injury or a missing part older than the incident (a scar from long ago) is not the incident's. Fails without the age window (the old scar and the old stump would be lasting) or the bleeding test.
        /// </summary>
        private static void CheckHodSeverity(Map map)
        {
            const string check = "severity";
            Guarded("hurt on duty: how bad the injury is", map, r =>
            {
                Pawn p = S31Guard(map, r);
                int since = Find.TickManager.TicksGame - HurtOnDuty.AssaultWindow;
                void Expect(string what, int level)
                {
                    int got = HurtOnDuty.Severity(p, since);
                    if (got != level) HodFail(check, $"{what}: severity {got}, expected {level}");
                }
                BodyPartRecord arm = p.RaceProps.body.AllParts.First(x => x.def == BodyPartDefOf.Arm);
                // (A lost arm is a missing-part hediff for the arm and one for each part under it: all of them as old as the one the check sets.)
                void LoseArm(int age)
                {
                    p.health.AddHediff(HediffDefOf.MissingBodyPart, arm);
                    foreach (Hediff_MissingPart gone in p.health.hediffSet.hediffs.OfType<Hediff_MissingPart>()) gone.ageTicks = age;
                }
                Expect("no wound", HurtOnDuty.Minor);
                S31Wound(p, HodBruise, 3f);
                Expect("a bruise of 3 HP", HurtOnDuty.Minor);
                S31Clean(p);
                S31Wound(p, HodBruise, 12f);
                Expect("a bruise of 12 HP (not bleeding)", HurtOnDuty.Serious);
                S31Clean(p);
                Hediff_Injury cut = S31Wound(p, HediffDefOf.Cut, 3f);
                if (!cut.Bleeding) HodFail(check, "premise: a fresh cut of 3 HP does not bleed on this guard");
                else Expect("a bleeding cut of 3 HP", HurtOnDuty.Serious);
                S31Clean(p);
                HodPermanent(p, check);
                Expect("a permanent visible injury", HurtOnDuty.Lasting);
                S31Clean(p);
                HodPermanent(p, check).ageTicks = 5 * HurtOnDuty.AssaultWindow;
                Expect("a permanent injury from before the incident", HurtOnDuty.Minor);
                S31Clean(p);
                LoseArm(0);
                Expect("a lost arm", HurtOnDuty.Lasting);
                S31Clean(p);
                LoseArm(5 * HurtOnDuty.AssaultWindow);
                Expect("a stump from before the incident", HurtOnDuty.Minor);
                // The window is the caller's: a hostage crisis that began long ago owns an injury from its start.
                S31Clean(p);
                HodPermanent(p, check).ageTicks = 3 * HurtOnDuty.AssaultWindow;
                if (HurtOnDuty.Severity(p, Find.TickManager.TicksGame - 4 * HurtOnDuty.AssaultWindow) != HurtOnDuty.Lasting) HodFail(check, "an injury as old as the incident (a hostage crisis that began earlier) was not counted");
            });
        }

        // ---- one case, one letter ------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard put down by an inmate through the game's own downing is one case with one letter that names the guard and the attacker; a second hook within the day joins it (still one case, one letter, one id); a
        /// different attacker is a new case, and the first letter's row for them is moot (withdrawn); the same attacker the next day is a new case. The leave runs from the moment the case opens.
        /// Fails without the hook (no case), without the join (a second case and letter) or without the withdrawal (two open letters).
        /// </summary>
        private static void CheckHodOneCaseOneLetter(Map map)
        {
            const string check = "one case, one letter";
            Guarded("hurt on duty: a downed guard is one case with one letter", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                int id0 = g.story.nextInjuryCaseId;
                Pawn guard = S31Guard(map, r), inmate = Throwaway(map, r), other = Throwaway(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                S31Wound(guard, HediffDefOf.Cut, 12f);
                if (!HodDown(guard, inmate))
                {
                    HodFail(check, "premise: the guard was not downed by the game's own downing");
                    return;
                }
                InjuryCase c = sc.injuries.FirstOrDefault();
                if (sc.injuries.Count != 1 || c == null || c.level != HurtOnDuty.Serious || c.attackerId != inmate.thingIDNumber || c.attackerName != inmate.LabelShort || sc.injuryCaseId != c.id || c.id != id0)
                {
                    HodFail(check, $"the downing did not open one serious case naming the attacker: {sc.injuries.Count} cases, {string.Join(" | ", sc.injuries.Select(x => x.Describe()))}, open {sc.injuryCaseId}, inmate {inmate.thingIDNumber}:{inmate.LabelShort}");
                    return;
                }
                List<ChoiceLetter_PrisonDecision> letters = HodLetters(r);
                if (letters.Count != 1 || !letters[0].pawns.Contains(guard) || !letters[0].Text.Resolve().Contains(guard.LabelShort) || !letters[0].Text.Resolve().Contains(inmate.LabelShort) || letters[0].note != c.id.ToString())
                    HodFail(check, $"expected one letter naming {guard.LabelShort} and {inmate.LabelShort} for case {c.id}, found {letters.Count} ({string.Join(" | ", letters.Select(l => l.note + ":" + l.Text.Resolve().Replace('\n', ' ')))})");
                // The default answer runs from the start.
                if (!StaffLives.OnLeave(sc) || sc.leaveReason != HurtOnDuty.ReasonInjury || sc.leaveUntilTick != c.tick + 3 * HodDay)
                    HodFail(check, $"before any answer the leave should run for 3 days from the case: on leave {StaffLives.OnLeave(sc)}, reason {sc.leaveReason}, until {sc.leaveUntilTick} (case {c.tick})");
                // A second hook the same day for the same officer and attacker joins.
                StaffStories.Notify_StaffDowned(guard, inmate);
                if (sc.injuries.Count != 1 || HodLetters(r).Count != 1 || g.story.nextInjuryCaseId != id0 + 1)
                    HodFail(check, $"a second hook within the day for the same attacker made another case or letter ({sc.injuries.Count} cases, {HodLetters(r).Count} letters, counter {g.story.nextInjuryCaseId} from {id0})");
                // A different attacker is a new case; the first letter's row for them is moot.
                StaffStories.Notify_StaffDowned(guard, other);
                List<ChoiceLetter_PrisonDecision> after = HodLetters(r).Where(l => !l.answered).ToList();
                if (sc.injuries.Count != 2 || sc.injuryCaseId != sc.injuries[1].id || sc.injuries[1].attackerId != other.thingIDNumber || after.Count != 1 || after[0].note != sc.injuries[1].id.ToString() || sc.injuries[0].answer == HurtOnDuty.AnswerNone)
                    HodFail(check, $"a different attacker did not open a second case with the first letter withdrawn ({sc.injuries.Count} cases, open {sc.injuryCaseId}, {after.Count} open letters {string.Join(",", after.Select(l => l.note))}, first answer {sc.injuries[0].answer})");
                // The same attacker the next day, after the first case: a new case again.
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + HodDay + HodHour);
                StaffStories.Notify_StaffDowned(guard, other);
                if (sc.injuries.Count != 3) HodFail(check, $"the same attacker more than a day later was not a new case ({sc.injuries.Count} cases)");
            });
        }

        /// <summary>
        /// A minor case (downed, nothing that bleeds, no wound of 10 HP, nothing lasting) sends no letter: one paid day off and a line in the chronicle, answered from the start. Fails if it asks the warden anything or its day is
        /// not one day.
        /// </summary>
        private static void CheckHodMinorHasNoLetter(Map map)
        {
            const string check = "minor";
            Guarded("hurt on duty: a minor injury is a paid day off and no letter", map, r =>
            {
                HodBegin(r);
                Pawn guard = S31Guard(map, r), inmate = Throwaway(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                S31Wound(guard, HodBruise, 3f);
                int funds0 = PrisonGame.Instance.ledger.funds;
                HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                InjuryCase c = sc.injuries.FirstOrDefault();
                if (c == null || c.level != HurtOnDuty.Minor || c.answer != HurtOnDuty.AnswerLeave || c.lettered)
                    HodFail(check, $"a bruise did not make a minor case answered with the leave and no letter ({c?.Describe() ?? "no case"})");
                if (HodLetters(r).Count != 0) HodFail(check, "a minor case sent a letter");
                if (!StaffLives.OnLeave(sc) || sc.leaveReason != HurtOnDuty.ReasonInjury || sc.leaveUntilTick != Find.TickManager.TicksGame + HodDay)
                    HodFail(check, $"a minor case should be one paid day: on leave {StaffLives.OnLeave(sc)}, reason {sc.leaveReason}, until {sc.leaveUntilTick} (now {Find.TickManager.TicksGame})");
                string line = "RP_Chr_HodMinor".Translate(guard.LabelShort, 1).Resolve();
                if (!r.NewEntries.Any(e => e.text == line && e.ids.Contains(guard.thingIDNumber))) HodFail(check, $"no chronicle line \"{line}\"");
                if (PrisonGame.Instance.ledger.funds != funds0) HodFail(check, "a minor case cost money");
            });
        }

        /// <summary>
        /// Who is a case: not the warden, not the state's chief, not a callout officer, not one whose dismissal is pending; an ordinary guard is (the control). Fails if any of those gets a case or a letter.
        /// </summary>
        private static void CheckHodWhoIsACase(Map map)
        {
            const string check = "who is a case";
            Guarded("hurt on duty: who is a case", map, r =>
            {
                HodBegin(r);
                Pawn inmate = Throwaway(map, r);
                Pawn Fresh(string what, System.Action<CompStaff> set)
                {
                    Pawn p = S31Guard(map, r);
                    S31Wound(p, HediffDefOf.Cut, 12f);
                    set(p.GetComp<CompStaff>());
                    if (HurtOnDuty.Open(p, inmate, HurtOnDuty.CauseAssault) != null || p.GetComp<CompStaff>().injuries.Count != 0) HodFail(check, $"{what} got a case");
                    return p;
                }
                StaffRoleDef wardenRole = DefDatabase<StaffRoleDef>.GetNamed("RP_Warden");
                Fresh("the warden", sc => sc.role = wardenRole);
                Fresh("the state's own chief", sc => sc.statePosted = true);
                Fresh("a callout officer", sc => sc.leaveAtTick = Find.TickManager.TicksGame + HodDay);
                Fresh("an officer let go and waiting to leave", sc => sc.pendingFire = 1);
                if (HodLetters(r).Count != 0) HodFail(check, "one of them got a letter");
                Pawn control = S31Guard(map, r);
                S31Wound(control, HediffDefOf.Cut, 12f);
                if (HurtOnDuty.Open(control, inmate, HurtOnDuty.CauseAssault) == null || HodLetters(r).Count != 1) HodFail(check, "premise: an ordinary guard in the same state got no case and one letter");
            });
        }

        // ---- the leave ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The leave on full pay: the officer is off the roster (their timetable sends them home at every hour), paid (still on the payroll at home), gone the days of the severity, and back with the reason cleared, the case
        /// closed, healed for the time away (stability 31), the fear of the attacker armed for the week, and no rest memory (an injury is no holiday). The control: a burnout leave still ends with the rest memory. Fails without
        /// the leave, without the reason (the rest would be given), or if the reason stays after the return.
        /// </summary>
        private static void CheckHodLeave(Map map)
        {
            const string check = "leave";
            Guarded("hurt on duty: the injury leave on full pay", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn guard = S31Guard(map, r), inmate = Throwaway(map, r), control = S31Guard(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                StaffWariness.Notify_HurtBy(guard, sc, inmate);
                S31Wound(guard, HediffDefOf.Cut, 12f);
                InjuryCase c = HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                HodAnswer(HodLetters(r).Single(), "RP_Hod_Leave", check);
                if (c == null || c.answer != HurtOnDuty.AnswerLeave) HodFail(check, $"the leave answer was not recorded ({c?.Describe()})");
                int start = c?.tick ?? 0;
                if (!StaffLives.OnLeave(sc) || sc.leaveUntilTick != start + 3 * HodDay) HodFail(check, $"a serious injury should be 3 days' leave: until {sc.leaveUntilTick}, case at {start}");
                for (int hour = 0; hour < 24; hour += 6)
                    if (!StaffCommute.ShouldBeHome(guard, sc, hour)) HodFail(check, $"the officer on leave is on the roster at hour {hour}");
                if (StaffCareers.Available(sc)) HodFail(check, "an officer on leave counts as available");
                float before = S31Healable(guard);
                StaffCommute.Leave(guard);
                if (!StaffCommute.AllStaff(map).Contains(guard) || StaffCareers.Wage(sc) <= 0 || StaffCareers.BasePay(new List<Pawn> { guard }) != StaffCareers.Wage(sc)) HodFail(check, "the officer on leave at home is not on the payroll at full wage");
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 73 * HodHour);
                StaffCommute.Arrive(guard, sc, map, g);
                if (!guard.Spawned) HodFail(check, "premise: the guard did not come back through the gate");
                if (StaffLives.OnLeave(sc) || sc.leaveUntilTick != -1 || sc.leaveReason != HurtOnDuty.ReasonNone || sc.injuryCaseId != -1 || sc.injuries.Count != 1)
                    HodFail(check, $"after the leave the reason or the case was not cleared (on leave {StaffLives.OnLeave(sc)}, until {sc.leaveUntilTick}, reason {sc.leaveReason}, open case {sc.injuryCaseId}, {sc.injuries.Count} recorded)");
                if (HodHas(guard, RPStaffLifeDefOf.RP_Rested)) HodFail(check, "an injury leave gave the rest memory");
                if (S31Healable(guard) >= before) HodFail(check, $"the wound did not heal at home (before {before:F2}, after {S31Healable(guard):F2})");
                if (!StaffWariness.WaryOf(sc, inmate) || sc.waryUntilTick < Find.TickManager.TicksGame + (StaffWariness.ReturnDays - 1) * HodDay)
                    HodFail(check, $"the fear of the attacker was not armed again for the week on the return (until {sc.waryUntilTick}, now {Find.TickManager.TicksGame})");
                if (!r.NewEntries.Any(e => e.text == "RP_Chr_HodBack".Translate(guard.LabelShort).Resolve())) HodFail(check, "no chronicle line for the return");
                // The control: a burnout leave is a rest, and the injury's change to the arrival did not take that from it.
                CompStaff csc = control.GetComp<CompStaff>();
                HurtOnDuty.WriteLeave(csc, Find.TickManager.TicksGame + HodHour, HurtOnDuty.ReasonBurnout);
                csc.restPending = true;
                if (!S31Commute(map, control, 2, check)) return;
                if (!HodHas(control, RPStaffLifeDefOf.RP_Rested) || csc.leaveReason != HurtOnDuty.ReasonNone) HodFail(check, $"control: a burnout leave's return did not give the rest or clear its reason (rested {HodHas(control, RPStaffLifeDefOf.RP_Rested)}, reason {csc.leaveReason})");
            });
        }

        /// <summary>
        /// An officer kept on site (downed in a medical bed, or the last on site) whose injury leave runs out is back in the hourly pass of their own comp (a real <c>CompTickInterval</c>, as a course's in-house end is): the
        /// leave and its reason cleared once, the case closed. A leave still running is left alone. The same for a burnout leave granted over it: the rest is given in-house too (and an injury alone gives none). Fails without the
        /// pass or its hook in the comp (the reason stays and the case stays open), or with the rest given for the injury, or not given for the burnout.
        /// </summary>
        private static void CheckHodInHouseExpiry(Map map)
        {
            const string check = "in-house";
            Guarded("hurt on duty: a leave that ends on site", map, r =>
            {
                HodBegin(r);
                Pawn guard = S31Guard(map, r), inmate = Throwaway(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                StaffWariness.Notify_HurtBy(guard, sc, inmate);
                S31Wound(guard, HediffDefOf.Cut, 12f);
                InjuryCase c = HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                HodAnswer(HodLetters(r).Single(), "RP_Hod_Leave", check);
                HodHourlyTick(guard);
                if (sc.leaveReason != HurtOnDuty.ReasonInjury || sc.injuryCaseId != c.id) HodFail(check, "the hourly pass ended a leave that is still running");
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 3 * HodDay + 1);
                HodHourlyTick(guard);
                if (sc.leaveReason != HurtOnDuty.ReasonNone || sc.leaveUntilTick != -1 || sc.injuryCaseId != -1 || StaffLives.OnLeave(sc) || sc.restPending)
                    HodFail(check, $"an expired leave on site was not ended by the comp's hourly pass (reason {sc.leaveReason}, until {sc.leaveUntilTick}, case {sc.injuryCaseId})");
                if (HodHas(guard, RPStaffLifeDefOf.RP_Rested) || sc.waryUntilTick < Find.TickManager.TicksGame + (StaffWariness.ReturnDays - 1) * HodDay) HodFail(check, "the in-house return gave the rest or did not arm the fear again");
                HodHourlyTick(guard);
                if (r.NewEntries.Count(e => e.text == "RP_Chr_HodBack".Translate(guard.LabelShort).Resolve()) != 1) HodFail(check, "the return was written more than once");
                // A burnout leave granted over the injury leave is a rest, and an injury leave that ends in-house still gives it.
                Pawn both = S31Guard(map, r);
                CompStaff bsc = both.GetComp<CompStaff>();
                S31Wound(both, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(both, inmate, HurtOnDuty.CauseAssault);
                var burnout = ChoiceLetter_PrisonDecision.Make("RP_Decision_Burnout", "x", "x", map, both);
                burnout.pawns.Add(both);
                new BurnoutDecision().Choose(burnout, map, 0);
                int end = bsc.leaveUntilTick;
                Find.TickManager.DebugSetTicksGame(end + 1);
                HodHourlyTick(both);
                if (!HodHas(both, RPStaffLifeDefOf.RP_Rested) || bsc.restPending || bsc.leaveReason != HurtOnDuty.ReasonNone)
                    HodFail(check, $"a burnout leave over an injury leave that ended in-house did not give the rest or clear the reason (rested {HodHas(both, RPStaffLifeDefOf.RP_Rested)}, pending {bsc.restPending}, reason {bsc.leaveReason})");
            });
        }

        /// <summary>
        /// Leaves do not shorten each other, and each keeps what it is owed: a burnout leave over a running injury leave keeps the injury's end and reason and is remembered on the case beside it; an injury leave over a
        /// running burnout leave keeps the longer end, is the injury's, and is no rest; sending them back on the next shift puts the burnout leave that ran before the case back, and one granted after the case opened
        /// as well (its rest still owed, and given at the return); a burnout leave over an injury leave keeps its rest at the return (the injury alone gives none). A booked course is kept (done on the return, the post line
        /// says the injury). Fails if either write replaces the other, if the later leave is lost when they are sent back, or if the rest is lost.
        /// </summary>
        private static void CheckHodLeavesDoNotShortenEachOther(Map map)
        {
            const string check = "leaves";
            Guarded("hurt on duty: the leaves take the longer", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                // An injury, then a burnout leave.
                Pawn a = S31Guard(map, r), inmate = Throwaway(map, r);
                int now = Find.TickManager.TicksGame;
                CompStaff asc = a.GetComp<CompStaff>();
                HodPermanent(a, check);
                InjuryCase ia = HurtOnDuty.Open(a, inmate, HurtOnDuty.CauseAssault);
                int injuryEnd = asc.leaveUntilTick;
                var burnout = ChoiceLetter_PrisonDecision.Make("RP_Decision_Burnout", "x", "x", map, a);
                burnout.pawns.Add(a);
                Find.TickManager.DebugSetTicksGame(now + HodHour);
                new BurnoutDecision().Choose(burnout, map, 0);
                int burnoutEnd0 = Find.TickManager.TicksGame + StaffLives.LeaveDays * HodDay;
                if (asc.leaveUntilTick != injuryEnd || asc.leaveReason != HurtOnDuty.ReasonInjury) HodFail(check, $"a burnout leave over a 5-day injury leave changed it (until {asc.leaveUntilTick} from {injuryEnd}, reason {asc.leaveReason})");
                if (ia == null || ia.priorLeaveUntil != burnoutEnd0 || ia.priorLeaveReason != HurtOnDuty.ReasonBurnout) HodFail(check, $"the burnout leave granted over the injury leave was not kept beside it on the case (prior {ia?.priorLeaveUntil}:{ia?.priorLeaveReason}, expected {burnoutEnd0}:{HurtOnDuty.ReasonBurnout})");
                // A burnout leave, then a minor injury.
                Pawn b = S31Guard(map, r);
                CompStaff bsc = b.GetComp<CompStaff>();
                var burnoutB = ChoiceLetter_PrisonDecision.Make("RP_Decision_Burnout", "x", "x", map, b);
                burnoutB.pawns.Add(b);
                new BurnoutDecision().Choose(burnoutB, map, 0);
                int burnoutEnd = bsc.leaveUntilTick;
                S31Clean(b);
                S31Wound(b, HodBruise, 2f);
                InjuryCase cb = HurtOnDuty.Open(b, inmate, HurtOnDuty.CauseAssault);
                if (bsc.leaveUntilTick != burnoutEnd || bsc.leaveReason != HurtOnDuty.ReasonInjury || cb == null || cb.priorLeaveUntil != burnoutEnd || cb.priorLeaveReason != HurtOnDuty.ReasonBurnout)
                    HodFail(check, $"a one-day injury leave over a 3-day burnout leave shortened it or lost the leave it covered (until {bsc.leaveUntilTick} from {burnoutEnd}, reason {bsc.leaveReason}, prior {cb?.priorLeaveUntil}:{cb?.priorLeaveReason})");
                // Back on the next shift (a serious case this time) puts the burnout leave back. (Two hours on: a letter of its own.)
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn c = S31Guard(map, r);
                CompStaff csc = c.GetComp<CompStaff>();
                var burnoutC = ChoiceLetter_PrisonDecision.Make("RP_Decision_Burnout", "x", "x", map, c);
                burnoutC.pawns.Add(c);
                new BurnoutDecision().Choose(burnoutC, map, 0);
                int burnoutEndC = csc.leaveUntilTick;
                // (Two hours on, so that the injury leave runs past the burnout leave: sent back, there is something to end.)
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                S31Wound(c, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(c, inmate, HurtOnDuty.CauseAssault);
                HodAnswer(HodLetters(r).Last(l => l.pawns.Contains(c)), "RP_Hod_Back", check);
                if (csc.leaveUntilTick != burnoutEndC || csc.leaveReason != HurtOnDuty.ReasonBurnout) HodFail(check, $"back on the next shift did not put the burnout leave back (until {csc.leaveUntilTick} from {burnoutEndC}, reason {csc.leaveReason})");
                // ...and so does one granted AFTER the case opened (a lasting injury: 5 days, so that the burnout leave of 3 days an hour in sits inside it): the leave that is left is the burnout's, its rest still owed, and the return gives it.
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn e = S31Guard(map, r);
                CompStaff esc = e.GetComp<CompStaff>();
                HodPermanent(e, check);
                HurtOnDuty.Open(e, inmate, HurtOnDuty.CauseAssault);
                int injuryEndE = esc.leaveUntilTick;
                var burnoutE = ChoiceLetter_PrisonDecision.Make("RP_Decision_Burnout", "x", "x", map, e);
                burnoutE.pawns.Add(e);
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + HodHour);
                new BurnoutDecision().Choose(burnoutE, map, 0);
                int burnoutEndE = Find.TickManager.TicksGame + StaffLives.LeaveDays * HodDay;
                if (esc.leaveUntilTick != injuryEndE || esc.leaveReason != HurtOnDuty.ReasonInjury || injuryEndE <= burnoutEndE) HodFail(check, $"premise: a burnout leave granted an hour into a 5-day injury leave should sit inside it as the injury's (until {esc.leaveUntilTick}, expected {injuryEndE}, burnout would end {burnoutEndE}, reason {esc.leaveReason})");
                HodAnswer(HodLetters(r).Last(l => l.pawns.Contains(e)), "RP_Hod_Back", check);
                if (esc.leaveUntilTick != burnoutEndE || esc.leaveReason != HurtOnDuty.ReasonBurnout || !esc.restPending)
                    HodFail(check, $"back on the next shift lost the burnout leave granted after the case opened (until {esc.leaveUntilTick}, expected {burnoutEndE}, reason {esc.leaveReason}, rest pending {esc.restPending})");
                if (!S31Commute(map, e, StaffLives.LeaveDays * 24 + 1, check)) return;
                if (!HodHas(e, RPStaffLifeDefOf.RP_Rested) || esc.leaveReason != HurtOnDuty.ReasonNone || esc.injuryCaseId != -1) HodFail(check, $"the burnout leave kept after being sent back did not give its rest or close the case at the return (rested {HodHas(e, RPStaffLifeDefOf.RP_Rested)}, reason {esc.leaveReason}, case {esc.injuryCaseId})");
                // A burnout leave granted an hour into a 3-day injury leave runs past it: the injury adds nothing to the leave, so sending them back ends nothing - not offered, and applied anyway (a letter answered before) it
                // is the leave and no penalty: no rushed-back memory, no week of the danger, no chronicle line, the leave as it was.
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn k = S31Guard(map, r);
                CompStaff ksc = k.GetComp<CompStaff>();
                S31Wound(k, HediffDefOf.Cut, 12f);
                InjuryCase kc = HurtOnDuty.Open(k, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision kLetter = HodLetters(r).Last(l => l.pawns.Contains(k));
                int kBack = kLetter.decision.Worker.Options(kLetter).ToList().IndexOf("RP_Hod_Back");
                if (kBack < 0 || kLetter.decision.Worker.Disabled(kLetter, kBack) != null) HodFail(check, "control: sent back is not offered while the injury leave alone runs");
                var burnoutK = ChoiceLetter_PrisonDecision.Make("RP_Decision_Burnout", "x", "x", map, k);
                burnoutK.pawns.Add(k);
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + HodHour);
                new BurnoutDecision().Choose(burnoutK, map, 0);
                int burnoutEndK = Find.TickManager.TicksGame + StaffLives.LeaveDays * HodDay;
                if (ksc.leaveUntilTick != burnoutEndK || ksc.leaveReason != HurtOnDuty.ReasonInjury || kc.priorLeaveUntil != burnoutEndK) HodFail(check, $"premise: a burnout leave an hour into a 3-day injury leave should run past it as the injury's, kept on the case (until {ksc.leaveUntilTick}, expected {burnoutEndK}, prior {kc.priorLeaveUntil})");
                if (kBack >= 0 && kLetter.decision.Worker.Disabled(kLetter, kBack) == null) HodFail(check, "sent back is offered when another leave runs past the injury's (it would end nothing)");
                HurtOnDuty.Apply(k, ksc, kc, "RP_Hod_Back", map);
                if (kc.answer != HurtOnDuty.AnswerLeave || HodHas(k, RPHurtDefOf.RP_RushedBack) || ksc.rushedBackUntilTick >= 0 || ksc.leaveUntilTick != burnoutEndK || r.NewEntries.Any(en => en.text == "RP_Chr_HodRushedBack".Translate(k.LabelShort).Resolve()))
                    HodFail(check, $"sent back over a leave that runs past the injury's was punished or changed the leave (answer {kc.answer}, rushed-back memory {HodHas(k, RPHurtDefOf.RP_RushedBack)}, until {ksc.leaveUntilTick} from {burnoutEndK}, danger until {ksc.rushedBackUntilTick})");
                // A burnout leave granted over an injury leave that is not sent back: the injury's end stands, the rest is still owed at the return.
                Pawn f = S31Guard(map, r);
                CompStaff fsc = f.GetComp<CompStaff>();
                S31Wound(f, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(f, inmate, HurtOnDuty.CauseAssault);
                var burnoutF = ChoiceLetter_PrisonDecision.Make("RP_Decision_Burnout", "x", "x", map, f);
                burnoutF.pawns.Add(f);
                new BurnoutDecision().Choose(burnoutF, map, 0);
                if (!S31Commute(map, f, Mathf.Max(3, StaffLives.LeaveDays) * 24 + 1, check)) return;
                if (!HodHas(f, RPStaffLifeDefOf.RP_Rested) || fsc.leaveReason != HurtOnDuty.ReasonNone) HodFail(check, $"a burnout leave over an injury leave lost its rest at the return (rested {HodHas(f, RPStaffLifeDefOf.RP_Rested)}, reason {fsc.leaveReason})");
                // (A course booked for a guard with a skill below the cap, never trained, with the money: or the check says so.)
                bool BookCourse(Pawn who, CompStaff wsc)
                {
                    wsc.lastTrainedTick = -1;
                    if (StaffTraining.SkillFor(wsc.role) is { } skill && who.skills?.GetSkill(skill) is { TotallyDisabled: false } rec) rec.Level = 4;
                    g.ledger.funds = 10000;
                    if (StaffTraining.Send(who, wsc)) return true;
                    HodFail(check, $"premise: a course could not be booked for the guard ({StaffTraining.CannotGo(who, wsc) ?? "no reason given"})");
                    return false;
                }
                // A booked course is kept: done on the return, with no rest; and the post line says the injury, not the course.
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn d = S31Guard(map, r);
                CompStaff dsc = d.GetComp<CompStaff>();
                g.ledger.funds = 10000;
                if (!BookCourse(d, dsc)) return;
                S31Wound(d, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(d, inmate, HurtOnDuty.CauseAssault);
                if (!dsc.trainingPending || dsc.leaveReason != HurtOnDuty.ReasonInjury) HodFail(check, $"an injury over a booked course dropped the course or kept its reason (course {dsc.trainingPending}, reason {dsc.leaveReason})");
                string post = ITab_StaffRecord.PostLine(d, dsc);
                // (The line is the keyed text as it is, uncoloured; Resolve() would colour "3.0 days" as a date, so both are read without tags.)
                if (HodPlain(post).Contains(HodPlain("RP_StaffWhereCourse".Translate().Resolve())) || !HodPlain(post).Contains(HodPlain("RP_StaffWhereInjuryLeave".Translate(((dsc.leaveUntilTick - Find.TickManager.TicksGame) / (float)HodDay).ToString("0.0")).Resolve())))
                    HodFail(check, $"the post line of an officer on an injury leave over a booked course says the course and not the injury leave ({post})");
                if (!S31Commute(map, d, 73, check)) return;
                if (dsc.trainingPending || dsc.lastTrainedTick < 0 || !HodHas(d, RPTrainingDefOf.RP_Trained) || HodHas(d, RPStaffLifeDefOf.RP_Rested))
                    HodFail(check, $"the course booked in an injury leave was not done on the return, or the injury gave a rest (pending {dsc.trainingPending}, trained {dsc.lastTrainedTick}, rested {HodHas(d, RPStaffLifeDefOf.RP_Rested)})");
                // Sent back on the next shift over a booked course whose own leave is over: done now, and no rest owed.
                Pawn h = S31Guard(map, r);
                CompStaff hsc = h.GetComp<CompStaff>();
                if (!BookCourse(h, hsc)) return;
                int courseEnd = hsc.leaveUntilTick;
                Find.TickManager.DebugSetTicksGame(courseEnd - HodHour);
                S31Wound(h, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(h, inmate, HurtOnDuty.CauseAssault);
                Find.TickManager.DebugSetTicksGame(courseEnd + HodHour);
                HodAnswer(HodLetters(r).Last(l => l.pawns.Contains(h)), "RP_Hod_Back", check);
                if (hsc.trainingPending || hsc.lastTrainedTick < 0 || hsc.leaveReason != HurtOnDuty.ReasonNone || hsc.restPending) HodFail(check, $"sent back after the course's own leave was over, the course was not done or a leave was left (pending {hsc.trainingPending}, reason {hsc.leaveReason}, rest {hsc.restPending})");
                // A course, an injury and a burnout leave over one another, the officer kept on site through the end (the real hourly pass of their comp): the course's in-house end comes first and must not drop the burnout's
                // rest (it was given nowhere, and stayed pending for a later leave) or leave the injury's reason: course done, rest given once, the reason cleared, the case closed.
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn m = S31Guard(map, r);
                CompStaff msc = m.GetComp<CompStaff>();
                if (!BookCourse(m, msc)) return;
                S31Wound(m, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(m, inmate, HurtOnDuty.CauseAssault);
                var burnoutM = ChoiceLetter_PrisonDecision.Make("RP_Decision_Burnout", "x", "x", map, m);
                burnoutM.pawns.Add(m);
                new BurnoutDecision().Choose(burnoutM, map, 0);
                HodAnswer(HodLetters(r).Last(l => l.pawns.Contains(m)), "RP_Hod_Leave", check);
                if (!msc.trainingPending || !msc.restPending || msc.leaveReason != HurtOnDuty.ReasonInjury || msc.injuryCaseId < 0) HodFail(check, $"premise: a course, an injury and a burnout leave over one another (course {msc.trainingPending}, rest pending {msc.restPending}, reason {msc.leaveReason}, case {msc.injuryCaseId})");
                Find.TickManager.DebugSetTicksGame(msc.leaveUntilTick + 1);
                HodHourlyTick(m);
                if (msc.trainingPending || msc.lastTrainedTick < 0 || !HodHas(m, RPStaffLifeDefOf.RP_Rested) || HodMemories(m, RPStaffLifeDefOf.RP_Rested) != 1 || msc.restPending || msc.leaveReason != HurtOnDuty.ReasonNone || msc.injuryCaseId != -1)
                    HodFail(check, $"a leave over a course and an injury that ended in-house did not give the burnout's rest once and end the injury (course pending {msc.trainingPending}, rested {HodMemories(m, RPStaffLifeDefOf.RP_Rested)}, rest pending {msc.restPending}, reason {msc.leaveReason}, case {msc.injuryCaseId})");
            });
        }

        // ---- compensation ------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Compensation: the money booked once ($400 for a serious case, from the wages account), colleagues on site and at home remember it (once for each officer), the leave goes on; answered again, or a second hook within
        /// the day, pays nothing more. With too little money the answer is the leave: nothing booked, no memory. Fails with a second payout, none, or a payout the prison cannot afford.
        /// </summary>
        private static void CheckHodCompensation(Map map)
        {
            const string check = "compensation";
            Guarded("hurt on duty: compensation is paid once", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn guard = S31Guard(map, r), colleague = S31Guard(map, r), atHome = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true), inmate = Throwaway(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                S31Wound(guard, HediffDefOf.Cut, 12f);
                int funds0 = g.ledger.funds;
                InjuryCase c = HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision letter = HodLetters(r).Single();
                HodAnswer(letter, "RP_Hod_Compensation", check);
                if (g.ledger.funds != funds0 - 400 || c.paid != 400 || c.answer != HurtOnDuty.AnswerCompensation) HodFail(check, $"a serious case should be paid $400 once (funds {funds0} -> {g.ledger.funds}, paid {c.paid}, answer {c.answer})");
                if (r.NewLedgerEntries.Count(e => e.amount == -400 && e.category == RPDefOf.RP_Wages) != 1) HodFail(check, "the ledger has not one -400 entry in the wages");
                if (!StaffLives.OnLeave(sc) || sc.leaveReason != HurtOnDuty.ReasonInjury) HodFail(check, "the compensation answer ended the leave");
                foreach (Pawn o in new[] { colleague, atHome })
                {
                    HodAnHourOfMemories(o);
                    if (HodMemories(o, RPHurtDefOf.RP_LookedAfter) != 1) HodFail(check, $"{o.LabelShort} ({(o == atHome ? "at home" : "on site")}) does not have the looked-after memory (once, and still there an hour of the game's memory passes later: {HodMemories(o, RPHurtDefOf.RP_LookedAfter)})");
                }
                if (HodHas(guard, RPHurtDefOf.RP_LookedAfter)) HodFail(check, "the officer's own memory is a colleague's");
                // Answered again (a replayed letter), and a second hook within the day: nothing more.
                PrisonDecisions.Choose(letter, 1);
                StaffStories.Notify_StaffDowned(guard, inmate);
                if (g.ledger.funds != funds0 - 400 || HodMemories(colleague, RPHurtDefOf.RP_LookedAfter) != 1) HodFail(check, $"a replay or a second hook paid again (funds {g.ledger.funds}, expected {funds0 - 400})");
                // Too little money: the leave, nothing booked.
                int lookedAfter0 = HodMemories(colleague, RPHurtDefOf.RP_LookedAfter);
                Pawn poor = S31Guard(map, r);
                S31Wound(poor, HediffDefOf.Cut, 12f);
                InjuryCase pc = HurtOnDuty.Open(poor, inmate, HurtOnDuty.CauseAssault);
                g.ledger.funds = 100;
                ChoiceLetter_PrisonDecision l2 = HodLetters(r).Last(l => l.pawns.Contains(poor));
                int comp = l2.decision.Worker.Options(l2).ToList().IndexOf("RP_Hod_Compensation");
                if (l2.decision.Worker.Disabled(l2, comp) == null) HodFail(check, "compensation is offered when the prison cannot pay it");
                l2.answered = true;
                PrisonDecisions.Choose(l2, comp);
                if (g.ledger.funds != 100 || pc.paid != 0 || pc.answer != HurtOnDuty.AnswerLeave || HodMemories(colleague, RPHurtDefOf.RP_LookedAfter) != lookedAfter0)
                    HodFail(check, $"with $100 the compensation was not turned into the leave (funds {g.ledger.funds}, paid {pc.paid}, answer {pc.answer})");
            });
        }

        /// <summary>
        /// A standing order for compensation answers at once and the letter never reaches the desk; a second hook the same day pays nothing more (one payout). Fails if the letter reaches the stack, or the payout comes twice.
        /// </summary>
        private static void CheckHodStandingOrder(Map map)
        {
            const string check = "standing order";
            Guarded("hurt on duty: a standing order pays once", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn guard = S31Guard(map, r), inmate = Throwaway(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                if (!StandingOrders.Keeps(PrisonDecisionDef.Named(HurtOnDuty.DecisionDef), "RP_Hod_Compensation")) HodFail(check, "the decision does not allow a standing order for compensation");
                StandingOrders.Set(g, PrisonDecisionDef.Named(HurtOnDuty.DecisionDef), "RP_Hod_Compensation");
                S31Wound(guard, HediffDefOf.Cut, 12f);
                int funds0 = g.ledger.funds;
                StaffStories.Notify_StaffDowned(guard, inmate);
                InjuryCase c = sc.injuries.FirstOrDefault();
                if (c == null || c.answer != HurtOnDuty.AnswerCompensation || g.ledger.funds != funds0 - 400 || Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is(HurtOnDuty.DecisionDef) && !l.answered))
                    HodFail(check, $"the standing order did not answer at once (case {c?.Describe() ?? "none"}, funds {funds0} -> {g.ledger.funds}, an open letter on the stack: {Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is(HurtOnDuty.DecisionDef) && !l.answered)})");
                StaffStories.Notify_StaffDowned(guard, inmate);
                if (sc.injuries.Count != 1 || g.ledger.funds != funds0 - 400) HodFail(check, $"a second hook after the order paid again or made another case (funds {g.ledger.funds}, {sc.injuries.Count} cases)");
            });
        }

        /// <summary>
        /// The letter is not urgent: past the day's budget it is held for the next day, and the officer is already on leave (the default starts with the case). The clock then runs for real: a day on, the leave runs and
        /// the letter is still held; four days on (past what a letter may wait, and past the leave) the midnight pass lapses it into the leave - nothing is paid, no leave is added, the officer is back through the in-house
        /// hour and the case closes. Fails if the letter is urgent (never held), if the leave waits for the letter, or if the lapse pays, answers anything but the leave or lengthens it.
        /// </summary>
        private static void CheckHodHeldLetterLapses(Map map)
        {
            const string check = "held letter";
            Guarded("hurt on duty: a held letter lapses into the leave", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn guard = S31Guard(map, r), inmate = Throwaway(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                S31Wound(guard, HediffDefOf.Cut, 12f);
                StoryLetters.TestBudget = true;
                g.story.decisionsToday = 99;
                int funds0 = g.ledger.funds;
                InjuryCase c = HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision held = g.story.deferredLetters.FirstOrDefault(l => l.Is(HurtOnDuty.DecisionDef) && l.pawns.Contains(guard));
                if (held == null || Find.LetterStack.LettersListForReading.Contains(held))
                {
                    HodFail(check, $"past the day's budget the letter was not held for the next day (held {held != null}); it is {(held == null ? "perhaps urgent" : "on the stack")}");
                    return;
                }
                if (StoryLetters.Urgent(held)) HodFail(check, "the letter counts as urgent");
                if (!StaffLives.OnLeave(sc) || sc.leaveReason != HurtOnDuty.ReasonInjury) HodFail(check, "the officer of a held letter is not already on leave");
                int leaveEnd = sc.leaveUntilTick;
                // A day on: the midnight pass finds the day's budget used, the letter waits, the leave runs.
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + HodDay);
                g.story.decisionsToday = 99;
                StoryLetters.Daily(g, map);
                if (!g.story.deferredLetters.Contains(held) || c.answer != HurtOnDuty.AnswerNone || !StaffLives.OnLeave(sc)) HodFail(check, $"a day on the letter should still be held with the leave running (held {g.story.deferredLetters.Contains(held)}, answer {c.answer}, on leave {StaffLives.OnLeave(sc)})");
                // Four days on: the letter has waited too long, and the leave is over.
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 3 * HodDay);
                g.story.decisionsToday = 99;
                StoryLetters.Daily(g, map);
                if (g.story.deferredLetters.Contains(held) || c.answer != HurtOnDuty.AnswerLeave || g.ledger.funds != funds0 || sc.leaveUntilTick != leaveEnd)
                    HodFail(check, $"the held letter did not lapse into the leave (still held {g.story.deferredLetters.Contains(held)}, answer {c.answer}, funds {funds0} -> {g.ledger.funds}, leave until {sc.leaveUntilTick} from {leaveEnd})");
                HodHourlyTick(guard);
                if (sc.injuryCaseId != -1 || sc.leaveReason != HurtOnDuty.ReasonNone) HodFail(check, $"after the lapse and the leave the officer is not back and the case not closed (case {sc.injuryCaseId}, reason {sc.leaveReason})");
            });
        }

        /// <summary>
        /// Four guards put down in one hour share one letter (a row for each officer, their names in its text, one label for the four) and use one place in the day's budget (the budget is on: a letter each would be four
        /// places, and the fourth would wait for the next day); one answer pays each of them once; one put down two hours later has a letter of their own (a second place). Fails with a letter or a place each.
        /// </summary>
        private static void CheckHodFourGuardsOneLetter(Map map)
        {
            const string check = "four guards";
            Guarded("hurt on duty: four guards in an hour, one letter", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn inmate = Throwaway(map, r);
                StoryLetters.TestBudget = true;
                g.story.decisionsToday = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount) - 2;
                int places0 = g.story.decisionsToday;
                var guards = new List<Pawn>();
                for (int i = 0; i < 4; i++)
                {
                    Pawn p = S31Guard(map, r);
                    S31Wound(p, HediffDefOf.Cut, 12f);
                    guards.Add(p);
                    HurtOnDuty.Open(p, inmate, HurtOnDuty.CauseAssault);
                }
                List<ChoiceLetter_PrisonDecision> letters = HodLetters(r);
                if (letters.Count != 1 || guards.Any(p => !letters[0].pawns.Contains(p) || !letters[0].Text.Resolve().Contains(p.LabelShort)) || letters[0].note.Split(',').Length != 4)
                    HodFail(check, $"four guards hurt in one hour should share one letter naming them all (found {letters.Count} letters, {letters.FirstOrDefault()?.pawns.Count} officers, note {letters.FirstOrDefault()?.note})");
                else
                {
                    if (!letters[0].Label.Resolve().Contains("4")) HodFail(check, $"the shared letter's label does not say how many ({letters[0].Label})");
                    if (g.story.decisionsToday != places0 + 1 || g.story.deferredLetters.Contains(letters[0])) HodFail(check, $"the four guards' letter should take one place of the day's budget and not be held (places {places0} -> {g.story.decisionsToday}, held {g.story.deferredLetters.Contains(letters[0])})");
                    int funds0 = g.ledger.funds;
                    HodAnswer(letters[0], "RP_Hod_Compensation", check);
                    if (g.ledger.funds != funds0 - 4 * 400 || guards.Any(p => p.GetComp<CompStaff>().injuries[0].answer != HurtOnDuty.AnswerCompensation))
                        HodFail(check, $"one answer should pay each of the four $400 once (funds {funds0} -> {g.ledger.funds})");
                }
                // Two hours later: a letter of their own, a second place (and the day has room for exactly it).
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn late = S31Guard(map, r);
                S31Wound(late, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(late, inmate, HurtOnDuty.CauseAssault);
                if (HodLetters(r).Count(l => !l.answered) != 1 || !HodLetters(r).Last().pawns.SequenceEqual(new[] { late }) || g.story.decisionsToday != places0 + 2) HodFail(check, $"an officer hurt two hours after the others did not get a letter of their own with a place of its own (places {g.story.decisionsToday}, expected {places0 + 2})");
            });
        }

        // ---- worse, and the money for it ---------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A second hook that finds the case worse raises its level (still one case) and lengthens the leave to the new level's (never shortening it); an answered case is not paid again for it; a minor case that turns
        /// serious gets its letter then, and it can be answered; a letter still open - on the stack, or held for the next day - is written again with the new level, days and money, and the pension note once it is lasting
        /// (and had none before). Fails if a case is made twice, the leave stays short, a payout repeats, the letter never comes or keeps quoting the old price.
        /// </summary>
        private static void CheckHodUpgrade(Map map)
        {
            const string check = "upgrade";
            Guarded("hurt on duty: a case found worse", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn inmate = Throwaway(map, r);
                // Serious, answered with compensation, then found lasting.
                Pawn a = S31Guard(map, r);
                CompStaff asc = a.GetComp<CompStaff>();
                S31Wound(a, HediffDefOf.Cut, 12f);
                InjuryCase ca = HurtOnDuty.Open(a, inmate, HurtOnDuty.CauseAssault);
                HodAnswer(HodLetters(r).Single(), "RP_Hod_Compensation", check);
                int funds0 = g.ledger.funds;
                HodPermanent(a, check);
                HurtOnDuty.Open(a, inmate, HurtOnDuty.CauseAssault);
                if (asc.injuries.Count != 1 || ca.level != HurtOnDuty.Lasting || asc.leaveUntilTick != ca.tick + 5 * HodDay || g.ledger.funds != funds0 || HodLetters(r).Count(l => !l.answered) != 0)
                    HodFail(check, $"a case found lasting: {asc.injuries.Count} cases, level {ca.level}, leave until {asc.leaveUntilTick} (case {ca.tick} + 5 days), funds {funds0} -> {g.ledger.funds}, {HodLetters(r).Count(l => !l.answered)} open letters (none expected: it was answered)");
                // Minor, then serious: the letter comes now.
                Pawn b = S31Guard(map, r);
                CompStaff bsc = b.GetComp<CompStaff>();
                S31Wound(b, HodBruise, 3f);
                InjuryCase cb = HurtOnDuty.Open(b, inmate, HurtOnDuty.CauseAssault);
                if (HodLetters(r).Any(l => l.pawns.Contains(b))) HodFail(check, "premise: the minor case had a letter");
                S31Wound(b, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(b, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision lb = HodLetters(r).FirstOrDefault(l => l.pawns.Contains(b));
                if (bsc.injuries.Count != 1 || cb.level != HurtOnDuty.Serious || lb == null || !cb.lettered || cb.answer != HurtOnDuty.AnswerNone || bsc.injuryCaseId != cb.id || bsc.leaveUntilTick != cb.tick + 3 * HodDay)
                    HodFail(check, $"a minor case that turned serious did not get its letter and 3 days ({bsc.injuries.Count} cases, level {cb.level}, letter {lb != null}, answer {cb.answer}, leave until {bsc.leaveUntilTick} from {cb.tick})");
                else
                {
                    int before = g.ledger.funds;
                    HodAnswer(lb, "RP_Hod_Compensation", check);
                    if (g.ledger.funds != before - 400 || cb.answer != HurtOnDuty.AnswerCompensation) HodFail(check, $"the late letter could not be answered with compensation (funds {before} -> {g.ledger.funds}, answer {cb.answer})");
                }
                // Serious with its letter open, then found lasting: the letter says lasting, five days and $800, offers the pension and says what it is for - and before that it said none of it.
                bool Says(ChoiceLetter_PrisonDecision l, bool lasting)
                {
                    // (Read without the colour tags: the row's numbers and money come out coloured, and "3 days" is coloured whole as a date, once in the row and again in the letter.)
                    string text = HodPlain(l.Text.Resolve());
                    (string level, string other, int days, int money, int otherMoney) = lasting ? (HodPlain("RP_HodLevel_3".Translate().Resolve()), HodPlain("RP_HodLevel_2".Translate().Resolve()), 5, 800, 400) : (HodPlain("RP_HodLevel_2".Translate().Resolve()), HodPlain("RP_HodLevel_3".Translate().Resolve()), 3, 400, 800);
                    return text.Contains(level) && !text.Contains(other) && text.Contains($" {days} days") && text.Contains(HodPlain(Money.Format(money))) && !text.Contains(HodPlain(Money.Format(otherMoney))) && text.Contains(HodPlain("RP_HodNotePension".Translate().Resolve())) == lasting;
                }
                Pawn c = S31Guard(map, r);
                S31Wound(c, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(c, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision lc = HodLetters(r).Last(l => l.pawns.Contains(c));
                if (!Says(lc, lasting: false) || lc.decision.Worker.Options(lc).Count != 3) HodFail(check, $"premise: a serious case's letter says serious, 3 days, $400 and no pension ({lc.Text.Resolve().Replace('\n', ' ')})");
                HodPermanent(c, check);
                HurtOnDuty.Open(c, inmate, HurtOnDuty.CauseAssault);
                if (!Says(lc, lasting: true) || !lc.decision.Worker.Options(lc).Contains("RP_Hod_Pension"))
                    HodFail(check, $"the case was found lasting but its open letter still quotes the old price or offers no pension ({lc.Text.Resolve().Replace('\n', ' ')}; offers {string.Join(", ", lc.decision.Worker.Options(lc))})");
                // A case that is not the open one found lasting (the same attacker again, after another's serious case): it takes the open place and sends its own letter with the lasting options; the other case, still
                // not answered, is over as far as the letter goes (the leave runs for it) and its letter is withdrawn - before, the lasting case got no letter and no pension was ever offered for it.
                // (Two hours on, so that the officer's letters stand alone: one written in the same hour is shared, and the shared letter is the next case below.)
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn n = S31Guard(map, r), inmateY = Throwaway(map, r);
                CompStaff nsc = n.GetComp<CompStaff>();
                S31Wound(n, HodBruise, 3f);
                InjuryCase na = HurtOnDuty.Open(n, inmate, HurtOnDuty.CauseAssault);
                S31Wound(n, HediffDefOf.Cut, 12f);
                InjuryCase nb = HurtOnDuty.Open(n, inmateY, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision lnb = HodLetters(r).LastOrDefault(l => l.pawns.Contains(n));
                if (na == null || nb == null || na == nb || nb.answer != HurtOnDuty.AnswerNone || nsc.injuryCaseId != nb.id || lnb == null || na.lettered) HodFail(check, "premise: an officer with a minor case of one attacker's and a serious open one of another's");
                else
                {
                    HodPermanent(n, check);
                    HurtOnDuty.Open(n, inmate, HurtOnDuty.CauseAssault);
                    ChoiceLetter_PrisonDecision lna = HodLetters(r).LastOrDefault(l => !l.answered && l.pawns.Contains(n));
                    var rows = lna == null ? new List<(Pawn pawn, CompStaff sc, InjuryCase c)>() : HurtOnDuty.LiveRows(lna);
                    if (na.level != HurtOnDuty.Lasting || !na.lettered || nsc.injuryCaseId != na.id || lna == null || rows.Count != 1 || rows[0].c != na || !lna.decision.Worker.Options(lna).Contains("RP_Hod_Pension"))
                        HodFail(check, $"a case that was not the open one found lasting got no letter of its own with the pension (level {na.level}, lettered {na.lettered}, open case {nsc.injuryCaseId} (expected {na.id}), letter {lna != null}, rows {rows.Count})");
                    if (nb.answer != HurtOnDuty.AnswerLeave || !lnb.answered) HodFail(check, $"the case another attacker's letter was about was not closed for the letter when the lasting one took its place (answer {nb.answer}, letter answered {lnb.answered})");
                }
                // The same on a letter shared with another officer hurt in the same hour: the row of the officer is one, and it is the lasting case's now (the other case's is moot); the letter lives on for the others.
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn o = S31Guard(map, r), m = S31Guard(map, r);
                CompStaff msc = m.GetComp<CompStaff>();
                S31Wound(o, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(o, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision lo = HodLetters(r).LastOrDefault(l => !l.answered && l.pawns.Contains(o));
                S31Wound(m, HodBruise, 3f);
                InjuryCase ma = HurtOnDuty.Open(m, inmate, HurtOnDuty.CauseAssault);
                S31Wound(m, HediffDefOf.Cut, 12f);
                InjuryCase mb = HurtOnDuty.Open(m, inmateY, HurtOnDuty.CauseAssault);
                if (lo == null || ma == null || mb == null || ma == mb || !lo.pawns.Contains(m) || mb.answer != HurtOnDuty.AnswerNone || msc.injuryCaseId != mb.id || ma.lettered) HodFail(check, "premise: an officer with a minor case and a serious open one of another attacker's, on a letter shared with a colleague");
                else
                {
                    HodPermanent(m, check);
                    HurtOnDuty.Open(m, inmate, HurtOnDuty.CauseAssault);
                    var shared = HurtOnDuty.LiveRows(lo);
                    if (ma.level != HurtOnDuty.Lasting || !ma.lettered || msc.injuryCaseId != ma.id || lo.answered || lo.pawns.Count(x => x == m) != 1 || shared.Count != 2 || !shared.Any(x => x.pawn == m && x.c == ma) || !shared.Any(x => x.pawn == o) || !lo.decision.Worker.Options(lo).Contains("RP_Hod_Pension"))
                        HodFail(check, $"on a shared letter the lasting case did not take its officer's row with the pension (level {ma.level}, lettered {ma.lettered}, open case {msc.injuryCaseId} (expected {ma.id}), letter answered {lo.answered}, rows {shared.Count})");
                    if (mb.answer != HurtOnDuty.AnswerLeave) HodFail(check, $"the case the shared letter's row was about was not closed for the letter when the lasting one took its place (answer {mb.answer})");
                }
                // The same for a letter held for the next day (the day's budget is used up): it is on the story, not the stack, and is written again there.
                StoryLetters.TestBudget = true;
                g.story.decisionsToday = 99;
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn d = S31Guard(map, r);
                S31Wound(d, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(d, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision ld = g.story.deferredLetters.FirstOrDefault(l => l.Is(HurtOnDuty.DecisionDef) && l.pawns.Contains(d));
                if (ld == null) HodFail(check, "premise: the letter was not held for the next day");
                else
                {
                    HodPermanent(d, check);
                    HurtOnDuty.Open(d, inmate, HurtOnDuty.CauseAssault);
                    if (!Says(ld, lasting: true)) HodFail(check, $"a held letter was not written again when the case was found lasting ({ld.Text.Resolve().Replace('\n', ' ')})");
                }
            });
        }

        // ---- back on the next shift ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Back on the next shift: no cost, the leave ends (the case closes), the officer has the rushed-back memory and colleagues on site and at home the colleague's one (of this officer); the fear of the attacker is armed for
        /// the week unless they had faced them down; a resignation while it lasts is put down to the danger, ahead of pay (a guard in the same job for 25 days) and of the fear (the control: without it, pay, then the fear).
        /// Fails if the leave goes on, the memories are missing, or the reason stays what it was.
        /// </summary>
        private static void CheckHodBackOnShift(Map map)
        {
            const string check = "back on the next shift";
            Guarded("hurt on duty: sent back on the next shift", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn guard = S31Guard(map, r), colleague = S31Guard(map, r), atHome = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard, atHome: true), inmate = Throwaway(map, r), faced = S31Guard(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                int now = Find.TickManager.TicksGame;
                (int hired0, int since0) = (sc.hiredTick, sc.roleSinceTick);
                r.Add(() => (sc.hiredTick, sc.roleSinceTick) = (hired0, since0));
                StaffWariness.Notify_HurtBy(guard, sc, inmate);
                S31Wound(guard, HediffDefOf.Cut, 12f);
                InjuryCase c = HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                int funds0 = g.ledger.funds;
                HodAnswer(HodLetters(r).Single(), "RP_Hod_Back", check);
                if (g.ledger.funds != funds0 || c.answer != HurtOnDuty.AnswerBack || StaffLives.OnLeave(sc) || sc.leaveReason != HurtOnDuty.ReasonNone || sc.injuryCaseId != -1)
                    HodFail(check, $"back on the next shift should cost nothing and end the leave and the case (funds {funds0} -> {g.ledger.funds}, answer {c.answer}, on leave {StaffLives.OnLeave(sc)}, reason {sc.leaveReason}, case {sc.injuryCaseId})");
                if (!HodHas(guard, RPHurtDefOf.RP_RushedBack) || sc.rushedBackUntilTick != now + HurtOnDuty.RushedBackDays * HodDay) HodFail(check, "the officer has no rushed-back memory, or it is not for its days");
                foreach (Pawn o in new[] { colleague, atHome })
                {
                    HodAnHourOfMemories(o);
                    if (HodMemories(o, RPHurtDefOf.RP_ColleagueRushedBack) != 1) HodFail(check, $"{o.LabelShort} ({(o == atHome ? "at home" : "on site")}) does not have the memory of the colleague who was sent back (once, and still there an hour of the game's memory passes later: {HodMemories(o, RPHurtDefOf.RP_ColleagueRushedBack)})");
                }
                if (HodHas(guard, RPHurtDefOf.RP_ColleagueRushedBack) || HodHas(guard, RPHurtDefOf.RP_LookedAfter)) HodFail(check, "the officer has a colleague's memory of themselves");
                if (!StaffWariness.WaryOf(sc, inmate) || sc.waryUntilTick < now + (StaffWariness.ReturnDays - 1) * HodDay) HodFail(check, $"the fear was not armed for the week (until {sc.waryUntilTick}, now {now})");
                // The resignation reason, with the guard stuck in the job for 25 days (pay) and then not (the fear).
                sc.hiredTick = now - 30 * HodDay;
                sc.roleSinceTick = -1;
                if (StaffCareers.ReasonFor(guard, sc) != ResignReason.Danger) HodFail(check, $"a resignation while rushed back is {StaffCareers.ReasonFor(guard, sc)}, not the danger (ahead of pay)");
                sc.rushedBackUntilTick = -1;
                if (StaffCareers.ReasonFor(guard, sc) != ResignReason.Pay) HodFail(check, $"control: without it a guard 30 days in the job should quit over pay ({StaffCareers.ReasonFor(guard, sc)})");
                sc.hiredTick = now - HodDay;
                if (StaffCareers.ReasonFor(guard, sc) != ResignReason.HatedInmate) HodFail(check, $"control: a new hire with the fear on should quit over it ({StaffCareers.ReasonFor(guard, sc)})");
                sc.rushedBackUntilTick = now + HodDay;
                if (StaffCareers.ReasonFor(guard, sc) != ResignReason.Danger) HodFail(check, "a resignation while rushed back is not the danger ahead of the fear");
                // One who had faced the attacker down is not made afraid again by being sent back.
                CompStaff fsc = faced.GetComp<CompStaff>();
                StaffWariness.Notify_HurtBy(faced, fsc, inmate);
                S31Wound(faced, HediffDefOf.Cut, 12f);
                HurtOnDuty.Open(faced, inmate, HurtOnDuty.CauseAssault);
                StaffWariness.Notify_Subdued(faced, inmate);
                HodAnswer(HodLetters(r).Last(l => l.pawns.Contains(faced)), "RP_Hod_Back", check);
                if (fsc.waryOfId != -1 || StaffWariness.WaryOf(fsc, inmate)) HodFail(check, "an officer who had faced the attacker down was made afraid of them again by being sent back");
            });
        }

        /// <summary>
        /// The fear on return: after the leave it runs a week more only while the attacker is still in the prison and the officer has not faced them down since (either clears it). Fails if the fear has run out by the return (the
        /// 3 days it had), or comes back for one who left or was faced down - or for an attacker who is simply not in the prison any more though nothing cleared the fear (it is armed only when the attacker is found).
        /// </summary>
        private static void CheckHodWarinessOnReturn(Map map)
        {
            const string check = "wariness";
            Guarded("hurt on duty: the fear armed again on the return", map, r =>
            {
                HodBegin(r);
                Pawn inmate = Throwaway(map, r);
                Pawn Leave(string what, System.Action<Pawn, CompStaff> between, Pawn attacker = null)
                {
                    attacker ??= inmate;
                    Pawn guard = S31Guard(map, r);
                    CompStaff sc = guard.GetComp<CompStaff>();
                    StaffWariness.Notify_HurtBy(guard, sc, attacker);
                    S31Wound(guard, HediffDefOf.Cut, 12f);
                    HurtOnDuty.Open(guard, attacker, HurtOnDuty.CauseAssault);
                    HodAnswer(HodLetters(r).Last(l => l.pawns.Contains(guard)), "RP_Hod_Leave", check);
                    StaffCommute.Leave(guard);
                    between(guard, sc);
                    Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 4 * HodDay);
                    StaffCommute.Arrive(guard, sc, map, PrisonGame.Instance);
                    if (!guard.Spawned) HodFail(check, $"premise ({what}): the guard did not come back");
                    return guard;
                }
                Pawn plain = Leave("plain", (p, sc) => { });
                CompStaff psc = plain.GetComp<CompStaff>();
                if (!StaffWariness.WaryOf(psc, inmate) || psc.waryUntilTick < Find.TickManager.TicksGame + (StaffWariness.ReturnDays - 1) * HodDay || !HodHas(plain, RPWaryDefOf.RP_Shaken, inmate))
                    HodFail(check, $"after a 4-day leave the fear should run a week more with its memory (wary {StaffWariness.WaryOf(psc, inmate)}, until {psc.waryUntilTick}, now {Find.TickManager.TicksGame}, shaken {HodHas(plain, RPWaryDefOf.RP_Shaken, inmate)})");
                Pawn faced = Leave("faced down", (p, sc) => StaffWariness.Notify_Subdued(p, inmate));
                if (faced.GetComp<CompStaff>().waryOfId != -1) HodFail(check, "the fear came back for an officer who had faced the attacker down");
                Pawn gone = Leave("attacker left", (p, sc) => StaffWariness.Notify_InmateLeaving(inmate));
                if (gone.GetComp<CompStaff>().waryOfId != -1) HodFail(check, "the fear came back for an attacker who has left the prison");
                // An attacker who is no longer in the prison with nothing having cleared the fear (it still names them): not armed (the week is not given).
                Pawn vanished = Throwaway(map, r);
                Pawn missing = Leave("attacker not found", (p, sc) => vanished.Destroy(DestroyMode.Vanish), vanished);
                CompStaff msc = missing.GetComp<CompStaff>();
                if (msc.waryUntilTick >= Find.TickManager.TicksGame + (StaffWariness.ReturnDays - 1) * HodDay)
                    HodFail(check, $"the fear was armed again for an attacker who is not in the prison (until {msc.waryUntilTick}, now {Find.TickManager.TicksGame})");
            });
        }

        // ---- lasting: a pension, a desk ----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A lasting injury offers a pension, and a desk post only to an officer with Intellectual 6 and the administrator's work (a control with Intellectual 3 is offered none, and the letter's text says what each is for - the pension
        /// only on a letter that offers it); a case that is not lasting is offered neither, and a blank letter (what a standing order asks) offers neither. The pension: $1,200 once, the officer is out through the retirement path
        /// (no resignation letter or count, the retirement counted), colleagues have the send-off, the case closes; a pension for a letter with a serious row gives that officer compensation instead. A downed officer cannot be
        /// pensioned yet (the letter says so, and an answer applied anyway gives the leave and no money). The desk: the administrator's role, the leave goes on, no money; on a letter shared with an officer who cannot do the
        /// work, that one has the leave. Fails if a premise is not built (the guard cannot do the work, the downing does not take), or on any of those.
        /// </summary>
        private static void CheckHodLastingPensionAndDesk(Map map)
        {
            const string check = "lasting";
            Guarded("hurt on duty: a lasting injury's pension and desk post", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn inmate = Throwaway(map, r);
                StaffRoleDef admin = RPParoleDefOf.RP_Administrator;
                // (The administrator's role may be behind a project in this run: done for the check.)
                if (Unlocks.GateOf(admin) is { } gate && g.bureaucracy != null && !g.bureaucracy.done.Contains(gate))
                {
                    g.bureaucracy.done.Add(gate);
                    r.Add(() => g.bureaucracy.done.Remove(gate));
                }
                if (!Unlocks.IsUnlocked(admin))
                {
                    HodFail(check, "premise: the administrator's role is not unlocked");
                    return;
                }
                var blank = new ChoiceLetter_PrisonDecision { decision = PrisonDecisionDef.Named(HurtOnDuty.DecisionDef) };
                if (blank.decision.Worker.Options(blank).Count != 3) HodFail(check, "a blank letter does not offer the three plain answers a standing order may be for");
                // (Two hours between the officers, each with a letter of their own, unless they are to share one. The skill is set; a guard who is to do the work is one of the made-up guards whose background does not forbid
                // the administrator's work, the next made up until there is one.)
                Pawn Hurt(int skill, bool lasting, bool share = false, bool forDesk = false)
                {
                    if (!share) Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                    Pawn p = S31Guard(map, r);
                    for (int tries = 0; forDesk && tries < 40 && !(p.skills?.GetSkill(SkillDefOf.Intellectual) is { TotallyDisabled: false } && !admin.workTypes.Any(w => p.WorkTypeIsDisabled(w))); tries++) p = S31Guard(map, r);
                    if (p.skills?.GetSkill(SkillDefOf.Intellectual) is { TotallyDisabled: false } intellectual) intellectual.Level = skill;
                    if (lasting) HodPermanent(p, check);
                    else S31Wound(p, HediffDefOf.Cut, 12f);
                    HurtOnDuty.Open(p, inmate, HurtOnDuty.CauseAssault);
                    return p;
                }
                ChoiceLetter_PrisonDecision LetterOf(Pawn p) => HodLetters(r).Last(l => l.pawns.Contains(p));
                IReadOnlyList<string> OffersOf(Pawn p) => LetterOf(p).decision.Worker.Options(LetterOf(p));
                // A serious injury: neither.
                Pawn serious = Hurt(8, lasting: false);
                if (OffersOf(serious).Count != 3) HodFail(check, $"a serious case is offered a pension or a desk ({string.Join(", ", OffersOf(serious))})");
                if (LetterOf(serious).Text.Resolve().Contains("RP_HodNotePension".Translate().Resolve())) HodFail(check, "a letter that offers no pension explains the pension");
                // No skill: a pension, no desk.
                Pawn weak = Hurt(3, lasting: true);
                if (!OffersOf(weak).Contains("RP_Hod_Pension") || OffersOf(weak).Contains("RP_Hod_Desk")) HodFail(check, $"an officer with Intellectual 3 should be offered the pension and no desk (offered {string.Join(", ", OffersOf(weak))})");
                if (!LetterOf(weak).Text.Resolve().Contains("RP_HodNotePension".Translate().Resolve()) || LetterOf(weak).Text.Resolve().Contains("RP_HodNoteDesk".Translate(HurtOnDuty.DeskSkill).Resolve())) HodFail(check, "a letter that offers the pension should say what it is for, and not explain a desk post it does not offer");
                // Skilled and able: both. The guard is built for it (Intellectual 8, the administrator's work not forbidden): if that does not hold, the check says so.
                Pawn able = Hurt(8, lasting: true, forDesk: true);
                CompStaff ac = able.GetComp<CompStaff>();
                if (!HurtOnDuty.DeskOk(able, ac))
                    HodFail(check, $"premise: the guard made for the desk post cannot have it (Intellectual {able.skills?.GetSkill(SkillDefOf.Intellectual)?.Level}, disabled {able.skills?.GetSkill(SkillDefOf.Intellectual)?.TotallyDisabled}, work disabled {admin.workTypes.Any(w => able.WorkTypeIsDisabled(w))})");
                else
                {
                    if (!OffersOf(able).Contains("RP_Hod_Desk")) HodFail(check, $"an officer with Intellectual 8 who can do the work is not offered the desk post ({string.Join(", ", OffersOf(able))})");
                    if (!LetterOf(able).Text.Resolve().Contains("RP_HodNoteDesk".Translate(HurtOnDuty.DeskSkill).Resolve())) HodFail(check, "a letter that offers the desk post does not say who it is for");
                    int funds1 = g.ledger.funds;
                    HodAnswer(LetterOf(able), "RP_Hod_Desk", check);
                    if (ac.role != admin || ac.injuries[0].answer != HurtOnDuty.AnswerDesk || g.ledger.funds != funds1 || !StaffLives.OnLeave(ac))
                        HodFail(check, $"the desk post: role {ac.role?.defName}, answer {ac.injuries[0].answer}, funds {funds1} -> {g.ledger.funds}, still on leave {StaffLives.OnLeave(ac)}");
                    // On a letter shared with an officer who cannot do the work: that one has the leave.
                    Pawn unable = Hurt(3, lasting: true, share: true), able2 = Hurt(8, lasting: true, share: true, forDesk: true);
                    if (LetterOf(unable) != LetterOf(able2)) HodFail(check, "premise: two officers hurt in one hour did not share a letter");
                    else if (HurtOnDuty.DeskOk(able2, able2.GetComp<CompStaff>()))
                    {
                        HodAnswer(LetterOf(able2), "RP_Hod_Desk", check);
                        if (able2.GetComp<CompStaff>().role != admin || unable.GetComp<CompStaff>().role == admin || unable.GetComp<CompStaff>().injuries[0].answer != HurtOnDuty.AnswerLeave)
                            HodFail(check, $"on a shared letter the desk post went to the one who can do the work and the other had the leave: roles {able2.GetComp<CompStaff>().role?.defName}/{unable.GetComp<CompStaff>().role?.defName}, the other's answer {unable.GetComp<CompStaff>().injuries[0].answer}");
                    }
                    else HodFail(check, "premise: the second guard made for the desk post cannot have it");
                }
                // A downed officer cannot be pensioned yet: the letter says so, and an answer applied anyway (a letter answered before they fell) gives the leave and no money.
                Pawn down = Hurt(3, lasting: true);
                CompStaff dsc = down.GetComp<CompStaff>();
                if (!HodDown(down, inmate)) HodFail(check, "premise: the officer was not downed");
                else
                {
                    int pension = OffersOf(down).ToList().IndexOf("RP_Hod_Pension");
                    if (LetterOf(down).decision.Worker.Disabled(LetterOf(down), pension) == null) HodFail(check, "a downed officer can be pensioned");
                    int fundsDown = g.ledger.funds;
                    HurtOnDuty.Apply(down, dsc, dsc.injuries.Last(), "RP_Hod_Pension", map);
                    if (g.ledger.funds != fundsDown || dsc.role == null || dsc.injuries.Last().answer != HurtOnDuty.AnswerLeave) HodFail(check, $"a pension applied to a downed officer was paid or retired them (funds {fundsDown} -> {g.ledger.funds}, role {dsc.role?.defName}, answer {dsc.injuries.Last().answer})");
                }
                // The pension, for an officer with a lasting injury and a serious row on the same letter.
                Pawn colleague = S31Guard(map, r);
                Pawn retiring = Hurt(3, lasting: true);
                Pawn second = Hurt(3, lasting: false, share: true);
                ChoiceLetter_PrisonDecision both = LetterOf(retiring);
                if (LetterOf(second) != both) HodFail(check, "premise: the two officers hurt in one hour did not share a letter");
                int resignations0 = g.stats.resignations, retired0 = g.stats.staffRetired, funds0 = g.ledger.funds;
                CompStaff rsc = retiring.GetComp<CompStaff>(), ssc = second.GetComp<CompStaff>();
                HodAnswer(both, "RP_Hod_Pension", check);
                if (rsc.role != null || rsc.injuryCaseId != -1 || rsc.injuries[0].answer != HurtOnDuty.AnswerPension || g.stats.staffRetired != retired0 + 1 || g.stats.resignations != resignations0)
                    HodFail(check, $"the pension did not retire the officer through the retirement path (role {rsc.role?.defName}, case {rsc.injuryCaseId}, answer {rsc.injuries[0].answer}, retired {retired0} -> {g.stats.staffRetired}, resignations {resignations0} -> {g.stats.resignations})");
                if (ssc.injuries[0].answer != HurtOnDuty.AnswerCompensation) HodFail(check, $"a serious row on a letter answered with a pension got {ssc.injuries[0].answer}, not compensation");
                if (g.ledger.funds != funds0 - 1200 - 400) HodFail(check, $"a pension and a compensation should cost 1600, the books say {funds0 - g.ledger.funds}");
                if (r.NewLetters.Any(l => l.Label.Resolve() == "RP_ResignLabel".Translate(retiring.LabelShort).Resolve())) HodFail(check, "a pension sent the resignation letter");
                if (HodMemories(colleague, RPHurtDefOf.RP_PensionSendOff) != 1) HodFail(check, "colleagues have no send-off memory for the pension");
                string line = "RP_Chr_HodPensioned".Translate(retiring.LabelShort, "guard", Money.Format(1200)).Resolve();
                if (!r.NewEntries.Any(e => e.text == line)) HodFail(check, $"no chronicle line \"{line}\"");
                // Pensioned at home on the leave (where the injury leave is spent): the officer is discarded on the spot, and the send-off must outlive them (the game drops a memory that names a discarded pawn).
                Pawn pensionedHome = Hurt(3, lasting: true);
                ChoiceLetter_PrisonDecision homeLetter = LetterOf(pensionedHome);
                StaffCommute.Leave(pensionedHome);
                HodAnswer(homeLetter, "RP_Hod_Pension", check);
                if (pensionedHome.GetComp<CompStaff>().role != null || HodMemories(colleague, RPHurtDefOf.RP_PensionSendOff) != 2) HodFail(check, $"an officer pensioned at home on leave (role {pensionedHome.GetComp<CompStaff>().role?.defName}): the colleagues' send-off memories are {HodMemories(colleague, RPHurtDefOf.RP_PensionSendOff)}, expected 2");
            });
        }

        // ---- the case closes ---------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The officer let go (on site or at home on leave), resigned, or dead closes the case and makes the letter's row moot (a letter with no row left is withdrawn; an answer of it afterwards pays nothing). A dismissal within a
        /// fortnight of a case costs the colleagues a memory (a control without a case, one a month old, and a resignation get none). Fails if the letter stays live or pays, or the memory is missing or given to the wrong one.
        /// </summary>
        private static void CheckHodOfficerLeaves(Map map)
        {
            const string check = "officer leaves";
            Guarded("hurt on duty: the officer let go, resigned or dead", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn inmate = Throwaway(map, r), colleague = S31Guard(map, r);
                Pawn Hurt(bool atHome = false)
                {
                    Pawn p = S31Guard(map, r);
                    S31Wound(p, HediffDefOf.Cut, 12f);
                    HurtOnDuty.Open(p, inmate, HurtOnDuty.CauseAssault);
                    if (atHome) StaffCommute.Leave(p);
                    return p;
                }
                // Dismissed on site.
                Pawn a = Hurt();
                CompStaff asc = a.GetComp<CompStaff>();
                ChoiceLetter_PrisonDecision la = HodLetters(r).Last(l => l.pawns.Contains(a));
                StaffUtility.Fire(a, byWarden: true);
                if (asc.injuryCaseId != -1 || !la.answered || Find.LetterStack.LettersListForReading.Contains(la)) HodFail(check, $"an officer let go: case {asc.injuryCaseId}, letter answered {la.answered}, still on the stack {Find.LetterStack.LettersListForReading.Contains(la)}");
                int funds0 = g.ledger.funds;
                PrisonDecisions.Choose(la, 1);
                if (g.ledger.funds != funds0) HodFail(check, "a letter answered after the officer was let go paid");
                if (HodMemories(colleague, RPHurtDefOf.RP_FiredWhileHurt) != 1) HodFail(check, "colleagues do not hold it against the prison that a hurt officer was let go");
                // Dismissed at home, on leave.
                Pawn b = Hurt(atHome: true);
                CompStaff bsc = b.GetComp<CompStaff>();
                ChoiceLetter_PrisonDecision lb = HodLetters(r).Last(l => l.pawns.Contains(b));
                StaffUtility.Fire(b, byWarden: true);
                if (bsc.injuryCaseId != -1 || !lb.answered || bsc.role != null) HodFail(check, $"an officer let go at home: case {bsc.injuryCaseId}, letter answered {lb.answered}, role {bsc.role?.defName}");
                if (HodMemories(colleague, RPHurtDefOf.RP_FiredWhileHurt) != 2) HodFail(check, "an officer let go at home on leave did not count with the colleagues");
                // Resigned (no record): the case closes, nobody holds it against the prison.
                Pawn c = Hurt();
                ChoiceLetter_PrisonDecision lc = HodLetters(r).Last(l => l.pawns.Contains(c));
                StaffUtility.Fire(c, record: false);
                if (c.GetComp<CompStaff>().injuryCaseId != -1 || !lc.answered || HodMemories(colleague, RPHurtDefOf.RP_FiredWhileHurt) != 2) HodFail(check, "a resignation did not close the case quietly");
                // One whose case is a month old is let go with no memory (and the control: no case at all).
                Pawn d = Hurt();
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 30 * HodDay);
                StaffUtility.Fire(d, byWarden: true);
                Pawn plain = S31Guard(map, r);
                StaffUtility.Fire(plain, byWarden: true);
                if (HodMemories(colleague, RPHurtDefOf.RP_FiredWhileHurt) != 2) HodFail(check, "a dismissal a month after a case, or of an officer with no case, cost the colleagues a memory");
                // A corruption dismissal of an officer with a recent case is not the warden's own: nobody holds it against the prison. (Nor is welfare's move: it is a Fire with no record, as a resignation is.)
                Pawn corrupt = Hurt();
                CorruptionSystem.Dismiss(corrupt, corrupt.GetComp<CompStaff>(), arrest: false);
                if (corrupt.GetComp<CompStaff>().role != null || HodMemories(colleague, RPHurtDefOf.RP_FiredWhileHurt) != 2) HodFail(check, $"premise or effect: a corruption dismissal of a hurt officer (let go {corrupt.GetComp<CompStaff>().role == null}) cost the colleagues a memory ({HodMemories(colleague, RPHurtDefOf.RP_FiredWhileHurt)} of 2)");
                // Dead.
                Pawn e = Hurt();
                CompStaff esc = e.GetComp<CompStaff>();
                ChoiceLetter_PrisonDecision le = HodLetters(r).Last(l => l.pawns.Contains(e));
                StaffDeaths.Notify_Died(e, esc, new DamageInfo(DamageDefOf.Blunt, 5f), null);
                if (esc.injuryCaseId != -1 || !le.answered) HodFail(check, $"an officer who died: case {esc.injuryCaseId}, letter answered {le.answered}");
            });
        }

        // ---- the hostage path ---------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard freed from a hostage crisis hurt, or downed in it, opens one case (the same attacker, the same day: the case the downing opened is the one); the leader is the attacker the case names; the injuries that count are
        /// as old as the crisis, not as the hour. A hostage freed unhurt has no case. Fails without the release's hook (no case) or with a second case for the downed hostage.
        /// </summary>
        private static void CheckHodHostage(Map map)
        {
            HostageCheck("hurt on duty", map, s =>
            {
                const string check = "hostage";
                HodBegin(s.r);
                // Downed in the crisis, then freed hurt: one case.
                StoryArc arc = s.Begin(out Pawn guard, out Pawn boss);
                if (arc == null) return;
                CompStaff sc = guard.GetComp<CompStaff>();
                S31Clean(guard);
                S31Wound(guard, HediffDefOf.Cut, 12f);
                StaffStories.Notify_StaffDowned(guard, boss);
                s.Worker.Finish(arc, map, HostageHow.GaveUp, HostageFate.Hurt, "", boss);
                if (sc.injuries.Count != 1 || sc.injuries[0].attackerId != boss.thingIDNumber || HodLetters(s.r).Count(l => l.pawns.Contains(guard)) != 1)
                    HodFail(check, $"a hostage downed in the crisis and freed hurt: {sc.injuries.Count} cases, {HodLetters(s.r).Count(l => l.pawns.Contains(guard))} letters ({string.Join(" | ", sc.injuries.Select(x => x.Describe()))})");
                // Freed hurt, never downed: a case of the hostage-taker's, with the injuries of the crisis (older than the hour).
                StoryArc arc2 = s.Begin(out Pawn guard2, out Pawn boss2);
                if (arc2 == null) return;
                CompStaff sc2 = guard2.GetComp<CompStaff>();
                S31Clean(guard2);
                Hediff_Injury old = HodPermanent(guard2, check);
                old.ageTicks = 3 * HurtOnDuty.AssaultWindow;
                arc2.startTick = Find.TickManager.TicksGame - 4 * HurtOnDuty.AssaultWindow;
                s.Worker.Finish(arc2, map, HostageHow.GaveUp, HostageFate.Hurt, "", boss2);
                InjuryCase c2 = sc2.injuries.FirstOrDefault();
                if (sc2.injuries.Count != 1 || c2.attackerId != boss2.thingIDNumber || c2.level != HurtOnDuty.Lasting || HodLetters(s.r).Count(l => l.pawns.Contains(guard2)) != 1)
                    HodFail(check, $"a hostage freed hurt: {sc2.injuries.Count} cases, {c2?.Describe() ?? "none"} (the crisis began {4 * HurtOnDuty.AssaultWindow} ticks ago and the injury is {3 * HurtOnDuty.AssaultWindow} old: lasting expected)");
                // Freed unhurt: nothing.
                StoryArc arc3 = s.Begin(out Pawn guard3, out Pawn boss3);
                if (arc3 == null) return;
                S31Clean(guard3);
                s.Worker.Finish(arc3, map, HostageHow.GaveUp, HostageFate.Freed, "", boss3);
                if (guard3.GetComp<CompStaff>().injuries.Count != 0) HodFail(check, "a hostage freed unhurt has a case");
            });
        }

        // ---- answers that no longer fit --------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// "Back on the next shift" is for an officer whose injury leave is still running: once it is over (a letter held for a day, or answered late) it is disabled on the letter, and applied anyway (a letter answered before)
        /// it is the leave and nothing else - no rushed-back memory, none for the colleagues, no week of the danger, no chronicle line. The control: with the leave running it is enabled. Fails if the officer is punished for
        /// a leave they have had.
        /// </summary>
        private static void CheckHodBackAfterTheLeaveIsOver(Map map)
        {
            const string check = "back after the leave";
            Guarded("hurt on duty: sent back after the leave is over", map, r =>
            {
                HodBegin(r);
                Pawn guard = S31Guard(map, r), colleague = S31Guard(map, r), inmate = Throwaway(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                StaffWariness.Notify_HurtBy(guard, sc, inmate);
                S31Wound(guard, HediffDefOf.Cut, 12f);
                InjuryCase c = HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision letter = HodLetters(r).Single();
                int back = letter.decision.Worker.Options(letter).ToList().IndexOf("RP_Hod_Back");
                if (letter.decision.Worker.Disabled(letter, back) != null) HodFail(check, $"control: sent back is disabled while the leave runs ({letter.decision.Worker.Disabled(letter, back)})");
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 3 * HodDay + HodHour);
                if (StaffLives.OnLeave(sc) || sc.leaveReason != HurtOnDuty.ReasonInjury) HodFail(check, "premise: the leave should be over and the reason still the injury's (they have not come through the gate)");
                if (letter.decision.Worker.Disabled(letter, back) == null) HodFail(check, "sent back is offered to an officer whose injury leave is over");
                int rushedUntil = sc.rushedBackUntilTick;
                HurtOnDuty.Apply(guard, sc, c, "RP_Hod_Back", map);
                if (c.answer != HurtOnDuty.AnswerLeave || HodHas(guard, RPHurtDefOf.RP_RushedBack) || HodHas(colleague, RPHurtDefOf.RP_ColleagueRushedBack) || sc.rushedBackUntilTick != rushedUntil || sc.rushedBackUntilTick > Find.TickManager.TicksGame)
                    HodFail(check, $"an officer sent back after their leave was over was punished for it (answer {c.answer}, rushed-back memory {HodHas(guard, RPHurtDefOf.RP_RushedBack)}, colleagues' {HodHas(colleague, RPHurtDefOf.RP_ColleagueRushedBack)}, until {sc.rushedBackUntilTick})");
                if (r.NewEntries.Any(e => e.text == "RP_Chr_HodRushedBack".Translate(guard.LabelShort).Resolve())) HodFail(check, "the chronicle says an officer was sent back hurt who had their whole leave");
                // The injury's three days are over but a burnout leave granted an hour into them still runs (the case's letter answered just after day 3): sent back ends nothing, so it is not offered and is no penalty.
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                Pawn over = S31Guard(map, r);
                CompStaff osc = over.GetComp<CompStaff>();
                S31Wound(over, HediffDefOf.Cut, 12f);
                InjuryCase oc = HurtOnDuty.Open(over, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision overLetter = HodLetters(r).Last(l => l.pawns.Contains(over));
                int overBack = overLetter.decision.Worker.Options(overLetter).ToList().IndexOf("RP_Hod_Back");
                var burnoutO = ChoiceLetter_PrisonDecision.Make("RP_Decision_Burnout", "x", "x", map, over);
                burnoutO.pawns.Add(over);
                Find.TickManager.DebugSetTicksGame(oc.tick + HodHour);
                new BurnoutDecision().Choose(burnoutO, map, 0);
                int burnoutEndO = osc.leaveUntilTick;
                Find.TickManager.DebugSetTicksGame(oc.tick + 3 * HodDay + HodHour / 2);
                if (!StaffLives.OnLeave(osc) || osc.leaveReason != HurtOnDuty.ReasonInjury || burnoutEndO <= Find.TickManager.TicksGame) HodFail(check, $"premise: the injury's days are over and the burnout leave granted in them still runs (on leave {StaffLives.OnLeave(osc)}, reason {osc.leaveReason}, burnout ends {burnoutEndO}, now {Find.TickManager.TicksGame})");
                if (overBack < 0 || overLetter.decision.Worker.Disabled(overLetter, overBack) == null) HodFail(check, "sent back is offered after the injury's days are over, while a burnout leave runs on");
                HurtOnDuty.Apply(over, osc, oc, "RP_Hod_Back", map);
                if (oc.answer != HurtOnDuty.AnswerLeave || HodHas(over, RPHurtDefOf.RP_RushedBack) || osc.rushedBackUntilTick >= 0 || osc.leaveUntilTick != burnoutEndO)
                    HodFail(check, $"an officer sent back after the injury's days, with a burnout leave still running, was punished or had the leave changed (answer {oc.answer}, rushed-back memory {HodHas(over, RPHurtDefOf.RP_RushedBack)}, danger until {osc.rushedBackUntilTick}, leave until {osc.leaveUntilTick} from {burnoutEndO})");
            });
        }

        /// <summary>
        /// What is checked again when an answer is applied: an officer whose dismissal is queued (carried, held - the Fire that cannot go through yet) is no case any more: their open letter is withdrawn, an answer applied to
        /// them (a standing order, a replay) pays nothing and answers nothing; one who became state-posted or a callout since the letter was written likewise. Fails if the money is paid or the colleagues remember it.
        /// </summary>
        private static void CheckHodQueuedDismissalIsMoot(Map map)
        {
            const string check = "queued dismissal";
            Guarded("hurt on duty: a dismissal that is queued makes the case moot", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn guard = S31Guard(map, r), colleague = S31Guard(map, r), inmate = Throwaway(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                S31Wound(guard, HediffDefOf.Cut, 12f);
                InjuryCase c = HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                ChoiceLetter_PrisonDecision letter = HodLetters(r).Single();
                guard.DeSpawn();
                StaffUtility.Fire(guard, byWarden: true);
                if (sc.pendingFire == 0 || sc.role == null) HodFail(check, $"premise: the dismissal of a carried officer should be queued (pending {sc.pendingFire}, role {sc.role?.defName})");
                if (!letter.answered || Find.LetterStack.LettersListForReading.Contains(letter)) HodFail(check, "the open letter of an officer whose dismissal is queued was not withdrawn");
                int funds0 = g.ledger.funds;
                HurtOnDuty.Apply(guard, sc, c, "RP_Hod_Compensation", map);
                PrisonDecisions.Choose(letter, 1);
                if (g.ledger.funds != funds0 || c.answer != HurtOnDuty.AnswerNone || HodHas(colleague, RPHurtDefOf.RP_LookedAfter)) HodFail(check, $"an answer to an officer whose dismissal is queued paid or was recorded (funds {funds0} -> {g.ledger.funds}, answer {c.answer}, colleagues' memory {HodHas(colleague, RPHurtDefOf.RP_LookedAfter)})");
                // Not a case any more by the time an answer is applied: state-posted, or a callout.
                Pawn posted = S31Guard(map, r), callout = S31Guard(map, r);
                S31Wound(posted, HediffDefOf.Cut, 12f);
                S31Wound(callout, HediffDefOf.Cut, 12f);
                InjuryCase pc = HurtOnDuty.Open(posted, inmate, HurtOnDuty.CauseAssault), cc = HurtOnDuty.Open(callout, inmate, HurtOnDuty.CauseAssault);
                CompStaff psc = posted.GetComp<CompStaff>(), csc = callout.GetComp<CompStaff>();
                psc.statePosted = true;
                csc.leaveAtTick = Find.TickManager.TicksGame + HodDay;
                HurtOnDuty.Apply(posted, psc, pc, "RP_Hod_Compensation", map);
                HurtOnDuty.Apply(callout, csc, cc, "RP_Hod_Compensation", map);
                if (g.ledger.funds != funds0 || pc.answer != HurtOnDuty.AnswerNone || cc.answer != HurtOnDuty.AnswerNone) HodFail(check, $"an answer to a state-posted officer or a callout paid (funds {funds0} -> {g.ledger.funds}, answers {pc.answer}/{cc.answer})");
                psc.statePosted = false;
                csc.leaveAtTick = -1;
            });
        }

        /// <summary>
        /// An old save's case (no injury entitlement stored: <c>injuryUntil</c> -1; the leave it started has no reason): "back on the next shift" is offered while that leave runs (the leave's own end stands for the
        /// entitlement) and ends it; an answered case closes at the return through the gate whatever the leave's reason (it stayed open for ever when only an injury reason closed it). Fails if "back" is refused or
        /// leaves the leave running, or if the case is still open after the return.
        /// </summary>
        private static void CheckHodOldSaveLeaveClosesTheCase(Map map)
        {
            const string check = "old save";
            Guarded("hurt on duty: an old save's case and its leave with no reason", map, r =>
            {
                HodBegin(r);
                PrisonGame g = PrisonGame.Instance;
                Pawn guardBack = S31Guard(map, r), guardPaid = S31Guard(map, r), inmate = Throwaway(map, r);
                var cases = new List<(Pawn guard, CompStaff sc, InjuryCase c, ChoiceLetter_PrisonDecision letter)>();
                foreach (Pawn guard in new[] { guardBack, guardPaid })
                {
                    CompStaff sc = guard.GetComp<CompStaff>();
                    S31Wound(guard, HediffDefOf.Cut, 12f);
                    InjuryCase c = HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                    // The case as an old save has it: the leave it started (4 days, no reason), no entitlement stored, nothing kept beside it.
                    (sc.leaveUntilTick, sc.leaveReason) = (Find.TickManager.TicksGame + 4 * HodDay, HurtOnDuty.ReasonNone);
                    (c.injuryUntil, c.priorLeaveUntil, c.priorLeaveReason) = (-1, -1, HurtOnDuty.ReasonNone);
                    cases.Add((guard, sc, c, HodLetters(r).LastOrDefault(l => !l.answered && l.pawns.Contains(guard))));
                    // (Two hours on: the next officer's letter is not a row on this one's.)
                    Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 2 * HodHour);
                }
                if (cases.Any(x => x.c == null || x.letter == null || !HurtOnDuty.InjuryRuns(x.sc, x.c))) { HodFail(check, "premise: an old save's case with its leave running and a letter that can be answered"); return; }
                // Back on the next shift: offered, and it ends the leave the case started; the answered case closes with it.
                (Pawn back, CompStaff bsc, InjuryCase bc, ChoiceLetter_PrisonDecision bl) = cases[0];
                HodAnswer(bl, "RP_Hod_Back", check);
                if (bc.answer != HurtOnDuty.AnswerBack || StaffLives.OnLeave(bsc) || bsc.leaveReason != HurtOnDuty.ReasonNone || bsc.injuryCaseId != -1 || bsc.rushedBackUntilTick < Find.TickManager.TicksGame)
                    HodFail(check, $"sent back on an old save's case, the leave did not end (answer {bc.answer}, on leave {StaffLives.OnLeave(bsc)}, reason {bsc.leaveReason}, open case {bsc.injuryCaseId}, rushed back until {bsc.rushedBackUntilTick})");
                // Answered another way, the leave runs on with no reason; at the return through the gate the case closes.
                (Pawn paid, CompStaff psc, InjuryCase pc, ChoiceLetter_PrisonDecision pl) = cases[1];
                HodAnswer(pl, "RP_Hod_Compensation", check);
                if (pc.answer != HurtOnDuty.AnswerCompensation || !StaffLives.OnLeave(psc) || psc.leaveReason != HurtOnDuty.ReasonNone || psc.injuryCaseId != pc.id)
                    HodFail(check, $"premise: answered, the old leave runs on with no reason and the case is still open (answer {pc.answer}, on leave {StaffLives.OnLeave(psc)}, reason {psc.leaveReason}, case {psc.injuryCaseId})");
                if (!S31Commute(map, paid, 4 * 24 + 1, check)) return;
                if (psc.injuryCaseId != -1) HodFail(check, $"after the old leave ran out and they came back through the gate the answered case is still open ({psc.injuryCaseId})");
            });
        }

        // ---- the record --------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The staff record keeps the latest five cases, newest first, each with the date, the severity, the attacker's name as it was and the answer. Fails if more are kept, the order is wrong or a line lacks what it says.
        /// </summary>
        private static void CheckHodRecord(Map map)
        {
            const string check = "record";
            Guarded("hurt on duty: the staff record", map, r =>
            {
                HodBegin(r);
                Pawn guard = S31Guard(map, r);
                CompStaff sc = guard.GetComp<CompStaff>();
                var names = new List<string>();
                for (int i = 0; i < 7; i++)
                {
                    Pawn inmate = Throwaway(map, r);
                    names.Add(inmate.LabelShort);
                    S31Clean(guard);
                    S31Wound(guard, HodBruise, 3f);
                    HurtOnDuty.Open(guard, inmate, HurtOnDuty.CauseAssault);
                    // (A day and an hour on: the next is another case.)
                    Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + HodDay + HodHour);
                }
                List<string> lines = HurtOnDuty.RecordLines(sc);
                if (sc.injuries.Count != HurtOnDuty.RecordKept || lines.Count != HurtOnDuty.RecordKept) HodFail(check, $"the record should keep the latest {HurtOnDuty.RecordKept} cases (kept {sc.injuries.Count}, listed {lines.Count})");
                else if (!lines[0].Contains(names[6]) || !lines[4].Contains(names[2]) || lines.Any(l => !l.Contains("RP_HodLevel_1".Translate().Resolve()) || !l.Contains("RP_HodAnswer_1".Translate().Resolve())))
                    HodFail(check, $"the record's lines are not the newest first with severity and answer: {string.Join(" / ", lines)}");
            });
        }

        // ---- the round trip through a save ---------------------------------------------------------------------------------------------------------------------

        private static int hodGuestId = -1, hodFundsBeforeSeed;

        /// <summary>For the snapshot: every officer's injury fields and cases (the ones at home too), the case counter, and the hurt-on-duty letters (on the stack and held) with their rows.</summary>
        private static string HodDescribe(PrisonGame g, Map map) =>
            g.story.nextInjuryCaseId + ":" + string.Join(",", S31Employed(map).Select(p =>
            {
                CompStaff sc = p.GetComp<CompStaff>();
                return $"{p.thingIDNumber}:{sc.leaveReason}:{sc.injuryCaseId}:{sc.rushedBackUntilTick}:[{string.Join(";", sc.injuries.Select(c => c.Describe()))}]";
            })) + "/" + string.Join(",", StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Where(d => d.Is(HurtOnDuty.DecisionDef)).Select(d => $"{d.answered}:{d.note}:{d.amount}:{string.Join("+", d.pawns.Select(p => p.thingIDNumber))}"));

        /// <summary>
        /// A guard made for the round trip: hurt seriously by a real inmate (an open case with its letter unanswered, the leave of 3 days), at home, rushed back until a tick, with an older closed case (every field off its default).
        /// The guard goes in with the save and is the one <see cref="CheckHodAfterReload"/> finds.
        /// </summary>
        private static void SeedHurtOnDutyForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            hodFundsBeforeSeed = g.ledger.funds;
            // (The attacker lends the case an id and a name and is not touched: opening a case reads nothing else of them, so this pick cannot be what moved the count's inmate in h2s (that cause is not known; the count's check
            // names the pawn when it recurs). It is still the highest-id inmate that is neither the count's nor its runaway, which costs nothing and keeps clear of the other seeds' lowest ids. No other seed's pick changes.)
            Pawn inmate = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() != null && p.thingIDNumber != countSeed.pawnId && p.thingIDNumber != countSeedWalker)
                .OrderByDescending(p => p.thingIDNumber).FirstOrDefault();
            if (inmate == null) reloadProblems.Add("saveload: no inmate to have hurt the seeded guard");
            StaffRoleDef role = RPSecurityDefOf.RP_Guard;
            Pawn guest = StaffUtility.GenerateCandidate(role);
            GenSpawn.Spawn(guest, IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            StaffUtility.ApplyRole(guest, role, record: false);
            NoMentalStateTraits(guest);
            NoMentalStateHediffs(guest);
            S31Clean(guest);
            S31Wound(guest, HediffDefOf.Cut, 12f);
            CompStaff sc = guest.GetComp<CompStaff>();
            int now = Find.TickManager.TicksGame;
            sc.injuries.Add(new InjuryCase { id = g.story.nextInjuryCaseId++, tick = Mathf.Max(1, now - 9 * HodDay), attackerId = 4242, attackerName = "Seeded Old", level = HurtOnDuty.Lasting, answer = HurtOnDuty.AnswerPension, lettered = true, paid = 1200, priorLeaveUntil = 7, priorLeaveReason = HurtOnDuty.ReasonBurnout, injuryUntil = Mathf.Max(2, now - 4 * HodDay) });
            HurtOnDuty.Open(guest, inmate, HurtOnDuty.CauseAssault);
            sc.rushedBackUntilTick = now + 5 * HodDay;
            StaffCommute.Leave(guest);
            hodGuestId = guest.thingIDNumber;
            RPLog.Message($"[RimPrison:test] saveload hurt on duty: {HodDescribe(g, map)} (attacker {inmate?.thingIDNumber}; the count's inmates, {countSeed.pawnId} and {countSeedWalker}, are not in it)");
        }

        /// <summary>
        /// The seeded guard came back from the save: still at home on the leave with the injury as its reason, both cases and the letter intact, the letter answered through its own path pays once and a replay pays nothing; then the
        /// guard, the letter and the money are taken out. Fails if the leave, the reason, a case or the letter did not survive the reload (the snapshot says which field), or the answer pays twice.
        /// </summary>
        private static void CheckHodAfterReload(Map map)
        {
            if (hodGuestId < 0) return;
            PrisonGame g = PrisonGame.Instance;
            Pawn guest = g.OffSite.FirstOrDefault(x => x.thingIDNumber == hodGuestId);
            if (guest == null) reloadProblems.Add($"the hurt guard at home did not come back from the round trip (id {hodGuestId})");
            else
            {
                CompStaff sc = guest.GetComp<CompStaff>();
                ChoiceLetter_PrisonDecision letter = StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(d => d.Is(HurtOnDuty.DecisionDef) && !d.answered && d.pawns.Contains(guest));
                if (!StaffLives.OnLeave(sc) || sc.leaveReason != HurtOnDuty.ReasonInjury || sc.injuries.Count != 2 || sc.injuryCaseId != sc.injuries[1].id || letter == null)
                    reloadProblems.Add($"the hurt guard's leave or case did not survive the reload (on leave {StaffLives.OnLeave(sc)}, reason {sc.leaveReason}, {sc.injuries.Count} cases, open {sc.injuryCaseId}, letter {letter != null})");
                else
                {
                    int funds0 = g.ledger.funds;
                    // (The compensation gives every colleague a memory; the ones this answer gave are taken off again, so that no later step of the plan runs with them.)
                    var lookedAfter0 = new HashSet<Thought_Memory>(StaffCommute.AllStaff(map).SelectMany(o => o.needs?.mood?.thoughts.memories.Memories.Where(m => m.def == RPHurtDefOf.RP_LookedAfter) ?? Enumerable.Empty<Thought_Memory>()));
                    letter.answered = true;
                    PrisonDecisions.Choose(letter, letter.decision.Worker.Options(letter).ToList().IndexOf("RP_Hod_Compensation"));
                    PrisonDecisions.Choose(letter, letter.decision.Worker.Options(letter).ToList().IndexOf("RP_Hod_Compensation"));
                    if (g.ledger.funds != funds0 - 400 || sc.injuries[1].answer != HurtOnDuty.AnswerCompensation)
                        reloadProblems.Add($"after the reload the letter's answer paid {funds0 - g.ledger.funds} (400 once expected), the case answer {sc.injuries[1].answer}");
                    Find.LetterStack.RemoveLetter(letter);
                    foreach (Pawn o in StaffCommute.AllStaff(map))
                        foreach (Thought_Memory m in o.needs?.mood?.thoughts.memories.Memories.Where(m => m.def == RPHurtDefOf.RP_LookedAfter && !lookedAfter0.Contains(m)).ToList() ?? new List<Thought_Memory>())
                            o.needs.mood.thoughts.memories.RemoveMemory(m);
                }
                g.home.Remove(guest);
                if (!guest.Destroyed) guest.Destroy(DestroyMode.Vanish);
                StaffCareers.InvalidateLeads();
            }
            g.ledger.funds = hodFundsBeforeSeed;
            hodGuestId = -1;
        }
    }
}
