using System.Collections.Generic;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Localization;
using Game.Rendering;
using Unity.Entities;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        public const long MapPingLifetimeMs = 10000;
        private readonly List<MapPing> _mapPings = new List<MapPing>();
        public IReadOnlyList<MapPing> MapPings => _mapPings;
        public bool CanPingMap => GameplaySyncReady && _session.CanSendMapPing;

        public void PingMapFocus()
        {
            if (!CanPingMap) return;
            var camera = World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<CameraUpdateSystem>();
            if (camera?.gamePlayController == null) return;
            var focus = camera.gamePlayController.pivot;
            _session.SendMapPing(focus.x, focus.y, focus.z);
        }

        internal void PruneMapPings()
        {
            if (!GameplaySyncReady) { _mapPings.Clear(); return; }
            long now = NowMs;
            _mapPings.RemoveAll(p => now - p.CreatedMs >= MapPingLifetimeMs);
        }

        private void RecordMapPing(MapPingMessage ping)
        {
            if (!GameplaySyncReady) return;
            PruneMapPings();
            if (_mapPings.Count >= 32) _mapPings.RemoveAt(0);
            _mapPings.Add(new MapPing { Message = ping, CreatedMs = NowMs });
            AppendSessionActivity(L10n.F("CS2MP.Activity.MapPing", ping.PlayerName));
        }
    }

    public sealed class MapPing
    {
        public MapPingMessage Message;
        public long CreatedMs;
    }
}
