using Game.Buildings;
using Game.City;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// The host's complete zone-demand state. After the first valid snapshot a client holds its three
    /// demand writers for the session and installs the host's headline values and serialized arrays;
    /// CityInfoUISystem keeps easing the bars toward them. A bad later snapshot keeps the last good
    /// values; only <see cref="ResetPending"/> releases the hold. Also reports household, citizen and
    /// pet count drift, which nothing corrects yet.
    /// </summary>
    public sealed class ZoneDemandChannel : IStateChannel, IPumpedStateChannel
    {
        public const byte Id = 18;
        public byte ChannelId => Id;

        /// <summary>Demand runs 0..100, so this is a drift of a tenth of the bar.</summary>
        private const int DemandGapThreshold = 10;

        /// <summary>Below this a count difference is ordinary simulation noise, not a desync.</summary>
        private const int CountGapPermille = 50;
        private const int CountGapFloor = 25;

        /// <summary>Snapshots between reports, so a persistent gap is logged about once a minute.</summary>
        private const int ReportEverySnapshots = 60;

        private EntityQuery _residentialProperties;
        private EntityQuery _commercialProperties;
        private EntityQuery _industrialProperties;
        private EntityQuery _residentialOnMarket;
        private EntityQuery _citizenOutsideConnections;
        private EntityQuery _population;
        private EntityQuery _households;
        private EntityQuery _rentingHouseholds;
        private EntityQuery _seekingHouseholds;
        private EntityQuery _citizens;
        private EntityQuery _pets;
        private EntityQuery _growables;
        private bool _ready;
        private int _snapshots;
        private int _hostSnapshots;
        private bool _hostSpawnerChecked;
        private bool _hasAuthoritativeSnapshot;
        private bool _captureWarned;
        private bool _applyWarned;
        private World _world;

        private readonly LocalAuthorityHold _authority = new LocalAuthorityHold(
            "ZoneDemand", "zone demand", "all residential, commercial, industrial and office demand",
            "zone-demand authority", typeof(ResidentialDemandSystem),
            typeof(CommercialDemandSystem), typeof(IndustrialDemandSystem));

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _residentialProperties = Live(em, ComponentType.ReadOnly<ResidentialProperty>());
            _commercialProperties = Live(em, ComponentType.ReadOnly<CommercialProperty>());
            _industrialProperties = Live(em, ComponentType.ReadOnly<IndustrialProperty>());
            _residentialOnMarket = Live(em, ComponentType.ReadOnly<ResidentialProperty>(),
                ComponentType.ReadOnly<PropertyOnMarket>());
            _citizenOutsideConnections = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Objects.OutsideConnection>(),
                None = SyncQuery.ReadOnly<global::Game.Objects.ElectricityOutsideConnection,
                    global::Game.Objects.WaterPipeOutsideConnection, Temp, Deleted>(),
            });
            _population = em.CreateEntityQuery(ComponentType.ReadOnly<Population>());
            _households = em.CreateEntityQuery(ComponentType.ReadOnly<Household>());
            _rentingHouseholds = Live(em, ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<PropertyRenter>());
            _seekingHouseholds = Live(em, ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<global::Game.Agents.PropertySeeker>());
            _citizens = em.CreateEntityQuery(ComponentType.ReadOnly<Citizen>());
            _pets = em.CreateEntityQuery(ComponentType.ReadOnly<HouseholdPet>());
            // Counted by chunk: microseconds even in a huge city.
            _growables = Live(em, ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<PrefabRef>());
            _ready = true;
        }

        private static EntityQuery Live(EntityManager em, params ComponentType[] all)
        {
            var required = new ComponentType[all.Length];
            for (int i = 0; i < all.Length; i++) required[i] = all[i];
            return em.CreateEntityQuery(new EntityQueryDesc
            {
                All = required,
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            // Demand is simulation output; without simulation sync each city keeps its own.
            MultiplayerService captureService = Mod.Service;
            if (captureService != null && !captureService.SimulationSyncEnabled) return false;

            Ensure(em);
            var residential = em.World.GetExistingSystemManaged<ResidentialDemandSystem>();
            var commercial = em.World.GetExistingSystemManaged<CommercialDemandSystem>();
            var industrial = em.World.GetExistingSystemManaged<IndustrialDemandSystem>();
            if (residential == null || commercial == null || industrial == null) return false;

            try
            {
                if (!DemandStateAccess.TryCapture(residential, commercial, industrial, out DemandStateSnapshot demand))
                    return false;
                demand.Write(writer);
            }
            catch (System.Exception ex)
            {
                if (!_captureWarned)
                {
                    _captureWarned = true;
                    SyncLog.Warn(LogTopic.City,
                        "ZoneDemand: complete demand capture failed (logged once): " +
                        ex.Message);
                }
                return false;
            }

            Unity.Mathematics.int3 residentialDemand = residential.buildingDemand;

            int growables = _growables.CalculateEntityCount();
            int residentialProperties = _residentialProperties.CalculateEntityCount();
            int commercialProperties = _commercialProperties.CalculateEntityCount();
            int industrialProperties = _industrialProperties.CalculateEntityCount();
            int households = _households.CalculateEntityCount();
            int citizens = _citizens.CalculateEntityCount();
            int pets = _pets.CalculateEntityCount();
            writer.WriteInt(growables);
            writer.WriteInt(residentialProperties);
            writer.WriteInt(commercialProperties);
            writer.WriteInt(industrialProperties);
            writer.WriteInt(households);
            writer.WriteInt(citizens);
            writer.WriteInt(pets);

            if (Mod.Service != null && Mod.Service.Session.Role == SessionRole.Host &&
                !_hostSpawnerChecked)
            {
                var hostSpawner = em.World.GetExistingSystemManaged<HouseholdSpawnSystem>();
                if (hostSpawner != null)
                {
                    _hostSpawnerChecked = true;
                    if (!hostSpawner.Enabled)
                    {
                        hostSpawner.Enabled = true;
                        SyncLog.Warn(LogTopic.City,
                            "PopulationHealth: restored the host's disabled vanilla " +
                            "HouseholdSpawnSystem after withdrawing household authority.");
                    }
                }
            }

            // Report the real population pipeline, so reuse of seeking households is not mistaken for a stall.
            if (Mod.Service != null && Mod.Service.Session.Role == SessionRole.Host &&
                ++_hostSnapshots % 30 == 0)
            {
                var spawner = em.World.GetExistingSystemManaged<HouseholdSpawnSystem>();
                int arrivedPopulation = -1;
                int populationWithMoveIn = -1;
                if (_population.CalculateEntityCount() == 1)
                {
                    Population population = em.GetComponentData<Population>(
                        _population.GetSingletonEntity());
                    arrivedPopulation = population.m_Population;
                    populationWithMoveIn = population.m_PopulationWithMoveIn;
                }
                var propertyCounts =
                    em.World.GetExistingSystemManaged<CountResidentialPropertySystem>();
                Unity.Mathematics.int3 freeUnits = new Unity.Mathematics.int3(-1);
                Unity.Mathematics.int3 totalUnits = new Unity.Mathematics.int3(-1);
                if (propertyCounts != null)
                {
                    freeUnits = propertyCounts.FreeProperties;
                    totalUnits = propertyCounts.TotalProperties;
                }
                var householdData = em.World.GetExistingSystemManaged<CountHouseholdDataSystem>();
                float unemployment = householdData == null ? -1f : householdData.UnemploymentRate;
                int workable = householdData == null ? -1 : householdData.WorkableCitizenCount;
                int workers = householdData == null ? -1 : householdData.CityWorkerCount;
                SyncLog.Detail(LogTopic.City, "PopulationHealth/30s host: spawner=" +
                    (spawner == null ? "missing" : spawner.Enabled ? "enabled" : "DISABLED") +
                    ", households=" + households + " (renting=" +
                    _rentingHouseholds.CalculateEntityCount() + ", seeking=" +
                    _seekingHouseholds.CalculateEntityCount() + "), citizens=" + citizens +
                    ", population=" + arrivedPopulation + "/" + populationWithMoveIn +
                    " (arrived/withMoveIn), pets=" + pets + ", residentialProperties=" +
                    residentialProperties + " (onMarket=" +
                    _residentialOnMarket.CalculateEntityCount() + ")" + ", freeUnits=" + freeUnits.x +
                    "/" + freeUnits.y + "/" + freeUnits.z + " of " + totalUnits.x + "/" +
                    totalUnits.y + "/" + totalUnits.z + ", householdDemand=" +
                    residential.householdDemand + ", buildingDemand=" + residentialDemand.x + "/" +
                    residentialDemand.y + "/" + residentialDemand.z + ", unemployment=" +
                    unemployment + "% (workable=" + workable + ", workers=" + workers + ")" +
                    ", outsideConnections=" + _citizenOutsideConnections.CalculateEntityCount() +
                    ".");
            }
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            DemandStateSnapshot hostDemand = DemandStateSnapshot.Read(reader);
            int hostBuildings = reader.ReadInt();
            int hostResidentialProperties = reader.ReadInt();
            int hostCommercialProperties = reader.ReadInt();
            int hostIndustrialProperties = reader.ReadInt();
            int hostHouseholds = reader.ReadInt();
            int hostCitizens = reader.ReadInt();
            int hostPets = reader.ReadInt();
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in zone-demand state.");

            var residential = em.World.GetExistingSystemManaged<ResidentialDemandSystem>();
            var commercial = em.World.GetExistingSystemManaged<CommercialDemandSystem>();
            var industrial = em.World.GetExistingSystemManaged<IndustrialDemandSystem>();
            if (residential == null || commercial == null || industrial == null)
                throw new System.InvalidOperationException("Vanilla zone-demand systems are unavailable.");

            Unity.Mathematics.int3 localResidential = residential.buildingDemand;
            int localCommercial = commercial.buildingDemand;
            int localIndustrial = industrial.industrialBuildingDemand;
            int localOffice = industrial.officeBuildingDemand;
            int localStorage = industrial.storageBuildingDemand;

            _world = em.World;
            MultiplayerService service = Mod.Service;
            if (service != null && !service.SimulationSyncEnabled)
            {
                // A page from before the switch: the local writers own demand now.
                _authority.Restore(em.World);
                return;
            }
            if (service != null && service.GameplaySyncReady)
                _authority.Apply(em.World, service.Session);
            try
            {
                DemandStateAccess.Apply(residential, commercial, industrial, hostDemand);
            }
            catch (System.Exception ex)
            {
                // A demand field is missing on this build. Keep the good snapshot and the hold: flipping the bars
                // back to local for a second is the fight this channel removes.
                if (!_hasAuthoritativeSnapshot)
                {
                    _authority.Restore(em.World);
                    throw;
                }
                if (!_applyWarned)
                {
                    _applyWarned = true;
                    SyncLog.Warn(LogTopic.City, "ZoneDemand: could not install host demand " +
                        "(logged once); holding the last good values: " + ex.Message);
                }
            }
            if (!_hasAuthoritativeSnapshot)
            {
                _hasAuthoritativeSnapshot = true;
                SyncLog.Detail(LogTopic.City, "ZoneDemand: first host demand installed (res " +
                    hostDemand.ResidentialLastBuilding.x + "/" +
                    hostDemand.ResidentialLastBuilding.y + "/" +
                    hostDemand.ResidentialLastBuilding.z + ", com " +
                    hostDemand.CommercialLastBuilding + ", ind " + hostDemand.IndustrialLast[1] +
                    ", off " + hostDemand.IndustrialLast[5] +
                    "); the local demand writers are held from here.");
            }

            if (++_snapshots % ReportEverySnapshots != 0) return;

            Unity.Mathematics.int3 hostResidential = hostDemand.ResidentialLastBuilding;
            int hostCommercial = hostDemand.CommercialLastBuilding;
            int hostIndustrial = hostDemand.IndustrialLast[1];
            int hostStorage = hostDemand.IndustrialLast[3];
            int hostOffice = hostDemand.IndustrialLast[5];
            int worstDemandGap = Max(
                Gap(localResidential.x, hostResidential.x),
                Gap(localResidential.y, hostResidential.y),
                Gap(localResidential.z, hostResidential.z),
                Gap(localCommercial, hostCommercial),
                Gap(localIndustrial, hostIndustrial),
                Gap(localOffice, hostOffice),
                Gap(localStorage, hostStorage));

            int localBuildings = _growables.CalculateEntityCount();
            bool buildingsDiverged = Diverged(localBuildings, hostBuildings);

            if (buildingsDiverged)
                SyncLog.Warn(LogTopic.City,
                    "ZoneDemand: building counts have drifted - this client has " + localBuildings +
                    ", the host has " + hostBuildings +
                    ". Zoned-building replication is not keeping up.");

            if (worstDemandGap >= DemandGapThreshold)
                SyncLog.Warn(LogTopic.City, "ZoneDemand: demand differs from the host by up to " +
                    worstDemandGap + " (res " + localResidential.x + "/" + localResidential.y + "/" +
                    localResidential.z + " vs " + hostResidential.x + "/" + hostResidential.y +
                    "/" + hostResidential.z + ", com " + localCommercial + " vs " +
                    hostCommercial + ", ind " + localIndustrial + " vs " +
                    hostIndustrial + ", off " + localOffice + " vs " +
                    hostOffice + ", sto " + localStorage + " vs " + hostStorage +
                    ").");

            int localCitizens = _citizens.CalculateEntityCount();
            int localPets = _pets.CalculateEntityCount();
            int localHouseholds = _households.CalculateEntityCount();
            if (Diverged(localCitizens, hostCitizens) || Diverged(localPets, hostPets) ||
                Diverged(localHouseholds, hostHouseholds))
                SyncLog.Detail(LogTopic.City, "ZoneDemand: occupancy differs - households " +
                    localHouseholds + "/" + hostHouseholds + ", people " + localCitizens + "/" +
                    hostCitizens + ", pets " + localPets + "/" + hostPets +
                    " (local/host). Residents are simulated per machine.");

            SyncLog.Detail(LogTopic.City, "ZoneDemand: buildings " + localBuildings + "/" +
                hostBuildings + " (local/host), properties res " +
                _residentialProperties.CalculateEntityCount() + "/" + hostResidentialProperties +
                ", com " + _commercialProperties.CalculateEntityCount() + "/" +
                hostCommercialProperties + ", ind " + _industrialProperties.CalculateEntityCount() +
                "/" + hostIndustrialProperties + ".");
        }

        public void Pump(EntityManager em)
        {
            _world = em.World;
            MultiplayerService service = Mod.Service;
            if (_hasAuthoritativeSnapshot && service != null && service.SimulationSyncReady &&
                service.Session.Role == SessionRole.Client)
                _authority.Apply(em.World, service.Session);
            else
                _authority.Restore(em.World);
        }

        public void ResetPending()
        {
            _hasAuthoritativeSnapshot = false;
            if (_world != null) _authority.Restore(_world);
        }

        private static int Gap(int local, int host) =>
            local > host ? local - host : host - local;

        private static int Max(params int[] values)
        {
            int best = 0;
            for (int i = 0; i < values.Length; i++) if (values[i] > best) best = values[i];
            return best;
        }

        /// <summary>Proportional, so a big city is not reported for a gap a small one would be.</summary>
        private static bool Diverged(int local, int host)
        {
            int gap = Gap(local, host);
            if (gap <= CountGapFloor) return false;
            int allowed = (int)((long)host * CountGapPermille / 1000L);
            return gap > allowed;
        }
    }
}
