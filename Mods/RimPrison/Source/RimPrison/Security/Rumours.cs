using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimPrison
{
    /// <summary>What a rumour grew out of (the keys are <c>RP_Rumour_{kind}_True</c> and <c>_False</c>: the thing as it was, and the version the wing made of it).</summary>
    public enum RumourKind : byte
    {
        CellSearch,
        Transfer,
        Death,
        YardOff,
        EarlyLockup,
        /// <summary>A restriction of the wing (the riot reckoning's answer: see <see cref="Rumours.Notify_Restriction"/>).</summary>
        Restriction,
        FoodScare,
        Strike,
        Supermax,
    }

    /// <summary>How a rumour ended (<see cref="Live"/> while it runs).</summary>
    public enum RumourEnd : byte
    {
        Live,
        /// <summary>It ran its course (<see cref="Rumours.LifeDays"/>, and a day more for each firm address).</summary>
        BurntOut,
        /// <summary>A calm address in the canteen showed it up for what it was (a false one only).</summary>
        Calmed,
        /// <summary>Something worse happened on the wing and the talk moved on to it.</summary>
        Replaced,
        /// <summary>Nobody of the wing was left to tell it.</summary>
        Emptied,
    }

    /// <summary>One rumour on one wing (a level). Saved in <see cref="PrisonMap.rumours"/>; who has heard it is not stored, the wing's share is (<see cref="reach"/>).</summary>
    public class Rumour : IExposable
    {
        public RumourKind kind;
        public SecurityLevelDef level;
        /// <summary>Who started it (the walk and an informant can name them), by id and name: the record outlives them.</summary>
        public int originId = -1;
        public string originName;
        /// <summary>Whether what it says holds (the warden does not know until <see cref="truthKnown"/>).</summary>
        public bool isTrue;
        /// <summary>
        /// The words: the thing as it was (the yard was called off) or the version the wing made of it (the yard is being closed for good). Mostly the opposite of <see cref="isTrue"/>, but not always: the distorted "the guards
        /// killed X" is the truth when a member of staff did, and the plain "the yard is off today" stops holding once the order is lifted. The words are what is shown, and what winds the wing up.
        /// </summary>
        public bool distorted;
        /// <summary>The share of the wing that has heard it (0-1): it grows at the hourly room meetings (<see cref="Rumours.Hourly"/>).</summary>
        public float reach;
        public int startTick;
        /// <summary>The tick it has run its course at: <see cref="Rumours.LifeDays"/> from the start, a day later for each firm address.</summary>
        public int expireTick;
        /// <summary>The tick it ended at (-1: live), and how.</summary>
        public int endTick = -1;
        public RumourEnd end;
        /// <summary>Who it is about, when it is about someone (the dead, the transferred, the one sent to supermax, the owner of the searched cell, one of the sick).</summary>
        public string subject;
        /// <summary>The warden knows whether it is true (a calm address, an informant), and who started it (the walk, an informant).</summary>
        public bool truthKnown, originKnown;

        public bool Live => endTick < 0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Defs.Look(ref level, "level");
            Scribe_Values.Look(ref originId, "originId", -1);
            Scribe_Values.Look(ref originName, "originName");
            Scribe_Values.Look(ref isTrue, "isTrue");
            // (After isTrue: its default is the words a rumour saved without the field had, the opposite of the truth.)
            Scribe_Values.Look(ref distorted, "distorted", !isTrue);
            Scribe_Values.Look(ref reach, "reach");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref expireTick, "expireTick");
            Scribe_Values.Look(ref endTick, "endTick", -1);
            Scribe_Values.Look(ref end, "end");
            Scribe_Values.Look(ref subject, "subject");
            Scribe_Values.Look(ref truthKnown, "truthKnown");
            Scribe_Values.Look(ref originKnown, "originKnown");
        }

        public Rumour Copy() => (Rumour)MemberwiseClone();

        public string Describe() => $"{kind}:{level?.defName}:{originId}:{originName}:{isTrue}:{distorted}:{reach:F3}:{startTick}:{expireTick}:{endTick}:{end}:{subject}:{truthKnown}:{originKnown}";
    }

    /// <summary>
    /// The rumour mill (gap analysis 7, item 5). Things the inmates half see start a rumour on the wing they touch, each at its own chance (<see cref="StartChance"/>): a cell search, a transfer out, a death, the yard called
    /// off or an early lockup, a restriction, a food scare, a staff strike, a send to supermax. A share of them are a distorted version of the thing (<see cref="DistortChance"/>: more when the wing is wound up). One live rumour
    /// per wing (a worse thing replaces a lesser one). It spreads at the hour's room meetings, read off the hourly patch count (<see cref="GangTurf.Sample"/>: no pass over pairs), and fades to nothing over
    /// <see cref="LifeDays"/> days; while it runs every inmate of the wing has a tension cause, "a rumour - ...", scaled by its strength (<see cref="TensionFor"/>). The answers: the warden's address in the canteen where the
    /// wing is (calm ends a false one at once and halves a true one, firm makes either stick a day longer, once), the walk of the wing names who started it, a known informant may tip the origin, or it is let burn out. A true
    /// one is never ended by talk.
    /// </summary>
    public static class Rumours
    {
        public const int MaxKept = 8, KeepEndedDays = 3;
        public const float LifeDays = 2f;
        /// <summary>What a firm address adds to a rumour's life: once per rumour (a second firm word adds nothing, see <see cref="MaxLifeTicks"/>).</summary>
        public const int FirmDays = 1;
        /// <summary>The share of the wing that knows it at the start, and how much of the rest an hour of meetings reaches: share × (the wing that met others of it in a shared place this hour) × (those who had not heard it).</summary>
        public const float StartReach = 0.25f, SpreadPerHour = 0.5f;
        /// <summary>Tension an hour at full strength, for the thing as it was and for a distorted version (the words wind the wing up, whether or not they hold).</summary>
        public const float TensionPerHour = 1f, DistortedTensionPerHour = 1.5f;
        /// <summary>An address answers a wing's rumour only when at least this many of the wing heard it (or the whole wing, when it is smaller): one stray inmate in another wing's canteen does not answer for theirs.</summary>
        public const int AddressMinHeard = 2;
        /// <summary>The chance a rumour is the distorted version: <see cref="DistortLow"/> on a wing at a mean tension of <see cref="DistortCalmAt"/> or under, up to <see cref="DistortHigh"/> at <see cref="DistortTenseAt"/>.</summary>
        public const float DistortLow = 0.25f, DistortHigh = 0.75f, DistortCalmAt = 30f, DistortTenseAt = 80f;
        /// <summary>The chance a known informant's tip is about the origin of the rumour on their own wing, when there is one they did not start and the warden does not know who did.</summary>
        public const float TipChance = 0.5f;
        /// <summary>The strength bands: faint under <see cref="SpreadingAt"/>, spreading under <see cref="EverywhereAt"/>, all over the wing from there.</summary>
        public const float SpreadingAt = 0.25f, EverywhereAt = 0.6f;
        /// <summary>A wing needs this many inmates for a rumour: one to start it and someone to tell.</summary>
        public const int MinWing = 2;

        public static int LifeTicks => Mathf.RoundToInt(LifeDays * GenDate.TicksPerDay);

        /// <summary>The longest a rumour runs from its start: its life and one firm address's day (firm addresses do not stack).</summary>
        public static int MaxLifeTicks => LifeTicks + FirmDays * GenDate.TicksPerDay;

        /// <summary>Whether a rumour can be told on the level: not supermax's walks, the padded cells or a referral's ward (their inmates never meet, nor reach a canteen).</summary>
        public static bool CanTell(SecurityLevelDef level) => level != null && !level.escortOnly && !level.paddedOnly && !level.referralOnly;

        /// <summary>For a test: whether a source starts a rumour, whether it is distorted, whether an informant's tip is about the origin (null: the dice).</summary>
        internal static bool? ForceStart, ForceDistort, ForceTip;

        private static int Now => Find.TickManager.TicksGame;

        /// <summary>The chance each source starts a rumour on the wing it touches (the more serious the thing, the likelier): also its rank, a worse thing replacing a lesser rumour.</summary>
        public static float StartChance(RumourKind kind) => kind switch
        {
            RumourKind.CellSearch => 0.15f,
            RumourKind.Transfer => 0.4f,
            RumourKind.Death => 0.75f,
            RumourKind.YardOff => 0.5f,
            RumourKind.EarlyLockup => 0.4f,
            RumourKind.Restriction => 0.6f,
            RumourKind.FoodScare => 0.6f,
            RumourKind.Strike => 0.5f,
            RumourKind.Supermax => 0.6f,
            _ => 0f,
        };

        public static float DistortChance(float meanTension) => Mathf.Lerp(DistortLow, DistortHigh, Mathf.InverseLerp(DistortCalmAt, DistortTenseAt, meanTension));

        public static string WingName(SecurityLevelDef level) => level?.labelShort ?? level?.label ?? "?";

        /// <summary>What the wing says, in its words: the thing as it was or the version they made of it (<see cref="Rumour.distorted"/>, not whether it holds: the words never tell the truth of it).</summary>
        public static string Text(Rumour r) => ("RP_Rumour_" + r.kind + (r.distorted ? "_False" : "_True")).Translate(r.subject ?? "?").Resolve();

        /// <summary>The strength now: the share that has heard it, fading over the life left (a firm address can hold it at full for a while).</summary>
        public static float Strength(Rumour r, int now) => !r.Live ? 0f : r.reach * Mathf.Clamp01((r.expireTick - now) / (float)LifeTicks);

        public static string BandKey(float strength) => strength < SpreadingAt ? "RP_Rumour_BandFaint" : strength < EverywhereAt ? "RP_Rumour_BandSpreading" : "RP_Rumour_BandEverywhere";

        public static Color BandColor(float strength) => strength < SpreadingAt ? PrisonHUD.Muted : strength < EverywhereAt ? PrisonHUD.Warn : PrisonHUD.Bad;

        /// <summary>The live rumour on a level of the map, or null (at most <see cref="MaxKept"/> to look through).</summary>
        public static Rumour LiveOn(PrisonMap pm, SecurityLevelDef level)
        {
            if (pm == null || level == null) return null;
            List<Rumour> list = pm.rumours;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Live && list[i].level == level) return list[i];
            return null;
        }

        public static Rumour LiveOn(Map map, SecurityLevelDef level) => LiveOn(PrisonMap.Of(map), level);

        /// <summary>The live rumour this inmate's wing is telling, or null.</summary>
        public static Rumour LiveFor(CompInmate c) => c?.parent is Pawn p && p.MapHeld is { } map ? LiveOn(map, c.security) : null;

        /// <summary>
        /// The rumour's term of the hour's tension for an inmate of its wing (0 when the wing tells none): its strength at <see cref="TensionPerHour"/> (a distorted version at <see cref="DistortedTensionPerHour"/>: what winds the
        /// wing up is what it is saying, and the size of the cause gives nothing away about whether it holds).
        /// </summary>
        public static float TensionFor(Pawn p, CompInmate c, PrisonMap pm)
        {
            if (pm == null || pm.rumours.Count == 0 || LiveOn(pm, c.security) is not { } r) return 0f;
            return Strength(r, Now) * (r.distorted ? DistortedTensionPerHour : TensionPerHour);
        }

        // ---- starting ---------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The active inmates of a level on the map (not those on their way out), and their mean tension.</summary>
        private static List<Pawn> WingOf(Map map, SecurityLevelDef level, out float meanTension)
        {
            var wing = new List<Pawn>();
            float sum = 0f;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!InmateUtility.IsActiveInmate(p) || p.Inmate() is not { } c || c.security != level || ShuttleSystem.OnTheWayOut(p)) continue;
                wing.Add(p);
                sum += c.tension;
            }
            meanTension = wing.Count > 0 ? sum / wing.Count : 0f;
            return wing;
        }

        /// <summary>
        /// Something the inmates half saw happened on the wing: a rumour may start (<see cref="StartChance"/>), distorted at <see cref="DistortChance"/>. <paramref name="subject"/>: who it is about (named in the text);
        /// <paramref name="exclude"/>: who cannot be the one who started it (the one it is about). <paramref name="distortedIsTrue"/>: the distorted version happens to be what happened (an inmate the guards did kill). Returns the
        /// rumour started, or null.
        /// </summary>
        public static Rumour Notify_Source(Map map, SecurityLevelDef level, RumourKind kind, string subject = null, Pawn exclude = null, bool distortedIsTrue = false)
        {
            if (map == null || !map.IsPlayerHome || !CanTell(level) || PrisonMap.Of(map) is not { } pm) return null;
            if (!(ForceStart ?? Rand.Chance(StartChance(kind)))) return null;
            List<Pawn> wing = WingOf(map, level, out float mean);
            if (exclude != null) wing.Remove(exclude);
            if (wing.Count < MinWing) return null;
            bool distorted = ForceDistort ?? Rand.Chance(DistortChance(mean));
            List<Pawn> awake = wing.FindAll(p => p.Awake() && !p.Downed && !p.InMentalState);
            Pawn origin = (awake.Count > 0 ? awake : wing).RandomElement();
            return Start(pm, level, kind, isTrue: !distorted || distortedIsTrue, subject, origin, distorted);
        }

        /// <summary>
        /// Starts the rumour on the wing (one live per wing: a live one is replaced only by a worse thing, or once it has faded under half the strength a new one starts at; else nothing starts and null is returned). Written in the
        /// chronicle as the wing tells it. <paramref name="distorted"/>: the words (null: the distorted version exactly when it is not true).
        /// </summary>
        public static Rumour Start(PrisonMap pm, SecurityLevelDef level, RumourKind kind, bool isTrue, string subject, Pawn origin, bool? distorted = null)
        {
            int now = Now;
            if (LiveOn(pm, level) is { } live)
            {
                if (StartChance(kind) <= StartChance(live.kind) && Strength(live, now) >= StartReach * 0.5f) return null;
                End(live, RumourEnd.Replaced, now);
            }
            var r = new Rumour
            {
                kind = kind, level = level, isTrue = isTrue, distorted = distorted ?? !isTrue, subject = subject, reach = StartReach, startTick = now, expireTick = now + LifeTicks,
                originId = origin?.thingIDNumber ?? -1, originName = origin?.LabelShort,
            };
            pm.rumours.Add(r);
            Prune(pm, now);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_RumourStarted".Translate(WingName(level), Text(r)));
            // (A false one: someone of the wing who knows better asks to see the warden about the talk, see WardenAsks.)
            if (!isTrue) WardenAsks.Notify_FalseRumour(pm.map, level, r.originId);
            return r;
        }

        /// <summary>
        /// A regime order that keeps the wing in was given (<see cref="RegimeOrders.Give"/>, <see cref="RegimeOrders.Restrict"/>): it may start a rumour. When the wing already tells one of this order (given again the same day,
        /// after it was lifted), nothing is rolled again: the plain version of it holds again (no re-roll by toggling the order).
        /// </summary>
        public static Rumour Notify_Order(Map map, SecurityLevelDef level, RumourKind kind)
        {
            if (LiveOn(map, level) is { } live && live.kind == kind)
            {
                if (!live.distorted) live.isTrue = true;
                return null;
            }
            return Notify_Source(map, level, kind);
        }

        /// <summary>The riot reckoning's restriction (yard off and an early lockup for days, <see cref="RegimeOrders.Restrict"/>): one roll for the restriction, not one for each of its two orders.</summary>
        public static Rumour Notify_Restriction(Map map, SecurityLevelDef level) => Notify_Order(map, level, RumourKind.Restriction);

        /// <summary>
        /// The order a live rumour of the level is about is over (lifted by the warden, or lapsed at midnight; a restriction lifted or run out): what the wing says no longer holds. The rumour goes on, now false and known to be
        /// (the warden knows their own orders), so a calm word can end it; it is not ended here (toggling the order is no way to silence the wing).
        /// </summary>
        public static void Notify_OrderOver(SecurityLevelDef level, bool yardOff, bool earlyLockup, bool restriction)
        {
            if (level == null) return;
            foreach (Map m in Find.Maps)
            {
                if (!m.IsPlayerHome || LiveOn(m, level) is not { } r) continue;
                if (r.kind == RumourKind.YardOff && yardOff || r.kind == RumourKind.EarlyLockup && earlyLockup || r.kind == RumourKind.Restriction && restriction) (r.isTrue, r.truthKnown) = (false, true);
            }
        }

        /// <summary>For each wing, the shakedown (<see cref="PrisonGame.lastShakedownTick"/>) its cell searches rolled for already. Not saved: a load in the middle of a shakedown may give a wing a second roll.</summary>
        internal static readonly Dictionary<SecurityLevelDef, int> shakedownRolled = new Dictionary<SecurityLevelDef, int>();

        /// <summary>
        /// A cell was turned over (<see cref="TunnelSystem.SearchCell"/>): the owner's wing saw it. During a shakedown, which turns over every cell, the wing rolls once for the whole of it (else a twenty-cell wing would be all but
        /// sure to start one); the owner may be the one who starts it.
        /// </summary>
        public static Rumour Notify_CellSearch(Pawn owner)
        {
            if (owner?.Inmate()?.security is not { } level) return null;
            if (PrisonGame.Instance is { } game && game.shakedownUntilTick > Now)
            {
                if (shakedownRolled.TryGetValue(level, out int at) && at == game.lastShakedownTick) return null;
                shakedownRolled[level] = game.lastShakedownTick;
            }
            return Notify_Source(owner.MapHeld, level, RumourKind.CellSearch, owner.LabelShort);
        }

        /// <summary>A death on the wing: the wing it touches is the dead one's. <paramref name="byStaff"/>: a member of staff killed them (the distorted "the guards killed X" is then the truth).</summary>
        public static Rumour Notify_Death(Pawn victim, Map map, bool byStaff) =>
            victim.Inmate()?.security is { } level ? Notify_Source(map, level, RumourKind.Death, victim.LabelShort, victim, distortedIsTrue: byStaff) : null;

        /// <summary>A staff strike touches every wing: each with inmates rolls for its own.</summary>
        public static void Notify_Strike(Map map)
        {
            foreach (SecurityLevelDef level in OccupiedLevels(map)) Notify_Source(map, level, RumourKind.Strike);
        }

        /// <summary>A food scare touches the wings of the sick: each rolls once, named after one of its sick.</summary>
        public static void Notify_FoodScare(Map map, List<Pawn> sick)
        {
            var done = new HashSet<SecurityLevelDef>();
            foreach (Pawn p in sick)
                if (p.Inmate()?.security is { } level && done.Add(level)) Notify_Source(map, level, RumourKind.FoodScare, p.LabelShort, p);
        }

        private static List<SecurityLevelDef> OccupiedLevels(Map map)
        {
            var levels = new List<SecurityLevelDef>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (InmateUtility.IsActiveInmate(p) && p.Inmate()?.security is { } level && !levels.Contains(level)) levels.Add(level);
            return levels;
        }

        public static void End(Rumour r, RumourEnd how, int now)
        {
            if (!r.Live) return;
            r.endTick = now;
            r.end = how;
        }

        /// <summary>Ended ones older than <see cref="KeepEndedDays"/> go; then the oldest ended ones while there are more than <see cref="MaxKept"/>.</summary>
        private static void Prune(PrisonMap pm, int now)
        {
            pm.rumours.RemoveAll(r => !r.Live && now - r.endTick > KeepEndedDays * GenDate.TicksPerDay);
            while (pm.rumours.Count > MaxKept)
            {
                int i = pm.rumours.FindIndex(r => !r.Live);
                pm.rumours.RemoveAt(i >= 0 ? i : 0);
            }
        }

        // ---- the hour: the spread and the end ---------------------------------------------------------------------------------------------------------

        /// <summary>The hour's count, filled by <see cref="GangTurf.Sample"/> (one pass over the inmates it makes anyway): the inmates of each level counted, and of each level in each shared patch.</summary>
        private static readonly Dictionary<SecurityLevelDef, int> wingCount = new Dictionary<SecurityLevelDef, int>();
        private static readonly Dictionary<long, int> patchCount = new Dictionary<long, int>();
        private static bool sampling;

        /// <summary>Before the patch count's pass: whether the mill counts this hour (only with a live rumour on the map; the counts of the last hour go).</summary>
        public static bool BeginSample(PrisonMap pm)
        {
            wingCount.Clear();
            patchCount.Clear();
            sampling = false;
            if (pm == null) return false;
            for (int i = 0; i < pm.rumours.Count && !sampling; i++) sampling = pm.rumours[i].Live;
            return sampling;
        }

        /// <summary>An inmate of the level counted in the hour's pass, in the shared patch <paramref name="patch"/> (-1: not in one).</summary>
        public static void Saw(SecurityLevelDef level, int patch)
        {
            if (!sampling || level == null) return;
            wingCount[level] = wingCount.TryGetValue(level, out int n) ? n + 1 : 1;
            if (patch < 0) return;
            long key = ((long)level.index << 32) | (uint)patch;
            patchCount[key] = patchCount.TryGetValue(key, out int m) ? m + 1 : 1;
        }

        /// <summary>The share of the level's counted inmates who stood in a shared patch with at least one other of the level this hour (the room meetings a rumour passes at).</summary>
        public static float MetShare(SecurityLevelDef level)
        {
            if (!wingCount.TryGetValue(level, out int all) || all == 0) return 0f;
            int met = 0;
            foreach (KeyValuePair<long, int> kv in patchCount)
                if ((int)(kv.Key >> 32) == level.index && kv.Value >= 2) met += kv.Value;
            return Mathf.Clamp01(met / (float)all);
        }

        /// <summary>
        /// The hour (after the patch count): each live rumour reaches more of its wing by the meetings of the hour, and ends when it has run its course or its wing is empty. Ended ones past their keeping go. Nothing per pair:
        /// a pass over the patch counts per live rumour.
        /// </summary>
        public static void Hourly(Map map, PrisonMap pm)
        {
            if (pm == null || pm.rumours.Count == 0) return;
            int now = Now;
            foreach (Rumour r in pm.rumours)
            {
                if (!r.Live) continue;
                if (now >= r.expireTick)
                {
                    End(r, RumourEnd.BurntOut, now);
                    continue;
                }
                if (!sampling) continue;
                if (!wingCount.ContainsKey(r.level))
                {
                    // (Counted and nobody of it on the map: there is nobody left to tell it.)
                    if (WingOf(map, r.level, out _).Count == 0) End(r, RumourEnd.Emptied, now);
                    continue;
                }
                r.reach = Mathf.Min(1f, r.reach + SpreadPerHour * MetShare(r.level) * (1f - r.reach));
            }
            sampling = false;
            Prune(pm, now);
        }

        // ---- the answers ------------------------------------------------------------------------------------------------------------------------------

        /// <summary>How many of the wing must hear an address for it to answer the wing's rumour: <see cref="AddressMinHeard"/>, or the whole wing when it is smaller.</summary>
        public static int AddressNeeds(Map map, SecurityLevelDef level) => Mathf.Clamp(WingOf(map, level, out _).Count, 1, AddressMinHeard);

        /// <summary>
        /// The warden's address was heard by <paramref name="heard"/>: every live rumour of a wing of which enough heard it (<see cref="AddressNeeds"/>) is answered. Calm: a false one ends at once, a true one loses half its reach
        /// (and the warden knows which it was); firm: either sticks a day longer, once per rumour (<see cref="MaxLifeTicks"/>). A live rumour the address did not reach is said in one message. Returns how many were answered (each
        /// with a message and a line).
        /// </summary>
        public static int Notify_Address(Map map, List<Pawn> heard, bool firm)
        {
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null || pm.rumours.Count == 0 || heard.Count == 0) return 0;
            var levels = new Dictionary<SecurityLevelDef, int>();
            foreach (Pawn p in heard)
                if (p.Inmate()?.security is { } l) levels[l] = levels.TryGetValue(l, out int n) ? n + 1 : 1;
            int now = Now, answered = 0;
            var missed = new List<string>();
            foreach (Rumour r in pm.rumours)
            {
                if (!r.Live) continue;
                string wing = WingName(r.level), text = Text(r);
                if (!levels.TryGetValue(r.level, out int of) || of < AddressNeeds(map, r.level))
                {
                    missed.Add(wing);
                    continue;
                }
                answered++;
                if (firm)
                {
                    int was = r.expireTick;
                    r.expireTick = Mathf.Min(r.expireTick + FirmDays * GenDate.TicksPerDay, r.startTick + MaxLifeTicks);
                    if (r.expireTick > was) Tell("RP_Rumour_FirmSticks".Translate(wing, text, FirmDays), "RP_Chr_RumourFirm".Translate(wing, text));
                    else Messages.Message("RP_Rumour_FirmAgain".Translate(wing, text), MessageTypeDefOf.NeutralEvent, historical: false);
                    continue;
                }
                r.truthKnown = true;
                if (!r.isTrue)
                {
                    End(r, RumourEnd.Calmed, now);
                    Tell("RP_Rumour_CalmEnds".Translate(wing, text), "RP_Chr_RumourCalmed".Translate(wing, text));
                }
                else
                {
                    r.reach *= 0.5f;
                    Tell("RP_Rumour_CalmTrue".Translate(wing, text), "RP_Chr_RumourCalmTrue".Translate(wing, text));
                }
            }
            // (The address was spent and the talk on these wings goes on: too few of them were in the canteen.)
            if (missed.Count > 0) Messages.Message("RP_Rumour_AddressMissed".Translate(string.Join(", ", missed), AddressMinHeard), MessageTypeDefOf.NeutralEvent, historical: false);
            return answered;
        }

        /// <summary>
        /// The warden walked the wing (the zone of <paramref name="level"/>) and met <paramref name="met"/>: if the wing tells a rumour and someone of it was met, who started it is named (once). Returns whether it was.
        /// </summary>
        public static bool Notify_Walked(Map map, SecurityLevelDef level, List<Pawn> met)
        {
            if (LiveOn(map, level) is not { originKnown: false } r || !met.Any(p => p.Inmate()?.security == level)) return false;
            r.originKnown = true;
            string wing = WingName(level), text = Text(r);
            Tell("RP_Rumour_WalkNames".Translate(wing, text, r.originName ?? "?"), "RP_Chr_RumourOrigin".Translate(wing, text, r.originName ?? "?"));
            return true;
        }

        /// <summary>
        /// A known informant's tip about the rumour on their own wing (<see cref="TipChance"/>): who started it, and whether it holds. Null when there is none to tell (no live rumour, the origin known already, the informant
        /// started it, the origin out of the tip's pool) or the dice say no.
        /// </summary>
        internal static string TryTip(Pawn snitch, CompInmate sc)
        {
            if (snitch.MapHeld is not { } map || LiveOn(map, sc.security) is not { originKnown: false } r || r.originId == snitch.thingIDNumber) return null;
            if (InformantSystem.TipPool != null && (CourtUtil.FindInmate(map, r.originId) is not { } origin || !InformantSystem.TipPool(origin))) return null;
            if (!(ForceTip ?? Rand.Chance(TipChance))) return null;
            r.originKnown = r.truthKnown = true;
            return (r.isTrue ? "RP_TipRumourTrue" : "RP_TipRumourFalse").Translate(r.originName ?? "?", Text(r), WingName(r.level));
        }

        /// <summary>The riot's line about the talk on the wing when it began (null: the wing told none), for its chronicle line and letter. The riot reckoning's record keeps a copy of the rumour itself (<see cref="RiotRecord.rumour"/>).</summary>
        public static string RiotLine(Map map, SecurityLevelDef level) =>
            LiveOn(map, level) is { } r ? "RP_Rumour_RiotLine".Translate(WingName(level), Text(r), (r.truthKnown ? (r.isTrue ? "RP_Rumour_True" : "RP_Rumour_False") : "RP_Rumour_Unknown").Translate()).Resolve() : null;

        private static void Tell(string message, string line)
        {
            Messages.Message(message, MessageTypeDefOf.NeutralEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, line);
        }

        // ---- the warden's orders from Intel -----------------------------------------------------------------------------------------------------------

        /// <summary>The wing zone of a level (painted, with cells), or null: where a walk of it goes. <paramref name="wings"/>: <see cref="WardenSystem.Wings"/> worked out already (null: worked out here).</summary>
        public static PrisonZoneDef WingZone(Map map, SecurityLevelDef level, List<PrisonZoneDef> wings = null)
        {
            foreach (PrisonZoneDef z in wings ?? WardenSystem.Wings(map))
                if (z.security == level) return z;
            return null;
        }

        /// <summary>The cell of <paramref name="cells"/> nearest their middle (invalid when there are none).</summary>
        private static IntVec3 Middle(List<IntVec3> cells)
        {
            if (cells == null || cells.Count == 0) return IntVec3.Invalid;
            Vector3 sum = Vector3.zero;
            foreach (IntVec3 c in cells) sum += c.ToVector3();
            Vector3 middle = sum / cells.Count;
            IntVec3 best = cells[0];
            float bestDist = float.MaxValue;
            foreach (IntVec3 c in cells)
            {
                float d = (c.ToVector3() - middle).sqrMagnitude;
                if (d < bestDist) (best, bestDist) = (c, d);
            }
            return best;
        }

        /// <summary>
        /// Where the warden stands to address the canteen of the wing: of the canteens the warden can reach, the one with most of the wing's inmates in it now, then the one with most cells the level may use, then the nearest to
        /// the middle of the wing's own zone; the cell of it nearest its middle. Invalid when there is none.
        /// </summary>
        public static IntVec3 CanteenSpotFor(Pawn warden, SecurityLevelDef level)
        {
            Map map = warden.MapHeld;
            PrisonMap pm = PrisonMap.Of(map);
            if (pm == null) return IntVec3.Invalid;
            IntVec3 wingAt = WingZone(map, level) is { } zone ? Middle(pm.zones.CellsOf(zone)) : IntVec3.Invalid;
            if (!wingAt.IsValid) wingAt = warden.PositionHeld;
            var inmates = new List<IntVec3>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (InmateUtility.IsActiveInmate(p) && p.Inmate()?.security == level) inmates.Add(p.Position);
            var scored = new List<(List<IntVec3> block, int here, int allowed, int dist)>();
            foreach (List<IntVec3> block in WardenSystem.Canteens(map))
            {
                var cells = new HashSet<IntVec3>(block);
                scored.Add((block, inmates.Count(cells.Contains), block.Count(c => pm.zones.Allows(c, level)), block.Min(c => (c - wingAt).LengthHorizontalSquared)));
            }
            foreach ((List<IntVec3> block, _, _, _) in scored.OrderByDescending(s => s.here).ThenByDescending(s => s.allowed).ThenBy(s => s.dist))
            {
                Vector3 sum = Vector3.zero;
                foreach (IntVec3 c in block) sum += c.ToVector3();
                Vector3 middle = sum / block.Count;
                IntVec3 best = IntVec3.Invalid;
                float bestDist = float.MaxValue;
                foreach (IntVec3 c in block)
                {
                    float d = (c.ToVector3() - middle).sqrMagnitude;
                    if (d >= bestDist || !c.Standable(map) || !warden.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) continue;
                    (best, bestDist) = (c, d);
                }
                if (best.IsValid) return best;
            }
            return IntVec3.Invalid;
        }

        /// <summary>Intel's address button: the warden goes to the canteen of the wing (<see cref="CanteenSpotFor"/>) and speaks there.</summary>
        public static bool OrderAddress(Pawn warden, SecurityLevelDef level, bool firm)
        {
            IntVec3 spot = CanteenSpotFor(warden, level);
            if (!spot.IsValid)
            {
                Messages.Message("RP_WardenWhy_NoCanteen".Translate(), warden, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            return WardenOrders.AddressAt(warden, spot, firm);
        }

        /// <summary>Why Intel's address button is off (resolved), or null.</summary>
        public static string AddressUnready(Pawn warden)
        {
            if (warden == null) return "RP_Rumour_NoWarden".Translate();
            if (warden.GetComp<CompStaff>() is not { } sc) return "RP_Rumour_NoWarden".Translate();
            return WardenSystem.Unready(warden, sc.addressReadyTick) ?? (WardenSystem.Canteens(warden.MapHeld).Count == 0 ? "RP_WardenWhy_NoCanteen".Translate().Resolve() : null);
        }

        /// <summary>Why Intel's walk button is off (resolved), or null. <paramref name="wings"/>: <see cref="WardenSystem.Wings"/> worked out already (Intel works it out once a frame, not once a row).</summary>
        public static string WalkUnready(Pawn warden, SecurityLevelDef level, List<PrisonZoneDef> wings = null)
        {
            if (warden == null || warden.GetComp<CompStaff>() is not { } sc) return "RP_Rumour_NoWarden".Translate();
            return WardenSystem.Unready(warden, sc.walkReadyTick) ?? (WingZone(warden.MapHeld, level, wings) == null ? "RP_Rumour_NoWingZone".Translate(WingName(level)).Resolve() : null);
        }

        // ---- what the screens show --------------------------------------------------------------------------------------------------------------------

        public static string TruthKey(Rumour r) => !r.truthKnown ? "RP_Rumour_Unknown" : r.isTrue ? "RP_Rumour_True" : "RP_Rumour_False";

        /// <summary>Intel's line of one rumour: the wing, the text, true or false once known, the band (or how it ended).</summary>
        public static string IntelLine(Rumour r, int now) =>
            "RP_Rumour_IntelLine".Translate(WingName(r.level), Text(r), TruthKey(r).Translate(), r.Live ? BandKey(Strength(r, now)).Translate() : ("RP_Rumour_Ended" + r.end).Translate()).Resolve();

        /// <summary>The rumours Intel lists: the live ones (strongest first), then those ended in the last day.</summary>
        public static List<Rumour> Shown(PrisonMap pm, int now)
        {
            if (pm == null) return new List<Rumour>();
            return pm.rumours.Where(r => r.Live || now - r.endTick < GenDate.TicksPerDay).OrderByDescending(r => r.Live).ThenByDescending(r => Strength(r, now)).ThenByDescending(r => r.endTick).ToList();
        }

        /// <summary>The file's line: what the inmate's wing is saying (and that they started it, once the warden knows).</summary>
        public static (string, Color)? FileLine(Pawn p, CompInmate c, int now)
        {
            if (LiveFor(c) is not { } r) return null;
            float strength = Strength(r, now);
            string line = "RP_Rumour_FileLine".Translate(Text(r), BandKey(strength).Translate()).Resolve();
            if (r.originKnown && r.originId == p.thingIDNumber) line += " " + "RP_Rumour_FileOrigin".Translate().Resolve();
            return (line, BandColor(strength));
        }

        /// <summary>For the saveload snapshot.</summary>
        public static string Describe(PrisonMap pm) => pm == null ? "" : string.Join(",", pm.rumours.Select(r => r.Describe()));
    }
}
