using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>A visitor who dies in the prison is the inmate's relative dead: the family's record, the grief, the chronicle (gap analysis 5, F2: they stayed alive in the record and could visit again).</summary>
    public static partial class AutoTest
    {
        private static void CheckVisitorDeathIsARelativeDeath(Map map)
        {
            Guarded("a visitor who dies in the prison is a relative dead", map, r =>
            {
                const string check = "visitor death";
                if (!PrisonMap.Of(map).zones.Any(RPVisitDefOf.RP_Visitation)) { ReviewFail(check, "the check's own premise: no visitation zone"); return; }
                Pawn inmate = Throwaway(map, r);
                CompInmate c = inmate.Inmate();
                List<Relative> family = Relatives.Of(c);
                // (Two who may come: one dies in the middle of the visit, the other on the way out after it.)
                while (family.Count(x => x.CanVisit) < 2) family.Add(Relatives.Make());
                Relatives.Recount(c);
                int deaths0 = PrisonGame.Instance.stats.deaths, letters0 = Find.LetterStack.LettersListForReading.Count;
                r.Add(() =>
                {
                    foreach (Letter l in Find.LetterStack.LettersListForReading.Skip(letters0).ToList()) Find.LetterStack.RemoveLetter(l);
                });

                Relative KillVisitor(string when, bool walkingOut)
                {
                    if (!VisitationSystem.SpawnVisitor(map, inmate, c, booth: false) || c.visitor is not { } visitor)
                    {
                        ReviewFail(check, $"the check's own premise: no visitor came ({when})");
                        return null;
                    }
                    r.Add(() =>
                    {
                        if (visitor.Corpse is { Destroyed: false } corpse) corpse.Destroy();
                        if (!visitor.Destroyed) visitor.Destroy();
                    });
                    Relative rel = family.Find(x => x.Label == c.visitorRelative);
                    if (rel == null) { ReviewFail(check, $"the check's own premise: the visitor is nobody of the family ({c.visitorRelative}, {when})"); return null; }
                    if (walkingOut) VisitationSystem.EndVisit(c, visitor);
                    int count0 = c.familyCount;
                    visitor.Kill(null);
                    if (rel.alive || c.familyCount != count0 - 1)
                        ReviewFail(check, $"{when}: the {rel.Label} who died visiting is alive {rel.alive} in the family's record (family who may visit {count0} -> {c.familyCount})");
                    for (int i = 0; i < 40; i++)
                        if (Relatives.RandomVisitor(c) == rel) { ReviewFail(check, $"{when}: the dead {rel.Label} can be picked to visit again"); break; }
                    return rel;
                }

                Relative first = KillVisitor("in the middle of the visit", walkingOut: false);
                if (first == null) return;
                if (inmate.needs.mood?.thoughts.memories.GetFirstMemoryOfDef(RPLifeDefOf.RP_FamilyDied) == null || c.mainCause != "RP_Cause_Grief")
                    ReviewFail(check, $"the inmate does not grieve the relative who died visiting (memory {inmate.needs.mood?.thoughts.memories.GetFirstMemoryOfDef(RPLifeDefOf.RP_FamilyDied) != null}, cause {c.mainCause})");
                if (!Find.LetterStack.LettersListForReading.Skip(letters0).Any(l => l.Label.RawText == "RP_VisitorDiedLabel".Translate().RawText))
                    ReviewFail(check, "no letter said a visitor died");
                Relative second = KillVisitor("walking out after the visit", walkingOut: true);
                if (PrisonGame.Instance.stats.deaths != deaths0) ReviewFail(check, $"a visitor's death was counted as a death in custody ({deaths0} -> {PrisonGame.Instance.stats.deaths})");
                RPLog.Message($"[RimPrison:test] visitor death: {first.Label} (during the visit) and {second?.Label ?? "nobody"} (walking out) dead in the record, the inmate grieving, no custody death");
            });
        }
    }
}
