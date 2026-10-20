using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Localization;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// A joining client's world-handover stage. No command is captured or applied until the host's
    /// world has loaded (<see cref="MultiplayerService.GameplaySyncReady"/>).
    /// </summary>
    public enum ClientWorldPhase
    {
        None,
        Connecting,
        Reconnecting,
        WaitingForMap,
        LoadingMap,
        WaitingForResume,
        InSession,
    }

    /// <summary>
    /// Bridge between the mod lifecycle/UI and the portable <see cref="MultiplayerSession"/>: created in
    /// <see cref="Mod.OnLoad"/>, pumped by <see cref="MultiplayerSystem"/>. Owns the session clock, maps
    /// settings to a <see cref="MultiplayerConfig"/>, and registers the blob and command allow-lists.
    /// </summary>
    public sealed partial class MultiplayerService
    {
        private const int DefaultPort = 25001;
        private const int DefaultMaxPlayers = 8;

        /// <summary>Ceiling for a streamed savegame (real saves are tens of MB).</summary>
        private const int MaxSaveBlobBytes = 256 * 1024 * 1024;

        /// <summary>If a received world never starts loading in this time, give up and recover.</summary>
        private const long MapLoadTimeoutMs = 120000;

        private const string MapChannel = "map";

        private readonly IModLogger _log;
        private readonly MultiplayerSession _session;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ConcurrentDictionary<int, RemotePlayer> _remotePlayers =
            new ConcurrentDictionary<int, RemotePlayer>();
        private ClientWorldPhase _phase = ClientWorldPhase.None;
        private long _worldInstallGeneration;
        private long _phaseChangedMs;
        private bool _sawLoading;
        private string _lastFault;

        // Runs before the realization observers, so the breadcrumb is flushed before a received command
        // reaches a crash-prone native operation.
        private long _appliedCommandTotal;
        private ushort _lastAppliedCommandId;
        private int _lastAppliedCommandOrigin;
        private int _lastAppliedCommandBytes;
        private long _lastAppliedCommandMs;
        private ushort _lastLoggedCommandId;
        private long _lastCommandLogMs;
        private long _lastCommandLoggedTotal;

        public MultiplayerService(IModLogger log)
        {
            _log = log;
            _session = new MultiplayerSession(log);
            _session.AddObserver(new ServiceObserver(this));

            // Allow-lists: anything outside them disconnects the peer.
            _session.AllowBlobChannel(MapChannel, MaxSaveBlobBytes);
            GameplayCommandRegistry.Register(_session);
        }

        public MultiplayerSession Session => _session;

        /// <summary>Monotonic millisecond clock shared with systems that need timing.</summary>
        public long NowMs => _clock.ElapsedMilliseconds;

        /// <summary>Latest known positions of the other players, for rendering their cursors.</summary>
        public IEnumerable<RemotePlayer> RemotePlayers => _remotePlayers.Values;

        /// <summary>The joining client's place in the world-handover flow.</summary>
        public ClientWorldPhase WorldPhase => _phase;

        /// <summary>Installed host worlds; advances only on an accepted Resume, never on an abort.</summary>
        public long WorldInstallGeneration => _worldInstallGeneration;

        /// <summary>Master switch from the settings screen.</summary>
        public static bool ModEnabled => Mod.Setting == null || Mod.Setting.EnableMod;

        /// <summary>
        /// The gate for capturing or applying gameplay: mod enabled, connected, and on a client the host's
        /// world loaded.
        /// </summary>
        public bool GameplaySyncReady =>
            ModEnabled &&
            _session.Status == SessionStatus.Connected &&
            !_worldSyncBarrierActive &&
            (_session.Role == SessionRole.Host || _phase == ClientWorldPhase.InSession);

        /// <summary>
        /// Whether the host replicates simulation decisions (growables, occupants, tenants, demand). Clients
        /// answer with what the host announced; player edits never consult this.
        /// </summary>
        public bool SimulationSyncEnabled => _session.SimulationSyncEnabled;

        /// <summary>
        /// <see cref="GameplaySyncReady"/> for the simulation systems. Off, they behave as in a closed
        /// session: drain the queue and release their native holds.
        /// </summary>
        public bool SimulationSyncReady => GameplaySyncReady && _session.SimulationSyncEnabled;

        internal string CommandDiagnosticSnapshot(long nowMs)
        {
            if (_appliedCommandTotal == 0) return "commands=0 lastCommand=none";
            long age = nowMs - _lastAppliedCommandMs;
            if (age < 0) age = 0;
            return "commands=" + _appliedCommandTotal +
                   " lastCommand=" + CommandName(_lastAppliedCommandId) +
                   " lastCommandId=" + _lastAppliedCommandId +
                   " lastCommandOrigin=" + _lastAppliedCommandOrigin +
                   " lastCommandBytes=" + _lastAppliedCommandBytes +
                   " lastCommandAgeMS=" + age;
        }

        private void RecordAppliedCommand(SimulationCommandMessage command)
        {
            if (command == null) return;

            long now = _clock.ElapsedMilliseconds;
            _appliedCommandTotal++;
            _lastAppliedCommandId = command.CommandId;
            _lastAppliedCommandOrigin = command.OriginPlayerId;
            _lastAppliedCommandBytes = command.Body != null ? command.Body.Length : 0;
            _lastAppliedCommandMs = now;

            // Log the first command, every type change and one sample per second.
            if (_lastCommandLoggedTotal != 0 &&
                command.CommandId == _lastLoggedCommandId &&
                now - _lastCommandLogMs < 1000)
                return;

            long commandsSinceLog = _appliedCommandTotal - _lastCommandLoggedTotal;
            _lastLoggedCommandId = command.CommandId;
            _lastCommandLogMs = now;
            _lastCommandLoggedTotal = _appliedCommandTotal;
            SyncLog.Trace(LogTopic.Session, "command-apply name=" + CommandName(command.CommandId) +
                " id=" + command.CommandId + " origin=" + command.OriginPlayerId + " tick=" +
                command.Tick + " bytes=" + _lastAppliedCommandBytes + " sinceLast=" +
                commandsSinceLog + " total=" + _appliedCommandTotal);
        }

        private void ResetCommandDiagnostics()
        {
            _appliedCommandTotal = 0;
            _lastAppliedCommandId = 0;
            _lastAppliedCommandOrigin = 0;
            _lastAppliedCommandBytes = 0;
            _lastAppliedCommandMs = 0;
            _lastLoggedCommandId = 0;
            _lastCommandLogMs = 0;
            _lastCommandLoggedTotal = 0;
        }

        private static string CommandName(ushort id) => GameplayCommandRegistry.Name(id);

        // Status texts are re-read every UI frame, so resolving through L10n follows a live language switch.
        // ---- Autosave guard (client only) -------------------------------------
        private bool _autosaveSuppressed;
        private bool _autosaveWasEnabled;
        public void SendChat(string text) => _session.SendChat(text);

        /// <summary>/sync: ask the host for a fresh world stream (host: refresh everyone).</summary>
        public void RequestWorldSync() => _session.RequestWorldSync();

        private string _lastDiagnosticsFile;
        private bool _lastDiagnosticsFailed;

        /// <summary>What Save Diagnostics last did this run, for the options screen.</summary>
        public string DiagnosticsStatusText =>
            _lastDiagnosticsFailed ? L10n.T(L10n.Key.DiagnosticsFailed)
            : _lastDiagnosticsFile == null ? L10n.T(L10n.Key.DiagnosticsNone)
            : L10n.F(L10n.Key.DiagnosticsSaved, _lastDiagnosticsFile);

        /// <summary>
        /// Save Diagnostics and /diag: this machine's session state plus the flight log in one file. Works
        /// offline too, so a join that failed can still be reported. Returns the file name, or null.
        /// </summary>
        public string ExportDiagnostics(string reason)
        {
            var snapshot = new System.Text.StringBuilder();
            snapshot.Append("mod=").Append(Mod.StampedVersion)
                .Append(" protocol=").Append(ProtocolConstants.ProtocolVersion)
                .Append(" game=").Append(GameVersionCheck.CurrentVersion).AppendLine();
            snapshot.Append("role=").Append(_session.Role).Append(" status=").Append(_session.Status)
                .Append(" phase=").Append(_phase).Append(" simulationSync=").Append(SimulationSyncEnabled)
                .AppendLine();
            snapshot.Append("lastFault=").Append(_lastFault ?? "none").AppendLine();
            snapshot.Append("unsupportedMods=").Append(ModsCheck.Summary()).AppendLine();
            snapshot.Append("modSet=[").Append(string.Join(", ", ModsCheck.Manifest)).Append(']').AppendLine();
            foreach (Peer peer in _session.Peers)
                snapshot.Append("peer #").Append(peer.PlayerId).Append(" '").Append(peer.Name)
                    .Append("' handshaked=").Append(peer.Handshaked)
                    .Append(" latencyMs=").Append(peer.LatencyMs)
                    .Append(" mod=").Append(peer.ModVersion ?? "?")
                    .Append(" build=").Append(peer.BuildId ?? "?").AppendLine();
            snapshot.Append("process ").Append(FlightRecorder.ProcessSnapshot());

            string file = FlightRecorder.ExportBundle(reason, snapshot.ToString());
            _lastDiagnosticsFailed = file == null;
            if (file != null) _lastDiagnosticsFile = file;
            if (file != null)
                _log.Event(LogTopic.Session, "Diagnostics saved to Logs/" + file + " (" + reason + ").");
            else
                _log.Warn(LogTopic.Session, "Diagnostics could not be saved (" + reason + ").");
            return file;
        }

        /// <summary>
        /// One automatic recovery repairs a divergence; a second inside this window is a storm that freezes
        /// both players and re-triggers after every reload. Every automatic caller goes through here; only
        /// manual /sync and the settings button call <see cref="RequestWorldSync"/> directly.
        /// </summary>
        private const long AutoRecoveryCooldownMs = 90000;
        private long _lastAutoRecoveryMs = long.MinValue;

        /// <summary>True while a world reload is already under way, in either role.</summary>
        private bool WorldRecoveryInFlight =>
            _worldSyncBarrierActive ||
            _phase == ClientWorldPhase.WaitingForMap ||
            _phase == ClientWorldPhase.LoadingMap ||
            _phase == ClientWorldPhase.WaitingForResume;

        /// <summary>A settled report waiting for the service tick to act on it. Main thread only.</summary>
        private Diagnostics.ResyncReport _settledReport;

        /// <summary>
        /// The handover broke and no world is coming: re-ask the host, bypassing the arbiter and the
        /// in-flight guard. Not a divergence claim.
        /// </summary>
        private bool _mapReRequestPending;

        internal void RequestMapAgainNextTick() => _mapReRequestPending = true;

        /// <summary>
        /// Pumped rather than direct: a request made inside the broken epoch is coalesced away.
        /// </summary>
        private void PumpMapReRequest()
        {
            if (!_mapReRequestPending) return;
            if (_session == null || _session.Status != SessionStatus.Connected)
            {
                _mapReRequestPending = false;
                return;
            }
            if (_session.WorldSyncSuspended) return;
            _mapReRequestPending = false;
            Diagnostics.SyncLog.Warn(LogTopic.Session,
                "World sync: asking the host to stream this city again - the previous handover " +
                "resumed before the snapshot had been installed.");
            _session.RequestAutomaticWorldSync("resume arrived before the snapshot finished loading");
        }

        /// <summary>
        /// The resync gate behind <see cref="Sync.Infrastructure.SyncInbox.Arbitrate"/>. The verdict is
        /// synchronous; only <see cref="Diagnostics.ResyncVerdict.Settled"/> reloads, and the reload starts
        /// from the service tick, not inside ToolUpdate.
        /// </summary>
        public Diagnostics.ResyncVerdict SettleResyncReport(Diagnostics.ResyncReport report)
        {
            if (report == null) return Diagnostics.ResyncVerdict.Settled;
            if (_session == null || _session.Status != SessionStatus.Connected)
                return Diagnostics.ResyncVerdict.AlreadyRecovering;

            Diagnostics.ResyncVerdict verdict =
                Diagnostics.ResyncArbiter.Submit(report, NowMs, WorldRecoveryInFlight);
            // First settled report wins; one reload answers both.
            if (verdict == Diagnostics.ResyncVerdict.Settled && _settledReport == null)
                _settledReport = report;
            return verdict;
        }

        public void RequestAutomaticWorldRecovery(string reason) =>
            RequestAutomaticWorldRecovery(Diagnostics.ResyncReport.FromReason(reason));

        /// <summary>Weighs a report from a caller that already dropped its work; reloads if it settles.</summary>
        public void RequestAutomaticWorldRecovery(Diagnostics.ResyncReport report)
        {
            if (report == null || _session == null || _session.Status != SessionStatus.Connected) return;
            if (Diagnostics.ResyncArbiter.Submit(report, NowMs, WorldRecoveryInFlight) !=
                Diagnostics.ResyncVerdict.Settled) return;
            RunAutomaticWorldRecovery(report);
        }

        /// <summary>
        /// Reloads for reports whose hold ran out: a retrying subsystem withdraws its report on success.
        /// </summary>
        private void PumpMaturedResyncReports()
        {
            if (_session == null || _session.Status != SessionStatus.Connected) return;
            // A running reload supersedes anything held.
            if (WorldRecoveryInFlight) return;

            // Verdicts settled inside a frame (see SettleResyncReport) are acted on here.
            Diagnostics.ResyncReport settled = _settledReport;
            _settledReport = null;
            if (settled != null) RunAutomaticWorldRecovery(settled);

            System.Collections.Generic.List<Diagnostics.ResyncReport> matured =
                Diagnostics.ResyncArbiter.TakeMatured(NowMs);
            if (matured == null || matured.Count == 0) return;
            // One reload settles every one of them; the rest are folded into it.
            RunAutomaticWorldRecovery(matured[0]);
        }

        private void RunAutomaticWorldRecovery(Diagnostics.ResyncReport report)
        {
            long now = NowMs;
            // `now - long.MinValue` wraps negative; guard the sentinel first.
            bool coolingDown = _lastAutoRecoveryMs != long.MinValue &&
                               now - _lastAutoRecoveryMs < AutoRecoveryCooldownMs;
            if (coolingDown)
            {
                Diagnostics.SyncLog.Warn(LogTopic.Session,
                    "World sync: skipped a second automatic reload within " +
                    (AutoRecoveryCooldownMs / 1000) + " s (" + report.Summary() +
                    "). The edit behind it is left un-synced; use /sync if the city looks out of step.");
                return;
            }
            _lastAutoRecoveryMs = now;
            Diagnostics.SyncLog.Event(LogTopic.Session,
                "World sync: reloading this city from the host now (" + report.Summary() + ").");
            // The subject tells host-only logs which client inbox failed.
            _session.RequestAutomaticWorldSync(report.Reason + " [" + report.Subject + "]");
        }

        // ---- Chat log (in-game hub panel) --------------------------------------

        private readonly object _chatLock = new object();
        private readonly SessionFeed _sessionFeed = new SessionFeed();
        private string _playerListJson = "[]";

        /// <summary>
        /// Typed player messages and session activity for the hub. Rebuilt only on change;
        /// activity has its own retention budget and never counts as unread player chat.
        /// </summary>
        public string ChatLogJson => _sessionFeed.Json;

        /// <summary>Host participant list, rebuilt on membership change; the local host is never kickable.</summary>
        public string PlayerListJson { get { lock (_chatLock) return _playerListJson; } }

        /// <summary>Remove one authenticated client selected in the host player list.</summary>
        public void KickPlayerFromUi(int playerId)
        {
            if (!_session.KickPlayer(playerId))
                _log.Warn(LogTopic.Session, "Ignored kick request for unavailable player #" +
                    playerId + ".");
        }

        /// <summary>Remove a client and block its address for the current hosting session.</summary>
        public void BanPlayerFromUi(int playerId)
        {
            if (!_session.BanPlayer(playerId))
                _log.Warn(LogTopic.Session, "Ignored ban request for unavailable player #" +
                    playerId + ".");
        }

        private void RefreshPlayerListJson()
        {
            lock (_chatLock)
            {
                if (_session.Role != SessionRole.Host)
                {
                    _playerListJson = "[]";
                    return;
                }

                var peers = new List<Peer>();
                foreach (Peer peer in _session.Peers)
                    if (peer.Handshaked) peers.Add(peer);
                peers.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));

                var sb = new System.Text.StringBuilder((peers.Count + 1) * 56 + 2);
                sb.Append("[{\"id\":").Append(_session.LocalPlayerId).Append(",\"name\":");
                AppendJsonString(sb, _session.LocalPlayerName);
                sb.Append(",\"isHost\":true,\"color\":\"")
                    .Append(Sync.Players.RemotePlayerMarkerSystem.PlayerColorCss(_session.LocalPlayerId))
                    .Append("\"}]");

                if (peers.Count > 0)
                {
                    // Replace the closing bracket: one small allocation.
                    sb.Length--;
                    for (int i = 0; i < peers.Count; i++)
                    {
                        Peer peer = peers[i];
                        sb.Append(",{\"id\":").Append(peer.PlayerId).Append(",\"name\":");
                        AppendJsonString(sb, peer.Name);
                        sb.Append(",\"isHost\":false,\"color\":\"")
                            .Append(Sync.Players.RemotePlayerMarkerSystem.PlayerColorCss(peer.PlayerId))
                            .Append("\"}");
                    }
                    sb.Append(']');
                }
                _playerListJson = sb.ToString();
            }
        }

        /// <summary>Mirrors session events into the mod log and records remote player positions.</summary>
        private sealed class ServiceObserver : SessionObserver
        {
            private readonly MultiplayerService _service;
            private readonly IModLogger _log;
            public ServiceObserver(MultiplayerService service) { _service = service; _log = service._log; }

            public override void OnStatusChanged(SessionStatus status, string detail)
            {
                _log.Detail(LogTopic.Session, status + ": " + detail);
                // The flight log is shared publicly: keep the host in the main log, only port and transport here.
                string flightDetail = status == SessionStatus.Connecting
                    ? "target=redacted port=" + _service._session.Port +
                      " encryption=" + _service._session.EncryptionActive
                    : detail;
                SyncLog.Trace(LogTopic.Session, "status " + status + " role=" +
                    _service._session.Role +
                    (string.IsNullOrEmpty(flightDetail) ? "" : " detail=" + flightDetail));
                if (status == SessionStatus.Connected &&
                    _service._session.Role == SessionRole.Client &&
                    (_service._phase == ClientWorldPhase.Connecting || _service._phase == ClientWorldPhase.Reconnecting))
                {
                    // Authenticated; the host streams its world to every fresh join.
                    _service.SetPhase(ClientWorldPhase.WaitingForMap);
                }
                else if (status == SessionStatus.Reconnecting)
                {
                    _service._lastFault = null;
                    _service.ResetWorldSyncState(restoreSpeed: false);
                    _service._worldSyncBarrierActive = true;
                    _service._worldSyncInputLocked = true;
                    _service.SetPhase(ClientWorldPhase.Reconnecting);
                    _service._remotePlayers.Clear();
                    Sync.Infrastructure.SyncInbox.DrainAll();
                    Diagnostics.ResyncArbiter.Reset();
                    _service.MaintainWorldSyncBarrier();
                }
                else if (status == SessionStatus.Offline || status == SessionStatus.Faulted)
                {
                    // Core knows nothing of worlds: request the deferred exit before clearing the client phase.
                    if (_service._clientHostWorldActive)
                    {
                        string reason = !string.IsNullOrWhiteSpace(detail) && detail != "Stopped"
                            ? detail
                            : "The connection to the host closed.";
                        _service.QueueClientMainMenu(reason);
                    }

                    if (status == SessionStatus.Faulted) _service._lastFault = detail;
                    _service.ResetWorldSyncState(restoreSpeed: true);
                    _service.SetPhase(ClientWorldPhase.None);
                    _service._remotePlayers.Clear();
                    _service._mapPings.Clear();
                    _service.StopFollowingPlayer();
                }

                // The status view owns connection guidance and transfer progress. The activity
                // history gets one concise lifecycle event, rather than paragraphs in player chat.
                if (status == SessionStatus.Connected && _service._session.Role == SessionRole.Host)
                {
                    _service.AppendSessionActivity(L10n.T("CS2MP.Activity.Hosting"));
                }
                else if (status == SessionStatus.Connected && _service._session.Role == SessionRole.Client)
                {
                    _service.AppendSessionActivity(L10n.T("CS2MP.Activity.Connected"));
                }
                else if (status == SessionStatus.Offline && _lastStatus == SessionStatus.Connected)
                {
                    // A clean end: clear stale fault text from an earlier failed attempt.
                    _service._lastFault = null;
                    _service.AppendSessionActivity(L10n.T("CS2MP.Activity.Closed"));
                }
                else if (status == SessionStatus.Faulted)
                    _service.AppendSessionActivity(FriendlyFaultSummary(detail), error: true);
                _service.RefreshPlayerListJson();
                _lastStatus = status;
            }

            private SessionStatus _lastStatus = SessionStatus.Offline;

            public override void OnPeerJoined(Peer peer)
            {
                _log.Event(LogTopic.Session, "Peer joined: " + peer);
                _service.RefreshPlayerListJson();
                // WorldResyncSystem observes joins too and pushes the live world to the newcomer.
            }
            public override void OnPeerLeft(Peer peer, string reason)
            {
                _log.Event(LogTopic.Session, "Peer left: " + peer + " (" + reason + ")");
                _service._remotePlayers.TryRemove(peer.PlayerId, out RemotePlayer removed);
                _service.RefreshPlayerListJson();
            }
            public override void OnChatReceived(string sender, string text)
            {
                _log.Detail(LogTopic.Session, (sender ?? "system") + ": " + text);
                if (sender == null)
                    _service.AppendSessionActivity(text);
                else
                    _service.AppendPlayerChat(sender, text);
            }
            public override void OnCommandReceived(SimulationCommandMessage command) =>
                _service.RecordAppliedCommand(command);
            public override void OnMapPingReceived(MapPingMessage ping) => _service.RecordMapPing(ping);
            public override void OnPlayerStateReceived(PlayerStateMessage state) => _service.RecordRemotePlayer(state);
            public override void OnBlobReceived(string channel, long transferId, byte[] data)
            {
                if (channel == MapChannel) _service.LoadReceivedMap(transferId, data);
            }
            public override void OnWorldSyncControl(WorldSyncStage stage, long epoch,
                float resumeSpeed, Core.Networking.ConnectionId connection) =>
                    _service.HandleWorldSyncControl(stage, epoch, resumeSpeed);
            public override void OnError(string message)
            {
                _service._lastFault = message;
                _log.Error(LogTopic.Session, message);
            }
        }
    }

    /// <summary>A snapshot of another player's map cursor, kept by <see cref="MultiplayerService"/>.</summary>
    public sealed class RemotePlayer
    {
        public int PlayerId;
        // Camera focus on the ground.
        public float X;
        public float Y;
        public float Z;
        // Camera eye position in the air.
        public float EyeX;
        public float EyeY;
        public float EyeZ;
        public float Yaw;
        public float Pitch;
        public float Zoom;
        public string Name;
        public Core.Protocol.Messages.PlayerHoverShape[] Hover =
            System.Array.Empty<Core.Protocol.Messages.PlayerHoverShape>();
        public long LastUpdateMs;
    }
}
