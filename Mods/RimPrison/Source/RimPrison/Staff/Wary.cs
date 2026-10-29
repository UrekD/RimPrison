using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPWaryDefOf
    {
        public static ThoughtDef RP_Shaken;
        public static ThoughtDef RP_FacedDown;

        static RPWaryDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPWaryDefOf));
    }

    /// <summary>
    /// Danger memories: a member of staff put down by an inmate is shaken and wary of them for a few days: slower to get
    /// hands on that inmate (subdue chance x0.8, after the cap so it shows everywhere). Restraining them anyway faces it
    /// down: the wariness goes, and they are proud of it. The staff record says who they are wary of. One attacker at a
    /// time (the latest); it ends when the inmate leaves the prison.
    /// </summary>
    public static class StaffWariness
    {
        public const int Days = 3;
        public const float SubdueFactor = 0.8f;

        public static bool WaryOf(CompStaff sc, Pawn inmate) =>
            sc != null && inmate != null && sc.waryOfId == inmate.thingIDNumber && sc.waryUntilTick > Find.TickManager.TicksGame;

        public static void Notify_HurtBy(Pawn staff, CompStaff sc, Pawn attacker)
        {
            if (attacker?.Inmate() == null) return;
            // A new attacker replaces the old one: the old fear goes with the slot (else it could never be faced down).
            if (sc.waryOfId >= 0 && sc.waryOfId != attacker.thingIDNumber)
                staff.needs?.mood?.thoughts.memories.Memories.Where(m => m.def == RPWaryDefOf.RP_Shaken && m.otherPawn?.thingIDNumber == sc.waryOfId).ToList()
                    .ForEach(m => staff.needs.mood.thoughts.memories.RemoveMemory(m));
            sc.waryOfId = attacker.thingIDNumber;
            sc.waryOfName = attacker.LabelShort;
            sc.waryUntilTick = Find.TickManager.TicksGame + Days * GenDate.TicksPerDay;
            staff.needs?.mood?.thoughts.memories.TryGainMemory(RPWaryDefOf.RP_Shaken, attacker);
        }

        /// <summary>Restrained the one who hurt them.</summary>
        public static void Notify_Subdued(Pawn guard, Pawn inmate)
        {
            if (guard.GetComp<CompStaff>() is not { } sc || !WaryOf(sc, inmate)) return;
            Clear(sc);
            guard.needs?.mood?.thoughts.memories.RemoveMemoriesOfDefWhereOtherPawnIs(RPWaryDefOf.RP_Shaken, inmate);
            guard.needs?.mood?.thoughts.memories.TryGainMemory(RPWaryDefOf.RP_FacedDown, inmate);
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_FacedDown".Translate(guard.LabelShort, inmate.LabelShort), guard, inmate);
        }

        /// <summary>The inmate left for good (released, transferred, dead): nobody is wary of them any more.</summary>
        public static void Notify_InmateLeaving(Pawn inmate)
        {
            Map map = inmate.MapHeld ?? Find.AnyPlayerHomeMap;
            if (PrisonGame.Instance == null || map == null) return;
            foreach (Pawn s in StaffCommute.AllStaff(map))
                if (s.GetComp<CompStaff>() is { } sc && sc.waryOfId == inmate.thingIDNumber) Clear(sc);
        }

        public static void Clear(CompStaff sc)
        {
            sc.waryOfId = -1;
            sc.waryOfName = null;
            sc.waryUntilTick = -1;
        }

        /// <summary>A line for the staff record (null when not wary).</summary>
        public static string StatusLine(CompStaff sc)
        {
            int left = sc.waryUntilTick - Find.TickManager.TicksGame;
            return sc.waryOfId < 0 || left <= 0 ? null : "RP_WaryOf".Translate(sc.waryOfName ?? "?", (left / (float)GenDate.TicksPerDay).ToString("0.0")).ToString();
        }
    }
}
