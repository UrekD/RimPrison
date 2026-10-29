using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimPrison
{
    public static partial class AutoTest
    {
        private static void HiringFail(string check, string what) => problems0.Add($"hiring ({check}): {what}");

        private static ulong HiringRandState() => (ulong)AccessTools.Property(typeof(Rand), "StateCompressed").GetValue(null);

        private static string HiringWho(Pawn p) => p.Name?.ToStringFull ?? p.LabelShort;

        private static string HiringSkills(Pawn p) => string.Join(",", p.skills.skills.Select(s => $"{s.def.defName}{s.Level}{s.passion}"));

        private static string HiringTraits(Pawn p) => string.Join(",", p.story.traits.allTraits.Select(t => $"{t.def.defName}{t.Degree}"));

        /// <summary>What a hiring check leaves behind it: the pool it ran on (its own, the live one put back after) and the pawns it owns.</summary>
        private sealed class HiringScope
        {
            public ApplicantPool Pool;
            public readonly List<Pawn> Owned = new List<Pawn>();
        }

        private static StoryWatcher_PopAdaptation HiringWatcher => Find.StoryWatcher.watcherPopAdaptation;

        /// <summary>The population watcher as it would be some days into the game, so that a reset shows.</summary>
        private static void HiringAdaptation(float days)
        {
            HiringWatcher.ResetAdaptDays();
            HiringWatcher.Debug_OffsetAdaptDays(days);
        }

        private static List<Pawn> HiringHeld(ApplicantPool pool) => ((ThingOwner<Pawn>)pool.GetDirectlyHeldThings()).InnerListForReading.ToList();

        /// <summary>A pawn of the check's goes completely: off the staff and the map, destroyed, no world pawn, discarded (a destroyed pawn is passed to the world pawns by RimWorld unless it is discarded with it).</summary>
        private static void HiringGone(Pawn p)
        {
            PrisonGame.Instance.home.Remove(p);
            if (p.holdingOwner != null && !p.Spawned) p.holdingOwner.Remove(p);
            ExDiscard(p);
            StaffCareers.InvalidateLeads();
            StaffCommute.StaffVersion++;
        }

        /// <summary>A pawn the check made or hired: it goes through <see cref="HiringGone"/> with the rollback, and the check's end asserts it did.</summary>
        private static Pawn HiringOwn(HiringScope scope, Rollback r, Pawn p)
        {
            if (p == null || scope.Owned.Contains(p)) return p;
            scope.Owned.Add(p);
            r.Add(() => HiringGone(p));
            return p;
        }

        /// <summary>
        /// A pool of the check's own (<paramref name="live"/>: the live one, emptied at the end), the funds for a few hires, and the check's seams put back after. What the check leaves is asserted against what was there before: the pawns on
        /// the map, the world pawns, the pawns held (the live pool's, which a temporary pool never touches) and at home, and that every pawn the check owns is discarded. A pawn spawned that the check did not name goes with it.
        /// </summary>
        private static HiringScope HiringStart(Rollback r, Map map, StaffRoleDef role, bool live = false)
        {
            PrisonGame g = PrisonGame.Instance;
            var scope = new HiringScope();
            ApplicantPool original = g.applicants;
            var onMap0 = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned);
            var world0 = new HashSet<Pawn>(Find.WorldPawns.AllPawnsAliveOrDead);
            var held0 = live ? new HashSet<Pawn>() : new HashSet<Pawn>(HiringHeld(original));
            var home0 = new HashSet<Pawn>(g.home.InnerListForReading);
            float adapt0 = HiringWatcher.AdaptDays;
            scope.Pool = live ? original : new ApplicantPool();
            ApplicantPool pool = scope.Pool;

            // (Registered first, so it runs last: after the check's own undo and the cleanup below.)
            r.Add(() =>
            {
                void Same(string what, HashSet<Pawn> before, IEnumerable<Pawn> now)
                {
                    var after = new HashSet<Pawn>(now);
                    List<Pawn> extra = after.Where(x => !before.Contains(x)).ToList(), missing = before.Where(x => !after.Contains(x)).ToList();
                    if (extra.Count != 0 || missing.Count != 0)
                        HiringFail("cleanup", $"{what} after the check: {extra.Count} more ({string.Join(",", extra.Select(HiringWho))}), {missing.Count} fewer ({string.Join(",", missing.Select(HiringWho))})");
                }
                Same("the pawns on the map", onMap0, map.mapPawns.AllPawnsSpawned);
                Same("the world pawns", world0, Find.WorldPawns.AllPawnsAliveOrDead);
                Same("the pawns held by the applicants' pool", held0, HiringHeld(g.applicants));
                Same("the staff at home", home0, g.home.InnerListForReading);
                if (!live && g.applicants != original) HiringFail("cleanup", "the live applicants' pool was not put back");
                foreach (Pawn p in scope.Owned)
                    if (HiringStillAround(g.applicants, p) is { } why) HiringFail("cleanup", $"{HiringWho(p)}, a pawn of the check's, was left behind: {why}");
            });
            r.Add(() =>
            {
                ApplicantPool.TickOverride = null;
                StaffUtility.CandidateUnfit = null;
                StaffUtility.CandidateChronologicalAge = null;
                Find.WindowStack.WindowOfType<Dialog_Applicants>()?.Close(false);
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(x => !onMap0.Contains(x)).ToList()) HiringGone(p);
                pool.Clear();
                g.applicants = original;
                HiringAdaptation(adapt0);
            });
            g.applicants = pool;
            g.ledger.funds = Math.Max(g.ledger.funds, StaffUtility.AgencyFee(role) * 6 + 1000);
            return scope;
        }

        /// <summary>A pawn the pool let go: not held, not spawned, not a world pawn, discarded.</summary>
        private static string HiringStillAround(ApplicantPool pool, Pawn p) =>
            pool.Holds(p) ? "still held" : p.Spawned ? "spawned" : Find.WorldPawns.Contains(p) ? "a world pawn" : !p.Discarded ? "not discarded" : null;

        /// <summary>A post open besides the guards' (the cook if it is, else the first that is).</summary>
        private static StaffRoleDef HiringOtherPost(StaffRoleDef guard)
        {
            StaffRoleDef cook = RPStaffRoleDefOf.RP_Cook;
            return Unlocks.IsUnlocked(cook) ? cook : StaffUtility.Roles.FirstOrDefault(x => x != guard && Unlocks.IsUnlocked(x));
        }

        /// <summary>
        /// Choosing the people you hire (gap analysis 6, item 3): the warden opens a post and is shown the applicants of the round, the same ones each time; hires one of them (that very pawn, one fee, once); the
        /// next round lets the old ones go and brings new ones; the agency hires at twice the fee; the applicant is rolled for kin as any hire is and a former inmate keeps their identity; nobody fit is an empty pool and no fee.
        /// </summary>
        private static void CheckHiring(Map map)
        {
            StaffRoleDef guard = RPSecurityDefOf.RP_Guard, cook = RPStaffRoleDefOf.RP_Cook;
            if (!Unlocks.IsUnlocked(guard)) { RPLog.Message("[RimPrison:test] hiring: not checked, the guard role is locked"); return; }
            // (Another post to open beside the guards': the cook if it is open, else the first that is.)
            StaffRoleDef other = HiringOtherPost(guard);
            if (other == null) RPLog.Message("[RimPrison:test] hiring: no second post is open, the checks that open two are cut short");

            Guarded("hiring - a round's applicants are made on the click, once, and are the same each time", map, r =>
            {
                const string check = "round";
                PrisonGame g = PrisonGame.Instance;
                HiringScope scope = HiringStart(r, map, guard);
                ApplicantPool pool = scope.Pool;
                if (pool.Count != 0) { HiringFail(check, "the check's own premise: the pool is not empty after it was cleared"); return; }
                // (Never for every post at once: only the post that was opened. Through the real entry, window and all.)
                HiringAdaptation(7f);
                int notices0 = StaffUtility.GainedColonistNotices;
                HiringUI.Open(map, guard);
                List<Applicant> first = pool.For(guard);
                if (first.Count == 0) { RPLog.Message("[RimPrison:test] hiring round: not checked, the generator found nobody for a guard"); return; }
                int now = ApplicantPool.Now;
                if (first.Count > ApplicantPool.PerRole || pool.Count != first.Count || other != null && pool.For(other).Count != 0)
                    HiringFail(check, $"opening the guards' post made {first.Count} guards ({pool.Count} applicants in all, {(other == null ? 0 : pool.For(other).Count)} of another post), at most {ApplicantPool.PerRole} and for that post only");
                foreach (Applicant a in first)
                    if (a.role != guard || a.appliedTick != now || a.round != ApplicantPool.RoundAt(now) || !pool.Holds(a.pawn) || a.pawn.Spawned || Find.WorldPawns.Contains(a.pawn))
                        HiringFail(check, $"an applicant ({HiringWho(a.pawn)}) has role {a.role?.defName}, applied at {a.appliedTick} (now {now}), round {a.round}, held {pool.Holds(a.pawn)}, spawned {a.pawn.Spawned}, a world pawn {Find.WorldPawns.Contains(a.pawn)}");
                if (pool.MadeThisRound(guard) != first.Count) HiringFail(check, $"the round says {pool.MadeThisRound(guard)} were made, there are {first.Count}");
                // Making the applicants is no gain of a colonist: the storyteller's population adaptation is as it was.
                if (HiringWatcher.AdaptDays != 7f || StaffUtility.GainedColonistNotices != notices0)
                    HiringFail(check, $"opening the post moved the population adaptation (7 -> {HiringWatcher.AdaptDays}) or told the watcher of a colonist gained ({StaffUtility.GainedColonistNotices - notices0} times)");
                Dialog_Applicants window = Find.WindowStack.WindowOfType<Dialog_Applicants>();
                if (window == null) HiringFail(check, "the real entry opened no applicants' window");
                else if (window.soundAppear != null || window.soundClose != null) HiringFail(check, "the applicants' window has a sound on opening or closing: it draws random numbers");

                // Opened again in the same round, through the real entry (the old window closed, a new one added): the same people, and not one random number drawn.
                string ids = string.Join(",", first.Select(a => a.pawn.thingIDNumber));
                ulong rand0 = HiringRandState();
                HiringUI.Open(map, guard);
                ulong rand1 = HiringRandState();
                string idsAgain = string.Join(",", pool.For(guard).Select(a => a.pawn.thingIDNumber));
                if (ids != idsAgain) HiringFail(check, $"opening the post again in the same round changed the applicants ({ids} -> {idsAgain})");
                if (rand0 != rand1) HiringFail(check, $"opening the post again drew random numbers (state {rand0} -> {rand1})");
                if (Find.WindowStack.WindowOfType<Dialog_Applicants>() is not { } again || again == window) HiringFail(check, "opening the post again did not show a window of its own");
                if (HiringWatcher.AdaptDays != 7f) HiringFail(check, $"opening the post again moved the population adaptation (7 -> {HiringWatcher.AdaptDays})");
                RPLog.Message($"[RimPrison:test] hiring round: {first.Count} guards made on the first open, the same ones, no random draw and no adaptation reset on the second (the real window)");
            });

            Guarded("hiring - an applicant chosen is the person hired, for one fee, once", map, r =>
            {
                const string check = "hire";
                PrisonGame g = PrisonGame.Instance;
                HiringScope scope = HiringStart(r, map, guard);
                ApplicantPool pool = scope.Pool;
                pool.Open(guard);
                List<Applicant> shown = pool.For(guard);
                if (shown.Count == 0) { RPLog.Message("[RimPrison:test] hiring hire: not checked, the generator found nobody for a guard"); return; }
                // (The second one if there is one: a hire that took the first, or made somebody new, shows.)
                Applicant chosen = shown[Math.Min(1, shown.Count - 1)];
                Pawn p = chosen.pawn;
                HiringOwn(scope, r, p);
                (int id, string who, string skills, string traits) = (p.thingIDNumber, HiringWho(p), HiringSkills(p), HiringTraits(p));
                var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
                int funds0 = g.ledger.funds;
                // The hire is the gain of a colonist: the population watcher is told once, then and not before.
                HiringAdaptation(7f);
                int notices0 = StaffUtility.GainedColonistNotices;
                bool hired = pool.TryHire(map, chosen);
                var made = map.mapPawns.FreeColonistsSpawned.Where(x => !colonists.Contains(x)).ToList();
                CompStaff sc = p.GetComp<CompStaff>();
                if (!hired || !p.Spawned || made.Count != 1 || made[0] != p)
                    HiringFail(check, $"hiring the chosen applicant went through {hired}, spawned {p.Spawned}, and {made.Count} new colonists ({string.Join(",", made.Select(HiringWho))}) came, expected only {who}");
                if (HiringWatcher.AdaptDays != 0f || StaffUtility.GainedColonistNotices != notices0 + 1)
                    HiringFail(check, $"the hire left the population adaptation at {HiringWatcher.AdaptDays} (0 expected) and told the watcher {StaffUtility.GainedColonistNotices - notices0} times (once expected)");
                if (p.thingIDNumber != id || HiringWho(p) != who || HiringSkills(p) != skills || HiringTraits(p) != traits)
                    HiringFail(check, $"the person hired is not the one shown: id {id} -> {p.thingIDNumber}, name {who} -> {HiringWho(p)}, skills {skills} -> {HiringSkills(p)}, traits {traits} -> {HiringTraits(p)}");
                if (sc?.role != guard || p.Faction != Faction.OfPlayer || sc.hiredTick == CompStaff.NotHired) HiringFail(check, $"hired, but not as a guard of the player's staff (role {sc?.role?.defName}, faction {p.Faction?.Name}, hired tick {sc?.hiredTick})");
                if (g.ledger.funds != funds0 - guard.hireCost) HiringFail(check, $"the fee charged was {funds0 - g.ledger.funds}, the post's is {guard.hireCost}");
                if (pool.Holds(p) || pool.For(guard).Count != shown.Count - 1 || pool.For(guard).Contains(chosen)) HiringFail(check, $"the hired applicant is still in the pool ({pool.Holds(p)}, {pool.For(guard).Count} left of {shown.Count})");
                if (!r.NewEntries.Any(en => en.text == "RP_Chr_Hired".Translate(p.LabelShort, guard.label).Resolve())) HiringFail(check, "no chronicle line for the hire: it did not run the ordinary hire");

                // The same person cannot be hired twice: refused, nobody new, no fee, no second notice.
                int funds1 = g.ledger.funds, colonists1 = map.mapPawns.FreeColonistsSpawned.Count, notices1 = StaffUtility.GainedColonistNotices;
                bool twice = pool.TryHire(map, chosen);
                if (twice || g.ledger.funds != funds1 || map.mapPawns.FreeColonistsSpawned.Count != colonists1 || StaffUtility.GainedColonistNotices != notices1)
                    HiringFail(check, $"hiring the same applicant again went through {twice}, funds {funds1} -> {g.ledger.funds}, colonists {colonists1} -> {map.mapPawns.FreeColonistsSpawned.Count}, notices {notices1} -> {StaffUtility.GainedColonistNotices}");

                // Funds short: refused with nothing changed (the applicant stays, nobody spawns, nothing is paid, nothing booked), the applicant's hire first, with its own baseline...
                Applicant next = pool.For(guard).FirstOrDefault();
                if (next == null) { RPLog.Message("[RimPrison:test] hiring hire: the funds-short half not checked, no applicant is left"); return; }
                int left = pool.Count;
                g.ledger.funds = guard.hireCost - 1;
                (int poorFunds, int poorLedger, int poorColonists, int poorNotices) = (g.ledger.funds, r.NewLedgerEntries.Count(), map.mapPawns.FreeColonistsSpawned.Count, StaffUtility.GainedColonistNotices);
                bool poor = pool.TryHire(map, next);
                if (poor || g.ledger.funds != poorFunds || r.NewLedgerEntries.Count() != poorLedger || pool.Count != left || !pool.Holds(next.pawn) || !pool.entries.Contains(next) || next.pawn.Spawned
                    || map.mapPawns.FreeColonistsSpawned.Count != poorColonists || StaffUtility.GainedColonistNotices != poorNotices)
                    HiringFail(check, $"with the funds short the applicant hire went through {poor}, funds {poorFunds} -> {g.ledger.funds}, ledger entries {poorLedger} -> {r.NewLedgerEntries.Count()}, the pool {left} -> {pool.Count}, held {pool.Holds(next.pawn)}, colonists {poorColonists} -> {map.mapPawns.FreeColonistsSpawned.Count}");

                // ... then the agency's, with another.
                g.ledger.funds = StaffUtility.AgencyFee(guard) - 1;
                (int agencyFunds, int agencyLedger, int agencyColonists, int agencyNotices) = (g.ledger.funds, r.NewLedgerEntries.Count(), map.mapPawns.FreeColonistsSpawned.Count, StaffUtility.GainedColonistNotices);
                bool poorAgency = StaffUtility.HireThroughAgency(map, guard);
                if (poorAgency || g.ledger.funds != agencyFunds || r.NewLedgerEntries.Count() != agencyLedger || map.mapPawns.FreeColonistsSpawned.Count != agencyColonists || StaffUtility.GainedColonistNotices != agencyNotices)
                    HiringFail(check, $"with the funds short the agency's hire went through {poorAgency}, funds {agencyFunds} -> {g.ledger.funds}, ledger entries {agencyLedger} -> {r.NewLedgerEntries.Count()}, colonists {agencyColonists} -> {map.mapPawns.FreeColonistsSpawned.Count}");
                RPLog.Message($"[RimPrison:test] hiring hire: {who} (id {id}) hired as the person shown for {guard.hireCost}, gone from the pool, not hirable twice, the population watcher told once; short funds change nothing");
            });

            Guarded("hiring - the next round lets the old applicants go and brings new ones", map, r =>
            {
                const string check = "next round";
                HiringScope scope = HiringStart(r, map, guard);
                ApplicantPool pool = scope.Pool;
                pool.Open(guard);
                if (other != null) pool.Open(other);
                List<Pawn> old = pool.entries.Select(a => a.pawn).ToList();
                if (old.Count == 0) { RPLog.Message("[RimPrison:test] hiring next round: not checked, the generator found nobody"); return; }
                int now = ApplicantPool.Now;

                // The guards' post opened in the next round: its old applicants go (they are not world pawns, not spawned) and new ones come; another post's stay until the daily pass.
                ApplicantPool.TickOverride = now + ApplicantPool.RoundTicks;
                List<Pawn> oldGuards = pool.For(guard).Select(a => a.pawn).ToList(), oldCooks = other == null ? new List<Pawn>() : pool.For(other).Select(a => a.pawn).ToList();
                if (!pool.Open(guard)) HiringFail(check, "opening the post in the next round made no new pool");
                foreach (Pawn p in oldGuards)
                    if (HiringStillAround(pool, p) is { } why) HiringFail(check, $"a guard of the old round ({HiringWho(p)}) was left behind: {why}");
                List<Applicant> fresh = pool.For(guard);
                if (fresh.Count == 0 || fresh.Any(a => oldGuards.Contains(a.pawn) || a.round != ApplicantPool.RoundAt(now) + 1 || a.appliedTick != now + ApplicantPool.RoundTicks))
                    HiringFail(check, $"the new round's pool has {fresh.Count} applicants, some of them the old ones or of the wrong round ({string.Join(",", fresh.Select(a => a.round + "@" + a.appliedTick))})");
                if (oldCooks.Count > 0 && oldCooks.Any(p => !pool.Holds(p))) HiringFail(check, "another post's applicants went when the guards' post was opened");

                // The daily pass lets the rest go, and anybody the pool holds that no entry names.
                Pawn orphan = HiringOwn(scope, r, StaffUtility.GenerateCandidate(guard));
                ((ThingOwner<Pawn>)pool.GetDirectlyHeldThings()).TryAdd(orphan, canMergeWithExistingStacks: false);
                ApplicantPool.TickOverride = now + 2 * ApplicantPool.RoundTicks;
                pool.Daily();
                foreach (Pawn p in oldCooks.Concat(fresh.Select(a => a.pawn)).Append(orphan))
                    if (HiringStillAround(pool, p) is { } why) HiringFail(check, $"the daily pass left {HiringWho(p)} behind: {why}");
                if (pool.Count != 0) HiringFail(check, $"the daily pass of a later round left {pool.Count} applicants");
                RPLog.Message($"[RimPrison:test] hiring next round: {oldGuards.Count} old guards let go when the post was opened again, the rest by the daily pass, none left as a world pawn");
            });

            Guarded("hiring - nobody fit is an empty pool, and opening it again is no second try", map, r =>
            {
                const string check = "unfit";
                PrisonGame g = PrisonGame.Instance;
                HiringScope scope = HiringStart(r, map, guard);
                ApplicantPool pool = scope.Pool;
                var tried = new List<Pawn>();
                StaffUtility.CandidateUnfit = p => { tried.Add(p); return true; };
                HiringAdaptation(7f);
                int funds0 = g.ledger.funds, colonists0 = map.mapPawns.FreeColonistsSpawned.Count;
                bool made = pool.Open(guard);
                int calls1 = tried.Count;
                bool again = pool.Open(guard);
                StaffUtility.CandidateUnfit = null;
                if (!made || again || calls1 == 0 || tried.Count != calls1 || pool.Count != 0 || pool.MadeThisRound(guard) != 0)
                    HiringFail(check, $"with every candidate unfit the pool was made {made}, has {pool.Count} applicants, says {pool.MadeThisRound(guard)} were made, and opening again made one {again} ({calls1} candidates tried, then {tried.Count})");
                if (g.ledger.funds != funds0 || map.mapPawns.FreeColonistsSpawned.Count != colonists0) HiringFail(check, $"funds {funds0} -> {g.ledger.funds} or colonists {colonists0} -> {map.mapPawns.FreeColonistsSpawned.Count} moved with nobody to hire");
                // Every candidate the generator made and the pool turned down is let go: none held, spawned, a world pawn or left undiscarded.
                foreach (Pawn p in tried)
                    if (HiringStillAround(pool, p) is { } why) HiringFail(check, $"a candidate who could not do the work ({HiringWho(p)}) was left behind: {why}");
                if (HiringWatcher.AdaptDays != 7f) HiringFail(check, $"making candidates nobody hired moved the population adaptation (7 -> {HiringWatcher.AdaptDays})");
                RPLog.Message($"[RimPrison:test] hiring unfit: every candidate unfit is an empty pool, no fee, and the same round does not try again; all {tried.Count} candidates tried were discarded");
            });

            Guarded("hiring - the candidates who fail the work on the way are discarded", map, r =>
            {
                const string check = "retries";
                HiringScope scope = HiringStart(r, map, guard);
                ApplicantPool pool = scope.Pool;
                var tried = new List<Pawn>();
                // (The first two candidates cannot do the work, the rest can.)
                StaffUtility.CandidateUnfit = p => { tried.Add(p); return tried.Count <= 2; };
                HiringAdaptation(7f);
                pool.Open(guard);
                StaffUtility.CandidateUnfit = null;
                if (tried.Count < 3 || pool.Count == 0) { HiringFail(check, $"{tried.Count} candidates were tried and {pool.Count} applicants made, expected at least three tried and some made"); return; }
                foreach (Pawn p in tried.Take(2))
                    if (HiringStillAround(pool, p) is { } why) HiringFail(check, $"an unfit candidate that was tried again ({HiringWho(p)}) was left behind: {why}");
                if (pool.entries.Any(a => tried.Take(2).Contains(a.pawn))) HiringFail(check, "an unfit candidate is among the applicants");
                if (pool.entries.Any(a => !pool.Holds(a.pawn))) HiringFail(check, "an applicant is not held by the pool");
                if (HiringWatcher.AdaptDays != 7f) HiringFail(check, $"the retries moved the population adaptation (7 -> {HiringWatcher.AdaptDays})");
                RPLog.Message($"[RimPrison:test] hiring retries: {tried.Count} candidates tried, the first two (unfit) discarded, {pool.Count} applicants held");
            });

            Guarded("hiring - the agency hires at twice the fee, and no fit candidate is no hire and no fee", map, r =>
            {
                const string check = "agency";
                PrisonGame g = PrisonGame.Instance;
                HiringScope scope = HiringStart(r, map, guard);
                var tried = new List<Pawn>();
                StaffUtility.CandidateUnfit = p => { tried.Add(p); return true; };
                HiringAdaptation(7f);
                int funds0 = g.ledger.funds, colonists0 = map.mapPawns.FreeColonistsSpawned.Count, notices0 = StaffUtility.GainedColonistNotices, ledger0 = r.NewLedgerEntries.Count();
                bool refused = StaffUtility.HireThroughAgency(map, guard);
                StaffUtility.CandidateUnfit = null;
                if (refused || g.ledger.funds != funds0 || r.NewLedgerEntries.Count() != ledger0 || map.mapPawns.FreeColonistsSpawned.Count != colonists0 || StaffUtility.GainedColonistNotices != notices0 || HiringWatcher.AdaptDays != 7f)
                    HiringFail(check, $"the agency with nobody fit hired {refused}, funds {funds0} -> {g.ledger.funds}, colonists {colonists0} -> {map.mapPawns.FreeColonistsSpawned.Count}, adaptation 7 -> {HiringWatcher.AdaptDays}");
                // (Every candidate the agency tried, the last included, is let go.)
                foreach (Pawn p in tried)
                    if (HiringStillAround(scope.Pool, p) is { } why) HiringFail(check, $"a candidate the agency turned down ({HiringWho(p)}) was left behind: {why}");
                var before = map.mapPawns.FreeColonistsSpawned.ToList();
                int notices1 = StaffUtility.GainedColonistNotices;
                bool hired = StaffUtility.HireThroughAgency(map, guard);
                Pawn p2 = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => !before.Contains(x));
                HiringOwn(scope, r, p2);
                if (!hired || p2 == null || p2.GetComp<CompStaff>()?.role != guard || g.ledger.funds != funds0 - 2 * guard.hireCost || StaffUtility.AgencyFee(guard) != 2 * guard.hireCost)
                    HiringFail(check, $"the agency hire went through {hired}, made {p2?.LabelShort ?? "nobody"} as {p2?.GetComp<CompStaff>()?.role?.defName}, funds {funds0} -> {g.ledger.funds} (twice the fee is {2 * guard.hireCost})");
                if (HiringWatcher.AdaptDays != 0f || StaffUtility.GainedColonistNotices != notices1 + 1)
                    HiringFail(check, $"the agency hire left the population adaptation at {HiringWatcher.AdaptDays} (0 expected) and told the watcher {StaffUtility.GainedColonistNotices - notices1} times (once expected)");
                RPLog.Message($"[RimPrison:test] hiring agency: one guard for {funds0 - g.ledger.funds}, twice the fee {guard.hireCost}; {tried.Count} unfit candidates discarded");
            });

            Guarded("hiring - an applicant is rolled for kin when hired, a former inmate keeps their own story", map, r =>
            {
                const string check = "kin";
                PrisonGame g = PrisonGame.Instance;
                HiringScope scope = HiringStart(r, map, guard);
                ApplicantPool pool = scope.Pool;
                Pawn inmate = Throwaway(map, r);
                KinAge(inmate, 40f);
                KinClean(r, inmate);
                KinPool(r, 1f, inmate);
                HiringOwn(scope, r, inmate);
                // (Made 75 years old, 35 more than the inmate: nothing but their parent.)
                StaffUtility.CandidateChronologicalAge = 75f;
                pool.Open(guard);
                StaffUtility.CandidateChronologicalAge = null;
                Applicant applicant = pool.For(guard).FirstOrDefault();
                if (applicant == null) { RPLog.Message("[RimPrison:test] hiring kin: not checked, the generator found nobody for a guard"); return; }
                Pawn p = applicant.pawn;
                HiringOwn(scope, r, p);
                KinClean(r, p);
                if (p.relations != null && p.relations.DirectRelations.Count != 0) HiringFail(check, $"an applicant had {p.relations.DirectRelations.Count} relations before they were hired: kin was rolled at generation");
                bool hired = pool.TryHire(map, applicant);
                if (!hired) { HiringFail(check, "the applicant was not hired"); return; }
                if (Kin.Between(p, inmate) != KinKind.Child) HiringFail(check, $"an applicant hired at odds of one was not made the inmate's parent (the inmate is their {Kin.Between(p, inmate)?.ToString() ?? "nothing"}): they did not get the ordinary hire's roll");
                else if (r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().All(l => !l.Is("RP_Decision_Kin") || !l.pawns.Contains(p))) HiringFail(check, "the guard hired kin to an inmate got no letter");

                // A former inmate through the register's hire: that very person, their record kept, and never rolled for kin (odds of one, an age that would fit).
                if (!Unlocks.IsUnlocked(cook) || !ExInmates.Roles.Contains(cook)) { RPLog.Message("[RimPrison:test] hiring kin: the former inmate half not checked, the cook post is locked"); return; }
                Pawn inmate2 = Throwaway(map, r);
                KinAge(inmate2, 40f);
                KinClean(r, inmate2);
                KinPool(r, 1f, inmate2);
                HiringOwn(scope, r, inmate2);
                FormerInmate former = ExFormer(map, r, 5f, ExCleanPast(), cook);
                Pawn fp = former.pawn;
                KinAge(fp, 75f);
                KinClean(r, fp);
                HiringOwn(scope, r, fp);
                (int id, string who, string skills) = (fp.thingIDNumber, HiringWho(fp), HiringSkills(fp));
                bool hiredFormer = ExInmates.Hire(g, map, former, cook);
                CompStaff fc = fp.GetComp<CompStaff>();
                if (!hiredFormer || !fp.Spawned || fp.thingIDNumber != id || HiringWho(fp) != who || HiringSkills(fp) != skills || fc?.formerPast == null || !former.hired)
                    HiringFail(check, $"the former inmate hired went through {hiredFormer}, spawned {fp.Spawned}, kept id {fp.thingIDNumber == id}, name {HiringWho(fp) == who}, skills {HiringSkills(fp) == skills}, their record {fc?.formerPast != null}");
                if (Kin.Between(fp, inmate2) != null) HiringFail(check, "a former inmate hired from the register was rolled for kin");
                RPLog.Message("[RimPrison:test] hiring kin: an applicant hired at odds of one is made kin and the guard asked; a former inmate keeps their identity and is not rolled");
            });

            Guarded("hiring - the former-inmate fixture lets go of the candidates it turns down", map, r =>
            {
                const string check = "fixture";
                HiringScope scope = HiringStart(r, map, guard);
                var world0 = new HashSet<Pawn>(Find.WorldPawns.AllPawnsAliveOrDead);
                // (A capability test that turns the first candidate down and takes the second.)
                int asked = 0;
                var rejected = new List<Pawn>();
                FormerInmate former = ExFormer(map, r, 5f, ExCleanPast(), cook, (_, _) => ++asked == 2, rejected);
                Pawn kept = HiringOwn(scope, r, former.pawn);
                if (asked != 2 || rejected.Count != 1 || rejected[0] == kept)
                    HiringFail(check, $"the fixture asked {asked} times (2 expected) and turned down {rejected.Count} (1 expected), the kept one among them {rejected.Contains(kept)}");
                foreach (Pawn p in rejected)
                    if (!p.Destroyed || p.Spawned || Find.WorldPawns.Contains(p) || Find.WorldPawns.ForcefullyKeptPawns.Contains(p) || !p.Discarded)
                        HiringFail(check, $"the candidate turned down was not let go of: destroyed {p.Destroyed}, spawned {p.Spawned}, a world pawn {Find.WorldPawns.Contains(p)}, kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(p)}, discarded {p.Discarded}");
                if (kept.Destroyed || kept.Discarded || kept.Spawned || !Find.WorldPawns.Contains(kept) || !Find.WorldPawns.ForcefullyKeptPawns.Contains(kept) || !PrisonGame.Instance.story.formerInmates.Contains(former))
                    HiringFail(check, $"the candidate taken is not a kept world pawn in the register: destroyed {kept.Destroyed}, discarded {kept.Discarded}, spawned {kept.Spawned}, a world pawn {Find.WorldPawns.Contains(kept)}, kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(kept)}");
                // The world pawns are what they were and the one kept, with nothing of the pre-existing ones removed.
                var now = new HashSet<Pawn>(Find.WorldPawns.AllPawnsAliveOrDead);
                List<Pawn> extra = now.Where(x => !world0.Contains(x) && x != kept).ToList(), missing = world0.Where(x => !now.Contains(x)).ToList();
                if (extra.Count != 0 || missing.Count != 0 || !now.Contains(kept))
                    HiringFail(check, $"the fixture left the world pawns with {extra.Count} more ({string.Join(",", extra.Select(HiringWho))}) and {missing.Count} fewer ({string.Join(",", missing.Select(HiringWho))}) than the one kept");
                RPLog.Message("[RimPrison:test] hiring fixture: the former inmate fixture let go of the candidate it turned down and kept the one it took, the world pawns as they were");
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------

        /// <summary>The pool as one comparable string, and how many of its pawns are world pawns (none may be).</summary>
        private static string HiringDescribe(PrisonGame g) => g.applicants.Describe() + $"/world={g.applicants.entries.Count(a => a.pawn != null && Find.WorldPawns.Contains(a.pawn))}";

        private static int hiringSeedId = -1;

        /// <summary>
        /// The pool off its defaults: one post's applicants of a later round (so a round and a tick of their own), another's of the current round (one of them is hired after the load), the generated applicants
        /// held and none of them a world pawn.
        /// </summary>
        private static void SeedHiringForReload(Map map)
        {
            ApplicantPool pool = PrisonGame.Instance.applicants;
            StaffRoleDef guard = RPSecurityDefOf.RP_Guard;
            // (The post whose applicants are of the current round: another open post if there is one, so that the later round has a post of its own; else the guards'.)
            StaffRoleDef now = HiringOtherPost(guard) ?? (Unlocks.IsUnlocked(guard) ? guard : null);
            pool.Clear();
            hiringSeedId = -1;
            if (now != guard)
            {
                ApplicantPool.TickOverride = ApplicantPool.Now + 2 * ApplicantPool.RoundTicks + 4321;
                try
                {
                    pool.Open(guard);
                }
                finally
                {
                    ApplicantPool.TickOverride = null;
                }
            }
            if (now != null)
            {
                pool.Open(now);
                hiringSeedId = pool.For(now).FirstOrDefault()?.pawn.thingIDNumber ?? -1;
            }
            if (pool.Count == 0) problems0.Add("saveload: the generator found no applicant to seed the pool with");
            else RPLog.Message($"[RimPrison:test] saveload hiring: {HiringDescribe(PrisonGame.Instance)}");
        }

        /// <summary>The applicant seeded in the current round, after the load: in the pool, still of the current round, hired once (a fee, a colonist) and refused the second time.</summary>
        private static void CheckHiringAfterReload(Map map)
        {
            if (hiringSeedId < 0) return;
            int id = hiringSeedId;
            hiringSeedId = -1;
            Guarded("hiring - an applicant saved in the current round is hired once after the load", map, r =>
            {
                const string check = "after the load";
                PrisonGame g = PrisonGame.Instance;
                Applicant a = g.applicants.entries.FirstOrDefault(x => x.pawn?.thingIDNumber == id);
                if (a == null) { HiringFail(check, "the applicant seeded in the current round is not in the pool after the load"); return; }
                if (a.round != ApplicantPool.RoundAt(ApplicantPool.Now)) { RPLog.Message("[RimPrison:test] hiring after the load: not checked, the round of the seeded applicant is over"); return; }
                HiringScope scope = HiringStart(r, map, a.role, live: true);
                ApplicantPool pool = scope.Pool;
                Pawn p = HiringOwn(scope, r, a.pawn);
                (string who, string skills, string traits) = (HiringWho(p), HiringSkills(p), HiringTraits(p));
                int funds0 = g.ledger.funds, colonists0 = map.mapPawns.FreeColonistsSpawned.Count;
                bool hired = pool.TryHire(map, a);
                if (!hired || !p.Spawned || pool.Holds(p) || pool.entries.Contains(a) || g.ledger.funds != funds0 - a.role.hireCost || map.mapPawns.FreeColonistsSpawned.Count != colonists0 + 1)
                    HiringFail(check, $"hiring the applicant saved in the current round went through {hired}, spawned {p.Spawned}, still held {pool.Holds(p)}, funds {funds0} -> {g.ledger.funds} (the fee is {a.role.hireCost}), colonists {colonists0} -> {map.mapPawns.FreeColonistsSpawned.Count}");
                if (HiringWho(p) != who || HiringSkills(p) != skills || HiringTraits(p) != traits) HiringFail(check, "the applicant hired after the load is not the one saved");
                int funds1 = g.ledger.funds, colonists1 = map.mapPawns.FreeColonistsSpawned.Count;
                bool twice = pool.TryHire(map, a);
                if (twice || g.ledger.funds != funds1 || map.mapPawns.FreeColonistsSpawned.Count != colonists1)
                    HiringFail(check, $"hiring the same applicant a second time went through {twice}, funds {funds1} -> {g.ledger.funds}, colonists {colonists1} -> {map.mapPawns.FreeColonistsSpawned.Count}");
                RPLog.Message($"[RimPrison:test] saveload hiring: {who} (saved in the current round) hired once after the load, refused the second time");
            });
        }
    }
}
