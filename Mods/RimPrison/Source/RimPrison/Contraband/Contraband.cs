using System.Linq;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    public enum ContrabandKind : byte
    {
        Weapon,
        Drug,
        Tool,
        Phone,
        /// <summary>Something that works in the stash until it is a drink (the mash of the hooch, see <see cref="HoochUtility"/>).</summary>
        Brew,
    }

    /// <summary>Marks a thing as contraband for inmates and says how it can be found.</summary>
    public class ContrabandExtension : DefModExtension
    {
        public ContrabandKind kind;
        /// <summary>Found by metal detectors.</summary>
        public bool metal;
        /// <summary>Weight when visitors / drops pick something to smuggle.</summary>
        public float smuggleWeight = 1f;
        public int stackCount = 1;
    }

    public static class ContrabandUtility
    {
        public static ContrabandExtension Ext(ThingDef def) => def.GetModExtension<ContrabandExtension>();

        public static bool IsContraband(Thing t) => t.def.GetModExtension<ContrabandExtension>() != null;

        private static List<ThingDef> allContraband;

        public static List<ThingDef> AllContraband
        {
            get
            {
                if (allContraband == null)
                {
                    allContraband = new List<ThingDef>();
                    foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                        if (d.HasModExtension<ContrabandExtension>()) allContraband.Add(d);
                }
                return allContraband;
            }
        }

        /// <summary>Contraband the inmate carries (inventory and equipment).</summary>
        public static void CarriedContraband(Pawn p, List<Thing> into)
        {
            into.Clear();
            if (p.inventory != null)
                foreach (Thing t in p.inventory.innerContainer)
                    if (IsContraband(t)) into.Add(t);
            if (p.equipment?.Primary is { } eq && IsContraband(eq)) into.Add(eq);
        }

        public static bool CarriesContraband(Pawn p, bool metalOnly = false)
        {
            if (p.inventory != null)
                foreach (Thing t in p.inventory.innerContainer)
                    if (Ext(t.def) is { } e && (!metalOnly || e.metal)) return true;
            return p.equipment?.Primary is { } eq && Ext(eq.def) is { } ee && (!metalOnly || ee.metal);
        }

        /// <summary>Puts a random smuggled item into the inmate's pockets (<paramref name="forDebt"/>: see <see cref="Give"/>).</summary>
        public static Thing GiveSmuggled(Pawn inmate, ContrabandKind? kind = null, bool forDebt = false) => Give(inmate, PickSmuggled(inmate, kind), forDebt);

        /// <summary>A random contraband def for this inmate (of <paramref name="kind"/> if given).</summary>
        public static ThingDef PickSmuggled(Pawn inmate, ContrabandKind? kind = null)
        {
            // Dangerous inmates want tools and weapons more than a high.
            float danger = inmate.Inmate()?.security?.dangerFactor ?? 1f;
            float Weight(ThingDef d)
            {
                ContrabandExtension e = Ext(d);
                if (kind != null && e.kind != kind) return 0f;
                return e.smuggleWeight * (danger >= 2f && e.kind is ContrabandKind.Tool or ContrabandKind.Weapon ? 3f : 1f);
            }
            return AllContraband.TryRandomElementByWeight(Weight, out ThingDef def) ? def : null;
        }

        /// <summary>How many of this item one delivery (<see cref="Give"/>) puts in the pockets.</summary>
        public static int DeliveryCount(ThingDef def) => Mathf.Min(Ext(def).stackCount, def.stackLimit);

        /// <param name="forDebt">A delivery that was arranged from outside (a visit, the post, a book, a guard on the take): for an inmate who arranged one to pay a debt, it goes to the creditor.
        /// Not one that just turns up in their pockets (a workshop, a plate from the gym, a guard's mistake): that is theirs.</param>
        /// <param name="smuggled">Whether it counts as smuggled in (the Statistics tab, the day's security grade): not for what is made inside (a harvest from the garden).</param>
        /// <param name="tracedTo">A tool taken out of a room's supply (see <see cref="ToolCount"/>): one thing, traced to the room and its work period before it is handed over, and never stacked.</param>
        public static Thing Give(Pawn inmate, ThingDef def, bool forDebt = false, bool smuggled = true, ToolPool tracedTo = null)
        {
            if (def == null) return null;
            Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            t.stackCount = tracedTo != null ? 1 : DeliveryCount(def);
            var before = inmate.inventory.innerContainer.Where(x => x.def == def).ToDictionary(x => x, x => x.stackCount);
            if (tracedTo != null) ToolCount.Trace(t, tracedTo);
            if (!inmate.inventory.innerContainer.TryAdd(t))
            {
                if (tracedTo != null) ToolCount.Untrace(t);
                return null;
            }
            if (smuggled)
            {
                PrisonGame.Instance.stats.contrabandSmuggled++;
                PrisonGame.Instance.today.contrabandSmuggled++;
            }
            // Merged into a stack they already had: the new thing is gone, return the stack it joined.
            if (t.Destroyed || t.holdingOwner != inmate.inventory.innerContainer)
                t = inmate.inventory.innerContainer.FirstOrDefault(x => x.def == def && before.TryGetValue(x, out int was) && x.stackCount > was)
                    ?? inmate.inventory.innerContainer.FirstOrDefault(x => x.def == def) ?? t;
            // Brought in to pay a debt: it goes to the creditor.
            if (forDebt) DebtSystem.Notify_Smuggled(inmate, t);
            return t;
        }

        /// <summary>For a check: the theft's roll, decided (null: the roll as it falls).</summary>
        internal static bool? TheftRollForTests;

        /// <summary>
        /// Workshop tools walk out in pockets unless a guard is watching. In a room whose tools are counted (<paramref name="zone"/>, see <see cref="ToolCount"/>) the tool comes out of the room's supply and is traced to it, and the old
        /// creation does not run: a room with nothing left in its stores loses nothing. Elsewhere (the garden) it is made as it always was.
        /// </summary>
        public static void Notify_WorkedWithTools(Pawn inmate, ContrabandKind kind = ContrabandKind.Tool, PrisonZoneDef zone = null)
        {
            CompInmate c = inmate.Inmate();
            if (c == null || c.tension < 25f) return;
            if (TensionUtility.GuardsNear(inmate, PrisonMap.Of(inmate.Map)) > 0) return;
            if (!(TheftRollForTests ?? Rand.Chance(0.06f))) return;
            if (ToolCount.Ledgered(zone)) ToolCount.Steal(inmate, kind, zone);
            else GiveSmuggled(inmate, kind);
        }

        /// <summary>A guard searches an inmate: everything found is confiscated and counts as misconduct.</summary>
        public static int Search(Pawn guard, Pawn inmate, float findChance)
        {
            if (CorruptionSystem.LooksAway(guard, inmate)) findChance *= 0.15f;
            // A routine frisk depends on the guard's state of mind; the certain pat-down of a restraint does not.
            if (findChance < 1f) findChance *= StaffMorale.SearchFactor(guard);
            var found = new List<Thing>();
            CarriedContraband(inmate, found);
            PrisonMap pm = PrisonMap.Of(inmate.MapHeld);
            int n = 0;
            foreach (Thing t in found)
            {
                if (!Rand.Chance(findChance)) continue;
                ContrabandKind kind = Ext(t.def).kind;
                if (t.ParentHolder is Pawn_EquipmentTracker eq) eq.Remove((ThingWithComps)t);
                else inmate.inventory.innerContainer.Remove(t);
                ToolCount.Notify_Taken(t, ToolSource.Pockets);
                t.Destroy();
                n++;
                pm?.AddFind(inmate.PositionHeld, kind, FindSource.Pockets);
            }
            ToolCount.Notify_Searched(inmate);
            CompInmate c = inmate.Inmate();
            if (c != null) c.lastSearchedTick = Find.TickManager.TicksGame;
            if (n > 0 && c?.informedBy != null) InformantSystem.Notify_TipActedOn(inmate, c);
            if (n > 0 && c != null) GuardEvidence(inmate, c);
            if (n > 0 && c != null) CorruptionSystem.Notify_ContrabandFound(inmate, c);
            if (n > 0 && c != null)
            {
                PrisonGame.Instance.stats.contrabandFound += n;
                PrisonGame.Instance.today.contrabandFound += n;
                MisconductUtility.Record(inmate, c, PrisonMap.Of(inmate.Map), RPContrabandDefOf.RP_ContrabandPossession, evidence: true);
                MoteMaker.ThrowText(inmate.DrawPos, inmate.Map, "RP_ContrabandFound".Translate(n), 4f);
                Messages.Message("RP_ContrabandFoundMessage".Translate(guard.LabelShort, inmate.LabelShort, n), inmate, MessageTypeDefOf.NeutralEvent, historical: false);
            }
            return n;
        }

        /// <summary>Contraband on the ground in inmate space, not watched, within reach.</summary>
        public static Thing FindLooseContraband(Pawn p, PrisonMap pm)
        {
            if (pm == null || !Rand.Chance(0.5f)) return null;
            // Nothing of the kind lying anywhere on the map (nearly always): no search through the haulables around them.
            bool any = false;
            List<ThingDef> kinds = AllContraband;
            for (int i = 0; i < kinds.Count && !any; i++) any = p.Map.listerThings.ThingsOfDef(kinds[i]).Count > 0;
            if (!any) return null;
            // (Whether they are watched is about them, not the item: asked once, for the first item that is otherwise fit.)
            bool? watched = null;
            return GenClosest.ClosestThingReachable(p.Position, p.Map, ThingRequest.ForGroup(ThingRequestGroup.HaulableEver), PathEndMode.ClosestTouch,
                TraverseParms.For(p), 12f, t => Ext(t.def) != null && t.Spawned && pm.zones.ZoneAt(t.Position) is { inmateAccess: true } && p.CanReserve(t)
                                                && !(watched ??= TensionUtility.Watched(p, pm)));
        }

        private static void GuardEvidence(Pawn inmate, CompInmate c)
        {
            // Contraband found: their cell is worth a look too.
            c.cellSearchPending = true;
        }

        /// <summary>Armed inmates draw their weapon when violence starts.</summary>
        public static void Notify_ViolenceStarting(Pawn inmate)
        {
            if (inmate.equipment == null || inmate.equipment.Primary != null || inmate.inventory == null) return;
            foreach (Thing t in inmate.inventory.innerContainer)
            {
                if (Ext(t.def) is not { kind: ContrabandKind.Weapon } || t is not ThingWithComps weapon || !t.def.IsWeapon) continue;
                inmate.inventory.innerContainer.Remove(weapon);
                inmate.equipment.AddEquipment(weapon);
                return;
            }
        }

        /// <summary>Draws a hidden weapon (always true, so it can sit in a condition chain).</summary>
        public static bool DrawWeaponIfAny(Pawn inmate)
        {
            Notify_ViolenceStarting(inmate);
            return true;
        }

        /// <summary>Weapons go back into the pocket once things calm down (so they stay hidden).</summary>
        public static void HideWeapon(Pawn inmate)
        {
            if (inmate.equipment?.Primary is { } eq && Ext(eq.def) != null)
            {
                inmate.equipment.Remove(eq);
                inmate.inventory.innerContainer.TryAdd(eq);
            }
        }

        /// <summary>Hourly: an inmate with drugs may use them (tension relief, addiction risk).</summary>
        public static Job TryUseDrugs(Pawn inmate, CompInmate c)
        {
            if (inmate.inventory == null || c.tension < 35f || !Rand.Chance(0.3f)) return null;
            // In the cell, the stash is within reach.
            if (inmate.GetRoom() == InmateUtility.CellOf(inmate) && StashUtility.Has(c, ContrabandKind.Drug)) StashUtility.Take(inmate, c, ContrabandKind.Drug);
            foreach (Thing t in inmate.inventory.innerContainer)
            {
                if (Ext(t.def) is not { kind: ContrabandKind.Drug } || !t.def.IsIngestible) continue;
                Job job = JobMaker.MakeJob(JobDefOf.Ingest, t);
                job.count = 1;
                return job;
            }
            return null;
        }
    }

    [DefOf]
    public static class RPContrabandDefOf
    {
        public static MisconductDef RP_ContrabandPossession;
        public static ThingDef RP_Hooch;
        public static ThingDef RP_HoochMash;
        public static ThingDef RP_Cellphone;
        public static ThingDef SmokeleafJoint;
        public static CrimeDef RP_Crime_SmokeleafGrowing;
        public static JobDef RP_Frisk;
        public static WorkGiverDef RP_Frisk_WorkGiver;

        static RPContrabandDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPContrabandDefOf));
    }

    /// <summary>Contraband possession is found, never chosen: the worker never acts on its own.</summary>
    public class MisconductWorker_Never : MisconductWorker
    {
        public override bool CanDo(Pawn p, CompInmate c) => false;
    }

    /// <summary>Guards search inmates: suspects first (metal detector hits), then random inmates out of their cells.</summary>
    public class WorkGiver_Frisk : WorkGiver_Scanner
    {
        public const int SearchCooldownTicks = GenDate.TicksPerDay;

        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.Frisk(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            !GuardUtility.IsGuard(pawn) || pawn.Map.mapPawns.PrisonersOfColonySpawnedCount == 0 || (!forced && (CareDetail.IsReserve(pawn) || InmateCandidates.Shared.Frisk(pawn.Map).Count == 0));

        /// <summary>
        /// On the book cart's round, in a class, at a funeral or on the way to a hearing: a routine frisk waits for these (it ended the cart's round with the books on the cart, and a class or a hearing is a
        /// session the inmate is not given again). Not work or visits: a kitchen hand or a workman is where the tools go missing, and a visit is where contraband changes hands.
        /// </summary>
        public static bool AtAppointment(Pawn inmate) =>
            inmate.CurJobDef is { } d && (d == RPLibraryDefOf.RP_DeliverBooks || d == RPProgramDefOf.RP_AttendClass || d == RPParoleDefOf.RP_AttendHearing || d == RPFuneralDefOf.RP_AttendFuneral);

        /// <summary>A frisk that is not owed, of an inmate at an appointment: it waits (a guard who picked them while they stood about gives up on arriving).</summary>
        public static bool WaitsForAppointment(Pawn inmate) => inmate.Inmate() is { } c && !Owed(c) && AtAppointment(inmate);

        /// <summary>A frisk the inmate is due for, whatever they are doing: suspicion, a search owed, a shakedown, the search of a work crew the warden asked for (see <see cref="ToolCount"/>).</summary>
        public static bool Owed(CompInmate c) => c.suspected || c.awaitingSearch || PrisonGame.Instance.shakedownUntilTick > Find.TickManager.TicksGame || ToolCount.SearchOwed(c);

        /// <summary>For a check: the routine frisk's roll, decided (null: the roll as it falls).</summary>
        internal static bool? RoutineRollForTests;

        /// <summary>The tests of <see cref="HasJobOnThing"/> that do not depend on the guard (on their feet, and due a frisk, or picked for a routine one this window).</summary>
        public static bool Candidate(Pawn inmate)
        {
            if (inmate.Downed || inmate.InMentalState || inmate.Inmate() is not { activeFlag: true } c) return false;
            bool due = Owed(c);
            if (!due)
            {
                // Routine: occasional random frisks of inmates out and about. Not one at an appointment (see AtAppointment).
                if (Find.TickManager.TicksGame - c.lastSearchedTick < SearchCooldownTicks || inmate.GetRoom() == InmateUtility.CellOf(inmate) || AtAppointment(inmate)) return false;
                if (!(RoutineRollForTests ?? Rand.ChanceSeeded(0.02f, inmate.thingIDNumber ^ (Find.TickManager.TicksGame / 2500)))) return false;
            }
            else if (!c.suspected && !c.awaitingSearch && !ToolCount.SearchOwed(c) && Find.TickManager.TicksGame - c.lastSearchedTick < GenDate.TicksPerHour * 6) return false;
            return true;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && Candidate(inmate) && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Some, 1, -1, null, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPContrabandDefOf.RP_Frisk, t);
    }

    public class JobDriver_Frisk : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Inmate.InMentalState);
            Toil approach = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return approach;
            yield return Toils_Jump.JumpIf(approach, () => !pawn.Position.AdjacentTo8WayOrInside(Inmate.Position));
            Toil hold = ToilMaker.MakeToil("HoldInmate");
            hold.initAction = () =>
            {
                // (A routine frisk picked while they stood about: if they have gone to an appointment since, it waits for another time.)
                if (WorkGiver_Frisk.WaitsForAppointment(Inmate))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                Job wait = JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture);
                wait.expiryInterval = 150;
                Inmate.jobs.StartJob(wait, JobCondition.InterruptForced);
            };
            yield return hold;
            Toil search = Toils_General.Wait(120);
            search.handlingFacing = true;
            search.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Inmate);
            search.WithProgressBarToilDelay(TargetIndex.A);
            yield return search;
            Toil done = ToilMaker.MakeToil("Searched");
            done.initAction = () =>
            {
                CompInmate c = Inmate.Inmate();
                int found = ContrabandUtility.Search(pawn, Inmate, 0.85f);
                if (found > 0)
                {
                    StaffMemories.Note(c, pawn, StaffMemories.FriskFound);
                    DebtSystem.Notify_Searched(Inmate, DebtSystem.FriskFind);
                }
                ComplaintSystem.Notify_Searched(pawn, Inmate, found > 0);
                if (c != null)
                {
                    GangUtility.Notify_Frisked(Inmate, c);
                    if (c.awaitingSearch)
                    {
                        c.awaitingSearch = false;
                        PrisonGame.Instance.stats.intakeSearches++;
                    }
                    c.suspected = false;
                    c.informedBy = null; // a tip is spent on the search that acted on it
                    c.tension = Mathf.Min(100f, c.tension + 2f); // being searched is humiliating
                }
            };
            yield return done;
        }
    }

    /// <summary>Walking over a metal detector flags inmates who carry metal contraband.</summary>
    public class CompMetalDetector : ThingComp
    {
        public override void CompTickInterval(int delta)
        {
            if (!parent.IsHashIntervalTick(30, delta) || !parent.Spawned) return;
            foreach (IntVec3 c in parent.OccupiedRect())
            {
                List<Thing> things = c.GetThingList(parent.Map);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is not Pawn p || p.Inmate() is not { activeFlag: true } inmate || inmate.suspected) continue;
                    if (!ContrabandUtility.CarriesContraband(p, metalOnly: true)) continue;
                    inmate.suspected = true;
                    MoteMaker.ThrowText(p.DrawPos, p.Map, "RP_DetectorAlarm".Translate(), new UnityEngine.Color(1f, 0.3f, 0.3f), 3f);
                    SoundDefOf.TinyBell.PlayOneShot(new TargetInfo(parent.Position, parent.Map));
                }
            }
        }
    }
}
