using System.Text;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Validation for everything a peer controls. Failures throw <see cref="ProtocolException"/>, which
    /// every receive path turns into a dropped message or disconnect.
    /// </summary>
    public static class WireGuard
    {
        /// <summary>Largest coordinate magnitude that can be meant seriously (CS2 maps are ~14 km).</summary>
        public const float MaxCoordinate = 1000000f;

        /// <summary>Cap for prefab/player names on the wire.</summary>
        public const int MaxNameLength = 128;

        /// <summary>Cap for one chat line.</summary>
        public const int MaxChatLength = 500;

        /// <summary>Cap for the reason a peer gives for asking to be re-synced (log text only).</summary>
        public const int MaxResyncReasonLength = 120;

        /// <summary>Cap for node/waypoint style repeat counts in commands.</summary>
        public const int MaxItemCount = 4096;

        /// <summary>
        /// A sanity bound, not a range: an extended curve parameter, clamped to 0..1 by every consumer.
        /// </summary>
        public const float MaxSplitPosition = 1000f;

        /// <summary>A bool byte that must be 0 or 1; anything else is "Invalid <paramref name="what"/>."</summary>
        public static bool ReadStrictBool(NetworkReader reader, string what)
        {
            byte value = reader.ReadByte();
            if (value > 1) throw new ProtocolException("Invalid " + what + ".");
            return value != 0;
        }

        /// <summary>
        /// A 16-bit count that is non-negative, under <paramref name="maxItems"/>, and fits the remaining bytes
        /// at <paramref name="bytesPerItem"/>, so a forged count cannot force a huge allocation.
        /// </summary>
        public static int ReadCount(NetworkReader reader, int bytesPerItem, int maxItems = MaxItemCount)
        {
            int count = reader.ReadShort();
            if (count < 0)
                throw new ProtocolException("Negative item count: " + count + ".");
            if (count > maxItems)
                throw new ProtocolException("Item count " + count + " exceeds limit " + maxItems + ".");
            if ((long)count * bytesPerItem > reader.Remaining)
                throw new ProtocolException("Item count " + count + " does not fit the remaining " +
                                            reader.Remaining + " payload byte(s).");
            return count;
        }

        /// <summary>Read a float that must be finite (no NaN/Infinity).</summary>
        public static float ReadFinite(NetworkReader reader)
        {
            float value = reader.ReadFloat();
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ProtocolException("Non-finite float on the wire.");
            return value;
        }

        /// <summary>Read a world coordinate: finite and within plausible map bounds.</summary>
        public static float ReadCoordinate(NetworkReader reader)
        {
            float value = ReadFinite(reader);
            if (value < -MaxCoordinate || value > MaxCoordinate)
                throw new ProtocolException("Coordinate " + value + " outside plausible bounds.");
            return value;
        }

        /// <summary>Read a prefab-style name: required, sane length, no control characters.</summary>
        public static string ReadName(NetworkReader reader)
        {
            string value = reader.ReadString();
            if (string.IsNullOrEmpty(value))
                throw new ProtocolException("Empty name on the wire.");
            if (value.Length > MaxNameLength)
                throw new ProtocolException("Name longer than " + MaxNameLength + " characters.");
            for (int i = 0; i < value.Length; i++)
                if (char.IsControl(value[i]))
                    throw new ProtocolException("Control character in name.");
            return value;
        }

        /// <summary>
        /// Strips control characters (log injection), truncates, never returns null. Sanitizes rather than
        /// rejects names and chat.
        /// </summary>
        public static string SanitizeText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length < maxLength ? value.Length : maxLength);
            for (int i = 0; i < value.Length && sb.Length < maxLength; i++)
            {
                char c = value[i];
                if (char.IsControl(c)) continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>Sanitize a player name; falls back to "Player" when nothing survives.</summary>
        public static string SanitizePlayerName(string value)
        {
            string clean = SanitizeText(value, 24);
            return clean.Length == 0 ? "Player" : clean;
        }
    }
}
