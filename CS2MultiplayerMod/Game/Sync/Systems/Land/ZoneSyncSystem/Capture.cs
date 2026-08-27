using System.Collections.Generic;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ZoneSyncSystem
    {
        private void CaptureUpdatedBlocks(long now)
        {
            if (_updatedBlocks.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> blocks = _updatedBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < blocks.Length; i++)
                {
                    Block block = EntityManager.GetComponentData<Block>(blocks[i]);
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blocks[i], true);

                    // Blocks update for many reasons (road edits, sim) — only zoned blocks
                    // are worth broadcasting; an all-None block that was just unzoned still
                    // carries names=0 + non-empty cells, which the receiver applies fine.
                    var names = new List<string>();
                    var cellBytes = new byte[cells.Length];
                    var cellStates = new byte[cells.Length];
                    bool anyZoned = false;
                    for (int c = 0; c < cells.Length; c++)
                    {
                        cellStates[c] = PortableCellState(cells[c].m_State);
                        cellBytes[c] = ZonePaintCommand.NoneCell;
                        ushort zoneIndex = cells[c].m_Zone.m_Index;
                        if (zoneIndex == 0) continue;

                        string zoneName = ResolveZoneName(zoneIndex);
                        if (zoneName == null) continue;

                        int tableIndex = names.IndexOf(zoneName);
                        if (tableIndex < 0)
                        {
                            if (names.Count >= ZonePaintCommand.NoneCell) continue; // table full — never in practice
                            names.Add(zoneName);
                            tableIndex = names.Count - 1;
                        }
                        cellBytes[c] = (byte)tableIndex;
                        anyZoned = true;
                    }

                    // Updated is shared by many simulation paths. Remember the last zoning
                    // content permanently rather than suppressing only one exact echo: unchanged
                    // blocks must not become a recurring network command.
                    ZoneBlockKey blockKey = StateKey(block);
                    int contentHash = ContentHash(names, cellBytes);
                    string guardKey = BlockKey(blockKey, contentHash);
                    if (_guard.Consume(guardKey, now))
                    {
                        _lastZoneStates[blockKey] = new ZoneBaseline
                        {
                            Entity = blocks[i],
                            Hash = contentHash,
                        };
                        if (anyZoned) _zonedBlocks.Add(blockKey);
                        continue;
                    }

                    ZoneBaseline previous;
                    if (_lastZoneStates.TryGetValue(blockKey, out previous) &&
                        previous.Entity == blocks[i] && previous.Hash == contentHash)
                        continue;

                    // Untouched-by-zoning blocks churn constantly (road rebuilding etc.);
                    // skip them unless we previously synced content for this block.
                    if (!anyZoned && !_zonedBlocks.Contains(blockKey))
                    {
                        _lastZoneStates[blockKey] = new ZoneBaseline
                        {
                            Entity = blocks[i],
                            Hash = contentHash,
                        };
                        continue;
                    }

                    var command = new ZonePaintCommand
                    {
                        PosX = block.m_Position.x,
                        PosY = block.m_Position.y,
                        PosZ = block.m_Position.z,
                        DirX = block.m_Direction.x,
                        DirZ = block.m_Direction.y,
                        SizeX = block.m_Size.x,
                        SizeY = block.m_Size.y,
                        ZoneNames = names.ToArray(),
                        Cells = cellBytes,
                        CellStates = cellStates,
                    };

                    bool coalesced = _outgoing.ContainsKey(blockKey);
                    if (!_outgoing.TrySetLatest(blockKey, command, MaxBufferedOutgoingZones))
                    {
                        SyncInbox.RequestResync("zone outgoing latest-state queue overflow");
                        if (!_outgoingOverflowWarned)
                        {
                            _outgoingOverflowWarned = true;
                            Mod.log.Warn("[MP] ZoneSync outgoing queue reached its safety limit; " +
                                         "requesting a fresh world sync.");
                        }
                        continue;
                    }

                    _lastZoneStates[blockKey] = new ZoneBaseline
                    {
                        Entity = blocks[i],
                        Hash = contentHash,
                    };
                    _zonedBlocks.Add(blockKey);
                    _diagnosticCaptured++;
                    if (coalesced) _diagnosticCoalesced++;
                }
            }
            finally
            {
                blocks.Dispose();
            }
        }

        private void FlushOutgoing(MultiplayerSession session)
        {
            int sent = 0;
            ZoneBlockKey blockKey;
            ZonePaintCommand command;
            while (sent < MaxSendPerFrame && _outgoing.TryTake(out blockKey, out command))
            {
                session.SendCommand(0, ZonePaintCommand.Id, command.Encode());
                sent++;
                _diagnosticSent++;
            }
            if (_outgoing.Count == 0) _outgoingOverflowWarned = false;
        }

        private static int ContentHash(List<string> names, byte[] cells)
        {
            unchecked
            {
                int hash = (int)2166136261;
                for (int i = 0; i < cells.Length; i++)
                {
                    // Hash the NAME of each cell's zone, not the table index, so identical
                    // zoning hashes identically regardless of table order.
                    string name = cells[i] != ZonePaintCommand.NoneCell && cells[i] < names.Count
                        ? names[cells[i]] : "";
                    for (int c = 0; c < name.Length; c++) hash = (hash ^ name[c]) * 16777619;
                    hash = (hash ^ '|') * 16777619;
                }
                return hash;
            }
        }

        private int ContentHash(DynamicBuffer<Cell> cells, out bool anyZoned)
        {
            unchecked
            {
                int hash = (int)2166136261;
                anyZoned = false;
                for (int i = 0; i < cells.Length; i++)
                {
                    ushort zoneIndex = cells[i].m_Zone.m_Index;
                    string name = zoneIndex == 0 ? null : ResolveZoneName(zoneIndex);
                    if (!string.IsNullOrEmpty(name))
                    {
                        anyZoned = true;
                        for (int c = 0; c < name.Length; c++) hash = (hash ^ name[c]) * 16777619;
                    }
                    hash = (hash ^ '|') * 16777619;
                }
                return hash;
            }
        }

        private static byte PortableCellState(CellFlags state)
        {
            byte result = 0;
            if ((state & CellFlags.Visible) != 0) result |= ZonePaintCommand.StateVisible;
            if ((state & CellFlags.Roadside) != 0) result |= ZonePaintCommand.StateRoadside;
            if ((state & CellFlags.RoadLeft) != 0) result |= ZonePaintCommand.StateRoadLeft;
            if ((state & CellFlags.RoadRight) != 0) result |= ZonePaintCommand.StateRoadRight;
            if ((state & CellFlags.RoadBack) != 0) result |= ZonePaintCommand.StateRoadBack;
            if ((state & CellFlags.Shared) != 0) result |= ZonePaintCommand.StateShared;
            if ((state & CellFlags.Occupied) != 0) result |= ZonePaintCommand.StateOccupied;
            return result;
        }

    }
}
