using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;

namespace Tungsten
{
    /// <summary>
    /// Reuses List<T> and HashSet<T> instances without compile-time dependency on T.
    /// Uses ThreadLocal storage to keep per-thread safety and supports multiple slots per type
    /// to avoid aliasing when the same method needs multiple lists of the same type.
    /// </summary>
    public static class ReusableCollectionPool
    {
        private static readonly object lockObj = new object();
        private static readonly Dictionary<(Type type, int slot), ThreadLocal<object>> listPoolsLegacy = new Dictionary<(Type, int), ThreadLocal<object>>();
        private static readonly Dictionary<(Type type, int slot), ThreadLocal<object>> setPoolsLegacy = new Dictionary<(Type, int), ThreadLocal<object>>();
        private static readonly Dictionary<Type, Action<object>> clearActionsLegacy = new Dictionary<Type, Action<object>>();

        private static readonly ConcurrentDictionary<(Type type, int slot), Lazy<ThreadLocal<object>>> listPoolsConcurrent = new();
        private static readonly ConcurrentDictionary<(Type type, int slot), Lazy<ThreadLocal<object>>> setPoolsConcurrent = new();
        private static readonly ConcurrentDictionary<Type, ClearActionEntry> clearActionsConcurrent = new();
        private static readonly ConcurrentDictionary<Type, ConstructorFactoryEntry> constructorFactoriesConcurrent = new();
        private static readonly ConcurrentDictionary<Type, AddActionEntry> addActionsConcurrent = new();

        private static volatile bool cachedEnableConcurrentHotPath = true;
        private static volatile bool cachedEnableConstructorCache = true;
        private static volatile bool vanillaFallbackActive = false;
        private static volatile bool constructorFallbackActive = false;
        private static int fallbackLogGate = 0;
        private static int constructorFallbackLogGate = 0;

        private sealed class ClearActionEntry
        {
            public bool HasClear;
            public Action<object> Action;
        }

        private sealed class ConstructorFactoryEntry
        {
            public bool HasFactory;
            public Func<object> Factory;
        }

        private sealed class AddActionEntry
        {
            public bool HasAdd;
            public Action<object, object> Action;
            public System.Reflection.MethodInfo Method;
        }

        public static void UpdateConfig(bool enableConcurrentHotPath, bool enableConstructorCacheOptimization)
        {
            cachedEnableConcurrentHotPath = enableConcurrentHotPath;
            cachedEnableConstructorCache = enableConstructorCacheOptimization;
        }

        public static bool IsConcurrentHotPathEnabled => cachedEnableConcurrentHotPath && !vanillaFallbackActive;
        public static bool IsVanillaFallbackActive => vanillaFallbackActive;
        public static bool IsConstructorCacheEnabled => cachedEnableConstructorCache && !constructorFallbackActive;
        public static bool IsConstructorCacheFallbackActive => constructorFallbackActive;

        public static bool TryResetRuntimeState()
        {
            if (!cachedEnableConcurrentHotPath)
            {
                TungstenMod.Instance?.Api?.Logger?.Notification(
                    "[Tungsten] [ReusableCollectionPool] Concurrent hot-path disabled by config; startup reset skipped"
                );
                return false;
            }

            try
            {
                vanillaFallbackActive = false;
                fallbackLogGate = 0;
                return true;
            }
            catch (Exception ex)
            {
                ForceVanillaFallback("runtime reset failed: " + ex.Message);
                return false;
            }
        }

        public static bool TryResetConstructorCacheState()
        {
            if (!cachedEnableConstructorCache)
            {
                TungstenMod.Instance?.Api?.Logger?.Notification(
                    "[Tungsten] [ReusableCollectionPoolCtorCache] Disabled by config; startup reset skipped"
                );
                return false;
            }

            try
            {
                constructorFallbackActive = false;
                constructorFallbackLogGate = 0;
                return true;
            }
            catch (Exception ex)
            {
                ForceConstructorCacheFallback("runtime reset failed: " + ex.Message);
                return false;
            }
        }

        public static void ForceVanillaFallback(string reason)
        {
            vanillaFallbackActive = true;
            OptimizationRuntimeCircuitBreaker.Disable("ReusableCollectionPoolConcurrentOptimization", reason, emitLog: false);
            if (Interlocked.CompareExchange(ref fallbackLogGate, 1, 0) == 0)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning(
                    "[Tungsten] [ReusableCollectionPool] Falling back to vanilla allocation mode: " + reason
                );
            }
        }

        public static void ForceConstructorCacheFallback(string reason)
        {
            constructorFallbackActive = true;
            OptimizationRuntimeCircuitBreaker.Disable("ReusableCollectionPoolConstructorCacheOptimization", reason, emitLog: false);
            if (Interlocked.CompareExchange(ref constructorFallbackLogGate, 1, 0) == 0)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning(
                    "[Tungsten] [ReusableCollectionPoolCtorCache] Falling back to Activator allocation mode: " + reason
                );
            }
        }

        public static object GetList(Type listType, int slot)
        {
            if (listType == null) throw new ArgumentNullException(nameof(listType));

            if (ThreadLocalHelper.IsDisposing)
                return Activator.CreateInstance(listType);

            if (!cachedEnableConcurrentHotPath)
                return GetListLegacy(listType, slot);

            if (!OptimizationRuntimeCircuitBreaker.ShouldRun("ReusableCollectionPoolConcurrentOptimization"))
                return Activator.CreateInstance(listType);

            if (vanillaFallbackActive)
                return Activator.CreateInstance(listType);

            try
            {
                var tl = GetOrCreateConcurrent(listPoolsConcurrent, listType, slot);
                object list = tl.Value;
                if (list == null)
                {
                    list = CreateInstance(listType);
                    tl.Value = list;
                }
                else
                {
                    if (list is IList ilist)
                    {
                        ilist.Clear();
                    }
                    else
                    {
                        GetClearActionConcurrent(listType)?.Invoke(list);
                    }
                }

                return list;
            }
            catch (Exception ex)
            {
                ForceVanillaFallback($"GetList failed for {listType.FullName}: {ex.Message}");
                return Activator.CreateInstance(listType);
            }
        }

        public static object GetHashSet(Type setType, int slot)
        {
            if (setType == null) throw new ArgumentNullException(nameof(setType));

            if (ThreadLocalHelper.IsDisposing)
                return Activator.CreateInstance(setType);

            if (!cachedEnableConcurrentHotPath)
                return GetHashSetLegacy(setType, slot);

            if (!OptimizationRuntimeCircuitBreaker.ShouldRun("ReusableCollectionPoolConcurrentOptimization"))
                return Activator.CreateInstance(setType);

            if (vanillaFallbackActive)
                return Activator.CreateInstance(setType);

            try
            {
                var tl = GetOrCreateConcurrent(setPoolsConcurrent, setType, slot);
                object set = tl.Value;
                if (set == null)
                {
                    set = CreateInstance(setType);
                    tl.Value = set;
                }
                else
                {
                    GetClearActionConcurrent(setType)?.Invoke(set);
                }

                return set;
            }
            catch (Exception ex)
            {
                ForceVanillaFallback($"GetHashSet failed for {setType.FullName}: {ex.Message}");
                return Activator.CreateInstance(setType);
            }
        }

        public static object ToList(IEnumerable source, Type listType, int slot)
        {
            var listObj = GetList(listType, slot);
            if (source == null) return listObj;

            if (listObj is IList ilist)
            {
                foreach (var item in source)
                {
                    ilist.Add(item);
                }
                return listObj;
            }

            var addEntry = addActionsConcurrent.GetOrAdd(listType, BuildAddActionEntry);
            if (addEntry.HasAdd && addEntry.Action != null)
            {
                foreach (var item in source)
                {
                    addEntry.Action(listObj, item);
                }
            }
            else if (addEntry.Method != null)
            {
                foreach (var item in source)
                {
                    addEntry.Method.Invoke(listObj, new[] { item });
                }
            }

            return listObj;
        }

        public static void ClearAll()
        {
            List<IDisposable> toUnregister = null;

            lock (lockObj)
            {
                if (listPoolsLegacy.Count > 0 || setPoolsLegacy.Count > 0)
                {
                    toUnregister = new List<IDisposable>(listPoolsLegacy.Count + setPoolsLegacy.Count);
                    foreach (var tl in listPoolsLegacy.Values)
                    {
                        if (tl != null)
                            toUnregister.Add(tl);
                    }
                    foreach (var tl in setPoolsLegacy.Values)
                    {
                        if (tl != null)
                            toUnregister.Add(tl);
                    }
                }

                listPoolsLegacy.Clear();
                setPoolsLegacy.Clear();
                clearActionsLegacy.Clear();
            }

            foreach (var kv in listPoolsConcurrent)
            {
                var lazy = kv.Value;
                if (lazy != null && lazy.IsValueCreated && lazy.Value != null)
                {
                    toUnregister ??= new List<IDisposable>();
                    toUnregister.Add(lazy.Value);
                }
            }

            foreach (var kv in setPoolsConcurrent)
            {
                var lazy = kv.Value;
                if (lazy != null && lazy.IsValueCreated && lazy.Value != null)
                {
                    toUnregister ??= new List<IDisposable>();
                    toUnregister.Add(lazy.Value);
                }
            }

            listPoolsConcurrent.Clear();
            setPoolsConcurrent.Clear();
            clearActionsConcurrent.Clear();
            constructorFactoriesConcurrent.Clear();
            addActionsConcurrent.Clear();

            if (toUnregister != null && toUnregister.Count > 0)
                ThreadLocalRegistry.UnregisterAll(toUnregister.ToArray());
        }

        private static object GetListLegacy(Type listType, int slot)
        {
            var tl = GetOrCreateLegacy(listPoolsLegacy, listType, slot);
            object list = tl.Value;
            if (list == null)
            {
                list = CreateInstance(listType);
                tl.Value = list;
            }
            else
            {
                if (list is IList ilist)
                {
                    ilist.Clear();
                }
                else
                {
                    GetClearActionLegacy(listType)?.Invoke(list);
                }
            }

            return list;
        }

        private static object GetHashSetLegacy(Type setType, int slot)
        {
            var tl = GetOrCreateLegacy(setPoolsLegacy, setType, slot);
            object set = tl.Value;
            if (set == null)
            {
                set = CreateInstance(setType);
                tl.Value = set;
            }
            else
            {
                GetClearActionLegacy(setType)?.Invoke(set);
            }

            return set;
        }

        private static object CreateInstance(Type type)
        {
            if (!cachedEnableConstructorCache || constructorFallbackActive)
                return Activator.CreateInstance(type);

            if (!OptimizationRuntimeCircuitBreaker.ShouldRun("ReusableCollectionPoolConstructorCacheOptimization"))
                return Activator.CreateInstance(type);

            try
            {
                var entry = constructorFactoriesConcurrent.GetOrAdd(type, BuildConstructorFactoryEntry);
                if (entry.HasFactory)
                    return entry.Factory();

                return Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                ForceConstructorCacheFallback($"factory failed for {type.FullName}: {ex.Message}");
                return Activator.CreateInstance(type);
            }
        }

        private static ThreadLocal<object> GetOrCreateLegacy(
            Dictionary<(Type type, int slot), ThreadLocal<object>> pools,
            Type type,
            int slot)
        {
            lock (lockObj)
            {
                if (!pools.TryGetValue((type, slot), out var tl))
                {
                    tl = new ThreadLocal<object>(() => null);
                    pools[(type, slot)] = tl;
                    ThreadLocalRegistry.Register(tl);
                }
                return tl;
            }
        }

        private static ThreadLocal<object> GetOrCreateConcurrent(
            ConcurrentDictionary<(Type type, int slot), Lazy<ThreadLocal<object>>> pools,
            Type type,
            int slot)
        {
            var lazy = pools.GetOrAdd(
                (type, slot),
                _ => new Lazy<ThreadLocal<object>>(
                    CreateAndRegisterThreadLocal,
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            );

            return lazy.Value;
        }

        private static ThreadLocal<object> CreateAndRegisterThreadLocal()
        {
            var tl = new ThreadLocal<object>(() => null);
            ThreadLocalRegistry.Register(tl);
            return tl;
        }

        private static Action<object> GetClearActionLegacy(Type type)
        {
            lock (lockObj)
            {
                if (clearActionsLegacy.TryGetValue(type, out var action))
                    return action;

                var clearMethod = type.GetMethod("Clear", Type.EmptyTypes);
                if (clearMethod == null)
                {
                    clearActionsLegacy[type] = null;
                    return null;
                }

                var param = Expression.Parameter(typeof(object), "obj");
                var call = Expression.Call(Expression.Convert(param, type), clearMethod);
                action = Expression.Lambda<Action<object>>(call, param).Compile();
                clearActionsLegacy[type] = action;
                return action;
            }
        }

        private static Action<object> GetClearActionConcurrent(Type type)
        {
            var entry = clearActionsConcurrent.GetOrAdd(type, BuildClearActionEntry);
            return entry.HasClear ? entry.Action : null;
        }

        private static ClearActionEntry BuildClearActionEntry(Type type)
        {
            var clearMethod = type.GetMethod("Clear", Type.EmptyTypes);
            if (clearMethod == null)
            {
                return new ClearActionEntry { HasClear = false };
            }

            var param = Expression.Parameter(typeof(object), "obj");
            var call = Expression.Call(Expression.Convert(param, type), clearMethod);
            var action = Expression.Lambda<Action<object>>(call, param).Compile();

            return new ClearActionEntry
            {
                HasClear = true,
                Action = action
            };
        }

        private static ConstructorFactoryEntry BuildConstructorFactoryEntry(Type type)
        {
            try
            {
                var newExpr = Expression.New(type);
                var body = Expression.Convert(newExpr, typeof(object));
                var factory = Expression.Lambda<Func<object>>(body).Compile();

                return new ConstructorFactoryEntry
                {
                    HasFactory = true,
                    Factory = factory
                };
            }
            catch
            {
                return new ConstructorFactoryEntry { HasFactory = false };
            }
        }

        private static AddActionEntry BuildAddActionEntry(Type type)
        {
            try
            {
                var addMethod = type.GetMethod("Add");
                if (addMethod == null)
                    return new AddActionEntry { HasAdd = false };

                var parameters = addMethod.GetParameters();
                if (parameters.Length != 1)
                    return new AddActionEntry { HasAdd = false, Method = addMethod };

                var listParam = Expression.Parameter(typeof(object), "list");
                var itemParam = Expression.Parameter(typeof(object), "item");
                var call = Expression.Call(
                    Expression.Convert(listParam, type),
                    addMethod,
                    Expression.Convert(itemParam, parameters[0].ParameterType)
                );

                var action = Expression.Lambda<Action<object, object>>(call, listParam, itemParam).Compile();
                return new AddActionEntry
                {
                    HasAdd = true,
                    Action = action,
                    Method = addMethod
                };
            }
            catch
            {
                return new AddActionEntry { HasAdd = false };
            }
        }
    }
}
