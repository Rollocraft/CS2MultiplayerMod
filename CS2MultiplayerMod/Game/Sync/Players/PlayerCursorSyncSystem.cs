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

        public static int FollowPlayerId = -1;
        public static long FollowStartedMs = 0;
        private float3 _lastFollowTargetPivot;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private CameraUpdateSystem _camera;
        private long _lastSentMs;
        private long _lastLogMs;
        private int _sent;

        private float3 _lastSentFocus;
        private float3 _lastSentEye;
        private float _lastSentYaw;

        public static void TeleportCameraTo(float3 position)
        {
            var camera = Unity.Entities.World.DefaultGameObjectInjectionWorld?.GetExistingSystemManaged<CameraUpdateSystem>();
            if (camera?.gamePlayController != null)
            {
                camera.gamePlayController.pivot = position;
            }
        }

        public static void StartFollowing(int playerId)
        {
            FollowPlayerId = playerId;
            var service = Mod.Service;
            if (service != null)
            {
                FollowStartedMs = service.NowMs;
                RemotePlayer target = service.FindRemotePlayer(playerId);
                if (target != null)
                {
                    TeleportCameraTo(new float3(target.X, target.Y, target.Z));
                }
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            FollowPlayerId = -1;
            FollowStartedMs = 0;
            Mod.log.Info(nameof(PlayerCursorSyncSystem) + " ready.");
            _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
        }

        protected override void OnDestroy()
        {
            FollowPlayerId = -1;
            FollowStartedMs = 0;
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null) return;

            MultiplayerSession session = service.Session;
            if (!service.GameplaySyncReady)
            {
                FollowPlayerId = -1;
                return;
            }

            long now = _clock.ElapsedMilliseconds;

            if (_camera == null)
            {
                _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
                if (_camera == null) return;
            }

            CameraController controller = _camera.gamePlayController;

            // Follow mode tracking
            if (FollowPlayerId != -1)
            {
                RemotePlayer target = service.FindRemotePlayer(FollowPlayerId);
                if (target != null && (now - target.LastUpdateMs <= 5000))
                {
                    if (controller != null)
                    {
                        float3 targetPos = new float3(target.X, target.Y, target.Z);
                        bool gracePeriod = (now - FollowStartedMs) < 1500;
                        bool keyboardMovementPressed = UnityEngine.Input.GetKey(UnityEngine.KeyCode.W) ||
                                                       UnityEngine.Input.GetKey(UnityEngine.KeyCode.A) ||
                                                       UnityEngine.Input.GetKey(UnityEngine.KeyCode.S) ||
                                                       UnityEngine.Input.GetKey(UnityEngine.KeyCode.D) ||
                                                       UnityEngine.Input.GetKey(UnityEngine.KeyCode.UpArrow) ||
                                                       UnityEngine.Input.GetKey(UnityEngine.KeyCode.DownArrow) ||
                                                       UnityEngine.Input.GetKey(UnityEngine.KeyCode.LeftArrow) ||
                                                       UnityEngine.Input.GetKey(UnityEngine.KeyCode.RightArrow);

                        // Only break follow if a keyboard movement key is explicitly pressed
                        if (!gracePeriod && keyboardMovementPressed)
                        {
                            FollowPlayerId = -1;
                            service.AppendSystemChat("Stopped following " + (target.Name ?? "player") + ".");
                        }
                        else
                        {
                            float dt = UnityEngine.Time.deltaTime;
                            float t = math.clamp(dt * 8f, 0.05f, 0.5f);
                            controller.pivot = math.lerp(controller.pivot, targetPos, t);
                            _lastFollowTargetPivot = controller.pivot;
                        }
                    }
                }
                else
                {
                    FollowPlayerId = -1;
                }
            }

            // The ground focus (pivot) is where the player is looking; the eye is where
            // their camera actually is, up in the air - both travel so markers can show
            // height. Fall back to the raw camera position when no gameplay camera is
            // active (menus, cinematic mode), which collapses the marker to a ground point.
            float3 eye = _camera.position;
            float3 focus = eye;
            float yaw = 0f;
            if (controller != null)
            {
                focus = controller.pivot;
                yaw = controller.rotation.y;
            }

            bool moved = math.distancesq(focus, _lastSentFocus) > 0.1f ||
                         math.distancesq(eye, _lastSentEye) > 0.1f ||
                         math.abs(yaw - _lastSentYaw) > 0.03f;

            // Adaptive frame-pacing: throttle cursor send frequency when local frame rate drops
            float frameDelta = UnityEngine.Time.unscaledDeltaTime;
            long activeInterval = frameDelta > 0.033f ? 100 : 75; // 10 Hz on low FPS, 13 Hz on 30+ FPS
            long minInterval = moved ? activeInterval : 1000;

            if (now - _lastSentMs < minInterval) return;
            _lastSentMs = now;
            _lastSentFocus = focus;
            _lastSentEye = eye;
            _lastSentYaw = yaw;

            session.SendPlayerState(focus.x, focus.y, focus.z, eye.x, eye.y, eye.z, yaw);
            _sent++;

            if (now - _lastLogMs >= 30000)
            {
                _lastLogMs = now;
                int remote = service.RemotePlayerCount;
                Mod.Verbose("[MP] Cursors: sent " + _sent + " position(s)/30s; tracking " + remote + " remote player(s).");
                _sent = 0;
            }
        }
    }
}
