using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Sync.ModSync;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>
    /// Reads and writes one third-party type through a closed generic built per type, so every access
    /// uses the engine's typed calls; untyped raw access faults natively on a wrong layout assumption.
    /// </summary>
    internal abstract class ModTypeAccessor
    {
        public Type Type { get; private set; }
        public ModTypeDescriptor Descriptor { get; private set; }
        public ModFieldPlan Plan { get; private set; }
        public ComponentType ComponentType { get; private set; }

        public ModTypeKind Kind => Descriptor.Kind;

        /// <summary>Builds the accessor for a type the catalogue has already accepted.</summary>
        public static ModTypeAccessor Create(Type type, ModTypeKind kind, ModFieldPlan plan,
            ModTypeDescriptor descriptor)
        {
            ModTypeAccessor accessor;
            switch (kind)
            {
                case ModTypeKind.Tag:
                    accessor = new TagAccessor();
                    break;
                case ModTypeKind.Buffer:
                    accessor = (ModTypeAccessor)Activator.CreateInstance(
                        typeof(BufferAccessor<>).MakeGenericType(type));
                    break;
                default:
                    accessor = (ModTypeAccessor)Activator.CreateInstance(
                        typeof(ComponentAccessor<>).MakeGenericType(type));
                    break;
            }

            accessor.Type = type;
            accessor.Plan = plan;
            accessor.Descriptor = descriptor;
            accessor.ComponentType = new ComponentType(type);
            return accessor;
        }

        public bool Has(EntityManager entities, Entity entity) => entities.HasComponent(entity, ComponentType);

        public void Remove(EntityManager entities, Entity entity)
        {
            if (Has(entities, entity)) entities.RemoveComponent(entity, ComponentType);
        }

        /// <summary>Appends the value and returns the element count (1 component, 0 tag, buffer length).</summary>
        public abstract int ReadInto(EntityManager entities, Entity entity, List<ModLeaf> sink,
            Func<Entity, ModEntityRef> translate);

        /// <summary>Writes an arriving value, adding the type to the entity if it is not there yet.</summary>
        public abstract void Apply(EntityManager entities, Entity entity, ModLeaf[] leaves,
            int elementCount, Func<ModEntityRef, Entity> translate);

        /// <summary>A component whose presence is its whole value.</summary>
        internal sealed class TagAccessor : ModTypeAccessor
        {
            public override int ReadInto(EntityManager entities, Entity entity, List<ModLeaf> sink,
                Func<Entity, ModEntityRef> translate) => 0;

            public override void Apply(EntityManager entities, Entity entity, ModLeaf[] leaves,
                int elementCount, Func<ModEntityRef, Entity> translate)
            {
                if (!Has(entities, entity)) entities.AddComponent(entity, ComponentType);
            }
        }

        internal sealed class ComponentAccessor<T> : ModTypeAccessor where T : unmanaged, IComponentData
        {
            public override int ReadInto(EntityManager entities, Entity entity, List<ModLeaf> sink,
                Func<Entity, ModEntityRef> translate)
            {
                T value = entities.GetComponentData<T>(entity);
                var leaves = new ModLeaf[Plan.Count];
                Plan.Read(value, leaves, 0, translate);
                sink.AddRange(leaves);
                return 1;
            }

            public override void Apply(EntityManager entities, Entity entity, ModLeaf[] leaves,
                int elementCount, Func<ModEntityRef, Entity> translate)
            {
                if (elementCount <= 0) return;

                // Start from the current value so fields the sender's plan lacks are kept, not zeroed.
                object boxed = Has(entities, entity)
                    ? (object)entities.GetComponentData<T>(entity)
                    : default(T);

                boxed = Plan.Write(boxed, leaves, 0, translate);

                if (!Has(entities, entity)) entities.AddComponent(entity, ComponentType);
                entities.SetComponentData(entity, (T)boxed);
            }
        }

        internal sealed class BufferAccessor<T> : ModTypeAccessor where T : unmanaged, IBufferElementData
        {
            public override int ReadInto(EntityManager entities, Entity entity, List<ModLeaf> sink,
                Func<Entity, ModEntityRef> translate)
            {
                DynamicBuffer<T> buffer = entities.GetBuffer<T>(entity, true);
                int stride = Plan.Count;
                var leaves = new ModLeaf[stride];
                for (int i = 0; i < buffer.Length; i++)
                {
                    Plan.Read(buffer[i], leaves, 0, translate);
                    sink.AddRange(leaves);
                }
                return buffer.Length;
            }

            public override void Apply(EntityManager entities, Entity entity, ModLeaf[] leaves,
                int elementCount, Func<ModEntityRef, Entity> translate)
            {
                DynamicBuffer<T> buffer = Has(entities, entity)
                    ? entities.GetBuffer<T>(entity, false)
                    : entities.AddBuffer<T>(entity);

                buffer.Clear();
                int stride = Plan.Count;
                for (int i = 0; i < elementCount; i++)
                {
                    object element = default(T);
                    element = Plan.Write(element, leaves, i * stride, translate);
                    buffer.Add((T)element);
                }
            }
        }
    }
}
