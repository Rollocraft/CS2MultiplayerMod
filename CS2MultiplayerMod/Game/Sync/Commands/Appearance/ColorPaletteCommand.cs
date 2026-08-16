using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>The complete savegame color-preset palette (empty means remove all presets).</summary>
    public sealed class ColorPaletteCommand : ISimulationCommand
    {
        public const ushort Id = 21;
        public const int MaxColorSets = 10;
        private const int ColorSetBytes = 48;
        public const int MaxEncodedBytes = 2 + MaxColorSets * ColorSetBytes;

        public VisualColorSet[] Colors;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            int count = Colors != null ? Colors.Length : 0;
            if (count > MaxColorSets)
                throw new ProtocolException("Color-palette command exceeds the preset limit.");
            writer.WriteShort((short)count);
            for (int i = 0; i < count; i++) Colors[i].Write(writer);
        }

        public void Read(NetworkReader reader)
        {
            int count = WireGuard.ReadCount(reader, ColorSetBytes, MaxColorSets);
            Colors = new VisualColorSet[count];
            for (int i = 0; i < count; i++) Colors[i] = VisualColorSet.Read(reader);
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in color-palette command.");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(2 + (Colors != null ? Colors.Length * ColorSetBytes : 0));
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Color-palette command exceeds its size limit.");
            return writer.ToArray();
        }

        public static ColorPaletteCommand Decode(byte[] body)
        {
            if (body == null || body.Length > MaxEncodedBytes)
                throw new ProtocolException("Color-palette command exceeds its size limit.");
            var command = new ColorPaletteCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
