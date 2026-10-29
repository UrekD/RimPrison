using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using RimWorld.Planet;

namespace RimPrison
{
    public static class EscapeUtility
    {
        public const int EscapeFine = 2000;

        /// <summary>A map-edge cell reachable when doors count as passable (they get bashed); walls stop escapes.</summary>
        public static bool TryFindEscapeRoute(Pawn p, out IntVec3 exit) =>
            RCellFinder.TryFindBestExitSpot(p, out exit, TraverseMode.PassDoors, canBash: true);

        /// <summary>True when the inmate can walk off the map right now without opening a door (perimeter breach).</summary>
        public static bool OpenRouteToEdge(Pawn p) => OpenRouteToEdge(p, out _);

        /// <summary>As above; <paramref name="onlyMomentary"/> when every route needs a door that is open only because
        /// someone is standing in the doorway, not held open, jammed by an item or corpse, or broken.</summary>
        public static bool OpenRouteToEdge(Pawn p, out bool onlyMomentary)
        {
            onlyMomentary = false;
            District district = p.GetDistrict();
            if (district == null) return false;
            if (district.TouchesMapEdge) return true;
            if (Traverse(district, d => d.FreePassage && !OpenOnlyForAPawn(d))) return true;
            if (!Traverse(district, d => d.FreePassage)) return false;
            onlyMomentary = true;
            return true;
        }

        /// <summary>
        /// The door stays open only because a pawn stands in the doorway (vanilla keeps doors open for anyone in them,
        /// so <see cref="Building_Door.FreePassage"/> is true while e.g. a visitor lingers there): a split-second
        /// chance. Items, corpses, a missing wall or a held-open door are a real breach.
        /// </summary>
        public static bool OpenOnlyForAPawn(Building_Door door)
        {
            if (door.HoldOpen || StuckOpen(door)) return false;
            bool pawn = false;
            foreach (IntVec3 c in door.OccupiedRect())
            {
                List<Thing> things = c.GetThingList(door.Map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    // Someone lying in the doorway keeps it open as surely as a crate.
                    if (t is Pawn p) { if (p.Downed) return false; pawn = true; }
                    else if (t.def.category == ThingCategory.Item || t is Corpse) return false;
                }
            }
            return pawn;
        }

        /// <summary>Vanilla's protected StuckOpen: always-open doors, and doors missing their wall.</summary>
        private static readonly System.Func<Building_Door, bool> StuckOpen =
            AccessTools.MethodDelegate<System.Func<Building_Door, bool>>(AccessTools.PropertyGetter(typeof(Building_Door), "StuckOpen"));

        private static bool Traverse(District district, System.Predicate<Building_Door> passable)
        {
            bool found = false;
            RegionTraverser.BreadthFirstTraverse(district.Regions[0], (from, reg) => reg.door == null || passable(reg.door), reg =>
            {
                if (reg.District.TouchesMapEdge) found = true;
                return found;
            }, 40);
            return found;
        }

        /// <summary>Opportunistic escape through a breach: checked once an hour per inmate.</summary>
        public static void HourlyCheck(Pawn p, CompInmate c)
        {
            // New arrivals (and returned fugitives) waiting at the intake spot are not escaping.
            if (p.InMentalState || p.Downed || !p.Awake() || !c.processed || c.awaitingSearch || !OpenRouteToEdge(p, out bool momentary)) return;
            // Already outside the walls (chased someone out in a rage, wandered through a breach): they run.
            if (p.GetDistrict()?.TouchesMapEdge == true)
            {
                CountSystem.StartEscaping(p, "RP_BreachSpotted".Translate(), EscapeWay.Walked);
                return;
            }
            float chance = 0.15f + c.tension / 200f;
            // A door someone is standing in is a split-second chance, not an open gate.
            if (momentary) chance *= 0.15f;
            if (c.HasReputation(ReputationDefOf.RP_EscapeArtist)) chance *= 2f;
            if (c.HasReputation(ReputationDefOf.RP_ModelPrisoner)) chance *= 0.25f;
            if (Rand.Chance(chance))
                CountSystem.StartEscaping(p, "RP_BreachSpotted".Translate(), EscapeWay.Walked);
        }

        /// <summary>
        /// The escape is known: the books (the count, the day's report), the fine, the police and the chronicle, and the letter. At once when somebody saw it, else when a count (or a sighting) tells the warden
        /// (see <see cref="CountSystem"/>): <paramref name="escapedTick"/> is when they got away, so the police's head start is what the wait left them. The letter is the count's own when <paramref name="letter"/>
        /// is false, and so is the chronicle line when <paramref name="line"/> is.
        /// </summary>
        public static void Register(Pawn p, PrisonGame game, bool stowaway, int escapedTick, Map map, IntVec3 cell, bool letter = true, bool line = true)
        {
            game.stats.escaped++;
            game.today.escapes++;
            CustodyContracts.Notify_Departed(p, "RP_CustodyEnd_Escaped");
            if (AutoTest.Active) RPLog.Message($"[escape] {p.LabelShort} left the map at {cell} mental={p.MentalStateDef?.defName}");
            game.ledger.Add(RPDefOf.RP_Fines, -EscapeFine, "RP_EscapeFineNote".Translate(p.LabelShort));
            FugitiveSystem.Notify_Escaped(p, game, escapedTick);
            // (A stowaway's escape has its line already, with how they did it: one line for the one event.)
            if (!stowaway && line) Story.Record(RPChronicleDefOf.RP_Chr_Security, 2, "RP_Chr_Escaped".Translate(p.LabelShort), p);
            if (stowaway) Story.MarkFirst("escape");
            else Story.RecordFirst("escape", RPChronicleDefOf.RP_Chr_Security, "RP_Chr_FirstEscape".Translate(p.LabelShort), p);
            if (letter)
                Find.LetterStack.ReceiveLetter("RP_EscapedLabel".Translate(), "RP_EscapedText".Translate(p.LabelShort, Money.Format(EscapeFine)), LetterDefOf.NegativeEvent, new LookTargets(cell, map));
        }
    }

    /// <summary>An inmate leaving the map without being released has escaped: fine, statistics, letter.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
    public static class Patch_Pawn_ExitMap_Escape
    {
        public static bool Prefix(Pawn __instance)
        {
            // Boarding the state shuttle (or any exit of someone on their way out) is the release leaving: it counts now. A stowaway who gets aboard has escaped.
            bool transfer = false, stowaway = false;
            if (PrisonGame.Instance != null)
            {
                transfer = __instance.Spawned && ShuttleSystem.Sys?.OutboundOf(__instance) is { kind: OutboundKind.Transferred };
                ShuttleSystem.Notify_Left(__instance);
                stowaway = ShuttleSystem.Notify_StowawayAboard(__instance);
            }
            if (__instance.Map != null && __instance.IsPrisonerOfColony)
                foreach (Pawn o in __instance.Map.mapPawns.PrisonersOfColonySpawned)
                    if (o.Inmate() is { } oc && oc.informedBy == __instance) oc.informedBy = null;
            if (transfer)
            {
                // A transfer that walks out (there is no shuttle to take it) is sent to another facility all the same, not set free: no freed-prisoner goodwill, no world pawn to keep.
                __instance.DeSpawn();
                Find.WorldPawns.PassToWorld(__instance, RimWorld.Planet.PawnDiscardDecideMode.Discard);
                return false;
            }
            if (!__instance.IsPrisonerOfColony || __instance.guest.Released || PrisonGame.Instance == null)
            {
                if (stowaway) ShuttleSystem.AnnounceStowawayAway(__instance);
                return true;
            }
            // Not an escape: already off the map, leaving with a caravan, or in a transport pod.
            if (!__instance.Spawned || __instance.IsCaravanMember() || ThingOwnerUtility.AnyParentIs<ActiveTransporterInfo>(__instance))
            {
                if (stowaway) ShuttleSystem.AnnounceStowawayAway(__instance);
                return true;
            }
            PrisonGame game = PrisonGame.Instance;
            // (A custody contract's bonus for them is forfeit as they go, seen or not: the books' own count may come much later.)
            CustodyContracts.Notify_Departed(__instance, "RP_CustodyEnd_Escaped");
            // Nobody saw them go (and they were not seen on the way): the books wait for the count, which finds the cell empty. Told at once otherwise.
            if (!CountSystem.Notify_Exiting(__instance, game, stowaway))
            {
                if (stowaway) ShuttleSystem.AnnounceStowawayAway(__instance);
                EscapeUtility.Register(__instance, game, stowaway, Find.TickManager.TicksGame, __instance.MapHeld, __instance.PositionHeld);
            }
            game.story.Notify_Leaving(__instance);
            game.story.dramaToday += 2;
            return true;
        }
    }
}

namespace RimPrison
{
    /// <summary>Deaths in custody: statistics and the safety grade.</summary>
    [HarmonyLib.HarmonyPatch(typeof(Verse.Pawn), nameof(Verse.Pawn.Kill))]
    public static class Patch_Pawn_Kill_Custody
    {
        public static void Prefix(Verse.Pawn __instance, Verse.DamageInfo? dinfo, Verse.Hediff exactCulprit)
        {
            if (PrisonGame.Instance == null || __instance.Dead) return;
            // Staff die too: their own aftermath.
            if (__instance.Faction == Faction.OfPlayer && !__instance.IsPrisonerOfColony && __instance.GetComp<CompStaff>() is { role: not null } sc)
            {
                StaffDeaths.Notify_Died(__instance, sc, dinfo, exactCulprit);
                return;
            }
            // A visitor: one of an inmate's family, dead in the prison.
            if (VisitorDeaths.Notify_Died(__instance)) return;
            if (!__instance.IsPrisonerOfColony) return;
            CustodyContracts.Notify_Departed(__instance, "RP_CustodyEnd_Died");
            if (__instance.Inmate() is { executing: true }) return; // counted as an execution, not a death in custody
            // Released, paroled or transferred and still waiting for the shuttle: no longer in custody. The prison has let them go, so the death is not counted against it
            // (no custody death in the books or the safety grade, no mourning in the wing, no state inquiry or fine); the chronicle says it happened.
            if (ShuttleSystem.OnTheWayOut(__instance))
            {
                Story.Record(RPChronicleDefOf.RP_Chr_Death, 1, "RP_Chr_DiedWaiting".Translate(__instance.LabelShort), __instance);
                return;
            }
            if (AutoTest.Active)
                RPLog.Message($"[RimPrison:death] {__instance.LabelShort} died: dinfo={dinfo?.Def?.defName} by {dinfo?.Instigator?.LabelShort} ({dinfo?.Instigator?.Faction?.Name}) culprit={exactCulprit?.def?.defName} hediffs={string.Join(",", __instance.health.hediffSet.hediffs.ConvertAll(h => h.def.defName))}");
            PrisonGame.Instance.stats.deaths++;
            PrisonGame.Instance.story.lastDeathTick = Find.TickManager.TicksGame;
            PrisonGame.Instance.today.deaths++;
            Pawn killer = dinfo?.Instigator as Pawn;
            TaggedString how = killer != null && killer != __instance ? "RP_Chr_DiedKilled".Translate(__instance.LabelShort, killer.LabelShort)
                : "RP_Chr_Died".Translate(__instance.LabelShort, (exactCulprit?.def?.label ?? dinfo?.Def?.label ?? "RP_Chr_UnknownCause".Translate().ToString()));
            Story.Record(RPChronicleDefOf.RP_Chr_Death, 2, how, __instance, killer);
            // (The warden's orders end with them: the funeral keeps who was kept apart from the dead one.)
            Funerals.Notify_Dying(__instance);
            PrisonGame.Instance.story.Notify_Leaving(__instance);
            PrisonGame.Instance.story.dramaToday += 2;
            DeathAftermath.Notify_Died(__instance, killer, exactCulprit, dinfo);
            Story.RecordFirst("death", RPChronicleDefOf.RP_Chr_Death, "RP_Chr_FirstDeath".Translate(__instance.LabelShort), __instance);
            if (dinfo?.Instigator is Pawn shooter && shooter.Faction == Faction.OfPlayer && ArmedResponse.IsArmed(shooter) && shooter.GetComp<CompStaff>()?.role != null)
            {
                PrisonGame.Instance.stats.shotByGuards++;
                StaffMemories.Notify_ShotDead(__instance, shooter);
            }
            else if (dinfo.HasValue || exactCulprit?.def == HediffDefOf.BloodLoss)
            {
                PrisonGame.Instance.stats.violentDeaths++;
                if (AutoTest.Active && __instance.MapHeld is { } deathMap && PrisonMap.Of(deathMap) is { } deathPm)
                    AutoTest.ViolentDeathNotes.Add($"{__instance.LabelShort} (#{__instance.Inmate()?.inmateNumber}, {__instance.MentalStateDef?.defName ?? "calm"}, tension {__instance.Inmate()?.tension:F0}, {(__instance.ownership?.OwnedBed != null ? "has a bed" : "no bed")}, "
                                                   + $"{(__instance.GetRoom() == InmateUtility.CellOf(__instance) ? "in their own cell" : "elsewhere, watched by " + TensionUtility.Watchers(__instance, deathPm))}) killed by "
                                                   + $"{dinfo?.Instigator?.LabelShort ?? exactCulprit?.def?.defName ?? "bleeding"} with {dinfo?.Def?.defName ?? "no blow"} on day {Ledger.CurrentDay}, "
                                                   + $"downed {__instance.Downed}, in a bed {__instance.InBed()}, staff on site [{string.Join(",", deathMap.mapPawns.FreeColonistsSpawned.Where(s => s.GetComp<CompStaff>()?.role != null).Select(s => $"{s.LabelShort}:{s.CurJobDef?.defName}"))}]");
            }
        }
    }
}
