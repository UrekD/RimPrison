using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>What the other pawn is to this one (see <see cref="Kin.Between"/>).</summary>
    public enum KinKind : byte
    {
        Parent,
        Child,
        Sibling,
        HalfSibling,
    }

    /// <summary>
    /// Kin across the wall (gap analysis 5, 2.2): about one hire in twelve is kin to an inmate, about one arrival in twelve to a member of staff, and now and then an arrival to another inmate. The
    /// kinship is made by hand after generation, as vanilla relations, so the Social tab, opinion and the family thoughts on a death come for free (the generator's own relations are never asked
    /// for: they would tie the newcomer to the warden or to anyone). Only <c>Parent</c> is a direct relation in vanilla (Child, Sibling, HalfSibling and Cousin are implied by it): a parent and a
    /// child is one relation; siblings share a mother and a father, half-siblings one of them, and those parents are made here as world pawns and kept (forcefully, and listed in
    /// <see cref="StorySystem.kinParents"/>) while a child of theirs is here: the world pawn collector only keeps what a world pawn is related to, would discard parents whose children are all on the map and
    /// clear the relations with them, and the siblings would stop being siblings. <see cref="Daily"/> lets them go when no child is left. Cousins would need four such pawns and are not made, and there are no chains (a link is only made between pawns with no parents or children yet; inmate-to-inmate kin is siblings only). Never the warden and never the state's posted chief; ages make sense (a parent is
    /// 16 to 45 years older). Kinship is public: the inmate file and the staff inspect string say so. The effects reuse hooks that were already there (see the callers named below).
    /// </summary>
    public static class Kin
    {
        /// <summary>Per hire (kin to an inmate), per arrival (kin to a member of staff), per arrival (kin to another inmate).</summary>
        public const float HireChance = 1f / 12f, ArrivalChance = 1f / 12f, InmateKinChance = 1f / 30f;

        /// <summary>A parent is this many years older than the child, at least and at most; siblings are this close in age (half-siblings a little further).</summary>
        public const float MinParentGap = 16f, MaxParentGap = 45f, MaxSiblingGap = 15f, MaxHalfSiblingGap = 20f;

        /// <summary>Added to the odds that an officer says yes to a fixer whose gang or ring holds their kin (<see cref="CorruptionSystem.OddsFor"/>; the odds run -0.3 to 1.3 before the clamp, so this is modest).</summary>
        public const float CorruptionLean = 0.15f;

        /// <summary>
        /// What a complaint's evidence gains when the officer is kin of the complainant's enemy (they had a motive: a true complaint is easier to believe) and loses when it is the complainant's own
        /// kin they are accused of abusing (an officer is not thought to do that: a true complaint is harder to believe). A false complaint is neither more nor less likely to be found out for it
        /// (see <see cref="ComplaintSystem.Evidence"/>).
        /// </summary>
        public const float MotiveEvidence = 0.15f, DoubtEvidence = 0.15f;

        /// <summary>
        /// A guard asked to restrain their own kin hesitates when another guard can go: in this share of windows of <see cref="HesitateWindow"/> ticks they do not take the job (a seeded roll per
        /// guard, inmate and window, so the work scan does not flicker), and in the rest they do. Never when nobody else can go, and never when the warden ordered it. A field so that a check can set it.
        /// </summary>
        public static float HesitateChance = 0.75f;

        public const int HesitateWindow = 250;

        private const float SiblingWeight = 3f, HalfSiblingWeight = 1f, ParentChildWeight = 1.5f;

        /// <summary>A check forces the rolls (1: always, 0: never). In an autotest run there is no kin unless a check asks for it: the plans' own hires and arrivals are as they were.</summary>
        public static float? ChanceOverride;

        /// <summary>A check's own pool to make kin with (still filtered as the map's would be: only staff who are eligible, only inmates inside), instead of the map's staff and inmates (the live prison's real ones are not touched).</summary>
        public static IEnumerable<Pawn> PoolOverride;

        private static float Chance(float normal) => AutoTest.Active ? ChanceOverride ?? 0f : ChanceOverride ?? normal;

        // ---- who is kin to whom ------------------------------------------------------------------------------------------

        private static float Age(Pawn p) => p.ageTracker.AgeChronologicalYearsFloat;

        /// <summary>Employed here, on site or at home, and not the warden, the state's chief, a callout or a visiting official: the staff kin may be made with.</summary>
        public static bool EligibleStaff(Pawn p) =>
            p is { Dead: false, Destroyed: false } && p.RaceProps.Humanlike && p.Faction == Faction.OfPlayer && !p.IsPrisonerOfColony
            && p.GetComp<CompStaff>() is { role: not null, Protected: false, leaveAtTick: < 0 } sc && !sc.role.visitor;

        /// <summary>On the staff at all (the warden and the state's chief too: they are kin to nobody, but a death in their family is as much theirs).</summary>
        public static bool IsStaffInside(Pawn p) => p is { Dead: false, Destroyed: false } && p.Faction == Faction.OfPlayer && !p.IsPrisonerOfColony && p.GetComp<CompStaff>() is { role: not null };

        public static bool IsInmateInside(Pawn p) => p is { Dead: false, Destroyed: false } && p.IsPrisonerOfColony && p.Inmate() is { activeFlag: true };

        /// <summary>
        /// What <paramref name="b"/> is to <paramref name="a"/>, by the direct <c>Parent</c> relations alone (null: none of the four). Cheap: it is asked of every guard and inmate by the work scans.
        /// </summary>
        public static KinKind? Between(Pawn a, Pawn b)
        {
            if (a == b || a?.relations == null || b?.relations == null) return null;
            List<DirectPawnRelation> ra = a.relations.DirectRelations, rb = b.relations.DirectRelations;
            int shared = 0;
            for (int i = 0; i < ra.Count; i++)
            {
                if (ra[i].def != PawnRelationDefOf.Parent || ra[i].otherPawn is not { } parent) continue;
                if (parent == b) return KinKind.Parent;
                for (int j = 0; j < rb.Count; j++)
                    if (rb[j].def == PawnRelationDefOf.Parent && rb[j].otherPawn == parent) shared++;
            }
            for (int j = 0; j < rb.Count; j++)
                if (rb[j].def == PawnRelationDefOf.Parent && rb[j].otherPawn == a) return KinKind.Child;
            return shared >= 2 ? KinKind.Sibling : shared == 1 ? KinKind.HalfSibling : null;
        }

        public static IEnumerable<Pawn> ParentsOf(Pawn p)
        {
            if (p?.relations == null) yield break;
            List<DirectPawnRelation> rel = p.relations.DirectRelations;
            for (int i = 0; i < rel.Count; i++)
                if (rel[i].def == PawnRelationDefOf.Parent && rel[i].otherPawn != null) yield return rel[i].otherPawn;
        }

        /// <summary>The kin of this pawn, anywhere (parents, children, siblings and half-siblings), with what each is to them.</summary>
        public static List<(Pawn other, KinKind kind)> Relatives(Pawn p)
        {
            var list = new List<(Pawn, KinKind)>();
            if (p?.relations == null) return list;
            var seen = new HashSet<Pawn> { p };
            foreach (Pawn parent in ParentsOf(p))
                if (seen.Add(parent)) list.Add((parent, KinKind.Parent));
            foreach (Pawn child in p.relations.Children)
                if (seen.Add(child)) list.Add((child, KinKind.Child));
            foreach (Pawn parent in ParentsOf(p).ToList())
                foreach (Pawn sibling in parent.relations.Children)
                    if (seen.Add(sibling) && Between(p, sibling) is { } kind) list.Add((sibling, kind));
            return list;
        }

        /// <summary>The kin of this pawn who are inmates now.</summary>
        public static List<Pawn> InmatesOf(Pawn p) => Relatives(p).Where(r => IsInmateInside(r.other)).Select(r => r.other).ToList();

        /// <summary>The kin of this pawn who are on the staff.</summary>
        public static List<Pawn> StaffOf(Pawn p) => Relatives(p).Where(r => IsStaffInside(r.other)).Select(r => r.other).ToList();

        /// <summary>What <paramref name="who"/> is to <paramref name="to"/> in the game's own words (brother, mother, daughter): "brother of guard Hale" is Label(the inmate, the guard).</summary>
        public static string Label(Pawn who, Pawn to) => to.GetMostImportantRelation(who)?.GetGenderSpecificLabel(who) ?? "RP_KinKinsfolk".Translate().ToString();

        /// <summary>"guard Hale" or "inmate Jones", for a line in the chronicle.</summary>
        public static string Mention(Pawn p) =>
            p.GetComp<CompStaff>() is { role: not null } sc ? "RP_KinStaffName".Translate(StaffCareers.Title(sc), p.LabelShort).Resolve() : "RP_KinInmateName".Translate(p.LabelShort).Resolve();

        /// <summary>"guard Hale" or "inmate Jones (#412)", for the file and the inspect string.</summary>
        public static string Describe(Pawn p) =>
            p.GetComp<CompStaff>() is { role: not null } sc ? "RP_KinStaffName".Translate(StaffCareers.Title(sc), p.LabelShort).Resolve()
                : "RP_KinInmateNumber".Translate(p.LabelShort, p.Inmate()?.inmateNumber ?? 0).Resolve();

        /// <summary>Kin inside, as the inmate file and the staff inspect string say it: "brother of guard Hale".</summary>
        public static IEnumerable<string> Lines(Pawn p)
        {
            foreach ((Pawn other, KinKind _) in Relatives(p))
                if (IsStaffInside(other) || IsInmateInside(other))
                    yield return "RP_KinLine".Translate(Label(p, other), Describe(other)).Resolve();
        }

        // ---- making it -------------------------------------------------------------------------------------------------------

        /// <summary>A pawn with no parent and no child of their own yet (see <see cref="Fits"/>).</summary>
        private static bool NoFamily(Pawn p) => !ParentsOf(p).Any() && !p.relations.Children.Any();

        /// <summary>
        /// The kinds <paramref name="n"/> could be to <paramref name="o"/> (what o is to n), with their weights, by ages. No chains: a link is only made between two pawns who have no parents and no children yet,
        /// so vanilla never derives a grandparent, an uncle, a niece or a cousin from it (kin is a pair, or siblings with the parents made for them).
        /// </summary>
        public static void Fits(Pawn n, Pawn o, List<(KinKind kind, float weight)> into)
        {
            if (n == o || n.relations == null || o.relations == null || !n.RaceProps.Humanlike || !o.RaceProps.Humanlike || Between(n, o) != null || !NoFamily(n) || !NoFamily(o)) return;
            float gap = Age(o) - Age(n);
            bool maleOrFemaleO = o.gender is Gender.Male or Gender.Female, maleOrFemaleN = n.gender is Gender.Male or Gender.Female;
            if (maleOrFemaleO && gap >= MinParentGap && gap <= MaxParentGap) into.Add((KinKind.Parent, ParentChildWeight));
            if (maleOrFemaleN && -gap >= MinParentGap && -gap <= MaxParentGap) into.Add((KinKind.Child, ParentChildWeight));
            float apart = Mathf.Abs(gap);
            if (apart <= MaxHalfSiblingGap)
            {
                if (apart <= MaxSiblingGap) into.Add((KinKind.Sibling, SiblingWeight));
                into.Add((KinKind.HalfSibling, HalfSiblingWeight));
            }
        }

        /// <summary>
        /// A parent for two siblings who have none: a world pawn of the right age (16 to 45 years older than the older, no more than that older than the younger), made without any relation of its own,
        /// kept for as long as a child of theirs is here (see <see cref="ReleaseParents"/>). Null when it could not be made.
        /// </summary>
        private static Pawn MakeParent(Gender gender, Pawn a, Pawn b)
        {
            float older = Mathf.Max(Age(a), Age(b)), younger = Mathf.Min(Age(a), Age(b));
            float min = older + MinParentGap;
            float max = Mathf.Max(min, Mathf.Min(younger + MaxParentGap, older + 30f));
            float chronological = Rand.Range(min, max);
            float biological = Rand.Range(16f, Mathf.Max(16.5f, Mathf.Min(a.RaceProps.lifeExpectancy, chronological)));
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, InmateGenerator.OutsideFaction, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false,
                colonistRelationChanceFactor: 0f, allowFood: false, forceNoGear: true, fixedBiologicalAge: biological, fixedChronologicalAge: chronological, fixedGender: gender);
            Pawn parent = PawnGenerator.GeneratePawn(request);
            if (parent == null) return null;
            if (!Find.WorldPawns.Contains(parent)) Find.WorldPawns.PassToWorld(parent, RimWorld.Planet.PawnDiscardDecideMode.KeepForever);
            else Find.WorldPawns.ForcefullyKeptPawns.Add(parent);
            PrisonGame.Instance?.story.kinParents.Add(parent);
            return parent;
        }

        /// <summary>A parent made and not used (the other could not be made): no longer kept.</summary>
        private static void DropParent(Pawn parent)
        {
            Find.WorldPawns.ForcefullyKeptPawns.Remove(parent);
            PrisonGame.Instance?.story.kinParents.Remove(parent);
        }

        /// <summary>A child of this parent is still here: on the map (spawned, or carried by somebody who is), at home on the staff, or kept as a former inmate (whose own keep keeps the parent then).</summary>
        private static bool AnyChildHere(Pawn parent, PrisonGame game)
        {
            if (parent.relations == null) return false;
            foreach (Pawn child in parent.relations.Children)
                if (child is { Dead: false, Destroyed: false } && (child.MapHeld != null || game.home.Contains(child) || Find.WorldPawns.ForcefullyKeptPawns.Contains(child))) return true;
            return false;
        }

        /// <summary>The parents made for siblings are let go (and the world pawn collector takes them) once no child of theirs is left here.</summary>
        public static void ReleaseParents(PrisonGame game)
        {
            List<Pawn> list = game.story.kinParents;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Pawn parent = list[i];
                if (parent != null && !parent.Destroyed && !parent.Discarded && AnyChildHere(parent, game)) continue;
                if (parent != null) Find.WorldPawns.ForcefullyKeptPawns.Remove(parent);
                list.RemoveAt(i);
            }
        }

        /// <summary>Makes <paramref name="other"/> the <paramref name="kind"/> of <paramref name="n"/> in vanilla's relations. False when a parent could not be made (nothing is changed then).</summary>
        public static bool Link(Pawn n, Pawn other, KinKind kind)
        {
            switch (kind)
            {
                case KinKind.Parent:
                    n.relations.AddDirectRelation(PawnRelationDefOf.Parent, other);
                    return true;
                case KinKind.Child:
                    other.relations.AddDirectRelation(PawnRelationDefOf.Parent, n);
                    return true;
                case KinKind.Sibling:
                    Pawn mother = MakeParent(Gender.Female, n, other), father = MakeParent(Gender.Male, n, other);
                    if (mother == null || father == null)
                    {
                        if (mother != null) DropParent(mother);
                        if (father != null) DropParent(father);
                        return false;
                    }
                    foreach (Pawn child in new[] { n, other })
                    {
                        child.relations.AddDirectRelation(PawnRelationDefOf.Parent, mother);
                        child.relations.AddDirectRelation(PawnRelationDefOf.Parent, father);
                    }
                    return true;
                default:
                    Pawn shared = MakeParent(Rand.Bool ? Gender.Female : Gender.Male, n, other);
                    if (shared == null) return false;
                    n.relations.AddDirectRelation(PawnRelationDefOf.Parent, shared);
                    other.relations.AddDirectRelation(PawnRelationDefOf.Parent, shared);
                    return true;
            }
        }

        /// <summary>Picks one of the pool the newcomer fits as kin (kinds by weight, siblings the likeliest) and makes it so. <paramref name="only"/>: just this kind. Null when nobody fits.</summary>
        public static (Pawn other, KinKind kind)? TryMake(Pawn newcomer, IEnumerable<Pawn> pool, KinKind? only = null)
        {
            var fits = new List<(Pawn other, KinKind kind, float weight)>();
            var kinds = new List<(KinKind kind, float weight)>();
            foreach (Pawn o in pool)
            {
                kinds.Clear();
                Fits(newcomer, o, kinds);
                foreach ((KinKind kind, float weight) in kinds)
                    if (only == null || kind == only) fits.Add((o, kind, weight));
            }
            if (fits.Count == 0) return null;
            (Pawn other, KinKind kind, float _) pick = fits.RandomElementByWeight(f => f.weight);
            return Link(newcomer, pick.other, pick.kind) ? (pick.other, pick.kind) : null;
        }

        /// <summary>
        /// A member of staff has just been hired (generated for the job, not a former inmate): at about one in twelve they are kin to an inmate inside. <paramref name="chance"/>: a check's own odds.
        /// The chronicle says so, and a guard is asked where they want to stand (see <see cref="AskIfDue"/>).
        /// </summary>
        public static (Pawn other, KinKind kind)? OnHired(Pawn staff, Map map, float? chance = null)
        {
            if (map == null || !EligibleStaff(staff) || !Rand.Chance(chance ?? Chance(HireChance))) return null;
            if (TryMake(staff, (PoolOverride ?? map.mapPawns.PrisonersOfColonySpawned).Where(IsInmateInside)) is not { } made) return null;
            Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_KinHired".Translate(Mention(staff), Mention(made.other), Label(made.other, staff)), staff, made.other);
            AskIfDue(staff);
            return made;
        }

        /// <summary>
        /// An inmate has just come in (a new arrival, not a return): at about one in twelve they are kin to a member of staff, else at a lower rate to an inmate already inside (siblings only).
        /// <paramref name="chance"/>: a check's own odds, for both rolls.
        /// </summary>
        public static (Pawn other, KinKind kind)? OnArrived(Pawn inmate, Map map, float? chance = null)
        {
            if (map == null || inmate.relations == null) return null;
            if (Rand.Chance(chance ?? Chance(ArrivalChance)) && TryMake(inmate, (PoolOverride ?? StaffCommute.AllStaff(map)).Where(EligibleStaff)) is { } withStaff)
            {
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, "RP_Chr_KinArrived".Translate(Mention(inmate), Mention(withStaff.other), Label(withStaff.other, inmate)), inmate, withStaff.other);
                AskIfDue(withStaff.other);
                return withStaff;
            }
            if (Rand.Chance(chance ?? Chance(InmateKinChance)) && TryMake(inmate, (PoolOverride ?? map.mapPawns.PrisonersOfColonySpawned).Where(p => p != inmate && IsInmateInside(p)), KinKind.Sibling) is { } withInmate)
            {
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_KinInmates".Translate(Mention(inmate), Mention(withInmate.other), Label(withInmate.other, inmate)), inmate, withInmate.other);
                return withInmate;
            }
            return null;
        }

        // ---- the officer's request --------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard with kin inside is asked once where they want to stand: kept off the wing their kin is in, or left on it (their call). Only guards: keeping off a wing is a matter of
        /// patrols and posts. Returns the letter, or null (no kin inside, not a guard, asked already).
        /// </summary>
        public static ChoiceLetter_PrisonDecision AskIfDue(Pawn staff)
        {
            if (staff.GetComp<CompStaff>() is not { role: { guardDuties: true }, kinAsked: false, Protected: false } sc) return null;
            List<Pawn> kin = InmatesOf(staff);
            if (kin.Count == 0) return null;
            sc.kinAsked = true;
            string who = string.Join(", ", kin.Select(k => "RP_KinAskOne".Translate(Label(k, staff), Describe(k), k.Inmate().security?.label ?? "?").Resolve()));
            return StaffLives.Ask("RP_Decision_Kin", staff, "RP_KinAskLabel".Translate(staff.LabelShort), "RP_KinAskText".Translate(staff.LabelShort, StaffCareers.Title(sc), who), 24);
        }

        /// <summary>The letter's answer (a letter left alone is "let them be").</summary>
        public static void Decide(Pawn staff, CompStaff sc, bool keepOff)
        {
            sc.kinKeepOff = keepOff;
            if (!keepOff) return;
            UnitOfficers.Notify_KeptOff(staff, sc);
            if (sc.patrolZone != null && KeepsOffZone(staff, sc.patrolZone)) sc.patrolZone = null;
            staff.needs?.mood?.thoughts.memories.TryGainMemory(RPStaffLifeDefOf.RP_ManagementListened);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_KinKeptOff".Translate(Mention(staff)), staff);
        }

        /// <summary>The wings (security levels) where this guard's kin are held, when they asked to be kept off them (null: they did not, or there is none: the usual case).</summary>
        public static List<SecurityLevelDef> KeptOffWings(Pawn guard)
        {
            if (guard.GetComp<CompStaff>() is not { kinKeepOff: true }) return null;
            List<SecurityLevelDef> wings = null;
            foreach ((Pawn other, KinKind _) in Relatives(guard))
                if (IsInmateInside(other) && other.Inmate().security is { } level) (wings ??= new List<SecurityLevelDef>()).Add(level);
            return wings;
        }

        /// <summary>This painted zone is a wing that holds kin of this guard, who asked to be kept off it.</summary>
        public static bool KeepsOffZone(Pawn guard, PrisonZoneDef zone) => zone?.security != null && KeptOffWings(guard)?.Contains(zone.security) == true;

        /// <summary>This cell is in a wing that holds kin of this guard, who asked to be kept off it (a guard post there is not theirs to man).</summary>
        public static bool KeepsOffCell(Pawn guard, Map map, IntVec3 cell) => guard.GetComp<CompStaff>() is { kinKeepOff: true } && KeepsOffZone(guard, PrisonMap.Of(map)?.zones.ZoneAt(cell));

        /// <summary>The patrol zone the warden may give this guard (the staff tab and the deployment tab offer only these).</summary>
        public static bool PatrolAllowed(Pawn guard, PrisonZoneDef zone) => !KeepsOffZone(guard, zone);

        /// <summary>Daily: the parents made for siblings are let go when no child is left, a guard kept off a wing whose kin was moved to another one is no longer set to patrol it, and a guard with kin inside who was never asked (retrained into the work) is.</summary>
        public static void Daily(Map map)
        {
            if (PrisonGame.Instance is { } game) ReleaseParents(game);
            foreach (Pawn p in StaffCommute.AllStaff(map))
            {
                if (p.GetComp<CompStaff>() is not { role: not null, Protected: false } sc) continue;
                UnitOfficers.Notify_KeptOff(p, sc);
                if (sc.patrolZone != null && KeepsOffZone(p, sc.patrolZone)) sc.patrolZone = null;
                if (!sc.kinAsked) AskIfDue(p);
            }
        }

        // ---- what the kinship does -------------------------------------------------------------------------------------------

        /// <summary>
        /// A guard asked to restrain their own kin hesitates when another guard can go (<see cref="GuardUtility.CanRespondTo"/>): see <see cref="HesitateChance"/>. <paramref name="window"/>:
        /// a check's own window (the current one by default).
        /// </summary>
        public static bool Hesitates(Pawn guard, Pawn inmate, int? window = null)
        {
            if (Between(guard, inmate) == null || !AnotherGuardCanGo(guard, inmate)) return false;
            int w = window ?? Find.TickManager.TicksGame / HesitateWindow;
            return Rand.ChanceSeeded(HesitateChance, Gen.HashCombineInt(Gen.HashCombineInt(guard.thingIDNumber, inmate.thingIDNumber), w));
        }

        /// <summary>
        /// Another guard who could take the response job themselves (see <see cref="CouldTakeResponse"/>), is on duty with nothing on that is more urgent (idle, a patrol, or standing a post), is no kin of the
        /// inmate and can reach them.
        /// </summary>
        private static bool AnotherGuardCanGo(Pawn guard, Pawn inmate)
        {
            if (guard.Map == null) return false;
            IReadOnlyList<Pawn> colonists = guard.Map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn o = colonists[i];
                if (o == guard || !GuardPosts.IdleOrPosted(o) || !CouldTakeResponse(o) || Between(o, inmate) != null || CareDetail.Holds(o, inmate)) continue;
                if (o.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Deadly)) return true;
            }
            return false;
        }

        /// <summary>Would this guard's work scan offer them the response job at all: a guard, the Warden work on and not disabled, not barred from violence, and with what the work giver requires (hands, for <c>RP_RespondToTrouble</c>).</summary>
        public static bool CouldTakeResponse(Pawn g)
        {
            WorkGiverDef giver = RPSecurityDefOf.RP_RespondToTrouble;
            return GuardUtility.IsGuard(g) && giver.workType is { } work && !g.WorkTypeIsDisabled(work) && g.workSettings?.WorkIsActive(work) == true && !g.WorkTagIsDisabled(WorkTags.Violent)
                && giver.Worker.MissingRequiredCapacity(g) == null;
        }

        /// <summary>The odds that an officer says yes to this fixer rise by <see cref="CorruptionLean"/> when the fixer, or the gang or the smuggling ring they are in, holds the officer's kin (see <see cref="CorruptionSystem.OddsFor"/>).</summary>
        public static float Leverage(Pawn staff, Pawn fixer)
        {
            if (fixer?.Inmate() is not { } fc) return 0f;
            foreach ((Pawn other, KinKind _) in Relatives(staff))
            {
                if (!IsInmateInside(other)) continue;
                if (other == fixer || fc.gang != null && other.Inmate().gang == fc.gang || RingSystem.ArcOf(other) is { } arc && arc == RingSystem.ArcOf(fixer)) return CorruptionLean;
            }
            return 0f;
        }

        /// <summary>The inmate has a feud with somebody the officer is kin of (a motive for the officer to have been hard on them).</summary>
        private static Pawn EnemyKinOf(Pawn inmate, Pawn officer)
        {
            if (inmate.relations == null) return null;
            foreach (DirectPawnRelation r in inmate.relations.DirectRelations)
                if (r.def == RPRelationDefOf.RP_Enemy && r.otherPawn != null && Between(officer, r.otherPawn) != null) return r.otherPawn;
            return null;
        }

        /// <summary>
        /// What kinship adds to a complaint's evidence (<see cref="ComplaintSystem.Evidence"/>): <see cref="MotiveEvidence"/> for a true complaint against an officer who is kin of the complainant's enemy,
        /// minus <see cref="DoubtEvidence"/> for a true complaint against an officer who is the complainant's own kin. Both put a line in what the investigation looked at, whatever is true.
        /// </summary>
        public static float ComplaintShift(bool isTrue, Pawn officer, Pawn inmate, List<string> lines)
        {
            float shift = 0f;
            if (EnemyKinOf(inmate, officer) is { } enemy)
            {
                lines?.Add("RP_ComplaintLook_KinMotive".Translate(officer.LabelShort, enemy.LabelShort, inmate.LabelShort).Resolve());
                if (isTrue) shift += MotiveEvidence;
            }
            if (Between(officer, inmate) != null)
            {
                lines?.Add("RP_ComplaintLook_KinOwn".Translate(officer.LabelShort, inmate.LabelShort).Resolve());
                if (isTrue) shift -= DoubtEvidence;
            }
            return shift;
        }

        /// <summary>
        /// A death in custody, an execution or a member of staff's death: the colleagues' line in the chronicle for every one of the dead's kin who is inside ("guard Hale's brother Jones died inside").
        /// Vanilla's own family thoughts (my brother died) come by themselves with the relations. A member of staff's mental break on the night is theirs to have: nothing in the mod bars it (only a run
        /// into the wild is barred for staff, see <see cref="Patch_MentalBreakWorker_BreakCanOccur_Warden"/>) and no check expects calm then.
        /// </summary>
        public static void Notify_Died(Pawn dead, bool executed = false)
        {
            if (dead?.relations == null || PrisonGame.Instance == null) return;
            bool staffDied = IsStaffInside(dead);
            foreach ((Pawn other, KinKind _) in Relatives(dead))
            {
                if (!IsStaffInside(other) && !IsInmateInside(other)) continue;
                // (An inmate whose kin died: a death in the family, see WatchList.)
                if (IsInmateInside(other)) WatchList.Notify_Trigger(other, RiskTrigger.FamilyDeath);
                string key = staffDied ? "RP_Chr_KinStaffDied" : executed ? "RP_Chr_KinExecuted" : "RP_Chr_KinDied";
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 2, key.Translate(Mention(other), Label(dead, other), dead.LabelShort), other, dead);
            }
        }
    }

    /// <summary>The officer's request: kept off the wing their kin is in, or left on it (see <see cref="Kin.AskIfDue"/>).</summary>
    public class KinDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Kin_KeepOff", "RP_Kin_LetBe" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 1;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            if (p is not { Dead: false } || p.GetComp<CompStaff>() is not { role: not null } sc) return;
            Kin.Decide(p, sc, keepOff: option == 0);
        }
    }
}
