using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Hourly tension model. Tension (0–100) rises with low mood and unmet needs and with volatile personalities;
    /// it falls when life is good. Guards nearby give suppression, which lowers the chance that tension turns into
    /// misconduct (but does not lower tension itself). The strongest contributors are kept as the "cause" so
    /// incidents can explain themselves.
    /// </summary>
    public static class TensionUtility
    {
        /// <summary>The cause of their tension as the warden may read it: a gang cause only once their gang is known
        /// (otherwise trouble on the wing, nothing more).</summary>
        public static string ShownCause(CompInmate c, string fallback = "RP_Cause_Mood")
        {
            string cause = c.mainCause ?? fallback;
            if (!c.gangKnown && (cause == "RP_Cause_RivalGang" || cause == "RP_Cause_GangWar" || cause == "RP_Cause_Turf" || GangDeals.IsDealCause(cause))) return "RP_Cause_WingTrouble";
            // Owing money is nobody's business until the warden has heard of the debt.
            return cause == "RP_Cause_Debt" && !DebtSystem.KnownAbout(c) ? "RP_Cause_WingTrouble" : cause;
        }

        /// <summary>The shown cause in words: a rumour's cause names what the wing is saying ("a rumour - the yard is being closed for good") while it is told.</summary>
        public static TaggedString CauseText(CompInmate c, string fallback = "RP_Cause_Mood")
        {
            string key = ShownCause(c, fallback);
            return key == "RP_Cause_Rumour" && Rumours.LiveFor(c) is { } r ? "RP_Cause_RumourOf".Translate(Rumours.Text(r)) : key.Translate();
        }

        /// <summary>For the checks: what the last <see cref="HourlyUpdate(Pawn, CompInmate, PrisonMap, float)"/> counted under a cause (0 when nothing).</summary>
        internal static float LastContribution(string label)
        {
            float sum = 0f;
            for (int i = 0; i < contributions.Count; i++)
                if (contributions[i].label == label) sum += contributions[i].value;
            return sum;
        }

        public const float GuardRadius = 10f;
        /// <summary>Persistent suppression wears off by about 5 a day.</summary>
        public const float SuppressionDecayPerHour = 5f / 24f;
        private static readonly List<(string label, float value)> contributions = new List<(string, float)>();

        public static void HourlyUpdate(Pawn p, CompInmate c, PrisonMap pm) => HourlyUpdate(p, c, pm, Watchers(p, pm));

        /// <param name="watchers"><see cref="Watchers"/> of the inmate now (the hourly update asks once for all that need it); suppression reads the count as if the cell were lit (the dark changes what is seen, never the odds).</param>
        public static void HourlyUpdate(Pawn p, CompInmate c, PrisonMap pm, float watchers)
        {
            contributions.Clear();
            float delta = 0f;
            Need_Mood mood = p.needs?.mood;
            if (mood != null)
            {
                float m = mood.CurLevelPercentage;
                float v = (0.5f - m) * 12f;
                Add(ref delta, v > 0 ? "RP_Cause_Mood" : "RP_Cause_Content", v);
            }
            if (p.needs?.food != null && p.needs.food.CurCategory >= HungerCategory.UrgentlyHungry) Add(ref delta, "RP_Cause_Hunger", 3f);
            if (p.needs?.TryGetNeed(RPDefOf.RP_Hygiene) is { CurLevel: < 0.2f }) Add(ref delta, "RP_Cause_Filthy", 1.5f);
            if (ExerciseUtility.Restless(p)) Add(ref delta, "RP_Cause_Restless", ExerciseUtility.RestlessTension);
            if (p.ownership?.OwnedBed == null) Add(ref delta, "RP_Cause_NoBed", 2f);
            if (c.job != null && LaborUtility.IsWorking(p, c)) Add(ref delta, "RP_Cause_Working", c.job.tensionPerHour);
            if (c.fear > 20f) Add(ref delta, "RP_Cause_Unsafe", c.fear / 25f);
            float cellmates = Cellmates.Tension(p, c);
            if (cellmates != 0f) Add(ref delta, cellmates > 0f ? "RP_Cause_Cellmate" : "RP_Cause_Content", cellmates);
            if (p.needs?.TryGetNeed(RPVisitDefOf.RP_Family) is { CurLevel: < 0.1f }) Add(ref delta, "RP_Cause_NoFamily", 1f);
            // Picked for a random drug test: searched again.
            if (DrugTesting.SearchedAgain(c)) Add(ref delta, "RP_Cause_SearchedAgain", DrugTesting.SearchedTension);
            if (FeudArc.Simmering(p, c)) Add(ref delta, "RP_Cause_Feud", 0.5f);
            // The warden's firm word in the canteen: the quiet it bought is paid for in the hours after.
            if (c.wardenFirmUntil > Find.TickManager.TicksGame) Add(ref delta, "RP_Cause_WardenFirm", WardenSystem.FirmTensionPerHour);
            float staffFeeling = StaffMemories.RoomTension(p, c);
            if (staffFeeling != 0f) Add(ref delta, staffFeeling > 0f ? "RP_Cause_HatedGuard" : "RP_Cause_Content", staffFeeling);
            // What the wing is saying (the rumour mill): its strength, read off the wing.
            float rumour = Rumours.TensionFor(p, c, pm);
            if (rumour > 0f) Add(ref delta, "RP_Cause_Rumour", rumour);
            float wantRelief = Wants.TensionRelief(c);
            if (wantRelief > 0f) Add(ref delta, "RP_Cause_Content", -wantRelief);
            float mentor = Mentors.Relief(p, c);
            if (mentor > 0f) Add(ref delta, "RP_Cause_Mentor", -mentor);
            float weather = WeatherStrain.For(p, out string weatherCause);
            if (weather > 0f) Add(ref delta, weatherCause, weather);
            float gangTension = GangUtility.TensionFromGangs(p, c, pm, out bool rivalsNear, out string turfCause);
            if (gangTension != 0f) Add(ref delta, rivalsNear ? "RP_Cause_RivalGang" : turfCause ?? "RP_Cause_Content", gangTension);
            float personality = PersonalityFactor(p, c);
            if (delta > 0f) delta *= personality * Mathf.Lerp(0.8f, 1.3f, Mathf.InverseLerp(0.5f, 2f, c.security?.dangerFactor ?? 1f));
            // Instigators in the same room wind others up.
            Room room = p.GetRoom();
            if (room != null && !room.PsychologicallyOutdoors && room.CellCount < 600)
            {
                int instigators = 0;
                if (pm.AnyInstigator)
                {
                    List<Thing> things = room.ContainedAndAdjacentThings;
                    for (int i = 0; i < things.Count; i++)
                        if (things[i] is Pawn other && other != p && other.Inmate() is { } oc && oc.HasReputation(ReputationDefOf.RP_Instigator)) instigators++;
                }
                if (instigators > 0) Add(ref delta, "RP_Cause_Instigator", 1.5f * instigators);
            }
            // (Suppression is the odds, and the dark is about being seen, not about the odds: it counts the watchers as if the cell were lit. Only a dark cell costs the second count.)
            float lit = p.Spawned && Dark(p.Position, p.Map, pm) ? WatchersAt(p.Position, p.Map, pm, ignoreDark: true) : watchers;
            c.suppression = Mathf.Clamp01(lit * 0.25f);
            if (c.suppression > 0.5f) Add(ref delta, "RP_Cause_Watched", (c.suppression - 0.5f) * 1.2f);
            // Suppressed inmates do not recover: calming effects are halved while they are beaten down.
            if (delta < 0f && Suppression.Held(c)) delta *= 0.5f;
            c.suppressed = Mathf.Max(0f, c.suppressed - SuppressionDecayPerHour);
            c.tension = Mathf.Clamp(c.tension + delta, 0f, 100f);
            if (contributions.Count > 0)
            {
                contributions.SortByDescending(x => x.value);
                if (contributions[0].value > 0f) c.mainCause = contributions[0].label;
            }
        }

        private static void Add(ref float delta, string label, float v)
        {
            delta += v;
            contributions.Add((label, v));
        }

        /// <summary>Volatile people heat up faster; calm ones slower.</summary>
        public static float PersonalityFactor(Pawn p, CompInmate c)
        {
            float f = 1f;
            TraitSet traits = p.story?.traits;
            if (traits != null)
            {
                if (traits.HasTrait(TraitDefOf.Abrasive)) f *= 1.25f;
                if (traits.HasTrait(TraitDefOf.Psychopath)) f *= 1.15f;
                if (traits.HasTrait(TraitDefOf.Bloodlust)) f *= 1.2f;
                if (traits.HasTrait(TraitDefOf.Kind)) f *= 0.75f;
                Trait nerves = traits.GetTrait(RPVanillaDefOf.Nerves);
                if (nerves != null) f *= nerves.Degree switch { -2 => 1.5f, -1 => 1.2f, 1 => 0.85f, 2 => 0.7f, _ => 1f };
            }
            for (int i = 0; i < c.reputations.Count; i++) f *= c.reputations[i].tensionFactor;
            return f;
        }

        /// <summary>Under this ground glow a cell is dark (vanilla's <c>PsychGlow.Lit</c> line, <c>GlowGrid.GameGlowLitThreshold</c>).</summary>
        public const float DarkGlow = 0.3f;
        /// <summary>In the dark a guard sees only this far (cells), and a camera sees nothing.</summary>
        public const float DarkSight = 3f;
        /// <summary>The ground glow a check forces on a cell (null in a game): the lamps and the sky it would take to make a cell dark or lit are the lamps' and the sky's business, not the check's.</summary>
        internal static Dictionary<IntVec3, float> TestGlow;

        /// <summary>Cells where the dark rule applies: what inmates are held in or sent to in their free time and the corridors, not a cell (a cell's own room, wing zone or not), not a staff-only zone, not ground outside the zones that is not indoors.</summary>
        public static bool DarkMatters(IntVec3 c, Map map, PrisonMap pm)
        {
            PrisonZoneDef zone = pm.zones.ZoneAt(c);
            if (zone != null && zone.staffOnly) return false;
            Room room = c.GetRoom(map);
            if (room == null || room.IsPrisonCell) return false;
            // (A cell outside the zones is a corridor when it is indoors; a painted one is a free-time zone or a wing's corridor.)
            return zone != null || !room.PsychologicallyOutdoors && !room.TouchesMapEdge;
        }

        /// <summary>Whether the cell is dark now and is one the dark rule applies to (see <see cref="DarkMatters"/>; a cell with a door is always counted lit). Daylight outdoors is bright; this is nights, blackouts and unlit indoor corners.</summary>
        public static bool Dark(IntVec3 c, Map map, PrisonMap pm, bool atNight = false)
        {
            if (!c.InBounds(map)) return false;
            // (The glow first: it is a read of an array, and answers no for nearly every cell by day.)
            float glow = TestGlow != null && TestGlow.TryGetValue(c, out float forced) ? forced : map.glowGrid.GroundGlowAt(c, ignoreSky: atNight);
            // (A door blocks light, so vanilla gives its cell no glow at all, by day too: a doorway is never dark, or an inmate in one would be unseen at noon.)
            return glow < DarkGlow && c.GetDoor(map) == null && DarkMatters(c, map, pm);
        }

        /// <summary>How far a guard sees a cell: <see cref="GuardRadius"/>, or <see cref="DarkSight"/> when it is dark.</summary>
        private static float SightTo(IntVec3 c, Map map, PrisonMap pm) => Dark(c, map, pm) ? DarkSight : GuardRadius;

        private static bool GuardSees(Pawn g, IntVec3 c, Map map, float radius) =>
            g != null && g.Spawned && g.Position.InHorDistOf(c, radius) && !g.Downed && g.Awake() && GenSight.LineOfSight(g.Position, c, map, skipFirstCell: true);

        /// <summary>The first guard who sees a cell (the same sight as <see cref="GuardsNear"/>), or null: who an adjudication sheet names as having seen it.</summary>
        public static Pawn GuardWhoSees(IntVec3 c, Map map, PrisonMap pm)
        {
            float radius = SightTo(c, map, pm);
            List<Pawn> guards = pm.Guards;
            for (int i = 0; i < guards.Count; i++)
                if (GuardSees(guards[i], c, map, radius)) return guards[i];
            return null;
        }

        /// <summary>The guards who see the inmate (in line of sight, within <see cref="GuardRadius"/>, or <see cref="DarkSight"/> when they stand in the dark).</summary>
        public static int GuardsNear(Pawn p, PrisonMap pm)
        {
            int n = 0;
            if (!p.Spawned) return 0; // (not on the map: nothing to look across)
            float radius = SightTo(p.Position, p.Map, pm);
            List<Pawn> guards = pm.Guards;
            for (int i = 0; i < guards.Count; i++)
                if (GuardSees(guards[i], p.Position, p.Map, radius)) n++;
            return n;
        }

        /// <summary>
        /// The one seen rule: whether anything that watches sees the inmate, so the misconduct they commit there is seen and the contraband they handle is watched. A guard or a manned tower, or a working camera, however
        /// little (it is <c>Watchers(p, pm) &gt; 0</c>, stopping at the first that settles it); in the dark (<see cref="Dark"/>) a guard or a tower sees only <see cref="DarkSight"/> cells and a camera sees nothing.
        /// </summary>
        public static bool Watched(Pawn p, PrisonMap pm)
        {
            // (Not on the map, carried to a bed: nothing to look across, as in GuardsNear, and no tower or camera sees a Position they left. The line of sight has no map to look at, and threw.)
            bool answer = p.Spawned && SeenAt(p.Position, p.Map, pm);
            if (AutoTest.Active) Shadow.CheckWatched(p, pm, answer);
            return answer;
        }

        /// <summary><see cref="Watched"/> for a cell: what would see an inmate standing there.</summary>
        public static bool SeenAt(IntVec3 c, Map map, PrisonMap pm)
        {
            bool dark = Dark(c, map, pm);
            float radius = dark ? DarkSight : GuardRadius;
            List<Pawn> guards = pm.Guards;
            for (int i = 0; i < guards.Count; i++)
                if (GuardSees(guards[i], c, map, radius)) return true;
            List<CompGuardTower> towers = pm.Towers;
            for (int i = 0; i < towers.Count; i++)
                if (TowerSees(towers[i], c)) return true;
            if (dark) return false;
            List<CompSecurityCamera> cams = pm.Cameras;
            for (int i = 0; i < cams.Count; i++)
                if (cams[i].Covers(c)) return true;
            return false;
        }

        /// <summary>A manned tower sees its whole radius, dark or not: it has a searchlight (the dark rule is for a guard on foot and a camera).</summary>
        public static bool TowerSees(CompGuardTower tower, IntVec3 c) => tower.Covers(c);

        /// <summary>Guards in sight count 1, manned towers 1, powered cameras covering the inmate 0.5 (none in the dark, and guards only close by; a tower's searchlight sees its radius).</summary>
        public static float Watchers(Pawn p, PrisonMap pm) => p.Spawned ? WatchersAt(p.Position, p.Map, pm) : 0f;

        /// <summary><see cref="Watchers"/> for a cell; <paramref name="ignoreDark"/> counts them as if the cell were lit (the count suppression is taken from).</summary>
        public static float WatchersAt(IntVec3 c, Map map, PrisonMap pm, bool ignoreDark = false)
        {
            bool dark = !ignoreDark && Dark(c, map, pm);
            float radius = dark ? DarkSight : GuardRadius;
            float w = 0f;
            List<Pawn> guards = pm.Guards;
            for (int i = 0; i < guards.Count; i++)
                if (GuardSees(guards[i], c, map, radius)) w += 1f;
            List<CompGuardTower> towers = pm.Towers;
            for (int i = 0; i < towers.Count; i++)
                if (TowerSees(towers[i], c)) w += 1f;
            if (dark) return w;
            List<CompSecurityCamera> cams = pm.Cameras;
            for (int i = 0; i < cams.Count; i++)
                if (cams[i].Covers(c)) w += 0.5f;
            return w;
        }

        /// <summary>Chance per hour that tension turns into misconduct.</summary>
        public static float MisconductChancePerHour(CompInmate c)
        {
            if (c.tension < 30f) return 0f;
            float t = (c.tension - 30f) / 70f;
            // (A shot-caller's deal keeps its members on its wing calmer: one lookup by gang, nothing when no deal runs.)
            return t * t * 0.35f * (1f - 0.7f * Mathf.Max(c.suppression, c.suppressed / 100f)) * GangDeals.MisconductFactor(c);
        }

        public static string Describe(CompInmate c)
        {
            var sb = new StringBuilder();
            sb.Append("RP_TensionLine".Translate(c.tension.ToString("F0"), (c.suppression * 100f).ToString("F0")));
            if (c.tension >= 30f && c.mainCause != null) sb.Append(" — ").Append(CauseText(c));
            return sb.ToString();
        }
    }
}
