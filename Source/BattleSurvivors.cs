using System;
using System.Collections;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace BattleSurvivors
{
    public class BattleSurvivorsMod : Mod
    {
        public BattleSurvivorsMod(ModContentPack content) : base(content)
        {
            Log.Warning("[BattleSurvivors] Assembly loaded (mod class constructed).");
            LongEventHandler.ExecuteWhenFinished(() => Startup.Init());
        }
    }

    [StaticConstructorOnStartup]
    public static class Startup
    {
        const string ModTag = "[BattleSurvivors] ";
        const string DdNamespace = "DynamicDiplomacy";
        const string EpNamespace = "FinitePopulationVeterans";

        [ThreadStatic] static bool rescuing;

        static readonly ConditionalWeakTable<Map, HashSet<Faction>> standingByMap = new ConditionalWeakTable<Map, HashSet<Faction>>();

        static Type ddArenaType;
        static Type epManagerType;
        static MethodInfo epAddVeteran;
        static FieldInfo epPinsField;
        static MethodInfo epPinsContains;
        static FieldInfo epVeteransField;
        static MethodInfo epVeteransContains;

        static FieldInfo arenaAttacker;
        static FieldInfo arenaDefender;
        static MethodInfo setFaction;
        static MethodInfo killMethod;
        static MethodInfo allSpawnedGetter;

        static MethodInfo getLord;
        static MethodInfo lordNotifyLost;
        static object lostVanished;

        static int started;

        static Startup()
        {
            Init();
        }

        public static void Init()
        {
            if (Interlocked.Exchange(ref started, 1) == 1)
                return;
            try
            {
                Log.Warning(ModTag + "Init running.");
                Apply();
            }
            catch (Exception e)
            {
                Log.Error(ModTag + "Failed to initialise: " + e);
            }
        }

        static Type FindType(string ns, string name)
        {
            return AccessTools.AllTypes().FirstOrDefault(t => t.Name == name && t.Namespace == ns);
        }

        static void Apply()
        {
            ddArenaType = FindType(DdNamespace, "MapParentNPCArena");
            epManagerType = FindType(EpNamespace, "WorldPopulationManager");
            if (ddArenaType == null || epManagerType == null)
            {
                Log.Warning(ModTag + "Dynamic Diplomacy or Eternal Pawns not found, nothing to patch.");
                return;
            }

            epAddVeteran = epManagerType.GetMethod("AddVeteran", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Pawn) }, null);
            epPinsField = epManagerType.GetField("manualVeteranPins", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            epVeteransField = epManagerType.GetField("allVeteranIdsCache", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (epPinsField != null)
                epPinsContains = epPinsField.FieldType.GetMethod("Contains", new[] { typeof(int) });
            if (epVeteransField != null)
                epVeteransContains = epVeteransField.FieldType.GetMethod("Contains", new[] { typeof(int) });
            if (epAddVeteran == null || epPinsField == null || epPinsContains == null
                || epVeteransField == null || epVeteransContains == null)
            {
                Log.Warning(ModTag + "Eternal Pawns API changed (AddVeteran, manualVeteranPins or allVeteranIdsCache missing), mod disabled.");
                return;
            }

            MethodInfo destroy = AccessTools.DeclaredMethod(typeof(Pawn), "Destroy", new[] { typeof(DestroyMode) });
            if (destroy == null)
            {
                Log.Warning(ModTag + "Pawn.Destroy not found, mod disabled.");
                return;
            }

            arenaAttacker = ddArenaType.GetField("attackerFaction", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            arenaDefender = ddArenaType.GetField("defenderFaction", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            setFaction = typeof(Pawn).GetMethod("SetFaction", new[] { typeof(Faction), typeof(Pawn) });
            killMethod = typeof(Pawn).GetMethods().FirstOrDefault(m => m.Name == "Kill" && m.GetParameters().Length == 2);
            PropertyInfo spawnedProp = typeof(MapPawns).GetProperty("AllPawnsSpawned", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            allSpawnedGetter = spawnedProp != null ? spawnedProp.GetGetMethod(true) : null;

            Type lordUtility = AccessTools.TypeByName("Verse.AI.Group.LordUtility");
            Type lordType = AccessTools.TypeByName("Verse.AI.Group.Lord");
            Type lostType = AccessTools.TypeByName("Verse.AI.Group.PawnLostCondition");
            if (lordUtility != null)
                getLord = lordUtility.GetMethod("GetLord", new[] { typeof(Pawn) });
            if (lordType != null)
                lordNotifyLost = lordType.GetMethod("Notify_PawnLost");
            if (lostType != null && Enum.IsDefined(lostType, "Vanished"))
                lostVanished = Enum.Parse(lostType, "Vanished");
            if (getLord == null || lordNotifyLost == null || lostVanished == null)
                Log.Warning(ModTag + "Lord API not found, rescued pawns will stay listed in their lord.");

            if (arenaAttacker == null || arenaDefender == null || setFaction == null || killMethod == null || allSpawnedGetter == null)
                Log.Warning(ModTag + "Captive fate disabled (arena factions, SetFaction or Kill not found), survivors are still saved.");

            var harmony = new Harmony("ghost.battlesurvivors");
            harmony.Patch(destroy, prefix: new HarmonyMethod(typeof(Startup).GetMethod(nameof(SaveBeforeDestroy), BindingFlags.Static | BindingFlags.NonPublic)));
            Log.Warning(ModTag + "Active: veterans and pinned pawns are saved before Dynamic Diplomacy destroys them.");
        }

        static bool InSet(FieldInfo field, MethodInfo contains, object manager, int id)
        {
            object set = field.GetValue(manager);
            return set != null && (bool)contains.Invoke(set, new object[] { id });
        }

        static void ReleaseFromLord(Pawn pawn)
        {
            if (getLord == null || lordNotifyLost == null || lostVanished == null)
                return;
            try
            {
                object lord = getLord.Invoke(null, new object[] { pawn });
                if (lord == null)
                    return;
                ParameterInfo[] ps = lordNotifyLost.GetParameters();
                object[] args = new object[ps.Length];
                args[0] = pawn;
                args[1] = lostVanished;
                lordNotifyLost.Invoke(lord, args);
            }
            catch (Exception e)
            {
                Log.Warning(ModTag + "Could not release " + pawn.LabelShort + " from its lord: " + e.Message);
            }
        }

        static HashSet<Faction> StandingFactions(Map map)
        {
            HashSet<Faction> set;
            if (standingByMap.TryGetValue(map, out set))
                return set;

            set = new HashSet<Faction>();
            IEnumerable pawns = allSpawnedGetter != null ? allSpawnedGetter.Invoke(map.mapPawns, null) as IEnumerable : null;
            if (pawns != null)
            {
                foreach (object o in pawns)
                {
                    Pawn p = o as Pawn;
                    if (p != null && p.Faction != null && !p.Dead && !p.Downed)
                        set.Add(p.Faction);
                }
            }
            standingByMap.Add(map, set);
            Log.Warning(ModTag + "Cleanup started, factions with a standing pawn: "
                + (set.Count == 0 ? "none" : string.Join(", ", set.Select(f => f.Name).ToArray())) + ".");
            return set;
        }

        static Faction WinnerOverCaptive(Pawn pawn, Map map, object arena)
        {
            if (arenaAttacker == null || arenaDefender == null)
                return null;
            Faction a = arenaAttacker.GetValue(arena) as Faction;
            Faction d = arenaDefender.GetValue(arena) as Faction;
            Faction own = pawn.Faction;
            if (a == null || d == null || own == null || (own != a && own != d))
                return null;
            Faction other = own == a ? d : a;
            return StandingFactions(map).Contains(other) ? other : null;
        }

        static bool SaveBeforeDestroy(Pawn __instance, DestroyMode mode)
        {
            if (rescuing || mode != DestroyMode.Vanish)
                return true;

            Pawn pawn = __instance;
            if (pawn == null)
                return true;

            if (pawn.Dead || !pawn.Spawned)
                return true;

            Map map = pawn.Map;
            if (map == null || map.Parent == null || !ddArenaType.IsInstanceOfType(map.Parent))
                return true;

            rescuing = true;
            try
            {
                StandingFactions(map);

                object manager = Find.World.GetComponent(epManagerType);
                if (manager == null)
                    return true;

                int id = pawn.thingIDNumber;
                bool keep = InSet(epVeteransField, epVeteransContains, manager, id)
                    || InSet(epPinsField, epPinsContains, manager, id);
                Log.Warning(ModTag + "Cleanup of " + pawn.LabelShort + ": faction=" + (pawn.Faction != null ? pawn.Faction.Name : "none")
                    + ", downed=" + pawn.Downed + ", pinned=" + InSet(epPinsField, epPinsContains, manager, id)
                    + ", veteran=" + InSet(epVeteransField, epVeteransContains, manager, id) + ", keep=" + keep + ".");
                if (!keep)
                    return true;

                if (pawn.Downed && setFaction != null && killMethod != null)
                {
                    Faction winner = WinnerOverCaptive(pawn, map, map.Parent);
                    if (winner == null)
                        Log.Warning(ModTag + pawn.LabelShort + " is downed but no winning faction was found, saving it as is.");
                    if (winner != null)
                    {
                        if (Rand.Value < 0.5f)
                        {
                            Log.Warning(ModTag + pawn.LabelShort + " was left downed and is killed by the winners.");
                            killMethod.Invoke(pawn, new object[] { null, null });
                            return false;
                        }
                        Log.Warning(ModTag + pawn.LabelShort + " was left downed and is recruited by " + winner.Name + ".");
                        setFaction.Invoke(pawn, new object[] { winner, null });
                    }
                }

                ReleaseFromLord(pawn);
                pawn.DeSpawn(DestroyMode.Vanish);
                epAddVeteran.Invoke(manager, new object[] { pawn });

                if (Find.WorldPawns.Contains(pawn))
                {
                    Log.Warning(ModTag + "Saved " + pawn.LabelShort + " to world pawns before the battle cleanup (downed=" + pawn.Downed + ").");
                    return false;
                }

                Log.Warning(ModTag + "Eternal Pawns refused " + pawn.LabelShort + ", letting the destroy proceed.");
                return true;
            }
            catch (Exception e)
            {
                Log.Error(ModTag + "Rescue failed for " + pawn.LabelShort + ": " + e);
                return true;
            }
            finally
            {
                rescuing = false;
            }
        }
    }
}
