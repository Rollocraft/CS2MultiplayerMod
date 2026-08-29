using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Commit orchestration for NetSyncSystem. A remote net operation includes the objects and areas
    // its native generation updates as side effects; the complete local preview graph is temporarily
    // Disabled so an unrelated tool can remain selected without either transaction consuming the
    // other one's entities.
    // Resolving the owner a generated child belongs to. The host describes each owner it generated
    // by prefab and position; the client matches that description against the batch it is holding,
    // and failing that against what is already live, because the two peers never share entity ids.
    // The describe/count helpers exist to make a failure legible in the log.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Replace the per-frame record of which owner each described sub-element named. Written by
        /// <see cref="OwnerDefinitionSnapshotSystem"/> in the phase before the game consumes those
        /// descriptions; an empty pass leaves the previous record intact, because the batch being
        /// validated has already had its descriptions taken.
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
        /// Recover the owner of a sub-element the native resolution pass left unset, in descending
        /// order of certainty: the entity's own surviving description, the description recorded for
        /// exactly this entity before the pass consumed it, and finally the batch's own description
        /// when it names a single owner. Ambiguity is never guessed away.
        /// </summary>
        private bool TryRelinkGeneratedOwner(Entity entity, HashSet<Entity> members, out Entity owner)
        {
            owner = Entity.Null;
            ArmedOwnerDefinition described;
            if (members == null || !TryResolveOwnerDescription(entity, out described)) return false;
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
            // Two different owners in one batch cannot be told apart with no record of this entity.
            // Re-parenting to the wrong building is worse than rejecting the batch.
            if (_pendingOwnerDefinitions.Count == 1)
            {
                described = _pendingOwnerDefinitions[0];
                return true;
            }
            described = default(ArmedOwnerDefinition);
            return false;
        }

        /// <summary>
        /// A candidate owner must be something the apply passes can already resolve. An entity whose
        /// own owner is still unset is another orphan: parenting one to the other would build a
        /// chain that no pass can follow, and an entity may never own itself.
        /// </summary>
        private bool IsResolvedOwnerCandidate(Entity candidate, Entity child)
        {
            if (candidate == child) return false;
            if (!EntityManager.HasComponent<Owner>(candidate)) return true;
            return EntityManager.GetComponentData<Owner>(candidate).m_Owner != Entity.Null;
        }

        /// <summary>
        /// Match an owner description against the armed transaction. The native pass compares the
        /// live transform bit-exactly, which a ground-conforming or attachment pass between
        /// generation and resolution can defeat; compare on the horizontal plane instead, where a
        /// placement does not move, and only accept a single candidate.
        /// </summary>
        private bool TryFindDescribedOwner(Entity child, Entity prefab,
            Unity.Mathematics.float3 position, HashSet<Entity> members, out Entity owner)
        {
            owner = Entity.Null;
            if (prefab == Entity.Null) return false;

            // Every sub-element of one placement names the same owner, so a single-entry memo turns
            // a per-orphan scan of the whole transaction into one scan for the batch.
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
            // A connector re-cut beside a building that already stands names a live owner. Owner
            // resolution only matches a Temp to a Temp, so it can never bind that pair and the
            // transaction alone cannot supply it either. Ask what is standing at the described
            // point instead; attaching to a live owner is the ordinary form the apply passes read.
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

        /// <summary>
        /// How many live objects of the described prefab stand where the description says. Zero
        /// means the description names something this machine does not have; more than one means
        /// the point is ambiguous and re-linking deliberately refused.
        /// </summary>
        private int LiveOwnerCandidates(Entity prefab, Unity.Mathematics.float3 position)
        {
            Entity ignored;
            if (TryFindLiveDescribedOwner(prefab, position, out ignored)) return 1;
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

        /// <summary>
        /// Name the entity a validation rule rejected. The reason string alone cannot distinguish an
        /// owner that never resolved from one deleted mid-transaction, which left several recorded
        /// sessions undiagnosable.
        /// </summary>
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

            ArmedOwnerDefinition described;
            if (!TryResolveOwnerDescription(entity, out described))
            {
                detail.Append(" wantedOwner=unknown armedOwners=")
                      .Append(_pendingOwnerDefinitions.Count);
            }
            else
            {
                detail.Append(" wantedOwner=")
                      .Append(PrefabIndex.SafeName(_prefabSystem, described.Prefab));
                // Distinguish the two ways the search can come up empty: no such owner is in the
                // transaction at all, or one is but sits outside the accepted distance. Only the
                // second is a tolerance question.
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
            // Owner resolution ignores Disabled entities, and the isolation this commit path applies
            // uses exactly that tag. Say so when an isolated candidate exists: it separates our own
            // interference from a description the batch genuinely cannot satisfy.
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

        /// <summary>
        /// Owners this commit path is currently hiding that could have satisfied the rejected
        /// entity's description. Owner resolution skips Disabled entities, so a non-zero count means
        /// our own isolation, not the world, is what the description could not reach.
        /// </summary>
        private int IsolatedOwnerCandidates(Entity entity)
        {
            ArmedOwnerDefinition described;
            if (!TryResolveOwnerDescription(entity, out described)) return 0;
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
