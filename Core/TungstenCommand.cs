using Vintagestory.API.Common;
using Vintagestory.API.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tungsten.Diagnostics;

namespace Tungsten
{
    public class TungstenCommand
    {
        private readonly TungstenMod mod;
        private static readonly Dictionary<string, Action<TungstenConfig, bool>> OptimizationSetters =
            new Dictionary<string, Action<TungstenConfig, bool>>(StringComparer.Ordinal)
            {
                ["entitylistreuse"] = (c, v) => c.EnableEntityListReuse = v,
                ["physicspacketlistreuse"] = (c, v) => c.EnablePhysicsManagerListOptimization = v,
                ["blocklistreuse"] = (c, v) => c.EnableBlockListReuse = v,
                ["getdropslistreuse"] = (c, v) => c.EnableGetDropsListReuse = v,
                ["eventmanagerlistreuse"] = (c, v) => c.EnableEventManagerListReuse = v,
                ["chunkloadingoptimization"] = (c, v) => c.EnableChunkLoadingOptimization = v,
                ["chunkunloadingoptimization"] = (c, v) => c.EnableChunkUnloadingOptimization = v,
                ["entitysimulationoptimization"] = (c, v) => c.EnableEntitySimulationOptimization = v,
                ["cookingcontaineroptimization"] = (c, v) => c.EnableCookingContainerOptimization = v,
                ["containeroptimization"] = (c, v) => c.EnableContainerOptimization = v,
                ["gridrecipeoptimization"] = (c, v) => c.EnableGridRecipeOptimization = v,
                ["propickreadingoptimization"] = (c, v) => c.EnablePropickReadingOptimization = v,
                ["sendplayerentitydeathsoptimization"] = (c, v) => c.EnableSendPlayerEntityDeathsOptimization = v,
                ["physicsmanagerlistoptimization"] = (c, v) => c.EnablePhysicsManagerListOptimization = v,
                ["physicsmanagermethodlistoptimization"] = (c, v) => c.EnablePhysicsManagerMethodListOptimization = v,
                ["servermainlinqoptimization"] = (c, v) => c.EnableServerMainLinqOptimization = v,
                ["placeholderoptimization"] = (c, v) => c.EnablePlaceholderOptimization = v,
                ["wildcardfastmatchoptimization"] = (c, v) => c.EnableWildcardFastMatchOptimization = v,
                ["getentitiesaroundoptimization"] = (c, v) => c.EnableGetEntitiesAroundOptimization = v,
                ["entitydespawnpacketoptimization"] = (c, v) => c.EnableEntityDespawnPacketOptimization = v,
                ["recipebaselinqoptimization"] = (c, v) => c.EnableRecipeBaseLinqOptimization = v,
                ["broadcastlinqoptimization"] = (c, v) => c.EnableBroadcastLinqOptimization = v,
                ["bulkentityattributespacketoptimization"] = (c, v) => c.EnableBulkEntityAttributesPacketOptimization = v,
                ["classregistryfrozenoptimization"] = (c, v) => c.EnableClassRegistryFrozenOptimization = v,
                ["getplayersaroundoptimization"] = (c, v) => c.EnableGetPlayersAroundOptimization = v,
                ["threadlocallifecyclereset"] = (c, v) => c.EnableThreadLocalLifecycleReset = v,
                ["reusablecollectionpoolconcurrentoptimization"] = (c, v) => c.EnableReusableCollectionPoolConcurrentOptimization = v,
                ["reusablecollectionpoolconstructorcacheoptimization"] = (c, v) => c.EnableReusableCollectionPoolConstructorCacheOptimization = v,
                ["unifiedruntimecircuitbreaker"] = (c, v) => c.EnableUnifiedRuntimeCircuitBreaker = v,
                ["ilsignaturemanifestvalidation"] = (c, v) => c.EnableIlSignatureManifestValidation = v,
                ["benchmarkharness"] = (c, v) => c.EnableBenchmarkHarness = v,
                ["genterrazeroallocoptimization"] = (c, v) => c.EnableGenTerraZeroAllocOptimization = v,
                ["genterrabitarrayoptimization"] = (c, v) => c.EnableGenTerraBitArrayOptimization = v,
                ["depositgeneratoroptimization"] = (c, v) => c.EnablePropickReadingOptimization = v
            };

        public TungstenCommand(TungstenMod mod, TungstenConfig config)
        {
            this.mod = mod;
            _ = config;
        }

        public TextCommandResult Execute(TextCommandCallingArgs args)
        {
            string subCommand = string.IsNullOrWhiteSpace(args.SubCmdCode) ? null : args.SubCmdCode.ToLowerInvariant();
            string firstArg = args.ArgCount > 0 ? (args[0] as string)?.ToLowerInvariant() : null;
            string command = string.IsNullOrWhiteSpace(subCommand) ? firstArg : subCommand;

            if (string.IsNullOrWhiteSpace(command))
            {
                return ShowStatus();
            }

            if (command == "reload")
            {
                mod.ReloadConfig();
                return TextCommandResult.Success("Config reloaded. Check server log for details.");
            }

            if (command == "stats")
            {
                var config = mod.GetConfig();
                if (!string.IsNullOrEmpty(firstArg))
                {
                    if (firstArg == "on")
                    {
                        config.EnableAdvancedMonitoring = true;
                        mod.Api.StoreModConfig(config, "tungsten.json");
                        mod.StartAdvancedMonitoring();
                        return TextCommandResult.Success("Advanced statistics enabled. Statistics will be logged every 30 seconds.");
                    }
                    else if (firstArg == "off")
                    {
                        config.EnableAdvancedMonitoring = false;
                        mod.Api.StoreModConfig(config, "tungsten.json");
                        mod.StopAdvancedMonitoring();
                        return TextCommandResult.Success("Advanced statistics disabled.");
                    }
                }

                if (mod.Api != null)
                {
                    int threads = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
                    long memory = System.GC.GetTotalMemory(false) / 1024 / 1024;
                    int registeredThreadLocals = ThreadLocalRegistry.Count;

                    if (config.EnableAdvancedMonitoring)
                        mod.ForceMonitorReport();

                    return TextCommandResult.Success(
                        $"Tungsten Statistics:\n" +
                        $"  Threads: {threads}\n" +
                        $"  Memory: {memory} MB\n" +
                        $"  ThreadLocal Instances: {registeredThreadLocals}\n" +
                        $"  GC Gen0: {System.GC.CollectionCount(0)}, Gen1: {System.GC.CollectionCount(1)}, Gen2: {System.GC.CollectionCount(2)}\n" +
                        $"  Advanced Statistics: {(config.EnableAdvancedMonitoring ? "ON" : "OFF")}\n" +
                        $"See server log for detailed statistics.");
                }
                return TextCommandResult.Error("Statistics not available");
            }

            if (command == "frameprofiler")
            {
                string action = firstArg;
                if (string.IsNullOrEmpty(action))
                {
                    return TextCommandResult.Success(mod.GetFrameProfilerStatus());
                }

                int? threshold = null;
                if (args.ArgCount >= 2 && args[1] is int parsed && parsed > 0)
                {
                    threshold = parsed;
                }
                return mod.ToggleFrameProfiler(action, threshold);
            }

            if (command == "benchmarkharness")
            {
                string action = firstArg;
                if (action == "on")
                    return mod.ToggleBenchmarkHarness(true);
                if (action == "off")
                    return mod.ToggleBenchmarkHarness(false);
                return TextCommandResult.Success(mod.GetBenchmarkHarnessStatus());
            }

            if (command == "health")
            {
                return ShowHealth();
            }

            if (command == "manifest")
            {
                OptimizationIlSignatureManifestValidator.DumpCurrentHashes(mod.Api);
                return TextCommandResult.Success("IL hashes dumped to server log. Check server-main.txt for output.");
            }

            if (command == "diag")
            {
                return HandleDiag(args);
            }

            if (command == "all")
            {
                string action = firstArg;

                if (action == "on" || action == "off")
                {
                    return ToggleAllOptimizations(action == "on");
                }

                return TextCommandResult.Error("Usage: /tungsten all [on|off]");
            }

            if (!string.IsNullOrEmpty(command))
            {
                string action = firstArg;
                if (action == "on" || action == "off")
                {
                    return ToggleOptimization(command, action == "on");
                }
            }

            return TextCommandResult.Error("Usage: /tungsten [<opt>|all|stats|diag|benchmarkharness|reload] [on|off]");
        }

        private TextCommandResult HandleDiag(TextCommandCallingArgs args)
        {
            var caller = args.Caller?.Player as IServerPlayer;
            string target = args.ArgCount > 0 ? (args[0] as string)?.ToLowerInvariant() : null;
            string action = args.ArgCount > 1 ? (args[1] as string)?.ToLowerInvariant() : null;

            if (string.IsNullOrEmpty(target))
            {
                var sb = new StringBuilder();
                sb.AppendLine("Tungsten Diagnostics — registered modules:");
                foreach (var m in DiagRegistry.All)
                    sb.Append("  ").Append(m.ShortName).Append(": ").AppendLine(m.Enabled ? "ON" : "off");
                sb.AppendLine("Usage: /tungsten diag [all|<module>] [on|off|dump|reset]");
                return TextCommandResult.Success(sb.ToString());
            }

            if (target == "all")
            {
                switch (action)
                {
                    case "on": DiagRegistry.EnableAll(); return TextCommandResult.Success("All diagnostic modules enabled.");
                    case "off": DiagRegistry.DisableAll(); return TextCommandResult.Success("All diagnostic modules disabled.");
                    case "dump": DiagRegistry.DumpAll(mod.Api, caller); return TextCommandResult.Success("Dumped all enabled modules. See chat/log.");
                    case "reset": DiagRegistry.ResetAll(); return TextCommandResult.Success("All diagnostic modules reset.");
                    default: return TextCommandResult.Error("Usage: /tungsten diag all [on|off|dump|reset]");
                }
            }

            var module = DiagRegistry.Get(target);
            if (module == null)
            {
                // Special commands that aren't modules
                if (target == "nativebench")
                {
                    TungstenNativeNoise.RunMicroBenchmark(mod.Api);
                    return TextCommandResult.Success("Native noise benchmark complete. See log.");
                }
                return TextCommandResult.Error($"Unknown diag module: {target}. Use /tungsten diag to list.");
            }

            switch (action)
            {
                case "on":
                    module.Enable();
                    mod.Api.Logger.Notification($"[Tungsten] [Diagnostics] Module '{target}' enabled");
                    return TextCommandResult.Success($"Diag '{target}' enabled.");
                case "off":
                    module.Disable();
                    mod.Api.Logger.Notification($"[Tungsten] [Diagnostics] Module '{target}' disabled");
                    return TextCommandResult.Success($"Diag '{target}' disabled.");
                case "dump": module.Dump(mod.Api, caller); return TextCommandResult.Success($"Dumped '{target}'. See chat/log.");
                case "reset": module.Reset(); return TextCommandResult.Success($"Diag '{target}' reset.");
                default:
                    module.Dump(mod.Api, caller);
                    return TextCommandResult.Success($"Dumped '{target}'. Use on/off/dump/reset.");
            }
        }

        private TextCommandResult ToggleAllOptimizations(bool enable)
        {
            var config = mod.GetConfig();
            config.EnableEntityListReuse = enable;
            config.EnableBlockListReuse = enable;
            config.EnableGetDropsListReuse = enable;
            config.EnableEventManagerListReuse = enable;
            config.EnableChunkLoadingOptimization = enable;
            config.EnableChunkUnloadingOptimization = enable;
            config.EnableEntitySimulationOptimization = enable;
            config.EnableCookingContainerOptimization = enable;
            config.EnableContainerOptimization = enable;
            config.EnableGridRecipeOptimization = enable;
            config.EnablePropickReadingOptimization = enable;
            config.EnableSendPlayerEntityDeathsOptimization = enable;
            config.EnablePhysicsManagerListOptimization = enable;
            config.EnablePhysicsManagerMethodListOptimization = enable;
            config.EnableServerMainLinqOptimization = enable;
            config.EnablePlaceholderOptimization = enable;
            config.EnableWildcardFastMatchOptimization = enable;
            config.EnableGetEntitiesAroundOptimization = enable;
            config.EnableEntityDespawnPacketOptimization = enable;
            config.EnableRecipeBaseLinqOptimization = enable;
            config.EnableBroadcastLinqOptimization = enable;
            config.EnableBulkEntityAttributesPacketOptimization = enable;
            config.EnableClassRegistryFrozenOptimization = enable;
            config.EnableGetPlayersAroundOptimization = enable;
            config.EnableGenTerraZeroAllocOptimization = enable;
            config.EnableGenTerraBitArrayOptimization = enable;

            mod.Api.StoreModConfig(config, "tungsten.json");

            return TextCommandResult.Success(
                $"All optimizations set to {(enable ? "ON" : "OFF")}.\n" +
                "⚠️ SERVER RESTART REQUIRED for changes to take effect.");
        }

        private TextCommandResult ToggleOptimization(string opt, bool enable)
        {
            var config = mod.GetConfig();
            if (!OptimizationSetters.TryGetValue(opt, out var setter))
            {
                return TextCommandResult.Error($"Unknown optimization: {opt}\nUse /tungsten to see available options.");
            }

            if (opt == "unifiedruntimecircuitbreaker")
            {
                setter(config, enable);
                mod.Api.StoreModConfig(config, "tungsten.json");
                OptimizationRuntimeCircuitBreaker.UpdateConfig(enable);
                if (enable)
                    OptimizationRuntimeCircuitBreaker.TryResetState();

                return TextCommandResult.Success(
                    $"Optimization '{opt}' set to {(enable ? "ON" : "OFF")}.\n" +
                    "Applied immediately (no restart required).");
            }

            if (opt == "benchmarkharness")
            {
                setter(config, enable);
                return mod.ToggleBenchmarkHarness(enable);
            }

            setter(config, enable);
            mod.Api.StoreModConfig(config, "tungsten.json");
            return TextCommandResult.Success(
                $"Optimization '{opt}' set to {(enable ? "ON" : "OFF")}.\n" +
                "⚠️ SERVER RESTART REQUIRED for changes to take effect.");
        }

        private TextCommandResult ShowStatus()
        {
            var config = mod.GetConfig();
            var status = new StringBuilder(1024);
            status.AppendLine("Tungsten v1.3.2 - Optimizations:");

            status.Append("entitylistreuse: ").Append(config.EnableEntityListReuse ? "ON" : "OFF").Append(" | ");
            status.Append("blocklistreuse: ").Append(config.EnableBlockListReuse ? "ON" : "OFF").AppendLine();
            status.Append("getdropslistreuse: ").Append(config.EnableGetDropsListReuse ? "ON" : "OFF").AppendLine();
            status.Append("eventmanagerlistreuse: ").Append(config.EnableEventManagerListReuse ? "ON" : "OFF").Append(" | ");
            status.Append("chunkloadingoptimization: ").Append(config.EnableChunkLoadingOptimization ? "ON" : "OFF").AppendLine();
            status.Append("chunkunloadingoptimization: ").Append(config.EnableChunkUnloadingOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("entitysimulationoptimization: ").Append(config.EnableEntitySimulationOptimization ? "ON" : "OFF").AppendLine();
            status.Append("cookingcontaineroptimization: ").Append(config.EnableCookingContainerOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("containeroptimization: ").Append(config.EnableContainerOptimization ? "ON" : "OFF").AppendLine();
            status.Append("gridrecipeoptimization: ").Append(config.EnableGridRecipeOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("propickreadingoptimization: ").Append(config.EnablePropickReadingOptimization ? "ON" : "OFF").AppendLine();
            status.Append("sendplayerentitydeathsoptimization: ").Append(config.EnableSendPlayerEntityDeathsOptimization ? "ON" : "OFF").AppendLine();
            status.Append("physicsmanagerlistoptimization: ").Append(config.EnablePhysicsManagerListOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("physicsmanagermethodlistoptimization: ").Append(config.EnablePhysicsManagerMethodListOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("servermainlinqoptimization: ").Append(config.EnableServerMainLinqOptimization ? "ON" : "OFF").AppendLine();
            status.Append("placeholderoptimization: ").Append(config.EnablePlaceholderOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("wildcardfastmatchoptimization: ").Append(config.EnableWildcardFastMatchOptimization ? "ON" : "OFF").AppendLine();
            status.Append("getentitiesaroundoptimization: ").Append(config.EnableGetEntitiesAroundOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("entitydespawnpacketoptimization: ").Append(config.EnableEntityDespawnPacketOptimization ? "ON" : "OFF").AppendLine();
            status.Append("recipebaselinqoptimization: ").Append(config.EnableRecipeBaseLinqOptimization ? "ON" : "OFF").AppendLine();
            status.Append("broadcastlinqoptimization: ").Append(config.EnableBroadcastLinqOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("bulkentityattributespacketoptimization: ").Append(config.EnableBulkEntityAttributesPacketOptimization ? "ON" : "OFF").AppendLine();
            status.Append("classregistryfrozenoptimization: ").Append(config.EnableClassRegistryFrozenOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("getplayersaroundoptimization: ").Append(config.EnableGetPlayersAroundOptimization ? "ON" : "OFF").AppendLine();
            status.AppendLine();

            status.Append("Memory: AdvancedMonitoring=").Append(config.EnableAdvancedMonitoring ? "ON" : "OFF").Append(", ");
            status.Append("Trimming=").Append(config.EnableCapacityTrimming ? "ON" : "OFF").Append(", ");
            status.Append("LifecycleReset=").Append(config.EnableThreadLocalLifecycleReset ? "ON" : "OFF").Append(", ");
            status.Append("PoolConcurrent=").Append(config.EnableReusableCollectionPoolConcurrentOptimization ? "ON" : "OFF").Append(", ");
            status.Append("PoolCtorCache=").Append(config.EnableReusableCollectionPoolConstructorCacheOptimization ? "ON" : "OFF").Append(", ");
            status.Append("RuntimeCB=").Append(config.EnableUnifiedRuntimeCircuitBreaker ? "ON" : "OFF").Append(", ");
            status.Append("ILManifest=").Append(config.EnableIlSignatureManifestValidation ? "ON" : "OFF").Append(", ");
            status.Append("Capacity=").Append(config.TargetCollectionCapacity).Append(", ");
            status.Append("ThreadLocals=").Append(ThreadLocalRegistry.Count).AppendLine();
            status.Append("Runtime: ").Append(OptimizationRuntimeCircuitBreaker.GetStatusSummary()).AppendLine();
            status.AppendLine(mod.GetBenchmarkHarnessStatus());
            status.Append(mod.GetFrameProfilerStatus());

            return TextCommandResult.Success(status.ToString());
        }

        private TextCommandResult ShowHealth()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Tungsten Multi-Mod Health ===");

            // Patch conflicts
            var conflicts = mod.GetPatchConflicts();
            if (conflicts == null || conflicts.Count == 0)
            {
                sb.AppendLine("Patch Conflicts: None detected");
            }
            else
            {
                sb.AppendLine($"Patch Conflicts: {conflicts.Count} potential issue(s)");
                foreach (var c in conflicts)
                    sb.AppendLine("  " + c.Replace("[Tungsten] [PatchConflict] ", ""));
            }

            // Handler counts by mod
            var watchdog = mod.GetLeakWatchdog();
            if (watchdog != null)
            {
                var counts = watchdog.GetCurrentCounts();
                sb.AppendLine($"Event Handlers by Mod ({counts.Values.Sum()} total):");
                foreach (var kv in counts.OrderByDescending(x => x.Value))
                    sb.AppendLine($"  {kv.Key}: {kv.Value}");
            }

            return TextCommandResult.Success(sb.ToString());
        }
    }
}
