using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>One inmate's history with one member of staff (the name outlives them).</summary>
    public class StaffMemory : IExposable
    {
        public int staffId;
        public string name;
        public float score;
        public int lastTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref staffId, "id");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref score, "score");
            Scribe_Values.Look(ref lastTick, "tick");
        }
    }

    /// <summary>
    /// Inmates remember the staff who dealt with them: restrained, caught with contraband, their cell turned over, a
    /// friend shot (worse); carried to a bed when hurt, not as a prisoner being taken to punishment (better). Scores fade
    /// a twentieth a day. A hated officer close by winds them up, a trusted one settles them; a hated one has a harder time
    /// restraining them (after the cap, so it always shows), a trusted one an easier; and when they lash out at staff, it
    /// is at the one they hate. A short list per inmate (the strongest feelings kept).
    /// </summary>
    public static class StaffMemories
    {
        public const int MaxPerInmate = 8;
        public const float BeatenInSight = -10f, Restrained = -15f, Resisted = -5f, FriskFound = -8f, CellSearched = -10f, FriendShot = -60f, Rescued = 15f;
        public const float HatedAt = -40f, TrustedAt = 30f, HatedTension = 1f, TrustedRelief = 0.5f, DailyFade = 0.05f, NearCells = 10f;
        public const float HatedSubdueFactor = 0.85f, TrustedSubdueBonus = 0.05f;

        /// <summary>A beating in the room: the one beaten remembers the guards who were there.</summary>
        public static void Notify_BeatenInSight(Pawn victim, CompInmate c)
        {
            if (victim.GetRoom() is not { } room) return;
            foreach (Pawn s in victim.Map.mapPawns.FreeColonistsSpawned)
                if (GuardUtility.IsGuard(s) && Near(victim, s, room)) Note(c, s, BeatenInSight);
        }

        public static void Note(CompInmate c, Pawn staff, float delta)
        {
            if (c == null || staff?.GetComp<CompStaff>()?.role == null) return;
            Note(c, staff.thingIDNumber, staff.LabelShort, delta);
        }

        /// <summary>
        /// The strongest feelings are kept: past the cap the weakest by |score|, after this delta is applied, is forgotten, which is the
        /// newcomer when theirs is the weakest (a first frisk does not push out an old grudge); of equals the older goes.
        /// </summary>
        public static void Note(CompInmate c, int staffId, string name, float delta)
        {
            StaffMemory m = c.staffMemories.FirstOrDefault(x => x.staffId == staffId);
            bool added = m == null;
            if (added) m = new StaffMemory { staffId = staffId, name = name };
            m.score = Mathf.Clamp(m.score + delta, -100f, 100f);
            m.lastTick = Find.TickManager.TicksGame;
            if (added)
            {
                c.staffMemories.Add(m);
                if (c.staffMemories.Count > MaxPerInmate) c.staffMemories.Remove(c.staffMemories.OrderBy(x => Mathf.Abs(x.score)).ThenBy(x => x.lastTick).First());
            }
            // A feeling strong enough to want to settle (and kept: a weak newcomer may just have been forgotten).
            if (delta < 0f && m.score <= Wants.GrudgeAt && c.staffMemories.Contains(m)) Wants.Notify_Grudge(c);
        }

        public static float ScoreFor(CompInmate c, Pawn staff)
        {
            if (c == null || staff == null) return 0f;
            for (int i = 0; i < c.staffMemories.Count; i++)
                if (c.staffMemories[i].staffId == staff.thingIDNumber) return c.staffMemories[i].score;
            return 0f;
        }

        public static StaffMemory MostHated(CompInmate c) => c.staffMemories.Where(m => m.score <= HatedAt).OrderBy(m => m.score).FirstOrDefault();
        public static StaffMemory MostTrusted(CompInmate c) => c.staffMemories.Where(m => m.score >= TrustedAt).OrderByDescending(m => m.score).FirstOrDefault();

        /// <summary>A guard's chance to restrain this inmate, after the cap: a hated guard x0.85, a trusted one +5 points.</summary>
        public static float AdjustSubdue(Pawn guard, Pawn inmate, float chance)
        {
            float s = ScoreFor(inmate.Inmate(), guard);
            return s <= HatedAt ? chance * HatedSubdueFactor : s >= TrustedAt ? Mathf.Min(0.95f, chance + TrustedSubdueBonus) : chance;
        }

        /// <summary>A carry to a bed finished: a hurt inmate remembers who carried them; a restrained one being taken to
        /// punishment (or after their own restraint) does not.</summary>
        public static void Notify_CarriedToBed(Pawn carrier, Pawn inmate)
        {
            if (inmate?.Inmate() is not { } c || inmate.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)
                || c.pendingPunishmentHours > 0 || c.forcedActivity != null) return;
            Note(c, carrier, Rescued);
            PrisonGame.Instance.stats.rescuesRemembered++;
        }

        /// <summary>Close enough to matter: the same room, and within a few steps in a big or outdoor one.</summary>
        internal static bool Near(Pawn a, Pawn b, Room room)
        {
            if (b.GetRoom() != room) return false;
            return (!room.PsychologicallyOutdoors && room.CellCount < 600) || a.PositionHeld.InHorDistOf(b.Position, NearCells);
        }

        /// <summary>Hourly tension from the staff in the room: a hated officer winds them up, a trusted one settles them.</summary>
        public static float RoomTension(Pawn p, CompInmate c)
        {
            // (Carried, an inmate is on no map but the carrier's: their hourly update still runs, with the room they are in. A carry to a solitary cell is not rare.)
            if (c.staffMemories.Count == 0 || p.GetRoom() is not { } room || p.MapHeld == null) return 0f;
            float t = 0f;
            foreach (Pawn s in p.MapHeld.mapPawns.FreeColonistsSpawned)
            {
                if (!Near(p, s, room)) continue;
                float score = ScoreFor(c, s);
                if (score <= HatedAt) t = Mathf.Max(t, HatedTension);
                else if (score >= TrustedAt && t <= 0f) t = -TrustedRelief;
            }
            return t;
        }

        /// <summary>Of the staff within reach, the one they hate most, if any.</summary>
        public static Pawn HatedAmong(CompInmate c, IEnumerable<Pawn> candidates)
        {
            if (!c.staffMemories.Any(m => m.score <= HatedAt)) return null;
            Pawn best = null;
            float worst = HatedAt;
            foreach (Pawn s in candidates)
            {
                float score = ScoreFor(c, s);
                if (score <= worst)
                {
                    worst = score;
                    best = s;
                }
            }
            return best;
        }

        /// <summary>Daily: feelings fade.</summary>
        public static void Daily(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } c) Fade(c);
        }

        public static void Fade(CompInmate c)
        {
            if (c.staffMemories.Count == 0) return;
            foreach (StaffMemory m in c.staffMemories) m.score *= 1f - DailyFade;
            c.staffMemories.RemoveAll(m => Mathf.Abs(m.score) < 2f);
        }

        /// <summary>A guard shot an inmate dead: their friends and gang hate the shooter.</summary>
        public static void Notify_ShotDead(Pawn victim, Pawn shooter)
        {
            if (victim.MapHeld == null) return;
            CompInmate vc = victim.Inmate();
            foreach (Pawn p in victim.MapHeld.mapPawns.PrisonersOfColonySpawned)
                if (p != victim && p.Inmate() is { } c && (PrisonRelations.Friends(p, victim) || vc?.gang != null && c.gang == vc.gang))
                    Note(c, shooter, FriendShot);
        }

        /// <summary>How the wing feels about this member of staff: (hated by, trusted by).</summary>
        public static (int hated, int trusted) Standing(Pawn staff)
        {
            int hated = 0, trusted = 0;
            Map map = staff.MapHeld ?? Find.AnyPlayerHomeMap;
            if (map == null) return (0, 0);
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                float s = ScoreFor(p.Inmate(), staff);
                if (s <= HatedAt) hated++;
                else if (s >= TrustedAt) trusted++;
            }
            return (hated, trusted);
        }
    }

    /// <summary>A wounded inmate carried to a bed by staff remembers who did it, once the carry is done (the job is also
    /// built for right-click menus and re-issued when interrupted, so it cannot count at creation).</summary>
    [HarmonyLib.HarmonyPatch(typeof(RimWorld.JobDriver_TakeToBed), "MakeNewToils")]
    public static class Patch_JobDriver_TakeToBed_Remember
    {
        public static void Prefix(RimWorld.JobDriver_TakeToBed __instance)
        {
            if (__instance.job?.def != RimWorld.JobDefOf.TakeWoundedPrisonerToBed || __instance.pawn?.GetComp<CompStaff>()?.role == null) return;
            Pawn carrier = __instance.pawn, takee = __instance.job.targetA.Thing as Pawn;
            __instance.AddFinishAction(condition =>
            {
                if (condition == Verse.AI.JobCondition.Succeeded) StaffMemories.Notify_CarriedToBed(carrier, takee);
            });
        }
    }
}
