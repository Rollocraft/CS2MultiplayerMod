namespace CS2MultiplayerMod.Core.Protocol
{
    public static class ProtocolConstants
    {
        /// <summary>
        /// Wire-format version. Bump when message layout changes to refuse handshake on mismatch.
        /// Current v13 adds the attach kind + parent node position to ObjectPlacementCommand,
        /// so net objects (roundabout islands) reattach on the receiver.
        /// See <see cref="Messages.HandshakeRequest"/> and version notes in doc/internals.
        /// </summary>
        public const int ProtocolVersion = 13;

        /// <summary>
        /// Hard cap on a single payload, guarding against corrupt length prefixes.
        /// This is the transport-level ceiling; each message type has a far smaller
        /// cap enforced by <see cref="MessageCodec"/>.
        /// </summary>
        public const int MaxPayloadBytes = 16 * 1024 * 1024;

        /// <summary>One blob slice on the wire. Also the per-chunk cap on receive.</summary>
        public const int BlobChunkBytes = 256 * 1024;

        /// <summary>Bytes of nonce in a handshake challenge.</summary>
        public const int ChallengeNonceBytes = 32;

        /// <summary>Bytes of an HMAC-SHA256 password proof.</summary>
        public const int PasswordProofBytes = 32;

        /// <summary>Most DLC entries a handshake may carry (the catalogue is ~2 dozen).</summary>
        public const int MaxDlcEntries = 64;

        /// <summary>Length cap for one DLC name in a handshake.</summary>
        public const int MaxDlcNameLength = 64;
    }
}
