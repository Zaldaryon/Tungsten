using HarmonyLib;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Vintagestory.API.Server;

namespace Tungsten
{
    public class EntitySimulationOptimizer
    {
        private readonly ICoreServerAPI api;

        private static readonly ThreadLocal<object> reusableDespawnList = new(() => null);

        public EntitySimulationOptimizer(ICoreServerAPI api)
        {
            this.api = api;

            // v1.10.0: Register ThreadLocal instances for proper disposal
            ThreadLocalRegistry.Register(reusableDespawnList);
        }

        /// <summary>
        /// Cleanup ThreadLocal registrations if initialization fails.
        /// </summary>
        public void CleanupOnFailure()
        {
            ThreadLocalRegistry.Unregister(reusableDespawnList);
        }

        public void ApplyPatches(Harmony harmony)
        {
            if (EntityListReuseOptimizer.TickEntitiesPatched)
            {
                api.Logger.Notification("[Tungsten] [EntitySimulationOptimizer] Skipping TickEntities patch (covered by EntityListReuseOptimizer)");
                return;
            }

            var entitySimType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemEntitySimulation");
            if (entitySimType == null)
            {
                api.Logger.Warning("[Tungsten] [EntitySimulationOptimizer] Could not find ServerSystemEntitySimulation type");
                return;
            }

            var tickEntitiesMethod = AccessTools.Method(entitySimType, "TickEntities");
            if (tickEntitiesMethod != null)
            {
                var transpiler = AccessTools.Method(typeof(EntitySimulationOptimizer), nameof(TickEntities_Transpiler));
                harmony.Patch(tickEntitiesMethod, transpiler: new HarmonyMethod(transpiler));            }
            else
            {
                api.Logger.Warning("[Tungsten] [EntitySimulationOptimizer] Could not find TickEntities method");
            }
        }

        public static IEnumerable<CodeInstruction> TickEntities_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int replacements = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor)
                {
                    if (ctor.DeclaringType.IsGenericType && ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        if (ctor.GetParameters().Length == 0)
                        {
                            var elementType = ctor.DeclaringType.GetGenericArguments()[0];
                            var helperMethod = AccessTools.Method(typeof(EntitySimulationOptimizer), nameof(GetReusableDespawnList))
                                .MakeGenericMethod(elementType);
                            
                            var newInstruction = new CodeInstruction(OpCodes.Call, helperMethod);
                            newInstruction.labels = codes[i].labels;
                            newInstruction.blocks = codes[i].blocks;
                            codes[i] = newInstruction;
                            replacements++;
                            break;
                        }
                    }
                }
            }

            return replacements == 1 ? codes : instructions;
        }

        public static List<T> GetReusableDespawnList<T>()
        {
            if (reusableDespawnList.Value == null)
                reusableDespawnList.Value = new List<T>(100);
            var list = (List<T>)reusableDespawnList.Value;
            list.Clear();
            ThreadLocalHelper.MaybeTrimList(list);
            return list;
        }
    }
}
