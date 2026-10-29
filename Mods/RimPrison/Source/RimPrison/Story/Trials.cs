using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPTrialDefOf
    {
        public static ThoughtDef RP_TrialJustice;
        public static ThoughtDef RP_TrialAcquitted;
        public static ThoughtDef RP_HatesSnitch;

        static RPTrialDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPTrialDefOf));
    }

    /// <summary>One person who could speak at a murder trial: kept by id and name, like the chronicle does, so the case outlives them.</summary>
    public class TrialWitness : IExposable
    {
        public int id = -1;
        public string name;
        /// <summary>A member of staff (reliable, but see <see cref="Trials.Unreliable"/>), not an inmate.</summary>
        public bool staff;
        /// <summary>The warden knows they saw it: staff report at once, the inmates are named when the prosecution assembles its case (or when they come forward).</summary>
        public bool known;
        /// <summary>How hard they have been got at (a threat 0.5, a beating 1), and whether they told the guards so. Hidden until it comes out.</summary>
        public float pressure;
        public bool pressureKnown;
        /// <summary>What the court heard (set at the verdict).</summary>
        public bool recanted, unreliable;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id", -1);
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref staff, "staff");
            Scribe_Values.Look(ref known, "known");
            Scribe_Values.Look(ref pressure, "pressure");
            Scribe_Values.Look(ref pressureKnown, "pressureKnown");
            Scribe_Values.Look(ref recanted, "recanted");
            Scribe_Values.Look(ref unreliable, "unreliable");
        }
    }

    /// <summary>
    /// A killing inside, on its way to court. Everything the prosecution has was fixed at the moment of the killing (who was in sight, what a camera covered, whether
    /// the killer was caught at the scene or with the weapon, their record); what the warden knows of it is a different list (see <see cref="Trials.KnownCase"/>).
    /// </summary>
    public class MurderTrial : IExposable
    {
        public int id;
        public int killerId = -1, victimId = -1;
        public string killerName, victimName;
        public bool staffVictim;
        /// <summary>What the accused is charged with: murder (the default, and what a case from before charges had), or another offence the sheet put to the court (see <see cref="Adjudication"/>).</summary>
        public MisconductDef offence;
        /// <summary>The prosecution has assembled its case: the warden now knows the camera, the weapon and the inmate witnesses.</summary>
        public bool assembled;
        /// <summary>Hidden until assembled (a guard on the scene is known at once): the footage, the weapon.</summary>
        public bool caughtAtScene, camera, weapon;
        /// <summary>Serious offences on their file before the killing.</summary>
        public int priorRecord;
        public int chargedTick, assembleTick, trialTick, courtTick = -1;
        /// <summary>Held in their cell for the day the court sits off-site (the room is not theirs to leave: see <see cref="Trials.TakeToCourt"/>).</summary>
        public bool courtLock;
        /// <summary>When the court's own lock-up was to end (a real sanction imposed meanwhile extends the confinement past it, and the end of the court day leaves that alone).</summary>
        public int courtLockUntil = -1;
        /// <summary>When the accused escaped and the police went looking (-1 none): the case waits for the recapture.</summary>
        public int fledTick = -1;
        /// <summary>A known informant the warden asked to testify (by id and name), exposed at the trial.</summary>
        public int informantId = -1;
        public string informantName;
        public List<TrialWitness> witnesses = new List<TrialWitness>();
        /// <summary>The victim's friends and gang when it happened: they want the killer if the court lets them go.</summary>
        public List<int> avengers = new List<int>();

        /// <summary>A killing, not another charge.</summary>
        public bool IsMurder => offence == null || offence == RPDeathDefOf.RP_Murder;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Defs.Look(ref offence, "offence");
            Scribe_Values.Look(ref killerId, "killerId", -1);
            Scribe_Values.Look(ref victimId, "victimId", -1);
            Scribe_Values.Look(ref killerName, "killerName");
            Scribe_Values.Look(ref victimName, "victimName");
            Scribe_Values.Look(ref staffVictim, "staffVictim");
            Scribe_Values.Look(ref assembled, "assembled");
            Scribe_Values.Look(ref caughtAtScene, "caughtAtScene");
            Scribe_Values.Look(ref camera, "camera");
            Scribe_Values.Look(ref weapon, "weapon");
            Scribe_Values.Look(ref priorRecord, "priorRecord");
            Scribe_Values.Look(ref chargedTick, "charged");
            Scribe_Values.Look(ref assembleTick, "assemble");
            Scribe_Values.Look(ref trialTick, "trial");
            Scribe_Values.Look(ref courtTick, "court", -1);
            Scribe_Values.Look(ref courtLock, "courtLock");
            Scribe_Values.Look(ref courtLockUntil, "courtLockUntil", -1);
            Scribe_Values.Look(ref fledTick, "fled", -1);
            Scribe_Values.Look(ref informantId, "informantId", -1);
            Scribe_Values.Look(ref informantName, "informantName");
            Scribe_Collections.Look(ref witnesses, "witnesses", LookMode.Deep);
            Scribe_Collections.Look(ref avengers, "avengers", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                offence ??= RPDeathDefOf.RP_Murder;
                witnesses ??= new List<TrialWitness>();
                witnesses.RemoveAll(w => w == null);
                avengers ??= new List<int>();
            }
        }
    }

    /// <summary>What a witness is worth in court on the day.</summary>
    public enum WitnessState : byte { Gone, Testifies, Recants, Unreliable }

    /// <summary>
    /// The murder trial. The killer is charged (the record, the higher level, the grudge of the victim's friends: <see cref="DeathAftermath.ChargeWithMurder"/>) and a date is
    /// set four days out. What the prosecution has comes from what really happened: the staff and inmates in sight, a camera over the spot, a guard at the scene, the weapon,
    /// the killer's record. A day after the charge it is assembled, and the warden is told what it has; until then only the staff who saw it are known. Before the trial the
    /// killer's gang (and the killer, if free) get at inmate witnesses: a threat or a beating, so a witness may recant on the stand. The warden can move witnesses to
    /// protective custody (nobody gets at them there) and ask a known informant to testify (a stronger case, and the informant is exposed). A corrupt officer on the killer's
    /// payroll may forget what they saw. On the day (by video from the hearing room if there is one, else the accused is away at court) the case is weighed: convicted
    /// (life or more days, maximum security) or acquitted (nothing added; the victim's friends and gang want revenge, and a strong case lost to tampering costs reputation).
    /// A member of staff's killer is tried the same way, and their colleagues' word counts for more. An open case is remand: the accused is not released (no sentence served, no
    /// parole hearing, no exoneration) until the court has sat; an escapee's case waits for the recapture, and a transfer out closes it at the warden's cost. Everything is kept by id
    /// and name: whoever dies or leaves just drops out.
    /// </summary>
    public static class Trials
    {
        public const int TrialDelayDays = 4;
        public const int AssembleAfterTicks = GenDate.TicksPerDay;
        public const int CourtStartHour = 10, CourtEndHour = 16, LastSittingHour = 17;
        public const int MaxWitnesses = 3, ExtraDays = 30;
        public const float InmateSight = 15f, StaffSight = 20f, CaughtRadius = 6f;
        // What each part of the case is worth (the case convicts at ConvictAt, never below AcquitBelow, and in between by a roll: see ConvictChance and DESIGN.md's odds table).
        // One officer who saw it convicts about two times in three on their own, and surely with the killer caught at the scene or a camera over it; three inmates who testify convict
        // surely, two four times in five, one never: tampering with an inmate witness is what swings a killing nobody on the staff saw.
        public const float StaffWitness = 0.53f, StaffVictimWitness = 0.58f, InmateWitness = 0.28f, Camera = 0.4f, Weapon = 0.15f, CaughtAtScene = 0.2f, Informant = 0.25f;
        public const float RecordEach = 0.05f, RecordMax = 0.15f, AcquitBelow = 0.4f, ConvictAt = 0.6f;
        // Getting at a witness.
        public const float PressureChancePerHour = 0.04f, BeatingChance = 0.25f, ThreatPressure = 0.5f, BeatingPressure = 1f, RecantAt = 1f, ReportChance = 0.35f;
        /// <summary>Reputation lost when a case is lost to tampering, or closed because the warden shipped the accused out.</summary>
        public const float PressHit = CourtUtil.ReputationHit;
        /// <summary>Pressure on a witness in protective custody halves every day they are there (per hour, as a factor).</summary>
        public static readonly float ProtectedHourlyDecay = Mathf.Pow(0.5f, 1f / 24f);

        private static int Now => Find.TickManager.TicksGame;

        public static int DayOf(int tick) => tick / GenDate.TicksPerDay + 1;

        public static string DateText(MurderTrial t) => "RP_TrialDay".Translate(DayOf(t.trialTick)).Resolve();

        public static SecurityLevelDef ProtectiveLevel => RPSecurityDefOf.RP_Protective;

        public static bool IsProtected(CompInmate c) => c?.security != null && c.security == ProtectiveLevel;

        // ---- lookups ---------------------------------------------------------------------------------------------

        /// <summary>An open case against them: they are on remand, and nothing releases them (a served sentence, parole, an exoneration) until the court has sat.</summary>
        public static bool OnRemand(PrisonGame game, Pawn p) => TrialOf(game, p) != null;

        /// <summary>The first court morning at least <paramref name="days"/> days on, at <see cref="CourtStartHour"/> local time (the court sits from then until <see cref="LastSittingHour"/>, so a killing late in the day is not tried a day early).</summary>
        public static int SittingTick(int now, int localHour, int days) => now + days * GenDate.TicksPerDay + (CourtStartHour - localHour + 24) % 24 * GenDate.TicksPerHour;

        /// <summary>The share of cases of this strength that convict: all from <see cref="ConvictAt"/>, none up to <see cref="AcquitBelow"/>, in between by the roll.</summary>
        public static float ConvictChance(float strength) => strength >= ConvictAt ? 1f : strength > AcquitBelow ? (strength - AcquitBelow) / (ConvictAt - AcquitBelow) : 0f;

        public static MurderTrial TrialOf(PrisonGame game, Pawn killer)
        {
            if (killer == null || game == null) return null;
            foreach (MurderTrial t in game.story.trials)
                if (t.killerId == killer.thingIDNumber) return t;
            return null;
        }

        public static MurderTrial ById(PrisonGame game, int id) => game?.story.trials.Find(t => t.id == id);

        // ---- the charge ------------------------------------------------------------------------------------------

        /// <summary>
        /// Opens the case for a killing: the prosecution's evidence is read off the scene as it was (called before the killer's own record of it is written). The caller writes the
        /// charge's letter and chronicle line (<see cref="DeathAftermath.ChargeWithMurder"/>, <see cref="StaffDeaths"/>).
        /// </summary>
        public static MurderTrial Open(PrisonGame game, Map map, Pawn victim, Pawn killer, CompInmate kc, bool staffVictim, DamageInfo? dinfo, List<int> avengers = null, IntVec3? sceneAt = null) =>
            Register(game, map, ReadScene(map, victim, killer, kc, staffVictim, dinfo, avengers, sceneAt), killer);

        /// <summary>
        /// What the prosecution has, read off the scene as it is now: the witnesses in sight, a camera over the spot, the weapon, a guard at the scene, the accused's record. The case is not opened
        /// (see <see cref="Register"/>): a charge the sheet may bring later (see <see cref="Adjudication"/>) keeps the scene as it was on the day.
        /// </summary>
        public static MurderTrial ReadScene(Map map, Pawn victim, Pawn killer, CompInmate kc, bool staffVictim, DamageInfo? dinfo, List<int> avengers = null, IntVec3? sceneAt = null, MisconductDef offence = null)
        {
            // (Where it happened: a blow that killed them hours later was struck at the cell the victim was in then, not where they lay down to bleed out.)
            IntVec3 spot = sceneAt is { IsValid: true } at ? at : victim.PositionHeld.IsValid ? victim.PositionHeld : killer.Position;
            var t = new MurderTrial
            {
                id = -1, killerId = killer.thingIDNumber, killerName = killer.LabelShort, victimId = victim.thingIDNumber, victimName = victim.LabelShort,
                staffVictim = staffVictim, offence = offence ?? RPDeathDefOf.RP_Murder,
            };
            // (Bare hands and teeth are not a weapon: the game names the fighter's own race as the weapon of a punch, so only a real weapon counts.)
            t.weapon = dinfo?.Weapon is { IsWeapon: true } || killer.equipment?.Primary is { def.IsWeapon: true };
            t.camera = CourtUtil.CameraOver(map, spot);
            foreach (MisconductRecord r in kc.records)
                if (r.seen && r.def.severity >= 2) t.priorRecord++;
            CollectWitnesses(t, victim, killer, kc, map, spot);
            if (avengers != null) t.avengers.AddRange(avengers);
            return t;
        }

        /// <summary>The case is opened: dated from now (charged now, assembled in a day, the court sitting <see cref="TrialDelayDays"/> days on) and put on the list.</summary>
        public static MurderTrial Register(PrisonGame game, Map map, MurderTrial t, Pawn killer)
        {
            (t.chargedTick, t.assembleTick, t.trialTick) = (Now, Now + AssembleAfterTicks, SittingTick(Now, GenLocalDate.HourOfDay(map), TrialDelayDays));
            string charge = ChargeText(t);
            // A second killing by an accused whose case is open is part of that case (one trial, one sitting): what the scene gave is added to it, and no second case is opened.
            if (TrialOf(game, killer) is { } open)
            {
                foreach (TrialWitness w in t.witnesses)
                    if (!open.witnesses.Exists(x => x.id == w.id) && open.witnesses.Count(x => x.staff == w.staff) < MaxWitnesses) open.witnesses.Add(w);
                (open.camera, open.weapon, open.caughtAtScene) = (open.camera || t.camera, open.weapon || t.weapon, open.caughtAtScene || t.caughtAtScene);
                // (A killing by an accused who is only charged with a lesser offence makes the case a murder case: it is tried for the death.)
                if (!open.IsMurder && t.IsMurder) (open.offence, open.victimId, open.victimName, open.staffVictim) = (t.offence, t.victimId, t.victimName, t.staffVictim);
                foreach (int id in t.avengers)
                    if (!open.avengers.Contains(id)) open.avengers.Add(id);
                if (AutoTest.Active) RPLog.Message($"[RimPrison:trial] {open.killerName} charged with a second offence ({charge}), added to the case for {ChargeText(open)}");
                return open;
            }
            t.id = game.story.nextTrialId++;
            game.story.trials.Add(t);
            Concepts.Teach(Concepts.Trial);
            if (AutoTest.Active) RPLog.Message($"[RimPrison:trial] {t.killerName} charged with {charge}: staff {t.witnesses.Count(w => w.staff)}, inmates {t.witnesses.Count(w => !w.staff)}, camera {t.camera}, weapon {t.weapon}, caught {t.caughtAtScene}, record {t.priorRecord}");
            return t;
        }

        /// <summary>What they are charged with, in a few words (a log line).</summary>
        private static string ChargeText(MurderTrial t) => t.IsMurder ? $"the murder of {t.victimName}" : $"{t.offence.label} ({t.victimName})";

        /// <summary>The keyed string for this case: the one for a charge other than murder is the key and "_Charge" (the words of a killing do not fit an assault; the same arguments).</summary>
        public static string Key(MurderTrial t, string key) => t == null || t.IsMurder ? key : key + "_Charge";

        /// <summary>The days a court adds for this offence (a conviction for a charge other than murder: no life, no move to maximum security).</summary>
        public static int ChargeDays(MisconductDef offence) => offence.chargeDays.max > 0 ? offence.chargeDays.RandomInRange : DefaultChargeDays;

        public const int DefaultChargeDays = 10;

        private static void CollectWitnesses(MurderTrial t, Pawn victim, Pawn killer, CompInmate kc, Map map, IntVec3 spot)
        {
            var staff = new List<(Pawn p, float d)>();
            var inmates = new List<(Pawn p, float d)>();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p == victim || p == killer || p.Dead || p.Downed || !p.RaceProps.Humanlike || !p.Awake()) continue;
                float d = p.Position.DistanceTo(spot);
                if (p.IsPrisonerOfColony)
                {
                    if (d > InmateSight || p.Inmate() is not { } c || !InmateUtility.IsActiveInmate(p) || !CourtUtil.InSight(p, spot, map, InmateSight)) continue;
                    // The killer's own people, and their friends, saw it and will say nothing.
                    if (kc.gang != null && c.gang == kc.gang || PrisonRelations.Friends(p, killer)) continue;
                    inmates.Add((p, d));
                }
                // (Not the riot police and the like, who are called in for a few hours and leave: the case would be weak by the time it is assembled.)
                else if (p.Faction == Faction.OfPlayer && p.GetComp<CompStaff>() is { role: not null, leaveAtTick: < 0 } sc && !CorruptionSystem.Suspended(sc))
                {
                    if (!CourtUtil.InSight(p, spot, map, StaffSight)) continue;
                    staff.Add((p, d));
                }
            }
            foreach ((Pawn p, float d) in staff.OrderBy(x => x.d).Take(MaxWitnesses))
            {
                t.witnesses.Add(new TrialWitness { id = p.thingIDNumber, name = p.LabelShort, staff = true, known = true });
                if (d <= CaughtRadius) t.caughtAtScene = true;
            }
            foreach ((Pawn p, float _) in inmates.OrderBy(x => x.d).Take(MaxWitnesses))
                t.witnesses.Add(new TrialWitness { id = p.thingIDNumber, name = p.LabelShort });
        }

        // ---- what the warden knows ------------------------------------------------------------------------------------

        /// <summary>Whether the witness can still be called: an officer who is still employed, an inmate who is still here (dead, released and transferred ones drop out).</summary>
        private static bool Present(Map map, TrialWitness w) => w.staff ? CourtUtil.FindStaff(map, w.id) != null : CourtUtil.FindInmate(map, w.id) != null;

        /// <summary>
        /// The case as the warden may read it: the officers who saw it and whether one had the killer at the scene; once assembled the footage, the weapon and the inmates who came
        /// forward; the record; the informant, if asked. With <paramref name="map"/>, witnesses who are gone are left out (they cannot testify).
        /// </summary>
        public static List<string> KnownCase(MurderTrial t, Map map = null)
        {
            var lines = new List<string>();
            List<string> staff = t.witnesses.Where(w => w.staff && w.known && (map == null || Present(map, w))).Select(w => w.name).ToList();
            if (staff.Count > 0) lines.Add("RP_TrialKnown_Staff".Translate(string.Join(", ", staff)).Resolve());
            if (t.caughtAtScene) lines.Add("RP_TrialKnown_Caught".Translate(t.killerName).Resolve());
            if (t.assembled && t.camera) lines.Add("RP_TrialKnown_Camera".Translate().Resolve());
            if (t.assembled && t.weapon) lines.Add("RP_TrialKnown_Weapon".Translate().Resolve());
            List<string> inmates = t.witnesses.Where(w => !w.staff && w.known && (map == null || Present(map, w))).Select(w => w.name + (w.pressureKnown ? " " + "RP_TrialThreatenedMark".Translate().Resolve() : "")).ToList();
            if (inmates.Count > 0) lines.Add("RP_TrialKnown_Inmates".Translate(string.Join(", ", inmates)).Resolve());
            if (t.priorRecord > 0) lines.Add(Key(t, "RP_TrialKnown_Record").Translate(t.priorRecord).Resolve());
            if (t.informantId >= 0 && (map == null || CourtUtil.FindInmate(map, t.informantId) != null)) lines.Add("RP_TrialKnown_Informant".Translate(t.informantName).Resolve());
            return lines;
        }

        /// <summary>The known case as a paragraph for a letter (empty when nothing is known yet): a blank line, then one line a fact.</summary>
        public static string KnownLinesText(MurderTrial t, Map map = null)
        {
            List<string> lines = KnownCase(t, map);
            return lines.Count == 0 ? "" : "\n\n" + CourtUtil.Bullets(lines);
        }

        /// <summary>Half way between never convicting and always convicting: the "uncertain" middle is split here, into leaning to an acquittal and to a conviction (the chance of a conviction runs from nothing to certain across it).</summary>
        public const float LeanMid = (AcquitBelow + ConvictAt) / 2f;

        public static string Band(float strength) =>
            strength < AcquitBelow ? "RP_TrialBand_Weak" : strength < ConvictAt ? (strength < LeanMid ? "RP_TrialBand_LeanAcquit" : "RP_TrialBand_LeanConvict") : "RP_TrialBand_Strong";

        /// <summary>How strong the case looks on what the warden knows (the same sum as the court's, over the known parts).</summary>
        public static string KnownBand(MurderTrial t, Map map) => Band(Strength(t, map, wardenView: true));

        private const float ScreenCacheSeconds = 1f;
        private static readonly Dictionary<int, (float at, string text)> bandCache = new Dictionary<int, (float, string)>();
        private static readonly Dictionary<int, (float at, List<string> lines)> fileCache = new Dictionary<int, (float, List<string>)>();

        /// <summary>The band as text, kept for a second per case: the screens ask on every event of every frame, and the sum goes through the witnesses on the map.</summary>
        public static string KnownBandCached(MurderTrial t, Map map)
        {
            float now = Time.realtimeSinceStartup;
            if (bandCache.TryGetValue(t.id, out (float at, string text) hit) && now - hit.at < ScreenCacheSeconds && now >= hit.at) return hit.text;
            string text = KnownBand(t, map).Translate().Resolve();
            bandCache[t.id] = (now, text);
            return text;
        }

        /// <summary>The file's lines of a case for the screens (kept for a second, see <see cref="KnownBandCached"/>).</summary>
        public static List<string> FileLinesCached(Pawn p)
        {
            float now = Time.realtimeSinceStartup;
            if (fileCache.TryGetValue(p.thingIDNumber, out (float at, List<string> lines) hit) && now - hit.at < ScreenCacheSeconds && now >= hit.at) return hit.lines;
            var lines = FileLines(p).ToList();
            fileCache[p.thingIDNumber] = (now, lines);
            return lines;
        }

        /// <summary>Forget what the screens kept (a test changes a case within the second).</summary>
        public static void ForgetScreenCache()
        {
            bandCache.Clear();
            fileCache.Clear();
        }

        // ---- the strength of the case -----------------------------------------------------------------------------------

        public static float Courage(CompInmate c) => (c.HasReputation(ReputationDefOf.RP_Tough) ? 0.3f : 0f) + (c.HasReputation(ReputationDefOf.RP_Volatile) ? 0.1f : 0f);

        /// <summary>A witness who was got at and cannot take it: pressure and what frightens them, less their nerve, reaches <see cref="RecantAt"/>. Fear alone is not enough.</summary>
        public static bool Recants(TrialWitness w, CompInmate wc) => w.pressure > 0f && w.pressure + wc.fear / 200f - Courage(wc) >= RecantAt;

        /// <summary>A corrupt officer on the payroll of the killer (or their gang) does not remember what they saw.</summary>
        public static bool Unreliable(Pawn officer, Pawn killer, CompInmate kc) =>
            killer != null && kc != null && officer.GetComp<CompStaff>() is { corrupt: true, doubleAgent: false } sc && CorruptionSystem.IsClient(sc, killer, kc);

        public static WitnessState StateOf(Map map, MurderTrial t, TrialWitness w, Pawn killer, CompInmate kc)
        {
            if (w.staff)
            {
                if (CourtUtil.FindStaff(map, w.id) is not { } officer) return WitnessState.Gone;
                return Unreliable(officer, killer, kc) ? WitnessState.Unreliable : WitnessState.Testifies;
            }
            if (CourtUtil.FindInmate(map, w.id) is not { } p || p.Inmate() is not { } wc) return WitnessState.Gone;
            return Recants(w, wc) ? WitnessState.Recants : WitnessState.Testifies;
        }

        /// <summary>
        /// The case in the court's hands (0-1), or as the warden can add it up (<paramref name="wardenView"/>: only what is known, a witness who reported a threat at half, an
        /// officer taken at their word). <paramref name="ignoreTampering"/>: the case as it stood before anyone was got at.
        /// </summary>
        public static float Strength(MurderTrial t, Map map, bool wardenView, bool ignoreTampering = false)
        {
            Pawn killer = CourtUtil.FindInmate(map, t.killerId);
            CompInmate kc = killer?.Inmate();
            bool asTold = wardenView || ignoreTampering;
            float s = 0f;
            foreach (TrialWitness w in t.witnesses)
            {
                if (wardenView && !w.known) continue;
                if (w.staff)
                {
                    if (CourtUtil.FindStaff(map, w.id) is not { } officer || !asTold && Unreliable(officer, killer, kc)) continue;
                    s += t.staffVictim ? StaffVictimWitness : StaffWitness;
                    continue;
                }
                if (CourtUtil.FindInmate(map, w.id) is not { } p || p.Inmate() is not { } wc || !asTold && Recants(w, wc)) continue;
                // (A threat the warden knows of counts against the witness, unless they are in protective custody now, where it fades.)
                s += wardenView && w.pressureKnown && !IsProtected(wc) ? InmateWitness / 2f : InmateWitness;
            }
            if (t.caughtAtScene) s += CaughtAtScene;
            if (t.camera && (!wardenView || t.assembled)) s += Camera;
            if (t.weapon && (!wardenView || t.assembled)) s += Weapon;
            s += Mathf.Min(RecordMax, RecordEach * t.priorRecord);
            if (t.informantId >= 0 && CourtUtil.FindInmate(map, t.informantId) != null) s += Informant;
            return Mathf.Clamp01(s);
        }

        // ---- the days before ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Hourly (once per home map, for the cases whose accused is on that map): the case is assembled, witnesses are got at (from the assembly on), the court sits. An escapee's
        /// case waits for the recapture; a case whose accused is gone for good is closed.
        /// </summary>
        public static void Hourly(PrisonGame game, Map map, int hour)
        {
            List<MurderTrial> trials = game.story.trials;
            if (trials.Count == 0) return;
            for (int i = trials.Count - 1; i >= 0 && i < trials.Count; i--)
            {
                MurderTrial t = trials[i];
                Pawn killer = CourtUtil.FindInmate(map, t.killerId);
                if (killer == null || killer.Inmate() == null)
                {
                    // On another of the player's home maps: that map's pass looks after it.
                    if (CourtUtil.OnAnotherHomeMap(map, t.killerId)) continue;
                    // Escaped and being looked for: the case stays open until they are brought back.
                    if (FugitiveSystem.Tracks(game, t.killerId))
                    {
                        if (t.fledTick < 0) Fled(t);
                        continue;
                    }
                    Moot(game, map, t);
                    continue;
                }
                if (t.fledTick >= 0) Recaptured(map, t, killer, hour);
                if (!t.assembled && Now >= t.assembleTick) Assemble(game, map, t);
                ProtectedPressureFades(map, t);
                if (Now < t.trialTick)
                {
                    if (t.assembled) PressureHour(game, map, t);
                    RemindTomorrow(t, killer, hour);
                    continue;
                }
                // (Not while someone is held hostage: nobody is taken to court or sits in the hearing room while the guards have a crisis on their hands; the day is kept when it is over.)
                if (HostageCrisis.Running(map) != null) continue;
                // The day: by video from the hearing room, or off-site (taken to court at ten, the verdict in the afternoon).
                bool room = HearingRooms.Available(map, PrisonMap.Of(map));
                if (Now - t.trialTick > 2 * GenDate.TicksPerDay) Verdict(game, map, t);
                else if (room && hour >= CourtStartHour && hour <= LastSittingHour) Verdict(game, map, t);
                else if (!room && t.courtTick < 0 && hour >= CourtStartHour && hour < CourtEndHour) TakeToCourt(map, t, killer);
                else if (!room && t.courtTick >= 0 && (hour >= CourtEndHour && hour <= LastSittingHour || Now - t.courtTick > 8 * GenDate.TicksPerHour)) Verdict(game, map, t);
            }
        }

        /// <summary>The hour of the evening before the court sits when the warden is told (an hour a day, so a case is announced once).</summary>
        public const int EveHour = 18;

        /// <summary>Cases announced the evening before the court sat (not saved: only the checks read it).</summary>
        public static int RemindersSent;

        /// <summary>
        /// The evening before the court sits (at <see cref="EveHour"/>, with the sitting within the day): a message says so, so the days of waiting do not end in a surprise. Returns whether it was sent.
        /// </summary>
        public static bool RemindTomorrow(MurderTrial t, Pawn killer, int hour)
        {
            int left = t.trialTick - Now;
            if (hour != EveHour || left <= 0 || left > GenDate.TicksPerDay) return false;
            RemindersSent++;
            Messages.Message(Key(t, "RP_TrialTomorrow").Translate(t.killerName, t.victimName, CourtStartHour), killer != null ? new LookTargets(killer) : LookTargets.Invalid, MessageTypeDefOf.NeutralEvent, historical: false);
            return true;
        }

        /// <summary>Who the warden knows will speak at the trial: the staff and the inmates the case names to them (what the prosecution has not told them stays untold).</summary>
        public static List<string> KnownSpeakers(MurderTrial t, Map map)
        {
            List<string> speakers = t.witnesses.Where(w => w.known && Present(map, w)).Select(w => w.name).ToList();
            // (An informant the warden asked to testify speaks too, and the warden knows: they decided it.)
            if (t.informantId >= 0 && CourtUtil.FindInmate(map, t.informantId) != null && !speakers.Contains(t.informantName)) speakers.Add(t.informantName);
            return speakers;
        }

        /// <summary>The accused is on the run and the police are after them: the trial waits for the recapture.</summary>
        private static void Fled(MurderTrial t)
        {
            t.fledTick = Now;
            t.courtTick = -1;
            t.courtLock = false;
            t.courtLockUntil = -1;
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, Key(t, "RP_Chr_TrialFled").Translate(t.killerName, t.victimName));
        }

        /// <summary>The accused is back: the trial goes on, on the day set or, if that has passed, at the first sitting a day on.</summary>
        private static void Recaptured(Map map, MurderTrial t, Pawn killer, int hour)
        {
            t.fledTick = -1;
            if (t.trialTick > Now) return;
            t.trialTick = SittingTick(Now, hour, 1);
            Messages.Message(Key(t, "RP_TrialResumed").Translate(t.killerName, t.victimName, DateText(t)), killer, MessageTypeDefOf.NeutralEvent, historical: false);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, Key(t, "RP_Chr_TrialResumed").Translate(t.killerName, t.victimName, DateText(t)), killer);
        }

        /// <summary>Off-site: the accused is away at court for the day (kept out of the regime, in their cell, until the verdict).</summary>
        public static void TakeToCourt(Map map, MurderTrial t, Pawn killer)
        {
            t.courtTick = Now;
            // The sitting begins: a short letter says who will speak, as far as the warden knows (the verdict comes in the afternoon).
            List<string> speakers = KnownSpeakers(t, map);
            Find.LetterStack.ReceiveLetter("RP_TrialSitsLabel".Translate(t.killerName),
                Key(t, "RP_TrialSitsText").Translate(t.killerName, t.victimName, speakers.Count > 0 ? string.Join(", ", speakers) : "RP_TrialSitsNobody".Translate().Resolve()),
                LetterDefOf.NeutralEvent, new LookTargets(killer));
            CompInmate kc = killer.Inmate();
            if (kc == null || kc.forcedActivity != null) return;
            kc.forcedActivity = RPDefOf.RP_Lockup;
            kc.forcedUntilTick = Now + 10 * GenDate.TicksPerHour;
            t.courtLock = true;
            t.courtLockUntil = kc.forcedUntilTick;
            InmateUtility.InterruptForRegimeChange(killer);
        }

        private static void EndCourtDay(MurderTrial t, Pawn killer)
        {
            if (!t.courtLock) return;
            t.courtLock = false;
            // (A sanction imposed while they were away at court extended the confinement past the court's own end: that one is still theirs to serve.)
            if (killer.Inmate() is { } kc && kc.forcedActivity == RPDefOf.RP_Lockup && (t.courtLockUntil < 0 || kc.forcedUntilTick <= t.courtLockUntil)) PunishmentUtility.End(killer, kc);
        }

        /// <summary>The accused is no longer here (dead, executed, shipped out, escaped for good): there is nothing to try.</summary>
        private static void Moot(PrisonGame game, Map map, MurderTrial t)
        {
            game.story.trials.Remove(t);
            ReleaseWitnesses(game, map, t);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, Key(t, "RP_Chr_TrialMoot").Translate(t.killerName, t.victimName));
        }

        /// <summary>
        /// The warden ships the accused out before the court sits: the case is closed (they are gone), and the press asks how the prison let a murder case lapse (the reputation an acquittal
        /// by recanting costs). Called by <see cref="InmateUtility.TransferOut"/>, whoever asked for it.
        /// </summary>
        public static void Notify_TransferredOut(PrisonGame game, Pawn p)
        {
            if (game == null || TrialOf(game, p) is not { } t) return;
            game.story.trials.Remove(t);
            ReleaseWitnesses(game, p.MapHeld ?? Find.AnyPlayerHomeMap, t);
            game.reputation = Mathf.Max(0f, game.reputation - PressHit);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 3, Key(t, "RP_Chr_TrialTransferred").Translate(t.killerName, t.victimName));
            Messages.Message(Key(t, "RP_TrialTransferredPress").Translate(t.killerName, t.victimName), MessageTypeDefOf.NegativeEvent, historical: false);
        }

        /// <summary>The prosecution has what it will use: the warden is told it, and asked about the witnesses and an informant where there are any.</summary>
        public static void Assemble(PrisonGame game, Map map, MurderTrial t)
        {
            t.assembled = true;
            foreach (TrialWitness w in t.witnesses) w.known = true;
            List<string> lines = KnownCase(t, map);
            Pawn killer = CourtUtil.FindInmate(map, t.killerId);
            string band = KnownBand(t, map).Translate().Resolve();
            string text = Key(t, "RP_TrialAssembledText").Translate(t.killerName, t.victimName, DateText(t), lines.Count > 0 ? CourtUtil.Bullets(lines) : "RP_TrialNothingKnown".Translate().Resolve(), band).Resolve();
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_TrialAssembled".Translate(t.killerName, DateText(t)), killer);
            // The inmates who came forward and a known informant: the warden's choices are part of the case's letter (one letter, not three in a tick).
            List<Pawn> toProtect = t.witnesses.Where(w => !w.staff).Select(w => CourtUtil.FindInmate(map, w.id)).Where(p => p != null).ToList();
            List<Pawn> informants = KnownInformants(map, t);
            // (Keeping the witnesses apart from the accused is the cheaper answer to the same worry: offered alongside the move to protective custody.)
            int choices = (toProtect.Count > 0 ? CaseProtect | CaseApart : 0) | (informants.Count > 0 ? CaseInformant : 0);
            if (choices == 0)
            {
                Find.LetterStack.ReceiveLetter("RP_TrialAssembledLabel".Translate(t.killerName), text, LetterDefOf.NeutralEvent, killer != null ? new LookTargets(killer) : LookTargets.Invalid);
                return;
            }
            if ((choices & CaseProtect) != 0) text += "\n\n" + "RP_TrialWitnessesText".Translate(t.killerName, string.Join(", ", toProtect.Select(p => p.LabelShort))).Resolve();
            if ((choices & CaseApart) != 0) text += " " + "RP_TrialApartText".Translate(t.killerName).Resolve();
            if ((choices & CaseInformant) != 0) text += "\n\n" + "RP_TrialInformantText".Translate(informants[0].LabelShort, t.killerName).Resolve();
            var targets = new List<Pawn>(toProtect);
            if ((choices & CaseInformant) != 0) targets.Add(informants[0]);
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_TrialCase", "RP_TrialAssembledLabel".Translate(t.killerName), text, map, new LookTargets(killer != null ? targets.Prepend(killer).ToList() : targets));
            letter.amount = t.id;
            letter.money = choices;
            if ((choices & CaseInformant) != 0) letter.subjectId = informants[0].thingIDNumber;
            letter.pawns.AddRange(targets);
            letter.StartTimeout(GenDate.TicksPerDay);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>What the case's letter offers (kept on the letter): moving the inmates who came forward out of reach, asking a known informant to testify, and keeping the witnesses apart from the accused (a letter from before it was offered has no such bit).</summary>
        public const int CaseProtect = 1, CaseInformant = 2, CaseApart = 4;

        /// <summary>The inmate witnesses the warden could still keep apart from the accused: known, not staff, here, with no order yet. Writing the orders is <see cref="KeepWitnessesApart"/>.</summary>
        public static List<Pawn> ApartCandidates(Map map, MurderTrial t)
        {
            var found = new List<Pawn>();
            if (map == null || CourtUtil.FindInmate(map, t.killerId) is not { } killer) return found;
            foreach (TrialWitness w in t.witnesses)
                if (!w.staff && w.known && CourtUtil.FindInmate(map, w.id) is { } p && p != killer && !KeepApart.Ordered(p, killer)) found.Add(p);
            return found;
        }

        /// <summary>Keeps every inmate witness apart from the accused (a keep-apart order for each pair, for the trial). Returns how many orders were written.</summary>
        public static int KeepWitnessesApart(Map map, MurderTrial t)
        {
            if (map == null || CourtUtil.FindInmate(map, t.killerId) is not { } killer) return 0;
            int written = 0;
            foreach (Pawn p in ApartCandidates(map, t))
                if (KeepApart.Add(p, killer, "trial", silent: true) == KeepApartResult.Added) written++;
            return written;
        }

        /// <summary>Informants the warden knows, who could be asked to testify: not the killer, not a witness already, not already asked, not exposed, not off the payroll.</summary>
        public static List<Pawn> KnownInformants(Map map, MurderTrial t)
        {
            var list = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (p.Inmate() is { } c && InmateUtility.IsActiveInmate(p) && c.KnownReputation(ReputationDefOf.RP_Snitch) && !c.snitchExposed && !c.informantQuit && !c.gangLeader
                    && p.thingIDNumber != t.killerId && p.thingIDNumber != t.informantId && DeathRowSystem.Ext(c) == null && !t.witnesses.Exists(w => w.id == p.thingIDNumber))
                    list.Add(p);
            return list;
        }

        /// <summary>The warden asks a known informant to testify: a stronger case (they are exposed on the day).</summary>
        public static bool AskInformant(Map map, MurderTrial t, Pawn informant)
        {
            if (t.informantId >= 0 || informant == null || informant.Inmate() == null || CourtUtil.FindInmate(map, informant.thingIDNumber) == null) return false;
            t.informantId = informant.thingIDNumber;
            t.informantName = informant.LabelShort;
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_TrialInformantAsked".Translate(informant.LabelShort, t.killerName), informant);
            return true;
        }

        /// <summary>A known inmate witness the warden could move to protective custody now (it is unlocked, they are not there already or on death row, and there is a place).</summary>
        private static bool CanProtect(Map map, TrialWitness w, SecurityLevelDef pc) =>
            !w.staff && w.known && CourtUtil.FindInmate(map, w.id) is { } p && p.Inmate() is { } c && ProtectableAsWitness(PrisonGame.Instance, p, c) && Unlocks.IsUnlocked(pc) && InmateUtility.HasRoomIn(p, pc);

        /// <summary>A witness the warden may move to protective custody: not there already, not on death row or in supermax (protective custody lets them mix with the vulnerable, and it would skip the step-down), and not themselves accused of a killing (it is for the people who may be got at, not a way out of the wing for the accused).</summary>
        internal static bool ProtectableAsWitness(PrisonGame game, Pawn p, CompInmate c) => !IsProtected(c) && DeathRowSystem.Ext(c) == null && c.security is not { escortOnly: true } && !OnRemand(game, p);

        /// <summary>Whether moving the witnesses can do anything: protective custody is unlocked and has room for at least one who is not there already.</summary>
        public static bool CanProtectAny(Map map, MurderTrial t)
        {
            SecurityLevelDef pc = ProtectiveLevel;
            return pc != null && t.witnesses.Exists(w => CanProtect(map, w, pc));
        }

        /// <summary>The inmates who came forward go to protective custody, where nobody gets at them. Returns how many moved.</summary>
        public static int ProtectWitnesses(Map map, MurderTrial t)
        {
            PrisonGame game = PrisonGame.Instance;
            SecurityLevelDef pc = ProtectiveLevel;
            if (pc == null) return 0;
            int moved = 0;
            foreach (TrialWitness w in t.witnesses)
            {
                if (w.staff || !w.known || CourtUtil.FindInmate(map, w.id) is not { } p || p.Inmate() is not { } c || !ProtectableAsWitness(game, p, c)) continue;
                if (!CanProtect(map, w, pc))
                {
                    Messages.Message(InmateUtility.NoRoomText(p, pc), p, MessageTypeDefOf.RejectInput, historical: false);
                    continue;
                }
                c.protectedFrom ??= c.security;
                InmateUtility.ChangeSecurity(p, c, pc);
                c.fear = Mathf.Max(0f, c.fear - 20f);
                moved++;
            }
            return moved;
        }

        /// <summary>The case is over: the witnesses the warden moved to protective custody for it go back to the wing they came from (see <see cref="TryReturn"/>).</summary>
        public static void ReleaseWitnesses(PrisonGame game, Map map, MurderTrial t)
        {
            if (map == null) return;
            foreach (TrialWitness w in t.witnesses)
                if (CourtUtil.FindInmate(map, w.id) is { } p && p.Inmate() is { } c) TryReturn(game, map, p, c);
        }

        /// <summary>Daily: whoever is still in protective custody for a case that is over, and had no room on their old wing then, goes back when there is.</summary>
        public static void Daily(PrisonGame game, Map map)
        {
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned.ToList())
                if (p.Inmate() is { protectedFrom: not null } c) TryReturn(game, map, p, c);
        }

        /// <summary>
        /// An inmate the warden moved to protective custody as a witness goes back to the level they came from, unless they are vulnerable (an informant who was exposed, a vulnerable
        /// reputation: they stay) or another open case still needs them there; with no room on the old wing they wait for the daily pass.
        /// </summary>
        private static void TryReturn(PrisonGame game, Map map, Pawn p, CompInmate c)
        {
            SecurityLevelDef from = c.protectedFrom;
            if (from == null) return;
            if (!IsProtected(c) || FearSystem.Vulnerable(c))
            {
                c.protectedFrom = null;
                return;
            }
            if (game.story.trials.Exists(o => o.witnesses.Exists(x => x.id == p.thingIDNumber && !x.staff)) || !Unlocks.IsUnlocked(from)) return;
            if (!InmateUtility.HasRoomIn(p, from))
            {
                // (A wing now kept for the other sex has no bed for them to come back to, ever: the warden is told once, and they stay where they are, no longer there for a case.)
                if (InmateUtility.TellIfWingKeepsThemOut(p, from)) c.protectedFrom = null;
                return;
            }
            c.protectedFrom = null;
            InmateUtility.ChangeSecurity(p, c, from);
            Messages.Message("RP_TrialWitnessReturned".Translate(p.LabelShort, from.label), p, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        // ---- getting at witnesses -----------------------------------------------------------------------------------------

        /// <summary>Hourly, from the day the case is assembled (that is when the killer's gang learns who will testify): each inmate witness the gang can reach may be got at.</summary>
        private static void PressureHour(PrisonGame game, Map map, MurderTrial t)
        {
            foreach (TrialWitness w in t.witnesses)
            {
                if (w.staff || w.recanted || !Rand.Chance(PressureChancePerHour) || CourtUtil.FindInmate(map, w.id) is not { } p) continue;
                TryPressure(game, map, t, w, p);
            }
        }

        /// <summary>Pressure on a witness in protective custody halves every day they are there (the fear wears off once nobody can get at them).</summary>
        private static void ProtectedPressureFades(Map map, MurderTrial t)
        {
            foreach (TrialWitness w in t.witnesses)
            {
                if (w.staff || w.pressure <= 0f || w.recanted || CourtUtil.FindInmate(map, w.id) is not { } p || p.Inmate() is not { } c || !IsProtected(c)) continue;
                w.pressure *= ProtectedHourlyDecay;
                if (w.pressure < 0.02f) w.pressure = 0f;
            }
        }

        /// <summary>Someone free in the same wing who is the killer, or of the killer's gang, and can get at them (not in protective custody, in the hole, on death row).</summary>
        public static Pawn Pressurer(Map map, MurderTrial t, Pawn witness)
        {
            CompInmate wc = witness.Inmate();
            if (wc == null || IsProtected(wc) || wc.forcedActivity != null || DeathRowSystem.Ext(wc) != null) return null;
            Pawn killer = CourtUtil.FindInmate(map, t.killerId);
            CompInmate kc = killer?.Inmate();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (p == witness || p.Inmate() is not { } c || !InmateUtility.IsActiveInmate(p) || p.Downed || !p.Awake() || c.forcedActivity != null || c.security != wc.security || IsProtected(c)) continue;
                if (p == killer || kc?.gang != null && c.gang == kc.gang) return p;
            }
            return null;
        }

        /// <summary>
        /// One attempt to get at a witness: a threat (or, one time in four, a beating). Fear, tension and the pressure that makes them recant on the stand; a witness who trusts
        /// the guards may report it (then the warden knows), or, trusting the warden, ask to see the warden about it (<see cref="WardenAsks"/>). Hidden otherwise. Returns false when nobody can reach them.
        /// </summary>
        public static bool TryPressure(PrisonGame game, Map map, MurderTrial t, TrialWitness w, Pawn witness, bool? beating = null, bool allowReport = true)
        {
            CompInmate wc = witness.Inmate();
            if (wc == null || w.staff || w.recanted) return false;
            Pawn from = Pressurer(map, t, witness);
            if (from == null) return false;
            // (Someone already badly hurt is threatened, not beaten: this is getting at a witness, not a killing. A threat can be passed on through a door, a beating needs both of them out of
            // their cells: at three in the morning, in lockup, nobody is beaten.)
            bool beat = (beating ?? Rand.Chance(BeatingChance)) && ComplaintSystem.Fit(witness) && !witness.Downed && !game.CurrentActivity(witness).lockup && !game.CurrentActivity(from).lockup;
            w.pressure += beat ? BeatingPressure : ThreatPressure;
            FearSystem.Scare(wc, beat ? 35f : 25f);
            wc.tension = Mathf.Min(100f, wc.tension + 12f);
            // (A cause that gives nothing away: the file must not say they were got at.)
            wc.mainCause = "RP_Cause_WingTrouble";
            if (beat)
            {
                // (No instigator: vanilla would give the witness a "Harmed me" memory naming the pressurer, which shows who got at them before the verdict.)
                witness.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Rand.RangeInclusive(4, 9)));
                // The pressurer's own file keeps a trace nobody has seen (no misconduct statistics, no relief of their tension: the warden would see the numbers move).
                if (from.Inmate() is { } fc) fc.AddRecord(new MisconductRecord { def = RPMisconductDefOf.RP_Fight, tick = Now, cause = fc.mainCause, seen = false });
            }
            if (allowReport && !w.pressureKnown && StaffMemories.MostHated(wc) == null && (ForcedReport ?? Rand.Chance(ReportChance)))
            {
                // (Of those who come forward, a witness the warden knows of who trusts the warden would sooner say it to the warden in person: they ask to see them, and a word with them is the report, see WardenAsks. Taken out
                // of the report's own odds, so that nothing more of the pressure shows than before: the request is the report waiting for the warden.)
                if (!(w.known && TrustsWarden(map, wc) && WardenAsks.Raise(witness, WardenAskReason.Witness))) Report(t, w, witness);
            }
            return true;
        }

        /// <summary>The report's dice, for a test to settle (null: the dice, <see cref="ReportChance"/>).</summary>
        public static bool? ForcedReport;

        /// <summary>A witness who trusts the warden in post (any trust at all: a word with them is enough) goes to the warden rather than the guards.</summary>
        public static bool TrustsWarden(Map map, CompInmate wc) => WardenSystem.InPost(map) is { } warden && StaffMemories.ScoreFor(wc, warden) > 0f;

        /// <summary>The threat comes out: told to the guards, or to the warden in person (<paramref name="warden"/>: who heard it, see <see cref="WardenAsks"/>).</summary>
        internal static void Report(MurderTrial t, TrialWitness w, Pawn witness, Pawn warden = null)
        {
            w.pressureKnown = true;
            w.known = true;
            string message = warden == null ? "RP_TrialWitnessReported".Translate(witness.LabelShort, t.killerName) : "RP_TrialWitnessToldWarden".Translate(witness.LabelShort, t.killerName, warden.LabelShort);
            Messages.Message(message, witness, MessageTypeDefOf.NegativeEvent, historical: false);
            string chronicle = warden == null ? "RP_Chr_TrialThreat".Translate(witness.LabelShort, t.killerName) : "RP_Chr_TrialThreatWarden".Translate(witness.LabelShort, t.killerName, warden.LabelShort);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, chronicle, warden == null ? new[] { witness } : new[] { witness, warden });
        }

        // ---- the day -----------------------------------------------------------------------------------------------------

        /// <summary>The court weighs the case and gives its verdict. What was hidden comes out in the letter.</summary>
        public static void Verdict(PrisonGame game, Map map, MurderTrial t)
        {
            game.story.trials.Remove(t);
            Pawn killer = CourtUtil.FindInmate(map, t.killerId);
            CompInmate kc = killer?.Inmate();
            if (killer == null || kc == null)
            {
                Moot(game, map, t);
                return;
            }
            EndCourtDay(t, killer);
            game.stats.trials++;
            var cameOut = new List<string>();
            int recanted = 0;
            foreach (TrialWitness w in t.witnesses)
            {
                WitnessState state = StateOf(map, t, w, killer, kc);
                if (state == WitnessState.Gone) continue;
                if (state == WitnessState.Recants)
                {
                    w.recanted = true;
                    recanted++;
                    game.stats.witnessesRecanted++;
                    cameOut.Add("RP_TrialCameOut_Recanted".Translate(w.name).Resolve());
                }
                else if (state == WitnessState.Unreliable)
                {
                    w.unreliable = true;
                    cameOut.Add("RP_TrialCameOut_Forgot".Translate(w.name).Resolve());
                    if (CourtUtil.FindStaff(map, w.id) is { } officer) CorruptionSystem.AddSuspicion(officer, officer.GetComp<CompStaff>(), 40f, "RP_Sus_TrialMemory");
                }
                else cameOut.Add("RP_TrialCameOut_Testified".Translate(w.name).Resolve());
            }
            if (t.camera) cameOut.Add("RP_TrialCameOut_Camera".Translate().Resolve());
            if (t.weapon) cameOut.Add("RP_TrialCameOut_Weapon".Translate().Resolve());
            if (t.informantId >= 0 && CourtUtil.FindInmate(map, t.informantId) is { } informant)
            {
                cameOut.Add("RP_TrialCameOut_Informant".Translate(informant.LabelShort).Resolve());
                ExposeInformant(game, map, t, informant, killer, kc);
            }
            float strength = Strength(t, map, wardenView: false);
            bool convicted = Rand.Chance(ConvictChance(strength));
            string venue = (HearingRooms.Available(map, PrisonMap.Of(map)) ? "RP_TrialVenue_Room" : "RP_TrialVenue_Court").Translate().Resolve();
            string evidence = cameOut.Count > 0 ? CourtUtil.Bullets(cameOut) : "RP_TrialCameOut_Nothing".Translate().Resolve();
            if (AutoTest.Active) RPLog.Message($"[RimPrison:trial] verdict on {t.killerName}: strength {strength:F2}, {(convicted ? "convicted" : "acquitted")}, recanted {recanted}");
            if (convicted) Convict(game, map, t, killer, kc, venue, evidence);
            else Acquit(game, map, t, killer, kc, venue, evidence, recanted, Strength(t, map, wardenView: false, ignoreTampering: true) >= ConvictAt);
            if (t.staffVictim)
                foreach (Pawn o in StaffCommute.AllStaff(map))
                    if (o.GetComp<CompStaff>()?.role != null) o.needs?.mood?.thoughts.memories.TryGainMemory(convicted ? RPTrialDefOf.RP_TrialJustice : RPTrialDefOf.RP_TrialAcquitted);
            ReleaseWitnesses(game, map, t);
        }

        internal static void Convict(PrisonGame game, Map map, MurderTrial t, Pawn killer, CompInmate kc, string venue, string evidence)
        {
            game.stats.convictions++;
            // (A charge other than murder adds the offence's days and no more: no life, no move to maximum security.)
            if (DeathRowSystem.Ext(kc) == null && !t.IsMurder)
            {
                if (!kc.IsLifer) kc.sentenceDays += ChargeDays(t.offence);
            }
            else if (DeathRowSystem.Ext(kc) == null)
            {
                if (!kc.IsLifer) kc.sentenceDays = Rand.Chance(0.5f) ? -1 : kc.sentenceDays + ExtraDays;
                // A convicted murderer goes to supermax when the wing has a free cell (one already there stays), else to maximum security if there is room.
                SecurityLevelDef max = Celebrities.Maximum;
                if (!SupermaxSystem.IsIn(kc) && !SupermaxSystem.TrySend(killer, kc, SupermaxReason.Convicted) && kc.security != max && Unlocks.IsUnlocked(max))
                {
                    if (InmateUtility.HasRoomIn(killer, max)) InmateUtility.ChangeSecurity(killer, kc, max);
                    else InmateUtility.TellIfWingKeepsThemOut(killer, max);
                }
            }
            string sentence = kc.IsLifer ? "RP_Chr_LifeSentence".Translate().Resolve() : "RP_Chr_Days".Translate(kc.TermDays).Resolve();
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 3, Key(t, "RP_Chr_TrialConvicted").Translate(t.killerName, t.victimName, sentence), killer);
            Find.LetterStack.ReceiveLetter("RP_TrialConvictedLabel".Translate(t.killerName), Key(t, "RP_TrialConvictedText").Translate(t.killerName, t.victimName, venue, sentence, evidence),
                LetterDefOf.PositiveEvent, killer);
        }

        internal static void Acquit(PrisonGame game, Map map, MurderTrial t, Pawn killer, CompInmate kc, string venue, string evidence, int recanted, bool wasStrong)
        {
            game.stats.acquittals++;
            // The charge falls: it comes off their file (the hole they were put in for it was served or is under way, as any remand). This case's record, not another's: it was written the tick of the charge.
            // (A charge other than murder wrote no record of its own: the offence it was for stays on the file, a court's acquittal or not.)
            if (t.IsMurder)
            {
                int charged = kc.records.FindLastIndex(r => r.def == RPDeathDefOf.RP_Murder && r.tick == t.chargedTick);
                if (charged < 0) charged = kc.records.FindLastIndex(r => r.def == RPDeathDefOf.RP_Murder);
                if (charged >= 0) kc.records.RemoveAt(charged);
                // A staff killer who was put in supermax at the charge goes back to where they were: the hole was for the charge.
                SupermaxSystem.StepDownAcquitted(killer, kc);
            }
            // The victim's friends and gang want revenge (and the inmates who spoke for the prosecution are the next to be got at).
            foreach (int id in t.avengers)
                if (CourtUtil.FindInmate(map, id) is { } p && p.Inmate() is { } c && p != killer)
                {
                    PrisonRelations.MakeEnemies(p, killer);
                    c.tension = Mathf.Min(100f, c.tension + 20f);
                    c.mainCause = "RP_Cause_Revenge";
                }
            foreach (TrialWitness w in t.witnesses)
                if (!w.staff && !w.recanted && CourtUtil.FindInmate(map, w.id) is { } wp && wp.Inmate() is { } wc) FearSystem.Scare(wc, 15f);
            // A strong case lost to witnesses who were got at: the press asks how a prison cannot protect its witnesses.
            bool embarrassing = wasStrong && recanted > 0;
            if (embarrassing) game.reputation = Mathf.Max(0f, game.reputation - PressHit);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 3, Key(t, "RP_Chr_TrialAcquitted").Translate(t.killerName, t.victimName), killer);
            Find.LetterStack.ReceiveLetter("RP_TrialAcquittedLabel".Translate(t.killerName),
                Key(t, "RP_TrialAcquittedText").Translate(t.killerName, t.victimName, venue, evidence) + (embarrassing ? "\n\n" + "RP_TrialAcquittedPress".Translate().Resolve() : ""),
                LetterDefOf.NegativeEvent, killer);
        }

        /// <summary>The informant testified: every inmate knows, and the killer's gang and friends want them.</summary>
        private static void ExposeInformant(PrisonGame game, Map map, MurderTrial t, Pawn informant, Pawn killer, CompInmate kc)
        {
            CompInmate ic = informant.Inmate();
            if (ic == null || ic.snitchExposed) return;
            ic.snitchExposed = true;
            InformantTask.End(informant, ic);
            FearSystem.Scare(ic, 30f);
            if (!ic.revealed.Contains(ReputationDefOf.RP_Snitch)) ic.revealed.Add(ReputationDefOf.RP_Snitch);
            ThoughtDef hates = RPTrialDefOf.RP_HatesSnitch;
            foreach (Pawn o in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (o == informant || o.Inmate() is not { } oc) continue;
                if (o != killer && (kc.gang == null || oc.gang != kc.gang) && !PrisonRelations.Friends(o, killer)) continue;
                o.needs?.mood?.thoughts.memories.TryGainMemory(hates, informant);
                oc.tension = Mathf.Min(100f, oc.tension + 15f);
            }
            Story.Record(RPChronicleDefOf.RP_Chr_Gangs, 2, "RP_Chr_TrialInformant".Translate(informant.LabelShort, t.killerName), informant, killer);
        }

        // ---- what the screens show ------------------------------------------------------------------------------------------

        /// <summary>For the file: the accused's open case; a witness the warden knows of (the prosecution's, in the trial of whom); where the accused is today.</summary>
        public static IEnumerable<string> FileLines(Pawn p)
        {
            PrisonGame game = PrisonGame.Instance;
            if (game == null || game.story.trials.Count == 0) yield break;
            Map map = p.MapHeld ?? Find.AnyPlayerHomeMap;
            foreach (MurderTrial t in game.story.trials)
            {
                if (t.killerId == p.thingIDNumber)
                {
                    yield return Key(t, "RP_FileTrial").Translate(t.victimName, DateText(t)).Resolve() + (t.assembled && map != null ? " " + "RP_FileTrialCase".Translate(KnownBand(t, map).Translate()).Resolve() : "");
                    if (t.courtTick >= 0) yield return "RP_FileTrialCourt".Translate().Resolve();
                }
                else if (t.witnesses.Exists(w => !w.staff && w.known && w.id == p.thingIDNumber))
                    yield return "RP_FileWitness".Translate(t.killerName, DateText(t)).Resolve() + (p.Inmate() is { } c && IsProtected(c) ? " " + "RP_FileWitnessProtected".Translate().Resolve() : "");
            }
        }

        /// <summary>Everything saved about the open cases in one string (the saveload plan compares it).</summary>
        public static string Describe(MurderTrial t) =>
            $"{t.id}:{(t.IsMurder ? "-" : t.offence.defName)}:{t.killerId}:{t.killerName}:{t.victimId}:{t.victimName}:{t.staffVictim}{t.assembled}{t.caughtAtScene}{t.camera}{t.weapon}{t.courtLock}:{t.courtLockUntil}:{t.fledTick}:{t.priorRecord}:{t.chargedTick}:{t.assembleTick}:{t.trialTick}:{t.courtTick}:{t.informantId}:{t.informantName}:"
            + string.Join("+", t.witnesses.Select(w => $"{w.id}={w.name}/{w.staff}{w.known}{w.pressureKnown}{w.recanted}{w.unreliable}/{w.pressure:F2}")) + ":" + string.Join("+", t.avengers);
    }

    /// <summary>
    /// The case's letter: the prosecution has assembled it, and the warden may move the inmates who came forward out of reach (protective custody) and ask a known informant to
    /// testify (a stronger case, and they are exposed on the day), as one decision. What is offered is on the letter (<see cref="Trials.CaseProtect"/>, <see cref="Trials.CaseInformant"/>);
    /// left unanswered it decides itself as "neither".
    /// </summary>
    public class PrisonDecisionWorker_TrialCase : PrisonDecisionWorker
    {
        private static readonly string[] Both = { "RP_TrialCase_Both", "RP_TrialCase_Protect", "RP_TrialCase_Ask", "RP_TrialCase_Neither" };
        private static readonly string[] ProtectOnly = { "RP_TrialCase_Protect", "RP_TrialCase_Neither" };
        private static readonly string[] AskOnly = { "RP_TrialCase_Ask", "RP_TrialCase_Neither" };
        private static readonly string[] BothApart = { "RP_TrialCase_Both", "RP_TrialCase_Protect", "RP_TrialCase_Apart", "RP_TrialCase_Ask", "RP_TrialCase_Neither" };
        private static readonly string[] ProtectApart = { "RP_TrialCase_Protect", "RP_TrialCase_Apart", "RP_TrialCase_Neither" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l)
        {
            bool apart = (l.money & Trials.CaseApart) != 0;
            int offered = l.money & (Trials.CaseProtect | Trials.CaseInformant);
            return offered == (Trials.CaseProtect | Trials.CaseInformant) ? apart ? BothApart : Both : (offered & Trials.CaseProtect) != 0 ? apart ? ProtectApart : ProtectOnly : AskOnly;
        }

        public override int TimeoutChoice => 1;

        public override int TimeoutOption(ChoiceLetter_PrisonDecision l) => Options(l).Count - 1;

        private static bool Protects(string key) => key is "RP_TrialCase_Both" or "RP_TrialCase_Protect";

        private static bool Asks(string key) => key is "RP_TrialCase_Both" or "RP_TrialCase_Ask";

        /// <summary>
        /// The informant the letter is about: the one it names by id (the people on a letter are dropped when destroyed, which would leave a witness last), and for a letter written before
        /// it named one, the last of its people. Null when they are gone.
        /// </summary>
        private static Pawn Informant(ChoiceLetter_PrisonDecision l)
        {
            if ((l.money & Trials.CaseInformant) == 0) return null;
            if (l.subjectId >= 0) return l.pawns.Find(p => p.thingIDNumber == l.subjectId);
            return l.pawns.Count > 0 ? l.pawns[l.pawns.Count - 1] : null;
        }

        public override string Disabled(ChoiceLetter_PrisonDecision l, int option)
        {
            string key = Options(l)[option];
            if (key == "RP_TrialCase_Apart")
            {
                if (Trials.ById(PrisonGame.Instance, l.amount) is not { } trial) return "RP_TrialOver".Translate().Resolve();
                Map home = l.map ?? Find.AnyPlayerHomeMap;
                if (Trials.ApartCandidates(home, trial).Count == 0) return "RP_TrialNoApart".Translate().Resolve();
                return PrisonGame.Instance.keepApart.Full ? "RP_KeepApartFull".Translate(KeepApartBook.Cap).Resolve() : null;
            }
            if (!Protects(key) && !Asks(key)) return null;
            if (Trials.ById(PrisonGame.Instance, l.amount) is not { } t) return "RP_TrialOver".Translate().Resolve();
            Map map = l.map ?? Find.AnyPlayerHomeMap;
            if (Protects(key) && map != null && !Trials.CanProtectAny(map, t)) return "RP_TrialNoProtection".Translate().Resolve();
            // (Gone: dead, released, transferred; or already exposed.)
            if (Asks(key) && (Informant(l) is not { } who || map == null || CourtUtil.FindInmate(map, who.thingIDNumber) is not { } p || p.Inmate() is not { snitchExposed: false }))
                return "RP_TrialInformantGone".Translate().Resolve();
            return null;
        }

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            string key = Options(l)[option];
            if (Trials.ById(PrisonGame.Instance, l.amount) is not { } t) return;
            if (key == "RP_TrialCase_Apart")
            {
                int written = Trials.KeepWitnessesApart(map, t);
                if (written > 0) Messages.Message("RP_TrialKeptApart".Translate(written, t.killerName), MessageTypeDefOf.NeutralEvent, historical: false);
                return;
            }
            if (Protects(key))
            {
                int moved = Trials.ProtectWitnesses(map, t);
                if (moved > 0) Messages.Message("RP_TrialProtected".Translate(moved), MessageTypeDefOf.NeutralEvent, historical: false);
            }
            if (Asks(key) && Informant(l) is { } who) Trials.AskInformant(map, t, who);
        }
    }

    /// <summary>
    /// Vanilla's Release order (the prisoner tab) is refused for an accused on remand: it would let the case lapse as moot with no cost (<see cref="CourtUtil.FindInmate"/> takes a released
    /// prisoner for gone), where shipping them out costs reputation (<see cref="Trials.Notify_TransferredOut"/>). A message says why.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetExclusiveInteraction))]
    public static class Patch_GuestTracker_NoReleaseOnRemand
    {
        public static bool Prefix(Pawn_GuestTracker __instance, PrisonerInteractionModeDef def, Pawn ___pawn)
        {
            if (def != PrisonerInteractionModeDefOf.Release || PrisonGame.Instance == null || ___pawn == null || !___pawn.IsPrisonerOfColony) return true;
            if (Trials.TrialOf(PrisonGame.Instance, ___pawn) is not { } t) return true;
            Messages.Message(Trials.Key(t, "RP_TrialNoRelease").Translate(___pawn.LabelShort, t.victimName), ___pawn, MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
    }

    /// <summary>A Release order given before the charge is called off when a guard comes for it (the accused is on remand now).</summary>
    [HarmonyLib.HarmonyPatch(typeof(WorkGiver_Warden_ReleasePrisoner), nameof(WorkGiver_Warden_ReleasePrisoner.JobOnThing))]
    public static class Patch_WorkGiver_ReleasePrisoner_Remand
    {
        public static bool Prefix(Thing t, ref Verse.AI.Job __result)
        {
            if (t is not Pawn p || PrisonGame.Instance == null || !p.IsPrisonerOfColony || p.guest == null || Trials.TrialOf(PrisonGame.Instance, p) is not { } trial) return true;
            if (p.guest.ExclusiveInteractionMode == PrisonerInteractionModeDefOf.Release)
            {
                p.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.MaintainOnly);
                Messages.Message(Trials.Key(trial, "RP_TrialNoRelease").Translate(p.LabelShort, trial.victimName), p, MessageTypeDefOf.RejectInput, historical: false);
            }
            __result = null;
            return false;
        }
    }
}
