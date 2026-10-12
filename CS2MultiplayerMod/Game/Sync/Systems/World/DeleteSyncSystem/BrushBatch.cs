using System.Collections.Generic;
using Game.Objects;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class DeleteSyncSystem
    {
        private bool TrySendObjectBrushDeletes(MultiplayerSession session, long now,
            EntityQuery query)
        {
            BuildSyncSystem buildSync = World.GetExistingSystemManaged<BuildSyncSystem>();
            if (buildSync == null || !buildSync.LocalObjectBrushAppliedThisFrame) return false;

            var batches = new Dictionary<string, List<ObjectDeleteBatchCommand.Position>>();
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    bool playerRemoved = _toolDeleteOriginals.Contains(entity) &&
                                         Mod.Service != null &&
                                         Mod.Service.SimulationSyncEnabled;
                    if (IsSimulationOwnedLifecycle(prefab) && !playerRemoved) continue;

                    Transform transform = EntityManager.GetComponentData<Transform>(entity);
                    if (_guard.Consume(DeleteKey(name, transform.m_Position), now)) continue;

                    if (!batches.TryGetValue(name, out List<ObjectDeleteBatchCommand.Position> positions))
                    {
                        positions = new List<ObjectDeleteBatchCommand.Position>();
                        batches[name] = positions;
                    }
                    positions.Add(new ObjectDeleteBatchCommand.Position
                    {
                        X = transform.m_Position.x,
                        Y = transform.m_Position.y,
                        Z = transform.m_Position.z,
                    });
                }
            }
            finally { entities.Dispose(); }

            int sent = 0;
            foreach (KeyValuePair<string, List<ObjectDeleteBatchCommand.Position>> batch in batches)
            {
                for (int offset = 0; offset < batch.Value.Count;
                     offset += ObjectDeleteBatchCommand.MaxDeletes)
                {
                    int count = System.Math.Min(ObjectDeleteBatchCommand.MaxDeletes,
                        batch.Value.Count - offset);
                    var chunk = new ObjectDeleteBatchCommand.Position[count];
                    batch.Value.CopyTo(offset, chunk, 0, count);
                    var command = new ObjectDeleteBatchCommand
                    {
                        PrefabName = batch.Key,
                        Positions = chunk,
                    };
                    session.SendCommand(0, ObjectDeleteBatchCommand.Id, command.Encode());
                    sent += count;
                }
            }

            SyncLog.Trace(LogTopic.Buildings, "object delete brush captured batches=" +
                batches.Count + " deletes=" + sent);
            return true;
        }

        private static void AppendObjectDeleteBatch(ObjectDeleteBatchCommand batch, long deadline,
            ref List<(ObjectDeleteCommand cmd, long deadline)> objects)
        {
            if (objects == null)
                objects = new List<(ObjectDeleteCommand, long)>(batch.Positions.Length);
            for (int i = 0; i < batch.Positions.Length; i++)
            {
                ObjectDeleteBatchCommand.Position position = batch.Positions[i];
                objects.Add((new ObjectDeleteCommand
                {
                    PrefabName = batch.PrefabName,
                    PosX = position.X,
                    PosY = position.Y,
                    PosZ = position.Z,
                }, deadline));
            }
        }
    }
}
