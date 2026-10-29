using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The people who applied for a post (see <see cref="ApplicantPool"/>): per applicant their portrait, name, age, the skills the post's work uses (with passion), the work of the post they cannot
    /// do and their traits, and a button to hire them; below, the agency's hire at once. The rows' texts are made when the window opens and whenever the pool changes, not every frame.
    /// </summary>
    public class Dialog_Applicants : Window
    {
        private sealed class Row
        {
            public Applicant applicant;
            public Pawn pawn;
            /// <summary>The name and age (cut to the row's width), the skills line and the traits as vanilla presents them, and what they cannot do (all cut to the width): made when the rows are, not every frame.</summary>
            public string name, cannotLine;
            /// <summary>The full name and the skills, for the tooltip of the name and the skills line; the traits' tooltip.</summary>
            public string cvTip, traitsTip;
            public bool cannot;
            /// <summary>The skills of the post: the text, its width and the passion (for the flame).</summary>
            public (string text, float width, Passion passion)[] skills;
            public (string text, float width, Color color)[] traits;
        }

        private const float RowHeight = 96f, SkillGap = 10f, TraitGap = 4f, PassionRoom = 20f;

        private readonly Map map;
        private readonly StaffRoleDef role;
        private readonly ApplicantPool pool;
        private readonly string title, feeLine, shortReason, agencyLabel, agencyTip, agencyShort, hireLabel, hireShort, canDoAll;
        private readonly List<SkillDef> skillDefs;
        private List<Row> rows = new List<Row>();
        private string poolLine;
        private int builtVersion = -1, roundShown, nextLineHour = int.MinValue;
        private float builtWidth;
        private string nextLine;

        public Dialog_Applicants(Map map, StaffRoleDef role)
        {
            this.map = map;
            this.role = role;
            pool = PrisonGame.Instance.applicants;
            roundShown = ApplicantPool.RoundAt(ApplicantPool.Now);
            title = "RP_ApplicantsTitle".Translate(role.LabelCap).ToString();
            feeLine = "RP_ApplicantsFee".Translate(Money.Format(role.hireCost), Money.Format(role.dailyWage)).ToString();
            shortReason = "RP_CannotAfford".Translate(Money.Format(role.hireCost)).ToString();
            hireLabel = "RP_ApplicantHire".Translate().ToString();
            hireShort = "RP_ApplicantShort".Translate(Money.Format(role.hireCost)).ToString();
            canDoAll = "RP_ApplicantCanDoAll".Translate().ToString();
            int agencyFee = StaffUtility.AgencyFee(role);
            agencyLabel = "RP_AgencyHire".Translate(Money.Format(agencyFee)).ToString();
            agencyTip = "RP_AgencyHireTip".Translate(Money.Format(agencyFee), StaffUtility.AgencyFeeFactor).ToString();
            agencyShort = "RP_ApplicantShort".Translate(Money.Format(agencyFee)).ToString();
            // The skills the post's work is done with, in the order its work types name them.
            skillDefs = role.workTypes.Where(w => w.relevantSkills != null).SelectMany(w => w.relevantSkills).Distinct().ToList();
            if (skillDefs.Count == 0) skillDefs = role.keySkills.ToList();
            // (No sound as it opens or closes: the sounds pick their grain, volume and pitch from the game's random numbers, and opening a post again in the round must draw none.)
            soundAppear = null;
            soundClose = null;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = false;
            forcePause = false;
        }

        public override Vector2 InitialSize => new Vector2(780f, 150f + ApplicantPool.PerRole * RowHeight + 90f);

        /// <summary>The width of the text of a row (the portrait on the left and the Hire button on the right left out).</summary>
        private static float TextWidth(float rowWidth) => rowWidth - 82f - 140f;

        private void Rebuild(float width)
        {
            builtVersion = pool.version;
            builtWidth = width;
            Text.Font = GameFont.Small;
            float w = TextWidth(width);
            rows = pool.For(role).Select(a => Build(a, w)).ToList();
            int made = pool.MadeThisRound(role);
            poolLine = made == 0 ? "RP_ApplicantsNobody".Translate(role.label).ToString()
                : rows.Count == 0 ? "RP_ApplicantsNoneLeft".Translate().ToString()
                : made < ApplicantPool.PerRole ? "RP_ApplicantsFew".Translate(made, ApplicantPool.PerRole).ToString() : null;
        }

        private Row Build(Applicant a, float w)
        {
            Pawn p = a.pawn;
            var row = new Row { applicant = a, pawn = p };
            string full = "RP_ApplicantName".Translate(p.Name?.ToStringFull ?? p.LabelCap, p.ageTracker.AgeBiologicalYears).ToString();
            row.name = full.Truncate(w);
            var skills = skillDefs.Select(s =>
            {
                SkillRecord rec = p.skills?.GetSkill(s);
                string level = rec == null || rec.TotallyDisabled ? "-" : rec.Level.ToString();
                return (label: s.LabelCap.ToString(), level, passion: rec?.passion ?? Passion.None);
            }).ToList();
            row.cvTip = full + "\n" + string.Join("\n", skills.Select(s => $"{s.label} {s.level}" + (s.passion == Passion.None ? "" : " - " + (s.passion == Passion.Major ? "RP_ApplicantPassionMajor" : "RP_ApplicantPassionMinor").Translate())));
            // (Kept to the line: when they do not all fit, each label is cut to its share.)
            (string text, float width, Passion passion)[] segments = skills.Select(s =>
            {
                string text = $"{s.label} {s.level}";
                return (text, Text.CalcSize(text).x + PassionRoom, s.passion);
            }).ToArray();
            if (segments.Length > 0 && segments.Sum(s => s.width) + SkillGap * (segments.Length - 1) > w)
            {
                float share = (w - SkillGap * (segments.Length - 1)) / segments.Length;
                segments = segments.Select(s => s.width <= share ? s : (s.text.Truncate(Mathf.Max(0f, share - PassionRoom)), share, s.passion)).ToArray();
            }
            row.skills = segments;
            // (The traits as RimWorld shows them: sorted, without a suppressed gene trait, a suppressed trait greyed and a gene's in the gene colour. Never what the mod keeps from the warden.)
            List<Trait> traits = p.story?.traits?.TraitsSorted.ToList() ?? new List<Trait>();
            row.traitsTip = string.Join("\n\n", traits.Select(t => t.LabelCap + "\n" + t.TipString(p)));
            row.traits = TraitSegments(traits, w);
            // (The post's work they cannot do: every applicant can do all of it, the generator takes nobody else, so this is a line that shows what the CV must not hide, not a list that fills.)
            row.cannot = role.workTypes.Any(x => p.WorkTypeIsDisabled(x));
            string cannotNames = string.Join(", ", role.workTypes.Where(x => p.WorkTypeIsDisabled(x)).Select(x => x.labelShort.CapitalizeFirst()));
            Text.Font = GameFont.Tiny;
            row.cannotLine = (row.cannot ? "RP_ApplicantCannot".Translate(cannotNames).ToString() : canDoAll).Truncate(w);
            Text.Font = GameFont.Small;
            return row;
        }

        private (string text, float width, Color color)[] TraitSegments(List<Trait> traits, float w)
        {
            if (traits.Count == 0)
            {
                string none = "RP_ApplicantNoTraits".Translate().ToString();
                return new[] { (none, Text.CalcSize(none).x + 2f, Color.white) };
            }
            var segments = new List<(string, float, Color)>();
            float ellipsis = Text.CalcSize("...").x + 2f, x = 0f;
            for (int i = 0; i < traits.Count; i++)
            {
                Trait t = traits[i];
                string text = t.LabelCap + (i < traits.Count - 1 ? "," : "");
                float width = Text.CalcSize(text).x + 2f;
                // (What does not fit is left out, with an ellipsis: the tooltip has them all.)
                if (x + width > w - (i < traits.Count - 1 ? ellipsis : 0f))
                {
                    if (x + ellipsis <= w) segments.Add(("...", ellipsis, Color.white));
                    break;
                }
                segments.Add((text, width, t.Suppressed ? ColoredText.SubtleGrayColor : t.sourceGene != null ? ColoredText.GeneColor : Color.white));
                x += width + TraitGap;
            }
            return segments.ToArray();
        }

        public override void DoWindowContents(Rect inRect)
        {
            // (A round that ended while the window was open: its applicants are gone, or about to be. Opening the post again is the way to the next round's.)
            if (ApplicantPool.RoundAt(ApplicantPool.Now) != roundShown || !Unlocks.IsUnlocked(role))
            {
                Close();
                return;
            }
            if (builtVersion != pool.version || !Mathf.Approximately(builtWidth, inRect.width)) Rebuild(inRect.width);
            PrisonGame game = PrisonGame.Instance;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), title);
            Text.Font = GameFont.Small;
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(0f, 34f, inRect.width, 24f), feeLine);
            int hour = (ApplicantPool.NextRoundTick(ApplicantPool.Now) - ApplicantPool.Now) / GenDate.TicksPerHour;
            if (hour != nextLineHour)
            {
                nextLineHour = hour;
                nextLine = "RP_ApplicantsNextRound".Translate((ApplicantPool.NextRoundTick(ApplicantPool.Now) - ApplicantPool.Now).ToStringTicksToPeriod()).ToString();
            }
            Widgets.Label(new Rect(0f, 56f, inRect.width, 24f), nextLine);
            GUI.color = Color.white;
            float y = 84f;
            if (poolLine != null)
            {
                GUI.color = PrisonHUD.Warn;
                Widgets.Label(new Rect(0f, y, inRect.width, 24f), poolLine);
                GUI.color = Color.white;
                y += 26f;
            }
            bool afford = game.ledger.CanAfford(role.hireCost);
            foreach (Row row in rows)
            {
                DrawRow(new Rect(0f, y, inRect.width, RowHeight - 4f), row, afford);
                y += RowHeight;
            }
            // The agency: a stranger at once, at twice the fee.
            var agency = new Rect(0f, inRect.height - 40f, 300f, 34f);
            bool agencyAfford = game.ledger.CanAfford(StaffUtility.AgencyFee(role));
            TooltipHandler.TipRegion(agency, agencyTip);
            if (Widgets.ButtonText(agency, agencyLabel, active: agencyAfford)) StaffUtility.HireThroughAgency(map, role);
            if (!agencyAfford)
            {
                GUI.color = PrisonHUD.Bad;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(agency.xMax + 10f, agency.y + 8f, 280f, 20f), agencyShort);
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
        }

        private void DrawRow(Rect r, Row row, bool afford)
        {
            Widgets.DrawMenuSection(r);
            GUI.DrawTexture(new Rect(r.x + 8f, r.y + 8f, 64f, 64f), PortraitsCache.Get(row.pawn, new Vector2(64f, 64f), Rot4.South, new Vector3(0f, 0f, 0.3f), 1.6f));
            float x = r.x + 82f, w = TextWidth(r.width);
            Rect nameRect = new Rect(x, r.y + 4f, w, 24f), skillsRect = new Rect(x, r.y + 26f, w, 22f), traitsRect = new Rect(x, r.y + 48f, w, 22f);
            Widgets.Label(nameRect, row.name);
            float sx = x;
            foreach ((string text, float width, Passion passion) in row.skills)
            {
                Widgets.Label(new Rect(sx, r.y + 26f, width, 22f), text);
                if (passion != Passion.None)
                    GUI.DrawTexture(new Rect(sx + width - 18f, r.y + 29f, 14f, 14f), passion == Passion.Major ? SkillUI.PassionMajorIcon : SkillUI.PassionMinorIcon);
                sx += width + SkillGap;
            }
            // (The name and the skills share a tooltip: the name in full and every skill with its passion.)
            TooltipHandler.TipRegion(new Rect(x, r.y + 4f, w, 44f), row.cvTip);
            float tx = x;
            foreach ((string text, float width, Color color) in row.traits)
            {
                GUI.color = color;
                Widgets.Label(new Rect(tx, traitsRect.y, width, 22f), text);
                tx += width + TraitGap;
            }
            GUI.color = Color.white;
            TooltipHandler.TipRegion(traitsRect, row.traitsTip);
            GUI.color = row.cannot ? PrisonHUD.Bad : PrisonHUD.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(x, r.y + 70f, w, 18f), row.cannotLine);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            var hire = new Rect(r.xMax - 130f, r.y + 12f, 122f, 32f);
            if (Widgets.ButtonText(hire, hireLabel, active: afford)) pool.TryHire(map, row.applicant);
            if (!afford)
            {
                TooltipHandler.TipRegion(hire, shortReason);
                GUI.color = PrisonHUD.Bad;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.UpperCenter;
                Widgets.Label(new Rect(hire.x - 6f, hire.yMax + 2f, hire.width + 12f, 30f), hireShort);
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
        }
    }

    public static class HiringUI
    {
        /// <summary>The warden opened a post in the Staff tab: its applicants for the round are made if there are none yet, and the window shows them.</summary>
        public static void Open(Map map, StaffRoleDef role)
        {
            Find.WindowStack.WindowOfType<Dialog_Applicants>()?.Close();
            PrisonGame.Instance.applicants.Open(role);
            Find.WindowStack.Add(new Dialog_Applicants(map, role));
        }
    }
}
