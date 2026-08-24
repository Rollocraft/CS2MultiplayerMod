using System;
using System.Buffers;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// High-performance shared buffer pool for packet serialization, blob splitting,
    /// and compression. Drastically cuts GC pressure across high-frequency sync operations.
    /// </summary>
    public static class BufferPool
    {
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

        /// <summary>
        /// Rent a byte array of at least <paramref name="minimumLength"/> bytes.
        /// Must be returned via <see cref="Return"/> when done.
        /// </summary>
        public static byte[] Rent(int minimumLength)
        {
            return Pool.Rent(minimumLength);
        }

        /// <summary>
        /// Return a previously rented byte array to the pool.
        /// </summary>
        public static void Return(byte[] array, bool clearArray = false)
        {
            if (array == null) return;
            try
            {
                Pool.Return(array, clearArray);
            }
            catch
            {
                // Defensive guard: ignore if pool was disposed or array was not from pool
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
            return Pool.Rent(minimumLength);
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
