using System.Collections.Concurrent;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates building relocation: detect Updated + <see cref="MovedLocation"/> and
    /// broadcast <see cref="ObjectMoveCommand"/> (old position identifies entity). Realize
    /// by spawning <see cref="CreationDefinition"/> with <c>m_Original</c> and
    /// Permanent|Relocate flags, same as game's move tool.
    /// </summary>
    public partial class MoveSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _movedObjects;
        private EntityQuery _liveObjects;
        private CommandObserver _observer;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(MoveSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            // Top-level objects relocated this frame. Updated narrows MovedLocation (which
            // can persist) to the frame the move actually happened.
            _movedObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<MovedLocation>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Created>(),
                },
            });

            _liveObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<global::Game.Net.Edge>(),
                },
            });

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, ObjectMoveCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
        }

        protected override void OnDestroy()
        {
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = service.NowMs;
            _guard.Prune(now);
            CaptureMoves(session, now);
        }

        /// <summary>Called by <see cref="SyncRealizeSystem"/> during ToolUpdate (see there for why).</summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;
            RealizeIncoming(session, service.NowMs);
        }

        private void CaptureMoves(MultiplayerSession session, long now)
        {
            if (_movedObjects.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _movedObjects.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    float3 oldPos = EntityManager.GetComponentData<MovedLocation>(entity).m_OldPosition;
                    Transform transform = EntityManager.GetComponentData<Transform>(entity);

                    // No actual displacement → an unrelated Updated on a once-moved object.
                    if (math.distancesq(oldPos, transform.m_Position) < 0.01f) continue;
                    if (_guard.Consume(MoveKey(name, transform.m_Position), now)) continue;

                    var command = new ObjectMoveCommand
                    {
                        PrefabName = name,
                        OldX = oldPos.x, OldY = oldPos.y, OldZ = oldPos.z,
                        NewX = transform.m_Position.x, NewY = transform.m_Position.y, NewZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x, RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z, RotW = transform.m_Rotation.value.w,
                    };
                    session.SendCommand(0, ObjectMoveCommand.Id, command.Encode());
                    Mod.Verbose("[MP] MoveSync captured relocation of '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                ObjectMoveCommand command;
                try { command = ObjectMoveCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] MoveSync: dropping malformed command: " + ex.Message); continue; }

                Entity prefab;
                if (!_prefabIndex.TryResolve(command.PrefabName, out prefab))
                {
                    Mod.log.Warn("[MP] MoveSync realize: unknown prefab '" + command.PrefabName + "'; skipping.");
                    continue;
                }

                var oldPos = new float3(command.OldX, command.OldY, command.OldZ);
                var newPos = new float3(command.NewX, command.NewY, command.NewZ);
                Entity original = FindAt(prefab, oldPos);
                if (original == Entity.Null)
                {
                    Mod.log.Warn("[MP] MoveSync realize: no local '" + command.PrefabName + "' near (" +
                                 oldPos.x.ToString("F0") + "," + oldPos.z.ToString("F0") + ") to move; skipping.");
                    continue;
                }

                _guard.Mark(MoveKey(command.PrefabName, newPos), now);
                try
                {
                    // The move tool's commit definition: m_Original points at the existing
                    // entity, Relocate tells GenerateObjectsSystem to move it instead of
                    // spawning a copy.
                    Entity definition = EntityManager.CreateEntity();
                    EntityManager.AddComponentData(definition, new CreationDefinition
                    {
                        m_Prefab = prefab,
                        m_Original = original,
                        m_RandomSeed = 0,
                        m_Flags = CreationFlags.Permanent | CreationFlags.Relocate,
                    });
                    EntityManager.AddComponentData(definition, new ObjectDefinition
                    {
                        m_Position = newPos,
                        m_Rotation = new quaternion(command.RotX, command.RotY, command.RotZ, command.RotW),
                        m_Scale = new float3(1f, 1f, 1f),
                        m_Probability = 100,
                    });
                    EntityManager.AddComponent<Updated>(definition);
                    EntityManager.AddComponent<Deleted>(definition);
                    Mod.Verbose("[MP] MoveSync realize: moved '" + command.PrefabName + "' from player " +
                                 message.OriginPlayerId + " to (" + newPos.x.ToString("F1") + "," +
                                 newPos.z.ToString("F1") + ").");
                }
                catch (System.Exception ex)
                {
                    Mod.log.Error("[MP] MoveSync realize FAILED for '" + command.PrefabName + "': " + ex);
                }
            }
        }

        private Entity FindAt(Entity prefab, float3 position)
        {
            NativeArray<Entity> candidates = _liveObjects.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(candidates[i]).m_Prefab != prefab) continue;
                    float3 pos = EntityManager.GetComponentData<Transform>(candidates[i]).m_Position;
                    if (math.distancesq(pos, position) <= 4f) return candidates[i];
                }
            }
            finally
            {
                candidates.Dispose();
            }
            return Entity.Null;
        }

        private static string MoveKey(string prefabName, float3 newPosition) =>
            "mov|" + ReplicationGuard.Key(prefabName, newPosition);

    }
}
