using System;
using System.Collections.Generic;
using System.Threading;

namespace Tungsten
{
    /// <summary>
    /// Central registry for tracking ThreadLocal instances to ensure proper disposal.
    /// </summary>
    public static class ThreadLocalRegistry
    {
        private static readonly HashSet<IDisposable> threadLocals = new HashSet<IDisposable>();
        private static readonly object lockObj = new object();

        /// <summary>
        /// Register a ThreadLocal instance for disposal tracking.
        /// Call this during optimizer initialization.
        /// </summary>
        public static void Register<T>(ThreadLocal<T> threadLocal)
        {
            if (threadLocal == null)
                return;

            lock (lockObj)
            {
                threadLocals.Add(threadLocal);
            }
        }

        /// <summary>
        /// Unregister a ThreadLocal instance (for cleanup on initialization failure).
        /// </summary>
        public static void Unregister<T>(ThreadLocal<T> threadLocal)
        {
            if (threadLocal == null)
                return;

            lock (lockObj)
            {
                threadLocals.Remove(threadLocal);
            }
        }

        /// <summary>
        /// Unregister multiple ThreadLocal instances at once.
        /// </summary>
        public static void UnregisterAll(params IDisposable[] threadLocalsToRemove)
        {
            if (threadLocalsToRemove == null || threadLocalsToRemove.Length == 0)
                return;

            lock (lockObj)
            {
                foreach (var tl in threadLocalsToRemove)
                {
                    if (tl != null)
                        threadLocals.Remove(tl);
                }
            }
        }

        /// <summary>
        /// Dispose all registered ThreadLocal instances.
        /// Call this during mod disposal.
        /// Returns the number of instances disposed.
        /// </summary>
        public static int DisposeAll()
        {
            IDisposable[] snapshot;
            lock (lockObj)
            {
                snapshot = new IDisposable[threadLocals.Count];
                threadLocals.CopyTo(snapshot);
                threadLocals.Clear();
            }

            foreach (var threadLocal in snapshot)
            {
                try
                {
                    threadLocal?.Dispose();
                }
                catch
                {
                    // Suppress exceptions during cleanup
                }
            }

            return snapshot.Length;
        }

        /// <summary>
        /// Get count of registered ThreadLocal instances (for diagnostics).
        /// </summary>
        public static int Count
        {
            get
            {
                lock (lockObj)
                {
                    return threadLocals.Count;
                }
            }
        }

        /// <summary>
        /// Get detailed statistics about ThreadLocal collections.
        /// Returns (totalCapacity, totalCount, instanceCount).
        /// </summary>
        public static (int totalCapacity, int totalCount, int instanceCount) GetStatistics()
        {
            IDisposable[] snapshot;
            lock (lockObj)
            {
                snapshot = new IDisposable[threadLocals.Count];
                threadLocals.CopyTo(snapshot);
            }

            int totalCapacity = 0;
            int totalCount = 0;
            int instanceCount = snapshot.Length;

            foreach (var threadLocal in snapshot)
            {
                try
                {
                    var type = threadLocal.GetType();
                    if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ThreadLocal<>))
                        continue;

                    var isValueCreatedProperty = type.GetProperty("IsValueCreated");
                    if (isValueCreatedProperty == null || isValueCreatedProperty.PropertyType != typeof(bool))
                        continue;

                    bool isValueCreated = (bool)isValueCreatedProperty.GetValue(threadLocal);
                    if (!isValueCreated)
                        continue;

                    var valueProperty = type.GetProperty("Value");
                    if (valueProperty == null)
                        continue;

                    var value = valueProperty.GetValue(threadLocal);
                    if (value == null)
                        continue;

                    var valueType = value.GetType();

                    // Check for List<T>
                    if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(List<>))
                    {
                        var capacityProp = valueType.GetProperty("Capacity");
                        var countProp = valueType.GetProperty("Count");
                        if (capacityProp != null && countProp != null)
                        {
                            totalCapacity += (int)capacityProp.GetValue(value);
                            totalCount += (int)countProp.GetValue(value);
                        }
                    }
                    // Check for Dictionary<TKey, TValue>
                    else if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    {
                        var countProp = valueType.GetProperty("Count");
                        if (countProp != null)
                        {
                            int count = (int)countProp.GetValue(value);
                            totalCount += count;
                            totalCapacity += count; // Dictionary doesn't expose capacity, use count as estimate
                        }
                    }
                    // Check for HashSet<T>
                    else if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(HashSet<>))
                    {
                        var countProp = valueType.GetProperty("Count");
                        if (countProp != null)
                        {
                            int count = (int)countProp.GetValue(value);
                            totalCount += count;
                            totalCapacity += count; // HashSet doesn't expose capacity, use count as estimate
                        }
                    }
                }
                catch
                {
                    // Skip on error
                }
            }

            return (totalCapacity, totalCount, instanceCount);
        }
    }
}
