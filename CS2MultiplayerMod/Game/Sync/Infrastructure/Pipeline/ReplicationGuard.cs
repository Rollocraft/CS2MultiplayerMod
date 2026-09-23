using System.Collections.Generic;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Breaks the echo loop: realizing a received placement marks a spatial key, and the detector
    /// consumes it instead of re-broadcasting. Keys use coarse buckets to tolerate snapping.
    /// </summary>
    public sealed class ReplicationGuard
    {
        private const long TtlMs = 15000;
        private readonly Dictionary<string, long> _expiry = new Dictionary<string, long>();

        public void Mark(string key, long nowMs) => _expiry[key] = nowMs + TtlMs;

        /// <summary>Returns true (and forgets the key) if it was a still-valid replica marker.</summary>
        public bool Consume(string key, long nowMs)
        {
            if (!_expiry.TryGetValue(key, out long expiresAt)) return false;
            _expiry.Remove(key);
            return expiresAt >= nowMs;
        }

        public void Prune(long nowMs)
        {
            if (_expiry.Count == 0) return;
            List<string> dead = null;
            foreach (var pair in _expiry)
                if (pair.Value < nowMs) (dead ?? (dead = new List<string>())).Add(pair.Key);
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _expiry.Remove(dead[i]);
        }

        /// <summary>Forget every marker when a world/session boundary invalidates spatial keys.</summary>
        public void Clear() => _expiry.Clear();

        /// <summary>Spatial key: prefab name + position rounded to 0.5 m buckets.</summary>
        public static string Key(string prefabName, float3 position)
        {
            long x = (long)math.round(position.x * 2f);
            long y = (long)math.round(position.y * 2f);
            long z = (long)math.round(position.z * 2f);
            return prefabName + "|" + x + "|" + y + "|" + z;
        }
    }
}
