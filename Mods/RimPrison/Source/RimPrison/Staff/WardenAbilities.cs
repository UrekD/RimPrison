using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// The warden's three gizmos (see <see cref="WardenSystem"/>): what each is, why it cannot be used now, and the order that sends the warden to do it. The effects are the system's;
    /// the walking and the waiting are the jobs below.
    /// </summary>
    public static class WardenGizmos
    {
        /// <summary>An inmate the warden can walk up to for a word: one of the active, on the map, and in their right mind (not mid riot, rage or breakdown).</summary>
        public static bool CanTalkTo(Pawn p) => p.IsPrisonerOfColony && InmateUtility.IsActiveInmate(p) && p.Spawned && !p.InMentalState;

        private static Texture2D Icon(string path) => ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.BadTex;

        public static IEnumerable<Gizmo> For(Pawn warden, CompStaff sc)
        {
            Concepts.Teach(Concepts.Warden);
            var address = new Command_Action
            {
                defaultLabel = "RP_WardenAddressGizmo".Translate(),
                defaultDesc = "RP_WardenAddressGizmoDesc".Translate(WardenSystem.CalmTension.ToString("0"), WardenSystem.CalmSuppression.ToString("0"), WardenSystem.FirmSuppression.ToString("0"),
                    WardenSystem.FirmTensionPerHour.ToString("0.0"), WardenSystem.FirmHours, WardenSystem.AddressCooldownHours),
                icon = Icon("UI/Commands/Attack"),
                action = () => Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("RP_WardenAddressCalm".Translate(), () => WardenOrders.Address(warden, firm: false)),
                    new FloatMenuOption("RP_WardenAddressFirm".Translate(), () => WardenOrders.Address(warden, firm: true)),
                })),
            };
            string why = WardenSystem.Unready(warden, sc.addressReadyTick);
            if (why == null && WardenSystem.CanteenSpot(warden) is { IsValid: false }) why = "RP_WardenWhy_NoCanteen".Translate();
            if (why != null) address.Disable(why);
            yield return address;

            var walk = new Command_Action
            {
                defaultLabel = "RP_WardenWalkGizmo".Translate(),
                defaultDesc = "RP_WardenWalkGizmoDesc".Translate(WardenSystem.WalkCooldownHours),
                icon = Icon("UI/Commands/SquadAttack"),
                action = () =>
                {
                    var options = new List<FloatMenuOption>();
                    foreach (PrisonZoneDef wing in WardenSystem.Wings(warden.MapHeld))
                    {
                        PrisonZoneDef chosen = wing;
                        options.Add(new FloatMenuOption(wing.LabelCap, () => WardenOrders.Walk(warden, chosen)));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                },
            };
            why = WardenSystem.Unready(warden, sc.walkReadyTick);
            if (why == null && WardenSystem.Wings(warden.MapHeld).Count == 0) why = "RP_WardenWhy_NoWing".Translate();
            if (why != null) walk.Disable(why);
            yield return walk;

            var word = new Command_Action
            {
                defaultLabel = "RP_WardenWordGizmo".Translate(),
                defaultDesc = "RP_WardenWordGizmoDesc".Translate(WardenSystem.WordCooldownHours),
                icon = Icon("UI/Commands/Draft"),
                action = () => Find.Targeter.BeginTargeting(new TargetingParameters
                {
                    canTargetPawns = true,
                    canTargetBuildings = false,
                    canTargetItems = false,
                    validator = t => t.Thing is Pawn p && CanTalkTo(p),
                }, target =>
                {
                    if (target.Thing is not Pawn inmate) return;
                    var options = new List<FloatMenuOption>();
                    foreach (WordOption o in WardenSystem.WordOptions(warden, inmate))
                    {
                        WordOption opt = o;
                        options.Add(opt.disabled != null
                            ? new FloatMenuOption(opt.label + " (" + opt.disabled + ")", null)
                            : new FloatMenuOption(opt.label, () => WardenOrders.Word(warden, inmate, opt.kind)));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }),
            };
            why = WardenSystem.Unready(warden, sc.wordReadyTick);
            if (why != null) word.Disable(why);
            yield return word;
        }
    }

    /// <summary>The orders: each starts the job that does the thing, or says why not.</summary>
    public static class WardenOrders
    {
        private static bool Go(Pawn warden, Job job)
        {
            if (warden.jobs.TryTakeOrderedJob(job, JobTag.Misc)) return true;
            Messages.Message("RP_WardenWhy_Cannot".Translate(warden.LabelShort), warden, MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }

        public static bool Address(Pawn warden, bool firm)
        {
            IntVec3 spot = WardenSystem.CanteenSpot(warden);
            if (!spot.IsValid)
            {
                Messages.Message("RP_WardenWhy_NoCanteen".Translate(), warden, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            return AddressAt(warden, spot, firm);
        }

        /// <summary>The address in the canteen of a given cell (Intel's rumour row picks the canteen where the wing is: see <see cref="Rumours.CanteenSpotFor"/>).</summary>
        public static bool AddressAt(Pawn warden, IntVec3 spot, bool firm)
        {
            Job job = JobMaker.MakeJob(RPWardenDefOf.RP_WardenAddress, spot);
            job.count = firm ? 1 : 0;
            return Go(warden, job);
        }

        public static bool Walk(Pawn warden, PrisonZoneDef wing)
        {
            List<IntVec3> stops = WardenSystem.WalkPlan(warden, wing);
            if (stops.Count == 0)
            {
                Messages.Message("RP_WardenWhy_NoWing".Translate(), warden, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            Job job = JobMaker.MakeJob(RPWardenDefOf.RP_WardenWalk);
            job.targetQueueA = stops.Select(c => new LocalTargetInfo(c)).ToList();
            job.count = wing.gridIndex;
            return Go(warden, job);
        }

        public static bool Word(Pawn warden, Pawn inmate, WardenWord kind)
        {
            Job job = JobMaker.MakeJob(RPWardenDefOf.RP_WardenWord, inmate);
            job.count = (int)kind;
            return Go(warden, job);
        }
    }

    /// <summary>
    /// Walk to the canteen, stand and speak, and then the room has heard it. The same job, ordered with <see cref="EulogyMode"/> in its count, is the warden speaking at a colleague's funeral (see <see cref="Funerals"/>): the spot
    /// is the funeral's, and what it does when it is said is the funeral's too (no canteen, no cooldown).
    /// </summary>
    public class JobDriver_WardenAddress : JobDriver
    {
        public const int SpeakTicks = 420;
        /// <summary>The job's count for a speech at a funeral (0 and 1 are the calm and the firm address).</summary>
        public const int EulogyMode = 2;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil speak = Toils_General.Wait(SpeakTicks);
            speak.socialMode = RandomSocialMode.Off;
            yield return speak;
            Toil done = ToilMaker.MakeToil("WardenAddressed");
            done.initAction = () =>
            {
                if (job.count == EulogyMode)
                {
                    Funerals.Notify_Spoke(pawn);
                    return;
                }
                bool firm = job.count == 1;
                if (WardenSystem.Address(pawn, firm) == 0) Messages.Message("RP_WardenAddressNobody".Translate(pawn.LabelShort), pawn, MessageTypeDefOf.RejectInput, historical: false);
            };
            done.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return done;
        }
    }

    /// <summary>A walk through the wing, stop by stop: a pause at each, those in earshot met once, and at the end whatever they had to say.</summary>
    public class JobDriver_WardenWalk : JobDriver
    {
        public const int PauseTicks = 150;

        /// <summary>Who was met on this walk (thing IDs: once each), and how it went (warmer, resentful).</summary>
        private List<int> met = new List<int>();
        private int warmer, resentful;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref met, "met", LookMode.Value);
            Scribe_Values.Look(ref warmer, "warmer");
            Scribe_Values.Look(ref resentful, "resentful");
            met ??= new List<int>();
        }

        private PrisonZoneDef Wing => PrisonZoneDef.FromGridIndex((byte)job.count);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            List<LocalTargetInfo> stops = job.targetQueueA;
            for (int i = 0; i < stops.Count; i++)
            {
                yield return Toils_Goto.GotoCell(stops[i].Cell, PathEndMode.OnCell);
                yield return Toils_General.Wait(PauseTicks);
                Toil meet = ToilMaker.MakeToil("WardenMeets");
                meet.initAction = Meet;
                meet.defaultCompleteMode = ToilCompleteMode.Instant;
                yield return meet;
            }
            Toil finish = ToilMaker.MakeToil("WardenWalked");
            finish.initAction = () =>
            {
                Map map = pawn.MapHeld;
                var seen = new List<Pawn>();
                foreach (int id in met)
                    if (CourtUtil.FindInmate(map, id) is { } p) seen.Add(p);
                WardenSystem.FinishWalk(pawn, seen, warmer, resentful, Wing);
            };
            finish.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finish;
        }

        private void Meet()
        {
            // (The cooldown runs from the first stop, not the last: a walk broken off after two stops has still been walked, and a new job meets everyone again.)
            WardenSystem.StartWalkCooldown(pawn);
            if (Wing == null) return;
            foreach (Pawn p in WardenSystem.NearOnWing(pawn, Wing))
            {
                if (met.Contains(p.thingIDNumber)) continue;
                met.Add(p.thingIDNumber);
                int effect = WardenSystem.Meet(pawn, p);
                if (effect > 0) warmer++;
                else if (effect < 0) resentful++;
            }
        }
    }

    /// <summary>Go to the inmate (in their cell or wherever they are), stay with them a while, and say it.</summary>
    public class JobDriver_WardenWord : JobDriver
    {
        public const int TalkTicks = 450;
        /// <summary>The inmate must stay this close for the talk (one who walks off is not talked to).</summary>
        public const float StayWithin = 4f;

        private Pawn Inmate => job.targetA.Thing as Pawn;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !InmateUtility.IsActiveInmate(Inmate));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil talk = Toils_General.Wait(TalkTicks);
            talk.FailOn(() => !pawn.Position.InHorDistOf(Inmate.Position, StayWithin));
            talk.socialMode = RandomSocialMode.Off;
            yield return talk;
            Toil done = ToilMaker.MakeToil("WardenSaidIt");
            done.initAction = () => WardenSystem.Word(pawn, Inmate, (WardenWord)job.count);
            done.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return done;
        }
    }

    /// <summary>
    /// The walk to bed the hour before sleep, and the wait there until the timetable says sleep: not a casual job (a wish for recreation or a snack does not turn the warden round on the way, and
    /// the warden is at the bed, not across the prison, when the hour comes and they lie down).
    /// </summary>
    public class JobDriver_WardenRetire : JobDriver
    {
        public const int WaitTicks = 3000;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil wait = Toils_General.Wait(WaitTicks);
            wait.AddPreTickAction(() =>
            {
                if (pawn.timetable?.CurrentAssignment == TimeAssignmentDefOf.Sleep) ReadyForNextToil();
            });
            yield return wait;
        }
    }
}
