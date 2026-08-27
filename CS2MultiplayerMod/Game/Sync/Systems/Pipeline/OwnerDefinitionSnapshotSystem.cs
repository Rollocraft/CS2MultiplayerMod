using Game;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Records which owner each generated sub-element was told to attach to, in the one window where
    /// that is still knowable. A generated sub-element whose owner is described by prefab and
    /// transform is born with its <see cref="Owner"/> unset; the game's resolution pass fills it in
    /// by an exact transform match and removes the description before it knows whether the match
    /// succeeded, so a miss leaves an orphan that nothing can trace back. Running immediately before
    /// that pass keeps the description available to the commit validator, which re-links from it.
    /// </summary>
    public partial class OwnerDefinitionSnapshotSystem : GameSystemBase
    {
        private NetSyncSystem _netSync;
        private EntityQuery _describedTemps;

        protected override void OnCreate()
        {
            base.OnCreate();
            Mod.log.Info(nameof(OwnerDefinitionSnapshotSystem) + " ready.");
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _describedTemps = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<OwnerDefinition>(),
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
            RequireForUpdate(_describedTemps);
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("OwnerDefSnapshot"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady) return;
                if (_netSync == null || !_netSync.HasArmedToolCommit) return;

                NativeArray<Entity> entities = _describedTemps.ToEntityArray(Allocator.Temp);
                try
                {
                    _netSync.BeginOwnerDescriptionSnapshot(entities.Length);
                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        OwnerDefinition described =
                            EntityManager.GetComponentData<OwnerDefinition>(entity);
                        if (described.m_Prefab == Entity.Null) continue;
                        _netSync.RecordOwnerDescription(entity, described.m_Prefab, described.m_Position);
                    }
                }
                finally
                {
                    entities.Dispose();
                }
            }
        }
    }
}
