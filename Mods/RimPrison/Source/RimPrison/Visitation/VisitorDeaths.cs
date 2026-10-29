using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// A visitor who dies in the prison (a fight in the visiting room, a raid on the way in or out) is a relative of the inmate they came for: the family's record marks them dead, so they never come back
    /// on a later visit or write again, and the inmate grieves as for a death outside, with the chronicle and a letter.
    /// </summary>
    public static class VisitorDeaths
    {
        /// <summary>Called as the pawn dies; true when they were somebody's visitor (and the rest of the custody death hook is not theirs).</summary>
        public static bool Notify_Died(Pawn visitor)
        {
            if (visitor.Map is not { } map || visitor.IsPrisonerOfColony || visitor.Faction == Faction.OfPlayer) return false;
            // The visit going on now, or one that is over and walking out (the lord still knows whom they came for; the inmate's visit has been cleared).
            Pawn inmate = null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } pc && pc.visitor == visitor)
                {
                    inmate = p;
                    break;
                }
            inmate ??= VisitationSystem.InmateOf(visitor);
            if (inmate?.Inmate() is not { } c) return false;
            // (A former inmate visiting a friend is not family: their death is not a relative's, and no relative of the same first name is marked dead. Their kept entry says so after the
            // visit too, when they die walking out.)
            if (c.visitor == visitor && c.visitorFormer || ExInmates.EntryOf(visitor) != null) return false;
            // (Nor is the victim's relative who came for a restorative meeting: the visit itself is marked, which still holds once the meeting is over and they walk out - the inmate's
            // own state is back to approved, or on to the outcome, by then.)
            if (VisitationSystem.VisitOf(visitor) is { ForAmends: true } || c.visitor == visitor && c.amendsState == AmendsState.Meeting) return false;
            Relative rel = c.visitor == visitor && c.visitorRelative != null
                ? Relatives.Of(c).Find(r => r.alive && r.Label == c.visitorRelative)
                : Relatives.Of(c).Find(r => r.alive && r.name == (visitor.Name as NameTriple)?.First);
            string who = rel?.Label ?? visitor.LabelShort;
            if (rel != null)
            {
                rel.alive = false;
                Relatives.Recount(c);
            }
            c.tension = Mathf.Min(100f, c.tension + 35f);
            c.mainCause = "RP_Cause_Grief";
            inmate.needs?.mood?.thoughts.memories.TryGainMemory(RPLifeDefOf.RP_FamilyDied);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, "RP_Chr_VisitorDied".Translate(inmate.LabelShort, who), inmate);
            Find.LetterStack.ReceiveLetter("RP_VisitorDiedLabel".Translate(), "RP_VisitorDiedText".Translate(inmate.LabelShort, who), LetterDefOf.NegativeEvent, new LookTargets(inmate));
            return true;
        }
    }
}
