using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// One keep-apart order (gap analysis 6, 1): the warden's word that two inmates are not to be put together. Kept as thing ids and names, so the order outlives the pawn in a file or a chronicle line; the
    /// pawns themselves are looked up once (see <see cref="KeepApartBook.EnsureResolved"/>) and are not saved. The reason is a key (<see cref="KeepApart.ReasonText"/>), the tick is when it was written.
    /// </summary>
    public class KeepApartOrder : IExposable
    {
        public int idA, idB;
        public string nameA, nameB;
        /// <summary>Why it was written: "warden" (by hand), or the letter or request that offered it ("feud", "trial", "debt", "gang", "request").</summary>
        public string reason;
        public int sinceTick;

        // Not saved: the pawns, and what the hourly pass last saw of the pair.
        public Pawn a, b;
        /// <summary>Why no bed could part them at the last pass, as the key of the line that says so (<see cref="KeepApart.BlockerLine"/>); null: one could, or they were not together.</summary>
        public string blocker;
        /// <summary>Both in one patch of a shared room (a yard, a canteen: a zone <see cref="PrisonZoneDef.keepable"/> patches are made of) at an hour, and the day it was seen: shown for that day.</summary>
        public string breach;
        public int breachDay = -1;
        /// <summary>The day the job rotation last handed one of the pair's room over to the other (<see cref="LaborUtility.AssignJobs"/>): at most once a day. Not saved: a load may hand over once more that day, to the same inmate.</summary>
        public int handoverDay = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref idA, "idA");
            Scribe_Values.Look(ref idB, "idB");
            Scribe_Values.Look(ref nameA, "nameA");
            Scribe_Values.Look(ref nameB, "nameB");
            Scribe_Values.Look(ref reason, "reason");
            Scribe_Values.Look(ref sinceTick, "sinceTick");
        }

        public bool Has(int id) => id == idA || id == idB;

        /// <summary>The other party's id.</summary>
        public int OtherId(int id) => id == idA ? idB : idA;

        public string NameOf(int id) => id == idA ? nameA : nameB;

        /// <summary>The other party from the point of view of this pawn (null while they are not found or are gone).</summary>
        public Pawn Other(Pawn p)
        {
            Pawn o = p.thingIDNumber == idA ? b : a;
            return o == null || o.Destroyed || o.Dead ? null : o;
        }
    }

    /// <summary>The prison's keep-apart orders (its own save block on <see cref="PrisonGame"/>): the list, and two lookups by thing id (the pair, and a pawn's orders) rebuilt after any change.</summary>
    public class KeepApartBook : IExposable
    {
        /// <summary>The most orders the warden can have at once.</summary>
        public const int Cap = 60;

        public List<KeepApartOrder> orders = new List<KeepApartOrder>();

        private Dictionary<long, KeepApartOrder> pairs;
        private Dictionary<int, List<KeepApartOrder>> byId;
        private bool resolved;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref orders, "orders", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                orders ??= new List<KeepApartOrder>();
                orders.RemoveAll(o => o == null || o.idA == o.idB);
                Changed();
            }
        }

        public bool Full => orders.Count >= Cap;

        /// <summary>The lookups and the pawns are worked out again on the next ask.</summary>
        public void Changed()
        {
            pairs = null;
            byId = null;
            resolved = false;
        }

        public static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        private void Index()
        {
            if (pairs != null) return;
            pairs = new Dictionary<long, KeepApartOrder>(orders.Count);
            byId = new Dictionary<int, List<KeepApartOrder>>(orders.Count);
            foreach (KeepApartOrder o in orders)
            {
                pairs[Key(o.idA, o.idB)] = o;
                foreach (int id in new[] { o.idA, o.idB })
                {
                    if (!byId.TryGetValue(id, out List<KeepApartOrder> list)) byId[id] = list = new List<KeepApartOrder>(1);
                    list.Add(o);
                }
            }
        }

        public KeepApartOrder Find(int a, int b)
        {
            if (orders.Count == 0) return null;
            Index();
            return pairs.TryGetValue(Key(a, b), out KeepApartOrder o) ? o : null;
        }

        /// <summary>The orders this pawn is a party to (null for none).</summary>
        public List<KeepApartOrder> OrdersOf(int id)
        {
            if (orders.Count == 0) return null;
            Index();
            return byId.TryGetValue(id, out List<KeepApartOrder> list) ? list : null;
        }

        /// <summary>Finds the pawns of every order once (one pass over the prisoners): after a load, a change of the list, or the daily pass. A party not found stays null.</summary>
        public void EnsureResolved()
        {
            if (resolved) return;
            resolved = true;
            Index();
            foreach (KeepApartOrder o in orders) (o.a, o.b) = (null, null);
            Map map = Verse.Find.AnyPlayerHomeMap;
            if (map == null) return;
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
            {
                if (!byId.TryGetValue(p.thingIDNumber, out List<KeepApartOrder> list)) continue;
                foreach (KeepApartOrder o in list)
                    if (o.idA == p.thingIDNumber) o.a = p;
                    else o.b = p;
            }
        }

        public void Add(KeepApartOrder o)
        {
            orders.Add(o);
            Changed();
            (o.a, o.b) = (null, null);
        }

        public void Remove(KeepApartOrder o)
        {
            orders.Remove(o);
            Changed();
        }
    }

    public enum KeepApartResult { Added, Exists, Full, Invalid }

    /// <summary>
    /// Keep-apart orders: the warden's list of inmates who are not to be put together. The placement reads it where it chooses - a cell (<see cref="Cellmates"/>: never one cell, a different wing when a bed allows), a patch of
    /// a yard or a canteen (<see cref="KeptRooms.PickCell"/>), a class (<see cref="Programs"/>), a job's room (<see cref="LaborUtility.AssignJobs"/>) - and says so (a line on both files and on Intel) when it cannot. It is advice
    /// to the placement and nothing else: the compliance reading (<see cref="RegimePlacement"/>) and the door verdict (which runs on path threads) never read it.
    /// </summary>
    public static class KeepApart
    {
        /// <summary>The pair term of a bed in the other's cell: as far below any cellmate's consideration as a wing kept for the other sex is, so that placing them there is only done when no other bed is.</summary>
        public const float SameCellTerm = Cellmates.ExcludedFit;

        /// <summary>The pair term of a bed in a wing the other lives in: more than the wing's own +20 and the review's <see cref="Cellmates.BetterBy"/> together, so a bed in another wing (or in none) is a better one.</summary>
        public const float SameWingTerm = -30f;

        /// <summary>Tests only: the day <see cref="Precedes(Pawn, Pawn)"/> and the job rotation go by, instead of the calendar's (the checks that run several days without a game running them).</summary>
        public static int? TestDay;

        /// <summary>The day the daily priority of a pair goes by.</summary>
        public static int Today => TestDay ?? GenDate.DaysPassed;

        /// <summary>Tests only: the placement ignores the orders (the negative run of the check, which puts the pair back in one cell).</summary>
        public static bool TermsOff;

        public static KeepApartBook Book => PrisonGame.Instance?.keepApart;

        /// <summary>Some order is in force (the cheap test every hook starts with).</summary>
        public static bool Any => !TermsOff && Book is { orders.Count: > 0 };

        /// <summary>Whether the warden keeps these two apart (a hash lookup on the two ids).</summary>
        public static bool Ordered(Pawn a, Pawn b) => Any && Book.Find(a.thingIDNumber, b.thingIDNumber) != null;

        public static KeepApartOrder OrderBetween(Pawn a, Pawn b) => Book?.Find(a.thingIDNumber, b.thingIDNumber);

        /// <summary>This pawn's orders (null for none), their pawns found.</summary>
        public static List<KeepApartOrder> OrdersOf(Pawn p)
        {
            if (!Any) return null;
            Book.EnsureResolved();
            return Book.OrdersOf(p.thingIDNumber);
        }

        // ---- writing and lifting -----------------------------------------------------------------------------------------------

        public static string ReasonText(string reason) => ("RP_KeepApartWhy_" + (reason ?? "warden")).Translate();

        public static bool Orderable(Pawn p) => p != null && p.IsPrisonerOfColony && !p.Dead && p.Inmate()?.security != null;

        /// <summary>Writes an order for the pair (the warden's own, or a letter's). Not for the same inmate twice, nor for a pair that has one, nor past <see cref="KeepApartBook.Cap"/>.</summary>
        public static KeepApartResult Add(Pawn a, Pawn b, string reason, bool silent = false)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || a == b || !Orderable(a) || !Orderable(b)) return KeepApartResult.Invalid;
            if (game.keepApart.Find(a.thingIDNumber, b.thingIDNumber) != null) return KeepApartResult.Exists;
            if (game.keepApart.Full)
            {
                if (!silent) Messages.Message("RP_KeepApartFull".Translate(KeepApartBook.Cap), MessageTypeDefOf.RejectInput, historical: false);
                return KeepApartResult.Full;
            }
            var order = new KeepApartOrder { idA = a.thingIDNumber, idB = b.thingIDNumber, nameA = a.LabelShort, nameB = b.LabelShort, reason = reason ?? "warden", sinceTick = Find.TickManager.TicksGame };
            game.keepApart.Add(order);
            (order.a, order.b) = (a, b);
            Refresh(order, Find.TickManager.TicksGame);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, "RP_Chr_KeepApart".Translate(a.LabelShort, b.LabelShort, ReasonText(order.reason)), a, b);
            if (!silent) Messages.Message("RP_KeepApartSet".Translate(a.LabelShort, b.LabelShort), new LookTargets(new[] { a, b }), MessageTypeDefOf.NeutralEvent, historical: false);
            return KeepApartResult.Added;
        }

        public static void Lift(KeepApartOrder order, bool silent = false)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || !game.keepApart.orders.Contains(order)) return;
            game.keepApart.Remove(order);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 1, "RP_Chr_KeepApartLifted".Translate(order.nameA, order.nameB), order.a, order.b);
            if (!silent) Messages.Message("RP_KeepApartLiftedMsg".Translate(order.nameA, order.nameB), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>The button of a row: writes the order for the pair, or lifts the one they have.</summary>
        public static void Toggle(Pawn a, Pawn b, string reason)
        {
            if (OrderBetween(a, b) is { } order) Lift(order);
            else Add(a, b, reason);
        }

        // ---- the daily pass ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// Someone left for good (released, transferred, escaped, dead: <see cref="Story.Notify_Leaving"/>, which ends their debts and arcs the same way): every order they are a party to ends now, and says nothing (the party is gone, and
        /// the lifting line would name two inmates of whom one is not here).
        /// </summary>
        public static void Notify_Leaving(Pawn p)
        {
            KeepApartBook book = Book;
            if (book == null || book.orders.Count == 0 || book.OrdersOf(p.thingIDNumber) is not { Count: > 0 }) return;
            book.orders.RemoveAll(o => o.Has(p.thingIDNumber));
            book.Changed();
        }

        /// <summary>
        /// Daily: the backstop of <see cref="Notify_Leaving"/> - an order with a party who is no longer one of the prison's prisoners (dead, destroyed, gone by a way that did not say so) is dropped. A prisoner who is only not spawned (carried,
        /// in a casket or a shuttle that is loading) is still in <see cref="MapPawns.PrisonersOfColony"/>, which looks into what the map holds, and keeps theirs. Before the review, so that no one is moved for an order that is over.
        /// </summary>
        public static void Daily(Map map)
        {
            KeepApartBook book = Book;
            if (book == null || book.orders.Count == 0 || map == null) return;
            var here = new HashSet<int>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColony)
                if (!p.Dead) here.Add(p.thingIDNumber);
            for (int i = book.orders.Count - 1; i >= 0; i--)
                if (!here.Contains(book.orders[i].idA) || !here.Contains(book.orders[i].idB)) book.orders.RemoveAt(i);
            // (The pawns are found again once a day, whatever the pass dropped: a lookup made while one was being carried is made again.)
            book.Changed();
            book.EnsureResolved();
        }

        /// <summary>Hourly: what the pass sees of each pair (both in one patch of a yard or a canteen now; why no bed could part them).</summary>
        public static void Hourly(Map map)
        {
            KeepApartBook book = Book;
            if (book == null || book.orders.Count == 0 || map == null) return;
            book.EnsureResolved();
            int now = Find.TickManager.TicksGame;
            // (The free beds are worked out once for every order of the pass, not once for each party of each.)
            var beds = new FreeBedPass(map);
            foreach (KeepApartOrder o in book.orders) Refresh(o, now, beds);
        }

        /// <summary>
        /// Works out the cached parts of one order's line: the breach seen now (both in one patch of a shared room: a yard, a canteen - the zones <see cref="KeptRooms.PickCell"/> keeps them out of each other's patch in; a
        /// wing's paint is one patch for the whole wing and is no breach), and why no bed could part them. <paramref name="beds"/>: the free beds of the hourly pass.
        /// </summary>
        public static void Refresh(KeepApartOrder o, int now, FreeBedPass beds = null)
        {
            Pawn a = o.a, b = o.b;
            if (a == null || b == null || a.Destroyed || b.Destroyed) return;
            PrisonMap pm = PrisonMap.Of(a.MapHeld);
            if (pm != null && a.Spawned && b.Spawned && b.Map == a.Map)
            {
                int patch = pm.zones.PatchAt(a.Position);
                if (patch >= 0 && patch == pm.zones.PatchAt(b.Position) && pm.zones.ZoneAt(a.Position) is { keepable: true } zone)
                {
                    o.breach = "RP_KeepApartBreach".Translate(zone.label, GenLocalDate.HourOfDay(a.Map).ToString("00")).ToString();
                    o.breachDay = GenDate.DaysPassed;
                }
            }
            o.blocker = null;
            if (!Shares(a, b, out bool cell)) return;
            // (Neither one could be put elsewhere: told in the line, which names whose file it is on, or both on Intel's.)
            if (Cellmates.CanBeParted(a, a.Inmate(), beds) || Cellmates.CanBeParted(b, b.Inmate(), beds)) return;
            o.blocker = cell ? "RP_KeepApartNoCell" : "RP_KeepApartNoWing";
        }

        /// <summary>The line for an order's blocker: "No bed for {inmate} in another cell", the file's own inmate (<paramref name="fromId"/>), or "No bed for either of them" on Intel's (no one in particular).</summary>
        public static string BlockerLine(KeepApartOrder o, int fromId = -1) =>
            fromId >= 0 ? o.blocker.Translate(o.NameOf(fromId)).ToString() : (o.blocker + "Either").Translate(o.nameA, o.nameB).ToString();

        // ---- reading -------------------------------------------------------------------------------------------------------------

        /// <summary>The wing a pawn lives in (the wing of the bed their cell is built round), null for a bed in no wing.</summary>
        private static SecurityLevelDef WingOfHome(Pawn p) => p?.Inmate() is { } c ? IntakeSystem.WingOf(Cellmates.HomeBed(p, c)) : null;

        /// <summary>Whether the two live in one cell, or else in one wing (a bed in no wing is no one's wing, and a prison with none cannot share one).</summary>
        public static bool Shares(Pawn a, Pawn b, out bool sameCell)
        {
            sameCell = Cellmates.Share(a, b);
            if (sameCell) return true;
            SecurityLevelDef wing = WingOfHome(a);
            return wing != null && wing == WingOfHome(b);
        }

        /// <summary>
        /// The order this inmate is breaking now, or null: they share a cell or a wing with the other party, and the warden has not placed one of them since the order was written (a placement the warden made after the order
        /// is the later word). <paramref name="sameCell"/>: it is the cell, not only the wing.
        /// </summary>
        public static KeepApartOrder Violation(Pawn p, CompInmate c, int now, out Pawn other, out bool sameCell)
        {
            other = null;
            sameCell = false;
            if (OrdersOf(p) is not { } orders) return null;
            foreach (KeepApartOrder o in orders)
            {
                Pawn m = o.Other(p);
                if (m == null || m.Inmate() is not { } mc || !m.IsPrisonerOfColony || m.MapHeld != p.MapHeld || !Shares(p, m, out bool cell)) continue;
                if (HoldsAfter(c, o, now) || HoldsAfter(mc, o, now)) continue;
                other = m;
                sameCell = cell;
                return o;
            }
            return null;
        }

        /// <summary>The warden placed this inmate after the order was written, and the hold on that placement is not over.</summary>
        private static bool HoldsAfter(CompInmate c, KeepApartOrder o, int now) => Cellmates.WardenHold(c, now) && c.wardenPlacedTick >= o.sinceTick;

        /// <summary>The pair's own term in the fit of a bed in a wing (see <see cref="SameWingTerm"/>): what the other lives in, for this inmate.</summary>
        public static float WingTerm(Building_Bed bed, CompInmate c)
        {
            if (!Any || c.parent is not Pawn p || OrdersOf(p) is not { } orders || IntakeSystem.WingOf(bed) is not { } wing) return 0f;
            float term = 0f;
            foreach (KeepApartOrder o in orders)
                if (o.Other(p) is { } m && m.IsPrisonerOfColony && WingOfHome(m) == wing) term += SameWingTerm;
            return term;
        }

        /// <summary>Whether this bed's cell is lived in by someone the pawn is kept apart from.</summary>
        public static bool RoomHoldsPartner(Building_Bed bed, Pawn p) => PartnerIn(bed, p) != null;

        /// <summary>Someone the pawn is kept apart from who lives in this bed's cell, or null.</summary>
        public static Pawn PartnerIn(Building_Bed bed, Pawn p)
        {
            if (!Any || OrdersOf(p) is not { } orders || bed.GetRoom() is not { } room) return null;
            foreach (KeepApartOrder o in orders)
                if (o.Other(p) is { } m && m.IsPrisonerOfColony && Cellmates.Home(m, m.Inmate()) == room) return m;
            return null;
        }

        /// <summary>
        /// Whether this inmate goes before the other today when the two cannot both have what there is one of (a seat in a class, a job in a room): it changes from one day to the next, and from one pair to the next, so
        /// that no one of a pair is always the one who waits.
        /// </summary>
        public static bool Precedes(Pawn a, Pawn b) => Precedes(a, b, Today);

        /// <summary>Whether <paramref name="a"/> goes before <paramref name="b"/> on the given day.</summary>
        public static bool Precedes(Pawn a, Pawn b, int day)
        {
            uint ra = DayRank(a, day), rb = DayRank(b, day);
            return ra != rb ? ra < rb : a.thingIDNumber < b.thingIDNumber;
        }

        private static uint DayRank(Pawn p, int day)
        {
            uint x = (uint)p.thingIDNumber * 2654435761u + (uint)day * 2246822519u;
            x ^= x >> 15;
            x *= 2246822519u;
            return x ^ (x >> 13);
        }

        private static readonly List<int> byRank = new List<int>();

        /// <summary>
        /// The cohort of a class with the second of a pair dropped: of two inmates the warden keeps apart, the one who <see cref="Precedes"/> today keeps the seat, and the other waits for another class. The same list when no one in
        /// it is ordered (the cohort of a prison with no orders is never copied); otherwise the cohort in its own order, less those dropped.
        /// </summary>
        public static IReadOnlyList<Pawn> Cohort(IReadOnlyList<Pawn> cohort)
        {
            if (!Any || cohort == null) return cohort;
            Book.EnsureResolved();
            bool ordered = false;
            for (int i = 0; i < cohort.Count && !ordered; i++) ordered = Book.OrdersOf(cohort[i].thingIDNumber) != null;
            if (!ordered) return cohort;
            // (Seated in the day's order, so the one who goes first is the one who keeps the seat; the list handed back is in the cohort's own order.)
            byRank.Clear();
            for (int i = 0; i < cohort.Count; i++) byRank.Add(i);
            byRank.Sort((x, y) => cohort[x] == cohort[y] ? 0 : Precedes(cohort[x], cohort[y]) ? -1 : 1);
            var seated = new HashSet<int>();
            var dropped = new HashSet<int>();
            foreach (int i in byRank)
            {
                int id = cohort[i].thingIDNumber;
                bool drop = false;
                if (Book.OrdersOf(id) is { } orders)
                    foreach (KeepApartOrder o in orders)
                        if (seated.Contains(o.OtherId(id))) drop = true;
                if (drop) dropped.Add(id);
                else seated.Add(id);
            }
            if (dropped.Count == 0) return cohort;
            var kept = new List<Pawn>(cohort.Count - dropped.Count);
            for (int i = 0; i < cohort.Count; i++)
                if (!dropped.Contains(cohort[i].thingIDNumber)) kept.Add(cohort[i]);
            return kept;
        }

        /// <summary>The patches of a zone the pawn's partners stand in (or are walking to): the pick of a yard or canteen cell tries them last. Empty when there is no order.</summary>
        public static void PartnerPatches(Pawn p, PrisonMap pm, List<int> into)
        {
            into.Clear();
            if (OrdersOf(p) is not { } orders) return;
            foreach (KeepApartOrder o in orders)
            {
                Pawn m = o.Other(p);
                if (m == null || !m.Spawned || m.Map != p.Map) continue;
                int at = pm.zones.PatchAt(m.Position);
                if (at >= 0 && !into.Contains(at)) into.Add(at);
                if (m.pather is { Moving: true } && m.pather.Destination.Cell is { IsValid: true } dest && dest.InBounds(m.Map))
                {
                    int to = pm.zones.PatchAt(dest);
                    if (to >= 0 && !into.Contains(to)) into.Add(to);
                }
            }
        }

        /// <summary>Whether a partner of this inmate already holds a job in the zone (<paramref name="held"/>: the jobs of the inmates seen so far, by thing id).</summary>
        public static bool PartnerHolds(Pawn p, PrisonZoneDef zone, Dictionary<int, InmateJobDef> held)
        {
            if (zone == null || !Any) return false;
            Book.EnsureResolved();
            if (Book.OrdersOf(p.thingIDNumber) is not { } orders) return false;
            foreach (KeepApartOrder o in orders)
                if (held.TryGetValue(o.OtherId(p.thingIDNumber), out InmateJobDef j) && j.zone == zone) return true;
            return false;
        }

        // ---- what the files and Intel say ----------------------------------------------------------------------------------------

        /// <summary>"A and B - since day 12, a feud" for Intel and the files (from the point of view of <paramref name="from"/> when given: the other's name only).</summary>
        public static string OrderLine(KeepApartOrder o, int fromId = -1)
        {
            string since = (o.sinceTick / GenDate.TicksPerDay + 1).ToString();
            return fromId >= 0
                ? "RP_KeepApartFileLine".Translate(o.NameOf(o.OtherId(fromId)), since, ReasonText(o.reason)).ToString()
                : "RP_KeepApartIntelLine".Translate(o.nameA, o.nameB, since, ReasonText(o.reason)).ToString();
        }

        /// <summary>The lines that say an order cannot be honoured today: no bed to part them (and why), both in one patch at an hour, both on a job in one room. Empty when it is being honoured. <paramref name="fromId"/>: the file it is for.</summary>
        public static IEnumerable<string> TroubleLines(KeepApartOrder o, int fromId = -1)
        {
            Pawn a = o.a, b = o.b;
            if (a == null || b == null || a.Destroyed || b.Destroyed) yield break;
            if (Shares(a, b, out _) && o.blocker != null) yield return BlockerLine(o, fromId);
            if (o.breach != null && o.breachDay == GenDate.DaysPassed) yield return o.breach;
            if (a.Inmate()?.job?.zone is { } zone && b.Inmate()?.job?.zone == zone) yield return "RP_KeepApartSameJob".Translate(zone.label).ToString();
        }

        /// <summary>The file's lines: each order the inmate is a party to, and what stands in the way of it.</summary>
        public static IEnumerable<(string, Color)> FileLines(Pawn p)
        {
            if (OrdersOf(p) is not { } orders) yield break;
            foreach (KeepApartOrder o in orders)
            {
                yield return (OrderLine(o, p.thingIDNumber), PrisonHUD.Muted);
                foreach (string trouble in TroubleLines(o, p.thingIDNumber)) yield return (trouble, PrisonHUD.Warn);
            }
        }

        /// <summary>Everything the book holds as one comparable string (the saveload plan's snapshot).</summary>
        public static string Describe(PrisonGame g)
        {
            var parts = new List<string>();
            foreach (KeepApartOrder o in g.keepApart.orders) parts.Add($"{o.idA}={o.nameA}/{o.idB}={o.nameB}/{o.reason}/{o.sinceTick}");
            return $"{g.keepApart.orders.Count}:{string.Join(",", parts)}";
        }
    }
}
