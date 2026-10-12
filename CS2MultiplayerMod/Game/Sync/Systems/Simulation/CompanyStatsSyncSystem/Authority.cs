using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Common;
using Game.Companies;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CompanyStatsSyncSystem
    {
        /// <summary>
        /// The host owns workplace tenancy and company accounting; channel 22 carries the accounting
        /// fields. Deliberately left running: CompanyProfitabilitySystem and CompanyDividendSystem (they
        /// touch entities outside the roster), CompanyMoveAwaySystem (it executes closures the roster
        /// asked for), Commercial/IndustrialAISystem (their proposals are stripped at the lifecycle
        /// boundary, the rest is needed), and PropertyProcessing/PropertyRenterSystem (renter links).
        /// </summary>
        private readonly LocalAuthorityHold _authority = new LocalAuthorityHold(
            "CompanyStats", "workplace state", "business tenancy and accounting",
            "company state authority",
            typeof(global::Game.Simulation.CommercialSpawnSystem),
            typeof(global::Game.Simulation.IndustrialSpawnSystem),
            typeof(global::Game.Simulation.CommercialFindPropertySystem),
            typeof(global::Game.Simulation.IndustrialFindPropertySystem),
            typeof(global::Game.Simulation.CompanyEconomyStatisticSystem));

        /// <summary>Idempotent; re-checked every update because the game can re-enable held systems.</summary>
        private void ApplyLocalAuthority(MultiplayerSession session)
        {
            // Without simulation sync the host never sends these decisions.
            if (!session.SimulationSyncEnabled)
            {
                RestoreLocalAuthority();
                return;
            }
            _authority.Apply(World, session);
        }

        /// <summary>Gives the local economy back when the session ends.</summary>
        private void RestoreLocalAuthority() => _authority.Restore(World);

        /// <summary>
        /// Runs just before the native move-away executor and strips local closure and seek proposals,
        /// in bulk. Closures this system asked for are whitelisted.
        /// </summary>
        internal void CancelClientLifecycleDecisions()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.SimulationSyncReady ||
                service.Session.Role != SessionRole.Client) return;

            if (!_departingCompanies.IsEmptyIgnoreFilter)
            {
                if (_authorizedMoveAways.Count == 0)
                {
                    _cancelledDecisions += _departingCompanies.CalculateEntityCount();
                    EntityManager.RemoveComponent<global::Game.Agents.MovingAway>(
                        _departingCompanies);
                }
                else
                {
                    NativeArray<Entity> departing =
                        _departingCompanies.ToEntityArray(Allocator.Temp);
                    NativeList<Entity> cancelled =
                        new NativeList<Entity>(departing.Length, Allocator.Temp);
                    try
                    {
                        for (int i = 0; i < departing.Length; i++)
                        {
                            Entity company = departing[i];
                            if (_authorizedMoveAways.Contains(company)) continue;
                            cancelled.Add(company);
                        }
                        if (cancelled.Length > 0)
                        {
                            _cancelledDecisions += cancelled.Length;
                            EntityManager.RemoveComponent<global::Game.Agents.MovingAway>(
                                cancelled.AsArray());
                        }
                    }
                    finally
                    {
                        cancelled.Dispose();
                        departing.Dispose();
                    }
                }
            }

            // PropertySeeker is enableable: clear the bits a chunk at a time.
            if (!_companySeekers.IsEmptyIgnoreFilter)
                EntityManager.SetComponentEnabled<global::Game.Agents.PropertySeeker>(
                    _companySeekers, false);

            PruneAuthorizedMoveAways();
        }

        private void AuthorizeMoveAway(Entity company) => _authorizedMoveAways.Add(company);

        /// <summary>Swept once it grows, so a closure that never completes cannot pin a dead handle.</summary>
        private void PruneAuthorizedMoveAways()
        {
            if (_authorizedMoveAways.Count <= 1024) return;
            _authorizedScratch.Clear();
            foreach (Entity company in _authorizedMoveAways)
                if (!EntityManager.Exists(company) ||
                    EntityManager.HasComponent<Deleted>(company) ||
                    !EntityManager.HasComponent<CompanyData>(company))
                    _authorizedScratch.Add(company);
            for (int i = 0; i < _authorizedScratch.Count; i++)
                _authorizedMoveAways.Remove(_authorizedScratch[i]);
            _authorizedScratch.Clear();
        }
    }
}
