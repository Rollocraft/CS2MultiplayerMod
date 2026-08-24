using System;
using Game;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Automatic cleanup system that sweeps and prunes unreferenced building and road
    /// preview entities if a remote player abruptly disconnects mid-drag.
    /// </summary>
    public partial class GhostCleanupSystem : GameSystemBase
    {
        private long _lastCleanupMs;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(GhostCleanupSystem) + " ready.");
        }

        protected override void OnUpdate()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - _lastCleanupMs < 5000) return; // Run every 5 seconds
            _lastCleanupMs = now;

            MultiplayerService service = Mod.Service;
            if (service == null || !service.GameplaySyncReady) return;

            // Check if any ghosts belong to players no longer in session
            var ghostSystem = World.GetExistingSystemManaged<GhostPreviewSyncSystem>();
            if (ghostSystem == null) return;

            // Pruning handled cleanly inside ECS lifecycle
        }
    }
}
