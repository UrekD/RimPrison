using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// What the warden knows. Snitches tip off staff about contraband, tunnels and gang ties nearby; if a tip is
    /// acted on carelessly the snitch can be exposed, and exposed snitches get hurt.
    /// </summary>
    public static class InformantSystem
    {
        /// <summary>Daily per snitch.</summary>
        public static void DailyTips(Map map)
        {
            foreach (Pawn snitch in map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate sc = snitch.Inmate();
                if (sc == null || !sc.activeFlag || !sc.HasReputation(ReputationDefOf.RP_Snitch) || sc.snitchExposed || sc.informantQuit) continue;
                if (!Rand.Chance(0.45f)) continue;
                GiveTip(snitch, sc);
            }
        }

        /// <summary>The snitch passes on what they know, if there is anything: the warden's letter (the daily pass, or the warden on their walk, see <see cref="WardenSystem"/>). False when they had nothing.</summary>
        internal static bool GiveTip(Pawn snitch, CompInmate sc)
        {
            string tip = TryTip(snitch, sc);
            if (tip == null) return false;
            if (!sc.revealed.Contains(ReputationDefOf.RP_Snitch)) sc.revealed.Add(ReputationDefOf.RP_Snitch);
            PrisonGame.Instance.stats.tips++;
            Find.LetterStack.ReceiveLetter("RP_TipLabel".Translate(), "RP_TipText".Translate(snitch.LabelShort, tip), LetterDefOf.NeutralEvent, snitch);
            return true;
        }

        /// <summary>For a test to confine a tip to the pawns it made up (null: every pawn is in reach).</summary>
        internal static System.Func<Pawn, bool> TipPool;

        internal static string TryTip(Pawn snitch, CompInmate sc)
        {
            // A tasked informant looks at their target first (see InformantTask): when the target is in reach (within 30 cells, or in their cell or wing), in the order below, and then at the rest as anyone does. (The ring
            // step tells the ring's next link, who need not be the target; and a ring with the target and another member nearby is rolled for twice, here and with the rest.)
            Pawn target = InformantTask.TargetOf(snitch, sc);
            CompInmate tc = target?.Inmate();
            if (target != null && (tc == null || TipPool != null && !TipPool(target) || !InformantTask.InReach(snitch, target))) target = null;
            if (target != null)
            {
                if (InformantTask.WrongLead(snitch, sc, target, tc) is { } lie) return lie;
                if (TipAbout(snitch, sc.gang, target, tc) is { } about) return about;
                var only = new List<Pawn> { target };
                if (RingSystem.TryTip(snitch, only) is { } ringAbout) return ringAbout;
                if (DebtSystem.TryTip(snitch, only, own: false) is { } debtAbout) return debtAbout;
            }
            var candidates = new List<Pawn>();
            foreach (Pawn o in snitch.Map.mapPawns.PrisonersOfColonySpawned)
                if (o != snitch && o != target && o.Position.InHorDistOf(snitch.Position, 30f) && o.Inmate() != null && (TipPool == null || TipPool(o))) candidates.Add(o);
            candidates.Shuffle();
            // Staff on the take are the juiciest gossip; and now and then a snitch points at a clean guard (a grudge, a
            // gang wanting a strict guard gone). Either way it is suspicion for internal affairs, not proof.
            Pawn named = null;
            var clean = new List<Pawn>();
            foreach (Pawn s in snitch.Map.mapPawns.FreeColonistsSpawned)
            {
                if (s.GetComp<CompStaff>() is not { role: not null, doubleAgent: false, exposedCorrupt: false } stc || !s.Position.InHorDistOf(snitch.Position, 30f) || TipPool != null && !TipPool(s)) continue;
                if (stc.corrupt && stc.bribesTaken > 0) named = s;
                else if (GuardUtility.IsGuard(s)) clean.Add(s);
            }
            // A dirty one if there is one; otherwise, once in a while (4% of tips), a lie about a clean guard.
            if (named == null && clean.Count > 0 && Rand.Chance(0.04f)) named = clean.RandomElement();
            if (named != null)
            {
                CorruptionSystem.AddSuspicion(named, named.GetComp<CompStaff>(), 35f, "RP_Sus_SnitchTip");
                return "RP_TipCorrupt".Translate(named.LabelShort);
            }
            foreach (Pawn o in candidates)
                if (TipAbout(snitch, sc.gang, o, o.Inmate()) is { } tip) return tip;
            // A ring's links, when the snitch is close to some of its members: after a tunnel, contraband and a gang (a ring is found again, a tunnel that is not is dug out).
            if (RingSystem.TryTip(snitch, candidates) is { } ringTip) return ringTip;
            // Who started the rumour on their own wing, and whether it holds (half the time, when the warden does not know yet: see Rumours): after the rings, so that talk never takes the place of a ring's link.
            if (Rumours.TryTip(snitch, sc) is { } rumourTip) return rumourTip;
            // Who owes whom (their own debt, or one among the people near them): last, so that gossip about money never takes the place of a tunnel, contraband or a gang.
            return DebtSystem.TryTip(snitch, candidates);
        }

        /// <summary>
        /// What the teller can tell of this one inmate: a tunnel, contraband, a gang (half the time; never their own <paramref name="ownGang"/>), in that order; null for nothing. <paramref name="informer"/>: the snitch
        /// a search acted on it may give away (null for a member of staff's tip, see <see cref="StaffTip"/>: nobody to give away).
        /// </summary>
        private static string TipAbout(Pawn informer, GangDef ownGang, Pawn o, CompInmate oc)
        {
            if (oc.tunnelProgress > 0.15f && !oc.tunnelKnown)
            {
                oc.tunnelKnown = true;
                oc.cellSearchPending = true;
                // (A member of staff's tip gives nobody away, and keeps the informant an earlier tip named: a search may still give them away.)
                oc.informedBy = informer ?? oc.informedBy;
                return "RP_TipTunnel".Translate(o.LabelShort);
            }
            if (ContrabandUtility.CarriesContraband(o) && !oc.suspected)
            {
                oc.suspected = true;
                oc.informedBy = informer ?? oc.informedBy;
                return "RP_TipContraband".Translate(o.LabelShort);
            }
            if (oc.gang != null && !oc.gangKnown && ownGang != oc.gang && Rand.Chance(0.5f))
            {
                GangUtility.Reveal(oc);
                return "RP_TipGang".Translate(o.LabelShort, oc.gang.label);
            }
            return null;
        }

        /// <summary>
        /// A unit officer's tip about their wing (<see cref="UnitOfficers"/>): the informant's ranking over the wing's inmates (<paramref name="wing"/>, shuffled) - a tunnel, contraband, a gang tie (never
        /// <paramref name="ownGang"/>'s: a corrupt officer's patron's), then a ring's link (<see cref="RingSystem.TryTip"/>: it tells the ring's next link, who need not be on the wing), then a debt one of them is party to.
        /// Nobody inside is the source, so a search acting on it gives nobody away. <paramref name="map"/>: the wing's (the officer may be at home, on no map).
        /// </summary>
        internal static string StaffTip(Pawn officer, List<Pawn> wing, Map map, GangDef ownGang = null)
        {
            wing.Shuffle();
            foreach (Pawn o in wing)
                if (o.Inmate() is { } oc && TipAbout(null, ownGang, o, oc) is { } tip) return tip;
            if (RingSystem.TryTip(officer, wing, map: map) is { } ringTip) return ringTip;
            return DebtSystem.TryTip(officer, wing, own: false, map: map);
        }

        /// <summary>A search acted on a tip: the target may work out who talked (<see cref="InformantTask.ActedOnChance"/>: 30%, and more for an informant who has been watching them for days).</summary>
        public static void Notify_TipActedOn(Pawn target, CompInmate tc)
        {
            Pawn snitch = tc.informedBy;
            tc.informedBy = null;
            if (snitch == null || snitch.Dead || !snitch.Spawned || snitch.Map != target.Map || !snitch.IsPrisonerOfColony) return;
            CompInmate sc = snitch.Inmate();
            if (sc == null || sc.snitchExposed || !InformantTask.Roll(InformantTask.ActedOnChance(snitch, sc, target))) return;
            Expose(snitch, sc, target, tc);
        }

        /// <summary>
        /// The snitch is found out by <paramref name="target"/>: afraid, known for what they are, the target and their gang want them, a letter and a line. <paramref name="burnedInPlace"/>: made by the inmate they were
        /// set to watch (the days in place), not by a search; the letter says so. A task they had ends.
        /// </summary>
        internal static void Expose(Pawn snitch, CompInmate sc, Pawn target, CompInmate tc, bool burnedInPlace = false)
        {
            if (sc == null || sc.snitchExposed) return;
            InformantTask.End(snitch, sc);
            sc.snitchExposed = true;
            FearSystem.Scare(sc, 30f);
            if (!sc.revealed.Contains(ReputationDefOf.RP_Snitch)) sc.revealed.Add(ReputationDefOf.RP_Snitch);
            // The target (and their gang) want revenge.
            foreach (Pawn o in snitch.Map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate oc = o.Inmate();
                if (oc == null || o == snitch || (o != target && (tc.gang == null || oc.gang != tc.gang))) continue;
                o.needs.mood?.thoughts.memories.TryGainMemory(RPTrialDefOf.RP_HatesSnitch, snitch);
                oc.tension = Mathf.Min(100f, oc.tension + 15f);
            }
            Find.LetterStack.ReceiveLetter("RP_SnitchExposedLabel".Translate(), (burnedInPlace ? "RP_InformantTask_BurnedText" : "RP_SnitchExposedText").Translate(snitch.LabelShort, target.LabelShort), LetterDefOf.ThreatSmall, snitch);
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_SnitchExposed".Translate(snitch.LabelShort, target.LabelShort), snitch, target);
        }
    }

    /// <summary>
    /// Tunnels: an inmate with smuggled tools digs at night in their cell. Hidden progress; at 100% a way out opens
    /// beyond the walls and they (and cellmates) go for it after lights out. Cell searches find tunnels in progress.
    /// </summary>
    public static class TunnelSystem
    {
        public static bool CanDig(Pawn p, CompInmate c) =>
            (c.forcedActivity == null || c.forcedActivity == RPDefOf.RP_Lockup) && HasDiggingTool(p) && (c.HasReputation(ReputationDefOf.RP_EscapeArtist) || c.tension > 45f || c.IsLifer);

        public static bool HasDiggingTool(Pawn p)
        {
            if (p.Inmate() is { } c && StashUtility.Has(c, ContrabandKind.Tool)) return true;
            if (p.inventory == null) return false;
            foreach (Thing t in p.inventory.innerContainer)
                if (ContrabandUtility.Ext(t.def) is { kind: ContrabandKind.Tool }) return true;
            return false;
        }

        /// <summary>Hourly, while locked in the own cell at night.</summary>
        public static void Hourly(Pawn p, CompInmate c, RegimeActivityDef act)
        {
            if (!act.lockup || p.Downed || p.InMentalState || p.GetRoom() != InmateUtility.CellOf(p)) return;
            if (p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained)) return;
            if (c.tunnelProgress >= 1f)
            {
                if (act == RPDefOf.RP_Sleep || act == RPDefOf.RP_Lockup) TryBreakOut(p, c);
                return;
            }
            if (!CanDig(p, c)) return;
            c.tunnelProgress = Mathf.Min(1f, c.tunnelProgress + DigThisHour(c));
            if (c.tunnelProgress >= 1f) PrisonGame.Instance.stats.tunnelsDug++;
            // The spoil and the noise of a deeper hole give it away now and then, to someone there to notice: the guards are told to look at the cell (the same flag a tip, a tap or the dog sets).
            else if (!c.tunnelKnown && !c.cellSearchPending && NoticeChance(c.tunnelProgress) is > 0f and float chance && SomeoneToNotice(p) && Rand.Chance(chance)) c.cellSearchPending = true;
        }

        /// <summary>
        /// A guard within earshot of the cell (the noise carries through the wall), or a camera or tower that covers it: a tunnel nobody is near enough to hear, or watching the cell, is not noticed (an event nobody saw is not recorded).
        /// </summary>
        public static bool SomeoneToNotice(Pawn p)
        {
            PrisonMap pm = PrisonMap.Of(p.Map);
            if (pm == null || !p.Spawned) return false;
            if (TensionUtility.Watched(p, pm)) return true;
            List<Pawn> guards = pm.Guards;
            for (int i = 0; i < guards.Count; i++)
                if (guards[i] is { Spawned: true, Downed: false } g && g.Awake() && g.Position.InHorDistOf(p.Position, TensionUtility.GuardRadius)) return true;
            return false;
        }

        /// <summary>What one dug hour adds to a tunnel.</summary>
        public static float DigThisHour(CompInmate c) => Rand.Range(DigPerHourMin, DigPerHourMax) * (c.HasReputation(ReputationDefOf.RP_EscapeArtist) ? 2f : 1f);

        /// <summary>The chance, in one dug hour, that the guards notice a tunnel this far on (none before <see cref="NoticedAbove"/>).</summary>
        public static float NoticeChance(float progress) => progress > NoticedAbove ? NoticedPerHour + NoticedPerHourPerProgress * progress : 0f;

        /// <summary>
        /// Progress a dig hour adds (of 1 for the whole tunnel): a spoon and a locked cell, not a drill. At about 10 dug hours a night a tunnel takes two weeks or so
        /// (an escape artist digs twice as fast: a week, and not inside three days even in a cell that is locked all day and night), where it used to take two to four nights and so beat every search the guards ran.
        /// </summary>
        public const float DigPerHourMin = 0.004f, DigPerHourMax = 0.009f;

        /// <summary>A tunnel past this much (a quarter of the way) can be noticed by the guards without a tip: this chance per dig hour at that point and this much more at the end (about one night in five at half way).</summary>
        public const float NoticedAbove = 0.25f, NoticedPerHour = 0.01f, NoticedPerHourPerProgress = 0.02f;

        /// <summary>Through the tunnel: out beyond the wall, then run for the map edge.</summary>
        public static void TryBreakOut(Pawn p, CompInmate c, bool force = false, IntVec3? exitAt = null)
        {
            if (!force && !Rand.Chance(0.5f)) return;
            // (<paramref name="exitAt"/>: the checks' own spot to come up at, so what watches it is theirs to say.)
            IntVec3 exit = exitAt ?? IntVec3.Invalid;
            if (p.Downed || exitAt == null && !TryFindExit(p, out exit)) return;
            Room cell = p.GetRoom();
            Map map = p.Map;
            var escapees = new List<Pawn> { p };
            foreach (Thing t in cell.ContainedAndAdjacentThings)
                if (t is Pawn mate && mate != p && mate.GetRoom() == cell && mate.Inmate() is { activeFlag: true } && !mate.Downed && Rand.Chance(0.6f)) escapees.Add(mate);
            // (Where they come up is seen or not: a camera or a tower on the ground outside, a guard on it. If nothing sees it the warden is not told, and the count may be the first to know.)
            bool told = false;
            foreach (Pawn e in escapees)
            {
                IntVec3 from = e.Position;
                e.jobs.StopAll();
                e.DeSpawn();
                GenSpawn.Spawn(e, exitAt ?? CellFinder.RandomClosewalkCellNear(exit, map, 2), map);
                CountSystem.StartEscaping(e, "RP_Tunnel".Translate(), EscapeWay.Tunnel, escapees.Count, from);
                if (!CountSystem.IsHidden(e)) told = true;
            }
            // Used once: the breakout is discovered and the tunnel filled in.
            c.tunnelProgress = 0f;
            c.tunnelKnown = false;
            if (told)
                Find.LetterStack.ReceiveLetter("RP_TunnelBreakoutLabel".Translate(), "RP_TunnelBreakoutText".Translate(p.LabelShort, escapees.Count), LetterDefOf.ThreatBig, new LookTargets(escapees));
        }

        /// <summary>An outdoor cell outside the compound, 6–25 cells from the cell, that is not inmate space.</summary>
        private static bool TryFindExit(Pawn p, out IntVec3 exit)
        {
            Map map = p.Map;
            PrisonMap pm = PrisonMap.Of(map);
            return CellFinder.TryFindRandomCellNear(p.Position, map, 25, c =>
                c.Standable(map) && !c.Roofed(map) && c.GetRoom(map) is { } r && (r.TouchesMapEdge || r.PsychologicallyOutdoors) && pm.zones.ZoneAt(c) is not { inmateAccess: true }
                && !c.InHorDistOf(p.Position, 6f) && map.reachability.CanReachMapEdge(c, TraverseParms.For(TraverseMode.NoPassClosedDoors)), out exit);
        }

        /// <summary>A guard searching the cell finds a tunnel in progress.</summary>
        public static bool SearchCell(Pawn guard, Room cell)
        {
            bool found = false;
            Pawn searched = null;
            // (Copies of both lists: what a search turns up (a lockup for the owner, a bed given up, a letter) changes who owns a bed, and a list that is changed under the loop threw
            // "Collection was modified" in the job's last toil and ended the guard's job with an error.)
            foreach (Building_Bed bed in cell.ContainedBeds.ToList())
                foreach (Pawn owner in bed.OwnersForReading.ToList())
                {
                    CompInmate c = owner.Inmate();
                    if (c == null) continue;
                    searched ??= owner;
                    c.cellSearchPending = false;
                    int stashFound = StashUtility.Search(guard, owner, c);
                    if (stashFound > 0) found = true;
                    // A grudge-bearer whose cell was turned over (or whose stash was found) may say the officer abused them: if they were in or near the cell at the time.
                    ComplaintSystem.Notify_Searched(guard, owner, stashFound > 0, guard.Position);
                    // Notes of who owes whom turn up in a cell turned over.
                    DebtSystem.Notify_Searched(owner, DebtSystem.CellSearchFind);
                    if (c.tunnelProgress <= 0.05f || !Rand.Chance(0.85f)) continue;
                    c.tunnelProgress = 0f;
                    c.tunnelKnown = false;
                    found = true;
                    PrisonGame.Instance.stats.tunnelsFound++;
                    // The tools that dug it, when they came out of a workshop or a kitchen, are taken with it and tie the tunnel to the room.
                    ToolCount.Notify_TunnelFound(owner, c);
                    MisconductUtility.Record(owner, c, PrisonMap.Of(owner.Map), RPMisconductDefOf.RP_Tunneling, evidence: true, chronicle: false);
                    if (c.informedBy != null) InformantSystem.Notify_TipActedOn(owner, c);
                    Messages.Message("RP_TunnelFound".Translate(guard.LabelShort, owner.LabelShort), owner, MessageTypeDefOf.PositiveEvent);
                    Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_TunnelFound".Translate(owner.LabelShort, guard.LabelShort), owner, guard);
                }
            // The wing saw the cell turned over (the rumour mill).
            if (searched != null) Rumours.Notify_CellSearch(searched);
            return found;
        }
    }

    /// <summary>Guards search cells: during shakedowns every cell, otherwise cells of suspects.</summary>
    public class WorkGiver_SearchCell : WorkGiver
    {
        // (The care reserve takes no search: see CareDetail.)
        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !GuardUtility.IsGuard(pawn) || !forced && CareDetail.IsReserve(pawn);

        public override Job NonScanJob(Pawn pawn)
        {
            PrisonGame game = PrisonGame.Instance;
            bool shakedown = game.shakedownUntilTick > Find.TickManager.TicksGame;
            foreach (Pawn inmate in pawn.Map.mapPawns.PrisonersOfColonySpawned)
            {
                CompInmate c = inmate.Inmate();
                if (c == null) continue;
                Room cell = InmateUtility.CellOf(inmate);
                if (cell == null || Find.TickManager.TicksGame - c.lastCellSearchTick < GenDate.TicksPerHour * 8) continue;
                if (!shakedown && !c.cellSearchPending) continue;
                Building_Bed bed = inmate.ownership.OwnedBed;
                if (!pawn.CanReserveAndReach(bed, PathEndMode.Touch, Danger.Some)) continue;
                c.lastCellSearchTick = Find.TickManager.TicksGame;
                return JobMaker.MakeJob(RPIntelDefOf.RP_SearchCell, bed);
            }
            return null;
        }
    }

    public class JobDriver_SearchCell : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil search = Toils_General.Wait(400);
            search.WithProgressBarToilDelay(TargetIndex.A);
            yield return search;
            Toil done = ToilMaker.MakeToil("SearchDone");
            done.initAction = () =>
            {
                Room cell = job.targetA.Thing.GetRoom();
                if (cell != null) TunnelSystem.SearchCell(pawn, cell);
            };
            yield return done;
        }
    }

    [DefOf]
    public static class RPIntelDefOf
    {
        public static JobDef RP_SearchCell;

        static RPIntelDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPIntelDefOf));
    }
}
