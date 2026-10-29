using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>What kind of violence a mark on the danger map is (a misconduct names its own in <see cref="MisconductDef.danger"/>; a stabbing is a fight with a weapon in hand).</summary>
    public enum DangerKind : byte
    {
        None,
        Fight,
        Stabbing,
        Assault,
        Riot,
        Death,
    }

    /// <summary>One piece of seen violence on the danger map (see <see cref="PrisonMap.incidents"/>): where, what, and when.</summary>
    public class IncidentMark : IExposable
    {
        public IntVec3 cell;
        public DangerKind kind;
        public int tick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref tick, "tick");
        }

        public string Describe() => $"{cell.x},{cell.z}:{kind}:{tick}";
    }

    /// <summary>A patch of the prison where violence was seen: its name, where to look, how hot it is, and what happened there.</summary>
    public class HotPatch
    {
        public string place;
        public IntVec3 center;
        public float heat;
        public int marks;
        /// <summary>Most of the patch is dark now (see <see cref="DangerMap.Unlit"/>): the guards there cannot see far and the cameras not at all.</summary>
        public bool unlit;
        /// <summary>Which patch it is: the index in the zone grid's patches, or -1 for a block of ground no zone covers (then <see cref="block"/>).</summary>
        public int patchIndex = -1;
        /// <summary>The key the patch is grouped by (the zone patch's anchor, or one for a block of ground no zone covers).</summary>
        public int key;
        /// <summary>A cell the patch holds (where its first mark is): a stable place to look at the patch from, whatever the zone grid's anchor does.</summary>
        public IntVec3 cell;
        public (int x, int z) block;
        public readonly int[] counts = new int[System.Enum.GetValues(typeof(DangerKind)).Length];

        /// <summary>"2 fight(s), 1 stabbing(s)".</summary>
        public string KindsText()
        {
            var parts = new List<string>();
            for (int k = (int)DangerKind.Fight; k < counts.Length; k++)
                if (counts[k] > 0) parts.Add(("RP_DangerCount_" + (DangerKind)k).Translate(counts[k]).ToString());
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// The danger map: where violence was seen in the last <see cref="PrisonMap.DangerDays"/> days (fights, stabbings, assaults on staff, riots starting, killings and deaths from wounds), the places to post guards and cameras. Only
    /// <b>seen</b> incidents are marked: a misconduct is written here when it was seen (<see cref="MisconductUtility.Record"/>), so a gang ambush no guard or camera saw is not on it, whatever the
    /// map could tell, and a weapon is only shown where it was seen; a violent death is always known (there is a body), a natural one is not violence. Each mark counts for what it was and less as it ages.
    /// </summary>
    public static class DangerMap
    {
        public static float Weight(DangerKind kind) => kind switch
        {
            DangerKind.Fight => 1f,
            DangerKind.Assault => 2f,
            DangerKind.Stabbing => 3f,
            DangerKind.Riot => 3f,
            DangerKind.Death => 4f,
            _ => 0f,
        };

        /// <summary>The share of its weight a mark of this age still has: all of it when new, under a third at the end of the month.</summary>
        public static float Fade(int age) => Mathf.Lerp(1f, 0.3f, Mathf.Clamp01(age / (float)(PrisonMap.DangerDays * GenDate.TicksPerDay)));

        public static float Heat(IncidentMark m, int now) => Weight(m.kind) * Fade(now - m.tick);

        /// <summary>A mark is on the map for <see cref="PrisonMap.DangerDays"/> days (the list is only pruned when the next is made, so an old one may still be in it).</summary>
        public static bool Shown(IncidentMark m, int now) => now - m.tick < PrisonMap.DangerDays * GenDate.TicksPerDay;

        /// <summary>
        /// The kind of violence a seen misconduct was, or none (an insult, a smashed chair, an escape: not violence to a person). A fight with something in the hand is a stabbing only when <paramref name="weaponSeen"/>:
        /// a fight is loud and always seen, but the weapon in the hand is not (a shiv drawn in a blind spot is known to nobody).
        /// </summary>
        public static DangerKind KindOf(Pawn who, MisconductDef def, bool weaponSeen)
        {
            DangerKind kind = def.danger;
            if (kind == DangerKind.Fight && weaponSeen && who.equipment?.Primary != null) kind = DangerKind.Stabbing;
            return kind;
        }

        /// <summary>Where the violence was: where the blows fall (the one who was set on, if it is known and on the map), else where the one who did it stood (a riot, a hostage-taking).</summary>
        public static IntVec3 SceneOf(Pawn who)
        {
            Pawn victim = who.MentalState switch
            {
                MentalState_Assaulting assaulting => assaulting.Target,
                MentalState_SocialFighting fighting => fighting.otherPawn,
                _ => null,
            };
            return victim is { Spawned: true } && victim.Map == who.MapHeld ? victim.PositionHeld : who.PositionHeld;
        }

        /// <summary>A misconduct was seen: if it was violence, it goes on the map where it happened (<paramref name="evidence"/>: staff found it, so what they found was seen wherever it was).</summary>
        public static void Note(PrisonMap pm, Pawn who, MisconductDef def, bool evidence = false)
        {
            if (pm == null || def.danger == DangerKind.None) return;
            bool weaponSeen = def.danger == DangerKind.Fight && who.equipment?.Primary != null && (evidence || TensionUtility.Watched(who, pm));
            pm.AddIncident(SceneOf(who), KindOf(who, def, weaponSeen));
        }

        /// <summary>
        /// Whether a death was violence: a killing (a blow from another pawn), a blow that is violence in itself, or a wound (an injury, or bleeding out or infection from one). Illness, old age, hunger, thirst,
        /// cold and heat are not.
        /// </summary>
        public static bool Violent(Pawn victim, DamageInfo? dinfo, Hediff culprit)
        {
            if (dinfo is { } d && (d.Instigator is Pawn other && other != victim || d.Def != null && d.Def.ExternalViolenceFor(victim))) return true;
            return culprit != null && (culprit is Hediff_Injury || culprit.def == HediffDefOf.BloodLoss || culprit.def == HediffDefOf.WoundInfection);
        }

        /// <summary>A violent death in the prison (always known: there is a body); a natural one is not violence and is not on the map.</summary>
        public static void NoteDeath(Pawn victim, DamageInfo? dinfo, Hediff culprit)
        {
            if (Violent(victim, dinfo, culprit) && victim.MapHeld is { } map && PrisonMap.Of(map) is { } pm) pm.AddIncident(victim.PositionHeld, DangerKind.Death);
        }

        /// <summary>The hottest patches (a connected patch of a painted zone, or a block of eight cells for ground no zone covers), hottest first, with at least one mark in the last month.</summary>
        public static List<HotPatch> Hottest(PrisonMap pm, int count = 3)
        {
            int now = Find.TickManager.TicksGame;
            var byKey = new Dictionary<int, (HotPatch patch, long sumX, long sumZ)>();
            foreach (IncidentMark m in pm.incidents)
            {
                if (!Shown(m, now)) continue;
                int patchIndex = pm.zones.PatchAt(m.cell);
                int key = KeyOf(pm, m.cell, patchIndex);
                if (!byKey.TryGetValue(key, out var entry)) entry = (new HotPatch { place = patchIndex >= 0 ? pm.zones.Patches[patchIndex].zone.label : null, patchIndex = patchIndex, key = key, cell = m.cell, block = (m.cell.x / 8, m.cell.z / 8) }, 0L, 0L);
                entry.patch.heat += Heat(m, now);
                entry.patch.marks++;
                entry.patch.counts[(int)m.kind]++;
                byKey[key] = (entry.patch, entry.sumX + m.cell.x, entry.sumZ + m.cell.z);
            }
            var list = new List<HotPatch>();
            foreach ((HotPatch patch, long sumX, long sumZ) in byKey.Values)
            {
                patch.center = new IntVec3((int)(sumX / patch.marks), 0, (int)(sumZ / patch.marks));
                patch.place = patch.place == null ? "RP_DangerPlaceNone".Translate(patch.center.x, patch.center.z).ToString() : "RP_DangerPlace".Translate(patch.place, patch.center.x, patch.center.z).ToString();
                list.Add(patch);
            }
            List<HotPatch> hottest = list.OrderByDescending(p => p.heat).ThenBy(p => p.center.x).ThenBy(p => p.center.z).Take(count).ToList();
            foreach (HotPatch patch in hottest) patch.unlit = UnlitCached(pm, patch, now);
            return hottest;
        }

        private static int KeyOf(PrisonMap pm, IntVec3 cell, int patchIndex) => patchIndex >= 0 ? pm.zones.Patches[patchIndex].anchor : -1 - (cell.x / 8) * 10000 - cell.z / 8;

        /// <summary>The patch a cell is in now (a connected patch of a painted zone, or its block of eight cells when no zone covers it), without heat or marks: to look at a patch again from a cell it was seen at.</summary>
        public static HotPatch PatchAt(PrisonMap pm, IntVec3 cell)
        {
            int patchIndex = pm.zones.PatchAt(cell);
            return new HotPatch { patchIndex = patchIndex, key = KeyOf(pm, cell, patchIndex), center = cell, cell = cell, block = (cell.x / 8, cell.z / 8) };
        }

        /// <summary>How long a patch's <see cref="HotPatch.unlit"/> is kept: the Deployment tab asks for the hottest patches on every draw event, and walking each patch's cells for the glow is not for that (dusk does not move faster).</summary>
        public const int UnlitKeepTicks = 250;
        private static PrisonMap unlitFor;
        private static readonly Dictionary<int, (int tick, bool unlit)> unlitStamps = new Dictionary<int, (int, bool)>();

        private static bool UnlitCached(PrisonMap pm, HotPatch patch, int now)
        {
            if (unlitFor != pm)
            {
                unlitStamps.Clear();
                unlitFor = pm;
            }
            if (unlitStamps.TryGetValue(patch.key, out var stamp) && now >= stamp.tick && now - stamp.tick < UnlitKeepTicks) return stamp.unlit;
            bool unlit = Unlit(pm, patch);
            unlitStamps[patch.key] = (now, unlit);
            return unlit;
        }

        /// <summary>Drops the kept <see cref="HotPatch.unlit"/> answers (a check that moves the glow by hand and asks again at once).</summary>
        internal static void ForgetUnlit() => unlitStamps.Clear();

        /// <summary>Whether more than half of the walkable cells of the patch are dark now, or <paramref name="atNight"/> with the sky left out (a patch no lamp lights: the same by day as by night) (<see cref="TensionUtility.Dark"/>: ground glow under 0.3 in a free-time zone or a corridor, not a cell), which is where a fight is seen only by a guard standing close.</summary>
        public static bool Unlit(PrisonMap pm, HotPatch patch, bool atNight = false)
        {
            Map map = pm.map;
            int total = 0, dark = 0;
            void Count(IntVec3 c)
            {
                if (!c.InBounds(map) || c.Impassable(map)) return;
                total++;
                if (TensionUtility.Dark(c, map, pm, atNight)) dark++;
            }
            if (patch.patchIndex >= 0)
            {
                foreach (IntVec3 c in pm.zones.CellsOf(pm.zones.Patches[patch.patchIndex].zone))
                    if (pm.zones.PatchAt(c) == patch.patchIndex) Count(c);
            }
            else
            {
                for (int x = patch.block.x * 8; x < patch.block.x * 8 + 8; x++)
                    for (int z = patch.block.z * 8; z < patch.block.z * 8 + 8; z++)
                        if (pm.zones.ZoneAt(new IntVec3(x, 0, z)) == null) Count(new IntVec3(x, 0, z));
            }
            return dark * 2 > total;
        }

        public static string Describe(PrisonMap pm) => string.Join(",", pm.incidents.Select(m => m.Describe()));
    }

    /// <summary>
    /// The danger map drawn next to the coverage and finds overlays: every cell near a mark glows red, deeper where more and worse happened and less as it ages, with a letter on each mark (F fight,
    /// S stabbing, A assault on staff, R riot, D death). Rebuilt when a mark is made and once an hour (the marks fade).
    /// </summary>
    public class DangerOverlay
    {
        private readonly Map map;
        private readonly float[] heat;
        private readonly Color[] cellColors;
        private readonly bool[] cellOn;
        private CellBoolDrawer drawer;
        private int shownFingerprint = -1;

        public static bool Show;
        /// <summary>Marks glow this far out (cells), less at the edge.</summary>
        public const float GlowRadius = 2.9f;

        public DangerOverlay(Map map)
        {
            this.map = map;
            heat = new float[map.cellIndices.NumGridCells];
            cellColors = new Color[map.cellIndices.NumGridCells];
            cellOn = new bool[map.cellIndices.NumGridCells];
        }

        public static Color ColorOf(DangerKind kind) => kind switch
        {
            DangerKind.Death => new Color(0.75f, 0.1f, 0.85f),
            DangerKind.Riot => new Color(1f, 0.45f, 0.1f),
            DangerKind.Stabbing => new Color(1f, 0.15f, 0.15f),
            DangerKind.Assault => new Color(1f, 0.35f, 0.25f),
            _ => new Color(1f, 0.85f, 0.25f),
        };

        public void Update()
        {
            if (!Show) return;
            Refresh();
            drawer.MarkForDraw();
            drawer.CellBoolDrawerUpdate();
        }

        private int Fingerprint(PrisonMap pm)
        {
            var h = new System.HashCode();
            h.Add(pm.incidents.Count);
            h.Add(pm.incidents.Count > 0 ? pm.incidents[pm.incidents.Count - 1].tick : 0);
            h.Add(Find.TickManager.TicksGame / GenDate.TicksPerHour);
            return h.ToHashCode();
        }

        /// <summary>Rebuilds the glow if what it shows has changed since it was built (or it never was); says whether it did.</summary>
        public bool Refresh()
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || drawer != null && Fingerprint(pm) == shownFingerprint) return false;
            Rebuild(pm);
            return true;
        }

        /// <summary>The glow on a cell, 0 when none (for tests).</summary>
        public float At(IntVec3 c) => c.InBounds(map) && cellOn[map.cellIndices.CellToIndex(c)] ? heat[map.cellIndices.CellToIndex(c)] : 0f;

        public void Rebuild(PrisonMap pm)
        {
            shownFingerprint = Fingerprint(pm);
            System.Array.Clear(heat, 0, heat.Length);
            System.Array.Clear(cellOn, 0, cellOn.Length);
            int now = Find.TickManager.TicksGame;
            var strongest = new Dictionary<int, (float weight, DangerKind kind)>();
            foreach (IncidentMark m in pm.incidents)
            {
                if (!DangerMap.Shown(m, now)) continue;
                float h = DangerMap.Heat(m, now);
                foreach (IntVec3 c in GenRadial.RadialCellsAround(m.cell, GlowRadius, true))
                {
                    if (!c.InBounds(map)) continue;
                    int i = map.cellIndices.CellToIndex(c);
                    float value = h * (1f - c.DistanceTo(m.cell) / (GlowRadius + 1f));
                    heat[i] += value;
                    cellOn[i] = true;
                    if (!strongest.TryGetValue(i, out var s) || value > s.weight) strongest[i] = (value, m.kind);
                }
            }
            for (int i = 0; i < heat.Length; i++)
            {
                if (!cellOn[i]) continue;
                Color col = ColorOf(strongest[i].kind);
                col.a = Mathf.Clamp01(heat[i] / 3f);
                cellColors[i] = col;
            }
            drawer ??= new CellBoolDrawer(i => cellOn[i], () => Color.white, i => cellColors[i], map.Size.x, map.Size.z, 3613, 0.5f);
            drawer.SetDirty();
        }

        public static void DrawLabels(PrisonMap pm)
        {
            int now = Find.TickManager.TicksGame;
            foreach (IncidentMark m in pm.incidents)
            {
                if (!DangerMap.Shown(m, now)) continue;
                // (Paler than the glow it stands on, or the letter cannot be read.)
                Color col = Color.Lerp(ColorOf(m.kind), Color.white, 0.6f);
                col.a = Mathf.Lerp(1f, 0.5f, Mathf.Clamp01((now - m.tick) / (float)(PrisonMap.DangerDays * GenDate.TicksPerDay)));
                GenMapUI.DrawThingLabel(m.cell.ToVector3Shifted().MapToUIPosition(), ("RP_DangerGlyph_" + m.kind).Translate(), col);
            }
        }
    }
}
