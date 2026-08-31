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
    // Resolving a remote operation against local entities: claiming the original it edits,
    // matching each definition's target, and replaying or completing the operation once done.
    public partial class BuildSyncSystem
    {
        private void ReplayNativeObject(SimulationCommandMessage message)
        {
            if (_nativeObjectReplayPrefix.Count >= MaxNativeObjectReplayPrefix)
            {
                Mod.log.Warn("[MP] BuildSync: native object replay prefix overflowed; requesting " +
                             "world recovery instead of losing an operation.");
                Diagnostics.FlightRecorder.Note("object replay prefix overflow; recovery requested");
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("native object replay prefix overflow", "object",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                    .About("object replay prefix")
                    .Tried("nothing - the replay prefix was full and the operation could not be kept"));
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
    }
}
