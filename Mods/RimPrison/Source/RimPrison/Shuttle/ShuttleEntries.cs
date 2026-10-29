using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace RimPrison
{
    public enum OutboundKind : byte
    {
        Released,
        Paroled,
        Transferred,
    }

    /// <summary>A released, paroled or transferred inmate on their way out: counted as released only when they leave the map.</summary>
    public class OutboundEntry : IExposable
    {
        public Pawn pawn;
        public OutboundKind kind;
        public int sinceTick;
        /// <summary>False for vanilla's own Release order: they leave by the shuttle too, but the warden has not served them out (no release for the reform grade).</summary>
        public bool counted = true;
        /// <summary>The release came with a reoffence to follow: counted when they leave, with the release.</summary>
        public bool reoffends;
        /// <summary>The supervision case that starts when they leave (see <see cref="Supervision"/>; -1: none, a legacy release, a vanilla release order or a transfer).</summary>
        public int caseId = -1;
        /// <summary>For a transfer: the guard walking them to the ramp (thing id), -1 none; since when the escort has had them.</summary>
        public int escortId = -1;
        public int escortTick = -1;
        /// <summary>Why they leave when it is not the plain release or transfer (a keyed string: compassionate release, the state's hospital), or null.</summary>
        public string cause;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref sinceTick, "since");
            Scribe_Values.Look(ref counted, "counted", true);
            Scribe_Values.Look(ref reoffends, "reoffends");
            Scribe_Values.Look(ref caseId, "caseId", -1);
            Scribe_Values.Look(ref escortId, "escortId", -1);
            Scribe_Values.Look(ref escortTick, "escortTick", -1);
            Scribe_Values.Look(ref cause, "cause");
        }
    }

    public enum MarshalKind : byte
    {
        /// <summary>An ex-inmate rearrested (a parole violation, a new offence): back for a new stay.</summary>
        Returning,
        /// <summary>An escapee the police have caught.</summary>
        Recaptured,
    }

    /// <summary>
    /// One of the state's own prisoners on their way back: a returning ex-inmate or a recaptured escapee, waiting in the world for the next daily shuttle (their bed is checked when the roster is made).
    /// They come first on the roster, are not paid for (the state pays nothing for the return), and are put through the same return as ever (<see cref="Reoffenders.Return"/>,
    /// <see cref="FugitiveSystem.Return"/>) as they step off.
    /// </summary>
    public class MarshalEntry : IExposable
    {
        public Pawn pawn;
        public MarshalKind kind;
        /// <summary>Why a returning ex-inmate was arrested (a keyed phrase, see <see cref="ParoleLife"/>); null for the rest.</summary>
        public string reason;
        /// <summary>For a recaptured escapee: when they got away (time on the run is not time served).</summary>
        public int escapedTick = -1;
        /// <summary>Since when they have waited for a shuttle that can take them.</summary>
        public int sinceTick;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref reason, "reason");
            Scribe_Values.Look(ref escapedTick, "escapedTick", -1);
            Scribe_Values.Look(ref sinceTick, "since");
        }
    }

    /// <summary>A new inmate who has stepped off the shuttle and is not yet in holding (or, without a holding zone, not yet in their cell).</summary>
    public class ArrivalEntry : IExposable
    {
        public Pawn pawn;
        public int sinceTick;
        /// <summary>The guard walking them (thing id), -1 none; since when the escort has had them.</summary>
        public int escortId = -1;
        public int escortTick = -1;
        /// <summary>When they stop waiting for a guard and walk to holding on their own (-1: <see cref="sinceTick"/> plus the wait, for an older save). <see cref="sinceTick"/> is only the clock of the ten hours to be inside.</summary>
        public int walkOnTick = -1;

        public int WalkOnAt => walkOnTick >= 0 ? walkOnTick : sinceTick + ShuttleSystem.EscortWaitTicks;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref sinceTick, "since");
            Scribe_Values.Look(ref walkOnTick, "walkOn", -1);
            Scribe_Values.Look(ref escortId, "escortId", -1);
            Scribe_Values.Look(ref escortTick, "escortTick", -1);
        }
    }
}
