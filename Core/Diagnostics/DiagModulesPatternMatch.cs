using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten.Diagnostics
{
    public sealed class DiagPlaceholder : IDiagModule
    {
        public string ShortName => "placeholder";
        public string DisplayName => "Placeholder Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long callsIntercepted;
        static long placeholdersResolved;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref callsIntercepted, 0); Interlocked.Exchange(ref placeholdersResolved, 0); startTick = Environment.TickCount64; }

        public static void OnIntercept() { if (enabled) Interlocked.Increment(ref callsIntercepted); }
        public static void OnPlaceholderResolved() { if (enabled) Interlocked.Increment(ref placeholdersResolved); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var calls = Interlocked.Read(ref callsIntercepted);
            var resolved = Interlocked.Read(ref placeholdersResolved);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"callsIntercepted={calls} placeholdersResolved={resolved}");
            DiagLog.Line(api, caller, $"rate={calls / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | avg={resolved / Math.Max(calls, 1):F1} placeholders/call");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagWildcardFastMatch : IDiagModule
    {
        public string ShortName => "wildcardfastmatch";
        public string DisplayName => "Wildcard FastMatch Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long lookups;
        static long cacheHits;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref lookups, 0); Interlocked.Exchange(ref cacheHits, 0); startTick = Environment.TickCount64; }

        public static void OnLookup() { if (enabled) Interlocked.Increment(ref lookups); }
        public static void OnCacheHit() { if (enabled) Interlocked.Increment(ref cacheHits); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var l = Interlocked.Read(ref lookups);
            var h = Interlocked.Read(ref cacheHits);
            var hitRate = l > 0 ? (double)h / l * 100.0 : 0;
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"lookups={l} cacheHits={h} hitRate={hitRate:F1}%");
            DiagLog.Line(api, caller, $"rate={l / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagGetEntitiesAround : IDiagModule
    {
        public string ShortName => "getentitiesaround";
        public string DisplayName => "GetEntitiesAround Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long callsTotal;
        static long listsReused;
        static long recursionFallbacks;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref callsTotal, 0); Interlocked.Exchange(ref listsReused, 0); Interlocked.Exchange(ref recursionFallbacks, 0); startTick = Environment.TickCount64; }

        public static void OnReuse() { if (enabled) { Interlocked.Increment(ref callsTotal); Interlocked.Increment(ref listsReused); } }
        public static void OnRecursionFallback() { if (enabled) { Interlocked.Increment(ref callsTotal); Interlocked.Increment(ref recursionFallbacks); } }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var total = Interlocked.Read(ref callsTotal);
            var reused = Interlocked.Read(ref listsReused);
            var fallbacks = Interlocked.Read(ref recursionFallbacks);
            var pct = total > 0 ? (double)reused / total * 100.0 : 0;
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"total={total} reused={reused} recursionFallbacks={fallbacks} reuseRate={pct:F1}%");
            DiagLog.Line(api, caller, $"rate={total / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{reused * 24 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagEntityDespawnPacket : IDiagModule
    {
        public string ShortName => "entitydespawnpacket";
        public string DisplayName => "EntityDespawnPacket Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long packetsBuilt;
        static long allocationsAvoided;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref packetsBuilt, 0); Interlocked.Exchange(ref allocationsAvoided, 0); startTick = Environment.TickCount64; }

        public static void OnPacketBuilt() { if (enabled) Interlocked.Increment(ref packetsBuilt); }
        public static void OnAllocationsAvoided(int count) { if (enabled) Interlocked.Add(ref allocationsAvoided, count); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var pkts = Interlocked.Read(ref packetsBuilt);
            var allocs = Interlocked.Read(ref allocationsAvoided);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"packetsBuilt={pkts} allocsAvoided={allocs} (3 LINQ chains replaced per packet)");
            DiagLog.Line(api, caller, $"rate={pkts / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{allocs * 32 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagRecipeBaseLinq : IDiagModule
    {
        public string ShortName => "recipebaselinq";
        public string DisplayName => "RecipeBase LINQ Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long callsIntercepted;
        static long allocationsAvoided;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref callsIntercepted, 0); Interlocked.Exchange(ref allocationsAvoided, 0); startTick = Environment.TickCount64; }

        public static void OnIntercept() { if (enabled) Interlocked.Increment(ref callsIntercepted); }
        public static void OnAllocationAvoided(int count) { if (enabled) Interlocked.Add(ref allocationsAvoided, count); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var calls = Interlocked.Read(ref callsIntercepted);
            var allocs = Interlocked.Read(ref allocationsAvoided);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"callsIntercepted={calls} allocsAvoided={allocs}");
            DiagLog.Line(api, caller, $"rate={calls / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{allocs * 40 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }
}
