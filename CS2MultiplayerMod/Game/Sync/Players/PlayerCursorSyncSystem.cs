using System.Diagnostics;
using Game;
using Game.Rendering;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Publishes the local camera focus a few times a second and collects the other players' positions
    /// and hover shapes. Per-player and lossy: only the newest position matters.
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
            _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
            CreateHoverCapture();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("PartnerCursor"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) { ClearHoverCapture(); return; }

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady) { ClearHoverCapture(); return; }

                long now = _clock.ElapsedMilliseconds;
                if (now - _lastSentMs < SendIntervalMs) return;
                _lastSentMs = now;

                if (_camera == null)
                {
                    _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
                    if (_camera == null) return;
                }

                // Focus is where they look, eye where the camera is (for height); without a gameplay camera the
                // marker collapses to a ground point.
                float3 eye = _camera.position;
                float3 focus = eye;
                float yaw = 0f;
                CameraController controller = _camera.gamePlayController;
                if (controller != null)
                {
                    focus = controller.pivot;
                    yaw = controller.rotation.y;
                }

                session.SendPlayerState(focus.x, focus.y, focus.z, eye.x, eye.y, eye.z, yaw, CaptureHover());
                _sent++;

                if (now - _lastLogMs >= 30000)
                {
                    _lastLogMs = now;
                    int remote = 0;
                    foreach (var _ in service.RemotePlayers) remote++;
                    SyncLog.Detail(LogTopic.Players, "Cursors: sent " + _sent +
                        " position(s)/30s; tracking " + remote + " remote player(s).");
                    _sent = 0;
                }
            }
        }
    }
}
