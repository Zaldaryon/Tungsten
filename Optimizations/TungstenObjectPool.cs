using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace Tungsten
{
    /// <summary>
    /// Bounded object pool with automatic shrinking and capacity management.
    /// Uses ConcurrentStack (LIFO) for cache locality.
    /// </summary>
    public class TungstenObjectPool<T> where T : class
    {
        private readonly ConcurrentStack<T> pool;
        private readonly Func<T> factory;
        private readonly Action<T> reset;
        private readonly int maxPoolSize;
        private readonly int targetCapacity;
        private int totalCreated;
        private long lastShrinkTicks;
        private readonly long shrinkIntervalTicks;
        private int returnCounter;
        private const int shrinkCheckInterval = 1000;
        private readonly object shrinkLock = new object(); // v1.10.3: Prevent race condition

        // Compiled delegates for near-native performance
        private static readonly Action<T> trimExcess;
        private static readonly Func<T, int> getCapacity;
        private static readonly bool isListType;
        private static readonly bool isDictionaryType;
        private static readonly bool isHashSetType;

        /// <summary>
        /// Static constructor to compile expression trees once per type.
        /// </summary>
        static TungstenObjectPool()
        {
            var type = typeof(T);

            // Check if T implements IList (List<>)
            isListType = typeof(System.Collections.IList).IsAssignableFrom(type);

            // Check if T implements IDictionary (Dictionary<,>)
            isDictionaryType = typeof(System.Collections.IDictionary).IsAssignableFrom(type);

            // Check if T is HashSet<>
            isHashSetType = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>);

            // Compile TrimExcess method as expression tree
            var trimMethod = type.GetMethod("TrimExcess", Type.EmptyTypes);
            if (trimMethod != null)
            {
                try
                {
                    var param = Expression.Parameter(typeof(T), "obj");
                    var call = Expression.Call(param, trimMethod);
                    trimExcess = Expression.Lambda<Action<T>>(call, param).Compile();
                }
                catch
                {
                    trimExcess = null;
                }
            }

            // Compile Capacity property getter as expression tree (for List<T>)
            if (isListType)
            {
                var capacityProperty = type.GetProperty("Capacity");
                if (capacityProperty != null)
                {
                    try
                    {
                        var param = Expression.Parameter(typeof(T), "obj");
                        var property = Expression.Property(param, capacityProperty);
                        getCapacity = Expression.Lambda<Func<T, int>>(property, param).Compile();
                    }
                    catch
                    {
                        getCapacity = null;
                    }
                }
            }
        }

        public TungstenObjectPool(
            Func<T> factory,
            Action<T> reset = null,
            int maxPoolSize = 32,
            int targetCapacity = 100,
            int shrinkIntervalSeconds = 60)
        {
            this.pool = new ConcurrentStack<T>();
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
            this.reset = reset;
            this.maxPoolSize = maxPoolSize;
            this.targetCapacity = targetCapacity;
            this.totalCreated = 0;
            this.lastShrinkTicks = DateTime.UtcNow.Ticks;
            this.shrinkIntervalTicks = TimeSpan.FromSeconds(shrinkIntervalSeconds).Ticks;
        }

        /// <summary>
        /// Get an object from the pool, or create a new one if pool is empty.
        /// </summary>
        public T Get()
        {
            if (pool.TryPop(out T item))
            {
                return item;
            }

            Interlocked.Increment(ref totalCreated);
            return factory();
        }

        /// <summary>
        /// Return an object to the pool. Performs reset and capacity management.
        /// Even if reset/trim fails, object is returned to pool to prevent leaks.
        /// v1.9.3: Check shrink only every N returns (counter-based, not timestamp).
        /// </summary>
        public void Return(T item)
        {
            if (item == null) return;

            try
            {
                reset?.Invoke(item);
                TrimCapacity(item);
            }
            catch (Exception ex)
            {
                TungstenMod.Instance?.Api?.Logger?.Warning($"[Tungsten] Pool reset/trim failed for {typeof(T).Name}: {ex.Message}");
            }

            // v1.9.3: Check shrink only every N returns (counter-based, not timestamp)
            if (Interlocked.Increment(ref returnCounter) % shrinkCheckInterval == 0)
                MaybeShrinkPool();

            if (pool.Count < maxPoolSize)
            {
                pool.Push(item);
            }
        }

        /// <summary>
        /// Trim excess capacity from collections to reduce memory footprint.
        /// </summary>
        private void TrimCapacity(T item)
        {
            if (trimExcess == null)
                return;

            // Handle List<T>
            if (isListType && getCapacity != null)
            {
                int capacity = getCapacity(item); // Compiled delegate - fast!
                if (capacity > targetCapacity * 2)
                {
                    trimExcess(item); // Compiled delegate - fast!
                }
            }
            // Handle Dictionary<TKey, TValue>
            else if (isDictionaryType)
            {
                var dict = item as System.Collections.IDictionary;
                if (dict != null && dict.Count == 0)
                {
                    trimExcess(item);
                }
            }
            // Handle HashSet<T>
            else if (isHashSetType)
            {
                trimExcess(item);
            }
        }

        /// <summary>
        /// Periodically shrink the pool to release memory during low activity.
        /// Thread-safe: only one thread performs shrinking per interval.
        /// v1.10.3: Fixed race condition with proper lock.
        /// </summary>
        private void MaybeShrinkPool()
        {
            long lastShrink = Interlocked.Read(ref lastShrinkTicks);
            long currentTicks = DateTime.UtcNow.Ticks;

            if (currentTicks - lastShrink <= shrinkIntervalTicks)
                return;

            // v1.10.3: Use lock to prevent race condition
            lock (shrinkLock)
            {
                // Re-check after acquiring lock
                lastShrink = Interlocked.Read(ref lastShrinkTicks);
                if (currentTicks - lastShrink <= shrinkIntervalTicks)
                    return;

                Interlocked.Exchange(ref lastShrinkTicks, currentTicks);

                int currentCount = pool.Count;
                if (currentCount <= 4)
                    return;

                int targetSize = Math.Max(4, currentCount / 2);
                int removed = 0;

                while (pool.Count > targetSize && pool.TryPop(out _))
                {
                    removed++;
                    if (removed >= currentCount / 2)
                        break;
                }

                if (removed > 0 && TungstenMod.Instance?.Api != null)
                {
                    TungstenMod.Instance.Api.Logger.Debug(
                        $"[Tungsten] Pool<{typeof(T).Name}> shrunk: removed {removed} objects, {pool.Count} remain");
                }
            }
        }

        /// <summary>
        /// Get pool statistics for monitoring.
        /// </summary>
        public PoolStats GetStats()
        {
            return new PoolStats
            {
                PooledCount = pool.Count,
                TotalCreated = totalCreated,
                MaxPoolSize = maxPoolSize
            };
        }
    }

    public struct PoolStats
    {
        public int PooledCount;
        public int TotalCreated;
        public int MaxPoolSize;
    }

    /// <summary>
    /// Helper class to create common pool types.
    /// </summary>
    public static class TungstenPools
    {
        public static TungstenObjectPool<List<T>> CreateListPool<T>(
            int maxPoolSize = 32,
            int targetCapacity = 100,
            int shrinkIntervalSeconds = 60)
        {
            return new TungstenObjectPool<List<T>>(
                factory: () => new List<T>(targetCapacity),
                reset: list => list.Clear(),
                maxPoolSize: maxPoolSize,
                targetCapacity: targetCapacity,
                shrinkIntervalSeconds: shrinkIntervalSeconds
            );
        }

        public static TungstenObjectPool<Dictionary<TKey, TValue>> CreateDictionaryPool<TKey, TValue>(
            int maxPoolSize = 32,
            int targetCapacity = 100,
            int shrinkIntervalSeconds = 60)
        {
            return new TungstenObjectPool<Dictionary<TKey, TValue>>(
                factory: () => new Dictionary<TKey, TValue>(targetCapacity),
                reset: dict => dict.Clear(),
                maxPoolSize: maxPoolSize,
                targetCapacity: targetCapacity,
                shrinkIntervalSeconds: shrinkIntervalSeconds
            );
        }

        public static TungstenObjectPool<HashSet<T>> CreateHashSetPool<T>(
            int maxPoolSize = 32,
            int targetCapacity = 100,
            int shrinkIntervalSeconds = 60)
        {
            return new TungstenObjectPool<HashSet<T>>(
                factory: () => new HashSet<T>(),
                reset: set => set.Clear(),
                maxPoolSize: maxPoolSize,
                targetCapacity: targetCapacity,
                shrinkIntervalSeconds: shrinkIntervalSeconds
            );
        }
    }
}
