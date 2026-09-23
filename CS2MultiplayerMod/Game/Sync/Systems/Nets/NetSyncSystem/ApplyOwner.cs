using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Resolving the owner of a generated child. Peers share no entity ids, so the host's prefab and
    // position description is matched against the held batch, then against what is live.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Written before the game consumes the descriptions; an empty pass keeps the previous record.
        /// </summary>
        public void BeginOwnerDescriptionSnapshot(int expected)
        {
            if (expected <= 0) return;
            _describedOwners.Clear();
        }

        public void RecordOwnerDescription(Entity entity, Entity ownerPrefab,
            Unity.Mathematics.float3 ownerPosition)
        {
            _describedOwners[entity] = new ArmedOwnerDefinition
            {
                Prefab = ownerPrefab,
                Position = ownerPosition,
            };
        }

        /// <summary>
        /// Recovers an unset owner from, in order: the surviving description, the recorded one, or the
        /// batch's single owner. Ambiguity is never guessed.
        /// </summary>
        private bool TryRelinkGeneratedOwner(Entity entity, HashSet<Entity> members, out Entity owner)
        {
            owner = Entity.Null;
            if (members == null ||
                !TryResolveOwnerDescription(entity, out ArmedOwnerDefinition described)) return false;
            return TryFindDescribedOwner(entity, described.Prefab, described.Position, members,
                out owner);
        }

        private bool TryResolveOwnerDescription(Entity entity, out ArmedOwnerDefinition described)
        {
            if (EntityManager.HasComponent<OwnerDefinition>(entity))
            {
                OwnerDefinition live = EntityManager.GetComponentData<OwnerDefinition>(entity);
                described = new ArmedOwnerDefinition
                {
                    Prefab = live.m_Prefab,
                    Position = live.m_Position,
                };
                return described.Prefab != Entity.Null;
            }
            if (_describedOwners.TryGetValue(entity, out described)) return true;
            // Two owners cannot be told apart; the wrong building is worse than a rejection.
            if (_pendingOwnerDefinitions.Count == 1)
            {
                described = _pendingOwnerDefinitions[0];
                return true;
            }
            described = default(ArmedOwnerDefinition);
            return false;
        }

        /// <summary>Resolvable owners only: no orphan chains and no self-ownership.</summary>
        private bool IsResolvedOwnerCandidate(Entity candidate, Entity child)
        {
            if (candidate == child) return false;
            if (!EntityManager.HasComponent<Owner>(candidate)) return true;
            return EntityManager.GetComponentData<Owner>(candidate).m_Owner != Entity.Null;
        }

        /// <summary>
        /// Matches in the horizontal plane (the native pass is bit-exact and ground conforming can defeat
        /// it), accepting only a single candidate.
        /// </summary>
        private bool TryFindDescribedOwner(Entity child, Entity prefab,
            Unity.Mathematics.float3 position, HashSet<Entity> members, out Entity owner)
        {
            owner = Entity.Null;
            if (prefab == Entity.Null) return false;

            // One placement names one owner: memo the last lookup.
            if (prefab == _lastDescribedOwnerPrefab && position.Equals(_lastDescribedOwnerPosition) &&
                _lastDescribedOwner != Entity.Null && _lastDescribedOwner != child &&
                members.Contains(_lastDescribedOwner))
            {
                owner = _lastDescribedOwner;
                return true;
            }

            const float maxHorizontalDistanceSq = 1f;
            float bestDistanceSq = float.MaxValue;
            int candidates = 0;
            foreach (Entity candidate in members)
            {
                if (!EntityManager.Exists(candidate) ||
                    !IsResolvedOwnerCandidate(candidate, child) ||
                    EntityManager.HasComponent<Deleted>(candidate) ||
                    !EntityManager.HasComponent<global::Game.Objects.Object>(candidate) ||
                    !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                    !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate)) continue;
                if (EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate)
                        .m_Prefab != prefab) continue;

                Unity.Mathematics.float3 candidatePosition =
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                        .m_Position;
                float distanceSq = Unity.Mathematics.math.distancesq(
                    candidatePosition.xz, position.xz);
                if (distanceSq > maxHorizontalDistanceSq) continue;
                candidates++;
                if (distanceSq >= bestDistanceSq) continue;
                bestDistanceSq = distanceSq;
                owner = candidate;
            }
            // A connector re-cut beside a standing building names a live owner, which Temp-to-Temp
            // resolution can never bind.
            if (candidates == 0) return TryFindLiveDescribedOwner(prefab, position, out owner);
            if (candidates != 1)
            {
                owner = Entity.Null;
                return false;
            }
            _lastDescribedOwnerPrefab = prefab;
            _lastDescribedOwnerPosition = position;
            _lastDescribedOwner = owner;
            return true;
        }

        /// <summary>Zero: not here. More than one: ambiguous, so re-linking is refused.</summary>
        private int LiveOwnerCandidates(Entity prefab, Unity.Mathematics.float3 position)
        {
            if (TryFindLiveDescribedOwner(prefab, position, out Entity ignored)) return 1;
            return _lastLiveOwnerCandidates;
        }

        private int _lastLiveOwnerCandidates;

        private bool TryFindLiveDescribedOwner(Entity prefab, Unity.Mathematics.float3 position,
            out Entity owner)
        {
            owner = Entity.Null;
            _lastLiveOwnerCandidates = 0;
            if (_ownerSearch == null) return false;

            const float searchRadius = 2f;
            const float maxHorizontalDistanceSq = 1f;
            var candidates = new NativeList<Entity>(Allocator.Temp);
            try
            {
                _ownerSearch.CollectNear(position, searchRadius, candidates);
                float bestDistanceSq = float.MaxValue;
                int matches = 0;
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    if (!EntityManager.Exists(candidate) ||
                        EntityManager.HasComponent<Deleted>(candidate) ||
                        EntityManager.HasComponent<Temp>(candidate) ||
                        !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                        !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate) ||
                        EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate)
                            .m_Prefab != prefab) continue;

                    float distanceSq = Unity.Mathematics.math.distancesq(
                        EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                            .m_Position.xz, position.xz);
                    if (distanceSq > maxHorizontalDistanceSq) continue;
                    matches++;
                    if (distanceSq >= bestDistanceSq) continue;
                    bestDistanceSq = distanceSq;
                    owner = candidate;
                }
                _lastLiveOwnerCandidates = matches;
                if (matches == 1) return true;
                owner = Entity.Null;
                return false;
            }
            finally
            {
                candidates.Dispose();
            }
        }

        /// <summary>Names the rejected entity so an unresolved owner can be told from a deleted one.</summary>
        private string DescribeOwnerFailure(Entity entity, Entity owner, HashSet<Entity> members)
        {
            var detail = new System.Text.StringBuilder("(");
            detail.Append(DescribeTransactionEntity(entity));
            detail.Append(EntityManager.HasComponent<OwnerDefinition>(entity)
                ? " ownerDefinition=present"
                : " ownerDefinition=consumed");
            if (owner == Entity.Null) detail.Append(" owner=unset");
            else if (!EntityManager.Exists(owner))
                detail.Append(" owner=#").Append(owner.Index).Append("=gone");
            else detail.Append(" owner=#").Append(owner.Index).Append("=deleted");

            if (!TryResolveOwnerDescription(entity, out ArmedOwnerDefinition described))
            {
                detail.Append(" wantedOwner=unknown armedOwners=")
                      .Append(_pendingOwnerDefinitions.Count);
            }
            else
            {
                detail.Append(" wantedOwner=")
                      .Append(PrefabIndex.SafeName(_prefabSystem, described.Prefab));
                // Not in the transaction at all, or present but beyond the accepted distance.
                int samePrefab = 0;
                float nearestSq = float.MaxValue;
                if (members != null)
                {
                    foreach (Entity candidate in members)
                    {
                        if (!EntityManager.Exists(candidate) ||
                            !EntityManager.HasComponent<global::Game.Objects.Transform>(candidate) ||
                            !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate) ||
                            EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate)
                                .m_Prefab != described.Prefab) continue;
                        samePrefab++;
                        float distanceSq = Unity.Mathematics.math.distancesq(
                            EntityManager.GetComponentData<global::Game.Objects.Transform>(candidate)
                                .m_Position.xz, described.Position.xz);
                        if (distanceSq < nearestSq) nearestSq = distanceSq;
                    }
                }
                detail.Append(" memberCandidates=").Append(samePrefab);
                if (samePrefab > 0)
                    detail.Append(" nearestM=")
                          .Append(Unity.Mathematics.math.sqrt(nearestSq).ToString("0.##"));
                else
                    detail.Append(" liveCandidates=")
                          .Append(LiveOwnerCandidates(described.Prefab, described.Position));
            }
            // Owner resolution skips Disabled, which our isolation uses: flag our own interference.
            int isolated = IsolatedOwnerCandidates(entity);
            if (isolated > 0) detail.Append(" isolatedCandidates=").Append(isolated);
            detail.Append(')');
            return detail.ToString();
        }

        private string DescribeTransactionEntity(Entity entity)
        {
            var detail = new System.Text.StringBuilder();
            if (EntityManager.HasComponent<Edge>(entity)) detail.Append("edge");
            else if (EntityManager.HasComponent<Node>(entity)) detail.Append("node");
            else if (EntityManager.HasComponent<Lane>(entity)) detail.Append("lane");
            else if (EntityManager.HasComponent<Aggregate>(entity)) detail.Append("aggr");
            else if (EntityManager.HasComponent<global::Game.Objects.Object>(entity)) detail.Append("obj");
            else if (EntityManager.HasComponent<global::Game.Areas.Area>(entity)) detail.Append("area");
            else detail.Append("other");
            detail.Append('#').Append(entity.Index);

            if (EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity))
            {
                Entity prefab =
                    EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity).m_Prefab;
                detail.Append(" prefab=").Append(PrefabIndex.SafeName(_prefabSystem, prefab));
            }
            if (EntityManager.HasComponent<Temp>(entity))
                detail.Append(" flags=").Append(EntityManager.GetComponentData<Temp>(entity)
                    .m_Flags.ToString().Replace(", ", "|"));
            return detail.ToString();
        }

        /// <summary>Candidates our isolation is hiding; non-zero means our interference, not the world.</summary>
        private int IsolatedOwnerCandidates(Entity entity)
        {
            if (!TryResolveOwnerDescription(entity, out ArmedOwnerDefinition described)) return 0;
            Entity prefab = described.Prefab;
            if (prefab == Entity.Null) return 0;

            int isolated = 0;
            for (int i = 0; i < _isolatedLocalTemps.Count; i++)
            {
                Entity candidate = _isolatedLocalTemps[i];
                if (!EntityManager.Exists(candidate) ||
                    !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(candidate)) continue;
                if (EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(candidate)
                        .m_Prefab == prefab) isolated++;
            }
            return isolated;
        }
    }
}
