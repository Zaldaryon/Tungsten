# Tungsten

Server-side performance optimization mod for [Vintage Story](https://vintagestory.at/).

Tungsten reduces memory allocation pressure and GC pauses on dedicated servers by patching hot paths in the game's server loop via [Harmony](https://github.com/pardeike/Harmony). All optimizations are transparent — gameplay behavior is identical to vanilla. If any patch fails or causes an error at runtime, it automatically disables itself and falls back to the original code.

**Requires Vintage Story 1.22.0+** · Server-side only (`requiredOnClient: false`)

## What it optimizes

Tungsten targets 17 allocation hotspots across the server tick loop:

| Area | What it does |
|------|-------------|
| **Entity tick & despawn** | Reuses lists allocated every tick for entity simulation, despawn tracking, and death notifications |
| **Block updates** | Reuses the 3 block-position lists allocated every 100ms during block change processing |
| **Item drops** | Reuses lists allocated on every block break for drop calculation |
| **Event system** | Reuses the callback-key snapshot list allocated every game tick |
| **Chunk loading** | Reuses lists and sets in chunk request queuing and chunk deletion |
| **Chunk unloading** | Reuses 12 collections across 5 methods in the chunk save/unload pipeline |
| **Cooking & containers** | Reuses lists for cooking stack and container inventory scans |
| **Recipe matching** | Reuses the 2 lists allocated per shapeless recipe match attempt |
| **Ore generation** | Reuses the hash set and caches bearing-block lookups in deposit generation |
| **Physics networking** | Reuses lists for client building, position packets, and animation packets |
| **Player queries** | Replaces LINQ-based `AllOnlinePlayers`/`AllPlayers` with direct iteration |
| **Placeholder parsing** | Single-pass placeholder resolver instead of N regex passes per placeholder set |
| **Wildcard matching** | Compiled regex cache with LRU eviction for `@`-pattern matching |

## Installation

1. Download the latest release from [GitHub Releases](https://github.com/Zaldaryon/Tungsten/releases) or [Mod DB](https://mods.vintagestory.at/tungsten).
2. Place the zip in your `VintagestoryData/Mods/` folder.
3. Start or restart the server.

All optimizations are enabled by default. No configuration needed.

## Configuration

Config file: `VintagestoryData/ModConfig/tungsten.json`

Each optimization can be toggled individually. Most require a server restart; a few apply immediately:
- `EnableAdvancedMonitoring` — runtime allocation tracking
- `EnableUnifiedRuntimeCircuitBreaker` — global safety switch
- `EnableBenchmarkHarness` — A/B performance comparison sessions
- `EnableFrameProfiler` — slow-tick logging integration

## Commands

All commands require `controlserver` privilege.

| Command | Description |
|---------|-------------|
| `/tungsten` | Show status of all optimizations and runtime health |
| `/tungsten all on\|off` | Toggle all optimizations (restart required) |
| `/tungsten <name> on\|off` | Toggle a specific optimization |
| `/tungsten reload` | Reload config and apply runtime-safe settings |
| `/tungsten stats [on\|off]` | Show or toggle advanced monitoring |
| `/tungsten benchmarkharness [on\|off]` | Control the benchmark harness |
| `/tungsten frameprofiler on\|off [ms]` | Control slow-tick logging with optional threshold |

## Safety model

- **Startup**: If a Harmony patch fails to apply, that optimization stays disabled. The server starts normally.
- **Runtime**: If an optimized path throws an exception, the circuit breaker disables it and falls back to vanilla behavior. No crash, no data loss.
- **IL validation**: On startup, Tungsten can validate method signatures against known hashes. If the game updated and a method changed, the affected optimization is auto-disabled before it can cause problems.
- **Compatibility**: If the `TemporalChunks` mod is detected, Tungsten auto-disables chunk-related optimizations to avoid conflicts.

## Compatibility

- Works with most content and gameplay mods.
- Potential Harmony conflicts only if another mod patches the exact same methods.
- Server-side only — clients don't need it installed.

## Building from source

Requires the Vintage Story game installation. Set the `VINTAGE_STORY` environment variable to your install path, then:

```bash
# Linux
./build.sh

# Windows
build.bat
```

Output: `bin/Tungsten-<version>.zip`

## License

Copyright © 2025 Zaldaryon — All Rights Reserved
