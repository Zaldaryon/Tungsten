using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten
{
    /// <summary>
    /// P10 benchmark harness for reproducible A/B sessions.
    /// Collects periodic runtime metrics into CSV and auto-disables itself on failure.
    /// </summary>
    public sealed class TungstenBenchmarkHarness : IDisposable
    {
        private readonly ICoreServerAPI api;
        private readonly Func<TungstenConfig> configProvider;
        private readonly Action<string> onCriticalFailure;
        private readonly object writeLock = new object();
        private readonly Process currentProcess;

        private Timer timer;
        private bool active;
        private string csvPath;
        private string profile;
        private string variant;
        private int sampleIntervalMs;
        private int durationSeconds;
        private DateTime sessionStartUtc;
        private TimeSpan lastCpuTime;
        private DateTime lastCpuCheckUtc;
        private long lastAllocatedBytes;
        private int lastGen0;
        private int lastGen1;
        private int lastGen2;
        private int failureGate;

        public TungstenBenchmarkHarness(ICoreServerAPI api, Func<TungstenConfig> configProvider, Action<string> onCriticalFailure)
        {
            this.api = api;
            this.configProvider = configProvider;
            this.onCriticalFailure = onCriticalFailure;
            currentProcess = Process.GetCurrentProcess();
        }

        public bool IsActive => active;
        public string CurrentCsvPath => csvPath;

        public bool TryStart()
        {
            if (active)
                return true;

            var config = configProvider?.Invoke();
            if (config == null || !config.EnableBenchmarkHarness)
                return false;

            try
            {
                profile = SanitizeToken(config.BenchmarkProfile);
                variant = SanitizeToken(config.BenchmarkVariant);
                sampleIntervalMs = Clamp(config.BenchmarkSampleIntervalMs, 1000, 60000);
                durationSeconds = Clamp(config.BenchmarkSessionDurationSeconds, 30, 86400);

                sessionStartUtc = DateTime.UtcNow;
                lastCpuTime = currentProcess.TotalProcessorTime;
                lastCpuCheckUtc = sessionStartUtc;
                lastAllocatedBytes = GC.GetTotalAllocatedBytes(false);
                lastGen0 = GC.CollectionCount(0);
                lastGen1 = GC.CollectionCount(1);
                lastGen2 = GC.CollectionCount(2);
                failureGate = 0;

                string csvDirectory = api.GetOrCreateDataPath("ModData");
                Directory.CreateDirectory(csvDirectory);
                string sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                csvPath = Path.Combine(csvDirectory, $"tungsten_benchmark_{profile}_{variant}_{sessionId}.csv");

                WriteHeaderIfNeeded();

                timer = new Timer(_ => SampleTick(), null, sampleIntervalMs, sampleIntervalMs);
                active = true;

                api.Logger.Notification(
                    $"[Tungsten] [BenchmarkHarness] Started profile={profile}, variant={variant}, duration={durationSeconds}s, sample={sampleIntervalMs}ms"
                );
                api.Logger.Notification($"[Tungsten] [BenchmarkHarness] CSV: {csvPath}");
                return true;
            }
            catch (Exception ex)
            {
                HandleFailure("start failed: " + ex.Message);
                return false;
            }
        }

        public void Stop(string reason = null)
        {
            if (!active && timer == null)
                return;

            active = false;
            timer?.Dispose();
            timer = null;

            if (!string.IsNullOrWhiteSpace(reason))
            {
                api.Logger.Notification("[Tungsten] [BenchmarkHarness] Stopped: " + reason);
            }
            else
            {
                api.Logger.Notification("[Tungsten] [BenchmarkHarness] Stopped");
            }
        }

        public string GetStatus()
        {
            if (!active)
                return "BenchmarkHarness: OFF";

            double elapsedSec = (DateTime.UtcNow - sessionStartUtc).TotalSeconds;
            return $"BenchmarkHarness: ON profile={profile} variant={variant} elapsed={elapsedSec:F0}s/{durationSeconds}s sample={sampleIntervalMs}ms";
        }

        private void SampleTick()
        {
            if (!active)
                return;

            try
            {
                DateTime now = DateTime.UtcNow;
                double elapsedSec = (now - sessionStartUtc).TotalSeconds;
                if (elapsedSec >= durationSeconds)
                {
                    Stop("session completed");
                    return;
                }

                TimeSpan cpuNow = currentProcess.TotalProcessorTime;
                double cpuUsedMs = (cpuNow - lastCpuTime).TotalMilliseconds;
                double wallMs = Math.Max(1, (now - lastCpuCheckUtc).TotalMilliseconds);
                double cpuPercent = (cpuUsedMs / (Environment.ProcessorCount * wallMs)) * 100.0;
                lastCpuTime = cpuNow;
                lastCpuCheckUtc = now;

                long managedBytes = GC.GetTotalMemory(false);
                long allocatedBytes = GC.GetTotalAllocatedBytes(false);
                long deltaAllocated = allocatedBytes - lastAllocatedBytes;
                double allocRateMbPerSec = (deltaAllocated * 1000.0 / wallMs) / (1024.0 * 1024.0);
                lastAllocatedBytes = allocatedBytes;

                int gen0 = GC.CollectionCount(0);
                int gen1 = GC.CollectionCount(1);
                int gen2 = GC.CollectionCount(2);
                int deltaGen0 = gen0 - lastGen0;
                int deltaGen1 = gen1 - lastGen1;
                int deltaGen2 = gen2 - lastGen2;
                lastGen0 = gen0;
                lastGen1 = gen1;
                lastGen2 = gen2;

                int threadCount = currentProcess.Threads.Count;
                int threadLocals = ThreadLocalRegistry.Count;
                string runtimeHealth = OptimizationRuntimeCircuitBreaker.GetStatusSummary();

                WriteRow(
                    now,
                    elapsedSec,
                    cpuPercent,
                    managedBytes / 1024.0 / 1024.0,
                    allocatedBytes / 1024.0 / 1024.0,
                    allocRateMbPerSec,
                    gen0,
                    gen1,
                    gen2,
                    deltaGen0,
                    deltaGen1,
                    deltaGen2,
                    threadCount,
                    threadLocals,
                    runtimeHealth
                );
            }
            catch (Exception ex)
            {
                HandleFailure("sampling failed: " + ex.Message);
            }
        }

        private void WriteHeaderIfNeeded()
        {
            lock (writeLock)
            {
                bool exists = File.Exists(csvPath);
                using var writer = new StreamWriter(csvPath, true);
                if (!exists)
                {
                    writer.WriteLine(
                        "TimestampUtc,Profile,Variant,ElapsedSeconds,CPU_Percent,ManagedMB,TotalAllocatedMB,AllocRateMBs,GC_Gen0,GC_Gen1,GC_Gen2,GC_Gen0_Delta,GC_Gen1_Delta,GC_Gen2_Delta,Threads,ThreadLocals,RuntimeHealth"
                    );
                }
            }
        }

        private void WriteRow(
            DateTime now,
            double elapsedSec,
            double cpuPercent,
            double managedMb,
            double totalAllocatedMb,
            double allocRateMbPerSec,
            int gen0,
            int gen1,
            int gen2,
            int deltaGen0,
            int deltaGen1,
            int deltaGen2,
            int threadCount,
            int threadLocals,
            string runtimeHealth)
        {
            lock (writeLock)
            {
                using var writer = new StreamWriter(csvPath, true);
                writer.WriteLine(
                    $"{now:yyyy-MM-dd HH:mm:ss},{profile},{variant},{elapsedSec:F0},{cpuPercent:F2},{managedMb:F2},{totalAllocatedMb:F2},{allocRateMbPerSec:F3},{gen0},{gen1},{gen2},{deltaGen0},{deltaGen1},{deltaGen2},{threadCount},{threadLocals},\"{runtimeHealth}\""
                );
            }
        }

        private void HandleFailure(string reason)
        {
            if (Interlocked.CompareExchange(ref failureGate, 1, 0) != 0)
                return;

            Stop("auto-disabled: " + reason);
            api.Logger.Warning("[Tungsten] [BenchmarkHarness] Disabled and falling back to vanilla behavior: " + reason);
            onCriticalFailure?.Invoke(reason);
        }

        private static string SanitizeToken(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "default";

            var chars = input.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '-' || chars[i] == '_'))
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
