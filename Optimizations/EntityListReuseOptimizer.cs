using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Tungsten;

public class EntityListReuseOptimizer
{
    private readonly ICoreServerAPI api;

    private static readonly ThreadLocal<List<KeyValuePair<Entity, EntityDespawnData>>> reusableDespawnList = new(() => new List<KeyValuePair<Entity, EntityDespawnData>>());
    private static bool tickEntitiesPatched;

    private static readonly ThreadLocal<List<long>> reusableEntityIdList = new(() => new List<long>());
    private static readonly ThreadLocal<List<int>> reusableDespawnReasonList = new(() => new List<int>());
    private static readonly ThreadLocal<List<int>> reusableDamageSourceList = new(() => new List<int>());
    private static int despawnListPeak;
    private static int entityIdListPeak;
    private static int despawnReasonListPeak;
    private static int damageSourceListPeak;

    public EntityListReuseOptimizer(ICoreServerAPI api)
    {
        this.api = api;

        // Register ThreadLocal instances for proper disposal (v1.10.0)
        ThreadLocalRegistry.Register(reusableDespawnList);
        ThreadLocalRegistry.Register(reusableEntityIdList);
        ThreadLocalRegistry.Register(reusableDespawnReasonList);
        ThreadLocalRegistry.Register(reusableDamageSourceList);
    }

    /// <summary>
    /// Cleanup ThreadLocal registrations if initialization fails.
    /// </summary>
    public void CleanupOnFailure()
    {
        ThreadLocalRegistry.UnregisterAll(
            reusableDespawnList,
            reusableEntityIdList,
            reusableDespawnReasonList,
            reusableDamageSourceList
        );
    }

    public static bool TickEntitiesPatched => tickEntitiesPatched;

    public void ApplyPatches(Harmony harmony)
    {
        var entitySimulationType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemEntitySimulation");

        // Patch TickEntities method
        var tickEntitiesMethod = AccessTools.Method(entitySimulationType, "TickEntities");
        if (tickEntitiesMethod != null)
        {
            var tickEntitiesPrefix = new HarmonyMethod(typeof(EntityListReuseOptimizer), nameof(TickEntities_Prefix));
            var tickEntitiesTranspiler = new HarmonyMethod(typeof(EntityListReuseOptimizer), nameof(TickEntities_Transpiler));
            harmony.Patch(tickEntitiesMethod, prefix: tickEntitiesPrefix, transpiler: tickEntitiesTranspiler);
        }

        // Patch SendEntityDespawns method
        var sendEntityDespawnsMethod = AccessTools.Method(entitySimulationType, "SendEntityDespawns");
        if (sendEntityDespawnsMethod != null)
        {
            var sendDespawnsPrefix = new HarmonyMethod(typeof(EntityListReuseOptimizer), nameof(SendEntityDespawns_Prefix));
            var sendDespawnsTranspiler = new HarmonyMethod(typeof(EntityListReuseOptimizer), nameof(SendEntityDespawns_Transpiler));
            harmony.Patch(sendEntityDespawnsMethod, prefix: sendDespawnsPrefix, transpiler: sendDespawnsTranspiler);
        }
    }

    // Clear the reusable list before TickEntities runs
    // v1.10.0: Added capacity trimming to reduce memory footprint
    // v1.10.1: Removed redundant GetConfig() call - if patch exists, optimization is enabled
    public static void TickEntities_Prefix()
    {
        try
        {
            // v1.10.0: ThreadLocalHelper uses cached config (no parameters needed)
            ThreadLocalHelper.GetAndClearWithPeak(reusableDespawnList, ref despawnListPeak);
        }
        catch { }
    }

    // Replace "new List<KeyValuePair<Entity, EntityDespawnData>>()" with our reusable list
    public static IEnumerable<CodeInstruction> TickEntities_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var getReusableList = AccessTools.Method(typeof(EntityListReuseOptimizer), nameof(GetReusableDespawnList));

        int replacementCount = 0;
        var targetType = typeof(KeyValuePair<Entity, EntityDespawnData>);

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Newobj)
            {
                var constructorInfo = codes[i].operand as ConstructorInfo;
                if (constructorInfo != null &&
                    constructorInfo.DeclaringType != null &&
                    constructorInfo.DeclaringType.IsGenericType &&
                    constructorInfo.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                    constructorInfo.DeclaringType.GetGenericArguments()[0] == targetType)
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, getReusableList);
                    replacementCount++;
                }
            }
        }

        // Safety check: TickEntities should have exactly 1 List allocation
        if (replacementCount != 1)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                $"[Tungsten] EntityListReuse: Expected 1 List allocation in TickEntities, found {replacementCount}. Optimization disabled for safety.");
            // Return original unmodified instructions
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        tickEntitiesPatched = true;
        foreach (var code in codes)
            yield return code;
    }

    // Clear the reusable lists before SendEntityDespawns runs
    // v1.10.0: Added capacity trimming to reduce memory footprint
    // v1.10.1: Removed redundant GetConfig() call - if patch exists, optimization is enabled
    public static void SendEntityDespawns_Prefix()
    {
        try
        {
            // v1.10.0: ThreadLocalHelper uses cached config (no parameters needed)
            ThreadLocalHelper.GetAndClearWithPeak(reusableEntityIdList, ref entityIdListPeak);
            ThreadLocalHelper.GetAndClearWithPeak(reusableDespawnReasonList, ref despawnReasonListPeak);
            ThreadLocalHelper.GetAndClearWithPeak(reusableDamageSourceList, ref damageSourceListPeak);
        }
        catch { }
    }

    // Replace the three list allocations in SendEntityDespawns using semantic analysis
    public static IEnumerable<CodeInstruction> SendEntityDespawns_Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        var getEntityIdList = AccessTools.Method(typeof(EntityListReuseOptimizer), nameof(GetReusableEntityIdList));
        var getDespawnReasonList = AccessTools.Method(typeof(EntityListReuseOptimizer), nameof(GetReusableDespawnReasonList));
        var getDamageSourceList = AccessTools.Method(typeof(EntityListReuseOptimizer), nameof(GetReusableDamageSourceList));

        // Step 1: Find all List allocations and track which local variable they're stored in
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

        // Step 2: Find SetEntityId, SetDespawnReason, SetDeathDamageSource calls
        // and trace back to find which local variable is used
        // Try multiple namespace patterns for robustness (game uses global namespace)
        var packetEntityDespawnType = AccessTools.TypeByName("Packet_EntityDespawn")
            ?? AccessTools.TypeByName("Vintagestory.API.Common.Packet_EntityDespawn")
            ?? AccessTools.TypeByName("Vintagestory.Common.Network.Packet_EntityDespawn");

        if (packetEntityDespawnType == null)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                "[Tungsten] EntityListReuse: Could not find Packet_EntityDespawn type. Optimization disabled.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        // Each setter has 2 overloads - specify parameter types to avoid ambiguous match
        // Game uses 1-parameter version (EntitySimulation.cs:454-456), not 3-parameter
        var setEntityIdMethod = AccessTools.Method(packetEntityDespawnType, "SetEntityId", new Type[] { typeof(long[]) });
        var setDespawnReasonMethod = AccessTools.Method(packetEntityDespawnType, "SetDespawnReason", new Type[] { typeof(int[]) });
        var setDeathDamageSourceMethod = AccessTools.Method(packetEntityDespawnType, "SetDeathDamageSource", new Type[] { typeof(int[]) });

        // Fallback to 3-parameter version if 1-parameter doesn't exist
        if (setEntityIdMethod == null)
            setEntityIdMethod = AccessTools.Method(packetEntityDespawnType, "SetEntityId", new Type[] { typeof(long[]), typeof(int), typeof(int) });
        if (setDespawnReasonMethod == null)
            setDespawnReasonMethod = AccessTools.Method(packetEntityDespawnType, "SetDespawnReason", new Type[] { typeof(int[]), typeof(int), typeof(int) });
        if (setDeathDamageSourceMethod == null)
            setDeathDamageSourceMethod = AccessTools.Method(packetEntityDespawnType, "SetDeathDamageSource", new Type[] { typeof(int[]), typeof(int), typeof(int) });

        if (setEntityIdMethod == null || setDespawnReasonMethod == null || setDeathDamageSourceMethod == null)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                $"[Tungsten] EntityListReuse: Could not find setter methods (SetEntityId={setEntityIdMethod != null}, SetDespawnReason={setDespawnReasonMethod != null}, SetDeathDamageSource={setDeathDamageSourceMethod != null}). Optimization disabled.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        int? entityIdListLocalVar = null;
        int? despawnReasonListLocalVar = null;
        int? damageSourceListLocalVar = null;

        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].opcode == OpCodes.Callvirt || codes[i].opcode == OpCodes.Call)
            {
                var method = codes[i].operand as MethodInfo;

                if (method == setEntityIdMethod)
                {
                    entityIdListLocalVar = FindLocalVarBeforeToArray(codes, i);
                }
                else if (method == setDespawnReasonMethod)
                {
                    despawnReasonListLocalVar = FindLocalVarBeforeToArray(codes, i);
                }
                else if (method == setDeathDamageSourceMethod)
                {
                    damageSourceListLocalVar = FindLocalVarBeforeToArray(codes, i);
                }
            }
        }

        // Step 3: Validate we found all three usages
        if (!entityIdListLocalVar.HasValue || !despawnReasonListLocalVar.HasValue || !damageSourceListLocalVar.HasValue)
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                $"[Tungsten] EntityListReuse: Could not trace list usage in SendEntityDespawns (found: EntityId={entityIdListLocalVar.HasValue}, DespawnReason={despawnReasonListLocalVar.HasValue}, DamageSource={damageSourceListLocalVar.HasValue}). Optimization disabled.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        // Step 4: Validate the local variables match our allocations
        if (!listAllocations.ContainsKey(entityIdListLocalVar.Value) ||
            !listAllocations.ContainsKey(despawnReasonListLocalVar.Value) ||
            !listAllocations.ContainsKey(damageSourceListLocalVar.Value))
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                "[Tungsten] EntityListReuse: List usage doesn't match allocations in SendEntityDespawns. Optimization disabled.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        // Step 5: Verify types match expectations
        var entityIdAlloc = listAllocations[entityIdListLocalVar.Value];
        var despawnReasonAlloc = listAllocations[despawnReasonListLocalVar.Value];
        var damageSourceAlloc = listAllocations[damageSourceListLocalVar.Value];

        if (entityIdAlloc.elementType != typeof(long) ||
            despawnReasonAlloc.elementType != typeof(int) ||
            damageSourceAlloc.elementType != typeof(int))
        {
            TungstenMod.Instance?.Api?.Logger?.Warning(
                "[Tungsten] EntityListReuse: List types don't match expectations in SendEntityDespawns. Optimization disabled.");
            foreach (var instruction in instructions)
                yield return instruction;
            yield break;
        }

        // Step 6: Replace allocations based on semantic usage (not declaration order!)
        codes[entityIdAlloc.allocIndex] = new CodeInstruction(OpCodes.Call, getEntityIdList);
        codes[despawnReasonAlloc.allocIndex] = new CodeInstruction(OpCodes.Call, getDespawnReasonList);
        codes[damageSourceAlloc.allocIndex] = new CodeInstruction(OpCodes.Call, getDamageSourceList);

        foreach (var code in codes)
            yield return code;
    }

    // Helper to find which local variable is loaded before ToArray() call
    // Pattern: ldloc.X -> callvirt ToArray -> callvirt SetXXX
    private static int? FindLocalVarBeforeToArray(List<CodeInstruction> codes, int setMethodIndex)
    {
        // Work backwards to find ToArray call
        for (int i = setMethodIndex - 1; i >= 0 && i > setMethodIndex - 10; i--)
        {
            if (codes[i].opcode == OpCodes.Callvirt)
            {
                var method = codes[i].operand as MethodInfo;
                if (method != null && method.Name == "ToArray")
                {
                    // Found ToArray, now look for ldloc before it
                    if (i > 0)
                    {
                        int localVar = GetLocalVariableIndex(codes[i - 1]);
                        if (localVar >= 0)
                            return localVar;
                    }
                }
            }
        }
        return null;
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

    public static List<KeyValuePair<Entity, EntityDespawnData>> GetReusableDespawnList()
    {
        var list = reusableDespawnList.Value;
        list.Clear();
        EnsureCapacity(list, despawnListPeak);
        return list;
    }

    public static List<long> GetReusableEntityIdList()
    {
        var list = reusableEntityIdList.Value;
        list.Clear();
        EnsureCapacity(list, entityIdListPeak);
        return list;
    }

    public static List<int> GetReusableDespawnReasonList()
    {
        var list = reusableDespawnReasonList.Value;
        list.Clear();
        EnsureCapacity(list, despawnReasonListPeak);
        return list;
    }

    public static List<int> GetReusableDamageSourceList()
    {
        var list = reusableDamageSourceList.Value;
        list.Clear();
        EnsureCapacity(list, damageSourceListPeak);
        return list;
    }

    private static void EnsureCapacity<T>(List<T> list, int peakCount)
    {
        if (peakCount > 0 && list.Capacity < peakCount)
            list.EnsureCapacity(peakCount);
    }
}
