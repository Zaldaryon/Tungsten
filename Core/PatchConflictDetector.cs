using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Server;

namespace Tungsten
{
    /// <summary>
    /// Detects potential Harmony patch conflicts between mods at startup.
    /// Reports: multiple transpilers on same method, multiple prefixes (potential skip conflicts).
    /// Zero runtime cost — runs once after all mods have loaded.
    /// </summary>
    public static class PatchConflictDetector
    {
        public static List<string> Run(ICoreServerAPI api)
        {
            var warnings = new List<string>();

            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;

                string methodName = $"{method.DeclaringType?.Name}.{method.Name}";

                // Multiple transpilers on same method = potential IL conflict
                if (info.Transpilers.Count > 1)
                {
                    var owners = info.Transpilers.Select(p => p.owner).Distinct().ToList();
                    if (owners.Count > 1)
                    {
                        string msg = $"[Tungsten] [PatchConflict] {methodName}: {info.Transpilers.Count} transpilers from [{string.Join(", ", owners)}]";
                        warnings.Add(msg);
                        api.Logger.Warning(msg);
                    }
                }

                // Multiple prefixes from different mods = potential skip conflict
                if (info.Prefixes.Count > 1)
                {
                    var owners = info.Prefixes.Select(p => p.owner).Distinct().ToList();
                    if (owners.Count > 1)
                    {
                        string msg = $"[Tungsten] [PatchConflict] {methodName}: {info.Prefixes.Count} prefixes from [{string.Join(", ", owners)}]";
                        warnings.Add(msg);
                        api.Logger.Notification(msg);
                    }
                }
            }

            if (warnings.Count == 0)
            {
                api.Logger.Notification("[Tungsten] [PatchConflict] No multi-mod patch conflicts detected.");
            }
            else
            {
                api.Logger.Warning($"[Tungsten] [PatchConflict] {warnings.Count} potential conflict(s) found. Use /tungsten health for details.");
            }

            return warnings;
        }
    }
}
