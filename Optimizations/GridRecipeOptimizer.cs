using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Tungsten
{
    /// <summary>
    /// Optimizes RecipeBase.MatchesShapeLess by reusing ThreadLocal lists instead of
    /// allocating new List&lt;ItemStack&gt; and List&lt;(ItemStack, IRecipeIngredient)&gt; per call.
    /// VS 1.22: MatchesShapeLess moved from GridRecipe to RecipeBase.
    /// </summary>
    public class GridRecipeOptimizer
    {
        private readonly ICoreServerAPI api;
        private static readonly ThreadLocal<List<ItemStack>> reusableItemStackList = new(() => new List<ItemStack>());
        private static readonly ThreadLocal<List<(ItemStack, IRecipeIngredient)>> reusableTupleList = new(() => new List<(ItemStack, IRecipeIngredient)>());

        public GridRecipeOptimizer(ICoreServerAPI api)
        {
            this.api = api;
            ThreadLocalRegistry.Register(reusableItemStackList);
            ThreadLocalRegistry.Register(reusableTupleList);
        }

        public void CleanupOnFailure()
        {
            ThreadLocalRegistry.UnregisterAll(reusableItemStackList, reusableTupleList);
        }

        public void ApplyPatches(Harmony harmony)
        {
            // VS 1.22: MatchesShapeLess is now on RecipeBase, not GridRecipe
            var recipeBaseType = AccessTools.TypeByName("Vintagestory.API.Common.RecipeBase");
            if (recipeBaseType == null)
            {
                api.Logger.Warning("[Tungsten] [GridRecipeOptimizer] Could not find RecipeBase type");
                return;
            }

            var method = AccessTools.Method(recipeBaseType, "MatchesShapeLess");
            if (method == null)
            {
                api.Logger.Warning("[Tungsten] [GridRecipeOptimizer] Could not find MatchesShapeLess method");
                return;
            }

            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(GridRecipeOptimizer), nameof(MatchesShapeLess_Prefix)),
                transpiler: new HarmonyMethod(typeof(GridRecipeOptimizer), nameof(MatchesShapeLess_Transpiler)));
        }

        public static void MatchesShapeLess_Prefix()
        {
            if (ThreadLocalHelper.IsDisposing) return;
            ThreadLocalHelper.GetAndClear(reusableItemStackList);
            ThreadLocalHelper.GetAndClear(reusableTupleList);
        }

        public static IEnumerable<CodeInstruction> MatchesShapeLess_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getItemStackList = AccessTools.Method(typeof(GridRecipeOptimizer), nameof(GetReusableItemStackList));
            var getTupleList = AccessTools.Method(typeof(GridRecipeOptimizer), nameof(GetReusableTupleList));
            int patchCount = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor &&
                    ctor.DeclaringType != null && ctor.DeclaringType.IsGenericType &&
                    ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                    ctor.GetParameters().Length == 0)
                {
                    var elementType = ctor.DeclaringType.GetGenericArguments()[0];

                    if (elementType == typeof(ItemStack) && patchCount == 0)
                    {
                        codes[i] = new CodeInstruction(OpCodes.Call, getItemStackList)
                        {
                            labels = codes[i].labels,
                            blocks = codes[i].blocks
                        };
                        patchCount++;
                    }
                    else if (patchCount == 1)
                    {
                        // Second list: List<(ItemStack, IRecipeIngredient)>
                        codes[i] = new CodeInstruction(OpCodes.Call, getTupleList)
                        {
                            labels = codes[i].labels,
                            blocks = codes[i].blocks
                        };
                        patchCount++;
                        break;
                    }
                }
            }

            if (patchCount != 2)
                TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] GridRecipeOptimizer: Expected 2 replacements, found {patchCount}");

            foreach (var code in codes)
                yield return code;
        }

        public static List<ItemStack> GetReusableItemStackList()
        {
            return ThreadLocalHelper.GetAndClear(reusableItemStackList);
        }

        public static List<(ItemStack, IRecipeIngredient)> GetReusableTupleList()
        {
            return ThreadLocalHelper.GetAndClear(reusableTupleList);
        }
    }
}
