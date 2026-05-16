using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten.Diagnostics
{
    public sealed class DiagEntityListReuse : IDiagModule
    {
        public string ShortName => "entitylistreuse";
        public string DisplayName => "Entity List Reuse";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long listsReused;
        static long fallbacks;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref listsReused, 0); Interlocked.Exchange(ref fallbacks, 0); startTick = Environment.TickCount64; }

        public static void OnReuse() { if (enabled) Interlocked.Increment(ref listsReused); }
        public static void OnFallback() { if (enabled) Interlocked.Increment(ref fallbacks); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var reused = Interlocked.Read(ref listsReused);
            var fb = Interlocked.Read(ref fallbacks);
            var total = reused + fb;
            var pct = total > 0 ? (double)reused / total * 100.0 : 0;
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;
            var rate = elapsed > 0 ? reused / elapsed : 0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"reused={reused} fallbacks={fb} total={total} reuseRate={pct:F1}%");
            DiagLog.Line(api, caller, $"rate={rate:F1}/s over {elapsed:F0}s | GC saved ~{reused * 48 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagBlockListReuse : IDiagModule
    {
        public string ShortName => "blocklistreuse";
        public string DisplayName => "Block List Reuse";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long listsReused;
        static long fallbacks;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref listsReused, 0); Interlocked.Exchange(ref fallbacks, 0); startTick = Environment.TickCount64; }

        public static void OnReuse() { if (enabled) Interlocked.Increment(ref listsReused); }
        public static void OnFallback() { if (enabled) Interlocked.Increment(ref fallbacks); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var reused = Interlocked.Read(ref listsReused);
            var fb = Interlocked.Read(ref fallbacks);
            var total = reused + fb;
            var pct = total > 0 ? (double)reused / total * 100.0 : 0;
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"reused={reused} fallbacks={fb} reuseRate={pct:F1}%");
            DiagLog.Line(api, caller, $"rate={reused / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{reused * 32 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagGetDropsListReuse : IDiagModule
    {
        public string ShortName => "getdropslistreuse";
        public string DisplayName => "GetDrops List Reuse";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long listsReused;
        static long fallbacks;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref listsReused, 0); Interlocked.Exchange(ref fallbacks, 0); startTick = Environment.TickCount64; }

        public static void OnReuse() { if (enabled) Interlocked.Increment(ref listsReused); }
        public static void OnFallback() { if (enabled) Interlocked.Increment(ref fallbacks); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var reused = Interlocked.Read(ref listsReused);
            var fb = Interlocked.Read(ref fallbacks);
            var total = reused + fb;
            var pct = total > 0 ? (double)reused / total * 100.0 : 0;
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"reused={reused} fallbacks={fb} reuseRate={pct:F1}%");
            DiagLog.Line(api, caller, $"rate={reused / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{reused * 64 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagEventManagerListReuse : IDiagModule
    {
        public string ShortName => "eventmanagerlistreuse";
        public string DisplayName => "EventManager List Reuse";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long listsReused;
        static long fallbacks;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref listsReused, 0); Interlocked.Exchange(ref fallbacks, 0); startTick = Environment.TickCount64; }

        public static void OnReuse() { if (enabled) Interlocked.Increment(ref listsReused); }
        public static void OnFallback() { if (enabled) Interlocked.Increment(ref fallbacks); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var reused = Interlocked.Read(ref listsReused);
            var fb = Interlocked.Read(ref fallbacks);
            var total = reused + fb;
            var pct = total > 0 ? (double)reused / total * 100.0 : 0;
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"reused={reused} fallbacks={fb} reuseRate={pct:F1}%");
            DiagLog.Line(api, caller, $"rate={reused / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{reused * 32 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagChunkLoading : IDiagModule
    {
        public string ShortName => "chunkloading";
        public string DisplayName => "Chunk Loading Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long chunksProcessed;
        static long allocationsAvoided;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref chunksProcessed, 0); Interlocked.Exchange(ref allocationsAvoided, 0); startTick = Environment.TickCount64; }

        public static void OnChunkProcessed() { if (enabled) Interlocked.Increment(ref chunksProcessed); }
        public static void OnAllocationAvoided() { if (enabled) Interlocked.Increment(ref allocationsAvoided); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var chunks = Interlocked.Read(ref chunksProcessed);
            var allocs = Interlocked.Read(ref allocationsAvoided);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"chunksProcessed={chunks} allocsAvoided={allocs}");
            DiagLog.Line(api, caller, $"rate={chunks / Math.Max(elapsed, 1):F1} chunks/s over {elapsed:F0}s");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagChunkUnloading : IDiagModule
    {
        public string ShortName => "chunkunloading";
        public string DisplayName => "Chunk Unloading Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long chunksProcessed;
        static long allocationsAvoided;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref chunksProcessed, 0); Interlocked.Exchange(ref allocationsAvoided, 0); startTick = Environment.TickCount64; }

        public static void OnChunkProcessed() { if (enabled) Interlocked.Increment(ref chunksProcessed); }
        public static void OnAllocationAvoided() { if (enabled) Interlocked.Increment(ref allocationsAvoided); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var chunks = Interlocked.Read(ref chunksProcessed);
            var allocs = Interlocked.Read(ref allocationsAvoided);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"chunksProcessed={chunks} allocsAvoided={allocs}");
            DiagLog.Line(api, caller, $"rate={chunks / Math.Max(elapsed, 1):F1} chunks/s over {elapsed:F0}s");
            DiagLog.Footer(api, caller);
        }
    }
}
