using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>
    /// Trusties, round 6 and 8 (see <see cref="Trusties"/>): a cut of a tree in the zone is never ended while it is valid. In real runs a trusty's cut of a tree that takes more than an hour (a slow cutter) began again from nothing
    /// every hour, so the tree was never felled: an hourly pass queued the trusty for a regime interrupt, which ends a cut, and a cut that is ended starts over. The hourly class pass (a class that takes the work slot) is the one
    /// that does it for every worker whose job has no work waiting (<see cref="ProgramSystem.KeepsToTheirJob"/>), and the outside job never said it had: it says so now (<see cref="InmateJobWorker_Outside.HasWork"/>), and the queue
    /// itself is nobody's to hold (a funeral's call, the one it once swallowed, must reach a trusty at a cut). Each check says what it would see without the change it guards.
    /// </summary>
    public static partial class AutoTest
    {
        private static void CheckTrustiesRound6(Map map)
        {
            CheckTrustyKeepsWorking(map);
            CheckTrustyClassesLeaveThem(map);
            CheckTrustyFuneralCall(map);
        }

        /// <summary>Queued inmates sent to their new job now (the queue is drained a few to a tick by the map's own tick: the check does not wait for it).</summary>
        private static void TrustyDrainInterrupts(PrisonMap pm)
        {
            for (int i = 0; i < 20 && pm.PendingInterrupts > 0; i++) pm.DrainInterrupts();
        }

        /// <summary>
        /// The party's hourly look at the running jobs, run for the check: the queue is empty before it (an entry left by an earlier step is for a job that is gone, and would stand in for the look's own request: the queue asks
        /// once per inmate until they are drained), the hour of the last look is put back so the look runs, and it is seen to run. Returns whether it queued the trusty.
        /// </summary>
        private static bool TrustyLook(TrustyScene s)
        {
            TrustyDrainInterrupts(s.pm);
            s.pm.party.lastHour = -1;
            s.Beat();
            bool ran = s.pm.party.lastHour >= 0;
            if (!ran) s.Fail("the check's own premise: the hourly look did not run");
            return ran && s.pm.PendingInterrupts > 0;
        }

        // ---- a valid cut is not ended ----------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A trusty at work on the strip with the party out and a cut of a tree in the zone under way: the party's hourly look of the running job (valid) leaves the cut be, and says the job has work (the class pass's word: the
        /// class check below). The ones that must move them still do (each is a control, true before and after the change): the party called in, a cut of a tree off the zone (the look sends them in), another slot, the status taken.
        /// </summary>
        private static void CheckTrustyKeepsWorking(Map map)
        {
            Guarded("trusties: a valid cut under way is left alone by the hourly look", map, r =>
            {
                const string check = "keeps working";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Plant tree = s.Tree(s.At(12, 3));
                Plant offTree = s.Tree(s.At(12, -5));
                Pawn guard = s.Guard("Ztkw1", s.At(10, 2));
                Pawn t = s.Trusty("Ztkw2", s.At(12, 2), claim: s.bed);
                CompInmate c = t.Inmate();
                s.Beat();
                if (!s.pm.party.isOut) { s.Fail($"the check's own premise: the party is not out: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                c.UpdateWorkPass();

                // A cut of a plant, started for them (the number of the job, for telling a job that went on from one that was started again).
                int Cut(Plant plant)
                {
                    t.jobs.StartJob(new Job(JobDefOf.CutPlant, plant), JobCondition.InterruptForced);
                    return t.CurJobDef == JobDefOf.CutPlant ? t.CurJob.loadID : -1;
                }

                bool Ended(int id) => t.CurJob?.loadID != id;
                int id = Cut(tree);
                if (id < 0) { s.Fail($"the check's own fixture: the cut did not start (doing {t.CurJobDef?.defName ?? "nothing"})"); TrustyStill(t, guard); return; }
                if (!InmateUtility.WouldInterruptForRegimeChange(t, emergency: false)) { s.Fail("the check's own premise: a cut is not something a regime change ends, so a queued interrupt is no test of anything"); TrustyStill(t, guard); return; }
                if (!t.Inmate().job.Worker.HasWork(t)) { s.Fail($"a trusty cutting a tree in the zone in the work hours with the party out is not said to have work: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }

                // The party's hourly look at the running job: a valid one is left alone. (The look runs at the first beat of an hour: the hour of the last look is put back for it, and the look is seen to run.)
                if (TrustyLook(s)) s.Fail("the party's hourly look asked for an interrupt of a valid cut");
                TrustyDrainInterrupts(s.pm);
                if (Ended(id)) s.Fail($"the party's hourly look ended a valid cut of a tree in the zone: now {t.CurJobDef?.defName ?? "nothing"} ({TrustyLine(s, t)})");
                TrustyStill(t);
                s.Place(t, s.At(12, 2));

                // Controls: what does move them.
                // (1) The party called in (a hostile threat on the map): the recall ends the cut.
                id = Cut(tree);
                if (id < 0) s.Fail("the check's own fixture: the cut did not start for the recall");
                else
                {
                    Trusties.ThreatForTests = true;
                    s.Beat();
                    TrustyDrainInterrupts(s.pm);
                    if (s.pm.party.isOut || !Ended(id)) s.Fail($"the party called in by a raid left a trusty at their cut (out {s.pm.party.isOut}, doing {t.CurJobDef?.defName ?? "nothing"})");
                    Trusties.ThreatForTests = false;
                    s.pm.party.holdUntilTick = -1;
                    s.Beat();
                    if (!s.pm.party.isOut) s.Fail($"the check's own fixture: the party did not go out again after the raid ({s.pm.party.state})");
                    // (Going out sends the trusty to look for work: a request for a job that is about to be stopped. It is drained here, not left in the queue to stand in for the next step's.)
                    TrustyDrainInterrupts(s.pm);
                    TrustyStill(t);
                    s.Place(t, s.At(12, 2));
                }
                // (2) A cut of a tree off the zone: the look sends them in.
                id = Cut(offTree);
                if (id < 0) s.Fail("the check's own fixture: the cut of the tree off the zone did not start");
                else if (Trusties.JobInZone(t.CurJob, s.pm)) s.Fail("the check's own premise: the cut of the tree off the zone passes the zone check");
                else
                {
                    // (A cut off the zone is not the work they are kept at: the look asks for the interrupt.)
                    bool queued = TrustyLook(s);
                    if (!queued && !Ended(id)) s.Fail("the party's hourly look did not queue the trusty at a cut of a tree off the zone");
                    TrustyDrainInterrupts(s.pm);
                    if (!Ended(id)) s.Fail("the party's hourly look left a trusty at a cut of a tree off the zone");
                }
                TrustyStill(t);
                s.Place(t, s.At(12, 2));
                // (3) Another slot (the regime puts them to free time): the change of activity moves them.
                s.RegimeAllDay(RPDefOf.RP_FreeTime);
                id = Cut(tree);
                if (id < 0) s.Fail("the check's own fixture: the cut did not start for the change of slot");
                else
                {
                    s.pm.QueueRegimeInterrupt(t);
                    TrustyDrainInterrupts(s.pm);
                    if (!Ended(id)) s.Fail("a regime interrupt left a trusty at their cut in a slot that is not work");
                }
                TrustyStill(t);
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                s.Place(t, s.At(12, 2));
                // (4) The status taken: they are sent to look for what the regime has for them.
                id = Cut(tree);
                if (id < 0) s.Fail("the check's own fixture: the cut did not start for the revoke");
                else
                {
                    Trusties.Revoke(t, null, silent: true);
                    TrustyDrainInterrupts(s.pm);
                    if (!Ended(id)) s.Fail("the status taken left the one who lost it at their cut");
                }
                TrustyStill(t, guard);
            });
        }

        // ---- a class does not take them ---------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The hourly class pass for a program that takes the work slot (foundation education: everyone, the work slot) collects its students from the inmates in the work slot except the ones whose job has work waiting. A trusty at the
        /// outside work with the party out and work in the zone is not collected (every attendee the pass starts a class for is queued for a regime interrupt, which ends a cut: without the outside job saying it has work the class took the
        /// trusty off the strip every hour); with the party in (the slot falls back to lockup) they are, so the pass is seen to look at trusties at all. Controls: a strip with no work in it gives them none (they are taught), and a course of
        /// the work slot the warden referred them to takes them in the referral pass; one at a cut that is the only work of the zone (a crop plant in no growing zone is no wild target) is not taken.
        /// </summary>
        private static void CheckTrustyClassesLeaveThem(Map map)
        {
            Guarded("trusties: a class does not take a trusty from the strip", map, r =>
            {
                const string check = "classes";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                ProgramDef prog = DefDatabase<ProgramDef>.GetNamedSilentFail("RP_FoundationEducation");
                if (prog == null || prog.Slot != RPLaborDefOf.RP_Work || !prog.everyone) { s.Fail("the check's own premise: no program for everyone that takes the work slot"); return; }
                Pawn guard = s.Guard("Ztcl1", s.At(10, 2));
                Pawn t = s.Trusty("Ztcl2", s.At(12, 2), claim: s.bed);
                CompInmate c = t.Inmate();
                // (Work in the zone to give: a tree to fell.)
                Plant tree = s.Tree(s.At(12, 3));
                // (Eligible for it: the course is for the ones who read badly.)
                if (t.skills?.GetSkill(SkillDefOf.Intellectual) is { } skill && skill.Level > prog.maxSkillLevel)
                {
                    int level0 = skill.Level;
                    r.Add(() => skill.Level = level0);
                    skill.Level = 0;
                }
                if (!prog.Eligible(t, c)) { s.Fail($"the check's own premise: the trusty is not eligible for {prog.defName}"); TrustyStill(t, guard); return; }
                s.Beat();
                c.UpdateWorkPass();
                if (!s.pm.party.isOut) { s.Fail($"the check's own premise: the party is not out: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                MethodInfo collect = AccessTools.Method(typeof(ProgramSystem), "CollectAttendees");
                var taken = AccessTools.Field(typeof(ProgramSystem), "taken")?.GetValue(null) as HashSet<Pawn>;
                if (collect == null || taken == null) { s.Fail("the check's own fixture: the class pass's collecting of students was not found"); TrustyStill(t, guard); return; }
                List<Pawn> Students(bool referredPass = false)
                {
                    taken.Clear();
                    var cand = new ProgramSystem.Candidate { program = prog };
                    collect.Invoke(null, new object[] { map, s.pm, s.g, prog, cand, referredPass });
                    return cand.pawns;
                }
                var worker = (InmateJobWorker_Outside)RPTrustyDefOf.RP_JobOutside.Worker;
                if (worker.Capacity(map) == 0) s.Fail("the check's own premise: the zone has a tree to fell and gives no work");
                if (Students().Contains(t)) s.Fail($"a class that takes the work slot collected a trusty at the outside work with the party out: {TrustyLine(s, t)}");
                // The control: with the party in the slot is no work for them, and a class may take them.
                s.pm.party.isOut = false;
                if (!Students().Contains(t)) s.Fail($"the check's own control: with the party in the class did not collect a trusty who holds the outside job (the pass does not look at them at all): {TrustyLine(s, t)}");
                s.pm.party.isOut = true;
                // The control: nothing left to do in the zone (the tree felled by somebody else, nothing growing): they are taught.
                tree.Destroy(DestroyMode.Vanish);
                if (worker.Capacity(map) != 0) s.Fail("the check's own premise: the zone has work after its only tree is gone");
                else if (!Students().Contains(t)) s.Fail($"a trusty with nothing to do in the zone, the party out, is not taught (the outside job says it has work whether it has or not): {TrustyLine(s, t)}");
                // At a cut already, with nothing else to cut: working is having work.
                ThingDef cropDef = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Potato");
                if (cropDef == null) s.Fail("the check's own fixture: no potato plant to cut");
                else
                {
                    var crop = (Plant)ThingMaker.MakeThing(cropDef);
                    crop.Growth = 1f;
                    // (Kept from the cold: on a cold map a plant dies the moment it is spawned, and there would be no crop to cut.)
                    TrustyHoldSeason(map, r, CellRect.SingleCell(s.At(14, 2)));
                    GenSpawn.Spawn(crop, s.At(14, 2), map);
                    r.Add(() =>
                    {
                        if (crop.Spawned) crop.Destroy(DestroyMode.Vanish);
                    });
                    if (!crop.Spawned || !crop.HarvestableNow) s.Fail($"the check's own fixture: the crop plant to cut is {(crop.Spawned ? "not ripe" : "dead")} on the strip ({map.mapTemperature.OutdoorTemp:F0} C outdoors)");
                    else
                    {
                        t.jobs.StartJob(new Job(JobDefOf.CutPlant, crop), JobCondition.InterruptForced);
                        if (t.CurJobDef != JobDefOf.CutPlant || !Trusties.IsWorkingOutside(t)) s.Fail($"the check's own fixture: the cut of the crop plant in the zone did not start ({TrustyLine(s, t)})");
                        else if (worker.Capacity(map) != 0) s.Fail("the check's own premise: a crop plant outside a growing zone is work to the zone");
                        else if (Students().Contains(t)) s.Fail($"a class collected a trusty at a cut in the zone because the zone has nothing else to cut: {TrustyLine(s, t)}");
                    }
                    TrustyStill(t);
                    if (crop.Spawned) crop.Destroy(DestroyMode.Vanish);
                }
                // The control: a work course the warden referred them to takes them (the referral pass), strip or not.
                tree = s.Tree(s.At(12, 3));
                ProgramDef referral0 = c.referral;
                r.Add(() => c.referral = referral0);
                c.referral = prog;
                if (worker.Capacity(map) == 0) s.Fail("the check's own premise: the zone has work again");
                if (Students().Contains(t)) s.Fail("a referred trusty was collected by the plain pass");
                if (!Students(referredPass: true).Contains(t)) s.Fail($"a trusty referred to a course of the work slot was left on the strip by the referral pass: {TrustyLine(s, t)}");
                c.referral = referral0;
                taken.Clear();
                TrustyStill(t, guard);
            });
        }

        // ---- a funeral calls them ---------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A trusty at a valid cut in the work hours with the party out is sent to a funeral they mourn at: the call is a regime interrupt like any (<see cref="Funerals"/> asks for it as it asks for a class's, and the regime's own giver
        /// then gives the job to the grave), and nothing holds it back. A service lasts 2000 ticks and a slow cut 4000: a call that was dropped meant a mourner never sent. (A hold of the interrupt for a trusty at work swallowed it.)
        /// </summary>
        private static void CheckTrustyFuneralCall(Map map)
        {
            Guarded("trusties: a funeral calls a trusty from a valid cut", map, r =>
            {
                const string check = "funeral";
                TrustyScene s = TrustyScene.Build(map, r, check);
                if (s == null) return;
                s.RegimeAllDay(RPLaborDefOf.RP_Work);
                Plant tree = s.Tree(s.At(12, 3));
                Pawn guard = s.Guard("Ztfu1", s.At(10, 2));
                Pawn t = s.Trusty("Ztfu2", s.At(12, 2), claim: s.bed);
                s.Beat();
                t.Inmate().UpdateWorkPass();
                if (!s.pm.party.isOut) { s.Fail($"the check's own premise: the party is not out: {TrustyLine(s, t)}"); TrustyStill(t, guard); return; }
                MethodInfo send = AccessTools.Method(typeof(Funerals), "Send");
                if (send == null) { s.Fail("the check's own fixture: the funeral's sending of an inmate was not found"); TrustyStill(t, guard); return; }
                t.jobs.StartJob(new Job(JobDefOf.CutPlant, tree), JobCondition.InterruptForced);
                if (t.CurJobDef != JobDefOf.CutPlant || !Trusties.IsWorkingOutside(t)) { s.Fail($"the check's own fixture: the cut did not start ({TrustyLine(s, t)})"); TrustyStill(t, guard); return; }
                int id = t.CurJob.loadID;
                TrustyDrainInterrupts(s.pm);
                int now = s.Now;
                // (A service on the strip, by the pad's rules: a ring of cells the trusty can walk to.)
                var f = new Funeral { deadId = 987661, name = "Ztfudead", state = FuneralState.Gathering, place = FuneralPlace.Pad, cell = s.At(14, 2), startTick = now, endTick = now + Funerals.GatherTicks };
                s.g.story.funerals.Add(f);
                r.Add(() => s.g.story.funerals.Remove(f));
                // The call, as the funeral makes it for an inmate it casts.
                send.Invoke(null, new object[] { f, t, s.pm, null, true });
                if (!f.Attends(t.thingIDNumber)) { s.Fail("the check's own fixture: the trusty was not sent to the funeral"); TrustyStill(t, guard); return; }
                TrustyDrainInterrupts(s.pm);
                if (t.CurJob?.loadID == id) s.Fail($"a trusty at a valid cut in the zone with the party out was not called from it by the funeral they are sent to (the call was dropped): {TrustyLine(s, t)}");
                else if (Funerals.JobFor(t, s.pm) is not { } job || job.def != RPFuneralDefOf.RP_AttendFuneral) s.Fail("the regime's giver has no job to the funeral for a trusty who was called to it");
                TrustyStill(t, guard);
            });
        }
    }
}
