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
/// v1.9.3: Removed ThreadLocal - BuildClientList is single-threaded (main server thread).
/// </summary>
public static class PhysicsManagerListOptimizer
{
    private static ICoreServerAPI api;
    private static bool isEnabled;

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

        ReplaceNewobjWithReusableList(codes, targetIndex.Value, ((ConstructorInfo)codes[targetIndex.Value].operand).DeclaringType, 0);
        api?.Logger.Debug($"[Tungsten] PhysicsManagerListOptimizer: Replaced allocation at IL_{targetIndex.Value}");

        return codes;
    }

    public static void Dispose()
    {
        isEnabled = false;
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
