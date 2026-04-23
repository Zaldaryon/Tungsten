using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Vintagestory.API.Server;

namespace Tungsten
{
    public class ChunkUnloadingOptimizer
    {
        private readonly ICoreServerAPI api;

        private static readonly ThreadLocal<object> reusableList1 = new(() => null);
        private static readonly ThreadLocal<object> reusableList2 = new(() => null);
        private static readonly ThreadLocal<object> reusableList3 = new(() => null);
        private static readonly ThreadLocal<object> reusableList4 = new(() => null);

        private static readonly ThreadLocal<object> reusableMapRegionList = new(() => null);
        private static readonly ThreadLocal<object> reusableMapRegionDbList = new(() => null);

        private static readonly ThreadLocal<object> reusableUnloadChunkList = new(() => null);
        private static readonly ThreadLocal<object> reusableUnloadMapChunkList = new(() => null);

        private static readonly ThreadLocal<object> reusableSendUnloadedList1 = new(() => null);
        private static readonly ThreadLocal<object> reusableSendUnloadedList2 = new(() => null);

        private static readonly ThreadLocal<object> reusableOutOfRangeList = new(() => null);
        private static readonly ThreadLocal<object> reusableOutOfRangeHashSet = new(() => null);

        public ChunkUnloadingOptimizer(ICoreServerAPI api)
        {
            this.api = api;

            // v1.10.0: Register ThreadLocal instances for proper disposal
            ThreadLocalRegistry.Register(reusableList1);
            ThreadLocalRegistry.Register(reusableList2);
            ThreadLocalRegistry.Register(reusableList3);
            ThreadLocalRegistry.Register(reusableList4);
            ThreadLocalRegistry.Register(reusableMapRegionList);
            ThreadLocalRegistry.Register(reusableMapRegionDbList);
            ThreadLocalRegistry.Register(reusableUnloadChunkList);
            ThreadLocalRegistry.Register(reusableUnloadMapChunkList);
            ThreadLocalRegistry.Register(reusableSendUnloadedList1);
            ThreadLocalRegistry.Register(reusableSendUnloadedList2);
            ThreadLocalRegistry.Register(reusableOutOfRangeList);
            ThreadLocalRegistry.Register(reusableOutOfRangeHashSet);
        }

        /// <summary>
        /// Cleanup ThreadLocal registrations if initialization fails.
        /// </summary>
        public void CleanupOnFailure()
        {
            ThreadLocalRegistry.UnregisterAll(
                reusableList1, reusableList2, reusableList3, reusableList4,
                reusableMapRegionList, reusableMapRegionDbList,
                reusableUnloadChunkList, reusableUnloadMapChunkList,
                reusableSendUnloadedList1, reusableSendUnloadedList2,
                reusableOutOfRangeList, reusableOutOfRangeHashSet
            );
        }

        public void ApplyPatches(Harmony harmony)
        {
            var unloadChunksType = AccessTools.TypeByName("Vintagestory.Server.ServerSystemUnloadChunks");
            if (unloadChunksType == null)
            {
                api.Logger.Warning("[Tungsten] [ChunkUnloadingOptimizer] Could not find ServerSystemUnloadChunks type");
                return;
            }

            // Patch SaveDirtyUnloadedChunks
            var saveDirtyMethod = AccessTools.Method(unloadChunksType, "SaveDirtyUnloadedChunks");
            if (saveDirtyMethod != null)
            {
                var transpiler = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(SaveDirtyUnloadedChunks_Transpiler));
                harmony.Patch(saveDirtyMethod, transpiler: new HarmonyMethod(transpiler));            }

            // Patch SaveDirtyMapRegions
            var saveMapRegionsMethod = AccessTools.Method(unloadChunksType, "SaveDirtyMapRegions");
            if (saveMapRegionsMethod != null)
            {
                var transpiler = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(SaveDirtyMapRegions_Transpiler));
                harmony.Patch(saveMapRegionsMethod, transpiler: new HarmonyMethod(transpiler));            }

            // Patch UnloadChunkColumns
            var unloadColumnsMethod = AccessTools.Method(unloadChunksType, "UnloadChunkColumns");
            if (unloadColumnsMethod != null)
            {
                var transpiler = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(UnloadChunkColumns_Transpiler));
                harmony.Patch(unloadColumnsMethod, transpiler: new HarmonyMethod(transpiler));            }

            // Patch SendUnloadedChunkUnloads
            var sendUnloadedMethod = AccessTools.Method(unloadChunksType, "SendUnloadedChunkUnloads");
            if (sendUnloadedMethod != null)
            {
                var transpiler = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(SendUnloadedChunkUnloads_Transpiler));
                harmony.Patch(sendUnloadedMethod, transpiler: new HarmonyMethod(transpiler));            }

            // Patch SendOutOfRangeChunkUnloads
            var sendOutOfRangeMethod = AccessTools.Method(unloadChunksType, "SendOutOfRangeChunkUnloads");
            if (sendOutOfRangeMethod != null)
            {
                var transpiler = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(SendOutOfRangeChunkUnloads_Transpiler));
                harmony.Patch(sendOutOfRangeMethod, transpiler: new HarmonyMethod(transpiler));            }
        }

        public static IEnumerable<CodeInstruction> SaveDirtyUnloadedChunks_Transpiler(IEnumerable<CodeInstruction> instructions)
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
                            MethodInfo helperMethod = null;
                            
                            if (replacements == 0)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableList1)).MakeGenericMethod(elementType);
                            else if (replacements == 1)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableList2)).MakeGenericMethod(elementType);
                            else if (replacements == 2)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableList3)).MakeGenericMethod(elementType);
                            else if (replacements == 3)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableList4)).MakeGenericMethod(elementType);
                            
                            if (helperMethod != null)
                            {
                                var newInstruction = new CodeInstruction(OpCodes.Call, helperMethod);
                                newInstruction.labels = codes[i].labels;
                                newInstruction.blocks = codes[i].blocks;
                                codes[i] = newInstruction;
                                replacements++;
                            }
                            
                            if (replacements >= 4) break;
                        }
                    }
                }
            }

            return replacements == 4 ? codes : instructions;
        }

        public static IEnumerable<CodeInstruction> SaveDirtyMapRegions_Transpiler(IEnumerable<CodeInstruction> instructions)
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
                            MethodInfo helperMethod = null;
                            
                            if (replacements == 0)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableMapRegionList)).MakeGenericMethod(elementType);
                            else if (replacements == 1)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableMapRegionDbList)).MakeGenericMethod(elementType);
                            
                            if (helperMethod != null)
                            {
                                var newInstruction = new CodeInstruction(OpCodes.Call, helperMethod);
                                newInstruction.labels = codes[i].labels;
                                newInstruction.blocks = codes[i].blocks;
                                codes[i] = newInstruction;
                                replacements++;
                            }
                            
                            if (replacements >= 2) break;
                        }
                    }
                }
            }

            return replacements == 2 ? codes : instructions;
        }

        public static IEnumerable<CodeInstruction> UnloadChunkColumns_Transpiler(IEnumerable<CodeInstruction> instructions)
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
                            MethodInfo helperMethod = null;
                            
                            if (replacements == 0)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableUnloadChunkList)).MakeGenericMethod(elementType);
                            else if (replacements == 1)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableUnloadMapChunkList)).MakeGenericMethod(elementType);
                            
                            if (helperMethod != null)
                            {
                                var newInstruction = new CodeInstruction(OpCodes.Call, helperMethod);
                                newInstruction.labels = codes[i].labels;
                                newInstruction.blocks = codes[i].blocks;
                                codes[i] = newInstruction;
                                replacements++;
                            }
                            
                            if (replacements >= 2) break;
                        }
                    }
                }
            }

            return replacements == 2 ? codes : instructions;
        }

        public static IEnumerable<CodeInstruction> SendUnloadedChunkUnloads_Transpiler(IEnumerable<CodeInstruction> instructions)
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
                            MethodInfo helperMethod = null;
                            
                            if (replacements == 0)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableSendUnloadedList1)).MakeGenericMethod(elementType);
                            else if (replacements == 1)
                                helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableSendUnloadedList2)).MakeGenericMethod(elementType);
                            
                            if (helperMethod != null)
                            {
                                var newInstruction = new CodeInstruction(OpCodes.Call, helperMethod);
                                newInstruction.labels = codes[i].labels;
                                newInstruction.blocks = codes[i].blocks;
                                codes[i] = newInstruction;
                                replacements++;
                            }
                            
                            if (replacements >= 2) break;
                        }
                    }
                }
            }

            return replacements == 2 ? codes : instructions;
        }

        public static IEnumerable<CodeInstruction> SendOutOfRangeChunkUnloads_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int replacements = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Newobj && codes[i].operand is ConstructorInfo ctor)
                {
                    // Handle List<long>
                    if (ctor.DeclaringType.IsGenericType && ctor.DeclaringType.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        if (ctor.GetParameters().Length == 0 && replacements == 0)
                        {
                            var elementType = ctor.DeclaringType.GetGenericArguments()[0];
                            var helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableOutOfRangeList)).MakeGenericMethod(elementType);
                            
                            var newInstruction = new CodeInstruction(OpCodes.Call, helperMethod);
                            newInstruction.labels = codes[i].labels;
                            newInstruction.blocks = codes[i].blocks;
                            codes[i] = newInstruction;
                            replacements++;
                        }
                    }
                    // Handle HashSet<long>
                    else if (ctor.DeclaringType.IsGenericType && ctor.DeclaringType.GetGenericTypeDefinition() == typeof(HashSet<>))
                    {
                        if (ctor.GetParameters().Length == 0 && replacements == 1)
                        {
                            var elementType = ctor.DeclaringType.GetGenericArguments()[0];
                            var helperMethod = AccessTools.Method(typeof(ChunkUnloadingOptimizer), nameof(GetReusableOutOfRangeHashSet)).MakeGenericMethod(elementType);
                            
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

            return replacements == 2 ? codes : instructions;
        }

        // Helper methods for SaveDirtyUnloadedChunks
        public static List<T> GetReusableList1<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(100);

            try
            {
                if (reusableList1.Value == null) reusableList1.Value = new List<T>(100);
                var list = (List<T>)reusableList1.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(100);
            }
        }

        public static List<T> GetReusableList2<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(100);

            try
            {
                if (reusableList2.Value == null) reusableList2.Value = new List<T>(100);
                var list = (List<T>)reusableList2.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(100);
            }
        }

        public static List<T> GetReusableList3<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(100);

            try
            {
                if (reusableList3.Value == null) reusableList3.Value = new List<T>(100);
                var list = (List<T>)reusableList3.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(100);
            }
        }

        public static List<T> GetReusableList4<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(100);

            try
            {
                if (reusableList4.Value == null) reusableList4.Value = new List<T>(100);
                var list = (List<T>)reusableList4.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(100);
            }
        }

        // Helper methods for SaveDirtyMapRegions
        public static List<T> GetReusableMapRegionList<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(50);

            try
            {
                if (reusableMapRegionList.Value == null) reusableMapRegionList.Value = new List<T>(50);
                var list = (List<T>)reusableMapRegionList.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(50);
            }
        }

        public static List<T> GetReusableMapRegionDbList<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(50);

            try
            {
                if (reusableMapRegionDbList.Value == null) reusableMapRegionDbList.Value = new List<T>(50);
                var list = (List<T>)reusableMapRegionDbList.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(50);
            }
        }

        // Helper methods for UnloadChunkColumns
        public static List<T> GetReusableUnloadChunkList<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(100);

            try
            {
                if (reusableUnloadChunkList.Value == null) reusableUnloadChunkList.Value = new List<T>(100);
                var list = (List<T>)reusableUnloadChunkList.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(100);
            }
        }

        public static List<T> GetReusableUnloadMapChunkList<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(50);

            try
            {
                if (reusableUnloadMapChunkList.Value == null) reusableUnloadMapChunkList.Value = new List<T>(50);
                var list = (List<T>)reusableUnloadMapChunkList.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(50);
            }
        }

        // Helper methods for SendUnloadedChunkUnloads
        public static List<T> GetReusableSendUnloadedList1<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(100);

            try
            {
                if (reusableSendUnloadedList1.Value == null) reusableSendUnloadedList1.Value = new List<T>(100);
                var list = (List<T>)reusableSendUnloadedList1.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(100);
            }
        }

        public static List<T> GetReusableSendUnloadedList2<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(100);

            try
            {
                if (reusableSendUnloadedList2.Value == null) reusableSendUnloadedList2.Value = new List<T>(100);
                var list = (List<T>)reusableSendUnloadedList2.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(100);
            }
        }

        // Helper methods for SendOutOfRangeChunkUnloads
        public static List<T> GetReusableOutOfRangeList<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new List<T>(100);

            try
            {
                if (reusableOutOfRangeList.Value == null) reusableOutOfRangeList.Value = new List<T>(100);
                var list = (List<T>)reusableOutOfRangeList.Value;
                list.Clear();
                return list;
            }
            catch (ObjectDisposedException)
            {
                return new List<T>(100);
            }
        }

        public static HashSet<T> GetReusableOutOfRangeHashSet<T>()
        {
            if (ThreadLocalHelper.IsDisposing)
                return new HashSet<T>();

            try
            {
                if (reusableOutOfRangeHashSet.Value == null) reusableOutOfRangeHashSet.Value = new HashSet<T>();
                var set = (HashSet<T>)reusableOutOfRangeHashSet.Value;
                set.Clear();
                return set;
            }
            catch (ObjectDisposedException)
            {
                return new HashSet<T>();
            }
        }
    }
}
