using System.Diagnostics;
using Game;
using Game.Rendering;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Session;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Publishes the local player's map focus (the camera pivot - the point on the
    /// ground the player is looking at) a few times a second, and lets the service
    /// collect the other players' positions for drawing their cursors. Unlike the
    /// city-state channels this is per-player and lossy: only the newest position
    /// matters. Rendering the remote cursors is handled separately.
    /// </summary>
    public partial class PlayerCursorSyncSystem : GameSystemBase
    {
        private const long SendIntervalMs = 100; // ~10 Hz

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private CameraUpdateSystem _camera;
        private long _lastSentMs;
        private long _lastLogMs;
        private int _sent;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(PlayerCursorSyncSystem) + " ready.");
            _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady) return;

            long now = _clock.ElapsedMilliseconds;
            if (now - _lastSentMs < SendIntervalMs) return;
            _lastSentMs = now;

            if (_camera == null)
            {
                _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
                if (_camera == null) return;
            }

            // The ground focus (pivot) is where the player is looking; the eye is where
            // their camera actually is, up in the air - both travel so markers can show
            // height. Fall back to the raw camera position when no gameplay camera is
            // active (menus, cinematic mode), which collapses the marker to a ground point.
            float3 eye = _camera.position;
            float3 focus = eye;
            float yaw = 0f;
            CameraController controller = _camera.gamePlayController;
            if (controller != null)
            {
                focus = controller.pivot;
                yaw = controller.rotation.y;
            }

            session.SendPlayerState(focus.x, focus.y, focus.z, eye.x, eye.y, eye.z, yaw);
            _sent++;

            if (now - _lastLogMs >= 30000)
            {
                _lastLogMs = now;
                int remote = 0;
                foreach (var _ in service.RemotePlayers) remote++;
                Mod.Verbose("[MP] Cursors: sent " + _sent + " position(s)/30s; tracking " + remote + " remote player(s).");
                _sent = 0;
            }
        }
    }
}
