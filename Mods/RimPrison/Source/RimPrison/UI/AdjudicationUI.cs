using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The adjudication sheet's window (see <see cref="Adjudication"/>): each case that waits, with the evidence and the four answers (the policy's sanction, loss of remission, days added with the days to add, a charge), and below it
    /// what the cases already answered on the sheet came to. A button that cannot be pressed says why. The window can be left: a case still waiting takes the policy's sanction at the next morning's pass.
    /// </summary>
    public class Dialog_Adjudication : Window
    {
        private const float RowH = 96f, Gap = 6f;

        private readonly Map map;
        private readonly Dictionary<int, int> days = new Dictionary<int, int>();
        private Vector2 scroll;

        /// <summary>The sheet whose cases it shows (-1: every case that waits).</summary>
        public int Sheet { get; }

        public Dialog_Adjudication(Map map, int sheet)
        {
            this.map = map;
            Sheet = sheet;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            forcePause = false;
        }

        public override Vector2 InitialSize => new Vector2(800f, 560f);

        public static void Open(Map map) => Find.WindowStack.Add(new Dialog_Adjudication(map, -1));

        private int DaysFor(AdjudicationCase k) => days.TryGetValue(k.id, out int d) ? d : Adjudication.SuggestedDays(k.offence);

        public override void DoWindowContents(Rect inRect)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null) return;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "RP_AdjWindowTitle".Translate());
            Text.Font = GameFont.Small;
            GUI.color = PrisonHUD.Muted;
            string hint = "RP_AdjWindowHint".Translate(Adjudication.MaxDays).ToString();
            float hintH = Text.CalcHeight(hint, inRect.width);
            Widgets.Label(new Rect(0f, 34f, inRect.width, hintH), hint);
            GUI.color = Color.white;
            List<AdjudicationCase> waiting = Adjudication.Waiting(game, Sheet);
            List<AdjudicationCase> decided = game.adjudication.cases.Where(k => k.sheet >= 0 && (Sheet < 0 || k.sheet == Sheet) && k.answer != AdjudicationAnswer.Pending && Find.TickManager.TicksGame - k.answerTick < GenDate.TicksPerHour * 6).ToList();
            var list = new Rect(0f, 40f + hintH, inRect.width, inRect.height - 40f - hintH);
            var view = new Rect(0f, 0f, list.width - 16f, waiting.Count * (RowH + Gap) + decided.Count * 22f + 30f);
            Widgets.BeginScrollView(list, ref scroll, view);
            float y = 0f;
            if (waiting.Count == 0)
            {
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(0f, y, view.width, 24f), "RP_AdjWindowNone".Translate());
                GUI.color = Color.white;
                y += 28f;
            }
            foreach (AdjudicationCase k in waiting)
            {
                DrawCase(new Rect(0f, y, view.width, RowH), game, k);
                y += RowH + Gap;
            }
            foreach (AdjudicationCase k in decided)
            {
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(4f, y, view.width - 4f, 22f), "RP_AdjWindowDecided".Translate(k.inmateName, k.offence.label, Adjudication.AnswerText(k)).ToString().Truncate(view.width - 8f));
                GUI.color = Color.white;
                y += 22f;
            }
            Widgets.EndScrollView();
        }

        private void DrawCase(Rect r, PrisonGame game, AdjudicationCase k)
        {
            Widgets.DrawBoxSolid(r, new Color(1f, 1f, 1f, 0.04f));
            Widgets.DrawBox(r);
            // (The inmate may be on another of the player's home maps: the case is decided there.)
            Map home = Adjudication.HomeOf(k, map) ?? map;
            Pawn p = CourtUtil.FindInmate(home, k.inmateId);
            CompInmate c = p?.Inmate();
            Widgets.Label(new Rect(r.x + 6f, r.y + 3f, r.width - 12f, 24f), "RP_AdjCaseTitle".Translate(k.inmateName, k.offence.label, ((Find.TickManager.TicksGame - k.tick) / (float)GenDate.TicksPerHour).ToString("F0")).ToString().Truncate(r.width - 14f));
            GUI.color = PrisonHUD.Muted;
            Widgets.Label(new Rect(r.x + 6f, r.y + 26f, r.width - 12f, 36f), Adjudication.EvidenceText(k));
            GUI.color = Color.white;
            if (p != null) Widgets.DrawHighlightIfMouseover(new Rect(r.x, r.y, r.width, 26f));
            if (p != null && Widgets.ButtonInvisible(new Rect(r.x, r.y, r.width, 26f))) CameraJumper.TryJumpAndSelect(p);

            float by = r.y + RowH - 32f, bx = r.x + 6f;
            // The policy's sanction.
            Sanction kind = game.punishments.KindFor(k.offence);
            int hours = kind == Sanction.Solitary ? game.punishments.SolitaryHoursFor(k.offence) : game.punishments.HoursFor(k.offence);
            string policy = kind == Sanction.Warning ? "RP_AdjBtn_PolicyWarning".Translate().ToString() : "RP_AdjBtn_Policy".Translate(("RP_Sanction_" + kind).Translate(), hours).ToString();
            Button(new Rect(bx, by, 176f, 28f), policy, "RP_AdjTip_Policy".Translate(), null, game, k, AdjudicationAnswer.Policy);
            bx += 182f;
            string noRemission = c == null ? null : Adjudication.Refused(game, home, k, c, AdjudicationAnswer.LoseRemission);
            Button(new Rect(bx, by, 190f, 28f), "RP_AdjBtn_Remission".Translate(c?.remissionEarned ?? 0).ToString(), "RP_AdjTip_Remission".Translate(), noRemission, game, k, AdjudicationAnswer.LoseRemission);
            bx += 196f;
            // Days added, with the days to add: the button adds, the arrows set them (five at a time, one with shift).
            int step = Event.current.shift ? 1 : 5, now = DaysFor(k);
            string noDays = c == null ? null : Adjudication.Refused(game, home, k, c, AdjudicationAnswer.DaysAdded);
            if (Widgets.ButtonText(new Rect(bx, by, 24f, 28f), "-", active: noDays == null)) days[k.id] = Mathf.Max(1, now - step);
            string daysTip = (k.Evidenced ? "RP_AdjTip_DaysEvidenced" : "RP_AdjTip_DaysUnevidenced").Translate(Adjudication.MaxDays, Money.Format(ReputationBehavior_Lawyer.CourtCosts)).ToString();
            Button(new Rect(bx + 26f, by, 150f, 28f), "RP_AdjBtn_Days".Translate(now).ToString(), daysTip, noDays, game, k, AdjudicationAnswer.DaysAdded, now);
            if (Widgets.ButtonText(new Rect(bx + 178f, by, 24f, 28f), "+", active: noDays == null)) days[k.id] = Mathf.Min(Adjudication.MaxDays, now + step);
            bx += 208f;
            string noCharge = c == null ? null : Adjudication.Refused(game, home, k, c, AdjudicationAnswer.Charge);
            Button(new Rect(bx, by, r.xMax - bx - 6f, 28f), "RP_AdjBtn_Charge".Translate().ToString(), "RP_AdjTip_Charge".Translate(), noCharge, game, k, AdjudicationAnswer.Charge);
        }

        private void Button(Rect r, string label, TaggedString tip, string refusedKey, PrisonGame game, AdjudicationCase k, AdjudicationAnswer answer, int daysToAdd = 0)
        {
            TooltipHandler.TipRegion(r, refusedKey != null ? refusedKey.Translate() : tip);
            if (!Widgets.ButtonText(r, label.Truncate(r.width - 6f), active: refusedKey == null)) return;
            string fell = Adjudication.Answer(game, map, k, answer, daysToAdd);
            if (fell != null) Messages.Message(fell.Translate(), MessageTypeDefOf.RejectInput, historical: false);
        }
    }
}
