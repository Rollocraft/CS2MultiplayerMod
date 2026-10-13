using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Unity.Entities;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        /// <summary>Host condition and abandonment; condition is the level-up progress.</summary>
        private bool ApplyConditionAndState(Entity building, GrowableLifecycleCommand command)
        {
            if (EntityManager.HasComponent<BuildingCondition>(building))
            {
                BuildingCondition condition = EntityManager.GetComponentData<BuildingCondition>(building);
                if (condition.m_Condition != command.Condition)
                {
                    condition.m_Condition = command.Condition;
                    EntityManager.SetComponentData(building, condition);
                }
            }

            bool needsUpdate = SetMarker<Abandoned>(building,
                (command.StateFlags & GrowableLifecycleCommand.StateAbandoned) != 0);
            needsUpdate |= SetMarker<Condemned>(building,
                (command.StateFlags & GrowableLifecycleCommand.StateCondemned) != 0);
            needsUpdate |= SetMarker<Destroyed>(building,
                (command.StateFlags & GrowableLifecycleCommand.StateDestroyed) != 0);

            bool hostConstructing =
                (command.Flags & GrowableLifecycleCommand.FlagUnderConstruction) != 0;
            bool localConstructing = EntityManager.HasComponent<UnderConstruction>(building);
            if (hostConstructing)
            {
                UnderConstruction construction = localConstructing
                    ? EntityManager.GetComponentData<UnderConstruction>(building)
                    : default(UnderConstruction);
                if (!localConstructing || construction.m_Progress != command.ConstructionProgress ||
                    construction.m_Speed != command.ConstructionSpeed)
                {
                    construction.m_Progress = command.ConstructionProgress;
                    construction.m_Speed = command.ConstructionSpeed;
                    if (localConstructing) EntityManager.SetComponentData(building, construction);
                    else
                    {
                        EntityManager.AddComponentData(building, construction);
                        needsUpdate = true;
                    }
                }
            }
            else if (localConstructing)
            {
                // BuildingConstructionSystem performs the completion side effects.
                UnderConstruction construction =
                    EntityManager.GetComponentData<UnderConstruction>(building);
                if (construction.m_Progress != byte.MaxValue)
                {
                    construction.m_Progress = byte.MaxValue;
                    EntityManager.SetComponentData(building, construction);
                    needsUpdate = true;
                }
            }
            return needsUpdate;
        }

        /// <summary>
        /// Completion also states the prefab; a missed level is repaired through BuildingConstructionSystem
        /// rather than by replacing PrefabRef.
        /// </summary>
        private bool RepairCompletedPrefab(Entity building, Entity hostPrefab,
            GrowableLifecycleCommand command)
        {
            if ((command.Flags & GrowableLifecycleCommand.FlagUnderConstruction) != 0 ||
                !IsGrowablePrefab(hostPrefab) ||
                !EntityManager.HasComponent<PrefabRef>(building)) return false;

            Entity currentPrefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            bool localConstructing = EntityManager.HasComponent<UnderConstruction>(building);
            if (currentPrefab == hostPrefab && !localConstructing) return false;

            UnderConstruction completion = localConstructing
                ? EntityManager.GetComponentData<UnderConstruction>(building)
                : default(UnderConstruction);
            if (completion.m_NewPrefab == hostPrefab && completion.m_Progress >= 100) return false;

            completion.m_NewPrefab = hostPrefab;
            completion.m_Progress = byte.MaxValue;
            if (completion.m_Speed == 0) completion.m_Speed = 1;
            if (localConstructing) EntityManager.SetComponentData(building, completion);
            else EntityManager.AddComponentData(building, completion);
            _repairedPrefabs++;
            return true;
        }

        private bool SetMarker<T>(Entity entity, bool wanted) where T : unmanaged, IComponentData
        {
            bool has = EntityManager.HasComponent<T>(entity);
            if (has == wanted) return false;
            if (wanted) EntityManager.AddComponent<T>(entity);
            else EntityManager.RemoveComponent<T>(entity);
            return true;
        }
    }
}
