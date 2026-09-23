using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// Supplies relay transports so Core never links the platform SDK. Null
    /// <see cref="RelayProvider.Current"/> (tests) means relay unavailable.
    /// </summary>
    public interface IRelayProvider
    {
        /// <summary>Null when relay sessions work right now, otherwise why they do not.</summary>
        string UnavailableReason { get; }

        /// <summary>What other players type to reach this machine; empty when unavailable.</summary>
        string LocalJoinCode { get; }

        /// <summary>The platform account's display name, or empty; used once as the first-run player name.</summary>
        string LocalPlayerName { get; }

        ITransport CreateHost(IModLogger log);

        ITransport CreateClient(IModLogger log, string joinCode);
    }

    /// <summary>Process-wide relay registration and the join-code format both sides agree on.</summary>
    public static class RelayProvider
    {
        /// <summary>Steam IDs are 17 digits and will stay that way for the life of the 7656119 block.</summary>
        private const int JoinCodeLength = 17;

        public static IRelayProvider Current;

        /// <summary>False on copies without a Steam library (Microsoft Store / Game Pass): the option is hidden.</summary>
        public static bool IsSupported => Current != null;

        public static bool IsAvailable => Current != null && Current.UnavailableReason == null;

        public static string UnavailableReason
        {
            get
            {
                return Current == null
                    ? "This copy of the game has no Steam support."
                    : Current.UnavailableReason;
            }
        }

        public static string LocalJoinCode => Current != null ? (Current.LocalJoinCode ?? "") : "";

        /// <summary>The platform display name, or empty without a platform backend.</summary>
        public static string LocalPlayerName
        {
            get
            {
                if (Current == null) return "";
                try { return Current.LocalPlayerName ?? ""; }
                catch (System.Exception) { return ""; }
            }
        }

        /// <summary>
        /// A pure format test, so the join screen routes without Steam running; an IPv4 address or host
        /// name never parses as a code.
        /// </summary>
        public static bool LooksLikeJoinCode(string text)
        {
            if (text == null) return false;
            string trimmed = text.Trim();
            if (trimmed.Length != JoinCodeLength) return false;
            for (int i = 0; i < trimmed.Length; i++)
                if (trimmed[i] < '0' || trimmed[i] > '9') return false;
            return true;
        }
    }
}
