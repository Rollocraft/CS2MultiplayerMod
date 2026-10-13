using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// Edited cells; unselected cells carry geometry only. World-space cell centres let receivers map
    /// onto their own block layout. Zone types travel as a per-message name table (0xFF = unzoned)
    /// because <c>ZoneType.m_Index</c> is per machine.
    /// </summary>
    public sealed class ZonePaintCommand : ISimulationCommand
    {
        public const ushort Id = 5;
        public const byte NoneCell = 0xFF;
        public const int MaxCells = 1024;
        public const int MaxEncodedBytes = 96 * 1024;

        // Portable CellFlags subset for target selection; never copied into local state.
        public const byte StateVisible = 1 << 0;
        public const byte StateRoadside = 1 << 1;
        public const byte StateRoadLeft = 1 << 2;
        public const byte StateRoadRight = 1 << 3;
        public const byte StateRoadBack = 1 << 4;
        public const byte StateShared = 1 << 5;
        public const byte StateOccupied = 1 << 6;
        public const byte StateEdited = 1 << 7;
        public const byte StateRoadMask = StateRoadside | StateRoadLeft | StateRoadRight | StateRoadBack;

        public float PosX, PosY, PosZ;
        public float DirX, DirZ;
        public int SizeX, SizeY;
        public string[] ZoneNames;
        public byte[] Cells;
        public byte[] CellStates;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            Validate();
            writer.WriteFloat(PosX);
            writer.WriteFloat(PosY);
            writer.WriteFloat(PosZ);
            writer.WriteFloat(DirX);
            writer.WriteFloat(DirZ);
            writer.WriteShort((short)SizeX);
            writer.WriteShort((short)SizeY);
            writer.WriteByte((byte)ZoneNames.Length);
            for (int i = 0; i < ZoneNames.Length; i++) writer.WriteString(ZoneNames[i]);
            writer.WriteShort((short)Cells.Length);
            writer.WriteBytes(Cells, 0, Cells.Length);
            writer.WriteBytes(CellStates, 0, CellStates.Length);
        }

        public void Read(NetworkReader reader)
        {
            PosX = WireGuard.ReadCoordinate(reader);
            PosY = WireGuard.ReadCoordinate(reader);
            PosZ = WireGuard.ReadCoordinate(reader);
            DirX = WireGuard.ReadFinite(reader);
            DirZ = WireGuard.ReadFinite(reader);
            SizeX = reader.ReadShort();
            SizeY = reader.ReadShort();
            ValidateGeometry();

            int names = reader.ReadByte();
            ZoneNames = new string[names];
            for (int i = 0; i < names; i++) ZoneNames[i] = WireGuard.ReadName(reader);

            int cells = WireGuard.ReadCount(reader, 2, MaxCells);
            if ((long)SizeX * SizeY != cells)
                throw new ProtocolException("Zone block dimensions " + SizeX + "x" + SizeY +
                                            " do not match cell count " + cells + ".");
            Cells = reader.ReadBytes(cells);
            CellStates = reader.ReadBytes(cells);
            ValidateCells();
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in zone-paint command.");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(256);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Zone-paint payload exceeds " + MaxEncodedBytes + " bytes.");
            return writer.ToArray();
        }

        public static ZonePaintCommand Decode(byte[] body)
        {
            if (body == null || body.Length > MaxEncodedBytes)
                throw new ProtocolException("Invalid zone-paint payload length.");
            var command = new ZonePaintCommand();
            command.Read(new NetworkReader(body));
            return command;
        }

        public bool IsCellVisible(int index) =>
            CellStates != null && index >= 0 && index < CellStates.Length &&
            (CellStates[index] & StateVisible) != 0;

        public bool IsCellEdited(int index) =>
            CellStates != null && index >= 0 && index < CellStates.Length &&
            (CellStates[index] & StateEdited) != 0;

        /// <summary>Coalesce unsent/unapplied patches without losing disjoint edits in one block.</summary>
        public void MergeEarlier(ZonePaintCommand earlier)
        {
            if (earlier == null) return;
            if (SizeX != earlier.SizeX || SizeY != earlier.SizeY)
                throw new ProtocolException("Cannot merge different zone block dimensions.");
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < Cells.Length; i++)
            {
                ZonePaintCommand source = IsCellEdited(i) ? this : earlier;
                if (!source.IsCellEdited(i))
                {
                    Cells[i] = NoneCell;
                    continue;
                }
                byte sourceIndex = source.Cells[i];
                CellStates[i] = source.CellStates[i];
                if (sourceIndex == NoneCell) { Cells[i] = NoneCell; continue; }
                string name = source.ZoneNames[sourceIndex];
                int index = names.IndexOf(name);
                if (index < 0)
                {
                    if (names.Count >= NoneCell)
                        throw new ProtocolException("Merged zoning exceeds the zone-name table limit.");
                    index = names.Count;
                    names.Add(name);
                }
                Cells[i] = (byte)index;
            }
            ZoneNames = names.ToArray();
        }

        /// <summary>Resolve one row-major source cell to its world-space centre.</summary>
        public bool TryGetCellCenter(int index, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            if (SizeX <= 0 || SizeY <= 0 || index < 0 || index >= SizeX * SizeY) return false;

            int cellX = index % SizeX;
            int cellY = index / SizeX;
            float across = (SizeX - cellX * 2 - 1) * 4f;
            float depth = (SizeY - cellY * 2 - 1) * 4f;
            x = PosX + DirX * depth + DirZ * across;
            y = PosY;
            z = PosZ + DirZ * depth - DirX * across;
            return true;
        }

        private void Validate()
        {
            ValidateCoordinate(PosX);
            ValidateCoordinate(PosY);
            ValidateCoordinate(PosZ);
            ValidateGeometry();

            if (ZoneNames == null || ZoneNames.Length > NoneCell)
                throw new ProtocolException("Invalid zone-name table.");
            for (int i = 0; i < ZoneNames.Length; i++) ValidateName(ZoneNames[i]);

            if (Cells == null || CellStates == null || Cells.Length != CellStates.Length ||
                Cells.Length > MaxCells || (long)SizeX * SizeY != Cells.Length)
                throw new ProtocolException("Zone cell arrays do not match the block dimensions.");
            ValidateCells();
        }

        private void ValidateGeometry()
        {
            if (SizeX <= 0 || SizeY <= 0 || (long)SizeX * SizeY > MaxCells)
                throw new ProtocolException("Invalid zone block dimensions " + SizeX + "x" + SizeY + ".");
            if (float.IsNaN(DirX) || float.IsInfinity(DirX) ||
                float.IsNaN(DirZ) || float.IsInfinity(DirZ))
                throw new ProtocolException("Non-finite zone block direction.");
            float lengthSquared = DirX * DirX + DirZ * DirZ;
            if (lengthSquared < 0.9f || lengthSquared > 1.1f)
                throw new ProtocolException("Zone block direction is not normalized.");
        }

        private void ValidateCells()
        {
            for (int i = 0; i < Cells.Length; i++)
            {
                if (Cells[i] != NoneCell && Cells[i] >= ZoneNames.Length)
                    throw new ProtocolException("Zone cell references a missing zone name.");
                if (IsCellEdited(i) && !IsCellVisible(i))
                    throw new ProtocolException("Edited zone cell must be visible at its source.");
            }
        }

        private static void ValidateCoordinate(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) ||
                value < -WireGuard.MaxCoordinate || value > WireGuard.MaxCoordinate)
                throw new ProtocolException("Invalid zone block coordinate.");
        }

        private static void ValidateName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > WireGuard.MaxNameLength)
                throw new ProtocolException("Invalid zone name.");
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i]))
                    throw new ProtocolException("Control character in zone name.");
        }
    }
}
