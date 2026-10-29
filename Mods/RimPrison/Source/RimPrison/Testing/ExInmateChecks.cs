using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Checks for former inmates who come back (<see cref="ExInmates"/>, gap analysis 5, 2.5): who is kept as a world pawn, the application and its letter, the hire with integrity from the record
    /// and what inside makes of it, and the visit as the kept pawn. Each is self-contained, on throwaway pawns, rolled back (<see cref="Rollback"/>), through the game's own paths (the release, the
    /// daily roll with its odds forced, the letter's own choice, the visitation system's spawn and completion). The saveload plan's seed and snapshot are at the end.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckExInmates(Map map)
        {
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns);
            CheckExInmateKept(map);
            CheckExInmateKeptCap(map);
            CheckExInmateApplies(map);
            CheckExInmateHired(map);
            CheckExInmateOldGangLeansOnThem(map);
            CheckExInmateVisit(map);
            Pawn[] left = LeftBehind(map, pawnsBefore).ToArray();
            if (left.Length > 0) problems0.Add($"ex-inmate checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        private static void ExFail(string check, string what) => problems0.Add($"ex-inmates ({check}): {what}");

        private const int ExDay = GenDate.TicksPerDay;

        /// <summary>A record to apply with: a clean one with a program.</summary>
        private static ExInmatePast ExCleanPast() => new ExInmatePast { crime = "Zexcheck", daysServed = 12f, troubles = 0, programs = 1 };

        /// <summary>
        /// A pawn a check is done with, completely: destroyed, no world pawn, discarded (RimWorld passes a destroyed pawn to the world pawns unless it is discarded with it, so a rollback that only destroys leaves it there).
        /// </summary>
        private static void ExDiscard(Pawn p)
        {
            if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
            Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
            if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
            if (!p.Discarded) Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.Discard);
        }

        /// <summary>
        /// A former inmate in the register as the game keeps one: a made-up pawn (a world pawn, forcefully kept) with a record, released <paramref name="daysAgo"/> days ago. <paramref name="able"/>:
        /// a pawn who can do that post's work (up to twenty are made; each one turned down is discarded at once and added to <paramref name="rejected"/> if given, the last is kept whatever it can do).
        /// <paramref name="canDo"/> is the capability test (the game's own if null).
        /// </summary>
        private static FormerInmate ExFormer(Map map, Rollback r, float daysAgo, ExInmatePast past, StaffRoleDef able = null, Func<Pawn, StaffRoleDef, bool> canDo = null, List<Pawn> rejected = null)
        {
            canDo ??= ExInmates.CanDo;
            Pawn p = null;
            for (int i = 0; i < 20; i++)
            {
                p = Throwaway(map, r, spawn: false);
                if (able == null || canDo(p, able) || i == 19) break;
                ExDiscard(p);
                rejected?.Add(p);
            }
            NoMentalStateTraits(p);
            Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever);
            r.Add(() =>
            {
                Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
                if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
            });
            var e = new FormerInmate { name = p.LabelShort, number = 98000 + (int)(daysAgo * 10f), releasedTick = Find.TickManager.TicksGame - (int)(daysAgo * ExDay), pawn = p, past = past, paroled = false };
            PrisonGame.Instance.story.formerInmates.Add(e);
            return e;
        }

        /// <summary>A member of staff who was hired from the register goes with the rollback: off the payroll and the map.</summary>
        private static void ExStaffGoes(Rollback r, Pawn p)
        {
            PrisonGame g = PrisonGame.Instance;
            r.Add(() =>
            {
                g.home.Remove(p);
                if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
                StaffCareers.InvalidateLeads();
                StaffCommute.StaffVersion++;
            });
        }

        /// <summary>Open posts for any number of inmates: one post per inmate, so a prison with fewer cooks than inmates has a vacancy.</summary>
        private static void ExPostsFor(Rollback r, int perPost)
        {
            int perPost0 = ExInmates.InmatesPerPost;
            r.Add(() => ExInmates.InmatesPerPost = perPost0);
            ExInmates.InmatesPerPost = perPost;
        }

        // ---- kept ------------------------------------------------------------------------------------------------

        private static void CheckExInmateKept(Map map)
        {
            Guarded("ex-inmates are kept as world pawns", map, r =>
            {
                const string check = "kept";
                PrisonGame g = PrisonGame.Instance;
                g.story.formerInmates.Clear();
                ProgramDef program = DefDatabase<ProgramDef>.AllDefsListForReading.First(d => !d.stage);

                // The real release, of three throwaways (innocent: none of them reoffends, which would keep them for the return instead): one with a passed program, one with a friend still inside, one with nothing to come back for.
                Pawn Leaves(Action<Pawn, CompInmate> set)
                {
                    Pawn p = Throwaway(map, r);
                    CompInmate c = p.Inmate();
                    c.innocent = true;
                    c.programsPassed = 0;
                    set(p, c);
                    r.Add(() => Find.WorldPawns.ForcefullyKeptPawns.Remove(p));
                    ReleaseSystem.Release(g, p, record: false);
                    return p;
                }

                Pawn trained = Leaves((p, c) =>
                {
                    c.programsPassed = 1;
                    c.passedPrograms.Add(program);
                });
                FormerInmate trainedEntry = g.story.formerInmates.LastOrDefault();
                if (trainedEntry?.pawn != trained || !Find.WorldPawns.ForcefullyKeptPawns.Contains(trained) || trainedEntry.past is not { programs: 1 })
                    ExFail(check, $"a release with a passed program did not keep the pawn (entry holds them {trainedEntry?.pawn == trained}, forcefully kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(trained)}, record programs {trainedEntry?.past?.programs})");

                Pawn friend = Throwaway(map, r);
                Pawn befriended = Leaves((p, c) => p.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, friend));
                FormerInmate befriendedEntry = g.story.formerInmates.LastOrDefault();
                if (befriendedEntry?.pawn != befriended || !Find.WorldPawns.ForcefullyKeptPawns.Contains(befriended))
                    ExFail(check, "a release with a friend still inside did not keep the pawn");

                Pawn nothing = Leaves((p, c) => { });
                FormerInmate nothingEntry = g.story.formerInmates.LastOrDefault();
                if (nothingEntry?.pawn != null || Find.WorldPawns.ForcefullyKeptPawns.Contains(nothing) || nothingEntry?.past == null)
                    ExFail(check, $"a release with nothing to come back for kept the pawn (entry holds one {nothingEntry?.pawn != null}, forcefully kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(nothing)}) or took no record");

                // The story done: the daily pass lets them go (no letter pending, the window shut, no friend inside).
                trainedEntry.releasedTick -= (ExInmates.MaxApplyDays + 1) * ExDay;
                ExInmates.Daily(g, map);
                if (trainedEntry.pawn != null || Find.WorldPawns.ForcefullyKeptPawns.Contains(trained))
                    ExFail(check, "a former inmate whose window has shut with no friend inside was still kept");
                if (befriendedEntry.pawn != befriended)
                    ExFail(check, "a former inmate with a friend still inside was let go");
                RPLog.Message("[RimPrison:test] ex-inmates kept: program and friend keep the pawn, nothing does not, the story done lets them go");
            });
        }

        private static void CheckExInmateKeptCap(Map map)
        {
            Guarded("ex-inmates keep a cap", map, r =>
            {
                const string check = "cap";
                PrisonGame g = PrisonGame.Instance;
                g.story.formerInmates.Clear();
                // Twenty kept (the oldest released first in the list, as the register is), and the 21st walks out.
                var kept = new List<FormerInmate>();
                for (int i = 0; i < ExInmates.MaxKept; i++) kept.Add(ExFormer(map, r, 40 - i, ExCleanPast()));
                Pawn oldest = kept[0].pawn;
                Pawn newest = Throwaway(map, r, spawn: false);
                CompInmate nc = newest.Inmate();
                nc.innocent = true;
                nc.programsPassed = 1;
                r.Add(() => Find.WorldPawns.ForcefullyKeptPawns.Remove(newest));
                FormerInmates.Notify_Released(g, newest, nc, paroled: false, reoffends: false);
                List<FormerInmate> now = g.story.formerInmates.Where(x => x.pawn != null).ToList();
                if (now.Count != ExInmates.MaxKept || kept[0].pawn != null || Find.WorldPawns.ForcefullyKeptPawns.Contains(oldest)
                    || kept.Skip(1).Any(x => x.pawn == null) || g.story.formerInmates.Last().pawn != newest)
                    ExFail(check, $"past the cap: {now.Count} kept (of {ExInmates.MaxKept}), the oldest still holds a pawn {kept[0].pawn != null} or is still kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(oldest)}, a newer one lost theirs {kept.Skip(1).Any(x => x.pawn == null)}, the newest kept {g.story.formerInmates.Last().pawn == newest}");
                RPLog.Message($"[RimPrison:test] ex-inmates cap: {now.Count} kept, the oldest released let go");
            });
        }

        // ---- the application ------------------------------------------------------------------------------------

        private static void CheckExInmateApplies(Map map)
        {
            Guarded("ex-inmates apply for work", map, r =>
            {
                const string check = "application";
                PrisonGame g = PrisonGame.Instance;
                g.story.formerInmates.Clear();
                if (StoryLetters.AnyOpen("RP_Decision_ExInmate")) { ExFail(check, "the check's own premise: an application is already open"); return; }
                ExPostsFor(r, 1);
                if (!ExInmates.Vacancies(map).Contains(RPStaffRoleDefOf.RP_Cook)) { ExFail(check, "the check's own premise: no vacancy for a cook"); return; }
                ExInmatePast past = ExCleanPast();
                past.trade = DefDatabase<ProgramDef>.AllDefsListForReading.FirstOrDefault(d => d.trade);
                past.gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                past.gangKnown = true;
                past.troubles = 2;

                // Within three days of release: no letter, however the roll goes.
                FormerInmate fresh = ExFormer(map, r, 2f, past, RPStaffRoleDefOf.RP_Cook);
                int sent = ExInmates.ApplicationRoll(g, map, chance: 1f);
                if (sent != 0 || fresh.applied || r.NewLetters.Any(l => l is ChoiceLetter_PrisonDecision d && d.Is("RP_Decision_ExInmate")))
                    ExFail(check, $"someone released two days ago applied (letters {sent}, applied {fresh.applied})");

                // Four days on, with a vacancy: the letter, with their record.
                fresh.releasedTick = Find.TickManager.TicksGame - 4 * ExDay;
                // (No vacancy first: a post for every inmate that is filled, with a cook, a workman and a gardener on the staff.)
                ExPostsFor(r, 1000000);
                foreach (StaffRoleDef role in ExInmates.Roles) ThrowawayStaff(map, r, role);
                if (ExInmates.Vacancies(map).Count != 0) { ExFail(check, "the check's own premise: a post is still open with one of each on the staff"); return; }
                if (ExInmates.ApplicationRoll(g, map, chance: 1f) != 0 || fresh.applied) ExFail(check, "they applied with no vacancy to hire into");
                ExInmates.InmatesPerPost = 1;
                // (The three throwaway staff above fill one post each, but the inmates are as many as the posts now: a vacancy again.)
                if (!ExInmates.Vacancies(map).Contains(RPStaffRoleDefOf.RP_Cook)) { ExFail(check, "the check's own premise: no vacancy again"); return; }
                if (ExInmates.ApplicationRoll(g, map, chance: 0f) != 0 || fresh.applied) ExFail(check, "they applied at odds of nothing");
                if (ExInmates.ApplicationRoll(g, map, chance: 1f) != 1 || !fresh.applied) { ExFail(check, "no letter when the roll was forced with a vacancy"); return; }
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_ExInmate"));
                if (letter == null) { ExFail(check, "the roll sent no decision letter"); return; }
                string text = letter.Text.Resolve();
                var wanted = new List<string> { fresh.name, past.crime, "12", "RP_ExInmate_ConductTroubles".Translate(2).Resolve(), "RP_ExInmate_Programs".Translate(1).Resolve(), "RP_ExInmate_Gang".Translate(past.gang.label).Resolve() };
                if (past.trade != null) wanted.Add("RP_ExInmate_Trade".Translate(past.trade.label).Resolve());
                string[] missing = wanted.Where(w => !text.Contains(w)).ToArray();
                if (missing.Length > 0) ExFail(check, $"the letter does not say {string.Join(" | ", missing)}: {text}");
                if (letter.subjectId != fresh.pawn.thingIDNumber) ExFail(check, "the letter is not about the applicant");
                IReadOnlyList<string> options = letter.decision.Worker.Options(letter);
                foreach (StaffRoleDef role in ExInmates.Roles)
                    if (role.guardDuties || role.warden || role.workTypes.Contains(WorkTypeDefOf.Warden)) ExFail(check, $"{role.defName} (a guard's, warden's or keys post) is offered to a former inmate");
                if (options.Count != ExInmates.Roles.Length + 1) ExFail(check, $"the letter offers {options.Count} choices for {ExInmates.Roles.Length} posts and a no");
                if (!StoryLetters.AnyOpen("RP_Decision_ExInmate")) ExFail(check, "the open letter is not found among the open decisions");

                // Declined: never asked again for that person.
                Pawn applicant = fresh.pawn;
                PrisonDecisions.Choose(letter, ExInmates.Roles.Length);
                Find.LetterStack.RemoveLetter(letter);
                if (!fresh.declined || fresh.hired) ExFail(check, "turning them down did not close it");
                // (Held and unasked again by the flag alone: the pawn put back, the letter's own mark cleared.)
                fresh.pawn = applicant;
                fresh.applied = false;
                fresh.releasedTick = Find.TickManager.TicksGame - 5 * ExDay;
                if (ExInmates.ApplicationRoll(g, map, chance: 1f) != 0) ExFail(check, "someone turned down was asked about again");

                // An unanswered letter is a no as well.
                // (This one had a gang the warden never knew of: the letter does not name it.)
                ExInmatePast hidden = ExCleanPast();
                hidden.gang = past.gang;
                hidden.gangKnown = false;
                FormerInmate silent = ExFormer(map, r, 6f, hidden, RPStaffRoleDefOf.RP_Cook);
                if (ExInmates.ApplicationRoll(g, map, chance: 1f) != 1 || !silent.applied) { ExFail(check, "the second applicant sent no letter"); return; }
                ChoiceLetter_PrisonDecision second = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_ExInmate") && l.subjectId == silent.pawn.thingIDNumber);
                if (second != null && second.Text.Resolve().Contains("RP_ExInmate_Gang".Translate(past.gang.label).Resolve())) ExFail(check, "the letter names a gang the warden never knew of");
                second?.Lapse();
                if (second != null) Find.LetterStack.RemoveLetter(second);
                if (second == null || !silent.declined || silent.hired) ExFail(check, "an application left unanswered was not a no");
                RPLog.Message("[RimPrison:test] ex-inmates application: none inside three days or without a vacancy, the letter has the record, a no is for good");
            });
        }

        // ---- the hire --------------------------------------------------------------------------------------------

        /// <summary>The applicant's letter, answered with the first post (a cook), through the letter's own choice; the hired pawn's integrity is the roll a fresh hire has plus the record's shift.</summary>
        private static Pawn ExHire(Map map, Rollback r, FormerInmate e, int seed, out float fresh)
        {
            PrisonGame g = PrisonGame.Instance;
            int sent = ExInmates.ApplicationRoll(g, map, chance: 1f);
            ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().LastOrDefault(l => l.Is("RP_Decision_ExInmate") && l.subjectId == e.pawn?.thingIDNumber);
            fresh = 0f;
            if (sent != 1 || letter == null)
            {
                ExFail("hire", "the applicant sent no letter");
                return null;
            }
            Pawn p = e.pawn;
            if (letter.decision.Worker.Disabled(letter, 0) is { } why)
            {
                ExFail("hire", "hiring them as a cook is not allowed: " + why);
                return null;
            }
            Rand.PushState(seed);
            fresh = CorruptionSystem.RollIntegrity(p);
            Rand.PopState();
            letter.answered = true;
            Rand.PushState(seed);
            try
            {
                PrisonDecisions.Choose(letter, 0);
            }
            finally
            {
                Rand.PopState();
            }
            Find.LetterStack.RemoveLetter(letter);
            ExStaffGoes(r, p);
            return p;
        }

        private static void CheckExInmateHired(Map map)
        {
            Guarded("ex-inmates are hired with integrity from the record", map, r =>
            {
                const string check = "hire";
                PrisonGame g = PrisonGame.Instance;
                g.story.formerInmates.Clear();
                if (StoryLetters.AnyOpen("RP_Decision_ExInmate")) { ExFail(check, "the check's own premise: an application is already open"); return; }
                ExPostsFor(r, 1);
                StaffRoleDef cook = RPStaffRoleDefOf.RP_Cook;
                if (!ExInmates.Vacancies(map).Contains(cook)) { ExFail(check, "the check's own premise: no vacancy for a cook"); return; }
                g.ledger.funds = Math.Max(g.ledger.funds, 100000);
                Pawn friend = Throwaway(map, r);
                var memory = RPExInmateDefOf.RP_FriendHired;

                // A clean record and a program: higher than a fresh hire's.
                ExInmatePast clean = ExCleanPast();
                FormerInmate good = ExFormer(map, r, 5f, clean, cook);
                good.pawn.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, friend);
                int funds0 = g.ledger.funds;
                Pawn hired = ExHire(map, r, good, 4242, out float fresh);
                if (hired == null) return;
                CompStaff sc = hired.GetComp<CompStaff>();
                float expected = ExInmates.StartingIntegrity(fresh, clean);
                if (!good.hired || good.pawn != null || sc?.role != cook || !hired.Spawned || hired.Faction != Faction.OfPlayer || !hired.IsColonist)
                    ExFail(check, $"hired but not on the staff as a cook (entry hired {good.hired}, holds a pawn {good.pawn != null}, role {sc?.role?.defName}, spawned {hired.Spawned}, faction {hired.Faction?.Name})");
                if (sc == null || sc.role.guardDuties || sc.role.workTypes.Contains(WorkTypeDefOf.Warden)) ExFail(check, "a former inmate was put in a security role");
                if (sc != null && (!Mathf.Approximately(sc.integrity, expected) || ExInmates.IntegrityShift(clean) <= 0f || (sc.integrity <= fresh && fresh + ExInmates.IntegrityShift(clean) < 100f)))
                    ExFail(check, $"a clean record and a program gave integrity {sc.integrity:F1}; a fresh hire's roll is {fresh:F1}, the record's shift {ExInmates.IntegrityShift(clean):F1}, expected {expected:F1}");
                if (Find.WorldPawns.ForcefullyKeptPawns.Contains(hired) || g.ledger.funds != funds0 - cook.hireCost)
                    ExFail(check, $"after the hire the pawn is still kept for the return ({Find.WorldPawns.ForcefullyKeptPawns.Contains(hired)}) or the fee was not paid (funds {funds0} -> {g.ledger.funds}, fee {cook.hireCost})");
                string line = "RP_Chr_HiredFormer".Translate(hired.LabelShort, cook.label, "12").Resolve();
                if (!r.NewEntries.Any(en => en.text == line)) ExFail(check, $"no chronicle line \"{line}\"");
                if (r.NewEntries.Count(en => en.text == "RP_Chr_Hired".Translate(hired.LabelShort, cook.label).Resolve()) != 0) ExFail(check, "the hire was written up twice (the plain one as well)");
                if (friend.needs.mood.thoughts.memories.GetFirstMemoryOfDef(memory) == null) ExFail(check, "a friend inside got no memory of it");
                // (The record as the file shows it: about twelve days and the program; not an exact float, and the text with its leading line break taken off for the message.)
                if (sc?.formerPast is not { } fp || Math.Abs(fp.daysServed - 12f) >= 0.5f || fp.programs < 1 || !sc.StatusLines().Contains(("\n" + "RP_StaffFormerInmate".Translate(fp.daysServed.ToString("0"))).ToString().Trim()))
                    ExFail(check, $"the staff file does not say former inmate, served 12 days: {sc?.StatusLines()?.Replace("\n", " | ")} (the record: {sc?.formerPast?.Describe() ?? "none"})");

                // A gang past: lower.
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                if (gang == null) { ExFail(check, "the check's own premise: no gang def"); return; }
                ExInmatePast dirty = new ExInmatePast { crime = "Zexcheck", daysServed = 40f, troubles = 0, programs = 0, gang = gang, gangRank = 1, gangKnown = false };
                FormerInmate bad = ExFormer(map, r, 5f, dirty, cook);
                Pawn hiredBad = ExHire(map, r, bad, 777, out float freshBad);
                if (hiredBad == null) return;
                CompStaff bc = hiredBad.GetComp<CompStaff>();
                if (bc == null || bc.role != cook || !bad.hired || !Mathf.Approximately(bc.integrity, ExInmates.StartingIntegrity(freshBad, dirty)) || ExInmates.IntegrityShift(dirty) >= 0f || bc.integrity >= freshBad)
                    ExFail(check, $"a gang past gave integrity {bc?.integrity:F1}; a fresh hire's roll is {freshBad:F1}, the record's shift {ExInmates.IntegrityShift(dirty):F1}");
                if (ExInmates.IntegrityShift(clean) <= ExInmates.IntegrityShift(dirty)) ExFail(check, "the clean record does not shift integrity above the gang past");
                // (A hired former inmate who is not asked again.)
                if (ExInmates.ApplicationRoll(g, map, chance: 1f) != 0) ExFail(check, "a hired former inmate applied again");
                RPLog.Message($"[RimPrison:test] ex-inmates hire: clean record {sc?.integrity:F1} against a fresh roll {fresh:F1}, gang past {bc?.integrity:F1} against {freshBad:F1}");
            });
        }

        private static void CheckExInmateOldGangLeansOnThem(Map map)
        {
            Guarded("ex-inmates' old gang leans on them", map, r =>
            {
                const string check = "old gang";
                PrisonGame g = PrisonGame.Instance;
                g.story.formerInmates.Clear();
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
                if (gang == null) { ExFail(check, "the check's own premise: no gang def"); return; }
                StaffRoleDef cook = RPStaffRoleDefOf.RP_Cook;
                Pawn staff = ThrowawayStaff(map, r, cook);
                CompStaff sc = staff.GetComp<CompStaff>();
                sc.formerPast = new ExInmatePast { crime = "Zexcheck", daysServed = 30f, gang = gang, gangRank = 0 };
                Pawn mate = Throwaway(map, r);
                (mate.Inmate().gang, mate.Inmate().gangLeader) = (gang, false);
                float odds0 = CorruptionSystem.ApproachOdds(staff, sc, 0f), odds1 = CorruptionSystem.ApproachOdds(staff, sc, ExInmates.LeanPressure);
                if (!Mathf.Approximately(odds1 - odds0, ExInmates.LeanPressure)) ExFail(check, $"the pressure does not weight the odds up ({odds0:F2} -> {odds1:F2})");
                // Nobody leans at odds of nothing, and nobody of another past is leaned on.
                sc.integrity = 5f;
                sc.lastApproachTick = -999999;
                if (ExInmates.Pressure(g, map, chance: 0f) != 0 || sc.corrupt) ExFail(check, "the old gang leaned at odds of nothing");
                int approached0 = g.stats.staffApproached;
                // Weak integrity under pressure: they say yes (on the old gang's payroll, hidden as corruption is).
                if (ExInmates.Pressure(g, map, chance: 1f) < 1 || !sc.corrupt || sc.patron?.Inmate()?.gang != gang || sc.patronGang != gang || g.stats.staffApproached != approached0 + 1)
                    ExFail(check, $"weak integrity leaned on by the old gang: corrupt {sc.corrupt}, patron {sc.patron?.LabelShort} of {sc.patron?.Inmate()?.gang?.defName}, approaches {approached0} -> {g.stats.staffApproached}");
                // At home (off the map): the lean is not made (it threw before: the offer looks for staff near them, and a pawn at home has no map); the review's finding.
                {
                    Pawn home = ThrowawayStaff(map, r, cook, atHome: true);
                    CompStaff hc = home.GetComp<CompStaff>();
                    (hc.formerPast, hc.integrity, hc.lastApproachTick) = (new ExInmatePast { crime = "Zexcheck", daysServed = 30f, gang = gang, gangRank = 0 }, 5f, -999999);
                    (sc.lastApproachTick, sc.integrity) = (Find.TickManager.TicksGame, 100f); // (the one on site is not asked now)
                    int n = -1;
                    try { n = ExInmates.Pressure(g, map, chance: 1f); }
                    catch (System.Exception ex) { ExFail(check, $"leaning on a former gang member at home threw: {ex.GetType().Name} {ex.Message}"); }
                    if (home.Spawned) ExFail(check, "the check's own premise: the member of staff at home is on the map");
                    else if (n != 0 || hc.corrupt || hc.lastApproachTick != -999999) ExFail(check, $"a former gang member at home was leaned on (leaned {n}, corrupt {hc.corrupt}, last approach {hc.lastApproachTick})");
                    hc.formerPast = null;
                }
                // Strong integrity: they say no, and it is not asked again at once.
                (sc.corrupt, sc.patron, sc.patronGang, sc.integrity, sc.lastApproachTick) = (false, null, null, 100f, -999999);
                if (ExInmates.Pressure(g, map, chance: 1f) < 1 || sc.corrupt || sc.lastApproachTick != Find.TickManager.TicksGame)
                    ExFail(check, $"strong integrity leaned on by the old gang: corrupt {sc.corrupt}, last approach {sc.lastApproachTick}");
                if (ExInmates.Pressure(g, map, chance: 1f) != 0) ExFail(check, "the old gang leaned twice within two days");
                // No gang past, nothing to lean on.
                sc.formerPast.gang = null;
                sc.lastApproachTick = -999999;
                if (ExInmates.Pressure(g, map, chance: 1f) != 0) ExFail(check, "someone with no gang past was leaned on by a gang");
                RPLog.Message("[RimPrison:test] ex-inmates old gang: weighted odds, yes at weak integrity, no at strong, not twice in two days");
            });
        }

        // ---- the visit -------------------------------------------------------------------------------------------

        private static void CheckExInmateVisit(Map map)
        {
            Guarded("ex-inmates visit as themselves", map, r =>
            {
                const string check = "visit";
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                if (!pm.zones.Any(RPVisitDefOf.RP_Visitation)) { ExFail(check, "the check's own premise: no visitation zone"); return; }
                g.story.formerInmates.Clear();
                Pawn friend = Throwaway(map, r);
                CompInmate fc = friend.Inmate();
                (fc.forcedActivity, fc.visitor, fc.visitBanUntilTick) = (null, null, -1);
                SecurityLevelDef sec = fc.security;
                bool hadPolicy = g.visitPolicies.TryGetValue(sec, out VisitPolicy policy0);
                VisitorSearchPolicy search0 = g.visitorSearch;
                r.Add(() =>
                {
                    if (hadPolicy) g.visitPolicies[sec] = policy0;
                    else g.visitPolicies.Remove(sec);
                    g.visitorSearch = search0;
                });
                g.visitPolicies[sec] = VisitPolicy.Contact;
                g.visitorSearch = VisitorSearchPolicy.PatDown;
                int hour = Enumerable.Range(9, 10).FirstOrDefault(h => !g.ActivityAt(fc, (h + 1) % 24).lockup);
                if (hour == 0) { ExFail(check, "the check's own premise: the friend is locked up at every visiting hour"); return; }
                int active = 0, boothsInUse = 0;

                // A former inmate with nobody inside to see does not come; one released an hour ago does not either.
                ExInmatePast past = ExCleanPast();
                past.dealer = true;
                FormerInmate lonely = ExFormer(map, r, 10f, ExCleanPast());
                ExInmates.HourlyVisits(map, g, hour, 6, 0, ref active, ref boothsInUse, chance: 1f);
                if (active != 0 || fc.visitor != null) ExFail(check, "a former inmate with no friend inside came to visit");
                g.story.formerInmates.Remove(lonely);
                FormerInmate soon = ExFormer(map, r, 0.1f, past);
                soon.pawn.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, friend);
                ExInmates.HourlyVisits(map, g, hour, 6, 0, ref active, ref boothsInUse, chance: 1f);
                if (active != 0 || fc.visitor != null) ExFail(check, "a former inmate came to visit within three days of release");
                g.story.formerInmates.Remove(soon);

                // A former inmate with a friend inside comes, as themselves.
                FormerInmate e = ExFormer(map, r, 10f, past);
                Pawn former = e.pawn;
                former.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, friend);
                int visits0 = g.stats.visits, cash0 = fc.cash;
                ExInmates.HourlyVisits(map, g, hour, 6, 0, ref active, ref boothsInUse, chance: 1f);
                r.Add(() =>
                {
                    if (former.Spawned) former.DeSpawn();
                });
                if (active != 1 || fc.visitor != former || !fc.visitorFormer)
                {
                    ExFail(check, $"the former inmate did not visit their friend (visits started {active}, the visitor is {fc.visitor?.LabelShort}, former {fc.visitorFormer})");
                    return;
                }
                if (!former.Spawned || VisitationSystem.InmateOf(former) != friend || former.Faction != InmateGenerator.OutsideFaction || e.lastVisitTick != Find.TickManager.TicksGame)
                    ExFail(check, $"the visitor is not the kept pawn as a visitor of the friend (spawned {former.Spawned}, came for {VisitationSystem.InmateOf(former)?.LabelShort}, faction {former.Faction?.Name}, last visit {e.lastVisitTick})");
                if (!Find.WorldPawns.ForcefullyKeptPawns.Contains(former) || Find.WorldPawns.Contains(former) || e.pawn != former)
                    ExFail(check, "while visiting the pawn is not kept (as a pawn on the map), or the entry lost them");
                if (!fc.visitorSearchPending) ExFail(check, "the former inmate is not searched like any visitor (a pat-down policy, a contact visit)");
                // Raised chance of contraband for a dealer or someone in a ring, not for anyone else.
                float baseChance = VisitationSystem.SmuggleChance(friend, fc);
                if (baseChance > 0f && (ExInmates.SmuggleChance(baseChance, past) <= baseChance || !Mathf.Approximately(ExInmates.SmuggleChance(baseChance, ExCleanPast()), baseChance)))
                    ExFail(check, $"the chance of contraband for a dealer ({ExInmates.SmuggleChance(baseChance, past):F2}) is not raised over {baseChance:F2}, or is for someone who was no dealer");
                // Not twice in a row (the cooldown), whatever the odds.
                ExInmates.HourlyVisits(map, g, hour, 6, 0, ref active, ref boothsInUse, chance: 1f);
                if (active != 1) ExFail(check, "the same former inmate came again at once");

                // The visit ends the way any does: counted, written up, and the family's money is not theirs to bring.
                fc.visitorContraband = null;
                VisitationSystem.Complete(friend, former);
                string line = "RP_Chr_VisitedByFormer".Translate(friend.LabelShort, former.LabelShort, "12").Resolve();
                if (g.stats.visits != visits0 + 1 || !r.NewEntries.Any(en => en.text == line) || fc.cash != cash0 || fc.visitor != null || fc.visitorFormer)
                    ExFail(check, $"after the visit: visits {visits0} -> {g.stats.visits}, chronicle line {r.NewEntries.Any(en => en.text == line)} (\"{line}\"), cash {cash0} -> {fc.cash}, visitor cleared {fc.visitor == null}");
                // ...and when they leave the map they are back in the world, still kept.
                former.ExitMap(false, Rot4.Invalid);
                if (former.Spawned || !Find.WorldPawns.Contains(former) || !Find.WorldPawns.ForcefullyKeptPawns.Contains(former) || e.pawn != former)
                    ExFail(check, $"after the visit the former inmate is not back in the world and kept (spawned {former.Spawned}, in the world {Find.WorldPawns.Contains(former)}, kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(former)}, entry {e.pawn == former})");

                // A relative's visit works as it did: a made-up visitor of the family, a family line in the chronicle, the visit counted.
                Pawn inmate = Throwaway(map, r);
                CompInmate ic = inmate.Inmate();
                (ic.forcedActivity, ic.visitor) = (null, null);
                while (Relatives.Of(ic).Count(x => x.CanVisit) < 1) Relatives.Of(ic).Add(Relatives.Make());
                Relatives.Recount(ic);
                int visits1 = g.stats.visits;
                if (!VisitationSystem.SpawnVisitor(map, inmate, ic, booth: false) || ic.visitor is not { } relative)
                {
                    ExFail(check, "a relative's visit did not start");
                    return;
                }
                r.Add(() =>
                {
                    if (relative.Corpse is { Destroyed: false } corpse) corpse.Destroy();
                    if (!relative.Destroyed) relative.Destroy();
                });
                if (relative == former || ic.visitorFormer || ic.visitorRelative == null || ic.visitor.Faction != InmateGenerator.OutsideFaction)
                    ExFail(check, $"a relative's visit is not a made-up visitor of the family (visitor {relative.LabelShort}, former {ic.visitorFormer}, relative {ic.visitorRelative})");
                string who = ic.visitorRelative;
                ic.visitorContraband = null;
                VisitationSystem.Complete(inmate, relative);
                if (g.stats.visits != visits1 + 1 || !r.NewEntries.Any(en => en.text == "RP_Chr_Visited".Translate(inmate.LabelShort, who).Resolve()))
                    ExFail(check, $"a relative's visit: visits {visits1} -> {g.stats.visits}, family line in the chronicle {r.NewEntries.Any(en => en.text == "RP_Chr_Visited".Translate(inmate.LabelShort, who).Resolve())}");
                RPLog.Message($"[RimPrison:test] ex-inmates visit: {former.LabelShort} came for {friend.LabelShort} as themselves, was searched like a visitor, went back to the world kept; a relative's visit as before");
            });
        }

        // ---- the saveload plan -----------------------------------------------------------------------------------

        /// <summary>Everything the ex-inmates keep in a save, as one comparable string: the register's new fields (the kept pawn and whether it is still kept, the flags, the last visit, the record) and the past of the hired.</summary>
        private static string ExInmatesDescribe(PrisonGame g, Map map)
        {
            string register = string.Join(",", g.story.formerInmates.Where(e => e.pawn != null || e.past != null || e.applied || e.declined || e.hired)
                .Select(e => $"{e.name}:{e.pawn?.thingIDNumber}:{e.pawn != null && Find.WorldPawns.ForcefullyKeptPawns.Contains(e.pawn)}:{e.applied}{e.declined}{e.hired}:{e.lastVisitTick}:{e.past?.Describe()}"));
            string staff = string.Join(",", StaffCommute.AllStaff(map).Where(s => s.GetComp<CompStaff>()?.formerPast != null).OrderBy(s => s.thingIDNumber)
                .Select(s => $"{s.thingIDNumber}:{s.GetComp<CompStaff>().formerPast.Describe()}:{s.GetComp<CompStaff>().integrity:F1}"));
            return $"{register}/{staff}";
        }

        /// <summary>A kept former inmate who was turned down, with a record that has every field set, and a member of staff with the past of a hired one.</summary>
        private static void SeedExInmatesForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            Pawn kept = InmateGenerator.Generate(InmateUtility.DefaultSecurity, throwawayNumber++);
            Find.WorldPawns.PassToWorld(kept, PawnDiscardDecideMode.KeepForever);
            ProgramDef trade = DefDatabase<ProgramDef>.AllDefsListForReading.FirstOrDefault(d => d.trade);
            GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.FirstOrDefault();
            g.story.formerInmates.Add(new FormerInmate
            {
                name = kept.LabelShort, number = 99997, releasedTick = Find.TickManager.TicksGame - 4 * ExDay, pawn = kept, applied = true, declined = true, lastVisitTick = Find.TickManager.TicksGame - 1234,
                past = new ExInmatePast { crime = "Zseedcrime", daysServed = 33.5f, troubles = 2, programs = 3, trade = trade, gang = gang, gangRank = 1, gangKnown = true, dealer = true, ring = true },
            });
            if (StaffCommute.AllStaff(map).FirstOrDefault(s => s.GetComp<CompStaff>() is { Protected: false } sc && sc.role != null) is { } hired)
            {
                CompStaff hc = hired.GetComp<CompStaff>();
                hc.formerPast = new ExInmatePast { crime = "Zhiredcrime", daysServed = 61f, troubles = 0, programs = 1, trade = trade, gang = gang, gangRank = 0, gangKnown = false, dealer = false, ring = true };
                hc.integrity = 77.5f;
            }
            else problems0.Add("saveload: no member of staff to give a former inmate's past");
        }
    }
}
