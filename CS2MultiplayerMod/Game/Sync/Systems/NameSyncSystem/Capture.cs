using System;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class NameSyncSystem
    {
        /// <summary>
        /// Diff the game's typed-name lookup against the last observation. The lookup is a managed
        /// table, updated the moment a player confirms a rename, so this sees a rename whether the
        /// game is running or paused - and one scan covers every kind of name in one place.
        /// </summary>
        private void ScanCustomNames(MultiplayerSession session)
        {
            _seen.Clear();
            if (!_namedEntities.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> entities = _namedEntities.ToEntityArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        string name;
                        if (!_nameSystem.TryGetCustomName(entity, out name) ||
                            string.IsNullOrEmpty(name)) continue;

                        _seen.Add(entity);
                        string previous;
                        if (_knownNames.TryGetValue(entity, out previous) && previous == name) continue;
                        _knownNames[entity] = name;
                        if (_primed) SendCustomName(session, entity, name);
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }

            CollectClearedNames(session);
            // The first scan of a session only records: both machines start from the same state
            // (a fresh map, or the world just streamed from the host), so nothing has changed yet.
            _primed = true;
        }

        /// <summary>
        /// A name that vanished from the lookup was either cleared by a player - which must
        /// replicate - or removed with its entity, which must not: the peer bulldozed the same
        /// thing and has nothing left to rename.
        /// </summary>
        private void CollectClearedNames(MultiplayerSession session)
        {
            _dropped.Clear();
            foreach (var pair in _knownNames)
                if (!_seen.Contains(pair.Key)) _dropped.Add(pair.Key);

            for (int i = 0; i < _dropped.Count; i++)
            {
                Entity entity = _dropped[i];
                bool alive = EntityManager.Exists(entity) &&
                             !EntityManager.HasComponent<Deleted>(entity);
                string current;
                // Still named, just not in the query yet: the marker component is added through a
                // command buffer, so a rename made this frame is invisible here for one frame.
                if (alive && _nameSystem.TryGetCustomName(entity, out current) &&
                    !string.IsNullOrEmpty(current)) continue;

                _knownNames.Remove(entity);
                if (!alive || !_primed) continue;
                SendCustomName(session, entity, string.Empty);
            }
        }

        /// <summary>
        /// Publish the auto-name draw of streets and districts that appeared this frame. Host only:
        /// the draw is a local random roll, so both machines rolling and sending would overwrite
        /// each other forever. The host's roll is the city's.
        /// </summary>
        private void CaptureCreatedAutoNames(MultiplayerSession session)
        {
            if (session.Role != SessionRole.Host) return;
            CaptureAutoNames(session, _createdStreets);
            CaptureAutoNames(session, _createdDistricts);
        }

        private void CaptureAutoNames(MultiplayerSession session, EntityQuery query)
        {
            if (query.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    int[] indices = ReadRandomIndices(entities[i]);
                    if (indices.Length == 0) continue;

                    byte kind;
                    string prefabName;
                    float3 anchor;
                    if (!TryIdentify(entities[i], out kind, out prefabName, out anchor)) continue;

                    Send(session, new EntityNameCommand
                    {
                        TargetKind = kind,
                        TargetPrefabName = prefabName,
                        AnchorX = anchor.x, AnchorY = anchor.y, AnchorZ = anchor.z,
                        SetsCustomName = false,
                        CustomName = string.Empty,
                        RandomIndices = indices,
                    }, "auto-name " + Describe(indices));
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void SendCustomName(MultiplayerSession session, Entity entity, string name)
        {
            byte kind;
            string prefabName;
            float3 anchor;
            if (!TryIdentify(entity, out kind, out prefabName, out anchor))
            {
                // Expected for citizens, vehicles and animals. Naming the prefab keeps the line
                // useful if some other kind of entity ever turns up here.
                Mod.Verbose("[MP] NameSync: '" + name + "' is on '" +
                            (LocalPrefabName(entity) ?? "?") + "', which has no cross-machine " +
                            "identity; not replicated.");
                return;
            }

            // Clamp here rather than at the encoder: a name past the cap should still replicate,
            // shortened, instead of being refused as an oversized command.
            string wire = CS2MultiplayerMod.Core.Protocol.WireGuard.SanitizeText(
                name, EntityNameCommand.MaxCustomNameLength);
            Send(session, new EntityNameCommand
            {
                TargetKind = kind,
                TargetPrefabName = prefabName,
                AnchorX = anchor.x, AnchorY = anchor.y, AnchorZ = anchor.z,
                SetsCustomName = true,
                CustomName = wire,
                RandomIndices = Array.Empty<int>(),
            }, wire.Length == 0 ? "name cleared" : "name '" + wire + "'");
        }

        private void Send(MultiplayerSession session, EntityNameCommand command, string what)
        {
            byte[] body;
            try { body = command.Encode(); }
            catch (Exception ex)
            {
                Mod.log.Warn("[MP] NameSync: could not encode " + what + " for " +
                             KindName(command.TargetKind) + " '" + command.TargetPrefabName +
                             "': " + ex.Message);
                return;
            }
            session.SendCommand(0, EntityNameCommand.Id, body);
            Mod.Verbose("[MP] NameSync captured " + what + " on " + KindName(command.TargetKind) +
                        " '" + command.TargetPrefabName + "'.");
        }

        /// <summary>The entity's current auto-name draw, one index per name slot its prefab has.</summary>
        private int[] ReadRandomIndices(Entity entity)
        {
            if (!EntityManager.HasBuffer<RandomLocalizationIndex>(entity)) return Array.Empty<int>();
            DynamicBuffer<RandomLocalizationIndex> buffer =
                EntityManager.GetBuffer<RandomLocalizationIndex>(entity, true);
            if (buffer.Length == 0 || buffer.Length > EntityNameCommand.MaxRandomIndices)
                return Array.Empty<int>();

            var indices = new int[buffer.Length];
            for (int i = 0; i < buffer.Length; i++) indices[i] = buffer[i].m_Index;
            return indices;
        }

        private string LocalPrefabName(Entity entity) =>
            EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity)
                ? _prefabIndex.NameOf(
                    EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity).m_Prefab)
                : null;

        private static string Describe(int[] indices)
        {
            if (indices == null || indices.Length == 0) return "(none)";
            string text = indices[0].ToString();
            for (int i = 1; i < indices.Length; i++) text += "/" + indices[i];
            return text;
        }
    }
}
