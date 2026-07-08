using System.Collections.Generic;
using Game.Common;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ZoneSyncSystem
    {
        private void ApplyZoneCommands(List<ZonePaintCommand> incoming, bool retryDue, long now)
        {
            // One scan: index local blocks by quantized position.
            var lookup = new Dictionary<long, Entity>();
            NativeArray<Entity> blocks = _allBlocks.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < blocks.Length; i++)
                    lookup[QuantizedPos(EntityManager.GetComponentData<Block>(blocks[i]).m_Position)] = blocks[i];
            }
            finally
            {
                blocks.Dispose();
            }

            int applied = 0, deferred = 0, expired = 0;

            if (incoming != null)
            {
                for (int i = 0; i < incoming.Count; i++)
                {
                    bool matched, changed;
                    ApplyOne(incoming[i], lookup, now, out matched, out changed);
                    if (changed) applied++;
                    if (!matched && _pending.Count < MaxPendingZones)
                    {
                        _pending.Add(new PendingZone { Command = incoming[i], DeadlineMs = now + ZoneRetryWindowMs });
                        deferred++;
                    }
                }
            }

            if (retryDue)
            {
                _lastRetryMs = now;
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    bool matched, changed;
                    ApplyOne(_pending[i].Command, lookup, now, out matched, out changed);
                    if (matched) { _pending.RemoveAt(i); if (changed) applied++; }
                    else if (now >= _pending[i].DeadlineMs) { _pending.RemoveAt(i); expired++; }
                }
            }

            if (applied > 0 || deferred > 0 || expired > 0)
                Mod.Verbose("[MP] ZoneSync: applied " + applied + " block(s)" +
                             (deferred > 0 ? ", deferred " + deferred + " (road/blocks not ready)" : "") +
                             (expired > 0 ? ", dropped " + expired + " (gave up)" : "") +
                             " [pending " + _pending.Count + "].");
        }

        /// <summary>
        /// Apply one zone command to its local block. <paramref name="matched"/> tells the
        /// caller whether the block exists yet (so an unmatched command can be retried);
        /// <paramref name="changed"/> whether any cell actually changed.
        /// </summary>
        private void ApplyOne(ZonePaintCommand command, Dictionary<long, Entity> lookup, long now, out bool matched, out bool changed)
        {
            matched = false;
            changed = false;
            var pos = new float3(command.PosX, command.PosY, command.PosZ);

            Entity blockEntity;
            if (!TryFindBlock(lookup, pos, out blockEntity)) return;
            matched = true;

            DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blockEntity);
            int count = math.min(cells.Length, command.Cells.Length);
            for (int c = 0; c < count; c++)
            {
                ushort wanted = 0;
                byte tableIndex = command.Cells[c];
                if (tableIndex != ZonePaintCommand.NoneCell && tableIndex < command.ZoneNames.Length)
                {
                    ushort resolved;
                    if (_nameToIndex.TryGetValue(command.ZoneNames[tableIndex], out resolved) ||
                        TryResolveZoneIndex(command.ZoneNames[tableIndex], out resolved))
                        wanted = resolved;
                    else
                        continue; // unknown zone prefab (mod mismatch) — leave the cell alone
                }

                Cell cell = cells[c];
                if (cell.m_Zone.m_Index == wanted) continue;
                cell.m_Zone = new ZoneType { m_Index = wanted };
                cells[c] = cell;
                changed = true;
            }

            if (!changed) return; // matched but identical — nothing to write (kills echo loops)

            // Suppress our own re-capture of this exact content, then let the game react.
            var names = new List<string>(command.ZoneNames);
            _guard.Mark(BlockKey(pos, ContentHash(names, command.Cells)), now);
            _zonedBlocks.Add(QuantizedPos(pos));
            EntityManager.AddComponent<Updated>(blockEntity);
        }

        /// <summary>
        /// Find local block tolerating sub-metre drift (road rebuilt by net sync) by checking
        /// 26 neighbouring 0.5 m buckets, never matching different blocks.
        /// </summary>
        private bool TryFindBlock(Dictionary<long, Entity> lookup, float3 pos, out Entity block)
        {
            if (lookup.TryGetValue(QuantizedPos(pos), out block)) return true;

            long qx = (long)math.round(pos.x * 2f);
            long qy = (long)math.round(pos.y * 2f);
            long qz = (long)math.round(pos.z * 2f);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        if (lookup.TryGetValue(PackQuant(qx + dx, qy + dy, qz + dz), out block)) return true;
                    }

            block = Entity.Null;
            return false;
        }

    }
}
