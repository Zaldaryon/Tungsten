using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Tungsten
{
    /// <summary>
    /// Uniform runtime circuit-breaker for optimization paths.
    /// When enabled, a failing optimization key is degraded to vanilla-safe behavior for the rest of runtime.
    /// </summary>
    public static class OptimizationRuntimeCircuitBreaker
    {
        private static volatile bool enabled = true;
        private static readonly ConcurrentDictionary<string, byte> disabledKeys = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> perKeyLogGates = new(StringComparer.OrdinalIgnoreCase);
        private static int resetLogGate;

        public static void UpdateConfig(bool isEnabled)
        {
            enabled = isEnabled;
            if (!isEnabled)
            {
                disabledKeys.Clear();
                perKeyLogGates.Clear();
                resetLogGate = 0;
            }
        }

        public static bool Enabled => enabled;

        public static bool ShouldRun(string optimizationKey)
        {
            if (!enabled || string.IsNullOrWhiteSpace(optimizationKey))
                return true;

            return !disabledKeys.ContainsKey(optimizationKey);
        }

        public static bool TryResetState()
        {
            if (!enabled)
            {
                TungstenMod.Instance?.Api?.Logger?.Notification(
                    "[Tungsten] [RuntimeCircuitBreaker] Disabled by config; startup reset skipped"
                );
                return false;
            }

            try
            {
                disabledKeys.Clear();
                perKeyLogGates.Clear();
                resetLogGate = 0;
                return true;
            }
            catch (Exception ex)
            {
                if (System.Threading.Interlocked.CompareExchange(ref resetLogGate, 1, 0) == 0)
                {
                    TungstenMod.Instance?.Api?.Logger?.Warning(
                        "[Tungsten] [RuntimeCircuitBreaker] Reset failed: " + ex.Message
                    );
                }
                return false;
            }
        }

        public static void Disable(string optimizationKey, string reason, bool emitLog = true)
        {
            if (!enabled || string.IsNullOrWhiteSpace(optimizationKey))
                return;

            disabledKeys[optimizationKey] = 1;
            if (!emitLog)
                return;

            if (perKeyLogGates.TryAdd(optimizationKey, 1))
            {
                TungstenMod.Instance?.Api?.Logger?.Warning(
                    "[Tungsten] [RuntimeCircuitBreaker] Disabled optimization and falling back to vanilla path: "
                    + optimizationKey + " (" + reason + ")"
                );
            }
        }

        public static string GetStatusSummary()
        {
            if (!enabled)
                return "CircuitBreaker=OFF";

            int degradedCount = disabledKeys.Count;
            if (degradedCount == 0)
                return "CircuitBreaker=ON (degraded=0)";

            string degradedList = string.Join(", ", disabledKeys.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return "CircuitBreaker=ON (degraded=" + degradedCount + ": " + degradedList + ")";
        }
    }
}
