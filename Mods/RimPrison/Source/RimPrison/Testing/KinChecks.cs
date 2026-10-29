using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Checks for kin across the wall (<see cref="Kin"/>, gap analysis 5, 2.2): the kinship made on a hire and on an arrival (the roll forced, on throwaway pawns and a pool of their own: the live
    /// prison's real staff and inmates are never made kin), who it is never made with, the officer's letter and what keeping off a wing does to a patrol and a post, the complaint's evidence, the
    /// corruption odds, the subdue hesitation and a death of kin. Each is self-contained and rolled back (<see cref="Rollback"/>); in a run of the plans there is no kin of the game's own (see
    /// <see cref="Kin.ChanceOverride"/>). The saveload plan's seed and snapshot are at the end.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckKin(Map map)
        {
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns);
            CheckKinMade(map);
            CheckKinNever(map);
            CheckKinHirePath(map);
            CheckKinLetter(map);
            CheckKinKeepOff(map);
            CheckKinComplaint(map);
            CheckKinCorruption(map);
            CheckKinSubdue(map);
            CheckKinDeath(map);
            Pawn[] left = LeftBehind(map, pawnsBefore).ToArray();
            if (left.Length > 0) problems0.Add($"kin checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        private static void KinFail(string check, string what) => problems0.Add($"kin ({check}): {what}");

        private static void KinAge(Pawn p, float years)
        {
            long ticks = (long)(years * GenDate.TicksPerYear);
            p.ageTracker.AgeBiologicalTicks = ticks;
            p.ageTracker.AgeChronologicalTicks = ticks;
        }

        /// <summary>The relations a check made, and the parents it generated for siblings (world pawns), gone with the rollback (discarded, not just destroyed: a destroyed pawn goes back to the world pawns).</summary>
        private static void KinClean(Rollback r, params Pawn[] pawns)
        {
            r.Add(() =>
            {
                var parents = new HashSet<Pawn>();
                foreach (Pawn p in pawns)
                    foreach (Pawn par in Kin.ParentsOf(p))
                        if (!par.Spawned && Find.WorldPawns.Contains(par)) parents.Add(par);
                foreach (Pawn p in pawns) p.relations?.ClearAllRelations();
                foreach (Pawn par in parents)
                {
                    par.relations?.ClearAllRelations();
                    PrisonGame.Instance?.story.kinParents.Remove(par);
                    ExDiscard(par);
                }
            });
        }

        private static void KinPool(Rollback r, float? chance, params Pawn[] pool)
        {
            Kin.PoolOverride = pool;
            Kin.ChanceOverride = chance;
            r.Add(() => (Kin.PoolOverride, Kin.ChanceOverride) = (null, null));
        }

        private static void KinShift(Pawn g, bool on)
        {
            for (int h = 0; h < 24; h++) g.timetable.SetAssignment(h, on ? TimeAssignmentDefOf.Work : TimeAssignmentDefOf.Sleep);
        }

        /// <summary>The real guards off duty for the check (timetables put back after), so that no one but the check's own guards can go.</summary>
        private static void KinRealGuardsOff(Map map, Rollback r)
        {
            foreach (Pawn real in map.mapPawns.FreeColonistsSpawned.Where(GuardUtility.IsGuard).ToList())
            {
                List<TimeAssignmentDef> times = real.timetable.times.ToList();
                r.Add(() =>
                {
                    for (int h = 0; h < 24; h++) real.timetable.SetAssignment(h, times[h]);
                });
                KinShift(real, false);
            }
        }

        // ---- the kinship ----------------------------------------------------------------------------------------------

        private static void CheckKinMade(Map map)
        {
            Guarded("kin is made by hand", map, r =>
            {
                const string check = "made";
                // A hire (forced) kin to an inmate: the guard is 24 years older than the inmate, so the inmate can only be their child.
                Pawn inmate = Throwaway(map, r);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                KinAge(inmate, 20f);
                KinAge(guard, 44f);
                KinClean(r, inmate, guard);
                KinPool(r, null, inmate);
                if (Kin.OnHired(guard, map, chance: 0f) != null || Kin.Between(guard, inmate) != null) KinFail(check, "a hire at odds of nothing was made kin");
                var made = Kin.OnHired(guard, map, chance: 1f);
                if (made == null || made.Value.other != inmate || made.Value.kind != KinKind.Child)
                {
                    KinFail(check, $"the forced roll on a hire did not make the inmate the guard's child (made {made?.other?.LabelShort} as {made?.kind})");
                    return;
                }
                if (!inmate.relations.DirectRelationExists(PawnRelationDefOf.Parent, guard) || Kin.Between(guard, inmate) != KinKind.Child || Kin.Between(inmate, guard) != KinKind.Parent
                    || !guard.GetRelations(inmate).Contains(PawnRelationDefOf.Child) || !inmate.GetRelations(guard).Contains(PawnRelationDefOf.Parent))
                    KinFail(check, "the vanilla relation is not there both ways (the inmate's parent is the guard; the guard's relations say child, the inmate's say parent)");
                float gap = guard.ageTracker.AgeChronologicalYearsFloat - inmate.ageTracker.AgeChronologicalYearsFloat;
                if (gap < Kin.MinParentGap || gap > Kin.MaxParentGap) KinFail(check, $"a parent {gap:F1} years older than the child");
                string line = "RP_Chr_KinHired".Translate(Kin.Mention(guard), Kin.Mention(inmate), Kin.Label(inmate, guard)).Resolve();
                if (!r.NewEntries.Any(en => en.text == line)) KinFail(check, $"no chronicle line \"{line}\"");

                // The files name each other: the inmate's file "son/daughter of guard X", the guard's string "father/mother of inmate Y (#n)".
                string kinOfGuard = "RP_KinLine".Translate(Kin.Label(inmate, guard), "RP_KinStaffName".Translate(StaffCareers.Title(guard.GetComp<CompStaff>()), guard.LabelShort)).Resolve();
                if (!Kin.Lines(inmate).Contains(kinOfGuard) || !ITab_InmateFile.StatusLines(inmate, inmate.Inmate(), Find.TickManager.TicksGame).Any(x => x.Item1 == kinOfGuard))
                    KinFail(check, $"the inmate file does not say \"{kinOfGuard}\": {string.Join(" | ", Kin.Lines(inmate))}");
                string kinOfInmate = "RP_KinLine".Translate(Kin.Label(guard, inmate), "RP_KinInmateNumber".Translate(inmate.LabelShort, inmate.Inmate().inmateNumber)).Resolve();
                if (!guard.GetComp<CompStaff>().StatusLines().Contains(kinOfInmate) || !Kin.Lines(guard).Contains(kinOfInmate))
                    KinFail(check, $"the staff string does not say \"{kinOfInmate}\": {guard.GetComp<CompStaff>().StatusLines().Replace("\n", " | ")}");

                // An arrival (forced) kin to a member of staff (the pool is the check's own: a cook 22 years older than the arrival is their parent), and kin to an inmate (siblings only).
                Pawn arrival = Throwaway(map, r);
                Pawn cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook);
                KinAge(arrival, 23f);
                KinAge(cook, 45f);
                KinClean(r, arrival, cook);
                KinPool(r, null, cook);
                var arrived = Kin.OnArrived(arrival, map, chance: 1f);
                if (arrived == null || arrived.Value.other != cook || arrived.Value.kind != KinKind.Parent) KinFail(check, $"the forced roll on an arrival did not make the cook their parent (made {arrived?.other?.LabelShort} as {arrived?.kind})");
                else if (!r.NewEntries.Any(en => en.text == "RP_Chr_KinArrived".Translate(Kin.Mention(arrival), Kin.Mention(cook), Kin.Label(cook, arrival)).Resolve())) KinFail(check, "no chronicle line for the arrival");

                Pawn newcomer = Throwaway(map, r), resident = Throwaway(map, r);
                KinAge(newcomer, 30f);
                KinAge(resident, 33f);
                KinClean(r, newcomer, resident);
                KinPool(r, null, resident);
                var withInmate = Kin.OnArrived(newcomer, map, chance: 1f);
                if (withInmate == null || withInmate.Value.other != resident || withInmate.Value.kind != KinKind.Sibling)
                    KinFail(check, $"an arrival of an age with an inmate's was not made their sibling (made {withInmate?.other?.LabelShort} as {withInmate?.kind}): inmate-to-inmate kin is siblings only");

                // Inmate-to-inmate kin is siblings only: an inmate 30 years older (a parent in age) and one 18 years apart (a half-sibling in age, no sibling) are refused.
                Pawn young = Throwaway(map, r), elder = Throwaway(map, r), halfAge = Throwaway(map, r);
                KinAge(young, 20f);
                KinAge(elder, 50f);
                KinAge(halfAge, 38f);
                KinClean(r, young, elder, halfAge);
                KinPool(r, null, elder, halfAge);
                if (Kin.OnArrived(young, map, chance: 1f) != null || Kin.Between(young, elder) != null || Kin.Between(young, halfAge) != null) KinFail(check, "an inmate was made kin to an inmate other than as a sibling (a parent or a half-sibling)");

                // No chains: a pair who has kin already (a parent, a child) is not made kin to anyone, so vanilla never derives a grandparent, an uncle, a niece or a cousin.
                Pawn grand = Throwaway(map, r), kid = Throwaway(map, r), late = Throwaway(map, r), lateStaff = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook);
                KinAge(grand, 44f);
                KinAge(kid, 20f);
                KinAge(late, 40f);
                KinAge(lateStaff, 45f);
                KinClean(r, grand, kid, late, lateStaff);
                if (!Kin.Link(kid, grand, KinKind.Parent)) { KinFail(check, "the pair for the chain check could not be linked"); return; }
                var chainFits = new List<(KinKind kind, float weight)>();
                foreach (Pawn linked in new[] { grand, kid })
                    foreach (Pawn newbie in new[] { late, lateStaff })
                    {
                        chainFits.Clear();
                        Kin.Fits(newbie, linked, chainFits);
                        if (chainFits.Count != 0) KinFail(check, $"{newbie.LabelShort} (age {newbie.ageTracker.AgeChronologicalYearsFloat:F0}) fits {string.Join("/", chainFits.Select(f => f.kind))} with {linked.LabelShort}, who is {(linked == grand ? "a parent" : "a child")} already: that would chain");
                    }
                KinPool(r, null, grand, kid);
                if (Kin.OnArrived(late, map, chance: 1f) != null || Kin.OnHired(lateStaff, map, chance: 1f) != null || Kin.ParentsOf(late).Any() || late.relations.Children.Any() || Kin.ParentsOf(lateStaff).Any())
                    KinFail(check, "a roll made kin to a pawn who is a parent or a child already (a chain)");
                // The chain would also be one on the newcomer's side: somebody who has kin is not rolled either.
                chainFits.Clear();
                Kin.Fits(grand, late, chainFits);
                if (chainFits.Count != 0) KinFail(check, "a parent already was fitted another kin (from their own side)");

                // Each kind, linked: the relation both ways, the ages and the parents that were made.
                foreach (KinKind kind in new[] { KinKind.Parent, KinKind.Child, KinKind.Sibling, KinKind.HalfSibling })
                {
                    Pawn a = Throwaway(map, r), b = Throwaway(map, r);
                    bool sibs = kind is KinKind.Sibling or KinKind.HalfSibling;
                    KinAge(a, sibs ? 30f : kind == KinKind.Parent ? 20f : 50f);
                    KinAge(b, sibs ? 34f : kind == KinKind.Parent ? 44f : 26f);
                    KinClean(r, a, b);
                    if (!Kin.Link(a, b, kind)) { KinFail(check, $"{kind} could not be linked"); continue; }
                    KinKind? seen = Kin.Between(a, b);
                    KinKind? back = Kin.Between(b, a);
                    KinKind wantBack = kind switch { KinKind.Parent => KinKind.Child, KinKind.Child => KinKind.Parent, _ => kind };
                    PawnRelationDef def = kind switch { KinKind.Parent => PawnRelationDefOf.Parent, KinKind.Child => PawnRelationDefOf.Child, KinKind.Sibling => PawnRelationDefOf.Sibling, _ => PawnRelationDefOf.HalfSibling };
                    PawnRelationDef defBack = kind switch { KinKind.Parent => PawnRelationDefOf.Child, KinKind.Child => PawnRelationDefOf.Parent, KinKind.Sibling => PawnRelationDefOf.Sibling, _ => PawnRelationDefOf.HalfSibling };
                    if (seen != kind || back != wantBack || !a.GetRelations(b).Contains(def) || !b.GetRelations(a).Contains(defBack))
                        KinFail(check, $"{kind}: the relation is not there both ways (a sees b as {seen}, b sees a as {back}; vanilla says {string.Join("/", a.GetRelations(b).Select(x => x.defName))} and {string.Join("/", b.GetRelations(a).Select(x => x.defName))})");
                    if (kind == KinKind.Sibling && a.GetRelations(b).Contains(PawnRelationDefOf.HalfSibling)) KinFail(check, "siblings are half-siblings as well");
                    if (!sibs) continue;
                    var parentsOfA = Kin.ParentsOf(a).ToList();
                    int want = kind == KinKind.Sibling ? 2 : 1;
                    if (parentsOfA.Count != want || !parentsOfA.All(par => Kin.ParentsOf(b).Contains(par) && Find.WorldPawns.Contains(par) && !par.Spawned))
                        KinFail(check, $"{kind}: expected {want} shared parent(s) kept in the world, got {parentsOfA.Count} ({string.Join(", ", parentsOfA.Select(par => par.LabelShort + (Find.WorldPawns.Contains(par) ? "" : " not in the world")))})");
                    foreach (Pawn par in parentsOfA)
                    {
                        // Kept (the world pawn collector would discard parents whose children are all on the map, and clear the relations with them) and listed.
                        if (!Find.WorldPawns.ForcefullyKeptPawns.Contains(par) || !PrisonGame.Instance.story.kinParents.Contains(par)) KinFail(check, $"{kind}: a parent made for siblings is not kept (forcefully kept {Find.WorldPawns.ForcefullyKeptPawns.Contains(par)}, listed {PrisonGame.Instance.story.kinParents.Contains(par)})");
                        float older = Math.Max(a.ageTracker.AgeChronologicalYearsFloat, b.ageTracker.AgeChronologicalYearsFloat), younger = Math.Min(a.ageTracker.AgeChronologicalYearsFloat, b.ageTracker.AgeChronologicalYearsFloat);
                        float age = par.ageTracker.AgeChronologicalYearsFloat;
                        if (age < older + Kin.MinParentGap - 0.01f || age > younger + Kin.MaxParentGap + 0.01f) KinFail(check, $"{kind}: a parent aged {age:F1} for children of {older:F1} and {younger:F1}");
                    }
                    if (kind == KinKind.Sibling && (a.GetMother() == null || a.GetFather() == null || a.GetMother() != b.GetMother() || a.GetFather() != b.GetFather())) KinFail(check, "siblings do not share both a mother and a father");
                }

                // The parents made for siblings are kept while a child of theirs is here, and let go (to the world pawn collector) when none is.
                {
                    Pawn c1 = Throwaway(map, r), c2 = Throwaway(map, r);
                    KinAge(c1, 30f);
                    KinAge(c2, 31f);
                    KinClean(r, c1, c2);
                    if (!Kin.Link(c1, c2, KinKind.Sibling)) { KinFail(check, "siblings could not be linked for the release check"); return; }
                    List<Pawn> made1 = Kin.ParentsOf(c1).ToList();
                    Kin.ReleaseParents(PrisonGame.Instance);
                    if (made1.Any(par => !Find.WorldPawns.ForcefullyKeptPawns.Contains(par))) KinFail(check, "the daily release let go a parent whose children are both on the map");
                    c1.Destroy(DestroyMode.Vanish);
                    Kin.ReleaseParents(PrisonGame.Instance);
                    if (made1.Any(par => !Find.WorldPawns.ForcefullyKeptPawns.Contains(par))) KinFail(check, "the daily release let go a parent with one child still on the map");
                    // The last child is carried (off the ground, on the map in somebody's arms): still here, so the parents stay pinned.
                    // (A guard: the mod lets only staff carry an inmate - Patch_Pawn_CarryTracker_TryStartCarry refuses an inmate carrier.)
                    Pawn carrier = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                    // (Down first: a pawn is carried when it is down, as a guard carries an inmate to bed.)
                    if (!c2.Downed) HealthUtility.DamageUntilDowned(c2, allowBleedingWounds: false);
                    // (The count overload, as vanilla's Toils_Haul.StartCarryThing: it despawns the pawn first; the plain overload adds a spawned pawn and ThingOwner refuses it.)
                    if (carrier.carryTracker.TryStartCarry(c2, 1, reserve: false) == 0) KinFail(check, $"the check's own premise: the carrier could not pick the child up (carrying {carrier.carryTracker.CarriedThing?.ToString() ?? "nothing"}) (downed {c2.Downed}, spawned {c2.Spawned})");
                    else
                    {
                        if (c2.Spawned || c2.MapHeld != map) KinFail(check, $"the check's own premise: the carried child is spawned {c2.Spawned}, held on {c2.MapHeld?.ToString() ?? "no map"}");
                        Kin.ReleaseParents(PrisonGame.Instance);
                        if (made1.Any(par => !Find.WorldPawns.ForcefullyKeptPawns.Contains(par) || !PrisonGame.Instance.story.kinParents.Contains(par))) KinFail(check, "the daily pass let go the parents of a child who is being carried on the map");
                        carrier.carryTracker.TryDropCarriedThing(carrier.Position, ThingPlaceMode.Near, out Thing _);
                    }
                    c2.Destroy(DestroyMode.Vanish);
                    Kin.ReleaseParents(PrisonGame.Instance);
                    if (made1.Any(par => Find.WorldPawns.ForcefullyKeptPawns.Contains(par) || PrisonGame.Instance.story.kinParents.Contains(par))) KinFail(check, "the daily release kept a parent with no child left");
                }

                // The fit of ages: siblings are close, a parent is 16 to 45 years older, nobody is kin across more than that, and a pawn who has a parent is not given siblings.
                Pawn x = Throwaway(map, r), y = Throwaway(map, r);
                KinClean(r, x, y);
                var fits = new List<(KinKind kind, float weight)>();
                KinKind[] FitsAt(float gapYears)
                {
                    float xAge = gapYears < 0f ? 70f : 20f;
                    KinAge(x, xAge);
                    KinAge(y, xAge + gapYears);
                    fits.Clear();
                    Kin.Fits(x, y, fits);
                    return fits.Select(f => f.kind).OrderBy(k => k).ToArray();
                }
                if (!FitsAt(3f).SequenceEqual(new[] { KinKind.Sibling, KinKind.HalfSibling })) KinFail(check, $"a gap of 3 years fits {string.Join("/", FitsAt(3f))}, not sibling and half-sibling");
                if (!FitsAt(17f).SequenceEqual(new[] { KinKind.Parent, KinKind.HalfSibling })) KinFail(check, $"a gap of 17 years fits {string.Join("/", FitsAt(17f))}, not parent and half-sibling");
                if (!FitsAt(-30f).SequenceEqual(new[] { KinKind.Child })) KinFail(check, $"a gap of -30 years fits {string.Join("/", FitsAt(-30f))}, not child");
                if (FitsAt(50f).Length != 0) KinFail(check, $"a gap of 50 years fits {string.Join("/", FitsAt(50f))}");
                if (FitsAt(10f).Length == 0) KinFail(check, "a gap of 10 years fits nothing");
                Pawn mother = Throwaway(map, r, spawn: false);
                mother.gender = Gender.Female;
                KinClean(r, mother);
                y.relations.AddDirectRelation(PawnRelationDefOf.Parent, mother);
                if (FitsAt(3f).Any(k => k is KinKind.Sibling or KinKind.HalfSibling)) KinFail(check, "somebody who has a parent was fitted a sibling");
                RPLog.Message("[RimPrison:test] kin made: a hire and an arrival forced, every kind linked both ways with parents of sensible ages, the file lines name each other");
            });
        }

        private static void CheckKinNever(Map map)
        {
            Guarded("kin is never made with the warden, the state's chief or a visitor", map, r =>
            {
                const string check = "never";
                Pawn inmate = Throwaway(map, r);
                Pawn protectedStaff = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                KinAge(inmate, 20f);
                KinAge(protectedStaff, 44f);
                KinClean(r, inmate, protectedStaff);
                CompStaff sc = protectedStaff.GetComp<CompStaff>();
                StaffRoleDef role0 = sc.role;
                if (!Kin.EligibleStaff(protectedStaff)) KinFail(check, "(control) a guard is not eligible");
                void Never(string who)
                {
                    if (Kin.EligibleStaff(protectedStaff)) KinFail(check, $"{who} is eligible for kin");
                    KinPool(r, null, protectedStaff);
                    if (Kin.OnArrived(inmate, map, chance: 1f) != null || Kin.Between(inmate, protectedStaff) != null) KinFail(check, $"an arrival was made kin to {who}");
                }
                sc.statePosted = true;
                Never("the state's posted chief");
                sc.statePosted = false;
                sc.role = RPWardenDefOf.RP_Warden;
                Never("the warden");
                sc.role = RPStaffRoleDefOf.RP_RiotPolice;
                sc.leaveAtTick = Find.TickManager.TicksGame + 1000;
                Never("a callout officer");
                sc.leaveAtTick = -1;
                sc.role = role0;
                if (!Kin.EligibleStaff(protectedStaff)) KinFail(check, "(control) the guard is not eligible again");
                // Ages that fit nothing: no kin however the roll goes, and an inmate who is not inside is nobody's kin.
                KinAge(protectedStaff, 90f);
                KinPool(r, null, inmate);
                if (Kin.OnHired(protectedStaff, map, chance: 1f) != null || Kin.Between(protectedStaff, inmate) != null) KinFail(check, "a hire 70 years older than the inmate was made kin to them");
                KinAge(protectedStaff, 44f);
                inmate.Inmate().activeFlag = false;
                if (Kin.OnHired(protectedStaff, map, chance: 1f) != null) KinFail(check, "a hire was made kin to an inmate who is not active");
                inmate.Inmate().activeFlag = true;
                RPLog.Message("[RimPrison:test] kin never: the warden, the state's chief, a callout, a mismatch of ages, an inmate who is not inside");
            });
        }

        private static void CheckKinHirePath(Map map)
        {
            Guarded("kin on a real hire", map, r =>
            {
                const string check = "hire";
                PrisonGame g = PrisonGame.Instance;
                StaffRoleDef role = RPSecurityDefOf.RP_Guard;
                if (!Unlocks.IsUnlocked(role)) { KinFail(check, "the check's own premise: the guard post is locked"); return; }
                g.ledger.funds = Math.Max(g.ledger.funds, 100000);
                Pawn inmate = Throwaway(map, r);
                KinAge(inmate, 40f);
                KinClean(r, inmate);
                KinPool(r, 1f, inmate);
                r.Add(() => StaffUtility.CandidateChronologicalAge = null);

                Pawn Hired(Func<bool> hire)
                {
                    var before = new HashSet<Pawn>(map.mapPawns.AllPawns);
                    if (!hire()) { KinFail(check, "the hire was refused"); return null; }
                    Pawn p = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => !before.Contains(x));
                    if (p == null) { KinFail(check, "no new member of staff on the map"); return null; }
                    ExStaffGoes(r, p);
                    KinClean(r, p);
                    return p;
                }

                // Paid for and generated at an age that fits (75 years chronological, 35 more than the inmate: nothing but their parent, so the inmate is the hire's child): the roll is taken (forced), and a guard is asked where they want to stand.
                StaffUtility.CandidateChronologicalAge = 75f;
                Pawn hired = Hired(() => StaffUtility.Hire(map, role));
                StaffUtility.CandidateChronologicalAge = null;
                if (hired != null)
                {
                    if (Math.Abs(hired.ageTracker.AgeChronologicalYearsFloat - 75f) > 0.5f) KinFail(check, $"the seam did not fix the candidate's age (it is {hired.ageTracker.AgeChronologicalYearsFloat:F1})");
                    else if (Kin.Between(hired, inmate) != KinKind.Child) KinFail(check, $"a generated, paid hire of 75 at odds of one was not made the inmate's parent (the inmate is their {Kin.Between(hired, inmate)?.ToString() ?? "nothing"})");
                    else if (r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().All(l => !l.Is("RP_Decision_Kin") || !l.pawns.Contains(hired))) KinFail(check, "the guard hired kin to an inmate got no letter");
                    else if (!r.NewEntries.Any(en => en.text == "RP_Chr_KinHired".Translate(Kin.Mention(hired), Kin.Mention(inmate), Kin.Label(inmate, hired)).Resolve()))
                        KinFail(check, "no chronicle line for the hire");
                }

                // Ages that fit nothing (a cryptosleep-aged hire, 330 years chronological), with an inmate who has no kin yet (the first has one now): paid for and hired all the same, never made kin, no letter.
                inmate = Throwaway(map, r);
                KinAge(inmate, 40f);
                KinClean(r, inmate);
                KinPool(r, 1f, inmate);
                StaffUtility.CandidateChronologicalAge = 330f;
                Pawn asleep = Hired(() => StaffUtility.Hire(map, role));
                StaffUtility.CandidateChronologicalAge = null;
                if (asleep != null)
                {
                    if (asleep.ageTracker.AgeChronologicalYearsFloat < 300f) KinFail(check, $"the seam did not fix the candidate's age (it is {asleep.ageTracker.AgeChronologicalYearsFloat:F1})");
                    else if (Kin.Between(asleep, inmate) != null || Kin.Relatives(asleep).Count != 0) KinFail(check, "a hire of 330 years chronological was made kin to the inmate");
                    else if (asleep.GetComp<CompStaff>().kinAsked || r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_Kin") && l.pawns.Contains(asleep))) KinFail(check, "a hire with no kin was asked where to stand");
                }

                // The facility's own staff (free) and a former inmate hired from the register (a candidate who was made already) are not rolled for, at an age that would fit, with an inmate they could be kin to.
                inmate = Throwaway(map, r);
                KinAge(inmate, 40f);
                KinClean(r, inmate);
                KinPool(r, 1f, inmate);
                StaffUtility.CandidateChronologicalAge = 75f;
                Pawn free = Hired(() => StaffUtility.Hire(map, role, free: true));
                if (free != null && Kin.Between(free, inmate) != null) KinFail(check, "a free hire (the facility's own staff) was made kin");
                Pawn candidate = StaffUtility.GenerateCandidate(role);
                StaffUtility.CandidateChronologicalAge = null;
                Pawn former = candidate == null ? null : Hired(() => StaffUtility.Hire(map, role, free: false, candidate, record: false));
                if (former != null && Kin.Between(former, inmate) != null) KinFail(check, "a candidate hired from the register was made kin");
                RPLog.Message("[RimPrison:test] kin hire: a generated paid hire of a fitting age is made the inmate's parent and the guard is asked; a hire of 330 years is not; a free hire and a register candidate are not rolled for");
            });
        }

        // ---- the officer's request --------------------------------------------------------------------------------------

        private static void CheckKinLetter(Map map)
        {
            Guarded("the officer's request comes once", map, r =>
            {
                const string check = "letter";
                Pawn inmate = Throwaway(map, r), cooksInmate = Throwaway(map, r);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook);
                KinAge(inmate, 20f);
                KinAge(cooksInmate, 20f);
                KinAge(guard, 44f);
                KinAge(cook, 44f);
                KinClean(r, inmate, cooksInmate, guard, cook);
                CompStaff sc = guard.GetComp<CompStaff>();
                KinPool(r, null, inmate);
                if (Kin.OnHired(guard, map, chance: 1f) == null) { KinFail(check, "the check's own premise: no kin made"); return; }
                var letters = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is("RP_Decision_Kin")).ToList();
                if (letters.Count != 1 || !sc.kinAsked || letters[0].pawns.FirstOrDefault() != guard)
                {
                    KinFail(check, $"{letters.Count} request letter(s) for the guard hired kin to an inmate (asked flag {sc.kinAsked}, about {letters.FirstOrDefault()?.pawns.FirstOrDefault()?.LabelShort})");
                    return;
                }
                ChoiceLetter_PrisonDecision letter = letters[0];
                string text = letter.Text.Resolve();
                string[] missing = new[] { guard.LabelShort, inmate.LabelShort, inmate.Inmate().inmateNumber.ToString(), inmate.Inmate().security.label }.Where(w => !text.Contains(w)).ToArray();
                if (missing.Length > 0) KinFail(check, $"the letter does not say {string.Join(" | ", missing)}: {text}");
                if (letter.decision.Worker.Options(letter) is not { Count: 2 } options || options[0] != "RP_Kin_KeepOff" || options[1] != "RP_Kin_LetBe") KinFail(check, "the letter does not offer keep off and let them be");
                // Once: not again from the hire's own path, the daily pass or a second ask.
                if (Kin.AskIfDue(guard) != null) KinFail(check, "the guard was asked a second time");
                Kin.Daily(map);
                if (r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.Is("RP_Decision_Kin")) != 1) KinFail(check, "the daily pass asked again");
                // Unanswered is a let-them-be.
                sc.kinKeepOff = true;
                letter.Lapse();
                Find.LetterStack.RemoveLetter(letter);
                if (sc.kinKeepOff) KinFail(check, "a letter that timed out kept the guard off the wing");
                // A cook with kin inside is not a guard: kin, but nothing to ask (keeping off a wing is a matter of patrols and posts).
                KinPool(r, null, cooksInmate);
                if (Kin.OnHired(cook, map, chance: 1f) == null || Kin.Between(cook, cooksInmate) == null) KinFail(check, "a cook hired was not made kin");
                if (cook.GetComp<CompStaff>().kinAsked || r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_Kin") && l.pawns.Contains(cook))) KinFail(check, "a cook was asked where to stand");
                RPLog.Message("[RimPrison:test] kin letter: once for a guard with the kin named, none for a cook, a timeout lets them be");
            });
        }

        private static void CheckKinKeepOff(Map map)
        {
            Guarded("keeping off a wing", map, r =>
            {
                const string check = "keep off";
                PrisonMap pm = PrisonMap.Of(map);
                Pawn inmate = Throwaway(map, r);
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), other = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                KinAge(inmate, 20f);
                KinAge(guard, 44f);
                KinClean(r, inmate, guard, other);
                KinShift(guard, true);
                KinShift(other, true);
                CompStaff sc = guard.GetComp<CompStaff>();
                PrisonZoneDef wing = DefDatabase<PrisonZoneDef>.AllDefsListForReading.FirstOrDefault(z => z.security == inmate.Inmate().security);
                if (wing == null) { KinFail(check, "the check's own premise: no wing for the inmate's level"); return; }
                KinPool(r, null, inmate);
                if (Kin.OnHired(guard, map, chance: 1f) == null) { KinFail(check, "the check's own premise: no kin made"); return; }
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_Kin"));
                if (letter == null) { KinFail(check, "the check's own premise: no letter"); return; }

                // Painted: the inmate stands in their wing. The patrol's control is built here: the other inmates are calm (nobody outranks the kin's inmate), so the unrestricted guard walks to them.
                IntVec3 cell = inmate.Position;
                PrisonZoneDef old = pm.zones.ZoneAt(cell);
                Paint(pm, cell, wing, r);
                sc.patrolZone = wing;
                foreach (Pawn calm in map.mapPawns.PrisonersOfColonySpawned.Where(x => x != inmate && x.Inmate() is { tension: > 0f }).ToList())
                {
                    CompInmate cc = calm.Inmate();
                    float tension0 = cc.tension;
                    cc.tension = 0f;
                    r.Add(() => cc.tension = tension0);
                }
                RPLog.Message($"[RimPrison:test] kin keep off: {guard.LabelShort}'s kin {inmate.LabelShort} stands in {wing.defName} at {cell}");

                // Before the answer nothing is changed: any zone is allowed, the wing is walked.
                if (!Kin.PatrolAllowed(guard, wing) || Kin.KeptOffWings(guard) != null) KinFail(check, "before the answer the guard is kept off the wing");
                int walked = PatrolsToKin(map, guard, inmate, r);
                if (walked == 0) KinFail(check, $"the control patrols never picked the kin's inmate (awake {inmate.Awake()}, in their own cell {inmate.GetRoom() == InmateUtility.CellOf(inmate)}, deployed zone {SpecialMeasures.DeployedZone(guard)?.defName ?? "none"}): the fixture does not exercise the patrol");

                letter.answered = true;
                PrisonDecisions.Choose(letter, 0);
                Find.LetterStack.RemoveLetter(letter);
                if (!sc.kinKeepOff || sc.patrolZone != null) KinFail(check, $"after keep off the flag is {sc.kinKeepOff} and the patrol zone {sc.patrolZone?.defName ?? "none"} (the wing the kin is in is no patrol zone)");
                if (Kin.PatrolAllowed(guard, wing) || !Kin.PatrolAllowed(guard, RPDefOf.RP_Yard) || !Kin.PatrolAllowed(other, wing))
                    KinFail(check, "the patrol zones offered: the wing of the kin must be off for this guard only, the yard on");
                if (!(Kin.KeptOffWings(guard)?.Contains(inmate.Inmate().security) ?? false)) KinFail(check, "the wings kept off do not include the kin's");
                if (!r.NewEntries.Any(en => en.text == "RP_Chr_KinKeptOff".Translate(Kin.Mention(guard)).Resolve())) KinFail(check, "no chronicle line for keeping off");
                if (!guard.GetComp<CompStaff>().StatusLines().Contains("RP_StaffKeptOff".Translate().Resolve())) KinFail(check, "the staff string does not say the guard is kept off");
                // The daily pass takes a zone that has become the kin's wing away again (the kin moved wings, or the warden set it by another route).
                sc.patrolZone = wing;
                Kin.Daily(map);
                if (sc.patrolZone != null) KinFail(check, "the daily pass left the patrol zone set to the kin's wing");

                // The patrol: the guard's walk among the inmates keeps out of that wing.
                int kept = PatrolsToKin(map, guard, inmate, r);
                if (kept != 0) KinFail(check, $"the guard kept off the wing still patrolled to the kin's inmate {kept} times in 40 (before: {walked})");

                // The patrol's target cell, at the boundary of the wing: the inmate stands outside it (so the patrol does go to them) with the wing's cells on one side, and no cell chosen near them is in the wing.
                Paint(pm, cell, old, r);
                if (!PaintHalfWing(map, pm, cell, wing, r, out List<IntVec3> wingCells)) KinFail(check, "the fixture needs open ground beside the kin's inmate for a wing boundary: none (3 standable cells on one side of them)");
                else
                {
                    int toKin = PatrolsToKin(map, guard, inmate, r);
                    if (toKin == 0) KinFail(check, "at the boundary the guard kept off the wing never walked to the inmate outside it");
                    int inWing = 0, valid = 0, trials = 300;
                    for (int i = 0; i < trials; i++)
                    {
                        IntVec3 dest = WorkGiver_Patrol.Destination(guard, pm, inmate, null);
                        if (!dest.IsValid) continue;
                        valid++;
                        if (Kin.KeepsOffCell(guard, map, dest)) inWing++;
                    }
                    if (valid == 0) KinFail(check, "no patrol target at all near the inmate at the boundary");
                    if (inWing != 0) KinFail(check, $"{inWing} of {trials} patrol targets near the inmate at the boundary were in the wing the guard keeps off");
                    // The control: without the request the same patrol does choose cells in that wing (the cells near the boundary are in reach).
                    sc.kinKeepOff = false;
                    int control = 0;
                    for (int i = 0; i < trials; i++)
                        if (WorkGiver_Patrol.Destination(guard, pm, inmate, null) is { IsValid: true } dest && wingCells.Contains(dest)) control++;
                    sc.kinKeepOff = true;
                    if (control == 0) KinFail(check, $"(control) a guard who did not ask never picked a cell of the wing in {trials} patrol targets: the boundary is not exercised");
                    RPLog.Message($"[RimPrison:test] kin keep off: at a boundary the kept-off guard's {valid} patrol targets were never in the wing (a guard who did not ask: {control} of {trials})");
                }

                // The post: a post in the wing is not manned by the guard, whoever sends them; another guard's all the same. The check puts one up in the wing (cells of the painted half, then the inmate's own).
                Paint(pm, cell, wing, r);
                Thing post = PostIn(map, wingCells ?? new List<IntVec3>(), cell, r);
                if (post == null) KinFail(check, "the check could not put a guard post up in the wing");
                else
                {
                    PrisonZoneDef oldPost = pm.zones.ZoneAt(post.Position);
                    if (oldPost != wing) Paint(pm, post.Position, wing, r);
                    CompGuardPost comp = post.TryGetComp<CompGuardPost>();
                    if (!GuardPosts.KeptOff(guard, comp) || GuardPosts.KeptOff(other, comp)) KinFail(check, "a post in the wing is not out of bounds for the guard kept off it only");
                    if (GuardPosts.CanTake(guard, comp, forced: true) || GuardPosts.FreeAndAble(guard, comp)) KinFail(check, "the guard kept off the wing can man a post in it");
                    if (!GuardPosts.CanTake(other, comp, forced: true))
                        KinFail(check, $"(control) the other guard cannot take the post either (open {comp.OpenNow}, on duty {GuardPosts.OnDuty(other)}, forbidden {post.IsForbidden(other)}, reach {other.CanReserveAndReach(post, PathEndMode.OnCell, Danger.Some)})");
                    sc.kinKeepOff = false;
                    if (GuardPosts.KeptOff(guard, comp)) KinFail(check, "the guard who let them be is kept off the post");
                    sc.kinKeepOff = true;
                }
                RPLog.Message("[RimPrison:test] kin keep off: the wing is no patrol zone, the patrol and its target cells keep out of it and a post in it is not theirs; the flag is in the saveload plan");
            });
        }

        /// <summary>Paints a cell as this zone (null: none), the old one put back by the rollback.</summary>
        private static void Paint(PrisonMap pm, IntVec3 cell, PrisonZoneDef zone, Rollback r)
        {
            PrisonZoneDef old = pm.zones.ZoneAt(cell);
            pm.zones.Set(cell, zone);
            r.Add(() => pm.zones.Set(cell, old));
        }

        /// <summary>
        /// A wing boundary at a cell: the standable cells within 4 on the side of it with the most of them (west, east, south or north of the cell, not the cell itself) painted as the wing. False when no side has 3.
        /// </summary>
        private static bool PaintHalfWing(Map map, PrisonMap pm, IntVec3 cell, PrisonZoneDef wing, Rollback r, out List<IntVec3> painted)
        {
            Func<IntVec3, bool>[] sides = { c => c.x < cell.x, c => c.x > cell.x, c => c.z < cell.z, c => c.z > cell.z };
            painted = null;
            List<IntVec3> best = null;
            foreach (Func<IntVec3, bool> side in sides)
            {
                List<IntVec3> cells = GenRadial.RadialCellsAround(cell, 4f, false).Where(c => c.InBounds(map) && side(c) && c.Standable(map) && pm.zones.ZoneAt(c) != wing).ToList();
                if (best == null || cells.Count > best.Count) best = cells;
            }
            if (best == null || best.Count < 3) return false;
            foreach (IntVec3 c in best) Paint(pm, c, wing, r);
            painted = best;
            return true;
        }

        /// <summary>A guard post standing in the wing: put up at one of the painted cells, or the given one; removed by the rollback. Null when none could be.</summary>
        private static Thing PostIn(Map map, List<IntVec3> cells, IntVec3 fallback, Rollback r)
        {
            foreach (IntVec3 c in cells.Append(fallback))
            {
                if (!c.Standable(map) || c.GetFirstBuilding(map) != null) continue;
                Thing post = ThingMaker.MakeThing(RPPostDefOf.RP_GuardPost);
                post.SetFactionDirect(Faction.OfPlayer);
                Thing spawned = GenSpawn.Spawn(post, c, map, Rot4.North);
                r.Add(() =>
                {
                    if (!spawned.Destroyed) spawned.Destroy();
                });
                return spawned;
            }
            return null;
        }

        /// <summary>How many of 40 patrols of this guard are to the (most tense) kin inmate: the inmate the patrol picks to walk to.</summary>
        private static int PatrolsToKin(Map map, Pawn guard, Pawn inmate, Rollback r)
        {
            CompInmate c = inmate.Inmate();
            float tension0 = c.tension;
            c.tension = 100f;
            r.Add(() => c.tension = tension0);
            PrisonMap pm = PrisonMap.Of(map);
            int picked = 0;
            for (int i = 0; i < 40; i++)
                if (WorkGiver_Patrol.Focus(guard, pm, out _) == inmate) picked++;
            c.tension = tension0;
            return picked;
        }

        // ---- complaints -------------------------------------------------------------------------------------------------

        private static void CheckKinComplaint(Map map)
        {
            Guarded("kin and a complaint's evidence", map, r =>
            {
                const string check = "complaint";
                Pawn witness = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn officer = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), officer2 = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn inmate = Throwaway(map, r), inmate2 = Throwaway(map, r), enemy = Throwaway(map, r);
                KinClean(r, officer, officer2, inmate, inmate2, enemy);
                Complaint Make(Pawn o, Pawn i, bool isTrue) => new Complaint
                {
                    inmateId = i.thingIDNumber, officerId = o.thingIDNumber, inmateName = i.LabelShort, officerName = o.LabelShort, kind = ComplaintKind.Force, isTrue = isTrue,
                    staffSaw = new List<int> { witness.thingIDNumber },
                };
                float Evidence(Complaint cm, out List<string> lines)
                {
                    lines = new List<string>();
                    return ComplaintSystem.Evidence(cm, map, lines);
                }
                // The baselines, with no kin: a staff witness is the only evidence.
                Complaint trueOne = Make(officer, inmate, true), falseOne = Make(officer, inmate, false);
                float trueBase = Evidence(trueOne, out List<string> baseLines), falseBase = Evidence(falseOne, out _);

                // A feud alone (the inmate's enemy is nobody's kin): nothing changes.
                inmate.relations.AddDirectRelation(RPRelationDefOf.RP_Enemy, enemy);
                if (!KinApprox(Evidence(trueOne, out _), trueBase)) KinFail(check, "a feud with nobody of the officer's kin changed the evidence");

                // The officer is kin of the complainant's enemy: a motive (a true complaint is easier to believe), and a line in what was looked at; a false complaint is not.
                Kin.Link(officer, enemy, KinKind.Child);
                float motive = Evidence(trueOne, out List<string> motiveLines);
                if (!KinApprox(motive, trueBase + Kin.MotiveEvidence)) KinFail(check, $"a true complaint against the kin of the complainant's enemy: evidence {motive:F2}, expected {trueBase:F2} + {Kin.MotiveEvidence:F2}");
                string motiveLine = "RP_ComplaintLook_KinMotive".Translate(officer.LabelShort, enemy.LabelShort, inmate.LabelShort).Resolve();
                if (!motiveLines.Contains(motiveLine) || motiveLines.Count != baseLines.Count + 1) KinFail(check, $"the kin line \"{motiveLine}\" is not the one new line in what was looked at ({baseLines.Count} lines before): {string.Join(" | ", motiveLines)}");
                float falseMotive = Evidence(falseOne, out List<string> falseLines);
                if (!KinApprox(falseMotive, falseBase) || !falseLines.Contains(motiveLine)) KinFail(check, $"a false complaint: evidence {falseMotive:F2} (expected the unchanged {falseBase:F2}) and the same line {falseLines.Contains(motiveLine)}");

                // The officer is accused of abusing their own kin: a true complaint is harder to believe.
                Complaint trueOwn = Make(officer2, inmate2, true), falseOwn = Make(officer2, inmate2, false);
                float ownBase = Evidence(trueOwn, out List<string> ownBaseLines), ownFalseBase = Evidence(falseOwn, out _);
                Kin.Link(officer2, inmate2, KinKind.Child);
                float doubt = Evidence(trueOwn, out List<string> ownLines);
                if (!KinApprox(doubt, ownBase - Kin.DoubtEvidence)) KinFail(check, $"a true complaint against the complainant's own kin: evidence {doubt:F2}, expected {ownBase:F2} - {Kin.DoubtEvidence:F2}");
                string ownLine = "RP_ComplaintLook_KinOwn".Translate(officer2.LabelShort, inmate2.LabelShort).Resolve();
                if (!ownLines.Contains(ownLine) || ownLines.Count != ownBaseLines.Count + 1) KinFail(check, $"the kin line \"{ownLine}\" is not the one new line ({ownBaseLines.Count} before): {string.Join(" | ", ownLines)}");
                if (!KinApprox(Evidence(falseOwn, out _), ownFalseBase)) KinFail(check, "a false complaint against the complainant's own kin changed the evidence");
                // (Never below nothing: no witness, no evidence, and the doubt does not take it under zero.)
                trueOwn.staffSaw.Clear();
                if (Evidence(trueOwn, out _) < 0f) KinFail(check, "the evidence went below zero");
                RPLog.Message($"[RimPrison:test] kin complaint: evidence {trueBase:F2} -> {motive:F2} (kin of the enemy), {ownBase:F2} -> {doubt:F2} (own kin), unchanged for a false one and without kin");
            });
        }

        private static bool KinApprox(float a, float b) => UnityEngine.Mathf.Abs(a - b) < 0.001f;

        // ---- corruption -------------------------------------------------------------------------------------------------

        private static void CheckKinCorruption(Map map)
        {
            Guarded("kin and the corruption odds", map, r =>
            {
                const string check = "corruption";
                PrisonGame g = PrisonGame.Instance;
                List<GangDef> gangs = DefDatabase<GangDef>.AllDefsListForReading;
                if (gangs.Count < 2) { KinFail(check, "the check's own premise: fewer than two gangs"); return; }
                Pawn officer = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn fixer = Throwaway(map, r), mate = Throwaway(map, r), outsider = Throwaway(map, r), loner = Throwaway(map, r);
                KinClean(r, officer, fixer, mate, outsider, loner);
                CompStaff sc = officer.GetComp<CompStaff>();
                (fixer.Inmate().gang, fixer.Inmate().gangLeader) = (gangs[0], true);
                (mate.Inmate().gang, mate.Inmate().gangLeader) = (gangs[0], false);
                (outsider.Inmate().gang, outsider.Inmate().gangLeader) = (gangs[1], false);
                loner.Inmate().gang = null;
                sc.integrity = 40f;
                float plain = CorruptionSystem.ApproachOdds(officer, sc, 0f);

                float Lean(Pawn who) => CorruptionSystem.OddsFor(officer, sc, who, 0f) - plain;
                // No kin: the odds are what they were, for anyone.
                if (!KinApprox(Lean(fixer), 0f) || !KinApprox(Lean(mate), 0f)) KinFail(check, "an officer with no kin was weighted by a fixer's gang");
                // Kin in the fixer's gang: the stated weight. Kin in another gang, or in no gang at all (no gang is not a gang): none.
                Kin.Link(officer, mate, KinKind.Child);
                if (!KinApprox(Lean(fixer), Kin.CorruptionLean)) KinFail(check, $"kin in the fixer's gang weighs the odds by {Lean(fixer):F2}, expected {Kin.CorruptionLean:F2}");
                if (!KinApprox(Lean(mate), Kin.CorruptionLean)) KinFail(check, "the fixer who is the kin themselves does not weigh the odds");
                if (!KinApprox(Lean(outsider), 0f)) KinFail(check, "a fixer of another gang than the kin's weighs the odds");
                if (!KinApprox(Lean(loner), 0f)) KinFail(check, "a fixer with no gang weighs the odds");
                (mate.Inmate().gang, fixer.Inmate().gang) = (null, null);
                if (!KinApprox(Lean(fixer), 0f)) KinFail(check, "two inmates with no gang were taken for one gang");
                // A ring: kin and fixer in the same smuggling ring (links by id), not in the same gang.
                (mate.Inmate().gang, fixer.Inmate().gang) = (null, null);
                int arcId0 = g.story.nextArcId;
                var arc = new StoryArc { def = RPRingDefOf.RP_Arc_SmugglingRing, id = g.story.nextArcId++, startTick = Find.TickManager.TicksGame, nextTick = Find.TickManager.TicksGame, ring = new SmugglingRing() };
                arc.pawns.Add(fixer);
                arc.ring.links.Add(new RingLink { role = RingRole.Dealer, pawnId = fixer.thingIDNumber, name = fixer.LabelShort });
                g.story.arcs.Add(arc);
                r.Add(() =>
                {
                    g.story.arcs.Remove(arc);
                    g.story.nextArcId = arcId0;
                });
                if (!KinApprox(Lean(fixer), 0f)) KinFail(check, "a ring the kin is not in weighs the odds");
                arc.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = mate.thingIDNumber, name = mate.LabelShort });
                if (!KinApprox(Lean(fixer), Kin.CorruptionLean)) KinFail(check, $"kin in the fixer's ring weighs the odds by {Lean(fixer):F2}, expected {Kin.CorruptionLean:F2}");
                if (!KinApprox(Lean(outsider), 0f)) KinFail(check, "a fixer outside the ring weighs the odds");

                // The real path: on a seed where the roll falls between the odds without and with the weight, the officer refuses a fixer who does not hold their kin and says yes to one who does.
                (mate.Inmate().gang, fixer.Inmate().gang) = (null, null);
                arc.ring.links.RemoveAll(l => l.role == RingRole.Courier);
                sc.integrity = 65f;
                float baseAt65 = CorruptionSystem.ApproachOdds(officer, sc, 0f);
                float integrity = 65f - 50f * (0.4f - baseAt65);
                int seed = -1;
                for (int s = 1; s < 5000 && seed < 0; s++)
                {
                    Rand.PushState(s);
                    float v = Rand.Value;
                    Rand.PopState();
                    if (v >= 0.4f && v < 0.4f + Kin.CorruptionLean - 0.01f) seed = s;
                }
                if (seed < 0) { KinFail(check, "the check's own premise: no seed between the odds"); return; }
                void Reset()
                {
                    (sc.corrupt, sc.patron, sc.patronGang, sc.integrity, sc.lastApproachTick, sc.suspicion) = (false, null, null, integrity, -999999, 0f);
                    sc.evidence.Clear();
                }
                bool Says(Pawn who)
                {
                    Reset();
                    Rand.PushState(seed);
                    try
                    {
                        return CorruptionSystem.Approach(officer, sc, who, 0f);
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                }
                if (Says(loner)) KinFail(check, "(control) a fixer who holds no kin of theirs was said yes to on the seed below the weighted odds");
                if (!Says(mate)) KinFail(check, "a fixer who is the officer's kin was not said yes to on a seed that falls under the weighted odds");
                Reset();
                RPLog.Message($"[RimPrison:test] kin corruption: +{Kin.CorruptionLean:F2} to the odds for a fixer whose gang, ring or self holds the officer's kin, nothing for any other");
            });
        }

        // ---- the subdue --------------------------------------------------------------------------------------------------

        private static void CheckKinSubdue(Map map)
        {
            Guarded("kin hesitates to restrain kin", map, r =>
            {
                const string check = "subdue";
                float chance0 = Kin.HesitateChance;
                r.Add(() => Kin.HesitateChance = chance0);
                KinRealGuardsOff(map, r);
                Pawn inmate = Throwaway(map, r);
                Pawn kinGuard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), other = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                KinClean(r, inmate, kinGuard, other);
                KinShift(kinGuard, true);
                KinShift(other, true);
                CompInmate c = inmate.Inmate();
                c.pendingPunishmentHours = 4;
                if (!GuardUtility.NeedsSubduing(inmate)) { KinFail(check, "the check's own premise: an inmate owing a punishment is not to be restrained"); return; }
                Kin.Link(inmate, kinGuard, KinKind.Parent);
                if (!GuardPosts.OnDuty(other) || !GuardPosts.OnDuty(kinGuard)) { KinFail(check, $"the check's own premise: the guards are not on duty (other {GuardPosts.OnDuty(other)}, kin {GuardPosts.OnDuty(kinGuard)})"); return; }

                // Sure to hesitate: with the other guard free, the kin guard does not go, the other does, and an order from the warden is not put off.
                Kin.HesitateChance = 1f;
                if (GuardUtility.CanRespondTo(kinGuard, inmate) || !GuardUtility.CanRespondTo(other, inmate) || !GuardUtility.CanRespondTo(kinGuard, inmate, forced: true))
                    KinFail(check, $"with two guards free: the kin guard can respond {GuardUtility.CanRespondTo(kinGuard, inmate)} (should not), the other {GuardUtility.CanRespondTo(other, inmate)}, the kin guard ordered to {GuardUtility.CanRespondTo(kinGuard, inmate, forced: true)}");
                // An alternative who could not take the job does not make the relative hesitate: the Warden work off (priority 0), or no hands (the work giver requires Manipulation).
                if (!Kin.CouldTakeResponse(other)) KinFail(check, "(control) the other guard could not take the response job to begin with");
                int priority0 = other.workSettings.GetPriority(WorkTypeDefOf.Warden);
                r.Add(() => other.workSettings.SetPriority(WorkTypeDefOf.Warden, priority0));
                other.workSettings.SetPriority(WorkTypeDefOf.Warden, 0);
                if (Kin.CouldTakeResponse(other) || !GuardUtility.CanRespondTo(kinGuard, inmate)) KinFail(check, "an alternative guard with the Warden work off made the kin guard hesitate");
                other.workSettings.SetPriority(WorkTypeDefOf.Warden, priority0 > 0 ? priority0 : 3);
                if (!GuardUtility.CanRespondTo(other, inmate) || GuardUtility.CanRespondTo(kinGuard, inmate)) KinFail(check, "with the Warden work on again the other guard did not take the job from the kin guard");
                List<Hediff> arms = new List<Hediff>();
                var armParts = new List<BodyPartRecord>();
                foreach (BodyPartRecord part in other.RaceProps.body.GetPartsWithTag(BodyPartTagDefOf.ManipulationLimbCore))
                {
                    // (A throwaway can be made with an arm already gone or a prosthetic one, which kept manipulation above 0: natural arms first.)
                    BodyPartRecord top = part;
                    while (top.parent != null && other.health.hediffSet.PartIsMissing(top.parent)) top = top.parent;
                    if (other.health.hediffSet.PartIsMissing(top) || other.health.hediffSet.HasDirectlyAddedPartFor(top)) other.health.RestorePart(top);
                    Hediff lost = HediffMaker.MakeHediff(HediffDefOf.MissingBodyPart, other, part);
                    other.health.AddHediff(lost, part);
                    arms.Add(lost);
                    armParts.Add(part);
                }
                // (Vanilla's RestorePart, not removing the hediff: the capacities are cached, and a removed missing part left the guard counted without hands.)
                void GiveHandsBack()
                {
                    foreach (BodyPartRecord part in armParts)
                        if (other.health.hediffSet.PartIsMissing(part)) other.health.RestorePart(part);
                }
                r.Add(GiveHandsBack);
                // (Manipulation can also come from a hediff's offset - a drug, an implant - and a guard with that is capable by vanilla's own test: those go too, for the throwaway's last minutes.)
                foreach (Hediff boost in other.health.hediffSet.hediffs.Where(h => h is not Hediff_MissingPart && h.CapMods != null && h.CapMods.Any(cm => cm.capacity == PawnCapacityDefOf.Manipulation && cm.offset > 0f)).ToList())
                    other.health.RemoveHediff(boost);
                float manipulation = other.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
                if (arms.Count > 0 && manipulation > 0f)
                {
                    RPLog.Message($"[RimPrison:test] kin subdue: not checked, the guard without arms keeps manipulation {manipulation:F2} (hediffs: {string.Join(", ", other.health.hediffSet.hediffs.Select(h => h.def.defName + (h.Part != null ? "@" + h.Part.def.defName : "")))})");
                }
                else if (arms.Count == 0) KinFail(check, "the check's own premise: no arms to take off the other guard");
                else if (!GuardPosts.OnDuty(other)) KinFail(check, "the check's own premise: the guard without hands is off duty (downed or sent home)");
                else if (Kin.CouldTakeResponse(other)) KinFail(check, $"a guard with no hands (manipulation {other.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation):F2}) counts as one who could take the response job");
                else if (!GuardUtility.CanRespondTo(kinGuard, inmate)) KinFail(check, "the kin guard hesitated when the only other guard has no hands");
                GiveHandsBack();
                if (!Kin.CouldTakeResponse(other)) KinFail(check, "the guard did not get their hands back (the check's cleanup)");
                // Alone: the other guard is off duty, and the kin guard goes.
                KinShift(other, false);
                if (!GuardUtility.CanRespondTo(kinGuard, inmate)) KinFail(check, "the only guard on duty hesitates to restrain their kin (an emergency is never refused)");
                KinShift(other, true);
                // A guard who is the kin of the inmate as well is no one to hand it to.
                Kin.Link(inmate, other, KinKind.Parent);
                if (!GuardUtility.CanRespondTo(kinGuard, inmate) || !GuardUtility.CanRespondTo(other, inmate)) KinFail(check, "two guards who are both the inmate's kin hesitate for each other");
                other.relations.ClearAllRelations();
                kinGuard.relations.ClearAllRelations();
                Kin.Link(inmate, kinGuard, KinKind.Parent);

                // A chance, not a refusal: at the stated chance the kin guard puts it off in most windows of 250 ticks but not all; the other never does; nobody does when no guard is kin.
                Kin.HesitateChance = chance0;
                int windows = 400, hesitated = 0, otherHesitated = 0;
                for (int w = 0; w < windows; w++)
                {
                    if (Kin.Hesitates(kinGuard, inmate, w)) hesitated++;
                    if (Kin.Hesitates(other, inmate, w)) otherHesitated++;
                }
                if (hesitated < windows * (chance0 - 0.12f) || hesitated > windows * (chance0 + 0.12f) || hesitated == windows) KinFail(check, $"the kin guard hesitated in {hesitated} of {windows} windows at a chance of {chance0:F2}");
                if (otherHesitated != 0) KinFail(check, $"a guard who is no kin hesitated in {otherHesitated} windows");
                RPLog.Message($"[RimPrison:test] kin subdue: the kin guard hesitated in {hesitated} of {windows} windows ({chance0:F2}), goes when alone or ordered, the other never");
            });
        }

        // ---- a death of kin ---------------------------------------------------------------------------------------------

        private static void CheckKinDeath(Map map)
        {
            Guarded("a death of kin", map, r =>
            {
                const string check = "death";
                // An inmate dies in custody: the officer's brother, and (executed) the officer's son. Vanilla's thought is there with the relation (my son died), and the chronicle has the colleague's line.
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn dead = Throwaway(map, r);
                KinAge(guard, 44f);
                KinAge(dead, 20f);
                KinClean(r, guard, dead);
                Kin.Link(dead, guard, KinKind.Parent);
                dead.relations.everSeenByPlayer = true;
                PawnRelationDef relation = guard.GetMostImportantRelation(dead);
                ThoughtDef thought = relation?.GetGenderSpecificDiedThought(dead);
                if (thought == null) { KinFail(check, "the check's own premise: vanilla has no died thought for the relation"); return; }
                var individual = new List<IndividualThoughtToAdd>();
                PawnDiedOrDownedThoughtsUtility.GetThoughts(dead, null, PawnDiedOrDownedThoughtsKind.Died, individual, new List<ThoughtToAddToAll>());
                if (!individual.Any(t => t.addTo == guard && t.thought.def == thought)) KinFail(check, $"vanilla gives the guard no {thought.defName} for a death of their kin (thoughts: {string.Join(", ", individual.Select(t => t.addTo.LabelShort + ":" + t.thought.def.defName))})");

                Kin.Notify_Died(dead);
                string died = "RP_Chr_KinDied".Translate(Kin.Mention(guard), Kin.Label(dead, guard), dead.LabelShort).Resolve();
                if (!r.NewEntries.Any(en => en.text == died)) KinFail(check, $"no colleague line \"{died}\" for a death in custody");
                if (r.NewEntries.Count(en => en.text == died) != 1) KinFail(check, "the colleague line was written more than once");

                // Executed, through the real execution: the line, and vanilla's memory on the officer; their mental break that night is not barred.
                Pawn condemned = Throwaway(map, r);
                KinAge(condemned, 21f);
                KinClean(r, condemned);
                Kin.Link(condemned, guard, KinKind.Parent);
                condemned.relations.everSeenByPlayer = true;
                ThoughtDef executedThought = guard.GetMostImportantRelation(condemned)?.GetGenderSpecificDiedThought(condemned);
                string name = condemned.LabelShort;
                string executedLine = "RP_Chr_KinExecuted".Translate(Kin.Mention(guard), Kin.Label(condemned, guard), name).Resolve();
                DeathRowSystem.Execute(condemned, null);
                if (!condemned.Dead) { KinFail(check, "the check's own premise: the execution did not kill"); return; }
                if (!r.NewEntries.Any(en => en.text == executedLine)) KinFail(check, $"no colleague line \"{executedLine}\" for an execution");
                if (executedThought == null) KinFail(check, "the check's own premise: vanilla has no died thought for the executed kin");
                else if (guard.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(executedThought) == null && !ThoughtUtility.ThoughtNullified(guard, executedThought))
                    KinFail(check, $"the guard has no {executedThought.defName} memory after the execution of their kin, and nothing in their traits or genes nullifies it ({string.Join(", ", guard.story.traits.allTraits.Select(t => t.def.defName))})");
                bool can = true;
                Patch_MentalBreakWorker_BreakCanOccur_Warden.Postfix(RPVanillaDefOf.GiveUpExit.Worker, guard, ref can);
                if (!can) KinFail(check, "a member of staff with an executed kin cannot have a mental break (the walk off the map is barred)");
                bool wild = true;
                Patch_MentalBreakWorker_BreakCanOccur_Warden.Postfix(RPVanillaDefOf.RunWild.Worker, guard, ref wild);
                if (wild) KinFail(check, "(control) a member of staff can run wild: the patch that bars it for staff is not applied");

                // A member of staff dies: their inmate kin's line.
                Pawn son = Throwaway(map, r);
                Pawn parent = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                KinClean(r, son, parent);
                Kin.Link(son, parent, KinKind.Parent);
                Kin.Notify_Died(parent);
                string staffLine = "RP_Chr_KinStaffDied".Translate(Kin.Mention(son), Kin.Label(parent, son), parent.LabelShort).Resolve();
                if (!r.NewEntries.Any(en => en.text == staffLine)) KinFail(check, $"no line \"{staffLine}\" for a member of staff's death");
                // No kin, no line.
                int entries = r.NewEntries.Count();
                Kin.Notify_Died(Throwaway(map, r));
                if (r.NewEntries.Count() != entries) KinFail(check, "a death with no kin wrote a colleague line");
                RPLog.Message("[RimPrison:test] kin death: the colleague's line for a death, an execution and a member of staff's death; vanilla's thought is there; the officer's break is not barred");
            });
        }

        // ---- the saveload plan -------------------------------------------------------------------------------------------

        /// <summary>Everything kin keeps in a save as one comparable string: the parent relations of every pawn here (by thing id: the child, the parent, the parent's sex, whether they are a world pawn, forcefully kept and listed as a made parent, their age) and the guards' two flags.</summary>
        private static string KinDescribe(PrisonGame g, Map map)
        {
            IEnumerable<Pawn> everyone = map.mapPawns.AllPawns.Concat(g.OffSite).Distinct().Where(p => p.relations != null).OrderBy(p => p.thingIDNumber);
            string pairs = string.Join(",", everyone.SelectMany(p => Kin.ParentsOf(p).OrderBy(par => par.thingIDNumber)
                .Select(par => $"{p.thingIDNumber}>{par.thingIDNumber}:{par.gender}:{Find.WorldPawns.Contains(par)}{Find.WorldPawns.ForcefullyKeptPawns.Contains(par)}{g.story.kinParents.Contains(par)}:{par.ageTracker.AgeChronologicalYearsFloat:F0}")));
            string flags = string.Join(",", StaffCommute.AllStaff(map).Where(s => s.GetComp<CompStaff>() is { kinAsked: true } or { kinKeepOff: true }).OrderBy(s => s.thingIDNumber)
                .Select(s => $"{s.thingIDNumber}:{s.GetComp<CompStaff>().kinAsked}{s.GetComp<CompStaff>().kinKeepOff}"));
            return $"{pairs}/{flags}";
        }

        /// <summary>A guard who is the parent of an inmate, asked and kept off the wing, and two inmates who are siblings (their parents are world pawns).</summary>
        private static void SeedKinForReload(Map map)
        {
            if (StaffCommute.AllStaff(map).FirstOrDefault(s => s.GetComp<CompStaff>() is { Protected: false, role.guardDuties: true } sc && Kin.EligibleStaff(s)) is not { } guard)
            {
                problems0.Add("saveload: no guard to make kin of an inmate");
                return;
            }
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => Kin.IsInmateInside(p) && Kin.Between(p, guard) == null && !Kin.Relatives(p).Any()).Take(3).ToList();
            if (inmates.Count < 3)
            {
                problems0.Add("saveload: fewer than three inmates to make kin");
                return;
            }
            Kin.Link(inmates[0], guard, KinKind.Parent);
            CompStaff sc = guard.GetComp<CompStaff>();
            (sc.kinAsked, sc.kinKeepOff) = (true, true);
            if (!Kin.Link(inmates[1], inmates[2], KinKind.Sibling)) problems0.Add("saveload: the siblings' parents could not be made");
        }
    }
}
