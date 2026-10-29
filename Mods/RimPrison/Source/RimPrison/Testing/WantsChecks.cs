using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the review of inmate wants (B1-B7, the balance, hidden-information and text points, and the hooks no
    /// check drove). Each is its own method called from <see cref="CheckWantsReview"/>, so its names stay its own. They work on
    /// throwaway inmates and staff, put back what they touch (<see cref="Rollback"/>), and drive the real paths: a blow
    /// that lands through <c>TakeDamage</c>, a release through <c>ReleaseSystem.Release</c>, a letter through the news worker.
    /// </summary>
    public static partial class AutoTest
    {
        // ---- helpers -------------------------------------------------------------------------------------------------

        private static InmateWant CloneWant(InmateWant w)
        {
            if (w == null) return null;
            var copy = new InmateWant();
            CopyWant(copy, w);
            return copy;
        }

        private static void CopyWant(InmateWant to, InmateWant from)
        {
            (to.def, to.status, to.progress, to.formedTick, to.deadlineTick, to.resolvedTick, to.renewals) = (from.def, from.status, from.progress, from.formedTick, from.deadlineTick, from.resolvedTick, from.renewals);
            (to.announced, to.halfway, to.slipLogged, to.targetId, to.targetName, to.targetIsStaff) = (from.announced, from.halfway, from.slipLogged, from.targetId, from.targetName, from.targetIsStaff);
            (to.note, to.program, to.count, to.baseline, to.last) = (from.note, from.program, from.count, from.baseline, from.last);
        }

        /// <summary>
        /// Every inmate's goal and staff feelings in one string (what a check that touches staff memories must leave as it found it). Of every prisoner on the map, spawned or not: a guard who is
        /// carrying an inmate puts them down when the check teleports the guard, and the inmate is then on the list that was a name short before (a goal "changed": the staff memories check, seen once in six runs).
        /// </summary>
        private static string GoalSnapshot(Map map) =>
            string.Join("|", map.mapPawns.PrisonersOfColony.OrderBy(x => x.thingIDNumber).Select(x => x.Inmate() is { } c
                ? $"{x.thingIDNumber}:{Wants.Describe(c)}:{string.Join(",", c.staffMemories.Select(m => $"{m.staffId}={m.score:F1}"))}" : ""));

        /// <summary>What differs between two <see cref="GoalSnapshot"/> strings (empty when they are the same): the entries that changed, before and after.</summary>
        private static string GoalDiff(string before, string after)
        {
            if (before == after) return "";
            string[] b = before.Split('|'), a = after.Split('|');
            var diff = new List<string>();
            for (int i = 0; i < Math.Max(b.Length, a.Length) && diff.Count < 4; i++)
                if ((i < b.Length ? b[i] : "(none)") != (i < a.Length ? a[i] : "(none)")) diff.Add($"[{(i < b.Length ? b[i] : "(none)")}] became [{(i < a.Length ? a[i] : "(none)")}]");
            return string.Join("; ", diff);
        }

        /// <summary>
        /// Puts every one of these inmates' goal (the object itself and what was done to it: a goal put aside is changed in place), staff
        /// feelings and record of past grudges back as they are now.
        /// </summary>
        private static Action RememberGoals(IEnumerable<Pawn> pawns)
        {
            var saved = new List<(CompInmate c, InmateWant want, InmateWant copy, List<StaffMemory> memories, float[] scores, List<GrudgeRecord> grudges)>();
            foreach (Pawn p in pawns.ToList())
                if (p.Inmate() is { } c) saved.Add((c, c.want, CloneWant(c.want), c.staffMemories.ToList(), c.staffMemories.Select(m => m.score).ToArray(), c.pastGrudges.ToList()));
            return () =>
            {
                foreach (var s in saved)
                {
                    s.c.want = s.want;
                    if (s.want != null) CopyWant(s.want, s.copy);
                    s.c.staffMemories.Clear();
                    s.c.staffMemories.AddRange(s.memories);
                    for (int i = 0; i < s.memories.Count; i++) s.memories[i].score = s.scores[i];
                    s.c.pastGrudges = s.grudges;
                }
            };
        }

        private static int Lines(Rollback r, string line) => r.NewEntries.Count(e => e.text == line);

        private static string WantLine(string key, Pawn who, InmateWant w) => key.Translate(who.LabelShort, Wants.Title(w)).Resolve();

        private static string WantLine(string key, Pawn who, InmateWant w, string reason) => key.Translate(who.LabelShort, Wants.Title(w), reason.Translate()).Resolve();

        private static void Remember(CompInmate c, Pawn staff, float score) =>
            c.staffMemories.Add(new StaffMemory { staffId = staff.thingIDNumber, name = staff.LabelShort, score = score, lastTick = Find.TickManager.TicksGame });

        /// <summary>A blow that lands: real damage, from this one, through the game's own damage path.</summary>
        private static void Strike(Pawn attacker, Pawn victim) =>
            victim.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 6f, 100f, -1f, attacker, null, null, DamageInfo.SourceCategory.ThingOrUnknown, null, true, true, QualityCategory.Normal, false));

        /// <summary>Beside the other pawn (a cell of their own in the same room), so that an interaction has the room it needs.</summary>
        private static void StandBeside(Map map, Pawn who, Pawn anchor)
        {
            if (!CellFinder.TryFindRandomReachableNearbyCell(anchor.Position, map, 2, TraverseParms.For(who), x => x.Standable(map) && x != anchor.Position && x.GetRoom(map) == anchor.GetRoom(), null, out IntVec3 cell)) return;
            who.pather.StopDead();
            who.Position = cell;
            who.Notify_Teleported();
        }

        /// <summary>Off every addiction (a second of the same kind would merge into the first, and a check would set the severity of a hediff that is not on them).</summary>
        private static void ClearAddictions(Pawn p)
        {
            foreach (Hediff h in p.health.hediffSet.hediffs.Where(x => x is Hediff_Addiction).ToList()) p.health.RemoveHediff(h);
        }

        /// <summary>An enemy inside: the relation, and its opinion made bad enough to hold a grudge (put back).</summary>
        private static void MakeEnemies(Rollback r, Pawn a, Pawn b, int opinion = -100)
        {
            PawnRelationDef enemy = RPRelationDefOf.RP_Enemy;
            int offset0 = enemy.opinionOffset;
            a.relations.AddDirectRelation(enemy, b);
            SetEnemyOpinion(a, b, opinion);
            r.Add(() =>
            {
                enemy.opinionOffset = offset0;
                a.relations.TryRemoveDirectRelation(enemy, b);
            });
        }

        /// <summary>Sets what <paramref name="from"/> thinks of <paramref name="to"/>, whatever else they already think of them (the enemy relation adds a fixed offset to it).</summary>
        private static void SetEnemyOpinion(Pawn from, Pawn to, int opinion)
        {
            RPRelationDefOf.RP_Enemy.opinionOffset = 0;
            RPRelationDefOf.RP_Enemy.opinionOffset = opinion - from.relations.OpinionOf(to);
        }

        private static Pawn ViolentThrowaway(Map map, Rollback r)
        {
            for (int i = 0; i < 8; i++)
            {
                Pawn p = Throwaway(map, r);
                if (!p.WorkTagIsDisabled(WorkTags.Violent)) return p;
            }
            return null;
        }

        /// <summary>Casts an inmate for an event the way the incidents do (<see cref="IncidentWorker_Prison.RandomInmate"/>).</summary>
        private sealed class StoryPicker : IncidentWorker_Prison
        {
            protected override bool TryExecuteWorker(IncidentParms parms) => false;

            public Pawn Pick(Map map, Func<Pawn, bool> validator) => RandomInmate(map, validator);
        }

        private static void CheckWantsReview(Map map)
        {
            CheckGrudgeNeedsABlow(map);
            CheckAssaultNeedsABlow(map);
            CheckWatchersIgnoreInmatesOffTheMap(map);
            CheckGrudgeCooldown(map);
            CheckEnemyGrudge(map);
            CheckHiddenGangGrudge(map);
            CheckMilestonesToldOnce(map);
            CheckBirthKeepsFamilyGoal(map);
            CheckSetbackCosts(map);
            CheckWantHooks(map);
            CheckWantEffects(map);
            CheckDiplomaFileWithoutProgram(map);
        }

        // ---- B1, B7: an insult, or an attack that has only started, does not settle a score ---------------------------------

        private static void CheckGrudgeNeedsABlow(Map map)
        {
            Guarded("a grudge needs a blow", map, r =>
            {
                Pawn holder = ViolentThrowaway(map, r), target = Throwaway(map, r), bystander = Throwaway(map, r);
                if (holder == null)
                {
                    problems0.Add("a grudge needs a blow: no throwaway inmate who can fight");
                    return;
                }
                StandBeside(map, target, holder);
                StandBeside(map, bystander, holder);
                CompInmate hc = holder.Inmate();
                hc.want = null;
                MakeEnemies(r, holder, target);
                InmateWant grudge = Wants.Form(holder, hc, RPWantDefOf.RP_Want_Score);
                if (grudge == null || grudge.targetId != target.thingIDNumber || grudge.targetIsStaff)
                {
                    problems0.Add($"a grudge needs a blow: could not set up an inmate grudge ({grudge?.targetName})");
                    return;
                }
                // They go for the one the grudge is against, whoever else is in reach (the choice follows the goal).
                Pawn picked = MisconductWorker.NearbyInmate(holder, 8f, o => o == target || o == bystander);
                grudge.targetId = bystander.thingIDNumber;
                Pawn pickedOther = MisconductWorker.NearbyInmate(holder, 8f, o => o == target || o == bystander);
                grudge.targetId = target.thingIDNumber;
                if (picked != target || pickedOther != bystander) problems0.Add($"a grudge needs a blow: the picker chose {picked?.LabelShort} with the grudge against {target.LabelShort} and {pickedOther?.LabelShort} with it against {bystander.LabelShort}");

                // An insult at them (the cheapest and commonest misconduct) is not the score settled.
                MisconductDef insult = DefDatabase<MisconductDef>.AllDefsListForReading.FirstOrDefault(m => m.Worker is MisconductWorker_Insult);
                MisconductDef fight = DefDatabase<MisconductDef>.AllDefsListForReading.FirstOrDefault(m => m.Worker is MisconductWorker_Fight);
                if (insult == null || fight == null)
                {
                    problems0.Add("a grudge needs a blow: no insult or fight misconduct");
                    return;
                }
                bool insulted = insult.Worker.TryExecute(holder, hc);
                if (!insulted) problems0.Add($"a grudge needs a blow: the insult could not be made (the check would prove nothing): awake {holder.Awake()}/{target.Awake()}, can interact {holder.interactions.CanInteractNowWith(target, InteractionDefOf.Insult)}, good position {SocialInteractionUtility.IsGoodPositionForInteraction(holder, target)}, {holder.Position} -> {target.Position}");
                if (grudge.Resolved) problems0.Add($"a grudge needs a blow: an insult settled the score ({grudge.status})");
                // A fight begun is not a blow landed either.
                bool fighting = fight.Worker.TryExecute(holder, hc);
                if (fighting && grudge.Resolved) problems0.Add($"a grudge needs a blow: a fight that has only begun settled the score ({grudge.status})");
                foreach (Pawn x in new[] { holder, target, bystander }) x.mindState.mentalStateHandler.Reset();
                // Nobody else's blow, and not a blow at somebody else.
                Strike(bystander, target);
                Strike(holder, bystander);
                if (grudge.Resolved) problems0.Add($"a grudge needs a blow: a blow from somebody else, or at somebody else, settled it ({grudge.status})");
                // A blow of theirs that lands does, with its line.
                Strike(holder, target);
                if (grudge.status != WantStatus.Fulfilled || Lines(r, WantLine("RP_Chr_WantFulfilled", holder, grudge)) != 1)
                    problems0.Add($"a grudge needs a blow: after a landed blow {grudge.status}, lines {Lines(r, WantLine("RP_Chr_WantFulfilled", holder, grudge))} (insult made {insulted}, fight begun {fighting})");
            });
        }

        /// <summary>
        /// The hourly tension pass counts the guards who can see each inmate. An inmate who is not on the map (carried off, leaving) has no map to
        /// look across: the count is zero, not a NullReferenceException that stopped the pass for everyone after them (seen in the state penitentiary's
        /// seven-day run).
        /// </summary>
        private static void CheckWatchersIgnoreInmatesOffTheMap(Map map)
        {
            Guarded("an inmate who is not on the map has no watchers", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                pm.Guards.Add(guard);
                r.Add(() => pm.Guards.Remove(guard));
                Pawn inmate = Throwaway(map, r, spawn: false);
                inmate.Position = guard.Position;
                float watchers = TensionUtility.Watchers(inmate, pm);
                if (watchers != 0f) problems0.Add($"watchers of an inmate who is not on the map: {watchers} (a guard is standing where they were last seen)");
            });
        }

        private static void CheckAssaultNeedsABlow(Map map)
        {
            Guarded("an assault settles it only when it lands", map, r =>
            {
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn holder = ViolentThrowaway(map, r);
                if (holder == null)
                {
                    problems0.Add("assault test: no throwaway inmate who can fight");
                    return;
                }
                CompInmate hc = holder.Inmate();
                hc.want = null;
                hc.staffMemories.Clear();
                Remember(hc, guard, -80f);
                InmateWant grudge = Wants.Form(holder, hc, RPWantDefOf.RP_Want_Score);
                if (grudge == null || grudge.targetId != guard.thingIDNumber || !grudge.targetIsStaff)
                {
                    problems0.Add($"assault test: no grudge against the guard ({grudge?.targetName})");
                    return;
                }
                MisconductDef assault = DefDatabase<MisconductDef>.AllDefsListForReading.First(m => m.Worker is MisconductWorker_AssaultStaff);
                bool started = assault.Worker.TryExecute(holder, hc);
                bool againstGuard = holder.MentalState is MentalState_Assaulting { Target: { } t } && t == guard;
                if (!started || !againstGuard) problems0.Add($"assault test: the assault did not start on the guard (started {started}, state {holder.MentalState?.def.defName})");
                // Started is not landed: a guard who subdues them on the way leaves the score where it was (and the officer's memory).
                float hate = StaffMemories.ScoreFor(hc, guard);
                if (grudge.Resolved || StaffMemories.ScoreFor(hc, guard) != hate) problems0.Add($"assault test: an assault that has only begun settled the score ({grudge.status})");
                Strike(holder, guard);
                if (grudge.status != WantStatus.Fulfilled || StaffMemories.ScoreFor(hc, guard) <= hate)
                    problems0.Add($"assault test: a blow that landed on the guard: {grudge.status}, hate {hate:F0} -> {StaffMemories.ScoreFor(hc, guard):F0}");
                holder.mindState.mentalStateHandler.Reset();
            });
        }

        // ---- B2: the same person is not held against again at once, however the grudge ended --------------------------------

        private static void CheckGrudgeCooldown(Map map)
        {
            Guarded("grudge cooldown", map, r =>
            {
                int day = GenDate.TicksPerDay;
                Pawn officer = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), other = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn fighter = ViolentThrowaway(map, r);
                if (fighter == null)
                {
                    problems0.Add("grudge cooldown test: no throwaway inmate who can fight");
                    return;
                }
                CompInmate hc = fighter.Inmate();
                hc.want = null;
                hc.gang = null; // (a grudge against staff held by a member of an undiscovered gang is not told, and this check counts the lines)
                hc.staffMemories.Clear();
                Remember(hc, officer, -100f);
                InmateWant first = Wants.Form(fighter, hc, RPWantDefOf.RP_Want_Score);
                if (first == null || first.targetId != officer.thingIDNumber)
                {
                    problems0.Add("grudge cooldown test: no grudge against the officer");
                    return;
                }
                Strike(fighter, officer);
                float left = StaffMemories.ScoreFor(hc, officer);
                if (first.status != WantStatus.Fulfilled || left > Wants.GrudgeAt)
                {
                    problems0.Add($"grudge cooldown test: the score was not settled with a memory still at the grudge ({first.status}, memory {left:F0})");
                    return;
                }
                // (a) Settled; two days on a goal is rolled (any), and at the next midnight the grudge is not formed again, nor the goal put aside.
                (hc.sentenceDays, hc.admittedTick) = (40, 0);
                InmateWant parole = Wants.Form(fighter, hc, RPWantDefOf.RP_Want_Parole);
                Wants.DailyFor(fighter, hc);
                if (parole == null || hc.want != parole || parole.Resolved)
                    problems0.Add($"grudge cooldown test: after settling a score and rolling another goal, the midnight sweep gave {(hc.want != null ? Wants.Title(hc.want) : "none")} ({hc.want?.status}); a new grudge against the same officer was formed");
                // (b) A grudge let go (the window passed) is not back the next day with the memory still bad, but is when the cooldown is over.
                hc.want = null;
                Remember(hc, other, -80f);
                InmateWant letGo = Wants.Form(fighter, hc, RPWantDefOf.RP_Want_Score);
                if (letGo == null || letGo.targetId != other.thingIDNumber)
                {
                    problems0.Add($"grudge cooldown test: no second grudge ({letGo?.targetName})");
                    return;
                }
                letGo.deadlineTick = Find.TickManager.TicksGame - 1;
                Wants.Hourly(fighter, hc);
                if (letGo.status != WantStatus.Lost || Lines(r, WantLine("RP_Chr_WantLost", fighter, letGo, "RP_Want_Reason_LetGo")) != 1)
                    problems0.Add($"grudge cooldown test: the grudge was not let go when its window passed ({letGo.status})");
                Wants.DailyFor(fighter, hc);
                if (hc.want != letGo)
                    problems0.Add($"grudge cooldown test: the day after a grudge was let go, {(hc.want != null ? Wants.Title(hc.want) : "none")} ({hc.want?.status}) had replaced it");
                foreach (GrudgeRecord record in hc.pastGrudges) record.tick -= (Wants.SameWantDays + 1) * day;
                letGo.resolvedTick -= 3 * day;
                hc.staffMemories.First(m => m.staffId == other.thingIDNumber).score = -80f;
                Wants.DailyFor(fighter, hc);
                if (hc.want == letGo || hc.want?.def != RPWantDefOf.RP_Want_Score || hc.want.Resolved || hc.want.targetId != other.thingIDNumber)
                    problems0.Add($"grudge cooldown test: once the cooldown was over the grudge did not come back ({(hc.want != null ? Wants.Title(hc.want) : "none")} {hc.want?.status})");
                // The record of past grudges is kept short: the ones whose time is up are forgotten as new ones are noted.
                if (hc.pastGrudges.Count > 2) problems0.Add($"grudge cooldown test: {hc.pastGrudges.Count} past grudges kept");
            });
        }

        /// <summary>The inmate-enemy branch: found through the enemy relation and a bad opinion, cooled when the opinion mends, not held again at once, gone when released.</summary>
        private static void CheckEnemyGrudge(Map map)
        {
            Guarded("enemy grudge", map, r =>
            {
                int day = GenDate.TicksPerDay;
                PrisonGame g = PrisonGame.Instance;
                Pawn holder = Throwaway(map, r), enemy = Throwaway(map, r);
                CompInmate hc = holder.Inmate(), ec = enemy.Inmate();
                ec.innocent = true; // (a release rolls no reoffence of its own)
                hc.want = null;
                hc.staffMemories.Clear();
                // (An opinion of -30 is an enemy, but not bad enough to hold a grudge.)
                MakeEnemies(r, holder, enemy, -30);
                if (Wants.Form(holder, hc, RPWantDefOf.RP_Want_Score) != null) problems0.Add($"enemy grudge test: a grudge was formed against an enemy at opinion {holder.relations.OpinionOf(enemy)}");
                SetEnemyOpinion(holder, enemy, -100);
                InmateWant grudge = Wants.Form(holder, hc, RPWantDefOf.RP_Want_Score);
                if (grudge == null || grudge.targetId != enemy.thingIDNumber || grudge.targetIsStaff || grudge.note == null)
                {
                    problems0.Add($"enemy grudge test: no grudge against an enemy at opinion {holder.relations.OpinionOf(enemy)} ({grudge?.targetName})");
                    return;
                }
                // The opinion mends: the grudge cools.
                SetEnemyOpinion(holder, enemy, 0);
                Wants.Hourly(holder, hc);
                if (grudge.status != WantStatus.Lost || Lines(r, WantLine("RP_Chr_WantLost", holder, grudge, "RP_Want_Reason_Cooled")) != 1)
                    problems0.Add($"enemy grudge test: the opinion mended ({holder.relations.OpinionOf(enemy)}) and the grudge is {grudge.status}");
                // It sours again the same day: no new grudge against the same one until the cooldown is over.
                SetEnemyOpinion(holder, enemy, -100);
                Wants.DailyFor(holder, hc);
                if (hc.want != grudge) problems0.Add($"enemy grudge test: a cooled grudge was formed again at the next midnight ({(hc.want != null ? Wants.Title(hc.want) : "none")})");
                foreach (GrudgeRecord record in hc.pastGrudges) record.tick -= (Wants.SameWantDays + 1) * day;
                grudge.resolvedTick -= 3 * day;
                Wants.DailyFor(holder, hc);
                InmateWant again = hc.want;
                if (again == grudge || again?.def != RPWantDefOf.RP_Want_Score || again.Resolved)
                {
                    problems0.Add($"enemy grudge test: after the cooldown no new grudge against the enemy ({(again != null ? Wants.Title(again) : "none")})");
                    return;
                }
                // The enemy walks out (a real release): the one they were after is gone.
                ReleaseSystem.Release(g, enemy, record: false);
                if (enemy.Spawned) enemy.DeSpawn();
                Wants.Hourly(holder, hc);
                if (again.status != WantStatus.Lost || Lines(r, WantLine("RP_Chr_WantLost", holder, again, "RP_Want_Reason_TargetGone")) != 1)
                    problems0.Add($"enemy grudge test: the enemy was released and the grudge is {again.status}");
            });
        }

        // ---- B3: a grudge from a shooting does not name a gang nobody has found -----------------------------------------------

        private static void CheckHiddenGangGrudge(Map map)
        {
            Guarded("hidden gang grudge", map, r =>
            {
                // (The shooting reaches every inmate of the gang, the real ones too: their goals and feelings are put back.)
                r.Add(RememberGoals(map.mapPawns.PrisonersOfColonySpawned));
                GangDef gang = DefDatabase<GangDef>.AllDefsListForReading.First();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn victim = Throwaway(map, r), mate = Throwaway(map, r), friend = Throwaway(map, r);
                CompInmate vc = victim.Inmate(), mc = mate.Inmate(), fc = friend.Inmate();
                (vc.gang, vc.gangKnown) = (gang, false);
                (mc.gang, mc.gangKnown) = (gang, false);
                (fc.gang, fc.gangKnown) = (null, false);
                foreach (CompInmate x in new[] { mc, fc })
                {
                    x.want = null;
                    x.staffMemories.Clear();
                }
                friend.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, victim);
                r.Add(() => friend.relations.TryRemoveDirectRelation(RPRelationDefOf.RP_Friend, victim));
                var hidden = map.mapPawns.PrisonersOfColonySpawned.Where(x => x.Inmate() is { gang: { } gx, gangKnown: false } && gx == gang).Select(x => x.thingIDNumber).ToHashSet();
                StaffMemories.Notify_ShotDead(victim, guard);
                // The gang-mate has the grudge (it plays out) but the chronicle does not say so; the friend is a visible relation and it does.
                if (mc.want?.def != RPWantDefOf.RP_Want_Score || fc.want?.def != RPWantDefOf.RP_Want_Score)
                    problems0.Add($"hidden gang test: grudges after the shooting: gang-mate {mc.want?.def?.defName}, friend {fc.want?.def?.defName}");
                else
                {
                    string mateLine = WantLine("RP_Chr_WantFormed", mate, mc.want), friendLine = WantLine("RP_Chr_WantFormed", friend, fc.want);
                    if (Lines(r, friendLine) != 1) problems0.Add($"hidden gang test: the friend's grudge was told {Lines(r, friendLine)} times (expected once)");
                    if (Lines(r, mateLine) != 0 || r.NewEntries.Any(e => e.ids.Any(hidden.Contains)))
                        problems0.Add($"hidden gang test: the chronicle names an undiscovered gang: {string.Join(" / ", r.NewEntries.Where(e => e.ids.Any(hidden.Contains)).Select(e => e.text))}");
                    // Once the gang is known, the same grudge is told like any other.
                    mc.gangKnown = true;
                    mc.want = null;
                    Wants.Notify_Grudge(mc);
                    if (mc.want == null || Lines(r, WantLine("RP_Chr_WantFormed", mate, mc.want)) != 1) problems0.Add("hidden gang test: a known gang member's grudge was not told");
                }
            });
        }

        // ---- B4: halfway and slipping are told once, not again after a setback -------------------------------------------------

        private static void CheckMilestonesToldOnce(Map map)
        {
            Guarded("milestones told once", map, r =>
            {
                int Count(Pawn who, InmateWant w, string key) => Lines(r, WantLine(key, who, w));
                // Make parole: the odds and the sentence served are read from the systems, so a refusal does not start the milestones over.
                Pawn hopeful = Throwaway(map, r);
                CompInmate hc = hopeful.Inmate();
                hc.want = null;
                hc.records = new List<MisconductRecord>();
                (hc.sentenceDays, hc.admittedTick, hc.lastHearingTick, hc.programsPassed, hc.hoursWorked, hc.tension) = (40, 0, -999999, 2, 0, 5f);
                InmateWant pw = Wants.Form(hopeful, hc, RPWantDefOf.RP_Want_Parole);
                if (pw == null)
                {
                    problems0.Add("milestones test: no make parole goal");
                    return;
                }
                Wants.Hourly(hopeful, hc);
                if (pw.status != WantStatus.OnTrack || Count(hopeful, pw, "RP_Chr_WantHalfway") != 1) problems0.Add($"milestones test: calm with two programs {pw.status}, halfway told {Count(hopeful, pw, "RP_Chr_WantHalfway")} times");
                Wants.Notify_ParoleDenied(hopeful, hc);
                Wants.Hourly(hopeful, hc);
                if (Count(hopeful, pw, "RP_Chr_WantHalfway") != 1) problems0.Add($"milestones test: after a refusal halfway was told {Count(hopeful, pw, "RP_Chr_WantHalfway")} times");
                (hc.tension, hc.programsPassed) = (100f, 0);
                Wants.Hourly(hopeful, hc);
                if (pw.status != WantStatus.Slipping || Count(hopeful, pw, "RP_Chr_WantSlipping") != 1) problems0.Add($"milestones test: tense with no programs {pw.status}, slipping told {Count(hopeful, pw, "RP_Chr_WantSlipping")} times");
                Wants.Notify_ParoleDenied(hopeful, hc);
                Wants.Hourly(hopeful, hc);
                Wants.Hourly(hopeful, hc);
                if (Count(hopeful, pw, "RP_Chr_WantSlipping") != 1 || Count(hopeful, pw, "RP_Chr_WantHalfway") != 1)
                    problems0.Add($"milestones test: after a second refusal slipping was told {Count(hopeful, pw, "RP_Chr_WantSlipping")} times and halfway {Count(hopeful, pw, "RP_Chr_WantHalfway")}");

                // Earn a diploma: the sessions sat still count after a missed deadline.
                Pawn student = Throwaway(map, r);
                CompInmate sc = student.Inmate();
                sc.want = null;
                student.skills.GetSkill(SkillDefOf.Intellectual).Level = 0;
                ProgramDef foundation = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation");
                sc.passedPrograms.Remove(foundation);
                sc.programProgress.Remove(foundation);
                InmateWant dw = Wants.Form(student, sc, RPWantDefOf.RP_Want_Diploma);
                if (dw == null)
                {
                    problems0.Add("milestones test: no diploma goal");
                    return;
                }
                sc.programProgress[foundation] = foundation.sessions / 2;
                Wants.Hourly(student, sc);
                dw.deadlineTick = Find.TickManager.TicksGame - 1;
                Wants.Hourly(student, sc);
                Wants.Hourly(student, sc);
                if (dw.renewals != 1 || Count(student, dw, "RP_Chr_WantHalfway") != 1)
                    problems0.Add($"milestones test: a diploma renewed after a timeout with sessions sat: renewals {dw.renewals}, halfway told {Count(student, dw, "RP_Chr_WantHalfway")} times");

                // Get clean: a relapse puts the addiction back, so the way up is walked again and the halfway is told again.
                Pawn addict = Throwaway(map, r);
                ClearAddictions(addict);
                CompInmate ac = addict.Inmate();
                ac.want = null;
                Hediff addiction = addict.health.AddHediff(ChemicalDefOf.Alcohol.addictionHediff);
                addiction.Severity = 0.6f;
                InmateWant cw = Wants.Form(addict, ac, RPWantDefOf.RP_Want_Clean);
                if (cw == null)
                {
                    problems0.Add("milestones test: no get clean goal");
                    return;
                }
                addiction.Severity = 0.3f;
                Wants.Hourly(addict, ac);
                addiction.Severity = 0.5f;
                Wants.Hourly(addict, ac);
                addiction.Severity = 0.2f;
                Wants.Hourly(addict, ac);
                if (cw.renewals != 1 || Count(addict, cw, "RP_Chr_WantHalfway") != 2)
                    problems0.Add($"milestones test: get clean after a relapse: renewals {cw.renewals}, halfway told {Count(addict, cw, "RP_Chr_WantHalfway")} times (expected twice: the way up started over)");
            });
        }

        // ---- B5: a birth does not wipe out a family goal in progress ---------------------------------------------------------

        private static void CheckBirthKeepsFamilyGoal(Map map)
        {
            Guarded("birth keeps family goal", map, r =>
            {
                Pawn homesick = Throwaway(map, r);
                CompInmate fc = homesick.Inmate();
                Relative partner = Relatives.Make("RP_Kin_Partner"), daughter = Relatives.Make("RP_Kin_Daughter");
                fc.relatives.Clear();
                fc.relatives.Add(partner);
                fc.relatives.Add(daughter);
                fc.familyCount = 2;
                fc.want = null;
                InmateWant fw = Wants.Form(homesick, fc, RPWantDefOf.RP_Want_Family, x => x.note = daughter.Label);
                if (fw == null)
                {
                    problems0.Add("birth test: no family goal");
                    return;
                }
                fw.count = 2;
                // The news from home, through the incident's own delivery: a birth.
                IncidentWorker_NewsFromHome.Deliver(homesick, fc, IncidentWorker_NewsFromHome.News.Birth);
                string setAside = "RP_Chr_WantSetAside".Translate(homesick.LabelShort, Wants.Title(fw)).Resolve();
                if (fc.want != fw || fw.status != WantStatus.OnTrack || fw.count != 2 || fw.note != daughter.Label || Lines(r, setAside) != 0)
                    problems0.Add($"birth test: a birth while a family goal was 2 of 3: now \"{(fc.want != null ? Wants.Title(fc.want) : "none")}\" {fc.want?.status} {fc.want?.count} of 3, set-aside lines {Lines(r, setAside)}");
                // The partner leaves (the news worker's path: the goal that hung on them is over, at once, not at the next hourly check).
                fc.want = null;
                InmateWant kin = Wants.Form(homesick, fc, RPWantDefOf.RP_Want_Family, x => x.note = partner.Label);
                if (kin == null)
                {
                    problems0.Add("birth test: no goal about the partner");
                    return;
                }
                IncidentWorker_NewsFromHome.Deliver(homesick, fc, IncidentWorker_NewsFromHome.News.Leaving);
                if (kin.status != WantStatus.Lost || Lines(r, WantLine("RP_Chr_WantLost", homesick, kin, "RP_Want_Reason_Estranged")) != 1)
                    problems0.Add($"news from home test: the partner left and the goal about them is {kin.status}");
                // Somebody in the family dies.
                fc.relatives.RemoveAll(x => x != daughter);
                Relative mother = Relatives.Make("RP_Kin_Mother");
                fc.relatives.Add(mother);
                fc.want = null;
                InmateWant grief = Wants.Form(homesick, fc, RPWantDefOf.RP_Want_Family, x => x.note = mother.Label);
                if (grief == null)
                {
                    problems0.Add("news from home test: no goal about the mother");
                    return;
                }
                fc.relatives.Remove(daughter);
                IncidentWorker_NewsFromHome.Deliver(homesick, fc, IncidentWorker_NewsFromHome.News.Death);
                if (grief.status != WantStatus.Lost || Lines(r, WantLine("RP_Chr_WantLost", homesick, grief, "RP_Want_Reason_Bereaved")) != 1)
                    problems0.Add($"news from home test: the mother died and the goal about them is {grief.status}");
            });
        }

        // ---- balance: what a setback costs --------------------------------------------------------------------------------------

        private static void CheckSetbackCosts(Map map)
        {
            Guarded("setback costs", map, r =>
            {
                Pawn hopeful = Throwaway(map, r);
                CompInmate hc = hopeful.Inmate();
                hc.want = null;
                hc.records = new List<MisconductRecord>();
                (hc.sentenceDays, hc.admittedTick, hc.lastHearingTick, hc.programsPassed, hc.hoursWorked) = (40, 0, -999999, 0, 0);
                InmateWant pw = Wants.Form(hopeful, hc, RPWantDefOf.RP_Want_Parole);
                if (pw == null)
                {
                    problems0.Add("setback costs test: no make parole goal");
                    return;
                }
                bool Thwarted() => hopeful.needs.mood.thoughts.memories.GetFirstMemoryOfDef(RPWantDefOf.RP_WantThwarted) != null;
                // A missed deadline is a disappointment, not a jolt: the thought, and no tension (the prison may be to blame).
                hc.tension = 20f;
                pw.deadlineTick = Find.TickManager.TicksGame - 1;
                Wants.Hourly(hopeful, hc);
                if (pw.renewals != 1 || hc.tension > 20.01f || !Thwarted()) problems0.Add($"setback costs test: a missed deadline: renewals {pw.renewals}, tension 20 -> {hc.tension:F1}, thought {Thwarted()}");
                // A refusal by the board, on its own: the setback in full.
                hc.tension = 20f;
                Wants.Notify_ParoleDenied(hopeful, hc);
                if (hc.tension < 20f + pw.def.tensionOnThwarted - 0.01f) problems0.Add($"setback costs test: a refusal by the board: tension 20 -> {hc.tension:F1}");
                // A hearing at the warden's desk gave its own thought (and its own tension when opposed): the goal's setback adds neither.
                hc.want = null;
                InmateWant heard = Wants.Form(hopeful, hc, RPWantDefOf.RP_Want_Parole);
                hopeful.needs.mood.thoughts.memories.RemoveMemoriesOfDef(RPWantDefOf.RP_WantThwarted);
                hc.tension = 20f;
                Wants.Notify_ParoleDenied(hopeful, hc, heard: true);
                if (heard == null || heard.renewals != 1 || hc.tension > 20.01f || Thwarted())
                    problems0.Add($"setback costs test: a heard refusal: renewals {heard?.renewals}, tension 20 -> {hc.tension:F1}, a second thought {Thwarted()}");
            });
        }

        // ---- the hooks the systems call -----------------------------------------------------------------------------------------

        private static void CheckWantHooks(Map map)
        {
            Guarded("want hooks", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                ProgramDef foundation = DefDatabase<ProgramDef>.GetNamed("RP_FoundationEducation");

                // A real release: the goal is reached as they go; back as a reoffender, a new goal is rolled.
                Pawn leaver = Throwaway(map, r);
                CompInmate lc = leaver.Inmate();
                lc.innocent = true;
                lc.want = null;
                (lc.sentenceDays, lc.admittedTick) = (40, 0);
                InmateWant release = Wants.Form(leaver, lc, RPWantDefOf.RP_Want_Parole);
                if (release == null) problems0.Add("hooks test: no make parole goal for the release");
                else
                {
                    ReleaseSystem.Release(g, leaver, paroled: true, record: false);
                    if (release.status != WantStatus.Fulfilled || Lines(r, WantLine("RP_Chr_WantFulfilled", leaver, release)) != 1)
                        problems0.Add($"hooks test: a parole granted through the release: {release.status}, lines {Lines(r, WantLine("RP_Chr_WantFulfilled", leaver, release))}");
                    if (leaver.Spawned) leaver.DeSpawn();
                    Reoffenders.Return(g, map, leaver, lc, bed: null);
                    if (lc.want == release || lc.want?.def == null || lc.want.Resolved)
                        problems0.Add($"hooks test: a reoffender returned with {(lc.want?.def != null ? Wants.Title(lc.want) + " " + lc.want.status : "no goal")} (a new stay, a new goal)");
                }

                // The board's own hearing denies them.
                Pawn heard = Throwaway(map, r);
                CompInmate dc = heard.Inmate();
                dc.want = null;
                dc.records = new List<MisconductRecord>();
                (dc.sentenceDays, dc.admittedTick, dc.lastHearingTick, dc.programsPassed, dc.hoursWorked, dc.tension) = (40, 0, -999999, 0, 0, 100f);
                InmateWant board = Wants.Form(heard, dc, RPWantDefOf.RP_Want_Parole);
                int refuse = 1;
                float odds = ParoleSystem.ParoleChance(dc);
                for (; refuse < 1000; refuse++)
                {
                    Rand.PushState(refuse);
                    bool granted = Rand.Chance(odds);
                    Rand.PopState();
                    if (!granted) break;
                }
                bool released;
                Rand.PushState(refuse);
                try { released = ParoleSystem.BoardDecides(heard, dc); }
                finally { Rand.PopState(); }
                if (board == null || released || board.renewals != 1 || Lines(r, WantLine("RP_Chr_WantRetry", heard, board, "RP_Want_Reason_ParoleDenied")) != 1)
                    problems0.Add($"hooks test: the board refused them: released {released}, renewals {board?.renewals}");

                // A visit from home counts.
                Pawn visited = Throwaway(map, r);
                CompInmate vc = visited.Inmate();
                vc.want = null;
                Relative daughter = Relatives.Make("RP_Kin_Daughter");
                vc.relatives.Clear();
                vc.relatives.Add(daughter);
                vc.familyCount = 1;
                InmateWant visit = Wants.Form(visited, vc, RPWantDefOf.RP_Want_Family, x => x.note = daughter.Label);
                vc.visitorRelative = daughter.Label;
                VisitationSystem.Complete(visited, null);
                if (visit == null || visit.count != 1) problems0.Add($"hooks test: a visit from the daughter: {visit?.count} of 3");

                // An overdose is a setback on getting clean.
                Pawn addict = Throwaway(map, r);
                ClearAddictions(addict);
                CompInmate ac = addict.Inmate();
                ac.want = null;
                addict.health.AddHediff(ChemicalDefOf.Alcohol.addictionHediff).Severity = 0.5f;
                InmateWant clean = Wants.Form(addict, ac, RPWantDefOf.RP_Want_Clean);
                Overdoses.Notify_Overdose(addict, ac, map);
                if (clean == null || clean.renewals != 1 || Lines(r, WantLine("RP_Chr_WantRetry", addict, clean, "RP_Want_Reason_Overdose")) != 1)
                    problems0.Add($"hooks test: an overdose on a get clean goal: renewals {clean?.renewals}");

                // A failed exam, at the end of a real class.
                Pawn student = Throwaway(map, r);
                CompInmate sc = student.Inmate();
                sc.want = null;
                student.skills.GetSkill(SkillDefOf.Intellectual).Level = 0;
                sc.passedPrograms.Remove(foundation);
                InmateWant diploma = Wants.Form(student, sc, RPWantDefOf.RP_Want_Diploma);
                sc.programProgress[foundation] = foundation.sessions - 1;
                int now = Find.TickManager.TicksGame;
                var session = new ProgramSession { program = foundation, startTick = now - 2000, endTick = now - 1, taughtTicks = 2000, attendees = new List<Pawn> { student }, listenedTicks = new Dictionary<Pawn, int> { [student] = 2000 } };
                float examChance = ProgramSystem.PassChance(student, foundation);
                int failing = 1;
                for (; failing < 1000; failing++)
                {
                    Rand.PushState(failing);
                    bool passes = Rand.Chance(examChance);
                    Rand.PopState();
                    if (!passes) break;
                }
                Rand.PushState(failing);
                try { ProgramSystem.Complete(pm, session); }
                finally { Rand.PopState(); }
                if (diploma == null || sc.passedPrograms.Contains(foundation) || diploma.renewals != 1 || Lines(r, WantLine("RP_Chr_WantRetry", student, diploma, "RP_Want_Reason_ExamFailed")) != 1)
                    problems0.Add($"hooks test: a failed exam at the end of a class: diploma {diploma?.status}, renewals {diploma?.renewals}, passed {sc.passedPrograms.Contains(foundation)} (odds {examChance:P0})");
            });
        }

        // ---- what a goal does to them ---------------------------------------------------------------------------------------------

        private static void CheckWantEffects(Map map)
        {
            Guarded("want effects", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                Pawn calm = Throwaway(map, r), other = Throwaway(map, r);
                CompInmate cc = calm.Inmate(), oc = other.Inmate();
                cc.want = oc.want = null;
                cc.records = new List<MisconductRecord>();
                (cc.sentenceDays, cc.admittedTick, cc.lastHearingTick, cc.programsPassed, cc.tension) = (40, 0, -999999, 2, 5f);
                InmateWant w = Wants.Form(calm, cc, RPWantDefOf.RP_Want_Parole);
                if (w == null)
                {
                    problems0.Add("want effects test: no goal");
                    return;
                }
                // The picker's own weights: serious misconduct x0.8 while the goal is on track, minor as before.
                cc.tension = 100f;
                MisconductUtility.BuildCandidates(calm, cc, null);
                var weighed = DefDatabase<MisconductDef>.AllDefsListForReading.Where(m => MisconductUtility.CandidateWeight(m) > 0f).ToDictionary(m => m, m => MisconductUtility.CandidateWeight(m));
                InmateWant held = cc.want;
                cc.want = null;
                MisconductUtility.BuildCandidates(calm, cc, null);
                cc.want = held;
                MisconductDef serious = weighed.Keys.FirstOrDefault(m => m.severity >= 2 && MisconductUtility.CandidateWeight(m) > 0f), minor = weighed.Keys.FirstOrDefault(m => m.severity < 2 && MisconductUtility.CandidateWeight(m) > 0f);
                if (serious == null || minor == null) problems0.Add("want effects test: no serious or minor misconduct among the candidates");
                else
                {
                    float seriousFree = MisconductUtility.CandidateWeight(serious), minorFree = MisconductUtility.CandidateWeight(minor);
                    if (!Mathf.Approximately(weighed[serious], seriousFree * Wants.AvoidFactor) || !Mathf.Approximately(weighed[minor], minorFree))
                        problems0.Add($"want effects test: the picker's weights with a goal on track: serious {seriousFree:F2} -> {weighed[serious]:F2} (x{Wants.AvoidFactor}), minor {minorFree:F2} -> {weighed[minor]:F2}");
                }
                // The hour's tension: a goal on track calms them a little (and the same hour without it does not).
                float suppressed0 = cc.suppressed;
                cc.tension = 50f;
                TensionUtility.HourlyUpdate(calm, cc, pm);
                float withGoal = cc.tension;
                (cc.tension, cc.suppressed) = (50f, suppressed0);
                cc.want = null;
                TensionUtility.HourlyUpdate(calm, cc, pm);
                float without = cc.tension;
                cc.want = held;
                if (w.status != WantStatus.OnTrack || withGoal >= without - 0.01f) problems0.Add($"want effects test: an hour with a goal on track left tension at {withGoal:F2}, without one {without:F2}");
                // The storyteller's pick: likelier with a goal at a milestone.
                w.progress = 0.8f;
                oc.want = null;
                var worker = new StoryPicker { def = DefDatabase<IncidentDef>.AllDefsListForReading.First(d => d.workerClass == typeof(IncidentWorker_NewsFromHome)) };
                int Share()
                {
                    int picked = 0;
                    Rand.PushState(7);
                    try
                    {
                        for (int i = 0; i < 600; i++)
                        {
                            cc.lastStoryTick = oc.lastStoryTick = -99999999;
                            if (worker.Pick(map, x => x == calm || x == other) == calm) picked++;
                        }
                    }
                    finally { Rand.PopState(); }
                    return picked;
                }
                int near = Share();
                cc.want = null;
                int plain = Share();
                cc.want = held;
                if (near < 360 || plain < 240 || plain > 360) problems0.Add($"want effects test: the storyteller picked the inmate with a goal near done {near}/600 times and without one {plain}/600 (expected about 71% and 50%)");
            });
        }

        // ---- save compatibility edge --------------------------------------------------------------------------------------------

        private static void CheckDiplomaFileWithoutProgram(Map map)
        {
            Guarded("diploma without program", map, r =>
            {
                Pawn student = Throwaway(map, r);
                CompInmate sc = student.Inmate();
                sc.want = null;
                student.skills.GetSkill(SkillDefOf.Intellectual).Level = 0;
                InmateWant dw = Wants.Form(student, sc, RPWantDefOf.RP_Want_Diploma);
                if (dw == null)
                {
                    problems0.Add("diploma without program test: no diploma goal");
                    return;
                }
                // A goal loaded with its program gone from the game: the file must still open.
                dw.program = null;
                string detail;
                try { detail = dw.def.Worker.Detail(student, sc, dw).Resolve(); }
                catch (Exception e)
                {
                    problems0.Add("diploma without program test: the file threw: " + e.GetType().Name);
                    return;
                }
                if (detail == null) problems0.Add("diploma without program test: no detail");
                Wants.Hourly(student, sc);
                if (dw.status != WantStatus.Lost) problems0.Add($"diploma without program test: the goal without its program is {dw.status} after the next check");
            });
        }
    }
}
