using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Burst-compatible zero-copy serialization utilities for reading and writing directly
    /// into unmanaged NativeArray and pointer memory blocks.
    /// </summary>
    public static unsafe class NativeBufferCodec
    {
        public static void CopyToNative<T>(byte[] source, int sourceOffset, NativeArray<T> destination, int destElementOffset, int byteCount) where T : struct
        {
            if (source == null || byteCount <= 0) return;
            fixed (byte* srcPtr = &source[sourceOffset])
            {
                byte* dstPtr = (byte*)destination.GetUnsafePtr() + (destElementOffset * UnsafeUtility.SizeOf<T>());
                UnsafeUtility.MemCpy(dstPtr, srcPtr, byteCount);
            }
        }

        public static void CopyFromNative<T>(NativeArray<T> source, int sourceElementOffset, byte[] destination, int destOffset, int byteCount) where T : struct
        {
            if (destination == null || byteCount <= 0) return;
            byte* srcPtr = (byte*)source.GetUnsafeReadOnlyPtr() + (sourceElementOffset * UnsafeUtility.SizeOf<T>());
            fixed (byte* dstPtr = &destination[destOffset])
            {
                UnsafeUtility.MemCpy(dstPtr, srcPtr, byteCount);
            }
        }
    }
}
