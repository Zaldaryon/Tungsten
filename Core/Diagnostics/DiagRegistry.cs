using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten.Diagnostics
{
    public static class DiagRegistry
    {
        private static readonly Dictionary<string, IDiagModule> modules = new(StringComparer.OrdinalIgnoreCase);
        private static Timer autoDumpTimer;
        private static ICoreServerAPI cachedApi;
        private const int AutoDumpIntervalMs = 300_000; // 5 minutes

        public static void Register(IDiagModule module)
        {
            modules[module.ShortName] = module;
        }

        public static IDiagModule Get(string shortName)
        {
            modules.TryGetValue(shortName, out var m);
            return m;
        }

        public static IEnumerable<IDiagModule> All => modules.Values;
        public static int Count => modules.Count;

        public static void SetApi(ICoreServerAPI api)
        {
            cachedApi = api;
        }

        public static void EnableAll()
        {
            foreach (var m in modules.Values) m.Enable();
            StartAutoDump();
            cachedApi?.Logger.Notification("[Tungsten] [Diagnostics] All modules enabled - auto-dump every 5 min");
        }

        public static void DisableAll()
        {
            foreach (var m in modules.Values) m.Disable();
            StopAutoDump();
            cachedApi?.Logger.Notification("[Tungsten] [Diagnostics] All modules disabled - auto-dump stopped");
        }

        public static void ResetAll()
        {
            foreach (var m in modules.Values) m.Reset();
            cachedApi?.Logger.Notification("[Tungsten] [Diagnostics] All modules reset");
        }

        public static void DumpAll(ICoreServerAPI api, IServerPlayer caller)
        {
            int dumped = 0;
            foreach (var m in modules.Values)
            {
                if (m.Enabled)
                {
                    m.Dump(api, caller);
                    dumped++;
                }
            }
            if (dumped == 0)
                DiagLog.Line(api, caller, "No enabled modules to dump. Use: /tungsten diag all on");
        }

        public static void Clear()
        {
            StopAutoDump();
            modules.Clear();
        }

        private static void StartAutoDump()
        {
            StopAutoDump();
            autoDumpTimer = new Timer(_ => AutoDump(), null, AutoDumpIntervalMs, AutoDumpIntervalMs);
        }

        private static void StopAutoDump()
        {
            autoDumpTimer?.Dispose();
            autoDumpTimer = null;
        }

        private static void AutoDump()
        {
            if (cachedApi == null) return;
            try
            {
                cachedApi.Logger.Notification("[Tungsten] [Diagnostics] === Auto-dump (periodic) ===");
                foreach (var m in modules.Values)
                {
                    if (m.Enabled)
                        m.Dump(cachedApi, null); // null caller = log only, no chat
                }
            }
            catch { }
        }

        public static void Dispose()
        {
            StopAutoDump();
            cachedApi = null;
        }
    }
}
