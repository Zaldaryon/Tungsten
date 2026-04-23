using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.MathTools;

namespace Tungsten
{
    public class EventManagerListOptimizer
    {
        private const string CircuitKey = "EventManagerListOptimization";

        private static readonly ThreadLocal<List<BlockPos>> _reusableBlockPosList = new(() => new List<BlockPos>());
        private static volatile bool disabled;
        private static int disableLogGate;
        private static int patchFailureCount;

        // v1.10.0: Static constructor to register ThreadLocal instances
        static EventManagerListOptimizer()
        {
            ThreadLocalRegistry.Register(_reusableBlockPosList);
        }

        /// <summary>
        /// Cleanup ThreadLocal registrations if initialization fails.
        /// </summary>
        public static void CleanupOnFailure()
        {
            ThreadLocalRegistry.Unregister(_reusableBlockPosList);
        }

        public static void ApplyPatches(Harmony harmony)
        {
            var eventManagerType = AccessTools.TypeByName("Vintagestory.Common.EventManager");
            if (eventManagerType == null)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning("[Tungsten] EventManagerListOptimizer: Could not find EventManager type");
                return;
            }

            var triggerGameTickMethod = AccessTools.Method(
                eventManagerType,
                "TriggerGameTick",
                new[] { typeof(long), typeof(Vintagestory.API.Common.IWorldAccessor) }
            );

            var triggerGameTickDebugMethod = AccessTools.Method(
                eventManagerType,
                "TriggerGameTickDebug",
                new[] { typeof(long), typeof(Vintagestory.API.Common.IWorldAccessor) }
            );

            if (triggerGameTickMethod == null || triggerGameTickDebugMethod == null)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning("[Tungsten] EventManagerListOptimizer: Required methods not found on EventManager");
                return;
            }

            if (triggerGameTickMethod.ReturnType != typeof(void) || triggerGameTickDebugMethod.ReturnType != typeof(void))
            {
                Disable("TriggerGameTick signature changed: expected void return type");
                return;
            }

            var transpilerMethod = AccessTools.Method(typeof(EventManagerListOptimizer), nameof(Transpiler));
            harmony.Patch(triggerGameTickMethod, transpiler: new HarmonyMethod(transpilerMethod));
            harmony.Patch(triggerGameTickDebugMethod, transpiler: new HarmonyMethod(transpilerMethod));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (disabled)
                return instructions;

            var codes = new List<CodeInstruction>(instructions);
            var listBlockPosType = typeof(List<BlockPos>);
            var listConstructor = AccessTools.Constructor(listBlockPosType, new[] { typeof(IEnumerable<BlockPos>) });
            if (listConstructor == null)
            {
                Disable("Could not find List<BlockPos> ctor(IEnumerable<BlockPos>)");
                return instructions;
            }

            int candidateReplacements = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor &&
                    ctor == listConstructor)
                {
                    candidateReplacements++;
                }
            }

            if (candidateReplacements != 1)
            {
                Disable($"Expected 1 List<BlockPos> constructor call, found {candidateReplacements}");
                return instructions;
            }

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor &&
                    ctor == listConstructor)
                {
                    // Replace with call to GetReusableList
                    codes[i] = new CodeInstruction(
                        OpCodes.Call,
                        AccessTools.Method(typeof(EventManagerListOptimizer), nameof(GetReusableList))
                    );
                    break;
                }
            }

            return codes;
        }

        public static List<BlockPos> GetReusableList(IEnumerable<BlockPos> source)
        {
            // v1.10.0: ThreadLocalHelper uses cached config (no GetConfig() call needed)
            var list = ThreadLocalHelper.GetAndClear(_reusableBlockPosList);

            list.AddRange(source);
            return list;
        }

        public static void Dispose()
        {
            disabled = false;
            patchFailureCount = 0;
            ThreadLocalRegistry.Unregister(_reusableBlockPosList);
            
            // v1.10.3: Clear list to prevent memory leak on reload
            if (_reusableBlockPosList.IsValueCreated)
            {
                _reusableBlockPosList.Value.Clear();
                _reusableBlockPosList.Value.TrimExcess();
            }
        }

        public static string GetRuntimeStatus()
        {
            if (disabled)
                return "degraded";

            if (!OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
                return "disabled by circuit-breaker";

            return "active";
        }

        public static int GetFailureCount()
        {
            return patchFailureCount;
        }

        private static void Disable(string reason)
        {
            disabled = true;
            patchFailureCount++;
            OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);
            if (Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning("[Tungsten] EventManagerListOptimizer: Disabled and falling back to vanilla: " + reason);
            }
        }
    }
}
