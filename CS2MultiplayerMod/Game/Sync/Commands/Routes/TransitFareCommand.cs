using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

namespace CS2MultiplayerMod.Game.Sync.Commands
{
    /// <summary>
    /// "This transport line now charges this much." The ticket price is a field on the line's
    /// runtime component, not a policy, so nothing else in the mod carries it: route geometry,
    /// stops, colour and name all replicate, and then the two cities quietly disagree about what
    /// riding the line costs - which shows up as a growing divergence in the transport budget
    /// rather than as anything visible on the map.
    ///
    /// The line is identified the way <see cref="RouteUpdateCommand"/> identifies one: by its
    /// route number, which both peers already agree on because route creation replicates it. The
    /// prefab name travels too and is checked before applying, so a number that has been reused
    /// for a different kind of line cannot have a price written onto it.
    /// </summary>
    public sealed class TransitFareCommand : ISimulationCommand
    {
        public const ushort Id = 30;

        /// <summary>
        /// The game's own slider stops far below this; the cap exists so a forged value cannot
        /// be written into the line, not to describe what a player can choose.
        /// </summary>
        public const int MaxTicketPrice = 65535;

        public const int MaxEncodedBytes = 256;

        public string RoutePrefabName;
        public int RouteNumber;
        public int TicketPrice;

        public ushort CommandId => Id;

        public void Write(NetworkWriter writer)
        {
            writer.WriteString(RoutePrefabName);
            writer.WriteInt(RouteNumber);
            writer.WriteInt(TicketPrice);
        }

        public void Read(NetworkReader reader)
        {
            RoutePrefabName = WireGuard.ReadName(reader);
            RouteNumber = reader.ReadInt();
            RouteCommandCodec.ValidateRouteNumber(RouteNumber);
            TicketPrice = reader.ReadInt();
            if (TicketPrice < 0 || TicketPrice > MaxTicketPrice)
                throw new ProtocolException("Transit fare " + TicketPrice +
                                            " is outside [0, " + MaxTicketPrice + "].");
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in transit-fare command.");
        }

        public byte[] Encode()
        {
            var writer = new NetworkWriter(64);
            Write(writer);
            if (writer.Length > MaxEncodedBytes)
                throw new ProtocolException("Transit-fare command exceeds its size limit.");
            return writer.ToArray();
        }

        public static TransitFareCommand Decode(byte[] body)
        {
            if (body == null || body.Length > MaxEncodedBytes)
                throw new ProtocolException("Transit-fare command exceeds its size limit.");
            var command = new TransitFareCommand();
            command.Read(new NetworkReader(body));
            return command;
        }
    }
}
