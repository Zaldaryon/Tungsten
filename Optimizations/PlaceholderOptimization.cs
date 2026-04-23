using HarmonyLib;
using System;
using System.Text;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace Tungsten.Optimizations;

/// <summary>
/// Optimizes RegistryObject.FillPlaceHolder(string, OrderedDictionary{string,string})
/// by replacing per-key regex passes with a single placeholder scan.
/// </summary>
public static class PlaceholderOptimization
{
    private const string CircuitKey = "PlaceholderOptimization";
    private const int SelfCheckCallBudget = 128;

    private static ICoreServerAPI api;
    private static int selfCheckRemaining;
    private static volatile bool disabled;
    private static int disableLogGate;

    public static void Initialize(ICoreServerAPI serverApi, Harmony harmony)
    {
        api = serverApi;
        selfCheckRemaining = SelfCheckCallBudget;
        disabled = false;
        disableLogGate = 0;

        var registryObjectType = AccessTools.TypeByName("Vintagestory.API.Common.RegistryObject");
        if (registryObjectType == null)
        {
            api.Logger.Warning("[Tungsten] [PlaceholderOptimization] Could not find RegistryObject type");
            return;
        }

        var method = AccessTools.Method(
            registryObjectType,
            "FillPlaceHolder",
            new[] { typeof(string), typeof(OrderedDictionary<string, string>) }
        );

        if (method == null)
        {
            api.Logger.Warning("[Tungsten] [PlaceholderOptimization] Could not find FillPlaceHolder overload");
            return;
        }

        harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlaceholderOptimization), nameof(FillPlaceHolder_Prefix)));
    }

    public static bool FillPlaceHolder_Prefix(string input, OrderedDictionary<string, string> searchReplace, ref string __result)
    {
        if (!OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return true;

        if (disabled)
            return true;

        try
        {
            string optimized = FillPlaceHolderOptimized(input, searchReplace);

            if (Interlocked.Decrement(ref selfCheckRemaining) >= 0)
            {
                string baseline = FillPlaceHolderBaseline(input, searchReplace);
                if (!string.Equals(optimized, baseline, StringComparison.Ordinal))
                {
                    Disable("self-check mismatch");
                    return true;
                }
            }

            __result = optimized;
            return false;
        }
        catch
        {
            // Safety fallback to vanilla implementation
            Disable("runtime exception");
            return true;
        }
    }

    private static string FillPlaceHolderOptimized(string input, OrderedDictionary<string, string> searchReplace)
    {
        if (string.IsNullOrEmpty(input) || searchReplace == null || searchReplace.Count == 0)
            return input;

        if (input.IndexOf('{') < 0)
            return input;

        StringBuilder sb = null;
        int cursor = 0;
        int scan = 0;

        while (scan < input.Length)
        {
            int openPos = input.IndexOf('{', scan);
            if (openPos < 0)
                break;

            int closePos = input.IndexOf('}', openPos + 1);
            if (closePos < 0)
                break;

            if (TryResolvePlaceholder(input.AsSpan(openPos + 1, closePos - openPos - 1), searchReplace, out string value))
            {
                sb ??= new StringBuilder(input.Length);

                if (openPos > cursor)
                    sb.Append(input, cursor, openPos - cursor);

                sb.Append(value);
                cursor = closePos + 1;
            }
            else if (sb != null)
            {
                sb.Append(input, cursor, closePos + 1 - cursor);
                cursor = closePos + 1;
            }

            scan = closePos + 1;
        }

        if (sb == null)
            return input;

        if (cursor < input.Length)
            sb.Append(input, cursor, input.Length - cursor);

        return sb.ToString();
    }

    private static bool TryResolvePlaceholder(
        ReadOnlySpan<char> placeholder,
        OrderedDictionary<string, string> searchReplace,
        out string value
    )
    {
        foreach (var kvp in searchReplace)
        {
            ReadOnlySpan<char> key = kvp.Key.AsSpan();
            int partStart = 0;

            for (int i = 0; i <= placeholder.Length; i++)
            {
                if (i < placeholder.Length && placeholder[i] != '|')
                    continue;

                ReadOnlySpan<char> part = placeholder.Slice(partStart, i - partStart);
                if (part.SequenceEqual(key))
                {
                    value = kvp.Value;
                    return true;
                }

                partStart = i + 1;
            }
        }

        value = null;
        return false;
    }

    private static string FillPlaceHolderBaseline(string input, OrderedDictionary<string, string> searchReplace)
    {
        if (searchReplace == null)
            return input;

        foreach (var val in searchReplace)
        {
            input = RegistryObject.FillPlaceHolder(input, val.Key, val.Value);
        }

        return input;
    }

    private static void Disable(string reason)
    {
        disabled = true;
        OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);
        if (Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
        {
            api?.Logger?.Warning("[Tungsten] [PlaceholderOptimization] Disabled and falling back to vanilla: " + reason);
        }
    }
}
