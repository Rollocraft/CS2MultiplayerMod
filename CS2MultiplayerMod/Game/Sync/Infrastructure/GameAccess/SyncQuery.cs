using System;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Read-only component sets for sync queries. A fresh array per call: <see cref="EntityQueryDesc"/>
    /// keeps the reference it is given.
    /// </summary>
    internal static class SyncQuery
    {
        /// <summary>Hands the query's current entities to <paramref name="use"/>; skips an empty query.</summary>
        public static void WithEntities(EntityQuery query, Action<NativeArray<Entity>> use)
        {
            if (query.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try { use(entities); }
            finally { entities.Dispose(); }
        }

        public static ComponentType[] ReadOnly<T1>() => new[]
        {
            ComponentType.ReadOnly<T1>(),
        };

        public static ComponentType[] ReadOnly<T1, T2>() => new[]
        {
            ComponentType.ReadOnly<T1>(), ComponentType.ReadOnly<T2>(),
        };

        public static ComponentType[] ReadOnly<T1, T2, T3>() => new[]
        {
            ComponentType.ReadOnly<T1>(), ComponentType.ReadOnly<T2>(),
            ComponentType.ReadOnly<T3>(),
        };

        public static ComponentType[] ReadOnly<T1, T2, T3, T4>() => new[]
        {
            ComponentType.ReadOnly<T1>(), ComponentType.ReadOnly<T2>(),
            ComponentType.ReadOnly<T3>(), ComponentType.ReadOnly<T4>(),
        };

        public static ComponentType[] ReadOnly<T1, T2, T3, T4, T5>() => new[]
        {
            ComponentType.ReadOnly<T1>(), ComponentType.ReadOnly<T2>(),
            ComponentType.ReadOnly<T3>(), ComponentType.ReadOnly<T4>(),
            ComponentType.ReadOnly<T5>(),
        };

        public static ComponentType[] ReadOnly<T1, T2, T3, T4, T5, T6>() => new[]
        {
            ComponentType.ReadOnly<T1>(), ComponentType.ReadOnly<T2>(),
            ComponentType.ReadOnly<T3>(), ComponentType.ReadOnly<T4>(),
            ComponentType.ReadOnly<T5>(), ComponentType.ReadOnly<T6>(),
        };

        public static ComponentType[] ReadOnly<T1, T2, T3, T4, T5, T6, T7>() => new[]
        {
            ComponentType.ReadOnly<T1>(), ComponentType.ReadOnly<T2>(),
            ComponentType.ReadOnly<T3>(), ComponentType.ReadOnly<T4>(),
            ComponentType.ReadOnly<T5>(), ComponentType.ReadOnly<T6>(),
            ComponentType.ReadOnly<T7>(),
        };

        public static ComponentType[] ReadOnly<T1, T2, T3, T4, T5, T6, T7, T8>() => new[]
        {
            ComponentType.ReadOnly<T1>(), ComponentType.ReadOnly<T2>(),
            ComponentType.ReadOnly<T3>(), ComponentType.ReadOnly<T4>(),
            ComponentType.ReadOnly<T5>(), ComponentType.ReadOnly<T6>(),
            ComponentType.ReadOnly<T7>(), ComponentType.ReadOnly<T8>(),
        };
    }
}
