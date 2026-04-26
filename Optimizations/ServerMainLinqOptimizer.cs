using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Tungsten.Optimizations;

/// <summary>
/// Eliminates LINQ allocations in ServerMain property getters using compiled delegates.
/// Targets: AllOnlinePlayers and AllPlayers (frequently accessed for broadcasts/queries).
/// Impact: Eliminates intermediate enumerator allocations and reduces array allocations.
/// v1.9.3: Fixed reflection overhead - now uses compiled expression trees for near-native performance.
/// v1.0.5: Fixed return type to IPlayer[] to match actual method signature.
/// </summary>
public static class ServerMainLinqOptimizer
{
    private const string CircuitKey = "ServerMainLinqOptimization";
    private static ICoreServerAPI api;
    
    // Compiled delegates for fast property access (no reflection overhead)
    private static System.Func<object, object> getClientsFunc;
    private static System.Func<object, object> getPlayersByUidFunc;
    private static System.Func<object, object> getPlayerFunc;

    public static void Initialize(ICoreServerAPI serverApi, Harmony harmony)
    {
        api = serverApi;

        var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
        if (serverMainType == null)
        {
            api.Logger.Debug("[Tungsten] ServerMainLinqOptimizer: Could not find ServerMain");
            return;
        }

        // Compile property accessors as expression trees for fast access
        var clientsField = serverMainType.GetField("Clients", BindingFlags.Public | BindingFlags.Instance);
        var playersByUidField = serverMainType.GetField("PlayersByUid", BindingFlags.Public | BindingFlags.Instance);
        var connectedClientType = AccessTools.TypeByName("Vintagestory.Server.ConnectedClient");
        var playerField = connectedClientType?.GetField("Player");

        if (clientsField == null || playersByUidField == null || playerField == null)
        {
            api.Logger.Debug($"[Tungsten] ServerMainLinqOptimizer: Missing - Clients:{clientsField == null}, PlayersByUid:{playersByUidField == null}, Player:{playerField == null}");
            return;
        }

        // Compile Clients field accessor
        getClientsFunc = CompileFieldGetter(clientsField);
        
        // Compile PlayersByUid field accessor
        getPlayersByUidFunc = CompileFieldGetter(playersByUidField);
        
        // Compile Player property accessor
        getPlayerFunc = CompileFieldGetter(playerField);

        if (getClientsFunc == null || getPlayersByUidFunc == null || getPlayerFunc == null)
        {
            api.Logger.Debug("[Tungsten] ServerMainLinqOptimizer: Failed to compile accessors");
            return;
        }

        // Patch AllOnlinePlayers property getter
        var allOnlinePlayersProperty = AccessTools.Property(serverMainType, "AllOnlinePlayers");
        if (allOnlinePlayersProperty != null)
        {
            var getter = allOnlinePlayersProperty.GetGetMethod();
            if (getter != null)
            {
                harmony.Patch(getter,
                    prefix: new HarmonyMethod(typeof(ServerMainLinqOptimizer), nameof(AllOnlinePlayers_Prefix)));            }
        }

        // Patch AllPlayers property getter
        var allPlayersProperty = AccessTools.Property(serverMainType, "AllPlayers");
        if (allPlayersProperty != null)
        {
            var getter = allPlayersProperty.GetGetMethod();
            if (getter != null)
            {
                harmony.Patch(getter,
                    prefix: new HarmonyMethod(typeof(ServerMainLinqOptimizer), nameof(AllPlayers_Prefix)));            }
        }
    }

    private static System.Func<object, object> CompileFieldGetter(FieldInfo field)
    {
        try
        {
            var param = Expression.Parameter(typeof(object), "instance");
            var cast = Expression.Convert(param, field.DeclaringType);
            var fieldAccess = Expression.Field(cast, field);
            var boxed = Expression.Convert(fieldAccess, typeof(object));
            return Expression.Lambda<System.Func<object, object>>(boxed, param).Compile();
        }
        catch
        {
            return null;
        }
    }


    public static bool AllOnlinePlayers_Prefix(object __instance, ref IPlayer[] __result)
    {
        if (!OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return true;

        try
        {
            TungstenProfiler.Mark("tungsten-allonlineplayers");
            var clients = getClientsFunc(__instance) as System.Collections.IDictionary;
            if (clients == null) return true;

            var players = new List<IServerPlayer>(clients.Count);
            foreach (System.Collections.DictionaryEntry entry in clients)
            {
                if (getPlayerFunc(entry.Value) is IServerPlayer sp)
                    players.Add(sp);
            }

            __result = players.ToArray();
            return false;
        }
        catch (Exception ex)
        {
            OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, ex.GetType().Name + ": " + ex.Message);
            return true;
        }
    }

    public static bool AllPlayers_Prefix(object __instance, ref IPlayer[] __result)
    {
        if (!OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return true;

        try
        {
            TungstenProfiler.Mark("tungsten-allplayers");
            var players = getPlayersByUidFunc(__instance) as System.Collections.IDictionary;
            if (players == null) return true;

            var result = new IPlayer[players.Count];
            int index = 0;
            foreach (System.Collections.DictionaryEntry entry in players)
            {
                result[index++] = entry.Value as IPlayer;
            }

            __result = result;
            return false;
        }
        catch (Exception ex)
        {
            OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, ex.GetType().Name + ": " + ex.Message);
            return true;
        }
    }

    public static void Dispose()
    {
        getClientsFunc = null;
        getPlayersByUidFunc = null;
        getPlayerFunc = null;
        api = null;
    }
}
