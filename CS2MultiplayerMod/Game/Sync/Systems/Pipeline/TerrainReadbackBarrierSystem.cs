using Game;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Completes the remote terrain height readback before interactive tools build previews from
    /// the stale CPU surface.
    /// </summary>
    public partial class TerrainReadbackBarrierSystem : GameSystemBase
    {
        private TerrainSyncSystem _terrainSync;

        protected override void OnCreate()
        {
            base.OnCreate();
            _terrainSync = World.GetOrCreateSystemManaged<TerrainSyncSystem>();
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("TerrainReadback"))
            {
                if (_terrainSync != null) _terrainSync.CompletePendingHeightReadback();
            }
        }
    }
}
