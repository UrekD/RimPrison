using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPPostDefOf
    {
        public static ThingDef RP_GuardPost;
        public static JobDef RP_ManPost;

        static RPPostDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPPostDefOf));
    }

    /// <summary>A guard post: a spot (see Defs/GuardPosts.xml) a guard on shift stands at in its hours. What the warden sets lives here (and is saved).</summary>
    public class CompProperties_GuardPost : CompProperties
    {
        public CompProperties_GuardPost() => compClass = typeof(CompGuardPost);
    }

    public class CompGuardPost : ThingComp
    {
        /// <summary>The hours it is manned: from (the hour it starts) to (the hour it ends, not manned in it), 0-23 each, either side of midnight. The same hour in both is all day, which is how a new post is.</summary>
        public int fromHour, toHour;

        /// <summary>The guard who stands here when on shift and free (null: any free guard).</summary>
        public Pawn assigned;

        public bool AllDay => fromHour == toHour;

        public bool CoversHour(int hour) => AllDay || Since(hour, fromHour) < Since(toHour, fromHour);

        /// <summary>Hours from <paramref name="start"/> round the clock to <paramref name="hour"/> (0-23).</summary>
        private static int Since(int hour, int start) => ((hour - start) % 24 + 24) % 24;

        /// <summary>The guard named for it, while they are alive (a guard let go or dead is no one's post: any free guard goes).</summary>
        public Pawn Assigned => assigned is { Dead: false, Destroyed: false } ? assigned : null;

        /// <summary>A guard was named and is gone (dead, or let go): until the warden names another, any free guard goes.</summary>
        public bool GuardGone => assigned != null && Assigned == null;

        /// <summary>Who the post is for, as the tab and the inspect string say it.</summary>
        public string GuardLabel() => Assigned?.LabelShortCap ?? (GuardGone ? assigned.LabelShortCap + " " + "RP_DeployPostGuardGone".Translate() : "RP_PostGuardAny".Translate().ToString());

        /// <summary>Its hours cover now and the state's chief is not setting the patrols: a guard is wanted here.</summary>
        public bool OpenNow => parent.Spawned && !SpecialMeasures.ChiefInPost && CoversHour(GenLocalDate.HourOfDay(parent.Map));

        /// <summary>Whoever has the manning job for this post, on the way or there (see <see cref="StandingThere"/>).</summary>
        public Pawn Stander => GuardPosts.StanderOf(parent);

        public bool StandingThere => Stander is { } p && p.Position == parent.Position;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref fromHour, "fromHour");
            Scribe_Values.Look(ref toHour, "toHour");
            // (A guard who is dead or let go is no reference to write: the load would warn of an unresolved one. The post is any free guard's from then on, as it already is.)
            if (Scribe.mode == LoadSaveMode.Saving && Assigned == null) assigned = null;
            Scribe_References.Look(ref assigned, "assigned");
        }

        public string HoursText() => AllDay ? "RP_PostHoursAll".Translate().ToString() : "RP_PostHoursRange".Translate(GuardPosts.HourLabel(fromHour), GuardPosts.HourLabel(toHour)).ToString();

        public override string CompInspectStringExtra()
        {
            var sb = new StringBuilder();
            sb.AppendLine("RP_PostInspectHours".Translate(HoursText()));
            sb.AppendLine("RP_PostInspectGuard".Translate(GuardLabel()));
            sb.Append(StatusText());
            return sb.ToString();
        }

        /// <summary>Who stands here now, or why nobody is sent.</summary>
        public string StatusText()
        {
            if (SpecialMeasures.ChiefInPost) return "RP_PostSuspended".Translate();
            Pawn stander = Stander;
            if (stander != null) return (stander.Position == parent.Position ? "RP_PostStanding" : "RP_PostOnTheWay").Translate(stander.LabelShort);
            return (OpenNow ? "RP_PostUnmanned" : "RP_PostOutsideHours").Translate();
        }
    }

    public static class GuardPosts
    {
        /// <summary>How often a guard standing at a post lets the think tree look for work that outranks it (an emergency, a search, an escort): vanilla's own expiry check.</summary>
        public const int OverrideCheckTicks = 120;

        public static string HourLabel(int hour) => "RP_PostHour".Translate(hour.ToString("00")).ToString();

        /// <summary>Every post on the map, in the order they were put up.</summary>
        public static List<CompGuardPost> Posts(Map map)
        {
            var list = new List<CompGuardPost>();
            List<Thing> things = map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost);
            for (int i = 0; i < things.Count; i++)
                if (things[i].TryGetComp<CompGuardPost>() is { } post) list.Add(post);
            list.Sort((a, b) => a.parent.thingIDNumber.CompareTo(b.parent.thingIDNumber));
            return list;
        }

        public static Pawn StanderOf(Thing post)
        {
            if (post.Map == null) return null;
            IReadOnlyList<Pawn> colonists = post.Map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                if (colonists[i].CurJobDef == RPPostDefOf.RP_ManPost && colonists[i].CurJob.targetA.Thing == post) return colonists[i];
            return null;
        }

        /// <summary>A guard at work now: on the roster, on site and awake, on the timetable's work hours (not on a break, not off shift, not in the hour they commute in), not on strike or walking home.</summary>
        public static bool OnDuty(Pawn p) =>
            p.Spawned && ShiftSystem.AvailableGuard(p) && !StaffCommute.AtHome(p) && !p.Downed && !p.Drafted && !p.InMentalState && p.Awake() && !IncidentWorker_StaffStrike.Striking(p)
            && p.CurJobDef != RPCommuteDefOf.RP_GoHome && (p.timetable == null || p.timetable.CurrentAssignment == TimeAssignmentDefOf.Work);

        /// <summary>On duty and with nothing on that a post is not as good as: no job, a patrol, a wander (an escort, a search, a rescue, pad duty or a tower is work). Standing at a post or walking to one is not free (see <see cref="Free"/>).</summary>
        private static bool Idle(Pawn p)
        {
            JobDef d = p.CurJobDef;
            return OnDuty(p) && (d == null || d == RPSecurityDefOf.RP_Patrol || d == JobDefOf.Wait_Wander || d == JobDefOf.GotoWander || d == JobDefOf.Wait || d == JobDefOf.Wait_MaintainPosture);
        }

        /// <summary>Idle, or on a post at all (what a check needs: a guard who has been given a post is still a guard the post work can use).</summary>
        public static bool IdleOrPosted(Pawn p) => Idle(p) || OnDuty(p) && p.CurJobDef == RPPostDefOf.RP_ManPost;

        /// <summary>
        /// Free to take <paramref name="forPost"/>: idle, or already on that very post. A guard on another post is not free for it: they never leave the one they are on for another (their work scan hands them back their own post),
        /// so to count them as someone "who will go" starves the post.
        /// </summary>
        public static bool Free(Pawn p, CompGuardPost forPost) =>
            // (A guard in the care reserve goes to no post, so they are no one who "will go" either: see CareDetail.)
            !CareDetail.IsReserve(p) && (Idle(p) || forPost != null && OnDuty(p) && p.CurJobDef == RPPostDefOf.RP_ManPost && p.CurJob.targetA.Thing == forPost.parent);

        /// <summary>Free for the post, allowed to go to it and able to reach it.</summary>
        public static bool FreeAndAble(Pawn g, CompGuardPost post) => Free(g, post) && !post.parent.IsForbidden(g) && !KeptOff(g, post) && g.CanReach(post.parent.Position, PathEndMode.OnCell, Danger.Some);

        /// <summary>A guard who asked to be kept off the wing their kin is in does not man a post in it (see <see cref="Kin"/>).</summary>
        public static bool KeptOff(Pawn guard, CompGuardPost post) => post.parent.Map != null && Kin.KeepsOffCell(guard, post.parent.Map, post.parent.Position);

        /// <summary>The guard is named for another post that is open, unmanned and theirs to take: that one is the one they go to.</summary>
        private static bool NamedElsewhere(Pawn pawn, CompGuardPost post)
        {
            List<Thing> things = pawn.Map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] == post.parent || things[i].TryGetComp<CompGuardPost>() is not { } other || other.Assigned != pawn || !other.OpenNow) continue;
                if (other.Stander is { } s && s != pawn || other.parent.IsForbidden(pawn) || KeptOff(pawn, other)) continue;
                if (pawn.CanReach(other.parent.Position, PathEndMode.OnCell, Danger.Some)) return true;
            }
            return false;
        }

        /// <summary>
        /// Would this guard be sent to this post now: it is open (its hours cover now, no state chief), nobody else is on it, the guard is on duty, and they are the one the post wants: the guard named for it if they are on duty and
        /// free (and can get there), else the nearest free guard. (Forced: the player asked for it, so only the first three count.)
        /// </summary>
        public static bool CanTake(Pawn pawn, CompGuardPost post, bool forced = false)
        {
            if (!post.OpenNow || !OnDuty(pawn) || post.parent.IsForbidden(pawn)) return false;
            // (A guard in the care reserve stands at no post, and none is on one: see CareDetail.)
            if (!forced && CareDetail.IsReserve(pawn)) return false;
            if (post.Stander is { } s && s != pawn) return false;
            // (Not even when the warden sends them: they asked, and the warden agreed.)
            if (KeptOff(pawn, post)) return false;
            if (!forced)
            {
                Pawn named = post.Assigned;
                if (named != null && named != pawn && FreeAndAble(named, post)) return false;
                // (A guard named for another open post goes to that one, not to the nearest: the post that names them is not left to wait.)
                if (named != pawn && NamedElsewhere(pawn, post)) return false;
                // (Whoever is on it keeps it: two free guards walking about would hand it back and forth at every check.)
                if (named != pawn && post.Stander != pawn && CloserFreeGuard(pawn, post, named)) return false;
            }
            return pawn.CanReserveAndReach(post.parent, PathEndMode.OnCell, Danger.Some, 1, -1, null, forced);
        }

        /// <summary>Another guard who would go (free for this post, not the one named, who is looked at on their own, nor one bound for the post that names them) stands nearer the post than this one: they are the one to send.</summary>
        private static bool CloserFreeGuard(Pawn pawn, CompGuardPost post, Pawn named)
        {
            IntVec3 at = post.parent.Position;
            int mine = pawn.Position.DistanceToSquared(at);
            IReadOnlyList<Pawn> colonists = pawn.Map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn other = colonists[i];
                if (other == pawn || other == named) continue;
                int theirs = other.Position.DistanceToSquared(at);
                if ((theirs < mine || theirs == mine && other.thingIDNumber < pawn.thingIDNumber) && FreeAndAble(other, post) && !NamedElsewhere(other, post)) return true;
            }
            return false;
        }

        /// <summary>The first post this guard would be sent to now, if any (what ends a patrol: the post work giver would hand it to them).</summary>
        public static CompGuardPost PostFor(Pawn pawn)
        {
            if (pawn.Map == null || SpecialMeasures.ChiefInPost) return null;
            List<Thing> things = pawn.Map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost);
            for (int i = 0; i < things.Count; i++)
                if (things[i].TryGetComp<CompGuardPost>() is { } post && CanTake(pawn, post)) return post;
            return null;
        }

        public static string PlaceOf(CompGuardPost post)
        {
            IntVec3 c = post.parent.Position;
            PrisonZoneDef zone = PrisonMap.Of(post.parent.Map)?.zones.ZoneAt(c);
            return zone != null ? "RP_PostPlace".Translate(zone.label, c.x, c.z).ToString() : "RP_PostPlaceNone".Translate(c.x, c.z).ToString();
        }

        /// <summary>How near a patch's cell a post is put up, and how near it must stand for the patch to count as posted (the same radius, so that a post put up for a patch is the one the patch sees).</summary>
        public const float NearRadius = 4.9f;

        /// <summary>
        /// The cell for a post at or nearest to <paramref name="near"/> (within <see cref="NearRadius"/>): standable, nothing built on it, no post or blueprint of one already there, and the game's own placement rules for the spot pass; one in
        /// the same room as <paramref name="near"/> first (a post through a wall is not the patch's), else any. Invalid when there is none.
        /// </summary>
        public static IntVec3 CellNear(Map map, IntVec3 near)
        {
            Room room = near.InBounds(map) ? near.GetRoom(map) : null;
            IntVec3 other = IntVec3.Invalid;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(near, NearRadius, true))
            {
                if (!c.InBounds(map) || !c.InHorDistOf(near, NearRadius) || !c.Standable(map) || c.GetEdifice(map) != null) continue;
                if (c.GetThingList(map).Any(t => t.def == RPPostDefOf.RP_GuardPost || t.def.entityDefToBuild == RPPostDefOf.RP_GuardPost)) continue;
                if (!GenConstruct.CanPlaceBlueprintAt(RPPostDefOf.RP_GuardPost, c, Rot4.North, map).Accepted) continue;
                if (room == null || c.GetRoom(map) == room) return c;
                if (!other.IsValid) other = c;
            }
            return other;
        }

        /// <summary>Puts a post up at once (a spot costs nothing and takes no work, like vanilla's) at the cell nearest to <paramref name="near"/>; null when there is no cell.</summary>
        public static Thing Place(Map map, IntVec3 near)
        {
            IntVec3 cell = CellNear(map, near);
            if (!cell.IsValid) return null;
            Thing post = ThingMaker.MakeThing(RPPostDefOf.RP_GuardPost);
            post.SetFactionDirect(Faction.OfPlayer);
            return GenSpawn.Spawn(post, cell, map, Rot4.North);
        }

        /// <summary>A post stands within <see cref="NearRadius"/> of the cell (a patch of the danger map already has one: no second one from its button).</summary>
        public static bool PostNear(Map map, IntVec3 c, float radius = NearRadius) =>
            map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost).Any(t => t.Position.InHorDistOf(c, radius));

        /// <summary>The posts and who is on them, on one line (the saveload plan compares it across a round trip).</summary>
        public static string Describe(Map map) =>
            string.Join(",", Posts(map).Select(p => $"{p.parent.Position.x},{p.parent.Position.z}:{p.fromHour}:{p.toHour}:{p.assigned?.thingIDNumber}"));
    }

    /// <summary>Guards on shift and free man the open posts (under the towers, above the patrol, under all the rest of a guard's work: a built tower keeps its guard, the posts take the guards no tower needs).</summary>
    public class WorkGiver_ManPost : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForDef(RPPostDefOf.RP_GuardPost);

        public override PathEndMode PathEndMode => PathEndMode.OnCell;

        // (With the state's chief in post the patrols are theirs: the warden's posts are suspended, nobody is sent. No posts on the map: nothing to scan.)
        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            !GuardUtility.IsGuard(pawn) || SpecialMeasures.ChiefInPost || pawn.Map == null || pawn.Map.listerThings.ThingsOfDef(RPPostDefOf.RP_GuardPost).Count == 0;

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) => t.TryGetComp<CompGuardPost>() is { } post && GuardPosts.CanTake(pawn, post, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Job job = JobMaker.MakeJob(RPPostDefOf.RP_ManPost, t);
            // (Vanilla's own check for what outranks a job that waits: every few seconds the think tree is asked, and an emergency, a search or an escort takes the guard from the post. Without it a guard stood
            // there through a riot.)
            job.expiryInterval = GuardPosts.OverrideCheckTicks;
            job.checkOverrideOnExpire = true;
            return job;
        }
    }

    /// <summary>Walk to the post and stand watch until its hours or the guard's shift end (or something outranks it).</summary>
    public class JobDriver_ManPost : JobDriver
    {
        private int nextCheckTick;

        private CompGuardPost Post => job.targetA.Thing?.TryGetComp<CompGuardPost>();

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // (A post taken down, or burnt, ends it: the guard is released.)
            this.FailOnDespawnedOrNull(TargetIndex.A);
            nextCheckTick = Find.TickManager.TicksGame + 30;
            AddEndCondition(() =>
            {
                int now = Find.TickManager.TicksGame;
                if (now < nextCheckTick) return JobCondition.Ongoing;
                nextCheckTick = now + 30;
                // Its hours are over, the state's chief has the patrols, or the guard's shift is (a break, the end of the day, the strike).
                return Post is { OpenNow: true } && GuardPosts.OnDuty(pawn) ? JobCondition.Ongoing : JobCondition.Succeeded;
            });
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil watch = Toils_General.Wait(GenDate.TicksPerHour);
            watch.handlingFacing = true;
            watch.tickIntervalAction = delta =>
            {
                if (pawn.IsHashIntervalTick(200, delta)) pawn.Rotation = Rot4.Random;
            };
            yield return watch;
        }
    }
}
