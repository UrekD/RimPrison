using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Riot negotiations: the ringleader names a grievance. Negotiating ends the riot at once and turns the grievance
    /// into a promise (see <see cref="DemandSystem"/>); the prison looks weak for it. Refusing leaves it to the guards.
    /// </summary>
    public static class RiotNegotiation
    {
        /// <summary>The need behind the ringleader's anger (their cause if it maps to a need, else the population's worst).</summary>
        public static NeedDef Grievance(Pawn leader, CompInmate c)
        {
            switch (c.mainCause)
            {
                case "RP_Cause_Hunger": return NeedDefOf.Food;
                case "RP_Cause_Filthy": return RPDefOf.RP_Hygiene;
                case "RP_Cause_NoFamily": return RPVisitDefOf.RP_Family;
            }
            NeedDef worst = NeedDefOf.Food;
            float worstAvg = 2f;
            foreach (NeedDef nd in DemandSystem.CandidateNeeds())
            {
                (float avg, _, int n) = DemandSystem.Survey(leader.Map, nd);
                if (n > 0 && avg < worstAvg)
                {
                    worstAvg = avg;
                    worst = nd;
                }
            }
            return worst;
        }

        public static void Negotiate(Map map, ChoiceLetter_PrisonDecision l)
        {
            PrisonGame game = PrisonGame.Instance;
            int rioting = l.pawns.Count(p => p != null && !p.Dead && p.MentalStateDef == RPMentalStateDefOf.RP_Rioting);
            if (rioting == 0)
            {
                Messages.Message("RP_RiotAlreadyOver".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
                return;
            }
            int calmed = 0;
            foreach (Pawn p in l.pawns)
            {
                if (p == null || p.Dead || p.MentalStateDef != RPMentalStateDefOf.RP_Rioting) continue;
                p.MentalState.RecoverFromState();
                if (p.Inmate() is { } c) c.tension = Mathf.Max(0f, c.tension - 20f);
                calmed++;
            }
            // With a promise already open, the new grievance replaces it and the clock restarts; giving in again
            // costs more face.
            if (l.need != null) DemandSystem.Accept(game, l.need, DemandSystem.InmateById(map, l.amount));
            game.reputation = Mathf.Max(0f, game.reputation - (game.stats.riotsNegotiated > 0 ? 2f : 1f));
            game.stats.riotsNegotiated++;
            RiotReview.Notify_Negotiated(l.amount);
            Messages.Message("RP_RiotNegotiated".Translate(calmed, l.need?.label ?? "?"), MessageTypeDefOf.NeutralEvent);
            // A deal with the riot's ringleader covers the hostage they took since: they are let go as part of it.
            if (HostageCrisis.Running(map) is { } crisis && crisis.pawns.Skip(1).Any(l.pawns.Contains))
                ((HostageArc)crisis.def.Worker).Finish(crisis, map, HostageHow.Conceded, HostageFate.Freed, l.need?.label ?? "?");
        }
    }
}
