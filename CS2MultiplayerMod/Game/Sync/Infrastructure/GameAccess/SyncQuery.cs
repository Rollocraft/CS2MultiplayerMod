using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Terse component lists for the <see cref="EntityQueryDesc"/> shapes the sync systems build.
    ///
    /// Every sync query is read-only - a system observes the world and mirrors it, it never claims
    /// write access through a query - so each entry of an All/Any/None set was one full line of
    /// <c>ComponentType.ReadOnly&lt;X&gt;(),</c>. Naming the components as type arguments says the
    /// same thing on one line, which is what makes a query's shape readable at a glance:
    ///
    /// <code>
    /// _movedObjects = GetEntityQuery(new EntityQueryDesc
    /// {
    ///     All = SyncQuery.ReadOnly&lt;Updated, MovedLocation, PrefabRef, Transform&gt;(),
    ///     None = SyncQuery.ReadOnly&lt;Temp, Owner, Deleted, Created&gt;(),
    /// });
    /// </code>
    ///
    /// A fresh array per call, deliberately: <see cref="EntityQueryDesc"/> keeps the reference it
    /// is given, so a shared static array would be one query's set handed to every other query.
    /// These run once per system in OnCreate, so the allocation costs nothing that matters.
    ///
    /// A query whose set needs a per-component comment keeps the long form - the comment is the
    /// reason that component is there, and it has nowhere to live in the short one.
    /// </summary>
    internal static class SyncQuery
    {
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
