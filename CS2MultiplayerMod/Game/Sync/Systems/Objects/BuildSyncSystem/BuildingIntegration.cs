using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Sync;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Completing a remote building's integration after its transaction drained: observed at
    // ModificationEnd, repaired from the next ToolUpdate so the native passes consume the tag.
    public partial class BuildSyncSystem
    {
        private const long BuildingIntegrationWindowMs = 15000;
        private const long BuildingIntegrationRetryMs = 500;
        private const int MaxBuildingIntegrations = 512;
        private const int MaxBuildingIntegrationRefreshesPerFrame = 32;
        private const int MaxBuildingIntegrationGraphEntities = 2048;
        private const float BuildingIntegrationMatchDistanceSq = 1f;
        private const float BuildingIntegrationRotationDot = 0.999f;

        private sealed class PendingBuildingIntegration
        {
            public Entity Building;
            public Entity Prefab;
            public float3 Position;
            public quaternion Rotation;
            public bool RootResolved;
            public bool RoadConnectionExpected;
            public Entity ExpectedRoad;
            public bool ExpectsElectricityConsumer;
            public bool ExpectsWaterConsumer;
            public string Source;
            public long Deadline;
            public long NextAttempt;
            public bool RefreshQueued;
            public bool AwaitingObservation;
            public int Attempts;
        }

        private struct BuildingIntegrationState
        {
            public bool Root;
            public bool OwnedGraph;
            public bool Road;
            public bool Utilities;
            public string Detail;

            public bool Ready => Root && OwnedGraph && Road && Utilities;
        }

        private struct OneSidedRoadConnection
        {
            public Entity Building;
            public Entity Road;
        }

        private readonly ActiveRetryClock _buildingIntegrationClock = new ActiveRetryClock();
        private readonly List<PendingBuildingIntegration> _buildingIntegrations =
            new List<PendingBuildingIntegration>();
        private readonly HashSet<Entity> _buildingIntegrationVisited = new HashSet<Entity>();
        private bool _buildingIntegrationGraphBoundReached;
        private readonly List<Entity> _buildingIntegrationRefreshTargets = new List<Entity>();
        private readonly List<OneSidedRoadConnection> _oneSidedRoadConnections =
            new List<OneSidedRoadConnection>();
        private Entity _lastIntegrationOwnerPrefab;
        private float3 _lastIntegrationOwnerPosition;
        private quaternion _lastIntegrationOwnerRotation;
        private Entity _lastIntegrationOwner;

        /// <summary>
        /// Registers a remote building. The first refresh is unconditional: connection warnings may have
        /// sampled the graph while it settled.
        /// </summary>
        public void TrackRemoteBuilding(Entity building, Entity prefab, float3 position,
            quaternion rotation, bool roadConnectionExpected, string source)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<BuildingData>(prefab)) return;
            Entity expectedRoad = LiveRoadReference(building);
            roadConnectionExpected = PrefabRequiresRoad(prefab) &&
                                     (roadConnectionExpected ||
                                      expectedRoad != Entity.Null);
            GetExpectedUtilityConsumers(prefab, out bool expectsElectricityConsumer,
                out bool expectsWaterConsumer);

            for (int i = 0; i < _buildingIntegrations.Count; i++)
            {
                PendingBuildingIntegration existing = _buildingIntegrations[i];
                bool sameEntity = building != Entity.Null && existing.Building == building;
                bool sameAnchor = existing.Prefab == prefab &&
                    math.distancesq(existing.Position, position) <=
                    BuildingIntegrationMatchDistanceSq &&
                    IntegrationRotationMatches(existing.Rotation, rotation);
                if (!sameEntity && !sameAnchor) continue;

                bool replacedTarget = (sameEntity && !sameAnchor) ||
                    (sameAnchor && building != Entity.Null &&
                     existing.Building != Entity.Null && existing.Building != building);
                existing.Prefab = prefab;
                existing.Position = position;
                existing.Rotation = rotation;
                existing.ExpectsElectricityConsumer = expectsElectricityConsumer;
                existing.ExpectsWaterConsumer = expectsWaterConsumer;
                if (building != Entity.Null || replacedTarget)
                {
                    existing.Building = building;
                    existing.RootResolved = building != Entity.Null;
                }
                if (replacedTarget)
                {
                    existing.RoadConnectionExpected = roadConnectionExpected;
                    existing.ExpectedRoad = expectedRoad;
                    existing.Attempts = 0;
                }
                else
                {
                    existing.RoadConnectionExpected |= roadConnectionExpected;
                    if (expectedRoad != Entity.Null) existing.ExpectedRoad = expectedRoad;
                }
                existing.Source = source ?? "remote";
                existing.Deadline = _buildingIntegrationClock.NowMs +
                                    BuildingIntegrationWindowMs;
                existing.NextAttempt = 0;
                existing.RefreshQueued = true;
                existing.AwaitingObservation = false;
                return;
            }

            if (_buildingIntegrations.Count >= MaxBuildingIntegrations)
            {
                PendingBuildingIntegration dropped = _buildingIntegrations[0];
                _buildingIntegrations.RemoveAt(0);
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: building integration tracker overflowed; dropped the oldest '" +
                    IntegrationPrefabName(dropped.Prefab) + "' check.");
            }

            if (_buildingIntegrations.Count == 0)
            {
                _buildingIntegrationClock.Reset();
                _buildingIntegrationClock.Observe(
                    Mod.Service != null ? Mod.Service.NowMs : 0,
                    RealizeGate.WorldBuildingHeld);
            }
            long activeNow = _buildingIntegrationClock.NowMs;
            _buildingIntegrations.Add(new PendingBuildingIntegration
            {
                Building = building,
                Prefab = prefab,
                Position = position,
                Rotation = rotation,
                RootResolved = building != Entity.Null,
                RoadConnectionExpected = roadConnectionExpected,
                ExpectedRoad = expectedRoad,
                ExpectsElectricityConsumer = expectsElectricityConsumer,
                ExpectsWaterConsumer = expectsWaterConsumer,
                Source = source ?? "remote",
                Deadline = activeNow + BuildingIntegrationWindowMs,
                RefreshQueued = true,
            });
        }

        private void TrackCommittedRemoteBuildings(ObjectToolOperationCommand command,
            ResolvedObjectDefinition[] resolved)
        {
            if (command == null || command.IsAssetStamp || command.Definitions == null ||
                resolved == null || command.RootIndex < 0 ||
                command.RootIndex >= command.Definitions.Length ||
                command.RootIndex >= resolved.Length) return;

            int i = command.RootIndex;
            ObjectToolDefinitionIntent definition = command.Definitions[i];
            if (definition == null || definition.Kind != ObjectToolDefinitionKind.Object ||
                definition.PrefabIsNull ||
                definition.Original.Kind != PortableEntityKind.None ||
                definition.Owner.Kind != PortableEntityKind.None ||
                definition.HasOwnerDefinition) return;

            CreationFlags flags = (CreationFlags)definition.CreationFlags;
            if ((flags & (CreationFlags.Delete | CreationFlags.Relocate |
                          CreationFlags.Recreate | CreationFlags.Upgrade |
                          CreationFlags.Optional)) != 0) return;

            Entity prefab = resolved[i].Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<BuildingData>(prefab)) return;

            var position = new float3(definition.Object.PosX, definition.Object.PosY,
                definition.Object.PosZ);
            var rotation = new quaternion(math.normalizesafe(
                new float4(definition.Object.RotX, definition.Object.RotY,
                    definition.Object.RotZ, definition.Object.RotW),
                new float4(0f, 0f, 0f, 1f)));
            bool snappedToNetwork = command.HasPlacementInput &&
                (command.PlacementTarget.Kind == PortableEntityKind.NetEdge ||
                 command.PlacementTarget.Kind == PortableEntityKind.NetNode);
            Entity building;
            BeginPortableResolve();
            try { building = FindIntegrationBuilding(prefab, position, rotation); }
            finally { EndPortableResolve(); }
            TrackRemoteBuilding(building, prefab, position, rotation,
                snappedToNetwork, "placed");
        }

        private Entity LiveRoadReference(Entity building)
        {
            if (building == Entity.Null || !EntityManager.Exists(building) ||
                EntityManager.HasComponent<Deleted>(building) ||
                !EntityManager.HasComponent<Building>(building)) return Entity.Null;
            Entity road = EntityManager.GetComponentData<Building>(building).m_RoadEdge;
            return road != Entity.Null && EntityManager.Exists(road) &&
                   !EntityManager.HasComponent<Deleted>(road) ? road : Entity.Null;
        }

        private void ApplyBuildingIntegrationRefreshes(long wallNow)
        {
            if (_buildingIntegrations.Count == 0) return;
            long activeNow = _buildingIntegrationClock.Observe(wallNow,
                RealizeGate.WorldBuildingHeld);
            if (RealizeGate.WorldBuildingHeld) return;

            int refreshed = 0;
            for (int i = 0; i < _buildingIntegrations.Count &&
                            refreshed < MaxBuildingIntegrationRefreshesPerFrame; i++)
            {
                PendingBuildingIntegration pending = _buildingIntegrations[i];
                if (pending.RootResolved && IntegrationRootWasSuperseded(pending))
                {
                    _buildingIntegrations.RemoveAt(i--);
                    continue;
                }
                if (!pending.RefreshQueued || pending.Building == Entity.Null ||
                    activeNow < pending.NextAttempt) continue;
                if (!IsLiveBuilding(pending.Building, pending.Prefab)) continue;

                TagBuildingIntegrationGraph(pending.Building);
                pending.RefreshQueued = false;
                pending.AwaitingObservation = true;
                pending.Attempts++;
                refreshed++;
            }
        }

        private void ObserveBuildingIntegrations(long wallNow)
        {
            if (_buildingIntegrations.Count == 0) return;
            bool held = RealizeGate.WorldBuildingHeld;
            long activeNow = _buildingIntegrationClock.Observe(wallNow, held);
            if (held) return;

            BeginPortableResolve();
            try
            {
                for (int i = _buildingIntegrations.Count - 1; i >= 0; i--)
                {
                    PendingBuildingIntegration pending = _buildingIntegrations[i];
                    if (pending.RootResolved && IntegrationRootWasSuperseded(pending))
                    {
                        _buildingIntegrations.RemoveAt(i);
                        continue;
                    }
                    if (!IsLiveBuilding(pending.Building, pending.Prefab))
                    {
                        pending.Building = FindIntegrationBuilding(pending.Prefab,
                            pending.Position, pending.Rotation);
                        pending.RootResolved = pending.Building != Entity.Null;
                    }

                    if (pending.Building == Entity.Null)
                    {
                        if (activeNow >= pending.Deadline)
                        {
                            RequestBuildingIntegrationRepair(pending,
                                new BuildingIntegrationState
                                {
                                    Detail = "the committed building root was not found",
                                });
                            _buildingIntegrations.RemoveAt(i);
                        }
                        continue;
                    }

                    Entity observedRoad = LiveRoadReference(pending.Building);
                    if (observedRoad != Entity.Null && PrefabRequiresRoad(pending.Prefab))
                    {
                        pending.RoadConnectionExpected = true;
                        pending.ExpectedRoad = observedRoad;
                    }

                    // Every tracked building gets one refresh; a settled graph can carry a stale warning.
                    if (!pending.AwaitingObservation) continue;

                    BuildingIntegrationState state = InspectBuildingIntegration(pending);
                    if (state.Ready)
                    {
                        SyncLog.Trace(LogTopic.Buildings,
                            "BuildSync: native building integration settled source=" +
                            pending.Source + " prefab=" + IntegrationPrefabName(pending.Prefab) +
                            " attempts=" + pending.Attempts);
                        _buildingIntegrations.RemoveAt(i);
                        continue;
                    }

                    if (activeNow >= pending.Deadline)
                    {
                        RequestBuildingIntegrationRepair(pending, state);
                        _buildingIntegrations.RemoveAt(i);
                        continue;
                    }

                    if (activeNow < pending.NextAttempt) continue;
                    pending.NextAttempt = activeNow + BuildingIntegrationRetryMs;
                    pending.RefreshQueued = true;
                    pending.AwaitingObservation = false;
                }
            }
            finally
            {
                EndPortableResolve();
            }
        }

        private Entity FindIntegrationBuilding(Entity prefab, float3 position,
            quaternion rotation)
        {
            Entity match = Entity.Null;
            int matches = 0;
            List<Entity> candidates = Candidates(_objectCandidates, _portableObjects, prefab);
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!IsLiveBuilding(candidate, prefab) || EntityManager.HasComponent<Owner>(candidate))
                    continue;
                global::Game.Objects.Transform candidateTransform = EntityManager
                    .GetComponentData<global::Game.Objects.Transform>(candidate);
                if (math.distancesq(candidateTransform.m_Position.xz, position.xz) >
                        BuildingIntegrationMatchDistanceSq ||
                    !IntegrationRotationMatches(candidateTransform.m_Rotation, rotation)) continue;
                matches++;
                match = candidate;
            }
            // XZ only when it identifies one root; stacked or duplicate candidates stay unresolved.
            return matches == 1 ? match : Entity.Null;
        }

        /// <summary>Restores a child's owner from its one-frame description; remote buildings only.</summary>
        internal bool TryRelinkExpectedBuildingOwner(Entity child, Entity ownerPrefab,
            float3 ownerPosition, quaternion ownerRotation)
        {
            if (child == Entity.Null || !EntityManager.Exists(child) ||
                EntityManager.HasComponent<Temp>(child) ||
                EntityManager.HasComponent<Deleted>(child) ||
                !EntityManager.HasComponent<Owner>(child)) return false;
            Owner current = EntityManager.GetComponentData<Owner>(child);
            if (current.m_Owner != Entity.Null) return false;

            bool expected = false;
            for (int i = 0; i < _buildingIntegrations.Count; i++)
            {
                PendingBuildingIntegration pending = _buildingIntegrations[i];
                if (pending.Prefab == ownerPrefab &&
                    math.distancesq(pending.Position, ownerPosition) <=
                    BuildingIntegrationMatchDistanceSq &&
                    IntegrationRotationMatches(pending.Rotation, ownerRotation))
                {
                    expected = true;
                    break;
                }
            }
            if (!expected) return false;

            Entity owner = Entity.Null;
            if (_lastIntegrationOwnerPrefab == ownerPrefab &&
                math.distancesq(_lastIntegrationOwnerPosition, ownerPosition) <=
                BuildingIntegrationMatchDistanceSq &&
                IntegrationRotationMatches(_lastIntegrationOwnerRotation, ownerRotation) &&
                IsLiveBuilding(_lastIntegrationOwner, ownerPrefab))
            {
                owner = _lastIntegrationOwner;
            }
            else
            {
                owner = FindIntegrationBuilding(ownerPrefab, ownerPosition, ownerRotation);
                _lastIntegrationOwnerPrefab = ownerPrefab;
                _lastIntegrationOwnerPosition = ownerPosition;
                _lastIntegrationOwnerRotation = ownerRotation;
                _lastIntegrationOwner = owner;
            }
            if (owner == Entity.Null || owner == child) return false;

            current.m_Owner = owner;
            EntityManager.SetComponentData(child, current);
            for (int i = 0; i < _buildingIntegrations.Count; i++)
            {
                PendingBuildingIntegration pending = _buildingIntegrations[i];
                if (pending.Prefab == ownerPrefab &&
                    math.distancesq(pending.Position, ownerPosition) <=
                    BuildingIntegrationMatchDistanceSq &&
                    IntegrationRotationMatches(pending.Rotation, ownerRotation))
                {
                    pending.Building = owner;
                    pending.RootResolved = true;
                }
            }
            return true;
        }

        /// <summary>One candidate snapshot per pass; the last-owner memo is discarded afterwards.</summary>
        internal void BeginExpectedBuildingOwnerRelinks()
        {
            BeginPortableResolve();
            ResetExpectedBuildingOwnerCache();
        }

        internal void EndExpectedBuildingOwnerRelinks()
        {
            ResetExpectedBuildingOwnerCache();
            EndPortableResolve();
        }

        private void ResetExpectedBuildingOwnerCache()
        {
            _lastIntegrationOwnerPrefab = Entity.Null;
            _lastIntegrationOwnerPosition = default(float3);
            _lastIntegrationOwnerRotation = quaternion.identity;
            _lastIntegrationOwner = Entity.Null;
        }

        private bool IsLiveBuilding(Entity building, Entity prefab)
        {
            if (building == Entity.Null || !EntityManager.Exists(building) ||
                EntityManager.HasComponent<Temp>(building) ||
                EntityManager.HasComponent<Deleted>(building) ||
                !EntityManager.HasComponent<Building>(building) ||
                !EntityManager.HasComponent<PrefabRef>(building) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(building)) return false;
            return EntityManager.GetComponentData<PrefabRef>(building).m_Prefab == prefab;
        }

        private bool IntegrationRootWasSuperseded(PendingBuildingIntegration pending)
        {
            Entity building = pending.Building;
            if (building == Entity.Null || !EntityManager.Exists(building) ||
                EntityManager.HasComponent<Deleted>(building) ||
                EntityManager.HasComponent<Temp>(building) ||
                !EntityManager.HasComponent<Building>(building) ||
                !EntityManager.HasComponent<PrefabRef>(building) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(building)) return true;
            if (EntityManager.GetComponentData<PrefabRef>(building).m_Prefab != pending.Prefab)
                return true;
            global::Game.Objects.Transform transform = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(building);
            return math.distancesq(transform.m_Position.xz, pending.Position.xz) >
                       BuildingIntegrationMatchDistanceSq ||
                   !IntegrationRotationMatches(transform.m_Rotation, pending.Rotation);
        }

        private static bool IntegrationRotationMatches(quaternion left, quaternion right)
        {
            return math.abs(math.dot(left.value, right.value)) >=
                   BuildingIntegrationRotationDot;
        }

        private BuildingIntegrationState InspectBuildingIntegration(
            PendingBuildingIntegration pending)
        {
            var state = new BuildingIntegrationState
            {
                Root = IsLiveBuilding(pending.Building, pending.Prefab),
                Road = true,
                Utilities = true,
            };
            if (!state.Root)
            {
                state.Detail = "the committed building root is no longer live";
                return state;
            }

            _buildingIntegrationVisited.Clear();
            _buildingIntegrationGraphBoundReached = false;
            state.OwnedGraph = IsOwnedIntegrationGraphReady(pending.Building,
                pending.Prefab, out state.Detail);
            bool roadConnectionExpected = pending.RoadConnectionExpected;
            if (roadConnectionExpected && pending.ExpectedRoad != Entity.Null &&
                (!EntityManager.Exists(pending.ExpectedRoad) ||
                 EntityManager.HasComponent<Deleted>(pending.ExpectedRoad)))
                roadConnectionExpected = false;
            if (roadConnectionExpected)
            {
                state.Road = HasReciprocalRoadConnection(pending.Building);
                if (!state.Road) state.Detail = "the required road link is absent or one-sided";
            }
            state.Utilities = HasExpectedUtilityConsumers(pending);
            if (!state.Utilities) state.Detail = "the expected utility consumers are absent";
            return state;
        }

        private bool IsOwnedIntegrationGraphReady(Entity owner, Entity ownerPrefab,
            out string reason)
        {
            reason = null;
            if (_buildingIntegrationGraphBoundReached) return true;
            if (_buildingIntegrationVisited.Count >= MaxBuildingIntegrationGraphEntities)
            {
                // Past the cap the graph is unknown, not broken; stop so it cannot look like a cycle.
                _buildingIntegrationGraphBoundReached = true;
                return true;
            }
            if (!_buildingIntegrationVisited.Add(owner))
            {
                reason = "the owned object graph contains a cycle";
                return false;
            }
            if (EntityManager.HasBuffer<global::Game.Net.SubLane>(owner))
            {
                DynamicBuffer<global::Game.Net.SubLane> ownerLanes =
                    EntityManager.GetBuffer<global::Game.Net.SubLane>(owner, true);
                for (int i = 0; i < ownerLanes.Length; i++)
                {
                    Entity lane = ownerLanes[i].m_SubLane;
                    if (!IsLiveOwnedEntity(owner, lane) ||
                        !EntityManager.HasComponent<Lane>(lane) ||
                        !EntityManager.HasComponent<PrefabRef>(lane))
                    {
                        reason = "an owned lane is stale or has no reciprocal owner";
                        return false;
                    }
                }
            }

            if (EntityManager.HasBuffer<global::Game.Net.SubNet>(owner))
            {
                DynamicBuffer<global::Game.Net.SubNet> subNets =
                    EntityManager.GetBuffer<global::Game.Net.SubNet>(owner, true);
                for (int i = 0; i < subNets.Length; i++)
                    if (!IsOwnedNetReady(owner, subNets[i].m_SubNet, out reason)) return false;
            }

            if (!EntityManager.HasBuffer<global::Game.Objects.SubObject>(owner)) return true;
            DynamicBuffer<global::Game.Objects.SubObject> subObjects =
                EntityManager.GetBuffer<global::Game.Objects.SubObject>(owner, true);
            for (int i = 0; i < subObjects.Length; i++)
            {
                Entity child = subObjects[i].m_SubObject;
                if (!IsLiveOwnedEntity(owner, child) ||
                    !EntityManager.HasComponent<PrefabRef>(child))
                {
                    reason = "a SubObject entry is stale or has no reciprocal owner";
                    return false;
                }
                // An attached child has its own lifecycle; only its link belongs to this placement.
                if (!EntityManager.HasComponent<Owner>(child)) continue;
                Entity childPrefab = EntityManager.GetComponentData<PrefabRef>(child).m_Prefab;
                if (!IsOwnedIntegrationGraphReady(child, childPrefab, out reason)) return false;
            }
            return true;
        }

        private bool IsOwnedNetReady(Entity owner, Entity net, out string reason)
        {
            reason = null;
            if (!IsLiveOwnedEntity(owner, net) ||
                !EntityManager.HasComponent<PrefabRef>(net) ||
                !EntityManager.HasBuffer<global::Game.Net.SubLane>(net))
            {
                reason = "an owned network entry is stale or incomplete";
                return false;
            }

            Entity netPrefab = EntityManager.GetComponentData<PrefabRef>(net).m_Prefab;
            if (netPrefab == Entity.Null || !EntityManager.Exists(netPrefab) ||
                !EntityManager.HasComponent<NetData>(netPrefab))
            {
                reason = "an owned network entry has no usable prefab";
                return false;
            }

            bool node = EntityManager.HasComponent<Node>(net);
            bool edge = EntityManager.HasComponent<Edge>(net);
            if (node == edge)
            {
                reason = "an owned network entry is neither one node nor one edge";
                return false;
            }
            if (node && !EntityManager.HasBuffer<ConnectedEdge>(net))
            {
                reason = "an owned network node has no connection buffer";
                return false;
            }
            if (edge)
            {
                if (!EntityManager.HasComponent<Curve>(net) ||
                    !EntityManager.HasBuffer<ConnectedNode>(net))
                {
                    reason = "an owned network edge has no curve or connection buffer";
                    return false;
                }
                Edge edgeData = EntityManager.GetComponentData<Edge>(net);
                if (!IsLiveNetNode(edgeData.m_Start) || !IsLiveNetNode(edgeData.m_End))
                {
                    reason = "an owned network edge has a stale endpoint";
                    return false;
                }
            }

            DynamicBuffer<global::Game.Net.SubLane> lanes =
                EntityManager.GetBuffer<global::Game.Net.SubLane>(net, true);
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!IsLiveOwnedEntity(net, lane) || !EntityManager.HasComponent<Lane>(lane) ||
                    !EntityManager.HasComponent<PrefabRef>(lane))
                {
                    reason = "an owned network lane is stale or has no reciprocal owner";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Linked by <see cref="Owner"/> or by <see cref="global::Game.Objects.Attached"/>: attached objects
        /// join the parent's SubObject buffer without an Owner.
        /// </summary>
        private bool IsLiveOwnedEntity(Entity owner, Entity child)
        {
            if (child == Entity.Null || !EntityManager.Exists(child) ||
                EntityManager.HasComponent<Temp>(child) ||
                EntityManager.HasComponent<Deleted>(child)) return false;
            if (EntityManager.HasComponent<Owner>(child) &&
                EntityManager.GetComponentData<Owner>(child).m_Owner == owner) return true;
            return EntityManager.HasComponent<global::Game.Objects.Attached>(child) &&
                   EntityManager.GetComponentData<global::Game.Objects.Attached>(child).m_Parent ==
                       owner;
        }

        private bool IsLiveNetNode(Entity node)
        {
            return node != Entity.Null && EntityManager.Exists(node) &&
                   !EntityManager.HasComponent<Temp>(node) &&
                   !EntityManager.HasComponent<Deleted>(node) &&
                   EntityManager.HasComponent<Node>(node) &&
                   EntityManager.HasBuffer<ConnectedEdge>(node);
        }

        private void TagBuildingIntegrationGraph(Entity building)
        {
            _buildingIntegrationVisited.Clear();
            _buildingIntegrationGraphBoundReached = false;
            _buildingIntegrationRefreshTargets.Clear();
            _oneSidedRoadConnections.Clear();
            CollectIntegrationOwner(building);
            RepairOneSidedRoadConnections();
            for (int i = 0; i < _buildingIntegrationRefreshTargets.Count; i++)
            {
                Entity target = _buildingIntegrationRefreshTargets[i];
                if (!EntityManager.HasComponent<Updated>(target))
                    EntityManager.AddComponent<Updated>(target);
            }
        }

        private void CollectIntegrationOwner(Entity owner)
        {
            if (owner == Entity.Null || !EntityManager.Exists(owner) ||
                EntityManager.HasComponent<Temp>(owner) ||
                EntityManager.HasComponent<Deleted>(owner) ||
                !_buildingIntegrationVisited.Add(owner) ||
                _buildingIntegrationVisited.Count > MaxBuildingIntegrationGraphEntities) return;

            bool nativeOwnerTrigger = EntityManager.HasComponent<Building>(owner) ||
                                      EntityManager.HasBuffer<global::Game.Net.SubLane>(owner) ||
                                      EntityManager.HasComponent<global::Game.Buildings.ServiceUpgrade>(owner) ||
                                      EntityManager.HasComponent<global::Game.Objects.SpawnLocation>(owner) ||
                                      EntityManager.HasComponent<global::Game.Routes.TakeoffLocation>(owner);
            if (nativeOwnerTrigger) _buildingIntegrationRefreshTargets.Add(owner);
            if (EntityManager.HasComponent<Building>(owner))
                CollectRoadReciprocal(owner);

            if (EntityManager.HasBuffer<global::Game.Net.SubNet>(owner))
            {
                DynamicBuffer<global::Game.Net.SubNet> subNets =
                    EntityManager.GetBuffer<global::Game.Net.SubNet>(owner, true);
                for (int i = 0; i < subNets.Length; i++)
                    CollectIntegrationOwner(subNets[i].m_SubNet);
            }
            if (EntityManager.HasBuffer<global::Game.Objects.SubObject>(owner))
            {
                DynamicBuffer<global::Game.Objects.SubObject> subObjects =
                    EntityManager.GetBuffer<global::Game.Objects.SubObject>(owner, true);
                for (int i = 0; i < subObjects.Length; i++)
                    CollectIntegrationOwner(subObjects[i].m_SubObject);
            }
        }

        private void CollectRoadReciprocal(Entity building)
        {
            Entity road = EntityManager.GetComponentData<Building>(building).m_RoadEdge;
            if (road == Entity.Null || !EntityManager.Exists(road) ||
                EntityManager.HasComponent<Temp>(road) ||
                EntityManager.HasComponent<Deleted>(road) ||
                !EntityManager.HasComponent<Edge>(road) ||
                !EntityManager.HasBuffer<ConnectedBuilding>(road)) return;

            _buildingIntegrationRefreshTargets.Add(road);
            DynamicBuffer<ConnectedBuilding> connected =
                EntityManager.GetBuffer<ConnectedBuilding>(road, true);
            for (int i = 0; i < connected.Length; i++)
                if (connected[i].m_Building == building) return;
            _oneSidedRoadConnections.Add(new OneSidedRoadConnection
            {
                Building = building,
                Road = road,
            });
        }

        /// <summary>
        /// Restores the reciprocal of a live Building.m_RoadEdge; Updated on both sides lets the native
        /// connection passes refresh.
        /// </summary>
        private void RepairOneSidedRoadConnections()
        {
            for (int i = 0; i < _oneSidedRoadConnections.Count; i++)
            {
                OneSidedRoadConnection missing = _oneSidedRoadConnections[i];
                if (missing.Building == Entity.Null || !EntityManager.Exists(missing.Building) ||
                    EntityManager.HasComponent<Temp>(missing.Building) ||
                    EntityManager.HasComponent<Deleted>(missing.Building) ||
                    !EntityManager.HasComponent<Building>(missing.Building) ||
                    !EntityManager.Exists(missing.Road) ||
                    EntityManager.HasComponent<Temp>(missing.Road) ||
                    EntityManager.HasComponent<Deleted>(missing.Road) ||
                    !EntityManager.HasBuffer<ConnectedBuilding>(missing.Road)) continue;
                if (EntityManager.GetComponentData<Building>(missing.Building).m_RoadEdge !=
                    missing.Road) continue;

                DynamicBuffer<ConnectedBuilding> connected =
                    EntityManager.GetBuffer<ConnectedBuilding>(missing.Road);
                bool alreadyPresent = false;
                for (int j = 0; j < connected.Length; j++)
                    if (connected[j].m_Building == missing.Building)
                    {
                        alreadyPresent = true;
                        break;
                    }
                if (alreadyPresent) continue;

                connected.Add(new ConnectedBuilding { m_Building = missing.Building });
                Entity update = EntityManager.CreateEntity();
                EntityManager.AddComponent<global::Game.Common.Event>(update);
                EntityManager.AddComponentData(update, new RoadConnectionUpdated
                {
                    m_Building = missing.Building,
                    m_Old = Entity.Null,
                    m_New = missing.Road,
                });
            }
        }

        private bool HasReciprocalRoadConnection(Entity building)
        {
            Building data = EntityManager.GetComponentData<Building>(building);
            Entity road = data.m_RoadEdge;
            if (road == Entity.Null || !EntityManager.Exists(road) ||
                EntityManager.HasComponent<Temp>(road) ||
                EntityManager.HasComponent<Deleted>(road) ||
                !EntityManager.HasBuffer<ConnectedBuilding>(road)) return false;
            DynamicBuffer<ConnectedBuilding> connected =
                EntityManager.GetBuffer<ConnectedBuilding>(road, true);
            for (int i = 0; i < connected.Length; i++)
                if (connected[i].m_Building == building) return true;
            return false;
        }

        private bool HasExpectedUtilityConsumers(PendingBuildingIntegration pending)
        {
            Entity building = pending.Building;
            if (EntityManager.HasComponent<global::Game.Objects.UnderConstruction>(building) ||
                EntityManager.HasComponent<Abandoned>(building) ||
                EntityManager.HasComponent<Destroyed>(building) ||
                EntityManager.HasComponent<global::Game.Objects.Placeholder>(building)) return true;

            return BuildingIntegrationPolicy.HasExpectedConsumers(
                pending.ExpectsElectricityConsumer, pending.ExpectsWaterConsumer,
                EntityManager.HasComponent<ElectricityConsumer>(building),
                EntityManager.HasComponent<WaterConsumer>(building));
        }

        private void GetExpectedUtilityConsumers(Entity prefab, out bool expectsElectricity,
            out bool expectsWater)
        {
            expectsElectricity = false;
            expectsWater = false;
            if (!EntityManager.HasComponent<ConsumptionData>(prefab) ||
                !EntityManager.HasComponent<ObjectData>(prefab)) return;

            // Consumption metadata does not imply consumers; the prefab's archetype decides.
            EntityArchetype archetype = EntityManager.GetComponentData<ObjectData>(prefab).m_Archetype;
            if (!archetype.Valid) return;
            NativeArray<ComponentType> components = archetype.GetComponentTypes();
            try
            {
                TypeIndex electricity = TypeManager.GetTypeIndex<ElectricityConsumer>();
                TypeIndex water = TypeManager.GetTypeIndex<WaterConsumer>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i].TypeIndex == electricity) expectsElectricity = true;
                    else if (components[i].TypeIndex == water) expectsWater = true;
                }
            }
            finally
            {
                components.Dispose();
            }
        }

        private bool PrefabRequiresRoad(Entity prefab)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<BuildingData>(prefab)) return false;
            BuildingData data = EntityManager.GetComponentData<BuildingData>(prefab);
            return (data.m_Flags & global::Game.Prefabs.BuildingFlags.RequireRoad) != 0;
        }

        private void RequestBuildingIntegrationRepair(PendingBuildingIntegration pending,
            BuildingIntegrationState state)
        {
            string prefabName = IntegrationPrefabName(pending.Prefab);
            SyncLog.Warn(LogTopic.Buildings, "BuildSync: remote " + pending.Source +
                " building '" + prefabName + "' did not complete native integration (" +
                (state.Detail ?? "unknown graph state") + "); requesting world repair.");
            SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                .Create("remote building failed native integration", "object",
                    CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.MissingTarget)
                .About("native graph of '" + prefabName + "' at (" +
                       pending.Position.x.ToString("F1") + "," +
                       pending.Position.z.ToString("F1") + ")")
                .Tried("re-ran native building, lane and connection initialization for 15 s of active attempts")
                .Fact("found the building root", state.Root)
                .Fact("has a reciprocal owned graph", state.OwnedGraph)
                .Fact("joined its expected road", state.Road)
                .Fact("has its utility consumers", state.Utilities));
        }

        private string IntegrationPrefabName(Entity prefab)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)) return "unknown";
            try { return _prefabSystem.GetPrefabName(prefab); }
            catch { return prefab.ToString(); }
        }

        private void ClearBuildingIntegrations()
        {
            _buildingIntegrations.Clear();
            _buildingIntegrationVisited.Clear();
            _buildingIntegrationRefreshTargets.Clear();
            _oneSidedRoadConnections.Clear();
            _buildingIntegrationClock.Reset();
            ResetExpectedBuildingOwnerCache();
        }
    }
}
