using System;
using System.Collections.Generic;
using Game.Common;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ZoneSyncSystem
    {
        private void ApplyZoneCommands(bool retryDue, long now)
        {
            Dictionary<long, List<Entity>> lookup = GetBlockLookup(now);
            int remaining = MaxApplyPerFrame;

            // Fresh states take priority. Because each source layout occurs at most once in _ready,
            // one frame can never structurally update the same Block over and over.
            int fresh = Math.Min(_ready.Count, remaining);
            for (int i = 0; i < fresh; i++)
            {
                ZoneBlockKey key;
                ZonePaintCommand command;
                if (!_ready.TryTake(out key, out command)) break;
                remaining--;

                bool matched, changed;
                ApplyOne(command, lookup, now, out matched, out changed);
                if (changed) _diagnosticApplied++;
                if (!matched)
                {
                    var pending = new PendingZone
                    {
                        Command = command,
                        DeadlineMs = now + ZoneRetryWindowMs,
                    };
                    if (!_pending.TrySetLatest(key, pending, MaxPendingZones))
                    {
                        RecoverFromQueueOverflow("zone target retry queue overflow");
                        return;
                    }
                    _diagnosticDeferred++;
                }
            }

            // Retry only with budget left after fresh work. If fresh work spends the frame budget,
            // leave the timer due so retries run at the first available frame instead of waiting
            // another interval.
            if (retryDue && remaining > 0)
            {
                int retries = Math.Min(_pending.Count, remaining);
                if (retries > 0) _lastRetryMs = now;
                for (int i = 0; i < retries; i++)
                {
                    ZoneBlockKey key;
                    PendingZone pending;
                    if (!_pending.TryTake(out key, out pending)) break;

                    bool matched, changed;
                    ApplyOne(pending.Command, lookup, now, out matched, out changed);
                    if (matched)
                    {
                        if (changed) _diagnosticApplied++;
                    }
                    else if (now >= pending.DeadlineMs)
                    {
                        _diagnosticExpired++;
                    }
                    else
                    {
                        // This item was removed immediately above, so reinsertion cannot exceed
                        // the capacity. Keeping the same deadline makes retry time strictly bounded.
                        _pending.TrySetLatest(key, pending, MaxPendingZones);
                    }
                }
            }
        }

        private Dictionary<long, List<Entity>> GetBlockLookup(long now)
        {
            if (!_blockLookupBuilt || now < _blockLookupBuiltAtMs ||
                now - _blockLookupBuiltAtMs >= BlockLookupRefreshMs)
                RebuildBlockLookup(now);
            return _blockLookup;
        }

        private void RebuildBlockLookup(long now)
        {
            ClearBlockLookup();
            NativeArray<Entity> blocks = _allBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < blocks.Length; i++)
                {
                    Block block = EntityManager.GetComponentData<Block>(blocks[i]);
                    AddBlockToLookup(blocks[i], block);
                }
            }
            finally
            {
                blocks.Dispose();
            }
            _blockLookupBuilt = true;
            _blockLookupBuiltAtMs = now;
        }

        private void ClearBlockLookup()
        {
            foreach (List<Entity> entities in _blockLookup.Values)
            {
                entities.Clear();
                _blockLookupListPool.Add(entities);
            }
            _blockLookup.Clear();
        }

        private void AddBlockToLookup(Entity entity, Block block)
        {
            float2 direction = block.m_Direction;
            float2 right = new float2(direction.y, -direction.x);
            float2 extents = math.abs(direction) * (block.m_Size.y * 4f) +
                             math.abs(right) * (block.m_Size.x * 4f);
            float2 center = block.m_Position.xz;
            int minX = (int)math.floor((center.x - extents.x) / BlockLookupBucketSize);
            int maxX = (int)math.floor((center.x + extents.x) / BlockLookupBucketSize);
            int minZ = (int)math.floor((center.y - extents.y) / BlockLookupBucketSize);
            int maxZ = (int)math.floor((center.y + extents.y) / BlockLookupBucketSize);

            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    long key = PackSpatialBucket(x, z);
                    List<Entity> entities;
                    if (!_blockLookup.TryGetValue(key, out entities))
                    {
                        if (_blockLookupListPool.Count > 0)
                        {
                            int last = _blockLookupListPool.Count - 1;
                            entities = _blockLookupListPool[last];
                            _blockLookupListPool.RemoveAt(last);
                        }
                        else
                        {
                            entities = new List<Entity>(8);
                        }
                        _blockLookup.Add(key, entities);
                    }
                    entities.Add(entity);
                }
        }

        /// <summary>
        /// Apply one zone command to its local block. <paramref name="matched"/> tells the
        /// caller whether the block exists yet (so an unmatched command can be retried);
        /// <paramref name="changed"/> whether any cell actually changed.
        /// </summary>
        private void ApplyOne(ZonePaintCommand command, Dictionary<long, List<Entity>> lookup,
            long now, out bool matched, out bool changed)
        {
            matched = true;
            changed = false;
            var mappedBlocks = new List<Entity>(4);
            var changedBlocks = new List<Entity>(4);
            var resolvedZones = new ushort[command.ZoneNames.Length];
            var knownZones = new bool[command.ZoneNames.Length];
            for (int i = 0; i < command.ZoneNames.Length; i++)
            {
                ushort resolved;
                if (_nameToIndex.TryGetValue(command.ZoneNames[i], out resolved) ||
                    TryResolveZoneIndex(command.ZoneNames[i], out resolved))
                {
                    resolvedZones[i] = resolved;
                    knownZones[i] = true;
                }
            }

            for (int c = 0; c < command.Cells.Length; c++)
            {
                if (!command.IsCellVisible(c)) continue;

                byte tableIndex = command.Cells[c];
                ushort wanted = 0;
                if (tableIndex != ZonePaintCommand.NoneCell)
                {
                    if (!knownZones[tableIndex]) continue; // Unknown prefab: preserve local zoning.
                    wanted = resolvedZones[tableIndex];
                }

                float sourceX, sourceY, sourceZ;
                if (!command.TryGetCellCenter(c, out sourceX, out sourceY, out sourceZ))
                {
                    matched = false;
                    continue;
                }

                Entity blockEntity;
                int localIndex;
                if (!TryFindLocalCell(lookup, command,
                        new float3(sourceX, sourceY, sourceZ), command.CellStates[c],
                        out blockEntity, out localIndex))
                {
                    matched = false;
                    continue;
                }

                AddUnique(mappedBlocks, blockEntity);
                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity);
                Cell cell = cells[localIndex];
                if (cell.m_Zone.m_Index == wanted) continue;
                cell.m_Zone = new ZoneType { m_Index = wanted };
                cells[localIndex] = cell;
                AddUnique(changedBlocks, blockEntity);
                changed = true;
            }

            // A source block may span several locally generated blocks, so finalize each target
            // once. For a partial application, leave unchanged targets alone: retrying a missing
            // cell must not repeatedly absorb an unrelated local edit into the capture baseline.
            for (int i = 0; i < mappedBlocks.Count; i++)
            {
                Entity blockEntity = mappedBlocks[i];
                bool blockChanged = changedBlocks.Contains(blockEntity);
                if (!matched && !blockChanged) continue;
                if (!IsLiveBlock(blockEntity)) continue;
                Block localBlock = EntityManager.GetComponentData<Block>(blockEntity);
                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity);
                bool anyZoned;
                int actualHash = ContentHash(cells, out anyZoned);
                ZoneBlockKey blockKey = StateKey(localBlock);
                _lastZoneStates[blockKey] = new ZoneBaseline
                {
                    Entity = blockEntity,
                    Hash = actualHash,
                };
                _outgoing.Remove(blockKey);
                if (anyZoned) _zonedBlocks.Add(blockKey);

                if (!blockChanged) continue;
                _guard.Mark(BlockKey(blockKey, actualHash), now);
                if (!EntityManager.HasComponent<Updated>(blockEntity))
                    EntityManager.AddComponent<Updated>(blockEntity);
            }
        }

        /// <summary>
        /// Map one visible source cell to the closest semantically compatible visible local cell.
        /// Searching immediate index neighbours handles a half-cell alignment difference without
        /// copying any of the sender's locally generated state flags.
        /// </summary>
        private bool TryFindLocalCell(Dictionary<long, List<Entity>> lookup,
            ZonePaintCommand command, float3 sourcePosition, byte sourceState,
            out Entity bestBlock, out int bestIndex)
        {
            bestBlock = Entity.Null;
            bestIndex = -1;
            List<Entity> candidates;
            if (!lookup.TryGetValue(SpatialBucket(sourcePosition.xz), out candidates)) return false;

            float2 sourceDirection = math.normalizesafe(new float2(command.DirX, command.DirZ));
            bool sourceHasRoad = (sourceState & ZonePaintCommand.StateRoadMask) != 0;
            bool sourceRoadside = (sourceState & ZonePaintCommand.StateRoadside) != 0;
            bool sourceShared = (sourceState & ZonePaintCommand.StateShared) != 0;
            bool sourceOccupied = (sourceState & ZonePaintCommand.StateOccupied) != 0;
            float2 sourceBlockPosition = new float2(command.PosX, command.PosZ);
            float bestScore = float.MaxValue;

            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                Entity blockEntity = candidates[candidateIndex];
                if (!IsLiveBlock(blockEntity)) continue;

                Block block = EntityManager.GetComponentData<Block>(blockEntity);
                float signedAlignment = math.dot(sourceDirection,
                    math.normalizesafe(block.m_Direction));
                float alignment = math.abs(signedAlignment);
                if (alignment < 0.8f) continue;
                float stripOffset = math.abs(math.dot(block.m_Position.xz - sourceBlockPosition,
                    sourceDirection));

                int2 baseIndex = ZoneUtils.GetCellIndex(block, sourcePosition.xz);
                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity, true);
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        int2 local = baseIndex + new int2(offsetX, offsetY);
                        if (local.x < 0 || local.y < 0 ||
                            local.x >= block.m_Size.x || local.y >= block.m_Size.y)
                            continue;

                        int index = local.y * block.m_Size.x + local.x;
                        if (index < 0 || index >= cells.Length) continue;
                        Cell cell = cells[index];
                        if ((cell.m_State & CellFlags.Visible) == 0) continue;

                        float3 localPosition = ZoneUtils.GetCellPosition(block, local);
                        float distanceSquared = math.lengthsq(localPosition.xz - sourcePosition.xz);
                        if (distanceSquared > 40f) continue;

                        bool localHasRoad = (cell.m_State & (CellFlags.Roadside |
                            CellFlags.RoadLeft | CellFlags.RoadRight | CellFlags.RoadBack)) != 0;
                        bool localRoadside = (cell.m_State & CellFlags.Roadside) != 0;
                        bool localShared = (cell.m_State & CellFlags.Shared) != 0;
                        bool localOccupied = (cell.m_State & CellFlags.Occupied) != 0;

                        float score = distanceSquared + (1f - alignment) * 32f +
                                      math.min(stripOffset * stripOffset * 0.25f, 64f);
                        if (signedAlignment < 0f) score += 32f;
                        if (sourceHasRoad != localHasRoad) score += 64f;
                        if (sourceRoadside != localRoadside) score += 16f;
                        if (sourceShared != localShared) score += 4f;
                        if (sourceOccupied != localOccupied) score += 2f;
                        if (block.m_Size.x == command.SizeX && block.m_Size.y == command.SizeY)
                            score -= 0.25f;

                        if (score >= bestScore) continue;
                        bestScore = score;
                        bestBlock = blockEntity;
                        bestIndex = index;
                    }
            }

            return bestBlock != Entity.Null;
        }

        private bool IsLiveBlock(Entity block)
        {
            return block != Entity.Null &&
                EntityManager.Exists(block) &&
                EntityManager.HasComponent<Block>(block) &&
                EntityManager.HasBuffer<Cell>(block) &&
                !EntityManager.HasComponent<Temp>(block) &&
                !EntityManager.HasComponent<Deleted>(block);
        }

        private static void AddUnique(List<Entity> entities, Entity entity)
        {
            if (!entities.Contains(entity)) entities.Add(entity);
        }

        private static long SpatialBucket(float2 position) =>
            PackSpatialBucket((int)math.floor(position.x / BlockLookupBucketSize),
                              (int)math.floor(position.y / BlockLookupBucketSize));

        private static long PackSpatialBucket(int x, int z) => ((long)x << 32) | (uint)z;
    }
}
