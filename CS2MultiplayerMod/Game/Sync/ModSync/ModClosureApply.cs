using System.Collections.Generic;
using CS2MultiplayerMod.Core.Sync.ModSync;
using Game.Common;
using Game.Net;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.ModSync
{
    /// <summary>What became of an arriving closure.</summary>
    internal enum ModApplyOutcome
    {
        Applied,

        /// <summary>The carrier is not on this machine yet. Worth waiting for.</summary>
        CarrierMissing,

        /// <summary>Something the payload points at is not here yet. Also worth waiting for.</summary>
        ReferenceMissing,

        /// <summary>A type in the payload has nothing to bind to here. Waiting will not help.</summary>
        TypeMissing,
    }

    /// <summary>
    /// Writes an arriving closure. Everything is resolved before anything is written: a road a frame
    /// from being built makes a place temporarily unfindable, and a half-applied closure has no record
    /// of the other half.
    /// </summary>
    internal sealed class ModClosureApply
    {
        private readonly EntityManager _entities;
        private readonly ModCarrierIdentity _identity;
        private readonly ModTypeBinding _binding;
        private readonly ModComponentCatalog _catalog;

        private readonly Dictionary<string, Entity> _resolved =
            new Dictionary<string, Entity>(System.StringComparer.Ordinal);
        private readonly List<Entity> _newSatellites = new List<Entity>();

        /// <summary>What went wrong, in words fit for a log line.</summary>
        public string Detail { get; private set; }

        public ModClosureApply(EntityManager entities, ModCarrierIdentity identity,
            ModTypeBinding binding, ModComponentCatalog catalog)
        {
            _entities = entities;
            _identity = identity;
            _binding = binding;
            _catalog = catalog;
        }

        public ModApplyOutcome Apply(ModStateSnapshot snapshot)
        {
            Detail = null;
            _resolved.Clear();
            _newSatellites.Clear();

            if (!_identity.TryResolve(snapshot.Carrier, out Entity carrier))
            {
                Detail = snapshot.Carrier.Key();
                return ModApplyOutcome.CarrierMissing;
            }

            ModApplyOutcome check = Resolve(snapshot);
            if (check != ModApplyOutcome.Applied) return check;

            // Everything resolved; from here only a bug can fail.
            List<Entity> stale = CollectExistingSatellites(carrier);

            for (int i = 0; i < snapshot.Satellites.Count; i++)
                _newSatellites.Add(_entities.CreateEntity());

            Write(carrier, snapshot.CarrierValues);
            for (int i = 0; i < snapshot.Satellites.Count; i++)
                Write(_newSatellites[i], snapshot.Satellites[i]);

            StripAbsentTypes(carrier, snapshot.CarrierValues);
            DestroySatellites(stale);
            if (snapshot.HasLaneSpeedReset) ApplyLaneSpeedReset(carrier, snapshot.LaneSpeedReset);

            // Same signal an ordinary edit leaves, for the mod's systems and rendering.
            if (!_entities.HasComponent<Updated>(carrier)) _entities.AddComponent<Updated>(carrier);
            if (!_entities.HasComponent<BatchesUpdated>(carrier))
                _entities.AddComponent<BatchesUpdated>(carrier);

            return ModApplyOutcome.Applied;
        }

        private void ApplyLaneSpeedReset(Entity carrier, float speed)
        {
            if (!_entities.HasComponent<Edge>(carrier) || !_entities.HasBuffer<SubLane>(carrier))
                return;
            // A reset can only accompany removal of this exact mod's durable component.
            for (int i = 0; i < _catalog.Entries.Count; i++)
                if (_catalog.Entries[i].Type.FullName ==
                        "RoadSpeedAdjuster.Components.CustomSpeed" &&
                    _catalog.Entries[i].Accessor.Has(_entities, carrier)) return;

            DynamicBuffer<SubLane> lanes = _entities.GetBuffer<SubLane>(carrier, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!_entities.Exists(lane)) continue;
                if (_entities.HasComponent<CarLane>(lane))
                {
                    CarLane car = _entities.GetComponentData<CarLane>(lane);
                    car.m_DefaultSpeedLimit = speed;
                    car.m_SpeedLimit = speed;
                    _entities.SetComponentData(lane, car);
                }
                else if (_entities.HasComponent<TrackLane>(lane))
                {
                    TrackLane track = _entities.GetComponentData<TrackLane>(lane);
                    track.m_SpeedLimit = speed;
                    _entities.SetComponentData(lane, track);
                }
            }
        }

        /// <summary>Resolves every place the payload names and checks every type is writable. Touches nothing.</summary>
        private ModApplyOutcome Resolve(ModStateSnapshot snapshot)
        {
            ModApplyOutcome outcome = ResolveEntity(snapshot.CarrierValues);
            if (outcome != ModApplyOutcome.Applied) return outcome;

            for (int i = 0; i < snapshot.Satellites.Count; i++)
            {
                outcome = ResolveEntity(snapshot.Satellites[i]);
                if (outcome != ModApplyOutcome.Applied) return outcome;
            }
            return ModApplyOutcome.Applied;
        }

        private ModApplyOutcome ResolveEntity(ModEntityValues values)
        {
            for (int i = 0; i < values.Components.Count; i++)
            {
                ModComponentValue component = values.Components[i];
                ModTypeAccessor accessor = _binding.AccessorFor(component.TypeIndex);
                if (accessor == null)
                {
                    Detail = _binding.ByIndex(component.TypeIndex).DisplayName;
                    return ModApplyOutcome.TypeMissing;
                }

                ModTypeDescriptor descriptor = _binding.ByIndex(component.TypeIndex);
                for (int leaf = 0; leaf < component.Leaves.Length; leaf++)
                {
                    if (descriptor.Leaves[leaf % descriptor.LeafCount] != ModValueKind.EntityRef)
                        continue;

                    ModEntityRef reference = component.Leaves[leaf].Reference;
                    if (reference.Kind == ModRefKind.Null || reference.Kind == ModRefKind.Satellite)
                        continue;

                    string key = reference.Key();
                    if (_resolved.ContainsKey(key)) continue;

                    if (!_identity.TryResolve(reference, out Entity target))
                    {
                        Detail = key;
                        return ModApplyOutcome.ReferenceMissing;
                    }
                    _resolved.Add(key, target);
                }
            }
            return ModApplyOutcome.Applied;
        }

        private void Write(Entity entity, ModEntityValues values)
        {
            for (int i = 0; i < values.Components.Count; i++)
            {
                ModComponentValue component = values.Components[i];
                ModTypeAccessor accessor = _binding.AccessorFor(component.TypeIndex);
                if (accessor == null) continue;
                accessor.Apply(_entities, entity, component.Leaves, component.ElementCount, Translate);
            }
        }

        private Entity Translate(ModEntityRef reference)
        {
            switch (reference.Kind)
            {
                case ModRefKind.Null:
                    return Entity.Null;
                case ModRefKind.Satellite:
                    return reference.SatelliteIndex < _newSatellites.Count
                        ? _newSatellites[reference.SatelliteIndex]
                        : Entity.Null;
                default:
                    return _resolved.TryGetValue(reference.Key(), out Entity found) ? found : Entity.Null;
            }
        }

        /// <summary>
        /// Removes replicated types the closure does not mention: a removal travels only as absence from a
        /// snapshot.
        /// </summary>
        private void StripAbsentTypes(Entity carrier, ModEntityValues values)
        {
            for (int i = 0; i < _catalog.Entries.Count; i++)
            {
                ModCatalogEntry entry = _catalog.Entries[i];
                if (!entry.Accessor.Has(_entities, carrier)) continue;

                if (!_binding.TryIndexOf(entry.Descriptor.Key, out int typeIndex)) continue;

                bool mentioned = false;
                for (int c = 0; c < values.Components.Count; c++)
                {
                    if (values.Components[c].TypeIndex != typeIndex) continue;
                    mentioned = true;
                    break;
                }
                if (!mentioned) entry.Accessor.Remove(_entities, carrier);
            }
        }

        /// <summary>Satellites the carrier owned before; replaced wholesale, as mods rebuild them per edit.</summary>
        private List<Entity> CollectExistingSatellites(Entity carrier)
        {
            var capture = new ModClosureCapture(_entities, _identity, _binding, _catalog);
            if (!_identity.TryDescribe(carrier, out ModEntityRef carrierRef)) return new List<Entity>();

            capture.Capture(carrier, carrierRef);

            // A rejected capture still lists the satellites it reached.
            return new List<Entity>(capture.SatelliteEntities);
        }

        private void DestroySatellites(List<Entity> satellites)
        {
            for (int i = 0; i < satellites.Count; i++)
            {
                Entity satellite = satellites[i];
                if (satellite == Entity.Null || !_entities.Exists(satellite)) continue;

                // Only an entity with no place of its own can be a satellite.
                if (_identity.TryDescribe(satellite, out ModEntityRef described)) continue;

                _entities.DestroyEntity(satellite);
            }
        }
    }
}
