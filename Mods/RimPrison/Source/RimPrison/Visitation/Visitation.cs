using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    [DefOf]
    public static class RPVisitDefOf
    {
        public static PrisonZoneDef RP_Visitation;
        public static DutyDef RP_VisitInmate;
        public static JobDef RP_VisitTalk;
        public static NeedDef RP_Family;
        public static ThoughtDef RP_FamilyVisited;
        public static PawnKindDef RP_Visitor;
        public static ThingDef RP_VisitationBooth;

        static RPVisitDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPVisitDefOf));
    }

    /// <summary>How a security level is visited: across a table, through glass, or not at all.</summary>
    public enum VisitPolicy : byte
    {
        Contact,
        Booth,
        None,
    }

    /// <summary>
    /// Family visits: during free time a relative may come to the visitation room. The inmate leaves the yard to meet
    /// them; a good visit lifts the family need and calms them down. Visitors sometimes slip contraband across the
    /// table, unless a guard is watching.
    /// </summary>
    public static class VisitationSystem
    {
        public const int VisitTicks = 1500;
        public const int MaxConcurrentVisits = 6;
        /// <summary>Family need restored by a visit across a table / through glass.</summary>
        public const float ContactFamily = 0.6f, BoothFamily = 0.35f;

        public static int Booths(Map map, PrisonMap pm)
        {
            int n = 0;
            foreach (Building b in map.listerBuildings.AllBuildingsColonistOfDef(RPVisitDefOf.RP_VisitationBooth))
                if (pm.zones.ZoneAt(b.Position) == RPVisitDefOf.RP_Visitation) n++;
            return n;
        }

        /// <summary>Hourly: schedule new visits for inmates with family who want one.</summary>
        public static void HourlyTick(Map map, PrisonGame game, int hour)
        {
            if (hour < 9 || hour > 18) return;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || !pm.zones.Any(RPVisitDefOf.RP_Visitation)) return;
            int active = 0, boothsInUse = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { visitor: not null } vc)
                {
                    // (A visit under way whose spot was kept for other levels since is ended: it does not count then.)
                    KeepVisitInBounds(p, vc, pm);
                    if (vc.visitor == null) continue;
                    active++;
                    if (vc.boothVisit) boothsInUse++;
                }
            int booths = Booths(map, pm);
            // Room size sets how many visits fit at once (a table per 8 cells or so).
            int maxVisits = Mathf.Max(2, Mathf.Min(MaxConcurrentVisits * 3, pm.zones.CellsOf(RPVisitDefOf.RP_Visitation).Count / 8));
            // A restorative meeting the warden approved goes before the families' own visits (see Amends).
            Amends.HourlyVisits(map, game, hour, maxVisits, booths, boothsInUse, ref active);
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (active >= maxVisits) break;
                CompInmate c = p.Inmate();
                if (c == null || c.familyCount <= 0 || !CanBeVisited(p, c, game, hour, booths, boothsInUse, out VisitPolicy policy)) continue;
                // (A shot-caller's deal that promises the gang's members visits calls one who is owed a visit, whatever their need and the odds.)
                if (!GangDeals.VisitOwed(c))
                {
                    Need family = p.needs.TryGetNeed(RPVisitDefOf.RP_Family);
                    if (family == null || family.CurLevel > 0.55f) continue;
                    // Thorough searches put families off.
                    if (!Rand.Chance(0.08f * c.familyCount * (game.visitorSearch == VisitorSearchPolicy.Thorough ? 0.7f : 1f))) continue;
                }
                if (!SpawnVisitor(map, p, c, policy == VisitPolicy.Booth)) continue;
                active++;
                if (policy == VisitPolicy.Booth) boothsInUse++;
            }
            // A former inmate with a friend inside may come too (a kept world pawn, not a relative).
            ExInmates.HourlyVisits(map, game, hour, maxVisits, booths, ref active, ref boothsInUse);
        }

        /// <summary>
        /// A visitor who waits, or is on the way to wait, in a patch kept for other levels than the inmate's (the patch was kept since, or the inmate was moved to another level) ends the visit: the
        /// visitor leaves, and nobody is relocated. Returns false when the visit ended, so no talk is started this time.
        /// </summary>
        public static bool KeepVisitInBounds(Pawn inmate, CompInmate c, PrisonMap pm)
        {
            Pawn visitor = c.visitor;
            if (visitor == null || pm == null || !pm.zones.AnyKept || c.security == null) return true;
            var job = visitor.GetLord()?.LordJob as LordJob_VisitInmate;
            bool waitingInKept = pm.zones.ZoneAt(visitor.Position) == RPVisitDefOf.RP_Visitation && !pm.zones.Allows(visitor.Position, c.security);
            bool headedForKept = job != null && job.Spot.IsValid && !pm.zones.Allows(job.Spot, c.security);
            if (!waitingInKept && !headedForKept) return true;
            EndVisit(c, visitor);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:test] kept rooms: the visit of {inmate.LabelShort} ended (the visitor's spot is in a patch kept for other levels)");
            return false;
        }

        /// <summary>
        /// Whether a visitor could be called for this inmate at this hour: nothing in the way (a level that sees no one, a visit already on, a punishment, a ban, laid up, locked up by the time
        /// they would come) and a way to meet (a table, or a booth that is free). <paramref name="policy"/>: how.
        /// </summary>
        internal static bool CanBeVisited(Pawn p, CompInmate c, PrisonGame game, int hour, int booths, int boothsInUse, out VisitPolicy policy)
        {
            policy = VisitPolicy.None;
            // (Supermax inmates see no one: a visit would take them out of the cell without a guard.)
            if (!c.activeFlag || c.visitor != null || c.forcedActivity != null || c.security is { escortOnly: true }) return false;
            // Laid up (downed, or in bed for medical reasons): they could not come to the table; nobody is called.
            if (p.Downed || HealthAIUtility.ShouldSeekMedicalRest(p)) return false;
            if (c.visitBanUntilTick > Find.TickManager.TicksGame) return false;
            // The visitor is here in about an hour: no point if the inmate is locked up (or asleep) by then.
            if (game.ActivityAt(c, (hour + 1) % 24).lockup) return false;
            policy = game.VisitPolicyFor(c.security);
            return policy != VisitPolicy.None && (policy != VisitPolicy.Booth || boothsInUse < booths);
        }

        /// <param name="former">A former inmate who comes to see a friend (their kept world pawn is the visitor: see <see cref="ExInmates"/>), not a relative made up for the visit.</param>
        /// <param name="amends">A relative of the victim, for a restorative meeting (see <see cref="Amends"/>): no family of the inmate's, and nothing smuggled; searched at the gate like anyone.</param>
        internal static bool SpawnVisitor(Map map, Pawn inmate, CompInmate c, bool booth, FormerInmate former = null, bool amends = false)
        {
            // (Nobody who may come: the family left is the outside supplier the warden barred, who takes no visit.)
            if (former == null && !amends && Relatives.AllBarred(c)) return false;
            Faction outside = InmateGenerator.OutsideFaction;
            if (!RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 entry, map, CellFinder.EdgeRoadChance_Always)) return false;
            Pawn visitor;
            if (former != null)
            {
                visitor = ExInmates.PrepareVisitor(former, outside);
                // (A friend, not family: no relative's name for the ring's "someone outside" or for a death in the family.)
                c.visitorRelative = null;
            }
            else
            {
                visitor = PawnGenerator.GeneratePawn(new PawnGenerationRequest(RPVisitDefOf.RP_Visitor, outside, PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowFood: false));
                // The visitor is someone from the inmate's family, by name (the victim's relative keeps a name of their own).
                if (amends) c.visitorRelative = null;
                else if (Relatives.RandomVisitor(c) is { } rel)
                {
                    visitor.Name = new NameTriple(rel.name, rel.name, (inmate.Name as NameTriple)?.Last ?? "");
                    c.visitorRelative = rel.Label;
                }
                else c.visitorRelative = null;
            }
            GenSpawn.Spawn(visitor, entry, map);
            // (Spawning takes a pawn out of the world, and with it the keep: put it back, so that the world takes them back when they leave.)
            if (former != null) ExInmates.KeepWhileOut(visitor);
            visitor.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Guest);
            IntVec3 spot = RegimeActivityWorker.RandomReachableZoneCell(visitor, PrisonMap.Of(map), RPVisitDefOf.RP_Visitation, c.security);
            if (!spot.IsValid)
            {
                // (A former inmate goes back to the world, still kept; a made-up relative is no one.)
                if (former != null)
                {
                    ExInmates.ReturnFromVisit(visitor);
                    c.visitorRelative = null;
                }
                else visitor.Destroy();
                return false;
            }
            LordMaker.MakeNewLord(outside, new LordJob_VisitInmate(inmate, spot, amends), map, new List<Pawn> { visitor });
            c.visitor = visitor;
            c.visitorFormer = former != null;
            c.boothVisit = booth;
            // Nothing crosses the glass.
            bool dealer = c.reputations.Exists(r => r.behaviorClass == typeof(ReputationBehavior_Dealer)) || former?.past is { dealer: true };
            // (The victim's family brings nothing in, and what was arranged over the phone is for the inmate's own.)
            float smuggle = amends ? 0f : SmuggleChance(inmate, c);
            if (former?.past != null) smuggle = ExInmates.SmuggleChance(smuggle, former.past);
            c.visitorContraband = !booth && !amends && Rand.Chance(smuggle) ? ContrabandUtility.PickSmuggled(inmate, dealer ? ContrabandKind.Drug : null) : null;
            if (!amends) c.smuggleArranged = false;
            // The tap's flag goes with the handover it overheard.
            c.visitFlagged = !amends && c.nextVisitFlagged;
            if (!amends) c.nextVisitFlagged = false;
            c.visitorArrivedTick = -1; // set when they reach the visitation room
            c.visitorStripSearched = false;
            // Glass needs no search; otherwise policy (or a phone-tap flag) decides.
            c.visitorSearchPending = !booth && VisitorSearch.PolicyFor(c, PrisonGame.Instance) != VisitorSearchPolicy.None;
            if (booth) c.visitFlagged = false;
            Messages.Message((amends ? "RP_AmendsVisitorArrived" : "RP_VisitorArrived").Translate(visitor.LabelShort, inmate.LabelShort), visitor, MessageTypeDefOf.SilentInput, historical: false);
            return true;
        }

        public static float SmuggleChance(Pawn inmate, CompInmate c)
        {
            // Taken out of a smuggling ring lately: the family is watched, nothing comes in.
            if (RingSystem.SupplyCut(inmate)) return 0f;
            // Arranged over the phone: the visitor comes ready.
            if (c.smuggleArranged) return 0.75f;
            float chance = 0.08f + c.tension / 500f;
            if (c.crime != null && c.crime.drugRelated) chance *= 2f;
            if (c.security != null) chance *= Mathf.Lerp(0.7f, 1.5f, Mathf.InverseLerp(0.5f, 2f, c.security.dangerFactor));
            return chance;
        }

        /// <summary>Visit over: needs, mood, tension; contraband changes hands unless a guard sees it.</summary>
        public static void Complete(Pawn inmate, Pawn visitor)
        {
            CompInmate c = inmate.Inmate();
            if (c == null) return;
            // The victim's family: no family need, money or news from home, and no tension off for a visit - the meeting's own outcome instead.
            if (c.amendsState == AmendsState.Meeting && c.visitor == visitor)
            {
                PrisonGame.Instance.stats.visits++;
                Amends.Complete(inmate, c, visitor);
                EndVisit(c, visitor);
                return;
            }
            Need family = inmate.needs.TryGetNeed(RPVisitDefOf.RP_Family);
            // A strip-searched family is a tense family.
            float indignity = c.visitorStripSearched ? 0.75f : 1f;
            if (family != null) family.CurLevel += (c.boothVisit ? BoothFamily : ContactFamily) * indignity;
            inmate.needs.mood?.thoughts.memories.TryGainMemory(RPVisitDefOf.RP_FamilyVisited);
            c.tension = Mathf.Max(0f, c.tension - 15f * indignity);
            // (A friend who got out brings no family money and no news from home.)
            if (!c.visitorFormer) CommissarySystem.FamilyDeposit(c);
            PrisonGame.Instance.stats.visits++;
            // (A friend who got out is no family: a shot-caller's deal that promises the gang family visits does not count that visit.)
            if (!c.visitorFormer) c.lastVisitTick = Find.TickManager.TicksGame;
            if (c.visitorFormer) Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_VisitedByFormer".Translate(inmate.LabelShort, visitor?.LabelShort ?? c.visitorRelative, ExInmates.EntryOf(visitor)?.past?.daysServed.ToString("0") ?? "?"), inmate);
            else if (c.visitorRelative != null)
            {
                Story.Record(RPChronicleDefOf.RP_Chr_Life, 1, "RP_Chr_Visited".Translate(inmate.LabelShort, c.visitorRelative), inmate);
                Wants.Notify_FamilyNews(inmate, c, FamilyNews.Contact);
            }
            if (c.visitorContraband != null)
            {
                bool watched = TensionUtility.GuardsNear(inmate, PrisonMap.Of(inmate.Map)) > 0 && Rand.Chance(0.6f);
                if (watched)
                {
                    c.suspected = true;
                    Messages.Message("RP_SmuggleSpotted".Translate(visitor.LabelShort, inmate.LabelShort), inmate, MessageTypeDefOf.NeutralEvent);
                }
                else
                {
                    ContrabandUtility.Give(inmate, c.visitorContraband, forDebt: true);
                    // (A ring's member has someone outside who brings it, and a courier carries it for the dealer.)
                    RingSystem.Notify_BroughtIn(inmate, c.visitorRelative, c.visitorContraband);
                }
            }
            EndVisit(c, visitor);
        }

        /// <summary>The inmate a visitor came for.</summary>
        public static Pawn InmateOf(Pawn visitor) => VisitOf(visitor)?.Inmate;

        /// <summary>The visit a visitor is on (under way, or over and walking out), or null.</summary>
        public static LordJob_VisitInmate VisitOf(Pawn visitor) => visitor?.GetLord()?.LordJob as LordJob_VisitInmate;

        public static void EndVisit(CompInmate c, Pawn visitor)
        {
            c.visitor = null;
            c.visitorFormer = false;
            c.visitorContraband = null;
            c.boothVisit = false;
            c.visitorSearchPending = false;
            c.visitorStripSearched = false;
            c.visitFlagged = false;
            Amends.Notify_VisitEnded(c);
            visitor?.GetLord()?.ReceiveMemo(LordJob_VisitInmate.MemoDone);
        }
    }

    public class LordJob_VisitInmate : LordJob
    {
        public const string MemoDone = "VisitDone";

        private Pawn inmate;
        private IntVec3 spot;
        private bool amends;

        public LordJob_VisitInmate()
        {
        }

        public LordJob_VisitInmate(Pawn inmate, IntVec3 spot, bool amends = false)
        {
            this.inmate = inmate;
            this.spot = spot;
            this.amends = amends;
        }

        public Pawn Inmate => inmate;

        /// <summary>The visitor is the victim's relative, come for a restorative meeting (see <see cref="RimPrison.Amends"/>): kept with the visit itself, so it still holds once the visit is over and they walk out.</summary>
        public bool ForAmends => amends;

        /// <summary>Where the visitor waits.</summary>
        public IntVec3 Spot => spot;

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            var visit = new LordToil_VisitInmate(spot);
            graph.StartingToil = visit;
            var exit = new LordToil_ExitMap(LocomotionUrgency.Walk);
            graph.AddToil(exit);
            var done = new Transition(visit, exit);
            done.AddTrigger(new Trigger_Memo(MemoDone));
            // Five hours of waiting, but a conversation already under way is allowed to finish (it ended mid-talk and
            // gave the inmate nothing); two hours more at the very most.
            done.AddTrigger(new Trigger_Custom(signal => signal.type == TriggerSignalType.Tick && lord.ticksInToil > GenDate.TicksPerHour * 5
                && (inmate?.CurJobDef != RPVisitDefOf.RP_VisitTalk || lord.ticksInToil > GenDate.TicksPerHour * 7)));
            done.AddTrigger(new Trigger_PawnHarmed());
            done.AddPreAction(new TransitionAction_Custom(() =>
            {
                CompInmate c = inmate?.Inmate();
                if (c != null && c.visitor != null && lord.ownedPawns.Contains(c.visitor))
                {
                    if (AutoTest.Active)
                        RPLog.Message($"[visit] {c.visitor.LabelShort} left without a visit: {inmate.LabelShort} {PrisonGame.Instance.CurrentActivity(inmate)?.defName} job={inmate.CurJobDef?.defName} pending={c.visitorSearchPending}");
                    VisitationSystem.EndVisit(c, null);
                }
            }));
            graph.AddTransition(done);
            return graph;
        }

        public override void ExposeData()
        {
            Scribe_References.Look(ref inmate, "inmate");
            Scribe_Values.Look(ref spot, "spot");
            Scribe_Values.Look(ref amends, "amends");
        }
    }

    public class LordToil_VisitInmate : LordToil
    {
        private readonly IntVec3 spot;

        public LordToil_VisitInmate(IntVec3 spot) => this.spot = spot;

        public override void UpdateAllDuties()
        {
            foreach (Pawn p in lord.ownedPawns) p.mindState.duty = new PawnDuty(RPVisitDefOf.RP_VisitInmate, spot);
        }
    }

    /// <summary>Visitor: go to the visitation room and wait at a seat (or the spot).</summary>
    public class JobGiver_VisitorWait : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            IntVec3 spot = pawn.mindState.duty?.focus.Cell ?? IntVec3.Invalid;
            if (!spot.IsValid) return null;
            if (!pawn.Position.InHorDistOf(spot, 3f))
            {
                Job go = JobMaker.MakeJob(JobDefOf.Goto, spot);
                go.locomotionUrgency = LocomotionUrgency.Walk;
                return go;
            }
            Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
            wait.expiryInterval = 300;
            return wait;
        }
    }

    /// <summary>Inmate side: walk to the visitor and talk across the table.</summary>
    public class JobDriver_VisitTalk : JobDriver
    {
        private Pawn Visitor => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Visitor.GetLord()?.CurLordToil is not LordToil_VisitInmate);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil talk = Toils_General.Wait(VisitationSystem.VisitTicks);
            talk.handlingFacing = true;
            talk.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(Visitor);
                if (pawn.IsHashIntervalTick(400, delta)) MoteMaker.MakeInteractionBubble(pawn, Visitor, ThingDefOf.Mote_Speech, InteractionDefOf.Chitchat.GetSymbol());
            };
            talk.WithProgressBarToilDelay(TargetIndex.A);
            yield return talk;
            Toil done = ToilMaker.MakeToil("VisitDone");
            done.initAction = () => VisitationSystem.Complete(pawn, Visitor);
            yield return done;
        }
    }
}
