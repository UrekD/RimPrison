using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The handover (Story/Handover.cs): the prison you were given has a past. The <c>handover</c> plan (the state penitentiary, as the scenario starts it with the dialog's choice on) judges the start: every seed is
    /// there and none shows on an Intel row, a file or an overlay before a find bar the two the code shows by design (the guard's suspicion file on the staff row, the feud's enmity on the two files); the old warden's note is
    /// the welcome letter and names only a suspicion and a wing and a crew; the chronicle's old entries sort before day one and the press does not read them as news; and, at the end, a cell search finds the tunnel by the
    /// usual path; the first daily pass keeps the debts (run once at the start and put back). The other plans start clean (<see cref="CheckHandoverStartsClean"/>). The events plan runs the self-contained part (<see cref="CheckHandover"/>): the
    /// options and the dialog's default, the letter, the clock arithmetic of a day before the game, the note on made-up seeds and a sowing on the live prison (the small one) put back. The round trip is in the saveload plan
    /// (<see cref="SeedHandoverForReload"/>). Premises the live prison may not give are told, not failed.
    /// </summary>
    public static partial class AutoTest
    {
        private static void HandoverFail(string check, string what) => problems0.Add($"handover ({check}): {what}");

        // ---- every other plan starts clean --------------------------------------------------------------------------------------------------------------

        /// <summary>A plan that does not ask for the handover starts as it always did: no flag, no secrets, nothing in the chronicle from before the game, and a welcome letter that is not the old warden's note.</summary>
        private static void CheckHandoverStartsClean(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            var seen = new List<string>();
            if (g.handover) seen.Add("the game carries the handover flag");
            if (Handover.Last != null) seen.Add("a handover was sown");
            if (g.story.entries.Any(e => e.tick < 0)) seen.Add("the chronicle has entries from before the game");
            if (g.story.debts.Count > 0) seen.Add($"{g.story.debts.Count} debts");
            if (RingSystem.Rings().Any()) seen.Add("a smuggling ring");
            if (g.story.arcs.Count > 0) seen.Add($"{g.story.arcs.Count} story arcs");
            if (g.story.complaints.Count > 0) seen.Add("a complaint");
            if (pm.turf.claims.Count > 0) seen.Add("a claim on turf");
            if (map.mapPawns.PrisonersOfColony.Any(p => p.Inmate() is { tunnelProgress: > 0f } or { tunnelKnown: true })) seen.Add("a tunnel");
            if (map.mapPawns.PrisonersOfColony.Any(p => p.relations != null && p.relations.DirectRelations.Any(r => r.def == RPRelationDefOf.RP_Enemy))) seen.Add("an enmity");
            foreach (Pawn s in StaffCommute.AllStaff(map))
                if (s.GetComp<CompStaff>() is { } sc && (sc.suspicion > 0f || sc.corrupt)) seen.Add($"{s.LabelShort} is under suspicion or on the take");
            string signature = "RP_HandoverNote_Sign".Translate().Resolve();
            if (Find.LetterStack.LettersListForReading.Any(l => l is ChoiceLetter cl && cl.Text.Resolve().Contains(signature))) seen.Add("a letter from the old warden");
            if (seen.Count > 0) HandoverFail("a clean start", $"the {Plan} plan did not ask for the handover and its start has: {string.Join(", ", seen)}");
        }

        // ---- the handover plan, at the start ---------------------------------------------------------------------------------------------------------------

        /// <summary>The handover plan's start: the prison as the scenario built it, paused, before any tick.</summary>
        private static void CheckHandoverAtStart(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            Handover.Seeds s = Handover.Last;
            if (s == null)
            {
                HandoverFail("sown", "nothing was sown at the start: the plan's options did not reach the scenario's start (Handover.Last is empty)");
                return;
            }
            RPLog.Message($"[RimPrison:test] handover at the start: {s.Describe()}");
            foreach (string what in HandoverProblems(map, s, expectBig: true, fresh: true)) HandoverFail("the start", what);
            // The welcome letter is the note, then the practical paragraph, then the steps and the tail of the penitentiary's letter (the same text the game builds).
            Letter welcome = Find.LetterStack.LettersListForReading.FirstOrDefault(l => l.Label.ToString() == "RP_IntroLabel".Translate(g.prisonName).ToString());
            string expected = PrisonGame.IntroText(g.prisonName, g.intake.hour, Facility.Penitentiary, Handover.NoteText(g.prisonName, s));
            string text = (welcome as ChoiceLetter)?.Text.Resolve();
            if (welcome == null) HandoverFail("the note", "no welcome letter at the start");
            else if (text != expected) HandoverFail("the note", $"the welcome letter is not the old warden's note with the penitentiary's steps (it reads \"{text?.Replace("\n", " / ")}\")");
            // What every other letter on the stack says may not name a hidden person (the old warden could not have known), nor the words of a system that is hidden. (The welcome letter is judged above, by its note: its steps
            // and its tail are the game's own words, which name the Intel screen's feuds.)
            foreach (Letter l in Find.LetterStack.LettersListForReading)
                if (l != welcome && l is ChoiceLetter cl && HandoverLeaks(cl.Text.Resolve(), s) is { } leak)
                    HandoverFail("hidden", $"the letter \"{l.Label}\" names {leak}");
            CheckHandoverFirstDailyPass(map, s);
        }

        /// <summary>
        /// The first daily pass keeps the seeded debts: run once (the debts are overdue by a day or two, the debtors without cash), each is still in the book and has been threatened, not paid, dropped or written off.
        /// What the pass touched is put back: the debts' stage and clock, the book (a protection offer may open one), and the debtors' fear (the rollback has the tension and the memories).
        /// </summary>
        private static void CheckHandoverFirstDailyPass(Map map, Handover.Seeds s)
        {
            PrisonGame g = PrisonGame.Instance;
            Guarded("handover: the first daily pass keeps the seeded debts", map, r =>
            {
                List<InmateDebt> book = g.story.debts.ToList();
                int nextId = g.story.nextDebtId;
                var stages = s.debts.Select(d => (d, d.stage, d.nextTick, d.stuckSince, d.beatings)).ToList();
                var fears = map.mapPawns.PrisonersOfColony.Where(p => p.Inmate() != null).Select(p => (p.Inmate(), p.Inmate().fear)).ToList();
                r.Add(() =>
                {
                    g.story.debts.Clear();
                    g.story.debts.AddRange(book);
                    g.story.nextDebtId = nextId;
                    foreach ((InmateDebt d, DebtStage stage, int nextTick, int stuckSince, int beatings) in stages) (d.stage, d.nextTick, d.stuckSince, d.beatings) = (stage, nextTick, stuckSince, beatings);
                    foreach ((CompInmate c, float fear) in fears) c.fear = fear;
                });
                DebtSystem.Daily(map);
                foreach (InmateDebt d in s.debts)
                {
                    if (!g.story.debts.Contains(d)) HandoverFail("the debts", $"the first daily pass took the debt of {d.debtorName} to {d.creditorName} out of the book ({d.amount}/{d.original} {d.stage})");
                    else if (d.stage != DebtStage.Threatened) HandoverFail("the debts", $"after the first daily pass the debt of {d.debtorName} to {d.creditorName} is {d.stage}, wanted threatened");
                }
            });
        }

        /// <summary>The handover plan's end: two days on, the old entries are still first and a cell search of the seeded cell finds the tunnel.</summary>
        private static void CheckHandoverAtEnd(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            Handover.Seeds s = Handover.Last;
            if (s == null)
            {
                HandoverFail("the end", "no sowing to judge");
                return;
            }
            if (!g.handover) HandoverFail("the end", "the handover flag was lost during the run");
            if (g.gradeHistory.Count == 0) RPLog.Message("[RimPrison:test] handover: no daily pass had run by the end of the run");
            else RPLog.Message($"[RimPrison:test] handover: after {g.gradeHistory.Count} daily passes the seeded debts are {string.Join(", ", s.debts.Select(d => g.story.debts.Contains(d) ? $"{d.debtorName} owes {d.creditorName} {d.amount}/{d.original} {d.stage}" : $"{d.debtorName} to {d.creditorName} gone from the book"))}");
            // The claim on the yard: told, not failed (the gang may have lost it to a midnight's count, see Handover.GangShare).
            if (s.gang != null) RPLog.Message($"[RimPrison:test] handover: the claim on the yard {(PrisonMap.Of(map).turf.claims.Any(x => x.zone == RPDefOf.RP_Yard && x.gang == s.gang) ? "held" : "was lost")} through {g.gradeHistory.Count} daily passes");
            // The chronicle's old entries are still there, still first.
            int oldNow = g.story.entries.Count(e => e.tick < 0);
            if (oldNow < s.chronicle.Count) HandoverFail("the chronicle", $"{s.chronicle.Count - oldNow} of the old warden's entries are gone after the run");
            if (s.tunneller == null || s.tunneller.Dead || !s.tunneller.Spawned || InmateUtility.CellOf(s.tunneller) is not { } cell)
            {
                RPLog.Message("[RimPrison:test] handover: the digger is not in a cell at the end of the run (dead, moved or out); the cell search was not run");
                return;
            }
            CompInmate c = s.tunneller.Inmate();
            Guarded("handover: a cell search of the seeded cell finds the tunnel", map, r =>
            {
                // (A member of staff with no role, so that nothing the search turns up is written up as a complaint.)
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                StaffRoleDef role = guard.GetComp<CompStaff>().role;
                guard.GetComp<CompStaff>().role = null;
                r.Add(() => guard.GetComp<CompStaff>().role = role);
                float progress = c.tunnelProgress;
                bool dug = progress > 0.05f;
                int found0 = g.stats.tunnelsFound;
                // (A search finds a tunnel 85 times in a hundred: a dozen of them is a sure thing, and what is tested is that the path finds it.)
                for (int i = 0; i < 12 && c.tunnelProgress > 0.05f; i++) TunnelSystem.SearchCell(guard, cell);
                if (!dug)
                {
                    // Found already, in the run, by a search of the guards' own (a noise noticed): the usual path, and then there was nothing left to find.
                    if (g.stats.tunnelsFound == 0) HandoverFail("the tunnel", $"the seeded tunnel was gone without a find at the end of the run (progress {progress:F2}): {s.tunneller.LabelShort} broke out, or it was reset");
                    else RPLog.Message($"[RimPrison:test] handover: the tunnel was found in the run already ({g.stats.tunnelsFound} found)");
                    return;
                }
                if (c.tunnelProgress > 0.05f) HandoverFail("the tunnel", $"twelve searches of {s.tunneller.LabelShort}'s cell did not find the tunnel (progress {progress:F2})");
                else if (g.stats.tunnelsFound != found0 + 1) HandoverFail("the tunnel", $"the tunnel is gone but the find was not counted ({found0} -> {g.stats.tunnelsFound})");
                else RPLog.Message($"[RimPrison:test] handover: a search of {s.tunneller.LabelShort}'s cell found the tunnel at {progress:F2}");
            });
        }

        // ---- what is judged: the sowing, what shows, the note, the chronicle ------------------------------------------------------------------------------------

        /// <summary>
        /// Everything a sowing should have made, as lines for what is wrong (none when it is right). <paramref name="expectBig"/>: the penitentiary's (the ring, the gang and the tunnel too); <paramref name="fresh"/>: the
        /// game's own start, with nothing else in it (no finds on the map, nothing more smuggled in than the sowing found). <paramref name="already"/>: a live prison's (not fresh) lines the screens showed before the sowing, which are not the sowing's.
        /// </summary>
        private static List<string> HandoverProblems(Map map, Handover.Seeds s, bool expectBig, bool fresh, HashSet<string> already = null)
        {
            var bad = new List<string>();
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            int now = Find.TickManager.TicksGame;
            if (!g.handover) bad.Add("the game does not carry the handover flag");
            string name(Pawn p) => p?.LabelShort ?? "nobody";

            // ---- the seeds exist
            if (s.fileGuard?.GetComp<CompStaff>() is not { } fsc) bad.Add("no guard has a suspicion file");
            else
            {
                if (!(fsc.suspicion > 0f && fsc.suspicion < CorruptionSystem.CaseAt)) bad.Add($"{name(s.fileGuard)}'s suspicion is {fsc.suspicion:F0}, wanted over 0 and under the {CorruptionSystem.CaseAt:F0} that opens a case");
                if (!fsc.evidence.Contains("RP_Sus_SeenTalking")) bad.Add($"{name(s.fileGuard)}'s file has no evidence line");
                if (fsc.iaOpen) bad.Add($"internal affairs already has a case open on {name(s.fileGuard)}");
            }
            if (s.debts.Count < (expectBig ? 2 : 1)) bad.Add($"{s.debts.Count} debts were sown, wanted {(expectBig ? 2 : 1)}");
            foreach (InmateDebt d in s.debts)
            {
                string line = $"the debt of {d.debtorName} to {d.creditorName}";
                if (!g.story.debts.Contains(d)) bad.Add(line + " is not in the book");
                if (!d.Overdue) bad.Add(line + " is not overdue");
                if (d.known) bad.Add(line + " is known to the warden");
                if (d.stage != DebtStage.Current) bad.Add(line + $" has already moved to {d.stage}");
                Pawn creditor = map.mapPawns.PrisonersOfColony.FirstOrDefault(p => p.thingIDNumber == d.creditorId), debtor = map.mapPawns.PrisonersOfColony.FirstOrDefault(p => p.thingIDNumber == d.debtorId);
                if (creditor == null || debtor == null) bad.Add(line + " has a party who is not an inmate here");
                else if (creditor.Inmate().security != debtor.Inmate().security) bad.Add(line + " is across wings: the creditor cannot reach the debtor and it is written off in three days");
                else if (debtor.Inmate().cash != 0) bad.Add($"{d.debtorName} has cash ({debtor.Inmate().cash}) to pay it with at the first daily pass");
            }
            if (s.feudA == null || s.feudB == null) bad.Add("no feud was sown");
            else
            {
                if (!PrisonRelations.Enemies(s.feudA, s.feudB) || !PrisonRelations.Enemies(s.feudB, s.feudA)) bad.Add($"{name(s.feudA)} and {name(s.feudB)} are not enemies on both files");
                if (s.feudA.Inmate().security != s.feudB.Inmate().security) bad.Add($"the feud's two are in different wings ({s.feudA.Inmate().security?.defName}, {s.feudB.Inmate().security?.defName}): they never meet");
                if (InmateUtility.CellOf(s.feudA) is { } ca && InmateUtility.CellOf(s.feudB) is { } cb && ca == cb) bad.Add("the feud's two share a cell");
                if (g.story.arcs.Any(a => !a.done && a.def == RPRelationDefOf.RP_Arc_Feud && (a.pawns.Contains(s.feudA) || a.pawns.Contains(s.feudB)))) bad.Add("a feud arc is running for them: it would be on the Intel screen with a letter");
            }
            if (fresh) bad.AddRange(HandoverEnmitiesBeyondTheFeud(map, s));
            if (s.complaint == null) bad.Add("no complaint is on its way");
            else
            {
                if (s.complaint.stage != ComplaintStage.Filed) bad.Add($"the complaint is {s.complaint.stage}, wanted filed and not yet on the warden's desk");
                if (!g.story.complaints.Contains(s.complaint)) bad.Add("the complaint is not among the game's");
                if (s.complaint.dueTick <= now) bad.Add("the complaint is due at once (it would be a letter in the first hour)");
                if (s.complaint.filedTick >= now) bad.Add("the complaint was not filed before the game");
                if (s.complaint.inmateId != s.complainant?.thingIDNumber) bad.Add("the complaint is not the complainant's");
                if (s.guardsFit > 1 && s.complaint.officerId == s.fileGuard?.thingIDNumber) bad.Add("the complaint is against the guard with the file: two threads in one person");
            }
            if (expectBig)
            {
                // ---- the tunnel
                if (s.tunneller == null) bad.Add("no tunnel was sown");
                else
                {
                    CompInmate tc = s.tunneller.Inmate();
                    if (s.tunnelLevel != RPSecurityDefOf.RP_Maximum) RPLog.Message($"[RimPrison:test] handover: the tunnel is in a {s.tunnelLevel?.defName} cell (the prison has no maximum inmate in a cell)");
                    if (InmateUtility.CellOf(s.tunneller) == null) bad.Add($"{name(s.tunneller)} has no cell to dig under");
                    if (Math.Abs(tc.tunnelProgress - Handover.TunnelProgress) > 0.01f) bad.Add($"the tunnel is at {tc.tunnelProgress:F2}, wanted a third");
                    if (tc.tunnelKnown || tc.cellSearchPending || tc.suspected) bad.Add("the tunnel is known, or a search of the cell is pending, or the digger is flagged");
                    if (!TunnelSystem.HasDiggingTool(s.tunneller) || !StashUtility.Has(tc, ContrabandKind.Tool)) bad.Add("the digger has no tool in the cell's stash: the tunnel would not go on");
                    if (!TunnelSystem.CanDig(s.tunneller, tc)) bad.Add("the digger could not dig (not a lifer, not an escape artist, not tense)");
                }
                // ---- the ring
                if (s.dealer == null || s.ring == null) bad.Add("no ring was sown");
                else
                {
                    SmugglingRing ring = s.ring.ring;
                    if (!g.story.arcs.Contains(s.ring) || RingSystem.ArcOfDealer(s.dealer) != s.ring) bad.Add("the ring is not the dealer's running arc");
                    if (!RingSystem.IsDealer(s.dealer.Inmate())) bad.Add($"{name(s.dealer)} is not a dealer");
                    if (s.dealer.Inmate().KnownReputation(DefDatabase<ReputationDef>.GetNamed("RP_Dealer"))) bad.Add($"{name(s.dealer)}'s dealing is known to the warden");
                    if (!ring.links.Exists(l => l.role == RingRole.Courier && l.pawnId == s.courier?.thingIDNumber)) bad.Add("the ring has no courier");
                    RingLink officerLink = ring.links.Find(l => l.role == RingRole.Officer);
                    if (officerLink == null) bad.Add("the ring has no officer on the payroll");
                    else if (officerLink.pawnId != s.officer?.thingIDNumber || s.officer.GetComp<CompStaff>() is not { corrupt: true } osc || osc.patron != s.dealer) bad.Add("the ring's officer is not the corrupt guard on the dealer's payroll");
                    if (ring.links.Any(l => l.known)) bad.Add("a link of the ring is known");
                    if (RingSystem.Visible(s.ring)) bad.Add("the ring is visible");
                    if (s.officer != s.fileGuard) bad.Add("the guard with the file is not the officer on the payroll: the hint would send the new warden to the wrong guard");
                }
                // ---- the gang and the yard
                if (s.gang == null || s.gangMembers.Count < Handover.GangMin) bad.Add($"the gang that holds the yard has {s.gangMembers.Count} members, wanted at least {Handover.GangMin}");
                else
                {
                    foreach (Pawn m in s.gangMembers)
                        if (m.Inmate().gang != s.gang || m.Inmate().gangKnown) bad.Add($"{name(m)} is not of the {s.gang.label}, or the warden knows them for one");
                    // (A gang rolled at intake may have its leader in another wing already, and then it is not given a second one.)
                    if (!GangUtility.HasLeader(map, s.gang, null)) bad.Add("the gang has no leader");
                    if (GangTurf.GangKnown(map, s.gang)) bad.Add("the warden knows the gang that holds the yard");
                    TurfClaim claim = pm.turf.claims.Find(x => x.zone == RPDefOf.RP_Yard && x.gang == s.gang);
                    if (claim == null) bad.Add("the gang holds no claim on the yard");
                    else if (!pm.zones.CellsOf(RPDefOf.RP_Yard).Take(4000).Any(c => GangTurf.HolderAt(pm, c) == s.gang)) bad.Add("no cell of the yard has the gang as its holder");
                }
                // ---- the crew: the dealer and the courier work the one the note does not name
                if (s.problemCrew == null) RPLog.Message("[RimPrison:test] handover: the dealer has no job (no laundry to work in?): the wrong hint points at no crew that is the problem");
                if (s.wrongCrew == s.problemCrew || s.wrongCrew == s.courier?.Inmate().job) bad.Add($"the note's crew ({s.wrongCrew?.label}) is the crew the dealer or the courier works: the hint would be true");
            }
            if (s.wrongCrew == null) bad.Add("the note has no crew to point at, so no wrong hint");

            // ---- the chronicle: the old warden's milestones, before day one
            List<ChronicleEntry> entries = g.story.entries;
            int firstNew = entries.FindIndex(e => e.tick >= 0), lastOld = entries.FindLastIndex(e => e.tick < 0);
            int oldCount = entries.Count(e => e.tick < 0);
            if (oldCount < (expectBig ? 5 : 3)) bad.Add($"{oldCount} old chronicle entries, wanted at least {(expectBig ? 5 : 3)}");
            if (firstNew >= 0 && lastOld > firstNew) bad.Add("an old entry sorts after a new one in the chronicle");
            for (int i = 1; i <= lastOld; i++)
                if (entries[i].tick < entries[i - 1].tick) bad.Add("the old entries are not in order of time");
            foreach (ChronicleEntry e in entries.Where(e => e.tick < 0))
            {
                if (e.Day >= 0 || e.Day < -Handover.OldestDays || e.tick > -Handover.NewestDays * GenDate.TicksPerDay + 23 * GenDate.TicksPerHour) bad.Add($"an old entry is at tick {e.tick} (day {e.Day}), outside the quadrum before the game");
                if (e.text.NullOrEmpty() || e.text.StartsWith("RP_") || e.text.Contains("{")) bad.Add($"an old entry reads \"{e.text}\"");
                if (e.ids.Count > 0) bad.Add($"an old entry names pawns (\"{e.text}\")");
                if (!Story.When(e.tick).Contains(" before ")) bad.Add($"an old entry's time reads \"{Story.When(e.tick)}\"");
                if (HandoverLeaks(e.text, s, names: false) is { } leak) bad.Add($"an old entry names {leak}: \"{e.text}\"");
            }
            if (!entries.Any(e => e.tick < 0 && e.importance >= 3)) bad.Add("no old entry is a milestone");
            // (The press and the weekly report read the last days: the old warden's words are not news for them.)
            if (fresh && IncidentWorker_PressInterest.RecentMilestones(g) != entries.Count(e => e.tick >= now - 5 * GenDate.TicksPerDay && e.importance >= 3)) bad.Add("the press counts an old entry as a recent milestone");
            if (PressDecision.Topic(g) is { } topic && entries.Any(e => e.tick < 0 && e.text == topic)) bad.Add("the press's topic is an old entry");
            List<ChronicleEntry> latest = ChronicleTab.Latest(g, 100).ToList();
            int lastNewShown = latest.FindLastIndex(e => e.tick >= 0), firstOldShown = latest.FindIndex(e => e.tick < 0);
            if (firstOldShown >= 0 && lastNewShown > firstOldShown) bad.Add("the overview lists an old entry before a new one (newest first)");

            // ---- hidden: what the screens show before a find
            string only = "intel staff: " + name(s.fileGuard);
            foreach (string line in HandoverShown(map, s, wholeScreen: fresh || already != null).Where(x => x != only && (already == null || !already.Contains(x)))) bad.Add("shows before a find: " + line);
            if (fresh)
            {
                if (pm.finds.Count > 0) bad.Add($"{pm.finds.Count} marks on the map of finds at the start");
                // (What the arrivals carried in before the sowing is the game's, counted already: the seeds add nothing.)
                if (g.stats.contrabandSmuggled != s.smuggled0 || g.today.contrabandSmuggled != s.todaySmuggled0) bad.Add($"the seeds count as smuggled in (the grade and the statistics: {s.smuggled0} -> {g.stats.contrabandSmuggled}, today {s.todaySmuggled0} -> {g.today.contrabandSmuggled})");
            }

            // ---- the note: two true hints and one wrong one, naming only a suspicion, a wing and a crew
            string note = Handover.NoteText(g.prisonName ?? "", s);
            if (note == null) bad.Add("no note: nothing to hint at");
            else
            {
                int bullets = note.Count(ch => ch == '•');
                if (bullets != 3) bad.Add($"the note has {bullets} hints, wanted three (two true and one wrong)");
                if (s.fileGuard != null && !note.Contains(s.fileGuard.LabelShort)) bad.Add("the note does not name the guard with the file");
                if (expectBig && s.tunnelLevel != null && !note.Contains(s.tunnelLevel.label)) bad.Add("the note does not name the wing the tunnel is under");
                if (!expectBig && s.feudLevel != null && !note.Contains(s.feudLevel.label)) bad.Add("the note does not name the wing the feud is in");
                if (s.wrongCrew != null && !note.Contains(s.wrongCrew.label + " crew")) bad.Add("the note does not tell the new warden to watch the wrong crew");
                if (s.problemCrew != null && note.ToLowerInvariant().Contains(s.problemCrew.label.ToLowerInvariant())) bad.Add($"the note names the crew the problem is in ({s.problemCrew.label})");
                if (HandoverLeaks(note, s) is { } leak) bad.Add($"the note names {leak}");
            }
            return bad;
        }

        /// <summary>
        /// What the old warden could not have known, if the text names it: a hidden person by name, or the word of a system the seeds hide (a ring, a tunnel, a debt, a dealer, a gang's name, a feud). Null when the text is clean.
        /// A file (the guard's name) and a wing or crew are allowed: that is what a suspicion is.
        /// </summary>
        private static string HandoverLeaks(string text, Handover.Seeds s, bool names = true)
        {
            if (text.NullOrEmpty()) return null;
            if (names)
                foreach (Pawn p in s.Hidden())
                    if (p != s.fileGuard && Regex.IsMatch(text, @"\b" + Regex.Escape(p.LabelShort) + @"\b")) return $"{p.LabelShort}, who is part of a secret";
            if (s.gang != null && text.IndexOf(s.gang.label, StringComparison.OrdinalIgnoreCase) >= 0) return "the gang";
            foreach (string word in new[] { " ring", "tunnel", " debt", "dealer", "courier", "smuggl", "complaint", "feud" })
                if (text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return $"\"{word.Trim()}\"";
            return null;
        }

        /// <summary>
        /// What the Intel screen's rows, the files and the overlays would show of the seeds now (the same tests the screens run). <paramref name="wholeScreen"/>: of everything on the screen (the game's own start, where nothing else
        /// has happened); else only of the seeds' own people and things (a live prison that has had a day of tests). The staff row is returned for the guard with the file (the caller allows exactly that: the code shows a file
        /// over 0 on it by design); the enemies line on a file is not looked for here (it is there by design, see <see cref="HandoverEnmitiesBeyondTheFeud"/>).
        /// </summary>
        private static List<string> HandoverShown(Map map, Handover.Seeds s, bool wholeScreen)
        {
            var shown = new List<string>();
            PrisonGame g = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            ReputationDef dealerRep = DefDatabase<ReputationDef>.GetNamed("RP_Dealer");
            var cast = new HashSet<Pawn>(s.Hidden());
            bool Mine(Pawn p) => wholeScreen || cast.Contains(p);
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
            {
                if (p.Inmate() is not { } c || !Mine(p)) continue;
                // The Intel screen's suspects row, its gang rows, and the inmate file's lines.
                if (c.suspected || c.cellSearchPending || c.tunnelKnown) shown.Add($"intel suspects: {p.LabelShort}");
                if (c.gang != null && c.gangKnown && (wholeScreen || c.gang == s.gang)) shown.Add($"intel gangs: {p.LabelShort} of the {c.gang.label}");
                if (c.KnownReputation(dealerRep)) shown.Add($"inmate file: {p.LabelShort} is a dealer");
                if (DebtSystem.KnownAbout(c)) shown.Add($"inmate file and intel: {p.LabelShort} is known to be in a debt");
                if (ComplaintSystem.FileLines(p).Any()) shown.Add($"inmate file: {p.LabelShort} has a complaint line");
            }
            foreach (InmateDebt d in DebtSystem.Known().Where(d => wholeScreen || s.debts.Contains(d))) shown.Add($"intel debts: {d.debtorName} owes {d.creditorName}");
            foreach (StoryArc a in g.story.arcs.Where(a => !a.done && a.def == RPRelationDefOf.RP_Arc_Feud && (wholeScreen || a.pawns.Contains(s.feudA) || a.pawns.Contains(s.feudB))))
                shown.Add($"intel feuds: {a.A?.LabelShort} and {a.B?.LabelShort}");
            foreach (StoryArc ring in RingSystem.VisibleRings().Where(x => wholeScreen || x == s.ring)) shown.Add($"intel rings: {ring.A?.LabelShort}'s");
            foreach (Complaint cm in g.story.complaints.Where(x => x.stage is ComplaintStage.Waiting or ComplaintStage.Investigating && (wholeScreen || x == s.complaint))) shown.Add($"intel complaints: {cm.inmateName} against {cm.officerName}");
            foreach ((int pi, TurfClaim claim) in GangTurf.KnownTurf(map, pm).Where(t => wholeScreen || t.claim.gang == s.gang)) shown.Add($"the turf overlay and the rooms line: {claim.gang.label} holds {GangTurf.PlaceName(pm.zones, pi)}");
            var staffCast = new HashSet<Pawn> { s.fileGuard, s.officer };
            foreach (Pawn staff in StaffCommute.AllStaff(map))
                if ((wholeScreen || staffCast.Contains(staff)) && staff.GetComp<CompStaff>() is { } sc && (sc.suspicion >= 1f || sc.iaOpen || sc.doubleAgent || sc.exposedCorrupt || CorruptionSystem.IaSuspended(sc))) shown.Add($"intel staff: {staff.LabelShort}");
            // The warden's own letters about them: none yet (a letter shows a hidden thing only after a find).
            if (wholeScreen)
                foreach (Letter l in Find.LetterStack.LettersListForReading)
                    if (l is ChoiceLetter_PrisonDecision d && (d.Is("RP_Decision_Complaint") || d.Is("RP_Decision_Arc") || d.Is("RP_Decision_InternalAffairs"))) shown.Add($"a decision letter ({d.decision.defName})");
            return shown;
        }

        /// <summary>
        /// The feud's enmity is a vanilla relation and the inmate file lists the enemies still inside (<c>ITab_InmateFile</c>: a relation to an inmate on the map): on the files of the two and of nobody else. Lines for the
        /// enmities a fresh start has besides the feud's (none: nothing has been fought yet).
        /// </summary>
        private static List<string> HandoverEnmitiesBeyondTheFeud(Map map, Handover.Seeds s)
        {
            var bad = new List<string>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                foreach (DirectPawnRelation r in p.relations?.DirectRelations ?? new List<DirectPawnRelation>())
                    if (r.def == RPRelationDefOf.RP_Enemy && r.otherPawn is { } other && other.IsPrisonerOfColony && other.Map == p.Map && !(p == s.feudA && other == s.feudB || p == s.feudB && other == s.feudA))
                        bad.Add($"{p.LabelShort}'s file lists {other.LabelShort} as an enemy: only the feud's two may be enemies at the start");
            return bad;
        }

        // ---- the events plan: self-contained ---------------------------------------------------------------------------------------------------------------

        private static void CheckHandover(Map map)
        {
            Rand.PushState(20261027);
            try
            {
                CheckHandoverDefaults();
                CheckHandoverLetter();
                CheckHandoverClock();
                CheckHandoverNote(map);
                CheckHandoverSownOnTheLivePrison(map);
            }
            finally
            {
                Rand.PopState();
            }
        }

        /// <summary>The options (clean unless asked), the dialog's default (on), and the pending choice (taken once).</summary>
        private static void CheckHandoverDefaults()
        {
            if (new NewPrisonOptions().inheritProblems) HandoverFail("options", "a start that does not ask for the handover has it on (every test's start would be sown)");
            var dialog = new Dialog_NewPrison();
            NewPrisonOptions chosen = Traverse.Create(dialog).Field("options").GetValue<NewPrisonOptions>();
            if (chosen == null || !chosen.inheritProblems) HandoverFail("dialog", "the new prison dialog does not start with \"inherit the old warden's problems\" on");
            if (!"RP_NewPrisonHandover".CanTranslate() || !"RP_NewPrisonHandoverTip".CanTranslate()) HandoverFail("dialog", "the checkbox has no keyed label or tip");
            bool pending0 = Handover.Pending;
            try
            {
                Handover.Pending = true;
                bool first = Handover.Take(), second = Handover.Take();
                if (!first || second) HandoverFail("pending", $"the dialog's choice is taken {(first ? "" : "not at all")}{(second ? " twice" : "")}: it must be taken once, or it would leak into the next game");
            }
            finally
            {
                Handover.Pending = pending0;
            }
        }

        /// <summary>The welcome letter: as ever without a note, the note then the practical paragraph then the steps and the tail with one.</summary>
        private static void CheckHandoverLetter()
        {
            foreach (Facility f in new[] { Facility.Inherited, Facility.Penitentiary })
            {
                string plain = PrisonGame.IntroText("Redfield", 9, f), withNull = PrisonGame.IntroText("Redfield", 9, f, null), withEmpty = PrisonGame.IntroText("Redfield", 9, f, "");
                if (plain != withNull || plain != withEmpty) HandoverFail("letter", $"the {f} welcome letter changed without a note");
                if (!plain.Contains("out on the rim") || plain.Contains("old warden")) HandoverFail("letter", $"the {f} welcome letter without a note is not the plain one");
                string noted = PrisonGame.IntroText("Redfield", 9, f, "NOTE-BODY");
                string[] paragraphs = noted.Split(new[] { "\n\n" }, StringSplitOptions.None);
                if (paragraphs[0] != "NOTE-BODY") HandoverFail("letter", $"the {f} letter with a note does not begin with it (\"{paragraphs[0]}\")");
                if (paragraphs.Length < 3 || !paragraphs[1].Contains("9:00")) HandoverFail("letter", $"the {f} letter's second paragraph is not the practical one (the shuttle's hour): \"{(paragraphs.Length > 1 ? paragraphs[1] : "")}\"");
                if (noted.Contains("out on the rim")) HandoverFail("letter", $"the {f} letter with a note still has the lead the note replaces");
                string steps = plain.Substring(plain.IndexOf("\n\n", StringComparison.Ordinal) + 2);
                if (!noted.EndsWith(steps)) HandoverFail("letter", $"the {f} letter with a note does not end with the steps and the tail of the plain one");
            }
        }

        /// <summary>The clock arithmetic of a day before the game: a day counts down (not toward zero), and the hours read right; the days after are as they were.</summary>
        private static void CheckHandoverClock()
        {
            (int tick, int day, int hour)[] cases =
            {
                (0, 0, 0), (GenDate.TicksPerHour * 5, 0, 5), (GenDate.TicksPerDay + GenDate.TicksPerHour * 14, 1, 14), (-1, -1, 23), (-GenDate.TicksPerDay, -1, 0),
                (-GenDate.TicksPerDay - 1, -2, 23), (-9 * GenDate.TicksPerDay + 14 * GenDate.TicksPerHour, -9, 14),
                // (Past 2^24 ticks, about day 280, a float no longer holds a tick: the day before is still the day before.)
                (300 * GenDate.TicksPerDay - 1, 299, 23), (300 * GenDate.TicksPerDay, 300, 0), (300 * GenDate.TicksPerDay + 7 * GenDate.TicksPerHour + 1, 300, 7),
            };
            foreach ((int tick, int day, int hour) in cases)
            {
                var e = new ChronicleEntry { tick = tick };
                if (e.Day != day || e.Hour != hour) HandoverFail("clock", $"tick {tick} reads day {e.Day} hour {e.Hour}, wanted day {day} hour {hour}");
            }
            if (Story.When(GenDate.TicksPerDay + GenDate.TicksPerHour * 14) != "RP_ChronicleWhen".Translate(2, 14).Resolve()) HandoverFail("clock", "a day of the game reads differently");
            if (Story.When(300 * GenDate.TicksPerDay - 1) != "RP_ChronicleWhen".Translate(300, 23).Resolve()) HandoverFail("clock", $"the last hour of day 300 reads \"{Story.When(300 * GenDate.TicksPerDay - 1)}\" (a float cannot hold the tick)");
            string before = Story.When(-9 * GenDate.TicksPerDay + 14 * GenDate.TicksPerHour);
            if (!before.Contains("9 days before day 1") || !before.Contains("14h")) HandoverFail("clock", $"nine days before the game reads \"{before}\"");
            if (!"RP_ChronicleBefore".CanTranslate()) HandoverFail("clock", "the chronicle's heading for the time before the game has no keyed string");
        }

        /// <summary>The note on seeds made up for it: two true hints and one wrong one in the penitentiary's words, the inherited facility's own, and no note where nothing was sown.</summary>
        private static void CheckHandoverNote(Map map)
        {
            Guarded("handover: the note", map, r =>
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), digger = Throwaway(map, r), dealer = Throwaway(map, r), pair = Throwaway(map, r);
                InmateJobDef laundry = DefDatabase<InmateJobDef>.GetNamed("RP_JobLaundry"), kitchen = DefDatabase<InmateJobDef>.GetNamed("RP_JobKitchen");
                var big = new Handover.Seeds
                {
                    facility = Facility.Penitentiary, fileGuard = guard, tunneller = digger, tunnelLevel = RPSecurityDefOf.RP_Maximum, dealer = dealer, problemCrew = laundry, wrongCrew = kitchen,
                };
                string note = Handover.NoteText("Redfield", big);
                if (note == null) HandoverFail("the note", "no note for a penitentiary with seeds");
                else
                {
                    if (!note.Contains("Redfield") || !note.Contains(guard.LabelShort) || !note.Contains(RPSecurityDefOf.RP_Maximum.label) || !note.Contains(kitchen.label + " crew")) HandoverFail("the note", $"the penitentiary's note lacks a part of its three hints: {note.Replace("\n", " / ")}");
                    if (note.Count(c => c == '•') != 3) HandoverFail("the note", $"the penitentiary's note has {note.Count(c => c == '•')} hints, wanted three");
                    if (HandoverLeaks(note, big) is { } leak) HandoverFail("the note", $"the penitentiary's note names {leak}");
                    if (note.ToLowerInvariant().Contains("laundry") || note.Contains(digger.LabelShort) || note.Contains(dealer.LabelShort)) HandoverFail("the note", "the note names the problem (the laundry, the digger or the dealer)");
                    // The order: the true hint of the file, the wrong crew, the true hint of the wing.
                    int atFile = note.IndexOf(guard.LabelShort, StringComparison.Ordinal), atCrew = note.IndexOf(kitchen.label + " crew", StringComparison.Ordinal), atWing = note.IndexOf(RPSecurityDefOf.RP_Maximum.label, StringComparison.Ordinal);
                    if (!(atFile < atCrew && atCrew < atWing))
                        HandoverFail("the note", "the hints are not in the order file, crew, wing (the wrong one is between the two true ones)");
                }
                SecurityLevelDef minimum = DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum");
                var small = new Handover.Seeds { facility = Facility.Inherited, fileGuard = guard, feudA = digger, feudB = pair, feudLevel = minimum, wrongCrew = kitchen };
                string smallNote = Handover.NoteText("Redfield", small);
                if (smallNote == null || !smallNote.Contains(minimum.label + " wing") || !smallNote.Contains(guard.LabelShort) || smallNote.Contains(RPSecurityDefOf.RP_Maximum.label) || smallNote.Contains(digger.LabelShort) || smallNote.Contains(pair.LabelShort))
                    HandoverFail("the note", $"the inherited facility's note is not the file, the crew and the feud's wing without the feud's names: {smallNote?.Replace("\n", " / ")}");
                if (Handover.NoteText("Redfield", new Handover.Seeds()) != null || Handover.NoteText("Redfield", null) != null) HandoverFail("the note", "a note was written with nothing sown");
                // The leak test itself finds a leak (so that a clean verdict above means something).
                if (HandoverLeaks("I watched " + digger.LabelShort + " dig a tunnel.", big) == null || HandoverLeaks("a ring of dealers", big) == null) HandoverFail("the note", "the leak test does not catch a name or a system's word");
                if (HandoverLeaks("Watch the kitchen crew.", big) != null) HandoverFail("the note", "the leak test calls a plain hint a leak");
            });
        }

        /// <summary>
        /// A sowing on the live prison (the small one of the events plan: a debt, a feud, a suspicion file, a complaint), judged and put back. The live prison has a day of events behind it: what its screens already showed
        /// (an inmate flagged, a debt or a complaint on a file) is not the sowing's, and the guards it has may all have a file or a case already, so the sowing is given two with clean sheets.
        /// </summary>
        private static void CheckHandoverSownOnTheLivePrison(Map map)
        {
            Guarded("handover: sown on the live prison", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                List<Pawn> able = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { security: not null } && InmateUtility.IsActiveInmate(p) && !p.Downed).ToList();
                SecurityLevelDef minimum = DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum"), medium = RPSecurityDefOf.RP_Medium;
                if (able.Count(p => p.Inmate().security == minimum) < 4 || able.Count(p => p.Inmate().security == medium) < 3)
                {
                    RPLog.Message("[RimPrison:test] handover: the live prison has too few able inmates of the minimum and medium wings for a sowing; not run");
                    return;
                }
                for (int fit = Handover.CleanGuards(map).Count; fit < 2; fit++) ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                // The feud goes to the minimum wing (every medium pair is put in an arc below), so the wing must have two quiet inmates, in cells of their own and in no story, as the sowing wants (Handover.FeudPair).
                // A day of events can leave it none (an inmate flagged, on a debt or a complaint, in an arc, in custody: the sowing's own log line says how many are left); then two made-up ones are put in two free beds of the wing,
                // rolled back with the rest.
                List<Pawn> feudWing = able.Where(p => p.Inmate().security == minimum && Handover.Sowable(p) && !g.story.InArc(p)).OrderBy(p => p.thingIDNumber).ToList();
                RPLog.Message($"[RimPrison:test] handover: the minimum wing has {able.Count(p => p.Inmate().security == minimum)} able inmates, {able.Count(p => p.Inmate().security == minimum && Handover.Sowable(p))} quiet, {feudWing.Count} quiet and in no story, "
                    + $"{feudWing.Count(p => InmateUtility.CellOf(p) != null)} with a cell, a pair {Handover.FeudPair(feudWing) != null}");
                if (Handover.FeudPair(feudWing) == null)
                {
                    List<Building_Bed> beds = new List<Building_Bed>();
                    foreach (Building_Bed b in IntakeSystem.FreeBedsInOrder(map, minimum).Where(b => IntakeSystem.WingOf(b) == minimum && b.OwnersForReading.Count == 0 && b.GetRoom() is { } room && !beds.Any(x => x.GetRoom() == room)))
                        if (beds.Count < 2) beds.Add(b);
                    if (beds.Count < 2)
                    {
                        RPLog.Message($"[RimPrison:test] handover: no pair of quiet inmates in the minimum wing, and no two free beds of it in cells of their own to make one; not run");
                        return;
                    }
                    foreach (Building_Bed b in beds) Throwaway(map, r, minimum).ownership.ClaimBedIfNonMedical(b);
                }
                // The small prison's other two inmate seeds come from the same quiet pool (Handover.Sow): the debt takes two of the medium wing, or two of the minimum wing when the medium one has fewer than two quiet,
                // and the complaint one of what is left of both. A day of events left stability20-s20a's medium wing with none quiet and its minimum wing with four: the feud and the debt took all four and nobody was left
                // to file the complaint. So the minimum wing is given made-up quiet inmates (no bed, so never the feud's pair) up to five: two for the feud, two for the debt at worst, one for the complaint.
                bool FitForASeed(Pawn p) => p.Spawned && p.Inmate() != null && InmateUtility.IsActiveInmate(p) && Handover.Sowable(p) && ComplaintSystem.OpenBy(g, p.thingIDNumber) == null;
                int Quiet(SecurityLevelDef level) => map.mapPawns.PrisonersOfColonySpawned.Count(p => p.Inmate()?.security == level && FitForASeed(p));
                int quietMinimum = Quiet(minimum), quietMedium = Quiet(medium), madeUp = 0;
                for (int tries = 0; Quiet(minimum) < 5 && tries < 10; tries++)
                    if (FitForASeed(Throwaway(map, r, minimum))) madeUp++;
                RPLog.Message($"[RimPrison:test] handover: quiet inmates fit for a seed - minimum {quietMinimum}, medium {quietMedium}; {madeUp} made up for the minimum wing");
                Action undo = HandoverCapture(map, g);
                r.Add(undo);
                // The live prison may have a feud arc running (the events plan leaves one: "let them settle it"): every pair of the medium wing is put in one, so the sowing's feud must go where no arc runs.
                // (Made by hand: the arc's own start is limited to one running at a time. The capture above puts the arcs and their counter back.)
                List<Pawn> inArcs = able.Where(p => p.Inmate().security == medium).OrderBy(p => p.thingIDNumber).ToList();
                for (int i = 0; i + 1 < inArcs.Count; i += 2)
                {
                    g.story.arcs.Add(new StoryArc { def = RPRelationDefOf.RP_Arc_Feud, id = g.story.nextArcId++, startTick = Find.TickManager.TicksGame, nextTick = Find.TickManager.TicksGame + GenDate.TicksPerDay });
                    g.story.arcs.Last().pawns.AddRange(new[] { inArcs[i], inArcs[i + 1] });
                }
                var before = new HashSet<string>(HandoverShown(map, new Handover.Seeds(), wholeScreen: true));
                Handover.Seeds s = Handover.Sow(map, Facility.Inherited);
                foreach (string what in HandoverProblems(map, s, expectBig: false, fresh: false, already: before)) HandoverFail("sown on the live prison", what);
                if (s.dealer != null || s.ring != null || s.tunneller != null || s.gang != null) HandoverFail("sown on the live prison", "the small prison was given a dealer, a ring, a tunnel or a gang: those are the penitentiary's");
            });
        }

        /// <summary>
        /// Captures what a sowing changes on the live prison and returns what puts it back: each inmate's gang, reputations, cash, job, goal (and how it stood), tunnel, friends and enmities, the stash and the pockets, the staff's suspicion and payroll,
        /// the game's arcs, debts and complaints (and their counters), the turf and the flag. (The check's rollback puts back the chronicle, the letters, the statistics and the memories.)
        /// </summary>
        private static Action HandoverCapture(Map map, PrisonGame g)
        {
            PrisonMap pm = PrisonMap.Of(map);
            var undo = new List<Action>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColony.Where(x => x.Inmate() != null).ToList())
            {
                CompInmate c = p.Inmate();
                (GangDef gang, bool gangKnown, bool tattoo, bool leader, byte rank) = (c.gang, c.gangKnown, c.gangTattoo, c.gangLeader, c.gangRank);
                (int cash, InmateJobDef job, int dealerId, float tunnel, bool tunnelKnown, bool searchPending, bool suspected, InmateWant want) = (c.cash, c.job, c.lastDealerId, c.tunnelProgress, c.tunnelKnown, c.cellSearchPending, c.suspected, c.want);
                List<ReputationDef> reps = c.reputations.ToList();
                List<Thing> stash = c.stash.ToList(), pockets = p.inventory?.innerContainer.ToList() ?? new List<Thing>();
                List<Pawn> enemies = p.relations?.DirectRelations.Where(x => x.def == RPRelationDefOf.RP_Enemy).Select(x => x.otherPawn).ToList() ?? new List<Pawn>();
                // (An enmity ends a friendship, and a new goal sets the old one aside: both are put back as they stood.)
                List<Pawn> friends = p.relations?.DirectRelations.Where(x => x.def == RPRelationDefOf.RP_Friend).Select(x => x.otherPawn).ToList() ?? new List<Pawn>();
                (WantStatus wantStatus, int wantResolved) = (want?.status ?? default, want?.resolvedTick ?? -1);
                undo.Add(() =>
                {
                    (c.gang, c.gangKnown, c.gangTattoo, c.gangLeader, c.gangRank) = (gang, gangKnown, tattoo, leader, rank);
                    (c.cash, c.job, c.lastDealerId, c.tunnelProgress, c.tunnelKnown, c.cellSearchPending, c.suspected, c.want) = (cash, job, dealerId, tunnel, tunnelKnown, searchPending, suspected, want);
                    if (want != null) (want.status, want.resolvedTick) = (wantStatus, wantResolved);
                    c.reputations.Clear();
                    c.reputations.AddRange(reps);
                    foreach (Thing t in c.stash.ToList())
                        if (!stash.Contains(t))
                        {
                            c.stash.Remove(t);
                            t.Destroy();
                        }
                    if (p.inventory != null)
                        foreach (Thing t in p.inventory.innerContainer.ToList())
                            if (!pockets.Contains(t)) t.Destroy();
                    if (p.relations != null)
                        foreach (DirectPawnRelation rel in p.relations.DirectRelations.Where(x => x.def == RPRelationDefOf.RP_Enemy && !enemies.Contains(x.otherPawn)).ToList())
                            p.relations.RemoveDirectRelation(RPRelationDefOf.RP_Enemy, rel.otherPawn);
                    if (p.relations != null)
                        foreach (Pawn f in friends)
                            if (f != null && !p.relations.DirectRelationExists(RPRelationDefOf.RP_Friend, f)) p.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, f);
                });
            }
            foreach (Pawn p in StaffCommute.AllStaff(map))
            {
                CompStaff sc = p.GetComp<CompStaff>();
                (bool corrupt, Pawn patron, GangDef patronGang, float integrity, float suspicion, bool iaOpen, List<string> evidence) = (sc.corrupt, sc.patron, sc.patronGang, sc.integrity, sc.suspicion, sc.iaOpen, sc.evidence.ToList());
                undo.Add(() =>
                {
                    (sc.corrupt, sc.patron, sc.patronGang, sc.integrity, sc.suspicion, sc.iaOpen) = (corrupt, patron, patronGang, integrity, suspicion, iaOpen);
                    sc.evidence.Clear();
                    sc.evidence.AddRange(evidence);
                });
            }
            (List<StoryArc> arcs, int arcId, List<InmateDebt> debts, int debtId, List<Complaint> complaints, int complaintId, bool handover, Handover.Seeds last) =
                (g.story.arcs.ToList(), g.story.nextArcId, g.story.debts.ToList(), g.story.nextDebtId, g.story.complaints.ToList(), g.story.nextComplaintId, g.handover, Handover.Last);
            List<TurfClaim> claims = pm.turf.claims.ToList();
            return () =>
            {
                for (int i = undo.Count - 1; i >= 0; i--) undo[i]();
                g.story.arcs.Clear();
                g.story.arcs.AddRange(arcs);
                g.story.nextArcId = arcId;
                g.story.debts.Clear();
                g.story.debts.AddRange(debts);
                g.story.nextDebtId = debtId;
                g.story.complaints.Clear();
                g.story.complaints.AddRange(complaints);
                g.story.nextComplaintId = complaintId;
                g.handover = handover;
                Handover.Last = last;
                pm.turf.Replace(claims);
            };
        }

        // ---- the saveload plan --------------------------------------------------------------------------------------------------------------------------------

        private static bool handoverWas;
        private static List<ChronicleEntry> handoverSeededEntries;
        private static int handoverDiggerId = -1, handoverFeudAId = -1, handoverFeudBId = -1, handoverFileId = -1;
        private static bool handoverFeudAdded, handoverFeudWereFriends;
        private static float handoverFileWas;
        private static List<string> handoverFileEvidenceWas;
        private static string handoverSeeded;

        /// <summary>Everything the handover keeps that a save must keep, as one string: the flag, the old chronicle entries, the tunnels dug, the enmities and the staff's files (the rings, debts, complaints and turf are in the snapshot's own lines).</summary>
        private static string HandoverDescribe(PrisonGame g, Map map)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(g.handover).Append('/').Append(string.Join(",", g.story.entries.Where(e => e.tick < 0).Select(e => $"{e.tick}:{e.importance}:{e.cat?.defName}:{e.text}"))).Append('/');
            var enemies = new SortedSet<string>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColony.OrderBy(x => x.thingIDNumber))
            {
                CompInmate c = p.Inmate();
                if (c == null) continue;
                if (c.tunnelProgress > 0f || c.tunnelKnown) sb.Append($"tunnel {p.thingIDNumber}:{c.tunnelProgress:F3}:{c.tunnelKnown};");
                if (p.relations != null)
                    foreach (DirectPawnRelation rel in p.relations.DirectRelations)
                        if (rel.def == RPRelationDefOf.RP_Enemy && rel.otherPawn != null) enemies.Add($"{System.Math.Min(p.thingIDNumber, rel.otherPawn.thingIDNumber)}>{System.Math.Max(p.thingIDNumber, rel.otherPawn.thingIDNumber)}");
            }
            sb.Append("enemies ").Append(string.Join(",", enemies)).Append(';');
            foreach (Pawn s in StaffCommute.AllStaff(map).OrderBy(x => x.thingIDNumber))
                if (s.GetComp<CompStaff>() is { } sc && (sc.suspicion > 0f || sc.corrupt)) sb.Append($"staff {s.thingIDNumber}:{sc.suspicion:F1}:{string.Join("+", sc.evidence)}:{sc.corrupt}:{sc.patron?.thingIDNumber}:{sc.integrity:F1};");
            return sb.ToString();
        }

        /// <summary>The flag, two old chronicle entries before everything, a tunnel a third dug under a real inmate, an enmity between two and a suspicion file on a guard: each off its default (put back after the reload).</summary>
        private static void SeedHandoverForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            handoverWas = g.handover;
            g.handover = true;
            handoverSeededEntries = new List<ChronicleEntry>
            {
                new ChronicleEntry { tick = -9 * GenDate.TicksPerDay + 14 * GenDate.TicksPerHour, cat = RPChronicleDefOf.RP_Chr_Decisions, importance = 3, text = "Seeded old milestone, in the old warden's words" },
                new ChronicleEntry { tick = -7 * GenDate.TicksPerDay + 3 * GenDate.TicksPerHour, cat = RPChronicleDefOf.RP_Chr_Security, importance = 2, text = "Seeded old lockdown" },
            };
            g.story.entries.InsertRange(0, handoverSeededEntries);
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate() is { tunnelProgress: 0f }).OrderBy(p => p.thingIDNumber).ToList();
            if (inmates.Count >= 3)
            {
                inmates[0].Inmate().tunnelProgress = Handover.TunnelProgress;
                handoverDiggerId = inmates[0].thingIDNumber;
                (handoverFeudAId, handoverFeudBId) = (inmates[1].thingIDNumber, inmates[2].thingIDNumber);
                handoverFeudAdded = !PrisonRelations.Enemies(inmates[1], inmates[2]);
                handoverFeudWereFriends = PrisonRelations.Friends(inmates[1], inmates[2]);
                PrisonRelations.MakeEnemies(inmates[1], inmates[2]);
            }
            else problems0.Add("saveload: no three inmates to seed a tunnel and an enmity with");
            if (StaffCommute.AllStaff(map).FirstOrDefault(p => GuardUtility.IsGuard(p) && p.GetComp<CompStaff>() is { Protected: false, suspicion: 0f }) is { } guard)
            {
                CompStaff sc = guard.GetComp<CompStaff>();
                (handoverFileId, handoverFileWas, handoverFileEvidenceWas) = (guard.thingIDNumber, sc.suspicion, sc.evidence.ToList());
                CorruptionSystem.AddSuspicion(guard, sc, Handover.FileSuspicion, "RP_Sus_SeenTalking");
            }
            else problems0.Add("saveload: no guard to seed a suspicion file on");
            handoverSeeded = HandoverDescribe(g, map);
            RPLog.Message($"[RimPrison:test] saveload handover: {handoverSeeded.Replace("\n", " ")}");
        }

        /// <summary>The seeded flag, old entries (still first, still before day one), tunnel, enmity and file came back whole; then they go.</summary>
        private static void CheckHandoverAfterReload(Map map)
        {
            if (handoverSeeded == null) return;
            PrisonGame g = PrisonGame.Instance;
            string after = HandoverDescribe(g, map);
            if (after != handoverSeeded) reloadProblems.Add($"the handover's state changed in the round trip: {handoverSeeded} -> {after}");
            else RPLog.Message("[RimPrison:test] saveload handover: intact");
            if (!g.handover) reloadProblems.Add("the handover flag was lost in the round trip");
            List<ChronicleEntry> old = g.story.entries.Where(e => e.tick < 0).ToList();
            if (old.Count != handoverSeededEntries.Count || g.story.entries.FindIndex(e => e.tick >= 0) < old.Count) reloadProblems.Add("the old chronicle entries are not all first and before day one after the round trip");
            // Put back.
            g.story.entries.RemoveAll(e => e.tick < 0 && handoverSeededEntries.Any(x => x.tick == e.tick && x.text == e.text));
            g.handover = handoverWas;
            Pawn PawnById(int id) => map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == id);
            if (PawnById(handoverDiggerId)?.Inmate() is { } dig) dig.tunnelProgress = 0f;
            if (handoverFeudAdded && PawnById(handoverFeudAId) is { } fa && PawnById(handoverFeudBId) is { } fb)
            {
                fa.relations.RemoveDirectRelation(RPRelationDefOf.RP_Enemy, fb);
                // (An enmity ended their friendship: it comes back.)
                if (handoverFeudWereFriends && !PrisonRelations.Friends(fa, fb)) fa.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, fb);
            }
            if (PawnById(handoverFileId)?.GetComp<CompStaff>() is { } sc)
            {
                sc.suspicion = handoverFileWas;
                sc.evidence = handoverFileEvidenceWas ?? new List<string>();
                sc.iaOpen = false;
            }
            handoverSeeded = null;
            handoverDiggerId = handoverFeudAId = handoverFeudBId = handoverFileId = -1;
        }
    }
}
