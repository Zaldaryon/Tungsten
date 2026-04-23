using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Tungsten
{
    public class ContainerOptimizer
    {
        private readonly ICoreServerAPI api;
        private static readonly ThreadLocal<List<ItemStack>> reusableNonEmptyList = new(() => new List<ItemStack>());

        public ContainerOptimizer(ICoreServerAPI api)
        {
            this.api = api;

            // v1.10.0: Register ThreadLocal instances for proper disposal
            ThreadLocalRegistry.Register(reusableNonEmptyList);
        }

        /// <summary>
        /// Cleanup ThreadLocal registrations if initialization fails.
        /// </summary>
        public void CleanupOnFailure()
        {
            ThreadLocalRegistry.Unregister(reusableNonEmptyList);
        }

        public void ApplyPatches(Harmony harmony)
        {
            var beContainerType = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityContainer");
            if (beContainerType == null)
            {
                api.Logger.Warning("[Tungsten] [ContainerOptimizer] Could not find BlockEntityContainer type");
                return;
            }

            var getNonEmptyMethod = AccessTools.Method(beContainerType, "GetNonEmptyContentStacks");
            if (getNonEmptyMethod != null)
            {
                var transpiler = AccessTools.Method(typeof(ContainerOptimizer), nameof(GetNonEmptyContentStacks_Transpiler));
                harmony.Patch(getNonEmptyMethod, transpiler: new HarmonyMethod(transpiler));            }

            // v1.0.5: Removed GetContentStacks optimization - causes conflicts with mods that hold references during consumption
        }

        public static IEnumerable<CodeInstruction> GetNonEmptyContentStacks_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getReusableList = AccessTools.Method(typeof(ContainerOptimizer), nameof(GetReusableNonEmptyList));
            int replaced = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is System.Reflection.ConstructorInfo ctor)
                {
                    if (ctor.DeclaringType != null && 
                        ctor.DeclaringType.IsGenericType && 
                        ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                        ctor.GetParameters().Length == 0)
                    {
                        codes[i] = new CodeInstruction(OpCodes.Call, getReusableList);
                        replaced++;
                        break;
                    }
                }
            }

            if (replaced != 1)
                TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] ContainerOptimizer: Expected 1 replacement, found {replaced}");

            foreach (var code in codes)
                yield return code;
        }

        public static List<ItemStack> GetReusableNonEmptyList()
        {
            return ThreadLocalHelper.GetAndClear(reusableNonEmptyList);
        }
    }
}
