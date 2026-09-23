using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Core.Session
{
    /// <summary>
    /// Password authentication with standard constructions: random nonces, proof =
    /// HMAC-SHA256(UTF-8 password, nonce | channel binding), fixed-time comparison, temporary bans.
    /// The binding is the TLS certificate hash each side saw, so a man-in-the-middle's proof fails.
    /// </summary>
    public static class HandshakeAuth
    {
        public static byte[] NewNonce()
        {
            var nonce = new byte[ProtocolConstants.ChallengeNonceBytes];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(nonce);
            return nonce;
        }

        public static byte[] ComputeProof(string password, byte[] nonce, byte[] channelBinding)
        {
            byte[] key = Encoding.UTF8.GetBytes(password ?? string.Empty);
            int bindingLength = channelBinding != null ? channelBinding.Length : 0;
            var message = new byte[(nonce != null ? nonce.Length : 0) + bindingLength];
            if (nonce != null) Buffer.BlockCopy(nonce, 0, message, 0, nonce.Length);
            if (bindingLength > 0)
                Buffer.BlockCopy(channelBinding, 0, message, message.Length - bindingLength, bindingLength);

            using (var hmac = new HMACSHA256(key))
                return hmac.ComputeHash(message);
        }

        /// <summary>Constant-time equality so a comparison timing leak cannot guide guessing.</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /// <summary>
    /// Failed attempts per address; <see cref="MaxFailures"/> within the window bans it for
    /// <see cref="BanMs"/>.
    /// </summary>
    public sealed class FailedAuthTracker
    {
        public const int MaxFailures = 5;
        public const long BanMs = 10 * 60 * 1000;
        private const long WindowMs = 10 * 60 * 1000;

        private sealed class Record
        {
            public int Failures;
            public long FirstFailureMs;
            public long BannedUntilMs;
        }

        private readonly Dictionary<string, Record> _records = new Dictionary<string, Record>();

        /// <summary>How many addresses still hold a failure record or an active ban.</summary>
        public int TrackedAddresses => _records.Count;

        public bool IsBanned(string address, long nowMs)
        {
            if (string.IsNullOrEmpty(address)) return false;
            if (!_records.TryGetValue(address, out Record record)) return false;
            if (record.BannedUntilMs > nowMs) return true;
            if (nowMs - record.FirstFailureMs > WindowMs) _records.Remove(address);
            return false;
        }

        /// <summary>Record one failure; returns true when this failure triggered a ban.</summary>
        public bool RecordFailure(string address, long nowMs)
        {
            if (string.IsNullOrEmpty(address)) return false;

            if (!_records.TryGetValue(address, out Record record) || nowMs - record.FirstFailureMs > WindowMs)
            {
                record = new Record { FirstFailureMs = nowMs };
                _records[address] = record;
            }

            record.Failures++;
            if (record.Failures < MaxFailures) return false;
            record.BannedUntilMs = nowMs + BanMs;
            return true;
        }

        public void RecordSuccess(string address)
        {
            if (!string.IsNullOrEmpty(address)) _records.Remove(address);
        }

        /// <summary>
        /// Drops records with no active ban and an expired window, so a host sprayed from many addresses
        /// does not keep one record each for the session.
        /// </summary>
        public void Prune(long nowMs)
        {
            if (_records.Count == 0) return;
            List<string> dead = null;
            foreach (var pair in _records)
            {
                Record record = pair.Value;
                if (record.BannedUntilMs > nowMs) continue;              // ban still active
                if (nowMs - record.FirstFailureMs <= WindowMs) continue; // window still open
                (dead ?? (dead = new List<string>())).Add(pair.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _records.Remove(dead[i]);
        }
    }
}
