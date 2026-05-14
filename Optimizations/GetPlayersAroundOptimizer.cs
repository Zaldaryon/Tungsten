using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Tungsten.Optimizations;

/// <summary>
/// Eliminates List + ToArray allocations in ServerMain.GetPlayersAround.
/// Uses ThreadLocal list reuse (same pattern as GetEntitiesAroundOptimizer).
/// Called by AiTaskBellAlarm during temporal storms and potentially by mods.
/// Impact: low-medium (only during storms with bells), but zero implementation cost.
/// </summary>
public static class GetPlayersAroundOptimizer
{
    private const string CircuitKey = "GetPlayersAroundOptimization";
    private static ICoreServerAPI api;
    private static volatile bool disabled;
    private static int disableLogGate;

    private static readonly ThreadLocal<List<IPlayer>> reusableList = new(() => new List<IPlayer>());

    // Compiled delegates for ConnectedClient access
    private static System.Func<object, object> getClients;
    private static System.Func<object, int> getClientState;
    private static System.Func<object, EntityPlayer> getEntityPlayer;
    private static System.Func<object, IServerPlayer> getPlayer;

    public static void Initialize(ICoreServerAPI serverApi, Harmony harmony)
    {
        api = serverApi;
        ThreadLocalRegistry.Register(reusableList);

        var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
        var connectedClientType = AccessTools.TypeByName("Vintagestory.Server.ConnectedClient");

        if (serverMainType == null || connectedClientType == null)
        {
            api.Logger.Warning("[Tungsten] [GetPlayersAroundOptimization] Could not find required types");
            return;
        }

        // Compile accessors - State and Entityplayer are properties in 1.22.2+
        var clientsField = serverMainType.GetField("Clients", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var stateProp = connectedClientType.GetProperty("State", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var stateField = connectedClientType.GetField("State", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var entityPlayerProp = connectedClientType.GetProperty("Entityplayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var entityPlayerField = connectedClientType.GetField("Entityplayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var playerField = connectedClientType.GetField("Player", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (clientsField == null || (stateProp == null && stateField == null) || playerField == null)
        {
            api.Logger.Warning("[Tungsten] [GetPlayersAroundOptimization] Could not find required fields");
            return;
        }

        getClients = CompileGetter<object>(serverMainType, clientsField);
        if (stateProp != null)
            getClientState = CompilePropertyGetterInt(connectedClientType, stateProp);
        else
            getClientState = CompileGetterInt(connectedClientType, stateField);
        getPlayer = CompileGetter<IServerPlayer>(connectedClientType, playerField);

        if (entityPlayerProp != null)
            getEntityPlayer = CompilePropertyGetter<EntityPlayer>(connectedClientType, entityPlayerProp);
        else if (entityPlayerField != null)
            getEntityPlayer = CompileGetter<EntityPlayer>(connectedClientType, entityPlayerField);

        if (getClientState == null || getPlayer == null)
        {
            api.Logger.Warning("[Tungsten] [GetPlayersAroundOptimization] Failed to compile accessors");
            return;
        }

        // Patch GetPlayersAround
        var method = AccessTools.Method(serverMainType, "GetPlayersAround",
            new[] { typeof(Vec3d), typeof(float), typeof(float), typeof(ActionConsumable<IPlayer>) });
        if (method == null)
        {
            api.Logger.Warning("[Tungsten] [GetPlayersAroundOptimization] Could not find GetPlayersAround method");
            return;
        }

        harmony.Patch(method, prefix: new HarmonyMethod(typeof(GetPlayersAroundOptimizer), nameof(Prefix)));
    }

    public static bool Prefix(object __instance, Vec3d position, float horRange, float vertRange,
        ActionConsumable<IPlayer> matches, ref IPlayer[] __result)
    {
        if (disabled || !OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return true;

        try
        {
            TungstenProfiler.Mark("tungsten-getplayersaround");

            var list = reusableList.Value;
            list.Clear();

            float horRangeSq = horRange * horRange;

            // Access Clients dictionary via compiled delegate
            var clientsDict = getClients(__instance) as IDictionary;
            if (clientsDict == null) return true;

            foreach (DictionaryEntry entry in clientsDict)
            {
                var client = entry.Value;
                int state = getClientState(client);
                if (state != 3) continue; // EnumClientState.Playing = 3

                var entityPlayer = getEntityPlayer?.Invoke(client);
                if (entityPlayer == null) continue;

                // EntityPlayer.Pos is EntityPos which has InRangeOf(Vec3d, float, float)
                if (!entityPlayer.Pos.InRangeOf(position, horRangeSq, vertRange))
                    continue;

                var player = getPlayer(client);
                if (player == null) continue;

                if (matches == null || matches(player))
                    list.Add(player);
            }

            __result = list.ToArray();
            list.Clear();
            return false;
        }
        catch (Exception)
        {
            Disable("runtime exception");
            return true;
        }
    }

    #region Compiled Delegate Helpers

    private static System.Func<object, T> CompileGetter<T>(Type declaringType, FieldInfo field)
    {
        var param = Expression.Parameter(typeof(object));
        var access = Expression.Field(Expression.Convert(param, declaringType), field);
        var convert = Expression.Convert(access, typeof(T));
        return Expression.Lambda<System.Func<object, T>>(convert, param).Compile();
    }

    private static System.Func<object, int> CompileGetterInt(Type declaringType, FieldInfo field)
    {
        var param = Expression.Parameter(typeof(object));
        var access = Expression.Field(Expression.Convert(param, declaringType), field);
        var convert = Expression.Convert(access, typeof(int));
        return Expression.Lambda<System.Func<object, int>>(convert, param).Compile();
    }

    private static System.Func<object, T> CompilePropertyGetter<T>(Type declaringType, PropertyInfo prop)
    {
        var param = Expression.Parameter(typeof(object));
        var access = Expression.Property(Expression.Convert(param, declaringType), prop);
        var convert = Expression.Convert(access, typeof(T));
        return Expression.Lambda<System.Func<object, T>>(convert, param).Compile();
    }

    private static System.Func<object, int> CompilePropertyGetterInt(Type declaringType, PropertyInfo prop)
    {
        var param = Expression.Parameter(typeof(object));
        var access = Expression.Property(Expression.Convert(param, declaringType), prop);
        var convert = Expression.Convert(access, typeof(int));
        return Expression.Lambda<System.Func<object, int>>(convert, param).Compile();
    }

    #endregion

    private static void Disable(string reason)
    {
        disabled = true;
        OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);
        if (Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
            TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] [GetPlayersAroundOptimization] Disabled: {reason}");
    }

    public static void Dispose()
    {
        disabled = false;
        disableLogGate = 0;
        api = null;
    }
}
