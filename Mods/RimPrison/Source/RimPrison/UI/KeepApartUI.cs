using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>The window of a file's "keep apart" button: every other inmate, with a button to write the order against them or to lift the one that is there.</summary>
    public class Dialog_KeepApart : Window
    {
        private readonly Pawn who;
        /// <summary>The inmates offered, with their row's label (made once: a row's text does not change while the window is open). One who leaves while it is open is taken off the list.</summary>
        private readonly List<(Pawn pawn, string label)> rows;
        private readonly string title, hint, liftLabel, setLabel;
        private float hintHeight = -1f;
        private Vector2 scroll;

        public Dialog_KeepApart(Pawn who)
        {
            this.who = who;
            rows = who.MapHeld.mapPawns.PrisonersOfColonySpawned.Where(p => p != who && KeepApart.Orderable(p)).OrderBy(p => p.LabelShort)
                .Select(p => (p, $"#{p.Inmate().inmateNumber:D5}  {p.LabelShortCap}  ({p.Inmate().security.label})")).ToList();
            (title, hint) = ("RP_KeepApartTitle".Translate(who.LabelShort).ToString(), "RP_KeepApartHint".Translate().ToString());
            (liftLabel, setLabel) = ("RP_KeepApartLiftShort".Translate().ToString(), "RP_KeepApartButton".Translate().ToString());
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = false;
            forcePause = false;
        }

        public override Vector2 InitialSize => new Vector2(400f, 520f);

        public override void DoWindowContents(Rect inRect)
        {
            // (The subject released, transferred or dead since the window opened: nothing to write an order for, so the window closes.)
            if (!KeepApart.Orderable(who))
            {
                Close();
                return;
            }
            // (The same for any other: not offered, so no button is left that does nothing.)
            rows.RemoveAll(row => !KeepApart.Orderable(row.pawn));
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), title);
            Text.Font = GameFont.Small;
            GUI.color = PrisonHUD.Muted;
            if (hintHeight < 0f) hintHeight = Text.CalcHeight(hint, inRect.width);
            Widgets.Label(new Rect(0f, 34f, inRect.width, hintHeight), hint);
            GUI.color = Color.white;
            var list = new Rect(0f, 38f + hintHeight, inRect.width, inRect.height - 38f - hintHeight);
            var view = new Rect(0f, 0f, list.width - 16f, rows.Count * 26f);
            Widgets.BeginScrollView(list, ref scroll, view);
            for (int i = 0; i < rows.Count; i++)
            {
                (Pawn other, string label) = rows[i];
                var row = new Rect(0f, i * 26f, view.width, 26f);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                Widgets.Label(new Rect(row.x + 4f, row.y + 2f, row.width - 110f, 24f), label.Truncate(row.width - 112f));
                bool ordered = KeepApart.Ordered(who, other);
                if (Widgets.ButtonText(new Rect(row.xMax - 100f, row.y + 1f, 98f, 24f), ordered ? liftLabel : setLabel)) KeepApart.Toggle(who, other, "warden");
            }
            Widgets.EndScrollView();
        }
    }

    /// <summary>The buttons of the keep-apart orders on the file and the Intel tab.</summary>
    public static class KeepApartUI
    {
        public static void OpenPicker(Pawn p) => Find.WindowStack.Add(new Dialog_KeepApart(p));

        /// <summary>A row's button for one pair: "Keep apart", or "Lift" when they have the order. Greyed when one of them is not an inmate here.</summary>
        public static void PairButton(Rect r, Pawn a, Pawn b, string reason)
        {
            bool ready = a != null && b != null && KeepApart.Orderable(a) && KeepApart.Orderable(b);
            bool ordered = ready && KeepApart.Ordered(a, b);
            TooltipHandler.TipRegion(r, (ordered ? "RP_KeepApartLiftTip" : "RP_KeepApartPairTip").Translate(a?.LabelShort ?? "?", b?.LabelShort ?? "?"));
            if (Widgets.ButtonText(r, (ordered ? "RP_KeepApartLiftShort" : "RP_KeepApartButton").Translate(), active: ready)) KeepApart.Toggle(a, b, reason);
        }

        /// <summary>The inmate witness of the case who can be kept apart from the accused (known, here and orderable), or null: what the button's readiness and its menu both go by.</summary>
        private static Pawn Witness(TrialWitness w, Pawn accused, List<Pawn> inmates)
        {
            if (w.staff || !w.known) return null;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].thingIDNumber == w.id) return inmates[i] != accused && KeepApart.Orderable(inmates[i]) ? inmates[i] : null;
            return null;
        }

        /// <summary>
        /// The trial row's button: a menu of the witnesses who can be kept apart from the accused (or have been). The button is live when there is one such witness (the same test as the menu's, a scan with no list built); the menu's list is built only when it is pressed.
        /// </summary>
        public static void WitnessButton(Rect r, Pawn accused, List<TrialWitness> witnesses, List<Pawn> inmates)
        {
            bool ready = false;
            if (accused != null && KeepApart.Orderable(accused))
                for (int i = 0; i < witnesses.Count && !ready; i++) ready = Witness(witnesses[i], accused, inmates) != null;
            TooltipHandler.TipRegion(r, "RP_KeepApartTrialTip".Translate(accused?.LabelShort ?? "?"));
            if (!Widgets.ButtonText(r, "RP_KeepApartButton".Translate(), active: ready)) return;
            var options = new List<FloatMenuOption>();
            foreach (TrialWitness w in witnesses)
            {
                Pawn witness = Witness(w, accused, inmates);
                if (witness == null) continue;
                bool ordered = KeepApart.Ordered(witness, accused);
                options.Add(new FloatMenuOption((ordered ? "RP_KeepApartLiftOption" : "RP_KeepApartOption").Translate(witness.LabelShort, accused.LabelShort), () => KeepApart.Toggle(witness, accused, "trial")));
            }
            if (options.Count > 0) Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
