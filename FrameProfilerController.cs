using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Tungsten
{
    /// <summary>
    /// Wraps the vanilla FrameProfilerUtil to allow toggling slow-tick logging without fighting the base game's settings.
    /// Restores the original profiler state on dispose.
    /// </summary>
    public class FrameProfilerController : IDisposable
    {
        private readonly ICoreServerAPI api;
        private FrameProfilerUtil profiler;
        private bool activated;

        private bool? originalEnabled;
        private bool? originalPrintSlow;
        private int? originalThreshold;

        public FrameProfilerController(ICoreServerAPI api)
        {
            this.api = api;
            profiler = api?.World?.FrameProfiler;
        }

        public bool Available => ResolveProfiler() != null;

        public bool Enable(int? threshold = null)
        {
            var target = ResolveProfiler();
            if (target == null)
            {
                api?.Logger.Warning("[Tungsten] FrameProfiler not available on this server - vanilla profiler controls skipped");
                return false;
            }

            try
            {
                if (originalEnabled == null)
                {
                    originalEnabled = target.Enabled;
                    originalPrintSlow = target.PrintSlowTicks;
                    originalThreshold = target.PrintSlowTicksThreshold;
                }

                if (threshold.HasValue)
                {
                    target.PrintSlowTicksThreshold = threshold.Value;
                }

                target.PrintSlowTicks = true;
                target.Enabled = true;
                activated = true;
                return true;
            }
            catch (Exception ex)
            {
                api?.Logger.Error($"[Tungsten] Failed to enable FrameProfiler: {ex.Message}");
                return false;
            }
        }

        public bool Disable()
        {
            if (!activated) return false;

            var target = ResolveProfiler();
            if (target == null)
            {
                api?.Logger.Warning("[Tungsten] FrameProfiler missing while disabling - vanilla profiler state could not be restored");
                activated = false;
                return false;
            }

            try
            {
                if (originalEnabled.HasValue)
                {
                    target.Enabled = originalEnabled.Value;
                    target.PrintSlowTicks = originalPrintSlow.GetValueOrDefault(false);
                    if (originalThreshold.HasValue)
                        target.PrintSlowTicksThreshold = originalThreshold.Value;
                }
                else
                {
                    target.Enabled = false;
                    target.PrintSlowTicks = false;
                }

                activated = false;
                return true;
            }
            catch (Exception ex)
            {
                api?.Logger.Error($"[Tungsten] Failed to disable FrameProfiler cleanly: {ex.Message}");
                return false;
            }
        }

        public string Status()
        {
            var target = ResolveProfiler();
            if (target == null) return "Unavailable (no FrameProfiler instance)";

            return $"Enabled={target.Enabled}, SlowTicks={target.PrintSlowTicks}, Threshold={target.PrintSlowTicksThreshold} ms";
        }

        public void Dispose()
        {
            if (activated)
            {
                Disable();
            }
        }

        private FrameProfilerUtil ResolveProfiler()
        {
            if (profiler != null) return profiler;

            profiler = api?.World?.FrameProfiler;
            return profiler;
        }
    }
}
