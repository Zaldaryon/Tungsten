using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace Tungsten
{
    /// <summary>
    /// Replaces 3x Select().ToArray() LINQ chains in ServerPackets.GetEntityDespawnPacket
    /// with a single-pass loop. Eliminates ~6 iterator/delegate allocations per call.
    /// ~10-50 calls/sec bursty during exploration/chunk transitions.
    /// </summary>
    public static class EntityDespawnPacketOptimizer
    {
        private const string CircuitKey = "EntityDespawnPacketOptimization";
        private static volatile bool disabled;
        private static int disableLogGate;

        public static void Initialize(ICoreServerAPI api, Harmony harmony)
        {
            var method = AccessTools.Method(typeof(ServerPackets), "GetEntityDespawnPacket");
            if (method == null)
            {
                api.Logger.Warning("[Tungsten] [EntityDespawnPacketOptimization] Could not find GetEntityDespawnPacket method");
                return;
            }

            harmony.Patch(method, prefix: new HarmonyMethod(typeof(EntityDespawnPacketOptimizer), nameof(GetEntityDespawnPacket_Prefix)));
        }

        public static bool GetEntityDespawnPacket_Prefix(List<EntityDespawn> despawns, ref Packet_Server __result)
        {
            if (disabled || !OptimizationRuntimeCircuitBreaker.ShouldRun(CircuitKey))
                return true;

            try
            {
                TungstenProfiler.Mark("tungsten-despawnpacket");
                int count = despawns.Count;
                long[] entityIds = new long[count];
                int[] despawnReasons = new int[count];
                int[] deathDamageSources = new int[count];

                for (int i = 0; i < count; i++)
                {
                    var item = despawns[i];
                    entityIds[i] = item.EntityId;

                    var data = item.DespawnData;
                    if (data != null)
                    {
                        despawnReasons[i] = (int)data.Reason;
                        deathDamageSources[i] = data.DamageSourceForDeath != null
                            ? (int)data.DamageSourceForDeath.Source
                            : (int)EnumDamageSource.Unknown;
                    }
                    else
                    {
                        despawnReasons[i] = (int)EnumDespawnReason.Death;
                        deathDamageSources[i] = (int)EnumDamageSource.Block;
                    }
                }

                var packetDespawn = new Packet_EntityDespawn();
                packetDespawn.SetEntityId(entityIds);
                packetDespawn.SetDeathDamageSource(deathDamageSources);
                packetDespawn.SetDespawnReason(despawnReasons);

                __result = new Packet_Server
                {
                    Id = 36,
                    EntityDespawn = packetDespawn
                };
                return false;
            }
            catch (Exception)
            {
                Disable("runtime exception");
                return true;
            }
        }

        private static void Disable(string reason)
        {
            disabled = true;
            OptimizationRuntimeCircuitBreaker.Disable(CircuitKey, reason, emitLog: false);
            if (Interlocked.CompareExchange(ref disableLogGate, 1, 0) == 0)
                TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] [EntityDespawnPacketOptimization] Disabled: {reason}");
        }

        public static void Dispose()
        {
            disabled = false;
            disableLogGate = 0;
        }
    }
}
