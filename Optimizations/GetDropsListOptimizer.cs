using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Tungsten
{
    public class GetDropsListOptimizer
    {
        private const string CircuitKey = "GetDropsListOptimization";
        private readonly ICoreServerAPI api;
        private static ICoreServerAPI staticApi;

        private static readonly ThreadLocal<List<ItemStack>> reusableCollectionList = new(() => new List<ItemStack>());

        private static readonly ThreadLocal<List<ItemStack>> reusableItemStackList = new(() => new List<ItemStack>());
        private static int disableLogGate;
        private static int patchFailureCount;

        public GetDropsListOptimizer(ICoreServerAPI api)
        {
            this.api = api;
            staticApi = api;

            // v1.10.0: Register ThreadLocal instances for proper disposal
            ThreadLocalRegistry.Register(reusableCollectionList);
            ThreadLocalRegistry.Register(reusableItemStackList);
        }

        /// <summary>
        /// Cleanup ThreadLocal registrations if initialization fails.
        /// </summary>
        public void CleanupOnFailure()
        {
            try
            {
                api?.Logger?.Debug("[Tungsten] [GetDropsListOptimization] CleanupOnFailure: clearing reusable list state");
            }
            finally
            {
                ThreadLocalRegistry.UnregisterAll(
                    reusableCollectionList,
                    reusableItemStackList
                );
            }
        }

        public static int GetPatchFailureCount()
        {
            return patchFailureCount;
        }

        public static string GetRuntimeStatus()
        {
            if (!OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
                return "disabled by circuit-breaker";

            return "active";
        }

        public static void ApplyPatches(Harmony harmony)
        {
            if (!OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
                return;

            var blockType = typeof(Block);
            var getDropsMethod = AccessTools.Method(blockType, "GetDrops", new[]
            {
                typeof(IWorldAccessor),
                typeof(BlockPos),
                typeof(IPlayer),
                typeof(float)
            });

            if (getDropsMethod == null)
            {
                Disable("GetDrops method not found");
                return;
            }

            try
            {
                harmony.Patch(getDropsMethod, transpiler: new HarmonyMethod(typeof(GetDropsListOptimizer), nameof(Transpiler_ReuseLists)));
            }
            catch (Exception ex)
            {
                Disable($"Patch registration failed: {ex.GetType().Name} {ex.Message}");
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler_ReuseLists(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var listCtor = AccessTools.Constructor(typeof(List<ItemStack>), Type.EmptyTypes);
            if (listCtor == null)
            {
                Disable("List<ItemStack> constructor not found");
                return instructions;
            }

            var getCollectionList = AccessTools.Method(typeof(GetDropsListOptimizer), nameof(GetCollectionList));
            var getItemStackList = AccessTools.Method(typeof(GetDropsListOptimizer), nameof(GetItemStackList));

            int patchCount = 0;
            int expectedPatches = 2;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is System.Reflection.ConstructorInfo ctor && ctor == listCtor)
                {
                    var newInstruction = new CodeInstruction(OpCodes.Call, patchCount == 0 ? getCollectionList : getItemStackList);
                    newInstruction.labels = codes[i].labels;
                    newInstruction.blocks = codes[i].blocks;
                    codes[i] = newInstruction;
                    patchCount++;

                    if (patchCount >= 2)
                        break;
                }
            }

            if (patchCount != expectedPatches)
            {
                Disable($"Expected {expectedPatches} List<ItemStack> allocations, found {patchCount}");
                return instructions;
            }

            return codes;
        }

        private static void Disable(string reason)
        {
            Interlocked.Increment(ref patchFailureCount);
            OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);
            if (System.Threading.Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
            {
                staticApi?.Logger?.Warning("[Tungsten] [GetDropsListOptimization] Disabled and falling back to vanilla: " + reason);
            }
        }


        public static List<ItemStack> GetCollectionList()
        {
            return ThreadLocalHelper.GetAndClear(reusableCollectionList);
        }

        public static List<ItemStack> GetItemStackList()
        {
            return ThreadLocalHelper.GetAndClear(reusableItemStackList);
        }
    }
}
