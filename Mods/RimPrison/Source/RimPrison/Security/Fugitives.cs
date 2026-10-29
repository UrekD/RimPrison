using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimPrison
{
    /// <summary>An escaped inmate the police are looking for.</summary>
    public class Fugitive : IExposable
    {
        public Pawn pawn;
        public int returnTick = -1;
        public int attempts;
        public int escapedTick = -1;
        /// <summary>How long they had before the police were told (a count that found the cell empty at six is a six hours' head start; seen going, none): the odds of getting them back fell with it.</summary>
        public int headStart;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn", saveDestroyedThings: false);
            Scribe_Values.Look(ref returnTick, "returnTick", -1);
            Scribe_Values.Look(ref attempts, "attempts");
            Scribe_Values.Look(ref escapedTick, "escapedTick", -1);
            Scribe_Values.Look(ref headStart, "headStart");
        }
    }

    /// <summary>
    /// Escapes are not always the end of the story: most escapees are recaptured within a few days and brought back
    /// with a longer sentence (the state pays nothing for the return, but it wins back some reputation).
    /// </summary>
    public static class FugitiveSystem
    {
        public const float RecaptureChance = 0.6f;
        public const float ExtraSentenceFactor = 0.5f;

        /// <summary>The police are looking for this escapee (they may be brought back): their open case waits for the recapture.</summary>
        public static bool Tracks(PrisonGame game, int pawnId) =>
            game.fugitives.Exists(f => f.pawn != null && f.pawn.thingIDNumber == pawnId) || game.shuttle.marshals.Exists(m => m.kind == MarshalKind.Recaptured && m.pawn != null && m.pawn.thingIDNumber == pawnId);

        /// <summary>
        /// The police are told of an escape: <paramref name="escapedTick"/> is when they got away (-1: now, an escape that was seen). Every hour of the head start before they were told takes
        /// something off the odds of getting the runaway back (<see cref="CountSystem.RecaptureChanceAfter"/>), and the search runs from now.
        /// </summary>
        public static void Notify_Escaped(Pawn p, PrisonGame game, int escapedTick = -1)
        {
            int now = Find.TickManager.TicksGame;
            int gone = escapedTick >= 0 ? escapedTick : now;
            int headStart = Mathf.Max(0, now - gone);
            if (!Rand.Chance(CountSystem.RecaptureChanceAfter(headStart))) return;
            game.fugitives.Add(new Fugitive { pawn = p, escapedTick = gone, headStart = headStart, returnTick = now + Mathf.RoundToInt(Rand.Range(1f, 4f) * GenDate.TicksPerDay) });
            Find.WorldPawns.ForcefullyKeptPawns.Add(p);
        }

        /// <summary>Hourly: the police have caught whoever is due (if there is a bed for them): the state's marshals bring them on the next daily shuttle, first on its roster.</summary>
        public static void Hourly(Map map, PrisonGame game)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = game.fugitives.Count - 1; i >= 0; i--)
            {
                Fugitive f = game.fugitives[i];
                // Gone, back already, or held somewhere (a caravan or pod): not ours to bring back.
                if (f.pawn == null || f.pawn.Dead || f.pawn.Destroyed || f.pawn.Spawned || f.pawn.ParentHolder != null || f.pawn.IsCaravanMember())
                {
                    Drop(game, i);
                    continue;
                }
                if (now < f.returnTick) continue;
                CompInmate c = f.pawn.Inmate();
                if (c == null)
                {
                    Drop(game, i);
                    continue;
                }
                if (IntakeSystem.FreeBedCount(map, c.security, gender: f.pawn.gender) == 0)
                {
                    // No bed: the state holds them elsewhere for a day, then sends them to another prison.
                    if (++f.attempts >= 3) Drop(game, i);
                    else f.returnTick = now + GenDate.TicksPerDay;
                    continue;
                }
                // (Moved to the shuttle's list, which keeps them in the world.)
                game.shuttle.AddMarshal(f.pawn, MarshalKind.Recaptured, escapedTick: f.escapedTick);
                game.fugitives.RemoveAt(i);
            }
        }

        /// <summary>The recaptured escapee steps out at <paramref name="at"/> (off the shuttle) with a longer sentence; <paramref name="escapedTick"/> is when they got away.</summary>
        internal static void Return(Map map, PrisonGame game, Pawn p, int escapedTick, CompInmate c, Building_Bed bed, IntVec3? at = null)
        {
            Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
            if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
            p.mindState?.mentalStateHandler?.Reset();
            GenSpawn.Spawn(p, at ?? IntakeSystem.IntakeCell(map, IntakeSystem.IntakeSpot(map)), map);
            InmateGenerator.MakeInmate(p, bed);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_Recaptured".Translate(p.LabelShort), p);
            c.processed = false;
            c.tunnelProgress = 0f;
            if (PrisonMap.Of(map)?.zones.Any(RPDefOf.RP_Holding) == true)
            {
                c.awaitingSearch = true;
                c.holdingSinceTick = Find.TickManager.TicksGame;
                if (Rand.Chance(IntakeSystem.ArrivalContrabandChance * 2f)) ContrabandUtility.GiveSmuggled(p);
            }
            // Time on the run does not count as time served.
            if (escapedTick >= 0 && c.admittedTick >= 0) c.admittedTick += Find.TickManager.TicksGame - escapedTick;
            int extra = 0;
            if (!c.IsLifer)
            {
                extra = Mathf.Max(3, Mathf.RoundToInt(c.sentenceDays * ExtraSentenceFactor));
                c.sentenceDays += extra;
            }
            c.tension = Mathf.Max(c.tension, 40f);
            game.stats.recaptured++;
            game.reputation = Mathf.Min(100f, game.reputation + 1f);
            Find.LetterStack.ReceiveLetter("RP_RecapturedLabel".Translate(), "RP_RecapturedText".Translate(p.LabelShort, extra), LetterDefOf.NeutralEvent, p);
        }

        private static void Drop(PrisonGame game, int index)
        {
            Pawn p = game.fugitives[index].pawn;
            if (p != null) Find.WorldPawns.ForcefullyKeptPawns.Remove(p);
            game.fugitives.RemoveAt(index);
        }
    }
}
