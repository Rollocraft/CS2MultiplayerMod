using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    /// <summary>
    /// Where a joining client stands in the world-handover flow. Gameplay sync is
    /// gated on <see cref="MultiplayerService.GameplaySyncReady"/>: no command is
    /// captured or applied until the host's world has actually finished loading, so
    /// remote edits never land in a half-replaced city.
    /// </summary>
    public enum ClientWorldPhase
    {
        None,
        Connecting,
        WaitingForMap,
        LoadingMap,
        WaitingForResume,
        InSession,
    }

    /// <summary>
    /// Process-wide bridge between the mod lifecycle / UI and the portable
    /// <see cref="MultiplayerSession"/>. Created once in <see cref="Mod.OnLoad"/> and
    /// pumped every simulation tick by <see cref="MultiplayerSystem"/>.
    /// It owns the monotonic clock the session needs and translates the settings screen's
    /// strings into a <see cref="MultiplayerConfig"/>. It also registers the security
    /// allow-lists: which blob channels a client accepts and which command ids peers may send.
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

        // The service observer runs before the individual realization observers. Keeping
        // this breadcrumb here means it is flushed before a received command can enter a
        // crash-prone game operation, including failures in native code with no stack trace.
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

            // Security allow-lists (secure by default in the core): the one blob channel
            // a client may receive, and the complete set of gameplay command ids. A peer
            // sending anything outside these is disconnected.
            _session.AllowBlobChannel(MapChannel, MaxSaveBlobBytes);
            GameplayCommandRegistry.Register(_session);
        }

        public MultiplayerSession Session => _session;

        /// <summary>Monotonic millisecond clock shared with systems that need timing.</summary>
        public long NowMs => _clock.ElapsedMilliseconds;

        /// <summary>Latest known positions of the other players, for rendering their cursors.</summary>
        public IEnumerable<RemotePlayer> RemotePlayers => _remotePlayers.Values;

        /// <summary>How many remote players are tracked, without walking the enumerator.</summary>
        public int RemotePlayerCount => _remotePlayers.Count;

        /// <summary>The tracked position of one player, or null when that id is unknown.</summary>
        public RemotePlayer FindRemotePlayer(int playerId)
        {
            RemotePlayer player;
            return _remotePlayers.TryGetValue(playerId, out player) ? player : null;
        }

        /// <summary>
        /// Resolve a player from what someone typed: an exact id, then an exact name, then a
        /// unique prefix. A prefix that matches more than one player resolves to nothing rather
        /// than to an arbitrary one - following the wrong partner is worse than being told to be
        /// more specific.
        /// </summary>
        public RemotePlayer FindRemotePlayerByName(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            query = query.Trim();
            if (query.Length == 0) return null;

            int id;
            if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                RemotePlayer byId = FindRemotePlayer(id);
                if (byId != null) return byId;
            }

            RemotePlayer exact = null;
            RemotePlayer prefix = null;
            bool prefixAmbiguous = false;
            foreach (RemotePlayer player in _remotePlayers.Values)
            {
                string name = PlayerDisplayName(player.PlayerId);
                if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) exact = player;
                else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                {
                    if (prefix != null) prefixAmbiguous = true;
                    prefix = player;
                }
            }
            if (exact != null) return exact;
            return prefixAmbiguous ? null : prefix;
        }

        /// <summary>
        /// The best name we can put on a player id. A client is never sent a roster, so for
        /// anyone but the host it can only offer the id - saying so plainly beats inventing a
        /// name that will not match what that player calls themselves.
        /// </summary>
        public string PlayerDisplayName(int playerId)
        {
            foreach (Peer peer in _session.Peers)
                if (peer.PlayerId == playerId && !string.IsNullOrEmpty(peer.Name)) return peer.Name;
            if (playerId == _session.LocalPlayerId) return _session.LocalPlayerName;
            if (playerId == MultiplayerSession.HostPlayerId) return "Host";
            return "Player " + playerId;
        }

        // ---- Camera intent -----------------------------------------------------------
        // Chat commands run on the UI thread and have no business touching the game camera.
        // They record what they want here; PlayerCursorSyncSystem, which owns the camera
        // reference, carries it out on its next update.

        private int _followPlayerId = -1;
        private float3 _cameraJump;
        private int _cameraJumpPending; // 0/1, Interlocked so the consumer takes it exactly once

        /// <summary>The player the camera is following, or -1.</summary>
        public int FollowPlayerId => Volatile.Read(ref _followPlayerId);

        /// <summary>Ask the camera to jump to a point on its next frame.</summary>
        public void RequestCameraJump(float3 target)
        {
            _cameraJump = target;
            Interlocked.Exchange(ref _cameraJumpPending, 1);
        }

        /// <summary>Consume a pending jump. True exactly once per request.</summary>
        public bool TakeCameraJump(out float3 target)
        {
            if (Interlocked.Exchange(ref _cameraJumpPending, 0) == 0)
            {
                target = default(float3);
                return false;
            }
            target = _cameraJump;
            return true;
        }

        /// <summary>Follow a player's camera focus until they stop reporting or ours moves.</summary>
        public void StartFollowing(int playerId) => Volatile.Write(ref _followPlayerId, playerId);

        public void StopFollowing() => Volatile.Write(ref _followPlayerId, -1);

        /// <summary>Post a line into the local chat feed without sending it to anyone.</summary>
        public void AppendSystemChat(string text) => AppendChatEntry(null, text);

        // ---- Map pings ---------------------------------------------------------------

        private float3 _localCameraFocus;
        private float3 _localPing;
        private int _localPingPending;

        /// <summary>
        /// Where the local camera is looking, republished by PlayerCursorSyncSystem each time it
        /// sends. Chat commands need a point on the map and have no camera reference of their own.
        /// </summary>
        public float3 LocalCameraFocus
        {
            get { return _localCameraFocus; }
            internal set { _localCameraFocus = value; }
        }

        /// <summary>
        /// Drop a beacon at the local camera focus for everyone in the session.
        ///
        /// The sender's own ring is recorded locally rather than waiting for the command to come
        /// back: a host is notified of its own commands and a client is not, so relying on the
        /// echo would draw the host's pings and silently swallow every client's.
        /// </summary>
        public void SendMapPing(string label)
        {
            if (!GameplaySyncReady) return;

            float3 at = _localCameraFocus;
            var command = new Sync.Commands.MapPingCommand
            {
                X = at.x,
                Y = at.y,
                Z = at.z,
                Label = Core.Protocol.WireGuard.SanitizeText(
                    label, Sync.Commands.MapPingCommand.MaxLabelLength),
            };

            try { _session.SendCommand(0, Sync.Commands.MapPingCommand.Id, command.Encode()); }
            catch (Exception ex)
            {
                _log.Warn("[MP] Ping not sent: " + ex.Message);
                return;
            }

            _localPing = at;
            Interlocked.Exchange(ref _localPingPending, 1);
            NotePing(at);

            AppendChatEntry(null, string.IsNullOrEmpty(command.Label)
                ? "Pinged (" + (int)at.x + ", " + (int)at.z + ")."
                : "Pinged (" + (int)at.x + ", " + (int)at.z + "): " + command.Label);
        }

        /// <summary>Consume the local player's own pending ping. True exactly once per send.</summary>
        public bool TakeLocalPing(out float3 position)
        {
            if (Interlocked.Exchange(ref _localPingPending, 0) == 0)
            {
                position = default(float3);
                return false;
            }
            position = _localPing;
            return true;
        }

        private float3 _lastPing;
        private bool _hasLastPing;

        /// <summary>Remember where the most recent ping landed, whoever dropped it.</summary>
        internal void NotePing(float3 position)
        {
            _lastPing = position;
            _hasLastPing = true;
        }

        /// <summary>Where the most recent ping landed, for "/goto ping".</summary>
        public bool TryGetLastPing(out float3 position)
        {
            position = _lastPing;
            return _hasLastPing;
        }

        /// <summary>The joining client's place in the world-handover flow.</summary>
        public ClientWorldPhase WorldPhase => _phase;

        /// <summary>
        /// Client-local generation of successfully installed authoritative worlds. It advances
        /// only when a Resume is accepted after WaitingForResume; aborting back to the old world
        /// deliberately leaves it unchanged.
        /// </summary>
        public long WorldInstallGeneration => _worldInstallGeneration;

        /// <summary>Master switch from the settings screen.</summary>
        public static bool ModEnabled => Mod.Setting == null || Mod.Setting.EnableMod;

        /// <summary>
        /// The one gate every sync system checks before capturing or applying gameplay:
        /// mod enabled, session connected, and - on a client - the host's world fully
        /// loaded. The host is always "in session" with its own world.
        /// </summary>
        public bool GameplaySyncReady =>
            ModEnabled &&
            _session.Status == SessionStatus.Connected &&
            !_worldSyncBarrierActive &&
            (_session.Role == SessionRole.Host || _phase == ClientWorldPhase.InSession);

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

            // Continuous brushes and road drags can produce many commands per second.
            // Log the first, every operation-type change, and one sample per second so
            // the file remains small without losing the operation active at a CTD.
            if (_lastCommandLoggedTotal != 0 &&
                command.CommandId == _lastLoggedCommandId &&
                now - _lastCommandLogMs < 1000)
                return;

            long commandsSinceLog = _appliedCommandTotal - _lastCommandLoggedTotal;
            _lastLoggedCommandId = command.CommandId;
            _lastCommandLogMs = now;
            _lastCommandLoggedTotal = _appliedCommandTotal;
            Diagnostics.FlightRecorder.Note(
                "command-apply name=" + CommandName(command.CommandId) +
                " id=" + command.CommandId +
                " origin=" + command.OriginPlayerId +
                " tick=" + command.Tick +
                " bytes=" + _lastAppliedCommandBytes +
                " sinceLast=" + commandsSinceLog +
                " total=" + _appliedCommandTotal);
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

        private static string CommandName(ushort id)
        {
            return GameplayCommandRegistry.Name(id);
        }

        // All Status*/UiStatus* texts are re-read every UI frame by the options screen
        // and the cs2mp bindings, so resolving them through L10n here makes them follow
        // the game language live (including a language switch mid-session).
        // ---- Autosave guard (client only) -------------------------------------
        private bool _autosaveSuppressed;
        private bool _autosaveWasEnabled;
        public void SendChat(string text) => _session.SendChat(text);

        /// <summary>/sync: ask the host for a fresh world stream (host: refresh everyone).</summary>
        public void RequestWorldSync() => _session.RequestWorldSync();

        /// <summary>
        /// One unresolved remote edit (a missed native capture, an owned sub-element that would not
        /// resolve) must never loop the whole tens-of-MB world through recovery. A single automatic
        /// recovery repairs a genuine divergence; a second inside this window is a storm — it freezes
        /// both players for the length of a save+stream and does not fix the offending edit, which
        /// simply re-triggers after every reload (the exact 52 MB epoch-loop seen in the field). EVERY
        /// automatic caller funnels through here so none can bypass the cap; only manual /sync and the
        /// settings button call <see cref="RequestWorldSync"/> directly.
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
        /// Set when this client must re-ask the host for a world it is otherwise never going to
        /// receive. It deliberately bypasses the resync arbiter and the in-flight guard: this is
        /// not a claim that the two cities diverged, it is a client saying the handover broke and
        /// it is still waiting. See the Resume-before-load case in WorldSync.
        /// </summary>
        private bool _mapReRequestPending;

        internal void RequestMapAgainNextTick() => _mapReRequestPending = true;

        /// <summary>
        /// Ask again for a world whose handover broke, once the session has actually left the epoch
        /// that broke. Waiting for that is why this is a pumped flag rather than a direct call: the
        /// session coalesces any request made while it is still inside the epoch.
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
            Diagnostics.SyncLog.ProdWarn(
                "World sync: asking the host to stream this city again - the previous handover " +
                "resumed before the snapshot had been installed.");
            _session.RequestWorldSync("resume arrived before the snapshot finished loading");
        }

        /// <summary>
        /// The synchronous resync gate wired into <see cref="Sync.Infrastructure.SyncInbox.Arbitrate"/>.
        /// A caller that can still hold its work puts its evidence here and acts on the verdict:
        /// only <see cref="Diagnostics.ResyncVerdict.Settled"/> reloads the world.
        ///
        /// The VERDICT is synchronous - the caller is mid-frame and has to know right now whether to
        /// keep its work. The reload is not: it is handed to the service tick, where world recovery
        /// has always been started from, rather than being kicked off from inside a ToolUpdate.
        /// </summary>
        public Diagnostics.ResyncVerdict SettleResyncReport(Diagnostics.ResyncReport report)
        {
            if (report == null) return Diagnostics.ResyncVerdict.Settled;
            if (_session == null || _session.Status != SessionStatus.Connected)
                return Diagnostics.ResyncVerdict.AlreadyRecovering;

            Diagnostics.ResyncVerdict verdict =
                Diagnostics.ResyncArbiter.Submit(report, NowMs, WorldRecoveryInFlight);
            // First settled report wins; a second one this frame is a consequence of the same
            // divergence and the one reload answers both.
            if (verdict == Diagnostics.ResyncVerdict.Settled && _settledReport == null)
                _settledReport = report;
            return verdict;
        }

        public void RequestAutomaticWorldRecovery(string reason)
        {
            RequestAutomaticWorldRecovery(Diagnostics.ResyncReport.FromReason(reason));
        }

        /// <summary>
        /// Weigh a queued report and, if it settles, reload the world. Requests that arrive here
        /// have already let go of their work, so a held verdict simply means the world is left
        /// alone and the arbiter keeps watching for the fault to recur.
        /// </summary>
        public void RequestAutomaticWorldRecovery(Diagnostics.ResyncReport report)
        {
            if (report == null || _session == null || _session.Status != SessionStatus.Connected) return;
            if (Diagnostics.ResyncArbiter.Submit(report, NowMs, WorldRecoveryInFlight) !=
                Diagnostics.ResyncVerdict.Settled) return;
            RunAutomaticWorldRecovery(report);
        }

        /// <summary>
        /// Reload the world for reports whose hold elapsed with nothing withdrawing them. Held is
        /// never "dismissed": a subsystem that can retry withdraws its report when it succeeds, and
        /// one that dropped its work simply lets the hold run out, which is what lands here.
        /// </summary>
        private void PumpMaturedResyncReports()
        {
            if (_session == null || _session.Status != SessionStatus.Connected) return;
            // A reload already running supersedes anything held: leave the evidence alone rather
            // than announcing a verdict on it that nothing is going to act on.
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
            // Guard the sentinel before subtracting it. `now - long.MinValue` wraps negative in
            // unchecked arithmetic, which otherwise makes the first automatic recovery look as if
            // it were inside the cooldown forever.
            bool coolingDown = _lastAutoRecoveryMs != long.MinValue &&
                               now - _lastAutoRecoveryMs < AutoRecoveryCooldownMs;
            if (coolingDown)
            {
                Diagnostics.SyncLog.ProdWarn(
                    "World sync: skipped a second automatic reload within " +
                    (AutoRecoveryCooldownMs / 1000) + " s (" + report.Reason +
                    "). The edit behind it is left un-synced; use /sync if the city looks out of step.");
                Diagnostics.FlightRecorder.Note("auto recovery suppressed (cooldown): " + report.Summary());
                return;
            }
            _lastAutoRecoveryMs = now;
            Diagnostics.SyncLog.Prod("World sync: reloading this city from the host now (" +
                                     report.Reason + ").");
            Diagnostics.FlightRecorder.Note("resync requested: " + report.Summary());
            _session.RequestWorldSync(report.Reason);
        }

        // ---- Chat log (in-game hub panel) --------------------------------------

        /// <summary>Bounded - old lines fall off so an all-night session cannot grow the UI payload.</summary>
        private const int MaxChatEntries = 120;

        private readonly object _chatLock = new object();
        private readonly List<ChatLogEntry> _chatLog = new List<ChatLogEntry>();
        private int _nextChatId = 1;
        private string _chatLogJson = "[]";
        private string _playerListJson = "[]";

        /// <summary>
        /// The chat/event feed as a JSON array for the hub panel binding:
        /// <c>[{"id":1,"sender":"Name"|null,"text":"...","time":"HH:mm"}, ...]</c>.
        /// Cached and rebuilt only on append, so the per-frame UI binding compares
        /// the same string instance instead of re-serializing the whole log.
        /// </summary>
        public string ChatLogJson { get { lock (_chatLock) return _chatLogJson; } }

        /// <summary>
        /// Host-side participant list used by the in-game panel. It is rebuilt only
        /// when session membership changes, avoiding a fresh JSON allocation every UI
        /// frame. The local host is included and is never kickable.
        /// </summary>
        public string PlayerListJson { get { lock (_chatLock) return _playerListJson; } }

        /// <summary>Remove one authenticated client selected in the host player list.</summary>
        public void KickPlayerFromUi(int playerId)
        {
            if (!_session.KickPlayer(playerId))
                _log.Warn("[MP] Ignored kick request for unavailable player #" + playerId + ".");
        }

        /// <summary>Remove a client and block its address for the current hosting session.</summary>
        public void BanPlayerFromUi(int playerId)
        {
            if (!_session.BanPlayer(playerId))
                _log.Warn("[MP] Ignored ban request for unavailable player #" + playerId + ".");
        }

        /// <summary>
        /// The participant list the in-game panel renders. The host builds it from its peer
        /// table; a client has no peer table beyond the host connection, so it builds one from
        /// the players it is tracking positions for. Either way the local player is included and
        /// is never kickable.
        ///
        /// A client used to be handed an empty array and rendered nothing at all, which made a
        /// two-player session look like a single-player one from one side of it.
        /// </summary>
        private void RefreshPlayerListJson()
        {
            lock (_chatLock)
            {
                switch (_session.Role)
                {
                    case SessionRole.Host: _playerListJson = BuildHostPlayerList(); break;
                    case SessionRole.Client: _playerListJson = BuildClientPlayerList(); break;
                    default: _playerListJson = "[]"; break;
                }
            }
        }

        private string BuildHostPlayerList()
        {
            var peers = new List<Peer>();
            foreach (Peer peer in _session.Peers)
                if (peer.Handshaked) peers.Add(peer);
            peers.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));

            var sb = new System.Text.StringBuilder((peers.Count + 1) * 72 + 2);
            sb.Append('[');
            AppendPlayerEntry(sb, _session.LocalPlayerId, _session.LocalPlayerName,
                isHost: true, isYou: true, latencyMs: 0);
            for (int i = 0; i < peers.Count; i++)
            {
                sb.Append(',');
                AppendPlayerEntry(sb, peers[i].PlayerId, peers[i].Name,
                    isHost: false, isYou: false, latencyMs: peers[i].LatencyMs);
            }
            sb.Append(']');
            return sb.ToString();
        }

        private string BuildClientPlayerList()
        {
            // The host is the one peer a client holds, and it carries the measured latency.
            Peer hostPeer = null;
            foreach (Peer peer in _session.Peers)
                if (peer.Handshaked || peer.PlayerId == MultiplayerSession.HostPlayerId) { hostPeer = peer; break; }

            var others = new List<int>();
            foreach (RemotePlayer player in _remotePlayers.Values)
                if (player.PlayerId != _session.LocalPlayerId &&
                    player.PlayerId != MultiplayerSession.HostPlayerId) others.Add(player.PlayerId);
            others.Sort();

            var sb = new System.Text.StringBuilder((others.Count + 2) * 72 + 2);
            sb.Append('[');
            AppendPlayerEntry(sb, MultiplayerSession.HostPlayerId,
                hostPeer != null && !string.IsNullOrEmpty(hostPeer.Name) ? hostPeer.Name : "Host",
                isHost: true, isYou: _session.LocalPlayerId == MultiplayerSession.HostPlayerId,
                latencyMs: hostPeer != null ? hostPeer.LatencyMs : -1);

            sb.Append(',');
            AppendPlayerEntry(sb, _session.LocalPlayerId, _session.LocalPlayerName,
                isHost: false, isYou: true, latencyMs: 0);

            for (int i = 0; i < others.Count; i++)
            {
                sb.Append(',');
                // No roster travels to clients, so another client's name is not known here.
                // PlayerDisplayName says "Player 3" rather than inventing one.
                AppendPlayerEntry(sb, others[i], PlayerDisplayName(others[i]),
                    isHost: false, isYou: false, latencyMs: -1);
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>
        /// One roster entry. A latency of -1 means "not measured from here" and the panel
        /// shows nothing rather than a misleading zero.
        /// </summary>
        private static void AppendPlayerEntry(System.Text.StringBuilder sb, int id, string name,
            bool isHost, bool isYou, int latencyMs)
        {
            sb.Append("{\"id\":").Append(id).Append(",\"name\":");
            AppendJsonString(sb, name);
            sb.Append(",\"isHost\":").Append(isHost ? "true" : "false");
            sb.Append(",\"isYou\":").Append(isYou ? "true" : "false");
            sb.Append(",\"latency\":").Append(latencyMs);
            sb.Append('}');
        }





        private struct ChatLogEntry
        {
            public int Id;
            public string Sender;
            public string Text;
            public string Time;
        }

        // ---- Map (savegame) sync ---------------------------------------------

        /// <summary>Default and lower bound for the periodic world re-stream, in minutes.</summary>
        private const int DefaultResyncMinutes = 15;
        private const int MinResyncMinutes = 5;

        private bool _warnedResyncMinutes;

        /// <summary>
        /// How often the host re-streams its world as a drift-correcting safety net.
        ///
        /// A world re-sync saves, streams and (on every client) reloads the whole city, so an
        /// interval far below the default is punishing. <c>int.TryParse</c> zeroes its out
        /// parameter on failure, so an unparseable box ("", "15m", "off") or a "0" meant to
        /// disable the feature must not fall through to a clamp of 1 - that produced a full
        /// save+stream+reload every single minute.
        /// </summary>
        public long ResyncIntervalMs
        {
            get
            {
                string raw = Mod.Setting != null ? (Mod.Setting.ResyncMinutes ?? "").Trim() : "";

                int minutes;
                if (!int.TryParse(raw, out minutes) || minutes <= 0) minutes = DefaultResyncMinutes;
                else if (minutes < MinResyncMinutes) minutes = MinResyncMinutes;

                if (!_warnedResyncMinutes && minutes.ToString() != raw)
                {
                    _warnedResyncMinutes = true;
                    _log.Warn("[MP] World re-sync interval '" + raw + "' is not a whole number of minutes >= " +
                              MinResyncMinutes + "; using " + minutes + " minutes instead.");
                }
                return (long)minutes * 60000L;
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
                _log.Info("[MP] " + status + ": " + detail);
                // Players commonly attach the flight log to a public support post. Keep
                // the target IP/hostname in the private main log, but retain the port and
                // transport mode needed to diagnose a connection-stage failure here.
                string flightDetail = status == SessionStatus.Connecting
                    ? "target=redacted port=" + _service._session.Port +
                      " encryption=" + _service._session.EncryptionActive
                    : detail;
                Diagnostics.FlightRecorder.Note("status " + status +
                    " role=" + _service._session.Role +
                    (string.IsNullOrEmpty(flightDetail) ? "" : " detail=" + flightDetail));
                if (status == SessionStatus.Connected &&
                    _service._session.Role == SessionRole.Client &&
                    _service._phase == ClientWorldPhase.Connecting)
                {
                    // Authenticated; the host streams its world to every fresh join.
                    _service.SetPhase(ClientWorldPhase.WaitingForMap);
                }
                else if (status == SessionStatus.Offline || status == SessionStatus.Faulted)
                {
                    // Core teardown deliberately knows nothing about game worlds. If this
                    // client had already installed the host's temporary city, hand the game
                    // layer a deferred exit request before clearing the client phase.
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
                }

                // Lifecycle lines in the hub feed. Like the core's "X joined." notices
                // these stay English: they are shared diagnostics, not translated UI.
                // Stop() fires Offline unconditionally (also after faults and no-op
                // disconnects), so "closed" is only posted when a session actually ran.
                if (status == SessionStatus.Connected && _service._session.Role == SessionRole.Host)
                {
                    _service.AppendChatEntry(null, "Session started - players can join now.");
                    if (!_service._session.UsesRelay)
                    {
                        if (_service._session.PublicExposure)
                            _service.AppendChatEntry(null, "Friends from another network can only join if you forward TCP port " +
                                _service._session.Port + " to this PC on your router and allow it through your firewall.");
                        else
                            _service.AppendChatEntry(null, "LAN-only is enabled - only players on your local network can join. " +
                                "If they cannot connect, allow TCP port " + _service._session.Port + " through your firewall.");
                    }
                }
                else if (status == SessionStatus.Connected && _service._session.Role == SessionRole.Client)
                {
                    // Joining replaces the client's world with the host's copy. Without this notice
                    // the swap reads as "my buildings disappeared" when both play the same city:
                    // anything built outside the session is not in the host's world.
                    _service.AppendChatEntry(null, "Connected - downloading the host's city. It will replace the world " +
                        "you have open in a moment, so anything you built outside this shared session (for example just " +
                        "before joining) is not part of it. Your own saves are untouched.");
                }
                else if (status == SessionStatus.Offline && _lastStatus == SessionStatus.Connected)
                {
                    // A live session ended cleanly (we left, or the host closed it — both are normal).
                    // Clear any stale fault text from an earlier failed attempt so the status reads as a
                    // plain disconnect, not "Connection failed".
                    _service._lastFault = null;
                    _service.AppendChatEntry(null, "Session closed.");
                }
                else if (status == SessionStatus.Faulted)
                    _service.AppendChatEntry(null, string.IsNullOrEmpty(detail) ? "Connection failed." : detail);
                _service.RefreshPlayerListJson();
                _lastStatus = status;
            }

            private SessionStatus _lastStatus = SessionStatus.Offline;

            public override void OnPeerJoined(Peer peer)
            {
                _log.Info("[MP] Peer joined: " + peer);
                Diagnostics.FlightRecorder.Note("peer joined #" + peer.PlayerId);
                _service.RefreshPlayerListJson();
                // WorldResyncSystem observes joins too and pushes the live world to the newcomer.
            }
            public override void OnPeerLeft(Peer peer, string reason)
            {
                _log.Info("[MP] Peer left: " + peer + " (" + reason + ")");
                Diagnostics.FlightRecorder.Note("peer left #" + peer.PlayerId + " (" + reason + ")");
                RemotePlayer removed;
                _service._remotePlayers.TryRemove(peer.PlayerId, out removed);
                _service.RefreshPlayerListJson();
            }
            public override void OnChatReceived(string sender, string text)
            {
                _log.Info("[MP] " + (sender ?? "system") + ": " + text);
                _service.AppendChatEntry(sender, text);
            }
            public override void OnCommandReceived(SimulationCommandMessage command)
            {
                _service.RecordAppliedCommand(command);
            }
            public override void OnPlayerStateReceived(PlayerStateMessage state) => _service.RecordRemotePlayer(state);
            public override void OnBlobReceived(string channel, long transferId, byte[] data)
            {
                if (channel == MapChannel) _service.LoadReceivedMap(transferId, data);
            }
            public override void OnWorldSyncControl(WorldSyncStage stage, long epoch,
                float resumeSpeed, Core.Networking.ConnectionId connection)
            {
                _service.HandleWorldSyncControl(stage, epoch, resumeSpeed);
            }
            public override void OnError(string message)
            {
                _service._lastFault = message;
                _log.Error("[MP] " + message);
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
        public long LastUpdateMs;
    }
}
