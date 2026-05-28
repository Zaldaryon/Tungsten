using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Server;

namespace Tungsten.Optimizations;

/// <summary>
/// Optimizes BuildClientList allocation in PhysicsManager (called every server tick).
/// Eliminates 1,200 List allocations per minute.
/// 
/// Safety: Uses a dedicated [ThreadStatic] list instead of ReusableCollectionPool.
/// BuildClientList assigns its result to a field (ClientList) that is read by physics
/// threads throughout the tick. A pooled instance could be cleared prematurely by the
/// pool on the next tick. The ThreadStatic approach ensures the list persists until
/// explicitly cleared on next call from the same thread.
/// </summary>
public static class PhysicsManagerListOptimizer
{
    private static ICoreServerAPI api;
    private static bool isEnabled;

    [ThreadStatic] private static object reusableClientList;

    public static void Initialize(ICoreServerAPI serverApi, Harmony harmony)
    {
        api = serverApi;
        isEnabled = TungstenMod.Instance?.GetConfig()?.EnablePhysicsManagerListOptimization ?? true;
        if (!isEnabled) return;

        var physicsManagerType = AccessTools.TypeByName("Vintagestory.Server.PhysicsManager");
        if (physicsManagerType == null)
        {
            api.Logger.Warning("[Tungsten] PhysicsManagerListOptimizer: Could not find PhysicsManager");
            return;
        }

        var buildClientListMethod = AccessTools.Method(physicsManagerType, "BuildClientList");
        if (buildClientListMethod != null)
        {
            harmony.Patch(buildClientListMethod,
                transpiler: new HarmonyMethod(typeof(PhysicsManagerListOptimizer), nameof(BuildClientList_Transpiler)));
        }
    }

    public static IEnumerable<CodeInstruction> BuildClientList_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        int allocations = 0;
        int? targetIndex = null;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor &&
                ctor.DeclaringType?.IsGenericType == true &&
                ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = ctor.DeclaringType.GetGenericArguments()[0];
                if (elementType.Name == "ConnectedClient" && ctor.GetParameters().Length == 0)
                {
                    allocations++;
                    targetIndex ??= i;
                }
            }
        }

        if (allocations != 1 || targetIndex == null)
        {
            api?.Logger.Warning($"[Tungsten] PhysicsManagerListOptimizer: Expected 1 List<ConnectedClient> allocation, found {allocations}. Optimization disabled.");
            return instructions;
        }

        // Replace new List<ConnectedClient>() with GetReusableClientList<ConnectedClient>()
        var listType = ((ConstructorInfo)codes[targetIndex.Value].operand).DeclaringType;
        var elementT = listType.GetGenericArguments()[0];
        var getMethod = AccessTools.Method(typeof(PhysicsManagerListOptimizer), nameof(GetReusableClientList))
            .MakeGenericMethod(elementT);

        var original = codes[targetIndex.Value];
        codes[targetIndex.Value] = new CodeInstruction(OpCodes.Call, getMethod)
        {
            labels = original.labels,
            blocks = original.blocks
        };

        return codes;
    }

    /// <summary>
    /// Returns a cleared, dedicated ThreadStatic list. This list is never shared via pool
    /// and persists until the next call from the same thread, ensuring physics threads
    /// that hold a reference to ClientList see stable data throughout the tick.
    /// </summary>
    public static List<T> GetReusableClientList<T>()
    {
        Diagnostics.DiagPhysicsManagerList.OnTick();
        Diagnostics.DiagPhysicsManagerList.OnAllocationAvoided();

        if (reusableClientList is List<T> list)
        {
            list.Clear();
            return list;
        }

        var newList = new List<T>(64);
        reusableClientList = newList;
        return newList;
    }

    public static void Dispose()
    {
        isEnabled = false;
        reusableClientList = null;
        api = null;
    }
}
