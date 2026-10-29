using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPDeathDefOf
    {
        public static MisconductDef RP_Murder;
        public static ThoughtDef RP_DeathInWing;

        static RPDeathDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPDeathDefOf));
    }

    /// <summary>A cover-up the press will dig out on <see cref="dueTick"/>.</summary>
    public class CoverUp : IExposable
    {
        public int dueTick;
        public string victim;

        public void ExposeData()
        {
            Scribe_Values.Look(ref dueTick, "due");
            Scribe_Values.Look(ref victim, "victim");
        }
    }

    /// <summary>
    /// A death in custody has consequences. Killed by another inmate: the killer is charged with murder (moved up a level if there
    /// is a bed, the trial that follows gives the sentence: see <see cref="Trials"/>), the victim's friends and gang become the killer's enemies, the
    /// wing mourns. Killed by staff or by neglect (starvation, blood loss, cold, heat, infection): a state inquiry asks
    /// the warden to cooperate (fine), blame the inmate (nothing now, maybe a scandal later) or suspend the officer.
    /// </summary>
    public static class DeathAftermath
    {
        public const int InquiryFine = 1500, ScandalLawsuit = 3000;
        /// <summary>A death from blood loss or infection this soon after another inmate's blow is that inmate's murder, not the warden's neglect.</summary>
        public const int AssaultMurderTicks = GenDate.TicksPerDay;

        public static void Notify_Died(Pawn victim, Pawn killer, Hediff culprit, DamageInfo? dinfo)
        {
            PrisonGame game = PrisonGame.Instance;
            Map map = victim.MapHeld;
            if (game == null || map == null) return;
            DangerMap.NoteDeath(victim, dinfo, culprit);
            GangExit.Notify_Died(victim, map);
            Mourn(victim, map);
            // (The wing talks of it: the rumour mill. Killed by staff, the twisted version - "the guards killed them", in the same words as ever - is the truth.)
            Rumours.Notify_Death(victim, map, killer != null && killer.Faction == Faction.OfPlayer && killer.GetComp<CompStaff>()?.role != null);
            Kin.Notify_Died(victim);
            if (killer != null && killer != victim && killer.IsPrisonerOfColony && killer.Inmate() is { } kc)
            {
                ChargeWithMurder(game, victim, killer, kc, map, dinfo: dinfo);
                return;
            }
            // A wound that bleeds out or festers later has no damage info: the inmate who hurt them last, within a day, is the killer (the scene is read where it happened).
            if (killer == null && culprit != null && (culprit.def == HediffDefOf.BloodLoss || culprit.def == HediffDefOf.WoundInfection) && RecentAssailant(victim, map) is { } assailant)
            {
                ChargeWithMurder(game, victim, assailant, assailant.Inmate(), map, sceneAt: victim.Inmate().assaultCell);
                return;
            }
            bool byStaff = killer != null && killer.Faction == Faction.OfPlayer && killer.GetComp<CompStaff>()?.role != null;
            bool neglect = killer == null && (culprit != null && (IsNeglect(culprit.def)
                // A heart attack the warden would not call the paramedics for, within the last two days.
                || culprit.def == IncidentWorker_MedicalEmergency.HeartAttack && victim.Inmate() is { careRefusedTick: >= 0 } vc
                   && Find.TickManager.TicksGame - vc.careRefusedTick < 2 * GenDate.TicksPerDay)
                // Dead of a condition the warden was told could be cured and chose to leave (see OldAge).
                || OldAge.Neglected(victim, culprit, dinfo));
            if (byStaff || neglect)
            {
                // (The family will write about it, and not kindly: see CondolenceSystem.)
                CondolenceSystem.Notify_Fault(victim);
                OpenInquiry(game, victim, byStaff ? killer : null, map);
            }
            else if (killer == null) OldAge.Notify_Died(victim, culprit, dinfo);
        }

        /// <summary>The inmate who hurt this one in the last day (see <see cref="CompInmate.assailantId"/>), if they are still here to be charged.</summary>
        public static Pawn RecentAssailant(Pawn victim, Map map)
        {
            if (victim.Inmate() is not { assailantId: >= 0 } vc || Find.TickManager.TicksGame - vc.assaultTick > AssaultMurderTicks) return null;
            Pawn assailant = CourtUtil.FindInmate(map, vc.assailantId);
            return assailant != null && assailant != victim && assailant.Inmate() != null ? assailant : null;
        }

        /// <summary>One of the staff was struck by an inmate this soon ago: a death from blood loss or infection is that inmate's doing, not the prison's neglect.</summary>
        public static bool RecentlyAssaulted(CompStaff sc) => sc.assaultedTick >= 0 && Find.TickManager.TicksGame - sc.assaultedTick <= AssaultMurderTicks;

        internal static bool IsNeglect(HediffDef d) =>
            d == HediffDefOf.Malnutrition || d == HediffDefOf.BloodLoss || d == HediffDefOf.Hypothermia || d == HediffDefOf.Heatstroke
            || d == HediffDefOf.WoundInfection;

        /// <summary>The victim's wing feels it (and a chaplain on staff holds a memorial: calmer).</summary>
        private static void Mourn(Pawn victim, Map map)
        {
            SecurityLevelDef sec = victim.Inmate()?.security;
            bool chaplain = StaffCommute.AllStaff(map).Any(p => p.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Chaplain);
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p == victim || p.Inmate() is not { } c || c.security != sec) continue;
                p.needs.mood?.thoughts.memories.TryGainMemory(RPDeathDefOf.RP_DeathInWing, victim);
                if (chaplain) c.tension = Mathf.Max(0f, c.tension - 5f);
            }
            if (chaplain) Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_Memorial".Translate(victim.LabelShort), victim);
        }

        /// <param name="staffVictim">A member of staff: their own chronicle line and letter come from StaffDeaths; the inmates
        /// who trusted them turn on the killer (rather than the victim's friends and gang).</param>
        /// <returns>The case against the killer: the charge is made now (the record, a higher level if there is a bed, the victim's friends and gang as enemies), the sentence is the trial's to give (see <see cref="Trials"/>).</returns>
        public static MurderTrial ChargeWithMurder(PrisonGame game, Pawn victim, Pawn killer, CompInmate kc, Map map, bool staffVictim = false, DamageInfo? dinfo = null, IntVec3? sceneAt = null)
        {
            game.stats.murders++;
            // What the prosecution has is read off the scene first (before the charge changes who is friends with whom).
            MurderTrial trial = Trials.Open(game, map, victim, killer, kc, staffVictim, dinfo, sceneAt: sceneAt);
            MisconductUtility.Record(killer, kc, PrisonMap.Of(map), RPDeathDefOf.RP_Murder, evidence: true, chronicle: false);
            // A member of staff killed: supermax, when the wing has a free cell; else a level up if there is a bed (or the warden is told the wing keeps them out).
            if (!(staffVictim && SupermaxSystem.TrySend(killer, kc, SupermaxReason.StaffKiller)) && kc.security?.demoteTo is { } up && Unlocks.IsUnlocked(up))
            {
                if (InmateUtility.HasRoomIn(killer, up)) InmateUtility.ChangeSecurity(killer, kc, up);
                else InmateUtility.TellIfWingKeepsThemOut(killer, up);
            }
            // The victim's friends and gang (for a dead officer, the inmates who trusted them) want the killer.
            CompInmate vc = victim.Inmate();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p == killer || p == victim || p.Inmate() is not { } c) continue;
                bool cares = staffVictim ? StaffMemories.ScoreFor(c, victim) >= StaffMemories.TrustedAt
                    : PrisonRelations.Friends(p, victim) || vc?.gang != null && c.gang == vc.gang;
                if (!cares) continue;
                PrisonRelations.MakeEnemies(p, killer);
                c.tension = Mathf.Min(100f, c.tension + 15f);
                c.mainCause = "RP_Cause_Revenge";
                trial.avengers.Add(p.thingIDNumber);
            }
            if (staffVictim) return trial;
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, "RP_Chr_Murder".Translate(killer.LabelShort, victim.LabelShort, Trials.DateText(trial)), killer, victim);
            Find.LetterStack.ReceiveLetter("RP_MurderLabel".Translate(killer.LabelShort), "RP_MurderText".Translate(killer.LabelShort, victim.LabelShort, Trials.DateText(trial), Trials.KnownLinesText(trial)), LetterDefOf.ThreatSmall, killer);
            return trial;
        }

        internal static void OpenInquiry(PrisonGame game, Pawn victim, Pawn officer, Map map)
        {
            game.stats.inquiries++;
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Inquiry", "RP_InquiryLabel".Translate(victim.LabelShort),
                (officer != null ? "RP_InquiryTextOfficer" : "RP_InquiryTextNeglect").Translate(victim.LabelShort, officer?.LabelShort ?? "", Money.Format(InquiryFine)), map, officer);
            letter.amount = victim.thingIDNumber;
            if (officer != null) letter.pawns.Add(officer);
            letter.money = InquiryFine;
            letter.StartTimeout(GenDate.TicksPerDay);
            // The name, for the letter's consequences (the victim is dead and gone): on the letter, as several inquiries can be open at once.
            letter.note = victim.LabelShort;
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>Hourly: buried cover-ups come out.</summary>
        public static void Hourly(PrisonGame game)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = game.story.coverUps.Count - 1; i >= 0; i--)
            {
                CoverUp c = game.story.coverUps[i];
                if (now < c.dueTick) continue;
                game.story.coverUps.RemoveAt(i);
                game.reputation = Mathf.Max(0f, game.reputation - 8f);
                game.ledger.Add(RPDefOf.RP_Fines, -ScandalLawsuit, "RP_ScandalNote".Translate(c.victim));
                Story.Record(RPChronicleDefOf.RP_Chr_Outside, 3, "RP_Chr_CoverUpExposed".Translate(c.victim));
                Find.LetterStack.ReceiveLetter("RP_CoverUpLabel".Translate(), "RP_CoverUpText".Translate(c.victim, Money.Format(ScandalLawsuit)), LetterDefOf.NegativeEvent);
            }
        }
    }

    public class PrisonDecisionWorker_Inquiry : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Inquiry_Cooperate", "RP_Inquiry_Blame", "RP_Inquiry_Suspend" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 0;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            if (option != 2) return null;
            if (l.pawns.Count == 0 || l.pawns[0] is not { Destroyed: false } o || o.GetComp<CompStaff>() is not { role: not null } sc) return "RP_InquiryNoOfficer".Translate();
            // (The state's chief and the warden cannot be suspended: the answer would cost the least and do nothing.)
            if (sc.Protected) return "RP_InquiryProtected".Translate(o.LabelShort);
            // (Nor a callout officer, the riot squad's: they leave on schedule within hours whatever the suspension says, so it would cost nothing.)
            return sc.leaveAtTick >= 0 ? "RP_InquiryCallout".Translate(o.LabelShort) : null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            // (A letter from before the name was kept on it falls back to the last victim.)
            string victim = l.note ?? game.story.lastInquiryVictim ?? "?";
            switch (option)
            {
                case 1:
                    // Nothing now; four times in ten it comes out within the week.
                    if (Rand.Chance(0.4f)) game.story.coverUps.Add(new CoverUp { victim = victim, dueTick = Find.TickManager.TicksGame + Rand.RangeInclusive(2, 6) * GenDate.TicksPerDay });
                    break;
                case 2:
                    Pawn officer = l.pawns[0];
                    if (officer.GetComp<CompStaff>() is { } sc) CorruptionSystem.Suspend(officer, sc);
                    game.reputation = Mathf.Max(0f, game.reputation - 1f);
                    foreach (Pawn o in map.mapPawns.FreeColonistsSpawned)
                        if (o != officer && o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(RPCorruptionDefOf.RP_ColleagueSacked);
                    break;
                default:
                    game.ledger.Add(RPDefOf.RP_Fines, -l.money, "RP_InquiryNote".Translate(victim));
                    game.reputation = Mathf.Max(0f, game.reputation - 2f);
                    break;
            }
        }
    }
}
