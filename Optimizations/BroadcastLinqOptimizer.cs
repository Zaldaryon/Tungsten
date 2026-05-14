using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Tungsten.Optimizations;

/// <summary>
/// Eliminates LINQ Any()/All() closure allocations in ServerMain.BroadcastArbitraryPacket
/// and BroadcastArbitraryUdpPacket. Vanilla allocates a lambda closure capturing client.Id
/// on every iteration of the client loop, plus an enumerator for skipPlayers array.
/// Replacement: simple for-loop over the params array. Zero allocations.
/// Impact: ~32 call sites, multiple broadcasts per tick on multiplayer servers.
/// </summary>
public static class BroadcastLinqOptimizer
{
    private const string CircuitKey = "BroadcastLinqOptimization";
    private static ICoreServerAPI api;
    private static volatile bool disabled;
    private static int disableLogGate;

    // Compiled delegates for zero-reflection hot path
    private static System.Func<object, object> getClients;
    private static System.Func<object, int> getClientState;
    private static System.Func<object, int> getClientId;
    private static System.Func<object, IServerPlayer> getClientPlayer;
    private static System.Func<object, object> getClientSocket;
    private static System.Func<object, byte[]> getReusableBuffer;
    private static System.Func<object, bool> getDoNetBenchmark;
    private static System.Func<object, object> getConfig;
    private static System.Func<object, bool> getCompressPackets;
    private static System.Action<object, object> callSerialize;
    private static Type dummyNetConnectionType;

    // SendPacket and SendPreparedPacket delegates
    private static System.Action<object, IServerPlayer, byte[]> callSendPacketPlayer;
    private static System.Action<object, object, byte[], bool> callSendPreparedPacket;
    private static System.Action<object, object, object> callSendPacketUdp;

    public static void Initialize(ICoreServerAPI serverApi, Harmony harmony)
    {
        api = serverApi;

        var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");
        var connectedClientType = AccessTools.TypeByName("Vintagestory.Server.ConnectedClient");
        var packetServerType = AccessTools.TypeByName("Vintagestory.Server.Packet_Server")
            ?? AccessTools.TypeByName("Packet_Server");
        var udpPacketType = AccessTools.TypeByName("Vintagestory.Server.Packet_UdpPacket")
            ?? AccessTools.TypeByName("Packet_UdpPacket");
        dummyNetConnectionType = AccessTools.TypeByName("Vintagestory.Server.DummyNetConnection")
            ?? AccessTools.TypeByName("Vintagestory.Common.DummyNetConnection");

        if (serverMainType == null || connectedClientType == null)
        {
            api.Logger.Warning("[Tungsten] [BroadcastLinqOptimization] Could not find required types");
            return;
        }

        // Compile all accessors
        if (!CompileAccessors(serverMainType, connectedClientType))
        {
            api.Logger.Warning("[Tungsten] [BroadcastLinqOptimization] Failed to compile accessors");
            return;
        }

        // Patch byte[] overload
        var byteMethod = AccessTools.Method(serverMainType, "BroadcastArbitraryPacket",
            new[] { typeof(byte[]), typeof(IServerPlayer[]) });
        if (byteMethod != null)
            harmony.Patch(byteMethod, prefix: new HarmonyMethod(typeof(BroadcastLinqOptimizer), nameof(BroadcastBytes_Prefix)));

        // Packet_Server overload: skipped in 1.22.2+ (reusableBuffer changed to static BoxedPacket,
        // PreparePacketForSending signature changed). The byte[] and UDP overloads still provide value.

        // Patch UDP overload
        if (udpPacketType != null)
        {
            var udpMethod = AccessTools.Method(serverMainType, "BroadcastArbitraryUdpPacket",
                new[] { udpPacketType, typeof(IServerPlayer[]) });
            if (udpMethod != null)
                harmony.Patch(udpMethod, prefix: new HarmonyMethod(typeof(BroadcastLinqOptimizer), nameof(BroadcastUdp_Prefix)));
        }
    }

    private static bool CompileAccessors(Type serverMainType, Type connectedClientType)
    {
        try
        {
            // ServerMain.Clients (Dictionary<int, ConnectedClient>)
            var clientsField = serverMainType.GetField("Clients", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (clientsField == null) return false;
            getClients = CompileGetter<object>(serverMainType, clientsField);

            // ConnectedClient.State (EnumClientState) - property in 1.22.2, field in earlier
            var stateProp = connectedClientType.GetProperty("State", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var stateField = connectedClientType.GetField("State", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (stateProp != null)
                getClientState = CompilePropertyGetterInt(connectedClientType, stateProp);
            else if (stateField != null)
                getClientState = CompileGetterInt(connectedClientType, stateField);
            else
                return false;

            // ConnectedClient.Id
            var idField = connectedClientType.GetField("Id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var idProp = AccessTools.Property(connectedClientType, "Id");
            if (idField != null)
                getClientId = CompileGetterInt(connectedClientType, idField);
            else if (idProp != null)
                getClientId = CompilePropertyGetterInt(connectedClientType, idProp);
            else
                return false;

            // ConnectedClient.Player (IServerPlayer)
            var playerField = connectedClientType.GetField("Player", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (playerField == null) return false;
            getClientPlayer = CompileGetter<IServerPlayer>(connectedClientType, playerField);

            // ConnectedClient.Socket (or socket in 1.22.2)
            var socketField = connectedClientType.GetField("Socket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? connectedClientType.GetField("socket", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (socketField != null)
                getClientSocket = CompileGetter<object>(connectedClientType, socketField);

            // ServerMain.reusableBuffer
            var bufferField = serverMainType.GetField("reusableBuffer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (bufferField != null)
                getReusableBuffer = CompileGetter<byte[]>(serverMainType, bufferField);

            // ServerMain.doNetBenchmark
            var benchField = serverMainType.GetField("doNetBenchmark", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (benchField != null)
                getDoNetBenchmark = CompileGetter<bool>(serverMainType, benchField);

            // ServerMain.Config
            var configProp = AccessTools.Property(serverMainType, "Config");
            if (configProp != null)
            {
                getConfig = CompilePropertyGetter<object>(serverMainType, configProp);
                var compressProp = AccessTools.Property(configProp.PropertyType, "CompressPackets");
                if (compressProp != null)
                    getCompressPackets = CompilePropertyGetter<bool>(configProp.PropertyType, compressProp);
            }

            // ServerMain.Serialize_(Packet_Server)
            var serializeMethodInfo = AccessTools.Method(serverMainType, "Serialize_");
            if (serializeMethodInfo != null)
                callSerialize = CompileAction(serverMainType, serializeMethodInfo);

            // ServerMain.SendPacket(IServerPlayer, byte[])
            var sendPacketMethod = AccessTools.Method(serverMainType, "SendPacket",
                new[] { typeof(IServerPlayer), typeof(byte[]) });
            if (sendPacketMethod != null)
                callSendPacketPlayer = CompileSendPacketPlayer(serverMainType, sendPacketMethod);

            // ServerMain.SendPreparedPacket(ConnectedClient, byte[], bool)
            var sendPreparedMethod = AccessTools.Method(serverMainType, "SendPreparedPacket",
                new[] { connectedClientType, typeof(byte[]), typeof(bool) });
            if (sendPreparedMethod != null)
                callSendPreparedPacket = CompileSendPreparedPacket(serverMainType, connectedClientType, sendPreparedMethod);

            // ServerMain.SendPacket(ConnectedClient, Packet_UdpPacket)
            var udpPacketType = AccessTools.TypeByName("Vintagestory.Server.Packet_UdpPacket")
                ?? AccessTools.TypeByName("Packet_UdpPacket");
            if (udpPacketType != null)
            {
                var sendUdpMethod = AccessTools.Method(serverMainType, "SendPacket",
                    new[] { connectedClientType, udpPacketType });
                if (sendUdpMethod != null)
                    callSendPacketUdp = CompileSendPacketUdp(serverMainType, connectedClientType, udpPacketType, sendUdpMethod);
            }

            return getClients != null && getClientState != null && getClientId != null && getClientPlayer != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool BroadcastBytes_Prefix(object __instance, byte[] data, IServerPlayer[] skipPlayers)
    {
        if (disabled || !OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return true;

        try
        {
            TungstenProfiler.Mark("tungsten-broadcast-bytes");
            var clientsDict = getClients(__instance) as IDictionary;
            if (clientsDict == null) return true;

            foreach (DictionaryEntry entry in clientsDict)
            {
                var client = entry.Value;
                int state = getClientState(client);
                if (state == 0 || state == 1) continue; // Offline=0, Queued=1

                if (!ShouldSkip(client, skipPlayers))
                {
                    var player = getClientPlayer(client);
                    if (player != null)
                        callSendPacketPlayer(__instance, player, data);
                }
            }
            return false;
        }
        catch (Exception)
        {
            Disable("runtime exception in BroadcastBytes");
            return true;
        }
    }

    public static bool BroadcastPacket_Prefix(object __instance, object packet, IServerPlayer[] skipPlayers)
    {
        if (disabled || !OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return true;

        try
        {
            TungstenProfiler.Mark("tungsten-broadcast-packet");
            var clientsDict = getClients(__instance) as IDictionary;
            if (clientsDict == null) return true;

            callSerialize(__instance, packet);
            byte[] preparedData = null;
            bool compressed = false;

            foreach (DictionaryEntry entry in clientsDict)
            {
                var client = entry.Value;
                int state = getClientState(client);
                if (state == 0 || state == 1) continue;

                if (!ShouldSkip(client, skipPlayers))
                {
                    if (preparedData == null)
                    {
                        var socket = getClientSocket(client);
                        if (socket == null) continue;
                        var buffer = getReusableBuffer(__instance);
                        var config = getConfig(__instance);
                        bool compress = getCompressPackets(config);
                        var prepareMethod = AccessTools.Method(socket.GetType(), "PreparePacketForSending");
                        var args = new object[] { buffer, compress, false };
                        preparedData = (byte[])prepareMethod.Invoke(socket, args);
                        compressed = (bool)args[2];
                    }
                    callSendPreparedPacket(__instance, client, preparedData, compressed);
                    if (dummyNetConnectionType != null && dummyNetConnectionType.IsInstanceOfType(getClientSocket(client)))
                        preparedData = null;
                }
            }
            return false;
        }
        catch (Exception)
        {
            Disable("runtime exception in BroadcastPacket");
            return true;
        }
    }

    public static bool BroadcastUdp_Prefix(object __instance, object data, IServerPlayer[] skipPlayers)
    {
        if (disabled || !OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return true;

        try
        {
            TungstenProfiler.Mark("tungsten-broadcast-udp");
            var clientsDict = getClients(__instance) as IDictionary;
            if (clientsDict == null) return true;

            foreach (DictionaryEntry entry in clientsDict)
            {
                var client = entry.Value;
                int state = getClientState(client);
                if (state == 0 || state == 1) continue;

                if (!ShouldSkip(client, skipPlayers))
                    callSendPacketUdp(__instance, client, data);
            }
            return false;
        }
        catch (Exception)
        {
            Disable("runtime exception in BroadcastUdp");
            return true;
        }
    }

    private static bool ShouldSkip(object client, IServerPlayer[] skipPlayers)
    {
        if (skipPlayers == null || skipPlayers.Length == 0)
            return false;

        int clientId = getClientId(client);
        for (int i = 0; i < skipPlayers.Length; i++)
        {
            if (skipPlayers[i]?.ClientId == clientId)
                return true;
        }
        return false;
    }

    #region Compiled Delegate Helpers

    private static System.Func<object, T> CompileGetter<T>(Type declaringType, FieldInfo field)
    {
        var param = Expression.Parameter(typeof(object));
        var access = Expression.Field(Expression.Convert(param, declaringType), field);
        var convert = typeof(T) == typeof(object) ? (Expression)Expression.Convert(access, typeof(object)) : Expression.Convert(access, typeof(T));
        return Expression.Lambda<System.Func<object, T>>(convert, param).Compile();
    }

    private static System.Func<object, int> CompileGetterInt(Type declaringType, FieldInfo field)
    {
        var param = Expression.Parameter(typeof(object));
        var access = Expression.Field(Expression.Convert(param, declaringType), field);
        var convert = Expression.Convert(access, typeof(int));
        return Expression.Lambda<System.Func<object, int>>(convert, param).Compile();
    }

    private static System.Func<object, int> CompilePropertyGetterInt(Type declaringType, PropertyInfo prop)
    {
        var param = Expression.Parameter(typeof(object));
        var access = Expression.Property(Expression.Convert(param, declaringType), prop);
        var convert = Expression.Convert(access, typeof(int));
        return Expression.Lambda<System.Func<object, int>>(convert, param).Compile();
    }

    private static System.Func<object, T> CompilePropertyGetter<T>(Type declaringType, PropertyInfo prop)
    {
        var param = Expression.Parameter(typeof(object));
        var access = Expression.Property(Expression.Convert(param, declaringType), prop);
        var convert = typeof(T) == typeof(object) ? (Expression)Expression.Convert(access, typeof(object)) : Expression.Convert(access, typeof(T));
        return Expression.Lambda<System.Func<object, T>>(convert, param).Compile();
    }

    private static System.Action<object, object> CompileAction(Type declaringType, MethodInfo method)
    {
        var instParam = Expression.Parameter(typeof(object));
        var argParam = Expression.Parameter(typeof(object));
        var call = Expression.Call(
            Expression.Convert(instParam, declaringType),
            method,
            Expression.Convert(argParam, method.GetParameters()[0].ParameterType));
        return Expression.Lambda<System.Action<object, object>>(call, instParam, argParam).Compile();
    }

    private static System.Action<object, IServerPlayer, byte[]> CompileSendPacketPlayer(Type declaringType, MethodInfo method)
    {
        var instParam = Expression.Parameter(typeof(object));
        var playerParam = Expression.Parameter(typeof(IServerPlayer));
        var dataParam = Expression.Parameter(typeof(byte[]));
        var call = Expression.Call(
            Expression.Convert(instParam, declaringType),
            method, playerParam, dataParam);
        return Expression.Lambda<System.Action<object, IServerPlayer, byte[]>>(call, instParam, playerParam, dataParam).Compile();
    }

    private static System.Action<object, object, byte[], bool> CompileSendPreparedPacket(Type declaringType, Type clientType, MethodInfo method)
    {
        var instParam = Expression.Parameter(typeof(object));
        var clientParam = Expression.Parameter(typeof(object));
        var dataParam = Expression.Parameter(typeof(byte[]));
        var compressedParam = Expression.Parameter(typeof(bool));
        var call = Expression.Call(
            Expression.Convert(instParam, declaringType),
            method,
            Expression.Convert(clientParam, clientType), dataParam, compressedParam);
        return Expression.Lambda<System.Action<object, object, byte[], bool>>(call, instParam, clientParam, dataParam, compressedParam).Compile();
    }

    private static System.Action<object, object, object> CompileSendPacketUdp(Type declaringType, Type clientType, Type packetType, MethodInfo method)
    {
        var instParam = Expression.Parameter(typeof(object));
        var clientParam = Expression.Parameter(typeof(object));
        var dataParam = Expression.Parameter(typeof(object));
        var call = Expression.Call(
            Expression.Convert(instParam, declaringType),
            method,
            Expression.Convert(clientParam, clientType),
            Expression.Convert(dataParam, packetType));
        return Expression.Lambda<System.Action<object, object, object>>(call, instParam, clientParam, dataParam).Compile();
    }

    #endregion

    private static void Disable(string reason)
    {
        disabled = true;
        OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);
        if (Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
            TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] [BroadcastLinqOptimization] Disabled: {reason}");
    }

    public static void Dispose()
    {
        disabled = false;
        disableLogGate = 0;
        getClients = null;
        getClientState = null;
        getClientId = null;
        getClientPlayer = null;
        getClientSocket = null;
        getReusableBuffer = null;
        getDoNetBenchmark = null;
        getConfig = null;
        getCompressPackets = null;
        callSerialize = null;
        callSendPacketPlayer = null;
        callSendPreparedPacket = null;
        callSendPacketUdp = null;
        api = null;
    }
}
