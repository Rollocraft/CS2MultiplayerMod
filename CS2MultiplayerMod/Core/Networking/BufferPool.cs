using System;
using System.Collections.Concurrent;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// High-performance shared buffer pool for packet serialization, blob splitting,
    /// and compression. Drastically cuts GC pressure across high-frequency sync operations.
    /// </summary>
    public static class BufferPool
    {
        private static readonly ConcurrentQueue<byte[]> PoolSmall = new ConcurrentQueue<byte[]>();
        private static readonly ConcurrentQueue<byte[]> PoolMedium = new ConcurrentQueue<byte[]>();
        private static readonly ConcurrentQueue<byte[]> PoolLarge = new ConcurrentQueue<byte[]>();

        private static int _smallCount;
        private static int _medCount;
        private static int _largeCount;

        private const int SmallThreshold = 4 * 1024;       // 4 KB
        private const int MediumThreshold = 64 * 1024;     // 64 KB
        private const int LargeThreshold = 256 * 1024;     // 256 KB (BlobChunkBytes)

        /// <summary>
        /// Rent a byte array of at least <paramref name="minimumLength"/> bytes.
        /// Must be returned via <see cref="Return"/> when done.
        /// </summary>
        public static byte[] Rent(int minimumLength)
        {
            if (minimumLength <= SmallThreshold)
            {
                if (PoolSmall.TryDequeue(out byte[] buf))
                {
                    System.Threading.Interlocked.Decrement(ref _smallCount);
                    return buf;
                }
                return new byte[SmallThreshold];
            }
            if (minimumLength <= MediumThreshold)
            {
                if (PoolMedium.TryDequeue(out byte[] buf))
                {
                    System.Threading.Interlocked.Decrement(ref _medCount);
                    return buf;
                }
                return new byte[MediumThreshold];
            }
            if (minimumLength <= LargeThreshold)
            {
                if (PoolLarge.TryDequeue(out byte[] buf))
                {
                    System.Threading.Interlocked.Decrement(ref _largeCount);
                    return buf;
                }
                return new byte[LargeThreshold];
            }
            return new byte[minimumLength];
        }

        /// <summary>
        /// Return a previously rented byte array to the pool.
        /// </summary>
        public static void Return(byte[] array, bool clearArray = false)
        {
            if (array == null) return;
            if (clearArray) Array.Clear(array, 0, array.Length);

            if (array.Length == SmallThreshold)
            {
                if (System.Threading.Interlocked.Increment(ref _smallCount) <= 64)
                    PoolSmall.Enqueue(array);
                else
                    System.Threading.Interlocked.Decrement(ref _smallCount);
            }
            else if (array.Length == MediumThreshold)
            {
                if (System.Threading.Interlocked.Increment(ref _medCount) <= 32)
                    PoolMedium.Enqueue(array);
                else
                    System.Threading.Interlocked.Decrement(ref _medCount);
            }
            else if (array.Length == LargeThreshold)
            {
                if (System.Threading.Interlocked.Increment(ref _largeCount) <= 16)
                    PoolLarge.Enqueue(array);
                else
                    System.Threading.Interlocked.Decrement(ref _largeCount);
            }
        }

        // Pinned 64 MB Large Object Heap (LOH) Slab to completely avoid GC fragmentation during save transfers
        private static readonly object SlabLock = new object();
        private static byte[] _preallocatedSlab;
        private static bool _slabInUse;

        public static byte[] RentLargeSlab(int minimumLength)
        {
            if (minimumLength <= 64 * 1024 * 1024)
            {
                lock (SlabLock)
                {
                    if (!_slabInUse)
                    {
                        if (_preallocatedSlab == null)
                        {
                            _preallocatedSlab = new byte[64 * 1024 * 1024];
                        }
                        _slabInUse = true;
                        return _preallocatedSlab;
                    }
                }
            }
            return Rent(minimumLength);
        }

        public static void ReturnLargeSlab(byte[] slab)
        {
            if (slab == null) return;
            lock (SlabLock)
            {
                if (ReferenceEquals(slab, _preallocatedSlab))
                {
                    _slabInUse = false;
                    return;
                }
            }
            Return(slab);
        }
    }
}
