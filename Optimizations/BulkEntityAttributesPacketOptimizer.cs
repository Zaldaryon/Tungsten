using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Tungsten.Diagnostics;
using Vintagestory.API.Server;

namespace Tungsten.Optimizations;

/// <summary>
/// Optimizes ServerPackets.GetBulkEntityAttributesPacket by reusing packet wrapper objects.
/// Vanilla allocates new Packet_BulkEntityAttributes + Packet_Server per call.
/// Called per-client per-tick when entities have dirty attributes (~200-400 calls/sec on busy servers).
/// The ToArray() calls are kept (arrays are consumed by network serializer and cannot be pooled).
/// Savings: eliminates 2 object allocations per call (packet wrappers only).
/// </summary>
public static class BulkEntityAttributesPacketOptimizer
{
    private const string CircuitKey = "BulkEntityAttributesPacketOptimization";
    private static ICoreServerAPI api;
    private static volatile bool disabled;
    private static int disableLogGate;

    // Compiled delegates
    private static Func<object> createBulkPacket;
    private static Func<object> createServerPacket;
    private static Action<object, object> setFullUpdates;
    private static Action<object, object> setPartialUpdates;
    private static Action<object, int> setPacketId;
    private static Action<object, object> setBulkAttributes;
    private static Func<IList, Array> toArrayFull;
    private static Func<IList, Array> toArrayPartial;

    // ThreadLocal reusable packet objects
    [ThreadStatic] private static object reusableBulkPacket;
    [ThreadStatic] private static object reusableServerPacket;

    public static void Initialize(ICoreServerAPI serverApi, Harmony harmony)
    {
        api = serverApi;

        var serverPacketsType = AccessTools.TypeByName("Vintagestory.Server.ServerPackets");
        var packetServerType = AccessTools.TypeByName("Vintagestory.Server.Packet_Server")
            ?? AccessTools.TypeByName("Packet_Server");
        var packetBulkType = AccessTools.TypeByName("Vintagestory.Server.Packet_BulkEntityAttributes")
            ?? AccessTools.TypeByName("Packet_BulkEntityAttributes");
        var packetEntityAttributesType = AccessTools.TypeByName("Vintagestory.Server.Packet_EntityAttributes")
            ?? AccessTools.TypeByName("Packet_EntityAttributes");
        var packetEntityAttributeUpdateType = AccessTools.TypeByName("Vintagestory.Server.Packet_EntityAttributeUpdate")
            ?? AccessTools.TypeByName("Packet_EntityAttributeUpdate");

        if (serverPacketsType == null || packetServerType == null || packetBulkType == null ||
            packetEntityAttributesType == null || packetEntityAttributeUpdateType == null)
        {
            api.Logger.Warning("[Tungsten] [BulkEntityAttributesPacketOptimization] Could not find required types");
            return;
        }

        // Compile constructors
        createBulkPacket = Expression.Lambda<Func<object>>(
            Expression.Convert(Expression.New(packetBulkType), typeof(object))).Compile();
        createServerPacket = Expression.Lambda<Func<object>>(
            Expression.Convert(Expression.New(packetServerType), typeof(object))).Compile();

        // Compile SetFullUpdates/SetPartialUpdates - use single-array overload
        var fullArrayType = packetEntityAttributesType.MakeArrayType();
        var partialArrayType = packetEntityAttributeUpdateType.MakeArrayType();
        var setFullMethod = AccessTools.Method(packetBulkType, "SetFullUpdates", new[] { fullArrayType });
        var setPartialMethod = AccessTools.Method(packetBulkType, "SetPartialUpdates", new[] { partialArrayType });
        if (setFullMethod == null || setPartialMethod == null)
        {
            api.Logger.Warning("[Tungsten] [BulkEntityAttributesPacketOptimization] Could not find Set methods");
            return;
        }

        setFullUpdates = CompileAction(packetBulkType, setFullMethod);
        setPartialUpdates = CompileAction(packetBulkType, setPartialMethod);

        // Compile Packet_Server field setters
        var idField = AccessTools.Field(packetServerType, "Id");
        var bulkField = AccessTools.Field(packetServerType, "BulkEntityAttributes");
        if (idField == null || bulkField == null)
        {
            api.Logger.Warning("[Tungsten] [BulkEntityAttributesPacketOptimization] Could not find Packet_Server fields");
            return;
        }

        setPacketId = CompileFieldSetterInt(packetServerType, idField);
        setBulkAttributes = CompileFieldSetter(packetServerType, bulkField);

        // Compile ToArray helpers using List<T>.ToArray() directly
        var fullListType = typeof(List<>).MakeGenericType(packetEntityAttributesType);
        var partialListType = typeof(List<>).MakeGenericType(packetEntityAttributeUpdateType);
        var fullToArray = fullListType.GetMethod("ToArray");
        var partialToArray = partialListType.GetMethod("ToArray");

        if (fullToArray != null)
        {
            var param = Expression.Parameter(typeof(IList));
            var call = Expression.Call(Expression.Convert(param, fullListType), fullToArray);
            toArrayFull = Expression.Lambda<Func<IList, Array>>(call, param).Compile();
        }
        if (partialToArray != null)
        {
            var param = Expression.Parameter(typeof(IList));
            var call = Expression.Call(Expression.Convert(param, partialListType), partialToArray);
            toArrayPartial = Expression.Lambda<Func<IList, Array>>(call, param).Compile();
        }

        if (toArrayFull == null || toArrayPartial == null)
        {
            api.Logger.Warning("[Tungsten] [BulkEntityAttributesPacketOptimization] Could not compile ToArray delegates");
            return;
        }

        // Find and patch the method
        var method = AccessTools.Method(serverPacketsType, "GetBulkEntityAttributesPacket",
            new[] { fullListType, partialListType });
        if (method == null)
        {
            api.Logger.Warning("[Tungsten] [BulkEntityAttributesPacketOptimization] Could not find target method");
            return;
        }

        harmony.Patch(method, prefix: new HarmonyMethod(typeof(BulkEntityAttributesPacketOptimizer), nameof(Prefix)));
    }

    public static bool Prefix(object fullPackets, object partialPackets, ref object __result)
    {
        if (disabled || !OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return true;

        try
        {
            TungstenProfiler.Mark("tungsten-bulkattributes");
            DiagBulkEntityAttributesPacket.OnPacketBuilt();

            var fullList = fullPackets as IList;
            var partialList = partialPackets as IList;
            if (fullList == null || partialList == null) return true;

            // Reuse packet wrapper objects (they're consumed synchronously by SendPacket)
            var bulk = reusableBulkPacket ??= createBulkPacket();
            var packet = reusableServerPacket ??= createServerPacket();
            DiagBulkEntityAttributesPacket.OnWrapperReused();

            // ToArray is unavoidable (network serializer consumes the arrays)
            // but we use compiled delegates instead of virtual dispatch
            var fullArray = toArrayFull(fullList);
            var partialArray = toArrayPartial(partialList);

            setFullUpdates(bulk, fullArray);
            setPartialUpdates(bulk, partialArray);
            setPacketId(packet, 60);
            setBulkAttributes(packet, bulk);

            __result = packet;
            return false;
        }
        catch (Exception)
        {
            Disable("runtime exception");
            return true;
        }
    }

    #region Compiled Delegate Helpers

    private static Action<object, object> CompileAction(Type declaringType, MethodInfo method)
    {
        var instParam = Expression.Parameter(typeof(object));
        var argParam = Expression.Parameter(typeof(object));
        var call = Expression.Call(
            Expression.Convert(instParam, declaringType),
            method,
            Expression.Convert(argParam, method.GetParameters()[0].ParameterType));
        return Expression.Lambda<Action<object, object>>(call, instParam, argParam).Compile();
    }

    private static Action<object, int> CompileFieldSetterInt(Type declaringType, FieldInfo field)
    {
        var instParam = Expression.Parameter(typeof(object));
        var valParam = Expression.Parameter(typeof(int));
        var assign = Expression.Assign(
            Expression.Field(Expression.Convert(instParam, declaringType), field),
            Expression.Convert(valParam, field.FieldType));
        return Expression.Lambda<Action<object, int>>(assign, instParam, valParam).Compile();
    }

    private static Action<object, object> CompileFieldSetter(Type declaringType, FieldInfo field)
    {
        var instParam = Expression.Parameter(typeof(object));
        var valParam = Expression.Parameter(typeof(object));
        var assign = Expression.Assign(
            Expression.Field(Expression.Convert(instParam, declaringType), field),
            Expression.Convert(valParam, field.FieldType));
        return Expression.Lambda<Action<object, object>>(assign, instParam, valParam).Compile();
    }

    #endregion

    private static void Disable(string reason)
    {
        disabled = true;
        OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);
        if (Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
            TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] [BulkEntityAttributesPacketOptimization] Disabled: {reason}");
    }

    public static void Dispose()
    {
        disabled = false;
        disableLogGate = 0;
        reusableBulkPacket = null;
        reusableServerPacket = null;
        api = null;
    }
}
