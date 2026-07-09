using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Game;
using Game.City;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Replicates object placements (buildings, props) bidirectionally: detect <see cref="Created"/>
    /// non-replicas, broadcast <see cref="ObjectPlacementCommand"/>; realize by spawning
    /// <see cref="CreationDefinition"/>. Guards via player id and <see cref="ReplicationGuard"/>;
    /// host relays to other clients. Known: Created query includes zoning growth.
    /// </summary>
    public partial class BuildSyncSystem : GameSystemBase
    {
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        /// <summary>A net object can outrun the road it attaches to; hold it until the node exists.</summary>
        private const long AttachRetryWindowMs = 10000;

        /// <summary>Ceiling on the wait list, so a peer can never grow it without bound.</summary>
        private const int MaxPendingAttachments = 256;

        private readonly List<(ObjectPlacementCommand command, Entity prefab, int originPlayerId, long deadline)> _attachRetry =
            new List<(ObjectPlacementCommand, Entity, int, long)>();

        private readonly Dictionary<string, int> _diag = new Dictionary<string, int>();
        private long _diagStartMs = -1;
        private int _diagTotal;

        // Diagnostic probes: how many entities each successive filter sees, so a quiet log
        // pinpoints whether the update phase is even seeing freshly-Created entities.
        private int _hbUpdates, _hbAnyCreated, _hbCreatedPrefab, _hbCreatedTransform, _hbFiltered;
        private EntityQuery _diagAnyCreated, _diagCreatedPrefab, _diagCreatedTransform;

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private ToolSystem _toolSystem;
        private EntityQuery _createdObjects;
        private EntityQuery _liveNodes;
        private EntityQuery _liveEdges;
        private EntityQuery _liveStaticObjects;
        private CommandObserver _observer;

        // Used by the realize path to reproduce the game's own building placement (a building
        // emits an object definition plus owner-linked lot-area and connection-net definitions).
        // leftHandTraffic mirrors the driveway sub-nets the way the game does; the two prefab
        // lookups feed NetUtils.GetSubNet / AreaUtils.SelectAreaPrefab. See Realize.cs.
        private CityConfigurationSystem _cityConfig;
        private ComponentLookup<NetGeometryData> _netGeometryLookup;
        private ComponentLookup<SpawnableObjectData> _spawnableObjectLookup;

        protected override void OnCreate()
        {
            base.OnCreate();

            Mod.log.Info(nameof(BuildSyncSystem) + " ready.");
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            _prefabIndex = new PrefabIndex(_prefabSystem, GetEntityQuery(ComponentType.ReadOnly<PrefabData>()));

            _cityConfig = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            _netGeometryLookup = GetComponentLookup<NetGeometryData>(isReadOnly: true);
            _spawnableObjectLookup = GetComponentLookup<SpawnableObjectData>(isReadOnly: true);

            // Top-level objects created this frame: prefab + transform, not a tool preview
            // (Temp), not an owned sub-object (Owner), not being deleted, not a net edge.
            _createdObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Created>(),
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

            // Attach targets for incoming net objects, matched by position.
            _liveNodes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<global::Game.Net.Node>() },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
            _liveEdges = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Net.Edge>(),
                    ComponentType.ReadOnly<global::Game.Net.Curve>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            // Standing placed objects (buildings, props), for the duplicate-placement guard in
            // Realize.cs. Static excludes vehicles/cims; Owner excludes sub-objects.
            _liveStaticObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<global::Game.Objects.Static>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });

            _diagAnyCreated = GetEntityQuery(ComponentType.ReadOnly<Created>());
            _diagCreatedPrefab = GetEntityQuery(ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<PrefabRef>());
            _diagCreatedTransform = GetEntityQuery(
                ComponentType.ReadOnly<Created>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>());

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, ObjectPlacementCommand.Id);
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

            // Diagnostic sampling runs every frame so the 5 s summary reflects peak visibility.
            _hbUpdates++;
            _hbAnyCreated = System.Math.Max(_hbAnyCreated, _diagAnyCreated.CalculateEntityCount());
            _hbCreatedPrefab = System.Math.Max(_hbCreatedPrefab, _diagCreatedPrefab.CalculateEntityCount());
            _hbCreatedTransform = System.Math.Max(_hbCreatedTransform, _diagCreatedTransform.CalculateEntityCount());
            _hbFiltered = System.Math.Max(_hbFiltered, _createdObjects.CalculateEntityCount());

            long now = service.NowMs;
            MultiplayerSession session = service.Session;
            if (service.GameplaySyncReady)
            {
                _guard.Prune(now);
                CaptureNewObjects(session, now);
            }
            FlushDiagnostics(now, service.GameplaySyncReady);
        }

        /// <summary>
        /// Called by <see cref="SyncRealizeSystem"/> during ToolUpdate. Definitions realize
        /// when created before Modification1 (see frame order in <see cref="SyncRealizeSystem"/>).
        /// Capture stays at ModificationEnd where one-frame <see cref="Created"/> tags live.
        /// </summary>
        public void RealizePending()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (service.GameplaySyncReady)
                RealizeIncoming(session, service.NowMs);
        }

        // Periodic summary of what the detector captured — reveals over-capture severity
        // and the exact prefab names being synced, without flooding the log per object.
        private void RecordDiagnostic(string prefabName)
        {
            _diagTotal++;
            int count;
            _diag.TryGetValue(prefabName, out count);
            _diag[prefabName] = count + 1;
        }

        private void FlushDiagnostics(long now, bool connected)
        {
            if (_diagStartMs < 0) { _diagStartMs = now; return; }
            if (now - _diagStartMs < 5000) return;

            // Only log when something is happening, to avoid spamming an idle main menu.
            if (connected || _hbAnyCreated > 0 || _diagTotal > 0)
            {
                var sb = new StringBuilder();
                sb.Append("[MP] BuildSync/5s: updates=").Append(_hbUpdates)
                  .Append(" created[any/+prefab/+transform/filtered]=")
                  .Append(_hbAnyCreated).Append('/').Append(_hbCreatedPrefab).Append('/')
                  .Append(_hbCreatedTransform).Append('/').Append(_hbFiltered)
                  .Append(" emitted=").Append(_diagTotal);
                if (_diagTotal > 0)
                {
                    sb.Append(" [");
                    int n = 0;
                    foreach (var pair in _diag)
                    {
                        if (n > 0) sb.Append(", ");
                        sb.Append(pair.Key).Append(" x").Append(pair.Value);
                        if (++n >= 10) { sb.Append(", ..."); break; }
                    }
                    sb.Append(']');
                }
                Mod.Verbose(sb.ToString());
            }

            _diag.Clear();
            _diagTotal = 0;
            _diagStartMs = now;
            _hbUpdates = _hbAnyCreated = _hbCreatedPrefab = _hbCreatedTransform = _hbFiltered = 0;
        }

        private void CaptureNewObjects(MultiplayerSession session, long now)
        {
            if (_createdObjects.IsEmptyIgnoreFilter) return;

            // Narrow capture to genuine player placements: tool-applied objects appear
            // on frames where the object tool is active. Simulation-spawned objects
            // (zone growth, sub-spawns) arrive regardless of the active tool, so
            // requiring it filters them out. (In-game verification tracked in docs.)
            if (!(_toolSystem.activeTool is ObjectToolSystem)) return;

            NativeArray<Entity> entities = _createdObjects.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    Transform transform = EntityManager.GetComponentData<Transform>(entity);

                    // Skip objects we just realized from a remote command — don't echo them.
                    if (_guard.Consume(ReplicationGuard.Key(name, transform.m_Position), now)) continue;

                    // A net object (roundabout island, turn-restriction sign) is inert without its
                    // parent: the ring and the restriction are derived from the parent's sub-objects,
                    // never from the object's transform. AttachSystem resolved the parent by now.
                    var attachKind = ObjectAttachKind.None;
                    bool isNode;
                    Unity.Mathematics.float3 attachPos;
                    if (NetAttachment.TryGetAttachment(EntityManager, entity, out isNode, out attachPos))
                        attachKind = isNode ? ObjectAttachKind.NetNode : ObjectAttachKind.NetEdge;

                    var command = new ObjectPlacementCommand
                    {
                        PrefabName = name,
                        PosX = transform.m_Position.x,
                        PosY = transform.m_Position.y,
                        PosZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x,
                        RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z,
                        RotW = transform.m_Rotation.value.w,
                        AttachKind = attachKind,
                        AttachX = attachPos.x,
                        AttachY = attachPos.y,
                        AttachZ = attachPos.z,
                    };
                    session.SendCommand(0, ObjectPlacementCommand.Id, command.Encode());
                    RecordDiagnostic(name);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }



        /// <summary>Routes received object-placement commands (sim thread) into the queue.</summary>
    }
}
