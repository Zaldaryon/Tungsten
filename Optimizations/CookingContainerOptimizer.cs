using HarmonyLib;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Tungsten
{
    public class CookingContainerOptimizer
    {
        private readonly ICoreServerAPI api;
        private static readonly ThreadLocal<List<ItemStack>> reusableCookingStacksList = new(() => new List<ItemStack>());

        public CookingContainerOptimizer(ICoreServerAPI api)
        {
            this.api = api;

            // v1.10.0: Register ThreadLocal instances for proper disposal
            ThreadLocalRegistry.Register(reusableCookingStacksList);
        }

        /// <summary>
        /// Cleanup ThreadLocal registrations if initialization fails.
        /// </summary>
        public void CleanupOnFailure()
        {
            ThreadLocalRegistry.Unregister(reusableCookingStacksList);
        }

        public void ApplyPatches(Harmony harmony)
        {
            var blockCookingContainerType = AccessTools.TypeByName("Vintagestory.GameContent.BlockCookingContainer");
            if (blockCookingContainerType == null)
            {
                api.Logger.Warning("[Tungsten] [CookingContainerOptimizer] Could not find BlockCookingContainer type");
                return;
            }

            var getCookingStacksMethod = AccessTools.Method(blockCookingContainerType, "GetCookingStacks");
            if (getCookingStacksMethod != null)
            {
                var transpiler = AccessTools.Method(typeof(CookingContainerOptimizer), nameof(GetCookingStacks_Transpiler));
                harmony.Patch(getCookingStacksMethod, transpiler: new HarmonyMethod(transpiler));            }
            else
            {
                api.Logger.Warning("[Tungsten] [CookingContainerOptimizer] Could not find GetCookingStacks method");
            }
        }

        public static IEnumerable<CodeInstruction> GetCookingStacks_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getReusableList = AccessTools.Method(typeof(CookingContainerOptimizer), nameof(GetReusableCookingStacksList));
            int replaced = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is System.Reflection.ConstructorInfo ctor)
                {
                    if (ctor.DeclaringType != null && 
                        ctor.DeclaringType.IsGenericType && 
                        ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                        ctor.GetParameters().Length == 1 &&
                        ctor.GetParameters()[0].ParameterType == typeof(int))
                    {
                        codes[i] = new CodeInstruction(OpCodes.Call, getReusableList);
                        replaced++;
                        break;
                    }
                }
            }

            if (replaced != 1)
                TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] CookingContainerOptimizer: Expected 1 replacement, found {replaced}");

            foreach (var code in codes)
                yield return code;
        }

        public static List<ItemStack> GetReusableCookingStacksList(int capacity)
        {
            // v1.10.0: ThreadLocalHelper uses cached config (no GetConfig() call needed)
            var list = ThreadLocalHelper.GetAndClear(reusableCookingStacksList);
            if (capacity > 0 && list.Capacity < capacity)
                list.EnsureCapacity(capacity);
            return list;
        }
    }
}
