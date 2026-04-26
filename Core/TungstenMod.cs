using HarmonyLib;
using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Tungsten.Optimizations;

namespace Tungsten
{
    public class TungstenMod : ModSystem
    {
        public static TungstenMod Instance { get; private set; }
        public ICoreServerAPI Api { get; private set; }
        public EntityListReuseOptimizer EntityListOptimizer { get; private set; }
        public BlockListReuseOptimizer BlockListOptimizer { get; private set; }
        public GetDropsListOptimizer GetDropsListOptimizer { get; private set; }
        private FrameProfilerController frameProfiler;
        private TungstenConfig config;
        private readonly object configLock = new object();
        private Harmony harmony;
        private TungstenMonitor monitor;
        private TungstenBenchmarkHarness benchmarkHarness;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            Instance = this;
            Api = api;

            config = api.LoadModConfig<TungstenConfig>("tungsten.json") ?? new TungstenConfig();
            frameProfiler = new FrameProfilerController(api);
            
            api.StoreModConfig(config, "tungsten.json");

            // v1.10.0: Update cached config values in ThreadLocalHelper for optimal performance
            ThreadLocalHelper.UpdateConfig(
                config.TargetCollectionCapacity,
                config.TrimCheckInterval,
                config.EnableCapacityTrimming,
                config.EnableThreadLocalLifecycleReset
            );
            ReusableCollectionPool.UpdateConfig(
                config.EnableReusableCollectionPoolConcurrentOptimization,
                config.EnableReusableCollectionPoolConstructorCacheOptimization
            );
            OptimizationRuntimeCircuitBreaker.UpdateConfig(config.EnableUnifiedRuntimeCircuitBreaker);

            // P1 safety: reset lifecycle state on startup. If this fails, force vanilla allocation fallback.
            if (config.EnableThreadLocalLifecycleReset)
            {
                if (!ThreadLocalHelper.TryResetDisposing())
                {
                    ThreadLocalHelper.ForceVanillaFallback("startup lifecycle reset failed");
                    config.EnableThreadLocalLifecycleReset = false;
                    api.StoreModConfig(config, "tungsten.json");
                    Api.Logger.Warning("[Tungsten] [ThreadLocalLifecycleReset] Disabled automatically after startup failure; vanilla fallback mode enabled");
                }
            }

            // P2 safety: reset reusable-collection runtime state. If this fails, force vanilla allocation fallback.
            if (config.EnableReusableCollectionPoolConcurrentOptimization)
            {
                if (!ReusableCollectionPool.TryResetRuntimeState())
                {
                    ReusableCollectionPool.ForceVanillaFallback("startup runtime reset failed");
                    config.EnableReusableCollectionPoolConcurrentOptimization = false;
                    api.StoreModConfig(config, "tungsten.json");
                    Api.Logger.Warning("[Tungsten] [ReusableCollectionPool] Concurrent optimization disabled automatically after startup failure; vanilla fallback mode enabled");
                }
            }

            // P3 safety: reset constructor-cache runtime state. If this fails, force Activator fallback.
            if (config.EnableReusableCollectionPoolConstructorCacheOptimization)
            {
                if (!ReusableCollectionPool.TryResetConstructorCacheState())
                {
                    ReusableCollectionPool.ForceConstructorCacheFallback("startup constructor-cache reset failed");
                    config.EnableReusableCollectionPoolConstructorCacheOptimization = false;
                    api.StoreModConfig(config, "tungsten.json");
                    Api.Logger.Warning("[Tungsten] [ReusableCollectionPoolCtorCache] Constructor-cache optimization disabled automatically after startup failure; Activator fallback mode enabled");
                }
            }

            // P4 safety: reset global runtime circuit-breaker state.
            if (config.EnableUnifiedRuntimeCircuitBreaker)
            {
                if (!OptimizationRuntimeCircuitBreaker.TryResetState())
                {
                    config.EnableUnifiedRuntimeCircuitBreaker = false;
                    OptimizationRuntimeCircuitBreaker.UpdateConfig(false);
                    api.StoreModConfig(config, "tungsten.json");
                    Api.Logger.Warning("[Tungsten] [RuntimeCircuitBreaker] Disabled automatically after startup failure");
                }
            }

            // P9 safety: validate IL signature manifest per version before patching.
            if (config.EnableIlSignatureManifestValidation)
            {
                var ilResult = OptimizationIlSignatureManifestValidator.ValidateAndApply(api, config);
                if (ilResult.ManifestUnavailableForVersion)
                {
                    config.EnableIlSignatureManifestValidation = false;
                    api.StoreModConfig(config, "tungsten.json");
                    Api.Logger.Warning("[Tungsten] [ILSignatureManifest] Disabled automatically: no built-in manifest for current game version");
                }
                else if (ilResult.DisabledOptimizations > 0)
                {
                    api.StoreModConfig(config, "tungsten.json");
                }
            }

            benchmarkHarness = new TungstenBenchmarkHarness(api, () => config, OnBenchmarkHarnessFailure);
            if (config.EnableBenchmarkHarness)
            {
                if (!benchmarkHarness.TryStart())
                {
                    config.EnableBenchmarkHarness = false;
                    api.StoreModConfig(config, "tungsten.json");
                    Api.Logger.Warning("[Tungsten] [BenchmarkHarness] Disabled automatically after startup failure");
                }
            }

            harmony = new Harmony("com.tungsten.optimizations");

            TungstenProfiler.Init();

            // Initialize advanced monitoring if enabled (v1.10.0)
            if (config.EnableAdvancedMonitoring)
            {
                monitor = new TungstenMonitor(api);
                monitor.StartAdvancedMonitoring();
            }

            // Optional FrameProfiler integration (vanilla profiler)
            if (config.EnableFrameProfiler)
            {
                if (!frameProfiler.Enable(config.FrameProfilerSlowTickThreshold))
                {
                    Api.Logger.Warning("[Tungsten] FrameProfiler enable requested but unavailable; leaving disabled");
                }
                else
                {
                    Api.Logger.Notification($"[Tungsten] FrameProfiler slow-tick logging enabled (threshold {config.FrameProfilerSlowTickThreshold} ms)");
                }
            }

            if (config.EnableEntityListReuse)
            {
                try
                {
                    EntityListOptimizer = new EntityListReuseOptimizer(api);
                    EntityListOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    EntityListOptimizer?.CleanupOnFailure();
                    EntityListOptimizer = null;
                    Api.Logger.Error("[Tungsten] [EntityListReuse] " + ex.Message);
                }
            }

            if (config.EnableBlockListReuse)
            {
                try
                {
                    BlockListOptimizer = new BlockListReuseOptimizer(api);
                    BlockListOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    BlockListOptimizer?.CleanupOnFailure();
                    BlockListOptimizer = null;
                    Api.Logger.Error("[Tungsten] [BlockListReuse] " + ex.Message);
                }
            }

            if (config.EnableGetDropsListReuse)
            {
                try
                {
                    GetDropsListOptimizer = new GetDropsListOptimizer(api);
                    GetDropsListOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    GetDropsListOptimizer?.CleanupOnFailure();
                    GetDropsListOptimizer = null;
                    Api.Logger.Error("[Tungsten] [GetDropsListReuse] " + ex.Message);
                }
            }

            if (config.EnableEventManagerListReuse)
            {
                try
                {
                    EventManagerListOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    EventManagerListOptimizer.CleanupOnFailure();
                    Api.Logger.Error("[Tungsten] [EventManagerListReuse] " + ex.Message);
                }
            }

            if (config.EnableChunkLoadingOptimization)
            {
                ChunkLoadingOptimizer chunkLoadingOptimizer = null;
                try
                {
                    chunkLoadingOptimizer = new ChunkLoadingOptimizer(api);
                    chunkLoadingOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    chunkLoadingOptimizer?.CleanupOnFailure();
                    Api.Logger.Error("[Tungsten] [ChunkLoadingOptimization] " + ex.Message);
                }
            }

            if (config.EnableChunkUnloadingOptimization)
            {
                ChunkUnloadingOptimizer chunkUnloadingOptimizer = null;
                try
                {
                    chunkUnloadingOptimizer = new ChunkUnloadingOptimizer(api);
                    chunkUnloadingOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    chunkUnloadingOptimizer?.CleanupOnFailure();
                    Api.Logger.Error("[Tungsten] [ChunkUnloadingOptimization] " + ex.Message);
                }
            }

            if (config.EnableEntitySimulationOptimization)
            {
                if (EntityListReuseOptimizer.TickEntitiesPatched)
                {
                    Api.Logger.Notification("[Tungsten] [EntitySimulationOptimization] Skipped - TickEntities already patched by EntityListReuseOptimizer");
                }
                else
                {
                    EntitySimulationOptimizer entitySimulationOptimizer = null;
                    try
                    {
                        entitySimulationOptimizer = new EntitySimulationOptimizer(api);
                        entitySimulationOptimizer.ApplyPatches(harmony);                }
                    catch (Exception ex)
                    {
                        entitySimulationOptimizer?.CleanupOnFailure();
                        Api.Logger.Error("[Tungsten] [EntitySimulationOptimization] " + ex.Message);
                    }
                }
            }

            if (config.EnableCookingContainerOptimization)
            {
                CookingContainerOptimizer cookingContainerOptimizer = null;
                try
                {
                    cookingContainerOptimizer = new CookingContainerOptimizer(api);
                    cookingContainerOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    cookingContainerOptimizer?.CleanupOnFailure();
                    Api.Logger.Error("[Tungsten] [CookingContainerOptimization] " + ex.Message);
                }
            }

            if (config.EnableContainerOptimization)
            {
                ContainerOptimizer containerOptimizer = null;
                try
                {
                    containerOptimizer = new ContainerOptimizer(api);
                    containerOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    containerOptimizer?.CleanupOnFailure();
                    Api.Logger.Error("[Tungsten] [ContainerOptimization] " + ex.Message);
                }
            }

            if (config.EnableGridRecipeOptimization)
            {
                GridRecipeOptimizer gridRecipeOptimizer = null;
                try
                {
                    gridRecipeOptimizer = new GridRecipeOptimizer(api);
                    gridRecipeOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    gridRecipeOptimizer?.CleanupOnFailure();
                    Api.Logger.Error("[Tungsten] [GridRecipeOptimization] " + ex.Message);
                }
            }

            if (config.EnableDepositGeneratorOptimization)
            {
                DepositGeneratorOptimizer depositGeneratorOptimizer = null;
                try
                {
                    depositGeneratorOptimizer = new DepositGeneratorOptimizer(api);
                    depositGeneratorOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    depositGeneratorOptimizer?.CleanupOnFailure();
                    Api.Logger.Error("[Tungsten] [DepositGeneratorOptimization] " + ex.Message);
                }
            }

            if (config.EnableSendPlayerEntityDeathsOptimization)
            {
                SendPlayerEntityDeathsOptimizer sendPlayerEntityDeathsOptimizer = null;
                try
                {
                    sendPlayerEntityDeathsOptimizer = new SendPlayerEntityDeathsOptimizer(api);
                    sendPlayerEntityDeathsOptimizer.ApplyPatches(harmony);                }
                catch (Exception ex)
                {
                    sendPlayerEntityDeathsOptimizer?.CleanupOnFailure();
                    Api.Logger.Error("[Tungsten] [SendPlayerEntityDeathsOptimization] " + ex.Message);
                }
            }
            // Advanced GC Optimizations (v1.11.0)
            if (config.EnablePhysicsManagerListOptimization)
            {
                try
                {
                    PhysicsManagerListOptimizer.Initialize(api, harmony);                }
                catch (Exception ex)
                {
                    Api.Logger.Error("[Tungsten] [PhysicsManagerListOptimization] " + ex.Message);
                }
            }

            if (config.EnablePhysicsManagerMethodListOptimization)
            {
                try
                {
                    PhysicsManagerMethodListOptimizer.Initialize(api, harmony);                }
                catch (Exception ex)
                {
                    Api.Logger.Error("[Tungsten] [PhysicsManagerMethodListOptimization] " + ex.Message);
                }
            }

            if (config.EnableServerMainLinqOptimization)
            {
                try
                {
                    ServerMainLinqOptimizer.Initialize(api, harmony);                }
                catch (Exception ex)
                {
                    Api.Logger.Error("[Tungsten] [ServerMainLinqOptimization] " + ex.Message);
                }
            }

            if (config.EnablePlaceholderOptimization)
            {
                try
                {
                    PlaceholderOptimization.Initialize(api, harmony);
                }
                catch (Exception ex)
                {
                    Api.Logger.Error("[Tungsten] [PlaceholderOptimization] " + ex.Message);
                }
            }

            if (config.EnableWildcardFastMatchOptimization)
            {
                try
                {
                    WildcardFastMatchOptimization.Initialize(api, harmony);
                }
                catch (Exception ex)
                {
                    Api.Logger.Error("[Tungsten] [WildcardFastMatchOptimization] " + ex.Message);
                }
            }

            // v1.3.0: New optimizations
            GetEntitiesAroundOptimizer getEntitiesAroundOptimizer = null;
            if (config.EnableGetEntitiesAroundOptimization)
            {
                try
                {
                    getEntitiesAroundOptimizer = new GetEntitiesAroundOptimizer(api);
                    getEntitiesAroundOptimizer.ApplyPatches(harmony);
                }
                catch (Exception ex)
                {
                    getEntitiesAroundOptimizer?.CleanupOnFailure();
                    getEntitiesAroundOptimizer = null;
                    Api.Logger.Error("[Tungsten] [GetEntitiesAroundOptimization] " + ex.Message);
                }
            }

            if (config.EnableEntityDespawnPacketOptimization)
            {
                try
                {
                    EntityDespawnPacketOptimizer.Initialize(api, harmony);
                }
                catch (Exception ex)
                {
                    Api.Logger.Error("[Tungsten] [EntityDespawnPacketOptimization] " + ex.Message);
                }
            }

            if (config.EnableRecipeBaseLinqOptimization)
            {
                try
                {
                    RecipeBaseLinqOptimizer.Initialize(api, harmony);
                }
                catch (Exception ex)
                {
                    Api.Logger.Error("[Tungsten] [RecipeBaseLinqOptimization] " + ex.Message);
                }
            }

            var tungstenCommand = new TungstenCommand(this, config);

            var onOff = new string[] { "on", "off" };
            api.ChatCommands.Create("tungsten")
                .WithDescription("Tungsten performance optimization controls")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("all")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("stats")
                    .WithArgs(api.ChatCommands.Parsers.OptionalWordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("reload")
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("entitylistreuse")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("physicspacketlistreuse")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("blocklistreuse")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("getdropslistreuse")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("eventmanagerlistreuse")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("chunkloadingoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("chunkunloadingoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("entitysimulationoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("cookingcontaineroptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("containeroptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("gridrecipeoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("depositgeneratoroptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("sendplayerentitydeathsoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("physicsmanagerlistoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("physicsmanagermethodlistoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("servermainlinqoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("placeholderoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("wildcardfastmatchoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("threadlocallifecyclereset")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("reusablecollectionpoolconcurrentoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("reusablecollectionpoolconstructorcacheoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("unifiedruntimecircuitbreaker")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("ilsignaturemanifestvalidation")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("benchmarkharness")
                    .WithArgs(api.ChatCommands.Parsers.OptionalWordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("frameprofiler")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff), api.ChatCommands.Parsers.OptionalInt("thresholdMs"))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("getentitiesaroundoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("entitydespawnpacketoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .BeginSubCommand("recipebaselinqoptimization")
                    .WithArgs(api.ChatCommands.Parsers.WordRange("action", onOff))
                    .HandleWith(args => tungstenCommand.Execute(args))
                .EndSubCommand()
                .HandleWith(args => tungstenCommand.Execute(args));
            int enabled = 0;
            var disabled = new System.Collections.Generic.List<string>();
            
            if (config.EnableEntityListReuse) enabled++; else disabled.Add("EntityListReuse");
            if (config.EnableBlockListReuse) enabled++; else disabled.Add("BlockListReuse");
            if (config.EnableGetDropsListReuse) enabled++; else disabled.Add("GetDropsListReuse");
            if (config.EnableEventManagerListReuse) enabled++; else disabled.Add("EventManagerListReuse");
            if (config.EnableChunkLoadingOptimization) enabled++; else disabled.Add("ChunkLoadingOptimization");
            if (config.EnableChunkUnloadingOptimization) enabled++; else disabled.Add("ChunkUnloadingOptimization");
            if (config.EnableEntitySimulationOptimization) enabled++; else disabled.Add("EntitySimulationOptimization");
            if (config.EnableCookingContainerOptimization) enabled++; else disabled.Add("CookingContainerOptimization");
            if (config.EnableContainerOptimization) enabled++; else disabled.Add("ContainerOptimization");
            if (config.EnableGridRecipeOptimization) enabled++; else disabled.Add("GridRecipeOptimization");
            if (config.EnableDepositGeneratorOptimization) enabled++; else disabled.Add("DepositGeneratorOptimization");
            if (config.EnableSendPlayerEntityDeathsOptimization) enabled++; else disabled.Add("SendPlayerEntityDeathsOptimization");
            if (config.EnablePhysicsManagerListOptimization) enabled++; else disabled.Add("PhysicsManagerListOptimization");
            if (config.EnablePhysicsManagerMethodListOptimization) enabled++; else disabled.Add("PhysicsManagerMethodListOptimization");
            if (config.EnableServerMainLinqOptimization) enabled++; else disabled.Add("ServerMainLinqOptimization");
            if (config.EnablePlaceholderOptimization) enabled++; else disabled.Add("PlaceholderOptimization");
            if (config.EnableWildcardFastMatchOptimization) enabled++; else disabled.Add("WildcardFastMatchOptimization");
            if (config.EnableGetEntitiesAroundOptimization) enabled++; else disabled.Add("GetEntitiesAroundOptimization");
            if (config.EnableEntityDespawnPacketOptimization) enabled++; else disabled.Add("EntityDespawnPacketOptimization");
            if (config.EnableRecipeBaseLinqOptimization) enabled++; else disabled.Add("RecipeBaseLinqOptimization");

            string msg = $"[Tungsten] Initialized ({enabled}/20 optimizations enabled)";
            if (disabled.Count > 0) msg += $" - Disabled: {string.Join(", ", disabled)}";
            Api.Logger.Notification(msg);
        }

        public override void Dispose()
        {
            monitor?.Dispose();
            benchmarkHarness?.Dispose();
            frameProfiler?.Dispose();
            harmony?.UnpatchAll("com.tungsten.optimizations");

            // v1.10.2: Dispose all static optimizers to prevent memory leaks on reload
            PhysicsManagerListOptimizer.Dispose();
            PhysicsManagerMethodListOptimizer.Dispose();
            ServerMainLinqOptimizer.Dispose();
            PlaceholderOptimization.Dispose();
            WildcardFastMatchOptimization.Dispose();
            EntityDespawnPacketOptimizer.Dispose();
            RecipeBaseLinqOptimizer.Dispose();

            // Dispose all instance optimizers
            EntityListOptimizer?.CleanupOnFailure();
            BlockListOptimizer?.CleanupOnFailure();
            GetDropsListOptimizer?.CleanupOnFailure();

            // v1.10.3: Notify ThreadLocalHelper BEFORE disposing ThreadLocal instances to prevent race conditions
            ThreadLocalHelper.NotifyDisposing();

            // Dispose all ThreadLocal instances to prevent resource leaks (v1.10.0)
            int threadLocalCount = ThreadLocalRegistry.DisposeAll();
            Api?.Logger.Debug($"[Tungsten] Disposed {threadLocalCount} ThreadLocal instances");

            Api?.Logger.Debug("[Tungsten] Mod disposed");
            Instance = null;
        }

        public TungstenConfig GetConfig()
        {
            lock (configLock)
            {
                return config;
            }
        }

        public void ForceMonitorReport()
        {
            monitor?.ForceReport();
        }

        public void StartAdvancedMonitoring()
        {
            monitor?.StartAdvancedMonitoring();
        }

        public void StopAdvancedMonitoring()
        {
            monitor?.StopAdvancedMonitoring();
        }

        public TextCommandResult ToggleBenchmarkHarness(bool enable)
        {
            config.EnableBenchmarkHarness = enable;

            if (enable)
            {
                if (benchmarkHarness == null)
                {
                    benchmarkHarness = new TungstenBenchmarkHarness(Api, () => config, OnBenchmarkHarnessFailure);
                }

                if (!benchmarkHarness.TryStart())
                {
                    config.EnableBenchmarkHarness = false;
                    Api.StoreModConfig(config, "tungsten.json");
                    return TextCommandResult.Error("BenchmarkHarness failed to start and was disabled (vanilla behavior preserved).");
                }

                Api.StoreModConfig(config, "tungsten.json");
                return TextCommandResult.Success("BenchmarkHarness enabled (applied immediately).");
            }

            benchmarkHarness?.Stop("disabled by command");
            Api.StoreModConfig(config, "tungsten.json");
            return TextCommandResult.Success("BenchmarkHarness disabled (applied immediately).");
        }

        public string GetBenchmarkHarnessStatus()
        {
            return benchmarkHarness?.GetStatus() ?? "BenchmarkHarness: OFF";
        }

        public TextCommandResult ToggleFrameProfiler(string action, int? thresholdMs)
        {
            if (frameProfiler == null || !frameProfiler.Available)
            {
                return TextCommandResult.Error("FrameProfiler unavailable on this server (vanilla profiler not present).");
            }

            if (action == "on")
            {
                if (thresholdMs.HasValue && thresholdMs.Value > 0)
                {
                    config.FrameProfilerSlowTickThreshold = thresholdMs.Value;
                }

                config.EnableFrameProfiler = true;
                frameProfiler.Enable(config.FrameProfilerSlowTickThreshold);
                Api.StoreModConfig(config, "tungsten.json");
                return TextCommandResult.Success($"FrameProfiler slow-tick logging enabled (threshold {config.FrameProfilerSlowTickThreshold} ms).");
            }

            if (action == "off")
            {
                config.EnableFrameProfiler = false;
                frameProfiler.Disable();
                Api.StoreModConfig(config, "tungsten.json");
                return TextCommandResult.Success("FrameProfiler slow-tick logging disabled (vanilla settings restored).");
            }

            return TextCommandResult.Error("Usage: /tungsten frameprofiler [on|off] [thresholdMs]");
        }

        public string GetFrameProfilerStatus()
        {
            if (frameProfiler == null || !frameProfiler.Available)
                return "FrameProfiler: unavailable";

            return "FrameProfiler: " + frameProfiler.Status();
        }

        /// <summary>
        /// Hot-reload configuration from disk (v1.10.0).
        /// Memory management settings can be changed at runtime.
        /// Optimization toggles require server restart to take effect.
        /// </summary>
        public void ReloadConfig()
        {
            lock (configLock)
            {
                var oldConfig = config;
                config = Api.LoadModConfig<TungstenConfig>("tungsten.json") ?? new TungstenConfig();

                // v1.10.1: Update cached config values immediately (inside lock to prevent race)
                ThreadLocalHelper.UpdateConfig(
                    config.TargetCollectionCapacity,
                    config.TrimCheckInterval,
                    config.EnableCapacityTrimming,
                    config.EnableThreadLocalLifecycleReset
                );
                ReusableCollectionPool.UpdateConfig(
                    config.EnableReusableCollectionPoolConcurrentOptimization,
                    config.EnableReusableCollectionPoolConstructorCacheOptimization
                );
                OptimizationRuntimeCircuitBreaker.UpdateConfig(config.EnableUnifiedRuntimeCircuitBreaker);
                if (config.EnableUnifiedRuntimeCircuitBreaker)
                {
                    OptimizationRuntimeCircuitBreaker.TryResetState();
                }

                // Log changes
                Api.Logger.Notification("[Tungsten] Config reloaded:");

                // Check memory management settings (can change at runtime)

                if (oldConfig.EnableCapacityTrimming != config.EnableCapacityTrimming)
                    Api.Logger.Notification($"  EnableCapacityTrimming: {oldConfig.EnableCapacityTrimming} → {config.EnableCapacityTrimming}");

                if (oldConfig.ObjectPoolMaxSize != config.ObjectPoolMaxSize)
                    Api.Logger.Notification($"  ObjectPoolMaxSize: {oldConfig.ObjectPoolMaxSize} → {config.ObjectPoolMaxSize} (NOTE: Existing pools not affected)");

                if (oldConfig.ObjectPoolShrinkIntervalSeconds != config.ObjectPoolShrinkIntervalSeconds)
                    Api.Logger.Notification($"  ObjectPoolShrinkIntervalSeconds: {oldConfig.ObjectPoolShrinkIntervalSeconds} → {config.ObjectPoolShrinkIntervalSeconds} (NOTE: Existing pools not affected)");

                if (oldConfig.TargetCollectionCapacity != config.TargetCollectionCapacity)
                    Api.Logger.Notification($"  TargetCollectionCapacity: {oldConfig.TargetCollectionCapacity} → {config.TargetCollectionCapacity} ✓ Applied");

                if (oldConfig.TrimCheckInterval != config.TrimCheckInterval)
                    Api.Logger.Notification($"  TrimCheckInterval: {oldConfig.TrimCheckInterval} → {config.TrimCheckInterval} ✓ Applied");

                if (oldConfig.EnableFrameProfiler != config.EnableFrameProfiler ||
                    oldConfig.FrameProfilerSlowTickThreshold != config.FrameProfilerSlowTickThreshold)
                {
                    if (config.EnableFrameProfiler)
                    {
                        frameProfiler?.Enable(config.FrameProfilerSlowTickThreshold);
                        Api.Logger.Notification($"  FrameProfiler: ENABLED (threshold {config.FrameProfilerSlowTickThreshold} ms)");
                    }
                    else
                    {
                        frameProfiler?.Disable();
                        Api.Logger.Notification("  FrameProfiler: DISABLED (vanilla settings restored)");
                    }
                }

                bool benchmarkChanged =
                    oldConfig.EnableBenchmarkHarness != config.EnableBenchmarkHarness ||
                    oldConfig.BenchmarkProfile != config.BenchmarkProfile ||
                    oldConfig.BenchmarkVariant != config.BenchmarkVariant ||
                    oldConfig.BenchmarkSessionDurationSeconds != config.BenchmarkSessionDurationSeconds ||
                    oldConfig.BenchmarkSampleIntervalMs != config.BenchmarkSampleIntervalMs;

                if (benchmarkChanged)
                {
                    benchmarkHarness?.Stop("config reload");
                    if (config.EnableBenchmarkHarness)
                    {
                        benchmarkHarness ??= new TungstenBenchmarkHarness(Api, () => config, OnBenchmarkHarnessFailure);
                        if (!benchmarkHarness.TryStart())
                        {
                            config.EnableBenchmarkHarness = false;
                            Api.Logger.Warning("  BenchmarkHarness: FAILED TO START (auto-disabled, vanilla behavior preserved)");
                        }
                        else
                        {
                            Api.Logger.Notification("  BenchmarkHarness: ENABLED ✓ Applied");
                        }
                    }
                    else
                    {
                        Api.Logger.Notification("  BenchmarkHarness: DISABLED ✓ Applied");
                    }
                }


                // Check if any optimization toggles changed (requires restart)
                bool restartRequired = false;
                restartRequired |= oldConfig.EnableEntityListReuse != config.EnableEntityListReuse;
                restartRequired |= oldConfig.EnableBlockListReuse != config.EnableBlockListReuse;
                restartRequired |= oldConfig.EnableGetDropsListReuse != config.EnableGetDropsListReuse;
                restartRequired |= oldConfig.EnableEventManagerListReuse != config.EnableEventManagerListReuse;
                restartRequired |= oldConfig.EnableChunkLoadingOptimization != config.EnableChunkLoadingOptimization;
                restartRequired |= oldConfig.EnableChunkUnloadingOptimization != config.EnableChunkUnloadingOptimization;
                restartRequired |= oldConfig.EnableEntitySimulationOptimization != config.EnableEntitySimulationOptimization;
                restartRequired |= oldConfig.EnableCookingContainerOptimization != config.EnableCookingContainerOptimization;
                restartRequired |= oldConfig.EnableContainerOptimization != config.EnableContainerOptimization;
                restartRequired |= oldConfig.EnableGridRecipeOptimization != config.EnableGridRecipeOptimization;
                restartRequired |= oldConfig.EnableDepositGeneratorOptimization != config.EnableDepositGeneratorOptimization;
                restartRequired |= oldConfig.EnableSendPlayerEntityDeathsOptimization != config.EnableSendPlayerEntityDeathsOptimization;
                restartRequired |= oldConfig.EnablePhysicsManagerListOptimization != config.EnablePhysicsManagerListOptimization;
                restartRequired |= oldConfig.EnablePhysicsManagerMethodListOptimization != config.EnablePhysicsManagerMethodListOptimization;
                restartRequired |= oldConfig.EnableServerMainLinqOptimization != config.EnableServerMainLinqOptimization;
                restartRequired |= oldConfig.EnablePlaceholderOptimization != config.EnablePlaceholderOptimization;
                restartRequired |= oldConfig.EnableWildcardFastMatchOptimization != config.EnableWildcardFastMatchOptimization;
                restartRequired |= oldConfig.EnableGetEntitiesAroundOptimization != config.EnableGetEntitiesAroundOptimization;
                restartRequired |= oldConfig.EnableEntityDespawnPacketOptimization != config.EnableEntityDespawnPacketOptimization;
                restartRequired |= oldConfig.EnableRecipeBaseLinqOptimization != config.EnableRecipeBaseLinqOptimization;
                restartRequired |= oldConfig.EnableThreadLocalLifecycleReset != config.EnableThreadLocalLifecycleReset;
                restartRequired |= oldConfig.EnableReusableCollectionPoolConcurrentOptimization != config.EnableReusableCollectionPoolConcurrentOptimization;
                restartRequired |= oldConfig.EnableReusableCollectionPoolConstructorCacheOptimization != config.EnableReusableCollectionPoolConstructorCacheOptimization;
                restartRequired |= oldConfig.EnableIlSignatureManifestValidation != config.EnableIlSignatureManifestValidation;

                if (restartRequired)
                {
                    Api.Logger.Warning("  ⚠ Optimization toggle changes detected - SERVER RESTART REQUIRED for these changes to take effect!");
                }

                Api.StoreModConfig(config, "tungsten.json");
            }
        }

        private void OnBenchmarkHarnessFailure(string reason)
        {
            try
            {
                lock (configLock)
                {
                    if (config != null && config.EnableBenchmarkHarness)
                    {
                        config.EnableBenchmarkHarness = false;
                        Api?.StoreModConfig(config, "tungsten.json");
                    }
                }
            }
            catch
            {
                // Suppress to avoid cascading failures during safety fallback.
            }

            Api?.Logger?.Warning("[Tungsten] [BenchmarkHarness] Auto-disabled due to runtime error: " + reason);
        }
    }
}
