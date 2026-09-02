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

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Encoding one captured definition: the placement command it becomes, its endpoints, and the
    // owner an endpoint is attached to - each described by prefab and position rather than by an
    // entity id the other peer would not recognise.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Convert an original-backed member of a mixed net-tool graph to the existing portable
        /// delete/replace representation. The complete set is only published inside a
        /// <see cref="NetToolOperationCommand"/>; returning a reason rejects that whole envelope.
        /// A null item with no reason is a generated invisible sub-net which the receiver recreates.
        /// </summary>
        private LocalNetToolOperationItem CaptureMixedMutationCommand(
            CreationDefinition definition, NetCourse course, out string unrepresentable)
        {
            unrepresentable = null;
            // Test this before the owner rule below. A building's connector is both owned and
            // hidden; deciding it on ownership first made the operation unrepresentable and voided
            // it, even though the hidden-sub-net rule further down already declares that exact
            // course the receiver's job to rebuild.
            if (IsGeneratedHiddenSubNet(definition)) return null;
            if (definition.m_Owner != Entity.Null || definition.m_Attached != Entity.Null)
            {
                unrepresentable = "reference an owner or attachment";
                return null;
            }

            Entity original = definition.m_Original;
            if (original == Entity.Null || !EntityManager.Exists(original) ||
                EntityManager.HasComponent<Deleted>(original) ||
                EntityManager.HasComponent<Temp>(original) ||
                EntityManager.HasComponent<Owner>(original) ||
                !EntityManager.HasComponent<Edge>(original) ||
                !EntityManager.HasComponent<Curve>(original) ||
                !EntityManager.HasComponent<PrefabRef>(original))
            {
                unrepresentable = "reference an unavailable or owned original edge";
                return null;
            }

            Entity originalPrefab = EntityManager.GetComponentData<PrefabRef>(original).m_Prefab;
            string originalName = PrefabNameOf(originalPrefab);
            if (string.IsNullOrEmpty(originalName))
            {
                unrepresentable = "reference an original edge whose prefab cannot be named";
                return null;
            }
            if (originalName.StartsWith("Invisible")) return null;

            CreationFlags flags = definition.m_Flags;
            const CreationFlags unsupportedLifecycle = CreationFlags.Permanent |
                CreationFlags.Select | CreationFlags.Attach | CreationFlags.Upgrade |
                CreationFlags.Relocate | CreationFlags.Parent | CreationFlags.Dragging |
                CreationFlags.Recreate | CreationFlags.Duplicate | CreationFlags.Repair |
                CreationFlags.Stamping;
            if ((flags & unsupportedLifecycle) != 0)
            {
                unrepresentable = "use an unsupported original-backed creation mode";
                return null;
            }
            const CreationFlags representedMutationFlags = CreationFlags.Delete |
                CreationFlags.Invert | CreationFlags.Align | CreationFlags.SubElevation;
            if ((flags & ~representedMutationFlags) != 0)
            {
                unrepresentable = "carry original-backed creation flags the mutation codec cannot preserve";
                return null;
            }

            Bezier4x3 oldCurve = EntityManager.GetComponentData<Curve>(original).m_Bezier;
            if ((flags & CreationFlags.Delete) != 0)
            {
                var deletion = new NetDeleteCommand
                {
                    PrefabName = originalName,
                    Ax = oldCurve.a.x, Ay = oldCurve.a.y, Az = oldCurve.a.z,
                    Bx = oldCurve.b.x, By = oldCurve.b.y, Bz = oldCurve.b.z,
                    Cx = oldCurve.c.x, Cy = oldCurve.c.y, Cz = oldCurve.c.z,
                    Dx = oldCurve.d.x, Dy = oldCurve.d.y, Dz = oldCurve.d.z,
                };
                return new LocalNetToolOperationItem
                {
                    CommandId = NetDeleteCommand.Id,
                    Original = original,
                    Delete = deletion,
                };
            }

            string newName = PrefabNameOf(definition.m_Prefab);
            if (string.IsNullOrEmpty(newName))
            {
                unrepresentable = "modify an original edge without a named target prefab";
                return null;
            }
            if (definition.m_SubPrefab != Entity.Null)
            {
                unrepresentable = "replace an edge with a lane sub-prefab";
                return null;
            }
            if (newName.StartsWith("Invisible")) return null;

            Bezier4x3 newCurve = course.m_Curve;
            var replacement = new NetReplaceCommand
            {
                PrefabName = newName,
                Ax = newCurve.a.x, Ay = newCurve.a.y, Az = newCurve.a.z,
                Bx = newCurve.b.x, By = newCurve.b.y, Bz = newCurve.b.z,
                Cx = newCurve.c.x, Cy = newCurve.c.y, Cz = newCurve.c.z,
                Dx = newCurve.d.x, Dy = newCurve.d.y, Dz = newCurve.d.z,
                OldAx = oldCurve.a.x, OldAy = oldCurve.a.y, OldAz = oldCurve.a.z,
                OldBx = oldCurve.b.x, OldBy = oldCurve.b.y, OldBz = oldCurve.b.z,
                OldCx = oldCurve.c.x, OldCy = oldCurve.c.y, OldCz = oldCurve.c.z,
                OldDx = oldCurve.d.x, OldDy = oldCurve.d.y, OldDz = oldCurve.d.z,
            };
            return new LocalNetToolOperationItem
            {
                CommandId = NetReplaceCommand.Id,
                Original = original,
                Replace = replacement,
            };
        }

        /// <summary>
        /// Build the wire command for one course definition. A null result with a null
        /// <paramref name="unrepresentable"/> is a deliberate skip (the game's own hidden sub-nets);
        /// a reason means this course belongs to the operation but cannot be replayed, which voids
        /// the native envelope.
        /// </summary>
        private NetPlacementCommand CaptureDefinitionCommand(CreationDefinition definition,
            NetCourse course, out string unrepresentable)
        {
            unrepresentable = null;
            string prefabName = PrefabNameOf(definition.m_Prefab);
            // Hidden sub-nets are regenerated by the receiver from the visible net; an unnamed
            // prefab is not a skip but a course that cannot be addressed on the wire, and dropping
            // it silently would remove a link from the middle of the operation.
            if (string.IsNullOrEmpty(prefabName))
            {
                unrepresentable = "use a prefab that cannot be named";
                return null;
            }
            if (prefabName.StartsWith("Invisible")) return null;

            Bezier4x3 curve = course.m_Curve;
            var command = new NetPlacementCommand
            {
                CourseIndex = 0,
                CourseCount = 1,
                HasNativeCourse = true,
                PrefabName = prefabName,
                SubPrefabName = PrefabNameOf(definition.m_SubPrefab),
                Ax = curve.a.x,
                Ay = curve.a.y,
                Az = curve.a.z,
                Bx = curve.b.x,
                By = curve.b.y,
                Bz = curve.b.z,
                Cx = curve.c.x,
                Cy = curve.c.y,
                Cz = curve.c.z,
                Dx = curve.d.x,
                Dy = curve.d.y,
                Dz = curve.d.z,
                Length = course.m_Length,
                RandomSeed = definition.m_RandomSeed,
                CreationFlags = (uint)definition.m_Flags,
                CourseElevationLeft = course.m_Elevation.x,
                CourseElevationRight = course.m_Elevation.y,
                FixedIndex = course.m_FixedIndex,
                Start = CaptureEndpoint(course.m_StartPosition),
                End = CaptureEndpoint(course.m_EndPosition),
            };
            const string unnamedOwner = "target an owned sub-net whose owner cannot be named";
            if ((command.Start.Kind == NetEndpointTargetKind.OwnedNode ||
                 command.Start.Kind == NetEndpointTargetKind.OwnedEdge) &&
                string.IsNullOrEmpty(command.Start.OwnerPrefabName))
            {
                unrepresentable = unnamedOwner;
                return null;
            }
            if ((command.End.Kind == NetEndpointTargetKind.OwnedNode ||
                 command.End.Kind == NetEndpointTargetKind.OwnedEdge) &&
                string.IsNullOrEmpty(command.End.OwnerPrefabName))
            {
                unrepresentable = unnamedOwner;
                return null;
            }
            return command;
        }

        private NetEndpointIntent CaptureEndpoint(CoursePos position)
        {
            NetEndpointIntent result = new NetEndpointIntent
            {
                Kind = position.m_Entity == Entity.Null
                    ? NetEndpointTargetKind.Free
                    : NetEndpointTargetKind.Infer,
                PosX = position.m_Position.x,
                PosY = position.m_Position.y,
                PosZ = position.m_Position.z,
                RotX = position.m_Rotation.value.x,
                RotY = position.m_Rotation.value.y,
                RotZ = position.m_Rotation.value.z,
                RotW = position.m_Rotation.value.w,
                ElevationLeft = position.m_Elevation.x,
                ElevationRight = position.m_Elevation.y,
                CourseDelta = position.m_CourseDelta,
                SplitPosition = position.m_SplitPosition,
                Flags = (uint)position.m_Flags,
                ParentMesh = position.m_ParentMesh,
                AnchorX = position.m_Position.x,
                AnchorY = position.m_Position.y,
                AnchorZ = position.m_Position.z,
            };

            Entity target = position.m_Entity;
            if (target == Entity.Null || !EntityManager.Exists(target)) return result;

            Entity targetPrefab = EntityManager.HasComponent<PrefabRef>(target)
                ? EntityManager.GetComponentData<PrefabRef>(target).m_Prefab
                : Entity.Null;
            result.TargetPrefabName = PrefabNameOf(targetPrefab);
            if (targetPrefab != Entity.Null && EntityManager.HasComponent<NetData>(targetPrefab))
            {
                NetData data = EntityManager.GetComponentData<NetData>(targetPrefab);
                result.TargetRequiredLayers = (uint)data.m_RequiredLayers;
                result.TargetConnectLayers = (uint)data.m_ConnectLayers;
            }

            if (EntityManager.HasComponent<Node>(target))
            {
                result.Kind = EntityManager.HasComponent<Owner>(target)
                    ? NetEndpointTargetKind.OwnedNode
                    : NetEndpointTargetKind.Node;
                float3 anchor = EntityManager.GetComponentData<Node>(target).m_Position;
                result.AnchorX = anchor.x; result.AnchorY = anchor.y; result.AnchorZ = anchor.z;
            }
            else if (EntityManager.HasComponent<Edge>(target) && EntityManager.HasComponent<Curve>(target))
            {
                result.Kind = EntityManager.HasComponent<Owner>(target)
                    ? NetEndpointTargetKind.OwnedEdge
                    : NetEndpointTargetKind.Edge;
                Bezier4x3 targetCurve = EntityManager.GetComponentData<Curve>(target).m_Bezier;
                float split = math.clamp(position.m_SplitPosition, 0f, 1f);
                float3 anchor = MathUtils.Position(targetCurve, split);
                result.AnchorX = anchor.x; result.AnchorY = anchor.y; result.AnchorZ = anchor.z;
                result.TargetAx = targetCurve.a.x; result.TargetAy = targetCurve.a.y; result.TargetAz = targetCurve.a.z;
                result.TargetBx = targetCurve.b.x; result.TargetBy = targetCurve.b.y; result.TargetBz = targetCurve.b.z;
                result.TargetCx = targetCurve.c.x; result.TargetCy = targetCurve.c.y; result.TargetCz = targetCurve.c.z;
                result.TargetDx = targetCurve.d.x; result.TargetDy = targetCurve.d.y; result.TargetDz = targetCurve.d.z;
            }
            if (result.Kind == NetEndpointTargetKind.OwnedNode ||
                result.Kind == NetEndpointTargetKind.OwnedEdge)
                CaptureEndpointOwner(target, ref result);
            return result;
        }

        private void CaptureEndpointOwner(Entity target, ref NetEndpointIntent result)
        {
            Entity cursor = target;
            Entity top = Entity.Null;
            for (int depth = 0; depth < 64 && EntityManager.HasComponent<Owner>(cursor); depth++)
            {
                Entity next = EntityManager.GetComponentData<Owner>(cursor).m_Owner;
                if (next == Entity.Null || next == cursor || !EntityManager.Exists(next)) return;
                top = next;
                cursor = next;
            }
            if (top == Entity.Null || !EntityManager.HasComponent<PrefabRef>(top) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(top)) return;
            result.OwnerPrefabName = PrefabNameOf(EntityManager.GetComponentData<PrefabRef>(top).m_Prefab);
            global::Game.Objects.Transform transform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(top);
            result.OwnerX = transform.m_Position.x;
            result.OwnerY = transform.m_Position.y;
            result.OwnerZ = transform.m_Position.z;
            result.OwnerRotX = transform.m_Rotation.value.x;
            result.OwnerRotY = transform.m_Rotation.value.y;
            result.OwnerRotZ = transform.m_Rotation.value.z;
            result.OwnerRotW = transform.m_Rotation.value.w;
        }

        private string PrefabNameOf(Entity prefab)
        {
            return prefab != Entity.Null && EntityManager.Exists(prefab)
                ? _prefabSystem.GetPrefabName(prefab)
                : null;
        }

        private void RecordPlacementOriginals(long now)
        {
            NativeArray<Entity> temps = _netTransactionTemps.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    if (!EntityManager.HasComponent<Temp>(temps[i])) continue;
                    Temp temp = EntityManager.GetComponentData<Temp>(temps[i]);
                    const TempFlags replacesOriginal = TempFlags.Delete | TempFlags.Replace |
                                                       TempFlags.Combine;
                    if ((temp.m_Flags & replacesOriginal) == 0) continue;
                    Entity original = temp.m_Original;
                    if (original == Entity.Null || !EntityManager.Exists(original) ||
                        !EntityManager.HasComponent<Edge>(original)) continue;
                    _committedNetSideEffects[original] = now + CommittedSideEffectWindowMs;
                }
            }
            finally
            {
                temps.Dispose();
            }
        }

        private void PruneCommittedNetSideEffects(long now)
        {
            if (_committedNetSideEffects.Count == 0) return;
            var expired = new List<Entity>();
            foreach (KeyValuePair<Entity, long> pair in _committedNetSideEffects)
                if (pair.Value < now) expired.Add(pair.Key);
            for (int i = 0; i < expired.Count; i++) _committedNetSideEffects.Remove(expired[i]);
        }
    }
}
