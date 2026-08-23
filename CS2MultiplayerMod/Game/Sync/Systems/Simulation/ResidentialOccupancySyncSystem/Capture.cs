using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        private static readonly int[] EmptyNameIndices = new int[0];

        /// <summary>Called once per city-state snapshot on the host.</summary>
        internal bool Capture(NetworkWriter writer)
        {
            if (writer == null) return false;
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
                service.Session.Role != Core.Session.SessionRole.Host) return false;

            if (_hostSweepEntities == null && !BeginHostSweep()) return WriteEmptySweep(writer);
            if (_captureCursor < 0 || _captureCursor >= _hostSweepEntities.Length)
            {
                _hostSweepEntities = null;
                _captureCursor = 0;
                AdvanceHostSweep();
                if (!BeginHostSweep()) return WriteEmptySweep(writer);
            }

            var snapshot = new ResidentialOccupancySnapshot
            {
                SweepId = _captureSweepId,
                PageIndex = _capturePageIndex,
            };
            var identities = new HashSet<PropertyRentIdentity>();
            int bytes = AddPriorityProperties(snapshot, identities);

            int index = _captureCursor;
            while (index < _hostSweepEntities.Length &&
                   snapshot.Properties.Count < ResidentialOccupancySnapshot.MaxProperties &&
                   bytes < PageByteBudget)
            {
                OccupancyProperty property;
                if (TryCaptureProperty(_hostSweepEntities[index], out property))
                {
                    if (identities.Add(property.Identity))
                    {
                        snapshot.Properties.Add(property);
                        bytes += EstimateBytes(property);
                    }
                }
                else _captureSkips++;
                index++;
            }

            bool cappedSweep = _capturePageIndex + 1 >= ResidentialOccupancySnapshot.MaxPagesPerSweep;
            snapshot.EndOfSweep = index >= _hostSweepEntities.Length || cappedSweep;
            if (snapshot.EndOfSweep)
            {
                _hostSweepEntities = null;
                _captureCursor = 0;
                AdvanceHostSweep();
            }
            else
            {
                _captureCursor = index;
                _capturePageIndex++;
            }

            if (snapshot.Properties.Count == 0 && !snapshot.EndOfSweep) return false;
            int before = writer.Length;
            snapshot.Write(writer);
            _sentBytes += writer.Length - before;
            _sentPages++;
            _sentProperties += snapshot.Properties.Count;
            return true;
        }

        private bool BeginHostSweep()
        {
            NativeArray<Entity> properties = _properties.ToEntityArray(Allocator.Temp);
            try
            {
                if (properties.Length == 0) return false;
                _hostSweepEntities = new Entity[properties.Length];
                for (int i = 0; i < properties.Length; i++) _hostSweepEntities[i] = properties[i];
                return true;
            }
            finally { properties.Dispose(); }
        }

        /// <summary>
        /// A city with no residential property still has to close its sweep, otherwise a client
        /// that bulldozed its last house would keep the previous roster cached forever.
        /// </summary>
        private bool WriteEmptySweep(NetworkWriter writer)
        {
            var empty = new ResidentialOccupancySnapshot
            {
                SweepId = _captureSweepId,
                PageIndex = 0,
                EndOfSweep = true,
            };
            int before = writer.Length;
            empty.Write(writer);
            _sentBytes += writer.Length - before;
            _sentPages++;
            AdvanceHostSweep();
            return true;
        }

        private int AddPriorityProperties(ResidentialOccupancySnapshot snapshot,
            HashSet<PropertyRentIdentity> identities)
        {
            int bytes = 0;
            int added = 0;
            while (added < PriorityPropertiesPerPage && _priorityOrder.Count > 0 &&
                   snapshot.Properties.Count < ResidentialOccupancySnapshot.MaxProperties &&
                   bytes < PageByteBudget)
            {
                PropertyRentIdentity identity;
                if (!_priorityOrder.TryDequeue(out identity)) break;
                OccupancyProperty property;
                if (!_priority.TryGetValue(identity, out property)) continue;
                _priority.Remove(identity);
                if (!identities.Add(identity)) continue;
                snapshot.Properties.Add(property);
                bytes += EstimateBytes(property);
                added++;
            }
            return bytes;
        }

        /// <summary>
        /// Rough encoded size, used only to decide when a page is full. The codec enforces the real
        /// cap; over-estimating here just makes pages slightly smaller than the budget.
        /// </summary>
        private static int EstimateBytes(OccupancyProperty property)
        {
            int bytes = 24;
            for (int h = 0; h < property.Households.Length; h++)
            {
                bytes += 24;
                bytes += property.Households[h].Citizens.Length * 12;
                bytes += property.Households[h].Pets.Length * 4;
            }
            return bytes;
        }

        /// <summary>
        /// One residential partition per update. This is the change detector that gets a newly
        /// occupied building onto the wire in seconds rather than waiting for the rolling baseline
        /// sweep to come round to it.
        /// </summary>
        private void ScanHostChanges(int bucket)
        {
            _properties.SetSharedComponentFilter(new UpdateFrame((uint)bucket));
            NativeArray<Entity> properties = default(NativeArray<Entity>);
            try
            {
                properties = _properties.ToEntityArray(Allocator.Temp);
                bool initialized = _hostBucketInitialized[bucket];
                for (int i = 0; i < properties.Length; i++)
                {
                    Entity property = properties[i];
                    OccupancyProperty captured;
                    if (!TryCaptureProperty(property, out captured)) continue;
                    int hash = Hash(captured);
                    HostObserved observed;
                    if (!_hostObserved.TryGetValue(property, out observed))
                    {
                        _hostObserved[property] = new HostObserved { Hash = hash, Bucket = bucket };
                        _hostObservedBuckets[bucket].Add(property);
                        if (initialized) Prioritize(captured);
                    }
                    else if (observed.Hash != hash)
                    {
                        observed.Hash = hash;
                        if (initialized) Prioritize(captured);
                    }
                }
                _hostBucketInitialized[bucket] = true;
            }
            finally
            {
                if (properties.IsCreated) properties.Dispose();
                _properties.ResetFilter();
            }
            PruneHostObservedBucket(bucket);
        }

        private void Prioritize(OccupancyProperty property)
        {
            PropertyRentIdentity identity = property.Identity;
            if (_priority.ContainsKey(identity))
            {
                _priority[identity] = property;
                return;
            }
            while (_priority.Count >= MaxPriorityProperties && _priorityOrder.Count > 0)
            {
                PropertyRentIdentity oldest;
                if (!_priorityOrder.TryDequeue(out oldest)) break;
                if (_priority.Remove(oldest)) _priorityDrops++;
            }
            if (_priority.Count >= MaxPriorityProperties)
            {
                _priorityDrops++;
                return;
            }
            _priority[identity] = property;
            _priorityOrder.Enqueue(identity);
            _priorityChanges++;
        }

        private void PruneHostObservedBucket(int bucket)
        {
            List<Entity> entities = _hostObservedBuckets[bucket];
            int write = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                HostObserved observed;
                if (!_hostObserved.TryGetValue(entity, out observed)) continue;
                if (!IsLiveProperty(entity) || observed.Bucket != bucket ||
                    EntityManager.GetSharedComponent<UpdateFrame>(entity).m_Index != (uint)bucket)
                {
                    _hostObserved.Remove(entity);
                    continue;
                }
                entities[write++] = entity;
            }
            if (write < entities.Count) entities.RemoveRange(write, entities.Count - write);
        }

        private bool TryCaptureProperty(Entity property, out OccupancyProperty result)
        {
            result = default(OccupancyProperty);
            if (!IsLiveProperty(property)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<BuildingPropertyData>(prefab)) return false;
            string prefabName = PrefabIndex.SafeName(_prefabSystem, prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;

            var households = new List<OccupancyHousehold>();
            DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(property, true);
            for (int i = 0; i < renters.Length; i++)
            {
                Entity renter = renters[i].m_Renter;
                // Companies rent the commercial half of a mixed building. They are a different
                // simulation with a different authority story; only households are ours.
                if (!IsCapturableHousehold(renter, property)) continue;
                if (households.Count >= ResidentialOccupancySnapshot.MaxHouseholdsPerProperty)
                    return false;
                OccupancyHousehold household;
                if (!TryCaptureHousehold(renter, out household)) return false;
                households.Add(household);
            }

            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(property);
            byte constructionSpeed = 0;
            if (EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(property))
            {
                // Zero means finished, so a site whose speed has not been drawn yet still reads as
                // "building". One is as slow as the game ever goes.
                byte speed = EntityManager
                    .GetComponentData<global::Game.Objects.UnderConstruction>(property).m_Speed;
                constructionSpeed = speed == 0 ? (byte)1 : speed;
            }
            result = new OccupancyProperty
            {
                PrefabName = prefabName,
                AnchorX = transform.m_Position.x,
                AnchorY = transform.m_Position.y,
                AnchorZ = transform.m_Position.z,
                ConstructionSpeed = constructionSpeed,
                Households = households.ToArray(),
            };
            // City-state capture is shared: never let a broken local asset name or transform reach
            // Write, where a throw would suppress every other channel in the same snapshot.
            return ResidentialOccupancySnapshot.IsValidProperty(result);
        }

        private bool IsCapturableHousehold(Entity renter, Entity property) =>
            renter != Entity.Null && EntityManager.Exists(renter) &&
            EntityManager.HasComponent<Household>(renter) &&
            EntityManager.HasComponent<PrefabRef>(renter) &&
            EntityManager.HasBuffer<HouseholdCitizen>(renter) &&
            EntityManager.HasBuffer<Resources>(renter) &&
            EntityManager.HasComponent<PropertyRenter>(renter) &&
            !EntityManager.HasComponent<Deleted>(renter) &&
            !EntityManager.HasComponent<Temp>(renter) &&
            !EntityManager.HasComponent<TouristHousehold>(renter) &&
            !EntityManager.HasComponent<CommuterHousehold>(renter) &&
            EntityManager.GetComponentData<PropertyRenter>(renter).m_Property == property;

        private bool TryCaptureHousehold(Entity entity, out OccupancyHousehold result)
        {
            result = default(OccupancyHousehold);
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            string prefabName = PrefabIndex.SafeName(_prefabSystem, prefab);
            if (string.IsNullOrEmpty(prefabName)) return false;

            Household data = EntityManager.GetComponentData<Household>(entity);
            PropertyRenter rented = EntityManager.GetComponentData<PropertyRenter>(entity);
            DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(entity, true);

            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(entity, true);
            if (members.Length > ResidentialOccupancySnapshot.MaxCitizensPerHousehold) return false;
            var citizens = new List<OccupancyCitizen>(members.Length);
            for (int i = 0; i < members.Length; i++)
            {
                OccupancyCitizen citizen;
                if (!TryCaptureCitizen(members[i].m_Citizen, out citizen)) continue;
                citizens.Add(citizen);
            }

            var pets = new List<string>();
            if (EntityManager.HasBuffer<HouseholdAnimal>(entity))
            {
                DynamicBuffer<HouseholdAnimal> animals =
                    EntityManager.GetBuffer<HouseholdAnimal>(entity, true);
                if (animals.Length > ResidentialOccupancySnapshot.MaxPetsPerHousehold) return false;
                for (int i = 0; i < animals.Length; i++)
                {
                    Entity pet = animals[i].m_HouseholdPet;
                    if (!EntityManager.Exists(pet) || EntityManager.HasComponent<Deleted>(pet) ||
                        !EntityManager.HasComponent<PrefabRef>(pet)) continue;
                    string petName = PrefabIndex.SafeName(_prefabSystem,
                        EntityManager.GetComponentData<PrefabRef>(pet).m_Prefab);
                    if (string.IsNullOrEmpty(petName)) continue;
                    pets.Add(petName);
                }
            }

            result = new OccupancyHousehold
            {
                PrefabName = prefabName,
                Flags = (byte)data.m_Flags,
                Rent = Clamp(rented.m_Rent, 0, ResidentialOccupancySnapshot.MaxRent),
                Savings = Clamp(data.m_Resources, -ResidentialOccupancySnapshot.MaxMoney,
                    ResidentialOccupancySnapshot.MaxMoney),
                Money = Clamp(EconomyUtils.GetResources(Resource.Money, resources),
                    -ResidentialOccupancySnapshot.MaxMoney,
                    ResidentialOccupancySnapshot.MaxMoney),
                NameIndices = CaptureNameIndices(entity),
                Citizens = citizens.ToArray(),
                Pets = pets.ToArray(),
            };
            return true;
        }

        /// <summary>
        /// The random name slots behind a family surname or a person's first name. Drawn per
        /// machine, so they are the difference between "the same family" and "a family with the
        /// same numbers".
        /// </summary>
        private int[] CaptureNameIndices(Entity entity)
        {
            if (!EntityManager.HasBuffer<RandomLocalizationIndex>(entity)) return EmptyNameIndices;
            DynamicBuffer<RandomLocalizationIndex> indices =
                EntityManager.GetBuffer<RandomLocalizationIndex>(entity, true);
            int count = indices.Length;
            if (count > ResidentialOccupancySnapshot.MaxNameIndices)
                count = ResidentialOccupancySnapshot.MaxNameIndices;
            if (count == 0) return EmptyNameIndices;
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = indices[i].m_Index < -1 ? -1 : indices[i].m_Index;
            return result;
        }

        private bool TryCaptureCitizen(Entity entity, out OccupancyCitizen result)
        {
            result = default(OccupancyCitizen);
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                !EntityManager.HasComponent<Citizen>(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return false;
            string name = PrefabIndex.SafeName(_prefabSystem,
                EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
            if (string.IsNullOrEmpty(name)) return false;

            Citizen data = EntityManager.GetComponentData<Citizen>(entity);
            bool employed = false;
            byte level = 0;
            if (EntityManager.HasComponent<Worker>(entity))
            {
                Worker worker = EntityManager.GetComponentData<Worker>(entity);
                employed = true;
                level = worker.m_Level > ResidentialOccupancySnapshot.MaxWorkerLevel
                    ? (byte)ResidentialOccupancySnapshot.MaxWorkerLevel : worker.m_Level;
            }
            result = new OccupancyCitizen
            {
                PrefabName = name,
                State = (short)data.m_State,
                PseudoRandom = data.m_PseudoRandom,
                BirthDay = data.m_BirthDay,
                Health = data.m_Health,
                WellBeing = data.m_WellBeing,
                Employment = OccupancyCitizen.PackEmployment(employed, level),
                NameIndices = CaptureNameIndices(entity),
            };
            return true;
        }

        /// <summary>
        /// Content hash of everything a client would have to change. Deliberately covers the fields
        /// that move — occupancy, money, rent, age/education flags, employment — and not the ones
        /// that drift continuously (health, wellbeing), which the baseline sweep carries anyway.
        /// A hash over those would mark half the city changed every scan.
        /// </summary>
        private static int Hash(OccupancyProperty property)
        {
            unchecked
            {
                int hash = (int)2166136261;
                // Construction is in the hash because its end is the change a client most needs to
                // hear about promptly; the rate itself only ever changes once, at creation.
                hash = (hash ^ property.ConstructionSpeed) * 16777619;
                hash = (hash ^ property.Households.Length) * 16777619;
                for (int h = 0; h < property.Households.Length; h++)
                {
                    OccupancyHousehold household = property.Households[h];
                    hash = (hash ^ household.PrefabName.GetHashCode()) * 16777619;
                    hash = (hash ^ household.Flags) * 16777619;
                    hash = (hash ^ household.Rent) * 16777619;
                    hash = (hash ^ household.Savings) * 16777619;
                    hash = (hash ^ household.Money) * 16777619;
                    hash = HashIndices(hash, household.NameIndices);
                    hash = (hash ^ household.Citizens.Length) * 16777619;
                    for (int c = 0; c < household.Citizens.Length; c++)
                    {
                        OccupancyCitizen citizen = household.Citizens[c];
                        hash = (hash ^ citizen.PrefabName.GetHashCode()) * 16777619;
                        hash = (hash ^ citizen.State) * 16777619;
                        hash = (hash ^ citizen.PseudoRandom) * 16777619;
                        hash = (hash ^ citizen.BirthDay) * 16777619;
                        hash = (hash ^ citizen.Employment) * 16777619;
                        hash = HashIndices(hash, citizen.NameIndices);
                    }
                    hash = (hash ^ household.Pets.Length) * 16777619;
                }
                return hash;
            }
        }

        private static int HashIndices(int hash, int[] indices)
        {
            unchecked
            {
                hash = (hash ^ indices.Length) * 16777619;
                for (int i = 0; i < indices.Length; i++) hash = (hash ^ indices[i]) * 16777619;
                return hash;
            }
        }

        private static int Clamp(int value, int min, int max) =>
            value < min ? min : value > max ? max : value;
    }
}
