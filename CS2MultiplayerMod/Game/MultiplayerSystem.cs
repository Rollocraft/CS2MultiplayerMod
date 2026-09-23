using Game;
using Game.SceneFlow;
using Unity.Entities;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Pumps <see cref="MultiplayerService"/> at <see cref="global::Game.SystemUpdatePhase.UIUpdate"/>
    /// (every frame, paused or in menus) and closes the session when the mod is disabled.
    /// </summary>
    public partial class MultiplayerSystem : GameSystemBase
    {
        private const long ActiveHealthIntervalMs = 10000;
        private const long IdleHealthIntervalMs = 60000;

        private global::Game.Simulation.SimulationSystem _simulation;
        private EntityQuery _tempEntities;
        private EntityQuery _definitionEntities;
        private long _lastHealthMs;
        private bool _wroteHealth;

        protected override void OnCreate()
        {
            base.OnCreate();
            _simulation = World.GetOrCreateSystemManaged<global::Game.Simulation.SimulationSystem>();
            SyncLog.Detail(LogTopic.Startup, nameof(MultiplayerSystem) + " created.");

            // Flight-log trends: Temps and definitions should hover near zero; steady growth is a leak.
            _tempEntities = GetEntityQuery(ComponentType.ReadOnly<global::Game.Tools.Temp>());
            _definitionEntities = GetEntityQuery(ComponentType.ReadOnly<global::Game.Tools.CreationDefinition>());
        }

        /// <summary>
        /// The world is about to be replaced while its sockets are still alive: close the session properly.
        /// Failures are swallowed, since a throwing system gets disabled and the per-frame watcher backs this up.
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

                // Keep pumping with gameplay sync off, so a deferred client close and cleanup can finish.
                service.Update(World);
                PumpHealth(service);
                return;
            }

            service.Update(World);
            PumpHealth(service);

            // Once per rendered frame; only while gameplay is live, so load frames are not counted.
            if (service.GameplaySyncReady)
                FrameProbe.Sample(_simulation.selectedSpeed, _simulation.frameIndex);
            else FrameProbe.Reset();
        }

        /// <summary>
        /// A flight-log health line every 10 s while active (60 s idle): memory, CPU, GC, entity trends,
        /// transfer progress, latency, load state, last applied command.
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
