using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>A kind of chronicle entry (filter, colour).</summary>
    public class ChronicleCategoryDef : Def
    {
        public Color color = Color.white;
        public int uiOrder;
    }

    [DefOf]
    public static class RPChronicleDefOf
    {
        public static ChronicleCategoryDef RP_Chr_Intake;
        public static ChronicleCategoryDef RP_Chr_Release;
        public static ChronicleCategoryDef RP_Chr_Violence;
        public static ChronicleCategoryDef RP_Chr_Death;
        public static ChronicleCategoryDef RP_Chr_Security;
        public static ChronicleCategoryDef RP_Chr_Gangs;
        public static ChronicleCategoryDef RP_Chr_Staff;
        public static ChronicleCategoryDef RP_Chr_Decisions;
        public static ChronicleCategoryDef RP_Chr_Outside;
        public static ChronicleCategoryDef RP_Chr_Life;

        static RPChronicleDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPChronicleDefOf));
    }

    /// <summary>
    /// One remembered moment. People are kept as thing IDs and names (not save references), so an entry outlives
    /// the pawns in it: released, transferred, dead or discarded.
    /// </summary>
    public class ChronicleEntry : IExposable
    {
        public int tick;
        public ChronicleCategoryDef cat;
        /// <summary>1 routine, 2 notable, 3 milestone.</summary>
        public int importance = 1;
        public string text;
        public List<int> ids = new List<int>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "t");
            Scribe_Defs.Look(ref cat, "c");
            Scribe_Values.Look(ref importance, "i", 1);
            Scribe_Values.Look(ref text, "x");
            Scribe_Collections.Look(ref ids, "p", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) ids ??= new List<int>();
        }

        /// <summary>The day of the game it happened on (0 is day 1); a day before the game, such as an old warden's milestone (<see cref="Handover"/>), is negative (counted down, not toward zero).</summary>
        public int Day => DayOf(tick);

        /// <summary>The hour of the day it happened in.</summary>
        public int Hour => HourOf(tick);

        /// <summary>The day of a tick, rounded down in integers (a float cannot hold a tick exactly past day 280): a tick before the game is a day before it.</summary>
        public static int DayOf(int tick) => tick >= 0 ? tick / GenDate.TicksPerDay : (tick + 1) / GenDate.TicksPerDay - 1;

        public static int HourOf(int tick) => (tick - DayOf(tick) * GenDate.TicksPerDay) / GenDate.TicksPerHour;

        /// <summary>A pawn from this entry still on the map (for the jump button), or null.</summary>
        public Pawn LivePawn(Map map)
        {
            if (map == null) return null;
            foreach (int id in ids)
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    if (p.thingIDNumber == id) return p;
            return null;
        }
    }

    /// <summary>A short line in someone's own history (shown in their file).</summary>
    public class LifeEntry : IExposable
    {
        public int tick;
        public int importance = 1;
        public string text;

        public void ExposeData()
        {
            Scribe_Values.Look(ref tick, "t");
            Scribe_Values.Look(ref importance, "i", 1);
            Scribe_Values.Look(ref text, "x");
        }
    }

    /// <summary>
    /// The prison's memory: a chronicle of what the warden knew happened (only facts that reached the warden are
    /// recorded — hidden deals stay hidden until found out), per-person life logs, and the firsts that make
    /// milestones. Written only through <see cref="Story.Record"/>.
    /// </summary>
    public class StorySystem : IExposable
    {
        public const int MaxEntries = 500;
        public const int MaxLife = 24;

        public List<ChronicleEntry> entries = new List<ChronicleEntry>();
        public HashSet<string> firsts = new HashSet<string>();
        public int admissions;
        [Unsaved] public PrisonPulse pulse = new PrisonPulse();
        /// <summary>Spent by story beats and big moments; reset at midnight.</summary>
        public int dramaToday;
        public int beatsToday;
        /// <summary>Decision letters delivered today, and the ones held back for the next day (see <see cref="StoryLetters"/>).</summary>
        public int decisionsToday;
        public List<ChoiceLetter_PrisonDecision> deferredLetters = new List<ChoiceLetter_PrisonDecision>();
        public int lastRiotTick = -1;
        public int lastDeathTick = -1;
        public int lastHazardDemandTick = -999999;
        /// <summary>How much the parole board listens to the warden (0.3-1.5).</summary>
        public float boardTrust = 1f;
        public List<CoverUp> coverUps = new List<CoverUp>();
        /// <summary>Condolence letters on their way (see <see cref="CondolenceSystem"/>), and the dead whose death was the prison's fault (by thing id: the family's letter is bitter).</summary>
        public List<Condolence> condolences = new List<Condolence>();
        public List<int> bitterDeaths = new List<int>();
        /// <summary>The funerals asked for, chosen or just held (see <see cref="Funerals"/>).</summary>
        public List<Funeral> funerals = new List<Funeral>();
        /// <summary>The inmates the warden kept apart from one who died, taken at the death (the order ends with it) for the funeral's list of mourners: triples: the dead one's thing id, the other's and the tick of the death (see <see cref="Funerals.Notify_Dying"/>).</summary>
        public List<int> funeralApart = new List<int>();
        public string lastInquiryVictim;
        public List<GangTruce> truces = new List<GangTruce>();
        public int outbreakUntilTick = -1;
        public bool outbreakVaccinated, outbreakIsolate;
        /// <summary>Inspection due at the tick (-1 none); prep 0 none, 1 deep clean, 2 bribed; a bribe exposed at the tick.</summary>
        public int inspectionTick = -1, inspectionPrep, bribeExposeTick = -1;
        /// <summary>What the inspector wrote down on the last visit, what a bribed visit left out, and the re-inspection due (see <see cref="InspectionReport"/>).</summary>
        public InspectionState inspection = new InspectionState();
        public int lastHolidayYear = -1;
        public int lastOfficerOfMonthDay = -1;
        /// <summary>Relief bought for one heat wave or cold snap: its map and the condition's start tick.</summary>
        public int weatherReliefMapId = -1, weatherReliefStartTick = -1;
        /// <summary>Settlements in a row in the red; what is left to repay on the state loan.</summary>
        public int debtDays, loanOwed;
        /// <summary>A food scare's second wave: due at the tick (-1 none) with this chance.</summary>
        public int foodScareTick = -1;
        public float foodScareChance;
        /// <summary>No inmate kitchen crew until the tick (stood down after a food scare).</summary>
        public int kitchenStandDownUntilTick = -1;
        /// <summary>A signed export contract: plates to deliver (0 none), delivered so far, deadline.</summary>
        public int contractTarget, contractDelivered, contractRunsLeft, contractDueTick = -1, lastExportRunDay = -1;
        /// <summary>License plates sold per morning export run, a running average.</summary>
        public float platesPerDay;
        /// <summary>The fire brigade arrives at the tick (-1 none called).</summary>
        public int fireBrigadeTick = -1;
        /// <summary>Everyone who walked out (the latest 60), and news from those who stayed out.</summary>
        public List<FormerInmate> formerInmates = new List<FormerInmate>();
        /// <summary>The parents that were made for siblings and half-siblings (see <see cref="Kin"/>): world pawns kept while a child of theirs is here, let go when none is (the world pawn collector would discard them, and the siblings with them).</summary>
        public List<Pawn> kinParents = new List<Pawn>();
        /// <summary>Parole hearings scheduled in the hearing room, until the board has decided (see <see cref="ParoleScene"/>).</summary>
        public List<ParoleCase> paroleCases = new List<ParoleCase>();
        /// <summary>The state's oversight of the prison: special measures, its security chief, flagship status (see <see cref="SpecialMeasures"/>).</summary>
        public StateOversight oversight = new StateOversight();
        public List<StoryArc> arcs = new List<StoryArc>();
        public int nextArcId = 1;
        /// <summary>Who owes whom among the inmates (see <see cref="DebtSystem"/>), and the next debt's id.</summary>
        public List<InmateDebt> debts = new List<InmateDebt>();
        public int nextDebtId = 1;
        public Dictionary<StoryArcDef, int> lastArcStart = new Dictionary<StoryArcDef, int>();
        /// <summary>When the last shot-caller's deal with each gang ended, by any road (an arc is forgotten once it is over, and the five days before the next one are counted from here: see <see cref="GangDeals"/>).</summary>
        public Dictionary<GangDef, int> gangDealEnded = new Dictionary<GangDef, int>();
        /// <summary>Murder cases on their way to court, and complaints against officers (open, or decided in the last month), with the next id of each (see <see cref="Trials"/>, <see cref="ComplaintSystem"/>).</summary>
        public List<MurderTrial> trials = new List<MurderTrial>();
        public int nextTrialId = 1;
        public List<Complaint> complaints = new List<Complaint>();
        public int nextComplaintId = 1;

        public StoryArc ArcById(int id)
        {
            foreach (StoryArc a in arcs)
                if (a.id == id) return a;
            return null;
        }

        public bool InArc(Pawn p)
        {
            foreach (StoryArc a in arcs)
                if (!a.done && a.def.occupiesCast && a.pawns.Contains(p)) return true;
            return false;
        }

        /// <summary>Starts an arc unless it is at its limit, too soon, or someone in it is already in a story.</summary>
        public StoryArc TryStartArc(StoryArcDef def, params Pawn[] cast)
        {
            int now = Find.TickManager.TicksGame;
            int running = 0;
            foreach (StoryArc a in arcs)
                if (!a.done && a.def == def) running++;
            if (running >= def.maxConcurrent) return null;
            if (lastArcStart.TryGetValue(def, out int last) && now - last < def.minRefireDays * GenDate.TicksPerDay) return null;
            foreach (Pawn p in cast)
                if (p == null || InArc(p)) return null;
            var arc = new StoryArc { def = def, id = nextArcId++, startTick = now, nextTick = now };
            arc.pawns.AddRange(cast);
            arcs.Add(arc);
            lastArcStart[def] = now;
            return arc;
        }

        /// <summary>Someone left for good (released, transferred, dead): their stories end now.</summary>
        public void Notify_Leaving(Pawn p)
        {
            // (Before a release clears the stay: an approved restorative meeting is refunded.)
            Amends.Notify_Leaving(p);
            Mentors.Notify_Leaving(p);
            DebtSystem.Notify_Leaving(p);
            KeepApart.Notify_Leaving(p);
            InformantTask.Notify_Leaving(p);
            if (p.Inmate() != null) StaffWariness.Notify_InmateLeaving(p);
            OldAge.Notify_Leaving(this, p);
            WatchList.Notify_Leaving(p);
            foreach (StoryArc a in arcs)
                if (!a.done) a.def.Worker.Notify_Leaving(a, p);
            // (Last: a war arc ends with its leader, and the gang's next one is picked after that.)
            GangRanks.Notify_Leaving(p);
            if (p.MapHeld is not { } map) return;
            // Staff on their payroll lose a patron; nobody keeps a reference to a pawn about to be discarded. All of them, on site or at home: a night guard at home by day kept a
            // transferred patron the checks below never saw go (a discarded pawn is still a prisoner of the colony and not dead), and a save then dropped the reference.
            foreach (Pawn s in StaffCommute.AllStaff(map))
                if (s.GetComp<CompStaff>() is { } sc && sc.patron == p) CorruptionSystem.Notify_PatronGone(sc);
        }

        /// <summary>Hourly: advance every arc; cancel those whose people are gone; forget finished ones.</summary>
        public void HourlyArcs(Map map)
        {
            for (int i = arcs.Count - 1; i >= 0; i--)
            {
                StoryArc a = arcs[i];
                StepArc(a, map);
                if (a.done) arcs.RemoveAt(i);
            }
            HostageCrisis.Sweep(map);
        }

        /// <summary>One arc's hour: its worker's step while it is valid; cancelled when it is not.</summary>
        public static void StepArc(StoryArc a, Map map)
        {
            if (!a.done && a.def.Worker.Valid(a)) a.def.Worker.Hourly(a, map);
            else if (!a.done)
            {
                a.done = true;
                a.def.Worker.Cancelled(a);
            }
        }

        public void Add(ChronicleEntry e)
        {
            entries.Add(e);
            if (entries.Count <= MaxEntries) return;
            // Forget the oldest routine entry first; milestones are never forgotten.
            for (int imp = 1; imp <= 2; imp++)
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].importance == imp)
                    {
                        entries.RemoveAt(i);
                        return;
                    }
        }

        public static void AddLife(List<LifeEntry> life, LifeEntry e)
        {
            life.Add(e);
            if (life.Count <= MaxLife) return;
            int victim = 0;
            for (int i = 1; i < life.Count; i++)
                if (life[i].importance < life[victim].importance) victim = i;
            life.RemoveAt(victim);
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            Scribe_Collections.Look(ref firsts, "firsts", LookMode.Value);
            Scribe_Values.Look(ref admissions, "admissions");
            Scribe_Values.Look(ref dramaToday, "dramaToday");
            Scribe_Values.Look(ref beatsToday, "beatsToday");
            Scribe_Values.Look(ref decisionsToday, "decisionsToday");
            Scribe_Collections.Look(ref deferredLetters, "deferredLetters", LookMode.Deep);
            Scribe_Values.Look(ref lastRiotTick, "lastRiotTick", -1);
            Scribe_Values.Look(ref lastDeathTick, "lastDeathTick", -1);
            Scribe_Values.Look(ref lastHazardDemandTick, "lastHazardDemandTick", -999999);
            Scribe_Values.Look(ref boardTrust, "boardTrust", 1f);
            Scribe_Collections.Look(ref coverUps, "coverUps", LookMode.Deep);
            Scribe_Collections.Look(ref condolences, "condolences", LookMode.Deep);
            Scribe_Collections.Look(ref bitterDeaths, "bitterDeaths", LookMode.Value);
            Scribe_Collections.Look(ref funerals, "funerals", LookMode.Deep);
            Scribe_Collections.Look(ref funeralApart, "funeralApart", LookMode.Value);
            Scribe_Values.Look(ref lastInquiryVictim, "lastInquiryVictim");
            Scribe_Collections.Look(ref truces, "truces", LookMode.Deep);
            Scribe_Values.Look(ref outbreakUntilTick, "outbreakUntil", -1);
            Scribe_Values.Look(ref outbreakVaccinated, "outbreakVaccinated");
            Scribe_Values.Look(ref outbreakIsolate, "outbreakIsolate");
            Scribe_Values.Look(ref inspectionTick, "inspectionTick", -1);
            Scribe_Values.Look(ref inspectionPrep, "inspectionPrep");
            Scribe_Values.Look(ref bribeExposeTick, "bribeExposeTick", -1);
            Scribe_Deep.Look(ref inspection, "inspection");
            Scribe_Values.Look(ref lastHolidayYear, "lastHolidayYear", -1);
            Scribe_Values.Look(ref lastOfficerOfMonthDay, "lastOfficerOfMonthDay", -1);
            Scribe_Values.Look(ref weatherReliefMapId, "weatherReliefMap", -1);
            Scribe_Values.Look(ref weatherReliefStartTick, "weatherReliefStart", -1);
            Scribe_Values.Look(ref debtDays, "debtDays");
            Scribe_Values.Look(ref loanOwed, "loanOwed");
            Scribe_Values.Look(ref foodScareTick, "foodScareTick", -1);
            Scribe_Values.Look(ref foodScareChance, "foodScareChance");
            Scribe_Values.Look(ref kitchenStandDownUntilTick, "kitchenStandDownUntil", -1);
            Scribe_Values.Look(ref contractTarget, "contractTarget");
            Scribe_Values.Look(ref contractDelivered, "contractDelivered");
            Scribe_Values.Look(ref contractRunsLeft, "contractRunsLeft");
            Scribe_Values.Look(ref contractDueTick, "contractDueTick", -1);
            Scribe_Values.Look(ref lastExportRunDay, "lastExportRunDay", -1);
            Scribe_Values.Look(ref platesPerDay, "platesPerDay");
            Scribe_Values.Look(ref fireBrigadeTick, "fireBrigadeTick", -1);
            Scribe_Collections.Look(ref formerInmates, "formerInmates", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.Saving) kinParents?.RemoveAll(x => x == null || x.Destroyed || x.Discarded);
            Scribe_Collections.Look(ref kinParents, "kinParents", LookMode.Reference);
            Scribe_Collections.Look(ref paroleCases, "paroleCases", LookMode.Deep);
            Scribe_Deep.Look(ref oversight, "oversight");
            Scribe_Collections.Look(ref arcs, "arcs", LookMode.Deep);
            Scribe_Values.Look(ref nextArcId, "nextArcId", 1);
            Scribe_Collections.Look(ref debts, "debts", LookMode.Deep);
            Scribe_Values.Look(ref nextDebtId, "nextDebtId", 1);
            Scribe_Collections.Look(ref lastArcStart, "lastArcStart", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref gangDealEnded, "gangDealEnded", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref trials, "trials", LookMode.Deep);
            Scribe_Values.Look(ref nextTrialId, "nextTrialId", 1);
            Scribe_Collections.Look(ref complaints, "complaints", LookMode.Deep);
            Scribe_Values.Look(ref nextComplaintId, "nextComplaintId", 1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                entries ??= new List<ChronicleEntry>();
                entries.RemoveAll(e => e?.cat == null);
                firsts ??= new HashSet<string>();
                pulse ??= new PrisonPulse();
                coverUps ??= new List<CoverUp>();
                condolences ??= new List<Condolence>();
                condolences.RemoveAll(c => c?.kind == null);
                bitterDeaths ??= new List<int>();
                funerals ??= new List<Funeral>();
                funerals.RemoveAll(f => f == null || f.deadId < 0);
                funeralApart ??= new List<int>();
                deferredLetters ??= new List<ChoiceLetter_PrisonDecision>();
                deferredLetters.RemoveAll(d => d?.decision == null);
                formerInmates ??= new List<FormerInmate>();
                kinParents ??= new List<Pawn>();
                kinParents.RemoveAll(x => x == null);
                paroleCases ??= new List<ParoleCase>();
                paroleCases.RemoveAll(c => c == null || c.pawnId < 0);
                truces ??= new List<GangTruce>();
                truces.RemoveAll(t => t.a == null || t.b == null);
                debts ??= new List<InmateDebt>();
                debts.RemoveAll(d => d == null);
                oversight ??= new StateOversight();
                inspection ??= new InspectionState();
                arcs ??= new List<StoryArc>();
                arcs.RemoveAll(a => a?.def == null);
                lastArcStart ??= new Dictionary<StoryArcDef, int>();
                lastArcStart.RemoveAll(kv => kv.Key == null);
                gangDealEnded ??= new Dictionary<GangDef, int>();
                gangDealEnded.RemoveAll(kv => kv.Key == null);
                trials ??= new List<MurderTrial>();
                trials.RemoveAll(t => t == null);
                complaints ??= new List<Complaint>();
                complaints.RemoveAll(c => c == null);
            }
        }
    }

    public static class Story
    {
        /// <summary>
        /// Records a moment in the chronicle and in the life log of every inmate and member of staff in it. Only call
        /// this for things the warden knows (a letter, a message, something seen).
        /// </summary>
        public static void Record(ChronicleCategoryDef cat, int importance, TaggedString text, params Pawn[] pawns)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || cat == null) return;
            string resolved = text.Resolve();
            var e = new ChronicleEntry { tick = Find.TickManager.TicksGame, cat = cat, importance = Mathf.Clamp(importance, 1, 3), text = resolved };
            if (pawns != null)
                foreach (Pawn p in pawns)
                {
                    if (p == null) continue;
                    e.ids.Add(p.thingIDNumber);
                    List<LifeEntry> life = LifeOf(p);
                    if (life != null) StorySystem.AddLife(life, new LifeEntry { tick = e.tick, importance = e.importance, text = resolved });
                }
            game.story.Add(e);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:chronicle] {cat.defName} {importance} {resolved}");
        }

        /// <summary>Records only the first time <paramref name="key"/> happens (milestones: first riot, first death...).</summary>
        public static void RecordFirst(string key, ChronicleCategoryDef cat, TaggedString text, params Pawn[] pawns)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || !game.story.firsts.Add(key)) return;
            Record(cat, 3, text, pawns);
            Concepts.Notify_First(key);
        }

        /// <summary>Notes that <paramref name="key"/> has happened (the next <see cref="RecordFirst"/> of it is no longer a first) without a line of its own: the event has its line already.</summary>
        public static void MarkFirst(string key)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || !game.story.firsts.Add(key)) return;
            Concepts.Notify_First(key);
        }

        /// <summary>An arrival through the gate (intake, transfer, celebrity), with milestones for the first and every
        /// round number.</summary>
        public static void Admitted(Pawn p, string key, int importance = 1)
        {
            PrisonGame game = PrisonGame.Instance;
            CompInmate c = p.Inmate();
            if (game == null || c == null) return;
            string sentence = c.IsLifer ? "RP_Chr_LifeSentence".Translate().ToString() : "RP_Chr_Days".Translate(c.TermDays).ToString();
            Record(RPChronicleDefOf.RP_Chr_Intake, importance, key.Translate(p.LabelShort, c.crime?.label ?? "?", sentence, c.security?.label ?? "?"), p);
            int n = ++game.story.admissions;
            if (n == 1) RecordFirst("admit1", RPChronicleDefOf.RP_Chr_Intake, "RP_Chr_FirstInmate".Translate(p.LabelShort), p);
            else if (n == 10 || n == 50 || n % 100 == 0) RecordFirst("admit" + n, RPChronicleDefOf.RP_Chr_Intake, "RP_Chr_NthInmate".Translate(n, p.LabelShort), p);
        }

        /// <summary>Whose story: a member of staff's service record, else the inmate file. (Every humanlike carries both
        /// comps, so the inmate one cannot be tested for null.)</summary>
        public static List<LifeEntry> LifeOf(Pawn p) => p.GetComp<CompStaff>() is { role: not null } sc ? sc.life : p.Inmate()?.life;

        /// <summary>"Day 12, 14h".</summary>
        public static string When(int tick)
        {
            int day = ChronicleEntry.DayOf(tick), hour = ChronicleEntry.HourOf(tick);
            // (Before the game: the old warden's time, "6 days before day 1".)
            return day < 0 ? "RP_ChronicleWhenBefore".Translate(-day, hour) : "RP_ChronicleWhen".Translate(day + 1, hour);
        }
    }
}
