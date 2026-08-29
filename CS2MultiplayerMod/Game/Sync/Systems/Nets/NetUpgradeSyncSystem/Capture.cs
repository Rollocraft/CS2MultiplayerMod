using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Session;

using CS2MultiplayerMod.Game.Sync.Commands;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Noticing an in-place composition change. There is no event for one, so each upgraded and
    // bare edge and node is compared against what it looked like last update, and a difference
    // becomes a command.
    public partial class NetUpgradeSyncSystem
    {
        // ---------------------------------------------------------------- capture

        private void CaptureEdgeUpgrades(MultiplayerSession session)
        {
            if (_upgradedEdges.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _upgradedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;

                    Bezier4x3 b = EntityManager.GetComponentData<Curve>(entity).m_Bezier;
                    CompositionFlags flags = EntityManager.GetComponentData<Upgraded>(entity).m_Flags;
                    NetUpgradeCommand.SubRep[] subs = ReadSubReplacements(entity);
                    var current = new SeenState
                    {
                        General = (uint)flags.m_General,
                        Left = (uint)flags.m_Left,
                        Right = (uint)flags.m_Right,
                        SubRepSig = SubRepSig(subs),
                    };

                    // Roads get Updated for many reasons (neighbour edits, traffic) - only
                    // an actual composition change for this segment is worth broadcasting.
                    // The cache is also written on apply, which suppresses the echo.
                    string key = EdgeKey(b.a, b.d);
                    SeenState last;
                    if (_lastSeen.TryGetValue(key, out last) && last.Equals(current)) continue;
                    _lastSeen[key] = current;

                    var command = new NetUpgradeCommand
                    {
                        PrefabName = name,
                        Ax = b.a.x, Ay = b.a.y, Az = b.a.z,
                        Dx = b.d.x, Dy = b.d.y, Dz = b.d.z,
                        General = current.General, Left = current.Left, Right = current.Right,
                        SubReps = subs,
                    };
                    session.SendCommand(0, NetUpgradeCommand.Id, command.Encode());
                    Mod.Verbose("[MP] NetUpgradeSync captured upgrade on '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureEdgeClears(MultiplayerSession session)
        {
            if (_lastSeen.Count == 0 || _bareEdges.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _bareEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Bezier4x3 b = EntityManager.GetComponentData<Curve>(entity).m_Bezier;
                    string key = EdgeKey(b.a, b.d);
                    SeenState last;
                    if (!_lastSeen.TryGetValue(key, out last) || last.IsCleared) continue;

                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;
                    _lastSeen[key] = default(SeenState);

                    var command = new NetUpgradeCommand
                    {
                        PrefabName = name,
                        Ax = b.a.x, Ay = b.a.y, Az = b.a.z,
                        Dx = b.d.x, Dy = b.d.y, Dz = b.d.z,
                    };
                    session.SendCommand(0, NetUpgradeCommand.Id, command.Encode());
                    Mod.Verbose("[MP] NetUpgradeSync captured upgrade REMOVAL on '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureNodeUpgrades(MultiplayerSession session)
        {
            if (_upgradedNodes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _upgradedNodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;

                    float3 pos = EntityManager.GetComponentData<Node>(entity).m_Position;
                    CompositionFlags flags = EntityManager.GetComponentData<Upgraded>(entity).m_Flags;
                    var current = new SeenState
                    {
                        General = (uint)flags.m_General,
                        Left = (uint)flags.m_Left,
                        Right = (uint)flags.m_Right,
                        SubRepSig = "",
                    };

                    string key = NodeKey(pos);
                    SeenState last;
                    if (_lastSeen.TryGetValue(key, out last) && last.Equals(current)) continue;
                    _lastSeen[key] = current;

                    var command = new NetUpgradeCommand
                    {
                        PrefabName = name,
                        Ax = pos.x, Ay = pos.y, Az = pos.z,
                        Dx = pos.x, Dy = pos.y, Dz = pos.z,
                        General = current.General, Left = current.Left, Right = current.Right,
                        IsNode = true,
                    };
                    session.SendCommand(0, NetUpgradeCommand.Id, command.Encode());
                    Mod.Verbose("[MP] NetUpgradeSync captured node upgrade at '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureNodeClears(MultiplayerSession session)
        {
            if (_lastSeen.Count == 0 || _bareNodes.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _bareNodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    float3 pos = EntityManager.GetComponentData<Node>(entity).m_Position;
                    string key = NodeKey(pos);
                    SeenState last;
                    if (!_lastSeen.TryGetValue(key, out last) || last.IsCleared) continue;

                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;
                    _lastSeen[key] = default(SeenState);

                    var command = new NetUpgradeCommand
                    {
                        PrefabName = name,
                        Ax = pos.x, Ay = pos.y, Az = pos.z,
                        Dx = pos.x, Dy = pos.y, Dz = pos.z,
                        IsNode = true,
                    };
                    session.SendCommand(0, NetUpgradeCommand.Id, command.Encode());
                    Mod.Verbose("[MP] NetUpgradeSync captured node upgrade REMOVAL at '" + name + "'.");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }
    }
}
