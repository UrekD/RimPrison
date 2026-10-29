using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPParoleDefOf
    {
        public static ThoughtDef RP_ParoleDenied;
        public static ThoughtDef RP_ParoleOpposed;
        public static PrisonZoneDef RP_HearingRoom;
        public static JobDef RP_AttendHearing;
        public static JobDef RP_EscortToHearing;
        public static JobDef RP_SitBoard;
        public static StaffRoleDef RP_Administrator;
        public static InmateWantDef RP_Want_ProveWrong;

        static RPParoleDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPParoleDefOf));
    }

    /// <summary>
    /// The warden at the parole hearing: once a day the closest call among eligible inmates comes to the warden, who may
    /// recommend (the board listens as much as it trusts the warden), oppose (the inmate knows) or abstain. The board
    /// remembers: a recommended parolee who reoffends costs reputation and trust; one who stays out builds it.
    /// </summary>
    public static class ParoleHearings
    {
        public const float TrustMin = 0.3f, TrustMax = 1.5f;

        /// <summary>
        /// The hearing for one inmate, with the warden's word (+1 recommend, -1 oppose, 0 none). <paramref name="scene"/>: the sitting in the
        /// hearing room that went before it: the board's odds are those of what was said there, and a refusal is written to the file with its reasons.
        /// </summary>
        public static bool Hold(PrisonGame game, Pawn p, int word, ParoleCase scene = null)
        {
            CompInmate c = p.Inmate();
            // (A letter that was put to the warden before they were sent to supermax: the answer comes too late, there is no parole from there.)
            if (!ParoleSystem.MayBeParoled(c)) return false;
            float chance = (scene != null ? scene.chance : ParoleSystem.ParoleChance(c)) + (word > 0 ? 0.25f * game.story.boardTrust : word < 0 ? -0.3f : 0f);
            c.lastHearingTick = Find.TickManager.TicksGame;
            if (Rand.Chance(Mathf.Clamp(chance, 0.02f, 0.95f)))
            {
                game.stats.paroled++;
                game.ledger.Add(RPDefOf.RP_Grants, ParoleSystem.ParoleBonus, "RP_ParoleNote".Translate(p.LabelShort));
                if (word > 0) game.stats.paroleRecommended++;
                ReleaseSystem.Release(game, p, paroled: true, recommended: word > 0);
                return true;
            }
            p.needs.mood?.thoughts.memories.TryGainMemory(word < 0 ? RPParoleDefOf.RP_ParoleOpposed : RPParoleDefOf.RP_ParoleDenied);
            if (word < 0) c.mainCause = "RP_Cause_ParoleDenied";
            // (The reasons are written down before the goal hears of the refusal: a goal given up turns on them.)
            string why = scene == null ? null : ParoleScene.NoteDenial(p, c, scene, word);
            Story.Record(RPChronicleDefOf.RP_Chr_Release, 1, why != null ? "RP_Chr_ParoleDeniedWhy".Translate(p.LabelShort, why) : (word < 0 ? "RP_Chr_ParoleOpposed" : "RP_Chr_ParoleDenied").Translate(p.LabelShort), p);
            Wants.Notify_ParoleDenied(p, c, heard: true);
            if (why != null) Wants.Notify_ParoleReasons(p, c);
            // (The warden's opposition angers them after the goal has taken its measure, so that cooling down again is not a gain.)
            if (word < 0) c.tension = Mathf.Min(100f, c.tension + 15f);
            return false;
        }

        /// <summary>The closest call of the day comes to the warden (one letter at a time).</summary>
        public static Pawn PickForWarden(List<Pawn> due)
        {
            if (due.Count == 0 || StoryLetters.Open().OfType<ChoiceLetter_PrisonDecision>().Any(l => l.Is("RP_Decision_Parole"))) return null;
            return due.OrderBy(p => Mathf.Abs(ParoleSystem.ParoleChance(p.Inmate()) - 0.5f)).First();
        }

        public static void AskWarden(PrisonGame game, Map map, Pawn p)
        {
            CompInmate c = p.Inmate();
            int seen = c.records.Count(r => r.seen && r.def.severity >= 2);
            string family = c.familyCount > 0 ? "RP_ParoleFamily".Translate(c.familyCount).ToString() : "RP_ParoleNoFamily".Translate().ToString();
            string ties = string.Join(", ", (p.relations?.DirectRelations ?? new List<DirectPawnRelation>())
                .Where(r => r.def == RPRelationDefOf.RP_Friend || r.def == RPRelationDefOf.RP_Enemy)
                .Select(r => (r.def == RPRelationDefOf.RP_Friend ? "RP_ParoleFriend" : "RP_ParoleEnemy").Translate(r.otherPawn.LabelShort).ToString()));
            TaggedString text = "RP_ParoleText".Translate(p.LabelShort, c.crime?.label ?? "?", c.DaysServed.ToString("0"), c.TermDays, seen, c.programsPassed, c.hoursWorked,
                family + (c.jobWaiting ? " " + "RP_ParoleJobWaiting".Translate() : ""), ties.Length > 0 ? ties : "RP_FileNone".Translate().ToString(),
                ParoleSystem.ParoleChance(c).ToStringPercent(), game.story.boardTrust.ToString("0.00"));
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Parole", "RP_ParoleLabel".Translate(p.LabelShort), text, map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>
        /// The warden's letter for a hearing that has been held in the hearing room: who sat, who spoke for and against them, the reoffend band, and
        /// the odds the board has before it. The word still counts as it always has.
        /// </summary>
        public static void AskWardenAfterSitting(PrisonGame game, Map map, Pawn p, ParoleCase hc)
        {
            CompInmate c = p.Inmate();
            int seen = c.records.Count(r => r.seen && r.def.severity >= 2);
            string board = hc.boardName != null ? "RP_ParoleBoard_Admin".Translate(hc.boardName).ToString() : "RP_ParoleBoard_Chair".Translate().ToString();
            string escort = hc.escorted ? "RP_ParoleEscort".Translate(hc.escortName).ToString() : "RP_ParoleNoEscort".Translate().ToString();
            TaggedString text = "RP_ParoleSceneText".Translate(p.LabelShort, c.crime?.label ?? "?", c.DaysServed.ToString("0"), c.TermDays, board, escort, seen, c.programsPassed, c.hoursWorked,
                c.tension.ToString("0"), hc.forText, hc.againstText, ParoleScene.BandLine(hc), hc.chance.ToStringPercent(), game.story.boardTrust.ToString("0.00"));
            ChoiceLetter_PrisonDecision letter = ChoiceLetter_PrisonDecision.Make("RP_Decision_Parole", "RP_ParoleLabel".Translate(p.LabelShort), text, map, p);
            letter.pawns.Add(p);
            letter.StartTimeout(GenDate.TicksPerHour * 12);
            Find.LetterStack.ReceiveLetter(letter);
        }

        /// <summary>Release: a recommended parolee who will stay out quietly builds the board's trust.</summary>
        public static void Notify_Released(PrisonGame game, bool recommended, bool reoffends)
        {
            if (recommended && !reoffends) game.story.boardTrust = Mathf.Min(TrustMax, game.story.boardTrust + 0.05f);
        }

        /// <summary>News of a rearrest: a recommendation that backfired costs trust and reputation.</summary>
        public static void Notify_Rearrested(PrisonGame game, Rearrest r)
        {
            if (!r.recommended) return;
            game.story.boardTrust = Mathf.Max(TrustMin, game.story.boardTrust - 0.15f);
            game.reputation = Mathf.Max(0f, game.reputation - 1f);
            Story.Record(RPChronicleDefOf.RP_Chr_Release, 2, "RP_Chr_RecommendationBackfired".Translate(r.name));
        }
    }

    public class ParoleDecision : PrisonDecisionWorker
    {
        private static readonly string[] Keys = { "RP_Parole_Recommend", "RP_Parole_Oppose", "RP_Parole_Abstain" };

        public override IReadOnlyList<string> Options(ChoiceLetter_PrisonDecision l) => Keys;

        public override int TimeoutChoice => 2;

        public override void Choose(ChoiceLetter_PrisonDecision l, Map map, int option)
        {
            Pawn p = l.pawns.Count > 0 ? l.pawns[0] : null;
            PrisonGame game = PrisonGame.Instance;
            int word = option == 0 ? 1 : option == 1 ? -1 : 0;
            // A hearing held in the hearing room goes on to its verdict; the board's own roll (no room) is the old way.
            if (p != null && ParoleScene.CaseOf(game, p) is { } held)
            {
                if (held.stage == HearingStage.Held) ParoleScene.Verdict(game, held, p, word);
                return;
            }
            if (p == null || !InmateUtility.IsActiveInmate(p)) return;
            // (Charged with a murder since the letter came: the hearing is adjourned until the court has sat.)
            if (Trials.OnRemand(game, p))
            {
                Messages.Message("RP_ParoleAdjourned".Translate(p.LabelShort), p, MessageTypeDefOf.NeutralEvent, historical: false);
                return;
            }
            bool paroled = ParoleHearings.Hold(game, p, word);
            Messages.Message((paroled ? "RP_ParoleGranted" : "RP_ParoleRefused").Translate(p.LabelShort), p, paroled ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NeutralEvent);
        }
    }

    /// <summary>
    /// Back through the same gate: half of those rearrested are sent here again, as the same person (their history,
    /// friends and enemies inside, the people who knew them) with a new, longer sentence.
    /// </summary>
    public static class Reoffenders
    {
        public const int MaxKept = 30;

        public static void KeepForReturn(PrisonGame game, Pawn p)
        {
            Find.WorldPawns.ForcefullyKeptPawns.Add(p);
            // Bounded memory: the oldest kept ex-inmates are let go.
            List<Rearrest> kept = game.rearrests.Where(r => r.pawn != null).ToList();
            for (int i = 0; i < kept.Count - MaxKept; i++) Forget(kept[i]);
        }

        public static void Forget(Rearrest r)
        {
            Pawn p = r.pawn;
            r.pawn = null;
            // (The register or a supervision case may keep the same person: the keep goes only when nobody else holds them.)
            if (p != null && !Supervision.HeldElsewhere(PrisonGame.Instance, p)) Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
        }

        /// <summary>Hourly: someone rearrested is due back and there is a bed for them: the state's marshals bring them on the next daily shuttle (first on its roster, see <see cref="ShuttleSystem.marshals"/>).</summary>
        public static void Hourly(PrisonGame game, Map map)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = game.returning.Count - 1; i >= 0; i--)
            {
                Rearrest r = game.returning[i];
                if (now < r.dueTick) continue;
                Pawn p = r.pawn;
                if (p == null || p.Dead || p.Destroyed || p.Spawned || p.Inmate() is not { } c)
                {
                    game.returning.RemoveAt(i);
                    if (p != null && !Supervision.HeldElsewhere(game, p)) Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
                    continue;
                }
                if (IntakeSystem.FreeBedCount(map, c.security, gender: p.gender) == 0) continue;
                game.returning.RemoveAt(i);
                game.shuttle.AddMarshal(p, MarshalKind.Returning, r.reason);
            }
        }

        /// <param name="reason">Why they were arrested again (a keyed phrase): the letter and the chronicle name it.</param>
        /// <param name="at">Where they step out (the ramp, off the shuttle); the intake spot if none.</param>
        internal static void Return(PrisonGame game, Map map, Pawn p, CompInmate c, Building_Bed bed, string reason = null, IntVec3? at = null)
        {
            // (Back as an inmate: no longer on the way out, if the shuttle had not yet taken them.)
            game.shuttle.Forget(p);
            Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
            if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
            p.mindState?.mentalStateHandler?.Reset();
            p.guest.Released = false;
            // A new conviction: often the same thing again, and the judge remembers.
            CrimeDef old = c.crime;
            if (c.security.crimes.Count > 0 && Rand.Chance(0.4f)) c.crime = c.security.crimes.RandomElement();
            if (!c.IsLifer && c.crime != null) c.sentenceDays = Mathf.RoundToInt(c.crime.sentenceDays.RandomInRange * 1.5f);
            c.admittedTick = Find.TickManager.TicksGame;
            c.timesAdmitted++;
            c.processed = false;
            // A new stay: nothing of the last one carries over (Release already cleared it; this covers any other way out).
            c.ResetForNewStay();
            // (Their rank in the gang is another's now, if the gang went on without them.)
            GangRanks.Notify_Returned(map, p, c);
            // A new stay, a new goal (the old one belonged to the last).
            c.want = null;
            Wants.Roll(p, c);
            c.tension = Mathf.Max(c.tension, 30f);
            GenSpawn.Spawn(p, at ?? IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            InmateGenerator.MakeInmate(p, bed);
            if (PrisonMap.Of(map)?.zones.Any(RPDefOf.RP_Holding) == true)
            {
                c.awaitingSearch = true;
                c.holdingSinceTick = Find.TickManager.TicksGame;
            }
            game.stats.admitted++;
            game.stats.reoffendersReturned++;
            string why = reason?.Translate();
            Story.Record(RPChronicleDefOf.RP_Chr_Intake, 2, why != null ? "RP_Chr_ReturnedFor".Translate(p.LabelShort, c.crime?.label ?? "?", c.timesAdmitted, why)
                : "RP_Chr_Returned".Translate(p.LabelShort, c.crime?.label ?? "?", c.timesAdmitted), p);
            TaggedString text = "RP_ReturnedText".Translate(p.LabelShort, c.crime?.label ?? "?", c.timesAdmitted,
                c.IsLifer ? "RP_Chr_LifeSentence".Translate().ToString() : "RP_Chr_Days".Translate(c.TermDays).ToString());
            if (why != null) text += " " + "RP_ReturnedReason".Translate(why);
            Find.LetterStack.ReceiveLetter("RP_ReturnedLabel".Translate(p.LabelShort), text, LetterDefOf.NeutralEvent, p);
        }
    }
}
