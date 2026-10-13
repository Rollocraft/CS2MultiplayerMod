using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol.Messages;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Session events, all on the game thread (<see cref="MultiplayerSession.Update"/>). Derive from
    /// <see cref="SessionObserver"/> to override only what is needed.
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

        /// <summary>Host only: apply the edit; the next snapshot confirms it.</summary>
        void OnStateEditReceived(StateEditMessage edit);

        /// <summary>Another player's position update arrived.</summary>
        void OnPlayerStateReceived(PlayerStateMessage state);

        /// <summary>A complete blob (all chunks reassembled) arrived on a named channel.</summary>
        void OnBlobReceived(string channel, byte[] data);

        /// <summary>A complete epoch-tagged blob; the base forwards to the two-argument overload.</summary>
        void OnBlobReceived(string channel, long transferId, byte[] data);

        /// <summary>An atomic world-sync control stage arrived.</summary>
        void OnWorldSyncControl(WorldSyncStage stage, long epoch, float resumeSpeed,
            ConnectionId connection);

        /// <summary>
        /// Host only: stream the world to <paramref name="connection"/>, or everyone for
        /// <see cref="ConnectionId.None"/>.
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
