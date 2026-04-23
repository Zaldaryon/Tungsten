using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Tungsten;

/// <summary>
/// Optimizes block position list allocations in ServerSystemBlockSimulation.
/// Replaces 3x List&lt;BlockPos&gt; allocations per 100ms tick with ThreadLocal reuse.
/// </summary>
public class BlockListReuseOptimizer
{
    private readonly ICoreServerAPI api;

    private static readonly ThreadLocal<List<BlockPos>> reusableModifiedBlocksList = new(() => new List<BlockPos>());
    private static readonly ThreadLocal<List<BlockPos>> reusableModifiedBlocksNoRelightList = new(() => new List<BlockPos>());
    private static readonly ThreadLocal<List<BlockPos>> reusableModifiedDecorsList = new(() => new List<BlockPos>());

    public BlockListReuseOptimizer(ICoreServerAPI api)
    {
        this.api = api;

        // Register ThreadLocal instances for proper disposal (v1.10.0)
        ThreadLocalRegistry.Register(reusableModifiedBlocksList);
        ThreadLocalRegistry.Register(reusableModifiedBlocksNoRelightList);
        ThreadLocalRegistry.Register(reusableModifiedDecorsList);
    }

    /// <summary>
    /// Cleanup ThreadLocal registrations if initialization fails.
    /// </summary>
    public void CleanupOnFailure()
    {
        ThreadLocalRegistry.UnregisterAll(
            reusableModifiedBlocksList,
            reusableModifiedBlocksNoRelightList,
            reusableModifiedDecorsList
        );
    }

    public void ApplyPatches(Harmony harmony)
    {
        var blockSimulationType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemBlockSimulation");

        if (blockSimulationType == null)
        {
            api.Logger.Warning("[Tungsten] BlockListReuse: Could not find ServerSystemBlockSimulation type. Optimization disabled.");
            return;
        }

        // Patch HandleDirtyAndUpdatedBlocks method
        var handleDirtyMethod = AccessTools.Method(blockSimulationType, "HandleDirtyAndUpdatedBlocks");
        if (handleDirtyMethod != null)
        {
            var prefix = new HarmonyMethod(typeof(BlockListReuseOptimizer), nameof(HandleDirtyAndUpdatedBlocks_Prefix));
            var transpiler = new HarmonyMethod(typeof(BlockListReuseOptimizer), nameof(HandleDirtyAndUpdatedBlocks_Transpiler));
            harmony.Patch(handleDirtyMethod, prefix: prefix, transpiler: transpiler);        }
        else
        {
            api.Logger.Warning("[Tungsten] BlockListReuse: Could not find HandleDirtyAndUpdatedBlocks method. Optimization disabled.");
        }
    }

    /// <summary>
    /// Clear all reusable lists before HandleDirtyAndUpdatedBlocks runs.
    /// </summary>
    public static void HandleDirtyAndUpdatedBlocks_Prefix()
    {
        try
        {
            // v1.10.0: ThreadLocalHelper uses cached config (no parameters needed)
            ThreadLocalHelper.GetAndClear(reusableModifiedBlocksList);
            ThreadLocalHelper.GetAndClear(reusableModifiedBlocksNoRelightList);
            ThreadLocalHelper.GetAndClear(reusableModifiedDecorsList);
        }
        catch
        {
            // Suppress exceptions - let original code run
        }
    }

    /// <summary>
    /// Transpiler for HandleDirtyAndUpdatedBlocks method.
    /// Replaces three List&lt;BlockPos&gt; allocations with reusable static lists.
    /// Uses semantic analysis to identify which list is which based on usage patterns.
    /// </summary>
    public static IEnumerable<CodeInstruction> HandleDirtyAndUpdatedBlocks_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var getModifiedBlocksList = AccessTools.Method(typeof(BlockListReuseOptimizer), nameof(GetReusableModifiedBlocksList));
        var getModifiedBlocksNoRelightList = AccessTools.Method(typeof(BlockListReuseOptimizer), nameof(GetReusableModifiedBlocksNoRelightList));
        var getModifiedDecorsList = AccessTools.Method(typeof(BlockListReuseOptimizer), nameof(GetReusableModifiedDecorsList));

        // Step 1: Find all List<BlockPos> allocations and track which local variable they're stored in
        var listAllocations = new Dictionary<int, (int allocIndex, Type elementType)>(); // localVarIndex -> (IL index, element type)

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Newobj)
            {
                var constructorInfo = codes[i].operand as ConstructorInfo;
                if (constructorInfo != null &&
                    constructorInfo.DeclaringType != null &&
                    constructorInfo.DeclaringType.IsGenericType &&
                    constructorInfo.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var elementType = constructorInfo.DeclaringType.GetGenericArguments()[0];

                    // Only interested in List<BlockPos>
                    if (elementType.Name == "BlockPos")
                    {
                        // Get which local variable this list is stored in
                        if (i + 1 < codes.Count)
                        {
                            int localVarIndex = GetLocalVariableIndex(codes[i + 1]);
                            if (localVarIndex >= 0)
                            {
                                listAllocations[localVarIndex] = (i, elementType);
                            }
                        }
                    }
                }
            }
        }

        // Validate we found exactly 3 List<BlockPos> allocations
        if (listAllocations.Count != 3)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                $"[Tungsten] BlockListReuse: Expected 3 List<BlockPos> allocations in HandleDirtyAndUpdatedBlocks, found {listAllocations.Count}. Game code may have changed. Optimization disabled for safety.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        // Step 2: Find SendSetBlocksPacket and SendSetDecorsPackets calls to identify which list is which
        // Pattern analysis from game code:
        // Line 591-598: ModifiedBlocks -> SendSetBlocksPacket(positions, 47)
        // Line 602-609: ModifiedBlocksNoRelight -> SendSetBlocksPacket(positions, 63)
        // Line 618-622: ModifiedDecors -> SendSetDecorsPackets(positions1)

        // These methods are in ServerMain (this.server), not ServerSystemBlockSimulation
        var serverMainType = AccessTools.TypeByName("Vintagestory.Server.ServerMain");

        if (serverMainType == null)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                "[Tungsten] BlockListReuse: Could not find ServerMain type. Optimization disabled.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        var sendSetBlocksPacketMethod = AccessTools.Method(serverMainType, "SendSetBlocksPacket");
        var sendSetDecorsPacketsMethod = AccessTools.Method(serverMainType, "SendSetDecorsPackets");

        if (sendSetBlocksPacketMethod == null || sendSetDecorsPacketsMethod == null)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                $"[Tungsten] BlockListReuse: Could not find packet sending methods (SendSetBlocksPacket={sendSetBlocksPacketMethod != null}, SendSetDecorsPackets={sendSetDecorsPacketsMethod != null}). Optimization disabled.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        // Track which local variables are used with which methods
        var sendSetBlocksPacketUsages = new List<(int callIndex, int localVarIndex, int? constantValue)>();
        var sendSetDecorsPacketsUsages = new List<(int callIndex, int localVarIndex)>();

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Callvirt || codes[i].opcode == OpCodes.Call)
            {
                var method = codes[i].operand as MethodInfo;

                if (method == sendSetBlocksPacketMethod)
                {
                    // Find the local variable loaded before this call
                    // Pattern: ldloc.X -> ldc.i4.X (constant) -> callvirt SendSetBlocksPacket
                    int? localVar = null;
                    int? constant = null;

                    // Look backwards for ldloc (first parameter - the list)
                    for (int j = i - 1; j >= 0 && j > i - 10; j--)
                    {
                        int varIndex = GetLocalVariableIndex(codes[j]);
                        if (varIndex >= 0 && listAllocations.ContainsKey(varIndex))
                        {
                            localVar = varIndex;
                            break;
                        }
                    }

                    // Look backwards for ldc.i4 (second parameter - the constant)
                    for (int j = i - 1; j >= 0 && j > i - 5; j--)
                    {
                        if (codes[j].opcode == OpCodes.Ldc_I4 ||
                            codes[j].opcode == OpCodes.Ldc_I4_S ||
                            codes[j].opcode == OpCodes.Ldc_I4_0 ||
                            codes[j].opcode == OpCodes.Ldc_I4_1 ||
                            codes[j].opcode == OpCodes.Ldc_I4_2 ||
                            codes[j].opcode == OpCodes.Ldc_I4_3 ||
                            codes[j].opcode == OpCodes.Ldc_I4_4 ||
                            codes[j].opcode == OpCodes.Ldc_I4_5 ||
                            codes[j].opcode == OpCodes.Ldc_I4_6 ||
                            codes[j].opcode == OpCodes.Ldc_I4_7 ||
                            codes[j].opcode == OpCodes.Ldc_I4_8)
                        {
                            constant = GetConstantValue(codes[j]);
                            break;
                        }
                    }

                    if (localVar.HasValue)
                    {
                        sendSetBlocksPacketUsages.Add((i, localVar.Value, constant));
                    }
                }
                else if (method == sendSetDecorsPacketsMethod)
                {
                    // Find the local variable loaded before this call
                    int? localVar = null;
                    for (int j = i - 1; j >= 0 && j > i - 10; j--)
                    {
                        int varIndex = GetLocalVariableIndex(codes[j]);
                        if (varIndex >= 0 && listAllocations.ContainsKey(varIndex))
                        {
                            localVar = varIndex;
                            break;
                        }
                    }

                    if (localVar.HasValue)
                    {
                        sendSetDecorsPacketsUsages.Add((i, localVar.Value));
                    }
                }
            }
        }

        // Step 3: Identify which list is which based on usage
        int? modifiedBlocksListLocalVar = null;
        int? modifiedBlocksNoRelightListLocalVar = null;
        int? modifiedDecorsListLocalVar = null;

        // Validate usage counts
        if (sendSetDecorsPacketsUsages.Count != 1)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                $"[Tungsten] BlockListReuse: Expected 1 SendSetDecorsPackets call, found {sendSetDecorsPacketsUsages.Count}. Game code may have changed.");
            // Don't fail yet - might still work with declaration order fallback
        }

        if (sendSetBlocksPacketUsages.Count != 2)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                $"[Tungsten] BlockListReuse: Expected 2 SendSetBlocksPacket calls, found {sendSetBlocksPacketUsages.Count}. Game code may have changed.");
            // Don't fail yet - might still work with declaration order fallback
        }

        // ModifiedDecors uses SendSetDecorsPackets - should have exactly 1 usage
        if (sendSetDecorsPacketsUsages.Count == 1)
        {
            modifiedDecorsListLocalVar = sendSetDecorsPacketsUsages[0].localVarIndex;
        }

        // ModifiedBlocks and ModifiedBlocksNoRelight both use SendSetBlocksPacket
        // ModifiedBlocks uses constant 47 (0x2F)
        // ModifiedBlocksNoRelight uses constant 63 (0x3F)
        foreach (var usage in sendSetBlocksPacketUsages)
        {
            if (usage.constantValue == 47)
            {
                modifiedBlocksListLocalVar = usage.localVarIndex;
            }
            else if (usage.constantValue == 63)
            {
                modifiedBlocksNoRelightListLocalVar = usage.localVarIndex;
            }
            else if (usage.constantValue.HasValue)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning(
                    $"[Tungsten] BlockListReuse: Found unexpected constant value {usage.constantValue.Value} in SendSetBlocksPacket call. Expected 47 or 63. Game code may have changed.");
            }
        }

        // Step 4: Fallback - if semantic analysis failed, try declaration order
        if (!modifiedBlocksListLocalVar.HasValue || !modifiedBlocksNoRelightListLocalVar.HasValue || !modifiedDecorsListLocalVar.HasValue)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                $"[Tungsten] BlockListReuse: Semantic analysis failed (ModifiedBlocks={modifiedBlocksListLocalVar.HasValue}, ModifiedBlocksNoRelight={modifiedBlocksNoRelightListLocalVar.HasValue}, ModifiedDecors={modifiedDecorsListLocalVar.HasValue}). Optimization disabled for safety.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        // Step 5: Validate we found all three lists
        if (!modifiedBlocksListLocalVar.HasValue || !modifiedBlocksNoRelightListLocalVar.HasValue || !modifiedDecorsListLocalVar.HasValue)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                $"[Tungsten] BlockListReuse: Could not identify all lists in HandleDirtyAndUpdatedBlocks (ModifiedBlocks={modifiedBlocksListLocalVar.HasValue}, ModifiedBlocksNoRelight={modifiedBlocksNoRelightListLocalVar.HasValue}, ModifiedDecors={modifiedDecorsListLocalVar.HasValue}). Optimization disabled.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        // Step 6: Validate the local variables match our allocations
        if (!listAllocations.ContainsKey(modifiedBlocksListLocalVar.Value) ||
            !listAllocations.ContainsKey(modifiedBlocksNoRelightListLocalVar.Value) ||
            !listAllocations.ContainsKey(modifiedDecorsListLocalVar.Value))
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                "[Tungsten] BlockListReuse: List usage doesn't match allocations in HandleDirtyAndUpdatedBlocks. Optimization disabled.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        // Step 7: Replace allocations based on semantic usage
        var modifiedBlocksAlloc = listAllocations[modifiedBlocksListLocalVar.Value];
        var modifiedBlocksNoRelightAlloc = listAllocations[modifiedBlocksNoRelightListLocalVar.Value];
        var modifiedDecorsAlloc = listAllocations[modifiedDecorsListLocalVar.Value];

        codes[modifiedBlocksAlloc.allocIndex] = new CodeInstruction(OpCodes.Call, getModifiedBlocksList);
        codes[modifiedBlocksNoRelightAlloc.allocIndex] = new CodeInstruction(OpCodes.Call, getModifiedBlocksNoRelightList);
        codes[modifiedDecorsAlloc.allocIndex] = new CodeInstruction(OpCodes.Call, getModifiedDecorsList);
        foreach (var code in codes)
            yield return code;
    }

    // Helper to extract local variable index from stloc/ldloc instructions
    private static int GetLocalVariableIndex(CodeInstruction instruction)
    {
        // Store local variable (stloc)
        if (instruction.opcode == OpCodes.Stloc_0) return 0;
        if (instruction.opcode == OpCodes.Stloc_1) return 1;
        if (instruction.opcode == OpCodes.Stloc_2) return 2;
        if (instruction.opcode == OpCodes.Stloc_3) return 3;
        if (instruction.opcode == OpCodes.Stloc_S || instruction.opcode == OpCodes.Stloc)
        {
            if (instruction.operand is LocalBuilder lb)
                return lb.LocalIndex;
        }

        // Load local variable (ldloc)
        if (instruction.opcode == OpCodes.Ldloc_0) return 0;
        if (instruction.opcode == OpCodes.Ldloc_1) return 1;
        if (instruction.opcode == OpCodes.Ldloc_2) return 2;
        if (instruction.opcode == OpCodes.Ldloc_3) return 3;
        if (instruction.opcode == OpCodes.Ldloc_S || instruction.opcode == OpCodes.Ldloc)
        {
            if (instruction.operand is LocalBuilder lb)
                return lb.LocalIndex;
        }

        // Load local variable address (ldloca)
        if (instruction.opcode == OpCodes.Ldloca_S || instruction.opcode == OpCodes.Ldloca)
        {
            if (instruction.operand is LocalBuilder lb)
                return lb.LocalIndex;
        }

        return -1;
    }

    // Helper to extract constant integer value from ldc instructions
    private static int? GetConstantValue(CodeInstruction instruction)
    {
        if (instruction.opcode == OpCodes.Ldc_I4_0) return 0;
        if (instruction.opcode == OpCodes.Ldc_I4_1) return 1;
        if (instruction.opcode == OpCodes.Ldc_I4_2) return 2;
        if (instruction.opcode == OpCodes.Ldc_I4_3) return 3;
        if (instruction.opcode == OpCodes.Ldc_I4_4) return 4;
        if (instruction.opcode == OpCodes.Ldc_I4_5) return 5;
        if (instruction.opcode == OpCodes.Ldc_I4_6) return 6;
        if (instruction.opcode == OpCodes.Ldc_I4_7) return 7;
        if (instruction.opcode == OpCodes.Ldc_I4_8) return 8;
        if (instruction.opcode == OpCodes.Ldc_I4 || instruction.opcode == OpCodes.Ldc_I4_S)
        {
            if (instruction.operand is int intValue)
                return intValue;
            if (instruction.operand is sbyte sbyteValue)
                return sbyteValue;
        }
        return null;
    }

    public static List<BlockPos> GetReusableModifiedBlocksList()
    {
        var list = reusableModifiedBlocksList.Value;
        list.Clear();
        return list;
    }

    public static List<BlockPos> GetReusableModifiedBlocksNoRelightList()
    {
        var list = reusableModifiedBlocksNoRelightList.Value;
        list.Clear();
        return list;
    }

    public static List<BlockPos> GetReusableModifiedDecorsList()
    {
        var list = reusableModifiedDecorsList.Value;
        list.Clear();
        return list;
    }
}
