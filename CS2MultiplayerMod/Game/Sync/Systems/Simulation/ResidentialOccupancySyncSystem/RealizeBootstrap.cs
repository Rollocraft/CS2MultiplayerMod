using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Matching a client's pre-join households to the host roster by what both peers agree on
    // (prefab, seeded randomness, name indices), so it adopts its own families instead of cloning.
    public partial class ResidentialOccupancySyncSystem
    {
        private Entity FindBootstrapHousehold(OccupancyHousehold wanted)
        {
            EnsureBootstrapIdentityIndex();
            if (!_bootstrapHouseholdIndex.TryGetValue(HouseholdBootstrapKey(wanted),
                out List<Entity> globalCandidates)) return Entity.Null;
            Entity match = Entity.Null;
            for (int i = 0; i < globalCandidates.Count; i++)
            {
                Entity candidate = globalCandidates[i];
                if (_claimedHouseholds.Contains(candidate) || candidate == Entity.Null ||
                    !EntityManager.Exists(candidate) || EntityManager.HasComponent<Deleted>(candidate))
                    continue;
                if (TryGetBoundHouseholdId(candidate, out ulong alreadyBound)) continue;
                if (!HouseholdBootstrapMatches(candidate, wanted)) continue;
                if (match != Entity.Null && match != candidate) return Entity.Null;
                match = candidate;
            }
            return match;
        }

        private void EnsureBootstrapIdentityIndex()
        {
            if (_bootstrapIdentityIndexBuilt) return;
            _bootstrapIdentityIndexBuilt = true;
            NativeArray<Entity> households = default(NativeArray<Entity>);
            NativeArray<Entity> citizens = default(NativeArray<Entity>);
            try
            {
                households = _bootstrapHouseholds.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < households.Length; i++)
                    AddBootstrapCandidate(_bootstrapHouseholdIndex,
                        HouseholdBootstrapKey(households[i]), households[i]);
                citizens = _bootstrapCitizens.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < citizens.Length; i++)
                    AddBootstrapCandidate(_bootstrapCitizenIndex,
                        CitizenBootstrapKey(citizens[i]), citizens[i]);
            }
            finally
            {
                if (households.IsCreated) households.Dispose();
                if (citizens.IsCreated) citizens.Dispose();
            }
        }

        private static void AddBootstrapCandidate(Dictionary<int, List<Entity>> index, int key,
            Entity entity)
        {
            if (!index.TryGetValue(key, out List<Entity> candidates))
            {
                candidates = new List<Entity>();
                index[key] = candidates;
            }
            candidates.Add(entity);
        }

        // Built over the whole city in one pass: one shared scratch buffer.
        private int HouseholdBootstrapKey(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household) ||
                !EntityManager.HasComponent<PrefabRef>(household) ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household)) return 0;
            string prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(household).m_Prefab);
            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            _bootstrapKeyScratch.Clear();
            for (int i = 0; i < members.Length; i++)
                _bootstrapKeyScratch.Add(CitizenBootstrapKey(members[i].m_Citizen));
            _bootstrapKeyScratch.Sort();
            return CombineBootstrapKey(prefabName, _bootstrapKeyScratch);
        }

        private int HouseholdBootstrapKey(OccupancyHousehold household)
        {
            _bootstrapKeyScratch.Clear();
            for (int i = 0; i < household.Citizens.Length; i++)
                _bootstrapKeyScratch.Add(CitizenBootstrapKey(household.Citizens[i]));
            _bootstrapKeyScratch.Sort();
            return CombineBootstrapKey(household.PrefabName, _bootstrapKeyScratch);
        }

        private int CitizenBootstrapKey(Entity citizen)
        {
            if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                !EntityManager.HasComponent<Citizen>(citizen) ||
                !EntityManager.HasComponent<PrefabRef>(citizen)) return 0;
            Citizen data = EntityManager.GetComponentData<Citizen>(citizen);
            string prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(citizen).m_Prefab);
            return CombineCitizenBootstrapKey(prefabName, data.m_PseudoRandom, data.m_BirthDay,
                (short)data.m_State & HostOwnedCitizenFlags);
        }

        private static int CitizenBootstrapKey(OccupancyCitizen citizen) =>
            CombineCitizenBootstrapKey(citizen.PrefabName, citizen.PseudoRandom, citizen.BirthDay,
                citizen.State & HostOwnedCitizenFlags);

        private static int CombineCitizenBootstrapKey(string prefabName, ushort pseudoRandom,
            short birthDay, int state)
        {
            unchecked
            {
                int hash = prefabName != null ? prefabName.GetHashCode() : 0;
                hash = hash * 397 ^ pseudoRandom;
                hash = hash * 397 ^ birthDay;
                return hash * 397 ^ state;
            }
        }

        private static int CombineBootstrapKey(string prefabName, List<int> citizenKeys)
        {
            unchecked
            {
                int hash = prefabName != null ? prefabName.GetHashCode() : 0;
                hash = hash * 397 ^ citizenKeys.Count;
                for (int i = 0; i < citizenKeys.Count; i++)
                    hash = hash * 397 ^ citizenKeys[i];
                return hash;
            }
        }

        private bool HouseholdBootstrapMatches(Entity household, OccupancyHousehold wanted)
        {
            if (!EntityManager.HasComponent<PrefabRef>(household) ||
                !EntityManager.HasBuffer<HouseholdCitizen>(household)) return false;
            string prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(household).m_Prefab);
            if (!string.Equals(prefabName, wanted.PrefabName, StringComparison.Ordinal)) return false;
            if (!BootstrapNameIndicesMatch(household, wanted.NameIndices)) return false;

            DynamicBuffer<HouseholdCitizen> members =
                EntityManager.GetBuffer<HouseholdCitizen>(household, true);
            if (members.Length != wanted.Citizens.Length) return false;
            _claimedCitizens.Clear();
            for (int i = 0; i < wanted.Citizens.Length; i++)
            {
                Entity match = Entity.Null;
                for (int j = 0; j < members.Length; j++)
                {
                    Entity candidate = members[j].m_Citizen;
                    if (_claimedCitizens.Contains(candidate) ||
                        !CitizenBootstrapMatches(candidate, wanted.Citizens[i])) continue;
                    match = candidate;
                    break;
                }
                if (match == Entity.Null)
                {
                    _claimedCitizens.Clear();
                    return false;
                }
                _claimedCitizens.Add(match);
            }
            _claimedCitizens.Clear();
            return true;
        }

        private bool CitizenBootstrapMatches(Entity citizen, OccupancyCitizen wanted)
        {
            if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                EntityManager.HasComponent<Deleted>(citizen) ||
                !EntityManager.HasComponent<Citizen>(citizen) ||
                !EntityManager.HasComponent<PrefabRef>(citizen)) return false;
            Citizen data = EntityManager.GetComponentData<Citizen>(citizen);
            if (data.m_PseudoRandom != wanted.PseudoRandom || data.m_BirthDay != wanted.BirthDay ||
                ((short)data.m_State & HostOwnedCitizenFlags) !=
                (wanted.State & HostOwnedCitizenFlags)) return false;
            string prefabName = _prefabIndex.NameOf(
                EntityManager.GetComponentData<PrefabRef>(citizen).m_Prefab);
            return string.Equals(prefabName, wanted.PrefabName, StringComparison.Ordinal) &&
                   BootstrapNameIndicesMatch(citizen, wanted.NameIndices);
        }

        private bool BootstrapNameIndicesMatch(Entity entity, int[] wanted)
        {
            if (wanted == null || wanted.Length == 0)
                return !EntityManager.HasBuffer<RandomLocalizationIndex>(entity) ||
                       EntityManager.GetBuffer<RandomLocalizationIndex>(entity, true).Length == 0;
            if (!EntityManager.HasBuffer<RandomLocalizationIndex>(entity)) return false;
            DynamicBuffer<RandomLocalizationIndex> indices =
                EntityManager.GetBuffer<RandomLocalizationIndex>(entity, true);
            if (indices.Length < wanted.Length) return false;
            for (int i = 0; i < wanted.Length; i++)
                if (indices[i].m_Index != wanted[i]) return false;
            return true;
        }
    }
}
