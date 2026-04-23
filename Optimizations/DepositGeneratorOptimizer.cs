using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Threading;
using System.Runtime.CompilerServices;
using Vintagestory.API.Server;

namespace Tungsten
{
    public class DepositGeneratorOptimizer
    {
        private readonly ICoreServerAPI api;
        private static readonly ThreadLocal<HashSet<int>> reusableOreBearingBlocks = new(() => new HashSet<int>());
        private static readonly ConditionalWeakTable<object, BearingBlocksCache> bearingBlocksCache = new ConditionalWeakTable<object, BearingBlocksCache>();
        private static readonly Dictionary<System.Type, System.Reflection.MethodInfo> bearingBlocksMethodCache = new Dictionary<System.Type, System.Reflection.MethodInfo>();
        private static readonly object cacheLock = new object();

        private class BearingBlocksCache
        {
            public int[] Blocks;
            public int SourceCount;
        }

        public DepositGeneratorOptimizer(ICoreServerAPI api)
        {
            this.api = api;

            // v1.10.0: Register ThreadLocal instances for proper disposal
            ThreadLocalRegistry.Register(reusableOreBearingBlocks);
        }

        /// <summary>
        /// Cleanup ThreadLocal registrations if initialization fails.
        /// </summary>
        public void CleanupOnFailure()
        {
            ThreadLocalRegistry.Unregister(reusableOreBearingBlocks);
        }

        public void ApplyPatches(Harmony harmony)
        {
            var discGeneratorType = AccessTools.TypeByName("Vintagestory.ServerMods.DiscDepositGenerator");
            if (discGeneratorType == null)
            {
                api.Logger.Warning("[Tungsten] [DepositGeneratorOptimizer] Could not find DiscDepositGenerator type");
                return;
            }

            var oreBearingMethod = AccessTools.Method(discGeneratorType, "oreBearingBlockQuantityRelative");
            if (oreBearingMethod != null)
            {
                var transpiler = AccessTools.Method(typeof(DepositGeneratorOptimizer), nameof(OreBearingBlockQuantityRelative_Transpiler));
                harmony.Patch(oreBearingMethod, transpiler: new HarmonyMethod(transpiler));            }
            else
            {
                api.Logger.Warning("[Tungsten] [DepositGeneratorOptimizer] Could not find oreBearingBlockQuantityRelative method");
            }
        }

        public static IEnumerable<CodeInstruction> OreBearingBlockQuantityRelative_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getReusableHashSet = AccessTools.Method(typeof(DepositGeneratorOptimizer), nameof(GetReusableOreBearingBlocks));
            var getBearingBlocksCached = AccessTools.Method(typeof(DepositGeneratorOptimizer), nameof(GetBearingBlocksCached));
            var targetMethod = AccessTools.Method(AccessTools.TypeByName("Vintagestory.ServerMods.DiscDepositGenerator"), "GetBearingBlocks");

            for (int i = 0; i < codes.Count; i++)
            {
                if (targetMethod != null &&
                    (codes[i].opcode == OpCodes.Callvirt || codes[i].opcode == OpCodes.Call) &&
                    codes[i].operand is System.Reflection.MethodInfo callMethod &&
                    callMethod == targetMethod)
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, getBearingBlocksCached);
                    continue;
                }

                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is System.Reflection.ConstructorInfo ctor)
                {
                    if (ctor.DeclaringType == typeof(HashSet<int>) && ctor.GetParameters().Length == 0)
                    {
                        var newInstruction = new CodeInstruction(OpCodes.Call, getReusableHashSet);
                        newInstruction.labels = codes[i].labels;
                        newInstruction.blocks = codes[i].blocks;
                        codes[i] = newInstruction;
                        break;
                    }
                }
            }

            foreach (var code in codes)
                yield return code;
        }

        public static HashSet<int> GetReusableOreBearingBlocks()
        {
            // v1.10.0: ThreadLocalHelper uses cached config (no GetConfig() call needed)
            return ThreadLocalHelper.GetAndClear(reusableOreBearingBlocks);
        }

        public static int[] GetBearingBlocksCached(object instance)
        {
            if (instance == null)
                return null;

            var cache = bearingBlocksCache.GetValue(instance, _ => new BearingBlocksCache());
            int currentCount = -1;

            var instanceType = instance.GetType();
            var placeBlocksField = AccessTools.Field(instanceType, "placeBlockByInBlockId");
            if (placeBlocksField != null)
            {
                var dict = placeBlocksField.GetValue(instance);
                if (dict != null)
                {
                    var countProp = dict.GetType().GetProperty("Count");
                    if (countProp != null)
                    {
                        currentCount = (int)countProp.GetValue(dict);
                    }
                }
            }

            if (cache.Blocks != null && (currentCount < 0 || cache.SourceCount == currentCount))
                return cache.Blocks;

            System.Reflection.MethodInfo getBearingBlocks;
            lock (cacheLock)
            {
                if (!bearingBlocksMethodCache.TryGetValue(instanceType, out getBearingBlocks))
                {
                    getBearingBlocks = AccessTools.Method(instanceType, "GetBearingBlocks");
                    bearingBlocksMethodCache[instanceType] = getBearingBlocks;
                }
            }

            if (getBearingBlocks == null)
                return null;

            var blocks = getBearingBlocks.Invoke(instance, null) as int[];
            cache.Blocks = blocks;
            cache.SourceCount = currentCount;
            return blocks;
        }

        public static void Dispose()
        {
            ThreadLocalRegistry.Unregister(reusableOreBearingBlocks);
        }
    }
}
