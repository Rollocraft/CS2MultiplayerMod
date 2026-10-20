using System;
using System.Collections.Generic;
using System.Globalization;
using CS2MultiplayerMod.Core.Diagnostics;
using Steamworks;

namespace CS2MultiplayerMod.Core.Networking.Steam
{
    /// <summary>
    /// The public server list as Steam lobbies. A lobby carries only <see cref="ServerListing"/>'s fields -
    /// never an address or a game-server entry - and a joiner takes the host from the lobby's owner, which
    /// only Steam sets. Results arrive on the game thread through the game's own callback pump.
    /// </summary>
    internal sealed class SteamServerDirectory : IServerDirectory
    {
        /// <summary>Found by search, but not offered to friends as a lobby to join.</summary>
        private const ELobbyType ListingType = ELobbyType.k_ELobbyTypeInvisible;

        /// <summary>The only other members are searchers reading the owner, each for a moment.</summary>
        private const int ListingMemberLimit = 250;

        private const int MaxResults = 50;

        /// <summary>A failed listing is not asked for again sooner than this.</summary>
        private const long CreateRetryMs = 30000;

        /// <summary>The host's relay ping location. Optional: without it a listing just shows no ping.</summary>
        private const string PingLocationKey = "loc";

        private readonly IModLogger _log;
        private readonly CallResult<LobbyCreated_t> _created;
        private readonly CallResult<LobbyMatchList_t> _matched;
        private readonly CallResult<LobbyEnter_t> _entered;
        private readonly Dictionary<string, string> _publishedData = new Dictionary<string, string>();

        /// <summary>Lobbies from the last search: the only ones a join may enter.</summary>
        private readonly HashSet<ulong> _found = new HashSet<ulong>();

        private readonly Dictionary<string, SteamNetworkPingLocation_t> _pingLocations =
            new Dictionary<string, SteamNetworkPingLocation_t>();

        private ulong _lobbyId;
        private ServerListing _wanted;
        private bool _creating;
        private long _retryAtMs;
        private Action<IReadOnlyList<ServerListing>, string> _searchDone;
        private Action<string, string> _resolveDone;

        public SteamServerDirectory(IModLogger log)
        {
            _log = log ?? NullModLogger.Instance;
            _created = CallResult<LobbyCreated_t>.Create(OnCreated);
            _matched = CallResult<LobbyMatchList_t>.Create(OnMatched);
            _entered = CallResult<LobbyEnter_t>.Create(OnEntered);
            // Pings need this side's relay measurements, which take a few seconds.
            try { SteamNetworkingUtils.InitRelayNetworkAccess(); }
            catch (Exception) { /* no pings, everything else works */ }
        }

        public bool IsPublished => _lobbyId != 0;

        public void Publish(ServerListing listing)
        {
            _wanted = listing;
            try
            {
                if (_lobbyId != 0 && SteamMatchmaking.GetLobbyOwner(new CSteamID(_lobbyId)).m_SteamID !=
                    SteamRelayProvider.LocalSteamId())
                {
                    _log.Warn(LogTopic.Transport, "The public listing was lost (Steam went offline?); listing again.");
                    _lobbyId = 0;
                    _publishedData.Clear();
                }

                if (_lobbyId != 0)
                {
                    Apply();
                    return;
                }
                if (_creating || MonotonicClock.NowMs < _retryAtMs) return;

                _creating = true;
                _created.Set(SteamMatchmaking.CreateLobby(ListingType, ListingMemberLimit));
            }
            catch (Exception ex)
            {
                _creating = false;
                _retryAtMs = MonotonicClock.NowMs + CreateRetryMs;
                _log.Warn(LogTopic.Transport, "Could not list the session publicly: " + ex.Message);
            }
        }

        public void Unpublish()
        {
            _wanted = null;
            // A creation still in flight is left as soon as it arrives.
            if (_lobbyId == 0) return;
            try { SteamMatchmaking.LeaveLobby(new CSteamID(_lobbyId)); }
            catch (Exception) { /* Steam is gone, and the lobby with it */ }
            _lobbyId = 0;
            _publishedData.Clear();
            _log.Event(LogTopic.Transport, "Removed the session from Public Lobbies.");
        }

        public void Search(Action<IReadOnlyList<ServerListing>, string> done)
        {
            bool running = _searchDone != null;
            _searchDone = done;
            if (running) return;
            try
            {
                SteamMatchmaking.AddRequestLobbyListStringFilter(ServerListing.MarkerKey, ServerListing.MarkerValue,
                    ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
                SteamMatchmaking.AddRequestLobbyListResultCountFilter(MaxResults);
                _matched.Set(SteamMatchmaking.RequestLobbyList());
            }
            catch (Exception ex)
            {
                _searchDone = null;
                done?.Invoke(null, "Steam could not search (" + ex.Message + ").");
            }
        }

        public void ResolveHost(string listingId, Action<string, string> done)
        {
            if (!ulong.TryParse(listingId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) ||
                !_found.Contains(id))
            {
                done?.Invoke(null, "That server is no longer listed. Refresh the list and try again.");
                return;
            }
            if (_resolveDone != null)
            {
                done?.Invoke(null, "Already contacting a server.");
                return;
            }

            _resolveDone = done;
            try
            {
                _entered.Set(SteamMatchmaking.JoinLobby(new CSteamID(id)));
            }
            catch (Exception ex)
            {
                _resolveDone = null;
                done?.Invoke(null, "Steam could not open the listing (" + ex.Message + ").");
            }
        }

        public bool EstimatePings(IReadOnlyList<ServerListing> listings)
        {
            bool added = false;
            try
            {
                foreach (ServerListing listing in listings)
                {
                    if (listing.PingMs >= 0 ||
                        !_pingLocations.TryGetValue(listing.Id, out SteamNetworkPingLocation_t location)) continue;
                    int ping = SteamNetworkingUtils.EstimatePingTimeFromLocalHost(ref location);
                    if (ping < 0) continue;
                    listing.PingMs = ping;
                    added = true;
                }
            }
            catch (Exception) { /* Steam is gone; the pings stay unknown */ }
            return added;
        }

        private void Apply()
        {
            var lobby = new CSteamID(_lobbyId);
            foreach (KeyValuePair<string, string> pair in _wanted.ToData())
                SetData(lobby, pair.Key, pair.Value);
            if (SteamNetworkingUtils.GetLocalPingLocation(out SteamNetworkPingLocation_t location) >= 0)
            {
                SteamNetworkingUtils.ConvertPingLocationToString(ref location, out string text,
                    Constants.k_cchMaxSteamNetworkingPingLocationString);
                if (!string.IsNullOrEmpty(text)) SetData(lobby, PingLocationKey, text);
            }
        }

        private void SetData(CSteamID lobby, string key, string value)
        {
            if (_publishedData.TryGetValue(key, out string current) && current == value) return;
            if (SteamMatchmaking.SetLobbyData(lobby, key, value)) _publishedData[key] = value;
        }

        private void OnCreated(LobbyCreated_t result, bool ioFailure)
        {
            _creating = false;
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                _retryAtMs = MonotonicClock.NowMs + CreateRetryMs;
                _log.Warn(LogTopic.Transport, "Steam did not create the public listing (" +
                    (ioFailure ? "no answer" : result.m_eResult.ToString()) + "); trying again in " +
                    (CreateRetryMs / 1000) + " s.");
                return;
            }

            try
            {
                if (_wanted == null)
                {
                    SteamMatchmaking.LeaveLobby(new CSteamID(result.m_ulSteamIDLobby));
                    return;
                }
                _lobbyId = result.m_ulSteamIDLobby;
                _publishedData.Clear();
                Apply();
                _log.Event(LogTopic.Transport, "Listed the session publicly on Steam as '" + _wanted.Name + "'.");
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.Transport, "Could not fill in the public listing: " + ex.Message);
            }
        }

        private void OnMatched(LobbyMatchList_t result, bool ioFailure)
        {
            Action<IReadOnlyList<ServerListing>, string> done = _searchDone;
            _searchDone = null;
            if (done == null) return;
            if (ioFailure)
            {
                done(null, "Steam did not answer the search.");
                return;
            }

            var listings = new List<ServerListing>();
            try
            {
                _found.Clear();
                _pingLocations.Clear();
                int count = (int)Math.Min(result.m_nLobbiesMatching, (uint)MaxResults);
                for (int i = 0; i < count; i++)
                {
                    CSteamID lobby = SteamMatchmaking.GetLobbyByIndex(i);
                    if (lobby.m_SteamID == 0 || lobby.m_SteamID == _lobbyId) continue;
                    ServerListing listing = ServerListing.FromData(
                        lobby.m_SteamID.ToString(CultureInfo.InvariantCulture),
                        key => SteamMatchmaking.GetLobbyData(lobby, key));
                    if (listing == null) continue;
                    _found.Add(lobby.m_SteamID);
                    listings.Add(listing);
                    string text = SteamMatchmaking.GetLobbyData(lobby, PingLocationKey);
                    if (!string.IsNullOrEmpty(text) &&
                        text.Length <= Constants.k_cchMaxSteamNetworkingPingLocationString &&
                        SteamNetworkingUtils.ParsePingLocationString(text, out SteamNetworkPingLocation_t location))
                        _pingLocations[listing.Id] = location;
                }
                EstimatePings(listings);
            }
            catch (Exception ex)
            {
                done(null, "Steam could not read the search results (" + ex.Message + ").");
                return;
            }
            done(listings, null);
        }

        private void OnEntered(LobbyEnter_t result, bool ioFailure)
        {
            Action<string, string> done = _resolveDone;
            _resolveDone = null;

            var response = (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
            bool entered = !ioFailure && response == EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess;
            ulong owner = 0;
            if (entered)
            {
                var lobby = new CSteamID(result.m_ulSteamIDLobby);
                try { owner = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID; }
                catch (Exception) { owner = 0; }
                try { SteamMatchmaking.LeaveLobby(lobby); }
                catch (Exception) { /* left with Steam */ }
            }

            if (done == null) return;
            if (!entered)
            {
                done(null, ioFailure ? "Steam did not answer." : "Steam would not open the listing (" + response + ").");
                return;
            }
            if (owner == 0 || !new CSteamID(owner).BIndividualAccount() || owner == SteamRelayProvider.LocalSteamId())
            {
                done(null, "The listing has no host to connect to.");
                return;
            }
            done(owner.ToString(CultureInfo.InvariantCulture), null);
        }
    }
}
