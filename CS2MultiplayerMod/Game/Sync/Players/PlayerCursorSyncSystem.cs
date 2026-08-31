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
    ///
    /// This system also owns every camera move the mod makes, because it holds the only
    /// reference to <see cref="CameraUpdateSystem"/>. The chat commands do not touch the
    /// camera themselves; they record an intent on <see cref="MultiplayerService"/>
    /// (<see cref="MultiplayerService.RequestCameraJump"/> and
    /// <see cref="MultiplayerService.FollowPlayerId"/>) and this system consumes it on the
    /// next frame, on the thread that may safely do so.
    /// </summary>
    public partial class PlayerCursorSyncSystem : GameSystemBase
    {
        /// <summary>Cadence while the camera is being moved.</summary>
        private const long MovingIntervalMs = 100; // ~10 Hz

        /// <summary>
        /// Cadence while it is not. A parked camera still has to report in - a peer that
        /// hears nothing for <c>StaleAfterMs</c> stops drawing the marker - but ten times a
        /// second to say "unchanged" is most of what this system used to send. A player
        /// reading a panel or sitting in a menu is the common case in a co-op session.
        /// </summary>
        private const long IdleIntervalMs = 1000;

        /// <summary>
        /// Movement below this is not worth a packet. Squared metres for the positions;
        /// the yaw threshold is radians and is roughly two degrees.
        /// </summary>
        private const float MovedDistanceSq = 0.1f;
        private const float MovedYaw = 0.03f;

        /// <summary>
        /// How far the pivot may drift from where follow mode last put it before we take
        /// that as the player moving their own camera and stop following. Testing the
        /// camera rather than polling keys keeps this out of the game's input handling -
        /// so it cannot fire while someone is typing, and needs no key list to maintain.
        /// </summary>
        private const float FollowBreakDistanceSq = 400f; // 20 m

        /// <summary>Fraction of the gap to the followed player closed per frame, time-scaled.</summary>
        private const float FollowLerpPerSecond = 8f;

        /// <summary>A followed player whose position went stale is no longer followable.</summary>
        private const long FollowStaleAfterMs = 5000;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private CameraUpdateSystem _camera;
        private long _lastSentMs;
        private long _lastLogMs;
        private int _sent;

        private float3 _lastSentFocus;
        private float3 _lastSentEye;
        private float _lastSentYaw;
        private bool _everSent;

        /// <summary>Where follow mode last placed the pivot, to notice the player taking over.</summary>
        private float3 _followPivot;
        private bool _followPivotValid;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(PlayerCursorSyncSystem) + " ready.");
            _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("PartnerCursor"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null) return;

                MultiplayerSession session = service.Session;
                if (!service.GameplaySyncReady)
                {
                    service.StopFollowing();
                    _followPivotValid = false;
                    _everSent = false;
                    return;
                }

                if (_camera == null)
                {
                    _camera = World.GetExistingSystemManaged<CameraUpdateSystem>();
                    if (_camera == null) return;
                }

                long now = _clock.ElapsedMilliseconds;
                CameraController controller = _camera.gamePlayController;

                ApplyCameraIntent(service, controller, now);

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

                // Send at the moving cadence while anything actually changed, and fall back to
                // a slow keepalive when it did not. The first send after becoming ready always
                // goes out so a partner does not wait a second for the marker to appear.
                bool moved = !_everSent ||
                             math.distancesq(focus, _lastSentFocus) > MovedDistanceSq ||
                             math.distancesq(eye, _lastSentEye) > MovedDistanceSq ||
                             math.abs(yaw - _lastSentYaw) > MovedYaw;

                if (now - _lastSentMs < (moved ? MovingIntervalMs : IdleIntervalMs)) return;

                _lastSentMs = now;
                _lastSentFocus = focus;
                _lastSentEye = eye;
                _lastSentYaw = yaw;
                _everSent = true;

                // Chat commands need a point on the map and hold no camera reference; this is
                // the only place that has one.
                service.LocalCameraFocus = focus;

                session.SendPlayerState(focus.x, focus.y, focus.z, eye.x, eye.y, eye.z, yaw);
                _sent++;

                if (now - _lastLogMs >= 30000)
                {
                    _lastLogMs = now;
                    Mod.Verbose("[MP] Cursors: sent " + _sent + " position(s)/30s; tracking " +
                                service.RemotePlayerCount + " remote player(s).");
                    _sent = 0;
                }
            }
        }

        /// <summary>
        /// Consume a pending camera jump, then advance follow mode by one frame. Both are
        /// no-ops without a gameplay camera, which is also the state a menu leaves behind.
        /// </summary>
        private void ApplyCameraIntent(MultiplayerService service, CameraController controller, long now)
        {
            if (controller == null)
            {
                _followPivotValid = false;
                return;
            }

            float3 jump;
            if (service.TakeCameraJump(out jump))
            {
                controller.pivot = jump;
                _followPivot = jump;
                _followPivotValid = true;
            }

            int followId = service.FollowPlayerId;
            if (followId < 0)
            {
                _followPivotValid = false;
                return;
            }

            RemotePlayer target = service.FindRemotePlayer(followId);
            if (target == null || now - target.LastUpdateMs > FollowStaleAfterMs)
            {
                service.StopFollowing();
                service.AppendSystemChat("Stopped following: that player is no longer reporting a position.");
                _followPivotValid = false;
                return;
            }

            // The player grabbing their own camera ends follow mode. Compare against where we
            // put the pivot last frame, not against the target: the follow lerp never lands
            // exactly on the target, so the target is not a fixed point to measure from.
            if (_followPivotValid &&
                math.distancesq((float3)controller.pivot, _followPivot) > FollowBreakDistanceSq)
            {
                service.StopFollowing();
                service.AppendSystemChat("Stopped following - camera moved.");
                _followPivotValid = false;
                return;
            }

            float3 wanted = new float3(target.X, target.Y, target.Z);
            float t = math.clamp(UnityEngine.Time.unscaledDeltaTime * FollowLerpPerSecond, 0.05f, 1f);
            float3 next = math.lerp(controller.pivot, wanted, t);
            controller.pivot = next;
            _followPivot = next;
            _followPivotValid = true;
        }
    }
}
