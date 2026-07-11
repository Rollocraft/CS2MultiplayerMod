using Game;
using Game.SceneFlow;
using Unity.Entities;
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
            Mod.log.Info(nameof(MultiplayerSystem) + " created.");

            // Trend counters for the flight log: live preview Temps and definition
            // entities should both hover near zero between edits - either climbing
            // steadily during a session is a leak.
            _tempEntities = GetEntityQuery(ComponentType.ReadOnly<global::Game.Tools.Temp>());
            _definitionEntities = GetEntityQuery(ComponentType.ReadOnly<global::Game.Tools.CreationDefinition>());
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            if (!MultiplayerService.ModEnabled)
            {
                if (service.Session.Role != SessionRole.None)
                {
                    Mod.log.Info("[MP] Mod disabled in settings - closing the active session.");
                    service.Disconnect();
                }
                return;
            }

            service.Update();
            PumpHealth(service);
        }

        /// <summary>
        /// One flight-log line every 10 s while multiplayer is active (60 s while idle):
        /// process memory/CPU/GC, entity trends, transport/blob progress, peer latency,
        /// world-load state and the most recently applied command. After a crash the tail
        /// distinguishes a resource ramp, stalled transfer and operation-specific native CTD.
        /// </summary>
        private void PumpHealth(MultiplayerService service)
        {
            if (!FlightRecorder.Enabled) return;
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
                FlightRecorder.NoteException("health-snapshot", ex);
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

            FlightRecorder.Note("health role=" + session.Role +
                " status=" + session.Status +
                " phase=" + service.WorldPhase +
                " gameLoading=" + gameLoading +
                " playerId=" + session.LocalPlayerId +
                " peers=" + peers +
                " pendingPeers=" + pendingPeers +
                " remotePlayers=" + remotePlayers +
                " latencyMS=" + latency +
                " oldestPeerAgeMS=" + oldestPeerAge +
                " entities=" + Value(entities) +
                " temps=" + Value(temps) +
                " defs=" + Value(definitions) +
                " sendKB=" + (session.PendingSendBytes >> 10) +
                " incomingBlob=" + incomingChannel +
                " incomingKB=" + (session.IncomingBlobReceived >> 10) + "/" + (session.IncomingBlobTotal >> 10) +
                " outgoingBlob=" + session.OutgoingBlobActive +
                " outgoingKB=" + (session.OutgoingBlobSent >> 10) + "/" + (session.OutgoingBlobTotal >> 10) +
                " " + service.CommandDiagnosticSnapshot(now) +
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
