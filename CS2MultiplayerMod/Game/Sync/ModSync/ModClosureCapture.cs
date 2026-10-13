using System.Collections.Generic;
using CS2MultiplayerMod.Core.Sync.ModSync;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>
    /// Collects everything another mod stores against one carrier into one portable, idempotent
    /// snapshot. The unit is the whole closure because mods rebuild their bookkeeping entities on
    /// every edit; a per-component diff would describe entities already gone.
    /// </summary>
    internal sealed class ModClosureCapture
    {
        private readonly EntityManager _entities;
        private readonly ModCarrierIdentity _identity;
        private readonly ModTypeBinding _binding;
        private readonly ModComponentCatalog _catalog;

        private readonly Dictionary<Entity, int> _satelliteIndex = new Dictionary<Entity, int>();
        private readonly List<Entity> _satelliteEntities = new List<Entity>();
        private readonly List<int> _satelliteDepth = new List<int>();

        /// <summary>Why the closure could not be described, or null when it could.</summary>
        public string Rejection { get; private set; }

        /// <summary>
        /// Carrier types not in the host's table, accumulated so the caller reports each once.
        /// </summary>
        public readonly HashSet<string> TypesNotInSession =
            new HashSet<string>(System.StringComparer.Ordinal);

        /// <summary>The bookkeeping entities this closure owns, in snapshot order.</summary>
        public IList<Entity> SatelliteEntities => _satelliteEntities;

        public ModClosureCapture(EntityManager entities, ModCarrierIdentity identity,
            ModTypeBinding binding, ModComponentCatalog catalog)
        {
            _entities = entities;
            _identity = identity;
            _binding = binding;
            _catalog = catalog;
        }

        /// <summary>
        /// The snapshot for <paramref name="carrier"/>, or null with <see cref="Rejection"/> set. Empty is a
        /// success: that is how a removal travels.
        /// </summary>
        public ModStateSnapshot Capture(Entity carrier, ModEntityRef carrierRef)
        {
            Rejection = null;
            _satelliteIndex.Clear();
            _satelliteEntities.Clear();
            _satelliteDepth.Clear();

            if (!ModEntityEligibility.IsLive(_entities, carrier))
            {
                Rejection = "belongs to a temporary or deleted object";
                return null;
            }

            var snapshot = new ModStateSnapshot
            {
                Carrier = carrierRef,
                CarrierValues = ReadEntity(carrier, 0)
            };
            if (Rejection != null) return null;

            // The list grows while walked; depth is enforced where a reference is followed.
            for (int i = 0; i < _satelliteEntities.Count; i++)
            {
                snapshot.Satellites.Add(ReadEntity(_satelliteEntities[i], _satelliteDepth[i]));
                if (Rejection != null) return null;
            }

            return snapshot;
        }

        private ModEntityValues ReadEntity(Entity entity, int depth)
        {
            var values = new ModEntityValues();
            var leaves = new List<ModLeaf>();

            for (int i = 0; i < _catalog.Entries.Count; i++)
            {
                ModCatalogEntry entry = _catalog.Entries[i];
                ModTypeAccessor accessor = entry.Accessor;
                if (!accessor.Has(_entities, entity)) continue;

                if (!_binding.TryIndexOf(entry.Descriptor.Key, out int typeIndex))
                {
                    TypesNotInSession.Add(entry.Descriptor.DisplayName);
                    continue;
                }

                leaves.Clear();
                int elements = accessor.ReadInto(_entities, entity, leaves,
                    target => Translate(target, depth));
                if (Rejection != null) return values;

                values.Components.Add(new ModComponentValue
                {
                    TypeIndex = typeIndex,
                    ElementCount = elements,
                    Leaves = leaves.ToArray(),
                });

                if (values.Components.Count > ModStateSnapshot.MaxComponentsPerEntity)
                {
                    Rejection = "carries more than " + ModStateSnapshot.MaxComponentsPerEntity +
                                " replicated types on one entity";
                    return values;
                }
            }
            return values;
        }

        /// <summary>An entity reference as a place in the city or a position in this closure.</summary>
        private ModEntityRef Translate(Entity target, int depth)
        {
            if (target == Entity.Null || !_entities.Exists(target)) return ModEntityRef.Null;
            if (!ModEntityEligibility.IsLive(_entities, target))
            {
                Rejection = "references a temporary or deleted object";
                return ModEntityRef.Null;
            }

            if (_satelliteIndex.TryGetValue(target, out int existing))
                return ModEntityRef.Satellite(existing);

            if (_identity.TryDescribe(target, out ModEntityRef described)) return described;

            // Not a place: part of the closure only if it holds replicated state.
            if (!HoldsReplicatedState(target))
            {
                // Sending a null would hand the peer a different city; refuse the closure and count it.
                Rejection = "references an entity that is neither a place in the world nor mod state";
                return ModEntityRef.Null;
            }

            if (depth >= ModSyncFeature.MaxClosureDepth)
            {
                Rejection = "reaches further than " + ModSyncFeature.MaxClosureDepth +
                            " references from its carrier";
                return ModEntityRef.Null;
            }

            if (_satelliteEntities.Count >= ModStateSnapshot.MaxSatellites)
            {
                Rejection = "owns more than " + ModStateSnapshot.MaxSatellites + " bookkeeping entities";
                return ModEntityRef.Null;
            }

            int index = _satelliteEntities.Count;
            _satelliteIndex.Add(target, index);
            _satelliteEntities.Add(target);
            _satelliteDepth.Add(depth + 1);
            return ModEntityRef.Satellite(index);
        }

        private bool HoldsReplicatedState(Entity entity)
        {
            for (int i = 0; i < _catalog.Entries.Count; i++)
                if (_catalog.Entries[i].Accessor.Has(_entities, entity)) return true;
            return false;
        }
    }
}
