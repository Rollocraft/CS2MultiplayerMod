using Game;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Systems.Net;
namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Records each generated sub-element's intended owner just before the game's owner resolution
    /// removes that description, so the commit validator can re-link an orphan the match missed.
    /// </summary>
    public partial class OwnerDefinitionSnapshotSystem : GameSystemBase
    {
        private NetSyncSystem _netSync;
        private BuildSyncSystem _buildSync;
        private EntityQuery _describedEntities;

        protected override void OnCreate()
        {
            base.OnCreate();
            _netSync = World.GetOrCreateSystemManaged<NetSyncSystem>();
            _buildSync = World.GetOrCreateSystemManaged<BuildSyncSystem>();
            _describedEntities = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<OwnerDefinition, Owner>(),
            });
            RequireForUpdate(_describedEntities);
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("OwnerDefSnapshot"))
            {
                MultiplayerService service = Mod.Service;
                if (service == null || !service.GameplaySyncReady) return;

                NativeArray<Entity> entities = _describedEntities.ToEntityArray(Allocator.Temp);
                try
                {
                    int tempCount = 0;
                    if (_netSync != null && _netSync.HasArmedToolCommit)
                    {
                        for (int i = 0; i < entities.Length; i++)
                            if (EntityManager.HasComponent<Temp>(entities[i])) tempCount++;
                        _netSync.BeginOwnerDescriptionSnapshot(tempCount);
                    }

                    int permanentRelinks = 0;
                    if (_buildSync != null) _buildSync.BeginExpectedBuildingOwnerRelinks();
                    try
                    {
                        for (int i = 0; i < entities.Length; i++)
                        {
                            Entity entity = entities[i];
                            OwnerDefinition described =
                                EntityManager.GetComponentData<OwnerDefinition>(entity);
                            if (described.m_Prefab == Entity.Null) continue;
                            if (EntityManager.HasComponent<Temp>(entity))
                            {
                                if (_netSync != null && _netSync.HasArmedToolCommit)
                                    _netSync.RecordOwnerDescription(entity, described.m_Prefab,
                                        described.m_Position);
                            }
                            else if (_buildSync != null &&
                                     _buildSync.TryRelinkExpectedBuildingOwner(entity,
                                         described.m_Prefab, described.m_Position,
                                         described.m_Rotation))
                            {
                                permanentRelinks++;
                            }
                        }
                    }
                    finally
                    {
                        if (_buildSync != null) _buildSync.EndExpectedBuildingOwnerRelinks();
                    }
                    if (permanentRelinks > 0)
                        SyncLog.Trace(LogTopic.Buildings,
                            "remote building owner links restored=" + permanentRelinks);
                }
                finally
                {
                    entities.Dispose();
                }
            }
        }
    }
}
