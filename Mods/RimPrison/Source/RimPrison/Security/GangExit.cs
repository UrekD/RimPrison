using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// A way out of a gang. A member (not the leader) with doubts (a birth in the family, a mentor, a reform program passed, a friend killed in a war)
    /// may come to want out: the goal <c>RP_Want_LeaveGang</c>. It goes in two steps. A session with the chaplain or a counsellor (the inmate confides, so
    /// the warden learns their gang), then, some hours later, the renouncement: they are an ex-member from then on. The old gang puts out a green light on
    /// the debts' own schedule (<see cref="DebtSystem"/>: a threat at once, a beating <see cref="DebtSystem.BeatingDelayDays"/> later, with a gang mate going for them
    /// as a recorded fight, again after <see cref="DebtSystem.RepeatDays"/> days, three beatings at the most) and asks the warden what to do: protective custody,
    /// another wing, or nothing. The old gang's members hate them for <see cref="HatedDays"/> days (<see cref="ThoughtWorker_ExMember"/>) and they are left out of
    /// turf presence for as long. Until the warden knows the gang, the goal is not shown and nothing says it exists (<see cref="Wants.Quiet"/>).
    /// </summary>
    public static class GangExit
    {
        public const int HatedDays = 30, DoubtDays = 3, RenounceHours = 6, SessionTicks = 1200;
        public const float DoubtChance = 0.5f;
        public const float MarkFearRelief = 30f;

        private static int Now => Find.TickManager.TicksGame;

        /// <summary>Left a gang lately: the old gang hates them and they are not counted for turf (the window of <see cref="HatedDays"/>).</summary>
        public static bool ExActive(CompInmate c) => c?.exGang != null && Now - c.exGangTick < HatedDays * GenDate.TicksPerDay;

        public static bool Seeking(CompInmate c) => c?.want is { def: { } d, Resolved: false } && d == RPGangStructureDefOf.RP_Want_LeaveGang;

        // ---- doubts ----------------------------------------------------------------------------------------------

        /// <summary>Something shook them (see the triggers in <see cref="Wants"/>, <see cref="Mentors"/> and <see cref="DeathAftermath"/>): the daily pass may turn it into the goal to leave.</summary>
        public static void Doubt(Pawn p, CompInmate c)
        {
            if (c?.gang == null || c.gangLeader || !InmateUtility.IsActiveInmate(p)) return;
            c.gangDoubtTick = Now;
        }

        /// <summary>A gang war's death: the dead one's friends in the same gang (the war must be on) begin to doubt.</summary>
        public static void Notify_Died(Pawn victim, Map map)
        {
            if (victim.Inmate() is not { gang: { } gang } vc || PrisonGame.Instance?.story.arcs is not { } arcs) return;
            bool war = false;
            // (A war that ended with the victim's own death, as the war's leader, is over by now: it is still in the list, done, until the next hourly pass.)
            foreach (StoryArc arc in arcs)
                if ((!arc.done || vc.gangLeader) && arc.def == RPGangWarDefOf.RP_Arc_GangWar && GangWarArc.Gangs(arc, out GangDef a, out GangDef b) && (a == gang || b == gang)) war = true;
            if (!war) return;
            foreach (Pawn m in map.mapPawns.PrisonersOfColonySpawned)
                if (m != victim && m.Inmate() is { } c && c.gang == gang && PrisonRelations.Friends(m, victim)) Doubt(m, c);
        }

        /// <summary>The goal itself, once, from a doubt (<paramref name="chance"/>: a check's own odds).</summary>
        public static InmateWant RollDoubt(Pawn p, CompInmate c, float? chance = null)
        {
            if (c.gangDoubtTick < 0) return null;
            bool fresh = Now - c.gangDoubtTick < DoubtDays * GenDate.TicksPerDay;
            c.gangDoubtTick = -1;
            if (!fresh || c.gang == null || !Rand.Chance(chance ?? DoubtChance)) return null;
            InmateWant w = Wants.Form(p, c, RPGangStructureDefOf.RP_Want_LeaveGang);
            // (They would sooner say it to the warden than wait for the chaplain: a word with them is the session, see WardenAsks. Only of a gang the warden knows: a request from a member of a hidden gang would say
            // the gang and the goal exist, which nothing may until the warden knows it. That one waits for the chaplain.)
            if (w != null && c.gangKnown) WardenAsks.Raise(p, WardenAskReason.Doubter);
            return w;
        }

        // ---- the session -----------------------------------------------------------------------------------------

        /// <summary>The chaplain or counsellor has heard them out: the gang is out in the open, and the renouncement follows.</summary>
        public static void Notify_Session(Pawn staff, Pawn inmate)
        {
            CompInmate c = inmate.Inmate();
            if (c?.want is not { Resolved: false, step: 0 } w || w.def != RPGangStructureDefOf.RP_Want_LeaveGang || c.gang == null) return;
            w.step = 1;
            w.stepTick = Now + RenounceHours * GenDate.TicksPerHour;
            GangUtility.Reveal(c);
            string role = staff.GetComp<CompStaff>()?.role?.label ?? "";
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_GangSession".Translate(staff.LabelShort, role, inmate.LabelShort, c.gang.label), inmate, staff);
            staff.skills?.Learn(SkillDefOf.Social, 300f);
            Wants.Refresh(inmate, c);
        }

        // ---- the renouncement ------------------------------------------------------------------------------------

        /// <summary>They are out: an ex-member, hated, left out of turf presence, with the old gang's green light on them (when it has anyone inside) and the warden asked what to do.</summary>
        public static void Renounce(Pawn p, CompInmate c)
        {
            GangDef gang = c.gang;
            if (gang == null) return;
            Map map = p.MapHeld ?? Find.AnyPlayerHomeMap;
            // (Said out loud: the warden knows the gang, whatever else they did not know.)
            GangUtility.Reveal(c);
            // (Out of a war's cast first, while they are still of the gang: a representative of a gang they quit would speak for it.)
            foreach (StoryArc arc in PrisonGame.Instance.story.arcs)
                if (!arc.done && arc.def == RPGangWarDefOf.RP_Arc_GangWar) arc.def.Worker.Notify_Leaving(arc, p);
            c.exGang = gang;
            c.exGangTick = Now;
            c.exMarked = false;
            c.exBeatings = 0;
            GangRanks.SetRank(c, GangRank.Member);
            c.gang = null;
            c.gangKnown = false;
            c.gangTattoo = false;
            c.gangDoubtTick = -1;
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_GangRenounced".Translate(p.LabelShort, gang.label), p);
            // (Only when somebody can get at them: else a beating is promised that cannot come.)
            if (map == null || !InmateUtility.IsActiveInmate(p) || !Reachable(map, p, c)) return;
            c.exMarked = true;
            c.exNextTick = Now + DebtSystem.BeatingDelayDays * GenDate.TicksPerDay;
            Threaten(p, c, gang);
            Ask(p, gang, map);
        }

        private static void Threaten(Pawn p, CompInmate c, GangDef gang)
        {
            c.tension = Mathf.Min(100f, c.tension + DebtSystem.ThreatTension);
            c.mainCause = "RP_Cause_GreenLight";
            FearSystem.Scare(c, DebtSystem.ThreatFear);
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPGangStructureDefOf.RP_MarkedByGang);
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_GreenLight".Translate(gang.label, p.LabelShort), p);
            WatchList.Notify_Trigger(p, RiskTrigger.GreenLight);
        }

        private static void Ask(Pawn p, GangDef gang, Map map)
        {
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_LeftGang", "RP_LeftGangLabel".Translate(p.LabelShort, gang.label),
                "RP_LeftGangText".Translate(p.LabelShort, gang.label, DebtSystem.BeatingDelayDays), map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }

        // ---- the green light -------------------------------------------------------------------------------------

        /// <summary>Hourly: a beating that is due is tried (a gang mate in the leaver's room goes for them: where no guard can see is preferred, a watched place once it is half a day overdue).</summary>
        public static void Hourly(Map map)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i].Inmate() is { exMarked: true } c && Now >= c.exNextTick) TryBeat(inmates[i], c, map, pm);
        }

        /// <summary>The gang member, the highest rank first, who can get at them now (as <see cref="DebtSystem"/>'s enforcer can).</summary>
        public static Pawn Enforcer(Pawn p, CompInmate c, Map map)
        {
            Pawn best = null;
            int bestRank = -1;
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn o = inmates[i];
                if (o == p || o.Inmate() is not { } oc || oc.gang != c.exGang || !DebtSystem.CanBeat(o, p)) continue;
                int rank = (int)GangRanks.Of(oc);
                if (rank > bestRank) (best, bestRank) = (o, rank);
            }
            return best;
        }

        /// <summary>
        /// The old gang's highest-ranked member the warden knows of (the leader first) who is in the prison: by rank alone, whether or not they could get at the leaver now (the order is for the placement, which acts over days; the
        /// beating is <see cref="Enforcer"/>'s). A member the warden does not know is not named: the order and its lines would give them away. One pass over the cached spawned prisoners, as the letter asks it every frame.
        /// </summary>
        public static Pawn Boss(Pawn p, CompInmate c, Map map)
        {
            Pawn best = null;
            int bestRank = -1;
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
            {
                Pawn o = inmates[i];
                if (o == p || o.Dead || o.Inmate() is not { gangKnown: true } oc || oc.gang != c.exGang) continue;
                int rank = (int)GangRanks.Of(oc);
                if (rank > bestRank) (best, bestRank) = (o, rank);
            }
            return best;
        }

        public static bool TryBeat(Pawn p, CompInmate c, Map map, PrisonMap pm)
        {
            if (!c.exMarked || DebtSystem.InCustody(c) || !InmateUtility.IsActiveInmate(p) || p.Downed || p.InMentalState || HostageCrisis.IsHeld(p)) return false;
            if (c.exBeatings >= DebtSystem.MaxBeatings)
            {
                c.exMarked = false;
                return false;
            }
            bool overdue = Now - c.exNextTick > 12 * GenDate.TicksPerHour;
            if (!overdue && TensionUtility.Watched(p, pm)) return false;
            Pawn enforcer = Enforcer(p, c, map);
            if (enforcer == null || !enforcer.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_AttackingInmate, null, forced: true, otherPawn: p)) return false;
            ContrabandUtility.Notify_ViolenceStarting(enforcer);
            CompInmate ec = enforcer.Inmate();
            // (Not "a green light from their old gang": that is the victim's, and would tie an unnamed enforcer to the gang.)
            ec.mainCause = "RP_Cause_WingTrouble";
            c.exBeatings++;
            c.exNextTick = Now + DebtSystem.RepeatDays * GenDate.TicksPerDay;
            // On the record: a fight (loud, so seen), with the gang's green light as its cause. (A member the warden does not know stays off the line.)
            MisconductUtility.Record(enforcer, ec, pm, RPMisconductDefOf.RP_Fight, chronicle: false);
            GangDef old = c.exGang;
            Story.Record(RPChronicleDefOf.RP_Chr_Violence, 2, (ec.gangKnown ? "RP_Chr_GreenLightBeating" : "RP_Chr_GreenLightBeatingUnnamed").Translate(enforcer.LabelShort, p.LabelShort, old.label),
                ec.gangKnown ? new[] { enforcer, p } : new[] { p });
            c.tension = Mathf.Min(100f, c.tension + DebtSystem.BeatingTension);
            FearSystem.Scare(c, DebtSystem.BeatingFear);
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPGangStructureDefOf.RP_BeatenForLeaving);
            WatchList.Notify_Trigger(p, RiskTrigger.GreenLight);
            PrisonRelations.MakeEnemies(p, enforcer);
            Wants.Notify_Grudge(c);
            c.mainCause = "RP_Cause_GreenLight";
            StaffMemories.Notify_BeatenInSight(p, c);
            return true;
        }

        // ---- the daily pass --------------------------------------------------------------------------------------

        /// <summary>Daily: doubts become goals; the old gang's green light ends when the hatred does or when it cannot get at them any more; an old hatred is forgotten.</summary>
        public static void Daily(Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p.Inmate() is not { } c) continue;
                if (c.gangDoubtTick >= 0 && InmateUtility.IsActiveInmate(p)) RollDoubt(p, c);
                if (c.exGang == null) continue;
                if (!ExActive(c))
                {
                    c.exGang = null;
                    c.exMarked = false;
                }
                else if (c.exMarked && !Reachable(map, p, c))
                {
                    c.exMarked = false;
                    Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 1, "RP_Chr_GreenLightOff".Translate(c.exGang.label, p.LabelShort), p);
                }
            }
        }

        /// <summary>Someone of the old gang is inside who can reach them: free, on the same wing, with the leaver neither in protective custody nor waiting for the gallows.</summary>
        public static bool Reachable(Map map, Pawn p, CompInmate c)
        {
            if (DebtSystem.InCustody(c) || DeathRowSystem.Ext(c) != null) return false;
            IReadOnlyList<Pawn> inmates = map.mapPawns.PrisonersOfColonySpawned;
            for (int i = 0; i < inmates.Count; i++)
                if (inmates[i] != p && inmates[i].Inmate() is { activeFlag: true } oc && oc.gang == c.exGang && oc.security == c.security && !DebtSystem.InCustody(oc) && DeathRowSystem.Ext(oc) == null) return true;
            return false;
        }

        // ---- the warden's choice ---------------------------------------------------------------------------------

        /// <summary>A wing they could go to away from the old gang: a level the warden has, with a bed free, not above their own in danger, with nobody of the gang on it. Null when there is none.</summary>
        public static SecurityLevelDef OtherWing(Pawn p, CompInmate c)
        {
            if (c.security == null || c.exGang == null || p.Map == null) return null;
            SecurityLevelDef best = null;
            foreach (SecurityLevelDef level in DefDatabase<SecurityLevelDef>.AllDefsListForReading)
            {
                if (level == c.security || level.escortOnly || level.referralOnly || level == RPSecurityDefOf.RP_Protective || level.GetModExtension<DeathRowExtension>() != null
                    || level.dangerFactor > c.security.dangerFactor || !Unlocks.IsUnlocked(level) || !InmateUtility.HasRoomIn(p, level) || GangOn(p.Map, level, c.exGang)) continue;
                if (best == null || level.dangerFactor > best.dangerFactor) best = level;
            }
            return best;
        }

        private static bool GangOn(Map map, SecurityLevelDef level, GangDef gang)
        {
            foreach (Pawn o in map.mapPawns.PrisonersOfColony)
                if (o.Inmate() is { } oc && oc.gang == gang && oc.security == level) return true;
            return false;
        }

        /// <summary>For the snapshot: everything saved about ranks, doubts and an ex-member in one string.</summary>
        public static string Describe(CompInmate c) =>
            $"{c.gangRank}:{c.gangTattoo}:{c.gangDoubtTick}:{c.exGang?.defName}:{c.exGangTick}:{c.exMarked}:{c.exNextTick}:{c.exBeatings}";
    }

    /// <summary>The goal to leave a gang (see <see cref="GangExit"/>): progress is the session, then the hours to the renouncement.</summary>
    public class WantWorker_LeaveGang : InmateWantWorker
    {
        public override bool CanForm(Pawn p, CompInmate c) => c.gang != null && !c.gangLeader && DeathRowSystem.Ext(c) == null;

        public override bool StillApplies(Pawn p, CompInmate c, InmateWant w) => c.gang != null && !c.gangLeader;

        public override void Begin(Pawn p, CompInmate c, InmateWant w)
        {
            base.Begin(p, c, w);
            w.step = 0;
            w.stepTick = -1;
        }

        public override void Evaluate(Pawn p, CompInmate c, InmateWant w)
        {
            if (w.step >= 1)
            {
                w.progress = 0.7f;
                w.status = WantStatus.OnTrack;
                if (Now >= w.stepTick) Wants.Fulfil(p, c, w);
                return;
            }
            w.progress = 0.15f;
            w.status = Behind(w, 0.5f) ? WantStatus.Slipping : WantStatus.OnTrack;
        }

        public override void Fulfilled(Pawn p, CompInmate c, InmateWant w) => GangExit.Renounce(p, c);

        public override TaggedString Detail(Pawn p, CompInmate c, InmateWant w) =>
            (w.step >= 1 ? "RP_Want_LeaveGang_Detail_Renounce" : "RP_Want_LeaveGang_Detail").Translate(c.gang?.label ?? c.exGang?.label ?? "?");
    }

    /// <summary>The chaplain or a counsellor sits down with an inmate who wants out of their gang.</summary>
    public class WorkGiver_GangExitSession : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => InmateCandidates.Shared.ExitSession(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !forced && InmateCandidates.Shared.ExitSession(pawn.Map).Count == 0;

        /// <summary>The tests of <see cref="HasJobOnThing"/> that do not depend on who is asking: able to talk, and wanting a session.</summary>
        public static bool Candidate(Pawn inmate) =>
            inmate.Inmate() is { want: { Resolved: false, step: 0 } w } && w.def == RPGangStructureDefOf.RP_Want_LeaveGang && PsychologyUtility.CanTalk(inmate);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false) =>
            t is Pawn inmate && Candidate(inmate) && pawn.CanReserveAndReach(inmate, PathEndMode.Touch, Danger.Some, 1, -1, null, forced);

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false) => JobMaker.MakeJob(RPGangStructureDefOf.RP_GangExitSession, t);
    }

    public class JobDriver_GangExitSession : JobDriver
    {
        private Pawn Inmate => (Pawn)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => !PsychologyUtility.CanTalk(Inmate) || !WorkGiver_GangExitSession.Candidate(Inmate));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil talk = Toils_General.Wait(GangExit.SessionTicks);
            talk.handlingFacing = true;
            talk.initAction = () =>
            {
                if (Inmate.CurJob is not { } cur || cur.def.casualInterruptible)
                    Inmate.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait_MaintainPosture, GangExit.SessionTicks + 50), JobCondition.InterruptForced);
            };
            talk.tickIntervalAction = _ => pawn.rotationTracker.FaceTarget(Inmate);
            talk.WithProgressBarToilDelay(TargetIndex.A);
            talk.FailOn(() => !pawn.Position.InHorDistOf(Inmate.Position, 3f));
            yield return talk;
            Toil done = ToilMaker.MakeToil("GangExitSessionDone");
            done.initAction = () => GangExit.Notify_Session(pawn, Inmate);
            yield return done;
        }
    }

    /// <summary>Opinion of an ex-member among the members of the gang they left, for <see cref="GangExit.HatedDays"/> days.</summary>
    public class ThoughtWorker_ExMember : ThoughtWorker
    {
        protected override ThoughtState CurrentSocialStateInternal(Pawn p, Pawn other)
        {
            if (!p.IsPrisonerOfColony || !other.IsPrisonerOfColony || p.Inmate() is not { gang: { } mine } pc || other.Inmate() is not { } oc || oc.exGang != mine || !GangExit.ExActive(oc)) return false;
            // (Only where the warden knows the holder's gang: a grudge shown in the Social tab toward an ex-member would point out every hidden member of the old gang.)
            return pc.gangKnown ? ThoughtState.ActiveAtStage(0) : ThoughtState.Inactive;
        }
    }

    /// <summary>The warden's answer to a renouncement: protective custody, another wing, keeping them apart from the gang's highest-ranked member, or nothing (the gang's beating comes on its schedule).</summary>
    public class LeftGangDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_LeftGang_PC", "RP_LeftGang_Wing", "RP_LeftGang_Apart", "RP_LeftGang_Nothing" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 3;

        private static Pawn Leaver(ChoiceLetter_PrisonDecision l) => l.pawns.Count > 0 && l.pawns[0] is { Spawned: true, Dead: false } p && InmateUtility.IsActiveInmate(p) && p.Inmate().exGang != null ? p : null;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            Pawn p = Leaver(l);
            if (p == null) return option == 3 ? null : "RP_LeftGang_Gone".Translate().ToString();
            if (option == 2) return NoApart(p);
            return option == 0 ? NoCustody(p) : option == 1 && GangExit.OtherWing(p, p.Inmate()) == null ? "RP_LeftGang_NoWing".Translate().ToString() : null;
        }

        /// <summary>Why they cannot be kept apart from the old gang's <see cref="GangExit.Boss"/> (null when they can): the warden knows nobody of it here, or they are kept apart from them already, or the list of orders is full.</summary>
        private static string NoApart(Pawn p)
        {
            Pawn boss = GangExit.Boss(p, p.Inmate(), p.MapHeld);
            if (boss == null) return "RP_LeftGang_NoBoss".Translate().ToString();
            if (KeepApart.Ordered(p, boss)) return "RP_LeftGang_AlreadyApart".Translate(boss.LabelShort).ToString();
            return PrisonGame.Instance.keepApart.Full ? "RP_KeepApartFull".Translate(KeepApartBook.Cap).ToString() : null;
        }

        /// <summary>Why protective custody cannot take them now (null when it can).</summary>
        private static string NoCustody(Pawn p)
        {
            SecurityLevelDef pc = RPSecurityDefOf.RP_Protective;
            if (pc == null || !Unlocks.IsUnlocked(pc) || !InmateUtility.HasRoomIn(p, pc)) return pc != null ? InmateUtility.NoRoomText(p, pc).ToString() : "RP_LeftGang_Gone".Translate().ToString();
            return null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = Leaver(l);
            if (p == null || option == 3) return;
            CompInmate c = p.Inmate();
            // (The letter may have sat for a day: the beds may have filled, or the other wing's gang come to it.)
            if (Disabled(l, option) is { } why)
            {
                Messages.Message(why, p, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (option == 2)
            {
                // (By rank, as the letter was offered: the placement keeps them from the gang's top, not only from whoever stood near them.)
                if (GangExit.Boss(p, c, p.MapHeld) is { } boss && KeepApart.Add(p, boss, "gang") == KeepApartResult.Added)
                    c.fear = Mathf.Max(0f, c.fear - GangExit.MarkFearRelief / 2f);
                return;
            }
            if (option == 0)
            {
                InmateUtility.ChangeSecurity(p, c, RPSecurityDefOf.RP_Protective, why: "RP_LeftGangWhy".Translate().ToString());
                c.fear = Mathf.Max(0f, c.fear - GangExit.MarkFearRelief);
            }
            else if (GangExit.OtherWing(p, c) is { } wing)
            {
                InmateUtility.ChangeSecurity(p, c, wing, why: "RP_LeftGangWhy".Translate().ToString());
                c.fear = Mathf.Max(0f, c.fear - GangExit.MarkFearRelief / 2f);
            }
        }
    }
}
