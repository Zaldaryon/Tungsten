using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten
{
    /// <summary>
    /// Monitoring and diagnostics for Tungsten memory optimization.
    /// Tracks thread usage, pool statistics, and memory patterns.
    /// Statistics collection is independent of optimizations - works even if all optimizations are disabled.
    /// </summary>
    public class TungstenMonitor
    {
        private const int AdvancedMonitorIntervalMs = 10000;
        private readonly ICoreServerAPI api;
        private readonly Dictionary<string, object> poolRegistry;
        private readonly Dictionary<string, System.Reflection.MethodInfo> cachedGetStatsMethods;
        private int maxThreadsObserved;
        private long lastMemoryBytes;
        private long lastTotalAllocations;
        private int lastGen0, lastGen1, lastGen2;
        private System.Threading.Timer advancedMonitorTimer;
        private string csvDirectory;
        private readonly object csvLock = new object();
        private readonly Process currentProcess;
        private TimeSpan lastCpuTime;
        private DateTime lastCpuCheck;

        public TungstenMonitor(ICoreServerAPI api)
        {
            this.api = api;
            this.poolRegistry = new Dictionary<string, object>();
            this.cachedGetStatsMethods = new Dictionary<string, System.Reflection.MethodInfo>();
            this.maxThreadsObserved = 0;
            this.lastTotalAllocations = GC.GetTotalAllocatedBytes(false);
            this.lastGen0 = GC.CollectionCount(0);
            this.lastGen1 = GC.CollectionCount(1);
            this.lastGen2 = GC.CollectionCount(2);
            this.currentProcess = Process.GetCurrentProcess();
            this.lastCpuTime = currentProcess.TotalProcessorTime;
            this.lastCpuCheck = DateTime.UtcNow;

            csvDirectory = api.GetOrCreateDataPath("ModData");

            LogInitialDiagnostics();
        }

        private void LogInitialDiagnostics()
        {
            int threadCount = currentProcess.Threads.Count;
            int threadPoolThreads = 0;
            int completionPortThreads = 0;
            ThreadPool.GetAvailableThreads(out threadPoolThreads, out completionPortThreads);

            int minWorkerThreads, minCompletionPortThreads;
            ThreadPool.GetMinThreads(out minWorkerThreads, out minCompletionPortThreads);

            int maxWorkerThreads, maxCompletionPortThreads;
            ThreadPool.GetMaxThreads(out maxWorkerThreads, out maxCompletionPortThreads);            api.Logger.Notification($"  In-Use Workers (approx): {maxWorkerThreads - threadPoolThreads}");
            maxThreadsObserved = threadCount;
            lastMemoryBytes = GC.GetTotalMemory(false);
        }

        public void RegisterPool(string name, object pool)
        {
            lock (poolRegistry)
            {
                poolRegistry[name] = pool;

                // Cache GetStats method to avoid reflection on every report
                if (pool != null)
                {
                    var statsMethod = pool.GetType().GetMethod("GetStats");
                    if (statsMethod != null)
                    {
                        cachedGetStatsMethods[name] = statsMethod;
                    }
                }
            }
        }

        private void ReportStatistics()
        {
            try
            {
                int currentThreadCount = currentProcess.Threads.Count;
                if (currentThreadCount > maxThreadsObserved)
                {
                    maxThreadsObserved = currentThreadCount;
                }

                // Calculate CPU usage
                DateTime now = DateTime.UtcNow;
                TimeSpan currentCpuTime = currentProcess.TotalProcessorTime;
                double cpuUsedMs = (currentCpuTime - lastCpuTime).TotalMilliseconds;
                double totalMs = (now - lastCpuCheck).TotalMilliseconds;
                double cpuUsagePercent = (cpuUsedMs / (Environment.ProcessorCount * totalMs)) * 100.0;
                lastCpuTime = currentCpuTime;
                lastCpuCheck = now;

                long currentMemory = GC.GetTotalMemory(false);
                long memoryDelta = currentMemory - lastMemoryBytes;
                lastMemoryBytes = currentMemory;

                int gen0 = GC.CollectionCount(0);
                int gen1 = GC.CollectionCount(1);
                int gen2 = GC.CollectionCount(2);
                int gen0Delta = gen0 - lastGen0;
                int gen1Delta = gen1 - lastGen1;
                int gen2Delta = gen2 - lastGen2;
                lastGen0 = gen0;
                lastGen1 = gen1;
                lastGen2 = gen2;

                long totalAllocations = GC.GetTotalAllocatedBytes(false);
                long allocationsDelta = totalAllocations - lastTotalAllocations;
                long allocationRatePerSec = allocationsDelta * 1000 / AdvancedMonitorIntervalMs;
                lastTotalAllocations = totalAllocations;

                var gcMemoryInfo = GC.GetGCMemoryInfo();
                long lohSize = gcMemoryInfo.FragmentedBytes;

                var (tlCapacity, tlCount, tlInstances) = ThreadLocalRegistry.GetStatistics();

                int cpuCores = Environment.ProcessorCount;

                api.Logger.Debug("[Tungsten] Periodic Statistics:");
                api.Logger.Debug($"  CPU Usage: {cpuUsagePercent:F1}% (Cores: {cpuCores})");
                api.Logger.Debug($"  Current Threads: {currentThreadCount}, Peak: {maxThreadsObserved}");
                api.Logger.Debug($"  Managed Memory: {currentMemory / 1024 / 1024} MB (Δ{memoryDelta / 1024} KB)");
                api.Logger.Debug($"  GC Gen0: {gen0} (+{gen0Delta}), Gen1: {gen1} (+{gen1Delta}), Gen2: {gen2} (+{gen2Delta})");
                api.Logger.Debug($"  Allocation Rate: {allocationRatePerSec / 1024 / 1024} MB/s");
                api.Logger.Debug($"  LOH Fragmented: {lohSize / 1024 / 1024} MB");
                api.Logger.Debug($"  ThreadLocal: {tlInstances} instances, Capacity: {tlCapacity}, Count: {tlCount}");

                // Write to daily CSV file
                lock (csvLock)
                {
                    string csvFilePath = System.IO.Path.Combine(csvDirectory, $"tungsten_stats_{DateTime.Now:yyyy-MM-dd}.csv");
                    System.IO.Directory.CreateDirectory(csvDirectory);
                    bool fileExists = System.IO.File.Exists(csvFilePath);
                    using (var writer = new System.IO.StreamWriter(csvFilePath, true))
                    {
                        if (!fileExists)
                        {
                            writer.WriteLine("Timestamp,CPU_Percent,CPU_Cores,Threads,PeakThreads,MemoryMB,MemoryDeltaKB,GC_Gen0,GC_Gen1,GC_Gen2,GC_Gen0_Delta,GC_Gen1_Delta,GC_Gen2_Delta,AllocationRate_MB_s,LOH_MB,TL_Instances,TL_Capacity,TL_Count");
                        }
                        writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{cpuUsagePercent:F1},{cpuCores},{currentThreadCount},{maxThreadsObserved},{currentMemory / 1024 / 1024},{memoryDelta / 1024},{gen0},{gen1},{gen2},{gen0Delta},{gen1Delta},{gen2Delta},{allocationRatePerSec / 1024 / 1024},{lohSize / 1024 / 1024},{tlInstances},{tlCapacity},{tlCount}");
                    }
                }

                // Report pool statistics using cached method info
                lock (poolRegistry)
                {
                    if (poolRegistry.Count > 0)
                    {
                        api.Logger.Debug("  Object Pool Stats:");
                        foreach (var kvp in poolRegistry)
                        {
                            // Use cached GetStats method to avoid reflection
                            if (cachedGetStatsMethods.TryGetValue(kvp.Key, out var statsMethod))
                            {
                                try
                                {
                                    var stats = statsMethod.Invoke(kvp.Value, null);
                                    api.Logger.Debug($"    {kvp.Key}: {stats}");
                                }
                                catch
                                {
                                    // Suppress exceptions in monitoring
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                api.Logger.Warning($"[Tungsten] Monitor error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            advancedMonitorTimer?.Dispose();
            currentProcess?.Dispose();
            lock (poolRegistry)
            {
                poolRegistry.Clear();
                cachedGetStatsMethods.Clear();
            }
        }

        public void StartAdvancedMonitoring()
        {
            if (advancedMonitorTimer == null)
            {
                advancedMonitorTimer = new System.Threading.Timer(_ => ReportStatistics(), null, AdvancedMonitorIntervalMs, AdvancedMonitorIntervalMs);
                api.Logger.Notification($"[Tungsten] Advanced monitoring started ({AdvancedMonitorIntervalMs / 1000}s interval)");
            }
        }

        public void StopAdvancedMonitoring()
        {
            if (advancedMonitorTimer != null)
            {
                advancedMonitorTimer.Dispose();
                advancedMonitorTimer = null;
            }
        }

        /// <summary>
        /// Force a statistics report (for debugging).
        /// </summary>
        public void ForceReport()
        {
            ReportStatistics();
        }
    }

    /// <summary>
    /// Helper for managing ThreadLocal collections with capacity trimming.
    /// v1.10.0: Simplified access-count based trimming (lower CPU overhead than timestamp checks).
    /// v1.9.3: Fixed reflection overhead - now uses compiled expression trees.
    /// </summary>
    public static class ThreadLocalHelper
    {
        // Cached config values to avoid repeated lookups (updated on config reload)
        private static int cachedTargetCapacity = 200;
        private static int cachedTrimInterval = 5000;
        private static bool cachedEnableTrimming = true;
        private static bool cachedEnableLifecycleReset = true;

        // v1.10.3: Track disposal state to prevent ObjectDisposedException during shutdown
        private static volatile bool isDisposing = false;
        private static int fallbackLogGate = 0;

        // v1.9.3: Compiled TrimExcess delegates for near-native performance
        private static readonly Dictionary<Type, Delegate> trimExcessDelegates = new Dictionary<Type, Delegate>();
        private static readonly object compileLock = new object();

        private class AccessCounter
        {
            public int Count;
        }

        [ThreadStatic]
        private static AccessCounter threadAccessCounter;

        public static void UpdateConfig(int targetCapacity, int trimInterval, bool enableTrimming, bool enableLifecycleReset)
        {
            cachedTargetCapacity = targetCapacity;
            cachedTrimInterval = trimInterval;
            cachedEnableTrimming = enableTrimming;
            cachedEnableLifecycleReset = enableLifecycleReset;
        }

        /// <summary>
        /// v1.10.3: Check if ThreadLocal instances are being disposed.
        /// </summary>
        public static bool IsDisposing => isDisposing;
        public static bool LifecycleResetEnabled => cachedEnableLifecycleReset;

        /// <summary>
        /// Startup safety reset for the lifecycle disposal flag.
        /// Returns false if reset is disabled or cannot be applied safely.
        /// </summary>
        public static bool TryResetDisposing()
        {
            if (!cachedEnableLifecycleReset)
            {
                TungstenMod.Instance?.Api?.Logger?.Notification(
                    "[Tungsten] [ThreadLocalLifecycleReset] Disabled by config; startup reset skipped"
                );
                return false;
            }

            try
            {
                isDisposing = false;
                fallbackLogGate = 0;
                return true;
            }
            catch (Exception ex)
            {
                ForceVanillaFallback("reset failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// v1.10.3: Notify that ThreadLocal instances are being disposed.
        /// Called by ThreadLocalRegistry before disposal to prevent race conditions.
        /// </summary>
        public static void NotifyDisposing()
        {
            isDisposing = true;
        }

        /// <summary>
        /// Hard safety mode: force allocation behavior equivalent to vanilla list/dictionary/hashset creation.
        /// </summary>
        public static void ForceVanillaFallback(string reason)
        {
            isDisposing = true;
            OptimizationRuntimeCircuitBreaker.Disable("ThreadLocalCollectionReuseCore", reason, emitLog: false);
            if (Interlocked.CompareExchange(ref fallbackLogGate, 1, 0) == 0)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning(
                    "[Tungsten] [ThreadLocalLifecycleReset] Falling back to vanilla allocation mode: " + reason
                );
            }
        }

        public static List<T> GetAndClear<T>(ThreadLocal<List<T>> threadLocal)
        {
            // v1.10.3: If disposing, return new list to avoid ObjectDisposedException during shutdown
            if (isDisposing)
                return new List<T>();
            if (!OptimizationRuntimeCircuitBreaker.ShouldRun("ThreadLocalCollectionReuseCore"))
                return new List<T>();

            try
            {
                var list = threadLocal.Value;
                list.Clear();
                if (cachedEnableTrimming)
                    MaybeTrimCapacity(list);
                return list;
            }
            catch (ObjectDisposedException)
            {
                // ThreadLocal was disposed during access - return new list as fallback
                return new List<T>();
            }
        }

        public static List<T> GetAndClearWithPeak<T>(ThreadLocal<List<T>> threadLocal, ref int peakCount)
        {
            if (isDisposing)
                return new List<T>();
            if (!OptimizationRuntimeCircuitBreaker.ShouldRun("ThreadLocalCollectionReuseCore"))
                return new List<T>();

            try
            {
                var list = threadLocal.Value;
                if (list.Count > peakCount)
                    peakCount = list.Count;
                list.Clear();
                if (cachedEnableTrimming)
                    MaybeTrimCapacity(list);
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>();
            }
        }

        public static Dictionary<TKey, TValue> GetAndClear<TKey, TValue>(ThreadLocal<Dictionary<TKey, TValue>> threadLocal)
        {
            // v1.10.3: If disposing, return new dictionary to avoid ObjectDisposedException during shutdown
            if (isDisposing)
                return new Dictionary<TKey, TValue>();
            if (!OptimizationRuntimeCircuitBreaker.ShouldRun("ThreadLocalCollectionReuseCore"))
                return new Dictionary<TKey, TValue>();

            try
            {
                var dict = threadLocal.Value;
                dict.Clear();
                if (cachedEnableTrimming)
                    MaybeTrimCapacity(dict);
                return dict;
            }
            catch (ObjectDisposedException)
            {
                // ThreadLocal was disposed during access - return new dictionary as fallback
                return new Dictionary<TKey, TValue>();
            }
        }

        public static HashSet<T> GetAndClear<T>(ThreadLocal<HashSet<T>> threadLocal)
        {
            // v1.10.3: If disposing, return new hashset to avoid ObjectDisposedException during shutdown
            if (isDisposing)
                return new HashSet<T>();
            if (!OptimizationRuntimeCircuitBreaker.ShouldRun("ThreadLocalCollectionReuseCore"))
                return new HashSet<T>();

            try
            {
                var set = threadLocal.Value;
                set.Clear();
                if (cachedEnableTrimming)
                    MaybeTrimCapacity(set);
                return set;
            }
            catch (ObjectDisposedException)
            {
                // ThreadLocal was disposed during access - return new hashset as fallback
                return new HashSet<T>();
            }
        }

        private static bool ShouldCheckTrim()
        {
            if (threadAccessCounter == null)
                threadAccessCounter = new AccessCounter();

            if (++threadAccessCounter.Count >= cachedTrimInterval)
            {
                threadAccessCounter.Count = 0;
                return true;
            }
            return false;
        }

        private static void MaybeTrimCapacity<T>(List<T> list)
        {
            if (ShouldCheckTrim() && list.Capacity > cachedTargetCapacity * 2)
                list.TrimExcess();
        }

        private static void MaybeTrimCapacity<TKey, TValue>(Dictionary<TKey, TValue> dict)
        {
            if (ShouldCheckTrim() && dict.Count == 0)
            {
                var trimAction = GetOrCompileTrimExcess<Dictionary<TKey, TValue>>();
                trimAction?.Invoke(dict);
            }
        }

        private static void MaybeTrimCapacity<T>(HashSet<T> set)
        {
            if (ShouldCheckTrim() && set.Count == 0)
            {
                var trimAction = GetOrCompileTrimExcess<HashSet<T>>();
                trimAction?.Invoke(set);
            }
        }

        public static void MaybeTrimList<T>(List<T> list)
        {
            if (list == null || !cachedEnableTrimming)
                return;

            MaybeTrimCapacity(list);
        }

        private static Action<T> GetOrCompileTrimExcess<T>()
        {
            var type = typeof(T);
            
            lock (compileLock)
            {
                if (trimExcessDelegates.TryGetValue(type, out var cached))
                    return cached as Action<T>;

                try
                {
                    var trimMethod = type.GetMethod("TrimExcess", Type.EmptyTypes);
                    if (trimMethod == null)
                        return null;

                    var param = System.Linq.Expressions.Expression.Parameter(type, "obj");
                    var call = System.Linq.Expressions.Expression.Call(param, trimMethod);
                    var lambda = System.Linq.Expressions.Expression.Lambda<Action<T>>(call, param);
                    var compiled = lambda.Compile();

                    trimExcessDelegates[type] = compiled;
                    return compiled;
                }
                catch (Exception ex)
                {
                    TungstenMod.Instance?.Api?.Logger?.Debug($"[Tungsten] Failed to compile TrimExcess for {type.Name}: {ex.Message}");
                    return null;
                }
            }
        }
    }
}
