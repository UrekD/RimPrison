using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    [DefOf]
    public static class RPLibraryDefOf
    {
        public static NeedDef RP_Literacy;
        public static PrisonZoneDef RP_Library;
        public static ThingDef RP_BookShelf;
        public static JobDef RP_DeliverBooks;
        public static JobDef RP_ReadBook;
        public static ThoughtDef RP_FinishedBook;

        static RPLibraryDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPLibraryDefOf));
    }

    public class CompProperties_BookShelf : CompProperties
    {
        /// <summary>Books the shelf holds.</summary>
        public int capacity = 24;

        public CompProperties_BookShelf() => compClass = typeof(CompBookShelf);
    }

    /// <summary>A shelf of library books: the stock the book cart is loaded from (bought at midnight, see <see cref="LibrarySystem.Daily"/>).</summary>
    public class CompBookShelf : ThingComp
    {
        public int books;

        public CompProperties_BookShelf Props => (CompProperties_BookShelf)props;

        /// <summary>Room for more books.</summary>
        public int Room => Mathf.Max(0, Props.capacity - books);

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            PrisonMap.Of(parent.Map)?.RegisterShelf(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            PrisonMap.Of(map)?.DeregisterShelf(this);
        }

        public override void PostExposeData() => Scribe_Values.Look(ref books, "rpBooks");

        public override string CompInspectStringExtra() => "RP_BookShelfStock".Translate(books, Props.capacity);

        /// <summary>Takes up to <paramref name="n"/> books off the shelf; how many came.</summary>
        public int Take(int n)
        {
            int taken = Mathf.Clamp(n, 0, books);
            books -= taken;
            return taken;
        }
    }

    /// <summary>
    /// The library: shelves in the library zone, stocked at midnight, and a book cart pushed round the cells by a trusted inmate (the library
    /// clerk, minimum security like the mail clerk). Inmates who can read (the literacy need) and are running short of it get a book left at
    /// their cell door (or through it, when they are confined); they read it in their cell, which fills the need and, for the education programs,
    /// raises the chance of passing the exam. A donated book now and then has a drug hidden in it, which lands in the cell's stash for a cell search to find.
    /// </summary>
    public static class LibrarySystem
    {
        /// <summary>What a book costs the prison, paid out of supplies when the shelves are filled.</summary>
        public const int BookPrice = 4;
        /// <summary>Books the cart carries on one round.</summary>
        public const int CartSize = 4;
        /// <summary>Inmates this far below full literacy want a book.</summary>
        public const float WantsBelow = 0.6f;
        /// <summary>They put the book down once the need is this high.</summary>
        public const float StopReadingAt = 0.95f;
        /// <summary>A book is this many ticks of reading, and an hour-long sitting gives about <see cref="GainPerTick"/> x <see cref="ReadTicks"/> of the need.</summary>
        public const int BookTicks = 2500, ReadTicks = 1500;
        public const float GainPerTick = 0.00033f;
        /// <summary>Ticks to load the cart at the shelf, and to hand a book over at the door.</summary>
        public const int LoadTicks = 120, HandOverTicks = 90;
        /// <summary>Library books are donated, so fewer carry something than the post does (a share of the mail's chance).</summary>
        public const float ContrabandShare = 0.5f;
        /// <summary>Inmates per place on the cart's crew: one clerk for this many (at least one with a shelf).</summary>
        public const int InmatesPerClerk = 40;
        /// <summary>Ticks of reading in a sitting before it settles them a little.</summary>
        public const int CalmingTicks = 600;

        public static Need Literacy(Pawn p) => p.needs?.TryGetNeed(RPLibraryDefOf.RP_Literacy);

        /// <summary>How well they read, 0 to 1 (0 for somebody who cannot).</summary>
        public static float LiteracyLevel(Pawn p) => Literacy(p)?.CurLevel ?? 0f;

        private static bool InLibrary(CompBookShelf s, PrisonMap pm) => s.parent.Spawned && pm.zones.ZoneAt(s.parent.Position) == RPLibraryDefOf.RP_Library;

        public static int Shelves(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            int n = 0;
            if (pm != null)
                foreach (CompBookShelf s in pm.Shelves)
                    if (InLibrary(s, pm)) n++;
            return n;
        }

        /// <summary>Places on the library crew: one with any shelf in the library, another per <see cref="InmatesPerClerk"/> inmates, never more than there are shelves.</summary>
        public static int ClerkSlots(Map map)
        {
            int shelves = Shelves(map);
            return shelves == 0 ? 0 : Mathf.Min(shelves, 1 + map.mapPawns.PrisonersOfColonySpawnedCount / InmatesPerClerk);
        }

        /// <summary>Midnight: the shelves are filled up (as far as the funds go), at <see cref="BookPrice"/> a book. Returns the books bought.</summary>
        public static int Daily(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            PrisonGame game = PrisonGame.Instance;
            if (pm == null || game == null) return 0;
            int bought = 0;
            foreach (CompBookShelf s in pm.Shelves)
            {
                if (!InLibrary(s, pm)) continue;
                int count = Mathf.Min(s.Room, Mathf.Max(0, game.ledger.funds) / BookPrice);
                if (count <= 0 || !game.ledger.TrySpend(RPDefOf.RP_Supplies, count * BookPrice, "RP_BooksNote".Translate(count))) continue;
                s.books += count;
                bought += count;
            }
            return bought;
        }

        /// <summary>A shelf in the library with books on it that the clerk can walk to. (The clerk's round is assigned work, exempt from the rooms kept for a level, see <see cref="ZoneGrid.SetKept"/>.)</summary>
        public static CompBookShelf StockedShelf(Pawn clerk, PrisonMap pm)
        {
            CompBookShelf best = null;
            float bestDist = float.MaxValue;
            foreach (CompBookShelf s in pm.Shelves)
            {
                if (s.books <= 0 || !InLibrary(s, pm)) continue;
                float d = (s.parent.Position - clerk.Position).LengthHorizontalSquared;
                if (d >= bestDist || !clerk.CanReserveAndReach(s.parent, PathEndMode.InteractionCell, Danger.Some)) continue;
                best = s;
                bestDist = d;
            }
            return best;
        }

        /// <summary>Wants a book now: reads, is running short of it, has none in the cell and is somewhere a cart can find them.</summary>
        public static bool WantsBook(Pawn p, CompInmate c) =>
            c.books <= 0 && p.Spawned && !p.Downed && !p.InMentalState && InmateUtility.IsActiveInmate(p) && p.ownership?.OwnedBed != null
            && Literacy(p) is { CurLevel: < WantsBelow };

        /// <summary>Who the cart should go to first: the least literate, those confined to their cells (nothing else to do), those working toward a diploma or in an education class.</summary>
        public static float Priority(Pawn p, CompInmate c)
        {
            float score = 1f - LiteracyLevel(p);
            if (c.forcedActivity != null) score += 0.5f;
            if (c.want?.def.Worker is WantWorker_Diploma) score += 0.4f;
            foreach (KeyValuePair<ProgramDef, int> kv in c.programProgress)
                if (kv.Key.literacyBonus > 0f)
                {
                    score += 0.3f;
                    break;
                }
            return score;
        }

        /// <summary>Where the cart stops for this inmate: their cell's door (a book is passed through the hatch whether it is locked or not), or the bed when the cell has no door.</summary>
        public static Thing DeliveryPoint(Pawn p)
        {
            Building_Bed bed = p.ownership?.OwnedBed;
            PrisonMap pm = PrisonMap.Of(p.Map);
            if (bed == null || pm == null) return null;
            Room cell = bed.GetRoom();
            if (cell != null)
                foreach (CompPrisonDoor d in pm.Doors)
                    if (d.cellRoom == cell && d.parent.Spawned) return d.parent;
            return bed;
        }

        /// <summary>Inmates another clerk's round is already on its way to: the stop they are at and the stops still queued (those served hold a book and no longer want one).</summary>
        private static HashSet<Pawn> OnOtherRounds(Map map, Pawn clerk)
        {
            HashSet<Pawn> taken = null;
            foreach (Pawn other in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (other == clerk || other.CurJob is not { } job || job.def != RPLibraryDefOf.RP_DeliverBooks) continue;
                taken ??= new HashSet<Pawn>();
                if (job.targetB.Pawn is { } here) taken.Add(here);
                foreach (LocalTargetInfo stop in job.GetTargetQueue(TargetIndex.B))
                    if (stop.Pawn is { } queued) taken.Add(queued);
            }
            return taken;
        }

        /// <summary>
        /// Up to <paramref name="max"/> inmates for one round, highest priority first (<see cref="Priority"/>). With a clerk, only those whose stop the clerk can
        /// walk to, and not those another clerk's round is already on its way to (the same four would be left a book once and passed by the second cart).
        /// </summary>
        public static List<Pawn> Recipients(Map map, Pawn clerk, int max)
        {
            HashSet<Pawn> taken = clerk == null ? null : OnOtherRounds(map, clerk);
            var ranked = new List<(Pawn p, float score)>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p != clerk && p.Inmate() is { } c && WantsBook(p, c) && taken?.Contains(p) != true) ranked.Add((p, Priority(p, c)));
            ranked.Sort((a, b) => b.score.CompareTo(a.score));
            var chosen = new List<Pawn>();
            foreach ((Pawn p, _) in ranked)
            {
                if (chosen.Count >= max) break;
                if (clerk != null && (DeliveryPoint(p) is not { } stop || !clerk.CanReach(stop, PathEndMode.Touch, Danger.Deadly))) continue;
                chosen.Add(p);
            }
            return chosen;
        }

        /// <summary>The clerk has a round to make: a stocked shelf they can walk to and somebody who wants a book at a door they can walk to (what <see cref="MakeRounds"/> would find).</summary>
        public static bool HasRounds(Pawn clerk)
        {
            PrisonMap pm = PrisonMap.Of(clerk.Map);
            return pm != null && StockedShelf(clerk, pm) != null && Recipients(clerk.Map, clerk, 1).Count > 0;
        }

        /// <summary>The recipients in the order the cart walks them: the nearest stop next, from <paramref name="from"/> (by where the cart stops, their cell door, not where they stand now).</summary>
        public static List<Pawn> Route(IntVec3 from, List<Pawn> recipients)
        {
            var left = new List<(Pawn p, IntVec3 at)>();
            foreach (Pawn p in recipients) left.Add((p, DeliveryPoint(p)?.Position ?? p.Position));
            var ordered = new List<Pawn>();
            IntVec3 here = from;
            while (left.Count > 0)
            {
                int next = 0;
                for (int i = 1; i < left.Count; i++)
                    if ((left[i].at - here).LengthHorizontalSquared < (left[next].at - here).LengthHorizontalSquared) next = i;
                ordered.Add(left[next].p);
                here = left[next].at;
                left.RemoveAt(next);
            }
            return ordered;
        }

        /// <summary>One round of the cart: load at the shelf, then on to each cell on the list.</summary>
        public static Job MakeRounds(Pawn clerk, PrisonMap pm)
        {
            CompBookShelf shelf = StockedShelf(clerk, pm);
            if (shelf == null) return null;
            List<Pawn> route = Recipients(clerk.Map, clerk, Mathf.Min(CartSize, shelf.books));
            if (route.Count == 0) return null;
            List<Pawn> ordered = Route(shelf.parent.Position, route);
            Job job = JobMaker.MakeJob(RPLibraryDefOf.RP_DeliverBooks, shelf.parent);
            foreach (Pawn r in ordered) job.AddQueuedTarget(TargetIndex.B, r);
            return job;
        }

        /// <summary>The chance a donated book hides something, for this inmate (dealers, gangs and drug offenders more likely).</summary>
        public static float ContrabandChance(CompInmate c) => MailSystem.ContrabandChance(c) * ContrabandShare;

        /// <summary>A book is handed over at the cell. <paramref name="hides"/> stands in for the dice (tests). False when they no longer need one.</summary>
        public static bool Deliver(Pawn clerk, Pawn p, bool? hides = null)
        {
            if (p.Inmate() is not { } c || Literacy(p) == null || c.books > 0) return false;
            c.books++;
            PrisonGame.Instance.stats.booksDelivered++;
            if (hides ?? Rand.Chance(ContrabandChance(c))) Hide(p, c);
            return true;
        }

        /// <summary>
        /// Something tucked in the book's spine: a drug, as in the post (no tools or phones that nothing screens a book for), which goes into the cell's stash, found by a
        /// cell search (or used in there). For somebody who arranged a delivery to pay a debt it goes to the creditor, as a letter's does.
        /// </summary>
        public static Thing Hide(Pawn p, CompInmate c)
        {
            ThingDef def = ContrabandUtility.PickSmuggled(p, ContrabandKind.Drug);
            if (def == null) return null;
            PrisonGame game = PrisonGame.Instance;
            Thing t;
            if (DebtSystem.Smuggling(c)) t = ContrabandUtility.Give(p, def, forDebt: true);
            else
            {
                t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                t.stackCount = Mathf.Min(ContrabandUtility.Ext(def).stackCount, def.stackLimit);
                if (!c.stash.TryAdd(t))
                {
                    t.Destroy();
                    return null;
                }
                game.stats.contrabandSmuggled++;
                game.today.contrabandSmuggled++;
            }
            if (t == null) return null;
            game.stats.bookContraband++;
            return t;
        }

        /// <summary>Reading for <paramref name="delta"/> ticks: the need rises; a book is used up every <see cref="BookTicks"/>.</summary>
        public static void ReadTick(Pawn p, CompInmate c, int delta)
        {
            if (Literacy(p) is { } need) need.CurLevel += GainPerTick * delta;
            p.skills?.Learn(SkillDefOf.Intellectual, 0.02f * delta);
            c.bookTicks += delta;
            if (c.bookTicks < BookTicks) return;
            c.bookTicks = 0;
            if (c.books > 0) c.books--;
            PrisonGame.Instance.stats.booksRead++;
            p.needs.mood?.thoughts.memories.TryGainMemory(RPLibraryDefOf.RP_FinishedBook);
        }

        /// <summary>In their own cell with a book and the need not met: read.</summary>
        public static Job TryRead(Pawn p, CompInmate c)
        {
            if (c.books <= 0 || p.Downed || p.InMentalState || Literacy(p) is not { CurLevel: < StopReadingAt }) return null;
            Room cell = InmateUtility.CellOf(p);
            return cell != null && p.GetRoom() == cell ? JobMaker.MakeJob(RPLibraryDefOf.RP_ReadBook) : null;
        }
    }

    /// <summary>The library clerk: pushes the book cart round the cells during work hours.</summary>
    public class InmateJobWorker_Library : InmateJobWorker
    {
        public override int Capacity(Map map) => LibrarySystem.ClerkSlots(map);

        /// <summary>Books to hand out: a round waits for the clerk, so a class does not take their work hours.</summary>
        public override bool HasWork(Pawn inmate) => LibrarySystem.HasRounds(inmate);

        public override Job TryGiveJob(Pawn inmate, PrisonMap pm) => LibrarySystem.MakeRounds(inmate, pm);
    }

    /// <summary>
    /// A round of the book cart: load books at the shelf, then for each inmate on the list walk to the door of their cell and hand one over. What is
    /// left on the cart when the job ends goes back on the shelf.
    /// </summary>
    public class JobDriver_DeliverBooks : JobDriver
    {
        private int carrying, handedOver;

        private CompBookShelf Shelf => job.targetA.Thing?.TryGetComp<CompBookShelf>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref carrying, "carrying");
            Scribe_Values.Look(ref handedOver, "handedOver");
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            this.AddFinishAction(c =>
            {
                if (carrying > 0 && Shelf is { } shelf) shelf.books += carrying;
                carrying = 0;
                if (AutoTest.Active) RPLog.Message($"[RimPrison:booktrip] {pawn.LabelShort}'s round ends {c} after {Find.TickManager.TicksGame - job.startTick} ticks, {handedOver} books handed over");
            });
            Toil load = Toils_General.Wait(LibrarySystem.LoadTicks);
            load.WithProgressBarToilDelay(TargetIndex.A);
            Toil take = ToilMaker.MakeToil("LoadCart");
            take.initAction = () =>
            {
                List<LocalTargetInfo> stops = job.GetTargetQueue(TargetIndex.B);
                carrying = Shelf?.Take(Mathf.Min(stops.Count, LibrarySystem.CartSize)) ?? 0;
                if (carrying == 0) EndJobWith(JobCondition.Incompletable);
            };
            Toil next = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.B);
            Toil more = Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.B, next);
            Toil pick = ToilMaker.MakeToil("FindTheDoor");
            pick.initAction = () =>
            {
                Pawn recipient = job.targetB.Pawn;
                Thing stop = recipient == null ? null : LibrarySystem.DeliveryPoint(recipient);
                if (carrying <= 0 || stop == null || !pawn.CanReach(stop, PathEndMode.Touch, Danger.Deadly)) JumpToToil(more);
                else job.SetTarget(TargetIndex.C, stop);
            };
            Toil hand = Toils_General.Wait(LibrarySystem.HandOverTicks);
            hand.WithProgressBarToilDelay(TargetIndex.C);
            Toil give = ToilMaker.MakeToil("HandOverBook");
            give.initAction = () =>
            {
                if (carrying > 0 && job.targetB.Pawn is { } recipient && LibrarySystem.Deliver(pawn, recipient))
                {
                    carrying--;
                    handedOver++;
                }
            };
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);
            yield return load;
            yield return take;
            yield return next;
            yield return pick;
            yield return Toils_Goto.GotoThing(TargetIndex.C, PathEndMode.Touch);
            yield return hand;
            yield return give;
            yield return more;
        }
    }

    /// <summary>Reading a library book in the cell, a sitting at a time.</summary>
    public class JobDriver_ReadBook : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => pawn.Inmate() is not { books: > 0 });
            Toil read = Toils_General.Wait(LibrarySystem.ReadTicks);
            read.tickIntervalAction = delta =>
            {
                if (pawn.Inmate() is not { } c) return;
                LibrarySystem.ReadTick(pawn, c, delta);
                if (LibrarySystem.LiteracyLevel(pawn) >= LibrarySystem.StopReadingAt) ReadyForNextToil();
            };
            read.AddFinishAction(() =>
            {
                if (pawn.Inmate() is { } c && Find.TickManager.TicksGame - job.startTick >= LibrarySystem.CalmingTicks) c.tension = Mathf.Max(0f, c.tension - 1.5f);
            });
            yield return read;
        }
    }
}
