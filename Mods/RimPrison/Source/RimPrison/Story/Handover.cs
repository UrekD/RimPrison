using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// The handover: the prison you were given has a past. A prison that is handed over built (the state penitentiary, the inherited facility) starts with secrets in the systems that already hide
    /// them, so the rings, debts and tunnels do not take days to form: a dealer whose ring has a courier and an officer on the payroll, two overdue debts, a feud, a tunnel a third dug in a
    /// maximum cell, a guard with a suspicion file, a complaint on its way to the desk, a gang holding the yard and the last quadrum's milestones in the old warden's words (the inherited facility
    /// gets a debt, a feud, a suspicion file and a complaint, among the half dozen inmates the old warden left in its cells). Every seed is a field that exists; nothing but <see cref="PrisonGame.handover"/> is saved
    /// for it. They are hidden as the systems hide them (a debt not <see cref="InmateDebt.known"/>, a gang not <see cref="CompInmate.gangKnown"/>, a ring with no link known, a tunnel not
    /// <see cref="CompInmate.tunnelKnown"/>, a complaint still <see cref="ComplaintStage.Filed"/>): the two things the code shows by design are the staff suspicion (the Intel staff row lists any file over 0)
    /// and the enmity (a vanilla relation, on the two files). The welcome letter is the old warden's note: two true hints and one wrong one, naming only what the old warden could have known (a suspicion, never a ring).
    /// The start sows it once (<see cref="Sow"/>, from <see cref="PrisonBuilder"/> behind the scenario's flag); a test start never does unless its plan asks.
    /// </summary>
    public static class Handover
    {
        /// <summary>Set by the new prison dialog for the game it is about to start (see <see cref="Take"/>).</summary>
        public static bool Pending;

        /// <summary>What the last <see cref="Sow"/> made (not saved: the welcome letter that follows it and the checks read it).</summary>
        public static Seeds Last;

        /// <summary>The old chronicle entries are of the quadrum before the game (days before day one, the oldest and the newest): the newest is past <c>PressDecision.Topic</c>'s five days.</summary>
        public const int OldestDays = 15, NewestDays = 6;

        /// <summary>The tunnel's progress (a third of the way) and what the debts are (drugs sold on credit at the dealer's price, a card debt) and how many days overdue each is.</summary>
        public const float TunnelProgress = 1f / 3f;
        public const int DrugDebt = 18, CardDebt = 14, DrugDebtDaysOverdue = 2, CardDebtDaysOverdue = 1;

        /// <summary>The suspicion file's weight (under the threshold at which internal affairs opens a case: <see cref="CorruptionSystem.CaseAt"/>), and the hours until the old complaint reaches the desk.</summary>
        public const float FileSuspicion = 28f;
        public const int ComplaintHours = 6;

        /// <summary>The share of the yard-goers (minimum and medium) the gang that holds the yard is, at the least (the claim is kept at 40% of those seen on the patch, and the dealer and the courier are at work in the laundry, not on the yard, for most of a day), and its size at the least and the most.</summary>
        public const float GangShare = 0.6f;
        public const int GangMin = 5, GangMax = 16;

        /// <summary>The inmates the old warden left in the inherited facility's cells (a few of each of its two levels), where the penitentiary has its own forty.</summary>
        public const int InheritedMinimum = 3, InheritedMedium = 3;

        /// <summary>The dialog's choice for the game being started, once: false for a game that is not the dialog's (a test's own options, a load, the vanilla flow).</summary>
        public static bool Take()
        {
            bool on = Pending;
            Pending = false;
            return on;
        }

        /// <summary>What a sowing made: the people and things the note and the checks name.</summary>
        public sealed class Seeds
        {
            public Facility facility;
            /// <summary>The dealer, their courier, the officer on their payroll and the gang that holds the yard (the penitentiary).</summary>
            public Pawn dealer, courier, officer;
            public GangDef gang;
            public readonly List<Pawn> gangMembers = new List<Pawn>();
            public StoryArc ring;
            /// <summary>The two overdue debts (one in the inherited facility), and the inmates in them.</summary>
            public readonly List<InmateDebt> debts = new List<InmateDebt>();
            public readonly List<Pawn> debtPawns = new List<Pawn>();
            /// <summary>The feud's two, the level it is in, the inmate whose tunnel is a third dug and the level they are in.</summary>
            public Pawn feudA, feudB, tunneller;
            public SecurityLevelDef feudLevel, tunnelLevel;
            /// <summary>The guard with the suspicion file, the complaint on its way and who filed it.</summary>
            public Pawn fileGuard, complainant;
            public Complaint complaint;
            /// <summary>The crew the dealer and their courier work on (the problem), and the crew the note tells the new warden to watch (not the problem).</summary>
            public InmateJobDef problemCrew, wrongCrew;
            /// <summary>The claim on the yard.</summary>
            public TurfClaim turf;
            /// <summary>The chronicle's old entries.</summary>
            public readonly List<ChronicleEntry> chronicle = new List<ChronicleEntry>();
            /// <summary>The inmates the sowing admitted (the inherited facility's).</summary>
            public readonly List<Pawn> admitted = new List<Pawn>();
            /// <summary>The note's text, as the welcome letter carries it.</summary>
            public string note;
            /// <summary>The smuggled-in counts (the statistics', today's) just before the seeds went in, and how many guards had a clean enough sheet to be given a file or a complaint.</summary>
            public int smuggled0, todaySmuggled0, guardsFit;

            /// <summary>Every inmate that is part of a secret (what no letter, line or file of the note may name).</summary>
            public IEnumerable<Pawn> Hidden()
            {
                foreach (Pawn p in new[] { dealer, courier, tunneller, feudA, feudB, complainant }) if (p != null) yield return p;
                foreach (Pawn p in gangMembers) yield return p;
                foreach (Pawn p in debtPawns) yield return p;
            }

            public string Describe() =>
                $"{facility}: dealer {dealer?.LabelShort}, courier {courier?.LabelShort}, officer {officer?.LabelShort}, gang {gang?.defName} x{gangMembers.Count}, ring {(ring != null)}, debts {debts.Count}, " +
                $"feud {feudA?.LabelShort}/{feudB?.LabelShort}, tunnel {tunneller?.LabelShort}, file {fileGuard?.LabelShort}, complaint {(complaint != null)}, crew {problemCrew?.defName}/{wrongCrew?.defName}, chronicle {chronicle.Count}, admitted {admitted.Count}";
        }

        // ---- sowing ------------------------------------------------------------------------------------------------------------------------------------

        private static int Now => Find.TickManager.TicksGame;

        private static SecurityLevelDef Level(string defName) => DefDatabase<SecurityLevelDef>.GetNamed(defName);

        /// <summary>
        /// Sows the secrets in the prison the builder has just put up (the inmates are in their cells, the staff at their posts): once, at the start. A seed that has nobody to be sown in (a small plot's
        /// prison with no maximum cell) is left out and said in the log; the note names only what was sown. Also called by the checks on a prison of their own making.
        /// </summary>
        public static Seeds Sow(Map map, Facility facility)
        {
            PrisonGame game = PrisonGame.Instance;
            PrisonMap pm = PrisonMap.Of(map);
            var s = new Seeds { facility = facility };
            Last = s;
            if (game == null || pm == null) return s;
            bool big = facility == Facility.Penitentiary;
            SecurityLevelDef minimum = Level("RP_Minimum"), medium = RPSecurityDefOf.RP_Medium, maximum = RPSecurityDefOf.RP_Maximum;

            // The inherited facility starts with an empty cell block: the old warden's few are left in it (the penitentiary has its forty).
            if (!big && InmatesOf(map).Count < InheritedMinimum + InheritedMedium)
                s.admitted.AddRange(PrisonBuilder.AdmitLeftBehind(map, (minimum.defName, InheritedMinimum), (medium.defName, InheritedMedium)));
            // (What the arrivals brought in is the game's own: the seeds are judged on what they add to it.)
            (s.smuggled0, s.todaySmuggled0) = (game.stats.contrabandSmuggled, game.today.contrabandSmuggled);
            List<Pawn> inmates = InmatesOf(map);
            foreach (Pawn p in inmates) p.Inmate().RefreshActiveFlag();
            var used = new HashSet<Pawn>();
            List<Pawn> Pool(SecurityLevelDef level) => inmates.Where(p => p.Inmate().security == level && !used.Contains(p) && Sowable(p)).ToList();
            List<Pawn> guards = CleanGuards(map);
            s.guardsFit = guards.Count;

            // The dealer, their courier and the gang that holds the yard (the penitentiary's: a prison of six has no gang to hold anything).
            if (big) SowGang(map, pm, s, inmates, minimum, medium, used);
            // The officer on the dealer's payroll, and the ring round the dealer (forming it reads the officer's payroll).
            if (s.dealer != null)
            {
                s.officer = guards.FirstOrDefault();
                if (s.officer?.GetComp<CompStaff>() is { } osc)
                {
                    osc.corrupt = true;
                    osc.patron = s.dealer;
                    osc.patronGang = s.dealer.Inmate().gang;
                    osc.integrity = 40f;
                }
                s.ring = RingSystem.Form(s.dealer, map, ignoreLimit: true);
                if (s.ring != null && s.courier != null)
                {
                    s.ring.ring.links.Add(new RingLink { role = RingRole.Courier, pawnId = s.courier.thingIDNumber, name = s.courier.LabelShort });
                    s.ring.ring.nextRecruitTick = Now + RingSystem.RebuildDays * GenDate.TicksPerDay;
                    s.ring.ring.moreShown = RingSystem.HasUnknown(s.ring);
                }
                else RPLog.Message("handover: the dealer's ring could not be formed");
                SowCrew(map, pm, s);
            }

            PickWrongCrew(s);
            // The feud: two in one wing, in cells of their own (the medium wing's in the penitentiary, whose minimum wing is a dormitory), before the debts take the wing's pairs.
            SowFeud(s, Pool, used, medium, minimum);
            // Two overdue debts (one in the inherited facility), in one wing so the creditor can reach the debtor.
            SowDebts(game, s, Pool, used, big, medium, minimum);
            // The tunnel, a third dug, in a maximum cell (the penitentiary's).
            if (big) SowTunnel(s, Pool(maximum).Concat(Pool(medium)).ToList(), used, maximum);
            // The guard with the suspicion file (the dealer's officer where there is one), and a complaint against another guard on its way to the desk.
            s.fileGuard = s.officer ?? guards.FirstOrDefault();
            if (s.fileGuard?.GetComp<CompStaff>() is { } fsc) CorruptionSystem.AddSuspicion(s.fileGuard, fsc, FileSuspicion, "RP_Sus_SeenTalking");
            SowComplaint(game, map, s, Pool(minimum).Concat(Pool(medium)).ToList(), guards.Where(g => g != s.fileGuard).DefaultIfEmpty(s.fileGuard).First(), used);
            // The old warden's milestones, the oldest first, before everything the game writes.
            SowChronicle(game, s);
            game.handover = true;
            RPLog.Message("handover sown: " + s.Describe());
            return s;
        }

        /// <summary>
        /// Guards with a clean sheet, the lowest thing id first: a file of the weight sown must not take one over the line at which internal affairs opens a case, nor a guard the warden already knows to be on the take.
        /// </summary>
        public static List<Pawn> CleanGuards(Map map) =>
            StaffCommute.AllStaff(map).Where(p => GuardUtility.IsGuard(p) && p.GetComp<CompStaff>() is { Protected: false, corrupt: false, doubleAgent: false, exposedCorrupt: false, iaOpen: false } sc
                && sc.suspicion + FileSuspicion < CorruptionSystem.CaseAt).OrderBy(p => p.thingIDNumber).ToList();

        /// <summary>An inmate the warden knows nothing about yet (not flagged, no cell search due, no debt or complaint on their file): a prison with a history has such people, and a seed does not go into one who is already on a screen.</summary>
        private static bool Quiet(Pawn p) =>
            p.Inmate() is { suspected: false, cellSearchPending: false, tunnelKnown: false } c && !DebtSystem.KnownAbout(c) && !ComplaintSystem.FileLines(p).Any();

        /// <summary>An inmate a seed may go into: nobody the warden knows anything about, not in custody elsewhere, not on death row.</summary>
        internal static bool Sowable(Pawn p) => Quiet(p) && !DebtSystem.InCustody(p.Inmate()) && DeathRowSystem.Ext(p.Inmate()) == null;

        /// <summary>The first two of the wing (in the order given) who are in cells of their own, one cell each: the feud's pair (<see cref="SowFeud"/>); null with none.</summary>
        internal static (Pawn a, Pawn b)? FeudPair(List<Pawn> wing)
        {
            for (int i = 0; i < wing.Count; i++)
                for (int j = i + 1; j < wing.Count; j++)
                    if (InmateUtility.CellOf(wing[i]) is { } a && InmateUtility.CellOf(wing[j]) is { } b && a != b) return (wing[i], wing[j]);
            return null;
        }

        private static List<Pawn> InmatesOf(Map map)
        {
            var list = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { security: not null } && InmateUtility.IsActiveInmate(p)) list.Add(p);
            list.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
            return list;
        }

        /// <summary>
        /// The gang that holds the yard: the one most of the yard-goers (minimum and medium) already belong to, else the first by name, made up to a good share of them with inmates who had no gang, a leader
        /// among them (if it has none), its mark on every other one (a frisk shows it). Its dealer (a medium inmate who is not its leader, with the dealer reputation, a little cash and two doses in the pockets) and the dealer's courier
        /// (another member). The claim on the yard patch is as old as the quadrum. Nothing is known: not the gang, not the dealer's reputation.
        /// </summary>
        private static void SowGang(Map map, PrisonMap pm, Seeds s, List<Pawn> inmates, SecurityLevelDef minimum, SecurityLevelDef medium, HashSet<Pawn> used)
        {
            List<Pawn> goers = inmates.Where(p => p.Inmate() is { } c && (c.security == minimum || c.security == medium) && !DebtSystem.InCustody(c) && DeathRowSystem.Ext(c) == null).ToList();
            if (goers.Count < GangMin) return;
            GangDef gang = goers.Where(p => p.Inmate().gang != null).GroupBy(p => p.Inmate().gang).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.defName).Select(g => g.Key).FirstOrDefault()
                ?? DefDatabase<GangDef>.AllDefsListForReading.OrderBy(g => g.defName).FirstOrDefault();
            if (gang == null) return;
            s.gang = gang;
            int want = Mathf.Clamp(Mathf.CeilToInt(goers.Count * GangShare), GangMin, GangMax);
            bool Fit(Pawn p) => p.Inmate() is { } c && !c.HasReputation(ReputationDefOf.RP_ModelPrisoner) && !c.HasReputation(ReputationDefOf.RP_Snitch) && !c.HasReputation(DefDatabase<ReputationDef>.GetNamed("RP_ExCop"));
            foreach (Pawn p in goers)
                if (p.Inmate().gang == gang) s.gangMembers.Add(p);
            foreach (Pawn p in goers)
            {
                if (s.gangMembers.Count >= want) break;
                if (p.Inmate().gang == null && Fit(p)) s.gangMembers.Add(p);
            }
            int recruited = 0;
            foreach (Pawn p in s.gangMembers)
            {
                CompInmate c = p.Inmate();
                c.gangKnown = false;
                if (c.gang == gang) continue;
                // (A new member carries the mark in every other case: a frisk shows it, which is how the gang is found out.)
                c.gang = gang;
                c.gangTattoo = recruited++ % 2 == 0;
                GangRanks.SetRank(c, GangRank.Member);
            }
            if (!GangUtility.HasLeader(map, gang, null) && s.gangMembers.FirstOrDefault(Fit) is { } boss) GangRanks.SetRank(boss.Inmate(), GangRank.Leader);
            // The dealer, and the courier who carries for them.
            ReputationDef dealerRep = DefDatabase<ReputationDef>.GetNamed("RP_Dealer");
            s.dealer = s.gangMembers.FirstOrDefault(p => p.Inmate() is { gangLeader: false } c && c.security == medium && Fit(p) && !dealerRep.conflictsWith.Exists(r => c.HasReputation(r)))
                ?? s.gangMembers.FirstOrDefault(p => p.Inmate() is { gangLeader: false } c && Fit(p) && !dealerRep.conflictsWith.Exists(r => c.HasReputation(r)));
            if (s.dealer == null) return;
            CompInmate dc = s.dealer.Inmate();
            dc.reputations.Add(dealerRep);
            dc.cash += 30;
            for (int i = 0; i < 2; i++) ContrabandUtility.Give(s.dealer, ContrabandUtility.PickSmuggled(s.dealer, ContrabandKind.Drug), smuggled: false);
            used.Add(s.dealer);
            s.courier = s.gangMembers.FirstOrDefault(p => p != s.dealer && Fit(p) && p.Inmate() is { gangLeader: false } c && !DebtSystem.InCustody(c));
            if (s.courier != null) used.Add(s.courier);
            // The yard is theirs: the patch with the most cells, held since a while before the game.
            List<ZonePatch> patches = pm.zones.Patches;
            int best = -1;
            for (int i = 0; i < patches.Count; i++)
                if (patches[i].zone == RPDefOf.RP_Yard && (best < 0 || patches[i].cells > patches[best].cells)) best = i;
            if (best >= 0)
            {
                pm.turf.Set(pm.zones, RPDefOf.RP_Yard, patches[best].anchor, gang, Now - 20 * GenDate.TicksPerDay);
                s.turf = pm.turf.At(patches[best].anchor);
            }
        }

        /// <summary>The dealer and the courier work the laundry crew (the problem the note does not name).</summary>
        private static void SowCrew(Map map, PrisonMap pm, Seeds s)
        {
            InmateJobDef laundry = DefDatabase<InmateJobDef>.GetNamedSilentFail("RP_JobLaundry");
            if (laundry != null && pm.zones.Any(RPLaborDefOf.RP_Laundry))
            {
                int cap = laundry.Worker.Capacity(map);
                List<Pawn> inmates = InmatesOf(map);
                foreach (Pawn p in new[] { s.dealer, s.courier })
                {
                    if (p?.Inmate() is not { } c || c.job == laundry || !LaborUtility.Eligible(c, laundry)) continue;
                    // (A crew already at its cap gives up a worker who is not the warden's pick for the seat: the job pass finds them another, and the crew does not start over its cap.)
                    if (inmates.Count(o => o.Inmate().job == laundry) >= cap)
                        if (inmates.FirstOrDefault(o => o != s.dealer && o != s.courier && o.Inmate() is { jobLocked: false } oc && oc.job == laundry) is { } other) other.Inmate().job = null;
                        else continue;
                    c.job = laundry;
                }
            }
            s.problemCrew = s.dealer.Inmate().job;
        }

        /// <summary>The crew the note tells the new warden to watch, which is not the one the problem is in (the kitchen, unless the dealer or their courier work it).</summary>
        private static void PickWrongCrew(Seeds s)
        {
            foreach (string name in new[] { "RP_JobKitchen", "RP_JobWorkshop", "RP_JobGarden" })
                if (DefDatabase<InmateJobDef>.GetNamedSilentFail(name) is { } crew && crew != s.problemCrew && crew != s.courier?.Inmate().job)
                {
                    s.wrongCrew = crew;
                    return;
                }
        }

        /// <summary>
        /// A drugs debt owed to the dealer (the penitentiary's), and a card debt between two others, each overdue by a day or two with the debtor out of cash (the first daily pass escalates them, it does not
        /// settle or drop them: both are in one wing, so the creditor can reach the debtor). Nobody has heard of either.
        /// </summary>
        private static void SowDebts(PrisonGame game, Seeds s, System.Func<SecurityLevelDef, List<Pawn>> pool, HashSet<Pawn> used, bool big, SecurityLevelDef medium, SecurityLevelDef minimum)
        {
            if (s.dealer != null)
            {
                Pawn debtor = pool(s.dealer.Inmate().security).FirstOrDefault(p => p != s.dealer && DebtSystem.CanLend(s.dealer, p));
                if (debtor != null)
                {
                    s.debts.Add(OpenDebt(s.dealer, debtor, DebtReason.Drugs, DrugDebt, DrugDebtDaysOverdue));
                    debtor.Inmate().lastDealerId = s.dealer.thingIDNumber;
                    s.debtPawns.Add(debtor);
                    used.Add(debtor);
                }
            }
            // The card debt: two of the medium wing (the inherited facility's three), else of the minimum one; the first pair in which the one can lend to the other.
            List<Pawn> wing = pool(medium);
            if (wing.Count < 2) wing = pool(minimum);
            for (int i = 0; i < wing.Count; i++)
                for (int j = 0; j < wing.Count; j++)
                {
                    if (i == j || !DebtSystem.CanLend(wing[i], wing[j])) continue;
                    s.debts.Add(OpenDebt(wing[i], wing[j], DebtReason.Cards, CardDebt, CardDebtDaysOverdue));
                    s.debtPawns.Add(wing[i]);
                    s.debtPawns.Add(wing[j]);
                    used.Add(wing[i]);
                    used.Add(wing[j]);
                    return;
                }
        }

        private static InmateDebt OpenDebt(Pawn creditor, Pawn debtor, DebtReason reason, int amount, int daysOverdue)
        {
            InmateDebt d = DebtSystem.Open(creditor, debtor, reason, amount, -daysOverdue);
            debtor.Inmate().cash = 0;
            return d;
        }

        /// <summary>
        /// A feud nobody has seen: two of one wing who are enemies, in cells of their own (an arc would be on the Intel screen with a letter; this is the grudge that comes before one). The wings are tried in the order given
        /// (a dormitory has no cells of their own to be in), and the gang's members are left out unless that leaves no pair (a feud inside the gang or with its leader is another story). Nobody who is in a story already (a feud arc running for them) is picked: the seeded grudge is an enemy relation, never a second feud on top of a running one. Nobody is told when there is no pair.
        /// </summary>
        private static void SowFeud(Seeds s, System.Func<SecurityLevelDef, List<Pawn>> pool, HashSet<Pawn> used, params SecurityLevelDef[] levels)
        {
            var fit = new List<string>();
            foreach (bool gangToo in new[] { false, true })
                foreach (SecurityLevelDef level in levels)
                {
                    List<Pawn> open = pool(level);
                    List<Pawn> wing = open.Where(p => (gangToo || !s.gangMembers.Contains(p)) && !PrisonGame.Instance.story.InArc(p)).ToList();
                    if (FeudPair(wing) is not { } pair)
                    {
                        fit.Add($"{level.defName}{(gangToo ? "+gang" : "")}: {open.Count} quiet, {wing.Count} not in a story, {wing.Count(p => InmateUtility.CellOf(p) != null)} with a cell");
                        continue;
                    }
                    (s.feudA, s.feudB, s.feudLevel) = (pair.a, pair.b, level);
                    PrisonRelations.MakeEnemies(s.feudA, s.feudB);
                    used.Add(s.feudA);
                    used.Add(s.feudB);
                    return;
                }
            RPLog.Message($"handover: no two inmates of one wing in cells of their own for a feud ({string.Join("; ", fit)})");
        }

        /// <summary>
        /// A tunnel a third dug under a maximum cell (a medium one on a prison with none): the digger can dig (a lifer, or an escape artist, which is hidden), the tool is in the cell's stash (found by a cell search, not a
        /// frisk). Nobody is told: not known, not flagged, no search pending.
        /// </summary>
        private static void SowTunnel(Seeds s, List<Pawn> candidates, HashSet<Pawn> used, SecurityLevelDef maximum)
        {
            Pawn digger = candidates.FirstOrDefault(p => InmateUtility.CellOf(p) != null && p.Inmate().IsLifer) ?? candidates.FirstOrDefault(p => InmateUtility.CellOf(p) != null);
            if (digger == null) return;
            CompInmate c = digger.Inmate();
            if (!c.IsLifer && !c.HasReputation(ReputationDefOf.RP_EscapeArtist))
            {
                // (No lifer to dig: an escape artist does, which also multiplies their escape misconduct and doubles the digging.)
                RPLog.Message($"handover: no lifer in the {c.security?.defName} wing to dig the tunnel: {digger.LabelShort} is an escape artist");
                c.reputations.Add(ReputationDefOf.RP_EscapeArtist);
            }
            c.tunnelProgress = TunnelProgress;
            c.tunnelKnown = false;
            c.cellSearchPending = false;
            c.suspected = false;
            Thing tool = ContrabandUtility.Give(digger, ContrabandUtility.PickSmuggled(digger, ContrabandKind.Tool), smuggled: false);
            if (tool != null) digger.inventory.innerContainer.TryTransferToContainer(tool, c.stash, tool.stackCount);
            s.tunneller = digger;
            s.tunnelLevel = c.security;
            used.Add(digger);
        }

        /// <summary>A complaint the old warden never answered: filed yesterday, on its way to the desk (the letter comes in a few hours; until then it is on no file and no screen).</summary>
        private static void SowComplaint(PrisonGame game, Map map, Seeds s, List<Pawn> pool, Pawn officer, HashSet<Pawn> used)
        {
            if (officer == null)
            {
                RPLog.Message("handover: no guard to be the complaint's officer; no complaint sown");
                return;
            }
            var refused = new List<string>();
            foreach (Pawn p in pool)
            {
                if (used.Contains(p)) continue;
                Complaint cm = ComplaintSystem.File(game, map, p, officer, ComplaintKind.Harassment, isTrue: true);
                if (cm == null)
                {
                    refused.Add($"{p.LabelShort} (a complaint open {ComplaintSystem.OpenBy(game, p.thingIDNumber) != null})");
                    continue;
                }
                cm.filedTick = Now - GenDate.TicksPerDay;
                cm.dueTick = Now + ComplaintHours * GenDate.TicksPerHour;
                s.complaint = cm;
                s.complainant = p;
                used.Add(p);
                return;
            }
            // (Said, as every seed left out is: the minimum and medium wings' quiet inmates were all taken by the feud and the debts, or refused by the complaints' own rules.)
            RPLog.Message($"handover: nobody to file the old complaint against {officer.LabelShort}: {pool.Count} quiet inmates of the minimum and medium wings, {pool.Count(used.Contains)} already given a seed, refused [{string.Join(", ", refused)}]");
        }

        // ---- the old warden's words ----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The old milestones: how many days before the game each was and at what hour, its kind, how much it weighs, its line (the old warden's own words, a keyed string) and the prisons that have it. The newest is
        /// <see cref="NewestDays"/> days back, the oldest <see cref="OldestDays"/>.
        /// </summary>
        private static readonly (int days, int hour, string cat, int importance, string key, Facility? only)[] OldEntries =
        {
            (OldestDays, 9, "RP_Chr_Intake", 3, "RP_Handover_Chr_Intake", Facility.Penitentiary),
            (14, 10, "RP_Chr_Decisions", 3, "RP_Handover_Chr_Pension", Facility.Inherited),
            (13, 15, "RP_Chr_Security", 2, "RP_Handover_Chr_Lockdown", null),
            (11, 10, "RP_Chr_Staff", 3, "RP_Handover_Chr_Sergeant", Facility.Penitentiary),
            (10, 14, "RP_Chr_Life", 2, "RP_Handover_Chr_Yard", Facility.Penitentiary),
            (8, 8, "RP_Chr_Decisions", 3, "RP_Handover_Chr_Books", null),
            (7, 16, "RP_Chr_Outside", 2, "RP_Handover_Chr_Inspector", Facility.Penitentiary),
            (NewestDays, 21, "RP_Chr_Outside", 3, "RP_Handover_Chr_Replaced", null),
        };

        private static void SowChronicle(PrisonGame game, Seeds s)
        {
            // (Before everything the build wrote: the list is in order of time, and these are the oldest there is.)
            var old = new List<ChronicleEntry>();
            foreach ((int days, int hour, string cat, int importance, string key, Facility? only) in OldEntries)
            {
                if (only != null && only != s.facility) continue;
                old.Add(new ChronicleEntry
                {
                    tick = -days * GenDate.TicksPerDay + hour * GenDate.TicksPerHour,
                    cat = DefDatabase<ChronicleCategoryDef>.GetNamed(cat),
                    importance = importance,
                    text = key.Translate().Resolve(),
                });
            }
            game.story.entries.InsertRange(0, old);
            s.chronicle.AddRange(old);
        }

        // ---- the note ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The old warden's note, which is the welcome letter's body: a greeting, three things the old warden would want known, and a signature. Two are true and one is not: a file open on a guard (the officer on the
        /// dealer's payroll where there is one: a suspicion, never a ring), a wing that is too quiet at night (the tunnel: the maximum wing), or in the inherited facility two men who do not belong in a room together (the feud),
        /// and a crew to watch that is not the one the problem is in (the kitchen, where the dealer and their courier work the laundry). A hint whose seed was not sown is left out, and so is the wrong one when there is no crew
        /// to point at. Null when nothing was sown.
        /// </summary>
        public static string NoteText(string prison, Seeds s)
        {
            if (s == null) return null;
            var hints = new List<string>();
            if (s.fileGuard != null) hints.Add("RP_HandoverHint_File".Translate(s.fileGuard.LabelShort).Resolve());
            if (s.wrongCrew != null) hints.Add("RP_HandoverHint_Crew".Translate(s.wrongCrew.label).Resolve());
            if (s.tunneller != null) hints.Add("RP_HandoverHint_Dig".Translate(s.tunnelLevel.label).Resolve());
            else if (s.feudA != null && s.feudLevel != null) hints.Add("RP_HandoverHint_Feud".Translate(s.feudLevel.label).Resolve());
            if (hints.Count == 0) return null;
            return "RP_HandoverNote_Lead".Translate(prison).Resolve() + "\n\n" + string.Join("\n", hints) + "\n\n" + "RP_HandoverNote_Sign".Translate().Resolve();
        }
    }

    /// <summary>A game being started or loaded has no handover pending: the dialog sets it after it makes its game, so a start that never reached the scenario's start cannot leak it into the next game.</summary>
    [HarmonyPatch(typeof(Game), MethodType.Constructor, new System.Type[0])]
    public static class Patch_Game_Constructor_ClearPendingHandover
    {
        public static void Postfix()
        {
            Handover.Pending = false;
            Handover.Last = null;
        }
    }
}
