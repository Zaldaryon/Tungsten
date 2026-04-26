using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Tungsten
{
    /// <summary>
    /// Eliminates the per-call List&lt;Entity&gt; allocation in GameMain.GetEntitiesAround.
    /// The intermediate list is reused via ThreadLocal; the .ToArray() remains (callers hold array references).
    /// ~300-450 calls/sec continuous on a 10-player server.
    /// </summary>
    public class GetEntitiesAroundOptimizer
    {
        private readonly ICoreServerAPI api;
        private static readonly ThreadLocal<List<Entity>> reusableEntityList = new(() => new List<Entity>());

        public GetEntitiesAroundOptimizer(ICoreServerAPI api)
        {
            this.api = api;
            ThreadLocalRegistry.Register(reusableEntityList);
        }

        public void CleanupOnFailure()
        {
            ThreadLocalRegistry.Unregister(reusableEntityList);
        }

        public void ApplyPatches(Harmony harmony)
        {
            var gameMainType = AccessTools.TypeByName("Vintagestory.Common.GameMain");
            if (gameMainType == null)
            {
                api.Logger.Warning("[Tungsten] [GetEntitiesAroundOptimization] Could not find GameMain type");
                return;
            }

            var method = AccessTools.Method(gameMainType, "GetEntitiesAround");
            if (method == null)
            {
                api.Logger.Warning("[Tungsten] [GetEntitiesAroundOptimization] Could not find GetEntitiesAround method");
                return;
            }

            var transpiler = AccessTools.Method(typeof(GetEntitiesAroundOptimizer), nameof(GetEntitiesAround_Transpiler));
            harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
        }

        public static IEnumerable<CodeInstruction> GetEntitiesAround_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var getReusableList = AccessTools.Method(typeof(GetEntitiesAroundOptimizer), nameof(GetReusableEntityList));
            int replaced = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is System.Reflection.ConstructorInfo ctor &&
                    ctor.DeclaringType != null &&
                    ctor.DeclaringType.IsGenericType &&
                    ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>) &&
                    ctor.DeclaringType.GetGenericArguments()[0] == typeof(Entity) &&
                    ctor.GetParameters().Length == 0)
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, getReusableList)
                    {
                        labels = codes[i].labels,
                        blocks = codes[i].blocks
                    };
                    replaced++;
                    break;
                }
            }

            if (replaced != 1)
                TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] [GetEntitiesAroundOptimization] Expected 1 replacement, found {replaced}");

            foreach (var code in codes)
                yield return code;
        }

        [ThreadStatic] private static int depth;

        public static List<Entity> GetReusableEntityList()
        {
            if (depth > 0)
                return new List<Entity>();

            depth++;
            try
            {
                return ThreadLocalHelper.GetAndClear(reusableEntityList);
            }
            catch
            {
                return new List<Entity>();
            }
            finally
            {
                depth--;
            }
        }
    }
}
