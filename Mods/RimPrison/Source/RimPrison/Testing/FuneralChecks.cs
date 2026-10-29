using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    /// <summary>
    /// The funeral (gap analysis 6, item 8) in the events plan, on the death checks' scene (<see cref="DeathScene"/>: a morgue and a burial plot on open ground outside the compound, a chapel painted beside them, a real workman and
    /// guards on real jobs). The scene's own people are the whole cast (<see cref="Funerals.CastForTests"/>: the prison's real inmates and staff are nobody's mourners here), and the funeral is on (an autotest run keeps the burial as it was
    /// unless a check turns it on). Each check fails without the part it is named for: the letter and the gate (a body is buried with no word asked, or before the hour), the grave (nobody gathers, no thought, the tension stays), a quiet
    /// burial (anybody is sent), the hour (a service at night, in a lockdown, past a day), keeping apart (both come), the staff's (colleagues who are not on duty, no speech), the family and the chapel (no visitor, the plot for mourners who
    /// cannot enter it), the guards (the mourners go without one), the rival (the chance, the wrong gang), the turf count (the gang's tally moves) and a service lapsing. A check that the real walking happened says so: the people
    /// are in the grave's ring by their own feet.
    /// </summary>
    public static partial class AutoTest
    {
        private static void FuneralFail(string check, string what) => problems0.Add($"funeral ({check}): {what}");

        private static void CheckFuneral(Map map)
        {
            if (RPFuneralDefOf.RP_AttendFuneral == null || PrisonDecisionDef.Named("RP_Decision_Funeral") == null)
            {
                FuneralFail("defs", "the funeral's jobs, thoughts or decision are not defined");
                return;
            }
            var pawnsBefore = new HashSet<Pawn>(map.mapPawns.AllPawns);
            CheckFuneralLetterAndGate(map);
            CheckFuneralAtTheGrave(map);
            CheckFuneralQuietGathersNobody(map);
            CheckFuneralHourAndTrouble(map);
            CheckFuneralNobodyRotAndStale(map);
            CheckFuneralKeepApart(map);
            CheckFuneralStaff(map);
            CheckFuneralFamilyChapelAndThePlot(map);
            CheckFuneralGuardsAreTheCost(map);
            CheckFuneralRival(map);
            CheckFuneralTurfCountLeavesThemOut(map);
            Pawn[] left = LeftBehind(map, pawnsBefore).Where(x => x.Name is NameSingle && x.LabelShort.StartsWith("Zfu")).ToArray();
            if (left.Length > 0) problems0.Add($"funeral checks left {left.Length} pawn(s) behind: {string.Join(", ", left.Select(x => x.LabelShort))}");
        }

        // ---- the scene ----------------------------------------------------------------------------------------------------------------------

        /// <summary>The death checks' scene with a chapel beside the plot, the funeral on, and nobody but the scene's own people in the cast.</summary>
        private sealed class FuneralScene
        {
            public readonly DeathScene d;
            public readonly Map map;
            public readonly PrisonGame g = PrisonGame.Instance;
            public readonly PrisonMap pm;
            public readonly Rollback r;
            public readonly string check;
            public readonly SecurityLevelDef level = InmateUtility.DefaultSecurity;
            public readonly HashSet<Pawn> cast = new HashSet<Pawn>();

            private FuneralScene(DeathScene d, Rollback r, string check)
            {
                this.d = d;
                this.r = r;
                this.check = check;
                map = d.map;
                pm = d.pm;
            }

            public CellRect ChapelRect => new CellRect(d.origin.x + 5, d.origin.z + 4, 4, 4);

            public CellRect PlotRect => d.PlotRect;

            public static FuneralScene Build(Map map, Rollback r, string check)
            {
                if (HostageCrisis.Running(map) != null)
                {
                    RPLog.Message($"[RimPrison:test] funeral ({check}): a hostage crisis is running, which stops every service; the check is skipped");
                    return null;
                }
                if (RiotPolice.RiotOngoing(map))
                {
                    RPLog.Message($"[RimPrison:test] funeral ({check}): a riot is on, which stops every service; the check is skipped");
                    return null;
                }
                DeathScene d = DeathScene.Build(map, r, check);
                if (d == null) return null;
                var s = new FuneralScene(d, r, check);
                PrisonGame g = s.g;
                List<Funeral> funerals0 = g.story.funerals;
                List<int> apart0 = g.story.funeralApart;
                int clock0 = Find.TickManager.TicksGame;
                int lockdown0 = g.lockdownUntilTick;
                var policies0 = new Dictionary<SecurityLevelDef, VisitPolicy>(g.visitPolicies);
                List<MurderTrial> trials0 = g.story.trials.ToList();
                r.Add(() =>
                {
                    Funerals.TestOn = false;
                    (Funerals.CastForTests, Funerals.ReachForTests, Funerals.ScuffleRollForTests, Funerals.ThreatForTests) = (null, null, null, null);
                    g.story.funerals = funerals0;
                    g.story.funeralApart = apart0;
                    g.story.trials = trials0;
                    g.lockdownUntilTick = lockdown0;
                    g.visitPolicies = policies0;
                    Find.TickManager.DebugSetTicksGame(clock0);
                });
                g.story.funerals = new List<Funeral>();
                g.story.funeralApart = new List<int>();
                // (A lockdown the real prison is under is not the check's: it is put off for it and back.)
                g.lockdownUntilTick = -1;
                Funerals.TestOn = true;
                Funerals.CastForTests = p => s.cast.Contains(p);
                // (The raiders the events plan left on the map are not the check's: no enemy is here unless a check says so.)
                Funerals.ThreatForTests = false;
                // The prison's own chapel is out of the scene (the middle of every chapel cell on the map is where a service stands): put back by the rollback.
                foreach (IntVec3 c in s.pm.zones.CellsOf(RPFaithDefOf.RP_Chapel).ToList())
                {
                    r.Add(() => s.pm.zones.Set(c, RPFaithDefOf.RP_Chapel));
                    s.pm.zones.Set(c, null);
                }
                foreach (IntVec3 c in s.ChapelRect)
                {
                    PrisonZoneDef was = s.pm.zones.ZoneAt(c);
                    r.Add(() => s.pm.zones.Set(c, was));
                    s.pm.zones.Set(c, RPFaithDefOf.RP_Chapel);
                }
                return s;
            }

            /// <summary>An inmate of the scene, in the regime's hands and nowhere special: searched in, awake, tense.</summary>
            public Pawn Inmate(string name, bool family = false, GangDef gang = null, SecurityLevelDef security = null)
            {
                Pawn p = d.Inmate(name, family);
                p.Name = new NameSingle("Zfu" + name);
                CompInmate c = p.Inmate();
                (c.processed, c.awaitingSearch, c.activeFlag, c.gang, c.gangKnown, c.tension) = (true, false, true, gang, gang != null, 50f);
                if (security != null) c.security = security;
                if (family) c.relatives[0].name = "Ines";
                cast.Add(p);
                return p;
            }

            public Pawn Staff(string name, StaffRoleDef role, IntVec3? at = null)
            {
                Pawn p = d.Staff(name, role, at);
                p.Name = new NameSingle("Zfu" + name);
                cast.Add(p);
                return p;
            }

            public Pawn Friend(Pawn of, string name, GangDef gang = null)
            {
                Pawn p = Inmate(name, false, gang);
                p.relations.AddDirectRelation(RPRelationDefOf.RP_Friend, of);
                return p;
            }

            /// <summary>The dead one's body in the morgue, as the guard who carried it would leave it, and the letter that comes of it.</summary>
            public (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) Dead(Pawn dead, int slot)
            {
                dead.Kill(null);
                Corpse body = d.Body(dead);
                if (body == null) return (null, null, null);
                d.PutInMorgue(body, slot);
                Funerals.Notify_Laid(body);
                Funeral f = Funerals.Of(dead);
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_Funeral") && l.amount == dead.thingIDNumber);
                if (f == null || letter == null) FuneralFail(check, $"no funeral was asked for {dead.LabelShort} (record {f != null}, letter {letter != null})");
                return (body, f, letter);
            }

            /// <summary>The warden's answer: 0 a quiet burial, 1 a funeral.</summary>
            public static void Answer(ChoiceLetter_PrisonDecision letter, int option)
            {
                letter.answered = true;
                PrisonDecisions.Choose(letter, option);
            }

            /// <summary>The clock at the start of the funeral's hour, and the hour's pass run for it (as the game's would): the funeral starts if it can.</summary>
            public void HourComes(Funeral f)
            {
                Find.TickManager.DebugSetTicksGame(f.holdTick);
                Funerals.Hourly(g, map, GenLocalDate.HourOfDay(map));
            }

            public void Place(Pawn p, IntVec3 cell) => d.Place(p, cell);
        }

        private static string FuneralOnes(Funeral f, IEnumerable<Pawn> pawns) => string.Join(", ", pawns.Select(p => p.LabelShort + (f.Attends(p.thingIDNumber) ? "+" : "-")));

        private static int FuneralStage(Pawn p, ThoughtDef def) => p.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(def)?.CurStageIndex ?? -1;

        /// <summary>The clock moved to the start of the next time the given hour of the day comes.</summary>
        private static void FuneralClockTo(Map map, int hour)
        {
            int ticks = (hour - GenLocalDate.HourOfDay(map) + 24) % 24 * GenDate.TicksPerHour - GenLocalDate.DayTick(map) % GenDate.TicksPerHour;
            if (ticks <= 0) ticks += GenDate.TicksPerDay;
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ticks);
        }

        // ---- the letter, and the burial that waits for it ------------------------------------------------------------------------------------

        /// <summary>
        /// A guard carries a body into the morgue and the warden is asked (the hook is the haul job's last toil); the letter has the two answers, a day to answer and a quiet burial for an unanswered one; the workman is not offered the
        /// body while the letter waits - and is when the funeral is off (the check's own proof that the gate is what holds him) - and a body with a family is a funeral in the chapel, not at a grave.
        /// </summary>
        private static void CheckFuneralLetterAndGate(Map map)
        {
            const string check = "the letter and the gate";
            Guarded("funeral: the letter, and the burial that waits for the answer", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Staff("Guard", RPSecurityDefOf.RP_Guard);
                Pawn workman = s.Staff("Workman", Role("RP_Workman"), s.d.At(8, 5));
                var haul = new WorkGiver_HaulToMorgue { def = DefDatabase<WorkGiverDef>.GetNamed("RP_HaulToMorgue") };
                var bury = new WorkGiver_BuryBody { def = DefDatabase<WorkGiverDef>.GetNamed("RP_BuryBody") };

                // The hook: a guard carries the body in on real ticks, and the letter is there when it is laid.
                Pawn dead = s.Inmate("Nobody");
                dead.Kill(null);
                Corpse body = s.d.Body(dead);
                if (body == null) return;
                if (!DeathRunJob(guard, haul, body, () => Morgue.InMorgue(body, s.pm), 1800) || !Morgue.InMorgue(body, s.pm))
                {
                    FuneralFail(check, $"the guard did not carry the body into the morgue (corpse at {body.Position}, guard {guard.CurJob?.def?.defName})");
                    return;
                }
                ShuttleTicks(() => Funerals.Of(dead) != null, 60);
                Funeral f = Funerals.Of(dead);
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_Funeral") && l.amount == dead.thingIDNumber);
                if (f == null || letter == null)
                {
                    FuneralFail(check, $"laying a body in the morgue by the haul job asked nobody (record {f != null}, letter {letter != null})");
                    return;
                }
                if (f.state != FuneralState.Asked || f.place != FuneralPlace.Grave || f.staff) FuneralFail(check, $"the new record is {f.Describe()} (asked, at the grave, not staff expected)");
                IReadOnlyList<string> options = letter.decision.Worker.Options(letter);
                if (options.Count != 2 || options[0] != "RP_Funeral_Quiet" || options[1] != "RP_Funeral_Hold" || letter.decision.Worker.TimeoutChoice != 0)
                    FuneralFail(check, $"the letter's answers are {string.Join("/", options)} with {letter.decision.Worker.TimeoutChoice} for no answer (a quiet burial first, and for no answer)");
                if (!letter.TimeoutActive || letter.decision.urgent) FuneralFail(check, $"the letter has an answer time {letter.TimeoutActive} and is urgent {letter.decision.urgent} (a day, not urgent)");
                if (!letter.Text.ToString().Contains("RP_FuneralPlace_Grave".Translate())) FuneralFail(check, "the letter does not say that it is at the grave");

                // The gate: the workman is offered the body when nothing was asked (the funeral off), not while the letter waits.
                Funerals.TestOn = false;
                bool freeWithout = bury.HasJobOnThing(workman, body);
                Funerals.TestOn = true;
                if (!freeWithout) FuneralFail(check, "the check's own premise: the workman is not offered a body with a plot and nobody asked");
                else if (bury.HasJobOnThing(workman, body)) FuneralFail(check, "the workman is offered the body while the letter is not answered (the burial must wait for the answer)");

                // No answer is a quiet burial: the workman may take it.
                letter.Lapse();
                if (f.state != FuneralState.Quiet || !bury.HasJobOnThing(workman, body)) FuneralFail(check, $"the unanswered letter left the funeral {f.state} and the workman's job {bury.HasJobOnThing(workman, body)} (a quiet burial, and he may take the body)");

                // A body with a family goes home on the shuttle: the funeral is in the chapel.
                Pawn kin = s.Inmate("Kin", family: true);
                (Corpse kinBody, Funeral kinF, ChoiceLetter_PrisonDecision kinLetter) = s.Dead(kin, 1);
                if (kinBody == null || kinF == null) return;
                if (kinF.place != FuneralPlace.Chapel || !kinLetter.Text.ToString().Contains("RP_FuneralPlace_Chapel".Translate())) FuneralFail(check, $"a body with a family is to be seen off {kinF.place} (in the chapel), the letter says so: {kinLetter.Text.ToString().Contains("RP_FuneralPlace_Chapel".Translate())}");
            });
        }

        // ---- at the grave -------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The funeral chosen: the burial waits for the hour; at the hour the friends and gang mates are sent (not a stranger, not one in the hole, in supermax or in protective custody), the chaplain and the guard who escorts them;
        /// they walk to the grave on their own feet and stand round it while the workman digs it; at the end the mourners have the "said goodbye" thought (the chaplain's stage) and less tension, the rest of the wing a little less, another
        /// wing none, and the line is in the chronicle. The turf count leaves them out for the hour and a little after.
        /// </summary>
        private static void CheckFuneralAtTheGrave(Map map)
        {
            const string check = "at the grave";
            Guarded("funeral: the friends and the chaplain gather at the grave within the hour", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                GangDef ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
                Pawn guard = s.Staff("Guard", RPSecurityDefOf.RP_Guard), guard2 = s.Staff("Guard2", RPSecurityDefOf.RP_Guard, s.d.At(9, 1));
                Pawn chaplain = s.Staff("Chaplain", RPStaffRoleDefOf.RP_Chaplain, s.d.At(8, 3));
                Pawn workman = s.Staff("Workman", Role("RP_Workman"), s.d.At(8, 5));
                var bury = new WorkGiver_BuryBody { def = DefDatabase<WorkGiverDef>.GetNamed("RP_BuryBody") };
                Pawn dead = s.Inmate("Dead", gang: ravens);
                Pawn friend = s.Friend(dead, "Friend"), mate = s.Inmate("Mate", gang: ravens), stranger = s.Inmate("Stranger");
                Pawn hole = s.Friend(dead, "Hole"), super = s.Friend(dead, "Super"), protective = s.Friend(dead, "Protective");
                Pawn otherWing = s.Inmate("Minimum", security: DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum"));
                hole.Inmate().forcedActivity = RPSecurityDefOf.RP_Solitary;
                super.Inmate().security = SupermaxSystem.Level;
                protective.Inmate().security = RPSecurityDefOf.RP_Protective;
                (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) = s.Dead(dead, 0);
                if (body == null || f == null) return;
                // (Nobody's friend by opinion: a stranger, or one of another wing, who happens to like the dead one is invited, and that is the rule. Such a one came and said goodbye in census c80-ev2 - "with 6 there", "another
                // wing's tension changed to 35", 50 less a mourner's 15 - and the end's lines for those who did not come were read of a mourner. One the rule invited is taken off the list, so that they stay the ones who did not come;
                // one invited who does not like the dead one is the product's fault.)
                foreach (Pawn p in new[] { stranger, otherWing })
                {
                    if (!f.invited.Contains(p.thingIDNumber)) continue;
                    int grief = Funerals.Grief(p, p.Inmate(), dead, dead.Inmate());
                    if (grief == 0)
                    {
                        FuneralFail(check, $"{p.LabelShort}, no friend, gang mate or liker of the dead one (opinion {p.relations?.OpinionOf(dead)}), is invited");
                        continue;
                    }
                    RPLog.Message($"[RimPrison:test] funeral: {p.LabelShort} happens to like the dead one (opinion {p.relations?.OpinionOf(dead)}, grief {grief}) and was invited; the check takes them off the list, they are the ones who do not come");
                    f.invited.Remove(p.thingIDNumber);
                }

                FuneralScene.Answer(letter, 1);
                if (f.state != FuneralState.Waiting || f.holdTick <= Find.TickManager.TicksGame) FuneralFail(check, $"the funeral chosen is {f.state} with the hour at {f.holdTick} (waiting for a later hour)");
                if (bury.HasJobOnThing(workman, body)) FuneralFail(check, "the workman is offered the body before the funeral's hour");
                s.HourComes(f);
                if (f.state != FuneralState.Gathering)
                {
                    FuneralFail(check, $"the funeral did not begin at its hour ({f.Describe()})");
                    return;
                }
                if (f.place != FuneralPlace.Grave || !f.cell.IsValid || s.pm.zones.ZoneAt(f.cell) != RPBurialDefOf.RP_BurialPlot) FuneralFail(check, $"the funeral is {f.place} at {f.cell} (at a grave site in the plot)");
                Pawn[] sent = { friend, mate, stranger, hole, super, protective, otherWing };
                bool come = f.Attends(friend.thingIDNumber) && f.Attends(mate.thingIDNumber);
                bool stayed = !f.Attends(hole.thingIDNumber) && !f.Attends(super.thingIDNumber) && !f.Attends(protective.thingIDNumber);
                bool strangerOut = !f.Attends(stranger.thingIDNumber) && !f.Attends(otherWing.thingIDNumber);
                if (!come || !stayed || !strangerOut)
                    FuneralFail(check, $"sent: {FuneralOnes(f, sent)} (the friend and the gang mate come; the hole, supermax, protective custody, a stranger and another wing's inmate do not)");
                int escorts = new[] { guard, guard2 }.Count(x => f.Attends(x.thingIDNumber));
                if (!f.Attends(chaplain.thingIDNumber) || chaplain.CurJobDef != RPFuneralDefOf.RP_LeadFuneral) FuneralFail(check, $"the chaplain was not sent ({chaplain.CurJobDef?.defName})");
                if (escorts != 1 || f.Attends(workman.thingIDNumber)) FuneralFail(check, $"{escorts} guard(s) escort them (one: of two guards one is spared) and the workman is sent {f.Attends(workman.thingIDNumber)}");
                Pawn escort = guard2.CurJobDef == RPFuneralDefOf.RP_AttendFuneral ? guard2 : guard;
                if (escort.CurJobDef != RPFuneralDefOf.RP_AttendFuneral) FuneralFail(check, "the escorting guard is not at the funeral (busy for the hour)");
                Pawn[] mourners = { friend, mate };
                foreach (Pawn p in mourners)
                {
                    Job job = Funerals.JobFor(p, s.pm);
                    if (job == null || job.def != RPFuneralDefOf.RP_AttendFuneral || !job.targetA.Cell.InHorDistOf(f.cell, Funerals.StandRadius + 1f)) FuneralFail(check, $"{p.LabelShort}'s job at the funeral is {job?.def?.defName} at {job?.targetA.Cell} (the grave's ring at {f.cell})");
                }
                // The regime's own giver sends them (the queued interrupt, then the regime's job pass: nothing here starts the job).
                ShuttleTicks(() => mourners.All(p => p.CurJobDef == RPFuneralDefOf.RP_AttendFuneral), 300);
                foreach (Pawn p in mourners)
                {
                    if (p.CurJobDef == RPFuneralDefOf.RP_AttendFuneral) continue;
                    FuneralFail(check, $"{p.LabelShort} was not taken to the funeral by the regime after 300 ticks (job {p.CurJobDef?.defName}: the giver does not ask for the funeral, or the interrupt is not queued)");
                    if (Funerals.JobFor(p, s.pm) is { } job) p.jobs.StartJob(job, JobCondition.InterruptForced);
                }
                if (RegimePlacement.Sanctioned(friend) != (friend.CurJobDef == RPFuneralDefOf.RP_AttendFuneral)) FuneralFail(check, "a mourner at the funeral is out of place by the regime's own reckoning");

                // The workman is offered the body now, and digs where they stand.
                if (!bury.HasJobOnThing(workman, body) || bury.JobOnThing(workman, body) is not { } dig || dig.targetB.Cell != f.cell)
                    FuneralFail(check, $"at the funeral the workman is not offered the body, or to dig at the funeral's site {f.cell}");
                else workman.jobs.StartJob(dig, JobCondition.InterruptForced);

                // On their own feet (a bit more than half the service: the check sets the end itself).
                bool Round(Pawn p) => p.Spawned && p.Position.InHorDistOf(f.cell, Funerals.StandRadius + 2f);
                ShuttleTicks(() => f.buried && Round(friend) && Round(mate) && Round(chaplain) && Round(escort), 1500);
                if (!Round(friend) || !Round(mate) || !Round(chaplain) || !Round(escort))
                    FuneralFail(check, $"after 1500 ticks the friend {Round(friend)}, the gang mate {Round(mate)}, the chaplain {Round(chaplain)} and the guard {Round(escort)} are not round the grave site {f.cell} (friend at {friend.Position}, {friend.CurJobDef?.defName})");
                Building_Grave grave = f.cell.GetThingList(map).OfType<Building_Grave>().FirstOrDefault();
                r.Add(() =>
                {
                    if (grave is { Destroyed: false }) grave.Destroy(DestroyMode.Vanish);
                });
                if (grave == null || grave.Corpse != body || body.Spawned) FuneralFail(check, $"the body is not laid in a grave at the funeral's site (grave {grave != null}, body spawned {body.Spawned})");
                if (!f.buried) FuneralFail(check, "the funeral's record does not say it was buried");
                if (f.state != FuneralState.Gathering) FuneralFail(check, $"the funeral ended before its time ({f.state})");

                // The end: the thought, the tension, the wing.
                foreach (Pawn p in new[] { friend, mate, stranger, otherWing, hole }) p.Inmate().tension = 50f;
                f.endTick = Find.TickManager.TicksGame;
                Funerals.Rare(g, map);
                if (f.state != FuneralState.Done) FuneralFail(check, $"the service did not end at its time ({f.state})");
                if (FuneralStage(friend, RPFuneralDefOf.RP_SaidGoodbye) != 1 || FuneralStage(mate, RPFuneralDefOf.RP_SaidGoodbye) != 1)
                    FuneralFail(check, $"the mourners' thought of the chaplain's service is {FuneralStage(friend, RPFuneralDefOf.RP_SaidGoodbye)} and {FuneralStage(mate, RPFuneralDefOf.RP_SaidGoodbye)} (stage 1)");
                string Who(Pawn p) => $"{p.LabelShort}: thought stage {FuneralStage(p, RPFuneralDefOf.RP_SaidGoodbye)}, attended {f.Attends(p.thingIDNumber)}, at {p.Position} ({p.Position.DistanceTo(f.cell):F1} from the grave), job {p.CurJobDef?.defName ?? "none"}, opinion of the dead one {p.relations?.OpinionOf(dead)}";
                if (FuneralStage(stranger, RPFuneralDefOf.RP_SaidGoodbye) >= 0 || FuneralStage(otherWing, RPFuneralDefOf.RP_SaidGoodbye) >= 0) FuneralFail(check, $"somebody who did not come said goodbye ({Who(stranger)}; {Who(otherWing)}; sent {FuneralOnes(f, sent)})");
                if (Math.Abs(friend.Inmate().tension - (50f - Funerals.ServiceTension)) > 0.01f) FuneralFail(check, $"a mourner's tension is {friend.Inmate().tension} (50 less {Funerals.ServiceTension} with the chaplain)");
                if (Math.Abs(stranger.Inmate().tension - (50f - Funerals.WingTension)) > 0.01f) FuneralFail(check, $"the wing's tension is {stranger.Inmate().tension} (50 less {Funerals.WingTension} for one who did not come; {Who(stranger)})");
                if (otherWing.Inmate().tension != 50f) FuneralFail(check, $"another wing's tension changed to {otherWing.Inmate().tension} ({Who(otherWing)})");
                if (!f.chaplain) FuneralFail(check, "the record does not say the chaplain led it");
                if (!r.NewEntries.Any(e => e.text.Contains(dead.LabelShort) && e.text.Contains("RP_FuneralChaplained".Translate()))) FuneralFail(check, "no chronicle line for the funeral, with the chaplain");

                // The turf count leaves them out for the hour and the turn of it after, and not past it.
                if (!Funerals.Away(friend)) FuneralFail(check, "a mourner is counted in the turf the moment the service is over");
                int end = f.endTick;
                Find.TickManager.DebugSetTicksGame(end + Funerals.AwayGraceTicks + 1);
                if (Funerals.Away(friend)) FuneralFail(check, "a mourner is left out of the turf count past the turn of the hour after the service");
                // The record goes with the day.
                Find.TickManager.DebugSetTicksGame(end + GenDate.TicksPerDay);
                Funerals.Hourly(g, map, 0);
                if (Funerals.Of(dead) != null) FuneralFail(check, "the record of the funeral is still there a day after");
            });
        }

        // ---- a quiet burial -----------------------------------------------------------------------------------------------------------------

        /// <summary>A quiet burial (answered, or what no answer is) gathers nobody - however many friends they had - and the workman is free to bury; no line says a funeral was held.</summary>
        private static void CheckFuneralQuietGathersNobody(Map map)
        {
            const string check = "a quiet burial";
            Guarded("funeral: a quiet burial gathers nobody", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn guard = s.Staff("Guard", RPSecurityDefOf.RP_Guard), guard2 = s.Staff("Guard2", RPSecurityDefOf.RP_Guard), chaplain = s.Staff("Chaplain", RPStaffRoleDefOf.RP_Chaplain);
                Pawn workman = s.Staff("Workman", Role("RP_Workman"), s.d.At(8, 5));
                var bury = new WorkGiver_BuryBody { def = DefDatabase<WorkGiverDef>.GetNamed("RP_BuryBody") };
                Pawn dead = s.Inmate("Dead");
                Pawn friend = s.Friend(dead, "Friend");
                (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) = s.Dead(dead, 0);
                if (body == null || f == null) return;
                FuneralScene.Answer(letter, 0);
                if (f.state != FuneralState.Quiet || !bury.HasJobOnThing(workman, body)) FuneralFail(check, $"the quiet burial is {f.state} and the workman's job {bury.HasJobOnThing(workman, body)}");
                // Every hour of two days: nobody is sent, nothing is held.
                int now = Find.TickManager.TicksGame;
                for (int i = 1; i <= 48; i++)
                {
                    Find.TickManager.DebugSetTicksGame(now + i * GenDate.TicksPerHour);
                    Funerals.Hourly(g, map, GenLocalDate.HourOfDay(map));
                    Funerals.Rare(g, map);
                }
                if (f.attendees.Count != 0 || Funerals.GatheringFor(friend) != null || Funerals.JobFor(friend, s.pm) != null || chaplain.CurJobDef == RPFuneralDefOf.RP_LeadFuneral || guard.CurJobDef == RPFuneralDefOf.RP_AttendFuneral || guard2.CurJobDef == RPFuneralDefOf.RP_AttendFuneral)
                    FuneralFail(check, $"after a quiet burial {f.attendees.Count} were sent, the friend has a funeral job {Funerals.JobFor(friend, s.pm) != null}, the chaplain {chaplain.CurJobDef?.defName}");
                if (FuneralStage(friend, RPFuneralDefOf.RP_SaidGoodbye) >= 0) FuneralFail(check, "the friend of one given a quiet burial said goodbye");
                if (r.NewEntries.Any(e => e.text.Contains(dead.LabelShort) && new[] { FuneralPlace.Grave, FuneralPlace.Chapel, FuneralPlace.Pad }.Any(pl => e.text.Contains("RP_FuneralPlace_" + pl.ToString()) || e.text.Contains(("RP_FuneralPlace_" + pl).Translate().ToString())))) FuneralFail(check, "a funeral is in the chronicle for a quiet burial");
                // The body leaves, and with it the record.
                body.Destroy();
                Funerals.Hourly(g, map, GenLocalDate.HourOfDay(map));
                if (Funerals.Of(dead) != null) FuneralFail(check, "the record of a quiet burial stays when the body is gone");
            });
        }

        // ---- the hour, and what stops it ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The hour chosen is the next one of free time or work in the dead's timetable (none of the hours before it is); at night it is not held; a lockdown keeps it from starting and a day of waiting is a quiet burial with its line;
        /// a riot keeps it from starting; one under way is broken off by a lockdown (or a riot) with no thought and no tension taken off.
        /// </summary>
        private static void CheckFuneralHourAndTrouble(Map map)
        {
            const string check = "the hour";
            Guarded("funeral: not at night, not in a lockdown or a riot, and it lapses after a day", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                s.Staff("Guard", RPSecurityDefOf.RP_Guard);
                s.Staff("Guard2", RPSecurityDefOf.RP_Guard);
                Pawn workman = s.Staff("Workman", Role("RP_Workman"), s.d.At(8, 5));
                var bury = new WorkGiver_BuryBody { def = DefDatabase<WorkGiverDef>.GetNamed("RP_BuryBody") };
                r.Add(() => RiotPolice.ForgetRiotForTests(map));
                int night = Enumerable.Range(0, 24).FirstOrDefault(h => !Funerals.ServiceHour(g, s.level, h), -1);
                if (night < 0) RPLog.Message("[RimPrison:test] funeral: the default level's timetable has no hour that is neither free time nor work; the night part of the hour check is skipped");

                // The hour: the next free-time or work hour, and none before it.
                Pawn dead = s.Inmate("Dead");
                s.Friend(dead, "Friend");
                (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) = s.Dead(dead, 0);
                if (body == null || f == null) return;
                int now = Find.TickManager.TicksGame, hourNow = GenLocalDate.HourOfDay(map);
                FuneralScene.Answer(letter, 1);
                int away = (f.holdTick - now + GenDate.TicksPerHour - 1) / GenDate.TicksPerHour;
                bool serviceAtHold = Funerals.ServiceHour(g, s.level, (hourNow + Math.Max(1, away)) % 24);
                bool earlier = false;
                for (int k = 1; k < away; k++) earlier |= Funerals.ServiceHour(g, s.level, (hourNow + k) % 24);
                if (f.state != FuneralState.Waiting || f.holdTick <= now || away > 24 || !serviceAtHold || earlier)
                    FuneralFail(check, $"the funeral's hour is {away} hours away, a free-time or work hour {serviceAtHold}, an earlier one was passed over {earlier} (the next one, within a day)");

                // At night it is not held, whatever its hour says.
                int hold = f.holdTick;
                if (night >= 0)
                {
                    FuneralClockTo(map, night);
                    f.holdTick = Find.TickManager.TicksGame;
                    Funerals.Hourly(g, map, night);
                    if (f.state != FuneralState.Waiting) FuneralFail(check, $"the funeral started at hour {night} of the dead's timetable, which is neither free time nor work ({f.state})");
                    f.holdTick = hold;
                }

                // A lockdown keeps it from starting, and the burial waits with it; a day after its hour it is a quiet burial, with the line.
                g.lockdownUntilTick = int.MaxValue;
                s.HourComes(f);
                if (f.state != FuneralState.Waiting || bury.HasJobOnThing(workman, body)) FuneralFail(check, $"in a lockdown the funeral is {f.state} (waiting) and the workman's job {bury.HasJobOnThing(workman, body)} (none: the burial waits too)");
                Find.TickManager.DebugSetTicksGame(hold + Funerals.LapseTicks + 1);
                Funerals.Hourly(g, map, GenLocalDate.HourOfDay(map));
                string lapsed = "RP_Chr_FuneralLapsed".Translate(dead.LabelShort, "RP_FuneralWhy_Late".Translate());
                if (f.state != FuneralState.Quiet || !r.NewEntries.Any(e => e.text == lapsed) || !bury.HasJobOnThing(workman, body))
                    FuneralFail(check, $"a day past its hour in a lockdown the funeral is {f.state} (a quiet burial), the line {r.NewEntries.Any(e => e.text == lapsed)}, the workman's job {bury.HasJobOnThing(workman, body)}");
                g.lockdownUntilTick = -1;

                // A riot keeps it from starting; a lockdown in the middle of one under way breaks it off.
                Pawn dead2 = s.Inmate("Dead2"), friend2 = s.Friend(dead2, "Friend2"), rioter = s.Inmate("Rioter");
                (Corpse body2, Funeral f2, ChoiceLetter_PrisonDecision letter2) = s.Dead(dead2, 1);
                if (body2 == null || f2 == null) return;
                FuneralScene.Answer(letter2, 1);
                rioter.mindState.mentalStateHandler.TryStartMentalState(RPMentalStateDefOf.RP_Rioting, null, forced: true);
                s.HourComes(f2);
                bool heldByRiot = f2.state == FuneralState.Waiting;
                rioter.mindState.mentalStateHandler.Reset();
                if (!heldByRiot) FuneralFail(check, $"a funeral began while a riot was on ({f2.state})");
                Funerals.Hourly(g, map, GenLocalDate.HourOfDay(map));
                if (f2.state != FuneralState.Gathering)
                {
                    FuneralFail(check, $"after the riot the funeral did not begin ({f2.Describe()})");
                    return;
                }
                friend2.Inmate().tension = 50f;
                g.lockdownUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
                Funerals.Rare(g, map);
                if (f2.state != FuneralState.Done || FuneralStage(friend2, RPFuneralDefOf.RP_SaidGoodbye) >= 0 || friend2.Inmate().tension != 50f)
                    FuneralFail(check, $"a lockdown that began in the middle left the funeral {f2.state}, the thought {FuneralStage(friend2, RPFuneralDefOf.RP_SaidGoodbye)}, the tension {friend2.Inmate().tension} (broken off: no thought, no relief)");
                string broken = "RP_Chr_FuneralBroken".Translate(dead2.LabelShort, "RP_FuneralWhy_Trouble".Translate());
                if (!r.NewEntries.Any(e => e.text == broken)) FuneralFail(check, "no chronicle line for a funeral broken off");
            });
        }

        // ---- nobody, the rot and the stale letter ---------------------------------------------------------------------------------------------

        /// <summary>
        /// A funeral nobody can come to is not held (it waits, and a colleague's with no colleague likewise); one that nobody stood at gives the wing and the staff nothing; a raid keeps it from starting and breaks one off; a letter is a
        /// quiet burial a day after it was written (not after it was delivered) and when the body has begun to rot, and a letter that is no longer open to an answer says so; the note of who was kept apart from the dead one is made at the death and goes after a week if the body is never laid.
        /// </summary>
        private static void CheckFuneralNobodyRotAndStale(Map map)
        {
            const string check = "nobody, the rot and the stale letter";
            Guarded("funeral: no service with nobody at it, a raid, the rot and a stale letter", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                int hour = GenLocalDate.HourOfDay(map);

                // A colleague's funeral with no colleague, no chaplain and no warden (nobody on the staff but the dead one): it waits.
                Pawn dead3 = s.Staff("Dead3", Role("RP_Janitor"));
                dead3.GetComp<CompStaff>().leaveAtTick = Find.TickManager.TicksGame + 10 * GenDate.TicksPerDay;
                dead3.Kill(null);
                Corpse body3 = s.d.Body(dead3);
                if (body3 == null) return;
                s.d.PutInMorgue(body3, 2);
                Funerals.Notify_Laid(body3);
                Funeral f3 = Funerals.Of(dead3);
                ChoiceLetter_PrisonDecision letter3 = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_Funeral") && l.amount == dead3.thingIDNumber);
                if (f3 == null || letter3 == null || !f3.staff)
                {
                    FuneralFail(check, "no funeral was asked for a colleague");
                    return;
                }
                FuneralScene.Answer(letter3, 1);
                s.HourComes(f3);
                if (f3.state != FuneralState.Waiting || f3.attendees.Count != 0) FuneralFail(check, $"a colleague's funeral with nobody to come began ({f3.Describe()})");
                // (Put off for good: the guards who come next would be its colleagues.)
                f3.state = FuneralState.Quiet;

                // (The guards are for the inmates' services: four, so that the ones a service has taken are not the last two.)
                for (int i = 1; i <= 4; i++) s.Staff("Guard" + i, RPSecurityDefOf.RP_Guard);

                // Nobody to come: it waits (the burial with it), and nobody is sent.
                Pawn dead = s.Inmate("Dead"), onlooker = s.Inmate("Onlooker");
                (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) = s.Dead(dead, 0);
                if (body == null || f == null) return;
                f.invited.Clear();
                FuneralScene.Answer(letter, 1);
                s.HourComes(f);
                if (f.state != FuneralState.Waiting || f.attendees.Count != 0 || onlooker.Inmate().tension != 50f) FuneralFail(check, $"a funeral with nobody to come began, or sent somebody ({f.Describe()}; waiting, nobody sent)");

                // Nobody at it at the end: the wing has no relief.
                Pawn dead2 = s.Inmate("Dead2"), friend2 = s.Friend(dead2, "Friend2");
                (Corpse body2, Funeral f2, ChoiceLetter_PrisonDecision letter2) = s.Dead(dead2, 1);
                if (body2 == null || f2 == null) return;
                FuneralScene.Answer(letter2, 1);
                s.HourComes(f2);
                if (f2.state != FuneralState.Gathering)
                {
                    FuneralFail(check, $"the funeral with a friend did not begin ({f2.Describe()})");
                    return;
                }
                f2.attendees.Clear();
                (onlooker.Inmate().tension, friend2.Inmate().tension) = (50f, 50f);
                f2.endTick = Find.TickManager.TicksGame;
                Funerals.Rare(g, map);
                if (f2.state != FuneralState.Done || onlooker.Inmate().tension != 50f || friend2.Inmate().tension != 50f || FuneralStage(friend2, RPFuneralDefOf.RP_SaidGoodbye) >= 0)
                    FuneralFail(check, $"a service nobody stood at left the wing's tension at {onlooker.Inmate().tension} and a friend's at {friend2.Inmate().tension} (50, 50), the friend's thought {FuneralStage(friend2, RPFuneralDefOf.RP_SaidGoodbye)} (none)");

                // A raid (an enemy on the map) keeps it from starting, and one under way is broken off.
                Pawn dead4 = s.Inmate("Dead4"), friend4 = s.Friend(dead4, "Friend4");
                (Corpse body4, Funeral f4, ChoiceLetter_PrisonDecision letter4) = s.Dead(dead4, 3);
                if (body4 == null || f4 == null) return;
                FuneralScene.Answer(letter4, 1);
                Funerals.ThreatForTests = true;
                s.HourComes(f4);
                bool heldByRaid = f4.state == FuneralState.Waiting;
                Funerals.ThreatForTests = false;
                if (!heldByRaid) FuneralFail(check, $"a funeral began while an enemy was on the map ({f4.state})");
                Funerals.Hourly(g, map, hour);
                if (f4.state != FuneralState.Gathering)
                {
                    FuneralFail(check, $"after the raid the funeral did not begin ({f4.Describe()})");
                    return;
                }
                friend4.Inmate().tension = 50f;
                Funerals.ThreatForTests = true;
                Funerals.Rare(g, map);
                Funerals.ThreatForTests = false;
                if (f4.state != FuneralState.Done || friend4.Inmate().tension != 50f || FuneralStage(friend4, RPFuneralDefOf.RP_SaidGoodbye) >= 0)
                    FuneralFail(check, $"a raid that began in the middle left the funeral {f4.state}, the tension {friend4.Inmate().tension} (50) (broken off: no thought, no relief)");

                // The letter's day counts from when it was written (a letter held back by the day's budget does not hold the body for the days it waits).
                Pawn dead5 = s.Inmate("Dead5");
                (Corpse body5, Funeral f5, ChoiceLetter_PrisonDecision letter5) = s.Dead(dead5, 4);
                if (body5 == null || f5 == null) return;
                if (letter5.decision.Worker.Disabled(letter5, 0) != null || letter5.decision.Worker.Disabled(letter5, 1) != null) FuneralFail(check, "an open letter has an answer that is disabled");
                f5.askedTick = Find.TickManager.TicksGame - Funerals.AskedMaxTicks - 1;
                Funerals.Hourly(g, map, hour);
                if (f5.state != FuneralState.Quiet) FuneralFail(check, $"a letter written more than a day ago is {f5.state} (a quiet burial)");
                // ...and what it was left unanswered cannot be answered: the answers say so, and choosing one does nothing.
                if (letter5.decision.Worker.Disabled(letter5, 1) == null || letter5.decision.Worker.Disabled(letter5, 0) == null) FuneralFail(check, "a letter that lapsed still offers its answers");
                FuneralScene.Answer(letter5, 1);
                if (f5.state != FuneralState.Quiet) FuneralFail(check, $"the answer to a lapsed letter moved the funeral to {f5.state}");

                // The body that has begun to rot cannot be held for the answer or for the hour.
                Pawn dead6 = s.Inmate("Dead6"), dead7 = s.Inmate("Dead7");
                (Corpse body6, Funeral f6, _) = s.Dead(dead6, 5);
                (Corpse body7, Funeral f7, ChoiceLetter_PrisonDecision letter7) = s.Dead(dead7, 6);
                if (body6 == null || f6 == null || body7 == null || f7 == null) return;
                FuneralScene.Answer(letter7, 1);
                Funerals.Hourly(g, map, hour);
                if (f6.state != FuneralState.Asked || f7.state == FuneralState.Quiet) FuneralFail(check, $"fresh bodies: the letter is {f6.state} (asked) and the chosen funeral {f7.state}");
                foreach (Corpse c in new[] { body6, body7 })
                    if (c.GetComp<CompRottable>() is { } rot) rot.RotProgress = rot.PropsRot.TicksToRotStart + 10;
                    else FuneralFail(check, "the premise: a body has no rot");
                Funerals.Hourly(g, map, hour);
                if (f6.state != FuneralState.Quiet || f7.state != FuneralState.Quiet) FuneralFail(check, $"rotting bodies: the open letter is {f6.state} and the chosen funeral {f7.state} (a quiet burial)");

                // Who was kept apart from the dead one is noted at the death (the order ends with it), and the note goes a week on if the body is never laid.
                Pawn dead8 = s.Inmate("Dead8"), other = s.Inmate("Other");
                if (KeepApart.Add(other, dead8, "feud", silent: true) != KeepApartResult.Added)
                {
                    FuneralFail(check, "the keep-apart order could not be written");
                    return;
                }
                dead8.Kill(null);
                List<int> note = g.story.funeralApart;
                if (note.Count != 3 || note[0] != dead8.thingIDNumber || note[1] != other.thingIDNumber || note[2] != Find.TickManager.TicksGame) FuneralFail(check, $"at the death the note is [{string.Join(", ", note)}] (the dead one's id #{dead8.thingIDNumber}, #{other.thingIDNumber} and the tick {Find.TickManager.TicksGame})");
                // (The body is never laid in the morgue: the note is not kept for ever.)
                Funerals.Hourly(g, map, hour);
                if (note.Count != 3) FuneralFail(check, "the note of who was kept apart is gone within the day of the death");
                Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + Funerals.RecordMaxTicks + 1);
                Funerals.Hourly(g, map, hour);
                if (note.Count != 0) FuneralFail(check, $"the note of who was kept apart is still there a week after the death ({note.Count} ids)");
                s.d.Body(dead8)?.Destroy();
            });
        }

        // ---- keeping apart ---------------------------------------------------------------------------------------------------------------------

        /// <summary>Of two friends of the dead whom the warden keeps apart one comes (the one who goes first today); one the warden keeps apart from the dead does not come at all.</summary>
        private static void CheckFuneralKeepApart(Map map)
        {
            const string check = "keeping apart";
            Guarded("funeral: a keep-apart partner does not attend", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                s.Staff("Guard", RPSecurityDefOf.RP_Guard);
                s.Staff("Guard2", RPSecurityDefOf.RP_Guard);
                Pawn dead = s.Inmate("Dead");
                Pawn a = s.Friend(dead, "A"), b = s.Friend(dead, "B"), c = s.Friend(dead, "C"), partner = s.Friend(dead, "Partner");
                // (The orders are written while they are alive: one between two friends, one between a friend and the one who will die.)
                if (KeepApart.Add(a, b, "feud", silent: true) != KeepApartResult.Added || KeepApart.Add(partner, dead, "feud", silent: true) != KeepApartResult.Added)
                {
                    FuneralFail(check, "the keep-apart orders could not be written");
                    return;
                }
                (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) = s.Dead(dead, 0);
                if (body == null || f == null) return;
                FuneralScene.Answer(letter, 1);
                s.HourComes(f);
                if (f.state != FuneralState.Gathering)
                {
                    FuneralFail(check, $"the funeral did not begin ({f.Describe()})");
                    return;
                }
                bool aFirst = KeepApart.Precedes(a, b);
                bool one = f.Attends(a.thingIDNumber) != f.Attends(b.thingIDNumber) && f.Attends((aFirst ? a : b).thingIDNumber);
                if (!one) FuneralFail(check, $"of two friends the warden keeps apart: {FuneralOnes(f, new[] { a, b })} (one comes, the one who goes first today: {(aFirst ? a : b).LabelShort})");
                // (The order with the dead one ended with their death: the funeral kept who it was with, from the kill hook, and the note is used up once the letter is written.)
                if (f.Attends(partner.thingIDNumber) || f.invited.Contains(partner.thingIDNumber)) FuneralFail(check, "one the warden kept apart from the dead one is invited to, or came to, the funeral");
                if (g.story.funeralApart.Count != 0) FuneralFail(check, $"the note of who was kept apart from the dead one was not used up when the letter was written ({g.story.funeralApart.Count} ids left)");
                if (!f.Attends(c.thingIDNumber)) FuneralFail(check, "a friend with no order was not sent");
            });
        }

        // ---- the staff's ------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A member of staff is seen off by the colleagues who are on duty and not needed elsewhere (a janitor at hand; not one on strike, not one suspended, not the workmen who dig, no more than the guards that can be spared), the
        /// chaplain leads it, the warden is sent to speak (the address job, in its funeral mode), no inmate comes; the staff have a morale thought, more for those who stood there and most when the warden spoke.
        /// </summary>
        private static void CheckFuneralStaff(Map map)
        {
            const string check = "a colleague";
            Guarded("funeral: a colleague is seen off, the warden speaks", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn warden = WardenSystem.InPost(map);
                if (warden != null && warden.Spawned) s.cast.Add(warden);
                // (The real warden is up and free for the service as the scene's staff are: they sleep when the real clock says night, and a warden in bed is not sent - the check moves the clock, not the warden's night. Put back by the rollback.)
                if (warden is { Spawned: true })
                {
                    if (warden.timetable != null)
                        for (int hour = 0; hour < 24; hour++)
                        {
                            int h = hour;
                            TimeAssignmentDef was = warden.timetable.GetAssignment(h);
                            r.Add(() => warden.timetable?.SetAssignment(h, was));
                            warden.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
                        }
                    if (warden.needs?.rest != null)
                    {
                        float rest = warden.needs.rest.CurLevel;
                        r.Add(() => warden.needs.rest.CurLevel = rest);
                        warden.needs.rest.CurLevel = 1f;
                    }
                }
                Funerals.ReachForTests = (p, c) => true;
                Pawn dead = s.Staff("Dead", RPSecurityDefOf.RP_Guard);
                // (A callout officer has no family to send for them: the plot is theirs.)
                dead.GetComp<CompStaff>().leaveAtTick = Find.TickManager.TicksGame + 10 * GenDate.TicksPerDay;
                // (A guard the player has drafted - for a raid, a fight, an escape - is not taken to a funeral: the first of the guards by id, so it would be the one sent.)
                Pawn drafted = s.Staff("Drafted", RPSecurityDefOf.RP_Guard);
                if (drafted.drafter != null) drafted.drafter.Drafted = true;
                else FuneralFail(check, "the premise: the check's guard cannot be drafted");
                Pawn guardA = s.Staff("GuardA", RPSecurityDefOf.RP_Guard), guardB = s.Staff("GuardB", RPSecurityDefOf.RP_Guard), janitor = s.Staff("Janitor", Role("RP_Janitor"));
                Pawn striker = s.Staff("Striker", Role("RP_Janitor")), suspended = s.Staff("Suspended", Role("RP_Janitor")), workman = s.Staff("Workman", Role("RP_Workman"), s.d.At(8, 5)), chaplain = s.Staff("Chaplain", RPStaffRoleDefOf.RP_Chaplain);
                striker.GetComp<CompStaff>().strikeUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
                suspended.GetComp<CompStaff>().suspendedUntilTick = Find.TickManager.TicksGame + GenDate.TicksPerDay;
                Pawn inmate = s.Inmate("Inmate", gang: DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"));
                r.Add(() =>
                {
                    if (warden is { Dead: false, Spawned: true } && warden.CurJobDef == RPWardenDefOf.RP_WardenAddress) warden.jobs.EndCurrentJob(JobCondition.InterruptForced);
                });
                // (No new job at once: a warden who was in bed would lie down again in the same breath; the funeral's hour comes before the next think.)
                if (warden != null && warden.Spawned) warden.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                dead.Kill(null);
                Corpse body = s.d.Body(dead);
                if (body == null) return;
                s.d.PutInMorgue(body, 0);
                Funerals.Notify_Laid(body);
                Funeral f = Funerals.Of(dead);
                ChoiceLetter_PrisonDecision letter = r.NewLetters.OfType<ChoiceLetter_PrisonDecision>().FirstOrDefault(l => l.Is("RP_Decision_Funeral") && l.amount == dead.thingIDNumber);
                if (f == null || letter == null || !f.staff || f.place != FuneralPlace.Grave)
                {
                    FuneralFail(check, $"the letter for a colleague with no family ({f?.Describe() ?? "no record"}, letter {letter != null}): at the grave, as one of the staff");
                    return;
                }
                if (letter.Text.ToString() != "RP_FuneralTextStaff".Translate(dead.LabelShort, "RP_FuneralPlace_Grave".Translate()).Resolve()) FuneralFail(check, "the letter for a colleague is not the staff's text");
                FuneralScene.Answer(letter, 1);
                s.HourComes(f);
                if (f.state != FuneralState.Gathering)
                {
                    FuneralFail(check, $"the funeral did not begin ({f.Describe()})");
                    return;
                }
                Pawn[] pool = { guardA, guardB, janitor, striker, suspended, workman, chaplain };
                int guards = new[] { guardA, guardB }.Count(x => f.Attends(x.thingIDNumber));
                if (f.Attends(drafted.thingIDNumber)) FuneralFail(check, "a drafted guard was sent to a colleague's funeral");
                if (!f.Attends(janitor.thingIDNumber) || f.Attends(striker.thingIDNumber) || f.Attends(suspended.thingIDNumber) || f.Attends(workman.thingIDNumber) || !f.Attends(chaplain.thingIDNumber) || guards != 1)
                    FuneralFail(check, $"the colleagues sent: {FuneralOnes(f, pool)} (the janitor, the chaplain and one of two guards; not the striker, the suspended, the workman who digs)");
                if (f.Attends(inmate.thingIDNumber) || f.mourners != 0) FuneralFail(check, "an inmate was sent to a colleague's funeral");
                if (warden != null && warden.Spawned)
                {
                    if (!f.Attends(warden.thingIDNumber) || warden.CurJobDef != RPWardenDefOf.RP_WardenAddress || warden.CurJob.count != JobDriver_WardenAddress.EulogyMode) FuneralFail(check, $"the warden was not sent to speak ({warden.CurJobDef?.defName}, count {warden.CurJob?.count})");
                    // The speech is the address job's own, in its funeral mode: the warden stands at the front (the real warden is put there, and back after) and the job runs on real ticks.
                    if (warden.CurJobDef == RPWardenDefOf.RP_WardenAddress)
                    {
                        IntVec3 home = warden.Position;
                        r.Add(() =>
                        {
                            if (warden is { Dead: false, Spawned: true }) s.Place(warden, home);
                        });
                        // (Not s.Place: a teleport ends the pawn's job - Pawn.Notify_Teleported - and the warden would be on GotoWander before the first tick. The job is kept; the teleport stops the pather without telling the job it has arrived - only a walk's own end does, through Notify_PatherArrived - so the walk toil would wait for ever, and the arrival is told here.)
                        warden.pather.StopDead();
                        warden.Position = warden.CurJob.targetA.Cell;
                        warden.Notify_Teleported(endCurrentJob: false);
                        warden.jobs.curDriver.Notify_PatherArrived();
                        ShuttleTicks(() => f.spoke, JobDriver_WardenAddress.SpeakTicks + 200);
                    }
                    if (!f.spoke) FuneralFail(check, $"the warden stood at the front for {JobDriver_WardenAddress.SpeakTicks + 200} ticks and the speech is not on the record (job {warden.CurJobDef?.defName}, the funeral {f.state})");
                }
                else RPLog.Message("[RimPrison:test] funeral: no warden on the map to speak; the speech is not checked");
                // (The scene's people are there: the service is judged by who stands near it. The real warden is not moved: the speech is on the record by the job's own call.)
                foreach (Pawn p in new[] { janitor, chaplain, guardA, guardB })
                    if (f.Attends(p.thingIDNumber)) s.Place(p, f.cell + new IntVec3(1, 0, 0));
                f.endTick = Find.TickManager.TicksGame;
                Funerals.Rare(g, map);
                int Stage(Pawn p) => FuneralStage(p, RPFuneralDefOf.RP_StaffFarewell);
                int want = f.spoke ? 2 : 1;
                if (f.state != FuneralState.Done || Stage(janitor) != want || Stage(workman) != 0 || Stage(dead) != -1)
                    FuneralFail(check, $"the staff's thoughts: {f.state}, the janitor who stood there {Stage(janitor)} (stage {want}), the workman who did not {Stage(workman)} (0), the dead one {Stage(dead)} (none)");
                if (FuneralStage(inmate, RPFuneralDefOf.RP_SaidGoodbye) >= 0 || FuneralStage(inmate, RPFuneralDefOf.RP_StaffFarewell) >= 0) FuneralFail(check, "an inmate has a thought of the colleague's funeral");
            });
        }

        // ---- the family, the chapel, and a plot nobody can walk into --------------------------------------------------------------------------

        /// <summary>
        /// The dead who go home have their funeral in the chapel, and a relative in touch comes from outside as a visitor for the hour (not when the visitation room cannot bring them: a level that sees no one, a family that is
        /// barred); an inmate's mourners who cannot walk into the staff-only plot have the service in the chapel while the burial goes on as it did, and the funeral that was to be at the grave is in the chapel when the shuttle takes the
        /// body first.
        /// </summary>
        private static void CheckFuneralFamilyChapelAndThePlot(Map map)
        {
            const string check = "the family and the chapel";
            Guarded("funeral: the family comes as visitors, the chapel when the mourners cannot reach the plot", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                s.Staff("Guard", RPSecurityDefOf.RP_Guard);
                s.Staff("Guard2", RPSecurityDefOf.RP_Guard);
                Pawn workman = s.Staff("Workman", Role("RP_Workman"), s.d.At(8, 5));
                var bury = new WorkGiver_BuryBody { def = DefDatabase<WorkGiverDef>.GetNamed("RP_BuryBody") };
                Funerals.ReachForTests = (p, c) => true;

                // A body with a family, seen off in the chapel, with the relative come from outside.
                Pawn dead = s.Inmate("Dead", family: true);
                Pawn friend = s.Friend(dead, "Friend");
                (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) = s.Dead(dead, 0);
                if (body == null || f == null) return;
                FuneralScene.Answer(letter, 1);
                bool visitation = s.pm.zones.Any(RPVisitDefOf.RP_Visitation);
                s.HourComes(f);
                if (f.state != FuneralState.Gathering || f.place != FuneralPlace.Chapel || !s.ChapelRect.Contains(f.cell) || s.pm.zones.ZoneAt(f.cell) != RPFaithDefOf.RP_Chapel)
                {
                    FuneralFail(check, $"the funeral for one who goes home is {f.Describe()} (gathering in the chapel, at a cell of it)");
                    return;
                }
                if (!f.Attends(friend.thingIDNumber)) FuneralFail(check, "the friend was not sent to the chapel");
                if (visitation)
                {
                    Pawn visitor = f.visitors.FirstOrDefault();
                    if (visitor != null) r.Add(() =>
                    {
                        if (!visitor.Destroyed) visitor.Destroy();
                    });
                    if (visitor == null || f.visitors.Count != 1 || !visitor.LabelShort.Contains("Ines") || f.relative == null || !f.relative.Contains("Ines") || visitor.GetLord()?.LordJob is not LordJob_AttendFuneral)
                        FuneralFail(check, $"the relative did not come as a visitor ({f.visitors.Count} visitors, {visitor?.LabelShort}, relative {f.relative}, lord {visitor?.GetLord()?.LordJob?.GetType().Name})");
                }
                else RPLog.Message("[RimPrison:test] funeral: the prison has no visitation room; the relative's visit is not checked");

                // No visitor when the dead one's level is not visited; none named when the family is estranged (nobody to come), and none brought when the family is barred.
                if (visitation)
                {
                    Pawn deadB = s.Inmate("DeadB", family: true);
                    (Corpse bodyB, Funeral fB, _) = s.Dead(deadB, 4);
                    if (bodyB == null || fB == null) return;
                    if (fB.kinName != "Ines" || fB.kinKind == null) FuneralFail(check, $"the record names {fB.kinName} as the relative who would come (Ines)");
                    g.visitPolicies[s.level] = VisitPolicy.None;
                    if (Funerals.RelativeWhoCanCome(g, map, s.pm, fB) != null) FuneralFail(check, "a relative comes for one whose level is visited by no one");
                    g.visitPolicies[s.level] = VisitPolicy.Booth;
                    if (Funerals.RelativeWhoCanCome(g, map, s.pm, fB) != null) FuneralFail(check, "a relative walks in to the service of one whose level is visited through glass");
                    g.visitPolicies[s.level] = VisitPolicy.Contact;
                    if (Funerals.RelativeWhoCanCome(g, map, s.pm, fB) == null) FuneralFail(check, "no relative can come for one of a level that is visited");
                    Pawn deadC = s.Inmate("DeadC", family: true);
                    deadC.Inmate().relatives[0].estranged = true;
                    (Corpse bodyC, Funeral fC, _) = s.Dead(deadC, 5);
                    if (bodyC == null || fC == null) return;
                    if (fC.kinName != null || Funerals.RelativeWhoCanCome(g, map, s.pm, fC) != null) FuneralFail(check, $"an estranged relative is named to come ({fC.kinName})");
                }

                // The plot is staff-only: mourners who cannot walk into it have the service in the chapel, and the burial goes on.
                CellRect plot = s.PlotRect;
                Funerals.ReachForTests = (p, c) => p.Inmate() == null || !plot.Contains(c);
                Pawn dead2 = s.Inmate("Dead2"), friend2 = s.Friend(dead2, "Friend2");
                (Corpse body2, Funeral f2, ChoiceLetter_PrisonDecision letter2) = s.Dead(dead2, 1);
                if (body2 == null || f2 == null) return;
                if (f2.place != FuneralPlace.Grave) FuneralFail(check, $"the premise: the letter for a body the plot can take says {f2.place}");
                FuneralScene.Answer(letter2, 1);
                s.HourComes(f2);
                if (f2.state != FuneralState.Gathering || f2.place != FuneralPlace.Chapel || !f2.Attends(friend2.thingIDNumber) || !bury.HasJobOnThing(workman, body2))
                    FuneralFail(check, $"mourners who cannot enter the plot: {f2.Describe()} (in the chapel with the friend), the workman's job {bury.HasJobOnThing(workman, body2)} (the burial goes on)");
                // ...and where they can, it is at the grave.
                Pawn dead3 = s.Inmate("Dead3"), friend3 = s.Friend(dead3, "Friend3");
                (Corpse body3, Funeral f3, ChoiceLetter_PrisonDecision letter3) = s.Dead(dead3, 2);
                if (body3 == null || f3 == null) return;
                Funerals.ReachForTests = (p, c) => true;
                // (The second funeral is still under way: the guards are with it. A third guard for these mourners.)
                s.Staff("Guard3", RPSecurityDefOf.RP_Guard);
                s.Staff("Guard4", RPSecurityDefOf.RP_Guard);
                FuneralScene.Answer(letter3, 1);
                s.HourComes(f3);
                if (f3.state != FuneralState.Gathering || f3.place != FuneralPlace.Grave) FuneralFail(check, $"mourners who can walk to the plot: {f3.Describe()} (at the grave)");

                // The shuttle takes the body first: the funeral is in the chapel.
                Pawn dead4 = s.Inmate("Dead4");
                (Corpse body4, Funeral f4, ChoiceLetter_PrisonDecision letter4) = s.Dead(dead4, 3);
                if (body4 == null || f4 == null) return;
                FuneralScene.Answer(letter4, 1);
                Funerals.Notify_Sent(dead4);
                if (f4.place != FuneralPlace.Chapel) FuneralFail(check, $"the body went on the shuttle and the funeral is {f4.place} (in the chapel)");
            });
        }

        // ---- the guards are the cost ----------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Mourners do not come out without a guard: with none to spare (the guards on duty, less the half who must stay) the service waits; with one, five mourners at most come (the rest are not sent), and that guard is at the funeral
        /// - busy - while the other is not; with more guards, more mourners.
        /// </summary>
        private static void CheckFuneralGuardsAreTheCost(Map map)
        {
            const string check = "the guards";
            Guarded("funeral: the guards who escort are busy, and the mourners do not come without one", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn dead = s.Inmate("Dead");
                var friends = new List<Pawn>();
                for (int i = 0; i < 7; i++) friends.Add(s.Friend(dead, "F" + i));
                (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) = s.Dead(dead, 0);
                if (body == null || f == null) return;
                FuneralScene.Answer(letter, 1);
                // No guard at all: it waits.
                s.HourComes(f);
                if (f.state != FuneralState.Waiting || f.attendees.Count != 0) FuneralFail(check, $"with no guard to spare the funeral is {f.Describe()} (waiting, nobody sent)");
                // One guard alone is the last on the floor: it waits.
                Pawn only = s.Staff("Only", RPSecurityDefOf.RP_Guard);
                Funerals.Hourly(g, map, GenLocalDate.HourOfDay(map));
                if (f.state != FuneralState.Waiting) FuneralFail(check, $"with one guard on duty (the last on the floor) the funeral began ({f.state})");
                // Two: one is spared, five mourners at most.
                Pawn second = s.Staff("Second", RPSecurityDefOf.RP_Guard, s.d.At(9, 1));
                Funerals.Hourly(g, map, GenLocalDate.HourOfDay(map));
                int busy = new[] { only, second }.Count(x => x.CurJobDef == RPFuneralDefOf.RP_AttendFuneral);
                int sent = friends.Count(x => f.Attends(x.thingIDNumber));
                if (f.state != FuneralState.Gathering || busy != 1 || sent != Funerals.EscortRatio || f.mourners != Funerals.EscortRatio)
                    FuneralFail(check, $"with two guards the funeral is {f.state}, {busy} guard(s) are at it (1), {sent} of seven friends are sent ({Funerals.EscortRatio}), {f.mourners} counted");
            });
            Guarded("funeral: more guards, more mourners", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                Pawn dead = s.Inmate("Dead");
                var friends = new List<Pawn>();
                for (int i = 0; i < 7; i++) friends.Add(s.Friend(dead, "F" + i));
                var guards = new List<Pawn>();
                for (int i = 0; i < 4; i++) guards.Add(s.Staff("G" + i, RPSecurityDefOf.RP_Guard, s.d.At(8 + i % 3, 1 + i / 3)));
                (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) = s.Dead(dead, 0);
                if (body == null || f == null) return;
                FuneralScene.Answer(letter, 1);
                s.HourComes(f);
                int busy = guards.Count(x => x.CurJobDef == RPFuneralDefOf.RP_AttendFuneral);
                int sent = friends.Count(x => f.Attends(x.thingIDNumber));
                if (f.state != FuneralState.Gathering || busy != 2 || sent != 7) FuneralFail(check, $"with four guards the funeral is {f.state}, {busy} guards are at it (2: half of four), {sent} of seven friends are sent (7)");
            });
        }

        // ---- the rival --------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A rival of the killer's gang who is on the dead's wing turns up at the stated chance (a roll under <see cref="Funerals.ScuffleChance"/>), only when the dead one was killed by that gang (the case against the killer says so) and
        /// the gangs are rivals; once they and a mourner are near each other the pastimes' scuffle is rolled between them and the chronicle says what came of it.
        /// </summary>
        private static void CheckFuneralRival(Map map)
        {
            const string check = "the rival";
            Guarded("funeral: a rival of the killer's gang turns up at the stated chance", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                GangDef ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"), saints = DefDatabase<GangDef>.GetNamed("RP_Gang_Saints");
                s.Staff("Guard", RPSecurityDefOf.RP_Guard);
                s.Staff("Guard2", RPSecurityDefOf.RP_Guard);
                Pawn dead = s.Inmate("Dead", gang: ravens), mate = s.Inmate("Mate", gang: ravens), killer = s.Inmate("Killer", gang: saints);
                Pawn rival = s.Inmate("Rival", gang: saints, security: s.level);
                g.story.trials.Add(new MurderTrial { id = 987001, victimId = dead.thingIDNumber, killerId = killer.thingIDNumber, victimName = dead.LabelShort, killerName = killer.LabelShort });
                (Corpse body, Funeral f, ChoiceLetter_PrisonDecision letter) = s.Dead(dead, 0);
                if (body == null || f == null) return;
                if (Funerals.KillerGangOf(map, dead.thingIDNumber) != saints) FuneralFail(check, $"the killer's gang is {Funerals.KillerGangOf(map, dead.thingIDNumber)?.defName} (the Saints)");
                FuneralScene.Answer(letter, 1);
                Find.TickManager.DebugSetTicksGame(f.holdTick);
                int hour = GenLocalDate.HourOfDay(map);
                Pawn Stirrer(float roll)
                {
                    Funerals.ScuffleRollForTests = roll;
                    Funerals.CastForTests = p => s.cast.Contains(p);
                    var cast = new Funerals.Cast();
                    cast.mourners.Add(mate);
                    Funerals.RollTheRivalForTests(g, map, s.pm, f, hour, cast);
                    return cast.stirrer;
                }
                // (The killer is made first, so has the lower id, and is on the wing and free to walk: the rival is the other of the gang, not the one who did it.)
                if (killer.thingIDNumber > rival.thingIDNumber) FuneralFail(check, "the premise: the killer has the higher id, which does not tell the killer from the rival");
                Pawn first = Stirrer(Funerals.ScuffleChance - 0.001f);
                if (first != rival) FuneralFail(check, $"a roll just under {Funerals.ScuffleChance:P0} brought {first?.LabelShort ?? "nobody"} (the rival, {rival.LabelShort}, not the killer)");
                // One the warden keeps apart from a mourner is not sent to stand by them.
                if (KeepApart.Add(rival, mate, "feud", silent: true) == KeepApartResult.Added)
                {
                    if (Stirrer(0f) != null) FuneralFail(check, "a rival the warden keeps apart from a mourner was sent to the funeral");
                    KeepApart.Lift(KeepApart.OrderBetween(rival, mate), silent: true);
                }
                else FuneralFail(check, "the keep-apart order for the rival and a mourner could not be written");
                if (Stirrer(Funerals.ScuffleChance) != null || Stirrer(0.9f) != null) FuneralFail(check, $"a roll at or over {Funerals.ScuffleChance:P0} brought the rival");
                // The wrong gang: the killer in the dead one's own gang; no case at all; a rival on another wing only.
                killer.Inmate().gang = ravens;
                if (Stirrer(0f) != null) FuneralFail(check, "a rival turned up when the killer was of the dead one's own gang");
                killer.Inmate().gang = saints;
                g.story.trials.RemoveAll(t => t.id == 987001);
                if (Stirrer(0f) != null) FuneralFail(check, "a rival turned up for a death with no case against anyone");
                g.story.trials.Add(new MurderTrial { id = 987001, victimId = dead.thingIDNumber, killerId = killer.thingIDNumber });
                rival.Inmate().security = DefDatabase<SecurityLevelDef>.GetNamed("RP_Minimum");
                if (Stirrer(0f) != null) FuneralFail(check, "a rival of another wing turned up");
                rival.Inmate().security = s.level;

                // A real start with the roll made: the rival is sent, and once near the mourner the scuffle is rolled once.
                Funerals.ScuffleRollForTests = 0f;
                Funerals.Hourly(g, map, hour);
                if (f.state != FuneralState.Gathering || f.stirrerId != rival.thingIDNumber || !f.Attends(f.stirrerId)) FuneralFail(check, $"the rival ({rival.LabelShort}, #{rival.thingIDNumber}) was not sent to the funeral, or the killer was ({f.Describe()})");
                Pawn stirrer = map.mapPawns.AllPawnsSpawned.FirstOrDefault(x => x.thingIDNumber == f.stirrerId);
                if (stirrer == null || f.cell.IsValid == false) return;
                s.Place(stirrer, f.cell + new IntVec3(1, 0, 0));
                s.Place(mate, f.cell + new IntVec3(-1, 0, 0));
                Funerals.Rare(g, map);
                string fight = "RP_Chr_FuneralScuffle".Translate(stirrer.LabelShort, stirrer.Inmate().gang.label, dead.LabelShort), nothing = "RP_Chr_FuneralScuffleNone".Translate(stirrer.LabelShort, stirrer.Inmate().gang.label, dead.LabelShort);
                int lines = r.NewEntries.Count(e => e.text == fight || e.text == nothing);
                if (!f.scuffled || lines != 1) FuneralFail(check, $"the rival stood beside a mourner: scuffle rolled {f.scuffled}, {lines} line(s) in the chronicle (one, a fight or nothing came of it)");
                Funerals.Rare(g, map);
                if (r.NewEntries.Count(e => e.text == fight || e.text == nothing) != 1) FuneralFail(check, "the scuffle was rolled twice");
                // The one who turned up is no mourner: no thought, no relief.
                f.endTick = Find.TickManager.TicksGame;
                stirrer.Inmate().tension = 50f;
                Funerals.Rare(g, map);
                if (FuneralStage(stirrer, RPFuneralDefOf.RP_SaidGoodbye) >= 0) FuneralFail(check, "the rival who came to make trouble said goodbye");
            });
        }

        // ---- the turf count ---------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The turf count: a gang's members standing on a yard patch are counted by the hourly pass; the same members at a funeral (or back from one within the turn of the hour) are not counted at all, and the count is the same again past
        /// it. The count of the others does not move.
        /// </summary>
        private static void CheckFuneralTurfCountLeavesThemOut(Map map)
        {
            const string check = "the turf count";
            Guarded("funeral: the gang at a funeral does not move the turf tally", map, r =>
            {
                FuneralScene s = FuneralScene.Build(map, r, check);
                if (s == null) return;
                PrisonGame g = s.g;
                PrisonMap pm = s.pm;
                TurfBook book = pm.turf;
                GangDef ravens = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens");
                List<ZonePatch> patches = pm.zones.Patches;
                int yardIndex = -1;
                for (int i = 0; i < patches.Count; i++)
                    if (patches[i].zone.gangTurf && (yardIndex < 0 || patches[i].cells > patches[yardIndex].cells)) yardIndex = i;
                List<IntVec3> cells = yardIndex < 0 ? new List<IntVec3>() : pm.zones.CellsOf(patches[yardIndex].zone).Where(c => pm.zones.PatchAt(c) == yardIndex && c.Standable(map)).Take(3).ToList();
                if (cells.Count < 3)
                {
                    RPLog.Message("[RimPrison:test] funeral: no yard patch with three standable cells; the turf check is skipped");
                    return;
                }
                var tallies0 = book.tallies.ToList();
                r.Add(() =>
                {
                    book.tallies.Clear();
                    foreach (KeyValuePair<int, TurfBook.PatchTally> kv in tallies0) book.tallies[kv.Key] = kv.Value;
                });
                var gang = new List<Pawn>();
                for (int i = 0; i < 3; i++)
                {
                    Pawn p = s.Inmate("Raven" + i, gang: ravens);
                    s.Place(p, cells[i]);
                    gang.Add(p);
                }
                int anchor = patches[yardIndex].anchor;
                (int people, int sum) Count()
                {
                    book.tallies.Clear();
                    GangTurf.Sample(map, pm);
                    return book.tallies.TryGetValue(anchor, out TurfBook.PatchTally t) ? (t.people, t.gangs.TryGetValue(ravens, out TurfBook.GangCount gc) ? gc.sum : 0) : (0, 0);
                }
                (int people, int sum) with = Count();
                if (with.sum < 3) FuneralFail(check, $"the premise: the pass counted {with.sum} of the three Ravens on the yard patch");
                int now = Find.TickManager.TicksGame;
                var f = new Funeral { deadId = 987654, name = "Zfudead", state = FuneralState.Gathering, startTick = now - 100, endTick = now + 1000, attendees = gang.Select(p => p.thingIDNumber).ToList() };
                g.story.funerals.Add(f);
                (int people, int sum) at = Count();
                if (at.sum != with.sum - 3 || at.people != with.people - 3) FuneralFail(check, $"with the three at a funeral the pass counts {at.people} people and {at.sum} Ravens (was {with.people} and {with.sum}: three fewer of each)");
                // Just after: still out; past the turn of the hour: counted again.
                (f.state, f.endTick) = (FuneralState.Done, now);
                Find.TickManager.DebugSetTicksGame(now + Funerals.AwayGraceTicks);
                (int people, int sum) after = Count();
                if (after.sum != at.sum) FuneralFail(check, $"at the turn of the hour after the service the pass counts {after.sum} Ravens (still {at.sum}, the three are out)");
                Find.TickManager.DebugSetTicksGame(now + Funerals.AwayGraceTicks + 2);
                (int people, int sum) later = Count();
                if (later != with) FuneralFail(check, $"past it the pass counts {later.people} people and {later.sum} Ravens (was {with.people} and {with.sum}, back as they were)");
            });
        }

        // ---- the saveload plan --------------------------------------------------------------------------------------------------------------

        private static List<Funeral> funeralsBeforeSeed;
        private static List<int> funeralApartBeforeSeed;
        private static string funeralSeedState;

        /// <summary>Every field of a funeral off its default, in a pending funeral (waiting for an hour days away, with a pawn as a visitor).</summary>
        private static void SeedFuneralForReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            funeralsBeforeSeed = g.story.funerals.ToList();
            int now = Find.TickManager.TicksGame;
            Pawn visitor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            var seeded = new Funeral
            {
                deadId = 987654, name = "Seedfuneral", staff = true, state = FuneralState.Waiting, place = FuneralPlace.Pad, cell = new IntVec3(3, 0, 4),
                askedTick = now - 123, holdTick = now + 5 * GenDate.TicksPerDay, startTick = now - 77, endTick = now - 55, attendees = new List<int> { 11, 22, 33 }, mourners = 3,
                chaplain = true, spoke = true, buried = true, stirrerId = 44, scuffled = true, relative = "Seedrelative",
                level = DefDatabase<SecurityLevelDef>.GetNamed("RP_Maximum"), gang = DefDatabase<GangDef>.GetNamed("RP_Gang_Ravens"), invited = new List<int> { 55, 66 }, kinName = "Seedkin", kinKind = "RP_Kin_Mother",
                visitors = visitor != null ? new List<Pawn> { visitor } : new List<Pawn>(),
            };
            var quiet = new Funeral { deadId = 987655, name = "Seedquiet", state = FuneralState.Quiet, place = FuneralPlace.Chapel, askedTick = now - 321 };
            g.story.funerals.Add(seeded);
            g.story.funerals.Add(quiet);
            funeralApartBeforeSeed = g.story.funeralApart;
            g.story.funeralApart = new List<int> { 987654, 77, now - 10, 987655, 88, now - 20 };
            funeralSeedState = Funerals.Describe(g);
            RPLog.Message($"[RimPrison:test] saveload funerals: {funeralSeedState}");
            if (visitor == null) reloadProblems.Add("saveload: no colonist to seed a funeral's visitor with");
        }

        private static void CheckFuneralAfterReload(Map map)
        {
            PrisonGame g = PrisonGame.Instance;
            if (funeralsBeforeSeed == null) return;
            string now = Funerals.Describe(g);
            if (now != funeralSeedState) reloadProblems.Add($"saveload: the funerals changed across the load ({funeralSeedState} -> {now})");
            g.story.funerals = funeralsBeforeSeed;
            g.story.funeralApart = funeralApartBeforeSeed ?? new List<int>();
            (funeralsBeforeSeed, funeralApartBeforeSeed) = (null, null);
        }
    }

    /// <summary>
    /// Autotest: the funeral checks' scene stands on open ground outside the compound, where an inmate is already outside the walls and runs at their next hourly check (the grave check's 1800 real ticks cross it: the gang mate and
    /// the stranger left the map). The scene's own people do not run; the real prison's do, as ever.
    /// </summary>
    [HarmonyPatch(typeof(EscapeUtility), nameof(EscapeUtility.HourlyCheck))]
    [TestOnly]
    public static class Patch_EscapeUtility_HourlyCheck_Funeral
    {
        public static bool Prefix(Pawn p) => Funerals.CastForTests == null || !Funerals.CastForTests(p);
    }
}
