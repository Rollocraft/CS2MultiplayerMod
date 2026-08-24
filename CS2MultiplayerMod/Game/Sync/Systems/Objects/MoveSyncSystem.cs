using System.Collections.Concurrent;
using Game;
using Game.Buildings;
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
    /// Replicates relocations. A simple free-standing object moves through one relocate definition;
    /// anything with owned geometry, a transport lifecycle, or a net attachment is re-derived on the
    /// receiver by the game's own definition generator from the same inputs the move tool had.
    /// </summary>
    public partial class MoveSyncSystem : GameSystemBase
    {
        private const long MoveRetryWindowMs = 10000;
        public bool DeferForTerrain;
        private readonly ConcurrentQueue<SimulationCommandMessage> _incoming =
            new ConcurrentQueue<SimulationCommandMessage>();
        private readonly ReplicationGuard _guard = new ReplicationGuard();

        private PrefabSystem _prefabSystem;
        private PrefabIndex _prefabIndex;
        private EntityQuery _movedObjects;
        private ObjectSearch _objectSearch;
        private CommandObserver _observer;
        private bool _hasBlockedMove;
        private SimulationCommandMessage _blockedMove;
        private long _blockedMoveDeadline;

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

            // A blocked move re-runs FindAt every frame until its retry window closes; that lookup
            // goes through the game's object search tree, not a query over the object domain.
            _objectSearch = new ObjectSearch(
                World.GetOrCreateSystemManaged<global::Game.Objects.SearchSystem>());

            if (Mod.Service != null)
            {
                _observer = new CommandObserver(_incoming, ObjectMoveCommand.Id);
                Mod.Service.Session.AddObserver(_observer);
            }
            SyncInbox.RegisterDrain(DrainQueue);
        }

        protected override void OnDestroy()
        {
            SyncInbox.UnregisterDrain(DrainQueue);
            if (_observer != null && Mod.Service != null)
                Mod.Service.Session.RemoveObserver(_observer);
            base.OnDestroy();
        }

        private void DrainQueue()
        {
            SyncInbox.Clear(_incoming);
            _hasBlockedMove = false;
            _blockedMove = null;
            _blockedMoveDeadline = 0;
            DeferForTerrain = false;
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
            if (DeferForTerrain) return;
            Net.NetSyncSystem coordinator = World.GetOrCreateSystemManaged<Net.NetSyncSystem>();
            if (!coordinator.CanBuildDefinitions) return;
            RealizeIncoming(session, service.NowMs);
        }

        private void CaptureMoves(MultiplayerSession session, long now)
        {
            BuildSyncSystem buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            if (buildSync.NativeLifecycleCapturedThisFrame ||
                World.GetOrCreateSystemManaged<Net.NetSyncSystem>().DidCommitObjectGraphThisFrame) return;
            if (_movedObjects.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _movedObjects.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    float3 oldPos = EntityManager.GetComponentData<MovedLocation>(entity).m_OldPosition;
                    Transform transform = EntityManager.GetComponentData<Transform>(entity);

                    // No actual displacement → an unrelated Updated on a once-moved object.
                    if (math.distancesq(oldPos, transform.m_Position) < 0.01f) continue;
                    if (_guard.Consume(MoveKey(name, transform.m_Position), now)) continue;

                    // A building's lot, driveways and installed upgrades move with it, and the move
                    // tool carries them as explicit definitions rather than re-deriving them. The
                    // receiver reproduces that by re-running the game's own generator over the same
                    // inputs, so the whole owned graph follows from prefab + old position + new
                    // transform + snapped parent - no need to ship the sender's several-hundred-
                    // definition batch.
                    float elevation = EntityManager.HasComponent<Elevation>(entity)
                        ? EntityManager.GetComponentData<Elevation>(entity).m_Elevation
                        : 0f;
                    var command = new ObjectMoveCommand
                    {
                        PrefabName = name,
                        OldX = oldPos.x, OldY = oldPos.y, OldZ = oldPos.z,
                        NewX = transform.m_Position.x, NewY = transform.m_Position.y, NewZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x, RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z, RotW = transform.m_Rotation.value.w,
                        Elevation = elevation,
                        ToolRandomSeed = buildSync.AppliedLifecycleToolSeed,
                    };
                    CaptureFinalEntityIdentity(command, entity, prefab, oldPos);
                    if (HasOwnedLifecycle(entity, prefab) &&
                        !command.DestinationAttachmentKnown)
                    {
                        Mod.log.Warn("[MP] MoveSync: final-entity fallback for '" + name +
                                     "' could not recover the applied road attachment; skipping " +
                                     "the unsafe partial move.");
                        Diagnostics.FlightRecorder.Note("relocation fallback lacked attachment prefab=" +
                                                        name);
                        continue;
                    }
                    session.SendCommand(0, ObjectMoveCommand.Id, command.Encode());
                    Mod.Verbose("[MP] MoveSync captured relocation of '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Publish a relocation observed in the applying tool's own definitions (see
        /// <c>BuildSyncSystem.CaptureLocalRelocationForApply</c>). That is the reliable signal: the
        /// apply pass records no "came from" marker on the moved entity itself.
        /// </summary>
        public void PublishLocalRelocation(Entity prefab, Entity original, float3 oldPosition,
            float3 newPosition, quaternion rotation, float elevation, uint toolSeed,
            Entity destinationParent, bool destinationAttachmentKnown)
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            if (math.distancesq(oldPosition, newPosition) < 0.01f) return;

            string name = _prefabSystem.GetPrefabName(prefab);
            if (string.IsNullOrEmpty(name)) return;

            var command = new ObjectMoveCommand
            {
                PrefabName = name,
                OldX = oldPosition.x, OldY = oldPosition.y, OldZ = oldPosition.z,
                NewX = newPosition.x, NewY = newPosition.y, NewZ = newPosition.z,
                RotX = rotation.value.x, RotY = rotation.value.y,
                RotZ = rotation.value.z, RotW = rotation.value.w,
                Elevation = elevation,
                ToolRandomSeed = toolSeed,
            };
            CaptureOriginalIdentity(command, original);
            if (!CaptureOwnerIdentity(command, original))
            {
                // An owned upgrade is found on the peer through its host. Without that identity the
                // move would name an object the peer cannot look up, so drop it here instead.
                Mod.log.Warn("[MP] MoveSync: relocation of owned '" + name +
                             "' could not describe its host building; skipping this move.");
                Diagnostics.FlightRecorder.Note("relocation host identity unavailable prefab=" + name);
                return;
            }
            CaptureSourceAttachment(command, original);
            if (!CaptureDestinationAttachment(command, destinationParent, newPosition,
                    destinationAttachmentKnown))
            {
                Diagnostics.FlightRecorder.Note("relocation destination attachment could not be encoded");
                return;
            }
            // Also stops the MovedLocation sweep below from sending this same move again. Mark only
            // once encoding succeeded so the final-entity fallback remains available on failure.
            _guard.Mark(MoveKey(name, newPosition), service.NowMs);
            service.Session.SendCommand(0, ObjectMoveCommand.Id, command.Encode());
            Mod.Verbose("[MP] MoveSync captured relocation of '" + name + "' from the tool definition.");
            Diagnostics.FlightRecorder.Note("relocation captured prefab=" + name +
                " seed=" + toolSeed);
        }

        private void RealizeIncoming(MultiplayerSession session, long now)
        {
            if (_hasBlockedMove)
            {
                if (!TryRealizeMove(_blockedMove, now))
                {
                    if (now < _blockedMoveDeadline) return;
                    // The object to relocate never arrived here. Drop the relocation rather than
                    // loop the whole world through recovery (which re-failed every reload).
                    Mod.log.Warn("[MP] MoveSync: relocation target did not resolve within the retry " +
                                 "window; dropping this move (use /sync if the city drifts).");
                    Diagnostics.FlightRecorder.Note("move dropped after retry window");
                    _hasBlockedMove = false;
                    _blockedMove = null;
                    return;
                }
                _hasBlockedMove = false;
                _blockedMove = null;
            }

            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (TryRealizeMove(message, now)) continue;
                _hasBlockedMove = true;
                _blockedMove = message;
                _blockedMoveDeadline = now + MoveRetryWindowMs;
                Diagnostics.FlightRecorder.Note("move target retrying");
                return;
            }
        }

        private bool TryRealizeMove(SimulationCommandMessage message, long now)
        {
            ObjectMoveCommand command;
            try { command = ObjectMoveCommand.Decode(message.Body); }
            catch (System.Exception ex)
            {
                // A malformed peer command is not local corruption; drop it, do not resync.
                Mod.log.Warn("[MP] MoveSync: dropping malformed command: " + ex.Message);
                return true;
            }

            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName,
                    candidate => EntityManager.HasComponent<ObjectData>(candidate),
                    out prefab)) return false;

            // An owned relocation is anchored on its host: the upgrade may not have realized here
            // yet, and once it has, its host is what tells two identical upgrades apart.
            Entity host = Entity.Null;
            if (command.HasOwnerIdentity && !TryResolveOwner(command, out host)) return false;

            var oldPos = new float3(command.OldX, command.OldY, command.OldZ);
            var newPos = new float3(command.NewX, command.NewY, command.NewZ);
            BuildSyncSystem buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            Entity sourceParent;
            if (!TryResolveAttachment(buildSync, command.SourceAttachmentKnown,
                    command.SourceAttachKind,
                    new float3(command.SourceAttachX, command.SourceAttachY,
                        command.SourceAttachZ), out sourceParent))
                return false;
            Entity destinationParent;
            if (!TryResolveAttachment(buildSync, command.DestinationAttachmentKnown,
                    command.DestinationAttachKind,
                    new float3(command.DestinationAttachX, command.DestinationAttachY,
                        command.DestinationAttachZ), out destinationParent))
                return false;

            Entity original = FindAt(prefab, oldPos, command.HasOriginalRandomSeed,
                command.OriginalRandomSeed,
                command.SourceAttachmentKnown && command.SourceAttachKind != ObjectAttachKind.None,
                sourceParent, host);
            if (original == Entity.Null)
            {
                // A reliable replay may arrive after this same move already committed.
                if (FindAt(prefab, newPos, command.HasOriginalRandomSeed,
                        command.OriginalRandomSeed,
                        command.DestinationAttachmentKnown &&
                        command.DestinationAttachKind != ObjectAttachKind.None,
                        destinationParent, host) != Entity.Null) return true;
                return false;
            }
            var rotation = new quaternion(command.RotX, command.RotY, command.RotZ, command.RotW);
            bool requiresCompleteLifecycle = RequiresCompleteLifecycle(original, prefab, command);
            if (requiresCompleteLifecycle && !command.DestinationAttachmentKnown)
            {
                Mod.log.Warn("[MP] MoveSync: relocation of '" + command.PrefabName +
                             "' lacks an authoritative destination attachment; dropping it instead " +
                             "of detaching its owned/roadside graph.");
                return true;
            }

            if (requiresCompleteLifecycle)
            {
                // Native derivation is required for small attached objects such as mailboxes too:
                // their route lanes and PathTargetMoved event are part of the normal transaction.
                SimulationCommandMessage retained = message;
                BuildSyncSystem.NativeDeriveResult derived = buildSync.TryDeriveObjectTransaction(
                    prefab, Entity.Null, original, destinationParent, newPos, rotation,
                    command.Elevation, command.ToolRandomSeed, "move " + command.PrefabName,
                    () => _incoming.Enqueue(retained), null);
                if (derived == BuildSyncSystem.NativeDeriveResult.Busy) return false;
                if (derived == BuildSyncSystem.NativeDeriveResult.Armed)
                {
                    _guard.Mark(MoveKey(command.PrefabName, newPos), now);
                    Mod.Verbose("[MP] MoveSync realize: derived relocation of '" +
                                command.PrefabName + "' from player " +
                                message.OriginPlayerId + ".");
                    return true;
                }
                if (derived == BuildSyncSystem.NativeDeriveResult.Failed) return true;

                // A root-only compatibility move would strand an owned graph or bypass attachment /
                // transport-stop lifecycle events, so unsupported native derivation is a hard stop.
                Mod.log.Warn("[MP] MoveSync: relocation of '" + command.PrefabName +
                             "' needs the game's object lifecycle generator; dropping this move.");
                return true;
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
                        m_RandomSeed = command.HasOriginalRandomSeed
                            ? command.OriginalRandomSeed
                            : 0,
                        m_Flags = CreationFlags.Permanent | CreationFlags.Relocate,
                    });
                    EntityManager.AddComponentData(definition, new ObjectDefinition
                    {
                        m_ParentMesh = -1,
                        m_Position = newPos,
                        m_Rotation = rotation,
                        m_LocalPosition = newPos,
                        m_LocalRotation = rotation,
                        m_Scale = new float3(1f, 1f, 1f),
                        m_Intensity = 1f,
                        m_Probability = 100,
                        m_PrefabSubIndex = -1,
                        m_Elevation = command.Elevation,
                    });
                    EntityManager.AddComponent<Updated>(definition);
                    EntityManager.AddComponent<Deleted>(definition);
                Mod.Verbose("[MP] MoveSync realize: moved '" + command.PrefabName + "' from player " +
                             message.OriginPlayerId + " to (" + newPos.x.ToString("F1") + "," +
                             newPos.z.ToString("F1") + ").");
            }
            catch (System.Exception ex)
            {
                // The definition was rejected before commit; drop this move rather than freeze
                // the world (the placer can /sync if the object looks out of place).
                Mod.log.Error("[MP] MoveSync realize FAILED for '" + command.PrefabName +
                              "'; dropping this move: " + ex);
                Diagnostics.FlightRecorder.Note("move realize failed; dropped");
            }
            return true;
        }

        private Entity FindAt(Entity prefab, float3 position, bool hasRandomSeed,
            int randomSeed, bool requireAttachment, Entity attachmentParent, Entity expectedOwner)
        {
            Entity best = Entity.Null;
            Entity bestSeedMatch = Entity.Null;
            float bestDistanceSq = float.MaxValue;
            float bestSeedDistanceSq = float.MaxValue;
            // The distance test below rejects past 2 m, so the tree only has to be asked about
            // that neighbourhood. The tree carries owned sub-objects the old query excluded, so
            // the Owner/Edge/liveness filtering that query did moves into the loop.
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                _objectSearch.CollectNear(position, FindRadius, candidates);
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!IsMoveCandidate(candidate, expectedOwner)) continue;
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab) continue;
                    if (requireAttachment &&
                        NetAttachment.GetNetParent(EntityManager, candidate) != attachmentParent)
                        continue;

                    float3 pos = EntityManager.GetComponentData<Transform>(candidate).m_Position;
                    float distanceSq = math.distancesq(pos, position);
                    if (distanceSq > FindRadius * FindRadius) continue;

                    if (distanceSq < bestDistanceSq)
                    {
                        best = candidate;
                        bestDistanceSq = distanceSq;
                    }

                    if (!hasRandomSeed ||
                        !EntityManager.HasComponent<PseudoRandomSeed>(candidate) ||
                        EntityManager.GetComponentData<PseudoRandomSeed>(candidate).m_Seed != randomSeed ||
                        distanceSq >= bestSeedDistanceSq) continue;
                    bestSeedMatch = candidate;
                    bestSeedDistanceSq = distanceSq;
                }
            }
            finally
            {
                candidates.Dispose();
            }
            // Seed is a strong discriminator for adjacent identical props, but position remains a
            // compatibility fallback for old/save-created entities whose seed drifted historically.
            return bestSeedMatch != Entity.Null ? bestSeedMatch : best;
        }

        /// <summary>How far a relocation's endpoint may sit from the entity it names (metres).</summary>
        private const float FindRadius = 2f;

        /// <summary>
        /// The live objects a relocation may name. Free-standing moves accept top-level objects
        /// only; an owned move accepts exactly the objects the named host owns, which is what keeps
        /// a neighbouring building's identical upgrade out of the candidate set.
        /// </summary>
        private bool IsMoveCandidate(Entity entity, Entity expectedOwner)
        {
            if (!EntityManager.Exists(entity)) return false;
            if (EntityManager.HasComponent<Temp>(entity) ||
                EntityManager.HasComponent<Deleted>(entity) ||
                EntityManager.HasComponent<global::Game.Net.Edge>(entity)) return false;
            if (expectedOwner == Entity.Null)
            {
                if (EntityManager.HasComponent<Owner>(entity)) return false;
            }
            else if (!EntityManager.HasComponent<Owner>(entity) ||
                     EntityManager.GetComponentData<Owner>(entity).m_Owner != expectedOwner)
            {
                return false;
            }
            return EntityManager.HasComponent<PrefabRef>(entity) &&
                   EntityManager.HasComponent<Transform>(entity);
        }

        /// <summary>
        /// Find the host named by an owned relocation. False means it is not here (yet), which the
        /// caller treats as "retry", not as a bad command.
        /// </summary>
        private bool TryResolveOwner(ObjectMoveCommand command, out Entity owner)
        {
            owner = Entity.Null;
            Entity ownerPrefab;
            if (!_prefabIndex.TryResolve(command.OwnerPrefabName,
                    candidate => EntityManager.HasComponent<ObjectData>(candidate),
                    out ownerPrefab)) return false;

            var ownerPosition = new float3(command.OwnerX, command.OwnerY, command.OwnerZ);
            var candidates = new NativeList<Entity>(16, Allocator.Temp);
            try
            {
                _objectSearch.CollectNear(ownerPosition, FindRadius, candidates);
                float bestDistanceSq = FindRadius * FindRadius;
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!EntityManager.Exists(candidate) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate) ||
                        !EntityManager.HasComponent<PrefabRef>(candidate) ||
                        !EntityManager.HasComponent<Transform>(candidate)) continue;
                    if (EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != ownerPrefab)
                        continue;

                    float distanceSq = math.distancesq(
                        EntityManager.GetComponentData<Transform>(candidate).m_Position,
                        ownerPosition);
                    if (distanceSq > bestDistanceSq) continue;
                    bestDistanceSq = distanceSq;
                    owner = candidate;
                }
            }
            finally
            {
                candidates.Dispose();
            }
            return owner != Entity.Null;
        }

        private static bool TryResolveAttachment(BuildSyncSystem buildSync, bool known,
            ObjectAttachKind kind, float3 anchor, out Entity parent)
        {
            parent = Entity.Null;
            if (!known || kind == ObjectAttachKind.None) return true;
            parent = buildSync.ResolveNetAttachment(kind, anchor);
            return parent != Entity.Null;
        }

        private bool RequiresCompleteLifecycle(Entity entity, Entity prefab,
            ObjectMoveCommand command)
        {
            // Moving an installed upgrade re-commits its host, re-lays the host's sub-nets around
            // the vacated and newly covered ground, and re-derives the host's road junction. A
            // root-only move would slide the upgrade off its driveways.
            return command.HasOwnerIdentity ||
                   HasOwnedLifecycle(entity, prefab) ||
                   (command.SourceAttachmentKnown &&
                    command.SourceAttachKind != ObjectAttachKind.None) ||
                   (command.DestinationAttachmentKnown &&
                    command.DestinationAttachKind != ObjectAttachKind.None);
        }

        private bool HasOwnedLifecycle(Entity entity, Entity prefab)
        {
            return EntityManager.HasComponent<Building>(entity) ||
                   EntityManager.HasComponent<global::Game.Objects.Attached>(entity) ||
                   EntityManager.HasComponent<global::Game.Routes.TransportStop>(entity) ||
                   EntityManager.HasComponent<TransportStopData>(prefab) ||
                   EntityManager.HasBuffer<global::Game.Buildings.InstalledUpgrade>(entity) ||
                   EntityManager.HasBuffer<global::Game.Objects.SubObject>(entity) ||
                   EntityManager.HasBuffer<global::Game.Net.SubNet>(entity) ||
                   EntityManager.HasBuffer<global::Game.Areas.SubArea>(entity);
        }

        private void CaptureOriginalIdentity(ObjectMoveCommand command, Entity original)
        {
            if (!EntityManager.HasComponent<PseudoRandomSeed>(original)) return;
            command.HasOriginalRandomSeed = true;
            command.OriginalRandomSeed = EntityManager.GetComponentData<PseudoRandomSeed>(original).m_Seed;
        }

        /// <summary>
        /// Describe the host of an owned relocation. Relocating an installed upgrade or sub-building
        /// from a building's upgrade list is the base game's only relocation, and it moves an owned
        /// entity: the peer finds it through its host, since a free-standing lookup by position can
        /// answer with a neighbouring building's identical upgrade. False means the object is owned
        /// but its host cannot be described - the caller must not publish a move nobody can resolve.
        /// </summary>
        private bool CaptureOwnerIdentity(ObjectMoveCommand command, Entity original)
        {
            if (!EntityManager.HasComponent<Owner>(original)) return true;

            Entity owner = EntityManager.GetComponentData<Owner>(original).m_Owner;
            if (owner == Entity.Null || !EntityManager.Exists(owner) ||
                !EntityManager.HasComponent<PrefabRef>(owner) ||
                !EntityManager.HasComponent<Transform>(owner)) return false;

            string ownerName = _prefabSystem.GetPrefabName(
                EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab);
            if (string.IsNullOrEmpty(ownerName)) return false;

            float3 ownerPosition = EntityManager.GetComponentData<Transform>(owner).m_Position;
            command.HasOwnerIdentity = true;
            command.OwnerPrefabName = ownerName;
            command.OwnerX = ownerPosition.x;
            command.OwnerY = ownerPosition.y;
            command.OwnerZ = ownerPosition.z;
            return true;
        }

        private void CaptureSourceAttachment(ObjectMoveCommand command, Entity original)
        {
            if (!EntityManager.HasComponent<global::Game.Objects.Attached>(original))
            {
                command.SourceAttachmentKnown = true;
                command.SourceAttachKind = ObjectAttachKind.None;
                return;
            }

            global::Game.Objects.Attached attached =
                EntityManager.GetComponentData<global::Game.Objects.Attached>(original);
            if (attached.m_Parent == original)
            {
                command.SourceAttachmentKnown = true;
                command.SourceAttachKind = ObjectAttachKind.None;
                return;
            }

            bool isNode;
            float3 anchor;
            if (!NetAttachment.TryGetAttachment(EntityManager, original, out isNode, out anchor))
                return;
            SetSourceAttachment(command, isNode, anchor);
        }

        private bool CaptureDestinationAttachment(ObjectMoveCommand command, Entity parent,
            float3 newPosition, bool known)
        {
            command.DestinationAttachmentKnown = known;
            if (!known) return true;
            if (parent == Entity.Null)
            {
                command.DestinationAttachKind = ObjectAttachKind.None;
                return true;
            }

            bool isNode;
            float3 anchor;
            if (!NetAttachment.TryDescribeParent(EntityManager, parent, newPosition,
                    out isNode, out anchor))
            {
                command.DestinationAttachmentKnown = false;
                return false;
            }
            SetDestinationAttachment(command, isNode, anchor);
            return true;
        }

        private void CaptureFinalEntityIdentity(ObjectMoveCommand command, Entity original,
            Entity prefab, float3 oldPosition)
        {
            CaptureOriginalIdentity(command, original);
            if (!EntityManager.HasComponent<global::Game.Objects.Attached>(original))
            {
                // For complex objects the final entity does not retain the snapped road control
                // point, so pretending "None" would detach it. Their pre-apply capture supplies it.
                if (!HasOwnedLifecycle(original, prefab))
                {
                    command.SourceAttachmentKnown = true;
                    command.SourceAttachKind = ObjectAttachKind.None;
                    command.DestinationAttachmentKnown = true;
                    command.DestinationAttachKind = ObjectAttachKind.None;
                }
                return;
            }

            global::Game.Objects.Attached attached =
                EntityManager.GetComponentData<global::Game.Objects.Attached>(original);
            bool isNode;
            float3 anchor;
            if (attached.m_Parent == original)
            {
                command.DestinationAttachmentKnown = true;
                command.DestinationAttachKind = ObjectAttachKind.None;
            }
            else if (NetAttachment.TryGetAttachment(EntityManager, original,
                         out isNode, out anchor))
            {
                SetDestinationAttachment(command, isNode, anchor);
            }

            if (NetAttachment.TryDescribeParent(EntityManager, attached.m_OldParent, oldPosition,
                    out isNode, out anchor))
                SetSourceAttachment(command, isNode, anchor);
        }

        private static void SetSourceAttachment(ObjectMoveCommand command, bool isNode,
            float3 anchor)
        {
            command.SourceAttachmentKnown = true;
            command.SourceAttachKind = isNode ? ObjectAttachKind.NetNode : ObjectAttachKind.NetEdge;
            command.SourceAttachX = anchor.x;
            command.SourceAttachY = anchor.y;
            command.SourceAttachZ = anchor.z;
        }

        private static void SetDestinationAttachment(ObjectMoveCommand command, bool isNode,
            float3 anchor)
        {
            command.DestinationAttachmentKnown = true;
            command.DestinationAttachKind = isNode ? ObjectAttachKind.NetNode : ObjectAttachKind.NetEdge;
            command.DestinationAttachX = anchor.x;
            command.DestinationAttachY = anchor.y;
            command.DestinationAttachZ = anchor.z;
        }

        private static string MoveKey(string prefabName, float3 newPosition) =>
            "mov|" + ReplicationGuard.Key(prefabName, newPosition);

    }
}
