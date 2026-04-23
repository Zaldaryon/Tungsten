using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Tungsten
{
    public class ChunkLoadingOptimizer
    {
        private readonly ICoreServerAPI api;

        private static readonly ThreadLocal<List<long>> reusableRequestList = new(() => new List<long>(200));
        public ChunkLoadingOptimizer(ICoreServerAPI api)
        {
            this.api = api;

            ThreadLocalRegistry.Register(reusableRequestList);
        }

        public void CleanupOnFailure()
        {
            ThreadLocalRegistry.Unregister(reusableRequestList);
        }

        public void ApplyPatches(Harmony harmony)
        {
            var supplyChunksType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemSupplyChunks");
            if (supplyChunksType == null)
            {
                api.Logger.Warning("[Tungsten] [ChunkLoadingOptimizer] Could not find ServerSystemSupplyChunks type");
                return;
            }

            var moveRequestsMethod = AccessTools.Method(supplyChunksType, "moveRequestsToGeneratingQueue");
            if (moveRequestsMethod != null)
            {
                harmony.Patch(moveRequestsMethod, 
                    transpiler: new HarmonyMethod(typeof(ChunkLoadingOptimizer), nameof(MoveRequests_Transpiler)));
            }

            var deleteChunksMethod = AccessTools.Method(supplyChunksType, "deleteChunks");
            if (deleteChunksMethod != null)
            {
                harmony.Patch(deleteChunksMethod,
                    transpiler: new HarmonyMethod(typeof(ChunkLoadingOptimizer), nameof(DeleteChunks_Transpiler)));
            }
        }

        public static IEnumerable<CodeInstruction> MoveRequests_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var listLongType = typeof(List<long>);
            int allocCount = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor)
                {
                    if (ctor.DeclaringType == listLongType && ctor.GetParameters().Length == 0)
                        allocCount++;
                }
            }

            if (allocCount != 1)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning(
                    $"[Tungsten] [ChunkLoadingOptimizer] moveRequestsToGeneratingQueue: Expected 1 List<long> allocation, found {allocCount}. Optimization disabled.");
                return instructions;
            }

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor)
                {
                    if (ctor.DeclaringType == listLongType && ctor.GetParameters().Length == 0)
                    {
                        codes[i] = new CodeInstruction(OpCodes.Call,
                            AccessTools.Method(typeof(ChunkLoadingOptimizer), nameof(GetReusableRequestList)));
                        break;
                    }
                }
            }

            return codes;
        }

        public static IEnumerable<CodeInstruction> DeleteChunks_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int listAllocCount = 0;
            int setAllocCount = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor)
                {
                    if (ctor.DeclaringType != null &&
                        ctor.DeclaringType.IsGenericType &&
                        ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                        ctor.GetParameters().Length == 0)
                    {
                        listAllocCount++;
                    }
                    else if (ctor.DeclaringType != null &&
                             ctor.DeclaringType.IsGenericType &&
                             ctor.DeclaringType.GetGenericTypeDefinition() == typeof(HashSet<>) &&
                             ctor.GetParameters().Length == 0)
                    {
                        setAllocCount++;
                    }
                }
            }

            if (listAllocCount != 2 || setAllocCount != 1)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning(
                    $"[Tungsten] [ChunkLoadingOptimizer] deleteChunks: Expected 2 List<> and 1 HashSet<> allocations, found {listAllocCount} and {setAllocCount}. Optimization disabled.");
                return instructions;
            }

            int listSlot = 0;
            int setSlot = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor)
                {
                    if (ctor.DeclaringType != null &&
                        ctor.DeclaringType.IsGenericType &&
                        ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                        ctor.GetParameters().Length == 0)
                    {
                        ReplaceNewobjWithReusableList(codes, i, ctor.DeclaringType, listSlot++);
                    }
                    else if (ctor.DeclaringType != null &&
                             ctor.DeclaringType.IsGenericType &&
                             ctor.DeclaringType.GetGenericTypeDefinition() == typeof(HashSet<>) &&
                             ctor.GetParameters().Length == 0)
                    {
                        ReplaceNewobjWithReusableHashSet(codes, i, ctor.DeclaringType, setSlot++);
                    }
                }
            }

            return codes;
        }

        public static List<long> GetReusableRequestList()
        {
            return ThreadLocalHelper.GetAndClear(reusableRequestList);
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

        private static void ReplaceNewobjWithReusableHashSet(List<CodeInstruction> codes, int index, Type setType, int slot)
        {
            var getTypeFromHandle = AccessTools.Method(typeof(Type), nameof(Type.GetTypeFromHandle));
            var getSet = AccessTools.Method(typeof(ReusableCollectionPool), nameof(ReusableCollectionPool.GetHashSet));

            var original = codes[index];
            var inst = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldtoken, setType),
                new CodeInstruction(OpCodes.Call, getTypeFromHandle),
                new CodeInstruction(OpCodes.Ldc_I4, slot),
                new CodeInstruction(OpCodes.Call, getSet),
                new CodeInstruction(OpCodes.Castclass, setType)
            };

            inst[0].labels.AddRange(original.labels);
            inst[0].blocks.AddRange(original.blocks);
            codes[index] = inst[0];
            codes.InsertRange(index + 1, inst.GetRange(1, inst.Count - 1));
        }
    }
}
