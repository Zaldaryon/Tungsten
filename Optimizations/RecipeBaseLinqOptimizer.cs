using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Tungsten
{
    /// <summary>
    /// Eliminates LINQ iterator allocations in RecipeBase.MergeStacks (2 iterators/call)
    /// and RecipeBase.MatchWildcardIngredients (3 iterators/call).
    /// ~25-750 allocs/sec eliminated during active crafting.
    /// Complements GridRecipeOptimizer which handles list allocations in MatchesShapeLess.
    /// </summary>
    public static class RecipeBaseLinqOptimizer
    {
        private const string CircuitKey = "RecipeBaseLinqOptimization";
        private static volatile bool disabled;
        private static int disableLogGate;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            var recipeBaseType = AccessTools.TypeByName("Vintagestory.API.Common.RecipeBase");
            if (recipeBaseType == null)
            {
                api.Logger.Warning("[Tungsten] [RecipeBaseLinqOptimization] Could not find RecipeBase type");
                return;
            }

            var mergeStacks = AccessTools.Method(recipeBaseType, "MergeStacks");
            if (mergeStacks != null)
            {
                harmony.Patch(mergeStacks, prefix: new HarmonyMethod(typeof(RecipeBaseLinqOptimizer), nameof(MergeStacks_Prefix)));
            }
            else
            {
                api.Logger.Warning("[Tungsten] [RecipeBaseLinqOptimization] Could not find MergeStacks method");
            }

            var matchWildcard = AccessTools.Method(recipeBaseType, "MatchWildcardIngredients");
            if (matchWildcard != null)
            {
                harmony.Patch(matchWildcard, prefix: new HarmonyMethod(typeof(RecipeBaseLinqOptimizer), nameof(MatchWildcardIngredients_Prefix)));
            }
            else
            {
                api.Logger.Warning("[Tungsten] [RecipeBaseLinqOptimization] Could not find MatchWildcardIngredients method");
            }
        }

        /// <summary>
        /// Replaces slots.Select(slot => slot.Itemstack).OfType&lt;ItemStack&gt;() with a direct for-loop.
        /// </summary>
        public static bool MergeStacks_Prefix(ItemSlot[] slots, List<ItemStack> stacks)
        {
            if (disabled || !OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
                return true;

            try
            {
                TungstenProfiler.Mark("tungsten-mergestacks");
                for (int i = 0; i < slots.Length; i++)
                {
                    ItemStack suppliedStack = slots[i].Itemstack;
                    if (suppliedStack == null) continue;

                    ItemStack similarStack = null;
                    for (int j = 0; j < stacks.Count; j++)
                    {
                        if (stacks[j].Satisfies(suppliedStack))
                        {
                            similarStack = stacks[j];
                            break;
                        }
                    }

                    if (similarStack != null)
                    {
                        similarStack.StackSize += suppliedStack.StackSize;
                    }
                    else
                    {
                        stacks.Add(suppliedStack.Clone());
                    }
                }

                return false;
            }
            catch (Exception)
            {
                Disable("MergeStacks runtime exception");
                return true;
            }
        }

        /// <summary>
        /// Replaces ingredients.OfType&lt;IRecipeIngredient&gt;().Where(...) with a direct for-loop.
        /// </summary>
        public static bool MatchWildcardIngredients_Prefix(RecipeBase __instance, List<ItemStack> suppliedStacks, IRecipeIngredient[] ingredients, ref bool __result)
        {
            if (disabled || !OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
                return true;

            try
            {
                TungstenProfiler.Mark("tungsten-matchwildcard");
                for (int i = 0; i < ingredients.Length; i++)
                {
                    var ingredient = ingredients[i];
                    if (ingredient == null) continue;
                    if (ingredient.MatchingType == EnumRecipeMatchType.Exact) continue;

                    bool found = false;
                    int matchIndex = -1;

                    for (int j = 0; j < suppliedStacks.Count; j++)
                    {
                        ItemStack inputStack = suppliedStacks[j];

                        if (ingredient.Type == inputStack.Class &&
                            WildcardUtil.Match(ingredient.Code, inputStack.Collectible.Code, ingredient.AllowedVariants) &&
                            inputStack.StackSize >= ingredient.Quantity &&
                            ingredient.Tags.Matches(in inputStack.Collectible.Tags) &&
                            inputStack.Collectible.MatchesForCrafting(inputStack, __instance, ingredient))
                        {
                            found = true;
                            matchIndex = j;
                            break;
                        }
                    }

                    if (!found)
                    {
                        __result = false;
                        return false;
                    }

                    suppliedStacks.RemoveAt(matchIndex);
                }

                __result = true;
                return false;
            }
            catch (Exception)
            {
                Disable("MatchWildcardIngredients runtime exception");
                return true;
            }
        }

        private static void Disable(string reason)
        {
            disabled = true;
            OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);
            if (Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
                TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] [RecipeBaseLinqOptimization] Disabled: {reason}");
        }

        public static void Dispose()
        {
            disabled = false;
            disableLogGate = 0;
        }
    }
}
