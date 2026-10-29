using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPFormerDefOf
    {
        public static ThoughtDef RP_FriendDoingWell;
        public static ThoughtDef RP_ThankedByFormer;

        static RPFormerDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPFormerDefOf));
    }

    /// <summary>Someone who did their time here: the record the prison keeps once they have gone (strings and ids, no pawn).</summary>
    public class FormerInmate : IExposable
    {
        public string name, crime, news, trustedName;
        /// <summary>A friend of theirs from inside who has died and been buried in the prison plot, while their news was still to come (their news says so).</summary>
        public string graveName;
        public int number, releasedTick, newsTick = -1, trustedId = -1;
        public float daysServed;
        public bool paroled, jobWaiting;
        /// <summary>Parole terms (see <see cref="ParoleLife"/>): supervised until the tick (-1: none, or an old entry), with a job and an address already in place or still to find.</summary>
        public int paroleEndTick = -1;
        public bool jobSecured, addressSecured;
        /// <summary>The address is lodging the prison paid for (see <see cref="ReleasePrep"/>), not their family's.</summary>
        public bool lodged;
        /// <summary>The supervision case that follows this release (see <see cref="Supervision"/>; -1: none, an old entry or a release that was settled at the gate).</summary>
        public int supervisionId = -1;
        /// <summary>The trade (the program that certified it; shown by its label, in the language of the day) that found them the job they have on parole, when a certified trade did and none was waiting; else null.</summary>
        public ProgramDef tradeJob;
        public List<int> friendIds = new List<int>();
        /// <summary>The world pawn, forcefully kept while there is something to come back for (see <see cref="ExInmates"/>); null when none is kept (or an old entry).</summary>
        public Pawn pawn;
        /// <summary>What the prison knew of them at the gate, for the application and the hire (null on an old entry: they never apply).</summary>
        public ExInmatePast past;
        /// <summary>Asked for work (the letter is out or was answered), turned down (never asked again), hired (they are staff now).</summary>
        public bool applied, declined, hired;
        /// <summary>The last time they came to visit a friend inside (-1 never).</summary>
        public int lastVisitTick = -1;
        /// <summary>Why they were let go when it was not the end of the sentence or parole (a keyed string: compassionate release), or null.</summary>
        public string cause;

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref crime, "crime");
            Scribe_Values.Look(ref news, "news");
            Scribe_Values.Look(ref trustedName, "trustedName");
            Scribe_Values.Look(ref graveName, "graveName");
            Scribe_Values.Look(ref number, "number");
            Scribe_Values.Look(ref releasedTick, "released");
            Scribe_Values.Look(ref newsTick, "newsTick", -1);
            Scribe_Values.Look(ref trustedId, "trustedId", -1);
            Scribe_Values.Look(ref daysServed, "days");
            Scribe_Values.Look(ref paroled, "paroled");
            Scribe_Values.Look(ref jobWaiting, "jobWaiting");
            Scribe_Values.Look(ref paroleEndTick, "paroleEnd", -1);
            Scribe_Values.Look(ref jobSecured, "jobSecured");
            Scribe_Values.Look(ref addressSecured, "addressSecured");
            Scribe_Values.Look(ref lodged, "lodged");
            Scribe_Values.Look(ref supervisionId, "supervisionId", -1);
            Scribe_Defs.Look(ref tradeJob, "tradeJob");
            Scribe_Collections.Look(ref friendIds, "friends", LookMode.Value);
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Deep.Look(ref past, "past");
            Scribe_Values.Look(ref applied, "applied");
            Scribe_Values.Look(ref declined, "declined");
            Scribe_Values.Look(ref hired, "hired");
            Scribe_Values.Look(ref lastVisitTick, "lastVisit", -1);
            Scribe_Values.Look(ref cause, "cause");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) friendIds ??= new List<int>();
        }
    }

    /// <summary>
    /// Life after release: the prison keeps a register of everyone who walked out, and for those who stay out, word comes
    /// back a week or two later: a job, a family, sometimes a letter thanking the officer they trusted. Friends still
    /// inside are glad of it; the officer is too. (Reoffenders' news comes as the rearrest.)
    /// </summary>
    public static class FormerInmates
    {
        public const int Max = 60, NewsMinDays = 5, NewsMaxDays = 15;

        /// <summary>At release, before relations and mentorships are wound up: who they leave behind and who they trusted.</summary>
        public static FormerInmate Notify_Released(PrisonGame game, Pawn p, CompInmate c, bool paroled, bool reoffends)
        {
            if (c == null) return null;
            var e = new FormerInmate
            {
                name = p.LabelShort,
                number = c.inmateNumber,
                crime = c.crime?.label,
                daysServed = c.DaysServed,
                releasedTick = Find.TickManager.TicksGame,
                paroled = paroled,
                jobWaiting = c.jobWaiting,
            };
            if (paroled) ParoleLife.Impose(e, c);
            if (p.MapHeld != null)
                foreach (Pawn o in p.MapHeld.mapPawns.PrisonersOfColonySpawned)
                    if (o != p && (PrisonRelations.Friends(o, p) || o.Inmate()?.mentorId == p.thingIDNumber || c.mentorId == o.thingIDNumber)) e.friendIds.Add(o.thingIDNumber);
            if (StaffMemories.MostTrusted(c) is { } t)
            {
                e.trustedId = t.staffId;
                e.trustedName = t.name;
            }
            ExInmates.Notify_Released(e, p, c, reoffends);
            if (!reoffends) e.newsTick = Find.TickManager.TicksGame + Rand.RangeInclusive(NewsMinDays, NewsMaxDays) * GenDate.TicksPerDay;
            game.story.formerInmates.Add(e);
            if (game.story.formerInmates.Count > Max)
            {
                // The oldest with no news still to come goes first: never drop a letter that is on its way (when every entry has news due the
                // register runs over the cap for a few days: news is due within NewsMaxDays, so it cannot grow without end).
                List<FormerInmate> list = game.story.formerInmates;
                int drop = list.FindIndex(x => x.newsTick < 0);
                if (drop >= 0)
                {
                    ExInmates.Unkeep(list[drop]);
                    list.RemoveAt(drop);
                }
            }
            ExInmates.EnforceCap(game);
            return e;
        }

        /// <summary>Hourly: news from outside that is due.</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            int now = Find.TickManager.TicksGame;
            foreach (FormerInmate e in game.story.formerInmates)
            {
                if (e.newsTick < 0 || now < e.newsTick) continue;
                // (Word of how they are getting on waits while a setback is open: a job lost is not "working, and staying out of trouble".)
                if (Supervision.SetbackOpen(game, e))
                {
                    e.newsTick = now + GenDate.TicksPerDay;
                    continue;
                }
                Deliver(game, map, e);
            }
        }

        public static void Deliver(PrisonGame game, Map map, FormerInmate e)
        {
            e.newsTick = -1;
            // A friend of theirs buried in the plot comes first (it is the one thing they would write about); then a job lined up; then a thank-you to the officer they trusted; else the family, or quiet.
            Pawn trusted = e.trustedId < 0 ? null : StaffCommute.AllStaff(map).FirstOrDefault(s => s.thingIDNumber == e.trustedId);
            e.news = e.graveName != null ? "RP_FormerNews_Grave" : e.jobWaiting ? "RP_FormerNews_Job" : e.tradeJob != null ? "RP_FormerNews_Trade" : trusted != null ? "RP_FormerNews_Thanks" : ParoleLife.Supervised(e, Find.TickManager.TicksGame) && Rand.Chance(0.5f) ? "RP_FormerNews_ParoleReport"
                : Rand.Chance(0.6f) ? "RP_FormerNews_Family" : "RP_FormerNews_Quiet";
            TaggedString text = e.news.Translate(e.name, e.trustedName ?? "?", e.graveName ?? "?", e.tradeJob?.label ?? "?");
            bool grave = e.graveName != null;
            e.graveName = null;
            game.stats.formerNews++;
            Messages.Message("RP_NewsFromOutside".Translate(text), grave ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.PositiveEvent);
            Story.Record(RPChronicleDefOf.RP_Chr_Outside, 1, "RP_Chr_NewsFromOutside".Translate(text));
            // (Word of a friend's grave is not a friend doing well.)
            if (!grave)
                foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                    if (e.friendIds.Contains(p.thingIDNumber)) p.needs?.mood?.thoughts.memories.TryGainMemory(RPFormerDefOf.RP_FriendDoingWell);
            if (e.news == "RP_FormerNews_Thanks") trusted?.needs?.mood?.thoughts.memories.TryGainMemory(RPFormerDefOf.RP_ThankedByFormer);
        }
    }
}
