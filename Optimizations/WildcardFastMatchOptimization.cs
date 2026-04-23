using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten.Optimizations;

/// <summary>
/// Optimizes WildcardUtil.fastMatch(string, string) for '@' regex patterns only.
/// VS 1.22: vanilla now uses the same iterative wildcard algorithm for '*' patterns
/// (with a faster SameCharIgnoreCase), so we only intercept '@' patterns where our
/// compiled regex cache provides a meaningful speedup over vanilla's uncompiled cache.
/// </summary>
public static class WildcardFastMatchOptimization
{
    private const string CircuitKey = "WildcardFastMatchOptimization";
    private const int MaxRegexCacheEntries = 2048;
    private const float CacheRetentionRatio = 0.75f;

    private sealed class RegexCacheEntry
    {
        public Regex Regex;
        public int AccessGeneration;

        public RegexCacheEntry(Regex regex, int generation)
        {
            Regex = regex;
            AccessGeneration = generation;
        }
    }

    private static readonly ConcurrentDictionary<string, RegexCacheEntry> regexCache = new(StringComparer.Ordinal);
    private static int regexCacheTrimGate;
    private static int regexCacheGeneration;
    private static volatile bool disabled;
    private static int disableLogGate;
    private static ICoreServerAPI api;

    public static void Initialize(ICoreServerAPI serverApi, Harmony harmony)
    {
        api = serverApi;
        disabled = false;
        disableLogGate = 0;

        var wildcardType = AccessTools.TypeByName("Vintagestory.API.Util.WildcardUtil");
        if (wildcardType == null)
        {
            api.Logger.Warning("[Tungsten] [WildcardFastMatchOptimization] Could not find WildcardUtil type");
            return;
        }

        var method = AccessTools.Method(wildcardType, "fastMatch", new[] { typeof(string), typeof(string) });
        if (method == null)
        {
            api.Logger.Warning("[Tungsten] [WildcardFastMatchOptimization] Could not find fastMatch(string,string)");
            return;
        }

        harmony.Patch(method, prefix: new HarmonyMethod(typeof(WildcardFastMatchOptimization), nameof(FastMatch_Prefix)));
    }

    public static bool FastMatch_Prefix(string needle, string haystack, ref bool __result)
    {
        if (!OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
            return true;

        if (disabled)
            return true;

        // Only intercept @ patterns — vanilla 1.22 handles * patterns optimally
        if (string.IsNullOrEmpty(needle) || needle[0] != '@')
            return true;

        try
        {
            var regex = GetCachedRegex(needle.Substring(1));
            if (regex == null)
                return true; // fallback to vanilla on invalid pattern

            __result = regex.IsMatch(haystack);
            return false;
        }
        catch
        {
            Disable("runtime exception");
            return true;
        }
    }

    public static void Dispose()
    {
        disabled = true;
        regexCache.Clear();
    }

    private static Regex GetCachedRegex(string pattern)
    {
        if (regexCache.Count > MaxRegexCacheEntries)
            TrimRegexCache();

        int generation = Interlocked.Increment(ref regexCacheGeneration);

        if (regexCache.TryGetValue(pattern, out var existing))
        {
            existing.AccessGeneration = generation;
            return existing.Regex;
        }

        Regex compiledRegex;
        try
        {
            compiledRegex = new Regex("^" + pattern + "$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            Disable("invalid regex pattern");
            return null;
        }

        var candidate = new RegexCacheEntry(compiledRegex, generation);
        var canonical = regexCache.GetOrAdd(pattern, candidate);
        if (!ReferenceEquals(canonical, candidate))
        {
            canonical.AccessGeneration = generation;
            return canonical.Regex;
        }

        return compiledRegex;
    }

    private static void TrimRegexCache()
    {
        if (Interlocked.CompareExchange(ref regexCacheTrimGate, 1, 0) != 0)
            return;

        try
        {
            int count = regexCache.Count;
            if (count > MaxRegexCacheEntries)
            {
                int retainCount = Math.Max(1, (int)(MaxRegexCacheEntries * CacheRetentionRatio));
                int removeCount = count - retainCount;
                if (removeCount <= 0) return;

                var keysToRemove = regexCache
                    .Select(kvp => new { kvp.Key, kvp.Value.AccessGeneration })
                    .OrderBy(entry => entry.AccessGeneration)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Take(removeCount)
                    .Select(entry => entry.Key)
                    .ToArray();

                foreach (var key in keysToRemove)
                    regexCache.TryRemove(key, out _);
            }
        }
        finally
        {
            Volatile.Write(ref regexCacheTrimGate, 0);
        }
    }

    private static void Disable(string reason)
    {
        disabled = true;
        OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);
        if (Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
            api?.Logger?.Warning("[Tungsten] [WildcardFastMatchOptimization] Disabled and falling back to vanilla: " + reason);
    }
}
