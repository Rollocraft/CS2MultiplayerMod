using System.Collections.Generic;
using Game.Areas;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class PolicySyncSystem
    {
        private void ApplyIncoming(MultiplayerSession session, long now)
        {
            for (int i = 0; i < _targetRetry.Count;)
            {
                var pending = _targetRetry[i];
                if (TryApplyPolicy(pending.cmd, pending.origin, now))
                {
                    _targetRetry.RemoveAt(i);
                    continue;
                }
                if (now >= pending.deadline)
                {
                    Mod.log.Warn("[MP] PolicySync: no local " +
                                 KindName(pending.cmd.TargetKind) + " '" +
                                 pending.cmd.TargetPrefabName + "' appeared within " +
                                 (TargetRetryWindowMs / 1000) + " s for policy '" +
                                 pending.cmd.PolicyPrefabName +
                                 "'; requesting world recovery.");
                    SyncInbox.RequestResync("policy target did not resolve");
                    _targetRetry.RemoveAt(i);
                    continue;
                }
                i++;
            }

            SimulationCommandMessage message;
            while (_incoming.TryDequeue(out message))
            {
                if (message.OriginPlayerId == session.LocalPlayerId) continue;

                EntityPolicyCommand command;
                try { command = EntityPolicyCommand.Decode(message.Body); }
                catch (System.Exception ex) { Mod.log.Warn("[MP] PolicySync: dropping malformed command: " + ex.Message); continue; }

                if (!TryApplyPolicy(command, message.OriginPlayerId, now))
                    QueuePolicyRetry(command, message.OriginPlayerId, now);
            }
        }

        /// <summary>
        /// Returns false only when the target can still appear after an ordered building/route
        /// transaction. Unknown policy prefabs and application failures are hard drops.
        /// </summary>
        private bool TryApplyPolicy(EntityPolicyCommand command, int origin, long now)
        {
            Entity policy;
            if (!_prefabIndex.TryResolve(command.PolicyPrefabName, out policy))
            {
                Mod.log.Warn("[MP] PolicySync: unknown policy '" +
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
                Mod.Verbose("[MP] PolicySync realize: '" + command.PolicyPrefabName + "' " +
                             (command.Active ? "on" : "off") + " for " +
                             KindName(command.TargetKind) + " '" +
                             command.TargetPrefabName + "' from player " + origin + ".");
            }
            catch (System.Exception ex)
            {
                Mod.log.Error("[MP] PolicySync realize FAILED for '" +
                              command.PolicyPrefabName + "': " + ex);
                SyncInbox.RequestResync("building policy application failed");
            }
            return true;
        }

        private void QueuePolicyRetry(EntityPolicyCommand command, int origin, long now)
        {
            string key = PendingPolicyKey(command);
            for (int i = 0; i < _targetRetry.Count; i++)
            {
                if (PendingPolicyKey(_targetRetry[i].cmd) != key) continue;
                // Only the newest state matters while its target is absent.
                _targetRetry[i] = (command, origin, now + TargetRetryWindowMs);
                return;
            }
            if (_targetRetry.Count >= MaxPendingTargets)
            {
                _targetRetry.RemoveAt(0);
                Mod.log.Warn("[MP] PolicySync: pending-target queue reached its bounded limit; " +
                             "requesting world recovery.");
                SyncInbox.RequestResync("policy target retry queue overflow");
            }
            _targetRetry.Add((command, origin, now + TargetRetryWindowMs));
            Diagnostics.FlightRecorder.Note("policy target retrying kind=" +
                                              KindName(command.TargetKind) +
                                              " prefab=" + command.TargetPrefabName);
        }

        private static string PendingPolicyKey(EntityPolicyCommand command) =>
            command.TargetKind + "|" + command.PolicyPrefabName + "|" +
            PolicyKey(command.PolicyPrefabName, command.TargetPrefabName,
                new float3(command.AnchorX, command.AnchorY, command.AnchorZ));

        private Entity FindTarget(byte kind, string prefabName, float3 anchor)
        {
            Entity prefab;
            if (!_prefabIndex.TryResolve(prefabName, out prefab)) return Entity.Null;

            EntityQuery query = kind == EntityPolicyCommand.KindDistrict ? _districts :
                                kind == EntityPolicyCommand.KindRoute ? _routes : _buildings;
            // Districts can drift further (their centroid moves when redrawn mid-flight).
            float maxSq = kind == EntityPolicyCommand.KindDistrict ? 250000f :
                          kind == EntityPolicyCommand.KindRoute ? 256f : 16f;

            Entity best = Entity.Null;
            float bestSq = maxSq;
            SearchTargets(query, prefab, kind, anchor, ref best, ref bestSq);
            // An owned service upgrade shares the building kind; try those too when no top-level
            // building answers (see the query's comment in PolicySyncSystem).
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
                    float3 candidate;
                    if (!TryAnchor(kind, entities[i], out candidate)) continue;
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
        /// An owned upgrade's on/off state, read where the game itself reads it.
        ///
        /// Turning an extension off goes through the "Out of Service" policy, but the resulting state
        /// does not live in the entity's <see cref="Policy"/> buffer - the building's own properties
        /// panel reads <c>Extension.m_Flags</c> / <c>Building.m_OptionMask</c>, and those are what the
        /// simulation acts on. Diffing the buffer therefore never saw the toggle at all. The synthetic
        /// entry below puts that flag into the same shape as a real policy so one diff covers both.
        /// </summary>
        private List<PolicyEntry> ReadUpgradePolicies(Entity entity)
        {
            List<PolicyEntry> policies = ReadPolicies(entity);
            Entity outOfService = OutOfServicePolicy();
            if (outOfService == Entity.Null) return policies;

            // The flag is reported unconditionally, including while the upgrade is destroyed or
            // burning (the simulation switches it off then). Both machines derive that state from
            // their own copy, so at worst each sends one redundant command that the other applies as
            // a no-op. Dropping the entry instead would read as "it disappeared" - which the diff
            // reports as switched off, silently re-enabling it on the peer.
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

        /// <summary>
        /// The shared "Out of Service" policy prefab, resolved by name the same way the building's
        /// properties panel resolves it.
        /// </summary>
        private Entity OutOfServicePolicy()
        {
            if (_outOfServicePolicy != Entity.Null &&
                EntityManager.Exists(_outOfServicePolicy)) return _outOfServicePolicy;
            Entity policy;
            _outOfServicePolicy = _prefabIndex.TryResolve(OutOfServicePolicyName, out policy)
                ? policy
                : Entity.Null;
            return _outOfServicePolicy;
        }

        private List<PolicyEntry> ReadPolicies(Entity entity)
        {
            // No buffer yet is a real state, not an error: it is what an upgrade looks like before it
            // is ever toggled, and comparing against it is what makes the first toggle replicate.
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
