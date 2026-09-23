namespace CS2MultiplayerMod.Core.Protocol
{
    public static class ProtocolConstants
    {
        /// <summary>
        /// Wire-format version; a mismatch is refused at the handshake. Bump on any layout change or new
        /// command, channel or message id.
        /// </summary>
        public const int ProtocolVersion = 70;

        /// <summary>Transport ceiling against corrupt length prefixes; <see cref="MessageCodec"/> caps each type far lower.</summary>
        public const int MaxPayloadBytes = 16 * 1024 * 1024;

        /// <summary>Envelope cap for one simulation command; each codec enforces a tighter limit.</summary>
        public const int MaxSimulationCommandPayloadBytes = 320 * 1024;

        /// <summary>One blob slice on the wire. Also the per-chunk cap on receive.</summary>
        public const int BlobChunkBytes = 256 * 1024;

        /// <summary>Bytes of nonce in a handshake challenge.</summary>
        public const int ChallengeNonceBytes = 32;

        /// <summary>Most DLC entries a handshake may carry (the catalogue is ~2 dozen).</summary>
        public const int MaxDlcEntries = 64;

        /// <summary>Length cap for one DLC name in a handshake.</summary>
        public const int MaxDlcNameLength = 64;

        /// <summary>Most other live mods a handshake may list.</summary>
        public const int MaxModManifestEntries = 128;

        /// <summary>Length cap for one mod line in a handshake (id, version and a clipped name).</summary>
        public const int MaxModManifestEntryLength = 128;

        /// <summary>Length cap for the source commit a handshake reports.</summary>
        public const int MaxBuildIdLength = 64;
    }
}
