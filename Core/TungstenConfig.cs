using Newtonsoft.Json;
using System.IO;
using Vintagestory.API.Server;

namespace Tungsten;

public class TungstenConfig
{
    // Optimization toggles
    public bool EnableEntityListReuse { get; set; } = true;
    public bool EnableBlockListReuse { get; set; } = true;
    public bool EnableGetDropsListReuse { get; set; } = true;
    public bool EnableEventManagerListReuse { get; set; } = true;
    public bool EnableChunkLoadingOptimization { get; set; } = true;
    public bool EnableChunkUnloadingOptimization { get; set; } = true;
    public bool EnableEntitySimulationOptimization { get; set; } = true;
    public bool EnableCookingContainerOptimization { get; set; } = true;
    public bool EnableContainerOptimization { get; set; } = true;
    public bool EnableGridRecipeOptimization { get; set; } = true;
    public bool EnableDepositGeneratorOptimization { get; set; } = true;
    public bool EnableSendPlayerEntityDeathsOptimization { get; set; } = true;
    public bool EnablePlaceholderOptimization { get; set; } = true;
    public bool EnableWildcardFastMatchOptimization { get; set; } = true;

    // New optimizations (v1.3.0)
    public bool EnableGetEntitiesAroundOptimization { get; set; } = true;
    public bool EnableEntityDespawnPacketOptimization { get; set; } = true;
    public bool EnableRecipeBaseLinqOptimization { get; set; } = true;

    // Advanced GC Optimizations (v1.9.2) - Prioritize GC performance over RAM
    public bool EnablePhysicsManagerListOptimization { get; set; } = true;
    public bool EnablePhysicsManagerMethodListOptimization { get; set; } = true;
    public bool EnableServerMainLinqOptimization { get; set; } = true;

    // Memory Management (v1.10.0)

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool EnableAdvancedMonitoring { get; set; } = false;
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool EnableCapacityTrimming { get; set; } = true;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool EnableThreadLocalLifecycleReset { get; set; } = true;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool EnableReusableCollectionPoolConcurrentOptimization { get; set; } = true;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool EnableReusableCollectionPoolConstructorCacheOptimization { get; set; } = true;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool EnableUnifiedRuntimeCircuitBreaker { get; set; } = true;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool EnableIlSignatureManifestValidation { get; set; } = true;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool EnableBenchmarkHarness { get; set; } = false;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public string BenchmarkProfile { get; set; } = "default";

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public string BenchmarkVariant { get; set; } = "A";

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public int BenchmarkSessionDurationSeconds { get; set; } = 600;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public int BenchmarkSampleIntervalMs { get; set; } = 5000;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public int ObjectPoolMaxSize { get; set; } = 32;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public int ObjectPoolShrinkIntervalSeconds { get; set; } = 60;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public int TargetCollectionCapacity { get; set; } = 200;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public int TrimCheckInterval { get; set; } = 5000;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public bool EnableFrameProfiler { get; set; } = false;

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
    public int FrameProfilerSlowTickThreshold { get; set; } = 40;

    private static string configPath;
    private static ICoreServerAPI api;

    public static TungstenConfig Load(ICoreServerAPI serverApi)
    {
        api = serverApi;
        configPath = Path.Combine(serverApi.GetOrCreateDataPath("ModConfig"), "TungstenConfig.json");

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                return JsonConvert.DeserializeObject<TungstenConfig>(json) ?? new TungstenConfig();
            }
            catch
            {
                api.Logger.Warning("[Tungsten] [Config] Failed to load config, using defaults");
            }
        }

        var config = new TungstenConfig();
        config.Save();
        return config;
    }

    public void Save()
    {
        try
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            File.WriteAllText(configPath, json);
        }
        catch
        {
            api?.Logger.Error("[Tungsten] [Config] Failed to save config");
        }
    }

    public bool SetValue(string category, bool value)
    {
        var changed = false;
        var normalizedCategory = NormalizeCategory(category);
        if (normalizedCategory == null)
            return false;

        switch (normalizedCategory)
        {
            case "entitylistreuse":
                if (EnableEntityListReuse != value)
                {
                    EnableEntityListReuse = value;
                    changed = true;
                }
                break;
            case "physicspacketlistreuse":
            case "physicsmanagerlistoptimization":
                if (EnablePhysicsManagerListOptimization != value)
                {
                    EnablePhysicsManagerListOptimization = value;
                    changed = true;
                }
                break;
            case "blocklistreuse":
                if (EnableBlockListReuse != value)
                {
                    EnableBlockListReuse = value;
                    changed = true;
                }
                break;
            case "getdropslistreuse":
                if (EnableGetDropsListReuse != value)
                {
                    EnableGetDropsListReuse = value;
                    changed = true;
                }
                break;
            case "eventmanagerlistreuse":
                if (EnableEventManagerListReuse != value)
                {
                    EnableEventManagerListReuse = value;
                    changed = true;
                }
                break;
            case "chunkloadingoptimization":
                if (EnableChunkLoadingOptimization != value)
                {
                    EnableChunkLoadingOptimization = value;
                    changed = true;
                }
                break;
            case "chunkunloadingoptimization":
                if (EnableChunkUnloadingOptimization != value)
                {
                    EnableChunkUnloadingOptimization = value;
                    changed = true;
                }
                break;
            case "entitysimulationoptimization":
                if (EnableEntitySimulationOptimization != value)
                {
                    EnableEntitySimulationOptimization = value;
                    changed = true;
                }
                break;
            case "cookingcontaineroptimization":
                if (EnableCookingContainerOptimization != value)
                {
                    EnableCookingContainerOptimization = value;
                    changed = true;
                }
                break;
            case "containeroptimization":
                if (EnableContainerOptimization != value)
                {
                    EnableContainerOptimization = value;
                    changed = true;
                }
                break;
            case "gridrecipeoptimization":
                if (EnableGridRecipeOptimization != value)
                {
                    EnableGridRecipeOptimization = value;
                    changed = true;
                }
                break;
            case "depositgeneratoroptimization":
                if (EnableDepositGeneratorOptimization != value)
                {
                    EnableDepositGeneratorOptimization = value;
                    changed = true;
                }
                break;
            case "sendplayerentitydeathsoptimization":
                if (EnableSendPlayerEntityDeathsOptimization != value)
                {
                    EnableSendPlayerEntityDeathsOptimization = value;
                    changed = true;
                }
                break;
            case "placeholderoptimization":
                if (EnablePlaceholderOptimization != value)
                {
                    EnablePlaceholderOptimization = value;
                    changed = true;
                }
                break;
            case "wildcardfastmatchoptimization":
                if (EnableWildcardFastMatchOptimization != value)
                {
                    EnableWildcardFastMatchOptimization = value;
                    changed = true;
                }
                break;
            case "threadlocallifecyclereset":
                if (EnableThreadLocalLifecycleReset != value)
                {
                    EnableThreadLocalLifecycleReset = value;
                    changed = true;
                }
                break;
            case "reusablecollectionpoolconcurrentoptimization":
                if (EnableReusableCollectionPoolConcurrentOptimization != value)
                {
                    EnableReusableCollectionPoolConcurrentOptimization = value;
                    changed = true;
                }
                break;
            case "reusablecollectionpoolconstructorcacheoptimization":
                if (EnableReusableCollectionPoolConstructorCacheOptimization != value)
                {
                    EnableReusableCollectionPoolConstructorCacheOptimization = value;
                    changed = true;
                }
                break;
            case "unifiedruntimecircuitbreaker":
                if (EnableUnifiedRuntimeCircuitBreaker != value)
                {
                    EnableUnifiedRuntimeCircuitBreaker = value;
                    changed = true;
                }
                break;
            case "ilsignaturemanifestvalidation":
                if (EnableIlSignatureManifestValidation != value)
                {
                    EnableIlSignatureManifestValidation = value;
                    changed = true;
                }
                break;
            case "benchmarkharness":
                if (EnableBenchmarkHarness != value)
                {
                    EnableBenchmarkHarness = value;
                    changed = true;
                }
                break;
            case "getentitiesaroundoptimization":
                if (EnableGetEntitiesAroundOptimization != value)
                {
                    EnableGetEntitiesAroundOptimization = value;
                    changed = true;
                }
                break;
            case "entitydespawnpacketoptimization":
                if (EnableEntityDespawnPacketOptimization != value)
                {
                    EnableEntityDespawnPacketOptimization = value;
                    changed = true;
                }
                break;
            case "recipebaselinqoptimization":
                if (EnableRecipeBaseLinqOptimization != value)
                {
                    EnableRecipeBaseLinqOptimization = value;
                    changed = true;
                }
                break;
        }

        if (changed)
        {
            Save();
        }

        return changed;
    }

    public bool? GetCategoryStatus(string category)
    {
        return NormalizeCategory(category) switch
        {
            "entitylistreuse" => EnableEntityListReuse,
            "blocklistreuse" => EnableBlockListReuse,
            "getdropslistreuse" => EnableGetDropsListReuse,
            "eventmanagerlistreuse" => EnableEventManagerListReuse,
            "chunkloadingoptimization" => EnableChunkLoadingOptimization,
            "chunkunloadingoptimization" => EnableChunkUnloadingOptimization,
            "physicspacketlistreuse" => EnablePhysicsManagerListOptimization,
            "physicsmanagerlistoptimization" => EnablePhysicsManagerListOptimization,
            "entitysimulationoptimization" => EnableEntitySimulationOptimization,
            "cookingcontaineroptimization" => EnableCookingContainerOptimization,
            "sendplayerentitydeathsoptimization" => EnableSendPlayerEntityDeathsOptimization,
            "containeroptimization" => EnableContainerOptimization,
            "gridrecipeoptimization" => EnableGridRecipeOptimization,
            "depositgeneratoroptimization" => EnableDepositGeneratorOptimization,
            "placeholderoptimization" => EnablePlaceholderOptimization,
            "wildcardfastmatchoptimization" => EnableWildcardFastMatchOptimization,
            "threadlocallifecyclereset" => EnableThreadLocalLifecycleReset,
            "reusablecollectionpoolconcurrentoptimization" => EnableReusableCollectionPoolConcurrentOptimization,
            "reusablecollectionpoolconstructorcacheoptimization" => EnableReusableCollectionPoolConstructorCacheOptimization,
            "unifiedruntimecircuitbreaker" => EnableUnifiedRuntimeCircuitBreaker,
            "ilsignaturemanifestvalidation" => EnableIlSignatureManifestValidation,
            "benchmarkharness" => EnableBenchmarkHarness,
            "getentitiesaroundoptimization" => EnableGetEntitiesAroundOptimization,
            "entitydespawnpacketoptimization" => EnableEntityDespawnPacketOptimization,
            "recipebaselinqoptimization" => EnableRecipeBaseLinqOptimization,
            _ => null
        };
    }

    public bool GetValue(string category)
    {
        return NormalizeCategory(category) switch
        {
            "entitylistreuse" => EnableEntityListReuse,
            "blocklistreuse" => EnableBlockListReuse,
            "physicspacketlistreuse" => EnablePhysicsManagerListOptimization,
            "physicsmanagerlistoptimization" => EnablePhysicsManagerListOptimization,
            "getdropslistreuse" => EnableGetDropsListReuse,
            "eventmanagerlistreuse" => EnableEventManagerListReuse,
            "chunkloadingoptimization" => EnableChunkLoadingOptimization,
            "chunkunloadingoptimization" => EnableChunkUnloadingOptimization,
            "entitysimulationoptimization" => EnableEntitySimulationOptimization,
            "containeroptimization" => EnableContainerOptimization,
            "sendplayerentitydeathsoptimization" => EnableSendPlayerEntityDeathsOptimization,
            "gridrecipeoptimization" => EnableGridRecipeOptimization,
            "depositgeneratoroptimization" => EnableDepositGeneratorOptimization,
            "placeholderoptimization" => EnablePlaceholderOptimization,
            "wildcardfastmatchoptimization" => EnableWildcardFastMatchOptimization,
            "threadlocallifecyclereset" => EnableThreadLocalLifecycleReset,
            "reusablecollectionpoolconcurrentoptimization" => EnableReusableCollectionPoolConcurrentOptimization,
            "reusablecollectionpoolconstructorcacheoptimization" => EnableReusableCollectionPoolConstructorCacheOptimization,
            "unifiedruntimecircuitbreaker" => EnableUnifiedRuntimeCircuitBreaker,
            "ilsignaturemanifestvalidation" => EnableIlSignatureManifestValidation,
            "benchmarkharness" => EnableBenchmarkHarness,
            "getentitiesaroundoptimization" => EnableGetEntitiesAroundOptimization,
            "entitydespawnpacketoptimization" => EnableEntityDespawnPacketOptimization,
            "recipebaselinqoptimization" => EnableRecipeBaseLinqOptimization,
            _ => false
        };
    }

    private static string NormalizeCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return null;

        return category.ToLowerInvariant();
    }
}
