using Game;
using Game.SceneFlow;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// ECS heartbeat for multiplayer. Runs at <see cref="global::Game.SystemUpdatePhase.UIUpdate"/>
    /// (every frame, even when paused/in menu) pumping <see cref="MultiplayerService"/>. Also enforces
    /// the "Enable Mod" setting: turning it off closes any active session. Declared <c>partial</c>
    /// because Unity's Entities source generators extend system types.
    /// </summary>
    public partial class MultiplayerSystem : GameSystemBase
    {
        private const long ActiveHealthIntervalMs = 10000;
        private const long IdleHealthIntervalMs = 60000;

        private EntityQuery _tempEntities;
        private EntityQuery _definitionEntities;
        private long _lastHealthMs;
        private bool _wroteHealth;

        protected override void OnCreate()
        {
            base.OnCreate();
            SyncLog.Detail(LogTopic.Startup, nameof(MultiplayerSystem) + " created.");

            // Trend counters for the flight log: live preview Temps and definition
            // entities should both hover near zero between edits - either climbing
            // steadily during a session is a leak.
            _tempEntities = GetEntityQuery(ComponentType.ReadOnly<global::Game.Tools.Temp>());
            _definitionEntities = GetEntityQuery(ComponentType.ReadOnly<global::Game.Tools.CreationDefinition>());
        }

        /// <summary>
        /// The game is about to replace the world - exiting to the main menu, loading
        /// another city, starting a new one. This fires while the outgoing world (and its
        /// sockets) are still alive, which is the moment a session has to be closed
        /// properly. Failures are swallowed on purpose: the base class disables a system
        /// that throws here, and losing the multiplayer pump is worse than a missed leave
        /// notice (the per-frame watcher covers it).
        /// </summary>
        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose,
            global::Game.GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            try
            {
                MultiplayerService service = Mod.Service;
                if (service != null) service.HandleWorldTransition(purpose, mode);
            }
            catch (System.Exception ex)
            {
                SyncLog.Error(LogTopic.Startup, "Closing the session on a world transition failed.",
                    ex);
            }
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            if (!MultiplayerService.ModEnabled)
            {
                if (service.Session.Role != SessionRole.None)
                {
                    SyncLog.Detail(LogTopic.Startup,
                        "Mod disabled in settings - closing the active session.");
                    service.Disconnect();
                }

                // Disconnecting a client can queue a safe return to the main menu while
                // its streamed world is still loading. Keep the lifecycle pump alive even
                // with gameplay sync disabled so that deferred close and cleanup can finish.
                service.Update(World);
                PumpHealth(service);
                return;
            }

            service.Update(World);
            PumpHealth(service);

            // This system runs at UIUpdate, which the game drives once per rendered frame, so it
            // is the honest place to time one. Only while gameplay is live: a world load would
            // otherwise report its own multi-second frames as the session's.
            if (service.GameplaySyncReady) FrameProbe.Sample();
            else FrameProbe.Reset();
        }

        /// <summary>
        /// One flight-log line every 10 s while multiplayer is active (60 s while idle):
        /// process memory/CPU/GC, entity trends, transport/blob progress, peer latency,
        /// world-load state and the most recently applied command. After a crash the tail
        /// distinguishes a resource ramp, stalled transfer and operation-specific native CTD.
        /// </summary>
        private void PumpHealth(MultiplayerService service)
        {
            if (!SyncLog.IsRecording(LogTopic.Performance)) return;
            MultiplayerSession session = service.Session;
            long now = service.NowMs;
            bool active = session.Role != SessionRole.None ||
                          session.Status != SessionStatus.Offline ||
                          service.WorldPhase != ClientWorldPhase.None;
            long interval = active ? ActiveHealthIntervalMs : IdleHealthIntervalMs;
            if (_wroteHealth && now - _lastHealthMs < interval) return;
            _lastHealthMs = now;
            _wroteHealth = true;

            try
            {
                WriteHealth(service, session, now);
            }
            catch (System.Exception ex)
            {
                // Diagnostics are never allowed to become the crash they are meant to explain.
                SyncLog.Error(LogTopic.Performance, "Could not write the periodic health snapshot.",
                    ex);
            }
        }

        private void WriteHealth(MultiplayerService service, MultiplayerSession session, long now)
        {
            int entities = SafeEntityCount();
            int temps = SafeQueryCount(_tempEntities);
            int definitions = SafeQueryCount(_definitionEntities);

            int peers = 0;
            int pendingPeers = 0;
            int latencyMin = int.MaxValue;
            int latencyMax = -1;
            long latencyTotal = 0;
            int latencySamples = 0;
            long oldestPeerAge = 0;
            foreach (Peer peer in session.Peers)
            {
                if (!peer.Handshaked)
                {
                    pendingPeers++;
                    continue;
                }

                peers++;
                if (peer.LatencyMs >= 0)
                {
                    if (peer.LatencyMs < latencyMin) latencyMin = peer.LatencyMs;
                    if (peer.LatencyMs > latencyMax) latencyMax = peer.LatencyMs;
                    latencyTotal += peer.LatencyMs;
                    latencySamples++;
                }
                long age = now - peer.LastSeenUnixMs;
                if (age > oldestPeerAge) oldestPeerAge = age;
            }

            int remotePlayers = 0;
            foreach (RemotePlayer ignored in service.RemotePlayers) remotePlayers++;

            bool gameLoading = false;
            try { gameLoading = GameManager.instance != null && GameManager.instance.isGameLoading; }
            catch { }

            string latency = latencySamples == 0
                ? "?"
                : latencyMin + "/" + (latencyTotal / latencySamples) + "/" + latencyMax;
            string incomingChannel = string.IsNullOrEmpty(session.IncomingBlobChannel)
                ? "none"
                : session.IncomingBlobChannel;

            SyncLog.Trace(LogTopic.Performance, "health role=" + session.Role + " status=" +
                session.Status + " phase=" + service.WorldPhase + " gameLoading=" + gameLoading +
                " playerId=" + session.LocalPlayerId + " peers=" + peers + " pendingPeers=" +
                pendingPeers + " remotePlayers=" + remotePlayers + " latencyMS=" + latency +
                " oldestPeerAgeMS=" + oldestPeerAge + " entities=" + Value(entities) + " temps=" +
                Value(temps) + " defs=" + Value(definitions) + " sendKB=" +
                (session.PendingSendBytes >> 10) + " incomingBlob=" + incomingChannel +
                " incomingKB=" + (session.IncomingBlobReceived >> 10) + "/" +
                (session.IncomingBlobTotal >> 10) + " outgoingBlob=" + session.OutgoingBlobActive +
                " outgoingKB=" + (session.OutgoingBlobSent >> 10) + "/" +
                (session.OutgoingBlobTotal >> 10) + " " + service.CommandDiagnosticSnapshot(now) +
                " " + FlightRecorder.ProcessSnapshot());
        }

        private int SafeEntityCount()
        {
            try { return EntityManager.Debug.EntityCount; }
            catch { return -1; }
        }

        private static int SafeQueryCount(EntityQuery query)
        {
            try { return query.CalculateEntityCount(); }
            catch { return -1; }
        }

        private static string Value(int value) => value < 0 ? "?" : value.ToString();
    }
}
