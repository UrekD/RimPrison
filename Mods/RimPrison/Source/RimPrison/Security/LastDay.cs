using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>Where a death-row case stands (saved on <see cref="CompInmate.execStage"/>): nothing yet (the appeals, or an execution date not reached), the date passed and the warden's authority awaited, authorised for a night, executed (the body is in the prison's hands like any death's).</summary>
    public enum ExecStage : byte { None, Awaiting, Authorised, Executed }

    [DefOf]
    public static class RPLastDayDefOf
    {
        public static ThoughtDef RP_LastVisit, RP_ChaplainAtTheDoor, RP_ExecutionDetail;

        static RPLastDayDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPLastDayDefOf));
    }

    /// <summary>
    /// The last day on death row. An execution is no longer automatic once the appeals run out: the case waits for the warden (<see cref="ExecStage.Awaiting"/>, the state fining the prison a little more every day) who authorises a night
    /// (the next 22:00, at least twelve hours ahead) or asks the state for one stay (once: a last hearing in three days at the usual odds, and a reputation cost). From 18:00 of the night the condemned has the last evening at the cell door
    /// (their family, the chaplain, or neither); on the night the guards the warden named (up to three) take them to the table, and a named guard with kin on the row refuses; if nobody has by the third hour any free guard may. The
    /// final action asks again whether it may be done, and the condemned's last words go into the chronicle. The stage is saved, and the evening and the procedure are read off the clock, so a reload never runs one twice.
    /// </summary>
    public static class LastDay
    {
        public const int NightHour = 22, EveningLeadHours = 4, MinLeadHours = 12, MissedAfterHours = 8, FallbackHours = 3, DetailMax = 3, StayHearingDays = 3, LastWordsPerKind = 3;
        public const float StayReputation = 3f;

        private static int Now => Find.TickManager.TicksGame;

        // ---- the night ------------------------------------------------------------------------------------------------------------------------

        /// <summary>The tick of the next <see cref="NightHour"/>:00 on this map's clock that is at least <paramref name="minLeadHours"/> away (whole ticks, so the hour of the night is exactly that hour).</summary>
        public static int NextNight(Map map, int now, int minLeadHours)
        {
            long local = Find.TickManager.TicksAbs + GenDate.LocalTicksOffsetFromLongitude(Find.WorldGrid.LongLatOf(map.Tile).x);
            long intoDay = (local % GenDate.TicksPerDay + GenDate.TicksPerDay) % GenDate.TicksPerDay;
            long ahead = (NightHour * (long)GenDate.TicksPerHour - intoDay + GenDate.TicksPerDay) % GenDate.TicksPerDay;
            if (ahead < minLeadHours * (long)GenDate.TicksPerHour) ahead += GenDate.TicksPerDay;
            return now + (int)ahead;
        }

        private static Map MapOf(Pawn p) => p.MapHeld ?? Find.AnyPlayerHomeMap;

        /// <summary>The case is open to the warden's hand: alive, on the map, in custody, still sentenced to death. For the buttons only: never a test of the procedure (it needs <c>activeFlag</c>, which is false for a pawn nobody sees spawned: use <see cref="InCustody"/>).</summary>
        private static bool Open(Pawn p, out CompInmate c, out DeathRowExtension ext)
        {
            c = p?.Inmate();
            ext = c == null ? null : DeathRowSystem.Ext(c);
            return ext != null && p is { Dead: false, Destroyed: false, Spawned: true } && p.IsPrisonerOfColony && c.activeFlag && p.guest is { Released: false } && !ShuttleSystem.OnTheWayOut(p);
        }

        // ---- the warden's two answers ---------------------------------------------------------------------------------------------------------

        /// <summary>Why the warden cannot authorise now (null: they can).</summary>
        public static string AuthoriseWhy(Pawn p) =>
            !Open(p, out CompInmate c, out _) ? "RP_Last_NotOpen".Translate().Resolve() : c.execStage != ExecStage.Awaiting ? "RP_Last_NotAwaiting".Translate().Resolve() : null;

        /// <summary>Authorises the execution for the next night (at least <see cref="MinLeadHours"/> ahead). False when the case is not awaiting (the answer is checked again here: a letter or a button can be stale).</summary>
        public static bool TryAuthorise(Pawn p)
        {
            if (AuthoriseWhy(p) != null) return false;
            CompInmate c = p.Inmate();
            int now = Now;
            c.execStage = ExecStage.Authorised;
            c.executionTick = NextNight(MapOf(p), now, MinLeadHours);
            c.lastEveningDone = false;
            c.lastOverdueTick = now;
            float hours = (c.executionTick - now) / (float)GenDate.TicksPerHour;
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_Authorised".Translate(p.LabelShort, hours.ToString("0")), p);
            Messages.Message("RP_Authorised".Translate(p.LabelShort, hours.ToString("0")), p, MessageTypeDefOf.NeutralEvent);
            return true;
        }

        /// <summary>Why the warden cannot ask for a stay now (null: they can): once per case, before the night begins.</summary>
        public static string StayWhy(Pawn p)
        {
            if (!Open(p, out CompInmate c, out _)) return "RP_Last_NotOpen".Translate().Resolve();
            if (c.stayUsed) return "RP_Last_StayUsed".Translate().Resolve();
            if (c.execStage == ExecStage.Awaiting || c.execStage == ExecStage.Authorised && Now < c.executionTick) return null;
            return "RP_Last_StayTooLate".Translate().Resolve();
        }

        /// <summary>
        /// Asks the state for the one stay: the night is cleared, the reputation with the state pays three points, and one more hearing is held in three days at the usual odds (a win commutes, a loss brings the case back to
        /// <see cref="ExecStage.Awaiting"/>, see <see cref="StayLost"/>).
        /// </summary>
        public static bool TryStay(Pawn p)
        {
            if (StayWhy(p) != null) return false;
            CompInmate c = p.Inmate();
            c.stayUsed = true;
            c.execStage = ExecStage.None;
            c.executionTick = -1;
            c.appealsLeft = 1;
            c.nextAppealTick = Now + StayHearingDays * GenDate.TicksPerDay;
            PrisonGame game = PrisonGame.Instance;
            game.reputation = Mathf.Max(0f, game.reputation - StayReputation);
            DeathRowSystem.RemoveRestraint(p);
            Story.Record(RPChronicleDefOf.RP_Chr_Decisions, 2, "RP_Chr_StayAsked".Translate(p.LabelShort, StayHearingDays), p);
            Messages.Message("RP_StayAsked".Translate(p.LabelShort, StayHearingDays), p, MessageTypeDefOf.NeutralEvent);
            return true;
        }

        /// <summary>The hearing a stay won was lost: the case is back with the warden, and the fine starts over.</summary>
        public static void StayLost(Pawn p, CompInmate c)
        {
            int now = Now;
            c.execStage = ExecStage.Awaiting;
            c.executionTick = now;
            c.lastOverdueTick = now;
            c.tension = Mathf.Min(100f, c.tension + 10f);
            Find.LetterStack.ReceiveLetter("RP_StayLostLabel".Translate(), "RP_StayLostText".Translate(p.LabelShort), LetterDefOf.NegativeEvent, p);
        }

        // ---- the hourly pass ------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// One condemned, once an hour (their execution date is set and any plea answered): the date reached enters <see cref="ExecStage.Awaiting"/> (a guard carrying them out already, as an old save may have, is the night itself); a case waiting is fined; an authorised one
        /// has its evening from 18:00, its detail kept to the guards who can still stand it, and a night nobody carried out by 06:00 moved to the next.
        /// </summary>
        public static void Hourly(Map map, Pawn p, CompInmate c, DeathRowExtension ext, int now)
        {
            switch (c.execStage)
            {
                case ExecStage.None:
                    if (now < c.executionTick || c.clemencyResolveTick >= 0) return;
                    if (HoldsCarry(map, p)) BeginLegacy(c, now);
                    else EnterAwaiting(p, c, now);
                    return;
                case ExecStage.Awaiting:
                    PruneDetail(map, c);
                    Fine(p, c, ext, now);
                    return;
                case ExecStage.Authorised:
                    PruneDetail(map, c);
                    if (now >= c.executionTick) RecordRefusals(map, p, c);
                    if (!c.lastEveningDone && now >= c.executionTick - EveningLeadHours * GenDate.TicksPerHour) Evening(map, p, c);
                    if (now >= c.executionTick + MissedAfterHours * GenDate.TicksPerHour && !HoldsCarry(map, p)) MissNight(map, p, c, now);
                    return;
            }
        }

        /// <summary>A guard has this one in a carry-out job (an old save's procedure under way, or this one's).</summary>
        private static bool HoldsCarry(Map map, Pawn p)
        {
            IReadOnlyList<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
                if (colonists[i].CurJobDef == RPDeathRowDefOf.RP_CarryOutExecution && colonists[i].CurJob.targetA.Thing == p) return true;
            return false;
        }

        /// <summary>An old save's procedure under way is finished as it was: the night is now, and the evening is not held.</summary>
        private static void BeginLegacy(CompInmate c, int now)
        {
            c.execStage = ExecStage.Authorised;
            c.executionTick = now;
            c.lastEveningDone = true;
        }

        private static void EnterAwaiting(Pawn p, CompInmate c, int now)
        {
            c.execStage = ExecStage.Awaiting;
            // (While the case waits, the date is the day it began to wait: the fine climbs from it, so an old save's case or a recaptured one starts at the first day's fine.)
            c.executionTick = now;
            c.lastOverdueTick = now;
            // (An old save's inmate may be in the restraint the old pass put on them for a guard who never came.)
            DeathRowSystem.RemoveRestraint(p);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, "RP_Chr_AwaitingAuthority".Translate(p.LabelShort), p);
            Find.LetterStack.ReceiveLetter("RP_AwaitingLabel".Translate(), "RP_AwaitingText".Translate(p.LabelShort, Money.Format(DeathRowSystem.Ext(c).overduePenaltyPerDay)), LetterDefOf.NegativeEvent, p);
        }

        /// <summary>What the state fines the prison for the day the case is awaited: the first day's fine, and as much more for every day it has waited, at most <see cref="DeathRowExtension.overdueDaysMax"/> times it (<see cref="CompInmate.executionTick"/> is the day the waiting began).</summary>
        public static int FineToday(CompInmate c, DeathRowExtension ext, int now) =>
            ext.overduePenaltyPerDay * Mathf.Clamp((now - c.executionTick) / GenDate.TicksPerDay, 1, ext.overdueDaysMax);

        private static void Fine(Pawn p, CompInmate c, DeathRowExtension ext, int now)
        {
            if (now - c.lastOverdueTick < GenDate.TicksPerDay) return;
            c.lastOverdueTick = now;
            PrisonGame.Instance.ledger.Add(RPDefOf.RP_Fines, -FineToday(c, ext, now), "RP_ExecutionOverdueNote".Translate(p.LabelShort));
        }

        /// <summary>The night went by with nobody to carry it out (they could not be restrained, or no guard took it): the next 22:00.</summary>
        private static void MissNight(Map map, Pawn p, CompInmate c, int now)
        {
            bool restrained = p.health.hediffSet.HasHediff(RPSecurityDefOf.RP_Restrained);
            // (No table is free: a body lies on the one there is, or there is none - that, not the condemned, is what kept the night.)
            string why = DeathRowSystem.BodyOnTable(map) != null ? "RP_NightMissed_BodyOnTable" : DeathRowSystem.FindTable(map, null) == null ? "RP_NightMissed_NoTable" : restrained ? "RP_NightMissed_NoGuard" : "RP_NightMissed_NotRestrained";
            c.executionTick = NextNight(map, now, 0);
            DeathRowSystem.RemoveRestraint(p);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, "RP_Chr_NightMissed".Translate(p.LabelShort, why.Translate()), p);
        }

        // ---- the last evening -----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The last evening, at the cell door like the hospice chaplain's visits: their family, if they have family the prison knows (a memory, calmer, a line), and the chaplain, if there is one on the staff (the same). Neither: skipped, and said.
        /// </summary>
        private static void Evening(Map map, Pawn p, CompInmate c)
        {
            c.lastEveningDone = true;
            string name = p.LabelShort;
            bool any = false;
            if (Relatives.Of(c).Find(r => r.CanVisit) is { } relative)
            {
                any = true;
                p.needs?.mood?.thoughts.memories.TryGainMemory(RPLastDayDefOf.RP_LastVisit);
                c.tension = Mathf.Max(0f, c.tension - 15f);
                Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, "RP_Chr_LastVisit".Translate(name, relative.Label), p);
            }
            if (StaffCommute.AllStaff(map).Any(s => s.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Chaplain))
            {
                any = true;
                p.needs?.mood?.thoughts.memories.TryGainMemory(RPLastDayDefOf.RP_ChaplainAtTheDoor);
                c.tension = Mathf.Max(0f, c.tension - 10f);
                if (FaithUtility.Need(p) is { } faith) faith.CurLevel = Mathf.Min(1f, faith.CurLevel + 0.2f);
                Story.Record(RPChronicleDefOf.RP_Chr_Death, 1, "RP_Chr_LastChaplain".Translate(name), p);
            }
            if (!any) Story.Record(RPChronicleDefOf.RP_Chr_Death, 1, "RP_Chr_LastEveningAlone".Translate(name), p);
        }

        // ---- the detail -----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The guards the warden may name for this one: every guard employed (on shift or off it, at home or on the floor), not on leave or let go and not kept off the condemned's wing by kin of their own. A guard off shift at the time of naming is
        /// still named; the night finds whoever is on.
        /// </summary>
        public static List<Pawn> Eligible(Map map, CompInmate c)
        {
            var list = new List<Pawn>();
            foreach (Pawn g in StaffCommute.AllStaff(map))
                if (g.GetComp<CompStaff>() is { leaveAtTick: < 0 } sc && !StaffLives.OnLeave(sc) && GuardUtility.IsGuard(g) && !(Kin.KeptOffWings(g)?.Contains(c.security) == true)) list.Add(g);
            return list;
        }

        /// <summary>The guards named for this one who are still employed here (on site or at home), in the order they were named.</summary>
        public static List<Pawn> Named(Map map, CompInmate c)
        {
            var list = new List<Pawn>();
            if (c.detailIds.Count == 0) return list;
            List<Pawn> staff = StaffCommute.AllStaff(map);
            foreach (int id in c.detailIds)
                if (staff.Find(x => x.thingIDNumber == id) is { } g) list.Add(g);
            return list;
        }

        /// <summary>Names a guard for the detail (up to <see cref="DetailMax"/>). False when it is full, they are named already or the case is not one the warden's hand is on.</summary>
        public static bool TryName(Pawn condemned, Pawn guard)
        {
            if (!Open(condemned, out CompInmate c, out _) || c.execStage is not (ExecStage.Awaiting or ExecStage.Authorised) || c.detailIds.Count >= DetailMax || c.detailIds.Contains(guard.thingIDNumber)) return false;
            if (!Eligible(MapOf(condemned), c).Contains(guard)) return false;
            c.detailIds.Add(guard.thingIDNumber);
            return true;
        }

        /// <summary>Takes a guard off the names. A refusal stays: the guard who refused this case never takes it, named or not.</summary>
        public static void Unname(CompInmate c, Pawn guard) => c.detailIds.Remove(guard.thingIDNumber);

        /// <summary>A guard who left the detail (let go, dead, on leave, no longer a guard, or kept off the wing now) is dropped from the names, hourly. (Off shift is not leaving it. Their refusals stay with the case.)</summary>
        private static void PruneDetail(Map map, CompInmate c)
        {
            if (c.detailIds.Count == 0) return;
            List<Pawn> eligible = Eligible(map, c);
            for (int i = c.detailIds.Count - 1; i >= 0; i--)
            {
                int id = c.detailIds[i];
                if (!eligible.Exists(g => g.thingIDNumber == id)) c.detailIds.RemoveAt(i);
            }
        }

        /// <summary>A guard with kin on the row will not be the one who takes them to the table (their own, or another condemned's: they would have been asked to carry out their own family's sentence, or a colleague's).</summary>
        public static bool Refuses(Pawn guard)
        {
            foreach (Pawn kin in Kin.InmatesOf(guard))
                if (kin.Inmate() is { } kc && DeathRowSystem.Ext(kc) != null) return true;
            return false;
        }

        /// <summary>The third hour of the night with nobody having taken them: any free guard may.</summary>
        private static bool Fallback(CompInmate c) => Now >= c.executionTick + FallbackHours * GenDate.TicksPerHour;

        /// <summary>
        /// May this guard be offered the carry (asked by the work giver, for a condemned whose night it is; writes nothing): never one who refused this case, and never one with kin on the row (the condemned's own, or another's: asked of every
        /// guard, named or not, the fallback included - the work giver's offer and its job are one answer, or vanilla's scanner logs an error); then anyone free when the warden named nobody, else a named guard and, from the third hour of the
        /// night, any free guard.
        /// </summary>
        public static bool MayTake(Pawn guard, CompInmate c)
        {
            int id = guard.thingIDNumber;
            if (c.detailRefused.Contains(id) || Refuses(guard)) return false;
            return c.detailIds.Count == 0 || c.detailIds.Contains(id) || Fallback(c);
        }

        /// <summary>
        /// The night's hourly pass: each guard the warden named who has kin on the row refuses, once - one line in the chronicle, kept for the case (they are never asked again, whoever is named after; the slot falls to a free guard at the third hour). This is
        /// where the refusal is decided, ahead of the work giver, which never offers them the carry (<see cref="MayTake"/>).
        /// </summary>
        private static void RecordRefusals(Map map, Pawn condemned, CompInmate c)
        {
            if (c.detailIds.Count == 0) return;
            foreach (Pawn guard in Named(map, c))
            {
                if (c.detailRefused.Contains(guard.thingIDNumber) || !Refuses(guard)) continue;
                c.detailRefused.Add(guard.thingIDNumber);
                Story.Record(RPChronicleDefOf.RP_Chr_Staff, 2, "RP_Chr_DetailRefused".Translate(Kin.Mention(guard), condemned.LabelShort), guard, condemned);
            }
        }

        // ---- the final action -----------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Still the prison's prisoner, on the prison's map or in a guard's arms on it: asked of the pawn held, never of <see cref="CompInmate.activeFlag"/> (a cache of the regime's, unsaved, that is false for a pawn nobody sees spawned: the one carried to the table, or just
        /// loaded in a guard's arms).
        /// </summary>
        public static bool InCustody(Pawn p) =>
            p.IsPrisonerOfColony && p.guest is { Released: false } && p.MapHeld is { IsPlayerHome: true } && !ShuttleSystem.OnTheWayOut(p) && !PrisonBreakUtility.IsPrisonBreaking(p);

        /// <summary>Why the sentence cannot be carried out right now (null: it can). Asked all the way and again at the last moment (and so the first thing an old save's carry meets: see below).</summary>
        public static string Blocker(Pawn inmate, Building table, bool onTable)
        {
            CompInmate c = inmate?.Inmate();
            if (c == null || inmate.Dead || inmate.Destroyed) return "RP_ExecStop_Gone".Translate().Resolve();
            if (DeathRowSystem.Ext(c) == null) return "RP_ExecStop_NotSentenced".Translate().Resolve();
            // An old save's carry already under way, before the hourly pass has met the case (only an old save has a carry at stage none with a date reached): it goes on as the night.
            if (c.execStage == ExecStage.None && c.executionTick >= 0 && Now >= c.executionTick && c.clemencyResolveTick < 0 && InCustody(inmate)) BeginLegacy(c, Now);
            if (c.execStage != ExecStage.Authorised) return "RP_ExecStop_NotAuthorised".Translate().Resolve();
            if (Now < c.executionTick) return "RP_ExecStop_NotYet".Translate().Resolve();
            if (c.clemencyResolveTick >= 0) return "RP_ExecStop_Clemency".Translate().Resolve();
            if (!InCustody(inmate)) return "RP_ExecStop_NotInCustody".Translate().Resolve();
            if (table == null || table.Destroyed || !table.Spawned) return "RP_ExecStop_NoTable".Translate().Resolve();
            if (onTable && (!inmate.Spawned || inmate.Position != table.Position)) return "RP_ExecStop_NotOnTable".Translate().Resolve();
            return null;
        }

        /// <summary>The procedure is stopped: the restraint comes off (the regime walks them back), and the chronicle says why.</summary>
        public static void Stopped(Pawn inmate, Pawn guard, string why)
        {
            if (inmate == null) return;
            DeathRowSystem.RemoveRestraint(inmate);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, "RP_Chr_ExecutionStopped".Translate(inmate.LabelShort, why), inmate);
        }

        /// <summary>The last action of the carry-out job: asks <see cref="Blocker"/> once more, and if nothing stands in the way gives the guards of the procedure their memory and carries out the sentence. False: stopped, and said.</summary>
        public static bool TryExecute(Pawn inmate, Pawn executioner, Building table)
        {
            if (Blocker(inmate, table, onTable: true) is { } why)
            {
                Stopped(inmate, executioner, why);
                return false;
            }
            CompInmate c = inmate.Inmate();
            Map map = inmate.Map;
            // The guard who did it and every guard the warden named who is on site, at any distance (a named guard at home or off duty that night was not there).
            foreach (Pawn g in StaffLives.OnSite(map))
                if (g == executioner || c.detailIds.Contains(g.thingIDNumber) && !c.detailRefused.Contains(g.thingIDNumber))
                    g.needs?.mood?.thoughts.memories.TryGainMemory(RPLastDayDefOf.RP_ExecutionDetail);
            DeathRowSystem.Execute(inmate, executioner);
            return true;
        }

        /// <summary>
        /// The executioner's next job once the sentence is carried out: the body off the table at once, to a free morgue cell they can reach, else a standable cell beside the table (the staff take it from there like any body, the corpse
        /// having been allowed by <see cref="DeathRowSystem.Execute"/>). Null: nothing to carry (no body, or nowhere to put it - the table then shows as holding a body, see <see cref="DeathRowSystem.BodyOnTable"/>).
        /// </summary>
        public static Job BodyJob(Pawn executioner, Corpse body, Building table)
        {
            if (body is not { Spawned: true } || table is not { Spawned: true } || PrisonMap.Of(body.Map) is not { } pm) return null;
            IntVec3 cell = Morgue.FreeCell(executioner, pm);
            if (!cell.IsValid)
                foreach (IntVec3 beside in GenAdj.CellsAdjacent8Way(table))
                    if (beside.InBounds(body.Map) && beside.Standable(body.Map) && beside.GetDoor(body.Map) == null && beside.GetFirstThing<Corpse>(body.Map) == null && executioner.CanReserveAndReach(beside, PathEndMode.OnCell, Danger.Some))
                    {
                        cell = beside;
                        break;
                    }
            if (!cell.IsValid) return null;
            Job job = JobMaker.MakeJob(RPMorgueDefOf.RP_HaulToMorgue, body, cell);
            job.count = 1;
            return job;
        }

        // ---- last words -----------------------------------------------------------------------------------------------------------------------

        /// <summary>A line from a set picked by the condemned's traits and what they did with the appeals (never by whether they were guilty: that is not on any file).</summary>
        private static string WordsKind(Pawn p, CompInmate c)
        {
            TraitSet traits = p.story?.traits;
            if (traits != null && (traits.HasTrait(TraitDefOf.Psychopath) || traits.HasTrait(TraitDefOf.Bloodlust))) return "RP_LastWords_Cold";
            if (c.stayUsed) return "RP_LastWords_Appeals";
            if (c.religious) return "RP_LastWords_Faith";
            if (traits != null && traits.HasTrait(TraitDefOf.Kind)) return "RP_LastWords_Regret";
            return "RP_LastWords_Plain";
        }

        /// <summary>The key of the last words this condemned would say (one of <see cref="LastWordsPerKind"/> of their kind, picked by who they are, so it is the same line however often it is asked).</summary>
        public static string WordsKey(Pawn p, CompInmate c) => WordsKind(p, c) + "_" + p.thingIDNumber % LastWordsPerKind;

        public static void LastWords(Pawn p, CompInmate c) =>
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, "RP_Chr_LastWords".Translate(p.LabelShort, WordsKey(p, c).Translate()), p);

        // ---- back from the dead ---------------------------------------------------------------------------------------------------------------

        /// <summary>An executed one who comes back to life (a resurrector serum) is back with the warden, the one stay used.</summary>
        public static void Notify_Resurrected(Pawn p)
        {
            if (PrisonGame.Instance == null || p.Inmate() is not { execStage: ExecStage.Executed } c) return;
            int now = Now;
            DeathRowSystem.ClearCase(c);
            c.executing = false;
            c.stayUsed = true;
            c.execStage = ExecStage.Awaiting;
            c.executionTick = now;
            c.lastOverdueTick = now;
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, "RP_Chr_ExecutedReturned".Translate(p.LabelShort), p);
        }

        // ---- what the warden reads ------------------------------------------------------------------------------------------------------------

        /// <summary>The case as one comparable string (the saveload plan's snapshot and the checks' messages).</summary>
        public static string Describe(CompInmate c) =>
            $"{c.execStage}/{c.executionTick}/{c.stayUsed}/{c.lastEveningDone}/{c.exposedWrongful}/{string.Join("+", c.detailIds)}/{string.Join("+", c.detailRefused)}";

        /// <summary>The detail as the file says it: who is named (a refusal marked), or that nobody is and any free guard will do.</summary>
        public static string DetailText(Map map, CompInmate c)
        {
            if (c.detailIds.Count == 0) return "RP_Last_DetailNone".Translate().Resolve();
            List<Pawn> staff = map == null ? null : StaffCommute.AllStaff(map);
            IEnumerable<string> names = c.detailIds.Select(id => (staff?.Find(x => x.thingIDNumber == id) is { } g ? g.LabelShort + (g.Spawned ? "" : " " + "RP_Last_OffSite".Translate().Resolve()) : "?") + (c.detailRefused.Contains(id) ? " " + "RP_Last_Refuses".Translate().Resolve() : ""));
            return "RP_Last_Detail".Translate(string.Join(", ", names)).Resolve();
        }

        /// <summary>The line on the file and in the alerts for a case the warden has to act on (null: none).</summary>
        public static string Line(CompInmate c, DeathRowExtension ext)
        {
            int now = Now;
            if (c.execStage == ExecStage.Awaiting) return "RP_Last_LineAwaiting".Translate(Money.Format(FineToday(c, ext, now))).Resolve();
            if (c.execStage == ExecStage.Authorised && now < c.executionTick) return "RP_Last_LineSet".Translate(((c.executionTick - now) / (float)GenDate.TicksPerHour).ToString("0")).Resolve();
            return null;
        }
    }

    /// <summary>An executed one who comes back to life is not the case they were: see <see cref="LastDay.Notify_Resurrected"/>.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.Notify_Resurrected))]
    public static class Patch_Pawn_HealthTracker_Resurrected_LastDay
    {
        public static void Postfix(Pawn ___pawn) => LastDay.Notify_Resurrected(___pawn);
    }
}
