using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Deployment, like Prison Architect's: security wings to paint, armed response and wing lockdowns on the left; every
    /// door with what lies on each side and its mode (changeable here), and where each guard patrols, on the right.
    /// </summary>
    public static class DeploymentTab
    {
        private static Vector2 doorScroll, patrolScroll;

        /// <summary>The left column's scroll and the height its content took the last time it was drawn: with every level open (the penitentiary: seven wings, seven lockdowns) it is taller
        /// than the window (about 740 against 560 on a 720-line screen), and the lockdowns at its foot were out of reach.</summary>
        public static Vector2 leftScroll;
        private static float leftHeight;

        // The wing designators and the doors in reading order change rarely, and the window asks on every GUI event: both lists are kept for a few frames.
        private const int ListFrames = 15;
        private static readonly List<Designator_PrisonZone> wingCache = new List<Designator_PrisonZone>();
        private static readonly List<CompPrisonDoor> doorCache = new List<CompPrisonDoor>();
        private static int wingFrame = -ListFrames, doorFrame = -ListFrames;
        private static int doorMapId = -1;

        /// <summary>A wing's line: its cells and free beds, each word in the number it takes ("1 cell", "4 cells").</summary>
        // (Vanilla's English plural keeps the singular for any count under two: "0 free bed".)
        private static string WingLine(int cells, int beds)
        {
            string Word(string key, int n) => n == 1 ? key.Translate().ToString() : Find.ActiveLanguageWorker.Pluralize(key.Translate());
            return "RP_DeployWingLine".Translate(cells, Word("RP_DeployCell", cells), beds, Word("RP_DeployBed", beds)).ToString();
        }

        public static void Draw(Rect rect, Map map, ref Vector2 scroll)
        {
            PrisonGame game = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            // (A scroll view only when the content of the last frame did not fit, so a short column is laid out as it was; its bar is added to the column's width, not taken from the rows, whose labels are cut
            // at 380.)
            bool scrolls = leftHeight > rect.height;
            var leftOuter = new Rect(rect.x, rect.y, 380f + (scrolls ? 16f : 0f), rect.height);
            var right = new Rect(leftOuter.xMax + 20f, rect.y, rect.xMax - leftOuter.xMax - 20f, rect.height);
            var left = new Rect(0f, 0f, 380f, Mathf.Max(leftHeight, leftOuter.height));
            Widgets.BeginScrollView(leftOuter, ref leftScroll, left, showScrollbars: scrolls);

            // Wings: paint them (same rows as Rooms), with beds and free beds.
            float y = left.y;
            Header(left.x, ref y, left.width, "RP_DeployWings");
            if (Time.frameCount - wingFrame >= ListFrames || Time.frameCount < wingFrame)
            {
                wingFrame = Time.frameCount;
                wingCache.Clear();
                wingCache.AddRange(RPDefOf.RP_PrisonZones.AllResolvedDesignators.OfType<Designator_PrisonZone>().Where(d => d.zone?.security != null));
            }
            List<Designator_PrisonZone> wings = wingCache;
            Dictionary<PrisonZoneDef, int> cellRooms = CellRooms(map, pm);
            foreach (Designator_PrisonZone d in wings)
            {
                var row = new Rect(left.x, y, left.width, 34f);
                bool locked = !Unlocks.IsUnlocked(d.zone);
                if (Find.DesignatorManager.SelectedDesignator == d) Widgets.DrawBox(row, 2);
                else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                GUI.color = locked ? Color.gray : d.zone.color;
                GUI.DrawTexture(new Rect(row.x + 4f, row.y + 5f, 24f, 24f), ZoneIcons.For(d.zone));
                GUI.color = locked ? Color.gray : Color.white;
                Widgets.Label(new Rect(row.x + 34f, row.y + 7f, 170f, 22f), d.zone.LabelCap);
                int beds = IntakeSystem.FreeBedCount(map, d.zone.security, inWingOnly: true);
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 206f, row.y + 9f, row.width - 206f, 20f),
                    locked ? "RP_RoomsLocked".Translate(Unlocks.GateOf(d.zone)?.label ?? "?").ToString() : WingLine(cellRooms.TryGetValue(d.zone, out int n) ? n : 0, beds));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                if (!locked && Widgets.ButtonInvisible(row)) Find.DesignatorManager.Select(d);
                y += 36f;
            }
            y += 10f;
            DrawUnitOfficers(left, ref y, map, wings, cellRooms);
            // Armed response.
            Header(left.x, ref y, left.width, "RP_ArmedPolicyLabel");
            // (Nobody is armed before the project: the choices are greyed out and say what they need, as the wing rows do.)
            BureaucracyDef armedGate = ArmedResponse.LockedBy;
            foreach (ArmedResponsePolicy pol in new[] { ArmedResponsePolicy.HoldFire, ArmedResponsePolicy.Escapes, ArmedResponsePolicy.EscapesAndViolence })
            {
                var r = new Rect(left.x, y, left.width, 24f);
                if (armedGate != null)
                {
                    GUI.color = Color.gray;
                    Widgets.RadioButtonLabeled(r, ("RP_Armed_" + pol).Translate(), game.armedResponse == pol);
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(r, "RP_ArmedLockedTip".Translate(armedGate.label));
                }
                else
                {
                    TooltipHandler.TipRegion(r, ("RP_ArmedDesc_" + pol).Translate());
                    if (Widgets.RadioButtonLabeled(r, ("RP_Armed_" + pol).Translate(), game.armedResponse == pol)) game.armedResponse = pol;
                }
                y += 26f;
            }
            if (armedGate != null)
            {
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(left.x, y, left.width, 20f), "RP_LockedBy".Translate(armedGate.label));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 22f;
            }
            // The state's hand on the policy (what is in force is not what is set, for as long as it lasts).
            if (ArmedResponse.Effective(game) != game.armedResponse)
            {
                GUI.color = PrisonHUD.Warn;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(left.x, y, left.width, 20f), "RP_DeployArmedLocked".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 22f;
            }
            if (SpecialMeasures.ChiefInPost)
            {
                GUI.color = PrisonHUD.Warn;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(left.x, y, left.width, 34f), "RP_DeployChiefPatrols".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 36f;
            }
            y += 10f;
            DrawPosts(left, ref y, map);
            DrawCare(left, ref y, map);
            EvacuationUI.Draw(left, ref y, map);
            // The roster's strip of guards on duty (worked out once an hour): what the posts' hours and the pad's guards are drawn from.
            Header(left.x, ref y, left.width, "RP_DeployRoster");
            var stripRect = new Rect(left.x, y, left.width, 44f);
            Roster.DrawStrip(stripRect, map);
            y += 50f;
            // The shuttle pad: guards stand watch on it while the shuttle is down (nobody slips aboard, the crates are searched).
            Header(left.x, ref y, left.width, "RP_DeployPad");
            var padRow = new Rect(left.x, y, left.width, 26f);
            TooltipHandler.TipRegion(padRow, "RP_DeployPadGuardsTip".Translate());
            Widgets.Label(new Rect(padRow.x, padRow.y + 3f, padRow.width - 80f, 22f), "RP_DeployPadGuards".Translate(game.shuttle.padGuards));
            if (Widgets.ButtonText(new Rect(padRow.xMax - 70f, padRow.y, 30f, 24f), "-") && game.shuttle.padGuards > 0) game.shuttle.padGuards--;
            if (Widgets.ButtonText(new Rect(padRow.xMax - 34f, padRow.y, 30f, 24f), "+") && game.shuttle.padGuards < ShuttleSystem.MaxPadGuards) game.shuttle.padGuards++;
            y += 28f;
            bool hasPad = ShuttleSystem.HasPad(map);
            GUI.color = hasPad ? PrisonHUD.Muted : PrisonHUD.Warn;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(left.x, y, left.width, 34f), (hasPad ? "RP_DeployPadBuilt" : "RP_DeployPadNone").Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            y += 36f;
            // Wing lockdowns.
            Header(left.x, ref y, left.width, "RP_DeployLockdowns");
            foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(s => s.uiOrder))
            {
                if (!Unlocks.IsUnlocked(sec)) continue;
                var r = new Rect(left.x, y, left.width, 26f);
                Widgets.DrawBoxSolid(new Rect(r.x, r.y + 5f, 5f, 16f), sec.color);
                Widgets.Label(new Rect(r.x + 10f, r.y + 3f, 180f, 22f), sec.LabelCap);
                bool active = game.wingLockdowns.TryGetValue(sec, out int until) && until > Find.TickManager.TicksGame;
                var b = new Rect(r.xMax - 150f, r.y, 150f, 24f);
                if (active)
                {
                    GUI.color = PrisonHUD.Bad;
                    if (Widgets.ButtonText(b, "RP_DeployLockdownEnd".Translate(((until - Find.TickManager.TicksGame) / (float)GenDate.TicksPerHour).ToString("0"))))
                        game.wingLockdowns[sec] = -1;
                    GUI.color = Color.white;
                }
                else if (Widgets.ButtonText(b, "RP_DeployLockdownStart".Translate())) game.StartWingLockdown(sec, 4);
                y += 28f;
            }
            Widgets.EndScrollView();
            leftHeight = y + 4f;

            // Doors: one row each, what lies on each side, mode menu, jump.
            float ry = right.y;
            Header(right.x, ref ry, right.width, "RP_DeployDoors");
            if (Time.frameCount - doorFrame >= ListFrames || Time.frameCount < doorFrame || doorMapId != map.uniqueID)
            {
                doorFrame = Time.frameCount;
                doorMapId = map.uniqueID;
                doorCache.Clear();
                doorCache.AddRange(pm.Doors.Where(d => d.parent.Spawned).OrderBy(d => d.parent.Position.z).ThenBy(d => d.parent.Position.x));
            }
            List<CompPrisonDoor> doors = doorCache;
            float doorsH = Mathf.Min(doors.Count * 26f, right.height * 0.45f);
            var outer = new Rect(right.x, ry, right.width, doorsH);
            var view = new Rect(0f, 0f, outer.width - 16f, doors.Count * 26f);
            Widgets.BeginScrollView(outer, ref doorScroll, view);
            for (int i = 0; i < doors.Count; i++)
            {
                CompPrisonDoor d = doors[i];
                var row = new Rect(0f, i * 26f, view.width, 24f);
                // (Only the doors in view: each row asks the map for its two sides and translates several strings.)
                if (row.yMax < doorScroll.y || row.y > doorScroll.y + outer.height || !d.parent.Spawned) continue;
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                (string here, string there) = Places(d, pm);
                string sides = "RP_DeployDoorSides".Translate(here ?? Side(d.sideA), there ?? Side(d.sideB)).ToString();
                var sidesRect = new Rect(row.x + 4f, row.y + 2f, 230f, 22f);
                Widgets.Label(sidesRect, sides.Truncate(228f));
                TooltipHandler.TipRegion(sidesRect, sides);
                GUI.color = ModeColor(d.mode);
                var modeRect = new Rect(row.x + 240f, row.y, 110f, 24f);
                TooltipHandler.TipRegion(modeRect, "RP_DoorModeDesc".Translate());
                if (Widgets.ButtonText(modeRect, ("RP_DoorMode_" + d.mode).Translate(), drawBackground: false))
                {
                    var options = new List<FloatMenuOption>();
                    foreach (PrisonDoorMode m in new[] { PrisonDoorMode.Auto, PrisonDoorMode.Inmates, PrisonDoorMode.StaffOnly, PrisonDoorMode.Locked })
                    {
                        PrisonDoorMode chosen = m;
                        options.Add(new FloatMenuOption(("RP_DoorMode_" + m).Translate(), () =>
                        {
                            d.mode = chosen;
                            pm.Notify_DoorsChanged();
                        }));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                GUI.color = d.mode == PrisonDoorMode.Locked ? PrisonHUD.Bad : d.inmatePass ? PrisonHUD.Warn : PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 356f, row.y + 4f, row.width - 386f, 20f), d.mode == PrisonDoorMode.Locked ? "RP_DoorLocked".Translate() : d.inmatePass ? "RP_DoorInmatesPass".Translate() : "RP_DoorInmatesBlocked".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                var jump = new Rect(row.xMax - 24f, row.y, 24f, 24f);
                if (Widgets.ButtonImage(jump, TexButton.Search)) CameraJumper.TryJumpAndSelect(d.parent);
            }
            Widgets.EndScrollView();
            ry += doorsH + 12f;

            // The danger map's hottest patches: where to post guards and cameras.
            Header(right.x, ref ry, right.width, "RP_DeployDanger");
            List<HotPatch> hot = DangerMap.Hottest(pm);
            TooltipHandler.TipRegion(new Rect(right.x, ry - 32f, right.width, 30f), "RP_DeployDangerTip".Translate());
            if (hot.Count == 0)
            {
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(right.x, ry, right.width, 24f), "RP_DeployDangerNone".Translate());
                GUI.color = Color.white;
                ry += 28f;
            }
            foreach (HotPatch patch in hot)
            {
                // (The place, and under it in small print what happened there.)
                var row = new Rect(right.x, ry, right.width, 48f);
                // (A patch that is mostly dark now says so: a guard sees 3 cells there and a camera nothing, so it wants a lamp as much as a guard.)
                string shown = patch.unlit ? "RP_DangerPlaceUnlit".Translate(patch.place).ToString() : patch.place;
                Widgets.Label(new Rect(row.x, row.y, row.width - 30f, 22f), shown.Truncate(row.width - 32f));
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 10f, row.y + 20f, row.width - PostButtonWidth - 50f, 18f), patch.KindsText().Truncate(row.width - PostButtonWidth - 52f));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(new Rect(row.x, row.y, row.width - PostButtonWidth - 30f, row.height), patch.unlit ? "RP_DangerRowUnlit".Translate(patch.place, patch.KindsText()) : "RP_DangerRow".Translate(patch.place, patch.KindsText()));
                if (Widgets.ButtonImage(new Rect(row.xMax - 24f, row.y, 24f, 24f), TexButton.Search)) CameraJumper.TryJump(patch.center, map);
                // A guard post at the patch: put up at once (a spot, like vanilla's), on its cell or the nearest cell to stand on. One already beside it: nothing to add.
                var post = new Rect(row.xMax - PostButtonWidth - 4f, row.y + 26f, PostButtonWidth, 22f);
                if (GuardPosts.PostNear(map, patch.center))
                {
                    GUI.color = PrisonHUD.Muted;
                    Widgets.Label(new Rect(post.x + 6f, post.y + 2f, post.width - 6f, 18f), "RP_PostHerePlaced".Translate());
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(post, "RP_PostHerePlacedTip".Translate());
                }
                else
                {
                    TooltipHandler.TipRegion(post, "RP_PostHereTip".Translate());
                    if (Widgets.ButtonText(post, "RP_PostHere".Translate()))
                    {
                        if (GuardPosts.Place(map, patch.center) == null) Messages.Message("RP_PostNoRoom".Translate(patch.place), MessageTypeDefOf.RejectInput, historical: false);
                        else postFrame = -ListFrames;
                    }
                }
                Text.Font = GameFont.Small;
                ry += 50f;
            }
            ry += 10f;

            // Patrols: one row per guard in a scroll view (the doors and the danger map above take what they take: every guard stays reachable however many there are).
            Header(right.x, ref ry, right.width, "RP_DeployPatrols");
            List<Pawn> guards = StaffCommute.AllStaff(map).Where(p => p.GetComp<CompStaff>().role.guardDuties).ToList();
            (Rect patrolOuter, Rect patrolView) = PatrolView(right, ry, guards.Count);
            Widgets.BeginScrollView(patrolOuter, ref patrolScroll, patrolView);
            for (int i = 0; i < guards.Count; i++)
            {
                var row = new Rect(0f, i * PatrolRowHeight, patrolView.width, 26f);
                if (row.yMax < patrolScroll.y || row.y > patrolScroll.y + patrolOuter.height) continue;
                Pawn g = guards[i];
                CompStaff sc = g.GetComp<CompStaff>();
                Widgets.Label(new Rect(row.x, row.y + 3f, 140f, 22f), g.LabelShortCap.Truncate(138f));
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(row.x + 142f, row.y + 3f, 120f, 22f), (StaffCommute.AtHome(g) ? "RP_StaffStatus_Home" : sc.nightShift ? "RP_StaffNightShift" : "RP_StaffDayShift").Translate());
                GUI.color = Color.white;
                if (Widgets.ButtonText(new Rect(row.xMax - 170f, row.y, 170f, 24f), sc.patrolZone?.LabelCap ?? "RP_PatrolAnywhereShort".Translate()))
                {
                    var options = new List<FloatMenuOption> { new FloatMenuOption("RP_PatrolAnywhere".Translate(), () => UnitOfficers.SetPatrol(g, sc, null)) };
                    foreach (PrisonZoneDef z in DefDatabase<PrisonZoneDef>.AllDefsListForReading)
                    {
                        if (!z.inmateAccess || !Kin.PatrolAllowed(g, z)) continue;
                        PrisonZoneDef chosen = z;
                        options.Add(new FloatMenuOption(z.LabelCap, () => UnitOfficers.SetPatrol(g, sc, chosen)));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            Widgets.EndScrollView();
        }

        public const float PatrolRowHeight = 28f;

        /// <summary>The danger list's "post a guard here" button.</summary>
        private const float PostButtonWidth = 130f;

        /// <summary>A guard post's block in the left column: its place, hours, guard, and who stands there.</summary>
        public const float PostRowHeight = 72f;

        // (The posts in order: the window asks on every GUI event.)
        private static readonly List<CompGuardPost> postCache = new List<CompGuardPost>();
        private static int postFrame = -ListFrames, postMapId = -1;

        private static void DrawPosts(Rect left, ref float y, Map map)
        {
            Header(left.x, ref y, left.width, "RP_DeployPosts");
            TooltipHandler.TipRegion(new Rect(left.x, y - 32f, left.width, 30f), "RP_DeployPostsTip".Translate());
            if (Time.frameCount - postFrame >= ListFrames || Time.frameCount < postFrame || postMapId != map.uniqueID)
            {
                postFrame = Time.frameCount;
                postMapId = map.uniqueID;
                postCache.Clear();
                postCache.AddRange(GuardPosts.Posts(map));
            }
            if (SpecialMeasures.ChiefInPost)
            {
                GUI.color = PrisonHUD.Warn;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(left.x, y, left.width, 34f), "RP_DeployPostsSuspended".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 36f;
            }
            if (postCache.Count == 0)
            {
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(left.x, y, left.width, 34f), "RP_DeployPostsNone".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 36f;
            }
            List<Pawn> guards = null;
            int[] strip = Roster.Strip(map);
            foreach (CompGuardPost post in postCache)
            {
                if (!post.parent.Spawned) continue;
                var row = new Rect(left.x, y, left.width, PostRowHeight - 2f);
                Widgets.DrawLightHighlight(row);
                string place = GuardPosts.PlaceOf(post);
                Widgets.Label(new Rect(row.x + 4f, row.y + 1f, row.width - 34f, 22f), place.Truncate(row.width - 36f));
                if (Widgets.ButtonImage(new Rect(row.xMax - 26f, row.y + 1f, 24f, 24f), TexButton.Search)) CameraJumper.TryJumpAndSelect(post.parent);
                // The hours: two menus of hours (the same hour in both is all day).
                var from = new Rect(row.x + 4f, row.y + 26f, 86f, 24f);
                var to = new Rect(from.xMax + 4f, from.y, 86f, 24f);
                TooltipHandler.TipRegion(new Rect(from.x, from.y, to.xMax - from.x, from.height), "RP_DeployPostHoursTip".Translate());
                if (Widgets.ButtonText(from, "RP_DeployPostFrom".Translate(GuardPosts.HourLabel(post.fromHour))))
                    Find.WindowStack.Add(new FloatMenu(HourOptions(h => post.fromHour = h)));
                if (Widgets.ButtonText(to, "RP_DeployPostTo".Translate(GuardPosts.HourLabel(post.toHour))))
                    Find.WindowStack.Add(new FloatMenu(HourOptions(h => post.toHour = h)));
                // The guard: any free guard, or one by name.
                var who = new Rect(to.xMax + 8f, from.y, row.xMax - to.xMax - 12f, 24f);
                TooltipHandler.TipRegion(who, "RP_DeployPostGuardTip".Translate());
                if (Widgets.ButtonText(who, post.GuardLabel().Truncate(who.width - 8f)))
                {
                    guards ??= StaffCommute.AllStaff(map).Where(p => p.GetComp<CompStaff>().role.guardDuties).ToList();
                    var options = new List<FloatMenuOption> { new FloatMenuOption("RP_DeployPostGuardMenuAny".Translate(), () => post.assigned = null) };
                    foreach (Pawn g in guards)
                    {
                        Pawn chosen = g;
                        options.Add(new FloatMenuOption(g.LabelShortCap, () => post.assigned = chosen));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
                // Who stands there now (or why nobody is sent).
                GUI.color = SpecialMeasures.ChiefInPost ? PrisonHUD.Warn : PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                // (How thin the roster is in the post's hours, from the strip: the fewest guards on duty in any of them.)
                string status = post.HoursText() + " (" + "RP_PostFewest".Translate(Roster.Fewest(strip, post.fromHour, post.toHour)) + ") - " + post.StatusText();
                var statusRect = new Rect(row.x + 4f, row.y + 52f, row.width - 8f, 18f);
                Widgets.Label(statusRect, status.Truncate(row.width - 10f));
                TooltipHandler.TipRegion(statusRect, status);
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += PostRowHeight;
            }
            y += 10f;
        }

        /// <summary>Where the unit officers' block began in the left column the last time it was drawn (the tour scrolls to it).</summary>
        public static float unitBlockY;

        public const float UnitRowHeight = 46f;

        /// <summary>The unit officers: a slot per wing with cells (or with an officer named), the guard named (a menu), and under it the days in post and the shift they work, or why the slot is vacant.</summary>
        private static void DrawUnitOfficers(Rect left, ref float y, Map map, List<Designator_PrisonZone> wings, Dictionary<PrisonZoneDef, int> cellRooms)
        {
            unitBlockY = y;
            Header(left.x, ref y, left.width, "RP_Unit_Header");
            TooltipHandler.TipRegion(new Rect(left.x, y - 32f, left.width, 30f), "RP_Unit_HeaderTip".Translate());
            bool any = false;
            foreach (Designator_PrisonZone d in wings)
            {
                SecurityLevelDef wing = d.zone.security;
                Pawn holder = UnitOfficers.HolderOf(map, wing);
                if (!Unlocks.IsUnlocked(d.zone) || holder == null && (!cellRooms.TryGetValue(d.zone, out int cells) || cells == 0)) continue;
                any = true;
                var row = new Rect(left.x, y, left.width, UnitRowHeight - 2f);
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                TooltipHandler.TipRegion(row, "RP_Unit_RowTip".Translate(d.zone.label, UnitOfficers.TrustPerDay.ToString("0.#"), StaffMemories.Restrained.ToString("0"), UnitOfficers.DriftCeiling.ToString("0"),
                    (UnitOfficers.TipBase * 100f).ToString("0"), (UnitOfficers.TipCap * 100f).ToString("0"), (UnitOfficers.ComplaintCut * 100f).ToString("0"), (UnitOfficers.LeanCap * 100f).ToString("0")));
                Widgets.DrawBoxSolid(new Rect(row.x, row.y + 5f, 5f, 16f), d.zone.color);
                Widgets.Label(new Rect(row.x + 10f, row.y + 2f, 180f, 22f), d.zone.LabelCap.ToString().Truncate(178f));
                var button = new Rect(row.xMax - 170f, row.y, 170f, 24f);
                if (Widgets.ButtonText(button, holder != null ? holder.LabelShortCap.Truncate(160f) : "RP_Unit_None".Translate().ToString()))
                    Find.WindowStack.Add(new FloatMenu(UnitOfficers.SlotOptions(map, wing), "RP_Unit_MenuTitle".Translate(d.zone.label)));
                CompStaff sc = holder?.GetComp<CompStaff>();
                GUI.color = sc == null ? PrisonHUD.Muted : UnitOfficers.Away(sc) || UnitOfficers.EffectsSuspended ? PrisonHUD.Warn : PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                string line = UnitOfficers.SlotLine(holder);
                var lineRect = new Rect(row.x + 10f, row.y + 25f, row.width - 12f, 18f);
                Widgets.Label(lineRect, line.Truncate(row.width - 14f));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += UnitRowHeight;
            }
            if (!any)
            {
                GUI.color = PrisonHUD.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(left.x, y, left.width, 34f), "RP_Unit_NoWings".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 36f;
            }
            y += 10f;
        }

        /// <summary>Where the care block began in the left column the last time it was drawn (the tour scrolls to it).</summary>
        public static float careBlockY;

        /// <summary>The care detail: how many guards are kept for care, who is on it (and who is named but cannot serve), the oldest care waiting, a place to name a guard, and the release.</summary>
        private static void DrawCare(Rect left, ref float y, Map map)
        {
            CareDetail care = CareDetail.Of(map);
            if (care == null) return;
            careBlockY = y;
            Header(left.x, ref y, left.width, "RP_DeployCare");
            TooltipHandler.TipRegion(new Rect(left.x, y - 32f, left.width, 30f), "RP_DeployCareTip".Translate());
            // (The queue is worked out for the tab only when something is kept for care: it is a pass over every inmate.)
            if (care.size > 0) care.QueueFor(map);
            var sizeRow = new Rect(left.x, y, left.width, 26f);
            Widgets.Label(new Rect(sizeRow.x, sizeRow.y + 3f, sizeRow.width - 80f, 22f), "RP_DeployCareSize".Translate(care.size));
            if (Widgets.ButtonText(new Rect(sizeRow.xMax - 70f, sizeRow.y, 30f, 24f), "-") && care.size > 0) care.SetSize(care.size - 1);
            if (Widgets.ButtonText(new Rect(sizeRow.xMax - 34f, sizeRow.y, 30f, 24f), "+") && care.size < CareDetail.MaxReserve) care.SetSize(care.size + 1);
            y += 28f;
            Text.Font = GameFont.Tiny;
            if (care.size == 0)
            {
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(left.x, y, left.width, 34f), "RP_DeployCareOff".Translate());
                y += 36f;
            }
            else
            {
                bool released = care.Released(map);
                GUI.color = PrisonHUD.Muted;
                string who = care.Members.Count == 0
                    ? "RP_CareMembersNone".Translate().ToString()
                    : "RP_CareMembers".Translate(string.Join(", ", care.Members.Select(m => care.IsPinned(m) ? "RP_CareMemberNamed".Translate(m.LabelShortCap).ToString() : m.LabelShortCap.ToString()))).ToString();
                var whoRect = new Rect(left.x, y, left.width, 34f);
                Widgets.Label(whoRect, who);
                y += 36f;
                if (released)
                {
                    GUI.color = PrisonHUD.Warn;
                    Widgets.Label(new Rect(left.x, y, left.width, 20f), "RP_CareReleasedNote".Translate());
                    y += 22f;
                }
                else if (care.Unavailable.Count > 0)
                {
                    GUI.color = PrisonHUD.Warn;
                    string gone = "RP_CareUnavailable".Translate(string.Join(", ", care.Unavailable.Select(u => "RP_CareUnavailableOne".Translate(u.pawn.LabelShortCap, u.why.Translate()).ToString()))).ToString();
                    Widgets.Label(new Rect(left.x, y, left.width, 34f), gone);
                    y += 36f;
                }
                // A place left empty: one guard stays outside the reserve whatever its size (muted, it is the rule), or there are not the guards for it (a warning).
                if (!released && care.Members.Count < care.size)
                {
                    GUI.color = care.Floored ? PrisonHUD.Muted : PrisonHUD.Warn;
                    Widgets.Label(new Rect(left.x, y, left.width, 34f), (care.Floored ? "RP_CareFloor" : "RP_CareShort").Translate(care.Members.Count, care.size));
                    y += 36f;
                }
                // Every guard on duty is in it (guards the warden named: the automatic pick keeps one out): nobody is left to answer the trouble (the warden's choice, but the tab says so).
                if (!released && care.Members.Count > 0 && care.OnDuty <= care.Members.Count)
                {
                    GUI.color = PrisonHUD.Bad;
                    Widgets.Label(new Rect(left.x, y, left.width, 20f), "RP_CareNoOneLeft".Translate());
                    y += 22f;
                }
                CareRequest oldest = care.OldestWaiting(out int ticks);
                var waitRect = new Rect(left.x, y, left.width, 20f);
                GUI.color = oldest == null ? PrisonHUD.Muted : PrisonHUD.Warn;
                Widgets.Label(waitRect, oldest == null
                    ? "RP_CareNothingWaiting".Translate().ToString()
                    : "RP_CareOldest".Translate(("RP_CareKind_" + oldest.kind).Translate(), oldest.inmate.LabelShort, (ticks / (float)GenDate.TicksPerHour).ToString("0.0")).ToString());
                TooltipHandler.TipRegion(waitRect, "RP_CareOldestTip".Translate());
                y += 22f;
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (care.size > 0)
            {
                var pin = new Rect(left.x, y, 150f, 24f);
                TooltipHandler.TipRegion(pin, "RP_CarePinTip".Translate());
                if (Widgets.ButtonText(pin, "RP_CarePin".Translate())) Find.WindowStack.Add(new FloatMenu(PinOptions(care, map)));
                var release = new Rect(pin.xMax + 8f, y, left.width - pin.width - 8f, 24f);
                TooltipHandler.TipRegion(release, "RP_CareReleaseTip".Translate());
                if (care.Released(map))
                {
                    if (Widgets.ButtonText(release, "RP_CareResume".Translate())) care.Resume(map);
                }
                else if (Widgets.ButtonText(release, "RP_CareRelease".Translate())) care.Release(map);
                y += 28f;
            }
            y += 10f;
        }

        /// <summary>The guards on the roster as a menu: a click names a guard for the reserve or takes the name off (one who cannot serve now says why).</summary>
        private static List<FloatMenuOption> PinOptions(CareDetail care, Map map)
        {
            var options = new List<FloatMenuOption>();
            foreach (Pawn g in StaffCommute.AllStaff(map).Where(p => p.GetComp<CompStaff>() is { role: { guardDuties: true, warden: false } }).OrderBy(p => p.thingIDNumber))
            {
                Pawn guard = g;
                bool pinned = care.IsPinned(guard);
                string label = (pinned ? "RP_CarePinMenuOff" : "RP_CarePinMenu").Translate(guard.LabelShortCap).ToString();
                if (CareDetail.Unfit(guard) is { } why) label = "RP_CarePinMenuWhy".Translate(label, why.Translate()).ToString();
                if (!pinned && care.pins.Count >= care.size) options.Add(new FloatMenuOption("RP_CarePinMenuWhy".Translate(label, "RP_CarePinFull".Translate()), null));
                else options.Add(new FloatMenuOption(label, () => care.TogglePin(guard)));
            }
            if (options.Count == 0) options.Add(new FloatMenuOption("RP_CarePinNone".Translate(), null));
            return options;
        }

        private static List<FloatMenuOption> HourOptions(System.Action<int> set)
        {
            var options = new List<FloatMenuOption>();
            for (int h = 0; h < 24; h++)
            {
                int hour = h;
                options.Add(new FloatMenuOption(GuardPosts.HourLabel(h), () => set(hour)));
            }
            return options;
        }

        /// <summary>The patrol list's scroll view: what is left of the column from <paramref name="top"/> down, and a view as tall as every guard's row (nobody is cut off, the list scrolls).</summary>
        public static (Rect outer, Rect view) PatrolView(Rect column, float top, int guards)
        {
            var outer = new Rect(column.x, top, column.width, Mathf.Max(0f, column.yMax - top));
            return (outer, new Rect(0f, 0f, outer.width - 16f, guards * PatrolRowHeight));
        }

        private static string Side(DoorSide s) => ("RP_DoorSide_" + s).Translate();

        /// <summary>Prison cells (rooms with a prisoner bed) per wing, in one pass; the zone's area is not a cell count.</summary>
        private static Dictionary<PrisonZoneDef, int> CellRooms(Map map, PrisonMap pm)
        {
            var rooms = new Dictionary<PrisonZoneDef, HashSet<Room>>();
            foreach (Building_Bed bed in map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>())
            {
                if (!bed.ForPrisoners || pm.zones.ZoneAt(bed.Position) is not { } wing || bed.GetRoom() is not { } r) continue;
                if (!rooms.TryGetValue(wing, out HashSet<Room> set)) rooms[wing] = set = new HashSet<Room>();
                set.Add(r);
            }
            return rooms.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }

        /// <summary>
        /// What is on each side of a door, by name: the room zone painted there (canteen, yard...), a cell, or outside.
        /// Null where nothing better than the access category is known.
        /// </summary>
        private static (string, string) Places(CompPrisonDoor d, PrisonMap pm)
        {
            Map map = d.parent.Map;
            IntVec3 p = d.parent.Position;
            // Vanilla's own door alignment: North means walls east and west, so the doorway runs north-south.
            bool northSouth = DoorUtility.DoorRotationAt(p, map, preferFences: false) == Rot4.North;
            IntVec3 a = northSouth ? p + IntVec3.North : p + IntVec3.East, b = northSouth ? p + IntVec3.South : p + IntVec3.West;
            string Place(IntVec3 c)
            {
                if (!c.InBounds(map)) return null;
                if (pm.zones.ZoneAt(c) is { security: null } zone) return zone.label; // a room zone, not a wing
                Room room = c.GetRoom(map);
                if (room == null) return null;
                if (room.IsPrisonCell) return "RP_DoorPlace_Cell".Translate();
                if (room.PsychologicallyOutdoors) return "RP_DoorPlace_Outside".Translate();
                return null;
            }
            return (Place(a), Place(b));
        }

        public static Color ModeColor(PrisonDoorMode m) => m switch
        {
            PrisonDoorMode.Locked => PrisonHUD.Bad,
            PrisonDoorMode.StaffOnly => new Color(0.45f, 0.65f, 0.95f),
            PrisonDoorMode.Inmates => PrisonHUD.Good,
            _ => Color.white,
        };

        private static void Header(float x, ref float y, float width, string key)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(x, y, width, 30f), key.Translate());
            Text.Font = GameFont.Small;
            y += 32f;
        }
    }
}
