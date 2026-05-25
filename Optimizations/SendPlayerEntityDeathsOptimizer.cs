using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten;

public class SendPlayerEntityDeathsOptimizer
{
    private const string CircuitKey = "SendPlayerEntityDeathsOptimization";

    private readonly ICoreServerAPI api;
    private static ICoreServerAPI staticApi;
    private static volatile bool disabled;
    private static int failureCount;
    private static int disableLogGate;

    public SendPlayerEntityDeathsOptimizer(ICoreServerAPI api)
    {
        this.api = api;
        staticApi = api;
    }

    /// <summary>
    /// Cleanup ThreadLocal registrations if initialization fails.
    /// </summary>
    public void CleanupOnFailure()
    {
        try
        {
            api?.Logger?.Debug("[Tungsten] [SendPlayerEntityDeathsOptimization] CleanupOnFailure: clearing reusable list state");
            ReusableCollectionPool.ClearAll();
        }
        finally
        {
            disabled = true;
        }
    }

    public static int GetFailureCount()
    {
        return failureCount;
    }

    public static string GetRuntimeStatus()
    {
        if (disabled)
            return "disabled (optimization-local)";

        if (!OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return "disabled by circuit-breaker";

        return "active";
    }

    public void ApplyPatches(Harmony harmony)
    {
        if (!OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return;

        var entitySimulationType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemEntitySimulation");
        var sendPlayerEntityDeathsMethod = AccessTools.Method(entitySimulationType, "SendPlayerEntityDeaths");

        if (sendPlayerEntityDeathsMethod == null)
        {
            Disable("Target method SendPlayerEntityDeaths not found");
            return;
        }

        try
        {
            var transpiler = new HarmonyMethod(typeof(SendPlayerEntityDeathsOptimizer), nameof(Transpiler));
            harmony.Patch(sendPlayerEntityDeathsMethod, transpiler: transpiler);
        }
        catch (Exception ex)
        {
            Disable($"Patch registration failed: {ex.GetType().Name} {ex.Message}");
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var toReusableList = AccessTools.Method(typeof(ReusableCollectionPool), nameof(ReusableCollectionPool.ToList));
        var getTypeFromHandle = AccessTools.Method(typeof(Type), nameof(Type.GetTypeFromHandle));
        var listType = typeof(List<>);
        var toListCount = 0;
        var replacementCount = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Call && codes[i].operand is System.Reflection.MethodInfo method)
            {
                if (method.Name == "ToList" && method.DeclaringType == typeof(Enumerable))
                    toListCount++;
            }
        }

        if (toListCount != 1)
        {
            Disable($"Expected 1 Enumerable.ToList call, found {toListCount}");
            return instructions;
        }

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Call && codes[i].operand is System.Reflection.MethodInfo method)
            {
                if (method.Name == "ToList" && method.DeclaringType == typeof(Enumerable))
                {
                    var elementType = method.GetGenericArguments()[0];
                    var concreteListType = listType.MakeGenericType(elementType);

                    var inst = new List<CodeInstruction>
                    {
                        new CodeInstruction(OpCodes.Ldtoken, concreteListType),
                        new CodeInstruction(OpCodes.Call, getTypeFromHandle),
                        new CodeInstruction(OpCodes.Ldc_I4_0),
                        new CodeInstruction(OpCodes.Call, toReusableList),
                        new CodeInstruction(OpCodes.Castclass, concreteListType)
                    };

                    codes[i] = inst[0];
                    codes.InsertRange(i + 1, inst.GetRange(1, inst.Count - 1));
                    replacementCount++;
                    break;
                }
            }
        }

        if (replacementCount != 1)
        {
            Disable("Could not replace Enumerable.ToList call");
            return instructions;
        }

        return codes;
    }

    public static void Dispose()
    {
        ReusableCollectionPool.ClearAll();
        disabled = false;
        staticApi = null;
    }

    private static void Disable(string reason)
    {
        Interlocked.Increment(ref failureCount);
        disabled = true;
        OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);

        if (Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
        {
            staticApi?.Logger?.Warning("[Tungsten] [SendPlayerEntityDeathsOptimization] Disabled and falling back to vanilla: " + reason);
        }
    }
}
