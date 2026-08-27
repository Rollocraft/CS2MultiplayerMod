using Game.Buildings;
using Game.City;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Reports the host's zone demand and the population living behind it, and logs how far this
    /// machine has drifted from either.
    ///
    /// Demand is not written here, deliberately. It carries no randomness at all: each demand
    /// system recomputes it from the city's own buildings, households and taxes every sixteen
    /// simulation frames. Forcing a value would be overwritten within a fraction of a second, and
    /// holding it would mean stopping systems whose other readers are not enumerable. What makes
    /// two players see the same demand is the same buildings standing in both cities - which is
    /// what <see cref="Systems.GrowableSyncSystem"/> establishes. This channel is how you tell
    /// whether that worked: a demand gap that persists means the building sets have drifted apart.
    ///
    /// The occupancy counts are here for the same reason. Households, citizens and pets are
    /// separate entities driven by each machine's own random stream; they start identical because a
    /// joining client loads the host's city, and they drift from there. Nothing corrects them yet,
    /// so the gap is what is reported.
    /// </summary>
    public sealed class ZoneDemandChannel : IStateChannel
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
                All = new[] { ComponentType.ReadOnly<global::Game.Objects.OutsideConnection>() },
                None = new[]
                {
                    ComponentType.ReadOnly<global::Game.Objects.ElectricityOutsideConnection>(),
                    ComponentType.ReadOnly<global::Game.Objects.WaterPipeOutsideConnection>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            _population = em.CreateEntityQuery(ComponentType.ReadOnly<Population>());
            _households = em.CreateEntityQuery(ComponentType.ReadOnly<Household>());
            _rentingHouseholds = Live(em, ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<PropertyRenter>());
            _seekingHouseholds = Live(em, ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<global::Game.Agents.PropertySeeker>());
            _citizens = em.CreateEntityQuery(ComponentType.ReadOnly<Citizen>());
            _pets = em.CreateEntityQuery(ComponentType.ReadOnly<HouseholdPet>());
            // Every building the zoning simulation could have grown. Counting by chunk keeps this
            // to a handful of microseconds even in a city of hundreds of thousands.
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
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            var residential = em.World.GetExistingSystemManaged<ResidentialDemandSystem>();
            var commercial = em.World.GetExistingSystemManaged<CommercialDemandSystem>();
            var industrial = em.World.GetExistingSystemManaged<IndustrialDemandSystem>();
            if (residential == null || commercial == null || industrial == null) return false;

            Unity.Mathematics.int3 residentialDemand = residential.buildingDemand;
            writer.WriteInt(residentialDemand.x);
            writer.WriteInt(residentialDemand.y);
            writer.WriteInt(residentialDemand.z);
            writer.WriteInt(commercial.buildingDemand);
            writer.WriteInt(industrial.industrialBuildingDemand);
            writer.WriteInt(industrial.officeBuildingDemand);
            writer.WriteInt(industrial.storageBuildingDemand);

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
                        Mod.log.Warn("[MP] PopulationHealth: restored the host's disabled vanilla " +
                                     "HouseholdSpawnSystem after withdrawing household authority.");
                    }
                }
            }

            // A household-state experiment once disabled the vanilla client spawner and counted
            // only brand-new graphs, which made an existing homeless family moving into a new
            // building invisible. Report the real host population pipeline so future reports can
            // distinguish normal reuse of seeking households from an actually stalled spawner.
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
                Mod.log.Info("[MP] PopulationHealth/30s host: spawner=" +
                             (spawner == null ? "missing" : spawner.Enabled ? "enabled" : "DISABLED") +
                             ", households=" + households + " (renting=" +
                             _rentingHouseholds.CalculateEntityCount() + ", seeking=" +
                             _seekingHouseholds.CalculateEntityCount() + "), citizens=" + citizens +
                             ", population=" + arrivedPopulation + "/" + populationWithMoveIn +
                             " (arrived/withMoveIn), pets=" + pets +
                             ", residentialProperties=" + residentialProperties +
                             " (onMarket=" + _residentialOnMarket.CalculateEntityCount() + ")" +
                             ", freeUnits=" + freeUnits.x + "/" + freeUnits.y + "/" + freeUnits.z +
                             " of " + totalUnits.x + "/" + totalUnits.y + "/" + totalUnits.z +
                             ", householdDemand=" + residential.householdDemand +
                             ", buildingDemand=" + residentialDemand.x + "/" +
                             residentialDemand.y + "/" + residentialDemand.z +
                             ", unemployment=" + unemployment + "% (workable=" + workable +
                             ", workers=" + workers + ")" +
                             ", outsideConnections=" +
                             _citizenOutsideConnections.CalculateEntityCount() + ".");
            }
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int hostResidentialLow = reader.ReadInt();
            int hostResidentialMedium = reader.ReadInt();
            int hostResidentialHigh = reader.ReadInt();
            int hostCommercial = reader.ReadInt();
            int hostIndustrial = reader.ReadInt();
            int hostOffice = reader.ReadInt();
            int hostStorage = reader.ReadInt();
            int hostBuildings = reader.ReadInt();
            int hostResidentialProperties = reader.ReadInt();
            int hostCommercialProperties = reader.ReadInt();
            int hostIndustrialProperties = reader.ReadInt();
            int hostHouseholds = reader.ReadInt();
            int hostCitizens = reader.ReadInt();
            int hostPets = reader.ReadInt();

            if (++_snapshots % ReportEverySnapshots != 0) return;

            var residential = em.World.GetExistingSystemManaged<ResidentialDemandSystem>();
            var commercial = em.World.GetExistingSystemManaged<CommercialDemandSystem>();
            var industrial = em.World.GetExistingSystemManaged<IndustrialDemandSystem>();
            if (residential == null || commercial == null || industrial == null) return;

            Unity.Mathematics.int3 localResidential = residential.buildingDemand;
            int worstDemandGap = Max(
                Gap(localResidential.x, hostResidentialLow),
                Gap(localResidential.y, hostResidentialMedium),
                Gap(localResidential.z, hostResidentialHigh),
                Gap(commercial.buildingDemand, hostCommercial),
                Gap(industrial.industrialBuildingDemand, hostIndustrial),
                Gap(industrial.officeBuildingDemand, hostOffice),
                Gap(industrial.storageBuildingDemand, hostStorage));

            int localBuildings = _growables.CalculateEntityCount();
            bool buildingsDiverged = Diverged(localBuildings, hostBuildings);

            if (buildingsDiverged)
                Mod.log.Warn("[MP] ZoneDemand: building counts have drifted - this client has " +
                             localBuildings + ", the host has " + hostBuildings +
                             ". Zoned-building replication is not keeping up.");

            if (worstDemandGap >= DemandGapThreshold)
                Mod.log.Warn("[MP] ZoneDemand: demand differs from the host by up to " +
                             worstDemandGap + " (res " + localResidential.x + "/" +
                             localResidential.y + "/" + localResidential.z + " vs " +
                             hostResidentialLow + "/" + hostResidentialMedium + "/" +
                             hostResidentialHigh + ", com " + commercial.buildingDemand + " vs " +
                             hostCommercial + ", ind " + industrial.industrialBuildingDemand +
                             " vs " + hostIndustrial + ", off " + industrial.officeBuildingDemand +
                             " vs " + hostOffice + ", sto " + industrial.storageBuildingDemand +
                             " vs " + hostStorage + ").");

            int localCitizens = _citizens.CalculateEntityCount();
            int localPets = _pets.CalculateEntityCount();
            int localHouseholds = _households.CalculateEntityCount();
            if (Diverged(localCitizens, hostCitizens) || Diverged(localPets, hostPets) ||
                Diverged(localHouseholds, hostHouseholds))
                Mod.Verbose("[MP] ZoneDemand: occupancy differs - households " + localHouseholds +
                            "/" + hostHouseholds + ", people " + localCitizens + "/" + hostCitizens +
                            ", pets " + localPets + "/" + hostPets +
                            " (local/host). Residents are simulated per machine.");

            Mod.Verbose("[MP] ZoneDemand: buildings " + localBuildings + "/" + hostBuildings +
                        " (local/host), properties res " + _residentialProperties.CalculateEntityCount() +
                        "/" + hostResidentialProperties + ", com " +
                        _commercialProperties.CalculateEntityCount() + "/" + hostCommercialProperties +
                        ", ind " + _industrialProperties.CalculateEntityCount() + "/" +
                        hostIndustrialProperties + ".");
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
