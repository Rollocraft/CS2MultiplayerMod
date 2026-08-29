using System;
using System.Text;

namespace CS2MultiplayerMod.Core.Protocol
{
    /// <summary>
    /// Validation helpers for wire values. Everything a remote peer controls - counts,
    /// lengths, floats, names, quaternions - must pass through here. All failures throw
    /// <see cref="ProtocolException"/>, which every receive path treats as drop message
    /// / disconnect sender, never crash.
    /// </summary>
    public static class WireGuard
    {
        /// <summary>Largest coordinate magnitude that can be meant seriously (CS2 maps are ~14 km).</summary>
        public const float MaxCoordinate = 1000000f;

        /// <summary>Cap for prefab/player names on the wire.</summary>
        public const int MaxNameLength = 128;

        /// <summary>Cap for one chat line.</summary>
        public const int MaxChatLength = 500;

        /// <summary>Cap for node/waypoint style repeat counts in commands.</summary>
        public const int MaxItemCount = 4096;

        /// <summary>
        /// Read repeat count as 16-bit value, prove it's plausible: non-negative,
        /// under <paramref name="maxItems"/>, and bytesPerItem x count fits remaining
        /// bytes - so forged count can never cause huge allocation.
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

        /// <summary>Read 3D world coordinates (X, Y, Z) with finite and boundary checks.</summary>
        public static void ReadCoordinate3(NetworkReader reader, out float x, out float y, out float z)
        {
            x = ReadCoordinate(reader);
            y = ReadCoordinate(reader);
            z = ReadCoordinate(reader);
        }

        /// <summary>Read and validate a normalized 3D rotation quaternion.</summary>
        public static void ReadQuaternion(NetworkReader reader, out float x, out float y, out float z, out float w)
        {
            x = ReadFinite(reader);
            y = ReadFinite(reader);
            z = ReadFinite(reader);
            w = ReadFinite(reader);

            float sqrMag = x * x + y * y + z * z + w * w;
            if (sqrMag < 0.0001f || Math.Abs(sqrMag - 1.0f) > 0.1f)
            {
                // Re-normalize or reject non-normalized quaternion
                if (sqrMag >= 0.0001f)
                {
                    float inv = 1.0f / (float)Math.Sqrt(sqrMag);
                    x *= inv;
                    y *= inv;
                    z *= inv;
                    w *= inv;
                }
                else
                {
                    throw new ProtocolException("Degenerate zero quaternion on the wire.");
                }
            }
        }

        /// <summary>Read an integer strictly within [min, max].</summary>
        public static int ReadRangedInt(NetworkReader reader, int min, int max)
        {
            int val = reader.ReadInt();
            if (val < min || val > max)
                throw new ProtocolException("Integer " + val + " outside range [" + min + ", " + max + "].");
            return val;
        }

        /// <summary>Read a float strictly within [min, max].</summary>
        public static float ReadRangedFloat(NetworkReader reader, float min, float max)
        {
            float val = ReadFinite(reader);
            if (val < min || val > max)
                throw new ProtocolException("Float " + val + " outside range [" + min + ", " + max + "].");
            return val;
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
        /// Sanitize free text for display/logging: strip control characters (kills log
        /// injection via embedded newlines/ANSI), collapse to the length cap, and never
        /// return null. Employs a zero-allocation fast path for clean strings.
        /// </summary>
        public static string SanitizeText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            // Fast-path scan: if no control chars and within length, return without allocating a StringBuilder
            bool needsCleaning = value.Length > maxLength;
            if (!needsCleaning)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    if (char.IsControl(value[i]))
                    {
                        needsCleaning = true;
                        break;
                    }
                }
                if (!needsCleaning) return value.Trim();
            }

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
