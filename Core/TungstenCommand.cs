using Vintagestory.API.Common;
using Vintagestory.API.Server;
using System;
using System.Collections.Generic;
using System.Text;

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
                ["depositgeneratoroptimization"] = (c, v) => c.EnableDepositGeneratorOptimization = v,
                ["sendplayerentitydeathsoptimization"] = (c, v) => c.EnableSendPlayerEntityDeathsOptimization = v,
                ["physicsmanagerlistoptimization"] = (c, v) => c.EnablePhysicsManagerListOptimization = v,
                ["physicsmanagermethodlistoptimization"] = (c, v) => c.EnablePhysicsManagerMethodListOptimization = v,
                ["servermainlinqoptimization"] = (c, v) => c.EnableServerMainLinqOptimization = v,
                ["placeholderoptimization"] = (c, v) => c.EnablePlaceholderOptimization = v,
                ["wildcardfastmatchoptimization"] = (c, v) => c.EnableWildcardFastMatchOptimization = v,
                ["getentitiesaroundoptimization"] = (c, v) => c.EnableGetEntitiesAroundOptimization = v,
                ["entitydespawnpacketoptimization"] = (c, v) => c.EnableEntityDespawnPacketOptimization = v,
                ["recipebaselinqoptimization"] = (c, v) => c.EnableRecipeBaseLinqOptimization = v,
                ["threadlocallifecyclereset"] = (c, v) => c.EnableThreadLocalLifecycleReset = v,
                ["reusablecollectionpoolconcurrentoptimization"] = (c, v) => c.EnableReusableCollectionPoolConcurrentOptimization = v,
                ["reusablecollectionpoolconstructorcacheoptimization"] = (c, v) => c.EnableReusableCollectionPoolConstructorCacheOptimization = v,
                ["unifiedruntimecircuitbreaker"] = (c, v) => c.EnableUnifiedRuntimeCircuitBreaker = v,
                ["ilsignaturemanifestvalidation"] = (c, v) => c.EnableIlSignatureManifestValidation = v,
                ["benchmarkharness"] = (c, v) => c.EnableBenchmarkHarness = v
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

            return TextCommandResult.Error("Usage: /tungsten [<opt>|all|stats|benchmarkharness|reload] [on|off]");
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
            config.EnableDepositGeneratorOptimization = enable;
            config.EnableSendPlayerEntityDeathsOptimization = enable;
            config.EnablePhysicsManagerListOptimization = enable;
            config.EnablePhysicsManagerMethodListOptimization = enable;
            config.EnableServerMainLinqOptimization = enable;
            config.EnablePlaceholderOptimization = enable;
            config.EnableWildcardFastMatchOptimization = enable;
            config.EnableGetEntitiesAroundOptimization = enable;
            config.EnableEntityDespawnPacketOptimization = enable;
            config.EnableRecipeBaseLinqOptimization = enable;

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
            status.AppendLine("Tungsten v1.3.0 - Optimizations:");

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
            status.Append("depositgeneratoroptimization: ").Append(config.EnableDepositGeneratorOptimization ? "ON" : "OFF").AppendLine();
            status.Append("sendplayerentitydeathsoptimization: ").Append(config.EnableSendPlayerEntityDeathsOptimization ? "ON" : "OFF").AppendLine();
            status.Append("physicsmanagerlistoptimization: ").Append(config.EnablePhysicsManagerListOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("physicsmanagermethodlistoptimization: ").Append(config.EnablePhysicsManagerMethodListOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("servermainlinqoptimization: ").Append(config.EnableServerMainLinqOptimization ? "ON" : "OFF").AppendLine();
            status.Append("placeholderoptimization: ").Append(config.EnablePlaceholderOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("wildcardfastmatchoptimization: ").Append(config.EnableWildcardFastMatchOptimization ? "ON" : "OFF").AppendLine();
            status.Append("getentitiesaroundoptimization: ").Append(config.EnableGetEntitiesAroundOptimization ? "ON" : "OFF").Append(" | ");
            status.Append("entitydespawnpacketoptimization: ").Append(config.EnableEntityDespawnPacketOptimization ? "ON" : "OFF").AppendLine();
            status.Append("recipebaselinqoptimization: ").Append(config.EnableRecipeBaseLinqOptimization ? "ON" : "OFF").AppendLine();
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
    }
}
