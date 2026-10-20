using System.Collections.Generic;
using System.Text;
using Game.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace CS2MultiplayerMod.Game
{
    public sealed partial class MultiplayerService
    {
        private const long CameraStaleAfterMs = 3000;
        private int _cameraJumpTarget;
        public int FollowingPlayerId { get; private set; }
        private long _cameraListAtMs = -1000;
        private string _cameraPlayersJson = "[]";

        public string CameraPlayersJson
        {
            get
            {
                if (!GameplaySyncReady) return "[]";
                if (NowMs - _cameraListAtMs < 500) return _cameraPlayersJson;
                _cameraListAtMs = NowMs;
                var players = new List<RemotePlayer>();
                foreach (var player in _remotePlayers.Values)
                    if (TryCameraPlayer(player.PlayerId, out _)) players.Add(player);
                players.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
                var json = new StringBuilder("[");
                foreach (var player in players)
                {
                    if (json.Length > 1) json.Append(',');
                    json.Append("{\"id\":").Append(player.PlayerId).Append(",\"name\":");
                    AppendJsonString(json, player.Name ?? "Player");
                    json.Append(",\"color\":\"")
                        .Append(Sync.Players.RemotePlayerMarkerSystem.PlayerColorCss(player.PlayerId))
                        .Append("\"}");
                }
                return _cameraPlayersJson = json.Append(']').ToString();
            }
        }

        private bool TryCameraPlayer(int id, out RemotePlayer player)
        {
            player = null;
            return GameplaySyncReady && id != _session.LocalPlayerId &&
                _remotePlayers.TryGetValue(id, out player) && NowMs - player.LastUpdateMs < CameraStaleAfterMs;
        }

        public void GoToPlayer(int id)
        {
            if (!TryCameraPlayer(id, out _)) return;
            StopFollowingPlayer();
            _cameraJumpTarget = id;
        }

        public void FollowPlayer(int id)
        {
            if (!TryCameraPlayer(id, out _)) return;
            FollowingPlayerId = id;
            _cameraJumpTarget = id;
        }

        public void StopFollowingPlayer()
        {
            FollowingPlayerId = 0;
            _cameraJumpTarget = 0;
        }

        internal void UpdatePlayerCamera(CameraUpdateSystem cameras, float deltaTime)
        {
            int target = _cameraJumpTarget != 0 ? _cameraJumpTarget : FollowingPlayerId;
            if (target == 0) return;
            var controller = cameras?.gamePlayController;
            if (!TryCameraPlayer(target, out var player) || controller == null ||
                !object.ReferenceEquals(cameras.activeCameraController, controller))
            {
                StopFollowingPlayer();
                return;
            }
            bool jump = _cameraJumpTarget != 0;
            _cameraJumpTarget = 0;
            var input = global::Game.Input.InputManager.instance;
            if (!jump && input != null && !input.hasInputFieldFocus)
            {
                if (UnityEngine.InputSystem.Keyboard.current?.escapeKey.wasPressedThisFrame == true)
                {
                    StopFollowingPlayer();
                    return;
                }
                foreach (var action in controller.inputActions)
                    if (action.enabled && action.GetMagnitude() > 0.05f)
                    {
                        StopFollowingPlayer();
                        return;
                    }
            }
            float blend = jump ? 1f : 1f - math.exp(-math.min(deltaTime, 0.1f) / 0.12f);
            controller.pivot = Vector3.Lerp(controller.pivot, new Vector3(player.X, player.Y, player.Z), blend);
            Vector3 rotation = controller.rotation;
            rotation.x = Mathf.LerpAngle(rotation.x, player.Pitch, blend);
            rotation.y = Mathf.LerpAngle(rotation.y, player.Yaw, blend);
            controller.rotation = rotation;
            controller.zoom = Mathf.Lerp(controller.zoom,
                math.clamp(player.Zoom, controller.zoomRange.min, controller.zoomRange.max), blend);
        }
    }
}
