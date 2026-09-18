using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates the Abandoned / Condemned / Destroyed markers of non-spawnable buildings
    /// (service buildings, signature buildings) - the one building-state slice with no owner:
    /// growable lifecycle sync only watches spawnables, and delete sync only watches removals.
    /// One <see cref="ServiceBuildingStateCommand"/> per transition carries the resulting
    /// marker set; the receiver writes the same markers and nudges dependents with Updated.
    ///
    /// Detection is a rolling scan over native UpdateFrame buckets (16 slices, same shape as
    /// the growable state scan), seeded once per session so pre-session ruins never broadcast.
    /// Realization resolves the standing building by prefab and position and retries briefly
    /// when a placement is still in flight - the same window the fire sync uses.
    /// </summary>
    public partial class ServiceBuildingStateSyncSystem : GameSystemBase
    {
        /// <summary>Native UpdateFrame partitions entities into this many buckets.</summary>
        private const int ScanBuckets = 16;

        /// <summary>Realize attempts per frame: each one scans the live buildings.</summary>
        private const int MaxRealizePerFrame = 4;

        /// <summary>How long a missing match gets retried before its state is dropped.</summary>
        private const long RetryWindowMs = 10000;

        /// <summary>Building match tolerance, squared metres (2 m): buildings do not move.</summary>
        private const float MatchTolSq = 4f;

        /// <summary>Dead-entity prune interval (30 s, same as the audits).</summary>
        private const long PruneIntervalMs = 30000;

        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly Dictionary<Entity, byte> _lastSeen = new Dictionary<Entity, byte>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();
        private readonly List<(ServiceBuildingStateCommand command, long deadline)> _retry =
            new List<(ServiceBuildingStateCommand, long)>();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _scanBuildings;
        private EntityQuery _liveBuildings;
        private CommandObserver _observer;
        private bool _seeded;
        private int _scanBucket;
        private long _nextPruneMs;

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            // UpdateFrame is required, not incidental: the rolling scan filters this
            // query by it, and filtering by an absent component throws. Same tradeoff
            // as the growable state scan - a building without UpdateFrame stays invisible.
            _scanBuildings = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, PrefabRef, Transform, UpdateFrame>(),
                None = SyncQuery.ReadOnly<Temp, Deleted, Owner>(),
            });

            _liveBuildings = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, PrefabRef, Transform>(),
                None = SyncQuery.ReadOnly<Temp, Deleted>(),
            });

            _observer = SyncObserverBinding.Bind(
                () => new CommandObserver(_incoming, ServiceBuildingStateCommand.Id)
                    {
                        MaxBodyBytes = ServiceBuildingStateCommand.MaxEncodedBytes,
                    },
                DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            SyncObserverBinding.Unbind(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("ServiceBuildingState"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    if (_lastSeen.Count > 0) _lastSeen.Clear();
                    if (_retry.Count > 0) _retry.Clear();
                    _guard.Clear();
                    _seeded = false;
                    _nextPruneMs = 0;
                    return;
                }

                long now = service.NowMs;
                _guard.Prune(now);

                if (!_seeded)
                {
                    SeedCache();
                    _seeded = true;
                    _nextPruneMs = now + PruneIntervalMs;
                    return;
                }

                ScanBucket(session, now);
                PruneDead(now);
            }
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate, next to fire sync:
        /// marker writes are plain component changes, no definitions and no terrain involved.</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;
            if (!service.GameplaySyncReady)
            {
                SyncInbox.Clear(_incoming);
                if (_retry.Count > 0) _retry.Clear();
                return;
            }

            MultiplayerSession session = service.Session;
            long now = service.NowMs;
            int attempts = 0;

            List<(ServiceBuildingStateCommand command, long deadline)> due = null;
            for (int i = 0; i < _retry.Count; i++)
            {
                if (_retry[i].deadline < now)
                {
                    SyncLog.Detail(LogTopic.Buildings, "ServiceBuildingState: giving up on '" +
                        _retry[i].command.PrefabName + "' whose building never arrived.");
                    continue;
                }
                (due ?? (due = new List<(ServiceBuildingStateCommand, long)>()))
                    .Add(_retry[i]);
            }
            _retry.Clear();
            if (due != null)
            {
                for (int i = 0; i < due.Count; i++)
                {
                    if (attempts >= MaxRealizePerFrame)
                    {
                        for (int j = i; j < due.Count; j++) _retry.Add(due[j]);
                        break;
                    }
                    attempts++;
                    if (!Realize(due[i].command, now))
                        _retry.Add((due[i].command, due[i].deadline));
                }
            }

            SimulationCommandMessage message;
            while (attempts < MaxRealizePerFrame && _incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                ServiceBuildingStateCommand command;
                try { command = ServiceBuildingStateCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.Buildings, "ServiceBuildingState: dropping malformed command: " +
                        ex.Message);
                    continue;
                }

                // Every scan counts toward the cap, match or not.
                attempts++;
                if (!Realize(command, now))
                    _retry.Add((command, now + RetryWindowMs));
            }
        }

        // ---- Capture ------------------------------------------------------------

        /// <summary>
        /// Learn every non-spawnable building's markers when sync starts (both sides hold the
        /// same downloaded world) without sending anything. Without this, every pre-session
        /// ruin would broadcast once on its first scan pass.
        /// </summary>
        private void SeedCache()
        {
            _scanBuildings.ResetFilter();
            NativeArray<Entity> entities = _scanBuildings.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (!IsCovered(entity)) continue;
                    _lastSeen[entity] = ReadFlags(entity);
                }
                SyncLog.Detail(LogTopic.Buildings, "ServiceBuildingState: watching " +
                    _lastSeen.Count + " service building(s).");
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void ScanBucket(MultiplayerSession session, long now)
        {
            _scanBuildings.SetSharedComponentFilter(new UpdateFrame((uint)_scanBucket));
            _scanBucket = (_scanBucket + 1) % ScanBuckets;

            NativeArray<Entity> entities = _scanBuildings.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (!IsCovered(entity)) continue;

                    byte current = ReadFlags(entity);
                    byte known;
                    if (!_lastSeen.TryGetValue(entity, out known))
                    {
                        // New to us (placed after seeding): adopt silently, like the seed.
                        _lastSeen[entity] = current;
                        continue;
                    }
                    if (current == known) continue;

                    // Re-check: the building may have been demolished between the
                    // covered check above and these resolving reads.
                    if (!EntityManager.Exists(entity)) continue;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string prefabName = _prefabIndex.NameOf(prefab);
                    float3 position = EntityManager.GetComponentData<Transform>(entity).m_Position;
                    if (string.IsNullOrEmpty(prefabName))
                    {
                        _lastSeen[entity] = current;
                        continue;
                    }

                    string key = ReplicationGuard.Key(prefabName, position);
                    _lastSeen[entity] = current;
                    if (_guard.Consume(key, now)) continue; // our own realize - no echo

                    var command = new ServiceBuildingStateCommand
                    {
                        PrefabName = prefabName,
                        X = position.x,
                        Y = position.y,
                        Z = position.z,
                        StateFlags = current,
                    };
                    try
                    {
                        session.SendCommand(0, ServiceBuildingStateCommand.Id, command.Encode());
                    }
                    catch (System.Exception ex)
                    {
                        SyncLog.Warn(LogTopic.Buildings, "ServiceBuildingState: refusing to send '" +
                            prefabName + "': " + ex.Message);
                        continue;
                    }
                    SyncLog.Detail(LogTopic.Buildings, "ServiceBuildingState sent '" + prefabName +
                        "' at " + position + ", flags " + current + ".");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void PruneDead(long now)
        {
            if (now < _nextPruneMs || _lastSeen.Count == 0) return;
            _nextPruneMs = now + PruneIntervalMs;
            List<Entity> dead = null;
            foreach (Entity entity in _lastSeen.Keys)
            {
                if (EntityManager.Exists(entity) &&
                    !EntityManager.HasComponent<Deleted>(entity)) continue;
                // Gone or demolished: removals replicate through delete sync, and a marker
                // for a grave nobody stands on carries nothing. Drop silently.
                if (dead == null) dead = new List<Entity>();
                dead.Add(entity);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _lastSeen.Remove(dead[i]);
        }

        /// <summary>
        /// True for buildings this system owns: everything that is not an autonomous growable.
        /// Spawnables stay with growable lifecycle sync (same predicate it uses); removals of
        /// any kind stay with delete sync.
        /// </summary>
        private bool IsCovered(Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) ||
                !EntityManager.HasComponent<PrefabRef>(entity)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)) return false;
            return !EntityManager.HasComponent<SpawnableBuildingData>(prefab) ||
                EntityManager.HasComponent<SignatureBuildingData>(prefab);
        }

        private byte ReadFlags(Entity entity)
        {
            byte flags = 0;
            if (EntityManager.HasComponent<Abandoned>(entity))
                flags |= ServiceBuildingStateCommand.StateAbandoned;
            if (EntityManager.HasComponent<Condemned>(entity))
                flags |= ServiceBuildingStateCommand.StateCondemned;
            if (EntityManager.HasComponent<Destroyed>(entity))
                flags |= ServiceBuildingStateCommand.StateDestroyed;
            return flags;
        }

        // ---- Realize ------------------------------------------------------------

        /// <summary>True when the command settled (applied, already matching, or permanently unresolvable).</summary>
        private bool Realize(ServiceBuildingStateCommand command, long now)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
            {
                SyncLog.Warn(LogTopic.Buildings, "ServiceBuildingState: no local prefab named '" +
                    command.PrefabName + "'; ignoring.");
                return true;
            }

            float3 target = new float3(command.X, command.Y, command.Z);
            Entity best = Entity.Null;
            float bestDistSq = MatchTolSq;

            NativeArray<Entity> candidates = _liveBuildings.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!EntityManager.Exists(candidate)) continue;
                    if (!EntityManager.HasComponent<PrefabRef>(candidate)) continue;
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab)
                        continue;
                    if (!EntityManager.HasComponent<Transform>(candidate)) continue;
                    float3 position = EntityManager
                        .GetComponentData<Transform>(candidate).m_Position;
                    float distSq = math.distancesq(position, target);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = candidate;
                    }
                }
            }
            finally
            {
                candidates.Dispose();
            }

            if (best == Entity.Null) return false;

            // A growable standing where the command points is owned by growable lifecycle
            // sync - never drive the same markers from two systems.
            Entity bestPrefab = EntityManager.GetComponentData<PrefabRef>(best).m_Prefab;
            if (EntityManager.HasComponent<SpawnableBuildingData>(bestPrefab) &&
                !EntityManager.HasComponent<SignatureBuildingData>(bestPrefab))
                return true;

            bool changed = false;
            changed |= SetMarker<Abandoned>(best,
                (command.StateFlags & ServiceBuildingStateCommand.StateAbandoned) != 0);
            changed |= SetMarker<Condemned>(best,
                (command.StateFlags & ServiceBuildingStateCommand.StateCondemned) != 0);
            changed |= SetMarker<Destroyed>(best,
                (command.StateFlags & ServiceBuildingStateCommand.StateDestroyed) != 0);
            if (changed && !EntityManager.HasComponent<Updated>(best))
                EntityManager.AddComponent<Updated>(best);

            _guard.Mark(ReplicationGuard.Key(command.PrefabName, target), now);
            if (changed)
                SyncLog.Detail(LogTopic.Buildings, "ServiceBuildingState realized '" +
                    command.PrefabName + "' at " + target + ", flags " + command.StateFlags + ".");
            return true;
        }

        private bool SetMarker<T>(Entity entity, bool wanted) where T : unmanaged, IComponentData
        {
            bool has = EntityManager.HasComponent<T>(entity);
            if (has == wanted) return false;
            if (wanted) EntityManager.AddComponent<T>(entity);
            else EntityManager.RemoveComponent<T>(entity);
            return true;
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
        }
    }
}
