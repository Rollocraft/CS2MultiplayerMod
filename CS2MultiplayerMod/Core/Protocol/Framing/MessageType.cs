namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>First payload byte. Stable across versions: append, never renumber.</summary>
    public enum MessageType : byte
    {
        Unknown = 0,

        /// <summary>Client -> host: introduce protocol version and identity.</summary>
        HandshakeRequest = 1,

        /// <summary>Host -> client: accept or reject, assigning a player id on success.</summary>
        HandshakeResponse = 2,

        /// <summary>Either direction: liveness keep-alive.</summary>
        Heartbeat = 3,

        /// <summary>Either direction: free-text chat / system notice.</summary>
        Chat = 4,

        /// <summary>Either direction: an envelope carrying a serialized simulation command.</summary>
        SimulationCommand = 5,

        /// <summary>Host -> clients: a replicated slice of authoritative state (money, population, ...).</summary>
        StateSnapshot = 6,

        /// <summary>Either direction: a player's camera/cursor position, relayed by the host.</summary>
        PlayerState = 7,

        /// <summary>Host -> clients: one chunk of a large named byte stream (e.g. a savegame).</summary>
        BlobChunk = 8,

        /// <summary>Client -> host edit of shared settings; confirmed by the next <see cref="StateSnapshot"/>.</summary>
        StateEdit = 9,

        /// <summary>Client -> host: stream the current world now.</summary>
        ResyncRequest = 10,

        /// <summary>Host -> client: the password-proof nonce and the host's protocol version.</summary>
        HandshakeChallenge = 11,

        /// <summary>World replacement: the host opens an epoch, clients acknowledge, the host resumes.</summary>
        WorldSyncControl = 12,

        /// <summary>Host -> client: why the session is ending, sent before closing.</summary>
        DisconnectNotice = 13,

        /// <summary>Host -> client: checks passed, awaiting manual approval.</summary>
        HandshakePending = 14,
    }
}
