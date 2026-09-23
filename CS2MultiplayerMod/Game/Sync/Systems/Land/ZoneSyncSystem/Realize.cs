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

            // Each source layout occurs once in _ready, so a Block is updated at most once per frame.
            int fresh = Math.Min(_ready.Count, remaining);
            for (int i = 0; i < fresh; i++)
            {
                if (!_ready.TryTake(out ZoneBlockKey key, out ZonePaintCommand command)) break;
                remaining--;

                ApplyOne(command, lookup, out bool matched, out bool changed, out bool absentGrid);
                if (changed) _diagnosticApplied++;
                if (!matched)
                {
                    var pending = new PendingZone
                    {
                        Command = command,
                        DeadlineMs = _retryClock.NowMs + ZoneRetryWindowMs,
                    };
                    if (!_pending.TrySetLatest(key, pending, MaxPendingZones))
                    {
                        RecoverFromQueueOverflow("zone target retry queue overflow");
                        return;
                    }
                    _diagnosticDeferred++;
                }
            }

            // Leave the timer due if fresh work used the budget.
            if (retryDue && remaining > 0)
            {
                int retries = Math.Min(_pending.Count, remaining);
                if (retries > 0) _lastRetryMs = now;
                for (int i = 0; i < retries; i++)
                {
                    if (!_pending.TryTake(out ZoneBlockKey key, out PendingZone pending)) break;

                    ApplyOne(pending.Command, lookup, out bool matched, out bool changed, out bool absentGrid);
                    if (matched)
                    {
                        WithdrawZoneRecovery(pending, now);
                        if (changed) _diagnosticApplied++;
                    }
                    else
                    {
                        if (pending.RecoveryReport == null && _retryClock.NowMs >= pending.DeadlineMs)
                        {
                            // Unresolved cells sit on live blocks with no zonable cell there: a no-op, not a divergence.
                            if (!absentGrid)
                            {
                                _diagnosticUnzonable++;
                                continue;
                            }
                            _diagnosticExpired++;
                            ZonePaintCommand command = pending.Command;
                            pending.RecoveryReport = Diagnostics.ResyncReport
                                .Create("edited zoning cells did not resolve", "zone",
                                    Diagnostics.ResyncEvidence.MissingTarget)
                                .About("zoning patch at " + command.PosX + "," + command.PosY + "," +
                                    command.PosZ + " facing " + command.DirX + "," + command.DirZ +
                                    " size " + command.SizeX + "x" + command.SizeY)
                                .Tried("retried unresolved cells for 12 s of eligible time; continuing during the recovery hold");
                            Infrastructure.SyncInbox.Settle(pending.RecoveryReport);
                        }
                        // Keep retrying while recovery is held; report once, not every retry.
                        _pending.TrySetLatest(key, pending, MaxPendingZones);
                    }
                }
            }
        }

        private static void WithdrawZoneRecovery(PendingZone pending, long now)
        {
            Diagnostics.ResyncReport report = pending.RecoveryReport;
            if (report == null) return;
            Diagnostics.ResyncArbiter.Withdraw(report.Subsystem, report.Reason, report.Subject, now,
                "the zoning patch resolved or was merged into a newer pending edit");
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
                             math.abs(right) * (block.m_Size.x * 4f) + ZoneCellMatchRules.LookupPadding;
            float2 center = block.m_Position.xz;
            int minX = (int)math.floor((center.x - extents.x) / BlockLookupBucketSize);
            int maxX = (int)math.floor((center.x + extents.x) / BlockLookupBucketSize);
            int minZ = (int)math.floor((center.y - extents.y) / BlockLookupBucketSize);
            int maxZ = (int)math.floor((center.y + extents.y) / BlockLookupBucketSize);

            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    long key = PackSpatialBucket(x, z);
                    if (!_blockLookup.TryGetValue(key, out List<Entity> entities))
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
        /// Applies edited cells. <paramref name="absentGrid"/> is set when an unresolved cell had no zoning
        /// grid at all, the only miss that means a diverged city.
        /// </summary>
        private void ApplyOne(ZonePaintCommand command, Dictionary<long, List<Entity>> lookup,
            out bool matched, out bool changed, out bool absentGrid)
        {
            matched = true;
            changed = false;
            absentGrid = false;
            var changedBlocks = new List<Entity>(4);
            var resolvedZones = new ushort[command.ZoneNames.Length];
            var knownZones = new bool[command.ZoneNames.Length];
            for (int i = 0; i < command.ZoneNames.Length; i++)
            {
                if (_nameToIndex.TryGetValue(command.ZoneNames[i], out ushort resolved) ||
                    TryResolveZoneIndex(command.ZoneNames[i], out resolved))
                {
                    resolvedZones[i] = resolved;
                    knownZones[i] = true;
                }
            }

            for (int c = 0; c < command.Cells.Length; c++)
            {
                if (!command.IsCellEdited(c) || !command.IsCellVisible(c)) continue;

                byte tableIndex = command.Cells[c];
                ushort wanted = 0;
                if (tableIndex != ZonePaintCommand.NoneCell)
                {
                    if (!knownZones[tableIndex]) { matched = false; absentGrid = true; continue; }
                    wanted = resolvedZones[tableIndex];
                }

                if (!command.TryGetCellCenter(c, out float sourceX, out float sourceY, out float sourceZ))
                {
                    matched = false;
                    absentGrid = true;
                    continue;
                }

                if (!TryFindLocalCell(lookup, command,
                        new float3(sourceX, sourceY, sourceZ), command.CellStates[c],
                        out Entity blockEntity, out int localIndex, out bool gridCoversCell))
                {
                    matched = false;
                    if (!gridCoversCell) absentGrid = true;
                    continue;
                }

                // Retire cells individually so a replay cannot undo a newer edit on a finished cell.
                command.CellStates[c] &= unchecked((byte)~ZonePaintCommand.StateEdited);
                DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity);
                Cell cell = cells[localIndex];
                if (cell.m_Zone.m_Index == wanted) continue;
                cell.m_Zone = new ZoneType { m_Index = wanted };
                cells[localIndex] = cell;
                AddUnique(changedBlocks, blockEntity);
                changed = true;
            }

            for (int i = 0; i < changedBlocks.Count; i++)
            {
                Entity block = changedBlocks[i];
                if (IsLiveBlock(block) && !EntityManager.HasComponent<Updated>(block))
                    EntityManager.AddComponent<Updated>(block);
            }
        }

        /// <summary>
        /// Maps a source cell to the closest compatible local cell, tolerating a half-cell offset.
        /// <paramref name="gridCoversCell"/> is false only when no block reaches the position at all.
        /// </summary>
        private bool TryFindLocalCell(Dictionary<long, List<Entity>> lookup,
            ZonePaintCommand command, float3 sourcePosition, byte sourceState,
            out Entity bestBlock, out int bestIndex, out bool gridCoversCell)
        {
            bestBlock = Entity.Null;
            bestIndex = -1;
            gridCoversCell = false;
            if (!lookup.TryGetValue(SpatialBucket(sourcePosition.xz), out List<Entity> candidates)) return false;

            float2 sourceDirection = math.normalizesafe(new float2(command.DirX, command.DirZ));
            float2 sourceBlockPosition = new float2(command.PosX, command.PosZ);
            float bestScore = float.MaxValue;

            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                Entity blockEntity = candidates[candidateIndex];
                if (!IsLiveBlock(blockEntity)) continue;
                gridCoversCell = true;

                Block block = EntityManager.GetComponentData<Block>(blockEntity);
                float signedAlignment = math.dot(sourceDirection,
                    math.normalizesafe(block.m_Direction));
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
                        if (!ZoneCellMatchRules.TryScore(distanceSquared,
                                block.m_Position.y - sourcePosition.y, signedAlignment, stripOffset,
                                sourceState, PortableCellState(cell.m_State),
                                block.m_Size.x == command.SizeX && block.m_Size.y == command.SizeY,
                                out float score)) continue;

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
