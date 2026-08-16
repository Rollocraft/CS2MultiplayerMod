namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>High-level lifecycle state of a <see cref="MultiplayerSession"/>.</summary>
    public enum SessionStatus
    {
        /// <summary>No session active.</summary>
        Offline,

        /// <summary>Client is establishing a connection / awaiting handshake acceptance.</summary>
        Connecting,

        /// <summary>Host is listening, or client handshake has been accepted.</summary>
        Connected,

        /// <summary>The session ended due to an error (see the reason reported to observers).</summary>
        Faulted,
    }
}
