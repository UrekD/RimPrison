using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Life on parole: a parolee walks out on terms, a job and an address (already in place, or still to find) for the rest of the sentence
    /// (five to thirty days). The terms are in the register and in the chronicle. Whether they stay out is still the reoffend roll made at release
    /// (<see cref="ParoleSystem.ReoffendChance"/>, which a job waiting already lowers); what the terms add is the cause: a parolee who is rearrested
    /// is arrested for the term they broke (or for a new offence), the cause the rearrest carries and every message, chronicle line and the letter
    /// that comes with them back names. Someone who served their whole sentence has no terms, and is only ever arrested for a new offence.
    /// </summary>
    public static class ParoleLife
    {
        public const int MinDays = 5, MaxDays = 30;
        public const string NewOffence = "RP_Violation_NewOffence";

        /// <summary>The chance a parolee certified in a trade (see <see cref="Vocation"/>) has a job in place on the day they walk out, when no job was already waiting for them.</summary>
        public const float TradeJobChance = 0.5f;

        /// <summary>The chance the "job" term of their parole is in place: certain when a job was waiting, else <see cref="TradeJobChance"/> for a certified trade, else none.</summary>
        public static float JobInPlaceChance(CompInmate c) => c.jobWaiting ? 1f : Vocation.HasTrade(c) ? TradeJobChance : 0f;

        /// <summary>At release on parole: how long they are supervised, and which of the terms are already in place.</summary>
        public static void Impose(FormerInmate e, CompInmate c)
        {
            int left = c.IsLifer ? MinDays : Mathf.RoundToInt(Mathf.Clamp(c.TermDays - c.DaysServed, MinDays, MaxDays));
            e.paroleEndTick = Find.TickManager.TicksGame + left * GenDate.TicksPerDay;
            float jobOdds = JobInPlaceChance(c);
            e.jobSecured = jobOdds >= 1f || jobOdds > 0f && Rand.Chance(jobOdds);
            // (A job in place that the trade found, not one waiting for them from outside: the register keeps the trade for the news that comes.)
            e.tradeJob = e.jobSecured && !c.jobWaiting ? Vocation.Trade(c) : null;
            e.addressSecured = c.familyCount > 0;
        }

        public static bool Supervised(FormerInmate e, int now) => e.paroleEndTick >= 0 && now < e.paroleEndTick;

        public static string JobTerm(FormerInmate e) =>
            e.tradeJob != null ? "RP_ParoleTerm_JobTrade".Translate(e.tradeJob.label) : (e.jobSecured ? "RP_ParoleTerm_JobKept" : "RP_ParoleTerm_JobFind").Translate();

        public static string AddressTerm(FormerInmate e) => (e.addressSecured ? "RP_ParoleTerm_AddressFamily" : "RP_ParoleTerm_AddressHostel").Translate();

        /// <summary>The terms in the chronicle: "keep the job waiting for them, live with their family".</summary>
        public static void RecordTerms(FormerInmate e, Pawn p)
        {
            int days = Mathf.Max(1, Mathf.RoundToInt((e.paroleEndTick - e.releasedTick) / (float)GenDate.TicksPerDay));
            Story.Record(RPChronicleDefOf.RP_Chr_Release, 1, "RP_Chr_ParoleTerms".Translate(e.name, JobTerm(e), AddressTerm(e), days), p);
        }

        /// <summary>How a parolee is getting on, for a letter from them: "working at the job that was waiting, living with family".</summary>
        public static string LetterLine(FormerInmate e) =>
            "RP_ParoleLetterLine".Translate(e.tradeJob != null ? "RP_ParoleLetter_JobTrade".Translate(e.tradeJob.label) : (e.jobSecured ? "RP_ParoleLetter_Job" : "RP_ParoleLetter_NoJob").Translate(), (e.addressSecured ? (e.lodged ? "RP_ParoleLetter_Lodging" : "RP_ParoleLetter_Family") : "RP_ParoleLetter_Hostel").Translate());

        /// <summary>
        /// What a parolee who is going to be rearrested is arrested for: a new offence, or the term they were least set up to keep (no job, no home to
        /// go to), or one they could break whatever their luck (the weekly reports, a job lost, an address left).
        /// </summary>
        public static string PickViolation(FormerInmate e)
        {
            var options = new List<(string key, float weight)>
            {
                (NewOffence, 1f),
                (e.jobSecured ? "RP_Violation_JobLost" : "RP_Violation_NoWork", e.jobSecured ? 1f : 1.5f),
                (e.addressSecured ? "RP_Violation_AddressLeft" : "RP_Violation_NoAddress", e.addressSecured ? 0.5f : 1.5f),
                ("RP_Violation_Reports", 1f),
            };
            return options.RandomElementByWeight(o => o.weight).key;
        }
    }

    /// <summary>A letter from somebody on parole (a friend of theirs from inside if there was one) who says how the terms are going.</summary>
    public class MailKindWorker_Parolee : MailKindWorker
    {
        /// <summary>
        /// Those under supervision who have not been rearrested. A release that is still under review (see <see cref="Supervision"/>) has no outcome yet, so whoever has left writes, and what they
        /// write is how they are getting on now (a job lost is in the letter that follows it); only a rearrest ends the letters. An older release (no case) is as it was: the register knew who would be
        /// rearrested (their news never comes: it is set only for those who stay out, and then it is what they sent); until then, and after, they write no letters.
        /// </summary>
        private static List<FormerInmate> Parolees()
        {
            int now = Find.TickManager.TicksGame;
            PrisonGame game = PrisonGame.Instance;
            return game.story.formerInmates.FindAll(e => e.paroled && ParoleLife.Supervised(e, now) && (e.supervisionId >= 0 && game.supervision.Find(e.supervisionId) is { } sc ? Supervision.MayWrite(sc) : e.newsTick >= 0 || e.news != null));
        }

        public override bool CanSend(Pawn p, CompInmate c) => Parolees().Count > 0;

        protected override string[] Open(Pawn p, CompInmate c, Relative sender)
        {
            List<FormerInmate> parolees = Parolees();
            if (parolees.Count == 0) return null;
            FormerInmate who = parolees.Find(e => e.friendIds.Contains(p.thingIDNumber)) ?? parolees.RandomElement();
            return new[] { p.LabelShort, who.name, ParoleLife.LetterLine(who) };
        }
    }
}
