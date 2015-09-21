// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace DotNetty.Buffers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Contracts;
    using DotNetty.Common;
    using DotNetty.Common.Internal;
    using DotNetty.Common.Internal.Logging;
    using DotNetty.Common.Utilities;

    public class PooledByteBufferAllocator : AbstractByteBufferAllocator
    {
        static readonly IInternalLogger Logger = InternalLoggerFactory.GetInstance<PooledByteBufferAllocator>();
        static readonly int DEFAULT_NUM_HEAP_ARENA;

        static readonly int DEFAULT_PAGE_SIZE;
        static readonly int DEFAULT_MAX_ORDER; // 8192 << 11 = 16 MiB per chunk
        static readonly int DEFAULT_TINY_CACHE_SIZE;
        static readonly int DEFAULT_SMALL_CACHE_SIZE;
        static readonly int DEFAULT_NORMAL_CACHE_SIZE;
        static readonly int DEFAULT_MAX_CACHED_BUFFER_CAPACITY;
        static readonly int DEFAULT_CACHE_TRIM_INTERVAL;

        static readonly int MIN_PAGE_SIZE = 4096;
        static readonly int MAX_CHUNK_SIZE = (int)((int.MaxValue + 1L) / 2);

        static PooledByteBufferAllocator()
        {
            int defaultPageSize = SystemPropertyUtil.GetInt("io.netty.allocator.pageSize", 8192);
            Exception pageSizeFallbackCause = null;
            try
            {
                ValidateAndCalculatePageShifts(defaultPageSize);
            }
            catch (Exception t)
            {
                pageSizeFallbackCause = t;
                defaultPageSize = 8192;
            }
            DEFAULT_PAGE_SIZE = defaultPageSize;

            int defaultMaxOrder = SystemPropertyUtil.GetInt("io.netty.allocator.maxOrder", 11);
            Exception maxOrderFallbackCause = null;
            try
            {
                ValidateAndCalculateChunkSize(DEFAULT_PAGE_SIZE, defaultMaxOrder);
            }
            catch (Exception t)
            {
                maxOrderFallbackCause = t;
                defaultMaxOrder = 11;
            }
            DEFAULT_MAX_ORDER = defaultMaxOrder;

            // Determine reasonable default for nHeapArena and nDirectArena.
            // Assuming each arena has 3 chunks, the pool should not consume more than 50% of max memory.

            // Use 2 * cores by default to reduce contention as we use 2 * cores for the number of EventLoops
            // in NIO and EPOLL as well. If we choose a smaller number we will run into hotspots as allocation and
            // deallocation needs to be synchronized on the PoolArena.
            // See https://github.com/netty/netty/issues/3888
            int defaultMinNumArena = Environment.ProcessorCount * 2;
            int defaultChunkSize = DEFAULT_PAGE_SIZE << DEFAULT_MAX_ORDER;
            DEFAULT_NUM_HEAP_ARENA = Math.Max(0, SystemPropertyUtil.GetInt("dotNetty.allocator.numHeapArenas", defaultMinNumArena));

            // cache sizes
            DEFAULT_TINY_CACHE_SIZE = SystemPropertyUtil.GetInt("io.netty.allocator.tinyCacheSize", 512);
            DEFAULT_SMALL_CACHE_SIZE = SystemPropertyUtil.GetInt("io.netty.allocator.smallCacheSize", 256);
            DEFAULT_NORMAL_CACHE_SIZE = SystemPropertyUtil.GetInt("io.netty.allocator.normalCacheSize", 64);

            // 32 kb is the default maximum capacity of the cached buffer. Similar to what is explained in
            // 'Scalable memory allocation using jemalloc'
            DEFAULT_MAX_CACHED_BUFFER_CAPACITY = SystemPropertyUtil.GetInt("io.netty.allocator.maxCachedBufferCapacity", 32 * 1024);

            // the number of threshold of allocations when cached entries will be freed up if not frequently used
            DEFAULT_CACHE_TRIM_INTERVAL = SystemPropertyUtil.GetInt(
                "io.netty.allocator.cacheTrimInterval", 8192);

            if (Logger.DebugEnabled)
            {
                Logger.Debug("-Dio.netty.allocator.numHeapArenas: {}", DEFAULT_NUM_HEAP_ARENA);
                if (pageSizeFallbackCause == null)
                {
                    Logger.Debug("-Dio.netty.allocator.pageSize: {}", DEFAULT_PAGE_SIZE);
                }
                else
                {
                    Logger.Debug("-Dio.netty.allocator.pageSize: {}", DEFAULT_PAGE_SIZE, pageSizeFallbackCause);
                }
                if (maxOrderFallbackCause == null)
                {
                    Logger.Debug("-Dio.netty.allocator.maxOrder: {}", DEFAULT_MAX_ORDER);
                }
                else
                {
                    Logger.Debug("-Dio.netty.allocator.maxOrder: {}", DEFAULT_MAX_ORDER, maxOrderFallbackCause);
                }
                Logger.Debug("-Dio.netty.allocator.chunkSize: {}", DEFAULT_PAGE_SIZE << DEFAULT_MAX_ORDER);
                Logger.Debug("-Dio.netty.allocator.tinyCacheSize: {}", DEFAULT_TINY_CACHE_SIZE);
                Logger.Debug("-Dio.netty.allocator.smallCacheSize: {}", DEFAULT_SMALL_CACHE_SIZE);
                Logger.Debug("-Dio.netty.allocator.normalCacheSize: {}", DEFAULT_NORMAL_CACHE_SIZE);
                Logger.Debug("-Dio.netty.allocator.maxCachedBufferCapacity: {}", DEFAULT_MAX_CACHED_BUFFER_CAPACITY);
                Logger.Debug("-Dio.netty.allocator.cacheTrimInterval: {}", DEFAULT_CACHE_TRIM_INTERVAL);
            }

            Default = new PooledByteBufferAllocator();
        }

        public static readonly PooledByteBufferAllocator Default;

        readonly PoolArena<byte[]>[] heapArenas;
        readonly int tinyCacheSize;
        readonly int smallCacheSize;
        readonly int normalCacheSize;
        readonly IReadOnlyList<IPoolArenaMetric> heapArenaMetrics;
        readonly PoolThreadLocalCache threadCache;

        public PooledByteBufferAllocator()
            : this(DEFAULT_NUM_HEAP_ARENA, DEFAULT_PAGE_SIZE, DEFAULT_MAX_ORDER)
        {
        }

        public PooledByteBufferAllocator(int nHeapArena, int pageSize, int maxOrder)
            : this(nHeapArena, pageSize, maxOrder,
                DEFAULT_TINY_CACHE_SIZE, DEFAULT_SMALL_CACHE_SIZE, DEFAULT_NORMAL_CACHE_SIZE)
        {
        }

        public PooledByteBufferAllocator(int nHeapArena, int pageSize, int maxOrder,
            int tinyCacheSize, int smallCacheSize, int normalCacheSize)
        {
            Contract.Requires(nHeapArena >= 0);

            //super(preferDirect);
            this.threadCache = new PoolThreadLocalCache(this);
            this.tinyCacheSize = tinyCacheSize;
            this.smallCacheSize = smallCacheSize;
            this.normalCacheSize = normalCacheSize;
            int chunkSize = ValidateAndCalculateChunkSize(pageSize, maxOrder);

            int pageShifts = ValidateAndCalculatePageShifts(pageSize);

            if (nHeapArena > 0)
            {
                this.heapArenas = NewArenaArray<byte[]>(nHeapArena);
                var metrics = new List<IPoolArenaMetric>(this.heapArenas.Length);
                for (int i = 0; i < this.heapArenas.Length; i++)
                {
                    var arena = new HeapArena(this, pageSize, maxOrder, pageShifts, chunkSize);
                    this.heapArenas[i] = arena;
                    metrics.Add(arena);
                }
                this.heapArenaMetrics = metrics.AsReadOnly();
            }
            else
            {
                this.heapArenas = null;
                this.heapArenaMetrics = new List<IPoolArenaMetric>();
            }
        }

        static PoolArena<T>[] NewArenaArray<T>(int size)
        {
            return new PoolArena<T>[size];
        }

        static int ValidateAndCalculatePageShifts(int pageSize)
        {
            Contract.Requires(pageSize >= MIN_PAGE_SIZE);
            Contract.Requires((pageSize & pageSize - 1) == 0, "Expected power of 2");

            // Logarithm base 2. At this point we know that pageSize is a power of two.
            return IntegerExtensions.Log2(pageSize);
        }

        static int ValidateAndCalculateChunkSize(int pageSize, int maxOrder)
        {
            if (maxOrder > 14)
            {
                throw new ArgumentException("maxOrder: " + maxOrder + " (expected: 0-14)");
            }

            // Ensure the resulting chunkSize does not overflow.
            int chunkSize = pageSize;
            for (int i = maxOrder; i > 0; i--)
            {
                if (chunkSize > MAX_CHUNK_SIZE >> 1)
                {
                    throw new ArgumentException(string.Format(
                        "pageSize ({0}) << maxOrder ({1}) must not exceed {2}", pageSize, maxOrder, MAX_CHUNK_SIZE));
                }
                chunkSize <<= 1;
            }
            return chunkSize;
        }

        protected override IByteBuffer NewBuffer(int initialCapacity, int maxCapacity)
        {
            PoolThreadCache<byte[]> cache = this.threadCache.Value;
            PoolArena<byte[]> heapArena = cache.HeapArena;

            IByteBuffer buf;
            if (heapArena != null)
            {
                buf = heapArena.Allocate(cache, initialCapacity, maxCapacity);
            }
            else
            {
                buf = new UnpooledHeapByteBuffer(this, initialCapacity, maxCapacity);
            }

            return ToLeakAwareBuffer(buf);
        }

        sealed class PoolThreadLocalCache : FastThreadLocal<PoolThreadCache<byte[]>>
        {
            readonly PooledByteBufferAllocator owner;

            public PoolThreadLocalCache(PooledByteBufferAllocator owner)
            {
                this.owner = owner;
            }

            protected override PoolThreadCache<byte[]> GetInitialValue()
            {
                lock (this)
                {
                    PoolArena<byte[]> heapArena = this.GetLeastUsedArena(this.owner.heapArenas);

                    return new PoolThreadCache<byte[]>(
                        heapArena, this.owner.tinyCacheSize, this.owner.smallCacheSize, this.owner.normalCacheSize,
                        DEFAULT_MAX_CACHED_BUFFER_CAPACITY, DEFAULT_CACHE_TRIM_INTERVAL);
                }
            }

            protected override void OnRemoval(PoolThreadCache<byte[]> threadCache)
            {
                threadCache.Free();
            }

            PoolArena<T> GetLeastUsedArena<T>(PoolArena<T>[] arenas)
            {
                if (arenas == null || arenas.Length == 0)
                {
                    return null;
                }

                PoolArena<T> minArena = arenas[0];
                for (int i = 1; i < arenas.Length; i++)
                {
                    PoolArena<T> arena = arenas[i];
                    if (arena.NumThreadCaches < minArena.NumThreadCaches)
                    {
                        minArena = arena;
                    }
                }

                return minArena;
            }
        }

        /**
 * Return the number of heap arenas.
 */

        public int NumHeapArenas()
        {
            return this.heapArenaMetrics.Count;
        }

        /**
     * Return a {@link List} of all heap {@link PoolArenaMetric}s that are provided by this pool.
     */

        public IReadOnlyList<IPoolArenaMetric> HeapArenas()
        {
            return this.heapArenaMetrics;
        }

        /**
     * Return the number of thread local caches used by this {@link PooledByteBufferAllocator}.
     */

        public int NumThreadLocalCaches()
        {
            PoolArena<byte[]>[] arenas = this.heapArenas;
            if (arenas == null)
            {
                return 0;
            }

            int total = 0;
            for (int i = 0; i < arenas.Length; i++)
            {
                total += arenas[i].NumThreadCaches;
            }

            return total;
        }

        /// Return the size of the tiny cache.
        public int TinyCacheSize()
        {
            return this.tinyCacheSize;
        }

        /// Return the size of the small cache.
        public int SmallCacheSize()
        {
            return this.smallCacheSize;
        }

        /// Return the size of the normal cache.
        public int NormalCacheSize()
        {
            return this.normalCacheSize;
        }

        internal PoolThreadCache<T> ThreadCache<T>()
        {
            return (PoolThreadCache<T>)(object)this.threadCache.Value;
        }

        // Too noisy at the moment.
        //
        //public String toString() {
        //    StringBuilder buf = new StringBuilder();
        //    int heapArenasLen = heapArenas == null ? 0 : heapArenas.length;
        //    buf.append(heapArenasLen);
        //    buf.append(" heap arena(s):");
        //    buf.append(StringUtil.NEWLINE);
        //    if (heapArenasLen > 0) {
        //        for (PoolArena<byte[]> a: heapArenas) {
        //            buf.append(a);
        //        }
        //    }
        //
        //    int directArenasLen = directArenas == null ? 0 : directArenas.length;
        //
        //    buf.append(directArenasLen);
        //    buf.append(" direct arena(s):");
        //    buf.append(StringUtil.NEWLINE);
        //    if (directArenasLen > 0) {
        //        for (PoolArena<ByteBuffer> a: directArenas) {
        //            buf.append(a);
        //        }
        //    }
        //
        //    return buf.toString();
        //}
    }
}