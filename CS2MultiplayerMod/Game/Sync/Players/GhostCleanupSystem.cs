using System;
using Game;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Players
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

            var activeIds = new System.Collections.Generic.HashSet<int>();
            if (service.LocalPlayerId != 0) activeIds.Add(service.LocalPlayerId);
            if (service.RemotePlayers != null)
            {
                foreach (var p in service.RemotePlayers)
                {
                    if (service.NowMs - p.LastUpdateMs <= 6000)
                    {
                        activeIds.Add(p.PlayerId);
                    }
                }
            }

            ghostSystem.PruneInactiveGhosts(activeIds);
        }
    }
}
