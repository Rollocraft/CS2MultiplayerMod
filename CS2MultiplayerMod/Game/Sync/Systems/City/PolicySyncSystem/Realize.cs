using System.Collections.Generic;
using Game.Areas;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
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
    public partial class PolicySyncSystem
    {
        private void ApplyIncoming(MultiplayerSession session, long now)
        {
            _targetRetry.Observe(now, RealizeGate.WorldBuildingHeld);
            _targetRetry.Pump(pending => TryApplyPolicy(pending.cmd, pending.origin, now),
                pending => SyncInbox.RequestResync(ResyncReport
                    .Create("policy target did not resolve", "policy", ResyncEvidence.MissingTarget)
                    .About("'" + pending.cmd.PolicyPrefabName + "' on " +
                           KindName(pending.cmd.TargetKind) + " '" + pending.cmd.TargetPrefabName + "'")
                    .Tried("retried for 15 s of eligible time, excluding dependency holds")));

            while (_incoming.TryDequeue(out SimulationCommandMessage message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                if (!CommandDecode.TryDecode(message, EntityPolicyCommand.Decode, LogTopic.City,
                        "PolicySync", out EntityPolicyCommand command))
                    continue;

                if (!TryApplyPolicy(command, message.OriginPlayerId, now))
                    QueuePolicyRetry(command, message.OriginPlayerId);
                else _targetRetry.Remove(PendingPolicyKey(command));
            }
        }

        /// <summary>False only while the target can still appear; unknown policies and failures are dropped.</summary>
        private bool TryApplyPolicy(EntityPolicyCommand command, int origin, long now)
        {
            if (!_prefabIndex.TryResolve(command.PolicyPrefabName, out Entity policy))
            {
                SyncLog.Warn(LogTopic.City, "PolicySync: unknown policy '" +
                    command.PolicyPrefabName + "'; skipping.");
                return true;
            }

            var anchor = new float3(command.AnchorX, command.AnchorY, command.AnchorZ);
            Entity target = FindTarget(command.TargetKind, command.TargetPrefabName, anchor);
            if (target == Entity.Null) return false;

            _guard.Mark(PolicyKey(command.PolicyPrefabName, command.TargetPrefabName, anchor), now);
            try
            {
                _policiesUI.SetPolicy(target, policy, command.Active, command.Adjustment);
                SyncLog.Detail(LogTopic.City, "PolicySync realize: '" + command.PolicyPrefabName +
                    "' " + (command.Active ? "on" : "off") + " for " + KindName(command.TargetKind) +
                    " '" + command.TargetPrefabName + "' from player " + origin + ".");
            }
            catch (System.Exception ex)
            {
                SyncLog.Error(LogTopic.City, "PolicySync realize FAILED for '" +
                    command.PolicyPrefabName + "': " + ex);
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("building policy application failed", "policy",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.Contradiction)
                    .About("policy application")
                    .Tried("nothing - applying the policy threw"));
            }
            return true;
        }

        private void QueuePolicyRetry(EntityPolicyCommand command, int origin)
        {
            if (!_targetRetry.SetLatest(PendingPolicyKey(command), (command, origin)))
            {
                SyncLog.Warn(LogTopic.City,
                    "PolicySync: pending-target queue reached its bounded limit; " +
                    "requesting world recovery.");
                SyncInbox.RequestResync(CS2MultiplayerMod.Game.Diagnostics.ResyncReport
                    .Create("policy target retry queue overflow", "policy",
                        CS2MultiplayerMod.Game.Diagnostics.ResyncEvidence.StreamLoss)
                    .About("policy retry queue")
                    .Tried("nothing - the oldest queued policy was shed to stay within the bound"));
            }
            SyncLog.Trace(LogTopic.City, "policy target retrying kind=" +
                KindName(command.TargetKind) + " prefab=" + command.TargetPrefabName);
        }

        private static string PendingPolicyKey(EntityPolicyCommand command) =>
            command.TargetKind + "|" + command.PolicyPrefabName + "|" +
            PolicyKey(command.PolicyPrefabName, command.TargetPrefabName,
                new float3(command.AnchorX, command.AnchorY, command.AnchorZ));

        private Entity FindTarget(byte kind, string prefabName, float3 anchor)
        {
            if (!_prefabIndex.TryResolve(prefabName, out Entity prefab)) return Entity.Null;

            EntityQuery query = kind == EntityPolicyCommand.KindDistrict ? _districts :
                                kind == EntityPolicyCommand.KindRoute ? _routes : _buildings;
            // Districts can drift further (their centroid moves when redrawn mid-flight).
            float maxSq = kind == EntityPolicyCommand.KindDistrict ? 250000f :
                          kind == EntityPolicyCommand.KindRoute ? 256f : 16f;

            Entity best = Entity.Null;
            float bestSq = maxSq;
            SearchTargets(query, prefab, kind, anchor, ref best, ref bestSq);
            // Owned upgrades share the building kind (see the query in PolicySyncSystem).
            if (best == Entity.Null && kind == EntityPolicyCommand.KindBuilding)
                SearchTargets(_ownedUpgrades, prefab, kind, anchor, ref best, ref bestSq);
            return best;
        }

        private void SearchTargets(EntityQuery query, Entity prefab, byte kind, float3 anchor,
            ref Entity best, ref float bestSq)
        {
            if (query.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab != prefab) continue;
                    if (!TryAnchor(kind, entities[i], out float3 candidate)) continue;
                    float d = math.distancesq(candidate, anchor);
                    if (d > bestSq) continue;
                    bestSq = d;
                    best = entities[i];
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>Cross-machine identity per target kind (entity ids differ per machine).</summary>
        private bool TryAnchor(byte kind, Entity entity, out float3 anchor)
        {
            anchor = default;
            switch (kind)
            {
                case EntityPolicyCommand.KindDistrict:
                {
                    DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(entity, true);
                    if (nodes.Length == 0) return false;
                    float3 sum = float3.zero;
                    for (int i = 0; i < nodes.Length; i++) sum += nodes[i].m_Position;
                    anchor = sum / nodes.Length;
                    anchor.y = 0f;
                    return true;
                }
                case EntityPolicyCommand.KindRoute:
                {
                    if (!EntityManager.HasBuffer<RouteWaypoint>(entity)) return false;
                    DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(entity, true);
                    if (waypoints.Length == 0 ||
                        !EntityManager.HasComponent<Position>(waypoints[0].m_Waypoint)) return false;
                    anchor = EntityManager.GetComponentData<Position>(waypoints[0].m_Waypoint).m_Position;
                    return true;
                }
                default:
                {
                    anchor = EntityManager.GetComponentData<global::Game.Objects.Transform>(entity).m_Position;
                    return true;
                }
            }
        }

        /// <summary>
        /// An upgrade's on/off state lives in <c>Extension.m_Flags</c> / <c>Building.m_OptionMask</c>, not
        /// the policy buffer; a synthetic entry puts it in the same diff.
        /// </summary>
        private List<PolicyEntry> ReadUpgradePolicies(Entity entity)
        {
            List<PolicyEntry> policies = ReadPolicies(entity);
            Entity outOfService = OutOfServicePolicy();
            if (outOfService == Entity.Null) return policies;

            // Reported even while destroyed or burning: at worst a redundant no-op, whereas dropping it would
            // read as switched off.
            bool disabled = IsUpgradeDisabled(entity);
            for (int i = 0; i < policies.Count; i++)
            {
                if (policies[i].Policy != outOfService) continue;
                PolicyEntry existing = policies[i];
                existing.Active = disabled;
                policies[i] = existing;
                return policies;
            }
            policies.Add(new PolicyEntry { Policy = outOfService, Active = disabled });
            return policies;
        }

        private bool IsUpgradeDisabled(Entity entity)
        {
            if (EntityManager.HasComponent<global::Game.Buildings.Extension>(entity))
                return (EntityManager.GetComponentData<global::Game.Buildings.Extension>(entity)
                    .m_Flags & global::Game.Buildings.ExtensionFlags.Disabled) != 0;
            if (EntityManager.HasComponent<global::Game.Buildings.Building>(entity))
                return global::Game.Buildings.BuildingUtils.CheckOption(
                    EntityManager.GetComponentData<global::Game.Buildings.Building>(entity),
                    global::Game.Buildings.BuildingOption.Inactive);
            return false;
        }

        /// <summary>Resolved by name, as the properties panel does.</summary>
        private Entity OutOfServicePolicy()
        {
            if (_outOfServicePolicy != Entity.Null &&
                EntityManager.Exists(_outOfServicePolicy)) return _outOfServicePolicy;
            _outOfServicePolicy = _prefabIndex.TryResolve(OutOfServicePolicyName, out Entity policy)
                ? policy
                : Entity.Null;
            return _outOfServicePolicy;
        }

        private List<PolicyEntry> ReadPolicies(Entity entity)
        {
            // No buffer is the untoggled baseline, which lets the first toggle replicate.
            if (!EntityManager.HasBuffer<Policy>(entity)) return new List<PolicyEntry>(0);

            DynamicBuffer<Policy> buffer = EntityManager.GetBuffer<Policy>(entity, true);
            var list = new List<PolicyEntry>(buffer.Length);
            for (int i = 0; i < buffer.Length; i++)
                list.Add(new PolicyEntry
                {
                    Policy = buffer[i].m_Policy,
                    Active = (buffer[i].m_Flags & PolicyFlags.Active) != 0,
                    Adjustment = buffer[i].m_Adjustment,
                });
            return list;
        }
    }
}
