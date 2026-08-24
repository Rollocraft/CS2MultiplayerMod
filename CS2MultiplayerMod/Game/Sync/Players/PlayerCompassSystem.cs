using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Game;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Players
{
    /// <summary>
    /// Computes compass bearings (0-360 degrees) and relative distances (km) from the local camera
    /// to all active remote player focus points across the map.
    /// </summary>
    public partial class PlayerCompassSystem : GameSystemBase
    {
        public struct PlayerBearing
        {
            public int PlayerId;
            public string PlayerName;
            public float DistanceKm;
            public float BearingDegrees; // 0=North, 90=East, 180=South, 270=West
        }

        private readonly ConcurrentDictionary<int, PlayerBearing> _bearings =
            new ConcurrentDictionary<int, PlayerBearing>();

        public IReadOnlyCollection<PlayerBearing> Bearings => _bearings.Values;

        private CameraUpdateSystem _cameraSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            _cameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            Mod.log.Info(nameof(PlayerCompassSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            MultiplayerService service = Mod.Service;
            if (service == null || _cameraSystem?.gamePlayController == null || !service.GameplaySyncReady)
            {
                _bearings.Clear();
                return;
            }

            float3 localPos = _cameraSystem.gamePlayController.pivot;

            foreach (var remote in service.RemotePlayers)
            {
                var targetPos = new float3(remote.X, remote.Y, remote.Z);
                float dx = targetPos.x - localPos.x;
                float dz = targetPos.z - localPos.z;

                float distanceMeters = math.sqrt(dx * dx + dz * dz);
                float distanceKm = distanceMeters / 1000f;

                // Calculate compass angle from North (Z-positive) clockwise
                float angleRad = math.atan2(dx, dz);
                float degrees = math.degrees(angleRad);
                if (degrees < 0) degrees += 360f;

                _bearings[remote.PlayerId] = new PlayerBearing
                {
                    PlayerId = remote.PlayerId,
                    PlayerName = remote.Name ?? ("Player #" + remote.PlayerId),
                    DistanceKm = (float)Math.Round(distanceKm, 2),
                    BearingDegrees = (float)Math.Round(degrees, 1)
                };
            }
        }
    }
}
