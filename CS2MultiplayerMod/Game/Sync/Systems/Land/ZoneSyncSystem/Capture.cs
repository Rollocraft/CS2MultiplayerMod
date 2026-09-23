using System.Collections.Generic;
using Game.Tools;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ZoneSyncSystem
    {
        // On the commit frame only: Updated blocks also include road regeneration and remote writes.
        internal void CaptureLocalToolApply()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;
            ToolBaseSystem active = _toolSystem.activeTool;
            if (!(active is ZoneToolSystem) || active.applyMode != ApplyMode.Apply) return;

            NativeArray<Entity> previews = _zonePreviews.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < previews.Length; i++)
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(previews[i]);
                    if ((temp.m_Flags & TempFlags.Delete) != 0 || !IsLiveBlock(temp.m_Original)) continue;
                    Block block = EntityManager.GetComponentData<Block>(temp.m_Original);
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(previews[i], true);
                    DynamicBuffer<Cell> original = EntityManager.GetBuffer<Cell>(temp.m_Original, true);
                    if (cells.Length != original.Length || cells.Length != block.m_Size.x * block.m_Size.y)
                        continue;

                    var names = new List<string>();
                    var values = new byte[cells.Length];
                    var states = new byte[cells.Length];
                    bool edited = false;
                    for (int c = 0; c < cells.Length; c++)
                    {
                        values[c] = ZonePaintCommand.NoneCell;
                        Cell before = original[c];
                        Cell preview = cells[c];
                        states[c] = PortableCellState(before.m_State);
                        // An overridden cell is kept by the commit; its preview must not erase another peer's zoning.
                        if ((preview.m_State & CellFlags.Selected) == 0 ||
                            (before.m_State & CellFlags.Overridden) != 0 ||
                            (before.m_State & CellFlags.Visible) == 0 ||
                            before.m_Zone.m_Index == preview.m_Zone.m_Index) continue;

                        if (preview.m_Zone.m_Index != 0)
                        {
                            string name = ResolveZoneName(preview.m_Zone.m_Index);
                            if (string.IsNullOrEmpty(name)) continue;
                            int index = names.IndexOf(name);
                            if (index < 0) { index = names.Count; names.Add(name); }
                            values[c] = (byte)index;
                        }
                        states[c] |= ZonePaintCommand.StateEdited;
                        edited = true;
                    }
                    if (!edited) continue;
                    var command = new ZonePaintCommand
                    {
                        PosX = block.m_Position.x, PosY = block.m_Position.y, PosZ = block.m_Position.z,
                        DirX = block.m_Direction.x, DirZ = block.m_Direction.y,
                        SizeX = block.m_Size.x, SizeY = block.m_Size.y,
                        ZoneNames = names.ToArray(), Cells = values, CellStates = states,
                    };
                    ZoneBlockKey key = StateKey(block);
                    bool coalesced = _outgoing.TryGetValue(key, out ZonePaintCommand earlier);
                    if (coalesced) command.MergeEarlier(earlier);
                    if (!_outgoing.TrySetLatest(key, command, MaxBufferedOutgoingZones))
                    {
                        RecoverFromQueueOverflow("zone outgoing patch queue overflow");
                        return;
                    }
                    _diagnosticCaptured++;
                    if (coalesced) _diagnosticCoalesced++;
                }
            }
            finally { previews.Dispose(); }
        }

        private void FlushOutgoing(MultiplayerSession session)
        {
            int sent = 0;
            while (sent < MaxSendPerFrame && _outgoing.TryTake(out ZoneBlockKey key, out ZonePaintCommand command))
            {
                session.SendCommand(0, ZonePaintCommand.Id, command.Encode());
                sent++;
                _diagnosticSent++;
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
