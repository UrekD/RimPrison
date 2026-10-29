using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// A story with more than one step (a feud, later debts, parole stories...): it starts from something that
    /// happened, may ask the warden to choose, and plays out over hours or days. The worker owns the behaviour; the
    /// arc only carries its cast and progress. Arcs end on their own or when someone in them leaves.
    /// </summary>
    public class StoryArcDef : Def
    {
        public System.Type workerClass = typeof(StoryArcWorker);
        public int maxConcurrent = 3;
        public float minRefireDays;
        /// <summary>Hours the warden has to answer the arc's decision letter.</summary>
        public int decisionHours = 12;
        /// <summary>The arc's decision letters warn of something that happens on the arc's own clock (a feud's flashpoint, a war's first blow): never held back by the day's budget of decisions.</summary>
        public bool urgent;
        /// <summary>Whether the people it is cast with are busy in a story (<see cref="StorySystem.InArc"/>); a background arc such as a smuggling ring is not.</summary>
        public bool occupiesCast = true;

        [Unsaved] private StoryArcWorker workerInt;

        public StoryArcWorker Worker
        {
            get
            {
                if (workerInt == null)
                {
                    workerInt = (StoryArcWorker)System.Activator.CreateInstance(workerClass);
                    workerInt.def = this;
                }
                return workerInt;
            }
        }
    }

    public class StoryArc : IExposable
    {
        public StoryArcDef def;
        public int id;
        public List<Pawn> pawns = new List<Pawn>();
        public int stage;
        public int startTick;
        /// <summary>When the arc's next step is due (meaning depends on the worker and stage).</summary>
        public int nextTick;
        public bool done;
        public string note;
        /// <summary>Steps taken so far (meaning depends on the worker), and a tick the worker keeps (-1 none).</summary>
        public int count;
        public int markTick = -1;
        /// <summary>A place the arc is about, as a zone patch's anchor cell index (-1 none; the gang war's turf, see <see cref="GangTurf"/>).</summary>
        public int stake = -1;
        /// <summary>A smuggling ring's links and heat (null for every other arc; see <see cref="SmugglingRing"/>).</summary>
        public SmugglingRing ring;
        /// <summary>A shot-caller's deal (null for every other arc; see <see cref="GangDeal"/>).</summary>
        public GangDeal deal;

        public Pawn A => pawns.Count > 0 ? pawns[0] : null;
        public Pawn B => pawns.Count > 1 ? pawns[1] : null;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref id, "id");
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Reference);
            Scribe_Values.Look(ref stage, "stage");
            Scribe_Values.Look(ref startTick, "start");
            Scribe_Values.Look(ref nextTick, "next");
            Scribe_Values.Look(ref done, "done");
            Scribe_Values.Look(ref note, "note");
            Scribe_Values.Look(ref count, "count");
            Scribe_Values.Look(ref markTick, "markTick", -1);
            Scribe_Values.Look(ref stake, "stake", -1);
            Scribe_Deep.Look(ref ring, "ring");
            Scribe_Deep.Look(ref deal, "deal");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) pawns ??= new List<Pawn>();
        }
    }

    public class StoryArcWorker
    {
        public StoryArcDef def;

        /// <summary>
        /// Everyone in it is still an inmate here (the default for inmate stories). Being carried (to solitary, to a
        /// bed) or a prison break does not end a story; release, transfer and death do.
        /// </summary>
        public virtual bool Valid(StoryArc arc)
        {
            foreach (Pawn p in arc.pawns)
                if (p == null || p.Dead || !p.SpawnedOrAnyParentSpawned || !p.IsPrisonerOfColony || p.guest.Released || p.Inmate() == null) return false;
            return true;
        }

        public virtual void Hourly(StoryArc arc, Map map)
        {
        }

        /// <summary>The arc stopped because someone left (released, transferred, died).</summary>
        public virtual void Cancelled(StoryArc arc)
        {
        }

        /// <summary>
        /// Someone is leaving for good (released, transferred, dead, escaped). By default that ends every arc they
        /// are cast in; an arc that outlives its cast overrides this.
        /// </summary>
        public virtual void Notify_Leaving(StoryArc arc, Pawn p)
        {
            if (!arc.pawns.Contains(p)) return;
            arc.done = true;
            Cancelled(arc);
        }

        public virtual IReadOnlyList<string> Options(StoryArc arc) => null;

        public virtual string Disabled(StoryArc arc, int option) => null;

        public virtual void Choose(StoryArc arc, Map map, int option)
        {
        }

        public virtual int TimeoutChoice(StoryArc arc) => 0;

        /// <summary>Sends the arc's decision letter (options from <see cref="Options"/>).</summary>
        protected void AskWarden(StoryArc arc, Map map, TaggedString label, TaggedString text)
        {
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Arc", label, text, map, new LookTargets(arc.pawns));
            letter.arcId = arc.id;
            letter.pawns.AddRange(arc.pawns);
            letter.StartTimeout(GenDate.TicksPerHour * def.decisionHours);
            Find.LetterStack.ReceiveLetter(letter);
        }
    }

    /// <summary>Routes an arc's decision letter to the arc's worker.</summary>
    public class PrisonDecisionWorker_Arc : PrisonDecisionWorker
    {
        private static StoryArc ArcOf(ChoiceLetter_PrisonDecision l) => PrisonGame.Instance?.story.ArcById(l.arcId);

        // (A letter of an arc that is over offers nothing to choose: a done arc stays in the list for a while.)
        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) =>
            ArcOf(l) is { done: false } arc ? arc.def.Worker.Options(arc) ?? NoArc : NoArc;

        private static readonly string[] NoArc = { "RP_ArcOver" };

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option) => ArcOf(l) is { } arc ? arc.def.Worker.Disabled(arc, option) : null;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            if (ArcOf(l) is { done: false } arc) arc.def.Worker.Choose(arc, map, option);
        }

        public override int TimeoutOption(ChoiceLetter_PrisonDecision l) => ArcOf(l) is { } arc ? arc.def.Worker.TimeoutChoice(arc) : 0;
    }
}
