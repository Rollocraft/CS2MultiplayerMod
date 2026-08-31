using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // The other half of a portable reference: walking the owner path a peer described down
    // through this machine's own graph, and finding the object, node, edge or area it names.
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// Name a reference that would not resolve. An unresolved reference costs everyone a full
        /// world stream, so the log line has to say which one it was: whether its prefab is even
        /// known here separates "this machine is missing the thing" from "this machine is missing
        /// the mod/DLC content".
        /// </summary>
        private string Describe(PortableEntityRef source)
        {
            Entity prefab;
            string prefabState = _prefabIndex.TryResolve(source.PrefabName, out prefab)
                ? ""
                : ", prefab unknown here";
            string owner = string.IsNullOrEmpty(source.OwnerPrefabName)
                ? ""
                : " under '" + source.OwnerPrefabName + "' at (" +
                  source.OwnerX.ToString("F1") + ", " + source.OwnerZ.ToString("F1") + ")";
            return "(" + source.Kind + " '" + source.PrefabName + "' at (" +
                   source.PosX.ToString("F1") + ", " + source.PosZ.ToString("F1") + ")" +
                   owner + prefabState + ")";
        }

        private bool TryResolvePortableRef(PortableEntityRef source, out Entity result)
        {
            result = Entity.Null;
            if (source.Kind == PortableEntityKind.None) return true;
            Entity prefab;
            if (!_prefabIndex.TryResolve(source.PrefabName, out prefab)) return false;

            // Owned lifecycle references first use the same owner buffers that the simulation
            // maintains. Geometry remains a compatibility fallback for references captured before
            // a structural path was available or for a benign buffer-layout difference.
            if (source.OwnerPath != null && source.OwnerPath.Length != 0 &&
                TryResolveOwnerPath(source, prefab, out result))
                return true;

            float3 position = new float3(source.PosX, source.PosY, source.PosZ);
            switch (source.Kind)
            {
                case PortableEntityKind.Object:
                    result = FindPortableObject(prefab, position, source);
                    return result != Entity.Null;
                case PortableEntityKind.NetNode:
                    result = FindPortableNode(prefab, position, source);
                    return result != Entity.Null;
                case PortableEntityKind.NetEdge:
                    result = FindPortableEdge(prefab, source);
                    return result != Entity.Null;
                case PortableEntityKind.Area:
                    result = FindPortableArea(prefab, position, source);
                    return result != Entity.Null;
                default:
                    return false;
            }
        }

        private bool TryResolveOwnerPath(PortableEntityRef source, Entity targetPrefab,
            out Entity result)
        {
            result = Entity.Null;
            if (string.IsNullOrEmpty(source.OwnerPrefabName) ||
                source.OwnerPath == null || source.OwnerPath.Length == 0 ||
                source.OwnerPath.Length > ObjectToolOperationCommand.MaxOwnerPathDepth)
                return false;

            Entity ownerPrefab;
            if (!_prefabIndex.TryResolve(source.OwnerPrefabName, out ownerPrefab))
                return false;
            Entity cursor = FindPortableObject(ownerPrefab,
                new float3(source.OwnerX, source.OwnerY, source.OwnerZ),
                default(PortableEntityRef));
            if (cursor == Entity.Null) return false;

            for (int i = 0; i < source.OwnerPath.Length; i++)
            {
                Entity child;
                if (!TryResolveOwnerPathStep(cursor, source.OwnerPath[i], out child))
                    return false;
                cursor = child;
            }

            PortableEntityKind resolvedKind;
            if (!EntityManager.Exists(cursor) ||
                EntityManager.HasComponent<Temp>(cursor) ||
                EntityManager.HasComponent<Deleted>(cursor) ||
                !EntityManager.HasComponent<PrefabRef>(cursor) ||
                EntityManager.GetComponentData<PrefabRef>(cursor).m_Prefab != targetPrefab ||
                !TryGetPortableEntityKind(cursor, out resolvedKind) ||
                resolvedKind != source.Kind)
                return false;
            if ((source.Kind == PortableEntityKind.NetNode ||
                 source.Kind == PortableEntityKind.NetEdge) &&
                !MatchesNetContract(targetPrefab, source))
                return false;

            result = cursor;
            return true;
        }

        private bool TryResolveOwnerPathStep(Entity owner, PortableOwnerPathStep step,
            out Entity result)
        {
            result = Entity.Null;
            Entity prefab;
            if (!_prefabIndex.TryResolve(step.PrefabName, out prefab)) return false;

            switch (step.BufferKind)
            {
                case PortableOwnerPathKind.InstalledUpgrade:
                    if (!EntityManager.HasBuffer<global::Game.Buildings.InstalledUpgrade>(owner))
                        return false;
                    DynamicBuffer<global::Game.Buildings.InstalledUpgrade> upgrades =
                        EntityManager.GetBuffer<global::Game.Buildings.InstalledUpgrade>(
                            owner, isReadOnly: true);
                    int upgradeOrdinal = 0;
                    for (int i = 0; i < upgrades.Length; i++)
                    {
                        Entity candidate = upgrades[i].m_Upgrade;
                        if (!MatchesOwnerPathCandidate(owner, candidate, prefab,
                                step.EntityKind)) continue;
                        if (upgradeOrdinal++ != step.PrefabOrdinal) continue;
                        result = candidate;
                        return true;
                    }
                    return false;

                case PortableOwnerPathKind.SubObject:
                    if (!EntityManager.HasBuffer<global::Game.Objects.SubObject>(owner))
                        return false;
                    DynamicBuffer<global::Game.Objects.SubObject> objects =
                        EntityManager.GetBuffer<global::Game.Objects.SubObject>(
                            owner, isReadOnly: true);
                    int objectOrdinal = 0;
                    for (int i = 0; i < objects.Length; i++)
                    {
                        Entity candidate = objects[i].m_SubObject;
                        if (!MatchesOwnerPathCandidate(owner, candidate, prefab,
                                step.EntityKind)) continue;
                        if (objectOrdinal++ != step.PrefabOrdinal) continue;
                        result = candidate;
                        return true;
                    }
                    return false;

                case PortableOwnerPathKind.SubNet:
                    if (!EntityManager.HasBuffer<global::Game.Net.SubNet>(owner))
                        return false;
                    DynamicBuffer<global::Game.Net.SubNet> nets =
                        EntityManager.GetBuffer<global::Game.Net.SubNet>(
                            owner, isReadOnly: true);
                    int netOrdinal = 0;
                    for (int i = 0; i < nets.Length; i++)
                    {
                        Entity candidate = nets[i].m_SubNet;
                        if (!MatchesOwnerPathCandidate(owner, candidate, prefab,
                                step.EntityKind)) continue;
                        if (netOrdinal++ != step.PrefabOrdinal) continue;
                        result = candidate;
                        return true;
                    }
                    return false;

                case PortableOwnerPathKind.SubArea:
                    if (!EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner))
                        return false;
                    DynamicBuffer<global::Game.Areas.SubArea> areas =
                        EntityManager.GetBuffer<global::Game.Areas.SubArea>(
                            owner, isReadOnly: true);
                    int areaOrdinal = 0;
                    for (int i = 0; i < areas.Length; i++)
                    {
                        Entity candidate = areas[i].m_Area;
                        if (!MatchesOwnerPathCandidate(owner, candidate, prefab,
                                step.EntityKind)) continue;
                        if (areaOrdinal++ != step.PrefabOrdinal) continue;
                        result = candidate;
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private bool MatchesOwnerPathCandidate(Entity owner, Entity candidate, Entity prefab,
            PortableEntityKind kind)
        {
            if (candidate == Entity.Null || !EntityManager.Exists(candidate) ||
                EntityManager.HasComponent<Temp>(candidate) ||
                EntityManager.HasComponent<Deleted>(candidate) ||
                !EntityManager.HasComponent<PrefabRef>(candidate) ||
                EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab ||
                !EntityManager.HasComponent<Owner>(candidate) ||
                EntityManager.GetComponentData<Owner>(candidate).m_Owner != owner)
                return false;
            PortableEntityKind candidateKind;
            return TryGetPortableEntityKind(candidate, out candidateKind) &&
                   candidateKind == kind;
        }

        private Entity FindPortableObject(Entity prefab, float3 position, PortableEntityRef identity)
        {
            List<Entity> candidates = Candidates(_objectCandidates, _portableObjects, prefab);
            Entity best = Entity.Null;
            float bestDistance = 4f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;
                float distance = math.distancesq(EntityManager
                    .GetComponentData<global::Game.Objects.Transform>(candidate).m_Position, position);
                if (distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private Entity FindPortableNode(Entity prefab, float3 position, PortableEntityRef identity)
        {
            if (!MatchesNetContract(prefab, identity)) return Entity.Null;
            List<Entity> candidates = Candidates(_nodeCandidates, _liveNodes, prefab);
            Entity best = Entity.Null;
            float bestDistance = 4f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;
                float3 candidatePosition = EntityManager.GetComponentData<Node>(candidate).m_Position;
                if (math.abs(candidatePosition.y - position.y) > 3f) continue;
                float distance = math.distancesq(candidatePosition.xz, position.xz);
                if (distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private Entity FindPortableEdge(Entity prefab, PortableEntityRef identity)
        {
            var sourceCurve = new Bezier4x3
            {
                a = new float3(identity.Ax, identity.Ay, identity.Az),
                b = new float3(identity.Bx, identity.By, identity.Bz),
                c = new float3(identity.Cx, identity.Cy, identity.Cz),
                d = new float3(identity.Dx, identity.Dy, identity.Dz),
            };
            float3 anchor = new float3(identity.PosX, identity.PosY, identity.PosZ);
            if (!MatchesNetContract(prefab, identity)) return Entity.Null;
            List<Entity> candidates = Candidates(_edgeCandidates, _liveEdges, prefab);
            Entity best = Entity.Null;
            float bestDistance = 2f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;
                Bezier4x3 curve = EntityManager.GetComponentData<global::Game.Net.Curve>(candidate).m_Bezier;
                if (!SplitMatch.IsSubCurve3D(curve, sourceCurve) &&
                    !SplitMatch.IsSubCurve3D(sourceCurve, curve)) continue;
                float t;
                float distance = MathUtils.Distance(curve, anchor, out t);
                if (distance >= bestDistance) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }

        private Entity FindPortableArea(Entity prefab, float3 anchor, PortableEntityRef identity)
        {
            List<Entity> candidates = Candidates(_areaCandidates, _portableAreas, prefab);
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.GetBuffer<global::Game.Areas.Node>(candidate, isReadOnly: true);
                if (nodes.Length > 0 && math.distancesq(nodes[0].m_Position, anchor) <= 4f)
                    return candidate;
            }
            return Entity.Null;
        }

        private bool MatchesNetContract(Entity prefab, PortableEntityRef identity)
        {
            if (!EntityManager.HasComponent<NetData>(prefab)) return false;
            NetData data = EntityManager.GetComponentData<NetData>(prefab);
            return (uint)data.m_RequiredLayers == identity.RequiredLayers &&
                   (uint)data.m_ConnectLayers == identity.ConnectLayers;
        }

        private bool MatchesPortableOwner(Entity candidate, PortableEntityRef identity)
        {
            bool wantsOwner = !string.IsNullOrEmpty(identity.OwnerPrefabName);
            Entity topOwner;
            if (!TryFindTopOwner(candidate, out topOwner)) return false;
            if (!wantsOwner) return topOwner == Entity.Null;
            if (topOwner == Entity.Null || !EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner)) return false;
            string ownerName = _prefabSystem.GetPrefabName(
                EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab);
            if (ownerName != identity.OwnerPrefabName) return false;
            float3 ownerPosition = EntityManager
                .GetComponentData<global::Game.Objects.Transform>(topOwner).m_Position;
            return math.distancesq(ownerPosition,
                new float3(identity.OwnerX, identity.OwnerY, identity.OwnerZ)) <= 4f;
        }
    }
}
