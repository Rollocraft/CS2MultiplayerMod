using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        private const long NativeObjectTargetRetryMs = 10000;
        private const long NativeObjectReplayRememberMs = 60000;
        private const int MaxNativeObjectReplayPrefix = 32;

        private struct NativeObjectOperationKey : System.IEquatable<NativeObjectOperationKey>
        {
            public int Origin;
            public long Operation;
            public bool Equals(NativeObjectOperationKey other) =>
                Origin == other.Origin && Operation == other.Operation;
            public override bool Equals(object obj) =>
                obj is NativeObjectOperationKey && Equals((NativeObjectOperationKey)obj);
            public override int GetHashCode()
            {
                unchecked { return Origin * 397 ^ Operation.GetHashCode(); }
            }
        }

        private enum NativeObjectResult : byte { Completed, Armed, Retry, Rejected }

        private sealed class ResolvedObjectDefinition
        {
            public Entity Prefab;
            public Entity SubPrefab;
            public Entity Original;
            public Entity Owner;
            public Entity Attached;
            public Entity OwnerDefinitionPrefab;
            public Entity StartEntity;
            public Entity EndEntity;
        }

        /// <summary>
        /// Spacing between attempts on a blocked operation. Resolution is cheap now but not free, and
        /// the geometry it waits for arrives on its own schedule - retrying every frame only burned
        /// the retry window at frame rate.
        /// </summary>
        private const long NativeObjectRetryIntervalMs = 200;

        private bool _hasBlockedNativeObject;
        private SimulationCommandMessage _blockedNativeObject;
        private long _blockedNativeObjectDeadline;
        private long _blockedNativeObjectNextAttemptMs;
        private string _lastUnresolvedObjectReason;
        // Commit validation can reject an operation after it left the network inbox. Replays must
        // return ahead of later commands, and more than one can become ready while another ordered
        // target is retrying. A bounded prefix avoids the former single-slot collision/drop.
        private readonly List<SimulationCommandMessage> _nativeObjectReplayPrefix =
            new List<SimulationCommandMessage>(MaxNativeObjectReplayPrefix);
        private readonly CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NativeObjectOperationKey>
            _recentNativeObjectOperations =
                new CS2MultiplayerMod.Core.Sync.OperationReplayWindow<NativeObjectOperationKey>();
        private EntityQuery _portableObjects;
        private EntityQuery _portableAreas;
        private Net.NetSyncSystem _nativeNetCoordinator;

        /// <summary>
        /// Candidates for one resolution pass, bucketed by prefab.
        ///
        /// A relocation names every element of a building's owned graph plus a stretch of road - 280+
        /// references for a large plant. Walking the whole city's objects/nodes/edges/areas once per
        /// reference took seconds of main-thread time per attempt, and a blocked operation repeated
        /// that every frame for its whole retry window. Snapshotting each domain once and grouping by
        /// prefab turns those thousands of city walks into four.
        /// </summary>
        private sealed class PortableCandidateIndex
        {
            private readonly Dictionary<Entity, List<Entity>> _byPrefab =
                new Dictionary<Entity, List<Entity>>();
            private static readonly List<Entity> Empty = new List<Entity>();
            private bool _filled;

            public void Invalidate()
            {
                foreach (KeyValuePair<Entity, List<Entity>> pair in _byPrefab)
                    pair.Value.Clear();
                _filled = false;
            }

            public void FillIfNeeded(EntityManager entityManager, EntityQuery query)
            {
                if (_filled) return;
                if (query.IsEmptyIgnoreFilter)
                {
                    _filled = true;
                    return;
                }
                NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity candidate = entities[i];
                        if (!entityManager.HasComponent<PrefabRef>(candidate)) continue;
                        Entity prefab = entityManager.GetComponentData<PrefabRef>(candidate).m_Prefab;
                        List<Entity> bucket;
                        if (!_byPrefab.TryGetValue(prefab, out bucket))
                        {
                            bucket = new List<Entity>();
                            _byPrefab[prefab] = bucket;
                        }
                        bucket.Add(candidate);
                    }
                }
                finally { entities.Dispose(); }
                _filled = true;
            }

            public List<Entity> Of(Entity prefab)
            {
                List<Entity> bucket;
                return _byPrefab.TryGetValue(prefab, out bucket) ? bucket : Empty;
            }
        }

        private readonly PortableCandidateIndex _objectCandidates = new PortableCandidateIndex();
        private readonly PortableCandidateIndex _nodeCandidates = new PortableCandidateIndex();
        private readonly PortableCandidateIndex _edgeCandidates = new PortableCandidateIndex();
        private readonly PortableCandidateIndex _areaCandidates = new PortableCandidateIndex();
        private int _portableIndexDepth;

        /// <summary>
        /// Prepare candidate domains for one resolution pass. Each domain is snapshotted lazily on
        /// its first lookup, so a plain building placement does not walk unrelated nodes, edges, and
        /// areas. Nothing inside a pass creates or destroys world entities, so each snapshot stays
        /// correct throughout it.
        /// </summary>
        private void BeginPortableResolve()
        {
            if (_portableIndexDepth++ != 0) return;
            _objectCandidates.Invalidate();
            _nodeCandidates.Invalidate();
            _edgeCandidates.Invalidate();
            _areaCandidates.Invalidate();
        }

        private void EndPortableResolve()
        {
            if (_portableIndexDepth > 0) _portableIndexDepth--;
        }

        /// <summary>
        /// Same-prefab candidates for <paramref name="prefab"/>. Outside a resolution pass the domain
        /// is snapshotted for this one lookup, so callers that resolve a single reference behave
        /// exactly as before.
        /// </summary>
        private List<Entity> Candidates(PortableCandidateIndex index, EntityQuery query, Entity prefab)
        {
            if (_portableIndexDepth == 0) index.Invalidate();
            index.FillIfNeeded(EntityManager, query);
            return index.Of(prefab);
        }

        private void InitializeNativeObjectOperations()
        {
            _nativeNetCoordinator = World.GetOrCreateSystemManaged<Net.NetSyncSystem>();
            _portableObjects = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Objects.Object>(),
                    ComponentType.ReadOnly<global::Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<global::Game.Objects.Moving>(),
                    ComponentType.ReadOnly<global::Game.Vehicles.Vehicle>(),
                    ComponentType.ReadOnly<global::Game.Creatures.Creature>(),
                },
            });
            _portableAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<global::Game.Areas.Area>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<global::Game.Areas.Node>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                },
            });
        }

        private void DrainNativeObjectOperations()
        {
            _hasBlockedNativeObject = false;
            _blockedNativeObject = null;
            _blockedNativeObjectDeadline = 0;
            _blockedNativeObjectNextAttemptMs = 0;
            _lastUnresolvedObjectReason = null;
            _nativeObjectReplayPrefix.Clear();
            _recentNativeObjectOperations.Clear();
        }

        private void PruneNativeObjectOperations(long now)
        {
            _recentNativeObjectOperations.Prune(now);
        }

        private bool TryRealizeBlockedNativeObject(long now)
        {
            if (!_hasBlockedNativeObject) return true;
            if (_nativeNetCoordinator.IsCommitBusy) return false;
            if (now < _blockedNativeObjectNextAttemptMs) return false;
            _blockedNativeObjectNextAttemptMs = now + NativeObjectRetryIntervalMs;

            NativeObjectResult result = TryRealizeRemoteObjectMessage(_blockedNativeObject, now);
            if (result == NativeObjectResult.Retry)
            {
                if (now < _blockedNativeObjectDeadline) return false;
                string placementPrefab;
                bool compactPlacement = TryDescribeBlockedPlacement(out placementPrefab);
                // The road/building/area this edit references never arrived on this machine. A
                // placement should normally take the compact local-regeneration path; reaching this
                // deadline means either its one snapped target is absent or a legacy/edit graph is
                // incompatible. In both cases silently dropping it leaves known world divergence.
                if (compactPlacement)
                {
                    Mod.log.Warn("[MP] BuildSync: building placement '" + placementPrefab +
                                 "' could not resolve its snapped target within the retry window (" +
                                 (_lastUnresolvedObjectReason ?? "unknown target") +
                                 "); requesting an automatic world sync.");
                    Diagnostics.FlightRecorder.Note(
                        "building placement target expired; world sync requested");
                }
                else
                {
                    Mod.log.Warn("[MP] BuildSync: native object operation target did not resolve " +
                                 "within the retry window (" +
                                 (_lastUnresolvedObjectReason ?? "unknown target") +
                                 "); requesting an automatic world sync.");
                    Diagnostics.FlightRecorder.Note(
                        "object operation target expired; world sync requested");
                }
                _hasBlockedNativeObject = false;
                _blockedNativeObject = null;
                _blockedNativeObjectDeadline = 0;
                _lastUnresolvedObjectReason = null;
                SyncInbox.RequestResync(compactPlacement
                    ? "building placement target did not resolve"
                    : "native object operation target did not resolve");
                return false;
            }

            _hasBlockedNativeObject = false;
            _blockedNativeObject = null;
            _blockedNativeObjectDeadline = 0;
            return result == NativeObjectResult.Completed;
        }

        private bool TryDescribeBlockedPlacement(out string prefabName)
        {
            prefabName = null;
            if (_blockedNativeObject == null ||
                _blockedNativeObject.CommandId != ObjectToolOperationCommand.Id) return false;
            try
            {
                ObjectToolOperationCommand command =
                    ObjectToolOperationCommand.Decode(_blockedNativeObject.Body);
                if (!command.HasPlacementInput || command.IsAssetStamp) return false;
                prefabName = command.Definitions[command.RootIndex].PrefabName;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private void BlockNativeObject(SimulationCommandMessage message, long now)
        {
            _blockedNativeObject = message;
            _blockedNativeObjectDeadline = now + NativeObjectTargetRetryMs;
            _blockedNativeObjectNextAttemptMs = now + NativeObjectRetryIntervalMs;
            _hasBlockedNativeObject = true;
            Diagnostics.FlightRecorder.Note("object operation target retrying");
        }

        /// <summary>
        /// Route one remote object-domain message. Both shapes share the single ordered retry slot,
        /// so a stamp waiting for its prefab cannot be overtaken by a later placement.
        /// </summary>
        private NativeObjectResult TryRealizeRemoteObjectMessage(SimulationCommandMessage message,
            long now)
        {
            return message.CommandId == AssetStampCommand.Id
                ? TryRealizeAssetStamp(message, now)
                : TryRealizeNativeObject(message, now);
        }

        /// <summary>
        /// Rebuild a remote stamp by running the game's own definition generator over the inputs
        /// its tool had. The generator derives every shared endpoint from the prefab's own averaged
        /// node table, so the intersection's internal junctions are bit-identical here by
        /// construction - which is the only way the node generator will merge them.
        /// </summary>
        private NativeObjectResult TryRealizeAssetStamp(SimulationCommandMessage message, long now)
        {
            AssetStampCommand command;
            try { command = AssetStampCommand.Decode(message.Body); }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: dropping malformed asset-stamp command: " + ex.Message);
                Diagnostics.FlightRecorder.Note("asset stamp dropped malformed");
                return NativeObjectResult.Rejected;
            }

            var key = new NativeObjectOperationKey
            {
                Origin = message.OriginPlayerId,
                Operation = command.OperationId,
            };
            if (_recentNativeObjectOperations.Contains(key, now))
            {
                Diagnostics.FlightRecorder.Note("asset stamp duplicate suppressed op=" +
                                                  command.OperationId);
                return NativeObjectResult.Completed;
            }

            Entity prefab;
            if (!_prefabIndex.TryResolve(command.PrefabName,
                    candidate => EntityManager.Exists(candidate) &&
                                 EntityManager.HasComponent<AssetStampData>(candidate),
                    out prefab))
            {
                // A peer with content we lack. Nothing local will make this resolve, so do not hold
                // the ordered queue for it.
                RecordRefused(command.PrefabName);
                Mod.log.Warn("[MP] BuildSync: asset stamp '" + command.PrefabName +
                             "' is unavailable here; skipping.");
                Diagnostics.FlightRecorder.Note("asset stamp prefab unavailable");
                return NativeObjectResult.Rejected;
            }

            var position = new float3(command.PosX, command.PosY, command.PosZ);
            var rotation = new quaternion(command.RotX, command.RotY, command.RotZ, command.RotW);
            string prefabName = command.PrefabName;
            NativeDeriveResult derived = TryDeriveObjectTransaction(
                prefab, Entity.Null, Entity.Null, Entity.Null, position, rotation,
                command.Elevation, command.ToolRandomSeed,
                "stamp " + prefabName,
                () => ReplayNativeObject(message),
                () => CompleteAssetStamp(key, prefab, prefabName, now),
                stamping: true);

            switch (derived)
            {
                case NativeDeriveResult.Armed:
                    Diagnostics.FlightRecorder.Note("asset stamp derived op=" +
                        command.OperationId + " prefab=" + prefabName);
                    return NativeObjectResult.Armed;
                case NativeDeriveResult.Busy:
                    return NativeObjectResult.Retry;
                case NativeDeriveResult.Unsupported:
                    // This build cannot reach the generator, and a stamp has no reduced form that
                    // preserves its topology. A world reload is the only complete fallback.
                    Mod.log.Warn("[MP] BuildSync: the game's definition generator is not reachable; " +
                                 "the remote stamp '" + prefabName +
                                 "' was skipped and world recovery was requested.");
                    Diagnostics.FlightRecorder.Note("asset stamp unsupported; recovery requested");
                    SyncInbox.RequestResync("asset stamp generator unavailable");
                    return NativeObjectResult.Rejected;
                case NativeDeriveResult.Failed:
                    Diagnostics.FlightRecorder.Note("asset stamp derive failed; recovery requested");
                    SyncInbox.RequestResync("asset stamp generation failed");
                    return NativeObjectResult.Rejected;
                default:
                    return NativeObjectResult.Rejected;
            }
        }

        private void CompleteAssetStamp(NativeObjectOperationKey key, Entity prefab,
            string prefabName, long capturedNow)
        {
            long now = Mod.Service != null ? Mod.Service.NowMs : capturedNow;
            _recentNativeObjectOperations.Remember(key, now, NativeObjectReplayRememberMs);
            try
            {
                ConstructionCharger.ChargeObject(EntityManager, prefab, prefabName);
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: committed stamp charge failed: " + ex.Message);
            }
            Diagnostics.FlightRecorder.Note("asset stamp transaction committed/drained op=" +
                                              key.Operation);
        }

        private NativeObjectResult TryRealizeNativeObject(SimulationCommandMessage message, long now)
        {
            ObjectToolOperationCommand command;
            try { command = ObjectToolOperationCommand.Decode(message.Body); }
            catch (System.Exception ex)
            {
                // A malformed command from a peer is a protocol/peer problem, not local world
                // corruption. The decode guard already protected us; drop it, do not resync.
                Mod.log.Warn("[MP] BuildSync: dropping malformed native object operation: " + ex.Message);
                Diagnostics.FlightRecorder.Note("object operation dropped malformed");
                return NativeObjectResult.Rejected;
            }

            // Permanent is local definition execution policy, not portable operation intent. New
            // senders remove it during capture; normalize again here so an older or hostile peer
            // cannot bypass the isolated Temp/apply/drain transaction or turn a resolvable command
            // into an impossible ten-second retry.
            int normalizedPermanentFlags = NormalizeRemoteObjectCreationFlags(command);
            if (normalizedPermanentFlags > 0)
                Diagnostics.FlightRecorder.Note("object operation normalized permanent flags=" +
                                                  normalizedPermanentFlags);

            string unsafePrefab;
            if (TryFindUnsafeSimulationReference(command, out unsafePrefab))
            {
                RecordRefused(unsafePrefab);
                Diagnostics.FlightRecorder.Note("object operation dropped (simulation-only prefab)");
                return NativeObjectResult.Rejected;
            }

            var key = new NativeObjectOperationKey
            {
                Origin = message.OriginPlayerId,
                Operation = command.OperationId,
            };
            if (_recentNativeObjectOperations.Contains(key, now))
            {
                Diagnostics.FlightRecorder.Note("object operation duplicate suppressed op=" +
                                                  command.OperationId);
                return NativeObjectResult.Completed;
            }

            NativeObjectResult placementResult;
            if (TryRealizePlacementInput(message, command, key, now, out placementResult))
                return placementResult;

            ResolvedObjectDefinition[] resolved;
            string reason;
            bool equivalentExists;
            int resolveStartTick = System.Environment.TickCount;
            BeginPortableResolve();
            try
            {
                if (!TryResolveObjectOperation(command, out resolved, out reason))
                {
                    // One line per attempt would be hundreds while an operation waits out its retry
                    // window; the reason only changes when the world does.
                    if (reason != _lastUnresolvedObjectReason)
                    {
                        _lastUnresolvedObjectReason = reason;
                        Diagnostics.FlightRecorder.Note("object operation unresolved op=" +
                                                          command.OperationId + " (" + reason + ")");
                    }
                    return NativeObjectResult.Retry;
                }
                _lastUnresolvedObjectReason = null;
                equivalentExists = EquivalentObjectOperationAlreadyExists(command, resolved);
            }
            finally { EndPortableResolve(); }

            if (equivalentExists)
            {
                _recentNativeObjectOperations.Remember(key, now, NativeObjectReplayRememberMs);
                Diagnostics.FlightRecorder.Note("object equivalent placement suppressed op=" +
                                                  command.OperationId);
                return NativeObjectResult.Completed;
            }

            if (!_nativeNetCoordinator.CanBuildDefinitions) return NativeObjectResult.Retry;
            int isolateStartTick = System.Environment.TickCount;
            _nativeNetCoordinator.PrepareDefinitionFrame();
            int generateStartTick = System.Environment.TickCount;

            var created = new List<Entity>(command.Definitions.Length);
            try
            {
                for (int i = 0; i < command.Definitions.Length; i++)
                    created.Add(CreateObjectToolDefinition(command.Definitions[i], resolved[i]));
            }
            catch (System.Exception ex)
            {
                // The partial definitions are torn down here, so nothing inconsistent was committed.
                // The operation nevertheless exists on the sender, so repair the known divergence.
                DestroyDefinitions(created);
                Mod.log.Warn("[MP] BuildSync: native object definitions could not be generated; " +
                             "requesting world recovery: " + ex.Message);
                Diagnostics.FlightRecorder.Note("object definitions failed=" + ex.GetType().Name +
                                                  "; recovery requested");
                SyncInbox.RequestResync("native object definitions could not be generated");
                return NativeObjectResult.Rejected;
            }

            SimulationCommandMessage retained = message;
            bool armed = _nativeNetCoordinator.ArmObjectCommit(
                () => ReplayNativeObject(retained),
                () => CompleteNativeObject(key, command, resolved, now),
                "native op=" + command.OperationId + " defs=" + command.Definitions.Length,
                command.IsAssetStamp,
                CollectOwnerDefinitions(command, resolved));
            if (!armed)
            {
                DestroyDefinitions(created);
                return NativeObjectResult.Retry;
            }
            RememberPlayerPlacedSpawnables(command, now);

            // Per-phase cost of one native operation. A big relocation is inherently a large
            // transaction; these numbers say which phase is actually spiking rather than leaving it
            // to guesswork.
            Diagnostics.FlightRecorder.Note("object definitions generated op=" + command.OperationId +
                " defs=" + created.Count +
                " resolveMS=" + (isolateStartTick - resolveStartTick) +
                " isolateMS=" + (generateStartTick - isolateStartTick) +
                " generateMS=" + (System.Environment.TickCount - generateStartTick));
            return NativeObjectResult.Armed;
        }

        /// <summary>
        /// The distinct owners this batch describes by prefab and transform instead of by entity.
        /// Native generation leaves such a sub-element's owner unset for a later spatial pass to
        /// fill in; retaining the descriptions lets the commit validator repair a link that pass
        /// missed. A batch normally describes exactly one owner - all of one placement's sub-nets,
        /// sub-areas and sub-objects name the same root.
        /// </summary>
        private List<Net.NetSyncSystem.ArmedOwnerDefinition> CollectOwnerDefinitions(
            ObjectToolOperationCommand command, ResolvedObjectDefinition[] resolved)
        {
            List<Net.NetSyncSystem.ArmedOwnerDefinition> owners = null;
            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = command.Definitions[i];
                if (!definition.HasOwnerDefinition) continue;
                Entity prefab = resolved[i].OwnerDefinitionPrefab;
                if (prefab == Entity.Null) continue;

                var described = new Net.NetSyncSystem.ArmedOwnerDefinition
                {
                    Prefab = prefab,
                    Position = new float3(definition.OwnerDefinitionX,
                        definition.OwnerDefinitionY, definition.OwnerDefinitionZ),
                };
                if (owners == null) owners = new List<Net.NetSyncSystem.ArmedOwnerDefinition>();
                bool known = false;
                for (int j = 0; j < owners.Count && !known; j++)
                    known = owners[j].Prefab == described.Prefab &&
                            owners[j].Position.Equals(described.Position);
                if (!known) owners.Add(described);
            }
            return owners;
        }

        /// <summary>
        /// Re-run a rooted placement from the object tool's inputs. A finished service-building or
        /// specialized-industry
        /// batch also contains road-alignment and driveway definitions which identify the sender's
        /// exact edge subdivision. Resolving those definitions one-for-one is impossible when the
        /// receiver has an equivalent road split into different entities; regenerating the batch
        /// from the snapped local edge avoids that accidental dependency.
        /// </summary>
        private bool TryRealizePlacementInput(SimulationCommandMessage message,
            ObjectToolOperationCommand command, NativeObjectOperationKey key, long now,
            out NativeObjectResult result)
        {
            result = NativeObjectResult.Rejected;
            if (!command.HasPlacementInput) return false;

            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            Entity prefab;
            if (!_prefabIndex.TryResolve(root.PrefabName,
                    candidate => ValidateDefinitionPrefab(ObjectToolDefinitionKind.Object, candidate),
                    out prefab))
            {
                _lastUnresolvedObjectReason = "placement prefab '" + root.PrefabName +
                                              "' is unavailable or incompatible";
                result = NativeObjectResult.Retry;
                return true;
            }

            Entity attachmentTarget;
            BeginPortableResolve();
            try
            {
                if (!TryResolvePortableRef(command.PlacementTarget, out attachmentTarget))
                {
                    _lastUnresolvedObjectReason = "placement snap target is not present";
                    result = NativeObjectResult.Retry;
                    return true;
                }
            }
            finally { EndPortableResolve(); }
            _lastUnresolvedObjectReason = null;

            var resolved = new ResolvedObjectDefinition[command.Definitions.Length];
            for (int i = 0; i < resolved.Length; i++) resolved[i] = new ResolvedObjectDefinition();
            resolved[command.RootIndex].Prefab = prefab;
            resolved[command.RootIndex].Attached = attachmentTarget;
            if (EquivalentObjectOperationAlreadyExists(command, resolved))
            {
                _recentNativeObjectOperations.Remember(key, now, NativeObjectReplayRememberMs);
                Diagnostics.FlightRecorder.Note("derived placement equivalent suppressed op=" +
                                                  command.OperationId);
                result = NativeObjectResult.Completed;
                return true;
            }

            ObjectDefinitionIntent placement = root.Object;
            var position = new float3(placement.PosX, placement.PosY, placement.PosZ);
            var rotation = new quaternion(math.normalizesafe(
                new float4(placement.RotX, placement.RotY, placement.RotZ, placement.RotW),
                new float4(0f, 0f, 0f, 1f)));
            SimulationCommandMessage retained = message;
            NativeDeriveResult derived = TryDeriveObjectTransaction(prefab, Entity.Null,
                Entity.Null, attachmentTarget, position, rotation, placement.Elevation,
                command.ToolRandomSeed,
                "building placement " + root.PrefabName + " op=" + command.OperationId,
                () => ReplayNativeObject(retained),
                () => CompleteNativeObject(key, command, resolved, now));
            switch (derived)
            {
                case NativeDeriveResult.Armed:
                    Diagnostics.FlightRecorder.Note("building placement regenerated op=" +
                                                      command.OperationId + " prefab=" +
                                                      root.PrefabName);
                    result = NativeObjectResult.Armed;
                    return true;
                case NativeDeriveResult.Busy:
                    result = NativeObjectResult.Retry;
                    return true;
                case NativeDeriveResult.Unsupported:
                    Diagnostics.FlightRecorder.Note(
                        "building placement generator unavailable; using exact graph fallback");
                    return false;
                case NativeDeriveResult.Failed:
                    // The complete captured graph is still present in the command. A transient
                    // local generator rejection must not discard the building before trying it.
                    Diagnostics.FlightRecorder.Note(
                        "building placement regeneration failed; using exact graph fallback");
                    return false;
                default:
                    return false;
            }
        }

        private static int NormalizeRemoteObjectCreationFlags(ObjectToolOperationCommand command)
        {
            int normalized = 0;
            if (command == null || command.Definitions == null) return normalized;

            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = command.Definitions[i];
                if (definition == null) continue;
                CreationFlags flags = (CreationFlags)definition.CreationFlags;
                if ((flags & CreationFlags.Permanent) == 0) continue;
                definition.CreationFlags = (uint)(flags & ~CreationFlags.Permanent);
                normalized++;
            }

            return normalized;
        }

        /// <summary>
        /// Reject object-tool batches that attempt to create or manipulate entities owned by
        /// simulation spawning. Their prefab names are enough to decide this before resolving
        /// live entity references, so a forged mover target cannot stall the ordered retry queue.
        /// Existing growables may be referenced by a legitimate edit, but they may not be the
        /// newly-created object in a placement batch.
        /// </summary>
        private bool TryFindUnsafeSimulationReference(ObjectToolOperationCommand command,
            out string prefabName)
        {
            bool specializedPlacement = IsSpecializedIndustryPlacement(command);
            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = command.Definitions[i];
                Entity prefab;

                if (!definition.PrefabIsNull &&
                    _prefabIndex.TryResolve(definition.PrefabName, out prefab))
                {
                    if (EntityManager.HasComponent<MovingObjectData>(prefab) ||
                        (definition.Kind == ObjectToolDefinitionKind.Object &&
                         definition.Original.Kind == PortableEntityKind.None &&
                         EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
                         !EntityManager.HasComponent<SignatureBuildingData>(prefab) &&
                         !(specializedPlacement &&
                           IsAllowedSpecializedSpawnable(command, i, prefab))))
                    {
                        prefabName = definition.PrefabName;
                        return true;
                    }
                }

                if (IsMovingPrefabName(definition.SubPrefabName, out prefabName) ||
                    IsMovingPrefabName(definition.AttachedPrefabName, out prefabName) ||
                    (definition.HasOwnerDefinition &&
                     IsMovingPrefabName(definition.OwnerDefinitionPrefabName, out prefabName)) ||
                    IsMovingPortableReference(definition.Original, out prefabName) ||
                    IsMovingPortableReference(definition.Owner, out prefabName) ||
                    IsMovingPortableReference(definition.Attached, out prefabName))
                    return true;

                if (definition.Kind == ObjectToolDefinitionKind.NetCourse &&
                    (IsMovingPortableReference(definition.NetCourse.Start.Entity, out prefabName) ||
                     IsMovingPortableReference(definition.NetCourse.End.Entity, out prefabName)))
                    return true;
            }

            prefabName = null;
            return false;
        }

        /// <summary>
        /// A specialized-industry placement is distinguishable from arbitrary growable creation:
        /// its new root owns an extractor/storage area declared by that root prefab. Some
        /// facilities use a placeholder root plus one level-one spawnable building attached to the
        /// placeholder prefab; older/direct variants use a spawnable root. Require the complete
        /// graph before exempting either exact form from the generic growable rejection.
        /// </summary>
        private bool IsSpecializedIndustryPlacement(ObjectToolOperationCommand command)
        {
            if (command == null || command.Definitions == null || command.RootIndex < 0 ||
                command.RootIndex >= command.Definitions.Length) return false;
            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            if (root == null || root.Kind != ObjectToolDefinitionKind.Object || root.PrefabIsNull ||
                root.Original.Kind != PortableEntityKind.None ||
                root.Owner.Kind != PortableEntityKind.None ||
                root.Attached.Kind != PortableEntityKind.None ||
                !string.IsNullOrEmpty(root.AttachedPrefabName)) return false;

            CreationFlags rootFlags = (CreationFlags)root.CreationFlags;
            if ((rootFlags & (CreationFlags.Delete | CreationFlags.Relocate |
                              CreationFlags.Recreate | CreationFlags.Upgrade |
                              CreationFlags.Permanent)) != 0) return false;

            Entity rootPrefab;
            if (!_prefabIndex.TryResolve(root.PrefabName, out rootPrefab)) return false;
            bool directSpawnable =
                EntityManager.HasComponent<SpawnableBuildingData>(rootPrefab) &&
                !EntityManager.HasComponent<SignatureBuildingData>(rootPrefab);
            bool placeholder =
                EntityManager.HasComponent<PlaceholderBuildingData>(rootPrefab) &&
                EntityManager.HasComponent<BuildingData>(rootPrefab);
            if (!directSpawnable && !placeholder) return false;

            bool hasPlaceholderAttachment = false;
            if (placeholder)
            {
                for (int i = 0; i < command.Definitions.Length; i++)
                {
                    Entity candidatePrefab;
                    if (i != command.RootIndex &&
                        TryGetSpecializedPlaceholderAttachment(command, i, root,
                            rootPrefab, out candidatePrefab))
                    {
                        hasPlaceholderAttachment = true;
                        break;
                    }
                }
                if (!hasPlaceholderAttachment) return false;
            }

            for (int i = 0; i < command.Definitions.Length; i++)
                if (IsOwnedSpecializedAreaDefinition(command.Definitions[i], root, rootPrefab))
                    return true;
            return false;
        }

        /// <summary>
        /// One extractor/storage lot belonging to this placement's root. The polygon is not
        /// required to be a drawn ring: the game lets a player leave the area tool without
        /// drawing one, and the building then commits with the lot its prefab declares.
        /// </summary>
        private bool IsOwnedSpecializedAreaDefinition(ObjectToolDefinitionIntent area,
            ObjectToolDefinitionIntent root, Entity rootPrefab)
        {
            if (area == null || area.Kind != ObjectToolDefinitionKind.Area ||
                area.PrefabIsNull || !area.HasOwnerDefinition ||
                area.OwnerDefinitionPrefabName != root.PrefabName ||
                area.Original.Kind != PortableEntityKind.None ||
                area.Owner.Kind != PortableEntityKind.None ||
                area.Attached.Kind != PortableEntityKind.None ||
                !string.IsNullOrEmpty(area.AttachedPrefabName) ||
                area.CreationFlags != 0 || area.AreaNodes == null ||
                area.AreaNodes.Length == 0 ||
                area.AreaNodes.Length > ObjectToolOperationCommand.MaxAreaNodesPerDefinition)
                return false;

            float3 rootPosition = new float3(root.Object.PosX, root.Object.PosY,
                root.Object.PosZ);
            float3 ownerPosition = new float3(area.OwnerDefinitionX,
                area.OwnerDefinitionY, area.OwnerDefinitionZ);
            if (math.distancesq(rootPosition, ownerPosition) > 0.01f) return false;
            float4 rootRotation = new float4(root.Object.RotX, root.Object.RotY,
                root.Object.RotZ, root.Object.RotW);
            float4 ownerRotation = new float4(area.OwnerDefinitionRotX,
                area.OwnerDefinitionRotY, area.OwnerDefinitionRotZ,
                area.OwnerDefinitionRotW);
            if (math.abs(math.dot(rootRotation, ownerRotation)) < 0.999f) return false;

            Entity areaPrefab;
            return _prefabIndex.TryResolve(area.PrefabName, out areaPrefab) &&
                   IsSpecializedAreaPrefab(areaPrefab) &&
                   PrefabDeclaresOwnedArea(rootPrefab, areaPrefab);
        }

        private bool IsAllowedSpecializedSpawnable(ObjectToolOperationCommand command,
            int definitionIndex, Entity definitionPrefab)
        {
            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            Entity rootPrefab;
            if (!_prefabIndex.TryResolve(root.PrefabName, out rootPrefab)) return false;
            if (definitionIndex == command.RootIndex)
                return definitionPrefab == rootPrefab &&
                       EntityManager.HasComponent<SpawnableBuildingData>(rootPrefab);

            Entity attachmentPrefab;
            return TryGetSpecializedPlaceholderAttachment(command, definitionIndex,
                       root, rootPrefab, out attachmentPrefab) &&
                   attachmentPrefab == definitionPrefab;
        }

        private bool TryGetSpecializedPlaceholderAttachment(
            ObjectToolOperationCommand command, int definitionIndex,
            ObjectToolDefinitionIntent root, Entity rootPrefab, out Entity attachmentPrefab)
        {
            attachmentPrefab = Entity.Null;
            if (definitionIndex < 0 || definitionIndex >= command.Definitions.Length ||
                rootPrefab == Entity.Null ||
                !EntityManager.HasComponent<PlaceholderBuildingData>(rootPrefab))
                return false;

            ObjectToolDefinitionIntent definition =
                command.Definitions[definitionIndex];
            if (definition == null ||
                definition.Kind != ObjectToolDefinitionKind.Object ||
                definition.PrefabIsNull ||
                definition.Original.Kind != PortableEntityKind.None ||
                definition.Owner.Kind != PortableEntityKind.None ||
                definition.Attached.Kind != PortableEntityKind.None ||
                definition.HasOwnerDefinition ||
                definition.AttachedPrefabName != root.PrefabName ||
                definition.CreationFlags != (uint)CreationFlags.Attach ||
                !_prefabIndex.TryResolve(definition.PrefabName,
                    out attachmentPrefab))
                return false;

            return IsCompatiblePlaceholderAttachment(definition,
                attachmentPrefab, rootPrefab);
        }

        private bool IsCompatiblePlaceholderAttachment(
            ObjectToolDefinitionIntent definition, Entity attachmentPrefab,
            Entity placeholderPrefab)
        {
            if (definition == null ||
                definition.Kind != ObjectToolDefinitionKind.Object ||
                ((CreationFlags)definition.CreationFlags &
                 CreationFlags.Attach) == 0)
                return false;

            return IsCompatiblePlaceholderAttachmentPrefab(attachmentPrefab,
                placeholderPrefab);
        }

        /// <summary>
        /// The prefab relationship used by a specialized-industry placeholder and its visible
        /// level-one building. Kept separate from the transient definition checks above so the
        /// same relationship can identify the committed live graph at ModificationEnd.
        /// </summary>
        private bool IsCompatiblePlaceholderAttachmentPrefab(Entity attachmentPrefab,
            Entity placeholderPrefab)
        {
            if (attachmentPrefab == Entity.Null ||
                placeholderPrefab == Entity.Null ||
                !EntityManager.HasComponent<PrefabData>(attachmentPrefab) ||
                !EntityManager.HasComponent<ObjectData>(attachmentPrefab) ||
                !EntityManager.HasComponent<SpawnableBuildingData>(attachmentPrefab) ||
                !EntityManager.HasComponent<BuildingData>(attachmentPrefab) ||
                !EntityManager.HasComponent<PrefabData>(placeholderPrefab) ||
                !EntityManager.HasComponent<ObjectData>(placeholderPrefab) ||
                !EntityManager.HasComponent<PlaceholderBuildingData>(placeholderPrefab) ||
                !EntityManager.HasComponent<BuildingData>(placeholderPrefab))
                return false;

            SpawnableBuildingData attachment =
                EntityManager.GetComponentData<SpawnableBuildingData>(
                    attachmentPrefab);
            PlaceholderBuildingData placeholder =
                EntityManager.GetComponentData<PlaceholderBuildingData>(
                    placeholderPrefab);
            if (attachment.m_Level != 1 ||
                attachment.m_ZonePrefab == Entity.Null ||
                placeholder.m_ZonePrefab == Entity.Null ||
                !EntityManager.HasComponent<ZoneData>(attachment.m_ZonePrefab) ||
                !EntityManager.HasComponent<ZoneData>(placeholder.m_ZonePrefab))
                return false;

            ZoneData attachmentZone =
                EntityManager.GetComponentData<ZoneData>(attachment.m_ZonePrefab);
            ZoneData placeholderZone =
                EntityManager.GetComponentData<ZoneData>(placeholder.m_ZonePrefab);
            if (!attachmentZone.m_ZoneType.Equals(
                    placeholderZone.m_ZoneType))
                return false;

            BuildingData attachmentBuilding =
                EntityManager.GetComponentData<BuildingData>(attachmentPrefab);
            BuildingData placeholderBuilding =
                EntityManager.GetComponentData<BuildingData>(placeholderPrefab);
            return math.all(attachmentBuilding.m_LotSize <=
                            placeholderBuilding.m_LotSize);
        }

        /// <summary>
        /// True for a committed spawnable building that belongs to a player-placed specialized
        /// industry graph. Placeholder variants attach their visible level-one building to the
        /// placeholder; direct variants declare the extractor/storage area on the spawnable root.
        /// </summary>
        private bool IsLiveSpecializedIndustrySpawnable(Entity entity, Entity prefab)
        {
            if (entity == Entity.Null || prefab == Entity.Null ||
                !EntityManager.Exists(entity) || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<SpawnableBuildingData>(prefab)) return false;

            // The prefab declaration alone is not origin evidence: a future simulation spawner
            // could legitimately choose a spawnable which also declares an area. Direct variants
            // count as placed only when this live instance actually owns the specialized area
            // graph produced by the object tool.
            if (PrefabDeclaresSpecializedArea(prefab) &&
                HasLiveOwnedSpecializedArea(entity)) return true;
            if (!EntityManager.HasComponent<global::Game.Objects.Attached>(entity)) return false;

            Entity parent = EntityManager
                .GetComponentData<global::Game.Objects.Attached>(entity).m_Parent;
            if (parent == Entity.Null || parent == entity || !EntityManager.Exists(parent))
                return false;

            // Prefab-local attachment definitions initially name the placeholder prefab itself;
            // after owner resolution the same relationship may name its live instance.
            Entity parentPrefab = Entity.Null;
            if (EntityManager.HasComponent<PrefabData>(parent))
                parentPrefab = parent;
            else if (EntityManager.HasComponent<PrefabRef>(parent))
                parentPrefab = EntityManager.GetComponentData<PrefabRef>(parent).m_Prefab;

            return parentPrefab != Entity.Null &&
                   IsCompatiblePlaceholderAttachmentPrefab(prefab, parentPrefab) &&
                   PrefabDeclaresSpecializedArea(parentPrefab);
        }

        private bool HasLiveOwnedSpecializedArea(Entity owner)
        {
            if (owner == Entity.Null || !EntityManager.Exists(owner) ||
                !EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner)) return false;

            DynamicBuffer<global::Game.Areas.SubArea> areas =
                EntityManager.GetBuffer<global::Game.Areas.SubArea>(owner, isReadOnly: true);
            for (int i = 0; i < areas.Length; i++)
            {
                Entity area = areas[i].m_Area;
                if (area == Entity.Null || !EntityManager.Exists(area) ||
                    !EntityManager.HasComponent<PrefabRef>(area)) continue;
                Entity areaPrefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                if (!IsSpecializedAreaPrefab(areaPrefab)) continue;

                Entity topOwner;
                if (TryFindTopOwner(area, out topOwner) && topOwner == owner) return true;
            }
            return false;
        }

        private static bool IsClosedAreaNodeRing(ObjectAreaNodeIntent[] nodes)
        {
            if (nodes == null || nodes.Length < 4 ||
                nodes.Length > ObjectToolOperationCommand.MaxAreaNodesPerDefinition) return false;
            ObjectAreaNodeIntent first = nodes[0];
            ObjectAreaNodeIntent last = nodes[nodes.Length - 1];
            return first.X == last.X && first.Y == last.Y && first.Z == last.Z;
        }

        private bool PrefabDeclaresOwnedArea(Entity objectPrefab, Entity areaPrefab)
        {
            if (!EntityManager.HasBuffer<SubArea>(objectPrefab)) return false;
            DynamicBuffer<SubArea> subAreas =
                EntityManager.GetBuffer<SubArea>(objectPrefab, isReadOnly: true);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity declared = subAreas[i].m_Prefab;
                if (declared == areaPrefab) return true;
                if (declared == Entity.Null || !EntityManager.Exists(declared)) continue;
                if (!EntityManager.HasBuffer<PlaceholderObjectElement>(declared)) continue;
                DynamicBuffer<PlaceholderObjectElement> candidates =
                    EntityManager.GetBuffer<PlaceholderObjectElement>(declared, isReadOnly: true);
                for (int j = 0; j < candidates.Length; j++)
                    if (candidates[j].m_Object == areaPrefab) return true;
            }
            return false;
        }

        private bool PrefabDeclaresSpecializedArea(Entity objectPrefab)
        {
            if (objectPrefab == Entity.Null || !EntityManager.Exists(objectPrefab) ||
                !EntityManager.HasBuffer<SubArea>(objectPrefab)) return false;

            DynamicBuffer<SubArea> subAreas =
                EntityManager.GetBuffer<SubArea>(objectPrefab, isReadOnly: true);
            for (int i = 0; i < subAreas.Length; i++)
            {
                Entity declared = subAreas[i].m_Prefab;
                if (IsSpecializedAreaPrefab(declared)) return true;
                if (declared == Entity.Null || !EntityManager.Exists(declared) ||
                    !EntityManager.HasBuffer<PlaceholderObjectElement>(declared)) continue;

                DynamicBuffer<PlaceholderObjectElement> candidates =
                    EntityManager.GetBuffer<PlaceholderObjectElement>(declared, isReadOnly: true);
                for (int j = 0; j < candidates.Length; j++)
                    if (IsSpecializedAreaPrefab(candidates[j].m_Object)) return true;
            }
            return false;
        }

        private bool IsMovingPrefabName(string name, out string unsafeName)
        {
            unsafeName = null;
            if (string.IsNullOrEmpty(name)) return false;
            Entity prefab;
            if (!_prefabIndex.TryResolve(name, out prefab)) return false;
            if (!EntityManager.HasComponent<MovingObjectData>(prefab)) return false;
            unsafeName = name;
            return true;
        }

        private bool IsMovingPortableReference(PortableEntityRef reference, out string unsafeName)
        {
            unsafeName = null;
            if (reference.Kind == PortableEntityKind.None ||
                string.IsNullOrEmpty(reference.PrefabName)) return false;
            Entity prefab;
            if (!_prefabIndex.TryResolve(reference.PrefabName, out prefab) ||
                !EntityManager.HasComponent<MovingObjectData>(prefab)) return false;
            unsafeName = reference.PrefabName;
            return true;
        }

        private void ReplayNativeObject(SimulationCommandMessage message)
        {
            if (_nativeObjectReplayPrefix.Count >= MaxNativeObjectReplayPrefix)
            {
                Mod.log.Warn("[MP] BuildSync: native object replay prefix overflowed; requesting " +
                             "world recovery instead of losing an operation.");
                Diagnostics.FlightRecorder.Note("object replay prefix overflow; recovery requested");
                SyncInbox.RequestResync("native object replay prefix overflow");
                return;
            }
            _nativeObjectReplayPrefix.Add(message);
            Diagnostics.FlightRecorder.Note("object transaction rejected; replay prioritized");
        }

        private void CompleteNativeObject(NativeObjectOperationKey key,
            ObjectToolOperationCommand command, ResolvedObjectDefinition[] resolved, long capturedNow)
        {
            long now = Mod.Service != null ? Mod.Service.NowMs : capturedNow;
            _recentNativeObjectOperations.Remember(key, now, NativeObjectReplayRememberMs);
            try
            {
                if (command.IsAssetStamp)
                {
                    Entity stampPrefab;
                    if (_prefabIndex.TryResolve(command.AssetStampPrefabName, out stampPrefab))
                        ConstructionCharger.ChargeObject(EntityManager, stampPrefab,
                            command.AssetStampPrefabName);
                }
                else
                {
                    ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
                    Entity rootPrefab = resolved[command.RootIndex].Prefab;
                    CreationFlags flags = (CreationFlags)root.CreationFlags;
                    if ((flags & CreationFlags.Relocate) == 0 && rootPrefab != Entity.Null)
                    {
                        bool isServiceUpgrade =
                            root.HasOwnerDefinition &&
                            (EntityManager.HasComponent<ServiceUpgradeData>(rootPrefab) ||
                             EntityManager.HasComponent<BuildingExtensionData>(rootPrefab));
                        if (root.Owner.Kind != PortableEntityKind.None ||
                            (flags & CreationFlags.Upgrade) != 0 ||
                            isServiceUpgrade)
                            ConstructionCharger.ChargeUpgrade(EntityManager, rootPrefab,
                                root.PrefabName ?? "object upgrade");
                        else
                            ConstructionCharger.ChargeObject(EntityManager, rootPrefab,
                                root.PrefabName ?? "object");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn("[MP] BuildSync: committed object charge failed: " + ex.Message);
            }
            Diagnostics.FlightRecorder.Note((command.IsAssetStamp
                ? "asset stamp transaction committed/drained op="
                : "object transaction committed/drained op=") + command.OperationId);
        }

        /// <summary>
        /// True when two <see cref="PortableEntityRef"/> values name the same source entity. Used to
        /// tell "the batch referenced one entity twice" apart from "two different source entities
        /// collapsed onto one local entity" - the latter is the aliasing hazard below.
        /// </summary>
        private static bool SamePortableSource(PortableEntityRef left, PortableEntityRef right)
        {
            if (left.Kind != right.Kind ||
                !string.Equals(left.PrefabName, right.PrefabName, System.StringComparison.Ordinal) ||
                !string.Equals(left.OwnerPrefabName, right.OwnerPrefabName,
                    System.StringComparison.Ordinal)) return false;

            float3 leftPosition = new float3(left.PosX, left.PosY, left.PosZ);
            float3 rightPosition = new float3(right.PosX, right.PosY, right.PosZ);
            if (!leftPosition.Equals(rightPosition)) return false;

            if (left.Kind != PortableEntityKind.NetEdge) return true;
            return left.Ax == right.Ax && left.Ay == right.Ay && left.Az == right.Az &&
                   left.Dx == right.Dx && left.Dy == right.Dy && left.Dz == right.Dz &&
                   left.Bx == right.Bx && left.By == right.By && left.Bz == right.Bz &&
                   left.Cx == right.Cx && left.Cy == right.Cy && left.Cz == right.Cz;
        }

        /// <summary>
        /// Claim one live entity as the original of exactly one definition in this batch.
        ///
        /// A tool's own output names every original at most once (its attachment set is de-duplicated
        /// and each owned element is visited once), so two definitions landing on the SAME local
        /// entity means this machine's geometry is subdivided differently from the sender's - it never
        /// received the split that separated them. Committing both would hand the apply passes two
        /// Temps sharing one original, which they dereference without a liveness check: the confirmed
        /// native crash. Refuse the batch instead; the caller retries and then requests recovery.
        /// </summary>
        private bool TryClaimObjectOriginal(
            Dictionary<Entity, PortableEntityRef> claims, PortableEntityRef source, Entity target)
        {
            if (target == Entity.Null) return true;
            PortableEntityRef claimed;
            if (!claims.TryGetValue(target, out claimed))
            {
                claims[target] = source;
                return true;
            }
            return SamePortableSource(claimed, source);
        }

        private bool TryResolveObjectOperation(ObjectToolOperationCommand command,
            out ResolvedObjectDefinition[] resolved, out string reason)
        {
            resolved = new ResolvedObjectDefinition[command.Definitions.Length];
            var originalClaims = new Dictionary<Entity, PortableEntityRef>();
            if (command.IsAssetStamp)
            {
                Entity stampPrefab;
                if (!_prefabIndex.TryResolve(command.AssetStampPrefabName,
                        candidate => EntityManager.Exists(candidate) &&
                                     EntityManager.HasComponent<AssetStampData>(candidate),
                        out stampPrefab))
                {
                    reason = "asset-stamp prefab is unavailable or incompatible";
                    return false;
                }
            }
            for (int i = 0; i < command.Definitions.Length; i++)
            {
                ObjectToolDefinitionIntent definition = command.Definitions[i];
                var target = new ResolvedObjectDefinition();
                if (!definition.PrefabIsNull &&
                    !_prefabIndex.TryResolve(definition.PrefabName,
                        candidate => ValidateDefinitionPrefab(definition.Kind, candidate),
                        out target.Prefab))
                {
                    reason = "definition " + i + " prefab '" + definition.PrefabName +
                             "' is unavailable or incompatible with " + definition.Kind;
                    return false;
                }
                if (!string.IsNullOrEmpty(definition.SubPrefabName) &&
                    (!_prefabIndex.TryResolve(definition.SubPrefabName, out target.SubPrefab) ||
                     !EntityManager.HasComponent<PrefabData>(target.SubPrefab)))
                {
                    reason = "definition sub-prefab is unavailable";
                    return false;
                }
                if (!TryResolvePortableRef(definition.Original, out target.Original))
                {
                    reason = "definition " + i + " original target is not present " +
                             Describe(definition.Original);
                    return false;
                }
                if (!TryClaimObjectOriginal(originalClaims, definition.Original, target.Original))
                {
                    reason = "definition " + i + " resolved onto an original already claimed by " +
                             "another definition (local geometry is subdivided differently)";
                    return false;
                }
                if (!TryResolvePortableRef(definition.Owner, out target.Owner))
                {
                    reason = "definition " + i + " owner target is not present " +
                             Describe(definition.Owner);
                    return false;
                }
                if (!TryResolvePortableRef(definition.Attached, out target.Attached))
                {
                    reason = "definition " + i + " attachment target is not present " +
                             Describe(definition.Attached);
                    return false;
                }
                if (!string.IsNullOrEmpty(definition.AttachedPrefabName))
                {
                    Entity attachedPrefab;
                    if (target.Attached != Entity.Null ||
                        !_prefabIndex.TryResolve(definition.AttachedPrefabName,
                            candidate => IsCompatiblePlaceholderAttachment(definition,
                                target.Prefab, candidate), out attachedPrefab))
                    {
                        reason = "prefab-local attachment is unavailable or incompatible";
                        return false;
                    }
                    target.Attached = attachedPrefab;
                }
                if (definition.PrefabIsNull && target.Original == Entity.Null)
                {
                    reason = "a null-prefab definition has no original";
                    return false;
                }
                if (definition.HasOwnerDefinition &&
                    !_prefabIndex.TryResolve(definition.OwnerDefinitionPrefabName,
                        candidate => EntityManager.HasComponent<ObjectData>(candidate),
                        out target.OwnerDefinitionPrefab))
                {
                    reason = "owner-definition prefab is unavailable";
                    return false;
                }
                if (definition.Kind == ObjectToolDefinitionKind.NetCourse &&
                    !TryResolvePortableRef(definition.NetCourse.Start.Entity,
                        out target.StartEntity))
                {
                    reason = "definition " + i + " network start target is not present";
                    return false;
                }
                if (definition.Kind == ObjectToolDefinitionKind.NetCourse &&
                    !TryResolvePortableRef(definition.NetCourse.End.Entity,
                        out target.EndEntity))
                {
                    reason = "definition " + i + " network end target is not present";
                    return false;
                }
                if (definition.Kind == ObjectToolDefinitionKind.Object &&
                    (((CreationFlags)definition.CreationFlags & CreationFlags.Delete) == 0) &&
                    !QuaternionIsPlausible(definition.Object.RotX, definition.Object.RotY,
                        definition.Object.RotZ, definition.Object.RotW))
                {
                    reason = "object definition has an invalid source rotation";
                    return false;
                }
                resolved[i] = target;
            }
            reason = null;
            Diagnostics.FlightRecorder.Note("object operation targets resolved defs=" + resolved.Length);
            return true;
        }

        private static bool QuaternionIsPlausible(float x, float y, float z, float w)
        {
            float lengthSq = x * x + y * y + z * z + w * w;
            return math.isfinite(lengthSq) && lengthSq >= 0.25f && lengthSq <= 2.25f;
        }

        private bool ValidateDefinitionPrefab(ObjectToolDefinitionKind kind, Entity prefab)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<PrefabData>(prefab)) return false;
            switch (kind)
            {
                case ObjectToolDefinitionKind.Object:
                    return EntityManager.HasComponent<ObjectData>(prefab);
                case ObjectToolDefinitionKind.NetCourse:
                    return EntityManager.HasComponent<NetData>(prefab) &&
                           EntityManager.HasComponent<NetGeometryData>(prefab);
                case ObjectToolDefinitionKind.Area:
                    return EntityManager.HasComponent<AreaData>(prefab);
                default:
                    return false;
            }
        }

        private Entity CreateObjectToolDefinition(ObjectToolDefinitionIntent source,
            ResolvedObjectDefinition resolved)
        {
            Entity entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new CreationDefinition
            {
                m_Prefab = resolved.Prefab,
                m_SubPrefab = resolved.SubPrefab,
                m_Original = resolved.Original,
                m_Owner = resolved.Owner,
                m_Attached = resolved.Attached,
                // Defense in depth: remote definitions always enter through the coordinator's
                // isolated transaction, even if a future caller skips the decode normalization.
                m_Flags = (CreationFlags)source.CreationFlags & ~CreationFlags.Permanent,
                m_RandomSeed = source.RandomSeed,
            });
            if (source.HasOwnerDefinition)
            {
                EntityManager.AddComponentData(entity, new OwnerDefinition
                {
                    m_Prefab = resolved.OwnerDefinitionPrefab,
                    m_Position = new float3(source.OwnerDefinitionX,
                        source.OwnerDefinitionY, source.OwnerDefinitionZ),
                    m_Rotation = new quaternion(source.OwnerDefinitionRotX,
                        source.OwnerDefinitionRotY, source.OwnerDefinitionRotZ,
                        source.OwnerDefinitionRotW),
                });
            }

            if (source.Kind == ObjectToolDefinitionKind.Object)
            {
                ObjectDefinitionIntent value = source.Object;
                EntityManager.AddComponentData(entity, new ObjectDefinition
                {
                    m_Position = new float3(value.PosX, value.PosY, value.PosZ),
                    m_LocalPosition = new float3(value.LocalX, value.LocalY, value.LocalZ),
                    m_Scale = new float3(value.ScaleX, value.ScaleY, value.ScaleZ),
                    m_Rotation = new quaternion(value.RotX, value.RotY, value.RotZ, value.RotW),
                    m_LocalRotation = new quaternion(value.LocalRotX, value.LocalRotY,
                        value.LocalRotZ, value.LocalRotW),
                    m_Elevation = value.Elevation,
                    m_Intensity = value.Intensity,
                    m_Age = value.Age,
                    m_IsDecoration = value.IsDecoration,
                    m_ParentMesh = value.ParentMesh,
                    m_GroupIndex = value.GroupIndex,
                    m_Probability = value.Probability,
                    m_PrefabSubIndex = value.PrefabSubIndex,
                });
            }
            else if (source.Kind == ObjectToolDefinitionKind.NetCourse)
            {
                ObjectNetCourseIntent value = source.NetCourse;
                EntityManager.AddComponentData(entity, new NetCourse
                {
                    m_StartPosition = CreateCoursePos(value.Start, resolved.StartEntity),
                    m_EndPosition = CreateCoursePos(value.End, resolved.EndEntity),
                    m_Curve = new Bezier4x3
                    {
                        a = new float3(value.Ax, value.Ay, value.Az),
                        b = new float3(value.Bx, value.By, value.Bz),
                        c = new float3(value.Cx, value.Cy, value.Cz),
                        d = new float3(value.Dx, value.Dy, value.Dz),
                    },
                    m_Elevation = new float2(value.ElevationLeft, value.ElevationRight),
                    m_Length = value.Length,
                    m_FixedIndex = value.FixedIndex,
                });
            }
            else
            {
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.AddBuffer<global::Game.Areas.Node>(entity);
                ObjectAreaNodeIntent[] sourceNodes = source.AreaNodes;
                nodes.ResizeUninitialized(sourceNodes.Length);
                for (int i = 0; i < sourceNodes.Length; i++)
                    nodes[i] = new global::Game.Areas.Node(
                        new float3(sourceNodes[i].X, sourceNodes[i].Y, sourceNodes[i].Z),
                        sourceNodes[i].Elevation);
            }

            if (source.HasUpgraded)
            {
                EntityManager.AddComponentData(entity, new Upgraded
                {
                    m_Flags = new CompositionFlags(
                        (CompositionFlags.General)source.UpgradeGeneral,
                        (CompositionFlags.Side)source.UpgradeLeft,
                        (CompositionFlags.Side)source.UpgradeRight),
                });
            }
            EntityManager.AddComponent<Updated>(entity);
            EntityManager.AddComponent<Deleted>(entity);
            return entity;
        }

        private static CoursePos CreateCoursePos(ObjectCoursePositionIntent source, Entity target)
        {
            return new CoursePos
            {
                m_Entity = target,
                m_Position = new float3(source.PosX, source.PosY, source.PosZ),
                m_Rotation = new quaternion(source.RotX, source.RotY, source.RotZ, source.RotW),
                m_Elevation = new float2(source.ElevationLeft, source.ElevationRight),
                m_CourseDelta = source.CourseDelta,
                m_SplitPosition = source.SplitPosition,
                m_Flags = (CoursePosFlags)source.Flags,
                m_ParentMesh = source.ParentMesh,
            };
        }

        private void DestroyDefinitions(List<Entity> definitions)
        {
            for (int i = 0; i < definitions.Count; i++)
                if (EntityManager.Exists(definitions[i])) EntityManager.DestroyEntity(definitions[i]);
        }

        private bool EquivalentObjectOperationAlreadyExists(ObjectToolOperationCommand command,
            ResolvedObjectDefinition[] resolved)
        {
            // A stamp has no root object identity. Replay suppression is handled by OperationId;
            // geometry proximity would incorrectly suppress two intentional adjacent stamps.
            if (command.IsAssetStamp) return false;
            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            if (root.Kind != ObjectToolDefinitionKind.Object ||
                root.Original.Kind != PortableEntityKind.None) return false;
            ObjectDefinitionIntent data = root.Object;
            PortableEntityRef wantedIdentity = default(PortableEntityRef);
            if (root.Owner.Kind != PortableEntityKind.None)
            {
                wantedIdentity.OwnerPrefabName = root.Owner.PrefabName;
                wantedIdentity.OwnerX = root.Owner.PosX;
                wantedIdentity.OwnerY = root.Owner.PosY;
                wantedIdentity.OwnerZ = root.Owner.PosZ;
            }
            else if (root.HasOwnerDefinition)
            {
                wantedIdentity.OwnerPrefabName = root.OwnerDefinitionPrefabName;
                wantedIdentity.OwnerX = root.OwnerDefinitionX;
                wantedIdentity.OwnerY = root.OwnerDefinitionY;
                wantedIdentity.OwnerZ = root.OwnerDefinitionZ;
            }
            return FindEquivalentPlacedObject(resolved[command.RootIndex].Prefab, data,
                root.RandomSeed, wantedIdentity) != Entity.Null;
        }

        /// <summary>
        /// Geometry alone is not a placement identity. Two players can legitimately place the same
        /// prefab close together, especially small roadside buildings and props. Require the
        /// generated variant seed as well, plus orientation for a free-standing object, before
        /// treating a different operation as an already-committed replay.
        /// </summary>
        private Entity FindEquivalentPlacedObject(Entity prefab, ObjectDefinitionIntent source,
            int randomSeed, PortableEntityRef identity)
        {
            List<Entity> candidates = Candidates(_objectCandidates, _portableObjects, prefab);
            float3 position = new float3(source.PosX, source.PosY, source.PosZ);
            float4 rotation = math.normalizesafe(new float4(source.RotX, source.RotY,
                    source.RotZ, source.RotW),
                new float4(0f, 0f, 0f, 1f));
            Entity best = Entity.Null;
            float bestDistance = 4f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;

                global::Game.Objects.Transform transform = EntityManager
                    .GetComponentData<global::Game.Objects.Transform>(candidate);
                float distance = math.distancesq(transform.m_Position, position);
                if (distance >= bestDistance) continue;

                // Attachment resolution may rotate the committed instance relative to its source
                // definition. For unattached objects, orientation is stable and distinguishes two
                // intentional close placements that happen to share a variant seed.
                bool attached = EntityManager.HasComponent<global::Game.Objects.Attached>(candidate);
                float rotationDot = attached ? 1f : math.abs(math.dot(
                    math.normalizesafe(transform.m_Rotation.value,
                        new float4(0f, 0f, 0f, 1f)), rotation));
                // Exact overlap can happen when two players click before receiving one another's
                // edit. It is a genuine collision even though their independently advanced seeds
                // differ; do not feed an impossible stacked graph into the native apply pipeline.
                if (distance <= ExactDuplicateDistanceSq && rotationDot >= 0.99999f)
                    return candidate;
                if (!EntityManager.HasComponent<PseudoRandomSeed>(candidate) ||
                    unchecked((ushort)EntityManager
                        .GetComponentData<PseudoRandomSeed>(candidate).m_Seed) !=
                    unchecked((ushort)randomSeed) || (!attached && rotationDot < 0.9999f)) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>
        /// Name a reference that would not resolve. An unresolved reference costs everyone a full
        /// world stream, so the log line has to say which one it was: whether its prefab is even
        /// known here separates "this machine is missing the thing" from "this machine is missing
        /// the mod/DLC content".
        /// </summary>
        private string Describe(PortableEntityRef source)
        {
            Entity prefab;
            string prefabState = _prefabIndex.TryResolve(source.PrefabName, out prefab)
                ? ""
                : ", prefab unknown here";
            string owner = string.IsNullOrEmpty(source.OwnerPrefabName)
                ? ""
                : " under '" + source.OwnerPrefabName + "' at (" +
                  source.OwnerX.ToString("F1") + ", " + source.OwnerZ.ToString("F1") + ")";
            return "(" + source.Kind + " '" + source.PrefabName + "' at (" +
                   source.PosX.ToString("F1") + ", " + source.PosZ.ToString("F1") + ")" +
                   owner + prefabState + ")";
        }

        private bool TryResolvePortableRef(PortableEntityRef source, out Entity result)
        {
            result = Entity.Null;
            if (source.Kind == PortableEntityKind.None) return true;
            Entity prefab;
            if (!_prefabIndex.TryResolve(source.PrefabName, out prefab)) return false;

            // Owned lifecycle references first use the same owner buffers that the simulation
            // maintains. Geometry remains a compatibility fallback for references captured before
            // a structural path was available or for a benign buffer-layout difference.
            if (source.OwnerPath != null && source.OwnerPath.Length != 0 &&
                TryResolveOwnerPath(source, prefab, out result))
                return true;

            float3 position = new float3(source.PosX, source.PosY, source.PosZ);
            switch (source.Kind)
            {
                case PortableEntityKind.Object:
                    result = FindPortableObject(prefab, position, source);
                    return result != Entity.Null;
                case PortableEntityKind.NetNode:
                    result = FindPortableNode(prefab, position, source);
                    return result != Entity.Null;
                case PortableEntityKind.NetEdge:
                    result = FindPortableEdge(prefab, source);
                    return result != Entity.Null;
                case PortableEntityKind.Area:
                    result = FindPortableArea(prefab, position, source);
                    return result != Entity.Null;
                default:
                    return false;
            }
        }

        private bool TryResolveOwnerPath(PortableEntityRef source, Entity targetPrefab,
            out Entity result)
        {
            result = Entity.Null;
            if (string.IsNullOrEmpty(source.OwnerPrefabName) ||
                source.OwnerPath == null || source.OwnerPath.Length == 0 ||
                source.OwnerPath.Length > ObjectToolOperationCommand.MaxOwnerPathDepth)
                return false;

            Entity ownerPrefab;
            if (!_prefabIndex.TryResolve(source.OwnerPrefabName, out ownerPrefab))
                return false;
            Entity cursor = FindPortableObject(ownerPrefab,
                new float3(source.OwnerX, source.OwnerY, source.OwnerZ),
                default(PortableEntityRef));
            if (cursor == Entity.Null) return false;

            for (int i = 0; i < source.OwnerPath.Length; i++)
            {
                Entity child;
                if (!TryResolveOwnerPathStep(cursor, source.OwnerPath[i], out child))
                    return false;
                cursor = child;
            }

            PortableEntityKind resolvedKind;
            if (!EntityManager.Exists(cursor) ||
                EntityManager.HasComponent<Temp>(cursor) ||
                EntityManager.HasComponent<Deleted>(cursor) ||
                !EntityManager.HasComponent<PrefabRef>(cursor) ||
                EntityManager.GetComponentData<PrefabRef>(cursor).m_Prefab != targetPrefab ||
                !TryGetPortableEntityKind(cursor, out resolvedKind) ||
                resolvedKind != source.Kind)
                return false;
            if ((source.Kind == PortableEntityKind.NetNode ||
                 source.Kind == PortableEntityKind.NetEdge) &&
                !MatchesNetContract(targetPrefab, source))
                return false;

            result = cursor;
            return true;
        }

        private bool TryResolveOwnerPathStep(Entity owner, PortableOwnerPathStep step,
            out Entity result)
        {
            result = Entity.Null;
            Entity prefab;
            if (!_prefabIndex.TryResolve(step.PrefabName, out prefab)) return false;

            switch (step.BufferKind)
            {
                case PortableOwnerPathKind.InstalledUpgrade:
                    if (!EntityManager.HasBuffer<global::Game.Buildings.InstalledUpgrade>(owner))
                        return false;
                    DynamicBuffer<global::Game.Buildings.InstalledUpgrade> upgrades =
                        EntityManager.GetBuffer<global::Game.Buildings.InstalledUpgrade>(
                            owner, isReadOnly: true);
                    int upgradeOrdinal = 0;
                    for (int i = 0; i < upgrades.Length; i++)
                    {
                        Entity candidate = upgrades[i].m_Upgrade;
                        if (!MatchesOwnerPathCandidate(owner, candidate, prefab,
                                step.EntityKind)) continue;
                        if (upgradeOrdinal++ != step.PrefabOrdinal) continue;
                        result = candidate;
                        return true;
                    }
                    return false;

                case PortableOwnerPathKind.SubObject:
                    if (!EntityManager.HasBuffer<global::Game.Objects.SubObject>(owner))
                        return false;
                    DynamicBuffer<global::Game.Objects.SubObject> objects =
                        EntityManager.GetBuffer<global::Game.Objects.SubObject>(
                            owner, isReadOnly: true);
                    int objectOrdinal = 0;
                    for (int i = 0; i < objects.Length; i++)
                    {
                        Entity candidate = objects[i].m_SubObject;
                        if (!MatchesOwnerPathCandidate(owner, candidate, prefab,
                                step.EntityKind)) continue;
                        if (objectOrdinal++ != step.PrefabOrdinal) continue;
                        result = candidate;
                        return true;
                    }
                    return false;

                case PortableOwnerPathKind.SubNet:
                    if (!EntityManager.HasBuffer<global::Game.Net.SubNet>(owner))
                        return false;
                    DynamicBuffer<global::Game.Net.SubNet> nets =
                        EntityManager.GetBuffer<global::Game.Net.SubNet>(
                            owner, isReadOnly: true);
                    int netOrdinal = 0;
                    for (int i = 0; i < nets.Length; i++)
                    {
                        Entity candidate = nets[i].m_SubNet;
                        if (!MatchesOwnerPathCandidate(owner, candidate, prefab,
                                step.EntityKind)) continue;
                        if (netOrdinal++ != step.PrefabOrdinal) continue;
                        result = candidate;
                        return true;
                    }
                    return false;

                case PortableOwnerPathKind.SubArea:
                    if (!EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner))
                        return false;
                    DynamicBuffer<global::Game.Areas.SubArea> areas =
                        EntityManager.GetBuffer<global::Game.Areas.SubArea>(
                            owner, isReadOnly: true);
                    int areaOrdinal = 0;
                    for (int i = 0; i < areas.Length; i++)
                    {
                        Entity candidate = areas[i].m_Area;
                        if (!MatchesOwnerPathCandidate(owner, candidate, prefab,
                                step.EntityKind)) continue;
                        if (areaOrdinal++ != step.PrefabOrdinal) continue;
                        result = candidate;
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private bool MatchesOwnerPathCandidate(Entity owner, Entity candidate, Entity prefab,
            PortableEntityKind kind)
        {
            if (candidate == Entity.Null || !EntityManager.Exists(candidate) ||
                EntityManager.HasComponent<Temp>(candidate) ||
                EntityManager.HasComponent<Deleted>(candidate) ||
                !EntityManager.HasComponent<PrefabRef>(candidate) ||
                EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab ||
                !EntityManager.HasComponent<Owner>(candidate) ||
                EntityManager.GetComponentData<Owner>(candidate).m_Owner != owner)
                return false;
            PortableEntityKind candidateKind;
            return TryGetPortableEntityKind(candidate, out candidateKind) &&
                   candidateKind == kind;
        }

        private Entity FindPortableObject(Entity prefab, float3 position, PortableEntityRef identity)
        {
            List<Entity> candidates = Candidates(_objectCandidates, _portableObjects, prefab);
            Entity best = Entity.Null;
            float bestDistance = 4f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;
                float distance = math.distancesq(EntityManager
                    .GetComponentData<global::Game.Objects.Transform>(candidate).m_Position, position);
                if (distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private Entity FindPortableNode(Entity prefab, float3 position, PortableEntityRef identity)
        {
            if (!MatchesNetContract(prefab, identity)) return Entity.Null;
            List<Entity> candidates = Candidates(_nodeCandidates, _liveNodes, prefab);
            Entity best = Entity.Null;
            float bestDistance = 4f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;
                float3 candidatePosition = EntityManager.GetComponentData<Node>(candidate).m_Position;
                if (math.abs(candidatePosition.y - position.y) > 3f) continue;
                float distance = math.distancesq(candidatePosition.xz, position.xz);
                if (distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private Entity FindPortableEdge(Entity prefab, PortableEntityRef identity)
        {
            var sourceCurve = new Bezier4x3
            {
                a = new float3(identity.Ax, identity.Ay, identity.Az),
                b = new float3(identity.Bx, identity.By, identity.Bz),
                c = new float3(identity.Cx, identity.Cy, identity.Cz),
                d = new float3(identity.Dx, identity.Dy, identity.Dz),
            };
            float3 anchor = new float3(identity.PosX, identity.PosY, identity.PosZ);
            if (!MatchesNetContract(prefab, identity)) return Entity.Null;
            List<Entity> candidates = Candidates(_edgeCandidates, _liveEdges, prefab);
            Entity best = Entity.Null;
            float bestDistance = 2f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;
                Bezier4x3 curve = EntityManager.GetComponentData<global::Game.Net.Curve>(candidate).m_Bezier;
                if (!SplitMatch.IsSubCurve3D(curve, sourceCurve) &&
                    !SplitMatch.IsSubCurve3D(sourceCurve, curve)) continue;
                float t;
                float distance = MathUtils.Distance(curve, anchor, out t);
                if (distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private Entity FindPortableArea(Entity prefab, float3 anchor, PortableEntityRef identity)
        {
            List<Entity> candidates = Candidates(_areaCandidates, _portableAreas, prefab);
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.GetBuffer<global::Game.Areas.Node>(candidate, isReadOnly: true);
                if (nodes.Length > 0 && math.distancesq(nodes[0].m_Position, anchor) <= 4f)
                    return candidate;
            }
            return Entity.Null;
        }

        private bool MatchesNetContract(Entity prefab, PortableEntityRef identity)
        {
            if (!EntityManager.HasComponent<NetData>(prefab)) return false;
            NetData data = EntityManager.GetComponentData<NetData>(prefab);
            return (uint)data.m_RequiredLayers == identity.RequiredLayers &&
                   (uint)data.m_ConnectLayers == identity.ConnectLayers;
        }

        private bool MatchesPortableOwner(Entity candidate, PortableEntityRef identity)
        {
            bool wantsOwner = !string.IsNullOrEmpty(identity.OwnerPrefabName);
            Entity topOwner;
            if (!TryFindTopOwner(candidate, out topOwner)) return false;
            if (!wantsOwner) return topOwner == Entity.Null;
            if (topOwner == Entity.Null || !EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner)) return false;
            string ownerName = _prefabSystem.GetPrefabName(
                EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab);
            if (ownerName != identity.OwnerPrefabName) return false;
            float3 ownerPosition = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(topOwner).m_Position;
            return math.distancesq(ownerPosition,
                new float3(identity.OwnerX, identity.OwnerY, identity.OwnerZ)) <= 4f;
        }
    }
}
