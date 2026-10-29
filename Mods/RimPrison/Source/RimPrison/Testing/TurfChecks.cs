using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Gang turf (events plan): a gang holds a place after a day in which most of the inmates there are its members; rivals there
    /// are afraid and tense, the holders at ease; the place flips with its line and letter; a war has a stake and settles it; and a hidden
    /// gang's turf is never shown or told. Works on throwaway inmates standing in the yard, and puts back everything it touches.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The seeded turf of the saveload plan: its patch's anchor (-1 none) and what the patch held before, to put back after the reload.</summary>
        private static int turfSeedAnchor = -1;
        private static (PrisonZoneDef zone, GangDef gang, int since, List<(GangDef raider, int net)> raids)? turfSeedWas;
        /// <summary>The war the saveload plan made fight over the seeded patch, and the stake it had before.</summary>
        private static (int id, int stake)? turfSeedWar;

        private static void TurfTest(Map map)
        {
            Guarded("turf", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                TurfBook book = pm.turf;
                GangDef saints = DefDatabase<GangDef>.GetNamed("RP_Gang_Saints"), ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"), brotherhood = DefDatabase<GangDef>.GetNamed("RP_Gang_Brotherhood");
                // The biggest yard patch, and somewhere that is not turf at all.
                List<ZonePatch> patches = pm.zones.Patches;
                int yardIndex = -1;
                for (int i = 0; i < patches.Count; i++)
                    if (patches[i].zone == RPDefOf.RP_Yard && (yardIndex < 0 || patches[i].cells > patches[yardIndex].cells)) yardIndex = i;
                if (yardIndex < 0)
                {
                    problems0.Add("turf test: the prison has no yard patch (four cells or more) to hold");
                    return;
                }
                ZonePatch yard = patches[yardIndex];
                List<IntVec3> yardCells = pm.zones.CellsOf(RPDefOf.RP_Yard).Where(c => pm.zones.PatchAt(c) == yardIndex && c.Standable(map)).ToList();
                IntVec3 away = IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map));
                if (yardCells.Count == 0 || GangTurf.HolderAt(pm, away) != null || pm.zones.PatchAt(away) >= 0 && pm.zones.Patches[pm.zones.PatchAt(away)].zone.gangTurf)
                {
                    problems0.Add($"turf test: no standable yard cell ({yardCells.Count}) or the intake cell is a turf zone");
                    return;
                }
                IntVec3 spot = yardCells[0];
                string place = GangTurf.PlaceName(pm.zones, yardIndex);

                // What is put back: every inmate's gang and active flag (the real inmates are made nobody's and not counted, so the throwaways
                // are the whole yard), the map's turf and day tally, truces, and the war arcs.
                var real = map.mapPawns.PrisonersOfColony.Select(x => x.Inmate()).Where(c => c != null).ToList();
                var state0 = real.Select(c => (c, c.gang, c.activeFlag)).ToList();
                r.Add(() =>
                {
                    foreach ((CompInmate c, GangDef gang, bool active) in state0) (c.gang, c.activeFlag) = (gang, active);
                });
                foreach (CompInmate c in real) (c.gang, c.activeFlag) = (null, false);
                List<TurfClaim> claims0 = book.claims.ToList();
                var tallies0 = book.tallies.ToList();
                r.Add(() =>
                {
                    book.Replace(claims0);
                    book.tallies.Clear();
                    foreach (KeyValuePair<int, TurfBook.PatchTally> kv in tallies0) book.tallies[kv.Key] = kv.Value;
                });
                book.Replace(new TurfClaim[0]);
                book.tallies.Clear();
                List<GangTruce> truces0 = g.story.truces.ToList();
                r.Add(() => g.story.truces = truces0);
                g.story.truces = new List<GangTruce>();
                List<StoryArc> arcs0 = g.story.arcs;
                Dictionary<StoryArcDef, int> lastStart0 = new Dictionary<StoryArcDef, int>(g.story.lastArcStart);
                int nextArc0 = g.story.nextArcId;
                r.Add(() =>
                {
                    g.story.arcs = arcs0;
                    g.story.lastArcStart = lastStart0;
                    g.story.nextArcId = nextArc0;
                });
                g.story.arcs = arcs0.Where(a => a.def != RPGangWarDefOf.RP_Arc_GangWar).ToList();

                var crew = new List<Pawn>();
                for (int i = 0; i < 9; i++)
                {
                    Pawn p = Throwaway(map, r);
                    crew.Add(p);
                    Place(p, yardCells[i % yardCells.Count]);
                }
                void Place(Pawn p, IntVec3 cell)
                {
                    p.pather.StopDead();
                    p.Position = cell;
                    p.Notify_Teleported();
                }
                void Be(int i, GangDef gang, bool known = true, bool active = true) => (crew[i].Inmate().gang, crew[i].Inmate().gangKnown, crew[i].Inmate().activeFlag) = (gang, known && gang != null, active);
                void Everyone(Func<int, GangDef> gangOf, bool known = true)
                {
                    for (int i = 0; i < crew.Count; i++) Be(i, gangOf(i), known);
                }
                // A day is over: the hour's pass counted who was there, and the daily pass judged it.
                void Day()
                {
                    GangTurf.Sample(map, pm);
                    GangTurf.Daily(map);
                }
                int Lines(string text) => g.story.entries.Count(e => e.cat == RPChronicleDefOf.RP_Chr_Gangs && e.text == text);
                int LettersNamed(string label) => r.NewLetters.Count(l => l.Label.ToString() == label);
                GangDef Holder() => GangTurf.HolderAt(pm, spot);
                string log = "";

                // Two members alone (and the rest not there): never enough to hold a place, whatever their share.
                Everyone(i => null);
                Be(0, ravens);
                Be(1, ravens);
                for (int i = 2; i < crew.Count; i++) Be(i, null, active: false);
                int entries0 = g.story.entries.Count;
                Day();
                bool pairHolds = Holder() != null || g.story.entries.Count != entries0;
                if (pairHolds) problems0.Add("turf test: two gang members alone held a place");

                // Six of nine inmates are Ravens (known): a day later the yard is theirs, with a line and a letter.
                Everyone(i => i < 6 ? ravens : null);
                Day();
                string claimedLine = "RP_Chr_TurfClaimed".Translate(ravens.label, place);
                bool claimed = Holder() == ravens && book.At(yard.anchor)?.gang == ravens && Lines(claimedLine) == 1 && LettersNamed("RP_TurfClaimedLabel".Translate(ravens.LabelCap, place)) == 1;
                if (!claimed) problems0.Add($"turf test: six of nine inmates in the yard were Ravens, and the day after the yard was not held by them with its line and letter (holder {Holder()?.defName}, lines {Lines(claimedLine)})");
                log += $"claimed={claimed}";

                // A rival entering gets the effect, a member the comfort; under one rival member's worth; with the truce, the hidden gang or the other place, none.
                Be(6, saints);
                CompInmate rival = crew[6].Inmate(), member = crew[0].Inmate();
                float Gangs(Pawn p, CompInmate c, out string cause) => GangUtility.TensionFromGangs(p, c, pm, out bool _, out cause);
                float rivalOn = Gangs(crew[6], rival, out string rivalCause), memberOn = Gangs(crew[0], member, out string _);
                TurfClaim held = book.At(yard.anchor);
                book.Remove(yard.anchor);
                float rivalOff = Gangs(crew[6], rival, out string _), memberOff = Gangs(crew[0], member, out string _);
                book.Set(pm.zones, held.zone, held.anchor, held.gang, held.sinceTick);
                float rivalTerm = rivalOn - rivalOff, memberTerm = memberOn - memberOff;
                bool tense = Mathf.Approximately(rivalTerm, GangTurf.RivalTurfTension) && rivalTerm <= 1.2f && rivalCause == "RP_Cause_Turf";
                bool easy = Mathf.Approximately(memberTerm, -GangTurf.OwnTurfRelief);
                bool afraid = Mathf.Approximately(GangTurf.FearTerm(crew[6], rival, pm), GangTurf.RivalTurfFear) && Mathf.Approximately(GangTurf.FearTerm(crew[0], member, pm), -GangTurf.OwnTurfFearRelief);
                bool dealsMore = GangTurf.OnOwnTurf(crew[0], member, pm) && !GangTurf.OnOwnTurf(crew[6], rival, pm);
                Place(crew[6], away);
                bool awayQuiet = GangTurf.TensionTerm(crew[6], rival, pm, out string _) == 0f && GangTurf.FearTerm(crew[6], rival, pm) == 0f;
                Place(crew[6], spot);
                g.story.truces.Add(new GangTruce { a = saints, b = ravens, untilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay });
                bool truceQuiet = GangTurf.TensionTerm(crew[6], rival, pm, out string _) == 0f && GangTurf.FearTerm(crew[6], rival, pm) == 0f;
                g.story.truces.Clear();
                if (!tense) problems0.Add($"turf test: a rival on the Ravens' yard got turf tension {rivalTerm:F2} (want {GangTurf.RivalTurfTension}, at most 1.2) named {rivalCause}");
                if (!easy) problems0.Add($"turf test: a Raven on their own yard got {memberTerm:F2} tension (want {-GangTurf.OwnTurfRelief})");
                if (!afraid) problems0.Add("turf test: fear on a rival's turf or relief on one's own was not applied");
                if (!dealsMore) problems0.Add("turf test: a dealer is not on their own turf in their own yard, or a rival is");
                if (!awayQuiet || !truceQuiet) problems0.Add($"turf test: turf affected an inmate off the turf ({awayQuiet}) or during a truce ({truceQuiet})");
                log += $" rival={rivalTerm:F2}/{GangTurf.FearTerm(crew[6], rival, pm):F0} member={memberTerm:F2}/{GangTurf.FearTerm(crew[0], member, pm):F0}";

                // The cause names turf only when the warden knows both gangs (else trouble on the wing), and a hidden inmate's stored cause is read as such.
                rival.gangKnown = false;
                Gangs(crew[6], rival, out string hiddenRival);
                rival.gangKnown = true;
                for (int i = 0; i < 6; i++) crew[i].Inmate().gangKnown = false;
                Gangs(crew[6], rival, out string hiddenHolder);
                for (int i = 0; i < 6; i++) crew[i].Inmate().gangKnown = true;
                rival.mainCause = "RP_Cause_Turf";
                rival.gangKnown = false;
                string shownCause = TensionUtility.ShownCause(rival);
                rival.gangKnown = true;
                if (hiddenRival != "RP_Cause_WingTrouble" || hiddenHolder != "RP_Cause_WingTrouble" || shownCause != "RP_Cause_WingTrouble")
                    problems0.Add($"turf test: the turf cause names turf for a hidden rival ({hiddenRival}), a hidden holder ({hiddenHolder}) or on a hidden inmate's file ({shownCause})");

                // Four Ravens of eight: a holder keeps what it has while it is 40% or more, with no line.
                Everyone(i => i < 4 ? ravens : null);
                Be(8, null, active: false);
                entries0 = g.story.entries.Count;
                Day();
                bool kept = Holder() == ravens && g.story.entries.Count == entries0;
                if (!kept) problems0.Add($"turf test: a gang with half the yard lost it or told of it (holder {Holder()?.defName})");

                // Seven Saints (known) and one Raven (known, so the Ravens are known too) among nine: the yard flips, with the line and the letter.
                Everyone(i => i < 7 ? saints : i == 8 ? ravens : null);
                Be(0, saints);
                Day();
                string takenLine = "RP_Chr_TurfTaken".Translate(saints.label, place, ravens.label);
                bool flipped = Holder() == saints && Lines(takenLine) == 1 && LettersNamed("RP_TurfTakenLabel".Translate(saints.LabelCap, place)) == 1;
                if (!flipped) problems0.Add($"turf test: seven Saints in nine took the yard but it did not flip with its line and letter (holder {Holder()?.defName}, lines {Lines(takenLine)})");
                log += $" flipped={flipped}";

                // The war's stake is the yard (rivals stand on it), attacks count as ground, and a raid not answered turns the yard over when the war ends.
                bool stake = GangTurf.PickStake(map, saints, ravens) == yard.anchor && GangTurf.StakeName(map, yard.anchor) == place;
                bool ranks = GangTurf.WarRank(pm, spot, saints, ravens, yard.anchor) == 2 && GangTurf.WarRank(pm, spot, saints, ravens, -1) == 1
                             && GangTurf.WarRank(pm, away, saints, ravens, yard.anchor) == 0 && GangTurf.WarRank(pm, spot, ravens, brotherhood, -1) == 0;
                if (!stake || !ranks) problems0.Add($"turf test: the war's stake or the ground of an attack was wrong (stake {stake}, ranks {ranks})");
                Be(0, saints);
                crew[0].Inmate().gangLeader = true;
                Be(8, ravens);
                crew[8].Inmate().gangLeader = true;
                StoryArc war = GangWarArc.Start(map, saints, ravens);
                bool stakeSet = war != null && war.stake == yard.anchor;
                bool told = false;
                if (war != null)
                {
                    ((GangWarArc)war.def.Worker).Declare(war, map, 2);
                    told = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.arcId == war.id && l.Text.ToString().Contains("RP_GangWarStake".Translate(place)));
                }
                if (!stakeSet || !told) problems0.Add($"turf test: the war was not about the yard, or its letter did not say so (war {war != null}, stake {war?.stake}, told {told})");
                crew[0].Inmate().gangLeader = crew[8].Inmate().gangLeader = false;
                // (Ravens strike on the Saints' yard twice, the Saints answer once: one raid is not enough; then a truce settles nothing either.)
                GangTurf.Notify_WarBlow(map, spot, ravens, saints);
                GangTurf.Notify_WarBlow(map, spot, ravens, saints);
                GangTurf.Notify_WarBlow(map, spot, saints, ravens);
                int raids = book.At(yard.anchor).Raids(ravens);
                GangTurf.SettleWar(map, saints, ravens, peace: false);
                bool answered = raids == 1 && Holder() == saints && book.At(yard.anchor).raiders.Count == 0;
                GangTurf.Notify_WarBlow(map, spot, ravens, saints);
                GangTurf.Notify_WarBlow(map, spot, ravens, saints);
                GangTurf.SettleWar(map, saints, ravens, peace: true);
                bool peace = Holder() == saints && book.At(yard.anchor).raiders.Count == 0;
                GangTurf.Notify_WarBlow(map, spot, ravens, saints);
                GangTurf.Notify_WarBlow(map, spot, ravens, saints);
                GangTurf.SettleWar(map, saints, ravens, peace: false);
                string warLine = "RP_Chr_TurfWarTaken".Translate(ravens.label, place, saints.label);
                bool warTaken = Holder() == ravens && Lines(warLine) == 1 && LettersNamed("RP_TurfTakenLabel".Translate(ravens.LabelCap, place)) == 1;
                if (!answered || !peace || !warTaken) problems0.Add($"turf test: a war's raids on the yard (answered {answered}, after a truce {peace}) did not turn it over with its line ({warTaken}, holder {Holder()?.defName})");
                log += $" war={stakeSet && warTaken}";

                // A holder that falls to a fifth of the yard loses it ("no longer hold"), with nobody else taking it.
                Everyone(i => i == 8 ? ravens : null);
                Be(0, ravens);
                Day();
                bool lost = Holder() == null && book.At(yard.anchor) == null && Lines("RP_Chr_TurfLost".Translate(ravens.label, place)) == 1;
                if (!lost) problems0.Add($"turf test: the Ravens with two in nine kept the yard or its loss was not told (holder {Holder()?.defName})");

                // A hidden gang's turf is never shown or told: the Saints take the yard again, then every crew member is hidden.
                Everyone(i => i < 6 ? saints : null);
                Day();
                Everyone(i => i < 6 ? saints : null, known: false);
                pm.TurfShown.Rebuild();
                string zoneHidden = GangTurf.ZoneLine(map, RPDefOf.RP_Yard), heldHidden = GangTurf.HeldLine(map, saints);
                bool shownHidden = pm.TurfShown.At(spot) != null || GangTurf.KnownTurf(map, pm).Any() || GangTurf.KnownHolderAt(pm, spot, map) != null;
                bool takenSecretly = Holder() == saints;
                // The Brotherhood (hidden too) takes it from them: not a line, not a letter.
                Everyone(i => i < 7 ? brotherhood : null, known: false);
                entries0 = g.story.entries.Count;
                int letters0 = r.NewLetters.Count();
                Day();
                bool silent = Holder() == brotherhood && g.story.entries.Count == entries0 && r.NewLetters.Count() == letters0;
                string zoneHidden2 = GangTurf.ZoneLine(map, RPDefOf.RP_Yard);
                pm.TurfShown.Rebuild();
                bool shownHidden2 = pm.TurfShown.At(spot) != null || GangTurf.KnownTurf(map, pm).Any();
                if (zoneHidden != null || heldHidden != null || shownHidden || zoneHidden2 != null || shownHidden2 || !takenSecretly || !silent)
                    problems0.Add($"turf test: a hidden gang's turf was shown or told (rooms line '{zoneHidden}'/'{zoneHidden2}', intel '{heldHidden}', overlay/known turf {shownHidden}/{shownHidden2}; held {takenSecretly}; the flip stayed silent {silent})");
                // ...and once the gang is known, the same turf shows: in gang colour, on the rooms line and in intel.
                Everyone(i => i < 7 ? brotherhood : null);
                pm.TurfShown.Rebuild();
                string zoneKnown = GangTurf.ZoneLine(map, RPDefOf.RP_Yard), heldKnown = GangTurf.HeldLine(map, brotherhood);
                Color? tint = pm.TurfShown.At(spot);
                bool shown = tint.HasValue && tint.Value == brotherhood.color && zoneKnown != null && zoneKnown.Contains(brotherhood.label) && heldKnown != null && heldKnown.Contains(place);
                if (!shown) problems0.Add($"turf test: a known gang's turf was not shown (tint {tint}, rooms line '{zoneKnown}', intel '{heldKnown}')");
                log += $" hidden={!shownHidden && !shownHidden2 && silent} shown={shown}";
                RPLog.Message($"[RimPrison:test] turf: {log}");
            });
        }
    }
}
