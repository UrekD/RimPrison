using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>What happened today; graded and reset at midnight.</summary>
    public class DailyIncidents : IExposable
    {
        public int misconducts, violent, escapes, deaths, contrabandFound, contrabandSmuggled, inmateWages, commissarySales;
        /// <summary>Smuggling rings broken whole today (each lifts the security grade, see <see cref="PrisonGrading"/>).</summary>
        public int ringsBusted;
        public int complianceSamples;
        public float complianceSum;
        /// <summary>The prison's power draw, sampled every hour of the day: the utility bill is the average, not the one moment of midnight.</summary>
        public int powerSamples;
        public float powerWattSum;
        /// <summary>Meals the kitchens finished today (the stoves' bills and the inmate cooks) and meals inmates ate today: the warden's desk shows both (see <see cref="DeskBoard"/>).</summary>
        public int mealsCooked, mealsEaten;
        /// <summary>Misconducts today by the security level of whoever did them (the wing block of the overview); reset at midnight with the rest.</summary>
        public Dictionary<SecurityLevelDef, int> misconductByLevel = new Dictionary<SecurityLevelDef, int>();

        public void NoteMisconduct(SecurityLevelDef level)
        {
            misconducts++;
            if (level == null) return;
            misconductByLevel.TryGetValue(level, out int n);
            misconductByLevel[level] = n + 1;
        }

        public int MisconductsOn(SecurityLevelDef level) => level != null && misconductByLevel.TryGetValue(level, out int n) ? n : 0;

        public void ExposeData()
        {
            Scribe_Values.Look(ref misconducts, "misconducts");
            Scribe_Values.Look(ref violent, "violent");
            Scribe_Values.Look(ref escapes, "escapes");
            Scribe_Values.Look(ref deaths, "deaths");
            Scribe_Values.Look(ref contrabandFound, "contrabandFound");
            Scribe_Values.Look(ref contrabandSmuggled, "contrabandSmuggled");
            Scribe_Values.Look(ref inmateWages, "inmateWages");
            Scribe_Values.Look(ref commissarySales, "commissarySales");
            Scribe_Values.Look(ref ringsBusted, "ringsBusted");
            Scribe_Values.Look(ref complianceSamples, "complianceSamples");
            Scribe_Values.Look(ref complianceSum, "complianceSum");
            Scribe_Values.Look(ref powerSamples, "powerSamples");
            Scribe_Values.Look(ref powerWattSum, "powerWattSum");
            Scribe_Values.Look(ref mealsCooked, "mealsCooked");
            Scribe_Values.Look(ref mealsEaten, "mealsEaten");
            Scribe_Collections.Look(ref misconductByLevel, "misconductByLevel", LookMode.Def, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                misconductByLevel ??= new Dictionary<SecurityLevelDef, int>();
                misconductByLevel.RemoveAll(kv => kv.Key == null);
            }
        }
    }

    public class ReportCard : IExposable
    {
        public float safety, security, health, conditions, order, reform;
        /// <summary>The inmates the card was worked out for (those waiting for the shuttle to leave are not counted). With none there is nothing to grade: the screens show a dash, not a grade.</summary>
        public int inmates;

        /// <summary>The numbers the grade was worked out from, kept with the card so the overview can say why it moved (see <see cref="GradeDrivers"/>). False for a card that did not keep them (a save from before): it shows "no detail".</summary>
        public bool detailed;
        public int violent, deaths, escapes, contrabandSmuggled, contrabandFound, ringsBusted, injured, employed, reformed, released, complianceSamples;
        /// <summary>The inmates' mean mood (0 to 1), the day's compliance sum (see <see cref="PrisonGrading.SampleCompliance"/>), the share of the last two weeks' releases that were paroled, and the share of all the released who stayed out.</summary>
        public float meanMood, complianceSum, paroleShare, stayedOut;

        public void ExposeData()
        {
            Scribe_Values.Look(ref safety, "safety");
            Scribe_Values.Look(ref security, "security");
            Scribe_Values.Look(ref health, "health");
            Scribe_Values.Look(ref conditions, "conditions");
            Scribe_Values.Look(ref order, "order");
            Scribe_Values.Look(ref reform, "reform");
            Scribe_Values.Look(ref inmates, "inmates");
            Scribe_Values.Look(ref detailed, "detailed");
            Scribe_Values.Look(ref violent, "violent");
            Scribe_Values.Look(ref deaths, "deaths");
            Scribe_Values.Look(ref escapes, "escapes");
            Scribe_Values.Look(ref contrabandSmuggled, "contrabandSmuggled");
            Scribe_Values.Look(ref contrabandFound, "contrabandFound");
            Scribe_Values.Look(ref ringsBusted, "ringsBusted");
            Scribe_Values.Look(ref injured, "injured");
            Scribe_Values.Look(ref employed, "employed");
            Scribe_Values.Look(ref reformed, "reformed");
            Scribe_Values.Look(ref released, "released");
            Scribe_Values.Look(ref complianceSamples, "complianceSamples");
            Scribe_Values.Look(ref meanMood, "meanMood");
            Scribe_Values.Look(ref complianceSum, "complianceSum");
            Scribe_Values.Look(ref paroleShare, "paroleShare");
            Scribe_Values.Look(ref stayedOut, "stayedOut");
        }

        public bool Graded => inmates > 0;

        public float Overall => safety * 0.25f + security * 0.2f + health * 0.15f + conditions * 0.15f + order * 0.15f + reform * 0.1f;

        public string Describe()
        {
            var sb = new StringBuilder();
            void Line(string key, float v) => sb.AppendLine($"  {key.Translate()}: {PrisonGrading.Letter(v)} ({v:F0})");
            Line("RP_Grade_Safety", safety);
            Line("RP_Grade_Security", security);
            Line("RP_Grade_Health", health);
            Line("RP_Grade_Conditions", conditions);
            Line("RP_Grade_Order", order);
            Line("RP_Grade_Reform", reform);
            return sb.ToString().TrimEndNewlines();
        }
    }

    public static class PrisonGrading
    {
        /// <summary>Security points for each ring broken whole in a day (two count at most).</summary>
        public const float RingBustBonus = 20f;

        public static string Letter(float v) => v >= 85f ? "A" : v >= 70f ? "B" : v >= 55f ? "C" : v >= 40f ? "D" : "F";

        public static ReportCard Compute(Map map, PrisonGame game)
        {
            DailyIncidents d = game.today;
            var card = new ReportCard();
            // (Those waiting for the shuttle to leave are no longer the prison's inmates: not counted, not graded.)
            var inmates = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                if (!ShuttleSystem.OnTheWayOut(p)) inmates.Add(p);
            card.inmates = inmates.Count;
            float n = Mathf.Max(1, inmates.Count);
            // Incidents per capita, so a fight costs a 10-inmate and a 100-inmate prison the same share (at least 5:
            // one incident in a two-inmate start-up is not a disaster).
            float pc = Mathf.Max(5, inmates.Count);
            card.safety = Mathf.Clamp(100f - 120f * d.violent / pc - 200f * d.deaths / pc, 0f, 100f);
            // Finding what was smuggled in only takes back half of what getting it in cost: a prison that finds a lot is never graded above one that never had any.
            // A smuggling ring broken whole is worth a good deal more than the finds it took: the supply chain is gone, not one delivery.
            card.security = Mathf.Clamp(100f - 400f * d.escapes / pc - 30f * d.contrabandSmuggled / pc + 15f * Mathf.Min(d.contrabandFound, d.contrabandSmuggled) / pc
                                        + RingBustBonus * Mathf.Min(2, d.ringsBusted), 0f, 100f);
            float mood = 0f, injured = 0f, employed = 0f, reformed = 0f;
            int today = Ledger.CurrentDay;
            foreach (Pawn p in inmates)
            {
                mood += p.needs.mood?.CurLevelPercentage ?? 0.5f;
                if (p.health.HasHediffsNeedingTend() || p.Downed) injured++;
                // Work: those who were at work (today or yesterday: the grade is taken just after midnight), not everyone who holds a job (a bench with no steel is no work).
                if (p.Inmate() is { job: not null } worker && today - worker.lastWorkDay <= 1) employed++;
                if (p.Inmate() is { passedPrograms.Count: > 0 }) reformed++;
            }
            card.health = Mathf.Clamp(100f - 150f * injured / n - 240f * d.deaths / pc, 0f, 100f);
            card.conditions = Mathf.Clamp(mood / n * 130f - 5f, 0f, 100f);
            card.order = d.complianceSamples == 0 ? 75f : Mathf.Clamp(d.complianceSum / d.complianceSamples * 110f, 0f, 100f);
            // Reform: work, programs, parole, and whether the people you let out stay out.
            // (Releases still under review have no outcome yet: only those whose outcome has been seen count, so a new release neither lifts nor sinks the figure before its review is over.)
            float stayedOut = StayedOutShare(game);
            // (Programs and parole as they stand now: who here has passed one, and how the releases of the last two weeks went: lifetime ratios hardly move after the first weeks.)
            float paroleShare = RecentParoleShare(game);
            card.reform = Mathf.Clamp(25f + 40f * employed / n + 15f * reformed / n + 10f * paroleShare + 20f * stayedOut, 0f, 100f);
            // What it was worked out from, for the overview's "why".
            card.detailed = true;
            (card.violent, card.deaths, card.escapes, card.contrabandSmuggled, card.contrabandFound, card.ringsBusted) = (d.violent, d.deaths, d.escapes, d.contrabandSmuggled, d.contrabandFound, d.ringsBusted);
            (card.injured, card.employed, card.reformed, card.released) = ((int)injured, (int)employed, (int)reformed, game.stats.released);
            (card.complianceSamples, card.complianceSum, card.meanMood, card.paroleShare, card.stayedOut) = (d.complianceSamples, d.complianceSum, mood / n, paroleShare, stayedOut);
            return card;
        }

        public const int ReformWindowDays = 14;

        /// <summary>The share of those released whose outcome is known who stayed out (0.5 with none): releases still under supervision (see <see cref="Supervision"/>) are not in it.</summary>
        public static float StayedOutShare(PrisonGame game)
        {
            int observed = game.stats.released - game.supervision.OpenCount;
            return observed > 0 ? 1f - Mathf.Min(game.stats.reoffended, observed) / (float)observed : 0.5f;
        }

        /// <summary>The share of the last two weeks' releases that were paroled (none released: 0).</summary>
        public static float RecentParoleShare(PrisonGame game)
        {
            int now = Find.TickManager.TicksGame, released = 0, paroled = 0;
            foreach (FormerInmate e in game.story.formerInmates)
            {
                if (now - e.releasedTick > ReformWindowDays * GenDate.TicksPerDay) continue;
                released++;
                if (e.paroled) paroled++;
            }
            return released > 0 ? paroled / (float)released : 0f;
        }

        /// <summary>Hourly sample of how many inmates are where the regime wants them.</summary>
        public static void SampleCompliance(Map map, PrisonGame game)
        {
            PrisonMap pm = PrisonMap.Of(map);
            int n = 0, ok = 0;
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (!InmateUtility.IsActiveInmate(p) || !RegimePlacement.InTheSample(p)) continue;
                n++;
                if (RegimePlacement.InExpectedPlace(p, game.CurrentActivity(p), pm)) ok++;
            }
            if (n == 0) return;
            game.today.complianceSamples++;
            game.today.complianceSum += ok / (float)n;
        }
    }

    /// <summary>Parole hearings and reoffending.</summary>
    public static class ParoleSystem
    {
        public const int ParoleBonus = 400;

        /// <summary>An inmate is heard at most once in this many days.</summary>
        public const int HearingGapDays = 4;

        /// <summary>The board's odds rise by this much for an inmate the warden told where they stand (see <see cref="WardenSystem"/>), for <see cref="PreparedDays"/> days after.</summary>
        public const float PreparedBonus = 0.06f;
        public const int PreparedDays = 4;

        public static bool Prepared(CompInmate c) => c.headsUpTick >= 0 && Find.TickManager.TicksGame - c.headsUpTick < PreparedDays * GenDate.TicksPerDay;

        /// <summary>No parole from a level that keeps its inmates to their cells (supermax): the way down is the step-down, and parole is the way out of the levels below it.</summary>
        public static bool MayBeParoled(CompInmate c) => c?.security is { escortOnly: false };

        /// <summary>Past half their sentence, not heard in the last few days, and nothing holds them: not for life, not on remand (an open murder case: no parole hearing until the court has sat), not in supermax.</summary>
        internal static bool HearingDue(PrisonGame game, Pawn p, CompInmate c) =>
            c != null && !c.IsLifer && MayBeParoled(c) && c.activeFlag && !Trials.OnRemand(game, p)
            && c.DaysServed >= c.TermDays * 0.5f && Find.TickManager.TicksGame - c.lastHearingTick >= GenDate.TicksPerDay * HearingGapDays;

        public static void DailyHearings(PrisonGame game, Map map)
        {
            var paroled = new List<Pawn>();
            var due = new List<Pawn>();
            foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
            {
                if (HearingDue(game, p, p.Inmate())) due.Add(p);
            }
            // With a hearing room, those it can hold are called to it in the board's hours (see ParoleScene); the rest are the board's to roll, as without one.
            List<Pawn> scheduled = ParoleScene.Schedule(game, map, due, out int overflow);
            foreach (Pawn p in scheduled) due.Remove(p);
            if (overflow > 0) Messages.Message("RP_ParoleOverflow".Translate(overflow), MessageTypeDefOf.NeutralEvent, historical: false);
            // The closest call comes to the warden's desk; the board decides the rest on its own. (Whoever the room hears gets the warden's letter at their hearing instead.)
            Pawn asked = scheduled.Count > 0 ? null : ParoleHearings.PickForWarden(due);
            if (asked != null)
            {
                asked.Inmate().lastHearingTick = Find.TickManager.TicksGame;
                ParoleHearings.AskWarden(game, map, asked);
            }
            foreach (Pawn p in due)
            {
                if (p == asked) continue;
                if (BoardDecides(p, p.Inmate())) paroled.Add(p);
            }
            foreach (Pawn p in paroled) Grant(game, p);
            if (paroled.Count > 0)
                Messages.Message("RP_Paroled".Translate(paroled.Count, Money.Format(ParoleBonus * paroled.Count)), new LookTargets(paroled), MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>The board lets them go on parole (the grant and the release).</summary>
        internal static void Grant(PrisonGame game, Pawn p)
        {
            game.stats.paroled++;
            game.ledger.Add(RPDefOf.RP_Grants, ParoleBonus, "RP_ParoleNote".Translate(p.LabelShort));
            ReleaseSystem.Release(game, p, paroled: true);
        }

        /// <summary>The board hears them on its own: true if they go, else their goal is set back.</summary>
        internal static bool BoardDecides(Pawn p, CompInmate c)
        {
            if (!MayBeParoled(c)) return false;
            c.lastHearingTick = Find.TickManager.TicksGame;
            if (Rand.Chance(ParoleChance(c))) return true;
            Wants.Notify_ParoleDenied(p, c);
            return false;
        }

        public static float ParoleChance(CompInmate c) => ParoleChance(c, recentIncidents: true);

        /// <param name="recentIncidents">Count the serious incidents seen in the last five days (the board does); a goal that measures what the inmate did leaves them out, since they age out by themselves.</param>
        public static float ParoleChance(CompInmate c, bool recentIncidents)
        {
            int recent = 0;
            if (recentIncidents)
                foreach (MisconductRecord r in c.records)
                    if (r.seen && Find.TickManager.TicksGame - r.tick < GenDate.TicksPerDay * 5 && r.def.severity >= 2) recent++;
            float chance = 0.15f + 0.3f * (1f - c.tension / 100f) + Mathf.Min(0.2f, c.hoursWorked / 200f) + 0.1f * c.programsPassed - 0.15f * recent;
            if (c.HasReputation(ReputationDefOf.RP_ModelPrisoner)) chance += 0.15f;
            // (Not in what a goal measures of what they made of themselves: being told is not a gain.)
            if (recentIncidents && Prepared(c)) chance += PreparedBonus;
            // (A positive drug test or a refusal, lately: the board reads it, though it is a mild record. Like the incidents, not what a goal measures.)
            if (recentIncidents && DrugTesting.FailedRecently(c)) chance -= DrugTesting.ParolePenalty;
            return Mathf.Clamp(chance, 0f, 0.9f);
        }

        /// <summary>A certified trade (see <see cref="Vocation"/>) takes this much off the reoffend chance, on top of what the program counts for.</summary>
        public const float TradeReoffendCut = 0.1f;

        /// <summary>Rolled on every release: work, programs and a certified trade lower the odds; amends made with the victim's family take the band a step lower (see <see cref="Amends.StepDown"/>).</summary>
        public static float ReoffendChance(CompInmate c)
        {
            float risk = Mathf.Clamp(0.55f - 0.12f * c.programsPassed - Mathf.Min(0.2f, c.hoursWorked / 150f) - (c.HasReputation(ReputationDefOf.RP_ModelPrisoner) ? 0.15f : 0f)
                                     - (c.jobWaiting ? 0.15f : 0f) - (Vocation.HasTrade(c) ? TradeReoffendCut : 0f), 0.05f, 0.9f);
            return Amends.Made(c) ? Amends.StepDown(risk) : risk;
        }
    }
}
