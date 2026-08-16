using System.Collections.Generic;
using System.Text;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Removes mover instances left in saves by older builds that treated simulation spawns
    /// as player placements. Those instances have a live mover archetype but no owning
    /// citizen, household, or vehicle controller, so later simulation/tool contact is unsafe.
    /// The sweep runs once after each world load and only deletes on positive missing-link
    /// evidence. Work is spread across frames to keep large cities responsive.
    /// </summary>
    public partial class WorldRepairSystem : GameSystemBase
    {
        private const int MaxCandidatesPerFrame = 2048;
        private const int MaxRepairsPerFrame = 128;

        private PrefabSystem _prefabSystem;
        private EntityQuery _moverCandidates;
        private NativeArray<Entity> _sweepCandidates;
        private int _sweepIndex;
        private int _sweepRemoved;
        private bool _sawLoading = true;
        private bool _sweeping;
        private readonly Dictionary<string, int> _sweepByPrefab =
            new Dictionary<string, int>();

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(WorldRepairSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // Top-level mover instances. Simulation-owned vehicles normally carry Owner;
            // pedestrians and pets are retained or removed by their explicit linkage below.
            _moverCandidates = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<global::Game.Objects.Transform>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<global::Game.Vehicles.Vehicle>(),
                    ComponentType.ReadOnly<global::Game.Creatures.Creature>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Owner>(),
                },
            });
        }

        protected override void OnDestroy()
        {
            CancelSweep();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (!MultiplayerService.ModEnabled)
            {
                CancelSweep();
                _sawLoading = true;
                return;
            }

            GameManager manager = GameManager.instance;
            if (manager == null) return;

            if (manager.isGameLoading)
            {
                CancelSweep();
                _sawLoading = true;
                return;
            }

            if (_sawLoading)
            {
                _sawLoading = false;
                if (!manager.gameMode.IsGame()) return;
                BeginSweep();
            }

            if (_sweeping) SweepStep();
        }

        private void BeginSweep()
        {
            CancelSweep();
            _sweepCandidates = _moverCandidates.ToEntityArray(Allocator.Persistent);
            _sweepIndex = 0;
            _sweepRemoved = 0;
            _sweepByPrefab.Clear();
            _sweeping = true;
            if (_sweepCandidates.Length == 0) FinishSweep();
        }

        private void SweepStep()
        {
            int scanned = 0;
            int removed = 0;
            while (_sweepIndex < _sweepCandidates.Length &&
                   scanned < MaxCandidatesPerFrame && removed < MaxRepairsPerFrame)
            {
                Entity entity = _sweepCandidates[_sweepIndex++];
                scanned++;
                if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity) ||
                    !IsStrandedMover(entity)) continue;
                RemoveStrandedMover(entity);
                removed++;
            }

            _sweepRemoved += removed;
            if (_sweepIndex >= _sweepCandidates.Length) FinishSweep();
        }

        /// <summary>
        /// True only when an instance's required simulation link is null or dead. Wildlife,
        /// parked vehicles, and every live linked resident/pet/controller are preserved.
        /// </summary>
        private bool IsStrandedMover(Entity entity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(entity)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<MovingObjectData>(prefab)) return false;

            if (EntityManager.HasComponent<global::Game.Creatures.Resident>(entity))
            {
                Entity citizen = EntityManager
                    .GetComponentData<global::Game.Creatures.Resident>(entity).m_Citizen;
                return citizen == Entity.Null || !EntityManager.Exists(citizen);
            }

            if (EntityManager.HasComponent<global::Game.Creatures.Pet>(entity))
            {
                Entity householdPet = EntityManager
                    .GetComponentData<global::Game.Creatures.Pet>(entity).m_HouseholdPet;
                return householdPet == Entity.Null || !EntityManager.Exists(householdPet);
            }

            if (EntityManager.HasComponent<global::Game.Vehicles.Vehicle>(entity))
            {
                if (EntityManager.HasComponent<global::Game.Vehicles.ParkedCar>(entity)) return false;
                if (EntityManager.HasComponent<global::Game.Vehicles.Controller>(entity))
                {
                    Entity controller = EntityManager
                        .GetComponentData<global::Game.Vehicles.Controller>(entity).m_Controller;
                    return controller == Entity.Null || !EntityManager.Exists(controller);
                }

                // Owner-less, unparked, and uncontrolled means no simulation system can
                // legitimately steer this instance. Normal transient traffic can respawn.
                return true;
            }

            // A creature without a resident/pet link can be legitimate wildlife.
            return false;
        }

        private void RemoveStrandedMover(Entity entity)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            string name = _prefabSystem.GetPrefabName(prefab);
            EntityManager.AddComponent<Deleted>(entity);

            if (string.IsNullOrEmpty(name)) name = "?";
            int count;
            _sweepByPrefab.TryGetValue(name, out count);
            _sweepByPrefab[name] = count + 1;
        }

        private void FinishSweep()
        {
            int scanned = _sweepCandidates.IsCreated ? _sweepCandidates.Length : 0;
            if (_sweepCandidates.IsCreated) _sweepCandidates.Dispose();
            _sweepCandidates = default(NativeArray<Entity>);
            _sweepIndex = 0;
            _sweeping = false;

            Diagnostics.FlightRecorder.Note("world repair scanned=" + scanned +
                                              " removed=" + _sweepRemoved);
            if (_sweepRemoved == 0) return;

            var detail = new StringBuilder();
            int written = 0;
            foreach (KeyValuePair<string, int> pair in _sweepByPrefab)
            {
                if (written++ > 0) detail.Append(", ");
                detail.Append(pair.Key).Append(" x").Append(pair.Value);
                if (written >= 10 && _sweepByPrefab.Count > 10)
                {
                    detail.Append(", ...");
                    break;
                }
            }
            Mod.log.Info("[MP] World repair: removed " + _sweepRemoved +
                         " stranded mover instance(s) left by an earlier session [" +
                         detail + "].");
        }

        private void CancelSweep()
        {
            if (_sweepCandidates.IsCreated) _sweepCandidates.Dispose();
            _sweepCandidates = default(NativeArray<Entity>);
            _sweepIndex = 0;
            _sweeping = false;
        }
    }
}
