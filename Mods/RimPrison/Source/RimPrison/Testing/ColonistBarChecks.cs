using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The colonist bar of the prison's staff (<see cref="StaffBar"/>): grouped by role in a fixed order, a compact bar with the warden, the pinned and whoever needs the player, and the pins in the
    /// saveload plan.
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>The home map's staff on the bar, in the order it draws them (the bar is rebuilt first: <paramref name="rebuild"/>).</summary>
        private static List<ColonistBar.Entry> BarStaff(Map map, bool rebuild = true)
        {
            if (rebuild) Find.ColonistBar.MarkColonistsDirty();
            return Find.ColonistBar.Entries.Where(e => e.map == map && e.pawn != null).ToList();
        }

        /// <summary>
        /// A made-up member of staff the compact bar has no reason to show: a generated pawn can come with a disease, an infection or a wound that wants tending now, which is "needs the player" (a
        /// census run had a fresh cook on the compact bar). Those are healed; what is left, if anything, is told (false: the check's premise does not hold).
        /// </summary>
        private static bool NoCallForThePlayer(Pawn p, out string why)
        {
            foreach (Hediff h in p.health.hediffSet.hediffs.Where(h => h.TendableNow() || h.Bleeding || h is Hediff_Injury).ToList()) p.health.RemoveHediff(h);
            if (p.InMentalState) p.MentalState.RecoverFromState();
            why = AttentionWhy(p);
            return !StaffBar.ShownOnCompactBar(PrisonGame.Instance, p);
        }

        /// <summary>What the compact bar's test sees in a pawn, for a failure's message.</summary>
        private static string AttentionWhy(Pawn p) =>
            $"{p.LabelShort}: protected {StaffUtility.IsProtected(p)}, pinned {StaffBar.IsPinned(p)}, downed {p.Downed}, mental {p.InMentalState}, burning {p.IsBurning()}, held {HostageCrisis.IsHeld(p)}, " +
                  $"bleeding {p.health.hediffSet.BleedRateTotal:0.###}, to tend {HealthAIUtility.ShouldBeTendedNowByPlayer(p)} ({string.Join(", ", p.health.hediffSet.hediffs.Select(h => h.def.defName))})";

        /// <summary>The bar's own layout needs a screen (headless it is 0 wide): the check's reads are at 1280 x 720 and put back.</summary>
        private static void BarAtASize(Rollback r)
        {
            (int w0, int h0) = (UI.screenWidth, UI.screenHeight);
            r.Add(() =>
            {
                (UI.screenWidth, UI.screenHeight) = (w0, h0);
                StaffBar.MarkDirty();
            });
            (UI.screenWidth, UI.screenHeight) = (1280, 720);
        }

        private static void CheckColonistBarGroupedByRole(Map map)
        {
            Guarded("the colonist bar groups the staff by role", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RimPrisonSettings settings = RimPrisonMod.Settings;
                (bool grouping0, bool compact0) = (settings.groupColonistBar, g.staffBarCompact);
                Dictionary<Pawn, int> orders0 = map.mapPawns.FreeColonists.ToDictionary(p => p, p => p.playerSettings.displayOrder);
                r.Add(() =>
                {
                    settings.groupColonistBar = grouping0;
                    g.staffBarCompact = compact0;
                    foreach (KeyValuePair<Pawn, int> kv in orders0)
                        if (!kv.Key.Destroyed) kv.Key.playerSettings.displayOrder = kv.Value;
                    StaffBar.MarkDirty();
                });
                BarAtASize(r);
                settings.groupColonistBar = true;
                g.staffBarCompact = false;
                // (Hired in the opposite order to the bar's, with two cooks and one with no role: the hire order alone would not do.)
                Pawn cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook), cook2 = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook);
                Pawn chaplain = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Chaplain), doctor = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor);
                Pawn armed = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_ArmedGuard), nobody = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Janitor);
                nobody.GetComp<CompStaff>().role = null;
                // (Recached once before the reads: it gives every new colonist a place in the vanilla order.)
                List<ColonistBar.Entry> entries = BarStaff(map);
                foreach (Pawn p in new[] { cook, cook2, chaplain, doctor, armed, nobody })
                    if (entries.All(e => e.pawn != p)) { ReviewFail("colonist bar", $"the check's own premise: {p.LabelShort} ({StaffBar.GroupOf(p)}) is not on the bar"); return; }

                Pawn warden = WardenSystem.InPost(map) is { Spawned: true } onTheFloor ? onTheFloor : null;
                if (warden != null && entries[0].pawn != warden) ReviewFail("colonist bar", $"the warden is not first on the grouped bar ({entries[0].pawn.LabelShort} is, {StaffBar.GroupOf(entries[0].pawn)})");
                for (int i = 1; i < entries.Count; i++)
                {
                    StaffBarGroup was = StaffBar.GroupOf(entries[i - 1].pawn), now = StaffBar.GroupOf(entries[i].pawn);
                    bool sameGroup = entries[i].group == entries[i - 1].group;
                    if (now < was) ReviewFail("colonist bar", $"{entries[i].pawn.LabelShort} ({now}) comes after {entries[i - 1].pawn.LabelShort} ({was}) on the grouped bar");
                    else if (sameGroup != (now == was)) ReviewFail("colonist bar", $"{entries[i - 1].pawn.LabelShort} ({was}, group {entries[i - 1].group}) and {entries[i].pawn.LabelShort} ({now}, group {entries[i].group}): a group is one category");
                    else if (!sameGroup && entries[i].group <= entries[i - 1].group) ReviewFail("colonist bar", $"the group index does not go up from {was} to {now} ({entries[i - 1].group}, {entries[i].group})");
                    else if (sameGroup && entries[i].pawn.playerSettings.displayOrder < entries[i - 1].pawn.playerSettings.displayOrder)
                        ReviewFail("colonist bar", $"inside the {now} group {entries[i].pawn.LabelShort} comes after {entries[i - 1].pawn.LabelShort} against vanilla's order");
                }
                int group(Pawn p) => entries.First(e => e.pawn == p).group;
                if (!(group(armed) < group(doctor) && group(doctor) < group(chaplain) && group(chaplain) < group(cook) && group(cook) < group(nobody)) || group(cook) != group(cook2))
                    ReviewFail("colonist bar", $"groups: armed guard {group(armed)}, doctor {group(doctor)}, chaplain {group(chaplain)}, cooks {group(cook)} and {group(cook2)}, no role {group(nobody)} (security, medical, programs, upkeep, other expected, in that order)");
                if (Find.ColonistBar.DrawLocs.Count != Find.ColonistBar.Entries.Count) ReviewFail("colonist bar", $"{Find.ColonistBar.Entries.Count} entries and {Find.ColonistBar.DrawLocs.Count} places on the grouped bar");
                if (!Find.ColonistBar.GetColonistsInOrder().Contains(armed) || Find.ColonistBar.ColonistsOrCorpsesInScreenRect(new UnityEngine.Rect(0f, 0f, UI.screenWidth, UI.screenHeight)).Count < entries.Count)
                    ReviewFail("colonist bar", "the bar's other readers (GetColonistsInOrder, ColonistsOrCorpsesInScreenRect) do not see the grouped entries");

                // Drag to reorder inside a group: the second cook goes before the first, and both stay in their group (the group's other staff are in front of them: the cooks are the newest).
                List<ColonistBar.Entry> upkeep = entries.Where(e => e.group == group(cook)).ToList();
                Find.ColonistBar.Reorder(upkeep.FindIndex(e => e.pawn == cook2), upkeep.FindIndex(e => e.pawn == cook), group(cook));
                List<ColonistBar.Entry> reordered = BarStaff(map, rebuild: false);
                int at(Pawn p) => reordered.FindIndex(e => e.pawn == p);
                if (at(cook2) > at(cook) || reordered[at(cook2)].group != reordered[at(cook)].group)
                    ReviewFail("colonist bar", $"dragging the second cook before the first in their group left them at {at(cook2)} and {at(cook)} (or in different groups)");

                // The setting off: vanilla's one group, in the hire order.
                settings.groupColonistBar = false;
                StaffBar.MarkDirty();
                List<ColonistBar.Entry> plain = BarStaff(map, rebuild: false);
                if (plain.Count != entries.Count || plain.Select(e => e.group).Distinct().Count() != 1) ReviewFail("colonist bar", $"with the setting off the bar has {plain.Count} entries in {plain.Select(e => e.group).Distinct().Count()} groups (the grouped bar had {entries.Count}; one group expected)");
                for (int i = 1; i < plain.Count; i++)
                    if (plain[i].pawn.playerSettings.displayOrder < plain[i - 1].pawn.playerSettings.displayOrder) { ReviewFail("colonist bar", "with the setting off the bar is not in vanilla's order"); break; }
                RPLog.Message($"[RimPrison:test] colonist bar groups: {entries.Select(e => e.group).Distinct().Count()} groups for {entries.Count} staff");
            });
        }

        /// <summary>The living staff of each category on the home map's part of the bar, read off a bar that shows everyone (grouped, not compact, nothing collapsed): what a label counts.</summary>
        private static Dictionary<StaffBarGroup, int> BarCounts(Map map) => BarStaff(map, rebuild: false).Where(e => !e.pawn.Dead).GroupBy(e => StaffBar.GroupOf(e.pawn)).ToDictionary(x => x.Key, x => x.Count());

        private static int CountOf(Dictionary<StaffBarGroup, int> counts, StaffBarGroup category) => counts.TryGetValue(category, out int n) ? n : 0;

        private static void CheckColonistBarLabels(Map map)
        {
            Guarded("the colonist bar labels each staff group with its category and count", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RimPrisonSettings settings = RimPrisonMod.Settings;
                (bool grouping0, bool compact0, List<StaffBarGroup> collapsed0, List<Pawn> pins0) = (settings.groupColonistBar, g.staffBarCompact, g.staffBarCollapsed.ToList(), g.staffBarPins.ToList());
                r.Add(() =>
                {
                    settings.groupColonistBar = grouping0;
                    g.staffBarCompact = compact0;
                    g.staffBarCollapsed.Clear();
                    g.staffBarCollapsed.AddRange(collapsed0);
                    g.staffBarPins.Clear();
                    g.staffBarPins.AddRange(pins0);
                    StaffBar.MarkDirty();
                });
                BarAtASize(r);
                settings.groupColonistBar = true;
                g.staffBarCompact = false;
                g.staffBarCollapsed.Clear();
                Pawn cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook), doctor = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor), guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn chaplain = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Chaplain);
                List<ColonistBar.Entry> entries = BarStaff(map);
                if (entries.All(e => e.pawn != guard)) { ReviewFail("colonist bar labels", "the check's own premise: the guard is not on the bar"); return; }
                Dictionary<StaffBarGroup, int> counts = BarCounts(map);

                void readLabels(string when)
                {
                    var homeGroups = Find.ColonistBar.Entries.Where(e => e.map == map).Select(e => e.group).Distinct().ToList();
                    IReadOnlyList<StaffBarLabel> labels = StaffBar.Labels;
                    if (labels.Count != homeGroups.Count || labels.Select(l => l.group).Distinct().Count() != labels.Count || labels.Any(l => !homeGroups.Contains(l.group)))
                        ReviewFail("colonist bar labels", $"{when}: {labels.Count} labels ({string.Join(",", labels.Select(l => $"{l.category}@{l.group}"))}) for the home map's groups {string.Join(",", homeGroups)} (one each expected)");
                    foreach (StaffBarLabel l in labels)
                    {
                        int expected = CountOf(counts, l.category);
                        string name = ("RP_BarGroup_" + l.category).Translate();
                        if (l.count != expected || !l.text.StartsWith(name) || l.category != StaffBarGroup.Warden && !l.text.Contains(expected.ToString()) || l.category == StaffBarGroup.Warden && l.text != name)
                            ReviewFail("colonist bar labels", $"{when}: the {l.category} label reads \"{l.text}\" with count {l.count} ({expected} staff of it on the map)");
                        if (Find.ColonistBar.Entries.Any(e => e.group == l.group && e.pawn != null && StaffBar.GroupOf(e.pawn) != l.category))
                            ReviewFail("colonist bar labels", $"{when}: the {l.category} label is on a group of another category");
                        // Under the group's portraits, centred, below the frame (vanilla's frame reaches 12 x scale under the last row), on the screen.
                        List<Vector2> locs = Find.ColonistBar.DrawLocs;
                        Vector2 size = Find.ColonistBar.Size;
                        float left = float.MaxValue, right = 0f, bottom = 0f;
                        for (int i = l.first; i <= l.last; i++)
                        {
                            left = Mathf.Min(left, locs[i].x);
                            right = Mathf.Max(right, locs[i].x + size.x);
                            bottom = Mathf.Max(bottom, locs[i].y + size.y);
                        }
                        if (Mathf.Abs(l.rect.center.x - (left + right) / 2f) > 0.5f || l.rect.y < bottom + 12f * Find.ColonistBar.Scale - 0.5f || l.rect.xMin < 0f || l.rect.xMax > UI.screenWidth || l.rect.yMax > UI.screenHeight)
                            ReviewFail("colonist bar labels", $"{when}: the {l.category} label's rect {l.rect} is not centred under its group's portraits ({left}..{right}, bottom {bottom}) or is off the screen");
                    }
                    foreach (StaffBarGroup c in new[] { StaffBarGroup.Security, StaffBarGroup.Medical, StaffBarGroup.Programs, StaffBarGroup.Upkeep })
                    {
                        bool shown = BarStaff(map, rebuild: false).Any(e => StaffBar.GroupOf(e.pawn) == c);
                        if (shown != labels.Any(l => l.category == c)) ReviewFail("colonist bar labels", $"{when}: the {c} group is on the bar {shown} and has a label {labels.Any(l => l.category == c)}");
                    }
                }
                readLabels("grouped bar");
                if (StaffBar.Labels.FirstOrDefault(l => l.category == StaffBarGroup.Security) is not { } sec || sec.count < 1 || sec.collapsed)
                    ReviewFail("colonist bar labels", "the grouped bar has no uncollapsed security label with the guard counted");

                // Compact: the groups left have their labels, the counts still of all the staff of the category.
                StaffBar.SetPinned(g, doctor, true);
                StaffBar.SetCompact(g, true);
                BarStaff(map);
                readLabels("compact bar");
                if (!StaffBar.Labels.Any(l => l.category == StaffBarGroup.Medical)) ReviewFail("colonist bar labels", "the compact bar has no medical label for the pinned doctor");
                StaffBar.SetCompact(g, false);

                // The setting off: no labels.
                settings.groupColonistBar = false;
                StaffBar.MarkDirty();
                BarStaff(map);
                if (StaffBar.Labels.Count != 0) ReviewFail("colonist bar labels", $"with the setting off the bar has {StaffBar.Labels.Count} labels");
                RPLog.Message($"[RimPrison:test] colonist bar labels: {entries.Select(e => e.group).Distinct().Count()} groups, one label each with its category and count; none with grouping off");
            });
        }

        private static void CheckColonistBarCollapse(Map map)
        {
            Guarded("a collapsed staff group of the colonist bar shows only its pinned and needy members", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RimPrisonSettings settings = RimPrisonMod.Settings;
                (bool grouping0, bool compact0, List<StaffBarGroup> collapsed0, List<Pawn> pins0) = (settings.groupColonistBar, g.staffBarCompact, g.staffBarCollapsed.ToList(), g.staffBarPins.ToList());
                r.Add(() =>
                {
                    settings.groupColonistBar = grouping0;
                    g.staffBarCompact = compact0;
                    g.staffBarCollapsed.Clear();
                    g.staffBarCollapsed.AddRange(collapsed0);
                    g.staffBarPins.Clear();
                    g.staffBarPins.AddRange(pins0);
                    StaffBar.MarkDirty();
                });
                BarAtASize(r);
                settings.groupColonistBar = true;
                g.staffBarCompact = false;
                g.staffBarCollapsed.Clear();
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), guard2 = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard), armed = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_ArmedGuard);
                Pawn cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook), doctor = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor);
                foreach (Pawn p in g.staffBarPins.ToList()) StaffBar.SetPinned(g, p, false);
                foreach (Pawn p in new[] { guard, guard2, armed, cook, doctor })
                    if (!NoCallForThePlayer(p, out string why)) { ReviewFail("colonist bar collapse", $"the check's own premise: a made-up member of staff needs the player already ({why})"); return; }

                // The bar's pawns of a category (the group of its label, or the whole bar's when it has none), by id.
                HashSet<int> onBar(StaffBarGroup c) => BarStaff(map, rebuild: false).Where(e => StaffBar.GroupOf(e.pawn) == c).Select(e => e.pawn.thingIDNumber).ToHashSet();
                int emptyEntries(StaffBarGroup c) => StaffBar.Labels.FirstOrDefault(l => l.category == c) is { } l ? Find.ColonistBar.Entries.Count(e => e.group == l.group && e.pawn == null) : -1;
                BarStaff(map);
                // What a collapsed category shows: the compact bar's test on the members the open group had (corpses and controllable subhumans included, as the bar has them).
                List<Pawn> securityPawns0 = BarStaff(map, rebuild: false).Where(e => StaffBar.GroupOf(e.pawn) == StaffBarGroup.Security).Select(e => e.pawn).ToList();
                HashSet<int> expectedCollapsed(StaffBarGroup c) => securityPawns0.Where(p => StaffBar.ShownOnCompactBar(g, p)).Select(p => p.thingIDNumber).ToHashSet();
                int securityCount = CountOf(BarCounts(map), StaffBarGroup.Security);
                var (security0, medical0, upkeep0) = (onBar(StaffBarGroup.Security), onBar(StaffBarGroup.Medical), onBar(StaffBarGroup.Upkeep));
                if (!security0.Contains(guard.thingIDNumber) || !security0.Contains(armed.thingIDNumber)) { ReviewFail("colonist bar collapse", "the check's own premise: the guards are not on the bar"); return; }

                // Collapsed with none pinned: the appointees only (or one empty entry when there are none); the other groups as they were.
                StaffBar.SetCollapsed(g, StaffBarGroup.Security, true);
                BarStaff(map);
                HashSet<int> want = expectedCollapsed(StaffBarGroup.Security);
                if (!onBar(StaffBarGroup.Security).SetEquals(want))
                    ReviewFail("colonist bar collapse", $"a collapsed security group shows {onBar(StaffBarGroup.Security).Count} staff ({string.Join(",", onBar(StaffBarGroup.Security))}), the compact bar's test passes {want.Count} ({string.Join(",", want)})");
                if (want.Count == 0 && emptyEntries(StaffBarGroup.Security) != 1) ReviewFail("colonist bar collapse", $"a collapsed security group with nobody to show has {emptyEntries(StaffBarGroup.Security)} empty entries (one expected: it keeps its slot and label)");
                if (want.Count > 0 && emptyEntries(StaffBarGroup.Security) != 0) ReviewFail("colonist bar collapse", "a collapsed security group with staff to show also has an empty entry");
                if (!onBar(StaffBarGroup.Medical).SetEquals(medical0) || !onBar(StaffBarGroup.Upkeep).SetEquals(upkeep0)) ReviewFail("colonist bar collapse", "collapsing security changed the medical or upkeep group");
                if (StaffBar.Labels.FirstOrDefault(l => l.category == StaffBarGroup.Security) is not { collapsed: true } label || label.count != securityCount || label.text != "RP_BarLabelCollapsed".Translate("RP_BarGroup_Security".Translate(), label.count).Resolve())
                    ReviewFail("colonist bar collapse", $"the collapsed security label reads \"{StaffBar.Labels.FirstOrDefault(l => l.category == StaffBarGroup.Security)?.text}\" (collapsed, with the count of all expected)");

                // One pinned, one downed: both stay, the rest go.
                StaffBar.SetPinned(g, guard, true);
                HealthUtility.DamageUntilDowned(guard2, allowBleedingWounds: false);
                if (!guard2.Downed) { ReviewFail("colonist bar collapse", "the check's own premise: the guard could not be downed"); return; }
                BarStaff(map);
                if (!onBar(StaffBarGroup.Security).SetEquals(expectedCollapsed(StaffBarGroup.Security)) || !onBar(StaffBarGroup.Security).Contains(guard.thingIDNumber) || !onBar(StaffBarGroup.Security).Contains(guard2.thingIDNumber) || onBar(StaffBarGroup.Security).Contains(armed.thingIDNumber))
                    ReviewFail("colonist bar collapse", $"a collapsed security group with one pinned and one downed guard shows {string.Join(",", onBar(StaffBarGroup.Security))} (the pinned, the downed and the appointees, not the healthy armed guard)");
                if (emptyEntries(StaffBarGroup.Security) != 0) ReviewFail("colonist bar collapse", "a collapsed group with staff to show has an empty entry");

                // The compact toggle wins: in compact mode a collapsed group shows what the compact bar shows, and the label still counts all.
                StaffBar.SetCompact(g, true);
                BarStaff(map);
                if (!onBar(StaffBarGroup.Security).SetEquals(expectedCollapsed(StaffBarGroup.Security)) || StaffBar.Labels.FirstOrDefault(l => l.category == StaffBarGroup.Security)?.count != securityCount)
                    ReviewFail("colonist bar collapse", "the compact bar with security collapsed does not show what the compact bar shows, or its label does not count all security staff");
                StaffBar.SetCompact(g, false);

                // Expanded: everyone back, and the toggle the click calls does the same.
                StaffBar.SetCollapsed(g, StaffBarGroup.Security, false);
                BarStaff(map);
                if (!onBar(StaffBarGroup.Security).IsSupersetOf(security0) || !onBar(StaffBarGroup.Medical).SetEquals(medical0) || StaffBar.Labels.FirstOrDefault(l => l.category == StaffBarGroup.Security)?.collapsed != false)
                    ReviewFail("colonist bar collapse", "expanding security did not bring its staff back");
                // The warden's group has nothing to collapse.
                StaffBar.SetCollapsed(g, StaffBarGroup.Warden, true);
                if (g.staffBarCollapsed.Contains(StaffBarGroup.Warden)) ReviewFail("colonist bar collapse", "the warden's group could be collapsed");
                // With grouping off the collapse does nothing.
                StaffBar.SetCollapsed(g, StaffBarGroup.Security, true);
                settings.groupColonistBar = false;
                StaffBar.MarkDirty();
                if (!BarStaff(map).Select(e => e.pawn.thingIDNumber).ToHashSet().IsSupersetOf(security0) || StaffBar.Labels.Count != 0)
                    ReviewFail("colonist bar collapse", "with grouping off a collapsed category still hides staff or labels are drawn");
                RPLog.Message("[RimPrison:test] colonist bar collapse: security collapsed to the pinned, the needy and the appointees (an empty entry when none); expanded, all back");
            });
        }

        private static void CheckCompactColonistBar(Map map)
        {
            Guarded("the compact colonist bar shows the warden, the pinned and those who need the player", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                (bool compact0, List<Pawn> pins0) = (g.staffBarCompact, g.staffBarPins.ToList());
                r.Add(() =>
                {
                    g.staffBarCompact = compact0;
                    g.staffBarPins.Clear();
                    g.staffBarPins.AddRange(pins0);
                    StaffBar.MarkDirty();
                });
                BarAtASize(r);
                Pawn cook = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Cook), doctor = ThrowawayStaff(map, r, RPStaffRoleDefOf.RP_Doctor), guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                Pawn warden = WardenSystem.InPost(map) is { Spawned: true } onTheFloor ? onTheFloor : null;
                foreach (Pawn p in new[] { cook, doctor, guard })
                    if (!NoCallForThePlayer(p, out string why)) { ReviewFail("compact colonist bar", $"the check's own premise: a made-up member of staff is on the compact bar already ({why})"); return; }
                StaffBar.SetPinned(g, doctor, true);
                StaffBar.SetCompact(g, true);
                bool on(Pawn p) => BarStaff(map, rebuild: false).Any(e => e.pawn == p);
                if (!on(doctor) || on(cook) || on(guard) || warden != null && !on(warden))
                    ReviewFail("compact colonist bar", $"pinned doctor on the bar {on(doctor)}, unpinned healthy cook {on(cook)}, guard {on(guard)}, warden {(warden == null ? "(none in post)" : on(warden).ToString())} (true, false, false, true expected)");

                // A drag on the compact bar moves nobody: vanilla's reorder renumbers the bar's entries only, and the hidden cook's place would shift once the full bar came back.
                int cookOrder = cook.playerSettings.displayOrder, doctorOrder = doctor.playerSettings.displayOrder;
                if (BarStaff(map, rebuild: false).FirstOrDefault(e => e.pawn == doctor) is { pawn: not null } doctorEntry)
                {
                    doctorEntry.reorderAction(0, 1);
                    if (cook.playerSettings.displayOrder != cookOrder || doctor.playerSettings.displayOrder != doctorOrder)
                        ReviewFail("compact colonist bar", $"a drag on the compact bar moved staff: the hidden cook {cookOrder} -> {cook.playerSettings.displayOrder}, the doctor {doctorOrder} -> {doctor.playerSettings.displayOrder}");
                }

                // One of them needs the player now: the 250-tick check finds it, with no mark on the bar from anywhere else.
                HealthUtility.DamageUntilDowned(guard, allowBleedingWounds: false);
                if (!guard.Downed) { ReviewFail("compact colonist bar", "the check's own premise: the guard could not be downed"); return; }
                StaffBar.Rare(g, map);
                if (!on(guard) || on(cook)) ReviewFail("compact colonist bar", $"a downed guard is on the compact bar {on(guard)}, the healthy cook {on(cook)} (true, false expected; {AttentionWhy(cook)})");

                // Unpinned, the doctor is off it again.
                StaffBar.SetPinned(g, doctor, false);
                if (on(doctor)) ReviewFail("compact colonist bar", "an unpinned healthy doctor is still on the compact bar");

                // Nobody of the map shown (no warden or state appointee on site, nobody in need): the map keeps its empty entry, so its frame takes the player home from another map.
                if (!map.mapPawns.FreeColonists.Any(p => StaffBar.ShownOnCompactBar(g, p) && p != guard))
                {
                    HealthUtility.HealNonPermanentInjuriesAndRestoreLegs(guard);
                    guard.health.Reset();
                    StaffBar.Rare(g, map);
                    List<ColonistBar.Entry> homeEntries = Find.ColonistBar.Entries.Where(e => e.map == map).ToList();
                    if (!map.mapPawns.FreeColonists.Any(p => StaffBar.ShownOnCompactBar(g, p)) && (homeEntries.Count != 1 || homeEntries[0].pawn != null))
                        ReviewFail("compact colonist bar", $"a compact bar with nobody of the map shown has {homeEntries.Count} entries of it ({string.Join(",", homeEntries.Select(e => e.pawn?.LabelShort ?? "empty"))}; one empty expected)");
                }

                // Back to all: everyone, grouped.
                StaffBar.SetCompact(g, false);
                if (!on(cook) || !on(doctor) || !on(guard)) ReviewFail("compact colonist bar", $"with the compact bar off the cook {on(cook)}, the doctor {on(doctor)} and the guard {on(guard)} are not all on it");
                RPLog.Message("[RimPrison:test] compact colonist bar: warden, pinned and downed shown; the rest hidden; all back after the toggle");
            });
        }

        // ---- the saveload plan: the pins and the toggle ---------------------------------------------------------------------------------------------------

        private static int barSeedId = -1;
        private static List<StaffBarGroup> barCollapsed0 = new List<StaffBarGroup>();

        /// <summary>The compact toggle and whether the seeded one was pinned before the seed (put back after the load, whatever the save had).</summary>
        private static bool barCompact0, barSeedWasPinned;

        /// <summary>A pinned member of staff and the compact bar, saved (the snapshot has both).</summary>
        private static void SeedBarForReload(Map map)
        {
            barSeedId = -1;
            PrisonGame g = PrisonGame.Instance;
            if (map.mapPawns.FreeColonistsSpawned.Where(x => x.GetComp<CompStaff>() is { role: not null, Protected: false }).OrderBy(x => x.thingIDNumber).FirstOrDefault() is not { } pinned)
            {
                reloadProblems.Add("saveload: no member of staff to pin");
                return;
            }
            barSeedId = pinned.thingIDNumber;
            (barCompact0, barSeedWasPinned) = (g.staffBarCompact, StaffBar.IsPinned(pinned));
            StaffBar.SetPinned(g, pinned, true);
            g.staffBarCompact = true;
            barCollapsed0 = g.staffBarCollapsed.ToList();
            StaffBar.SetCollapsed(g, StaffBarGroup.Security, true);
            StaffBar.SetCollapsed(g, StaffBarGroup.Upkeep, true);
        }

        private static void CheckBarAfterReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            if (barSeedId < 0) return;
            Pawn seeded = g.staffBarPins.FirstOrDefault(p => p.thingIDNumber == barSeedId);
            if (!g.staffBarCompact || seeded == null)
                reloadProblems.Add($"the compact colonist bar and its pin did not survive the save (compact {g.staffBarCompact}, pins {string.Join(",", g.staffBarPins.Select(p => p.thingIDNumber))}, pinned {barSeedId})");
            else RPLog.Message("[RimPrison:test] saveload colonist bar: the pin and the compact bar are intact");
            if (!g.staffBarCollapsed.Contains(StaffBarGroup.Security) || !g.staffBarCollapsed.Contains(StaffBarGroup.Upkeep))
                reloadProblems.Add($"the collapsed colonist bar groups did not survive the save (collapsed: {string.Join(",", g.staffBarCollapsed)}; security and upkeep expected)");
            g.staffBarCollapsed.Clear();
            g.staffBarCollapsed.AddRange(barCollapsed0);
            // (Back as the game had them before the seed: the toggle, and the seeded one's pin only if it was not there before.)
            g.staffBarCompact = barCompact0;
            if (seeded != null && !barSeedWasPinned) g.staffBarPins.Remove(seeded);
            StaffBar.MarkDirty();
        }
    }
}
