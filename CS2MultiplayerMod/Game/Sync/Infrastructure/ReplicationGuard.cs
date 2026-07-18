using System.Collections.Generic;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Breaks the placement echo loop. When a machine realizes a placement it received,
    /// it <see cref="Mark"/>s a spatial key; when its own detector later sees that
    /// freshly-created object, <see cref="Consume"/> recognises it as a replica and
    /// suppresses re-broadcasting it. Without this, every received placement would be
    /// re-detected and re-sent forever.
    ///
    /// Keys quantise position into coarse buckets so a realized object that snapped a
    /// little still matches the request.
    /// </summary>
    public sealed class ReplicationGuard
    {
        private const long TtlMs = 15000;
        private struct Marker
        {
            public long ExpiresAt;
            public int Count;
        }

        private readonly Dictionary<string, Marker> _markers = new Dictionary<string, Marker>();

        public void Mark(string key, long nowMs)
        {
            Marker marker;
            if (!_markers.TryGetValue(key, out marker) || marker.ExpiresAt < nowMs)
                marker.Count = 0;
            marker.Count++;
            marker.ExpiresAt = nowMs + TtlMs;
            _markers[key] = marker;
        }

        /// <summary>Returns true (and forgets the key) if it was a still-valid replica marker.</summary>
        public bool Consume(string key, long nowMs)
        {
            Marker marker;
            if (!_markers.TryGetValue(key, out marker)) return false;
            if (marker.ExpiresAt < nowMs)
            {
                _markers.Remove(key);
                return false;
            }

            if (--marker.Count <= 0) _markers.Remove(key);
            else _markers[key] = marker;
            return true;
        }

        public void Prune(long nowMs)
        {
            if (_markers.Count == 0) return;
            List<string> dead = null;
            foreach (var pair in _markers)
                if (pair.Value.ExpiresAt < nowMs) (dead ?? (dead = new List<string>())).Add(pair.Key);
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) _markers.Remove(dead[i]);
        }

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
