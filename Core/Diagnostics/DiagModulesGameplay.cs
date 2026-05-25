using System;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten.Diagnostics
{
    public sealed class DiagEntitySimulation : IDiagModule
    {
        public string ShortName => "entitysimulation";
        public string DisplayName => "Entity Simulation Optimization";
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
            if (ticks == 0)
                DiagLog.Line(api, caller, "path covered by entitylistreuse (shared TickEntities patch)");
            else
                DiagLog.Line(api, caller, $"ticks={ticks} allocsAvoided={allocs}");
            DiagLog.Line(api, caller, $"rate={ticks / Math.Max(elapsed, 1):F1} ticks/s over {elapsed:F0}s");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagCookingContainer : IDiagModule
    {
        public string ShortName => "cookingcontainer";
        public string DisplayName => "Cooking Container Optimization";
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
            DiagLog.Line(api, caller, $"rate={calls / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{allocs * 32 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagContainer : IDiagModule
    {
        public string ShortName => "container";
        public string DisplayName => "Container Optimization";
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
            DiagLog.Line(api, caller, $"rate={calls / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{allocs * 32 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagGridRecipe : IDiagModule
    {
        public string ShortName => "gridrecipe";
        public string DisplayName => "Grid Recipe Optimization";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        static long matchesTotal;
        static long allocationsAvoided;
        static long startTick;

        public void Enable() { startTick = Environment.TickCount64; enabled = true; }
        public void Disable() { enabled = false; }
        public void Reset() { Interlocked.Exchange(ref matchesTotal, 0); Interlocked.Exchange(ref allocationsAvoided, 0); startTick = Environment.TickCount64; }

        public static void OnMatch() { if (enabled) Interlocked.Increment(ref matchesTotal); }
        public static void OnAllocationAvoided(int count) { if (enabled) Interlocked.Add(ref allocationsAvoided, count); }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            var matches = Interlocked.Read(ref matchesTotal);
            var allocs = Interlocked.Read(ref allocationsAvoided);
            var elapsed = (Environment.TickCount64 - startTick) / 1000.0;

            DiagLog.Header(api, caller, ShortName);
            DiagLog.Line(api, caller, $"matchesTotal={matches} allocsAvoided={allocs} (2 per match)");
            DiagLog.Line(api, caller, $"rate={matches / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{allocs * 48 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }

    public sealed class DiagPropickReading : IDiagModule
    {
        public string ShortName => "propickreading";
        public string DisplayName => "Propick Reading Optimization";
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
            DiagLog.Line(api, caller, $"rate={calls / Math.Max(elapsed, 1):F1}/s over {elapsed:F0}s | GC saved ~{allocs * 64 / 1024.0 / 1024.0:F3} MB");
            DiagLog.Footer(api, caller);
        }
    }
}
