using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// Supplies relay transports. Kept behind an interface so Core never links the
    /// platform SDK directly: the game layer registers the real implementation at
    /// load, and the test harness leaves <see cref="RelayProvider.Current"/> null,
    /// which reads as "relay unavailable".
    /// </summary>
    public interface IRelayProvider
    {
        /// <summary>Null when relay sessions work right now, otherwise why they do not.</summary>
        string UnavailableReason { get; }

        /// <summary>What other players type to reach this machine; empty when unavailable.</summary>
        string LocalJoinCode { get; }

        ITransport CreateHost(IModLogger log);

        ITransport CreateClient(IModLogger log, string joinCode);
    }

    /// <summary>Process-wide relay registration and the join-code format both sides agree on.</summary>
    public static class RelayProvider
    {
        /// <summary>Steam IDs are 17 digits and will stay that way for the life of the 7656119 block.</summary>
        private const int JoinCodeLength = 17;

        public static IRelayProvider Current;

        /// <summary>
        /// Whether relay sessions exist as a choice on this machine at all. False on
        /// copies of the game that ship no Steam library (Microsoft Store / Game Pass):
        /// there is nothing to be signed in to and nothing to wait for, so the option is
        /// not offered rather than shown as unavailable.
        /// </summary>
        public static bool IsSupported
        {
            get { return Current != null; }
        }

        public static bool IsAvailable
        {
            get { return Current != null && Current.UnavailableReason == null; }
        }

        public static string UnavailableReason
        {
            get
            {
                return Current == null
                    ? "This copy of the game has no Steam support."
                    : Current.UnavailableReason;
            }
        }

        public static string LocalJoinCode
        {
            get { return Current != null ? (Current.LocalJoinCode ?? "") : ""; }
        }

        /// <summary>
        /// Whether a typed target is a join code rather than an address. Pure format test
        /// so the join screen can route the player without Steam being up, and so an IPv4
        /// address (dots) or host name never resolves as a code.
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
