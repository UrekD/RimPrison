using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimPrison
{
    [DefOf]
    public static class RPCondolenceDefOf
    {
        public static MailKindDef RP_Mail_Condolence;
        public static MailKindDef RP_Mail_CondolenceBitter;

        static RPCondolenceDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RPCondolenceDefOf));
    }

    /// <summary>A family's letter to the warden about one of the dead, on its way: who (by name), from whom, which kind of letter, and when it arrives.</summary>
    public class Condolence : IExposable
    {
        public MailKindDef kind;
        public string name, from;
        /// <summary>One of the staff (their family is the prison's own to look after, so the letter reads differently).</summary>
        public bool staff;
        public int dueTick;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref kind, "kind");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref from, "from");
            Scribe_Values.Look(ref staff, "staff");
            Scribe_Values.Look(ref dueTick, "due");
        }

        public string Describe() => $"{kind?.defName}:{name}:{from}:{staff}:{dueTick}";
    }

    /// <summary>A condolence is not post for an inmate: the kind is the family's letter to the warden, so the mailroom never draws it (see <see cref="CondolenceSystem"/>).</summary>
    public class MailKindWorker_Condolence : MailKindWorker
    {
        public override bool CanSend(Pawn p, CompInmate c) => false;
    }

    /// <summary>
    /// What happens to the prison's dead once they are in the morgue (<see cref="Morgue"/>): at lift-off the state shuttle takes every body that lies there, and the family of the dead, if there is one, writes
    /// to the warden a day or three later. The letter is bitter when the death was the prison's fault (a killing by staff, or neglect: the two deaths that open a state inquiry); a murder by another inmate is not.
    /// An inmate's family is whoever of their relatives is still in touch; a member of the prison's staff has one too, unless they were here for a few hours as a callout. A body with no family is not sent: if the
    /// prison has a burial plot (<see cref="Burial"/>) that can take it the workmen bury it there; with no plot, or one that cannot take it, it goes with the others and nobody writes.
    /// </summary>
    public static class CondolenceSystem
    {
        public const int MinDays = 1, MaxDays = 3, MaxFaults = 200;

        /// <summary>The death was the prison's fault: the family's letter will say so (noted while the pawn is still alive, in the kill's prefix).</summary>
        public static void Notify_Fault(Pawn victim)
        {
            List<int> list = PrisonGame.Instance.story.bitterDeaths;
            if (list.Contains(victim.thingIDNumber)) return;
            list.Add(victim.thingIDNumber);
            // (A body that never reaches the shuttle, burnt or lost, leaves its entry behind: the oldest go first.)
            if (list.Count > MaxFaults) list.RemoveAt(0);
        }

        /// <summary>The note is withdrawn: the pawn is alive again (the note is written before the death and would otherwise bitter a later one).</summary>
        public static void ClearFault(Pawn pawn) => PrisonGame.Instance?.story.bitterDeaths.Remove(pawn.thingIDNumber);

        public static bool AtFault(Pawn dead) => PrisonGame.Instance.story.bitterDeaths.Contains(dead.thingIDNumber);

        /// <summary>Whether somebody would write about this death, and who (the relative's label; null for staff, whose family is not named).</summary>
        public static bool HasFamily(Pawn dead, out string from, out bool staff)
        {
            from = null;
            staff = false;
            if (dead.GetComp<CompStaff>() is { role: not null } sc)
            {
                staff = true;
                return sc.leaveAtTick < 0;
            }
            if (dead.Inmate() is { } c && Relatives.Of(c).Find(r => r.CanVisit) is { } relative)
            {
                from = relative.Label;
                return true;
            }
            return false;
        }

        public static bool HasFamily(Pawn dead) => HasFamily(dead, out _, out _);

        /// <summary>
        /// Lift-off: every body of the prison's own in the morgue goes with the shuttle, but the ones the plot will take now (no family, a plot painted, and ground a workman can dig and reach: see
        /// <see cref="Burial.Capacity"/>); a body the plot cannot take (full, no soil, outside the grounds, walled off, no workman) goes as one with a family does, with no letter if nobody would write, and the warden
        /// is told so in the one message. Says how many left.
        /// </summary>
        public static int Ship(Map map, PrisonGame game)
        {
            if (PrisonMap.Of(map) is not { } pm) return 0;
            var bodies = new List<Corpse>();
            var wanted = new List<Corpse>();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
                if (t is Corpse c && Morgue.Ours(c) && Morgue.InMorgue(c, pm)) (Burial.Wants(c, pm) ? wanted : bodies).Add(c);
            // The oldest wait for the graves there are room for; the rest are not held for a plot that cannot take them.
            int room = wanted.Count > 0 ? Burial.Capacity(map, pm, wanted.Count) : 0;
            wanted.Sort((a, b) => b.Age.CompareTo(a.Age));
            int refused = 0;
            for (int i = room; i < wanted.Count; i++)
            {
                bodies.Add(wanted[i]);
                refused++;
            }
            foreach (Corpse c in bodies) Send(game, c);
            if (bodies.Count > 0)
            {
                string text = "RP_BodiesSent".Translate(bodies.Count);
                if (refused > 0) text += " " + "RP_BodiesNoPlot".Translate(refused);
                Messages.Message(text, refused > 0 ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.NeutralEvent, historical: false);
            }
            return bodies.Count;
        }

        internal static void Send(PrisonGame game, Corpse corpse)
        {
            Pawn dead = corpse.InnerPawn;
            string name = dead.LabelShort;
            bool family = HasFamily(dead, out string from, out bool staff), fault = AtFault(dead), old = dead.Inmate() is { diedOfAge: true };
            corpse.Destroy();
            game.story.bitterDeaths.Remove(dead.thingIDNumber);
            game.stats.bodiesSent++;
            Funerals.Notify_Sent(dead);
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 1, "RP_Chr_BodySent".Translate(name), dead);
            if (!family) return;
            game.story.condolences.Add(new Condolence
            {
                kind = fault ? RPCondolenceDefOf.RP_Mail_CondolenceBitter : old ? RPOldAgeDefOf.RP_Mail_CondolenceOld : RPCondolenceDefOf.RP_Mail_Condolence,
                name = name,
                from = from,
                staff = staff,
                dueTick = Find.TickManager.TicksGame + Rand.RangeInclusive(MinDays, MaxDays) * GenDate.TicksPerDay,
            });
        }

        /// <summary>Hourly: the letters that have come.</summary>
        public static void Hourly(PrisonGame game)
        {
            List<Condolence> due = game.story.condolences;
            int now = Find.TickManager.TicksGame;
            for (int i = due.Count - 1; i >= 0; i--)
            {
                if (now < due[i].dueTick) continue;
                Condolence c = due[i];
                due.RemoveAt(i);
                Deliver(game, c);
            }
        }

        public static void Deliver(PrisonGame game, Condolence c)
        {
            MailKindDef kind = c.kind;
            string text = (kind.defName + (c.staff ? "_StaffText" : "_Text")).Translate(c.name, c.from ?? "").Resolve();
            Find.LetterStack.ReceiveLetter((kind.defName + "_Label").Translate(c.name), text, kind.reputation < 0f ? LetterDefOf.NegativeEvent : LetterDefOf.NeutralEvent);
            if (kind.reputation != 0f) game.reputation = Mathf.Max(0f, game.reputation + kind.reputation);
            if (kind.chronicleKey != null) Story.Record(kind == RPOldAgeDefOf.RP_Mail_CompassionateRelease ? RPChronicleDefOf.RP_Chr_Release : RPChronicleDefOf.RP_Chr_Death, kind.importance, kind.chronicleKey.Translate(c.name));
            game.stats.condolences++;
        }
    }

    /// <summary>A pawn who comes back to life (a resurrector serum, a death refusal) was not a death the prison answers for: the note of its fault, written before it died, goes.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.Notify_Resurrected))]
    public static class Patch_Pawn_HealthTracker_Resurrected_Fault
    {
        public static void Postfix(Pawn ___pawn) => CondolenceSystem.ClearFault(___pawn);
    }
}
