using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPTrainingDefOf
    {
        public static ThoughtDef RP_Trained;

        static RPTrainingDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPTrainingDefOf));
    }

    /// <summary>
    /// A training course, from the staff record: paid, a day away (off site like leave), and they come back better at the
    /// job (two levels in its skill: melee for guards, the role's key skill for everyone else) and pleased to be invested
    /// in. One course a fortnight each, and only up to a solid professional level (16): past that, experience teaches.
    /// </summary>
    public static class StaffTraining
    {
        public const int Cost = 300, Days = 1, CooldownDays = 15, Levels = 2, CourseCap = 16;

        public static SkillDef SkillFor(StaffRoleDef role) =>
            role == null ? null : role.guardDuties ? SkillDefOf.Melee : role.keySkills.Count > 0 ? role.keySkills[0] : null;

        /// <summary>Why they cannot go now (null: they can).</summary>
        public static string CannotGo(Pawn p, CompStaff sc)
        {
            if (sc?.statePosted == true) return "RP_TrainingStatePosted".Translate();
            if (sc?.role is { warden: true }) return "RP_TrainingWarden".Translate();
            if (sc?.role == null || SkillFor(sc.role) is not { } skill || p.skills?.GetSkill(skill) is not { TotallyDisabled: false } rec) return "RP_TrainingNoSkill".Translate();
            if (sc.leaveAtTick >= 0) return "RP_TrainingTemporary".Translate();
            if (rec.Level + Levels > CourseCap) return "RP_TrainingMaxed".Translate(CourseCap);
            if (sc.trainingPending || StaffLives.OnLeave(sc)) return "RP_TrainingAway".Translate();
            if (CorruptionSystem.Suspended(sc) || IncidentWorker_StaffStrike.Striking(p)) return "RP_TrainingNotNow".Translate();
            int since = Find.TickManager.TicksGame - sc.lastTrainedTick;
            if (sc.lastTrainedTick >= 0 && since < CooldownDays * GenDate.TicksPerDay)
                return "RP_TrainingRecently".Translate(((CooldownDays * GenDate.TicksPerDay - since) / (float)GenDate.TicksPerDay).ToString("0"));
            if (PrisonGame.Instance.ledger.funds < Cost) return "RP_CannotAfford".Translate(Money.Format(Cost));
            return null;
        }

        public static bool Send(Pawn p, CompStaff sc)
        {
            if (CannotGo(p, sc) != null || !PrisonGame.Instance.ledger.TrySpend(RPDefOf.RP_Wages, Cost, "RP_TrainingNote".Translate(p.LabelShort))) return false;
            sc.trainingPending = true;
            sc.leaveUntilTick = Find.TickManager.TicksGame + Days * GenDate.TicksPerDay;
            StaffCareers.InvalidateLeads();
            Roster.Invalidate();
            Messages.Message("RP_TrainingSent".Translate(p.LabelShort, SkillFor(sc.role).label), p, MessageTypeDefOf.NeutralEvent, historical: false);
            return true;
        }

        /// <summary>A course whose time ran out while they were kept on site (the prison is never left empty): done
        /// in-house. Not for someone who has left the staff since.</summary>
        public static void CheckInHouse(Pawn p, CompStaff sc)
        {
            if (!sc.trainingPending || sc.role == null || p?.Spawned != true || StaffLives.OnLeave(sc)) return;
            sc.leaveUntilTick = -1;
            Roster.Invalidate();
            Complete(p, sc);
        }

        /// <summary>Back through the gate after the course.</summary>
        public static void Complete(Pawn p, CompStaff sc)
        {
            sc.trainingPending = false;
            sc.lastTrainedTick = Find.TickManager.TicksGame;
            SkillDef skill = SkillFor(sc.role);
            if (skill != null && p.skills?.GetSkill(skill) is { TotallyDisabled: false } rec) rec.Level = Mathf.Min(CourseCap, rec.Level + Levels);
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPTrainingDefOf.RP_Trained);
            PrisonGame.Instance.stats.staffTrained++;
            Story.Record(RPChronicleDefOf.RP_Chr_Staff, 1, "RP_Chr_Trained".Translate(p.LabelShort, skill?.label ?? "?"), p);
        }
    }
}
