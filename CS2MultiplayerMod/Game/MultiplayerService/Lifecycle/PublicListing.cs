using System;
using System.Collections.Generic;
using System.Text;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        /// <summary>How often a listed host refreshes its entry; only changed fields are sent.</summary>
        private const long ListingRefreshMs = 5000;

        /// <summary>
        /// Shortest gap between two server searches: a little under the browser's 5 s refresh limit, so a
        /// refresh it allows is never refused here.
        /// </summary>
        private const long SearchCooldownMs = 4500;

        /// <summary>After a search, unknown pings are retried this long, once a second, while Steam measures.</summary>
        private const long PingRetryWindowMs = 10000;

        private IServerDirectory _directory;
        private bool _directoryOpened;
        private MultiplayerConfig _hostConfig;
        private bool _listed;
        private long _listingRefreshedMs;
        private long _lastSearchMs = -SearchCooldownMs;
        private bool _searching;
        private bool _listedJoinPending;
        private string _serverListJson = "[]";
        private string _serverListError = "";
        private IReadOnlyList<ServerListing> _servers = Array.Empty<ServerListing>();
        private long _pingRetryUntilMs;
        private long _pingRetriedMs;

        /// <summary>Listings from the last search as JSON, compatible hosts first; "[]" before any.</summary>
        public string ServerListJson => _serverListJson;

        /// <summary>"searching", "joining" (asking Steam for the host) or "idle".</summary>
        public string ServerListState => _listedJoinPending ? "joining" : _searching ? "searching" : "idle";

        /// <summary>Why the last search or listed join failed; empty otherwise.</summary>
        public string ServerListError => _serverListError;

        private IServerDirectory ServerDirectory
        {
            get
            {
                if (_directoryOpened || RelayProvider.Current == null) return _directory;
                _directoryOpened = true;
                try { _directory = RelayProvider.Current.OpenDirectory(_log); }
                catch (Exception ex) { _log.Warn(LogTopic.Transport, "No Public Lobbies: " + ex.Message); }
                return _directory;
            }
        }

        /// <summary>Keeps this host's public entry in step with the session.</summary>
        private void PumpPublicListing()
        {
            bool wanted = _session.Role == SessionRole.Host && _session.Status == SessionStatus.Connected &&
                          _session.ListedJoinsOpen && _hostConfig != null;
            if (!wanted)
            {
                StopListing();
                return;
            }
            if (_listed && NowMs - _listingRefreshedMs < ListingRefreshMs) return;

            IServerDirectory directory = ServerDirectory;
            if (directory == null) return;
            _listed = true;
            _listingRefreshedMs = NowMs;
            directory.Publish(BuildListing(_hostConfig));
        }

        private void StopListing()
        {
            if (!_listed) return;
            _listed = false;
            _directory?.Unpublish();
        }

        private ServerListing BuildListing(MultiplayerConfig config)
        {
            string name = Mod.Setting != null ? (Mod.Setting.ServerName ?? "").Trim() : "";
            return new ServerListing
            {
                Name = name.Length > 0 ? name : _session.LocalPlayerName,
                Players = 1 + HandshakedPeerCount(),
                MaxPlayers = config.MaxPlayers,
                PasswordRequired = config.Password.Length > 0,
                AsksJoinMessage = config.AskJoinMessage,
                ModVersion = config.ModVersion,
                GameVersion = config.GameVersion,
            };
        }

        public void SearchServers()
        {
            IServerDirectory directory = ServerDirectory;
            if (directory == null || !RelayProvider.IsAvailable)
            {
                _serverListError = RelayProvider.UnavailableReason ?? "";
                _serverListJson = ServerListToJson(Array.Empty<ServerListing>());
                return;
            }
            if (_searching || NowMs - _lastSearchMs < SearchCooldownMs) return;

            _searching = true;
            _lastSearchMs = NowMs;
            directory.Search(OnServersFound);
        }

        private void OnServersFound(IReadOnlyList<ServerListing> servers, string error)
        {
            _searching = false;
            _serverListError = error ?? "";
            if (servers == null)
            {
                _log.Warn(LogTopic.Transport, "Server search failed: " + error);
                return;
            }
            _servers = servers;
            _pingRetryUntilMs = NowMs + PingRetryWindowMs;
            _serverListJson = ServerListToJson(servers);
        }

        private void PumpServerPings()
        {
            if (NowMs >= _pingRetryUntilMs || NowMs - _pingRetriedMs < 1000 || _directory == null) return;
            _pingRetriedMs = NowMs;
            if (_directory.EstimatePings(_servers)) _serverListJson = ServerListToJson(_servers);
        }

        /// <summary>
        /// Joins a host from the public list over the relay only, so neither side learns the other's address.
        /// The host is the listing's owner as Steam reports it, never what the listing itself claims.
        /// </summary>
        public void JoinListedServer(string listingId, string password, string message)
        {
            if (_listedJoinPending || _session.Role != SessionRole.None || Mod.Setting == null) return;
            IServerDirectory directory = ServerDirectory;
            if (directory == null) return;

            _listedJoinPending = true;
            _serverListError = "";
            directory.ResolveHost(listingId, (joinCode, error) =>
            {
                _listedJoinPending = false;
                if (joinCode == null)
                {
                    _serverListError = error ?? "";
                    _log.Warn(LogTopic.Session, "Could not reach the listed server: " + error);
                    return;
                }
                if (_session.Role == SessionRole.None && Mod.Setting != null)
                    StartJoin(Mod.Setting, new ListedJoin(joinCode, password, message));
            });
        }

        private static string ServerListToJson(IReadOnlyList<ServerListing> servers)
        {
            string modVersion = Mod.CompatibilityVersion;
            string gameVersion = GameVersionCheck.CurrentVersion;
            var sb = new StringBuilder(servers.Count * 192 + 2).Append('[');
            bool first = true;
            // Compatible hosts first, each group in Steam's order (nearest first).
            for (int pass = 0; pass < 2; pass++)
                foreach (ServerListing server in servers)
                {
                    bool compatible = string.Equals(server.ModVersion, modVersion, StringComparison.Ordinal) &&
                                      string.Equals(server.GameVersion, gameVersion, StringComparison.Ordinal);
                    if (compatible != (pass == 0)) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":");
                    AppendJsonString(sb, server.Id);
                    sb.Append(",\"name\":");
                    AppendJsonString(sb, server.Name);
                    sb.Append(",\"players\":").Append(server.Players)
                      .Append(",\"max\":").Append(server.MaxPlayers)
                      .Append(",\"password\":").Append(server.PasswordRequired ? "true" : "false")
                      .Append(",\"message\":").Append(server.AsksJoinMessage ? "true" : "false")
                      .Append(",\"compatible\":").Append(compatible ? "true" : "false")
                      .Append(",\"version\":");
                    AppendJsonString(sb, server.ModVersion);
                    // Left out when unknown, so no culture's minus sign reaches the JSON.
                    if (server.PingMs >= 0) sb.Append(",\"ping\":").Append(server.PingMs);
                    sb.Append('}');
                }
            return sb.Append(']').ToString();
        }

        /// <summary>A join picked from the public list instead of the Join form's fields.</summary>
        private sealed class ListedJoin
        {
            public readonly string JoinCode;
            public readonly string Password;
            public readonly string Message;

            public ListedJoin(string joinCode, string password, string message)
            {
                JoinCode = joinCode;
                Password = password ?? "";
                Message = message ?? "";
            }
        }
    }
}
