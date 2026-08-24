using System;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "The zoning covered by this block now looks like this." The source block geometry
    /// gives every cell a portable world-space centre, so receivers can map cells onto their
    /// own generated block layout rather than assuming buffer indexes are identical. Zone
    /// types are carried as prefab names via a small per-message string table because
    /// <c>ZoneType.m_Index</c> is a per-machine value. Cell bytes index into that table;
    /// 0xFF means unzoned. See <see cref="ZoneSyncSystem"/>.
    /// </summary>
    public sealed class ZonePaintCommand : ISimulationCommand
    {
        public const ushort Id = 5;
        public const byte NoneCell = 0xFF;
        public const int MaxCells = 1024;
        public const int MaxEncodedBytes = 96 * 1024;

        // Portable subset of Game.Zones.CellFlags. These describe source-cell semantics for
        // target selection; the receiver never copies them into its locally generated state.
        public const byte StateVisible = 1 << 0;
        public const byte StateRoadside = 1 << 1;
        public const byte StateRoadLeft = 1 << 2;
        public const byte StateRoadRight = 1 << 3;
        public const byte StateRoadBack = 1 << 4;
        public const byte StateShared = 1 << 5;
        public const byte StateOccupied = 1 << 6;
        public const byte StateRoadMask = StateRoadside | StateRoadLeft | StateRoadRight | StateRoadBack;
        private const byte KnownStateMask = StateVisible | StateRoadMask | StateShared | StateOccupied;

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
                if ((CellStates[i] & ~KnownStateMask) != 0)
                    throw new ProtocolException("Zone cell contains unknown state flags.");
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
