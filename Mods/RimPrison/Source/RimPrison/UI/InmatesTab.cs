using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The inmate list, Prison Architect style: portrait, number, name, security, crime, sentence served, tension and mood
    /// bars, what they are known for, prison job and status icons. Every header sorts (click again to reverse); a search
    /// box and filter chips narrow it down; right-click a row for actions.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class InmatesTab
    {
        private enum Col { Number, Name, Security, Crime, Sentence, Tension, Mood, Want, Known, Job }

        private enum Flag { Tense, Suspected, Gang, NoJob, HungerStrike, Confined, Slipping }

        // x and width of each column at a 934 px wide table; a leading 30 px portrait.
        private static readonly (Col col, float x, float w, string key)[] Columns =
        {
            (Col.Number, 30f, 44f, "#"), (Col.Name, 76f, 112f, "RP_Col_Name"), (Col.Security, 190f, 80f, "RP_Col_Security"),
            (Col.Crime, 272f, 84f, "RP_Col_Crime"), (Col.Sentence, 358f, 92f, "RP_Col_Sentence"), (Col.Tension, 452f, 74f, "RP_Col_Tension"),
            (Col.Mood, 528f, 50f, "RP_Col_Mood"), (Col.Want, 580f, 112f, "RP_Col_Want"), (Col.Known, 694f, 112f, "RP_Col_KnownFor"), (Col.Job, 808f, 94f, "RP_Col_Job"),
        };

        /// <summary>Where the clickable part of a row ends (the job column is a button of its own).</summary>
        private const float RowClickWidth = 806f;

        private static Col sortBy = Col.Tension;
        private static bool descending = true;
        private static string search = "";
        private static readonly HashSet<SecurityLevelDef> hiddenLevels = new HashSet<SecurityLevelDef>();
        private static readonly HashSet<Flag> flags = new HashSet<Flag>();

        private static readonly Texture2D Padlock = ContentFinder<Texture2D>.Get("UI/Icons/Padlock", reportFailure: false);
        private static readonly Texture2D Cross = ContentFinder<Texture2D>.Get("UI/Icons/Cross", reportFailure: false);
        private static readonly Texture2D Fist = ContentFinder<Texture2D>.Get("UI/Icons/Fist", reportFailure: false);
        private static readonly Texture2D Bed = ContentFinder<Texture2D>.Get("UI/Icons/Bed", reportFailure: false);
        private static readonly Texture2D Star = ContentFinder<Texture2D>.Get("UI/Icons/Star", reportFailure: false);

        public static void Draw(Rect rect, Map map, ref Vector2 scroll)
        {
            List<Pawn> inmates = Filtered(map);
            float y = rect.y;
            DrawFilters(new Rect(rect.x, y, rect.width, 26f), map);
            y += 32f;
            Text.Font = GameFont.Small;
            // Headers
            foreach ((Col col, float x, float w, string key) in Columns)
            {
                var h = new Rect(rect.x + x, y, w, 24f);
                string label = key == "#" ? "#" : key.Translate().ToString();
                if (sortBy == col) label += descending ? " ▼" : " ▲";
                if (Mouse.IsOver(h)) Widgets.DrawHighlight(h);
                Widgets.Label(h, label);
                TooltipHandler.TipRegion(h, "RP_InmatesSortTip".Translate(key == "#" ? "RP_InmatesNumberLabel".Translate() : key.Translate()));
                if (Widgets.ButtonInvisible(h))
                {
                    if (sortBy == col) descending = !descending;
                    else
                    {
                        sortBy = col;
                        descending = col is Col.Tension or Col.Sentence or Col.Security or Col.Want;
                    }
                }
            }
            y += 26f;
            Widgets.DrawLineHorizontal(rect.x, y - 2f, rect.width);
            var outer = new Rect(rect.x, y, rect.width, rect.yMax - y);
            var view = new Rect(0f, 0f, outer.width - 16f, inmates.Count * 32f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float ry = 0f;
            for (int i = 0; i < inmates.Count; i++)
            {
                // Only rows in view are drawn (portraits are not free).
                // (The list is a few frames old: someone released or dead since has no row.)
                if (ry + 32f >= scroll.y && ry <= scroll.y + outer.height && inmates[i].Spawned && !inmates[i].Dead) DrawRow(new Rect(0f, ry, view.width, 30f), inmates[i], i);
                ry += 32f;
            }
            Widgets.EndScrollView();
        }

        // The list is filtered and sorted with LINQ, and a window asks for it on every GUI event (a layout, a repaint, each mouse event): kept for a few frames
        // unless the search, the chips or the sort moved. (Who is in it and in what order can lag a few frames; every row reads the pawn live.)
        private const int ListFrames = 10;
        private static readonly List<Pawn> listCache = new List<Pawn>();
        private static int listFrame = -ListFrames, filterVersion;
        private static int listVersion = -1;
        private static int listMapId = -1;
        private static string listSearch;
        private static Col listSort;
        private static bool listDescending;

        private static List<Pawn> Filtered(Map map)
        {
            if (listMapId == map.uniqueID && listVersion == filterVersion && listSearch == search && listSort == sortBy && listDescending == descending
                && hiddenLevels.Count + flags.Count == listChips && Time.frameCount - listFrame < ListFrames && Time.frameCount >= listFrame)
                return listCache;
            listMapId = map.uniqueID;
            listVersion = filterVersion;
            listSearch = search;
            listSort = sortBy;
            listDescending = descending;
            listChips = hiddenLevels.Count + flags.Count;
            listFrame = Time.frameCount;
            listCache.Clear();
            listCache.AddRange(Filter(map));
            return listCache;
        }

        private static int listChips;

        private static List<Pawn> Filter(Map map)
        {
            IEnumerable<Pawn> q = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate()?.security != null);
            if (!search.NullOrEmpty())
            {
                string s = search.ToLowerInvariant();
                q = q.Where(p => p.LabelShort.ToLowerInvariant().Contains(s) || p.Inmate().inmateNumber.ToString().Contains(s)
                                 || (p.Inmate().crime?.label.ToLowerInvariant().Contains(s) ?? false));
            }
            if (hiddenLevels.Count > 0) q = q.Where(p => !hiddenLevels.Contains(p.Inmate().security));
            foreach (Flag f in flags) q = q.Where(p => Has(p, p.Inmate(), f));
            q = sortBy switch
            {
                Col.Number => Order(q, p => p.Inmate().inmateNumber),
                Col.Name => descending ? q.OrderByDescending(p => p.LabelShort) : q.OrderBy(p => p.LabelShort),
                Col.Security => Order(q, p => p.Inmate().security.uiOrder),
                Col.Crime => descending ? q.OrderByDescending(p => p.Inmate().crime?.label ?? "") : q.OrderBy(p => p.Inmate().crime?.label ?? ""),
                Col.Sentence => Order(q, p => p.Inmate().IsLifer ? 1f : p.Inmate().DaysServed / Mathf.Max(1f, p.Inmate().TermDays)),
                Col.Mood => Order(q, p => p.needs.mood?.CurLevelPercentage ?? 0.5f),
                Col.Want => Order(q, p => WantOrder(p.Inmate())),
                Col.Known => Order(q, p => p.Inmate().reputations.Count(r => p.Inmate().KnownReputation(r)) + (p.Inmate().gangKnown && p.Inmate().gang != null ? 10 : 0)),
                Col.Job => descending ? q.OrderByDescending(p => p.Inmate().job?.label ?? "") : q.OrderBy(p => p.Inmate().job?.label ?? ""),
                _ => Order(q, p => p.Inmate().tension),
            };
            return q.ToList();
        }

        /// <summary>The goal the warden may know of (a secret one, or one that would name a gang not found yet, is no goal as far as any screen, sort or filter goes: <see cref="Wants.Shown"/>).</summary>
        internal static InmateWant ShownWant(CompInmate c) => Wants.Shown(c);

        /// <summary>Slipping goals first, then those on track, the lost, the fulfilled; nobody's last.</summary>
        internal static float WantOrder(CompInmate c) => ShownWant(c) is not { } w ? 0f : w.status switch
        {
            WantStatus.Slipping => 3f,
            WantStatus.OnTrack => 2f,
            WantStatus.Lost => 1f,
            _ => 0.5f,
        };

        /// <summary>The colour of a goal's state: on track, slipping, fulfilled, lost.</summary>
        internal static Color WantTone(WantStatus status) => status switch
        {
            WantStatus.OnTrack => PrisonHUD.Good,
            WantStatus.Slipping => PrisonHUD.Warn,
            WantStatus.Fulfilled => new Color(0.45f, 0.65f, 0.45f),
            _ => PrisonHUD.Muted,
        };

        private static IEnumerable<Pawn> Order(IEnumerable<Pawn> q, System.Func<Pawn, float> key) => descending ? q.OrderByDescending(key) : q.OrderBy(key);

        private static bool Has(Pawn p, CompInmate c, Flag f) => f switch
        {
            Flag.Tense => c.tension >= 60f,
            Flag.Suspected => c.suspected || c.cellSearchPending,
            Flag.Gang => c.gang != null && c.gangKnown,
            Flag.NoJob => c.job == null,
            Flag.HungerStrike => HungerStrike.Fasting(c),
            Flag.Slipping => ShownWant(c) is { status: WantStatus.Slipping },
            _ => c.forcedActivity != null || p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p),
        };

        private static void DrawFilters(Rect r, Map map)
        {
            var box = new Rect(r.x, r.y, 190f, r.height);
            search = Widgets.TextField(box, search);
            if (search.NullOrEmpty())
            {
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(new Rect(box.x + 6f, box.y + 3f, box.width, box.height), "RP_InmatesSearch".Translate());
                GUI.color = Color.white;
            }
            float x = box.xMax + 10f;
            foreach (SecurityLevelDef sec in DefDatabase<SecurityLevelDef>.AllDefsListForReading.OrderBy(s => s.uiOrder))
            {
                if (!Unlocks.IsUnlocked(sec)) continue;
                var chip = new Rect(x, r.y + 3f, 20f, 20f);
                bool on = !hiddenLevels.Contains(sec);
                Widgets.DrawBoxSolid(chip, on ? sec.color : new Color(sec.color.r, sec.color.g, sec.color.b, 0.2f));
                if (on) Widgets.DrawBox(chip);
                TooltipHandler.TipRegion(chip, "RP_InmatesLevelTip".Translate(sec.label));
                if (Widgets.ButtonInvisible(chip))
                {
                    if (on) hiddenLevels.Add(sec);
                    else hiddenLevels.Remove(sec);
                    filterVersion++;
                }
                x += 24f;
            }
            x += 8f;
            foreach (Flag f in System.Enum.GetValues(typeof(Flag)))
            {
                string label = ("RP_InmatesFilter_" + f).Translate();
                float w = Text.CalcSize(label).x + 14f;
                var chip = new Rect(x, r.y + 1f, w, r.height - 2f);
                bool on = flags.Contains(f);
                Widgets.DrawBoxSolid(chip, on ? new Color(0.3f, 0.45f, 0.6f) : new Color(0.18f, 0.19f, 0.21f));
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(chip, label);
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(chip, ("RP_InmatesFilterTip_" + f).Translate());
                if (Widgets.ButtonInvisible(chip))
                {
                    if (on) flags.Remove(f);
                    else flags.Add(f);
                    filterVersion++;
                }
                x += w + 4f;
            }
        }

        private static void DrawRow(Rect row, Pawn p, int index)
        {
            CompInmate c = p.Inmate();
            if (index % 2 == 1) Widgets.DrawLightHighlight(row);
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            GUI.DrawTexture(new Rect(row.x, row.y + 1f, 28f, 28f), PortraitsCache.Get(p, new Vector2(28f, 28f), Rot4.South, new Vector3(0f, 0f, 0.3f), 1.6f));
            float ty = row.y + 5f;
            foreach ((Col col, float x, float w, string _) in Columns)
            {
                var cell = new Rect(row.x + x, ty, w - 4f, 22f);
                switch (col)
                {
                    case Col.Number:
                        GUI.color = PrisonHUD.Muted;
                        Text.Font = GameFont.Tiny;
                        Widgets.Label(cell, c.inmateNumber.ToString("D5"));
                        Text.Font = GameFont.Small;
                        GUI.color = Color.white;
                        break;
                    case Col.Name:
                        Widgets.Label(cell, p.LabelShortCap.Truncate(cell.width));
                        break;
                    case Col.Security:
                        Widgets.DrawBoxSolid(new Rect(cell.x, cell.y + 2f, 6f, 16f), c.security.color);
                        Widgets.Label(new Rect(cell.x + 10f, cell.y, cell.width - 10f, cell.height), (c.security.labelShort ?? c.security.label).CapitalizeFirst());
                        break;
                    case Col.Crime:
                        Widgets.Label(cell, (c.crime?.LabelCap ?? "?").Truncate(cell.width));
                        break;
                    case Col.Sentence:
                        DrawSentence(cell, c);
                        break;
                    case Col.Tension:
                        Bar(cell, c.tension / 100f, Color.Lerp(PrisonHUD.Good, PrisonHUD.Bad, c.tension / 100f), c.tension.ToString("F0") + (c.suspected ? " !" : ""));
                        break;
                    case Col.Mood:
                        float mood = p.needs.mood?.CurLevelPercentage ?? 0.5f;
                        GUI.color = mood < 0.3f ? PrisonHUD.Bad : mood < 0.5f ? PrisonHUD.Warn : Color.white;
                        Widgets.Label(cell, mood.ToStringPercent());
                        GUI.color = Color.white;
                        break;
                    case Col.Want:
                        DrawWant(cell, p, c);
                        break;
                    case Col.Known:
                        DrawKnown(cell, c);
                        break;
                    case Col.Job:
                        if (Widgets.ButtonText(new Rect(cell.x, row.y + 3f, cell.width, 24f), c.job?.LabelCap ?? "RP_NoJob".Translate(), drawBackground: false))
                            Find.WindowStack.Add(new FloatMenu(JobOptions(c)));
                        break;
                }
            }
            DrawStatus(new Rect(row.x + 904f, row.y + 5f, 30f, 20f), p, c);
            var clickArea = new Rect(row.x, row.y, RowClickWidth, row.height);
            // (The inspect text is the row's tooltip everywhere but over the want column, which has its own.)
            (_, float wantX, float wantW, _) = System.Array.Find(Columns, col => col.col == Col.Want);
            TooltipHandler.TipRegion(new Rect(clickArea.x, clickArea.y, wantX, clickArea.height), () => c.CompInspectStringExtra() ?? "", p.thingIDNumber);
            TooltipHandler.TipRegion(new Rect(clickArea.x + wantX + wantW, clickArea.y, clickArea.width - wantX - wantW, clickArea.height), () => c.CompInspectStringExtra() ?? "", p.thingIDNumber + 1_000_000);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && Mouse.IsOver(row))
            {
                Find.WindowStack.Add(new FloatMenu(Actions(p, c)));
                Event.current.Use();
            }
            else if (Widgets.ButtonInvisible(clickArea)) CameraJumper.TryJumpAndSelect(p);
        }

        /// <summary>The goal as a bar in its state's colour (the title on it); a dash for none, or one the warden does not know of. The tooltip says how it stands.</summary>
        private static void DrawWant(Rect cell, Pawn p, CompInmate c)
        {
            if (ShownWant(c) is not { } want)
            {
                GUI.color = PrisonHUD.Muted;
                Widgets.Label(cell, "–");
                GUI.color = Color.white;
                return;
            }
            Bar(cell, want.progress, WantTone(want.status), Wants.Title(want).Truncate(cell.width - 6f));
            TooltipHandler.TipRegion(cell, () => $"{Wants.Title(want)} - {Wants.StatusLabel(want)}\n\n{(want.Resolved ? want.def.description : want.def.Worker.Detail(p, c, want).Resolve())}", 8_420_000 + p.thingIDNumber);
        }

        private static void DrawSentence(Rect cell, CompInmate c)
        {
            if (DeathRowSystem.Ext(c) != null || c.IsLifer)
            {
                GUI.color = new Color(0.75f, 0.25f, 0.3f);
                Widgets.Label(cell, DeathRowSystem.Ext(c) != null ? "RP_DeathRowShort".Translate().ToString().ToUpperInvariant() : "RP_InmatesLife".Translate().ToString());
                GUI.color = Color.white;
                return;
            }
            float frac = Mathf.Clamp01(c.DaysServed / Mathf.Max(1f, c.TermDays));
            Bar(cell, frac, new Color(0.35f, 0.55f, 0.75f), "RP_InmatesServedBar".Translate(c.DaysServed.ToString("F0"), c.TermDays));
        }

        private static void Bar(Rect cell, float frac, Color color, string text)
        {
            var bar = new Rect(cell.x, cell.y + 3f, cell.width - 2f, 15f);
            Widgets.DrawBoxSolid(bar, new Color(0.12f, 0.12f, 0.14f));
            Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(frac), bar.height), color);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(bar, text);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        /// <summary>Known gang first (in its colour), then known reputations, as small chips.</summary>
        private static void DrawKnown(Rect cell, CompInmate c)
        {
            float x = cell.x;
            Text.Font = GameFont.Tiny;
            void Chip(string label, Color color)
            {
                float w = Mathf.Min(Text.CalcSize(label).x + 8f, cell.xMax - x);
                if (w < 12f) return;
                var r = new Rect(x, cell.y + 2f, w, 17f);
                Widgets.DrawBoxSolidWithOutline(r, new Color(color.r * 0.3f, color.g * 0.3f, color.b * 0.3f), color);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(r, label.Truncate(w - 2f));
                Text.Anchor = TextAnchor.UpperLeft;
                x += w + 3f;
            }
            if (c.gang != null && c.gangKnown) Chip(c.gang.label + GangRanks.ChipSuffix(c), c.gang.color);
            else if (c.gang == null && GangExit.ExActive(c)) Chip("RP_FileExMember".Translate(c.exGang.label), c.exGang.color);
            foreach (string rep in c.ReputationsText().Split(new[] { ", " }, System.StringSplitOptions.RemoveEmptyEntries)) Chip(rep, new Color(0.7f, 0.7f, 0.75f));
            Text.Font = GameFont.Small;
        }

        private static void DrawStatus(Rect r, Pawn p, CompInmate c)
        {
            Texture2D icon = null;
            string tip = null;
            if (HungerStrike.Fasting(c)) (icon, tip) = (Fist, "RP_InmatesStatus_HungerStrike".Translate());
            else if (c.forcedActivity != null) (icon, tip) = (Padlock, "RP_InmatesStatus_Confined".Translate(c.forcedActivity.label));
            else if (p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p)) (icon, tip) = (Cross, "RP_InmatesStatus_Infirmary".Translate());
            else if (c.awaitingSearch || !c.processed) (icon, tip) = (Bed, "RP_InmatesStatus_Holding".Translate());
            else if (c.gangLeader && c.gangKnown) (icon, tip) = (Star, "RP_InmatesStatus_Leader".Translate());
            if (icon == null) return;
            GUI.DrawTexture(new Rect(r.x, r.y, 20f, 20f), icon);
            TooltipHandler.TipRegion(r, tip);
        }

        private static List<FloatMenuOption> Actions(Pawn p, CompInmate c)
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("RP_InmatesOpenFile".Translate(), () =>
                {
                    CameraJumper.TryJumpAndSelect(p);
                    Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                    if (Find.MainTabsRoot.OpenTab != MainButtonDefOf.Inspect) Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, playSound: false);
                    if (Find.WindowStack.WindowOfType<MainTabWindow_Inspect>() is { } inspect) inspect.OpenTabType = typeof(ITab_InmateFile);
                }),
            };
            // (Released, paroled and transferred inmates waiting for the shuttle are not the prison's to command: the file only.)
            if (ShuttleSystem.OnTheWayOut(p)) return options;
            SecurityLevelDef pc = RPSecurityDefOf.RP_Protective;
            if (pc != null && c.security != pc && DeathRowSystem.Ext(c) == null)
                options.Add(Unlocks.IsUnlocked(pc) && InmateUtility.HasRoomIn(p, pc)
                    ? new FloatMenuOption("RP_ToProtective".Translate(), () => InmateUtility.ChangeSecurity(p, c, pc))
                    : new FloatMenuOption("RP_ToProtective".Translate() + " (" + InmateUtility.NoRoomText(p, pc) + ")", null));
            if (DeathRowSystem.Ext(c) == null && SupermaxSystem.Level is { } supermax && c.security != supermax && Unlocks.IsUnlocked(supermax))
                options.Add(SupermaxSystem.CannotSend(p, c) is { } why
                    ? new FloatMenuOption("RP_ToSupermax".Translate() + " (" + why + ")", null)
                    : new FloatMenuOption("RP_ToSupermax".Translate(), () => SupermaxSystem.TrySend(p, c, SupermaxReason.Warden)));
            if (DeathRowSystem.Ext(c) == null)
                options.Add(new FloatMenuOption("RP_TransferOut".Translate(Money.Format(InmateUtility.TransferCost)), () => InmateUtility.TransferOut(p, c)));
            if (DeathRowSystem.Ext(c) == null)
                options.Add(new FloatMenuOption("RP_InmatesMoveCell".Translate(), () => Find.WindowStack.Add(new FloatMenu(Cellmates.MoveOptions(p, c)))));
            options.Add(new FloatMenuOption("RP_InmatesSetJob".Translate(), () => Find.WindowStack.Add(new FloatMenu(JobOptions(c)))));
            options.Add(new FloatMenuOption("RP_InmatesRefer".Translate(), () => Find.WindowStack.Add(new FloatMenu(ReferOptions(p, c)))));
            return options;
        }

        /// <summary>Refer to a program they have not passed (first seat at the next session); or withdraw a referral.</summary>
        private static List<FloatMenuOption> ReferOptions(Pawn p, CompInmate c)
        {
            var options = new List<FloatMenuOption>();
            if (c.referral != null)
                options.Add(new FloatMenuOption("RP_ReferWithdraw".Translate(c.referral.label), () => c.referral = null));
            foreach (ProgramDef prog in DefDatabase<ProgramDef>.AllDefsListForReading.OrderBy(x => x.uiOrder))
            {
                if (c.passedPrograms.Contains(prog) || c.referral == prog) continue;
                if (!prog.Suitable(p, c))
                {
                    options.Add(new FloatMenuOption(prog.LabelCap + " (" + "RP_ReferNotSuitable".Translate() + ")", null));
                    continue;
                }
                bool running = PrisonGame.Instance.programs.IsRunning(prog);
                string label = prog.LabelCap.ToString() + (running ? "" : " (" + "RP_ReferNotRunning".Translate().ToString() + ")");
                options.Add(new FloatMenuOption(label, () =>
                {
                    c.referral = prog;
                    Messages.Message("RP_Referred".Translate(p.LabelShort, prog.label), p, MessageTypeDefOf.NeutralEvent, historical: false);
                }));
            }
            return options;
        }

        /// <summary>"2/3 filled": how many inmates hold the job and how many places the prison has for it (a mailroom without a door an inmate may use has none).</summary>
        internal static string JobSlots(InmateJobDef job, Map map)
        {
            int filled = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate()?.job == job) filled++;
            return "RP_JobSlots".Translate(filled, job.Worker.Capacity(map)).Resolve();
        }

        private static List<FloatMenuOption> JobOptions(CompInmate c)
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("RP_NoJob".Translate(), () => { c.job = null; c.jobLocked = true; }),
                new FloatMenuOption("RP_JobAuto".Translate(), () => c.jobLocked = false),
            };
            foreach (InmateJobDef job in DefDatabase<InmateJobDef>.AllDefsListForReading)
            {
                InmateJobDef j = job;
                bool trained = j.requiredProgram == null || c.passedPrograms.Contains(j.requiredProgram);
                bool allowed = j.allowedSecurity.Count == 0 || j.allowedSecurity.Contains(c.security);
                if (!trained || !allowed)
                    options.Add(new FloatMenuOption(j.LabelCap + " (" + (!allowed ? "RP_JobNotForSecurity".Translate() : "RP_JobNeedsProgram".Translate(j.requiredProgram.label)) + ")", null));
                else
                    options.Add(new FloatMenuOption(j.LabelCap + " (" + JobSlots(j, c.Pawn.Map) + ")", () => { c.job = j; c.jobLocked = true; }));
            }
            return options;
        }
    }
}
