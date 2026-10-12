using Unity.Entities;
using Unity.Collections;
using Game;
using Game.City;
using Game.Common;
using Game.Prefabs;
using Game.Simulation;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Marks a milestone event created only for the native popup; a correction system removes the
    /// dev-tree points the game would award for it.
    /// </summary>
    internal struct RemoteMilestonePopup : IComponentData
    {
    }

    /// <summary>
    /// Milestone level and loan limit, plus the unlock cascade of every reached milestone. One-time
    /// rewards stay host-authoritative.
    /// </summary>
    public sealed class MilestoneStateChannel : IStateChannel, IPumpedStateChannel
    {
        public const byte Id = 4;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private EntityQuery _milestones;
        private DeferredPrefabUnlocker _unlocks;
        private EndFrameBarrier _barrier;
        private EntityArchetype _popupEventArchetype;
        private bool _hasAuthoritativeLevel;
        private int _authoritativeLevel;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(
                ComponentType.ReadWrite<MilestoneLevel>(),
                ComponentType.ReadWrite<Creditworthiness>());
            // MilestoneSystem's catalogue query; MilestoneData needs no IncludePrefab.
            _milestones = em.CreateEntityQuery(ComponentType.ReadOnly<MilestoneData>());
            _unlocks = new DeferredPrefabUnlocker(em);
            _barrier = em.World.GetOrCreateSystemManaged<EndFrameBarrier>();
            _popupEventArchetype = em.CreateArchetype(
                ComponentType.ReadWrite<Event>(),
                ComponentType.ReadWrite<MilestoneReachedEvent>(),
                ComponentType.ReadWrite<RemoteMilestonePopup>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;
            Entity city = _query.GetSingletonEntity();
            writer.WriteInt(em.GetComponentData<MilestoneLevel>(city).m_AchievedMilestone);
            writer.WriteInt(em.GetComponentData<Creditworthiness>(city).m_Amount);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int level = reader.ReadInt();
            int creditworthiness = reader.ReadInt();
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in milestone state.");
            if (level < 0)
                throw new ProtocolException("Negative achieved milestone: " + level + ".");
            if (creditworthiness < 0)
                throw new ProtocolException("Negative creditworthiness: " + creditworthiness + ".");
            if (_query.CalculateEntityCount() == 0) return;

            NativeArray<Entity> milestoneEntities = _milestones.ToEntityArray(Allocator.Temp);
            NativeArray<MilestoneData> milestoneData =
                _milestones.ToComponentDataArray<MilestoneData>(Allocator.Temp);
            try
            {
                int highestMilestone = 0;
                for (int i = 0; i < milestoneData.Length; i++)
                    if (milestoneData[i].m_Index > highestMilestone)
                        highestMilestone = milestoneData[i].m_Index;
                if (level > highestMilestone)
                    throw new ProtocolException("Achieved milestone " + level +
                        " exceeds highest known milestone " + highestMilestone + ".");

                ApplyValidated(em, level, creditworthiness, milestoneEntities, milestoneData);
            }
            finally
            {
                milestoneData.Dispose();
                milestoneEntities.Dispose();
            }
        }

        private void ApplyValidated(EntityManager em, int level, int creditworthiness,
            NativeArray<Entity> milestoneEntities, NativeArray<MilestoneData> milestoneData)
        {
            Entity e = _query.GetSingletonEntity();
            MilestoneLevel m = em.GetComponentData<MilestoneLevel>(e);
            bool notifyClient = _hasAuthoritativeLevel &&
                                level > _authoritativeLevel &&
                                m.m_AchievedMilestone < level;

            // Setting the level directly skips MilestoneReachedEvent, so later increases get one deferred
            // popup; the first snapshot is a baseline. Queue before mutating so a failure stays retryable.
            if (notifyClient)
                QueuePopup(level, milestoneEntities, milestoneData);

            m.m_AchievedMilestone = level;
            em.SetComponentData(e, m);
            em.SetComponentData(e, new Creditworthiness { m_Amount = creditworthiness });
            _authoritativeLevel = level;
            _hasAuthoritativeLevel = true;

            // Repair every reached milestone's unlocks, which never replays cash or loan rewards.
            int queued = ReconcileUnlocks(em, level, milestoneEntities, milestoneData);
            if (queued > 0)
            {
                SyncLog.Detail(LogTopic.City, "MilestoneState: queued " + queued +
                    " missing milestone unlock(s) through level " + level + ".");
            }
        }

        private void QueuePopup(int level, NativeArray<Entity> entities,
            NativeArray<MilestoneData> milestones)
        {
            Entity milestone = Entity.Null;
            for (int i = 0; i < milestones.Length; i++)
            {
                if (milestones[i].m_Index != level) continue;
                milestone = entities[i];
                break;
            }

            EntityCommandBuffer ecb = _barrier.CreateCommandBuffer();
            Entity popupEvent = ecb.CreateEntity(_popupEventArchetype);
            ecb.SetComponent(popupEvent, new MilestoneReachedEvent(milestone, level));
            SyncLog.Detail(LogTopic.City,
                "MilestoneState: queued native client popup for milestone " + level + ".");
        }

        private int ReconcileUnlocks(EntityManager em, int achievedMilestone,
            NativeArray<Entity> entities, NativeArray<MilestoneData> milestones)
        {
            int queued = 0;
            for (int i = 0; i < milestones.Length; i++)
            {
                Entity milestone = entities[i];
                bool locked = em.HasComponent<Locked>(milestone) &&
                              em.IsComponentEnabled<Locked>(milestone);
                if (locked && milestones[i].m_Index <= achievedMilestone &&
                    _unlocks.TryQueue(milestone))
                    queued++;
            }
            return queued;
        }

        public void Pump(EntityManager em)
        {
            if (_unlocks != null) _unlocks.PruneCompleted();
        }

        public void ResetPending()
        {
            if (_unlocks != null) _unlocks.Reset();
            _hasAuthoritativeLevel = false;
            _authoritativeLevel = 0;
        }
    }
}
