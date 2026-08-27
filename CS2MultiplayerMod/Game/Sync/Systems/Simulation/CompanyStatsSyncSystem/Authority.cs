using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
using Game.Common;
using Game.Companies;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CompanyStatsSyncSystem
    {
        /// <summary>
        /// The systems a client must not run once the host owns workplace tenancy. Between them
        /// these decide that a business exists at all and which building it takes, which is
        /// exactly the pair of decisions the host's absolute per-building roster replaces. A
        /// client left running them opens shops the host never had, and no amount of correcting
        /// figures afterwards removes a business that should not be there.
        ///
        /// Not on this list, deliberately:
        ///
        /// * <c>CompanyMoveAwaySystem</c> stays running. It executes a closure the host roster
        ///   asked for; it does not choose who closes.
        /// * <c>CompanyEconomyStatisticSystem</c>, <c>CommercialAISystem</c> and
        ///   <c>IndustrialAISystem</c> stay running. Besides proposing that a business give up,
        ///   they are the native producers of the figures, resource orders and demand signals the
        ///   rest of the local simulation reads. The every-update lifecycle boundary strips their
        ///   move-away and property-seeking proposals before the consumers run, which is the
        ///   narrow part, and leaves the rest intact.
        /// * <c>PropertyProcessingSystem</c> and <c>PropertyRenterSystem</c> stay running. They
        ///   maintain the native renter links and execute the move-ins this system queues.
        /// </summary>
        private static readonly Type[] ClientSuppressedSystems =
        {
            typeof(global::Game.Simulation.CommercialSpawnSystem),
            typeof(global::Game.Simulation.IndustrialSpawnSystem),
            typeof(global::Game.Simulation.CommercialFindPropertySystem),
            typeof(global::Game.Simulation.IndustrialFindPropertySystem),
        };

        private readonly Dictionary<Type, bool> _suppressedWasEnabled = new Dictionary<Type, bool>();
        private bool _authorityApplied;

        /// <summary>
        /// Hands workplace tenancy to the host. Idempotent, and re-checked every update so a
        /// system the game re-enables on a state change does not quietly start opening businesses
        /// this peer's own way again.
        /// </summary>
        private void ApplyLocalAuthority(MultiplayerSession session)
        {
            if (session.Role == SessionRole.Host)
            {
                // A host owns its own simulation. Restore in case this process was a client
                // earlier in its life.
                RestoreLocalAuthority();
                return;
            }

            for (int i = 0; i < ClientSuppressedSystems.Length; i++)
            {
                Type type = ClientSuppressedSystems[i];
                ComponentSystemBase system = World.GetExistingSystemManaged(type);
                if (system == null) continue;
                if (!_suppressedWasEnabled.ContainsKey(type))
                    _suppressedWasEnabled[type] = system.Enabled;
                if (!system.Enabled) continue;
                // If a system that was initially off becomes enabled during the session, remember
                // that latest native intent before holding it again so disconnect restores it on.
                _suppressedWasEnabled[type] = true;
                system.Enabled = false;
                Mod.Verbose("[MP] CompanyStats: " + type.Name +
                            " disabled on this client; the host decides which business is where.");
            }

            if (_authorityApplied) return;
            _authorityApplied = true;
            Mod.log.Info("[MP] CompanyStats: workplace tenancy handed to the host (" +
                         ClientSuppressedSystems.Length + " simulation system(s) held).");
            Diagnostics.FlightRecorder.Note("company tenancy authority -> host");
        }

        /// <summary>
        /// Gives the local simulation its economy back when the session ends. Without this a
        /// player who leaves a session keeps a city no business can ever open in again.
        /// </summary>
        private void RestoreLocalAuthority()
        {
            if (_suppressedWasEnabled.Count == 0)
            {
                _authorityApplied = false;
                return;
            }

            foreach (KeyValuePair<Type, bool> pair in _suppressedWasEnabled)
            {
                ComponentSystemBase system = World.GetExistingSystemManaged(pair.Key);
                if (system != null) system.Enabled = pair.Value;
            }
            _suppressedWasEnabled.Clear();
            _authorityApplied = false;
            Mod.log.Info("[MP] CompanyStats: workplace tenancy returned to the local simulation.");
            Diagnostics.FlightRecorder.Note("company tenancy authority -> local");
        }

        /// <summary>
        /// Called immediately before the native move-away executor. The systems that propose a
        /// closure also produce figures and demand this peer still needs, so rather than holding
        /// them their proposals are removed here, at the last point before anything acts on them.
        /// Closures this system asked for are whitelisted and pass straight through.
        ///
        /// Both cancellations are issued in bulk: a busy economy proposes plenty of these, and one
        /// structural change per business would be a sync point each.
        /// </summary>
        internal void CancelClientLifecycleDecisions()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady ||
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

            // PropertySeeker is enableable, so this query holds exactly the businesses whose flag
            // is set. Clearing the bits a chunk at a time replaces one main-thread call each.
            if (!_companySeekers.IsEmptyIgnoreFilter)
                EntityManager.SetComponentEnabled<global::Game.Agents.PropertySeeker>(
                    _companySeekers, false);

            PruneAuthorizedMoveAways();
        }

        private void AuthorizeMoveAway(Entity company) => _authorizedMoveAways.Add(company);

        /// <summary>
        /// The whitelist only ever holds businesses this system is closing, so it is naturally
        /// small; it is still swept once it grows, because a closure that never completes would
        /// otherwise keep a dead entity handle alive for the rest of the session.
        /// </summary>
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
