using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimPrison
{
    /// <summary>One person who applied for a post: who, for what, when, and in which recruitment round (the pawn is held by <see cref="ApplicantPool"/>).</summary>
    public class Applicant : IExposable
    {
        public Pawn pawn;
        public StaffRoleDef role;
        public int appliedTick;
        public int round;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Defs.Look(ref role, "role");
            Scribe_Values.Look(ref appliedTick, "appliedTick");
            Scribe_Values.Look(ref round, "round");
        }
    }

    /// <summary>The recruitment round a post's applicants were made in, and how many of them there were (a hire leaves fewer in the pool, never more made).</summary>
    public class RecruitmentRound : IExposable
    {
        public StaffRoleDef role;
        public int round;
        public int made;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref role, "role");
            Scribe_Values.Look(ref round, "round");
            Scribe_Values.Look(ref made, "made");
        }
    }

    /// <summary>
    /// The people who applied for the posts the warden opened (see <see cref="Dialog_Applicants"/>): at most <see cref="PerRole"/> per post, made when the warden opens a post in a recruitment
    /// round that has none yet, and gone with the round. The pawns are held here, deep-saved, as the staff at home are in <see cref="PrisonGame.home"/>: not world pawns, not on a map.
    /// </summary>
    public class ApplicantPool : IExposable, IThingHolder
    {
        public const int PerRole = 3, RoundDays = 3, RoundTicks = RoundDays * GenDate.TicksPerDay;

        /// <summary>A check's seam: the tick the pool goes by (null: the game's own), to run a later round without the days.</summary>
        internal static int? TickOverride;

        public static int Now => TickOverride ?? Find.TickManager.TicksGame;

        public static int RoundAt(int tick) => tick / RoundTicks;

        public static int NextRoundTick(int tick) => (RoundAt(tick) + 1) * RoundTicks;

        private ThingOwner<Pawn> held;
        public List<Applicant> entries = new List<Applicant>();
        public List<RecruitmentRound> rounds = new List<RecruitmentRound>();
        /// <summary>Counts every change (an applicant made, hired or gone): a window's rows are built again when it moves.</summary>
        [Unsaved] public int version;

        public ApplicantPool()
        {
            held = new ThingOwner<Pawn>(this);
        }

        public IThingHolder ParentHolder => PrisonGame.Instance;

        public ThingOwner GetDirectlyHeldThings() => held;

        public void GetChildHolders(List<IThingHolder> outChildren) => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());

        public int Count => entries.Count;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref held, "held", this);
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            Scribe_Collections.Look(ref rounds, "rounds", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                held ??= new ThingOwner<Pawn>(this);
                entries ??= new List<Applicant>();
                rounds ??= new List<RecruitmentRound>();
                // (A post the mod no longer has, or an entry whose pawn did not load: nothing to hire. The pawn left in the holder is let go by the next daily pass.)
                entries.RemoveAll(a => a?.pawn == null || a.role == null);
                rounds.RemoveAll(r => r?.role == null);
            }
        }

        /// <summary>The applicants for a post, in the order they applied.</summary>
        public List<Applicant> For(StaffRoleDef role) => entries.Where(a => a.role == role).ToList();

        public bool Holds(Pawn p) => p != null && held.Contains(p);

        private RecruitmentRound RoundOf(StaffRoleDef role)
        {
            for (int i = 0; i < rounds.Count; i++)
                if (rounds[i].role == role) return rounds[i];
            return null;
        }

        /// <summary>How many applicants this post's round made (more than are left once some were hired); 0 when its pool is not made for the round now.</summary>
        public int MadeThisRound(StaffRoleDef role) => RoundOf(role) is { } r && r.round == RoundAt(Now) ? r.made : 0;

        /// <summary>
        /// The warden opened a post. Its pool for this round is made now, from the generator, unless it was made already (a pool that is empty because it was hired out or because nobody
        /// could do the work stays empty until the next round: opening again is no reroll). Returns whether a pool was made. Draws no random numbers when it was not.
        /// </summary>
        public bool Open(StaffRoleDef role)
        {
            int now = Now, round = RoundAt(now);
            RecruitmentRound made = RoundOf(role);
            if (made != null && made.round == round) return false;
            DiscardWhere(a => a.role == role);
            if (made == null) rounds.Add(made = new RecruitmentRound { role = role });
            made.round = round;
            made.made = 0;
            for (int i = 0; i < PerRole; i++)
            {
                if (!StaffUtility.TryGenerateCandidate(role, out Pawn p, keepAdaptation: true))
                {
                    // (The generator gave up: the last one it made could not do the work and is let go. No more are tried this round.)
                    Release(p);
                    break;
                }
                if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
                held.TryAdd(p, canMergeWithExistingStacks: false);
                entries.Add(new Applicant { pawn = p, role = role, appliedTick = now, round = round });
                made.made++;
            }
            version++;
            return true;
        }

        /// <summary>
        /// Hires this very pawn (their fee, the ordinary hire's everything, the roll of kin): false with nothing changed when they are gone already (hired, or from a round that is over),
        /// the post is locked or the funds are short.
        /// </summary>
        public bool TryHire(Map map, Applicant applicant)
        {
            if (applicant?.pawn == null || !entries.Contains(applicant) || !held.Contains(applicant.pawn))
            {
                Messages.Message("RP_ApplicantGone".Translate(applicant?.pawn?.LabelShort ?? "?"), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            StaffRoleDef role = applicant.role;
            if (applicant.round != RoundAt(Now))
            {
                // (Their round is over and the daily pass has not come yet.)
                Messages.Message("RP_ApplicantGone".Translate(applicant.pawn.LabelShort), MessageTypeDefOf.RejectInput, historical: false);
                DiscardWhere(a => a.round != RoundAt(Now));
                return false;
            }
            if (!Unlocks.IsUnlocked(role)) return false;
            if (!PrisonGame.Instance.ledger.CanAfford(role.hireCost))
            {
                Messages.Message("RP_CannotAfford".Translate(Money.Format(role.hireCost)), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            Pawn p = applicant.pawn;
            // (Out of the pool first: a pawn in a holder cannot be spawned.)
            held.Remove(p);
            entries.Remove(applicant);
            if (!StaffUtility.Hire(map, role, free: false, p, applicant: true))
            {
                held.TryAdd(p, canMergeWithExistingStacks: false);
                entries.Add(applicant);
                return false;
            }
            version++;
            return true;
        }

        /// <summary>The daily pass: the applicants of a past round go (the pool never holds more than <see cref="PerRole"/> a post, and old ones do not pile up in the saves), and so does anybody the pool holds without an entry.</summary>
        public void Daily()
        {
            int round = RoundAt(Now);
            DiscardWhere(a => a.round < round || a.pawn == null || a.role == null || !held.Contains(a.pawn));
            if (held.Count == entries.Count) return;
            var listed = new HashSet<Pawn>(entries.Select(a => a.pawn));
            foreach (Pawn p in held.InnerListForReading.ToList())
                if (!listed.Contains(p))
                {
                    held.Remove(p);
                    Release(p);
                    version++;
                }
        }

        /// <summary>Lets everybody go (a check's cleanup).</summary>
        internal void Clear()
        {
            DiscardWhere(_ => true);
            rounds.Clear();
            foreach (Pawn p in held.InnerListForReading.ToList())
            {
                held.Remove(p);
                Release(p);
            }
            version++;
        }

        private void DiscardWhere(System.Predicate<Applicant> test)
        {
            bool any = false;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Applicant a = entries[i];
                if (!test(a)) continue;
                entries.RemoveAt(i);
                any = true;
                if (a.pawn == null) continue;
                if (held.Contains(a.pawn)) held.Remove(a.pawn);
                Release(a.pawn);
            }
            if (any) version++;
        }

        /// <summary>A pawn nobody holds any more is discarded: no world pawn, and the relations they might have are cleared (see <see cref="WorldPawns"/>'s discard).</summary>
        internal static void Release(Pawn p)
        {
            if (p == null || p.Destroyed || p.Discarded || p.Spawned || p.holdingOwner != null) return;
            if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
            Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.Discard);
        }

        /// <summary>Every applicant as one comparable string (the saveload plan).</summary>
        public string Describe() =>
            $"{entries.Count}/{held.Count}:" + string.Join(",", entries.OrderBy(a => a.pawn?.thingIDNumber ?? -1).Select(a =>
                $"{a.role?.defName}:{a.pawn?.thingIDNumber}:{a.pawn?.Name?.ToStringFull}:{string.Join("+", a.pawn?.skills?.skills.Select(s => $"{s.def.defName}{s.Level}{s.passion}") ?? Enumerable.Empty<string>())}:{string.Join("+", a.pawn?.story?.traits?.allTraits.Select(t => $"{t.def.defName}{t.Degree}") ?? Enumerable.Empty<string>())}:{a.appliedTick}:{a.round}:{held.Contains(a.pawn)}"))
            + "/" + string.Join(",", rounds.OrderBy(r => r.role.defName).Select(r => $"{r.role.defName}:{r.round}:{r.made}"));
    }
}
