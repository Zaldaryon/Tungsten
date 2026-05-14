# Tungsten

Server-side performance optimization mod for Vintage Story.

## Scope
- Side: server only (`requiredOnClient: false`)
- Dependency declared in `modinfo.json`: `game: 1.22.0` (minimum-version semantics)
- Target framework: `net10.0` (Vintage Story 1.22 ships with .NET 10)
- Validated operational window: `1.22.0+`

## Goals
- Reduce allocation pressure and GC churn in hot paths.
- Keep black-box behavior equivalent to vanilla gameplay logic.
- Fail safe: if optimization safety checks fail, fallback to vanilla-like paths.

## Optimization Catalog (24)

### Core list reuse
1. `EnableEntityListReuse`
2. `EnableBlockListReuse`
3. `EnableGetDropsListReuse`
4. `EnableEventManagerListReuse`

### Chunk and world flow
5. `EnableChunkLoadingOptimization`
6. `EnableChunkUnloadingOptimization`

### Gameplay/crafting/inventory
7. `EnableEntitySimulationOptimization`
8. `EnableCookingContainerOptimization`
9. `EnableContainerOptimization`
10. `EnableGridRecipeOptimization`
11. `EnableDepositGeneratorOptimization`

### Networking and server loop
12. `EnableSendPlayerEntityDeathsOptimization`
13. `EnablePhysicsManagerListOptimization`
14. `EnablePhysicsManagerMethodListOptimization`
15. `EnableServerMainLinqOptimization`

### Registry and pattern matching
16. `EnablePlaceholderOptimization`
17. `EnableWildcardFastMatchOptimization`

### Entity queries and recipe LINQ (v1.3.0)
18. `EnableGetEntitiesAroundOptimization`
19. `EnableEntityDespawnPacketOptimization`
20. `EnableRecipeBaseLinqOptimization`

### Broadcast, packets, and registry compaction (v1.3.1)
21. `EnableBroadcastLinqOptimization`
22. `EnableBulkEntityAttributesPacketOptimization`
23. `EnableClassRegistryFrozenOptimization`
24. `EnableGetPlayersAroundOptimization`

## Key Optimizations Summary
- `PlaceholderOptimization`:
  - patches `RegistryObject.FillPlaceHolder(string, OrderedDictionary<string,string>)`
  - single-pass placeholder parsing with startup equivalence self-check
- `WildcardFastMatchOptimization`:
  - patches `WildcardUtil.fastMatch(string,string)`
  - compiled regex cache with LRU eviction for `@` patterns only
  - vanilla 1.22 handles `*` patterns optimally; Tungsten only adds compiled regex for `@`
- `GridRecipeOptimization`:
  - patches `RecipeBase.MatchesShapeLess` (moved from GridRecipe in 1.22)
  - eliminates 2 list allocations per shapeless recipe match
- `GetEntitiesAroundOptimization` (v1.3.0):
  - patches `GameMain.GetEntitiesAround`
  - reuses intermediate `List<Entity>` via ThreadLocal with recursion guard
  - ~300-450 calls/sec continuous on active servers
- `EntityDespawnPacketOptimization` (v1.3.0):
  - patches `ServerPackets.GetEntityDespawnPacket`
  - single-pass loop replacing 3x LINQ Select().ToArray() chains
- `RecipeBaseLinqOptimization` (v1.3.0):
  - patches `RecipeBase.MergeStacks` and `RecipeBase.MatchWildcardIngredients`
  - eliminates LINQ iterator allocations in crafting recipe matching
- `BroadcastLinqOptimization` (v1.3.1):
  - patches 3 `ServerMain` broadcast overloads
  - eliminates LINQ `Any()`/`All()` closure allocations in player filtering
- `BulkEntityAttributesPacketOptimization` (v1.3.1):
  - patches `ServerPackets.GetBulkEntityAttributesPacket`
  - reuses packet wrapper objects via ThreadStatic, eliminates 2 allocs/client/tick
- `ClassRegistryFrozenOptimization` (v1.3.1):
  - compacts 19 ClassRegistry dictionaries via `TrimExcess()` after mod loading
  - reduces memory waste and improves cache locality for registry lookups
- `GetPlayersAroundOptimization` (v1.3.1):
  - patches `ServerMain.GetPlayersAround`
  - ThreadLocal list reuse with recursion guard (same pattern as GetEntitiesAround)

## v1.2.3 Migration Notes (from 1.2.x)
- **Removed** `EnableSendChunksListReuse` — vanilla 1.22 uses `FastList<T>` which already implements zero-allocation clear
- **Removed** `EnableRegistryResolveExperimentalOptimization` — was 1.21.6-only experimental
- **Rewritten** `GridRecipeOptimizer` — target moved from `GridRecipe` to `RecipeBase`, list types updated for 1.22 API
- **Refactored** `WildcardFastMatchOptimization` — vanilla 1.22 has same iterative matcher; now only intercepts `@` regex patterns
- **Target framework** changed from `net8.0` to `net10.0`
- **Minimum game version** changed from `1.21.5` to `1.22.0`
- IL signature manifest must be regenerated (all hashes changed due to .NET 10 recompilation)

## Safety and Fallback Model

### Startup safety
- Patch failure: optimization remains disabled, vanilla path is preserved.
- Lifecycle safety:
  - P1 `EnableThreadLocalLifecycleReset`: startup reset; fallback to vanilla allocations if reset fails.
  - P2 `EnableReusableCollectionPoolConcurrentOptimization`: concurrent pool path reset; fallback if reset fails.
  - P3 `EnableReusableCollectionPoolConstructorCacheOptimization`: constructor-cache reset; fallback to `Activator` path if reset fails.
  - P4 `EnableUnifiedRuntimeCircuitBreaker`: global runtime breaker reset; auto-disabled if reset fails.
  - P9 `EnableIlSignatureManifestValidation`: validates IL hashes before patching; auto-disables affected optimizations on mismatch.
  - P10 `EnableBenchmarkHarness`: starts benchmark session only when healthy.

### Runtime safety
- Runtime exceptions in integrated optimization keys degrade to vanilla-safe paths.
- Circuit-breaker status is visible in `/tungsten`.
- Benchmark harness failure auto-disables harness and preserves gameplay behavior.

## Installation
1. Build/package or obtain `Tungsten.zip`.
2. Place it in `VintagestoryData/Mods/`.
3. Start or restart the server.

## Configuration
- File: `VintagestoryData/ModConfig/tungsten.json`
- Most optimization toggles require server restart.
- Runtime/process toggles that apply immediately:
  - `EnableAdvancedMonitoring`
  - `EnableUnifiedRuntimeCircuitBreaker`
  - `EnableBenchmarkHarness`
  - `EnableFrameProfiler`

### Default operational/safety settings
- `EnableAdvancedMonitoring: false`
- `EnableCapacityTrimming: true`
- `EnableThreadLocalLifecycleReset: true`
- `EnableReusableCollectionPoolConcurrentOptimization: true`
- `EnableReusableCollectionPoolConstructorCacheOptimization: true`
- `EnableUnifiedRuntimeCircuitBreaker: true`
- `EnableIlSignatureManifestValidation: true`
- `EnableBenchmarkHarness: false`
- `EnableFrameProfiler: false`

### Benchmark defaults
- `BenchmarkProfile: "default"`
- `BenchmarkVariant: "A"`
- `BenchmarkSessionDurationSeconds: 600`
- `BenchmarkSampleIntervalMs: 5000`

### Pool/memory defaults
- `ObjectPoolMaxSize: 32`
- `ObjectPoolShrinkIntervalSeconds: 60`
- `TargetCollectionCapacity: 200`
- `TrimCheckInterval: 5000`
- `FrameProfilerSlowTickThreshold: 40`

## Commands
- `/tungsten` shows optimization status + runtime health.
- `/tungsten all on|off` toggles all optimization flags (restart required).
- `/tungsten <optimization> on|off` toggles one optimization flag.
- `/tungsten reload` reloads config and applies runtime-safe settings.
- `/tungsten stats [on|off]` shows/toggles advanced monitoring.
- `/tungsten benchmarkharness [on|off]` controls benchmark harness.
- `/tungsten frameprofiler on|off [thresholdMs]` controls vanilla frame profiler integration.

## Logging Conventions
- Prefix: `[Tungsten]`
- Optimization-specific tags, for example:
  - `[PlaceholderOptimization]`
  - `[WildcardFastMatchOptimization]`
  - `[RuntimeCircuitBreaker]`
  - `[ILSignatureManifest]`
  - `[BenchmarkHarness]`
- Automatic disable/fallback events are explicitly logged.

## Operational Process

### Recommended rollout
1. Keep defaults and start server.
2. Verify startup logs for disabled optimizations and safety warnings.
3. Run representative workload.
4. Use `/tungsten` and `/tungsten stats` to inspect health.
5. If needed, run `/tungsten benchmarkharness on` for A/B sessions.

### Regression response
1. Identify degraded key in runtime circuit-breaker status.
2. Disable the specific optimization via command/config.
3. Restart if toggle requires restart.
4. Keep other optimizations enabled.

### Version upgrades
1. Upgrade server.
2. Review startup IL manifest validation logs.
3. Confirm that critical keys are not auto-disabled due to signature drift.
4. Re-run benchmark profile.

## Compatibility Notes
- Compatible with most content/gameplay mods.
- Harmony conflicts are possible when another mod patches identical methods.

## Documentation Index
- Main docs index: `Documentation/README.md`
- Detailed optimization summaries: `Documentation/Description/`
- PR analysis and implementation docs: `Documentation/ImplementationInTungsten/`
- Vanilla reference notes: `Documentation/ImplementationInVS/`

## License
Copyright (c) 2025 Zaldaryon - All Rights Reserved
