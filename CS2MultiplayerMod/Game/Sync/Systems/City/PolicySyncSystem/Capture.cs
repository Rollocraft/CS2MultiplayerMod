using System.Collections.Generic;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class PolicySyncSystem
    {
        private void Scan(MultiplayerSession session, long now)
        {
            _next.Clear();
            ScanKind(session, now, _districts, EntityPolicyCommand.KindDistrict);
            ScanKind(session, now, _routes, EntityPolicyCommand.KindRoute);
            ScanKind(session, now, _buildings, EntityPolicyCommand.KindBuilding);
            // Disabling a service upgrade is the "Out of Service" policy on the owned upgrade entity.
            ScanKind(session, now, _ownedUpgrades, EntityPolicyCommand.KindBuilding,
                playerUpgradesOnly: true);

            // Swap: entities that vanished fall out of the cache automatically.
            var swap = _known;
            _known = _next;
            _next = swap;
            // The very first scan of a session only records — both machines already
            // share the same state (defaults or the streamed world), so nothing to send.
            _primed = true;
        }

        private void ScanKind(MultiplayerSession session, long now, EntityQuery query, byte kind,
            bool playerUpgradesOnly = false)
        {
            if (query.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    // Both tool entry points for an upgrade require ServiceUpgradeData, so anything
                    // else parented to a building is lot content the simulation spawns and despawns
                    // (a storage yard's container piles) - never a player's on/off decision.
                    if (playerUpgradesOnly &&
                        !EntityManager.HasComponent<ServiceUpgradeData>(prefab)) continue;
                    string targetName = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(targetName)) continue;

                    float3 anchor;
                    if (!TryAnchor(kind, entity, out anchor)) continue;

                    var current = playerUpgradesOnly
                        ? ReadUpgradePolicies(entity)
                        : ReadPolicies(entity);
                    List<PolicyEntry> old;
                    bool had = _known.TryGetValue(entity, out old);
                    _next[entity] = current;
                    if (!had || !_primed) continue;

                    EmitDiff(session, now, kind, targetName, anchor, old, current);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void EmitDiff(MultiplayerSession session, long now, byte kind, string targetName,
                              float3 anchor, List<PolicyEntry> old, List<PolicyEntry> current)
        {
            for (int c = 0; c < current.Count; c++)
            {
                PolicyEntry entry = current[c];
                bool changed = true;
                for (int o = 0; o < old.Count; o++)
                {
                    if (old[o].Policy != entry.Policy) continue;
                    changed = old[o].Active != entry.Active || old[o].Adjustment != entry.Adjustment;
                    break;
                }
                if (changed) Send(session, now, kind, targetName, anchor, entry.Policy, entry.Active, entry.Adjustment);
            }

            // A policy entry that disappeared entirely counts as "switched off".
            for (int o = 0; o < old.Count; o++)
            {
                if (!old[o].Active) continue;
                bool gone = true;
                for (int c = 0; c < current.Count; c++)
                    if (current[c].Policy == old[o].Policy) { gone = false; break; }
                if (gone) Send(session, now, kind, targetName, anchor, old[o].Policy, false, old[o].Adjustment);
            }
        }

        private void Send(MultiplayerSession session, long now, byte kind, string targetName,
                          float3 anchor, Entity policy, bool active, float adjustment)
        {
            string policyName = _prefabSystem.GetPrefabName(policy);
            if (string.IsNullOrEmpty(policyName)) return;

            // The change we are looking at may simply be a remote command we just applied.
            if (_guard.Consume(PolicyKey(policyName, targetName, anchor), now)) return;

            var command = new EntityPolicyCommand
            {
                TargetKind = kind,
                TargetPrefabName = targetName,
                AnchorX = anchor.x, AnchorY = anchor.y, AnchorZ = anchor.z,
                PolicyPrefabName = policyName,
                Active = active,
                Adjustment = adjustment,
            };
            session.SendCommand(0, EntityPolicyCommand.Id, command.Encode());
            Mod.Verbose("[MP] PolicySync captured '" + policyName + "' (" + (active ? "on" : "off") +
                         ", " + adjustment + ") on " + KindName(kind) + " '" + targetName + "'.");
        }

    }
}
