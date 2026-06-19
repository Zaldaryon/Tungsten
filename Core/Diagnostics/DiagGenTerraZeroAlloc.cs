using System;
using System.Diagnostics;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten.Diagnostics
{
    /// <summary>
    /// A/B diagnostic for GenTerra zero-alloc optimization.
    /// When enabled, runs BOTH paths (optimized AND vanilla) on the SAME chunk
    /// for every generated chunk, collecting paired timing data.
    /// 
    /// This guarantees identical terrain complexity for both measurements,
    /// making the comparison statistically valid.
    /// 
    /// Usage: /tungsten stats genterrazeroalloc on
    ///        ... wait for world exploration (50+ chunks) ...
    ///        /tungsten stats genterrazeroalloc dump
    /// 
    /// Note: When enabled, worldgen is ~2× slower (runs each chunk twice).
    /// Disable after collecting data.
    /// </summary>
    public sealed class DiagGenTerraZeroAlloc : IDiagModule
    {
        public string ShortName => "genterrazeroalloc";
        public string DisplayName => "GenTerra Zero-Alloc A/B";
        public static volatile bool enabled;
        public bool Enabled => enabled;

        // Per-chunk paired measurements
        private static long s_chunks;
        private static long s_totalTicksA;
        private static long s_totalTicksB;
        private static long s_totalAllocA;
        private static long s_totalAllocB;

        // For variance calculation (Welford's online algorithm)
        private static double s_meanDiffMs;
        private static double s_m2DiffMs;

        private static long s_minTicksA = long.MaxValue;
        private static long s_maxTicksA;
        private static long s_minTicksB = long.MaxValue;
        private static long s_maxTicksB;
        private static long s_startTick;

        private static readonly Lock s_lock = new();

        // Flag read by the optimizer to know which pass is running
        // true = use pool (optimized), false = allocate (vanilla)
        public static volatile bool s_usePoolThisPass = true;

        /// <summary>
        /// Records a paired A/B measurement for one chunk.
        /// Called by the generate() postfix after running both passes.
        /// Defense-in-depth: rejects samples >1000ms (OS thread preemption noise).
        /// Primary protection against stale timestamps is the s_prefixRan guard.
        /// </summary>
        public static void RecordPairedResult(long ticksA, long ticksB, long allocA, long allocB)
        {
            if (!enabled) return;

            double freq = Stopwatch.Frequency;

            // Reject outliers: normal chunk gen is 5-50ms; >1000ms indicates OS-level interference
            double msA = ticksA / freq * 1000.0;
            double msB = ticksB / freq * 1000.0;
            if (msA > 1000.0 || msB > 1000.0) return;

            double diffMs = msB - msA;

            lock (s_lock)
            {
                s_chunks++;
                s_totalTicksA += ticksA;
                s_totalTicksB += ticksB;
                s_totalAllocA += allocA;
                s_totalAllocB += allocB;

                // Welford's online variance for the per-chunk time difference
                double delta = diffMs - s_meanDiffMs;
                s_meanDiffMs += delta / s_chunks;
                double delta2 = diffMs - s_meanDiffMs;
                s_m2DiffMs += delta * delta2;

                // Min/max
                if (ticksA < s_minTicksA) s_minTicksA = ticksA;
                if (ticksA > s_maxTicksA) s_maxTicksA = ticksA;
                if (ticksB < s_minTicksB) s_minTicksB = ticksB;
                if (ticksB > s_maxTicksB) s_maxTicksB = ticksB;
            }
        }

        public void Enable()
        {
            Reset();
            s_startTick = Environment.TickCount64;
            enabled = true;
        }

        public void Disable() { enabled = false; s_usePoolThisPass = true; }

        public void Reset()
        {
            lock (s_lock)
            {
                s_chunks = 0;
                s_totalTicksA = 0;
                s_totalTicksB = 0;
                s_totalAllocA = 0;
                s_totalAllocB = 0;
                s_meanDiffMs = 0;
                s_m2DiffMs = 0;
                s_minTicksA = long.MaxValue;
                s_maxTicksA = 0;
                s_minTicksB = long.MaxValue;
                s_maxTicksB = 0;
                s_startTick = Environment.TickCount64;
            }
        }

        public void Dump(ICoreServerAPI api, IServerPlayer caller)
        {
            long chunks, ticksA, ticksB, allocA, allocB, minA, maxA, minB, maxB;
            double meanDiff, m2Diff;

            lock (s_lock)
            {
                chunks = s_chunks;
                ticksA = s_totalTicksA;
                ticksB = s_totalTicksB;
                allocA = s_totalAllocA;
                allocB = s_totalAllocB;
                minA = s_minTicksA;
                maxA = s_maxTicksA;
                minB = s_minTicksB;
                maxB = s_maxTicksB;
                meanDiff = s_meanDiffMs;
                m2Diff = s_m2DiffMs;
            }

            double elapsed = (Environment.TickCount64 - s_startTick) / 1000.0;
            double freq = Stopwatch.Frequency;

            DiagLog.Header(api, caller, ShortName);

            if (chunks == 0)
            {
                DiagLog.Line(api, caller, "No data. Enable and explore to generate chunks.");
                DiagLog.Footer(api, caller);
                return;
            }

            double avgMsA = (ticksA / freq * 1000.0) / chunks;
            double avgMsB = (ticksB / freq * 1000.0) / chunks;
            double minMsA = minA != long.MaxValue ? minA / freq * 1000.0 : 0;
            double maxMsA = maxA / freq * 1000.0;
            double minMsB = minB != long.MaxValue ? minB / freq * 1000.0 : 0;
            double maxMsB = maxB / freq * 1000.0;
            double avgAllocKbA = allocA / 1024.0 / chunks;
            double avgAllocKbB = allocB / 1024.0 / chunks;
            double speedupPct = avgMsB > 0 ? (avgMsB - avgMsA) / avgMsB * 100.0 : 0;

            // Standard deviation and confidence interval of the difference
            double varianceDiff = chunks > 1 ? m2Diff / (chunks - 1) : 0;
            double stddevDiff = Math.Sqrt(varianceDiff);
            double stderrDiff = chunks > 1 ? stddevDiff / Math.Sqrt(chunks) : 0;
            // 95% confidence interval (z=1.96 for large n)
            double ci95 = stderrDiff * 1.96;

            DiagLog.Line(api, caller, $"── Paired A/B Test ({chunks} chunks, {elapsed:F0}s) ──");
            DiagLog.Line(api, caller, $"  Same chunk run with BOTH paths for direct comparison.");
            DiagLog.Line(api, caller, $"");
            DiagLog.Line(api, caller, $"  A (optimized):  avg={avgMsA:F3}ms  [{minMsA:F2}..{maxMsA:F2}ms]  alloc={avgAllocKbA:F0}KB/chunk");
            DiagLog.Line(api, caller, $"  B (vanilla):    avg={avgMsB:F3}ms  [{minMsB:F2}..{maxMsB:F2}ms]  alloc={avgAllocKbB:F0}KB/chunk");
            DiagLog.Line(api, caller, $"");
            DiagLog.Line(api, caller, $"  ── Result ──");
            DiagLog.Line(api, caller, $"  Speedup: {speedupPct:F1}%  ({avgMsB - avgMsA:F3}ms saved/chunk)");
            DiagLog.Line(api, caller, $"  Alloc reduction: {avgAllocKbB - avgAllocKbA:F0} KB/chunk ({(avgAllocKbB > 0 ? (1 - avgAllocKbA / avgAllocKbB) * 100 : 0):F0}% less)");
            DiagLog.Line(api, caller, $"");
            DiagLog.Line(api, caller, $"  ── Statistical Confidence ──");
            DiagLog.Line(api, caller, $"  Mean diff (B-A): {meanDiff:F3}ms ± {ci95:F3}ms (95% CI)");
            DiagLog.Line(api, caller, $"  Std dev: {stddevDiff:F3}ms  Samples: {chunks}");

            if (chunks >= 30 && meanDiff - ci95 > 0)
                DiagLog.Line(api, caller, $"  Verdict: STATISTICALLY SIGNIFICANT (A is faster, p<0.05)");
            else if (chunks >= 30 && meanDiff + ci95 < 0)
                DiagLog.Line(api, caller, $"  Verdict: STATISTICALLY SIGNIFICANT (B is faster - optimization HURTS!)");
            else if (chunks >= 30)
                DiagLog.Line(api, caller, $"  Verdict: NOT SIGNIFICANT (difference within noise, need more samples)");
            else
                DiagLog.Line(api, caller, $"  Verdict: INSUFFICIENT DATA (need 30+ chunks, have {chunks})");

            DiagLog.Footer(api, caller);
        }
    }
}
