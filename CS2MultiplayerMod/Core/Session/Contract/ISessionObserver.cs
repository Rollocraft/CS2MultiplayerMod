using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Receives session events. All callbacks are invoked on the game thread (from
    /// <see cref="MultiplayerSession.Update"/>), so implementations may safely touch
    /// game/UI state. Derive from <see cref="SessionObserver"/> to override only what
    /// you need.
    /// </summary>
    public interface ISessionObserver
    {
        void OnStatusChanged(SessionStatus status, string detail);
        void OnPeerJoined(Peer peer);
        void OnPeerLeft(Peer peer, string reason);
        void OnChatReceived(string senderName, string text);
        void OnCommandReceived(SimulationCommandMessage command);

        /// <summary>A replicated state snapshot arrived (clients only). Apply it to the world.</summary>
        void OnStateReceived(StateSnapshotMessage snapshot);

        /// <summary>
        /// A client's edit of a player-editable state channel arrived (host only).
        /// Apply it to the world; the next snapshot broadcast confirms it to everyone.
        /// </summary>
        void OnStateEditReceived(StateEditMessage edit);

        /// <summary>Another player's position update arrived.</summary>
        void OnPlayerStateReceived(PlayerStateMessage state);

        /// <summary>A complete blob (all chunks reassembled) arrived on a named channel.</summary>
        void OnBlobReceived(string channel, byte[] data);

        /// <summary>
        /// A complete epoch-tagged blob arrived. The base observer forwards this to the
        /// legacy two-argument callback so observers that do not care about epochs keep working.
        /// </summary>
        void OnBlobReceived(string channel, long transferId, byte[] data);

        /// <summary>An atomic world-sync control stage arrived.</summary>
        void OnWorldSyncControl(WorldSyncStage stage, long epoch, float resumeSpeed,
            ConnectionId connection);

        /// <summary>
        /// A player ran /sync (host only). Stream the current world to
        /// <paramref name="connection"/>, or to everyone when it is
        /// <see cref="ConnectionId.None"/> (the host itself asked).
        /// </summary>
        void OnResyncRequested(int playerId, ConnectionId connection);

        void OnError(string message);
    }

    /// <summary>No-op base so observers can override selectively.</summary>
    public abstract class SessionObserver : ISessionObserver
    {
        public virtual void OnStatusChanged(SessionStatus status, string detail) { }
        public virtual void OnPeerJoined(Peer peer) { }
        public virtual void OnPeerLeft(Peer peer, string reason) { }
        public virtual void OnChatReceived(string senderName, string text) { }
        public virtual void OnCommandReceived(SimulationCommandMessage command) { }
        public virtual void OnStateReceived(StateSnapshotMessage snapshot) { }
        public virtual void OnStateEditReceived(StateEditMessage edit) { }
        public virtual void OnPlayerStateReceived(PlayerStateMessage state) { }
        public virtual void OnBlobReceived(string channel, byte[] data) { }
        public virtual void OnBlobReceived(string channel, long transferId, byte[] data) =>
            OnBlobReceived(channel, data);
        public virtual void OnWorldSyncControl(WorldSyncStage stage, long epoch, float resumeSpeed,
            ConnectionId connection) { }
        public virtual void OnResyncRequested(int playerId, ConnectionId connection) { }
        public virtual void OnError(string message) { }
    }
}
