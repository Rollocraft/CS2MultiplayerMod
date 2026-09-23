using System;
using CS2MultiplayerMod.Game.Sync.Commands;
using Game.Economy;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CompanyStatsSyncSystem
    {
        private readonly int[] _wantedResourceAmounts = new int[EconomyUtils.ResourceCount];
        private readonly int[] _localResourceAmounts = new int[EconomyUtils.ResourceCount];
        private readonly bool[] _seenLocalResources = new bool[EconomyUtils.ResourceCount];

        /// <summary>Linear sparse compare; unchanged inventories never dirty their chunk.</summary>
        private void ApplyResources(Entity company, CompanyStatsEntry entry)
        {
            if (!EntityManager.HasBuffer<Resources>(company)) return;
            Array.Clear(_wantedResourceAmounts, 0, _wantedResourceAmounts.Length);
            Array.Clear(_localResourceAmounts, 0, _localResourceAmounts.Length);
            Array.Clear(_seenLocalResources, 0, _seenLocalResources.Length);
            CompanyStatsResource[] wanted = entry.Resources;
            if (wanted != null)
                for (int i = 0; i < wanted.Length; i++)
                {
                    int index = wanted[i].Index;
                    // Wire slots can outlive a game version's resource catalogue.
                    if (index >= 0 && index < _wantedResourceAmounts.Length)
                        _wantedResourceAmounts[index] = wanted[i].Amount;
                }

            DynamicBuffer<Resources> resources = EntityManager.GetBuffer<Resources>(company, true);
            for (int i = 0; i < resources.Length; i++)
            {
                Resources local = resources[i];
                int index = EconomyUtils.GetResourceIndex(local.m_Resource);
                // Match GetResources' first-entry semantics, including malformed duplicates.
                if (index < 0 || index >= _localResourceAmounts.Length ||
                    _seenLocalResources[index]) continue;
                _seenLocalResources[index] = true;
                _localResourceAmounts[index] = local.m_Amount;
            }

            bool changed = false;
            for (int i = 0; i < _wantedResourceAmounts.Length; i++)
            {
                if (_localResourceAmounts[i] == _wantedResourceAmounts[i]) continue;
                if (!changed) resources = EntityManager.GetBuffer<Resources>(company);
                // Keep native resource mutation semantics (zero entries and insertion order).
                EconomyUtils.SetResources(EconomyUtils.GetResource(i), resources,
                    _wantedResourceAmounts[i]);
                changed = true;
            }
            if (changed) _correctedResources++;
        }
    }
}
