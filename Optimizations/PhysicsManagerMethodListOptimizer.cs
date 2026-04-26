using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Server;

namespace Tungsten.Optimizations;

/// <summary>
/// Eliminates list allocations in PhysicsManager method-local variables.
/// Targets: SendPositionsAndAnimations (3 lists) and SendTrackedEntitiesStateChanges (1 list).
/// Impact: ~420 allocations/minute eliminated (30Hz × 4 lists × threads).
/// </summary>
public static class PhysicsManagerMethodListOptimizer
{
    private static ICoreServerAPI api;

    public static void Initialize(ICoreServerAPI serverApi, Harmony harmony)
    {
        api = serverApi;

        var physicsManagerType = AccessTools.TypeByName("Vintagestory.Server.PhysicsManager");
        if (physicsManagerType == null)
        {
            api.Logger.Warning("[Tungsten] PhysicsManagerMethodListOptimizer: Could not find PhysicsManager");
            return;
        }

        // Patch SendPositionsAndAnimations (3 list allocations)
        var sendPositionsMethod = AccessTools.Method(physicsManagerType, "SendPositionsAndAnimations");
        if (sendPositionsMethod != null)
        {
            harmony.Patch(sendPositionsMethod,
                transpiler: new HarmonyMethod(typeof(PhysicsManagerMethodListOptimizer), nameof(SendPositionsAndAnimations_Transpiler)));        }

        // Patch SendTrackedEntitiesStateChanges (1 list allocation)
        var sendTrackedMethod = AccessTools.Method(physicsManagerType, "SendTrackedEntitiesStateChanges");
        if (sendTrackedMethod != null)
        {
            harmony.Patch(sendTrackedMethod,
                transpiler: new HarmonyMethod(typeof(PhysicsManagerMethodListOptimizer), nameof(SendTrackedEntitiesStateChanges_Transpiler)));        }
    }

    public static IEnumerable<CodeInstruction> SendPositionsAndAnimations_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        int allocCount = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor &&
                ctor.DeclaringType?.IsGenericType == true &&
                ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                ctor.GetParameters().Length == 0)
            {
                allocCount++;
            }
        }

        if (allocCount != 3)
        {
            api?.Logger.Warning($"[Tungsten] SendPositionsAndAnimations: Expected 3 list allocations, found {allocCount}. Optimization disabled.");
            return instructions;
        }

        int replaced = 0;
        int slot = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor &&
                ctor.DeclaringType?.IsGenericType == true &&
                ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                ctor.GetParameters().Length == 0)
            {
                // Replace first 3 List allocations with typed reusable lists
                ReplaceNewobjWithReusableList(codes, i, ctor.DeclaringType, slot);
                replaced++;
                slot++;
                if (replaced >= 3) break;
            }
        }

        if (replaced > 0)
            api?.Logger.Debug($"[Tungsten] SendPositionsAndAnimations: Replaced {replaced} list allocations");

        return codes;
    }

    public static IEnumerable<CodeInstruction> SendTrackedEntitiesStateChanges_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        int allocCount = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor &&
                ctor.DeclaringType?.IsGenericType == true &&
                ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                ctor.GetParameters().Length == 0)
            {
                allocCount++;
            }
        }

        if (allocCount != 1)
        {
            api?.Logger.Warning($"[Tungsten] SendTrackedEntitiesStateChanges: Expected 1 list allocation, found {allocCount}. Optimization disabled.");
            return instructions;
        }

        int replaced = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor &&
                ctor.DeclaringType?.IsGenericType == true &&
                ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                ctor.GetParameters().Length == 0)
            {
                ReplaceNewobjWithReusableList(codes, i, ctor.DeclaringType, 0);
                replaced++;
                break; // Only replace first allocation
            }
        }

        if (replaced > 0)
            api?.Logger.Debug($"[Tungsten] SendTrackedEntitiesStateChanges: Replaced {replaced} list allocation");

        return codes;
    }

    public static void Dispose()
    {
        ReusableCollectionPool.ClearAll();
        api = null;
    }

    private static void ReplaceNewobjWithReusableList(List<CodeInstruction> codes, int index, Type listType, int slot)
    {
        var getTypeFromHandle = AccessTools.Method(typeof(Type), nameof(Type.GetTypeFromHandle));
        var getList = AccessTools.Method(typeof(ReusableCollectionPool), nameof(ReusableCollectionPool.GetList));

        var original = codes[index];
        var inst = new List<CodeInstruction>
        {
            new CodeInstruction(OpCodes.Ldtoken, listType),
            new CodeInstruction(OpCodes.Call, getTypeFromHandle),
            new CodeInstruction(OpCodes.Ldc_I4, slot),
            new CodeInstruction(OpCodes.Call, getList),
            new CodeInstruction(OpCodes.Castclass, listType)
        };

        inst[0].labels.AddRange(original.labels);
        inst[0].blocks.AddRange(original.blocks);
        codes[index] = inst[0];
        codes.InsertRange(index + 1, inst.GetRange(1, inst.Count - 1));
    }
}
