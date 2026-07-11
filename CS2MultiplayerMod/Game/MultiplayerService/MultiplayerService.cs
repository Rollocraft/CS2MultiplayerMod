using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;

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
            _session.AllowCommands(
                ObjectPlacementCommand.Id, NetPlacementCommand.Id,
                ObjectDeleteCommand.Id, NetDeleteCommand.Id,
                ZonePaintCommand.Id, TerrainBrushCommand.Id,
                UpgradePlacementCommand.Id, ObjectMoveCommand.Id, NetUpgradeCommand.Id,
                AreaCreateCommand.Id, AreaUpdateCommand.Id, AreaDeleteCommand.Id,
                RouteCreateCommand.Id, RouteUpdateCommand.Id, RouteDeleteCommand.Id,
                TilePurchaseCommand.Id, EntityPolicyCommand.Id, DevTreePurchaseCommand.Id,
                NetReplaceCommand.Id);
        }

        public MultiplayerSession Session => _session;

        /// <summary>Monotonic millisecond clock shared with systems that need timing.</summary>
        public long NowMs => _clock.ElapsedMilliseconds;

        /// <summary>Latest known positions of the other players, for rendering their cursors.</summary>
        public IEnumerable<RemotePlayer> RemotePlayers => _remotePlayers.Values;

        /// <summary>The joining client's place in the world-handover flow.</summary>
        public ClientWorldPhase WorldPhase => _phase;

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
            switch (id)
            {
                case ObjectPlacementCommand.Id: return "object-place";
                case NetPlacementCommand.Id: return "net-place";
                case ObjectDeleteCommand.Id: return "object-delete";
                case NetDeleteCommand.Id: return "net-delete";
                case ZonePaintCommand.Id: return "zone-paint";
                case TerrainBrushCommand.Id: return "terrain-brush";
                case UpgradePlacementCommand.Id: return "building-upgrade";
                case ObjectMoveCommand.Id: return "object-move";
                case NetUpgradeCommand.Id: return "net-upgrade";
                case AreaCreateCommand.Id: return "area-create";
                case AreaDeleteCommand.Id: return "area-delete";
                case RouteCreateCommand.Id: return "route-create";
                case RouteDeleteCommand.Id: return "route-delete";
                case TilePurchaseCommand.Id: return "tile-purchase";
                case EntityPolicyCommand.Id: return "policy-edit";
                case AreaUpdateCommand.Id: return "area-update";
                case RouteUpdateCommand.Id: return "route-update";
                case DevTreePurchaseCommand.Id: return "dev-tree-purchase";
                case NetReplaceCommand.Id: return "net-replace";
                default: return "unknown";
            }
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

        // ---- Chat log (in-game hub panel) --------------------------------------

        /// <summary>Bounded - old lines fall off so an all-night session cannot grow the UI payload.</summary>
        private const int MaxChatEntries = 120;

        private readonly object _chatLock = new object();
        private readonly List<ChatLogEntry> _chatLog = new List<ChatLogEntry>();
        private int _nextChatId = 1;
        private string _chatLogJson = "[]";

        /// <summary>
        /// The chat/event feed as a JSON array for the hub panel binding:
        /// <c>[{"id":1,"sender":"Name"|null,"text":"...","time":"HH:mm"}, ...]</c>.
        /// Cached and rebuilt only on append, so the per-frame UI binding compares
        /// the same string instance instead of re-serializing the whole log.
        /// </summary>
        public string ChatLogJson { get { lock (_chatLock) return _chatLogJson; } }





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
                    if (status == SessionStatus.Faulted) _service._lastFault = detail;
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
                    if (_service._session.PublicExposure)
                        _service.AppendChatEntry(null, "Friends from another network can only join if you forward TCP port " +
                            _service._session.Port + " to this PC on your router and allow it through your firewall.");
                    else
                        _service.AppendChatEntry(null, "LAN-only is enabled - only players on your local network can join. " +
                            "If they cannot connect, allow TCP port " + _service._session.Port + " through your firewall.");
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
                _lastStatus = status;
            }

            private SessionStatus _lastStatus = SessionStatus.Offline;

            public override void OnPeerJoined(Peer peer)
            {
                _log.Info("[MP] Peer joined: " + peer);
                Diagnostics.FlightRecorder.Note("peer joined #" + peer.PlayerId);
                // WorldResyncSystem observes joins too and pushes the live world to the newcomer.
            }
            public override void OnPeerLeft(Peer peer, string reason)
            {
                _log.Info("[MP] Peer left: " + peer + " (" + reason + ")");
                Diagnostics.FlightRecorder.Note("peer left #" + peer.PlayerId + " (" + reason + ")");
                RemotePlayer removed;
                _service._remotePlayers.TryRemove(peer.PlayerId, out removed);
            }
            public override void OnChatReceived(string sender, string text)
            {
                _log.Info("[MP] " + (sender ?? "system") + ": " + text);
                _service.AppendChatEntry(sender, text);
            }
            public override void OnCommandReceived(SimulationCommandMessage command) =>
                _service.RecordAppliedCommand(command);
            public override void OnPlayerStateReceived(PlayerStateMessage state) => _service.RecordRemotePlayer(state);
            public override void OnBlobReceived(string channel, byte[] data)
            {
                if (channel == MapChannel) _service.LoadReceivedMap(data);
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
