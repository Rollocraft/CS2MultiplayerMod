using Game.Buildings;
using Game.Objects;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        /// <summary>
        /// Announces buildings the zoning simulation grew this frame. Runs on the host only: a
        /// client's own spawner is held (see Authority.cs), so nothing it creates could be its own
        /// decision, and one-way traffic is what makes a create/remove feedback loop impossible.
        /// </summary>
        private void CaptureCreated(MultiplayerSession session, long now)
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

                    string name = PrefabIndexSafeName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    global::Game.Objects.Transform transform =
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);

                    // The variant the building renders as. It is drawn from this machine's random
                    // stream at creation, so a peer that rebuilds the same prefab without it gets
                    // the right building in the wrong style.
                    ushort seed = EntityManager.HasComponent<global::Game.Common.PseudoRandomSeed>(entity)
                        ? EntityManager.GetComponentData<global::Game.Common.PseudoRandomSeed>(entity).m_Seed
                        : (ushort)0;

                    byte flags = 0;
                    if (EntityManager.HasComponent<UnderConstruction>(entity))
                        flags |= GrowableLifecycleCommand.FlagUnderConstruction;

                    var command = new GrowableLifecycleCommand
                    {
                        Op = GrowableLifecycleCommand.OpSpawn,
                        PrefabName = name,
                        AnchorX = transform.m_Position.x,
                        AnchorY = transform.m_Position.y,
                        AnchorZ = transform.m_Position.z,
                        RotX = transform.m_Rotation.value.x,
                        RotY = transform.m_Rotation.value.y,
                        RotZ = transform.m_Rotation.value.z,
                        RotW = transform.m_Rotation.value.w,
                        RandomSeed = seed,
                        Flags = flags,
                        Condition = CaptureCondition(entity),
                        StateFlags = CaptureStateFlags(entity),
                    };
                    Send(session, command);
                    _sentSpawn++;
                    Mod.Verbose("[MP] GrowableSync capture: grew '" + name + "' at " +
                                Format(transform.m_Position) + " seed=" + seed + " seq=" +
                                command.Sequence + ".");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Announces buildings the simulation retired - condemned by a zoning change, abandoned
        /// then destroyed, or collapsed. A bulldoze is not one of these: that is a player action
        /// and already travels as a delete command, so re-sending it here would remove the same
        /// building twice.
        /// </summary>
        private void CaptureRemoved(MultiplayerSession session, long now)
        {
            if (_deletedBuildings.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _deletedBuildings.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    if (!IsAutonomousGrowable(entity, now)) continue;
                    if (_deleteSync != null && _deleteSync.IsToolDeleteOriginal(entity)) continue;

                    string name = PrefabIndexSafeName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    global::Game.Objects.Transform transform =
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
                    _announcedLevelChange.Remove(entity);

                    var command = new GrowableLifecycleCommand
                    {
                        Op = GrowableLifecycleCommand.OpRemove,
                        PrefabName = name,
                        AnchorX = transform.m_Position.x,
                        AnchorY = transform.m_Position.y,
                        AnchorZ = transform.m_Position.z,
                    };
                    Send(session, command);
                    _sentRemove++;
                    Mod.Verbose("[MP] GrowableSync capture: retired '" + name + "' at " +
                                Format(transform.m_Position) + " seq=" + command.Sequence + ".");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Announces level changes. A building levels up by being handed the prefab it is about to
        /// become, and that prefab is picked from this machine's random stream out of every
        /// candidate that fits the lot - so the peer has to be told which one, not just that a
        /// level change happened.
        ///
        /// Polled rather than captured from a Created frame: the marker is added to a building that
        /// already exists, so there is no one frame to catch it on. The query only holds buildings
        /// currently under construction, which is a handful even in a large city.
        /// </summary>
        private void CaptureLevelChanges(MultiplayerSession session, long now)
        {
            if (_announcedLevelChange.Count > 0) PruneAnnouncedLevelChanges();
            if (_levelChanging.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _levelChanging.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity newPrefab = EntityManager.GetComponentData<UnderConstruction>(entity).m_NewPrefab;

                    // A freshly grown building is also under construction, but with no replacement
                    // prefab. Its spawn command already carried everything the peer needs.
                    if (newPrefab == Entity.Null) continue;
                    if (!IsAutonomousGrowable(entity, now)) continue;
                    if (!IsGrowablePrefab(newPrefab)) continue;

                    Entity announced;
                    if (_announcedLevelChange.TryGetValue(entity, out announced) &&
                        announced == newPrefab) continue;

                    string name = PrefabIndexSafeName(newPrefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    if (_announcedLevelChange.Count >= MaxTrackedLevelChanges)
                    {
                        // Only reachable if buildings are levelling faster than they finish. Drop
                        // the memory rather than the cap: a repeat announcement is idempotent on
                        // the receiver, an unbounded dictionary is not recoverable.
                        Mod.log.Warn("[MP] GrowableSync: level-change memory hit " +
                                     MaxTrackedLevelChanges + " entries and was cleared; " +
                                     "some level changes may be announced twice.");
                        _announcedLevelChange.Clear();
                    }
                    _announcedLevelChange[entity] = newPrefab;

                    global::Game.Objects.Transform transform =
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
                    var command = new GrowableLifecycleCommand
                    {
                        Op = GrowableLifecycleCommand.OpLevel,
                        PrefabName = name,
                        AnchorX = transform.m_Position.x,
                        AnchorY = transform.m_Position.y,
                        AnchorZ = transform.m_Position.z,
                        Condition = CaptureCondition(entity),
                        StateFlags = CaptureStateFlags(entity),
                    };
                    Send(session, command);
                    _sentLevel++;
                    Mod.Verbose("[MP] GrowableSync capture: level change to '" + name + "' at " +
                                Format(transform.m_Position) + " seq=" + command.Sequence + ".");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>Forgets buildings that finished levelling, so a later change is announced again.</summary>
        private void PruneAnnouncedLevelChanges()
        {
            _staleLevelChanges.Clear();
            foreach (System.Collections.Generic.KeyValuePair<Entity, Entity> pair in _announcedLevelChange)
            {
                Entity entity = pair.Key;
                if (!EntityManager.Exists(entity) ||
                    !EntityManager.HasComponent<UnderConstruction>(entity) ||
                    EntityManager.GetComponentData<UnderConstruction>(entity).m_NewPrefab != pair.Value)
                    _staleLevelChanges.Add(entity);
            }
            for (int i = 0; i < _staleLevelChanges.Count; i++)
                _announcedLevelChange.Remove(_staleLevelChanges[i]);
            _staleLevelChanges.Clear();
        }

        private string PrefabIndexSafeName(Entity prefab) =>
            PrefabIndex.SafeName(_prefabSystem, prefab);

        private static string Format(Unity.Mathematics.float3 position) =>
            "(" + position.x.ToString("F1") + ", " + position.y.ToString("F1") + ", " +
            position.z.ToString("F1") + ")";
    }
}
