using Game;
using CS2MultiplayerMod.Game.Sync.Players;
using CS2MultiplayerMod.Game.Sync.Systems;
using CS2MultiplayerMod.Game.Sync.Systems.Net;

namespace CS2MultiplayerMod.Game.Sync
{
    /// <summary>
    /// Central registration catalog for all multiplayer ECS synchronization and simulation systems.
    /// Orders and schedules systems across their respective <see cref="SystemUpdatePhase"/> buckets.
    /// </summary>
    public static class SyncSystemRegistration
    {
        /// <summary>
        /// Registers all multiplayer networking, presence, tool, and game state systems into the update pipeline.
        /// </summary>
        /// <param name="updateSystem">The active game update system loop.</param>
        public static void RegisterAll(UpdateSystem updateSystem)
        {
            RegisterCoreSystems(updateSystem);
            RegisterPlayerSystems(updateSystem);
            RegisterCityAndEconomySystems(updateSystem);
            RegisterModificationSystems(updateSystem);
            RegisterToolAndRealizeSystems(updateSystem);
            RegisterSimulationSystems(updateSystem);
        }

        private static void RegisterCoreSystems(UpdateSystem updateSystem)
        {
            // UIUpdate, not GameSimulation: the session pump and menus must run even when paused
            updateSystem.UpdateAt<MultiplayerSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<MultiplayerUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<WorldResyncSystem>(SystemUpdatePhase.UIUpdate);
        }

        private static void RegisterPlayerSystems(UpdateSystem updateSystem)
        {
            // Cursor position and compass bearings are captured and pumped during UIUpdate
            updateSystem.UpdateAt<PlayerCursorSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<PlayerCompassSystem>(SystemUpdatePhase.UIUpdate);

            // Overlays, ground markers, and pings rendered in Rendering phase
            updateSystem.UpdateAt<RemotePlayerMarkerSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<PlayerCursorRenderSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<MapPingSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<GhostPreviewSyncSystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAt<PlayerSpectatorSystem>(SystemUpdatePhase.Rendering);
        }

        private static void RegisterCityAndEconomySystems(UpdateSystem updateSystem)
        {
            // Main aggregated city state channel sync
            updateSystem.UpdateAt<CityStateSyncSystem>(SystemUpdatePhase.UIUpdate);

            // Specific UI-driven management dialogs & policies that work while paused
            updateSystem.UpdateAt<PolicySyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<DevTreeSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<SimulationSpeedSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<BuildingToggleSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<CityBudgetSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<CityLoanSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<ParkFeeSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<ServiceDistrictSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<TrafficControlSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<CustomNameSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<DistrictClaimSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<ChirperSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<WeatherControlSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<MilestoneSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<TransitColorSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<TransitLineDetailSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<UtilityGridSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<EmergencyShelterSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<ServiceFleetSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<TransitFareSyncSystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<UtilityTradeSyncSystem>(SystemUpdatePhase.UIUpdate);

            // Selected info UI customizations
            updateSystem.UpdateAfter<VisualCustomizationSyncSystem, global::Game.UI.InGame.SelectedInfoUISystem>(
                SystemUpdatePhase.UIUpdate);
        }

        private static void RegisterModificationSystems(UpdateSystem updateSystem)
        {
            // Placement and geometry capture at ModificationEnd where Created/Updated tags are alive
            updateSystem.UpdateAt<BuildSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<NetSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<DeleteSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<GrowableSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<NetReplaceSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<ZoneSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<TerrainSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<UpgradeSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<MoveSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<NetUpgradeSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<AreaSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<RouteSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<TilePurchaseSyncSystem>(SystemUpdatePhase.ModificationEnd);
            updateSystem.UpdateAt<DisasterSyncSystem>(SystemUpdatePhase.ModificationEnd);

            // Renaming and random name initialization
            updateSystem.UpdateAfter<NameSyncSystem, global::Game.Common.RandomLocalizationInitializeSystem>(
                SystemUpdatePhase.ModificationEnd);

            // Sub-element ownership mapping
            updateSystem.UpdateBefore<OwnerDefinitionSnapshotSystem, global::Game.Tools.FindOwnersSystem2>(
                SystemUpdatePhase.Modification2B);
        }

        private static void RegisterToolAndRealizeSystems(UpdateSystem updateSystem)
        {
            // Tool phase realization, world cleanup, and gates
            updateSystem.UpdateBefore<WorldRepairSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateBefore<TerrainReadbackBarrierSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<SyncRealizeSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<GhostCleanupSystem>(SystemUpdatePhase.ToolUpdate);

            updateSystem.UpdateBefore<ObjectToolApplyCaptureSystem, global::Game.Tools.ToolOutputSystem>(
                SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAfter<DefinitionGateSystem, global::Game.Tools.ToolOutputBarrier>(
                SystemUpdatePhase.ToolUpdate);
        }

        private static void RegisterSimulationSystems(UpdateSystem updateSystem)
        {
            // Residential occupancy & household economy simulation synchronization
            updateSystem.UpdateBefore<ResidentialOccupancyDepartureCaptureSystem, global::Game.Simulation.HouseholdMoveAwaySystem>(
                SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<ResidentialOccupancyRentSeedSystem, global::Game.Simulation.RentAdjustSystem>(
                SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<PropertyRentSyncSystem, global::Game.Simulation.RentAdjustSystem>(
                SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<ResidentialHouseholdEconomyCorrectionSystem, global::Game.Simulation.RentAdjustSystem>(
                SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<ResidentialOccupancySyncSystem, global::Game.Simulation.PropertyProcessingSystem>(
                SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<ResidentialOccupancyFinalizeSystem, global::Game.Simulation.PropertyProcessingSystem>(
                SystemUpdatePhase.GameSimulation);

            // Periodic simulation health and environmental checks
            updateSystem.UpdateAt<PollutionSyncSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<ChecksumSyncSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<MicroDesyncHealerSystem>(SystemUpdatePhase.GameSimulation);
        }
    }
}
