using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPrison
{
    [DefOf]
    public static class RPFuneralDefOf
    {
        public static JobDef RP_AttendFuneral;
        public static JobDef RP_LeadFuneral;
        public static ThoughtDef RP_SaidGoodbye;
        public static ThoughtDef RP_StaffFarewell;

        static RPFuneralDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPFuneralDefOf));
    }

    public enum FuneralState : byte
    {
        /// <summary>The letter is out (or waits for the day's budget): the burial waits for the answer.</summary>
        Asked,
        /// <summary>A quiet burial: the answer, or the lapse of one. Kept until the body is gone.</summary>
        Quiet,
        /// <summary>A funeral chosen, waiting for its hour (and for the lockdown or the riot to end, and for a guard to spare).</summary>
        Waiting,
        Gathering,
        /// <summary>Held (or broken off): kept to the end of the day, and the turf pass leaves its mourners out until the hour has turned.</summary>
        Done,
    }

    public enum FuneralPlace : byte
    {
        Grave,
        Chapel,
        Pad,
    }

    /// <summary>
    /// One of the prison's dead and the funeral that goes with them (see <see cref="Funerals"/>): who, the state it is in, where, when, who was sent to it. The record is the letter's answer, kept as long as the body lies in the morgue
    /// and for the day of the funeral; it is not the chronicle's.
    /// </summary>
    public class Funeral : IExposable
    {
        public int deadId = -1;
        public string name;
        /// <summary>One of the staff: their colleagues attend, and the warden speaks.</summary>
        public bool staff;
        public FuneralState state;
        public FuneralPlace place;
        /// <summary>The grave's south end, or the middle of the chapel, or the pad's cell: where the mourners stand round.</summary>
        public IntVec3 cell = IntVec3.Invalid;
        /// <summary>When the letter was sent, the hour chosen (the tick it starts at: the pass of that hour holds it), when it began and when it ends.</summary>
        public int askedTick, holdTick = -1, startTick = -1, endTick = -1;
        /// <summary>Everybody sent to it, by thing id: the mourners, the escorting guards, the colleagues, the chaplain and the one who speaks (and a rival who came to make trouble).</summary>
        public List<int> attendees = new List<int>();
        public int mourners;
        public bool chaplain, spoke, buried;
        /// <summary>What the record keeps of the dead one (their file goes with the body, which the shuttle may take before the service): their wing, their gang, who was to mourn them when the body was laid in the morgue (by id, the closest first) and which relative would come from outside (name and kind).</summary>
        public SecurityLevelDef level;
        public GangDef gang;
        public List<int> invited = new List<int>();
        public string kinName, kinKind;
        /// <summary>A rival of the killer's gang, who came to the service; whether the fight was rolled.</summary>
        public int stirrerId = -1;
        public bool scuffled;
        /// <summary>A relative of the dead who came from outside for the hour (the label), and the visitors as pawns.</summary>
        public string relative;
        public List<Pawn> visitors = new List<Pawn>();

        public bool Attends(int id) => attendees.Contains(id);

        public void ExposeData()
        {
            Scribe_Values.Look(ref deadId, "deadId", -1);
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref staff, "staff");
            Scribe_Values.Look(ref state, "state");
            Scribe_Values.Look(ref place, "place");
            Scribe_Values.Look(ref cell, "cell", IntVec3.Invalid);
            Scribe_Values.Look(ref askedTick, "asked");
            Scribe_Values.Look(ref holdTick, "hold", -1);
            Scribe_Values.Look(ref startTick, "start", -1);
            Scribe_Values.Look(ref endTick, "end", -1);
            Scribe_Collections.Look(ref attendees, "attendees", LookMode.Value);
            Scribe_Values.Look(ref mourners, "mourners");
            Scribe_Values.Look(ref chaplain, "chaplain");
            Scribe_Values.Look(ref spoke, "spoke");
            Scribe_Values.Look(ref buried, "buried");
            Scribe_Defs.Look(ref level, "level");
            Scribe_Defs.Look(ref gang, "gang");
            Scribe_Collections.Look(ref invited, "invited", LookMode.Value);
            Scribe_Values.Look(ref kinName, "kinName");
            Scribe_Values.Look(ref kinKind, "kinKind");
            Scribe_Values.Look(ref stirrerId, "stirrerId", -1);
            Scribe_Values.Look(ref scuffled, "scuffled");
            Scribe_Values.Look(ref relative, "relative");
            if (Scribe.mode == LoadSaveMode.Saving) visitors?.RemoveAll(x => x == null || x.Destroyed);
            Scribe_Collections.Look(ref visitors, "visitors", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                attendees ??= new List<int>();
                invited ??= new List<int>();
                visitors ??= new List<Pawn>();
                visitors.RemoveAll(x => x == null);
            }
        }

        public string Describe() => $"{deadId}:{name}:{staff}:{state}:{place}:{cell}:{askedTick}:{holdTick}:{startTick}:{endTick}:{string.Join("+", attendees)}:{mourners}:{chaplain}{spoke}{buried}:{stirrerId}:{scuffled}:{relative}:{visitors.Count}:{level?.defName}:{gang?.defName}:{string.Join("+", invited)}:{kinName}/{kinKind}";
    }

    /// <summary>
    /// The funeral: when a body of the prison's own is laid in the morgue the warden is asked whether it gets a funeral or a quiet burial (the answer a letter left unanswered gives, and today's way). A funeral is held at the next
    /// free-time or work hour of the dead's regime (not at night; not in a lockdown or a riot, when it waits up to a day and then lapses to a quiet burial) at the grave in the plot, or - for the dead who go home on the shuttle, and for an
    /// inmate whose mourners cannot walk into the plot, which is staff-only - in the chapel, or at the shuttle pad when there is none. Who comes: the chaplain, the dead inmate's friends (a friend inside, or an opinion of them past
    /// <see cref="FriendOpinion"/>) and gang mates, let out of the regime for the hour as a class is (kept in, in the hole, in supermax or in protective custody they do not come; two the warden keeps apart do not both), the guards who
    /// escort them (busy for the hour), and the dead's relatives as visitors if the visitation room can bring them. A member of staff is seen off by the colleagues on duty and not needed elsewhere, and the warden speaks. It ends in a
    /// "said goodbye" thought for the mourners, tension off for them and for the wing (or a morale thought for the staff), and the gathering is left out of the turf count (<see cref="Away"/>); a rival of the killer's gang on the wing
    /// may turn up and start a fight (the pastimes' scuffle). The burial waits for the answer and for the hour, so that the grave is dug with them standing round it.
    /// </summary>
    public static class Funerals
    {
        /// <summary>How long a service lasts (under an hour: it is over before the next hour's passes), and how long past its end the turf count still leaves its mourners out (the pass at the turn of the hour).</summary>
        public const int GatherTicks = 2000, AwayGraceTicks = 1000;
        /// <summary>A funeral that cannot be held (a lockdown that does not end, no guard to spare) lapses to a quiet burial a day after its hour; a letter that nobody answered is a quiet burial in a day.</summary>
        public const int LapseTicks = GenDate.TicksPerDay, AnswerTicks = GenDate.TicksPerDay;
        /// <summary>A letter is a quiet burial a day after it was written, whether or not it was delivered (a deferred letter does not hold the body for the days it waits: a body in the morgue rots in about two and a half), and a quiet burial's record is kept this long for a body that is still there.</summary>
        public const int AskedMaxTicks = AnswerTicks, RecordMaxTicks = 7 * GenDate.TicksPerDay;
        /// <summary>An inmate whose opinion of the dead is this high mourns them, friend or not.</summary>
        public const int FriendOpinion = 20;
        public const int MaxMourners = 16, MaxColleagues = 10, EscortRatio = 5;
        /// <summary>The cells round the middle that the mourners stand on.</summary>
        public const float StandRadius = 4.9f;
        /// <summary>The chance that a rival of the killer's gang on the dead's wing turns up and starts something.</summary>
        public const float ScuffleChance = 0.25f;
        /// <summary>Tension off for a mourner (with the chaplain's service), for the rest of the wing.</summary>
        public const float MournerTension = 10f, ServiceTension = 15f, WingTension = 4f;
        /// <summary>A service's end: a mourner counts as there when they are this near the middle.</summary>
        public const float PresentWithin = 11f;

        /// <summary>The checks turn the funeral on (an autotest run keeps the burial as it was, like the decision budget: <see cref="StoryLetters.TestBudget"/>).</summary>
        public static bool TestOn;

        /// <summary>For a check: the roll the rival's turning up is decided by (null: a real roll).</summary>
        internal static float? ScuffleRollForTests;

        /// <summary>For a check: only these pawns are anyone's mourners, colleagues, escorts or chaplain (null: everybody; a check's scene is not the real prison's staff and inmates).</summary>
        internal static System.Func<Pawn, bool> CastForTests;

        /// <summary>For a check: whether an enemy is on the map (null: the game's own answer; the check's map has the events plan's raiders on it, and they are nobody's here).</summary>
        internal static bool? ThreatForTests;

        /// <summary>For a check: who can walk to a cell (null: the pathing's own answer).</summary>
        internal static System.Func<Pawn, IntVec3, bool> ReachForTests;

        public static bool Active => PrisonGame.Instance != null && (!AutoTest.Active || TestOn);

        private static List<Funeral> All => PrisonGame.Instance.story.funerals;

        public static Funeral Of(int deadId)
        {
            List<Funeral> list = PrisonGame.Instance?.story.funerals;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
                if (list[i].deadId == deadId) return list[i];
            return null;
        }

        public static Funeral Of(Pawn dead) => dead == null ? null : Of(dead.thingIDNumber);

        /// <summary>The funeral under way that this pawn was sent to.</summary>
        public static Funeral GatheringFor(Pawn p)
        {
            List<Funeral> list = PrisonGame.Instance?.story.funerals;
            if (list == null || list.Count == 0) return null;
            for (int i = 0; i < list.Count; i++)
                if (list[i].state == FuneralState.Gathering && list[i].Attends(p.thingIDNumber)) return list[i];
            return null;
        }

        /// <summary>The pawn is at a funeral, or was within the turn of the hour after it: the turf count does not see them (a gang's members in one patch for the hour must not tip it).</summary>
        public static bool Away(Pawn p)
        {
            List<Funeral> list = PrisonGame.Instance?.story.funerals;
            if (list == null || list.Count == 0) return false;
            int now = Find.TickManager.TicksGame;
            for (int i = 0; i < list.Count; i++)
                if (list[i].state is FuneralState.Gathering or FuneralState.Done && list[i].startTick >= 0 && now <= list[i].endTick + AwayGraceTicks && list[i].Attends(p.thingIDNumber)) return true;
            return false;
        }

        // ---- the letter ---------------------------------------------------------------------------------------------------------------------

        /// <summary>A body of the prison's own has been laid in the morgue (by the job that carries it there): the warden is asked what it gets.</summary>
        public static void Notify_Laid(Corpse c)
        {
            if (!Active || c?.InnerPawn == null || !c.Spawned || PrisonMap.Of(c.Map) is not { } pm || !Morgue.Ours(c) || !Mournable(c.InnerPawn) || !Morgue.InMorgue(c, pm) || Of(c.InnerPawn) != null) return;
            Ask(PrisonGame.Instance, c.Map, pm, c);
        }

        /// <summary>One of the prison's own people: an inmate or one of the staff (the morgue takes a visitor's body too, and no funeral is asked for them).</summary>
        private static bool Mournable(Pawn dead) => IsStaff(dead) || dead.Inmate() is { security: not null };

        /// <summary>Whether there is a place to hold a funeral for this body: the grave (the plot will take it), the chapel or the shuttle pad.</summary>
        public static bool HasPlace(Map map, PrisonMap pm, Corpse body) => body != null && Burial.Wanted(body, pm) || HasVenue(map, pm);

        internal static bool HasVenue(Map map, PrisonMap pm) => pm.zones.Any(RPFaithDefOf.RP_Chapel) || ShuttleSystem.HasPad(map);

        /// <summary>An inmate is dying (the kill hook, before <see cref="StorySystem.Notify_Leaving"/> ends the warden's orders with them): the ones the warden kept apart from them are kept for the letter, which comes when the body is laid in the morgue - a feud partner at the grave is the trouble the order was for.</summary>
        public static void Notify_Dying(Pawn p)
        {
            if (!Active || p.Inmate() is not { security: not null } || KeepApart.Book is not { orders.Count: > 0 } book || book.OrdersOf(p.thingIDNumber) is not { Count: > 0 } orders) return;
            List<int> pairs = PrisonGame.Instance.story.funeralApart;
            foreach (KeepApartOrder o in orders)
            {
                pairs.Add(p.thingIDNumber);
                pairs.Add(o.OtherId(p.thingIDNumber));
                pairs.Add(Find.TickManager.TicksGame);
            }
        }

        /// <summary>The ids of those kept apart from the dead one at their death, and the note is done with.</summary>
        private static HashSet<int> TakeApart(PrisonGame game, int deadId)
        {
            var apart = new HashSet<int>();
            List<int> pairs = game.story.funeralApart;
            for (int i = pairs.Count - 3; i >= 0; i -= 3)
            {
                if (pairs[i] != deadId) continue;
                apart.Add(pairs[i + 1]);
                pairs.RemoveRange(i, 3);
            }
            return apart;
        }

        /// <summary>Hourly: the note of a dead one whose body was never laid in the morgue (a body is laid within hours, or the guards never come for it) is done with after <see cref="RecordMaxTicks"/>.</summary>
        private static void ForgetApart(PrisonGame game)
        {
            List<int> pairs = game.story.funeralApart;
            int now = Find.TickManager.TicksGame;
            for (int i = pairs.Count - 3; i >= 0; i -= 3)
                if (now - pairs[i + 2] > RecordMaxTicks) pairs.RemoveRange(i, 3);
        }

        /// <summary>Writes the letter. Nothing when there is nowhere to hold one.</summary>
        internal static Funeral Ask(PrisonGame game, Map map, PrisonMap pm, Corpse body)
        {
            Pawn dead = body.InnerPawn;
            if (!HasPlace(map, pm, body)) return null;
            bool staff = IsStaff(dead);
            FuneralPlace place = Burial.Wanted(body, pm) ? FuneralPlace.Grave : pm.zones.Any(RPFaithDefOf.RP_Chapel) ? FuneralPlace.Chapel : FuneralPlace.Pad;
            var f = new Funeral { deadId = dead.thingIDNumber, name = dead.LabelShort, staff = staff, state = FuneralState.Asked, place = place, askedTick = Find.TickManager.TicksGame };
            if (!staff) Invite(f, dead, TakeApart(game, dead.thingIDNumber));
            game.story.funerals.Add(f);
            string where = ("RP_FuneralPlace_" + place).Translate();
            // (The rival is only spoken of where one could come: the dead one's gang's rival killed them.)
            string rival = f.gang != null && KillerGangOf(map, dead.thingIDNumber) is { } killers && GangUtility.AreRivals(killers, f.gang) ? "RP_FuneralRivalNote".Translate(f.name, ScuffleChance.ToStringPercent()).Resolve() : "";
            TaggedString text = staff ? "RP_FuneralTextStaff".Translate(f.name, where) : "RP_FuneralText".Translate(f.name, where, rival);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Funeral", "RP_FuneralLabel".Translate(f.name), text, map, new LookTargets(body));
            letter.amount = f.deadId;
            letter.note = f.name;
            letter.StartTimeout(AnswerTicks);
            Find.LetterStack.ReceiveLetter(letter);
            return f;
        }

        public static bool IsStaff(Pawn dead) => dead.GetComp<CompStaff>() is { role: not null };

        /// <summary>Who would mourn this inmate, taken while their file is in the morgue (the shuttle may take the body, and the pawn with it, before the service is held): their wing and gang, the friends and gang mates by closeness, the relative who would come. Not those the warden kept apart from them (<paramref name="apart"/>).</summary>
        private static void Invite(Funeral f, Pawn dead, HashSet<int> apart)
        {
            if (dead.Inmate() is not { } dc || dead.MapHeld is not { } map) return;
            f.level = dc.security;
            f.gang = dc.gang;
            var grieving = new List<(Pawn p, int grief)>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p == dead || p.Inmate() is not { } c) continue;
                int grief = Grief(p, c, dead, dc, apart);
                if (grief > 0) grieving.Add((p, grief));
            }
            grieving.Sort((a, b) => a.grief != b.grief ? b.grief.CompareTo(a.grief) : a.p.thingIDNumber.CompareTo(b.p.thingIDNumber));
            f.invited.AddRange(grieving.Take(MaxMourners * 2).Select(x => x.p.thingIDNumber));
            if (Relatives.RandomVisitor(dc) is { } kin) (f.kinName, f.kinKind) = (kin.name, kin.kind);
        }

        /// <summary>The funeral is chosen: its hour is the next free-time or work hour of the dead's regime. None at all (a regime with no such hour) is a quiet burial, and the line says so.</summary>
        internal static void Plan(PrisonGame game, Map map, Funeral f)
        {
            int hold = NextHoldTick(game, map, LevelOf(map, f));
            if (hold < 0)
            {
                Quiet(f, "RP_FuneralWhy_NoHour".Translate());
                return;
            }
            f.state = FuneralState.Waiting;
            f.holdTick = hold;
        }

        /// <summary>The quiet burial, with the line that says why a funeral that was chosen did not happen.</summary>
        internal static void Quiet(Funeral f, TaggedString why)
        {
            f.state = FuneralState.Quiet;
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 1, "RP_Chr_FuneralLapsed".Translate(f.name, why));
            Messages.Message("RP_FuneralLapsed".Translate(f.name, why), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        // ---- the hour -----------------------------------------------------------------------------------------------------------------------

        /// <summary>The level whose timetable the funeral keeps to: the dead's (the default level for the staff).</summary>
        private static SecurityLevelDef LevelOf(Map map, Funeral f) => !f.staff && f.level != null ? f.level : InmateUtility.DefaultSecurity;

        /// <summary>An hour of free time or of work in the level's timetable.</summary>
        public static bool ServiceHour(PrisonGame game, SecurityLevelDef level, int hour)
        {
            RegimeActivityDef slot = game.SlotAt(level, hour);
            return slot == RPDefOf.RP_FreeTime || slot == RPLaborDefOf.RP_Work;
        }

        /// <summary>The tick the next free-time or work hour starts at (the next hour at the earliest), or -1 when the timetable has none.</summary>
        public static int NextHoldTick(PrisonGame game, Map map, SecurityLevelDef level)
        {
            int hourStart = Find.TickManager.TicksGame - GenLocalDate.DayTick(map) % GenDate.TicksPerHour;
            int hour = GenLocalDate.HourOfDay(map);
            for (int k = 1; k <= 24; k++)
                if (ServiceHour(game, level, (hour + k) % 24)) return hourStart + k * GenDate.TicksPerHour;
            return -1;
        }

        // ---- the passes ---------------------------------------------------------------------------------------------------------------------

        /// <summary>The body has begun to rot: it cannot be held for a letter or an hour any longer (the morgue is not cold), and the burial is a quiet one.</summary>
        private static bool Rotting(Map map, Funeral f) => CorpseOf(map, f.deadId) is { } body && body.GetRotStage() != RotStage.Fresh;

        /// <summary>Hourly: a funeral whose hour has come starts (when nothing is in its way), one that has waited a day lapses, a letter nobody answered becomes a quiet burial (as does one whose body has begun to rot), and the old records go.</summary>
        public static void Hourly(PrisonGame game, Map map, int hour)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            if (game.story.funeralApart.Count > 0) ForgetApart(game);
            List<Funeral> list = game.story.funerals;
            if (list.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Funeral f = list[i];
                switch (f.state)
                {
                    case FuneralState.Asked:
                        if (now - f.askedTick > AskedMaxTicks) Quiet(f, "RP_FuneralWhy_NoAnswer".Translate());
                        else if (Rotting(map, f)) Quiet(f, "RP_FuneralWhy_Rot".Translate());
                        break;
                    case FuneralState.Waiting:
                        if (Rotting(map, f)) Quiet(f, "RP_FuneralWhy_Rot".Translate());
                        else if (now >= f.holdTick - 60 && TryStart(game, map, pm, f)) break;
                        else if (now - f.holdTick > LapseTicks) Quiet(f, "RP_FuneralWhy_Late".Translate());
                        break;
                    case FuneralState.Quiet:
                        if (CorpseOf(map, f.deadId) == null || now - f.askedTick > RecordMaxTicks) list.RemoveAt(i);
                        break;
                    case FuneralState.Done:
                        if (now / GenDate.TicksPerDay > f.endTick / GenDate.TicksPerDay) list.RemoveAt(i);
                        break;
                }
            }
        }

        /// <summary>Every 250 ticks: a service is broken off for a lockdown or a riot, a rival who has come is looked at once, and a service ends when its time is up.</summary>
        public static void Rare(PrisonGame game, Map map)
        {
            List<Funeral> list = game.story.funerals;
            if (list.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return;
            foreach (Funeral f in list.ToList())
            {
                if (f.state != FuneralState.Gathering) continue;
                if (Trouble(game, map))
                {
                    Conclude(game, map, pm, f, "RP_FuneralWhy_Trouble".Translate());
                    continue;
                }
                if (f.stirrerId >= 0 && !f.scuffled) LookToTheRival(map, f);
                if (now >= f.endTick) Conclude(game, map, pm, f, null);
            }
        }

        /// <summary>A lockdown, a riot, a hostage crisis or a raid (an enemy on the map): no service is held, and one under way is broken off.</summary>
        internal static bool Trouble(PrisonGame game, Map map) =>
            game.LockdownActive || HostageCrisis.Running(map) != null || RiotPolice.RiotOngoing(map) || (ThreatForTests ?? GenHostility.AnyHostileActiveThreatToPlayer(map));

        public static Corpse CorpseOf(Map map, int deadId)
        {
            if (map == null) return null;
            List<Thing> corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
            for (int i = 0; i < corpses.Count; i++)
                if (corpses[i] is Corpse c && c.InnerPawn?.thingIDNumber == deadId) return c;
            return null;
        }

        // ---- the burial ---------------------------------------------------------------------------------------------------------------------

        /// <summary>The workmen may take this body (to the plot): no funeral was asked for it, it is a quiet burial, or the funeral is on or over. A letter not yet answered and an hour not yet come hold it.</summary>
        public static bool MayBury(Corpse c)
        {
            if (!Active || c?.InnerPawn == null) return true;
            Funeral f = Of(c.InnerPawn);
            return f == null || f.state is FuneralState.Quiet or FuneralState.Gathering or FuneralState.Done;
        }

        /// <summary>The grave site the funeral stands round, when it is still free to dig in (the workman digs there, with the mourners round it).</summary>
        public static IntVec3 SiteFor(Corpse c, Map map, PrisonMap pm)
        {
            Funeral f = Active ? Of(c.InnerPawn) : null;
            return f is { state: FuneralState.Gathering, place: FuneralPlace.Grave } && f.cell.IsValid && Burial.Fits(map, pm, f.cell) ? f.cell : IntVec3.Invalid;
        }

        /// <summary>The warden has said their piece at the funeral they were sent to (the address job, in its funeral mode).</summary>
        public static void Notify_Spoke(Pawn warden)
        {
            if (GatheringFor(warden) is { } f) f.spoke = true;
        }

        /// <summary>A grave was dug and the body laid in it.</summary>
        public static void Notify_Buried(Pawn dead)
        {
            if (Of(dead) is { } f) f.buried = true;
        }

        /// <summary>The shuttle took the body: a funeral that was to be at the grave is in the chapel (or at the pad) instead, and a quiet burial's record is done with.</summary>
        public static void Notify_Sent(Pawn dead)
        {
            if (!Active || Of(dead) is not { } f) return;
            if (f.state == FuneralState.Quiet) All.Remove(f);
            else if (f.state is FuneralState.Asked or FuneralState.Waiting && f.place == FuneralPlace.Grave)
                f.place = PrisonMap.Of(Find.AnyPlayerHomeMap) is { } pm && pm.zones.Any(RPFaithDefOf.RP_Chapel) ? FuneralPlace.Chapel : FuneralPlace.Pad;
        }

        // ---- the gathering ------------------------------------------------------------------------------------------------------------------

        /// <summary>Everyone who can be sent: the lists the service is made of.</summary>
        internal sealed class Cast
        {
            public readonly List<Pawn> mourners = new List<Pawn>(), escorts = new List<Pawn>(), colleagues = new List<Pawn>();
            public Pawn chaplain, speaker, stirrer;
            public Relative relative;

            /// <summary>Nobody at all is to come (a service with no one at it is not held).</summary>
            public bool Nobody => mourners.Count == 0 && colleagues.Count == 0 && stirrer == null && chaplain == null && speaker == null && relative == null;
        }

        /// <summary>
        /// Starts the funeral if its hour is right and nothing is in the way: the dead's timetable has free time or work now, no lockdown, no riot, no hostage crisis, and a guard to spare when inmates are to come. False: it waits (and
        /// lapses after a day).
        /// </summary>
        internal static bool TryStart(PrisonGame game, Map map, PrisonMap pm, Funeral f)
        {
            Corpse body = CorpseOf(map, f.deadId);
            int hour = GenLocalDate.HourOfDay(map);
            if (!ServiceHour(game, LevelOf(map, f), hour)) return false;
            if (Trouble(game, map)) return false;
            Cast cast = f.staff ? StaffCast(game, map, pm, f, hour) : InmateCast(game, map, pm, f, body, hour);
            if (cast == null) return false;
            (FuneralPlace place, IntVec3 cell) = (f.place, f.cell);
            // (Nobody who can walk there: it waits, as for a guard - and the place it was settled on is not kept.)
            if (!Settle(map, pm, f, body, cast) || cast.Nobody)
            {
                (f.place, f.cell) = (place, cell);
                return false;
            }
            Begin(game, map, pm, f, cast, body?.InnerPawn);
            return true;
        }

        /// <summary>Whether this pawn can walk to the cell (the pathing's answer, or a check's).</summary>
        internal static bool CanGet(Pawn p, IntVec3 cell) => ReachForTests != null ? ReachForTests(p, cell) : p.CanReach(cell, PathEndMode.Touch, Danger.Some);

        private static bool FreeToLeave(Pawn p, int hour)
        {
            if (p == null || !p.Spawned || p.Dead || p.Downed || p.Drafted || p.InMentalState || p.GetComp<CompStaff>() is not { role: not null } sc || CastForTests != null && !CastForTests(p)) return false;
            if (HostageCrisis.IsHeld(p) || IncidentWorker_StaffStrike.Striking(p) || StaffCommute.ShouldBeHome(p, sc, hour) || p.CurJobDef == RPCommuteDefOf.RP_GoHome) return false;
            // (On a job somebody is counting on - carrying a body, tending, a supermax walk - they are needed elsewhere: only a job that may be put down for something else.)
            Job job = p.CurJob;
            return job == null || job.def.casualInterruptible;
        }

        private static List<Pawn> StaffOnDuty(Map map, int hour, System.Func<Pawn, bool> filter)
        {
            var list = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                if (FreeToLeave(p, hour) && filter(p)) list.Add(p);
            return list;
        }

        /// <summary>How many of the guards on duty can be spared: at most half of them (never the last).</summary>
        internal static int GuardsToSpare(Map map, int hour)
        {
            int onDuty = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                if (GuardUtility.IsGuard(p) && p.GetComp<CompStaff>() is { } sc && !StaffCommute.ShouldBeHome(p, sc, hour) && !p.Downed && (CastForTests == null || CastForTests(p))) onDuty++;
            return onDuty <= 1 ? 0 : onDuty / 2;
        }

        private static Pawn ChaplainFor(Map map, int hour, int deadId)
        {
            Pawn best = null;
            foreach (Pawn p in StaffOnDuty(map, hour, x => x.thingIDNumber != deadId && x.GetComp<CompStaff>().role == RPStaffRoleDefOf.RP_Chaplain && x.CurJobDef != RPProgramDefOf.RP_TeachClass))
                if (best == null || p.thingIDNumber < best.thingIDNumber) best = p;
            return best;
        }

        /// <summary>Whether an inmate is out of the regime's hands for an hour as a class is: all but the shut in (the hole, a punishment, lockup in the regime or a lockdown), supermax, protective custody, the condemned.</summary>
        internal static bool CanComeOut(PrisonGame game, PrisonMap pm, Pawn p, int hour) => WhyNotComeOut(game, pm, p, hour) == null;

        /// <summary>What keeps the inmate from coming out for the hour (the first thing), or null when nothing does: the answer of <see cref="CanComeOut"/>, with its reason for the logs.</summary>
        internal static string WhyNotComeOut(PrisonGame game, PrisonMap pm, Pawn p, int hour)
        {
            if (!InmateUtility.IsActiveInmate(p)) return "not an active inmate";
            if (p.Inmate() is not { processed: true, activeFlag: true } c) return "not processed or not flagged active";
            if (CastForTests != null && !CastForTests(p)) return "left out by the test's cast";
            if (p.Downed) return "downed";
            if (!p.Awake()) return "asleep";
            if (p.InMentalState) return "in a mental state";
            if (c.forcedActivity != null) return $"forced into {c.forcedActivity.defName}";
            if (c.awaitingSearch) return "awaiting a search";
            if (c.leg != null) return "on a transfer leg";
            if (c.visitor != null) return "with a visitor";
            if (c.security is { escortOnly: true }) return "escort only";
            if (game.LockedDown(c)) return "locked down";
            if (Trials.IsProtected(c)) return "in protective custody";
            if (DeathRowSystem.Ext(c) != null) return "on death row";
            if (HostageCrisis.IsHeld(p)) return "a hostage";
            if (ShuttleSystem.OnTheWayOut(p)) return "on the way out";
            if (ShuttleSystem.IsArrival(p)) return "an arrival";
            if (game.ActivityAt(c, hour).lockup) return "in lockup that hour";
            if (ProgramSystem.SessionOf(p, pm) is { } session) return $"in a class ({session.program?.defName})";
            return null;
        }

        /// <summary>How much the inmate mourns the dead, or zero when they do not: a friend inside, an opinion of them past <see cref="FriendOpinion"/>, a gang mate. Not an enemy, not one the warden kept apart from them (<paramref name="apart"/>, taken at the death).</summary>
        internal static int Grief(Pawn p, CompInmate c, Pawn dead, CompInmate dc, HashSet<int> apart = null)
        {
            if (p == dead || PrisonRelations.Enemies(p, dead) || PrisonRelations.Enemies(dead, p) || apart != null && apart.Contains(p.thingIDNumber)) return 0;
            int grief = 0;
            if (PrisonRelations.Friends(p, dead) || PrisonRelations.Friends(dead, p)) grief += 100;
            if (dc?.gang != null && c.gang == dc.gang) grief += 50;
            int opinion = p.relations?.OpinionOf(dead) ?? 0;
            if (opinion >= FriendOpinion) grief += opinion;
            return grief;
        }

        private static Cast InmateCast(PrisonGame game, Map map, PrisonMap pm, Funeral f, Corpse body, int hour)
        {
            var cast = new Cast();
            // The friends and gang mates the body's file named when it was laid in the morgue (not those the warden kept apart from the dead one: the file left them out), who can be let out now.
            var candidates = new List<Pawn>();
            foreach (int id in f.invited)
                if (CourtUtil.FindInmate(map, id) is { } p && CanComeOut(game, pm, p, hour)) candidates.Add(p);
            // (Of two the warden keeps apart, one comes: the one who goes first today.)
            cast.mourners.AddRange(KeepApart.Cohort(candidates).Take(MaxMourners));
            if (cast.mourners.Count > 0) RollTheRival(game, map, pm, f, hour, cast);
            cast.chaplain = ChaplainFor(map, hour, f.deadId);
            cast.relative = RelativeWhoCanCome(game, map, pm, f);
            // (The rival is taken out by a guard too: one of the people the escorts take.)
            int stirrers = cast.stirrer != null ? 1 : 0;
            int needed = (cast.mourners.Count + stirrers + EscortRatio - 1) / EscortRatio;
            if (cast.mourners.Count > 0)
            {
                int spare = GuardsToSpare(map, hour);
                IntVec3 near = f.cell.IsValid ? f.cell : body is { Spawned: true } ? body.Position : IntVec3.Invalid;
                List<Pawn> guards = StaffOnDuty(map, hour, GuardUtility.IsGuard).OrderBy(g => near.IsValid ? (g.Position - near).LengthHorizontalSquared : 0).Take(spare).ToList();
                // No guard to spare: the service waits (and lapses after a day), the mourners do not walk out without one.
                if (guards.Count == 0) return null;
                cast.escorts.AddRange(guards.Take(needed));
                int room = cast.escorts.Count * EscortRatio - stirrers;
                if (cast.mourners.Count > room) cast.mourners.RemoveRange(room, cast.mourners.Count - room);
            }
            return cast;
        }

        private static Cast StaffCast(PrisonGame game, Map map, PrisonMap pm, Funeral f, int hour)
        {
            var cast = new Cast { chaplain = ChaplainFor(map, hour, f.deadId) };
            // (The warden speaks if they are free as any of the staff is, and not in the middle of another of their own.)
            Pawn warden = WardenSystem.InPost(map);
            if (warden != null && warden.thingIDNumber != f.deadId && !WardenSystem.DoingAbility(warden) && FreeToLeave(warden, hour)) cast.speaker = warden;
            int spare = GuardsToSpare(map, hour);
            var guards = 0;
            bool grave = f.place == FuneralPlace.Grave;
            foreach (Pawn p in StaffOnDuty(map, hour, x => x.thingIDNumber != f.deadId && x != cast.chaplain && x != cast.speaker).OrderBy(x => x.thingIDNumber))
            {
                if (cast.colleagues.Count >= MaxColleagues) break;
                // (The workmen are the ones who dig the grave.)
                if (grave && Burial.IsWorkman(p)) continue;
                if (GuardUtility.IsGuard(p))
                {
                    if (guards >= spare) continue;
                    guards++;
                }
                cast.colleagues.Add(p);
            }
            return cast;
        }

        /// <summary>
        /// A rival of the killer's gang on the dead's wing turns up, at <see cref="ScuffleChance"/> (the roll is made once, as the service begins): the dead was killed by an inmate of a gang that is the dead's gang's rival, and a member of it who is
        /// free to walk is on the same wing - not the killer, and not one the warden keeps apart from a mourner.
        /// </summary>
        private static void RollTheRival(PrisonGame game, Map map, PrisonMap pm, Funeral f, int hour, Cast cast)
        {
            Pawn killer = KillerOf(map, f.deadId);
            if (f.gang == null || killer?.Inmate()?.gang is not { } killers || !GangUtility.AreRivals(killers, f.gang)) return;
            Pawn rival = null;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p != killer && p.Inmate() is { } c && c.gang == killers && c.security == f.level && CanComeOut(game, pm, p, hour) && !cast.mourners.Exists(m => KeepApart.Ordered(p, m)) && (rival == null || p.thingIDNumber < rival.thingIDNumber)) rival = p;
            if (rival == null) return;
            float roll = ScuffleRollForTests ?? Rand.Value;
            if (roll < ScuffleChance) cast.stirrer = rival;
        }

        /// <summary>For a check: the rival's roll, made as the service begins, on its own.</summary>
        internal static void RollTheRivalForTests(PrisonGame game, Map map, PrisonMap pm, Funeral f, int hour, Cast cast) => RollTheRival(game, map, pm, f, hour, cast);

        /// <summary>The gang of the inmate who killed this one (by the case against them), or null.</summary>
        public static GangDef KillerGangOf(Map map, int deadId) => KillerOf(map, deadId)?.Inmate()?.gang;

        /// <summary>The inmate the case against names as the killer of this one, or null.</summary>
        public static Pawn KillerOf(Map map, int deadId) =>
            PrisonGame.Instance.story.trials.Find(t => t.victimId == deadId && t.killerId >= 0) is { } trial ? CourtUtil.FindInmate(map, trial.killerId) : null;

        /// <summary>The relative of the dead who could come from outside as a visitor, if the visitation system can bring them: a visitation room, a level that is visited in person (not through glass, not at all), someone in touch and not barred, a way in.</summary>
        internal static Relative RelativeWhoCanCome(PrisonGame game, Map map, PrisonMap pm, Funeral f)
        {
            if (f.kinName == null || !pm.zones.Any(RPVisitDefOf.RP_Visitation) || game.VisitPolicyFor(f.level) != VisitPolicy.Contact) return null;
            var rel = new Relative { name = f.kinName, kind = f.kinKind };
            if (RingSystem.SupplierBarred(rel.Label)) return null;
            return RCellFinder.TryFindRandomPawnEntryCell(out _, map, CellFinder.EdgeRoadChance_Always) ? rel : null;
        }

        // ---- the place ----------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Where it is held: the grave when the plot will take the body and the mourners can walk to it (the plot is staff-only: for an inmate's mourners it usually cannot be reached), else the chapel, else the pad. The people who cannot
        /// get there are dropped from the cast. False when there is no place at all.
        /// </summary>
        private static bool Settle(Map map, PrisonMap pm, Funeral f, Corpse body, Cast cast)
        {
            var going = new List<Pawn>(cast.mourners);
            going.AddRange(cast.colleagues);
            if (cast.stirrer != null) going.Add(cast.stirrer);
            if (body is { Spawned: true } && Burial.Wanted(body, pm) && Burial.FindSite(map, pm, null) is { IsValid: true } site)
            {
                bool anyone = going.Count == 0 || going.Any(p => CanGet(p, site));
                if (anyone || !HasVenue(map, pm))
                {
                    (f.place, f.cell) = (FuneralPlace.Grave, site);
                    return Prune(cast);
                }
            }
            if (!HasVenue(map, pm)) return false;
            if (pm.zones.Any(RPFaithDefOf.RP_Chapel))
            {
                f.place = FuneralPlace.Chapel;
                f.cell = MiddleOf(map, pm.zones.CellsOf(RPFaithDefOf.RP_Chapel));
            }
            else
            {
                f.place = FuneralPlace.Pad;
                f.cell = ShuttleSystem.Pads(map).Select(b => b.Position).FirstOrDefault();
            }
            return f.cell.IsValid && Prune(cast);

            bool Prune(Cast c)
            {
                c.mourners.RemoveAll(p => !CanGet(p, f.cell));
                c.colleagues.RemoveAll(p => !CanGet(p, f.cell));
                // (A rival who came to stand at the back of a funeral nobody else is at does not come alone.)
                if (c.stirrer != null && (c.mourners.Count == 0 || !CanGet(c.stirrer, f.cell))) c.stirrer = null;
                if (c.chaplain != null && !CanGet(c.chaplain, f.cell)) c.chaplain = null;
                if (c.speaker != null && !CanGet(c.speaker, f.cell)) c.speaker = null;
                // (Guards go with the inmates they take out: none to escort, none go.)
                if (c.mourners.Count == 0) c.escorts.Clear();
                else c.escorts.RemoveAll(p => !CanGet(p, f.cell));
                return true;
            }
        }

        private static IntVec3 MiddleOf(Map map, List<IntVec3> cells)
        {
            if (cells.Count == 0) return IntVec3.Invalid;
            Vector3 sum = Vector3.zero;
            foreach (IntVec3 c in cells) sum += c.ToVector3();
            Vector3 middle = sum / cells.Count;
            IntVec3 best = IntVec3.Invalid;
            float bestDist = float.MaxValue;
            foreach (IntVec3 c in cells)
            {
                float d = (c.ToVector3() - middle).sqrMagnitude;
                if (d < bestDist && c.Standable(map)) (best, bestDist) = (c, d);
            }
            return best;
        }

        /// <summary>A cell round the middle for this pawn to stand on: standable, off the grave's own cells, in the chapel while it has room, in a patch their level may use and one they can walk to. Invalid when there is none.</summary>
        public static IntVec3 Stand(Funeral f, Pawn p, PrisonMap pm)
        {
            Map map = p.Map;
            var cells = new List<IntVec3>();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(f.cell, StandRadius, true))
            {
                if (!c.InBounds(map) || !c.Standable(map)) continue;
                if (f.place == FuneralPlace.Grave && GenAdj.OccupiedRect(f.cell, Rot4.North, ThingDefOf.Grave.Size).Contains(c)) continue;
                cells.Add(c);
            }
            if (f.place == FuneralPlace.Chapel && cells.Exists(c => pm.zones.ZoneAt(c) == RPFaithDefOf.RP_Chapel)) cells.RemoveAll(c => pm.zones.ZoneAt(c) != RPFaithDefOf.RP_Chapel);
            cells.Shuffle();
            for (int i = 0; i < cells.Count && i < 16; i++)
                if ((p.Inmate() == null || KeptRooms.Allows(pm, cells[i], p)) && CanGet(p, cells[i])) return cells[i];
            return IntVec3.Invalid;
        }

        // ---- begin, jobs, end ---------------------------------------------------------------------------------------------------------------

        private static Job AttendJob(Funeral f, IntVec3 stand, JobDef def)
        {
            Job job = JobMaker.MakeJob(def, stand, f.cell);
            job.expiryInterval = f.endTick - Find.TickManager.TicksGame + 600;
            job.checkOverrideOnExpire = false;
            return job;
        }

        /// <summary>The job an inmate sent to a service takes up (the regime's own giver asks, before the slot's activity): walk to the grave, the chapel or the pad and stand there.</summary>
        public static Job JobFor(Pawn p, PrisonMap pm)
        {
            if (GatheringFor(p) is not { } f) return null;
            IntVec3 stand = Stand(f, p, pm);
            return stand.IsValid ? AttendJob(f, stand, RPFuneralDefOf.RP_AttendFuneral) : null;
        }

        private static void Send(Funeral f, Pawn p, PrisonMap pm, JobDef def, bool inmate)
        {
            if (inmate)
            {
                // (The regime's own giver sends them on, as it sends a class: see JobFor.)
                f.attendees.Add(p.thingIDNumber);
                pm.QueueRegimeInterrupt(p);
                return;
            }
            IntVec3 stand = Stand(f, p, pm);
            if (!stand.IsValid) return;
            f.attendees.Add(p.thingIDNumber);
            p.jobs.StartJob(AttendJob(f, stand, def), JobCondition.InterruptForced);
        }

        private static void Begin(PrisonGame game, Map map, PrisonMap pm, Funeral f, Cast cast, Pawn dead)
        {
            int now = Find.TickManager.TicksGame;
            f.state = FuneralState.Gathering;
            (f.startTick, f.endTick) = (now, now + GatherTicks);
            f.attendees.Clear();
            f.mourners = cast.mourners.Count;
            f.stirrerId = cast.stirrer?.thingIDNumber ?? -1;
            f.scuffled = false;
            foreach (Pawn p in cast.mourners) Send(f, p, pm, null, inmate: true);
            if (cast.stirrer != null) Send(f, cast.stirrer, pm, null, inmate: true);
            foreach (Pawn p in cast.escorts) Send(f, p, pm, RPFuneralDefOf.RP_AttendFuneral, inmate: false);
            foreach (Pawn p in cast.colleagues) Send(f, p, pm, RPFuneralDefOf.RP_AttendFuneral, inmate: false);
            if (cast.chaplain != null && Stand(f, cast.chaplain, pm) is { IsValid: true } at)
            {
                f.attendees.Add(cast.chaplain.thingIDNumber);
                cast.chaplain.jobs.StartJob(AttendJob(f, at, RPFuneralDefOf.RP_LeadFuneral), JobCondition.InterruptForced);
            }
            if (cast.speaker != null && Stand(f, cast.speaker, pm) is { IsValid: true } front)
            {
                f.attendees.Add(cast.speaker.thingIDNumber);
                // (The address job, reused: the warden walks to the spot and speaks there, and what it does is the eulogy's, not the canteen's.)
                Job job = JobMaker.MakeJob(RPWardenDefOf.RP_WardenAddress, front);
                job.count = JobDriver_WardenAddress.EulogyMode;
                cast.speaker.jobs.StartJob(job, JobCondition.InterruptForced);
            }
            if (cast.relative != null) BringRelative(game, map, f, cast.relative, dead);
            Messages.Message("RP_FuneralBegins".Translate(f.name, ("RP_FuneralPlace_" + f.place).Translate()), new LookTargets(f.cell, map), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>The relative comes in from the road as a visitor for the hour, and goes back out at the end of it.</summary>
        private static void BringRelative(PrisonGame game, Map map, Funeral f, Relative rel, Pawn dead)
        {
            if (!RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 entry, map, CellFinder.EdgeRoadChance_Always)) return;
            Faction outside = InmateGenerator.OutsideFaction;
            Pawn visitor = PawnGenerator.GeneratePawn(new PawnGenerationRequest(RPVisitDefOf.RP_Visitor, outside, PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowFood: false));
            visitor.Name = new NameTriple(rel.name, rel.name, (dead?.Name as NameTriple)?.Last ?? "");
            GenSpawn.Spawn(visitor, entry, map);
            visitor.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Guest);
            if (!CanGet(visitor, f.cell))
            {
                visitor.Destroy();
                return;
            }
            LordMaker.MakeNewLord(outside, new LordJob_AttendFuneral(f.cell, f.endTick), map, new List<Pawn> { visitor });
            f.visitors.Add(visitor);
            f.relative = rel.Label;
        }

        /// <summary>The rival who turned up: once they and a mourner are within a few steps of each other, the pastimes' scuffle is rolled between them.</summary>
        private static void LookToTheRival(Map map, Funeral f)
        {
            Pawn rival = CourtUtil.FindInmate(map, f.stirrerId);
            if (rival == null)
            {
                f.scuffled = true;
                return;
            }
            bool near = false;
            foreach (int id in f.attendees)
                if (id != f.stirrerId && CourtUtil.FindInmate(map, id) is { } m && m.Position.InHorDistOf(rival.Position, 8f)) near = true;
            if (!near || !rival.Position.InHorDistOf(f.cell, PresentWithin)) return;
            f.scuffled = true;
            string gang = rival.Inmate()?.gang?.label ?? "?";
            bool fight = Scuffles.TryStart(map, (a, b) => a.thingIDNumber == f.stirrerId && f.Attends(b.thingIDNumber) || b.thingIDNumber == f.stirrerId && f.Attends(a.thingIDNumber));
            Story.Record(RPChronicleDefOf.RP_Chr_Violence, fight ? 2 : 1, (fight ? "RP_Chr_FuneralScuffle" : "RP_Chr_FuneralScuffleNone").Translate(rival.LabelShort, gang, f.name), rival);
        }

        /// <summary>The service is over (or broken off, with the reason): the mourners' thought and tension, the wing's tension, the staff's morale, the chronicle line.</summary>
        internal static void Conclude(PrisonGame game, Map map, PrisonMap pm, Funeral f, TaggedString? brokenOff)
        {
            f.state = FuneralState.Done;
            f.endTick = Find.TickManager.TicksGame;
            Pawn dead = CorpseOf(map, f.deadId)?.InnerPawn;
            if (brokenOff is { } why)
            {
                Story.Record(RPChronicleDefOf.RP_Chr_Death, 1, "RP_Chr_FuneralBroken".Translate(f.name, why), dead);
                Messages.Message("RP_FuneralBroken".Translate(f.name, why), MessageTypeDefOf.NegativeEvent, historical: false);
                return;
            }
            var there = new List<Pawn>();
            foreach (int id in f.attendees)
                if (FindAttendee(map, id) is { } p && p.Position.InHorDistOf(f.cell, PresentWithin)) there.Add(p);
            f.chaplain = there.Exists(p => p.GetComp<CompStaff>()?.role == RPStaffRoleDefOf.RP_Chaplain);
            // (Nobody stood there - they were let out and did not get there, or were called away: no relief for the wing and no thought for the staff.)
            if (there.Count > 0)
            {
                if (f.staff) StaffReaction(map, f, there);
                else InmateReaction(map, f, there);
            }
            string extras = (f.chaplain ? " " + "RP_FuneralChaplained".Translate() : "") + (f.spoke ? " " + "RP_FuneralWardenSpoke".Translate() : "") + (f.relative != null ? " " + "RP_FuneralRelative".Translate(f.relative) : "");
            Story.Record(RPChronicleDefOf.RP_Chr_Death, f.staff ? 2 : 1, "RP_Chr_Funeral".Translate(f.name, ("RP_FuneralPlace_" + f.place).Translate(), there.Count(x => x.Inmate() != null || x.GetComp<CompStaff>() != null), extras), dead);
        }

        private static Pawn FindAttendee(Map map, int id)
        {
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                if (p.thingIDNumber == id) return p;
            return null;
        }

        private static void InmateReaction(Map map, Funeral f, List<Pawn> there)
        {
            int stage = f.chaplain ? 1 : 0;
            var mourners = new HashSet<Pawn>();
            foreach (Pawn p in there)
            {
                if (p.Inmate() is not { } c || p.thingIDNumber == f.stirrerId) continue;
                mourners.Add(p);
                if (p.needs?.mood != null && ThoughtMaker.MakeThought(RPFuneralDefOf.RP_SaidGoodbye, stage) is Thought_Memory m) p.needs.mood.thoughts.memories.TryGainMemory(m);
                c.tension = Mathf.Max(0f, c.tension - (f.chaplain ? ServiceTension : MournerTension));
            }
            // The wing: the dead's level (kept on the record), who did not come.
            SecurityLevelDef level = f.level;
            if (level == null) return;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (!mourners.Contains(p) && p.thingIDNumber != f.stirrerId && p.Inmate() is { } c && c.security == level && InmateUtility.IsActiveInmate(p)) c.tension = Mathf.Max(0f, c.tension - WingTension);
        }

        private static void StaffReaction(Map map, Funeral f, List<Pawn> there)
        {
            var came = new HashSet<Pawn>(there);
            foreach (Pawn p in StaffCommute.AllStaff(map))
            {
                if (p.thingIDNumber == f.deadId || p.needs?.mood == null || p.GetComp<CompStaff>()?.role == null) continue;
                int stage = !came.Contains(p) ? 0 : f.spoke ? 2 : 1;
                if (ThoughtMaker.MakeThought(RPFuneralDefOf.RP_StaffFarewell, stage) is Thought_Memory m) p.needs.mood.thoughts.memories.TryGainMemory(m);
            }
        }

        // ---- for the saveload plan ----------------------------------------------------------------------------------------------------------

        public static string Describe(PrisonGame game) => string.Join(",", game.story.funerals.Select(f => f.Describe())) + "|apart " + string.Join("+", game.story.funeralApart);
    }

    /// <summary>The letter: a quiet burial (what an unanswered letter is) or a funeral.</summary>
    public class FuneralDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Funeral_Quiet", "RP_Funeral_Hold" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 0;

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            // (A letter that was answered or lapsed meanwhile - a quiet burial by the day, by the rot - has nothing left to decide.)
            if (Funerals.Of(l.amount) is not { state: FuneralState.Asked }) return "RP_FuneralStale".Translate().ToString();
            if (option != 1) return null;
            Map map = l.map ?? Find.AnyPlayerHomeMap;
            if (map == null || PrisonMap.Of(map) is not { } pm) return null;
            Corpse body = Funerals.CorpseOf(map, l.amount);
            return Funerals.HasPlace(map, pm, body) ? null : "RP_FuneralNoPlace".Translate().ToString();
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            PrisonGame game = PrisonGame.Instance;
            if (Funerals.Of(l.amount) is not { state: FuneralState.Asked } f) return;
            if (option != 1)
            {
                f.state = FuneralState.Quiet;
                return;
            }
            Funerals.Plan(game, map, f);
        }
    }

    /// <summary>The visitors' way of waiting in the prison (the visiting duty), for the one hour of the service, and then out of the gate.</summary>
    public class LordJob_AttendFuneral : LordJob
    {
        private IntVec3 spot;
        private int endTick;

        public LordJob_AttendFuneral()
        {
        }

        public LordJob_AttendFuneral(IntVec3 spot, int endTick)
        {
            this.spot = spot;
            this.endTick = endTick;
        }

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            var wait = new LordToil_VisitInmate(spot);
            graph.StartingToil = wait;
            var exit = new LordToil_ExitMap(LocomotionUrgency.Walk);
            graph.AddToil(exit);
            var done = new Transition(wait, exit);
            done.AddTrigger(new Trigger_Custom(signal => signal.type == TriggerSignalType.Tick && Find.TickManager.TicksGame >= endTick));
            done.AddTrigger(new Trigger_PawnHarmed());
            graph.AddTransition(done);
            return graph;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref spot, "spot");
            Scribe_Values.Look(ref endTick, "endTick");
        }
    }

    /// <summary>Walk to the spot and stand there, facing the grave (or the front of the chapel), until the service is over: an inmate sent by the regime, a guard who escorts them, a colleague.</summary>
    public class JobDriver_AttendFuneral : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddFailCondition(() => Funerals.GatheringFor(pawn) == null);
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);
            Toil stand = ToilMaker.MakeToil("StandAtTheService");
            stand.defaultCompleteMode = ToilCompleteMode.Never;
            stand.handlingFacing = true;
            stand.socialMode = RandomSocialMode.Off;
            stand.tickIntervalAction = delta => pawn.rotationTracker.FaceCell(job.targetB.Cell);
            yield return stand;
        }
    }
}
