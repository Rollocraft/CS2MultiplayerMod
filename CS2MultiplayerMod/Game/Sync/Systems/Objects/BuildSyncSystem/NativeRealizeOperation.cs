using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Realizing one remote operation - asset stamp, object placement, or the placement input
    // behind it - and normalising the creation flags a peer may have sent.
    public partial class BuildSyncSystem
    {
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
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: dropping malformed asset-stamp command: " + ex.Message);
                return NativeObjectResult.Rejected;
            }

            var key = new NativeObjectOperationKey
            {
                Origin = message.OriginPlayerId,
                Operation = command.OperationId,
            };
            if (_recentNativeObjectOperations.Contains(key, now))
            {
                SyncLog.Trace(LogTopic.Buildings, "asset stamp duplicate suppressed op=" +
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
                SyncLog.Warn(LogTopic.Buildings, "BuildSync: asset stamp '" + command.PrefabName +
                    "' is unavailable here; skipping.");
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
                    SyncLog.Trace(LogTopic.Buildings, "asset stamp derived op=" +
                        command.OperationId + " prefab=" + prefabName);
                    return NativeObjectResult.Armed;
                case NativeDeriveResult.Busy:
                    return NativeObjectResult.Retry;
                case NativeDeriveResult.Unsupported:
                    // This build cannot reach the generator, and a stamp has no reduced form that
                    // preserves its topology. A world reload is the only complete fallback.
                    SyncLog.Warn(LogTopic.Buildings,
                        "BuildSync: the game's definition generator is not reachable; " +
                        "the remote stamp '" + prefabName +
                        "' was skipped and world recovery was requested.");
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("asset stamp generator unavailable", "object",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                        .About("asset stamp generator")
                        .Tried("nothing - this build cannot reach the generator and a stamp has no reduced form"));
                    return NativeObjectResult.Rejected;
                case NativeDeriveResult.Failed:
                    SyncLog.Trace(LogTopic.Buildings,
                        "asset stamp derive failed; recovery requested");
                    SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                        .Create("asset stamp generation failed", "object",
                            CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                        .About("asset stamp generation")
                        .Tried("nothing - the generator refused the stamp and will refuse it again"));
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
                SyncLog.Warn(LogTopic.Buildings, "BuildSync: committed stamp charge failed: " +
                    ex.Message);
            }
            SyncLog.Trace(LogTopic.Buildings, "asset stamp transaction committed/drained op=" +
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
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: dropping malformed native object operation: " + ex.Message);
                return NativeObjectResult.Rejected;
            }

            // Permanent is local definition execution policy, not portable operation intent. New
            // senders remove it during capture; normalize again here so an older or hostile peer
            // cannot bypass the isolated Temp/apply/drain transaction or turn a resolvable command
            // into an impossible ten-second retry.
            int normalizedPermanentFlags = NormalizeRemoteObjectCreationFlags(command);
            if (normalizedPermanentFlags > 0)
                SyncLog.Trace(LogTopic.Buildings, "object operation normalized permanent flags=" +
                    normalizedPermanentFlags);

            string unsafePrefab;
            if (TryFindUnsafeSimulationReference(command, out unsafePrefab))
            {
                RecordRefused(unsafePrefab);
                SyncLog.Trace(LogTopic.Buildings,
                    "object operation dropped (simulation-only prefab)");
                return NativeObjectResult.Rejected;
            }

            var key = new NativeObjectOperationKey
            {
                Origin = message.OriginPlayerId,
                Operation = command.OperationId,
            };
            if (_recentNativeObjectOperations.Contains(key, now))
            {
                SyncLog.Trace(LogTopic.Buildings, "object operation duplicate suppressed op=" +
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
                        SyncLog.Trace(LogTopic.Buildings, "object operation unresolved op=" +
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
                SyncLog.Trace(LogTopic.Buildings, "object equivalent placement suppressed op=" +
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
                SyncLog.Warn(LogTopic.Buildings,
                    "BuildSync: native object definitions could not be generated; " +
                    "requesting world recovery: " + ex.Message);
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("native object definitions could not be generated", "object",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("object definition generation")
                    .Tried("tore the partial definitions down, so nothing inconsistent was committed"));
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
            SyncLog.Trace(LogTopic.Buildings, "object definitions generated op=" +
                command.OperationId + " defs=" + created.Count + " resolveMS=" +
                (isolateStartTick - resolveStartTick) + " isolateMS=" +
                (generateStartTick - isolateStartTick) + " generateMS=" +
                (System.Environment.TickCount - generateStartTick));
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
        /// Re-run an ordinary rooted placement from the object tool's inputs. A finished service
        /// building batch can contain road-alignment and driveway definitions which identify the
        /// sender's exact edge subdivision. Resolving those definitions one-for-one is impossible
        /// when the receiver has an equivalent road split into different entities; regenerating the
        /// batch from the snapped local edge avoids that accidental dependency. Composite
        /// specialized industries retain their complete captured graph below.
        /// </summary>
        private bool TryRealizePlacementInput(SimulationCommandMessage message,
            ObjectToolOperationCommand command, NativeObjectOperationKey key, long now,
            out NativeObjectResult result)
        {
            result = NativeObjectResult.Rejected;
            if (!command.HasPlacementInput) return false;

            // A specialized-industry preview carries attachment selection, lot-surface and access
            // definitions which are not recoverable from its single placement control point. The
            // compact generator consequently produces only the placeholder and area. Replay the
            // complete captured graph below so the visible building and its owned topology commit
            // as the same transaction.
            if (IsSpecializedIndustryPlacement(command))
            {
                SyncLog.Trace(LogTopic.Buildings,
                    "specialized placement using complete captured graph defs=" +
                    command.Definitions.Length);
                return false;
            }

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
                SyncLog.Trace(LogTopic.Buildings, "derived placement equivalent suppressed op=" +
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
                    SyncLog.Trace(LogTopic.Buildings, "building placement regenerated op=" +
                        command.OperationId + " prefab=" + root.PrefabName);
                    result = NativeObjectResult.Armed;
                    return true;
                case NativeDeriveResult.Busy:
                    result = NativeObjectResult.Retry;
                    return true;
                case NativeDeriveResult.Unsupported:
                    SyncLog.Trace(LogTopic.Buildings,
                        "building placement generator unavailable; using exact graph fallback");
                    return false;
                case NativeDeriveResult.Failed:
                    // The complete captured graph is still present in the command. A transient
                    // local generator rejection must not discard the building before trying it.
                    SyncLog.Trace(LogTopic.Buildings,
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
    }
}
