using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The morning after (Security/RiotReview.cs): the headless proof. Every text; a riot of four in the canteen with one camera that sees two of them (the other two in the dark), real ticks while it runs, the damage
    /// bill from a rioter's blow and the staff and inmates hurt, the riot's end, the hour's quiet that closes the record and the next 06:00's one letter that names only the two seen; "charge" through the letter
    /// (two cases on the adjudication book) and its second step; a restriction of two days that keeps the yard shut on days 1 and 2 and not on day 3, holds the wing down only for the days served, and whose tension
    /// lands after it (lifting one of its orders ends it; a second shower left alone is no restriction); the answers (two at most, each once, amnesty alone, the negotiated menu, ship out only for a seen ringleader,
    /// restitution out of cash, no spokesperson nobody saw); charge against the riot's own records (nobody sanctioned or on the sheet for the riot is charged again, with the policy and with the sheet) and a refused
    /// choice asked again; two riots on a map, a joiner, a rioter of no riot, a blow from staff as a sighting; one budgeted letter a riot; the book's last ten. Self-contained, on throwaway inmates and a book of its
    /// own (the prison's watchers, the glow, the regime's orders, the demand and the adjudication book set aside and put back). The round trip of a record half answered is in the saveload plan
    /// (<see cref="SeedRiotReviewForReload"/>). Premises the live prison may not give (room in the canteen, a yard hour) are told and not failed.
    /// </summary>
    public static partial class AutoTest
    {
        private static void RiotReviewFail(string check, string what) => problems0.Add($"riot review ({check}): {what}");

        private static void CheckRiotReview(Map map)
        {
            CheckRiotReviewTexts(map);
            CheckRiotReviewScene(map);
            CheckRiotReviewRestrict(map);
            CheckRiotReviewAnswers(map);
            CheckRiotReviewCharge(map);
            CheckRiotReviewRecords(map);
            CheckRiotReviewBudget(map);
            CheckRiotReviewBook(map);
        }

        // ---- helpers ------------------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>A riot book of the check's own (the game's put back), the adjudication cases and the demand as they were.</summary>
        private static void RiotSetAside(PrisonGame g, Rollback r)
        {
            RiotBook book0 = g.riots;
            g.riots = new RiotBook();
            List<AdjudicationCase> cases0 = g.adjudication.cases.ToList();
            InmateDemand demand0 = g.demand;
            g.demand = new InmateDemand();
            bool adjudicate0 = g.adjudicate;
            g.adjudicate = false;
            // (A promise to the inmates ends a hunger strike: the live inmates' are put back.)
            List<(CompInmate c, int until)> strikes0 = Find.Maps.SelectMany(m => m.mapPawns.PrisonersOfColony).Where(x => x.Inmate() != null).Select(x => (x.Inmate(), x.Inmate().hungerStrikeUntilTick)).ToList();
            r.Add(() =>
            {
                g.riots = book0;
                g.adjudication.cases.RemoveAll(k => !cases0.Contains(k));
                g.demand = demand0;
                g.adjudicate = adjudicate0;
                foreach ((CompInmate c, int until) in strikes0) c.hungerStrikeUntilTick = until;
            });
        }

        /// <summary>A throwaway inmate who can riot (violence is not beneath them), tense, on the spot.</summary>
        private static Pawn RiotRioter(Map map, Rollback r, IntVec3 at, SecurityLevelDef level = null)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Pawn p = Throwaway(map, r, level);
                if (p.WorkTagIsDisabled(WorkTags.Violent))
                {
                    p.Destroy(DestroyMode.Vanish);
                    continue;
                }
                r.Add(() => p.ownership?.UnclaimAll());
                p.pather?.StopDead();
                p.jobs?.StopAll();
                p.Position = at;
                p.Notify_Teleported();
                p.Inmate().tension = 80f;
                return p;
            }
            return null;
        }

        /// <summary>A record of a riot that is over, made by hand: the ringleader and the others, each seen or not (the ringleader first).</summary>
        private static RiotRecord RiotRecordOf(PrisonGame g, Map map, SecurityLevelDef level, params (Pawn p, bool seen)[] who)
        {
            int now = Find.TickManager.TicksGame;
            var rec = new RiotRecord
            {
                id = g.riots.nextId++, mapId = map.uniqueID, level = level, startTick = now - 3 * GenDate.TicksPerHour, lastRiotTick = now - 2 * GenDate.TicksPerHour, endTick = now - GenDate.TicksPerHour,
                ringleaderId = who.Length > 0 ? who[0].p.thingIDNumber : -1, ringleaderName = who.Length > 0 ? who[0].p.LabelShort : null, grievance = NeedDefOf.Food, letterTick = now,
            };
            foreach ((Pawn p, bool seen) in who)
            {
                rec.participants.Add(new RiotParticipant { id = p.thingIDNumber, name = p.LabelShort, seen = seen });
                // (Inside before the riot: a throwaway is admitted as it is made, and the riot of another stay is not theirs.)
                if (p.Inmate() is { } c && c.admittedTick > rec.startTick) c.admittedTick = rec.startTick - 1;
            }
            g.riots.records.Add(rec);
            return rec;
        }

        private static bool NamedIn(string text, string name) => Regex.IsMatch(text, @"(^|[^\p{L}])" + Regex.Escape(name) + @"($|[^\p{L}])");

        // ---- the texts ---------------------------------------------------------------------------------------------------------------------------------------------------------

        private static readonly string[] riotReviewKeys =
        {
            "RP_RiotReviewLabel", "RP_RiotReviewText", "RP_RiotReviewAnswers", "RP_RiotReviewMoreLabel", "RP_RiotReviewMoreText", "RP_RiotReport_Ringleader", "RP_RiotReport_RingleaderUnseen", "RP_RiotReport_Joined",
            "RP_RiotReport_Others", "RP_RiotReport_OthersOnly", "RP_RiotReport_NobodyElse", "RP_RiotReport_Hurt", "RP_RiotReport_Dead", "RP_RiotReport_Damage", "RP_RiotReport_NoDamage", "RP_RiotReport_Yesterday",
            "RP_RiotReport_GrievanceReal", "RP_RiotReport_GrievanceNot", "RP_RiotReport_Negotiated", "RP_RiotReport_Lacked", "RP_RiotLacked_NoGuard", "RP_RiotLacked_GuardFar", "RP_RiotLacked_Dark", "RP_RiotLacked_Roster",
            "RP_RiotReview_Restrict1", "RP_RiotReview_Restrict2", "RP_RiotReview_Restrict3", "RP_RiotReview_ShipOut", "RP_RiotReview_Charge", "RP_RiotReview_Restitution", "RP_RiotReview_Grievance", "RP_RiotReview_Amnesty",
            "RP_RiotReview_None", "RP_RiotReview_Enough", "RP_RiotRefused_Done", "RP_RiotRefused_Given", "RP_RiotRefused_AmnestyAlone", "RP_RiotRefused_NoWing", "RP_RiotRefused_Restricted", "RP_RiotRefused_RingleaderUnseen",
            "RP_RiotRefused_RingleaderGone", "RP_RiotRefused_NobodySeen", "RP_RiotRefused_NothingBroken", "RP_RiotRefused_Negotiated", "RP_RiotRefused_NoGrievance", "RP_RiotAnswerNotGiven", "RP_Cause_RiotRingleader",
            "RP_RiotCharged", "RP_RiotRestitution", "RP_RiotRestitutionNote", "RP_RiotRestrictOver", "RP_Chr_RiotOver", "RP_Chr_RiotRestrict", "RP_Chr_RestrictionGoesOn", "RP_Chr_RiotRestrictOver", "RP_Chr_RiotCharged",
            "RP_Chr_RiotRestitution", "RP_Chr_RiotGrievance", "RP_Chr_RiotAmnesty", "RP_RiotAnswered_Restrict", "RP_RiotAnswered_ShipOut", "RP_RiotAnswered_Charge", "RP_RiotAnswered_Restitution", "RP_RiotAnswered_Grievance",
            "RP_RiotAnswered_Amnesty", "RP_RiotAnswered_None", "RP_RiotState_Open", "RP_RiotState_Due", "RP_RiotState_Waiting", "RP_RiotState_NoReckoning", "RP_RiotState_MoreToCome", "RP_RiotState_Negotiated", "RP_IntelRiots",
            "RP_IntelHint_Riots", "RP_IntelRiotLine", "RP_IntelRiotWho", "RP_IntelRiotWhoUnseen", "RP_IntelTip_Riot",
            "RP_RiotReviewAgainLabel", "RP_RiotReviewAgainText", "RP_RiotReviewPromiseOpen", "RP_RiotReport_DeadAndUnseen", "RP_RiotReport_DeadUnseen", "RP_RiotRefused_DealtWith", "RP_RiotRefused_Changed",
            "RP_RiotReport_Rumour",
        };

        private static void CheckRiotReviewTexts(Map map)
        {
            Guarded("riot review: the decisions and every text", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RiotSetAside(g, r);
                foreach (string key in riotReviewKeys)
                {
                    if (!key.CanTranslate()) RiotReviewFail("texts", $"no keyed string {key}");
                    // (RimWorld capitalises the letter after ": " in a translated text: these use a dash or a comma.)
                    else if (Regex.IsMatch(key.Translate().RawText, @": \p{Ll}")) RiotReviewFail("texts", $"{key} has a colon before a word RimWorld would capitalise");
                }
                PrisonDecisionDef first = DefDatabase<PrisonDecisionDef>.GetNamedSilentFail(RiotReview.DecisionDef), more = DefDatabase<PrisonDecisionDef>.GetNamedSilentFail(RiotReview.MoreDecisionDef);
                // (The morning letter is budgeted: not urgent, and an answer time over the hours under which the budget lets a letter through. Its second step is the same letter's: never held back.)
                if (first == null || more == null || first.urgent || !more.urgent || RiotReview.AnswerHours <= StoryLetters.UrgentHours)
                    RiotReviewFail("decisions", $"the morning letter is not budgeted or its second step is (first {first?.urgent}, second {more?.urgent}, {RiotReview.AnswerHours} hours against {StoryLetters.UrgentHours})");
                if (RPRiotReviewDefOf.RP_Restitution is not { income: true }) RiotReviewFail("decisions", "the ledger category for restitution is missing or is not income");
                // Every line of the report, the list and the states read with nothing unfilled, for a record of each kind.
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                foreach (bool seen in new[] { true, false })
                {
                    var rec = new RiotRecord
                    {
                        id = 1, mapId = map.uniqueID, level = level, startTick = Find.TickManager.TicksGame - 5000, lastRiotTick = Find.TickManager.TicksGame - 2000, ringleaderId = 1, ringleaderName = "Zlead", grievance = NeedDefOf.Food,
                        grievanceReal = seen, needPercent = 22, missedMeals = 3, lockedHours = 8, nearestGuard = seen ? 14 : -1, dark = seen, guardsRostered = 2, wingInmates = 9, negotiated = seen, damage = seen ? 420f : 0f,
                        deaths = seen ? new List<string> { "Zdead" } : new List<string>(), deadIds = seen ? new List<int> { 7, 8 } : new List<int> { 9 }, staffHurt = new List<int> { 5 },
                    };
                    // (One with a promise open: the letter says meeting the grievance replaces it.)
                    g.demand = seen ? new InmateDemand() : new InmateDemand { need = NeedDefOf.Rest, deadlineTick = Find.TickManager.TicksGame + 1000 };
                    rec.participants.Add(new RiotParticipant { id = 1, name = "Zlead", seen = seen });
                    rec.participants.Add(new RiotParticipant { id = 2, name = "Zseen", seen = true });
                    rec.participants.Add(new RiotParticipant { id = 3, name = "Zunseen", seen = false });
                    // (One with the rumour the wing was telling: the report says it.)
                    if (seen) rec.rumour = new Rumour { kind = RumourKind.Supermax, level = level, isTrue = false, distorted = true, reach = 0.5f, startTick = 1, expireTick = 2 };
                    var texts = new List<string> { RiotReview.LetterText(rec, map), RiotReview.ListLine(rec, map), RiotReview.StateText(rec) };
                    string talk = "RP_Rumour_Supermax_False".Translate("?").Resolve();
                    if (texts[0].Contains(talk) != seen) RiotReviewFail("texts", $"the report {(seen ? "does not give" : "gives")} the rumour the wing was telling (\"{talk}\", one kept {seen}): {texts[0]}");
                    foreach (RiotAnswer a in System.Enum.GetValues(typeof(RiotAnswer))) texts.Add(RiotReview.AnswerText(rec, a));
                    rec.answers.Add(RiotAnswer.Charge);
                    texts.Add(RiotReview.StateText(rec));
                    rec.endTick = 1;
                    texts.Add(RiotReview.StateText(rec));
                    foreach (string t in texts)
                        if (t.Contains("{") || t.Contains("RP_") || t.Contains("Translation error")) RiotReviewFail("texts", $"a text has something unfilled in it: {t}");
                    if (NamedIn(texts[0], "Zunseen") || NamedIn(texts[0], "Zlead") != seen || !NamedIn(texts[0], "Zseen"))
                        RiotReviewFail("texts", $"the report does not name exactly who was seen (ringleader seen {seen}): {texts[0]}");
                    // (The dead: the named and a count of the rioters nobody saw - one beside "Zdead", or one alone.)
                    string dead = seen ? "RP_RiotReport_DeadAndUnseen".Translate("Zdead", 1).Resolve() : "RP_RiotReport_DeadUnseen".Translate(1).Resolve();
                    string promise = "RP_RiotReviewPromiseOpen".Translate(NeedDefOf.Rest.label).Resolve();
                    if (!texts[0].Contains(dead) || texts[0].Contains(promise) == seen)
                        RiotReviewFail("texts", $"the report does not give the dead as \"{dead}\", or the open promise wrongly (promise open {!seen}): {texts[0]}");
                }
            });
        }

        // ---- the headless proof: a riot of four, a camera on two -----------------------------------------------------------------------------------------------------------------

        private static void CheckRiotReviewScene(Map map)
        {
            Guarded("riot review: a riot of four, a camera on two, the morning's one letter", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                RiotSetAside(g, r);
                // (What watches the prison is put aside: the scene's camera is the only watcher. The glow is the check's to say.)
                List<Pawn> guards0 = pm.Guards.ToList();
                List<CompSecurityCamera> cameras0 = pm.Cameras.ToList();
                List<CompGuardTower> towers0 = pm.Towers.ToList();
                r.Add(() =>
                {
                    pm.Guards.Clear();
                    pm.Guards.AddRange(guards0);
                    pm.Cameras.Clear();
                    pm.Cameras.AddRange(cameras0);
                    pm.Towers.Clear();
                    pm.Towers.AddRange(towers0);
                });
                pm.Guards.Clear();
                pm.Cameras.Clear();
                pm.Towers.Clear();
                TensionUtility.TestGlow = new Dictionary<IntVec3, float>();
                r.Add(() => TensionUtility.TestGlow = null);
                bool? roll0 = HostageCrisis.ForcedRoll;
                HostageCrisis.ForcedRoll = false;
                (int lastRiot0, int drama0) = (g.story.lastRiotTick, g.story.dramaToday);
                r.Add(() => (HostageCrisis.ForcedRoll, g.story.lastRiotTick, g.story.dramaToday) = (roll0, lastRiot0, drama0));
                // (A riot is loud: the sanction policy deals with every rioter as it starts, and "charge" skips anyone it dealt with. Here the policy gives nothing for a riot, so the two seen are the charge's.)
                PunishmentPolicy policy0 = g.punishments;
                r.Add(() => g.punishments = policy0);
                g.punishments = new PunishmentPolicy();
                g.punishments.Set(RPRiotDefOf.RP_Riot, 0);
                g.punishments.Set(RPRiotDefOf.RP_RiotJoined, 0);
                // (The real inmates are calm, so that none is pulled into the riot here.)
                foreach (Pawn calm in map.mapPawns.PrisonersOfColonySpawned)
                    if (calm.Inmate() is { } cc) cc.tension = 0f;
                IntVec3 centre = PrisonBuilder.CanteenCell(map);
                Room room = centre.InBounds(map) ? centre.GetRoom(map) : null;
                List<IntVec3> spots = room == null ? new List<IntVec3>() : GenRadial.RadialCellsAround(centre, 4f, true)
                    .Where(c => c.InBounds(map) && c.Standable(map) && c.GetRoom(map) == room && c.GetEdifice(map) == null && c.GetDoor(map) == null && TensionUtility.DarkMatters(c, map, pm)).ToList();
                if (spots.Count < 5)
                {
                    RPLog.Message($"[RimPrison:test] riot review scene: not run (the canteen has {spots.Count} free cells near its centre, 5 wanted)");
                    return;
                }
                var crew = new List<Pawn>();
                for (int i = 0; i < 4; i++)
                    if (RiotRioter(map, r, spots[i]) is { } p) crew.Add(p);
                if (crew.Count < 4)
                {
                    RPLog.Message("[RimPrison:test] riot review scene: not run (no four throwaways who can riot)");
                    return;
                }
                CompSecurityCamera camera = SpawnDarkCamera(map, r, spots[4]);
                pm.Cameras.Clear();
                pm.Cameras.Add(camera);
                // The ringleader and the last stand in the dark (a camera sees nothing there), the two between in the light.
                bool[] lit = { false, true, true, false };
                for (int i = 0; i < 4; i++) TensionUtility.TestGlow[crew[i].Position] = lit[i] ? 1f : 0f;
                bool premise = Enumerable.Range(0, 4).All(i => TensionUtility.Watched(crew[i], pm) == lit[i]);
                if (!premise)
                {
                    RPLog.Message($"[RimPrison:test] riot review scene: not run (the camera does not see the two lit and miss the two dark: {string.Join(",", crew.Select(x => TensionUtility.Watched(x, pm)))})");
                    return;
                }
                Pawn lead = crew[0];
                int cases0 = g.adjudication.cases.Count;
                bool started = RPRiotDefOf.RP_Riot.Worker.TryExecute(lead, lead.Inmate());
                RiotRecord rec = g.riots.records.LastOrDefault();
                if (!started || rec == null)
                {
                    RiotReviewFail("open", $"the riot did not start or opened no record (started {started}, records {g.riots.records.Count})");
                    return;
                }
                string seenNow = string.Join(",", crew.Select(x => rec.Of(x.thingIDNumber)?.seen.ToString() ?? "absent"));
                if (rec.ringleaderId != lead.thingIDNumber || rec.participants.Count != 4 || crew.Any(x => rec.Of(x.thingIDNumber) == null) || Enumerable.Range(0, 4).Any(i => rec.Of(crew[i].thingIDNumber)?.seen != lit[i])
                    || !rec.Open || rec.level != lead.Inmate().security || rec.grievance == null || !rec.dark || rec.nearestGuard != -1)
                    RiotReviewFail("open", $"the record at the start is not the four with the two lit ones seen, the ringleader in the dark and no guard ({rec.Describe()}; seen {seenNow})");
                // Nothing watches from here on: the camera is off and every cell around is dark (a guard sees three cells in the dark).
                pm.Cameras.Clear();
                foreach (IntVec3 c in GenRadial.RadialCellsAround(centre, 16f, true)) TensionUtility.TestGlow[c] = 0f;
                // (The seen rule after the start: the two in the dark, sampled again with nothing watching, stay unseen.)
                RiotReview.Notify_Rioting(crew[0]);
                RiotReview.Notify_Rioting(crew[3]);
                if (rec.Of(crew[0].thingIDNumber)?.seen != false || rec.Of(crew[3].thingIDNumber)?.seen != false)
                    RiotReviewFail("seen", $"a rioter in the dark, sampled with nothing watching, was marked seen ({rec.Describe()})");
                // The damage bill: a rioter smashes the camera. A guard takes a blow from one (seen already), and gives one back to the other seen one (a blow from staff is a sighting: the two in the dark stay out of it).
                int hp0 = camera.parent.HitPoints;
                camera.parent.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 20f, instigator: crew[1]));
                if (rec.damage <= 0f || camera.parent.HitPoints >= hp0) RiotReviewFail("damage", $"a rioter's blow on the camera ({hp0} -> {camera.parent.HitPoints} hit points) put {rec.damage:F1} on the bill");
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                guard.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 5f, armorPenetration: 2f, instigator: crew[2]));
                crew[1].TakeDamage(new DamageInfo(DamageDefOf.Blunt, 4f, armorPenetration: 2f, instigator: guard));
                if (!rec.staffHurt.Contains(guard.thingIDNumber) || !rec.inmatesHurt.Contains(crew[1].thingIDNumber))
                    RiotReviewFail("hurt", $"a guard hit by a rioter and a rioter hit by the guard are not on the record (staff {string.Join("/", rec.staffHurt)}, inmates {string.Join("/", rec.inmatesHurt)}, guard {guard.thingIDNumber}, rioter {crew[1].thingIDNumber})");
                // (A guard's stray blow on property is not the riot's bill.)
                float bill0 = rec.damage;
                if (!camera.parent.Destroyed) camera.parent.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 3f, instigator: guard));
                if (Mathf.Abs(rec.damage - bill0) > 0.01f) RiotReviewFail("damage", $"a guard's blow on the camera went on the riot's bill ({bill0:F1} -> {rec.damage:F1})");
                // (The guard goes before the riot runs: the rioters would go for them.)
                guard.Destroy(DestroyMode.Vanish);
                // Real ticks while the riot runs: the record's clock follows the rioters' own ticks.
                int t0 = Find.TickManager.TicksGame;
                for (int i = 0; i < 300; i++) Find.TickManager.DoSingleTick();
                bool stillRioting = crew.Any(x => !x.Dead && x.MentalStateDef == RPMentalStateDefOf.RP_Rioting);
                if (stillRioting && (rec.lastRiotTick <= t0 || !rec.Open))
                    RiotReviewFail("ticks", $"after 300 ticks of rioting the record's clock is at {rec.lastRiotTick} (from {t0}), open {rec.Open}");
                else if (!stillRioting) RPLog.Message("[RimPrison:test] riot review scene: nobody was still rioting after 300 ticks (the live prison put them down); the clock is not judged");
                // (Nothing watches in the dark but a guard three cells off: a live guard who came that close saw one of the two in the dark. That is told, and the letter is judged against the record.)
                if (rec.Of(crew[0].thingIDNumber)?.seen == true || rec.Of(crew[3].thingIDNumber)?.seen == true)
                    RPLog.Message("[RimPrison:test] riot review scene: a live guard saw one of the two in the dark during the ticks; the letter is judged against the record");
                // The riot ends: each rioter stands down (as the guards would put them down).
                foreach (Pawn p in crew)
                {
                    if (p.MentalStateDef == RPMentalStateDefOf.RP_Rioting) p.MentalState.RecoverFromState();
                    p.Inmate().tension = 0f;
                }
                for (int i = 0; i < 60; i++) Find.TickManager.DoSingleTick();
                int ended = rec.lastRiotTick;
                // Quiet for less than an hour: still open; an hour: closed, and no letter before 06:00; then the morning's one letter, once.
                RiotReview.Hourly(g, map, 3, ended + RiotReview.CloseAfterTicks - 10);
                bool openBefore = rec.Open;
                RiotReview.Hourly(g, map, 3, ended + RiotReview.CloseAfterTicks + 10);
                List<ChoiceLetter_PrisonDecision> Reviews(string def) => r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(def) && l.amount == rec.id && !l.answered).ToList();
                if (!openBefore || rec.Open || Reviews(RiotReview.DecisionDef).Count != 0)
                    RiotReviewFail("close", $"the record did not stay open under an hour of quiet and close after it without a letter (open before {openBefore}, open after {rec.Open}, letters {Reviews(RiotReview.DecisionDef).Count})");
                RiotReview.Hourly(g, map, RiotReview.LetterHour, ended + 2 * RiotReview.CloseAfterTicks);
                RiotReview.Hourly(g, map, RiotReview.LetterHour, ended + 3 * RiotReview.CloseAfterTicks);
                List<ChoiceLetter_PrisonDecision> letters = Reviews(RiotReview.DecisionDef);
                if (letters.Count != 1)
                {
                    RiotReviewFail("letter", $"the morning after brought {letters.Count} letters for the riot, not one");
                    return;
                }
                ChoiceLetter_PrisonDecision letter = letters[0];
                string text = letter.Text.Resolve();
                List<RiotParticipant> seenList = rec.participants.Where(x => x.seen).ToList(), unseenList = rec.participants.Where(x => !x.seen).ToList();
                if (seenList.Count != 2) RPLog.Message($"[RimPrison:test] riot review scene: the record has {seenList.Count} seen at the letter (2 at the start)");
                if (seenList.Any(x => !NamedIn(text, x.name)) || unseenList.Any(x => NamedIn(text, x.name)) || letter.pawns.Count != seenList.Count || letter.pawns.Any(p => rec.Of(p.thingIDNumber)?.seen != true))
                    RiotReviewFail("letter", $"the letter does not name exactly the seen ({string.Join(",", seenList.Select(x => x.name))}; unseen {string.Join(",", unseenList.Select(x => x.name))}; pawns {string.Join(",", letter.pawns.Select(p => p.LabelShort))}): {text}");
                if (!text.Contains(Money.Format(RiotReview.Bill(rec))) || RiotReview.Bill(rec) <= 0) RiotReviewFail("letter", $"the letter does not give the damage bill ({Money.Format(RiotReview.Bill(rec))})");
                var worker = (PrisonDecisionWorker_RiotReview)letter.decision.Worker;
                int chargeable = RiotReview.Chargeable(g, map, rec).Count;
                if (chargeable != seenList.Count) RPLog.Message($"[RimPrison:test] riot review scene: {chargeable} of the {seenList.Count} seen are still here to be charged");
                // (The ringleader was in the dark: "ship out" says why it cannot be given; "charge" can.)
                bool leadSeen = rec.RingleaderSeen;
                if (!leadSeen && worker.Disabled(letter, 3) == null) RiotReviewFail("letter", "'ship the ringleader out' is offered for a ringleader nobody saw");
                if (worker.Disabled(letter, 4) != null) RiotReviewFail("letter", $"'charge what was seen' is not offered with two seen ({worker.Disabled(letter, 4)})");
                // Charge, through the letter: the seen go on the adjudication book, two cases, each once.
                letter.answered = true;
                PrisonDecisions.Choose(letter, 4);
                Find.LetterStack.RemoveLetter(letter);
                List<AdjudicationCase> added = g.adjudication.cases.Skip(cases0).ToList();
                HashSet<int> seenIds = new HashSet<int>(seenList.Select(x => x.id));
                if (added.Count != chargeable || added.Count == 0 || added.Any(k => !seenIds.Contains(k.inmateId)) || added.Select(k => k.inmateId).Distinct().Count() != added.Count || rec.charged != added.Count)
                    RiotReviewFail("charge", $"'charge' put {added.Count} cases on the book ({string.Join(",", added.Select(k => k.inmateName))}), not one for each of the {chargeable} seen still here");
                if (rec.answers.Count != 1 || rec.answers[0] != RiotAnswer.Charge || rec.done) RiotReviewFail("charge", $"the record's answers after 'charge' are {RiotReview.AnswersText(rec)} (done {rec.done})");
                List<ChoiceLetter_PrisonDecision> more = Reviews(RiotReview.MoreDecisionDef);
                if (more.Count != 1)
                {
                    RiotReviewFail("second answer", $"after one answer {more.Count} letters ask for a second, not one");
                    return;
                }
                // The second step: charge again is refused, amnesty too (another answer was given); "that is all" ends it, and nothing more is asked.
                var moreWorker = (PrisonDecisionWorker_RiotReview)more[0].decision.Worker;
                if (moreWorker.Disabled(more[0], 4) == null || moreWorker.Disabled(more[0], 7) == null) RiotReviewFail("second answer", "the second step offers 'charge' again or 'amnesty' after another answer");
                more[0].answered = true;
                PrisonDecisions.Choose(more[0], PrisonDecisionWorker_RiotReview.NoneOption);
                Find.LetterStack.RemoveLetter(more[0]);
                if (!rec.done || rec.answers.Count != 1 || Reviews(RiotReview.MoreDecisionDef).Count != 0 || g.adjudication.cases.Count - cases0 != added.Count)
                    RiotReviewFail("second answer", $"'that is all' did not end the answers ({RiotReview.AnswersText(rec)}, done {rec.done}, letters {Reviews(RiotReview.MoreDecisionDef).Count})");
            });
        }

        // ---- the restriction --------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRiotReviewRestrict(Map map)
        {
            Guarded("riot review: a restriction of 2 days shuts the yard on days 1 and 2, not 3, and its cost lands after", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RiotSetAside(g, r);
                Dictionary<SecurityLevelDef, DayOrders> orders0 = g.dayOrders;
                Dictionary<SecurityLevelDef, OrderDays> days0 = g.orderDays;
                g.dayOrders = new Dictionary<SecurityLevelDef, DayOrders>();
                g.orderDays = new Dictionary<SecurityLevelDef, OrderDays>();
                // (Each day of it holds the whole wing down a little more: the live inmates' suppression is put back.)
                List<(CompInmate c, float s)> suppressed0 = map.mapPawns.PrisonersOfColony.Where(x => x.Inmate() != null).Select(x => (x.Inmate(), x.Inmate().suppressed)).ToList();
                r.Add(() =>
                {
                    g.dayOrders = orders0;
                    g.orderDays = days0;
                    foreach ((CompInmate c, float s) in suppressed0) c.suppressed = s;
                    foreach (Map m in Find.Maps)
                        if (m.IsPlayerHome) PrisonMap.Of(m)?.Notify_DoorsChanged();
                });
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                int yard = -1;
                for (int h = 0; h < 24 && yard < 0; h++)
                    if (g.RegimeFor(level).At(h) == RPDefOf.RP_FreeTime) yard = h;
                if (yard < 0 || g.LevelLockedDown(level))
                {
                    RPLog.Message($"[RimPrison:test] riot review restriction: not run (the {level.label} timetable has no yard hour, or the wing is locked down)");
                    return;
                }
                Pawn a = Throwaway(map, r, level);
                CompInmate ac = a.Inmate();
                (ac.tension, ac.suppressed) = (20f, 0f);
                RiotRecord rec = RiotRecordOf(g, map, level, (a, true));
                int today = Ledger.CurrentDay;
                if (!RiotReview.Apply(g, map, rec, RiotAnswer.Restrict, 2))
                {
                    RiotReviewFail("restrict", $"a restriction of 2 days was not given ({RiotReview.Refused(g, map, rec, RiotAnswer.Restrict)})");
                    return;
                }
                DayOrders o = g.dayOrders.TryGetValue(level, out DayOrders o0) ? o0 : null;
                int now = Find.TickManager.TicksGame;
                const float day = GenDate.TicksPerDay, per = RiotReview.RestrictSuppressionPerDay;
                // Day 1: the yard is shut and the wing riots less, but nothing is held down up front (it is credited as each day is served) and no tension moves.
                if (o == null || g.SlotAt(level, yard) != RPDefOf.RP_Lockup || !RegimeOrders.Restricted(g, level) || o.untilDay != today + 1 || o.creditFromTick != now || rec.restrictUntilDay != today + 1 || rec.restrictDays != 2)
                    RiotReviewFail("restrict", $"day 1: the yard hour {yard}:00 is {g.SlotAt(level, yard)?.defName}, the order {o?.Describe()}, the record to day {rec.restrictUntilDay} (today {today})");
                if (ac.suppressed > 0.01f || RiotReview.WingFactor(g, level) > RiotReview.RestrictRiotFactor + 0.01f)
                    RiotReviewFail("restrict", $"day 1: the wing was held down up front (suppression {ac.suppressed:F1}) or does not riot less (the wing's riot factor {RiotReview.WingFactor(g, level):F2})");
                if (Mathf.Abs(ac.tension - 20f) > 0.01f) RiotReviewFail("restrict", $"day 1: the tension moved at once ({ac.tension:F1}); it is to land when the restriction ends");
                if (o == null) return;
                // Midnight into day 2 (the order is moved a day back, as the regime's own check of the midnight pass does, and a whole day is served): day 1 is credited.
                o.day = today - 1;
                o.untilDay = today;
                o.creditFromTick = now - (int)day;
                RegimeOrders.Sweep(g, map);
                if (RegimeOrders.Of(g, level) is not { } o2 || o2.day != today || g.SlotAt(level, yard) != RPDefOf.RP_Lockup || o2.lockupFrom != RegimeOrders.EarlyLockupHour || o2.creditFromTick != now || rec.restrictBilled)
                    RiotReviewFail("restrict", $"day 2: the yard hour is {g.SlotAt(level, yard)?.defName}, the order {RegimeOrders.Of(g, level)?.Describe()}, billed {rec.restrictBilled}");
                if (!g.orderDays.TryGetValue(level, out OrderDays od) || !od.days.Contains(today)) RiotReviewFail("restrict", "day 2 is not counted as a day ordered in (the inspector's habit)");
                if (Mathf.Abs(ac.suppressed - per) > 0.01f || Mathf.Abs(ac.tension - 20f) > 0.01f)
                    RiotReviewFail("restrict", $"day 2: suppression {ac.suppressed:F1} ({per:F1} wanted, day 1 served), tension {ac.tension:F1} (unchanged wanted)");
                // Midnight into day 3: over (half a day served this time: credited pro rata). The yard is back, and the tension it held down lands.
                if (RegimeOrders.Of(g, level) is { } o3)
                {
                    o3.day = today - 1;
                    o3.untilDay = today - 1;
                    o3.creditFromTick = now - (int)(day / 2f);
                }
                RegimeOrders.Sweep(g, map);
                if (RegimeOrders.Of(g, level) != null || g.SlotAt(level, yard) == RPDefOf.RP_Lockup || RegimeOrders.Restricted(g, level))
                    RiotReviewFail("restrict", $"day 3: the restriction is still in force (yard hour {g.SlotAt(level, yard)?.defName}, order {RegimeOrders.Of(g, level)?.Describe()})");
                if (Mathf.Abs(ac.suppressed - 1.5f * per) > 0.05f) RiotReviewFail("restrict", $"day 3: suppression {ac.suppressed:F2} ({1.5f * per:F2} wanted: a day and a half served)");
                float wanted = 20f + 2f * RiotReview.RestrictTensionPerDay;
                if (!rec.restrictBilled || Mathf.Abs(ac.tension - wanted) > 0.01f) RiotReviewFail("restrict", $"day 3: the tension did not land once it ended (billed {rec.restrictBilled}, tension {ac.tension:F1}, {wanted:F1} wanted)");
                // (A second midnight bills nothing more and holds nothing down.)
                float held = ac.suppressed;
                RegimeOrders.Sweep(g, map);
                RiotReview.Notify_RestrictionEnded(g, level, early: false);
                if (Mathf.Abs(ac.tension - wanted) > 0.01f || Mathf.Abs(ac.suppressed - held) > 0.01f) RiotReviewFail("restrict", $"the restriction's tension landed twice ({ac.tension:F1}) or it held the wing down after it ended ({ac.suppressed:F1})");
                // Lifting the yard order alone ends a restriction on its first day: its one day lands at once, the quarter day it ran is credited, and what is left (the early lockup) is today's order only.
                RiotRecord rec2 = RiotRecordOf(g, map, level, (a, true));
                (ac.tension, ac.suppressed) = (20f, 0f);
                if (!RiotReview.Apply(g, map, rec2, RiotAnswer.Restrict, 3)) RiotReviewFail("restrict", "a second riot's restriction, after the first ended, was not given");
                else
                {
                    RegimeOrders.Of(g, level).creditFromTick = now - (int)(day / 4f);
                    RegimeOrders.Lift(g, level, DayOrder.YardOff);
                    if (!rec2.restrictBilled || Mathf.Abs(ac.tension - (20f + RiotReview.RestrictTensionPerDay)) > 0.01f || Mathf.Abs(ac.suppressed - per / 4f) > 0.05f)
                        RiotReviewFail("restrict", $"a restriction lifted on its first day did not land its one day and a quarter day's hold (billed {rec2.restrictBilled}, tension {ac.tension:F1}, suppression {ac.suppressed:F2})");
                    if (RegimeOrders.Restricted(g, level) || RiotReview.WingFactor(g, level) < 0.99f || RegimeOrders.Of(g, level) is not { lockupFrom: >= 0, untilDay: -1, creditFromTick: -1 })
                        RiotReviewFail("restrict", $"with the yard order lifted the restriction goes on (restricted {RegimeOrders.Restricted(g, level)}, factor {RiotReview.WingFactor(g, level):F2}, order {RegimeOrders.Of(g, level)?.Describe()})");
                    RegimeOrders.Lift(g, level, DayOrder.EarlyLockup);
                }
                // A restriction with a second shower: the two that confine lifted, the shower alone is no restriction - at once, and at midnight nothing is carried, credited or billed again.
                g.dayOrders.Clear();
                RiotRecord rec3 = RiotRecordOf(g, map, level, (a, true));
                (ac.tension, ac.suppressed) = (20f, 0f);
                if (!RiotReview.Apply(g, map, rec3, RiotAnswer.Restrict, 3)) RiotReviewFail("restrict", "a third riot's restriction was not given");
                else if (!RegimeOrders.Give(g, map, level, DayOrder.DoubleShower))
                    RPLog.Message("[RimPrison:test] riot review restriction: the restriction and a second shower are not checked (no hour left today for a shower)");
                else
                {
                    RegimeOrders.Of(g, level).creditFromTick = now;
                    RegimeOrders.Lift(g, level, DayOrder.EarlyLockup);
                    RegimeOrders.Lift(g, level, DayOrder.YardOff);
                    float billed = 20f + RiotReview.RestrictTensionPerDay;
                    if (RegimeOrders.Restricted(g, level) || !rec3.restrictBilled || Mathf.Abs(ac.tension - billed) > 0.01f)
                        RiotReviewFail("restrict", $"a restriction left with a second shower is still in force or was not billed its one day (restricted {RegimeOrders.Restricted(g, level)}, billed {rec3.restrictBilled}, tension {ac.tension:F1})");
                    if (RegimeOrders.Of(g, level) is { } left)
                    {
                        left.day = today - 1;
                        RegimeOrders.Sweep(g, map);
                    }
                    if (RegimeOrders.Of(g, level) != null || ac.suppressed > 0.01f || Mathf.Abs(ac.tension - billed) > 0.01f)
                        RiotReviewFail("restrict", $"the midnight after, the shower was carried, credited or billed as a restriction day (order {RegimeOrders.Of(g, level)?.Describe()}, suppression {ac.suppressed:F1}, tension {ac.tension:F1})");
                }
            });
        }

        // ---- charge, against the riot's own records -------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRiotReviewCharge(Map map)
        {
            Guarded("riot review: charge skips anyone the riot's own records dealt with (sanctioned, or on the sheet answered or not), and a refused choice asks again", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RiotSetAside(g, r);
                PunishmentPolicy policy0 = g.punishments;
                r.Add(() => g.punishments = policy0);
                g.punishments = new PunishmentPolicy();
                // (Joining a riot gives nothing under this policy; inciting one keeps the default.)
                g.punishments.Set(RPRiotDefOf.RP_RiotJoined, 0);
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                Pawn a = Throwaway(map, r, level), b = Throwaway(map, r, level), c = Throwaway(map, r, level);
                RiotRecord rec = RiotRecordOf(g, map, level, (a, true), (b, true), (c, true));
                // The riot's own records (a riot is loud: seen): the ringleader's, which the policy sanctioned; c's, for which the policy gives nothing; b has none.
                a.Inmate().AddRecord(new MisconductRecord { def = RPRiotDefOf.RP_Riot, tick = rec.startTick, seen = true });
                c.Inmate().AddRecord(new MisconductRecord { def = RPRiotDefOf.RP_RiotJoined, tick = rec.startTick + 10, seen = true });
                List<Pawn> chargeable = RiotReview.Chargeable(g, map, rec);
                if (chargeable.Contains(a) || !chargeable.Contains(b) || !chargeable.Contains(c))
                    RiotReviewFail("charge", $"with the ringleader sanctioned at the riot, the chargeable are {string.Join(",", chargeable.Select(p => p.LabelShort))} (the two others wanted, not {a.LabelShort})");
                int cases0 = g.adjudication.cases.Count;
                if (!RiotReview.Apply(g, map, rec, RiotAnswer.Charge)) RiotReviewFail("charge", $"'charge' with two not dealt with was not given ({RiotReview.Refused(g, map, rec, RiotAnswer.Charge)})");
                List<AdjudicationCase> added = g.adjudication.cases.Skip(cases0).ToList();
                if (added.Count != 2 || added.Any(k => k.inmateId == a.thingIDNumber) || added.Any(k => k.tick < rec.startTick || k.tick > rec.lastRiotTick) || rec.charged != 2)
                    RiotReviewFail("charge", $"'charge' put {added.Count} cases on the book ({string.Join(",", added.Select(k => $"{k.inmateName}@{k.tick}"))}; window {rec.startTick}-{rec.lastRiotTick}), the two not dealt with wanted, inside the riot's window");
                if (!RiotReview.DealtWith(g, rec, b) || !RiotReview.DealtWith(g, rec, c)) RiotReviewFail("charge", "the cases a charge made do not count as this riot's (a second charge would put them on twice)");
                // Adjudication on: the riot queued a case for each rioter, and one was answered on the morning's sheet before the riot's letter. Both are on the sheet for this riot: "charge" is refused, and its button says why.
                g.adjudicate = true;
                Pawn d = Throwaway(map, r, level), e = Throwaway(map, r, level);
                RiotRecord rec2 = RiotRecordOf(g, map, level, (d, true), (e, true));
                rec2.lastRiotTick = Find.TickManager.TicksGame;
                if (!Adjudication.Queue(d, d.Inmate(), RPRiotDefOf.RP_Riot, evidence: true) || !Adjudication.Queue(e, e.Inmate(), RPRiotDefOf.RP_RiotJoined, evidence: true))
                {
                    RiotReviewFail("charge", "the riot's cases could not be queued for two throwaways");
                    return;
                }
                AdjudicationCase answered = g.adjudication.cases.Last(k => k.inmateId == d.thingIDNumber);
                (answered.answer, answered.answerTick, answered.days) = (AdjudicationAnswer.DaysAdded, Find.TickManager.TicksGame, 5);
                string why = RiotReview.Refused(g, map, rec2, RiotAnswer.Charge);
                if (why != "RP_RiotRefused_DealtWith".Translate().Resolve()) RiotReviewFail("charge", $"with both on the sheet for the riot (one answered), 'charge' is refused for \"{why ?? "nothing"}\"");
                ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make(RiotReview.DecisionDef, RiotReview.Label(rec2), RiotReview.LetterText(rec2, map), map);
                letter.amount = rec2.id;
                var worker = (PrisonDecisionWorker_RiotReview)letter.decision.Worker;
                if (worker.Disabled(letter, 4) != why) RiotReviewFail("charge", $"the letter's 'charge' button does not say why it cannot be given ({worker.Disabled(letter, 4) ?? "enabled"})");
                // Chosen all the same (the button was drawn before the sheet was answered): nothing is given, and the same step comes again with the reason - not "a second answer".
                int cases1 = g.adjudication.cases.Count;
                letter.answered = true;
                PrisonDecisions.Choose(letter, 4);
                List<ChoiceLetter_PrisonDecision> again = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(RiotReview.MoreDecisionDef) && l.amount == rec2.id && !l.answered).ToList();
                string againLabel = again.Count == 1 ? ((TaggedString)again[0].Label).Resolve() : "";
                if (g.adjudication.cases.Count != cases1 || rec2.answers.Count != 0 || rec2.done || again.Count != 1 || againLabel != "RP_RiotReviewAgainLabel".Translate(level.label).Resolve() || why == null
                    || !again[0].Text.Resolve().Contains(why))
                    RiotReviewFail("refused", $"a refused choice gave something or did not ask again with the reason (cases +{g.adjudication.cases.Count - cases1}, answers {RiotReview.AnswersText(rec2)}, done {rec2.done}, letters {again.Count} \"{againLabel}\")");
                foreach (ChoiceLetter_PrisonDecision l in again) Find.LetterStack.RemoveLetter(l);
            });
        }

        // ---- the records: two riots, a joiner, a rioter of no riot, a blow from staff --------------------------------------------------------------------------------------------------

        private static void CheckRiotReviewRecords(Map map)
        {
            Guarded("riot review: two riots on a map keep apart, a joiner goes into the puller's record, a rioter of no riot into none, a blow from staff is a sighting, still here is the same stay", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RiotSetAside(g, r);
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                Pawn a = Throwaway(map, r, level), b = Throwaway(map, r, level), j = Throwaway(map, r, level), x = Throwaway(map, r, level);
                RiotRecord first = RiotRecordOf(g, map, level, (a, false)), second = RiotRecordOf(g, map, level, (b, false));
                int now = Find.TickManager.TicksGame;
                foreach (RiotRecord rr in new[] { first, second }) (rr.endTick, rr.lastRiotTick, rr.letterTick) = (-1, now - 500, -1);
                // Each rioter keeps their own riot's clock.
                RiotReview.Notify_Rioting(a);
                if (first.lastRiotTick != now || second.lastRiotTick != now - 500) RiotReviewFail("records", $"a rioter of the first riot moved the clocks to {first.lastRiotTick} and {second.lastRiotTick} ({now} and {now - 500} wanted)");
                // A joiner pulled in by the second riot's ringleader is in the second record only.
                RiotReview.Notify_Rioting(j, b);
                if (second.Of(j.thingIDNumber) == null || first.Of(j.thingIDNumber) != null) RiotReviewFail("records", $"a joiner pulled in from the second riot is in the first ({first.Of(j.thingIDNumber) != null}) or not in the second ({second.Of(j.thingIDNumber) != null})");
                // One rioting for no riot (an assault's hostage-taker) joins neither.
                int n1 = first.participants.Count, n2 = second.participants.Count;
                RiotReview.Notify_Rioting(x);
                if (first.participants.Count != n1 || second.participants.Count != n2) RiotReviewFail("records", "a pawn in no riot's record was put in an open one");
                // A guard's blow on a rioter nobody saw: the guard saw them.
                Pawn guard = ThrowawayStaff(map, r, RPSecurityDefOf.RP_Guard);
                first.Of(a.thingIDNumber).seen = false;
                RiotReview.Notify_Damage(a, new DamageInfo(DamageDefOf.Blunt, 3f, instigator: guard), victimRioting: true, 0, 0f);
                if (first.Of(a.thingIDNumber)?.seen != true || !first.inmatesHurt.Contains(a.thingIDNumber))
                    RiotReviewFail("seen", $"a rioter a guard struck is not seen or not hurt on the record ({first.Describe()})");
                // Still here: a seen joiner admitted after the riot began (an arrival pulled in) is this riot's; one admitted after its last rioting left and came back.
                Pawn lead = Throwaway(map, r, level), late = Throwaway(map, r, level), back = Throwaway(map, r, level);
                RiotRecord third = RiotRecordOf(g, map, level, (lead, true), (late, true), (back, true));
                late.Inmate().admittedTick = third.startTick + 10;
                back.Inmate().admittedTick = third.lastRiotTick + 10;
                List<Pawn> here = RiotReview.SeenHere(third, map);
                if (!here.Contains(lead) || !here.Contains(late) || here.Contains(back))
                    RiotReviewFail("here", $"the seen still here are {string.Join(",", here.Select(p => p.LabelShort))} ({lead.LabelShort} and {late.LabelShort}, admitted mid-riot, wanted; not {back.LabelShort}, admitted after it)");
            });
        }

        // ---- the answers ---------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRiotReviewAnswers(Map map)
        {
            Guarded("riot review: two answers at most, each once, amnesty alone, the negotiated menu, ship out for a seen ringleader", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RiotSetAside(g, r);
                SecurityLevelDef level = InmateUtility.DefaultSecurity;
                Pawn a = Throwaway(map, r, level), b = Throwaway(map, r, level), c = Throwaway(map, r, level);
                (a.Inmate().cash, b.Inmate().cash, c.Inmate().cash) = (100, 500, 900);
                // Restitution out of the seen ones' cash, as far as it goes (the unseen pays nothing), then the grievance: two answers, and the record is done.
                RiotRecord rec = RiotRecordOf(g, map, level, (a, true), (b, true), (c, false));
                rec.damage = 300f;
                int funds0 = g.ledger.funds;
                if (RiotReview.Refused(g, map, rec, RiotAnswer.Amnesty) != null) RiotReviewFail("answers", "amnesty is refused as a first answer");
                if (!RiotReview.Apply(g, map, rec, RiotAnswer.Restitution) || rec.restitution != 250 || a.Inmate().cash != 0 || b.Inmate().cash != 350 || c.Inmate().cash != 900 || g.ledger.funds - funds0 != 250)
                    RiotReviewFail("restitution", $"a 300 bill split between two seen with 100 and 500 took {rec.restitution} ({a.Inmate().cash}/{b.Inmate().cash}/{c.Inmate().cash} left, funds +{g.ledger.funds - funds0}); 250 wanted, 150 a share, the unseen untouched");
                if (RiotReview.Apply(g, map, rec, RiotAnswer.Restitution) || g.ledger.funds - funds0 != 250) RiotReviewFail("answers", "restitution was given twice");
                if (RiotReview.Refused(g, map, rec, RiotAnswer.Amnesty) == null || RiotReview.Apply(g, map, rec, RiotAnswer.Amnesty)) RiotReviewFail("answers", "amnesty was offered or given after another answer");
                if (!RiotReview.Apply(g, map, rec, RiotAnswer.Grievance) || g.demand.need != NeedDefOf.Food || g.demand.spokesId != a.thingIDNumber || !rec.done || rec.answers.Count != 2)
                    RiotReviewFail("grievance", $"meeting the grievance made no promise to the ringleader or did not end the answers (promise {g.demand.need?.defName} to {g.demand.spokesId}, done {rec.done}, {RiotReview.AnswersText(rec)})");
                if (RiotReview.Apply(g, map, rec, RiotAnswer.Charge) || rec.answers.Count != 2) RiotReviewFail("answers", "a third answer was given");
                // A ringleader nobody saw is not the promise's spokesperson (the chronicle would name them when it is kept or broken).
                RiotRecord unseenGrievance = RiotRecordOf(g, map, level, (c, false), (b, true));
                if (!RiotReview.Apply(g, map, unseenGrievance, RiotAnswer.Grievance) || g.demand.need != NeedDefOf.Food || g.demand.spokesId != -1 || g.demand.spokesName != null)
                    RiotReviewFail("grievance", $"meeting the grievance of a riot whose ringleader nobody saw made them the spokesperson (to {g.demand.spokesId}, {g.demand.spokesName ?? "nobody"})");
                // A negotiated riot: "meet the grievance" is done already.
                RiotRecord deal = RiotRecordOf(g, map, level, (b, true), (c, true));
                deal.letterTick = -1;
                RiotReview.Notify_Negotiated(b.thingIDNumber);
                if (!deal.negotiated || RiotReview.Refused(g, map, deal, RiotAnswer.Grievance) == null || !RiotReview.LetterText(deal, map).Contains("RP_RiotReport_Negotiated".Translate(NeedDefOf.Food.label).Resolve()))
                    RiotReviewFail("negotiated", $"a negotiated riot is not marked, or still offers the grievance (negotiated {deal.negotiated})");
                // Ship out: only for a seen ringleader still here; given, they are on their way out and it is one answer.
                g.ledger.funds = InmateUtility.TransferCost * 3;
                RiotRecord unseenLead = RiotRecordOf(g, map, level, (c, false), (b, true));
                if (RiotReview.Refused(g, map, unseenLead, RiotAnswer.ShipOut) == null) RiotReviewFail("ship out", "'ship out' is offered for a ringleader nobody saw");
                RiotRecord seenLead = RiotRecordOf(g, map, level, (c, true));
                r.Add(() => ShuttleRemovePawn(c));
                if (RiotReview.Refused(g, map, seenLead, RiotAnswer.ShipOut) is { } why) RiotReviewFail("ship out", $"'ship out' is refused for a seen ringleader ({why})");
                else if (!RiotReview.Apply(g, map, seenLead, RiotAnswer.ShipOut) || !(ShuttleSystem.OnTheWayOut(c) || !c.Spawned) || seenLead.answers.Count != 1)
                    RiotReviewFail("ship out", $"shipping a seen ringleader out did not put them on the way out ({RiotReview.AnswersText(seenLead)})");
                // Amnesty: alone; the wing's tension down, the reputation down, and the wing riots more readily for a while.
                Pawn d = Throwaway(map, r, level);
                d.Inmate().tension = 50f;
                RiotRecord amnesty = RiotRecordOf(g, map, level, (d, true));
                float rep0 = g.reputation;
                float factor0 = RiotReview.WingFactor(g, level);
                if (!RiotReview.Apply(g, map, amnesty, RiotAnswer.Amnesty) || !amnesty.done || Mathf.Abs(d.Inmate().tension - (50f - RiotReview.AmnestyTension)) > 0.01f || g.reputation > rep0 - RiotReview.AmnestyReputation + 0.01f
                    || RiotReview.WingFactor(g, level) < factor0 * RiotReview.AmnestyRiotFactor - 0.01f)
                    RiotReviewFail("amnesty", $"amnesty did not end the answers, calm the wing, cost reputation and raise the wing's riot odds (done {amnesty.done}, tension {d.Inmate().tension:F1}, reputation {rep0:F1} -> {g.reputation:F1}, factor {factor0:F2} -> {RiotReview.WingFactor(g, level):F2})");
                if (RiotReview.Refused(g, map, amnesty, RiotAnswer.Charge) == null) RiotReviewFail("amnesty", "another answer is offered after amnesty");
            });
        }

        // ---- the budget ------------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRiotReviewBudget(Map map)
        {
            Guarded("riot review: one budgeted letter a riot, two riots in a night, the second waits as the budget says", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RiotSetAside(g, r);
                bool test0 = StoryLetters.TestBudget;
                int decisions0 = g.story.decisionsToday;
                List<ChoiceLetter_PrisonDecision> deferred0 = g.story.deferredLetters.ToList();
                r.Add(() =>
                {
                    StoryLetters.TestBudget = test0;
                    g.story.decisionsToday = decisions0;
                    g.story.deferredLetters.Clear();
                    g.story.deferredLetters.AddRange(deferred0);
                });
                StoryLetters.TestBudget = true;
                int budget = StoryLetters.Budget(map.mapPawns.PrisonersOfColonySpawnedCount);
                g.story.decisionsToday = budget - 1;
                RiotRecord first = RiotRecordOf(g, map, InmateUtility.DefaultSecurity), second = RiotRecordOf(g, map, InmateUtility.DefaultSecurity);
                first.letterTick = second.letterTick = -1;
                RiotReview.Hourly(g, map, RiotReview.LetterHour);
                bool OnStack(RiotRecord x) => Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is(RiotReview.DecisionDef) && l.amount == x.id);
                bool Held(RiotRecord x) => g.story.deferredLetters.Any(l => l.Is(RiotReview.DecisionDef) && l.amount == x.id);
                if (!OnStack(first) || OnStack(second) || !Held(second) || first.letterTick < 0 || second.letterTick < 0)
                    RiotReviewFail("budget", $"with one decision left in the day, two riots' letters went {(OnStack(first) ? "first on the desk" : "first not on the desk")}, {(OnStack(second) ? "second on the desk" : Held(second) ? "second held" : "second nowhere")}");
                // (Held back, it is still open for the Intel list; the next day delivers it.)
                if (!RiotReview.LetterOpen(second)) RiotReviewFail("budget", "the held letter does not count as open");
                StoryLetters.NewDay(g);
                StoryLetters.Daily(g, map);
                if (!OnStack(second) || Held(second)) RiotReviewFail("budget", "the next day did not deliver the held letter");
                // (One letter a riot: the pass again sends none.)
                int count = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.Is(RiotReview.DecisionDef));
                RiotReview.Hourly(g, map, RiotReview.LetterHour);
                if (r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().Count(l => l.Is(RiotReview.DecisionDef)) != count || count != 2) RiotReviewFail("budget", $"the riots had {count} letters, and the next morning's pass sent more");
                // An unanswered letter is "no reckoning".
                ChoiceLetter_PrisonDecision firstLetter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().First(l => l.Is(RiotReview.DecisionDef) && l.amount == first.id);
                firstLetter.Lapse();
                if (!first.done || first.answers.Count != 0 || RiotReview.StateText(first) != "RP_RiotState_NoReckoning".Translate().Resolve())
                    RiotReviewFail("timeout", $"an unanswered letter left the record {(first.done ? "done" : "open")} with {RiotReview.AnswersText(first)} ({RiotReview.StateText(first)})");
            });
        }

        // ---- the book --------------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void CheckRiotReviewBook(Map map)
        {
            Guarded("riot review: the last ten kept, the answered first to go", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                RiotSetAside(g, r);
                for (int i = 0; i < 12; i++)
                {
                    RiotRecord x = RiotRecordOf(g, map, InmateUtility.DefaultSecurity);
                    x.done = i != 0;
                    // (The second is answered, but its restriction is still running: its tension lands from it when it ends.)
                    if (i == 1) (x.restrictDays, x.restrictUntilDay) = (2, Ledger.CurrentDay + 1);
                }
                RiotReview.Trim(g.riots);
                // (The first is still unanswered and the second's restriction not billed: they stay; the two oldest answered after them go.)
                if (g.riots.records.Count != RiotReview.MaxRecords || g.riots.records[0].id != 1 || g.riots.records[1].id != 2 || g.riots.records.Any(x => x.id == 3 || x.id == 4))
                    RiotReviewFail("book", $"twelve records trimmed to {g.riots.records.Count}: {string.Join(",", g.riots.records.Select(x => x.id))} (1, 2 and 5-12 wanted)");
                // (An old save has no book: the load makes an empty one.)
                if (new RiotBook().records.Count != 0 || new RiotBook().Describe() != "1:") RiotReviewFail("book", "a new book is not empty");
            });
        }

        // ---- the saveload plan ----------------------------------------------------------------------------------------------------------------------------------------------------------

        private static string RiotReviewDescribe(PrisonGame g) => g.riots.Describe();

        private static RiotBook riotReviewBefore;
        private static string riotReviewSeedDescribed;
        private static int riotReviewSeedId = -1;

        /// <summary>
        /// Saveload plan, before the snapshot: a book with a record half answered (restitution given, its second step's letter open, every field off its default, the rumour it kept too), one still open and one done, with the next id off its
        /// default. The two inmates in it have no riot of their own in its window (a riot's own record would have dealt with them, and the charge after the load would have nobody to charge).
        /// </summary>
        private static void SeedRiotReviewForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            int since = Find.TickManager.TicksGame - 10 * GenDate.TicksPerHour;
            List<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned.Where(p => InmateUtility.IsActiveInmate(p) && !ShuttleSystem.OnTheWayOut(p) && p.Inmate().admittedTick < since
                                                                                && !p.Inmate().records.Any(x => (x.def == RPRiotDefOf.RP_Riot || x.def == RPRiotDefOf.RP_RiotJoined) && x.tick >= since))
                .OrderBy(p => p.thingIDNumber).Take(2).ToList();
            if (inmates.Count < 2)
            {
                RPLog.Message("[RimPrison:test] saveload riot review: not seeded (needs two inmates)");
                return;
            }
            riotReviewBefore = g.riots;
            int now = Find.TickManager.TicksGame, day = Ledger.CurrentDay;
            Pawn a = inmates[0], b = inmates[1];
            var half = new RiotRecord
            {
                id = 5, mapId = map.uniqueID, level = a.Inmate().security ?? InmateUtility.DefaultSecurity, startTick = now - 9 * GenDate.TicksPerHour, endTick = now - 6 * GenDate.TicksPerHour, lastRiotTick = now - 7 * GenDate.TicksPerHour,
                ringleaderId = a.thingIDNumber, ringleaderName = a.LabelShort, staffHurt = new List<int> { 77, 78 }, inmatesHurt = new List<int> { b.thingIDNumber }, deaths = new List<string> { "Zdead" }, deadIds = new List<int> { 79, 4242 }, damage = 345.5f,
                grievance = NeedDefOf.Food, grievanceReal = true, needPercent = 23, missedMeals = 4, lockedHours = 9, nearestGuard = 14, dark = true, guardsRostered = 3, wingInmates = 11, negotiated = true, letterTick = now - 1000,
                answers = new List<RiotAnswer> { RiotAnswer.Restitution }, restrictDays = 2, restrictUntilDay = day - 1, restrictBilled = true, amnestyUntilTick = now - 50, charged = 1, restitution = 120,
                // (The rumour the wing was telling when it began, every field of it off its default.)
                rumour = new Rumour
                {
                    kind = RumourKind.Death, level = a.Inmate().security ?? InmateUtility.DefaultSecurity, originId = b.thingIDNumber, originName = b.LabelShort, isTrue = true, distorted = true, reach = 0.625f,
                    startTick = Mathf.Max(1, now - 12 * GenDate.TicksPerHour), expireTick = now + 2000, endTick = Mathf.Max(1, now - 5 * GenDate.TicksPerHour), end = RumourEnd.Calmed, subject = "Zriotdead", truthKnown = true,
                    originKnown = true,
                },
            };
            half.participants.Add(new RiotParticipant { id = a.thingIDNumber, name = a.LabelShort, seen = true });
            half.participants.Add(new RiotParticipant { id = b.thingIDNumber, name = b.LabelShort, seen = true });
            half.participants.Add(new RiotParticipant { id = 4242, name = "Zunseen", seen = false });
            var open = new RiotRecord { id = 6, mapId = map.uniqueID, level = InmateUtility.DefaultSecurity, startTick = now - 100, lastRiotTick = now, ringleaderId = 4343, ringleaderName = "Zopen" };
            open.participants.Add(new RiotParticipant { id = 4343, name = "Zopen" });
            // (A third, done: the flag that ends the answers comes back too.)
            var done = new RiotRecord { id = 4, mapId = map.uniqueID, level = InmateUtility.DefaultSecurity, startTick = now - 30000, endTick = now - 25000, lastRiotTick = now - 26000, ringleaderId = 4444, ringleaderName = "Zdone", letterTick = now - 20000, done = true };
            done.participants.Add(new RiotParticipant { id = 4444, name = "Zdone", seen = true });
            g.riots = new RiotBook { nextId = 7, records = new List<RiotRecord> { done, half, open } };
            riotReviewSeedId = half.id;
            // (Its second step is on the desk: the save keeps the letter, and the answer after the load is given once.)
            RiotReview.SendMore(map, half);
            riotReviewSeedDescribed = RiotReviewDescribe(g);
            RPLog.Message($"[RimPrison:test] saveload riot review: {riotReviewSeedDescribed.Length} characters seeded");
        }

        /// <summary>
        /// After the reload: the book came back whole, the second step's letter with it; the answer already given is not given again; the second, through the letter, is given once (and the letter chosen again gives
        /// nothing); a third is refused. Then the plan's seeds go.
        /// </summary>
        private static void CheckRiotReviewAfterReload(Map map)
        {
            if (riotReviewBefore == null) return;
            PrisonGame g = PrisonGame.Instance;
            string after = RiotReviewDescribe(g);
            bool whole = after == riotReviewSeedDescribed;
            RPLog.Message($"[RimPrison:test] saveload riot review: {(whole ? "intact" : "changed")}");
            if (!whole) reloadProblems.Add($"the riot book changed in the round trip: {riotReviewSeedDescribed} -> {after}");
            RiotRecord half = g.riots.ById(riotReviewSeedId);
            if (g.riots.ById(4) is not { done: true }) reloadProblems.Add("riot review: after the reload the answered record is gone or not done");
            // (The charge after the load goes on an adjudication book of its own: the sheet's seeded state is the adjudication seed's to compare after this, and nothing of it - not even the next case id - is touched here.)
            AdjudicationBook adjudication0 = g.adjudication;
            g.adjudication = new AdjudicationBook();
            ChoiceLetter_PrisonDecision letter = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is(RiotReview.MoreDecisionDef) && l.amount == riotReviewSeedId && !l.answered);
            if (half == null || letter == null) reloadProblems.Add($"riot review: after the reload the half-answered record ({half != null}) or its open letter ({letter != null}) is gone");
            else
            {
                int funds0 = g.ledger.funds, cash0 = map.mapPawns.PrisonersOfColonySpawned.Sum(p => p.Inmate()?.cash ?? 0), cases0 = g.adjudication.cases.Count;
                if (RiotReview.Apply(g, map, half, RiotAnswer.Restitution) || g.ledger.funds != funds0 || map.mapPawns.PrisonersOfColonySpawned.Sum(p => p.Inmate()?.cash ?? 0) != cash0 || half.answers.Count != 1)
                    reloadProblems.Add("riot review: after the reload the answer already given (restitution) was given again");
                letter.answered = true;
                PrisonDecisions.Choose(letter, 4);
                Find.LetterStack.RemoveLetter(letter);
                int charged = g.adjudication.cases.Count - cases0;
                PrisonDecisions.Choose(letter, 4);
                bool once = g.adjudication.cases.Count - cases0 == charged;
                if (half.answers.Count != 2 || half.answers[1] != RiotAnswer.Charge || !half.done || charged < 1 || !once || RiotReview.Apply(g, map, half, RiotAnswer.Amnesty))
                    reloadProblems.Add($"riot review: after the reload the second answer was not given once through the letter ({RiotReview.AnswersText(half)}, done {half.done}, {charged} cases, once {once})");
                else RPLog.Message($"[RimPrison:test] saveload riot review: the second answer given once after the load ({charged} cases)");
            }
            foreach (ChoiceLetter_PrisonDecision l in Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_PrisonDecision>().Where(l => l.Is(RiotReview.MoreDecisionDef) || l.Is(RiotReview.DecisionDef)).ToList())
                if (l.amount == riotReviewSeedId) Find.LetterStack.RemoveLetter(l);
            g.adjudication = adjudication0;
            g.riots = riotReviewBefore;
            riotReviewBefore = null;
            riotReviewSeedId = -1;
        }
    }
}
