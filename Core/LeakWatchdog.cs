using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Tungsten
{
    /// <summary>
    /// Periodic watchdog that detects mods leaking event handlers.
    /// Counts GameTickListeners per mod assembly every 60s, warns on monotonic growth.
    /// Zero performance impact — iterates handler lists (typically 1-5K entries) once per minute.
    /// </summary>
    public sealed class LeakWatchdog : IDisposable
    {
        private readonly ICoreServerAPI api;
        private readonly Timer timer;
        private readonly Dictionary<string, int> previousCounts = new();
        private readonly Dictionary<string, int> growthStreak = new();
        private Dictionary<Assembly, string> assemblyToMod;
        private readonly object syncLock = new();

        private const int CheckIntervalMs = 60_000;
        private const int GrowthThreshold = 3; // 3 consecutive increases = leak warning

        public LeakWatchdog(ICoreServerAPI api)
        {
            this.api = api;
            BuildAssemblyMap();
            timer = new Timer(Check, null, CheckIntervalMs, CheckIntervalMs);
        }

        private void BuildAssemblyMap()
        {
            assemblyToMod = new Dictionary<Assembly, string>();
            foreach (var mod in api.ModLoader.Mods)
            {
                foreach (var system in mod.Systems)
                {
                    var asm = system.GetType().Assembly;
                    assemblyToMod.TryAdd(asm, mod.Info.ModID);
                }
            }
        }

        private void Check(object state)
        {
            try
            {
                var currentCounts = CountHandlersByMod();

                lock (syncLock)
                {
                    foreach (var (modId, count) in currentCounts)
                    {
                        if (previousCounts.TryGetValue(modId, out int prev) && count > prev)
                        {
                            growthStreak.TryGetValue(modId, out int streak);
                            growthStreak[modId] = streak + 1;

                            if (growthStreak[modId] == GrowthThreshold)
                            {
                                api.Logger.Warning(
                                    $"[Tungsten] [LeakWatchdog] Mod '{modId}' handler count growing: {prev} → {count} " +
                                    $"({GrowthThreshold} consecutive increases). Possible event handler leak.");
                            }
                        }
                        else
                        {
                            growthStreak[modId] = 0;
                        }
                    }

                    previousCounts.Clear();
                    foreach (var kv in currentCounts)
                        previousCounts[kv.Key] = kv.Value;
                }
            }
            catch { } // Watchdog must never crash the server
        }

        private Dictionary<string, int> CountHandlersByMod()
        {
            var counts = new Dictionary<string, int>();

            // Access EventManager's listener lists via reflection
            var eventManager = api.Event;
            var emType = eventManager.GetType();

            // Walk up to find the actual EventManager with the lists
            var fields = GetListenerFields(emType);

            foreach (var field in fields)
            {
                var list = field.GetValue(eventManager);
                if (list == null) continue;

                CountDelegatesInList(list, counts);
            }

            return counts;
        }

        private static FieldInfo[] GetListenerFields(Type type)
        {
            var fields = new List<FieldInfo>();
            var current = type;
            while (current != null)
            {
                foreach (var f in current.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (f.FieldType.IsGenericType &&
                        f.FieldType.GetGenericTypeDefinition() == typeof(List<>) &&
                        f.Name.Contains("Listener", StringComparison.OrdinalIgnoreCase))
                    {
                        fields.Add(f);
                    }
                }
                current = current.BaseType;
            }
            return fields.ToArray();
        }

        private void CountDelegatesInList(object list, Dictionary<string, int> counts)
        {
            // List<GameTickListener> or similar — iterate via IList
            if (list is not System.Collections.IList ilist) return;

            for (int i = 0; i < ilist.Count; i++)
            {
                var item = ilist[i];
                if (item == null) continue;

                // Get the Handler delegate field
                var handlerField = item.GetType().GetField("Handler",
                    BindingFlags.Public | BindingFlags.Instance);
                if (handlerField == null) continue;

                var handler = handlerField.GetValue(item) as Delegate;
                if (handler == null) continue;

                string modId = ResolveModId(handler);
                counts.TryGetValue(modId, out int c);
                counts[modId] = c + 1;
            }
        }

        private string ResolveModId(Delegate handler)
        {
            var asm = handler.Method.DeclaringType?.Assembly;
            if (asm != null && assemblyToMod.TryGetValue(asm, out string modId))
                return modId;
            return "game";
        }

        /// <summary>
        /// Returns current handler counts by mod for the /tungsten health command.
        /// </summary>
        public Dictionary<string, int> GetCurrentCounts()
        {
            return CountHandlersByMod();
        }

        public void Dispose()
        {
            timer?.Dispose();
        }
    }
}
