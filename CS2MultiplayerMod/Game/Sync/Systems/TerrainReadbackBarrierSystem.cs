using Game;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Completes a remote terrain pass's asynchronous CPU height readback at the beginning of
    /// ToolUpdate. Running before interactive tools matters: a road preview generated from the old
    /// CPU surface is already wrong even if realization waits later in the same phase.
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
            if (_terrainSync != null) _terrainSync.CompletePendingHeightReadback();
        }
    }
}
