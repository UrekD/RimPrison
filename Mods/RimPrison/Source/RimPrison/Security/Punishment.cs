using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RimPrison
{
    /// <summary>What a punishment takes away. Solitary (the hole, escorted by a guard) is the default for every
    /// misconduct; the others are lighter and need no escort.</summary>
    public enum Sanction : byte
    {
        Solitary,
        CellConfinement,
        LoseYard,
        LoseVisits,
        LoseCommissary,
        Warning,
    }

    /// <summary>The sanction and its hours per misconduct, set by the warden (defaults: solitary unless the def says otherwise, hours from the defs).</summary>
    public class PunishmentPolicy : IExposable
    {
        public Dictionary<MisconductDef, int> hours = new Dictionary<MisconductDef, int>();
        public Dictionary<MisconductDef, Sanction> kinds = new Dictionary<MisconductDef, Sanction>();

        /// <summary>The hours the warden set (or the def's): what the policy screen shows and edits, whatever the state allows.</summary>
        public int HoursFor(MisconductDef def)
        {
            def = def.sanctionAs ?? def;
            return hours.TryGetValue(def, out int h) ? h : def.punishmentHours;
        }

        /// <summary>The hours of solitary this misconduct earns: the warden's, within the state's cap while special measures hold (only solitary is capped, not the lighter sanctions).</summary>
        public int SolitaryHoursFor(MisconductDef def) => SpecialMeasures.LimitSolitary(HoursFor(def));

        public Sanction KindFor(MisconductDef def) => kinds.TryGetValue(def.sanctionAs ?? def, out Sanction k) ? k : (def.sanctionAs ?? def).defaultSanction;

        public void Set(MisconductDef def, int h) => hours[def] = h;

        public void SetKind(MisconductDef def, Sanction k) => kinds[def] = k;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref hours, "hours", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref kinds, "kinds", LookMode.Def, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                hours ??= new Dictionary<MisconductDef, int>();
                hours.RemoveAll(kv => kv.Key == null);
                kinds ??= new Dictionary<MisconductDef, Sanction>();
                kinds.RemoveAll(kv => kv.Key == null);
            }
        }
    }

    /// <summary>
    /// Graduated sanctions: the lighter ones start at once with no guard needed (and a guard's restraint after the act
    /// separates them without taking them to the hole). Cell confinement keeps them in their own cell (the regime's
    /// lockup, showers allowed; no bed to keep them in: the hole); no yard turns free time into time in the cell; no
    /// visits and no shop ban those for the hours; a warning goes on the record and no further. A light sanction eases
    /// some tension (less than the hole) and suppresses less; bans of different kinds run side by side, a second of the
    /// same kind extends the first.
    /// </summary>
    public static class Sanctions
    {
        public const float LightRelief = 15f, ConfinementSuppression = 0.5f;

        public static bool Banned(CompInmate c, Sanction kind)
        {
            int now = Find.TickManager.TicksGame;
            return kind switch
            {
                // (An inmate on watch is out of the yard as well: see WatchList.)
                Sanction.LoseYard => c.yardBanUntilTick > now || WatchList.OnWatch(c),
                // (A shot-caller's deal that opens the commissary to the gang's members holds their ban off while it runs: the ban's own clock goes on, and is back in force when the deal ends.)
                Sanction.LoseCommissary => c.shopBanUntilTick > now && !GangDeals.Waives(c, GangDealTerm.Commissary),
                Sanction.LoseVisits => c.visitBanUntilTick > now,
                _ => false,
            };
        }

        /// <summary>
        /// A seen misconduct: the warden's sheet takes it when they adjudicate the serious and it is one (see <see cref="Adjudication"/>: the case is queued, and decided at the sheet); else the
        /// policy applies. <paramref name="evidence"/>: found by staff directly (search, discovery), which counts as an officer's sighting on the sheet.
        /// </summary>
        public static void Apply(Pawn p, CompInmate c, MisconductDef def, bool evidence = false)
        {
            if (Adjudication.Takes(def) && Adjudication.Queue(p, c, def, evidence)) return;
            ApplyPolicy(p, c, def);
        }

        /// <summary>The policy's sanction for a misconduct (solitary: due, for a guard to carry out).</summary>
        public static void ApplyPolicy(Pawn p, CompInmate c, MisconductDef def, bool markLast = true)
        {
            PunishmentPolicy policy = PrisonGame.Instance.punishments;
            Sanction kind = policy.KindFor(def);
            int hours = kind == Sanction.Solitary ? policy.SolitaryHoursFor(def) : policy.HoursFor(def);
            if (hours <= 0 && kind != Sanction.Warning) return;
            Impose(p, c, def, kind, hours, markLast);
        }

        /// <summary>Whether the policy gives anything for the misconduct (a warning, or hours of a sanction): what <see cref="ApplyPolicy"/> would impose.</summary>
        public static bool PolicyImposes(MisconductDef def)
        {
            PunishmentPolicy policy = PrisonGame.Instance.punishments;
            Sanction kind = policy.KindFor(def);
            return kind == Sanction.Warning || (kind == Sanction.Solitary ? policy.SolitaryHoursFor(def) : policy.HoursFor(def)) > 0;
        }

        /// <summary>
        /// A sanction of this kind and length, as the policy would have given it for <paramref name="def"/> (the same effects, chronicle line and statistics). <paramref name="markLast"/>: a light sanction marks the
        /// newest unpunished record on the file (a caller that knows which record it is for marks that one itself).
        /// </summary>
        public static void Impose(Pawn p, CompInmate c, MisconductDef def, Sanction kind, int hours, bool markLast = true)
        {
            int now = Find.TickManager.TicksGame, until = now + hours * GenDate.TicksPerHour;
            switch (kind)
            {
                case Sanction.Solitary:
                    c.pendingPunishmentHours = UnityEngine.Mathf.Max(c.pendingPunishmentHours, hours);
                    return;
                case Sanction.CellConfinement:
                    if (p.ownership?.OwnedBed == null || c.pendingPunishmentHours > 0)
                    {
                        // No cell to keep them in, or the hole is due anyway (it would replace the confinement): the hole (solitary, so within the state's cap).
                        c.pendingPunishmentHours = UnityEngine.Mathf.Max(c.pendingPunishmentHours, SpecialMeasures.LimitSolitary(hours));
                        return;
                    }
                    // Already confined (in the cell, or in the hole): kept there until this runs out too.
                    if (c.forcedActivity != null) c.forcedUntilTick = UnityEngine.Mathf.Max(c.forcedUntilTick, until);
                    else
                    {
                        c.forcedActivity = RPDefOf.RP_Lockup;
                        c.forcedUntilTick = until;
                        InmateUtility.InterruptForRegimeChange(p);
                    }
                    c.suppressed = UnityEngine.Mathf.Min(100f, c.suppressed + hours * ConfinementSuppression);
                    break;
                case Sanction.LoseVisits:
                    c.visitBanUntilTick = UnityEngine.Mathf.Max(c.visitBanUntilTick, until);
                    // A visit already arranged or under way is called off.
                    if (c.visitor != null) VisitationSystem.EndVisit(c, c.visitor);
                    break;
                case Sanction.LoseYard:
                    c.yardBanUntilTick = UnityEngine.Mathf.Max(c.yardBanUntilTick, until);
                    // Out in free time now: back to the cell.
                    if (p.MapHeld != null && PrisonGame.Instance.SlotAt(c.security, GenLocalDate.HourOfDay(p.MapHeld)) == RPDefOf.RP_FreeTime)
                        InmateUtility.InterruptForRegimeChange(p);
                    break;
                case Sanction.LoseCommissary:
                    c.shopBanUntilTick = UnityEngine.Mathf.Max(c.shopBanUntilTick, until);
                    break;
            }
            if (markLast) c.MarkLastRecordPunished();
            PrisonGame.Instance.stats.lightSanctions++;
            if (kind == Sanction.Warning)
            {
                Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_Warned".Translate(p.LabelShort, def.label), p);
                return;
            }
            PrisonGame.Instance.stats.punishments++;
            c.tension = UnityEngine.Mathf.Max(0f, c.tension - LightRelief);
            p.needs?.mood?.thoughts.memories.TryGainMemory(RPSecurityDefOf.RP_Sanctioned);
            Story.Record(RPChronicleDefOf.RP_Chr_Security, 1, "RP_Chr_Sanctioned".Translate(p.LabelShort, ("RP_Sanction_" + kind).Translate(), hours, def.label), p);
        }
    }

    public static class ReputationUtility
    {
        /// <summary>0–2 reputations per inmate, weighted by commonality and security level.</summary>
        public static void Roll(Pawn p, CompInmate c)
        {
            int count = Rand.Chance(0.35f) ? 0 : Rand.Chance(0.7f) ? 1 : 2;
            var pool = new List<ReputationDef>(DefDatabase<ReputationDef>.AllDefsListForReading);
            for (int i = 0; i < count && pool.Count > 0; i++)
            {
                if (!pool.TryRandomElementByWeight(r => r.commonality * (r.typicalFor.Contains(c.security) ? 3f : r.typicalFor.Count > 0 ? 0.4f : 1f), out ReputationDef pick)) break;
                c.reputations.Add(pick);
                pool.Remove(pick);
                pool.RemoveAll(r => r.conflictsWith.Contains(pick) || pick.conflictsWith.Contains(r));
            }
        }
    }
}
