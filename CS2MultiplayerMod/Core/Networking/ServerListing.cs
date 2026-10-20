using System;
using System.Collections.Generic;
using System.Globalization;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// The public server list. It holds what <see cref="ServerListing"/> publishes and never an address: a
    /// listed host is dialed over the relay as the account that owns the listing. Game thread only.
    /// </summary>
    public interface IServerDirectory
    {
        /// <summary>True once the listing exists on the platform.</summary>
        bool IsPublished { get; }

        /// <summary>Creates the listing on first use and updates it in place after that.</summary>
        void Publish(ServerListing listing);

        void Unpublish();

        /// <summary>Lists other compatible sessions; <paramref name="done"/> gets them, or null and why not.</summary>
        void Search(Action<IReadOnlyList<ServerListing>, string> done);

        /// <summary>
        /// Fills in still-unknown pings of listings from the last search. Purely local; the local measurements
        /// arrive a few seconds after first use. True when any ping was added.
        /// </summary>
        bool EstimatePings(IReadOnlyList<ServerListing> listings);

        /// <summary>
        /// The join code of the account that owns a listing from the last search, as the platform reports it.
        /// A listing's own fields are its creator's word and never name the host. Null and why not on failure.
        /// </summary>
        void ResolveHost(string listingId, Action<string, string> done);
    }

    /// <summary>One entry of the public server list. Everything read back from the platform is untrusted.</summary>
    public sealed class ServerListing
    {
        /// <summary>Present on every listing; its value is the protocol, so a search finds compatible hosts only.</summary>
        public const string MarkerKey = "cs2mp";

        public const int MaxNameLength = 48;
        private const int MaxVersionLength = 32;
        private const int MaxSeats = 64;

        private const string NameKey = "name";
        private const string PlayersKey = "players";
        private const string MaxPlayersKey = "max";
        private const string PasswordKey = "pw";
        private const string JoinMessageKey = "msg";
        private const string ModVersionKey = "mod";
        private const string GameVersionKey = "game";

        public static string MarkerValue => ProtocolConstants.ProtocolVersion.ToString(CultureInfo.InvariantCulture);

        /// <summary>The platform's id for the listing; set on search results only.</summary>
        public string Id = "";
        public string Name = "";
        public int Players;
        public int MaxPlayers;
        public bool PasswordRequired;
        public bool AsksJoinMessage;
        public string ModVersion = "";
        public string GameVersion = "";

        /// <summary>Estimated round trip to the host in ms, -1 if unknown. The searcher's own estimate, never published.</summary>
        public int PingMs = -1;

        /// <summary>The marker comes last, so a search never finds a half-written listing.</summary>
        public IEnumerable<KeyValuePair<string, string>> ToData()
        {
            yield return Pair(NameKey, WireGuard.SanitizeDisplayText(Name, MaxNameLength));
            yield return Pair(PlayersKey, Players.ToString(CultureInfo.InvariantCulture));
            yield return Pair(MaxPlayersKey, MaxPlayers.ToString(CultureInfo.InvariantCulture));
            yield return Pair(PasswordKey, Flag(PasswordRequired));
            yield return Pair(JoinMessageKey, Flag(AsksJoinMessage));
            yield return Pair(ModVersionKey, WireGuard.SanitizeText(ModVersion, MaxVersionLength));
            yield return Pair(GameVersionKey, WireGuard.SanitizeText(GameVersion, MaxVersionLength));
            yield return Pair(MarkerKey, MarkerValue);
        }

        /// <summary>Null unless the marker names this protocol; every field is bounded and plain text.</summary>
        public static ServerListing FromData(string id, Func<string, string> read)
        {
            if (read == null || read(MarkerKey) != MarkerValue) return null;
            return new ServerListing
            {
                Id = id ?? "",
                Name = WireGuard.SanitizeDisplayText(read(NameKey), MaxNameLength),
                Players = Seats(read(PlayersKey)),
                MaxPlayers = Seats(read(MaxPlayersKey)),
                PasswordRequired = read(PasswordKey) == "1",
                AsksJoinMessage = read(JoinMessageKey) == "1",
                ModVersion = WireGuard.SanitizeDisplayText(read(ModVersionKey), MaxVersionLength),
                GameVersion = WireGuard.SanitizeDisplayText(read(GameVersionKey), MaxVersionLength),
            };
        }

        private static KeyValuePair<string, string> Pair(string key, string value) =>
            new KeyValuePair<string, string>(key, value);

        private static string Flag(bool value) => value ? "1" : "0";

        private static int Seats(string text)
        {
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) return 0;
            return value > MaxSeats ? MaxSeats : value;
        }
    }
}
