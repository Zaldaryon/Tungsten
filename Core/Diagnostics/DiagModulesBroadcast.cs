using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten.Diagnostics
{
    public sealed class DiagBroadcastLinq : IDiagModule
    {
        public string ShortName => "broadcastlinq";
        public string DisplayName => "Broadcast LINQ Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long broadcastsIntercepted;
        static long closuresAvoided;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref broadcastsIntercepted, 0); Interlocked.Exchange(ref closuresAvoided, 0); startTick = Environment.TickCount64; }

        public static void OnIntercept() { if (enabled) Interlocked.Increment(ref broadcastsIntercepted); }
        public static void OnClosuresAvoided(int count) { if (enabled) Interlocked.Add(ref closuresAvoided, count); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var broadcasts = Interlocked.Read(ref broadcastsIntercepted);
            var closures = Interlocked.Read(ref closuresAvoided);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"broadcastsIntercepted={broadcasts} closuresAvoided={closures}");
            DiagLog.Line(api, caller, $"rate={broadcasts / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{closures * 64 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagBulkEntityAttributesPacket : IDiagModule
    {
        public string ShortName => "bulkentityattributespacket";
        public string DisplayName => "BulkEntityAttributes Packet Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long packetsBuilt;
        static long wrappersReused;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref packetsBuilt, 0); Interlocked.Exchange(ref wrappersReused, 0); startTick = Environment.TickCount64; }

        public static void OnPacketBuilt() { if (enabled) Interlocked.Increment(ref packetsBuilt); }
        public static void OnWrapperReused() { if (enabled) Interlocked.Increment(ref wrappersReused); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var pkts = Interlocked.Read(ref packetsBuilt);
            var reused = Interlocked.Read(ref wrappersReused);
            var pct = pkts > 0 ? (double)reused / pkts * 100.0 : 0;
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"packetsBuilt={pkts} wrappersReused={reused} reuseRate={pct:F1}%");
            DiagLog.Line(api, caller, $"rate={pkts / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{reused * 2 * 48 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagClassRegistryFrozen : IDiagModule
    {
        public string ShortName => "classregistryfrozen";
        public string DisplayName => "ClassRegistry Frozen Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long dictionariesCompacted;
        static long totalEntriesTrimmed;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref dictionariesCompacted, 0); Interlocked.Exchange(ref totalEntriesTrimmed, 0); startTick = Environment.TickCount64; }

        public static void OnDictionaryCompacted(int entries) { Interlocked.Increment(ref dictionariesCompacted); Interlocked.Add(ref totalEntriesTrimmed, entries); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var dicts = Interlocked.Read(ref dictionariesCompacted);
            var entries = Interlocked.Read(ref totalEntriesTrimmed);

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"dictionariesCompacted={dicts} totalEntries={entries} (one-shot at startup)");
            DiagLog.Line(api, caller, $"estimated memory saved ~{dicts * 2 / 1024.0:F1} KB (hash table slack reclaimed)");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagGetPlayersAround : IDiagModule
    {
        public string ShortName => "getplayersaround";
        public string DisplayName => "GetPlayersAround Optimization";
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
}
