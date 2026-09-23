using Unity.Collections;
using Unity.Entities;
using Game;
using Game.City;
using Game.Prefabs;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Channels;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Remote popup events are presentation only: channel 5 already installs the host's point total,
    /// so the points <see cref="DevTreeSystem"/> awards for our marked events are removed afterwards.
    /// </summary>
    public partial class RemoteMilestoneRewardCorrectionSystem : GameSystemBase
    {
        private EntityQuery _popupEvents;
        private EntityQuery _points;

        protected override void OnCreate()
        {
            base.OnCreate();
            _popupEvents = GetEntityQuery(
                ComponentType.ReadOnly<MilestoneReachedEvent>(),
                ComponentType.ReadOnly<RemoteMilestonePopup>());
            _points = GetEntityQuery(ComponentType.ReadWrite<DevTreePoints>());
            RequireForUpdate(_popupEvents);
            RequireForUpdate(_points);
        }

        protected override void OnUpdate()
        {
            int duplicatePoints = 0;
            NativeArray<MilestoneReachedEvent> events =
                _popupEvents.ToComponentDataArray<MilestoneReachedEvent>(Allocator.Temp);
            try
            {
                for (int i = 0; i < events.Length; i++)
                {
                    MilestoneReachedEvent reached = events[i];
                    if (reached.m_Milestone != Entity.Null &&
                        EntityManager.HasComponent<MilestoneData>(reached.m_Milestone))
                    {
                        duplicatePoints += EntityManager
                            .GetComponentData<MilestoneData>(reached.m_Milestone).m_DevTreePoints;
                    }
                    else
                    {
                        duplicatePoints += DefaultPoints(reached.m_Index);
                    }
                }
            }
            finally
            {
                events.Dispose();
            }

            if (duplicatePoints == 0) return;
            Entity city = _points.GetSingletonEntity();
            DevTreePoints points = EntityManager.GetComponentData<DevTreePoints>(city);
            points.m_Points -= duplicatePoints;
            EntityManager.SetComponentData(city, points);
            SyncLog.Detail(LogTopic.City,
                "MilestoneState: removed " + duplicatePoints +
                " duplicate development point(s) from client popup event.");
        }

        private static int DefaultPoints(int level)
        {
            if (level <= 0) return 0;
            if (level >= 19) return 10;
            return (level + 1) / 2 + 1;
        }
    }
}
