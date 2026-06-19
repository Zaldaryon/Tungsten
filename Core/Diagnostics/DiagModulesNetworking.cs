using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten.Diagnostics
{
    public sealed class DiagSendPlayerEntityDeaths : IDiagModule
    {
        public string ShortName => "sendplayerentitydeaths";
        public string DisplayName => "SendPlayerEntityDeaths Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long callsIntercepted;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref callsIntercepted, 0); startTick = Environment.TickCount64; }

        public static void OnIntercept() { if (enabled) Interlocked.Increment(ref callsIntercepted); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var calls = Interlocked.Read(ref callsIntercepted);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"callsIntercepted={calls} (Enumerable.ToList→pool replacement)");
            DiagLog.Line(api, caller, $"rate={calls / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagPhysicsManagerList : IDiagModule
    {
        public string ShortName => "physicsmanagerlist";
        public string DisplayName => "PhysicsManager List Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long ticksTotal;
        static long allocationsAvoided;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref ticksTotal, 0); Interlocked.Exchange(ref allocationsAvoided, 0); startTick = Environment.TickCount64; }

        public static void OnTick() { if (enabled) Interlocked.Increment(ref ticksTotal); }
        public static void OnAllocationAvoided() { if (enabled) Interlocked.Increment(ref allocationsAvoided); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var ticks = Interlocked.Read(ref ticksTotal);
            var allocs = Interlocked.Read(ref allocationsAvoided);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"ticks={ticks} allocsAvoided={allocs}");
            DiagLog.Line(api, caller, $"rate={ticks / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{allocs * 32 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagPhysicsManagerMethodList : IDiagModule
    {
        public string ShortName => "physicsmanagermethodlist";
        public string DisplayName => "PhysicsManager MethodList Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { startTick = Environment.TickCount64; }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, "counter shared with physicsmanagerlist (same ReusableCollectionPool path)");
            DiagLog.Line(api, caller, $"target methods: SendPositionsAndAnimations, SendTrackedEntitiesStateChanges");
            DiagLog.Line(api, caller, $"active only in multiplayer with entities in player tracking range ({elapsed:F0}s elapsed)");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagServerMainLinq : IDiagModule
    {
        public string ShortName => "servermainlinq";
        public string DisplayName => "ServerMain LINQ Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long callsIntercepted;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref callsIntercepted, 0); startTick = Environment.TickCount64; }

        public static void OnIntercept() { if (enabled) Interlocked.Increment(ref callsIntercepted); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var calls = Interlocked.Read(ref callsIntercepted);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"callsIntercepted={calls} (LINQ→loop replacements)");
            DiagLog.Line(api, caller, $"rate={calls / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s");
            DiagLog.Footer(api, caller);
        }
    }
}
