using System;
using System.Reflection;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>Calls EntityManager.CreateEntity(EntityArchetype) through reflection.</summary>
    internal static class ArchetypeEntityFactory
    {
        private static readonly MethodInfo CreateWithArchetype = typeof(EntityManager).GetMethod(
            "CreateEntity",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[] { typeof(EntityArchetype) },
            null);

        internal static Entity Create(EntityManager entityManager, EntityArchetype archetype)
        {
            if (CreateWithArchetype == null)
                throw new MissingMethodException(typeof(EntityManager).FullName,
                    "CreateEntity(EntityArchetype)");

            return (Entity)CreateWithArchetype.Invoke(entityManager, new object[] { archetype });
        }
    }
}
