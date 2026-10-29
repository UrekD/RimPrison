using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>
    /// Regression checks for the inmate mailroom clerk in big blocks: the builder gives them a mailroom of their own (the staff room's stays for the
    /// guards), the clerk's job has a slot for it, and a worker coming through its Auto door can reach the table. Each is its own method, works
    /// on the map as built, and puts back what it touches (the <see cref="Rollback"/>, in a finally).
    /// </summary>
    public static partial class AutoTest
    {
        /// <summary>Every hour of the huge plan: the clerk job's slots, who holds it, what they are doing and how much post waits (the trace of what stopped the clerk).</summary>
        private static void LogClerks(Map map, int hour)
        {
            PrisonGame g = PrisonGame.Instance;
            InmateJobDef job = DefDatabase<InmateJobDef>.GetNamed("RP_JobMailroom");
            List<Pawn> clerks = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.Inmate()?.job == job).ToList();
            int minimum = map.mapPawns.PrisonersOfColonySpawned.Count(p => p.Inmate() is { activeFlag: true, security: { } s } && job.allowedSecurity.Contains(s));
            RPLog.Message($"[RimPrison:clerk] hour {hour}: slots {job.Worker.Capacity(map)}, {clerks.Count} clerks of {minimum} minimum-security inmates, {g.mail.waiting.Count} letters waiting, {g.stats.lettersDelivered} delivered");
            foreach (Pawn p in clerks)
            {
                CompInmate c = p.Inmate();
                RPLog.Message($"[RimPrison:clerk]   {p.LabelShort}: activity {g.CurrentActivity(p)?.defName} job {p.CurJobDef?.defName} at {p.Position - PrisonBuilder.Origin(map)} pass {c.workPass?.defName} tension {c.tension:F0} forced {c.forcedActivity?.defName} mental {p.MentalStateDef?.defName}");
                // (An hour the clerk could have been at the table: at work, well, on their feet, and the post waiting. See ClerkShiftHours.)
                if (g.mail.waiting.Count > 0 && g.CurrentActivity(p)?.defName == "RP_Work" && !p.InMentalState && !p.Downed && p.CurJobDef != JobDefOf.LayDown && c.forcedActivity == null) ClerkShiftHours++;
            }
        }

        /// <summary>
        /// A block of <see cref="PrisonBuilder.BigMailroomCells"/> cells or more has a second mailroom behind an Auto door from inmate space: the clerk's job has a
        /// slot (the starter prison's staff-room mailroom gave it none), the job's capacity is that slot, and with the door open to its
        /// worker the table can be walked to from the hall, while shut it cannot (the room has no other way in).
        /// </summary>
        private static void CheckBigBlockHasClerkMailroom(Map map)
        {
            if (PrisonBuilder.Cells < PrisonBuilder.BigMailroomCells) return;
            Guarded("big block has a clerk mailroom", map, r =>
            {
                PrisonMap pm = PrisonMap.Of(map);
                List<Thing> tables = MailSystem.Tables(map);
                int slots = MailSystem.InmateClerkSlots(map), capacity = DefDatabase<InmateJobDef>.GetNamed("RP_JobMailroom").Worker.Capacity(map);
                CompPrisonDoor door = pm.Doors.FirstOrDefault(d => d.workZone == RPMailDefOf.RP_Mailroom);
                if (tables.Count < 2 || slots != 1 || capacity != slots || door == null || door.mode != PrisonDoorMode.Auto)
                {
                    problems0.Add($"clerk mailroom: {tables.Count} mailroom tables (the staff room's and the clerk's expected), {slots} clerk slots and job capacity {capacity} (1 expected), "
                                  + $"work door {(door == null ? "missing" : door.mode.ToString())}");
                    return;
                }
                // The Inmates tab's job menu says how many places the job has (it showed none).
                string slotsText = InmatesTab.JobSlots(DefDatabase<InmateJobDef>.GetNamed("RP_JobMailroom"), map);
                if (slotsText != "RP_JobSlots".Translate(map.mapPawns.PrisonersOfColonySpawned.Count(x => x.Inmate()?.job == DefDatabase<InmateJobDef>.GetNamed("RP_JobMailroom")), slots).Resolve() || !slotsText.Contains("1"))
                    problems0.Add($"clerk mailroom: the job menu says \"{slotsText}\" for the mailroom job");
                // The tables the door leads to: the clerk's; the staff room's is behind a StaffOnly door.
                IntVec3 inside = IntVec3.Invalid, outside = IntVec3.Invalid;
                foreach (IntVec3 c in GenAdj.CellsAdjacentCardinal(door.parent).Where(c => c.Walkable(map)))
                    if (pm.zones.ZoneAt(c) == RPMailDefOf.RP_Mailroom) inside = c;
                    else outside = c;
                if (!inside.IsValid || !outside.IsValid)
                {
                    problems0.Add("clerk mailroom: the work door does not lead from outside into the mailroom zone");
                    return;
                }
                Thing clerkTable = tables.FirstOrDefault(t => t.OccupiedRect().Cells.Any(c => c.GetRoom(map) == inside.GetRoom(map)));
                if (clerkTable == null)
                {
                    problems0.Add("clerk mailroom: no mailroom table in the room behind the work door");
                    return;
                }
                bool wasOpen = door.inmatePass;
                r.Add(() => door.inmatePass = wasOpen);
                IntVec3 beside = clerkTable.OccupiedRect().AdjacentCellsCardinal.FirstOrDefault(c => c.GetRoom(map) == inside.GetRoom(map) && c.Walkable(map));
                door.inmatePass = false;
                bool shut = InmateReachability.CanInmateWalk(map, outside, beside);
                door.inmatePass = true;
                bool open = InmateReachability.CanInmateWalk(map, outside, beside);
                if (shut || !open || !beside.IsValid)
                    problems0.Add($"clerk mailroom: the table can be walked to with the door shut {shut}, with it open {open} (false and true expected), a spot beside the table {beside.IsValid}");
            });
        }

        /// <summary>
        /// A guard leaves post to a clerk who is on mailroom duty (there is a slot for the clerk's job and an inmate holds it) until it has waited
        /// <see cref="MailSystem.GuardTakesOverTicks"/>: without a clerk, or from the clerk's own hands, the post is always theirs to take. (The guards used to sort every
        /// night's post the moment it came, before the clerk's shift began, so the clerk never saw a letter.)
        /// </summary>
        private static void CheckGuardsLeaveFreshPostToTheClerk(Map map)
        {
            Guarded("guards leave fresh post to the clerk", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                PrisonMap pm = PrisonMap.Of(map);
                MailSystem mail = g.mail;
                List<Pawn> able = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && !x.Downed && x.Inmate().activeFlag).Take(2).ToList();
                Pawn guard = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => GuardUtility.IsGuard(x));
                if (able.Count < 2 || guard == null)
                {
                    problems0.Add($"guards and the clerk: needs two active inmates and a guard, found {able.Count} and {(guard == null ? "no guard" : "one")}");
                    return;
                }
                (Pawn recipient, Pawn clerk) = (able[0], able[1]);
                (CompInmate rc, CompInmate cc) = (recipient.Inmate(), clerk.Inmate());
                // The mailroom and a door into it for a worker (the layout may have neither).
                if (!MailSystem.HasMailroom(map))
                {
                    Building table = map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def.surfaceType == SurfaceType.Eat && pm.zones.ZoneAt(b.Position) is { staffOnly: true });
                    if (table == null)
                    {
                        problems0.Add("guards and the clerk: the prison has no mailroom and no table in a staff zone to paint one over");
                        return;
                    }
                    foreach (IntVec3 cell in table.OccupiedRect().Cells.ToList())
                    {
                        PrisonZoneDef old = pm.zones.ZoneAt(cell);
                        r.Add(() => pm.zones.Set(cell, old));
                        pm.zones.Set(cell, RPMailDefOf.RP_Mailroom);
                    }
                }
                if (MailSystem.InmateClerkSlots(map) == 0)
                {
                    CompPrisonDoor door = pm.Doors.FirstOrDefault();
                    if (door == null)
                    {
                        problems0.Add("guards and the clerk: the prison has no door to make a mailroom door of");
                        return;
                    }
                    PrisonZoneDef zone0 = door.workZone;
                    r.Add(() => door.workZone = zone0);
                    door.workZone = RPMailDefOf.RP_Mailroom;
                }
                InmateJobDef mailJob = DefDatabase<InmateJobDef>.GetNamed("RP_JobMailroom");
                InmateJobDef job0 = cc.job;
                List<MailLetter> waiting0 = mail.waiting.ToList();
                r.Add(() =>
                {
                    cc.job = job0;
                    mail.waiting.Clear();
                    mail.waiting.AddRange(waiting0);
                });
                mail.waiting.Clear();
                MailLetter letter = mail.Compose(recipient, rc, RPMailDefOf.RP_Mail_Ordinary, hides: false);
                letter.arrivedTick = Find.TickManager.TicksGame;
                mail.waiting.Add(letter);
                cc.job = null;
                bool noClerk = mail.AnyWaiting(map, guard);
                cc.job = mailJob;
                bool freshForGuard = mail.AnyWaiting(map, guard), freshForClerk = mail.AnyWaiting(map, clerk), freshTaken = mail.SortNext(guard);
                letter.arrivedTick -= MailSystem.GuardTakesOverTicks;
                bool agedForGuard = mail.AnyWaiting(map, guard);
                letter.arrivedTick += 1;
                bool justUnderForGuard = !mail.AnyWaiting(map, guard);
                if (!noClerk || freshForGuard || !freshForClerk || freshTaken || !mail.waiting.Contains(letter) || !agedForGuard || !justUnderForGuard)
                    problems0.Add($"guards and the clerk: without a clerk a guard may take the post {noClerk}; with one on duty a guard may take fresh post {freshForGuard} (and did {freshTaken}), the clerk may {freshForClerk}; "
                                  + $"a guard takes post that has waited {MailSystem.GuardTakesOverTicks} ticks {agedForGuard} and not one tick less {justUnderForGuard}");
            });
        }

        /// <summary>
        /// A class that takes the work slot leaves a clerk with post waiting at their table (it took the clerk for the whole shift: 18 letters waited
        /// all morning while the clerk sat in class); a clerk with nothing to sort, and a worker in any other job, may still be taught.
        /// </summary>
        private static void CheckClassLeavesTheClerkTheirPost(Map map)
        {
            Guarded("a class leaves the clerk the post", map, r =>
            {
                PrisonGame g = PrisonGame.Instance;
                MailSystem mail = g.mail;
                List<Pawn> able = map.mapPawns.PrisonersOfColonySpawned.Where(x => InmateUtility.IsActiveInmate(x) && !x.Downed && x.Inmate().activeFlag).Take(2).ToList();
                ProgramDef program = DefDatabase<ProgramDef>.AllDefsListForReading.FirstOrDefault(p => p.Slot == RPLaborDefOf.RP_Work);
                if (able.Count < 2 || program == null)
                {
                    problems0.Add($"class and clerk: needs two active inmates and a program in the work slot, found {able.Count} and {(program == null ? "no program" : "one")}");
                    return;
                }
                (Pawn recipient, Pawn clerk) = (able[0], able[1]);
                (CompInmate rc, CompInmate cc) = (recipient.Inmate(), clerk.Inmate());
                InmateJobDef mailJob = DefDatabase<InmateJobDef>.GetNamed("RP_JobMailroom"), other = DefDatabase<InmateJobDef>.AllDefsListForReading.First(j => j != mailJob);
                InmateJobDef job0 = cc.job;
                List<MailLetter> waiting0 = mail.waiting.ToList();
                r.Add(() =>
                {
                    cc.job = job0;
                    mail.waiting.Clear();
                    mail.waiting.AddRange(waiting0);
                });
                mail.waiting.Clear();
                cc.job = mailJob;
                bool nothingToSort = !ProgramSystem.KeepsToTheirJob(program, clerk, cc);
                mail.waiting.Add(mail.Compose(recipient, rc, RPMailDefOf.RP_Mail_Ordinary, hides: false));
                bool postWaiting = ProgramSystem.KeepsToTheirJob(program, clerk, cc);
                cc.job = other;
                bool otherJob = !ProgramSystem.KeepsToTheirJob(program, clerk, cc);
                cc.job = null;
                bool noJob = !ProgramSystem.KeepsToTheirJob(program, clerk, cc);
                if (!nothingToSort || !postWaiting || !otherJob || !noJob)
                    problems0.Add($"class and clerk: a class may take a clerk with nothing to sort {nothingToSort}, not one with post waiting {postWaiting}; may take a worker in {other.defName} {otherJob} and one with no job {noJob}");
            });
        }
    }
}
