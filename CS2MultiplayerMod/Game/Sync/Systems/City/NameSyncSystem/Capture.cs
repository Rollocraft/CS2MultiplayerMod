using System;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class NameSyncSystem
    {
        /// <summary>
        /// Diffs the game's typed-name table, which updates on confirm, paused or not, for every kind.
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
                        if (!_nameSystem.TryGetCustomName(entity, out string name) ||
                            string.IsNullOrEmpty(name)) continue;

                        _seen.Add(entity);
                        if (_knownNames.TryGetValue(entity, out string previous) && previous == name) continue;
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
            // The first scan only records: both machines start from the same state.
            _primed = true;
        }

        /// <summary>
        /// A vanished name was cleared (replicates) or went with its entity (does not: the peer deleted it too).
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
                // The marker arrives through a command buffer, one frame late.
                if (alive && _nameSystem.TryGetCustomName(entity, out string current) &&
                    !string.IsNullOrEmpty(current)) continue;

                _knownNames.Remove(entity);
                if (!alive || !_primed) continue;
                SendCustomName(session, entity, string.Empty);
            }
        }

        /// <summary>Host only: auto-names are local rolls, and the host's is the city's.</summary>
        private void CaptureAutoNames(MultiplayerSession session, long now)
        {
            if (session.Role != SessionRole.Host) return;
            if (!_autoBaselined) BaselineStreets();
            CollectChangedStreets(now);
            PublishSettledStreets(session, now);
            CaptureCreatedDistrictNames(session);
        }

        /// <summary>
        /// Records every street's current name once per world; only later changes travel. A street
        /// created this frame is a real change.
        /// </summary>
        private void BaselineStreets()
        {
            _autoBaselined = true;
            if (_streets.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _streets.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (EntityManager.HasComponent<Created>(entities[i])) continue;
                    string stamp = StampOf(entities[i]);
                    if (stamp != null) _publishedAuto[entities[i]] = stamp;
                }
            }
            finally
            {
                entities.Dispose();
            }
            SyncLog.Detail(LogTopic.City, "NameSync: baselined " + _publishedAuto.Count +
                " street draw(s).");
        }

        /// <summary>
        /// Notes streets whose edge set changed. A grid regroups repeatedly, so the draw is read after a
        /// settle window that is never extended, or a busy street would never publish.
        /// </summary>
        private void CollectChangedStreets(long now)
        {
            if (_changedStreets.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _changedStreets.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (_dirtyStreets.ContainsKey(entities[i])) continue;
                    // Cosmetic: shedding one street's draw costs a wrong name, never geometry.
                    if (_dirtyStreets.Count >= MaxPendingTargets) continue;
                    _dirtyStreets[entities[i]] = now + AutoNameSettleMs;
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void PublishSettledStreets(MultiplayerSession session, long now)
        {
            if (_dirtyStreets.Count == 0) return;

            _dirtyDue.Clear();
            foreach (var pair in _dirtyStreets)
                if (now >= pair.Value) _dirtyDue.Add(pair.Key);

            for (int i = 0; i < _dirtyDue.Count; i++)
            {
                Entity street = _dirtyDue[i];
                _dirtyStreets.Remove(street);
                if (!EntityManager.Exists(street) ||
                    EntityManager.HasComponent<Deleted>(street))
                {
                    // Merged away: the aggregate that swallowed it is dirty too and speaks for it.
                    _publishedAuto.Remove(street);
                    continue;
                }

                if (!TryIdentify(street, out byte kind, out string prefabName, out float3 anchor)) continue;
                int[] indices = ReadRandomIndices(street);
                if (indices.Length == 0) continue;

                string stamp = Stamp(prefabName, anchor, ElementCount(street), indices);
                if (_publishedAuto.TryGetValue(street, out string published) && published == stamp) continue;
                _publishedAuto[street] = stamp;

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

        private void CaptureCreatedDistrictNames(MultiplayerSession session)
        {
            if (_createdDistricts.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _createdDistricts.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    int[] indices = ReadRandomIndices(entities[i]);
                    if (indices.Length == 0) continue;

                    if (!TryIdentify(entities[i], out byte kind, out string prefabName, out float3 anchor)) continue;

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

        private string StampOf(Entity street)
        {
            if (!TryIdentify(street, out byte kind, out string prefabName, out float3 anchor)) return null;
            int[] indices = ReadRandomIndices(street);
            if (indices.Length == 0) return null;
            return Stamp(prefabName, anchor, ElementCount(street), indices);
        }

        /// <summary>
        /// What a peer needs to reproduce the name, including the edge count: a merge can grow the street
        /// without changing its draw or first edge.
        /// </summary>
        private string Stamp(string prefabName, float3 anchor, int elements, int[] indices) =>
            Infrastructure.ReplicationGuard.Key(prefabName, anchor) + "|" + elements + "|" +
            Describe(indices);

        private int ElementCount(Entity street) =>
            EntityManager.HasBuffer<global::Game.Net.AggregateElement>(street)
                ? EntityManager.GetBuffer<global::Game.Net.AggregateElement>(street, true).Length
                : 0;

        /// <summary>Drop the record of streets that no longer exist (merged away or bulldozed).</summary>
        private void PrunePublished()
        {
            if (_publishedAuto.Count == 0) return;

            _publishedDead.Clear();
            foreach (var pair in _publishedAuto)
                if (!EntityManager.Exists(pair.Key) ||
                    EntityManager.HasComponent<Deleted>(pair.Key))
                    _publishedDead.Add(pair.Key);
            for (int i = 0; i < _publishedDead.Count; i++) _publishedAuto.Remove(_publishedDead[i]);
        }

        private void SendCustomName(MultiplayerSession session, Entity entity, string name)
        {
            if (!TryIdentify(entity, out byte kind, out string prefabName, out float3 anchor))
            {
                SyncLog.Detail(LogTopic.City, "NameSync: '" + name + "' is on '" +
                    (LocalPrefabName(entity) ?? "?") + "', which has no cross-machine " +
                    "identity; not replicated.");
                return;
            }

            // Clamp here so an over-long name replicates shortened instead of being refused.
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
                SyncLog.Warn(LogTopic.City, "NameSync: could not encode " + what + " for " +
                    KindName(command.TargetKind) + " '" + command.TargetPrefabName + "': " +
                    ex.Message);
                return;
            }
            session.SendCommand(0, EntityNameCommand.Id, body);
            SyncLog.Trace(LogTopic.City, "NameSync captured " + what + " on " +
                KindName(command.TargetKind) + " '" + command.TargetPrefabName + "'.");
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
