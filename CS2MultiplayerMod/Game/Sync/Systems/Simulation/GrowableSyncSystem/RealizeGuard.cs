using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        /// <summary>The definition becomes an entity later, so our building is recognised by position.</summary>
        private void NoteSelfRealized(Entity prefab, float3 position,
            GrowableLifecycleCommand command, long now)
        {
            if (_selfRealized.Count >= MaxSelfRealized) _selfRealized.RemoveAt(0);
            _selfRealized.Add(new PendingRealizedSpawn
            {
                Prefab = prefab,
                Position = position,
                Expiry = now + SelfRealizedWindowMs,
                Command = command,
            });
        }

        private bool TryTakeSelfRealized(Entity prefab, float3 position, long now,
            out GrowableLifecycleCommand command)
        {
            command = null;
            for (int i = _selfRealized.Count - 1; i >= 0; i--)
            {
                PendingRealizedSpawn entry = _selfRealized[i];
                if (entry.Expiry <= now) { _selfRealized.RemoveAt(i); continue; }
                if (entry.Prefab != prefab ||
                    math.distancesq(entry.Position.xz, position.xz) >
                    AnchorMatchDistance * AnchorMatchDistance) continue;
                command = entry.Command;
                _selfRealized.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Removes zoned buildings this client grew itself, e.g. while authority was briefly returned
        /// during a resync. On a client every zoned building comes from the host.
        /// </summary>
        private void RejectLocallyGrownBuildings(long now)
        {
            if (_createdBuildings.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _createdBuildings.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!IsAutonomousGrowable(entity, now)) continue;

                    global::Game.Objects.Transform transform = EntityManager
                        .GetComponentData<global::Game.Objects.Transform>(entity);
                    float3 position = transform.m_Position;
                    if (TryTakeSelfRealized(prefab, position, now, out GrowableLifecycleCommand command))
                    {
                        // Now a real building: the first safe point for its clock and state.
                        ApplyConditionAndState(entity, command);
                        if (_buildSync != null)
                            _buildSync.TrackRemoteBuilding(entity, prefab, position,
                                transform.m_Rotation,
                                roadConnectionExpected: true, source: "growable");
                        continue;
                    }

                    EntityManager.AddComponent<Deleted>(entity);
                    _rejectedLocal++;
                    SyncLog.Warn(LogTopic.Buildings, "GrowableSync: this client grew '" +
                        PrefabIndexSafeName(prefab) + "' at " + Format(position) +
                        " on its own; removed (the host decides zoned buildings).");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }
    }
}
