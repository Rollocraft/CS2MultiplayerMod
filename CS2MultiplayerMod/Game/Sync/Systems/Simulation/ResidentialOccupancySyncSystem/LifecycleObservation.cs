using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Turns household-member and health changes into property signals: births, deaths and splits
    /// do not move the household, so renter events miss them.
    /// </summary>
    public partial class ResidentialOccupancySyncSystem
    {
        internal void ProcessObservedHouseholdLifecycleChanges(
            NativeArray<Entity> changedHouseholds, NativeArray<Entity> changedHealthCitizens)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady) return;

            _lifecyclePropertyScratch.Clear();
            for (int i = 0; i < changedHouseholds.Length; i++)
                AddHouseholdLifecycleProperty(changedHouseholds[i]);
            for (int i = 0; i < changedHealthCitizens.Length; i++)
            {
                Entity citizen = changedHealthCitizens[i];
                if (citizen == Entity.Null || !EntityManager.Exists(citizen) ||
                    !EntityManager.HasComponent<HouseholdMember>(citizen) ||
                    EntityManager.HasComponent<Deleted>(citizen) ||
                    EntityManager.HasComponent<Temp>(citizen)) continue;
                AddHouseholdLifecycleProperty(
                    EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household);
            }

            if (service.Session.Role == SessionRole.Host)
            {
                foreach (Entity property in _lifecyclePropertyScratch)
                {
                    if (!TryGetHostPropertyIdentity(property, out PropertyIdentity identity)) continue;
                    if (_hostObserved.TryGetValue(property, out HostObserved observed)) observed.Stale = true;
                    Prioritize(property, identity);
                    _lifecyclePrioritySignals++;
                }
            }
            else
            {
                foreach (Entity property in _lifecyclePropertyScratch)
                {
                    if (!_cache.ContainsKey(property)) continue;
                    MarkDirty(property);
                    _lifecycleRepairSignals++;
                }
            }
            _lifecyclePropertyScratch.Clear();
        }

        private void AddHouseholdLifecycleProperty(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household) ||
                !EntityManager.HasComponent<Household>(household) ||
                !EntityManager.HasComponent<PropertyRenter>(household) ||
                EntityManager.HasComponent<Deleted>(household) ||
                EntityManager.HasComponent<Temp>(household) ||
                EntityManager.HasComponent<TouristHousehold>(household) ||
                EntityManager.HasComponent<CommuterHousehold>(household)) return;
            Entity property = EntityManager.GetComponentData<PropertyRenter>(household).m_Property;
            if (IsLiveProperty(property)) _lifecyclePropertyScratch.Add(property);
        }
    }
}
